using System;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Character2D;
using YLFramework.Overlap;
using YLFramework.Platform2D;
using YLFramework.StateMachine;

namespace YLFramework.Behavior.Acceptance
{
    public static class BehaviorAcceptanceBuilder
    {
        private const string Root = "Assets/Tests/YLFramework/Fixtures/Extensions/Behavior";

        [MenuItem("Tools/YLFramework/Behavior Acceptance/Generate Owned Fixture")]
        public static void GenerateMenu() => Generate();

        public static string Generate(bool development = false, Action<string> folderCreated = null)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("Save open scenes before generating the owned fixture. No scene was overwritten.");
                }
            }

            string folder = "GeneratedAcceptance_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(Root, folder);
            folder = Root + "/" + folder;
            folderCreated?.Invoke(folder);
            var asset = ScriptableObject.CreateInstance<RuntimeFsmController>();
            asset.name = "BehaviorAcceptanceFSM";
            foreach (string name in new[] { "Idle", "Active" })
            {
                var state = new FsmStateNodeData { name = name, isDefaultState = name == "Idle" };
                state.stateScripts.Add(new FsmStateScriptInfo { className = typeof(BehaviorAcceptanceState).FullName });
                asset.states.Add(state);
            }
            asset.parameters.Add(new FsmParameterData { name = "Go", parameterType = ParameterType.Bool });
            asset.parameters.Add(new FsmParameterData { name = "Fire", parameterType = ParameterType.Trigger });
            var transition = new FsmTransitionData { fromStateName = "Idle", toStateName = "Active" };
            transition.conditions.Add(new FsmConditionData { parameterName = "Go", targetValue = 1, compareType = CompareType.Equal });
            asset.transitions.Add(transition);
            string assetPath = folder + "/BehaviorAcceptanceFSM.asset";
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssetIfDirty(asset);
            string scenePath = folder + "/BehaviorAcceptance.unity";
            bool onlyEmptyUntitled = SceneManager.sceneCount == 1 && string.IsNullOrEmpty(SceneManager.GetSceneAt(0).path) && SceneManager.GetSceneAt(0).rootCount == 0;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, onlyEmptyUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                var obj = new GameObject("YL.Behavior.Acceptance");
                SceneManager.MoveGameObjectToScene(obj, scene);
                var probe = obj.AddComponent<BehaviorPlayerProbe>();
                probe.source = asset;
                probe.sourceGuid = AssetDatabase.AssetPathToGUID(assetPath);
                probe.expectedDevelopment = development;
                var events = (GameObject)PrefabUtility.InstantiatePrefab(CreateEventsPrefab(folder), scene);
                events.transform.SetParent(obj.transform, true);
                probe.eventReceiver = events.GetComponent<BehaviorEventReceiver>();
                probe.eventMotor = events.GetComponentInChildren<CharacterController2D>(true);
                probe.eventPlatform = events.GetComponentInChildren<Moveable>(true);
                probe.eventQuery = events.GetComponentInChildren<OverlapDetector2D>(true);
                probe.eventSphere = events.GetComponentInChildren<OverlapDetector3D>(true);
                probe.eventTarget = events.transform.Find("Target2D").GetComponent<Collider2D>();
                probe.eventTarget3D = events.transform.Find("Target3D").GetComponent<Collider>();
                var camera = new GameObject("YL.Behavior.AcceptanceCamera").AddComponent<Camera>();
                SceneManager.MoveGameObjectToScene(camera.gameObject, scene);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.025f, 0.04f, 0.065f);
                if (!EditorSceneManager.SaveScene(scene, scenePath))
                {
                    throw new InvalidOperationException("Acceptance scene save failed.");
                }
            }
            finally
            {
                if (onlyEmptyUntitled)
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            Debug.Log($"Owned fixture: {scenePath}; source GUID {AssetDatabase.AssetPathToGUID(assetPath)}. Generated, NOT run or Player-verified.");
            return scenePath;
        }

        internal static GameObject CreateEventsPrefab(string folder)
        {
            var root = new GameObject("YL.Behavior.SerializedEvents");
            root.SetActive(false);
            root.transform.position = new Vector3(3240, 3200);
            try
            {
                GameObject Child(string name, Vector3 position)
                {
                    var child = new GameObject(name);
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = position;
                    return child;
                }
                var receiver = root.AddComponent<BehaviorEventReceiver>();
                Child("Target2D", Vector3.zero).AddComponent<BoxCollider2D>();
                var query = Child("Query2D", Vector3.zero).AddComponent<OverlapDetector2D>();
                query.Composite = new OverlapComposite2D(OverlapShape2D.Circle(Vector2.zero, 2));
                query.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 });
                UnityEventTools.AddPersistentListener(query.EnteredEvent, receiver.ReceiveHit);
                UnityEventTools.AddPersistentListener(query.StayedEvent, receiver.ReceiveStay);
                UnityEventTools.AddPersistentListener(query.ExitedEvent, receiver.ReceiveExit);
                Child("Target3D", Vector3.forward * 20).AddComponent<BoxCollider>();
                var sphere = Child("Query3D", Vector3.forward * 20).AddComponent<OverlapDetector3D>();
                sphere.Composite = new OverlapComposite3D(OverlapShape3D.Sphere(Vector3.zero, 2));
                sphere.Filter = new OverlapFilter3D(1);
                UnityEventTools.AddPersistentListener(sphere.EnteredEvent, receiver.ReceiveHit3D);
                UnityEventTools.AddPersistentListener(sphere.StayedEvent, receiver.ReceiveStay3D);
                UnityEventTools.AddPersistentListener(sphere.ExitedEvent, receiver.ReceiveExit3D);
                var motorObject = Child("Motor", Vector3.right * 5);
                motorObject.AddComponent<Rigidbody2D>().gravityScale = 2;
                motorObject.AddComponent<BoxCollider2D>().size = new Vector2(1, 2);
                var motor = motorObject.AddComponent<CharacterController2D>();
                motor.ChecksWalls = true;
                motor.ChecksCeiling = true;
                motor.onGroundValueChange = new YLFramework.Character2D.BoolUnityEvent();
                motor.onTouchWallValueChange = new YLFramework.Character2D.BoolUnityEvent();
                motor.onTopValueChange = new YLFramework.Character2D.BoolUnityEvent();
                motor.onJumpCountValueChange = new IntUnityEvent();
                motor.onVelocityValueChange = new Vector2UnityEvent();
                UnityEventTools.AddPersistentListener(motor.onGroundValueChange, receiver.ReceiveBool);
                UnityEventTools.AddPersistentListener(motor.onTouchWallValueChange, receiver.ReceiveWall);
                UnityEventTools.AddPersistentListener(motor.onTopValueChange, receiver.ReceiveTop);
                UnityEventTools.AddPersistentListener(motor.onJumpCountValueChange, receiver.ReceiveInt);
                UnityEventTools.AddPersistentListener(motor.onVelocityValueChange, receiver.ReceiveVector2);
                Child("Floor", new Vector3(5, -1.5f)).AddComponent<BoxCollider2D>().size = new Vector2(4, 1);
                Child("Wall", new Vector3(5.74f, 0)).AddComponent<BoxCollider2D>().size = new Vector2(0.5f, 2);
                Child("Ceiling", new Vector3(5, 2)).AddComponent<BoxCollider2D>().size = new Vector2(2, 0.5f);
                var platform = Child("Moveable", Vector3.right * 12).AddComponent<Moveable>();
                platform.Positions.Add(platform.transform.position);
                platform.Positions.Add(platform.transform.position + Vector3.right);
                platform.Speed = 2;
                platform.MoveType = MoveType.Once;
                platform.onMove = new Vector3UnityEvent();
                UnityEventTools.AddPersistentListener(platform.onMove, receiver.ReceiveVector3);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/BehaviorEvents.prefab");
                if (!prefab)
                {
                    throw new InvalidOperationException("Could not save serialized Player event fixture.");
                }

                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

    }
}
