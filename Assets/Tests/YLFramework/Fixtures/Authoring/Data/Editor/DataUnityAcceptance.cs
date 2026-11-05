using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.DataAssets;
using Object = UnityEngine.Object;
using YLFramework.Editor.DataAssets;

namespace YLFramework.Tests.DataBatch
{
    // 主工程B接收验收；不进入产品默认导出，输出由主工程独占入口绑定。
    [InitializeOnLoad]
    public static class DataUnityAcceptance
    {
        private const string Project = "G:/Unity/Projects/CGJ2024";
        private const string Plugin = "Packages/com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll";
        private const string ProfileKey = "YLFramework.BDataBatch.Profile";

        [Serializable]
        private sealed class ProfileState
        {
            public string company;
            public string product;
            public string testProduct;
        }

        static DataUnityAcceptance()
        {
            if (!Environment.GetCommandLineArgs().Contains("-dataAcceptanceProfile"))
            {
                return;
            }

            CheckProject();
            string saved = SessionState.GetString(ProfileKey, string.Empty);
            var profile = string.IsNullOrEmpty(saved) ? new ProfileState
            {
                company = PlayerSettings.companyName,
                product = PlayerSettings.productName,
                testProduct = "YL-MainBData-" + Path.GetFileName(Output())
            } : JsonConvert.DeserializeObject<ProfileState>(saved);
            SessionState.SetString(ProfileKey, JsonConvert.SerializeObject(profile));
            PlayerSettings.companyName = "YLFrameworkTests";
            PlayerSettings.productName = profile.testProduct;
            Require(Application.persistentDataPath.Contains(profile.testProduct), "The test profile did not isolate persistentDataPath.");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Output(), "profile.json")), JsonConvert.SerializeObject(new
            {
                original = profile,
                persistentDataPath = Application.persistentDataPath
            }, Formatting.Indented));
            EditorApplication.quitting += RestoreProfile;
        }

        private static void RestoreProfile()
        {
            string saved = SessionState.GetString(ProfileKey, string.Empty);
            if (string.IsNullOrEmpty(saved))
            {
                return;
            }

            var profile = JsonConvert.DeserializeObject<ProfileState>(saved);
            Require(PlayerSettings.companyName == "YLFrameworkTests" && PlayerSettings.productName == profile.testProduct, "Test profile was changed by another owner; refusing to overwrite it.");
            PlayerSettings.companyName = profile.company;
            PlayerSettings.productName = profile.product;
            SessionState.EraseString(ProfileKey);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Output(), "profile-restored.json")), JsonConvert.SerializeObject(new
            {
                company = PlayerSettings.companyName,
                product = PlayerSettings.productName
            }, Formatting.Indented));
        }

        public static void BuildMono() => BuildPlayer(ScriptingImplementation.Mono2x);
        public static void BuildIL2CPP() => BuildPlayer(ScriptingImplementation.IL2CPP);

        private static void BuildPlayer(ScriptingImplementation backend)
        {
            CheckProject();
            Require(PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';').Contains("YL_DATA_ACCEPTANCE"),
                "The explicit Player host must enable YL_DATA_ACCEPTANCE; ordinary production builds must not include the probe.");
            var target = NamedBuildTarget.Standalone;
            var previousBackend = PlayerSettings.GetScriptingBackend(target);
            var previousStripping = PlayerSettings.GetManagedStrippingLevel(target);
            string company = PlayerSettings.companyName, product = PlayerSettings.productName;
            var scenes = EditorSceneManager.GetSceneManagerSetup();
            int exit = 1;
            var receipt = new Dictionary<string, object> { ["backend"] = backend.ToString(), ["output"] = Output() };
            try
            {
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    Require(!EditorSceneManager.GetSceneAt(i).isDirty, "Refuse to replace an unsaved scene.");
                }

                Require(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), "Windows64 support unavailable.");
                PlayerSettings.companyName = "YLFrameworkTests";
                PlayerSettings.productName = "YL-MainBData-Player-" + backend;
                PlayerSettings.SetScriptingBackend(target, backend);
                PlayerSettings.SetManagedStrippingLevel(target, backend == ScriptingImplementation.IL2CPP ? ManagedStrippingLevel.Low : ManagedStrippingLevel.Disabled);
                string parent = "Assets/Tests/YLFramework/Fixtures/Extensions/Data";
                EnsureFolder(parent);
                string folder = parent + "/Player-" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
                if (!Application.isBatchMode)
                {
                    DataMainAcceptance.OwnBuildFolder(folder);
                }

                string prefix = "BData_" + Guid.NewGuid().ToString("N");
                EnsureFolder(folder + "/Resources/" + prefix);
                var languages = ScriptableObject.CreateInstance<YLFramework.Localization.MultiLanguageConfigs>();
                AssetDatabase.CreateAsset(languages, folder + "/Resources/" + prefix + "/Localization.asset");
                foreach (var language in new[] { SystemLanguage.Chinese, SystemLanguage.English })
                {
                    var asset = ScriptableObject.CreateInstance<YLFramework.Localization.LanguageConfig>();
                    asset.language = language;
                    asset.languagesInfo.Add(new YLFramework.Localization.LanguageInfo { id = 1, str = language == SystemLanguage.English ? "Native English" : "Chinese fallback must not win" });
                    AssetDatabase.AddObjectToAsset(asset, languages);
                    languages.Languages.Add(asset);
                    EditorUtility.SetDirty(asset);
                }
                EditorUtility.SetDirty(languages);
                AssetDatabase.SaveAssetIfDirty(languages);
                var root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
                AssetDatabase.CreateAsset(root, folder + "/Resources/" + prefix + "/Data.asset");
                var child = ScriptableObject.CreateInstance<DataPlayerFixture>();
                ScriptableObjectConfigsController.SetDataType(root, MonoScript.FromScriptableObject(child));
                child.id = 17;
                child.amount = 41;
                child.parent = root;
                AssetDatabase.AddObjectToAsset(child, root);
                root.configs.Add(child);
                EditorUtility.SetDirty(child);
                EditorUtility.SetDirty(root);
                AssetDatabase.SaveAssetIfDirty(root);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var host = new GameObject("B Data Player Probe").AddComponent<DataPlayerProbe>();
                var text = new GameObject("B localized business component");
                text.SetActive(false);
                var sink = text.AddComponent<DataPlayerTextSink>();
                var localized = text.AddComponent<YLFramework.Localization.LocalizeString>();
                localized.Id = 1;
                var stringFields = new SerializedObject(localized);
                stringFields.FindProperty("componentType").intValue = (int)YLFramework.Localization.ComponentType.Other;
                stringFields.FindProperty("componentFullName").stringValue = typeof(DataPlayerTextSink).FullName;
                stringFields.FindProperty("fieldName").stringValue = "text";
                stringFields.ApplyModifiedPropertiesWithoutUndo();
                var fields = new SerializedObject(host);
                fields.FindProperty("resourcePrefix").stringValue = prefix;
                fields.FindProperty("backend").stringValue = backend.ToString();
                fields.FindProperty("sink").objectReferenceValue = sink;
                fields.ApplyModifiedPropertiesWithoutUndo();
                string scenePath = folder + "/PlayerProbe.unity";
                Require(EditorSceneManager.SaveScene(scene, scenePath), "Could not save the owned probe scene.");
                string output = Path.Combine(Output(), "Player/BData.exe");
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(output)));
                receipt["scene"] = scenePath;
                receipt["output"] = output;
                receipt["stripping"] = PlayerSettings.GetManagedStrippingLevel(target).ToString();
                receipt["development"] = false;
                receipt["options"] = BuildOptions.None.ToString();
                receipt["compiler"] = PlayerSettings.GetIl2CppCompilerConfiguration(target).ToString();
                receipt["projectPath"] = Project;
                receipt["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
                var sourceRoots = new[] { "Packages/com.kano.ylframework", "Packages/com.kano.ylframework.data-assets", "Packages/com.kano.ylframework.localization" };
                DataMainAcceptance.Write(Path.Combine(Output(), "source-binding.json"), sourceRoots.SelectMany(root => Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*.cs", SearchOption.AllDirectories)).Where(IsBPath).Select(p => new { path = p, sha256 = DataMainAcceptance.Hash(p) }));
                var built = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, target = BuildTarget.StandaloneWindows64, locationPathName = output, options = BuildOptions.None });
                receipt["result"] = built.summary.result.ToString();
                receipt["errors"] = built.summary.totalErrors;
                receipt["warnings"] = built.summary.totalWarnings;
                receipt["development"] = (built.summary.options & BuildOptions.Development) != 0;
                receipt["options"] = built.summary.options.ToString();
                Require(built.summary.result == BuildResult.Succeeded, "Player build failed: " + built.summary.result);
                Require((built.summary.options & BuildOptions.Development) == 0, "Release build required.");
                receipt["steps"] = built.steps.Select(s => new { s.name, seconds = s.duration.TotalSeconds, messages = s.messages.Select(m => new { m.content, type = m.type.ToString() }) }).ToArray();
                string stripped = "Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped";
                if (backend == ScriptingImplementation.IL2CPP)
                {
                    Require(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Path.GetDirectoryName(output), "GameAssembly.dll"))), "Missing native IL2CPP output.");
                    Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Output(), "managed-stripped")));
                    foreach (string assembly in new[] { "YLFramework.Runtime", "YLFramework.DataAssets", "YLFramework.Localization", typeof(DataPlayerProbe).Assembly.GetName().Name })
                    {
                        File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(stripped, assembly + ".dll")), global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Output(), "managed-stripped", assembly + ".dll")));
                    }
                }
                var excluded = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(output)), "MiniExcel*", SearchOption.AllDirectories).Concat(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(output)), "UnityEditor*.dll", SearchOption.AllDirectories)).ToArray();
                receipt["forbiddenPlayerFiles"] = excluded;
                Require(excluded.Length == 0, "Editor/backend artifact entered build output.");
                receipt["executableSha256"] = DataMainAcceptance.Hash(output);
                exit = 0;
            }
            catch (Exception error)
            {
                receipt["error"] = error.ToString();
                Debug.LogException(error);
            }
            finally
            {
                try
                {
                    PlayerSettings.SetScriptingBackend(target, previousBackend);
                    PlayerSettings.SetManagedStrippingLevel(target, previousStripping);
                    PlayerSettings.companyName = company;
                    PlayerSettings.productName = product;
                    if (scenes.Length > 0 && scenes.Any(s => s.isLoaded) && scenes.Any(s => s.isActive) && scenes.All(s => !string.IsNullOrEmpty(s.path)))
                    {
                        EditorSceneManager.RestoreSceneManagerSetup(scenes);
                    }
                    else
                    {
                        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    }

                    bool restored = PlayerSettings.GetScriptingBackend(target) == previousBackend && PlayerSettings.GetManagedStrippingLevel(target) == previousStripping && PlayerSettings.companyName == company && PlayerSettings.productName == product;
                    receipt["settingsRestored"] = restored;
                    if (!restored)
                    {
                        exit = 3;
                    }
                }
                catch (Exception error)
                {
                    receipt["restoreError"] = error.ToString();
                    receipt["settingsRestored"] = false;
                    exit = 3;
                }
                receipt["passed"] = exit == 0;
                receipt["exitCode"] = exit;
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Output(), "build.json")), JsonConvert.SerializeObject(receipt, Formatting.Indented));
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(exit);
                }
            }
            if (exit != 0)
            {
                throw new InvalidOperationException("Main data Player build failed; inspect build.json.");
            }
        }

        public static void Audit()
        {
            var report = new Dictionary<string, object>();
            int exit = 0;
            try
            {
                CheckProject();
                report["projectPath"] = Path.GetDirectoryName(Application.dataPath);
                report["unityVersion"] = Application.unityVersion;
                report["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
                var importer = AssetImporter.GetAtPath(Plugin) as PluginImporter;
                Require(importer != null, "MiniExcel PluginImporter missing.");
                report["pluginEditor"] = importer.GetCompatibleWithEditor();
                report["pluginAny"] = importer.GetCompatibleWithAnyPlatform();
                report["pluginWindows64"] = importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64);
                Require(importer.GetCompatibleWithEditor() && !importer.GetCompatibleWithAnyPlatform() && !importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64), "MiniExcel must be Editor-only.");
                using (var stream = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(Plugin)))
                using (var sha = SHA256.Create())
                {
                    report["pluginSha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }

                Require((string)report["pluginSha256"] == "0a49b66224bef9fdcec12800e4660267a993b1ece87727d9fe15c7dabf58ba1e", "MiniExcel LFS binary differs.");
                var runtime = typeof(ScriptableObjectConfigs).Assembly;
                var editor = typeof(ScriptableObjectConfigsController).Assembly;
                report["runtimeEditorProfileReferences"] = runtime.GetReferencedAssemblies().Select(a => a.Name).ToArray();
                report["editorReferences"] = editor.GetReferencedAssemblies().Select(a => a.Name).ToArray();
                Require(!runtime.GetReferencedAssemblies().Any(a => a.Name.Contains("MiniExcel")) && !editor.GetReferencedAssemblies().Any(a => a.Name.Contains("MiniExcel")), "Ordinary YL assemblies must not require MiniExcel.");
                var player = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                report["playerYlAssemblies"] = player.Where(a => a.name.StartsWith("YLFramework", StringComparison.Ordinal)).Select(a => a.name).ToArray();
                Require(!player.Any(a => a.name == "YLFramework.Excel.Editor" || a.name == "YLFramework.Editor" || a.name == "YLFramework.DataAssets.Editor" || a.name == "YLFramework.Localization.Editor"), "Editor assembly entered the Player graph.");
                Require(player.Any(a => a.name == "YLFramework.DataAssets") && player.Any(a => a.name == "YLFramework.Localization") && player.Any(a => a.name == "YLFramework.Save"), "The Data combination lost a required runtime assembly.");
                var playerRuntime = player.Single(a => a.name == "YLFramework.Runtime");
                report["playerRuntimeReferences"] = playerRuntime.compiledAssemblyReferences.Select(Path.GetFileName).ToArray();
                Require(!playerRuntime.compiledAssemblyReferences.Any(p => Path.GetFileName(p).StartsWith("UnityEditor", StringComparison.Ordinal) || Path.GetFileName(p).Contains("MiniExcel")), "Player Runtime has an Editor/backend reference.");
                var paths = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/com.kano.ylframework", StringComparison.Ordinal)).Select(path => new { path, guid = AssetDatabase.AssetPathToGUID(path) }).Where(p => !string.IsNullOrEmpty(p.guid)).ToArray();
                var collisions = paths.GroupBy(p => p.guid).Where(g => g.Count() > 1 && g.Any(p => IsBPath(p.path))).Select(g => g.Select(p => p.path).ToArray()).ToArray();
                var own = paths.Where(p => IsBPath(p.path)).ToArray();
                foreach (var asset in own)
                {
                    var match = Regex.Match(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asset.path + ".meta")), @"(?m)^guid: ([0-9a-f]{32})\s*$");
                    Require(match.Success && match.Groups[1].Value == asset.guid, "Disk/index GUID mismatch: " + asset.path);
                }
                report["bMetaCount"] = own.Length;
                report["bGuidCollisions"] = collisions;
                Require(collisions.Length == 0, "B GUID collides with another asset.");
                report["saveManagerGuid"] = AssetDatabase.AssetPathToGUID("Packages/com.kano.ylframework.save/Runtime/Systems/Save/SaveManager.cs");
                Require((string)report["saveManagerGuid"] == "ff5957d84032533449f21601f372570a", "Existing SaveManager identity changed.");
                report["subAssetProbe"] = ProbeSubAsset();
                using (var watcher = new FileSystemWatcher(Output()))
                {
                    watcher.EnableRaisingEvents = true;
                    report["watcherBackend"] = typeof(FileSystemWatcher).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic).Where(f => f.Name.IndexOf("watcher", StringComparison.OrdinalIgnoreCase) >= 0).Select(f => f.Name + ":" + f.GetValue(f.IsStatic ? null : watcher)?.GetType().FullName).ToArray();
                }
                report["passed"] = true;
            }
            catch (Exception error)
            {
                report["passed"] = false;
                report["error"] = error.ToString();
                exit = 1;
                Debug.LogException(error);
            }
            finally
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Output(), "audit.json")), JsonConvert.SerializeObject(report, Formatting.Indented));
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(exit);
                }
            }
            if (exit != 0)
            {
                throw new InvalidOperationException("Main data audit failed; inspect audit.json.");
            }
        }

        private static object ProbeSubAsset()
        {
            const string Parent = "Assets/Tests/YLFramework/Fixtures/Extensions/Data";
            EnsureFolder(Parent);
            string folder = Parent + "/Probe-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(Parent, Path.GetFileName(folder));
            var root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            try
            {
                string path = folder + "/Root.asset";
                AssetDatabase.CreateAsset(root, path);
                var child = ScriptableObject.CreateInstance<ScriptableObjectData>();
                child.parent = root;
                child.id = 5;
                AssetDatabase.AddObjectToAsset(child, root);
                root.configs.Add(child);
                var states = new List<object> { SubState("immediate", root, child) };
                EditorUtility.SetDirty(root);
                AssetDatabase.SaveAssetIfDirty(root);
                states.Add(SubState("saved", root, child));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                states.Add(SubState("imported", root, child));
                return states;
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static object SubState(string stage, ScriptableObjectConfigs root, ScriptableObjectData child) => new
        {
            stage,
            rootPath = AssetDatabase.GetAssetPath(root),
            childPath = AssetDatabase.GetAssetPath(child),
            rootMain = AssetDatabase.IsMainAsset(root),
            childSub = AssetDatabase.IsSubAsset(child),
            parentMatches = child.parent == root,
            member = root.configs.Contains(child),
            owned = ScriptableObjectConfigsController.IsOwned(root, child)
        };

        private static bool IsBPath(string path)
        {
            path = path.Replace('\\', '/');
            return path.Contains("/Systems/DataAssets/") || path.Contains("/Systems/Localization/") || path.Contains("/Systems/Save/") || path.Contains("/Integrations/Excel/") || path.Contains("/Extensions/Data/") ||
                path.Contains("/Tests/EditMode/Editor/DataAssets/") || path.Contains("/Tests/EditMode/Editor/Localization/") ||
                path.Contains("/Tests/EditMode/Editor/Excel/") || path.Contains("/Tests/EditMode/Editor/Data/") ||
                path.StartsWith("Assets/Tests/YLFramework/Fixtures/Save/", StringComparison.Ordinal);
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
        private static void CheckProject()
        {
            Require(DataMainAcceptance.Canonical(Path.GetDirectoryName(Application.dataPath)) == Project, "Wrong main project.");
            DataMainAcceptance.Identity();
        }
        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }
        private static string Output()
        {
            var args = Environment.GetCommandLineArgs();
            if (!Application.isBatchMode)
            {
                return DataMainAcceptance.Output();
            }

            int index = Array.IndexOf(args, "-dataAcceptanceOutput");
            if (index < 0 || index + 1 >= args.Length || !DataMainAcceptance.Canonical(args[index + 1]).StartsWith("G:/Unity/MB26/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Missing dedicated main B output.");
            }

            return args[index + 1];
        }
    }
}
