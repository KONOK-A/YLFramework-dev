using System;
using YLFramework.Singletons;

namespace YLFramework.Events
{
    /// <summary>
    /// 以事件类型为 key 的全局事件系统：每个泛型类型对应一个事件。
    /// 按泛型声明类型路由，独立 EventBus 不参与对象池。
    /// Dispose 或 Play Session reset 永久关闭旧实例并截断旧在途通知。
    /// 关闭后注册与发送抛出 ObjectDisposedException，退订为空操作；Instance 可创建新实例。
    /// </summary>
    /// <remarks>
    /// 重复回调分别注册，由调用方保存句柄并按生命周期解绑；按委托取消只移除最后一个匹配的普通委托子序列。
    /// 发送同步执行注册顺序的快照，首个异常会中断后续监听；分发中的普通取消不撤回当前快照。
    /// </remarks>
    public class TypeEventSystem : ISingleton
    {
        private static TypeEventSystem _sessionInstance;
        private readonly EventBus _bus = new();

        /// <summary>登记会话重置回调，使禁用 Domain Reload 时旧实例也能结束生命周期。</summary>
        static TypeEventSystem()
        {
            SingletonSessionState.RegisterSessionReset(ResetSessionState);
        }

        /// <summary>摘除本会话的独立实例引用，再永久关闭旧事件上下文。</summary>
        private static void ResetSessionState()
        {
            // Singleton 自身可能已先清入口，独立保留本轮待关闭实例。
            var previous = _sessionInstance;
            _sessionInstance = null;
            previous?.Dispose();
        }

        /// <summary>
        /// 全局单例实例。
        /// </summary>
        public static TypeEventSystem Instance => SingletonProperty<TypeEventSystem>.Instance;

        /// <summary>
        /// 默认构造并同步发送事件实例；类型通道未建立时不构造，已有空通道时仍会构造。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        public void Send<T>() where T : new()
        {
            _bus.SendMessage<T>();
        }

        /// <summary>
        /// 发送事件实例；无监听时为空操作。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        /// <param name="e">事件实例。</param>
        public void Send<T>(T e)
        {
            _bus.SendMessage(e);
        }

        /// <summary>
        /// 注册指定事件类型的监听，返回注销句柄；仅非空回调会创建缺失的类型通道。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        /// <param name="eventAction">事件监听回调。</param>
        /// <returns>用于注销本次注册的句柄。</returns>
        public IUnregister Register<T>(Action<T> eventAction)
        {
            return _bus.RegisterMessage(eventAction);
        }

        /// <summary>
        /// 注销指定事件类型上最后一个匹配的普通委托子序列，不撤回已开始的分发快照。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        /// <param name="eventAction">要注销的监听回调。</param>
        public void Unregister<T>(Action<T> eventAction)
        {
            _bus.UnregisterMessage(eventAction);
        }

        /// <inheritdoc />
        public void OnSingletonInit()
        {
            _bus.ClearKeys();
            _sessionInstance = this;
        }

        /// <summary>
        /// 永久关闭自身并注销所有监听；仅释放仍指向自身的单例入口，重复调用为空操作。
        /// 下次访问 <see cref="Instance"/> 会创建新实例，旧实例不能影响它。
        /// </summary>
        public void Dispose()
        {
            if (_bus.IsDisposed)
            {
                return;
            }

            _bus.Dispose();
            if (ReferenceEquals(_sessionInstance, this))
            {
                _sessionInstance = null;
            }

            if (SingletonProperty<TypeEventSystem>.TryGetInstance(out var current) && ReferenceEquals(current, this))
            {
                SingletonProperty<TypeEventSystem>.Dispose();
            }
        }
    }
}
