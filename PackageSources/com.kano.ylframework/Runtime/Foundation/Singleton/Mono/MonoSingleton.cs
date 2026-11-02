using UnityEngine;
using MonoBehaviour = UnityEngine.MonoBehaviour;

namespace YLFramework.Singletons
{
    /// <summary>
    /// MonoBehaviour 单例基类：通过 <see cref="MonoSingletonProperty{T}"/> 查找或创建实例，支持在场景中预先摆放。
    /// Instance 优先复用场景中已有实例（普通版本不做去重销毁，见 <see cref="SingletonCreator"/> 的警告），
    /// 找不到时新建以类型名命名的 GameObject；默认查找/创建仅限播放中，已有缓存不受此限制。
    /// <see cref="Dispose"/> 销毁当前实例的 GameObject 并清空缓存（若销毁的不是缓存实例，则只销毁自身），下次访问会重新查找/创建。
    /// </summary>
    /// <typeparam name="T">单例类型（继承时传入自身）。</typeparam>
    [DisallowMultipleComponent]
    public abstract class MonoSingleton<T> : MonoBehaviour, ISingleton where T : MonoSingleton<T>
    {
        /// <summary>
        /// 单例实例，首次访问时从场景查找或创建
        /// </summary>
        public static T Instance => MonoSingletonProperty<T>.Instance;

        /// <summary>
        /// 当前是否已经持有单例实例（不会触发查找或创建）
        /// </summary>
        public static bool HasInstance => MonoSingletonProperty<T>.HasInstance;

        /// <summary>
        /// 尝试获取已经持有的单例实例（不会触发查找或创建）
        /// </summary>
        public static bool TryGetInstance(out T instance) => MonoSingletonProperty<T>.TryGetInstance(out instance);

        /// <summary>
        /// 单例初始化方法
        /// </summary>
        public virtual void OnSingletonInit()
        {
        }

        /// <summary>
        /// 释放单例：若自身是当前缓存实例则走缓存释放（销毁 GameObject），否则只销毁自身的 GameObject。
        /// </summary>
        public virtual void Dispose()
        {
            if (MonoSingletonProperty<T>.IsCurrent(this as T))
            {
                MonoSingletonProperty<T>.Dispose();
            }
            else if (this && gameObject)
            {
                Object.Destroy(gameObject);
            }
        }

        /// <summary>
        /// 应用退出时标记退出流程并销毁当前实例，避免退出期间再次创建；可重写扩展。
        /// </summary>
        protected virtual void OnApplicationQuit()
        {
            SingletonSessionState.MarkApplicationQuitting();
            if (!MonoSingletonProperty<T>.HasInstance)
            {
                return;
            }

            if (this && gameObject)
            {
                Object.Destroy(gameObject);
            }
            MonoSingletonProperty<T>.ResetIfCurrent(this as T);
        }

        /// <summary>
        /// 被销毁时若自身是当前缓存实例则清空缓存，保证外部销毁后 Instance 能重新查找/创建。
        /// </summary>
        protected virtual void OnDestroy()
        {
            MonoSingletonProperty<T>.ResetIfCurrent(this as T);
        }
    }
}
