using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.U2D;
using YLFramework.Resource;

namespace YLFramework.Tests.EditMode
{
    [Category("YL.Special.ResourceUpdates")]
    public sealed class ResourceContentCatalogTests
    {
        private const string RuntimeRoot = "G:/Project/StreamingAssets/aa";
        private const string Parent = ResourceContentCatalog.RuntimeToken + "/root.bundle";
        private const string Child = ResourceContentCatalog.RuntimeToken + "/child.bundle";

        [Test]
        public void CanonicalizesLocalRoutesAndKeepsBundleDependencyEdges()
        {
            var child = Bundle(RuntimeRoot + "/child.bundle");
            var parent = Bundle(Parent, child);
            var locator = new ResourceLocationMap("test-catalog");
            locator.Add("material", new ResourceLocationBase("material", "Assets/Material.txt",
                typeof(BundledAssetProvider).FullName, typeof(TextAsset), parent));

            var catalog = ResourceContentCatalog.InspectLocator(locator, RuntimeRoot);

            Assert.That(catalog.BundleRoutes.Keys, Is.EquivalentTo(new[] { Parent, Child }));
            Assert.That(catalog.BundleRoutes[Parent], Is.EqualTo(new[] { Child }));
            Assert.That(catalog.BundleRoutes[Child], Is.Empty);
        }

        [Test]
        public void ProjectsOfficialFlattenedAssetDependenciesOntoTheOwningBundle()
        {
            var child = Bundle(Child);
            var owner = Bundle(Parent);
            var locator = new ResourceLocationMap("packed-asset");
            locator.Add("chapter", new ResourceLocationBase("chapter", "Assets/Chapter.txt",
                typeof(BundledAssetProvider).FullName, typeof(TextAsset), owner, child));

            var catalog = ResourceContentCatalog.InspectLocator(locator, RuntimeRoot);

            Assert.That(catalog.BundleRoutes[Parent], Is.EqualTo(new[] { Child }));
            Assert.That(catalog.BundleRoutes[Child], Is.Empty);
        }

        [Test]
        public void StockPackedSceneRetainsItsOwningBundleAndDependencies()
        {
            var locator = new ResourceLocationMap("packed-scene");
            locator.Add("chapter", new ResourceLocationBase("chapter", "Assets/Chapter.unity",
                typeof(BundledAssetProvider).FullName, typeof(SceneInstance), Bundle(Parent), Bundle(Child)));

            var catalog = ResourceContentCatalog.InspectLocator(locator, RuntimeRoot);

            Assert.That(catalog.BundleRoutes[Parent], Is.EqualTo(new[] { Child }));
        }

        [Test]
        public void StockInstalledBundleCanOmitDownloadOptions()
        {
            var stripped = Bundle(Parent);
            stripped.Data = null;
            var locator = new ResourceLocationMap("stripped-local");
            locator.Add("root", stripped);

            var catalog = ResourceContentCatalog.InspectLocator(locator, RuntimeRoot);

            Assert.That(catalog.BundleRoutes.Keys, Is.EqualTo(new[] { Parent }));
        }

        [Test]
        public void StockRemoteBundleStillRequiresDownloadOptions()
        {
            var stripped = Bundle("https://example.invalid/remote.bundle");
            stripped.Data = null;
            var locator = new ResourceLocationMap("stripped-remote");
            locator.Add("remote", stripped);

            Assert.Throws<InvalidDataException>(() => ResourceContentCatalog.InspectLocator(locator, RuntimeRoot));
        }

        [Test]
        public void DynamicSpriteUsesItsSignedAtlasAliasAndTheOfficialProvider()
        {
            var atlas = new ResourceLocationBase("main-atlas", "Assets/Icons.spriteatlas",
                typeof(BundledAssetProvider).FullName, typeof(SpriteAtlas), Bundle(Parent));
            var locator = new ResourceLocationMap("packed-atlas");
            locator.Add("icons", atlas);
            var catalog = ResourceContentCatalog.InspectLocator(locator, RuntimeRoot);
            var sprite = new ResourceLocationBase("icons[star]", "Assets/Icons.spriteatlas[star]",
                typeof(AtlasSpriteProvider).FullName, typeof(Sprite), atlas);
            var unlisted = new ResourceLocationBase("unlisted[star]", sprite.InternalId,
                sprite.ProviderId, typeof(Sprite), atlas);

            using var manager = new ResourceManager();
            manager.ResourceProviders.Add(new AtlasSpriteProvider());
            Assert.That(catalog.GetAtlasSpriteId(sprite, RuntimeRoot, manager), Is.EqualTo(sprite.InternalId));
            Assert.Throws<InvalidDataException>(() => catalog.GetAtlasSpriteId(unlisted, RuntimeRoot, manager));
        }

        [Test]
        public void RefusesTwoEquivalentBundleLocationsWithDifferentRequestOptions()
        {
            var locator = new ResourceLocationMap("test-catalog");
            var alternate = Bundle(RuntimeRoot + "/root.bundle");
            ((AssetBundleRequestOptions)alternate.Data).Crc = 42;
            locator.Add("root", Bundle(Parent));
            locator.Add("alias", alternate);

            Assert.Throws<InvalidDataException>(() => ResourceContentCatalog.InspectLocator(locator, RuntimeRoot));
        }

        [Test]
        public void RefusesCustomProviderByDefault()
        {
            var locator = new ResourceLocationMap("test-catalog");
            locator.Add("root", new ResourceLocationBase("root", Parent, "custom-provider", typeof(TextAsset)));

            Assert.Throws<InvalidDataException>(() => ResourceContentCatalog.InspectLocator(locator, RuntimeRoot));
        }

        private static ResourceLocationBase Bundle(string id, params IResourceLocation[] dependencies) =>
            new ResourceLocationBase(id, id, typeof(AssetBundleProvider).FullName, typeof(IAssetBundleResource), dependencies)
            {
                Data = new AssetBundleRequestOptions { BundleName = Path.GetFileNameWithoutExtension(id), BundleSize = 128 }
            };
    }
}
