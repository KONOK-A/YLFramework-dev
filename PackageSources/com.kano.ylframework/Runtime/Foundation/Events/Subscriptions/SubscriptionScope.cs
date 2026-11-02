using System;
using System.Collections.Generic;

namespace YLFramework.Events
{
    /// <summary>
    /// 只管理明确加入的订阅。Dispose 永久关闭；失败责任保留，可再次 Dispose 重试。
    /// 关闭后新增的句柄立即注销，失败时仍由本作用域持有。
    /// </summary>
    public sealed class SubscriptionScope : IDisposable
    {
        private readonly List<IUnregister> _subscriptions = new();
        private bool _isCleaning;

        /// <summary>是否已永久关闭。</summary>
        public bool IsDisposed
        {
            get; private set;
        }

        /// <summary>尚未完成清理的责任数量。</summary>
        public int Count => _subscriptions.Count;

        /// <summary>加入句柄；关闭后立即取消，不复活作用域。</summary>
        /// <remarks>不按句柄去重，重复加入会分别承担清理责任；传入 null 时忽略。</remarks>
        public void Add(IUnregister subscription)
        {
            if (subscription == null)
            {
                return;
            }

            if (!IsDisposed)
            {
                _subscriptions.Add(subscription);
                return;
            }
            try
            {
                subscription.Unregister();
            }
            catch
            {
                _subscriptions.Add(subscription);
                throw;
            }
        }

        /// <summary>尝试本轮全部取消，失败责任和新加入责任保留。</summary>
        public void Dispose()
        {
            IsDisposed = true;
            if (_isCleaning)
            {
                return;
            }

            _isCleaning = true;
            try
            {
                SubscriptionCleanup.ReleaseAll(_subscriptions, token => token.Unregister());
            }
            finally
            {
                _isCleaning = false;
            }
        }
    }
}
