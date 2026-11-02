using System;
using System.Threading;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>单个资源消费者的等待状态；终态与结果是否已领取、请求是否已释放分别记录。</summary>
    public enum ResourceRequestStatus
    {
        /// <summary>仍在等待结果。</summary>
        Pending,
        /// <summary>已成功产生结果，可由调用方单次领取。</summary>
        Succeeded,
        /// <summary>取得失败，原因保存在请求错误中。</summary>
        Failed,
        /// <summary>本消费者已取消等待，不代表共享生产已停止。</summary>
        Canceled,
        /// <summary>等待期间所属资源会话结束，不再交付结果。</summary>
        Invalidated
    }

    /// <summary>供资源会话批量冻结请求、释放未交付责任并通知观察者的内部协议。</summary>
    internal interface IResourceRequest : IDisposable
    {
        /// <summary>冻结待完成请求的失效终态，并暂存解绑及通知责任。</summary>
        void PrepareInvalidation();
        /// <summary>解除本请求的等待，归还未领取结果并结束请求持有责任。</summary>
        void ReleaseInvalidatedResult();
        /// <summary>通知冻结时捕获的完成观察者。</summary>
        void NotifyInvalidation();
    }

    /// <summary>独立消费者。yield 只等待终态；TakeResult 恰一次转交结果，Dispose 只释放未领取结果。</summary>
    /// <remarks>所有操作在接受请求的 Unity 主线程执行；完成观察异常逐个报告，不改终态。</remarks>
    /// <typeparam name="T">成功时交付的结果类型；其释放规则由创建请求的入口指定。</typeparam>
    public class ResourceRequest<T> : CustomYieldInstruction, IDisposable, IResourceRequest
    {
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly Action<T> _releaseResult;
        private T _result;
        private Action _cancel;
        private Func<float> _progressReader;
        private Action<ResourceRequest<T>> _observers;
        private Action<ResourceRequest<T>> _invalidationObservers;
        private Action _invalidationDetach;
        private bool _isClaimed;
        private float _progress;
        internal Action OwnershipEnded;
        internal Action Terminal;

        /// <summary>当前等待状态；释放成功请求不会将成功终态改成取消。</summary>
        public ResourceRequestStatus Status
        {
            get; private set;
        }
        /// <summary>失败、取消或会话失效的原因；等待中及成功时为 null。</summary>
        public ResourceException Error
        {
            get; private set;
        }
        /// <summary>是否已进入终态；为 true 不代表结果已经被领取。</summary>
        public bool IsDone => Status != ResourceRequestStatus.Pending;
        /// <summary>请求是否已释放或被会话关闭；释放后不能再领取结果。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>供协程等待终态，不隐式领取或转移结果。</summary>
        public override bool keepWaiting => !IsDone;
        /// <summary>在途借读 producer 进度；成功为 1，其他终态冻结最后样本，不能代替 IsDone 判断。</summary>
        public float Progress
        {
            get
            {
                CheckThread();
                var value = _progressReader == null ? _progress : _progressReader();
                return float.IsNaN(value) ? 0 : Mathf.Clamp01(value);
            }
        }

        /// <summary>建立独立请求，并指定迟到或未领取结果的归还动作。</summary>
        internal ResourceRequest(Action<T> releaseResult = null) => this._releaseResult = releaseResult;

        /// <summary>终态后订阅即时回放。通知栈中的退订/Dispose 不跳过已捕获的其他观察者。</summary>
        public event Action<ResourceRequest<T>> Completed
        {
            add
            {
                CheckThread();
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(ResourceRequest<T>));
                }

                if (value == null)
                {
                    return;
                }

                if (IsDone)
                {
                    foreach (Action<ResourceRequest<T>> observer in value.GetInvocationList())
                    {
                        Invoke(observer);
                    }
                }
                else
                {
                    _observers += value;
                }
            }
            remove
            {
                CheckThread();
                _observers -= value;
            }
        }

        /// <summary>绑定本消费者退出等待时的解绑动作及在途进度读取器。</summary>
        internal void Attach(Action onCancel, Func<float> getProgress)
        {
            _cancel = onCancel;
            _progressReader = getProgress;
        }

        /// <summary>暂存成功结果；请求已结束时立即归还迟到结果，不覆盖原终态。</summary>
        internal bool SetSuccess(T value)
        {
            if (IsDone || IsDisposed)
            {
                _releaseResult?.Invoke(value);
                return false;
            }
            _result = value;
            SetTerminal(ResourceRequestStatus.Succeeded, null);
            return true;
        }

        /// <summary>在尚未结束时登记错误终态，通知由调用方另行触发。</summary>
        internal void SetFailure(ResourceRequestStatus status, ResourceException error)
        {
            if (IsDone)
            {
                return;
            }

            SetTerminal(status, error);
        }

        /// <summary>冻结进度和终态，解除在途绑定并结算终态跟踪。</summary>
        private void SetTerminal(ResourceRequestStatus status, ResourceException error)
        {
            _progress = status == ResourceRequestStatus.Succeeded ? 1 : Progress;
            Status = status;
            Error = error;
            _cancel = null;
            _progressReader = null;
            var terminal = Terminal;
            Terminal = null;
            terminal?.Invoke();
        }

        /// <summary>移走当前观察者快照后逐个通知，避免重入重复派发。</summary>
        internal void Notify()
        {
            var snapshot = _observers;
            _observers = null;
            NotifyObservers(snapshot);
        }

        /// <summary>逐个调用已捕获的观察者，不受通知期间退订影响。</summary>
        private void NotifyObservers(Action<ResourceRequest<T>> snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            foreach (Action<ResourceRequest<T>> observer in snapshot.GetInvocationList())
            {
                Invoke(observer);
            }
        }

        /// <summary>执行单个完成观察者并记录其异常，继续通知其他观察者。</summary>
        private void Invoke(Action<ResourceRequest<T>> observer)
        {
            try
            {
                observer(this);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        /// <summary>成功结果单次转移。失败/取消抛原资源错误；Dispose 后始终抛 ObjectDisposedException。</summary>
        /// <returns>由调用方接管的结果；后续释放本请求不再归还它。</returns>
        /// <exception cref="ObjectDisposedException">请求已经释放或被会话关闭。</exception>
        /// <exception cref="InvalidOperationException">线程不符、请求尚未成功或结果已经领取。</exception>
        /// <exception cref="ResourceException">请求以错误终态结束。</exception>
        public T TakeResult()
        {
            CheckThread();
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(ResourceRequest<T>));
            }

            if (Status != ResourceRequestStatus.Succeeded)
            {
                if (Error != null)
                {
                    throw Error;
                }

                throw new InvalidOperationException("The resource request has not succeeded.");
            }
            if (_isClaimed)
            {
                throw new InvalidOperationException("The resource result was already taken.");
            }

            _isClaimed = true;
            var value = _result;
            _result = default;
            EndOwnership();
            return value;
        }

        /// <summary>只退出自己的等待；已完成时无副作用，存在其他消费者时不撤销共享生产。</summary>
        public void Cancel()
        {
            CheckThread();
            if (IsDone)
            {
                return;
            }

            var detach = _cancel;
            SetTerminal(ResourceRequestStatus.Canceled, new ResourceException(ResourceError.Canceled, "The resource consumer canceled its wait."));
            detach?.Invoke();
            Notify();
        }

        /// <summary>先冻结本请求，防止退出回调重入改写同批请求的结束原因。</summary>
        void IResourceRequest.PrepareInvalidation()
        {
            if (IsDisposed)
            {
                return;
            }

            if (!IsDone)
            {
                _invalidationDetach = _cancel;
                SetTerminal(ResourceRequestStatus.Invalidated, new ResourceException(ResourceError.SessionUnavailable, "The resource session ended."));
            }
            IsDisposed = true;
            _invalidationObservers = _observers;
            _observers = null;
        }

        /// <summary>履行会话冻结时暂存的解绑和结果归还责任。</summary>
        void IResourceRequest.ReleaseInvalidatedResult()
        {
            try
            {
                var detach = _invalidationDetach;
                _invalidationDetach = null;
                detach?.Invoke();
                ReleaseUnclaimedResult();
            }
            finally
            {
                EndOwnership();
            }
        }

        /// <summary>派发会话冻结时捕获的观察者，不重新读取订阅列表。</summary>
        void IResourceRequest.NotifyInvalidation()
        {
            var snapshot = _invalidationObservers;
            _invalidationObservers = null;
            NotifyObservers(snapshot);
        }

        /// <summary>关闭本请求；在途时取消自己的等待，成功时只归还尚未领取的结果。</summary>
        /// <remarks>先标记关闭再清理，防止重入重复释放；结果归还动作的异常会向外传播。</remarks>
        /// <exception cref="InvalidOperationException">未在接受请求的 Unity 主线程调用。</exception>
        public void Dispose()
        {
            CheckThread();
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            try
            {
                if (!IsDone)
                {
                    Cancel();
                }
                else
                {
                    ReleaseUnclaimedResult();
                }
            }
            finally
            {
                _observers = null;
                EndOwnership();
            }
        }

        /// <summary>先移走未领取结果，再执行其归还动作，确保同一责任只归还一次。</summary>
        private void ReleaseUnclaimedResult()
        {
            if (Status != ResourceRequestStatus.Succeeded || _isClaimed)
            {
                return;
            }

            var value = _result;
            _result = default;
            _isClaimed = true;
            _releaseResult?.Invoke(value);
        }

        /// <summary>单次通知资源系统，本请求不再保有可交付结果。</summary>
        private void EndOwnership()
        {
            var ended = OwnershipEnded;
            OwnershipEnded = null;
            ended?.Invoke();
        }

        /// <summary>拒绝跨越请求创建线程的操作。</summary>
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("Resource requests must be used on their accepting Unity thread.");
            }
        }
    }
}
