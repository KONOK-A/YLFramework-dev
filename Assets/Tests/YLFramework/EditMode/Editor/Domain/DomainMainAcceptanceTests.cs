using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Mission;
using YLFramework.Skill;
using YLFramework.Editor.Buff;
using YLFramework.Editor.Mission;
using YLFramework.Editor.Skill;

namespace YLFramework.Tests.Extensions.Domain
{
    public sealed class DomainAuthoringProtectionTests
    {
        private string _folder;
        private string[] _absentParents = Array.Empty<string>();

        [SetUp]
        public void SetUp()
        {
            _absentParents = DomainTestAssets.CaptureAbsentParents();
            _folder = "Assets/DomainFixture-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder)), Is.Not.Empty);
        }

        [TearDown] public void TearDown() => DomainTestAssets.DeleteFolder(_folder, _absentParents);

        [TestCase("Skill")]
        [TestCase("Buff")]
        [TestCase("Mission")]
        public void CurrentAuthoringSavesOnlyItsRootAndKeepsUnrelatedDirtyAssetsAndUnknownFiles(string module)
        {
            string materialPath = _folder + "/Unrelated.mat";
            var material = new Material(Shader.Find("Sprites/Default")) { color = Color.blue };
            AssetDatabase.CreateAsset(material, materialPath);
            byte[] materialBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(materialPath));
            byte[] materialMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(materialPath + ".meta"));
            string unknownPath = _folder + "/Keep.txt";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(unknownPath), "preserve unknown author content");
            AssetDatabase.ImportAsset(unknownPath, ImportAssetOptions.ForceSynchronousImport);
            byte[] unknownBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(unknownPath));
            byte[] unknownMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(unknownPath + ".meta"));
            var rootType = module == "Skill" ? typeof(SkillsConfig) : module == "Buff" ? typeof(BuffsConfig) : typeof(MissionsConfig);
            var root = ScriptableObject.CreateInstance(rootType);
            string rootPath = _folder + "/Root.asset";
            AssetDatabase.CreateAsset(root, rootPath);
            material.color = Color.red;
            EditorUtility.SetDirty(material);
            if (root is SkillsConfig skill)
            {
                SkillsConfigController.CreateSkillConfigData(typeof(SkillConfigData), skill);
            }
            else if (root is BuffsConfig buff)
            {
                BuffsConfigController.CreateBuffConfigData(typeof(BuffConfigData), buff);
            }
            else
            {
                MissionsConfigController.CreateMissionConfigData(typeof(MissionConfigData), (MissionsConfig)root);
            }
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
            AssetDatabase.ImportAsset(rootPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            using var serialized = new SerializedObject(AssetDatabase.LoadAssetAtPath(rootPath, rootType));
            var rows = serialized.FindProperty(module == "Skill" ? "skills" : module == "Buff" ? "buffs" : "missions");
            Assert.That(rows.arraySize, Is.EqualTo(1));
            Assert.That(rows.GetArrayElementAtIndex(0).objectReferenceValue, Is.Not.Null);
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(materialPath)), Is.EqualTo(materialBytes));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(materialPath + ".meta")), Is.EqualTo(materialMeta));
            Assert.That(EditorUtility.IsDirty(material), Is.True);
            Assert.That(material.color, Is.EqualTo(Color.red));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(unknownPath)), Is.EqualTo(unknownBytes));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(unknownPath + ".meta")), Is.EqualTo(unknownMeta));
        }
    }
}
