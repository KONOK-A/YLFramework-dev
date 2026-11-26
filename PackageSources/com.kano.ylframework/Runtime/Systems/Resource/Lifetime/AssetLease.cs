using System;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace YLFramework.Resource
{
    /// <summary>一份独立官方资产引用；Asset 仅借读，Retain 才产生新的所有权。</summary>
    public sealed class AssetLease<T> : IDisposable, IResourceAssetLease where T : UnityEngine.Object
    {
        private AddressablesHandleOwner<T> _owner;
        private readonly IResourceLocation _location;
        internal ResourceSystem System
        {
            get;
        }
        ResourceSystem IResourceLease.System => System;
        ResourceScope IResourceLease.Scope
        {
            get; set;
        }
        internal ResourceScope Scope => ((IResourceLease)this).Scope;
        Type IResourceAssetLease.AssetType => typeof(T);

        /// <summary>冻结后的官方 RuntimeKey，不按别名或裸对象猜测释放责任。</summary>
        public string Key
        {
            get;
        }
        /// <summary>明确的业务责任标签；为空不推断实际业务。</summary>
        public string OwnerLabel
        {
            get;
        }
        /// <summary>本份引用是否已归还。</summary>
        public bool IsDisposed => _owner == null;
        bool IResourceLease.IsSettled => IsDisposed;
        /// <summary>会话和官方引用仍有效，且 Unity 对象尚未被外部销毁。</summary>
        public bool IsValid => _owner != null && System.IsSessionActive && _owner.BorrowedHandle.IsValid() && _owner.BorrowedHandle.Result;

        /// <summary>在本租约有效期间借读，不创建副本。</summary>
        public T Asset
        {
            get
            {
                System.CheckThread();
                if (_owner == null)
                    throw new ObjectDisposedException(nameof(AssetLease<T>));
                if (!IsValid)
                    throw new ResourceException(ResourceError.SessionUnavailable, "The leased asset is no longer available: " + Key);
                return _owner.BorrowedHandle.Result;
            }
        }

        internal AssetLease(ResourceSystem system, string key, AddressablesHandleOwner<T> owner,
            IResourceLocation location, string ownerLabel = null)
        {
            System = system;
            Key = key;
            OwnerLabel = ownerLabel;
            _owner = owner;
            _location = location;
            try
            {
                system.RegisterLease(this, key, ownerLabel);
                system.OnImageLeaseAdded(location, owner.BorrowedHandle);
            }
            catch
            {
                _owner = null;
                system.UnregisterLease(this);
                // 创建失败时，调用方仍负责归还传入的独立引用。
                throw;
            }
        }

        /// <summary>取得独立引用，不把 Scope 或池的所有者身份复制给新租约。</summary>
        /// <param name="ownerLabel">新业务责任标签。</param>
        /// <returns>调用方负责归还的独立租约。</returns>
        public AssetLease<T> Retain(string ownerLabel = null)
        {
            System.RequireAvailable();
            _ = Asset;
            var retained = _owner.Retain();
            try
            {
                return new AssetLease<T>(System, Key, retained, _location, ownerLabel);
            }
            catch
            {
                retained.Dispose();
                throw;
            }
        }

        /// <summary>幂等归还本份引用，不销毁共享 Unity 对象。</summary>
        public void Dispose()
        {
            System.CheckThread();
            var owner = _owner;
            if (owner == null)
                return;
            _owner = null;
            try
            {
                System.OnImageLeaseReleased(_location);
            }
            finally
            {
                try
                {
                    owner.Dispose();
                }
                finally
                {
                    System.UnregisterLease(this);
                }
            }
        }

        ResourceRequest<bool> IResourceLease.CloseAsync()
        {
            try
            {
                Dispose();
                return ResourceSystem.Success(true);
            }
            catch (Exception error)
            {
                return ResourceSystem.Failure<bool>(error);
            }
        }
    }
}
