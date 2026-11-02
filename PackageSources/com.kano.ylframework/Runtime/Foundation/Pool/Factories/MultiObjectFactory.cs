using System;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 复合对象工厂：集中提供多种基于反射的创建方式。
    /// 仅负责创建对象，不承担依赖注入或对象注册职责。
    /// </summary>
    /// <remarks>对象的后续清理归调用方；初始化回调抛错会直接传播，不在此回滚或释放已创建对象。</remarks>
    public static class MultiObjectFactory
    {
        /// <summary>
        /// 使用指定构造参数创建对象；参数为空时等价于调用无参公共构造函数。
        /// </summary>
        /// <param name="type">目标类型。</param>
        /// <param name="constructorArgs">构造函数参数。</param>
        public static object Create(Type type, params object[] constructorArgs)
        {
            return Activator.CreateInstance(type, constructorArgs);
        }

        /// <summary>
        /// 使用指定构造参数创建对象。
        /// </summary>
        /// <typeparam name="T">目标类型。</typeparam>
        /// <param name="constructorArgs">传给公开构造函数的参数。</param>
        public static T Create<T>(params object[] constructorArgs)
        {
            return (T)Create(typeof(T), constructorArgs);
        }

        /// <summary>
        /// 使用非公开无参构造函数创建对象。
        /// </summary>
        /// <param name="type">目标类型。</param>
        /// <exception cref="MissingMethodException">类型没有非公开无参构造函数。</exception>
        public static object CreateNonPublicCtorObject(Type type)
        {
            return NonPublicConstructorFinder.Create(type);
        }

        /// <summary>
        /// 使用非公开无参构造函数创建对象。
        /// </summary>
        /// <typeparam name="T">目标类型。</typeparam>
        public static T CreateNonPublicCtorObject<T>()
        {
            return (T)CreateNonPublicCtorObject(typeof(T));
        }

        /// <summary>
        /// 创建对象并立即执行初始化回调。
        /// </summary>
        /// <param name="type">目标类型。</param>
        /// <param name="onObjectCreate">创建成功后的初始化回调，可为 null。</param>
        /// <param name="constructorArgs">构造函数参数。</param>
        public static object CreateObjectWithInitAction(Type type, Action<object> onObjectCreate,
            params object[] constructorArgs)
        {
            var obj = Create(type, constructorArgs);
            onObjectCreate?.Invoke(obj);
            return obj;
        }

        /// <summary>
        /// 创建对象并立即执行初始化回调（强类型版本，行为与弱类型版本一致）。
        /// </summary>
        /// <typeparam name="T">目标类型。</typeparam>
        /// <param name="onObjectCreate">创建成功后的初始化回调，可为 null。</param>
        /// <param name="constructorArgs">传给公开构造函数的参数。</param>
        public static T CreateWithInitialAction<T>(Action<T> onObjectCreate,
            params object[] constructorArgs)
        {
            var obj = Create<T>(constructorArgs);
            onObjectCreate?.Invoke(obj);
            return obj;
        }
    }
}
