using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private readonly Queue<Func<bool>> _catalogControls = new();
        private bool _catalogControlRunning;
        private bool _catalogApplyInFlight;
        private bool _startupCatalogUpdatesPrepared;

        /// <summary>在官方 InitializeAsync 之前验证正式 runtime data；默认不取得受控更新权限。</summary>
        /// <remarks>
        /// 显式启用代表应用组合根承诺本方最先组织初始化，并协调外部 Init/Check/Update；
        /// 公开 API 无法证明外部尚未完成的初始化不存在，本方法不反射或猜测全局引用计数。
        /// </remarks>
        private IEnumerator PrepareCatalogUpdatePolicy(ResourceManager manager)
        {
            CheckThread();
            _startupCatalogUpdatesPrepared = false;
            if (!Configuration.EnableStartupCatalogUpdates)
                yield break;
            if (_businessStarted)
                throw new ResourceException(ResourceError.Busy, "The startup catalog update window cannot reopen after a business asset load.");
            if (Addressables.ResourceLocators.Any())
                throw new ResourceException(ResourceError.ConfigurationConflict, "Existing Addressables locators permit load-only coexistence, not controlled catalog initialization.");

            RequireVerifiableRuntimeDataSource();
            var path = CatalogRuntimeDataPath();
            var provider = new JsonAssetProvider();
            provider.Initialize("YLFramework.Resource.RuntimeDataPolicy." + Guid.NewGuid().ToString("N"), null);
            var location = new ResourceLocationBase("YL runtime catalog policy", path, provider.ProviderId, typeof(ResourceManagerRuntimeData));
            location.Data = new ProviderLoadRequestOptions { IgnoreFailures = false };
            AddressablesHandleOwner<ResourceManagerRuntimeData> pin = null;
            manager.ResourceProviders.Add(provider);
            try
            {
                var handle = manager.ProvideResource<ResourceManagerRuntimeData>(location);
                try
                {
                    pin = AddressablesHandleOwner<ResourceManagerRuntimeData>.AcquireReference(manager, handle);
                }
                finally
                {
                    manager.Release(handle);
                }
                var completionObserved = false;
                handle.Completed += _ => completionObserved = true;
                while (!completionObserved)
                    yield return null;
                if (State != ResourceSystemState.Initializing || !IsSessionActive)
                {
                    yield break;
                }

                var completed = pin.BorrowedHandle;
                if (completed.Status != AsyncOperationStatus.Succeeded || completed.Result == null)
                    throw new ResourceException(ResourceError.InvalidConfiguration, "The official Addressables runtime data could not be verified for controlled startup updates.", completed.OperationException);
                if (!completed.Result.DisableCatalogUpdateOnStartup)
                    throw new ResourceException(ResourceError.InvalidConfiguration, "Controlled startup updates require ResourceManagerRuntimeData.DisableCatalogUpdateOnStartup=true in the actual settings.json.");
                if (Addressables.ResourceLocators.Any())
                    throw new ResourceException(ResourceError.ConfigurationConflict, "Addressables locators appeared during policy verification; controlled initialization requires external coordination.");
                RequireVerifiableRuntimeDataSource();
                if (!string.Equals(path, CatalogRuntimeDataPath(), StringComparison.Ordinal))
                    throw new ResourceException(ResourceError.ConfigurationConflict, "The official runtime data path changed during policy verification.");
                _startupCatalogUpdatesPrepared = true;
            }
            finally
            {
                try
                {
                    pin?.Dispose();
                }
                finally
                {
                    // 只移除本次唯一 ID 的官方 Provider，绝不替换或清理外部 Provider。
                    manager.ResourceProviders.Remove(provider);
                }
            }
        }

        private static void RequireVerifiableRuntimeDataSource()
        {
            // 官方会把完整的 IResourceLocation 传给全局回调，不能用未转换的路径证明两次读取相同。
            if (Addressables.InternalIdTransformFunc != null || Addressables.WebRequestOverride != null ||
                Addressables.ResourceManager.ResourceProviders.Any(value => value.ProviderId == typeof(JsonAssetProvider).FullName))
                throw new ResourceException(ResourceError.ConfigurationConflict,
                    "Controlled startup cannot verify the official runtime data with an external ID transform, web request override or runtime JSON provider.");
        }

        private static string CatalogRuntimeDataPath()
        {
            // 与两个受支持版本的 Addressables.InitializeAsync(bool) 使用完全相同的路径规则。
            var path = Addressables.RuntimePath + "/settings.json";
#if UNITY_EDITOR
            path = PlayerPrefs.GetString(Addressables.kAddressablesRuntimeDataPath, path);
#endif
            return Addressables.ResolveInternalId(path);
        }

        /// <summary>官方依赖下载大小；精确 keys 在调用时快照，不构成业务资产首载。</summary>
        public ResourceRequest<long> GetDownloadSizeAsync(IEnumerable<string> keys, CancellationToken cancellation = default)
        {
            CheckThread();
            var request = Track(new ResourceRequest<long>());
            try
            {
                RequireAvailable();
                var frozen = FreezeDeliveryKeys(keys);
                request.DiagnosticIdentity.Key = string.Join(", ", frozen);
                BindCancellation(request, cancellation);
                if (!request.IsDone)
                    Run(ObserveDelivery(request, () => Addressables.GetDownloadSizeAsync(frozen), handle => handle.Result, true), error => Fail(request, error));
            }
            catch (Exception error)
            {
                Fail(request, error);
            }
            return request;
        }

        /// <summary>按官方 Union 下载依赖，显式归还本方 Handle，不取得业务资产 Lease。</summary>
        /// <remarks>取消仅退出本消费者等待；已经启动的官方操作仍排空，不能承诺网络请求中止。</remarks>
        public ResourceRequest<bool> DownloadDependenciesAsync(IEnumerable<string> keys, CancellationToken cancellation = default)
        {
            CheckThread();
            var request = Track(new ResourceRequest<bool>());
            try
            {
                RequireAvailable();
                var frozen = FreezeDeliveryKeys(keys);
                request.DiagnosticIdentity.Key = string.Join(", ", frozen);
                BindCancellation(request, cancellation);
                if (!request.IsDone)
                    Run(ObserveDelivery(request, () => Addressables.DownloadDependenciesAsync(frozen, Addressables.MergeMode.Union, false)
                        .Convert<IList<IAssetBundleResource>>(), _ => true, true), error => Fail(request, error));
            }
            catch (Exception error)
            {
                Fail(request, error);
            }
            return request;
        }

        /// <summary>串行执行本方官方 Catalog 检查，交付只读 ID 快照，不要求启用 Apply。</summary>
        /// <remarks>官方检查尝试读取的远端 Hash 缺失时报错，不当作无更新。外部 Addressables 控制操作仍须由应用协调。</remarks>
        public ResourceRequest<IReadOnlyList<string>> CheckForCatalogUpdatesAsync(CancellationToken cancellation = default)
        {
            CheckThread();
            var request = Track(new ResourceRequest<IReadOnlyList<string>>());
            try
            {
                RequireAvailable();
                BindCancellation(request, cancellation);
                QueueCatalogControl(request, () => Addressables.CheckForCatalogUpdates(true), SnapshotCatalogCheck);
            }
            catch (Exception error)
            {
                Fail(request, error);
            }
            return request;
        }

        /// <summary>仅更新调用方明确列出的官方 Catalog ID；不默认更新全部，不清理 Bundle cache。</summary>
        /// <returns>至少一个官方 Catalog 的登记 Hash 改变或自定义 Locator 被替换时为 true；无变化时为 false。</returns>
        /// <remarks>
        /// 须显式启用并在官方 Init 前验证自动检查已禁用；仅业务首载前可执行。
        /// 远端 Hash 缺失或与官方登记版本不符时报错；不验证下载字节，也不撤销 Addressables 已完成的变更。
        /// </remarks>
        public ResourceRequest<bool> ApplyCatalogUpdatesAsync(IEnumerable<string> catalogIds, CancellationToken cancellation = default)
        {
            CheckThread();
            var request = Track(new ResourceRequest<bool>());
            try
            {
                RequireAvailable();
                RequireStartupCatalogUpdateWindow();
                var frozen = FreezeDeliveryKeys(catalogIds);
                request.DiagnosticIdentity.Key = string.Join(", ", frozen);
                BindCancellation(request, cancellation);
                var targets = new (ResourceLocatorInfo Info, IResourceLocator Before, string LocalHash, bool HasRemoteHash)[frozen.Length];
                QueueCatalogControl(request, () =>
                {
                    // 入队之后仍可能发生业务首载或 Stop，不能只在公开入口检查一次。
                    RequireStartupCatalogUpdateWindow();
                    for (var i = 0; i < frozen.Length; i++)
                    {
                        var info = Addressables.GetLocatorInfo(frozen[i]);
                        if (info?.CatalogLocation == null)
                            throw new ResourceException(ResourceError.AddressNotFound, "An explicit catalog ID has no official catalog location: " + frozen[i]);
                        targets[i] = (info, info.Locator, info.LocalHash,
                            info.CatalogLocation.Dependencies?.Count == (int)ContentCatalogProvider.DependencyHashIndex.Count);
                    }
                    _catalogApplyInFlight = true;
                    return Addressables.UpdateCatalogs(frozen, true);
                }, handle => SnapshotCatalogApply(handle, frozen, targets), appliesCatalog: true);
            }
            catch (Exception error)
            {
                Fail(request, error);
            }
            return request;
        }

        private static IReadOnlyList<string> SnapshotCatalogCheck(AsyncOperationHandle<List<string>> handle)
        {
            // 官方 Check 会忽略无异常的空 Hash；必须在归还 pin 前验证它实际读取的依赖。
            foreach (var hash in ReadCatalogOperationGroup(handle))
                RequireRemoteCatalogHash(hash);
            return Array.AsReadOnly(handle.Result.ToArray());
        }

        private static bool SnapshotCatalogApply(AsyncOperationHandle<List<IResourceLocator>> handle, string[] ids,
            (ResourceLocatorInfo Info, IResourceLocator Before, string LocalHash, bool HasRemoteHash)[] targets)
        {
            var catalogs = ReadCatalogOperationGroup(handle);
            var locators = handle.Result;
            if (locators?.Count != ids.Length || catalogs.Count != ids.Length)
                throw new ResourceException(ResourceError.LoadFailed, "Addressables did not return the requested catalog update results.");

            var changed = false;
            for (var i = 0; i < ids.Length; i++)
            {
                var target = targets[i];
                if (!catalogs[i].IsValid() || locators[i] == null || !ReferenceEquals(locators[i], target.Info.Locator))
                    throw new ResourceException(ResourceError.LoadFailed, "Addressables did not install the result for catalog ID: " + ids[i]);

                // 官方也接受 Provider 直接返回 Locator；该分支没有登记 Hash，只能判断替换。
                if (catalogs[i].Result is IResourceLocator)
                {
                    changed |= !ReferenceEquals(target.Before, locators[i]);
                    continue;
                }
                if (!target.HasRemoteHash)
                    continue;

                // 成功加载旧的缓存或内置目录并不表示远端版本已生效。
                var hashes = ReadCatalogOperationGroup(catalogs[i]);
                var remoteHash = RequireRemoteCatalogHash(hashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote], ids[i]);
                if (!string.Equals(target.Info.LocalHash, remoteHash, StringComparison.Ordinal))
                    throw new ResourceException(ResourceError.LoadFailed, "Addressables did not install the remote hash for catalog ID: " + ids[i]);
                changed |= !string.Equals(target.LocalHash, remoteHash, StringComparison.Ordinal);
            }
            return changed;
        }

        private static IList<AsyncOperationHandle> ReadCatalogOperationGroup(AsyncOperationHandle operation)
        {
            var dependencies = new List<AsyncOperationHandle>(1);
            operation.GetDependencies(dependencies);
            if (dependencies.Count == 1 && dependencies[0].IsValid() &&
                dependencies[0].Result is IList<AsyncOperationHandle> group)
                return group;
            throw new ResourceException(ResourceError.LoadFailed, "Addressables did not expose the catalog operation dependency group.");
        }

        private static string RequireRemoteCatalogHash(AsyncOperationHandle handle, string catalogId = null)
        {
            if (handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded && handle.OperationException == null &&
                handle.Result is string hash && !string.IsNullOrWhiteSpace(hash))
                return hash;
            throw new ResourceException(ResourceError.LoadFailed, "Addressables could not read the remote catalog hash" +
                (catalogId == null ? "." : " for catalog ID: " + catalogId), handle.IsValid() ? handle.OperationException : null);
        }

        private void RequireStartupCatalogUpdateWindow()
        {
            RequireAvailable();
            if (!StartupUpdateWindowOpen)
                throw new ResourceException(ResourceError.Busy, "Catalog updates are restricted to the startup window before the first business asset load.");
            if (!Configuration.EnableStartupCatalogUpdates || !_startupCatalogUpdatesPrepared)
                throw new ResourceException(ResourceError.InvalidConfiguration, "Controlled startup catalog updates were not explicitly enabled and verified before official initialization.");
        }

        private static string[] FreezeDeliveryKeys(IEnumerable<string> keys)
        {
            if (keys == null)
                throw new ResourceException(ResourceError.InvalidAddress, "An explicit non-empty key or catalog ID collection is required.");
            var frozen = keys.Distinct(StringComparer.Ordinal).ToArray();
            if (frozen.Length == 0 || frozen.Any(string.IsNullOrWhiteSpace))
                throw new ResourceException(ResourceError.InvalidAddress, "Keys and catalog IDs cannot be null, empty, or whitespace.");
            return frozen;
        }

        private void QueueCatalogControl<TValue, TResult>(ResourceRequest<TResult> request,
            Func<AsyncOperationHandle<TValue>> start, Func<AsyncOperationHandle<TValue>, TResult> snapshot, bool appliesCatalog = false)
        {
            _catalogControls.Enqueue(() =>
            {
                if (request.IsDone)
                    return false;
                Run(ObserveDelivery(request, start, snapshot, false, ReleaseCatalogControlAfterTurn, appliesCatalog), error => Fail(request, error));
                return true;
            });
            StartNextCatalogControl();
        }

        private void StartNextCatalogControl()
        {
            while (!_catalogControlRunning && _catalogControls.Count != 0)
            {
                _catalogControlRunning = true;
                if (_catalogControls.Dequeue()())
                    return;
                _catalogControlRunning = false;
            }
        }

        private IEnumerator ObserveDelivery<TValue, TResult>(ResourceRequest<TResult> request,
            Func<AsyncOperationHandle<TValue>> start, Func<AsyncOperationHandle<TValue>, TResult> snapshot, bool ownsReturnedHandle,
            Action settled = null, bool appliesCatalog = false)
        {
            AddressablesHandleOwner<TValue> pin = null;
            try
            {
                if (request.IsDone)
                    yield break;
                RequireAvailable();
                var manager = Addressables.ResourceManager;
                var handle = start();
                try
                {
                    // Check/Update 可能返回官方共享别名，必须立即独立 Acquire，不能 Take 掉别人的原引用。
                    pin = AddressablesHandleOwner<TValue>.AcquireReference(manager, handle);
                }
                finally
                {
                    if (ownsReturnedHandle)
                        manager.Release(handle);
                }
                request.SetProgressReader(() => handle.IsValid() ? handle.PercentComplete : 1);
                // IsDone 可能先于 deferred Completed/官方 auto-release；必须先观察真实完成回调。
                var completionObserved = false;
                handle.Completed += _ => completionObserved = true;
                while (!completionObserved)
                    yield return null;
                if (request.IsDone)
                    yield break;
                if (!IsSessionActive || State != ResourceSystemState.Ready)
                    throw AdmissionError();
                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw new ResourceException(ResourceError.LoadFailed, "The official Addressables delivery operation failed.", handle.OperationException);
                var result = snapshot(handle);
                pin.Dispose();
                pin = null;
                if (appliesCatalog)
                    _catalogApplyInFlight = false;
                request.SetSuccess(result);
                request.NotifyCompletion();
            }
            finally
            {
                try
                {
                    pin?.Dispose();
                }
                finally
                {
                    if (appliesCatalog)
                        _catalogApplyInFlight = false;
                    settled?.Invoke();
                }
            }
        }

        private void ReleaseCatalogControlAfterTurn()
        {
            var released = false;
            Post(() =>
            {
                _catalogControlRunning = false;
                released = true;
                StartNextCatalogControl();
            });
            // Post 自身不占 root work；保留一个短 barrier，避免 Stop 在该下一轮前移除推进订阅。
            Run(WaitForCatalogControlTurn(() => released));
        }

        private static IEnumerator WaitForCatalogControlTurn(Func<bool> released)
        {
            while (!released())
                yield return null;
        }
    }
}
