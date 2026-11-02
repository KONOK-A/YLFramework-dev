using System.Collections;
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
        private static void AddAutomaticAtlasFixture(ResourceReleaseFixture fixture)
        {
            var source = fixture.Folder + "/Content/AutoAtlasSprites.png";
            Assert.That(AssetDatabase.CopyAsset(fixture.Folder + "/Content/Sprites.png", source), Is.True);
            var texture = new Texture2D(4, 2);
            try
            {
                texture.SetPixels(new[] { Color.green, Color.green, Color.blue, Color.blue, Color.green, Color.green, Color.blue, Color.blue });
                texture.Apply();
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
            AssetDatabase.ImportAsset(source, ImportAssetOptions.ForceSynchronousImport);
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta"));
            var project = fixture.Project;
            project.groups.Add(new ResourceGroup { id = "automatic-atlas" });
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            var configuration = ResourceAtlasGenerator.CreateConfiguration(project, fixture.Folder + "/Content", "GeneratedAtlases");
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "native",
                groupId = "automatic-atlas",
                address = "atlases/generated",
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(source) }
            });
            EditorUtility.SetDirty(configuration);
            AssetDatabase.SaveAssetIfDirty(configuration);
            ResourceAtlasGenerator.Generate(project);
            var map = ResourceBuildMap.Create(fixture.Project);
            Assert.That(map.Assets.Single(x => x.Address.Path == "atlases/generated").AssetType, Is.EqualTo(typeof(SpriteAtlas)));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta")));
        }

        [UnityTest]
        public IEnumerator AutomaticAtlasPixelsAndConsumerClonesUseTheExistingLeaseAndSharedTextureContract()
        {
            yield return new EnterPlayMode();
            yield return AutomaticAtlasBody();
        }

        private static IEnumerator AutomaticAtlasBody()
        {
            var snapshot = Snapshot();
            var descriptor = snapshot.Document.Assets.Single(x => x.Address == "atlases/generated");
            Assert.That(descriptor.Kind, Is.EqualTo("Asset"));
            var system = Initialize(snapshot);
            var address = Address(snapshot, "atlases/generated");
            using var request = system.AcquireAsync<SpriteAtlas>(address);
            yield return Wait(request);
            using var held = request.TakeResult();
            var atlas = held.Asset;
            var probe = atlas.GetSprite("Left");
            var host = new GameObject("B06 generated Atlas consumer", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            try
            {
                Assert.That(probe && probe.texture, Is.True);
                AssertSpritePixel(probe, Color.green);
                var packed = probe.texture;
                var image = host.AddComponent<ResourceImage>();
                image.SetAtlasSprite(address, "Right");
                yield return UntilScene85(() => image.Status != ResourceRequestStatus.Pending);
                Assert.That(image.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), image.Error?.ToString());
                var target = host.GetComponent<Image>();
                var first = target.sprite;
                Assert.That(first.texture, Is.SameAs(packed));
                AssertSpritePixel(first, Color.blue);
                image.SetAtlasSprite(address, "Left");
                yield return UntilScene85(() => image.Status != ResourceRequestStatus.Pending);
                yield return null;
                Assert.That(first == null, Is.True, "Only the replaced GetSprite clone is destroyed.");
                AssertSpritePixel(target.sprite, Color.green);
                var last = target.sprite;
                image.enabled = false;
                yield return null;
                Assert.That(last == null, Is.True);
                Assert.That(held.IsValid && packed, Is.True, "Another Atlas lease must keep the shared packed texture alive.");
                AssertSpritePixel(probe, Color.green);
                Object.Destroy(probe);
                probe = null;
                held.Dispose();
                yield return UntilScene85(() => system.GetDiagnosticsSnapshot().BundleRecordCount == 0);
                Assert.That(atlas == null && packed == null, Is.True, "Final lease unloads real native Atlas/texture, not just managed counters.");
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            }
            finally
            {
                if (probe)
                {
                    Object.Destroy(probe);
                }

                Object.DestroyImmediate(host);
            }
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
                var rect = sprite.textureRect;
                sample.ReadPixels(new Rect(Mathf.Floor(rect.center.x), Mathf.Floor(rect.center.y), 1, 1), 0, 0);
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
    }
}
