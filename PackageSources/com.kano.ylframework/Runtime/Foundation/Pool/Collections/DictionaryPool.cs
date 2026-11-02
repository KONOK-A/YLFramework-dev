using System;
using System.Collections.Generic;
using YLFramework.Singletons;

namespace YLFramework.Pooling
{
    /// <summary>
    /// Dictionary&lt;TKey, TValue&gt; 的静态对象池。
    /// 回收时只清空元素、保留容量。
    /// 非线程安全，只接受从本池取得的默认比较器字典。
    /// </summary>
    /// <typeparam name="TKey">键类型。</typeparam>
    /// <typeparam name="TValue">值类型。</typeparam>
    public static class DictionaryPool<TKey, TValue>
    {
        private static int _createCapacity;
        private static readonly ObjectPool<Dictionary<TKey, TValue>> _pool = new(
            Create, reset: dictionary => dictionary.Clear(), cacheOrder: PoolCacheOrder.Lifo);

        /// <summary>登记会话清理，进入新运行会话时丢弃旧的空闲字典缓存。</summary>
        static DictionaryPool()
        {
            SingletonSessionState.RegisterSessionReset(Clear);
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
        /// 从池中获取字典。
        /// </summary>
        public static Dictionary<TKey, TValue> Allocate()
        {
            return Allocate(0);
        }

        /// <summary>
        /// 从池中获取字典，池为空时按 capacity 预分配容量。
        /// 注意：池中已有缓存时返回现有字典，不保证容量不小于 capacity。
        /// </summary>
        /// <param name="capacity">池为空时的初始容量。</param>
        /// <exception cref="ArgumentOutOfRangeException">capacity 为负数。</exception>
        public static Dictionary<TKey, TValue> Allocate(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            // 请求容量只供稳定工厂冷创建使用，热租不创建捕获闭包或扩容字典。
            var previousCapacity = _createCapacity;
            _createCapacity = capacity;
            try
            {
                return _pool.Allocate();
            }
            finally
            {
                _createCapacity = previousCapacity;
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

        /// <summary>按当前请求的初始容量冷创建字典，使用默认键比较器。</summary>
        private static Dictionary<TKey, TValue> Create() => new(_createCapacity);
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
