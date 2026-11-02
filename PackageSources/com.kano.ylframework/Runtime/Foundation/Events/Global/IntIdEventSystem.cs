using System;
using YLFramework.Singletons;

namespace YLFramework.Events
{
    /// <summary>
    /// 以整数 ID 为 key 的全局事件系统。
    /// key 通过 <see cref="IConvertible.ToInt32"/> 归一化，因此不同数值类型但数值相同的 key 会共享同一事件。
    /// 同一 key 共享一个事件实例；无监听回调时发送为空操作。
    /// 各系统持有独立 EventBus；Dispose 或 Play Session reset 永久关闭旧实例，截断旧在途通知。
    /// 关闭后注册与发送抛出 ObjectDisposedException，退订为空操作；Instance 可创建新实例。
    /// </summary>
    /// <remarks>
    /// 重复回调分别注册，由调用方保存句柄并按生命周期解绑；按委托取消只移除最后一个匹配的普通委托子序列。
    /// 发送同步执行注册顺序的快照，首个异常会中断后续监听；分发中的普通取消不撤回当前快照。
    /// </remarks>
    public class IntIdEventSystem : ISingleton
    {
        /// <summary>
        /// 全局单例实例。
        /// </summary>
        public static IntIdEventSystem Instance => SingletonProperty<IntIdEventSystem>.Instance;

        private static IntIdEventSystem _sessionInstance;
        private readonly EventBus _bus = new();

        /// <summary>登记会话重置回调，使禁用 Domain Reload 时旧实例也能结束生命周期。</summary>
        static IntIdEventSystem()
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
        /// 创建事件系统实例；外部请通过 <see cref="Instance"/> 访问单例。
        /// </summary>
        protected IntIdEventSystem()
        {
        }

        /// <summary>
        /// 注册指定整数 key 的监听，返回注销句柄；仅非空回调会创建缺失的通道。
        /// 回调参数依次为归一化后的整数 key 与发送时的参数数组。
        /// </summary>
        /// <typeparam name="T">可转换为 int 的 key 类型。</typeparam>
        /// <param name="key">事件 key。</param>
        /// <param name="eventAction">事件监听回调。</param>
        /// <returns>用于注销本次注册的句柄。</returns>
        public IUnregister Register<T>(T key, Action<int, object[]> eventAction) where T : IConvertible
        {
            var intKey = key.ToInt32(null);
            return _bus.Register(intKey, eventAction);
        }

        /// <summary>
        /// 注销指定 key 上的一个监听。
        /// </summary>
        /// <typeparam name="T">可转换为 int 的 key 类型。</typeparam>
        /// <param name="key">事件 key。</param>
        /// <param name="eventAction">要注销的监听回调。</param>
        public void Unregister<T>(T key, Action<int, object[]> eventAction) where T : IConvertible
        {
            var intKey = key.ToInt32(null);
            _bus.Unregister(intKey, eventAction);
        }

        /// <summary>
        /// 终结并移除指定 key 的整个事件及在途通知（该 key 上所有监听失效）。
        /// </summary>
        /// <typeparam name="T">可转换为 int 的 key 类型。</typeparam>
        /// <param name="key">事件 key。</param>
        public void Unregister<T>(T key) where T : IConvertible
        {
            var intKey = key.ToInt32(null);

            _bus.RemoveKey(intKey);
        }

        /// <summary>
        /// 终结并移除所有 key 的事件及在途通知，系统仍可用；关闭后为空操作。
        /// </summary>
        public void UnregisterAll()
        {
            if (!_bus.IsDisposed)
            {
                _bus.ClearKeys();
            }
        }

        /// <summary>
        /// 向指定 key 发送事件，参数作为 object[] 传递；无监听时为空操作。
        /// 发送时回调收到的第一个参数是归一化后的整数 key。
        /// </summary>
        /// <typeparam name="T">可转换为 int 的 key 类型。</typeparam>
        /// <param name="key">事件 key。</param>
        /// <param name="args">事件参数。</param>
        public void Send<T>(T key, params object[] args) where T : IConvertible
        {
            var intKey = key.ToInt32(null);

            _bus.Send(intKey, intKey, args);
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

            if (SingletonProperty<IntIdEventSystem>.TryGetInstance(out var current) && ReferenceEquals(current, this))
            {
                SingletonProperty<IntIdEventSystem>.Dispose();
            }
        }
    }
}
