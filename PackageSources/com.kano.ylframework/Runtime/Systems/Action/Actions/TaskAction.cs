using System;
using System.Threading.Tasks;

namespace YLFramework.Actions
{
    /// <summary>
    /// 观察 Task 完成的池化动作，取消动作不会取消或 Dispose 外部 Task。
    /// </summary>
    /// <remarks>Reset 使旧完成通知失效；暂停动作不暂停外部 Task。</remarks>
    public class TaskAction : IAction
    {
        private static ActionPool<TaskAction> _pool =
            new ActionPool<TaskAction>(() => new TaskAction(), null, 10);

        private Func<Task> _taskGetter = null;
        private CompletionSlot _completion;

        // 完成槽隔离旧任务的异步回调：Reset/Deinit 先断开 Target，旧任务结束时就找不到可操作的动作。
        private sealed class CompletionSlot
        {
            /// <summary>本轮任务仍可通知的动作；Reset 或 Deinit 后清空。</summary>
            public TaskAction Target;
        }

        /// <summary>仅供对象池创建任务观察动作，任务工厂在 Allocate 时绑定。</summary>
        private TaskAction()
        {
        }

        /// <summary>
        /// 分配任务动作，工厂在每次启动时调用。
        /// </summary>
        /// <param name="taskGetter">每次启动时调用的 Task 工厂。</param>
        /// <returns>尚未启动且尚未调用任务工厂的动作。</returns>
        public static TaskAction Allocate(Func<Task> taskGetter)
        {
            var coroutineAction = _pool.Allocate();
            coroutineAction.ActionId = ActionKit.IdGenerator++;
            coroutineAction.IsDeinitialized = false;
            coroutineAction.Reset();
            coroutineAction._taskGetter = taskGetter;
            return coroutineAction;
        }

        /// <inheritdoc />
        public bool IsPaused
        {
            get; set;
        }

        /// <summary>使旧完成通知失效，清除任务工厂并安排延迟回收，不取消外部 Task。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                InvalidateCompletion();
                _taskGetter = null;

                ActionQueue.AddCallback(new ActionQueueRecycleCallback<TaskAction>(_pool, this));
            }
        }

        /// <summary>使旧完成通知失效并重置阶段；下次启动重新调用工厂，不取消旧 Task。</summary>
        public void Reset()
        {
            InvalidateCompletion();
            IsPaused = false;
            Status = ActionStatus.NotStart;
        }

        /// <summary>断开旧完成通知，避免旧任务结束时操作重置或复用后的动作；不取消外部任务。</summary>
        private void InvalidateCompletion()
        {
            if (_completion == null)
            {
                return;
            }

            _completion.Target = null;
            _completion = null;
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

        /// <summary>调用任务工厂并开始观察；已完成的 Task 可在此同步标记动作完成。</summary>
        /// <remarks>应在 Unity 主线程启动，使异步完成通知回到 Unity 的同步上下文。</remarks>
        public void OnStart()
        {
            StartTask();
        }

        /// <summary>为本轮启动建立独立完成槽，再异步观察工厂返回的任务。</summary>
        void StartTask()
        {
            var completion = new CompletionSlot { Target = this };
            _completion = completion;
            ObserveTask(_taskGetter, completion);
        }

        /// <summary>等待任务并更新仍绑定的动作；任务失败或取消按清理处理，不通知正常完成。</summary>
        private static async void ObserveTask(Func<Task> taskGetter, CompletionSlot completion)
        {
            // await 后的续体回到 Unity 主线程同步上下文（前提是动作在主线程启动）：
            // 取消 → 清理动作；异常 → 清理动作并记录日志；成功 → 直接标记 Finished，等待执行器正常收尾。
            Task task = null;
            try
            {
                task = taskGetter();
                taskGetter = null;
                await task;
            }
            catch (OperationCanceledException) when (task != null && task.IsCanceled)
            {
                completion.Target?.Deinit();
                return;
            }
            catch (Exception exception)
            {
                var target = completion.Target;
                if (target != null)
                {
                    target.Deinit();
                    UnityEngine.Debug.LogException(exception);
                }
                return;
            }

            var action = completion.Target;
            if (action == null)
            {
                return;
            }

            action.InvalidateCompletion();
            action.Status = ActionStatus.Finished;
        }

        /// <inheritdoc />
        public void OnExecute(float dt)
        {
        }

        /// <inheritdoc />
        public void OnFinish()
        {
        }
    }

    /// <summary>
    /// 将 Task 工厂或已有 Task 接入动作组合。
    /// </summary>
    /// <remarks>任务失败或取消会释放动作而非正常完成；取消动作不会取消外部任务。</remarks>
    public static class TaskExtension
    {
        /// <summary>
        /// 追加等待任务完成的动作，在每次启动时调用工厂。
        /// </summary>
        /// <param name="self">要追加任务动作的父组合。</param>
        /// <param name="taskGetter">每次启动时调用的 Task 工厂。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Task(this ISequence self, Func<Task> taskGetter)
        {
            return self.Append(TaskAction.Allocate(taskGetter));
        }

        /// <summary>
        /// 包装同一个 Task；Reset 仍观察原任务，不创建新任务。
        /// </summary>
        /// <param name="self">被观察的原 Task。</param>
        /// <returns>观察原任务完成的动作。</returns>
        public static IAction ToAction(this Task self)
        {
            return TaskAction.Allocate(() => self);
        }
    }
}
