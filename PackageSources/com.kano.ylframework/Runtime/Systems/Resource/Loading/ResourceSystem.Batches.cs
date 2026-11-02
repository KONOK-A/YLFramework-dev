using System;
using System.Collections.Generic;
using System.Linq;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        /// <summary>枚举此 Bundle 在正式 Catalog 中的可赋值 typed 资产（含子资产），等待各自 producer 完成后交割；不取得未登记内容。</summary>
        /// <typeparam name="T">筛选及借读的 Unity 资源类型。</typeparam>
        /// <param name="projectId">已挂载目录的项目标识。</param>
        /// <param name="bundleId">目录中的逻辑 Bundle 标识，不接受场景 Bundle。</param>
        /// <returns>持有全部成功成员的批次请求；任一成员失败则归还本批责任。</returns>
        public ResourceRequest<AssetBatchLease<T>> AcquireAllAsync<T>(string projectId, string bundleId) where T : UnityEngine.Object
        {
            CheckThread();
            if (!IsAvailable)
            {
                return BatchFailure<T>(ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            if (IsProjectBlocked(projectId))
            {
                return BatchFailure<T>(ResourceError.Busy, "The project is inside its activation barrier.");
            }

            if (projectId == null || !_catalogs.TryGetValue(projectId, out var catalog) || catalog.Snapshot == null ||
                bundleId == null || !catalog.Snapshot.Bundles.TryGetValue(bundleId, out var descriptor))
            {
                return BatchFailure<T>(ResourceError.AddressNotFound, "Unknown mounted project/Bundle identity.");
            }

            if (descriptor.IsScene)
            {
                return BatchFailure<T>(ResourceError.Unsupported, "Scene Bundle preparation is not an asset batch or Scene load.");
            }

            return AcquireBatch<T>(catalog.Entries.Where(x => x.Descriptor?.BundleId == bundleId && typeof(T).IsAssignableFrom(x.Location.AssetType))
                .Select(x => (x, true)).ToArray(), "typed Bundle batch '" + bundleId + "'");
        }

        /// <summary>按 canonical AssetId 取得 Catalog Group；required 失败归还整个批次，optional 失败保留 cause。不创建实例、不加载 Scene。</summary>
        /// <param name="projectId">已登记目录的项目标识。</param>
        /// <param name="groupId">目录中的资源组标识。</param>
        /// <returns>普通资产批次请求；等待全部子请求结束后交割，成功结果保留可选项错误。</returns>
        public ResourceRequest<AssetBatchLease<UnityEngine.Object>> AcquireGroupAsync(string projectId, string groupId)
        {
            CheckThread();
            if (!IsAvailable)
            {
                return BatchFailure<UnityEngine.Object>(ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            if (IsProjectBlocked(projectId))
            {
                return BatchFailure<UnityEngine.Object>(ResourceError.Busy, "The project is inside its activation barrier.");
            }

            if (projectId == null || !_catalogs.TryGetValue(projectId, out var catalog))
            {
                return BatchFailure<UnityEngine.Object>(ResourceError.AddressNotFound, "Unknown resource project.");
            }

            var group = catalog.Groups.FirstOrDefault(x => x.Id == groupId);
            if (group == null)
            {
                return BatchFailure<UnityEngine.Object>(ResourceError.AddressNotFound, "Unknown Catalog Group.");
            }

            var entries = catalog.Entries.ToDictionary(x => x.AssetId, StringComparer.Ordinal);
            return AcquireBatch<UnityEngine.Object>(group.Members.Select(x => (entries.TryGetValue(x.AssetId, out var entry) ? entry : null, x.Required)).ToArray(),
                "Group '" + groupId + "'");
        }

        /// <summary>普通成员取得AssetLease；Bundle Scene仅保活图，Editor Scene只验证路径；均不调用SceneManager。</summary>
        /// <remarks>每批等待和取消独立；必需项失败立即释放本批责任，迟到 native 结果仍由原 producer 收尾。</remarks>
        /// <param name="projectId">已登记目录的项目标识。</param>
        /// <param name="groupId">待预加载的目录组标识。</param>
        /// <returns>组责任聚合的请求，成功后须显式领取并归还组租约。</returns>
        public ResourceRequest<ResourceGroupLease> PreloadGroupAsync(string projectId, string groupId)
        {
            CheckThread();
            var request = new ResourceRequest<ResourceGroupLease>(value => value.Dispose());
            if (!IsAvailable)
            {
                return Fail(request, ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            if (IsProjectBlocked(projectId))
            {
                return Fail(request, ResourceError.Busy, "The project is inside its activation barrier.");
            }

            if (projectId == null || !_catalogs.TryGetValue(projectId, out var catalog))
            {
                return Fail(request, ResourceError.AddressNotFound, "Unknown resource project.");
            }

            var group = catalog.Groups.FirstOrDefault(x => x.Id == groupId);
            if (group == null)
            {
                return Fail(request, ResourceError.AddressNotFound, "Unknown Catalog Group.");
            }

            var entries = catalog.Entries.ToDictionary(x => x.AssetId, StringComparer.Ordinal);
            var descriptors = catalog.Snapshot?.Document.Assets.ToDictionary(x => x.AssetId, StringComparer.Ordinal);
            var children = new List<GroupPreloadChild>();
            var sceneBundles = new Dictionary<string, ResourceRequest<BundleLease>>(StringComparer.Ordinal);
            var ownerLabel = "Group preload: " + projectId + "/" + groupId;
            var starting = true;
            var finishing = false;
            Track(request);
            // 逐项关闭本组子请求并汇总清理异常，不接管其他消费者的责任。
            void ReleaseChildren()
            {
                List<Exception> errors = null;
                foreach (var child in children)
                {
                    try
                    {
                        child.Asset?.Dispose();
                    }
                    catch (Exception error)
                    {
                        (errors ??= new List<Exception>()).Add(error);
                    }
                    try
                    {
                        child.SceneBundle?.Dispose();
                    }
                    catch (Exception error)
                    {
                        (errors ??= new List<Exception>()).Add(error);
                    }
                }
                if (errors != null)
                {
                    throw new AggregateException("Group preload child cleanup failed.", errors);
                }
            }
            request.Attach(ReleaseChildren, () => children.Count == 0 ? 0 : children.Average(x => x.Asset?.Progress ?? x.SceneBundle?.Progress ?? 1));
            // 在组可交割时复核准入并转移租约，失败则归还本组已取得的责任。
            void Finish()
            {
                if (starting || finishing || request.IsDone)
                {
                    return;
                }

                var requiredFailed = children.Any(x => x.Required && x.IsDone && x.Error != null);
                if (!requiredFailed && children.Any(x => !x.IsDone))
                {
                    return;
                }

                finishing = true;
                var assets = new List<AssetLease<UnityEngine.Object>>();
                var bundles = new List<BundleLease>();
                try
                {
                    if (!requiredFailed)
                    {
                        int before;
                        do
                        {
                            before = children.Count(x => x.Error != null);
                            foreach (var child in children)
                            {
                                if (child.Error != null)
                                {
                                    continue;
                                }

                                try
                                {
                                    if (child.Kind == ResourceGroupItemKind.SceneBundle)
                                    {
                                        RequireHealthyBundle(catalog.Snapshot, child.BundleId);
                                    }
                                    else if (entries.TryGetValue(child.AssetId, out var entry))
                                    {
                                        RequireContentAdmission(entry);
                                    }
                                }
                                catch (Exception error)
                                {
                                    child.ResolutionError = ResourceContentValidation.Failure(error, "A Group member became unavailable before handoff.");
                                }
                            }
                            requiredFailed = children.Any(x => x.Required && x.Error != null);
                            if (requiredFailed)
                            {
                                break;
                            }
                            // optional归还可能触发用户释放回调；完成这些回调后再复核剩余待交付项。
                            foreach (var child in children.Where(x => x.Error != null))
                            {
                                child.Asset?.Dispose();
                                child.SceneBundle?.Dispose();
                            }
                        } while (children.Count(x => x.Error != null) != before);
                    }
                    if (request.IsDone)
                    {
                        return;
                    }

                    if (requiredFailed)
                    {
                        // 先停止自己的等待/未领取结果，再复制稳定的逐项错误；不等别人的 native 操作。
                        ReleaseChildren();
                        var items = children.Select(x => x.Result()).ToArray();
                        request.SetFailure(ResourceRequestStatus.Failed, new ResourceException(ResourceError.LoadFailed,
                            "Group preload '" + groupId + "' failed; batch ownership was rolled back.",
                            new ResourceGroupFailure(items, items.Where(x => x.Error != null).Select(x => x.Error))));
                    }
                    else
                    {
                        var claimed = new HashSet<ResourceRequest<BundleLease>>();
                        foreach (var child in children)
                        {
                            if (child.Error != null)
                            {
                                continue;
                            }

                            if (child.Asset != null)
                            {
                                var lease = child.Asset.TakeResult();
                                assets.Add(lease);
                                lease.OwnerLabel = ownerLabel;
                            }
                            else if (child.SceneBundle != null && claimed.Add(child.SceneBundle))
                            {
                                var lease = child.SceneBundle.TakeResult();
                                bundles.Add(lease);
                                lease.OwnerLabel = ownerLabel;
                            }
                        }
                        request.SetSuccess(new ResourceGroupLease(projectId, groupId, children.Select(x => x.Result()), assets, bundles));
                    }
                }
                catch (Exception cause)
                {
                    var cleanup = new List<Exception> { cause };
                    foreach (var lease in assets)
                    {
                        try
                        {
                            lease.Dispose();
                        }
                        catch (Exception error)
                        {
                            cleanup.Add(error);
                        }
                    }

                    foreach (var lease in bundles)
                    {
                        try
                        {
                            lease.Dispose();
                        }
                        catch (Exception error)
                        {
                            cleanup.Add(error);
                        }
                    }

                    var items = children.Select(x => x.Result()).ToArray();
                    request.SetFailure(ResourceRequestStatus.Failed, new ResourceException(ResourceError.LoadFailed, "Group preload result transfer failed.",
                        new ResourceGroupFailure(items, items.Where(x => x.Error != null).Select(x => x.Error).Cast<Exception>().Concat(cleanup))));
                }
                finally
                {
                    ReleaseChildren();
                    finishing = false;
                }
                request.Notify();
            }
            foreach (var member in group.Members)
            {
                if (request.IsDone)
                {
                    break;
                }

                entries.TryGetValue(member.AssetId, out var entry);
                ResourceCatalogAsset descriptor = null;
                descriptors?.TryGetValue(member.AssetId, out descriptor);
                var child = new GroupPreloadChild
                {
                    AssetId = member.AssetId,
                    Required = member.Required,
                    Address = entry?.Address ?? (descriptor == null ? (ResourceAddress?)null : new ResourceAddress(projectId, descriptor.Address, descriptor.SubAssetKey)),
                    Kind = descriptor?.Kind == "Scene" ? ResourceGroupItemKind.SceneBundle : ResourceGroupItemKind.Asset,
                    BundleId = descriptor?.BundleId
                };
                children.Add(child);
                if (child.Kind == ResourceGroupItemKind.SceneBundle)
                {
                    if (!sceneBundles.TryGetValue(child.BundleId, out var preparing))
                    {
                        // Public 图根让正常 Shutdown 也能准确识别组尚未交回的预加载责任。
                        preparing = AcquireBundleRequest(projectId, child.BundleId, false, true, new BundleLeaseOrigin(groupId: groupId));
                        sceneBundles.Add(child.BundleId, preparing);
                    }
                    child.SceneBundle = preparing;
                    if (!request.IsDone)
                    {
                        preparing.Completed += _ => Finish();
                    }
                }
                else if (entry != null && _sources[entry.Location.Source] is IResourceSceneSource sceneSource && sceneSource.IsScene(entry))
                {
                    child.Kind = ResourceGroupItemKind.ScenePreview;
                    try
                    {
                        sceneSource.ResolveScenePath(entry);
                    }
                    catch (Exception error)
                    {
                        child.ResolutionError = error as ResourceException ?? new ResourceException(ResourceError.LoadFailed,
                            "Editor Scene preparation failed: " + entry.Address, error, entry.Address, entry.Location.Source);
                    }
                }
                else if (entry != null)
                {
                    child.Asset = AcquireAsync<UnityEngine.Object>(entry.Address);
                    if (!request.IsDone)
                    {
                        child.Asset.Completed += _ => Finish();
                    }
                }
                else
                {
                    child.ResolutionError = new ResourceException(ResourceError.AddressNotFound, "Missing canonical Group member: " + member.AssetId);
                }

                if (request.IsDone)
                {
                    ReleaseChildren();
                }
            }
            starting = false;
            Finish();
            return request;
        }

        /// <summary>保存一个组成员的解析结果及资产或场景 Bundle 子请求。</summary>
        private sealed class GroupPreloadChild
        {
            internal string AssetId;
            internal ResourceAddress? Address;
            internal bool Required;
            internal ResourceGroupItemKind Kind;
            internal string BundleId;
            internal ResourceRequest<AssetLease<UnityEngine.Object>> Asset;
            internal ResourceRequest<BundleLease> SceneBundle;
            internal ResourceException ResolutionError;
            internal bool IsDone => ResolutionError != null || (Asset?.IsDone ?? SceneBundle?.IsDone ?? true);
            internal ResourceException Error => ResolutionError ?? Asset?.Error ?? SceneBundle?.Error;
            /// <summary>复制该成员当前可观察结果，不转移子请求或租约责任。</summary>
            internal ResourceGroupItemResult Result() => new(AssetId, Address, Required, Kind, BundleId, Error);
        }

        /// <summary>建立带标准错误终态的独立资产批次请求。</summary>
        private static ResourceRequest<AssetBatchLease<T>> BatchFailure<T>(ResourceError kind, string message) where T : UnityEngine.Object =>
            Fail(new ResourceRequest<AssetBatchLease<T>>(value => value.Dispose()), kind, message);

        /// <summary>聚合普通资产子请求，按必需项规则交付批次或归还本批责任。</summary>
        /// <remarks>直接归还子项时不逐项隔离清理异常，异常可能中断后续归还或通知。</remarks>
        private ResourceRequest<AssetBatchLease<T>> AcquireBatch<T>((ResourceCatalogEntry Entry, bool Required)[] entries, string label) where T : UnityEngine.Object
        {
            var request = new ResourceRequest<AssetBatchLease<T>>(value => value.Dispose());
            var children = new List<(ResourceRequest<AssetLease<T>> Request, bool Required, ResourceCatalogEntry Entry)>();
            var starting = true;
            var finishing = false;
            Track(request);
            request.Attach(() =>
            {
                foreach (var child in children.ToArray())
                {
                    child.Request.Dispose();
                }
            },
                () => children.Count == 0 ? 0 : children.Sum(x => x.Request.Progress) / children.Count);
            // 全部子请求终结后复核可交付项，成功结果只转移给本批一次。
            void Finish()
            {
                if (starting || finishing || request.IsDone || children.Any(x => !x.Request.IsDone))
                {
                    return;
                }

                finishing = true;
                var errors = children.Where(x => x.Request.Status != ResourceRequestStatus.Succeeded).Select(x => x.Request.Error).ToList();
                var rejected = new HashSet<ResourceRequest<AssetLease<T>>>();
                var requiredFailed = children.Any(x => x.Required && x.Request.Status != ResourceRequestStatus.Succeeded);
                try
                {
                    if (!requiredFailed)
                    {
                        int before;
                        do
                        {
                            before = rejected.Count;
                            foreach (var child in children)
                            {
                                if (child.Request.Status != ResourceRequestStatus.Succeeded || rejected.Contains(child.Request))
                                {
                                    continue;
                                }

                                try
                                {
                                    RequireContentAdmission(child.Entry);
                                }
                                catch (Exception error)
                                {
                                    rejected.Add(child.Request);
                                    errors.Add(ResourceContentValidation.Failure(error, "A batch member became unavailable before handoff."));
                                    requiredFailed |= child.Required;
                                }
                            }
                            if (requiredFailed)
                            {
                                break;
                            }

                            foreach (var child in rejected)
                            {
                                child.Dispose();
                            }
                        } while (rejected.Count != before);
                    }
                    if (request.IsDone)
                    {
                        return;
                    }

                    if (requiredFailed)
                    {
                        request.SetFailure(ResourceRequestStatus.Failed, new ResourceException(ResourceError.LoadFailed, label + " failed; required member ownership was rolled back.",
                            new AggregateException(errors)));
                    }
                    else
                    {
                        var leases = new List<AssetLease<T>>();
                        try
                        {
                            foreach (var child in children)
                            {
                                if (child.Request.Status == ResourceRequestStatus.Succeeded && !rejected.Contains(child.Request))
                                {
                                    leases.Add(child.Request.TakeResult());
                                }
                            }

                            request.SetSuccess(new AssetBatchLease<T>(leases, errors));
                        }
                        catch (Exception error)
                        {
                            foreach (var lease in leases)
                            {
                                lease.Dispose();
                            }

                            request.SetFailure(ResourceRequestStatus.Failed, ResourceContentValidation.Failure(error, "Batch result transfer failed."));
                        }
                    }
                }
                finally
                {
                    foreach (var child in children)
                    {
                        child.Request.Dispose();
                    }

                    finishing = false;
                }
                request.Notify();
            }
            foreach (var entry in entries)
            {
                if (request.IsDone)
                {
                    break;
                }

                var child = entry.Entry == null ? Fail(new ResourceRequest<AssetLease<T>>(value => value.Dispose()), ResourceError.Unsupported,
                    "Scene/group member cannot be acquired as an ordinary asset in this Source.") : AcquireAsync<T>(entry.Entry.Address);
                children.Add((child, entry.Required, entry.Entry));
                if (request.IsDone)
                {
                    child.Dispose();
                }
                else
                {
                    child.Completed += _ => Finish();
                }
            }
            starting = false;
            Finish();
            return request;
        }
    }
}
