using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace YLFramework.Extensions
{

    /// <summary>
    /// 管理 EventTrigger 的运行时回调，不删除事件条目，也不自动解绑回调。
    /// trigger 为 null 或已销毁时跳过；移除操作不影响 Inspector 配置的持久回调。
    /// </summary>
    public static class EventTriggerExtensions
    {

        /// <summary>向首个匹配事件类型的条目添加回调；没有匹配条目时先创建条目。</summary>
        /// <param name="trigger">接收回调的 EventTrigger。</param>
        /// <param name="eventType">待监听的事件类型。</param>
        /// <param name="action">接收事件数据的运行时回调。</param>
        public static void AddListener(this EventTrigger trigger, EventTriggerType eventType, UnityAction<BaseEventData> action)
        {

            if (trigger == null)
            {
                return;
            }

            EventTrigger.Entry entry = null;

            foreach (var item in trigger.triggers)
            {
                if (item.eventID == eventType)
                {
                    entry = item;
                    break;
                }
            }

            if (entry == null)
            {
                entry = new EventTrigger.Entry();
                entry.eventID = eventType;
                trigger.triggers.Add(entry);
            }

            entry.callback.AddListener(action);
        }

        /// <summary>从所有匹配事件类型的条目中移除指定的运行时回调，保留条目本身。</summary>
        /// <param name="trigger">待移除回调的 EventTrigger。</param>
        /// <param name="eventType">待匹配的事件类型。</param>
        /// <param name="action">需要解除的回调。</param>
        public static void RemoveListener(this EventTrigger trigger, EventTriggerType eventType, UnityAction<BaseEventData> action)
        {
            if (trigger == null)
            {
                return;
            }

            foreach (var item in trigger.triggers)
            {
                if (item.eventID == eventType)
                {
                    item.callback.RemoveListener(action);
                }
            }
        }

        /// <summary>清除所有匹配事件类型条目的运行时回调，保留条目本身。</summary>
        /// <param name="trigger">待清理的 EventTrigger。</param>
        /// <param name="eventType">待匹配的事件类型。</param>
        public static void RemoveAllListener(this EventTrigger trigger, EventTriggerType eventType)
        {
            if (trigger == null)
            {
                return;
            }

            foreach (var item in trigger.triggers)
            {
                if (item.eventID == eventType)
                {
                    item.callback.RemoveAllListeners();
                }
            }
        }


        /// <summary>清除全部事件条目的运行时回调，保留所有条目。</summary>
        /// <param name="trigger">待清理的 EventTrigger。</param>
        public static void RemoveAllListener(this EventTrigger trigger)
        {
            if (trigger == null)
            {
                return;
            }

            foreach (var item in trigger.triggers)
            {
                item.callback.RemoveAllListeners();
            }
        }

    }
}
