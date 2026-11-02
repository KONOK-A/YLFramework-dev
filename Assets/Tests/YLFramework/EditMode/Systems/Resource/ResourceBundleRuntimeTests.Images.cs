using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.U2D;
using UnityEngine.UI;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceBundleRuntimeTests
    {
        private static void AddAtlas89Fixture(ResourceReleaseFixture fixture)
        {
            var sprites = fixture.Folder + "/Content/AtlasSprites.png";
            Assert.That(AssetDatabase.CopyAsset(fixture.Folder + "/Content/Sprites.png", sprites), Is.True);
            var atlas = ResourceAtlasTests.AddAtlas(fixture, spritePath: sprites);
            var project = fixture.Project;
            project.groups.Add(new ResourceGroup
            {
                id = "atlas89",
                selections = new List<ResourceSelection> {
                new() { guid = AssetDatabase.AssetPathToGUID(atlas) } }
            });
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            Assert.That(ResourceEditorOperations.PrepareAtlases(project), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExplicitAtlasClonesUseNormalLeasesAndBuiltInMountDoesNotCreateCache()
        {
            yield return new EnterPlayMode();
            yield return AtlasImageBody89();
        }

        private static IEnumerator AtlasImageBody89()
        {
            var snapshot = Snapshot();
            var cache = Path.Combine(Path.GetTempPath(), "kilo", "ab89-" + System.Guid.NewGuid().ToString("N").Substring(0, 16));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cache)), Is.False);
            var store = new ArtifactStore(cache, new[] { new ResourceProjectMount(snapshot.Document.ProjectId, snapshot.Document.Platform, "fixture-v1", snapshot) });
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(store));
            yield return Wait(initialized);
            Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
            var system = initialized.TakeResult();
            Assert.That(system, Is.Not.Null, "Successful initialization must return the borrowed authority.");
            Assert.That(system, Is.SameAs(ResourceSystem.Current));
            Debug.Log("YL89 native Atlas initialized; requesting formal Atlas address.");
            using var request = system.AcquireAsync<SpriteAtlas>(Address(snapshot, "Explicit.spriteatlasv2"));
            yield return Wait(request);
            using var held = request.TakeResult();
            Assert.That(held, Is.Not.Null);
            var atlas = held.Asset;
            Assert.That(atlas, Is.Not.Null);
            var probe = atlas.GetSprite("Left");
            Debug.Log("YL89 Play Atlas clone: found=" + (bool)probe + "; texture=" + (probe && probe.texture));
            Assert.That(probe && probe.texture, Is.True, "GetSprite must reference a real packed texture, without a global late-binding service.");
            var texture = new Texture2D(2, 2);
            var placeholder = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one / 2);
            var host = new GameObject("Atlas image89", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            try
            {
                var target = host.GetComponent<Image>();
                target.sprite = placeholder;
                var image = host.AddComponent<ResourceImage>();
                image.SetAtlasSprite(Address(snapshot, "Explicit.spriteatlasv2"), "Left");
                yield return UntilScene85(() => image.Status != ResourceRequestStatus.Pending);
                Assert.That(image.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), image.Error?.ToString());
                var first = target.sprite;
                var packedTexture = first.texture;
                Assert.That(first, Is.Not.SameAs(probe));
                Assert.That(first.texture, Is.SameAs(probe.texture));
                Assert.That(first.rect.width, Is.EqualTo(2));
                image.Refresh();
                yield return UntilScene85(() => image.Status != ResourceRequestStatus.Pending);
                yield return null;
                Assert.That(first == null, Is.True, "Refresh destroys the previous consumer clone, not the packed texture.");
                Assert.That(target.sprite.texture, Is.SameAs(probe.texture));
                image.SetAtlasSprite(Address(snapshot, "Explicit.spriteatlasv2"), "missing");
                yield return UntilScene85(() => image.Status != ResourceRequestStatus.Pending);
                Assert.That(image.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
                Assert.That(target.sprite, Is.SameAs(placeholder));
                Assert.That(held.IsValid && probe.texture, Is.True);
                image.SetAtlasSprite(Address(snapshot, "Explicit.spriteatlasv2"), "Right");
                yield return UntilScene85(() => image.Status != ResourceRequestStatus.Pending);
                var last = target.sprite;
                image.enabled = false;
                yield return null;
                Assert.That(last == null, Is.True);
                Assert.That(target.sprite, Is.SameAs(placeholder));
                Assert.That(held.IsValid, Is.True);
                Object.Destroy(probe);
                probe = null;
                held.Dispose();
                yield return UntilScene85(() => system.GetDiagnosticsSnapshot().BundleRecordCount == 0);
                Assert.That(atlas == null, Is.True);
                Assert.That(packedTexture == null, Is.True, "The final Atlas Lease must unload its real Bundle texture, not borrow an Editor preview.");
                image.enabled = true;
                yield return UntilScene85(() => image.Status != ResourceRequestStatus.Pending);
                Assert.That(image.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), image.Error?.ToString());
                var sessionClone = target.sprite;
                typeof(ResourceSystem).GetMethod("EndSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
                yield return null;
                yield return null;
                Assert.That(sessionClone == null, Is.True);
                Assert.That(target.sprite, Is.SameAs(placeholder));
                Assert.That(placeholder && texture, Is.True);
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cache)), Is.False, "Built-in mount and native consumption must not create or copy into the persistent store.");
            }
            finally
            {
                if (probe)
                {
                    Object.Destroy(probe);
                }

                Object.DestroyImmediate(host);
                Object.Destroy(placeholder);
                Object.Destroy(texture);
            }
        }

        [UnityTest]
        public IEnumerator ImageComponentUsesRealCatalogSpriteAndNeverDestroysAnotherConsumersBorrowedAsset()
        {
            yield return new EnterPlayMode();
            yield return CatalogImageBody89();
        }

        private static IEnumerator CatalogImageBody89()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var descriptor = snapshot.Document.Assets.Single(value => value.SubAssetName == "Left");
            var address = Address(snapshot, descriptor.Address, descriptor.SubAssetKey);
            using var pending = system.AcquireAsync<Sprite>(address);
            yield return Wait(pending);
            using var independent = pending.TakeResult();
            var native = independent.Asset;
            var host = new GameObject("YL89 native sprite consumer");
            try
            {
                var first = new GameObject("first target", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
                var second = new GameObject("second target", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
                first.transform.SetParent(host.transform);
                second.transform.SetParent(host.transform);
                var component = host.AddComponent<ResourceImage>();
                component.Target = first;
                component.SetImage(address);
                Assert.That(component.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), component.Error?.ToString());
                Assert.That(first.sprite, Is.SameAs(native));
                var nativeLoads = system.GetDiagnosticsSnapshot().NativeBundleLoadCount;
                component.Target = second;
                Assert.That(first.sprite, Is.Null, "Changing targets restores only the previous target's owned display.");
                Assert.That(second.sprite, Is.SameAs(native));
                Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.EqualTo(nativeLoads));
                Assert.That(system.GetDiagnosticsReport().Assets.Single(value => value.Address == address).Owners
                    .Any(value => value.OwnerLabel != null && value.OwnerLabel.Contains("second target")), Is.True);
                component.enabled = false;
                yield return null;
                Assert.That(second.sprite, Is.Null);
                Assert.That(independent.IsValid, Is.True);
                Assert.That(native == null, Is.False, "Catalog Sprite is borrowed from its Bundle, not created by the UI consumer.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
            independent.Dispose();
            Assert.That(native == null, Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator Native89OwnersPreloadImagesAndCoreRegressionShareOnePublication()
        {
            yield return new EnterPlayMode();
            yield return DiagnosticsBody89();
            yield return CloseCurrent();
            yield return GroupPreloadBody89();
            yield return CloseCurrent();
            yield return CatalogImageBody89();
            yield return CloseCurrent();
            yield return NativeBody();
            yield return CloseCurrent();
            yield return BatchBody();
            yield return CloseCurrent();
            yield return BundleObserversBody();
            yield return CloseCurrent();
            yield return AssetNativeCancelBody();
            yield return CloseCurrent();
            yield return Scene85OwnershipBody();
            yield return CloseCurrent();
            yield return CoreNativeInstancesBody();
            yield return CloseCurrent();
        }
    }
}
