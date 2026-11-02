using System;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 成员注入失败：成员不可注入（readonly / 无 setter / 索引器）、必要依赖缺失或成员写入抛错。
    /// </summary>
    public class IOCInjectionException : IOCException
    {
        /// <summary>
        /// 注入失败时的依赖链，供诊断使用；无上下文时为 null。
        /// </summary>
        public string DependencyPath
        {
            get;
        }

        /// <summary>
        /// 使用指定错误消息创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        public IOCInjectionException(string message) : this(message, null)
        {
        }

        /// <summary>
        /// 使用指定错误消息与依赖链创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        /// <param name="dependencyPath">注入失败时的依赖链。</param>
        public IOCInjectionException(string message, string dependencyPath) : base(message)
        {
            DependencyPath = dependencyPath;
        }

        /// <summary>
        /// 使用指定错误消息、依赖链与内部异常创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        /// <param name="dependencyPath">注入失败时的依赖链。</param>
        /// <param name="innerException">引发当前异常的内部异常。</param>
        public IOCInjectionException(string message, string dependencyPath, Exception innerException)
            : base(message, innerException)
        {
            DependencyPath = dependencyPath;
        }
    }
}
