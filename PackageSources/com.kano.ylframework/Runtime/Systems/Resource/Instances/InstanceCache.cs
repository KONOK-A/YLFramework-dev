using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Pooling;
using Object = UnityEngine.Object;

namespace YLFramework.Resource
{
    /// <summary>缓存桶的寿命策略；不自动改变借出实例的场景归属。</summary>
    public enum InstanceCachePolicy
    {
        /// <summary>绑定已加载场景，卸载该场景时退休。</summary>
        SceneScoped,
        /// <summary>闲置缓存跨场景保留，借出对象不会自动变为 DontDestroyOnLoad。</summary>
        Persistent
    }

    /// <summary>Foundation 管 FIFO/容量，Resource 管实例身份和独立 Prefab pin，退休不撤销借出。</summary>
    public sealed class InstanceCache : IDisposable
    {
        internal sealed class Entry
        {
            internal GameObject Object;
            internal InstanceLease Checkout;
            internal InstanceLease Closing;
            internal ResourceRequest<InstanceLease> PendingRequest;
            internal bool CanReturn;
            internal bool IsDestroying;
            internal long DeathObservedAt = -1;
        }

        // 预约覆盖所有 Unity/Foundation 回调及异步存活复核；只有交割后才成为独立 Lease。
        internal sealed class Reservation : IDisposable
        {
            private InstanceCache _cache;
            private readonly Entry _entry;
            private readonly Transform _parent;
            private readonly Scene _scene;
            private readonly Func<bool> _canDeliver;

            internal Reservation(InstanceCache cache, Entry entry, Transform parent, Scene scene, Func<bool> canDeliver)
            {
                _cache = cache;
                _entry = entry;
                _parent = parent;
                _scene = scene;
                _canDeliver = canDeliver;
            }

            internal InstanceLease Deliver()
            {
                var cache = _cache ?? throw new ObjectDisposedException(nameof(Reservation));
                cache.ValidateCheckout(_entry, _parent, _scene, _canDeliver);
                var lease = new InstanceLease(cache, _entry);
                _cache = null;
                cache._operations--;
                cache.Owner.SettlePendingInstanceEntry(_entry);
                return lease;
            }

            public void Dispose()
            {
                var cache = _cache;
                if (cache == null)
                {
                    return;
                }
                _cache = null;
                try
                {
                    Destroy(_entry);
                }
                finally
                {
                    cache._operations--;
                    cache.Prune(8);
                }
            }
        }

        internal readonly ResourceSystem Owner;
        internal readonly Scene ScopeScene;
        internal readonly GameObject Prefab;
        internal readonly string Key;
        private readonly AssetLease<GameObject> _prefab;
        private readonly SafeObjectPool<GameObjectPoolData> _dataOwner;
        private readonly List<Entry> _instances = new();
        private GameObjectPoolData _data;
        private int _operations;
        private int _pruneIndex;
        private bool _keepEmpty;
        private bool _isReleased;
        private bool _isPruning;

        /// <summary>固定的缓存寿命策略。</summary>
        public InstanceCachePolicy Policy
        {
            get;
        }
        /// <summary>已退休时拒绝新工作；仍可能持有借出或等待真实销毁的对象。</summary>
        public bool IsRetired
        {
            get; private set;
        }

        internal InstanceCache(ResourceSystem owner, AssetLease<GameObject> pin, InstanceCachePolicy policy,
            Scene scene, Transform root)
        {
            Owner = owner;
            _prefab = pin;
            Prefab = pin.Asset;
            Key = pin.Key;
            Policy = policy;
            ScopeScene = scene;
            _dataOwner = SafeObjectPool<GameObjectPoolData>.Instance;
            _data = _dataOwner.Allocate();
            try
            {
                _data.Init("Resource Prefab Cache", root, 32);
            }
            catch
            {
                _dataOwner.Discard(_data);
                _data = null;
                throw;
            }
        }

        internal void KeepEmpty() => _keepEmpty = true;
        internal bool OwnsPrefabPin(IResourceLease lease) => ReferenceEquals(_prefab, lease);

        internal ResourcePoolDiagnostics CaptureDiagnostics()
        {
            var active = 0;
            var cleanup = 0;
            foreach (var entry in _instances)
            {
                if (entry.IsDestroying || !entry.Object)
                    cleanup++;
                else if (entry.Checkout != null)
                    active++;
            }
            return new ResourcePoolDiagnostics(Key, Policy, ScopeScene.IsValid() ? ScopeScene.name : null, IsRetired,
                IsRetired ? 0 : _data?.CountLiveIdleWithoutPruning() ?? 0, active, cleanup, _operations);
        }

        /// <summary>空闲上限，默认 32；-1 不限、0 不缓存。缩容后的 pin 仍等待真实销毁。</summary>
        public int MaxCapacity
        {
            get
            {
                CheckOpen();
                return _data.MaxCapacity;
            }
            set
            {
                CheckOpen();
                _operations++;
                try
                {
                    _data.MaxCapacity = value;
                }
                finally
                {
                    _operations--;
                    Prune(8);
                }
            }
        }

        /// <summary>Foundation 报告的存活空闲数，不包含借出、预约和等待销毁的对象。</summary>
        public int CachedCount
        {
            get
            {
                Owner.CheckThread();
                return IsRetired ? 0 : _data.Count;
            }
        }

        private void CheckOpen()
        {
            Owner.RequireAvailable();
            if (IsRetired)
            {
                throw new ObjectDisposedException(nameof(InstanceCache));
            }
            if (!_data.RootTransform || _data.RootTransform.gameObject.activeInHierarchy ||
                (Policy == InstanceCachePolicy.SceneScoped && (!ScopeScene.IsValid() || !ScopeScene.isLoaded)))
            {
                Dispose();
                throw new ObjectDisposedException(nameof(InstanceCache));
            }
        }

        internal static Scene TargetScene(Transform parent, Scene fallback)
        {
            if (!ReferenceEquals(parent, null) && !parent)
            {
                throw new ArgumentException("The instance parent was destroyed.", nameof(parent));
            }
            if (parent && GameObjectPoolData.IsCacheHierarchy(parent))
            {
                throw new ArgumentException("Instances cannot be checked out below a cache root.", nameof(parent));
            }
            var scene = parent ? parent.gameObject.scene : fallback;
            if (scene == default && ReferenceEquals(parent, null))
            {
                scene = SceneManager.GetActiveScene();
            }
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new ArgumentException("The target scene must still be loaded.");
            }
            return scene;
        }

        /// <summary>始终新建，归还即销毁，不消费或增加空闲项。</summary>
        public InstanceLease Instantiate(Transform parent = null)
        {
            using var reservation = Reserve(false, parent, DefaultScene(), null);
            return reservation.Deliver();
        }

        /// <summary>明确复用，优先借空闲项；不自动重置业务脚本、事件或物理状态。</summary>
        public InstanceLease Rent(Transform parent = null)
        {
            using var reservation = Reserve(true, parent, DefaultScene(), null);
            return reservation.Deliver();
        }

        private Scene DefaultScene() => Policy == InstanceCachePolicy.SceneScoped
            ? ScopeScene : SceneManager.GetActiveScene();

        internal Reservation Reserve(bool reusable, Transform parent, Scene scene, Func<bool> canDeliver,
            ResourceRequest<InstanceLease> input = null)
        {
            CheckAdmission(canDeliver);
            scene = TargetScene(parent, scene);
            _operations++;
            Entry entry = null;
            try
            {
                var hadIdle = reusable && _data.Count != 0;
                var candidates = input != null && hadIdle ? _data.PoolQueue : null;
                GameObject reused;
                try
                {
                    reused = reusable ? _data.Allocate(parent, scene) : null;
                }
                finally
                {
                    TrackConsumedIdle(candidates, input);
                }
                if (!ReferenceEquals(reused, null))
                {
                    foreach (var candidate in _instances)
                    {
                        if (ReferenceEquals(candidate.Object, reused))
                        {
                            entry = candidate;
                            break;
                        }
                    }
                    if (entry == null || entry.Checkout != null || entry.Closing != null || entry.IsDestroying)
                    {
                        throw new InvalidOperationException("The Foundation bucket returned an unowned instance.");
                    }
                    Owner.TrackPendingInstanceEntry(input, entry);
                }
                CheckAdmission(canDeliver);
                if (hadIdle && ReferenceEquals(reused, null))
                {
                    throw InvalidCheckout();
                }
                if (entry == null)
                {
                    entry = CreateInactive(reusable, input);
                    CheckAdmission(canDeliver);
                    var obj = entry.Object;
                    obj.transform.SetParent(parent, false);
                    CheckAdmission(canDeliver);
                    if (!obj)
                    {
                        throw InvalidCheckout();
                    }
                    if (!parent)
                    {
                        SceneManager.MoveGameObjectToScene(obj, scene);
                    }
                    CheckAdmission(canDeliver);
                    if (obj)
                    {
                        obj.SetActive(true);
                    }
                }
                ValidateCheckout(entry, parent, scene, canDeliver);
                return new Reservation(this, entry, parent, scene, canDeliver);
            }
            catch
            {
                try
                {
                    if (entry != null)
                    {
                        Destroy(entry);
                    }
                }
                finally
                {
                    _operations--;
                    Prune(8);
                }
                throw;
            }
        }

        private void TrackConsumedIdle(IReadOnlyCollection<GameObject> candidates, ResourceRequest<InstanceLease> input)
        {
            if (candidates == null)
            {
                return;
            }
            // 仅异步预约取快照：Foundation 回调可能丢弃候选后返回 null，不能丢失这部分销毁责任。
            var remaining = new HashSet<GameObject>(_data.PoolQueue);
            foreach (var candidate in candidates)
            {
                if (remaining.Contains(candidate))
                {
                    continue;
                }
                foreach (var entry in _instances)
                {
                    if (ReferenceEquals(entry.Object, candidate))
                    {
                        if (entry.Checkout == null && entry.Closing == null)
                        {
                            Owner.TrackPendingInstanceEntry(input, entry);
                        }
                        break;
                    }
                }
            }
        }

        private void CheckAdmission(Func<bool> canDeliver)
        {
            CheckOpen();
            if (canDeliver != null && !canDeliver())
            {
                throw new OperationCanceledException("The instance consumer stopped accepting delivery.");
            }
        }

        private void ValidateCheckout(Entry entry, Transform parent, Scene scene, Func<bool> canDeliver)
        {
            CheckAdmission(canDeliver);
            if (TargetScene(parent, scene) != scene || !entry.Object || entry.IsDestroying ||
                !entry.Object.activeSelf || entry.Object.transform.parent != parent ||
                entry.Object.scene != scene || GameObjectPoolData.IsCacheHierarchy(entry.Object.transform.parent))
            {
                throw InvalidCheckout();
            }
        }

        private static ResourceException InvalidCheckout() => new(ResourceError.LoadFailed,
            "A lifecycle callback invalidated the instance checkout.");

        private Entry CreateInactive(bool reusable, ResourceRequest<InstanceLease> input = null)
        {
            CheckOpen();
            var obj = Object.Instantiate(_prefab.Asset, _data.RootTransform, false);
            var entry = new Entry { Object = obj, CanReturn = reusable };
            _instances.Add(entry);
            Owner.TrackPendingInstanceEntry(input, entry);
            try
            {
                if (!obj)
                {
                    throw InvalidCheckout();
                }
                obj.SetActive(false);
                return entry;
            }
            catch
            {
                Destroy(entry);
                throw;
            }
        }

        /// <summary>补至有界目标空闲数，在失活根下逐项登记；不调用业务初始化回调。</summary>
        public int Prewarm(int target)
        {
            CheckOpen();
            if (target < 0 || (_data.MaxCapacity >= 0 && target > _data.MaxCapacity))
            {
                throw new ArgumentOutOfRangeException(nameof(target));
            }
            var added = 0;
            var missing = Math.Max(0, target - _data.Count);
            _operations++;
            try
            {
                for (var i = 0; i < missing && _data.Count < target; i++)
                {
                    CheckOpen();
                    var entry = CreateInactive(true);
                    try
                    {
                        CheckOpen();
                        if (_data.Recycle(entry.Object))
                        {
                            added++;
                        }
                        else
                        {
                            Destroy(entry);
                        }
                        CheckOpen();
                    }
                    catch
                    {
                        Destroy(entry);
                        throw;
                    }
                }
                return added;
            }
            finally
            {
                _operations--;
                Prune(8);
            }
        }

        internal void Return(Entry entry, InstanceLease lease, bool discard)
        {
            _operations++;
            entry.Closing = lease;
            try
            {
                if (!entry.Object)
                {
                    return;
                }
                if (discard || !entry.CanReturn || IsRetired || !Owner.IsAvailable || entry.IsDestroying)
                {
                    Destroy(entry);
                }
                else if (_data.Recycle(entry.Object))
                {
                    entry.Closing = null;
                    lease.Settle();
                }
                else
                {
                    Destroy(entry);
                }
            }
            catch
            {
                Destroy(entry);
                throw;
            }
            finally
            {
                _operations--;
                Prune(8);
            }
        }

        private static void Destroy(Entry entry)
        {
            if (entry.IsDestroying)
            {
                return;
            }
            entry.IsDestroying = true;
            if (!entry.Object)
            {
                return;
            }
            try
            {
                entry.Object.SetActive(false);
            }
            finally
            {
                if (entry.Object)
                {
                    Object.Destroy(entry.Object);
                }
            }
        }

        /// <summary>退休桶并清空闲项和未交割预约；不强制销毁仍借出的实例。</summary>
        public void Dispose()
        {
            Owner.CheckThread();
            if (IsRetired)
            {
                return;
            }
            IsRetired = true;
            Owner.RetireInstanceCache(this);
            _operations++;
            try
            {
                _data.OnRecycle();
                foreach (var entry in _instances.ToArray())
                {
                    if (entry.Checkout == null)
                    {
                        Destroy(entry);
                    }
                }
            }
            finally
            {
                _operations--;
                Prune(8);
            }
        }

        internal void Prune(int budget)
        {
            if (_isReleased || _isPruning)
            {
                return;
            }
            _isPruning = true;
            try
            {
                if (!IsRetired && (!_data.RootTransform ||
                    (Policy == InstanceCachePolicy.SceneScoped && (!ScopeScene.IsValid() || !ScopeScene.isLoaded))))
                {
                    Dispose();
                }
                for (var i = 0; i < budget && _instances.Count != 0; i++)
                {
                    if (_pruneIndex >= _instances.Count)
                    {
                        _pruneIndex = 0;
                    }
                    var entry = _instances[_pruneIndex];
                    if (entry.Object)
                    {
                        _pruneIndex++;
                        continue;
                    }
                    // 外部 OnDestroy 也可能重入 Return/Stop；下一次统一清理才跨过该回调栈。
                    if (entry.DeathObservedAt < 0)
                    {
                        entry.DeathObservedAt = Owner.InstanceCleanupEpoch;
                    }
                    if (entry.DeathObservedAt == Owner.InstanceCleanupEpoch)
                    {
                        _pruneIndex++;
                        continue;
                    }
                    _instances[_pruneIndex] = _instances[_instances.Count - 1];
                    _instances.RemoveAt(_instances.Count - 1);
                    var checkout = entry.Checkout;
                    var closing = entry.Closing;
                    entry.Checkout = null;
                    entry.Closing = null;
                    checkout?.Settle();
                    closing?.Settle();
                    Owner.SettlePendingInstanceEntry(entry);
                }
                if (_operations != 0 || _instances.Count != 0)
                {
                    return;
                }
                if (!IsRetired && !_keepEmpty)
                {
                    Dispose();
                }
                if (!IsRetired)
                {
                    return;
                }
                _isReleased = true;
                var data = _data;
                _data = null;
                try
                {
                    _dataOwner.Recycle(data);
                }
                finally
                {
                    try
                    {
                        _prefab.Dispose();
                    }
                    finally
                    {
                        Owner.ReleaseInstanceCache(this);
                    }
                }
            }
            finally
            {
                _isPruning = false;
            }
        }
    }
}
