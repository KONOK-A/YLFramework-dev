using System;
using YLFramework.Singletons;
using YLFramework.Timing.Internal;

namespace YLFramework.Timing
{
    /// <summary>
    /// 调用方持有的任务存储，不进入 ObjectPool。每次 Start 创建新 run 代次，旧句柄只呈现 Invalidated。
    /// 活跃 run 必须先显式取消；单次/末轮 callback 内可重启，旧 finally 不写新 run。仅主线程使用。
    /// </summary>
    public sealed class ReusableTimer : IDisposable
    {
        private readonly TimerScheduler _manual;
        private readonly object _session;
        private readonly TimerRun _storage = new();
        private bool _isDisposed;

        /// <summary>绑定当前执行会话，但不创建默认宿主；同会话 Shutdown 后可显式重新 Start。</summary>
        public ReusableTimer() => _session = SingletonSessionState.ExecutionSession;
        /// <summary>绑定显式手动内核，不读取 Unity 时间，不创建默认宿主。</summary>
        public ReusableTimer(TimerScheduler scheduler) => _manual = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        /// <summary>是否永久关闭；Dispose 后不能复活。</summary>
        public bool IsDisposed => _isDisposed;
        /// <summary>当前代次的观察/控制句柄；尚未启动时为默认无效值。历史结果须在下次 Start 前自行复制。</summary>
        public TimerHandle Current => _storage.Generation == 0 ? default : new TimerHandle(_storage);

        /// <summary>新建一次秒等待，默认 Unscaled；拒绝活跃 run、已关闭槽或旧会话槽。</summary>
        public TimerHandle Start(double delay, Action callback = null, TimerClock clock = TimerClock.Unscaled,
            Action<TimerCancellationReason> onCancelled = null)
        {
            TimerScheduler.ValidateSeconds(delay, nameof(delay));
            TimerScheduler.ValidateSecondClock(clock);
            return StartRun(clock, delay, 0, 0, 0, 1, callback, null, onCancelled);
        }

        /// <summary>新建秒周期，count 为含首轮总次数，null 为无限。</summary>
        public TimerHandle StartRepeat(double firstDelay, double interval, Action<long> callback = null, int? count = null,
            TimerClock clock = TimerClock.Unscaled, Action<TimerCancellationReason> onCancelled = null)
        {
            Timer.ValidateRepeat(firstDelay, interval, count, clock);
            return StartRun(clock, firstDelay, 0, interval, 0, count, null, callback, onCancelled);
        }

        /// <summary>新建一次 rendered frame 等待；零最早下一合法帧。</summary>
        public TimerHandle StartFrames(long frameDelay, Action callback = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            if (frameDelay < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameDelay));
            }

            return StartRun(TimerClock.RenderedFrame, 0, Math.Max(1, frameDelay), 0, 0, 1, callback, null, onCancelled);
        }

        /// <summary>新建帧周期；首次 max(1, firstDelay)，interval 须大于零。</summary>
        public TimerHandle StartRepeatFrames(long firstDelay, long interval, Action<long> callback = null, int? count = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            Timer.ValidateFrameRepeat(firstDelay, interval, count);
            return StartRun(TimerClock.RenderedFrame, 0, Math.Max(1, firstDelay), 0, interval, count, null, callback, onCancelled);
        }

        /// <summary>核对槽生命周期、执行会话及旧任务责任后，在原手动内核或当前默认内核上复用存储。</summary>
        private TimerHandle StartRun(TimerClock clock, double delay, long frames, double intervalSeconds,
            long intervalFrames, int? count, Action callback, Action<long> tickCallback, Action<TimerCancellationReason> onCancelled)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ReusableTimer));
            }

            if (_manual == null && !ReferenceEquals(_session, SingletonSessionState.ExecutionSession))
            {
                throw new InvalidOperationException("ReusableTimer belongs to an old execution session.");
            }

            if (Current.IsActive)
            {
                throw new InvalidOperationException("Cancel the active timer before starting a new run.");
            }

            return (_manual ?? Timer.GetScheduler()).Schedule(clock, delay, frames, intervalSeconds, intervalFrames,
                count, callback, tickCallback, onCancelled, _storage);
        }

        /// <summary>永久关闭槽并取消当前责任；先关闭再通知，取消回调不能重启此槽。</summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Current.Cancel();
        }
    }
}
