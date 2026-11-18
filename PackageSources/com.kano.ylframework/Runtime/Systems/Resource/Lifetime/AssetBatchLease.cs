using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace YLFramework.Resource
{
    /// <summary>一份官方批量引用；整批资源只在租约有效期间借读。</summary>
    public sealed class AssetBatchLease<T> : IDisposable, IResourceLease where T : Object
    {
        private readonly ResourceSystem _system;
        private IReadOnlyList<T> _assets;
        private AddressablesHandleOwner<IList<T>> _owner;

        ResourceSystem IResourceLease.System => _system;
        ResourceScope IResourceLease.Scope
        {
            get; set;
        }
        bool IResourceLease.IsSettled => IsDisposed;

        /// <summary>本份引用是否已归还。</summary>
        public bool IsDisposed => _owner == null;

        /// <summary>会话、官方引用和所有已加载对象仍有效。</summary>
        public bool IsValid
        {
            get
            {
                _system.CheckThread();
                if (_owner == null || !_system.IsSessionActive || !_owner.BorrowedHandle.IsValid())
                {
                    return false;
                }
                foreach (var asset in _assets)
                {
                    if (!asset)
                        return false;
                }
                return true;
            }
        }

        /// <summary>借读完整的不同资源集合，不承诺 Label 内部的排序。</summary>
        public IReadOnlyList<T> Assets
        {
            get
            {
                _system.CheckThread();
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(AssetBatchLease<T>));
                if (!IsValid)
                    throw new ResourceException(ResourceError.SessionUnavailable, "The required asset batch is no longer available.");
                return _assets;
            }
        }

        internal AssetBatchLease(ResourceSystem system, string[] keys, AddressablesHandleOwner<IList<T>> owner, T[] assets)
        {
            _system = system;
            _assets = Array.AsReadOnly(assets);
            _owner = owner;
            _system.RegisterLease(this, string.Join(", ", keys));
        }

        /// <summary>归还本份官方引用，不销毁共享对象或撤销其他消费者的引用。</summary>
        public void Dispose()
        {
            _system.CheckThread();
            var owner = _owner;
            if (owner == null)
                return;
            _owner = null;
            _assets = null;
            try
            {
                owner.Dispose();
            }
            finally { _system.UnregisterLease(this); }
        }

        ResourceRequest<bool> IResourceLease.CloseAsync()
        {
            try
            {
                Dispose();
                return ResourceSystem.Success(true);
            }
            catch (Exception error) { return ResourceSystem.Failure<bool>(error); }
        }
    }
}
