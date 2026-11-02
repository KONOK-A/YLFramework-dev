using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>确切本地发布的身份与操作统计；不持有目录锁，使用时仍需重新验证文件。</summary>
    public sealed class ResourceRelease
    {
        /// <summary>项目和平台对应的自有发布根目录。</summary>
        public string RootPath
        {
            get;
        }
        /// <summary>以 CatalogDigest 命名的确切版本目录，不是当前版本指针。</summary>
        public string DirectoryPath
        {
            get;
        }
        /// <summary>该发布已验证的正式 Catalog 文档。</summary>
        public ResourceCatalogDocument Catalog
        {
            get;
        }
        /// <summary>本次发布是否命中相同当前版本而未再切换 Index；只读获取不记录此状态。</summary>
        public bool IsRepeat
        {
            get;
        }
        /// <summary>本次新暂存的交付文件字节数，不包含 Catalog 和完成记录；只读获取时为零。</summary>
        public long CopiedBytes
        {
            get;
        }
        /// <summary>本次发布记录的总耗时，单位毫秒；只读获取时没有该次测量。</summary>
        public double ElapsedMilliseconds
        {
            get; internal set;
        }
        /// <summary>本次向暂存目录复制交付文件的耗时，单位毫秒。</summary>
        public double CopyMilliseconds
        {
            get; internal set;
        }
        /// <summary>新 staging 的文件及 native 回读检查；不含提交前后其余重复完整性检查。读取历史时没有该次测量。</summary>
        public double StagingValidationMilliseconds
        {
            get; internal set;
        }
        internal string ProjectGuid
        {
            get;
        }
        /// <summary>绑定确切发布根、Catalog 与可选归属 GUID，不据此取得文件写入权。</summary>
        internal ResourceRelease(string root, ResourceCatalogDocument catalog, bool repeat = false, long copied = 0, string projectGuid = null)
        {
            RootPath = root;
            DirectoryPath = Path.Combine(root, catalog.CatalogDigest);
            Catalog = catalog;
            IsRepeat = repeat;
            CopiedBytes = copied;
            ProjectGuid = projectGuid;
        }
    }

    /// <summary>本地/内部受控发布。只新增不可变版本，最后原子替换 Index；不写 StreamingAssets，不删除任何版本。</summary>
    [InitializeOnLoad]
    public static class ResourcePublisher
    {
        private const string OwnerFile = ".ylr-release-owner.json";
        private const string LockFile = ".ylr-release.lock";
        private const string JournalFile = ".ylr-publish-journal.json";
        private const string IndexFile = "release-index.json";
        private const string CatalogFile = "resource-catalog.json";
        private const string CompleteFile = ".ylr-complete.json";
        private const string Staging = ".staging";
        private static readonly int _mainThread;
        private static bool _isPublishing;
        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error,
            MaxDepth = 32,
            Culture = CultureInfo.InvariantCulture
        };

        /// <summary>发布根的持久归属及明确登记的版本集合，不依靠目录扫描推断所有权。</summary>
        private sealed class Owner
        {
            /// <summary>发布归属记录格式版本。</summary>
            public int Version = 1;
            /// <summary>登记时的完整工程根路径。</summary>
            public string ProjectRoot;
            /// <summary>所属项目配置的持久 GUID。</summary>
            public string ProjectGuid;
            /// <summary>所属项目的技术标识。</summary>
            public string ProjectId;
            /// <summary>归属的平台名称。</summary>
            public string Platform;
            /// <summary>明确登记的 Catalog 摘要集合，可包含已安装但尚未成为当前索引的完整版本。</summary>
            public string[] KnownVersions = Array.Empty<string>();
        }
        /// <summary>单次发布的原索引、目标索引和私有暂存文件清单，供按实际提交点恢复。</summary>
        private sealed class Journal
        {
            /// <summary>发布日志格式版本。</summary>
            public int Version = 1;
            /// <summary>本次尝试所属项目配置的 GUID。</summary>
            public string ProjectGuid;
            /// <summary>本次尝试所属项目的技术标识。</summary>
            public string ProjectId;
            /// <summary>本次尝试的平台名称。</summary>
            public string Platform;
            /// <summary>随机尝试身份，也是私有暂存子目录名。</summary>
            public string Attempt;
            /// <summary>提交前确切 Index；首次发布时可为 null。</summary>
            public JObject From;
            /// <summary>本次明确请求提交的目标 Index。</summary>
            public JObject To;
            /// <summary>暂存中允许出现及恢复时可清理的有限文件名集合。</summary>
            public string[] Files;
        }
        /// <summary>版本目录完成记录，将项目归属与 Catalog、BuildId 绑定，不是密码学签名。</summary>
        private sealed class Complete
        {
            /// <summary>完成记录格式版本。</summary>
            public int Version = 1;
            /// <summary>版本所属项目配置的 GUID。</summary>
            public string ProjectGuid;
            /// <summary>版本所属项目的技术标识。</summary>
            public string ProjectId;
            /// <summary>版本的平台名称。</summary>
            public string Platform;
            /// <summary>Catalog 规范构建身份。</summary>
            public string BuildId;
            /// <summary>完整 Catalog 字节摘要及版本目录身份。</summary>
            public string CatalogDigest;
        }

        /// <summary>记录允许执行发布的 Editor 主线程身份。</summary>
        static ResourcePublisher() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        /// <summary>只返回预期输出，不创建或认领目录。</summary>
        public static string GetReleaseRoot(ResourceBuildResult result)
        {
            if (result?.Map == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return Root(result.Map.ProjectId, result.Platform);
        }

        /// <summary>只读检查预期发布根及当前已登记版本，不认领目录或恢复未完成发布。</summary>
        internal static string Preflight(ResourceProject project, BuildTarget target)
        {
            ValidateEditor();
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("A saved ResourceProject is required.");
            }

            var root = Root(project.projectId, target.ToString());
            var owner = ReadOwner(root, guid, project.projectId, target.ToString());
            if (owner != null)
            {
                ValidateRoot(root, owner);
                var index = ReadIndex(root, owner);
                if (index != null)
                {
                    VerifyVersion(root, owner, index);
                }
            }
            return root;
        }

        /// <summary>只读取得当前 owned 发布，不执行 Recover 的提交点恢复或临时目录清理。</summary>
        public static ResourceRelease ReadCurrent(ResourceProject project, BuildTarget target, string appContentCompatibilityId)
        {
            ValidateEditor();
            if (project && EditorUtility.IsDirty(project))
            {
                throw new InvalidOperationException("发布集合候选定义未保存，拒绝按临时身份遗漏项目：" + AssetDatabase.GetAssetPath(project));
            }

            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("A saved ResourceProject is required.");
            }

            var root = Root(project.projectId, target.ToString());
            var owner = ReadOwner(root, guid, project.projectId, target.ToString());
            if (owner == null)
            {
                return null;
            }

            ValidateRoot(root, owner);
            var index = ReadIndex(root, owner);
            if (index == null)
            {
                return null;
            }

            if (index.UnityCompatibility != Application.unityVersion || index.AppContentCompatibilityId != appContentCompatibilityId)
            {
                throw new InvalidOperationException("Published Project has different Unity/App compatibility: " + project.projectId);
            }

            return new ResourceRelease(root, VerifyVersion(root, owner, index), projectGuid: owner.ProjectGuid);
        }

        /// <summary>按确切摘要只读选择历史 owned Release，供本地更新/回滚准备；不改最后发布 Index，也不操作 Runtime active。</summary>
        public static ResourceRelease ReadVersion(ResourceProject project, BuildTarget target, string appContentCompatibilityId, string catalogDigest)
        {
            ValidateEditor();
            ResourceCatalogCodec.Hex(catalogDigest, 64);
            if (project && EditorUtility.IsDirty(project))
            {
                throw new InvalidOperationException("Save the ResourceProject before selecting a release.");
            }

            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("A saved ResourceProject is required.");
            }

            var root = Root(project.projectId, target.ToString());
            var owner = ReadOwner(root, guid, project.projectId, target.ToString());
            if (owner == null || !owner.KnownVersions.Contains(catalogDigest))
            {
                throw new InvalidDataException("The selected version is not an owned release.");
            }

            ValidateRoot(root, owner);
            var catalog = VerifyComplete(Path.Combine(root, catalogDigest), owner, null);
            if (catalog.UnityCompatibility != Application.unityVersion || catalog.AppContentCompatibilityId != appContentCompatibilityId)
            {
                throw new InvalidDataException("The selected release has incompatible Unity/App requirements.");
            }

            return new ResourceRelease(root, catalog, projectGuid: owner.ProjectGuid);
        }

        /// <summary>只读枚举 owner 明确登记的完整版本。不扫描猜测目录，不恢复事务，不修改当前 Index，不删除旧版本。</summary>
        public static IReadOnlyList<ResourceRelease> ReadHistory(ResourceProject project, BuildTarget target)
        {
            ValidateEditor();
            if (project && EditorUtility.IsDirty(project))
            {
                throw new InvalidOperationException("Save the ResourceProject before reading owned history.");
            }

            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("A saved ResourceProject is required.");
            }

            var root = Root(project.projectId, target.ToString());
            var owner = ReadOwner(root, guid, project.projectId, target.ToString());
            if (owner == null)
            {
                return Array.Empty<ResourceRelease>();
            }

            ValidateRoot(root, owner);
            return owner.KnownVersions.OrderBy(x => x, StringComparer.Ordinal).Select(digest =>
                new ResourceRelease(root, VerifyComplete(Path.Combine(root, digest), owner, null), projectGuid: owner.ProjectGuid)).ToList().AsReadOnly();
        }

        /// <summary>复验明确发布的原始归属与完整版本，可同时核对独立复制目录，不信任内存引用本身。</summary>
        internal static ResourceCatalogDocument VerifyExportInput(ResourceRelease release, string copiedDirectory = null)
        {
            ValidateEditor();
            if (release?.Catalog == null || string.IsNullOrEmpty(release.ProjectGuid))
            {
                throw new ArgumentException("An explicit owned release is required.");
            }

            var catalog = release.Catalog;
            var root = Root(catalog.ProjectId, catalog.Platform);
            if (root != release.RootPath)
            {
                throw new InvalidOperationException("Release root identity differs from its Publisher origin.");
            }

            var owner = ReadOwner(root, release.ProjectGuid, catalog.ProjectId, catalog.Platform);
            if (owner == null || !owner.KnownVersions.Contains(catalog.CatalogDigest))
            {
                throw new InvalidOperationException("Release is no longer registered to this owner.");
            }

            ValidateRoot(root, owner);
            var expected = ResourceReleaseIndex.Create(catalog);
            var verified = VerifyComplete(release.DirectoryPath, owner, expected);
            if (copiedDirectory != null)
            {
                VerifyComplete(copiedDirectory, owner, expected);
            }

            return verified;
        }

        /// <summary>取消只在 Index 提交前生效。提交后的 IO 故障可能使调用结果未定，Recover 以实际 Index 确认完整快照。</summary>
        public static ResourceRelease Publish(ResourceBuildResult result, string appContentCompatibilityId, CancellationToken cancellation = default) =>
            Execute(result, appContentCompatibilityId, cancellation, null);

        /// <summary>使用调用方拥有的密钥来源复验加密交付后发布，结果不保存 provider 或密钥，Index 提交边界同普通发布。</summary>
        public static ResourceRelease PublishWithContentKey(ResourceBuildResult result, string appContentCompatibilityId,
            IResourceContentKeyProvider provider, CancellationToken cancellation = default) =>
            ExecuteWithContentKey(result, appContentCompatibilityId, cancellation, null, provider);

        /// <summary>通过共用发布实现执行不附带内容密钥来源的明确构建结果。</summary>
        internal static ResourceRelease Execute(ResourceBuildResult result, string app, CancellationToken cancellation, Action<string> checkpoint) =>
            ExecuteWithContentKey(result, app, cancellation, checkpoint, null);

        /// <summary>
        /// 同时锁定工作区与发布根，验证交付后安装完整新版本，最后提交 Index；不改写已安装版本。
        /// 失败保留日志供显式恢复，已安装但未索引的版本也保留；提交后抛错不能证明发布未生效。
        /// </summary>
        internal static ResourceRelease ExecuteWithContentKey(ResourceBuildResult result, string app, CancellationToken cancellation,
            Action<string> checkpoint, IResourceContentKeyProvider provider)
        {
            ValidateEditor();
            if (_isPublishing)
            {
                throw new InvalidOperationException("A resource publication is already running.");
            }

            cancellation.ThrowIfCancellationRequested();
            _isPublishing = true;
            byte[] contentKey = null;
            try
            {
                if (result == null)
                {
                    throw new ArgumentNullException(nameof(result));
                }

                ResourceBundleBuilder.ValidateContentPolicy(result.ContentEncryption, result.ContentKeyId, result.Platform);
                if (result.ContentEncryption != ResourceContentEncryption.None)
                {
                    contentKey = ResourceBundleBuilder.CopyContentKey(provider, result.Map.ProjectId, result.ContentKeyId);
                }

                var elapsed = Stopwatch.StartNew();
                var copyWatch = new Stopwatch();
                var validationWatch = new Stopwatch();
                using var workspaceLock = ResourceCatalogBuilder.LockWorkspace(result);
                var catalog = ResourceCatalogBuilder.CreateLocked(result, app);
                // 命中已发布版本也必须验证本次key及所有段，不能只信BuildResult或历史Complete。
                if (result.ContentEncryption != ResourceContentEncryption.None)
                {
                    VerifyContent(result.DeliveryPath, catalog, result, contentKey, cancellation, workspaceNames: true);
                }

                var root = GetReleaseRoot(result);
                var owner = ReadOwner(root, result.Map.ProjectGuid, result.Map.ProjectId, result.Platform);
                using var releaseLock = Acquire(root, ref owner, result.Map.ProjectGuid, result.Map.ProjectId, result.Platform);
                RecoverLocked(root, owner);
                var current = ReadIndex(root, owner);
                if (current != null)
                {
                    VerifyVersion(root, owner, current);
                }

                var target = ResourceReleaseIndex.Create(catalog);
                var directory = Path.Combine(root, target.CatalogDigest);
                ResourceRelease Completed(bool repeat = false, long bytes = 0) => new(root, catalog, repeat, bytes, result.Map.ProjectGuid)
                {
                    ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                    CopyMilliseconds = copyWatch.Elapsed.TotalMilliseconds,
                    StagingValidationMilliseconds = validationWatch.Elapsed.TotalMilliseconds
                };
                var existing = Directory.Exists(directory);
                if (existing)
                {
                    VerifyVersion(root, owner, target);
                    if (result.ContentEncryption != ResourceContentEncryption.None)
                    {
                        VerifyContent(directory, catalog, result, contentKey, cancellation);
                        VerifyNative(directory, catalog, result, cancellation, contentKey);
                    }
                }
                if (current?.CatalogDigest == target.CatalogDigest)
                {
                    return Completed(true);
                }

                cancellation.ThrowIfCancellationRequested();
                var journal = new Journal
                {
                    ProjectGuid = owner.ProjectGuid,
                    ProjectId = owner.ProjectId,
                    Platform = owner.Platform,
                    Attempt = Guid.NewGuid().ToString("N"),
                    From = current == null ? null : Parse(current.Write()),
                    To = Parse(target.Write()),
                    Files = AllFiles(catalog).Select(x => x.RelativeFile).Append(CatalogFile).Append(CompleteFile).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                };
                AtomicWrite(root, JournalFile, Metadata(journal), cancellation);
                var stage = Path.Combine(root, Staging, journal.Attempt);
                long copied = 0;
                void Step(string name)
                {
                    checkpoint?.Invoke(name);
                    cancellation.ThrowIfCancellationRequested();
                    ValidateRoot(root, owner, journal);
                }
                if (!existing)
                {
                    Directory.CreateDirectory(stage);
                    Step("StagingCreated");
                    foreach (var file in result.DeliveryFiles)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ResourceBuildOperation.RejectLinks(result.DeliveryPath);
                        copyWatch.Start();
                        using (var source = ResourceBuildWorkspace.OpenVerified(Path.Combine(result.DeliveryPath, file.Name), file.Length, file.Sha256, cancellation))
                        using (var output = File.Open(Path.Combine(stage, ResourceCatalogBuilder.PublishedFile(result, file.Name)), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            ResourceBundleBuilder.CopyContent(source, output, cancellation);
                            output.Flush(true);
                            copied += source.Length;
                        }
                        copyWatch.Stop();
                        Step("FileCopied");
                    }
                    WriteNew(Path.Combine(stage, CatalogFile), ResourceCatalogCodec.Write(catalog));
                    Step("CatalogWritten");
                    validationWatch.Start();
                    VerifyFiles(stage, catalog, false);
                    if (result.ContentEncryption != ResourceContentEncryption.None)
                    {
                        VerifyContent(stage, catalog, result, contentKey, cancellation);
                    }

                    VerifyNative(stage, catalog, result, cancellation, contentKey);
                    validationWatch.Stop();
                    WriteNew(Path.Combine(stage, CompleteFile), Metadata(new Complete
                    {
                        ProjectGuid = owner.ProjectGuid,
                        ProjectId = owner.ProjectId,
                        Platform = owner.Platform,
                        BuildId = catalog.BuildId,
                        CatalogDigest = catalog.CatalogDigest
                    }));
                    validationWatch.Start();
                    VerifyComplete(stage, owner, target);
                    validationWatch.Stop();
                    Step("StageVerified");
                    owner.KnownVersions = owner.KnownVersions.Append(target.CatalogDigest).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    if (owner.KnownVersions.Length > 1024)
                    {
                        throw new InvalidOperationException("Owned release version registration limit exceeded.");
                    }

                    AtomicWrite(root, OwnerFile, Metadata(owner), cancellation);
                    VerifyComplete(stage, owner, target);
                    Directory.Move(stage, directory);
                }
                Step("VersionInstalled");
                VerifyVersion(root, owner, target);
                Step("BeforeIndex");
                // 回调/外部受控工具也不能在提交前改掉基线；不以目录里的“最新”猜测授权。
                if (!SameIndex(ReadIndex(root, owner), current))
                {
                    throw new InvalidOperationException("Release Index changed during publication.");
                }

                if (current != null)
                {
                    VerifyVersion(root, owner, current);
                }

                VerifyVersion(root, owner, target);
                ValidateRoot(root, owner, journal);
                cancellation.ThrowIfCancellationRequested();
                AtomicWrite(root, IndexFile, target.Write(), cancellation);
                // 这是提交点：之后不再处理 cancellation。故障留 journal，恢复按实际 From/To，而不是无条件降级。
                checkpoint?.Invoke("IndexCommitted");
                RecoverLocked(root, owner);
                return Completed(false, copied);
            }
            finally
            {
                if (contentKey != null)
                {
                    Array.Clear(contentKey, 0, contentKey.Length);
                }

                _isPublishing = false;
            }
        }

        /// <summary>显式恢复 owned 本地事务。只认 journal 的 From/To 与完整快照，不激活 Runtime、不挑最新目录。</summary>
        public static ResourceRelease Recover(ResourceProject project, BuildTarget target, string appContentCompatibilityId)
        {
            ValidateEditor();
            if (_isPublishing)
            {
                throw new InvalidOperationException("A resource publication is already running.");
            }

            if (!project)
            {
                throw new ArgumentNullException(nameof(project));
            }

            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("A saved ResourceProject is required.");
            }

            var root = Root(project.projectId, target.ToString());
            var owner = ReadOwner(root, guid, project.projectId, target.ToString());
            if (owner == null)
            {
                return null;
            }

            using var locked = Acquire(root, ref owner, guid, project.projectId, target.ToString());
            RecoverLocked(root, owner);
            var index = ReadIndex(root, owner);
            if (index == null)
            {
                return null;
            }

            var catalog = VerifyVersion(root, owner, index);
            if (catalog.UnityCompatibility != Application.unityVersion || catalog.AppContentCompatibilityId != appContentCompatibilityId)
            {
                throw new InvalidDataException("Current published release is not compatible with the explicit recovery configuration.");
            }

            return new ResourceRelease(root, catalog, projectGuid: owner.ProjectGuid);
        }

        /// <summary>在锁内以实际 Index 匹配日志 From 或 To，仅清理该次登记的私有暂存和日志，不回退已安装版本。</summary>
        private static void RecoverLocked(string root, Owner owner)
        {
            ValidateRoot(root, owner);
            var journal = ReadJournal(root, owner);
            if (journal == null)
            {
                return;
            }

            var from = JournalIndex(journal.From, owner);
            var to = JournalIndex(journal.To, owner);
            var current = ReadIndex(root, owner);
            if (!SameIndex(current, from) && !SameIndex(current, to))
            {
                throw new InvalidOperationException("Journal has a stale/conflicting base; refusing to overwrite the current Release Index.");
            }

            if (current != null)
            {
                VerifyVersion(root, owner, current);
            }
            // From 仍有效即中止；To 已提交即确认成功。完整但未被索引的新版本也保留，不删除已安装目录。
            var stage = Path.Combine(root, Staging, journal.Attempt);
            if (Directory.Exists(stage))
            {
                ValidateRoot(root, owner, journal);
                foreach (var file in journal.Files)
                {
                    var path = Path.Combine(stage, file);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                Directory.Delete(stage, false);
            }
            ValidateRoot(root, owner, journal);
            File.Delete(Path.Combine(root, JournalFile));
            if (File.Exists(Path.Combine(root, JournalFile + ".next")))
            {
                File.Delete(Path.Combine(root, JournalFile + ".next"));
            }
        }

        /// <summary>取得排他发布锁并重验归属，只认领本次新建且无未知内容的根，返回句柄由调用方释放。</summary>
        private static FileStream Acquire(string root, ref Owner owner, string guid, string project, string platform)
        {
            var creating = owner == null;
            if (creating)
            {
                Directory.CreateDirectory(root);
            }

            var locked = File.Open(Path.Combine(root, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (creating && !File.Exists(Path.Combine(root, OwnerFile)))
                {
                    if (Directory.GetFileSystemEntries(root).Any(x => Path.GetFileName(x) != LockFile))
                    {
                        throw new InvalidOperationException("Unknown content appeared before output ownership registration.");
                    }

                    owner = new Owner { ProjectRoot = ResourceBuildMap.Absolute("."), ProjectGuid = guid, ProjectId = project, Platform = platform };
                    AtomicWrite(root, OwnerFile, Metadata(owner));
                }
                else
                {
                    owner = ReadOwner(root, guid, project, platform);
                }

                ValidateRoot(root, owner);
                return locked;
            }
            catch
            {
                locked.Dispose();
                throw;
            }
        }

        /// <summary>只读核对完整工程、项目与平台归属及目录闭包，根不存在返回 null，未知输出拒绝接管。</summary>
        private static Owner ReadOwner(string root, string guid, string project, string platform)
        {
            ResourceBuildOperation.RejectLinks(root);
            if (File.Exists(root) || File.Exists(root + ".meta"))
            {
                throw new InvalidOperationException("Release root is occupied by a file or orphan meta.");
            }

            if (!Directory.Exists(root))
            {
                return null;
            }

            if (!File.Exists(Path.Combine(root, OwnerFile)))
            {
                throw new InvalidOperationException("Unknown release output; refusing adoption.");
            }

            var owner = ReadMetadata<Owner>(Path.Combine(root, OwnerFile), "Version,ProjectRoot,ProjectGuid,ProjectId,Platform,KnownVersions");
            if (owner.Version != 1 || owner.ProjectRoot != ResourceBuildMap.Absolute(".") || owner.ProjectGuid != guid || owner.ProjectId != project || owner.Platform != platform ||
                owner.KnownVersions == null || owner.KnownVersions.Length > 1024 || owner.KnownVersions.Distinct(StringComparer.Ordinal).Count() != owner.KnownVersions.Length)
            {
                throw new InvalidOperationException("Release output ownership conflict.");
            }

            foreach (var digest in owner.KnownVersions)
            {
                Hex(digest, 64);
            }

            ValidateRoot(root, owner);
            return owner;
        }

        /// <summary>核对现场归属与日志未变，拒绝未登记根内容及暂存文件，并检查已登记版本结构。</summary>
        private static void ValidateRoot(string root, Owner owner, Journal expectedJournal = null)
        {
            ResourceBuildOperation.RejectLinks(root);
            if (owner == null || !File.Exists(Path.Combine(root, OwnerFile)))
            {
                throw new InvalidOperationException("Release ownership was removed during the transaction.");
            }

            var liveOwner = ReadMetadata<Owner>(Path.Combine(root, OwnerFile), "Version,ProjectRoot,ProjectGuid,ProjectId,Platform,KnownVersions");
            if (!Metadata(liveOwner).SequenceEqual(Metadata(owner)))
            {
                throw new InvalidOperationException("Release ownership/version registration changed during the transaction.");
            }

            var journal = ReadJournal(root, owner);
            if (expectedJournal != null && (journal == null || !Metadata(journal).SequenceEqual(Metadata(expectedJournal))))
            {
                throw new InvalidOperationException("Publication journal no longer authorizes this attempt.");
            }

            var controls = new[] { OwnerFile, OwnerFile + ".next", LockFile, JournalFile, JournalFile + ".next", IndexFile, IndexFile + ".next" };
            foreach (var path in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(path);
                if (File.Exists(path) && controls.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (Directory.Exists(path) && name == Staging)
                {
                    foreach (var entry in Directory.GetFileSystemEntries(path))
                    {
                        if (journal == null || !Directory.Exists(entry) || Path.GetFileName(entry) != journal.Attempt)
                        {
                            throw new InvalidOperationException("Unregistered staging content: " + entry);
                        }

                        foreach (var file in Directory.GetFileSystemEntries(entry))
                        {
                            if (!File.Exists(file) || !journal.Files.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                            {
                                throw new InvalidOperationException("Unregistered stage file/directory: " + file);
                            }
                        }
                    }
                    continue;
                }
                if (Directory.Exists(path) && owner.KnownVersions.Contains(name, StringComparer.Ordinal))
                {
                    VerifyComplete(path, owner, null, false);
                    continue;
                }
                throw new InvalidOperationException("Unknown release content: " + path);
            }
        }

        /// <summary>读取并验证有界发布日志、索引身份和安全文件名，不从未知暂存推测可恢复范围。</summary>
        private static Journal ReadJournal(string root, Owner owner)
        {
            var path = Path.Combine(root, JournalFile);
            if (!File.Exists(path))
            {
                return null;
            }

            var journal = ReadMetadata<Journal>(path, "Version,ProjectGuid,ProjectId,Platform,Attempt,From,To,Files");
            if (journal.Version != 1 || journal.ProjectGuid != owner.ProjectGuid || journal.ProjectId != owner.ProjectId || journal.Platform != owner.Platform)
            {
                throw new InvalidOperationException("Publication journal ownership conflict.");
            }

            Hex(journal.Attempt, 32);
            if (JournalIndex(journal.To, owner) == null || journal.Files == null || journal.Files.Length == 0 || journal.Files.Length > 65540 ||
                journal.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length || !journal.Files.Contains(CatalogFile) || !journal.Files.Contains(CompleteFile))
            {
                throw new InvalidDataException("Invalid publication journal file registration.");
            }

            JournalIndex(journal.From, owner);
            foreach (var file in journal.Files)
            {
                if (file == CatalogFile || file == CompleteFile)
                {
                    continue;
                }

                var native = file.EndsWith(".manifest", StringComparison.Ordinal) ? file.Substring(0, file.Length - 9) : file;
                if (native == "manifest")
                {
                    continue;
                }

                if (!native.StartsWith("b_", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Unsafe publication journal file.");
                }

                Hex(native.Substring(2), 32);
            }
            return journal;
        }

        /// <summary>读取当前确切 Index 并核对项目及平台，缺失时返回 null。</summary>
        private static ResourceReleaseIndex ReadIndex(string root, Owner owner) => !File.Exists(Path.Combine(root, IndexFile)) ? null :
            JournalIndex(Parse(ReadBounded(Path.Combine(root, IndexFile))), owner);

        /// <summary>将规范 JSON 解析为与当前归属匹配的 Index，允许首次发布的空基线。</summary>
        private static ResourceReleaseIndex JournalIndex(JObject data, Owner owner)
        {
            if (data == null)
            {
                return null;
            }

            return ResourceReleaseIndex.Read(Canonical(data), owner.ProjectId, owner.Platform,
                (string)data["UnityCompatibility"], (string)data["AppContentCompatibilityId"]);
        }
        /// <summary>比较 Index 的完整规范字节，不仅比较 Catalog 摘要。</summary>
        private static bool SameIndex(ResourceReleaseIndex left, ResourceReleaseIndex right) => left == null ? right == null :
            right != null && left.Write().SequenceEqual(right.Write());

        /// <summary>要求索引指向明确登记的版本，再验证完整版本与 Index 的身份一致。</summary>
        private static ResourceCatalogDocument VerifyVersion(string root, Owner owner, ResourceReleaseIndex index)
        {
            if (!owner.KnownVersions.Contains(index.CatalogDigest, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Release Index references an unowned version.");
            }

            return VerifyComplete(Path.Combine(root, index.CatalogDigest), owner, index);
        }

        /// <summary>验证完成记录、Catalog 摘要、目录身份和文件闭包，可按调用点选择是否重算交付文件哈希。</summary>
        private static ResourceCatalogDocument VerifyComplete(string directory, Owner owner, ResourceReleaseIndex expected, bool hashFiles = true)
        {
            ResourceBuildOperation.RejectLinks(directory);
            if (!Directory.Exists(directory))
            {
                throw new InvalidDataException("Published version directory is missing.");
            }

            var complete = ReadMetadata<Complete>(Path.Combine(directory, CompleteFile), "Version,ProjectGuid,ProjectId,Platform,BuildId,CatalogDigest");
            if (complete.Version != 1 || complete.ProjectGuid != owner.ProjectGuid || complete.ProjectId != owner.ProjectId || complete.Platform != owner.Platform)
            {
                throw new InvalidOperationException("Published snapshot ownership conflict.");
            }

            Hex(complete.BuildId, 64);
            Hex(complete.CatalogDigest, 64);
            if (Path.GetFileName(directory) != complete.CatalogDigest && Path.GetFileName(Path.GetDirectoryName(directory)) != Staging)
            {
                throw new InvalidDataException("Version directory does not match CatalogDigest.");
            }

            var bytes = ReadBounded(Path.Combine(directory, CatalogFile));
            if (ResourceCatalogCodec.Digest(bytes) != complete.CatalogDigest)
            {
                throw new InvalidDataException("CatalogDigest mismatch.");
            }

            var header = Parse(bytes);
            var catalog = ResourceCatalogCodec.Read(bytes, owner.ProjectId, owner.Platform,
                (string)header["UnityCompatibility"], (string)header["AppContentCompatibilityId"]);
            if (catalog.BuildId != complete.BuildId || expected != null && !expected.Write().SequenceEqual(ResourceReleaseIndex.Create(catalog).Write()))
            {
                throw new InvalidDataException("Complete/Catalog/Release Index identity mismatch.");
            }

            VerifyFiles(directory, catalog, true, hashFiles);
            return catalog;
        }

        /// <summary>枚举 Catalog 声明的 Bundle 与支持文件，不含 Catalog 或完成记录自身。</summary>
        private static IEnumerable<ResourceCatalogFile> AllFiles(ResourceCatalogDocument catalog) => catalog.Bundles.Cast<ResourceCatalogFile>().Concat(catalog.SupportFiles);

        /// <summary>要求版本文件集精确闭合且 Catalog 未变，需要时逐文件验证长度和全部字节摘要。</summary>
        private static void VerifyFiles(string directory, ResourceCatalogDocument catalog, bool complete, bool hashFiles = true)
        {
            var expected = AllFiles(catalog).Select(x => x.RelativeFile).Append(CatalogFile).ToList();
            if (complete)
            {
                expected.Add(CompleteFile);
            }

            if (!Directory.GetFileSystemEntries(directory).All(File.Exists) || !Directory.GetFiles(directory).Select(Path.GetFileName)
                .OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException("Snapshot contains missing or unregistered files/directories.");
            }

            if (ResourceCatalogCodec.Digest(ReadBounded(Path.Combine(directory, CatalogFile))) != catalog.CatalogDigest)
            {
                throw new InvalidDataException("Staged Catalog changed during publication.");
            }

            if (!hashFiles)
            {
                return;
            }

            foreach (var file in AllFiles(catalog))
            {
                using var stream = File.OpenRead(Path.Combine(directory, file.RelativeFile));
                using var sha = SHA256.Create();
                if (stream.Length != file.Length || BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != file.Sha256)
                {
                    throw new InvalidDataException("Snapshot file integrity mismatch: " + file.RelativeFile);
                }
            }
        }

        /// <summary>用明确密钥复验所有二进制交付与原始构建字节一致，支持工作区名或发布物理名。</summary>
        private static void VerifyContent(string directory, ResourceCatalogDocument catalog, ResourceBuildResult result, byte[] key,
            CancellationToken cancellation, bool workspaceNames = false)
        {
            foreach (var file in catalog.Bundles.Cast<ResourceCatalogFile>().Concat(catalog.SupportFiles.Where(x => x.RelativeFile == catalog.ManifestFile)))
            {
                cancellation.ThrowIfCancellationRequested();
                var name = file is ResourceCatalogBundle bundle ? bundle.LogicalId : result.ManifestBundleFile;
                var raw = result.Files.Single(x => x.Name == name);
                using var input = ResourceBuildWorkspace.OpenVerified(Path.Combine(directory, workspaceNames ? name : file.RelativeFile), file.Length, file.Sha256, cancellation);
                ResourceBundleBuilder.ValidateDelivery(input, result, name, key, raw.Length, raw.Sha256, cancellation);
            }
        }

        /// <summary>同时持有读回 Bundle 及可能被原生继续读取的输入，先卸载 Bundle 再释放流。</summary>
        private sealed class NativeReadback
        {
            internal AssetBundle Bundle;
            internal ResourceBundleInput Input;
        }

        // 卸载/释放异常时保留原owner，不让GC或下一次验证猜测native已停止读取。
        private static readonly List<NativeReadback> _retainedReadbacks = new();

        /// <summary>对照 Catalog 与构建布局原生读回发布字节，拒绝外部已加载 Bundle，并保留无法安全释放的所有者。</summary>
        private static void VerifyNative(string directory, ResourceCatalogDocument catalog, ResourceBuildResult result, CancellationToken cancellation, byte[] key)
        {
            if (_retainedReadbacks.Count != 0)
            {
                throw new InvalidOperationException("A previous native readback owner could not be released; ownership is retained for diagnosis.");
            }

            if (AssetBundle.GetAllLoadedAssetBundles().Any(x => x))
            {
                throw new InvalidOperationException("Native publication readback requires no externally loaded Bundles.");
            }

            var loaded = new List<NativeReadback>();
            var bundles = new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
            Exception primaryFailure = null;
            NativeReadback Load(ResourceCatalogFile file)
            {
                var owner = new NativeReadback();
                loaded.Add(owner);
                if (result.ContentEncryption == ResourceContentEncryption.None)
                {
                    owner.Bundle = AssetBundle.LoadFromFile(Path.Combine(directory, file.RelativeFile));
                }
                else
                {
                    var verified = ResourceBuildWorkspace.OpenVerified(Path.Combine(directory, file.RelativeFile), file.Length, file.Sha256, cancellation);
                    var name = file is ResourceCatalogBundle bundle ? bundle.LogicalId : result.ManifestBundleFile;
                    try
                    {
                        owner.Input = ResourceBundleInput.Open(verified, ResourceBundleBuilder.ContentContext(result, name), key, token: cancellation);
                    }
                    catch
                    {
                        verified.Dispose();
                        throw;
                    }
                    owner.Bundle = AssetBundle.LoadFromStream(owner.Input.Stream, 0, ResourceBundleEnvelope.NativeReadBufferSize);
                    if (owner.Input.Fault != null)
                    {
                        throw owner.Input.Fault;
                    }
                }
                return owner;
            }
            try
            {
                var rootOwner = Load(catalog.SupportFiles.Single(x => x.RelativeFile == catalog.ManifestFile));
                var root = rootOwner.Bundle;
                if (!root)
                {
                    throw new InvalidDataException("Cannot read staged Manifest Bundle.");
                }

                var manifest = root.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                if (!manifest || !manifest.GetAllAssetBundles().OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(catalog.Bundles.Select(x => x.LogicalId)))
                {
                    throw new InvalidDataException("Catalog/Manifest Bundle set mismatch.");
                }

                while (bundles.Count != catalog.Bundles.Count)
                {
                    var batch = catalog.Bundles.Where(x => !bundles.ContainsKey(x.LogicalId) && x.Dependencies.All(bundles.ContainsKey)).ToArray();
                    if (batch.Length == 0)
                    {
                        throw new InvalidDataException("Catalog Bundle dependency cycle.");
                    }

                    foreach (var entry in batch)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (manifest.GetAssetBundleHash(entry.NativeName).ToString() != entry.UnityHash ||
                            !manifest.GetDirectDependencies(entry.NativeName).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(entry.Dependencies))
                        {
                            throw new InvalidDataException("Catalog/Manifest dependency/hash mismatch.");
                        }

                        var owner = Load(entry);
                        var bundle = owner.Bundle;
                        if (!bundle)
                        {
                            throw new InvalidDataException("Cannot read staged Bundle: " + entry.RelativeFile);
                        }

                        bundles.Add(entry.LogicalId, bundle);
                        if (bundle.name != entry.NativeName)
                        {
                            throw new InvalidDataException("Staged native Bundle identity mismatch.");
                        }

                        var paths = entry.IsScene ? bundle.GetAllScenePaths() : bundle.GetAllAssetNames();
                        if (!paths.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                            result.Map.Bundles.Single(x => x.Name == entry.LogicalId).AssetPaths.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)))
                        {
                            throw new InvalidDataException("Staged Bundle file contents mismatch.");
                        }

                        foreach (var asset in result.Map.Assets.Where(x => x.BundleName == entry.LogicalId && !x.IsScene))
                        {
                            var value = bundle.LoadAsset(asset.SourcePath, asset.AssetType);
                            if (!value || !asset.AssetType.IsInstanceOfType(value))
                            {
                                throw new InvalidDataException("Staged main asset type mismatch.");
                            }

                            if (value is UnityEngine.U2D.SpriteAtlas atlas)
                            {
                                ResourceAtlasRules.ValidateReadback(atlas, asset.SourcePath);
                            }
                        }
                        foreach (var sub in result.SubAssets.Where(x => x.MainAsset.BundleName == entry.LogicalId))
                        {
                            if (bundle.LoadAssetWithSubAssets(sub.MainAsset.SourcePath, sub.AssetType).Count(x => x && x.name == sub.Name && sub.AssetType.IsInstanceOfType(x)) != 1)
                            {
                                throw new InvalidDataException("Staged subasset is ambiguous/missing.");
                            }
                        }

                        if (owner.Input?.Fault != null)
                        {
                            throw owner.Input.Fault;
                        }
                    }
                }
                foreach (var owner in loaded)
                {
                    if (owner.Input?.Fault != null)
                    {
                        throw owner.Input.Fault;
                    }
                }
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                var cleanup = ReleaseNativeReadbacks(loaded);
                if (cleanup != null)
                {
                    throw primaryFailure == null ? cleanup : new AggregateException("Native readback and owner release failed.", primaryFailure, cleanup);
                }
            }
        }

        /// <summary>逆序卸载本次 Bundle 后释放其输入，汇总读取或清理失败，保留清理失败的原所有者。</summary>
        private static Exception ReleaseNativeReadbacks(List<NativeReadback> loaded)
        {
            List<Exception> failures = null;
            for (var i = loaded.Count - 1; i >= 0; i--)
            {
                var owner = loaded[i];
                try
                {
                    if (owner.Bundle)
                    {
                        owner.Bundle.Unload(true);
                        owner.Bundle = null;
                    }
                    // 较早依赖可能在后续资产物化或卸载时才失败；释放输入不能抹去这个发布失败。
                    if (owner.Input?.Fault != null)
                    {
                        (failures ??= new()).Add(new IOException("A staged native input faulted before release.", owner.Input.Fault));
                    }

                    owner.Input?.Dispose();
                    owner.Input = null;
                }
                catch (Exception error)
                {
                    _retainedReadbacks.Add(owner);
                    (failures ??= new()).Add(new InvalidOperationException("Native/input release failed; the original owner is retained and further verification is refused.", error));
                }
            }
            return failures == null ? null : failures.Count == 1 ? failures[0] : new AggregateException("Native readback inputs or cleanup failed.", failures);
        }

        /// <summary>校验项目名、平台和路径长度后定位发布根，不创建或认领目录。</summary>
        private static string Root(string project, string platform)
        {
            ResourceAddress.ValidateId(project, nameof(project));
            var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (project.Length > 128 || project.EndsWith(".", StringComparison.Ordinal) || reserved.Contains(project.Split('.')[0], StringComparer.OrdinalIgnoreCase) ||
                !new[] { "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64" }.Contains(platform, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Unsafe release ProjectId or unsupported platform.");
            }

            var root = ResourceBuildMap.Absolute("AssetBundles/YLFramework/" + project + "/" + platform);
            if (Path.DirectorySeparatorChar == '\\' && Path.Combine(root, new string('0', 64), "b_" + new string('0', 32) + ".manifest").Length >= 260)
            {
                throw new InvalidOperationException("Release paths exceed this Unity Mono Windows file API limit; use a shorter project root/ProjectId.");
            }

            return root;
        }
        /// <summary>要求非播放、编译或导入中的稳定 Editor 主线程。</summary>
        private static void ValidateEditor()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Publication requires a settled Editor main thread outside Play mode.");
            }
        }
        /// <summary>要求持久摘要或尝试 token 使用指定长度的小写十六进制。</summary>
        private static void Hex(string value, int count)
        {
            if (value == null || value.Length != count || value.Any(x => !(x >= '0' && x <= '9' || x >= 'a' && x <= 'f')))
            {
                throw new InvalidDataException("Invalid canonical publication identity/token.");
            }
        }
        /// <summary>读取存在且未超出 Catalog 大小上限的发布元数据。</summary>
        private static byte[] ReadBounded(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > ResourceCatalogCodec.MaximumBytes)
            {
                throw new InvalidDataException("Missing or oversized publication metadata: " + path);
            }

            return File.ReadAllBytes(path);
        }
        /// <summary>严格解析有界 UTF-8 规范 JSON，拒绝重复字段、尾随内容及非规范字节。</summary>
        private static JObject Parse(byte[] bytes)
        {
            try
            {
                if (bytes.Length == 0 || bytes.Length > ResourceCatalogCodec.MaximumBytes)
                {
                    throw new InvalidDataException("Publication metadata limit exceeded.");
                }

                using var input = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
                using var reader = new JsonTextReader(input) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
                var data = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Load });
                if (reader.Read())
                {
                    throw new InvalidDataException("Additional publication metadata content.");
                }

                if (!bytes.SequenceEqual(Canonical(data)))
                {
                    throw new InvalidDataException("Noncanonical publication metadata.");
                }

                return data;
            }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException)
            {
                throw new InvalidDataException("Invalid publication metadata.", error);
            }
        }
        /// <summary>要求元数据字段集合精确匹配后反序列化，拒绝未知字段或字段类型错误。</summary>
        private static T ReadMetadata<T>(string path, string fields)
        {
            var data = Parse(ReadBounded(path));
            if (!data.Properties().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(fields.Split(',').OrderBy(x => x, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("Unknown/missing publication metadata fields.");
            }

            try
            {
                return data.ToObject<T>(JsonSerializer.Create(_jsonSettings));
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Invalid publication metadata field types.", error);
            }
        }
        /// <summary>按固定 JSON 设置将归属或日志对象转为规范字节。</summary>
        private static byte[] Metadata(object value) => Canonical(JObject.FromObject(value, JsonSerializer.Create(_jsonSettings)));
        /// <summary>递归排序对象属性并用固定转义规则输出有界 UTF-8 字节，保留数组顺序。</summary>
        private static byte[] Canonical(JToken value)
        {
            JToken Order(JToken token) => token switch
            {
                JObject obj => new JObject(obj.Properties().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new JProperty(x.Name, Order(x.Value)))),
                JArray array => new JArray(array.Select(Order)),
                _ => token.DeepClone()
            };
            var builder = new StringBuilder();
            using (var writer = new JsonTextWriter(new StringWriter(builder, CultureInfo.InvariantCulture))
            {
                Formatting = Formatting.None,
                StringEscapeHandling = StringEscapeHandling.EscapeNonAscii
            })
            {
                Order(value).WriteTo(writer);
            }

            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            if (bytes.Length > ResourceCatalogCodec.MaximumBytes)
            {
                throw new InvalidDataException("Publication metadata limit exceeded.");
            }

            return bytes;
        }
        /// <summary>独占新建文件并刷新到磁盘，不覆盖已有文件。</summary>
        private static void WriteNew(string path, byte[] bytes)
        {
            using var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        /// <summary>经同目录暂存提交单个元数据文件，不把多份发布文件视为同一原子操作。</summary>
        private static void AtomicWrite(string root, string name, byte[] bytes, CancellationToken cancellation = default)
            => AtomicWriteCore(root, name, bytes, cancellation, null, null, null);

        // 逐次调用接点只控制 IO 边界；正式入口不注入，所有校验与重试始终共用此实现。
        private static void AtomicWriteCore(string root, string name, byte[] bytes, CancellationToken cancellation,
            Action<string, string> replace, Action<int> backoff, Action afterNextWritten)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = Path.Combine(root, name);
            var next = path + ".next";
            ResourceBuildOperation.RejectLinks(path);
            ResourceBuildOperation.RejectLinks(next);
            var previous = File.Exists(path) ? ReadBounded(path) : null;
            using (var stream = File.Open(next, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            afterNextWritten?.Invoke();
            if (previous == null)
            {
                cancellation.ThrowIfCancellationRequested();
                File.Move(next, path);
                return;
            }
            for (var attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (replace == null)
                    {
                        File.Replace(next, path, null);
                    }
                    else
                    {
                        replace(next, path);
                    }

                    return;
                }
                // Win32 1175 保留 From/To 原名；仅在两份内容仍完全一致时有限重试，不处理可能已部分提交的1176/1177。
                catch (IOException error) when (Application.platform == RuntimePlatform.WindowsEditor && (uint)error.HResult == 0x80070497u && attempt < 5)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var delay = attempt == 0 ? 10 : 25 << (attempt - 1);
                    if (backoff == null)
                    {
                        Thread.Sleep(delay);
                    }
                    else
                    {
                        backoff(delay);
                    }

                    cancellation.ThrowIfCancellationRequested();
                    ResourceBuildOperation.RejectLinks(path);
                    ResourceBuildOperation.RejectLinks(next);
                    if (!ReadBounded(path).SequenceEqual(previous) || !ReadBounded(next).SequenceEqual(bytes))
                    {
                        throw new InvalidOperationException("Publication metadata changed after an atomic replacement failure; retry refused.", error);
                    }
                }
            }
        }
    }
}
