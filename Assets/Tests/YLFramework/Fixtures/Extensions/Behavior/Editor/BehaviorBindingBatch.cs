using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Behavior.Acceptance
{
    [InitializeOnLoad]
    public static class BehaviorBindingBatch
    {
        private const string Key = "YLFramework.Behavior.BindingBatch";
        [Serializable]
        private sealed class State
        {
            public string output;
            public string assetPath;
            public string scriptPath;
            public string typeName;
            public string assetGuid;
            public string scriptGuid;
            public string error;
            public long deadlineTicks;
            public bool complete;
            public bool passed;
        }
        private static State _state;

        static BehaviorBindingBatch()
        {
            string json = SessionState.GetString(Key, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            _state = JsonUtility.FromJson<State>(json);
            if (!_state.complete)
            {
                Hook();
            }
        }

        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }
        private static void Save() => SessionState.SetString(Key, JsonUtility.ToJson(_state));

        public static void Run()
        {
            if (!Application.isBatchMode || EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Use isolated EditMode BatchMode.");
            }

            string scene = BehaviorAcceptanceBuilder.Generate();
            string folder = Path.GetDirectoryName(scene).Replace('\\', '/');
            string assetPath = folder + "/BehaviorAcceptanceFSM.asset";
            var asset = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(assetPath);
            var node = new FsmStateNodeData { name = "AutoBound" };
            asset.AddState(node);
            AssetDatabase.SaveAssetIfDirty(asset);
            _state = new State { output = BehaviorUnityBatch.Output(), assetPath = assetPath, assetGuid = AssetDatabase.AssetPathToGUID(assetPath), scriptPath = folder + "/T3ABoundState" + Guid.NewGuid().ToString("N") + ".cs", deadlineTicks = DateTime.UtcNow.AddMinutes(2).Ticks };
            Save();
            Hook();
            // 使用产品Create And Add入口及其待绑定队列，不在测试中手动附加脚本。
            var creator = Type.GetType("YLFramework.Editor.StateMachine.FsmStateCreator, YLFramework.Editor", true);
            var create = creator.GetMethod("CreateFsmState", BindingFlags.Static | BindingFlags.NonPublic);
            if (!(bool)create.Invoke(null, new object[] { _state.scriptPath, false, asset, node }))
            {
                throw new InvalidOperationException("Create And Add refused the owned new path.");
            }
        }

        private static void Tick()
        {
            if (_state == null || _state.complete || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            try
            {
                if (DateTime.UtcNow.Ticks > _state.deadlineTicks)
                {
                    throw new TimeoutException("Compiled state was not safely attached after import/reload.");
                }

                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(_state.scriptPath);
                var type = script ? script.GetClass() : null;
                if (type == null)
                {
                    return;
                }

                var asset = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(_state.assetPath);
                var node = asset ? asset.GetStateNodeData("AutoBound") : null;
                string guid = AssetDatabase.AssetPathToGUID(_state.scriptPath);
                if (node == null || !node.stateScripts.Any(info => info.guid == guid && info.className == type.FullName))
                {
                    return;
                }

                AssetDatabase.SaveAssetIfDirty(asset);
                AssetDatabase.ImportAsset(_state.assetPath, ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(_state.assetPath);
                reloaded.ClearCache();
                if (!reloaded.GetStateNodeData("AutoBound").stateScripts.Any(info => info.guid == guid && info.className == type.FullName))
                {
                    throw new InvalidOperationException("Bound script did not survive asset reimport.");
                }

                if (!reloaded.ValidateConfiguration(out string error))
                {
                    throw new InvalidOperationException(error);
                }

                _state.typeName = type.FullName;
                _state.scriptGuid = guid;
                _state.passed = true;
                Finish(0);
            }
            catch (Exception error)
            {
                _state.error = error.ToString();
                Debug.LogException(error);
                Finish(1);
            }
        }

        private static void Finish(int code)
        {
            _state.complete = true;
            EditorApplication.update -= Tick;
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_state.output, "binding-results.json")), JsonUtility.ToJson(_state, true), new UTF8Encoding(false));
            SessionState.EraseString(Key);
            Debug.Log($"T3-A Create And Add actual compile/import/reload/reimport: {(_state.passed ? "PASS" : "FAIL")}");
            EditorApplication.Exit(code);
        }
    }
}
