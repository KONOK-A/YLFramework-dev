using System;
using YLFramework.Singletons;

namespace YLFramework.Events
{
    /// <summary>
    /// 以字符串为 key 的全局事件系统，支持 0~3 个参数的重载。
    /// 同一 key 共享一个事件实例；无监听回调时发送为空操作。
    /// 类型安全：同一个 key 必须始终使用相同的参数签名。
    /// 若按其他签名访问已存在的 key（注册 / 注销 / 发送），会抛出
    /// <see cref="InvalidOperationException"/>，错误信息包含 key、已绑定的事件类型与请求类型；
    /// 如需更换签名，请先调用 <see cref="Unregister(string)"/> 移除旧事件。
    /// 各系统持有独立 EventBus；Dispose 或 Play Session reset 永久关闭旧实例，截断旧在途通知。
    /// 关闭后注册与发送抛出 ObjectDisposedException，退订为空操作；Instance 可创建新实例。
    /// </summary>
    /// <remarks>
    /// 重复回调分别注册，由调用方保存句柄并按生命周期解绑；按委托取消只移除最后一个匹配的普通委托子序列。
    /// 发送同步执行注册顺序的快照，首个异常会中断后续监听；分发中的普通取消不撤回当前快照。
    /// </remarks>
    public class StringEventSystem : ISingleton
    {
        /// <summary>
        /// 全局单例实例。
        /// </summary>
        public static StringEventSystem Instance => SingletonProperty<StringEventSystem>.Instance;

        private static StringEventSystem _sessionInstance;
        private readonly EventBus _bus = new();

        /// <summary>登记会话重置回调，使禁用 Domain Reload 时旧实例也能结束生命周期。</summary>
        static StringEventSystem()
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

        #region 零参

        /// <summary>
        /// 注册无参事件的监听；仅非空回调会在 key 不存在时创建对应通道。
        /// </summary>
        /// <param name="key">事件 key。</param>
        /// <param name="action">事件监听回调。</param>
        /// <returns>用于注销本次注册的句柄。</returns>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public IUnregister Register(string key, Action action)
        {
            return _bus.Register(key, action);
        }

        /// <summary>
        /// 注销无参事件上的一个监听。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Unregister(string key, Action action)
        {
            _bus.Unregister(key, action);
        }

        /// <summary>
        /// 发送无参事件；无监听时为空操作。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Send(string key)
        {
            _bus.Send(key);
        }

        #endregion

        #region 1参

        /// <summary>
        /// 注册单参数事件的监听；仅非空回调会在 key 不存在时创建对应通道。
        /// </summary>
        /// <returns>用于注销本次注册的句柄。</returns>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public IUnregister Register<T>(string key, Action<T> action)
        {
            return _bus.Register(key, action);
        }

        /// <summary>
        /// 注销单参数事件上的一个监听。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Unregister<T>(string key, Action<T> action)
        {
            _bus.Unregister(key, action);
        }

        /// <summary>
        /// 发送单参数事件；无监听时为空操作。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Send<T>(string key, T value)
        {
            _bus.Send(key, value);
        }

        #endregion

        #region 2参

        /// <summary>
        /// 注册双参数事件的监听；仅非空回调会在 key 不存在时创建对应通道。
        /// </summary>
        /// <returns>用于注销本次注册的句柄。</returns>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public IUnregister Register<T1, T2>(string key, Action<T1, T2> action)
        {
            return _bus.Register(key, action);
        }

        /// <summary>
        /// 注销双参数事件上的一个监听。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Unregister<T1, T2>(string key, Action<T1, T2> action)
        {
            _bus.Unregister(key, action);
        }

        /// <summary>
        /// 发送双参数事件；无监听时为空操作。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Send<T1, T2>(string key, T1 param1, T2 param2)
        {
            _bus.Send(key, param1, param2);
        }

        #endregion

        #region 3参

        /// <summary>
        /// 注册三参数事件的监听；仅非空回调会在 key 不存在时创建对应通道。
        /// </summary>
        /// <returns>用于注销本次注册的句柄。</returns>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public IUnregister Register<T1, T2, T3>(string key, Action<T1, T2, T3> action)
        {
            return _bus.Register(key, action);
        }

        /// <summary>
        /// 注销三参数事件上的一个监听。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Unregister<T1, T2, T3>(string key, Action<T1, T2, T3> action)
        {
            _bus.Unregister(key, action);
        }

        /// <summary>
        /// 发送三参数事件；无监听时为空操作。
        /// </summary>
        /// <exception cref="InvalidOperationException">key 已被其他参数签名占用。</exception>
        public void Send<T1, T2, T3>(string key, T1 param1, T2 param2, T3 param3)
        {
            _bus.Send(key, param1, param2, param3);
        }

        #endregion

        /// <summary>
        /// 终结指定 key 的整个事件及在途通知（该 key 上所有监听失效）。
        /// 与签名无关，始终可用；移除后可安全地用其他参数签名重新使用该 key。
        /// </summary>
        /// <param name="key">事件 key。</param>
        public void Unregister(string key)
        {
            _bus.RemoveKey(key);
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

            if (SingletonProperty<StringEventSystem>.TryGetInstance(out var current) && ReferenceEquals(current, this))
            {
                SingletonProperty<StringEventSystem>.Dispose();
            }
        }
    }
}
