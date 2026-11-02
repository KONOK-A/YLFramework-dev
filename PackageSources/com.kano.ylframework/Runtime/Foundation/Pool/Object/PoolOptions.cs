namespace YLFramework.Pooling
{
    /// <summary>空闲对象的分配顺序。</summary>
    public enum PoolCacheOrder
    {
        /// <summary>先进先出（默认）。</summary>
        Fifo,
        /// <summary>后进先出。</summary>
        Lifo
    }

    /// <summary>目标空闲数量预热的清理方式。</summary>
    public enum PoolPrewarmMode
    {
        /// <summary>执行回收 Reset；Safe 入口执行领域 OnRecycle。</summary>
        Recycle,
        /// <summary>只登记并缓存新对象，不执行取得和回收回调。</summary>
        Raw
    }
}
