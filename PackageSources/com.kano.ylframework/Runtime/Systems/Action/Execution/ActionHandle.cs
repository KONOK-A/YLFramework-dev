namespace YLFramework.Actions
{
    /// <summary>一次命名根动作执行的只读值类型句柄；结束或池复用后不会控制新任务。</summary>
    /// <remarks>只用于标准 StartNamed 路径。每次操作复核动作标识、控制器代次及名字登记；默认值无效。仅在 Unity 主线程使用。</remarks>
    public readonly struct ActionHandle
    {
        private readonly MonoUpdateActionExecutor _executor;
        private readonly MonoUpdateActionExecutor.ActionTask _task;
        private readonly IAction _action;
        private readonly ulong _actionId;
        private readonly ActionController _controller;
        private readonly uint _generation;
        private readonly ulong _nameToken;

        // 快照命名登记、动作标识和控制器代次，避免旧句柄控制后来的执行。
        internal ActionHandle(MonoUpdateActionExecutor executor, MonoUpdateActionExecutor.ActionTask task)
        {
            _executor = executor;
            _task = task;
            _action = task.Action;
            _actionId = task.ActionId;
            _controller = (ActionController)task.Controller;
            _generation = task.ControllerGeneration;
            _nameToken = task.NameToken;
        }

        /// <summary>是否仍持有未结束的原执行租约；暂停时仍有效，ActionId 为 0 也可有效。</summary>
        public bool IsActive => _executor && _executor.IsNamedLeaseActive(_task, _nameToken, _action,
            _actionId, _controller, _generation);

        /// <summary>暂停原动作的后续推进，不停止外部 Task 或 Unity 协程。</summary>
        /// <returns>租约仍有效时为 true；失效句柄不产生操作。</returns>
        public bool Pause()
        {
            if (!IsActive)
            {
                return false;
            }

            _controller.IsPaused = true;
            return true;
        }

        /// <summary>恢复原动作的后续推进。</summary>
        /// <returns>租约仍有效时为 true；失效句柄不产生操作。</returns>
        public bool Resume()
        {
            if (!IsActive)
            {
                return false;
            }

            _controller.IsPaused = false;
            return true;
        }

        /// <summary>取消原执行并释放名字，不调用正常完成通知；清理异常仍向调用方传播。</summary>
        /// <returns>租约仍有效且已请求取消时为 true；失效句柄不取消后来同名任务。</returns>
        public bool Cancel()
        {
            if (!IsActive)
            {
                return false;
            }

            _executor.CancelNamed(_task);
            return true;
        }

        /// <summary>切换原执行后续 Update 使用的时间增量，不改变执行租约或更新频率。</summary>
        /// <param name="mode">缩放或非缩放的时间增量模式。</param>
        /// <returns>租约仍有效时为 true；失效句柄不修改复用的控制器。</returns>
        public bool SetUpdateMode(ActionUpdateModes mode)
        {
            if (!IsActive)
            {
                return false;
            }

            _controller.UpdateMode = mode;
            return true;
        }
    }
}
