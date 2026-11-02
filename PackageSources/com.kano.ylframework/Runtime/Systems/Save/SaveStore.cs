using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SaveItem = YLFramework.Save.SaveManager.SaveItem;

namespace YLFramework.Save
{
    /// <summary>
    /// 唯一的存档引擎。显式根目录、同根独占 owner，同槽串行、跨槽独立；不依赖 Unity。
    /// </summary>
    /// <remarks>
    /// 对象和设置保持裸 JSON，索引版本为 2。模型必须是普通 DTO；异步反序列化/Converter 在工作线程执行，
    /// 不得调用 Unity API。同步读取保留调用线程；保存序列化在调用线程先捕获快照。
    /// 缓存返回原实例，但调用方负责实例本身的并发修改。并发前配置 SerializerSettings，Converter 必须可并发且不得重入本 owner。
    /// 禁止 TypeNameHandling。备份修复、删除和索引修改会保留失败原件；非全根多文件崩溃事务。
    /// Dispose 取消未提交操作并等待已进入的操作退出；commit 后不将成功改报取消。
    /// 本引擎没有内建加密、认证或密钥来源，也不读取用户密钥；文件备份及路径检查不构成安全加密保证。
    /// </remarks>
    public sealed class SaveStore : IDisposable
    {
        /// <summary>唯一受支持的索引格式版本；历史与未来版本均拒绝自动转换。</summary>
        public const int FormatVersion = 2;
        /// <summary>每槽主模型使用的固定文件基名，不包含 JSON 扩展名。</summary>
        public const string ModelFileName = "ArchiveModel";
        private readonly object _lifecycle = new object();
        private readonly object _metadata = new object();
        private readonly object _cacheLock = new object();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new ConcurrentDictionary<int, SemaphoreSlim>();
        /// <summary>保存对象原实例及其读取来源，使备份来源在缓存命中后仍可观察。</summary>
        private sealed class CachedObject
        {
            internal object Value;
            internal SaveReadStatus Status;
        }
        private readonly Dictionary<int, Dictionary<string, CachedObject>> _cache = new Dictionary<int, Dictionary<string, CachedObject>>();
        private readonly FileStream _rootLock;
        private SaveIndex _index;
        private bool _requiresIndexRecovery;
        private bool _isDisposed;
        private bool _hasFinishedDisposal;
        private int _active;
        /// <summary>经规范化的绝对根目录；索引与对象在 SaveData 下，全局设置在 Setting 下。</summary>
        public string StorageRoot
        {
            get;
        }
        /// <summary>对象与设置共用的序列化配置原引用，不用于索引；开始并发操作前应完成配置。</summary>
        public JsonSerializerSettings SerializerSettings
        {
            get;
        }
        /// <summary>后续对象与设置写入的 JSON 排版；索引固定缩进，不受此属性影响。</summary>
        public Formatting SerializationFormat
        {
            get; set;
        }
        private string SaveDirectory => Path.Combine(StorageRoot, "SaveData");
        private string SettingDirectory => Path.Combine(StorageRoot, "Setting");
        private string IndexPath => Path.Combine(SaveDirectory, "SaveSystemData.json");

        /// <summary>独占根目录锁文件并加载索引；孤立存档、索引损坏无有效备份或版本不符时拒绝打开。</summary>
        /// <param name="rootPath">显式存储根目录，不能为文件系统根或穿越已有链接。</param>
        /// <param name="settings">对象与设置使用的配置；null 创建独立默认配置。</param>
        /// <param name="formatting">对象与设置写入时的 JSON 排版。</param>
        /// <remarks>备份索引可用于读取，但显式恢复前禁止依赖索引的写操作；构造失败会释放已经取得的根锁。</remarks>
        public SaveStore(string rootPath, JsonSerializerSettings settings = null, Formatting formatting = Formatting.Indented)
        {
            StorageRoot = SaveFileIO.NormalizeRoot(rootPath);
            SerializerSettings = settings ?? new JsonSerializerSettings();
            SerializationFormat = formatting;
            Directory.CreateDirectory(StorageRoot);
            var lockPath = SafePath(Path.Combine(StorageRoot, ".yl-save.lock"));
            _rootLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var status = SaveFileIO.ReadJson<SaveIndex>(SafePath(IndexPath), null, out _index, out var error);
                if (status == SaveReadStatus.Failed)
                {
                    throw new InvalidDataException("Cannot read save index: " + error);
                }

                if (status == SaveReadStatus.Missing)
                {
                    if (Directory.Exists(SaveDirectory) && Directory.EnumerateFileSystemEntries(SaveDirectory).Any())
                    {
                        throw new InvalidDataException("Save files exist without an index; refusing to create an empty index.");
                    }

                    _index = new SaveIndex();
                }
                _index.Validate();
                _requiresIndexRecovery = status == SaveReadStatus.RecoveredFromBackup;
                foreach (var item in _index.SaveItemDict.Values)
                {
                    RefreshSaveTime(item);
                }
            }
            catch
            {
                _rootLock.Dispose();
                _shutdown.Dispose();
                throw;
            }
        }

        /// <summary>索引持久化的当前槽号；null 表示未选择，设置不存在的槽号会抛出异常。</summary>
        public int? CurrentSaveId
        {
            get => ReadIndex(index => index.CurrentSaveId);
            set => Execute(-2, token =>
            {
                lock (_metadata)
                {
                    if (value.HasValue && !_index.SaveItemDict.ContainsKey(value.Value))
                    {
                        throw new KeyNotFoundException("Unknown save slot: " + value);
                    }

                    var next = _index.Copy();
                    next.CurrentSaveId = value;
                    WriteIndex(next, token);
                }
                return true;
            });
        }

        /// <summary>取得新的槽列表容器，条目仍为引擎持有的原实例，未规定排序。</summary>
        public List<SaveItem> GetAllSaveItems() => ReadIndex(index => index.SaveItemDict.Values.ToList());
        /// <summary>仅查询索引是否包含槽号，不检查槽目录或对象文件是否存在。</summary>
        /// <param name="id">待查询的槽号。</param>
        public bool HasSaveItem(int id) => ReadIndex(index => index.SaveItemDict.ContainsKey(id));
        /// <summary>取得索引持有的槽元数据原实例，不存在时返回 null。</summary>
        /// <param name="id">待查询的槽号。</param>
        public SaveItem GetSaveItem(int id) => ReadIndex(index => index.SaveItemDict.TryGetValue(id, out var item) ? item : null);
        /// <summary>当前索引是否来自备份，必须先显式恢复才能继续修改索引或保存对象。</summary>
        public bool IndexNeedsRecovery => ReadIndex(_ => _requiresIndexRecovery);
        /// <summary>阻塞等待新建空槽及索引提交，不创建主模型或自动选中该槽。</summary>
        /// <param name="id">指定的非负槽号；null 从 0 起选择索引与磁盘上都未占用的槽号。</param>
        public SaveItem CreateSaveItem(int? id = null) => CreateSaveItemAsync(id).GetAwaiter().GetResult();
        /// <summary>串行提交新空槽元数据，拒绝覆盖已登记或磁盘上已有的同号路径。</summary>
        /// <param name="id">指定的非负槽号；null 自动选择未占用槽号。</param>
        /// <param name="cancellationToken">调用方取消信号，与引擎关闭信号共同限制提交前操作。</param>
        public Task<SaveItem> CreateSaveItemAsync(int? id = null, CancellationToken cancellationToken = default)
            => ExecuteAsync(-2, token => CreateSlot(id, null, null, token), cancellationToken);

        /// <summary>分配槽号并提交索引，可先落地模型目录；索引失败时将已移动的模型目录移回暂存位置。</summary>
        private SaveItem CreateSlot(int? requestedId, object model, string json, CancellationToken token)
        {
            lock (_metadata)
            {
                EnsureIndexWritable();
                var id = requestedId ?? 0;
                if (id < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(requestedId));
                }

                if (!requestedId.HasValue)
                {
                    while (_index.SaveItemDict.ContainsKey(id) || Directory.Exists(SlotPath(id)) || File.Exists(SlotPath(id)))
                    {
                        id = checked(id + 1);
                    }
                }

                if (_index.SaveItemDict.ContainsKey(id) || Directory.Exists(SlotPath(id)) || File.Exists(SlotPath(id)))
                {
                    throw new IOException("Save slot already exists; no overwrite is allowed: " + id);
                }

                var item = new SaveItem(id, DateTime.Now);
                var next = _index.Copy();
                next.SaveItemDict.Add(id, item);
                string staging = null;
                var moved = false;
                try
                {
                    if (json != null)
                    {
                        staging = SafePath(Path.Combine(SaveDirectory, ".creating-" + Guid.NewGuid().ToString("N")));
                        SaveFileIO.WriteTextAtomic(Path.Combine(staging, ModelFileName + ".json"), json, token, false);
                        token.ThrowIfCancellationRequested();
                        Directory.Move(staging, SlotPath(id));
                        moved = true;
                    }
                    WriteIndex(next, token);
                }
                catch
                {
                    // 失败模型文件保留在独立 staging，既不占用正式槽，也不销毁诊断样本。
                    if (moved)
                    {
                        Directory.Move(SlotPath(id), staging);
                    }

                    throw;
                }
                if (model != null)
                {
                    SetCache(id, ModelFileName, model);
                }

                return item;
            }
        }

        /// <summary>持久化槽显示名称，不存在时返回 false；写入失败向外抛出且不先改内存。</summary>
        /// <param name="id">待修改的槽号。</param>
        /// <param name="name">新的显示名称，允许为 null。</param>
        public bool RenameSaveItem(int id, string name) => ChangeItem(id, item => item.Rename(name));
        /// <summary>持久化槽备注，不存在时返回 false；写入失败向外抛出。</summary>
        /// <param name="id">待修改的槽号。</param>
        /// <param name="note">新的备注，允许为 null。</param>
        public bool SetSaveItemNote(int id, string note) => ChangeItem(id, item => item.SetNote(note));
        /// <summary>对条目副本提交索引，成功后再同步已借出的原条目引用。</summary>
        private bool ChangeItem(int id, Action<SaveItem> change)
        {
            return Execute(id, token =>
            {
                lock (_metadata)
                {
                    if (!_index.SaveItemDict.TryGetValue(id, out var original))
                    {
                        return false;
                    }

                    var modified = original.Copy();
                    change(modified);
                    var next = _index.Copy();
                    next.SaveItemDict[id] = modified;
                    WriteIndex(next, token);
                    change(original);
                    _index.SaveItemDict[id] = original;
                    return true;
                }
            });
        }

        /// <summary>删除是显式软删除，目录留在 SaveData/.deleted-*；索引失败时回滚目录，不递归销毁未知内容。</summary>
        /// <param name="id">待删除的槽号；索引中不存在时不执行删除。</param>
        public void DeleteSaveItem(int id) => DeleteSaveItemAsync(id).GetAwaiter().GetResult();
        /// <summary>先隔离槽目录再提交索引删除，成功后清除该槽缓存和匹配的当前选择。</summary>
        /// <param name="id">待删除的槽号；不存在时正常结束。</param>
        /// <param name="cancellationToken">提交索引前可请求取消；失败时尝试将目录移回原位置。</param>
        public Task DeleteSaveItemAsync(int id, CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(id, token =>
            {
                lock (_metadata)
                {
                    if (!_index.SaveItemDict.ContainsKey(id))
                    {
                        return false;
                    }

                    EnsureIndexWritable();
                    var path = SlotPath(id);
                    var tomb = SafePath(Path.Combine(SaveDirectory, ".deleted-" + id + "-" + Guid.NewGuid().ToString("N")));
                    var moved = Directory.Exists(path);
                    if (moved)
                    {
                        Directory.Move(path, tomb);
                    }

                    var next = _index.Copy();
                    next.SaveItemDict.Remove(id);
                    if (next.CurrentSaveId == id)
                    {
                        next.CurrentSaveId = null;
                    }

                    try
                    {
                        WriteIndex(next, token);
                    }
                    catch
                    {
                        if (moved)
                        {
                            Directory.Move(tomb, path);
                        }
                        throw;
                    }
                    lock (_cacheLock)
                    {
                        _cache.Remove(id);
                    }

                    return true;
                }
            }, cancellationToken);
        }

        /// <summary>逐槽执行软删除；不是整批事务，中途失败时保留此前已完成的删除。</summary>
        public void DeleteAllSaveItems()
        {
            foreach (var item in GetAllSaveItems())
            {
                DeleteSaveItem(item.SaveId);
            }
        }

        /// <summary>阻塞等待对象 JSON 提交，成功后将传入原实例放入该槽缓存。</summary>
        /// <param name="value">要保存的非 null 对象。</param>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">必须已登记的槽号。</param>
        public void SaveObject(object value, string name, int id = 0) => SaveObjectAsync(value, name, id).GetAwaiter().GetResult();
        /// <summary>调用线程序列化后排入同槽 IO，拒绝覆盖需要恢复的主件，提交后才更新缓存与内存保存时间。</summary>
        /// <param name="value">要保存的非 null 对象；缓存保留此原实例，而非 JSON 反序列化副本。</param>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">必须已登记且允许写入的槽号。</param>
        /// <param name="cancellationToken">提交前取消信号，提交成功后不再改报取消。</param>
        public Task SaveObjectAsync(object value, string name, int id = 0, CancellationToken cancellationToken = default)
        {
            ValidateName(name);
            cancellationToken.ThrowIfCancellationRequested();
            var json = Serialize(value);
            return ExecuteAsync(id, token =>
            {
                RequireSlot(id, true);
                var path = ObjectPath(id, name);
                SaveFileIO.ValidateWritableJson(path);
                var savedAt = DateTime.Now;
                SaveFileIO.WriteTextAtomic(path, json, token);
                SetCache(id, name, value);
                lock (_metadata)
                {
                    _index.SaveItemDict[id].UpdateSaveTime(savedAt);
                }

                return true;
            }, cancellationToken);
        }

        /// <summary>同步读取对象或返回缓存原实例；主备均缺失返回 null，内容或 IO 失败抛出异常。</summary>
        /// <typeparam name="T">目标引用类型，必须与同名缓存的实际类型兼容。</typeparam>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">必须已登记的槽号。</param>
        public T LoadObject<T>(string name, int id = 0) where T : class
            => ReadObject<T>(name, id).Value;
        /// <summary>通过异步读取取得对象，仍使用同一槽缓存；缺失返回 null，失败不转换成默认值。</summary>
        /// <typeparam name="T">目标引用类型，反序列化及 Converter 不得调用 Unity API。</typeparam>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">必须已登记的槽号。</param>
        /// <param name="cancellationToken">排队及文件读取前后检查的取消信号。</param>
        public async Task<T> LoadObjectAsync<T>(string name, int id = 0, CancellationToken cancellationToken = default) where T : class
            => (await ReadObjectAsync<T>(name, id, cancellationToken).ConfigureAwait(false)).Value;
        /// <summary>在调用线程读取对象，连同来源和缓存命中标记返回，不自动修复备份读取的主件。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">必须已登记的槽号。</param>
        public SaveReadResult<T> ReadObject<T>(string name, int id = 0) where T : class
        {
            ValidateName(name);
            return Execute(id, token => ReadObjectCore<T>(name, id, token));
        }
        /// <summary>按槽串行读取对象与来源信息；未命中缓存时在工作线程执行反序列化。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">必须已登记的槽号。</param>
        /// <param name="cancellationToken">排队及读取前后检查的取消信号，不中断已进入的同步文件读取。</param>
        public Task<SaveReadResult<T>> ReadObjectAsync<T>(string name, int id = 0, CancellationToken cancellationToken = default) where T : class
        {
            ValidateName(name);
            return ExecuteAsync(id, token => ReadObjectCore<T>(name, id, token), cancellationToken);
        }
        /// <summary>优先检查同槽同名缓存及类型，磁盘读取完成且未取消后才发布新缓存。</summary>
        private SaveReadResult<T> ReadObjectCore<T>(string name, int id, CancellationToken token) where T : class
        {
            RequireSlot(id);
            token.ThrowIfCancellationRequested();
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(id, out var objects) && objects.TryGetValue(name, out var cached))
                {
                    if (cached.Value is T typed)
                    {
                        return new SaveReadResult<T>(typed, cached.Status, true);
                    }

                    throw new InvalidOperationException("Cached save type does not match requested type: " + name);
                }
            }
            var result = ReadFile<T>(ObjectPath(id, name));
            token.ThrowIfCancellationRequested();
            if (result.Value != null)
            {
                SetCache(id, name, result.Value, result.Status);
            }

            return result;
        }

        /// <summary>查询已登记槽中的主文件是否存在，不查缓存、备份或内容有效性。</summary>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">待查询的槽号；未登记时返回 false。</param>
        public bool HasObject(string name, int id = 0)
        {
            ValidateName(name);
            return Execute(id, _ => HasSaveItem(id) && File.Exists(ObjectPath(id, name)));
        }
        /// <summary>按 Ordinal 排序列出槽目录下的 JSON 主文件基名，不包含备份与软删除文件。</summary>
        /// <param name="id">待查询槽号；槽或目录不存在时返回空列表。</param>
        public List<string> GetObjectNames(int id)
        {
            return Execute(id, _ =>
            {
                if (!HasSaveItem(id) || !Directory.Exists(SlotPath(id)))
                {
                    return new List<string>();
                }

                return Directory.GetFiles(SlotPath(id), "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).ToList();
            });
        }

        /// <summary>软删除对象主件与备份并清除缓存，返回删除前主件是否存在；移动失败时尝试回滚已移动文件。</summary>
        /// <param name="name">不含扩展名的合法文件名。</param>
        /// <param name="id">所属槽号；未登记时返回 false。</param>
        public bool DeleteObject(string name, int id = 0)
        {
            ValidateName(name);
            return Execute(id, token =>
            {
                if (!HasSaveItem(id))
                {
                    return false;
                }

                var path = ObjectPath(id, name);
                var existed = File.Exists(path);
                // 备份先移走；失败不能让后续读取复活已删除对象。所有原件仍可在 tomb 中找回。
                var suffix = ".deleted-" + Guid.NewGuid().ToString("N");
                var files = new[] { path + ".bak", path };
                var moved = new List<string>();
                try
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var file in files)
                    {
                        if (File.Exists(SafePath(file)))
                        {
                            File.Move(file, file + suffix);
                            moved.Add(file);
                        }
                    }
                }
                catch
                {
                    foreach (var file in moved.AsEnumerable().Reverse())
                    {
                        File.Move(file + suffix, file);
                    }
                    throw;
                }
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(id, out var objects))
                    {
                        objects.Remove(name);
                    }
                }

                return existed;
            });
        }

        /// <summary>阻塞等待全局设置 JSON 提交，不使用对象槽或对象缓存。</summary>
        /// <param name="value">要保存的非 null 设置对象。</param>
        /// <param name="name">不含扩展名的合法设置文件名。</param>
        public void SaveGlobalSetting(object value, string name) => SaveGlobalSettingAsync(value, name).GetAwaiter().GetResult();
        /// <summary>在调用线程捕获设置 JSON 后串行写入设置目录，拒绝覆盖需要恢复的内容。</summary>
        /// <param name="value">要保存的非 null 设置对象。</param>
        /// <param name="name">不含扩展名的合法设置文件名。</param>
        /// <param name="cancellationToken">排队与提交前取消信号。</param>
        public Task SaveGlobalSettingAsync(object value, string name, CancellationToken cancellationToken = default)
        {
            ValidateName(name);
            cancellationToken.ThrowIfCancellationRequested();
            var json = Serialize(value);
            return ExecuteAsync(-1, token =>
            {
                var path = SettingPath(name);
                SaveFileIO.ValidateWritableJson(path);
                SaveFileIO.WriteTextAtomic(path, json, token);
                return true;
            }, cancellationToken);
        }

        /// <summary>同步读取设置，仅主备均缺失时创建并保存默认实例；损坏或 IO 失败不覆盖为默认值。</summary>
        /// <typeparam name="T">可无参创建的设置引用类型。</typeparam>
        /// <param name="name">不含扩展名的合法设置文件名。</param>
        public T LoadGlobalSetting<T>(string name) where T : class, new()
        {
            ValidateName(name);
            return Execute(-1, token =>
            {
                var path = SettingPath(name);
                var value = ReadValue<T>(path);
                if (value != null)
                {
                    return value;
                }

                value = new T();
                SaveFileIO.WriteTextAtomic(path, Serialize(value), token, false);
                return value;
            });
        }
        /// <summary>读取已有设置，主备均缺失时返回 false，不创建默认值；内容或 IO 失败仍抛出。</summary>
        /// <typeparam name="T">目标设置引用类型。</typeparam>
        /// <param name="value">读取结果；缺失时为 null。</param>
        /// <param name="name">不含扩展名的合法设置文件名。</param>
        public bool TryLoadGlobalSetting<T>(out T value, string name) where T : class
        {
            ValidateName(name);
            value = Execute(-1, _ => ReadValue<T>(SettingPath(name)));
            return value != null;
        }
        /// <summary>同步读取设置及主件或备份来源，不使用缓存，不创建默认值或自动修盘。</summary>
        /// <typeparam name="T">目标设置引用类型。</typeparam>
        /// <param name="name">不含扩展名的合法设置文件名。</param>
        public SaveReadResult<T> ReadGlobalSetting<T>(string name) where T : class
        {
            ValidateName(name);
            return Execute(-1, _ => ReadFile<T>(SettingPath(name)));
        }

        /// <summary>将整个设置目录移到根目录下的独立软删除位置，不递归清理其中内容。</summary>
        public void DeleteAllSetting()
        {
            Execute(-1, token =>
            {
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(SafePath(SettingDirectory)))
                {
                    Directory.Move(SettingDirectory, SafePath(Path.Combine(StorageRoot, ".deleted-settings-" + Guid.NewGuid().ToString("N"))));
                }

                return true;
            });
        }

        /// <summary>显式恢复对象备份并移除该对象缓存；原 .bak 保持不变，已有主件另存后返回其路径。</summary>
        /// <param name="name">不含扩展名的合法对象文件名。</param>
        /// <param name="id">必须已登记的槽号。</param>
        /// <returns>原主件保留路径；主件原本不存在时为 null。</returns>
        public string RestoreObjectBackup(string name, int id = 0)
        {
            ValidateName(name);
            return Execute(id, token =>
            {
                RequireSlot(id);
                var preserved = SaveFileIO.RestoreBackup<JToken>(ObjectPath(id, name), null, token);
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(id, out var objects))
                    {
                        objects.Remove(name);
                    }
                }

                return preserved;
            });
        }
        /// <summary>显式用有效备份替换设置主件，保留原备份及已有主件。</summary>
        /// <param name="name">不含扩展名的合法设置文件名。</param>
        /// <returns>原主件保留路径；主件原本不存在时为 null。</returns>
        public string RestoreSettingBackup(string name)
        {
            ValidateName(name);
            return Execute(-1, token => SaveFileIO.RestoreBackup<JToken>(SettingPath(name), null, token));
        }
        /// <summary>仅在备份索引读取模式下恢复索引，提交后替换内存索引、解除恢复限制并清空对象缓存。</summary>
        /// <returns>原索引主件保留路径；主件原本不存在时为 null。</returns>
        public string RestoreIndexBackup()
        {
            return Execute(-2, token =>
            {
                lock (_metadata)
                {
                    if (!_requiresIndexRecovery)
                    {
                        throw new InvalidOperationException("Reopen a damaged index in backup-read mode before restoring it; an active healthy index cannot be rolled back.");
                    }

                    SaveIndex restored = null;
                    var preserved = SaveFileIO.RestoreBackup<SaveIndex>(SafePath(IndexPath), null, token, index =>
                    {
                        index.Validate();
                        foreach (var item in index.SaveItemDict.Values)
                        {
                            RefreshSaveTime(item);
                        }

                        restored = index;
                    });
                    _index = restored;
                    _requiresIndexRecovery = false;
                    lock (_cacheLock)
                    {
                        _cache.Clear();
                    }

                    return preserved;
                }
            });
        }

        /// <summary>阻塞等待创建主模型及其新槽，拒绝覆盖已有槽，返回缓存持有的模型原实例。</summary>
        /// <typeparam name="T">可无参创建并能保留所赋 Id 的普通主模型类型。</typeparam>
        /// <param name="id">新模型与新槽共同使用的非负槽号。</param>
        public T CreateModel<T>(int id) where T : class, ISaveModel, new() => CreateModelAsync<T>(id).GetAwaiter().GetResult();
        /// <summary>在调用线程创建并序列化主模型，再异步提交模型目录及新槽索引。</summary>
        /// <typeparam name="T">可无参创建并能保留所赋 Id 的普通主模型类型。</typeparam>
        /// <param name="id">新模型与新槽共同使用的非负槽号。</param>
        /// <param name="cancellationToken">创建前及提交前检查的取消信号。</param>
        public Task<T> CreateModelAsync<T>(int id, CancellationToken cancellationToken = default) where T : class, ISaveModel, new()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = new T { Id = id };
            if (id < 0 || model.Id != id)
            {
                throw new ArgumentException("Model Id must match a non-negative slot id.");
            }

            var json = Serialize(model);
            return ExecuteAsync(id, cancellation =>
            {
                CreateSlot(id, model, json, cancellation);
                return model;
            }, cancellationToken);
        }
        /// <summary>按模型 Id 保存到既有槽的固定主模型文件，不隐式新建槽。</summary>
        /// <typeparam name="T">实现槽身份契约的主模型类型。</typeparam>
        /// <param name="model">待保存的非 null 主模型。</param>
        public void SaveModel<T>(T model) where T : class, ISaveModel
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            SaveObject(model, ModelFileName, model.Id);
        }
        /// <summary>捕获模型 JSON 后异步保存到其 Id 对应的既有槽，复用对象写入与缓存契约。</summary>
        /// <typeparam name="T">实现槽身份契约的主模型类型。</typeparam>
        /// <param name="model">待保存的非 null 主模型。</param>
        /// <param name="cancellationToken">排队与提交前取消信号。</param>
        public Task SaveModelAsync<T>(T model, CancellationToken cancellationToken = default) where T : class, ISaveModel
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }
            return SaveObjectAsync(model, ModelFileName, model.Id, cancellationToken);
        }
        /// <summary>读取指定槽或当前槽主模型并校验 Id；未选择槽时抛出，模型缺失时返回 null。</summary>
        /// <typeparam name="T">实现槽身份契约的目标模型类型。</typeparam>
        /// <param name="id">显式槽号；null 使用索引的当前选择。</param>
        public T LoadModel<T>(int? id = null) where T : class, ISaveModel
        {
            var slot = id ?? CurrentSaveId ?? throw new InvalidOperationException("No current save slot is selected.");
            var model = LoadObject<T>(ModelFileName, slot);
            if (model != null && model.Id != slot)
            {
                throw new InvalidDataException("Model Id does not match its slot: " + slot);
            }

            return model;
        }
        /// <summary>异步读取主模型并验证其 Id 与目标槽一致，不把身份错误或读取失败当作缺失。</summary>
        /// <typeparam name="T">实现槽身份契约的目标模型类型。</typeparam>
        /// <param name="id">显式槽号；null 使用索引的当前选择，未选择时抛出。</param>
        /// <param name="cancellationToken">排队及读取前后检查的取消信号。</param>
        public async Task<T> LoadModelAsync<T>(int? id = null, CancellationToken cancellationToken = default) where T : class, ISaveModel
        {
            var slot = id ?? CurrentSaveId ?? throw new InvalidOperationException("No current save slot is selected.");
            var model = await LoadObjectAsync<T>(ModelFileName, slot, cancellationToken).ConfigureAwait(false);
            if (model != null && model.Id != slot)
            {
                throw new InvalidDataException("Model Id does not match its slot: " + slot);
            }

            return model;
        }

        /// <summary>按槽号稳定加载主模型；跳过没有主模型的普通对象槽，仍使用同一对象缓存。</summary>
        /// <typeparam name="T">实现槽身份契约的目标模型类型。</typeparam>
        /// <param name="cancellationToken">各槽加载前及单次读取中检查的取消信号；失败不返回部分列表。</param>
        public async Task<List<T>> LoadAllModelsAsync<T>(CancellationToken cancellationToken = default) where T : class, ISaveModel
        {
            var models = new List<T>();
            foreach (var item in GetAllSaveItems().OrderBy(item => item.SaveId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = await LoadModelAsync<T>(item.SaveId, cancellationToken).ConfigureAwait(false);
                if (model != null)
                {
                    models.Add(model);
                }
            }
            return models;
        }

        /// <summary>清空当前对象缓存而不改盘，不取消其他槽已在进行的读取或阻止它们随后重新填充缓存。</summary>
        public void ClearCache()
        {
            Execute(-2, _ =>
            {
                lock (_cacheLock)
                {
                    _cache.Clear();
                }
                return true;
            });
        }
        /// <summary>按槽和忽略大小写的文件名保存对象原实例及来源，不复制或持久化对象。</summary>
        private void SetCache(int id, string name, object value, SaveReadStatus status = SaveReadStatus.Success)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(id, out var objects))
                {
                    _cache.Add(id, objects = new Dictionary<string, CachedObject>(StringComparer.OrdinalIgnoreCase));
                }

                objects[name] = new CachedObject { Value = value, Status = status };
            }
        }

        /// <summary>读取文件结果中的对象值，缺失为 null，沿用底层失败异常。</summary>
        private T ReadValue<T>(string path) where T : class => ReadFile<T>(path).Value;
        /// <summary>校验序列化策略后读取主件或备份，主备均缺失返回空值，内容失败转为异常。</summary>
        private SaveReadResult<T> ReadFile<T>(string path) where T : class
        {
            ValidateSerializer();
            var status = SaveFileIO.ReadJson<T>(path, SerializerSettings, out var value, out var error);
            if (status == SaveReadStatus.Failed)
            {
                throw new InvalidDataException("Cannot read save data: " + path + " | " + error);
            }

            return new SaveReadResult<T>(value, status);
        }
        /// <summary>在调用线程检查对象与引擎存活状态，使用当前显式配置生成待提交 JSON 快照。</summary>
        private string Serialize(object value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            lock (_lifecycle)
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(nameof(SaveStore));
                }
            }

            ValidateSerializer();
            return SaveFileIO.Serialize(value, SerializerSettings, SerializationFormat);
        }
        /// <summary>拒绝 TypeNameHandling，避免存档中的类型元数据决定 CLR 类型实例化。</summary>
        private void ValidateSerializer()
        {
            if (SerializerSettings.TypeNameHandling != TypeNameHandling.None)
            {
                throw new NotSupportedException("Save data must not instantiate CLR types from JSON ($type).");
            }
        }
        /// <summary>计入引擎活动操作后在元数据锁内读取索引，使释放过程等待本次读取退出。</summary>
        private T ReadIndex<T>(Func<SaveIndex, T> read)
        {
            using (var operation = Begin(CancellationToken.None))
            {
                try
                {
                    lock (_metadata)
                    {
                        return read(_index);
                    }
                }
                finally
                {
                    End();
                }
            }
        }
        /// <summary>确认槽已登记，写入调用额外要求索引不处于待恢复状态。</summary>
        private void RequireSlot(int id, bool writing = false)
        {
            lock (_metadata)
            {
                if (writing)
                {
                    EnsureIndexWritable();
                }
                if (!_index.SaveItemDict.ContainsKey(id))
                {
                    throw new KeyNotFoundException("Unknown save slot: " + id);
                }
            }
        }
        /// <summary>索引仅来自备份时拒绝修改，要求先显式恢复，避免覆盖原损坏证据。</summary>
        private void EnsureIndexWritable()
        {
            if (_requiresIndexRecovery)
            {
                throw new SaveRecoveryRequiredException(IndexPath);
            }
        }
        /// <summary>验证主件可写及候选索引合法，提交成功后才发布新的内存索引。</summary>
        private void WriteIndex(SaveIndex next, CancellationToken token)
        {
            EnsureIndexWritable();
            SaveFileIO.ValidateWritableJson(SafePath(IndexPath));
            next.Validate();
            SaveFileIO.WriteTextAtomic(IndexPath, SaveFileIO.Serialize(next, null, Formatting.Indented), token);
            _index = next;
        }
        /// <summary>用槽内 JSON 主文件的最新本地修改时间向后校准条目保存时间，不改写索引。</summary>
        private void RefreshSaveTime(SaveItem item)
        {
            var directory = SlotPath(item.SaveId);
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var time = File.GetLastWriteTime(SafePath(path));
                if (time > item.LastSaveTime)
                {
                    item.UpdateSaveTime(time);
                }
            }
        }
        /// <summary>以固定区域格式构造槽目录路径，并检查主路径及备份路径不存在已有链接。</summary>
        private string SlotPath(int id) => SafePath(Path.Combine(SaveDirectory, id.ToString(CultureInfo.InvariantCulture)));
        /// <summary>为已校验名称构造槽内 JSON 主文件路径。</summary>
        private string ObjectPath(int id, string name) => SafePath(Path.Combine(SlotPath(id), name + ".json"));
        /// <summary>为已校验名称构造全局设置 JSON 主文件路径。</summary>
        private string SettingPath(string name) => SafePath(Path.Combine(SettingDirectory, name + ".json"));
        /// <summary>在操作前检查目标与备份路径的已有链接，校验通过后返回原路径。</summary>
        private string SafePath(string path)
        {
            SaveFileIO.EnsureNoLinks(path);
            SaveFileIO.EnsureNoLinks(path + ".bak");
            return path;
        }
        /// <summary>统一验证对象和设置名称，禁止名称改变既定目录层级。</summary>
        private static void ValidateName(string name)
            => SaveFileIO.ValidateFileName(name);

        /// <summary>同步进入作用域串行闸门，在调用线程执行业务，并在所有退出路径归还闸门和活动计数。</summary>
        private T Execute<T>(int scope, Func<CancellationToken, T> action)
        {
            using (var operation = Begin(CancellationToken.None))
            {
                var gate = _gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
                var entered = false;
                try
                {
                    gate.Wait(operation.Token);
                    entered = true;
                    operation.Token.ThrowIfCancellationRequested();
                    return action(operation.Token);
                }
                finally
                {
                    if (entered)
                    {
                        gate.Release();
                    }
                    End();
                }
            }
        }
        /// <summary>异步等待作用域闸门，再在线程池执行同步 IO，活动计数覆盖排队至业务退出的整个阶段。</summary>
        private async Task<T> ExecuteAsync<T>(int scope, Func<CancellationToken, T> action, CancellationToken cancellationToken)
        {
            using (var operation = Begin(cancellationToken))
            {
                var gate = _gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
                var entered = false;
                try
                {
                    await gate.WaitAsync(operation.Token).ConfigureAwait(false);
                    entered = true;
                    return await Task.Run(() =>
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        return action(operation.Token);
                    }).ConfigureAwait(false);
                }
                finally
                {
                    if (entered)
                    {
                        gate.Release();
                    }
                    End();
                }
            }
        }
        /// <summary>拒绝关闭后的新操作，将调用方取消与引擎关闭信号链接并登记活动操作。</summary>
        private CancellationTokenSource Begin(CancellationToken token)
        {
            lock (_lifecycle)
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(nameof(SaveStore));
                }

                token.ThrowIfCancellationRequested();
                var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
                _active++;
                return linked;
            }
        }
        /// <summary>减少活动操作计数并唤醒正在等待排空的释放调用。</summary>
        private void End()
        {
            lock (_lifecycle)
            {
                _active--;
                Monitor.PulseAll(_lifecycle);
            }
        }
        /// <summary>禁止新操作、取消未提交工作并阻塞等待活动操作退出，再清空缓存和释放根锁及各闸门。</summary>
        /// <remarks>重复或并发调用等待同一轮释放完成；不得从本引擎的 Converter 或内部操作中重入释放。</remarks>
        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_isDisposed)
                {
                    while (!_hasFinishedDisposal)
                    {
                        Monitor.Wait(_lifecycle);
                    }
                    return;
                }
                _isDisposed = true;
            }
            try
            {
                _shutdown.Cancel();
                lock (_lifecycle)
                {
                    while (_active != 0)
                    {
                        Monitor.Wait(_lifecycle);
                    }
                }

                lock (_cacheLock)
                {
                    _cache.Clear();
                }

                _rootLock.Dispose();
                _shutdown.Dispose();
                foreach (var gate in _gates.Values)
                {
                    gate.Dispose();
                }
            }
            finally
            {
                lock (_lifecycle)
                {
                    _hasFinishedDisposal = true;
                    Monitor.PulseAll(_lifecycle);
                }
            }
        }
    }
}
