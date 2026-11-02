using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 单个 key 的 FIFO 实例缓存；只在 Unity 主线程使用，不拥有 Prefab 资产。
    /// Data 经 SafeObjectPool 复用，GameObject 的归属绑定不可复用的桶生命周期。
    /// </summary>
    public class GameObjectPoolData : IOfPool, IPoolType
    {
        /// <summary>区分借出、空闲与生命周期转换阶段，防止 Unity 回调重入时重复接纳同一实例。</summary>
        private enum State
        {
            Leased,
            Returning,
            Idle,
            Allocating,
            Retired
        }

        /// <summary>一次桶初始化的稳定归属；Data 复用后旧实例仍凭此身份识别晚归还。</summary>
        internal sealed class Lifetime
        {
            internal object Owner;
            internal string Key;
            internal bool IsClosed;
        }

        /// <summary>按 GameObject 弱键保存不可迁移的桶生命周期与当前借用状态。</summary>
        private sealed class Entry
        {
            internal Lifetime Lifetime;
            internal State State;
        }

        // 跨 key/宿主/Data 的唯一归属。弱键不保活借出对象；退休记录保留到包装对象被 GC。
        private static readonly ConditionalWeakTable<GameObject, Entry> _entries = new();
        private static readonly ConditionalWeakTable<Transform, Lifetime> _cacheRoots = new();
        private readonly Queue<GameObject> _queue;
        private readonly object _identity = new();
        private Lifetime _lifetime;
        private int _maxCapacity = -1;
        private bool _isClearing;
        private bool _isInitializing;
        internal SafeObjectPool<GameObjectPoolData> CacheOwner;

        /// <summary>池拥有的缓存节点，不允许外部替换或销毁后继续使用。</summary>
        public Transform RootTransform
        {
            get; private set;
        }

        /// <summary>空闲队列的只读快照；修改快照不影响池，热路径请使用 Count。</summary>
        public IReadOnlyCollection<GameObject> PoolQueue
        {
            get
            {
                PruneDestroyed();
                return _queue.ToArray();
            }
        }

        /// <summary>存活的空闲数量，不包括借出和转换期实例。</summary>
        public int Count
        {
            get
            {
                PruneDestroyed();
                return _queue.Count;
            }
        }

        /// <summary>空闲上限：-1 无限，0 禁缓存；缩容立即退休多余空闲实例。</summary>
        public int MaxCapacity
        {
            get => _maxCapacity;
            set
            {
                ValidateCapacity(value);
                _maxCapacity = value;
                PruneDestroyed();
                if (value >= 0 && _queue.Count > value)
                {
                    RetireCached(_queue.Count - value);
                }
            }
        }

        /// <summary>创建使用默认队列容量的 Data，缓存根与桶归属须另行 Init。</summary>
        public GameObjectPoolData() : this(-1) { }

        /// <summary>capacity 是队列初始容量，-1 使用默认值，不是缓存上限。</summary>
        /// <param name="capacity">队列初始容量，必须不小于 -1；不会创建 GameObject。</param>
        public GameObjectPoolData(int capacity = -1)
        {
            ValidateCapacity(capacity);
            _queue = capacity == -1 ? new Queue<GameObject>() : new Queue<GameObject>(capacity);
        }

        /// <summary>校验容量取值；-1 保留为不限缓存或使用默认队列容量的约定值。</summary>
        internal static void ValidateCapacity(int capacity)
        {
            if (capacity < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
        }

        /// <summary>拒绝不能标识桶的 null 或空名称。</summary>
        internal static void ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("GameObject Pool key 不能为空。", nameof(key));
            }
        }

        /// <summary>拒绝非已加载场景中的实例以及缓存根，避免把资产或池自身当作可复用对象。</summary>
        internal static void ValidateInstance(GameObject obj)
        {
            if (obj && (!obj.scene.IsValid() || !obj.scene.isLoaded))
            {
                throw new ArgumentException("只能接纳场景实例，不能归还 Prefab 资产。", nameof(obj));
            }

            if (obj && _cacheRoots.TryGetValue(obj.transform, out _))
            {
                throw new ArgumentException("不能将缓存根放入对象池。", nameof(obj));
            }
        }

        /// <summary>沿祖先链判断目标层级是否属于任意已登记缓存根，供借出时排除缓存层级。</summary>
        internal static bool IsCacheHierarchy(Transform parent)
        {
            for (var node = parent; node; node = node.parent)
            {
                if (_cacheRoots.TryGetValue(node, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按托管引用查询初次接纳时的桶归属，不依赖对象当前名称或 Unity 存活状态。</summary>
        internal static bool TryGetLifetime(GameObject obj, out Lifetime lifetime)
        {
            if (!ReferenceEquals(obj, null) && _entries.TryGetValue(obj, out var entry))
            {
                lifetime = entry.Lifetime;
                return true;
            }
            lifetime = null;
            return false;
        }

        /// <summary>拒绝已登记实例跨宿主或跨 key 归还，未登记实例留给接纳流程处理。</summary>
        internal static void ValidateOwner(GameObject obj, object owner, string key)
        {
            if (TryGetLifetime(obj, out var lifetime) &&
                (!ReferenceEquals(lifetime.Owner, owner) || lifetime.Key != key))
            {
                throw new InvalidOperationException("不能将 GameObject 归还到其他 key 或宿主。归属不随 name 改变。");
            }
        }

        /// <summary>创建独立桶；活动桶不能重复 Init，须先 OnRecycle 关闭。</summary>
        /// <param name="assetPath">桶的非空 key，仅用作名称，不在此加载资产。</param>
        /// <param name="poolRootObj">新建失活缓存根的父节点，不能为 null 或已销毁对象。</param>
        /// <param name="capacity">空闲实例上限，-1 表示不限，0 禁止缓存。</param>
        public void Init(string assetPath, Transform poolRootObj, int capacity = -1) =>
            Init(assetPath, poolRootObj, capacity, _identity);

        /// <summary>以给定宿主身份开启新桶生命周期；先登记归属再挂接缓存根，失败时关闭本次生命周期。</summary>
        internal void Init(string key, Transform poolRoot, int capacity, object owner)
        {
            ValidateKey(key);
            ValidateCapacity(capacity);
            if (!poolRoot)
            {
                throw new ArgumentNullException(nameof(poolRoot));
            }

            if (_isInitializing || _isClearing || (_lifetime != null && !_lifetime.IsClosed))
            {
                throw new InvalidOperationException("不能重新初始化尚未关闭的 GameObject 桶。");
            }

            _isInitializing = true;
            GameObject root = null;
            try
            {
                root = new GameObject(key);
                root.SetActive(false);
                RootTransform = root.transform;
                _maxCapacity = capacity;
                var lifetime = new Lifetime { Owner = owner, Key = key };
                _lifetime = lifetime;
                _cacheRoots.Add(RootTransform, lifetime);
                // 先登记，再触发父节点 OnTransformChildrenChanged；回调不能重建同一 Data。
                RootTransform.SetParent(poolRoot, false);
                if (!IsLive(lifetime) || RootTransform.parent != poolRoot)
                {
                    throw new ObjectDisposedException(nameof(GameObjectPoolData));
                }
            }
            catch
            {
                OnRecycle();
                if (root)
                {
                    Object.Destroy(root);
                }

                throw;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        internal Lifetime CurrentLifetime => _lifetime;
        /// <summary>确认操作仍属于当前未关闭的桶，且缓存根未被外部销毁。</summary>
        internal bool IsLive(Lifetime lifetime) => ReferenceEquals(_lifetime, lifetime) &&
            lifetime != null && !lifetime.IsClosed && RootTransform;

        /// <summary>从空闲队列剔除已销毁实例，保留其余实例的 FIFO 顺序。</summary>
        private void PruneDestroyed()
        {
            // 不新增扫描宿主；在计数、归还、分配及容量操作时惰性剔除 fake-null。
            var count = _queue.Count;
            for (var i = 0; i < count; i++)
            {
                var obj = _queue.Dequeue();
                if (obj)
                {
                    _queue.Enqueue(obj);
                }
                else if (!ReferenceEquals(obj, null) && _entries.TryGetValue(obj, out var entry))
                {
                    entry.State = State.Retired;
                }
            }
        }

        /// <summary>永久标记实例后停用并请求延迟销毁，即使停用失败也尝试销毁，不允许重复处理。</summary>
        private static void Retire(GameObject obj, Entry entry)
        {
            if (entry.State == State.Retired)
            {
                return;
            }

            entry.State = State.Retired;
            if (!obj)
            {
                return;
            }
            // 先退休再停用：OnDisable 重入不能重新接纳同帧待 Destroy 的对象。
            try
            {
                obj.SetActive(false);
            }
            finally
            {
                if (obj)
                {
                    Object.Destroy(obj);
                }
            }
        }

        /// <summary>消费旧桶的有效晚归还并退休实例，始终返回 false，不转入新桶缓存。</summary>
        internal static bool ReturnToClosedLifetime(GameObject obj)
        {
            if (!_entries.TryGetValue(obj, out var entry) || entry.State != State.Leased)
            {
                return false;
            }

            Retire(obj, entry);
            return false;
        }

        /// <summary>
        /// 接管未登记的场景实例，或归还本桶借出实例；跨桶抛错且无副作用。
        /// false 仅表示未缓存，满池/旧桶晚还已消费并销毁引用；重复/转换期不重复处理。
        /// </summary>
        /// <param name="obj">待接纳或归还的场景实例，null 或已销毁对象会被忽略。</param>
        /// <returns>是否进入当前桶的空闲缓存。</returns>
        public bool Recycle(GameObject obj) => Recycle(obj, int.MaxValue, _lifetime);

        /// <summary>按指定生命周期接纳或消费借用；停用和换父节点后复核归属、存活与容量，再发布为空闲实例。</summary>
        internal bool Recycle(GameObject obj, int target, Lifetime lifetime)
        {
            if (!obj)
            {
                return false;
            }

            ValidateInstance(obj);
            if (lifetime == null)
            {
                throw new InvalidOperationException("GameObject 桶尚未初始化。");
            }

            if (RootTransform && (obj.transform == RootTransform || RootTransform.IsChildOf(obj.transform)))
            {
                throw new ArgumentException("不能将缓存根或其祖先放入对象池。", nameof(obj));
            }

            ValidateOwner(obj, lifetime.Owner, lifetime.Key);
            if (_entries.TryGetValue(obj, out var entry))
            {
                if (!ReferenceEquals(entry.Lifetime, lifetime))
                {
                    if (entry.Lifetime.IsClosed)
                    {
                        return ReturnToClosedLifetime(obj);
                    }

                    throw new InvalidOperationException("GameObject 不属于当前桶生命周期。");
                }
                if (entry.State != State.Leased)
                {
                    return false;
                }
            }
            else
            {
                if (!IsLive(lifetime))
                {
                    throw new ObjectDisposedException(nameof(GameObjectPoolData));
                }

                entry = new Entry { Lifetime = lifetime, State = State.Leased };
                _entries.Add(obj, entry);
            }

            var willCache = IsLive(lifetime) && Count < target && (_maxCapacity == -1 || Count < _maxCapacity);
            entry.State = State.Returning;
            try
            {
                if (willCache)
                {
                    // 不发布转换期对象；先停用，防止父节点变化触发额外 OnEnable。
                    obj.SetActive(false);
                    if (obj && IsLive(lifetime))
                    {
                        obj.transform.SetParent(RootTransform, true);
                    }
                }
                if (willCache && obj && IsLive(lifetime) && !obj.activeSelf &&
                    obj.transform.parent == RootTransform && Count < target &&
                    (_maxCapacity == -1 || Count < _maxCapacity))
                {
                    entry.State = State.Idle;
                    _queue.Enqueue(obj);
                    return true;
                }
            }
            catch (Exception error)
            {
                try
                {
                    Retire(obj, entry);
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException(error, cleanup);
                }
                throw;
            }
            Retire(obj, entry);
            return false;
        }

        /// <summary>
        /// 空池返回 null；取得期间拒绝同对象归还。先设置最终层级/场景，再激活。
        /// parent 为 null 时移入当前活动场景；回调关闭桶或破坏对象时不交付失效实例。
        /// </summary>
        /// <param name="parent">借出后的父节点，不能位于任何缓存根层级内。</param>
        /// <returns>当前桶可交付的借出实例；无有效缓存或桶已关闭时为 null。</returns>
        public GameObject Allocate(Transform parent = null) => Allocate(parent, SceneManager.GetActiveScene());

        // 异步调用方可捕获目标场景；不能在完成时误用后来切换的 active scene。
        internal GameObject Allocate(Transform parent, Scene targetScene)
        {
            var lifetime = _lifetime;
            if (!IsLive(lifetime))
            {
                return null;
            }

            if (parent && IsCacheHierarchy(parent))
            {
                throw new ArgumentException("不能将借出对象挂在任何缓存节点或其后代下。", nameof(parent));
            }

            if (!parent && (!targetScene.IsValid() || !targetScene.isLoaded))
            {
                throw new ArgumentException("借出对象的目标场景必须仍然有效。", nameof(targetScene));
            }

            PruneDestroyed();
            while (_queue.Count > 0)
            {
                var obj = _queue.Dequeue();
                _entries.TryGetValue(obj, out var entry);
                if (!obj)
                {
                    entry.State = State.Retired;
                    continue;
                }
                entry.State = State.Allocating;
                try
                {
                    obj.transform.SetParent(parent, true);
                    if (obj && IsLive(lifetime))
                    {
                        if (!parent)
                        {
                            SceneManager.MoveGameObjectToScene(obj, targetScene);
                        }

                        obj.SetActive(true);
                    }
                    if (obj && IsLive(lifetime) && obj.activeSelf && obj.transform.parent == parent &&
                        !IsCacheHierarchy(obj.transform.parent))
                    {
                        entry.State = State.Leased;
                        return obj;
                    }
                }
                catch (Exception error)
                {
                    try
                    {
                        Retire(obj, entry);
                    }
                    catch (Exception cleanup)
                    {
                        throw new AggregateException(error, cleanup);
                    }
                    throw;
                }
                Retire(obj, entry);
                if (!IsLive(lifetime))
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>按目标空闲数克隆缺口并走统一归还流程；桶失效即停止，已接纳实例不回滚。</summary>
        internal void Prewarm(GameObject prefab, int target)
        {
            var lifetime = _lifetime;
            var missing = Math.Max(0, target - Count);
            for (var i = 0; i < missing && Count < target; i++)
            {
                if (!IsLive(lifetime))
                {
                    throw new ObjectDisposedException(nameof(GameObjectPoolData));
                }
                // 在失活缓存根下创建，预热不触发 active Prefab 的 OnEnable。
                var obj = Object.Instantiate(prefab, RootTransform, false);
                obj.name = lifetime.Key;
                if (!IsLive(lifetime))
                {
                    var entry = new Entry { Lifetime = lifetime, State = State.Leased };
                    _entries.Add(obj, entry);
                    Retire(obj, entry);
                    throw new ObjectDisposedException(nameof(GameObjectPoolData));
                }
                Recycle(obj, target, lifetime);
            }
        }

        /// <summary>先摘除空闲快照阻止回调重复借出，再逐项退休并汇总清理异常。</summary>
        private void RetireCached(int count)
        {
            if (count == 0)
            {
                return;
            }

            var snapshot = new GameObject[count];
            for (var i = 0; i < count; i++)
            {
                var obj = _queue.Dequeue();
                _entries.TryGetValue(obj, out var entry);
                entry.State = State.Returning;
                snapshot[i] = obj;
            }
            List<Exception> errors = null;
            foreach (var obj in snapshot)
            {
                _entries.TryGetValue(obj, out var entry);
                try
                {
                    Retire(obj, entry);
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
            }
            if (errors != null)
            {
                throw new AggregateException(errors);
            }
        }

        /// <summary>先关闭桶再清空闲快照；不销毁借出对象，晚归还到原归属时销毁。</summary>
        public void OnRecycle()
        {
            if (_lifetime == null || _lifetime.IsClosed)
            {
                return;
            }

            _lifetime.IsClosed = true;
            _isClearing = true;
            var root = RootTransform;
            RootTransform = null;
            try
            {
                RetireCached(_queue.Count);
            }
            finally
            {
                if (root)
                {
                    Object.Destroy(root.gameObject);
                }

                _maxCapacity = -1;
                _isClearing = false;
            }
        }

        /// <summary>Data 的普通池标记，不是 GameObject 的身份判断。</summary>
        public bool IsRecycled
        {
            get; set;
        }

        /// <summary>捕获既有类型单例入口，避免跨会话晚归还误用新 Safe 池；宿主会覆盖精确 owner。</summary>
        public void OnAllocate() => CacheOwner = SafeObjectPool<GameObjectPoolData>.Instance;

        /// <summary>仅适用于从 Safe 类型单例借出的 Data，关闭后归还原缓存；私有 Safe 使用者应显式归还其 owner。</summary>
        public void RecycleToCache()
        {
            if (CacheOwner == null)
            {
                throw new InvalidOperationException("Data 未从 Safe 类型单例借出，请直接 OnRecycle 关闭。");
            }

            CacheOwner.Recycle(this);
        }
    }
}
