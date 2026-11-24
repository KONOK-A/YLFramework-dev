using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private sealed class ImageHold
        {
            // 活跃时仅借读同一官方 operation；只有 IdleNode 非空时才拥有一份额外 Acquire pin。
            internal AsyncOperationHandle Handle;
            internal string Key;
            internal int Leases;
            internal long Bytes;
            internal double IdleSince;
            internal LinkedListNode<ImageHold> IdleNode;
        }

        private readonly Dictionary<string, ImageHold> _imageHolds = new(StringComparer.Ordinal);
        private readonly LinkedList<ImageHold> _idleImages = new();
        private AddressablesImageProvider _imageProvider;
        private AddressablesImageProvider.Context _imageContext;
        private IDisposable _imageCacheTick;
        private long _idleImageBytes;
        private bool _imagesRetired;

        /// <summary>本方明确持有官方 Handle 的 idle 图片数，不包含活跃业务 Lease。</summary>
        public int CachedImageCount
        {
            get
            {
                CheckThread();
                return _idleImages.Count;
            }
        }

        /// <summary>idle Handle 保活的无 mip RGBA 估算字节数，不代表 native memory。</summary>
        public long CachedImageEstimatedBytes
        {
            get
            {
                CheckThread();
                return _idleImageBytes;
            }
        }

        /// <summary>只注册本方唯一 Provider；重复调用不新增，不替换其他消费者的 Provider。</summary>
        private void InitializeImageProvider()
        {
            CheckThread();
            if (_imageContext != null)
            {
                return;
            }
            var providers = Addressables.ResourceManager.ResourceProviders;
            foreach (var provider in providers)
            {
                if (!string.Equals(provider.ProviderId, AddressablesImageProvider.Id, StringComparison.Ordinal))
                {
                    continue;
                }
                if (provider is not AddressablesImageProvider imageProvider)
                {
                    throw new ResourceException(ResourceError.ConfigurationConflict, "The dynamic image Provider ID is already owned by another Provider.");
                }
                _imageProvider = imageProvider;
                break;
            }
            if (_imageProvider == null)
            {
                _imageProvider = new AddressablesImageProvider();
                providers.Add(_imageProvider);
            }
            _imageContext = new AddressablesImageProvider.Context(Configuration.Images, routine => Run(routine));
        }

        /// <summary>按精确 URL 或绝对本地 PNG/JPEG 路径取得统一 Lease；取消只结束本消费者等待。</summary>
        /// <remarks>冷加载始终异步。等待 IO 槽时取消不启动加载；已交给 Addressables 的操作继续排空。</remarks>
        public ResourceRequest<AssetLease<Texture2D>> AcquireImageAsync(string location, CancellationToken cancellation = default)
        {
            CheckThread();
            var request = new ResourceRequest<AssetLease<Texture2D>>(lease => lease.Dispose())
            {
                EventTrace = BeginEvent(ResourceEventSource.Image, location)
            };
            try
            {
                RequireAvailable();
                InitializeImageProvider();
                var resourceLocation = AddressablesImageProvider.CreateLocation(location, _imageContext);
                TrimImageCache();
                _businessStarted = true;
                Track(request, resourceLocation.PrimaryKey);
                BindCancellation(request, cancellation);
                if (!request.IsDone)
                {
                    Run(AcquireImageRoutine(resourceLocation, request), error => Fail(request, error));
                }
                return request;
            }
            catch (ResourceException error)
            {
                return Fail(request, error);
            }
            catch (NotSupportedException error)
            {
                return Fail(request, new ResourceException(ResourceError.Unsupported,
                    "This platform does not support the requested dynamic image input.", error));
            }
            catch (ArgumentException error)
            {
                return Fail(request, new ResourceException(ResourceError.InvalidAddress,
                    "Invalid dynamic image location.", error));
            }
        }

        private IEnumerator AcquireImageRoutine(IResourceLocation location, ResourceRequest<AssetLease<Texture2D>> request)
        {
            var key = location.InternalId;
            // 未调用 ProvideResource 就没有官方 operation；预期取消无需发送 IO 或制造 SDK 失败。
            while (!request.IsDone)
            {
                if (_imageHolds.TryGetValue(key, out var hold) && hold.Handle.IsValid() || _imageProvider.CanStart(key, _imageContext))
                {
                    RequireAvailable();
                    yield return AcquireLocationRoutine(location, key, request);
                    yield break;
                }
                yield return null;
            }
        }

        // 由统一 AssetLease 构造/Retain/Dispose 调用，不观察或反射官方全局引用计数。
        internal void OnImageLeaseAdded(IResourceLocation location, AsyncOperationHandle handle)
        {
            if (!(location?.Data is AddressablesImageProvider.Input input) || !ReferenceEquals(input.Owner, _imageContext))
            {
                return;
            }
            if (!_imageHolds.TryGetValue(location.InternalId, out var hold))
            {
                hold = new ImageHold { Key = location.InternalId, Handle = handle };
                _imageHolds.Add(hold.Key, hold);
            }
            hold.Leases++;
            if (hold.IdleNode != null)
            {
                var cached = hold.Handle;
                RemoveIdleImage(hold);
                hold.Handle = handle;
                Addressables.ResourceManager.Release(cached);
            }
        }

        internal void OnImageLeaseReleased(IResourceLocation location)
        {
            if (!(location?.Data is AddressablesImageProvider.Input input) || !ReferenceEquals(input.Owner, _imageContext))
            {
                return;
            }
            var hold = _imageHolds[location.InternalId];
            if (--hold.Leases != 0)
            {
                return;
            }
            var options = Configuration.Images;
            var texture = hold.Handle.IsValid() ? hold.Handle.Result as Texture2D : null;
            var bytes = texture ? (long)texture.width * texture.height * 4 : 0;
            if (_imagesRetired || !IsAvailable || !texture || options.IdleCapacity == 0 ||
                options.IdleCapacityBytes < bytes || options.IdleTimeToLive == TimeSpan.Zero)
            {
                _imageHolds.Remove(hold.Key);
                return;
            }
            // 此钩子先于最后一份业务 Handle 的 Release，先明确 Acquire 才能延续官方 operation。
            Addressables.ResourceManager.Acquire(hold.Handle);
            hold.Bytes = bytes;
            hold.IdleSince = Time.realtimeSinceStartupAsDouble;
            hold.IdleNode = _idleImages.AddLast(hold);
            _idleImageBytes += bytes;
            _imageCacheTick ??= AddTick(TrimImageCache);
            TrimImageCache();
        }

        /// <summary>只归还本方 idle Handle；不会撤销活跃 Lease 或销毁外部 Texture。</summary>
        public void ClearIdleImageCache()
        {
            CheckThread();
            while (_idleImages.First != null)
            {
                ReleaseIdleImage(_idleImages.First.Value);
            }
            RemoveImageCacheTickIfIdle();
        }

        private void TrimImageCache()
        {
            var options = Configuration.Images;
            var now = Time.realtimeSinceStartupAsDouble;
            while (_idleImages.First != null)
            {
                var hold = _idleImages.First.Value;
                if (!_imagesRetired && hold.Handle.IsValid() && hold.Handle.Result is Texture2D texture && texture &&
                    _idleImages.Count <= options.IdleCapacity && _idleImageBytes <= options.IdleCapacityBytes &&
                    now - hold.IdleSince < options.IdleTimeToLive.TotalSeconds)
                {
                    break;
                }
                ReleaseIdleImage(hold);
            }
            RemoveImageCacheTickIfIdle();
        }

        private void RemoveIdleImage(ImageHold hold)
        {
            _idleImages.Remove(hold.IdleNode);
            hold.IdleNode = null;
            _idleImageBytes -= hold.Bytes;
        }

        private void ReleaseIdleImage(ImageHold hold)
        {
            RemoveIdleImage(hold);
            _imageHolds.Remove(hold.Key);
            var handle = hold.Handle;
            hold.Handle = default;
            Addressables.ResourceManager.Release(handle);
        }

        private void RemoveImageCacheTickIfIdle()
        {
            if (_idleImages.Count != 0)
            {
                return;
            }
            var tick = _imageCacheTick;
            _imageCacheTick = null;
            tick?.Dispose();
        }

        private void RetireImages()
        {
            _imagesRetired = true;
            ClearIdleImageCache();
        }

        private bool HasImageWork => _imageContext != null && _imageContext.WorkCount != 0;
    }
}
