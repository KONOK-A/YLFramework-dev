using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Events
{
    /// <summary>
    /// 注销句柄的生命周期绑定扩展：把注册得到的 <see cref="IUnregister"/> 挂到 GameObject 生命周期上自动注销。
    /// </summary>
    public static class UnregisterExtension
    {
        /// <summary>
        /// 当指定 GameObject 被销毁时自动注销；Unity 仅保证曾 active 的对象收到 OnDestroy。
        /// 从未 active 的宿主须显式清理；已经销毁或为 null 的宿主立即取消。
        /// </summary>
        /// <param name="subscription">待托管的注销句柄。</param>
        /// <param name="gameObject">生命周期宿主。</param>
        /// <returns>传入的注销句柄。</returns>
        public static IUnregister UnregisterWhenGameObjectDestroyed(this IUnregister subscription, GameObject gameObject)
        {
            if (subscription == null)
            {
                return null;
            }

            if (!gameObject)
            {
                subscription.Unregister();
                return subscription;
            }
            var trigger = gameObject.GetComponent<OnDestroyUnregisterTrigger>();
            if (!trigger)
            {
                trigger = gameObject.AddComponent<OnDestroyUnregisterTrigger>();
            }
            return trigger.AddUnregister(subscription);
        }

        /// <summary>
        /// 当指定组件的 GameObject 被销毁时自动注销。
        /// </summary>
        /// <param name="subscription">待托管的注销句柄。</param>
        /// <param name="component">生命周期宿主组件。</param>
        /// <returns>传入的注销句柄。</returns>
        public static IUnregister UnregisterWhenGameObjectDestroyed<T>(this IUnregister subscription, T component)
            where T : Component
        {
            return subscription.UnregisterWhenGameObjectDestroyed(component ? component.gameObject : null);
        }

        /// <summary>
        /// 下一次实际 OnDisable 时注销，不在绑定 inactive 对象时立即取消，也不在 Enable 时自动重订。
        /// </summary>
        /// <param name="subscription">待托管的注销句柄。</param>
        /// <param name="gameObject">生命周期宿主。</param>
        /// <returns>传入的注销句柄。</returns>
        public static IUnregister UnregisterWhenDisabled(this IUnregister subscription, GameObject gameObject)
        {
            if (subscription == null)
            {
                return null;
            }

            if (!gameObject)
            {
                subscription.Unregister();
                return subscription;
            }
            var trigger = gameObject.GetComponent<OnDisableUnregisterTrigger>();
            if (!trigger)
            {
                trigger = gameObject.AddComponent<OnDisableUnregisterTrigger>();
            }
            return trigger.AddUnregister(subscription);
        }

        /// <summary>
        /// 当指定组件的 GameObject 被禁用（OnDisable）时自动注销。
        /// </summary>
        /// <param name="subscription">待托管的注销句柄。</param>
        /// <param name="component">生命周期宿主组件。</param>
        /// <returns>传入的注销句柄。</returns>
        public static IUnregister UnregisterWhenDisabled<T>(this IUnregister subscription, T component)
            where T : Component
        {
            return subscription.UnregisterWhenDisabled(component ? component.gameObject : null);
        }

        /// <summary>
        /// 任意场景卸载时自动注销（包括 additive 场景；不区分 active scene）。
        /// 运行时创建常驻单例触发器，语义详见 <see cref="SceneUnregisterTrigger"/>。
        /// </summary>
        /// <param name="subscription">待托管的注销句柄。</param>
        /// <returns>传入的注销句柄。</returns>
        public static IUnregister UnregisterWhenAnySceneUnloaded(this IUnregister subscription)
        {
            if (subscription == null)
            {
                return null;
            }

            return SceneUnregisterTrigger.Instance.AddUnregister(subscription);
        }

        /// <summary>
        /// 仅在指定的已加载 Scene 卸载时注销，捕获其 handle，不随 active scene 或 GameObject 迁移改变。
        /// 无效或已卸载的 Scene 立即取消；不创建场景宿主。
        /// </summary>
        public static IUnregister UnregisterWhenSceneUnloaded(this IUnregister subscription, Scene scene)
        {
            if (subscription == null)
            {
                return null;
            }

            if (!scene.IsValid() || !scene.isLoaded)
            {
                subscription.Unregister();
                return subscription;
            }
            return SceneUnregisterTrigger.Instance.AddUnregister(subscription, scene);
        }
    }
}
