using System;
using System.Collections.Generic;
using System.Linq;

namespace YLFramework.Resource
{
    /// <summary>应用显式绑定的项目、兼容要求和可选内置快照。不会扫描发布目录推断版本。</summary>
    public sealed class ResourceProjectMount
    {
        /// <summary>显式挂载的项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>该项目要求的桌面制品平台标识。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>应用要求的内容兼容标识，须与所选 Catalog 一致。</summary>
        public string AppContentCompatibilityId
        {
            get;
        }
        /// <summary>显式绑定的内置回退快照；未提供时为 null。</summary>
        public ResourceLocalSnapshot BuiltIn
        {
            get;
        }
        /// <summary>可选的可信远端发布源；未配置时该项目不参与远端选择。</summary>
        public ResourceRemoteSource Remote
        {
            get;
        }
        /// <summary>创建 Catalog 时补充到常用 Unity 类型映射中的编译类型集合。</summary>
        internal IReadOnlyList<Type> AssetTypes
        {
            get;
        }

        /// <summary>绑定项目兼容要求，并核对可选内置快照与此挂载的身份。</summary>
        /// <param name="projectId">项目标识。</param>
        /// <param name="platform">支持的本地桌面平台标识。</param>
        /// <param name="appContentCompatibilityId">应用内容兼容标识。</param>
        /// <param name="builtIn">可选的同项目、平台及应用兼容标识的内置快照。</param>
        /// <param name="assetTypes">在常用 Unity 类型之外补充的编译资源类型集合。</param>
        /// <param name="remote">应用显式配置的可信远端发布源。</param>
        public ResourceProjectMount(string projectId, string platform, string appContentCompatibilityId,
            ResourceLocalSnapshot builtIn = null, IEnumerable<Type> assetTypes = null, ResourceRemoteSource remote = null)
        {
            ResourceCatalogCodec.Id(projectId);
            ResourceCatalogCodec.Id(appContentCompatibilityId);
            if (!new[] { "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64" }.Contains(platform))
            {
                throw new ArgumentException("A supported local desktop platform is required.", nameof(platform));
            }

            ProjectId = projectId;
            Platform = platform;
            AppContentCompatibilityId = appContentCompatibilityId;
            BuiltIn = builtIn;
            Remote = remote;
            AssetTypes = ResourceCatalog.Copy(assetTypes);
            if (builtIn != null && (builtIn.Document.ProjectId != projectId || builtIn.Document.Platform != platform ||
                builtIn.Document.AppContentCompatibilityId != appContentCompatibilityId))
            {
                throw new ArgumentException("The explicit built-in snapshot does not match its mount.", nameof(builtIn));
            }
        }
    }

    /// <summary>更新计划中的单个交付文件及本地复用判断，不持有文件句柄。</summary>
    public sealed class UpdateFile
    {
        /// <summary>Catalog 登记的相对文件名。</summary>
        public string RelativeFile
        {
            get;
        }
        /// <summary>交付文件的预期字节数。</summary>
        public long Length
        {
            get;
        }
        /// <summary>检查时是否已在来源版本中验证到同名、同长度、同 SHA256 的文件。</summary>
        public bool IsReusable
        {
            get;
        }
        internal string Input;
        /// <summary>保存文件描述与可选的本地复制输入路径。</summary>
        internal UpdateFile(string file, long length, bool reusable, string input)
        {
            RelativeFile = file;
            Length = length;
            IsReusable = reusable;
            Input = input;
        }
    }

    /// <summary>一次显式闭包选择；未变化的项目也须给出确切摘要，不推断跨 Catalog 依赖。</summary>
    public sealed class UpdateProject
    {
        /// <summary>本次闭包选择对应的项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>目标制品的平台标识。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>计划检查时来源 Catalog 的摘要，提交前仍须核对。</summary>
        public string FromDigest
        {
            get;
        }
        /// <summary>显式选择的目标 Catalog 摘要。</summary>
        public string ToDigest
        {
            get;
        }
        /// <summary>本地目标快照的目录；远端计划中的条目为 null，安装不会回填此属性。</summary>
        public string SourceDirectory
        {
            get;
        }
        /// <summary>本次需要安装的目标文件描述；目标版本已存在时可为空。</summary>
        public IReadOnlyList<UpdateFile> Files
        {
            get;
        }
        /// <summary>来源与目标 Catalog 摘要是否不同，不表示发布授权是否需要刷新。</summary>
        public bool IsChanged => FromDigest != ToDigest;
        internal ResourceLocalSnapshot From;
        internal ResourceLocalSnapshot Target;
        internal ResourceRemoteRelease Remote;
        /// <summary>记录本地来源与目标快照的明确转换。</summary>
        internal UpdateProject(ResourceLocalSnapshot from, ResourceLocalSnapshot target, IEnumerable<UpdateFile> files)
        {
            From = from;
            Target = target;
            ProjectId = target.Document.ProjectId;
            Platform = target.Document.Platform;
            FromDigest = from.Document.CatalogDigest;
            ToDigest = target.Document.CatalogDigest;
            SourceDirectory = target.DirectoryPath;
            Files = ResourceCatalog.Copy(files);
        }
        /// <summary>记录本地来源与已认证远端发布目标，暂不绑定目标目录。</summary>
        internal UpdateProject(ResourceLocalSnapshot from, ResourceRemoteRelease remote, IEnumerable<UpdateFile> files)
        {
            From = from;
            Remote = remote;
            ProjectId = remote.ProjectId;
            Platform = remote.Platform;
            FromDigest = from.Document.CatalogDigest;
            ToDigest = remote.CatalogDigest;
            Files = ResourceCatalog.Copy(files);
        }
    }

    /// <summary>只读检查结果。Reusable 文件仍复制到独立版本，不以硬链接或 mtime 代替完整性。</summary>
    public sealed class UpdatePlan
    {
        /// <summary>包含未变化项目在内的完整项目转换集合。</summary>
        public IReadOnlyList<UpdateProject> Projects
        {
            get;
        }
        /// <summary>待安装文件和事务控制记录的保守增量空间上界，不是磁盘空间预留。</summary>
        public long RequiredBytes
        {
            get;
        }
        /// <summary>计划文件中不可从来源版本复用的交付字节之和，不含全部事务开销。</summary>
        public long MissingBytes
        {
            get;
        }
        /// <summary>计划文件中可从来源版本复制的交付字节之和。</summary>
        public long ReusableBytes
        {
            get;
        }
        /// <summary>检查时观测到的目标卷可用空间，准备时可能已变化。</summary>
        public long AvailableBytes
        {
            get;
        }
        /// <summary>检查时的可用空间是否覆盖保守预算，不代表空间已被预留。</summary>
        public bool HasEnoughSpace => AvailableBytes >= RequiredBytes;
        internal string Root;
        internal bool IsRemote => Projects.Any(x => x.Remote != null);
        /// <summary>复制闭包选择并汇总文件复用量和空间预算。</summary>
        internal UpdatePlan(string root, IEnumerable<UpdateProject> projects, long available, long required)
        {
            Root = root;
            Projects = ResourceCatalog.Copy(projects);
            AvailableBytes = available;
            RequiredBytes = required;
            MissingBytes = Projects.SelectMany(x => x.Files).Where(x => !x.IsReusable).Sum(x => x.Length);
            ReusableBytes = Projects.SelectMany(x => x.Files).Where(x => x.IsReusable).Sum(x => x.Length);
        }
    }

    /// <summary>由完整 owned 版本重建的准备结果；不是激活权限，仍须检查 from/to 和真实资源屏障。</summary>
    public sealed class PreparedUpdate
    {
        /// <summary>已准备的完整来源和目标闭包，激活时仍须复验。</summary>
        public IReadOnlyList<UpdateProject> Projects
        {
            get;
        }
        internal string Root;
        internal bool IsRollback
        {
            get;
        }
        internal IReadOnlyList<ResourceRemoteRelease> Releases
        {
            get;
        }
        /// <summary>保存准备身份与发布授权副本；持久准备记录仍由 Store 管理。</summary>
        internal PreparedUpdate(string root, IEnumerable<UpdateProject> projects, bool isRollback = false, IEnumerable<ResourceRemoteRelease> releases = null)
        {
            Root = root;
            Projects = ResourceCatalog.Copy(projects);
            IsRollback = isRollback;
            Releases = ResourceCatalog.Copy(releases);
        }
    }

    /// <summary>一次版本清理的完成目录、保护集合与逐项错误；已删除内容不会因后续错误恢复。</summary>
    public sealed class VersionCleanupResult
    {
        /// <summary>本次已完成删除的版本或 staging 目录，不逐项列出已删下载分片。</summary>
        public IReadOnlyList<string> DeletedPaths
        {
            get;
        }
        /// <summary>因持久索引、准备记录或运行中责任而保留的版本路径。</summary>
        public IReadOnlyList<string> ProtectedVersions
        {
            get;
        }
        /// <summary>清理中记录的文件错误及保留的未知下载条目说明。</summary>
        public IReadOnlyList<string> Errors
        {
            get;
        }
        /// <summary>是否未记录清理错误；不表示所有磁盘目录都已删除。</summary>
        public bool HasSucceeded => Errors.Count == 0;
        /// <summary>复制本次清理结果，避免结果集合继续随调用方变化。</summary>
        internal VersionCleanupResult(IEnumerable<string> deleted, IEnumerable<string> protectedVersions, IEnumerable<string> errors)
        {
            DeletedPaths = ResourceCatalog.Copy(deleted);
            ProtectedVersions = ResourceCatalog.Copy(protectedVersions);
            Errors = ResourceCatalog.Copy(errors);
        }
    }
}
