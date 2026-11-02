using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YLFramework.Events
{
    /// <summary>
    /// <see cref="EventListener"/> 的便捷扩展：按 EventType 注册 / 注销鼠标、碰撞与 Trigger 回调，
    /// 注册类方法统一返回 <see cref="IUnregister"/>；注销类方法在组件不存在时为空操作，不会自动创建组件。
    /// </summary>
    /// <remarks>
    /// 重复委托分别注册，调用方需保存句柄或显式注销；禁用组件不会自动解绑。
    /// 此处的取消只作用于代码通道，不修改 Inspector 中的 UnityEvent；分发时序与异常规则由 EventListener 决定。
    /// </remarks>
    public static class EventListenerExtension
    {
        #region 工具方法

        /// <summary>
        /// 注册一个事件到身上的 EventListener 组件（不存在时自动添加），返回注销句柄。
        /// </summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="selfComponent">目标组件。</param>
        /// <param name="eventType">事件类型。</param>
        /// <param name="action">事件监听回调。</param>
        /// <returns>用于注销本次注册的句柄。</returns>
        public static IUnregister RegisterEvent<T>(this Component selfComponent, EventType eventType, Action<T> action)
        {
            var owner = selfComponent.gameObject;
            var listener = owner.GetComponent<EventListener>();
            if (!listener)
            {
                listener = owner.AddComponent<EventListener>();
            }
            return listener.Register(eventType, action);
        }

        /// <summary>
        /// 从身上的 EventListener 组件上注销某一个事件。
        /// </summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="selfComponent">目标组件。</param>
        /// <param name="eventType">事件类型。</param>
        /// <param name="action">要注销的监听回调。</param>
        public static void UnregisterEvent<T>(this Component selfComponent, EventType eventType, Action<T> action)
        {
            var listener = selfComponent.GetComponent<EventListener>();
            if (listener != null)
            {
                listener.Unregister(eventType, action);
            }
        }

        /// <summary>
        /// 从身上的 EventListener 组件上注销某一类事件的全部监听。
        /// </summary>
        /// <param name="selfComponent">目标组件。</param>
        /// <param name="eventType">事件类型。</param>
        public static void UnregisterEvent(this Component selfComponent, EventType eventType)
        {
            var listener = selfComponent.GetComponent<EventListener>();
            if (listener != null)
            {
                listener.Unregister(eventType);
            }
        }

        /// <summary>
        /// 注销身上的 EventListener 组件上的所有代码事件。
        /// </summary>
        /// <param name="selfComponent">目标组件。</param>
        /// <param name="eventType">历史参数，已忽略。</param>
        public static void RemoveAllEvent(this Component selfComponent, EventType eventType)
        {
            RemoveAllEvent(selfComponent);
        }

        /// <summary>
        /// 注销身上的 EventListener 组件上的所有代码事件。
        /// </summary>
        /// <param name="selfComponent">目标组件。</param>
        public static void RemoveAllEvent(this Component selfComponent)
        {
            var listener = selfComponent.GetComponent<EventListener>();
            if (listener != null)
            {
                listener.UnregisterAll();
            }
        }

        #endregion

        #region 鼠标相关事件

        /// <summary>注册指针进入回调。</summary>
        public static IUnregister OnMouseEnter(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnMouseEnter, action);
        }

        /// <summary>注册指针离开回调。</summary>
        public static IUnregister OnMouseExit(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnMouseExit, action);
        }

        /// <summary>注册指针点击回调。</summary>
        public static IUnregister OnClick(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnClick, action);
        }

        /// <summary>注册指针按下回调。</summary>
        public static IUnregister OnClickDown(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnClickDown, action);
        }

        /// <summary>注册指针抬起回调。</summary>
        public static IUnregister OnClickUp(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnClickUp, action);
        }

        /// <summary>注册拖拽中回调。</summary>
        public static IUnregister OnDrag(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnDrag, action);
        }

        /// <summary>注册开始拖拽回调。</summary>
        public static IUnregister OnBeginDrag(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnBeginDrag, action);
        }

        /// <summary>注册结束拖拽回调。</summary>
        public static IUnregister OnEndDrag(this Component com, Action<PointerEventData> action)
        {
            return RegisterEvent(com, EventType.OnEndDrag, action);
        }

        /// <summary>注销全部指针点击回调，不包含指针按下或抬起回调。</summary>
        public static void RemoveClick(this Component com)
        {
            UnregisterEvent(com, EventType.OnClick);
        }

        /// <summary>注销全部指针按下回调。</summary>
        public static void RemoveClickDown(this Component com)
        {
            UnregisterEvent(com, EventType.OnClickDown);
        }

        /// <summary>注销全部指针抬起回调。</summary>
        public static void RemoveClickUp(this Component com)
        {
            UnregisterEvent(com, EventType.OnClickUp);
        }

        /// <summary>注销全部拖拽中回调。</summary>
        public static void RemoveDrag(this Component com)
        {
            UnregisterEvent(com, EventType.OnDrag);
        }

        /// <summary>注销全部开始拖拽回调。</summary>
        public static void RemoveBeginDrag(this Component com)
        {
            UnregisterEvent(com, EventType.OnBeginDrag);
        }

        /// <summary>注销全部结束拖拽回调。</summary>
        public static void RemoveEndDrag(this Component com)
        {
            UnregisterEvent(com, EventType.OnEndDrag);
        }

        /// <summary>注销指定的指针点击回调。</summary>
        public static void RemoveClick(this Component com, Action<PointerEventData> action)
        {
            UnregisterEvent(com, EventType.OnClick, action);
        }

        /// <summary>注销指定的指针按下回调。</summary>
        public static void RemoveClickDown(this Component com, Action<PointerEventData> action)
        {
            UnregisterEvent(com, EventType.OnClickDown, action);
        }

        /// <summary>注销指定的指针抬起回调。</summary>
        public static void RemoveClickUp(this Component com, Action<PointerEventData> action)
        {
            UnregisterEvent(com, EventType.OnClickUp, action);
        }

        /// <summary>注销指定的拖拽中回调。</summary>
        public static void RemoveDrag(this Component com, Action<PointerEventData> action)
        {
            UnregisterEvent(com, EventType.OnDrag, action);
        }

        /// <summary>注销指定的开始拖拽回调。</summary>
        public static void RemoveBeginDrag(this Component com, Action<PointerEventData> action)
        {
            UnregisterEvent(com, EventType.OnBeginDrag, action);
        }

        /// <summary>注销指定的结束拖拽回调。</summary>
        public static void RemoveEndDrag(this Component com, Action<PointerEventData> action)
        {
            UnregisterEvent(com, EventType.OnEndDrag, action);
        }

        #endregion

        #region 碰撞相关事件

        /// <summary>注册 3D 碰撞进入回调。</summary>
        public static IUnregister OnCollisionEnter(this Component com, Action<Collision> action)
        {
            return RegisterEvent(com, EventType.OnCollisionEnter, action);
        }

        /// <summary>注册 3D 碰撞保持回调。</summary>
        public static IUnregister OnCollisionStay(this Component com, Action<Collision> action)
        {
            return RegisterEvent(com, EventType.OnCollisionStay, action);
        }

        /// <summary>注册 3D 碰撞离开回调。</summary>
        public static IUnregister OnCollisionExit(this Component com, Action<Collision> action)
        {
            return RegisterEvent(com, EventType.OnCollisionExit, action);
        }

        /// <summary>注册 2D 碰撞进入回调。</summary>
        public static IUnregister OnCollisionEnter2D(this Component com, Action<Collision2D> action)
        {
            return RegisterEvent(com, EventType.OnCollisionEnter2D, action);
        }

        /// <summary>注册 2D 碰撞保持回调。</summary>
        public static IUnregister OnCollisionStay2D(this Component com, Action<Collision2D> action)
        {
            return RegisterEvent(com, EventType.OnCollisionStay2D, action);
        }

        /// <summary>注册 2D 碰撞离开回调。</summary>
        public static IUnregister OnCollisionExit2D(this Component com, Action<Collision2D> action)
        {
            return RegisterEvent(com, EventType.OnCollisionExit2D, action);
        }

        /// <summary>注销指定的 3D 碰撞进入回调。</summary>
        public static void RemoveCollisionEnter(this Component com, Action<Collision> action)
        {
            UnregisterEvent(com, EventType.OnCollisionEnter, action);
        }

        /// <summary>注销指定的 3D 碰撞保持回调。</summary>
        public static void RemoveCollisionStay(this Component com, Action<Collision> action)
        {
            UnregisterEvent(com, EventType.OnCollisionStay, action);
        }

        /// <summary>注销指定的 3D 碰撞离开回调。</summary>
        public static void RemoveCollisionExit(this Component com, Action<Collision> action)
        {
            UnregisterEvent(com, EventType.OnCollisionExit, action);
        }

        /// <summary>注销指定的 2D 碰撞进入回调。</summary>
        public static void RemoveCollisionEnter2D(this Component com, Action<Collision2D> action)
        {
            UnregisterEvent(com, EventType.OnCollisionEnter2D, action);
        }

        /// <summary>注销指定的 2D 碰撞保持回调。</summary>
        public static void RemoveCollisionStay2D(this Component com, Action<Collision2D> action)
        {
            UnregisterEvent(com, EventType.OnCollisionStay2D, action);
        }

        /// <summary>注销指定的 2D 碰撞离开回调。</summary>
        public static void RemoveCollisionExit2D(this Component com, Action<Collision2D> action)
        {
            UnregisterEvent(com, EventType.OnCollisionExit2D, action);
        }

        /// <summary>注销全部 3D 碰撞进入回调。</summary>
        public static void RemoveCollisionEnter(this Component com)
        {
            UnregisterEvent(com, EventType.OnCollisionEnter);
        }

        /// <summary>注销全部 3D 碰撞保持回调。</summary>
        public static void RemoveCollisionStay(this Component com)
        {
            UnregisterEvent(com, EventType.OnCollisionStay);
        }

        /// <summary>注销全部 3D 碰撞离开回调。</summary>
        public static void RemoveCollisionExit(this Component com)
        {
            UnregisterEvent(com, EventType.OnCollisionExit);
        }

        /// <summary>注销全部 2D 碰撞进入回调。</summary>
        public static void RemoveCollisionEnter2D(this Component com)
        {
            UnregisterEvent(com, EventType.OnCollisionEnter2D);
        }

        /// <summary>注销全部 2D 碰撞保持回调。</summary>
        public static void RemoveCollisionStay2D(this Component com)
        {
            UnregisterEvent(com, EventType.OnCollisionStay2D);
        }

        /// <summary>注销全部 2D 碰撞离开回调。</summary>
        public static void RemoveCollisionExit2D(this Component com)
        {
            UnregisterEvent(com, EventType.OnCollisionExit2D);
        }

        #endregion

        #region 触发相关事件

        /// <summary>注册 3D Trigger 进入回调。</summary>
        public static IUnregister OnTriggerEnter(this Component com, Action<Collider> action)
        {
            return RegisterEvent(com, EventType.OnTriggerEnter, action);
        }

        /// <summary>注册 3D Trigger 保持回调。</summary>
        public static IUnregister OnTriggerStay(this Component com, Action<Collider> action)
        {
            return RegisterEvent(com, EventType.OnTriggerStay, action);
        }

        /// <summary>注册 3D Trigger 离开回调。</summary>
        public static IUnregister OnTriggerExit(this Component com, Action<Collider> action)
        {
            return RegisterEvent(com, EventType.OnTriggerExit, action);
        }

        /// <summary>注册 2D Trigger 进入回调。</summary>
        public static IUnregister OnTriggerEnter2D(this Component com, Action<Collider2D> action)
        {
            return RegisterEvent(com, EventType.OnTriggerEnter2D, action);
        }

        /// <summary>注册 2D Trigger 保持回调。</summary>
        public static IUnregister OnTriggerStay2D(this Component com, Action<Collider2D> action)
        {
            return RegisterEvent(com, EventType.OnTriggerStay2D, action);
        }

        /// <summary>注册 2D Trigger 离开回调。</summary>
        public static IUnregister OnTriggerExit2D(this Component com, Action<Collider2D> action)
        {
            return RegisterEvent(com, EventType.OnTriggerExit2D, action);
        }

        /// <summary>注销指定的 3D Trigger 进入回调。</summary>
        public static void RemoveTriggerEnter(this Component com, Action<Collider> action)
        {
            UnregisterEvent(com, EventType.OnTriggerEnter, action);
        }

        /// <summary>注销指定的 3D Trigger 保持回调。</summary>
        public static void RemoveTriggerStay(this Component com, Action<Collider> action)
        {
            UnregisterEvent(com, EventType.OnTriggerStay, action);
        }

        /// <summary>注销指定的 3D Trigger 离开回调。</summary>
        public static void RemoveTriggerExit(this Component com, Action<Collider> action)
        {
            UnregisterEvent(com, EventType.OnTriggerExit, action);
        }

        /// <summary>注销指定的 2D Trigger 进入回调。</summary>
        public static void RemoveTriggerEnter2D(this Component com, Action<Collider2D> action)
        {
            UnregisterEvent(com, EventType.OnTriggerEnter2D, action);
        }

        /// <summary>注销指定的 2D Trigger 保持回调。</summary>
        public static void RemoveTriggerStay2D(this Component com, Action<Collider2D> action)
        {
            UnregisterEvent(com, EventType.OnTriggerStay2D, action);
        }

        /// <summary>注销指定的 2D Trigger 离开回调。</summary>
        public static void RemoveTriggerExit2D(this Component com, Action<Collider2D> action)
        {
            UnregisterEvent(com, EventType.OnTriggerExit2D, action);
        }

        /// <summary>注销全部 3D Trigger 进入回调。</summary>
        public static void RemoveTriggerEnter(this Component com)
        {
            UnregisterEvent(com, EventType.OnTriggerEnter);
        }

        /// <summary>注销全部 3D Trigger 保持回调。</summary>
        public static void RemoveTriggerStay(this Component com)
        {
            UnregisterEvent(com, EventType.OnTriggerStay);
        }

        /// <summary>注销全部 3D Trigger 离开回调。</summary>
        public static void RemoveTriggerExit(this Component com)
        {
            UnregisterEvent(com, EventType.OnTriggerExit);
        }

        /// <summary>注销全部 2D Trigger 进入回调。</summary>
        public static void RemoveTriggerEnter2D(this Component com)
        {
            UnregisterEvent(com, EventType.OnTriggerEnter2D);
        }

        /// <summary>注销全部 2D Trigger 保持回调。</summary>
        public static void RemoveTriggerStay2D(this Component com)
        {
            UnregisterEvent(com, EventType.OnTriggerStay2D);
        }

        /// <summary>注销全部 2D Trigger 离开回调。</summary>
        public static void RemoveTriggerExit2D(this Component com)
        {
            UnregisterEvent(com, EventType.OnTriggerExit2D);
        }

        #endregion
    }
}
