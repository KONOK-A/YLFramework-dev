using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
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
                probe.eventQuery = events.GetComponentInChildren<OverlapCircle2D>(true);
                probe.eventSphere = events.GetComponentInChildren<OverlapSphere3D>(true);
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
                var query = Child("Query2D", Vector3.zero).AddComponent<OverlapCircle2D>();
                query.IsRunning = false;
                query.Radius = 2;
                query.LayerMask = 1;
                query.onColliderEnter = new OverlapEvent2D();
                query.onColliderExit = new OverlapEvent2D();
                UnityEventTools.AddPersistentListener(query.onColliderEnter, receiver.ReceiveHit);
                UnityEventTools.AddPersistentListener(query.onColliderExit, receiver.ReceiveHit);
                Child("Target3D", Vector3.forward * 20).AddComponent<BoxCollider>();
                var sphere = Child("Query3D", Vector3.forward * 20).AddComponent<OverlapSphere3D>();
                sphere.IsRunning = false;
                sphere.Radius = 2;
                sphere.LayerMask = 1;
                sphere.onColliderEnter = new OverlapEvent3D();
                sphere.onColliderExit = new OverlapEvent3D();
                UnityEventTools.AddPersistentListener(sphere.onColliderEnter, receiver.ReceiveHit3D);
                UnityEventTools.AddPersistentListener(sphere.onColliderExit, receiver.ReceiveHit3D);
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

        [MenuItem("Tools/YLFramework/Behavior Acceptance/Build Release Mono Windows64")]
        public static void BuildMono() => Build(ScriptingImplementation.Mono2x, false);

        [MenuItem("Tools/YLFramework/Behavior Acceptance/Build Release IL2CPP Windows64")]
        public static void BuildIL2CPP() => Build(ScriptingImplementation.IL2CPP, false);

        [MenuItem("Tools/YLFramework/Behavior Acceptance/Build Development Mono Windows64")]
        public static void BuildDevelopmentMono() => Build(ScriptingImplementation.Mono2x, true);

        [MenuItem("Tools/YLFramework/Behavior Acceptance/Build Development IL2CPP Windows64")]
        public static void BuildDevelopmentIL2CPP() => Build(ScriptingImplementation.IL2CPP, true);

        internal static BuildReport Build(ScriptingImplementation expected, bool development, string outputDirectory = null)
        {
            if (Array.IndexOf(PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';'), "YL_BEHAVIOR_ACCEPTANCE") < 0)
            {
                throw new InvalidOperationException("The explicit Player host must enable YL_BEHAVIOR_ACCEPTANCE before building the probe.");
            }
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64 || PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) != expected)
            {
                throw new InvalidOperationException($"Select Windows64 and {expected} in the main Unity integration workspace first. This tool does not modify ProjectSettings.");
            }

            string scene = Generate(development);
            string directory = outputDirectory == null ? Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Temp/Behavior.Acceptance/" + expected + "/" + (development ? "Development/" : "Release/") + Guid.NewGuid().ToString("N"))) : Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(outputDirectory));
            if (outputDirectory != null && (!directory.StartsWith("C:\\Users\\Kano\\AppData\\Local\\Temp\\kilo\\BA26\\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(directory)))))
            {
                throw new InvalidOperationException("CI output must be a new child of the dedicated A evidence directory.");
            }

            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Temp")))))
            {
                throw new InvalidOperationException("Missing project Temp parent.");
            }

            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, target = BuildTarget.StandaloneWindows64, locationPathName = Path.Combine(directory, "BehaviorAcceptance.exe"), options = development ? BuildOptions.Development : BuildOptions.None });
            Debug.Log($"Behavior {expected} build: {report.summary.result}; development={development}; errors={report.summary.totalErrors}; output={directory}. Player runtime is not yet verified. Run with -batchmode -nographics -logFile <owned-log-path>, require exit 0 and YL_BEHAVIOR_ACCEPTANCE result=PASS.");
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException("Behavior acceptance Player build failed.");
            }

            return report;
        }
    }
}
