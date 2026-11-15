using System;
using UnityEngine;

namespace YLFramework.Actions
{
    public static partial class IActionExtensions
    {
        /// <summary>在组件所属 GameObject 上启动匿名动作并返回安全执行租约。</summary>
        /// <param name="self">未被调度且未清理的动作。</param>
        /// <param name="owner">执行宿主所在的组件；停用组件不取消，销毁 GameObject 取消。</param>
        /// <param name="onFinish">正常完成且清理前通知；取消不通知。</param>
        /// <returns>同步完成时已失效的句柄；旧句柄不能控制复用的动作或控制器。</returns>
        public static ActionHandle StartHandle(this IAction self, MonoBehaviour owner, Action onFinish = null)
        {
            MonoUpdateActionExecutor.ValidateAction(self);
            if (!owner)
                throw new ArgumentNullException(nameof(owner));
            ActionKit.Initialize();
            return MonoUpdateActionExecutor.Find(owner.gameObject, true).StartAnonymous(self,
                onFinish == null ? (Action<IActionController>)null : _ => onFinish());
        }

        /// <summary>在当前场景宿主上启动匿名动作，场景卸载时取消。</summary>
        /// <param name="self">未被调度且未清理的动作。</param>
        /// <param name="onFinish">正常完成时通知，取消不通知。</param>
        /// <returns>只控制本次执行的安全句柄。</returns>
        /// <remarks>复用原场景宿主缓存规则；切换活动场景不迁移已有宿主。</remarks>
        public static ActionHandle StartHandleCurrentScene(this IAction self, Action onFinish = null)
        {
            return self.StartHandle(GetStartHost(self, false), onFinish);
        }

        /// <summary>在跨场景全局宿主上启动匿名动作，持续到结束、取消或 Shutdown。</summary>
        /// <param name="self">未被调度且未清理的动作。</param>
        /// <param name="onFinish">正常完成时通知，取消不通知。</param>
        /// <returns>只控制本次执行的安全句柄。</returns>
        /// <remarks>只在运行时主线程使用；宿主不可用时抛 InvalidOperationException。</remarks>
        public static ActionHandle StartHandleGlobal(this IAction self, Action onFinish = null)
        {
            return self.StartHandle(GetStartHost(self, true), onFinish);
        }

        /// <summary>在指定组件所属 GameObject 的 Update 执行器上接管动作，并立即推进一次。</summary>
        /// <param name="self">未被其他执行器持有的动作。</param>
        /// <param name="monoBehaviour">执行宿主；依附其 GameObject，而不是仅依附该组件。</param>
        /// <param name="onFinish">正常完成且清理前调用，取消或宿主销毁时不调用。</param>
        /// <returns>当前执行的控制器；完成或取消后会被回收，不是长期句柄。</returns>
        /// <remarks>
        /// 禁用 GameObject 会暂停更新，销毁 GameObject 会取消动作；禁用传入组件不停止执行器。
        /// 即时动作可能在返回前完成并回收，返回的控制器此时已失效。
        /// </remarks>
        public static IActionController Start(this IAction self, MonoBehaviour monoBehaviour,
            Action<IActionController> onFinish = null)
        {
            MonoUpdateActionExecutor.ValidateAction(self);
            if (!monoBehaviour)
                throw new ArgumentNullException(nameof(monoBehaviour));
            ActionKit.Initialize();
            var controller = ActionController.Allocate();
            var generation = ((ActionController)controller).Generation;
            controller.ActionId = self.ActionId;
            controller.Action = self;
            controller.UpdateMode = ActionUpdateModes.ScaledDeltaTime;
            try
            {
                monoBehaviour.ExecuteByUpdate(self, controller, onFinish);
            }
            catch
            {
                if (((ActionController)controller).Generation == generation)
                {
                    controller.Recycle();
                }

                throw;
            }
            return controller;
        }

        /// <summary>在组件所属 GameObject 上启动动作，正常完成时调用不带参数的回调。</summary>
        /// <param name="self">要启动的动作。</param>
        /// <param name="monoBehaviour">执行宿主所在的组件。</param>
        /// <param name="onFinish">正常完成回调，取消时不调用。</param>
        /// <returns>仅在本次执行期间有效的控制器。</returns>
        public static IActionController Start(this IAction self, MonoBehaviour monoBehaviour, Action onFinish)
        {
            return self.Start(monoBehaviour, onFinish == null ? (Action<IActionController>)null : _ => onFinish());
        }

        /// <summary>在模块场景宿主上启动动作，宿主随其所属场景卸载而销毁。</summary>
        /// <param name="self">要启动的动作。</param>
        /// <param name="onFinish">正常完成且清理前调用的回调。</param>
        /// <returns>仅在本次执行期间有效的控制器。</returns>
        /// <remarks>没有宿主时在当前活动场景创建；已有宿主按单例缓存复用，切换活动场景不会迁移它。</remarks>
        public static IActionController StartCurrentScene(this IAction self,
            Action<IActionController> onFinish = null)
        {
            return self.Start(GetStartHost(self, false), onFinish);
        }

        /// <summary>在现有或按需创建的场景宿主上启动动作，正常完成时调用不带参数的回调。</summary>
        /// <param name="self">要启动的动作。</param>
        /// <param name="onFinish">正常完成回调，取消时不调用。</param>
        public static IActionController StartCurrentScene(this IAction self, Action onFinish)
        {
            return self.Start(GetStartHost(self, false), onFinish);
        }

        /// <summary>在跨场景的模块全局宿主上启动动作，不依赖调用方对象的存活。</summary>
        /// <param name="self">要启动的动作。</param>
        /// <param name="onFinish">正常完成且清理前调用的回调。</param>
        /// <returns>仅在本次执行期间有效的控制器。</returns>
        /// <remarks>仅在运行时主线程使用；动作持续到完成、取消、Shutdown 或全局宿主销毁。</remarks>
        public static IActionController StartGlobal(this IAction self, Action<IActionController> onFinish = null)
        {
            return self.Start(GetStartHost(self, true), onFinish);
        }

        /// <summary>在跨场景全局宿主上启动动作，正常完成时调用不带参数的回调。</summary>
        /// <param name="self">要启动的动作。</param>
        /// <param name="onFinish">正常完成回调，取消时不调用。</param>
        public static IActionController StartGlobal(this IAction self, Action onFinish)
        {
            return self.Start(GetStartHost(self, true), onFinish);
        }

        private static MonoBehaviour GetStartHost(IAction action, bool global)
        {
            MonoUpdateActionExecutor.ValidateAction(action);
            MonoBehaviour host = global ? (MonoBehaviour)ActionGlobalHost.Instance : ActionKitCurrentScene.SceneComponent;
            if (!host)
                throw new InvalidOperationException("ActionKit start host is unavailable.");
            return host;
        }

        /// <summary>暂停控制器仍匹配的动作更新，不取消动作，也不阻止首次启动。</summary>
        /// <param name="self">当前执行的控制器。</param>
        public static void Pause(this IActionController self)
        {
            if (self.Action != null && self.ActionId == self.Action.ActionId)
            {
                self.Action.IsPaused = true;
            }
        }

        /// <summary>恢复控制器仍匹配的动作更新。</summary>
        /// <param name="self">当前执行的控制器。</param>
        public static void Resume(this IActionController self)
        {
            if (self.Action != null && self.ActionId == self.Action.ActionId)
            {
                self.Action.IsPaused = false;
            }
        }
    }
}
