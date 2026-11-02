using UnityEngine;

namespace YLFramework.Timing.Internal
{
    /// <summary>连接计时任务与 GameObject 生命周期监听器，仅持有被绑定的精确任务代次。</summary>
    internal sealed class TimerGameObjectBinding
    {
        private TimerHandle _handle;
        private TimerScheduler _scheduler;
        private GameObject _owner;
        private TimerLifetimeListener _listener;
        private readonly TimerGameObjectLifetime _lifetime;

        /// <summary>捕获任务句柄、所属调度器与对象监听器；两侧集合的登记由绑定入口负责。</summary>
        internal TimerGameObjectBinding(TimerHandle handle, GameObject owner,
            TimerGameObjectLifetime lifetime, TimerLifetimeListener listener)
        {
            _handle = handle;
            _scheduler = handle.Run.Owner;
            _owner = owner;
            _lifetime = lifetime;
            _listener = listener;
        }

        /// <summary>按销毁或停用策略取消捕获代次，旧快照不会接管复用后产生的新任务。</summary>
        internal void OwnerEnded(bool destroyed)
        {
            if (_scheduler != null && (destroyed || _lifetime == TimerGameObjectLifetime.CancelOnDisable))
            {
                _handle.Cancel();
            }
        }

        /// <summary>幂等解除调度器、监听器和任务之间的绑定引用，本操作不主动取消任务。</summary>
        internal void Detach()
        {
            if (_scheduler == null)
            {
                return;
            }

            _scheduler.RemoveBinding(_listener);
            // Remove 只操作托管集合；对象已 fake-null 时也必须释放旧监听责任。
            _listener.Remove(this);
            var run = _handle.Run;
            if (run != null && ReferenceEquals(run.Binding, this))
            {
                run.Binding = null;
            }

            _handle = default;
            _scheduler = null;
            _owner = null;
            _listener = null;
        }
    }
}
