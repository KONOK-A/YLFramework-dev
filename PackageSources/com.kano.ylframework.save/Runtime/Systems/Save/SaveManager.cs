using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace YLFramework.Save
{
    /// <summary>Unity 入口，仅负责主线程默认路径捕获和既有 API；所有存档、模型、设置及缓存均由 SaveStore 实现。</summary>
    /// <remarks>
    /// 仅支持当前索引格式；不转换历史存档，不自动清空损坏档。
    /// 对象/设置仍为裸 JSON。损坏无可用备份、版本、IO 和权限失败会抛异常；备份读取不修盘。
    /// 新异步接口在调用线程序列化快照后异步 IO；DTO/Converter 不得在线程池调用 Unity API。
    /// SetStorageRoot / Close 取消并排空旧 owner，不能把旧请求的缓存写入新根。
    /// </remarks>
    public static partial class SaveManager
    {
        /// <summary>当前索引格式版本，与统一存储引擎保持一致，不表示支持历史格式导入。</summary>
        public const int CurrentDataVersion = SaveStore.FormatVersion;
        private static readonly object _sync = new object();
        private static SaveStore _store;
        private static string _storageRootOverride;
        private static string _defaultStorageRoot;
        /// <summary>后续引擎共用的对象与设置序列化配置原实例；应在并发使用前配置，关闭不会重建此配置。</summary>
        public static JsonSerializerSettings SerializerSettings { get; } = new JsonSerializerSettings();
        /// <summary>后续对象与设置写入的 JSON 排版，不改变索引固定排版。</summary>
        public static Formatting SerializationFormat { get; set; } = Formatting.Indented;

        /// <summary>显式根可在纯 BCL 环境使用；默认 Unity 路径必须先在主线程捕获。</summary>
        public static string StorageRoot
        {
            get
            {
                lock (_sync)
                {
                    if (!string.IsNullOrEmpty(_storageRootOverride))
                    {
                        return _storageRootOverride;
                    }

                    if (_defaultStorageRoot != null)
                    {
                        return _defaultStorageRoot;
                    }

                    if (SynchronizationContext.Current?.GetType().FullName != "UnityEngine.UnitySynchronizationContext")
                    {
                        throw new InvalidOperationException("Capture the default save root on the Unity main thread, or call SetStorageRoot with an explicit path before worker use.");
                    }

                    return _defaultStorageRoot = Application.persistentDataPath;
                }
            }
        }

        /// <summary>按当前根延迟创建唯一入口引擎，并将最新排版选项传给后续写入。</summary>
        private static SaveStore Store
        {
            get
            {
                lock (_sync)
                {
                    if (_store == null)
                    {
                        _store = new SaveStore(StorageRoot, SerializerSettings, SerializationFormat);
                    }

                    _store.SerializationFormat = SerializationFormat;
                    return _store;
                }
            }
        }

        /// <summary>只切换路径，不复制或迁移数据。同根重设也会关闭旧 owner 并清除缓存。</summary>
        /// <param name="rootPath">下次创建引擎的根目录；null 或空字符串恢复默认路径，路径合法性延迟到引擎创建时校验。</param>
        public static void SetStorageRoot(string rootPath)
        {
            SaveStore previous;
            lock (_sync)
            {
                previous = _store;
                _store = null;
                _storageRootOverride = string.IsNullOrEmpty(rootPath) ? null : rootPath;
            }
            previous?.Dispose();
        }

        /// <summary>解除当前引擎并等待其释放，不修改显式根或序列化配置；后续访问可重新打开同根。</summary>
        public static void Close()
        {
            SaveStore previous;
            lock (_sync)
            {
                previous = _store;
                _store = null;
            }
            previous?.Dispose();
        }

        /// <summary>新 Unity 执行会话关闭旧引擎、捕获主线程默认路径，并去重安装退出清理。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginUnitySession()
        {
            Close();
            lock (_sync)
            {
                _storageRootOverride = null;
                _defaultStorageRoot = Application.persistentDataPath;
            }
            Application.quitting -= Close;
            Application.quitting += Close;
        }

        /// <summary>持久化的当前槽选择；null 清除选择，设置不存在的槽号会抛出异常。</summary>
        public static int? CurrentSaveId
        {
            get => Store.CurrentSaveId; set => Store.CurrentSaveId = value;
        }
        /// <summary>当前索引是否从备份读取，显式恢复前不能继续修改索引或保存对象。</summary>
        public static bool IndexNeedsRecovery => Store.IndexNeedsRecovery;
        /// <summary>取得按指定规则排列的新列表，列表内元数据仍为引擎持有的原实例。</summary>
        /// <param name="saveOrder">排序规则；None 或未知值保留引擎当前枚举顺序。</param>
        public static List<SaveItem> GetAllSaveItems(SaveOrder saveOrder = SaveOrder.None)
        {
            var items = Store.GetAllSaveItems();
            switch (saveOrder)
            {
                case SaveOrder.CreateTime:
                    return items.OrderBy(s => s.CreateTime).ToList();
                case SaveOrder.CreateTimeDescending:
                    return items.OrderByDescending(s => s.CreateTime).ToList();
                case SaveOrder.SaveTime:
                    return items.OrderBy(s => s.LastSaveTime).ToList();
                case SaveOrder.SaveTimeDescending:
                    return items.OrderByDescending(s => s.LastSaveTime).ToList();
                case SaveOrder.SaveId:
                    return items.OrderBy(s => s.SaveId).ToList();
                case SaveOrder.SaveIdDescending:
                    return items.OrderByDescending(s => s.SaveId).ToList();
                case SaveOrder.Name:
                    return items.OrderBy(s => s.DisplayName ?? string.Empty).ToList();
                case SaveOrder.NameDescending:
                    return items.OrderByDescending(s => s.DisplayName ?? string.Empty).ToList();
                default:
                    return items;
            }
        }
        /// <summary>按自定义键排序槽列表，不复制条目，也不将排序结果写回索引。</summary>
        /// <typeparam name="T">排序键类型，使用该类型的默认比较器。</typeparam>
        /// <param name="orderFunc">从槽元数据提取排序键的非 null 函数。</param>
        /// <param name="isDescending">是否按降序排列。</param>
        public static List<SaveItem> GetAllSaveItems<T>(Func<SaveItem, T> orderFunc, bool isDescending = false)
        {
            if (orderFunc == null)
            {
                throw new ArgumentNullException(nameof(orderFunc));
            }

            var items = Store.GetAllSaveItems();
            return (isDescending ? items.OrderByDescending(orderFunc) : items.OrderBy(orderFunc)).ToList();
        }
        /// <summary>仅查询索引是否登记该槽，不检查槽目录或对象文件。</summary>
        /// <param name="saveId">待查询的槽号。</param>
        public static bool HasSaveItem(int saveId) => Store.HasSaveItem(saveId);
        /// <summary>按条目槽号查询索引，不要求该条目就是当前引擎持有的实例。</summary>
        /// <param name="saveItem">待查询条目；null 返回 false。</param>
        public static bool HasSaveItem(SaveItem saveItem) => saveItem != null && HasSaveItem(saveItem.SaveId);
        /// <summary>尝试取得槽元数据原实例；未登记返回 false，打开或读取索引失败仍抛出。</summary>
        /// <param name="saveId">待查询的槽号。</param>
        /// <param name="saveItem">索引持有的条目，未登记时为 null。</param>
        public static bool TryGetSaveItem(int saveId, out SaveItem saveItem)
        {
            saveItem = Store.GetSaveItem(saveId);
            return saveItem != null;
        }
        /// <summary>按槽号取得索引内元数据原实例，未登记时返回 null。</summary>
        /// <param name="id">待查询的槽号。</param>
        public static SaveItem GetSaveItemWithId(int id) => Store.GetSaveItem(id);
        /// <summary>返回索引登记的槽数，不按磁盘目录数量推断。</summary>
        public static int GetSaveItemCount() => Store.GetAllSaveItems().Count;
        /// <summary>同步创建最低未占用槽号的空槽，不创建主模型或自动改变当前选择。</summary>
        public static SaveItem CreateSaveItem() => Store.CreateSaveItem();
        /// <summary>异步提交新空槽索引，拒绝覆盖索引或磁盘上已占用的槽号。</summary>
        /// <param name="saveId">指定非负槽号；null 自动选择最低未占用槽号。</param>
        /// <param name="cancellationToken">排队与提交前取消信号。</param>
        public static Task<SaveItem> CreateSaveItemAsync(int? saveId = null, CancellationToken cancellationToken = default) => Store.CreateSaveItemAsync(saveId, cancellationToken);
        /// <summary>持久化槽显示名称，槽不存在时返回 false，写盘失败不先修改原条目。</summary>
        /// <param name="saveId">待修改槽号。</param>
        /// <param name="displayName">新的显示名称，允许为 null。</param>
        public static bool RenameSaveItem(int saveId, string displayName) => Store.RenameSaveItem(saveId, displayName);
        /// <summary>按条目槽号持久化显示名称，条目为 null 或槽不存在时返回 false。</summary>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        /// <param name="displayName">新的显示名称，允许为 null。</param>
        public static bool RenameSaveItem(SaveItem saveItem, string displayName) => saveItem != null && RenameSaveItem(saveItem.SaveId, displayName);
        /// <summary>持久化槽备注，槽不存在时返回 false，IO 或恢复限制错误仍抛出。</summary>
        /// <param name="saveId">待修改槽号。</param>
        /// <param name="note">新的备注，允许为 null。</param>
        public static bool SetSaveItemNote(int saveId, string note) => Store.SetSaveItemNote(saveId, note);
        /// <summary>按条目槽号持久化备注，条目为 null 或槽不存在时返回 false。</summary>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        /// <param name="note">新的备注，允许为 null。</param>
        public static bool SetSaveItemNote(SaveItem saveItem, string note) => saveItem != null && SetSaveItemNote(saveItem.SaveId, note);
        /// <summary>同步软删除槽目录并提交索引删除，不递归销毁目录内容；未登记时不操作。</summary>
        /// <param name="saveId">待删除槽号。</param>
        public static void DeleteSaveItem(int saveId) => Store.DeleteSaveItem(saveId);
        /// <summary>按条目槽号软删除槽，不要求传入当前引擎持有的条目实例。</summary>
        /// <param name="saveItem">提供槽号的条目；null 不操作。</param>
        public static void DeleteSaveItem(SaveItem saveItem)
        {
            if (saveItem != null)
            {
                DeleteSaveItem(saveItem.SaveId);
            }
        }
        /// <summary>异步软删除槽，索引提交失败时尝试恢复目录；不存在的槽正常结束。</summary>
        /// <param name="saveId">待删除槽号。</param>
        /// <param name="cancellationToken">排队与索引提交前取消信号。</param>
        public static Task DeleteSaveItemAsync(int saveId, CancellationToken cancellationToken = default) => Store.DeleteSaveItemAsync(saveId, cancellationToken);
        /// <summary>逐槽执行软删除，不构成整批事务；中途失败不会撤销此前成功的删除。</summary>
        public static void DeleteAllSaveItems() => Store.DeleteAllSaveItems();
        /// <summary>先逐槽软删除，再软删除设置目录；不删除根目录，也不是全根原子操作。</summary>
        public static void DeleteAll()
        {
            var store = Store;
            store.DeleteAllSaveItems();
            store.DeleteAllSetting();
        }

        /// <summary>同步保存指定名称的对象；槽未登记时记录 Unity 错误并返回，其他校验或 IO 失败仍抛出。</summary>
        /// <param name="saveObject">要保存的非 null 对象。</param>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">对象所属槽号，默认 0 不代表自动创建该槽。</param>
        public static void SaveObject(object saveObject, string saveFileName, int saveId = 0)
        {
            if (saveObject == null)
            {
                throw new ArgumentNullException(nameof(saveObject));
            }

            SaveFileIO.ValidateFileName(saveFileName);
            var store = Store;
            if (!store.HasSaveItem(saveId))
            {
                Debug.LogError("#YLFramework 没有该存档项，非法访问 :" + saveId);
                return;
            }
            store.SaveObject(saveObject, saveFileName, saveId);
        }
        /// <summary>按条目槽号同步保存具名对象；对象始终要求非 null，条目为 null 时不执行写入。</summary>
        /// <param name="saveObject">要保存的非 null 对象。</param>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static void SaveObject(object saveObject, string saveFileName, SaveItem saveItem)
        {
            if (saveObject == null)
            {
                throw new ArgumentNullException(nameof(saveObject));
            }

            if (saveItem != null)
            {
                SaveObject(saveObject, saveFileName, saveItem.SaveId);
            }
        }
        /// <summary>以对象运行时类型名保存到条目指定槽；条目为 null 时直接不操作。</summary>
        /// <param name="saveObject">要保存的对象；条目非空时不能为 null。</param>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static void SaveObject(object saveObject, SaveItem saveItem)
        {
            if (saveItem != null)
            {
                SaveObject(saveObject, saveItem.SaveId);
            }
        }
        /// <summary>以对象运行时类型名作为文件名同步保存，不自动创建槽。</summary>
        /// <param name="saveObject">要保存的非 null 对象。</param>
        /// <param name="saveId">对象所属槽号；未登记时记录错误并返回。</param>
        public static void SaveObject(object saveObject, int saveId = 0)
        {
            if (saveObject == null)
            {
                throw new ArgumentNullException(nameof(saveObject));
            }
            SaveObject(saveObject, saveObject.GetType().Name, saveId);
        }
        /// <summary>在调用线程捕获对象 JSON 后异步提交；与同步兼容入口不同，槽不存在会抛出而非只记录日志。</summary>
        /// <param name="saveObject">要保存的非 null 对象，成功后缓存保留原实例。</param>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">必须已登记的槽号。</param>
        /// <param name="cancellationToken">排队与提交前取消信号，提交成功后不改报取消。</param>
        public static Task SaveObjectAsync(object saveObject, string saveFileName, int saveId = 0, CancellationToken cancellationToken = default)
            => Store.SaveObjectAsync(saveObject, saveFileName, saveId, cancellationToken);

        /// <summary>同步读取对象；槽不存在时记录错误并返回 null，主备均缺失也返回 null，其他读取失败仍抛出。</summary>
        /// <typeparam name="T">目标引用类型，需与同名缓存类型兼容。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">对象所属槽号。</param>
        public static T LoadObject<T>(string saveFileName, int saveId = 0) where T : class
        {
            SaveFileIO.ValidateFileName(saveFileName);
            var store = Store;
            if (!store.HasSaveItem(saveId))
            {
                Debug.LogError("#YLFramework 没有该存档项，非法访问 :" + saveId);
                return null;
            }
            return store.LoadObject<T>(saveFileName, saveId);
        }
        /// <summary>按条目槽号读取具名对象，条目为 null 时直接返回 null。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static T LoadObject<T>(string saveFileName, SaveItem saveItem) where T : class => saveItem == null ? null : LoadObject<T>(saveFileName, saveItem.SaveId);
        /// <summary>以目标类型名读取条目指定槽中的对象，条目为 null 时返回 null。</summary>
        /// <typeparam name="T">目标引用类型，其名称同时作为文件名。</typeparam>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static T LoadObject<T>(SaveItem saveItem) where T : class => saveItem == null ? null : LoadObject<T>(typeof(T).Name, saveItem.SaveId);
        /// <summary>以目标类型名读取对象；未登记槽或主备均缺失时返回 null。</summary>
        /// <typeparam name="T">目标引用类型，其名称同时作为文件名。</typeparam>
        /// <param name="saveId">对象所属槽号。</param>
        public static T LoadObject<T>(int saveId = 0) where T : class => LoadObject<T>(typeof(T).Name, saveId);
        /// <summary>异步读取具名对象并复用缓存；主备均缺失返回 null，未登记槽或其他读取失败抛出。</summary>
        /// <typeparam name="T">目标引用类型，工作线程 Converter 不得访问 Unity API。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">必须已登记的槽号。</param>
        /// <param name="cancellationToken">排队及读取前后检查的取消信号。</param>
        public static Task<T> LoadObjectAsync<T>(string saveFileName, int saveId = 0, CancellationToken cancellationToken = default) where T : class
            => Store.LoadObjectAsync<T>(saveFileName, saveId, cancellationToken);
        /// <summary>同步读取对象及来源、缓存标记；主备均缺失返回 Missing，其他读取失败抛出，不自动修盘。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">必须已登记的槽号。</param>
        public static SaveReadResult<T> ReadObject<T>(string saveFileName, int saveId = 0) where T : class => Store.ReadObject<T>(saveFileName, saveId);
        /// <summary>异步读取对象与文件来源，备份来源在后续缓存命中时仍保留。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">必须已登记的槽号。</param>
        /// <param name="cancellationToken">排队及读取前后检查的取消信号。</param>
        public static Task<SaveReadResult<T>> ReadObjectAsync<T>(string saveFileName, int saveId = 0, CancellationToken cancellationToken = default) where T : class
            => Store.ReadObjectAsync<T>(saveFileName, saveId, cancellationToken);
        /// <summary>尝试同步读取对象；槽或文件缺失返回 false，不吞掉损坏、缓存类型或 IO 错误。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="value">读取结果，缺失时为 null。</param>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">对象所属槽号。</param>
        public static bool TryLoadObject<T>(out T value, string saveFileName, int saveId = 0) where T : class
        {
            SaveFileIO.ValidateFileName(saveFileName);
            var store = Store;
            value = store.HasSaveItem(saveId) ? store.LoadObject<T>(saveFileName, saveId) : null;
            return value != null;
        }
        /// <summary>按条目槽号尝试读取具名对象，条目为 null 时返回 false，不创建默认对象。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="value">读取结果，未读取到时为 null。</param>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static bool TryLoadObject<T>(out T value, string saveFileName, SaveItem saveItem) where T : class
        {
            value = null;
            return saveItem != null && TryLoadObject(out value, saveFileName, saveItem.SaveId);
        }
        /// <summary>以目标类型名尝试读取条目指定槽的对象，缺失返回 false，真实读取失败仍抛出。</summary>
        /// <typeparam name="T">目标引用类型，其名称同时作为文件名。</typeparam>
        /// <param name="value">读取结果，未读取到时为 null。</param>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static bool TryLoadObject<T>(out T value, SaveItem saveItem) where T : class => TryLoadObject(out value, typeof(T).Name, saveItem);
        /// <summary>以目标类型名尝试读取对象，槽或文件缺失返回 false。</summary>
        /// <typeparam name="T">目标引用类型，其名称同时作为文件名。</typeparam>
        /// <param name="value">读取结果，缺失时为 null。</param>
        /// <param name="saveId">对象所属槽号。</param>
        public static bool TryLoadObject<T>(out T value, int saveId = 0) where T : class => TryLoadObject(out value, typeof(T).Name, saveId);
        /// <summary>仅在槽或文件缺失时返回调用方提供的默认实例，不保存默认值，也不掩盖读取失败。</summary>
        /// <typeparam name="T">目标引用类型。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="defaultValue">缺失时直接返回的实例，允许为 null。</param>
        /// <param name="saveId">对象所属槽号。</param>
        public static T LoadObjectOrDefault<T>(string saveFileName, T defaultValue = null, int saveId = 0) where T : class
            => TryLoadObject<T>(out var value, saveFileName, saveId) ? value : defaultValue;

        /// <summary>软删除对象主件和备份并移除缓存，返回删除前主件是否存在；失败仍抛出。</summary>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">对象所属槽号；未登记时返回 false。</param>
        public static bool DeleteObject(string saveFileName, int saveId = 0)
        {
            SaveFileIO.ValidateFileName(saveFileName);
            return Store.DeleteObject(saveFileName, saveId);
        }
        /// <summary>按条目槽号软删除具名对象，条目为 null 时返回 false。</summary>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static bool DeleteObject(string saveFileName, SaveItem saveItem) => saveItem != null && DeleteObject(saveFileName, saveItem.SaveId);
        /// <summary>软删除指定名称的对象，不校验对象类型，也不返回是否存在。</summary>
        /// <typeparam name="T">保留的泛型调用参数，不参与路径选择或内容读取。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">对象所属槽号。</param>
        public static void DeleteObject<T>(string saveFileName, int saveId) where T : class => DeleteObject(saveFileName, saveId);
        /// <summary>按条目槽号软删除具名对象，不读取或校验文件中的对象类型。</summary>
        /// <typeparam name="T">保留的泛型调用参数，不参与路径选择或内容读取。</typeparam>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveItem">提供槽号的条目；null 不操作。</param>
        public static void DeleteObject<T>(string saveFileName, SaveItem saveItem) where T : class
        {
            if (saveItem != null)
            {
                DeleteObject(saveFileName, saveItem.SaveId);
            }
        }
        /// <summary>以目标类型名软删除对象，不读取文件或校验其实际类型。</summary>
        /// <typeparam name="T">用于提供文件名的引用类型。</typeparam>
        /// <param name="saveId">对象所属槽号。</param>
        public static void DeleteObject<T>(int saveId) where T : class => DeleteObject(typeof(T).Name, saveId);
        /// <summary>以目标类型名软删除条目指定槽的对象，条目为 null 时不操作。</summary>
        /// <typeparam name="T">用于提供文件名的引用类型。</typeparam>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static void DeleteObject<T>(SaveItem saveItem) where T : class
        {
            if (saveItem != null)
            {
                DeleteObject<T>(saveItem.SaveId);
            }
        }
        /// <summary>按 Ordinal 排序列出槽内 JSON 主文件基名；未登记或无目录时返回空列表。</summary>
        /// <param name="saveId">待查询槽号。</param>
        public static List<string> GetObjectNames(int saveId) => Store.GetObjectNames(saveId);
        /// <summary>按条目槽号列出对象主文件基名，条目为 null 时返回空列表。</summary>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static List<string> GetObjectNames(SaveItem saveItem) => saveItem == null ? new List<string>() : GetObjectNames(saveItem.SaveId);
        /// <summary>仅查询已登记槽的对象主文件是否存在，不查备份、缓存或 JSON 有效性。</summary>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveId">待查询槽号。</param>
        public static bool HasObject(string saveFileName, int saveId = 0)
        {
            SaveFileIO.ValidateFileName(saveFileName);
            return Store.HasObject(saveFileName, saveId);
        }
        /// <summary>按条目槽号查询对象主文件是否存在，条目为 null 时返回 false。</summary>
        /// <param name="saveFileName">不含扩展名的合法文件名。</param>
        /// <param name="saveItem">提供槽号的条目，允许为 null。</param>
        public static bool HasObject(string saveFileName, SaveItem saveItem) => saveItem != null && HasObject(saveFileName, saveItem.SaveId);
        /// <summary>清空当前对象缓存而不改盘，不取消其他槽正在执行的读取。</summary>
        public static void ClearCache() => Store.ClearCache();

        /// <summary>只有缺失文件才创建默认值；损坏/IO 错误不会被默认值覆盖。</summary>
        /// <typeparam name="T">可无参创建的设置引用类型。</typeparam>
        /// <param name="fileName">不含扩展名的合法设置文件名。</param>
        public static T LoadGlobalSetting<T>(string fileName) where T : class, new()
        {
            SaveFileIO.ValidateFileName(fileName);
            return Store.LoadGlobalSetting<T>(fileName);
        }
        /// <summary>以类型名读取全局设置，主备均缺失时创建并保存默认实例。</summary>
        /// <typeparam name="T">可无参创建的设置引用类型，其名称同时作为文件名。</typeparam>
        public static T LoadGlobalSetting<T>() where T : class, new() => LoadGlobalSetting<T>(typeof(T).Name);
        /// <summary>只尝试读取已有设置；主备均缺失返回 false，损坏或 IO 失败仍抛出，不创建默认值。</summary>
        /// <typeparam name="T">目标设置引用类型，本入口不调用无参构造器。</typeparam>
        /// <param name="setting">读取到的设置；缺失时为 null。</param>
        /// <param name="fileName">不含扩展名的合法设置文件名。</param>
        public static bool TryLoadGlobalSetting<T>(out T setting, string fileName) where T : class, new()
        {
            SaveFileIO.ValidateFileName(fileName);
            return Store.TryLoadGlobalSetting(out setting, fileName);
        }
        /// <summary>以类型名尝试读取已有全局设置，不创建默认实例或掩盖真实失败。</summary>
        /// <typeparam name="T">目标设置引用类型，其名称同时作为文件名。</typeparam>
        /// <param name="setting">读取到的设置；缺失时为 null。</param>
        public static bool TryLoadGlobalSetting<T>(out T setting) where T : class, new() => TryLoadGlobalSetting(out setting, typeof(T).Name);
        /// <summary>读取设置及主件或备份来源，不使用对象缓存，不创建默认值或自动修盘。</summary>
        /// <typeparam name="T">目标设置引用类型。</typeparam>
        /// <param name="fileName">不含扩展名的合法设置文件名。</param>
        public static SaveReadResult<T> ReadGlobalSetting<T>(string fileName) where T : class => Store.ReadGlobalSetting<T>(fileName);
        /// <summary>同步提交具名全局设置 JSON，不属于任何存档槽，也不更新对象缓存。</summary>
        /// <param name="saveObject">要保存的非 null 设置对象。</param>
        /// <param name="fileName">不含扩展名的合法设置文件名。</param>
        public static void SaveGlobalSetting(object saveObject, string fileName)
        {
            if (saveObject == null)
            {
                throw new ArgumentNullException(nameof(saveObject));
            }
            SaveFileIO.ValidateFileName(fileName);
            Store.SaveGlobalSetting(saveObject, fileName);
        }
        /// <summary>以对象运行时类型名为文件名同步保存全局设置。</summary>
        /// <param name="saveObject">要保存的非 null 设置对象。</param>
        public static void SaveGlobalSetting(object saveObject)
        {
            if (saveObject == null)
            {
                throw new ArgumentNullException(nameof(saveObject));
            }
            SaveGlobalSetting(saveObject, saveObject.GetType().Name);
        }
        /// <summary>在调用线程捕获设置 JSON 后异步提交，现有数据需要恢复时拒绝覆盖。</summary>
        /// <param name="saveObject">要保存的非 null 设置对象。</param>
        /// <param name="fileName">不含扩展名的合法设置文件名。</param>
        /// <param name="cancellationToken">排队与提交前取消信号。</param>
        public static Task SaveGlobalSettingAsync(object saveObject, string fileName, CancellationToken cancellationToken = default)
            => Store.SaveGlobalSettingAsync(saveObject, fileName, cancellationToken);
        /// <summary>软删除整个设置目录，不递归销毁其中内容，也不影响存档槽。</summary>
        public static void DeleteAllSetting() => Store.DeleteAllSetting();
        /// <summary>显式用有效备份恢复对象并清除对应缓存，保留原备份与已有主件。</summary>
        /// <param name="fileName">不含扩展名的合法对象文件名。</param>
        /// <param name="saveId">必须已登记的槽号。</param>
        /// <returns>原主件另存路径；主件原本不存在时为 null。</returns>
        public static string RestoreObjectBackup(string fileName, int saveId = 0) => Store.RestoreObjectBackup(fileName, saveId);
        /// <summary>显式用有效备份恢复全局设置，原备份保持不变。</summary>
        /// <param name="fileName">不含扩展名的合法设置文件名。</param>
        /// <returns>原主件另存路径；主件原本不存在时为 null。</returns>
        public static string RestoreSettingBackup(string fileName) => Store.RestoreSettingBackup(fileName);
        /// <summary>仅在备份索引读取模式下恢复索引，成功后解除恢复限制并清空对象缓存。</summary>
        /// <returns>原索引主件另存路径；主件原本不存在时为 null。</returns>
        public static string RestoreIndexBackup() => Store.RestoreIndexBackup();

        /// <summary>同步创建指定槽及主模型，不覆盖已有槽或自动改变当前选择。</summary>
        /// <typeparam name="T">可无参创建并保留所赋 Id 的普通主模型类型。</typeparam>
        /// <param name="saveId">新槽与主模型共同使用的非负槽号。</param>
        public static T CreateModel<T>(int saveId) where T : class, ISaveModel, new() => Store.CreateModel<T>(saveId);
        /// <summary>在调用线程创建并序列化主模型，再异步提交新槽与固定模型文件。</summary>
        /// <typeparam name="T">可无参创建并保留所赋 Id 的普通主模型类型。</typeparam>
        /// <param name="saveId">新槽与主模型共同使用的非负槽号。</param>
        /// <param name="cancellationToken">创建前及提交前检查的取消信号。</param>
        public static Task<T> CreateModelAsync<T>(int saveId, CancellationToken cancellationToken = default) where T : class, ISaveModel, new() => Store.CreateModelAsync<T>(saveId, cancellationToken);
        /// <summary>读取指定槽或当前选择的主模型，并校验模型 Id；未选择槽时抛出，模型文件缺失返回 null。</summary>
        /// <typeparam name="T">实现槽身份契约的目标主模型类型。</typeparam>
        /// <param name="saveId">显式槽号；null 使用索引的当前选择。</param>
        public static T LoadModel<T>(int? saveId = null) where T : class, ISaveModel => Store.LoadModel<T>(saveId);
        /// <summary>异步读取主模型并验证槽身份，不把身份不符或读取错误当作缺失。</summary>
        /// <typeparam name="T">实现槽身份契约的目标主模型类型。</typeparam>
        /// <param name="saveId">显式槽号；null 使用索引的当前选择，未选择时抛出。</param>
        /// <param name="cancellationToken">排队及读取前后检查的取消信号。</param>
        public static Task<T> LoadModelAsync<T>(int? saveId = null, CancellationToken cancellationToken = default) where T : class, ISaveModel => Store.LoadModelAsync<T>(saveId, cancellationToken);
        /// <summary>按模型 Id 同步保存到既有槽的固定主模型文件，不自动新建槽。</summary>
        /// <typeparam name="T">实现槽身份契约的主模型类型。</typeparam>
        /// <param name="model">要保存的非 null 主模型。</param>
        public static void SaveModel<T>(T model) where T : class, ISaveModel => Store.SaveModel(model);
        /// <summary>异步保存到模型 Id 对应的既有槽，沿用对象提交及缓存契约。</summary>
        /// <typeparam name="T">实现槽身份契约的主模型类型。</typeparam>
        /// <param name="model">要保存的非 null 主模型。</param>
        /// <param name="cancellationToken">排队与提交前取消信号。</param>
        public static Task SaveModelAsync<T>(T model, CancellationToken cancellationToken = default) where T : class, ISaveModel => Store.SaveModelAsync(model, cancellationToken);
        /// <summary>按槽号升序逐一加载主模型，跳过没有主模型的普通槽，失败不返回部分列表。</summary>
        /// <typeparam name="T">实现槽身份契约的目标主模型类型。</typeparam>
        /// <param name="cancellationToken">每槽加载前及读取中检查的取消信号。</param>
        public static Task<List<T>> LoadAllModelsAsync<T>(CancellationToken cancellationToken = default) where T : class, ISaveModel => Store.LoadAllModelsAsync<T>(cancellationToken);
    }
}
