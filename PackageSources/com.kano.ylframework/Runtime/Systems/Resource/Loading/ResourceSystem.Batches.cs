using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
#if UNITY_EDITOR
using UnityEngine.ResourceManagement.ResourceProviders;
#endif
using Object = UnityEngine.Object;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        /// <summary>异步加载显式地址或官方 Labels；每个 key 都须匹配类型，所有不同 location 都成功才交付。</summary>
        /// <remarks>批量输入在调用时冻结。不返回部分结果；取消只退出自己的等待，已开始的官方操作仍须排空。</remarks>
        public ResourceRequest<AssetBatchLease<T>> AcquireBatchAsync<T>(IEnumerable<string> keys, CancellationToken cancellation = default) where T : Object
        {
            CheckThread();
            var request = Track(new ResourceRequest<AssetBatchLease<T>>(lease => lease.Dispose()));
            try
            {
                RequireAvailable();
                var frozen = FreezeDeliveryKeys(keys);
                request.DiagnosticIdentity.Key = string.Join(", ", frozen);
                if (request.IsDone)
                    return request;
                RequireAvailable();
                _businessStarted = true;
                BindCancellation(request, cancellation);
                if (!request.IsDone)
                {
                    Run(AcquireRequiredBatch(frozen, request), error => Fail(request, error));
                }
            }
            catch (Exception error)
            {
                Fail(request, error);
            }
            return request;
        }

        private IEnumerator AcquireRequiredBatch<T>(string[] keys, ResourceRequest<AssetBatchLease<T>> request) where T : Object
        {
            if (request.IsDone)
                yield break;
            RequireAvailable();
            var manager = Addressables.ResourceManager;
            var locations = new List<IResourceLocation>();
            var seen = new HashSet<IResourceLocation>(BatchLocationIdentity.Instance);
            var identities = new Dictionary<(string Provider, string Id, Type Type), IResourceLocation>();
            // Union 单独查询各 key，不能让一个不存在的必需 key 被其它 key 的结果掩盖。
            foreach (var key in keys)
            {
                var query = Addressables.LoadResourceLocationsAsync(key, typeof(T));
                var queryOwner = AddressablesHandleOwner<IList<IResourceLocation>>.Take(manager, query);
                try
                {
                    while (!query.IsDone && !request.IsDone)
                        yield return null;
                    if (request.IsDone)
                        yield break;
                    if (query.Status != AsyncOperationStatus.Succeeded)
                    {
                        throw new ResourceException(ResourceError.LoadFailed, "Required batch location lookup failed: " + key, query.OperationException);
                    }
                    if (query.Result == null || query.Result.Count == 0)
                    {
                        throw new ResourceException(ResourceError.AddressNotFound, "No matching location for required batch key: " + key);
                    }
                    foreach (var location in query.Result)
                    {
                        // 同一官方 location 可同时被地址和 label 命中。不要把自定义依赖、Data 或哈希碰撞当成等价。
                        if (!seen.Add(location))
                            continue;
                        // Addressables 1.25 can share two different locations with this identity in its provider cache.
                        var identity = (location.ProviderId, location.InternalId, location.ResourceType);
                        if (identities.TryGetValue(identity, out var first))
                        {
#if UNITY_EDITOR
                            // Fast Mode creates plain AssetDatabase locations per lookup key. A custom ID transform can distinguish their references.
                            if (location.ProviderId == typeof(AssetDatabaseProvider).FullName &&
                                first.GetType() == typeof(ResourceLocationBase) && location.GetType() == typeof(ResourceLocationBase) &&
                                first.PrimaryKey == location.PrimaryKey && first.Data == null && location.Data == null &&
                                !first.HasDependencies && !location.HasDependencies && manager.InternalIdTransformFunc == null &&
                                manager.GetResourceProvider(typeof(T), location)?.GetType() == typeof(AssetDatabaseProvider))
                            {
                                continue;
                            }
#endif
                            throw new ResourceException(ResourceError.InvalidAddress, "Required batch has distinct locations with the same official identity: " + key);
                        }
                        identities.Add(identity, location);
                        locations.Add(location);
                    }
                }
                finally { DisposeOwner(queryOwner, request.DiagnosticIdentity); }
                if (request.IsDone)
                    yield break;
                RequireAvailable();
            }

            // 本方总是归还组引用，包括官方返回部分结果的失败；本层禁止领取该部分结果。
            var handle = manager.ProvideResources<T>(locations, false);
            var owner = AddressablesHandleOwner<IList<T>>.Take(manager, handle);
            try
            {
                request.SetProgressReader(() => handle.IsValid() ? handle.PercentComplete : 0);
                while (!handle.IsDone && !request.IsDone)
                    yield return null;
                if (request.IsDone)
                    yield break;
                RequireAvailable();
                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null || handle.Result.Count != locations.Count)
                {
                    throw new ResourceException(ResourceError.LoadFailed, "One or more required Addressables batch assets failed to load.", handle.OperationException);
                }
                var assets = new T[handle.Result.Count];
                for (var index = 0; index < assets.Length; index++)
                {
                    var asset = handle.Result[index];
                    if (!asset)
                        throw new ResourceException(ResourceError.LoadFailed, "A required Addressables batch asset is missing.");
                    assets[index] = asset;
                }
                var lease = new AssetBatchLease<T>(this, keys, owner, assets);
                owner = null;
                request.BeforeTake = () => _ = lease.Assets;
                request.SetSuccess(lease);
                request.Notify();
            }
            finally { DisposeOwner(owner, request.DiagnosticIdentity); }
        }

        private sealed class BatchLocationIdentity : IEqualityComparer<IResourceLocation>
        {
            internal static readonly BatchLocationIdentity Instance = new();
            public bool Equals(IResourceLocation left, IResourceLocation right) => ReferenceEquals(left, right);
            public int GetHashCode(IResourceLocation location) => RuntimeHelpers.GetHashCode(location);
        }
    }
}
