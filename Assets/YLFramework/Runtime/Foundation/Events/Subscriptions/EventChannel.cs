using System;

namespace YLFramework.Events
{
    /// <summary>
    /// 无参实例事件。普通广播遇首个异常立即停止，注册/退订只影响下一次或嵌套发布；
    /// 每次注册返回独立身份，Once 在执行用户代码前消费；owner 结束截断旧快照。
    /// </summary>
    /// <remarks>
    /// 回调同步按注册顺序执行，重复委托分别保留；持有者负责取消句柄或在所属生命周期结束时关闭通道。
    /// TriggerAll 在监听之间继续通知，但 Once 的多播委托视为一个监听，其内部异常仍会跳过后续目标。
    /// </remarks>
    public class EventChannel : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action> _listeners;
        /// <summary>创建独立实例事件，不加入全局上下文。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <inheritdoc />
        public IUnregister Register(Action callback) => _listeners.Register(callback);
        /// <summary>只执行一次；多播回调整体作为一个 Once 监听。</summary>
        public IUnregister RegisterOnce(Action callback) => _listeners.Register(callback, true);
        /// <summary>先同步调用，成功后再注册；失败不会留下本次订阅。</summary>
        public IUnregister RegisterWithACall(Action callback)
        {
            ThrowIfDisposed();
            callback?.Invoke();
            return Register(callback);
        }
        /// <summary>移除最后一个相等的普通委托子序列；Once 使用其精确 token 取消。</summary>
        public void Unregister(Action callback) => _listeners.Unregister(callback);
        /// <summary>按注册顺序发布，首个异常原样中断后续监听。</summary>
        public void Trigger() => _listeners.Dispatch(new Invocation(), false);
        /// <summary>显式逐监听继续通知，最后抛原异常或 AggregateException。</summary>
        public void TriggerAll() => _listeners.Dispatch(new Invocation(), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <summary>无参分发的调用器，不捕获额外状态。</summary>
        private readonly struct Invocation : IEventInvocation<Action>
        {
            /// <inheritdoc />
            public void Invoke(Action callback) => callback();
        }
    }

    /// <summary>单参数实例事件；使用声明类型分发，无参观察与强类型监听共享同一顺序。</summary>
    /// <remarks>重复注册、同步快照、异常与解绑责任遵循无参 EventChannel 的规则。</remarks>
    public class EventChannel<T1> : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action<T1>> _listeners;
        /// <summary>创建独立实例事件。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action<T1>>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <summary>注册强类型回调，null 返回空句柄。</summary>
        public IUnregister Register(Action<T1> callback) => _listeners.Register(callback);
        /// <summary>用户代码执行前消费本次注册，嵌套发布不会再次执行。</summary>
        public IUnregister RegisterOnce(Action<T1> callback) => _listeners.Register(callback, true);
        /// <summary>移除最后一个相等的普通委托子序列；Once 使用其 token。</summary>
        public void Unregister(Action<T1> callback) => _listeners.Unregister(callback);
        /// <summary>同步传入事件参数，首个回调异常立即中断后续监听。</summary>
        public void Trigger(T1 p1) => _listeners.Dispatch(new Invocation(p1), false);
        /// <summary>显式继续通知并汇总异常。</summary>
        public void TriggerAll(T1 p1) => _listeners.Dispatch(new Invocation(p1), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <inheritdoc />
        IUnregister IEventSource.Register(Action callback)
        {
            ThrowIfDisposed();
            return callback == null ? EmptyUnregister.Instance : Register(_ => callback());
        }
        /// <summary>保存本轮单个参数，以强类型方式调用监听。</summary>
        private readonly struct Invocation : IEventInvocation<Action<T1>>
        {
            private readonly T1 _p1;
            internal Invocation(T1 p1) => _p1 = p1;
            /// <inheritdoc />
            public void Invoke(Action<T1> callback) => callback(_p1);
        }
    }

    /// <summary>双参数实例事件；快照、精确身份及生命周期语义与无参 EventChannel 一致。</summary>
    public class EventChannel<T1, T2> : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action<T1, T2>> _listeners;
        /// <summary>创建独立实例事件。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action<T1, T2>>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <summary>注册强类型回调，返回本次精确身份。</summary>
        public IUnregister Register(Action<T1, T2> callback) => _listeners.Register(callback);
        /// <summary>在用户代码前消费的单次监听。</summary>
        public IUnregister RegisterOnce(Action<T1, T2> callback) => _listeners.Register(callback, true);
        /// <summary>移除最后相等的普通委托子序列。</summary>
        public void Unregister(Action<T1, T2> callback) => _listeners.Unregister(callback);
        /// <summary>同步传入两个事件参数，首个回调异常立即中断后续监听。</summary>
        public void Trigger(T1 p1, T2 p2) => _listeners.Dispatch(new Invocation(p1, p2), false);
        /// <summary>显式继续通知并汇总异常。</summary>
        public void TriggerAll(T1 p1, T2 p2) => _listeners.Dispatch(new Invocation(p1, p2), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <inheritdoc />
        IUnregister IEventSource.Register(Action callback)
        {
            ThrowIfDisposed();
            return callback == null ? EmptyUnregister.Instance : Register((_, __) => callback());
        }
        /// <summary>保存本轮两个参数，以强类型方式调用监听。</summary>
        private readonly struct Invocation : IEventInvocation<Action<T1, T2>>
        {
            private readonly T1 _p1;
            private readonly T2 _p2;
            internal Invocation(T1 p1, T2 p2)
            {
                _p1 = p1;
                _p2 = p2;
            }
            /// <inheritdoc />
            public void Invoke(Action<T1, T2> callback) => callback(_p1, _p2);
        }
    }

    /// <summary>三参数实例事件；无参观察不另建分发通道。</summary>
    /// <remarks>重复注册、同步快照、异常与解绑责任遵循无参 EventChannel 的规则。</remarks>
    public class EventChannel<T1, T2, T3> : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action<T1, T2, T3>> _listeners;
        /// <summary>创建独立实例事件。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action<T1, T2, T3>>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <summary>注册强类型回调，返回本次精确身份。</summary>
        public IUnregister Register(Action<T1, T2, T3> callback) => _listeners.Register(callback);
        /// <summary>在用户代码前消费的单次监听。</summary>
        public IUnregister RegisterOnce(Action<T1, T2, T3> callback) => _listeners.Register(callback, true);
        /// <summary>移除最后相等的普通委托子序列。</summary>
        public void Unregister(Action<T1, T2, T3> callback) => _listeners.Unregister(callback);
        /// <summary>同步传入三个事件参数，首个回调异常立即中断后续监听。</summary>
        public void Trigger(T1 p1, T2 p2, T3 p3) => _listeners.Dispatch(new Invocation(p1, p2, p3), false);
        /// <summary>显式继续通知并汇总异常。</summary>
        public void TriggerAll(T1 p1, T2 p2, T3 p3) => _listeners.Dispatch(new Invocation(p1, p2, p3), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <inheritdoc />
        IUnregister IEventSource.Register(Action callback)
        {
            ThrowIfDisposed();
            return callback == null ? EmptyUnregister.Instance : Register((_, __, ___) => callback());
        }
        /// <summary>保存本轮三个参数，以强类型方式调用监听。</summary>
        private readonly struct Invocation : IEventInvocation<Action<T1, T2, T3>>
        {
            private readonly T1 _p1;
            private readonly T2 _p2;
            private readonly T3 _p3;
            internal Invocation(T1 p1, T2 p2, T3 p3)
            {
                _p1 = p1;
                _p2 = p2;
                _p3 = p3;
            }
            /// <inheritdoc />
            public void Invoke(Action<T1, T2, T3> callback) => callback(_p1, _p2, _p3);
        }
    }

    /// <summary>四参数实例事件，直接调用强类型 Action，不使用反射或 object[]。</summary>
    /// <remarks>重复注册、同步快照、异常与解绑责任遵循无参 EventChannel 的规则。</remarks>
    public class EventChannel<T1, T2, T3, T4> : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action<T1, T2, T3, T4>> _listeners;
        /// <summary>创建独立实例事件。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action<T1, T2, T3, T4>>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <summary>注册强类型回调，返回本次精确身份。</summary>
        public IUnregister Register(Action<T1, T2, T3, T4> callback) => _listeners.Register(callback);
        /// <summary>在用户代码前消费的单次监听。</summary>
        public IUnregister RegisterOnce(Action<T1, T2, T3, T4> callback) => _listeners.Register(callback, true);
        /// <summary>移除最后相等的普通委托子序列。</summary>
        public void Unregister(Action<T1, T2, T3, T4> callback) => _listeners.Unregister(callback);
        /// <summary>同步传入四个事件参数，首个回调异常立即中断后续监听。</summary>
        public void Trigger(T1 p1, T2 p2, T3 p3, T4 p4) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4), false);
        /// <summary>显式继续通知并汇总异常。</summary>
        public void TriggerAll(T1 p1, T2 p2, T3 p3, T4 p4) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <inheritdoc />
        IUnregister IEventSource.Register(Action callback)
        {
            ThrowIfDisposed();
            return callback == null ? EmptyUnregister.Instance : Register((_, __, ___, ____) => callback());
        }
        /// <summary>保存本轮四个参数，以强类型方式调用监听。</summary>
        private readonly struct Invocation : IEventInvocation<Action<T1, T2, T3, T4>>
        {
            private readonly T1 _p1;
            private readonly T2 _p2;
            private readonly T3 _p3;
            private readonly T4 _p4;
            internal Invocation(T1 p1, T2 p2, T3 p3, T4 p4)
            {
                _p1 = p1;
                _p2 = p2;
                _p3 = p3;
                _p4 = p4;
            }
            /// <inheritdoc />
            public void Invoke(Action<T1, T2, T3, T4> callback) => callback(_p1, _p2, _p3, _p4);
        }
    }

    /// <summary>五参数实例事件，直接调用强类型 Action。</summary>
    /// <remarks>重复注册、同步快照、异常与解绑责任遵循无参 EventChannel 的规则。</remarks>
    public class EventChannel<T1, T2, T3, T4, T5> : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action<T1, T2, T3, T4, T5>> _listeners;
        /// <summary>创建独立实例事件。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action<T1, T2, T3, T4, T5>>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <summary>注册强类型回调，返回本次精确身份。</summary>
        public IUnregister Register(Action<T1, T2, T3, T4, T5> callback) => _listeners.Register(callback);
        /// <summary>在用户代码前消费的单次监听。</summary>
        public IUnregister RegisterOnce(Action<T1, T2, T3, T4, T5> callback) => _listeners.Register(callback, true);
        /// <summary>移除最后相等的普通委托子序列。</summary>
        public void Unregister(Action<T1, T2, T3, T4, T5> callback) => _listeners.Unregister(callback);
        /// <summary>同步传入五个事件参数，首个回调异常立即中断后续监听。</summary>
        public void Trigger(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4, p5), false);
        /// <summary>显式继续通知并汇总异常。</summary>
        public void TriggerAll(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4, p5), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <inheritdoc />
        IUnregister IEventSource.Register(Action callback)
        {
            ThrowIfDisposed();
            return callback == null ? EmptyUnregister.Instance : Register((_, __, ___, ____, _____) => callback());
        }
        /// <summary>保存本轮五个参数，以强类型方式调用监听。</summary>
        private readonly struct Invocation : IEventInvocation<Action<T1, T2, T3, T4, T5>>
        {
            private readonly T1 _p1;
            private readonly T2 _p2;
            private readonly T3 _p3;
            private readonly T4 _p4;
            private readonly T5 _p5;
            internal Invocation(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5)
            {
                _p1 = p1;
                _p2 = p2;
                _p3 = p3;
                _p4 = p4;
                _p5 = p5;
            }
            /// <inheritdoc />
            public void Invoke(Action<T1, T2, T3, T4, T5> callback) => callback(_p1, _p2, _p3, _p4, _p5);
        }
    }

    /// <summary>六参数实例事件，直接调用强类型 Action。</summary>
    /// <remarks>重复注册、同步快照、异常与解绑责任遵循无参 EventChannel 的规则。</remarks>
    public class EventChannel<T1, T2, T3, T4, T5, T6> : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action<T1, T2, T3, T4, T5, T6>> _listeners;
        /// <summary>创建独立实例事件。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action<T1, T2, T3, T4, T5, T6>>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <summary>注册强类型回调，返回本次精确身份。</summary>
        public IUnregister Register(Action<T1, T2, T3, T4, T5, T6> callback) => _listeners.Register(callback);
        /// <summary>在用户代码前消费的单次监听。</summary>
        public IUnregister RegisterOnce(Action<T1, T2, T3, T4, T5, T6> callback) => _listeners.Register(callback, true);
        /// <summary>移除最后相等的普通委托子序列。</summary>
        public void Unregister(Action<T1, T2, T3, T4, T5, T6> callback) => _listeners.Unregister(callback);
        /// <summary>同步传入六个事件参数，首个回调异常立即中断后续监听。</summary>
        public void Trigger(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4, p5, p6), false);
        /// <summary>显式继续通知并汇总异常。</summary>
        public void TriggerAll(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4, p5, p6), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <inheritdoc />
        IUnregister IEventSource.Register(Action callback)
        {
            ThrowIfDisposed();
            return callback == null ? EmptyUnregister.Instance : Register((_, __, ___, ____, _____, ______) => callback());
        }
        /// <summary>保存本轮六个参数，以强类型方式调用监听。</summary>
        private readonly struct Invocation : IEventInvocation<Action<T1, T2, T3, T4, T5, T6>>
        {
            private readonly T1 _p1;
            private readonly T2 _p2;
            private readonly T3 _p3;
            private readonly T4 _p4;
            private readonly T5 _p5;
            private readonly T6 _p6;
            internal Invocation(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6)
            {
                _p1 = p1;
                _p2 = p2;
                _p3 = p3;
                _p4 = p4;
                _p5 = p5;
                _p6 = p6;
            }
            /// <inheritdoc />
            public void Invoke(Action<T1, T2, T3, T4, T5, T6> callback) => callback(_p1, _p2, _p3, _p4, _p5, _p6);
        }
    }

    /// <summary>七参数实例事件，不要求将多参数消息强制改成 DTO。</summary>
    /// <remarks>重复注册、同步快照、异常与解绑责任遵循无参 EventChannel 的规则。</remarks>
    public class EventChannel<T1, T2, T3, T4, T5, T6, T7> : EventChannelBase, IEventSource
    {
        private readonly EventRegistrations<Action<T1, T2, T3, T4, T5, T6, T7>> _listeners;
        /// <summary>创建独立实例事件。</summary>
        public EventChannel() => _listeners = new EventRegistrations<Action<T1, T2, T3, T4, T5, T6, T7>>(this);
        /// <inheritdoc />
        public override int ListenerCount => _listeners.Count;
        /// <summary>注册强类型回调，返回本次精确身份。</summary>
        public IUnregister Register(Action<T1, T2, T3, T4, T5, T6, T7> callback) => _listeners.Register(callback);
        /// <summary>在用户代码前消费的单次监听。</summary>
        public IUnregister RegisterOnce(Action<T1, T2, T3, T4, T5, T6, T7> callback) => _listeners.Register(callback, true);
        /// <summary>移除最后相等的普通委托子序列。</summary>
        public void Unregister(Action<T1, T2, T3, T4, T5, T6, T7> callback) => _listeners.Unregister(callback);
        /// <summary>同步传入七个事件参数，首个回调异常立即中断后续监听。</summary>
        public void Trigger(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4, p5, p6, p7), false);
        /// <summary>显式继续通知并汇总异常。</summary>
        public void TriggerAll(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7) => _listeners.Dispatch(new Invocation(p1, p2, p3, p4, p5, p6, p7), true);
        /// <inheritdoc />
        protected override void ClearListenersCore() => _listeners.Clear();
        /// <inheritdoc />
        IUnregister IEventSource.Register(Action callback)
        {
            ThrowIfDisposed();
            return callback == null ? EmptyUnregister.Instance : Register((_, __, ___, ____, _____, ______, _______) => callback());
        }
        /// <summary>保存本轮七个参数，以强类型方式调用监听。</summary>
        private readonly struct Invocation : IEventInvocation<Action<T1, T2, T3, T4, T5, T6, T7>>
        {
            private readonly T1 _p1;
            private readonly T2 _p2;
            private readonly T3 _p3;
            private readonly T4 _p4;
            private readonly T5 _p5;
            private readonly T6 _p6;
            private readonly T7 _p7;
            internal Invocation(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7)
            {
                _p1 = p1;
                _p2 = p2;
                _p3 = p3;
                _p4 = p4;
                _p5 = p5;
                _p6 = p6;
                _p7 = p7;
            }
            /// <inheritdoc />
            public void Invoke(Action<T1, T2, T3, T4, T5, T6, T7> callback) => callback(_p1, _p2, _p3, _p4, _p5, _p6, _p7);
        }
    }
}
