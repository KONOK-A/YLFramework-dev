using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceLocalSnapshotTests
    {
        internal static object Open(ResourceRelease release)
        {
            var type = typeof(ResourceSystem).Assembly.GetType("YLFramework.Resource.ResourceLocalSnapshot");
            Assert.That(type, Is.Not.Null, "Runtime must mount an explicit verified Catalog v1 publication through its owned resource service.");
            try
            {
                return type.GetMethod("Open").Invoke(null, new object[] { release.DirectoryPath, release.Catalog.ProjectId,
                    release.Catalog.CatalogDigest, release.Catalog.Platform, "fixture-v1" });
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException;
            }
        }

        [Test]
        public void ExplicitBuiltInLayoutOpensAnExportWithoutWritingStreamingAssetsOrTheStore()
        {
            var method = typeof(ResourceLocalSnapshot).GetMethod("OpenBuiltIn");
            Assert.That(method, Is.Not.Null, "The formal built-in layout must be consumable without application-owned path glue.");
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            project.groups.RemoveAll(value => value.id != "entry");
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            var release = ResourcePublisher.Publish(fixture.Build(), "fixture-v1");
            fixture.ReleaseRoot = release.RootPath;
            var root = Path.Combine(Path.GetTempPath(), "kilo", "bi89-" + Guid.NewGuid().ToString("N").Substring(0, 16));
            Assert.That(Directory.Exists(root), Is.False);
            var parent = Path.Combine(root, release.Catalog.ProjectId, release.Catalog.Platform);
            Directory.CreateDirectory(parent);
            try
            {
                var path = Path.Combine(parent, release.Catalog.CatalogDigest);
                ResourceEditorOperations.ExportRelease(release, path);
                ResourceLocalSnapshot OpenBuiltIn() => (ResourceLocalSnapshot)method.Invoke(null, new object[] {
                    release.Catalog.ProjectId, release.Catalog.CatalogDigest, release.Catalog.Platform, "fixture-v1", root });
                var snapshot = OpenBuiltIn();
                Assert.That(snapshot.DirectoryPath, Is.EqualTo(path));
                var storeRoot = Path.Combine(root, "s");
                var store = new ArtifactStore(storeRoot, new[] { new ResourceProjectMount(snapshot.Document.ProjectId, snapshot.Document.Platform, "fixture-v1", snapshot) });
                Assert.That(store.Mount().Single().Document.CatalogDigest, Is.EqualTo(release.Catalog.CatalogDigest));
                Assert.That(Directory.Exists(storeRoot), Is.False, "Mount is pure; the built-in source is never copied merely to initialize.");
                var editorMeta = Path.Combine(path, "resource-catalog.json.meta");
                File.WriteAllText(editorMeta, "fixture-owned-editor-metadata");
                Assert.That(Assert.Throws<TargetInvocationException>(() => OpenBuiltIn()).InnerException, Is.TypeOf<InvalidDataException>(),
                    "Built-in lookup must preserve the strict closed file set rather than silently ignore Editor metadata.");
                File.Delete(editorMeta);
                var bundle = Path.Combine(path, snapshot.Document.Bundles.Single().RelativeFile);
                File.WriteAllText(bundle, "tampered");
                Assert.That(Assert.Throws<TargetInvocationException>(() => OpenBuiltIn()).InnerException, Is.TypeOf<InvalidDataException>());
                File.Delete(bundle);
                Assert.That(Assert.Throws<TargetInvocationException>(() => OpenBuiltIn()).InnerException, Is.TypeOf<InvalidDataException>());
                Assert.That(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] {
                    "../escape", release.Catalog.CatalogDigest, release.Catalog.Platform, "fixture-v1", root })).InnerException, Is.InstanceOf<ArgumentException>());
                fixture.AssertProtected();
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ExplicitPublicationMountValidatesEveryFileAndCompiledTypesWithoutNativeLoads()
        {
            using var fixture = new ResourceReleaseFixture();
            var build = fixture.Build();
            var release = ResourcePublisher.Publish(build, "fixture-v1");
            fixture.ReleaseRoot = release.RootPath;
            var root = Path.GetPathRoot(release.DirectoryPath);
            Assert.Throws<ArgumentException>(() => ResourceLocalSnapshot.Open(root.Substring(0, 2) + release.DirectoryPath.Substring(root.Length),
                release.Catalog.ProjectId, release.Catalog.CatalogDigest, release.Catalog.Platform, "fixture-v1"));
            var nativeBefore = AssetBundle.GetAllLoadedAssetBundles().Select(x => x.GetInstanceID()).ToArray();
            var snapshot = Open(release);
            var catalog = (ResourceCatalog)snapshot.GetType().GetMethod("CreateCatalog").Invoke(snapshot, new object[] { null });
            Assert.That(catalog.Resolve(new ResourceAddress(release.Catalog.ProjectId, "entry.txt")).Location.AssetType, Is.EqualTo(typeof(TextAsset)));
            foreach (var sprite in release.Catalog.Assets.Where(x => x.Kind == "SubAsset"))
            {
                Assert.That(catalog.Resolve(new ResourceAddress(release.Catalog.ProjectId, sprite.Address, sprite.SubAssetKey)).AssetId, Is.EqualTo(sprite.AssetId));
            }

            CollectionAssert.AreEquivalent(nativeBefore, AssetBundle.GetAllLoadedAssetBundles().Select(x => x.GetInstanceID()));
            var file = Path.Combine(release.DirectoryPath, release.Catalog.Bundles[0].RelativeFile);
            var bytes = File.ReadAllBytes(file);
            try
            {
                File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
                Assert.Throws<InvalidDataException>(() => Open(release));
                File.Delete(file);
                Assert.That(Assert.Throws<InvalidDataException>(() => Open(release)).Message, Is.EqualTo("Publication contains missing or unregistered content."));
            }
            finally
            {
                File.WriteAllBytes(file, bytes);
            }
            fixture.AssertProtected();
        }
    }
}
