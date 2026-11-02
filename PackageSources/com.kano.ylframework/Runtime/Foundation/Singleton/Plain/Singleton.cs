namespace YLFramework.Singletons
{
    /// <summary>
    /// 纯 C# 单例基类：继承后通过静态 <see cref="Instance"/> 访问，首次访问时经 <see cref="SingletonCreator"/> 创建并调用
    /// <see cref="OnSingletonInit"/>。
    /// <see cref="Dispose"/> 只释放实例引用，不会调用任何清理/销毁回调，下次访问 Instance 会重新创建。
    /// </summary>
    /// <typeparam name="T">单例类型（继承时传入自身）。</typeparam>
    public abstract class Singleton<T> : ISingleton where T : Singleton<T>
    {
        /// <summary>
        /// 单例实例，首次访问时创建
        /// </summary>
        public static T Instance => SingletonProperty<T>.Instance;

        /// <summary>
        /// 当前是否已经创建过单例实例（不会触发创建）
        /// </summary>
        public static bool HasInstance => SingletonProperty<T>.HasInstance;

        /// <summary>
        /// 尝试获取已创建的单例实例（不会触发创建）
        /// </summary>
        public static bool TryGetInstance(out T instance) => SingletonProperty<T>.TryGetInstance(out instance);

        /// <summary>
        /// 单例初始化时调用
        /// </summary>
        public virtual void OnSingletonInit()
        {
        }

        /// <summary>
        /// 单例类实例释放，下次访问 Instance 会重新创建
        /// </summary>
        public virtual void Dispose()
        {
            SingletonProperty<T>.Dispose();
        }
    }
}
