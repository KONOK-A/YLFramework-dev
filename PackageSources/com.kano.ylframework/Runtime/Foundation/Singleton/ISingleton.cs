namespace YLFramework.Singletons
{
    /// <summary>
    /// 单例标记接口：配合 SingletonProperty / MonoSingletonProperty / MonoSingleton 等入口使用，
    /// 实例的创建、缓存与释放由对应的入口类型负责，本接口只提供初始化回调。
    /// </summary>
    public interface ISingleton
    {
        /// <summary>
        /// 单例入口创建或采用实例后的初始化回调，是否去重由具体入口负责。
        /// 部分入口未做重入保护，初始化中访问 Instance 可能递归创建或重复初始化。
        /// </summary>
        void OnSingletonInit();
    }
}
