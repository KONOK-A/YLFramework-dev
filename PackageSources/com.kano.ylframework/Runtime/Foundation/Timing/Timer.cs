using System;

namespace YLFramework.Timing
{
    /// <summary>主线程 Timer 默认服务；仅有效投递惰性创建独立 DDOL 驱动，不依赖 UpdateManager 或 Root。</summary>
    public static class Timer
    {
        /// <summary>取得当前默认调度器；执行门禁关闭时拒绝，允许执行且没有宿主时才惰性创建。</summary>
        internal static TimerScheduler GetScheduler() => Internal.TimerDriver.GetScheduler();

        /// <summary>一次秒等待，默认 Unscaled；非法参数或无运行权限时拒绝，不创建宿主。</summary>
        public static TimerHandle Delay(double delay, Action callback = null, TimerClock clock = TimerClock.Unscaled,
            Action<TimerCancellationReason> onCancelled = null)
        {
            TimerScheduler.ValidateSeconds(delay, nameof(delay));
            TimerScheduler.ValidateSecondClock(clock);
            return GetScheduler().Delay(delay, callback, clock, onCancelled);
        }

        /// <summary>fixed-delay 秒周期；count 含首轮，null 为无限，首次只等待 firstDelay。</summary>
        public static TimerHandle Repeat(double firstDelay, double interval, Action<long> callback = null, int? count = null,
            TimerClock clock = TimerClock.Unscaled, Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateRepeat(firstDelay, interval, count, clock);
            return GetScheduler().Repeat(firstDelay, interval, callback, count, clock, onCancelled);
        }

        /// <summary>在获取默认宿主前校验秒周期的首延迟、正间隔、次数及计时域。</summary>
        internal static void ValidateRepeat(double firstDelay, double interval, int? count, TimerClock clock)
        {
            TimerScheduler.ValidateSeconds(firstDelay, nameof(firstDelay));
            TimerScheduler.ValidateSeconds(interval, nameof(interval));
            if (interval == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }

            TimerScheduler.ValidateCount(count);
            TimerScheduler.ValidateSecondClock(clock);
        }

        /// <summary>等待未来 rendered frame；冷启动以首次 Update 锚定登记帧，零等待也不 inline。</summary>
        public static TimerHandle DelayFrames(long frameDelay, Action callback = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            if (frameDelay < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameDelay));
            }

            return GetScheduler().DelayFrames(frameDelay, callback, onCancelled);
        }

        /// <summary>帧周期，interval 须大于零；count 含首轮，null 为无限。</summary>
        public static TimerHandle RepeatFrames(long firstDelay, long interval, Action<long> callback = null, int? count = null,
            Action<TimerCancellationReason> onCancelled = null)
        {
            ValidateFrameRepeat(firstDelay, interval, count);
            return GetScheduler().RepeatFrames(firstDelay, interval, callback, count, onCancelled);
        }

        /// <summary>在获取默认宿主前校验帧周期的非负首延迟、正间隔及次数。</summary>
        internal static void ValidateFrameRepeat(long firstDelay, long interval, int? count)
        {
            if (firstDelay < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(firstDelay));
            }

            if (interval <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }

            TimerScheduler.ValidateCount(count);
        }

        /// <summary>仅投递时换算一次非负 Unscaled delay；过去时间按零等待，不追踪墙钟、不提供后台唤醒。</summary>
        public static TimerHandle At(DateTimeOffset target, Action callback = null,
            Action<TimerCancellationReason> onCancelled = null) =>
            Delay(DelayUntil(target, DateTimeOffset.UtcNow), callback, TimerClock.Unscaled, onCancelled);

        /// <summary>将目标时刻相对给定当前时刻换算为非负秒数，不保留墙钟跟踪责任。</summary>
        internal static double DelayUntil(DateTimeOffset target, DateTimeOffset now) => Math.Max(0, (target - now).TotalSeconds);

        /// <summary>只结束既有默认 owner，不创建宿主，不清理其他领域。后续有效投递可以显式建立新 owner。</summary>
        public static void Shutdown() => Internal.TimerDriver.Shutdown();
    }
}
