using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>一次明确版本的只读预检，不是写入权限或可跳过复验的凭证。</summary>
    public sealed class ResourceBuiltInPlan
    {
        /// <summary>已明确绑定摘要的独立发布快照，执行部署时仍须重新打开验证。</summary>
        public ResourceLocalSnapshot Source
        {
            get;
        }
        /// <summary>源完成记录中的项目配置 GUID，用于目标归属和恢复空间分区。</summary>
        public string ProjectGuid
        {
            get;
        }
        /// <summary>预期的内置版本外壳绝对路径，不是 payload 目录。</summary>
        public string DirectoryPath
        {
            get;
        }
        /// <summary>Assets 外专属部署恢复空间的绝对路径。</summary>
        public string WorkspacePath
        {
            get;
        }
        /// <summary>待复制进 payload 的完整文件名集合，包含 Catalog 和完成记录。</summary>
        public IReadOnlyList<string> Files
        {
            get;
        }
        /// <summary>预检时源文件集合的字节总量，不包含新建部署外壳的控制记录。</summary>
        public long PayloadBytes
        {
            get;
        }
        /// <summary>预检时是否已有身份与文件完整匹配的目标版本，不是执行时免检许可。</summary>
        public bool IsRepeat
        {
            get;
        }
        internal int InstallDepth
        {
            get;
        }
        internal ResourceRelease Release
        {
            get; set;
        }
        /// <summary>复制本次预检定位信息并统计明确源文件集。</summary>
        internal ResourceBuiltInPlan(ResourceLocalSnapshot source, string guid, string directory, string workspace, int depth, bool repeat)
        {
            Source = source;
            ProjectGuid = guid;
            DirectoryPath = directory;
            WorkspacePath = workspace;
            InstallDepth = depth;
            IsRepeat = repeat;
            Files = ResourceBuiltInDeployer.PayloadFiles(source.Document).ToList().AsReadOnly();
            PayloadBytes = Files.Sum(name => new FileInfo(Path.Combine(source.DirectoryPath, name)).Length);
        }
        /// <summary>展示明确版本、目标和操作范围，不创建目录或执行部署。</summary>
        public override string ToString() => $"ProjectId={Source.Document.ProjectId} / Platform={Source.Document.Platform}\n" +
            $"Unity={Source.Document.UnityCompatibility} / App={Source.Document.AppContentCompatibilityId}\nCatalogDigest={Source.Document.CatalogDigest}\n" +
            $"来源={Source.DirectoryPath}\n目标={DirectoryPath}\n文件={Files.Count} / payload bytes={PayloadBytes} / 已完整部署={IsRepeat}\n" +
            $"操作空间={WorkspacePath}；仅新增不可变版本，不初始化或激活Runtime。";
    }

    /// <summary>已确认的内置版本位置及复制结果，不表示 Runtime 已挂载或激活。</summary>
    public sealed class ResourceBuiltInDeployment
    {
        /// <summary>完整部署版本的外壳绝对路径。</summary>
        public string DirectoryPath
        {
            get;
        }
        /// <summary>相对于Application.streamingAssetsPath；运行时仍应按明确身份调用OpenBuiltIn，不保存开发机绝对发布路径。</summary>
        public string RelativeDirectory
        {
            get;
        }
        /// <summary>通过正式内置入口重新打开验证的 payload 快照。</summary>
        public ResourceLocalSnapshot Snapshot
        {
            get;
        }
        /// <summary>是否确认既有完整版本而未新增复制，恢复已安装版本时也为 true。</summary>
        public bool IsRepeat
        {
            get;
        }
        /// <summary>本次复制的 payload 字节数，不包含部署控制记录，重复确认时为零。</summary>
        public long CopiedBytes
        {
            get;
        }
        /// <summary>记录已确认的外壳、快照及运行时可用的相对定位。</summary>
        internal ResourceBuiltInDeployment(string directory, ResourceLocalSnapshot snapshot, bool repeat, long bytes)
        {
            DirectoryPath = directory;
            Snapshot = snapshot;
            IsRepeat = repeat;
            CopiedBytes = bytes;
            RelativeDirectory = "YLFrameworkResources/" + snapshot.Document.ProjectId + "/" + snapshot.Document.Platform + "/" + snapshot.Document.CatalogDigest;
        }
    }

    /// <summary>Editor专用内置交付。严格验证后仅新增版本；恢复空间在Assets之外，不覆盖旧部署或更改active/LKG。</summary>
    [InitializeOnLoad]
    public static class ResourceBuiltInDeployer
    {
        private const string JournalFile = "journal.json";
        private const string LockFile = "deploy.lock";
        private static readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;
        private static bool _isDeploying;
        /// <summary>自有内置交付的固定根位置；读取此路径不认领或创建目录。</summary>
        public static string ResourcesRoot => ResourceBuildMap.Absolute("Assets/StreamingAssets/YLFrameworkResources");

        /// <summary>纯值定位独立恢复空间；不预检版本、不解析journal、不创建目录/锁或恢复。</summary>
        public static string GetWorkspacePath(string projectGuid, string platform)
        {
            ResourceCatalogCodec.Hex(projectGuid, 32);
            if (!new[] { "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64" }.Contains(platform, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Unsupported built-in workspace platform.");
            }

            return WorkspaceFor(projectGuid, platform);
        }

        /// <summary>单次部署日志还原的明确 Catalog、归属、尝试身份和最外层缺失安装节点。</summary>
        private sealed class Attempt
        {
            internal ResourceCatalogDocument Catalog;
            internal string Guid;
            internal string Token;
            internal int Depth;
            internal string Workspace => WorkspaceFor(Guid, Catalog.Platform);
            internal string Stage => Path.Combine(Workspace, "stage");
            internal string Destination => Paths(Catalog)[Depth];
            internal string Wrapper => Path.Combine(Stage, Relative(Paths(Catalog)[3], Destination));
        }

        /// <summary>选择Publisher确切owned Release；复验源归属、完整文件闭包、平台与兼容信息，不选择“最新”。</summary>
        public static ResourceBuiltInPlan Preflight(ResourceRelease release)
        {
            var catalog = ResourcePublisher.VerifyExportInput(release);
            var plan = Preflight(ResourceLocalSnapshot.Open(release.DirectoryPath, catalog.ProjectId, catalog.CatalogDigest,
                catalog.Platform, catalog.AppContentCompatibilityId));
            plan.Release = release;
            return plan;
        }

        /// <summary>完整快照入口；源和目标均只读预检，不建目录/锁、不Recover。冲突异常给出具体路径。</summary>
        public static ResourceBuiltInPlan Preflight(ResourceLocalSnapshot source)
        {
            ValidateEditor();
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var catalog = source.Document;
            ValidateIdentity(catalog.ProjectId, catalog.Platform);
            source = ResourceLocalSnapshot.Open(source.DirectoryPath, catalog.ProjectId, catalog.CatalogDigest, catalog.Platform, catalog.AppContentCompatibilityId);
            var guid = ResourceCatalogCodec.Text(ResourceBuiltInLayout.ReadObject(Path.Combine(source.DirectoryPath, ".ylr-complete.json")), "ProjectGuid");
            var workspace = WorkspaceFor(guid, catalog.Platform);
            if (Path.DirectorySeparatorChar == '\\' && Path.Combine(workspace, "stage", catalog.ProjectId, catalog.Platform, catalog.CatalogDigest,
                ResourceBuiltInLayout.PayloadFolder, "b_" + new string('0', 32) + ".manifest").Length >= 260)
            {
                throw new InvalidOperationException("Built-in staging paths exceed this Unity Mono Windows file API limit.");
            }

            if (Inside(source.DirectoryPath, ResourcesRoot) || Inside(source.DirectoryPath, workspace))
            {
                throw new InvalidOperationException("Deployment input must be an independent complete publication, not its target/workspace: " + source.DirectoryPath);
            }

            ValidateWorkspace(workspace, guid, catalog.ProjectId, catalog.Platform, false);
            if (File.Exists(Path.Combine(workspace, JournalFile)) || File.Exists(Path.Combine(workspace, JournalFile + ".next")))
            {
                throw new InvalidOperationException("Built-in deployment recovery is required before another deployment: " + workspace);
            }

            ValidateRoot();
            var paths = Paths(catalog);
            var depth = Array.FindIndex(paths, path => !Directory.Exists(path));
            var repeat = depth < 0;
            if (Directory.Exists(paths[1]))
            {
                RequireOwner(paths[1], Owner(catalog.ProjectId, null, guid));
            }

            if (Directory.Exists(paths[2]))
            {
                RequireOwner(paths[2], Owner(catalog.ProjectId, catalog.Platform, guid));
            }

            if (repeat)
            {
                VerifyWrapper(paths[3], catalog, guid);
            }

            return new ResourceBuiltInPlan(source, guid, paths[3], workspace, repeat ? 3 : depth, repeat);
        }

        /// <summary>预检会重新执行。取消仅在Directory.Move提交前生效；提交后故障须Recover确认，不能按异常假定未安装。</summary>
        public static ResourceBuiltInDeployment Deploy(ResourceBuiltInPlan plan, CancellationToken cancellation = default) => Execute(plan, cancellation, null);

        /// <summary>锁内复验来源和目标，暂存完整外壳后仅搬入最外层缺失节点，故障保留日志供显式恢复。</summary>
        internal static ResourceBuiltInDeployment Execute(ResourceBuiltInPlan plan, CancellationToken cancellation, Action<string> checkpoint)
        {
            ValidateEditor();
            if (_isDeploying)
            {
                throw new InvalidOperationException("A built-in deployment is already running.");
            }

            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            cancellation.ThrowIfCancellationRequested();
            _isDeploying = true;
            try
            {
                var current = plan.Release == null ? Preflight(plan.Source) : Preflight(plan.Release);
                if (current.ProjectGuid != plan.ProjectGuid)
                {
                    throw new InvalidDataException("The deployment source owner changed after preflight.");
                }

                if (current.IsRepeat)
                {
                    return Result(current.DirectoryPath, current.Source.Document, true, 0);
                }

                EnsureWorkspace(current);
                using var locked = new FileStream(Path.Combine(current.WorkspacePath, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // 锁内复验；不信任之前的只读预览或锁外观测。
                current = plan.Release == null ? Preflight(plan.Source) : Preflight(plan.Release);
                var attempt = new Attempt
                {
                    Catalog = current.Source.Document,
                    Guid = current.ProjectGuid,
                    Token = Guid.NewGuid().ToString("N"),
                    Depth = current.InstallDepth
                };
                WriteJournal(attempt);
                var expected = StageFiles(attempt);
                void Step(string name)
                {
                    checkpoint?.Invoke(name);
                    cancellation.ThrowIfCancellationRequested();
                    RequireAttempt(attempt);
                    ValidateRoot();
                    ValidatePartialStage(attempt, expected);
                }
                Step("JournalWritten");
                Directory.CreateDirectory(attempt.Stage);
                foreach (var pair in expected.Where(x => x.Value != null))
                {
                    var path = Path.Combine(attempt.Stage, pair.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    WriteNew(path, pair.Value);
                }
                Step("StagingCreated");
                long copied = 0;
                foreach (var name in current.Files)
                {
                    Step("BeforeCopy");
                    var input = Path.Combine(current.Source.DirectoryPath, name);
                    ResourceLocalSnapshot.RejectLinks(input);
                    var output = Path.Combine(ResourceBuiltInLayout.Payload(attempt.Wrapper), name);
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            target.Write(buffer, 0, read);
                            copied += read;
                        }
                        target.Flush(true);
                    }
                    Step("FileCopied");
                }
                VerifyStage(attempt, expected);
                Step("StageVerified");
                if (plan.Release != null)
                {
                    ResourcePublisher.VerifyExportInput(plan.Release);
                }
                else
                {
                    ResourceLocalSnapshot.Open(current.Source.DirectoryPath, attempt.Catalog.ProjectId, attempt.Catalog.CatalogDigest,
                    attempt.Catalog.Platform, attempt.Catalog.AppContentCompatibilityId);
                }

                Step("BeforeInstall");
                VerifyStage(attempt, expected);
                checkpoint?.Invoke("CommitReady");
                RequireAbsent(attempt.Destination);
                cancellation.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(attempt.Destination);
                if (!Directory.Exists(parent))
                {
                    var streamingAssets = ResourceBuildMap.Absolute("Assets/StreamingAssets");
                    if (attempt.Depth != 0 || !string.Equals(parent, streamingAssets, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("The deployment parent disappeared before commit: " + parent);
                    }
                    ResourceLocalSnapshot.RejectLinks(parent);
                    RequireAbsent(parent);
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", "StreamingAssets")) || !Directory.Exists(parent))
                    {
                        throw new IOException("Could not create the standard StreamingAssets folder for explicit deployment.");
                    }
                }
                // 搬入最外层缺失节点，其内容已全部闭合；不先创建未归属的目标父目录，也从不替换旧版本。
                Directory.Move(attempt.Stage, attempt.Destination);
                checkpoint?.Invoke("VersionInstalled");
                RequireAttempt(attempt);
                ValidateRoot();
                VerifyWrapper(current.DirectoryPath, attempt.Catalog, attempt.Guid);
                checkpoint?.Invoke("BeforeImport");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ValidateRoot();
                checkpoint?.Invoke("AfterImport");
                var result = Result(current.DirectoryPath, attempt.Catalog, false, copied);
                FinishAttempt(attempt);
                return result;
            }
            finally
            {
                _isDeploying = false;
            }
        }

        /// <summary>明确恢复选中Project的部署。已安装完整版本予以确认；未安装则仅删除journal登记的私有stage，返回null供重新部署。</summary>
        public static ResourceBuiltInDeployment Recover(ResourceProject project, BuildTarget target)
        {
            if (!project)
            {
                throw new ArgumentNullException(nameof(project));
            }

            return Recover(project.projectId, target.ToString(), AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project)));
        }

        /// <summary>按完整项目与平台身份恢复登记尝试，确认已安装版本或清理私有暂存；不扫描猜测最新版本。</summary>
        public static ResourceBuiltInDeployment Recover(string projectId, string platform, string projectGuid)
        {
            ValidateEditor();
            if (_isDeploying)
            {
                throw new InvalidOperationException("A built-in deployment is already running.");
            }

            ValidateIdentity(projectId, platform);
            ResourceCatalogCodec.Hex(projectGuid, 32);
            var workspace = WorkspaceFor(projectGuid, platform);
            ValidateWorkspace(workspace, projectGuid, projectId, platform, true);
            if (!Directory.Exists(workspace))
            {
                return null;
            }

            _isDeploying = true;
            try
            {
                using var locked = new FileStream(Path.Combine(workspace, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var attempt = ReadAttempt(workspace, projectGuid, projectId, platform);
                if (attempt == null)
                {
                    return null;
                }

                ValidateRoot();
                var expected = StageFiles(attempt);
                ValidatePartialStage(attempt, expected);
                var wrapper = Paths(attempt.Catalog)[3];
                var installed = Directory.Exists(wrapper);
                if (installed)
                {
                    VerifyWrapper(wrapper, attempt.Catalog, attempt.Guid);
                }

                if (Directory.Exists(attempt.Stage))
                {
                    DeleteStage(attempt, expected);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ValidateRoot();
                var result = installed ? Result(wrapper, attempt.Catalog, true, 0) : null;
                FinishAttempt(attempt);
                return result;
            }
            finally
            {
                _isDeploying = false;
            }
        }

        /// <summary>按稳定顺序列出交付文件、Catalog 与完成记录，不把签名元数据混入 payload。</summary>
        internal static IEnumerable<string> PayloadFiles(ResourceCatalogDocument catalog) => catalog.Bundles.Select(x => x.RelativeFile)
            .Concat(catalog.SupportFiles.Select(x => x.RelativeFile)).Concat(new[] { "resource-catalog.json", ".ylr-complete.json" }).OrderBy(x => x, StringComparer.Ordinal);

        /// <summary>通过正式内置快照入口复验安装结果，再创建部署描述。</summary>
        private static ResourceBuiltInDeployment Result(string wrapper, ResourceCatalogDocument catalog, bool repeat, long copied) =>
            new(wrapper, ResourceLocalSnapshot.OpenBuiltIn(catalog.ProjectId, catalog.CatalogDigest, catalog.Platform, catalog.AppContentCompatibilityId), repeat, copied);

        /// <summary>列出根、项目、平台到确切摘要版本的四层目标节点。</summary>
        private static string[] Paths(ResourceCatalogDocument catalog) => new[] { ResourcesRoot, Path.Combine(ResourcesRoot, catalog.ProjectId),
            Path.Combine(ResourcesRoot, catalog.ProjectId, catalog.Platform), Path.Combine(ResourcesRoot, catalog.ProjectId, catalog.Platform, catalog.CatalogDigest) };
        // 短路径只作定位；完整GUID/平台仍在owner中比较，任何token碰撞只能拒绝，不能认领。
        private static string WorkspaceFor(string guid, string platform) => ResourceBuildMap.Absolute("AssetBundles/YLBI_" +
            ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(guid + "/" + platform)).Substring(0, 16));
        /// <summary>为已确认祖先关系的路径计算相对后缀。</summary>
        private static string Relative(string path, string parent) => path == parent ? "" : path.Substring(parent.Length + 1);
        /// <summary>忽略大小写判断路径是否等于或位于指定绝对根内。</summary>
        private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        /// <summary>按部署层级生成包含完整工程归属的规范 owner 字节。</summary>
        private static byte[] Owner(string project = null, string platform = null, string guid = null) => ResourceCatalogCodec.CanonicalBytes(new JObject
        {
            ["Version"] = 1,
            ["ProjectRoot"] = ResourceBuildMap.Absolute("."),
            ["ProjectId"] = project,
            ["Platform"] = platform,
            ["ProjectGuid"] = guid
        });

        /// <summary>要求现有目录的规范 owner 与明确预期逐字节相同，不接管未知归属。</summary>
        private static void RequireOwner(string directory, byte[] expected)
        {
            var path = Path.Combine(directory, ResourceBuiltInLayout.OwnerFile);
            if (!File.Exists(path) || !ResourceCatalogCodec.CanonicalBytes(ResourceBuiltInLayout.ReadObject(path)).SequenceEqual(expected))
            {
                throw new InvalidDataException("Unknown/conflicting built-in ownership; refusing adoption: " + directory);
            }
        }

        /// <summary>递归验证整个自有部署根的层级归属、目录 meta 和完整版本，拒绝未知内容。</summary>
        private static void ValidateRoot()
        {
            ResourceLocalSnapshot.RejectLinks(ResourcesRoot);
            ResourceLocalSnapshot.RejectLinks(ResourcesRoot + ".meta");
            if (!Directory.Exists(ResourcesRoot))
            {
                RequireAbsent(ResourcesRoot);
                return;
            }
            RequireOwner(ResourcesRoot, Owner());
            ValidateFolderMeta(ResourcesRoot);
            Visit(ResourcesRoot, 0, null, null, null);

            void Visit(string directory, int depth, string project, string platform, string guid)
            {
                foreach (var entry in Directory.GetFileSystemEntries(directory))
                {
                    ResourceLocalSnapshot.RejectLinks(entry);
                    var name = Path.GetFileName(entry);
                    if (name == ResourceBuiltInLayout.OwnerFile && File.Exists(entry))
                    {
                        continue;
                    }

                    if (name.EndsWith(".meta", StringComparison.Ordinal) && File.Exists(entry))
                    {
                        var folder = entry.Substring(0, entry.Length - 5);
                        if (!Directory.Exists(folder))
                        {
                            throw new InvalidDataException("Orphan/unknown built-in metadata: " + entry);
                        }

                        ValidateFolderMeta(folder);
                        continue;
                    }
                    if (!Directory.Exists(entry))
                    {
                        throw new InvalidDataException("Unknown built-in content: " + entry);
                    }

                    if (depth == 0)
                    {
                        ValidateIdentity(name, "StandaloneWindows64");
                        var owner = ResourceBuiltInLayout.ReadObject(Path.Combine(entry, ResourceBuiltInLayout.OwnerFile));
                        var childGuid = ResourceCatalogCodec.Text(owner, "ProjectGuid");
                        ResourceCatalogCodec.Hex(childGuid, 32);
                        RequireOwner(entry, Owner(name, null, childGuid));
                        Visit(entry, 1, name, null, childGuid);
                    }
                    else if (depth == 1)
                    {
                        ValidateIdentity(project, name);
                        RequireOwner(entry, Owner(project, name, guid));
                        Visit(entry, 2, project, name, guid);
                    }
                    else
                    {
                        ResourceCatalogCodec.Hex(name, 64);
                        var receipt = ResourceBuiltInLayout.ReadObject(Path.Combine(entry, ResourceBuiltInLayout.ReceiptFile));
                        if (ResourceCatalogCodec.Text(receipt, "ProjectGuid") != guid)
                        {
                            throw new InvalidDataException("Built-in version owner differs: " + entry);
                        }

                        var payload = ResourceBuiltInLayout.Payload(entry);
                        var document = ResourceBuiltInLayout.ReadObject(Path.Combine(payload, "resource-catalog.json"));
                        ResourceBuiltInLayout.Open(entry, project, name, platform, ResourceCatalogCodec.Text(document, "AppContentCompatibilityId"));
                    }
                }
            }
        }

        /// <summary>若目录 meta 已生成则验证真实文件夹元数据形状，首次导入前允许缺失。</summary>
        private static void ValidateFolderMeta(string directory)
        {
            var meta = directory + ".meta";
            ResourceLocalSnapshot.RejectLinks(meta);
            if (!File.Exists(meta))
            {
                return; // 首次导入前尚无目录meta，刷新后只接纳对应owned目录的真实folder meta。
            }

            if (new FileInfo(meta).Length > 65536)
            {
                throw new InvalidDataException("Oversized built-in folder metadata: " + meta);
            }

            var text = File.ReadAllText(meta);
            if (!Regex.IsMatch(text, @"(?m)^fileFormatVersion: 2\r?$") || !Regex.IsMatch(text, @"(?m)^folderAsset: yes\r?$") ||
                !Regex.IsMatch(text, @"(?m)^guid: [0-9a-f]{32}\r?$"))
            {
                throw new InvalidDataException("Invalid built-in folder metadata: " + meta);
            }
        }

        /// <summary>通过正式布局验证外壳与 payload，并逐字节核对明确归属的部署回执。</summary>
        private static void VerifyWrapper(string wrapper, ResourceCatalogDocument catalog, string guid)
        {
            ResourceBuiltInLayout.Open(wrapper, catalog.ProjectId, catalog.CatalogDigest, catalog.Platform, catalog.AppContentCompatibilityId);
            if (!File.ReadAllBytes(Path.Combine(wrapper, ResourceBuiltInLayout.ReceiptFile)).SequenceEqual(ResourceBuiltInLayout.Receipt(catalog, guid)))
            {
                throw new InvalidDataException("Built-in version provenance differs: " + wrapper);
            }
        }

        /// <summary>拒绝不安全项目名、不支持平台和超出当前文件 API 限制的目标路径。</summary>
        private static void ValidateIdentity(string project, string platform)
        {
            ResourceCatalogCodec.Id(project);
            var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (project.EndsWith(".", StringComparison.Ordinal) || reserved.Contains(project.Split('.')[0], StringComparer.OrdinalIgnoreCase) ||
                !new[] { "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64" }.Contains(platform, StringComparer.Ordinal))
            {
                throw new InvalidDataException("Unsafe built-in project/platform identity: " + project + "/" + platform);
            }

            if (Path.DirectorySeparatorChar == '\\' && Path.Combine(ResourcesRoot, project, platform, new string('0', 64), "payload~", "b_" + new string('0', 32) + ".manifest").Length >= 260)
            {
                throw new InvalidOperationException("Built-in paths exceed this Unity Mono Windows file API limit; shorten the project path/ProjectId.");
            }
        }

        /// <summary>要求目标本体及 meta 均未占用且不经过链接，避免覆盖或接管。</summary>
        private static void RequireAbsent(string path)
        {
            ResourceLocalSnapshot.RejectLinks(path);
            ResourceLocalSnapshot.RejectLinks(path + ".meta");
            if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta") || Directory.Exists(path + ".meta"))
            {
                throw new InvalidOperationException("Built-in destination identity is occupied; refusing overwrite/adoption: " + path);
            }
        }

        /// <summary>验证或新建专属恢复空间，已存在时不重新授予归属。</summary>
        private static void EnsureWorkspace(ResourceBuiltInPlan plan)
        {
            ValidateWorkspace(plan.WorkspacePath, plan.ProjectGuid, plan.Source.Document.ProjectId, plan.Source.Document.Platform, false);
            if (Directory.Exists(plan.WorkspacePath))
            {
                return;
            }

            Directory.CreateDirectory(plan.WorkspacePath);
            WriteNew(Path.Combine(plan.WorkspacePath, ResourceBuiltInLayout.OwnerFile), Owner(plan.Source.Document.ProjectId, plan.Source.Document.Platform, plan.ProjectGuid));
        }

        /// <summary>核对完整恢复空间归属、日志和已登记暂存闭包，未恢复尝试阻止下一次部署。</summary>
        private static void ValidateWorkspace(string workspace, string guid, string project, string platform, bool recovering)
        {
            ResourceBuildOperation.RejectLinks(workspace);
            if (!Directory.Exists(workspace))
            {
                RequireAbsent(workspace);
                return;
            }
            RequireOwner(workspace, Owner(project, platform, guid));
            var attempt = ReadAttempt(workspace, guid, project, platform);
            foreach (var entry in Directory.GetFileSystemEntries(workspace))
            {
                var name = Path.GetFileName(entry);
                if (File.Exists(entry) && new[] { ResourceBuiltInLayout.OwnerFile, LockFile, JournalFile, JournalFile + ".next" }.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (attempt != null && Directory.Exists(entry) && name == "stage")
                {
                    ValidatePartialStage(attempt, StageFiles(attempt));
                    continue;
                }
                throw new InvalidDataException("Unknown deployment workspace content: " + entry);
            }
            if (!recovering && attempt != null)
            {
                throw new InvalidOperationException("Built-in recovery required: " + workspace);
            }
        }

        /// <summary>严格读取 journal 或独立 next 记录并还原明确部署计划，冲突记录拒绝恢复写入。</summary>
        private static Attempt ReadAttempt(string workspace, string guid, string project, string platform)
        {
            var journal = Path.Combine(workspace, JournalFile);
            if (File.Exists(journal) && (File.Exists(journal + ".next") || Directory.Exists(journal + ".next")))
            {
                throw new InvalidDataException("Conflicting deployment journal/next records; refusing recovery writes: " + workspace);
            }
            // 初次journal原子更名之前可能只留下next；没有开始stage，恢复仍须验证完整记录后才能删除。
            if (!File.Exists(journal))
            {
                journal += ".next";
            }

            if (!File.Exists(journal))
            {
                return null;
            }

            var data = ResourceBuiltInLayout.ReadObject(journal);
            ResourceCatalogCodec.Fields(data, "Version,ProjectRoot,ProjectGuid,Attempt,InstallDepth,Catalog");
            if (ResourceCatalogCodec.Integer(data, "Version") != 1 || ResourceCatalogCodec.Text(data, "ProjectRoot") != ResourceBuildMap.Absolute(".") ||
                ResourceCatalogCodec.Text(data, "ProjectGuid") != guid)
            {
                throw new InvalidDataException("Deployment journal owner conflict.");
            }

            var token = ResourceCatalogCodec.Text(data, "Attempt");
            ResourceCatalogCodec.Hex(token, 32);
            var depth = ResourceCatalogCodec.Integer(data, "InstallDepth");
            if (depth < 0 || depth > 3 || data["Catalog"] is not JObject catalog)
            {
                throw new InvalidDataException("Invalid deployment journal plan.");
            }

            var document = ResourceCatalogCodec.Read(ResourceCatalogCodec.CanonicalBytes(catalog), project, platform, Application.unityVersion,
                ResourceCatalogCodec.Text(catalog, "AppContentCompatibilityId"));
            return new Attempt { Catalog = document, Guid = guid, Token = token, Depth = (int)depth };
        }

        /// <summary>序列化尝试身份、安装深度与完整 Catalog，供恢复时精确重建范围。</summary>
        private static byte[] Journal(Attempt attempt) => ResourceCatalogCodec.CanonicalBytes(new JObject
        {
            ["Version"] = 1,
            ["ProjectRoot"] = ResourceBuildMap.Absolute("."),
            ["ProjectGuid"] = attempt.Guid,
            ["Attempt"] = attempt.Token,
            ["InstallDepth"] = attempt.Depth,
            ["Catalog"] = ResourceCatalogCodec.ParseObject(ResourceCatalogCodec.Write(attempt.Catalog))
        });

        /// <summary>独占新建并刷新 next 记录后更名为 journal，不覆盖已有恢复记录。</summary>
        private static void WriteJournal(Attempt attempt)
        {
            var next = Path.Combine(attempt.Workspace, JournalFile + ".next");
            WriteNew(next, Journal(attempt));
            File.Move(next, Path.Combine(attempt.Workspace, JournalFile));
        }

        /// <summary>复验恢复空间并要求现场日志仍与本次尝试的完整规范字节一致。</summary>
        private static void RequireAttempt(Attempt attempt)
        {
            ValidateWorkspace(attempt.Workspace, attempt.Guid, attempt.Catalog.ProjectId, attempt.Catalog.Platform, true);
            var live = ReadAttempt(attempt.Workspace, attempt.Guid, attempt.Catalog.ProjectId, attempt.Catalog.Platform);
            if (live == null || !Journal(live).SequenceEqual(Journal(attempt)))
            {
                throw new InvalidDataException("Deployment journal changed during the operation.");
            }
        }

        /// <summary>展开私有暂存的完整文件集合，控制记录带确切字节，待复制 payload 以 null 标记。</summary>
        private static Dictionary<string, byte[]> StageFiles(Attempt attempt)
        {
            var paths = Paths(attempt.Catalog);
            var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (var i = attempt.Depth; i < 3; i++)
            {
                expected.Add(Path.Combine(Relative(paths[i], attempt.Destination), ResourceBuiltInLayout.OwnerFile),
                    i == 0 ? Owner() : i == 1 ? Owner(attempt.Catalog.ProjectId, null, attempt.Guid) : Owner(attempt.Catalog.ProjectId, attempt.Catalog.Platform, attempt.Guid));
            }

            var wrapper = Relative(paths[3], attempt.Destination);
            expected.Add(Path.Combine(wrapper, ResourceBuiltInLayout.ReceiptFile), ResourceBuiltInLayout.Receipt(attempt.Catalog, attempt.Guid));
            foreach (var name in PayloadFiles(attempt.Catalog))
            {
                expected.Add(Path.Combine(wrapper, ResourceBuiltInLayout.PayloadFolder, name), null);
            }

            return expected;
        }

        /// <summary>允许登记文件尚未写全，但拒绝暂存中的链接、未知文件及未知目录。</summary>
        private static void ValidatePartialStage(Attempt attempt, Dictionary<string, byte[]> expected)
        {
            ResourceBuildOperation.RejectLinks(attempt.Stage);
            if (File.Exists(attempt.Stage))
            {
                throw new InvalidDataException("Deployment stage is not a directory.");
            }

            if (!Directory.Exists(attempt.Stage))
            {
                return;
            }

            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in expected.Keys)
            {
                for (var parent = Path.GetDirectoryName(file); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                {
                    directories.Add(parent);
                }
            }

            foreach (var path in Directory.GetFileSystemEntries(attempt.Stage, "*", SearchOption.AllDirectories))
            {
                var name = Relative(path, attempt.Stage);
                if (Directory.Exists(path) ? !directories.Contains(name) : !expected.ContainsKey(name))
                {
                    throw new InvalidDataException("Unregistered deployment staging content: " + path);
                }
            }
        }

        /// <summary>确认暂存文件集完整且控制字节匹配，再完整验证外壳和 payload。</summary>
        private static void VerifyStage(Attempt attempt, Dictionary<string, byte[]> expected)
        {
            ValidatePartialStage(attempt, expected);
            if (!Directory.GetFiles(attempt.Stage, "*", SearchOption.AllDirectories).Select(x => Relative(x, attempt.Stage))
                .OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.Keys.OrderBy(x => x, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("Deployment stage is incomplete.");
            }

            foreach (var pair in expected.Where(x => x.Value != null))
            {
                if (!File.ReadAllBytes(Path.Combine(attempt.Stage, pair.Key)).SequenceEqual(pair.Value))
                {
                    throw new InvalidDataException("Deployment stage ownership changed.");
                }
            }

            VerifyWrapper(attempt.Wrapper, attempt.Catalog, attempt.Guid);
        }

        /// <summary>先验证部分暂存仅含登记范围，再删除其文件与空目录，不触碰已安装版本。</summary>
        private static void DeleteStage(Attempt attempt, Dictionary<string, byte[]> expected)
        {
            ValidatePartialStage(attempt, expected);
            foreach (var file in Directory.GetFiles(attempt.Stage, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }

            foreach (var directory in Directory.GetDirectories(attempt.Stage, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            {
                Directory.Delete(directory, false);
            }

            Directory.Delete(attempt.Stage, false);
        }

        /// <summary>核对尝试身份且暂存已不存在后删除恢复记录，保留恢复空间和已部署版本。</summary>
        private static void FinishAttempt(Attempt attempt)
        {
            RequireAttempt(attempt);
            if (Directory.Exists(attempt.Stage))
            {
                throw new InvalidOperationException("Deployment staging still exists; recovery is required.");
            }

            File.Delete(Path.Combine(attempt.Workspace, JournalFile + ".next"));
            File.Delete(Path.Combine(attempt.Workspace, JournalFile));
        }

        /// <summary>独占新建并刷新完整字节，不覆盖现有文件。</summary>
        private static void WriteNew(string path, byte[] bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        /// <summary>要求稳定的 Editor 主线程且不处于播放、编译或导入状态。</summary>
        private static void ValidateEditor()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Built-in deployment requires the settled Editor main thread outside Play mode.");
            }
        }
    }
}
