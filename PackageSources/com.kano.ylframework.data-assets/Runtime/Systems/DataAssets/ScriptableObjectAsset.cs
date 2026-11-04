using System;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.DataAssets
{
    /// <summary>owner持有至使用结束。Dispose取消本请求、移除本登记并归还根Lease；Asset及子条目均为借读。</summary>
    /// <typeparam name="T">取得后供借读的共享配置类型。</typeparam>
    public sealed class ScriptableObjectAsset<T> : CustomYieldInstruction, IDisposable where T : ScriptableObject
    {
        private ResourceRequest<AssetLease<T>> _request;
        private AssetLease<T> _lease;
        private Action<T> _loaded;
        private Action _released;

        /// <summary>本次加载的状态；资源取得成功但登记回调失败时记为失败。</summary>
        public ResourceRequestStatus Status { get; private set; } = ResourceRequestStatus.Pending;
        /// <summary>资源请求或登记回调记录的失败原因。</summary>
        public Exception Error
        {
            get; private set;
        }
        /// <summary>本句柄是否已结束所有权，结束后不能再通过它访问资产。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>加载是否已成功、失败或取消；完成不代表资产仍可用。</summary>
        public bool IsDone => Status != ResourceRequestStatus.Pending;
        /// <summary>句柄未释放且所持资源租约仍有效时为真。</summary>
        public bool IsValid => !IsDisposed && _lease != null && _lease.IsValid;
        /// <summary>协程仅在加载未完成时继续等待，等待结束不会释放句柄。</summary>
        public override bool keepWaiting => !IsDone;
        /// <summary>借读成功取得的共享资产，不创建运行时副本。</summary>
        /// <exception cref="ObjectDisposedException">句柄已释放。</exception>
        /// <exception cref="InvalidOperationException">加载尚未成功。</exception>
        public T Asset
        {
            get
            {
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(GetType().Name);
                }

                if (Status != ResourceRequestStatus.Succeeded)
                {
                    throw new InvalidOperationException("The ScriptableObject load has not succeeded.", Error);
                }

                return _lease.Asset;
            }
        }

        /// <summary>创建尚未启动、等待显式资源入口的加载句柄。</summary>
        internal ScriptableObjectAsset()
        {
        }

        /// <summary>登记生命周期责任并启动资源请求，成功取得租约后调用登记回调。</summary>
        internal void Start(ResourceSystem resource, ResourceAddress address, Action<T> onLoaded = null, Action onReleased = null)
        {
            if (resource == null)
            {
                throw new ArgumentNullException(nameof(resource));
            }

            _loaded = onLoaded;
            _released = onReleased;
            DataAssetLifetime.Track(this);
            try
            {
                var pending = resource.AcquireAsync<T>(address);
                // 资源加载触发SO.OnEnable时，owner可能已重入关闭；不能再订阅并遗留未领取结果。
                if (IsDisposed)
                {
                    pending.Dispose();
                    return;
                }
                _request = pending;
                _request.Completed += Complete;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>领取成功结果并执行登记，记录失败后结束本次请求持有。</summary>
        private void Complete(ResourceRequest<AssetLease<T>> completed)
        {
            if (IsDisposed)
            {
                return;
            }

            Status = completed.Status;
            Error = completed.Error;
            try
            {
                if (Status == ResourceRequestStatus.Succeeded)
                {
                    _lease = completed.TakeResult();
                    _loaded?.Invoke(_lease.Asset);
                }
            }
            catch (Exception error)
            {
                // 登记校验失败仍须归还已领取的Lease，不能留半成功缓存。
                Error = error;
                Status = ResourceRequestStatus.Failed;
                _lease?.Dispose();
                _lease = null;
            }
            finally
            {
                _loaded = null;
                completed.Completed -= Complete;
                completed.Dispose();
                _request = null;
            }
        }

        /// <summary>取消本消费者请求、移除关联登记并归还已持有租约；重复调用不再执行释放。</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            if (!IsDone)
            {
                Status = ResourceRequestStatus.Canceled;
            }

            var pending = _request;
            _request = null;
            var onReleased = _released;
            _released = null;
            _loaded = null;
            try
            {
                if (pending != null)
                {
                    pending.Completed -= Complete;
                    pending.Dispose();
                }
                onReleased?.Invoke();
            }
            finally
            {
                _lease?.Dispose();
                _lease = null;
                DataAssetLifetime.Untrack(this);
            }
        }
    }
}
