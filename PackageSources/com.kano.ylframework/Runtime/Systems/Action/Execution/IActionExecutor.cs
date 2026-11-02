using System;

namespace YLFramework.Actions
{
    /// <summary>接收动作控制器并管理其执行的宿主扩展点。</summary>
    public interface IActionExecutor
    {
        /// <summary>接管控制器指向的动作，管理更新、正常完成通知和清理。</summary>
        /// <param name="controller">已绑定动作和匹配标识的控制器。</param>
        /// <param name="onFinish">正常完成且清理前调用的回调，取消时不调用。</param>
        void Execute(IActionController controller, Action<IActionController> onFinish = null);
    }

    /// <summary>供自定义执行器复用的单步动作推进逻辑。</summary>
    public static class IActionExecutorExtensions
    {
        /// <summary>推进控制器绑定的动作，正常完成后通知并清理动作。</summary>
        /// <param name="self">调用此辅助方法的执行器。</param>
        /// <param name="controller">待推进的控制器。</param>
        /// <param name="dt">本次时间增量，单位为秒。</param>
        /// <param name="onFinish">正常完成回调；取消或绑定失效时不调用。</param>
        /// <returns>任务已完成、取消或绑定失效时为 true，执行器应将其移出更新列表。</returns>
        /// <remarks>
        /// 此方法不回收控制器，也不选择时间源，仍由调用方管理。
        /// 完成回调异常会向外传播，但仍尝试清理当前动作；Execute 自身抛异常时不进入此完成清理分支。
        /// </remarks>
        public static bool UpdateAction(this IActionExecutor self, IActionController controller, float dt,
            Action<IActionController> onFinish = null)
        {
            var action = controller?.Action;
            if (action == null || action.ActionId != controller.ActionId || action.IsDeinitialized)
            {
                return true;
            }

            var id = action.ActionId;
            var generation = controller is ActionController concrete ? concrete.Generation : 0;
            // 推进可能取消动作、替换绑定或回收控制器：之后的完成通知与清理都要以本次标识和代次复核为前提。
            if (action.Execute(dt))
            {
                Exception callbackError = null;
                try
                {
                    if (!action.IsDeinitialized && action.ActionId == id && controller.ActionId == id &&
                        ReferenceEquals(controller.Action, action) &&
                        (!(controller is ActionController current) || current.Generation == generation))
                    {
                        onFinish?.Invoke(controller);
                    }
                }
                catch (Exception exception)
                {
                    callbackError = exception;
                    throw;
                }
                finally
                {
                    try
                    {
                        if (action.ActionId == id && !action.IsDeinitialized)
                        {
                            action.Deinit();
                        }
                    }
                    catch (Exception cleanupError)
                    {
                        if (callbackError == null)
                        {
                            throw;
                        }

                        callbackError.Data["ActionKit.CleanupException"] = callbackError.Data["ActionKit.CleanupException"] is Exception previous
                            ? new AggregateException(previous, cleanupError) : cleanupError;
                    }
                }
                return true;
            }

            return action.ActionId != id || action.IsDeinitialized;
        }
    }
}
