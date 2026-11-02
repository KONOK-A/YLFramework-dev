using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Equipment;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Extensions.Equipment
{
    public class EquipmentTwoPlayTests
    {
        private SceneSetup[] _scenes;
        private string _folder;
        private bool _wasReloadEnabled;
        private EnterPlayModeOptions _reloadOptions;

        [SetUp]
        public void Prepare()
        {
            _scenes = EditorSceneManager.GetSceneManagerSetup();
            _wasReloadEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _reloadOptions = EditorSettings.enterPlayModeOptions;
            var parent = "Assets/Tests/YLFramework/Fixtures/Extensions";
            var name = "EqTwoPlay-" + Guid.NewGuid().ToString("N");
            _folder = parent + "/" + name;
            AssetDatabase.CreateFolder(parent, name);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, _folder + "/Empty.unity");
        }

        [UnityTearDown]
        public IEnumerator Restore()
        {
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            EditorSettings.enterPlayModeOptions = _reloadOptions;
            EditorSettings.enterPlayModeOptionsEnabled = _wasReloadEnabled;
            if (_scenes != null)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_scenes);
            }

            if (_folder != null)
            {
                AssetDatabase.DeleteAsset(_folder);
            }
        }

        private sealed class Source : IResourceSource
        {
            public ResourceSourceId Id => new("eq-two-play");
            public bool SupportsSynchronousLoad => true;
            internal EquipmentsConfig Root;
            internal int Releases;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new(Root);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => throw new NotSupportedException();
            public void Release(Object asset) => Releases++;
        }

        [UnityTest]
        public IEnumerator BothRealPlaySessionsReturnAttachedDetachedAndRootOwnership()
        {
            try
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = _reloadOptions | EnterPlayModeOptions.DisableDomainReload;
                for (var round = 0; round < 2; round++)
                {
                    yield return new EnterPlayMode(false);
                    Assert.That(EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload, Is.Not.Zero);
                    Assert.That(EquipmentManager.GetConfig("Equipment.TwoPlay"), Is.Null);
                    Assert.That(ResourceSystem.Current, Is.Null);
                    var root = ScriptableObject.CreateInstance<EquipmentsConfig>();
                    root.name = "Equipment.TwoPlay";
                    var row = ScriptableObject.CreateInstance<EquipmentConfigData>();
                    row.id = 1001;
                    row.parent = root;
                    row.equipmentType = EquipmentType.Script;
                    row.equipmentClassFullName = typeof(EquipmentLifecycleTests.Probe).FullName;
                    root.Equipments.Add(row);
                    var source = new Source { Root = root };
                    var address = new ResourceAddress("eq-two-play", "root");
                    using (var init = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("eq-two-play", new[] {
                        new ResourceCatalogEntry("root", address, new ResourceLocation(source.Id, "root", typeof(EquipmentsConfig))) }) }, new IResourceSource[] { source })))
                    {
                        var system = init.TakeResult();
                        EquipmentManager.LoadConfig(system, address);
                    }
                    var host = new EquipmentLifecycleTests.Host();
                    var attached = (EquipmentLifecycleTests.Probe)EquipmentManager.CreateAndEquip(host, 1001);
                    var detached = (EquipmentLifecycleTests.Probe)EquipmentManager.CreateEquipment(1001);
                    var borrowed = new EquipmentLifecycleTests.Probe { EquipmentData = row };
                    EquipmentManager.Equip(host, borrowed);
                    yield return new ExitPlayMode();
                    Assert.That(host.Equipments, Is.Empty, "round " + round);
                    Assert.That(attached.Unloads, Is.EqualTo(1));
                    Assert.That(detached.Unloads, Is.EqualTo(1));
                    Assert.That(borrowed.Unloads, Is.Zero);
                    Assert.That(borrowed.UnEquips, Is.EqualTo(1));
                    Assert.That(EquipmentManager.GetConfig("Equipment.TwoPlay"), Is.Null);
                    Assert.That(ResourceSystem.Current, Is.Null);
                    Assert.That(source.Releases, Is.EqualTo(1));
                    if (row)
                    {
                        Object.DestroyImmediate(row);
                    }

                    if (root)
                    {
                        Object.DestroyImmediate(root);
                    }
                }
            }
            finally
            {
                EditorSettings.enterPlayModeOptions = _reloadOptions;
                EditorSettings.enterPlayModeOptionsEnabled = _wasReloadEnabled;
            }
        }
    }
}
