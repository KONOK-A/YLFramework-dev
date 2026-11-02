namespace YLFramework.Singletons
{
    /// <summary>
    /// 普通类单例入口：为每个封闭类型 T 维护一个实例，首次访问 <see cref="Instance"/> 时经
    /// <see cref="SingletonCreator.CreateSingleTon{T}"/> 创建并调用 OnSingletonInit。
    /// <see cref="Dispose"/> 只释放引用（不销毁实例、不调用清理回调），下次访问会重建；
    /// 进入新的 Play 会话时会自动清空引用，与 Domain Reload 行为保持一致。
    /// 创建过程加锁，但实例使用本身不保证线程安全。
    /// </summary>
    /// <typeparam name="T">单例类型。</typeparam>
    public class SingletonProperty<T> where T : class, ISingleton
    {
        private static T _instance;
        private static readonly object _lock = new();

        /// <summary>为当前封闭类型登记新 Play 会话的缓存重置。</summary>
        static SingletonProperty()
        {
            SingletonSessionState.RegisterSessionReset(ResetSessionState);
        }

        /// <summary>
        /// 单例实例；首次访问时创建，已释放或会话重置后会重新创建。
        /// </summary>
        /// <remarks>初始化回调返回后才写入缓存；回调中重入本入口可能递归创建。</remarks>
        public static T Instance
        {
            get
            {
                lock (_lock)
                {
                    _instance ??= SingletonCreator.CreateSingleTon<T>();
                }

                return _instance;
            }
        }

        /// <summary>
        /// 当前是否已经创建过单例实例（不会触发创建）
        /// </summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// 尝试获取已创建的单例实例（不会触发创建）
        /// </summary>
        public static bool TryGetInstance(out T instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// 释放当前T类型的单例实例，下次访问 Instance 会重新创建
        /// </summary>
        public static void Dispose()
        {
            _instance = null;
        }

        /// <summary>进入新 Play 会话时清空缓存引用，不调用旧实例的清理逻辑。</summary>
        private static void ResetSessionState()
        {
            _instance = null;
        }
    }
}
