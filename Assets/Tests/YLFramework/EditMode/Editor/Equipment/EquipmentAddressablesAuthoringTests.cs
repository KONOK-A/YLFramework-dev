using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Equipment.Editor
{
    [Category("YL.Resource.Addressables.Consumers")]
    public sealed class EquipmentAddressablesAuthoringTests
    {
        private string _folder;
        private AddressableAssetSettings _settings;

        [SetUp]
        public void SetUp()
        {
            const string parent = "Assets";
            Assert.That(AssetDatabase.IsValidFolder(parent), Is.True);
            string name = "EquipmentAddressables-" + Guid.NewGuid().ToString("N");
            _folder = parent + "/" + name;
            AssetDatabase.CreateFolder(parent, name);
            _settings = AddressableAssetSettings.Create(_folder, "Settings", true, true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_folder != null)
                AssetDatabase.DeleteAsset(_folder);
        }

        [Test]
        public void AddressSelectionUsesOfficialNamespaceAndAssetTypeWithoutLegacyProjects()
        {
            AddPrefab("hero", "gameplay/weapons/hero");
            AddPrefab("other", "other/weapons/other");
            var text = new TextAsset("not a prefab");
            string path = _folder + "/text.asset";
            AssetDatabase.CreateAsset(text, path);
            _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), _settings.DefaultGroup).address = "gameplay/text";

            var addresses = GetAddresses("gameplay");
            Assert.That(addresses, Is.EqualTo(new[] { "weapons/hero" }));
        }

        [Test]
        public void AmbiguousOfficialAddressIsRejectedInsteadOfSelectingTheFirstEntry()
        {
            AddPrefab("first", "gameplay/duplicate");
            AddPrefab("second", "gameplay/duplicate");
            var error = Assert.Throws<TargetInvocationException>(() => GetAddresses("gameplay"));
            Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>());
        }

        private void AddPrefab(string name, string address)
        {
            var source = new GameObject(name);
            string path = _folder + "/" + name + ".prefab";
            try
            {
                PrefabUtility.SaveAsPrefabAsset(source, path);
            }
            finally { Object.DestroyImmediate(source); }
            _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), _settings.DefaultGroup).address = address;
        }

        private string[] GetAddresses(string project)
        {
            var authoring = Type.GetType("YLFramework.Editor.Equipment.EquipmentAuthoring, YLFramework.Gameplay.Editor", true);
            var method = authoring.GetMethod("GetAddresses", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (string[])method.Invoke(null, new object[] { _settings, project, typeof(GameObject) });
        }
    }
}
