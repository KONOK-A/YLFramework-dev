using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using YLFramework.Singletons;
using YLFramework.Foundation;

namespace YLFramework.Events
{
    /// <summary>
    /// EventListener 支持的事件类型。
    /// </summary>
    public enum EventType
    {
        /// <summary>指针进入（对应 IPointerEnterHandler）。</summary>
        OnMouseEnter,
        /// <summary>指针离开（对应 IPointerExitHandler）。</summary>
        OnMouseExit,
        /// <summary>指针点击（对应 IPointerClickHandler）。</summary>
        OnClick,
        /// <summary>指针按下（对应 IPointerDownHandler）。</summary>
        OnClickDown,
        /// <summary>指针抬起（对应 IPointerUpHandler）。</summary>
        OnClickUp,
        /// <summary>拖拽中（对应 IDragHandler）。</summary>
        OnDrag,
        /// <summary>开始拖拽（对应 IBeginDragHandler）。</summary>
        OnBeginDrag,
        /// <summary>结束拖拽（对应 IEndDragHandler）。</summary>
        OnEndDrag,
        /// <summary>3D 碰撞进入。</summary>
        OnCollisionEnter,
        /// <summary>3D 碰撞保持。</summary>
        OnCollisionStay,
        /// <summary>3D 碰撞离开。</summary>
        OnCollisionExit,
        /// <summary>2D 碰撞进入。</summary>
        OnCollisionEnter2D,
        /// <summary>2D 碰撞保持。</summary>
        OnCollisionStay2D,
        /// <summary>2D 碰撞离开。</summary>
        OnCollisionExit2D,
        /// <summary>3D 触发器进入。</summary>
        OnTriggerEnter,
        /// <summary>3D 触发器保持。</summary>
        OnTriggerStay,
        /// <summary>3D 触发器离开。</summary>
        OnTriggerExit,
        /// <summary>2D 触发器进入。</summary>
        OnTriggerEnter2D,
        /// <summary>2D 触发器保持。</summary>
        OnTriggerStay2D,
        /// <summary>2D 触发器离开。</summary>
        OnTriggerExit2D,
    }

    /// <summary>
    /// 鼠标 / 拖拽事件的聚合接口，便于统一接收 EventSystem 的指针回调。
    /// </summary>
    public interface IMouseEvent : IPointerEnterHandler, IPointerExitHandler,
        IPointerClickHandler, IPointerDownHandler, IPointerUpHandler,
        IBeginDragHandler, IEndDragHandler, IDragHandler
    {
    }

    /// <summary>
    /// Unity GameObject 事件监听组件：把鼠标 / 拖拽 / 碰撞 / Trigger 回调转成共享 EventChannel 分发给代码注册，
    /// 同时支持 Inspector 中配置 UnityEvent。
    /// 每个 <see cref="EventType"/> 只持有一个参数类型确定的事件实例，
    /// 注册时按 Unity 原生 payload 校验，错误类型或非法 EventType 在修改前抛出异常。
    /// 组件拥有代码通道，销毁时永久关闭这些通道（不修改 Inspector 中的 UnityEvent）。
    /// </summary>
    /// <remarks>
    /// 每条消息先同步分发代码通道，再调用对应 UnityEvent；代码回调抛异常时，后续代码监听和本次 UnityEvent 均不会执行。
    /// 不提供跨监听异常隔离；销毁清理依赖 Unity 实际发送 OnDestroy，从未激活的宿主仍需调用方显式清理。
    /// </remarks>
    public class EventListener : MonoBehaviour, IMouseEvent
    {
        /// <summary>为事件字典提供 EventType 的强类型相等比较和整数哈希。</summary>
        private class EventTypeEnumComparer : Singleton<EventTypeEnumComparer>, IEqualityComparer<EventType>
        {
            /// <summary>直接比较两个事件枚举值，避免转换为 Enum 后比较。</summary>
            public bool Equals(EventType x, EventType y)
            {
                return x == y;
            }

            /// <summary>以事件枚举的整数值作为字典哈希。</summary>
            public int GetHashCode(EventType obj)
            {
                return (int)obj;
            }
        }

        #region 数据结构

        // 使用单例比较器
        private readonly Dictionary<EventType, EventChannelBase> _eventInfoDict = new(EventTypeEnumComparer.Instance);
        private bool _isDestroyed;

        #endregion

        #region 外部访问

        /// <summary>
        /// 注册一个指定 EventType 的事件监听，返回用于注销本次注册的句柄。
        /// 参数声明类型必须精确匹配 Unity 原生 payload；空回调返回空句柄，不分配事件。
        /// 不随 Disable 自动清理；需要该边界时显式绑定返回的句柄。
        /// 重复委托独立注册，返回的句柄只取消本次注册。
        /// </summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="eventType">事件类型。</param>
        /// <param name="action">事件监听回调。</param>
        /// <returns>用于精确注销本次注册的句柄。</returns>
        public IUnregister Register<T>(EventType eventType, Action<T> action)
        {
            if (_isDestroyed || !this)
            {
                throw new ObjectDisposedException(nameof(EventListener));
            }

            ValidatePayload<T>(eventType);
            if (action == null)
            {
                return EmptyUnregister.Instance;
            }

            if (_eventInfoDict.TryGetValue(eventType, out var easyEvent))
            {
                return ((EventChannel<T>)easyEvent).Register(action);
            }

            var newEvent = new EventChannel<T>();
            _eventInfoDict.TryAdd(eventType, newEvent);
            return newEvent.Register(action);
        }

        /// <summary>按事件种类核对精确的 Unity 参数类型，拒绝非法枚举值或不匹配的泛型声明。</summary>
        private static void ValidatePayload<T>(EventType eventType)
        {
            Type payloadType;
            switch (eventType)
            {
                case EventType.OnMouseEnter:
                case EventType.OnMouseExit:
                case EventType.OnClick:
                case EventType.OnClickDown:
                case EventType.OnClickUp:
                case EventType.OnDrag:
                case EventType.OnBeginDrag:
                case EventType.OnEndDrag:
                    payloadType = typeof(PointerEventData);
                    break;
                case EventType.OnCollisionEnter:
                case EventType.OnCollisionStay:
                case EventType.OnCollisionExit:
                    payloadType = typeof(Collision);
                    break;
                case EventType.OnCollisionEnter2D:
                case EventType.OnCollisionStay2D:
                case EventType.OnCollisionExit2D:
                    payloadType = typeof(Collision2D);
                    break;
                case EventType.OnTriggerEnter:
                case EventType.OnTriggerStay:
                case EventType.OnTriggerExit:
                    payloadType = typeof(Collider);
                    break;
                case EventType.OnTriggerEnter2D:
                case EventType.OnTriggerStay2D:
                case EventType.OnTriggerExit2D:
                    payloadType = typeof(Collider2D);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "未知 Unity 事件类型。");
            }
            if (typeof(T) != payloadType)
            {
                throw new ArgumentException($"{eventType} 的 Unity payload 是 {payloadType.FullName}，不能注册 {typeof(T).FullName}。", nameof(eventType));
            }
        }

        /// <summary>
        /// 移除指定 EventType 上最后一个匹配的普通委托子序列，不撤回已开始的分发快照。
        /// </summary>
        /// <typeparam name="T">事件参数类型。</typeparam>
        /// <param name="eventType">事件类型。</param>
        /// <param name="action">要注销的监听回调。</param>
        public void Unregister<T>(EventType eventType, Action<T> action)
        {
            ValidatePayload<T>(eventType);
            if (!_eventInfoDict.TryGetValue(eventType, out var easyEvent))
            {
                return;
            }
            // 有保存的事件则移除
            if (easyEvent is EventChannel<T> e)
            {
                e.Unregister(action);
            }
        }

        /// <summary>
        /// 永久关闭指定 EventType 的代码通道，终结旧 token 和旧在途快照；后续注册建立新通道。
        /// </summary>
        /// <param name="eventType">事件类型。</param>
        public void Unregister(EventType eventType)
        {
            if (!_eventInfoDict.TryGetValue(eventType, out var easyEvent))
            {
                return;
            }
            // 有保存的事件则移除
            easyEvent.Dispose();
            _eventInfoDict.Remove(eventType);
        }

        /// <summary>
        /// 关闭并移除当前全部代码通道，截断其在途快照；清理全部尝试后再统一报告异常。
        /// </summary>
        /// <param name="clearUnityEvents">是否同时将 UnityEvent 字段置空；仅影响当前实例内存，不改写序列化资源。</param>
        public void UnregisterAll(bool clearUnityEvents = false)
        {
            // 组件私有通道直接结束 owner，不把其寿命绑定到全局事件池。
            List<Exception> errors = null;
            try
            {
                SubscriptionCleanup.ReleaseAll(_eventInfoDict, pair => pair.Value.Dispose());
            }
            catch (Exception error)
            {
                (errors ??= new List<Exception>()).Add(error);
            }

            if (clearUnityEvents)
            {
                // UnityEvent.RemoveAllListeners 只能移除运行时动态添加的监听，清不掉 Inspector 配置的持久监听；
                // 需要“全部清空”时只能把字段整体置空（仅影响运行时内存，不会改写资源文件）
                pointerEnter = null;
                pointerExit = null;
                pointerClick = null;
                pointerDown = null;
                pointerUp = null;
                beginDrag = null;
                drag = null;
                endDrag = null;

                collisionEnter = null;
                collisionStay = null;
                collisionExit = null;
                collisionEnter2D = null;
                collisionStay2D = null;
                collisionExit2D = null;

                triggerEnter = null;
                triggerStay = null;
                triggerExit = null;
                triggerEnter2D = null;
                triggerStay2D = null;
                triggerExit2D = null;
            }
            SubscriptionCleanup.ThrowErrors(errors);
        }

        #endregion

        #region UnityEvent

        #region Unity鼠标事件
        [SerializeField]
        private UnityEvent<PointerEventData> pointerEnter;
        [SerializeField]
        private UnityEvent<PointerEventData> pointerExit;
        [SerializeField]
        private UnityEvent<PointerEventData> pointerClick;
        [SerializeField]
        private UnityEvent<PointerEventData> pointerDown;
        [SerializeField]
        private UnityEvent<PointerEventData> pointerUp;
        [SerializeField]
        private UnityEvent<PointerEventData> beginDrag;
        [SerializeField]
        private UnityEvent<PointerEventData> drag;
        [SerializeField]
        private UnityEvent<PointerEventData> endDrag;

        #endregion

        #region Unity碰撞事件
        [SerializeField]
        private UnityEvent<Collision> collisionEnter;
        [SerializeField]
        private UnityEvent<Collision> collisionStay;
        [SerializeField]
        private UnityEvent<Collision> collisionExit;
        [SerializeField]
        private UnityEvent<Collision2D> collisionEnter2D;
        [SerializeField]
        private UnityEvent<Collision2D> collisionStay2D;
        [SerializeField]
        private UnityEvent<Collision2D> collisionExit2D;

        #endregion

        #region Trigger事件

        [SerializeField]
        private UnityEvent<Collider> triggerEnter;
        [SerializeField]
        private UnityEvent<Collider> triggerStay;
        [SerializeField]
        private UnityEvent<Collider> triggerExit;
        [SerializeField]
        private UnityEvent<Collider2D> triggerEnter2D;
        [SerializeField]
        private UnityEvent<Collider2D> triggerStay2D;
        [SerializeField]
        private UnityEvent<Collider2D> triggerExit2D;

        #endregion

        #endregion

        [SerializeField]
        [Tooltip("仅过滤 Collision/Trigger 的对方层，同时作用于代码和 Inspector；3D/2D 共用，None 不接收物理事件。")]
        private LayerMask physicsLayerMask = Physics.AllLayers;

        /// <summary>物理消息的对方层过滤；不影响指针消息或 Unity 原生物理判定。</summary>
        public LayerMask PhysicsLayerMask
        {
            get => physicsLayerMask; set => physicsLayerMask = value;
        }

        /// <summary>标记组件已销毁，尝试关闭自有代码通道，并在 Unity 消息边界记录清理异常。</summary>
        private void OnDestroy()
        {
            _isDestroyed = true;
            try
            {
                UnregisterAll();
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
            }
        }

        /// <summary>仅向已存在且参数匹配的代码通道同步发布，不为未监听的消息建立通道。</summary>
        private void TriggerEvent<T>(EventType eventType, T eventData)
        {
            if (!_eventInfoDict.TryGetValue(eventType, out var easyEvent))
            {
                return;
            }

            if (easyEvent is EventChannel<T> e)
            {
                e.Trigger(eventData);
            }
        }

        #region 鼠标事件

        /// <summary>指针进入时触发 OnMouseEnter 与 Inspector 事件。</summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnMouseEnter, eventData);
            pointerEnter?.Invoke(eventData);
        }

        /// <summary>指针离开时触发 OnMouseExit 与 Inspector 事件。</summary>
        public void OnPointerExit(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnMouseExit, eventData);
            pointerExit?.Invoke(eventData);
        }

        /// <summary>开始拖拽时触发 OnBeginDrag 与 Inspector 事件。</summary>
        public void OnBeginDrag(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnBeginDrag, eventData);
            beginDrag?.Invoke(eventData);
        }

        /// <summary>拖拽中触发 OnDrag 与 Inspector 事件。</summary>
        public void OnDrag(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnDrag, eventData);
            drag?.Invoke(eventData);
        }

        /// <summary>结束拖拽时触发 OnEndDrag 与 Inspector 事件。</summary>
        public void OnEndDrag(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnEndDrag, eventData);
            endDrag?.Invoke(eventData);
        }

        /// <summary>指针点击时触发 OnClick 与 Inspector 事件。</summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnClick, eventData);
            pointerClick?.Invoke(eventData);
        }

        /// <summary>指针按下时触发 OnClickDown 与 Inspector 事件。</summary>
        public void OnPointerDown(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnClickDown, eventData);
            pointerDown?.Invoke(eventData);
        }

        /// <summary>指针抬起时触发 OnClickUp 与 Inspector 事件。</summary>
        public void OnPointerUp(PointerEventData eventData)
        {
            TriggerEvent(EventType.OnClickUp, eventData);
            pointerUp?.Invoke(eventData);
        }

        #endregion

        #region 碰撞事件

        /// <summary>按对方层筛选 3D 碰撞进入消息，通过后交给代码通道和 Inspector 事件。</summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnCollisionEnter, collision);
            collisionEnter?.Invoke(collision);
        }

        /// <summary>按对方层筛选 3D 持续碰撞消息，通过后转发原始碰撞数据。</summary>
        private void OnCollisionStay(Collision collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnCollisionStay, collision);
            collisionStay?.Invoke(collision);
        }

        /// <summary>按对方当前层筛选 3D 碰撞离开消息，不补发被过滤的离开通知。</summary>
        private void OnCollisionExit(Collision collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnCollisionExit, collision);
            collisionExit?.Invoke(collision);
        }

        /// <summary>按对方层筛选 2D 碰撞进入消息，通过后交给代码通道和 Inspector 事件。</summary>
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnCollisionEnter2D, collision);
            collisionEnter2D?.Invoke(collision);
        }

        /// <summary>按对方层筛选 2D 持续碰撞消息，通过后转发原始碰撞数据。</summary>
        private void OnCollisionStay2D(Collision2D collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnCollisionStay2D, collision);
            collisionStay2D?.Invoke(collision);
        }

        /// <summary>按对方当前层筛选 2D 碰撞离开消息，不补发被过滤的离开通知。</summary>
        private void OnCollisionExit2D(Collision2D collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnCollisionExit2D, collision);
            collisionExit2D?.Invoke(collision);
        }

        #endregion

        #region 触发事件

        /// <summary>按对方层筛选 3D 触发进入消息，通过后转发对方碰撞体。</summary>
        private void OnTriggerEnter(Collider other)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(other.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnTriggerEnter, other);
            triggerEnter?.Invoke(other);
        }

        /// <summary>按对方层筛选 3D 持续触发消息，通过后转发对方碰撞体。</summary>
        private void OnTriggerStay(Collider other)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(other.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnTriggerStay, other);
            triggerStay?.Invoke(other);
        }

        /// <summary>按对方当前层筛选 3D 触发离开消息，不补发被过滤的离开通知。</summary>
        private void OnTriggerExit(Collider other)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(other.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnTriggerExit, other);
            triggerExit?.Invoke(other);
        }

        /// <summary>按对方层筛选 2D 触发进入消息，通过后转发对方碰撞体。</summary>
        private void OnTriggerEnter2D(Collider2D collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnTriggerEnter2D, collision);
            triggerEnter2D?.Invoke(collision);
        }

        /// <summary>按对方层筛选 2D 持续触发消息，通过后转发对方碰撞体。</summary>
        private void OnTriggerStay2D(Collider2D collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnTriggerStay2D, collision);
            triggerStay2D?.Invoke(collision);
        }

        /// <summary>按对方当前层筛选 2D 触发离开消息，不补发被过滤的离开通知。</summary>
        private void OnTriggerExit2D(Collider2D collision)
        {
            if (physicsLayerMask.value != Physics.AllLayers && !physicsLayerMask.Contains(collision.gameObject.layer))
            {
                return;
            }

            TriggerEvent(EventType.OnTriggerExit2D, collision);
            triggerExit2D?.Invoke(collision);
        }

        #endregion
    }
}
