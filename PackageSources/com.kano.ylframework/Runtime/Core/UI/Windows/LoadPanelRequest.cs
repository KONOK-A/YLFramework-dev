using System;
using UnityEngine;

namespace YLFramework.Core
{
    /// <summary>独立 Panel 消费者的结果。进度转发真实资源解析，终态进度为 1 不等于成功，必须检查 Status。</summary>
    public sealed class LoadPanelRequest<T> : YLFramework.Async.CustomAsyncOperation<LoadPanelRequest<T>> where T : Panel
    {
        /// <summary>本次成功交付的引用；不延长 Session 寿命，关闭或复用后不会撤回已完成的终态。</summary>
        public T Panel
        {
            get; private set;
        }
        /// <summary>独立消费者终态，不能由进度或面板引用推断。</summary>
        public LoadViewStatus Status
        {
            get; private set;
        }
        /// <summary>原始失败原因；清理错误附在既有 Core CleanupException Data 中。</summary>
        public Exception Exception
        {
            get; private set;
        }
        private PanelManager.PanelOperation _operation;
        /// <summary>连接本消费者的进度与终态，不另行启动资源生产。</summary>
        internal LoadPanelRequest(PanelManager.PanelOperation operation)
        {
            this._operation = operation;
            operation.ProgressChanged = value => Progress = value;
            operation.Completed = Complete;
        }
        /// <summary>在接受请求的线程取消本消费者；提交、失败收尾或终态后不再接受主动取消。</summary>
        public override void Abort() => _operation?.Abort();
        /// <summary>先固定独立终态并断开操作引用，再通知异步观察者；观察者异常只记录到日志。</summary>
        private void Complete(Panel result, Exception error, LoadViewStatus status)
        {
            Panel = (T)result;
            Status = status;
            Exception = error;
            _operation = null;
            try
            {
                base.Complete(error?.ToString());
            }
            catch (Exception notificationError)
            {
                Debug.LogException(notificationError);
            }
        }
    }
}
