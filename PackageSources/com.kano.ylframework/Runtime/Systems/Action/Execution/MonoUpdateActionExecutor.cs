using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace YLFramework.Actions
{
    // 在所属 GameObject 每帧推进动作任务：所有 Start* 路径的运行时实现，对象、场景与全局宿主共用同一套调度逻辑。
    // 任务记录在 _tasks 中按创建顺序推进；任务释放集中在最外层执行栈退出时进行，避免回调期间复用控制器。
    [ExecuteAlways]
    internal class MonoUpdateActionExecutor : MonoBehaviour, IActionExecutor
    {
        /// <summary>一次调度的运行时记录，绑定动作与控制器，并保存用于检测租约失效的标识快照。</summary>
        public class ActionTask
        {
            /// <summary>本次调度接管的根动作。</summary>
            public IAction Action;
            /// <summary>绑定本次动作执行的控制器，任务结束后由执行器归还池。</summary>
            public IActionController Controller;
            /// <summary>根动作正常完成且释放前调用的通知，取消不触发。</summary>
            public Action<IActionController> OnFinish;
            // 调度时快照的动作标识；运行中与当前值不一致说明动作已被复用，任务应立即作废。
            internal ulong ActionId;
            // 调度时快照的控制器代次，用于识别控制器被池复用。
            internal uint ControllerGeneration;
            // 已结束（完成、被取消或被判定失效）；ReleaseFinished 会回收 Done 的任务。
            internal bool IsDone;
            // 命名任务的名字；匿名任务为 null，不进入名字索引。
            internal string Flag;
            // 名字登记代次；句柄用它确认自己引用的仍是登记时的那个任务。
            internal ulong NameToken;
        }

        private readonly List<ActionTask> _tasks = new List<ActionTask>();
        // 当前执行嵌套深度；大于 0 时说明有任务正在回调栈中执行，宿主释放要等栈退出。
        private int _executionDepth;
        private bool _isStopping;
        // 匿名执行不创建名字索引，也不分配额外包装对象。
        private Dictionary<string, ActionTask> _namedTasks;
        private ulong _nextNameToken;
        // 动作所有权按对象身份登记，不受动作实现自定义 Equals 或 GetHashCode 的影响。
        private sealed class ActionIdentity : IEqualityComparer<IAction>
        {
            /// <summary>比较是否为同一个动作对象，不按状态或业务数据判断相等。</summary>
            public bool Equals(IAction first, IAction second) => ReferenceEquals(first, second);
            /// <summary>取得与对象身份比较一致的哈希值。</summary>
            public int GetHashCode(IAction value) => RuntimeHelpers.GetHashCode(value);
        }
        // 全局唯一所有权：某个动作实例同一时间只能被一个执行任务持有，防止池对象被重复调度或跨执行器共享。
        private static readonly Dictionary<IAction, ActionTask> _owners =
            new Dictionary<IAction, ActionTask>(new ActionIdentity());
        // 模块级执行作用域计数（所有执行器共享）；归零时处理被延迟的 ActionKit.Shutdown。
        internal static int ExecutionScopes
        {
            get; private set;
        }
        // 登记模块级执行作用域，阻止 Shutdown 在业务回调栈内实际释放资源。
        internal static void EnterExecutionScope() => ExecutionScopes++;
        // 最外层模块级作用域退出时，继续处理此前请求的关停。
        internal static void LeaveExecutionScope()
        {
            ExecutionScopes--;
            if (ExecutionScopes == 0)
            {
                ActionKit.CompletePendingShutdown();
            }
        }

        // ActionTask 池化复用；回收时清空全部字段，避免旧任务引用被带到下一次调度。
        private static readonly ActionPool<ActionTask> _actionTaskPool = new ActionPool<ActionTask>(
            () => new ActionTask(), task =>
            {
                task.Action = null;
                task.Controller = null;
                task.OnFinish = null;
                task.ActionId = 0;
                task.ControllerGeneration = 0;
                task.IsDone = false;
                task.Flag = null;
                task.NameToken = 0;
            }, 50);

        internal int ScheduledCount => _tasks.Count;
        internal bool IsStopping => _isStopping;

        /// <summary>以匿名根任务接管控制器并同步首次推进动作，后续由 Update 驱动。</summary>
        /// <param name="controller">已经绑定动作及匹配标识的控制器。</param>
        /// <param name="onFinish">正常完成且清理前调用的可选通知。</param>
        public void Execute(IActionController controller, Action<IActionController> onFinish = null)
        {
            Execute(controller, onFinish, null);
        }

        internal static void ValidateFlag(string flag)
        {
            if (string.IsNullOrWhiteSpace(flag))
            {
                throw new ArgumentException("A named action requires a non-blank flag.", nameof(flag));
            }
        }

        // 调度前校验：动作非空、未清理，且没有被其他执行任务以相同标识持有。
        internal static void ValidateAction(IAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (action.IsDeinitialized)
            {
                throw new InvalidOperationException("Allocate a new action before scheduling a deinitialized action.");
            }

            if (_owners.TryGetValue(action, out var registered) && registered.ActionId == action.ActionId)
            {
                throw new InvalidOperationException("The same action already has an executor.");
            }
        }

        // 在指定 GameObject 上查找仍可用的执行器；create 为 true 时按需添加组件，否则返回 null。
        internal static MonoUpdateActionExecutor Find(GameObject owner, bool create = false)
        {
            if (!owner)
            {
                return null;
            }

            foreach (var candidate in owner.GetComponents<MonoUpdateActionExecutor>())
            {
                if (!candidate.IsStopping)
                {
                    return candidate;
                }
            }

            return create ? owner.AddComponent<MonoUpdateActionExecutor>() : null;
        }

        // 命名启动：先验证，再按 replace 语义取消旧同名任务，登记新名字并立即做首次推进；
        // 动作若同步完成，返回的句柄在返回时已失效。
        internal ActionHandle StartNamed(IAction action, string flag, bool replace,
            Action<IActionController> onFinish)
        {
            ValidateFlag(flag);
            ValidateAction(action);
            if (_isStopping || ActionKit.IsShuttingDown)
            {
                throw new InvalidOperationException("ActionKit executor is shutting down.");
            }

            if (TryGetNamed(flag, out var previous))
            {
                if (!replace)
                {
                    throw new InvalidOperationException("An active action already uses flag: " + flag);
                }

                previous.Cancel();
            }
            // 取消时的清理回调可能已重入登记同名任务或关闭宿主；不覆盖它，也不复活旧任务。
            if (!this || _isStopping || ActionKit.IsShuttingDown)
            {
                throw new InvalidOperationException("ActionKit executor is shutting down.");
            }

            if (TryGetNamed(flag, out _))
            {
                throw new InvalidOperationException("An active action already uses flag: " + flag);
            }

            ValidateAction(action);
            var controller = (ActionController)ActionController.Allocate();
            var generation = controller.Generation;
            controller.Action = action;
            controller.ActionId = action.ActionId;
            controller.UpdateMode = ActionUpdateModes.ScaledDeltaTime;
            try
            {
                return Execute(controller, onFinish, flag);
            }
            catch
            {
                if (controller.Generation == generation)
                {
                    controller.Recycle();
                }

                throw;
            }
        }

        // 查询命名任务：若登记仍在但租约已失效，会顺手剔除这条过期登记，避免句柄指向死任务。
        internal bool TryGetNamed(string flag, out ActionHandle handle)
        {
            ValidateFlag(flag);
            handle = default;
            if (_isStopping || ActionKit.IsShuttingDown || _namedTasks == null ||
                !_namedTasks.TryGetValue(flag, out var task))
            {
                return false;
            }

            handle = new ActionHandle(this, task);
            if (handle.IsActive)
            {
                return true;
            }

            RemoveName(task);
            handle = default;
            return false;
        }

        // 句柄操作共用的租约复核：名字代次、任务身份、动作标识与控制器代次全部匹配且都在效时才为 true；
        // 只要动作已 Finished、已清理或控制器被复用，就删除名字登记并返回 false。
        internal bool IsNamedLeaseActive(ActionTask task, ulong token, IAction action, ulong actionId,
            ActionController controller, uint generation)
        {
            if (_isStopping || ActionKit.IsShuttingDown || task == null || token == 0 ||
                task.NameToken != token || task.IsDone || !ReferenceEquals(task.Action, action) ||
                task.ActionId != actionId || !ReferenceEquals(task.Controller, controller) ||
                task.ControllerGeneration != generation)
            {
                return false;
            }

            if (action.ActionId == actionId && !action.IsDeinitialized && action.Status != ActionStatus.Finished &&
                controller.Generation == generation && HasController(task))
            {
                return true;
            }

            RemoveName(task);
            return false;
        }

        // 取消命名任务：先移出名字索引并标记 Done，再在受控执行作用域内清理动作，
        // 栈退出时统一回收任务、控制器与动作对象。
        internal void CancelNamed(ActionTask task)
        {
            task.IsDone = true;
            RemoveName(task);
            _executionDepth++;
            EnterExecutionScope();
            Exception executionError = null;
            try
            {
                task.Controller.Deinit();
            }
            catch (Exception exception)
            {
                executionError = exception;
                throw;
            }
            finally
            {
                try
                {
                    ExitExecution();
                }
                catch (Exception cleanupError)
                {
                    if (executionError == null)
                    {
                        throw;
                    }

                    AddDiagnostic(executionError, "ActionKit.CleanupException", cleanupError);
                }
            }
        }

        /// <summary>释放仍属于本任务的名字登记，并清空任务保存的名字与登记代次。</summary>
        private void RemoveName(ActionTask task)
        {
            if (task.Flag == null)
            {
                return;
            }
            // 只删除仍属于本任务的登记：同名名字可能已被取消回调重入替换成新任务。
            if (_namedTasks != null && _namedTasks.TryGetValue(task.Flag, out var registered) &&
                ReferenceEquals(registered, task) && registered.NameToken == task.NameToken)
            {
                _namedTasks.Remove(task.Flag);
            }

            task.Flag = null;
            task.NameToken = 0;
        }

        // 调度入口：校验动作所有权与重复调度，登记任务（可选命名），随后以 dt=0 做首次同步推进（触发 OnStart，可立即完成）。
        private ActionHandle Execute(IActionController controller, Action<IActionController> onFinish, string flag)
        {
            if (_isStopping || ActionKit.IsShuttingDown)
            {
                throw new InvalidOperationException("ActionKit executor is shutting down.");
            }

            if (controller == null || controller.Action == null || controller.ActionId != controller.Action.ActionId)
            {
                throw new ArgumentException("The controller must identify the action it schedules.", nameof(controller));
            }

            if (controller.Action.IsDeinitialized)
            {
                throw new InvalidOperationException("Allocate a new action before scheduling a deinitialized action.");
            }

            if (_owners.TryGetValue(controller.Action, out var registered) && registered.ActionId == controller.ActionId)
            {
                throw new InvalidOperationException("The same action already has an executor.");
            }

            foreach (var existing in _tasks)
            {
                if (ReferenceEquals(existing.Action, controller.Action) && existing.ActionId == controller.ActionId)
                {
                    throw new InvalidOperationException("The same action cannot be started twice on this executor.");
                }
            }

            var task = _actionTaskPool.Allocate();
            task.Action = controller.Action;
            task.Controller = controller;
            task.OnFinish = onFinish;
            task.ActionId = controller.ActionId;
            task.ControllerGeneration = controller is ActionController concrete ? concrete.Generation : 0;
            _tasks.Add(task);
            _owners[task.Action] = task;
            var handle = default(ActionHandle);
            if (flag != null)
            {
                if (_namedTasks == null)
                {
                    _namedTasks = new Dictionary<string, ActionTask>(StringComparer.Ordinal);
                }

                task.Flag = flag;
                task.NameToken = ++_nextNameToken;
                if (task.NameToken == 0)
                {
                    task.NameToken = ++_nextNameToken;
                }

                _namedTasks.Add(flag, task);
                handle = new ActionHandle(this, task);
            }
            _executionDepth++;
            EnterExecutionScope();
            Exception executionError = null;
            try
            {
                // 动作若带着 Finished 状态再次被调度（尚未回收），先 Reset 回到起点再推进。
                if (task.Action.Status == ActionStatus.Finished)
                {
                    task.Action.Reset();
                }

                if (_isStopping || ActionKit.IsShuttingDown)
                {
                    task.IsDone = true;
                }
                else
                {
                    Step(task, 0);
                }
            }
            catch (Exception exception)
            {
                task.IsDone = true;
                executionError = exception;
                throw;
            }
            finally
            {
                try
                {
                    ExitExecution();
                }
                catch (Exception cleanupError)
                {
                    if (executionError == null)
                    {
                        throw;
                    }

                    AddDiagnostic(executionError, "ActionKit.CleanupException", cleanupError);
                }
            }
            return handle;
        }

        /// <summary>响应宿主 Update；ExecuteAlways 仅用于生命周期清理，编辑状态不自动推进动作。</summary>
        private void Update()
        {
            if (Application.IsPlaying(gameObject))
            {
                Tick(Time.deltaTime, Time.unscaledDeltaTime);
            }
        }

        // 每次宿主 Update 调用一次：按任务创建顺序推进，每个任务按自身控制器的 UpdateMode 选择时间增量。
        internal void Tick(float scaledDeltaTime, float unscaledDeltaTime)
        {
            if (_isStopping || ActionKit.IsShuttingDown)
            {
                return;
            }
            // 固定本轮迭代上限：推进期间新启动的任务已完成首次推进，留到下一轮再走常规更新。
            var count = _tasks.Count;
            _executionDepth++;
            EnterExecutionScope();
            Exception executionError = null;
            try
            {
                for (var index = 0; index < count && !_isStopping; index++)
                {
                    var task = _tasks[index];
                    if (task.IsDone)
                    {
                        continue;
                    }

                    var mode = task.Controller.UpdateMode;
                    if (mode == ActionUpdateModes.ScaledDeltaTime)
                    {
                        Step(task, scaledDeltaTime);
                    }
                    else if (mode == ActionUpdateModes.UnscaledDeltaTime)
                    {
                        Step(task, unscaledDeltaTime);
                    }
                }
            }
            catch (Exception exception)
            {
                executionError = exception;
                throw;
            }
            finally
            {
                try
                {
                    ExitExecution();
                }
                catch (Exception cleanupError)
                {
                    if (executionError == null)
                    {
                        throw;
                    }

                    AddDiagnostic(executionError, "ActionKit.CleanupException", cleanupError);
                }
            }
        }

        // 控制器、动作与代次三方匹配：任何一方被复用或失配都视为任务失效。
        private static bool HasController(ActionTask task) =>
            task.Controller.ActionId == task.ActionId && ReferenceEquals(task.Controller.Action, task.Action) &&
            (!(task.Controller is ActionController concrete) || concrete.Generation == task.ControllerGeneration);

        /// <summary>推进单个任务并处理终止登记，只有仍有效的正常完成任务才发出完成通知。</summary>
        private void Step(ActionTask task, float dt)
        {
            try
            {
                // 推进前后都复核租约：动作可能在 OnStart/OnExecute 中取消自己、被替换或触发 Shutdown。
                if (task.IsDone || !HasController(task) || task.Action.ActionId != task.ActionId || task.Action.IsDeinitialized)
                {
                    task.IsDone = true;
                    return;
                }
                var completed = task.Action.Execute(dt);
                if (task.IsDone || !HasController(task) || task.Action.ActionId != task.ActionId || task.Action.IsDeinitialized)
                {
                    task.IsDone = true;
                    return;
                }
                if (!completed)
                {
                    return;
                }

                task.IsDone = true;
                RemoveName(task);
                // 仅在未停止且租约仍有效时通知正常完成；回调抛出的异常会向上传播，并在执行栈退出时完成清理。
                if (!_isStopping && HasController(task) && !task.Action.IsDeinitialized)
                {
                    task.OnFinish?.Invoke(task.Controller);
                }
            }
            catch
            {
                task.IsDone = true;
                RemoveName(task);
                throw;
            }
        }

        // 宿主释放或 Shutdown 时停用执行器：不再接受新任务、清空名字索引；
        // 若当前没有执行中的回调则立即释放全部任务（不触发正常完成通知），否则等最外层执行退出后释放。
        internal void StopAll()
        {
            _isStopping = true;
            _namedTasks?.Clear();
            if (_executionDepth == 0)
            {
                ReleaseFinished();
            }
        }

        /// <summary>结束一次执行，按嵌套深度衔接任务释放与延后请求的模块关停。</summary>
        private void ExitExecution()
        {
            _executionDepth--;
            Exception releaseError = null;
            try
            {
                // 只有最外层执行退出时才统一释放：更内层的回调仍可能在访问任务与控制器。
                if (_executionDepth == 0)
                {
                    ReleaseFinished();
                }
            }
            catch (Exception exception)
            {
                releaseError = exception;
                throw;
            }
            finally
            {
                try
                {
                    LeaveExecutionScope();
                }
                catch (Exception shutdownError)
                {
                    if (releaseError == null)
                    {
                        throw;
                    }

                    AddDiagnostic(releaseError, "ActionKit.ShutdownException", shutdownError);
                }
            }
        }

        /// <summary>把次级清理异常附加到主异常的诊断项，已有诊断时合并保存。</summary>
        private static void AddDiagnostic(Exception primary, string key, Exception secondary)
        {
            primary.Data[key] = primary.Data[key] is Exception previous
                ? new AggregateException(previous, secondary) : secondary;
        }

        /// <summary>移除已终止任务，按租约释放动作和控制器；停止期间一并释放尚未完成的任务。</summary>
        private void ReleaseFinished()
        {
            // 逆序移除，避免原地删除改变尚未访问项的索引。
            List<Exception> errors = null;
            for (var index = _tasks.Count - 1; index >= 0; index--)
            {
                var task = _tasks[index];
                if (!_isStopping && !task.IsDone)
                {
                    continue;
                }

                _tasks.RemoveAt(index);
                RemoveName(task);
                if (_owners.TryGetValue(task.Action, out var owner) && ReferenceEquals(owner, task))
                {
                    _owners.Remove(task.Action);
                }

                try
                {
                    // 依次释放动作、归还控制器、回收任务对象；每步独立收集异常，保证释放流程完整。
                    if (task.Action.ActionId == task.ActionId && !task.Action.IsDeinitialized)
                    {
                        task.Action.Deinit();
                    }
                }
                catch (Exception exception)
                {
                    if (errors == null)
                    {
                        errors = new List<Exception>();
                    }

                    errors.Add(exception);
                }
                finally
                {
                    try
                    {
                        if (HasController(task))
                        {
                            task.Controller.Recycle();
                        }
                    }
                    catch (Exception exception)
                    {
                        if (errors == null)
                        {
                            errors = new List<Exception>();
                        }

                        errors.Add(exception);
                    }
                    finally
                    {
                        _actionTaskPool.Recycle(task);
                    }
                }
            }
            if (errors != null)
            {
                throw new AggregateException("ActionKit action cleanup failed.", errors);
            }
        }

        /// <summary>执行器销毁时取消其全部任务，不发出正常完成通知。</summary>
        private void OnDestroy() => StopAll();
    }

    /// <summary>将动作交给组件所属 GameObject 的 Update 执行器。</summary>
    public static class MonoUpdateActionExecutorExtension
    {
        /// <summary>复用或添加模块执行器，立即接管控制器并首次推进动作。</summary>
        /// <typeparam name="T">宿主组件的类型。</typeparam>
        /// <param name="self">执行器所在 GameObject 的组件。</param>
        /// <param name="action">返回给调用方的动作，应与 controller.Action 相同。</param>
        /// <param name="controller">已绑定动作和匹配标识的控制器。</param>
        /// <param name="onFinish">正常完成且清理前调用的回调。</param>
        /// <returns>传入的动作；实际调度以 controller.Action 为准。</returns>
        public static IAction ExecuteByUpdate<T>(this T self, IAction action, IActionController controller,
            Action<IActionController> onFinish = null) where T : MonoBehaviour
        {
            MonoUpdateActionExecutor executor = null;
            foreach (var candidate in self.gameObject.GetComponents<MonoUpdateActionExecutor>())
            {
                if (!candidate.IsStopping)
                {
                    executor = candidate;
                    break;
                }
            }
            if (!executor)
            {
                executor = self.gameObject.AddComponent<MonoUpdateActionExecutor>();
            }

            executor.Execute(controller, onFinish);
            return action;
        }
    }
}
