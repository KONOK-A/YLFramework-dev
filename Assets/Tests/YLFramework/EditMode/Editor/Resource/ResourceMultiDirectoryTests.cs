using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceMultiDirectoryTests
    {
        private string _root;
        private ResourceProject _project;

        [SetUp]
        public void SetUp()
        {
            _root = "Assets/Tests/YLFramework/Fixtures/MultiDirectory-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", Path.GetFileName(_root));
            foreach (var name in new[] { "First", "Second", "Unused" })
            {
                AssetDatabase.CreateFolder(_root, name);
            }

            _project = ScriptableObject.CreateInstance<ResourceProject>();
            _project.projectId = "multi-" + Guid.NewGuid().ToString("N");
            _project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_root + "/First") });
            _project.groups.Add(new ResourceGroup { id = "main" });
            AssetDatabase.CreateAsset(_project, _root + "/Project.asset");
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(_root);

        [Test]
        public void CurrentModelsExposeOnlyTheExplicitDirectoryAndAtlasOwnershipContract()
        {
            const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var name in new[] { "AssetRootGuid", "DirectoryConfigurationVersion", "legacyAssetRootGuid", "DirectoryDefinitions" })
            {
                Assert.That(typeof(ResourceProject).GetMember(name, Members), Is.Empty, "ResourceProject must not expose a second directory model: " + name);
            }

            Assert.That(typeof(ResourceProject).GetField("sourceDirectories", Members)?.FieldType,
                Is.EqualTo(typeof(List<ResourceSourceDirectory>)));
            foreach (var name in new[] { "LayoutVersion", "RootGuid" })
            {
                Assert.That(typeof(ResourceAtlasConfiguration).GetMember(name, Members), Is.Empty, "Atlas ownership must not depend on a legacy layout: " + name);
            }

            Assert.That(typeof(ResourceBuildMap).GetMember("AssetRoot", Members), Is.Empty, "BuildMap must expose the complete source collection, never a single-root shell.");
            var factories = typeof(ResourceAtlasGenerator).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(x => x.Name == "CreateConfiguration").ToArray();
            Assert.That(factories.Length, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { typeof(ResourceProject), typeof(string), typeof(string) },
                factories.Single().GetParameters().Select(x => x.ParameterType));
            Assert.That(factories.Single().GetParameters().Any(x => x.IsOptional), Is.False, "Every caller must explicitly choose the generation parent and folder.");
        }

        [Test]
        public void OneGroupCanSelectTwoRegisteredDirectoriesWithoutCollectingUnselectedFiles()
        {
            var a = Text("First/a.txt");
            var b = Text("Second/b.txt");
            Text("Second/unselected.txt");
            Configure(("First", ""), ("Second", "audio"));
            ResourceEditorOperations.AddSelections(_project, 0, new[] { AssetDatabase.LoadMainAssetAtPath(a), AssetDatabase.LoadMainAssetAtPath(b) });
            var map = ResourceBuildMap.Create(_project);
            CollectionAssert.AreEquivalent(new[] { "a.txt", "audio/b.txt" }, map.Assets.Select(x => x.Address.Path));
            CollectionAssert.AreEquivalent(new[] { a, b }, map.Bundles.SelectMany(x => x.AssetPaths));
        }

        [Test]
        public void ReorderingOrAddingDirectoriesDoesNotRenameExistingAddresses()
        {
            var a = Text("First/a.txt");
            var b = Text("Second/b.txt");
            _project.groups[0].selections.Add(Select(a));
            _project.groups[0].selections.Add(Select(b));
            Configure(("First", ""), ("Second", "audio"));
            CollectionAssert.AreEquivalent(new[] { "a.txt", "audio/b.txt" }, ResourceBuildMap.Create(_project).Assets.Select(x => x.Address.Path));
            Configure(("Unused", "extra"), ("Second", "audio"), ("First", ""));
            CollectionAssert.AreEquivalent(new[] { "a.txt", "audio/b.txt" }, ResourceBuildMap.Create(_project).Assets.Select(x => x.Address.Path));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOrEmptySourceCollectionCannotFallBackToTheProjectFolderOrExistingSelections(bool missing)
        {
            var path = Text("First/a.txt");
            _project.groups[0].selections.Add(Select(path));
            var source = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
            Configure();
            if (missing)
            {
                _project.sourceDirectories = null;
            }

            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(_project));
            Assert.Throws<InvalidOperationException>(() => ResourceWorkbenchGui.DisplayPaths(_project));
            Assert.That(_project.groups[0].selections.Count, Is.EqualTo(1));
            CollectionAssert.AreEqual(source, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
        }

        [Test]
        public void RemovingUsedDirectoryKeepsTheSelectionAndSourceButBlocksTheBuild()
        {
            var a = Text("First/a.txt");
            var b = Text("Second/b.txt");
            _project.groups[0].selections.Add(Select(a));
            _project.groups[0].selections.Add(Select(b));
            Configure(("First", ""), ("Second", "audio"));
            Assert.That(ResourceBuildMap.Create(_project).Assets.Count, Is.EqualTo(2));
            Configure(("First", ""));
            var error = Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(_project));
            Assert.That(error.Message, Does.Contain(b));
            Assert.That(_project.groups[0].selections.Count, Is.EqualTo(2));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(b)), Is.True);
        }

        [Test]
        public void DuplicateAndNestedDirectoriesAreRejectedRatherThanUsingTheFirstMatch()
        {
            _project.groups[0].selections.Add(Select(Text("First/a.txt")));
            Configure(("First", ""), ("First", "other"));
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(_project));
            AssetDatabase.CreateFolder(_root + "/First", "Child");
            Configure(("First", ""), ("First/Child", "child"));
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(_project));
        }

        [Test]
        public void AddressCollisionReportsBothActualSourcePaths()
        {
            var a = Text("First/same.txt");
            var b = Text("Second/same.txt");
            Configure(("First", ""), ("Second", ""));
            _project.groups[0].selections.Add(Select(a));
            _project.groups[0].selections.Add(Select(b));
            var error = Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(_project));
            Assert.That(error.Message, Does.Contain(a).And.Contain(b).And.Contain("same.txt"));
        }

        [Test]
        public void BrowsingCurrentDirectoriesPreservesAddressAliasAndDoesNotWriteTheAsset()
        {
            var a = Text("First/a.txt");
            Configure(("Second", "other"), ("First", "audio"));
            var selection = Select(a);
            selection.aliases = new[] { "stable" };
            _project.groups[0].selections.Add(selection);
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssetIfDirty(_project);
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(_project)));
            var map = ResourceBuildMap.Create(_project);
            Assert.That(ResourceWorkbenchGui.DisplayPaths(_project).DisplayAddress(a), Is.EqualTo("audio/a.txt"));
            Assert.That(map.Assets.Single().Address.Path, Is.EqualTo("audio/a.txt"));
            Assert.That(map.Assets.Single().Aliases.Single().Path, Is.EqualTo("stable"));
            Assert.That(EditorUtility.IsDirty(_project), Is.False);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(_project))));
        }

        [Test]
        public void SourceTraceAcceptsDirectoryReorderingButRejectsChangedPrefixOrRemovedSource()
        {
            _project.groups[0].selections.Add(Select(Text("First/a.txt")));
            Configure(("First", ""), ("Second", "audio"));
            var map = ResourceBuildMap.Create(_project);
            var source = map.Assets.Single().Source;
            Configure(("Second", "audio"), ("First", ""));
            Assert.That(ResourceBuildSourceLocator.Resolve(_project, source).Group, Is.EqualTo(0));
            Configure(("Second", "audio"), ("First", "changed"));
            Assert.Throws<InvalidOperationException>(() => ResourceBuildSourceLocator.Resolve(_project, source));
            Configure(("Second", "audio"));
            Assert.Throws<InvalidOperationException>(() => ResourceBuildSourceLocator.Resolve(_project, source));
        }

        [Test]
        public void ExplicitSaveDoesNotRewriteDirectoryOrderPrefixesOrAddresses()
        {
            var selection = Select(Text("First/a.txt"));
            selection.aliases = new[] { "stable" };
            _project.groups[0].selections.Add(selection);
            Configure(("Second", "other"), ("First", "audio"));
            var before = JsonConvert.SerializeObject(_project.sourceDirectories);
            EditorUtility.SetDirty(_project);
            ResourceEditorOperations.SaveProject(_project);
            Assert.That(JsonConvert.SerializeObject(_project.sourceDirectories), Is.EqualTo(before));
            var map = ResourceBuildMap.Create(_project);
            Assert.That(map.Assets.Single().Address.Path, Is.EqualTo("audio/a.txt"));
            Assert.That(map.Assets.Single().Aliases.Single().Path, Is.EqualTo("stable"));
            Assert.That(EditorUtility.IsDirty(_project), Is.False);
        }

        [Test]
        public void DisplayDirectoryValidationReusesTheSameSnapshotButInvalidatesChangedMeaning()
        {
            Configure(("First", ""), ("Second", "audio"));
            var before = ResourceWorkbenchGui.DisplayPaths(_project);
            Assert.That(ResourceWorkbenchGui.DisplayPaths(_project), Is.SameAs(before));
            Assert.That(before.DisplayAddress(_root + "/First"), Is.EqualTo(""));
            Assert.That(before.DisplayAddress(_root + "/Second/a.txt"), Is.EqualTo("audio/a.txt"), "A display address is computed from the frozen directory relation, not by scanning the file system.");
            Configure(("Second", "audio"), ("First", ""));
            Assert.That(ResourceWorkbenchGui.DisplayPaths(_project), Is.SameAs(before), "A mere list order change must not rescan source trees while drawing.");
            Configure(("First", "changed"), ("Second", "audio"));
            Assert.That(ResourceWorkbenchGui.DisplayPaths(_project), Is.Not.SameAs(before));
            Assert.That(ResourceWorkbenchGui.DisplayPaths(_project).DisplayAddress(_root + "/First"), Is.EqualTo("changed"));
            Configure();
            Assert.Throws<InvalidOperationException>(() => ResourceWorkbenchGui.DisplayPaths(_project));
        }

        [Test]
        public void MovingAnOwnedSourceDirectoryUsesItsGuidAndPreservesItsRelativeAddress()
        {
            var file = Text("First/a.txt");
            var selection = Select(file);
            _project.groups[0].selections.Add(selection);
            Configure(("First", "audio"), ("Second", "other"));
            var directoryGuid = _project.sourceDirectories[0].guid;
            Assert.That(ResourceBuildMap.Create(_project).Assets.Single().Address.Path, Is.EqualTo("audio/a.txt"));
            Assert.That(AssetDatabase.MoveAsset(_root + "/First", _root + "/MovedFirst"), Is.Empty);
            var map = ResourceBuildMap.Create(_project);
            Assert.That(_project.sourceDirectories[0].guid, Is.EqualTo(directoryGuid));
            Assert.That(map.Assets.Single().SourcePath, Is.EqualTo(_root + "/MovedFirst/a.txt"));
            Assert.That(map.Assets.Single().Guid, Is.EqualTo(selection.guid));
            Assert.That(map.Assets.Single().Address.Path, Is.EqualTo("audio/a.txt"));
        }

        [Test]
        public void MissingGuidAssetsRootAndAFileCannotBecomeSourceDirectoriesOrExpandTheRange()
        {
            var file = Text("First/a.txt");
            _project.groups[0].selections.Add(Select(file));
            foreach (var guid in new[] { Guid.NewGuid().ToString("N"), AssetDatabase.AssetPathToGUID("Assets"), AssetDatabase.AssetPathToGUID(file) })
            {
                JsonUtility.FromJsonOverwrite(JsonConvert.SerializeObject(new
                {
                    sourceDirectories = new[] { new { guid = guid, addressPrefix = "" } }
                }), _project);
                var error = Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(_project));
                Assert.That(error.Message, Does.Contain("目录"));
                Assert.That(_project.groups[0].selections.Count, Is.EqualTo(1));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(file)), Is.True);
            }
        }

        private string Text(string relative)
        {
            var path = _root + "/" + relative;
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "owned multi-directory fixture");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static ResourceSelection Select(string path) => new() { guid = AssetDatabase.AssetPathToGUID(path) };

        private void Configure(params (string Directory, string Prefix)[] directories) => JsonUtility.FromJsonOverwrite(JsonConvert.SerializeObject(new
        {
            sourceDirectories = directories.Select(x => new { guid = AssetDatabase.AssetPathToGUID(_root + "/" + x.Directory), addressPrefix = x.Prefix }).ToArray()
        }), _project);
    }
}
