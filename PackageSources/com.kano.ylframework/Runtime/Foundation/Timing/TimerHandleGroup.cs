using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using YLFramework.Pooling;

namespace YLFramework.Timing
{
    /// <summary>
    /// 收集已有精确句柄的非拥有式取消组，仅主线程使用。
    /// 不转移任务 Scope、不占名称、不创建 Timer 或参与推进。
    /// </summary>
    public sealed class TimerHandleGroup
    {
        private List<TimerHandle> _handles;

        /// <summary>记录一个已有句柄；默认值、终态及重复句柄允许记录，取消不会改写其终态。</summary>
        public void Add(TimerHandle handle) => (_handles ??= ListPool<TimerHandle>.Allocate()).Add(handle);

        /// <summary>解除首次匹配的收集责任，不取消任务，也不改变原 Scope。</summary>
        public bool Remove(TimerHandle handle) => _handles != null && _handles.Remove(handle);

        /// <summary>取消并释放当前收集的旧句柄；回调中新加入的句柄保留到下一轮。</summary>
        public void CancelAll() => Clear();

        /// <summary>
        /// 先释放当前集合责任，再逐项尝试取消并归还旧集合；一个通知异常不丢失其余责任。
        /// 手动取消异常在必要清理后传播，多个异常聚合。组仍可继续 Add。
        /// </summary>
        public void Clear()
        {
            var snapshot = _handles;
            if (snapshot == null)
            {
                return;
            }

            _handles = null;
            List<Exception> errors = null;
            try
            {
                foreach (var handle in snapshot)
                {
                    try
                    {
                        handle.Cancel();
                    }
                    catch (Exception error)
                    {
                        (errors ??= new List<Exception>()).Add(error);
                    }
                }
            }
            finally
            {
                ListPool<TimerHandle>.Recycle(snapshot);
            }
            if (errors == null)
            {
                return;
            }

            if (errors.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }

            throw new AggregateException(errors);
        }
    }
}
