using System;

namespace YLFramework.Events
{
    /// <summary>
    /// 回调句柄释放回调的时机，决定注销时回调的调用与清空顺序。
    /// </summary>
    public enum CallbackReleaseMode
    {
        /// <summary>
        /// 严格在回调成功返回后清空：回调为 null 时调用会抛出 NullReferenceException（零初始化值类型句柄的默认行为），
        /// 回调抛异常时句柄不清空、可重试。
        /// </summary>
        StrictAfterSuccess,

        /// <summary>
        /// 回调成功调用后清空：回调为 null 时为空操作；回调抛异常时句柄不清空、可重试。
        /// </summary>
        AfterSuccess,

        /// <summary>
        /// 调用回调前先清空句柄：回调抛异常或重入调用都不会再次执行回调（SingleUseUnregister 使用的模式）。
        /// </summary>
        BeforeInvoke
    }

    /// <summary>
    /// 值类型回调句柄：持有注销回调与释放时机。
    /// 结构体副本各自持有独立的释放状态：注销一个副本不会影响其他副本，复制后不要依赖两者联动。
    /// </summary>
    public struct CallbackUnregister : IUnregister
    {
        private Action _callback;
        private readonly CallbackReleaseMode _mode;

        /// <summary>
        /// 创建回调句柄。
        /// </summary>
        /// <param name="callback">注销时执行的回调，可为 null（是否允许取决于 <paramref name="mode"/>）。</param>
        /// <param name="mode">回调释放时机，默认 AfterSuccess。</param>
        public CallbackUnregister(Action callback, CallbackReleaseMode mode = CallbackReleaseMode.AfterSuccess)
        {
            _callback = callback;
            _mode = mode;
        }

        /// <summary>
        /// 按释放时机调用并清空回调。重复调用或重入时是否抛异常由 mode 决定：
        /// StrictAfterSuccess 对已清空的句柄会抛出 NullReferenceException，其余模式为空操作。
        /// </summary>
        public void Unregister()
        {
            var callback = _callback;
            if (_mode == CallbackReleaseMode.BeforeInvoke)
            {
                _callback = null;
            }

            if (_mode == CallbackReleaseMode.StrictAfterSuccess)
            {
                callback.Invoke();
            }
            else
            {
                callback?.Invoke();
            }

            _callback = null;
        }
    }
}
