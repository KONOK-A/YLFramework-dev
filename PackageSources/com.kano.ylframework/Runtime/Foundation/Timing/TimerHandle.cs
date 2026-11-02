using YLFramework.Timing.Internal;

namespace YLFramework.Timing
{
    /// <summary>
    /// 精确控制一次 run 的只读句柄。默认值与旧代次为 Invalidated，控制为空操作。
    /// 普通 run 保留终态和执行次数，完成后不保留 callback 或调度器。仅主线程使用。
    /// </summary>
    public readonly struct TimerHandle
    {
        private readonly TimerRun _run;
        private readonly ulong _generation;

        /// <summary>同时捕获任务存储与当前代次，防止旧句柄控制复用后的新任务。</summary>
        internal TimerHandle(TimerRun run)
        {
            _run = run;
            _generation = run.Generation;
        }
        private bool IsCurrent => _run != null && _run.Generation == _generation;
        internal TimerRun Run => IsCurrent ? _run : null;

        /// <summary>本次 run 的状态；末轮 Executing 时尚未成为终态。</summary>
        public TimerState Status => IsCurrent ? _run.State : TimerState.Invalidated;
        /// <summary>仍有调度/取消责任，包含暂停和非末轮执行；末轮回调开始前为 false。</summary>
        public bool IsActive
        {
            get
            {
                if (!IsCurrent)
                {
                    return false;
                }

                _run.Owner?.Refresh();
                return IsCurrent && _run.Owner != null;
            }
        }
        /// <summary>是否已进入稳定终态；不能用 !IsActive 替代此查询。</summary>
        public bool IsFinished => Status == TimerState.Completed || Status == TimerState.Cancelled ||
            Status == TimerState.Faulted || Status == TimerState.Invalidated;
        /// <summary>已消费的到期轮数，从零开始；回调开始前递增，纯等待也计数。</summary>
        public long ExecutionCount => IsCurrent ? _run.ExecutionCount : 0;
        /// <summary>秒域剩余量；默认实例按统一采样器取得当前合法渲染样本，手动实例用最近输入。暂停冻结，执行中/终态为零。</summary>
        public double RemainingSeconds => IsActive ? _run.Owner.RemainingSeconds(_run) : 0;
        /// <summary>帧域的剩余 rendered frame 数；秒任务、执行中或终态为零。</summary>
        public long RemainingFrames => IsActive ? _run.Owner.RemainingFrames(_run) : 0;
        /// <summary>先提交取消与清引用再通知；手动通知异常传播，默认边界日志报告。末轮/终态与重复取消返回 false。</summary>
        public bool Cancel() => IsActive && _run.Owner.Cancel(_run, _generation);
        /// <summary>冻结剩余量；非末轮回调内冻结完整下一周期。重复暂停返回 true。</summary>
        public bool Pause() => IsActive && _run.Owner.Pause(_run, _generation);
        /// <summary>按当前样本恢复等待，最早下一 rendered frame；回调内可撤销刚请求的暂停，其他非暂停状态返回 false。</summary>
        public bool Resume() => IsActive && _run.Owner.Resume(_run, _generation);

        /// <summary>
        /// 等待本句柄捕获的 run 进入终态；结束后读取 TimerWait.Status 区分完成、取消、故障和失效。
        /// 停止等待协程不会取消 Timer；等待器分配不属于 NonAlloc 承诺。
        /// </summary>
        public TimerWait WaitForTermination() => new TimerWait(this);
    }
}
