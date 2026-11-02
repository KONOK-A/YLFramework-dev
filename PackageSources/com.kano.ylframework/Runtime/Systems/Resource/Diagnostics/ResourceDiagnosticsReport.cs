using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace YLFramework.Resource
{
    /// <summary>供真实资产租约维护诊断链表和责任标签的内部借读接口，不额外持有资源。</summary>
    internal interface IAssetLeaseDiagnostics
    {
        /// <summary>同一资产记录诊断链表中的前一份租约。</summary>
        IAssetLeaseDiagnostics Previous
        {
            get; set;
        }
        /// <summary>同一资产记录诊断链表中的后一份租约。</summary>
        IAssetLeaseDiagnostics Next
        {
            get; set;
        }
        /// <summary>取得此租约时使用的地址，可能是资源别名。</summary>
        ResourceAddress Address
        {
            get;
        }
        /// <summary>调用方显式填写的责任标签，不推断未标注的实际持有者。</summary>
        string OwnerLabel
        {
            get;
        }
        /// <summary>此租约是否承担实例或缓存的内部 Prefab 保活责任。</summary>
        bool IsInstancePin
        {
            get;
        }
    }

    /// <summary>按需复制的不可变诊断数据。不持有 Unity 对象、可变 records 或任何资源使用权。</summary>
    /// <remarks>各项按采样顺序复制，不保证所有运行中数据来自同一瞬间；旧报告不会随之后的计数与标签变化而更新。</remarks>
    public sealed class ResourceDiagnosticsReport
    {
        // 诊断集合来自实际责任数量，不能误套单个 Catalog 的条目上限。
        /// <summary>复制已有诊断值对象的集合并提供只读视图，不深拷贝元素。</summary>
        internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> values) =>
            (values == null ? new List<T>() : new List<T>(values)).AsReadOnly();

        /// <summary>报告对象构造时的 UTC 时间，不是全部字段的统一读取时刻。</summary>
        public DateTime CapturedAtUtc
        {
            get;
        }
        /// <summary>采样主体的资源系统状态。</summary>
        public ResourceSystemState State
        {
            get;
        }
        /// <summary>资产与 Bundle 逻辑责任计数的值快照。</summary>
        public ResourceDiagnosticsSnapshot Summary
        {
            get;
        }
        /// <summary>本次只读采样的显式数据预算与进程CNG成本；不保活输入、不代表进程总内存。</summary>
        public ResourceContentDiagnosticsSnapshot Content
        {
            get;
        }
        /// <summary>当前挂载及资产记录仍引用的项目 Catalog 身份副本。</summary>
        public IReadOnlyList<ResourceProjectDiagnostics> Projects
        {
            get;
        }
        /// <summary>共享资产记录及其等待者、租约责任的采样副本。</summary>
        public IReadOnlyList<ResourceAssetDiagnostics> Assets
        {
            get;
        }
        /// <summary>Bundle 图节点与保留原因的采样副本。</summary>
        public IReadOnlyList<ResourceBundleDiagnostics> Bundles
        {
            get;
        }
        /// <summary>实例缓存桶的分类计数副本，不包含实例对象引用。</summary>
        public IReadOnlyList<ResourceInstanceCacheDiagnostics> InstanceCaches
        {
            get;
        }
        /// <summary>场景加载请求和场景记录的采样副本，不授予场景所有权。</summary>
        public IReadOnlyList<ResourceSceneDiagnostics> Scenes
        {
            get;
        }
        /// <summary>远端、内容 IO 及本地版本事务的阶段采样。</summary>
        public IReadOnlyList<ResourceUpdateDiagnostics> Updates
        {
            get;
        }
        /// <summary>Store 已记录恢复说明的副本；报告捕获不触发恢复。</summary>
        public IReadOnlyList<string> RecoveryNotes
        {
            get;
        }
        /// <summary>有界历史中按先后顺序保留的事件副本，默认历史关闭时为空。</summary>
        public IReadOnlyList<ResourceDiagnosticEvent> History
        {
            get;
        }
        /// <summary>捕获时配置的历史容量；0 表示历史关闭。</summary>
        public int HistoryCapacity
        {
            get;
        }

        /// <summary>复制各类诊断值集合，切断报告与运行中可变记录集合的关联。</summary>
        internal ResourceDiagnosticsReport(ResourceSystemState state, ResourceDiagnosticsSnapshot summary,
            IEnumerable<ResourceProjectDiagnostics> projects, IEnumerable<ResourceAssetDiagnostics> assets,
            IEnumerable<ResourceBundleDiagnostics> bundles, IEnumerable<ResourceInstanceCacheDiagnostics> caches,
            IEnumerable<ResourceSceneDiagnostics> scenes, IEnumerable<ResourceUpdateDiagnostics> updates,
            IEnumerable<string> recoveryNotes, IEnumerable<ResourceDiagnosticEvent> history, int capacity, ResourceContentDiagnosticsSnapshot content = default)
        {
            CapturedAtUtc = DateTime.UtcNow;
            State = state;
            Summary = summary;
            Content = content;
            Projects = Copy(projects);
            Assets = Copy(assets);
            Bundles = Copy(bundles);
            InstanceCaches = Copy(caches);
            Scenes = Copy(scenes);
            Updates = Copy(updates);
            RecoveryNotes = Copy(recoveryNotes);
            History = Copy(history);
            HistoryCapacity = capacity;
        }
    }

    /// <summary>单个 Catalog 的项目身份、兼容信息和来源类别副本，不持有 Catalog。</summary>
    public sealed class ResourceProjectDiagnostics
    {
        /// <summary>Catalog 所属的项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>运行中 Catalog 实例的快照身份，不等于内容摘要。</summary>
        public Guid SnapshotId
        {
            get;
        }
        /// <summary>Catalog 的内容摘要；非制品 Catalog 可未提供。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>本地制品文档格式版本；无制品快照时为 null。</summary>
        public int? SchemaVersion
        {
            get;
        }
        /// <summary>制品文档中的构建身份；无文档时为 null。</summary>
        public string BuildId
        {
            get;
        }
        /// <summary>制品文档声明的平台；无文档时为 null。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>制品文档声明的 Unity 兼容标识；无文档时为 null。</summary>
        public string UnityCompatibility
        {
            get;
        }
        /// <summary>制品文档声明的应用内容兼容标识；无文档时为 null。</summary>
        public string AppContentCompatibilityId
        {
            get;
        }
        /// <summary>采样时该项目是否处于本主体激活取得屏障。</summary>
        public bool IsActivationBlocked
        {
            get;
        }
        /// <summary>Catalog、实际资产记录及制品快照涉及的去重来源类别。</summary>
        public IReadOnlyList<ResourceSourceId> Sources
        {
            get;
        }

        /// <summary>从 Catalog 及活动来源复制身份信息，不保存文档或运行记录引用。</summary>
        internal ResourceProjectDiagnostics(ResourceCatalog catalog, bool blocked, IEnumerable<ResourceSourceId> activeSources)
        {
            ProjectId = catalog.ProjectId;
            SnapshotId = catalog.SnapshotId;
            CatalogDigest = catalog.CatalogDigest;
            IsActivationBlocked = blocked;
            var document = catalog.Snapshot?.Document;
            SchemaVersion = document?.SchemaVersion;
            BuildId = document?.BuildId;
            Platform = document?.Platform;
            UnityCompatibility = document?.UnityCompatibility;
            AppContentCompatibilityId = document?.AppContentCompatibilityId;
            Sources = ResourceDiagnosticsReport.Copy(catalog.Entries.Select(x => x.Location.Source).Concat(activeSources)
                .Concat(document == null ? Array.Empty<ResourceSourceId>() : new[] { ResourceSourceId.AssetBundle }).Distinct());
        }
    }

    /// <summary>一份真实资产租约的地址与显式责任标签副本，不延长租约寿命。</summary>
    public sealed class ResourceLeaseOwnerDiagnostics
    {
        /// <summary>资产记录的规范地址。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>取得租约时使用的原始地址，可能是别名。</summary>
        public ResourceAddress RequestedAddress
        {
            get;
        }
        /// <summary>采样时租约上的显式责任标签，未标注时不猜测持有者。</summary>
        public string OwnerLabel
        {
            get;
        }
        /// <summary>是否填写了非空白责任标签。</summary>
        public bool IsLabeled => !string.IsNullOrWhiteSpace(OwnerLabel);
        /// <summary>此责任是否为实例或缓存的内部 Prefab pin。</summary>
        public bool IsInstancePin
        {
            get;
        }
        /// <summary>复制租约的原始地址、标签和责任类别，规范地址由资产记录提供。</summary>
        internal ResourceLeaseOwnerDiagnostics(IAssetLeaseDiagnostics lease, ResourceAddress canonicalAddress)
        {
            Address = canonicalAddress;
            RequestedAddress = lease.Address;
            OwnerLabel = lease.OwnerLabel;
            IsInstancePin = lease.IsInstancePin;
        }
    }

    /// <summary>共享资产生产记录的值采样；仅复制对象存活判断和实例 ID，不保存 Unity 引用。</summary>
    public sealed class ResourceAssetDiagnostics
    {
        /// <summary>Catalog 登记的规范资产身份。</summary>
        public string AssetId
        {
            get;
        }
        /// <summary>共享资产记录的规范资源地址。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>此记录所属的运行中 Catalog 快照身份。</summary>
        public Guid SnapshotId
        {
            get;
        }
        /// <summary>此记录所属 Catalog 的内容摘要。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>生产此资源的来源类别。</summary>
        public ResourceSourceId Source
        {
            get;
        }
        /// <summary>来源用于展示的定位字符串，不是资源使用句柄。</summary>
        public string Locator
        {
            get;
        }
        /// <summary>位置声明的资产类型元数据，不引用已加载 Unity 资源。</summary>
        public Type DeclaredType
        {
            get;
        }
        /// <summary>共享资产记录是否仍处于加载阶段。</summary>
        public bool IsLoading
        {
            get;
        }
        /// <summary>尚未启动图片IO；仍属于同一记录及独立等待者，不是native操作。</summary>
        public bool IsQueued
        {
            get;
        }
        /// <summary>共享生产记录是否已请求取消，不等于其 native 操作已经停止。</summary>
        public bool CancellationRequested
        {
            get;
        }
        /// <summary>采样时生产操作或记录保留的进度，非加载记录报告为 1。</summary>
        public float Progress
        {
            get;
        }
        /// <summary>仍在共享资产记录上等待交付的消费者数量。</summary>
        public int WaiterCount
        {
            get;
        }
        /// <summary>共享资产记录的总租约数，包含内部实例 pin。</summary>
        public int LeaseCount
        {
            get;
        }
        /// <summary>总租约中承担实例或缓存保活责任的 pin 数，不应再次加到总数。</summary>
        public int InstancePins
        {
            get;
        }
        /// <summary>采样时资源是否通过 Unity 对象存活判断。</summary>
        public bool IsObjectAlive
        {
            get;
        }
        /// <summary>存活 Unity 资源的实例 ID；对象不存在或已被销毁时为 0。</summary>
        public int ObjectInstanceId
        {
            get;
        }
        /// <summary>此资产记录是否具备进入图片闲置缓存的资格。</summary>
        public bool IsImageCacheEligible
        {
            get;
        }
        /// <summary>采样时是否已登记到图片闲置缓存链表。</summary>
        public bool IsImageIdle
        {
            get;
        }
        /// <summary>图片缓存的估计字节，不是 Unity native memory 的测量值。</summary>
        public long ImageBytes
        {
            get;
        }
        /// <summary>记录上最近一次进入图片闲置缓存的实时秒数；非闲置时不表示当前闲置起点。</summary>
        public double IdleSince
        {
            get;
        }
        /// <summary>采样时可报告的租约责任；会话结束后仅保留内部实例 pin，不是租约总数的替代。</summary>
        public IReadOnlyList<ResourceLeaseOwnerDiagnostics> Owners
        {
            get;
        }

        /// <summary>复制资产记录和租约标签，使用 Unity fake-null 语义采样对象存活。</summary>
        internal ResourceAssetDiagnostics(ResourceSystem.AssetRecord record)
        {
            AssetId = record.Entry.AssetId;
            Address = record.Entry.Address;
            SnapshotId = record.Key.Item1.SnapshotId;
            CatalogDigest = record.Key.Item1.CatalogDigest;
            Source = record.Entry.Location.Source;
            Locator = record.Entry.Location.DisplayLocator;
            DeclaredType = record.Entry.Location.AssetType;
            IsLoading = record.IsLoading;
            CancellationRequested = record.CancelRequested;
            IsQueued = record.ImageQueueNode != null;
            Progress = record.IsLoading ? record.Operation?.Progress ?? record.LastProgress : 1;
            WaiterCount = record.Waiters.Count;
            LeaseCount = record.LeaseCount;
            InstancePins = record.InstancePins;
            IsObjectAlive = record.Asset;
            ObjectInstanceId = record.Asset ? record.Asset.GetInstanceID() : 0;
            IsImageCacheEligible = record.IsImageCacheEligible;
            IsImageIdle = record.ImageCacheNode != null;
            ImageBytes = record.ImageBytes;
            IdleSince = record.IdleSince;
            var owners = new List<ResourceLeaseOwnerDiagnostics>();
            for (var lease = record.LeaseHead; lease != null; lease = lease.Next)
            {
                if (record.Owner.IsSessionActive || lease.IsInstancePin)
                {
                    owners.Add(new ResourceLeaseOwnerDiagnostics(lease, record.Entry.Address));
                }
            }

            Owners = ResourceDiagnosticsReport.Copy(owners);
        }
    }

    /// <summary>Count 为真实图根或依赖入边数；native pending 的 Count 为0，不能混入 RootPins。</summary>
    public sealed class ResourceRetentionReason
    {
        /// <summary>实际图根、依赖入边或在途责任的诊断类别。</summary>
        public string Kind
        {
            get;
        }
        /// <summary>真实租约上的显式责任标签；无法取得时为 null。</summary>
        public string OwnerLabel
        {
            get;
        }
        /// <summary>可关联的规范资产或场景地址；不适用时为 null。</summary>
        public ResourceAddress? Address
        {
            get;
        }
        /// <summary>依赖入边的父 Bundle 标识；非父依赖原因时为 null。</summary>
        public string ParentBundleId
        {
            get;
        }
        /// <summary>此原因对应的真实图根或依赖入边计数；单纯在途观察项不增加此计数。</summary>
        public int Count
        {
            get;
        }
        /// <summary>采样时生成的原因说明文本，不引用运行中持有者。</summary>
        public string Detail
        {
            get;
        }

        /// <summary>保存一个真实保留原因的纯值描述。</summary>
        internal ResourceRetentionReason(string kind, string label, ResourceAddress? address, string parent, int count, string detail)
        {
            Kind = kind;
            OwnerLabel = label;
            Address = address;
            ParentBundleId = parent;
            Count = count;
            Detail = detail;
        }
    }

    /// <summary>Bundle 图节点的状态、逻辑引用与保留原因副本，不持有原生 Bundle 或内容输入。</summary>
    public sealed class ResourceBundleDiagnostics
    {
        /// <summary>Bundle 快照所属项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>Bundle 快照所属 Catalog 的内容摘要。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>Catalog 内的逻辑 Bundle 标识。</summary>
        public string BundleId
        {
            get;
        }
        /// <summary>制品声明的原生 Bundle 名称，用于定位项目 native namespace。</summary>
        public string NativeName
        {
            get;
        }
        /// <summary>采样时内部 Bundle 状态的文本副本。</summary>
        public string State
        {
            get;
        }
        /// <summary>是否仍登记原生创建请求；不是单独读取该请求的完成标志。</summary>
        public bool IsNativePending
        {
            get;
        }
        /// <summary>是否仍登记内容输入准备工作。</summary>
        public bool IsContentWorkPending
        {
            get;
        }
        /// <summary>此节点是否已读取到加密内容头。</summary>
        public bool IsEncrypted
        {
            get;
        }
        /// <summary>当前内容输入是否已记录读取或认证故障。</summary>
        public bool IsContentFaulted
        {
            get;
        }
        /// <summary>是否已记录内容清理错误，未完成的输入责任可能仍被保留。</summary>
        public bool HasContentCleanupFailed
        {
            get;
        }
        /// <summary>原生请求的采样进度；无原生请求时由节点是否结束给出 0 或 1。</summary>
        public float Progress
        {
            get;
        }
        /// <summary>采样时 Bundle 对象是否通过 Unity 存活判断。</summary>
        public bool IsObjectAlive
        {
            get;
        }
        /// <summary>仍在此 Bundle 记录上等待的消费者数量。</summary>
        public int WaiterCount
        {
            get;
        }
        /// <summary>真实公共及内部 Bundle 图根的持有计数。</summary>
        public int RootPins
        {
            get;
        }
        /// <summary>引用总数扣除图根后的依赖入边计数。</summary>
        public int DependencyPins
        {
            get;
        }
        /// <summary>Catalog交付文件字节，可能是密文；不是native memory。</summary>
        public long FileBytes
        {
            get;
        }
        /// <summary>加载错误优先、否则清理错误的文本副本；两者均无时为 null。</summary>
        public string Cause
        {
            get;
        }
        /// <summary>真实图根、依赖入边及在途观察项的只读原因集合。</summary>
        public IReadOnlyList<ResourceRetentionReason> RetentionReasons
        {
            get;
        }

        /// <summary>复制节点当前数值、故障文本与已整理的原因，不保存可变图节点。</summary>
        internal ResourceBundleDiagnostics(ResourceSystem.BundleRecord record, IEnumerable<ResourceRetentionReason> reasons)
        {
            ProjectId = record.Snapshot.Document.ProjectId;
            CatalogDigest = record.Snapshot.Document.CatalogDigest;
            BundleId = record.Descriptor.LogicalId;
            NativeName = record.Descriptor.NativeName;
            State = record.State.ToString();
            IsNativePending = record.Native != null;
            Progress = record.Native?.progress ?? (record.IsDone ? 1 : 0);
            IsObjectAlive = record.Bundle;
            IsContentWorkPending = record.InputWork != null;
            IsEncrypted = record.Header != null;
            IsContentFaulted = record.Input?.Fault != null;
            HasContentCleanupFailed = record.CleanupError != null;
            WaiterCount = record.Waiters.Count;
            RootPins = record.RootPins;
            DependencyPins = record.References - record.RootPins;
            FileBytes = record.Descriptor.Length;
            Cause = (record.Error ?? record.CleanupError)?.ToString();
            RetentionReasons = ResourceDiagnosticsReport.Copy(reasons);
        }
    }

    /// <summary>实例缓存桶的身份与分类计数副本，不保存实例、场景对象或 Prefab 租约。</summary>
    public sealed class ResourceInstanceCacheDiagnostics
    {
        /// <summary>缓存来源 Prefab 的规范地址。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>来源资产所属的运行中 Catalog 快照身份。</summary>
        public Guid SnapshotId
        {
            get;
        }
        /// <summary>来源资产所属 Catalog 的内容摘要。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>该桶配置的场景寿命策略。</summary>
        public InstanceCachePolicy Policy
        {
            get;
        }
        /// <summary>缓存绑定的范围场景句柄值，不代表场景当前仍加载。</summary>
        public int SceneHandle
        {
            get;
        }
        /// <summary>桶是否已退休并停止接受新的正常借出。</summary>
        public bool IsRetired
        {
            get;
        }
        /// <summary>对象存活且仍登记借出句柄的条目数。</summary>
        public int ActiveCount
        {
            get;
        }
        /// <summary>对象存活、无借出句柄且仍在闲置队列中的条目数。</summary>
        public int IdleCount
        {
            get;
        }
        /// <summary>对象仍存活但已标记销毁，或无操作过渡却已脱离正常状态的条目数。</summary>
        public int DestroyPendingCount
        {
            get;
        }
        /// <summary>Unity 对象已失效、尚未由正常 Prune 移除的条目数。</summary>
        public int DeadAwaitingPruneCount
        {
            get;
        }
        /// <summary>存在缓存操作时，尚未归入借出、闲置或销毁分类的存活条目数。</summary>
        public int TransitionCount
        {
            get;
        }
        /// <summary>缓存记录的进行中操作计数，不等同于原生异步请求数量。</summary>
        public int OperationCount
        {
            get;
        }

        /// <summary>保存来源身份和缓存分类计数，不复制任何运行中实例引用。</summary>
        internal ResourceInstanceCacheDiagnostics(ResourceSystem.AssetRecord asset, InstanceCachePolicy policy, int sceneHandle,
            bool retired, int active, int idle, int destroying, int dead, int transitions, int operations)
        {
            Address = asset.Entry.Address;
            SnapshotId = asset.Key.Item1.SnapshotId;
            CatalogDigest = asset.Key.Item1.CatalogDigest;
            Policy = policy;
            SceneHandle = sceneHandle;
            IsRetired = retired;
            ActiveCount = active;
            IdleCount = idle;
            DestroyPendingCount = destroying;
            DeadAwaitingPruneCount = dead;
            OperationCount = operations;
            TransitionCount = transitions;
        }
    }

    /// <summary>一次场景加载责任或已建立场景记录的值描述，不授予场景句柄的控制权。</summary>
    public sealed class ResourceSceneDiagnostics
    {
        /// <summary>场景资源的规范地址。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>场景来源 Catalog 的运行中快照身份。</summary>
        public Guid SnapshotId
        {
            get;
        }
        /// <summary>场景来源 Catalog 的内容摘要。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>加载请求或场景记录使用的精确场景路径。</summary>
        public string Path
        {
            get;
        }
        /// <summary>本主体为此次场景加载分配的序号，用于区分同路径的不同责任。</summary>
        public long LoadSequence
        {
            get;
        }
        /// <summary>已建立记录的场景句柄；尚为加载请求时为 null。</summary>
        public int? Handle
        {
            get;
        }
        /// <summary>采样时加载或记录生命周期阶段的文本描述。</summary>
        public string State
        {
            get;
        }
        /// <summary>是否存在尚未完成的原生加载或卸载操作。</summary>
        public bool IsNativePending
        {
            get;
        }
        /// <summary>已建立记录的场景在采样时是否仍加载。</summary>
        public bool IsLoaded
        {
            get;
        }
        /// <summary>已建立记录的场景身份是否存在歧义。</summary>
        public bool IsAmbiguous
        {
            get;
        }
        /// <summary>加载请求是否正等待显式允许场景激活。</summary>
        public bool IsWaitingForActivation
        {
            get;
        }
        /// <summary>此行是否来自已建立的场景记录，而非尚未交接的加载责任。</summary>
        public bool HasRecord
        {
            get;
        }
        /// <summary>此次责任是否已经启动过原生场景加载。</summary>
        public bool HasNativeStarted
        {
            get;
        }
        /// <summary>此次原生场景加载是否已完成，不表示后续卸载或清理已完成。</summary>
        public bool IsNativeCompleted
        {
            get;
        }
        /// <summary>尚在加载责任中的请求状态；已建立记录时为 null。</summary>
        public ResourceRequestStatus? RequestStatus
        {
            get;
        }

        /// <summary>复制场景身份、请求阶段与记录标志，不保存 Scene 或原生操作对象。</summary>
        internal ResourceSceneDiagnostics(ResourceAddress address, Guid snapshot, string digest, string path, long sequence,
            int? handle, string state, bool native, bool loaded, bool ambiguous, bool activation, ResourceRequestStatus? status,
            bool hasRecord, bool nativeStarted, bool nativeCompleted)
        {
            Address = address;
            SnapshotId = snapshot;
            CatalogDigest = digest;
            Path = path;
            LoadSequence = sequence;
            Handle = handle;
            State = state;
            IsNativePending = native;
            IsLoaded = loaded;
            IsAmbiguous = ambiguous;
            IsWaitingForActivation = activation;
            RequestStatus = status;
            HasRecord = hasRecord;
            HasNativeStarted = nativeStarted;
            IsNativeCompleted = nativeCompleted;
        }
    }

    /// <summary>更新作业或单个 GET 的阶段值采样，未提供的可空指标不以零伪装为已知。</summary>
    public sealed class ResourceUpdateDiagnostics
    {
        /// <summary>采样时检查、准备、下载、退避、内容 IO 或激活阶段的文本描述。</summary>
        public string Phase
        {
            get;
        }
        /// <summary>共享远端作业当前等待者数量；非远端阶段可为 0。</summary>
        public int WaiterCount
        {
            get;
        }
        /// <summary>活动 GET 的文件进度，非 GET 阶段可为 0，不表示整个更新包总进度。</summary>
        public float Progress
        {
            get;
        }
        /// <summary>当前文件已接收或保留的分片字节，含续传偏移；不代表本轮累计网络流量。</summary>
        public long? TransferredBytes
        {
            get;
        }
        /// <summary>当前 GET 已知的完整文件长度；未知或非 GET 阶段为 null。</summary>
        public long? ExpectedBytes
        {
            get;
        }
        /// <summary>当前 GET 已开始的重试次数，不包含首次尝试；非 GET 阶段为 null。</summary>
        public int? RetryCount
        {
            get;
        }
        /// <summary>当前 GET 是否处于下一次尝试前的退避等待。</summary>
        public bool IsWaitingToRetry
        {
            get;
        }
        /// <summary>当前传输对象创建以来的毫秒数，包含退避；非 GET 阶段可为 null。</summary>
        public double? DurationMilliseconds
        {
            get;
        }
        /// <summary>此阶段关联的项目副本，内容 IO 可列出输入项目，激活时列出屏障项目。</summary>
        public IReadOnlyList<string> ActivationProjects
        {
            get;
        }

        /// <summary>保存阶段指标并复制关联项目集合，不保留生产者或传输对象。</summary>
        internal ResourceUpdateDiagnostics(string phase, int waiters, float progress, long? bytes, long? expected,
            int? retries, bool waiting, double? duration, IEnumerable<string> projects = null)
        {
            Phase = phase;
            WaiterCount = waiters;
            Progress = progress;
            TransferredBytes = bytes;
            ExpectedBytes = expected;
            RetryCount = retries;
            IsWaitingToRetry = waiting;
            DurationMilliseconds = duration;
            ActivationProjects = ResourceDiagnosticsReport.Copy(projects);
        }
    }

    /// <summary>有界历史中的单个事件值，错误仅保存文本，不持有异常及其资源引用。</summary>
    public sealed class ResourceDiagnosticEvent
    {
        /// <summary>事件值创建时的 UTC 时间。</summary>
        public DateTime AtUtc
        {
            get;
        }
        /// <summary>请求完成、生产失败或迟到结果等事件类别。</summary>
        public string Kind
        {
            get;
        }
        /// <summary>事件关联的请求或生产结果状态；未关联时为 null。</summary>
        public ResourceRequestStatus? Status
        {
            get;
        }
        /// <summary>事件关联的资源地址；无可用地址时为 null。</summary>
        public ResourceAddress? Address
        {
            get;
        }
        /// <summary>捕获时的错误文本；无错误说明时为 null。</summary>
        public string Cause
        {
            get;
        }
        /// <summary>已提供的耗时毫秒数；是否有效须结合 HasDuration，缺省值为 0。</summary>
        public double DurationMilliseconds
        {
            get;
        }
        /// <summary>事件是否实际提供了耗时，而非使用默认数值。</summary>
        public bool HasDuration
        {
            get;
        }

        /// <summary>记录事件时间及纯值说明，不抓取调用栈或保留原异常。</summary>
        internal ResourceDiagnosticEvent(string kind, ResourceRequestStatus? status, ResourceAddress? address, string cause, double? duration)
        {
            AtUtc = DateTime.UtcNow;
            Kind = kind;
            Status = status;
            Address = address;
            Cause = cause;
            HasDuration = duration.HasValue;
            DurationMilliseconds = duration ?? 0;
        }
    }
}
