using UnityEngine;

namespace YLFramework.Singletons
{
    /// <summary>
    /// 可替换 MonoBehaviour 单例：按 Awake 登记次序保留最后登记者，销毁较早登记的实例（同帧内也可区分）。
    /// Instance 首次访问时优先从场景查找，找不到才创建（创建的对象 HideAndDontSave）；
    /// Awake 登记时会 DontDestroyOnLoad；退出流程中仅返回已有缓存，不再查找或创建。
    /// </summary>
    /// <typeparam name="T">单例类型（继承时传入自身）。</typeparam>
    public class ReplaceableMonoSingleton<T> : MonoBehaviour where T : ReplaceableMonoSingleton<T>
    {
        private static T _instance;
        private static long _creationCounter;

        /// <summary>
        /// Awake 时的 Time.time，可用于外部判断实例创建时间。
        /// </summary>
        protected float InitializationTime;

        /// <summary>
        /// 当前实例的 Awake 登记顺序，同一帧内也能区分先后
        /// </summary>
        private long _creationOrder;

        /// <summary>登记新 Play 会话的实例缓存和登记计数重置。</summary>
        static ReplaceableMonoSingleton()
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
                if (_instance == null)
                {
                    if (SingletonSessionState.IsApplicationQuitting)
                    {
                        return null;
                    }

                    _instance = FindFirstObjectByType<T>();
                    if (_instance == null)
                    {
                        var obj = new GameObject
                        {
                            hideFlags = HideFlags.HideAndDontSave
                        };
                        _instance = obj.AddComponent<T>();
                    }
                }

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
        /// 按 Awake 登记顺序竞争：销毁序号小于自身的场景实例，并将自身设为当前单例。
        /// </summary>
        protected virtual void Awake()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            InitializationTime = Time.time;
            _creationOrder = ++_creationCounter;

            DontDestroyOnLoad(this.gameObject);

            var check = FindObjectsByType<T>(FindObjectsSortMode.InstanceID);
            foreach (var searched in check)
            {
                if (searched == this)
                {
                    continue;
                }
                if (searched._creationOrder < _creationOrder)
                {
                    Destroy(searched.gameObject);
                }
            }

            _instance = this as T;
        }

        /// <summary>进入新 Play 会话时清空实例引用并重置 Awake 登记序号，不主动销毁旧对象。</summary>
        private static void ResetSessionState()
        {
            _instance = null;
            _creationCounter = 0;
        }
    }
}
