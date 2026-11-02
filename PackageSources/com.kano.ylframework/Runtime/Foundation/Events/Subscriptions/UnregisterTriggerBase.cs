using System;
using UnityEngine;

namespace YLFramework.Events
{
    /// <summary>
    /// 生命周期注销触发器基类：收集需要随某个生命周期自动注销的句柄，
    /// 由子类在 OnDestroy / OnDisable / 场景卸载时调用 <see cref="TriggerUnregisters"/>。
    /// 每轮全部尝试，失败责任与新增句柄留到下次显式清理；销毁后新增句柄立即取消。
    /// </summary>
    public abstract class UnregisterTriggerBase : MonoBehaviour
    {
        private readonly SubscriptionSet<IUnregister> _subscriptions = new();
        private bool _isCleaning;
        private bool _isClosed;

        /// <summary>宿主已结束；不能再托管新的存活订阅。</summary>
        protected bool IsClosed => _isClosed || !this;

        /// <summary>
        /// 加入一个注销句柄，生命周期触发时统一注销；同一句柄重复加入只保留一份（去重）。
        /// </summary>
        /// <param name="subscription">待托管的注销句柄。</param>
        /// <returns>传入的句柄，便于链式使用。</returns>
        public IUnregister AddUnregister(IUnregister subscription)
        {
            if (subscription == null)
            {
                return null;
            }

            if (IsClosed)
            {
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
            else
            {
                _subscriptions.Add(subscription);
            }

            return subscription;
        }

        /// <summary>
        /// 从容器中移除一个注销句柄（仅解除托管，不会触发注销）。
        /// </summary>
        /// <param name="subscription">要移除的注销句柄。</param>
        public virtual void RemoveUnregister(IUnregister subscription)
        {
            _subscriptions.Remove(subscription);
        }

        /// <summary>
        /// 尝试本轮全部注销器，失败责任保留供显式重试，结束后抛出原异常或聚合异常。
        /// </summary>
        public void Unregister()
        {
            TriggerUnregisters();
        }

        /// <summary>
        /// 使用共享必要清理机制；同轮重入不重试失败项，也不消耗新加入的责任。
        /// </summary>
        protected virtual void TriggerUnregisters()
        {
            if (_isCleaning)
            {
                return;
            }

            _isCleaning = true;
            try
            {
                _subscriptions.ReleaseAll(token => token.Unregister());
            }
            finally
            {
                _isCleaning = false;
            }
        }

        /// <summary>Unity 消息边界：尝试全部责任后集中记录异常，不声称失败项已成功取消。</summary>
        protected void TriggerUnregistersAndLog()
        {
            try
            {
                TriggerUnregisters();
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
            }
        }

        /// <summary>终结托管，并收尾所有 pending 责任；Unity 仅保证曾 active 的对象收到 OnDestroy。</summary>
        protected virtual void OnDestroy()
        {
            _isClosed = true;
            TriggerUnregistersAndLog();
        }
    }
}
