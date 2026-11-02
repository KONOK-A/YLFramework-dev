using System;
using System.Collections.Generic;
using System.Linq;

namespace YLFramework.Resource
{
    /// <summary>有界制品观测的结束状态；只有 Ready 才给出清理候选字节汇总。</summary>
    public enum ArtifactQueryStatus
    {
        /// <summary>已完成本次元数据与文件集合观测，未发现使候选失效的变化。</summary>
        Ready,
        /// <summary>未配置制品存储。</summary>
        NotConfigured,
        /// <summary>配置的存储根当前不存在。</summary>
        Missing,
        /// <summary>版本操作或已有存储锁使当前观测无法继续。</summary>
        Busy,
        /// <summary>发现待恢复记录；查询未执行恢复或删除。</summary>
        RecoveryRequired,
        /// <summary>归属、记录结构或受保护版本的文件集合不符合要求。</summary>
        Invalid,
        /// <summary>文件系统访问失败，无法完成观测。</summary>
        Unreadable,
        /// <summary>达到显式扫描步骤上限，尚未完成观测。</summary>
        Incomplete,
        /// <summary>扫描期间存储活动、会话、保护责任或文件观测值发生变化。</summary>
        Changed,
        /// <summary>观察到调用方取消，保留已采集数据但不提供有效候选结论。</summary>
        Canceled
    }
    /// <summary>扫描条目在制品目录布局中的类别。</summary>
    public enum ArtifactKind
    {
        /// <summary>正式版本目录。</summary>
        Version,
        /// <summary>安装过程的 staging 目录。</summary>
        Staging,
        /// <summary>下载分片及其归属记录。</summary>
        Partial,
        /// <summary>存储归属、索引或准备记录等控制文件。</summary>
        Metadata,
        /// <summary>未识别为受管条目的文件或目录。</summary>
        Unknown
    }
    /// <summary>单个扫描条目的记录与文件集合状态，不表示内容已重新验签或验 hash。</summary>
    public enum ArtifactState
    {
        /// <summary>具有有效 owned Complete 记录及登记文件集合，尚不证明内容可挂载。</summary>
        CompleteRecord,
        /// <summary>具有 staging 归属记录的安装条目。</summary>
        Staging,
        /// <summary>具有 Partial 归属记录的下载条目。</summary>
        Partial,
        /// <summary>正式版本缺少登记文件，可能是清理中断后的残余。</summary>
        Incomplete,
        /// <summary>未确认受管归属或无需进一步分类的条目。</summary>
        Unknown,
        /// <summary>记录、路径或文件集合校验失败。</summary>
        Invalid,
        /// <summary>访问条目时出现文件系统错误。</summary>
        Unreadable,
        /// <summary>发现链接或重解析点，未跟随其目标。</summary>
        Linked
    }

    /// <summary>版本路径当前被保留的原因，可同时具有多个标志。</summary>
    [Flags]
    public enum ArtifactProtection
    {
        /// <summary>本次未观察到保护责任，不等于获得删除授权。</summary>
        None = 0,
        /// <summary>持久 active 索引指向此版本。</summary>
        Active = 1,
        /// <summary>持久 last-known-good 索引指向此版本。</summary>
        LastKnownGood = 2,
        /// <summary>准备记录仍引用此来源版本。</summary>
        PreparedFrom = 4,
        /// <summary>准备记录仍引用此目标版本。</summary>
        PreparedTo = 8,
        /// <summary>资源主体当前挂载此版本。</summary>
        Mounted = 16,
        /// <summary>运行中记录或内容责任仍持有此版本。</summary>
        Held = 32,
        /// <summary>进行中的加载、更新或尚未收尾的内容工作仍涉及此版本。</summary>
        InFlight = 64
    }

    /// <summary>制品报告中的项目配置身份副本，不保留挂载对象。</summary>
    public sealed class ArtifactProjectInfo
    {
        /// <summary>显式配置的项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>该项目绑定的制品平台。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>该项目要求的应用内容兼容标识。</summary>
        public string AppContentCompatibilityId
        {
            get;
        }
        /// <summary>从挂载配置复制报告需要的身份字段。</summary>
        internal ArtifactProjectInfo(ResourceProjectMount mount)
        {
            ProjectId = mount.ProjectId;
            Platform = mount.Platform;
            AppContentCompatibilityId = mount.AppContentCompatibilityId;
        }
    }

    /// <summary>某个版本目录在观测时的保护原因副本，不增加持有责任。</summary>
    public sealed class ArtifactVersionProtection
    {
        /// <summary>受保护的版本目录路径，查询不保证目录当前仍存在。</summary>
        public string DirectoryPath
        {
            get;
        }
        /// <summary>本次观测合并得到的保护标志。</summary>
        public ArtifactProtection Reasons
        {
            get;
        }
        /// <summary>保存路径及合并后的保护原因。</summary>
        internal ArtifactVersionProtection(string path, ArtifactProtection reasons)
        {
            DirectoryPath = path;
            Reasons = reasons;
        }
    }

    /// <summary>准备记录中单个项目的来源与目标身份副本。</summary>
    public sealed class ArtifactTransition
    {
        /// <summary>转换所属的项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>转换所属的平台标识。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>准备记录绑定的来源 Catalog 摘要。</summary>
        public string FromDigest
        {
            get;
        }
        /// <summary>准备记录绑定的目标 Catalog 摘要。</summary>
        public string ToDigest
        {
            get;
        }
        /// <summary>保存单个项目转换的纯身份数据。</summary>
        internal ArtifactTransition(string project, string platform, string from, string to)
        {
            ProjectId = project;
            Platform = platform;
            FromDigest = from;
            ToDigest = to;
        }
    }

    /// <summary>只读事务身份，不是可激活的 PreparedUpdate，也不持有快照或 Unity 对象。</summary>
    public sealed class ArtifactPreparedInfo
    {
        /// <summary>被观测的持久准备记录路径。</summary>
        public string RecordPath
        {
            get;
        }
        /// <summary>准备记录包含的完整项目转换集合副本。</summary>
        public IReadOnlyList<ArtifactTransition> Projects
        {
            get;
        }
        /// <summary>复制准备记录的路径和转换描述，不保留可激活句柄。</summary>
        internal ArtifactPreparedInfo(string path, IEnumerable<ArtifactTransition> projects)
        {
            RecordPath = path;
            Projects = Array.AsReadOnly(projects.ToArray());
        }
    }

    /// <summary>文件逻辑字节观测。CompleteRecord 仅指 owned Complete 记录及登记文件集合，不证明内容 SHA、可挂载性或签名新鲜度。</summary>
    public sealed class ArtifactEntry
    {
        /// <summary>条目代表的文件、目录或分片记录路径。</summary>
        public string Path
        {
            get;
        }
        /// <summary>条目所属项目；根级或未归属项目的条目可为 null。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>条目所属平台；未绑定项目时为 null。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>从有效记录取得的 Catalog 摘要；无法取得时为 null。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>条目在制品布局中的类别。</summary>
        public ArtifactKind Kind
        {
            get;
        }
        /// <summary>本次记录与文件集合检查得到的状态。</summary>
        public ArtifactState State
        {
            get;
        }
        /// <summary>此路径在本次观测中合并得到的保护原因。</summary>
        public ArtifactProtection Protection
        {
            get;
        }
        /// <summary>已观察到的普通文件逻辑长度之和，不包含未知目录内部或链接目标。</summary>
        public long ObservedBytes
        {
            get;
        }
        /// <summary>条目的逻辑字节统计是否完整，不表示文件内容完整性已验证。</summary>
        public bool IsSizeComplete
        {
            get;
        }
        /// <summary>Ready 观测中归属和大小已确认且无保护的清理候选，实际清理仍须复验。</summary>
        public bool IsCleanupCandidate
        {
            get;
        }
        /// <summary>本条目的校验、访问或保留原因说明副本。</summary>
        public IReadOnlyList<string> Issues
        {
            get;
        }
        /// <summary>复制条目观测值及问题集合，不保留扫描器的可变记录。</summary>
        internal ArtifactEntry(string path, ResourceProjectMount project, string digest, ArtifactKind kind, ArtifactState state,
            ArtifactProtection protection, long bytes, bool sizeComplete, bool candidate, IEnumerable<string> issues)
        {
            Path = path;
            ProjectId = project?.ProjectId;
            Platform = project?.Platform;
            CatalogDigest = digest;
            Kind = kind;
            State = state;
            Protection = protection;
            ObservedBytes = bytes;
            IsSizeComplete = sizeComplete;
            IsCleanupCandidate = candidate;
            Issues = Array.AsReadOnly(issues.ToArray());
        }
    }

    /// <summary>一次有界观测，不是磁盘快照或删除授权。实际清理必须重新调用 CleanupVersionsAsync。</summary>
    public sealed class ArtifactStoreReport
    {
        /// <summary>被观测的存储根；未配置 Store 时为 null。</summary>
        public string RootPath
        {
            get;
        }
        /// <summary>扫描器开始采样的 UTC 时间。</summary>
        public DateTimeOffset StartedUtc
        {
            get;
        }
        /// <summary>报告构造完成时的 UTC 时间，条目并非在此同一时刻读取。</summary>
        public DateTimeOffset CompletedUtc
        {
            get;
        }
        /// <summary>本次有界观测的结束状态。</summary>
        public ArtifactQueryStatus Status
        {
            get;
        }
        /// <summary>显式项目配置身份的只读副本。</summary>
        public IReadOnlyList<ArtifactProjectInfo> Projects
        {
            get;
        }
        /// <summary>本次已采集的互不重叠条目；非 Ready 时可仅覆盖部分目录。</summary>
        public IReadOnlyList<ArtifactEntry> Entries
        {
            get;
        }
        /// <summary>扫描时读取到的准备记录身份，不是可激活的准备结果。</summary>
        public IReadOnlyList<ArtifactPreparedInfo> PreparedUpdates
        {
            get;
        }
        /// <summary>持久记录和运行中责任提供的版本路径保护副本。</summary>
        public IReadOnlyList<ArtifactVersionProtection> ProtectedVersions
        {
            get;
        }
        /// <summary>采样时涉及该 Store 的运行中操作说明。</summary>
        public IReadOnlyList<string> InFlightOperations
        {
            get;
        }
        /// <summary>本次扫描级问题与中止原因。</summary>
        public IReadOnlyList<string> Issues
        {
            get;
        }
        /// <summary>已观测的普通文件逻辑长度之和，条目互不重叠；不含未知目录内部或链接目标。</summary>
        public long ObservedBytes
        {
            get;
        }
        /// <summary>是否已 Ready 且所有条目的逻辑字节统计完整。</summary>
        public bool IsSizeComplete
        {
            get;
        }
        /// <summary>仅 Ready 时提供已确认候选的逻辑字节；不是保证可释放的磁盘空间，更不是 native memory。</summary>
        public long? CleanupCandidateBytes
        {
            get;
        }
        /// <summary>冻结本次观测集合，并仅在 Ready 状态计算候选字节汇总。</summary>
        internal ArtifactStoreReport(ArtifactStore store, DateTimeOffset started, ArtifactQueryStatus status,
            IEnumerable<ArtifactEntry> entries, IEnumerable<ArtifactPreparedInfo> prepared,
            IEnumerable<ArtifactVersionProtection> protections, IEnumerable<string> operations, IEnumerable<string> issues)
        {
            RootPath = store?.RootPath;
            StartedUtc = started;
            CompletedUtc = DateTimeOffset.UtcNow;
            Status = status;
            Projects = Array.AsReadOnly(store == null ? Array.Empty<ArtifactProjectInfo>() : store.Projects.Select(x => new ArtifactProjectInfo(x)).ToArray());
            Entries = Array.AsReadOnly(entries.ToArray());
            PreparedUpdates = Array.AsReadOnly(prepared.ToArray());
            ProtectedVersions = Array.AsReadOnly(protections.ToArray());
            InFlightOperations = Array.AsReadOnly(operations.ToArray());
            Issues = Array.AsReadOnly(issues.ToArray());
            ObservedBytes = Entries.Sum(x => x.ObservedBytes);
            IsSizeComplete = status == ArtifactQueryStatus.Ready && Entries.All(x => x.IsSizeComplete);
            CleanupCandidateBytes = status == ArtifactQueryStatus.Ready ? Entries.Where(x => x.IsCleanupCandidate).Sum(x => x.ObservedBytes) : null;
        }
    }
}
