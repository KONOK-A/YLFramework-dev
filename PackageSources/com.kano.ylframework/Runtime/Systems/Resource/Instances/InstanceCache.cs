using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Pooling;
using Object = UnityEngine.Object;

namespace YLFramework.Resource
{
    /// <summary>实例缓存桶的场景寿命策略，不自动改变借出实例的 Unity 场景归属。</summary>
    public enum InstanceCachePolicy
    {
        /// <summary>绑定一个已加载场景，场景卸载时退休该桶。</summary>
        SceneScoped,
        /// <summary>缓存桶可跨场景保留；借出实例不会自动标记 DontDestroyOnLoad。</summary>
        Persistent
    }

    /// <summary>
    /// Resource 拥有 Prefab pin 和实例责任，Foundation 桶拥有 FIFO/容量/复用机制。
    /// Dispose 退休缓存但不销毁借出对象；晚归还销毁，全部实例真实死亡后才释放 pin/Data。
    /// </summary>
    /// <remarks>在所属资源系统的 Unity 主线程使用；空闲与借出实例都由本缓存的独立 Prefab 租约保活。</remarks>
    public sealed partial class InstanceCache : IDisposable
    {
        /// <summary>追踪一个真实实例、当前借出身份及是否已请求销毁。</summary>
        internal sealed class Entry
        {
            internal GameObject Object;
            internal InstanceLease Checkout;
            internal bool IsDestroying;
        }

        internal readonly ResourceSystem Owner;
        internal readonly ResourceSystem.AssetRecord AssetRecord;
        internal readonly Scene ScopeScene;
        private readonly AssetLease<GameObject> _prefab;
        private readonly SafeObjectPool<GameObjectPoolData> _dataOwner;
        private GameObjectPoolData _data;
        private readonly List<Entry> _instances = new();
        private int _pruneIndex;
        private int _operations;
        private bool _isReleased;
        private bool _isCommitted;
        /// <summary>创建本桶时固定的场景寿命策略。</summary>
        public InstanceCachePolicy Policy
        {
            get;
        }
        /// <summary>是否已退休并拒绝新借出；为 true 不代表借出对象已销毁或 Prefab pin 已归还。</summary>
        public bool IsRetired
        {
            get; private set;
        }
        /// <summary>显式取得缓存或成功交付后才保留空桶；失败/交付前取消不能留下无主的creation pin。</summary>
        internal void Commit() => _isCommitted = true;

        /// <summary>接管独立 Prefab pin 并取得 Foundation 桶数据，初始化失败时丢弃桶数据。</summary>
        internal InstanceCache(ResourceSystem owner, AssetLease<GameObject> pin, InstanceCachePolicy policy, Scene scene, Transform root)
        {
            Owner = owner;
            _prefab = pin;
            AssetRecord = pin.Record;
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

        /// <summary>空闲上限，默认32；-1无限，0不缓存。缩容由 Foundation 退休多余实例，pin仍等真实销毁。</summary>
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
        /// <summary>Foundation 桶报告的空闲项数；退休后为零，不表示全部实例的存活数量。</summary>
        public int CachedCount
        {
            get
            {
                Owner.CheckThread();
                return IsRetired ? 0 : _data.Count;
            }
        }
        internal bool HasLiveOwners
        {
            get
            {
                if (_operations != 0)
                {
                    return true;
                }

                foreach (var entry in _instances)
                {
                    if (entry.Object && entry.Checkout != null)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>检查线程、会话、项目屏障及桶和场景状态，拒绝继续使用已退休缓存。</summary>
        private void CheckOpen()
        {
            Owner.CheckThread();
            if (!Owner.IsAvailable)
            {
                throw new ResourceException(ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            if (Owner.IsProjectBlocked(AssetRecord.Entry.Address.ProjectId))
            {
                throw new ResourceException(ResourceError.Busy, "The project is inside its activation barrier.");
            }

            if (IsRetired)
            {
                throw new ObjectDisposedException(nameof(InstanceCache));
            }

            if (!_data.RootTransform)
            {
                Dispose();
                throw new ObjectDisposedException(nameof(InstanceCache));
            }
            if (Policy == InstanceCachePolicy.SceneScoped && (!ScopeScene.IsValid() || !ScopeScene.isLoaded))
            {
                throw new ObjectDisposedException(nameof(InstanceCache));
            }
        }

        /// <summary>优先解析父节点所属场景，拒绝已销毁父节点、缓存层级和未加载场景。</summary>
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
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new ArgumentException("The target scene must still be loaded.");
            }

            return scene;
        }

        /// <summary>从本缓存创建/取得。SceneScoped 无父节点时使用捕获的 scope scene；Persistent 使用当前活动场景。</summary>
        /// <param name="parent">借出实例的父节点；为 null 时使用本缓存策略选择的目标场景。</param>
        /// <returns>由调用方负责 Return 或 Discard 的独立实例租约。</returns>
        public InstanceLease Instantiate(Transform parent = null) => Instantiate(parent,
            Policy == InstanceCachePolicy.SceneScoped ? ScopeScene : SceneManager.GetActiveScene());

        /// <summary>复用或创建实例，生命周期回调后复核借出状态并交付独立租约。</summary>
        internal InstanceLease Instantiate(Transform parent, Scene scene)
        {
            CheckOpen();
            Owner.RequireContentAdmission(AssetRecord.Entry);
            scene = TargetScene(parent, scene);
            _operations++;
            Entry entry = null;
            try
            {
                var reused = _data.Allocate(parent, scene);
                if (IsRetired || !Owner.IsAvailable)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable, "The cache retired during checkout.");
                }

                CheckOpen();
                if (reused)
                {
                    entry = _instances.Find(x => ReferenceEquals(x.Object, reused));
                    if (entry == null)
                    {
                        throw new InvalidOperationException("The Foundation bucket returned an unowned instance.");
                    }
                }
                else
                {
                    entry = CreateInactive();
                    var obj = entry.Object;
                    obj.transform.SetParent(parent, false);
                    if (!parent)
                    {
                        SceneManager.MoveGameObjectToScene(obj, scene);
                    }

                    if (!IsRetired && Owner.IsAvailable && obj)
                    {
                        obj.SetActive(true);
                    }
                }
                if (!entry.Object || IsRetired || !Owner.IsAvailable || !entry.Object.activeSelf || entry.Object.transform.parent != parent ||
                    GameObjectPoolData.IsCacheHierarchy(entry.Object.transform.parent))
                {
                    throw new ResourceException(ResourceError.LoadFailed, "A lifecycle callback invalidated the instance checkout.");
                }

                Owner.RequireContentAdmission(AssetRecord.Entry);
                return new InstanceLease(this, entry);
            }
            catch
            {
                if (entry != null)
                {
                    Destroy(entry);
                }

                throw;
            }
            finally
            {
                _operations--;
                Prune(8);
            }
        }

        /// <summary>创建并登记一个尚未激活的实例条目。</summary>
        private Entry CreateInactive()
        {
            CheckOpen();
            // pin及操作责任在 Instantiate 之前建立；失活根避免 Awake/OnEnable 早于实例登记。
            var obj = Object.Instantiate(_prefab.Asset, _data.RootTransform, false);
            var entry = new Entry { Object = obj };
            _instances.Add(entry);
            if (!obj)
            {
                throw new ResourceException(ResourceError.LoadFailed, "Prefab creation did not produce a live instance.");
            }

            obj.SetActive(false);
            return entry;
        }

        /// <summary>补至目标空闲数量，逐个登记从未激活的实例；不使用不可观察实例集合的隐式预热。</summary>
        /// <param name="target">目标空闲数量，须非负且不超过有限容量上限。</param>
        /// <returns>本次成功加入空闲桶的实例数量。</returns>
        /// <exception cref="ArgumentOutOfRangeException">目标为负或超过有限容量上限。</exception>
        public int Prewarm(int target)
        {
            CheckOpen();
            Owner.RequireContentAdmission(AssetRecord.Entry);
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
                    var entry = CreateInactive();
                    try
                    {
                        Owner.RequireContentAdmission(AssetRecord.Entry);
                        if (_data.Recycle(entry.Object))
                        {
                            added++;
                        }
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

        /// <summary>按丢弃、退休和会话状态销毁或回收本条目，并保持操作期间的 Prefab 保活。</summary>
        internal void Return(Entry entry, bool discard)
        {
            _operations++;
            try
            {
                if (!entry.Object)
                {
                    return;
                }

                if (discard || IsRetired || !Owner.IsSessionActive || entry.IsDestroying)
                {
                    Destroy(entry);
                }
                else
                {
                    try
                    {
                        _data.Recycle(entry.Object);
                    }
                    catch
                    {
                        Destroy(entry);
                        throw;
                    }
                }
            }
            finally
            {
                _operations--;
                Prune(8);
            }
        }

        /// <summary>单次标记销毁并停用实例，即使停用失败也尝试提交 Unity 延迟销毁。</summary>
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

        /// <summary>退休本桶并清理空闲实例，不撤销仍由调用方持有的借出身份。</summary>
        /// <remarks>晚归还实例会销毁；销毁只是提交请求，实际存活条目及在途操作清空后才归还 Prefab pin。</remarks>
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
            }
            finally
            {
                _operations--;
                Prune(8);
            }
        }

        /// <summary>资源会话退出时退休缓存并请求销毁全部实例，包括尚未归还者。</summary>
        internal void EndSession()
        {
            Dispose();
            _operations++;
            try
            {
                foreach (var entry in _instances.ToArray())
                {
                    Destroy(entry);
                }
            }
            finally
            {
                _operations--;
                Prune(8);
            }
        }

        /// <summary>有界清除已真实销毁的条目，退休且无人使用时归还桶数据及 Prefab pin。</summary>
        internal void Prune(int budget)
        {
            if (_isReleased)
            {
                return;
            }

            if (!IsRetired && (!_data.RootTransform || (!_isCommitted && !HasLiveOwners)))
            {
                Dispose();
                return;
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
                entry.Checkout = null;
                _instances[_pruneIndex] = _instances[_instances.Count - 1];
                _instances.RemoveAt(_instances.Count - 1);
            }
            if (!IsRetired || _operations != 0 || _instances.Count != 0)
            {
                return;
            }

            _isReleased = true;
            var owned = _data;
            _data = null;
            try
            {
                _dataOwner.Recycle(owned);
            }
            finally
            {
                Owner.ReleaseInstanceCache(this);
                _prefab.Dispose();
            }
        }
    }
}
