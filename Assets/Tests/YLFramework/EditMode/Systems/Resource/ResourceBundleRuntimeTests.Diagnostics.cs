using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceBundleRuntimeTests
    {
        [UnityTest]
        public IEnumerator SimulationGroupScenePreviewsArePreparedWithoutLoadingAndRequiredFailureKeepsOutsideOwners()
        {
            yield return new EnterPlayMode();
            yield return SimulationGroupBody89();
        }

        private static IEnumerator SimulationGroupBody89()
        {
            var snapshot = Snapshot();
            var folder = SessionState.GetString(Key + "folder", "");
            foreach (var bindPublished in new[] { false, true })
            {
                var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
                var simulation = new ResourceEditorSimulation(new[] { project }, bindPublished ? new[] { snapshot.Document } : null);
                var config = simulation.CreateConfiguration(new ResourceImageOptions(maximumConcurrentLoads: 1));
                var catalog = config.Catalogs.Single();
                var sceneEntry = catalog.Entries.Single(value => value.Address.Path == "Owned.unity");
                Assert.That(sceneEntry.Location.AssetType, Is.EqualTo(typeof(SceneAsset)));
                var textEntry = catalog.Entries.Single(value => value.Address.Path == "entry.txt");
                catalog.Groups = catalog.Groups.Concat(new[] { new ResourceCatalogGroup("scene-preview89", new[] {
                    new ResourceCatalogGroupMember(textEntry.AssetId, true), new ResourceCatalogGroupMember(sceneEntry.AssetId, true) }) }).ToArray();
                using var initialized = ResourceSystem.InitializeAsync(config);
                Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), initialized.Error?.ToString());
                var system = initialized.TakeResult();
                using var outside = system.Acquire<TextAsset>(textEntry.Address);
                var sceneCount = SceneManager.sceneCount;
                using (var preload = system.PreloadGroupAsync(catalog.ProjectId, "scene-preview89"))
                {
                    yield return Wait(preload);
                    using var group = preload.TakeResult();
                    Assert.That(group.Items.Single(value => value.AssetId == sceneEntry.AssetId).Kind.ToString(), Is.EqualTo("ScenePreview"));
                    Assert.That(group.Assets.Count, Is.EqualTo(1));
                    Assert.That(group.Assets[0].Asset, Is.SameAs(outside.Asset));
                    Assert.That(group.SceneBundles, Is.Empty, "Simulation cannot fabricate a native BundleLease.");
                    Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));
                    Assert.That(system.GetSceneRecords(), Is.Empty);
                    Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
                }
                var source = sceneEntry.Location.Locator;
                var moved = source.Replace("Owned.unity", "Moved89.unity");
                Assert.That(AssetDatabase.MoveAsset(source, moved), Is.Empty);
                try
                {
                    using var failure = system.PreloadGroupAsync(catalog.ProjectId, "scene-preview89");
                    yield return UntilScene85(() => failure.IsDone);
                    Assert.That(failure.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                    Assert.That(failure.Error.InnerException, Is.TypeOf<ResourceGroupFailure>());
                    Assert.That(outside.IsValid, Is.True);
                    Assert.That(outside.Asset.text, Is.EqualTo("first release content"));
                    Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));
                    Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
                }
                finally
                {
                    Assert.That(AssetDatabase.MoveAsset(moved, source), Is.Empty);
                }
                outside.Dispose();
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
                using var shutdown = system.ShutdownAsync();
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
        }

        [UnityTest]
        public IEnumerator GroupPreloadReallyAcquiresAssetsButOnlyPreparesScenesAndSharesTheirNativeBundle()
        {
            yield return new EnterPlayMode();
            yield return GroupPreloadBody89();
        }

        private static IEnumerator GroupPreloadBody89()
        {
            var snapshot = Snapshot();
            var catalog = snapshot.CreateCatalog();
            catalog.Groups = catalog.Groups.Concat(new[] { new ResourceCatalogGroup("mixed89", new[]
            {
                new ResourceCatalogGroupMember(snapshot.Document.Assets.Single(x => x.Address == "Owned.unity").AssetId, true),
                new ResourceCatalogGroupMember(snapshot.Document.Assets.Single(x => x.Address == "bulk83-0.txt").AssetId, true)
            }) }).ToArray();
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { catalog }));
            var system = initialized.TakeResult();
            var before = Enumerable.Range(0, SceneManager.sceneCount).Select(x => SceneManager.GetSceneAt(x).handle).ToArray();
            using var request = system.PreloadGroupAsync(snapshot.Document.ProjectId, "mixed89");
            Assert.That(system.GetDiagnosticsReport().Bundles.SelectMany(x => x.RetentionReasons).Any(x => x.Kind == "GroupPreload"), Is.True);
            yield return Wait(request);
            using var group = request.TakeResult();
            Assert.That(group.Assets.Count, Is.EqualTo(1));
            Assert.That(((TextAsset)group.Assets[0].Asset).text, Is.EqualTo("batch83-bulk83-0"));
            Assert.That(group.SceneBundles.Count, Is.EqualTo(1));
            Assert.That(group.SceneBundles[0].Bundle.isStreamedSceneAssetBundle, Is.True);
            CollectionAssert.AreEquivalent(before, Enumerable.Range(0, SceneManager.sceneCount).Select(x => SceneManager.GetSceneAt(x).handle));
            Assert.That(system.GetSceneRecords(), Is.Empty, "Scene preparation cannot invent a loaded SceneRecord.");
            var loadCount = system.GetDiagnosticsSnapshot().NativeBundleLoadCount;
            using var duplicate = system.PreloadGroupAsync(snapshot.Document.ProjectId, "mixed89");
            Assert.That(duplicate.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            using var scene = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            yield return Wait(scene);
            using var lease = scene.TakeResult();
            var loaded = system.GetDiagnosticsReport();
            Assert.That(loaded.Scenes.Single().Handle, Is.EqualTo(lease.Record.Handle));
            Assert.That(loaded.Scenes.Single().LoadSequence, Is.EqualTo(scene.LoadSequence));
            Assert.That(loaded.Bundles.SelectMany(x => x.RetentionReasons).Any(x => x.Kind == "SceneRecord" && x.Address == Address(snapshot, "Owned.unity")), Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.EqualTo(loadCount), "A prepared Ready Scene Bundle must be reused, not produced twice.");
            group.Dispose();
            duplicate.Dispose();
            Assert.That(lease.IsValid, Is.True, "The real SceneRecord owns its own graph after preloads are released.");
            using var unloaded = system.UnloadSceneAsync(lease);
            yield return Wait(unloaded);
            yield return Scene85Drained(system);
        }

        [UnityTest]
        public IEnumerator DetailedDiagnosticsExplainActualAssetInstancePublicRootsAndDependencyEdges()
        {
            yield return new EnterPlayMode();
            yield return DiagnosticsBody89();
        }

        private static IEnumerator DiagnosticsBody89()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            using var prefab = system.Acquire<GameObject>(address);
            var nativePrefab = prefab.Asset;
            prefab.OwnerLabel = "diagnostics89-external";
            using var cache = system.GetInstanceCache(prefab, InstanceCachePolicy.Persistent);
            cache.Prewarm(2);
            using var active = cache.Instantiate();
            var descriptor = snapshot.Document.Assets.Single(x => x.Address == "A.prefab");
            using var raw = system.AcquireBundle(snapshot.Document.ProjectId, descriptor.BundleId);
            raw.OwnerLabel = "diagnostics89-native-caller";
            var report = system.GetDiagnosticsReport();
            Assert.That(report.Projects.Single().CatalogDigest, Is.EqualTo(snapshot.Document.CatalogDigest));
            Assert.That(report.Projects.Single().Platform, Is.EqualTo("StandaloneWindows64"));
            Assert.That(report.Projects.Single().BuildId, Is.EqualTo(snapshot.Document.BuildId));
            Assert.That(report.Assets.Single().LeaseCount, Is.EqualTo(2));
            Assert.That(report.Assets.Single().InstancePins, Is.EqualTo(1));
            Assert.That(report.InstanceCaches.Single().ActiveCount, Is.EqualTo(1));
            Assert.That(report.InstanceCaches.Single().IdleCount, Is.EqualTo(1));
            var root = report.Bundles.Single(x => x.BundleId == descriptor.BundleId);
            Assert.That(root.FileBytes, Is.EqualTo(snapshot.Document.Bundles.Single(x => x.LogicalId == descriptor.BundleId).Length));
            Assert.That(root.RetentionReasons.Any(x => x.Kind == "PublicBundleLease" && x.OwnerLabel == "diagnostics89-native-caller"), Is.True);
            Assert.That(root.RetentionReasons.Any(x => x.Kind == "Asset" && x.Address == address && x.Detail.Contains("active=1 idle=1")), Is.True);
            foreach (var bundle in report.Bundles)
            {
                Assert.That(bundle.RetentionReasons.Where(x => x.Kind == "ParentDependency").Sum(x => x.Count), Is.EqualTo(bundle.DependencyPins));
                Assert.That(bundle.RetentionReasons.Where(x => x.Kind != "ParentDependency").Sum(x => x.Count), Is.EqualTo(bundle.RootPins));
            }
            var holders = new AssetLease<GameObject>[300];
            try
            {
                for (var i = 0; i < holders.Length; i++)
                {
                    holders[i] = system.Acquire<GameObject>(address);
                    holders[i].OwnerLabel = "diagnostics89-owner-" + i + "-" + new string('x', 100);
                }
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var sampled = system.GetDiagnosticsReport();
                elapsed.Stop();
                Assert.That(sampled.Assets.Single().Owners.Count, Is.EqualTo(302));
                var detail = sampled.Bundles.SelectMany(x => x.RetentionReasons).Single(x => x.Kind == "Asset").Detail;
                foreach (var holder in holders)
                {
                    Assert.That(detail, Does.Contain(holder.OwnerLabel));
                }

                Debug.Log("YL8.9 diagnostics sample: labeled holders=300, capture=" + elapsed.Elapsed.TotalMilliseconds.ToString("F2") +
                    " ms, retention detail=" + detail.Length + " chars. Current-thread allocation counter returns zero in this Mono runtime and is not treated as a measurement.");
            }
            finally
            {
                foreach (var holder in holders)
                {
                    holder?.Dispose();
                }
            }
            active.Discard();
            Assert.That(system.GetDiagnosticsReport().InstanceCaches.Single().DestroyPendingCount, Is.EqualTo(1));
            cache.Dispose();
            prefab.Dispose();
            raw.Dispose();
            Assert.That(nativePrefab == null, Is.False, "Destroy-pending instances keep the actual Prefab/Bundle graph alive.");
            yield return UntilScene85(() => system.GetDiagnosticsSnapshot().BundleRecordCount == 0);
            Assert.That(nativePrefab == null, Is.True);
            Assert.That(system.GetDiagnosticsReport().Assets, Is.Empty);
            Assert.That(report.InstanceCaches.Single().ActiveCount, Is.EqualTo(1), "Captured diagnostics cannot follow mutable cache/record lifetimes.");
        }
    }
}
