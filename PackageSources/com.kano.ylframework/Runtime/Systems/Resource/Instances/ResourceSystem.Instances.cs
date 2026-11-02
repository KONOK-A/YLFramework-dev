using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private readonly Dictionary<(AssetRecord, InstanceCachePolicy, int), InstanceCache> _openInstanceCaches = new();
        private readonly HashSet<InstanceCache> _instanceCaches = new();
        private Transform _instanceRoot;
        private static readonly List<InstanceCache> _instanceCleanup = new();
        private static int _instanceCleanupIndex;
        private static bool _isPruningInstances;

        /// <summary>默认按目标场景缓存。返回独立实例责任，Dispose/Return回桶，Discard销毁。</summary>
        /// <param name="address">资源目录中的 Prefab 地址。</param>
        /// <param name="parent">借出实例的父节点；为 null 时放入当前活动场景。</param>
        /// <returns>由调用方负责结束借出的实例租约。</returns>
        public InstanceLease Instantiate(ResourceAddress address, Transform parent = null)
        {
            using var prefab = Acquire<GameObject>(address);
            return Instantiate(prefab, parent);
        }

        /// <summary>不消费输入 Lease；实例缓存取得自己的 Prefab pin。裸 Prefab/Attach 不由此入口纳管。</summary>
        /// <param name="prefab">来自本资源系统的 Prefab 资产租约，调用方仍负责其原有责任。</param>
        /// <param name="parent">借出实例的父节点；为 null 时放入当前活动场景。</param>
        /// <returns>拥有独立借出身份的实例租约。</returns>
        public InstanceLease Instantiate(AssetLease<GameObject> prefab, Transform parent = null)
            => Instantiate(prefab, parent, SceneManager.GetActiveScene());

        /// <summary>解析明确目标场景，从对应场景桶借出实例并确认交付。</summary>
        internal InstanceLease Instantiate(AssetLease<GameObject> prefab, Transform parent, Scene targetScene)
        {
            CheckThread();
            var scene = InstanceCache.TargetScene(parent, targetScene);
            var cache = GetInstanceCacheCore(prefab, InstanceCachePolicy.SceneScoped, scene);
            var lease = cache.Instantiate(parent, scene);
            cache.Commit();
            return lease;
        }

        /// <summary>异步取得 Prefab 后在主线程创建或复用实例，并通过请求交付借出责任。</summary>
        /// <param name="address">资源目录中的 Prefab 地址。</param>
        /// <param name="parent">借出实例的父节点；无父节点时捕获调用时的活动场景。</param>
        /// <returns>成功后可单次领取实例租约的请求；未领取结果由请求归还。</returns>
        /// <remarks>取消解除本请求的等待；若实例创建回调重入取消，尚未交付的实例会被丢弃。</remarks>
        public ResourceRequest<InstanceLease> InstantiateAsync(ResourceAddress address, Transform parent = null)
        {
            CheckThread();
            var request = new ResourceRequest<InstanceLease>(value => value.Dispose());
            if (!IsAvailable)
            {
                return Fail(request, ResourceError.SessionUnavailable, "ResourceSystem is not available.", address);
            }

            Scene scene;
            try
            {
                scene = InstanceCache.TargetScene(parent, SceneManager.GetActiveScene());
            }
            catch (ArgumentException error)
            {
                return Fail(request, ResourceError.LoadFailed, "Invalid instance destination.", address, cause: error);
            }
            ResourceRequest<AssetLease<GameObject>> child = null;
            Track(request);
            request.Attach(() => child?.Dispose(), () => child?.Progress ?? 0);
            child = AcquireAsync<GameObject>(address);
            if (request.IsDone)
            {
                child.Dispose();
                return request;
            }
            child.Completed += result =>
            {
                if (request.IsDone)
                {
                    result.Dispose();
                    return;
                }
                try
                {
                    using var prefab = result.TakeResult();
                    var target = InstanceCache.TargetScene(parent, scene);
                    var cache = GetInstanceCacheCore(prefab, InstanceCachePolicy.SceneScoped, target);
                    var lease = cache.Instantiate(parent, target);
                    if (request.IsDone)
                    {
                        lease.Discard();
                    }
                    else
                    {
                        cache.Commit();
                        request.SetSuccess(lease);
                    }
                }
                catch (Exception cause)
                {
                    if (!request.IsDone)
                    {
                        request.SetFailure(ResourceRequestStatus.Failed, cause as ResourceException ??
                        new ResourceException(ResourceError.LoadFailed, "Prefab instance creation failed.", cause, address));
                    }
                }
                finally
                {
                    result.Dispose();
                }
                request.Notify();
            };
            return request;
        }

        /// <summary>取得本 authority 的正式缓存。SceneScoped 默认绑定当前活动场景，Persistent仅缓存跨场景，不自动DDOL借出对象。</summary>
        /// <param name="address">资源目录中的 Prefab 地址。</param>
        /// <param name="policy">缓存桶的场景寿命策略。</param>
        /// <param name="scene">SceneScoped 绑定的场景；默认值使用当前活动场景，Persistent 不使用此值分桶。</param>
        /// <returns>可能与其他调用方共享的正式缓存；关闭它会退休整个桶。</returns>
        public InstanceCache GetInstanceCache(ResourceAddress address, InstanceCachePolicy policy = InstanceCachePolicy.SceneScoped, Scene scene = default)
        {
            using var prefab = Acquire<GameObject>(address);
            return GetInstanceCache(prefab, policy, scene);
        }

        /// <summary>用本系统 Prefab 租约取得共享缓存，不消费输入租约；缓存另持独立 pin。</summary>
        /// <param name="prefab">来自本资源系统的 Prefab 资产租约。</param>
        /// <param name="policy">缓存桶的场景寿命策略。</param>
        /// <param name="scene">SceneScoped 绑定的场景；默认值使用当前活动场景，Persistent 不使用此值分桶。</param>
        /// <returns>按资产记录、策略和绑定场景复用的正式缓存。</returns>
        public InstanceCache GetInstanceCache(AssetLease<GameObject> prefab, InstanceCachePolicy policy = InstanceCachePolicy.SceneScoped, Scene scene = default)
        {
            var cache = GetInstanceCacheCore(prefab, policy, scene);
            cache.Commit();
            return cache;
        }

        /// <summary>校验 Prefab 与目标策略，复用开放桶或建立持有独立 pin 的新桶。</summary>
        private InstanceCache GetInstanceCacheCore(AssetLease<GameObject> prefab, InstanceCachePolicy policy, Scene scene)
        {
            CheckThread();
            if (!IsAvailable)
            {
                throw new ResourceException(ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            if (prefab == null)
            {
                throw new ArgumentNullException(nameof(prefab));
            }

            var asset = prefab.Asset;
            RequireContentAdmission(prefab.Record.Entry);
            if (IsProjectBlocked(prefab.Address.ProjectId))
            {
                throw new ResourceException(ResourceError.Busy, "The project is inside its activation barrier.");
            }

            if (!ReferenceEquals(prefab.Record.Owner, this) || asset.scene.IsValid())
            {
                throw new ArgumentException("An asset Prefab lease from this authority is required.", nameof(prefab));
            }

            if (policy != InstanceCachePolicy.SceneScoped && policy != InstanceCachePolicy.Persistent)
            {
                throw new ArgumentOutOfRangeException(nameof(policy));
            }

            if (policy == InstanceCachePolicy.SceneScoped)
            {
                if (scene == default)
                {
                    scene = SceneManager.GetActiveScene();
                }

                InstanceCache.TargetScene(null, scene);
            }
            var key = (prefab.Record, policy, policy == InstanceCachePolicy.Persistent ? 0 : (int)scene.handle);
            if (_openInstanceCaches.TryGetValue(key, out var cache))
            {
                cache.Prune(8);
                if (!cache.IsRetired)
                {
                    return cache;
                }
            }
            var pin = new AssetLease<GameObject>(prefab.Record, prefab.Address, true);
            try
            {
                if (!_instanceRoot)
                {
                    _instanceRoot = new GameObject("YL Resource Instance Cache").transform;
                    UnityEngine.Object.DontDestroyOnLoad(_instanceRoot.gameObject);
                }
                cache = new InstanceCache(this, pin, policy, scene, _instanceRoot);
                _openInstanceCaches.Add(key, cache);
                if (_instanceCaches.Count == 0)
                {
                    SceneManager.sceneUnloaded += OnInstanceSceneUnloaded;
                }

                _instanceCaches.Add(cache);
                _instanceCleanup.Add(cache);
                InstallInstanceCleanup();
                return cache;
            }
            catch
            {
                pin.Dispose();
                throw;
            }
        }

        /// <summary>退休当前缓存快照；不销毁借出实例。回调新建的缓存不属于本次快照。</summary>
        public void ClearInstanceCaches()
        {
            CheckThread();
            foreach (var cache in new List<InstanceCache>(_openInstanceCaches.Values))
            {
                cache.Dispose();
            }
        }

        /// <summary>退休绑定到已卸载场景的开放缓存，不处理 Persistent 桶。</summary>
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

        /// <summary>从开放桶索引移除指定旧缓存，保留其尚未销毁实例的清理责任。</summary>
        internal void RetireInstanceCache(InstanceCache cache)
        {
            var key = (cache.AssetRecord, cache.Policy, cache.Policy == InstanceCachePolicy.Persistent ? 0 : (int)cache.ScopeScene.handle);
            if (_openInstanceCaches.TryGetValue(key, out var current) && ReferenceEquals(current, cache))
            {
                _openInstanceCaches.Remove(key);
            }
        }

        /// <summary>移除已排空缓存，最后一桶结束时解除场景订阅并销毁共享缓存根。</summary>
        internal void ReleaseInstanceCache(InstanceCache cache)
        {
            _instanceCaches.Remove(cache);
            _instanceCleanup.Remove(cache);
            if (_instanceCaches.Count != 0)
            {
                return;
            }

            SceneManager.sceneUnloaded -= OnInstanceSceneUnloaded;
            if (_instanceRoot)
            {
                UnityEngine.Object.Destroy(_instanceRoot.gameObject);
            }

            _instanceRoot = null;
        }

        // 无协程/MonoBehaviour宿主，退出不新建对象；每帧最多8个桶、每桶8项fake-null检查。
        /// <summary>按帧推进远端、内容、场景与实例清理。</summary>
        private static void TickInstanceCleanup()
        {
            if (_isPruningInstances)
            {
                return;
            }

            _isPruningInstances = true;
            try
            {
                TickRemoteUpdates();
                TickContentWork();
                TickSceneCleanup();
                _authority?.TrimImageCache();
                var count = Math.Min(8, _instanceCleanup.Count);
                for (var i = 0; i < count && _instanceCleanup.Count != 0; i++)
                {
                    if (_instanceCleanupIndex >= _instanceCleanup.Count)
                    {
                        _instanceCleanupIndex = 0;
                    }

                    var cache = _instanceCleanup[_instanceCleanupIndex];
                    cache.Prune(8);
                    if (_instanceCleanupIndex < _instanceCleanup.Count && ReferenceEquals(_instanceCleanup[_instanceCleanupIndex], cache))
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

        /// <summary>幂等安装 PlayerLoop 清理回调，不创建 MonoBehaviour 或协程宿主。</summary>
        private static void InstallInstanceCleanup()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var children = new List<PlayerLoopSystem>(loop.subSystemList);
            if (children.Exists(x => x.type == typeof(InstanceCache)))
            {
                return;
            }

            children.Add(new PlayerLoopSystem { type = typeof(InstanceCache), updateDelegate = TickInstanceCleanup });
            loop.subSystemList = children.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
        }
    }
}
