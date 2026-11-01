using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Tests.EquipmentAcceptance
{
    [InitializeOnLoad]
    public static class EquipmentBuild
    {
        private const string Key = "YLFramework.Equipment.Acceptance.Build";
        [Serializable]
        private sealed class Job
        {
            public string gate;
            public string output;
            public string run;
            public string archive;
            public bool il2cpp;
            public bool exit;
        }
        private static int _stableFrames;
        static EquipmentBuild()
        {
            if (SessionState.GetString(Key, "").Length != 0)
            {
                EditorApplication.update += Resume;
            }
        }
        private static void Require(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }
        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }
        private static void Write(string path, object value)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
            File.Move(temp, path);
        }
        public static void Queue(string gate, string output, bool il2cpp) => Queue(gate, output, il2cpp, false);
        private static void Queue(string gate, string output, bool il2cpp, bool exit, string archive = null)
        {
            Require(SessionState.GetString(Key, "").Length == 0, "Another Equipment build is queued.");
            var temp = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp/kilo")) + Path.DirectorySeparatorChar;
            output = Path.GetFullPath(output);
            Require(output.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(output), "Expected a new, approved short temporary build output.");
            EquipmentSettings.Capture(gate);
            if (archive != null)
            {
                Require(Path.GetFullPath(archive).StartsWith(temp, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(archive), "Expected a new owned gate archive.");
            }

            var job = new Job { gate = Path.GetFullPath(gate), output = output, run = "EQ-" + Guid.NewGuid().ToString("N"), il2cpp = il2cpp, exit = exit, archive = archive };
            Write(job.gate + "/queued.json", job);
            SessionState.SetString(Key, JsonConvert.SerializeObject(job));
            EquipmentSettings.EnableAcceptance();
            EquipmentSettings.SetBackend(il2cpp);
            EditorApplication.update -= Resume;
            EditorApplication.update += Resume;
        }
        public static void QueueFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            string Get(string key)
            {
                var i = Array.IndexOf(args, key);
                if (i < 0 || i + 1 == args.Length)
                {
                    throw new ArgumentException("Missing " + key);
                }
                return args[i + 1];
            }
            Require(Application.isBatchMode, "CLI queue is for an owned receiver Editor only.");
            var approved = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp/kilo")) + Path.DirectorySeparatorChar;
            Require(Path.GetFullPath(Application.dataPath).StartsWith(approved, StringComparison.OrdinalIgnoreCase), "Never exit the user's main Editor.");
            Require(File.Exists("equipment-receiver.json"), "Missing independent receiver identity.");
            var identity = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("equipment-receiver.json"));
            Require((string)identity["purpose"] == "Equipment-independent-receiver" && Path.GetFullPath((string)identity["project"]) == Path.GetFullPath("."), "Receiver identity mismatch.");
            var setup = EditorSceneManager.GetSceneManagerSetup();
            if (setup.Length == 0 || setup.Any(scene => string.IsNullOrEmpty(scene.path)))
            {
                for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    Require(!UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty, "Do not save an unexpected dirty receiver scene.");
                }

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Require(EditorSceneManager.SaveScene(scene, "Assets/EquipmentHarness/Empty.unity"), "Could not save the receiver-owned empty scene.");
            }
            Queue(Get("--eq-gate"), Get("--eq-output"), Get("--eq-backend") == "IL2CPP", true, Get("--eq-archive"));
        }
        private static void Resume()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || EditorApplication.isPlaying)
            {
                _stableFrames = 0;
                return;
            }
#if YL_EQUIPMENT_ACCEPTANCE
            if (++_stableFrames < 8)
            {
                return;
            }
            EditorApplication.update -= Resume;
            var job = JsonConvert.DeserializeObject<Job>(SessionState.GetString(Key, ""));
            if (job != null) BuildConfigured(job);
#endif
        }
        private static void BuildConfigured(Job job)
        {
            var succeeded = false;
            Exception failure = null;
            var folder = "Assets/YLFramework/Tests/Fixtures/Extensions/Equipment/Run-" + job.run;
            try
            {
#if YL_EQUIPMENT_ACCEPTANCE
                Require(!EditorUtility.scriptCompilationFailed, "Compilation failed before Player build.");
                Directory.CreateDirectory(job.output);
                EquipmentSettings.OwnPath(folder);
                EnsureFolder(folder); AssetDatabase.CreateFolder(folder, "Resources");
                var prefix = job.run.Replace("-", "");
                var prefab = new GameObject("Equipment Prefab");
                try
                {
                    prefab.AddComponent<YLFramework.Equipment.Acceptance.PrefabEquipmentProbe>();
                    PrefabUtility.SaveAsPrefabAsset(prefab, folder + "/Resources/" + prefix + "Prefab.prefab");
                }
                finally { UnityEngine.Object.DestroyImmediate(prefab); }
                var root = ScriptableObject.CreateInstance<EquipmentsConfig>(); root.name = prefix;
                AssetDatabase.CreateAsset(root, folder + "/Resources/" + prefix + "Root.asset");
                foreach (var id in new[] { 1001, 1002 })
                {
                    var data = ScriptableObject.CreateInstance<YLFramework.Equipment.Acceptance.ProbeEquipmentData>();
                    data.id = id; data.name = id.ToString(); data.parent = root; data.bonus = 42;
                    data.equipmentType = id == 1001 ? EquipmentType.Script : EquipmentType.Asset;
                    data.equipmentClassFullName = "YLFramework.Equipment.Acceptance.StringOnlyEquipment";
                    data.projectName = "equipment-acceptance"; data.assetName = "prefab";
                    AssetDatabase.AddObjectToAsset(data, root); root.Equipments.Add(data);
                }
                EditorUtility.SetDirty(root); AssetDatabase.SaveAssetIfDirty(root);
                File.WriteAllText(folder + "/link.xml", "<linker><assembly fullname=\"YLFramework.Equipment.Acceptance.Player\"><type fullname=\"YLFramework.Equipment.Acceptance.StringOnlyEquipment\" preserve=\"all\"/></assembly></linker>");
                AssetDatabase.ImportAsset(folder + "/link.xml");
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var probe = new GameObject("Equipment Player Acceptance").AddComponent<YLFramework.Equipment.Acceptance.EquipmentPlayerProbe>();
                probe.prefix = prefix;
                var scenePath = folder + "/Equipment.unity";
                Require(EditorSceneManager.SaveScene(scene, scenePath), "Could not save the owned acceptance scene.");
                var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                if (job.exit)
                {
                    Require(!UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(package =>
                        !package.name.StartsWith("com.unity.modules.", StringComparison.Ordinal) &&
                        !new[] { "com.unity.ugui", "com.unity.nuget.newtonsoft-json" }.Contains(package.name)), "Independent receiver contains an undeclared package.");
                    Require(!assemblies.Any(assembly => !System.Text.RegularExpressions.Regex.IsMatch(assembly.name,
                        @"^(?:YLFramework\.Runtime|YLFramework\.ResourceStartup\.Sample|YLFramework\.Equipment\.Acceptance\.Player|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity(?:\.[A-Za-z0-9_]+)+)$") ||
                        assembly.name.Contains("Tests") || assembly.name.Contains("TestRunner")), "Independent receiver contains forbidden assemblies.");
                }
                // 宿主验收程序集不是产品依赖；每个正式 Player 消费者仍须保持独立的引用闭包。
                foreach (var assembly in assemblies.Where(a => a.name == "YLFramework.Runtime" || a.name == "YLFramework.ResourceStartup.Sample" || a.name == "YLFramework.Equipment.Acceptance.Player"))
                    Require(!assembly.assemblyReferences.Select(reference => reference.name).Concat(assembly.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).Any(name =>
                        !(assembly.name != "YLFramework.Runtime" && name == "YLFramework.Runtime") &&
                        (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$") ||
                         name.Contains("TestRunner") || name.Contains("Editor"))), "Forbidden YL Player assembly reference: " + assembly.name);
                Write(job.gate + "/inputs.json", new { job.run, job.il2cpp, scenePath, link = File.ReadAllText(folder + "/link.xml"),
                    assemblies = assemblies.Where(a => a.name.StartsWith("YLFramework", StringComparison.Ordinal)).Select(a => new { a.name, a.sourceFiles, references = a.assemblyReferences.Select(r => r.name).ToArray() }),
                    sources = Directory.GetFiles("Assets/YLFramework/Runtime", "*.cs", SearchOption.AllDirectories).ToDictionary(p => p, Hash) });
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, target = BuildTarget.StandaloneWindows64,
                    locationPathName = job.output + "/EquipmentProbe.exe", options = BuildOptions.None });
                var summary = report.summary;
                Write(job.gate + "/BuildReport.json", new { job.run, job.il2cpp, result = summary.result.ToString(), options = summary.options.ToString(), summary.totalErrors,
                    summary.totalWarnings, summary.totalSize, duration = summary.totalTime.TotalSeconds, summary.outputPath,
                    backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone).ToString(), stripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone).ToString(),
                    compiler = PlayerSettings.GetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone).ToString(),
                    files = report.GetFiles().Where(f => File.Exists(f.path)).Select(f => new { f.path, f.role, sha256 = Hash(f.path) }).ToArray() });
                Require(summary.result == BuildResult.Succeeded && summary.totalErrors == 0 && summary.options == BuildOptions.None, "Actual Player build did not satisfy the gate.");
                var notices = job.output + "/ThirdPartyNotices"; Directory.CreateDirectory(notices);
                File.Copy("Assets/YLFramework/LICENSE.md", notices + "/LICENSE.md");
                succeeded = true;
#else
                throw new InvalidOperationException("Acceptance consumer is not compiled.");
#endif
            }
            catch (Exception error)
            {
                failure = error;
                Debug.LogException(error);
            }
            finally
            {
                try
                {
                    EquipmentSettings.Restore();
                    if (AssetDatabase.IsValidFolder(folder))
                    {
                        AssetDatabase.DeleteAsset(folder);
                    }
                }
                catch (Exception error)
                {
                    succeeded = false;
                    failure = failure == null ? error : new AggregateException(failure, error);
                    Debug.LogException(error);
                }
                SessionState.EraseString(Key);
                Write(job.gate + "/gate-complete.json", new
                {
                    succeeded,
                    job.run,
                    job.output,
                    job.il2cpp,
                    error = failure?.ToString(),
                    utc = DateTime.UtcNow
                });
                if (job.archive != null)
                {
                    Directory.CreateDirectory(job.archive);
                    foreach (var file in Directory.GetFiles(job.gate, "*", SearchOption.AllDirectories))
                    {
                        var target = Path.Combine(job.archive, file.Substring(job.gate.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(file, target);
                    }
                }
                if (job.exit)
                {
                    EditorApplication.Exit(succeeded ? 0 : 1);
                }
            }
        }
        private static void EnsureFolder(string path)
        {
            var current = "Assets";
            foreach (var part in path.Substring(7).Split('/'))
            {
                var next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, part);
                }
                current = next;
            }
        }
    }
}
