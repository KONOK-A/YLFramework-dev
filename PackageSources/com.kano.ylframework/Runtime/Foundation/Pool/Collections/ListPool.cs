using System;
using System.Collections.Generic;
using YLFramework.Singletons;

namespace YLFramework.Pooling
{
    /// <summary>
    /// List&lt;T&gt; 的静态对象池。
    /// 回收时只清空元素、保留容量；池为空时按请求容量创建新列表。
    /// 非线程安全，只接受从本池取得的列表。
    /// </summary>
    /// <typeparam name="T">列表元素类型。</typeparam>
    public static class ListPool<T>
    {
        private static int _createCapacity;
        private static readonly ObjectPool<List<T>> _pool = new(
            Create, reset: list => list.Clear(), cacheOrder: PoolCacheOrder.Lifo);

        /// <summary>登记会话清理，进入新运行会话时丢弃旧的空闲列表缓存。</summary>
        static ListPool()
        {
            SingletonSessionState.RegisterSessionReset(Clear);
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
        public static List<T> Allocate(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            // 稳定工厂只在冷创建时读取容量，避免每次租用创建捕获闭包。
            var previousCapacity = _createCapacity;
            List<T> list;
            _createCapacity = capacity;
            try
            {
                list = _pool.Allocate();
            }
            finally
            {
                _createCapacity = previousCapacity;
            }

            try
            {
                if (list.Capacity < capacity)
                {
                    list.Capacity = capacity;
                }
                return list;
            }
            catch
            {
                _pool.Discard(list);
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

        /// <summary>按当前租用请求的容量冷创建列表，供固定工厂复用。</summary>
        private static List<T> Create() => new(_createCapacity);
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
