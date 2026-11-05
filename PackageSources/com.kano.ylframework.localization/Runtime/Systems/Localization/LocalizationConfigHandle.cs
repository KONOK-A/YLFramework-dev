using System;
using YLFramework.Resource;

namespace YLFramework.Localization
{
    /// <summary>持有一份本地化根登记及可选资源请求、租约；调用方在借读结束时释放句柄。</summary>
    public sealed class LocalizationConfigHandle : IDisposable
    {
        private ResourceRequest<AssetLease<MultiLanguageConfigs>> _request;
        private AssetLease<MultiLanguageConfigs> _lease;
        private MultiLanguageConfigs _config;
        /// <summary>借读当前根；尚未取得、已释放或所持租约失效时返回空引用。</summary>
        public MultiLanguageConfigs Config => !IsDisposed && (_lease == null || _lease.IsValid) ? _config : null;
        /// <summary>句柄是否已结束自身加载与登记责任。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>当前是否仍持有尚未完成的资源请求，不表示根已成功登记。</summary>
        public bool IsLoading => _request != null && !_request.IsDone;
        /// <summary>最近加载或登记失败所记录的异常；没有异常不等同于加载成功。</summary>
        public Exception Error
        {
            get; private set;
        }
        /// <summary>创建由管理器接管生命周期的空句柄。</summary>
        internal LocalizationConfigHandle()
        {
        }

        /// <summary>绑定借读根并交由管理器校验登记，登记失败时清除根引用。</summary>
        internal void Attach(MultiLanguageConfigs config)
        {
            this._config = config;
            try
            {
                LocalizationManager.Register(this);
            }
            catch
            {
                this._config = null;
                throw;
            }
        }

        /// <summary>取得根资源请求，并在完成时交割租约和登记；启动期间已释放则归还请求。</summary>
        internal void Load(ResourceSystem resources, ResourceAddress address)
        {
            var pending = resources.AcquireAsync<MultiLanguageConfigs>(address);
            if (IsDisposed)
            {
                pending.Dispose();
                return;
            }
            _request = pending;
            pending.Completed += Complete;
        }

        /// <summary>领取成功租约后尝试登记根，记录失败原因并结束本次资源请求持有。</summary>
        private void Complete(ResourceRequest<AssetLease<MultiLanguageConfigs>> completed)
        {
            if (ReferenceEquals(_request, completed))
            {
                _request = null;
            }

            try
            {
                if (IsDisposed || completed.IsDisposed)
                {
                    return;
                }

                if (completed.Status != ResourceRequestStatus.Succeeded)
                {
                    Error = completed.Error;
                    return;
                }
                _lease = completed.TakeResult();
                Attach(_lease.Asset);
            }
            catch (Exception error)
            {
                Error = error;
                _lease?.Dispose();
                _lease = null;
            }
            finally
            {
                completed.Dispose();
            }
        }

        /// <summary>取消本消费者请求、移除自身登记并归还所持租约，不直接销毁外部借入的根。</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            var pending = _request;
            _request = null;
            var held = _lease;
            _lease = null;
            try
            {
                pending?.Dispose();
                LocalizationManager.Unregister(this);
            }
            finally
            {
                _config = null;
                held?.Dispose();
            }
        }
    }
}
