using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    public partial class ResourceImageTests
    {
        private string _file;
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Application.temporaryCachePath)), Is.True);
            _file = Path.Combine(Application.temporaryCachePath, "YL89-Image-" + Guid.NewGuid().ToString("N") + ".png");
            var image = new Texture2D(7, 3, TextureFormat.RGBA32, false);
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), image.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (_host)
            {
                Object.DestroyImmediate(_host);
            }

            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            typeof(ResourceSystem).GetMethod("ResetSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_file));
        }

        private static ResourceSystem Initialize(ResourceImageOptions options = null)
        {
            using var request = ResourceSystem.InitializeAsync(new ResourceConfiguration(Array.Empty<ResourceCatalog>(), options));
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            return request.TakeResult();
        }

        private static ResourceRequest<AssetLease<Texture2D>> Acquire(ResourceSystem system, string location)
            => system.AcquireImageAsync(location);

        private static void Flush(ResourceSystem system)
            => system.ClearImageCache();

        [UnityTest]
        public IEnumerator DirectImagesShareProductionCancelIndependentlyAndFlushOnlyIdleOwners()
        {
            var system = Initialize();
            using var first = Acquire(system, _file);
            using var second = Acquire(system, _file);
            first.Cancel();
            yield return second;
            Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), second.Error?.ToString());
            using var lease = second.TakeResult();
            var texture = lease.Asset;
            Assert.That(texture.width, Is.EqualTo(7));
            Flush(system);
            Assert.That(lease.IsValid, Is.True, "A cache flush cannot revoke an active consumer.");
            lease.Dispose();
            using (var cached = Acquire(system, _file))
            {
                Assert.That(cached.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                using var warm = cached.TakeResult();
                Assert.That(warm.Asset, Is.SameAs(texture));
            }
            Flush(system);
            yield return null;
            Assert.That(texture == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ImageComponentDisableReleasesOwnedSpriteAndDoesNotDestroyBorrowedPlaceholder()
        {
            var system = Initialize();
            _host = new GameObject("YL89 Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var target = _host.GetComponent<Image>();
            var placeholderTexture = new Texture2D(2, 2);
            var placeholder = Sprite.Create(placeholderTexture, new Rect(0, 0, 2, 2), Vector2.one * .5f);
            try
            {
                target.sprite = placeholder;
                var type = typeof(ResourceSystem).Assembly.GetType("YLFramework.Resource.ResourceImage");
                Assert.That(type, Is.Not.Null, "A formal Unity UI component must own its display lease.");
                var component = (Behaviour)_host.AddComponent(type);
                type.GetMethod("SetImage", new[] { typeof(string) }).Invoke(component, new object[] { _file });
                var until = Time.realtimeSinceStartup + 8;
                while (target.sprite == placeholder && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }

                Assert.That(target.sprite, Is.Not.Null.And.Not.SameAs(placeholder));
                var created = target.sprite;
                component.enabled = false;
                yield return null;
                Assert.That(created == null, Is.True, "The UI-created Sprite is not a borrowed project asset.");
                Assert.That(target.sprite, Is.SameAs(placeholder));
                Assert.That(placeholder, Is.Not.Null);
                Flush(system);
                yield return null;
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(_host);
                _host = null;
                Object.DestroyImmediate(placeholder);
                Object.DestroyImmediate(placeholderTexture);
            }
        }

        [UnityTest]
        public IEnumerator MissingAtlasSpriteReleasesItsLeaseWithoutDestroyingTheBorrowedAtlas()
        {
            var atlas = new UnityEngine.U2D.SpriteAtlas();
            try
            {
                var setter = typeof(ResourceImage).GetMethod("SetAtlasSprite", new[] { typeof(ResourceAddress), typeof(string) });
                Assert.That(setter, Is.Not.Null, "Atlas lookup must be an explicit Image operation, not an implicit global tag registry.");
                var address = new ResourceAddress("atlas-image", "icons");
                var source = new ResourceSystemTests.ControlledSource("test-atlas-image") { Asset = atlas, Immediate = true };
                var catalog = new ResourceCatalog(address.ProjectId, new[] { new ResourceCatalogEntry("atlas", address,
                    new YLFramework.Resource.ResourceLocation(source.Id, "borrowed-atlas", typeof(UnityEngine.U2D.SpriteAtlas))) });
                using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { catalog }, new IResourceSource[] { source }));
                var system = initialized.TakeResult();
                _host = new GameObject("AtlasImage89", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                var root = _host;
                var image = root.AddComponent<ResourceImage>();
                setter.Invoke(image, new object[] { address, "not-in-the-atlas" });
                yield return WaitUntil(() => image.Status != ResourceRequestStatus.Pending);
                Assert.That(image.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(image.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
                Assert.That(atlas, Is.Not.Null);
                Assert.That(root.GetComponent<Image>().sprite, Is.Null);
                typeof(ResourceImage).GetField("subAsset", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(image, "old-catalog-subasset");
                image.Refresh();
                Assert.That(source.Loads, Is.EqualTo(2), "Switching Inspector source mode must not retain a hidden Catalog subasset key for an Atlas main address.");
                Assert.That(image.Error.Message, Does.Contain("SpriteAtlas does not contain"));
            }
            finally
            {
                if (atlas)
                {
                    UnityEngine.Object.DestroyImmediate(atlas);
                }
            }
        }

        [UnityTest]
        public IEnumerator RawImageTargetReplacementDoesNotRestoreOverAnExternalChange()
        {
            var system = Initialize();
            _host = new GameObject("YL89 RawImage", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var target = _host.GetComponent<RawImage>();
            var external = new Texture2D(2, 2);
            try
            {
                var type = typeof(ResourceSystem).Assembly.GetType("YLFramework.Resource.ResourceImage");
                Assert.That(type, Is.Not.Null);
                var component = (Behaviour)_host.AddComponent(type);
                type.GetMethod("SetImage", new[] { typeof(string) }).Invoke(component, new object[] { _file });
                var until = Time.realtimeSinceStartup + 8;
                while (!target.texture && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }

                Assert.That(target.texture, Is.Not.Null);
                target.texture = external;
                component.enabled = false;
                Assert.That(target.texture, Is.SameAs(external), "Release only restores the display it actually owns.");
                Assert.That(external, Is.Not.Null);
                Flush(system);
            }
            finally
            {
                Object.DestroyImmediate(external);
            }
        }

        [UnityTest]
        public IEnumerator DestroyedTargetDuringRequestHasAStableFailureInsteadOfPendingForever()
        {
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            _host = new GameObject("YL89 destroyed target", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var component = _host.AddComponent<ResourceImage>();
            component.SetImage(_file);
            Object.DestroyImmediate(_host.GetComponent<RawImage>());
            var until = Time.realtimeSinceStartup + 8;
            while (component.Status == ResourceRequestStatus.Pending && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(component.IsLoading, Is.False);
            Assert.That(component.Error, Is.Not.Null);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CacheCapacityAndTtlEvictOnlyIdleImagesAndShutdownFlushesWithoutBusy()
        {
            var secondFile = _file + ".other.png";
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(_file), global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            try
            {
                var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 7 * 3 * 4, idleTimeToLive: TimeSpan.FromSeconds(.05)));
                var first = system.AcquireImage(_file);
                var nativeFirst = first.Asset;
                var second = system.AcquireImage(secondFile);
                var nativeSecond = second.Asset;
                first.Dispose();
                second.Dispose();
                yield return null;
                Assert.That(nativeFirst == null, Is.True, "LRU must evict the oldest zero-owner image when the byte budget is full.");
                var until = Time.realtimeSinceStartup + 2;
                while (nativeSecond && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }

                Assert.That(nativeSecond == null, Is.True, "TTL is enforced by the existing cleanup loop, not only the next acquire.");
                using (var last = system.AcquireImage(_file))
                {
                    Assert.That(last.IsValid, Is.True);
                }

                using var shutdown = system.ShutdownAsync();
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            }
            finally
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            }
        }

        [UnityTest]
        public IEnumerator TinyImagesAlsoHaveAnEntryCountBoundNotOnlyAPixelBudget()
        {
            var options = new ResourceImageOptions(maximumEncodedBytes: 1024, maximumPixels: 1024, idleCapacityBytes: 1024, idleCapacity: 1);
            var system = Initialize(options);
            var secondFile = _file + ".second.png";
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(_file), global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            try
            {
                var first = system.AcquireImage(_file);
                var old = first.Asset;
                first.Dispose();
                using (var second = system.AcquireImage(secondFile))
                {
                    Assert.That(second.IsValid, Is.True);
                }

                yield return null;
                Assert.That(old == null, Is.True);
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.EqualTo(1));
                Flush(system);
            }
            finally
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(secondFile));
            }
        }

        [UnityTest]
        public IEnumerator EncodedAndPixelBudgetsRejectBeforeNativeDecode()
        {
            var system = Initialize(new ResourceImageOptions(maximumEncodedBytes: 24));
            using (var tooManyBytes = system.AcquireImageAsync(_file))
            {
                yield return tooManyBytes;
                Assert.That(tooManyBytes.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(tooManyBytes.Error.ToString(), Does.Contain("byte limit"));
            }
            using (var closed = system.ShutdownAsync())
            {
                Assert.That(closed.TakeResult(), Is.True);
            }

            system = Initialize(new ResourceImageOptions(maximumPixels: 2));
            using (var tooManyPixels = system.AcquireImageAsync(_file))
            {
                yield return tooManyPixels;
                Assert.That(tooManyPixels.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(tooManyPixels.Error.ToString(), Does.Contain("pixel budget"));
            }
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator JpegHeaderIsValidatedAndTheDecodedDisplayTextureHasNoReadablePixelCopy()
        {
            var input = new Texture2D(9, 5, TextureFormat.RGB24, false);
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), input.EncodeToJPG());
            }
            finally
            {
                Object.DestroyImmediate(input);
            }
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            using var request = system.AcquireImageAsync(_file);
            yield return request;
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            var lease = request.TakeResult();
            var texture = lease.Asset;
            Assert.That(texture.width, Is.EqualTo(9));
            Assert.That(texture.height, Is.EqualTo(5));
            Assert.That(texture.isReadable, Is.False);
            lease.Dispose();
            yield return null;
            Assert.That(texture == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SetNativeSizeCannotCommitALeaseAfterReentrantDisable()
        {
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            _host = new GameObject("YL89 reentrant dimensions", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var target = _host.GetComponent<RawImage>();
            var component = _host.AddComponent<ResourceImage>();
            component.SetNativeSize = true;
            var reentrant = _host.AddComponent<ImageDimensionsObserver>();
            reentrant.Changed = () =>
            {
                if (target.texture)
                {
                    component.enabled = false;
                }
            };
            component.SetImage(_file);
            yield return WaitUntil(() => !component.enabled);
            yield return null;
            Assert.That(target.texture == null, Is.True, "The disabled consumer restores its original display.");
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero, "A UI sizing callback cannot publish ownership after OnDisable.");
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyedBackingTextureEndsTheSpriteConsumerAndReleasesItsLease()
        {
            var system = Initialize(new ResourceImageOptions(idleCapacityBytes: 0));
            _host = new GameObject("YL89 externally destroyed texture", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var component = _host.AddComponent<ResourceImage>();
            component.SetImage(_file);
            yield return WaitUntil(() => component.Status == ResourceRequestStatus.Succeeded);
            Object.Destroy(_host.GetComponent<Image>().sprite.texture);
            yield return null;
            yield return null;
            Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(_host.GetComponent<Image>().sprite, Is.Null);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void UrlIdentityRemainsExactWhileDiagnosticTextDoesNotExposeQueryCredentials()
        {
            const string Url = "https://example.invalid/Image?token=YL89-private&Version=AbC";
            var location = new YLFramework.Resource.ResourceLocation(ResourceSourceId.UrlTexture, Url, typeof(Texture2D));
            var different = new YLFramework.Resource.ResourceLocation(ResourceSourceId.UrlTexture, Url.Replace("AbC", "abc"), typeof(Texture2D));
            Assert.That(location.Locator, Is.EqualTo(Url));
            Assert.That(location.Equals(different), Is.False);
            Assert.That(location.ToString(), Does.Not.Contain("YL89-private"));
        }

        [Test]
        public void ArtifactStoreApplicationsCanConfigureTheSameImageSafetyAndCachePolicy()
        {
            string root = Path.Combine(Application.temporaryCachePath, "YL89-Policy-" + Guid.NewGuid().ToString("N"));
            var store = new ArtifactStore(root, new[] { new ResourceProjectMount("policy", "StandaloneWindows64", "v1") });
            var images = new ResourceImageOptions(idleCapacityBytes: 0);
            var configuration = new ResourceConfiguration(store, images: images);
            Assert.That(configuration.ArtifactStore, Is.SameAs(store));
            Assert.That(configuration.Images, Is.SameAs(images), "Versioned applications retain the actual image policy, including optional decryption support.");
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False, "Configuring image policy must not initialize or write the store.");
        }

        [UnityTest]
        public IEnumerator DecodedMemoryAndIdleReuseAreMeasuredSeparatelyFromEncodedFileBytes()
        {
            var input = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
            byte[] encoded;
            try
            {
                encoded = input.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(input);
            }
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_file), encoded);
            var system = Initialize();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var lease = system.AcquireImage(_file);
            var coldMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            var texture = lease.Asset;
            Assert.That(texture.isReadable, Is.False, "CPU pixel readback is not retained for display-only decoded images.");
            Assert.That(texture.mipmapCount, Is.EqualTo(1), "UI images do not require a generated mip chain.");
            var runtimeBytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
            lease.Dispose();
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            elapsed.Restart();
            using (var warm = system.AcquireImage(_file))
            {
                Assert.That(warm.Asset, Is.SameAs(texture));
            }

            var warmMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            Debug.Log($"YL8.9 image memory measurement: PNG={encoded.Length} bytes, RGBA idle budget estimate={1024L * 1024 * 4} bytes, Unity Profiler runtime sample={runtimeBytes} bytes, cold decode={coldMilliseconds:F2} ms, ready cache acquire={warmMilliseconds:F2} ms; not process peak/RSS.");
            Flush(system);
            yield return null;
            Assert.That(texture == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }
    }

    public sealed class ImageDimensionsObserver : UIBehaviour
    {
        public Action Changed;
        protected override void OnRectTransformDimensionsChange() => Changed?.Invoke();
    }
}
