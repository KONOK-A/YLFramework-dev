using System;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 非公开无参构造函数的查找与调用工具，供 NonPublicObjectFactory 与 MultiObjectFactory 共用。
    /// </summary>
    internal static class NonPublicConstructorFinder
    {
        /// <summary>
        /// 查找类型上的无参非公开构造函数。
        /// </summary>
        /// <exception cref="MissingMethodException">类型没有非公开无参构造函数。</exception>
        internal static ConstructorInfo Find(Type type)
        {
            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var constructor in constructors)
            {
                if (constructor.GetParameters().Length == 0)
                {
                    return constructor;
                }
            }

            throw new MissingMethodException(
                $"[Pool] {type.FullName} 未找到非公开无参构造函数。");
        }

        /// <summary>
        /// 调用非公开无参构造函数创建实例。
        /// </summary>
        internal static object Create(Type type)
        {
            return Invoke(Find(type));
        }

        /// <summary>
        /// 调用已解析的构造函数创建实例；构造函数内部异常解包后原样抛出（保留原始堆栈）。
        /// </summary>
        internal static object Invoke(ConstructorInfo constructor)
        {
            try
            {
                return constructor.Invoke(null);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw; // 不可达：满足编译器控制流要求
            }
        }
    }
}
