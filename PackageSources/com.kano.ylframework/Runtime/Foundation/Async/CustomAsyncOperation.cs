using System;
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
        // 常规完成链：Complete(error) 写结果 -> 无参 Complete 收束进度并去重 -> IsCompleted 的 setter 发通知。

        /// <summary>旧版完成事件；新代码使用 AddCompleteEvent，以便在请求已经结束时也能收到通知。</summary>
        /// <remarks>常规完成时先调用此事件，再调用 AddCompleteEvent 的订阅；结束后直接订阅此事件不会补发。</remarks>
        [Obsolete("已过时,请使用AddCompleteEvent代替!")]
        public event Action<T> Completed = null;

        private Action<T> _completed;
        private bool _isCompleted = false;
        private string _error = string.Empty;
        private const string CheckingError = "正在检测中...";

        /// <summary>请求是否已经结束；成功、失败或基类 Abort 都会结束等待。</summary>
        public bool IsDone => IsCompleted;

        /// <summary>供派生类访问的结束标记，赋为 true 时同步发出完成通知。</summary>
        /// <remarks>
        /// 每次直接赋为 true 都会依次调用旧事件、完成订阅和 OnCompleted，不会自行去重。
        /// 通常应调用 Complete，由该入口避免重复完成通知；回调抛异常时，后续回调和钩子不会继续执行。
        /// </remarks>
        protected bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                // 先提交结束状态，回调里再次 Complete 才会命中重复检查，不会递归重发通知。
                _isCompleted = value;
                if (_isCompleted)
                {
                    // 按自类型约定，将当前请求作为 T 回传给订阅者。
                    Completed?.Invoke(this as T);
                    _completed?.Invoke(this as T);
                    OnCompleted();
                }
            }
        }

        /// <summary>由派生类报告的进度；通常按 0 到 1 表达，但本属性不限制取值范围。</summary>
        /// <remarks>通过本类的完成入口结束请求时会设为 1；不能仅凭进度判断请求是否成功。</remarks>
        public float Progress
        {
            get; protected set;
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
            private set => _error = value;
        }

        /// <summary>供 Unity 的 yield return 检查是否继续等待；只等待结束，不自动启动请求或抛出其错误。</summary>
        public override bool keepWaiting => !IsDone;

        /// <summary>请求首次通过完成入口结束时的扩展钩子；常规完成在通知订阅后调用，静默完成直接调用。</summary>
        /// <remarks>前序订阅者抛异常会阻止此钩子执行，因此不能把它当作一定执行的 finally。</remarks>
        protected virtual void OnCompleted()
        {
        }

        /// <summary>保存结果信息并结束请求；首次结束时同步通知订阅，再调用 OnCompleted。</summary>
        /// <param name="error">原样保存的错误信息；省略时写入 null。</param>
        /// <remarks>重复调用仍会更新 Error 并将进度设为 1，但不重发通知或再次执行完成钩子。</remarks>
        protected void Complete(string error = null)
        {
            // 通知同步执行，先存结果才能让回调读到本次错误，而不是旧值。
            Error = error;
            Complete();
        }

        /// <summary>将进度收束到 1，并仅在尚未结束时进入同步通知链。</summary>
        private void Complete()
        {
            Progress = 1;
            if (IsCompleted)
            {
                return;
            }

            IsCompleted = true;
        }

        /// <summary>保存结果并静默结束，跳过两类完成订阅，但仍调用 OnCompleted。</summary>
        /// <param name="error">原样保存的错误信息；省略时写入 null。</param>
        /// <remarks>不会清空订阅；之后通过 AddCompleteEvent 注册的迟到订阅仍会立即执行。重复完成只更新结果和进度。</remarks>
        protected void CompleteWithoutNotify(string error = null)
        {
            Error = error;
            CompleteWithoutNotify();
        }

        /// <summary>直接设置结束字段，绕过属性中的事件通知，再执行一次完成钩子。</summary>
        private void CompleteWithoutNotify()
        {
            Progress = 1;
            if (IsCompleted)
            {
                return;
            }

            // 直接写字段而非属性，跳过订阅通知，但仍执行派生类的完成钩子。
            _isCompleted = true;
            OnCompleted();
        }

        /// <summary>注册完成回调；若请求已经结束，在本次注册调用中立即同步执行该回调。</summary>
        /// <param name="action">接收当前请求的回调，允许为 null；重复注册会分别保留并通知。</param>
        /// <remarks>回调不会自动解绑，且立即执行时的异常会直接传给注册者；按需保存委托并在所属生命周期结束时移除。</remarks>
        public void AddCompleteEvent(Action<T> action)
        {
            _completed += action;
            // 只补发本次 action，不调用整条 _completed，以免给先前订阅者重发。
            if (IsDone)
            {
                action?.Invoke(this as T);
            }
        }

        /// <summary>移除一次匹配的完成订阅；同一委托重复注册时，一次调用只移除其中一次。</summary>
        public void RemoveCompleteEvent(Action<T> action)
        {
            _completed -= action;
        }

        /// <summary>清空 AddCompleteEvent 保存的订阅，不清空旧版 Completed 事件。</summary>
        public void RemoveAllCompleteEvent()
        {
            _completed = null;
        }

        /// <summary>以取消错误结束等待，先执行完成链，再调用 OnAbort。</summary>
        /// <remarks>
        /// 基类本身不会停止实际生产任务，派生类需实现取消逻辑。
        /// 即使请求已结束，也会改写 Error 并再次调用 OnAbort；完成链抛异常时则不会进入 OnAbort。
        /// </remarks>
        public virtual void Abort()
        {
            Complete("request abort!");
            OnAbort();
        }

        /// <summary>供派生类取消实际工作的钩子；应考虑重复 Abort，不能假定此时生产任务已经停止。</summary>
        protected virtual void OnAbort()
        {
        }
    }
}
