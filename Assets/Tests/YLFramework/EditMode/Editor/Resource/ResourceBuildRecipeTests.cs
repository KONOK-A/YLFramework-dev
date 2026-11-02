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
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceBuildRecipeTests
    {
        [Test]
        public void SavedRecipeResolvesAfterReloadWithoutWindowOrRuntimeInitialization()
        {
            var type = typeof(ResourceProject).Assembly.GetType("YLFramework.Editor.Resource.ResourceBuildRecipe");
            Assert.That(type, Is.Not.Null, "A shared, persistent execution recipe is missing; window fields are not project configuration.");
            var resolve = typeof(ResourceEditorOperations).GetMethod("ResolveRecipe", new[] { type });
            Assert.That(resolve, Is.Not.Null, "Viewing and executing must use one side-effect-free resolver.");
            var root = "Assets/Tests/YLFramework/Fixtures/Recipe-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", Path.GetFileName(root)), Is.Not.Empty);
            try
            {
                var project = ScriptableObject.CreateInstance<ResourceProject>();
                project.projectId = "recipe-owned";
                project.contentKeyId = "not-a-key";
                AssetDatabase.CreateAsset(project, root + "/Project.asset");
                var recipe = ScriptableObject.CreateInstance(type);
                AssetDatabase.CreateAsset(recipe, root + "/Recipe.asset");
                using (var serialized = new SerializedObject(recipe))
                {
                    serialized.FindProperty("project").objectReferenceValue = project;
                    serialized.FindProperty("target").intValue = (int)BuildTarget.StandaloneWindows64;
                    serialized.FindProperty("appContentCompatibilityId").stringValue = "recipe-v1";
                    serialized.FindProperty("forceRebuild").boolValue = true;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                AssetDatabase.SaveAssetIfDirty(recipe);
                AssetDatabase.ImportAsset(root + "/Recipe.asset", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                recipe = AssetDatabase.LoadAssetAtPath<ScriptableObject>(root + "/Recipe.asset");
                var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Project.asset"));
                var input = resolve.Invoke(null, new object[] { recipe });
                Assert.That(Property(input, "ProjectId"), Is.EqualTo("recipe-owned"));
                Assert.That(Property(input, "Target"), Is.EqualTo(BuildTarget.StandaloneWindows64));
                Assert.That(Property(input, "AppContentCompatibilityId"), Is.EqualTo("recipe-v1"));
                Assert.That(Property(input, "ForceRebuild"), Is.True);
                Assert.That(input.ToString(), Does.Contain("BuildOnly").And.Contain("recipe-v1"));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(root + "/Project.asset")));
                Assert.That(YLFramework.Resource.ResourceSystem.Current, Is.Null);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("AssetBundles/YLFramework/recipe-owned")), Is.False);
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        private static object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);

        [Test]
        public void SerializedRecipeSupportsUndoAndFrozenOptionsDoNotTrackLaterEdits()
        {
            using var fixture = new RecipeFixture();
            var recipe = fixture.Recipe;
            var input = ResourceEditorOperations.ResolveRecipe(recipe);
            Undo.IncrementCurrentGroup();
            using (var serialized = new SerializedObject(recipe))
            {
                serialized.FindProperty("appContentCompatibilityId").stringValue = "changed-app";
                serialized.FindProperty("forceRebuild").boolValue = true;
                serialized.ApplyModifiedProperties();
            }
            Undo.FlushUndoRecordObjects();
            Assert.That(ResourceEditorOperations.ResolveRecipe(recipe).AppContentCompatibilityId, Is.EqualTo("changed-app"));
            Assert.That(input.AppContentCompatibilityId, Is.EqualTo("recipe-v1"));
            Assert.That(input.ForceRebuild, Is.False);
            var dirty = ResourceEditorOperations.ExecuteRecipe(recipe);
            Assert.That(dirty.BuildError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(dirty.Build, Is.Null);
            Undo.PerformUndo();
            Assert.That(recipe.appContentCompatibilityId, Is.EqualTo("recipe-v1"));
            Assert.That(recipe.forceRebuild, Is.False);
            ResourceEditorOperations.SaveRecipe(recipe);
        }

        [TestCase("missing-project")]
        [TestCase("invalid-app")]
        [TestCase("invalid-action")]
        [TestCase("generation-without-publish")]
        [TestCase("invalid-generation-path")]
        [TestCase("unsupported-target")]
        public void InvalidRecipesRejectWithoutProjectWritesOrWorkspaceOwnership(string mode)
        {
            using var fixture = new RecipeFixture();
            var recipe = fixture.Recipe;
            var source = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath));
            switch (mode)
            {
                case "missing-project":
                    recipe.project = null;
                    break;
                case "invalid-app":
                    recipe.appContentCompatibilityId = "../not-an-id";
                    break;
                case "invalid-action":
                    recipe.action = (ResourceBuildAction)999;
                    break;
                case "generation-without-publish":
                    recipe.generateReferences = true;
                    break;
                case "invalid-generation-path":
                    recipe.action = ResourceBuildAction.BuildAndPublish;
                    recipe.generateReferences = true;
                    recipe.referenceDirectory = "Assets/../Scenes";
                    break;
                case "unsupported-target":
                    recipe.target = BuildTarget.Android;
                    break;
            }
            EditorUtility.SetDirty(recipe);
            ResourceEditorOperations.SaveRecipe(recipe);
            var result = ResourceEditorOperations.ExecuteRecipe(recipe);
            Assert.That(result.BuildError, Is.Not.Null);
            Assert.That(result.Build, Is.Null);
            Assert.That(result.Release, Is.Null);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.WorkspaceRoot)), Is.False);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ReleaseRoot)), Is.False);
            CollectionAssert.AreEqual(source, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath)));
        }

        [Test]
        public void CancellationAndMissingExternalKeyDoNotCreateOutputs()
        {
            using var fixture = new RecipeFixture();
            var cancelled = ResourceEditorOperations.ExecuteRecipe(fixture.Recipe, new CancellationToken(true));
            Assert.That(cancelled.BuildError, Is.InstanceOf<OperationCanceledException>());
            var project = fixture.Project;
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "recipe-test";
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            fixture.TrackWorkspace();
            var source = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath));
            var result = ResourceEditorOperations.ExecuteRecipe(fixture.Recipe, provider: new RecipeKeys { Missing = true });
            Assert.That(result.BuildError, Is.TypeOf<ResourceException>());
            Assert.That(((ResourceException)result.BuildError).Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            Assert.That(result.Build, Is.Null);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ReleaseRoot)), Is.False);
            Assert.That(fixture.WorkspaceRoots.All(x => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(x))), Is.True);
            CollectionAssert.AreEqual(source, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath)));
        }

        [Test]
        public void InspectorAndWindowlessExampleUseTheSameServiceAndPreserveLegacyNoneIdentity()
        {
            using var fixture = new RecipeFixture();
            var source = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath));
            var input = ResourceEditorOperations.ResolveRecipe(fixture.Recipe);
            var window = ScriptableObject.CreateInstance<ResServiceEditorWindow>();
            try
            {
                window.SetProject(fixture.Project);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
            var method = typeof(ResourceBuildRecipeEditor).GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Static);
            var ui = (ResourceEditorBuildReport)method.Invoke(null, new object[] { fixture.Recipe });
            Assert.That(ui.BuildError, Is.Null, ui.BuildError?.ToString());
            Assert.That(ui.Release, Is.Null, "BuildOnly must not publish.");
            Assert.That(ui.Invocation.ToString(), Is.EqualTo(input.ToString()));
            var legacy = ResourceEditorOperations.BuildOnly(fixture.Project, "recipe-v1");
            Assert.That(legacy.BuildError, Is.Null, legacy.BuildError?.ToString());
            CollectionAssert.AreEqual(ResourceCatalogCodec.Write(ResourceCatalogBuilder.Create(ui.Build, "recipe-v1")),
                ResourceCatalogCodec.Write(ResourceCatalogBuilder.Create(legacy.Build, "recipe-v1")));
            var recipe = fixture.Recipe;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            EditorUtility.SetDirty(recipe);
            ResourceEditorOperations.SaveRecipe(recipe);
#if YL_TEST_RESOURCE_STARTUP_SAMPLE
            var sample = Type.GetType("YLFramework.Samples.Resource.Editor.ResourceBuildRecipeExample, YLFramework.ResourceStartup.Sample.Editor", true);
            var api = sample.GetMethod("Build");
            var report = (ResourceEditorBuildReport)api.Invoke(null, new object[] { fixture.RecipePath, null });
            Assert.That(report.BuildError, Is.Null, report.BuildError?.ToString());
            Assert.That(report.PublicationError, Is.Null, report.PublicationError?.ToString());
            Assert.That(report.Release, Is.Not.Null);
            Assert.That(report.Release.Catalog.AppContentCompatibilityId, Is.EqualTo("recipe-v1"));
#else
            var report = ResourceEditorOperations.ExecuteRecipe(recipe);
            Assert.That(report.BuildError, Is.Null);
            Assert.That(report.PublicationError, Is.Null);
            Assert.That(report.Release, Is.Not.Null);
#endif
            CollectionAssert.AreEqual(source, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath)));
        }

        [Test]
        public void RecipeRejectsEffectiveProjectPolicyChangesFromAReentrantKeyProvider()
        {
            using var fixture = new RecipeFixture();
            var project = fixture.Project;
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "recipe-test";
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            fixture.TrackWorkspace();
            var changed = false;
            var keys = new RecipeKeys
            {
                OnCopy = () =>
                {
                    if (changed)
                    {
                        return;
                    }

                    changed = true;
                    var current = fixture.Project;
                    current.compression = ResourceCompression.Uncompressed;
                    EditorUtility.SetDirty(current);
                    ResourceEditorOperations.SaveProject(current);
                    fixture.TrackWorkspace();
                }
            };
            var report = ResourceEditorOperations.ExecuteRecipe(fixture.Recipe, provider: keys);
            Assert.That(changed, Is.True);
            Assert.That(report.BuildError, Is.InstanceOf<InvalidOperationException>(), "A recipe must not report frozen Lz4 while building a reentrant Uncompressed definition.");
            Assert.That(report.Build, Is.Null);
            Assert.That(fixture.WorkspaceRoots.All(x => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(x))), Is.True);
            Assert.That(fixture.Project.compression, Is.EqualTo(ResourceCompression.Uncompressed), "Do not undo application-owned edits to restore a snapshot.");
        }

        [Test]
        public void EncryptedRecipeFreezesExecutionOptionsAndFailuresKeepTheOldRelease()
        {
            using var fixture = new RecipeFixture();
            var project = fixture.Project;
            project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
            project.contentKeyId = "recipe-test";
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            fixture.TrackWorkspace();
            var recipe = fixture.Recipe;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            EditorUtility.SetDirty(recipe);
            ResourceEditorOperations.SaveRecipe(recipe);
            var source = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath));
            var keys = new RecipeKeys
            {
                OnCopy = () =>
                {
                    var current = fixture.Recipe;
                    current.appContentCompatibilityId = "future-app";
                    current.action = ResourceBuildAction.BuildOnly;
                    EditorUtility.SetDirty(current);
                }
            };
            var report = ResourceEditorOperations.ExecuteRecipe(recipe, provider: keys);
            Assert.That(report.BuildError, Is.Null, report.BuildError?.ToString());
            Assert.That(report.PublicationError, Is.Null, report.PublicationError?.ToString());
            Assert.That(report.Release.Catalog.AppContentCompatibilityId, Is.EqualTo("recipe-v1"));
            Assert.That(report.Invocation.Action, Is.EqualTo(ResourceBuildAction.BuildAndPublish));
            Assert.That(report.Build.ContentEncryption, Is.EqualTo(ResourceContentEncryption.Aes256GcmHkdfSegmentedV1));
            Assert.That(keys.Copies, Is.GreaterThan(0));
            var before = ResourceReleaseFixture.Snapshot(fixture.ReleaseRoot);
            recipe = fixture.Recipe;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.appContentCompatibilityId = "recipe-v1";
            EditorUtility.SetDirty(recipe);
            ResourceEditorOperations.SaveRecipe(recipe);
            var failed = ResourceEditorOperations.ExecuteRecipe(recipe, provider: new RecipeKeys { Missing = true });
            Assert.That(failed.BuildError, Is.Not.Null);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(fixture.ReleaseRoot));
            CollectionAssert.AreEqual(source, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.ProjectPath)));
            var cancelled = ResourceEditorOperations.ExecuteRecipe(recipe, new CancellationToken(true), keys);
            Assert.That(cancelled.BuildError, Is.InstanceOf<OperationCanceledException>());
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(fixture.ReleaseRoot));
        }

        private sealed class RecipeKeys : IResourceContentKeyProvider
        {
            internal bool Missing;
            internal int Copies;
            internal Action OnCopy;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                Copies++;
                OnCopy?.Invoke();
                if (Missing)
                {
                    return false;
                }
                // 运行时生成的自有非生产夹具内容，不是发布密钥或配置字段。
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)(i + 101);
                }

                return keyId == "recipe-test";
            }
        }

        private sealed class RecipeFixture : IDisposable
        {
            internal readonly string Folder;
            internal string ProjectPath => Folder + "/Project.asset";
            internal string RecipePath => Folder + "/Recipe.asset";
            internal ResourceProject Project => AssetDatabase.LoadAssetAtPath<ResourceProject>(ProjectPath);
            internal ResourceBuildRecipe Recipe => AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(RecipePath);
            internal readonly HashSet<string> WorkspaceRoots = new();
            internal string WorkspaceRoot;
            internal readonly string ReleaseRoot;

            internal RecipeFixture()
            {
                const string Parent = "Assets/Tests/YLFramework/Fixtures";
                if (!AssetDatabase.IsValidFolder(Parent))
                {
                    AssetDatabase.CreateFolder("Assets/Tests/YLFramework", "Fixtures");
                }

                Folder = Parent + "/Recipe-" + Guid.NewGuid().ToString("N");
                Assert.That(AssetDatabase.CreateFolder(Parent, Path.GetFileName(Folder)), Is.Not.Empty);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Folder + "/entry.txt"), "owned recipe fixture");
                AssetDatabase.ImportAsset(Folder + "/entry.txt", ImportAssetOptions.ForceSynchronousImport);
                var project = ScriptableObject.CreateInstance<ResourceProject>();
                project.projectId = "recipe-" + Guid.NewGuid().ToString("N");
                project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(Folder) });
                project.groups.Add(new ResourceGroup
                {
                    id = "text",
                    selections = new List<ResourceSelection> {
                    new() { guid = AssetDatabase.AssetPathToGUID(Folder + "/entry.txt"), address = "entry" } }
                });
                AssetDatabase.CreateAsset(project, ProjectPath);
                var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
                recipe.project = project;
                recipe.appContentCompatibilityId = "recipe-v1";
                AssetDatabase.CreateAsset(recipe, RecipePath);
                ReleaseRoot = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("AssetBundles/YLFramework/" + project.projectId + "/" + recipe.target));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ReleaseRoot)), Is.False);
                TrackWorkspace();
            }

            internal void TrackWorkspace()
            {
                WorkspaceRoot = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(Project, EditorUserBuildSettings.activeBuildTarget));
                if (WorkspaceRoots.Add(WorkspaceRoot))
                {
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(WorkspaceRoot)), Is.False);
                }
            }

            public void Dispose()
            {
                foreach (var root in WorkspaceRoots)
                {
                    if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
                    {
                        Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
                    }
                }

                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ReleaseRoot)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(ReleaseRoot), true);
                }

                var projectRoot = Path.GetDirectoryName(ReleaseRoot);
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(projectRoot)) && !Directory.EnumerateFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(projectRoot)).Any())
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(projectRoot));
                }

                AssetDatabase.DeleteAsset(Folder);
            }
        }
    }
}
