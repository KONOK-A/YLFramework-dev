namespace YLFramework.Pooling
{
    /// <summary>
    /// 让对象自己掌握回收入口的接口：实现方决定回收进哪个池（或不回收）。
    /// 各实现语义不同，例如事件对象回收进 SafeObjectPool，
    /// 而 MappedProperty 的 RecycleToCache 语义等价于 Dispose（并不入池）。
    /// </summary>
    public interface IPoolType
    {
        /// <summary>
        /// 将当前对象回收（具体含义由实现方决定）。
        /// </summary>
        void RecycleToCache();
    }
}
