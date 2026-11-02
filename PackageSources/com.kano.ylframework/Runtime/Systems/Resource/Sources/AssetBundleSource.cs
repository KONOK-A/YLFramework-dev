using System;
using System.Linq;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>将正式快照条目加载为 Unity 资产，并以 Bundle 图租约保活其依赖闭包。</summary>
    /// <remarks>由 ResourceSystem 在主线程使用；快照须匹配运行平台与 Unity 版本，加密输入另受 Windows x64 CNG 限制。</remarks>
    internal sealed class AssetBundleSource : IResourceSource
    {
        private readonly ResourceSystem _system;
        /// <summary>本来源的固定 AssetBundle 身份。</summary>
        public ResourceSourceId Id => ResourceSourceId.AssetBundle;
        /// <summary>允许同步取得图与资产，但不能据此阻塞等待已经在途的异步生产。</summary>
        public bool SupportsSynchronousLoad => true;
        /// <summary>绑定唯一资源权威，不创建第二份 Bundle 图或缓存。</summary>
        internal AssetBundleSource(ResourceSystem system) => this._system = system;
        /// <summary>要求已验证快照及非场景描述，拒绝普通内存条目与独立显式依赖执行。</summary>
        public void Validate(ResourceCatalogEntry entry)
        {
            if (entry.Snapshot == null || entry.Descriptor == null || entry.Descriptor.Kind == "Scene" || entry.Dependencies.Count != 0)
            {
                throw new ArgumentException("AssetBundle sources require an explicit verified Catalog snapshot descriptor.");
            }
        }
        /// <summary>同步取得 Bundle 图租约并加载资产；成功将租约附在结果上，失败则归还。</summary>
        public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type)
        {
            using var request = _system.AcquireBundleRequest(entry.Address.ProjectId, entry.Descriptor.BundleId, true, false, new BundleLeaseOrigin(entry.Address));
            var lease = request.TakeResult();
            try
            {
                var result = entry.Descriptor.Kind == "SubAsset" ? Select(entry, type, lease.Bundle.LoadAssetWithSubAssets(entry.Descriptor.LoadName, type)) :
                    lease.Bundle.LoadAsset(entry.Descriptor.LoadName, type);
                _system.RequireHealthyBundle(lease);
                return new ResourceSourceResult(result, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        /// <summary>先等待 Bundle 图，再观察不可撤销的原生资产请求。</summary>
        public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => new Operation(_system, entry, type);
        /// <summary>不单独销毁资产；物理释放由结果附带的 Bundle 图保活责任统一决定。</summary>
        public void Release(UnityEngine.Object asset)
        {
        }

        /// <summary>按可赋值的编译类型与精确名称选择唯一原生子资产，不把逻辑 localId 当作 Unity 名称。</summary>
        private static UnityEngine.Object Select(ResourceCatalogEntry entry, Type type, UnityEngine.Object[] assets)
        {
            var matches = assets.Where(x => x && type.IsInstanceOfType(x) && x.name == entry.Descriptor.SubAssetName).ToArray();
            if (matches.Length != 1)
            {
                throw new ResourceException(ResourceError.LoadFailed,
                $"Subasset '{entry.Descriptor.SubAssetName}'/{type.FullName} has {matches.Length} native matches.", address: entry.Address, source: ResourceSourceId.AssetBundle);
            }

            return matches[0];
        }

        /// <summary>串接 Bundle 图取得与原生资产加载，并把图租约随来源结果移交。</summary>
        private sealed class Operation : ResourceSourceOperation
        {
            private readonly ResourceCatalogEntry _entry;
            private readonly Type _type;
            private ResourceRequest<BundleLease> _bundleRequest;
            private BundleLease _retention;
            private AssetBundleRequest _native;
            /// <summary>仅在原生资产请求尚未启动且操作未完成时允许取消图等待。</summary>
            public override bool SupportsCancellation => _native == null && !IsDone;
            /// <summary>将图取得与资产加载各映射到一半展示进度，不表示等比例时间或字节量。</summary>
            public override float Progress => _native != null ? 0.5f + _native.progress * 0.5f : (_bundleRequest?.Progress ?? 0) * 0.5f;
            /// <summary>保存条目与编译类型，并订阅当前资源系统的 Bundle 图请求。</summary>
            internal Operation(ResourceSystem system, ResourceCatalogEntry entry, Type type)
            {
                this._entry = entry;
                this._type = type;
                _bundleRequest = system.AcquireBundleRequest(entry.Address.ProjectId, entry.Descriptor.BundleId, false, false, new BundleLeaseOrigin(entry.Address));
                _bundleRequest.Completed += OnBundle;
            }
            /// <summary>接管图租约并检查输入健康状态，再启动原生资产加载；始终释放图请求外壳。</summary>
            private void OnBundle(ResourceRequest<BundleLease> request)
            {
                if (IsDone)
                {
                    return;
                }

                Exception failure = null;
                try
                {
                    _retention = request.TakeResult();
                    _retention.Record.Owner.RequireHealthyBundle(_retention);
                    _native = _entry.Descriptor.Kind == "SubAsset" ? _retention.Bundle.LoadAssetWithSubAssetsAsync(_entry.Descriptor.LoadName, _type) :
                        _retention.Bundle.LoadAssetAsync(_entry.Descriptor.LoadName, _type);
                    if (_native == null)
                    {
                        throw new InvalidOperationException("Unity did not return an AssetBundleRequest.");
                    }

                    _native.completed += OnAsset;
                    if (_native.isDone)
                    {
                        OnAsset(_native);
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    request.Dispose();
                    _bundleRequest = null;
                }
                if (failure != null)
                {
                    Finish(null, failure);
                }
            }
            /// <summary>解除原生订阅，读取资产并再次核对依赖闭包健康状态后交割结果。</summary>
            private void OnAsset(AsyncOperation operation)
            {
                if (IsDone)
                {
                    return;
                }

                _native.completed -= OnAsset;
                UnityEngine.Object asset = null;
                Exception error = null;
                try
                {
                    asset = _entry.Descriptor.Kind == "SubAsset" ? Select(_entry, _type, _native.allAssets) : _native.asset;
                    _retention.Record.Owner.RequireHealthyBundle(_retention);
                }
                catch (Exception cause)
                {
                    error = cause;
                }
                Finish(asset, error);
            }
            /// <summary>先移出操作持有的图租约，再随成功或失败结果交给共享记录统一结算。</summary>
            private void Finish(UnityEngine.Object asset, Exception error)
            {
                var owned = _retention;
                _retention = null;
                Complete(asset, error, owned);
            }
            /// <summary>只取消尚未进入原生资产加载的图请求；原生阶段继续保活到完成。</summary>
            public override void Cancel()
            {
                // 已启动的 AssetBundleRequest 无法撤销；保活必须持续到 OnAsset。
                if (_native != null || IsDone)
                {
                    return;
                }

                _bundleRequest?.Cancel();
            }
            /// <summary>在基类确认完成后释放原生观察引用，不回收已交割的图租约。</summary>
            public override void Dispose()
            {
                base.Dispose();
                _native = null;
            }
        }
    }
}
