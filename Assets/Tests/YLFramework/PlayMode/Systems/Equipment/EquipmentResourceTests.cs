#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Equipment;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Equipment
{
    public class EquipmentResourceTests
    {
        private string _folder;
        private string _prefix;
        private ResourceSystem _resources;
        private EquipmentsConfig _root;
        private readonly ResourceAddress _configAddress = new("eq-fixture", "config");
        private readonly ResourceAddress _prefabAddress = new("eq-fixture", "prefab");

        private void Prepare(int components = 1)
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another Resource authority.");
            EquipmentManager.Shutdown();
            _prefix = "Eq" + Guid.NewGuid().ToString("N");
            var parent = "Assets/Tests/YLFramework/Fixtures/Extensions";
            _folder = parent + "/" + _prefix;
            AssetDatabase.CreateFolder(parent, _prefix);
            AssetDatabase.CreateFolder(_folder, "Resources");
            var go = new GameObject("Equipment Resource Probe");
            try
            {
                for (var i = 0; i < components; i++)
                {
                    go.AddComponent<EquipmentResourceProbe>();
                }

                PrefabUtility.SaveAsPrefabAsset(go, _folder + "/Resources/" + _prefix + "Prefab.prefab");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            _root = ScriptableObject.CreateInstance<EquipmentsConfig>();
            _root.name = _prefix;
            var data = ScriptableObject.CreateInstance<EquipmentConfigData>();
            data.parent = _root;
            data.id = 1001;
            data.equipmentType = EquipmentType.Asset;
            data.projectName = "eq-fixture";
            data.assetName = "prefab";
            _root.Equipments.Add(data);
            AssetDatabase.CreateAsset(_root, _folder + "/Resources/" + _prefix + "Config.asset");
            AssetDatabase.AddObjectToAsset(data, _root);
            EditorUtility.SetDirty(_root);
            AssetDatabase.SaveAssetIfDirty(_root);
            var entries = new[]
            {
                new ResourceCatalogEntry("config", _configAddress, new ResourceLocation(ResourceSourceId.Resources, _prefix + "Config", typeof(EquipmentsConfig))),
                new ResourceCatalogEntry("prefab", _prefabAddress, new ResourceLocation(ResourceSourceId.Resources, _prefix + "Prefab", typeof(GameObject)))
            };
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("eq-fixture", entries) }));
            _resources = initialized.TakeResult();
            EquipmentManager.LoadConfig(_resources, _configAddress);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EquipmentResourceProbe.FailLoad = false;
            EquipmentManager.Shutdown();
            if (_resources != null && ReferenceEquals(ResourceSystem.Current, _resources))
            {
                using var shutdown = _resources.ShutdownAsync();
                while (!shutdown.IsDone)
                {
                    yield return null;
                }

                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
            yield return null;
            if (_folder != null)
            {
                AssetDatabase.DeleteAsset(_folder);
            }

            EquipmentResourceProbe.LastLoaded = null;
        }

        [Test]
        public void PrefabUsesResourceCacheAndRootLeaseIsIndependent()
        {
            Prepare();
            using var independent = _resources.Acquire<EquipmentsConfig>(_configAddress);
            EquipmentManager.LoadConfig(_resources, _configAddress);
            var first = (EquipmentResourceProbe)EquipmentManager.CreateEquipment(1001);
            EquipmentManager.DestroyEquipment(first);
            var cache = _resources.GetInstanceCache(_prefabAddress);
            Assert.That(cache.CachedCount, Is.EqualTo(1));
            Assert.That(first.EquipmentData, Is.Null);
            var second = (EquipmentResourceProbe)EquipmentManager.CreateEquipment(1001);
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.loads, Is.EqualTo(2));
            EquipmentManager.Shutdown();
            Assert.That(independent.Asset, Is.SameAs(_root));
            Assert.That(second.unloads, Is.EqualTo(2));
            Assert.That(ResourceSystem.Current, Is.SameAs(_resources));
            Assert.That(cache.CachedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator MissingRootComponentIsDiscarded()
        {
            Prepare(0);
            Assert.Throws<InvalidOperationException>(() => EquipmentManager.CreateEquipment(1001));
            yield return null;
            Assert.That(_resources.GetInstanceCache(_prefabAddress).CachedCount, Is.Zero);
            Assert.That(Object.FindObjectsOfType<EquipmentResourceProbe>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator AmbiguousRootComponentsAreDiscarded()
        {
            Prepare(2);
            Assert.Throws<InvalidOperationException>(() => EquipmentManager.CreateEquipment(1001));
            yield return null;
            Assert.That(_resources.GetInstanceCache(_prefabAddress).CachedCount, Is.Zero);
            Assert.That(Object.FindObjectsOfType<EquipmentResourceProbe>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator FailedOnLoadedDoesNotEnterIdleCache()
        {
            Prepare();
            EquipmentResourceProbe.FailLoad = true;
            Assert.Throws<InvalidOperationException>(() => EquipmentManager.CreateEquipment(1001));
            var failed = EquipmentResourceProbe.LastLoaded;
            Assert.That(failed.unloads, Is.EqualTo(1));
            yield return null;
            Assert.That(failed == null, Is.True);
            EquipmentResourceProbe.FailLoad = false;
            var replacement = EquipmentManager.CreateEquipment(1001);
            Assert.That(ReferenceEquals(replacement, failed), Is.False);
        }

        [UnityTest]
        public IEnumerator ExternallyDestroyedEquipmentIsRemovedFromHost()
        {
            Prepare();
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
            Prepare();
            using var independent = _resources.Acquire<EquipmentsConfig>(_configAddress);
            var host = new Host();
            var go = new GameObject("External borrower");
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
            Prepare();
            var host = new Host();
            EquipmentManager.CreateAndEquip(host, 1001);
            using var busy = _resources.ShutdownAsync();
            Assert.That(busy.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            EquipmentManager.Shutdown();
            using var shutdown = _resources.ShutdownAsync();
            while (!shutdown.IsDone)
            {
                yield return null;
            }

            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(host.Equipments, Is.Empty);
            Assert.That(EquipmentManager.GetConfig(_prefix), Is.Null);
        }

        [UnityTest]
        public IEnumerator SessionInvalidationBeforeEquipmentResetStillReleasesRecordedLeases()
        {
            Prepare();
            var host = new Host();
            EquipmentManager.CreateAndEquip(host, 1001);
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                typeof(ResourceSystem).GetMethod("EndSession", flags).Invoke(null, null);
                Assert.That(ResourceSystem.Current, Is.Null);
                yield return null;
                EquipmentManager.Shutdown();
                Assert.That(host.Equipments, Is.Empty);
                Assert.That(EquipmentManager.GetConfig(_prefix), Is.Null);
            }
            finally
            {
                // 本用例只终结自己创建的 authority，恢复其 admission 门以隔离后续测试。
                typeof(ResourceSystem).GetMethod("ResetSession", flags).Invoke(null, null);
            }
        }

        private sealed class Host : IEquippable
        {
            public List<IEquipment> Equipments { get; } = new();
            public bool CanEquip(IEquipment equipment) => true;
        }

        private sealed class ReleasingSource : IResourceSource
        {
            public ResourceSourceId Id => new("equipment-release-fixture");
            public bool SupportsSynchronousLoad => true;
            internal EquipmentsConfig Root;
            internal Action Releasing;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new(Root);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => throw new NotSupportedException();
            public void Release(Object asset) => Releasing?.Invoke();
        }
        public sealed class ReleaseScript : IEquipment
        {
            public IEquippable Equipper
            {
                get; set;
            }
            public EquipmentConfigData EquipmentData
            {
                get; set;
            }
            public void OnLoaded()
            {
            }
            public void OnUnloaded()
            {
            }
            public void OnEquip()
            {
            }
            public void OnUnequip()
            {
            }
        }
        [Test]
        public void RootRetirementRemainsActiveInsideSourceReleaseCallback()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            EquipmentManager.Shutdown();
            var config = ScriptableObject.CreateInstance<EquipmentsConfig>();
            config.name = "RetiringRoot";
            var row = ScriptableObject.CreateInstance<EquipmentConfigData>();
            row.id = 1001;
            row.parent = config;
            row.equipmentType = EquipmentType.Script;
            row.equipmentClassFullName = typeof(ReleaseScript).FullName;
            config.Equipments.Add(row);
            var source = new ReleasingSource { Root = config };
            var address = new ResourceAddress("equipment-release", "root");
            try
            {
                using var init = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("equipment-release", new[] {
                    new ResourceCatalogEntry("root", address, new ResourceLocation(source.Id, "root", typeof(EquipmentsConfig))) }) }, new IResourceSource[] { source }));
                _resources = init.TakeResult();
                EquipmentManager.LoadConfig(_resources, address);
                var rejected = false;
                source.Releasing = () =>
                {
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
                Assert.That(rejected, Is.True);
                Assert.That(EquipmentManager.GetConfig("RetiringRoot"), Is.Null);
            }
            finally
            {
                EquipmentManager.Shutdown();
                Object.DestroyImmediate(row);
                Object.DestroyImmediate(config);
            }
        }
    }
}
#endif
