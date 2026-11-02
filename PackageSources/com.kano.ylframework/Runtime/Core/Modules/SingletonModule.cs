namespace YLFramework.Core
{
    /// <summary>为具体模块提供只查询注册表的静态访问入口，不自行构造或启动单例。</summary>
    /// <typeparam name="T">要查询的模块类型，通常为派生模块自身。</typeparam>
    public class SingletonModule<T> : Module where T : Module
    {

        /// <summary>
        /// 查询实际注册实例（包括Starting）；未注册或关闭后返回null，不创建对象。
        /// </summary>
        public static T Instance => ModuleManager.GetModule<T>();

        /// <summary>提供空的业务启动钩子，派生模块可重写以执行自己的启动逻辑。</summary>
        public override void OnStart()
        {
        }
    }

}
