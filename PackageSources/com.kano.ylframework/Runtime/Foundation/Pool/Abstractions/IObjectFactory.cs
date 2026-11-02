namespace YLFramework.Pooling
{
    /// <summary>
    /// 对象工厂接口，负责对象的创建策略。
    /// 泛型参数为协变（out），允许把派生类型的工厂赋给基类型工厂变量。
    /// </summary>
    /// <typeparam name="T">创建的对象类型。</typeparam>
    public interface IObjectFactory<out T>
    {
        /// <summary>
        /// 创建一个新对象。
        /// </summary>
        T Create();
    }
}
