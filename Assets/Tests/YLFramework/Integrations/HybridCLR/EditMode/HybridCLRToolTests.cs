using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Integrations.HybridCLR;
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
            var previousDefaultSettings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                Assert.That(profile.schemaVersion, Is.EqualTo(2));
                Assert.That(profile.recipe, Is.Null);
                Assert.That(profile.addressableSettings, Is.SameAs(AddressableAssetSettingsDefaultObject.GetSettings(false)));
                Assert.That(profile.codeGroup, Is.Null, "The tool must not silently choose an official code group.");
                Assert.That(profile.target, Is.EqualTo(BuildTarget.StandaloneWindows64));
                Assert.That(EditorUtility.IsDirty(profile), Is.False);
                Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidateInputPaths(profile));
                BindOfficialSettings(root, profile);
                Assert.DoesNotThrow(() => HybridCLRBuildPipeline.ValidateInputPaths(profile));
                var paths = new[] { AssetDatabase.GetAssetPath(profile), AssetDatabase.GetAssetPath(profile.addressableSettings), AssetDatabase.GetAssetPath(profile.codeGroup) };
                var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
                var bytes = paths.Select(File.ReadAllBytes).ToArray();
                var metas = paths.Select(path => File.ReadAllBytes(path + ".meta")).ToArray();
                var projectId = profile.projectId;
                var generated = profile.generatedAssetDirectory;
                var baseline = profile.baselineDirectory;
                var development = profile.development;
                var official = profile.addressableSettings;
                var code = profile.codeGroup;
                Resources.UnloadAsset(profile);
                Resources.UnloadAsset(code);
                Resources.UnloadAsset(official);
                Assert.That((bool)profile || (bool)official || (bool)code, Is.False);
                var restored = AssetDatabase.LoadAssetAtPath<HybridCLRBuildProfile>(paths[0]);
                Assert.That(restored.schemaVersion, Is.EqualTo(2));
                Assert.That(restored.recipe, Is.Null);
                Assert.That(restored.projectId, Is.EqualTo(projectId));
                Assert.That(restored.development, Is.EqualTo(development));
                Assert.That(restored.baseProfile, Is.Null);
                Assert.That(restored.hotUpdateAssemblies, Is.Empty);
                Assert.That(restored.generatedAssetDirectory, Is.EqualTo(generated));
                Assert.That(restored.baselineDirectory, Is.EqualTo(baseline));
                Assert.That(restored.appContentCompatibilityId, Is.EqualTo("aa-base-v2"));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(restored.addressableSettings)), Is.EqualTo(guids[1]));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(restored.codeGroup)), Is.EqualTo(guids[2]));
                Assert.That(restored.codeGroup.Settings, Is.SameAs(restored.addressableSettings));
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
                Assert.That(AddressableAssetSettingsDefaultObject.GetSettings(false), Is.SameAs(previousDefaultSettings));
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
            AddressableAssetSettings official = null;
            AddressableAssetGroup code = null;
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                official = BindOfficialSettings(root, profile);
                code = profile.codeGroup;
                var manifest = CommitToolPayload(profile);
                var foreignPath = root + "/user.txt";
                File.WriteAllText(foreignPath, "unrelated user content");
                AssetDatabase.ImportAsset(foreignPath, ImportAssetOptions.ForceSynchronousImport);
                var foreignGroup = NewOfficialGroup(official, "user-content");
                var foreign = official.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(foreignPath), foreignGroup);
                foreign.SetAddress("user/content");
                SaveOfficialSettings(official);
                var foreignBytes = File.ReadAllBytes(AssetDatabase.GetAssetPath(foreignGroup));
                Undo.IncrementCurrentGroup();
                var undo = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Register exact code entries");
                ProfileTool("MapGeneratedResources", profile);
                Assert.That(code.entries.Count, Is.EqualTo(2));
                Assert.That(ResourceAddressablesAuthoring.FindUniqueEntry(official, HybridCLRManifest.ResourceKey(profile.projectId, HybridCLRManifest.ManifestPath)).parentGroup, Is.SameAs(code));
                Assert.That(ResourceAddressablesAuthoring.FindUniqueEntry(official, HybridCLRManifest.ResourceKey(profile.projectId, manifest.HotUpdateAssemblies.Single().Address)).parentGroup, Is.SameAs(code));
                ProfileTool("MapGeneratedResources", profile);
                Assert.That(code.entries.Count, Is.EqualTo(2));
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undo);
                Undo.PerformUndo();
                Assert.That(code.entries, Is.Empty);
                Assert.That(foreignGroup.entries.Single().address, Is.EqualTo("user/content"));
                CollectionAssert.AreEqual(foreignBytes, File.ReadAllBytes(AssetDatabase.GetAssetPath(foreignGroup)));
            }
            finally
            {
                if (official)
                    Undo.ClearUndo(official);
                if (code)
                    Undo.ClearUndo(code);
                AssetDatabase.DeleteAsset(root);
            }
        }

        private static string NewToolRoot()
        {
            var name = "HybridCLRUI-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", name);
            return "Assets/Tests/YLFramework/Fixtures/" + name;
        }

        private static AddressableAssetSettings CreateOfficialSettings(string root, out AddressableAssetGroup code, out AddressableAssetGroup sample)
        {
            var settings = AddressableAssetSettings.Create(root, "OfficialSettings", true, true);
            settings.DisableCatalogUpdateOnStartup = true;
            settings.BuildRemoteCatalog = true;
            code = settings.DefaultGroup;
            sample = NewOfficialGroup(settings, "Sample");
            foreach (var group in new[] { code, sample })
            {
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
                schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
                group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            }
            SaveOfficialSettings(settings);
            return settings;
        }

        private static AddressableAssetGroup NewOfficialGroup(AddressableAssetSettings settings, string name) =>
            settings.CreateGroup(name, false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));

        private static AddressableAssetSettings BindOfficialSettings(string root, HybridCLRBuildProfile profile)
        {
            var settings = CreateOfficialSettings(root, out var code, out _);
            profile.addressableSettings = settings;
            profile.codeGroup = code;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            return settings;
        }

        private static void SaveOfficialSettings(AddressableAssetSettings settings)
        {
            foreach (var group in settings.groups)
            {
                if (!group)
                    continue;
                foreach (var schema in group.Schemas)
                    AssetDatabase.SaveAssetIfDirty(schema);
                AssetDatabase.SaveAssetIfDirty(group);
            }
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        private static HybridCLRManifest CommitToolPayload(HybridCLRBuildProfile profile)
        {
            var assembly = HybridCLRAssemblyAnalysis.Read(typeof(HybridCLRManifest).Assembly.Location);
            var manifest = new HybridCLRManifest
            {
                ProjectId = profile.projectId,
                AppContentCompatibilityId = profile.appContentCompatibilityId,
                UnityVersion = Application.unityVersion,
                Development = profile.development,
                HotUpdateAssemblies = new[] { new HybridCLRPayload
                {
                    Name = assembly.Name, Identity = assembly.Identity, Mvid = assembly.Mvid,
                    Length = assembly.Length, Sha256 = assembly.Sha256,
                    Address = HybridCLRManifest.PayloadAddress(assembly.Name, false), References = assembly.References
                } }
            };
            manifest.Seal();
            HybridCLRBuildPipeline.CommitPayloads(profile, manifest, new HybridCLRAssemblySet
            {
                HotUpdate = new[] { assembly },
                Aot = Array.Empty<HybridCLRAssemblyRecord>()
            });
            return manifest;
        }

        [Test]
        public void MappingANewGenerationDirectoryRejectsOldCodeMappingsWithoutChangingTheProject()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var official = BindOfficialSettings(root, profile);
                CommitToolPayload(profile);
                ProfileTool("MapGeneratedResources", profile);
                SaveOfficialSettings(official);
                var before = EditorJsonUtility.ToJson(official);
                var code = EditorJsonUtility.ToJson(profile.codeGroup);
                profile.generatedAssetDirectory = root + "/OtherGenerated";
                CommitToolPayload(profile);
                Assert.Throws<InvalidDataException>(() => ProfileTool("MapGeneratedResources", profile));
                Assert.That(EditorJsonUtility.ToJson(official), Is.EqualTo(before));
                Assert.That(EditorJsonUtility.ToJson(profile.codeGroup), Is.EqualTo(code));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void ConfigurationInspectionReportsAnInvalidOfficialGroupWithoutRewritingUserInputs()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var official = BindOfficialSettings(root, profile);
                profile.codeGroup = null;
                var before = EditorJsonUtility.ToJson(profile);
                var settingsBefore = File.ReadAllBytes(AssetDatabase.GetAssetPath(official));
                var result = (HybridCLRProfileInspection)ProfileTool("Inspect", profile);
                Assert.That(result.Errors.Any(e => e.Contains("official Addressables settings") && e.Contains("code group")), Is.True);
                Assert.That(EditorJsonUtility.ToJson(profile), Is.EqualTo(before));
                CollectionAssert.AreEqual(settingsBefore, File.ReadAllBytes(AssetDatabase.GetAssetPath(official)));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [Test]
        public void MappingUsesTheExplicitOfficialGroupWithoutClaimingSimilarlyNamedUserGroups()
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var official = BindOfficialSettings(root, profile);
                var first = NewOfficialGroup(official, "HybridCLR-Code");
                var second = NewOfficialGroup(official, "HYBRIDCLR-CODE-2");
                SaveOfficialSettings(official);
                var firstBytes = File.ReadAllBytes(AssetDatabase.GetAssetPath(first));
                var secondBytes = File.ReadAllBytes(AssetDatabase.GetAssetPath(second));
                var groups = official.groups.Select(AssetDatabase.GetAssetPath).ToArray();
                CommitToolPayload(profile);
                ProfileTool("MapGeneratedResources", profile);
                Assert.That(official.groups.Select(AssetDatabase.GetAssetPath), Is.EqualTo(groups));
                Assert.That(profile.codeGroup.entries.Count, Is.EqualTo(2));
                Assert.That(first.entries, Is.Empty);
                Assert.That(second.entries, Is.Empty);
                CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(AssetDatabase.GetAssetPath(first)));
                CollectionAssert.AreEqual(secondBytes, File.ReadAllBytes(AssetDatabase.GetAssetPath(second)));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
            }
        }

        [TestCase("label-collision")]
        [TestCase("address-collision")]
        public void InvalidCanonicalCodeSelectionsAreDiagnosedWithoutRewritingUserConfiguration(string invalid)
        {
            var root = NewToolRoot();
            try
            {
                var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Game.asset");
                var official = BindOfficialSettings(root, profile);
                var manifest = CommitToolPayload(profile);
                var path = root + "/foreign.txt";
                File.WriteAllText(path, "must remain unchanged");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var foreignGroup = NewOfficialGroup(official, "foreign");
                var foreign = official.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), foreignGroup);
                var key = HybridCLRManifest.ResourceKey(profile.projectId, HybridCLRManifest.ManifestPath);
                if (invalid == "label-collision")
                {
                    official.AddLabel(key);
                    foreign.SetAddress("foreign/content");
                    foreign.SetLabel(key, true);
                }
                else
                {
                    foreign.SetAddress(key);
                }

                SaveOfficialSettings(official);
                var before = EditorJsonUtility.ToJson(official);
                var groupBefore = EditorJsonUtility.ToJson(foreignGroup);
                Assert.Throws<InvalidDataException>(() => ProfileTool("MapGeneratedResources", profile));
                Assert.That(EditorJsonUtility.ToJson(official), Is.EqualTo(before));
                Assert.That(EditorJsonUtility.ToJson(foreignGroup), Is.EqualTo(groupBefore));
                Assert.That(official.FindAssetEntry(AssetDatabase.AssetPathToGUID(profile.generatedAssetDirectory + "/" + manifest.HotUpdateAssemblies.Single().Address)), Is.Null);
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
                BindOfficialSettings(root, profile);
                CommitToolPayload(profile);
                var code = profile.generatedAssetDirectory + "/hybridclr";
                Assert.That(AssetDatabase.DeleteAsset(code), Is.True);
                Assert.That(AssetDatabase.IsValidFolder(code), Is.False);
                var result = (HybridCLRProfileInspection)ProfileTool("Inspect", profile);
                Assert.That(result.Errors.Any(e => e.Contains("owned generated payload is missing")), Is.True,
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
            var officialRoot = NewToolRoot();
            try
            {
                var official = CreateOfficialSettings(officialRoot, out var code, out var sample);
                var profile = HybridCLRExampleAssets.Create(root, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Build/HybridCLR/ToolTest-" + Guid.NewGuid().ToString("N"))), official, code, sample);
                Assert.That(profile.schemaVersion, Is.EqualTo(2));
                Assert.That(profile.recipe, Is.Null);
                Assert.That(profile.addressableSettings, Is.SameAs(official));
                Assert.That(profile.codeGroup, Is.SameAs(code));
                Assert.That(profile.appContentCompatibilityId, Is.EqualTo("hybridclr-aa-base-v2"));
                Assert.That(ResourceAddressablesAuthoring.FindUniqueEntry(official, profile.projectId + "/sample/message.json").parentGroup, Is.SameAs(sample));
                Assert.That(ResourceAddressablesAuthoring.FindUniqueEntry(official, profile.projectId + "/sample/hot-view").parentGroup, Is.SameAs(sample));
                Assert.That(code.entries, Is.Empty);
                Assert.Throws<FileNotFoundException>(() => ProfileTool("MapGeneratedResources", profile), "The sample does not fabricate a generated code manifest.");
                Assert.That(EditorUtility.IsDirty(profile) || EditorUtility.IsDirty(official) || EditorUtility.IsDirty(sample), Is.False);
                Assert.Throws<ArgumentException>(() => HybridCLRExampleAssets.Create(root, profile.baselineDirectory, official, code, sample));
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
                AssetDatabase.DeleteAsset(officialRoot);
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
            var officialRoot = NewToolRoot();
            try
            {
                var official = CreateOfficialSettings(officialRoot, out var code, out var sample);
                var profile = HybridCLRExampleAssets.Create(root, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Build/HybridCLR/PrefabTest-" + Guid.NewGuid().ToString("N"))), official, code, sample);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Data/HotView.prefab");
                Assert.That(prefab, Is.Not.Null);
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab), Is.Zero);
                Assert.That(prefab.GetComponents<Component>().Any(c => c && c.GetType().FullName == "YLFramework.Samples.HybridCLR.HotUpdate.ExampleBehaviour"), Is.True);
                Assert.That(ResourceAddressablesAuthoring.FindUniqueEntry(official, profile.projectId + "/sample/hot-view").guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(root + "/Data/HotView.prefab")));
                Assert.That(ResourceAddressablesAuthoring.FindUniqueEntry(official, profile.projectId + "/sample/hot-view").parentGroup, Is.SameAs(sample));
                Assert.That(scene.isDirty, Is.EqualTo(wasDirty));
            }
            finally
            {
                AssetDatabase.DeleteAsset(officialRoot);
                if (AssetDatabase.IsValidFolder(root))
                {
                    AssetDatabase.DeleteAsset(root);
                }
            }
        }

        [UnityTest]
        public IEnumerator ReadOnlyInspectionCanBeClickedAndShowsErrorsWithoutRewritingInputs()
        {
            RequireOwnedGuiHost();
            var root = NewToolRoot();
            var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Profile.asset");
            var settings = BindOfficialSettings(root, profile);
            profile.codeGroup = null;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            var profileBytes = File.ReadAllBytes(AssetDatabase.GetAssetPath(profile));
            var settingsBytes = File.ReadAllBytes(AssetDatabase.GetAssetPath(settings));
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var window = ScriptableObject.CreateInstance<HybridCLRWindow>();
            Type debugger = null;
            try
            {
                SelectWindowProfile(window, profile);
                window.position = new Rect(80, 40, 950, 1100);
                window.ShowUtility();
                window.Focus();
                yield return null;
                debugger = BeginGuiCapture(window);
                window.Repaint();
                yield return null;
                ClickCurrentButton(window, debugger, "Inspect Inputs (Read Only)");
                yield return null;
                window.Repaint();
                yield return null;
                Assert.That(DrawnText(debugger).Any(text => text.Contains("official Addressables settings") && text.Contains("code group")), Is.True,
                    "The real inspection error must be drawn by the current workbench, not hidden in a private legacy tab.");
                CollectionAssert.AreEqual(profileBytes, File.ReadAllBytes(AssetDatabase.GetAssetPath(profile)));
                CollectionAssert.AreEqual(settingsBytes, File.ReadAllBytes(AssetDatabase.GetAssetPath(settings)));
                Assert.That(EditorUtility.IsDirty(profile) || EditorUtility.IsDirty(settings), Is.False);
                Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
                Assert.That(Directory.Exists(profile.generatedAssetDirectory), Is.False);
            }
            finally
            {
                if (debugger != null)
                    debugger.GetMethod("StopDebugging", StaticFlags).Invoke(null, null);
                window.Close();
                AssetDatabase.DeleteAsset(root);
            }
        }

        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static void RequireOwnedGuiHost()
        {
            Assert.That(File.Exists(Path.Combine(Path.GetFullPath(Application.dataPath + "/.."), ".yl-owner.json")), Is.True,
                "Interactive workbench tests run only in an owned test host.");
        }

        private static void SelectWindowProfile(HybridCLRWindow window, HybridCLRBuildProfile profile)
        {
            using var state = new SerializedObject(window);
            var field = state.FindProperty("profile");
            Assert.That(field, Is.Not.Null);
            field.objectReferenceValue = profile;
            state.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Type BeginGuiCapture(HybridCLRWindow window)
        {
            // Observe the actual Unity IMGUI draw stream; no fields or hooks are added to the product.
            var debugger = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIViewDebuggerHelper", true);
            var parent = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(parent, Is.Not.Null);
            debugger.GetMethod("DebugWindow", StaticFlags).Invoke(null, new[] { parent.GetValue(window) });
            return debugger;
        }

        private static object[] DrawInstructions(Type debugger)
        {
            var instruction = typeof(EditorWindow).Assembly.GetType("UnityEditor.IMGUIDrawInstruction", true);
            var list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(instruction));
            var method = debugger.GetMethod("GetDrawInstructions", StaticFlags);
            Assert.That(method, Is.Not.Null);
            var parameters = method.GetParameters();
            Assert.That(parameters.Length, Is.InRange(1, 2));
            method.Invoke(null, parameters.Length == 2 ? new object[] { list, false } : new object[] { list });
            return list.Cast<object>().ToArray();
        }

        private static string[] DrawnText(Type debugger) => DrawInstructions(debugger)
            .Select(item => (GUIContent)item.GetType().GetField("usedGUIContent").GetValue(item)).Where(content => content != null).Select(content => content.text ?? "").ToArray();

        private static void ClickCurrentButton(HybridCLRWindow window, Type debugger, string label)
        {
            var matches = DrawInstructions(debugger).Where(item => ((GUIContent)item.GetType().GetField("usedGUIContent").GetValue(item))?.text == label).ToArray();
            Assert.That(matches.Length, Is.EqualTo(1), "The current production button must be rendered exactly once: " + label);
            var area = (Rect)matches[0].GetType().GetField("rect").GetValue(matches[0]);
            Assert.That(area.width, Is.GreaterThan(0));
            Assert.That(area.height, Is.GreaterThan(0));
            window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = area.center });
            window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = area.center });
        }

        [UnityTest]
        public IEnumerator FormalPreflightButtonShowsTheRealFailedReportWithoutBuildingOrChangingSettings()
        {
            RequireOwnedGuiHost();
            var root = NewToolRoot();
            var profile = (HybridCLRBuildProfile)ProfileTool("Create", root + "/Profile.asset");
            BindOfficialSettings(root, profile);
            profile.schemaVersion = 1;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            var bytes = File.ReadAllBytes(AssetDatabase.GetAssetPath(profile));
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var window = ScriptableObject.CreateInstance<HybridCLRWindow>();
            Type debugger = null;
            try
            {
                SelectWindowProfile(window, profile);
                window.position = new Rect(80, 40, 950, 1100);
                window.ShowUtility();
                window.Focus();
                yield return null;
                debugger = BeginGuiCapture(window);
                window.Repaint();
                yield return null;
                ClickCurrentButton(window, debugger, "Validate Formal Build Preconditions");
                yield return null;
                var field = typeof(HybridCLRWindow).GetField("_report", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null);
                var report = (HybridCLRBuildReport)field.GetValue(window);
                Assert.That(report, Is.Not.Null);
                Assert.That(report.HasSucceeded, Is.False);
                Assert.That(report.Stages.Count, Is.EqualTo(1));
                Assert.That(report.Stages[0].Name, Is.EqualTo("Preflight"));
                Assert.That(report.Stages[0].HasSucceeded, Is.False);
                Assert.That(report.Stages[0].Error, Is.TypeOf<InvalidDataException>());
                Assert.That(report.ResourceBuild, Is.Null);
                window.Repaint();
                yield return null;
                Assert.That(DrawnText(debugger), Does.Contain(report.Summary));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(AssetDatabase.GetAssetPath(profile)));
                Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
                Assert.That(Directory.Exists(profile.generatedAssetDirectory), Is.False);
                Assert.That(Directory.Exists(profile.baselineDirectory), Is.False);
            }
            finally
            {
                if (debugger != null)
                    debugger.GetMethod("StopDebugging", StaticFlags).Invoke(null, null);
                window.Close();
                AssetDatabase.DeleteAsset(root);
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
