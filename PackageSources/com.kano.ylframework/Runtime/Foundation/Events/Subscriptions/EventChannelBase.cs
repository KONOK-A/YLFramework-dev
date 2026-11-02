using System;

namespace YLFramework.Events
{
    /// <summary>
    /// 实例事件的生命周期。普通退订保留在途快照；ResetOwner 与 Dispose 截断旧 owner 的快照。
    /// 事件与句柄不是线程安全的，应由所属上下文串行使用。
    /// </summary>
    public abstract class EventChannelBase : IDisposable
    {
        private object _incarnation = new();

        /// <summary>是否永久关闭。关闭后不能注册、发布或重置。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>普通注册展开后的 target 数量；Once 的 multicast 回调作为一个监听。</summary>
        public abstract int ListenerCount
        {
            get;
        }
        /// <summary>是否存在监听。</summary>
        public bool HasListener => ListenerCount != 0;

        /// <summary>只清监听，不截断当前快照；旧 token 不会影响后续注册。</summary>
        public void ClearListeners()
        {
            ThrowIfDisposed();
            ClearListenersCore();
            OnListenersChanged();
        }

        /// <summary>终结当前 owner 代次并清监听；同一实例仍可用于下一生命周期。</summary>
        public void ResetOwner()
        {
            ThrowIfDisposed();
            _incarnation = new object();
            ClearListenersCore();
            OnListenersChanged();
        }

        /// <summary>永久关闭，撤销所有身份并截断在途快照；重复调用为空操作。</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _incarnation = new object();
            ClearListenersCore();
            OnListenersChanged();
        }

        /// <summary>清除具体签名的注册记录。</summary>
        protected abstract void ClearListenersCore();
        /// <summary>监听变更已提交后的组合适配钩子，不参与普通广播异常策略。</summary>
        protected virtual void OnListenersChanged()
        {
        }
        /// <summary>由注册集合在变更提交后通知派生通道，供组合事件维护连接。</summary>
        internal void NotifyListenersChanged() => OnListenersChanged();
        /// <summary>当前生命周期的引用身份；重置或关闭时更换。</summary>
        internal object Incarnation => _incarnation;
        /// <summary>检查快照是否仍属于当前代次，避免旧通知进入下一生命周期。</summary>
        internal bool IsCurrent(object incarnation) => ReferenceEquals(_incarnation, incarnation);
        /// <summary>拒绝在永久关闭后开始注册、发布或重置操作。</summary>
        internal void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }
    }
}
