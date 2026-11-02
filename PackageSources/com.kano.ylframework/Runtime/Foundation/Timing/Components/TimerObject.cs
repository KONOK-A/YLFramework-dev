using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

namespace YLFramework.Timing
{
    /// <summary>
    /// Inspector 计时组件，消费公共 Timing 服务；启用启动一轮，组件或 GameObject 停用取消本轮。
    /// onCompleted 仅在正常到期时调用，onAborted 仅在取消或 owner 失效时调用，故障不视为中断。
    /// </summary>
    public class TimerObject : MonoBehaviour
    {
        /// <summary>每次启用时登记的等待秒数，须为有限非负值；修改不影响已经登记的本轮。</summary>
        [Tooltip("延迟时间")]
        public float delay = 2;

        /// <summary>是否使用 Unscaled 秒域；仅在启动本轮时读取。</summary>
        [Tooltip("忽略时间缩放")]
        public bool ignoreTimeScale = true;

        /// <summary>本轮正常到期时同步调用的事件，异常交由调度器报告。</summary>
        [Tooltip("事件回调")]
        [FormerlySerializedAs("Action")] public UnityEvent onCompleted;

        /// <summary>本轮被取消或所属调度器失效时同步调用的事件；正常完成和故障不触发。</summary>
        [Tooltip("中断回调")]
        [FormerlySerializedAs("Abort")] public UnityEvent onAborted;

        private ReusableTimer _timer;
        private TimerHandle _current;
        private System.Action _onAction;
        private System.Action<TimerCancellationReason> _onCancelled;
        private bool _isDestroyed;

        /// <summary>复用本组件的存储启动新一轮等待，并绑定 GameObject 的停用取消边界。</summary>
        private void OnEnable()
        {
            if (_isDestroyed)
            {
                return;
            }

            _timer ??= new ReusableTimer();
            _onAction ??= OnAction;
            _onCancelled ??= OnCancelled;
            _current = _timer.Start(delay, _onAction,
                ignoreTimeScale ? TimerClock.Unscaled : TimerClock.Scaled, _onCancelled);
            _current.BindTo(gameObject);
        }

        /// <summary>将本轮到期通知同步转发给 Inspector 配置的完成事件。</summary>
        private void OnAction()
        {
            onCompleted?.Invoke();
        }

        /// <summary>将取消或调度器失效通知统一转发给中断事件，不区分传入原因。</summary>
        private void OnCancelled(TimerCancellationReason reason)
        {
            onAborted?.Invoke();
        }

        /// <summary>组件或 GameObject 停用时取消当前责任，不暂停或自动保留本轮。</summary>
        private void OnDisable() => _current.Cancel();

        /// <summary>先禁止再次启用启动，再关闭复用槽并清空本组件持有的句柄与回调引用。</summary>
        private void OnDestroy()
        {
            _isDestroyed = true;
            _timer?.Dispose();
            _timer = null;
            _current = default;
            _onAction = null;
            _onCancelled = null;
        }

    }
}
