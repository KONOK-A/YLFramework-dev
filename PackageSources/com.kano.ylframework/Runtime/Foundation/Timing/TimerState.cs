namespace YLFramework.Timing
{
    /// <summary>一次 run 的状态；Executing 尚未完成，末轮执行中也不再接受控制。</summary>
    public enum TimerState
    {
        /// <summary>默认句柄、复用槽旧代次或调度器关闭后的失效终态。</summary>
        Invalidated,
        /// <summary>已登记，等待后续合法帧检查到期。</summary>
        Scheduled,
        /// <summary>暂停等待，剩余秒数或帧数被冻结。</summary>
        Paused,
        /// <summary>正在同步执行到期回调；末轮已释放控制责任，但尚未提交终态。</summary>
        Executing,
        /// <summary>全部到期轮次正常执行后的终态，不要求存在用户回调。</summary>
        Completed,
        /// <summary>显式取消或生命周期绑定触发取消后的终态。</summary>
        Cancelled,
        /// <summary>调度或回调执行失败后提交的故障终态。</summary>
        Faulted
    }

    /// <summary>取消通知的原因；Faulted 与正常完成不会触发取消通知。</summary>
    public enum TimerCancellationReason
    {
        /// <summary>任务被显式取消，或由范围、绑定、复用槽等调用句柄取消。</summary>
        Cancelled,
        /// <summary>所属调度器关闭，任务以 Invalidated 结束。</summary>
        SchedulerDisposed
    }
}
