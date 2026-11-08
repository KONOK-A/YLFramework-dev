using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using YLFramework.Character2D;
using YLFramework.Overlap;
using YLFramework.Platform2D;
using YLFramework.StateMachine;
using YLFramework.Editor.StateMachine;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class BehaviorAuthoringTests
    {
        private const string Root = "Assets/Tests/YLFramework/Fixtures/Extensions/Behavior";
        private string _folder;
        private readonly List<GameObject> _owned = new List<GameObject>();
        private Undo.UndoRedoCallback[] _userUndoCallbacks;
        private FsmStateInspectorHelper _stateHelper;
        private FsmStateNodeData _previousNode;
        private RuntimeFsmController _previousStateController;
        private FsmStateGraphView _previousGraph;
        private FsmTransitionInspectorHelper _transitionHelper;
        private FsmTransitionData _previousTransition;
        private RuntimeFsmController _previousTransitionController;
        private int[] _previousSelection;

        [SetUp]
        public void Setup()
        {
            string name = "GeneratedTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(Root, name);
            _folder = Root + "/" + name;
            _userUndoCallbacks = Resources.FindObjectsOfTypeAll<FsmEditorWindow>().Select(window => (Undo.UndoRedoCallback)Delegate.CreateDelegate(typeof(Undo.UndoRedoCallback), window, "OnUndoRedo")).ToArray();
            foreach (var callback in _userUndoCallbacks)
            {
                Undo.undoRedoPerformed -= callback;
            }

            _stateHelper = FsmStateInspectorHelper.Instance;
            _previousNode = _stateHelper.node;
            _previousStateController = _stateHelper.controller;
            _previousGraph = _stateHelper.grap;
            _transitionHelper = FsmTransitionInspectorHelper.Instance;
            _previousTransition = _transitionHelper.transition;
            _previousTransitionController = _transitionHelper.controller;
            _previousSelection = Selection.instanceIDs;
        }

        [TearDown]
        public void Cleanup()
        {
            try
            {
                foreach (var obj in _owned)
                {
                    if (obj)
                    {
                        UnityEngine.Object.DestroyImmediate(obj);
                    }
                }

                _owned.Clear();
                if (!string.IsNullOrEmpty(_folder) && _folder.StartsWith(Root + "/GeneratedTest_", StringComparison.Ordinal))
                {
                    AssetDatabase.DeleteAsset(_folder);
                }
            }
            finally
            {
                if (_stateHelper)
                {
                    _stateHelper.node = _previousNode;
                    _stateHelper.controller = _previousStateController;
                    _stateHelper.grap = _previousGraph;
                }
                if (_transitionHelper)
                {
                    _transitionHelper.transition = _previousTransition;
                    _transitionHelper.controller = _previousTransitionController;
                }
                Selection.instanceIDs = _previousSelection;
                foreach (var callback in _userUndoCallbacks)
                {
                    Undo.undoRedoPerformed += callback;
                }

            }
        }

        private RuntimeFsmController CreateAsset()
        {
            var asset = ScriptableObject.CreateInstance<RuntimeFsmController>();
            AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(_folder + "/FSM.asset"));
            return asset;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentVariantValuesKeepNestedOverridesThroughUndoAndColdRead(bool redo)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var root = new GameObject("CurrentVariantNames");
            root.SetActive(false);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                root.AddComponent<CharacterController2D>();
                root.AddComponent<YLFramework.RedDots.Reddot>();
                var emitter = root.AddComponent<YLFramework.Audio.AudioEmitter>();
                using (var serialized = new SerializedObject(emitter))
                {
                    serialized.FindProperty("weightedClips").arraySize = 1;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                var basePath = _folder + "/CurrentVariantBase.prefab";
                var basePrefab = PrefabUtility.SaveAsPrefabAsset(root, basePath);
                var baseBytes = File.ReadAllBytes(basePath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab, scene);
                var paths = new[]
                {
                    (Type: typeof(CharacterController2D), Current: "maxJumpCount", Value: "4"),
                    (Type: typeof(CharacterController2D), Current: "maxFallingVelocity", Value: "23.5"),
                    (Type: typeof(YLFramework.RedDots.Reddot), Current: "path", Value: "Current/Variant")
                };
                PrefabUtility.SetPropertyModifications(instance, paths.Select(binding => new PropertyModification
                {
                    target = basePrefab.GetComponent(binding.Type),
                    propertyPath = binding.Current,
                    value = binding.Value
                }).ToArray());
                using (var data = new SerializedObject(instance.GetComponent<YLFramework.Audio.AudioEmitter>()))
                {
                    data.FindProperty("weightedClips.Array.data[0].weight").floatValue = 3.5f;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                Undo.IncrementCurrentGroup();
                using (var data = new SerializedObject(instance.GetComponent<CharacterController2D>()))
                {
                    data.FindProperty("maxJumpCount").intValue = 5;
                    data.ApplyModifiedProperties();
                }
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(instance.GetComponent<CharacterController2D>().maxJumpCount, Is.EqualTo(4));
                if (redo)
                    Undo.PerformRedo();
                var path = _folder + "/CurrentVariant.prefab";
                PrefabUtility.SaveAsPrefabAsset(instance, path);
                var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
                var bytes = File.ReadAllBytes(path);
                string guid = AssetDatabase.AssetPathToGUID(path);
                UnityEngine.Object.DestroyImmediate(instance);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach (var binding in paths)
                    {
                        using var serialized = new SerializedObject(contents.GetComponent(binding.Type));
                        Assert.That(serialized.FindProperty(binding.Current).prefabOverride, Is.True, binding.Current);
                        Assert.That(PrefabUtility.GetPropertyModifications(contents).Single(modification => modification.propertyPath == binding.Current).value,
                            Is.EqualTo(redo && binding.Current == "maxJumpCount" ? "5" : binding.Value), binding.Current);
                    }
                    using var audio = new SerializedObject(contents.GetComponent<YLFramework.Audio.AudioEmitter>());
                    Assert.That(audio.FindProperty("weightedClips.Array.data[0].weight").prefabOverride, Is.True);
                    Assert.That(audio.FindProperty("weightedClips.Array.data[0].weight").floatValue, Is.EqualTo(3.5f));
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var restored = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(restored), Is.SameAs(basePrefab));
                Assert.That(restored.GetComponent<CharacterController2D>().maxJumpCount, Is.EqualTo(redo ? 5 : 4));
                Assert.That(restored.GetComponent<CharacterController2D>().maxFallingVelocity, Is.EqualTo(23.5f));
                using (var data = new SerializedObject(restored.GetComponent<YLFramework.Audio.AudioEmitter>()))
                    Assert.That(data.FindProperty("weightedClips.Array.data[0].weight").floatValue, Is.EqualTo(3.5f));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                CollectionAssert.AreEqual(baseBytes, File.ReadAllBytes(basePath));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentVariantArrayOverridesKeepSizeValuesAndReferences(bool transforms)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var root = new GameObject("CurrentVariantArrays");
            root.SetActive(false);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var target = new GameObject("AreaNode");
                target.transform.SetParent(root.transform, false);
                target.transform.localPosition = new Vector3(2, 3);
                var source = root.AddComponent<OverlapDetector2D>();
                if (transforms)
                    source.SetAreaTransforms(Array.Empty<Transform>());
                else
                    source.SetAreaPositions(Array.Empty<Vector3>());
                root.AddComponent<YLFramework.Audio.AudioEmitter>();
                var basePath = _folder + "/CurrentArrayBase.prefab";
                var basePrefab = PrefabUtility.SaveAsPrefabAsset(root, basePath);
                var baseBytes = File.ReadAllBytes(basePath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab, scene);
                var area = instance.GetComponent<OverlapDetector2D>();
                var emitter = instance.GetComponent<YLFramework.Audio.AudioEmitter>();
                string input = transforms ? "areaTransforms" : "areaPositions";
                using (var serialized = new SerializedObject(area))
                {
                    var nodes = serialized.FindProperty(input);
                    Assert.That(nodes.arraySize, Is.Zero);
                    nodes.arraySize = 2;
                    if (transforms)
                    {
                        nodes.GetArrayElementAtIndex(0).objectReferenceValue = instance.transform;
                        nodes.GetArrayElementAtIndex(1).objectReferenceValue = instance.transform.Find("AreaNode");
                    }
                    else
                    {
                        nodes.GetArrayElementAtIndex(0).vector3Value = Vector3.zero;
                        nodes.GetArrayElementAtIndex(1).vector3Value = new Vector3(2, 3, 4);
                    }
                    serialized.ApplyModifiedProperties();
                }
                using (var serialized = new SerializedObject(emitter))
                {
                    var clips = serialized.FindProperty("weightedClips");
                    Assert.That(clips.arraySize, Is.Zero);
                    clips.arraySize = 1;
                    clips.GetArrayElementAtIndex(0).FindPropertyRelative("weight").floatValue = 3.5f;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                Undo.IncrementCurrentGroup();
                using (var data = new SerializedObject(emitter))
                {
                    data.FindProperty("weightedClips.Array.data[0].weight").floatValue = 4.5f;
                    data.ApplyModifiedProperties();
                }
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                using (var data = new SerializedObject(emitter))
                    Assert.That(data.FindProperty("weightedClips.Array.data[0].weight").floatValue, Is.EqualTo(3.5f));
                string path = _folder + "/CurrentArrayVariant.prefab";
                Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, path), Is.Not.Null);
                var bytes = File.ReadAllBytes(path);
                var meta = File.ReadAllBytes(path + ".meta");
                UnityEngine.Object.DestroyImmediate(instance);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(contents), Is.SameAs(basePrefab));
                    using var nodes = new SerializedObject(contents.GetComponent<OverlapDetector2D>());
                    Assert.That(nodes.FindProperty(input).arraySize, Is.EqualTo(2));
                    Assert.That(nodes.FindProperty(input + ".Array.size").prefabOverride, Is.True);
                    if (transforms)
                    {
                        Assert.That(nodes.FindProperty(input).GetArrayElementAtIndex(0).objectReferenceValue, Is.SameAs(contents.transform));
                        Assert.That(nodes.FindProperty(input).GetArrayElementAtIndex(1).objectReferenceValue, Is.SameAs(contents.transform.Find("AreaNode")));
                    }
                    else
                        Assert.That(nodes.FindProperty(input).GetArrayElementAtIndex(1).vector3Value, Is.EqualTo(new Vector3(2, 3, 4)));
                    using var audio = new SerializedObject(contents.GetComponent<YLFramework.Audio.AudioEmitter>());
                    Assert.That(audio.FindProperty("weightedClips").arraySize, Is.EqualTo(1));
                    Assert.That(audio.FindProperty("weightedClips.Array.data[0].weight").floatValue, Is.EqualTo(3.5f));
                    Assert.That(audio.FindProperty("weightedClips.Array.data[0].weight").prefabOverride, Is.True);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
                using (var serialized = new SerializedObject(basePrefab.GetComponent<YLFramework.Audio.AudioEmitter>()))
                {
                    Assert.That(serialized.FindProperty("weightedClips").arraySize, Is.Zero);
                }
                CollectionAssert.AreEqual(baseBytes, File.ReadAllBytes(basePath));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentComponentsKeepExplicitValuesAreaNodesAndObjectLinks(bool enabled)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var root = new GameObject("CurrentComponents");
            root.SetActive(false);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var character = root.AddComponent<CharacterController2D>();
                character.maxFallingVelocity = 23.5f;
                character.maxJumpCount = 4;
                root.AddComponent<BoxCollider2D>();
                var node = new GameObject("AreaNode").transform;
                node.SetParent(root.transform, false);
                node.localPosition = new Vector3(2, 3, 4);
                var area = root.AddComponent<OverlapDetector2D>();
                area.SetAreaTransforms(new[] { root.transform, node, null });
                using (var serialized = new SerializedObject(area))
                {
                    serialized.FindProperty("runOnEnable").boolValue = enabled;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                var child = new GameObject("Indicator", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                child.transform.SetParent(root.transform, false);
                var reddot = root.AddComponent<YLFramework.RedDots.Reddot>();
                reddot.Path = "Legacy/Indicator";
                reddot.Indicator = child;
                root.AddComponent<YLFramework.Actions.ScreenTransitionCanvas>().colorImage = child.GetComponent<UnityEngine.UI.Image>();
                var path = _folder + "/CurrentComponents.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                var componentTypes = new[] { typeof(CharacterController2D), typeof(OverlapDetector2D), typeof(YLFramework.RedDots.Reddot), typeof(YLFramework.Actions.ScreenTransitionCanvas) };
                string Snapshot(GameObject value) => string.Join("\n", componentTypes.Select(type => EditorJsonUtility.ToJson(value.GetComponent(type))));
                var snapshot = Snapshot(prefab);
                var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
                var bytes = File.ReadAllBytes(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var restored = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(Snapshot(restored), Is.EqualTo(snapshot));
                using (var data = new SerializedObject(restored.GetComponent<OverlapDetector2D>()))
                {
                    Assert.That(data.FindProperty("runOnEnable").boolValue, Is.EqualTo(enabled));
                    Assert.That(data.FindProperty("areaTransforms").GetArrayElementAtIndex(0).objectReferenceValue, Is.SameAs(restored.transform));
                    Assert.That(data.FindProperty("areaTransforms").GetArrayElementAtIndex(1).objectReferenceValue, Is.SameAs(restored.transform.Find("AreaNode")));
                    Assert.That(data.FindProperty("areaTransforms").GetArrayElementAtIndex(2).objectReferenceValue, Is.Null);
                }
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentStateMachineRetainsLayersScriptsGroupsAndExplicitAutomaticPolicy(bool automatic)
        {
            var asset = CreateAsset();
            asset.layers.Add("Live");
            asset.originGuid = "legacy-origin-guid";
            var idle = FsmStateNodeFactory.CreateStateNode(asset, "Idle", new Rect(0, 0, 200, 40), true);
            FsmStateNodeFactory.CreateStateNode(asset, "Run", new Rect(200, 0, 200, 40), false);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(Root + "/Player/BehaviorAcceptanceState.cs");
            Assert.That(script, Is.Not.Null);
            idle.stateScripts.Add(new FsmStateScriptInfo
            {
                className = script.GetClass().FullName,
                guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script))
            });
            asset.AddParameters(new FsmParameterData { name = "Go", parameterType = ParameterType.Bool });
            FsmTransitionFactory.CreateTransition(asset, "Idle", "Run");
            var transition = asset.GetTransitionData("Idle", "Run");
            transition.autoSwitch = automatic;
            transition.groupConditions.Add(new GroupCondition
            {
                conditions = new List<FsmConditionData>
                { new FsmConditionData { parameterName = "Go", targetValue = 1 } }
            });
            asset.Save();
            AssetDatabase.SaveAssetIfDirty(asset);
            string path = AssetDatabase.GetAssetPath(asset);
            string guid = AssetDatabase.AssetPathToGUID(path);
            var meta = File.ReadAllBytes(path + ".meta");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var reloaded = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
            var serialized = new SerializedObject(reloaded);
            Assert.That(serialized.FindProperty("layers"), Is.Not.Null, "Migration target field missing: layers");
            Assert.That(serialized.FindProperty("layers").GetArrayElementAtIndex(0).stringValue, Is.EqualTo("Live"));
            Assert.That(serialized.FindProperty("originGuid").stringValue, Is.EqualTo("legacy-origin-guid"));
            var states = serialized.FindProperty("states");
            var node = Enumerable.Range(0, states.arraySize).Select(states.GetArrayElementAtIndex)
                .Single(state => state.FindPropertyRelative("name").stringValue == "Idle");
            Assert.That(node.FindPropertyRelative("isDefaultState").boolValue, Is.True);
            Assert.That(node.FindPropertyRelative("stateScripts").arraySize, Is.EqualTo(1));
            Assert.That(reloaded.GetStateNodeData("Idle").stateScripts.Single().className, Is.EqualTo(script.GetClass().FullName));
            Assert.That(reloaded.GetStateNodeData("Idle").stateScripts.Single().guid,
                Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script))));
            var moved = serialized.FindProperty("transitions").GetArrayElementAtIndex(0);
            Assert.That(moved.FindPropertyRelative("autoSwitch").boolValue, Is.EqualTo(automatic));
            Assert.That(moved.FindPropertyRelative("groupConditions").arraySize, Is.EqualTo(1));
            Assert.That(reloaded.GetTransitionData("Idle", "Run").groupConditions[0].conditions[0].parameterName, Is.EqualTo("Go"));
            Assert.That(reloaded.ValidateConfiguration(out string error), Is.True, error);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            EditorUtility.SetDirty(reloaded);
            AssetDatabase.SaveAssetIfDirty(reloaded);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path).GetTransitionData("Idle", "Run").autoSwitch, Is.EqualTo(automatic));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
        }

        [Test]
        public void AssetFsmSerializesOnlyNamedControllerLayers()
        {
            var asset = CreateAsset();
            var obj = new GameObject("OwnedNamedLayerHost");
            _owned.Add(obj);
            var host = obj.AddComponent<FsmController>();
            host.AddRuntimeFsmController(asset, "Gameplay");
            using (var serialized = new SerializedObject(host))
            {
                var layers = serialized.FindProperty("runtimeFsmControllerLayers");
                Assert.That(layers, Is.Not.Null, "Migration target field missing: runtimeFsmControllerLayers");
                Assert.That(layers.arraySize, Is.EqualTo(1));
                Assert.That(layers.GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue, Is.EqualTo("Gameplay"));
                Assert.That(layers.GetArrayElementAtIndex(0).FindPropertyRelative("controller").objectReferenceValue, Is.EqualTo(asset));
                Assert.That(serialized.FindProperty("_runtimeFSMController"), Is.Null, "Unreleased YL assets must not retain the old flat-controller schema or its migration path.");
            }
            string path = _folder + "/NamedLayerHost.prefab";
            PrefabUtility.SaveAsPrefabAsset(obj, path);
            var bytes = File.ReadAllBytes(path);
            var meta = File.ReadAllBytes(path + ".meta");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var loaded = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var reloaded = loaded.GetComponent<FsmController>();
                Assert.That(reloaded.RuntimeFsmControllerLayers.Count, Is.EqualTo(1));
                Assert.That(reloaded.RuntimeFsmControllerLayers[0].name, Is.EqualTo("Gameplay"));
                Assert.That(reloaded.RuntimeFsmControllerLayers[0].controller, Is.EqualTo(asset));
                using (var serialized = new SerializedObject(reloaded))
                {
                    Assert.That(serialized.FindProperty("_runtimeFSMController"), Is.Null);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(loaded);
            }
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
        }

        [Test]
        public void StateScriptRefreshUsesGuidWithoutMigratingClassNameOnlyBindings()
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(Root + "/Player/BehaviorAcceptanceState.cs");
            Assert.That(script, Is.Not.Null);
            Assert.That(script.GetClass(), Is.Not.Null);
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
            string className = script.GetClass().FullName;
            var asset = CreateAsset();
            var node = new FsmStateNodeData { name = "BoundState", isDefaultState = true };
            var bound = new FsmStateScriptInfo { guid = guid, className = "OldClassName" };
            node.stateScripts.Add(bound);
            asset.states.Add(node);
            node.RefreshStateScripts(asset);
            Assert.That(bound.guid, Is.EqualTo(guid));
            Assert.That(bound.className, Is.EqualTo(className), "An explicit GUID remains the current authoring binding identity.");
            var classNameOnly = new FsmStateScriptInfo { guid = string.Empty, className = className };
            node.stateScripts.Clear();
            node.stateScripts.Add(classNameOnly);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            string before = EditorJsonUtility.ToJson(asset);
            node.RefreshStateScripts(asset);
            Assert.That(classNameOnly.guid, Is.Empty, "Inspector refresh must not migrate old className-only data by scanning all state scripts.");
            Assert.That(EditorJsonUtility.ToJson(asset), Is.EqualTo(before));
            Assert.That(EditorUtility.IsDirty(asset), Is.False);
        }

        [UnityTest]
        public IEnumerator PendingBindingUsesGuidAndKeepsCanonicalBindingsUnique()
        {
            var pendingType = typeof(FsmStateCreator).Assembly.GetType("YLFramework.Editor.StateMachine.PendingFsmStateBindings", true);
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var load = pendingType.GetMethod("Load", flags);
            var save = pendingType.GetMethod("Save", flags);
            var schedule = pendingType.GetMethod("Schedule", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var pending = load.Invoke(null, null);
            var itemsField = pending.GetType().GetField("items");
            Assert.That(((IList)itemsField.GetValue(pending)).Count, Is.Zero, "Do not modify another user's pending script creation.");
            string key = (string)pendingType.GetProperty("Key", flags).GetValue(null);
            string previous = SessionState.GetString(key, "<absent>");
            var asset = CreateAsset();
            string assetGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
            string scriptPath = Root + "/Player/BehaviorAcceptanceState.cs";
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            Assert.That(script && script.GetClass() != null, Is.True);
            string guid = AssetDatabase.AssetPathToGUID(scriptPath), className = script.GetClass().FullName;
            var node = new FsmStateNodeData { name = "Pending", isDefaultState = true };
            var classNameOnly = new FsmStateScriptInfo { guid = string.Empty, className = className };
            node.stateScripts.Add(classNameOnly);
            asset.states.Add(node);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            try
            {
                schedule.Invoke(null, new object[] { asset, node, scriptPath });
                for (int i = 0; i < 30 && node.stateScripts.All(info => info.guid != guid); i++)
                {
                    yield return null;
                }

                Assert.That(node.stateScripts.Count(info => info.guid == guid), Is.EqualTo(1), "A className-only record must not masquerade as the imported script GUID.");
                Assert.That(classNameOnly.guid, Is.Empty, "Auto-binding does not migrate unsupported old entries.");
                Assert.That(node.stateScripts.Count, Is.EqualTo(2));

                // 旧记录仅用于反例，不把无GUID数据当作有效的Editor配置保留到下一阶段。
                node.stateScripts.Remove(classNameOnly);
                EditorUtility.SetDirty(asset);
                schedule.Invoke(null, new object[] { asset, node, scriptPath });
                schedule.Invoke(null, new object[] { asset, node, scriptPath });
                for (int i = 0; i < 4; i++)
                {
                    yield return null;
                }

                Assert.That(node.stateScripts.Count, Is.EqualTo(1));
                Assert.That(node.stateScripts[0].guid, Is.EqualTo(guid));
                Assert.That(node.stateScripts[0].className, Is.EqualTo(className));
                Assert.That(((IList)itemsField.GetValue(load.Invoke(null, null))).Count, Is.Zero);
                AssetDatabase.SaveAssetIfDirty(asset);
                string path = AssetDatabase.GetAssetPath(asset);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
                reloaded.ClearCache();
                Assert.That(reloaded.GetStateNodeData("Pending").stateScripts.Count, Is.EqualTo(1));
                Assert.That(reloaded.GetStateNodeData("Pending").stateScripts[0].guid, Is.EqualTo(guid));
            }
            finally
            {
                pending = load.Invoke(null, null);
                var items = (IList)itemsField.GetValue(pending);
                bool removed = false;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if ((string)items[i].GetType().GetField("assetGuid").GetValue(items[i]) == assetGuid)
                    {
                        items.RemoveAt(i);
                        removed = true;
                    }
                }

                if (removed)
                {
                    save.Invoke(null, new[] { pending });
                }

                if (items.Count == 0)
                {
                    if (previous == "<absent>")
                    {
                        SessionState.EraseString(key);
                    }
                    else
                    {
                        SessionState.SetString(key, previous);
                    }
                }

                Undo.ClearUndo(asset);
            }
        }

        [Test]
        public void StateTransitionHierarchyAndConditionGroupsSurviveNativeAssetSaveImport()
        {
            var asset = CreateAsset();
            var root = FsmStateNodeFactory.CreateStateNode(asset, "Root", new Rect(0, 0, 200, 40), true, true);
            FsmStateNodeFactory.CreateStateNode(asset, "Idle", new Rect(0, 100, 200, 40), true, false, new List<string> { root.name });
            FsmStateNodeFactory.CreateStateNode(asset, "Run", new Rect(200, 100, 200, 40), false, false, new List<string> { root.name });
            asset.AddParameters(new FsmParameterData { name = "Go", parameterType = ParameterType.Bool });
            FsmTransitionFactory.CreateTransition(asset, "Idle", "Run");
            var transition = asset.GetTransitionData("Idle", "Run");
            transition.conditions.Add(new FsmConditionData { parameterName = "Go", targetValue = 1 });
            transition.groupConditions.Add(new GroupCondition { conditions = new List<FsmConditionData> { new FsmConditionData { parameterName = "Go", targetValue = 0 } } });
            asset.Save();
            string path = AssetDatabase.GetAssetPath(asset);
            string guid = AssetDatabase.AssetPathToGUID(path);
            AssetDatabase.SaveAssetIfDirty(asset);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
            reloaded.ClearCache();
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(reloaded.GetDefaultStateNodeData("Root").name, Is.EqualTo("Idle"));
            Assert.That(reloaded.GetTransitionData("Idle", "Run").groupConditions[0].conditions[0].parameterName, Is.EqualTo("Go"));
            Assert.That(reloaded.ValidateConfiguration(out string error), Is.True, error);
        }

        [Test]
        public void RenamingStateAndItsTransitionsSupportsUndoRedo()
        {
            var asset = CreateAsset();
            var idle = FsmStateNodeFactory.CreateStateNode(asset, "Idle", new Rect(0, 0, 200, 40), true);
            FsmStateNodeFactory.CreateStateNode(asset, "Run", new Rect(200, 0, 200, 40), false);
            FsmTransitionFactory.CreateTransition(asset, "Idle", "Run");
            Undo.FlushUndoRecordObjects();
            Undo.ClearUndo(asset);
            Undo.IncrementCurrentGroup();
            Assert.That(FsmStateNodeFactory.Rename(asset, idle, "Rest"), Is.True);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            asset.ClearCache();
            Assert.That(asset.GetStateNodeData("Idle"), Is.Not.Null);
            Assert.That(asset.transitions[0].fromStateName, Is.EqualTo("Idle"));
            Undo.PerformRedo();
            asset.ClearCache();
            Assert.That(asset.GetStateNodeData("Rest"), Is.Not.Null);
            Assert.That(asset.transitions[0].fromStateName, Is.EqualTo("Rest"));
        }

        private static void CheckUndoRedo(RuntimeFsmController asset, Action change, Action before, Action after)
        {
            Undo.FlushUndoRecordObjects();
            Undo.ClearUndo(asset);
            Undo.IncrementCurrentGroup();
            before();
            change();
            Undo.FlushUndoRecordObjects();
            after();
            Undo.PerformUndo();
            asset.ClearCache();
            before();
            Undo.PerformRedo();
            asset.ClearCache();
            after();
        }

        [Test]
        public void ConditionValuesGroupsAndSerializedReorderingSupportUndoRedo()
        {
            var asset = CreateAsset();
            var untouched = CreateAsset();
            AssetDatabase.SaveAssetIfDirty(untouched);
            asset.states.Add(new FsmStateNodeData { name = "Idle", isDefaultState = true });
            asset.states.Add(new FsmStateNodeData { name = "Run" });
            asset.parameters.Add(new FsmParameterData { name = "Count", parameterType = ParameterType.Int });
            asset.transitions.Add(new FsmTransitionData { fromStateName = "Idle", toStateName = "Run" });
            Func<FsmTransitionData> transition = () => asset.GetTransitionData("Idle", "Run");
            CheckUndoRedo(asset, () => FsmTransitionFactory.SetAutoSwitch(asset, transition(), true),
                () => Assert.That(transition().autoSwitch, Is.False), () => Assert.That(transition().autoSwitch, Is.True));
            CheckUndoRedo(asset, () => FsmConditionFactory.AddGroup(asset, transition()),
                () => Assert.That(transition().groupConditions.Count, Is.Zero), () => Assert.That(transition().groupConditions.Count, Is.EqualTo(1)));
            Func<GroupCondition> group = () => transition().groupConditions[0];
            CheckUndoRedo(asset, () => FsmConditionFactory.AddGroupCondition(asset, group()),
                () => Assert.That(group().conditions.Count, Is.Zero), () => Assert.That(group().conditions.Count, Is.EqualTo(1)));
            Func<FsmConditionData> condition = () => group().conditions[0];
            float old = condition().targetValue;
            CheckUndoRedo(asset, () => FsmConditionFactory.SetTargetValue(asset, condition(), 7),
                () => Assert.That(condition().targetValue, Is.EqualTo(old)), () => Assert.That(condition().targetValue, Is.EqualTo(7)));
            var oldComparison = condition().compareType;
            CheckUndoRedo(asset, () => FsmConditionFactory.SetComparison(asset, condition(), CompareType.Less),
                () => Assert.That(condition().compareType, Is.EqualTo(oldComparison)), () => Assert.That(condition().compareType, Is.EqualTo(CompareType.Less)));
            CheckUndoRedo(asset, () => FsmConditionFactory.RemoveGroupCondition(asset, group(), 0),
                () => Assert.That(group().conditions.Count, Is.EqualTo(1)), () => Assert.That(group().conditions.Count, Is.Zero));
            CheckUndoRedo(asset, () => FsmConditionFactory.RemoveGroup(asset, transition(), group()),
                () => Assert.That(transition().groupConditions.Count, Is.EqualTo(1)), () => Assert.That(transition().groupConditions.Count, Is.Zero));

            transition().conditions.Add(new FsmConditionData { parameterName = "Count", targetValue = 1 });
            transition().conditions.Add(new FsmConditionData { parameterName = "Count", targetValue = 2 });
            transition().groupConditions.Add(new GroupCondition
            {
                conditions = new List<FsmConditionData>
            {
                new FsmConditionData { parameterName = "Count", targetValue = 1 },
                new FsmConditionData { parameterName = "Count", targetValue = 2 }
            }
            });
            asset.Save();
            foreach (bool nested in new[] { false, true })
            {
                Func<float> firstValue = () => nested ? transition().groupConditions[0].conditions[0].targetValue : transition().conditions[0].targetValue;
                CheckUndoRedo(asset, () =>
                {
                    using (var serialized = new SerializedObject(asset))
                    {
                        var property = serialized.FindProperty("transitions").GetArrayElementAtIndex(0);
                        if (nested)
                        {
                            property = property.FindPropertyRelative(nameof(FsmTransitionData.groupConditions)).GetArrayElementAtIndex(0);
                        }

                        property.FindPropertyRelative("conditions").MoveArrayElement(0, 1);
                        serialized.ApplyModifiedProperties();
                    }
                    asset.ClearCache();
                }, () => Assert.That(firstValue(), Is.EqualTo(1)), () => Assert.That(firstValue(), Is.EqualTo(2)));
            }
            Assert.That(EditorUtility.IsDirty(untouched), Is.False);
        }

        [Test]
        public void MissingParameterDoesNotHideEditableEdgesOrBreakTheirInspectorLookup()
        {
            var asset = CreateAsset();
            asset.states.Add(new FsmStateNodeData { name = "Idle", isDefaultState = true });
            asset.states.Add(new FsmStateNodeData { name = "Run" });
            var edge = new FsmTransitionData { fromStateName = "Idle", toStateName = "Run" };
            edge.conditions.Add(new FsmConditionData());
            asset.transitions.Add(edge);
            Assert.That(asset.ValidateConfiguration(out _), Is.False);
            Assert.That(asset.GetCurrentShowTransitionData(string.Empty), Does.Contain(edge));
            Assert.That(asset.GetParameterData(null), Is.Null);
            Assert.That(asset.GetParameterData(string.Empty), Is.Null);
        }

        [Test]
        public void ActualScriptCreatorRefusesAnOccupiedUnimportedPath()
        {
            string directory = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "Behavior.Offline", "Creator_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
            try
            {
                string path = Path.Combine(directory, "OccupiedUnimported.cs");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "class DifferentTypeName {}\n");
                byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                var create = typeof(FsmStateCreator).GetMethod("CreateFsmState", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.That(create.Invoke(null, new object[] { path, false, null, null }), Is.EqualTo(false));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(directory), true);
            }
        }

        public sealed class DrawerScopeWindow : EditorWindow
        {
            [global::UnityEngine.Serialization.FormerlySerializedAs("Host")]
            public FsmController host;
            [global::UnityEngine.Serialization.FormerlySerializedAs("Complete")]
            public bool isComplete;
            public Exception Failure;
            private void OnGUI()
            {
                if (!host || isComplete)
                {
                    return;
                }

                try
                {
                    using (var serialized = new SerializedObject(host))
                    using (new EditorGUI.DisabledScope(true))
                    {
                        bool before = GUI.enabled;
                        var layers = serialized.FindProperty("runtimeFsmControllerLayers");
                        var drawer = new CustomRuntimeFsmControllerLayer();
                        for (int i = 0; i < layers.arraySize; i++)
                        {
                            drawer.OnGUI(new Rect(0, 25 * i, 400, 20), layers.GetArrayElementAtIndex(i), GUIContent.none);
                        }

                        if (GUI.enabled != before)
                        {
                            throw new InvalidOperationException("Drawer consumed the caller's disabled scope.");
                        }
                    }
                }
                catch (Exception error)
                {
                    Failure = error;
                }
                isComplete = true;
            }
        }

        public sealed class TransitionInspectorWindow : EditorWindow
        {
            [global::UnityEngine.Serialization.FormerlySerializedAs("Helper")]
            public FsmTransitionInspectorHelper helper;
            [global::UnityEngine.Serialization.FormerlySerializedAs("NodeHelper")]
            public FsmStateInspectorHelper nodeHelper;
            [global::UnityEngine.Serialization.FormerlySerializedAs("Inspector")]
            public UnityEditor.Editor inspector;
            public Exception Failure;
            [global::UnityEngine.Serialization.FormerlySerializedAs("DrawCount")]
            public int drawCount;
            public void Initialize(RuntimeFsmController asset, FsmTransitionData edge)
            {
                helper = ScriptableObject.CreateInstance<FsmTransitionInspectorHelper>();
                helper.controller = asset;
                helper.transition = edge;
                inspector = UnityEditor.Editor.CreateEditor(helper);
            }
            public void InitializeNode(RuntimeFsmController asset, FsmStateNodeData node)
            {
                nodeHelper = ScriptableObject.CreateInstance<FsmStateInspectorHelper>();
                nodeHelper.controller = asset;
                nodeHelper.node = node;
                inspector = UnityEditor.Editor.CreateEditor(nodeHelper);
            }
            private void OnGUI()
            {
                if (!inspector || Failure != null)
                {
                    return;
                }

                try
                {
                    if (nodeHelper)
                    {
                        inspector.DrawHeader();
                    }
                    inspector.OnInspectorGUI();
                    drawCount++;
                }
                catch (Exception error)
                {
                    Failure = error;
                }
            }
            private void OnDisable()
            {
                if (inspector)
                {
                    DestroyImmediate(inspector);
                }

                if (helper)
                {
                    DestroyImmediate(helper);
                }

                if (nodeHelper)
                {
                    DestroyImmediate(nodeHelper);
                }
            }
        }

        [UnityTest]
        public IEnumerator ClearedStateInspectorStillDrawsHeaderWithoutException()
        {
            var asset = CreateAsset();
            var node = new FsmStateNodeData { name = "OwnedNode" };
            asset.states.Add(node);
            var window = ScriptableObject.CreateInstance<TransitionInspectorWindow>();
            try
            {
                window.InitializeNode(asset, node);
                window.ShowUtility();
                for (int i = 0; i < 10 && window.drawCount == 0; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.drawCount, Is.GreaterThan(0));
                Assert.That(window.Failure, Is.Null);
                int before = window.drawCount;
                window.nodeHelper.Inspect(asset, null, null);
                for (int i = 0; i < 10 && window.drawCount == before && window.Failure == null; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Failure, Is.Null, "A locked Inspector header must accept its cleared node selection.");
                Assert.That(window.drawCount, Is.GreaterThan(before));
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SelectedTransitionInspectorSurvivesDeletionAndUndoRedo()
        {
            var asset = CreateAsset();
            var edge = new FsmTransitionData { fromStateName = "A", toStateName = "B" };
            edge.conditions.Add(new FsmConditionData());
            asset.transitions.Add(edge);
            var window = ScriptableObject.CreateInstance<TransitionInspectorWindow>();
            try
            {
                window.Initialize(asset, edge);
                window.ShowUtility();
                for (int i = 0; i < 10 && window.drawCount == 0; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.drawCount, Is.GreaterThan(0));
                Assert.That(window.Failure, Is.Null);
                int before = window.drawCount;
                Undo.IncrementCurrentGroup();
                FsmTransitionFactory.DeleteTransition(asset, edge);
                Undo.FlushUndoRecordObjects();
                for (int i = 0; i < 10 && window.drawCount == before && window.Failure == null; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Failure, Is.Null, "An open or locked Inspector must not draw a removed SerializedProperty.");
                Assert.That(window.drawCount, Is.GreaterThan(before));
                Undo.PerformUndo();
                asset.ClearCache();
                Assert.That(asset.GetTransitionData("A", "B"), Is.Not.Null);
                window.Repaint();
                yield return null;
                Assert.That(window.Failure, Is.Null);
                Undo.PerformRedo();
                window.Repaint();
                yield return null;
                Assert.That(window.Failure, Is.Null);
            }
            finally
            {
                window.Close();
                Undo.ClearUndo(asset);
            }
        }

        [UnityTest]
        public IEnumerator TransitionInspectorRebindsAfterEarlierEdgeRemovalAndSelectionChange()
        {
            var asset = CreateAsset();
            var first = new FsmTransitionData { fromStateName = "A", toStateName = "B" };
            var second = new FsmTransitionData { fromStateName = "B", toStateName = "C" };
            second.groupConditions.Add(new GroupCondition());
            second.groupConditions[0].conditions.Add(new FsmConditionData());
            asset.transitions.Add(first);
            asset.transitions.Add(second);
            var window = ScriptableObject.CreateInstance<TransitionInspectorWindow>();
            try
            {
                window.Initialize(asset, second);
                window.ShowUtility();
                for (int i = 0; i < 10 && window.drawCount == 0; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Failure, Is.Null);
                int before = window.drawCount;
                FsmTransitionFactory.DeleteTransition(asset, first);
                for (int i = 0; i < 10 && window.drawCount == before && window.Failure == null; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Failure, Is.Null);
                var listField = typeof(FsmTransitionInspector).GetField("_reorderableList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(((UnityEditorInternal.ReorderableList)listField.GetValue(window.inspector)).serializedProperty.propertyPath, Is.EqualTo("transitions.Array.data[0].conditions"));
                var group = ((FsmTransitionInspector)window.inspector).GetReorderableList(second.groupConditions[0]);
                Assert.That(group.serializedProperty.propertyPath, Is.EqualTo($"transitions.Array.data[0].{nameof(FsmTransitionData.groupConditions)}.Array.data[0].conditions"));
                asset.transitions.Add(first);
                asset.ClearCache();
                window.helper.transition = first;
                before = window.drawCount;
                for (int i = 0; i < 10 && window.drawCount == before; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Failure, Is.Null);
                Assert.That(((UnityEditorInternal.ReorderableList)listField.GetValue(window.inspector)).serializedProperty.propertyPath, Is.EqualTo("transitions.Array.data[1].conditions"));
            }
            finally
            {
                window.Close();
                Undo.ClearUndo(asset);
            }
        }

        [Test]
        public void DeleteKeyRemovesSelectedStatesAndTheirTransitionsWithOneUndo()
        {
            var window = ScriptableObject.CreateInstance<GraphPreviewWindow>();
            var selection = Selection.instanceIDs;
            var nodes = new List<FsmNodeView>();
            try
            {
                window.ShowUtility();
                window.Initialize();
                var graph = window.Graph;
                var asset = window.Source;
                nodes.AddRange(graph.StateNodes.Values);
                graph.AddToSelection(graph.GetNode("Idle"));
                graph.AddToSelection(graph.GetNode("Nested"));
                graph.Focus();
                var keyTarget = graph.GetNode("Idle").Q<IMGUIContainer>();
                using (var key = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Delete }))
                {
                    keyTarget.SendEvent(key);
                }

                Undo.FlushUndoRecordObjects();
                Assert.That(asset.states.Select(state => state.name), Is.EquivalentTo(new[] { "Entry", "Any State" }));
                Assert.That(asset.transitions, Is.Empty);
                Undo.PerformUndo();
                asset.ClearCache();
                Assert.That(asset.GetStateNodeData("Idle"), Is.Not.Null);
                Assert.That(asset.GetStateNodeData("Nested"), Is.Not.Null);
                Assert.That(asset.GetTransitionData("Any State", "Nested"), Is.Not.Null);
                Undo.PerformRedo();
                asset.ClearCache();
                Assert.That(asset.GetStateNodeData("Idle"), Is.Null);
                Assert.That(asset.GetStateNodeData("Nested"), Is.Null);
                Undo.ClearUndo(asset);
            }
            finally
            {
                if (window.Graph != null)
                {
                    nodes.AddRange(window.Graph.StateNodes.Values);
                }

                foreach (var node in nodes.Distinct())
                {
                    EditorApplication.delayCall -= (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), node, "DelayCallSelection");
                }

                window.Close();
                Selection.instanceIDs = selection;
            }
        }

        [Test]
        public void DeleteCommandKeepsBuiltInStatesAndIgnoresTextFieldDelete()
        {
            var window = ScriptableObject.CreateInstance<GraphPreviewWindow>();
            var selection = Selection.instanceIDs;
            var nodes = new List<FsmNodeView>();
            try
            {
                window.ShowUtility();
                window.Initialize();
                var graph = window.Graph;
                var asset = window.Source;
                nodes.AddRange(graph.StateNodes.Values);
                graph.AddToSelection(graph.GetNode("Entry"));
                graph.AddToSelection(graph.GetNode("Any State"));
                Assert.That(graph.deleteSelection, Is.Not.Null);
                graph.deleteSelection("Delete", GraphView.AskUser.DontAskUser);
                Assert.That(asset.states.Count, Is.EqualTo(4));
                graph.ClearSelection();
                graph.AddToSelection(graph.GetNode("Idle"));
                var text = new TextField { value = "Do not delete the selected state while typing" };
                graph.Add(text);
                text.Focus();
                using (var key = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Delete }))
                {
                    text.SendEvent(key);
                }

                Assert.That(asset.GetStateNodeData("Idle"), Is.Not.Null);
            }
            finally
            {
                if (window.Graph != null)
                {
                    nodes.AddRange(window.Graph.StateNodes.Values);
                }

                foreach (var node in nodes.Distinct())
                {
                    EditorApplication.delayCall -= (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), node, "DelayCallSelection");
                }

                window.Close();
                Selection.instanceIDs = selection;
            }
        }

        public sealed class GraphPreviewWindow : EditorWindow
        {
            public FsmStateGraphView Graph;
            public RuntimeFsmController Source => _source;
            private Context _previous;
            private Context _localContext;
            private RuntimeFsmController _source;
            private System.Reflection.FieldInfo _contextField;
            private readonly string[] _integerKeys = { "YL.AssetFSMControllerInstanceID", "FSMControllerIndex" };
            private bool[] _hadIntegerKeys;
            private int[] _previousIntegers;
            private bool _hadGuid;
            private string _previousGuid;

            public void Initialize()
            {
                _hadIntegerKeys = _integerKeys.Select(EditorPrefs.HasKey).ToArray();
                _previousIntegers = _integerKeys.Select(key => EditorPrefs.GetInt(key)).ToArray();
                _hadGuid = EditorPrefs.HasKey("YL.AssetFSMRuntimeFSMControllerGUID");
                _previousGuid = EditorPrefs.GetString("YL.AssetFSMRuntimeFSMControllerGUID");
                foreach (string key in _integerKeys)
                {
                    EditorPrefs.SetInt(key, 0);
                }

                _contextField = typeof(Context).GetField("_instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                _previous = (Context)_contextField.GetValue(null);
                var context = (Context)Activator.CreateInstance(typeof(Context), true);
                _localContext = context;
                _source = ScriptableObject.CreateInstance<RuntimeFsmController>();
                _source.states.Add(new FsmStateNodeData { name = "Entry", isBuiltInState = true, builtInStateName = "Entry", rect = new Rect(80, 280, 200, 40) });
                _source.states.Add(new FsmStateNodeData { name = "Any State", isBuiltInState = true, builtInStateName = "Any State", rect = new Rect(80, 80, 200, 40) });
                _source.states.Add(new FsmStateNodeData { name = "Idle", isDefaultState = true, rect = new Rect(440, 280, 200, 40) });
                _source.states.Add(new FsmStateNodeData { name = "Nested", isSubStateMachine = true, rect = new Rect(440, 80, 200, 40) });
                _source.transitions.Add(new FsmTransitionData { fromStateName = "Any State", toStateName = "Nested" });
                typeof(Context).GetField("_runtimeFsmController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(context, _source);
                _contextField.SetValue(null, context);
                Graph = new FsmStateGraphView();
                Graph.style.flexGrow = 1;
                rootVisualElement.Add(Graph);
            }

            public void RestoreUserContextBetweenInputEvents()
            {
                _contextField.SetValue(null, _previous);
                RestorePreferences();
            }

            public bool SendNativeGraphEvent(Event input)
            {
                bool handled = false;
                Debug.Log("[YL NativeInput] Sending " + input.type + ": button=" + input.button + ", key=" + input.keyCode);
                InGraphContext(() => handled = SendEvent(input));
                Debug.Log("[YL NativeInput] Dispatched " + input.type);
                return handled;
            }

            public void ExecuteGraphMenuAction(DropdownMenuAction action) => InGraphContext(action.Execute);

            private void InGraphContext(Action operation)
            {
                var current = _contextField.GetValue(null);
                int host = EditorPrefs.GetInt(_integerKeys[0]);
                int index = EditorPrefs.GetInt(_integerKeys[1]);
                bool hadHost = EditorPrefs.HasKey(_integerKeys[0]), hadIndex = EditorPrefs.HasKey(_integerKeys[1]);
                try
                {
                    EditorPrefs.SetInt(_integerKeys[0], 0);
                    EditorPrefs.SetInt(_integerKeys[1], 0);
                    _contextField.SetValue(null, _localContext);
                    operation();
                }
                finally
                {
                    _contextField.SetValue(null, current);
                    if (hadHost)
                    {
                        EditorPrefs.SetInt(_integerKeys[0], host);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(_integerKeys[0]);
                    }

                    if (hadIndex)
                    {
                        EditorPrefs.SetInt(_integerKeys[1], index);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(_integerKeys[1]);
                    }
                }
            }

            private void RestorePreferences()
            {
                if (_hadIntegerKeys == null)
                {
                    return;
                }

                for (int i = 0; i < _integerKeys.Length; i++)
                {
                    if (_hadIntegerKeys[i])
                    {
                        EditorPrefs.SetInt(_integerKeys[i], _previousIntegers[i]);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(_integerKeys[i]);
                    }
                }

                if (_hadGuid)
                {
                    EditorPrefs.SetString("YL.AssetFSMRuntimeFSMControllerGUID", _previousGuid);
                }
                else
                {
                    EditorPrefs.DeleteKey("YL.AssetFSMRuntimeFSMControllerGUID");
                }
            }

            private void OnDisable()
            {
                rootVisualElement.Clear();
                if (_contextField != null)
                {
                    _contextField.SetValue(null, _previous);
                }

                if (_source)
                {
                    DestroyImmediate(_source);
                }

                RestorePreferences();
            }
        }

        [UnityTest]
        [Category("YL.Special.NativeInput")]
        public IEnumerator NativeWindowMouseSelectionRoutesDeleteToTheSelectedNode()
        {
            var window = ScriptableObject.CreateInstance<GraphPreviewWindow>();
            try
            {
                Debug.Log("[YL NativeInput] Opening selection window");
                window.Initialize();
                Debug.Log("[YL NativeInput] Selection graph initialized");
                window.ShowUtility();
                Debug.Log("[YL NativeInput] Selection utility shown");
                window.position = new Rect(160, 120, 800, 500);
                window.RestoreUserContextBetweenInputEvents();
                for (int i = 0; i < 8; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var graph = window.Graph;
                Debug.Log("[YL NativeInput] Selection window laid out");
                var node = graph.GetNode("Idle");
                var point = node.worldBound.center;
                var previousFocus = new TextField { value = "Focus starts outside the graph" };
                previousFocus.style.height = 20;
                window.rootVisualElement.Add(previousFocus);
                window.Focus();
                previousFocus.Focus();
                window.SendNativeGraphEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = point });
                window.SendNativeGraphEvent(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = point });
                Assert.That(node.selected, Is.True, "A native window mouse click must actually select the node.");
                var focused = window.rootVisualElement.focusController.focusedElement as VisualElement;
                Assert.That(focused != null && (focused == graph || graph.Contains(focused)), Is.True, "Mouse selection must retain keyboard focus inside the graph, not in the IMGUI label or a sibling field.");
                Assert.That(focused, Is.Not.InstanceOf<IMGUIContainer>());
                window.SendNativeGraphEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Delete });
                window.SendNativeGraphEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.Delete });
                Assert.That(window.Source.states.Any(state => state.name == "Idle"), Is.False, "Delete must arrive through the window dispatch/focus chain without graph.Focus or graph.SendEvent.");
            }
            finally
            {
                if (window.Source)
                {
                    Undo.ClearUndo(window.Source);
                }

                window.Close();
            }
        }

        [UnityTest]
        [Category("YL.Special.NativeInput")]
        public IEnumerator NativeBackgroundRightClickOffersStateAndSubStateCreation()
        {
            var window = ScriptableObject.CreateInstance<GraphPreviewWindow>();
            try
            {
                window.Initialize();
                window.ShowUtility();
                window.position = new Rect(160, 120, 800, 500);
                window.Graph.style.marginLeft = 120;
                window.Graph.style.marginTop = 50;
                window.RestoreUserContextBetweenInputEvents();
                for (int i = 0; i < 8; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var graph = window.Graph;
                graph.UpdateViewTransform(new Vector3(65, 35), Vector3.one * 0.7f);
                for (int i = 0; i < 3; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var point = new Vector2(graph.worldBound.xMax - 45, graph.worldBound.yMax - 45);
                var expectedPosition = graph.contentViewContainer.WorldToLocal(point);
                Assert.That(graph.panel.Pick(point), Is.SameAs(graph), "A full-viewport decorative element must not intercept background context clicks.");
                List<DropdownMenuAction> actions = null;
                graph.RegisterCallback<ContextualMenuPopulateEvent>(evt =>
                {
                    actions = evt.menu.MenuItems().OfType<DropdownMenuAction>().ToList();
                    evt.PreventDefault();
                    // Unity 6 的 PostDispatch 不再检查 PreventDefault；保留原动作，仅阻止阻塞式菜单显示。
                    evt.menu.MenuItems().Clear();
                    evt.StopImmediatePropagation();
                });
                window.Focus();
                window.SendNativeGraphEvent(new Event { type = EventType.MouseDown, button = 1, clickCount = 1, mousePosition = point });
                window.SendNativeGraphEvent(new Event { type = EventType.MouseUp, button = 1, clickCount = 1, mousePosition = point });
                Assert.That(actions, Is.Not.Null, "Right mouse-up must invoke native ContextualMenuPopulateEvent, not a direct call to BuildContextualMenu.");
                CollectionAssert.Contains(actions.Select(action => action.name), "Create State");
                CollectionAssert.Contains(actions.Select(action => action.name), "Create Sub-State Machine");
                var before = window.Source.states.ToArray();
                window.ExecuteGraphMenuAction(actions.Single(action => action.name == "Create State"));
                var created = window.Source.states.Single(state => !before.Contains(state));
                Assert.That(created.isSubStateMachine, Is.False);
                Assert.That(Mathf.Abs(created.rect.center.x - expectedPosition.x), Is.LessThan(20.01f), "State creation must convert the panel click position once, including the graph dock offset and zoom.");
                Assert.That(Mathf.Abs(created.rect.center.y - expectedPosition.y), Is.LessThan(20.01f));
                before = window.Source.states.ToArray();
                window.ExecuteGraphMenuAction(actions.Single(action => action.name == "Create Sub-State Machine"));
                var sub = window.Source.states.Single(state => !before.Contains(state) && state.isSubStateMachine);
                Assert.That(window.Source.states.Count(state => !before.Contains(state) && state.isBuiltInState && state.parents.Contains(sub.name)), Is.EqualTo(3));
            }
            finally
            {
                if (window.Source)
                {
                    Undo.ClearUndo(window.Source);
                }
                window.Close();
            }
        }

        [Test]
        public void GraphPreviewDoesNotResolveThePreviouslySelectedUserHost()
        {
            string[] keys = { "YL.AssetFSMControllerInstanceID", "FSMControllerIndex" };
            var had = keys.Select(EditorPrefs.HasKey).ToArray();
            var values = keys.Select(key => EditorPrefs.GetInt(key)).ToArray();
            string guidKey = "YL.AssetFSMRuntimeFSMControllerGUID";
            bool hadGuid = EditorPrefs.HasKey(guidKey);
            string guid = EditorPrefs.GetString(guidKey);
            var hostObject = new GameObject("OwnedPreviousGraphHost");
            _owned.Add(hostObject);
            var asset = CreateAsset();
            asset.states.Add(new FsmStateNodeData { name = "DoNotSnap", isDefaultState = true, rect = new Rect(3, 7, 173, 37) });
            var host = hostObject.AddComponent<FsmController>();
            host.AddRuntimeFsmController(asset, "Previous");
            var window = ScriptableObject.CreateInstance<GraphPreviewWindow>();
            try
            {
                EditorPrefs.SetInt(keys[0], hostObject.GetInstanceID());
                EditorPrefs.SetInt(keys[1], 3);
                window.ShowUtility();
                window.Initialize();
                Assert.That(Context.Instance.RuntimeFsmController, Is.SameAs(window.Source));
                Assert.That(window.Graph.StateNodes.ContainsKey("DoNotSnap"), Is.False);
                window.Close();
                Assert.That(EditorPrefs.GetInt(keys[0]), Is.EqualTo(hostObject.GetInstanceID()));
                Assert.That(EditorPrefs.GetInt(keys[1]), Is.EqualTo(3));
                Assert.That(asset.states[0].rect, Is.EqualTo(new Rect(3, 7, 173, 37)));
            }
            finally
            {
                if (window)
                {
                    window.Close();
                }

                for (int i = 0; i < keys.Length; i++)
                {
                    if (had[i])
                    {
                        EditorPrefs.SetInt(keys[i], values[i]);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(keys[i]);
                    }
                }

                if (hadGuid)
                {
                    EditorPrefs.SetString(guidKey, guid);
                }
                else
                {
                    EditorPrefs.DeleteKey(guidKey);
                }
            }
        }

        [UnityTest]
        public IEnumerator GraphGridFillsViewportBehindContentAtDifferentZooms()
        {
            var window = ScriptableObject.CreateInstance<GraphPreviewWindow>();
            try
            {
                window.Initialize();
                window.position = new Rect(120, 120, 760, 480);
                window.ShowUtility();
                window.RestoreUserContextBetweenInputEvents();
                for (int i = 0; i < 10; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var graph = window.Graph;
                var grid = graph.Q<GridBackground>();
                Assert.That(grid, Is.Not.Null);
                Assert.That(grid.parent, Is.SameAs(graph), "Grid must be an unscaled GraphView background, not a content child.");
                foreach (float zoom in new[] { 0.5f, 1f, 2f })
                {
                    graph.UpdateViewTransform(new Vector3(30, 20), Vector3.one * zoom);
                    for (int i = 0; i < 4; i++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                    Assert.That(grid.layout.width, Is.EqualTo(graph.layout.width).Within(1));
                    Assert.That(grid.layout.height, Is.EqualTo(graph.layout.height).Within(1));
                    Assert.That(grid.worldBound.size, Is.EqualTo(graph.worldBound.size));
                    Assert.That(grid.customStyle.TryGetValue(new CustomStyleProperty<Color>("--line-color"), out var minor), Is.True, "The dark/light theme must define visible grid lines.");
                    Assert.That(grid.customStyle.TryGetValue(new CustomStyleProperty<Color>("--grid-background-color"), out var background), Is.True);
                    Assert.That(Mathf.Abs(minor.grayscale - background.grayscale) * minor.a, Is.GreaterThan(0.035f));
                }
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator GraphTransitionOverlayUsesCrispWidthsAcrossZoomLevels()
        {
            var window = ScriptableObject.CreateInstance<GraphPreviewWindow>();
            try
            {
                window.Initialize();
                window.position = new Rect(120, 120, 760, 480);
                window.ShowUtility();
                window.RestoreUserContextBetweenInputEvents();
                for (int i = 0; i < 10; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                var graph = window.Graph;
                var overlay = graph.Q<FsmTransitionBackground>();
                Assert.That(overlay.parent, Is.SameAs(graph));
                var width = typeof(FsmTransitionBackground).GetProperty("LineWidth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(width, Is.Not.Null, "Transition widths need a bounded screen-space stroke instead of a wide feathered band.");
                foreach (float zoom in new[] { 0.1f, 0.5f, 1f, 3f })
                {
                    graph.UpdateViewTransform(new Vector3(20, 10), Vector3.one * zoom);
                    for (int i = 0; i < 4; i++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                    Assert.That(overlay.layout.width, Is.EqualTo(graph.layout.width).Within(1));
                    Assert.That(overlay.layout.height, Is.EqualTo(graph.layout.height).Within(1));
                    Assert.That((float)width.GetValue(overlay), Is.InRange(1f, 2.5f));
                }
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator LayerDrawerPreservesCallerDisabledScopeAcrossMultipleRows()
        {
            var obj = new GameObject("YL.Behavior.DrawerHost");
            _owned.Add(obj);
            var host = obj.AddComponent<FsmController>();
            host.AddRuntimeFsmController(CreateAsset(), "A");
            host.AddRuntimeFsmController(CreateAsset(), "B");
            var window = ScriptableObject.CreateInstance<DrawerScopeWindow>();
            try
            {
                window.host = host;
                window.ShowUtility();
                window.Repaint();
                for (int i = 0; i < 20 && !window.isComplete; i++)
                {
                    yield return null;
                }

                Assert.That(window.isComplete, Is.True, "GUI gate requires a live Editor window.");
                Assert.That(window.Failure, Is.Null);
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void CurrentFixtureProducerPreservesUnrelatedDirtyAsset()
        {
            string path = _folder + "/Unrelated.mat";
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssetIfDirty(material);
            byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            material.name = "UnsavedUserValue";
            EditorUtility.SetDirty(material);
            var builder = Type.GetType("YLFramework.Behavior.Acceptance.BehaviorAcceptanceBuilder, YLFramework.Behavior.Acceptance.Editor", true);
            builder.GetMethod("CreateEventsPrefab", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { _folder });
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before), "The current producer must save only its own outputs.");
            Assert.That(material.name, Is.EqualTo("UnsavedUserValue"));
            Assert.That(EditorUtility.IsDirty(material), Is.True);
        }

        [Test]
        [Category("YL.Special.BehaviorPlayer")]
        public void CurrentAuthoredBehaviorExecutesPersistentContactsInTwoActualMonoPlayers()
        {
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            var builder = Type.GetType("YLFramework.Behavior.Acceptance.BehaviorAcceptanceBuilder, YLFramework.Behavior.Acceptance.Editor", true);
            string generated = null;
            string scratch = "Assets/__YLBehaviorPlayer_" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(scratch), Is.False);
            Assert.That(AssetDatabase.CreateFolder("Assets", scratch.Substring(7)), Is.Not.Empty);
            try
            {
                Assert.That(Application.isBatchMode, Is.True);
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/YLTestingProfile/Empty.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
                string scenePath = (string)builder.GetMethod("Generate").Invoke(null,
                    new object[] { true, new Action<string>(path => generated = path) });
                string prefab = generated + "/BehaviorEvents.prefab";
                var bytes = File.ReadAllBytes(prefab);
                var meta = File.ReadAllBytes(prefab + ".meta");
                Assert.That(AssetDatabase.AssetPathToGUID(prefab), Is.Not.Empty.And.Not.EqualTo("73dab49f422e8304e846b6bf7838e329"));
                File.Copy(prefab, Path.Combine(output, "Behavior-current.prefab"), false);
                File.Copy(prefab + ".meta", Path.Combine(output, "Behavior-current.prefab.meta"), false);
                global::YLFramework.Testing.EditorFixtures.StandaloneFixturePlayer.BuildAndRun(output, scratch, "YLFramework.Overlap", null, target =>
                {
                    var authored = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
                    try
                    {
                        foreach (var root in authored.GetRootGameObjects())
                            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, target);
                    }
                    finally { UnityEditor.SceneManagement.EditorSceneManager.CloseScene(authored, true); }
                }, new[] { "YL_BEHAVIOR_ACCEPTANCE" });
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(prefab));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(prefab + ".meta"));
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(scratch), Is.True);
                if (generated != null)
                {
                    Assert.That(generated.StartsWith(Root + "/GeneratedAcceptance_", StringComparison.Ordinal), Is.True);
                    Assert.That(AssetDatabase.DeleteAsset(generated), Is.True);
                }
            }
        }

        [Test]
        public void PlayerFixturePersistsGenericEventConsumersThroughNativeSaveAndReload()
        {
            var builder = Type.GetType("YLFramework.Behavior.Acceptance.BehaviorAcceptanceBuilder, YLFramework.Behavior.Acceptance.Editor", true);
            var create = builder.GetMethod("CreateEventsPrefab", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(create, Is.Not.Null, "The Player fixture needs a native serialized event consumer.");
            create.Invoke(null, new object[] { _folder });
            string path = _folder + "/BehaviorEvents.prefab";
            GameObject loaded = null;
            try
            {
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path), Is.Not.Null, "The Player scene must consume serialized event listeners, not runtime AddListener substitutes.");
                string guid = AssetDatabase.AssetPathToGUID(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                loaded = PrefabUtility.LoadPrefabContents(path);
                var receiverType = Type.GetType("YLFramework.Behavior.Acceptance.BehaviorEventReceiver, YLFramework.Behavior.Acceptance", true);
                var receiver = loaded.GetComponentInChildren(receiverType, true);
                Assert.That(receiver, Is.Not.Null);
                var motor = loaded.GetComponentInChildren<CharacterController2D>(true);
                var overlap = loaded.GetComponentInChildren<OverlapDetector2D>(true);
                var sphere = loaded.GetComponentInChildren<OverlapDetector3D>(true);
                Assert.That(overlap && sphere, Is.True, "The current producer must use Detector/Contact, not retired Overlap components.");
                var platform = loaded.GetComponentInChildren<Moveable>(true);
                var events = new UnityEventBase[] { motor.onGroundValueChange, motor.onTouchWallValueChange, motor.onTopValueChange,
                    motor.onJumpCountValueChange, motor.onVelocityValueChange, platform.onMove,
                    overlap.EnteredEvent, overlap.StayedEvent, overlap.ExitedEvent,
                    sphere.EnteredEvent, sphere.StayedEvent, sphere.ExitedEvent };
                string[] methods = { "ReceiveBool", "ReceiveWall", "ReceiveTop", "ReceiveInt", "ReceiveVector2", "ReceiveVector3",
                    "ReceiveHit", "ReceiveStay", "ReceiveExit", "ReceiveHit3D", "ReceiveStay3D", "ReceiveExit3D" };
                for (int i = 0; i < events.Length; i++)
                {
                    Assert.That(events[i].GetPersistentEventCount(), Is.EqualTo(1), methods[i]);
                    Assert.That(events[i].GetPersistentTarget(0), Is.EqualTo(receiver), methods[i]);
                    Assert.That(events[i].GetPersistentMethodName(0), Is.EqualTo(methods[i]));
                    Assert.That(events[i].GetPersistentListenerState(0), Is.EqualTo(UnityEventCallState.RuntimeOnly));
                }
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            }
            finally
            {
                if (loaded)
                {
                    PrefabUtility.UnloadPrefabContents(loaded);
                }
            }
        }

        [Test]
        public void OverlapMotorAndMoveablePersistentEventsSurvivePrefabSaveReload()
        {
            var receiverType = Type.GetType("YLFramework.Behavior.Acceptance.BehaviorEventReceiver, YLFramework.Behavior.Acceptance", true);
            var obj = new GameObject("YL.Behavior.EventPrefab");
            obj.SetActive(false);
            _owned.Add(obj);
            var receiver = obj.AddComponent(receiverType);
            var overlap = obj.AddComponent<OverlapDetector2D>();
            UnityEventTools.AddPersistentListener(overlap.EnteredEvent,
                (UnityAction<OverlapContact<Collider2D>>)Delegate.CreateDelegate(typeof(UnityAction<OverlapContact<Collider2D>>), receiver, "ReceiveHit"));
            UnityEventTools.AddPersistentListener(overlap.ExitedEvent,
                (UnityAction<OverlapContact<Collider2D>, OverlapExitReason>)Delegate.CreateDelegate(typeof(UnityAction<OverlapContact<Collider2D>, OverlapExitReason>), receiver, "ReceiveExit"));
            Assert.That(overlap.EnteredEvent.GetPersistentListenerState(0), Is.EqualTo(UnityEventCallState.RuntimeOnly));
            // 本用例在EditMode实际Invoke；明确序列化为EditorAndRuntime，不把RuntimeOnly的未调用误判为丢失绑定。
            overlap.EnteredEvent.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            overlap.ExitedEvent.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            obj.AddComponent<BoxCollider2D>();
            var motor = obj.AddComponent<CharacterController2D>();
            motor.onGroundValueChange = new YLFramework.Character2D.BoolUnityEvent();
            motor.onJumpCountValueChange = new IntUnityEvent();
            motor.onVelocityValueChange = new Vector2UnityEvent();
            UnityEventTools.AddPersistentListener(motor.onGroundValueChange, (UnityAction<bool>)Delegate.CreateDelegate(typeof(UnityAction<bool>), receiver, "ReceiveBool"));
            UnityEventTools.AddPersistentListener(motor.onJumpCountValueChange, (UnityAction<int>)Delegate.CreateDelegate(typeof(UnityAction<int>), receiver, "ReceiveInt"));
            UnityEventTools.AddPersistentListener(motor.onVelocityValueChange, (UnityAction<Vector2>)Delegate.CreateDelegate(typeof(UnityAction<Vector2>), receiver, "ReceiveVector2"));
            motor.onGroundValueChange.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            motor.onJumpCountValueChange.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            motor.onVelocityValueChange.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            var moveable = obj.AddComponent<Moveable>();
            moveable.Positions.Add(Vector3.zero);
            moveable.Positions.Add(Vector3.right);
            moveable.onMove = new Vector3UnityEvent();
            UnityEventTools.AddPersistentListener(moveable.onMove, (UnityAction<Vector3>)Delegate.CreateDelegate(typeof(UnityAction<Vector3>), receiver, "ReceiveVector3"));
            moveable.onMove.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime);
            string path = _folder + "/Events.prefab";
            Assert.That(PrefabUtility.SaveAsPrefabAsset(obj, path), Is.Not.Null);
            var loaded = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var loadedReceiver = loaded.GetComponent(receiverType);
                var loadedOverlap = loaded.GetComponent<OverlapDetector2D>();
                Assert.That(loadedOverlap.EnteredEvent.GetPersistentEventCount(), Is.EqualTo(1));
                Assert.That(loadedOverlap.EnteredEvent.GetPersistentListenerState(0), Is.EqualTo(UnityEventCallState.EditorAndRuntime));
                loadedOverlap.EnteredEvent.Invoke(default);
                loadedOverlap.ExitedEvent.Invoke(default, OverlapExitReason.TargetDestroyed);
                var loadedMotor = loaded.GetComponent<CharacterController2D>();
                loadedMotor.onGroundValueChange.Invoke(true);
                loadedMotor.onJumpCountValueChange.Invoke(2);
                loadedMotor.onVelocityValueChange.Invoke(Vector2.one);
                loaded.GetComponent<Moveable>().onMove.Invoke(Vector3.one);
                var serialized = new SerializedObject(loadedReceiver);
                Assert.That(serialized.FindProperty("hitCount").intValue, Is.EqualTo(2));
                Assert.That(serialized.FindProperty("exitReason").intValue, Is.EqualTo((int)OverlapExitReason.TargetDestroyed));
                foreach (string name in new[] { "boolCount", "intCount", "vector2Count", "vector3Count" })
                {
                    Assert.That(serialized.FindProperty(name).intValue, Is.EqualTo(1), name);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(loaded);
            }
        }
    }
}
