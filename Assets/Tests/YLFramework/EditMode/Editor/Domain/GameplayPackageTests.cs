using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Equipment;
using YLFramework.Mission;
using YLFramework.Skill;

namespace YLFramework.Tests
{
    [Category("YL.Module.Domain")]
    public class GameplayPackageTests
    {
        [Serializable]
        private sealed class MoveTable
        {
            public Move[] moves;
        }
        [Serializable]
        private sealed class Move
        {
            public string fullName;
            public string currentAssembly;
            public string sourceAssembly;
            public string package;
            public string scriptGuid;
        }
        [Serializable]
        private sealed class Frozen
        {
            public Asset[] assets; public Catalog catalog;
        }
        [Serializable]
        private sealed class Asset
        {
            public string path; public string guid; public long localId; public string typeFullName; public string scriptGuid;
        }
        [Serializable]
        private sealed class Catalog
        {
            public string projectId; public string versionDirectory; public string catalogDigest; public string platform; public string unityCompatibility; public string appContentCompatibilityId;
        }

        [TestCase(typeof(SkillsConfig), "YLFramework.Skill")]
        [TestCase(typeof(BuffsConfig), "YLFramework.Buff")]
        [TestCase(typeof(MissionsConfig), "YLFramework.Mission")]
        [TestCase(typeof(EquipmentsConfig), "YLFramework.Equipment")]
        public void DomainAssetsUseIndependentRuntimeOwnersWithoutMutualReferences(Type type, string owner)
        {
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo(owner));
            string[] domains = { "YLFramework.Skill", "YLFramework.Buff", "YLFramework.Mission", "YLFramework.Equipment" };
            Assert.That(type.Assembly.GetReferencedAssemblies().Where(reference => domains.Contains(reference.Name)), Is.Empty);
        }

        [Test]
        public void AuthoringAndExitHooksShareOneEditorWithoutPublishingInternalHelpers()
        {
            var editor = typeof(YLFramework.Editor.Skill.SkillAuthoring).Assembly;
            Assert.That(editor.GetName().Name, Is.EqualTo("YLFramework.Gameplay.Editor"));
            Assert.That(typeof(YLFramework.Editor.Buff.BuffAuthoring).Assembly, Is.SameAs(editor));
            Assert.That(typeof(YLFramework.Editor.Mission.MissionAuthoring).Assembly, Is.SameAs(editor));
            Assert.That(typeof(YLFramework.Editor.Equipment.EquipmentAuthoring).Assembly, Is.SameAs(editor));
            foreach (var name in new[] { "YLFramework.Editor.Domain.DomainAssetOwnership", "YLFramework.Editor.Domain.DomainResourceIconGui",
                "YLFramework.Editor.Domain.DomainSessionLifecycle", "YLFramework.Editor.Equipment.EquipmentSessionLifecycle" })
            {
                var type = editor.GetType(name);
                Assert.That(type, Is.Not.Null, name);
                Assert.That(type.IsVisible, Is.False, name);
            }
        }

        [TestCase("DomainAssetOwnership", "1ddd9cd019957054cbe3961a7ca6d7b5")]
        [TestCase("DomainResourceIconGui", "95449e78762f9234296438a98b933042")]
        [TestCase("DomainSessionLifecycle", "5005498a231ad034cba239967420cf2e")]
        public void SharedHelpersKeepTheirInternalSingleOwnerAndOriginalGuid(string name, string guid)
        {
            string path = "Packages/com.kano.ylframework.gameplay/Editor/Shared/" + name + ".cs";
            var owner = typeof(YLFramework.Editor.Skill.SkillsConfigController).Assembly;
            var type = owner.GetType("YLFramework.Editor.Domain." + name, true);
            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(path).GetClass(), Is.SameAs(type));
            Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetType(type.FullName) != null), Is.EqualTo(1));
        }

        [Test]
        public void EditorTypesHaveNoRuntimeNamespaceAliases()
        {
            var owner = typeof(YLFramework.Editor.Skill.SkillsConfigController).Assembly;
            Assert.That(owner.GetType("YLFramework.Editor.Skill.SkillsConfigController"), Is.Not.Null);
            Assert.That(owner.GetType("YLFramework.Skill.SkillsConfigController"), Is.Null);
        }

        [Test]
        public void AllFourPublishedMoveAnchorsResolveToTheirOriginalScriptGuidInGameplay()
        {
            var table = JsonUtility.FromJson<MoveTable>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(
                "Assets/Tests/YLFramework/Fixtures/NamespaceMigration/AssemblyMoves.json")));
            var moves = table.moves.Where(move => move.package == "com.kano.ylframework.gameplay").ToArray();
            Assert.That(table.moves, Has.Length.EqualTo(26));
            Assert.That(moves, Has.Length.EqualTo(4));
            foreach (var move in moves)
            {
                Assert.That(move.sourceAssembly, Is.EqualTo("YLFramework.Runtime"));
                var owner = System.Reflection.Assembly.Load(move.currentAssembly);
                Assert.That(owner.GetType(move.fullName)?.IsPublic, Is.True, move.fullName);
                string script = AssetDatabase.GUIDToAssetPath(move.scriptGuid);
                Assert.That(script, Does.StartWith("Packages/com.kano.ylframework.gameplay/Runtime/"));
                string definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(script);
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(definition)), Does.Contain('"' + move.currentAssembly + '"'));
                Assert.That(typeof(YLFramework.YLFrameworkRoot).Assembly.GetType(move.fullName), Is.Null);
            }
        }

        [Test]
        public void FrozenNineDomainAssetsReopenWithOriginalGuidLocalIdAndScriptWhileOldCatalogIsRejected()
        {
            const string root = "Assets/Tests/YLFramework/Fixtures/NamespaceMigration/AssemblySplitV1/";
            var frozen = JsonUtility.FromJson<Frozen>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(root + "FixtureIdentity.json")));
            var expected = frozen.assets.Where(asset => asset.typeFullName.StartsWith("YLFramework.Skill.", StringComparison.Ordinal) ||
                asset.typeFullName.StartsWith("YLFramework.Buff.", StringComparison.Ordinal) || asset.typeFullName.StartsWith("YLFramework.Mission.", StringComparison.Ordinal) ||
                asset.typeFullName.StartsWith("YLFramework.Equipment.", StringComparison.Ordinal)).ToArray();
            Assert.That(expected, Has.Length.EqualTo(9));
            var types = expected.Select(asset =>
            {
                var loaded = AssetDatabase.LoadAssetAtPath<ScriptableObject>(root + asset.path);
                Assert.That((bool)loaded, Is.True, asset.path);
                Assert.That(loaded.GetType().FullName, Is.EqualTo(asset.typeFullName));
                Assert.That(loaded.GetType().Assembly.GetName().Name, Is.EqualTo(string.Join(".", asset.typeFullName.Split('.').Take(2))));
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(loaded, out string guid, out long localId), Is.True);
                Assert.That(guid, Is.EqualTo(asset.guid));
                Assert.That(localId, Is.EqualTo(asset.localId));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(loaded))), Is.EqualTo(asset.scriptGuid));
                return loaded.GetType();
            }).Distinct().ToArray();
            string directory = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(root + frozen.catalog.versionDirectory));
            var catalog = YLFramework.Resource.ResourceCatalogCodec.Read(File.ReadAllBytes(Path.Combine(directory, "resource-catalog.json")),
                frozen.catalog.projectId, frozen.catalog.platform, frozen.catalog.unityCompatibility, frozen.catalog.appContentCompatibilityId);
            Assert.That(catalog.CatalogDigest, Is.EqualTo(frozen.catalog.catalogDigest));
            Assert.That(catalog.Assets.Any(asset => asset.AssetType == "YLFramework.Skill.SkillConfigData" && asset.AssetAssembly == "YLFramework.Runtime"), Is.True);
            if (Application.unityVersion == frozen.catalog.unityCompatibility)
            {
                var snapshot = YLFramework.Resource.ResourceLocalSnapshot.Open(directory, catalog.ProjectId, catalog.CatalogDigest, catalog.Platform, catalog.AppContentCompatibilityId);
                var error = Assert.Throws<YLFramework.Resource.ResourceException>(() => snapshot.CreateCatalog(types));
                Assert.That(error.Kind, Is.EqualTo(YLFramework.Resource.ResourceError.TypeMismatch));
                Assert.That(error.Message, Does.Contain("YLFramework.Skill.SkillConfigData"));
            }
        }
    }
}
