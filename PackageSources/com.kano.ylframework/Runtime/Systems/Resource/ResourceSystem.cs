using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using YLFramework.Events;
using YLFramework.Singletons;
using YLFramework.Update;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Resource
{
    /// <summary>明确的 YL 会话状态，不重置共享 Addressables。</summary>
    public enum ResourceSystemState
    {
        /// <summary>本方正在等待官方初始化。</summary>
        Initializing,
        /// <summary>接受新的业务责任。</summary>
        Ready,
        /// <summary>入口冻结，仍允许既有责任归还。</summary>
        ShuttingDown,
        /// <summary>本方责任已经排空并解除绑定。</summary>
        Closed,
        /// <summary>初始化失败，仍需由启动所有者收尾。</summary>
        Failed,
        /// <summary>执行会话结束，仅推进旧责任排空。</summary>
        Invalidated
    }

    /// <summary>Addressables 上的业务所有权服务，不维护 Bundle 图、私有目录或第二个 ResourceManager。</summary>
    public sealed partial class ResourceSystem
    {
        private static ResourceSystem _authority;
        private static int _mainThreadId;
        private static bool _businessStarted;
        private static readonly List<ResourceSystem> DrainingSessions = new();
        private readonly object _executionSession = SingletonSessionState.ExecutionSession;
        private readonly HashSet<IResourceLease> _leases = new();
        private readonly HashSet<IResourceLease> _internalLeases = new();
        private readonly Dictionary<IResourceLease, (int Id, string Key, string Owner)> _leaseInfo = new();
        private readonly Dictionary<(string Key, Type Type), HashSet<IResourceLease>> _readyIndex = new();
        private int _nextLeaseId;
        private int _nextRequestId;
        private readonly HashSet<IResourceRequest> _requests = new();
        private readonly HashSet<ResourceScope> _scopes = new();
        private readonly List<Job> _work = new();
        private readonly List<(Func<bool> Pending, ResourceDrainDiagnostics Details)> _drains = new();
        private readonly List<Action> _ticks = new();
        private readonly ConcurrentQueue<Action> _posted = new();
        private readonly List<ResourceRequest<ResourceSystem>> _starting = new();
        private readonly List<ResourceRequest<bool>> _stopping = new();
        private readonly List<Exception> _cleanupErrors = new();
        private readonly ResourceStartup _startup;
        private IUnregister _update;
        private bool _ended;
        private bool _closingScopes;
        private bool _checkingStop;
        private bool _freezingRequests;

        /// <summary>只借读当前会话；读取本身不初始化。</summary>
        public static ResourceSystem Current => _authority != null &&
            (_authority.State == ResourceSystemState.Ready || _authority.State == ResourceSystemState.ShuttingDown) ? _authority : null;
        /// <summary>本服务当前状态。</summary>
        public ResourceSystemState State { get; private set; } = ResourceSystemState.Initializing;
        /// <summary>本方行为策略，资源事实仍以官方配置为准。</summary>
        public ResourceConfiguration Configuration
        {
            get;
        }
        /// <summary>是否接受新的业务请求。</summary>
        public bool IsAvailable => IsSessionActive && State == ResourceSystemState.Ready && !_catalogApplyInFlight;
        /// <summary>原执行会话仍有效；不等同于服务正在接受新工作。</summary>
        public bool IsSessionActive => !_ended && SingletonSessionState.CanRunExecution && ReferenceEquals(_executionSession, SingletonSessionState.ExecutionSession);
        internal int ActiveLeaseCount => _leases.Count;
        internal bool StartupUpdateWindowOpen => IsAvailable && !_businessStarted;

        static ResourceSystem()
        {
            SingletonSessionState.RegisterSessionReset(ResetSession);
            Application.quitting += EndSession;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode) EndSession();
            };
            EditorApplication.update += () => { if (!Application.isPlaying) TickDrainingSessions(); };
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _businessStarted = false;
        }
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InitializeEditorThread() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;
#endif

        private ResourceSystem(ResourceConfiguration configuration, ResourceStartup startup)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _startup = startup;
            _update = UpdateManager.AddUpdateEvent(Tick);
        }

        internal static ResourceRequest<ResourceSystem> InitializeAsync(ResourceConfiguration configuration, ResourceStartup startup)
        {
            AssertThread();
            if (!SingletonSessionState.CanRunExecution)
                return Failure<ResourceSystem>(new ResourceException(ResourceError.SessionUnavailable, "Resource startup requires an active Play/Player session."));
            if (_authority != null && !ReferenceEquals(_authority._startup, startup))
                return Failure<ResourceSystem>(new ResourceException(ResourceError.ConfigurationConflict, "Another ResourceStartup owns this execution session."));
            if (_authority == null)
                _authority = new ResourceSystem(configuration, startup);
            var system = _authority;
            if (system.IsAvailable)
                return Success(system);
            if (system.State != ResourceSystemState.Initializing)
                return Failure<ResourceSystem>(new ResourceException(ResourceError.Busy, "The resource service must finish closing before restart."));
            var request = new ResourceRequest<ResourceSystem>();
            system._starting.Add(request);
            request.Attach(() => system._starting.Remove(request), null);
            if (system._starting.Count == 1 && system._work.Count == 0)
                system.Run(system.InitializeOfficial(), system.FailInitialization);
            return request;
        }

        private IEnumerator InitializeOfficial()
        {
            if (State != ResourceSystemState.Initializing)
                throw new ResourceException(ResourceError.Canceled, "Startup was stopped before initialization began.");
            yield return PrepareCatalogUpdatePolicy();
            if (State != ResourceSystemState.Initializing || !IsSessionActive)
                throw new ResourceException(ResourceError.Canceled, "Startup was stopped before official initialization began.");
            var handle = Addressables.InitializeAsync(true);
            var pin = AddressablesHandleOwner<UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator>.Observe(Addressables.ResourceManager, handle);
            try
            {
                while (!handle.IsDone)
                    yield return null;
                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw handle.OperationException ?? new InvalidOperationException("Addressables initialization failed.");
                if (State != ResourceSystemState.Initializing || _ended)
                    throw new ResourceException(ResourceError.Canceled, "Startup was stopped during initialization.");
                InitializeImageProvider();
                State = ResourceSystemState.Ready;
            }
            finally { DisposeOwner(pin); }
            var waiters = _starting.ToArray();
            _starting.Clear();
            foreach (var request in waiters)
            {
                request.SetSuccess(this);
                request.Notify();
            }
        }

        private void FailInitialization(Exception error)
        {
            if (State == ResourceSystemState.Initializing)
                State = ResourceSystemState.Failed;
            var waiters = _starting.ToArray();
            _starting.Clear();
            foreach (var request in waiters)
            {
                request.SetFailure(ResourceRequestStatus.Failed, Wrap(error));
                request.Notify();
            }
        }

        /// <summary>创建明确的业务 Scope，不加载资源。</summary>
        public ResourceScope CreateScope(string ownerLabel)
        {
            RequireAvailable();
            if (string.IsNullOrWhiteSpace(ownerLabel))
                throw new ArgumentException("A business owner label is required.", nameof(ownerLabel));
            var scope = new ResourceScope(this, ownerLabel);
            _scopes.Add(scope);
            return scope;
        }

        /// <summary>以官方唯一地址异步取得独立租约，取消只影响本次消费者。</summary>
        public ResourceRequest<AssetLease<T>> AcquireAsync<T>(string address, CancellationToken cancellation = default) where T : UnityEngine.Object =>
            AcquireAssetAsync<T>(address, cancellation);

        internal ResourceRequest<AssetLease<T>> AcquireAssetAsync<T>(string address, CancellationToken cancellation,
            ResourceScope scope = null) where T : UnityEngine.Object
        {
            CheckThread();
            var request = new ResourceRequest<AssetLease<T>>(lease => lease.Dispose());
            if (!IsAvailable)
                return Fail(request, AdmissionError());
            Track(request, address, operation: nameof(AcquireAsync));
            if (string.IsNullOrWhiteSpace(address))
                return Fail(request, new ResourceException(ResourceError.InvalidAddress, "An exact Addressables key is required."));
            _businessStarted = true;
            BindCancellation(request, cancellation);
            if (!request.IsDone)
            {
                scope?.BeginAssetWork();
                Run(AcquireRoutine(address, request, scope), error => Fail(request, error));
            }
            return request;
        }

        /// <summary>冻结官方 AssetReference 的 RuntimeKey，不占用其单一操作槽。</summary>
        public ResourceRequest<AssetLease<T>> AcquireAsync<T>(AssetReferenceT<T> reference, CancellationToken cancellation = default) where T : UnityEngine.Object =>
            AcquireAsync<T>(ReferenceKey(reference), cancellation);

        /// <summary>业务命名空间的一次投影；不挂载私有 Catalog。</summary>
        public ResourceRequest<AssetLease<T>> AcquireAsync<T>(ResourceAddress address) where T : UnityEngine.Object => AcquireAsync<T>(ProjectKey(address));

        private IEnumerator AcquireRoutine<T>(string key, ResourceRequest<AssetLease<T>> request,
            ResourceScope scope) where T : UnityEngine.Object
        {
            AddressablesHandleOwner<IList<IResourceLocation>> owner = null;
            try
            {
                if (request.IsDone)
                    yield break;
                var handle = Addressables.LoadResourceLocationsAsync(key, typeof(T));
                owner = AddressablesHandleOwner<IList<IResourceLocation>>.Take(Addressables.ResourceManager, handle);
                while (!handle.IsDone && !request.IsDone)
                    yield return null;
                if (request.IsDone)
                    yield break;
                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw handle.OperationException ?? new InvalidOperationException("Location resolution failed: " + key);
                if (handle.Result.Count != 1)
                    throw new ResourceException(handle.Result.Count == 0 ? ResourceError.AddressNotFound : ResourceError.InvalidAddress,
                        "An asset key must resolve to exactly one location: " + key);
                yield return AcquireLocationRoutine(handle.Result[0], key, request, scope);
            }
            finally
            {
                try
                {
                    DisposeOwner(owner, request.DiagnosticIdentity, scope);
                }
                finally { scope?.EndAssetWork(); }
            }
        }

        private IEnumerator AcquireLocationRoutine<T>(IResourceLocation location, string key, ResourceRequest<AssetLease<T>> request,
            ResourceScope scope = null) where T : UnityEngine.Object
        {
            var handle = Addressables.ResourceManager.ProvideResource<T>(location);
            AddressablesHandleOwner<T> owner = AddressablesHandleOwner<T>.Take(Addressables.ResourceManager, handle);
            try
            {
                request.SetProgressReader(() => handle.IsValid() ? handle.PercentComplete : 0);
                while (!handle.IsDone && !request.IsDone)
                    yield return null;
                if (request.IsDone)
                    yield break;
                RequireAvailable();
                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw handle.OperationException ?? new InvalidOperationException("Asset loading failed: " + key);
                if (!handle.Result)
                    throw new ResourceException(ResourceError.LoadFailed, "The loaded Unity object is missing: " + key);
                var lease = new AssetLease<T>(this, key, owner, location);
                owner = null;
                request.SetSuccess(lease);
                request.Notify();
            }
            finally { DisposeOwner(owner, request.DiagnosticIdentity, scope); }
        }

        /// <summary>只从 YL 已持有的就绪引用增持；不会阻塞、加载或查询官方私有缓存。</summary>
        public bool TryAcquireLoaded<T>(string key, out AssetLease<T> lease) where T : UnityEngine.Object
        {
            RequireAvailable();
            if (!_readyIndex.TryGetValue((key, typeof(T)), out var candidates))
            {
                lease = null;
                return false;
            }
            foreach (var candidate in candidates.Cast<AssetLease<T>>())
            {
                if (candidate.Key != key || !candidate.IsValid)
                    continue;
                lease = candidate.Retain();
                return true;
            }
            lease = null;
            return false;
        }

        internal static string ReferenceKey(AssetReference reference)
        {
            if (reference == null || !reference.RuntimeKeyIsValid())
                throw new ArgumentException("A valid AssetReference is required.");
            return reference.RuntimeKey as string ?? throw new ArgumentException("The AssetReference runtime key is not supported.");
        }

        internal static string ProjectKey(ResourceAddress address)
        {
            if (!address.IsValid || !string.IsNullOrEmpty(address.SubAsset))
                throw new ResourceException(ResourceError.InvalidAddress, "Use an official AssetReference for subobjects.");
            return address.ProjectId + "/" + address.Path;
        }

        internal void BindCancellation<T>(ResourceRequest<T> request, CancellationToken token)
        {
            request.Cancellation = token;
            if (token.IsCancellationRequested)
            {
                request.Cancel();
                return;
            }
            if (!token.CanBeCanceled)
                return;
            var registration = token.Register(() => Post(() => { if (!request.IsDone) request.Cancel(); }));
            request.Terminal += registration.Dispose;
        }

        internal ResourceRequest<T> Track<T>(ResourceRequest<T> request, string key = null, string owner = null, [CallerMemberName] string operation = null)
        {
            CheckThread();
            if (State != ResourceSystemState.Ready || !IsSessionActive)
                return request;
            if (!_requests.Add(request))
                return request;
            request.DiagnosticId = ++_nextRequestId;
            request.DiagnosticIdentity = (operation, key, owner);
            request.OwnershipEnded += () => _requests.Remove(request);
            return request;
        }

        internal void RegisterLease(IResourceLease lease, string key = null, string ownerLabel = null)
        {
            CheckThread();
            if (!_leases.Add(lease))
                return;
            _leaseInfo.Add(lease, (++_nextLeaseId, key, ownerLabel));
            if (lease is IResourceAssetLease asset)
            {
                var indexKey = (asset.Key, asset.AssetType);
                if (!_readyIndex.TryGetValue(indexKey, out var values))
                    _readyIndex.Add(indexKey, values = new HashSet<IResourceLease>());
                values.Add(lease);
            }
        }
        internal void UnregisterLease(IResourceLease lease)
        {
            CheckThread();
            _leases.Remove(lease);
            _internalLeases.Remove(lease);
            _leaseInfo.Remove(lease);
            lease.Scope?.ForgetLease(lease);
            if (lease is IResourceAssetLease asset && _readyIndex.TryGetValue((asset.Key, asset.AssetType), out var values))
            {
                values.Remove(lease);
                if (values.Count == 0)
                    _readyIndex.Remove((asset.Key, asset.AssetType));
            }
        }
        internal void MakeInternal(IResourceLease lease)
        {
            CheckThread();
            _internalLeases.Add(lease);
        }
        internal void MakeExternal(IResourceLease lease)
        {
            CheckThread();
            _internalLeases.Remove(lease);
        }
        internal void ScopeSettled(ResourceScope scope) => _scopes.Remove(scope);
        internal void Post(Action action) => _posted.Enqueue(action);

        internal IDisposable AddTick(Action action)
        {
            CheckThread();
            _ticks.Add(action);
            return new TickRegistration(this, action);
        }

        internal void Run(IEnumerator routine, Action<Exception> failed = null)
        {
            CheckThread();
            _work.Add(new Job(routine, failed));
        }

        private void Tick()
        {
            if (!IsSessionActive && !_ended)
                Invalidate();
            AdvanceCleanup();
            CheckStop();
            TickDrainingSessions();
        }

        private void AdvanceCleanup()
        {
            int count = _posted.Count;
            while (count-- > 0 && _posted.TryDequeue(out var action))
                InvokeCleanup(action);
            foreach (var job in _work.ToArray())
            {
                try
                {
                    if (job.MoveNext())
                        continue;
                }
                catch (Exception error) { if (job.Failed != null) job.Failed(error); else RecordCleanupError(error); }
                finally { if (job.Finished) _work.Remove(job); }
            }
            foreach (var tick in _ticks.ToArray())
                InvokeCleanup(tick);
            _drains.RemoveAll(drain => !drain.Pending());
        }

        private void InvokeCleanup(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error) { RecordCleanupError(error); }
        }

        private void RecordCleanupError(Exception error)
        {
            _cleanupErrors.Add(error);
            Debug.LogException(error);
        }

        private void DisposeOwner<T>(AddressablesHandleOwner<T> owner,
            (string Operation, string Key, string Owner) identity = default, ResourceScope scope = null)
        {
            if (owner == null)
                return;
            owner.Dispose();
            if (!owner.IsDraining)
                return;
            // Resolve/provide 工作退出前，把尚未归还的同一份引用计入来源 Scope 的排空。
            scope?.BeginAssetWork();
            _drains.Add((() =>
            {
                if (owner.IsDraining)
                    return true;
                scope?.EndAssetWork();
                return false;
            }, new ResourceDrainDiagnostics(identity.Operation ?? "Initialize", identity.Key, scope?.OwnerLabel ?? identity.Owner)));
        }

        internal ResourceRequest<bool> ShutdownAsync(ResourceStartup startup)
        {
            CheckThread();
            if (!ReferenceEquals(_startup, startup))
                return Failure<bool>(new ResourceException(ResourceError.ConfigurationConflict, "Only the startup owner may stop this service."));
            if (State == ResourceSystemState.Closed)
                return Success(true);
            if (!IsSessionActive)
                return Failure<bool>(new ResourceException(ResourceError.SessionUnavailable, "The resource session ended."));
            var request = new ResourceRequest<bool>();
            _stopping.Add(request);
            if (State != ResourceSystemState.ShuttingDown)
            {
                State = ResourceSystemState.ShuttingDown;
                FreezeRequests();
            }
            CheckStop();
            return request;
        }

        private void CheckStop()
        {
            if (_checkingStop || _freezingRequests || State != ResourceSystemState.ShuttingDown || _stopping.Count == 0)
                return;
            _checkingStop = true;
            try
            {
                var external = _leases.Where(lease => !lease.IsSettled && lease.Scope == null && !_internalLeases.Contains(lease)).ToArray();
                if (external.Length != 0)
                {
                    var blockers = external.Select(lease => _leaseInfo[lease]).OrderBy(info => info.Id)
                        .Select(info => $"#{info.Id} key='{info.Key ?? "(unknown)"}' owner='{info.Owner ?? "(unlabeled)"}'");
                    CompleteStops(new ResourceException(ResourceError.Busy,
                        $"{external.Length} independent resource leases still belong to their callers: {string.Join("; ", blockers)}."));
                    return;
                }
                if (!_closingScopes)
                {
                    _closingScopes = true;
                    RetrySceneCleanup();
                    foreach (var scope in _scopes.ToArray())
                        scope.Dispose();
                    RetireInstances();
                    RetireImages();
                }
                var failure = _scopes.Select(scope => scope.CloseError).FirstOrDefault(error => error != null) ?? SceneCleanupError;
                if (failure != null)
                {
                    _closingScopes = false;
                    CompleteStops(failure);
                    return;
                }
                if (_leases.Count != 0 || _work.Count != 0 || _drains.Count != 0 || HasInstanceWork || HasImageWork)
                    return;
                if (_cleanupErrors.Count != 0)
                {
                    CompleteStops(new AggregateException("Resource cleanup failed.", _cleanupErrors));
                    return;
                }
                State = ResourceSystemState.Closed;
                _ended = true;
                UnbindCoreResourceProvider();
                _update?.Unregister();
                _update = null;
                if (ReferenceEquals(_authority, this))
                    _authority = null;
                _startup.OnServiceStopped(this);
                CompleteStops(null);
            }
            finally { _checkingStop = false; }
        }

        private void CompleteStops(Exception error)
        {
            foreach (var request in SetStopResults(error))
                request.Notify();
        }

        private ResourceRequest<bool>[] SetStopResults(Exception error, ResourceRequestStatus status = ResourceRequestStatus.Failed)
        {
            var requests = _stopping.ToArray();
            _stopping.Clear();
            var cause = error == null ? null : Wrap(error);
            foreach (var request in requests)
            {
                if (cause == null)
                    request.SetSuccess(true);
                else
                    request.SetFailure(status, cause);
            }
            return requests;
        }

        private void FreezeRequests()
        {
            if (_freezingRequests)
                return;
            _freezingRequests = true;
            try
            {
                var requests = _requests.ToArray();
                foreach (var request in requests)
                    request.PrepareInvalidation();
                foreach (var request in requests)
                    request.ReleaseInvalidatedResult();
                foreach (var request in requests)
                    request.NotifyInvalidation();
            }
            finally { _freezingRequests = false; }
        }

        private void Invalidate()
        {
            if (_ended)
                return;
            _ended = true;
            State = ResourceSystemState.Invalidated;
            var stopping = SetStopResults(new ResourceException(ResourceError.SessionUnavailable, "The resource session ended."),
                ResourceRequestStatus.Invalidated);
            FreezeRequests();
            foreach (var scope in _scopes.ToArray())
                scope.Dispose();
            foreach (var lease in _leases.ToArray())
                if (!IsInstancePin(lease))
                    InvokeCleanup(lease.Dispose);
            RetireInstances();
            RetireImages();
            UnbindCoreResourceProvider();
            _update?.Unregister();
            _update = null;
            if (!DrainingSessions.Contains(this))
                DrainingSessions.Add(this);
            if (ReferenceEquals(_authority, this))
                _authority = null;
            foreach (var request in stopping)
                request.Notify();
        }

        private static void ResetSession()
        {
            EndSession();
            _businessStarted = false;
        }
        private static void EndSession()
        {
            _authority?.Invalidate();
        }
        private static void TickDrainingSessions()
        {
            foreach (var system in DrainingSessions.ToArray())
            {
                system.AdvanceCleanup();
                if (system._leases.Count == 0 && system._work.Count == 0 && system._drains.Count == 0 && !system.HasInstanceWork && !system.HasImageWork)
                {
                    DrainingSessions.Remove(system);
                    system._startup.OnServiceStopped(system);
                }
            }
        }

        internal void RequireAvailable()
        {
            CheckThread();
            if (!IsAvailable)
                throw AdmissionError();
        }

        private ResourceException AdmissionError() => _catalogApplyInFlight && IsSessionActive && State == ResourceSystemState.Ready
            ? new ResourceException(ResourceError.Busy, "The official startup catalog update has not settled.")
            : new ResourceException(ResourceError.SessionUnavailable, "ResourceSystem is not accepting new work.");

        internal void CheckThread() => AssertThread();
        internal static ResourceSystem GetOwnedSystem(ResourceStartup startup) => _authority != null && ReferenceEquals(_authority._startup, startup) ? _authority : null;
        internal static void CheckStartupThread() => AssertThread();
        private static void AssertThread()
        {
            if (_mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("Resource operations require the Unity main thread.");
        }

        internal static ResourceException Wrap(Exception error) => error as ResourceException ??
            new ResourceException(ResourceError.LoadFailed, error.Message, error);
        internal static ResourceRequest<T> Success<T>(T value)
        {
            var request = new ResourceRequest<T>();
            request.SetSuccess(value);
            return request;
        }
        internal static ResourceRequest<T> Failure<T>(Exception error) => Fail(new ResourceRequest<T>(), error);
        internal static ResourceRequest<T> Fail<T>(ResourceRequest<T> request, Exception error)
        {
            if (!request.IsDone)
            {
                request.SetFailure(ResourceRequestStatus.Failed, Wrap(error));
                request.Notify();
            }
            return request;
        }

        private sealed class TickRegistration : IDisposable
        {
            private ResourceSystem _system;
            private readonly Action _callback;
            internal TickRegistration(ResourceSystem system, Action callback)
            {
                _system = system;
                _callback = callback;
            }
            public void Dispose()
            {
                _system?.CheckThread();
                _system?._ticks.Remove(_callback);
                _system = null;
            }
        }

        private sealed class Job : IDisposable
        {
            private readonly Stack<IEnumerator> _stack = new();
            private object _waiting;
            internal readonly Action<Exception> Failed;
            internal bool Finished
            {
                get; private set;
            }
            internal Job(IEnumerator routine, Action<Exception> failed)
            {
                _stack.Push(routine);
                Failed = failed;
            }
            internal bool MoveNext()
            {
                try
                {
                    if (_waiting is CustomYieldInstruction custom && custom.keepWaiting)
                        return true;
                    if (_waiting is AsyncOperation native && !native.isDone)
                        return true;
                    _waiting = null;
                    while (_stack.Count != 0)
                    {
                        var current = _stack.Peek();
                        if (!current.MoveNext())
                        {
                            (current as IDisposable)?.Dispose();
                            _stack.Pop();
                            continue;
                        }
                        if (current.Current is IEnumerator nested && current.Current is not CustomYieldInstruction)
                        {
                            _stack.Push(nested);
                            continue;
                        }
                        _waiting = current.Current;
                        return true;
                    }
                    Finished = true;
                    return false;
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                Finished = true;
                while (_stack.Count != 0)
                    (_stack.Pop() as IDisposable)?.Dispose();
            }
        }
    }
}
