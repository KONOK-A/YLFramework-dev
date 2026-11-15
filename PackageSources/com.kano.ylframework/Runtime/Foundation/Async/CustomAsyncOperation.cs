using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using UnityEngine;

namespace YLFramework.Async
{
    /// <summary>可供 Unity 协程等待的请求基类，集中保存进度、结束状态和完成订阅。</summary>
    /// <typeparam name="T">具体请求类型，派生类通常传入自身类型，使回调直接收到该请求。</typeparam>
    /// <remarks>
    /// 本类不启动任务，也不切换线程；派生类负责推进实际工作，再调用完成入口。
    /// IsDone 只表示等待已结束，不代表成功，调用方还需检查 Error 或具体请求的结果。
    /// 所有通知均同步执行，订阅者抛出的异常会向外传播并中断后续通知，不提供逐个回调的异常隔离。
    /// </remarks>
    public class CustomAsyncOperation<T> : CustomYieldInstruction where T : CustomAsyncOperation<T>
    {
        private Action<T> _completed;
        private bool _isCompleted;
        private bool _completionFinished;
        private float _progress;
        private TaskCompletionSource<T> _completionSource;
        private string _error = string.Empty;
        private const string CheckingError = "正在检测中...";

        /// <summary>请求是否已经结束；成功、失败或基类 Abort 都会结束等待。</summary>
        public bool IsDone => IsCompleted;

        /// <summary>供派生类读取的单向结束标记；只能通过完成入口提交。</summary>
        protected bool IsCompleted => _isCompleted;

        /// <summary>由派生类报告的进度；通常按 0 到 1 表达，但本属性不限制取值范围。</summary>
        /// <remarks>终态固定为 1，之后的进度写入无效；不能仅凭进度判断请求是否成功。</remarks>
        public float Progress
        {
            get => _progress;
            protected set
            {
                if (!IsDone)
                {
                    _progress = value;
                }
            }
        }

        /// <summary>请求结束后的错误信息；未结束时固定返回“正在检测中...”。</summary>
        /// <remarks>完成入口原样保存传入值，包括 null 和空字符串；具体成功判定仍以派生请求的契约为准。</remarks>
        public string Error
        {
            get
            {
                if (!IsDone)
                {
                    return CheckingError;
                }

                return _error;
            }
        }

        /// <summary>供 Unity 的 yield return 检查是否继续等待；只等待结束，不自动启动请求或抛出其错误。</summary>
        public override bool keepWaiting => !IsDone;

        /// <summary>按需取得并缓存同一请求的 Task；不启动工作，不根据业务结果制造 Faulted/Canceled。</summary>
        /// <remarks>
        /// 由 owner 串行取得；可供多个消费者 await。完成通知及必要钩子收束后返回请求自身，
        /// 不受普通订阅移除、静默完成或通知异常影响，不自动取消生产者或切换线程。
        /// </remarks>
        public Task<T> AsTask()
        {
            _completionSource ??= new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_completionFinished)
            {
                _completionSource.TrySetResult(this as T);
            }
            return _completionSource.Task;
        }

        /// <summary>请求首次通过完成入口结束时的扩展钩子；常规完成在通知订阅后调用，静默完成直接调用。</summary>
        /// <remarks>前序订阅者抛异常仍会执行本钩子；它不替代派生请求对实际生产任务的清理。</remarks>
        protected virtual void OnCompleted()
        {
        }

        /// <summary>保存结果信息并结束请求；首次结束时同步通知订阅，再调用 OnCompleted。</summary>
        /// <param name="error">原样保存的错误信息；省略时写入 null。</param>
        /// <remarks>仅首次提交生效；之后完成或取消均不改写结果、进度和钩子次数。</remarks>
        protected void Complete(string error = null) => Complete(error, true, false);

        /// <summary>保存结果并静默结束，跳过完成订阅，但仍调用 OnCompleted。</summary>
        /// <param name="error">原样保存的错误信息；省略时写入 null。</param>
        /// <remarks>断开已保存订阅；之后注册的迟到订阅仍立即执行，重复完成无副作用。</remarks>
        protected void CompleteWithoutNotify(string error = null) => Complete(error, false, false);

        private void Complete(string error, bool notify, bool aborted)
        {
            if (IsCompleted)
            {
                return;
            }

            _error = error;
            _progress = 1;
            _isCompleted = true;
            var callbacks = _completed;
            _completed = null;
            List<Exception> failures = null;
            try
            {
                if (notify)
                {
                    callbacks?.Invoke(this as T);
                }
            }
            catch (Exception exception)
            {
                failures = new List<Exception> { exception };
            }
            finally
            {
                try
                {
                    OnCompleted();
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
                finally
                {
                    try
                    {
                        if (aborted)
                        {
                            try
                            {
                                OnAbort();
                            }
                            catch (Exception exception)
                            {
                                (failures ??= new List<Exception>()).Add(exception);
                            }
                        }
                    }
                    finally
                    {
                        // IsDone 已提交，但通知内的重入 AsTask 仍须等全部钩子收束。
                        _completionFinished = true;
                        _completionSource?.TrySetResult(this as T);
                    }
                }
            }

            if (failures?.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            if (failures != null)
            {
                throw new AggregateException(failures);
            }
        }

        /// <summary>注册完成回调；若请求已经结束，在本次注册调用中立即同步执行该回调。</summary>
        /// <param name="action">接收当前请求的回调，允许为 null；重复注册会分别保留并通知。</param>
        /// <remarks>完成时自动断开保存的订阅；迟到回调不再保存，异常直接传给注册者。</remarks>
        public void AddCompleteEvent(Action<T> action)
        {
            if (IsDone)
            {
                action?.Invoke(this as T);
            }
            else
            {
                _completed += action;
            }
        }

        /// <summary>移除一次匹配的完成订阅；同一委托重复注册时，一次调用只移除其中一次。</summary>
        public void RemoveCompleteEvent(Action<T> action)
        {
            _completed -= action;
        }

        /// <summary>清空 AddCompleteEvent 保存的全部完成订阅。</summary>
        public void RemoveAllCompleteEvent()
        {
            _completed = null;
        }

        /// <summary>以取消错误结束等待，先执行完成链，再调用 OnAbort。</summary>
        /// <remarks>
        /// 基类本身不会停止实际生产任务，派生类需实现取消逻辑。
        /// 仅首次取消生效；完成链抛异常仍执行一次 OnAbort，终态后调用无副作用。
        /// </remarks>
        public virtual void Abort() => Complete("request abort!", true, true);

        /// <summary>首次基类取消的钩子，完成通知后执行一次；不能假定此时生产任务已经停止。</summary>
        protected virtual void OnAbort()
        {
        }
    }
}
