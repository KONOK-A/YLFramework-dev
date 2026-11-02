using System;
using UnityEngine;

namespace YLFramework.Actions
{
    public static partial class IActionExtensions
    {
        /// <summary>在组件所属 GameObject 上命名启动根动作，首次同步推进前登记名字。</summary>
        /// <param name="self">未被执行器持有且未清理的动作；重名拒绝不会消费此动作。</param>
        /// <param name="monoBehaviour">执行宿主组件，作用域为其 GameObject。</param>
        /// <param name="flag">作用域内 ordinal、区分大小写的非空白名字。</param>
        /// <param name="onFinish">正常完成通知；调用前名字已释放，取消时不调用。</param>
        /// <returns>安全执行句柄；同步完成时返回的句柄已无效。</returns>
        /// <exception cref="System.ArgumentNullException">monoBehaviour 为 null。</exception>
        /// <exception cref="System.ArgumentException">flag 为空或仅含空白。</exception>
        /// <exception cref="System.InvalidOperationException">模块正在关停、动作已被其他执行器持有或已清理，或名字已被占用。</exception>
        public static ActionHandle StartNamed(this IAction self, MonoBehaviour monoBehaviour, string flag,
            Action<IActionController> onFinish = null) => StartNamedOn(self, monoBehaviour, flag, false, onFinish);

        /// <summary>在组件所属 GameObject 上命名启动，使用无参数的正常完成通知。</summary>
        /// <param name="self">要启动的根动作。</param>
        /// <param name="monoBehaviour">执行宿主组件。</param>
        /// <param name="flag">区分大小写的非空白名字。</param>
        /// <param name="onFinish">正常完成且名字释放后调用的通知。</param>
        /// <returns>仅控制本次租约的安全句柄。</returns>
        public static ActionHandle StartNamed(this IAction self, MonoBehaviour monoBehaviour, string flag,
            Action onFinish) => self.StartNamed(monoBehaviour, flag,
            onFinish == null ? (Action<IActionController>)null : _ => onFinish());

        /// <summary>先验证新动作，再取消对象作用域的旧同名执行并启动新动作；失败不恢复旧任务。</summary>
        /// <param name="self">未被执行器持有且未清理的新根动作。</param>
        /// <param name="monoBehaviour">执行宿主组件，作用域为其 GameObject。</param>
        /// <param name="flag">区分大小写的非空白名字。</param>
        /// <param name="onFinish">仅新动作正常完成时调用，旧任务取消不触发成功通知。</param>
        /// <returns>新执行的安全句柄；替换不是事务性回滚。</returns>
        /// <exception cref="System.ArgumentNullException">monoBehaviour 为 null。</exception>
        /// <exception cref="System.ArgumentException">flag 为空或仅含空白。</exception>
        /// <exception cref="System.InvalidOperationException">模块正在关停、动作已被其他执行器持有或已清理，或取消旧任务时同名登记被重入替换。</exception>
        public static ActionHandle ReplaceNamed(this IAction self, MonoBehaviour monoBehaviour, string flag,
            Action<IActionController> onFinish = null) => StartNamedOn(self, monoBehaviour, flag, true, onFinish);

        /// <summary>显式替换对象作用域的同名执行，使用无参数的正常完成通知。</summary>
        /// <param name="self">新根动作。</param>
        /// <param name="monoBehaviour">执行宿主组件。</param>
        /// <param name="flag">区分大小写的非空白名字。</param>
        /// <param name="onFinish">仅新任务成功时调用。</param>
        /// <returns>新执行的安全句柄，失败不恢复旧任务。</returns>
        public static ActionHandle ReplaceNamed(this IAction self, MonoBehaviour monoBehaviour, string flag,
            Action onFinish) => self.ReplaceNamed(monoBehaviour, flag,
            onFinish == null ? (Action<IActionController>)null : _ => onFinish());

        /// <summary>在当前场景宿主作用域中命名启动；宿主不存在时按需创建，宿主所属场景卸载时取消。</summary>
        /// <param name="self">要启动的根动作。</param>
        /// <param name="flag">区分大小写的非空白名字。</param>
        /// <param name="onFinish">正常完成且名字释放后调用的通知。</param>
        /// <returns>仅控制本次执行租约的安全句柄。</returns>
        /// <exception cref="System.ArgumentNullException">场景宿主当前不可用（如应用退出期间）。</exception>
        /// <exception cref="System.ArgumentException">flag 为空或仅含空白。</exception>
        /// <exception cref="System.InvalidOperationException">模块正在关停、动作已被其他执行器持有或已清理，或名字已被占用。</exception>
        public static ActionHandle StartNamedCurrentScene(this IAction self, string flag,
            Action<IActionController> onFinish = null)
        {
            ValidateNamedStart(self, flag);
            return self.StartNamed(ActionKitCurrentScene.SceneComponent, flag, onFinish);
        }

        /// <summary>在当前场景宿主上命名启动，使用无参数正常完成通知。</summary>
        /// <param name="self">要启动的根动作。</param>
        /// <param name="flag">非空白名字。</param>
        /// <param name="onFinish">正常完成通知。</param>
        /// <returns>本次执行的安全句柄。</returns>
        public static ActionHandle StartNamedCurrentScene(this IAction self, string flag, Action onFinish) =>
            self.StartNamedCurrentScene(flag, onFinish == null ? (Action<IActionController>)null : _ => onFinish());

        /// <summary>验证后显式替换当前场景宿主上的同名执行，失败不恢复旧任务。</summary>
        /// <param name="self">新根动作。</param>
        /// <param name="flag">非空白名字。</param>
        /// <param name="onFinish">仅新任务成功时调用。</param>
        /// <returns>新执行的安全句柄。</returns>
        /// <exception cref="System.ArgumentNullException">场景宿主当前不可用（如应用退出期间）。</exception>
        /// <exception cref="System.ArgumentException">flag 为空或仅含空白。</exception>
        /// <exception cref="System.InvalidOperationException">模块正在关停、动作已被其他执行器持有或已清理，或取消旧任务时同名登记被重入替换。</exception>
        public static ActionHandle ReplaceNamedCurrentScene(this IAction self, string flag,
            Action<IActionController> onFinish = null)
        {
            ValidateNamedStart(self, flag);
            return self.ReplaceNamed(ActionKitCurrentScene.SceneComponent, flag, onFinish);
        }

        /// <summary>显式替换场景同名执行，使用无参数正常完成通知。</summary>
        /// <param name="self">新根动作。</param>
        /// <param name="flag">非空白名字。</param>
        /// <param name="onFinish">仅新任务成功时调用。</param>
        /// <returns>新执行的安全句柄，失败不恢复旧任务。</returns>
        public static ActionHandle ReplaceNamedCurrentScene(this IAction self, string flag, Action onFinish) =>
            self.ReplaceNamedCurrentScene(flag, onFinish == null ? (Action<IActionController>)null : _ => onFinish());

        /// <summary>在跨场景全局宿主作用域中命名启动，不依附调用方对象。</summary>
        /// <param name="self">要启动的根动作。</param>
        /// <param name="flag">非空白名字，与对象、场景及 Timer 名字互相独立。</param>
        /// <param name="onFinish">正常完成且名字释放后调用的通知。</param>
        /// <returns>仅控制本次执行租约的安全句柄。</returns>
        /// <exception cref="System.ArgumentNullException">全局宿主当前不可用（如非播放模式）。</exception>
        /// <exception cref="System.ArgumentException">flag 为空或仅含空白。</exception>
        /// <exception cref="System.InvalidOperationException">模块正在关停、动作已被其他执行器持有或已清理，或名字已被占用。</exception>
        public static ActionHandle StartNamedGlobal(this IAction self, string flag,
            Action<IActionController> onFinish = null)
        {
            ValidateNamedStart(self, flag);
            return self.StartNamed(ActionGlobalHost.Instance, flag, onFinish);
        }

        /// <summary>在全局宿主上命名启动，使用无参数正常完成通知。</summary>
        /// <param name="self">要启动的根动作。</param>
        /// <param name="flag">非空白名字。</param>
        /// <param name="onFinish">正常完成通知。</param>
        /// <returns>本次执行的安全句柄。</returns>
        public static ActionHandle StartNamedGlobal(this IAction self, string flag, Action onFinish) =>
            self.StartNamedGlobal(flag, onFinish == null ? (Action<IActionController>)null : _ => onFinish());

        /// <summary>验证后显式替换全局同名执行，旧任务取消不触发成功通知，失败不恢复旧任务。</summary>
        /// <param name="self">新根动作。</param>
        /// <param name="flag">非空白名字。</param>
        /// <param name="onFinish">仅新任务成功时调用。</param>
        /// <returns>新执行的安全句柄。</returns>
        /// <exception cref="System.ArgumentNullException">全局宿主当前不可用（如非播放模式）。</exception>
        /// <exception cref="System.ArgumentException">flag 为空或仅含空白。</exception>
        /// <exception cref="System.InvalidOperationException">模块正在关停、动作已被其他执行器持有或已清理，或取消旧任务时同名登记被重入替换。</exception>
        public static ActionHandle ReplaceNamedGlobal(this IAction self, string flag,
            Action<IActionController> onFinish = null)
        {
            ValidateNamedStart(self, flag);
            return self.ReplaceNamed(ActionGlobalHost.Instance, flag, onFinish);
        }

        /// <summary>显式替换全局同名执行，使用无参数正常完成通知。</summary>
        /// <param name="self">新根动作。</param>
        /// <param name="flag">非空白名字。</param>
        /// <param name="onFinish">仅新任务成功时调用。</param>
        /// <returns>新执行的安全句柄，失败不恢复旧任务。</returns>
        public static ActionHandle ReplaceNamedGlobal(this IAction self, string flag, Action onFinish) =>
            self.ReplaceNamedGlobal(flag, onFinish == null ? (Action<IActionController>)null : _ => onFinish());

        /// <summary>在获取宿主前校验名字与动作，避免无效请求创建执行设施。</summary>
        private static void ValidateNamedStart(IAction action, string flag)
        {
            MonoUpdateActionExecutor.ValidateFlag(flag);
            MonoUpdateActionExecutor.ValidateAction(action);
        }

        /// <summary>校验宿主与动作后初始化模块，在宿主 GameObject 上登记命名根执行。</summary>
        private static ActionHandle StartNamedOn(IAction action, MonoBehaviour owner, string flag, bool replace,
            Action<IActionController> onFinish)
        {
            ValidateNamedStart(action, flag);
            if (!owner)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            ActionKit.Initialize();
            return MonoUpdateActionExecutor.Find(owner.gameObject, true).StartNamed(action, flag, replace, onFinish);
        }
    }

    public partial class ActionKit
    {
        /// <summary>查询组件所属 GameObject 的命名根执行，不创建宿主、执行器或初始化模块。</summary>
        /// <param name="owner">作用域所在组件；不存在时返回 false。</param>
        /// <param name="flag">区分大小写的非空白名字。</param>
        /// <param name="handle">查到的安全租约句柄；未找到时为默认值。</param>
        /// <returns>存在有效命名执行时为 true；已结束登记会被剔除。</returns>
        public static bool TryGetNamed(MonoBehaviour owner, string flag, out ActionHandle handle)
        {
            MonoUpdateActionExecutor.ValidateFlag(flag);
            handle = default;
            var executor = owner ? MonoUpdateActionExecutor.Find(owner.gameObject) : null;
            return executor && executor.TryGetNamed(flag, out handle);
        }

        /// <summary>非创建式查询对象作用域是否有有效同名执行。</summary>
        public static bool ContainsNamed(MonoBehaviour owner, string flag) => TryGetNamed(owner, flag, out _);
        /// <summary>非创建式取消对象作用域的当前同名执行；不存在时返回 false，不触发成功回调。</summary>
        public static bool CancelNamed(MonoBehaviour owner, string flag) => TryGetNamed(owner, flag, out var handle) && handle.Cancel();
        /// <summary>非创建式暂停对象作用域的当前同名执行；不存在时返回 false。</summary>
        public static bool PauseNamed(MonoBehaviour owner, string flag) => TryGetNamed(owner, flag, out var handle) && handle.Pause();
        /// <summary>非创建式恢复对象作用域的当前同名执行；不存在时返回 false。</summary>
        public static bool ResumeNamed(MonoBehaviour owner, string flag) => TryGetNamed(owner, flag, out var handle) && handle.Resume();

        /// <summary>查询现有当前场景宿主的命名执行，不创建场景宿主或执行器。</summary>
        /// <param name="flag">区分大小写的非空白名字。</param>
        /// <param name="handle">有效句柄；未找到时为默认值。</param>
        /// <returns>现有场景宿主上存在有效命名执行时为 true。</returns>
        public static bool TryGetNamedCurrentScene(string flag, out ActionHandle handle)
        {
            ActionKitCurrentScene.TryGetInstance(out var host);
            return TryGetNamed(host, flag, out handle);
        }

        /// <summary>非创建式查询场景宿主上的有效同名执行。</summary>
        public static bool ContainsNamedCurrentScene(string flag) => TryGetNamedCurrentScene(flag, out _);
        /// <summary>非创建式取消场景同名执行；不存在时返回 false，不触发成功回调。</summary>
        public static bool CancelNamedCurrentScene(string flag) => TryGetNamedCurrentScene(flag, out var handle) && handle.Cancel();
        /// <summary>非创建式暂停场景同名执行；不存在时返回 false。</summary>
        public static bool PauseNamedCurrentScene(string flag) => TryGetNamedCurrentScene(flag, out var handle) && handle.Pause();
        /// <summary>非创建式恢复场景同名执行；不存在时返回 false。</summary>
        public static bool ResumeNamedCurrentScene(string flag) => TryGetNamedCurrentScene(flag, out var handle) && handle.Resume();

        /// <summary>查询现有跨场景全局宿主的命名执行，不创建全局宿主或执行器。</summary>
        /// <param name="flag">区分大小写的非空白名字。</param>
        /// <param name="handle">有效句柄；未找到时为默认值。</param>
        /// <returns>现有全局宿主上存在有效命名执行时为 true。</returns>
        public static bool TryGetNamedGlobal(string flag, out ActionHandle handle)
        {
            ActionGlobalHost.TryGetInstance(out var host);
            return TryGetNamed(host, flag, out handle);
        }

        /// <summary>非创建式查询全局宿主上的有效同名执行。</summary>
        public static bool ContainsNamedGlobal(string flag) => TryGetNamedGlobal(flag, out _);
        /// <summary>非创建式取消全局同名执行；不存在时返回 false，不触发成功回调。</summary>
        public static bool CancelNamedGlobal(string flag) => TryGetNamedGlobal(flag, out var handle) && handle.Cancel();
        /// <summary>非创建式暂停全局同名执行；不存在时返回 false。</summary>
        public static bool PauseNamedGlobal(string flag) => TryGetNamedGlobal(flag, out var handle) && handle.Pause();
        /// <summary>非创建式恢复全局同名执行；不存在时返回 false。</summary>
        public static bool ResumeNamedGlobal(string flag) => TryGetNamedGlobal(flag, out var handle) && handle.Resume();
    }
}
