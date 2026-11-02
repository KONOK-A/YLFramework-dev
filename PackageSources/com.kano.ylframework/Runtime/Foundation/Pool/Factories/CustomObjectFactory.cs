using System;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 使用自定义委托创建对象的工厂。
    /// </summary>
    /// <typeparam name="T">创建的对象类型。</typeparam>
    public class CustomObjectFactory<T> : IObjectFactory<T>
    {
        private readonly Func<T> _factoryMethod;

        /// <summary>
        /// 创建工厂。
        /// </summary>
        /// <param name="factoryMethod">对象创建委托，不能为 null。</param>
        /// <exception cref="ArgumentNullException">factoryMethod 为 null。</exception>
        public CustomObjectFactory(Func<T> factoryMethod)
        {
            _factoryMethod = factoryMethod ?? throw new ArgumentNullException(nameof(factoryMethod));
        }

        /// <summary>
        /// 调用自定义委托创建对象；委托本身返回 null 时结果为 null。
        /// </summary>
        public T Create()
        {
            return _factoryMethod();
        }
    }
}
