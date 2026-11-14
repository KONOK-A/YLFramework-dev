using System;
using System.Collections.Generic;
using YLFramework.Singletons;

namespace YLFramework.Pooling
{
    /// <summary>
    /// List&lt;T&gt; 的静态对象池。
    /// 回收时清空元素，仅保留实际容量在配置上限内的列表。
    /// 非线程安全，只接受从本池取得的列表。
    /// </summary>
    /// <typeparam name="T">列表元素类型。</typeparam>
    public static class ListPool<T>
    {
        private static int _maxRetainedCapacity = 4096;
        private static readonly ObjectPool<List<T>> _pool = new(
            Create, onAllocate: null, reset: list => list.Clear(), onDiscard: null,
            maxPoolSize: 20, cacheOrder: PoolCacheOrder.Lifo, canRetain: CanRetain);

        /// <summary>登记会话清理，进入新运行会话时丢弃旧的空闲列表缓存。</summary>
        static ListPool()
        {
            SingletonSessionState.RegisterSessionReset(_pool.ResetSession);
        }

        /// <summary>
        /// 空闲列表数量上限，默认 20；非正值表示不限。
        /// </summary>
        public static int MaxPoolSize
        {
            get => _pool.MaxPoolSize;
            set => _pool.MaxPoolSize = value;
        }

        /// <summary>
        /// 可保留的实际槽位上限，默认4096；-1不限，0仅保留零容量列表。
        /// 降低阈值清空当前闲置缓存，不影响已借出对象；不是精确字节预算。
        /// </summary>
        public static int MaxRetainedCapacity
        {
            get => _maxRetainedCapacity;
            set
            {
                if (value < -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                var previous = _maxRetainedCapacity;
                _maxRetainedCapacity = value;
                if (value >= 0 && (previous < 0 || value < previous))
                {
                    _pool.Clear();
                }
            }
        }

        /// <summary>
        /// 从池中获取列表，默认容量为 8。
        /// </summary>
        public static List<T> Allocate()
        {
            return Allocate(8);
        }

        /// <summary>
        /// 从池中获取列表，并保证其 Capacity 不小于 capacity。
        /// </summary>
        /// <param name="capacity">期望的最小容量。</param>
        /// <exception cref="ArgumentOutOfRangeException">capacity 为负数。</exception>
        public static List<T> Allocate(int capacity) => Allocate(capacity, false, out _);

        /// <summary>作用域借用并保证最小容量，必须通过返回的租约结束，不能裸引用归还。</summary>
        public static PoolLease<List<T>> Rent(out List<T> list, int capacity = 8)
        {
            list = Allocate(capacity, true, out var rental);
            return rental;
        }

        private static List<T> Allocate(int capacity, bool scoped, out PoolLease<List<T>> rental)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            rental = default;
            List<T> list;
            if (scoped)
            {
                rental = _pool.Rent(out list);
            }
            else
            {
                list = _pool.Allocate();
            }

            try
            {
                if (list.Capacity < capacity)
                {
                    list.Capacity = capacity;
                }
                return list;
            }
            catch (Exception error)
            {
                try
                {
                    if (scoped)
                    {
                        rental.Discard();
                    }
                    else
                    {
                        _pool.Discard(list);
                    }
                }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        /// <summary>
        /// 结束列表借用：清空元素后按容量决定是否缓存。归还后不得继续使用列表。
        /// </summary>
        /// <param name="toRecycle">待回收列表，null 会被忽略。</param>
        /// <remarks>null 和重复归还不执行任何操作；缓存已满时仍清空元素并结束借用。</remarks>
        /// <exception cref="InvalidOperationException">列表不是从本池取得的。</exception>
        public static void Recycle(List<T> toRecycle)
        {
            _pool.Recycle(toRecycle);
        }

        /// <summary>
        /// 丢弃所有空闲缓存列表，不影响已借出的列表。
        /// </summary>
        public static void Clear()
        {
            _pool.Clear();
        }

        private static List<T> Create() => new();

        private static bool CanRetain(List<T> list) => _maxRetainedCapacity < 0 || list.Capacity <= _maxRetainedCapacity;
    }

    /// <summary>
    /// <see cref="ListPool{T}"/> 的扩展入口。
    /// </summary>
    public static class ListPoolExtensions
    {
        /// <summary>
        /// 将列表回收进 ListPool。
        /// </summary>
        /// <param name="self">从同类型 ListPool 借出的列表；归还后不得继续使用，null 会被忽略。</param>
        public static void RecycleToPool<T>(this List<T> self)
        {
            ListPool<T>.Recycle(self);
        }
    }
}
