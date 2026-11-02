using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed class ResourceMultiDirectoryAtlasTests
    {
        [Test]
        public void ExplicitRegistrationGeneratesOutsideEverySourceAndKeepsOwnedIdentitiesOnRepeat()
        {
            using var fixture = new ResourceReleaseFixture();
            AddDirectory(fixture, "Other");
            var other = ResourceAutomaticAtlasTests.AddSprite(fixture.Folder + "/Other", "Blue", Color.blue);
            var project = fixture.Project;
            ConfigureDirectories(project, (fixture.Folder + "/Content", "ui"), (fixture.Folder + "/Other", "audio"));
            project.groups.Clear();
            project.groups.Add(new ResourceGroup { id = "generated" });
            Save(project);
            var configuration = CreateExplicit(project, fixture.Folder, "ExternalAtlases");
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "all",
                groupId = "generated",
                address = "atlases/all",
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(fixture.Folder + "/Content/Sprites.png"), AssetDatabase.LoadAssetAtPath<Texture2D>(other) }
            });
            Save(configuration);
            var metas = new[] { fixture.Folder + "/Content/Sprites.png.meta", other + ".meta" }.Select(File.ReadAllBytes).ToArray();
            var preview = ResourceAtlasGenerator.Preview(project);
            Assert.That(preview.DirectoryPath, Is.EqualTo(fixture.Folder + "/ExternalAtlases"));
            Assert.That(preview.Changes.Single().Sources.Count, Is.EqualTo(2));
            ResourceAtlasGenerator.Generate(project);
            var output = ResourceAtlasGenerator.Preview(project).Changes.Single();
            Assert.That(output.Kind, Is.EqualTo(ResourceAtlasChangeKind.Current));
            var before = ResourceReleaseFixture.Snapshot(preview.DirectoryPath);
            var projectBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(project)));
            ResourceAtlasGenerator.Generate(project);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(preview.DirectoryPath));
            CollectionAssert.AreEqual(projectBytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(project))));
            Assert.That(AssetDatabase.AssetPathToGUID(output.Path), Is.EqualTo(output.Guid));
            var map = ResourceBuildMap.Create(project);
            Assert.That(map.Assets.Single().Address.Path, Is.EqualTo("atlases/all"));
            CollectionAssert.IsSubsetOf(new[] { fixture.Folder + "/Content/Sprites.png", other }, map.Bundles.SelectMany(x => x.AssetPaths).ToArray());
            CollectionAssert.AreEqual(metas[0], File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Folder + "/Content/Sprites.png.meta")));
            CollectionAssert.AreEqual(metas[1], File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(other + ".meta")));
            fixture.AssertProtected();
        }

        [Test]
        public void PerFolderUsesEachPersistentPrefixAndFolderGuidIncludingTheSourceRootItself()
        {
            using var fixture = new ResourceReleaseFixture();
            AddDirectory(fixture, "Other");
            AssetDatabase.CreateFolder(fixture.Folder + "/Content", "Icons");
            Assert.That(AssetDatabase.MoveAsset(fixture.Folder + "/Content/Sprites.png", fixture.Folder + "/Content/Icons/Sprites.png"), Is.Empty);
            ResourceAutomaticAtlasTests.AddSprite(fixture.Folder + "/Other", "Blue", Color.blue);
            var project = fixture.Project;
            ConfigureDirectories(project, (fixture.Folder + "/Content", "ui"), (fixture.Folder + "/Other", "audio"));
            project.groups.Clear();
            project.groups.Add(new ResourceGroup { id = "generated" });
            Save(project);
            var configuration = CreateExplicit(project, fixture.Folder, "ExternalAtlases");
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "folders",
                groupId = "generated",
                address = "atlases/shared",
                grouping = ResourceAtlasGrouping.PerFolder,
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(fixture.Folder + "/Content"), AssetDatabase.LoadAssetAtPath<DefaultAsset>(fixture.Folder + "/Other") }
            });
            Save(configuration);
            ResourceAtlasGenerator.Generate(project);
            var first = ResourceAtlasGenerator.Preview(project);
            CollectionAssert.AreEquivalent(new[] { "atlases/shared/ui/Icons", "atlases/shared/audio" }, first.Changes.Select(x => x.Address));
            var icon = first.Changes.Single(x => x.Address == "atlases/shared/ui/Icons");
            Assert.That(icon.Key, Is.EqualTo("folders/" + AssetDatabase.AssetPathToGUID(fixture.Folder + "/Content/Icons")));
            var before = ResourceReleaseFixture.Snapshot(first.DirectoryPath);
            ConfigureDirectories(project, (fixture.Folder + "/Other", "audio"), (fixture.Folder + "/Content", "ui"));
            Save(project);
            ResourceAtlasGenerator.Generate(project);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(first.DirectoryPath));
            Assert.That(AssetDatabase.MoveAsset(fixture.Folder + "/Content/Icons", fixture.Folder + "/Content/Renamed"), Is.Empty);
            ResourceAtlasGenerator.Generate(project);
            var moved = ResourceAtlasGenerator.Preview(project).Changes.Single(x => x.Key == icon.Key);
            Assert.That(moved.Guid, Is.EqualTo(icon.Guid));
            Assert.That(moved.Address, Is.EqualTo("atlases/shared/ui/Renamed"));
            CollectionAssert.AreEquivalent(new[] { "atlases/shared/ui/Renamed", "atlases/shared/audio" }, ResourceBuildMap.Create(project).Assets.Select(x => x.Address.Path));
            fixture.AssertProtected();
        }

        [Test]
        public void ExplicitSourceInternalGenerationKeepsItsOwnershipAndExclusionWhenAnotherSourceIsAdded()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = ResourceAutomaticAtlasTests.Configure(fixture);
            var sourcePath = fixture.Folder + "/Content/Sprites.png";
            var sourceBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath));
            var sourceMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath + ".meta"));
            ResourceAtlasGenerator.Generate(fixture.Project);
            var preview = ResourceAtlasGenerator.Preview(fixture.Project);
            var output = preview.Changes.Single();
            CollectionAssert.AreEqual(new[] { sourcePath }, output.Sources);
            var before = ResourceReleaseFixture.Snapshot(preview.DirectoryPath);
            AddDirectory(fixture, "Other");
            var project = fixture.Project;
            ConfigureDirectories(project, (fixture.Folder + "/Other", "extra"), (fixture.Folder + "/Content", ""));
            Save(project);
            Assert.That(ResourceAtlasGenerator.Preview(project).Changes.Single().Kind, Is.EqualTo(ResourceAtlasChangeKind.Current));
            ResourceAtlasGenerator.Generate(project);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(preview.DirectoryPath));
            Assert.That(AssetDatabase.AssetPathToGUID(output.Path), Is.EqualTo(output.Guid));
            Assert.That(ResourceBuildMap.Create(project).Assets.Single().Address.Path, Is.EqualTo("atlases/ui"));
            Assert.That(AssetDatabase.GetAssetPath(configuration), Is.EqualTo(fixture.Folder + "/Content/GeneratedAtlases/AutomaticAtlases.asset"));
            project.groups[0].selections.Add(new ResourceSelection
            {
                guid = AssetDatabase.AssetPathToGUID(fixture.Folder + "/Content"),
                extensions = new[] { ".txt", ".spriteatlasv2" }
            });
            Save(project);
            var map = ResourceBuildMap.Create(project);
            CollectionAssert.AreEquivalent(new[] { "atlases/ui", "entry.txt" }, map.Assets.Select(x => x.Address.Path));
            Assert.That(map.Assets.Count(x => x.Guid == output.Guid), Is.EqualTo(1), "Ancestor selection must not collect an automatic output a second time.");
            Assert.That(map.Bundles.SelectMany(x => x.AssetPaths), Does.Not.Contain(AssetDatabase.GetAssetPath(configuration)));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(preview.DirectoryPath));
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath)));
            CollectionAssert.AreEqual(sourceMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath + ".meta")));
            fixture.AssertProtected();
        }

        [Test]
        public void MissingGenerationParentIdentityCannotFallBackToASourceOrTheConfigurationLocation()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = ResourceAutomaticAtlasTests.Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var directory = ResourceAtlasGenerator.Preview(fixture.Project).DirectoryPath;
            using (var serialized = new SerializedObject(configuration))
            {
                var parent = serialized.FindProperty("generationParentGuid");
                Assert.That(parent, Is.Not.Null);
                parent.stringValue = "";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Save(configuration);
            var before = ResourceReleaseFixture.Snapshot(fixture.Folder);
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Preview(fixture.Project));
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(fixture.Folder));
            Assert.That(AssetDatabase.IsValidFolder(directory), Is.True);
            fixture.AssertProtected();
        }

        [TestCase(null)]
        [TestCase("")]
        public void MultiSourceRegistrationRequiresAnExplicitOutputParentRatherThanTakingTheFirstSource(string parent)
        {
            using var fixture = new ResourceReleaseFixture();
            AddDirectory(fixture, "Other");
            var project = fixture.Project;
            ConfigureDirectories(project, (fixture.Folder + "/Content", ""), (fixture.Folder + "/Other", "audio"));
            Save(project);
            var before = ResourceReleaseFixture.Snapshot(fixture.Folder);
            Assert.Throws<InvalidOperationException>(() => CreateExplicit(project, parent, "GeneratedAtlases"));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(fixture.Folder));
            Assert.That(project.automaticAtlases, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExplicitRegistrationNeverAdoptsAnExistingDirectoryOrOrphanMeta(bool orphanMeta)
        {
            using var fixture = new ResourceReleaseFixture();
            var path = fixture.Folder + "/ExternalAtlases";
            if (orphanMeta)
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"), "external content, not an owned directory");
            }
            else
            {
                AddDirectory(fixture, "ExternalAtlases");
            }

            var before = ResourceReleaseFixture.Snapshot(fixture.Folder);
            Assert.Throws<InvalidOperationException>(() => CreateExplicit(fixture.Project, fixture.Folder, "ExternalAtlases"));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(fixture.Folder));
            Assert.That(fixture.Project.automaticAtlases, Is.Null);
        }

        [Test]
        public void UnknownContentInAnExternalOutputDirectoryStillBlocksGenerationWithoutWrites()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = CreateExplicit(fixture.Project, fixture.Folder, "ExternalAtlases");
            var directory = Path.GetDirectoryName(AssetDatabase.GetAssetPath(configuration));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(directory + "/unknown.txt"), "external content");
            var before = ResourceReleaseFixture.Snapshot(directory);
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(directory));
        }

        [Test]
        public void AReboundVisibleReferenceCannotRemoveProtectionFromTheOriginallyRegisteredDirectory()
        {
            using var owner = new ResourceReleaseFixture();
            using var other = new ResourceReleaseFixture();
            var original = CreateExplicit(owner.Project, owner.Folder, "ExternalAtlases");
            var originalDirectory = Path.GetDirectoryName(AssetDatabase.GetAssetPath(original)).Replace('\\', '/');
            AddDirectory(owner, "Different");
            var copyPath = owner.Folder + "/Different/ConfigurationCopy.asset";
            Assert.That(AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(original), copyPath), Is.True);
            owner.Project.automaticAtlases = AssetDatabase.LoadAssetAtPath<ResourceAtlasConfiguration>(copyPath);
            Save(owner.Project);
            var before = ResourceReleaseFixture.Snapshot(originalDirectory);
            Assert.Throws<InvalidOperationException>(() => CreateExplicit(other.Project, originalDirectory, "Intruder"));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(originalDirectory));
        }

        [Test]
        public void EqualLeafFolderNamesHaveDistinctOutputIdentitiesAndAddressConflictsShowBothSources()
        {
            using var fixture = new ResourceReleaseFixture();
            AddDirectory(fixture, "Other");
            var first = fixture.Folder + "/Content/Icons";
            var second = fixture.Folder + "/Other/Icons";
            AssetDatabase.CreateFolder(fixture.Folder + "/Content", "Icons");
            AssetDatabase.CreateFolder(fixture.Folder + "/Other", "Icons");
            Assert.That(AssetDatabase.MoveAsset(fixture.Folder + "/Content/Sprites.png", first + "/Sprites.png"), Is.Empty);
            ResourceAutomaticAtlasTests.AddSprite(second, "Blue", Color.blue);
            var project = fixture.Project;
            ConfigureDirectories(project, (fixture.Folder + "/Content", "a"), (fixture.Folder + "/Other", "b"));
            project.groups.Clear();
            project.groups.Add(new ResourceGroup { id = "generated" });
            Save(project);
            var configuration = CreateExplicit(project, fixture.Folder, "ExternalAtlases");
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "same-leaf",
                groupId = "generated",
                address = "atlases/shared",
                grouping = ResourceAtlasGrouping.PerFolder,
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(first), AssetDatabase.LoadAssetAtPath<DefaultAsset>(second) }
            });
            Save(configuration);
            ResourceAtlasGenerator.Generate(project);
            var plan = ResourceAtlasGenerator.Preview(project);
            CollectionAssert.AreEquivalent(new[] { "atlases/shared/a/Icons", "atlases/shared/b/Icons" }, plan.Changes.Select(x => x.Address));
            Assert.That(plan.Changes.Select(x => x.Key).Distinct().Count(), Is.EqualTo(2));
            var before = ResourceReleaseFixture.Snapshot(plan.DirectoryPath);
            ConfigureDirectories(project, (fixture.Folder + "/Content", ""), (fixture.Folder + "/Other", ""));
            Save(project);
            var error = Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Preview(project));
            Assert.That(error.Message, Does.Contain("atlases/shared/Icons").And.Contain(first).And.Contain(second));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(plan.DirectoryPath));
        }

        internal static void AddDirectory(ResourceReleaseFixture fixture, string name) =>
            Assert.That(AssetDatabase.CreateFolder(fixture.Folder, name), Is.Not.Empty);

        internal static void ConfigureDirectories(ResourceProject project, params (string Path, string Prefix)[] directories) =>
            JsonUtility.FromJsonOverwrite(JsonConvert.SerializeObject(new
            {
                sourceDirectories = directories.Select(x => new { guid = AssetDatabase.AssetPathToGUID(x.Path), addressPrefix = x.Prefix }).ToArray()
            }), project);

        internal static void Save(Object asset)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        internal static ResourceAtlasConfiguration CreateExplicit(ResourceProject project, string parent, string name)
        {
            var method = typeof(ResourceAtlasGenerator).GetMethod("CreateConfiguration", new[] { typeof(ResourceProject), typeof(string), typeof(string) });
            Assert.That(method, Is.Not.Null, "Explicit owned Atlas registration must support an existing output parent outside the sources.");
            try
            {
                return (ResourceAtlasConfiguration)method.Invoke(null, new object[] { project, parent, name });
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
