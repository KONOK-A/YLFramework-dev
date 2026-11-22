using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>不可复用的一次实例身份；旧 Lease 不能操作下一次 checkout。</summary>
    public sealed class InstanceLease : IDisposable, IResourceLease
    {
        private readonly InstanceCache _cache;
        private List<ResourceRequest<bool>> _closing;
        private InstanceCache.Entry _entry;
        private bool _isSettled;

        internal InstanceLease(InstanceCache cache, InstanceCache.Entry entry)
        {
            _cache = cache;
            _entry = entry;
            entry.Checkout = this;
            try
            {
                cache.Owner.RegisterLease(this, cache.Key);
            }
            catch
            {
                entry.Checkout = null;
                _entry = null;
                throw;
            }
        }

        internal ResourceSystem System => _cache.Owner;
        internal ResourceScope Scope
        {
            get; set;
        }
        ResourceSystem IResourceLease.System => System;
        ResourceScope IResourceLease.Scope
        {
            get => Scope; set => Scope = value;
        }
        bool IResourceLease.IsSettled => _isSettled;

        /// <summary>已结束本轮借用；为 true 不表示 Unity 已执行延迟销毁。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>当前身份仍借出且对象和执行会话存活；Stopping 不撤销现有借读。</summary>
        public bool IsValid => !IsDisposed && _entry != null && ReferenceEquals(_entry.Checkout, this) &&
            _entry.Object && !_entry.IsDestroying && System.IsSessionActive;

        /// <summary>借读本次对象；不得在归还后继续使用或送入其他池。</summary>
        public GameObject GameObject
        {
            get
            {
                System.CheckThread();
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(InstanceLease));
                }
                if (!IsValid)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable,
                        "The instance checkout is no longer valid.");
                }
                return _entry.Object;
            }
        }

        /// <summary>Rent 归池，Instantiate 销毁；退休桶和 Stopping 期间的归还一律销毁。</summary>
        public void Return() => Release(false);
        /// <summary>丢弃损坏或初始化失败的对象，绝不入池。</summary>
        public void Discard() => Release(true);
        /// <summary>发起 Return，不阻塞等待 Unity 销毁。</summary>
        public void Dispose() => Return();

        /// <summary>发起同一归还，每个调用者独立观察；销毁路径只在实际死亡后成功。</summary>
        public ResourceRequest<bool> CloseAsync()
        {
            System.CheckThread();
            try
            {
                Return();
            }
            catch (Exception error)
            {
                return ResourceSystem.Failure<bool>(error);
            }
            if (_isSettled)
            {
                return ResourceSystem.Success(true);
            }
            var request = new ResourceRequest<bool>();
            _closing ??= new List<ResourceRequest<bool>>();
            _closing.Add(request);
            request.Attach(() => _closing.Remove(request), null);
            return request;
        }

        private void Release(bool discard)
        {
            System.CheckThread();
            if (IsDisposed)
            {
                return;
            }
            IsDisposed = true;
            System.MakeInternal(this);
            var entry = _entry;
            if (entry == null || !ReferenceEquals(entry.Checkout, this))
            {
                Settle();
                return;
            }
            entry.Checkout = null;
            _cache.Return(entry, this, discard);
        }

        internal void Settle()
        {
            if (_isSettled)
            {
                return;
            }
            _isSettled = true;
            IsDisposed = true;
            _entry = null;
            System.UnregisterLease(this);
            if (_closing == null)
            {
                return;
            }
            var closing = _closing.ToArray();
            _closing = null;
            foreach (var request in closing)
            {
                request.SetSuccess(true);
            }
            foreach (var request in closing)
            {
                request.NotifyCompletion();
            }
        }
    }
}
