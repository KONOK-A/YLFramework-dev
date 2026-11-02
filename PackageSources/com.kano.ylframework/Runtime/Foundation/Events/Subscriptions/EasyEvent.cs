using YLFramework.Events.Internal;

namespace YLFramework.Events
{
    /// <summary>
    /// 无参池化事件，注册、Once 与分发复用共享 EventChannel。
    /// 普通退订和 UnregisterAll 保留在途快照；回收或重新租用终结旧 owner 代次，截断旧快照。
    /// 归还后的原始对象别名不得继续使用，旧 token 不会影响下一租期的订阅。
    /// </summary>
    public class EasyEvent : EventChannel, IEasyEvent
    {
        /// <summary>从容量 50、预热 10 的事件池租用实例。</summary>
        public static EasyEvent Allocate() => EasyEventPool<EasyEvent>.Allocate(10);

        /// <summary>归还池租用的实例，并终结本次事件生命周期。</summary>
        public static void Recycle(EasyEvent easyEvent) => EasyEventPool<EasyEvent>.Recycle(easyEvent, 10);

        /// <summary>清除监听和旧 token，不终结当前在途快照。</summary>
        public void UnregisterAll() => ClearListeners();

        /// <inheritdoc />
        public void RecycleToCache() => Recycle(this);

        /// <inheritdoc />
        public void OnAllocate() => ResetOwner();

        /// <inheritdoc />
        public void OnRecycle() => ResetOwner();

        /// <inheritdoc />
        public bool IsRecycled
        {
            get; set;
        }
    }

    /// <summary>单参数池化事件；强类型通知和无参观察共享同一注册顺序及精确 token。</summary>
    /// <typeparam name="T">事件参数类型。</typeparam>
    public class EasyEvent<T> : EventChannel<T>, IEasyEvent
    {
        /// <summary>从容量 50、预热 1 的事件池租用实例。</summary>
        public static EasyEvent<T> Allocate() => EasyEventPool<EasyEvent<T>>.Allocate(1);

        /// <summary>归还池租用的实例，截断旧 owner 的在途通知。</summary>
        public static void Recycle(EasyEvent<T> easyEvent) => EasyEventPool<EasyEvent<T>>.Recycle(easyEvent, 1);

        /// <summary>清除监听和旧 token，保留普通在途快照。</summary>
        public void UnregisterAll() => ClearListeners();

        /// <inheritdoc />
        public void RecycleToCache() => Recycle(this);

        /// <inheritdoc />
        public void OnAllocate() => ResetOwner();

        /// <inheritdoc />
        public void OnRecycle() => ResetOwner();

        /// <inheritdoc />
        public bool IsRecycled
        {
            get; set;
        }
    }

    /// <summary>双参数池化事件，复用共享 channel 的快照、Once 和生命周期机制。</summary>
    /// <typeparam name="T1">第一个参数类型。</typeparam>
    /// <typeparam name="T2">第二个参数类型。</typeparam>
    public class EasyEvent<T1, T2> : EventChannel<T1, T2>, IEasyEvent
    {
        /// <summary>从容量 50、预热 1 的事件池租用实例。</summary>
        public static EasyEvent<T1, T2> Allocate() => EasyEventPool<EasyEvent<T1, T2>>.Allocate(1);

        /// <summary>归还池租用的实例，截断旧 owner 的在途通知。</summary>
        public static void Recycle(EasyEvent<T1, T2> easyEvent) => EasyEventPool<EasyEvent<T1, T2>>.Recycle(easyEvent, 1);

        /// <summary>清除监听和旧 token，保留普通在途快照。</summary>
        public void UnregisterAll() => ClearListeners();

        /// <inheritdoc />
        public void RecycleToCache() => Recycle(this);

        /// <inheritdoc />
        public void OnAllocate() => ResetOwner();

        /// <inheritdoc />
        public void OnRecycle() => ResetOwner();

        /// <inheritdoc />
        public bool IsRecycled
        {
            get; set;
        }
    }

    /// <summary>三参数池化事件，复用共享 channel，不维护第二套分发算法。</summary>
    /// <typeparam name="T1">第一个参数类型。</typeparam>
    /// <typeparam name="T2">第二个参数类型。</typeparam>
    /// <typeparam name="T3">第三个参数类型。</typeparam>
    public class EasyEvent<T1, T2, T3> : EventChannel<T1, T2, T3>, IEasyEvent
    {
        /// <summary>从容量 50、预热 1 的事件池租用实例。</summary>
        public static EasyEvent<T1, T2, T3> Allocate() => EasyEventPool<EasyEvent<T1, T2, T3>>.Allocate(1);

        /// <summary>归还池租用的实例，截断旧 owner 的在途通知。</summary>
        public static void Recycle(EasyEvent<T1, T2, T3> easyEvent) => EasyEventPool<EasyEvent<T1, T2, T3>>.Recycle(easyEvent, 1);

        /// <summary>清除监听和旧 token，保留普通在途快照。</summary>
        public void UnregisterAll() => ClearListeners();

        /// <inheritdoc />
        public void RecycleToCache() => Recycle(this);

        /// <inheritdoc />
        public void OnAllocate() => ResetOwner();

        /// <inheritdoc />
        public void OnRecycle() => ResetOwner();

        /// <inheritdoc />
        public bool IsRecycled
        {
            get; set;
        }
    }
}
