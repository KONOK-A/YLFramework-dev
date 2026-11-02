using System;

namespace YLFramework.Actions
{
    /// <summary>
    /// 启动后轮询条件，为真时调用条件回调并标记完成。
    /// </summary>
    /// <remarks>分配后不会自动执行，需交给 Start 或组合动作；完成后由执行器清理并回收，不应长期持有实例。</remarks>
    public class ConditionAction : IAction
    {
        private Func<bool> _condition;
        private Action _onCondition;

        private static ActionPool<ConditionAction> _pool =
            new ActionPool<ConditionAction>(() => new ConditionAction(), null, 10);

        /// <summary>仅供对象池创建条件动作，条件及回调由 Allocate 设置。</summary>
        private ConditionAction()
        {
        }

        /// <summary>
        /// 分配条件等待动作，不在启动阶段检测条件。
        /// </summary>
        /// <param name="condition">每次 OnExecute 时检测的条件。</param>
        /// <param name="onCondition">条件满足后、标记完成前执行的可选回调。</param>
        public static ConditionAction Allocate(Func<bool> condition, Action onCondition = null)
        {
            var conditionAction = _pool.Allocate();
            conditionAction.ActionId = ActionKit.IdGenerator++;
            conditionAction.IsDeinitialized = false;
            conditionAction.Reset();
            conditionAction._condition = condition;
            conditionAction._onCondition = onCondition;
            return conditionAction;
        }

        /// <inheritdoc />
        public bool IsPaused
        {
            get; set;
        }
        /// <inheritdoc />
        public bool IsDeinitialized
        {
            get; set;
        }
        /// <inheritdoc />
        public ulong ActionId
        {
            get; set;
        }
        /// <inheritdoc />
        public ActionStatus Status
        {
            get; set;
        }
        /// <summary>启动时不检测条件，条件从后续 OnExecute 开始轮询。</summary>
        public void OnStart()
        {
        }

        /// <summary>条件为真时调用条件回调并标记完成；条件回调若取消动作，则本次不会标记完成。</summary>
        /// <param name="dt">本次时间增量；条件检测不使用该值，仅用于满足统一的执行契约。</param>
        public void OnExecute(float dt)
        {
            // 条件与回调可能重入取消、回收或关闭模块；只允许本次租约继续完成。
            var actionId = ActionId;
            if (_condition.Invoke() && !IsDeinitialized && ActionId == actionId && !ActionKit.IsShuttingDown)
            {
                _onCondition?.Invoke();
                if (!IsDeinitialized && ActionId == actionId && !ActionKit.IsShuttingDown)
                {
                    this.Finish();
                }
            }
        }

        /// <inheritdoc />
        public void OnFinish()
        {
        }

        /// <summary>清除条件与回调并安排延迟回收，不触发条件回调。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                _condition = null;
                _onCondition = null;
                ActionQueue.AddCallback(new ActionQueueRecycleCallback<ConditionAction>(_pool, this));
            }
        }

        /// <summary>重置执行阶段和暂停状态，保留条件与回调；下次启动重新开始轮询。</summary>
        public void Reset()
        {
            IsPaused = false;
            Status = ActionStatus.NotStart;
        }
    }

    /// <summary>
    /// 向组合追加条件等待动作的链式扩展。
    /// </summary>
    public static class ConditionExtension
    {
        /// <summary>
        /// 追加启动后持续检测条件的动作，条件为真时完成并允许组合继续推进。
        /// </summary>
        /// <param name="self">要追加条件等待动作的父组合。</param>
        /// <param name="condition">每次未暂停执行时检测的条件。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Condition(this ISequence self, Func<bool> condition)
        {
            return self.Append(global::YLFramework.Actions.ConditionAction.Allocate(condition));
        }
    }
}
