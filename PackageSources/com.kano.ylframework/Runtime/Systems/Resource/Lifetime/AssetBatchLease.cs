using System;
using System.Collections.Generic;
using System.Threading;

namespace YLFramework.Resource
{
    /// <summary>typed 批量/Group 的拥有型结果。每个成员是独立正式 Lease，Dispose 只释放本批责任；Errors 保留可选成员失败。</summary>
    /// <typeparam name="T">本批租约借读的 Unity 资源类型。</typeparam>
    public sealed class AssetBatchLease<T> : IDisposable where T : UnityEngine.Object
    {
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        /// <summary>本批持有的成功成员租约；读取集合不转移单项责任。</summary>
        public IReadOnlyList<AssetLease<T>> Items
        {
            get;
        }
        /// <summary>未阻止批次成功的可选成员错误，不包含本批释放时发生的异常。</summary>
        public IReadOnlyList<ResourceException> Errors
        {
            get;
        }
        /// <summary>本批是否已经开始归还；清理失败也不会恢复持有身份。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>复制成员和错误列表，接管传入租约而不增加资源引用计数。</summary>
        internal AssetBatchLease(IEnumerable<AssetLease<T>> items, IEnumerable<ResourceException> errors)
        {
            Items = ResourceCatalog.Copy(items);
            Errors = ResourceCatalog.Copy(errors);
        }
        /// <summary>在接受批次的线程逐项归还自有租约，继续清理后再汇总错误。</summary>
        /// <exception cref="InvalidOperationException">未在接受批次的 Unity 主线程调用。</exception>
        /// <exception cref="AggregateException">一个或多个成员归还失败。</exception>
        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("Resource batches must be used on their accepting Unity thread.");
            }

            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            List<Exception> errors = null;
            foreach (var item in Items)
            {
                try
                {
                    item.Dispose();
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
            }

            if (errors != null)
            {
                throw new AggregateException("Resource batch cleanup failed.", errors);
            }
        }
    }
}
