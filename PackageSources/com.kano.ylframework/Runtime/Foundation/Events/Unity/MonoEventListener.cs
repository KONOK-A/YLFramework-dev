using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Events
{
    /// <summary>
    /// 将本对象的原生生命周期转发到 Inspector 事件，不持有全局状态。
    /// 需要观察 Awake/首次 OnEnable 时，应在 Prefab 或未激活对象上预先绑定。
    /// </summary>
    /// <remarks>只转发实际收到的消息，不提供监听解绑或逐回调异常隔离；运行时绑定的持有者自行管理其生命周期。</remarks>
    [AddComponentMenu("YLFramework/Events/Mono Event Listener")]
    public class MonoEventListener : MonoBehaviour
    {
        /// <summary>本组件收到 Awake 时的可序列化通知。</summary>
        public UnityEvent onAwake = new UnityEvent();
        /// <summary>本组件首次收到 Start 时的可序列化通知。</summary>
        public UnityEvent onStart = new UnityEvent();
        /// <summary>本组件每次收到 OnEnable 时的可序列化通知。</summary>
        public UnityEvent onEnable = new UnityEvent();
        /// <summary>本组件收到 OnDisable 时的可序列化通知。</summary>
        public UnityEvent onDisable = new UnityEvent();
        /// <summary>本组件收到 OnDestroy 时的可序列化通知。</summary>
        public UnityEvent onDestroy = new UnityEvent();

        /// <summary>在初始化消息中同步通知已预先绑定的监听，事件字段为空时跳过。</summary>
        private void Awake() => onAwake?.Invoke();
        /// <summary>在首次启动消息中同步通知启动监听。</summary>
        private void Start() => onStart?.Invoke();
        /// <summary>在每次启用消息中同步通知监听。</summary>
        private void OnEnable() => onEnable?.Invoke();
        /// <summary>在禁用消息中同步通知监听。</summary>
        private void OnDisable() => onDisable?.Invoke();
        /// <summary>在 Unity 实际发送销毁消息时同步通知监听。</summary>
        private void OnDestroy() => onDestroy?.Invoke();
    }
}
