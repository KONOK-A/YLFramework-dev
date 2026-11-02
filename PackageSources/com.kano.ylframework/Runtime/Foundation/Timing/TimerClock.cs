namespace YLFramework.Timing
{
    /// <summary>计时域；帧域仅代表 Unity rendered frame，不代表 FixedUpdate 或 Action 步数。</summary>
    public enum TimerClock
    {
        /// <summary>受时间缩放影响的秒域；默认驱动采样 Time.timeAsDouble。</summary>
        Scaled,
        /// <summary>不受时间缩放影响的秒域；默认驱动采样 Time.unscaledTimeAsDouble。</summary>
        Unscaled,
        /// <summary>渲染帧计数域，按帧身份而非固定时间步推进。</summary>
        RenderedFrame
    }
}
