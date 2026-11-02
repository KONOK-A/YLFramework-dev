using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.StateMachine;

namespace YLFramework.Behavior.Acceptance
{
    [InitializeOnLoad]
    public static class BehaviorReloadBatch
    {
        private const string Key = "YLFramework.Behavior.ReloadBatch";
        private const string HostName = "YL.Behavior.ReloadHost";
        [Serializable]
        private sealed class Round
        {
            public int mode;
            public int round;
            public int frame;
            public int cloneId;
            public bool passed;
            public bool afterExitNoClone;
            public string utc;
        }
        [Serializable]
        private sealed class SavedScene
        {
            public string path;
            public bool loaded;
            public bool active;
        }
        [Serializable]
        private sealed class State
        {
            public string output;
            public string scene;
            public string sourceName;
            public string phase;
            public string error;
            public int mode;
            public int round;
            public int entryFrame;
            public bool originalEnabled;
            public bool complete;
            public int originalOptions;
            public double deadline;
            public List<Round> results = new List<Round>();
            public SavedScene[] originalScenes;
        }
        private static State _state;

        static BehaviorReloadBatch()
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

        private static void Save() => SessionState.SetString(Key, JsonUtility.ToJson(_state));
        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged -= OnPlayState;
            EditorApplication.playModeStateChanged += OnPlayState;
        }

        public static void Run()
        {
            if (!Application.isBatchMode || EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Use the isolated A BatchMode in EditMode.");
            }

            if (!string.IsNullOrEmpty(SessionState.GetString(Key, string.Empty)))
            {
                throw new InvalidOperationException("A reload run is already recorded in this Editor session.");
            }

            _state = new State
            {
                output = BehaviorUnityBatch.Output(),
                originalEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                originalOptions = (int)EditorSettings.enterPlayModeOptions,
                originalScenes = EditorSceneManager.GetSceneManagerSetup().Select(scene => new SavedScene { path = scene.path, loaded = scene.isLoaded, active = scene.isActive }).ToArray(),
                phase = "prepare",
                deadline = EditorApplication.timeSinceStartup + 60
            };
            _state.scene = BehaviorAcceptanceBuilder.Generate();
            EditorSceneManager.OpenScene(_state.scene, OpenSceneMode.Single);
            var player = UnityEngine.Object.FindObjectOfType<BehaviorPlayerProbe>();
            if (!player || !player.source)
            {
                throw new InvalidOperationException("Owned fixture source is missing.");
            }

            player.enabled = false;
            var obj = new GameObject(HostName);
            var asset = obj.AddComponent<FsmController>();
            asset.AddRuntimeFsmController(player.source, "Reload");
            var probe = obj.AddComponent<BehaviorReloadProbe>();
            probe.asset = asset;
            probe.source = player.source;
            _state.sourceName = player.source.name;
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), _state.scene);
            Save();
            Hook();
        }

        private static BehaviorReloadProbe Probe()
        {
            var obj = GameObject.Find(HostName);
            return obj ? obj.GetComponent<BehaviorReloadProbe>() : null;
        }

        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException($"mode={_state.mode}, round={_state.round}, phase={_state.phase}: {message}");
            }
        }

        private static void OnPlayState(PlayModeStateChange change)
        {
            if (_state == null || _state.complete)
            {
                return;
            }

            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                _state.phase = "verify";
                _state.entryFrame = Time.frameCount;
                _state.deadline = EditorApplication.timeSinceStartup + 30;
                Save();
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                if (!string.IsNullOrEmpty(_state.error))
                {
                    RestoreAndExit(1);
                    return;
                }
                _state.phase = "exited";
                _state.deadline = EditorApplication.timeSinceStartup + 30;
                Save();
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
                Require(EditorApplication.timeSinceStartup <= _state.deadline, "Timed out waiting for a real play-state transition.");
                if (_state.phase == "prepare")
                {
                    if (_state.round == 0 && _state.mode > 0)
                    {
                        EditorSceneManager.OpenScene(_state.scene, OpenSceneMode.Single);
                    }

                    EditorSettings.enterPlayModeOptionsEnabled = _state.mode != 0;
                    EditorSettings.enterPlayModeOptions = _state.mode == 2 ? EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload : EnterPlayModeOptions.DisableDomainReload;
                    _state.phase = "entering";
                    _state.deadline = EditorApplication.timeSinceStartup + 30;
                    Save();
                    EditorApplication.isPlaying = true;
                }
                else if (_state.phase == "verify" && EditorApplication.isPlaying && Time.frameCount - _state.entryFrame >= 5)
                {
                    var probe = Probe();
                    Require(probe && probe.asset && probe.source, "Scene-authored dual FSM probe is missing.");
                    Require(probe.LightweightAllocated && probe.LightweightState == 1, "Lightweight FSM did not start fresh.");
                    Require(probe.asset.IsInitialized && probe.asset.GetCurrentStateInfo(0)?.Data.name == "Idle", "Scene-authored asset FSM did not start at Idle.");
                    Require(probe.asset.RuntimeFsmController.Count == 1 && probe.asset.RuntimeFsmController[0] && probe.asset.RuntimeFsmController[0] != probe.source, "Owned native clone is missing or source was reused.");
                    probe.asset.SetBool("Go", true);
                    Require(probe.asset.GetCurrentStateInfo(0)?.Data.name == "Active" && probe.LightweightState == 1, "Asset transition affected lightweight state.");
                    Require(probe.ChangeLightweight() && probe.LightweightState == 2 && probe.asset.GetCurrentStateInfo(0)?.Data.name == "Active", "Lightweight transition affected the asset FSM.");
                    probe.asset.SetTrigger("Fire");
                    probe.asset.SetTrigger("Fire");
                    probe.asset.ResetTrigger("Fire");
                    Require(!probe.asset.GetTrigger("Fire") && probe.source.parameters[0].Value == 0, "Trigger reset/source isolation failed.");
                    _state.results.Add(new Round { mode = _state.mode, round = _state.round, frame = Time.frameCount, cloneId = probe.asset.RuntimeFsmController[0].GetInstanceID(), passed = true, utc = DateTime.UtcNow.ToString("o") });
                    _state.phase = "leaving";
                    _state.deadline = EditorApplication.timeSinceStartup + 30;
                    Save();
                    EditorApplication.isPlaying = false;
                }
                else if (_state.phase == "exited")
                {
                    var probe = Probe();
                    Require(!probe || !probe.LightweightAllocated, "Lightweight state was retained after exiting Play.");
                    Require(Resources.FindObjectsOfTypeAll<RuntimeFsmController>().All(asset => !asset || asset.name != _state.sourceName || EditorUtility.IsPersistent(asset)), "A native configuration clone leaked after exiting Play.");
                    _state.results[_state.results.Count - 1].afterExitNoClone = true;
                    _state.round++;
                    if (_state.round == 2)
                    {
                        _state.round = 0;
                        _state.mode++;
                    }
                    if (_state.mode == 3)
                    {
                        RestoreAndExit(0);
                        return;
                    }
                    _state.phase = "prepare";
                    _state.deadline = EditorApplication.timeSinceStartup + 30;
                    Save();
                }
            }
            catch (Exception error)
            {
                _state.error = error.ToString();
                Save();
                Debug.LogException(error);
                if (EditorApplication.isPlaying)
                {
                    _state.phase = "failedLeaving";
                    Save();
                    EditorApplication.isPlaying = false;
                }
                else
                {
                    RestoreAndExit(1);
                }
            }
        }

        private static void RestoreAndExit(int code)
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayState;
            try
            {
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)_state.originalOptions;
                EditorSettings.enterPlayModeOptionsEnabled = _state.originalEnabled;
                var originals = _state.originalScenes.Where(scene => !string.IsNullOrEmpty(scene.path)).Select(scene => new SceneSetup { path = scene.path, isActive = scene.active, isLoaded = scene.loaded }).ToArray();
                if (originals.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(originals);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
            catch (Exception error)
            {
                _state.error += "\nRestore: " + error;
                code = 1;
            }
            _state.complete = true;
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_state.output, "reload-results.json")), JsonUtility.ToJson(_state, true), new UTF8Encoding(false));
            SessionState.EraseString(Key);
            Debug.Log($"T3-A Reload matrix finished: rounds={_state.results.Count}; result={(code == 0 ? "PASS" : "FAIL")}; original settings restored.");
            EditorApplication.Exit(code);
        }
    }
}
