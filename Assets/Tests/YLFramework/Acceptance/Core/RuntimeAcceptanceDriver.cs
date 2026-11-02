#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.UI;

namespace YLFramework.Core.Tests
{
    /// <summary>5.5 同域真实双 Play 的临时状态 owner；调用前必须单独获得 Logger 隔离授权。</summary>
    public static class RuntimeAcceptanceDriver
    {
        public const string ScenePath = "Assets/Tests/YLFramework/Acceptance/Core/Runtime55.unity";
        private static SceneSetup[] _scenes;
        private static bool _isEnabled;
        private static bool _hasFailed;
        private static bool _isArmed;
        private static EnterPlayModeOptions _options;
        private static ILogHandler _handler;
        private static ILogHandler _native;
        private static int _enters;
        private static int _exits;
        private static int _verified;
        private static int _initialRound;
        private static double _deadline;
        private static double _next;
        private static string _log;
        private static string _phase;

        public static string Begin(string url, string logPath)
        {
            if (_isArmed || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                throw new InvalidOperationException("Acceptance requires an idle Editor.");
            }

            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("Do not overwrite a dirty scene.");
                }
            }

            _scenes = EditorSceneManager.GetSceneManagerSetup();
            _isEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _options = EditorSettings.enterPlayModeOptions;
            _handler = Debug.unityLogger.logHandler;
            _native = (ILogHandler)Activator.CreateInstance(typeof(Debug).Assembly.GetType("UnityEngine.DebugLogHandler"), true);
            _enters = _exits = _verified = 0;
            _initialRound = RuntimeAcceptanceEntry.Round;
            _deadline = EditorApplication.timeSinceStartup + 60;
            _next = 0;
            _hasFailed = false;
            _log = logPath;
            _isArmed = true;
            _phase = "entering";
            Debug.unityLogger.logHandler = _native;
            try
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var go = new GameObject("Runtime55.Acceptance");
                go.AddComponent<AudioListener>();
                var entry = go.AddComponent<RuntimeAcceptanceEntry>();
                entry.colorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.kano.ylframework/Prefabs/ColorSelector.prefab");
                entry.serverUrl = url;
                if (!entry.colorPrefab || !EditorSceneManager.SaveScene(scene, ScenePath))
                {
                    throw new InvalidOperationException("Temporary acceptance scene creation failed.");
                }

                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_log), "BEGIN domain=" + AppDomain.CurrentDomain.Id + " options=" +
                    EditorSettings.enterPlayModeOptions + Environment.NewLine);
                RuntimeAcceptanceEntry.Result = null;
                EditorApplication.playModeStateChanged += State;
                EditorApplication.update += Tick;
                // 由真实 Editor Play 入口发起首轮；不在 Roslyn/MCP 的调用栈内触发切换。
                return _log;
            }
            catch
            {
                _hasFailed = true;
                Restore();
                throw;
            }
        }

        private static void State(PlayModeStateChange state)
        {
            Debug.unityLogger.logHandler = _native;
            Append("STATE " + state + " domain=" + AppDomain.CurrentDomain.Id +
                " result=" + RuntimeAcceptanceEntry.Result + Environment.NewLine);
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _enters++;
                _phase = "running";
                _deadline = EditorApplication.timeSinceStartup + 35;
            }
            if (state != PlayModeStateChange.EnteredEditMode)
            {
                return;
            }

            _exits++;
            if (_enters != _exits || RuntimeAcceptanceEntry.Result != "READY round=" + (_initialRound + _enters))
            {
                _hasFailed = true;
            }

            if (!_hasFailed)
            {
                try
                {
                    RuntimeAcceptanceEntry.VerifyExit(_initialRound + _enters);
                    if (UnityEngine.Object.FindObjectsOfType<YLFramework.Async.CoroutineStarter>(true).Length != 0)
                    {
                        throw new InvalidOperationException("Real exit left a Foundation coroutine host.");
                    }

                    _verified++;
                    Append("EXIT_PASS round=" + _exits + Environment.NewLine);
                }
                catch (Exception error)
                {
                    _hasFailed = true;
                    Append("FAIL " + error + Environment.NewLine);
                }
            }
            if (_exits == 1 && !_hasFailed)
            {
                _phase = "between";
                _next = EditorApplication.timeSinceStartup + .2;
            }
            else
            {
                Restore();
            }
        }

        private static void Tick()
        {
            if (_phase != "between" && EditorApplication.timeSinceStartup > _deadline)
            {
                _hasFailed = true;
                Append("FAIL timeout phase=" + _phase + Environment.NewLine);
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    Restore();
                    return;
                }
                _phase = "leaving";
                _deadline = EditorApplication.timeSinceStartup + 15;
                EditorApplication.isPlaying = false;
                return;
            }
            if (EditorApplication.isPlaying)
            {
                var result = RuntimeAcceptanceEntry.Result;
                if (_phase == "running" && result != null && (result.StartsWith("READY", StringComparison.Ordinal) || result.StartsWith("FAILED", StringComparison.Ordinal)))
                {
                    _hasFailed |= result.StartsWith("FAILED", StringComparison.Ordinal);
                    _phase = "leaving";
                    _deadline = EditorApplication.timeSinceStartup + 15;
                    EditorApplication.isPlaying = false;
                }
            }
            else if (_next > 0 && EditorApplication.timeSinceStartup >= _next)
            {
                _next = 0;
                _phase = "entering";
                _deadline = EditorApplication.timeSinceStartup + 15;
                RuntimeAcceptanceEntry.Result = null;
                EditorApplication.isPlaying = true;
            }
        }

        public static void Restore()
        {
            if (!_isArmed)
            {
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _hasFailed = true;
                _phase = "leaving";
                _deadline = EditorApplication.timeSinceStartup + 15;
                EditorApplication.isPlaying = false;
                return;
            }
            _hasFailed |= _enters != 2 || _exits != 2 || _verified != 2;
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= State;
            var errors = new List<Exception>();
            void Attempt(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                    _hasFailed = true;
                }
            }
            try
            {
                Attempt(() => EditorSettings.enterPlayModeOptionsEnabled = _isEnabled);
                Attempt(() => EditorSettings.enterPlayModeOptions = _options);
                Attempt(() => EditorSceneManager.RestoreSceneManagerSetup(_scenes));
                Append((_hasFailed ? "FAILED" : "DOUBLE_PLAY_PASS") + " enters=" + _enters + " exits=" + _exits + " verified=" + _verified +
                    " optionsRestored=" + EditorSettings.enterPlayModeOptionsEnabled + ":" + EditorSettings.enterPlayModeOptions + Environment.NewLine);
            }
            finally
            {
                Debug.unityLogger.logHandler = _handler;
                _isArmed = errors.Count != 0;
            }
            if (errors.Count != 0)
            {
                throw new AggregateException("Acceptance state restoration incomplete; Restore can be retried.", errors);
            }
        }

        private static void Append(string message)
        {
            try
            {
                File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_log), message);
            }
            catch (Exception error)
            {
                _hasFailed = true;
                Debug.LogError("RUNTIME55 evidence log failed: " + error);
            }
        }
    }
}
#endif
