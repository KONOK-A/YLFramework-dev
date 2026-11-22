using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using UnityEngine.U2D;
using UnityEngine.UI;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Resource.Addressables.Runtime")]
    public sealed class ResourceAddressablesImageTests
    {
        private readonly List<IDisposable> _owned = new();
        private readonly List<Object> _assets = new();
        private ResourceStartup _startup;
        private string _file;
        private byte[] _png;
        private GameObject _host;
        private ResourceLocationMap _locator;
        private BorrowedImageProvider _provider;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            _file = Path.Combine(Application.temporaryCachePath, "YL-AA-image-" + Guid.NewGuid().ToString("N") + ".png");
            _png = Encode(7, 3);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), _png);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_host)
            {
                Object.Destroy(_host);
            }
            foreach (var host in _assets.OfType<GameObject>())
                if (host)
                    Object.Destroy(host);
            yield return null;
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                _owned[i].Dispose();
            }
            _owned.Clear();
            if (_startup != null)
            {
                using var stopped = _startup.StopAsync();
                yield return stopped;
                Assert.That(stopped.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stopped.Error?.ToString());
            }
            if (_locator != null)
            {
                Addressables.RemoveResourceLocator(_locator);
            }
            if (_provider != null)
            {
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
            }
            foreach (var asset in _assets)
                if (asset)
                    Object.Destroy(asset);
            _assets.Clear();
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
            yield return null;
        }

        private IEnumerator Start(ResourceImageOptions options = null, bool bindCoreResourceProvider = true)
        {
            _startup = new ResourceStartup(new ResourceConfiguration(images: options), bindCoreResourceProvider);
            using var started = _startup.StartAsync();
            yield return started;
            Assert.That(started.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), started.Error?.ToString());
            Assert.That(started.TakeResult(), Is.SameAs(_startup));
        }

        private T Own<T>(T value) where T : IDisposable
        {
            _owned.Add(value);
            return value;
        }

        [UnityTest]
        public IEnumerator SameUrl_CancelOneConsumer_KeepsOtherOfficialHandleAlive()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0));
            var entered = 0;
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                Interlocked.Increment(ref entered);
                await release.Task;
                await Send(context, _png);
            });
            using var cancel = new CancellationTokenSource();
            var first = Own(_startup.Resources.AcquireImageAsync(server.Root + "Image?Version=AbC", cancel.Token));
            var second = Own(_startup.Resources.AcquireImageAsync(server.Root + "Image?Version=AbC"));
            try
            {
                yield return Until(() => Volatile.Read(ref entered) == 1);
                cancel.Cancel();
                yield return Until(() => first.IsDone);
                Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                Assert.That(second.IsDone, Is.False);
                release.TrySetResult(true);
                yield return second;
                Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), second.Error?.ToString());
                var lease = Own(second.TakeResult());
                var texture = lease.Asset;
                Assert.That(texture.width, Is.EqualTo(7));
                Assert.That(texture.height, Is.EqualTo(3));
                Assert.That(texture.isReadable, Is.False);
                Assert.That(texture.mipmapCount, Is.EqualTo(1));
                Assert.That(Volatile.Read(ref entered), Is.EqualTo(1), "Sharing belongs to the official operation cache.");
                _startup.Resources.ClearIdleImageCache();
                Assert.That(lease.IsValid, Is.True);
                lease.Dispose();
                yield return null;
                Assert.That(texture == null, Is.True);
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [UnityTest]
        public IEnumerator HundredConsumersShareOneGetAndAnIdleHitWhileCancellationStaysIndependent()
        {
            yield return Start(bindCoreResourceProvider: false);
            var system = _startup.Resources;
            var gets = new ConcurrentQueue<string>();
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                var url = context.Request.RawUrl;
                gets.Enqueue(url);
                if (url != "/Image?Version=AbC")
                {
                    await Send(context, Array.Empty<byte>(), 404);
                    return;
                }
                await release.Task;
                await Send(context, _png);
            });
            var address = server.Root + "Image?Version=AbC";
            var requests = new ResourceRequest<AssetLease<Texture2D>>[100];
            var completions = new int[requests.Length];
            var completionFrames = new int[requests.Length];
            var begin = Time.frameCount;
            try
            {
                for (var i = 0; i < requests.Length; i++)
                {
                    var index = i;
                    requests[i] = Own(system.AcquireImageAsync(address));
                    requests[i].Completed += _ =>
                    {
                        completions[index]++;
                        completionFrames[index] = Time.frameCount - begin;
                    };
                }
                yield return Until(() => gets.Count >= 1);
                Assert.That(system.GetDiagnostics().RequestCount, Is.EqualTo(100));
                for (var i = 0; i < 10; i++)
                    requests[i].Cancel();
                Assert.That(requests.Take(10).All(value => value.Status == ResourceRequestStatus.Canceled), Is.True);
                Assert.That(requests.Skip(10).All(value => !value.IsDone), Is.True);
                release.TrySetResult(true);
                yield return Until(() => requests.All(value => value.IsDone));
                Assert.That(completions.All(count => count == 1), Is.True);
                var leases = requests.Skip(10).Select(request => Own(request.TakeResult())).ToArray();
                var texture = leases[0].Asset;
                Assert.That(leases.All(lease => lease.IsValid && lease.Asset == texture), Is.True);
                Assert.That(system.GetDiagnostics().LeaseCount, Is.EqualTo(90));
                foreach (var lease in leases)
                    lease.Dispose();
                Assert.That(system.CachedImageCount, Is.EqualTo(1));
                using var warm = system.AcquireImageAsync(address);
                yield return warm;
                using var hit = warm.TakeResult();
                Assert.That(hit.Asset, Is.SameAs(texture));
                CollectionAssert.AreEqual(new[] { "/Image?Version=AbC" }, gets.ToArray());

                ExpectProviderFailure("HTTP 404");
                using var different = system.AcquireImageAsync(server.Root + "image?version=abc");
                yield return different;
                Assert.That(different.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(different.Error.ToString(), Does.Contain("404"));
                Assert.That(hit.IsValid && hit.Asset == texture, Is.True);
                CollectionAssert.AreEqual(new[] { "/Image?Version=AbC", "/image?version=abc" }, gets.ToArray());
                var frames = completionFrames.Skip(10).OrderBy(value => value).ToArray();
                TestContext.WriteLine($"Official image fan-out: 100 consumers, 10 canceled, 1 successful GET; P50={frames[44]} frames, P95={frames[85]} frames.");
                hit.Dispose();
                system.ClearIdleImageCache();
                yield return Until(() => texture == null);
                Assert.That(system.GetDiagnostics().LeaseCount, Is.Zero);
            }
            finally { release.TrySetResult(true); }
        }

        [UnityTest]
        [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.WindowsPlayer)]
        public IEnumerator WindowsPartiallyQualifiedLocationsNeverStartAnImageLoad()
        {
            yield return Start(bindCoreResourceProvider: false);
            var drive = Path.GetPathRoot(Path.GetFullPath(_file)).Substring(0, 2);
            foreach (var address in new[] { drive + Path.GetFileName(_file), "\\" + Path.GetFileName(_file) })
            {
                using var request = _startup.Resources.AcquireImageAsync(address);
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(request.Error.Kind, Is.EqualTo(ResourceError.InvalidAddress));
            }
            var snapshot = _startup.Resources.GetDiagnostics();
            Assert.That(snapshot.PendingWorkCount + snapshot.RequestCount + snapshot.DrainingHandleCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DistinctImages_DefaultProviderConcurrencyIsFour()
        {
            yield return Start();
            var entered = new ConcurrentQueue<string>();
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                entered.Enqueue(context.Request.RawUrl);
                await release.Task;
                await Send(context, _png);
            });
            var requests = new List<ResourceRequest<AssetLease<Texture2D>>>();
            try
            {
                for (var i = 0; i < 7; i++)
                {
                    requests.Add(Own(_startup.Resources.AcquireImageAsync(server.Root + i)));
                }
                yield return Until(() => entered.Count >= 4);
                for (var i = 0; i < 5; i++)
                {
                    yield return null;
                }
                Assert.That(entered.Count, Is.EqualTo(4));
                release.TrySetResult(true);
                yield return Until(() => requests.All(request => request.IsDone));
                Assert.That(requests.All(request => request.Status == ResourceRequestStatus.Succeeded), Is.True);
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [UnityTest]
        public IEnumerator RefreshDisableDestroy_RejectLateGeneration_KeepBorrowedPlaceholder()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0));
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            var next = Encode(11, 4);
            using var server = new ImageServer(async context =>
            {
                if (context.Request.RawUrl == "/old")
                {
                    entered.TrySetResult(true);
                    await release.Task;
                    await Send(context, _png);
                }
                else
                {
                    await Send(context, next);
                }
            });
            _host = new GameObject("Addressables image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var target = _host.GetComponent<Image>();
            var placeholderTexture = new Texture2D(2, 2);
            var placeholder = Sprite.Create(placeholderTexture, new Rect(0, 0, 2, 2), Vector2.one * .5f);
            try
            {
                target.sprite = placeholder;
                var component = _host.AddComponent<ResourceImage>();
                var independent = Own(_startup.Resources.AcquireImageAsync(server.Root + "old"));
                component.SetImage(server.Root + "old");
                yield return Until(() => entered.Task.IsCompleted);
                component.SetImage(server.Root + "new");
                yield return Until(() => component.Status == ResourceRequestStatus.Succeeded);
                var clone = target.sprite;
                var texture = clone.texture;
                Assert.That(texture.width, Is.EqualTo(11));
                release.TrySetResult(true);
                yield return independent;
                var oldLease = Own(independent.TakeResult());
                Assert.That(target.sprite, Is.SameAs(clone));
                component.enabled = false;
                yield return null;
                Assert.That(clone == null, Is.True);
                Assert.That(texture == null, Is.True);
                Assert.That(target.sprite, Is.SameAs(placeholder));
                Assert.That(oldLease.IsValid, Is.True);
                Object.Destroy(_host);
                _host = null;
                yield return null;
                Assert.That(placeholder && placeholderTexture, Is.True);
            }
            finally
            {
                release.TrySetResult(true);
                if (_host)
                {
                    Object.DestroyImmediate(_host);
                    _host = null;
                }
                Object.DestroyImmediate(placeholder);
                Object.DestroyImmediate(placeholderTexture);
            }
        }

        [UnityTest]
        public IEnumerator IdleCache_RetainDelaysTtl_TrimNeverRevokesActiveLease()
        {
            yield return Start(new ResourceImageOptions(idleCapacity: 1, idleTimeToLive: TimeSpan.FromSeconds(.1)));
            var first = Own(_startup.Resources.AcquireImageAsync(_file));
            yield return first;
            var lease = Own(first.TakeResult());
            var retained = Own(lease.Retain("image regression retained"));
            var texture = lease.Asset;
            lease.Dispose();
            Assert.That(_startup.Resources.CachedImageCount, Is.Zero, "An active Retain is not an idle cache item.");
            var until = Time.realtimeSinceStartupAsDouble + .2;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
            }
            Assert.That(retained.IsValid, Is.True);
            retained.Dispose();
            var warmRequest = Own(_startup.Resources.AcquireImageAsync(_file));
            yield return warmRequest;
            var warm = Own(warmRequest.TakeResult());
            Assert.That(warm.Asset, Is.SameAs(texture), "TTL begins at the last return, not at completion.");
            _startup.Resources.ClearIdleImageCache();
            Assert.That(warm.IsValid, Is.True);
            warm.Dispose();
            yield return Until(() => texture == null);
        }

        [UnityTest]
        public IEnumerator CacheEntryAndByteBudget_EvictOnlyIdleHandles()
        {
            yield return Start(new ResourceImageOptions(idleCapacity: 1, idleCapacityBytes: 7 * 3 * 4));
            var first = Own(_startup.Resources.AcquireImageAsync(_file));
            yield return first;
            var firstLease = Own(first.TakeResult());
            var firstTexture = firstLease.Asset;
            firstLease.Dispose();
            var secondFile = _file + ".other.png";
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(secondFile), _png);
            try
            {
                var second = Own(_startup.Resources.AcquireImageAsync(secondFile));
                yield return second;
                var secondLease = Own(second.TakeResult());
                var secondTexture = secondLease.Asset;
                Assert.That(firstTexture, Is.Not.Null, "Active loads do not consume idle capacity.");
                secondLease.Dispose();
                yield return Until(() => firstTexture == null);
                Assert.That(secondTexture, Is.Not.Null);
                Assert.That(_startup.Resources.CachedImageCount, Is.EqualTo(1));
                Assert.That(_startup.Resources.CachedImageEstimatedBytes, Is.EqualTo(84));
            }
            finally
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            }
        }

        [UnityTest]
        public IEnumerator EncodedPixelAndMalformedInputs_FailBeforeNativeDecode()
        {
            yield return Start(new ResourceImageOptions(maximumEncodedBytes: 2048, maximumPixels: 16));
            using var server = new ImageServer(context => context.Request.RawUrl == "/large"
                ? Send(context, new byte[4096], chunked: true)
                : context.Request.RawUrl == "/declared" ? Send(context, new byte[4096])
                : context.Request.RawUrl == "/bad" ? Send(context, Encoding.ASCII.GetBytes("not PNG or JPEG"))
                : Send(context, _png));
            foreach (var path in new[] { "large", "declared", "bad", "pixels" })
            {
                ExpectProviderFailure(path == "large" || path == "declared" ? "encoded byte budget" :
                    path == "bad" ? "valid PNG or JPEG" : "pixel budget");
                var request = Own(_startup.Resources.AcquireImageAsync(server.Root + path));
                yield return request;
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed), path);
                Assert.That(request.Error, Is.Not.Null);
            }
        }

        [UnityTest]
        public IEnumerator HttpFailureAndExactUrlIdentity_DiagnosticsNeverExposeQuery()
        {
            yield return Start();
            var entered = new ConcurrentQueue<string>();
            using var server = new ImageServer(context =>
            {
                entered.Enqueue(context.Request.RawUrl);
                return context.Request.RawUrl == "/Image?Version=AbC"
                    ? Send(context, _png) : Send(context, Array.Empty<byte>(), 404);
            });
            var success = Own(_startup.Resources.AcquireImageAsync(server.Root + "Image?Version=AbC"));
            ExpectProviderFailure("HTTP 404");
            var failure = Own(_startup.Resources.AcquireImageAsync(server.Root + "image?Version=abc&token=secret-image-token"));
            yield return success;
            yield return failure;
            Assert.That(success.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(failure.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(failure.Error.ToString(), Does.Contain("404").And.Not.Contain("secret-image-token"));
            Assert.That(entered.Count, Is.EqualTo(2));
            var lease = Own(success.TakeResult());
            Assert.That(lease.Key, Does.Not.Contain("Version=AbC"));
        }

        [UnityTest]
        public IEnumerator IdleTtl_ExpiresWithoutAnotherAcquire()
        {
            yield return Start(new ResourceImageOptions(idleTimeToLive: TimeSpan.FromSeconds(.05)));
            var request = Own(_startup.Resources.AcquireImageAsync(_file));
            yield return request;
            var lease = Own(request.TakeResult());
            var texture = lease.Asset;
            lease.Dispose();
            Assert.That(_startup.Resources.CachedImageCount, Is.EqualTo(1));
            yield return Until(() => texture == null);
            Assert.That(_startup.Resources.CachedImageCount, Is.Zero);
            Assert.That(_startup.Resources.CachedImageEstimatedBytes, Is.Zero);
        }

        [UnityTest]
        public IEnumerator StopRetiresIdle_BusyDoesNotDestroyIndependentImage()
        {
            yield return Start();
            var request = Own(_startup.Resources.AcquireImageAsync(_file));
            yield return request;
            var lease = Own(request.TakeResult());
            var texture = lease.Asset;
            using (var busy = _startup.StopAsync())
            {
                yield return busy;
                Assert.That(busy.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
                Assert.That(lease.IsValid, Is.True);
                Assert.That(lease.Asset, Is.SameAs(texture));
            }
            lease.Dispose();
            yield return Until(() => texture == null);
            using var stopped = _startup.StopAsync();
            yield return stopped;
            Assert.That(stopped.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stopped.Error?.ToString());
        }

        [UnityTest]
        public IEnumerator JpegInput_IsValidatedAndReleasedThroughTheSameProvider()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0));
            var input = new Texture2D(9, 5, TextureFormat.RGB24, false);
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), input.EncodeToJPG());
            }
            finally
            {
                Object.DestroyImmediate(input);
            }
            var request = Own(_startup.Resources.AcquireImageAsync(_file));
            yield return request;
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            var lease = Own(request.TakeResult());
            var texture = lease.Asset;
            Assert.That(texture.width, Is.EqualTo(9));
            Assert.That(texture.height, Is.EqualTo(5));
            Assert.That(texture.isReadable, Is.False);
            lease.Dispose();
            yield return Until(() => texture == null);
        }

        [UnityTest]
        public IEnumerator RegisterTwice_LeavesOtherProvidersAndBorrowedTexturesUntouched()
        {
            yield return Start();
            var providers = Addressables.ResourceManager.ResourceProviders;
            var method = typeof(ResourceSystem).GetMethod("InitializeImageProvider",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(_startup.Resources, null);
            var original = providers.ToArray();
            method.Invoke(_startup.Resources, null);
            method.Invoke(_startup.Resources, null);
            CollectionAssert.AreEqual(original, providers.ToArray());
            var provider = providers.Single(value => value.GetType().Name == "AddressablesImageProvider");
            var external = new Texture2D(2, 2);
            try
            {
                provider.Release(null, external);
                yield return null;
                Assert.That(external, Is.Not.Null, "Release must reject textures this Provider did not create.");
            }
            finally
            {
                Object.DestroyImmediate(external);
            }
        }

        [UnityTest]
        public IEnumerator OfficialSpriteAddress_ClearDoesNotDestroyBorrowedSprite()
        {
            yield return Start();
            var texture = new Texture2D(5, 3);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 5, 3), Vector2.one * .5f);
            RegisterAsset("image/sprite", sprite);
            try
            {
                _host = new GameObject("Official sprite", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                var component = _host.AddComponent<ResourceImage>();
                component.SetAddress("image/sprite");
                yield return Until(() => component.Status != ResourceRequestStatus.Pending);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
                Assert.That(_host.GetComponent<Image>().sprite, Is.SameAs(sprite));
                component.Clear();
                yield return null;
                Assert.That(sprite && texture, Is.True);
                Assert.That(_provider.Releases, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
            }
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator OfficialAtlasedSpriteReference_BorrowsProviderSpriteUntilTheLastHandleIsReleased()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            var reference = AtlasedReference("Manual", "Pixels");
            var external = Addressables.LoadAssetAsync<Sprite>(reference.RuntimeKey);
            Sprite sprite;
            try
            {
                yield return external;
                sprite = external.Result;
                Assert.That(sprite && sprite.texture, Is.True, external.OperationException?.ToString());

                var placeholder = Placeholder();
                var target = NewTarget<Image>("Official atlased Sprite reference");
                target.sprite = placeholder;
                var component = target.gameObject.AddComponent<ResourceImage>();
                component.SetImage(reference);
                yield return Until(() => !component.IsLoading);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
                Assert.That(target.sprite, Is.SameAs(sprite));
                Assert.That(reference.OperationHandle.IsValid(), Is.False, "The component must not occupy the AssetReference operation slot.");
                Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));

                component.Clear();
                yield return NoOwnedWork();
                Assert.That(target.sprite, Is.SameAs(placeholder));
                Assert.That(external.IsValid() && sprite && sprite.texture, Is.True, "Only Addressables owns the returned Sprite clone.");
            }
            finally
            {
                Addressables.Release(external);
            }
            yield return Until(() => sprite == null);
        }

        [UnityTest]
        public IEnumerator ClearingAnAtlasedSpriteReferenceDuringProvisionRejectsLateDelivery()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            var reference = AtlasedReference("Colors", "Blue");
            var placeholder = Placeholder();
            var target = NewTarget<Image>("Canceled atlased Sprite reference");
            target.sprite = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            int notifications = 0;
            component.Completed += _ => notifications++;

            var key = reference.RuntimeKey.ToString();
            var manager = Addressables.ResourceManager;
            var originalTransform = manager.InternalIdTransformFunc;
            bool clearedWhilePending = false;
            // 官方 AtlasSpriteProvider 在 GetSprite 前转换 ID；在该边界取消，不依赖 Bundle 或帧调度速度。
            manager.InternalIdTransformFunc = nativeLocation =>
            {
                if (nativeLocation.ProviderId == typeof(AtlasSpriteProvider).FullName && nativeLocation.PrimaryKey == key)
                {
                    clearedWhilePending = component.IsLoading;
                    component.Clear();
                }
                return originalTransform == null ? nativeLocation.InternalId : originalTransform(nativeLocation);
            };
            try
            {
                component.SetImage(reference);
                yield return Until(() => !component.IsLoading);
                Assert.That(clearedWhilePending, Is.True, "Cancel while the actual AtlasSpriteProvider is still producing the Sprite.");
                yield return NoOwnedWork();
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                Assert.That(target.sprite, Is.SameAs(placeholder));
                Assert.That(notifications, Is.Zero);
            }
            finally
            {
                manager.InternalIdTransformFunc = originalTransform;
            }
        }

        private static AssetReferenceAtlasedSprite AtlasedReference(string atlasName, string spriteName)
        {
            var path = $"Assets/YLAddressablesOwnershipContent/{atlasName}.spriteatlasv2";
            var reference = new AssetReferenceAtlasedSprite(UnityEditor.AssetDatabase.AssetPathToGUID(path)) { SubObjectName = spriteName };
            Assert.That(reference.RuntimeKeyIsValid(), Is.True, "Expected the actual built Atlas fixture at " + path);
            return reference;
        }
#endif

        [UnityTest]
        public IEnumerator MissingAtlasSprite_RestoresBorrowedPlaceholder_LeavesIndependentAtlasAlive()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            var request = Own(_startup.Resources.AcquireAsync<SpriteAtlas>("ownership/atlas"));
            yield return Until(() => request.IsDone);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            var independent = Own(request.TakeResult());
            var atlas = independent.Asset;
            var otherClone = Keep(atlas.GetSprite("Pixels"));
            Assert.That(otherClone && otherClone.texture, Is.True, "Use the actual prepared official Atlas, not an empty SpriteAtlas shell.");
            var packed = otherClone.texture;
            var placeholder = Placeholder();
            var target = NewTarget<Image>("Missing Atlas sprite");
            target.sprite = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetAtlasSprite("ownership/atlas", "Pixels");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), "The same official Atlas key and existing sprite must succeed before exercising the missing-sprite branch: " + component.Error);
            var previous = target.sprite;
            Assert.That(previous, Is.Not.SameAs(otherClone));
            component.SetAtlasSprite("ownership/atlas", "__missing_sprite_" + Guid.NewGuid().ToString("N"));
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(component.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            Assert.That(component.IsLoading, Is.False);
            Assert.That(target.sprite, Is.SameAs(placeholder));
            Assert.That(placeholder && placeholder.texture, Is.True);
            Assert.That(independent.IsValid && atlas && otherClone && packed, Is.True);
            Assert.That(otherClone.texture, Is.SameAs(packed));
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1), "Only the independently owned Atlas lease survives the failed UI request.");
            yield return Until(() => previous == null);
            Object.Destroy(otherClone);
            yield return null;
            independent.Dispose();
            yield return NoOwnedWork();
        }

        [UnityTest]
        public IEnumerator SwitchingTargets_RestoresPreviousTarget_DoesNotDestroyIndependentSprite()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            var texture = Keep(new Texture2D(5, 3));
            var sprite = Keep(Sprite.Create(texture, new Rect(0, 0, 5, 3), Vector2.one * .5f));
            RegisterAsset("image/target-sprite", sprite);
            var request = Own(_startup.Resources.AcquireAsync<Object>("image/target-sprite"));
            yield return Until(() => request.IsDone);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            var independent = Own(request.TakeResult());
            var firstPlaceholder = Placeholder();
            var secondPlaceholder = Placeholder();
            var first = NewTarget<Image>("First image target");
            var second = NewTarget<Image>("Second image target");
            first.sprite = firstPlaceholder;
            second.sprite = secondPlaceholder;
            var consumerHost = Keep(new GameObject("Target-switch consumer"));
            var component = consumerHost.AddComponent<ResourceImage>();
            component.Target = first;
            component.SetAddress("image/target-sprite");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            Assert.That(first.sprite, Is.SameAs(sprite));
            component.Target = second;
            Assert.That(first.sprite, Is.SameAs(firstPlaceholder), "Changing targets restores only the display previously owned by this component.");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            Assert.That(second.sprite, Is.SameAs(sprite));
            component.Clear();
            yield return null;
            Assert.That(second.sprite, Is.SameAs(secondPlaceholder));
            Assert.That(independent.IsValid && sprite && texture, Is.True);
            Assert.That(independent.Asset, Is.SameAs(sprite));
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));
            Assert.That(_provider.Releases, Is.Zero);
            independent.Dispose();
            yield return NoOwnedWork();
            Assert.That(_provider.Releases, Is.EqualTo(1));
            Assert.That(sprite && texture && firstPlaceholder && secondPlaceholder, Is.True, "The official controlled Provider lends real Unity objects; UI must not destroy them.");
        }

        [UnityTest]
        public IEnumerator ExternalRawTextureReplacement_ClearDoesNotOverwriteBorrowedValue()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            using var server = new ImageServer(context => Send(context, _png));
            var target = NewTarget<RawImage>("Externally replaced texture");
            var placeholder = Keep(new Texture2D(2, 2));
            var external = Keep(new Texture2D(3, 2));
            target.texture = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetImage(server.Root + "owned");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            var native = target.texture;
            Assert.That(native, Is.Not.Null.And.Not.SameAs(placeholder));
            target.texture = external;
            component.Clear();
            yield return Until(() => native == null);
            Assert.That(target.texture, Is.SameAs(external), "Clear may restore only a display whose current value is still its own.");
            Assert.That(external && placeholder, Is.True);
            yield return NoOwnedWork();
        }

        [UnityTest]
        public IEnumerator TargetDestroyedDuringHttpRequest_TerminatesWithoutPendingOrLease()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                entered.TrySetResult(true);
                await release.Task;
                await Send(context, _png);
            });
            var target = NewTarget<RawImage>("Destroyed request target");
            var component = Keep(new GameObject("Surviving request consumer")).AddComponent<ResourceImage>();
            component.Target = target;
            try
            {
                component.SetImage(server.Root + "pending");
                yield return Until(() => entered.Task.IsCompleted);
                Assert.That(component.IsLoading, Is.True);
                Object.Destroy(target.gameObject);
                yield return null;
                Assert.That(target == null && component.isActiveAndEnabled, Is.True);
                release.TrySetResult(true);
                yield return Until(() => component.Status != ResourceRequestStatus.Pending);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(component.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
                Assert.That(component.IsLoading, Is.False);
                yield return NoOwnedWork();
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [UnityTest]
        public IEnumerator SetNativeSize_ReentrantDisable_ReleasesDisplayedTextureAndLease()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            using var server = new ImageServer(context => Send(context, _png));
            var target = NewTarget<RawImage>("Native size reentry");
            target.rectTransform.sizeDelta = new Vector2(100, 100);
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetNativeSize = true;
            var observer = target.gameObject.AddComponent<AddressablesImageDimensionsObserver>();
            Texture native = null;
            int callbacks = 0;
            observer.Changed = () =>
            {
                if (target.texture && component.enabled)
                {
                    callbacks++;
                    native = target.texture;
                    component.enabled = false;
                }
            };
            try
            {
                component.SetImage(server.Root + "sized");
                yield return Until(() => !component.enabled);
                Assert.That(callbacks, Is.GreaterThan(0), "The real RectTransform sizing callback must execute inside application of the decoded texture.");
                Assert.That(ReferenceEquals(native, null), Is.False);
                yield return Until(() => native == null);
                Assert.That(target.texture, Is.Null);
                Assert.That(component.IsLoading, Is.False);
                Assert.That(component.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
                yield return NoOwnedWork();
            }
            finally
            {
                observer.Changed = null;
            }
        }

        [UnityTest]
        public IEnumerator ExternallyDestroyedBackingTexture_FailsAndReleasesOwnedSpriteAndLease()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            using var server = new ImageServer(context => Send(context, _png));
            var placeholder = Placeholder();
            var target = NewTarget<Image>("Destroyed backing texture");
            target.sprite = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetImage(server.Root + "backing");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            var ownedSprite = target.sprite;
            var native = ownedSprite.texture;
            Assert.That(ownedSprite, Is.Not.SameAs(placeholder));
            Object.Destroy(native);
            yield return Until(() => component.Status == ResourceRequestStatus.Failed);
            Assert.That(component.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(component.IsLoading, Is.False);
            yield return Until(() => ownedSprite == null);
            Assert.That(native == null, Is.True);
            Assert.That(target.sprite, Is.SameAs(placeholder));
            Assert.That(placeholder && placeholder.texture, Is.True);
            yield return NoOwnedWork();
        }

        [UnityTest]
        public IEnumerator SingleLoadSlot_CanceledQueuedConsumersNeverIssueHttpGet()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0, maximumConcurrentLoads: 1), bindCoreResourceProvider: false);
            var entered = new ConcurrentQueue<string>();
            var release = new TaskCompletionSource<bool>();
            using var server = new ImageServer(async context =>
            {
                entered.Enqueue(context.Request.RawUrl);
                await release.Task;
                await Send(context, _png);
            });
            var first = Own(_startup.Resources.AcquireImageAsync(server.Root + "active"));
            var shared = Own(_startup.Resources.AcquireImageAsync(server.Root + "active"));
            try
            {
                yield return Until(() => entered.Count == 1);
                var canceled = Own(_startup.Resources.AcquireImageAsync(server.Root + "canceled"));
                var disposed = Own(_startup.Resources.AcquireImageAsync(server.Root + "disposed"));
                var survivor = Own(_startup.Resources.AcquireImageAsync(server.Root + "survivor"));
                for (int frame = 0; frame < 4; frame++)
                    yield return null;
                Assert.That(entered.Count, Is.EqualTo(1), "The explicitly configured single slot must bound distinct HTTP operations.");
                canceled.Cancel();
                disposed.Dispose();
                first.Cancel();
                first.Dispose();
                canceled.Dispose();
                Assert.That(shared.IsDone, Is.False, "Canceling one shared consumer must not release the remaining consumer's occupied slot.");
                Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                Assert.That(disposed.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                release.TrySetResult(true);
                yield return Until(() => shared.IsDone && survivor.IsDone);
                Assert.That(shared.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shared.Error?.ToString());
                Assert.That(survivor.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), survivor.Error?.ToString());
                var sharedLease = Own(shared.TakeResult());
                var survivorLease = Own(survivor.TakeResult());
                Assert.That(sharedLease.Asset.width, Is.EqualTo(7));
                Assert.That(survivorLease.Asset.width, Is.EqualTo(7));
                sharedLease.Dispose();
                survivorLease.Dispose();
                yield return NoOwnedWork();
                CollectionAssert.AreEquivalent(new[] { "/active", "/survivor" }, entered.ToArray(), "Canceled queued work must never issue GET, including while old official handles drain; no FIFO order is required.");
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [UnityTest]
        public IEnumerator DestroyActiveComponent_ReleasesNewImage_LeavesIndependentOldImageAlive()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            byte[] nextBytes = Encode(11, 4);
            using var server = new ImageServer(async context =>
            {
                if (context.Request.RawUrl == "/old")
                {
                    entered.TrySetResult(true);
                    await release.Task;
                    await Send(context, _png);
                }
                else
                    await Send(context, nextBytes);
            });
            var oldRequest = Own(_startup.Resources.AcquireImageAsync(server.Root + "old"));
            var placeholder = Keep(new Texture2D(2, 2));
            var target = NewTarget<RawImage>("Active destruction");
            target.texture = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            try
            {
                component.SetImage(server.Root + "old");
                yield return Until(() => entered.Task.IsCompleted);
                component.SetImage(server.Root + "new");
                yield return Until(() => component.Status != ResourceRequestStatus.Pending);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
                var native = target.texture;
                Assert.That(native.width, Is.EqualTo(11));
                release.TrySetResult(true);
                yield return Until(() => oldRequest.IsDone);
                Assert.That(oldRequest.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), oldRequest.Error?.ToString());
                var independent = Own(oldRequest.TakeResult());
                var oldNative = independent.Asset;
                Assert.That(oldNative.width, Is.EqualTo(7));
                Assert.That(target.texture, Is.SameAs(native));
                Assert.That(component.isActiveAndEnabled && target.gameObject.activeInHierarchy, Is.True);
                Object.Destroy(component);
                yield return Until(() => component == null && native == null);
                Assert.That(target && target.gameObject.activeInHierarchy, Is.True, "Destroy the active ResourceImage component directly, not an already disabled host.");
                Assert.That(target.texture, Is.SameAs(placeholder));
                Assert.That(independent.IsValid && oldNative && placeholder, Is.True);
                Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));
                independent.Dispose();
                yield return Until(() => oldNative == null);
                yield return NoOwnedWork();
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [UnityTest]
        public IEnumerator RestoreSetterRebind_HotImageIsNotOverwrittenByOuterRefresh()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            int gets = 0;
            using var server = new ImageServer(context =>
            {
                Interlocked.Increment(ref gets);
                return Send(context, _png);
            });
            var warmRequest = Own(_startup.Resources.AcquireImageAsync(server.Root + "second"));
            yield return Until(() => warmRequest.IsDone);
            Assert.That(warmRequest.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), warmRequest.Error?.ToString());
            var warm = Own(warmRequest.TakeResult());
            var placeholder = Placeholder();
            var target = NewTarget<Image>("Restore setter rebind");
            target.sprite = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetImage(server.Root + "first");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            var oldSprite = target.sprite;
            var oldNative = oldSprite.texture;
            bool entered = false;
            UnityAction observer = () =>
            {
                if (entered)
                    return;
                entered = true;
                component.SetImage(server.Root + "second");
            };
            target.RegisterDirtyVerticesCallback(observer);
            try
            {
                component.Refresh();
                Assert.That(entered, Is.True, "Restore must actually execute the external UI setter callback.");
                yield return Until(() => component.Status != ResourceRequestStatus.Pending);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
                Assert.That(target.sprite.texture, Is.SameAs(warm.Asset));
            }
            finally
            {
                target.UnregisterDirtyVerticesCallback(observer);
            }
            component.Clear();
            yield return Until(() => oldSprite == null && oldNative == null);
            Assert.That(target.sprite, Is.SameAs(placeholder));
            Assert.That(warm.IsValid, Is.True);
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));
            Assert.That(Volatile.Read(ref gets), Is.EqualTo(2), "The outer refresh must not acquire or download a third generation.");
            var nextNative = warm.Asset;
            warm.Dispose();
            yield return Until(() => nextNative == null);
            yield return NoOwnedWork();
        }

        [UnityTest]
        public IEnumerator OldSetterRebindThenThrow_DoesNotFailOrReleaseNewGeneration()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            using var server = new ImageServer(context => Send(context, _png));
            var firstRequest = Own(_startup.Resources.AcquireImageAsync(server.Root + "first"));
            var secondRequest = Own(_startup.Resources.AcquireImageAsync(server.Root + "second"));
            yield return Until(() => firstRequest.IsDone && secondRequest.IsDone);
            Assert.That(firstRequest.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), firstRequest.Error?.ToString());
            Assert.That(secondRequest.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), secondRequest.Error?.ToString());
            var firstWarm = Own(firstRequest.TakeResult());
            var secondWarm = Own(secondRequest.TakeResult());
            var target = NewTarget<Image>("Old setter throws after rebind");
            var component = target.gameObject.AddComponent<ResourceImage>();
            bool entered = false;
            UnityAction observer = () =>
            {
                if (entered)
                    return;
                entered = true;
                component.SetImage(server.Root + "second");
                throw new InvalidOperationException("AA image old setter callback");
            };
            target.RegisterDirtyVerticesCallback(observer);
            try
            {
                component.SetImage(server.Root + "first");
                yield return Until(() => entered && component.Status != ResourceRequestStatus.Pending);
                Assert.That(entered, Is.True);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                Assert.That(component.Error, Is.Null);
                Assert.That(target.sprite.texture, Is.SameAs(secondWarm.Asset));
                Assert.That(firstWarm.IsValid && secondWarm.IsValid, Is.True);
            }
            finally
            {
                target.UnregisterDirtyVerticesCallback(observer);
            }
            var currentSprite = target.sprite;
            component.Clear();
            yield return Until(() => currentSprite == null);
            Assert.That(target.sprite, Is.Null);
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(2), "An obsolete setter exception cannot revoke a new generation or leak the old one.");
            firstWarm.Dispose();
            secondWarm.Dispose();
            yield return NoOwnedWork();
        }

        [UnityTest]
        public IEnumerator RestoreSetterThrows_StillDestroysOwnedSpriteAndReleasesDecodedTexture()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            using var server = new ImageServer(context => Send(context, _png));
            var placeholder = Placeholder();
            var target = NewTarget<Image>("Restore setter throws");
            target.sprite = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetImage(server.Root + "restore-throw");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            var sprite = target.sprite;
            var native = sprite.texture;
            int callbacks = 0;
            UnityAction observer = () =>
            {
                callbacks++;
                throw new InvalidOperationException("AA image restore setter callback");
            };
            target.RegisterDirtyVerticesCallback(observer);
            try
            {
                Assert.Throws<InvalidOperationException>(() => component.Clear());
            }
            finally
            {
                target.UnregisterDirtyVerticesCallback(observer);
            }
            Assert.That(callbacks, Is.EqualTo(1));
            yield return Until(() => sprite == null && native == null);
            Assert.That(target.sprite, Is.SameAs(placeholder));
            Assert.That(placeholder && placeholder.texture, Is.True);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(component.IsLoading, Is.False);
            yield return NoOwnedWork();
        }

        [UnityTest]
        public IEnumerator FreshDecodedResult_DisposedUnclaimedByCompletionObserver_DestroysNativeTexture()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            int gets = 0;
            using var server = new ImageServer(context =>
            {
                Interlocked.Increment(ref gets);
                return Send(context, _png);
            });
            Texture2D native = null;
            bool borrowed = false;
            int observers = 0;
            var request = Own(_startup.Resources.AcquireImageAsync(server.Root + "fresh-unclaimed"));
            request.Completed += completed =>
            {
                observers++;
                if (completed.Status != ResourceRequestStatus.Succeeded)
                    return;
                var key = _startup.Resources.GetDiagnostics().Leases.Single().Key;
                borrowed = _startup.Resources.TryAcquireLoaded<Texture2D>(key, out var probe);
                using (probe)
                {
                    if (borrowed)
                        native = probe.Asset;
                    completed.Dispose();
                }
            };
            yield return Until(() => request.IsDone);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            Assert.That(request.IsDisposed && borrowed, Is.True);
            Assert.That(observers, Is.EqualTo(1));
            Assert.That(ReferenceEquals(native, null), Is.False, "Capture the actual freshly decoded Texture through a separate public hot lease without claiming the original result.");
            Assert.Throws<ObjectDisposedException>(() => request.TakeResult());
            yield return Until(() => native == null);
            Assert.That(Volatile.Read(ref gets), Is.EqualTo(1));
            yield return NoOwnedWork();
        }

        [UnityTest]
        public IEnumerator OfficialAtlasAddress_OwnsClone_NotAtlasOrOtherConsumerClone()
        {
            yield return Start();
            var request = Own(_startup.Resources.AcquireAsync<SpriteAtlas>("ownership/atlas"));
            yield return request;
            var independent = Own(request.TakeResult());
            var atlas = independent.Asset;
            var externalClone = Keep(atlas.GetSprite("Pixels"));
            Assert.That(externalClone && externalClone.texture, Is.True);
            var target = NewTarget<Image>("Official Atlas");
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetAtlasSprite("ownership/atlas", "Pixels");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            var clone = target.sprite;
            Assert.That(clone, Is.Not.SameAs(externalClone));
            Assert.That(clone.texture, Is.SameAs(externalClone.texture), "Both consumers must use the official packed pixels.");
            component.Clear();
            yield return Until(() => clone == null);
            Assert.That(independent.IsValid && atlas && externalClone && externalClone.texture, Is.True);
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));
            Object.Destroy(externalClone);
            yield return Until(() => externalClone == null);
            independent.Dispose();
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.Zero);
        }
        [UnityTest]
        public IEnumerator AtlasColorSwitch_DestroysOnlyOldClone_LeavesOtherConsumerPixelsAlive()
        {
            yield return Start(new ResourceImageOptions(idleCapacityBytes: 0), bindCoreResourceProvider: false);
            const string AtlasAddress = "ownership/color-atlas";
            var request = Own(_startup.Resources.AcquireAsync<SpriteAtlas>(AtlasAddress));
            yield return Until(() => request.IsDone);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            var independent = Own(request.TakeResult());
            var atlas = independent.Asset;
            Assert.That(atlas.spriteCount, Is.EqualTo(2));
            var otherClone = Keep(atlas.GetSprite("Red"));
            Assert.That(otherClone && otherClone.texture, Is.True);
            var packed = otherClone.texture;
            bool readPixels = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32);
            if (!readPixels)
                Debug.Log("AA Atlas clone lifetime assertions remain active; GPU color readback is not executed on this graphics device: " + SystemInfo.graphicsDeviceType);
            if (readPixels)
                AssertSpritePixel(otherClone, Color.red);
            var placeholder = Placeholder();
            var target = NewTarget<Image>("Two-color Atlas switch");
            target.sprite = placeholder;
            var component = target.gameObject.AddComponent<ResourceImage>();
            component.SetAtlasSprite(AtlasAddress, "Blue");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            var first = target.sprite;
            Assert.That(first, Is.Not.SameAs(otherClone));
            Assert.That(first.texture, Is.SameAs(packed));
            if (readPixels)
                AssertSpritePixel(first, Color.blue);
            component.SetAtlasSprite(AtlasAddress, "Red");
            yield return Until(() => component.Status != ResourceRequestStatus.Pending);
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
            yield return Until(() => first == null);
            var last = target.sprite;
            Assert.That(last, Is.Not.SameAs(otherClone));
            Assert.That(last.texture, Is.SameAs(packed));
            Assert.That(otherClone && independent.IsValid, Is.True);
            if (readPixels)
            {
                AssertSpritePixel(last, Color.red);
                AssertSpritePixel(otherClone, Color.red);
            }
            component.Clear();
            yield return Until(() => last == null);
            Assert.That(target.sprite, Is.SameAs(placeholder));
            Assert.That(otherClone && packed && atlas && independent.IsValid, Is.True);
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.EqualTo(1));
            if (readPixels)
                AssertSpritePixel(otherClone, Color.red);
            Object.Destroy(otherClone);
            yield return Until(() => otherClone == null);
            independent.Dispose();
            yield return NoOwnedWork();
            Assert.That(placeholder && placeholder.texture, Is.True);
        }

        private static void AssertSpritePixel(Sprite sprite, Color expected)
        {
            var texture = sprite.texture;
            var render = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var sample = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(texture, render);
                RenderTexture.active = render;
                var uv = sprite.uv;
                Assert.That(uv.Length, Is.GreaterThanOrEqualTo(3));
                var center = new Vector2(uv.Average(point => point.x) * texture.width, uv.Average(point => point.y) * texture.height);
                sample.ReadPixels(new Rect(Mathf.Clamp(Mathf.Floor(center.x), 0, texture.width - 1), Mathf.Clamp(Mathf.Floor(center.y), 0, texture.height - 1), 1, 1), 0, 0);
                sample.Apply();
                var actual = sample.GetPixel(0, 0);
                Assert.That(actual.r, Is.EqualTo(expected.r).Within(.04f));
                Assert.That(actual.g, Is.EqualTo(expected.g).Within(.04f));
                Assert.That(actual.b, Is.EqualTo(expected.b).Within(.04f));
                Assert.That(actual.a, Is.GreaterThan(.95f));
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(render);
                Object.DestroyImmediate(sample);
            }
        }

        private T Keep<T>(T asset) where T : Object
        {
            _assets.Add(asset);
            return asset;
        }

        private T NewTarget<T>(string name) where T : Graphic
            => Keep(new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(T))).GetComponent<T>();

        private Sprite Placeholder()
        {
            var texture = Keep(new Texture2D(2, 2));
            return Keep(Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * .5f));
        }

        private IEnumerator NoOwnedWork()
        {
            Assert.That(_startup.CoreProvider, Is.Null, "Image-only cleanup must not wait for a deliberately live Core.ApplicationScenes scope.");
            Assert.That(_startup.Resources.Configuration.Images.IdleCapacityBytes, Is.Zero, "These native-destruction cases explicitly disable idle caching; default TTL is a separate tested contract.");
            yield return Until(() =>
            {
                var report = _startup.Resources.GetDiagnostics();
                return report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 &&
                    report.PendingWorkCount == 0 && report.DrainingHandleCount == 0;
            });
            Assert.That(_startup.Resources.GetDiagnostics().Errors, Is.Empty);
            Assert.That(_startup.Resources.CachedImageCount, Is.Zero);
        }

        private void RegisterAsset(string address, Object asset)
        {
            _provider = new BorrowedImageProvider(asset);
            Addressables.ResourceManager.ResourceProviders.Add(_provider);
            _locator = new ResourceLocationMap("YL image regression " + Guid.NewGuid());
            _locator.Add(address, new ResourceLocationBase(address, address, _provider.ProviderId, asset.GetType()));
            Addressables.AddResourceLocator(_locator);
        }

        private sealed class BorrowedImageProvider : ResourceProviderBase
        {
            private readonly Object _asset;
            internal int Releases;
            internal BorrowedImageProvider(Object asset)
            {
                _asset = asset;
                m_ProviderId = "YL image fixture " + Guid.NewGuid();
            }
            public override bool CanProvide(Type type, IResourceLocation location) => type.IsInstanceOfType(_asset);
            public override void Provide(ProvideHandle handle) => handle.Complete(_asset, true, null);
            public override void Release(IResourceLocation location, object asset) => Releases++;
        }

        private static byte[] Encode(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                return texture.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private IEnumerator Until(Func<bool> condition, [CallerMemberName] string operation = null, [CallerLineNumber] int line = 0)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }
            var resources = _startup?.Resources;
            var report = resources?.GetDiagnostics();
            string state = report == null ? "no current owned service" :
                $"{report.State}; leases={report.LeaseCount}; scopes={report.ScopeCount} [{string.Join(",", report.Scopes.Select(scope => scope.Owner))}]; requests={report.RequestCount}; work={report.PendingWorkCount}; drains={report.DrainingHandleCount}; idle={resources.CachedImageCount}; errors={string.Join(";", report.Errors)}";
            Assert.That(condition(), Is.True, $"{operation}:{line} did not settle within 10 seconds. {state}");
        }

        private static void ExpectProviderFailure(string message)
        {
            // 官方 runtime data 可以关闭 RM 日志；有默认日志处理器时只接受本次已知失败，不屏蔽其他错误。
            if (ResourceManager.ExceptionHandler != null)
            {
                LogAssert.Expect(LogType.Error, new Regex(Regex.Escape(message)));
            }
        }

        private sealed class ImageServer : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly Task _listening;
            private readonly ConcurrentBag<Task> _responses = new();
            internal readonly string Root;

            internal ImageServer(Func<HttpListenerContext, Task> handler)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                Root = "http://127.0.0.1:" + ((IPEndPoint)probe.LocalEndpoint).Port + "/";
                probe.Stop();
                _listener.Prefixes.Add(Root);
                _listener.Start();
                _listening = Task.Run(async () =>
                {
                    try
                    {
                        while (_listener.IsListening)
                        {
                            _responses.Add(handler(await _listener.GetContextAsync()));
                        }
                    }
                    catch (HttpListenerException) when (!_listener.IsListening) { }
                    catch (ObjectDisposedException) when (!_listener.IsListening) { }
                });
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
                Assert.That(_listening.Wait(TimeSpan.FromSeconds(3)), Is.True);
                Assert.That(Task.WaitAll(_responses.ToArray(), TimeSpan.FromSeconds(3)), Is.True);
            }
        }

        private static async Task Send(HttpListenerContext context, byte[] bytes, int status = 200, bool chunked = false)
        {
            try
            {
                context.Response.StatusCode = status;
                context.Response.SendChunked = chunked;
                if (!chunked)
                {
                    context.Response.ContentLength64 = bytes.Length;
                }
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (HttpListenerException) { /* A consumer may already have released its connection. */ }
            catch (ObjectDisposedException) { /* Fixture shutdown can close a completed response. */ }
            finally
            {
                context.Response.Close();
            }
        }
    }

    public sealed class AddressablesImageDimensionsObserver : UIBehaviour
    {
        public Action Changed;
        protected override void OnRectTransformDimensionsChange() => Changed?.Invoke();
    }
}
