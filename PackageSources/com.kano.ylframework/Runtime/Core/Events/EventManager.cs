using System;
using YLFramework.Core.RoslynAnalyzers;
using YLFramework.Events;

namespace YLFramework.Core
{
    /// <summary>
    /// Core 的强类型事件入口，全部转接 EventBus.Default，不持有独立事件状态。
    /// string 保留完整 Ordinal 身份，与 int 键互不相通；同一键必须使用精确一致的参数签名。
    /// AddEvent 返回本次注册的精确取消句柄，null 回调返回空句柄但仍校验键及已有签名。
    /// RemoveEvent 只取消最后匹配的委托子序列，保留签名及普通在途快照。
    /// TriggerEvent 保留原异常并立即中断；TriggerEventAll 尝试所有观察者后汇总异常。
    /// 发布、取消及清理不创建 Default，缺失键为空操作。
    /// </summary>
    public static class EventManager
    {
        /// <summary>先校验键，再按需取得默认上下文；只有注册非空回调的入口才允许创建缺失上下文。</summary>
        private static EventBus GetDefault(EventKey key, bool create = false)
        {
            key.Validate();
            return EventBus.TryGetDefault(out var bus) ? bus : create ? EventBus.Default : null;
        }

        #region int 键

        /// <summary>为 int 键注册无参回调，返回仅取消本次注册的句柄。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent(int eventId, Action callback)
            => GetDefault(eventId, callback != null)?.Register(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的无参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent(int eventId, Action callback)
            => GetDefault(eventId)?.Unregister(eventId, callback);

        /// <summary>同步发布 int 键的无参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent(int eventId)
            => GetDefault(eventId)?.Send(eventId);

        /// <summary>尝试 int 键无参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll(int eventId)
            => GetDefault(eventId)?.SendAll(eventId);

        /// <summary>为 int 键注册单参回调，参数类型须与已有通道精确一致。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1>(int eventId, Action<T1> callback)
            => GetDefault(eventId, callback != null)?.Register<T1>(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的单参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1>(int eventId, Action<T1> callback)
            => GetDefault(eventId)?.Unregister<T1>(eventId, callback);

        /// <summary>同步发布 int 键的单参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1>(int eventId, T1 p)
            => GetDefault(eventId)?.Send<T1>(eventId, p);

        /// <summary>尝试 int 键单参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1>(int eventId, T1 p1)
            => GetDefault(eventId)?.SendAll<T1>(eventId, p1);

        /// <summary>为 int 键注册双参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2>(int eventId, Action<T1, T2> callback)
            => GetDefault(eventId, callback != null)?.Register<T1, T2>(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的双参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2>(int eventId, Action<T1, T2> callback)
            => GetDefault(eventId)?.Unregister<T1, T2>(eventId, callback);

        /// <summary>同步发布 int 键的双参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2>(int eventId, T1 p, T2 p2)
            => GetDefault(eventId)?.Send<T1, T2>(eventId, p, p2);

        /// <summary>尝试 int 键双参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2>(int eventId, T1 p1, T2 p2)
            => GetDefault(eventId)?.SendAll<T1, T2>(eventId, p1, p2);

        /// <summary>为 int 键注册三参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3>(int eventId, Action<T1, T2, T3> callback)
            => GetDefault(eventId, callback != null)?.Register<T1, T2, T3>(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的三参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3>(int eventId, Action<T1, T2, T3> callback)
            => GetDefault(eventId)?.Unregister<T1, T2, T3>(eventId, callback);

        /// <summary>同步发布 int 键的三参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3>(int eventId, T1 p, T2 p2, T3 p3)
            => GetDefault(eventId)?.Send<T1, T2, T3>(eventId, p, p2, p3);

        /// <summary>尝试 int 键三参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3>(int eventId, T1 p1, T2 p2, T3 p3)
            => GetDefault(eventId)?.SendAll<T1, T2, T3>(eventId, p1, p2, p3);

        /// <summary>为 int 键注册四参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4>(int eventId, Action<T1, T2, T3, T4> callback)
            => GetDefault(eventId, callback != null)?.Register<T1, T2, T3, T4>(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的四参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4>(int eventId, Action<T1, T2, T3, T4> callback)
            => GetDefault(eventId)?.Unregister<T1, T2, T3, T4>(eventId, callback);

        /// <summary>同步发布 int 键的四参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4>(int eventId, T1 p, T2 p2, T3 p3, T4 p4)
            => GetDefault(eventId)?.Send<T1, T2, T3, T4>(eventId, p, p2, p3, p4);

        /// <summary>尝试 int 键四参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4>(int eventId, T1 p1, T2 p2, T3 p3, T4 p4)
            => GetDefault(eventId)?.SendAll<T1, T2, T3, T4>(eventId, p1, p2, p3, p4);

        /// <summary>为 int 键注册五参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4, T5>(int eventId, Action<T1, T2, T3, T4, T5> callback)
            => GetDefault(eventId, callback != null)?.Register<T1, T2, T3, T4, T5>(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的五参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4, T5>(int eventId, Action<T1, T2, T3, T4, T5> callback)
            => GetDefault(eventId)?.Unregister<T1, T2, T3, T4, T5>(eventId, callback);

        /// <summary>同步发布 int 键的五参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4, T5>(int eventId, T1 p, T2 p2, T3 p3, T4 p4, T5 p5)
            => GetDefault(eventId)?.Send<T1, T2, T3, T4, T5>(eventId, p, p2, p3, p4, p5);

        /// <summary>尝试 int 键五参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4, T5>(int eventId, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5)
            => GetDefault(eventId)?.SendAll<T1, T2, T3, T4, T5>(eventId, p1, p2, p3, p4, p5);

        /// <summary>为 int 键注册六参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4, T5, T6>(int eventId, Action<T1, T2, T3, T4, T5, T6> callback)
            => GetDefault(eventId, callback != null)?.Register<T1, T2, T3, T4, T5, T6>(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的六参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4, T5, T6>(int eventId, Action<T1, T2, T3, T4, T5, T6> callback)
            => GetDefault(eventId)?.Unregister<T1, T2, T3, T4, T5, T6>(eventId, callback);

        /// <summary>同步发布 int 键的六参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4, T5, T6>(int eventId, T1 p, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6)
            => GetDefault(eventId)?.Send<T1, T2, T3, T4, T5, T6>(eventId, p, p2, p3, p4, p5, p6);

        /// <summary>尝试 int 键六参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4, T5, T6>(int eventId, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6)
            => GetDefault(eventId)?.SendAll<T1, T2, T3, T4, T5, T6>(eventId, p1, p2, p3, p4, p5, p6);

        /// <summary>为 int 键注册七参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4, T5, T6, T7>(int eventId, Action<T1, T2, T3, T4, T5, T6, T7> callback)
            => GetDefault(eventId, callback != null)?.Register<T1, T2, T3, T4, T5, T6, T7>(eventId, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 int 键的七参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4, T5, T6, T7>(int eventId, Action<T1, T2, T3, T4, T5, T6, T7> callback)
            => GetDefault(eventId)?.Unregister<T1, T2, T3, T4, T5, T6, T7>(eventId, callback);

        /// <summary>同步发布 int 键的七参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4, T5, T6, T7>(int eventId, T1 p, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7)
            => GetDefault(eventId)?.Send<T1, T2, T3, T4, T5, T6, T7>(eventId, p, p2, p3, p4, p5, p6, p7);

        /// <summary>尝试 int 键七参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4, T5, T6, T7>(int eventId, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7)
            => GetDefault(eventId)?.SendAll<T1, T2, T3, T4, T5, T6, T7>(eventId, p1, p2, p3, p4, p5, p6, p7);

        #endregion

        #region string 键

        /// <summary>为完整 string 键注册无参回调，返回仅取消本次注册的句柄。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent(string eventName, Action callback)
            => GetDefault(eventName, callback != null)?.Register(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的无参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent(string eventName, Action callback)
            => GetDefault(eventName)?.Unregister(eventName, callback);

        /// <summary>同步发布 string 键的无参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent(string eventName)
            => GetDefault(eventName)?.Send(eventName);

        /// <summary>尝试 string 键无参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll(string eventName)
            => GetDefault(eventName)?.SendAll(eventName);

        /// <summary>为 string 键注册单参回调，参数类型须与已有通道精确一致。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1>(string eventName, Action<T1> callback)
            => GetDefault(eventName, callback != null)?.Register<T1>(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的单参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1>(string eventName, Action<T1> callback)
            => GetDefault(eventName)?.Unregister<T1>(eventName, callback);

        /// <summary>同步发布 string 键的单参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1>(string eventName, T1 p)
            => GetDefault(eventName)?.Send<T1>(eventName, p);

        /// <summary>尝试 string 键单参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1>(string eventName, T1 p1)
            => GetDefault(eventName)?.SendAll<T1>(eventName, p1);

        /// <summary>为 string 键注册双参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2>(string eventName, Action<T1, T2> callback)
            => GetDefault(eventName, callback != null)?.Register<T1, T2>(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的双参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2>(string eventName, Action<T1, T2> callback)
            => GetDefault(eventName)?.Unregister<T1, T2>(eventName, callback);

        /// <summary>同步发布 string 键的双参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2>(string eventName, T1 p, T2 p2)
            => GetDefault(eventName)?.Send<T1, T2>(eventName, p, p2);

        /// <summary>尝试 string 键双参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2>(string eventName, T1 p1, T2 p2)
            => GetDefault(eventName)?.SendAll<T1, T2>(eventName, p1, p2);

        /// <summary>为 string 键注册三参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3>(string eventName, Action<T1, T2, T3> callback)
            => GetDefault(eventName, callback != null)?.Register<T1, T2, T3>(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的三参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3>(string eventName, Action<T1, T2, T3> callback)
            => GetDefault(eventName)?.Unregister<T1, T2, T3>(eventName, callback);

        /// <summary>同步发布 string 键的三参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3>(string eventName, T1 p, T2 p2, T3 p3)
            => GetDefault(eventName)?.Send<T1, T2, T3>(eventName, p, p2, p3);

        /// <summary>尝试 string 键三参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3>(string eventName, T1 p1, T2 p2, T3 p3)
            => GetDefault(eventName)?.SendAll<T1, T2, T3>(eventName, p1, p2, p3);

        /// <summary>为 string 键注册四参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4>(string eventName, Action<T1, T2, T3, T4> callback)
            => GetDefault(eventName, callback != null)?.Register<T1, T2, T3, T4>(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的四参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4>(string eventName, Action<T1, T2, T3, T4> callback)
            => GetDefault(eventName)?.Unregister<T1, T2, T3, T4>(eventName, callback);

        /// <summary>同步发布 string 键的四参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4>(string eventName, T1 p, T2 p2, T3 p3, T4 p4)
            => GetDefault(eventName)?.Send<T1, T2, T3, T4>(eventName, p, p2, p3, p4);

        /// <summary>尝试 string 键四参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4>(string eventName, T1 p1, T2 p2, T3 p3, T4 p4)
            => GetDefault(eventName)?.SendAll<T1, T2, T3, T4>(eventName, p1, p2, p3, p4);

        /// <summary>为 string 键注册五参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4, T5>(string eventName, Action<T1, T2, T3, T4, T5> callback)
            => GetDefault(eventName, callback != null)?.Register<T1, T2, T3, T4, T5>(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的五参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4, T5>(string eventName, Action<T1, T2, T3, T4, T5> callback)
            => GetDefault(eventName)?.Unregister<T1, T2, T3, T4, T5>(eventName, callback);

        /// <summary>同步发布 string 键的五参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4, T5>(string eventName, T1 p, T2 p2, T3 p3, T4 p4, T5 p5)
            => GetDefault(eventName)?.Send<T1, T2, T3, T4, T5>(eventName, p, p2, p3, p4, p5);

        /// <summary>尝试 string 键五参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4, T5>(string eventName, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5)
            => GetDefault(eventName)?.SendAll<T1, T2, T3, T4, T5>(eventName, p1, p2, p3, p4, p5);

        /// <summary>为 string 键注册六参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4, T5, T6>(string eventName, Action<T1, T2, T3, T4, T5, T6> callback)
            => GetDefault(eventName, callback != null)?.Register<T1, T2, T3, T4, T5, T6>(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的六参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4, T5, T6>(string eventName, Action<T1, T2, T3, T4, T5, T6> callback)
            => GetDefault(eventName)?.Unregister<T1, T2, T3, T4, T5, T6>(eventName, callback);

        /// <summary>同步发布 string 键的六参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4, T5, T6>(string eventName, T1 p, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6)
            => GetDefault(eventName)?.Send<T1, T2, T3, T4, T5, T6>(eventName, p, p2, p3, p4, p5, p6);

        /// <summary>尝试 string 键六参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4, T5, T6>(string eventName, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6)
            => GetDefault(eventName)?.SendAll<T1, T2, T3, T4, T5, T6>(eventName, p1, p2, p3, p4, p5, p6);

        /// <summary>为 string 键注册七参回调，参数类型及顺序共同确定通道签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 方法代替!")]
        public static IUnregister AddEvent<T1, T2, T3, T4, T5, T6, T7>(string eventName, Action<T1, T2, T3, T4, T5, T6, T7> callback)
            => GetDefault(eventName, callback != null)?.Register<T1, T2, T3, T4, T5, T6, T7>(eventName, callback) ?? EmptyUnregister.Instance;

        /// <summary>按 string 键的七参签名取消最后匹配的普通委托子序列，不删除键或签名。</summary>
        [WarnInClasses(typeof(View), "推荐使用 View 中的 RegisterEvent 来注册事件,通过该方法注册事件会自动移除!")]
        public static void RemoveEvent<T1, T2, T3, T4, T5, T6, T7>(string eventName, Action<T1, T2, T3, T4, T5, T6, T7> callback)
            => GetDefault(eventName)?.Unregister<T1, T2, T3, T4, T5, T6, T7>(eventName, callback);

        /// <summary>同步发布 string 键的七参事件，首个回调异常立即传播。</summary>
        public static void TriggerEvent<T1, T2, T3, T4, T5, T6, T7>(string eventName, T1 p, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7)
            => GetDefault(eventName)?.Send<T1, T2, T3, T4, T5, T6, T7>(eventName, p, p2, p3, p4, p5, p6, p7);

        /// <summary>尝试 string 键七参通道中的全部监听，结束后报告单个原异常或聚合异常。</summary>
        public static void TriggerEventAll<T1, T2, T3, T4, T5, T6, T7>(string eventName, T1 p1, T2 p2, T3 p3, T4 p4, T5 p5, T6 p6, T7 p7)
            => GetDefault(eventName)?.SendAll<T1, T2, T3, T4, T5, T6, T7>(eventName, p1, p2, p3, p4, p5, p6, p7);

        #endregion

        /// <summary>
        /// 终结并删除整个 Default 的全部键、签名及在途快照，包括通过其他 EventBus.Default 入口建立的键。
        /// Default 实例保持可用；不影响独立 EventBus 或 YL Global facade，不应作为 Module/View 的退出清理。
        /// </summary>
        public static void RemoveAllEvent()
        {
            if (EventBus.TryGetDefault(out var bus))
            {
                bus.ClearKeys();
            }
        }

        /// <summary>终结并删除 Default 中此完整 string 键及在途快照，允许重新绑定签名；旧 token 失效。</summary>
        public static void RemoveAllEvent(string eventName)
            => GetDefault(eventName)?.RemoveKey(eventName);

        /// <summary>终结并删除 Default 中此 int 键及在途快照，允许重新绑定签名；旧 token 失效。</summary>
        public static void RemoveAllEvent(int eventId)
            => GetDefault(eventId)?.RemoveKey(eventId);
    }
}
