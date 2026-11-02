using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>
    /// 本地制品的唯一写入/归属边界。版本只新增，发布源只读；目录摘要不代表生产签名授权。
    /// 同一 store 的显式项目集合是激活事务闭包；恢复依据 journal 决策选择完整闭包，不独立挑选各项目新旧指针。
    /// </summary>
    /// <remarks>
    /// 文件安装、指针镜像和清理分步落盘，不是一次文件系统整体替换；准备失败可留下已安装版本或 staging。
    /// 普通入口要求构造时的 Unity 线程；内容 IO 入口由调用方调度，操作门禁不代表所有读取都具备线程安全保证。
    /// </remarks>
    public sealed partial class ArtifactStore
    {
        private const string OwnerFile = ".ylr-store-owner.json";
        private const string VersionFile = ".ylr-version.json";
        private const string JournalFile = "commit-journal.json";
        private readonly Dictionary<string, ResourceProjectMount> _mounts;
        private readonly Dictionary<string, ResourceLocalSnapshot> _verified = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _recoveryNotes = new(StringComparer.Ordinal);
        private string _ownerId;
        private int _operationState;
        private bool IsOperating => Volatile.Read(ref _operationState) != 0;
        private bool _isCreating;
        private CancellationToken _operationCancellation;
        private readonly string _unityCompatibility = Application.unityVersion;
        // 仅用于使分段只读观测失效，不是版本索引；任一 Store 事务都保守地要求重试。
        private static long _activitySerial;
        private readonly int _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        internal Action<string> Checkpoint;
        /// <summary>本 Store 独占管理的规范绝对根路径，不接管未知既有目录。</summary>
        public string RootPath
        {
            get;
        }
        /// <summary>构造时明确绑定的完整项目集合，也是激活与恢复的闭包边界。</summary>
        public IReadOnlyList<ResourceProjectMount> Projects
        {
            get;
        }
        /// <summary>已记录恢复说明的集合副本；读取本属性不会执行恢复。</summary>
        public IReadOnlyList<string> RecoveryNotes => ResourceCatalog.Copy(_recoveryNotes.Values);
        /// <summary>应用持久数据目录下的默认私有子目录，不会因读取而创建。</summary>
        public static string DefaultRoot => Path.Combine(Application.persistentDataPath, "YLFramework", "Resource");

        /// <summary>校验私有根路径和显式项目闭包，构造本身不创建目录或认领既有内容。</summary>
        /// <param name="root">不得与受保护工程目录重叠的绝对私有子目录。</param>
        /// <param name="projects">1 至 64 个非空、项目标识在忽略大小写时仍互异的挂载。</param>
        public ArtifactStore(string root, IEnumerable<ResourceProjectMount> projects)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            {
                throw new ArgumentException("An absolute owned root is required.", nameof(root));
            }

            RootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            foreach (var protectedRoot in new[] { Application.dataPath, Path.Combine(projectRoot, "Packages"), Path.Combine(projectRoot, "ProjectSettings"), Path.Combine(projectRoot, "AssetBundles") })
            {
                if (Within(RootPath, protectedRoot) || Within(protectedRoot, RootPath))
                {
                    throw new ArgumentException("ArtifactStore overlaps a protected project directory.");
                }
            }

            if (RootPath == Path.GetPathRoot(RootPath)?.TrimEnd(Path.DirectorySeparatorChar) ||
                string.Equals(RootPath, Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A private child directory is required.");
            }

            Projects = ResourceCatalog.Copy(projects ?? throw new ArgumentNullException(nameof(projects)));
            if (Projects.Count == 0 || Projects.Count > 64 || Projects.Any(x => x == null) ||
                Projects.Select(x => x.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Projects.Count)
            {
                throw new ArgumentException("A bounded explicit, case-distinct project closure is required.");
            }

            _mounts = Projects.ToDictionary(x => x.ProjectId, StringComparer.Ordinal);
            foreach (var mount in Projects)
            {
                SafeSegment(mount.ProjectId);
            }

            ResourceLocalSnapshot.RejectLinks(RootPath);
        }

        // 按目录分隔边界判断规范路径是否位于指定根内，供保护目录重叠检查使用。
        private static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // 拒绝不适合用作 Store 路径段的标识及 Windows 保留名称。
        private static void SafeSegment(string value)
        {
            ResourceCatalogCodec.Id(value);
            var stem = value.Split('.')[0].ToUpperInvariant();
            if (value.EndsWith(".", StringComparison.Ordinal) || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
            {
                throw new InvalidDataException("Unsafe store path segment: " + value);
            }
        }

        // 将已绑定项目定位到其唯一的平台目录。
        private string ProjectRoot(string project) => Path.Combine(RootPath, project, _mounts[project].Platform);
        /// <summary>以项目及规范摘要定位正式版本，并检查剩余路径长度预算。</summary>
        internal string VersionPath(string project, string digest)
        {
            ResourceCatalogCodec.Hex(digest, 64);
            var path = Path.Combine(ProjectRoot(project), "versions", digest);
            if (path.Length > 210)
            {
                throw new PathTooLongException("ArtifactStore root leaves insufficient room for version files.");
            }

            return path;
        }
        // journal 放在稳定的首项目目录，记录整个闭包。配置不匹配时拒绝恢复，绝不部分挂载。
        private string CoordinatorRoot => ProjectRoot(Projects.OrderBy(x => x.ProjectId, StringComparer.Ordinal).First().ProjectId);
        private string JournalPath => Path.Combine(CoordinatorRoot, JournalFile);
        private string PreparedRoot => Path.Combine(CoordinatorRoot, "prepared");
        // 同一完整转换闭包共享准备记录槽，身份不依赖调用方持有的句柄实例。
        private string PreparedPath(JArray transition) => Path.Combine(PreparedRoot, ResourceCatalogCodec.Digest(ResourceCatalogCodec.CanonicalBytes(transition)) + ".json");
        private string OwnerPath => Path.Combine(RootPath, OwnerFile);
        // 同时识别文件和目录占位，避免将错误类型的既有条目当作缺失。
        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        // 读取有界元数据对象，同时执行操作取消和链接检查。
        private JObject Read(string path)
        {
            _operationCancellation.ThrowIfCancellationRequested();
            ResourceLocalSnapshot.RejectLinks(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024)
            {
                throw new InvalidDataException("Store metadata exceeds its bound.");
            }

            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            return ResourceCatalogCodec.ParseObject(buffer.ToArray());
        }

        // 先持久写入独立 next，再在归属及可选保护条件复核后替换单个控制文件。
        private void Write(string path, JObject data, Action beforeReplace = null)
        {
            if (!_isCreating)
            {
                RequireOwner();
            }

            ResourceLocalSnapshot.RejectLinks(path);
            var next = path + ".next";
            if (Exists(next))
            {
                throw new InvalidDataException("Unrecovered store replacement file: " + next);
            }

            var bytes = ResourceCatalogCodec.CanonicalBytes(data);
            using (var output = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            Checkpoint?.Invoke("BeforeReplace:" + Path.GetFileName(path));
            if (!_isCreating)
            {
                RequireOwner();
            }

            beforeReplace?.Invoke();
            if (File.Exists(path))
            {
                File.Replace(next, path, null);
            }
            else
            {
                File.Move(next, path);
            }
        }

        // 构造绑定根路径、项目闭包及固定控制槽的归属描述。
        private JObject Owner() => new()
        {
            ["Version"] = 1,
            ["Kind"] = "YLFramework.Resource.ArtifactStore",
            ["Root"] = RootPath,
            ["OwnerId"] = _ownerId,
            ["Projects"] = new JArray(Projects.OrderBy(x => x.ProjectId, StringComparer.Ordinal).Select(x => new JObject { ["ProjectId"] = x.ProjectId, ["Platform"] = x.Platform })),
            // 这些固定控制槽明确属于此 owner；未提交的截断 next 也不会被当成 foreign 文件或有效决策。
            ["ControlFiles"] = new JArray(ControlFiles().SelectMany(x => new[] { x, x + ".next" }).Select(x => x.Substring(RootPath.Length + 1).Replace('\\', '/')))
        };

        // 枚举根归属记录预先声明的指针镜像与 journal 控制槽。
        private IEnumerable<string> ControlFiles() => Projects.OrderBy(x => x.ProjectId, StringComparer.Ordinal)
            .SelectMany(x => new[] { "active.json", "last-known-good.json" }.Select(name => Path.Combine(ProjectRoot(x.ProjectId), name))).Append(JournalPath);

        // 要求根及所有项目归属仍完整匹配，不认领消失或未知的 Store。
        private void RequireOwner()
        {
            if (!ValidateOwner())
            {
                throw new InvalidDataException("ArtifactStore ownership disappeared.");
            }
        }

        // 限制普通入口只能在构造此 Store 的 Unity 线程执行。
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException("ArtifactStore must be used on its Unity thread.");
            }
        }

        // 缺失根返回 false；既有根必须具有完全匹配的根和项目归属记录。
        private bool ValidateOwner()
        {
            ResourceLocalSnapshot.RejectLinks(RootPath);
            if (!Exists(RootPath))
            {
                return false;
            }

            if (!Directory.Exists(RootPath) || !File.Exists(OwnerPath))
            {
                throw new InvalidDataException("Unknown ArtifactStore root; refusing to adopt it.");
            }

            var data = Read(OwnerPath);
            var id = ResourceCatalogCodec.Text(data, "OwnerId");
            ResourceCatalogCodec.Hex(id, 32);
            if (_ownerId != null && _ownerId != id)
            {
                throw new InvalidDataException("ArtifactStore owner changed during this session.");
            }

            _ownerId = id;
            if (!JToken.DeepEquals(data, Owner()))
            {
                throw new InvalidDataException("ArtifactStore ownership or project closure mismatch.");
            }

            foreach (var mount in Projects)
            {
                var root = ProjectRoot(mount.ProjectId);
                ResourceLocalSnapshot.RejectLinks(root);
                if (!Directory.Exists(root) || !JToken.DeepEquals(Read(Path.Combine(root, OwnerFile)), ProjectOwner(mount.ProjectId)))
                {
                    throw new InvalidDataException("Project store ownership mismatch.");
                }
            }
            return true;
        }

        // 生成单个项目平台目录的归属身份，不包含版本选择。
        private JObject ProjectOwner(string project) => new() { ["Version"] = 1, ["OwnerId"] = _ownerId, ["ProjectId"] = project, ["Platform"] = _mounts[project].Platform };

        // 取得本实例操作责任；写路径另持有文件锁，显式只读检查不创建持久锁文件。
        private IDisposable Enter(bool create, CancellationToken cancellation = default, bool contentIO = false, bool readOnly = false)
        {
            if (!contentIO)
            {
                CheckThread();
            }

            if (Interlocked.CompareExchange(ref _operationState, 1, 0) != 0)
            {
                throw new ResourceException(ResourceError.Busy, "ArtifactStore is already operating.");
            }

            _operationCancellation = cancellation;
            Interlocked.Increment(ref _activitySerial);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!ValidateOwner())
                {
                    if (!create)
                    {
                        return new StoreLock(this, null);
                    }

                    _ownerId = Guid.NewGuid().ToString("N");
                    _isCreating = true;
                    try
                    {
                        Directory.CreateDirectory(RootPath);
                        foreach (var mount in Projects)
                        {
                            var root = ProjectRoot(mount.ProjectId);
                            Directory.CreateDirectory(root);
                            Write(Path.Combine(root, OwnerFile), ProjectOwner(mount.ProjectId));
                            foreach (var folder in new[] { "versions", "staging", "parts" })
                            {
                                Directory.CreateDirectory(Path.Combine(root, folder));
                            }
                        }
                        Directory.CreateDirectory(PreparedRoot);
                        Write(OwnerPath, Owner());
                    }
                    finally
                    {
                        _isCreating = false;
                    }
                }
                // Check只取得进程内读责任；缺失的持久lock也不能由只读查询创建。
                if (readOnly)
                {
                    return new StoreLock(this, null);
                }

                var lockPath = Path.Combine(RootPath, ".ylr-store.lock");
                ResourceLocalSnapshot.RejectLinks(lockPath);
                return new StoreLock(this, new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch
            {
                _operationCancellation = default;
                Volatile.Write(ref _operationState, 0);
                Interlocked.Increment(ref _activitySerial);
                throw;
            }
        }

        /// <summary>持有一次 Store 操作的进程内门禁及可选文件锁，释放时更新观测代次。</summary>
        private sealed class StoreLock : IDisposable
        {
            private readonly ArtifactStore _owner;
            private readonly FileStream _stream;
            /// <summary>接管当前操作责任及已取得的可选文件锁。</summary>
            internal StoreLock(ArtifactStore owner, FileStream stream)
            {
                this._owner = owner;
                this._stream = stream;
            }
            /// <summary>关闭文件锁，并在收尾中清除该操作的取消状态与进程内门禁。</summary>
            public void Dispose()
            {
                try
                {
                    _stream?.Dispose();
                }
                finally
                {
                    _owner._operationCancellation = default;
                    Volatile.Write(ref _owner._operationState, 0);
                    Interlocked.Increment(ref _activitySerial);
                }
            }
        }

        // 验证正式版本归属后按需复用已验证快照，refresh 要求重新打开内容。
        private ResourceLocalSnapshot Open(string project, string digest, bool refresh = false, CancellationToken cancellation = default)
        {
            var path = VersionPath(project, digest);
            var marker = Read(Path.Combine(path, VersionFile));
            ValidateVersionMarker(marker, project, digest);
            if (!refresh && _verified.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var mount = _mounts[project];
            var snapshot = ResourceLocalSnapshot.OpenOwned(path, project, digest, mount.Platform, mount.AppContentCompatibilityId,
                _unityCompatibility, cancellation.CanBeCanceled ? cancellation : _operationCancellation);
            _verified[path] = snapshot;
            return snapshot;
        }

        // 校验 Complete 版本标记的归属、项目平台、摘要与登记文件名集合。
        private void ValidateVersionMarker(JObject marker, string project, string digest)
        {
            ResourceCatalogCodec.Fields(marker, "Version,OwnerId,ProjectId,Platform,CatalogDigest,State,Files");
            if (ResourceCatalogCodec.Integer(marker, "Version") != 1 || ResourceCatalogCodec.Text(marker, "OwnerId") != _ownerId ||
                ResourceCatalogCodec.Text(marker, "ProjectId") != project || ResourceCatalogCodec.Text(marker, "Platform") != _mounts[project].Platform ||
                ResourceCatalogCodec.Text(marker, "CatalogDigest") != digest || ResourceCatalogCodec.Text(marker, "State") != "Complete")
            {
                throw new InvalidDataException("Version Complete ownership/identity mismatch.");
            }

            RegisteredFiles(marker);
        }

        // 读取闭合文件名白名单，拒绝重复项、缺失基础文件及不安全路径成分。
        private static string[] RegisteredFiles(JObject marker)
        {
            var files = ReadArray(marker, "Files").Select(x => x.Type == JTokenType.String ? (string)x : throw new InvalidDataException("Expected registered filename.")).ToArray();
            if (files.Length < 2 || files.Length != files.Distinct(StringComparer.OrdinalIgnoreCase).Count() ||
                !files.Contains("resource-catalog.json") || !files.Contains(".ylr-complete.json"))
            {
                throw new InvalidDataException("Invalid owned file allowlist.");
            }

            foreach (var name in files)
            {
                if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    name.IndexOfAny(new[] { '/', '\\', ':', '%', '?' }) >= 0 || name.EndsWith(".", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Unsafe registered filename.");
                }
            }

            return files;
        }

        // 要求元数据指定字段确为数组，避免将缺失或其他类型解释为空集合。
        private static JArray ReadArray(JObject data, string field) => data[field] as JArray ?? throw new InvalidDataException("Expected array: " + field);

        // 从快照文档生成 Store 自有的 Complete 版本描述。
        private JObject VersionMarker(ResourceLocalSnapshot snapshot) => VersionMarker(snapshot.Document);
        // 将文档的交付文件和基础元数据登记为该版本的已知文件集合。
        private JObject VersionMarker(ResourceCatalogDocument document) => new()
        {
            ["Version"] = 1,
            ["OwnerId"] = _ownerId ?? new string('0', 32),
            ["ProjectId"] = document.ProjectId,
            ["Platform"] = document.Platform,
            ["CatalogDigest"] = document.CatalogDigest,
            ["State"] = "Complete",
            ["Files"] = new JArray(Files(document).Select(x => x.RelativeFile).Concat(new[] { "resource-catalog.json", ".ylr-complete.json" }))
        };

        // 核对显式兼容闭包后重开所选快照，Store 内版本还须验证 owned 标记。
        private ResourceLocalSnapshot Reopen(ResourceLocalSnapshot snapshot, CancellationToken cancellation = default)
        {
            var doc = snapshot.Document;
            if (!_mounts.TryGetValue(doc.ProjectId, out var mount) || doc.Platform != mount.Platform || doc.AppContentCompatibilityId != mount.AppContentCompatibilityId ||
                doc.UnityCompatibility != _unityCompatibility)
            {
                throw new InvalidDataException("The selected snapshot is outside the explicit compatible project closure.");
            }

            if (snapshot.DirectoryPath == VersionPath(doc.ProjectId, doc.CatalogDigest))
            {
                return Open(doc.ProjectId, doc.CatalogDigest, true, cancellation);
            }

            return snapshot.Reopen(cancellation.CanBeCanceled ? cancellation : _operationCancellation);
        }

        // 构造完整闭包索引，并携带当前可读的最高已确认发布历史。
        private JObject Index(IEnumerable<ResourceLocalSnapshot> snapshots) => WithFloors(new JObject
        {
            ["Version"] = 1,
            ["OwnerId"] = _ownerId,
            ["Projects"] = new JArray(snapshots.OrderBy(x => x.Document.ProjectId, StringComparer.Ordinal).Select(x => new JObject
            {
                ["ProjectId"] = x.Document.ProjectId,
                ["Platform"] = x.Document.Platform,
                ["CatalogDigest"] = x.Document.CatalogDigest
            }))
        }, ReadReleaseFloors());

        // 验证索引归属、历史门槛和完整项目平台集合，此处不验证版本文件内容。
        private JObject ValidateIndex(JObject index)
        {
            var version = ResourceCatalogCodec.Integer(index, "Version");
            ResourceCatalogCodec.Fields(index, version == 2 ? "Version,OwnerId,Projects,ReleaseFloors" : "Version,OwnerId,Projects");
            if (version != 1 && version != 2 || ResourceCatalogCodec.Text(index, "OwnerId") != _ownerId)
            {
                throw new InvalidDataException("Activation index ownership mismatch.");
            }

            if (version == 2)
            {
                ValidateFloors(index["ReleaseFloors"]);
            }

            var projects = ReadArray(index, "Projects");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in projects)
            {
                if (token is not JObject item)
                {
                    throw new InvalidDataException("Invalid activation project.");
                }

                ResourceCatalogCodec.Fields(item, "ProjectId,Platform,CatalogDigest");
                var project = ResourceCatalogCodec.Text(item, "ProjectId");
                if (!_mounts.TryGetValue(project, out var mount) || !seen.Add(project) || ResourceCatalogCodec.Text(item, "Platform") != mount.Platform)
                {
                    throw new InvalidDataException("Activation index project/platform closure mismatch.");
                }

                ResourceCatalogCodec.Hex(ResourceCatalogCodec.Text(item, "CatalogDigest"), 64);
            }
            if (seen.Count != _mounts.Count)
            {
                throw new InvalidDataException("Incomplete activation closure.");
            }

            return index;
        }

        // 打开已校验索引引用的整个版本闭包，不独立回退单个项目。
        private ResourceLocalSnapshot[] OpenIndex(JObject index, bool refresh = true) => ReadArray(ValidateIndex(index), "Projects")
            .Cast<JObject>().Select(x => Open((string)x["ProjectId"], (string)x["CatalogDigest"], refresh)).ToArray();

        // 要求各项目镜像一致且全部存在或全部缺失，并可记录每个读取路径。
        private JObject ReadIndex(string name, Action<string> observe = null)
        {
            JObject selected = null;
            var missing = 0;
            foreach (var project in Projects)
            {
                var path = Path.Combine(ProjectRoot(project.ProjectId), name);
                observe?.Invoke(path);
                if (!Exists(path))
                {
                    missing++;
                    continue;
                }
                var index = ValidateIndex(Read(path));
                if (selected != null && !JToken.DeepEquals(selected, index))
                {
                    throw new InvalidDataException("Activation index mirrors disagree on the complete closure.");
                }

                selected = index;
            }
            if (missing != 0 && missing != _mounts.Count)
            {
                throw new InvalidDataException("Activation index closure has missing mirrors.");
            }

            return selected;
        }

        // 尝试完整打开一种索引，文件类错误记录为恢复说明并允许改选其他完整闭包。
        private JObject ValidIndex(string name, bool refresh = true)
        {
            try
            {
                var index = ReadIndex(name);
                if (index != null)
                {
                    OpenIndex(index, refresh);
                }
                return index;
            }
            catch (Exception error) when (IsFileFailure(error))
            {
                _recoveryNotes[name] = name + ": " + error.Message;
                return null;
            }
        }

        // 按 active、LKG、显式内置集合的顺序选择完整兼容闭包。
        private ResourceLocalSnapshot[] Select(bool refresh = true)
        {
            foreach (var name in new[] { "active.json", "last-known-good.json" })
            {
                var index = ValidIndex(name, refresh);
                if (index != null)
                {
                    return OpenIndex(index, false);
                }
            }
            if (Projects.Any(x => x.BuiltIn == null))
            {
                throw new InvalidDataException("No complete compatible active, last-known-good or explicitly bound built-in closure.");
            }

            return Projects.OrderBy(x => x.ProjectId, StringComparer.Ordinal).Select(x => refresh ? Reopen(x.BuiltIn) : x.BuiltIn).ToArray();
        }

        /// <summary>在 Unity 线程执行恢复并选出完整的持久或内置版本闭包。</summary>
        internal IReadOnlyList<ResourceLocalSnapshot> Mount()
        {
            CheckThread();
            return MountForContent(CancellationToken.None);
        }

        /// <summary>在内容 IO 操作责任内恢复并选择闭包，必要时修复 active 镜像。</summary>
        internal IReadOnlyList<ResourceLocalSnapshot> MountForContent(CancellationToken cancellation)
        {
            using var guard = Enter(false, cancellation, true);
            _recoveryNotes.Clear();
            if (Directory.Exists(RootPath))
            {
                RecoverLocked();
            }

            var selected = Select();
            if (Directory.Exists(RootPath) && selected.All(x => x.DirectoryPath == VersionPath(x.Document.ProjectId, x.Document.CatalogDigest)))
            {
                var active = ValidIndex("active.json");
                var repair = Index(selected);
                if (!JToken.DeepEquals(active, repair))
                {
                    foreach (var project in Projects)
                    {
                        RestorePointer(project.ProjectId, "active.json", repair);
                    }
                }
            }
            return selected;
        }

        /// <summary>在 Unity 线程检查本地完整目标闭包，不创建 Store 或执行恢复写入。</summary>
        internal UpdatePlan Check(IEnumerable<ResourceLocalSnapshot> current, IEnumerable<ResourceLocalSnapshot> targetClosure)
        {
            CheckThread();
            if (IsOperating)
            {
                throw new ResourceException(ResourceError.Busy, "ArtifactStore is already operating.");
            }

            return CheckCore(current, targetClosure, CancellationToken.None);
        }

        /// <summary>在只读内容 IO 门禁内生成本地检查计划。</summary>
        internal UpdatePlan CheckForContent(IEnumerable<ResourceLocalSnapshot> current, IEnumerable<ResourceLocalSnapshot> targetClosure, CancellationToken cancellation)
        {
            using var guard = Enter(false, cancellation, true, true);
            return CheckCore(current, targetClosure, cancellation);
        }

        /// <summary>在检查结果交回主线程时复核 Store、操作状态和转换闭包身份。</summary>
        internal void RequireCheckedPlan(UpdatePlan plan)
        {
            CheckThread();
            if (plan == null || plan.Root != RootPath)
            {
                throw new ArgumentException("The checked plan belongs to another store.");
            }

            if (IsOperating)
            {
                throw new ResourceException(ResourceError.Busy, "ArtifactStore changed to an active operation before check handoff.");
            }

            ValidateOwner();
            RequireNoJournal();
            ReadTransition(Transition(plan.Projects));
        }

        // 复验目标及可复用输入，计算完整闭包的安装与控制记录空间预算。
        private UpdatePlan CheckCore(IEnumerable<ResourceLocalSnapshot> current, IEnumerable<ResourceLocalSnapshot> targetClosure, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateOwner(); // Check 不创建 root/lock/staging，也不执行恢复写入。
            if (File.Exists(JournalPath))
            {
                throw new ResourceException(ResourceError.Busy, "Recover the pending commit before checking another update.");
            }

            var from = Closure(current);
            var targets = Closure(targetClosure);
            var entries = new List<UpdateProject>();
            long required = 0;
            var budgeted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in _mounts.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                var target = Reopen(targets[project], cancellation);
                var old = from[project];
                var files = new List<UpdateFile>();
                if (target.Document.CatalogDigest != old.Document.CatalogDigest && !Exists(VersionPath(project, target.Document.CatalogDigest)))
                {
                    foreach (var file in Files(target.Document))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var reusable = Files(old.Document).Any(x => x.RelativeFile == file.RelativeFile && x.Length == file.Length && x.Sha256 == file.Sha256);
                        if (reusable)
                        {
                            using (old.OpenVerified(file, cancellation))
                            {
                            }
                        }

                        files.Add(new UpdateFile(file.RelativeFile, file.Length, reusable, Path.Combine(reusable ? old.DirectoryPath : target.DirectoryPath, file.RelativeFile)));
                    }
                }
                else if (Exists(VersionPath(project, target.Document.CatalogDigest)))
                {
                    Open(project, target.Document.CatalogDigest, true, cancellation);
                }

                foreach (var snapshot in new[] { old, target })
                {
                    if (!Exists(VersionPath(project, snapshot.Document.CatalogDigest)) && budgeted.Add(project + ":" + snapshot.Document.CatalogDigest))
                    {
                        required = checked(required + Files(snapshot.Document).Sum(x => x.Length) +
                            new FileInfo(Path.Combine(snapshot.DirectoryPath, "resource-catalog.json")).Length +
                            new FileInfo(Path.Combine(snapshot.DirectoryPath, ".ylr-complete.json")).Length +
                            ResourceCatalogCodec.CanonicalBytes(VersionMarker(snapshot)).Length * 2L + 512);
                    }
                }

                entries.Add(new UpdateProject(old, target, files));
            }
            // 保守预算还包含整组镜像、journal/next 和归属记录；不把控制文件开销藏在每版本固定 16 KiB 中。
            var fromBytes = ResourceCatalogCodec.CanonicalBytes(Index(entries.Select(x => x.From))).Length + 32L;
            var toBytes = ResourceCatalogCodec.CanonicalBytes(Index(entries.Select(x => x.Target))).Length + 32L;
            required = checked(required + (fromBytes + toBytes) * _mounts.Count + Math.Max(fromBytes, toBytes) +
                ResourceCatalogCodec.CanonicalBytes(Transition(entries)).Length * 4L + ResourceCatalogCodec.CanonicalBytes(Owner()).Length +
                Projects.Sum(x => ResourceCatalogCodec.CanonicalBytes(ProjectOwner(x.ProjectId)).Length + 32L) + 4096);
            var available = AvailableSpace();
            ValidateOwner();
            RequireNoJournal();
            cancellation.ThrowIfCancellationRequested();
            return new UpdatePlan(RootPath, entries, available, required);
        }

        // 将显式快照集合绑定到全部挂载项目，拒绝未知、重复或遗漏项目。
        private Dictionary<string, ResourceLocalSnapshot> Closure(IEnumerable<ResourceLocalSnapshot> snapshots)
        {
            var result = new Dictionary<string, ResourceLocalSnapshot>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots ?? throw new ArgumentNullException(nameof(snapshots)))
            {
                if (snapshot == null || !_mounts.ContainsKey(snapshot.Document.ProjectId) || result.ContainsKey(snapshot.Document.ProjectId))
                {
                    throw new InvalidDataException("Unknown or duplicate project in the explicit version closure.");
                }

                result.Add(snapshot.Document.ProjectId, snapshot);
            }
            if (result.Count != _mounts.Count)
            {
                throw new InvalidDataException("Every mounted store project must have an explicit target CatalogDigest.");
            }

            return result;
        }

        // 枚举 Catalog 中的 Bundle 和支持文件，不额外包含 Catalog 或 Complete 元数据。
        private static IEnumerable<ResourceCatalogFile> Files(ResourceCatalogDocument document) => document.Bundles.Cast<ResourceCatalogFile>().Concat(document.SupportFiles);

        /// <summary>同步完成本地安装、内容验证和完整闭包准备记录发布，不切换 active。</summary>
        internal PreparedUpdate Prepare(UpdatePlan plan, CancellationToken cancellation, ResourceDecryptionOptions decryption = null)
        {
            InstallPreparation(plan, cancellation);
            using var receipt = CapturePreparation(plan, decryption);
            receipt.Validate(cancellation);
            return CompletePreparation(plan, receipt, cancellation);
        }

        /// <summary>在 Unity 线程安装本地计划的来源与目标版本，支持调用方存活复核。</summary>
        internal void InstallPreparation(UpdatePlan plan, CancellationToken cancellation, Action ensureAlive = null)
            => InstallPreparationCore(plan, cancellation, ensureAlive, false);

        /// <summary>在内容 IO 工作中安装本地计划，不直接发布准备记录。</summary>
        internal void InstallPreparationForContent(UpdatePlan plan, CancellationToken cancellation)
            => InstallPreparationCore(plan, cancellation, null, true);

        // 在写入责任内逐项目安装完整来源和目标；中断不撤销此前已安装的独立版本。
        private void InstallPreparationCore(UpdatePlan plan, CancellationToken cancellation, Action ensureAlive, bool contentIO)
        {
            if (plan == null || plan.Root != RootPath)
            {
                throw new ArgumentException("The plan belongs to another store.");
            }

            if (plan.IsRemote)
            {
                throw new ResourceException(ResourceError.Unsupported, "Remote plans require the ResourceSystem asynchronous download path.");
            }

            cancellation.ThrowIfCancellationRequested();
            using var guard = Enter(true, cancellation, contentIO);
            RecoverMetadata();
            RequireNoJournal();
            foreach (var entry in plan.Projects)
            {
                cancellation.ThrowIfCancellationRequested();
                ensureAlive?.Invoke();
                Install(entry.From, cancellation, ensureAlive: ensureAlive);
                Install(entry.Target, cancellation, entry.Files, ensureAlive);
            }
            cancellation.ThrowIfCancellationRequested();
            ensureAlive?.Invoke();
        }

        /// <summary>捕获准备所需的完整输入，并在释放 Store 排他锁后调用内容 provider。</summary>
        internal ValidationReceipt CapturePreparation(UpdatePlan plan, ResourceDecryptionOptions decryption = null, ResourceContentBudget budget = null)
        {
            if (plan == null || plan.Root != RootPath)
            {
                throw new ArgumentException("The plan belongs to another store.");
            }

            ResourceLocalSnapshot[] targets;
            ResourceLocalSnapshot[] sources;
            using (Enter(false))
            {
                RequireOwner();
                RecoverMetadata();
                RequireNoJournal();
                ReadTransition(Transition(plan.Projects));
                targets = plan.Projects.Select(x => x.Remote == null ? Open(x.ProjectId, x.ToDigest) : OpenRemoteVersion(x.Remote, false)).ToArray();
                sources = plan.Projects.Select(x => Open(x.ProjectId, x.FromDigest)).ToArray();
            }
            // 任意应用provider只在排他锁释放后调用；收据在调用前已绑定本次事务代次。
            return ValidationReceipt.Capture(this, plan, targets, sources, decryption, budget);
        }

        /// <summary>消费匹配的验证收据，复核来源、发布时效及历史门槛后发布准备记录。</summary>
        internal PreparedUpdate CompletePreparation(UpdatePlan plan, ValidationReceipt receipt, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            receipt.Require(this, plan);
            using var guard = Enter(false);
            RequireOwner();
            RecoverMetadata();
            RequireNoJournal();
            receipt.Consume();
            RequireFrom(plan.Projects);
            foreach (var entry in plan.Projects)
            {
                entry.Remote?.RequireFresh(DateTimeOffset.UtcNow);
                if (entry.Remote != null)
                {
                    CheckReleaseFloor(entry.Remote);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return RecordPrepared(plan, receipt);
        }

        // 以完整转换身份发布或刷新准备授权，保留同一项目已有的较高发布序号。
        private PreparedUpdate RecordPrepared(UpdatePlan plan, ValidationReceipt receipt)
        {
            receipt.RequireStable();
            var transition = Transition(plan.Projects);
            var releases = plan.Projects.Where(x => x.Remote != null).Select(x => x.Remote).ToArray();
            var preparedPath = PreparedPath(transition);
            // 此时完整闭包已逐一验证，同名候选有本次显式选择作为归属依据。
            RemoveCandidate(preparedPath + ".next", x => ValidatePreparedRecord(x, preparedPath));
            JObject previous = null;
            if (File.Exists(preparedPath))
            {
                previous = Read(preparedPath);
                ValidatePreparedRecord(previous, preparedPath);
                if (!JToken.DeepEquals(ReadTransition(previous["Transition"]), transition))
                {
                    throw new InvalidDataException("Prepared closure identity changed.");
                }

                var authorizations = ReadReleaseRecords(previous).ToDictionary(x => x.ProjectId, StringComparer.Ordinal);
                foreach (var release in releases)
                {
                    if (authorizations.TryGetValue(release.ProjectId, out var existing) && release.Sequence < existing.Sequence)
                    {
                        throw new InvalidDataException("A prepared authorization cannot be silently downgraded; discard it explicitly before choosing an older authorized release.");
                    }

                    authorizations[release.ProjectId] = release;
                }
                releases = authorizations.Values.OrderBy(x => x.ProjectId, StringComparer.Ordinal).ToArray();
            }
            var record = new JObject { ["Version"] = releases.Length == 0 ? 1 : 2, ["OwnerId"] = _ownerId, ["State"] = "Complete", ["Transition"] = transition };
            if (releases.Length != 0)
            {
                record["Releases"] = ReleaseRecords(releases);
            }

            if (!JToken.DeepEquals(previous, record))
            {
                Write(preparedPath, record, () =>
            {
                receipt.RequireStable();
                RequireFrom(plan.Projects);
                if (previous != null && !JToken.DeepEquals(Read(preparedPath), previous))
                {
                    throw new InvalidDataException("Prepared authorization changed during refresh.");
                }
            });
            }

            return new PreparedUpdate(RootPath, plan.Projects.Select(x => new UpdateProject(x.From, Open(x.ProjectId, x.ToDigest), Array.Empty<UpdateFile>())), releases: releases);
        }

        // 要求当前持久选择仍等于计划来源，拒绝基于过时闭包继续准备或提交。
        private void RequireFrom(IEnumerable<UpdateProject> projects)
        {
            var current = Select(false).ToDictionary(x => x.Document.ProjectId, StringComparer.Ordinal);
            foreach (var project in projects)
            {
                if (current[project.ProjectId].Document.CatalogDigest != project.FromDigest)
                {
                    throw new ResourceException(ResourceError.StaleBase, "The persistent activation base changed: " + project.ProjectId);
                }
            }
        }

        // 这是一次事务的短期认证责任，不是第二份版本索引或可持久复用的解密标记。
        /// <summary>持有本次转换验证输入及内容责任，绑定 Store、操作实例、归属和活动代次。</summary>
        internal sealed class ValidationReceipt : IDisposable
        {
            private readonly ArtifactStore _store;
            private readonly object _operation;
            private readonly string _transition;
            private readonly string _owner;
            private readonly long _epoch;
            private readonly List<(FileStream stream, ResourceCatalogFile file, long length)> _files = new();
            private ResourceContentValidation _content;
            private bool _isValidated;
            private bool _isConsumed;
            private bool _isDisposing;
            private bool _isDisposed;
            internal IReadOnlyList<ResourceLocalSnapshot> Targets
            {
                get;
            }
            internal IReadOnlyList<ResourceLocalSnapshot> Snapshots
            {
                get;
            }

            // 捕获操作身份和去重后的快照集合，供验证及清理责任追踪。
            private ValidationReceipt(ArtifactStore store, object operation, ResourceLocalSnapshot[] targets, ResourceLocalSnapshot[] sources)
            {
                this._store = store;
                this._operation = operation;
                _owner = store._ownerId;
                _epoch = Interlocked.Read(ref _activitySerial);
                _transition = Identity(operation);
                Targets = ResourceCatalog.Copy(targets);
                Snapshots = ResourceCatalog.Copy(targets.Concat(sources).GroupBy(x => x.DirectoryPath, StringComparer.OrdinalIgnoreCase).Select(x => x.First()));
            }

            // 用规范化完整转换描述绑定计划或准备结果，检测其内部身份漂移。
            private static string Identity(object operation) => Convert.ToBase64String(ResourceCatalogCodec.CanonicalBytes(
                Transition(operation is UpdatePlan plan ? plan.Projects : ((PreparedUpdate)operation).Projects)));

            /// <summary>打开并持有验证输入；捕获失败时清理，未完成的清理责任由异常保留。</summary>
            internal static ValidationReceipt Capture(ArtifactStore store, object operation, ResourceLocalSnapshot[] targets, ResourceLocalSnapshot[] sources,
                ResourceDecryptionOptions decryption, ResourceContentBudget budget)
            {
                var receipt = new ValidationReceipt(store, operation, targets, sources);
                try
                {
                    foreach (var snapshot in receipt.Snapshots)
                    {
                        var document = snapshot.Document;
                        var target = targets.Any(x => x.DirectoryPath == snapshot.DirectoryPath);
                        var files = target ? document.SupportFiles.Where(x => x.RelativeFile != document.ManifestFile) : Files(document);
                        foreach (var file in files.Append(
                            new ResourceCatalogFile(new JObject
                            {
                                ["RelativeFile"] = "resource-catalog.json",
                                ["Length"] = ResourceCatalogCodec.Write(document).LongLength,
                                ["Sha256"] = document.CatalogDigest
                            })))
                        {
                            var stream = snapshot.OpenContentFile(file);
                            receipt._files.Add((stream, file, stream.Length));
                        }
                        foreach (var name in new[] { ".ylr-complete.json", VersionFile })
                        {
                            var path = Path.Combine(snapshot.DirectoryPath, name);
                            ResourceLocalSnapshot.RejectLinks(path);
                            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            receipt._files.Add((stream, null, stream.Length));
                        }
                        receipt.RequireMetadata(snapshot);
                    }
                    receipt._content = ResourceContentValidation.Capture(targets, decryption, budget: budget);
                    return receipt;
                }
                catch (Exception error)
                {
                    if (error is ResourceException { InnerException: ResourceContentCleanupException { Owner: ResourceContentValidation retained } } resource)
                    {
                        receipt._content = retained;
                        throw new ResourceException(resource.Kind, "Update content capture failed; the unfinished validation and its protected snapshots remain owned.",
                            new ResourceContentCleanupException(receipt, error));
                    }
                    try
                    {
                        receipt.Dispose();
                    }
                    catch (Exception cleanup)
                    {
                        throw new ResourceException(error is ResourceException failure ? failure.Kind : ResourceError.UpdateFailed,
                            "Update validation capture and cleanup failed; ownership remains in the cause.",
                            new ResourceContentCleanupException(receipt, new AggregateException(error, cleanup)));
                    }
                    throw;
                }
            }

            /// <summary>验证所持文件及目标内容，全部完成后才将收据标记为可消费。</summary>
            internal void Validate(CancellationToken cancellation = default)
            {
                if (_isDisposing)
                {
                    throw new ObjectDisposedException(nameof(ValidationReceipt));
                }

                cancellation.ThrowIfCancellationRequested();
                _store.Checkpoint?.Invoke("ReceiptShaStarted");
                try
                {
                    foreach (var file in _files)
                    {
                        if (file.file != null)
                        {
                            ResourceLocalSnapshot.VerifyContentFile(file.stream, file.file, cancellation);
                        }
                    }
                }
                finally
                {
                    _store.Checkpoint?.Invoke("ReceiptShaFinished");
                }
                _content.Validate(cancellation, _store.Checkpoint);
                cancellation.ThrowIfCancellationRequested();
                _isValidated = true;
            }

            /// <summary>要求收据已完成、未消费且匹配操作与活动代次，并复核输入稳定性。</summary>
            internal void Require(ArtifactStore expectedStore, object expectedOperation)
            {
                _store.CheckThread();
                if (!ReferenceEquals(_store, expectedStore) || !ReferenceEquals(_operation, expectedOperation) || _transition != Identity(expectedOperation) ||
                    _owner != _store._ownerId || _isDisposing || _isConsumed || !_isValidated)
                {
                    throw new InvalidOperationException("A completed, matching, unused update content receipt is required.");
                }

                if (_epoch != Interlocked.Read(ref _activitySerial))
                {
                    throw new ResourceException(ResourceError.StaleBase, "The store changed during content validation; explicitly retry the operation.");
                }

                RequireStable();
            }

            /// <summary>在稳定性复核后标记收据已消费，阻止其再次用于另一轮交接。</summary>
            internal void Consume()
            {
                RequireStable();
                _isConsumed = true;
            }

            /// <summary>复核内容责任、所持文件长度和版本元数据，不声称重新计算全部文件摘要。</summary>
            internal void RequireStable()
            {
                if (_isDisposing || !_isValidated)
                {
                    throw new InvalidOperationException("Update content validation has not completed or was released.");
                }

                _content.RequireStable();
                foreach (var file in _files)
                {
                    if (!file.stream.CanRead || file.stream.Length != file.length)
                    {
                        throw new InvalidDataException("A validated update file changed before commit.");
                    }
                }

                foreach (var snapshot in Snapshots)
                {
                    RequireMetadata(snapshot);
                }
            }

            // 核对正式目录、Complete 身份及精确文件集合，拒绝验证期间的归属漂移。
            private void RequireMetadata(ResourceLocalSnapshot snapshot)
            {
                var document = snapshot.Document;
                if (snapshot.DirectoryPath != _store.VersionPath(document.ProjectId, document.CatalogDigest) ||
                    !JToken.DeepEquals(_store.Read(Path.Combine(snapshot.DirectoryPath, VersionFile)), _store.VersionMarker(document)))
                {
                    throw new InvalidDataException("The validated update version ownership changed.");
                }

                ResourceLocalSnapshot.ValidateComplete(_store.Read(Path.Combine(snapshot.DirectoryPath, ".ylr-complete.json")),
                    document.ProjectId, document.Platform, document.CatalogDigest, document.BuildId);
                var names = Files(document).Select(x => x.RelativeFile).Concat(new[] { "resource-catalog.json", ".ylr-complete.json", VersionFile });
                KnownDirectoryFiles(snapshot.DirectoryPath, new HashSet<string>(names, StringComparer.Ordinal)).ToArray();
                if (Directory.GetFiles(snapshot.DirectoryPath).Length != names.Count())
                {
                    throw new InvalidDataException("The validated update file closure changed.");
                }
            }

            /// <summary>释放内容及文件输入；失败项仍由本收据持有，允许后续继续清理。</summary>
            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposing = true;
                var failures = new List<Exception>();
                if (_content != null)
                {
                    try
                    {
                        _content.Dispose();
                        _content = null;
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }

                for (var i = _files.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _files[i].stream.Dispose();
                        _files.RemoveAt(i);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }

                if (failures.Count != 0)
                {
                    throw new ResourceContentCleanupException(this, new AggregateException("Update content cleanup failed; unresolved inputs remain owned.", failures));
                }

                _isDisposed = true;
            }
        }

        // 复制只读发布输入到独立 staging，完整验证后提升为正式版本；不修改发布源。
        private ResourceLocalSnapshot Install(ResourceLocalSnapshot source, CancellationToken cancellation, IReadOnlyList<UpdateFile> reuse = null, Action ensureAlive = null)
        {
            ensureAlive?.Invoke();
            var project = source.Document.ProjectId;
            var digest = source.Document.CatalogDigest;
            var target = VersionPath(project, digest);
            if (Exists(target))
            {
                return Open(project, digest, cancellation: cancellation);
            }
            source = Reopen(source, cancellation);
            var stage = Path.Combine(ProjectRoot(project), "staging", Guid.NewGuid().ToString("N"));
            ResourceLocalSnapshot.RejectLinks(stage);
            Directory.CreateDirectory(stage);
            var registered = Files(source.Document).Select(x => x.RelativeFile).Concat(new[] { "resource-catalog.json", ".ylr-complete.json" }).ToArray();
            var stageMarker = new JObject
            {
                ["Version"] = 1,
                ["OwnerId"] = _ownerId,
                ["ProjectId"] = project,
                ["Platform"] = _mounts[project].Platform,
                ["CatalogDigest"] = digest,
                ["Attempt"] = Path.GetFileName(stage),
                ["Files"] = new JArray(registered)
            };
            Write(Path.Combine(stage, ".ylr-stage.json"), stageMarker);
            foreach (var name in registered)
            {
                cancellation.ThrowIfCancellationRequested();
                ensureAlive?.Invoke();
                var inputPath = reuse?.FirstOrDefault(x => x.RelativeFile == name)?.Input ?? Path.Combine(source.DirectoryPath, name);
                ResourceLocalSnapshot.RejectLinks(inputPath);
                using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(Path.Combine(stage, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[64 * 1024];
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ensureAlive?.Invoke();
                        output.Write(buffer, 0, count);
                    }
                    output.Flush(true);
                }
                Checkpoint?.Invoke("FileCopied");
                ensureAlive?.Invoke();
            }
            // 先验证普通发布文件，最后写 owned Complete。取消/失败留下的 staging 从不参与挂载。
            var mount = _mounts[project];
            ResourceLocalSnapshot.OpenStaging(stage, project, digest, mount.Platform, mount.AppContentCompatibilityId, _unityCompatibility, cancellation);
            cancellation.ThrowIfCancellationRequested();
            ensureAlive?.Invoke();
            return PromoteStage(stage, stageMarker, source.Document, ensureAlive, cancellation);
        }

        // 写入 owned Complete 后复核 staging 归属并移动目录；此步骤不发布准备记录或激活指针。
        private ResourceLocalSnapshot PromoteStage(string stage, JObject stageMarker, ResourceCatalogDocument document, Action ensureAlive = null,
            CancellationToken cancellation = default)
        {
            ensureAlive?.Invoke();
            var project = document.ProjectId;
            var digest = document.CatalogDigest;
            RequireOwner();
            var versionMarker = VersionMarker(document);
            Write(Path.Combine(stage, VersionFile), versionMarker);
            Checkpoint?.Invoke("CompleteWritten");
            ensureAlive?.Invoke();
            RequireOwner();
            if (!JToken.DeepEquals(Read(Path.Combine(stage, ".ylr-stage.json")), stageMarker) || !JToken.DeepEquals(Read(Path.Combine(stage, VersionFile)), versionMarker))
            {
                throw new InvalidDataException("Staging ownership changed before promotion.");
            }

            File.Delete(Path.Combine(stage, ".ylr-stage.json"));
            Directory.Move(stage, VersionPath(project, digest));
            Checkpoint?.Invoke("VersionInstalled");
            ensureAlive?.Invoke();
            return Open(project, digest, cancellation: cancellation);
        }

        // 按项目顺序生成完整来源到目标转换的稳定元数据身份。
        private static JArray Transition(IEnumerable<UpdateProject> projects) => new(projects.OrderBy(x => x.ProjectId, StringComparer.Ordinal)
            .Select(x => new JObject { ["ProjectId"] = x.ProjectId, ["Platform"] = x.Platform, ["From"] = x.FromDigest, ["To"] = x.ToDigest }));

        // 验证转换覆盖全部挂载项目且各平台及来源、目标摘要格式有效。
        private JArray ReadTransition(JToken token)
        {
            if (token is not JArray array || array.Count != _mounts.Count)
            {
                throw new InvalidDataException("Invalid version transaction closure.");
            }

            var projects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                if (item is not JObject entry)
                {
                    throw new InvalidDataException("Expected transaction project.");
                }

                ResourceCatalogCodec.Fields(entry, "ProjectId,Platform,From,To");
                var project = ResourceCatalogCodec.Text(entry, "ProjectId");
                if (!_mounts.TryGetValue(project, out var mount) || !projects.Add(project) || ResourceCatalogCodec.Text(entry, "Platform") != mount.Platform)
                {
                    throw new InvalidDataException("Transaction project/platform closure mismatch.");
                }

                ResourceCatalogCodec.Hex(ResourceCatalogCodec.Text(entry, "From"), 64);
                ResourceCatalogCodec.Hex(ResourceCatalogCodec.Text(entry, "To"), 64);
            }
            return array;
        }

        /// <summary>重开持久准备记录引用的完整版本，返回可再次提交检查的准备身份。</summary>
        /// <remarks>此入口可能清理已归属的 next 元数据；存在待恢复 journal 时拒绝枚举，不是纯只读查询。</remarks>
        public IReadOnlyList<PreparedUpdate> GetPreparedUpdates()
        {
            using var guard = Enter(false);
            if (!ValidateOwner())
            {
                return Array.Empty<PreparedUpdate>();
            }

            RecoverMetadata();
            RequireNoJournal();
            return PreparedRecords().Select(x => RecoverPrepared(ReadTransition(x["Transition"]), ReadReleaseRecords(x))).ToArray();
        }

        // 枚举并校验正式准备记录，未知 next 不视为已完成准备。
        private IEnumerable<JObject> PreparedRecords()
        {
            ResourceLocalSnapshot.RejectLinks(PreparedRoot);
            foreach (var path in Directory.EnumerateFiles(PreparedRoot, "*.json"))
            {
                yield return ReadPreparedRecord(path);
            }
        }

        // 读取准备记录并核对其归属、状态及文件名绑定的转换身份。
        private JObject ReadPreparedRecord(string path)
        {
            var record = Read(path);
            ValidatePreparedRecord(record, path);
            return record;
        }

        // 从已验证准备记录重开完整来源和目标版本，重建进程内准备句柄。
        private PreparedUpdate RecoverPrepared(JArray transition, ResourceRemoteRelease[] releases)
        {
            var projects = new List<UpdateProject>();
            foreach (JObject item in transition)
            {
                var project = (string)item["ProjectId"];
                var from = (string)item["From"];
                var to = (string)item["To"];
                var target = Open(project, to, true);
                projects.Add(new UpdateProject(Open(project, from), target, Array.Empty<UpdateFile>()));
            }
            return new PreparedUpdate(RootPath, projects, releases: releases);
        }

        /// <summary>识别可按文件或元数据失败处理的异常类别，不包含任意运行时错误。</summary>
        internal static bool IsFileFailure(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException;
    }
}
