using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceMultiDirectoryIntegrationTests
    {
        private string _root;
        private ResourceProject _project;
        private bool _hasCreatedModern;

        [SetUp]
        public void SetUp()
        {
            Assert.That(AssetDatabase.IsValidFolder(ResourceMultiDirectoryEvidence.FixtureRoot), Is.True, "仅使用本批已登记夹具父目录。 ");
            if (!AssetDatabase.IsValidFolder(ResourceMultiDirectoryEvidence.Root))
            {
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceMultiDirectoryEvidence.Root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceMultiDirectoryEvidence.Root + ".meta")), Is.False);
                Assert.That(AssetDatabase.CreateFolder(ResourceMultiDirectoryEvidence.FixtureRoot, "Modern"), Is.Not.Empty);
                _hasCreatedModern = true;
            }
            _root = ResourceMultiDirectoryEvidence.Root + "/Integration-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder(ResourceMultiDirectoryEvidence.Root, Path.GetFileName(_root)), Is.Not.Empty);
            foreach (var name in new[] { "Textures", "Materials", "Text" })
            {
                Assert.That(AssetDatabase.CreateFolder(_root, name), Is.Not.Empty);
            }

            _project = ResourceEditorOperations.CreateProject(_root + "/Project.asset");
            _project.projectId = "multi-integration-" + Guid.NewGuid().ToString("N");
        }

        [TearDown]
        public void TearDown()
        {
            if (_project)
            {
                Undo.ClearUndo(_project);
            }

            if (!string.IsNullOrEmpty(_root) && AssetDatabase.IsValidFolder(_root))
            {
                Assert.That(AssetDatabase.DeleteAsset(_root), Is.True);
            }

            if (_hasCreatedModern && AssetDatabase.IsValidFolder(ResourceMultiDirectoryEvidence.Root) &&
                !Directory.EnumerateFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(ResourceMultiDirectoryEvidence.Root)).Any())
            {
                Assert.That(AssetDatabase.DeleteAsset(ResourceMultiDirectoryEvidence.Root), Is.True);
            }

            _hasCreatedModern = false;
        }

        [Test]
        public void CrossDirectoryDependencyIsAssignedOnceAcrossGroupsWithoutCollectingUnselectedText()
        {
            SharedGraph();
            var map = ResourceBuildMap.Create(_project);
            CollectionAssert.AreEquivalent(new[] { "materials/A.mat", "materials/B.mat", "text/selected.txt" }, map.Assets.Select(x => x.Address.Path));
            Assert.That(map.Groups["main"].Count, Is.EqualTo(2), "同 Group 的材质与文本来自不同源目录。 ");
            var shared = map.Bundles.Single(x => x.IsShared);
            CollectionAssert.AreEqual(new[] { _root + "/Textures/Shared.asset" }, shared.AssetPaths);
            var paths = map.Bundles.SelectMany(x => x.AssetPaths).ToArray();
            Assert.That(paths.Count(x => x == _root + "/Textures/Shared.asset"), Is.EqualTo(1));
            Assert.That(paths, Does.Not.Contain(_root + "/Text/unselected.txt"));
            var a = map.Assets.Single(x => x.Address.Path == "materials/A.mat");
            var b = map.Assets.Single(x => x.Address.Path == "materials/B.mat");
            Assert.That(map.Bundles.Single(x => x.Name == a.BundleName).Dependencies, Does.Contain(shared.Name));
            Assert.That(map.Bundles.Single(x => x.Name == b.BundleName).Dependencies, Does.Contain(shared.Name));
        }

        [Test]
        public void RemovingADependencyOnlyDirectoryRejectsTheGraphWithoutDeletingSelectionsOrSourceBytes()
        {
            SharedGraph();
            var before = ResourceReleaseFixture.Snapshot(_root);
            ResourceMultiDirectoryEvidence.SetSources(_project, (_root + "/Materials", "materials"), (_root + "/Text", "text"));
            var error = Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(_project));
            Assert.That(error.Message, Does.Contain(_root + "/Textures/Shared.asset"));
            Assert.That(_project.groups.Single(x => x.id == "main").selections.Count, Is.EqualTo(2));
            Assert.That(_project.groups.Single(x => x.id == "other").selections.Count, Is.EqualTo(1));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(_root), "A dependency-range failure must not save a definition or rewrite source files/meta.");
        }

        [Test]
        public void SimulationResolvesTheSameCanonicalIdentityAndAliasAfterDirectoryReordering()
        {
            SharedGraph();
            var map = ResourceBuildMap.Create(_project);
            var simulation = new ResourceEditorSimulation(new[] { _project });
            var expected = map.Assets.Single(x => x.Address.Path == "text/selected.txt");
            var canonical = new ResourceAddress(_project.projectId, "text/selected.txt");
            var alias = new ResourceAddress(_project.projectId, "message");
            Assert.That(simulation.Resolve(canonical).AssetId, Is.EqualTo(expected.AssetId));
            Assert.That(simulation.Resolve(alias).AssetId, Is.EqualTo(expected.AssetId));
            Assert.That(simulation.Load<TextAsset>(alias), Is.SameAs(AssetDatabase.LoadAssetAtPath<TextAsset>(_root + "/Text/selected.txt")));
            Assert.That(simulation.Load<Material>(new ResourceAddress(_project.projectId, "materials/A.mat")),
                Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(_root + "/Materials/A.mat")));
            ResourceMultiDirectoryEvidence.SetSources(_project, (_root + "/Text", "text"), (_root + "/Materials", "materials"), (_root + "/Textures", "textures"));
            Assert.That(simulation.Resolve(alias).AssetId, Is.EqualTo(expected.AssetId));
            CollectionAssert.AreEquivalent(map.Groups["main"], simulation.GetGroup(_project.projectId, "main").Select(x => x.AssetId));
            Assert.Throws<ArgumentException>(() => simulation.Resolve(new ResourceAddress(_project.projectId, "text/unselected.txt")));
            Assert.That(ResourceSystem.Current, Is.Null, "Simulation 不应初始化 Runtime。 ");
        }

        [Test]
        public void SetSourceDirectoriesSupportsUndoAndOnlyExplicitSavePersistsTheNewPrefix()
        {
            var path = Text("selected.txt");
            _project.groups.Add(new ResourceGroup { id = "main", selections = new List<ResourceSelection> { Select(path) } });
            ResourceMultiDirectoryEvidence.SetSources(_project, (_root + "/Text", "text"));
            EditorUtility.SetDirty(_project);
            ResourceEditorOperations.SaveProject(_project);
            var projectPath = AssetDatabase.GetAssetPath(_project);
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath));
            ResourceMultiDirectoryEvidence.SetSources(_project, (_root + "/Text", "renamed"));
            Undo.FlushUndoRecordObjects();
            Assert.That(EditorUtility.IsDirty(_project), Is.True);
            Assert.That(ResourceBuildMap.Create(_project).Assets.Single().Address.Path, Is.EqualTo("renamed/selected.txt"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath)), "编辑不能悄悄保存。 ");
            Undo.PerformUndo();
            Assert.That(ResourceBuildMap.Create(_project).Assets.Single().Address.Path, Is.EqualTo("text/selected.txt"));
            Undo.PerformRedo();
            Assert.That(ResourceBuildMap.Create(_project).Assets.Single().Address.Path, Is.EqualTo("renamed/selected.txt"));
            ResourceEditorOperations.SaveProject(_project);
            Undo.ClearUndo(_project);
            Resources.UnloadAsset(_project);
            _project = AssetDatabase.LoadAssetAtPath<ResourceProject>(projectPath);
            Assert.That(ResourceBuildMap.Create(_project).Assets.Single().Address.Path, Is.EqualTo("renamed/selected.txt"));
            Assert.That(EditorUtility.IsDirty(_project), Is.False);
        }

        [Test]
        public void CurrentDirectoryModelSaveAndReloadKeepCanonicalAddressAliasAndSourceIdentity()
        {
            var path = Text("selected.txt");
            _project.groups.Add(new ResourceGroup { id = "main", selections = new List<ResourceSelection> { Select(path, "message") } });
            ResourceMultiDirectoryEvidence.SetSources(_project, (_root + "/Materials", "materials"), (_root + "/Text", "messages"));
            EditorUtility.SetDirty(_project);
            ResourceEditorOperations.SaveProject(_project);
            var projectPath = AssetDatabase.GetAssetPath(_project);
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath));
            var projectMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath + ".meta"));
            var source = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var sourceMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
            var first = ResourceBuildMap.Create(_project).Assets.Single();
            Assert.That(first.Address.Path, Is.EqualTo("messages/selected.txt"));
            Assert.That(first.Aliases.Single().Path, Is.EqualTo("message"));
            Assert.That(EditorUtility.IsDirty(_project), Is.False);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath)), "只读解析不能改写当前目录列表。 ");
            _project.displayName = "当前目录模型的显式保存";
            EditorUtility.SetDirty(_project);
            ResourceEditorOperations.SaveProject(_project);
            Undo.ClearUndo(_project);
            Resources.UnloadAsset(_project);
            _project = AssetDatabase.LoadAssetAtPath<ResourceProject>(projectPath);
            var saved = ResourceBuildMap.Create(_project).Assets.Single();
            Assert.That(saved.Address.Path, Is.EqualTo("messages/selected.txt"));
            Assert.That(saved.Aliases.Single().Path, Is.EqualTo("message"));
            Assert.That(saved.AssetId, Is.EqualTo(first.AssetId));
            Assert.That(_project.displayName, Is.EqualTo("当前目录模型的显式保存"));
            CollectionAssert.AreEqual(new[] { AssetDatabase.AssetPathToGUID(_root + "/Materials"), AssetDatabase.AssetPathToGUID(_root + "/Text") },
                _project.sourceDirectories.Select(x => x.guid));
            CollectionAssert.AreEqual(new[] { "materials", "messages" }, _project.sourceDirectories.Select(x => x.addressPrefix));
            CollectionAssert.AreEqual(projectMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(projectPath + ".meta")));
            CollectionAssert.AreEqual(source, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            CollectionAssert.AreEqual(sourceMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            Assert.That(EditorUtility.IsDirty(_project), Is.False);
        }

        private void SharedGraph()
        {
            var texture = new Texture2D(2, 2) { name = "Shared" };
            AssetDatabase.CreateAsset(texture, _root + "/Textures/Shared.asset");
            var shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            foreach (var name in new[] { "A", "B" })
            {
                AssetDatabase.CreateAsset(new Material(shader) { name = name, mainTexture = texture }, _root + "/Materials/" + name + ".mat");
            }

            var text = Text("selected.txt");
            Text("unselected.txt");
            _project.groups.Add(new ResourceGroup { id = "main", selections = new List<ResourceSelection> { Select(_root + "/Materials/A.mat"), Select(text, "message") } });
            _project.groups.Add(new ResourceGroup { id = "other", selections = new List<ResourceSelection> { Select(_root + "/Materials/B.mat") } });
            ResourceMultiDirectoryEvidence.SetSources(_project, (_root + "/Textures", "textures"), (_root + "/Materials", "materials"), (_root + "/Text", "text"));
            EditorUtility.SetDirty(_project);
            ResourceEditorOperations.SaveProject(_project);
        }

        private string Text(string name)
        {
            var path = _root + "/Text/" + name;
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "自有多目录测试文本");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static ResourceSelection Select(string path, string alias = null) => new()
        {
            guid = AssetDatabase.AssetPathToGUID(path),
            aliases = alias == null ? Array.Empty<string>() : new[] { alias }
        };
    }
}
