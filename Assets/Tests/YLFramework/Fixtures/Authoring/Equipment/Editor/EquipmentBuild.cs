using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using YLFramework.Equipment;
using YLFramework.Editor.Resource;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Tests.EquipmentAcceptance
{
    [InitializeOnLoad]
    public static class EquipmentBuild
    {
        private const string Key = "YLFramework.Equipment.Acceptance.Build";
        [Serializable]
        private sealed class Job
        {
            public int schemaVersion;
            public string gate;
            public string output;
            public string run;
            public string archive;
            public string packageCatalog;
            public string packageCatalogSha256;
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
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)))).Replace("-", "");
        }
        private static void Write(string path, object value)
        {
            var temp = path + ".tmp";
            WriteBytes(temp, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented)));
            File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(temp), global::YLFramework.Testing.PackageTestPaths.Physical(path));
        }
        private static void WriteBytes(string path, byte[] bytes)
        {
            using var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        public static void Queue(string gate, string output, bool il2cpp)
        {
            string catalog = Path.GetFullPath("Tools/Packaging/PackageCatalog.json");
            Queue(gate, output, il2cpp, false, null, catalog, Hash(catalog));
        }
        private static void Queue(string gate, string output, bool il2cpp, bool exit, string archive, string catalog, string catalogSha256)
        {
            Require(SessionState.GetString(Key, "").Length == 0, "Another Equipment build is queued.");
            var temp = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp/kilo"))) + Path.DirectorySeparatorChar;
            output = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(output));
            Require(output.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), "Expected a new, approved short temporary build output.");
            catalog = Path.GetFullPath(catalog);
            RejectLinks(catalog);
            Require(Regex.IsMatch(catalogSha256 ?? "", "^[0-9a-fA-F]{64}$") && Hash(catalog) == catalogSha256.ToUpperInvariant(), "The exact current PackageCatalog path and SHA are required.");
            EquipmentSettings.Capture(gate);
            if (archive != null)
            {
                Require(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(archive)).StartsWith(temp, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(archive)), "Expected a new owned gate archive.");
            }

            var job = new Job { schemaVersion = 2, gate = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(gate)), output = output, run = "EQ-" + Guid.NewGuid().ToString("N"), il2cpp = il2cpp, exit = exit, archive = archive, packageCatalog = catalog, packageCatalogSha256 = catalogSha256.ToLowerInvariant() };
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
            var approved = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp/kilo"))) + Path.DirectorySeparatorChar;
            Require(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath)).StartsWith(approved, StringComparison.OrdinalIgnoreCase), "Never exit the user's main Editor.");
            Require(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("equipment-receiver.json")), "Missing independent receiver identity.");
            var identity = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("equipment-receiver.json")));
            Require((string)identity["purpose"] == "Equipment-independent-receiver" && Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical((string)identity["project"])) == Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(".")), "Receiver identity mismatch.");
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
            string backend = Get("--eq-backend");
            Require(backend == "Mono" || backend == "IL2CPP", "An explicit supported backend is required.");
            Queue(Get("--eq-gate"), Get("--eq-output"), backend == "IL2CPP", true, Get("--eq-archive"), Get("--eq-package-catalog"), Get("--eq-package-catalog-sha256"));
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
            var folder = "Assets/Tests/YLFramework/Fixtures/Extensions/Equipment/Run-" + job.run;
            string defaultGuid = null, beforeBuildSettings = null, boundBuildSettings = null;
            bool deleteOwned = true;
            bool folderOwned = false;
            try
            {
#if YL_EQUIPMENT_ACCEPTANCE
                Require(job.schemaVersion == 2 && Hash(job.packageCatalog).ToLowerInvariant() == job.packageCatalogSha256, "Queued catalog identity changed; old jobs are not implicitly upgraded.");
                Require(!EditorUtility.scriptCompilationFailed, "Compilation failed before Player build.");
                Require(EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64 && BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), "An already active and installed Windows x64 target is required.");
                Require(!Directory.Exists(folder) && !File.Exists(folder + ".meta") && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(folder)), "Unknown fixture output will not be replaced.");
                Require(AddressableAssetSettingsDefaultObject.GetSettings(false) == null && !EditorBuildSettings.TryGetConfigObject<AddressableAssetSettingsDefaultObject>(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName, out _), "Existing official Settings are not adopted or replaced.");
                var packageFacts = CapturePackageFacts(job);
                string aa = Path.GetFullPath(Addressables.BuildPath);
                RejectLinks(aa);
                Require(!Directory.Exists(aa) || !Directory.EnumerateFileSystemEntries(aa).Any(), "Unknown official AA build bytes will not be replaced.");
                Require(!Directory.Exists(job.output) && !File.Exists(job.output), "The queued Player output was occupied before build.");
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(job.output));
                EquipmentSettings.OwnPath(folder);
                EnsureFolder(folder); AssetDatabase.CreateFolder(folder, "Content"); AssetDatabase.CreateFolder(folder, "AA");
                folderOwned = true;
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var scenePath = folder + "/Equipment.unity";
                Require(EditorSceneManager.SaveScene(scene, scenePath), "Could not save the empty owned build scene.");
                var prefab = new GameObject("Equipment Prefab");
                string prefabPath = folder + "/Content/Equipment.prefab";
                try
                {
                    prefab.AddComponent<YLFramework.Equipment.Acceptance.PrefabEquipmentProbe>();
                    Require(PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath), "Could not save the real equipment Prefab.");
                }
                finally { UnityEngine.Object.DestroyImmediate(prefab); }
                const string projectId = "equipment-acceptance";
                Require(job.run.StartsWith("EQ-", StringComparison.Ordinal) && Guid.TryParseExact(job.run.Substring(3), "N", out _), "Invalid queued Equipment run identity.");
                string contentToken = job.run.Substring(3);
                var root = ScriptableObject.CreateInstance<EquipmentsConfig>(); root.name = job.run;
                string configPath = folder + "/Content/Equipments.asset";
                AssetDatabase.CreateAsset(root, configPath);
                foreach (var id in new[] { 1001, 1002 })
                {
                    var data = ScriptableObject.CreateInstance<YLFramework.Equipment.Acceptance.ProbeEquipmentData>();
                    data.id = id; data.name = id.ToString(); data.parent = root; data.bonus = 42;
                    data.description = contentToken;
                    data.equipmentType = id == 1001 ? EquipmentType.Script : EquipmentType.Asset;
                    data.equipmentClassFullName = "YLFramework.Equipment.Acceptance.StringOnlyEquipment";
                    data.projectName = projectId; data.assetName = "prefab";
                    AssetDatabase.AddObjectToAsset(data, root); root.Equipments.Add(data);
                }
                EditorUtility.SetDirty(root); AssetDatabase.SaveAssetIfDirty(root);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/link.xml"), "<linker><assembly fullname=\"YLFramework.Equipment.Acceptance.Player\"><type fullname=\"YLFramework.Equipment.Acceptance.StringOnlyEquipment\" preserve=\"all\"/></assembly></linker>");
                AssetDatabase.ImportAsset(folder + "/link.xml");
                Require(EditorSceneManager.SaveScene(scene, scenePath), "The owned fixture scene is dirty before content build.");
                beforeBuildSettings = Hash("ProjectSettings/EditorBuildSettings.asset");
                WriteBytes(job.gate + "/EditorBuildSettings.before.asset", File.ReadAllBytes("ProjectSettings/EditorBuildSettings.asset"));
                var settings = AddressableAssetSettings.Create(folder + "/AA", "Settings", true, true);
                Require(settings, "Official Settings creation failed.");
                settings.DisableCatalogUpdateOnStartup = true;
                settings.BuildRemoteCatalog = false;
                settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                int packed = settings.DataBuilders.FindIndex(builder => builder is BuildScriptPackedMode);
                Require(packed >= 0, "The official packed Player content builder is missing.");
                settings.ActivePlayerDataBuilderIndex = packed;
                var group = settings.DefaultGroup;
                var bundle = group.GetSchema<BundledAssetGroupSchema>();
                Require(bundle != null, "The official bundled group schema is missing.");
                bundle.UseDefaultSchemaSettings = false;
                bundle.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                bundle.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
                bundle.IncludeInBuild = true;
                AddEntry(settings, group, configPath, projectId + "/config");
                AddEntry(settings, group, prefabPath, projectId + "/prefab");
                var defaults = ScriptableObject.CreateInstance<AddressableAssetSettingsDefaultObject>();
                var defaultPath = folder + "/AA/DefaultObject.asset";
                AssetDatabase.CreateAsset(defaults, defaultPath);
                using (var state = new SerializedObject(defaults))
                {
                    var guid = state.FindProperty("m_AddressableAssetSettingsGuid");
                    Require(guid != null, "The installed official default-object serialized contract is unavailable.");
                    guid.stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings));
                    Require(!string.IsNullOrEmpty(guid.stringValue), "The new official Settings have no actual GUID.");
                    state.ApplyModifiedPropertiesWithoutUndo();
                }
                defaultGuid = AssetDatabase.AssetPathToGUID(defaultPath);
                deleteOwned = false;
                EditorBuildSettings.AddConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName, defaults, true);
                AssetDatabase.SaveAssets();
                boundBuildSettings = Hash("ProjectSettings/EditorBuildSettings.asset");
                Require(AddressableAssetSettingsDefaultObject.GetSettings(false) == settings, "The official default did not bind to the owned Settings.");
                var content = ResourceEditorOperations.BuildContent(settings, Path.Combine(job.gate, "official-content-state.bin"));
                var contentFiles = Inventory(aa);
                Require(contentFiles.Any(row => Path.GetFileName((string)row["path"]) == "settings.json") && contentFiles.Any(row => ((string)row["path"]).Contains("catalog")) && contentFiles.Any(row => ((string)row["path"]).EndsWith(".bundle", StringComparison.Ordinal)), "Official AA runtime settings/catalog/bundles are missing.");
                string contentHash = FingerprintDirectory(aa);
                scene = SceneManager.GetSceneByPath(scenePath);
                Require(scene.IsValid() && scene.isLoaded && scene == SceneManager.GetActiveScene() && !scene.isDirty && scene.rootCount == 0, "The official build did not restore the saved empty Equipment scene.");
                var probe = new GameObject("Equipment Player Acceptance").AddComponent<YLFramework.Equipment.Acceptance.EquipmentPlayerProbe>();
                probe.projectId = projectId; probe.contentHash = contentHash; probe.contentToken = contentToken;
                Require(EditorSceneManager.SaveScene(scene, scenePath), "Could not save the owned acceptance scene.");
                var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
                Write(job.gate + "/inputs.json", new { schemaVersion = 2, job.run, job.il2cpp, scenePath, projectId, contentHash, contentToken, packageFacts,
                    officialContent = new { OutputPath = content.Result.OutputPath, content.ContentStatePath, stateSha256 = Hash(content.ContentStatePath), files = contentFiles }, link = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/link.xml")),
                    assemblies = assemblies.Where(a => a.name.StartsWith("YLFramework", StringComparison.Ordinal)).Select(a => new { a.name, a.sourceFiles, references = a.assemblyReferences.Select(r => r.name).ToArray() }),
                    sources = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework/Runtime"), "*.cs", SearchOption.AllDirectories).ToDictionary(p => p, Hash) });
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, target = BuildTarget.StandaloneWindows64,
                    locationPathName = job.output + "/EquipmentProbe.exe", options = BuildOptions.None });
                var summary = report.summary;
                var actualBackend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone).ToString();
                Require(actualBackend == (job.il2cpp ? nameof(ScriptingImplementation.IL2CPP) : nameof(ScriptingImplementation.Mono2x)), "The actual backend differs from the request.");
                Require(summary.result == BuildResult.Succeeded && summary.totalErrors == 0 && summary.options == BuildOptions.None, "Actual Player build did not satisfy the gate.");
                var managedRoot = job.il2cpp ? "Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped" : Path.Combine(job.output, "EquipmentProbe_Data/Managed");
                var managedFiles = Directory.GetFiles(managedRoot, "*.dll").OrderBy(path => path, StringComparer.Ordinal).ToArray();
                // Mono can replace the requested core library with Editor mscorlib during reflection-only loading.
                // Read the actual emitted PE with the Cecil reader already shipped with this Editor.
                var readerPath = Path.Combine(EditorApplication.applicationContentsPath, "Managed/Unity.Cecil.dll");
                RejectLinks(readerPath);
                using var hasher = SHA256.Create();
                var readerBytes = File.ReadAllBytes(readerPath);
                var readerSha256 = BitConverter.ToString(hasher.ComputeHash(readerBytes)).Replace("-", "").ToLowerInvariant();
                var reader = System.Reflection.Assembly.Load(readerBytes);
                Require(reader.GetName().Name == "Unity.Cecil", "The installed PE reader has an unexpected identity.");
                var assemblyDefinition = reader.GetType("Mono.Cecil.AssemblyDefinition", true);
                var readAssembly = assemblyDefinition.GetMethod("ReadAssembly", new[] { typeof(Stream) });
                var nameProperty = reader.GetType("Mono.Cecil.AssemblyNameReference", true).GetProperty("Name");
                var assemblyName = assemblyDefinition.GetProperty("Name");
                var mainModule = assemblyDefinition.GetProperty("MainModule");
                var moduleType = mainModule.PropertyType;
                var assemblyReferences = moduleType.GetProperty("AssemblyReferences");
                var moduleId = moduleType.GetProperty("Mvid");
                var managedEvidence = managedFiles.Select(path =>
                {
                    RejectLinks(path);
                    using var file = File.OpenRead(path);
                    var sha256 = BitConverter.ToString(hasher.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                    file.Position = 0;
                    using var assembly = (IDisposable)readAssembly.Invoke(null, new object[] { file });
                    var name = (string)nameProperty.GetValue(assemblyName.GetValue(assembly));
                    var module = mainModule.GetValue(assembly);
                    var references = ((System.Collections.IEnumerable)assemblyReferences.GetValue(module)).Cast<object>()
                        .Select(reference => (string)nameProperty.GetValue(reference)).ToArray();
                    Require(name == Path.GetFileNameWithoutExtension(path) && !IsForbiddenPlayerDependency(name) && !references.Any(IsForbiddenPlayerDependency),
                        "Forbidden or foreign emitted Player assembly/reference: " + name + " -> " + string.Join(", ", references.Where(IsForbiddenPlayerDependency)));
                    return new { path, name, sha256, mvid = (Guid)moduleId.GetValue(module), references };
                }).ToArray();
                var managedNames = managedEvidence.Select(row => row.name).ToHashSet(StringComparer.Ordinal);
                Require(new[] { "YLFramework.Runtime", "YLFramework.Equipment", "YLFramework.Equipment.Acceptance.Player" }.All(managedNames.Contains) &&
                    !Directory.EnumerateFiles(job.output, "*.dll", SearchOption.AllDirectories).Any(path => IsForbiddenPlayerDependency(Path.GetFileNameWithoutExtension(path))),
                    "The actual Player lacks required Equipment assemblies or includes forbidden Player files.");
                if (job.il2cpp)
                    Require(File.Exists(Path.Combine(job.output, "GameAssembly.dll")) && File.Exists(Path.Combine(job.output, "EquipmentProbe_Data/il2cpp_data/Metadata/global-metadata.dat")),
                        "Actual IL2CPP code and metadata are missing.");
                var installedAa = Path.Combine(job.output, "EquipmentProbe_Data/StreamingAssets/aa");
                Require(FingerprintDirectory(aa) == contentHash && FingerprintDirectory(installedAa) == contentHash, "Player AA bytes differ from the actual built official content.");
                Write(job.gate + "/BuildReport.json", new { schemaVersion = 2, job.run, job.il2cpp, projectId, contentHash, contentToken, packageFacts,
                    officialContent = new { OutputPath = content.Result.OutputPath, content.ContentStatePath, stateSha256 = Hash(content.ContentStatePath), files = contentFiles, installedAa }, result = summary.result.ToString(), options = summary.options.ToString(), summary.totalErrors,
                    summary.totalWarnings, summary.totalSize, duration = summary.totalTime.TotalSeconds, summary.outputPath,
                    backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone).ToString(), stripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone).ToString(),
                    compiler = PlayerSettings.GetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone).ToString(),
                    metadataReader = new { path = readerPath, sha256 = readerSha256 }, managedFiles = managedEvidence,
                    files = report.GetFiles().Where(f => File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(f.path))).Select(f => new { f.path, f.role, sha256 = Hash(f.path) }).ToArray() });
                var notices = job.output + "/ThirdPartyNotices"; Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(notices));
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework/LICENSE.md"),global::YLFramework.Testing.PackageTestPaths.Physical( notices + "/LICENSE.md"));
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
                    try
                    {
                        if (defaultGuid != null)
                        {
                            Require(boundBuildSettings != null && Hash("ProjectSettings/EditorBuildSettings.asset") == boundBuildSettings &&
                                EditorBuildSettings.TryGetConfigObject<AddressableAssetSettingsDefaultObject>(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName, out var current) &&
                                AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(current)) == defaultGuid, "The default binding changed; preserve unknown state and fixture bytes.");
                            AddressableAssetSettingsDefaultObject.Settings = null;
                            EditorBuildSettings.RemoveConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName);
                            Require(Hash("ProjectSettings/EditorBuildSettings.asset") == beforeBuildSettings, "The original Editor build settings did not round-trip exactly.");
                            deleteOwned = true;
                        }
                    }
                    finally { EquipmentSettings.Restore(); }
                    if (folderOwned && deleteOwned && AssetDatabase.IsValidFolder(folder))
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
                    schemaVersion = 2,
                    succeeded,
                    job.run,
                    job.output,
                    job.il2cpp,
                    error = failure?.ToString(),
                    utc = DateTime.UtcNow
                });
                if (job.archive != null)
                {
                    Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(job.archive));
                    foreach (var file in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(job.gate), "*", SearchOption.AllDirectories))
                    {
                        var target = Path.Combine(job.archive, file.Substring(job.gate.Length + 1));
                        Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(target)));
                        File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(file), global::YLFramework.Testing.PackageTestPaths.Physical(target));
                    }
                }
                if (job.exit)
                {
                    EditorApplication.Exit(succeeded ? 0 : 1);
                }
            }
        }
        private static void AddEntry(AddressableAssetSettings settings, AddressableAssetGroup group, string path, string key)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            Require(Regex.IsMatch(guid ?? "", "^[0-9a-f]{32}$") && settings.FindAssetEntry(guid) == null, "A new official entry requires a real unique asset GUID.");
            var entry = settings.CreateOrMoveEntry(guid, group, false, false);
            Require(entry != null, "Official Entry creation failed.");
            entry.SetAddress(key, false);
            EditorUtility.SetDirty(group);
            EditorUtility.SetDirty(settings);
        }
        private static JArray Inventory(string root)
        {
            RejectLinks(root);
            Require(Directory.Exists(root), "Actual official AA content is missing.");
            return new JArray(Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                RejectLinks(path);
                return new JObject { ["path"] = path.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'), ["bytes"] = new FileInfo(path).Length, ["sha256"] = Hash(path).ToLowerInvariant() };
            }));
        }
        private static string FingerprintDirectory(string root)
        {
            var rows = Inventory(root).OfType<JObject>().OrderBy(row => (string)row["path"], StringComparer.Ordinal)
                .Select(row => (string)row["path"] + "=" + (string)row["sha256"]);
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
            return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }
        private static void RejectLinks(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                Require((!File.Exists(current) && !Directory.Exists(current)) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Linked build inputs or outputs are refused.");
        }
        private static bool Inside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static bool IsForbiddenPlayerDependency(string name) =>
            Regex.IsMatch(name, @"TestRunner|nunit|\.Tests(?:\.|$)|(?:^|\.)Editor(?:\.|$)", RegexOptions.IgnoreCase);

        private static JObject CapturePackageFacts(Job job)
        {
            var installed = PackageInfo.GetAllRegisteredPackages().ToDictionary(package => package.name, StringComparer.Ordinal);
            var catalog = JObject.Parse(File.ReadAllText(job.packageCatalog));
            Require((int?)catalog["schemaVersion"] == 1, "Unsupported PackageCatalog schema.");
            var entries = ((JArray)catalog["packages"]).OfType<JObject>().ToDictionary(package => (string)package["id"], StringComparer.Ordinal);
            var entry = PackageInfo.FindForAssembly(typeof(EquipmentManager).Assembly);
            Require(entry != null && entry.name == "com.kano.ylframework.gameplay", "Equipment must resolve from its actual declared released extension, not a merged main-package assumption.");
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(entry.name);
            while (pending.Count != 0)
            {
                string id = pending.Pop();
                if (!reachable.Add(id))
                    continue;
                Require(installed.ContainsKey(id), "A required actual package dependency is absent: " + id);
                foreach (var dependency in installed[id].dependencies)
                    pending.Push(dependency.name);
            }
            Require(new[] { "com.kano.ylframework", "com.unity.addressables", "com.unity.profiling.core", "com.unity.ugui", "com.unity.nuget.newtonsoft-json" }.All(reachable.Contains), "The exact Equipment/main/official AA dependency closure is missing.");
            var declared = new Dictionary<string, (PackageInfo Package, string Path)>(StringComparer.Ordinal);
            var facts = new JArray();
            var locked = JObject.Parse(File.ReadAllText("Packages/packages-lock.json"))["dependencies"] as JObject;
            foreach (var package in installed.Values.OrderBy(value => value.name, StringComparer.Ordinal))
            {
                bool module = package.name.StartsWith("com.unity.modules.", StringComparison.Ordinal) && package.source == UnityEditor.PackageManager.PackageSource.BuiltIn;
                if (job.exit)
                    Require(reachable.Contains(package.name) || module, "Independent receiver resolved an unrelated package: " + package.name);
                if (!reachable.Contains(package.name))
                    continue;
                RejectLinks(package.resolvedPath);
                var manifestPath = Path.Combine(package.resolvedPath, "package.json");
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                var resolution = locked?[package.name] as JObject;
                Require((string)manifest["name"] == package.name && (string)manifest["version"] == package.version && resolution != null && package.errors != null && package.errors.Length == 0, "Actual PackageInfo/manifest/lock identity differs: " + package.name);
                bool yl = package.name.StartsWith("com.kano.ylframework", StringComparison.Ordinal);
                if (yl)
                {
                    Require(entries.ContainsKey(package.name), "A released extension is absent from PackageCatalog.");
                    if (job.exit)
                        Require(package.source == UnityEditor.PackageManager.PackageSource.LocalTarball && (string)resolution["source"] == "local-tarball", "An independent Equipment receiver requires actual released local tarballs, not copied PackageSources.");
                    foreach (var row in ((JArray)entries[package.name]["assemblies"]).OfType<JObject>().Where(row => (bool)row["player"] && !((string)row["path"]).StartsWith("Samples~", StringComparison.Ordinal)))
                        declared.Add((string)row["name"], (package, (string)row["path"]));
                }
                else if (package.source == UnityEditor.PackageManager.PackageSource.Registry)
                    Require(package.registry?.url == "https://packages.unity.com" && (string)resolution["source"] == "registry" && (string)resolution["url"] == package.registry.url && (string)resolution["version"] == package.version, "An SDK dependency is not the exact official registry resolution: " + package.name);
                else
                    Require(package.source == UnityEditor.PackageManager.PackageSource.BuiltIn && (string)resolution["source"] == "builtin" && (string)resolution["version"] == package.version, "Unsupported SDK source: " + package.name);
                facts.Add(new JObject
                {
                    ["name"] = package.name,
                    ["version"] = package.version,
                    ["source"] = package.source.ToString(),
                    ["packageId"] = package.packageId,
                    ["resolvedPath"] = package.resolvedPath,
                    ["manifestSha256"] = Hash(manifestPath),
                    ["direct"] = package.isDirectDependency,
                    ["dependencies"] = JObject.FromObject((package.dependencies ?? Array.Empty<UnityEditor.PackageManager.DependencyInfo>()).ToDictionary(value => value.name, value => value.version)),
                    ["lock"] = resolution.DeepClone()
                });
            }
            var sdk = new Dictionary<string, (string Package, string Path)>(StringComparer.Ordinal)
            {
                ["Unity.Addressables"] = ("com.unity.addressables", "Runtime/Unity.Addressables.asmdef"),
                ["Unity.ResourceManager"] = ("com.unity.addressables", "Runtime/ResourceManager/Unity.ResourceManager.asmdef"),
                ["Unity.Profiling.Core"] = ("com.unity.profiling.core", "Runtime/Unity.Profiling.Core.asmdef"),
                ["Unity.ScriptableBuildPipeline"] = ("com.unity.scriptablebuildpipeline", "Runtime/Unity.ScriptableBuildPipeline.asmdef"),
                ["UnityEngine.UI"] = ("com.unity.ugui", "Runtime/UGUI/UnityEngine.UI.asmdef"),
                ["Unity.InternalAPIEngineBridge.004"] = ("com.unity.ugui", "Runtime/InternalBridge/Unity.InternalAPIEngineBridge.004.asmdef"),
                ["Unity.TextMeshPro"] = ("com.unity.ugui", "Runtime/TMP/Unity.TextMeshPro.asmdef")
            };
            var player = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
            var outputs = player.ToDictionary(assembly => assembly.name, assembly => Path.GetFullPath(assembly.outputPath), StringComparer.Ordinal);
            var assemblies = new JArray();
            string editor = (string)typeof(EditorApplication).GetProperty("applicationContentsPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GetValue(null);
            const string nunitPackage = "com.unity.ext.nunit";
            const string nunitRelative = "net40/unity-custom/nunit.framework.dll";
            string nunitInput = null;
            string nunitSha = null;
            foreach (var assembly in player)
            {
                bool probe = assembly.name == "YLFramework.Equipment.Acceptance.Player";
                bool known = declared.ContainsKey(assembly.name) || sdk.ContainsKey(assembly.name) || probe;
                if (!job.exit && !known)
                    continue;
                Require(known && !Regex.IsMatch(assembly.name, "Editor|TestRunner|nunit|\\.Tests", RegexOptions.IgnoreCase), "Forbidden Player assembly: " + assembly.name);
                string definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                Require(!string.IsNullOrEmpty(definition), "The actual assembly has no declared asmdef: " + assembly.name);
                string physical = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(definition));
                RejectLinks(physical);
                var owner = PackageInfo.FindForAssetPath(definition);
                if (declared.TryGetValue(assembly.name, out var contract))
                    Require(owner != null && owner.name == contract.Package.name && physical == Path.GetFullPath(Path.Combine(owner.resolvedPath, contract.Path)), "Player assembly is not its exact PackageCatalog file: " + assembly.name);
                else if (sdk.TryGetValue(assembly.name, out var official))
                    Require(owner != null && reachable.Contains(official.Package) && owner.name == official.Package &&
                        physical.Equals(Path.GetFullPath(Path.Combine(owner.resolvedPath, official.Path)), StringComparison.OrdinalIgnoreCase),
                        "Official Player assembly provenance differs: " + assembly.name);
                else
                    Require(owner == null && definition.StartsWith("Assets/", StringComparison.Ordinal), "The explicit Player probe is not a receiver-owned consumer.");
                Require((string)JObject.Parse(File.ReadAllText(physical))["name"] == assembly.name && assembly.sourceFiles.Length != 0, "The actual compiled assembly definition identity differs.");
                string sourceRoot = Path.GetDirectoryName(physical);
                foreach (var source in assembly.sourceFiles)
                {
                    string file = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(source));
                    RejectLinks(file);
                    Require(Inside(file, sourceRoot), "Foreign compiled source: " + source);
                }
                foreach (var reference in assembly.assemblyReferences.Select(value => value.name))
                    Require((declared.ContainsKey(reference) || sdk.ContainsKey(reference)) && !Regex.IsMatch(reference, "Editor|TestRunner|nunit|\\.Tests", RegexOptions.IgnoreCase), "Forbidden Player assembly edge: " + assembly.name + " -> " + reference);
                foreach (var reference in assembly.compiledAssemblyReferences)
                {
                    string name = Path.GetFileNameWithoutExtension(reference);
                    string file = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(reference));
                    RejectLinks(file);
                    bool engine = Regex.IsMatch(name, @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Microsoft\.CSharp|Microsoft\.Win32\.Primitives)$") && Inside(file, editor);
                    bool built = outputs.TryGetValue(name, out string output) && (declared.ContainsKey(name) || sdk.ContainsKey(name)) && file == output;
                    bool json = name == "Newtonsoft.Json" && Inside(file, installed["com.unity.nuget.newtonsoft-json"].resolvedPath);
                    // UPM's test-framework dependency is an implicit compiler input even without test asmdefs.
                    // Verify exactly that BuiltIn plugin here; the emitted assembly and Player file gates forbid it.
                    if (name == "nunit.framework" && reachable.Contains(nunitPackage) && installed[nunitPackage].source == UnityEditor.PackageManager.PackageSource.BuiltIn)
                    {
                        string input = Path.GetFullPath(Path.Combine(installed[nunitPackage].resolvedPath, nunitRelative));
                        if (file.Equals(input, StringComparison.OrdinalIgnoreCase))
                        {
                            if (nunitInput == null)
                            {
                                string original = Path.Combine(editor, "Resources/PackageManager/BuiltInPackages", nunitPackage, nunitRelative);
                                RejectLinks(original);
                                nunitSha = Hash(file);
                                Require(nunitSha == Hash(original) && Hash(file + ".meta") == Hash(original + ".meta"), "Implicit NUnit input differs from the installed Editor package.");
                                nunitInput = file;
                            }
                            continue;
                        }
                    }
                    Require((engine || built || json) && !Regex.IsMatch(name, "Editor|TestRunner|nunit|\\.Tests", RegexOptions.IgnoreCase), "Forbidden compiled Player dependency provenance: " + assembly.name + " -> " + reference);
                }
                assemblies.Add(new JObject
                {
                    ["name"] = assembly.name,
                    ["definition"] = definition,
                    ["definitionSha256"] = Hash(physical),
                    ["package"] = owner?.name,
                    ["sources"] = new JArray(assembly.sourceFiles.Select(source => new JObject { ["path"] = source, ["sha256"] = Hash(source) })),
                    ["references"] = new JArray(assembly.assemblyReferences.Select(reference => reference.name)),
                    ["compiledReferences"] = new JArray(assembly.compiledAssemblyReferences)
                });
            }
            Require(player.Any(assembly => assembly.name == "YLFramework.Equipment") && player.Any(assembly => assembly.name == "YLFramework.Runtime") && player.Any(assembly => assembly.name == "YLFramework.Equipment.Acceptance.Player"), "The actual released Equipment/main/probe Player closure is incomplete.");
            return new JObject
            {
                ["catalog"] = job.packageCatalog,
                ["catalogSha256"] = job.packageCatalogSha256,
                ["entryPackage"] = entry.name,
                ["packages"] = facts,
                ["playerAssemblies"] = assemblies,
                ["implicitCompilerInput"] = nunitInput == null ? null : new JObject { ["path"] = nunitInput, ["sha256"] = nunitSha }
            };
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
