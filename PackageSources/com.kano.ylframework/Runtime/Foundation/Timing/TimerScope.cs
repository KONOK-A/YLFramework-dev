using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Timing.Internal;

namespace YLFramework.Timing
{
    /// <summary>
    /// 独立的计时任务责任与 Ordinal 名称域，仅主线程使用，不参与推进、不拥有调度器。
    /// 默认范围捕获创建时的执行会话；同会话宿主重建后可重新投递，但不会恢复旧任务。
    /// </summary>
    public sealed class TimerScope : IDisposable
    {
        private readonly TimerScheduler _scheduler;
        private readonly object _session;
        private readonly HashSet<TimerRun> _runs = new();
        private readonly Dictionary<string, TimerRun> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ulong> _replacements = new(StringComparer.Ordinal);
        private ulong _version;

        /// <summary>创建默认运行范围；不创建 Timer 宿主。</summary>
        public TimerScope() => _session = SingletonSessionState.ExecutionSession;

        /// <summary>绑定明确的手动实例；实例关闭后不切换到默认或其他实例。</summary>
        public TimerScope(TimerScheduler scheduler) => _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

        /// <summary>范围是否已永久关闭；关闭后投递抛出 ObjectDisposedException。</summary>
        public bool IsDisposed
        {
            get; private set;
        }

        /// <summary>匿名秒等待，参数与 TimerScheduler.Delay 一致。</summary>
        public TimerHandle Delay(double delay, Action callback = null, TimerClock clock = TimerClock.Unscaled,
            Action<TimerCancellationReason> onCancelled = null) =>
            Schedule(null, false, clock, delay, 0, 0, 0, false, 1, callback, null, onCancelled);

        /// <summary>匿名秒周期任务，count 含首轮，null 为无限。</summary>
        public TimerHandle Repeat(double firstDelay, double interval, Action<long> callback = null, int? count = null,
            TimerClock clock = TimerClock.Unscaled, Action<TimerCancellationReason> onCancelled = null) =>
            Schedule(null, false, clock, firstDelay, 0, interval, 0, true, count, null, callback, onCancelled);

        /// <summary>匿名 rendered frame 等待，零表示下一个合法帧。</summary>
        public TimerHandle DelayFrames(long frameDelay, Action callback = null,
            Action<TimerCancellationReason> onCancelled = null) =>
            Schedule(null, false, TimerClock.RenderedFrame, 0, frameDelay, 0, 0, false, 1, callback, null, onCancelled, true);

        /// <summary>匿名帧周期任务，interval 须为正数，count 含首轮。</summary>
        public TimerHandle RepeatFrames(long firstDelay, long interval, Action<long> callback = null, int? count = null,
            Action<TimerCancellationReason> onCancelled = null) =>
            Schedule(null, false, TimerClock.RenderedFrame, 0, firstDelay, 0, interval, true, count, null, callback, onCancelled, true);

        /// <summary>登记命名秒等待；名称须非空白，活动同名任务拒绝重复投递。</summary>
        public TimerHandle PostNamed(string name, double delay, Action callback = null, TimerClock clock = TimerClock.Unscaled,
            Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, false, clock, delay, 0, 0, 0, false, 1, callback, null, onCancelled);
        }

        /// <summary>登记命名秒周期任务；firstDelay 与 interval 独立，count 含首轮。</summary>
        public TimerHandle PostNamed(string name, double firstDelay, double interval, Action<long> callback = null,
            int? count = null, TimerClock clock = TimerClock.Unscaled, Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, false, clock, firstDelay, 0, interval, 0, true, count, null, callback, onCancelled);
        }

        /// <summary>登记命名帧等待。</summary>
        public TimerHandle PostNamedFrames(string name, long frameDelay, Action callback = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, false, TimerClock.RenderedFrame, 0, frameDelay, 0, 0, false, 1, callback, null, onCancelled, true);
        }

        /// <summary>登记命名帧周期任务，count 含首轮。</summary>
        public TimerHandle PostNamedFrames(string name, long firstDelay, long interval, Action<long> callback = null,
            int? count = null, Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, false, TimerClock.RenderedFrame, 0, firstDelay, 0, interval, true, count, null, callback, onCancelled, true);
        }

        /// <summary>
        /// 先校验再取消旧同名任务并登记秒等待。取消通知重入同名投递时，外层不覆盖它并返回默认句柄。
        /// 手动取消通知抛错时保留已提交的取消，不继续外层投递。
        /// </summary>
        public TimerHandle ReplaceNamed(string name, double delay, Action callback = null, TimerClock clock = TimerClock.Unscaled,
            Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, true, clock, delay, 0, 0, 0, false, 1, callback, null, onCancelled);
        }

        /// <summary>替换命名秒周期任务；同名取消重入优先于本次外层投递。</summary>
        public TimerHandle ReplaceNamed(string name, double firstDelay, double interval, Action<long> callback = null,
            int? count = null, TimerClock clock = TimerClock.Unscaled, Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, true, clock, firstDelay, 0, interval, 0, true, count, null, callback, onCancelled);
        }

        /// <summary>替换命名帧等待；先校验参数，同名取消重入优先。</summary>
        public TimerHandle ReplaceNamedFrames(string name, long frameDelay, Action callback = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, true, TimerClock.RenderedFrame, 0, frameDelay, 0, 0, false, 1, callback, null, onCancelled, true);
        }

        /// <summary>替换命名帧周期任务；先校验参数，同名取消重入优先。</summary>
        public TimerHandle ReplaceNamedFrames(string name, long firstDelay, long interval, Action<long> callback = null,
            int? count = null, Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateName(name);
            return Schedule(name, true, TimerClock.RenderedFrame, 0, firstDelay, 0, interval, true, count, null, callback, onCancelled, true);
        }

        /// <summary>校验并登记范围内任务；命名替换以版本标记确认取消通知重入后是否仍持有本次投递责任。</summary>
        private TimerHandle Schedule(string name, bool replace, TimerClock clock, double delay, long frames,
            double intervalSeconds, long intervalFrames, bool repeating, int? count, Action callback,
            Action<long> tickCallback, Action<TimerCancellationReason> onCancelled, bool frameTask = false)
        {
            if (frameTask)
            {
                if (frames < 0)
                {
                    throw new ArgumentOutOfRangeException(repeating ? "firstDelay" : "frameDelay");
                }

                if (repeating && intervalFrames <= 0)
                {
                    throw new ArgumentOutOfRangeException("interval");
                }
            }
            else
            {
                TimerScheduler.ValidateSeconds(delay, repeating ? "firstDelay" : "delay");
                TimerScheduler.ValidateSecondClock(clock);
                if (repeating)
                {
                    TimerScheduler.ValidateSeconds(intervalSeconds, "interval");
                    if (intervalSeconds == 0)
                    {
                        throw new ArgumentOutOfRangeException("interval");
                    }
                }
            }
            TimerScheduler.ValidateCount(count);
            ThrowIfUnavailable();
            if (!replace && name != null && Contains(name))
            {
                throw new InvalidOperationException("Timer name already exists in this scope: " + name);
            }

            var version = replace ? checked(++_version) : 0;
            if (replace)
            {
                _replacements[name] = version;
            }

            try
            {
                if (replace)
                {
                    (_scheduler ?? Timer.GetScheduler()).ValidateDeadline(clock, delay, frameTask ? Math.Max(1, frames) : 0);
                    ThrowIfUnavailable();
                    if (!_replacements.TryGetValue(name, out var validated) || validated != version)
                    {
                        return default;
                    }

                    Cancel(name);
                    ThrowIfUnavailable();
                    if (!_replacements.TryGetValue(name, out var current) || current != version)
                    {
                        return default;
                    }
                }
                var scheduler = _scheduler ?? Timer.GetScheduler();
                // 默认 owner 的失效清理可能通知用户并重入；取得实例后再次核对本次投递责任。
                ThrowIfUnavailable();
                if (replace && (!_replacements.TryGetValue(name, out var latest) || latest != version))
                {
                    return default;
                }

                if (!replace && name != null && Contains(name))
                {
                    throw new InvalidOperationException("Timer name already exists in this scope: " + name);
                }

                TimerHandle handle;
                if (frameTask)
                {
                    handle = repeating ? scheduler.RepeatFrames(frames, intervalFrames, tickCallback, count, onCancelled) :
                        scheduler.DelayFrames(frames, callback, onCancelled);
                }
                else
                {
                    handle = repeating ? scheduler.Repeat(delay, intervalSeconds, tickCallback, count, clock, onCancelled) :
                        scheduler.Delay(delay, callback, clock, onCancelled);
                }

                var run = handle.Run;
                run.Scope = this;
                run.Name = name;
                _runs.Add(run);
                if (name != null)
                {
                    _names.Add(name, run);
                    _replacements.Remove(name);
                }
                return handle;
            }
            finally
            {
                if (replace && _replacements.TryGetValue(name, out var current) && current == version)
                {
                    _replacements.Remove(name);
                }
            }
        }

        /// <summary>查询活动同名责任，不创建默认宿主。名称按 Ordinal 比较。</summary>
        public bool Contains(string name) => TryGet(name, out _);

        /// <summary>查询命名句柄；未找到或范围已关闭时返回 false 和默认句柄，不创建宿主。</summary>
        public bool TryGet(string name, out TimerHandle handle)
        {
            ValidateName(name);
            if (!IsDisposed && _names.TryGetValue(name, out var run))
            {
                handle = new TimerHandle(run);
                if (handle.IsActive && ReferenceEquals(run.Scope, this))
                {
                    return true;
                }

                Detach(run);
            }
            handle = default;
            return false;
        }

        /// <summary>取消当前同名任务；取消已提交后通知异常不复活旧责任。</summary>
        public bool Cancel(string name) => TryGet(name, out var handle) && handle.Cancel();

        /// <summary>暂停当前同名任务并冻结剩余量。</summary>
        public bool Pause(string name) => TryGet(name, out var handle) && handle.Pause();

        /// <summary>恢复当前同名任务，不创建或重建宿主。</summary>
        public bool Resume(string name) => TryGet(name, out var handle) && handle.Resume();

        /// <summary>尝试取消入口时的全部责任；取消通知中新投递的任务保留，范围可继续使用。</summary>
        public void CancelAll()
        {
            var snapshot = ListPool<TimerHandle>.Allocate(_runs.Count);
            List<Exception> errors = null;
            try
            {
                foreach (var run in _runs)
                {
                    snapshot.Add(new TimerHandle(run));
                }

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

        /// <summary>先永久关闭再取消全部责任，禁止取消通知重入投递；不关闭所属调度器。</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            CancelAll();
        }

        /// <summary>解除仍归属于本范围的任务与名称占用，不移除已经被其他任务替换的同名条目。</summary>
        internal void Detach(TimerRun run)
        {
            if (!ReferenceEquals(run.Scope, this))
            {
                return;
            }

            if (run.Name != null && _names.TryGetValue(run.Name, out var current) && ReferenceEquals(current, run))
            {
                _names.Remove(run.Name);
            }

            _runs.Remove(run);
            run.Scope = null;
            run.Name = null;
        }

        /// <summary>拒绝空白名称，避免命名入口与匿名任务混用。</summary>
        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Timer name cannot be empty.", nameof(name));
            }
        }

        /// <summary>拒绝已关闭范围、已关闭的绑定内核，以及默认范围捕获的旧执行会话。</summary>
        private void ThrowIfUnavailable()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(TimerScope));
            }

            if (_scheduler != null)
            {
                if (_scheduler.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(TimerScheduler));
                }
            }
            else if (!ReferenceEquals(_session, SingletonSessionState.ExecutionSession))
            {
                throw new InvalidOperationException("Timer scope belongs to an earlier execution session.");
            }
        }
    }
}
