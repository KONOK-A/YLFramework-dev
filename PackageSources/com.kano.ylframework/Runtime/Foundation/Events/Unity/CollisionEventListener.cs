using System;
using UnityEngine;
using UnityEngine.Events;
using YLFramework.Foundation;


namespace YLFramework.Events
{

    /// <summary>可序列化的 3D 碰撞事件，向监听传入 Unity 的 Collision 数据。</summary>
    [Serializable]
    public class CollisionEvent : UnityEvent<Collision>
    {
    }
    /// <summary>可序列化的 2D 碰撞事件，向监听传入 Unity 的 Collision2D 数据。</summary>
    [Serializable]
    public class CollisionEvent2D : UnityEvent<Collision2D>
    {
    }

    /// <summary>按对方层将 Unity 的 3D/2D 碰撞消息同步转发给可序列化事件。</summary>
    /// <remarks>
    /// 每条消息独立过滤，不配对补发进入或离开通知；改变层过滤可能使监听收不到对应的离开通知。
    /// 不额外隔离回调异常或管理代码监听的解绑，绑定方应按自己的生命周期移除监听。
    /// </remarks>
    [AddComponentMenu("YLFramework/Events/Collision Event Listener")]
    public class CollisionEventListener : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("过滤对方 GameObject 的层；3D/2D 共用，None 不接收任何碰撞。")]
        private LayerMask layerMask = Physics.AllLayers;
        /// <summary>通过层过滤后的 3D 碰撞进入通知。</summary>
        public CollisionEvent onCollisionEnter = new CollisionEvent();
        /// <summary>通过层过滤后的 3D 碰撞持续通知。</summary>
        public CollisionEvent onCollisionStay = new CollisionEvent();
        /// <summary>通过层过滤后的 3D 碰撞离开通知。</summary>
        public CollisionEvent onCollisionExit = new CollisionEvent();
        /// <summary>通过层过滤后的 2D 碰撞进入通知。</summary>
        public CollisionEvent2D onCollision2DEnter = new CollisionEvent2D();
        /// <summary>通过层过滤后的 2D 碰撞持续通知。</summary>
        public CollisionEvent2D onCollision2DStay = new CollisionEvent2D();
        /// <summary>通过层过滤后的 2D 碰撞离开通知。</summary>
        public CollisionEvent2D onCollision2DExit = new CollisionEvent2D();

        /// <summary>对方物理对象的层过滤；不影响 Unity 原生碰撞判定。</summary>
        public LayerMask PhysicsLayerMask
        {
            get => layerMask; set => layerMask = value;
        }



        /// <summary>在 Unity 重置组件时恢复接收所有层的碰撞消息。</summary>
        private void Reset()
        {
            layerMask = Physics.AllLayers;
        }



        /// <summary>筛选 3D 碰撞进入消息，并把原始碰撞数据传给对应监听。</summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onCollisionEnter?.Invoke(collision);
        }


        /// <summary>筛选 3D 持续碰撞消息，并把原始碰撞数据传给对应监听。</summary>
        private void OnCollisionStay(Collision collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onCollisionStay?.Invoke(collision);
        }

        /// <summary>筛选 3D 碰撞离开消息，只有对方当前层仍匹配时才通知监听。</summary>
        private void OnCollisionExit(Collision collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onCollisionExit?.Invoke(collision);
        }

        /// <summary>筛选 2D 碰撞进入消息，并把原始碰撞数据传给对应监听。</summary>
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onCollision2DEnter?.Invoke(collision);
        }

        /// <summary>筛选 2D 持续碰撞消息，并把原始碰撞数据传给对应监听。</summary>
        private void OnCollisionStay2D(Collision2D collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onCollision2DStay?.Invoke(collision);
        }

        /// <summary>筛选 2D 碰撞离开消息，只有对方当前层仍匹配时才通知监听。</summary>
        private void OnCollisionExit2D(Collision2D collision)
        {
            if (!layerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            onCollision2DExit?.Invoke(collision);
        }

    }

}
