using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Experimental.GraphView;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.StateMachine;

namespace YLFramework.StateMachine.Acceptance
{
    [Category("YL.Module.StateMachine")]
    [Category("YL.Special.StateMachinePackage")]
    [Category("YL.Package.StateMachineStandalone")]
    public sealed class StateMachinePackageAcceptanceTests
    {
        private const string ProbeAssembly = "YLFramework.StateMachine.Acceptance";
        private const string StateScript = "Assets/Tests/YLFramework/Fixtures/StateMachine/Runtime/StateMachinePlayerState.cs";
        private const string RunnerScene = "Assets/YLTestingProfile/Empty.unity";
        private static readonly string _domainToken = Guid.NewGuid().ToString("N");
        private static string Project => Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
        private static string PendingKey => "YLFramework.StateMachine.Acceptance/CreateAndAdd/" + Application.dataPath;

        [Serializable]
        private sealed class Owner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
            public string workspaceId;
        }

        [Serializable]
        private sealed class OwnedAsset
        {
            public string path;
            public string guid;
            public string sha256;
            public string metaSha256;
        }

        [Serializable]
        private sealed class BindingRun
        {
            public int schemaVersion = 1;
            public string status;
            public string error;
            public string output;
            public string token;
            public string folder;
            public string folderGuid;
            public string folderMetaSha256;
            public string className;
            public string assemblyName;
            public string beforeDomain;
            public string cleanupDomain;
            public string afterCleanupDomain;
            public string unityVersion;
            public string runtimeAssembly;
            public string editorAssembly;
            public int pid;
            public long deadlineTicks;
            public bool reopened;
            public List<OwnedAsset> assets = new List<OwnedAsset>();
        }

        [Serializable]
        private sealed class StateCounts
        {
            public string node;
            public int creates;
            public int enters;
            public int updates;
            public int exits;
        }

        [Serializable]
        private sealed class PlayerReport
        {
            public int schemaVersion;
            public string status;
            public string error;
            public bool isEditor;
            public string platform;
            public string unityVersion;
            public string backend;
            public int pid;
            public int run;
            public string token;
            public string runtimeAssembly;
            public string configurationAssembly;
            public string stateAssembly;
            public string[] frameworkAssemblies;
            public StateCounts[] states;
            public string[] events;
            public int disableExits;
            public int destroyExits;
            public bool disabledUpdatesStopped;
            public bool cloneReused;
            public bool cloneReleased;
            public bool sourceUnchanged;
        }

        [UnityTest]
        public IEnumerator CreateAndAddCompilesANewUserAssemblyAndPersistsTheBindingAcrossReload()
        {
            BeginCreateAndAdd();
            yield return new RecompileScripts(true);
            while (!CompiledBindingReady())
            {
                yield return null;
            }
            VerifyBindingAndBeginCleanup();
            yield return new RecompileScripts(true);
            VerifyCleanupReload();
        }

        private static bool CompiledBindingReady()
        {
            // UTF restores only the iterator PC, so no binding object may live across any yield.
            var run = ReadPending();
            Assert.That(_domainToken, Is.Not.EqualTo(run.beforeDomain), "A requested compilation alone is not evidence of an actual domain reload.");
            Assert.That(EditorUtility.scriptCompilationFailed, Is.False);
            Assert.That(DateTime.UtcNow.Ticks, Is.LessThan(run.deadlineTicks), "Create And Add did not bind the compiled script before its deadline.");
            return BindingReady(run);
        }

        private static void VerifyBindingAndBeginCleanup()
        {
            var run = ReadPending();
            Assert.That(DateTime.UtcNow.Ticks, Is.LessThan(run.deadlineTicks), "A binding that only arrived after the deadline is not a successful timed run.");
            VerifyReopenedBinding(run);
            run.reopened = true;
            run.status = "BoundAndReopened";
            run.cleanupDomain = _domainToken;
            CaptureAsset(run, run.folder + "/Controller.asset");
            WriteNew(Path.Combine(run.output, "FsmBinding-verified.json"), JsonUtility.ToJson(run, true));
            SessionState.SetString(PendingKey, JsonUtility.ToJson(run));
            DeleteOwnedAssets(run);
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
        }

        private static void VerifyCleanupReload()
        {
            var run = ReadPending();
            Assert.That(run.reopened, Is.True);
            Assert.That(_domainToken, Is.Not.EqualTo(run.cleanupDomain), "Removing the owned generated assembly must also settle through a real reload.");
            Assert.That(Directory.Exists(Physical(run.folder)) || File.Exists(Physical(run.folder + ".meta")), Is.False);
            Assert.That(AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == run.assemblyName), Is.False);
            run.afterCleanupDomain = _domainToken;
            run.status = "Passed";
            WriteNew(Path.Combine(run.output, "FsmBinding-result.json"), JsonUtility.ToJson(run, true));
            SessionState.EraseString(PendingKey);
        }

        private static void BeginCreateAndAdd()
        {
            string output = RequireContext();
            RequireEmptyScenes();
            Assert.That(SessionState.GetString(PendingKey, string.Empty), Is.Empty, "Do not adopt a previous incomplete binding run.");
            var run = NewOwnedAssets(output);
            run.status = "Pending";
            run.className = "FsmGenerated" + run.token;
            run.assemblyName = "YLFramework.StateMachine.Acceptance.Generated" + run.token;
            run.beforeDomain = _domainToken;
            run.deadlineTicks = DateTime.UtcNow.AddMinutes(2).Ticks;
            SessionState.SetString(PendingKey, JsonUtility.ToJson(run));
            var controller = ScriptableObject.CreateInstance<RuntimeFsmController>();
            var node = new FsmStateNodeData { name = "Generated", isDefaultState = true };
            controller.states.Add(node);
            Assert.That(File.Exists(Physical(run.folder + "/Controller.asset")) || File.Exists(Physical(run.folder + "/Controller.asset.meta")), Is.False);
            AssetDatabase.CreateAsset(controller, run.folder + "/Controller.asset");
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
            CaptureAsset(run, run.folder + "/Controller.asset");
            SessionState.SetString(PendingKey, JsonUtility.ToJson(run));
            string definition = "{\"name\":\"" + run.assemblyName + "\",\"references\":[\"YLFramework.StateMachine\"]," +
                "\"autoReferenced\":false,\"overrideReferences\":true,\"precompiledReferences\":[],\"noEngineReferences\":false}";
            WriteNew(Physical(run.folder + "/Generated.asmdef"), definition);
            string scriptPath = run.folder + "/" + run.className + ".cs";
            Assert.That(File.Exists(Physical(scriptPath)) || File.Exists(Physical(scriptPath + ".meta")), Is.False);
            // This is the actual product Create And Add entry. The product owns scheduling, importing and attaching.
            Assert.That(FsmStateCreator.CreateFsmState(scriptPath, false, controller, node), Is.True);
            Assert.That(node.stateScripts, Is.Empty, "An uncompiled script must not be manually attached by the fixture.");
            CaptureAsset(run, scriptPath);
            CaptureAsset(run, run.folder + "/Generated.asmdef");
            SessionState.SetString(PendingKey, JsonUtility.ToJson(run));
            WriteNew(Path.Combine(output, "FsmBinding-pending.json"), JsonUtility.ToJson(run, true));
        }

        private static BindingRun ReadPending()
        {
            string output = RequireContext();
            string json = SessionState.GetString(PendingKey, string.Empty);
            Assert.That(json, Is.Not.Empty, "Reload lost the owned binding identity.");
            var run = JsonUtility.FromJson<BindingRun>(json);
            Assert.That(run, Is.Not.Null);
            Assert.That(run.schemaVersion, Is.EqualTo(1));
            Assert.That(run.output, Is.EqualTo(output));
            Assert.That(Guid.TryParseExact(run.token, "N", out _), Is.True);
            Assert.That(run.folder, Is.EqualTo("Assets/__YLFsm_" + run.token));
            Assert.That(run.pid, Is.EqualTo(System.Diagnostics.Process.GetCurrentProcess().Id));
            Assert.That(run.unityVersion, Is.EqualTo(Application.unityVersion));
            return run;
        }

        private static bool BindingReady(BindingRun run)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return false;
            }
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(run.folder + "/" + run.className + ".cs");
            var asset = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(run.folder + "/Controller.asset");
            var node = asset ? asset.GetStateNodeData("Generated") : null;
            return script && script.GetClass() != null && node != null && node.stateScripts.Count != 0;
        }

        private static void VerifyReopenedBinding(BindingRun run)
        {
            string path = run.folder + "/" + run.className + ".cs";
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            var type = script.GetClass();
            Assert.That(type.FullName, Is.EqualTo(run.className));
            Assert.That(type.BaseType, Is.EqualTo(typeof(FsmState)));
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo(run.assemblyName));
            Assert.That(Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(path)), Is.EqualTo(run.assemblyName));
            var assembly = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(value => value.name == run.assemblyName);
            Assert.That(assembly.assemblyReferences.Select(reference => reference.name), Does.Contain("YLFramework.StateMachine"));
            Assert.That(assembly.sourceFiles.Select(Physical), Does.Contain(Physical(path)));
            Assert.That(File.Exists(Physical(assembly.outputPath)), Is.True);
            string assetPath = run.folder + "/Controller.asset";
            var asset = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(assetPath);
            Assert.That(EditorUtility.IsDirty(asset), Is.False, "The pending product binding must already have been saved, not rescued by the test.");
            string guid = run.assets.Single(value => value.path == path).guid;
            Assert.That(asset.GetStateNodeData("Generated").stateScripts.Single().guid, Is.EqualTo(guid));
            Resources.UnloadAsset(asset);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var reopened = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(assetPath);
            Assert.That(ReferenceEquals(asset, reopened), Is.False, "Reopening must read a new asset object, not the old in-memory binding.");
            Assert.That(AssetDatabase.AssetPathToGUID(assetPath), Is.EqualTo(run.assets.Single(value => value.path == assetPath).guid));
            var binding = reopened.GetStateNodeData("Generated").stateScripts.Single();
            Assert.That(binding.guid, Is.EqualTo(guid));
            Assert.That(binding.className, Is.EqualTo(run.className));
            Assert.That(reopened.ValidateConfiguration(out string error), Is.True, error);
            foreach (string generated in new[] { path, assetPath, run.folder + "/Generated.asmdef" })
            {
                File.Copy(Physical(generated), Path.Combine(run.output, Path.GetFileName(generated)), false);
                File.Copy(Physical(generated + ".meta"), Path.Combine(run.output, Path.GetFileName(generated) + ".meta"), false);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentAuthoringColdReadRetainsScriptsGroupsAndNamedPrefabLayers(bool automatic)
        {
            string output = RequireContext();
            RequireEmptyScenes();
            EditorSceneManager.OpenScene(RunnerScene, OpenSceneMode.Single);
            RequireEmptyScenes();
            var run = NewOwnedAssets(output);
            var selection = Selection.instanceIDs;
            var asset = CreatePlayerConfiguration(run);
            string path = AssetDatabase.GetAssetPath(asset);
            string guid = AssetDatabase.AssetPathToGUID(path);
            asset.layers.Add("Nested");
            asset.originGuid = guid;
            asset.viewPosition = new Vector3(20, 40, 0);
            asset.viewScale = Vector3.one * 0.6f;
            var transition = asset.GetTransitionData("Idle", "Nested");
            transition.autoSwitch = automatic;
            transition.groupConditions.Add(new GroupCondition
            {
                conditions = new List<FsmConditionData>
                {
                    new FsmConditionData { parameterName = "advance", targetValue = 0, compareType = CompareType.Equal }
                }
            });
            asset.Save();
            AssetDatabase.SaveAssetIfDirty(asset);
            CaptureAsset(run, path);
            var originalActive = SceneManager.GetActiveScene();
            var authoringScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            if (SceneManager.GetActiveScene() != authoringScene)
            {
                SceneManager.SetActiveScene(authoringScene);
            }
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(authoringScene));
            var host = new GameObject("OwnedCurrentFsmLayers");
            host.SetActive(false);
            try
            {
                var controller = host.AddComponent<FsmController>();
                controller.AddRuntimeFsmController(asset, "Gameplay");
                using (var serialized = new SerializedObject(controller))
                {
                    serialized.FindProperty("resetOnDisable").boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                string prefabPath = run.folder + "/Host.prefab";
                Assert.That(PrefabUtility.SaveAsPrefabAsset(host, prefabPath), Is.Not.Null);
                CaptureAsset(run, prefabPath);
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
                Undo.ClearUndo(asset);
                Resources.UnloadAsset(asset);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var reopened = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
                Assert.That(ReferenceEquals(reopened, asset), Is.False);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(reopened.layers, Is.EqualTo(new[] { "Nested" }));
                Assert.That(reopened.originGuid, Is.EqualTo(guid));
                Assert.That(reopened.viewPosition, Is.EqualTo(new Vector3(20, 40, 0)));
                Assert.That(reopened.viewScale, Is.EqualTo(Vector3.one * 0.6f));
                Assert.That(reopened.GetDefaultStateNodeData(string.Empty).name, Is.EqualTo("Idle"));
                Assert.That(reopened.GetDefaultStateNodeData("Nested").name, Is.EqualTo("Child"));
                Assert.That(reopened.GetStateNodeData("Nested").isSubStateMachine, Is.True);
                Assert.That(reopened.GetStateNodeData(FsmConst.EntryState).IsEntryState, Is.True);
                Assert.That(reopened.GetStateNodeData(FsmConst.AnyState).IsAnyState, Is.True);
                foreach (string node in new[] { "Idle", "Nested", "Child" })
                {
                    var script = reopened.GetStateNodeData(node).stateScripts.Single();
                    Assert.That(script.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(StateScript)));
                    Assert.That(script.className, Is.EqualTo("YLFramework.StateMachine.Acceptance.StateMachinePlayerState"));
                }
                var edge = reopened.GetTransitionData("Idle", "Nested");
                Assert.That(edge.autoSwitch, Is.EqualTo(automatic));
                Assert.That(edge.conditions.Single().targetValue, Is.EqualTo(1));
                Assert.That(edge.groupConditions.Single().conditions.Single().parameterName, Is.EqualTo("advance"));
                Assert.That(edge.groupConditions.Single().conditions.Single().targetValue, Is.Zero);
                Assert.That(reopened.parameters.Single().Value, Is.Zero);
                Assert.That(reopened.ValidateConfiguration(out string error), Is.True, error);
                var contents = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(contents), Is.Zero);
                    var restored = contents.GetComponent<FsmController>();
                    Assert.That(restored.RuntimeFsmControllerLayers.Single().name, Is.EqualTo("Gameplay"));
                    Assert.That(restored.RuntimeFsmControllerLayers.Single().controller, Is.SameAs(reopened));
                    using var serialized = new SerializedObject(restored);
                    Assert.That(serialized.FindProperty("resetOnDisable").boolValue, Is.False);
                    Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(restored))),
                        Is.EqualTo("669ecbf1eff5cdb4fb85896497a489b6"));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
                foreach (var input in run.assets)
                {
                    string name = (automatic ? "Automatic-" : "Conditional-") + Path.GetFileName(input.path);
                    File.Copy(Physical(input.path), Path.Combine(output, name), false);
                    File.Copy(Physical(input.path + ".meta"), Path.Combine(output, name + ".meta"), false);
                }
                run.status = "ColdReadVerified";
                run.reopened = true;
                WriteNew(Path.Combine(output, "FsmCurrentInput-" + automatic + ".json"), JsonUtility.ToJson(run, true));
                DeleteOwnedAssets(run);
            }
            finally
            {
                if (host)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
                if (originalActive.IsValid() && originalActive.isLoaded)
                {
                    SceneManager.SetActiveScene(originalActive);
                }
                if (authoringScene.IsValid() && authoringScene.isLoaded)
                {
                    Assert.That(EditorSceneManager.CloseScene(authoringScene, true), Is.True);
                }
                Selection.instanceIDs = selection;
            }
        }

        [Test]
        public void CurrentGraphDeletesNestedNodesAndRestoresBindingsWithOneUndoRedo()
        {
            string output = RequireContext();
            RequireEmptyScenes();
            var run = NewOwnedAssets(output);
            var selection = Selection.instanceIDs;
            var instanceField = typeof(Context).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = instanceField.GetValue(null);
            string[] integerKeys = { "YL.AssetFSMControllerInstanceID", "FSMControllerIndex" };
            bool[] hadKeys = integerKeys.Select(EditorPrefs.HasKey).ToArray();
            int[] values = integerKeys.Select(key => EditorPrefs.GetInt(key)).ToArray();
            const string GuidKey = "YL.AssetFSMRuntimeFSMControllerGUID";
            bool hadGuid = EditorPrefs.HasKey(GuidKey);
            string previousGuid = EditorPrefs.GetString(GuidKey);
            FsmStateGraphView graph = null;
            try
            {
                var asset = CreatePlayerConfiguration(run);
                foreach (string key in integerKeys)
                {
                    EditorPrefs.SetInt(key, 0);
                }
                var context = (Context)Activator.CreateInstance(typeof(Context), true);
                context.RuntimeFsmControllerGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
                instanceField.SetValue(null, context);
                graph = new FsmStateGraphView();
                Assert.That(graph.StateNodes.Keys, Is.EquivalentTo(new[] { "Any State", "Entry", "Idle", "Nested" }));
                Undo.FlushUndoRecordObjects();
                Undo.ClearUndo(asset);
                graph.AddToSelection(graph.GetNode("Nested"));
                graph.deleteSelection("Delete", GraphView.AskUser.DontAskUser);
                Undo.FlushUndoRecordObjects();
                Assert.That(asset.states.Select(node => node.name), Is.EquivalentTo(new[] { "Any State", "Entry", "Idle" }));
                Assert.That(asset.transitions, Is.Empty);
                Undo.PerformUndo();
                asset.ClearCache();
                graph.RefreshNodes();
                Assert.That(graph.GetNode("Nested"), Is.Not.Null);
                Assert.That(asset.GetDefaultStateNodeData("Nested").name, Is.EqualTo("Child"));
                Assert.That(asset.GetStateNodeData("Child").stateScripts.Single().guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(StateScript)));
                Assert.That(asset.GetTransitionData("Idle", "Nested").conditions.Single().parameterName, Is.EqualTo("advance"));
                Undo.PerformRedo();
                asset.ClearCache();
                graph.RefreshNodes();
                Assert.That(graph.GetNode("Nested"), Is.Null);
                Assert.That(asset.GetStateNodeData("Child"), Is.Null);
                Assert.That(asset.GetStateNodeData(FsmConst.EntryState).IsEntryState, Is.True);
                Assert.That(asset.GetStateNodeData(FsmConst.AnyState).IsAnyState, Is.True);
                Undo.ClearUndo(asset);
                asset.Save();
                AssetDatabase.SaveAssetIfDirty(asset);
                CaptureAsset(run, AssetDatabase.GetAssetPath(asset));
                DeleteOwnedAssets(run);
            }
            finally
            {
                graph?.ClearSelection();
                graph?.Clear();
                instanceField.SetValue(null, previous);
                for (int index = 0; index < integerKeys.Length; index++)
                {
                    if (hadKeys[index])
                    {
                        EditorPrefs.SetInt(integerKeys[index], values[index]);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(integerKeys[index]);
                    }
                }
                if (hadGuid)
                {
                    EditorPrefs.SetString(GuidKey, previousGuid);
                }
                else
                {
                    EditorPrefs.DeleteKey(GuidKey);
                }
                Selection.instanceIDs = selection;
            }
        }

        [Test]
        public void MonoPlayerLoadsTheNewRuntimeAssetAndRunsUserStateLifecyclesInTwoProcesses()
        {
            string output = RequireContext();
            RequireEmptyScenes();
            Assert.That(SessionState.GetString(PendingKey, string.Empty), Is.Empty, "Do not build a Player after an incomplete generated-assembly run.");
            Assert.That(Application.platform, Is.EqualTo(RuntimePlatform.WindowsEditor));
            Assert.That(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), Is.True);
            var run = NewOwnedAssets(output);
            string player = Path.Combine(Project, "P", run.token.Substring(0, 8));
            Assert.That(Directory.Exists(player) || File.Exists(player), Is.False);
            NoLinks(player);
            Directory.CreateDirectory(player);
            WriteNew(Path.Combine(player, ".fsm-owner.json"), JsonUtility.ToJson(run, true));
            WriteNew(Path.Combine(output, "fsm-player-location.txt"), player);
            NoLinks(Physical(RunnerScene));
            Assert.That(File.Exists(Physical(RunnerScene)), Is.True, "The owned session must provide its prepared empty scene.");
            EditorSceneManager.OpenScene(RunnerScene, OpenSceneMode.Single);
            RequireEmptyScenes();
            var originalActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            string executable = Path.Combine(player, "P.exe");
            RuntimeFsmController configuration;
            try
            {
                if (SceneManager.GetActiveScene() != scene)
                {
                    SceneManager.SetActiveScene(scene);
                }
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene), "The actual owned build scene must be active before authoring its probe.");
                configuration = CreatePlayerConfiguration(run);
                Assert.That((bool)configuration, Is.True, "Scene changes must not unload the asset before its serialized probe reference is assigned.");
                var host = new GameObject("OwnedStateMachinePlayerProbe");
                SceneManager.MoveGameObjectToScene(host, scene);
                // Fixed test-owned authoring type only; no fixture reference is added to the shared test asmdef.
                var type = Type.GetType("YLFramework.StateMachine.Acceptance.StateMachinePlayerProbe, " + ProbeAssembly, true);
                var probe = host.AddComponent(type);
                using (var serialized = new SerializedObject(probe))
                {
                    serialized.FindProperty("configuration").objectReferenceValue = configuration;
                    serialized.FindProperty("runToken").stringValue = run.token;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    Assert.That(serialized.FindProperty("configuration").objectReferenceValue, Is.SameAs(configuration));
                }
                string scenePath = run.folder + "/Player.unity";
                Assert.That(File.Exists(Physical(scenePath)) || File.Exists(Physical(scenePath + ".meta")), Is.False);
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                CaptureAsset(run, scenePath);
                foreach (var input in run.assets)
                {
                    File.Copy(Physical(input.path), Path.Combine(output, "FsmPlayer-" + Path.GetFileName(input.path)), false);
                    File.Copy(Physical(input.path + ".meta"), Path.Combine(output, "FsmPlayer-" + Path.GetFileName(input.path) + ".meta"), false);
                }
                run.status = "PlayerInputSaved";
                WriteNew(Path.Combine(output, "FsmPlayer-current-input.json"), JsonUtility.ToJson(run, true));
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = executable,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                    extraScriptingDefines = new[] { "YL_STATE_MACHINE_ACCEPTANCE" }
                });
                Assert.That(build.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                if (originalActive.IsValid() && originalActive.isLoaded)
                {
                    SceneManager.SetActiveScene(originalActive);
                }
                if (scene.IsValid() && scene.isLoaded)
                {
                    Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True);
                }
            }
            string managed = Path.Combine(player, "P_Data/Managed");
            CollectionAssert.AreEquivalent(new[] { "YLFramework.Runtime.dll", "YLFramework.StateMachine.dll", ProbeAssembly + ".dll" },
                Directory.GetFiles(managed, "YLFramework*.dll").Select(Path.GetFileName));
            int previousPid = 0;
            for (int round = 1; round <= 2; round++)
            {
                string result = Path.Combine(player, "result-" + round + ".json");
                Assert.That(File.Exists(result), Is.False);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = player,
                    Arguments = "-batchmode -nographics -ylFsmResult \"" + result + "\" -logFile \"" + Path.Combine(player, "player-" + round + ".log") + "\""
                });
                Assert.That(process, Is.Not.Null);
                if (!process.WaitForExit(90000))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                    Assert.Fail("The actual FSM Player timed out. A killed process is not a successful lifecycle run.");
                }
                Assert.That(process.ExitCode, Is.Zero);
                NoLinks(result);
                var report = JsonUtility.FromJson<PlayerReport>(File.ReadAllText(result));
                string copy = Path.Combine(output, "FsmPlayer-" + round + ".json");
                Assert.That(File.Exists(copy), Is.False);
                File.Copy(result, copy, false);
                AssertPlayerReport(report, process.Id, previousPid, round, run.token);
                previousPid = process.Id;
            }
            configuration = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(run.folder + "/StateMachinePackageAcceptance.asset");
            Assert.That((bool)configuration, Is.True);
            Assert.That(EditorUtility.IsDirty(configuration), Is.False);
            DeleteOwnedAssets(run);
        }

        private static RuntimeFsmController CreatePlayerConfiguration(BindingRun run)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(StateScript);
            Assert.That(script, Is.Not.Null);
            Assert.That(script.GetClass().FullName, Is.EqualTo("YLFramework.StateMachine.Acceptance.StateMachinePlayerState"));
            Assert.That(script.GetClass().Assembly.GetName().Name, Is.EqualTo(ProbeAssembly));
            string path = run.folder + "/StateMachinePackageAcceptance.asset";
            Assert.That(File.Exists(Physical(path)) || File.Exists(Physical(path + ".meta")), Is.False);
            var creator = ScriptableObject.CreateInstance<RuntimeFsmControllerCreator>();
            var selection = Selection.instanceIDs;
            try
            {
                creator.Action(0, path, null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(creator);
                Selection.instanceIDs = selection;
            }
            var asset = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
            var idle = FsmStateNodeFactory.CreateStateNode(asset, "Idle", new Rect(400, 100, 200, 40), true);
            var nested = FsmStateNodeFactory.CreateStateNode(asset, "Nested", new Rect(400, 300, 200, 40), false, true);
            var child = FsmStateNodeFactory.CreateStateNode(asset, "Child", new Rect(400, 100, 200, 40), true, false, new List<string> { "Nested" });
            foreach (var node in new[] { idle, nested, child })
            {
                node.AddStateScript(script);
                Assert.That(node.stateScripts.Single().guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(StateScript)));
            }
            asset.AddParameters(new FsmParameterData { name = "advance", parameterType = ParameterType.Bool });
            FsmTransitionFactory.CreateTransition(asset, "Idle", "Nested");
            FsmConditionFactory.CreateCondition(asset, asset.GetTransitionData("Idle", "Nested"));
            FsmConditionFactory.SetTargetValue(asset, asset.GetTransitionData("Idle", "Nested").conditions.Single(), 1);
            Assert.That(asset.ValidateConfiguration(out string error), Is.True, error);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            CaptureAsset(run, path);
            return asset;
        }

        private static void AssertPlayerReport(PlayerReport report, int pid, int previousPid, int round, string token)
        {
            Assert.That(report, Is.Not.Null);
            Assert.That(report.schemaVersion, Is.EqualTo(1));
            Assert.That(report.status, Is.EqualTo("Passed"), report.error);
            Assert.That(report.error, Is.Null.Or.Empty);
            Assert.That(report.pid, Is.EqualTo(pid).And.Not.EqualTo(previousPid));
            Assert.That(report.run, Is.EqualTo(round));
            Assert.That(report.token, Is.EqualTo(token));
            Assert.That(report.isEditor, Is.False);
            Assert.That(report.platform, Is.EqualTo("WindowsPlayer"));
            Assert.That(report.backend, Is.EqualTo("Mono"));
            Assert.That(report.unityVersion, Is.EqualTo(Application.unityVersion));
            Assert.That(report.runtimeAssembly, Is.EqualTo("YLFramework.StateMachine"));
            Assert.That(report.configurationAssembly, Is.EqualTo("YLFramework.StateMachine"));
            Assert.That(report.stateAssembly, Is.EqualTo(ProbeAssembly));
            CollectionAssert.AreEquivalent(new[] { "YLFramework.Runtime", "YLFramework.StateMachine", ProbeAssembly }, report.frameworkAssemblies);
            Assert.That(report.disableExits, Is.EqualTo(2));
            Assert.That(report.destroyExits, Is.EqualTo(1));
            Assert.That(report.disabledUpdatesStopped && report.cloneReused && report.cloneReleased && report.sourceUnchanged, Is.True);
            Assert.That(report.states, Has.Length.EqualTo(3));
            foreach (var node in new[] { "Idle", "Nested", "Child" })
            {
                var counts = report.states.Single(value => value.node == node);
                Assert.That(counts.creates, Is.EqualTo(1), node);
                Assert.That(counts.enters, Is.EqualTo(node == "Idle" ? 2 : 1), node);
                Assert.That(counts.exits, Is.EqualTo(node == "Idle" ? 2 : 1), node);
                Assert.That(counts.updates, Is.GreaterThanOrEqualTo(node == "Idle" ? 2 : 1), node);
            }
            CollectionAssert.AreEqual(new[] { "create:Idle", "enter:Idle", "exit:Idle", "create:Nested", "enter:Nested",
                "create:Child", "enter:Child", "exit:Nested", "exit:Child", "enter:Idle", "exit:Idle" }, report.events);
        }

        private static string RequireContext()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying, Is.True, "Only the tool-owned batch host may run this fixture.");
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            NoLinks(Project);
            string hostPath = Path.Combine(Project, ".yl-owner.json");
            NoLinks(hostPath);
            var host = JsonUtility.FromJson<Owner>(File.ReadAllText(hostPath));
            Assert.That(host.schemaVersion, Is.EqualTo(1));
            Assert.That(host.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(host.kind, Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath(host.path), Is.EqualTo(Project).IgnoreCase);
            Assert.That(host.workspaceId, Is.Not.Null.And.Not.Empty);
            NoLinks(output);
            var ancestor = new DirectoryInfo(output);
            while (ancestor != null && !File.Exists(Path.Combine(ancestor.FullName, ".yl-owner.json")))
            {
                ancestor = ancestor.Parent;
            }
            Assert.That(ancestor, Is.Not.Null, "A current result owner is required.");
            string ownerPath = Path.Combine(ancestor.FullName, ".yl-owner.json");
            NoLinks(ownerPath);
            var result = JsonUtility.FromJson<Owner>(File.ReadAllText(ownerPath));
            Assert.That(result.schemaVersion, Is.EqualTo(1));
            Assert.That(result.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(result.kind, Is.EqualTo("Result"));
            Assert.That(result.workspaceId, Is.EqualTo(host.workspaceId));
            Assert.That(Path.GetFullPath(result.path), Is.EqualTo(ancestor.FullName).IgnoreCase);
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(package => package.name == "com.kano.ylframework" || package.name.StartsWith("com.kano.ylframework.", StringComparison.Ordinal))
                .Select(package => package.name);
            CollectionAssert.AreEquivalent(new[] { "com.kano.ylframework", "com.kano.ylframework.state-machine" }, packages,
                "The standalone proof must not silently install unrelated optional packages.");
            Assert.That(typeof(FsmController).Assembly.GetName().Name, Is.EqualTo("YLFramework.StateMachine"));
            Assert.That(typeof(FsmStateCreator).Assembly.GetName().Name, Is.EqualTo("YLFramework.StateMachine.Editor"));
            return Path.GetFullPath(output);
        }

        private static void RequireEmptyScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                Assert.That(scene.isDirty, Is.False, "Never save or replace an unknown dirty scene.");
                Assert.That(scene.rootCount, Is.Zero, "SessionNative must prepare the owned empty runner scene before this fixture runs.");
                Assert.That(string.IsNullOrEmpty(scene.path) || scene.path == RunnerScene, Is.True);
            }
        }

        private static BindingRun NewOwnedAssets(string output)
        {
            string token = Guid.NewGuid().ToString("N");
            string folder = "Assets/__YLFsm_" + token;
            NoLinks(Physical(folder));
            Assert.That(Directory.Exists(Physical(folder)) || File.Exists(Physical(folder)) || File.Exists(Physical(folder + ".meta")), Is.False);
            string guid = AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            Assert.That(guid, Is.Not.Null.And.Not.Empty);
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(folder));
            Assert.That(Directory.GetFileSystemEntries(Physical(folder)), Is.Empty);
            return new BindingRun
            {
                output = output,
                token = token,
                folder = folder,
                folderGuid = guid,
                folderMetaSha256 = Hash(Physical(folder + ".meta")),
                unityVersion = Application.unityVersion,
                runtimeAssembly = typeof(FsmController).Assembly.GetName().Name,
                editorAssembly = typeof(FsmStateCreator).Assembly.GetName().Name,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id
            };
        }

        private static void CaptureAsset(BindingRun run, string path)
        {
            Assert.That(path.StartsWith(run.folder + "/", StringComparison.Ordinal) && Path.GetDirectoryName(path).Replace('\\', '/') == run.folder, Is.True);
            NoLinks(Physical(path));
            NoLinks(Physical(path + ".meta"));
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Is.Not.Null.And.Not.Empty, path);
            run.assets.RemoveAll(asset => asset.path == path);
            run.assets.Add(new OwnedAsset { path = path, guid = guid, sha256 = Hash(Physical(path)), metaSha256 = Hash(Physical(path + ".meta")) });
        }

        private static void DeleteOwnedAssets(BindingRun run)
        {
            NoLinks(Physical(run.folder));
            Assert.That(run.folder, Is.EqualTo("Assets/__YLFsm_" + run.token));
            Assert.That(AssetDatabase.AssetPathToGUID(run.folder), Is.EqualTo(run.folderGuid));
            Assert.That(Hash(Physical(run.folder + ".meta")), Is.EqualTo(run.folderMetaSha256));
            Assert.That(Directory.GetDirectories(Physical(run.folder)), Is.Empty, "Do not recursively remove an unknown child directory.");
            CollectionAssert.AreEquivalent(run.assets.SelectMany(asset => new[] { Physical(asset.path), Physical(asset.path + ".meta") }),
                Directory.GetFiles(Physical(run.folder)), "Cleanup may only remove the exact recorded owned file set.");
            foreach (var asset in run.assets)
            {
                NoLinks(Physical(asset.path));
                NoLinks(Physical(asset.path + ".meta"));
                Assert.That(AssetDatabase.AssetPathToGUID(asset.path), Is.EqualTo(asset.guid));
                Assert.That(Hash(Physical(asset.path)), Is.EqualTo(asset.sha256));
                Assert.That(Hash(Physical(asset.path + ".meta")), Is.EqualTo(asset.metaSha256));
            }
            foreach (var asset in run.assets)
            {
                Assert.That(AssetDatabase.DeleteAsset(asset.path), Is.True, asset.path);
            }
            Assert.That(Directory.GetFileSystemEntries(Physical(run.folder)), Is.Empty);
            Assert.That(AssetDatabase.DeleteAsset(run.folder), Is.True);
        }

        [TearDown]
        public void PreserveIncompleteBindingEvidence()
        {
            if (TestContext.CurrentContext.Test.FullName != typeof(StateMachinePackageAcceptanceTests).FullName + "." + nameof(CreateAndAddCompilesANewUserAssemblyAndPersistsTheBindingAcrossReload))
            {
                return;
            }
            string json = SessionState.GetString(PendingKey, string.Empty);
            if (!string.IsNullOrEmpty(json) && TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                var run = ReadPending();
                run.status = "Failed";
                run.error = TestContext.CurrentContext.Result.Message;
                WriteNew(Path.Combine(run.output, "FsmBinding-failed.json"), JsonUtility.ToJson(run, true));
                // Failed generated inputs remain in this owned host for diagnosis; no unknown files are adopted or removed.
            }
        }

        private static string Physical(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Project, path));

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void WriteNew(string path, string value)
        {
            Assert.That(Directory.Exists(Path.GetDirectoryName(path)), Is.True);
            NoLinks(path);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(value);
        }

        private static void NoLinks(string path)
        {
            if (File.Exists(path))
            {
                Assert.That((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, Is.True, path);
            }
            for (var directory = new DirectoryInfo(File.Exists(path) ? Path.GetDirectoryName(path) : path); directory != null; directory = directory.Parent)
            {
                if (directory.Exists)
                {
                    Assert.That((directory.Attributes & FileAttributes.ReparsePoint) == 0, Is.True, directory.FullName);
                }
            }
        }
    }
}
