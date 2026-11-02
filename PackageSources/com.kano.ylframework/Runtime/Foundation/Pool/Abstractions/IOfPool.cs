namespace YLFramework.Pooling
{
    /// <summary>
    /// Runtime 领域对象的生命周期接口，实现该接口不等于实例必定进入缓存。
    /// SafeObjectPool 取得时清除回收标记再调用 <see cref="OnAllocate"/>；
    /// 归还先进入内核转换态并标记 <see cref="IsRecycled"/>，再调用 <see cref="OnRecycle"/>。
    /// 回调失败时对象永久隔离并传播异常，不回滚为可重试状态。
    /// </summary>
    public interface IOfPool
    {
        /// <summary>
        /// 对象被分配出池时调用。默认空实现，可按需覆盖。
        /// </summary>
        public virtual void OnAllocate()
        {
        }

        /// <summary>
        /// 对象被回收时调用，用于清理状态以便复用。
        /// Safe 池满或关闭时仍调用一次；成功归还或失败隔离后，不会因重复归还再次触发。
        /// </summary>
        void OnRecycle();

        /// <summary>
        /// 领域可观察的回收状态，不表示真实池所有权或租用代次。
        /// </summary>
        bool IsRecycled
        {
            get; set;
        }
    }
}
