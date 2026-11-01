using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Tests.DomainMain
{
    // 主工程专属入口不退出用户 Editor；SessionState 使保护跨程序集重载持续生效。
    [InitializeOnLoad]
    public static class DomainMainAcceptance
    {
        public const string Evidence = "G:/Unity/MC26-20261020T173208Z";
        private const string Project = "G:/Unity/Projects/CGJ2024";
        private const string Key = "YLFramework.Domain.MainAcceptance";
        private static readonly Results _callback = new Results();
        private static readonly string[] _mapFields = { "scriptingBackend", "managedStrippingLevel", "scriptingDefineSymbols" };
        private static double _configurationStableAfter;

        [Serializable]
        private sealed class Map
        {
            public string field;
            public string[] keys;
            public string[] texts;
            public int[] numbers;
            public bool strings;
        }

        [Serializable]
        private sealed class Settings
        {
            public string playerJson;
            public string playerHash;
            public string editorHash;
            public string company;
            public string product;
            public string saveTestOutput;
            public bool playerDirty;
            public bool reloadEnabled;
            public int reload;
            public SceneSetup[] scenes;
            public Map[] maps;
        }

        static DomainMainAcceptance()
        {
            TestRunnerApi.RegisterTestCallback(_callback);
            if (SessionState.GetBool(Key + ".PendingRestore", false))
            {
                EditorApplication.update += FinishTests;
            }

            if (SessionState.GetBool(Key + ".BuildPending", false))
            {
                EditorApplication.update += BuildQueued;
            }

            if (SessionState.GetBool(Key + ".BuildRestore", false))
            {
                EditorApplication.update += FinishBuild;
            }

            if (!string.IsNullOrEmpty(SessionState.GetString(Key + ".Configuration", "")))
            {
                SessionState.SetBool(Key + ".ConfigurationReloaded", true);
                _configurationStableAfter = EditorApplication.timeSinceStartup + 5;
                EditorApplication.update += FinishConfiguration;
            }
        }

        private static bool TestRunActive() => (bool)typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

        public static object Status() => new
        {
            projectPath = Path.GetDirectoryName(Application.dataPath),
            pid = System.Diagnostics.Process.GetCurrentProcess().Id,
            Application.unityVersion,
            EditorApplication.isCompiling,
            EditorApplication.isUpdating,
            EditorApplication.isPlaying,
            EditorUtility.scriptCompilationFailed,
            building = BuildPipeline.isBuildingPlayer,
            utfActive = TestRunActive(),
            protection = SessionState.GetBool(Key + ".Protection", false),
            output = SessionState.GetString(Key + ".Output", ""),
            scenes = EditorSceneManager.GetSceneManagerSetup(),
            EditorSettings.enterPlayModeOptionsEnabled,
            reload = (int)EditorSettings.enterPlayModeOptions
        };

        private static string Begin(string name)
        {
            Identity();
            Require(SessionState.GetBool(Key + ".Protection", false), "Main protection is not active.");
            Require(string.IsNullOrEmpty(SessionState.GetString(Key + ".Output", "")) && !TestRunActive(), "An earlier gate is still active.");
            Require(!EditorApplication.isCompiling && !EditorApplication.isUpdating && !EditorApplication.isPlayingOrWillChangePlaymode &&
                !EditorUtility.scriptCompilationFailed && !BuildPipeline.isBuildingPlayer, "Main Editor is not ready.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Require(!SceneManager.GetSceneAt(i).isDirty && !string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path), "Protect unsaved scenes.");
            }

            Require(PrefabStageUtility.GetCurrentPrefabStage() == null, "Protect the user's prefab stage.");
            Require(Regex.IsMatch(name, "^[a-z0-9-]+$"), "Invalid Run name.");
            string output = Evidence + "/" + name;
            Require(!Directory.Exists(output), "Never overwrite evidence.");
            Directory.CreateDirectory(output);
            SessionState.EraseString(Key + ".RunFailure");
            SessionState.SetString(Key + ".Output", output);
            Write(output + "/editor-before.json", Status());
            return output;
        }

        private static UnityEngine.Object PlayerSettingsObject() => (UnityEngine.Object)typeof(PlayerSettings)
            .GetMethod("InternalGetPlayerSettingsObject", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

        private static void Capture(string output)
        {
            var settings = PlayerSettingsObject();
            var maps = new List<Map>();
            using (var serialized = new SerializedObject(settings))
            {
                serialized.Update();
                foreach (string field in _mapFields)
                {
                    var property = serialized.FindProperty(field);
                    var entries = Enumerable.Range(0, property.arraySize).Select(property.GetArrayElementAtIndex).ToArray();
                    bool strings = field == "scriptingDefineSymbols";
                    maps.Add(new Map
                    {
                        field = field,
                        strings = strings,
                        keys = entries.Select(p => p.FindPropertyRelative("first").stringValue).ToArray(),
                        texts = strings ? entries.Select(p => p.FindPropertyRelative("second").stringValue).ToArray() : null,
                        numbers = strings ? null : entries.Select(p => p.FindPropertyRelative("second").intValue).ToArray()
                    });
                }
            }
            var state = new Settings
            {
                playerJson = EditorJsonUtility.ToJson(settings),
                playerDirty = EditorUtility.IsDirty(settings),
                playerHash = Hash("ProjectSettings/ProjectSettings.asset"),
                editorHash = Hash("ProjectSettings/EditorSettings.asset"),
                company = PlayerSettings.companyName,
                product = PlayerSettings.productName,
                maps = maps.ToArray(),
                saveTestOutput = Environment.GetEnvironmentVariable("YL_SAVE_TEST_OUTPUT"),
                scenes = EditorSceneManager.GetSceneManagerSetup(),
                reloadEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                reload = (int)EditorSettings.enterPlayModeOptions
            };
            File.Copy("ProjectSettings/ProjectSettings.asset", output + "/ProjectSettings.before.asset");
            File.Copy("ProjectSettings/EditorSettings.asset", output + "/EditorSettings.before.asset");
            Write(output + "/settings-before.json", state);
            SessionState.SetString(Key + ".Settings", JsonConvert.SerializeObject(state));
        }

        private static void Restore(string output)
        {
            var state = JsonConvert.DeserializeObject<Settings>(SessionState.GetString(Key + ".Settings", ""));
            Require(state != null, "Missing original settings.");
            var settings = PlayerSettingsObject();
            var original = JObject.Parse(state.playerJson);
            var observed = JObject.Parse(EditorJsonUtility.ToJson(settings));
            foreach (string field in _mapFields)
            {
                ((JObject)original["PlayerSettings"]).Remove(field);
                ((JObject)observed["PlayerSettings"]).Remove(field);
            }
            Require(JToken.DeepEquals(original, observed), "Unrelated in-memory PlayerSettings changed; preserve live content.");
            Require(PlayerSettings.companyName == state.company && PlayerSettings.productName == state.product, "Company/product changes are not owned by this acceptance.");
            using (var serialized = new SerializedObject(settings))
            {
                serialized.Update();
                foreach (var map in state.maps)
                {
                    ValidateUnownedMap(serialized.FindProperty(map.field), map);
                }
            }
            // 所有冲突检查均先于setter/Apply，不能在拒绝磁盘冲突前覆盖用户内存。
            RestoreBytes(output, "ProjectSettings", _mapFields, Array.Empty<string>(), true);
            RestoreBytes(output, "EditorSettings", Array.Empty<string>(), new[] { "m_EnterPlayModeOptionsEnabled", "m_EnterPlayModeOptions" }, true);
            using (var serialized = new SerializedObject(settings))
            {
                serialized.Update();
                foreach (var map in state.maps)
                {
                    var property = serialized.FindProperty(map.field);
                    property.arraySize = map.keys.Length;
                    for (int i = 0; i < map.keys.Length; i++)
                    {
                        var entry = property.GetArrayElementAtIndex(i);
                        entry.FindPropertyRelative("first").stringValue = map.keys[i];
                        if (map.strings)
                        {
                            entry.FindPropertyRelative("second").stringValue = map.texts[i];
                        }
                        else
                        {
                            entry.FindPropertyRelative("second").intValue = map.numbers[i];
                        }
                    }
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            if ((int)EditorSettings.enterPlayModeOptions != state.reload)
            {
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)state.reload;
            }

            if (EditorSettings.enterPlayModeOptionsEnabled != state.reloadEnabled)
            {
                EditorSettings.enterPlayModeOptionsEnabled = state.reloadEnabled;
            }

            Require(JToken.DeepEquals(JObject.Parse(state.playerJson), JObject.Parse(EditorJsonUtility.ToJson(settings))), "PlayerSettings memory was not restored.");
            RestoreBytes(output, "ProjectSettings", _mapFields, Array.Empty<string>());
            RestoreBytes(output, "EditorSettings", Array.Empty<string>(), new[] { "m_EnterPlayModeOptionsEnabled", "m_EnterPlayModeOptions" });
            Require(Hash("ProjectSettings/ProjectSettings.asset") == state.playerHash && Hash("ProjectSettings/EditorSettings.asset") == state.editorHash,
                "Original ProjectSettings bytes were not restored.");
            if (state.playerDirty)
            {
                EditorUtility.SetDirty(settings);
            }
            else
            {
                EditorUtility.ClearDirty(settings);
            }

            if (JsonConvert.SerializeObject(state.scenes) != JsonConvert.SerializeObject(EditorSceneManager.GetSceneManagerSetup()))
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Require(!SceneManager.GetSceneAt(i).isDirty, "Do not discard a dirty scene after UTF.");
                }

                EditorSceneManager.RestoreSceneManagerSetup(state.scenes);
            }
            Require(JsonConvert.SerializeObject(state.scenes) == JsonConvert.SerializeObject(EditorSceneManager.GetSceneManagerSetup()), "Original scenes were not restored.");
            if (SessionState.GetBool(Key + ".SaveEnvironment", false))
            {
                Require(Environment.GetEnvironmentVariable("YL_SAVE_TEST_OUTPUT") == output + "/save-fixtures", "Another owner changed the Save test environment.");
                Environment.SetEnvironmentVariable("YL_SAVE_TEST_OUTPUT", state.saveTestOutput);
                SessionState.EraseBool(Key + ".SaveEnvironment");
            }
            Write(output + "/settings-restored.json", new
            {
                restored = true,
                memoryRestored = true,
                scenesRestored = true,
                state.playerHash,
                state.editorHash,
                state.maps,
                state.playerDirty,
                state.reloadEnabled,
                state.reload
            });
        }

        private static void ValidateUnownedMap(SerializedProperty property, Map snapshot)
        {
            var current = Enumerable.Range(0, property.arraySize).Select(property.GetArrayElementAtIndex)
                .Where(p => p.FindPropertyRelative("first").stringValue != "Standalone").ToArray();
            var expected = Enumerable.Range(0, snapshot.keys.Length).Where(i => snapshot.keys[i] != "Standalone").ToArray();
            Require(current.Length == expected.Length && current.Select((entry, i) =>
                entry.FindPropertyRelative("first").stringValue == snapshot.keys[expected[i]] &&
                (snapshot.strings ? entry.FindPropertyRelative("second").stringValue == snapshot.texts[expected[i]] :
                    entry.FindPropertyRelative("second").intValue == snapshot.numbers[expected[i]])).All(same => same),
                "Unrelated platform map changed; preserve the user's memory: " + snapshot.field);
        }

        private static void RestoreBytes(string output, string name, string[] maps, string[] scalars, bool validateOnly = false)
        {
            string path = "ProjectSettings/" + name + ".asset", backup = output + "/" + name + ".before.asset";
            if (Hash(path) == Hash(backup))
            {
                return;
            }

            string original = File.ReadAllText(backup), current = File.ReadAllText(path), normalized = current;
            foreach (string field in scalars)
            {
                string pattern = "(?m)^  " + field + ":.*$", before = Regex.Match(original, pattern).Value;
                Require(before.Length != 0 && Regex.Matches(normalized, pattern).Count == 1, "Unexpected settings scalar layout.");
                normalized = Regex.Replace(normalized, pattern, _ => before);
            }
            foreach (string field in maps)
            {
                string pattern = "(?ms)^  " + field + ":.*?(?=^  [^ ]|\\z)";
                string before = Regex.Match(original, pattern).Value, after = Regex.Match(normalized, pattern).Value;
                const string Standalone = "(?m)^    Standalone: [^\\r\\n]*\\r?\\n";
                Require(before.Length != 0 && Regex.Replace(before, Standalone, "") == Regex.Replace(after, Standalone, ""), "Unrelated platform map changed.");
                normalized = Regex.Replace(normalized, pattern, _ => before);
            }
            Require(normalized == original && File.ReadAllText(path) == current, "Unattributed settings change; do not overwrite it.");
            if (validateOnly)
            {
                return;
            }

            File.Copy(path, output + "/" + name + ".owned-side-effect.asset");
            File.Copy(backup, path, true);
        }

        public static void RunTests(string mode, string name, string scope = "Domain", bool rejectRequestReceipt = false)
        {
            string[] groups = scope == "Domain" ? new[] { "^YLFramework[.]Tests[.]Extensions[.]Domain[.]" } :
                scope == "Protection" ? new[] { "^YLFramework[.]Tests[.]DomainMainAcceptanceTests[.]" } :
                scope == "Excel" ? new[] { "^YLFramework[.]Domain[.]Excel[.]Tests[.]" } :
                scope == "Boundary" ? new[] { "^YLFramework[.]Foundation[.]Tests[.]FoundationBoundaryTests[.]", "^YLFramework[.]Core[.]Tests[.]CoreArchitectureBoundaryTests[.]", "^YLFramework[.]Tests[.]Resource[.]FrameworkAssemblyIndependenceTests[.]", "^YLFramework[.]Core[.]Tests[.]CoreIdentityTests[.]", "^YLFramework[.]Tests[.]ExportUnityPackageTests[.]" } :
                scope == "ResourceUI" ? new[] { "^YLFramework[.]Tests[.]Resource[.]ResourceSystemTests[.]", "^YLFramework[.]Tests[.]Resource[.]ResourceImageTests[.]", "^YLFramework[.]Core[.]Tests[.]CoreResourcePlayModeTests[.]" } :
                scope == "DataSave" ? new[] { "^YLFramework[.]Tests[.]Extensions[.]Data[.]Save[.]SaveManagerFacadeTests[.]", "^YLFramework[.]Tests[.]Extensions[.]Data[.]Save[.]SaveStoreSafetyTests[.](SlotsObjectsSettingsAndCurrentItemSurviveAColdReopen|ModelIdsSelectionAndDeletionUseTheSameSlotsAndCache|CurrentModelsPreserveStringValuesAndRejectWrongIdsOnLoad)$" } :
                scope == "DataResource" ? new[] { "^YLFramework[.]Tests[.]DataAssets[.]DataAssetResourceTests[.]" } :
                scope == "BehaviorCombination" ? new[] { "^YLFramework[.]Tests[.]Extensions[.]Behavior[.]OverlapComponentTests[.]", "^YLFramework[.]Tests[.]Extensions[.]Behavior[.]AssetFsmRuntimeTests[.]" } :
                scope == "Behavior" ? new[] { "^YLFramework[.]Tests[.]Extensions[.]Behavior[.]" } :
                throw new ArgumentException("Unknown exact scope.", nameof(scope));
            string output = Begin(name);
            try
            {
                Capture(output);
                string folder = "Assets/YLFramework/Tests/Fixtures/Extensions/Domain/Main-" + Guid.NewGuid().ToString("N");
                OwnPath(folder);
                Require(!string.IsNullOrEmpty(AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder))), "Could not create owned test folder.");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                string scenePath = folder + "/MainEmpty.unity";
                Require(EditorSceneManager.SaveScene(scene, scenePath), "Could not save owned empty test scene.");
                SessionState.SetString(Key + ".TestScene", scenePath);
                Write(output + "/owned-test-scene.json", new
                {
                    folder,
                    scene = scenePath,
                    roots = scene.rootCount
                });
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                if (scope == "DataSave")
                {
                    Environment.SetEnvironmentVariable("YL_SAVE_TEST_OUTPUT", output + "/save-fixtures");
                    SessionState.SetBool(Key + ".SaveEnvironment", true);
                }
                SessionState.SetString(Key + ".Groups", JsonConvert.SerializeObject(groups));
                var filter = new Filter { testMode = mode == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode, groupNames = groups };
                if (scope == "Protection")
                {
                    var fixture = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Tests.DomainMainAcceptanceTests")).Single(t => t != null);
                    filter.testNames = fixture.GetMethods().Where(m => m.GetCustomAttributes(false).Any(a => a.GetType().FullName == "NUnit.Framework.TestAttribute"))
                        .Select(m => fixture.FullName + "." + m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                }
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                try
                {
                    Require(!rejectRequestReceipt || scope == "Protection", "Fault injection is restricted to the explicit protection suite.");
                    if (rejectRequestReceipt)
                    {
                        Directory.CreateDirectory(output + "/test-request.json");
                    }

                    string job = api.Execute(new ExecutionSettings(filter));
                    Write(output + "/test-request.json", new
                    {
                        mode,
                        scope,
                        filter,
                        job
                    });
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(api);
                }
            }
            catch (Exception error)
            {
                SessionState.SetString(Key + ".RunFailure", error.ToString());
                DeferRunRestore();
                try
                {
                    Write(output + "/run-failed.json", new
                    {
                        error = error.ToString(),
                        utfActive = TestRunActive(),
                        recoveryDeferred = true,
                        outputPreserved = SessionState.GetString(Key + ".Output", "") == output,
                        settingsPreserved = !string.IsNullOrEmpty(SessionState.GetString(Key + ".Settings", "")),
                        noRestorePublished = !File.Exists(output + "/settings-restored.json"),
                        utc = DateTime.UtcNow
                    });
                }
                catch (Exception writeError)
                {
                    throw new AggregateException("Run failed and its disk receipt was unavailable; SessionState still owns recovery.", error, writeError);
                }
                throw;
            }
        }

        private static void DeferRunRestore()
        {
            SessionState.SetBool(Key + ".PendingRestore", true);
            EditorApplication.update -= FinishTests;
            EditorApplication.update += FinishTests;
        }

        private sealed class Results : ICallbacks
        {
            private static IEnumerable<ITestAdaptor> Leaves(ITestAdaptor test) => test.HasChildren ? test.Children.SelectMany(Leaves) : new[] { test };
            public void RunStarted(ITestAdaptor tests)
            {
                string output = SessionState.GetString(Key + ".Output", "");
                if (string.IsNullOrEmpty(output))
                {
                    return;
                }

                var groups = JsonConvert.DeserializeObject<string[]>(SessionState.GetString(Key + ".Groups", "[]"));
                Write(output + "/discovered.json", Leaves(tests).Select(t => t.FullName).Where(t => groups.Any(g => Regex.IsMatch(t, g))).ToArray());
            }
            public void TestStarted(ITestAdaptor test)
            {
            }
            public void TestFinished(ITestResultAdaptor result)
            {
            }
            public void RunFinished(ITestResultAdaptor result)
            {
                string output = SessionState.GetString(Key + ".Output", "");
                if (string.IsNullOrEmpty(output))
                {
                    return;
                }

                File.WriteAllText(output + "/results.xml", result.ToXml().OuterXml);
                Write(output + "/summary.json", new
                {
                    result.ResultState,
                    result.PassCount,
                    result.FailCount,
                    result.SkipCount,
                    result.InconclusiveCount,
                    result.Duration,
                    result.StartTime,
                    result.EndTime,
                    pid = System.Diagnostics.Process.GetCurrentProcess().Id
                });
                DeferRunRestore();
            }
        }

        private static void FinishTests()
        {
            if (TestRunActive() || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            EditorApplication.update -= FinishTests;
            string output = SessionState.GetString(Key + ".Output", "");
            try
            {
                Identity();
                Restore(output);
                string failure = SessionState.GetString(Key + ".RunFailure", "");
                Write(output + "/gate-complete.json", new
                {
                    closed = true,
                    utfInactive = !TestRunActive(),
                    infrastructureError = failure,
                    pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    utc = DateTime.UtcNow
                });
                SessionState.EraseBool(Key + ".PendingRestore");
                SessionState.EraseString(Key + ".Output");
                SessionState.EraseString(Key + ".Groups");
                SessionState.EraseString(Key + ".TestScene");
                SessionState.EraseString(Key + ".Settings");
                SessionState.EraseString(Key + ".RunFailure");
            }
            catch (Exception error)
            {
                Write(output + "/restore-failed.json", new
                {
                    error = error.ToString()
                });
                throw;
            }
        }

        public static void ConfigureExcel(string name, bool enable)
        {
            string output = Begin(name);
            Require(enable == string.IsNullOrEmpty(SessionState.GetString(Key + ".ExcelSettings", "")), "Excel configuration ownership mismatch.");
            string symbols;
            if (enable)
            {
                Capture(output);
                symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone);
                Require(!symbols.Split(';').Contains("YL_DOMAIN_EXCEL"), "Do not take ownership of an existing product define.");
                SessionState.SetString(Key + ".ExcelSettings", SessionState.GetString(Key + ".Settings", ""));
                SessionState.SetString(Key + ".ExcelOrigin", output);
                SessionState.SetString(Key + ".ExcelDefines", symbols);
                SessionState.EraseString(Key + ".Settings");
                symbols += (string.IsNullOrEmpty(symbols) ? "" : ";") + "YL_DOMAIN_EXCEL";
            }
            else
            {
                symbols = SessionState.GetString(Key + ".ExcelDefines", "");
                SessionState.SetString(Key + ".Settings", SessionState.GetString(Key + ".ExcelSettings", ""));
                string origin = SessionState.GetString(Key + ".ExcelOrigin", "");
                File.Copy(origin + "/ProjectSettings.before.asset", output + "/ProjectSettings.before.asset");
                File.Copy(origin + "/EditorSettings.before.asset", output + "/EditorSettings.before.asset");
            }
            SessionState.SetString(Key + ".Configuration", enable ? "enable" : "restore");
            SessionState.SetBool(Key + ".ConfigurationReloaded", false);
            _configurationStableAfter = EditorApplication.timeSinceStartup + 5;
            EditorApplication.update -= FinishConfiguration;
            EditorApplication.update += FinishConfiguration;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone, symbols);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static void FinishConfiguration()
        {
            if (!SessionState.GetBool(Key + ".ConfigurationReloaded", false) || EditorApplication.isCompiling || EditorApplication.isUpdating || TestRunActive() || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _configurationStableAfter = EditorApplication.timeSinceStartup + 5;
                return;
            }
            if (EditorApplication.timeSinceStartup < _configurationStableAfter)
            {
                return;
            }

            EditorApplication.update -= FinishConfiguration;
            string output = SessionState.GetString(Key + ".Output", "");
            try
            {
                Identity();
                Require(!EditorUtility.scriptCompilationFailed, "Optional Excel compilation failed; retain original settings and evidence.");
                bool enable = SessionState.GetString(Key + ".Configuration", "") == "enable";
                Require(PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';').Contains("YL_DOMAIN_EXCEL") == enable,
                    "Actual define does not match the requested configuration.");
                Require(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "YLFramework.Domain.Excel.Editor") == enable,
                    "Loaded optional assembly does not match the compiled configuration.");
                if (!enable)
                {
                    Restore(output);
                    foreach (string suffix in new[] { ".ExcelSettings", ".ExcelOrigin", ".ExcelDefines" })
                    {
                        SessionState.EraseString(Key + suffix);
                    }
                }
                Write(output + "/configuration-complete.json", new
                {
                    passed = true,
                    enabled = enable,
                    state = Status(),
                    utc = DateTime.UtcNow
                });
                SessionState.EraseString(Key + ".Configuration");
                SessionState.EraseString(Key + ".Output");
                SessionState.EraseString(Key + ".Settings");
            }
            catch (Exception error)
            {
                Write(output + "/configuration-failed.json", new
                {
                    error = error.ToString()
                });
                throw;
            }
        }

        public static void QueueBuild(string name, bool il2cpp)
        {
            string output = Begin(name);
            Capture(output);
            SessionState.SetBool(Key + ".BuildIL2CPP", il2cpp);
            SessionState.SetBool(Key + ".BuildPending", true);
            Write(output + "/build-request.json", new
            {
                name,
                il2cpp,
                utc = DateTime.UtcNow,
                state = Status()
            });
            EditorApplication.update += BuildQueued;
        }

        private static void BuildQueued()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            EditorApplication.update -= BuildQueued;
            SessionState.EraseBool(Key + ".BuildPending");
            string output = SessionState.GetString(Key + ".Output", ""), run = Path.GetFileName(output);
            bool il2cpp = SessionState.GetBool(Key + ".BuildIL2CPP", false);
            var backend = il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x;
            var receipt = new Dictionary<string, object>
            {
                ["passed"] = false,
                ["backend"] = backend.ToString(),
                ["projectPath"] = Project,
                ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["unity"] = Application.unityVersion,
                ["run"] = run
            };
            try
            {
                Identity();
                Require(!TestRunActive() && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorUtility.scriptCompilationFailed && !BuildPipeline.isBuildingPlayer, "Unsafe build state.");
                Require(PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';').Contains("YL_DOMAIN_ACCEPTANCE"),
                    "The explicit Player host must enable YL_DOMAIN_ACCEPTANCE; ordinary production builds must not include the probe.");
                Require(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), "Windows64 build support unavailable.");
                Require(PlayerSettings.GetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone) == Il2CppCompilerConfiguration.Release, "The original compiler must be Release; do not insert an unrelated sparse compiler key.");
                if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) != backend)
                {
                    PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                }

                var stripping = il2cpp ? ManagedStrippingLevel.Low : ManagedStrippingLevel.Disabled;
                if (PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone) != stripping)
                {
                    PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, stripping);
                }

                string folder = "Assets/YLFramework/Tests/Fixtures/Extensions/Domain/Main-" + Guid.NewGuid().ToString("N");
                OwnPath(folder);
                string scene = YLFramework.Tests.DomainAcceptance.Editor.DomainAcceptanceAssets.Create(folder, "MC26Main_" + run, run, backend.ToString(), true);
                receipt["scene"] = scene;
                receipt["ownedFolder"] = folder;
                var graph = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                var runtime = graph.Single(a => a.name == "YLFramework.Runtime");
                var references = runtime.assemblyReferences.Select(a => a.name).Concat(runtime.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).Distinct().ToArray();
                Require(!references.Any(n => !Regex.IsMatch(n, @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$") || n.Contains("Tests") || n.Contains("TestRunner")), "Forbidden Runtime Player dependency.");
                Write(output + "/source-binding.json", new
                {
                    head = Git("rev-parse HEAD").Trim(),
                    candidate = "aadc11b5099b76f16667aa9818ca927104cd12be",
                    assemblies = graph.Select(a => new
                    {
                        a.name,
                        a.defines,
                        a.compiledAssemblyReferences,
                        sourceFiles = a.sourceFiles.Select(p => new { path = p.Replace('\\', '/'), sha256 = Hash(p) }).ToArray()
                    }),
                    consumerSceneSha256 = Hash(scene),
                    linkerSha256 = Hash("Assets/YLFramework/Tests/Fixtures/Extensions/Domain/link.xml")
                });
                string player = output + "/Player/DomainProbe.exe";
                Directory.CreateDirectory(Path.GetDirectoryName(player));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scene },
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    locationPathName = player,
                    options = BuildOptions.None
                });
                receipt["result"] = report.summary.result.ToString();
                receipt["errors"] = report.summary.totalErrors;
                receipt["warnings"] = report.summary.totalWarnings;
                receipt["options"] = report.summary.options.ToString();
                receipt["development"] = (report.summary.options & BuildOptions.Development) != 0;
                receipt["stripping"] = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone).ToString();
                receipt["compiler"] = PlayerSettings.GetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone).ToString();
                receipt["guid"] = report.summary.guid.ToString();
                receipt["seconds"] = report.summary.totalTime.TotalSeconds;
                receipt["runtimeReferences"] = references;
                receipt["executable"] = player;
                receipt["steps"] = report.steps.Select(s => new { s.name, seconds = s.duration.TotalSeconds, messages = s.messages.Select(m => new { m.content, type = m.type.ToString() }) });
                if (File.Exists("Library/LastBuild.buildreport"))
                {
                    File.Copy("Library/LastBuild.buildreport", output + "/LastBuild.buildreport");
                }

                Require(report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0 && report.summary.options == BuildOptions.None, "Actual Release build did not succeed.");
                var forbidden = Directory.GetFiles(output + "/Player", "*", SearchOption.AllDirectories).Where(p =>
                    Path.GetFileName(p).StartsWith("MiniExcel", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("UnityEditor", StringComparison.Ordinal) ||
                    Path.GetFileName(p).Contains("Domain.Excel.Editor") || Path.GetFileName(p).Contains("YLFramework.Excel.Editor")).ToArray();
                Require(forbidden.Length == 0, "Editor/backend files entered Player output.");
                if (il2cpp)
                {
                    Require(File.Exists(output + "/Player/GameAssembly.dll"), "Missing native IL2CPP output.");
                    string stripped = "Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped";
                    Directory.CreateDirectory(output + "/managed-stripped");
                    foreach (string assembly in new[] { "YLFramework.Runtime", typeof(YLFramework.Tests.DomainAcceptance.DomainAcceptanceProbe).Assembly.GetName().Name })
                    {
                        File.Copy(stripped + "/" + assembly + ".dll", output + "/managed-stripped/" + assembly + ".dll");
                    }
                }
                string licenses = output + "/Player/Licenses";
                Directory.CreateDirectory(licenses);
                File.Copy("Assets/YLFramework/LICENSE.md", licenses + "/YLFramework-MIT.md");
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Where(p => p.name == "com.unity.nuget.newtonsoft-json" || p.name == "com.unity.ugui"))
                {
                    foreach (string file in Directory.GetFiles(package.resolvedPath).Where(p => Regex.IsMatch(Path.GetFileName(p), "(?i)license|third.?party.*notices")))
                    {
                        File.Copy(file, licenses + "/" + package.name + "-" + Path.GetFileName(file));
                    }
                }

                Write(output + "/player-files.json", Directory.GetFiles(output + "/Player", "*", SearchOption.AllDirectories).Select(p => new { path = p, sha256 = Hash(p), length = new FileInfo(p).Length }));
                receipt["passed"] = true;
            }
            catch (Exception error)
            {
                receipt["error"] = error.ToString();
                Debug.LogException(error);
            }
            finally
            {
                Write(output + "/build.json", receipt);
                SessionState.SetBool(Key + ".BuildRestore", true);
                EditorApplication.update += FinishBuild;
            }
        }

        private static void FinishBuild()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || TestRunActive() || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            EditorApplication.update -= FinishBuild;
            string output = SessionState.GetString(Key + ".Output", "");
            try
            {
                Identity();
                Restore(output);
                Write(output + "/gate-complete.json", new
                {
                    closed = true,
                    utfInactive = !TestRunActive(),
                    pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    utc = DateTime.UtcNow
                });
                SessionState.EraseBool(Key + ".BuildRestore");
                SessionState.EraseBool(Key + ".BuildIL2CPP");
                SessionState.EraseString(Key + ".Output");
                SessionState.EraseString(Key + ".Settings");
            }
            catch (Exception error)
            {
                Write(output + "/restore-failed.json", new
                {
                    error = error.ToString()
                });
                throw;
            }
        }

        public static void Audit(string name)
        {
            string output = Begin(name);
            try
            {
                var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var player = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                bool excel = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';').Contains("YL_DOMAIN_EXCEL");
                string[] modules = { "Reddot", "Logging", "Skill", "Buff", "Mission" };
                var files = Directory.GetFiles("Assets/YLFramework", "*.cs", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/'))
                    .Where(p => modules.Any(m => p.StartsWith("Assets/YLFramework/Runtime/Systems/" + m + "/", StringComparison.Ordinal) ||
                        p.StartsWith("Assets/YLFramework/Editor/Systems/" + m + "/", StringComparison.Ordinal) ||
                        p.Contains("/Tests/EditMode/Systems/" + m + "/") || p.Contains("/Tests/PlayMode/Systems/" + m + "/")) ||
                        p.StartsWith("Assets/YLFramework/Editor/Systems/Shared/", StringComparison.Ordinal) ||
                        p.Contains("/Tests/EditMode/Editor/Domain/") || p.Contains("/Tests/PlayMode/Systems/Domain/") ||
                        p.Contains("/Tests/Tools/Extensions/Domain/") || (excel && p.Contains("/Integrations/DomainExcel/"))).ToArray();
                var rows = files.Select(p => new
                {
                    path = p,
                    sha256 = Hash(p),
                    assembly = graph.SingleOrDefault(a =>
                    a.sourceFiles.Any(f => Path.GetFullPath(f) == Path.GetFullPath(p)))?.name
                }).ToArray();
                foreach (var row in rows)
                {
                    string expected = row.path.StartsWith("Assets/YLFramework/Runtime/", StringComparison.Ordinal) ? "YLFramework.Runtime" :
                        row.path.StartsWith("Assets/YLFramework/Editor/", StringComparison.Ordinal) ? "YLFramework.Editor" :
                        row.path.Contains("/Tests/EditMode/") ? "YLFramework.Tests.EditMode" : row.path.Contains("/Tests/PlayMode/") ? "YLFramework.Tests.PlayMode" :
                        row.path.Contains("/Tests/Integrations/DomainExcel/EditMode/") ? "YLFramework.Domain.Excel.Tests" :
                        row.path.Contains("/Integrations/DomainExcel/Editor/") ? "YLFramework.Domain.Excel.Editor" : "Assembly-CSharp-Editor";
                    Require(row.assembly == expected, row.path + " -> " + row.assembly + ", expected " + expected);
                }
                var runtime = player.Single(a => a.name == "YLFramework.Runtime");
                var refs = runtime.assemblyReferences.Select(a => a.name).Concat(runtime.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).Distinct().ToArray();
                Require(!refs.Any(p => !Regex.IsMatch(p, @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$") || p.Contains("Tests") || p.Contains("TestRunner")), "Forbidden Runtime/Player dependency.");
                Require(!player.Any(a => a.name.Contains("Excel.Editor")), "Optional Excel entered the Player graph.");
                Require(excel || graph.All(a => !a.name.StartsWith("YLFramework.Domain.Excel.", StringComparison.Ordinal)), "Optional DomainExcel compiled without its define.");
                const string PluginPath = "Assets/YLFramework/Integrations/Excel/Editor/Plugins/MiniExcel.dll";
                var plugin = AssetImporter.GetAtPath(PluginPath) as PluginImporter;
                Require(plugin && plugin.GetCompatibleWithEditor() && !plugin.GetCompatibleWithAnyPlatform() &&
                    !plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64), "MiniExcel is not Editor-only.");
                Require(new FileInfo(PluginPath).Length == 200192 && Hash(PluginPath) == "0a49b66224bef9fdcec12800e4660267a993b1ece87727d9fe15c7dabf58ba1e", "Approved MiniExcel binary changed.");
                Write(output + "/excel-dependencies.json", new
                {
                    enabled = excel,
                    backendProductTip = "5dce4f3e39e2f4bd11602e65cabace5f56108cf8",
                    validatedPin = JObject.Parse(File.ReadAllText("Assets/YLFramework/Integrations/DomainExcel/Editor/Dependencies.json")),
                    sha256 = Hash(PluginPath),
                    editorOnly = true,
                    licenseSha256 = Hash("Assets/YLFramework/Integrations/Excel/Editor/Licenses/MiniExcel-LICENSE.txt")
                });
                var preflight = JObject.Parse(File.ReadAllText(Evidence + "/preflight.json"));
                var metas = preflight["candidatePaths"].Values<string>().Where(p => p.EndsWith(".meta", StringComparison.Ordinal)).ToArray();
                var tree = Git("ls-tree -r aadc11b5099b76f16667aa9818ca927104cd12be").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToDictionary(l => l.Substring(l.IndexOf('\t') + 1), l => l.Split('\t')[0].Split(' ')[2]);
                var currentHashes = Git("hash-object --stdin-paths", string.Join("\n", metas) + "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Require(currentHashes.Length == metas.Length, "Missing meta hash input.");
                var allGuids = Directory.EnumerateFiles("Assets", "*.meta", SearchOption.AllDirectories).Select(p => new
                {
                    path = p.Replace('\\', '/'),
                    guid = Regex.Match(File.ReadAllText("\\\\?\\" + Path.GetFullPath(p)), "(?m)^guid: ([0-9a-f]{32})\\s*$").Groups[1].Value
                }).Where(r => r.guid.Length == 32).GroupBy(r => r.guid).ToDictionary(g => g.Key, g => g.Select(r => r.path).ToArray());
                var identities = metas.Select((p, i) => new
                {
                    path = p,
                    expectedBlob = tree[p],
                    blob = currentHashes[i],
                    sha256 = Hash(p),
                    guid = Regex.Match(File.ReadAllText(p), "(?m)^guid: ([0-9a-f]{32})\\s*$").Groups[1].Value
                }).ToArray();
                foreach (var meta in identities)
                {
                    Require(meta.blob == meta.expectedBlob && meta.guid.Length == 32, "Candidate meta changed: " + meta.path);
                    Require(allGuids.TryGetValue(meta.guid, out var owners) && owners.Length == 1 && owners[0] == meta.path, "Raw meta identity collision: " + meta.path);
                    var assetPath = meta.path.Substring(0, meta.path.Length - 5);
                    Require(AssetDatabase.AssetPathToGUID(assetPath) == meta.guid && AssetDatabase.GUIDToAssetPath(meta.guid) == assetPath, "AssetDatabase identity/owner mismatch: " + meta.path);
                }
                string[] assemblies = rows.Select(r => r.assembly).Distinct().ToArray();
                Directory.CreateDirectory(output + "/assembly-inputs");
                int number = 0;
                foreach (string file in Directory.GetFiles("Library/Bee", "*.rsp", SearchOption.AllDirectories)
                    .Where(p => assemblies.Any(a => Path.GetFileName(p).StartsWith(a + ".", StringComparison.Ordinal))))
                {
                    File.Copy(file, output + "/assembly-inputs/" + (++number).ToString("D3") + "-" + Path.GetFileName(file));
                }

                Write(output + "/source-owners.json", rows);
                Write(output + "/meta-audit.json", identities);
                Write(output + "/compilation.json", new
                {
                    editor = graph.Where(a => assemblies.Contains(a.name)).Select(a => new { a.name, a.sourceFiles, a.defines, a.compiledAssemblyReferences }),
                    player = player.Where(a => a.name.StartsWith("YLFramework", StringComparison.Ordinal)).Select(a => new { a.name, a.sourceFiles, a.defines, a.compiledAssemblyReferences }),
                    editorBinary = AppDomain.CurrentDomain.GetAssemblies().Where(a => assemblies.Contains(a.GetName().Name)).Select(a => new
                    {
                        name = a.GetName().Name,
                        path = a.Location,
                        sha256 = Hash(a.Location),
                        references = a.GetReferencedAssemblies().Select(r => r.FullName).ToArray()
                    })
                });
                Write(output + "/audit-complete.json", new
                {
                    passed = true,
                    excel,
                    sources = rows.Length,
                    candidateMetas = metas.Length,
                    responseFiles = number,
                    state = Status(),
                    sourceHead = Git("rev-parse HEAD").Trim()
                });
            }
            finally
            {
                SessionState.EraseString(Key + ".Output");
            }
        }

        private static string Git(string arguments, string input = null)
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = Project,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = input != null
                }
            };
            process.Start();
            if (input != null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            string output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Require(process.ExitCode == 0, "Git read failed: " + errors);
            return output;
        }

        public static void ArchiveOwnedAssets(string archiveName = "archived-assets")
        {
            Identity();
            Require(SessionState.GetBool(Key + ".Protection", false) && string.IsNullOrEmpty(SessionState.GetString(Key + ".Output", "")) &&
                !TestRunActive() && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !BuildPipeline.isBuildingPlayer,
                "All gates must finish before archiving owned fixtures.");
            Require(Regex.IsMatch(archiveName, "^archived-[a-z0-9-]+$"), "Invalid archive identity.");
            string archive = Evidence + "/" + archiveName;
            Require(!Directory.Exists(archive), "Do not overwrite an archive.");
            var paths = JsonConvert.DeserializeObject<List<string>>(SessionState.GetString(Key + ".Owned", "[]"));
            var generated = new List<object>();
            var roots = new List<string>();
            foreach (string path in paths.Where(p => Directory.Exists(p) && Regex.IsMatch(p, "^Assets/(Domain(Fixture|ReviewFixture|ExcelFixture)-[0-9a-f]{32}|YLFramework/Tests/Fixtures/Extensions/Domain/Main-[0-9a-f]{32})$")).ToArray())
            {
                foreach (string guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { path }))
                {
                    string asset = AssetDatabase.GUIDToAssetPath(guid);
                    var root = AssetDatabase.LoadMainAssetAtPath(asset);
                    string folder = root is YLFramework.Skill.SkillsConfig ? "Assets/YLFrameworkData/Skill/Skills/" + guid :
                        root is YLFramework.Buff.BuffsConfig ? "Assets/YLFrameworkData/Buff/Buffs/" + guid :
                        root is YLFramework.Mission.MissionsConfig ? "Assets/YLFrameworkData/Mission/" + guid : null;
                    if (folder == null)
                    {
                        continue;
                    }

                    roots.Add(asset);
                    if (Directory.Exists(folder))
                    {
                        paths.Add(folder);
                        generated.Add(new
                        {
                            asset,
                            guid,
                            folder
                        });
                    }
                }
            }
            paths = paths.Distinct().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            foreach (string path in paths)
            {
                Require(Regex.IsMatch(path, "^Assets/(Domain(Fixture|ReviewFixture|ExcelFixture)-[0-9a-f]{32}|InitTestScene[0-9]+[.]unity|YLFramework/Tests/Fixtures/Extensions/Domain/Main-[0-9a-f]{32}|YLFrameworkData/(Skill/Skills|Buff/Buffs|Mission)/[0-9a-f]{32})$"), "Never archive an unattributed path.");
            }

            Directory.CreateDirectory(archive);
            var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*", SearchOption.AllDirectories).Concat(new[] { p + ".meta" }) : new[] { p, p + ".meta" })
                .Distinct().Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string file in files)
            {
                for (string cursor = Path.GetFullPath(file); cursor != null; cursor = Path.GetDirectoryName(cursor))
                {
                    Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "Do not follow linked archive content.");
                }

                string target = archive + "/" + file;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
                Require(Hash(file) == Hash(target), "Archive copy changed.");
            }
            Write(Evidence + "/" + archiveName + "-receipt.json", new
            {
                paths,
                generated,
                files = files.Select(p => new { path = p, sha256 = Hash(p) }).ToArray(),
                utc = DateTime.UtcNow
            });
            // Unity删除父目录不逐个调用子根的删除钩子；先归还本轮根的GUID自有目录。
            foreach (string root in roots.Distinct())
            {
                if (File.Exists(root))
                {
                    Require(AssetDatabase.DeleteAsset(root), "Could not delete owned root: " + root);
                }
            }

            foreach (string path in paths.OrderByDescending(p => p.Length))
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    Require(AssetDatabase.DeleteAsset(path), "Could not delete owned fixture: " + path);
                }
            }

            var before = JObject.Parse(File.ReadAllText(Evidence + "/preflight.json"));
            var started = (DateTime)JObject.Parse(File.ReadAllText(Evidence + "/owner.json"))["utc"];
            var parents = new List<string>();
            foreach (string parent in new[] { "Assets/YLFrameworkData/Skill/Skills", "Assets/YLFrameworkData/Buff/Buffs", "Assets/YLFrameworkData/Skill", "Assets/YLFrameworkData/Buff", "Assets/YLFrameworkData/Mission", "Assets/YLFrameworkData" })
            {
                if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    continue;
                }

                if (!File.Exists(parent + ".meta") || File.GetCreationTimeUtc(parent + ".meta") < started ||
                    ((string)before["status"]).Contains("? " + parent + ".meta") || Git("ls-files -- " + parent + ".meta").Length != 0)
                {
                    continue;
                }

                string target = archive + "/" + parent + ".meta";
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(parent + ".meta", target, false);
                Require(AssetDatabase.DeleteAsset(parent), "Could not delete this Run's empty parent.");
                parents.Add(parent);
            }
            Write(Evidence + "/" + archiveName + "-complete.json", new
            {
                passed = paths.All(p => !File.Exists(p) && !Directory.Exists(p)),
                archived = files.Length,
                removedEmptyCreatedParents = parents,
                utc = DateTime.UtcNow
            });
        }

        public static void FinishProtection()
        {
            Identity();
            Require(string.IsNullOrEmpty(SessionState.GetString(Key + ".Output", "")) && !TestRunActive() && !EditorApplication.isPlayingOrWillChangePlaymode &&
                !EditorApplication.isCompiling && !EditorApplication.isUpdating && !BuildPipeline.isBuildingPlayer && !EditorUtility.scriptCompilationFailed,
                "Wait for the real Editor terminal state.");
            Require(!SessionState.GetBool(Key + ".PendingRestore", false) && !SessionState.GetBool(Key + ".BuildRestore", false) &&
                string.IsNullOrEmpty(SessionState.GetString(Key + ".ExcelSettings", "")) && string.IsNullOrEmpty(SessionState.GetString(Key + ".Settings", "")), "A settings owner remains active.");
            var baseline = JObject.Parse(File.ReadAllText(Evidence + "/editor-before.json"));
            var dirty = baseline["dirty"].Select(d =>
            {
                var obj = AssetDatabase.LoadMainAssetAtPath((string)d["path"]);
                bool same = obj && JToken.DeepEquals(JObject.Parse((string)d["serialized"]), JObject.Parse(EditorJsonUtility.ToJson(obj)));
                Require(obj && EditorUtility.IsDirty(obj) && same, "Original dirty asset memory changed: " + d["path"]);
                return new
                {
                    path = (string)d["path"],
                    dirtyPreserved = true,
                    memoryPreserved = same
                };
            }).ToArray();
            Require(JToken.DeepEquals(baseline["scenes"], JToken.FromObject(EditorSceneManager.GetSceneManagerSetup())), "Original scene set changed.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Require(!SceneManager.GetSceneAt(i).isDirty, "Unexpected dirty scene.");
            }

            Require(JToken.DeepEquals(JObject.Parse((string)baseline["playerSettings"]), JObject.Parse(EditorJsonUtility.ToJson(PlayerSettingsObject()))), "Original PlayerSettings memory changed.");
            Require(EditorSettings.enterPlayModeOptionsEnabled == (bool)baseline["enterPlayModeOptionsEnabled"] && (int)EditorSettings.enterPlayModeOptions == (int)baseline["reload"], "Domain Reload settings not restored.");
            string[] intentional = { "Assets/YLFramework/LICENSE.md", "Assets/YLFramework/Documentation/README.md", "Assets/YLFramework/Documentation/Architecture.md" };
            var changed = JArray.Parse(File.ReadAllText(Evidence + "/protected-files.json")).Where(row =>
                !intentional.Contains((string)row["path"]) && !((string)row["path"]).StartsWith(".kilo/plans/", StringComparison.Ordinal))
                .Where(row => !File.Exists((string)row["path"]) || Hash((string)row["path"]) != (string)row["sha256"]).Select(row => (string)row["path"]).ToArray();
            Require(changed.Length == 0, "Protected user/game/settings/package bytes changed: " + string.Join(", ", changed));
            Selection.instanceIDs = baseline["selection"].Values<int>().Where(id => EditorUtility.InstanceIDToObject(id)).ToArray();
            SessionState.EraseBool(Key + ".Protection");
            SessionState.EraseString(Key + ".Owned");
            Write(Evidence + "/protection-complete.json", new
            {
                passed = true,
                protectedUserBytesPreserved = true,
                dirty,
                selectionRestored = true,
                settingsMemoryPreserved = true,
                originalScenesPreserved = true,
                fenceReleased = true,
                state = Status(),
                utc = DateTime.UtcNow
            });
        }

        public static void Identity()
        {
            var owner = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(Evidence + "/owner.json"));
            Require(Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)).Replace('\\', '/') == Project &&
                Application.unityVersion == "2022.3.62f3c1" &&
                Convert.ToInt32(owner["pid"]) == System.Diagnostics.Process.GetCurrentProcess().Id &&
                (string)owner["projectPath"] == Project &&
                (string)owner["sourceTip"] == "aadc11b5099b76f16667aa9818ca927104cd12be" &&
                File.Exists(Evidence + "/preflight.json"), "Wrong main project, PID or frozen input.");
        }

        public static void Protect()
        {
            Identity();
            SessionState.SetBool(Key + ".Protection", true);
            Write(Evidence + "/protection-started.json", new
            {
                utc = DateTime.UtcNow,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id
            });
        }

        public static void OwnPath(string path)
        {
            Identity();
            Require(SessionState.GetBool(Key + ".Protection", false), "Main protection is not active.");
            Require((Regex.IsMatch(path, "^Assets/Domain(Fixture|ReviewFixture|ExcelFixture)-[0-9a-f]{32}$") ||
                Regex.IsMatch(path, "^Assets/YLFramework/Tests/Fixtures/Extensions/Domain/Main-[0-9a-f]{32}$") ||
                (TestRunActive() && Regex.IsMatch(path, "^Assets/InitTestScene[0-9]+[.]unity$"))) &&
                !File.Exists(path) && !Directory.Exists(path), "Cannot claim existing or external content.");
            var paths = JsonConvert.DeserializeObject<List<string>>(SessionState.GetString(Key + ".Owned", "[]"));
            paths.Add(path);
            SessionState.SetString(Key + ".Owned", JsonConvert.SerializeObject(paths));
            Write(Evidence + "/owned-paths.json", paths);
        }

        internal static string[] FilterSaves(string[] paths)
        {
            if (!SessionState.GetBool(Key + ".Protection", false))
            {
                return paths;
            }

            Identity();
            var owned = JsonConvert.DeserializeObject<List<string>>(SessionState.GetString(Key + ".Owned", "[]"));
            foreach (string path in paths)
            {
                var match = Regex.Match(path, "^(Assets/YLFrameworkData/(?:Skill/Skills|Buff/Buffs|Mission)/([0-9a-f]{32}))(?:/|$)");
                if (!match.Success || owned.Contains(match.Groups[1].Value))
                {
                    continue;
                }

                string rootAsset = AssetDatabase.GUIDToAssetPath(match.Groups[2].Value);
                if (!owned.Any(root => rootAsset.StartsWith(root + "/", StringComparison.Ordinal)))
                {
                    continue;
                }
                // 仅认本轮已登记根的 GUID 目录，不能因路径前缀放行其他用户配表。
                owned.Add(match.Groups[1].Value);
                SessionState.SetString(Key + ".Owned", JsonConvert.SerializeObject(owned));
                Write(Evidence + "/owned-paths.json", owned);
            }
            var allowed = paths.Where(p => owned.Any(root => p == root || p.StartsWith(root + "/", StringComparison.Ordinal))).ToArray();
            var deferred = paths.Except(allowed).ToArray();
            if (deferred.Length != 0)
            {
                File.AppendAllText(Evidence + "/deferred-saves.jsonl", JsonConvert.SerializeObject(new
                {
                    utc = DateTime.UtcNow,
                    deferred
                }) + Environment.NewLine);
            }

            return allowed;
        }

        internal static void RegisterCreated(string path)
        {
            if (!SessionState.GetBool(Key + ".Protection", false) || !TestRunActive())
            {
                return;
            }

            if (path.EndsWith(".meta", StringComparison.Ordinal))
            {
                path = path.Substring(0, path.Length - 5);
            }

            if (Regex.IsMatch(path, "^Assets/InitTestScene[0-9]+[.]unity$") && !File.Exists(path))
            {
                OwnPath(path);
            }
        }

        internal static void Write(string path, object value)
        {
            string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(pending, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path))
            {
                File.Replace(pending, path, null);
            }
            else
            {
                File.Move(pending, path);
            }
        }

        internal static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    internal sealed class DomainMainSaveFence : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths) => DomainMainAcceptance.FilterSaves(paths);
        private static void OnWillCreateAsset(string path) => DomainMainAcceptance.RegisterCreated(path);
    }
}
