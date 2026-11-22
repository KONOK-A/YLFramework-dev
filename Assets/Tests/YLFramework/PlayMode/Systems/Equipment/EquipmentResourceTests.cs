#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Equipment;
using YLFramework.Resource;
using YLFramework.Tests.Extensions.Domain;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Equipment
{
    [Category("YL.Resource.Addressables.Regression")]
    public class EquipmentResourceTests
    {
        private string _folder;
        private string _folderGuid;
        private string _prefix;
        private bool _ownsManager;
        private AddressablesDomainResourceFixture _fixture;
        private EquipmentsConfig _root;
        private InstanceCache _cache;
        private ResourceSystem Resources => _fixture.Resources;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _ownsManager = false;
            _fixture = null;
            _folder = null;
            _folderGuid = null;
            EquipmentResourceProbe.FailLoad = false;
            EquipmentResourceProbe.LastLoaded = null;
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another resource authority.");
            var configs = (IDictionary)typeof(EquipmentManager).GetField("_configs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.That(configs.Count, Is.Zero, "Do not clear another manager's configurations.");
            _ownsManager = true;
            _fixture = new AddressablesDomainResourceFixture();
            yield return _fixture.Start();
        }

        private IEnumerator Prepare(int components = 1, bool preloadPrefab = true)
        {
            _prefix = "Eq" + Guid.NewGuid().ToString("N");
            _folder = "Assets/__YL" + _prefix;
            Assert.That(AssetDatabase.IsValidFolder(_folder) || Directory.Exists(_folder) || File.Exists(_folder + ".meta"), Is.False);
            _folderGuid = AssetDatabase.CreateFolder("Assets", "__YL" + _prefix);
            Assert.That(_folderGuid, Is.Not.Empty);
            var go = new GameObject("Equipment Resource Probe");
            GameObject prefab;
            try
            {
                for (var i = 0; i < components; i++)
                    go.AddComponent<EquipmentResourceProbe>();
                prefab = PrefabUtility.SaveAsPrefabAsset(go, _folder + "/Equipment.prefab");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            Assert.That(prefab, Is.Not.Null);
            Assert.That(EditorUtility.IsPersistent(prefab), Is.True);
            Assert.That(prefab.scene.IsValid(), Is.False, "The provider must return a real Prefab asset, never a scene object.");
            _fixture.Add("prefab", prefab);
            _root = _fixture.New<EquipmentsConfig>("config");
            _root.name = _prefix;
            var data = _fixture.New<EquipmentConfigData>();
            data.parent = _root;
            data.id = 1001;
            data.equipmentType = EquipmentType.Asset;
            data.projectName = _fixture.Project;
            data.assetName = "prefab";
            _root.Equipments.Add(data);
            var load = _fixture.Track(EquipmentManager.LoadConfigAsync(Resources, _fixture.Address("config")));
            yield return AddressablesDomainResourceFixture.Wait(() => load.IsCompleted, "equipment config load");
            Assert.That(load.IsCompletedSuccessfully, Is.True, load.Exception?.ToString());
            if (preloadPrefab)
            {
                using var request = Resources.AcquireAsync<GameObject>(_fixture.Key("prefab"));
                yield return AddressablesDomainResourceFixture.Wait(() => request.IsDone, "equipment prefab preload");
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
                using var lease = request.TakeResult();
                _cache = Resources.GetOrCreateInstanceCache(lease);
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EquipmentResourceProbe.FailLoad = false;
            if (_ownsManager)
                EquipmentManager.Shutdown();
            if (_fixture != null)
                yield return _fixture.Close();
            yield return null;
            if (!string.IsNullOrEmpty(_folderGuid))
            {
                Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(_folderGuid), "Do not delete a replaced fixture folder.");
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }
            EquipmentResourceProbe.LastLoaded = null;
        }

        [UnityTest]
        public IEnumerator PrefabUsesResourceCacheAndRootLeaseIsIndependent()
        {
            yield return Prepare(preloadPrefab: false);
            Assert.That(Resources.TryAcquireLoaded<EquipmentsConfig>(_fixture.Key("config"), out var lease), Is.True);
            var independent = _fixture.Hold(lease);
            EquipmentManager.LoadConfig(Resources, _fixture.Address("config"));
            var create = _fixture.Track(EquipmentManager.CreateEquipmentAsync(1001));
            yield return AddressablesDomainResourceFixture.Wait(() => create.IsCompleted, "cold equipment creation");
            Assert.That(create.IsCompletedSuccessfully, Is.True, create.Exception?.ToString());
            var first = (EquipmentResourceProbe)create.Result;
            Assert.That(first.EquipmentData, Is.SameAs(_root.Equipments[0]), "Equipment borrows its row from the owned root.");
            Assert.That(Resources.TryAcquireLoaded<GameObject>(_fixture.Key("prefab"), out var prefab), Is.True);
            using (prefab)
                _cache = Resources.GetOrCreateInstanceCache(prefab);
            EquipmentManager.DestroyEquipment(first);
            Assert.That(_cache.CachedCount, Is.EqualTo(1));
            Assert.That(first.EquipmentData, Is.Null);
            var second = (EquipmentResourceProbe)EquipmentManager.CreateEquipment(1001);
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.loads, Is.EqualTo(2));
            EquipmentManager.Shutdown();
            Assert.That(independent.Asset, Is.SameAs(_root));
            Assert.That(second.unloads, Is.EqualTo(2));
            Assert.That(ResourceSystem.Current, Is.SameAs(Resources));
            Assert.That(_cache.CachedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator MissingRootComponentIsDiscarded()
        {
            yield return Prepare(0);
            Assert.Throws<InvalidOperationException>(() => EquipmentManager.CreateEquipment(1001));
            yield return null;
            Assert.That(_cache.CachedCount, Is.Zero);
            Assert.That(Object.FindObjectsOfType<EquipmentResourceProbe>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator AmbiguousRootComponentsAreDiscarded()
        {
            yield return Prepare(2);
            Assert.Throws<InvalidOperationException>(() => EquipmentManager.CreateEquipment(1001));
            yield return null;
            Assert.That(_cache.CachedCount, Is.Zero);
            Assert.That(Object.FindObjectsOfType<EquipmentResourceProbe>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator FailedOnLoadedDoesNotEnterIdleCache()
        {
            yield return Prepare();
            EquipmentResourceProbe.FailLoad = true;
            Assert.Throws<InvalidOperationException>(() => EquipmentManager.CreateEquipment(1001));
            var failed = EquipmentResourceProbe.LastLoaded;
            Assert.That(failed.unloads, Is.EqualTo(1));
            Assert.That(_cache.CachedCount, Is.Zero);
            yield return null;
            Assert.That(failed == null, Is.True);
            EquipmentResourceProbe.FailLoad = false;
            var replacement = EquipmentManager.CreateEquipment(1001);
            Assert.That(ReferenceEquals(replacement, failed), Is.False);
        }

        [UnityTest]
        public IEnumerator ExternallyDestroyedEquipmentIsRemovedFromHost()
        {
            yield return Prepare();
            var host = new Host();
            var equipment = (EquipmentResourceProbe)EquipmentManager.CreateAndEquip(host, 1001);
            Object.Destroy(equipment.gameObject);
            yield return null;
            EquipmentManager.CloseHost(host);
            Assert.That(host.Equipments, Is.Empty);
            Assert.That(EquipmentManager.GetEquipmentInfo(1001), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator RemovingConfigDetachesDestroyedBorrowerUsingRecordedDefinition()
        {
            yield return Prepare();
            Assert.That(Resources.TryAcquireLoaded<EquipmentsConfig>(_fixture.Key("config"), out var lease), Is.True);
            var independent = _fixture.Hold(lease);
            var host = new Host();
            var go = _fixture.OwnAsset(new GameObject("External borrower"));
            var borrowed = go.AddComponent<EquipmentResourceProbe>();
            borrowed.EquipmentData = independent.Asset.Equipments[0];
            EquipmentManager.Equip(host, borrowed);
            Object.Destroy(go);
            yield return null;
            EquipmentManager.RemoveConfig(_root);
            Assert.That(host.Equipments, Is.Empty);
            Assert.That(independent.Asset, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator ResourceShutdownRespectsLiveEquipmentBarrier()
        {
            yield return Prepare();
            var host = new Host();
            var equipment = EquipmentManager.CreateAndEquip(host, 1001);
            using var busy = _fixture.Startup.StopAsync();
            yield return AddressablesDomainResourceFixture.Wait(() => busy.IsDone, "live equipment stop barrier");
            Assert.That(busy.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(equipment.EquipmentData, Is.SameAs(_root.Equipments[0]), "Stopping must not steal a live checkout.");
            EquipmentManager.Shutdown();
            using var stop = _fixture.Startup.StopAsync();
            yield return AddressablesDomainResourceFixture.Wait(() => stop.IsDone, "equipment shutdown retry");
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            Assert.That(host.Equipments, Is.Empty);
            Assert.That(EquipmentManager.GetConfig(_prefix), Is.Null);
        }

        [UnityTest]
        public IEnumerator SessionInvalidationBeforeEquipmentResetStillReleasesRecordedLeases()
        {
            yield return Prepare();
            var host = new Host();
            EquipmentManager.CreateAndEquip(host, 1001);
            var previous = Resources;
            var end = typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(end, Is.Not.Null, "Exercise the real execution-session exit hook.");
            end.Invoke(null, null);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(previous.State, Is.EqualTo(ResourceSystemState.Invalidated));
            EquipmentManager.Shutdown();
            Assert.That(host.Equipments, Is.Empty);
            Assert.That(EquipmentManager.GetConfig(_prefix), Is.Null);

            // A new owned session advances the previous session's native destruction drain.
            _fixture.Startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = _fixture.Startup.StartAsync();
            yield return AddressablesDomainResourceFixture.Wait(() => start.IsDone, "replacement resource session");
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
            _fixture.Resources = start.TakeResult().Resources;
            yield return AddressablesDomainResourceFixture.Wait(() => AddressablesDomainResourceFixture.IsQuiescent(previous), "invalidated equipment responsibility drain");
            AddressablesDomainResourceFixture.AssertQuiescent(previous);
            Assert.That(Resources, Is.Not.SameAs(previous));
        }

        [UnityTest]
        public IEnumerator ClosingHostCancelsOnlyItsOwnPendingCheckout()
        {
            yield return Prepare(preloadPrefab: false);
            _fixture.Provider.Delay = true;
            int before = _fixture.Provider.Provides;
            var firstHost = new Host();
            var secondHost = new Host();
            var first = _fixture.Track(EquipmentManager.CreateAndEquipAsync(firstHost, 1001));
            var second = _fixture.Track(EquipmentManager.CreateAndEquipAsync(secondHost, 1001));
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Provides == before + 1, "shared pending prefab");
            EquipmentManager.CloseHost(firstHost);
            _fixture.Provider.FinishPending();
            yield return AddressablesDomainResourceFixture.Wait(() => first.IsCompleted && second.IsCompleted, "isolated equipment host cancellation");
            Assert.That(first.IsCanceled, Is.True, first.Exception?.ToString());
            Assert.That(firstHost.Equipments, Is.Empty);
            Assert.That(second.IsCompletedSuccessfully, Is.True, second.Exception?.ToString());
            Assert.That(secondHost.Equipments, Has.Count.EqualTo(1));
            Assert.That(second.Result.Equipper, Is.SameAs(secondHost));
            Assert.That(EquipmentManager.GetConfig(_prefix), Is.SameAs(_root));
        }

        private sealed class Host : IEquippable
        {
            public List<IEquipment> Equipments { get; } = new();
            public bool CanEquip(IEquipment equipment) => true;
        }

        public sealed class ReleaseScript : IEquipment
        {
            public IEquippable Equipper { get; set; }
            public EquipmentConfigData EquipmentData { get; set; }
            public void OnLoaded() { }
            public void OnUnloaded() { }
            public void OnEquip() { }
            public void OnUnequip() { }
        }

        [UnityTest]
        public IEnumerator RootRetirementRemainsActiveInsideSourceReleaseCallback()
        {
            var config = _fixture.New<EquipmentsConfig>("retiring");
            config.name = "RetiringRoot";
            var row = _fixture.New<EquipmentConfigData>();
            row.id = 1001;
            row.parent = config;
            row.equipmentType = EquipmentType.Script;
            row.equipmentClassFullName = typeof(ReleaseScript).FullName;
            config.Equipments.Add(row);
            var load = _fixture.Track(EquipmentManager.LoadConfigAsync(Resources, _fixture.Address("retiring")));
            yield return AddressablesDomainResourceFixture.Wait(() => load.IsCompleted, "retiring root load");
            Assert.That(load.IsCompletedSuccessfully, Is.True, load.Exception?.ToString());
            var rejected = false;
            _fixture.Provider.Releasing = (_, asset) =>
            {
                Assert.That(asset, Is.SameAs(config));
                try
                {
                    EquipmentManager.AddConfig(config);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
            };
            EquipmentManager.RemoveConfig(config);
            yield return AddressablesDomainResourceFixture.Wait(() => _fixture.Provider.Releases == 1, "retiring root provider release");
            Assert.That(rejected, Is.True);
            Assert.That(EquipmentManager.GetConfig("RetiringRoot"), Is.Null);
        }
    }
}
#endif
