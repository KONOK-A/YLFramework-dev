using System;
using UnityEngine;
using UnityEngine.Events;


namespace YLFramework.Events
{
    /// <summary>向监听传递动画事件名称的可序列化 UnityEvent。</summary>
    [Serializable]
    public class AnimationEvent : UnityEvent<string>
    {
    }

    /// <summary>承接动画事件入口，并把传入名称同步转发给 Inspector 或代码绑定的监听。</summary>
    /// <remarks>不提供名称路由、逐回调异常隔离或自动解绑；运行时添加监听的持有者自行管理取消。</remarks>
    [AddComponentMenu("YLFramework/Events/Animation Event Listener")]
    public class AnimationEventListener : MonoBehaviour
    {
        /// <summary>接收动画事件名称的通知，可在 Inspector 配置或由代码绑定。</summary>
        [Tooltip("由 AnimationClip 的 OnAnimationEvent(string) 事件传入名称；不监听 Animator 状态变化。")]
        public AnimationEvent animationEvent = new AnimationEvent();

        /// <summary>原样同步传递本次动画事件名称；通知字段为空时跳过。</summary>
        /// <param name="name">动画事件传入的名称，不在此处解析或校验。</param>
        public void OnAnimationEvent(string name)
        {
            animationEvent?.Invoke(name);
        }
    }
}
