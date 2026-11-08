using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Integrations.HybridCLR.Editor;
using YLFramework.Samples.HybridCLR.Editor;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRToolTests
    {
        [Test]
        public void CreatingAProfileCreatesSavedResourceBindingsWithoutChangingGlobalSettings()
        {
            var root = NewToolRoot();
            var rootGuid = AssetDatabase.AssetPathToGUID(root);
            var settings = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")) ? File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")) : null;
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                Assert.That(profile.recipe, Is.Not.Null);
                Assert.That(profile.recipe.project, Is.Not.Null);
                Assert.That(profile.recipe.action, Is.EqualTo(ResourceBuildAction.BuildAndPublish));
                Assert.That(profile.recipe.target, Is.EqualTo(BuildTarget.StandaloneWindows64));
                Assert.That(profile.recipe.project.sourceDirectories.Count, Is.EqualTo(1));
                var selection = profile.recipe.project.groups.Single().selections.Single();
                Assert.That(AssetDatabase.GUIDToAssetPath(selection.guid), Is.EqualTo(profile.generatedAssetDirectory + "/hybridclr"));
                Assert.That(selection.extensions, Is.EquivalentTo(new[] { ".bytes", ".json" }));
                Assert.That(EditorUtility.IsDirty(profile) || EditorUtility.IsDirty(profile.recipe) || EditorUtility.IsDirty(profile.recipe.project), Is.False);
                Assert.DoesNotThrow(() => HybridCLRBuildPipeline.ValidateInputPaths(profile));
                var paths = new[] { AssetDatabase.GetAssetPath(profile), AssetDatabase.GetAssetPath(profile.recipe), AssetDatabase.GetAssetPath(profile.recipe.project) };
                var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
                var bytes = paths.Select(File.ReadAllBytes).ToArray();
                var metas = paths.Select(path => File.ReadAllBytes(path + ".meta")).ToArray();
                var projectId = profile.recipe.project.projectId;
                var generated = profile.generatedAssetDirectory;
                var baseline = profile.baselineDirectory;
                var development = profile.development;
                var recipe = profile.recipe;
                var project = recipe.project;
                Resources.UnloadAsset(profile);
                Resources.UnloadAsset(recipe);
                Resources.UnloadAsset(project);
                Assert.That((bool)profile || (bool)recipe || (bool)project, Is.False);
                var restored = AssetDatabase.LoadAssetAtPath<HybridCLRBuildProfile>(paths[0]);
                Assert.That(restored.recipe.project.projectId, Is.EqualTo(projectId));
                Assert.That(restored.development, Is.EqualTo(development));
                Assert.That(restored.baseProfile, Is.Null);
                Assert.That(restored.hotUpdateAssemblies, Is.Empty);
                Assert.That(restored.generatedAssetDirectory, Is.EqualTo(generated));
                Assert.That(restored.baselineDirectory, Is.EqualTo(baseline));
                Assert.That(restored.recipe.appContentCompatibilityId, Is.EqualTo("base-v1"));
                Assert.That(restored.recipe.action, Is.EqualTo(ResourceBuildAction.BuildAndPublish));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(restored.recipe)), Is.EqualTo(guids[1]));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(restored.recipe.project)), Is.EqualTo(guids[2]));
                Assert.That(AssetDatabase.GUIDToAssetPath(restored.recipe.project.groups.Single().selections.Single().guid), Is.EqualTo(generated + "/hybridclr"));
                var output = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
                Assert.That(Directory.Exists(output), Is.True);
                var evidence = Path.Combine(output, "HybridCLRFormalProfileProducer");
                Assert.That(Directory.Exists(evidence), Is.False);
                Directory.CreateDirectory(evidence);
                for (var i = 0; i < paths.Length; i++)
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(paths[i]), Is.EqualTo(guids[i]));
                    CollectionAssert.AreEqual(bytes[i], File.ReadAllBytes(paths[i]));
                    CollectionAssert.AreEqual(metas[i], File.ReadAllBytes(paths[i] + ".meta"));
                    File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(paths[i])), bytes[i]);
                    File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(paths[i]) + ".meta"), metas[i]);
                    TestContext.WriteLine("Formal profile input: {0}; GUID={1}", paths[i], guids[i]);
                }
                Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")), Is.EqualTo(settings != null));
                if (settings != null)
                {
                    CollectionAssert.AreEqual(settings, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")));
                }
            }
            finally
            {
                Assert.That(AssetDatabase.AssetPathToGUID(root), Is.EqualTo(rootGuid));
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void CreatingAProfileCannotOverwriteAnExistingAsset()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var path = AssetDatabase.GetAssetPath(profile);
                var guid = AssetDatabase.AssetPathToGUID(path);
                var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                Assert.Throws<IOException>(() => ProfileTool("Create", path));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void GeneratedResourceMappingIsIdempotentPreservesOtherGroupsAndSupportsUndo()
        {
            var root = NewToolRoot();
            ResourceProject project = null;
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                project = profile.recipe.project;
                project.sourceDirectories.Clear();
                project.groups.Clear();
                project.groups.Add(new ResourceGroup { id = "user-content" });
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
                ProfileTool("MapGeneratedResources", profile);
                Assert.That(project.sourceDirectories.Count, Is.EqualTo(1));
                Assert.That(project.groups.Select(g => g.id), Does.Contain("user-content"));
                Assert.That(project.groups.Sum(g => g.selections.Count), Is.EqualTo(1));
                ProfileTool("MapGeneratedResources", profile);
                Assert.That(project.sourceDirectories.Count, Is.EqualTo(1));
                Assert.That(project.groups.Sum(g => g.selections.Count), Is.EqualTo(1));
                Undo.PerformUndo();
                Assert.That(project.sourceDirectories, Is.Empty);
                Assert.That(project.groups.Select(g => g.id), Is.EqualTo(new[] { "user-content" }));
            }
            finally
            {
                if (project)
                {
                    Undo.ClearUndo(project);
                }
                AssetDatabase.DeleteAsset(root);
            }
        }

        private static string NewToolRoot()
        {
            var name = "HybridCLRUI-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", name);
            return "Assets/Tests/YLFramework/Fixtures/" + name;
        }

        [Test]
        public void MappingANewGenerationDirectoryRejectsOldCodeMappingsWithoutChangingTheProject()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var project = profile.recipe.project;
                var before = EditorJsonUtility.ToJson(project);
                profile.generatedAssetDirectory = root + "/OtherGenerated";
                Assert.Throws<InvalidDataException>(() => ProfileTool("MapGeneratedResources", profile));
                Assert.That(EditorJsonUtility.ToJson(project), Is.EqualTo(before));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void ConfigurationInspectionReportsStaleCodeMappingsEvenWhenTheNewMappingExists()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                AssetDatabase.CreateFolder(root, "OtherGenerated");
                AssetDatabase.CreateFolder(root + "/OtherGenerated", "hybridclr");
                profile.generatedAssetDirectory = root + "/OtherGenerated";
                profile.recipe.project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(profile.generatedAssetDirectory) });
                profile.recipe.project.groups.Add(new ResourceGroup
                {
                    id = "new-code",
                    selections = new System.Collections.Generic.List<ResourceSelection>
                    { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(profile.generatedAssetDirectory + "/hybridclr"), extensions = new[] { ".bytes", ".json" } } }
                });
                var result = (HybridCLRProfileInspection)ProfileTool("Inspect", profile);
                Assert.That(result.Errors.Any(e => e.Contains("代码映射") && e.Contains("其他目录")), Is.True,
                    "Preflight must diagnose the old canonical code mapping, not only notice that the new mapping exists.");
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void MappingAvoidsUserGroupIdsCaseInsensitivelyIncludingSuffixes()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var project = profile.recipe.project;
                project.sourceDirectories.Clear();
                project.groups.Clear();
                project.groups.Add(new ResourceGroup { id = "HybridCLR-Code" });
                project.groups.Add(new ResourceGroup { id = "HYBRIDCLR-CODE-2" });
                ProfileTool("MapGeneratedResources", profile);
                Assert.That(project.groups.Select(g => g.id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(project.groups.Count));
                Assert.That(project.groups.Select(g => g.id), Does.Contain("HybridCLR-Code").And.Contain("HYBRIDCLR-CODE-2"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [TestCase("aliases")]
        [TestCase("duplicate")]
        public void InvalidCanonicalCodeSelectionsAreDiagnosedWithoutRewritingUserConfiguration(string invalid)
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var project = profile.recipe.project;
                var selection = project.groups.Single().selections.Single();
                if (invalid == "aliases")
                {
                    selection.aliases = new[] { "not-valid-for-a-folder" };
                }
                else
                {
                    project.groups.Add(new ResourceGroup
                    {
                        id = "duplicate-selection",
                        selections = new System.Collections.Generic.List<ResourceSelection>
                    { new ResourceSelection { guid = selection.guid, extensions = new[] { ".bytes", ".json" } } }
                    });
                }

                var before = EditorJsonUtility.ToJson(project);
                Assert.Throws<InvalidDataException>(() => ProfileTool("MapGeneratedResources", profile));
                Assert.That(EditorJsonUtility.ToJson(project), Is.EqualTo(before));
                var result = (HybridCLRProfileInspection)ProfileTool("Inspect", profile);
                Assert.That(result.Errors.Any(e => e.Contains("代码目录选择")), Is.True, "Configuration inspection must explain invalid canonical code selections.");
                Assert.That(EditorJsonUtility.ToJson(project), Is.EqualTo(before));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void DeletedCodeFolderIsNotAcceptedThroughItsRecentlyDeletedGuid()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var code = profile.generatedAssetDirectory + "/hybridclr";
                Assert.That(AssetDatabase.DeleteAsset(code), Is.True);
                Assert.That(AssetDatabase.IsValidFolder(code), Is.False);
                var result = (HybridCLRProfileInspection)ProfileTool("Inspect", profile);
                Assert.That(result.Errors.Any(e => e.Contains("代码来源目录")), Is.True,
                    "A historical GUID is not evidence that the selected folder still exists.");
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        private static object ProfileTool(string method, params object[] arguments)
        {
            var type = typeof(HybridCLRBuildProfile).Assembly.GetType("YLFramework.Integrations.HybridCLR.Editor.HybridCLRProfileTools");
            Assert.That(type, Is.Not.Null, "The window needs an explicit, testable profile-creation and resource-binding action.");
            var action = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(action, Is.Not.Null);
            try
            {
                return action.Invoke(null, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        [Test]
        public void IsolatedSampleAssetsAreExplicitAndDoNotConfigureTheOfficialSettingsOrPlayer()
        {
            var root = "Assets/Tests/YLFramework/Fixtures/HybridCLRTool-" + Guid.NewGuid().ToString("N");
            var settings = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")) ? File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")) : null;
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var development = EditorUserBuildSettings.development;
            try
            {
                var profile = HybridCLRExampleAssets.Create(root, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Build/HybridCLR/ToolTest-" + Guid.NewGuid().ToString("N"))));
                Assert.That(profile.recipe.action, Is.EqualTo(ResourceBuildAction.BuildAndPublish));
                Assert.That(profile.recipe.project.groups.Count, Is.EqualTo(3));
                Assert.That(profile.recipe.project.sourceDirectories.Count, Is.EqualTo(2));
                Assert.That(profile.recipe.project.groups.Single(g => g.id == "configuration").selections.Single().address, Is.EqualTo("sample/message.json"));
                Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(profile.recipe.project), "The empty code group must not be buildable until generation succeeds.");
                Assert.That(EditorUtility.IsDirty(profile) || EditorUtility.IsDirty(profile.recipe) || EditorUtility.IsDirty(profile.recipe.project), Is.False);
                Assert.Throws<ArgumentException>(() => HybridCLRExampleAssets.Create(root, profile.baselineDirectory));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")), Is.EqualTo(settings != null));
                if (settings != null)
                {
                    CollectionAssert.AreEqual(settings, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")));
                }

                Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
                Assert.That(EditorUserBuildSettings.development, Is.EqualTo(development));
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(root))
                {
                    AssetDatabase.DeleteAsset(root);
                }
            }
        }

        [UnityTest]
        public IEnumerator AnExplicitPlayerLoopUpdatePumpsTheEditorContextWithinTheOriginalBudget()
        {
            var context = System.Threading.SynchronizationContext.Current;
            Assert.That(context, Is.Not.Null);
            var pumped = 0;
            context.Post(_ => pumped++, null);
            var startedFrame = Time.frameCount;
            for (var i = 0; i < 240 && pumped == 0; i++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }

            Assert.That(pumped, Is.EqualTo(1), $"context={context.GetType().FullName}; frameDelta={Time.frameCount - startedFrame}.");
        }

        [UnityTest]
        public IEnumerator TheSampleSupportExercisesAnActualAsyncContinuation()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "YLFramework.HybridCLR.Sample.Support");
            var method = assembly.GetType("YLFramework.Samples.HybridCLR.HotUpdate.VersionInfo", true).GetMethod("DescribeAsync");
            Assert.That(method, Is.Not.Null);
            var context = System.Threading.SynchronizationContext.Current?.GetType().FullName ?? "none";
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var startedFrame = Time.frameCount;
            var startedEditorTime = EditorApplication.timeSinceStartup;
            var task = (System.Threading.Tasks.Task<string>)method.Invoke(null, null);
            for (var i = 0; i < 240 && !task.IsCompleted; i++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True,
                $"Async status={task.Status}; context={context}; elapsedMs={elapsed.Elapsed.TotalMilliseconds:F3}; editorSeconds={EditorApplication.timeSinceStartup - startedEditorTime:F6}; frameDelta={Time.frameCount - startedFrame}.");
            Assert.That(task.IsFaulted, Is.False);
            Assert.That(task.Result, Is.EqualTo("v1-async"));
        }

        [Test]
        public void SamplePrefabHasARealHotScriptWithoutDirtyingTheOpenScene()
        {
            var root = "Assets/Tests/YLFramework/Fixtures/HybridCLRPrefab-" + Guid.NewGuid().ToString("N");
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            try
            {
                var profile = HybridCLRExampleAssets.Create(root, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Build/HybridCLR/PrefabTest-" + Guid.NewGuid().ToString("N"))));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Data/HotView.prefab");
                Assert.That(prefab, Is.Not.Null);
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero);
                Assert.That(prefab.GetComponents<Component>().Any(c => c && c.GetType().FullName == "YLFramework.Samples.HybridCLR.HotUpdate.ExampleBehaviour"), Is.True);
                Assert.That(profile.recipe.project.groups.Single(g => g.id == "prefab").selections.Single().address, Is.EqualTo("sample/hot-view"));
                Assert.That(scene.isDirty, Is.EqualTo(wasDirty));
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(root))
                {
                    AssetDatabase.DeleteAsset(root);
                }
            }
        }

        [UnityTest]
        public IEnumerator EveryNavigationTabCanBeClickedAndKeepsARealSelectedSkin()
        {
            var window = ScriptableObject.CreateInstance<HybridCLRWindow>();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                window.position = new Rect(180, 100, 900, 600);
                window.ShowUtility();
                window.Focus();
                yield return null;
                var style = (GUIStyle)typeof(HybridCLRWindow).GetField("_navigationStyle", flags).GetValue(window);
                Assert.That(EditorGUIUtility.GetBuiltinSkin(EditorSkin.Inspector).FindStyle(style.name), Is.Not.Null,
                    "Renaming a native Editor style loses its skin-based selected-state painting.");
                foreach (var target in new[] { 1, 2, 0 })
                {
                    window.Repaint();
                    yield return null;
                    var area = NavigationArea(window);
                    var point = new Vector2(area.x + area.width * (target + .5f) / 3, area.center.y);
                    window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = point });
                    window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = point });
                    yield return null;
                    Assert.That(typeof(HybridCLRWindow).GetField("page", flags).GetValue(window), Is.EqualTo(target), "Each tab must be reachable through actual GUI mouse events, including returning to the first tab.");
                }
            }
            finally
            {
                window.Close();
            }
        }

        private static Rect NavigationArea(HybridCLRWindow window)
        {
            var field = typeof(HybridCLRWindow).GetField("_navigationBounds", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var area = (Rect)field.GetValue(window);
            Assert.That(area.height, Is.EqualTo(30));
            Assert.That(area.width, Is.GreaterThan(window.position.width * .8f));
            return area;
        }

        [UnityTest]
        public IEnumerator NavigationButtonsKeepUniformHeightAndTypography()
        {
            var window = ScriptableObject.CreateInstance<HybridCLRWindow>();
            try
            {
                window.Show();
                yield return null;
                var style = (GUIStyle)typeof(HybridCLRWindow).GetField("_navigationStyle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                Assert.That(style.fixedHeight, Is.EqualTo(30));
                Assert.That(style.fixedWidth, Is.Zero);
                Assert.That(style.alignment, Is.EqualTo(TextAnchor.MiddleCenter));
                Assert.That(style.fontSize, Is.EqualTo(12));
                Assert.That(style.fontStyle, Is.EqualTo(FontStyle.Bold));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator OpeningTheWindowDoesNotGenerateInstallOrSaveAnyAssets()
        {
            var settings = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")) ? File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")) : null;
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var window = ScriptableObject.CreateInstance<HybridCLRWindow>();
            try
            {
                window.Show();
                yield return null;
                Assert.That(EditorUtility.scriptCompilationFailed, Is.False);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")), Is.EqualTo(settings != null));
                if (settings != null)
                {
                    CollectionAssert.AreEqual(settings, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/HybridCLRSettings.asset")));
                }

                Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
            }
            finally
            {
                window.Close();
            }
        }
    }
}
