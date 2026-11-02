using UnityEngine;

namespace YLFramework.Singletons
{
    /// <summary>
    /// 常驻 MonoBehaviour 单例：Instance 首次访问时优先从场景查找，找不到才创建。
    /// 场景中存在多个实例时保留先激活（先登记）的，后激活者自动销毁自身 GameObject。
    /// 实例会 DontDestroyOnLoad 跨场景常驻；退出流程中仅返回已有缓存，不再查找或创建。
    /// </summary>
    /// <typeparam name="T">单例类型（继承时传入自身）。</typeparam>
    public class PersistentMonoSingleton<T> : MonoBehaviour where T : PersistentMonoSingleton<T>
    {
        private static T _instance;

        /// <summary>登记新 Play 会话的实例缓存重置。</summary>
        static PersistentMonoSingleton()
        {
            SingletonSessionState.RegisterSessionReset(ResetSessionState);
        }

        /// <summary>
        /// 单例实例；首次访问时从场景查找或创建，退出流程中无缓存时返回 null。
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_instance != null)
                {
                    return _instance;
                }
                if (SingletonSessionState.IsApplicationQuitting)
                {
                    return null;
                }
                _instance = FindFirstObjectByType<T>();
                if (_instance != null)
                {
                    return _instance;
                }
                var obj = new GameObject();
                _instance = obj.AddComponent<T>();
                return _instance;
            }
        }

        /// <summary>
        /// 当前是否已经持有单例实例（不会触发查找或创建）
        /// </summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// 尝试获取已经持有的单例实例（不会触发查找或创建）
        /// </summary>
        public static bool TryGetInstance(out T instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// 登记实例：已有实例时销毁自身；成为当前实例后常驻（同时覆盖在 Awake 之前被 Instance 创建的对象）。
        /// </summary>
        protected virtual void Awake()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (_instance == null)
            {
                _instance = this as T;
            }
            else
            {
                if (this != _instance)
                {
                    if (this && gameObject)
                    {
                        Object.Destroy(gameObject);
                    }
                    return;
                }
            }

            //在 Awake 前被 Instance 获取的实例同样需要持久化（上游 v1.0.197 修复）。
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>进入新 Play 会话时清空缓存，不主动销毁旧 GameObject。</summary>
        private static void ResetSessionState()
        {
            _instance = null;
        }
    }
}
