using System;

namespace YLFramework.Pooling
{
    /// <summary>
    /// 一次带代次的借用。Dispose 正常回收，Discard 清理后永久退出缓存；复制件只能共同结束一次。
    /// 不阻止原始对象引用逃逸或结束后的写入，不提供线程安全。转存为 IDisposable 会装箱。
    /// </summary>
    /// <typeparam name="T">由原池持有借还身份的引用对象。</typeparam>
    public readonly struct PoolLease<T> : IDisposable where T : class
    {
        private readonly ObjectPool<T> _pool;
        private readonly T _item;
        private readonly ObjectPool<T>.Entry _entry;
        private readonly ulong _generation;

        internal PoolLease(ObjectPool<T> pool, T item, ObjectPool<T>.Entry entry, ulong generation)
        {
            _pool = pool;
            _item = item;
            _entry = entry;
            _generation = generation;
        }

        /// <summary>结束当前借用，清理后尝试缓存；默认、重复或旧代次调用无副作用。</summary>
        public void Dispose() => _pool?.EndLease(_item, _entry, _generation, true);

        /// <summary>结束当前借用并永久丢弃；仍执行原池的清理，不能再次结束或重放失败清理。</summary>
        public void Discard() => _pool?.EndLease(_item, _entry, _generation, false);
    }
}
