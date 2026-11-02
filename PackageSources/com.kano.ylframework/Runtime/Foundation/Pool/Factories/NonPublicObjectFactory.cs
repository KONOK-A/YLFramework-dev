using System.Reflection;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 使用非公开无参构造函数创建对象的工厂。
    /// 构造函数只在工厂实例化时解析一次并缓存，避免每次创建都反射查找。
    /// </summary>
    /// <typeparam name="T">创建的对象类型。</typeparam>
    public class NonPublicObjectFactory<T> : IObjectFactory<T> where T : class
    {
        private readonly ConstructorInfo _constructor;

        /// <summary>
        /// 创建工厂并解析目标类型的非公开无参构造函数。
        /// </summary>
        /// <exception cref="System.MissingMethodException">类型没有非公开无参构造函数。</exception>
        public NonPublicObjectFactory()
        {
            _constructor = NonPublicConstructorFinder.Find(typeof(T));
        }

        /// <summary>
        /// 通过非公开无参构造函数创建实例；构造函数内部异常解包后原样抛出（保留原始堆栈）。
        /// </summary>
        public T Create()
        {
            return (T)NonPublicConstructorFinder.Invoke(_constructor);
        }
    }
}
