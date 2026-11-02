using System;
using YLFramework.Pooling;

namespace YLFramework.Actions
{
    // YL 负责通用缓存，Action 只保留回收重置策略与静态池所有权。
    internal sealed class ActionPool<T> where T : class
    {
        private readonly ObjectPool<T> _pool;

        // LIFO 空闲缓存；每次回收都调用可选重置回调；容量不设上限（MaxPoolSize=0 表示无限）；
        // 预热使用 Raw 模式跳过重置回调，因为新建实例无需重置。
        internal ActionPool(Func<T> factory, Action<T> reset = null, int initCount = 0,
            Action<T> onAllocate = null)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            _pool = new ObjectPool<T>(factory, onAllocate: onAllocate, reset: reset,
                maxPoolSize: 0, cacheOrder: PoolCacheOrder.Lifo);
            _pool.Prewarm(initCount, PoolPrewarmMode.Raw);
        }

        internal T Allocate() => _pool.Allocate();

        internal bool Recycle(T item) => _pool.Recycle(item);

        // 不调用 Deinit；动作的领域清理由延迟项入队前负责。
        internal void Discard(T item) => _pool.Discard(item);
    }
}
