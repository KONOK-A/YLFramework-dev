using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using YLFramework.Singletons;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Resource
{
    /// <summary>单个资源系统实例从显式初始化到关闭或会话失效的状态。</summary>
    public enum ResourceSystemState
    {
        /// <summary>正在验证配置、挂载目录或认证内容，尚不接受资源取得。</summary>
        Initializing,
        /// <summary>初始化完成，可在有效会话内接受资源请求。</summary>
        Ready,
        /// <summary>停止接受新取得，等待无主在途操作和清理责任排空。</summary>
        ShuttingDown,
        /// <summary>正常关闭完成，不再作为当前资源权威。</summary>
        Closed,
        /// <summary>初始化失败，不能提供资源服务。</summary>
        Failed,
        /// <summary>执行环境退出导致本资源会话失效，旧责任由原系统继续收尾。</summary>
        Invalidated
    }

    /// <summary>逻辑记账快照，不代表 Unity native memory 用量或物理卸载。</summary>
    public readonly struct ResourceDiagnosticsSnapshot
    {
        /// <summary>仍登记的共享资产记录数，包含在途和闲置缓存记录。</summary>
        public int RecordCount
        {
            get;
        }
        /// <summary>仍处于加载状态的资产记录数，不单独统计 Bundle 生产。</summary>
        public int ProducerCount
        {
            get;
        }
        /// <summary>共享资产记录上仍等待交付的消费者总数。</summary>
        public int WaiterCount
        {
            get;
        }
        /// <summary>资产记录的持有责任总数，包含实例缓存内部 pin。</summary>
        public int LeaseCount
        {
            get;
        }
        /// <summary>尚未回收的 Bundle 图节点数。</summary>
        public int BundleRecordCount
        {
            get;
        }
        /// <summary>公共及内部 Bundle 图根租约的持有计数。</summary>
        public int BundleRootPinCount
        {
            get;
        }
        /// <summary>由依赖者节点持有的 Bundle 依赖入边总数。</summary>
        public int BundleDependencyPinCount
        {
            get;
        }
        /// <summary>占用异步 Bundle 并发槽的节点数，包含输入准备阶段。</summary>
        public int RunningBundleCount
        {
            get;
        }
        /// <summary>依赖已就绪、正在等待并发槽的 Bundle 节点数。</summary>
        public int QueuedBundleCount
        {
            get;
        }
        /// <summary>本系统累计启动或调用原生 Bundle 加载的次数。</summary>
        public int NativeBundleLoadCount
        {
            get;
        }
        /// <summary>本系统累计完成 Bundle.Unload 调用的次数，不表示物理内存已回收。</summary>
        public int NativeBundleUnloadCount
        {
            get;
        }
        /// <summary>保存一次逻辑账本采样，不持有采样涉及的资源。</summary>
        internal ResourceDiagnosticsSnapshot(int records, int producers, int waiters, int leases, int bundles,
            int roots, int edges, int running, int queued, int loads, int unloads)
        {
            RecordCount = records;
            ProducerCount = producers;
            WaiterCount = waiters;
            LeaseCount = leases;
            BundleRecordCount = bundles;
            BundleRootPinCount = roots;
            BundleDependencyPinCount = edges;
            RunningBundleCount = running;
            QueuedBundleCount = queued;
            NativeBundleLoadCount = loads;
            NativeBundleUnloadCount = unloads;
        }
    }

    /// <summary>每会话唯一、显式初始化的资源权威。所有入口仅用于 Unity 主线程，不创建协程宿主。</summary>
    /// <remarks>
    /// 一个系统实例管理自己的资源会话；正常关闭后可在同一 ExecutionSession 内重新初始化另一实例。
    /// 捕获的 ExecutionSession 仅用于识别执行环境是否已更替，不能替代资源系统自身的状态和责任账本。
    /// </remarks>
    public sealed partial class ResourceSystem
    {
        private static ResourceSystem _authority;
        private static readonly HashSet<ResourceSystem> _draining = new();
        private static bool _isEnding;
        private static int _mainThreadId;
        private readonly ResourceConfiguration _configuration;
        private readonly object _session = SingletonSessionState.ExecutionSession;
        private readonly Dictionary<string, ResourceCatalog> _catalogs = new(StringComparer.Ordinal);
        private readonly Dictionary<ResourceSourceId, IResourceSource> _sources = new();
        private readonly Dictionary<(ResourceCatalog, string, Type), AssetRecord> _records = new();
        private readonly HashSet<IResourceRequest> _ownedRequests = new();
        private readonly List<ResourceRequest<ResourceSystem>> _initialization = new();
        private readonly List<ResourceRequest<bool>> _shutdowns = new();
        private bool _isCheckingShutdown;

        /// <summary>读取不加载资源、不初始化服务、不创建宿主；初始化失败或关闭后为 null。</summary>
        public static ResourceSystem Current => _authority != null &&
            (_authority.State == ResourceSystemState.Ready || _authority.State == ResourceSystemState.ShuttingDown) ? _authority : null;
        /// <summary>本资源系统实例的初始化、关闭或失效状态。</summary>
        public ResourceSystemState State { get; private set; } = ResourceSystemState.Initializing;
        /// <summary>是否处于 Ready 且执行环境仍有效，可接受新的资源取得。</summary>
        public bool IsAvailable => State == ResourceSystemState.Ready && IsSessionActive;
        /// <summary>执行环境身份及资源会话状态是否仍有效；初始化和正常关闭排空期间也可为 true。</summary>
        internal bool IsSessionActive => !_isEnding && SingletonSessionState.CanRunExecution && ReferenceEquals(_session, SingletonSessionState.ExecutionSession) &&
            (State == ResourceSystemState.Ready || State == ResourceSystemState.Initializing || State == ResourceSystemState.ShuttingDown);

        /// <summary>安装执行会话、应用退出及 Editor 生命周期回调，不创建资源服务。</summary>
        static ResourceSystem()
        {
            SingletonSessionState.RegisterSessionReset(ResetSession);
            Application.quitting += EndSession;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += () =>
            {
                if (!Application.isPlaying)
                {
                    TickInstanceCleanup();
                }
            };
#endif
        }

        /// <summary>记录 Unity 运行初始化所在的主线程身份。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeThread() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;
#if UNITY_EDITOR
        /// <summary>在 Editor 加载时建立相同的主线程检查基准。</summary>
        [InitializeOnLoadMethod]
        private static void InitializeEditorThread() => InitializeThread();
        /// <summary>在编辑与运行模式切换的退出边界使当前资源会话失效。</summary>
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.EnteredEditMode)
            {
                EndSession();
            }
        }
#endif

        /// <summary>保存显式配置并创建本实例的内容预算，不在构造时加载资源。</summary>
        private ResourceSystem(ResourceConfiguration configuration)
        {
            this._configuration = configuration;
            _contentBudget = new ResourceContentBudget(configuration?.Decryption?.MaxBufferedBytes ?? 128L * 1024 * 1024);
        }

        /// <summary>8.1 内存验证可即时完成。同一配置对象的在途初始化可 join；不同配置须先成功 Shutdown。</summary>
        /// <remarks>结果为借用的唯一服务，不因初始化 Request Dispose 而关闭；不提供自动领取 await。</remarks>
        /// <param name="config">本次资源会话使用的显式配置；合并初始化按配置对象身份判断。</param>
        /// <returns>独立初始化请求，须显式领取服务引用；旧会话未排空时返回 Busy 错误。</returns>
        public static ResourceRequest<ResourceSystem> InitializeAsync(ResourceConfiguration config)
        {
            AssertThread();
            var request = new ResourceRequest<ResourceSystem>();
            if (_isEnding || !SingletonSessionState.CanRunExecution)
            {
                return Fail(request, ResourceError.SessionUnavailable, "Resource initialization requires an active Play/Player session.");
            }

            TickInstanceCleanup();
            if (_draining.Count != 0)
            {
                return Fail(request, ResourceError.Busy, "An old session still owns unfinished native producers.");
            }

            if (_authority != null)
            {
                if (!ReferenceEquals(_authority._configuration, config))
                {
                    return Fail(request, ResourceError.ConfigurationConflict, "Another resource configuration is active or initializing.");
                }

                if (_authority.State == ResourceSystemState.Initializing)
                {
                    _authority.JoinInitialization(request);
                }
                else if (_authority.IsAvailable)
                {
                    request.SetSuccess(_authority);
                    request.Notify();
                }
                else
                {
                    return Fail(request, ResourceError.Busy, "The resource authority is closing.");
                }

                return request;
            }
            var system = new ResourceSystem(config);
            _authority = system;
            system.JoinInitialization(request);
            try
            {
                if (config?.ArtifactStore == null)
                {
                    system.ContinueInitialization(null);
                }
                else
                {
                    system._initializationWork = system.StartContentWork(token => config.ArtifactStore.MountForContent(token), (mounted, error) =>
                {
                    system._initializationWork = null;
                    if (error != null)
                    {
                        system.FinishInitialization(error is ResourceException || error is System.OperationCanceledException ? error :
                        new ResourceException(ResourceError.InvalidConfiguration, "Resource mounted configuration validation failed.", error));
                    }
                    else
                    {
                        system.ContinueInitialization(mounted);
                    }
                }, snapshots: new List<ResourceLocalSnapshot>(config.ArtifactStore.Projects.Select(x => x.BuiltIn)));
                }
            }
            catch (Exception error)
            {
                system.FinishInitialization(error as ResourceException ?? new ResourceException(ResourceError.InvalidConfiguration, "Resource configuration validation failed.", error));
            }
            return request;
        }

        /// <summary>登记来源与冻结目录，验证项目身份、平台、路径及来源约束。</summary>
        private void ValidateConfiguration(IReadOnlyList<ResourceLocalSnapshot> mounted)
        {
            if (_configuration == null)
            {
                throw new ArgumentException("An explicit resource configuration is required.");
            }

            foreach (var source in _configuration.Sources)
            {
                if (source == null || !source.Id.IsValid || _sources.ContainsKey(source.Id))
                {
                    throw new ArgumentException("Null, invalid or duplicate source identity.");
                }

                _sources.Add(source.Id, source);
            }
            _sources.Add(ResourceSourceId.AssetBundle, new AssetBundleSource(this));
            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var configuredCatalogs = new List<ResourceCatalog>(_configuration.Catalogs);
            if (_configuration.ArtifactStore != null)
            {
                foreach (var snapshot in mounted)
                {
                    configuredCatalogs.Add(CreateStoreCatalog(snapshot));
                }
            }

            foreach (var catalog in configuredCatalogs)
            {
                if (catalog == null || !projects.Add(catalog.ProjectId))
                {
                    throw new ArgumentException("Null or case-conflicting project catalog.");
                }

                _catalogs.Add(catalog.ProjectId, catalog);
                if (catalog.Snapshot != null)
                {
                    ValidateSnapshotPlatform(catalog.Snapshot);
                }

                var resourcePaths = new HashSet<(string, Type)>(new ResourcesPathComparer());
                foreach (var entry in catalog.Entries)
                {
                    if (!_sources.TryGetValue(entry.Location.Source, out var source))
                    {
                        throw new ArgumentException($"Unconfigured source '{entry.Location.Source}' for '{entry.Address}'.");
                    }

                    source.Validate(entry);
                    if (entry.Location.Source.Equals(ResourceSourceId.Resources) && !resourcePaths.Add((entry.Location.Locator, entry.Location.AssetType)))
                    {
                        throw new ArgumentException($"Case-conflicting Resources locator: '{entry.Location.Locator}'.");
                    }

                    if (!IsSessionActive)
                    {
                        throw new ResourceException(ResourceError.SessionUnavailable, "The session ended during source validation.");
                    }
                }
            }
        }

        /// <summary>按资源类型及不区分大小写的 Resources 路径检测冲突。</summary>
        private sealed class ResourcesPathComparer : IEqualityComparer<(string, Type)>
        {
            /// <summary>比较声明类型和不区分大小写的定位路径。</summary>
            /// <param name="left">第一个路径与声明类型。</param>
            /// <param name="right">第二个路径与声明类型。</param>
            /// <returns>声明类型相同且路径忽略大小写相等时为 true。</returns>
            public bool Equals((string, Type) left, (string, Type) right) => left.Item2 == right.Item2 && StringComparer.OrdinalIgnoreCase.Equals(left.Item1, right.Item1);
            /// <summary>为相同类型及等价路径生成一致哈希。</summary>
            /// <param name="value">参与比较的路径与声明类型。</param>
            /// <returns>忽略路径大小写并包含声明类型的哈希。</returns>
            public int GetHashCode((string, Type) value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item1) ^ value.Item2.GetHashCode();
        }

        /// <summary>同步取得一份独立资产租约；不阻塞等待已经在途的异步生产。</summary>
        /// <typeparam name="T">要借读的 Unity 资源类型，须能接收目录声明类型。</typeparam>
        /// <param name="address">冻结目录中的资源地址。</param>
        /// <returns>由调用方负责归还的租约，共享资源不得自行销毁。</returns>
        /// <exception cref="ResourceException">资源不可取得、类型不符、仍在加载或来源不支持同步生产。</exception>
        public AssetLease<T> Acquire<T>(ResourceAddress address) where T : UnityEngine.Object
        {
            using var request = AcquireRequest<T>(address, true);
            if (request.Status != ResourceRequestStatus.Succeeded)
            {
                throw request.Error;
            }

            return request.TakeResult();
        }

        /// <summary>建立独立消费者请求，同一目录资产复用共享生产及记录。</summary>
        /// <typeparam name="T">要借读的 Unity 资源类型，须能接收目录声明类型。</typeparam>
        /// <param name="address">冻结目录中的资源地址。</param>
        /// <returns>成功后可单次领取资产租约的请求；等待本身不转移租约责任。</returns>
        public ResourceRequest<AssetLease<T>> AcquireAsync<T>(ResourceAddress address) where T : UnityEngine.Object => AcquireRequest<T>(address, false);
        /// <summary>创建绑定本系统的责任域，用于集中管理显式接管的请求和租约。</summary>
        /// <returns>由调用方关闭的空责任域，不预先持有资源。</returns>
        public ResourceScope CreateScope()
        {
            CheckThread();
            if (!IsAvailable)
            {
                throw new ResourceException(ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            return new ResourceScope(this);
        }

        /// <summary>校验会话、项目屏障及资源地址，再将取得意图交给目录条目处理。</summary>
        private ResourceRequest<AssetLease<T>> AcquireRequest<T>(ResourceAddress address, bool synchronous) where T : UnityEngine.Object
        {
            CheckThread();
            var request = new ResourceRequest<AssetLease<T>>(lease => lease.Dispose());
            if (!IsAvailable)
            {
                return Fail(request, ResourceError.SessionUnavailable, "ResourceSystem is not available.", address);
            }

            if (!address.IsValid)
            {
                return Fail(request, ResourceError.InvalidAddress, "A valid resource address is required.", address);
            }

            if (IsProjectBlocked(address.ProjectId))
            {
                return Fail(request, ResourceError.Busy, "The project is inside its activation barrier.", address);
            }

            ResourceCatalogEntry entry;
            ResourceCatalog catalog;
            try
            {
                if (!_catalogs.TryGetValue(address.ProjectId, out catalog))
                {
                    throw new ArgumentException("Unknown resource project.");
                }

                entry = catalog.Resolve(address);
            }
            catch (ArgumentException error)
            {
                return Fail(request, ResourceError.AddressNotFound, $"Unknown resource address: {address}.", address, cause: error);
            }
            return AcquireEntry<T>(catalog, entry, address, synchronous, request);
        }

        /// <summary>复用或建立共享资产记录，为本请求登记独立等待或已就绪租约。</summary>
        private ResourceRequest<AssetLease<T>> AcquireEntry<T>(ResourceCatalog catalog, ResourceCatalogEntry entry,
            ResourceAddress address, bool synchronous, ResourceRequest<AssetLease<T>> request, bool cacheImage = false) where T : UnityEngine.Object
        {
            try
            {
                RequireContentAdmission(entry);
            }
            catch (ResourceException error)
            {
                request.SetFailure(ResourceRequestStatus.Failed, error);
                request.Notify();
                return request;
            }
            if (!typeof(T).IsAssignableFrom(entry.Location.AssetType))
            {
                return Fail(request, ResourceError.TypeMismatch, $"'{address}' declares {entry.Location.AssetType.FullName}, not {typeof(T).FullName}.", address, entry.Location.Source);
            }
            // 当前投影只是可赋值借读；同一目录声明类型的基类请求不重复建立 native producer。
            var key = (catalog, entry.AssetId, entry.Location.AssetType);
            if (_records.TryGetValue(key, out var record))
            {
                if (record.IsLoading && synchronous)
                {
                    return Fail(request, ResourceError.InProgress, $"'{address}' is already loading; use AcquireAsync.", address, entry.Location.Source);
                }

                if (!record.IsLoading)
                {
                    if (!record.Asset)
                    {
                        if (record.LeaseCount != 0)
                        {
                            return Fail(request, ResourceError.LoadFailed, "The cached Unity object was destroyed externally.", address, entry.Location.Source);
                        }

                        ReleaseRecord(record);
                        return AcquireEntry<T>(catalog, entry, address, synchronous, request, cacheImage);
                    }
                    RemoveIdleImage(record);
                    Track(request);
                    request.SetSuccess(new AssetLease<T>(record, address));
                    request.Notify();
                    return request;
                }
            }
            else
            {
                var source = _sources[entry.Location.Source];
                if (synchronous && !source.SupportsSynchronousLoad)
                {
                    return Fail(request, ResourceError.Unsupported, "This source cannot produce synchronously.", address, entry.Location.Source);
                }

                record = new AssetRecord(this, key, entry, source);
                record.IsImageCacheEligible = cacheImage;
                _records.Add(key, record);
            }
            Track(request);
            var waiter = new Waiter<T>(request, address);
            record.Waiters.Add(waiter);
            var loading = record;
            request.Attach(() =>
            {
                loading.Waiters.Remove(waiter);
                CancelUnobservedProducer(loading);
            }, () => loading.Operation?.Progress ?? loading.LastProgress);
            if (record.HasStarted)
            {
                return request;
            }

            record.HasStarted = true;
            try
            {
                if (synchronous)
                {
                    CompleteRecord(record, record.Source.Load(entry, entry.Location.AssetType), null);
                }
                else
                {
                    StartAsyncProducer(record);
                }
            }
            catch (Exception cause)
            {
                // 只捕获来源生产失败；观察者异常已在 Request 中隔离，不伪装成加载失败。
                if (record.IsLoading)
                {
                    CompleteRecord(record, default, cause);
                }
                else
                {
                    throw;
                }
            }
            return request;
        }

        /// <summary>将图片生产加入限流队列，其他来源直接启动异步操作。</summary>
        private void StartAsyncProducer(AssetRecord record)
        {
            if (record.Source is TextureResourceSource)
            {
                record.ImageQueueNode = _imageLoadQueue.AddLast(record);
                PumpImageLoads();
            }
            else
            {
                StartSourceOperation(record);
            }
        }

        /// <summary>接管来源操作和完成订阅，兼容来源即时完成的情况。</summary>
        private void StartSourceOperation(AssetRecord record)
        {
            var operation = record.Source.LoadAsync(record.Entry, record.Entry.Location.AssetType) ?? throw new InvalidOperationException("The source did not return a producer operation.");
            record.Operation = operation;
            record.Completion = () => FinishOperation(record);
            operation.Completed += record.Completion;
            if (operation.IsDone)
            {
                FinishOperation(record);
            }
            else
            {
                CancelUnobservedProducer(record);
            }
        }

        /// <summary>无人等待时移除未启动图片任务，或请求支持取消的来源结束生产。</summary>
        private void CancelUnobservedProducer(AssetRecord record)
        {
            if (record.Waiters.Count == 0 && record.ImageQueueNode != null)
            {
                _imageLoadQueue.Remove(record.ImageQueueNode);
                record.ImageQueueNode = null;
                record.IsLoading = false;
                ReleaseRecord(record);
                CheckDrain();
                return;
            }
            if (record.Waiters.Count != 0 || record.CancelRequested || record.Operation?.SupportsCancellation != true)
            {
                return;
            }
            // 先标记这一代已进入取消收尾，再调用可能同步完成/重入的私有 IO 取消。
            record.CancelRequested = true;
            record.Operation.Cancel();
        }

        /// <summary>跟踪本系统请求，直到失败终态或成功结果的持有责任结束。</summary>
        internal void Track<T>(ResourceRequest<T> request)
        {
            TrackDiagnostics(request);
            _ownedRequests.Add(request);
            request.Terminal = () =>
            {
                if (request.Status == ResourceRequestStatus.Succeeded)
                {
                    return;
                }

                _ownedRequests.Remove(request);
                request.OwnershipEnded = null;
            };
            request.OwnershipEnded = () => _ownedRequests.Remove(request);
        }

        /// <summary>接收来源终态并释放操作；已取消生产有新等待者时，先结清旧结果再重新生产。</summary>
        private void FinishOperation(AssetRecord record)
        {
            if (!record.IsLoading)
            {
                return;
            }

            var operation = record.Operation;
            record.LastProgress = operation.Progress;
            var result = operation.TakeResult();
            var cause = operation.Error;
            operation.Completed -= record.Completion;
            record.Completion = null;
            record.Operation = null;
            operation.Dispose();
            ReleaseImageLoadSlot(record);
            if (record.CancelRequested && record.Waiters.Count != 0 && IsAvailable)
            {
                // 新消费者等待旧 IO 收尾，不接收旧 Abort 结果，也不与它重叠生产。
                try
                {
                    ReleaseProduction(record.Source, result);
                    record.CancelRequested = false;
                    record.LastProgress = 0;
                    StartAsyncProducer(record);
                }
                catch (Exception error)
                {
                    if (record.IsLoading)
                    {
                        CompleteRecord(record, default, error);
                    }
                    else
                    {
                        throw;
                    }
                }
                return;
            }
            CompleteRecord(record, result, cause);
            PumpImageLoads();
        }

        /// <summary>验证生产结果并统一准备消费者终态，结算无主结果后再通知业务。</summary>
        private void CompleteRecord(AssetRecord record, ResourceSourceResult result, Exception cause)
        {
            var asset = result.Asset;
            record.Retention = result.Retention;
            record.IsLoading = false;
            var waiters = record.Waiters.ToArray();
            record.Waiters.Clear();
            ResourceException failure = null;
            if (!IsSessionActive)
            {
                failure = new ResourceException(ResourceError.SessionUnavailable, "The producing session ended.", cause, record.Entry.Address, record.Entry.Location.Source);
            }
            else if (cause != null || !asset)
            {
                failure = new ResourceException(cause is ResourceException resourceError ? resourceError.Kind : ResourceError.LoadFailed,
                    $"Source failed to load '{record.Entry.Address}' from {record.Entry.Location}.", cause, record.Entry.Address, record.Entry.Location.Source);
            }
            else if (!record.Entry.Location.AssetType.IsInstanceOfType(asset))
            {
                failure = new ResourceException(ResourceError.TypeMismatch, $"Source returned {asset.GetType().FullName} for {record.Entry.Location}.", address: record.Entry.Address, source: record.Entry.Location.Source);
            }

            record.Asset = asset;
            // 全部消费者先登记独立 lease 和终态，清生产责任后才允许业务观察重入。
            foreach (var waiter in waiters)
            {
                waiter.Prepare(record, failure);
            }

            RecordProducerCompletion(record, failure, waiters.Length);
            if (failure != null || record.LeaseCount == 0)
            {
                ReleaseRecord(record);
            }

            CheckDrain();
            foreach (var waiter in waiters)
            {
                waiter.Notify();
            }
        }

        /// <summary>同一目录、规范资产及声明类型的共享生产与引用计数记录。</summary>
        internal sealed class AssetRecord
        {
            internal readonly ResourceSystem Owner;
            internal readonly (ResourceCatalog, string, Type) Key;
            internal readonly ResourceCatalogEntry Entry;
            internal readonly IResourceSource Source;
            internal readonly List<IWaiter> Waiters = new();
            internal UnityEngine.Object Asset;
            internal IDisposable Retention;
            internal int LeaseCount;
            internal int InstancePins;
            internal IAssetLeaseDiagnostics LeaseHead;
            internal bool IsImageCacheEligible;
            internal LinkedListNode<AssetRecord> ImageCacheNode;
            internal LinkedListNode<AssetRecord> ImageQueueNode;
            internal bool IsImageLoadRunning;
            internal long ImageBytes;
            internal double IdleSince;
            internal float LastProgress;
            internal bool IsLoading = true;
            internal bool CancelRequested;
            internal bool HasStarted;
            internal ResourceSourceOperation Operation;
            internal Action Completion;
            /// <summary>建立尚未启动的加载记录，绑定唯一来源和资源会话。</summary>
            internal AssetRecord(ResourceSystem owner, (ResourceCatalog, string, Type) key, ResourceCatalogEntry entry, IResourceSource source)
            {
                Owner = owner;
                Key = key;
                Entry = entry;
                Source = source;
            }
        }

        /// <summary>将消费者终态准备与通知分开，避免交付一半时发生业务重入。</summary>
        internal interface IWaiter
        {
            /// <summary>为一个消费者创建租约或登记错误，但不通知观察者。</summary>
            void Prepare(AssetRecord record, ResourceException failure);
            /// <summary>通知已准备终态的消费者。</summary>
            void Notify();
        }

        /// <summary>将共享记录的结果投影为一个指定类型消费者的独立租约。</summary>
        /// <typeparam name="T">该消费者借读的 Unity 资源类型。</typeparam>
        private sealed class Waiter<T> : IWaiter where T : UnityEngine.Object
        {
            private readonly ResourceRequest<AssetLease<T>> _request;
            private readonly ResourceAddress _address;
            /// <summary>保存消费者及其原始地址，不增加资产引用计数。</summary>
            internal Waiter(ResourceRequest<AssetLease<T>> request, ResourceAddress address)
            {
                this._request = request;
                this._address = address;
            }
            /// <summary>仅为仍在等待的请求登记一份租约或错误终态。</summary>
            /// <param name="record">完成生产的共享资产记录。</param>
            /// <param name="failure">生产失败原因；成功时为 null。</param>
            public void Prepare(AssetRecord record, ResourceException failure)
            {
                if (_request.IsDone)
                {
                    return;
                }

                if (failure == null)
                {
                    _request.SetSuccess(new AssetLease<T>(record, _address));
                }
                else
                {
                    _request.SetFailure(failure.Kind == ResourceError.SessionUnavailable ? ResourceRequestStatus.Invalidated : ResourceRequestStatus.Failed, failure);
                }
            }
            /// <summary>派发本消费者的完成通知。</summary>
            public void Notify() => _request.Notify();
        }

        /// <summary>结算一份租约，末份归还时尝试图片缓存或释放；失效公共租约不扣除仍存活的实例 pin。</summary>
        internal void ReleaseLease(AssetRecord record, bool instancePin = false)
        {
            if (!instancePin && !IsSessionActive)
            {
                return; // 退出已结算public责任，不能扣仍在等待Destroy的实例pin。
            }

            if (record.LeaseCount == 0)
            {
                return; // 会话强制退出已结算；旧 lease 不得影响新 record。
            }

            if (instancePin)
            {
                record.InstancePins--;
            }

            if (--record.LeaseCount == 0 && !record.IsLoading && !TryCacheImage(record))
            {
                ReleaseRecord(record);
            }

            CheckDrain();
        }

        /// <summary>移除当前共享记录并清空其持有字段，再归还来源生产结果。</summary>
        private void ReleaseRecord(AssetRecord record)
        {
            RemoveIdleImage(record);
            if (_records.TryGetValue(record.Key, out var current) && ReferenceEquals(current, record))
            {
                _records.Remove(record.Key);
            }

            var asset = record.Asset;
            record.Asset = null;
            record.LeaseCount = 0;
            var retention = record.Retention;
            record.Retention = null;
            ReleaseProduction(record.Source, new ResourceSourceResult(asset, retention));
        }

        /// <summary>归还生产结果的来源对象和附带保活责任。</summary>
        private static void ReleaseProduction(IResourceSource source, ResourceSourceResult result)
        {
            if (!ReferenceEquals(result.Asset, null))
            {
                try
                {
                    source.Release(result.Asset);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                }
            }

            // 上面的 catch 仅隔离 source.Release；Retention.Dispose 失败仍向调用方传播。
            result.Retention?.Dispose();
        }

        /// <summary>live Lease/等待者返回 Busy 并保持可用；零 owner 原生操作由同一 cleanup owner 排空后关闭。</summary>
        /// <returns>正常关闭后结果为 true 的请求；Busy 不会消费仍由调用方持有的责任。</returns>
        /// <remarks>关闭检查会清空闲置图片并退休实例缓存；取消返回请求不撤销已经开始的系统关闭。</remarks>
        public ResourceRequest<bool> ShutdownAsync()
        {
            CheckThread();
            var request = new ResourceRequest<bool>();
            if (State == ResourceSystemState.Closed)
            {
                request.SetSuccess(true);
                return request;
            }
            if (!IsSessionActive)
            {
                return Fail(request, ResourceError.SessionUnavailable, "The resource session is unavailable.");
            }

            if (_isUpdating || _remoteJobs.Count != 0)
            {
                return Fail(request, ResourceError.Busy, "A resource version operation is in progress.");
            }

            if (_isCheckingShutdown)
            {
                return Fail(request, ResourceError.Busy, "Resource shutdown is checking its ownership barrier.");
            }

            if (State == ResourceSystemState.ShuttingDown)
            {
                _shutdowns.Add(request);
                CheckDrain();
                return request;
            }
            State = ResourceSystemState.ShuttingDown;
            _isCheckingShutdown = true;
            try
            {
                ClearImageCache();
                ClearInstanceCaches();
                var busy = _publicBundleLeases.Count != 0 || _sceneLoads.Count != 0 || _sceneRecords.Count != 0;
                foreach (var cache in _instanceCaches)
                {
                    if (cache.HasLiveOwners)
                    {
                        busy = true;
                    }
                }

                foreach (var record in _records.Values)
                {
                    if (record.LeaseCount > record.InstancePins || record.Waiters.Count != 0)
                    {
                        busy = true;
                    }
                }

                if (busy)
                {
                    State = ResourceSystemState.Ready;
                    return Fail(request, ResourceError.Busy, "Resource shutdown is blocked by live leases, instances, Scenes or consumers.");
                }
            }
            catch
            {
                State = ResourceSystemState.Ready;
                throw;
            }
            finally
            {
                _isCheckingShutdown = false;
            }
            _shutdowns.Add(request);
            CheckDrain();
            return request;
        }

        /// <summary>确认全部资源和清理账本排空，再完成正常关闭及等待它的请求。</summary>
        private void CheckDrain()
        {
            if (_isCheckingShutdown)
            {
                return;
            }

            if (_records.Count != 0 || _bundleRecords.Count != 0 || _instanceCaches.Count != 0 || _sceneLoads.Count != 0 || _sceneRecords.Count != 0 || _remoteJobs.Count != 0 ||
                _contentWork.Count != 0 || _retainedContentCleanup.Count != 0 || !_contentFaults.IsEmpty)
            {
                return;
            }

            _bundleQueue.Clear();
            _draining.Remove(this);
            if (State != ResourceSystemState.ShuttingDown)
            {
                return;
            }

            State = ResourceSystemState.Closed;
            UnbindCoreResourceProvider();
            if (ReferenceEquals(_authority, this))
            {
                _authority = null;
            }

            var completed = _shutdowns.ToArray();
            _shutdowns.Clear();
            foreach (var request in completed)
            {
                request.SetSuccess(true);
            }

            foreach (var request in completed)
            {
                request.Notify();
            }
        }

        /// <summary>撤销当前权威并冻结请求，旧实例继续承担原生迟到结果和销毁收尾。</summary>
        private static void EndSession()
        {
            _isEnding = true;
            var system = _authority;
            _authority = null;
            if (system == null)
            {
                return;
            }

            system.State = ResourceSystemState.Invalidated;
            system.CancelContentWork();
            system.UnbindCoreResourceProvider();
            _draining.Add(system);
            var requests = new List<IResourceRequest>(system._ownedRequests);
            system._ownedRequests.Clear();
            foreach (var request in system._initialization)
            {
                requests.Add(request);
            }

            system._initialization.Clear();
            foreach (var request in system._shutdowns)
            {
                requests.Add(request);
            }

            system._shutdowns.Clear();
            // 整批先冻结终态和解绑等待，再释放/通知；观察者取消兄弟或 Dispose Scope 不改变退出原因。
            foreach (var request in requests)
            {
                request.PrepareInvalidation();
            }

            foreach (var request in requests)
            {
                try
                {
                    request.ReleaseInvalidatedResult();
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                }
            }

            system.EndSceneSession();
            foreach (var cache in new List<InstanceCache>(system._instanceCaches))
            {
                cache.EndSession();
            }

            foreach (var record in new List<AssetRecord>(system._records.Values))
            {
                if (!record.IsLoading)
                {
                    if (record.InstancePins == 0)
                    {
                        system.ReleaseRecord(record);
                    }
                    else
                    {
                        record.LeaseCount = record.InstancePins;
                    }
                }
            }

            foreach (var lease in new List<BundleLease>(system._publicBundleLeases))
            {
                lease.Dispose();
            }

            system.CheckDrain();
            foreach (var request in requests)
            {
                request.NotifyInvalidation();
            }
        }

        /// <summary>结束旧资源会话并重新开放执行入口，不丢弃尚未排空的旧清理责任。</summary>
        private static void ResetSession()
        {
            EndSession();
            _isEnding = false;
        }

        /// <summary>采样本系统资产及 Bundle 的逻辑责任计数，不执行加载或回收。</summary>
        /// <returns>当前计数的值快照，不保活其中涉及的资源。</returns>
        public ResourceDiagnosticsSnapshot GetDiagnosticsSnapshot()
        {
            CheckThread();
            var producers = 0;
            var waiters = 0;
            var leases = 0;
            foreach (var record in _records.Values)
            {
                if (record.IsLoading)
                {
                    producers++;
                }
                waiters += record.Waiters.Count;
                leases += record.LeaseCount;
            }
            var roots = 0;
            var edges = 0;
            var queued = 0;
            foreach (var record in _bundleRecords.Values)
            {
                roots += record.RootPins;
                edges += record.References - record.RootPins;
                if (record.State == BundleState.Queued)
                {
                    queued++;
                }
            }
            return new ResourceDiagnosticsSnapshot(_records.Count, producers, waiters, leases, _bundleRecords.Count, roots, edges,
                _runningBundles, queued, _nativeBundleLoads, _nativeBundleUnloads);
        }

        /// <summary>为请求登记标准资源错误并通知，然后返回同一请求。</summary>
        private static ResourceRequest<T> Fail<T>(ResourceRequest<T> request, ResourceError kind, string message,
            ResourceAddress? address = null, ResourceSourceId? source = null, Exception cause = null)
        {
            request.SetFailure(ResourceRequestStatus.Failed, new ResourceException(kind, message, cause, address, source));
            request.Notify();
            return request;
        }

        /// <summary>要求实例操作发生在记录的 Unity 主线程。</summary>
        internal void CheckThread() => AssertThread();
        /// <summary>拒绝在非 Unity 主线程调用资源系统入口。</summary>
        internal static void AssertThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException("ResourceSystem must be used on the Unity main thread.");
            }
        }
    }
}
