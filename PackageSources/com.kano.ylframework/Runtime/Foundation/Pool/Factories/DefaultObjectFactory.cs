using System.Reflection;
using System.Runtime.ExceptionServices;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 默认对象工厂：通过无参构造函数创建对象。
    /// </summary>
    /// <typeparam name="T">创建的对象类型。</typeparam>
    public class DefaultObjectFactory<T> : IObjectFactory<T> where T : class, new()
    {
        /// <summary>
        /// 创建 T 的新实例。构造函数抛出异常时解包并原样重新抛出（保留原始堆栈），
        /// 避免不同运行时的 TargetInvocationException 包装差异。
        /// </summary>
        public T Create()
        {
            try
            {
                return new T();
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw; // 不可达：满足编译器控制流要求
            }
        }
    }
}
