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
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Tests.DataBatch
{
    // 主工程接收入口。保留用户 Editor 和脏资产，不借用 B/A 的证据身份。
    [InitializeOnLoad]
    public static class DataMainAcceptance
    {
        public const string Evidence = "G:/Unity/MB26";
        private const string Project = "G:/Unity/Projects/CGJ2024";
        private const string Key = "YLFramework.Data.MainAcceptance";
        private static readonly Results _callback = new Results();

        [Serializable]
        private sealed class Map
        {
            public string field;
            public string[] keys;
            public int[] values;
        }
        [Serializable]
        private sealed class Settings
        {
            public string company;
            public string product;
            public string sha256;
            public string serialized;
            public bool wasDirty;
            public Map[] maps;
            public Dictionary<string, string> environment;
        }

        static DataMainAcceptance()
        {
            TestRunnerApi.RegisterTestCallback(_callback);
            if (SessionState.GetBool(Key + ".PendingRestore", false))
            {
                EditorApplication.update += FinishTests;
            }
        }

        private static bool TestRunActive() => (bool)typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

        public static string Canonical(string path) => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Replace('\\', '/').TrimEnd('/');

        public static void Identity()
        {
            if (Canonical(Path.GetDirectoryName(Application.dataPath)) != Project || Application.unityVersion != "2022.3.62f3c1")
            {
                throw new InvalidOperationException("Wrong main project or Unity version.");
            }

            var owner = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Evidence + "/owner.json")));
            if (Canonical((string)owner["projectPath"]) != Project || Convert.ToInt32(owner["pid"]) != System.Diagnostics.Process.GetCurrentProcess().Id ||
                (string)owner["sourceTip"] != "5dce4f3e39e2f4bd11602e65cabace5f56108cf8" || !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Evidence + "/preflight.json")))
            {
                throw new InvalidOperationException("Main evidence/PID/source binding changed.");
            }
        }

        public static object Status() => new
        {
            projectPath = Canonical(Path.GetDirectoryName(Application.dataPath)),
            pid = System.Diagnostics.Process.GetCurrentProcess().Id,
            Application.unityVersion,
            EditorApplication.isCompiling,
            EditorApplication.isUpdating,
            EditorApplication.isPlaying,
            EditorUtility.scriptCompilationFailed,
            building = BuildPipeline.isBuildingPlayer,
            output = SessionState.GetString(Key, ""),
            scenes = EditorSceneManager.GetSceneManagerSetup(),
            EditorSettings.enterPlayModeOptionsEnabled,
            EditorSettings.enterPlayModeOptions
        };

        private static string Begin(string name)
        {
            Identity();
            Require(string.IsNullOrEmpty(SessionState.GetString(Key, "")), "Another main data gate is active.");
            Require(string.IsNullOrEmpty(SessionState.GetString(Key + ".Settings", "")) && !TestRunActive(), "A previous test/settings owner has not closed.");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                !EditorUtility.scriptCompilationFailed && !BuildPipeline.isBuildingPlayer, "Editor is not ready.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Require(!SceneManager.GetSceneAt(i).isDirty && !string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path), "Do not replace dirty/untitled scenes.");
            }

            Require(PrefabStageUtility.GetCurrentPrefabStage() == null, "Do not replace a user's prefab stage.");
            Require(Regex.IsMatch(name, "^[a-z0-9-]+$"), "Invalid run name.");
            string output = Evidence + "/" + name;
            Require(!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), "Never overwrite evidence.");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(output));
            Write(output + "/editor-before.json", Status());
            SessionState.SetString(Key, output);
            return output;
        }

        public static string Output()
        {
            Identity();
            string output = SessionState.GetString(Key, "");
            Require(!string.IsNullOrEmpty(output) && Canonical(output).StartsWith(Evidence + "/", StringComparison.Ordinal) && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), "No owned main gate.");
            return output;
        }

        private static UnityEngine.Object PlayerSettingsObject() => (UnityEngine.Object)typeof(PlayerSettings)
            .GetMethod("InternalGetPlayerSettingsObject", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

        private static Settings Capture(string output)
        {
            var settings = PlayerSettingsObject();
            var maps = new List<Map>();
            using (var serialized = new SerializedObject(settings))
            {
                serialized.Update();
                foreach (string field in new[] { "scriptingBackend", "managedStrippingLevel" })
                {
                    var property = serialized.FindProperty(field);
                    var items = Enumerable.Range(0, property.arraySize).Select(property.GetArrayElementAtIndex).ToArray();
                    maps.Add(new Map
                    {
                        field = field,
                        keys = items.Select(p => p.FindPropertyRelative("first").stringValue).ToArray(),
                        values = items.Select(p => p.FindPropertyRelative("second").intValue).ToArray()
                    });
                }
            }
            var state = new Settings
            {
                company = PlayerSettings.companyName,
                product = PlayerSettings.productName,
                sha256 = Hash("ProjectSettings/ProjectSettings.asset"),
                maps = maps.ToArray(),
                serialized = EditorJsonUtility.ToJson(settings),
                wasDirty = EditorUtility.IsDirty(settings),
                environment = new Dictionary<string, string>()
            };
            foreach (string variable in new[] { "YL_DATA_UNITY_OUTPUT", "YL_SAVE_TEST_OUTPUT", "YL_EXCEL_TEST_OUTPUT" })
            {
                state.environment.Add(variable, Environment.GetEnvironmentVariable(variable));
            }

            File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/ProjectSettings.asset"), global::YLFramework.Testing.PackageTestPaths.Physical(output + "/ProjectSettings.before.asset"));
            Write(output + "/settings-before.json", state);
            SessionState.SetString(Key + ".Settings", JsonConvert.SerializeObject(state));
            return state;
        }

        private static void Restore(string output)
        {
            var state = JsonConvert.DeserializeObject<Settings>(SessionState.GetString(Key + ".Settings", ""));
            if (state == null)
            {
                return;
            }

            var settings = PlayerSettingsObject();
            var expected = JObject.Parse(state.serialized);
            var observed = JObject.Parse(EditorJsonUtility.ToJson(settings));
            foreach (string field in new[] { "companyName", "productName", "scriptingBackend", "managedStrippingLevel" })
            {
                ((JObject)expected["PlayerSettings"]).Remove(field);
                ((JObject)observed["PlayerSettings"]).Remove(field);
            }
            Require(JToken.DeepEquals(expected, observed), "Unrelated in-memory PlayerSettings changed; preserve the live object.");
            PlayerSettings.companyName = state.company;
            PlayerSettings.productName = state.product;
            using (var serialized = new SerializedObject(settings))
            {
                serialized.Update();
                foreach (var map in state.maps)
                {
                    var property = serialized.FindProperty(map.field);
                    property.arraySize = map.keys.Length;
                    for (int i = 0; i < map.keys.Length; i++)
                    {
                        var item = property.GetArrayElementAtIndex(i);
                        item.FindPropertyRelative("first").stringValue = map.keys[i];
                        item.FindPropertyRelative("second").intValue = map.values[i];
                    }
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Require(JToken.DeepEquals(JObject.Parse(EditorJsonUtility.ToJson(settings)), JObject.Parse(state.serialized)), "In-memory settings were not restored.");
            // Build/UTF可能保存PlayerSettings。只接受本轮四项设置的差异，不保存整个脏对象。
            string beforeFile = output + "/ProjectSettings.before.asset";
            string currentHash = Hash("ProjectSettings/ProjectSettings.asset");
            if (currentHash != state.sha256)
            {
                string original = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(beforeFile));
                string normalized = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/ProjectSettings.asset"));
                foreach (string field in new[] { "companyName", "productName" })
                {
                    string pattern = "(?m)^  " + field + ":.*$";
                    string originalLine = Regex.Match(original, pattern).Value;
                    Require(originalLine.Length != 0 && Regex.Matches(normalized, pattern).Count == 1, "Unexpected settings layout.");
                    normalized = Regex.Replace(normalized, pattern, _ => originalLine);
                }
                foreach (string field in new[] { "scriptingBackend", "managedStrippingLevel" })
                {
                    string pattern = "(?ms)^  " + field + ":.*?(?=^  [^ ]|\\z)";
                    string originalBlock = Regex.Match(original, pattern).Value;
                    string currentBlock = Regex.Match(normalized, pattern).Value;
                    string standalone = "(?m)^    Standalone: [0-9]+\\r?\\n";
                    Require(originalBlock.Length != 0 && Regex.Replace(originalBlock, standalone, "") == Regex.Replace(currentBlock, standalone, ""), "Unrelated platform settings changed.");
                    normalized = Regex.Replace(normalized, pattern, _ => originalBlock);
                }
                Require(normalized == original && currentHash == Hash("ProjectSettings/ProjectSettings.asset"), "Unattributed disk settings change; do not overwrite it.");
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/ProjectSettings.asset"), global::YLFramework.Testing.PackageTestPaths.Physical(output + "/ProjectSettings.owned-side-effect.asset"));
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(beforeFile), global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/ProjectSettings.asset"), true);
            }
            bool restored = Hash("ProjectSettings/ProjectSettings.asset") == state.sha256;
            if (restored && !state.wasDirty)
            {
                EditorUtility.ClearDirty(settings);
            }
            else if (state.wasDirty)
            {
                EditorUtility.SetDirty(settings);
            }

            foreach (var variable in state.environment)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }

            Write(output + "/settings-restored.json", new
            {
                restored,
                before = state.sha256,
                after = Hash("ProjectSettings/ProjectSettings.asset"),
                memoryRestored = true,
                state.wasDirty,
                state.maps
            });
            Require(restored, "PlayerSettings bytes were not restored. Preserve evidence and inspect, never restore an unrelated HEAD.");
            SessionState.EraseString(Key + ".Settings");
        }

        public static void RunTests(string mode, string name, string scope = "Data")
        {
            string output = Begin(name);
            try
            {
                Capture(output);
                PlayerSettings.companyName = "YLFrameworkTests";
                PlayerSettings.productName = "YL-MainBData-" + name;
                Require(Application.persistentDataPath.Contains("YL-MainBData-"), "Test profile is not isolated.");
                foreach (string variable in new[] { "YL_DATA_UNITY_OUTPUT", "YL_SAVE_TEST_OUTPUT", "YL_EXCEL_TEST_OUTPUT" })
                {
                    Environment.SetEnvironmentVariable(variable, output);
                }

                Write(output + "/profile.json", new
                {
                    Application.persistentDataPath,
                    PlayerSettings.companyName,
                    PlayerSettings.productName
                });
                SessionState.SetBool(Key + ".SaveFence", true);
                string[] groups = scope == "Data" ? new[] { "^YLFramework[.]Tests[.]Extensions[.]Data[.]", "^YLFramework[.]Tests[.]DataAssets[.]", "^YLFramework[.]Tests[.]SerializedDictionarySaveTests[.]" } :
                    scope == "Boundary" ? new[] { "^YLFramework[.]Foundation[.]Tests[.]FoundationBoundaryTests[.]", "^YLFramework[.]Core[.]Tests[.]CoreArchitectureBoundaryTests[.]", "^YLFramework[.]Tests[.]Resource[.]FrameworkAssemblyIndependenceTests[.]", "^YLFramework[.]Core[.]Tests[.]CoreIdentityTests[.]", "^YLFramework[.]Tests[.]ExportUnityPackageTests[.]" } :
                    scope == "Behavior" ? new[] { "^YLFramework[.]Tests[.]Extensions[.]Behavior[.]" } :
                    scope == "ResourceUI" ? new[] { "^YLFramework[.]Tests[.]Resource[.]ResourceSystemTests[.]", "^YLFramework[.]Tests[.]Resource[.]ResourceImageTests[.]", "^YLFramework[.]Core[.]Tests[.]CoreResourcePlayModeTests[.]" } : throw new ArgumentException("Unknown exact scope.");
                var filter = new Filter { testMode = mode == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode, groupNames = groups };
                SessionState.SetString(Key + ".Groups", JsonConvert.SerializeObject(groups));
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                try
                {
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
            catch
            {
                try
                {
                    Restore(output);
                }
                finally
                {
                    End();
                }
                throw;
            }
        }

        private sealed class Results : ICallbacks
        {
            private static IEnumerable<ITestAdaptor> Leaves(ITestAdaptor test) => test.HasChildren ? test.Children.SelectMany(Leaves) : new[] { test };
            public void RunStarted(ITestAdaptor tests)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(Key, "")))
                {
                    return;
                }

                var all = Leaves(tests).Select(t => t.FullName).ToArray();
                var groups = JsonConvert.DeserializeObject<string[]>(SessionState.GetString(Key + ".Groups", "[]"));
                Write(Output() + "/discovery-all.json", all);
                Write(Output() + "/discovered.json", all.Where(t => groups.Any(g => Regex.IsMatch(t, g))).ToArray());
            }
            public void TestStarted(ITestAdaptor test)
            {
            }
            public void TestFinished(ITestResultAdaptor result)
            {
            }
            public void RunFinished(ITestResultAdaptor result)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(Key, "")))
                {
                    return;
                }

                string output = Output();
                try
                {
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/results.xml"), result.ToXml().OuterXml);
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
                }
                finally
                {
                    SessionState.SetBool(Key + ".PendingRestore", true);
                    EditorApplication.update -= FinishTests;
                    EditorApplication.update += FinishTests;
                }
            }
        }

        private static void FinishTests()
        {
            if (TestRunActive() || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            EditorApplication.update -= FinishTests;
            SessionState.EraseBool(Key + ".PendingRestore");
            string output = Output();
            try
            {
                Restore(output);
            }
            catch (Exception error)
            {
                Write(output + "/restore-failed.json", new
                {
                    error = error.ToString()
                });
                throw;
            }
            finally
            {
                End();
            }
            Write(output + "/gate-complete.json", new
            {
                closed = true,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                settingsSha256 = Hash("ProjectSettings/ProjectSettings.asset")
            });
        }

        public static string ExpectedSourceOwner(string path)
        {
            var value = path.Replace('\\', '/');
            if (value.StartsWith("Packages/com.kano.ylframework.localization/Runtime/", StringComparison.Ordinal))
                return "YLFramework.Localization";
            if (value.StartsWith("Packages/com.kano.ylframework.localization/Editor/", StringComparison.Ordinal))
                return "YLFramework.Localization.Editor";
            if (value.StartsWith("Packages/com.kano.ylframework.data-assets/Runtime/", StringComparison.Ordinal))
                return "YLFramework.DataAssets";
            if (value.StartsWith("Packages/com.kano.ylframework.data-assets/Editor/", StringComparison.Ordinal))
                return "YLFramework.DataAssets.Editor";
            if (value.StartsWith("Packages/com.kano.ylframework.save/Runtime/", StringComparison.Ordinal))
                return "YLFramework.Save";
            if (value.StartsWith("Assets/Tests/YLFramework/EditMode/Systems/Save/", StringComparison.Ordinal))
                return "YLFramework.Save.Tests.EditMode";
            if (value.StartsWith("Assets/Tests/YLFramework/Fixtures/Save/Runtime/", StringComparison.Ordinal))
                return "YLFramework.Save.Acceptance";
            bool system = value.Contains("/Systems/Localization/");
            bool dataTest = value.Contains("/Systems/DataAssets/") || system || value.Contains("/Editor/DataAssets/") ||
                value.Contains("/Editor/Localization/") || value.Contains("/Editor/Excel/") || value.Contains("/Editor/Data/");
            if (dataTest && value.StartsWith("Assets/Tests/YLFramework/EditMode/", StringComparison.Ordinal))
                return "YLFramework.Tests.EditMode";
            if (dataTest && value.StartsWith("Assets/Tests/YLFramework/PlayMode/", StringComparison.Ordinal))
                return "YLFramework.Tests.PlayMode";
            if (value.StartsWith("Assets/Tests/YLFramework/Fixtures/Authoring/Data/Runtime/", StringComparison.Ordinal))
                return "YLFramework.Tests.Data.Probes";
            if (value.StartsWith("Assets/Tests/YLFramework/Fixtures/Authoring/Data/Editor/", StringComparison.Ordinal))
                return "YLFramework.Tests.Tools.Editor";
            return null;
        }

        public static void Audit(string name)
        {
            string output = Begin(name);
            try
            {
                DataUnityAcceptance.Audit();
                var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var roots = new[] { "Packages/com.kano.ylframework", "Packages/com.kano.ylframework.data-assets", "Packages/com.kano.ylframework.localization", "Packages/com.kano.ylframework.save", "Assets/Tests/YLFramework" };
                var files = roots.SelectMany(root => Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*.cs", SearchOption.AllDirectories)
                    .Select(path => root + "/" + Path.GetRelativePath(global::YLFramework.Testing.PackageTestPaths.Physical(root), path).Replace('\\', '/')))
                    .Where(p => ExpectedSourceOwner(p) != null).ToArray();
                var rows = files.Select(p => new { path = p, sha256 = Hash(p), assembly = graph.SingleOrDefault(a => a.sourceFiles.Any(f => Canonical(f) == Canonical(p)))?.name }).ToArray();
                Require(rows.All(r => r.assembly != null), "New source was omitted from actual Unity compilation.");
                foreach (var row in rows)
                {
                    string expected = ExpectedSourceOwner(row.path);
                    Require(row.assembly == expected, row.path + " compiled into " + row.assembly + ", expected " + expected);
                }
                Write(output + "/source-owners.json", rows);
                Write(output + "/compilation.json", graph.Where(a => rows.Any(r => r.assembly == a.name)).Select(a => new { a.name, a.sourceFiles, a.defines, a.compiledAssemblyReferences }));
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/assembly-inputs"));
                int number = 0;
                foreach (string file in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical("Library/Bee"), "*.rsp", SearchOption.AllDirectories).Where(p => rows.Any(r => Path.GetFileName(p).StartsWith(r.assembly + ".", StringComparison.Ordinal))))
                {
                    File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(file), global::YLFramework.Testing.PackageTestPaths.Physical(output + "/assembly-inputs/" + (++number).ToString("D3") + "-" + Path.GetFileName(file)));
                }
            }
            finally
            {
                End();
            }
        }

        public static void BuildMono(string name) => Build(name, false);
        public static void BuildIL2CPP(string name) => Build(name, true);

        private static void Build(string name, bool il2cpp)
        {
            string output = Begin(name);
            try
            {
                Require(EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64, "Do not change the user's active platform.");
                Require(PlayerSettings.GetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone) == Il2CppCompilerConfiguration.Release, "IL2CPP compiler must be Release.");
                Capture(output);
                SessionState.SetBool(Key + ".SaveFence", true);
                if (il2cpp)
                {
                    DataUnityAcceptance.BuildIL2CPP();
                }
                else
                {
                    DataUnityAcceptance.BuildMono();
                }
            }
            finally
            {
                try
                {
                    Restore(output);
                }
                catch (Exception error)
                {
                    Write(output + "/restore-failed.json", new
                    {
                        error = error.ToString()
                    });
                    throw;
                }
                finally
                {
                    End();
                }
                Write(output + "/gate-complete.json", new
                {
                    closed = true,
                    pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    settingsSha256 = Hash("ProjectSettings/ProjectSettings.asset")
                });
            }
        }

        internal static void OwnBuildFolder(string folder)
        {
            Require(Regex.IsMatch(folder, "^Assets/Tests/YLFramework/Fixtures/Extensions/Data/Player-[0-9a-f]{32}$"), "Unowned build fixture.");
            SessionState.SetString(Key + ".Assets", folder);
            Write(Output() + "/owned-fixture.json", new
            {
                folder
            });
        }

        public static void OwnTestPath(string path)
        {
            Output();
            Require(!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), "Do not claim an existing test path.");
            Require(Regex.IsMatch(path, "^Assets/(YLExcel-|YLExcelBinding-|YLWorkbookMemory-|__YLDataAssets_)[0-9a-f]{32}$") ||
                Regex.IsMatch(path, "^Assets/Tests/YLFramework/Fixtures/Extensions/Data/Localization/Run-[0-9a-f]{32}[.]asset$") ||
                Regex.IsMatch(path, "^Assets/YLFrameworkData/(DataAssets|Localization)/[0-9a-f]{32}$"), "Unowned test fixture path.");
            var owned = JsonConvert.DeserializeObject<List<string>>(SessionState.GetString(Key + ".Owned", "[]"));
            if (path.StartsWith("Assets/YLFrameworkData/", StringComparison.Ordinal))
            {
                string root = AssetDatabase.GUIDToAssetPath(Path.GetFileName(path));
                Require(owned.Any(p => root == p || root.StartsWith(p + "/", StringComparison.Ordinal)), "Data folder has no owned root.");
            }
            owned.Add(path);
            SessionState.SetString(Key + ".Owned", JsonConvert.SerializeObject(owned));
            Write(Output() + "/owned-test-paths.json", owned);
        }

        internal static string[] FilterSaves(string[] paths)
        {
            if (!SessionState.GetBool(Key + ".SaveFence", false))
            {
                return paths;
            }

            Identity();
            string folder = SessionState.GetString(Key + ".Assets", "");
            var owned = JsonConvert.DeserializeObject<List<string>>(SessionState.GetString(Key + ".Owned", "[]"));
            var allowed = paths.Where(p => (!string.IsNullOrEmpty(folder) && p.StartsWith(folder + "/", StringComparison.Ordinal)) ||
                owned.Any(root => p == root || p.StartsWith(root + "/", StringComparison.Ordinal)) ||
                Regex.IsMatch(p, "^Assets/InitTestScene[0-9]+[.]unity$")).ToArray();
            var deferred = paths.Except(allowed).ToArray();
            if (deferred.Length != 0)
            {
                File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Output() + "/deferred-saves.jsonl"), JsonConvert.SerializeObject(new
                {
                    utc = DateTime.UtcNow,
                    deferred
                }) + Environment.NewLine);
            }

            return allowed;
        }

        private static void End()
        {
            SessionState.EraseString(Key);
            SessionState.EraseString(Key + ".Assets");
            SessionState.EraseString(Key + ".Groups");
            SessionState.EraseString(Key + ".Owned");
            SessionState.EraseBool(Key + ".SaveFence");
        }
        internal static void Write(string path, object value)
        {
            string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(pending), JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
            {
                File.Replace(pending, path, null);
            }
            else
            {
                File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(pending), global::YLFramework.Testing.PackageTestPaths.Physical(path));
            }
        }
        internal static string Hash(string path)
        {
            using var stream = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    internal sealed class DataMainSaveFence : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths) => DataMainAcceptance.FilterSaves(paths);
    }
}
