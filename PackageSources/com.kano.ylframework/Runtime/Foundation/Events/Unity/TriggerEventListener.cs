using System;
using UnityEngine;
using UnityEngine.Events;
using YLFramework.Foundation;

namespace YLFramework.Events
{

    /// <summary>可序列化的 3D 触发事件，向监听传入对方 Collider。</summary>
    [Serializable]
    public class TriggerEvent : UnityEvent<Collider>
    {
    }
    /// <summary>可序列化的 2D 触发事件，向监听传入对方 Collider2D。</summary>
    [Serializable]
    public class TriggerEvent2D : UnityEvent<Collider2D>
    {
    }

    /// <summary>按对方层将 Unity 的 3D/2D 触发消息同步转发给可序列化事件。</summary>
    /// <remarks>
    /// 每条消息独立过滤，不配对补发进入或离开通知；改变层过滤可能使监听收不到对应的离开通知。
    /// 不额外隔离回调异常或管理代码监听的解绑，绑定方应按自己的生命周期移除监听。
    /// </remarks>
    [AddComponentMenu("YLFramework/Events/Trigger Event Listener")]
    public class TriggerEventListener : MonoBehaviour
    {

        [SerializeField]
        [Tooltip("过滤对方 GameObject 的层；3D/2D 共用，None 不接收任何 Trigger。")]
        private LayerMask layerMask = Physics.AllLayers;

        /// <summary>通过层过滤后的 3D 触发进入通知。</summary>
        public TriggerEvent onTriggerEnter = new TriggerEvent();
        /// <summary>通过层过滤后的 3D 触发持续通知。</summary>
        public TriggerEvent onTriggerStay = new TriggerEvent();
        /// <summary>通过层过滤后的 3D 触发离开通知。</summary>
        public TriggerEvent onTriggerExit = new TriggerEvent();
        /// <summary>通过层过滤后的 2D 触发进入通知。</summary>
        public TriggerEvent2D onTriggerEnter2D = new TriggerEvent2D();
        /// <summary>通过层过滤后的 2D 触发持续通知。</summary>
        public TriggerEvent2D onTriggerStay2D = new TriggerEvent2D();
        /// <summary>通过层过滤后的 2D 触发离开通知。</summary>
        public TriggerEvent2D onTriggerExit2D = new TriggerEvent2D();

        /// <summary>对方物理对象的层过滤；不影响 Unity 原生 Trigger 判定。</summary>
        public LayerMask PhysicsLayerMask
        {
            get => layerMask; set => layerMask = value;
        }


        /// <summary>在 Unity 重置组件时恢复接收所有层的触发消息。</summary>
        private void Reset()
        {
            layerMask = Physics.AllLayers;
        }

        /// <summary>筛选 3D 触发进入消息，并将对方碰撞体传给对应监听。</summary>
        private void OnTriggerEnter(Collider other)
        {
            if (!layerMask.Contains(other.gameObject.layer))
            {
                return;
            }

            onTriggerEnter?.Invoke(other);
        }

        /// <summary>筛选 3D 持续触发消息，并将对方碰撞体传给对应监听。</summary>
        private void OnTriggerStay(Collider other)
        {
            if (!layerMask.Contains(other.gameObject.layer))
            {
                return;
            }

            onTriggerStay?.Invoke(other);
        }

        /// <summary>筛选 3D 触发离开消息，只有对方当前层仍匹配时才通知监听。</summary>
        private void OnTriggerExit(Collider other)
        {
            if (!layerMask.Contains(other.gameObject.layer))
            {
                return;
            }

            onTriggerExit?.Invoke(other);
        }

        /// <summary>筛选 2D 触发进入消息，并将对方碰撞体传给对应监听。</summary>
        private void OnTriggerEnter2D(Collider2D collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onTriggerEnter2D?.Invoke(collision);
        }

        /// <summary>筛选 2D 持续触发消息，并将对方碰撞体传给对应监听。</summary>
        private void OnTriggerStay2D(Collider2D collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onTriggerStay2D?.Invoke(collision);
        }

        /// <summary>筛选 2D 触发离开消息，只有对方当前层仍匹配时才通知监听。</summary>
        private void OnTriggerExit2D(Collider2D collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onTriggerExit2D?.Invoke(collision);
        }


    }
}
