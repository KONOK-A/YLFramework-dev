using YLFramework.Pooling;

namespace YLFramework.Events
{
    /// <summary>
    /// 轻量事件契约：提供类型擦除的无参注册入口与统一注销。
    /// 带参数的事件额外提供对应的强类型 Register / Unregister / Trigger 方法（见各 EasyEvent 变体）。
    /// </summary>
    public interface IEasyEvent : IEventSource, IOfPool, IPoolType
    {
        /// <summary>
        /// 注销当前事件上的所有回调。
        /// </summary>
        void UnregisterAll();
    }
}
