using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Events
{
    /// <summary>
    /// 按类型存储一组事件实例的集合（类型 → 事件）。
    /// 实例版可用于子系统内部；静态入口维护一个全局实例，
    /// 每次进入 Play 会话时通过 Singleton 重置机制终结标准事件的旧代次并清空映射，
    /// 避免 Domain Reload disabled 时旧监听残留到下一次 Play。
    /// 会话运行期内有意注册的全局事件不会被清空。
    /// </summary>
    public class EasyEvents
    {
        private static readonly EasyEvents _globalEasyEvents = new();

        /// <summary>为全局事件集合登记 Play 会话重置入口。</summary>
        static EasyEvents()
        {
            // Domain Reload disabled 时静态字段会跨 Play 会话保留，
            // 保持全局容器实例，结束旧事件代次，不把其 owned event 归入池。
            SingletonSessionState.RegisterSessionReset(ClearGlobalEvents);
        }

        /// <summary>
        /// 获取全局集合中已注册的事件；类型未注册时返回 default。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        public static T Get<T>() where T : IEasyEvent, new()
        {
            return _globalEasyEvents.GetEvent<T>();
        }

        /// <summary>
        /// 向全局集合注册一个事件类型；同一类型重复注册会被忽略。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        public static void Register<T>() where T : IEasyEvent, new()
        {
            _globalEasyEvents.AddEvent<T>();
        }

        /// <summary>结束全局标准事件的旧代次并清理映射，将清理异常记录到日志而不阻断其他会话重置。</summary>
        private static void ClearGlobalEvents()
        {
            try
            {
                _globalEasyEvents.ClearListeners(true);
            }
            catch (Exception error)
            {
                // 自定义事件清理失败不能阻断其他 Singleton/Pool 的会话重置。
                Debug.LogException(error);
            }
        }

        private readonly Dictionary<Type, IEasyEvent> _eventTypeDict = new();

        /// <summary>
        /// 添加一个事件实例；同一类型已存在时忽略本次添加（不抛异常）。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        public void AddEvent<T>() where T : IEasyEvent, new()
        {
            var eventType = typeof(T);
            if (!_eventTypeDict.ContainsKey(eventType))
            {
                _eventTypeDict.Add(eventType, new T());
            }
        }

        /// <summary>
        /// 获取已注册的事件实例；类型不存在时返回 default。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        public T GetEvent<T>() where T : IEasyEvent, new()
        {
            return _eventTypeDict.TryGetValue(typeof(T), out var e) ? (T)e : default;
        }

        /// <summary>
        /// 获取已注册的事件实例，不存在时创建并保存。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        public T GetOrAddEvent<T>() where T : IEasyEvent, new()
        {
            var eventType = typeof(T);
            if (_eventTypeDict.TryGetValue(eventType, out var e))
            {
                return (T)e;
            }

            var instance = new T();
            _eventTypeDict.Add(eventType, instance);
            return instance;
        }

        /// <summary>
        /// 移除指定类型的事件映射，不注销其监听或回收实例；持有该事件的调用方仍可继续使用它。
        /// </summary>
        /// <typeparam name="T">事件类型。</typeparam>
        /// <returns>存在并被移除时返回 true。</returns>
        public bool Remove<T>() where T : IEasyEvent, new()
        {
            return _eventTypeDict.Remove(typeof(T));
        }

        /// <summary>
        /// 清空所有类型映射。只解除引用，不回收事件实例（事件生命周期由创建方管理）。
        /// </summary>
        public void Clear()
        {
            _eventTypeDict.Clear();
        }

        /// <summary>
        /// 摘除本轮映射并逐项注销监听；不回收实例、不截断普通在途快照。
        /// 全部尝试后报告清理异常，重入新增的映射保留。
        /// </summary>
        public void ClearListeners()
        {
            ClearListeners(false);
        }

        /// <summary>先摘除本轮映射再逐事件清理，按需结束标准通道代次，并在全部尝试后统一报告异常。</summary>
        private void ClearListeners(bool resetOwners)
        {
            var events = new List<IEasyEvent>(_eventTypeDict.Values);
            _eventTypeDict.Clear();
            List<Exception> errors = null;
            foreach (var e in events)
            {
                if (resetOwners && e is EventChannelBase channel)
                {
                    try
                    {
                        channel.ResetOwner();
                    }
                    catch (Exception error)
                    {
                        (errors ??= new List<Exception>()).Add(error);
                    }
                }
                try
                {
                    // 自定义事件可能重实现接口并持有额外责任，不能被标准 channel 的重置替代。
                    e.UnregisterAll();
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
            }
            SubscriptionCleanup.ThrowErrors(errors);
        }
    }
}
