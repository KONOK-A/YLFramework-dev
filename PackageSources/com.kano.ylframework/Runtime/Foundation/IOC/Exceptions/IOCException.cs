using System;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// IOC 容器注册、解析与注入错误的异常基类；null 参数等入口校验仍可抛出标准参数异常。
    /// </summary>
    public class IOCException : Exception
    {
        /// <summary>
        /// 使用指定错误消息创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        public IOCException(string message) : base(message)
        {
        }

        /// <summary>
        /// 使用指定错误消息与内部异常创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        /// <param name="innerException">引发当前异常的内部异常。</param>
        public IOCException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
