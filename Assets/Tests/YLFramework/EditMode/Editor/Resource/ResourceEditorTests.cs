using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

using YLFramework.Resource;
using YLFramework.Tests.ResourceBuild;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceEditor
{
    public class ResourceEditorTests
    {
        private string _folder;
        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/Tests/YLFramework/EditMode/Resource82c-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode", Path.GetFileName(_folder));
        }
        [TearDown] public void TearDown() => AssetDatabase.DeleteAsset(_folder);

        [TestCase(false)]
        [TestCase(true)]
        [Category("YL.Special.ResourceCurrent")]
        public void CurrentResourceAssetsKeepNestedDefinitionsReferencesAndExplicitFlags(bool enabled)
        {
            var fields = new Dictionary<Type, string[]>
            {
                [typeof(ResourceProject)] = "ProjectId DisplayName SourceDirectories Compression ContentEncryption ContentKeyId AutomaticAtlases AutomaticAtlasConfigurationGuid Groups".Split(' '),
                [typeof(ResourceSourceDirectory)] = "Guid AddressPrefix".Split(' '),
                [typeof(ResourceGroup)] = "Id Packing Selections".Split(' '),
                [typeof(ResourceSelection)] = "Guid Address Aliases Extensions".Split(' '),
                [typeof(ResourceAtlasConfiguration)] = "Rules ProjectGuid GenerationParentGuid DirectoryGuid ConfigurationGuid ConfigurationMetaHash DirectoryMetaHash Outputs".Split(' '),
                [typeof(ResourceAtlasConfiguration.Output)] = "Key FileName Guid InputHash AssetHash MetaHash".Split(' '),
                [typeof(ResourceAtlasRule)] = "Id GroupId Grouping Address Sources AllowRotation TightPacking Padding MaxTextureSize Readable MipMaps SRgb FilterMode Compression".Split(' '),
                [typeof(ResourceBuildRecipe)] = "Project Target AppContentCompatibilityId Action ForceRebuild GenerateReferences ReferenceDirectory".Split(' '),
                [typeof(ResourceWorkbenchLayout)] = "GroupWidth DetailWidth ShowGroups ShowDetails CompactDetails Settings Output OutputHeight Task Columns SelectionProjectGuid SelectionGroup EntrySearch SelectedRows".Split(' '),
                [typeof(ResourceWorkbenchRowState)] = "ProjectGuid Group Guid Definition Address".Split(' ')
            };
            string Current(string old) => old == "SRgb" ? "srgb" : char.ToLowerInvariant(old[0]) + old.Substring(1);
            foreach (var pair in fields)
            {
                foreach (var old in pair.Value)
                {
                    var field = pair.Key.GetField(Current(old), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.That(field, Is.Not.Null, pair.Key.Name + "." + Current(old));
                    Assert.That(field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>(), Is.Empty, pair.Key.Name + "." + old);
                }
            }
            var atlas = ScriptableObject.CreateInstance<ResourceAtlasConfiguration>();
            AssetDatabase.CreateAsset(atlas, _folder + "/Atlas.asset");
            atlas.projectGuid = "project-guid";
            atlas.generationParentGuid = "parent-guid";
            atlas.directoryGuid = "directory-guid";
            atlas.configurationGuid = "configuration-guid";
            atlas.configurationMetaHash = "configuration-hash";
            atlas.directoryMetaHash = "directory-hash";
            atlas.rules.Add(new ResourceAtlasRule
            {
                id = "legacy-rule",
                groupId = "ui",
                grouping = ResourceAtlasGrouping.PerFolder,
                address = "legacy/atlas",
                sources = new Object[] { atlas },
                allowRotation = enabled,
                tightPacking = !enabled,
                padding = 8,
                maxTextureSize = 1024,
                readable = enabled,
                mipMaps = !enabled,
                srgb = enabled,
                filterMode = FilterMode.Point,
                compression = TextureImporterCompression.Compressed
            });
            atlas.outputs.Add(new ResourceAtlasConfiguration.Output
            {
                key = "legacy-key",
                fileName = "legacy.spriteatlas",
                guid = "output-guid",
                inputHash = "input-hash",
                assetHash = "asset-hash",
                metaHash = "meta-hash"
            });
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            AssetDatabase.CreateAsset(project, _folder + "/Project.asset");
            project.projectId = "legacy-project";
            project.displayName = "Legacy Project";
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = "source-guid", addressPrefix = "legacy" });
            project.compression = ResourceCompression.Uncompressed;
            project.contentEncryption = ResourceContentEncryption.None;
            project.contentKeyId = "legacy-key-id";
            project.automaticAtlases = atlas;
            project.automaticAtlasConfigurationGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(atlas));
            project.groups.Add(new ResourceGroup
            {
                id = "ui",
                packing = ResourcePacking.PerAsset,
                selections = new List<ResourceSelection>
                {
                    new ResourceSelection { guid = "selection-guid", address = "legacy/item", aliases = new[] { "old/item" }, extensions = new[] { ".prefab" } }
                }
            });
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            AssetDatabase.CreateAsset(recipe, _folder + "/Recipe.asset");
            recipe.project = project;
            recipe.target = BuildTarget.StandaloneWindows64;
            recipe.appContentCompatibilityId = "legacy-app";
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.forceRebuild = enabled;
            recipe.generateReferences = !enabled;
            recipe.referenceDirectory = "Assets/LegacyReferences";
            var window = ScriptableObject.CreateInstance<ResServiceEditorWindow>();
            try
            {
                window.hideFlags = HideFlags.None;
                var layout = new ResourceWorkbenchLayout
                {
                    groupWidth = 237,
                    detailWidth = 419,
                    showGroups = enabled,
                    showDetails = !enabled,
                    compactDetails = enabled,
                    settings = !enabled,
                    output = enabled,
                    outputHeight = 187,
                    task = 2,
                    selectionProjectGuid = "selected-project",
                    selectionGroup = "ui",
                    entrySearch = "legacy-search",
                    selectedRows = new[] { new ResourceWorkbenchRowState { projectGuid = "selected-project", group = "ui", guid = "row-guid", definition = "row-definition", address = "row-address" } }
                };
                typeof(ResServiceEditorWindow).GetField("layout", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, layout);
                AssetDatabase.CreateAsset(window, _folder + "/Window.asset");
                foreach (var asset in new Object[] { atlas, project, recipe, window })
                {
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssetIfDirty(asset);
                    var path = AssetDatabase.GetAssetPath(asset);
                    var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
                    var snapshot = EditorJsonUtility.ToJson(asset);
                    var yaml = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    foreach (var old in fields.Values.SelectMany(value => value).Distinct())
                    {
                        Assert.That(System.Text.RegularExpressions.Regex.IsMatch(yaml, @"(?m)^[ \t]*(?:- )?" + old + ":"), Is.False, path + ":" + old);
                    }

                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    var restored = AssetDatabase.LoadMainAssetAtPath(path);
                    Assert.That(EditorJsonUtility.ToJson(restored), Is.EqualTo(snapshot), path);
                    EditorUtility.SetDirty(restored);
                    AssetDatabase.SaveAssetIfDirty(restored);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    Assert.That(EditorJsonUtility.ToJson(AssetDatabase.LoadMainAssetAtPath(path)), Is.EqualTo(snapshot), path);
                    CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
                }
                Assert.That(AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(_folder + "/Recipe.asset").project, Is.SameAs(project));
            }
            finally
            {
                if (window && !EditorUtility.IsPersistent(window))
                {
                    Object.DestroyImmediate(window);
                }
            }
        }

        [Test]
        public void ResourceEditorPathsFollowBuildServiceCodeGenerationAndDataResponsibilities()
        {
            const string Root = "Packages/com.kano.ylframework/Editor/Systems/Resource/";
            foreach (var file in new[] { "Build/ResourceBuildOperation.cs", "Build/ResourceBuildMap.cs", "Build/ResourceBuildWorkspace.cs",
                "Build/ResourceBundleBuilder.cs", "Build/ResourceCatalogBuilder.cs", "Data/ResourceProject.cs", "Publishing/ResourcePublisher.cs",
                "ResourceEditorOperations.cs", "ResourceEditorSimulation.cs", "Workbench/ResServiceAssetsMenu.cs", "Workbench/ResServiceEditorWindow.cs",
                "CodeGeneration/ResourceCodeGenerator.cs", "CodeGeneration/ResourceReferenceOutput.cs" })
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + file)) && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + file + ".meta")), Is.True, file);
                Assert.That(Path.GetFileNameWithoutExtension(UnityEditor.Compilation.CompilationPipeline.GetAssemblyNameFromScriptPath(Root + file)), Is.EqualTo("YLFramework.Editor"));
            }
            var assembly = typeof(ResourceProject).Assembly;
            Assert.That(assembly.GetType("YLFramework.Editor.Resource.BuildScript"), Is.Null);
            Assert.That(assembly.GetType("YLFramework.Editor.Resource.AssetBundleExporter"), Is.Null);
            Assert.That(assembly.GetType("YLFramework.Editor.Resource.ResourceBuildOperation").GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(x => x.Name == "Execute" || x.Name == "CreatePlan"), Is.False, "There must not be a competing legacy builder.");
        }

        [Test]
        public void FormalSelectionEditingSupportsUndoAndSavedGuidWithoutImporterWrites()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            var path = fixture.Folder + "/Content/Shared.asset";
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
            var count = project.groups[0].selections.Count;
            ResourceEditorOperations.AddSelections(project, 0, new[] { AssetDatabase.LoadMainAssetAtPath(path) });
            Assert.That(project.groups[0].selections.Count, Is.EqualTo(count + 1));
            Undo.PerformUndo();
            Assert.That(project.groups[0].selections.Count, Is.EqualTo(count));
            Undo.PerformRedo();
            Assert.That(project.groups[0].selections[count].guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
            AssetDatabase.SaveAssetIfDirty(project);
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(project), ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.That(fixture.Project.groups[0].selections.Count, Is.EqualTo(count + 1));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.AddSelections(fixture.Project, 0, new[] { AssetDatabase.LoadMainAssetAtPath(_folder) }));
            Undo.ClearUndo(fixture.Project);
        }

        [Test]
        public void WorkbenchCreatesAndSavesOnlyNewAssetsIdentityWithoutOverwritingExistingData()
        {
            var path = _folder + "/Created.asset";
            var project = ResourceEditorOperations.CreateProject(path);
            Assert.That(project, Is.SameAs(AssetDatabase.LoadAssetAtPath<ResourceProject>(path)));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Has.Length.EqualTo(32));
            project.displayName = "saved definition";
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
            Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.CreateProject(path));
            Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.CreateProject("Packages/Protected82c.asset"));
            Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.CreateProject(_folder + "/../Escaped.asset"));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadAssetAtPath<ResourceProject>(path).displayName, Is.EqualTo("saved definition"));
        }

        [Test]
        public void WorkbenchRefusesJunctionDefinitionBeforeEditingOrSavingExternalBytes()
        {
            using var fixture = new ResourceReleaseFixture();
            var source = fixture.Folder + "/Project.asset";
            var outside = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Temp/Definition82c-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(outside));
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(source), global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "Project.asset")));
            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta"), global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "Project.asset.meta")));
            AssetDatabase.DeleteAsset(source);
            var link = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Linked"));
            try
            {
                ResourceOutputSafetyTests.CreateJunction(link, outside);
                AssetDatabase.ImportAsset(_folder + "/Linked/Project.asset", ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var linked = AssetDatabase.LoadAssetAtPath<ResourceProject>(_folder + "/Linked/Project.asset");
                Assert.That(linked, Is.Not.Null);
                var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "Project.asset")));
                var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "Project.asset.meta")));
                var view = new ResServiceView();
                try
                {
                    Assert.Throws<InvalidOperationException>(() => view.SetProject(linked));
                }
                finally
                {
                    view.OnDispose();
                }
                Assert.Throws<InvalidOperationException>(() => Invoke(typeof(ResourceEditorOperations), "SaveProject", linked));
                Assert.Throws<InvalidOperationException>(() => Invoke(typeof(ResourceEditorOperations), "CreateProject", _folder + "/Linked/New.asset"));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "Project.asset"))));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "Project.asset.meta"))));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(outside, "New.asset"))), Is.False);
            }
            finally
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(link)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(link));
                }

                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Linked.meta")))
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Linked.meta"));
                }

                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(outside), true);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        [Test]
        public void DirtyCandidateCannotDisappearFromPublishedGenerationCollection()
        {
            using var first = new ResourceReleaseFixture();
            using var second = new ResourceReleaseFixture();
            var output = _folder + "/Generated";
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(output));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/YLResourceReferences.cs"), "collection sentinel");
            var path = output + "/YLResourceReferences.cs";
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var time = File.GetLastWriteTimeUtc(path);
            first.Project.projectId = "unpersisted-id";
            EditorUtility.SetDirty(first.Project);
            var error = Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.PublishedCatalogs("fixture-v1"));
            StringAssert.Contains("未保存", error.Message, "A dirty definition must fail as dirty, not be interpreted as an unpublished project.");
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(time));
        }

        [Test]
        public void DirtyDefinitionsAndCanceledOperationsDoNotCreateOutputs()
        {
            using var fixture = new ResourceReleaseFixture();
            var project = fixture.Project;
            project.displayName = "pending definition edit";
            EditorUtility.SetDirty(project);
            Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.Preflight(project, "fixture-v1"));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var report = ResourceEditorOperations.BuildAndPublish(project, "fixture-v1", cancellation: cancel.Token);
            Assert.That(report.BuildError, Is.InstanceOf<OperationCanceledException>());
            Assert.That(report.Build, Is.Null);
            Assert.That(report.Release, Is.Null);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("AssetBundles/YLFramework/" + project.projectId)), Is.False);
        }

        private ResourceProject Project(string root, string name, string selection)
        {
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "yl-editor-" + Guid.NewGuid().ToString("N");
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(root) });
            project.groups = new List<ResourceGroup> { new() { id = "main", selections = new List<ResourceSelection>
                { new() { guid = AssetDatabase.AssetPathToGUID(selection), extensions = new[] { ".bytes" } } } } };
            AssetDatabase.CreateAsset(project, _folder + "/" + name + ".asset");
            return project;
        }
        private void Text(string path)
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "isolated formal resource selection");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }
        [Test]
        public void DirectoryBoundaryDoesNotClaimSiblingWithSamePrefix()
        {
            AssetDatabase.CreateFolder(_folder, "Owner");
            AssetDatabase.CreateFolder(_folder, "OwnerExtra");
            Text(_folder + "/Owner/Text.bytes");
            Text(_folder + "/OwnerExtra/Text.bytes");
            var project = Project(_folder + "/Owner", "Project", _folder + "/Owner");
            var map = ResourceBuildMap.Create(project);
            CollectionAssert.AreEqual(new[] { _folder + "/Owner/Text.bytes" }, map.Assets.Select(x => x.SourcePath));
            project.groups[0].selections[0].guid = AssetDatabase.AssetPathToGUID(_folder + "/OwnerExtra/Text.bytes");
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(project));
            Assert.That(AssetImporter.GetAtPath(_folder + "/OwnerExtra/Text.bytes").assetBundleName, Is.Empty);
        }
        [Test]
        public void OverlappingProjectsAndOutsideSelectionsRemainRejected()
        {
            AssetDatabase.CreateFolder(_folder, "Child");
            Text(_folder + "/Child/Text.bytes");
            Text(_folder + "/Default.bytes");
            var parent = Project(_folder, "Parent", _folder + "/Child/Text.bytes");
            var child = Project(_folder + "/Child", "Child", _folder + "/Child/Text.bytes");
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(parent));
            AssetDatabase.DeleteAsset(_folder + "/Parent.asset");
            child.groups[0].selections[0].guid = AssetDatabase.AssetPathToGUID(_folder + "/Default.bytes");
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(child));
        }
        internal static object Invoke(Type type, string method, params object[] args)
        {
            var member = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).SingleOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            Assert.That(member, Is.Not.Null, "Missing operation: " + method);
            try
            {
                return member.Invoke(null, args);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }
    }
}
