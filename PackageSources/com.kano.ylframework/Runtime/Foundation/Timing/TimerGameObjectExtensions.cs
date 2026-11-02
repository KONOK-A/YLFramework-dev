using System;
using UnityEngine;
using YLFramework.Timing.Internal;

namespace YLFramework.Timing
{
    /// <summary>GameObject 的计时取消边界；停用不是暂停，组件禁用不是 GameObject 停用。</summary>
    public enum TimerGameObjectLifetime
    {
        /// <summary>GameObject 停用或销毁时取消，重新启用不会恢复旧任务。</summary>
        CancelOnDisable,
        /// <summary>GameObject 销毁时取消，单纯停用不影响计时。</summary>
        CancelOnDestroy
    }

    /// <summary>为精确计时句柄附加 GameObject 生命周期取消责任，不改变任务所属调度器或名称域。</summary>
    public static class TimerGameObjectExtensions
    {
        /// <summary>
        /// 将当前活跃 run 绑定到对象。重绑先解除旧对象；终态/旧代次返回 false。
        /// 对象已销毁或停用策略下已 inactive 时取消当前 run 并返回 false，不执行成功回调。
        /// paused run 仍受绑定约束；手动内核的存活兜底随 Advance 执行。
        /// </summary>
        /// <remarks>内部监听组件被销毁时同样按销毁边界处理；取消通知的异常遵循所属调度器的策略。</remarks>
        public static bool BindTo(this TimerHandle handle, GameObject owner,
            TimerGameObjectLifetime lifetime = TimerGameObjectLifetime.CancelOnDisable)
        {
            if (lifetime != TimerGameObjectLifetime.CancelOnDisable && lifetime != TimerGameObjectLifetime.CancelOnDestroy)
            {
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            }

            if (!handle.IsActive)
            {
                return false;
            }

            var run = handle.Run;
            run.Binding?.Detach();
            if (!owner || (lifetime == TimerGameObjectLifetime.CancelOnDisable && !owner.activeInHierarchy))
            {
                handle.Cancel();
                return false;
            }
            var listener = owner.GetComponent<TimerLifetimeListener>();
            if (!listener)
            {
                listener = owner.AddComponent<TimerLifetimeListener>();
            }

            var binding = new TimerGameObjectBinding(handle, owner, lifetime, listener);
            run.Binding = binding;
            run.Owner.AddBinding(listener);
            listener.Add(binding);
            return true;
        }
    }
}
