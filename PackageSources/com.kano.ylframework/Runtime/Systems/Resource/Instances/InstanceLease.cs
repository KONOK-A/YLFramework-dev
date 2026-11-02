using System;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>一次 checkout 的独立身份。归还后不可再使用对象；旧 Lease 不能操作下一次 checkout。</summary>
    /// <remarks>借读和归还均须在 Unity 主线程执行；请求销毁不等于真实销毁，期间的资源保活仍由原缓存负责。</remarks>
    public sealed class InstanceLease : IDisposable
    {
        private InstanceCache.Entry _entry;
        private readonly InstanceCache _cache;
        /// <summary>为缓存条目建立本次独立借出身份，不复用上次租约对象。</summary>
        internal InstanceLease(InstanceCache cache, InstanceCache.Entry entry)
        {
            this._cache = cache;
            this._entry = entry;
            entry.Checkout = this;
        }

        /// <summary>是否已主动结束本次借出身份；未归还也可能因外部销毁或会话退出而无效。</summary>
        public bool IsDisposed => _entry == null;
        /// <summary>条目是否仍属于本次借出，实例存活且所属资源会话有效。</summary>
        public bool IsValid => _entry != null && ReferenceEquals(_entry.Checkout, this) && _entry.Object && _cache.Owner.IsSessionActive;

        /// <summary>只在当前 checkout 有效时借读；不得自行归还到其他池或在 Return 后保留使用权。</summary>
        /// <exception cref="ObjectDisposedException">本次借出已经归还或丢弃。</exception>
        /// <exception cref="InvalidOperationException">未在 Unity 主线程读取。</exception>
        /// <exception cref="ResourceException">本次借出、实例或资源会话已经无效。</exception>
        public GameObject GameObject
        {
            get
            {
                _cache.Owner.CheckThread();
                if (_entry == null)
                {
                    throw new ObjectDisposedException(nameof(InstanceLease));
                }

                if (!IsValid)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable, "The instance checkout is no longer valid.");
                }

                return _entry.Object;
            }
        }

        /// <summary>结束本次 checkout；正常实例回原桶，已退休桶或超容量实例销毁。</summary>
        public void Return() => Release(false);
        /// <summary>结束本次 checkout 并销毁损坏/失败实例，绝不进入空闲缓存。</summary>
        public void Discard() => Release(true);
        /// <summary>按 Return 规则结束本次借出，不额外销毁仍可缓存的实例。</summary>
        public void Dispose() => Return();

        /// <summary>先撤销本租约身份，再向原缓存归还或丢弃仍属于自己的条目。</summary>
        private void Release(bool discard)
        {
            _cache.Owner.CheckThread();
            var owned = _entry;
            if (owned == null)
            {
                return;
            }

            _entry = null;
            if (!ReferenceEquals(owned.Checkout, this))
            {
                return;
            }

            owned.Checkout = null;
            _cache.Return(owned, discard);
        }
    }
}
