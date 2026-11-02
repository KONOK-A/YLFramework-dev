using System;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 解析失败：必要依赖未注册、目标类型不可实例化、构造函数选择歧义或构造函数自身抛异常。
    /// </summary>
    public class IOCResolutionException : IOCException
    {
        /// <summary>
        /// 解析失败时的依赖链，例如 "PlayerController -> IWeapon -> IAmmoProvider"；无上下文时为 null。
        /// </summary>
        public string DependencyPath
        {
            get;
        }

        /// <summary>
        /// 使用指定错误消息创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        public IOCResolutionException(string message) : this(message, null)
        {
        }

        /// <summary>
        /// 使用指定错误消息与依赖链创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        /// <param name="dependencyPath">解析失败时的依赖链。</param>
        public IOCResolutionException(string message, string dependencyPath) : base(message)
        {
            DependencyPath = dependencyPath;
        }

        /// <summary>
        /// 使用指定错误消息、依赖链与内部异常创建异常。
        /// </summary>
        /// <param name="message">错误消息。</param>
        /// <param name="dependencyPath">解析失败时的依赖链。</param>
        /// <param name="innerException">引发当前异常的内部异常（如构造函数抛出的原始异常）。</param>
        public IOCResolutionException(string message, string dependencyPath, Exception innerException)
            : base(message, innerException)
        {
            DependencyPath = dependencyPath;
        }
    }
}
