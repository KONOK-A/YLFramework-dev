using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private readonly Dictionary<(string, GameObject, InstanceCachePolicy, int), InstanceCache>
            _openInstanceCaches = new();
        private readonly List<InstanceCache> _instanceCaches = new();
        // 每个请求持有一份例程责任及未交付 Entry 责任；取消状态不代表这些责任已经结束。
        private readonly Dictionary<ResourceRequest<InstanceLease>, int> _pendingInstanceRequests = new();
        private Transform _instanceRoot;
        private IDisposable _instanceTick;
        private int _instanceOperations;
        private int _instanceCleanupIndex;
        private long _instanceCleanupEpoch;
        private bool _isPruningInstances;

        /// <summary>异步取得官方 Prefab，每次新建；取消或未领取的对象由请求丢弃。</summary>
        public ResourceRequest<InstanceLease> InstantiateAsync(string key, Transform parent = null,
            Scene scene = default, CancellationToken cancellation = default)
            => CreateInstanceAsync(key, parent, scene, cancellation, false);

        /// <summary>异步取得官方 Prefab，明确复用空闲实例，不足时才创建。</summary>
        public ResourceRequest<InstanceLease> RentAsync(string key, Transform parent = null,
            Scene scene = default, CancellationToken cancellation = default)
            => CreateInstanceAsync(key, parent, scene, cancellation, true);

        /// <summary>仅用本系统已持有的 Prefab 新建；不加载、不等 IO，原 Lease 仍由调用者持有。</summary>
        public InstanceLease Instantiate(AssetLease<GameObject> prefab, Transform parent = null, Scene scene = default)
            => CreateInstance(prefab, parent, scene, false);

        /// <summary>仅用本系统已持有的 Prefab 复用或新建；不会自动重置业务状态。</summary>
        public InstanceLease Rent(AssetLease<GameObject> prefab, Transform parent = null, Scene scene = default)
            => CreateInstance(prefab, parent, scene, true);

        private InstanceLease CreateInstance(AssetLease<GameObject> prefab, Transform parent, Scene scene, bool reusable)
        {
            RequireAvailable();
            scene = InstanceCache.TargetScene(parent, scene);
            var cache = GetOrCreateInstanceCacheCore(prefab, InstanceCachePolicy.SceneScoped, scene);
            using var reservation = cache.Reserve(reusable, parent, scene, null);
            return reservation.Deliver();
        }

        private ResourceRequest<InstanceLease> CreateInstanceAsync(string key, Transform parent, Scene scene,
            CancellationToken cancellation, bool reusable)
        {
            CheckThread();
            try
            {
                RequireAvailable();
                scene = InstanceCache.TargetScene(parent, scene);
            }
            catch (Exception error)
            {
                return Failure<InstanceLease>(error);
            }
            var request = Track(new ResourceRequest<InstanceLease>(lease => lease.Discard()), key,
                operation: reusable ? nameof(RentAsync) : nameof(InstantiateAsync));
            _pendingInstanceRequests.Add(request, 1);
            _businessStarted = true;
            try
            {
                BindCancellation(request, cancellation);
                Run(CreateInstanceRoutine(request, key, parent, scene, cancellation, reusable));
            }
            catch (Exception error)
            {
                ReleasePendingInstanceRequest(request);
                FailInstanceRequest(request, error);
                request.NotifyCompletion();
            }
            return request;
        }

        private IEnumerator CreateInstanceRoutine(ResourceRequest<InstanceLease> request, string key,
            Transform parent, Scene scene, CancellationToken cancellation, bool reusable)
        {
            _instanceOperations++;
            ResourceRequest<AssetLease<GameObject>> loading = null;
            AssetLease<GameObject> prefab = null;
            InstanceCache.Reservation reservation = null;
            try
            {
                if (request.IsDone)
                {
                    yield break;
                }
                // 调用方先取得请求，Scope 才能在 Awake/OnEnable 重入时取消这一轮。
                yield return null;
                if (cancellation.IsCancellationRequested)
                {
                    request.Cancel();
                }
                if (request.IsDone)
                {
                    yield break;
                }
                try
                {
                    RequireAvailable();
                    loading = AcquireAsync<GameObject>(key, cancellation);
                    request.Attach(() => loading.Dispose(), () => loading.Progress);
                }
                catch (Exception error)
                {
                    FailInstanceRequest(request, error);
                }
                if (request.IsDone)
                {
                    yield break;
                }
                while (!loading.IsDone && !request.IsDone)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        request.Cancel();
                    }
                    yield return null;
                }
                if (request.IsDone)
                {
                    yield break;
                }
                try
                {
                    prefab = loading.TakeResult();
                    MakeInternal(prefab);
                    loading.Dispose();
                    if (InstanceCache.TargetScene(parent, scene) != scene)
                    {
                        throw new ArgumentException("The instance parent changed its target scene while loading.");
                    }
                    var cache = GetOrCreateInstanceCacheCore(prefab, InstanceCachePolicy.SceneScoped, scene);
                    reservation = cache.Reserve(reusable, parent, scene,
                        () => !request.IsDone && !cancellation.IsCancellationRequested, request);
                    prefab.Dispose();
                    prefab = null;
                }
                catch (Exception error)
                {
                    FailInstanceRequest(request, error);
                }
                if (request.IsDone)
                {
                    yield break;
                }
                // Unity 的 Object.Destroy 在回调当帧仍可能为真，异步交割等实际死亡边界后再复核。
                yield return null;
                if (cancellation.IsCancellationRequested)
                {
                    request.Cancel();
                }
                if (!request.IsDone)
                {
                    try
                    {
                        var lease = reservation.Deliver();
                        request.ValidateBeforeTake = () =>
                        {
                            if (!IsAvailable || !lease.IsValid)
                            {
                                request.Dispose();
                                throw new ResourceException(ResourceError.SessionUnavailable,
                                    "The instance ended before its result was claimed.");
                            }
                        };
                        request.SetSuccess(lease);
                        _pendingInstanceRequests.Remove(request);
                    }
                    catch (Exception error)
                    {
                        FailInstanceRequest(request, error);
                    }
                }
            }
            finally
            {
                try
                {
                    reservation?.Dispose();
                }
                finally
                {
                    try
                    {
                        prefab?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            loading?.Dispose();
                        }
                        finally
                        {
                            _instanceOperations--;
                            ReleasePendingInstanceRequest(request);
                            request.NotifyCompletion();
                        }
                    }
                }
            }
        }

        private static void FailInstanceRequest(ResourceRequest<InstanceLease> request, Exception error)
        {
            if (error is OperationCanceledException || error is ResourceException { Kind: ResourceError.Canceled })
            {
                request.Cancel();
                return;
            }
            request.SetFailure(ResourceRequestStatus.Failed, error as ResourceException ??
                new ResourceException(ResourceError.LoadFailed, "Prefab instance creation failed.", error));
        }

        /// <summary>取得或创建共享实例缓存并保留空池；新缓存会独立增持 Prefab，不消费传入租约。</summary>
        public InstanceCache GetOrCreateInstanceCache(AssetLease<GameObject> prefab,
            InstanceCachePolicy policy = InstanceCachePolicy.SceneScoped, Scene scene = default)
        {
            var cache = GetOrCreateInstanceCacheCore(prefab, policy, scene);
            cache.KeepEmpty();
            return cache;
        }

        private InstanceCache GetOrCreateInstanceCacheCore(AssetLease<GameObject> prefab, InstanceCachePolicy policy, Scene scene)
        {
            RequireAvailable();
            if (prefab == null)
            {
                throw new ArgumentNullException(nameof(prefab));
            }
            var asset = prefab.Asset;
            if (!ReferenceEquals(prefab.System, this) || asset.scene.IsValid())
            {
                throw new ArgumentException("A Prefab asset lease from this resource system is required.", nameof(prefab));
            }
            if (policy != InstanceCachePolicy.SceneScoped && policy != InstanceCachePolicy.Persistent)
            {
                throw new ArgumentOutOfRangeException(nameof(policy));
            }
            if (policy == InstanceCachePolicy.SceneScoped)
            {
                scene = InstanceCache.TargetScene(null, scene);
            }
            var key = (prefab.Key, asset, policy, policy == InstanceCachePolicy.Persistent ? 0 : (int)scene.handle);
            if (_openInstanceCaches.TryGetValue(key, out var cache))
            {
                cache.Prune(8);
                if (!cache.IsRetired)
                {
                    return cache;
                }
            }
            AssetLease<GameObject> pin = null;
            InstanceCache created = null;
            _instanceOperations++;
            try
            {
                pin = prefab.Retain("Resource instance cache");
                MakeInternal(pin);
                if (!_instanceRoot)
                {
                    _instanceRoot = new GameObject("YL Resource Instance Cache").transform;
                    UnityEngine.Object.DontDestroyOnLoad(_instanceRoot.gameObject);
                }
                cache = created = new InstanceCache(this, pin, policy, scene, _instanceRoot);
                _openInstanceCaches.Add(key, cache);
                _instanceCaches.Add(cache);
                if (_instanceTick == null)
                {
                    SceneManager.sceneUnloaded += OnInstanceSceneUnloaded;
                    _instanceTick = AddTick(TickInstanceCleanup);
                }
                RequireAvailable();
                return cache;
            }
            catch
            {
                if (created != null)
                {
                    created.Dispose();
                }
                else
                {
                    pin?.Dispose();
                    if (_instanceCaches.Count == 0 && _instanceRoot)
                    {
                        UnityEngine.Object.Destroy(_instanceRoot.gameObject);
                        _instanceRoot = null;
                    }
                }
                throw;
            }
            finally
            {
                _instanceOperations--;
            }
        }

        /// <summary>退休当前实例缓存并清除空闲实例；已借出的实例在归还时销毁。</summary>
        public void RetireInstanceCaches()
        {
            CheckThread();
            List<Exception> errors = null;
            foreach (var cache in new List<InstanceCache>(_openInstanceCaches.Values))
            {
                try
                {
                    cache.Dispose();
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
            }
            if (errors != null)
            {
                throw new AggregateException("Instance cache retirement failed.", errors);
            }
        }

        internal bool HasInstanceWork => _pendingInstanceRequests.Count != 0 ||
            _instanceOperations != 0 || _instanceCaches.Count != 0;
        internal long InstanceCleanupEpoch => _instanceCleanupEpoch;

        internal bool HasPendingInstanceRequest(ResourceRequest<InstanceLease> input)
        {
            CheckThread();
            return input != null && _pendingInstanceRequests.ContainsKey(input);
        }

        internal void TrackPendingInstanceEntry(ResourceRequest<InstanceLease> input, InstanceCache.Entry entry)
        {
            if (input == null || entry.PendingRequest != null || !_pendingInstanceRequests.TryGetValue(input, out var count))
            {
                return;
            }
            entry.PendingRequest = input;
            _pendingInstanceRequests[input] = count + 1;
        }

        internal void SettlePendingInstanceEntry(InstanceCache.Entry entry)
        {
            var input = entry.PendingRequest;
            entry.PendingRequest = null;
            if (input != null)
            {
                ReleasePendingInstanceRequest(input);
            }
        }

        private void ReleasePendingInstanceRequest(ResourceRequest<InstanceLease> input)
        {
            if (!_pendingInstanceRequests.TryGetValue(input, out var count))
            {
                return;
            }
            if (count == 1)
            {
                _pendingInstanceRequests.Remove(input);
            }
            else
            {
                _pendingInstanceRequests[input] = count - 1;
            }
        }

        internal bool IsInstancePin(IResourceLease lease)
        {
            foreach (var cache in _instanceCaches)
            {
                if (cache.OwnsPrefabPin(lease))
                {
                    return true;
                }
            }
            return false;
        }

        private void OnInstanceSceneUnloaded(Scene scene)
        {
            foreach (var cache in new List<InstanceCache>(_openInstanceCaches.Values))
            {
                if (cache.Policy == InstanceCachePolicy.SceneScoped && cache.ScopeScene == scene)
                {
                    cache.Dispose();
                }
            }
        }

        internal void RetireInstanceCache(InstanceCache cache)
        {
            var key = (cache.Key, cache.Prefab, cache.Policy,
                cache.Policy == InstanceCachePolicy.Persistent ? 0 : (int)cache.ScopeScene.handle);
            if (_openInstanceCaches.TryGetValue(key, out var current) && ReferenceEquals(current, cache))
            {
                _openInstanceCaches.Remove(key);
            }
        }

        internal void ReleaseInstanceCache(InstanceCache cache)
        {
            _instanceCaches.Remove(cache);
            if (_instanceCaches.Count != 0)
            {
                return;
            }
            SceneManager.sceneUnloaded -= OnInstanceSceneUnloaded;
            _instanceTick?.Dispose();
            _instanceTick = null;
            if (_instanceRoot)
            {
                UnityEngine.Object.Destroy(_instanceRoot.gameObject);
            }
            _instanceRoot = null;
        }

        private void TickInstanceCleanup()
        {
            if (_isPruningInstances)
            {
                return;
            }
            _isPruningInstances = true;
            try
            {
                _instanceCleanupEpoch++;
                var count = Math.Min(8, _instanceCaches.Count);
                for (var i = 0; i < count && _instanceCaches.Count != 0; i++)
                {
                    if (_instanceCleanupIndex >= _instanceCaches.Count)
                    {
                        _instanceCleanupIndex = 0;
                    }
                    var cache = _instanceCaches[_instanceCleanupIndex];
                    cache.Prune(8);
                    if (_instanceCleanupIndex < _instanceCaches.Count &&
                        ReferenceEquals(_instanceCaches[_instanceCleanupIndex], cache))
                    {
                        _instanceCleanupIndex++;
                    }
                }
            }
            finally
            {
                _isPruningInstances = false;
            }
        }
    }
}
