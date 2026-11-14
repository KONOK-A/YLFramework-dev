using System;
using System.Collections.Generic;
using YLFramework.Singletons;

namespace YLFramework.Pooling
{
    /// <summary>
    /// Dictionary&lt;TKey, TValue&gt; 的静态对象池。
    /// 回收时清空元素，仅保留实际容量在配置上限内的字典。
    /// 非线程安全，只接受从本池取得的默认比较器字典。
    /// </summary>
    /// <typeparam name="TKey">键类型。</typeparam>
    /// <typeparam name="TValue">值类型。</typeparam>
    public static class DictionaryPool<TKey, TValue>
    {
        private static int _maxRetainedCapacity = 8192;
        private static readonly ObjectPool<Dictionary<TKey, TValue>> _pool = new(
            Create, onAllocate: null, reset: dictionary => dictionary.Clear(), onDiscard: null,
            maxPoolSize: 20, cacheOrder: PoolCacheOrder.Lifo, canRetain: CanRetain);

        /// <summary>登记会话清理，进入新运行会话时丢弃旧的空闲字典缓存。</summary>
        static DictionaryPool()
        {
            SingletonSessionState.RegisterSessionReset(_pool.ResetSession);
        }

        /// <summary>
        /// 空闲字典数量上限，默认 20；非正值表示不限。
        /// </summary>
        public static int MaxPoolSize
        {
            get => _pool.MaxPoolSize;
            set => _pool.MaxPoolSize = value;
        }

        /// <summary>
        /// 可保留的实际槽位上限，默认8192；-1不限，0仅保留零容量字典。
        /// 降低阈值清空当前闲置缓存；请求元素数与实际槽位数不同，不是字节预算。
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
        /// 从池中获取字典。
        /// </summary>
        public static Dictionary<TKey, TValue> Allocate()
        {
            return Allocate(0);
        }

        /// <summary>
        /// 从池中获取字典，冷热取得均保证实际容量不小于 capacity。
        /// </summary>
        /// <param name="capacity">本次借用的最小容量。</param>
        /// <exception cref="ArgumentOutOfRangeException">capacity 为负数。</exception>
        public static Dictionary<TKey, TValue> Allocate(int capacity) => Allocate(capacity, false, out _);

        /// <summary>作用域借用并保证最小容量，必须通过返回的租约结束，不接纳外部比较器。</summary>
        public static PoolLease<Dictionary<TKey, TValue>> Rent(out Dictionary<TKey, TValue> dictionary, int capacity = 0)
        {
            dictionary = Allocate(capacity, true, out var rental);
            return rental;
        }

        private static Dictionary<TKey, TValue> Allocate(int capacity, bool scoped, out PoolLease<Dictionary<TKey, TValue>> rental)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            rental = default;
            Dictionary<TKey, TValue> dictionary;
            if (scoped)
            {
                rental = _pool.Rent(out dictionary);
            }
            else
            {
                dictionary = _pool.Allocate();
            }
            try
            {
                dictionary.EnsureCapacity(capacity);
                return dictionary;
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
                        _pool.Discard(dictionary);
                    }
                }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        /// <summary>
        /// 结束字典借用：清空元素后按容量决定是否缓存。归还后不得继续使用字典。
        /// </summary>
        /// <param name="toRecycle">待回收字典，null 会被忽略。</param>
        /// <remarks>null 和重复归还不执行任何操作；缓存已满时仍清空元素并结束借用。</remarks>
        /// <exception cref="InvalidOperationException">字典不是从本池取得的。</exception>
        public static void Recycle(Dictionary<TKey, TValue> toRecycle)
        {
            _pool.Recycle(toRecycle);
        }

        /// <summary>
        /// 丢弃所有空闲缓存字典，不影响已借出的字典。
        /// </summary>
        public static void Clear()
        {
            _pool.Clear();
        }

        private static Dictionary<TKey, TValue> Create() => new();

        private static bool CanRetain(Dictionary<TKey, TValue> dictionary) =>
            _maxRetainedCapacity < 0 || dictionary.EnsureCapacity(0) <= _maxRetainedCapacity;
    }

    /// <summary>
    /// <see cref="DictionaryPool{TKey,TValue}"/> 的扩展入口。
    /// </summary>
    public static class DictionaryPoolExtensions
    {
        /// <summary>
        /// 将字典回收进 DictionaryPool。
        /// </summary>
        /// <param name="self">从同类型 DictionaryPool 借出的字典；归还后不得继续使用，null 会被忽略。</param>
        public static void RecycleToPool<TKey, TValue>(this Dictionary<TKey, TValue> self)
        {
            DictionaryPool<TKey, TValue>.Recycle(self);
        }
    }
}
