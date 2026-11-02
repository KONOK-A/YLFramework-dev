using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Singletons;

namespace YLFramework.Events
{
    /// <summary>
    /// 单例触发器：任意场景卸载时注销容器内的所有句柄。
    /// 监听 <see cref="SceneManager.sceneUnloaded"/>，
    /// 任何场景卸载都会触发，包括 additive 加载的场景；不区分 active scene，也不区分句柄注册时所在场景。
    /// 实例常驻（DontDestroyOnLoad）并由 MonoSingletonProperty 管理；仅运行时可用。
    /// 同时承接显式 Scene.handle 的订阅；场景宿主迁移不会改变已捕获的场景所有权。
    /// </summary>
    public class SceneUnregisterTrigger : UnregisterTriggerBase, ISingleton
    {
        private readonly Dictionary<int, SubscriptionSet<IUnregister>> _sceneSubscriptions = new();
        private bool _isCleaningScenes;

        /// <summary>托管到指定已加载场景；无效或已卸载时立即尝试取消，失败责任交给基类保留并向外抛错。</summary>
        /// <param name="subscription">待托管的注销句柄；为 null 时忽略。</param>
        /// <param name="scene">捕获卸载归属的场景，后续不随宿主迁移改变。</param>
        /// <returns>原句柄；自动取消不会创建新订阅。</returns>
        public IUnregister AddUnregister(IUnregister subscription, Scene scene)
        {
            if (subscription == null)
            {
                return null;
            }

            if (IsClosed || !scene.IsValid() || !scene.isLoaded)
            {
                if (IsClosed)
                {
                    return base.AddUnregister(subscription);
                }

                try
                {
                    subscription.Unregister();
                }
                catch
                {
                    base.AddUnregister(subscription);
                    throw;
                }
                return subscription;
            }
            if (!_sceneSubscriptions.TryGetValue(scene.handle, out var subscriptions))
            {
                _sceneSubscriptions.Add(scene.handle, subscriptions = new SubscriptionSet<IUnregister>());
            }

            subscriptions.Add(subscription);
            return subscription;
        }

        /// <summary>仅解除该宿主对句柄的全部托管（AnyScene 及指定 Scene），不会取消订阅。</summary>
        public override void RemoveUnregister(IUnregister subscription)
        {
            base.RemoveUnregister(subscription);
            foreach (var subscriptions in _sceneSubscriptions.Values)
            {
                subscriptions.Remove(subscription);
            }
        }

        /// <summary>
        /// 全局唯一实例；Play 模式下首次访问时自动创建。
        /// </summary>
        public static SceneUnregisterTrigger Instance =>
            MonoSingletonProperty<SceneUnregisterTrigger>.Instance;

        /// <inheritdoc />
        public void OnSingletonInit()
        {
        }

        /// <summary>将宿主设为常驻隐藏对象，并连接全局场景卸载通知。</summary>
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            hideFlags = HideFlags.HideInHierarchy;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        /// <summary>先解除全局场景通知，再由基类关闭托管并尝试清理全部订阅。</summary>
        protected override void OnDestroy()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            base.OnDestroy();
        }

        /// <summary>清理任意场景责任及本场景的定向责任，将该轮聚合清理异常记录到 Unity 日志。</summary>
        private void OnSceneUnloaded(Scene scene)
        {
            try
            {
                ReleaseSubscriptions(scene.handle);
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
            }
        }

        /// <summary>显式清理及宿主销毁涵盖 AnyScene 与所有指定 Scene 的责任。</summary>
        protected override void TriggerUnregisters() => ReleaseSubscriptions(null);

        /// <summary>捕获指定场景的本轮责任并逐项清理，同时保留失败项和重入新增项。</summary>
        /// <param name="sceneHandle">指定场景句柄；为 null 时涵盖全部场景，任意场景责任始终参与本轮。</param>
        private void ReleaseSubscriptions(int? sceneHandle)
        {
            if (_isCleaningScenes)
            {
                return;
            }

            _isCleaningScenes = true;
            // 在任何取消回调前捕获本轮场景集合，重入新增场景留到下一轮。
            var snapshot = new Dictionary<int, List<IUnregister>>();
            foreach (var pair in _sceneSubscriptions)
            {
                if (!sceneHandle.HasValue || pair.Key == sceneHandle.Value)
                {
                    snapshot.Add(pair.Key, new List<IUnregister>(pair.Value.DetachAll()));
                }
            }

            List<Exception> errors = null;
            try
            {
                try
                {
                    base.TriggerUnregisters();
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
                foreach (var pair in snapshot)
                {
                    try
                    {
                        SubscriptionCleanup.ReleaseAll(pair.Value, token => token.Unregister());
                    }
                    catch (Exception error)
                    {
                        (errors ??= new List<Exception>()).Add(error);
                    }
                    var subscriptions = _sceneSubscriptions[pair.Key];
                    foreach (var failure in pair.Value)
                    {
                        subscriptions.Add(failure);
                    }

                    if (subscriptions.Count == 0)
                    {
                        _sceneSubscriptions.Remove(pair.Key);
                    }
                }
            }
            finally
            {
                _isCleaningScenes = false;
            }
            SubscriptionCleanup.ThrowErrors(errors);
        }
    }
}
