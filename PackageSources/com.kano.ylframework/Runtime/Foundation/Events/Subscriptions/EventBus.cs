using System;
using System.Collections.Generic;
using YLFramework.Singletons;

namespace YLFramework.Events
{
    /// <summary>
    /// 非线程安全的强类型事件上下文。每个键只绑定一个精确参数签名，广播由共享 EventChannel 执行。
    /// 缺失键的发布、退订及空回调注册不建键；清监听保留签名，RemoveKey 才允许重新绑定。
    /// 普通 Send 保留原异常并立即中断；SendAll 尝试所有观察者并汇总异常。
    /// Dispose 永久关闭实例；独立实例不随 Play Session 自动关闭。
    /// </summary>
    /// <remarks>
    /// 重复委托分别注册，返回的句柄只取消对应注册；按委托退订只移除最后一个匹配的普通委托子序列。
    /// 监听在当前调用栈同步执行，注册和普通退订只影响下一轮或嵌套分发，不撤回当前快照。
    /// 调用方负责取消订阅或关闭所属上下文；存活上下文的按键操作均校验有效键，空回调也不会绕过签名检查。
    /// </remarks>
    public sealed class EventBus : IDisposable
    {
        private static EventBus _default;
        private static bool _isSessionResetRegistered;
        private readonly Dictionary<EventKey, EventChannelBase> _channels = new();
        private bool _isDisposed;

        /// <summary>显式选择全局上下文；仅此入口会惰性创建 Default。</summary>
        public static EventBus Default
        {
            get
            {
                if (!_isSessionResetRegistered)
                {
                    SingletonSessionState.RegisterSessionReset(ResetDefault);
                    _isSessionResetRegistered = true;
                }
                return _default ??= new EventBus();
            }
        }

        /// <summary>非创建式取得 Default，可在成功后调用其 Send/SendAll/RemoveKey。</summary>
        /// <param name="bus">已存在的全局上下文；不存在时为 null。</param>
        /// <returns>当前是否已有全局上下文，不会因查询而创建实例。</returns>
        public static bool TryGetDefault(out EventBus bus)
        {
            bus = _default;
            return bus != null;
        }

        /// <summary>非创建式关闭旧 Default；下一次显式访问 Default 建立新实例。</summary>
        public static void ResetDefault()
        {
            var previous = _default;
            _default = null;
            previous?.Dispose();
        }

        /// <summary>实例是否已永久关闭；关闭后仍可查询此标记。</summary>
        public bool IsDisposed => _isDisposed;
        /// <summary>当前绑定的键数量，包含已清空监听但保留签名的键。</summary>
        public int KeyCount
        {
            get
            {
                ThrowIfDisposed();
                return _channels.Count;
            }
        }

        /// <summary>检查有效键是否已绑定通道，不创建缺失的键。</summary>
        public bool HasKey(EventKey key)
        {
            ThrowIfDisposed();
            key.Validate();
            return _channels.ContainsKey(key);
        }

        /// <summary>检查指定键是否至少有一个监听；缺失键返回 false。</summary>
        public bool HasListener(EventKey key) => ListenerCount(key) != 0;

        /// <summary>返回指定键的分发条目数；普通多播按目标计数，Once 整体计数，缺失键返回 0。</summary>
        public int ListenerCount(EventKey key)
        {
            ThrowIfDisposed();
            key.Validate();
            return _channels.TryGetValue(key, out var channel) ? channel.ListenerCount : 0;
        }

        /// <summary>拒绝在上下文永久关闭后执行需要存活通道的操作。</summary>
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(EventBus));
            }
        }

        /// <summary>非创建式取得精确签名的通道；先检查上下文和键，已有其他签名时抛出异常。</summary>
        private TChannel GetExisting<TChannel>(EventKey key) where TChannel : EventChannelBase
        {
            ThrowIfDisposed();
            key.Validate();
            if (!_channels.TryGetValue(key, out var channel))
            {
                return null;
            }

            if (channel.GetType() != typeof(TChannel))
            {
                throw new InvalidOperationException(
                    $"Event key {key} is bound to {channel.GetType()}, not {typeof(TChannel)}. RemoveKey before changing its signature.");
            }
            return (TChannel)channel;
        }

        /// <summary>在指定键的无参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register(EventKey key, Action callback) => RegisterCore(key, callback, false);
        /// <summary>注册无参一次性监听，在进入用户回调前消费，嵌套分发不会重复执行本次注册。</summary>
        public IUnregister RegisterOnce(EventKey key, Action callback) => RegisterCore(key, callback, true);

        /// <summary>校验无参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore(EventKey key, Action callback, bool once)
        {
            var channel = GetExisting<EventChannel>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按无参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister(EventKey key, Action callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布无参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send(EventKey key) => GetExisting<EventChannel>(key)?.Trigger();
        /// <summary>同步尝试无参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll(EventKey key) => GetExisting<EventChannel>(key)?.TriggerAll();

        /// <summary>在指定键的单参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register<T1>(EventKey key, Action<T1> callback) => RegisterCore(key, callback, false);
        /// <summary>在指定键的单参通道注册一次性监听，进入回调前消费本次注册。</summary>
        public IUnregister RegisterOnce<T1>(EventKey key, Action<T1> callback) => RegisterCore(key, callback, true);

        /// <summary>校验单参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore<T1>(EventKey key, Action<T1> callback, bool once)
        {
            var channel = GetExisting<EventChannel<T1>>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel<T1>());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按单参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister<T1>(EventKey key, Action<T1> callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel<T1>>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布单参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send<T1>(EventKey key, T1 p1) => GetExisting<EventChannel<T1>>(key)?.Trigger(p1);
        /// <summary>同步尝试单参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll<T1>(EventKey key, T1 p1) => GetExisting<EventChannel<T1>>(key)?.TriggerAll(p1);

        /// <summary>在指定键的双参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register<T1, T2>(EventKey key, Action<T1, T2> callback) => RegisterCore(key, callback, false);
        /// <summary>在指定键的双参通道注册一次性监听，进入回调前消费本次注册。</summary>
        public IUnregister RegisterOnce<T1, T2>(EventKey key, Action<T1, T2> callback) => RegisterCore(key, callback, true);

        /// <summary>校验双参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore<T1, T2>(EventKey key, Action<T1, T2> callback, bool once)
        {
            var channel = GetExisting<EventChannel<T1, T2>>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel<T1, T2>());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按双参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister<T1, T2>(EventKey key, Action<T1, T2> callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel<T1, T2>>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布双参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send<T1, T2>(EventKey key, T1 p1, T2 p2) => GetExisting<EventChannel<T1, T2>>(key)?.Trigger(p1, p2);
        /// <summary>同步尝试双参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll<T1, T2>(EventKey key, T1 p1, T2 p2) => GetExisting<EventChannel<T1, T2>>(key)?.TriggerAll(p1, p2);

        /// <summary>在指定键的三参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register<T1, T2, T3>(EventKey key, Action<T1, T2, T3> callback) => RegisterCore(key, callback, false);
        /// <summary>在指定键的三参通道注册一次性监听，进入回调前消费本次注册。</summary>
        public IUnregister RegisterOnce<T1, T2, T3>(EventKey key, Action<T1, T2, T3> callback) => RegisterCore(key, callback, true);

        /// <summary>校验三参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore<T1, T2, T3>(EventKey key, Action<T1, T2, T3> callback, bool once)
        {
            var channel = GetExisting<EventChannel<T1, T2, T3>>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel<T1, T2, T3>());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按三参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister<T1, T2, T3>(EventKey key, Action<T1, T2, T3> callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel<T1, T2, T3>>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布三参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send<T1, T2, T3>(EventKey key, T1 p1, T2 p2, T3 p3) => GetExisting<EventChannel<T1, T2, T3>>(key)?.Trigger(p1, p2, p3);
        /// <summary>同步尝试三参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll<T1, T2, T3>(EventKey key, T1 p1, T2 p2, T3 p3) => GetExisting<EventChannel<T1, T2, T3>>(key)?.TriggerAll(p1, p2, p3);

        /// <summary>在指定键的四参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register<T1, T2, T3, T4>(EventKey key, Action<T1, T2, T3, T4> callback) => RegisterCore(key, callback, false);
        /// <summary>在指定键的四参通道注册一次性监听，进入回调前消费本次注册。</summary>
        public IUnregister RegisterOnce<T1, T2, T3, T4>(EventKey key, Action<T1, T2, T3, T4> callback) => RegisterCore(key, callback, true);

        /// <summary>校验四参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore<T1, T2, T3, T4>(EventKey key, Action<T1, T2, T3, T4> callback, bool once)
        {
            var channel = GetExisting<EventChannel<T1, T2, T3, T4>>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel<T1, T2, T3, T4>());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按四参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister<T1, T2, T3, T4>(EventKey key, Action<T1, T2, T3, T4> callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel<T1, T2, T3, T4>>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布四参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send<T1, T2, T3, T4>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4) => GetExisting<EventChannel<T1, T2, T3, T4>>(key)?.Trigger(p1, p2, p3, p4);
        /// <summary>同步尝试四参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll<T1, T2, T3, T4>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4) => GetExisting<EventChannel<T1, T2, T3, T4>>(key)?.TriggerAll(p1, p2, p3, p4);

        /// <summary>在指定键的五参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register<T1, T2, T3, T4, T5>(EventKey key, Action<T1, T2, T3, T4, T5> callback) => RegisterCore(key, callback, false);
        /// <summary>在指定键的五参通道注册一次性监听，进入回调前消费本次注册。</summary>
        public IUnregister RegisterOnce<T1, T2, T3, T4, T5>(EventKey key, Action<T1, T2, T3, T4, T5> callback) => RegisterCore(key, callback, true);

        /// <summary>校验五参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore<T1, T2, T3, T4, T5>(EventKey key, Action<T1, T2, T3, T4, T5> callback, bool once)
        {
            var channel = GetExisting<EventChannel<T1, T2, T3, T4, T5>>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel<T1, T2, T3, T4, T5>());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按五参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister<T1, T2, T3, T4, T5>(EventKey key, Action<T1, T2, T3, T4, T5> callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel<T1, T2, T3, T4, T5>>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布五参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send<T1, T2, T3, T4, T5>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5) => GetExisting<EventChannel<T1, T2, T3, T4, T5>>(key)?.Trigger(p1, p2, p3, p4, p5);
        /// <summary>同步尝试五参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll<T1, T2, T3, T4, T5>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5) => GetExisting<EventChannel<T1, T2, T3, T4, T5>>(key)?.TriggerAll(p1, p2, p3, p4, p5);

        /// <summary>在指定键的六参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register<T1, T2, T3, T4, T5, T6>(EventKey key, Action<T1, T2, T3, T4, T5, T6> callback) => RegisterCore(key, callback, false);
        /// <summary>在指定键的六参通道注册一次性监听，进入回调前消费本次注册。</summary>
        public IUnregister RegisterOnce<T1, T2, T3, T4, T5, T6>(EventKey key, Action<T1, T2, T3, T4, T5, T6> callback) => RegisterCore(key, callback, true);

        /// <summary>校验六参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore<T1, T2, T3, T4, T5, T6>(EventKey key, Action<T1, T2, T3, T4, T5, T6> callback, bool once)
        {
            var channel = GetExisting<EventChannel<T1, T2, T3, T4, T5, T6>>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel<T1, T2, T3, T4, T5, T6>());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按六参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister<T1, T2, T3, T4, T5, T6>(EventKey key, Action<T1, T2, T3, T4, T5, T6> callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel<T1, T2, T3, T4, T5, T6>>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布六参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send<T1, T2, T3, T4, T5, T6>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6) => GetExisting<EventChannel<T1, T2, T3, T4, T5, T6>>(key)?.Trigger(p1, p2, p3, p4, p5, p6);
        /// <summary>同步尝试六参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll<T1, T2, T3, T4, T5, T6>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6) => GetExisting<EventChannel<T1, T2, T3, T4, T5, T6>>(key)?.TriggerAll(p1, p2, p3, p4, p5, p6);

        /// <summary>在指定键的七参通道追加监听，返回本次注册的精确取消句柄。</summary>
        public IUnregister Register<T1, T2, T3, T4, T5, T6, T7>(EventKey key, Action<T1, T2, T3, T4, T5, T6, T7> callback) => RegisterCore(key, callback, false);
        /// <summary>在指定键的七参通道注册一次性监听，进入回调前消费本次注册。</summary>
        public IUnregister RegisterOnce<T1, T2, T3, T4, T5, T6, T7>(EventKey key, Action<T1, T2, T3, T4, T5, T6, T7> callback) => RegisterCore(key, callback, true);

        /// <summary>校验七参签名，并仅为非空回调建立缺失通道后完成注册。</summary>
        private IUnregister RegisterCore<T1, T2, T3, T4, T5, T6, T7>(EventKey key, Action<T1, T2, T3, T4, T5, T6, T7> callback, bool once)
        {
            var channel = GetExisting<EventChannel<T1, T2, T3, T4, T5, T6, T7>>(key);
            if (callback == null)
            {
                return EmptyUnregister.Instance;
            }

            if (channel == null)
            {
                _channels.Add(key, channel = new EventChannel<T1, T2, T3, T4, T5, T6, T7>());
            }

            return once ? channel.RegisterOnce(callback) : channel.Register(callback);
        }

        /// <summary>按七参签名取消最后一个匹配的普通委托子序列；缺失键或已关闭上下文为空操作。</summary>
        public void Unregister<T1, T2, T3, T4, T5, T6, T7>(EventKey key, Action<T1, T2, T3, T4, T5, T6, T7> callback)
        {
            if (_isDisposed)
            {
                return;
            }

            var channel = GetExisting<EventChannel<T1, T2, T3, T4, T5, T6, T7>>(key);
            channel?.Unregister(callback);
        }

        /// <summary>同步发布七参事件，首个回调异常立即传播；缺失键不创建通道。</summary>
        public void Send<T1, T2, T3, T4, T5, T6, T7>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7) => GetExisting<EventChannel<T1, T2, T3, T4, T5, T6, T7>>(key)?.Trigger(p1, p2, p3, p4, p5, p6, p7);
        /// <summary>同步尝试七参通道的全部监听，结束时抛出单个原异常或聚合异常。</summary>
        public void SendAll<T1, T2, T3, T4, T5, T6, T7>(EventKey key, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7) => GetExisting<EventChannel<T1, T2, T3, T4, T5, T6, T7>>(key)?.TriggerAll(p1, p2, p3, p4, p5, p6, p7);

        /// <summary>按 typeof(T) 注册 payload；与命名键入口区分，不按实际 payload 子类型路由。</summary>
        public IUnregister RegisterMessage<T>(Action<T> callback) => Register(EventKey.ForType<T>(), callback);
        /// <summary>在 typeof(T) 的 payload 通道注册一次性回调。</summary>
        public IUnregister RegisterMessageOnce<T>(Action<T> callback) => RegisterOnce(EventKey.ForType<T>(), callback);
        /// <summary>只取消 typeof(T) 通道上最后一个相等的普通委托子序列；已关闭时为空操作。</summary>
        public void UnregisterMessage<T>(Action<T> callback) => Unregister(EventKey.ForType<T>(), callback);
        /// <summary>按泛型声明类型发布 payload；string/int/enum payload 不会被解释为命名键。</summary>
        public void SendMessage<T>(T payload) => Send(EventKey.ForType<T>(), payload);
        /// <summary>构造并发布 typeof(T) 的消息；缺失键时不构造 payload。</summary>
        public void SendMessage<T>() where T : new() => GetExisting<EventChannel<T>>(EventKey.ForType<T>())?.Trigger(new T());
        /// <summary>按 typeof(T) 发布 payload，逐观察者继续并汇总异常。</summary>
        public void SendMessageAll<T>(T payload) => SendAll(EventKey.ForType<T>(), payload);

        /// <summary>仅清监听，保留已有签名；普通在途快照仍按 channel 契约完成。</summary>
        public void ClearListeners(EventKey key)
        {
            ThrowIfDisposed();
            key.Validate();
            if (_channels.TryGetValue(key, out var channel))
            {
                channel.ClearListeners();
            }
        }

        /// <summary>仅清所有键的监听，不删除键或签名。</summary>
        public void ClearListeners()
        {
            ThrowIfDisposed();
            foreach (var channel in _channels.Values)
            {
                channel.ClearListeners();
            }
        }

        /// <summary>终结指定键的 channel 与旧在途快照；删除签名，允许后续重新绑定。已关闭时返回 false。</summary>
        public bool RemoveKey(EventKey key)
        {
            if (_isDisposed)
            {
                return false;
            }

            key.Validate();
            if (!_channels.TryGetValue(key, out var channel))
            {
                return false;
            }

            _channels.Remove(key);
            channel.Dispose();
            return true;
        }

        /// <summary>终结并删除全部键，但 bus 本身保持可用。</summary>
        public void ClearKeys()
        {
            ThrowIfDisposed();
            foreach (var channel in _channels.Values)
            {
                channel.Dispose();
            }

            _channels.Clear();
        }

        /// <summary>永久关闭 bus，清除监听及签名；重复调用无副作用。</summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (ReferenceEquals(_default, this))
            {
                _default = null;
            }

            foreach (var channel in _channels.Values)
            {
                channel.Dispose();
            }

            _channels.Clear();
        }
    }
}
