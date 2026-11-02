namespace YLFramework.FSM
{
    /// <summary>
    /// 状态机的运行状态。
    /// </summary>
    public enum MachineState
    {
        /// <summary>
        /// 已分配或已回收，尚未调用 SetUp 启动；此时不能驱动，也不能切换状态。
        /// </summary>
        NotStart,

        /// <summary>
        /// 运行中：可以驱动 Update 系列方法，也可以切换状态。
        /// </summary>
        Running,

        /// <summary>
        /// 已暂停：当前状态已执行 Exit，驱动方法不再生效；
        /// 可通过 Continue() 恢复当前状态，或 Continue(stateId) 切换到其他状态。
        /// 暂停不是终止状态，回收后仍可重新使用。
        /// </summary>
        Stop
    }
}
