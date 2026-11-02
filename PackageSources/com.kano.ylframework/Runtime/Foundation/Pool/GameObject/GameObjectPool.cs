using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 按字符串 key 复用场景实例的主线程单例；空池 Allocate 返回 null，不自动加载或创建。
    /// 缓存跟随 DontDestroyOnLoad 宿主；不拥有 Prefab/AssetBundle 资产及其引用计数。
    /// </summary>
    [MonoSingletonPath("YLFramework/Pool/GameObjectPool")]
    public class GameObjectPool : MonoBehaviour, ISingleton
    {
        /// <summary>按需取得场景实例缓存宿主；访问入口本身不加载池化资源。</summary>
        public static GameObjectPool Instance => MonoSingletonProperty<GameObjectPool>.Instance;

        private readonly Dictionary<string, GameObjectPoolData> _pools = new();
        private readonly Dictionary<string, bool> _creating = new();
        private readonly object _identity = new();
        private static readonly List<WeakReference<GameObjectPool>> _sessionHosts = new();
        private SafeObjectPool<GameObjectPoolData> _dataPool;
        private bool _isDestroyed;
        private bool _isResetting;

        /// <summary>登记会话重置入口，清理禁用 Domain Reload 时可能保留的宿主缓存。</summary>
        static GameObjectPool()
        {
            SingletonSessionState.RegisterSessionReset(ResetHosts);
        }

        /// <summary>逐个关闭仍存活宿主的旧桶并汇总异常，随后允许宿主重新取得本轮 Data 池。</summary>
        private static void ResetHosts()
        {
            List<Exception> errors = null;
            for (var i = _sessionHosts.Count - 1; i >= 0; i--)
            {
                if (!_sessionHosts[i].TryGetTarget(out var host) || !host)
                {
                    _sessionHosts.RemoveAt(i);
                    continue;
                }
                host._isResetting = true;
                try
                {
                    host.ClearAll();
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
                finally
                {
                    host._dataPool = null;
                    host._isResetting = false;
                }
            }
            if (errors != null)
            {
                throw new AggregateException(errors);
            }
        }

        /// <summary>保留单例初始化入口；宿主登记与跨场景保留由 Awake 完成。</summary>
        public void OnSingletonInit()
        {
        }

        /// <summary>保留宿主根层级并登记弱引用，使会话重置可找到直接创建或被单例接纳的宿主。</summary>
        private void Awake()
        {
            // 路径根被预先建立或宿主被直接收养时，也保证缓存跨场景存活。
            DontDestroyOnLoad(transform.root.gameObject);
            _sessionHosts.Add(new WeakReference<GameObjectPool>(this));
        }

        /// <summary>禁止再创建桶，关闭当前缓存，并在清理失败时仍通知单例入口宿主已销毁。</summary>
        private void OnDestroy()
        {
            _isDestroyed = true;
            try
            {
                ClearAll();
            }
            finally
            {
                MonoSingletonProperty<GameObjectPool>.NotifyDestroyed(this);
            }
        }

        /// <summary>阻止已销毁或会话重置中的宿主继续创建桶或借出对象。</summary>
        private void ThrowIfDestroyed()
        {
            if (_isDestroyed || _isResetting || !this)
            {
                throw new ObjectDisposedException(nameof(GameObjectPool));
            }
        }

        /// <summary>在修改桶之前校验名称、容量以及可被该容量容纳的非负预热目标。</summary>
        private static void ValidateInitialization(string key, int capacity, int target)
        {
            GameObjectPoolData.ValidateKey(key);
            GameObjectPoolData.ValidateCapacity(capacity);
            if (target < 0 || (capacity >= 0 && target > capacity))
            {
                throw new ArgumentOutOfRangeException(nameof(target));
            }
        }

        /// <summary>校验场景实例与稳定归属，并排除宿主自身及祖先，避免归还时破坏缓存层级。</summary>
        private void ValidateInput(string key, GameObject obj)
        {
            GameObjectPoolData.ValidateInstance(obj);
            GameObjectPoolData.ValidateOwner(obj, _identity, key);
            if (this && transform.IsChildOf(obj.transform))
            {
                throw new ArgumentException("不能将池宿主或其祖先归还到对象池。", nameof(obj));
            }
        }

        /// <summary>复用有效桶或从 Data 池借用并初始化新桶；同 key 重入或创建中被清理时不发布该桶。</summary>
        private GameObjectPoolData GetOrCreate(string key, int capacity = -1)
        {
            ThrowIfDestroyed();
            if (_creating.ContainsKey(key))
            {
                throw new InvalidOperationException("不能在层级回调中重入创建同一 key 的桶。");
            }

            if (_pools.TryGetValue(key, out var data))
            {
                if (data.IsLive(data.CurrentLifetime))
                {
                    return data;
                }

                _pools.Remove(key);
                _dataPool.Recycle(data);
            }
            var owner = _dataPool ??= SafeObjectPool<GameObjectPoolData>.Instance;
            _creating.Add(key, false);
            data = null;
            try
            {
                data = owner.Allocate();
                data.CacheOwner = owner;
                data.Init(key, transform, capacity, _identity);
                ThrowIfDestroyed();
                if (_creating[key])
                {
                    throw new ObjectDisposedException(nameof(GameObjectPoolData));
                }

                _pools.Add(key, data);
                return data;
            }
            catch
            {
                if (data != null)
                {
                    owner.Discard(data);
                }

                throw;
            }
            finally
            {
                _creating.Remove(key);
            }
        }

        /// <summary>
        /// 设置空闲容量并按目标空闲数量补齐；-1 无限，0 禁缓存。非法输入在修改桶之前抛错。
        /// Prefab 仅在本次调用中使用，不保留资产引用；预热在失活根下创建。
        /// </summary>
        /// <param name="keyName">非空的稳定桶名称。</param>
        /// <param name="maxCapacity">空闲实例上限，-1 表示不限，不能小于 -1。</param>
        /// <param name="prefab">本次补齐使用的克隆源；defaultQuantity 为正时必须提供。</param>
        /// <param name="defaultQuantity">目标空闲总数，不是新增数量；必须非负且不超过有限容量。</param>
        public void InitOneGoData(string keyName, int maxCapacity = -1, GameObject prefab = null, int defaultQuantity = 0)
        {
            ValidateInitialization(keyName, maxCapacity, defaultQuantity);
            if (defaultQuantity > 0 && !prefab)
            {
                throw new ArgumentNullException(nameof(prefab));
            }

            var data = GetOrCreate(keyName, maxCapacity);
            data.MaxCapacity = maxCapacity;
            if (defaultQuantity > 0)
            {
                data.Prewarm(prefab, defaultQuantity);
            }
        }

        /// <summary>使用 Prefab 名称作为首次注册 key；取得后改名不改变归属。</summary>
        /// <param name="prefab">克隆源及桶名称来源，不能为 null 或已销毁对象。</param>
        /// <param name="maxCapacity">空闲实例上限，-1 表示不限，0 禁止缓存。</param>
        /// <param name="defaultQuantity">目标空闲总数，必须非负且不超过有限容量。</param>
        public void InitOneGoData(GameObject prefab, int maxCapacity = -1, int defaultQuantity = 0)
        {
            if (!prefab)
            {
                throw new ArgumentNullException(nameof(prefab));
            }

            InitOneGoData(prefab.name, maxCapacity, prefab, defaultQuantity);
        }

        /// <summary>
        /// 数组长度作为目标空闲总量；已有缓存计入目标，null/失效/重复输入不占接纳名额。
        /// 只接纳当前桶或未登记的场景实例，重复/已空闲输入不销毁；超目标的独占输入退休。
        /// 外来归属和非法参数在修改桶/输入前拒绝；生命周期回调失败不提供整批事务回滚。
        /// </summary>
        /// <param name="keyName">非空的稳定桶名称。</param>
        /// <param name="maxCapacity">空闲实例上限，-1 表示不限；有限容量不得小于数组长度。</param>
        /// <param name="gameObjects">用于补齐的场景实例；null 按空数组处理。</param>
        public void InitOneGoData(string keyName, int maxCapacity, GameObject[] gameObjects)
        {
            GameObjectPoolData.ValidateKey(keyName);
            GameObjectPoolData.ValidateCapacity(maxCapacity);
            var unique = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            if (gameObjects != null)
            {
                foreach (var obj in gameObjects)
                {
                    if (!obj || !seen.Add(obj))
                    {
                        continue;
                    }

                    ValidateInput(keyName, obj);
                    unique.Add(obj);
                }
            }
            var target = gameObjects?.Length ?? 0;
            ValidateInitialization(keyName, maxCapacity, target);
            var data = GetOrCreate(keyName, maxCapacity);
            var lifetime = data.CurrentLifetime;
            data.MaxCapacity = maxCapacity;
            foreach (var obj in unique)
            {
                if (!data.IsLive(lifetime))
                {
                    throw new ObjectDisposedException(nameof(GameObjectPoolData));
                }
                // 名称仅供层级识别；实例路由由不可变归属决定。
                if (obj)
                {
                    obj.name = keyName;
                }

                data.Recycle(obj, target, lifetime);
            }
        }

        /// <summary>用已有实例初始化无限容量桶；显式数组入口避免与 Prefab 可选参数重载歧义。</summary>
        public void InitOneGoData(string keyName, GameObject[] gameObjects) => InitOneGoData(keyName, -1, gameObjects);

        /// <summary>从指定桶取得，空桶返回 null；parent 为 null 时回到当前活动场景。</summary>
        /// <param name="keyName">非空的稳定桶名称。</param>
        /// <param name="parent">借出后的父节点，不能位于任何缓存根层级内。</param>
        /// <returns>已完成层级设置的借出实例；桶不存在或无可交付实例时为 null。</returns>
        public GameObject Allocate(string keyName, Transform parent = null)
        {
            GameObjectPoolData.ValidateKey(keyName);
            ThrowIfDestroyed();
            return _pools.TryGetValue(keyName, out var data) ? data.Allocate(parent) : null;
        }

        /// <summary>按稳定归属归还；首次接纳未登记实例时才使用对象名称作为 key。</summary>
        public void Recycle(GameObject go)
        {
            if (!go)
            {
                return;
            }

            Recycle(GameObjectPoolData.TryGetLifetime(go, out var lifetime) ? lifetime.Key : go.name, go);
        }

        /// <summary>
        /// 接纳未登记实例或归还本 key 借出实例；跨 key/宿主抛错且不修改对象。
        /// false 仅表示未缓存，满池/已关闭桶的晚归还会销毁，不得继续使用。
        /// 原始引用不区分租用代次，归还后禁止保留旧别名。
        /// </summary>
        /// <param name="keyName">非空的桶名称；已登记实例必须与原归属一致。</param>
        /// <param name="obj">待归还的场景实例，null 或已销毁对象返回 false。</param>
        /// <returns>是否进入空闲缓存；重复或转换期归还同样返回 false。</returns>
        public bool Recycle(string keyName, GameObject obj)
        {
            GameObjectPoolData.ValidateKey(keyName);
            if (!obj)
            {
                return false;
            }

            ValidateInput(keyName, obj);
            if (GameObjectPoolData.TryGetLifetime(obj, out var lifetime) && lifetime.IsClosed)
            {
                return GameObjectPoolData.ReturnToClosedLifetime(obj);
            }

            return GetOrCreate(keyName).Recycle(obj);
        }

        /// <summary>存活的空闲实例数量；查询不创建桶或宿主。</summary>
        public int GetCachedCount(string keyName)
        {
            GameObjectPoolData.ValidateKey(keyName);
            return _pools.TryGetValue(keyName, out var data) ? data.Count : 0;
        }

        /// <summary>先摘桶，再销毁空闲实例与缓存节点；借出对象保留，晚归还时退休。</summary>
        public void Clear(string keyName)
        {
            GameObjectPoolData.ValidateKey(keyName);
            if (_creating.ContainsKey(keyName))
            {
                _creating[keyName] = true;
            }

            if (!_pools.TryGetValue(keyName, out var data))
            {
                return;
            }

            _pools.Remove(keyName);
            _dataPool.Recycle(data);
        }

        /// <summary>摘除所有当前桶再逐项关闭；回调新建的桶保留，宿主销毁时禁止新建。</summary>
        public void ClearAll()
        {
            foreach (var key in new List<string>(_creating.Keys))
            {
                _creating[key] = true;
            }

            if (_pools.Count == 0)
            {
                return;
            }

            var snapshot = new List<GameObjectPoolData>(_pools.Values);
            _pools.Clear();
            List<Exception> errors = null;
            foreach (var data in snapshot)
            {
                try
                {
                    _dataPool.Recycle(data);
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
    }
}
