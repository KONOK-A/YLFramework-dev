using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using YLFramework.Pooling;
using YLFramework.Timing.Internal;

namespace YLFramework.Timing
{
    /// <summary>
    /// Foundation 的秒/帧到期内核，仅主线程使用；显式实例不读取 Unity Time、不创建宿主。
    /// 三域共享 run 状态和派发算法。登记不执行到期回调，fixed-delay、不补发，每个 rendered frame 至多执行一次。
    /// </summary>
    /// <remarks>默认实例由内部 TimerDriver 驱动，禁止调用其手动推进入口。</remarks>
    public sealed class TimerScheduler : IDisposable
    {
        private readonly TimerHeap _scaled = new(TimerClock.Scaled);
        private readonly TimerHeap _unscaled = new(TimerClock.Unscaled);
        private readonly TimerHeap _frames = new(TimerClock.RenderedFrame);
        private readonly HashSet<TimerRun> _owned = new();
        private readonly List<TimerRun> _pending = new();
        private readonly Dictionary<TimerLifetimeListener, int> _bindingOwners = new();
        private double _scaledTime;
        private double _unscaledTime;
        private long _renderedFrame;
        private ulong _order;
        private bool _isAdvancing;
        private bool _isDisposed;
        private readonly bool _isAutomatic;
        private Action _synchronize;
        private bool _isFrameReady = true;
        private bool _isSecondsReady = true;
        private long _lastDispatchedFrame;
        /// <summary>调度器是否已永久关闭；关闭后不再接收任务或样本推进。</summary>
        internal bool IsDisposed => _isDisposed;

        /// <summary>创建由宿主采样的自动内核，首个合法样本到达前延后锚定任务期限。</summary>
        internal TimerScheduler(Action synchronize) : this(0, 0, 0)
        {
            _isAutomatic = true;
            _synchronize = synchronize;
            _isFrameReady = _isSecondsReady = false;
        }

        /// <summary>请求自动宿主同步当前合法样本；手动内核不执行额外采样。</summary>
        internal void Refresh() => _synchronize?.Invoke();

        /// <summary>接收自动宿主的帧身份和样本就绪状态，秒样本不可用时保留先前双时钟值。</summary>
        internal void Sample(double scaledTime, double unscaledTime, long frame, bool secondsReady)
        {
            _renderedFrame = frame;
            _isFrameReady = true;
            _isSecondsReady = secondsReady;
            if (!secondsReady)
            {
                return;
            }

            _scaledTime = scaledTime;
            _unscaledTime = unscaledTime;
        }

        /// <summary>使用宿主最近提交的样本推进自动内核，执行权限由宿主在调用前核对。</summary>
        internal void AdvanceAutomatic() => AdvanceCore(_scaledTime, _unscaledTime, _renderedFrame);

        /// <summary>为命名替换预先检查期限是否可表示；取消通知可能改变样本，正式登记时仍需重新校验。</summary>
        internal void ValidateDeadline(TimerClock clock, double delay, long frames)
        {
            Refresh();
            ThrowIfDisposed();
            if (_renderedFrame == long.MaxValue)
            {
                throw new InvalidOperationException("Rendered frame clock is exhausted.");
            }

            if (clock == TimerClock.RenderedFrame)
            {
                _ = checked(_renderedFrame + frames);
            }
            else
            {
                AddSeconds(ReadClock(clock), delay);
            }
        }

        /// <summary>以明确的初始双时钟和 rendered frame 样本创建手动实例；输入须有限且非负。</summary>
        public TimerScheduler(double scaledTime, double unscaledTime, long renderedFrame)
        {
            ValidateSample(scaledTime, unscaledTime, renderedFrame);
            _scaledTime = scaledTime;
            _unscaledTime = unscaledTime;
            _renderedFrame = renderedFrame;
            _lastDispatchedFrame = renderedFrame;
        }

        /// <summary>登记一次秒等待；默认 Unscaled，零 delay 最早在下一 rendered frame 执行，callback 可为 null。</summary>
        public TimerHandle Delay(double delay, Action callback = null, TimerClock clock = TimerClock.Unscaled,
            Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateSeconds(delay, nameof(delay));
            ValidateSecondClock(clock);
            return Schedule(clock, delay, 0, 0, 0, 1, callback, null, onCancelled);
        }

        /// <summary>
        /// 首次只等待 firstDelay，后续等待 interval（须大于零），下一 deadline 为实际派发样本 + interval。
        /// count 为含首轮的总次数且须大于零；null 表示无限。callback 参数为从 1 开始的轮数，可为 null。
        /// </summary>
        public TimerHandle Repeat(double firstDelay, double interval, Action<long> callback = null, int? count = null,
            TimerClock clock = TimerClock.Unscaled, Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateSeconds(firstDelay, nameof(firstDelay));
            ValidateSeconds(interval, nameof(interval));
            if (interval == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }

            ValidateCount(count);
            ValidateSecondClock(clock);
            return Schedule(clock, firstDelay, 0, interval, 0, count, null, callback, onCancelled);
        }

        /// <summary>等待登记帧之后的 frameDelay 个 rendered frame；零等同下一个帧，不能用于 FixedUpdate 计数。</summary>
        public TimerHandle DelayFrames(long frameDelay, Action callback = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            if (frameDelay < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameDelay));
            }

            return Schedule(TimerClock.RenderedFrame, 0, Math.Max(1, frameDelay), 0, 0, 1, callback, null, onCancelled);
        }

        /// <summary>帧周期任务；首次等待 max(1, firstDelay) 帧，后续 interval 须大于零；count 含首轮，null 为无限。</summary>
        public TimerHandle RepeatFrames(long firstDelay, long interval, Action<long> callback = null, int? count = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            if (firstDelay < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(firstDelay));
            }

            if (interval <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }

            ValidateCount(count);
            return Schedule(TimerClock.RenderedFrame, 0, Math.Max(1, firstDelay), 0, interval, count, null, callback, onCancelled);
        }

        /// <summary>为已校验的入口分配或复用任务存储，更新代次并加入延后队列，不内联执行到期回调。</summary>
        internal TimerHandle Schedule(TimerClock clock, double delay, long frames, double intervalSeconds,
            long intervalFrames, int? count, Action callback, Action<long> tickCallback,
            Action<TimerCancellationReason> onCancelled, TimerRun storage = null)
        {
            Refresh();
            ThrowIfDisposed();
            if (_renderedFrame == long.MaxValue)
            {
                throw new InvalidOperationException("Rendered frame clock is exhausted.");
            }

            var dueSeconds = clock == TimerClock.RenderedFrame ? 0 : AddSeconds(ReadClock(clock), delay);
            var dueFrame = clock == TimerClock.RenderedFrame ? checked(_renderedFrame + frames) : 0;
            var run = storage ?? new TimerRun();
            if (run.Owner != null)
            {
                throw new InvalidOperationException("Timer run is still active.");
            }

            run.Generation = checked(run.Generation + 1);
            run.Owner = this;
            run.State = TimerState.Scheduled;
            run.Clock = clock;
            run.Callback = callback;
            run.TickCallback = tickCallback;
            run.OnCancelled = onCancelled;
            run.RemainingCount = count;
            run.IntervalSeconds = intervalSeconds;
            run.IntervalFrames = intervalFrames;
            run.DueSeconds = dueSeconds;
            run.DueFrame = dueFrame;
            run.ExecutionCount = 0;
            run.ExecutingGeneration = 0;
            run.PauseNext = false;
            run.FrozenSeconds = delay;
            run.FrozenFrames = frames;
            run.NeedsAnchor = clock == TimerClock.RenderedFrame ? !_isFrameReady : !_isSecondsReady;
            Queue(run);
            _owned.Add(run);
            return new TimerHandle(run);
        }

        /// <summary>
        /// 提交有限、非回拨的 rendered 样本并处理到期任务。相同帧仅刷新样本，不派发；递归调用为空操作。
        /// 回调中新建/恢复及下一周期均留至后续帧。各域依次 Unscaled、Scaled、Frame，无跨域物理时间全序。
        /// 一个失败不阻断其他就绪任务；必要收尾后传播原始异常，多错以 AggregateException 汇总。
        /// </summary>
        public void Advance(double scaledTime, double unscaledTime, long renderedFrame)
        {
            if (_isAutomatic)
            {
                throw new InvalidOperationException("Default timers are driven by TimerDriver.");
            }

            AdvanceCore(scaledTime, unscaledTime, renderedFrame);
        }

        /// <summary>防止递归推进，接收非回拨样本，并在新渲染帧依次检查绑定、转移待登记任务和派发三个域。</summary>
        private void AdvanceCore(double scaledTime, double unscaledTime, long renderedFrame)
        {
            if (_isAdvancing)
            {
                return;
            }

            ThrowIfDisposed();
            ValidateSample(scaledTime, unscaledTime, renderedFrame);
            if (scaledTime < _scaledTime)
            {
                throw new ArgumentOutOfRangeException(nameof(scaledTime));
            }

            if (unscaledTime < _unscaledTime)
            {
                throw new ArgumentOutOfRangeException(nameof(unscaledTime));
            }

            if (renderedFrame < _renderedFrame)
            {
                throw new ArgumentOutOfRangeException(nameof(renderedFrame));
            }

            var newFrame = renderedFrame > _lastDispatchedFrame;
            _scaledTime = scaledTime;
            _unscaledTime = unscaledTime;
            _renderedFrame = renderedFrame;
            if (!newFrame)
            {
                return;
            }

            _lastDispatchedFrame = renderedFrame;

            List<Exception> errors = null;
            _isAdvancing = true;
            try
            {
                CheckBindings(ref errors);
                // 只转移此前的登记责任；callback 产生的 pending 不参与本轮任何时钟域。
                for (var i = 0; i < _pending.Count;)
                {
                    var run = _pending[i];
                    if (run.NeedsAnchor)
                    {
                        try
                        {
                            SetNextDeadline(run, run.FrozenSeconds, run.FrozenFrames);
                            run.NeedsAnchor = false;
                            run.EarliestFrame = checked(_renderedFrame + 1);
                        }
                        catch (Exception error)
                        {
                            Finish(run, TimerState.Faulted);
                            AddError(ref errors, error);
                            continue;
                        }
                    }
                    if (run.EarliestFrame > _renderedFrame)
                    {
                        i++;
                        continue;
                    }
                    RemoveQueued(run);
                    Heap(run.Clock).Insert(run);
                }
                Dispatch(_unscaled, ref errors);
                Dispatch(_scaled, ref errors);
                Dispatch(_frames, ref errors);
            }
            finally
            {
                _isAdvancing = false;
            }
            ThrowErrors(errors);
        }

        /// <summary>逐项派发当前域的到期任务，收集失败，并为仍有调度责任的周期任务安排下一轮。</summary>
        private void Dispatch(TimerHeap heap, ref List<Exception> errors)
        {
            while (!_isDisposed && heap.Count > 0)
            {
                var run = heap.Top;
                if (run.Clock == TimerClock.RenderedFrame ? _renderedFrame < run.DueFrame : ReadClock(run.Clock) < run.DueSeconds)
                {
                    break;
                }

                heap.Pop();
                var generation = run.Generation;
                var callback = run.Callback;
                var tickCallback = run.TickCallback;
                run.ExecutingGeneration = generation;
                run.State = TimerState.Executing;
                var last = run.RemainingCount == 1;
                if (run.RemainingCount.HasValue)
                {
                    run.RemainingCount--;
                }

                if (last)
                {
                    Detach(run); // 末轮完成责任已预留，Cancel/Dispose 不再接管它。
                }

                Exception failure = null;
                try
                {
                    run.ExecutionCount = checked(run.ExecutionCount + 1);
                    callback?.Invoke();
                    tickCallback?.Invoke(run.ExecutionCount);
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    // 旧执行栈只收尾所捕获的 run/代次，不能写入新启动的存储。
                    if (run.Generation == generation)
                    {
                        run.ExecutingGeneration = 0;
                        if (last)
                        {
                            Finish(run, failure == null ? TimerState.Completed : TimerState.Faulted);
                        }
                        else if (ReferenceEquals(run.Owner, this))
                        {
                            if (failure != null)
                            {
                                Finish(run, TimerState.Faulted);
                            }
                            else if (run.PauseNext)
                            {
                                run.PauseNext = false;
                                run.State = TimerState.Paused;
                            }
                            else
                            {
                                try
                                {
                                    SetNextDeadline(run, run.IntervalSeconds, run.IntervalFrames);
                                    run.State = TimerState.Scheduled;
                                    Queue(run);
                                }
                                catch (Exception error)
                                {
                                    failure = error;
                                    Finish(run, TimerState.Faulted);
                                }
                            }
                        }
                    }
                }
                if (failure != null)
                {
                    AddError(ref errors, failure);
                }
            }
        }

        /// <summary>取消仍归属本内核的精确代次，先提交终态及解除引用，再按手动或自动边界通知取消。</summary>
        internal bool Cancel(TimerRun run, ulong generation)
        {
            Refresh();
            if (!Owns(run, generation))
            {
                return false;
            }

            var notify = run.OnCancelled;
            Finish(run, TimerState.Cancelled);
            try
            {
                notify?.Invoke(TimerCancellationReason.Cancelled);
            }
            catch (Exception error)
            {
                if (!_isAutomatic)
                {
                    throw;
                }

                UnityEngine.Debug.LogException(error);
            }
            return true;
        }

        /// <summary>冻结当前等待量；非末轮回调内只请求下一轮暂停，保留 Executing 状态直至回调返回。</summary>
        internal bool Pause(TimerRun run, ulong generation)
        {
            Refresh();
            if (!Owns(run, generation))
            {
                return false;
            }

            if (run.State == TimerState.Paused)
            {
                return true;
            }

            if (run.State == TimerState.Executing)
            {
                run.PauseNext = true;
                run.FrozenSeconds = run.IntervalSeconds;
                run.FrozenFrames = run.IntervalFrames;
                return true;
            }
            else
            {
                run.FrozenSeconds = RemainingSeconds(run);
                run.FrozenFrames = RemainingFrames(run);
                RemoveQueued(run);
            }
            run.State = TimerState.Paused;
            return true;
        }

        /// <summary>恢复精确代次的冻结等待，或在回调尚未退出时撤销暂停请求。</summary>
        internal bool Resume(TimerRun run, ulong generation)
        {
            Refresh();
            if (!Owns(run, generation))
            {
                return false;
            }
            // 回调仍在栈上时，仅撤销暂停；旧租约退出后统一续调度，避免双入队。
            if (run.State == TimerState.Executing && run.ExecutingGeneration == generation && run.PauseNext)
            {
                run.PauseNext = false;
                return true;
            }
            if (run.State != TimerState.Paused)
            {
                return false;
            }

            run.NeedsAnchor = run.Clock == TimerClock.RenderedFrame ? !_isFrameReady : !_isSecondsReady;
            if (!run.NeedsAnchor)
            {
                SetNextDeadline(run, run.FrozenSeconds, run.FrozenFrames);
            }

            Queue(run);
            run.State = TimerState.Scheduled;
            return true;
        }

        /// <summary>读取秒任务的当前或冻结剩余量；未锚定任务保留原等待量，其他状态返回零。</summary>
        internal double RemainingSeconds(TimerRun run) => run.Clock == TimerClock.RenderedFrame ? 0 :
            run.State == TimerState.Paused ? run.FrozenSeconds :
            run.State == TimerState.Scheduled ? run.NeedsAnchor ? run.FrozenSeconds : Math.Max(0, run.DueSeconds - ReadClock(run.Clock)) : 0;
        /// <summary>读取帧任务的当前或冻结剩余量，不将秒域或执行中任务换算为帧数。</summary>
        internal long RemainingFrames(TimerRun run) => run.Clock != TimerClock.RenderedFrame ? 0 :
            run.State == TimerState.Paused ? run.FrozenFrames :
            run.State == TimerState.Scheduled ? run.NeedsAnchor ? run.FrozenFrames : Math.Max(0, run.DueFrame - _renderedFrame) : 0;

        /// <summary>同时核对调度归属和代次，拒绝终态任务及复用前的旧控制请求。</summary>
        private bool Owns(TimerRun run, ulong generation) => ReferenceEquals(run.Owner, this) && run.Generation == generation;
        /// <summary>取得指定秒域最近保存的样本，调用方负责排除帧域。</summary>
        private double ReadClock(TimerClock clock) => clock == TimerClock.Scaled ? _scaledTime : _unscaledTime;
        /// <summary>取得指定计时域独立维护的到期堆。</summary>
        private TimerHeap Heap(TimerClock clock) => clock == TimerClock.RenderedFrame ? _frames :
            clock == TimerClock.Scaled ? _scaled : _unscaled;
        /// <summary>以当前样本计算新的秒或帧期限，不以旧期限补发遗漏周期，并检查范围溢出。</summary>
        private void SetNextDeadline(TimerRun run, double seconds, long frames)
        {
            if (run.Clock == TimerClock.RenderedFrame)
            {
                run.DueFrame = checked(_renderedFrame + frames);
            }
            else
            {
                run.DueSeconds = AddSeconds(ReadClock(run.Clock), seconds);
            }
        }

        /// <summary>分配新的稳定顺序并加入待登记列表，确保本轮新建、恢复或续期任务最早下一帧执行。</summary>
        private void Queue(TimerRun run)
        {
            run.EarliestFrame = checked(_renderedFrame + 1);
            run.Order = checked(++_order);
            run.PendingIndex = _pending.Count;
            _pending.Add(run);
        }

        /// <summary>解除任务的堆或待登记位置，待登记列表以尾项补位并维护反向索引。</summary>
        private void RemoveQueued(TimerRun run)
        {
            run.Heap?.Remove(run);
            if (run.PendingIndex < 0)
            {
                return;
            }

            var index = run.PendingIndex;
            var last = _pending[_pending.Count - 1];
            _pending.RemoveAt(_pending.Count - 1);
            if (index < _pending.Count)
            {
                _pending[index] = last;
                last.PendingIndex = index;
            }
            run.PendingIndex = -1;
        }

        /// <summary>解除队列、调度器、范围与对象绑定责任，并清空用户委托；不在此处改写任务状态。</summary>
        private void Detach(TimerRun run)
        {
            RemoveQueued(run);
            _owned.Remove(run);
            run.Scope?.Detach(run);
            run.Binding?.Detach();
            run.Owner = null;
            run.Callback = null;
            run.TickCallback = null;
            run.OnCancelled = null;
            run.PauseNext = false;
        }

        /// <summary>增加监听器在本内核中的绑定计数，确保暂停任务仍参与对象存活检查。</summary>
        internal void AddBinding(TimerLifetimeListener listener)
        {
            _bindingOwners.TryGetValue(listener, out var count);
            _bindingOwners[listener] = count + 1;
        }
        /// <summary>减少监听器的绑定计数，最后一项解除后停止由本内核检查该监听器。</summary>
        internal void RemoveBinding(TimerLifetimeListener listener)
        {
            if (!_bindingOwners.TryGetValue(listener, out var count))
            {
                return;
            }

            if (count == 1)
            {
                _bindingOwners.Remove(listener);
            }
            else
            {
                _bindingOwners[listener] = count - 1;
            }
        }

        /// <summary>快照检查本内核持有的对象监听器，容许取消过程改动绑定集合，并收集通知异常。</summary>
        private void CheckBindings(ref List<Exception> errors)
        {
            if (_bindingOwners.Count == 0)
            {
                return;
            }

            var snapshot = ListPool<TimerLifetimeListener>.Allocate();
            try
            {
                snapshot.AddRange(_bindingOwners.Keys);
                foreach (var listener in snapshot)
                {
                    try
                    {
                        listener.CheckOwner();
                    }
                    catch (Exception error)
                    {
                        AddError(ref errors, error);
                    }
                }
            }
            finally
            {
                ListPool<TimerLifetimeListener>.Recycle(snapshot);
            }
        }

        /// <summary>先提交指定终态，再解除该任务的全部调度及订阅责任。</summary>
        private void Finish(TimerRun run, TimerState state)
        {
            run.State = state;
            Detach(run);
        }

        /// <summary>永久关闭实例，先提交全部待调度/暂停/非末轮执行任务失效，再逐项通知并汇总异常。</summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _synchronize = null;
            var snapshot = ListPool<TimerRun>.Allocate();
            var notifications = ListPool<Action<TimerCancellationReason>>.Allocate();
            List<Exception> errors = null;
            try
            {
                snapshot.AddRange(_owned);
                foreach (var run in snapshot)
                {
                    if (!ReferenceEquals(run.Owner, this))
                    {
                        continue;
                    }

                    if (run.OnCancelled != null)
                    {
                        notifications.Add(run.OnCancelled);
                    }

                    Finish(run, TimerState.Invalidated);
                }
                foreach (var notify in notifications)
                {
                    try
                    {
                        notify?.Invoke(TimerCancellationReason.SchedulerDisposed);
                    }
                    catch (Exception error)
                    {
                        AddError(ref errors, error);
                    }
                }
            }
            finally
            {
                ListPool<TimerRun>.Recycle(snapshot);
                ListPool<Action<TimerCancellationReason>>.Recycle(notifications);
            }
            ThrowErrors(errors);
        }

        /// <summary>拒绝在永久关闭的内核上继续登记或推进工作。</summary>
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(TimerScheduler));
            }
        }
        /// <summary>按调用方提供的参数名拒绝负数及非有限秒值。</summary>
        internal static void ValidateSeconds(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }
        /// <summary>校验双时钟样本有限且非负、渲染帧非负；是否回拨由推进入口另行检查。</summary>
        private static void ValidateSample(double scaledTime, double unscaledTime, long renderedFrame)
        {
            ValidateSeconds(scaledTime, nameof(scaledTime));
            ValidateSeconds(unscaledTime, nameof(unscaledTime));
            if (renderedFrame < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(renderedFrame));
            }
        }
        /// <summary>允许 null 表示无限周期，指定有限总次数时要求大于零。</summary>
        internal static void ValidateCount(int? count)
        {
            if (count.HasValue && count.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
        }
        /// <summary>限制秒入口仅使用 Scaled 或 Unscaled，拒绝帧域及未定义枚举值。</summary>
        internal static void ValidateSecondClock(TimerClock clock)
        {
            if (clock != TimerClock.Scaled && clock != TimerClock.Unscaled)
            {
                throw new ArgumentOutOfRangeException(nameof(clock));
            }
        }
        /// <summary>相加有限秒样本与延迟，结果超出可表示范围时抛出溢出异常。</summary>
        private static double AddSeconds(double now, double delay)
        {
            var due = now + delay;
            if (double.IsInfinity(due))
            {
                throw new OverflowException("Timer deadline exceeds the seconds clock range.");
            }

            return due;
        }
        /// <summary>仅在首次失败时创建错误列表，保留本轮所有待报告异常。</summary>
        private static void AddError(ref List<Exception> errors, Exception error) => (errors ??= new List<Exception>()).Add(error);
        /// <summary>自动内核逐项记录异常；手动内核保留单个异常的原始栈，多异常以 AggregateException 抛出。</summary>
        private void ThrowErrors(List<Exception> errors)
        {
            if (errors == null)
            {
                return;
            }

            if (_isAutomatic)
            {
                foreach (var error in errors)
                {
                    UnityEngine.Debug.LogException(error);
                }

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
