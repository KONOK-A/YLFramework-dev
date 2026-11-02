using System;
using System.Threading;

namespace YLFramework.Actions
{
    /// <summary>将 BCL IObservable 适配为等待首值的动作。</summary>
    /// <typeparam name="T">事件源的值类型；动作只等待通知，不返回该值。</typeparam>
    /// <remarks>
    /// 首个有效 OnNext 标记正常完成；无值的 OnCompleted 或 OnError 会取消动作，错误同时写入 Unity 日志。
    /// 取消仅释放订阅，不触发正常完成，也不保证终止事件源背后的外部 Task；事件源本身不会被 Dispose。
    /// 应在 Unity 主线程启动；异线程通知投递到捕获的同步上下文，无上下文时由启动线程的 OnExecute 消费。
    /// </remarks>
    public class UniRxAction<T> : IAction, IActionExecutionVersion
    {
        private static readonly ActionPool<UniRxAction<T>> _pool = new ActionPool<UniRxAction<T>>(
            () => new UniRxAction<T>(), null, 10, action =>
            {
                action.ActionId = ActionKit.IdGenerator++;
                action.IsDeinitialized = false;
                action._hasCleanupFailed = false;
                try
                {
                    action.Reset();
                }
                catch
                {
                    action.IsDeinitialized = true;
                    action._observableGetter = null;
                    throw;
                }
            });
        private Func<IObservable<T>> _observableGetter;
        private Subscription _subscription;
        private bool _hasCleanupFailed;
        // 每次 Reset 递增；Execute 的重入保护用它识别"同一次推进期间对象是否已被重置或复用"。
        private ulong _executionVersion;
        ulong IActionExecutionVersion.ExecutionVersion => _executionVersion;

        /// <summary>仅供对象池创建首值等待动作，不在构造时取得事件源或订阅。</summary>
        private UniRxAction()
        {
        }

        /// <summary>分配动作，启动时才调用事件源工厂。</summary>
        /// <param name="observableGetter">非空的事件源工厂，每次启动时调用一次。</param>
        /// <returns>尚未订阅的池化动作；工厂引用保留到 Deinit。</returns>
        /// <exception cref="System.ArgumentNullException">observableGetter 为 null。</exception>
        public static UniRxAction<T> Allocate(Func<IObservable<T>> observableGetter)
        {
            if (observableGetter == null)
            {
                throw new ArgumentNullException(nameof(observableGetter));
            }

            var action = _pool.Allocate();
            action._observableGetter = observableGetter;
            return action;
        }

        /// <summary>执行器的暂停标记，不会暂停事件源或阻止外部通知。</summary>
        public bool IsPaused
        {
            get; set;
        }
        /// <summary>读取或设置动作已释放的标记。</summary>
        public bool IsDeinitialized
        {
            get; set;
        }
        /// <summary>读取或设置当前分配的动作标识，用于隔离迟到通知。</summary>
        public ulong ActionId
        {
            get; set;
        }
        /// <summary>读取或设置动作执行状态。</summary>
        public ActionStatus Status
        {
            get; set;
        }

        /// <summary>重置执行状态并释放旧订阅，下次启动会重新调用事件源工厂。</summary>
        public void Reset()
        {
            _executionVersion++;
            Status = ActionStatus.NotStart;
            IsPaused = false;
            ReleaseSubscription();
        }

        /// <summary>释放订阅并排队回收动作，不触发正常完成；订阅释放失败时延迟丢弃，不重试释放。</summary>
        public void Deinit()
        {
            if (IsDeinitialized)
            {
                return;
            }

            IsDeinitialized = true;
            _observableGetter = null;
            try
            {
                ReleaseSubscription();
            }
            finally
            {
                // Subscribe 返回前可能已取消；晚到句柄的释放结果要等当前栈退出后再判断。
                ActionQueue.AddCallback(new ActionQueueRecycleCallback<UniRxAction<T>>(_pool, this,
                    discardWhen: action => action._hasCleanupFailed));
            }
        }

        /// <summary>先摘除当前订阅槽再关闭它，释放失败时记录本次租约不可回池。</summary>
        private void ReleaseSubscription()
        {
            var subscription = _subscription;
            _subscription = null;
            try
            {
                subscription?.Close();
            }
            catch
            {
                // Finish/Reset 已摘掉订阅槽，但释放失败不能在随后 Deinit 时被误判为干净。
                if (subscription != null && ActionId == subscription.ActionId)
                {
                    _hasCleanupFailed = true;
                }

                throw;
            }
        }

        /// <summary>调用事件源工厂并订阅，通知可在 Subscribe 返回前同步到达。</summary>
        public void OnStart()
        {
            var subscription = new Subscription(this);
            _subscription = subscription;
            try
            {
                var observable = _observableGetter();
                if (!IsCurrent(subscription))
                {
                    return;
                }

                if (observable == null)
                {
                    throw new InvalidOperationException("The observable factory returned null.");
                }

                var disposable = observable.Subscribe(subscription);
                try
                {
                    subscription.Attach(disposable);
                }
                catch
                {
                    if (disposable != null && ActionId == subscription.ActionId)
                    {
                        _hasCleanupFailed = true;
                    }

                    throw;
                }
                subscription.Deliver();
            }
            catch (Exception original)
            {
                if (IsCurrent(subscription))
                {
                    try
                    {
                        Deinit();
                    }
                    catch (Exception cleanup)
                    {
                        throw new AggregateException(original, cleanup);
                    }
                }
                throw;
            }
        }

        /// <summary>在启动线程处理待投递的通知，不用时间增量推进事件源。</summary>
        public void OnExecute(float dt) => _subscription?.Deliver();
        /// <summary>正常完成时释放订阅，不释放事件源本身。</summary>
        public void OnFinish() => ReleaseSubscription();

        /// <summary>确认通知槽仍属于当前未清理的动作使用周期，拒绝旧槽的迟到信号。</summary>
        private bool IsCurrent(Subscription subscription) => !IsDeinitialized && ActionId == subscription.ActionId &&
            ReferenceEquals(_subscription, subscription);

        // 一次启动对应一个订阅槽：只采纳首个到达的信号（首值 / 空完成 / 错误），其余全部忽略；
        // 通知可能来自其他线程，经捕获的同步上下文或启动线程的 OnExecute 回到启动线程后才会作用于动作。
        private sealed class Subscription : IObserver<T>
        {
            private static readonly object _firstValue = new object();
            private static readonly object _emptyCompletion = new object();
            internal readonly ulong ActionId;
            private readonly int _threadId;
            private readonly SynchronizationContext _context;
            private UniRxAction<T> _target;
            private IDisposable _disposable;
            private object _signal;
            private int _closed;

            // 快照动作标识、启动线程和同步上下文，使通知回到本轮启动侧处理。
            internal Subscription(UniRxAction<T> target)
            {
                _target = target;
                ActionId = target.ActionId;
                _threadId = Thread.CurrentThread.ManagedThreadId;
                _context = SynchronizationContext.Current;
            }

            // 接收 Subscribe 返回的句柄；若订阅已关闭，立即释放这个晚到的句柄。
            internal void Attach(IDisposable disposable)
            {
                if (disposable == null)
                {
                    throw new InvalidOperationException("The observable returned a null subscription.");
                }
                // Subscribe 返回句柄前可能已同步通知或重入取消，晚到的句柄仍需释放。
                if (Volatile.Read(ref _closed) != 0)
                {
                    disposable.Dispose();
                }
                else
                {
                    _disposable = disposable;
                }
            }

            // 关闭通知入口并断开目标，至多尝试一次释放当前订阅句柄。
            internal void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                {
                    return;
                }

                _target = null;
                var disposable = _disposable;
                _disposable = null;
                Interlocked.Exchange(ref _signal, null);
                disposable?.Dispose();
            }

            /// <summary>提交首值信号，只等待通知到达，不保存或返回 value。</summary>
            public void OnNext(T value) => Publish(_firstValue);
            /// <summary>提交无值完成信号，若先于首值到达则按取消处理。</summary>
            public void OnCompleted() => Publish(_emptyCompletion);
            /// <summary>提交错误信号；空错误转换为参数异常，先到的错误按取消处理。</summary>
            public void OnError(Exception error) => Publish(error ?? new ArgumentNullException(nameof(error)));

            /// <summary>用首信号占位并请求投递，忽略后续信号或已关闭的订阅。</summary>
            private void Publish(object signal)
            {
                if (Volatile.Read(ref _closed) != 0 || Interlocked.CompareExchange(ref _signal, signal, null) != null)
                {
                    return;
                }

                if (Thread.CurrentThread.ManagedThreadId == _threadId)
                {
                    Deliver();
                }
                else if (_context != null)
                {
                    _context.Post(_ => Deliver(), null);
                }
                // 未捕获同步上下文时，由启动线程在 OnExecute 中消费通知。
            }

            // 在启动线程复核通知槽后应用首信号，空完成与错误只清理动作，不通知正常完成。
            internal void Deliver()
            {
                if (Thread.CurrentThread.ManagedThreadId != _threadId || Volatile.Read(ref _closed) != 0)
                {
                    return;
                }

                var target = _target;
                if (target == null || !target.IsCurrent(this))
                {
                    return;
                }

                var signal = Volatile.Read(ref _signal);
                if (signal == null)
                {
                    return;
                }
                // 首值才算正常完成；空完成和错误按取消处理。
                if (ReferenceEquals(signal, _firstValue))
                {
                    target.Finish();
                }
                else
                {
                    try
                    {
                        target.Deinit();
                    }
                    finally
                    {
                        if (signal is Exception exception)
                        {
                            UnityEngine.Debug.LogException(exception);
                        }
                    }
                }
            }
        }
    }

    /// <summary>BCL Observable 与 ActionKit 动作的桥接扩展。</summary>
    public static class ActionKitUniRxExtension
    {
        /// <summary>将事件源包装为启动时订阅、首值到达时标记完成的动作。</summary>
        /// <typeparam name="T">事件源的值类型。</typeparam>
        /// <param name="self">要订阅的非空事件源；每轮启动会重新订阅同一个 source。</param>
        /// <remarks>只释放订阅，不释放事件源；空完成和错误属于取消而非正常完成。</remarks>
        /// <exception cref="System.ArgumentNullException">self 为 null。</exception>
        public static IAction ToAction<T>(this IObservable<T> self)
        {
            if (self == null)
            {
                throw new ArgumentNullException(nameof(self));
            }

            return UniRxAction<T>.Allocate(() => self);
        }

        /// <summary>将等待首值的事件源工厂动作追加到序列，并返回原序列。</summary>
        /// <typeparam name="T">事件源的值类型。</typeparam>
        /// <param name="self">要追加子动作的非空组合。</param>
        /// <param name="observableGetter">每次启动时取得事件源的非空工厂。</param>
        /// <remarks>每次启动时调用工厂；空完成或错误会取消该步骤，不推进正常完成流程。</remarks>
        /// <exception cref="System.ArgumentNullException">self 或 observableGetter 为 null。</exception>
        public static ISequence UniRx<T>(this ISequence self, Func<IObservable<T>> observableGetter)
        {
            if (self == null)
            {
                throw new ArgumentNullException(nameof(self));
            }

            return self.Append(UniRxAction<T>.Allocate(observableGetter));
        }
    }
}
