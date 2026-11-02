using System;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 注册的目标类型或非空实例不可赋值给服务类型时抛出；null 类型由入口抛出 ArgumentNullException。
    /// </summary>
    public class IOCRegistrationException : IOCException
    {
        /// <summary>
        /// 使用指定错误消息创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        public IOCRegistrationException(string message) : base(message)
        {
        }

        /// <summary>
        /// 使用指定错误消息与内部异常创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        /// <param name="innerException">引发当前异常的内部异常。</param>
        public IOCRegistrationException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
