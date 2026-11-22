#if UNITY_EDITOR
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
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using YLFramework.Acceptance;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Testing
{
    public static class ResourceAddressablesPlayerEntry
    {
        // Parent CLI owns the host/process lease. This method never opens the development project.
        public static void Execute()
        {
            var argv = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(argv, "-ylResourceAddressablesPlayerRequest");
            if (index < 0 || index + 1 >= argv.Length || Array.LastIndexOf(argv, "-ylResourceAddressablesPlayerRequest") != index)
                throw new InvalidOperationException("An explicit owned Resource Addressables Player request is required.");
            string requestPath = Path.GetFullPath(argv[index + 1]);
            RejectLinks(requestPath);
            var request = Read(requestPath);
            Validate(request, requestPath);
            string output = Text(request, "output");
            var report = new JObject
            {
                ["schemaVersion"] = 1, ["purpose"] = "YLResource.Addressables.PlayerBuild", ["status"] = "Failed",
                ["inputHash"] = Text(request, "inputHash"), ["hostId"] = Text(request, "hostId"),
                ["resultId"] = Text(request, "resultId"), ["workspaceId"] = Text(request, "workspaceId"),
                ["requestPath"] = requestPath, ["requestSha256"] = Hash(requestPath),
                ["unityVersion"] = Application.unityVersion, ["isEditor"] = Application.isEditor,
                ["isBatchMode"] = Application.isBatchMode, ["backend"] = "IL2CPP", ["target"] = "StandaloneWindows64",
                ["startedUtc"] = DateTime.UtcNow.ToString("o")
            };
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            report["pid"] = process.Id;
            int exitCode = 1;
            try
            {
                Build(request, report);
                report["status"] = "Built";
                exitCode = 0;
            }
            catch (Exception error)
            {
                report["error"] = error.ToString();
                Debug.LogException(error);
            }
            finally
            {
                report["finishedUtc"] = DateTime.UtcNow.ToString("o");
                WriteNew(output, report.ToString(Formatting.Indented));
                EditorApplication.Exit(exitCode);
            }
        }

        private static void Build(JObject request, JObject report)
        {
            Require(!EditorUtility.scriptCompilationFailed && !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                "The owned host must have a successful settled compile.");
            Require(EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64 &&
                BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64),
                "PrerequisiteNotMet: launch the supported Windows x64 build target; do not fall back to Mono.");
            Require(!AddressableAssetSettingsDefaultObject.SettingsExists &&
                !EditorBuildSettings.TryGetConfigObject<AddressableAssetSettingsDefaultObject>(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName, out _) &&
                !AssetDatabase.IsValidFolder(AddressableAssetSettingsDefaultObject.kDefaultConfigFolder) &&
                !Directory.Exists(Path.Combine(Project(), AddressableAssetSettingsDefaultObject.kDefaultConfigFolder)),
                "Use a fresh owned Player host; existing Addressables settings/configuration are not adopted or overwritten.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                Require(!scene.isDirty && scene.rootCount == 0, "Refusing to replace a dirty or nonempty host scene.");
            }
            string project = Project();
            string token = Guid.NewGuid().ToString("N");
            string shortToken = token.Substring(0, 8);
            string assets = "Assets/__YLResourceAA_" + shortToken;
            string playerRoot = Path.Combine(project, "P", shortToken);
            string remoteRoot = Path.Combine(project, "RA", shortToken, "Remote");
            string localRoot = Path.GetFullPath(Addressables.BuildPath);
            foreach (var path in new[] { Path.Combine(project, assets), playerRoot, remoteRoot })
                Require(!File.Exists(path) && !Directory.Exists(path), "Unknown build output exists: " + path);
            Require(!Directory.Exists(localRoot) || !Directory.EnumerateFileSystemEntries(localRoot).Any(),
                "Existing Addressables BuildPath bytes are not silently replaced.");
            RejectLinks(Path.GetDirectoryName(playerRoot));
            RejectLinks(Path.GetDirectoryName(remoteRoot));
            Directory.CreateDirectory(playerRoot);
            Directory.CreateDirectory(remoteRoot);
            Require(!string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", assets.Substring("Assets/".Length))), "Fixture folder creation failed.");
            AssetDatabase.CreateFolder(assets, "AA");
            AssetDatabase.CreateFolder("Assets", "AddressableAssetsData");
            var beforeBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            var beforeStripping = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Standalone);
            string defaultObjectGuid = null;
            try
            {
                var settings = AddressableAssetSettings.Create(assets + "/AA", "Addressables", true, true);
                AddressableAssetSettingsDefaultObject.Settings = settings;
                defaultObjectGuid = AssetDatabase.AssetPathToGUID(AddressableAssetSettingsDefaultObject.kDefaultConfigFolder + "/DefaultObject.asset");
                settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                settings.BuildRemoteCatalog = false;
                var encryptedRemoteGroup = (bool?)request["encryptedRemoteGroup"] == true;
                var verifiedSizeSnapshot = (bool?)request["verifiedSizeSnapshot"] == true;
                var heldDownload = (string)request["heldDownload"] ?? "";
                if (encryptedRemoteGroup) settings.DisableCatalogUpdateOnStartup = true;
                int builder = settings.DataBuilders.FindIndex(value => value is BuildScriptPackedMode);
                Require(builder >= 0, "The official packed player content builder is unavailable.");
                settings.ActivePlayerDataBuilderIndex = builder;
                var local = settings.DefaultGroup;
                ConfigureGroup(settings, local, AddressableAssetSettings.kLocalBuildPath, AddressableAssetSettings.kLocalLoadPath);
                settings.profileSettings.CreateValue("AcceptanceRemoteBuild", remoteRoot.Replace('\\', '/'));
                settings.profileSettings.CreateValue("AcceptanceRemoteLoad", ResourceAddressablesPlayerProbe.RemotePrefix.TrimEnd('/'));
                var remote = settings.CreateGroup("AcceptanceRemote", false, false, false, null,
                    typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                ConfigureGroup(settings, remote, "AcceptanceRemoteBuild", "AcceptanceRemoteLoad");
                File.WriteAllText(Path.Combine(project, assets, "Local.txt"), "local:" + token, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(project, assets, "Cached.txt"), "cached:" + token, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(project, assets, "Parallel.txt"), "parallel:" + token, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(assets + "/Local.txt", ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(assets + "/Cached.txt", ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(assets + "/Parallel.txt", ImportAssetOptions.ForceSynchronousImport);
                settings.AddLabel(ResourceAddressablesPlayerProbe.BatchLabel);
                var text = Add(settings, local, assets + "/Local.txt", ResourceAddressablesPlayerProbe.TextKey);
                text.SetLabel(ResourceAddressablesPlayerProbe.BatchLabel, true);
                text.SetLabel(text.address, true, true);
                Add(settings, local, assets + "/Parallel.txt", ResourceAddressablesPlayerProbe.ParallelKey).SetLabel(ResourceAddressablesPlayerProbe.BatchLabel, true);
                var cached = Add(settings, remote, assets + "/Cached.txt", ResourceAddressablesPlayerProbe.CachedKey);
                AddressableAssetGroup protectedGroup = null;
                P0SizePackedBuilder sizeBuilder = null;
                if (encryptedRemoteGroup)
                {
                    settings.AddLabel(ResourceAddressablesPlayerProbe.SizeMixedLabel);
                    cached.SetLabel(ResourceAddressablesPlayerProbe.SizeMixedLabel, true);
                    text.SetLabel(ResourceAddressablesPlayerProbe.SizeMixedLabel, true);
                    protectedGroup = settings.CreateGroup("P0Protected", false, false, false, null, typeof(BundledAssetGroupSchema));
                    ConfigureGroup(settings, protectedGroup, "AcceptanceRemoteBuild", "AcceptanceRemoteLoad");
                    var schema = protectedGroup.GetSchema<BundledAssetGroupSchema>();
                    schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
                    schema.UseAssetBundleCache = false;
                    foreach (var pair in new[] { ("SizeA", ResourceAddressablesPlayerProbe.SizeProtectedA), ("SizeB", ResourceAddressablesPlayerProbe.SizeProtectedB) })
                    {
                        string path = assets + "/" + pair.Item1 + ".txt";
                        WriteNew(Path.GetFullPath(path), pair.Item1 + ":" + token);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                        Add(settings, protectedGroup, path, pair.Item2).SetLabel(ResourceAddressablesPlayerProbe.SizeMixedLabel, true);
                    }
                    if (heldDownload == "Separated")
                    {
                        var authored = new SerializedObject(schema);
                        var selection = authored.FindProperty("m_AssetBundleProviderType");
                        Require(selection != null, "Owned schema lacks its serialized provider choice.");
                        selection.FindPropertyRelative("m_AssemblyName").stringValue = typeof(P0HeldBundleProvider).Assembly.FullName;
                        selection.FindPropertyRelative("m_ClassName").stringValue = typeof(P0HeldBundleProvider).FullName;
                        authored.ApplyModifiedPropertiesWithoutUndo();
                        Require(schema.AssetBundleProviderType.Value == typeof(P0HeldBundleProvider), "The author choice is not the isolated Bundle Provider.");
                    }
                    if (verifiedSizeSnapshot || heldDownload == "Separated")
                    {
                        sizeBuilder = ScriptableObject.CreateInstance<P0SizePackedBuilder>();
                        sizeBuilder.ProtectedGroup = protectedGroup;
                        sizeBuilder.SeparateMount = heldDownload == "Separated";
                        AssetDatabase.CreateAsset(sizeBuilder, assets + "/P0SizePackedBuilder.asset");
                        Require(settings.AddDataBuilder(sizeBuilder), "Could not register the isolated Packed Builder hook.");
                        settings.ActivePlayerDataBuilderIndex = settings.DataBuilders.IndexOf(sizeBuilder);
                    }
                }
                var benchmarkClose = (bool?)request["scopeCloseBenchmark"] == true;
                if (benchmarkClose)
                {
                    AddScopeCloseContent(settings, assets);
                }
                var clean = new GameObject("Acceptance clean Prefab");
                try
                {
                    new GameObject("Child").transform.SetParent(clean.transform, false);
                    Require(PrefabUtility.SaveAsPrefabAsset(clean, assets + "/Clean.prefab"), "Clean Prefab save failed.");
                }
                finally { UnityEngine.Object.DestroyImmediate(clean); }
                Add(settings, local, assets + "/Clean.prefab", ResourceAddressablesPlayerProbe.PrefabKey);
                var additive = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Official AA additive marker");
                Require(EditorSceneManager.SaveScene(additive, assets + "/Additive.unity"), "Additive scene save failed.");
                Add(settings, local, assets + "/Additive.unity", ResourceAddressablesPlayerProbe.SceneKey);
                var (guidOnly, addressOnly) = AddPublishedKeyContent(settings, assets, token);
                var packedSprite = AddPackedSprite(settings, assets, report);
                settings.AddLabel("unassigned-publication-label");
                AssetDatabase.SaveAssets();
                var names = ResourceCodeGenerator.GenerateSource(settings, "YLFramework.Acceptance.Published");
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult content);
                Require(content != null && string.IsNullOrEmpty(content.Error), "Official BuildPlayerContent failed: " + content?.Error);
                var files = Inventory(localRoot, remoteRoot);
                if (encryptedRemoteGroup)
                {
                    string RelativeBundle(AddressableAssetGroup group)
                    {
                        string path = Path.GetFullPath(content.AssetBundleBuildResults.Single(bundle => bundle.SourceAssetGroup == group).FilePath);
                        Require(Inside(path, remoteRoot) && File.Exists(path), "Expected a real remote output owned by " + group.Name);
                        return path.Substring(remoteRoot.Length + 1).Replace('\\', '/');
                    }
                    string catalog = Text(request, "addressablesVersion") == "2.10.3" ? "catalog.bin" : "catalog.json";
                    Require(File.Exists(Path.Combine(localRoot, catalog)), "The version's default catalog file was not written: " + catalog);
                    report["p0ProtectedGroupGuid"] = protectedGroup.Guid;
                    report["p0ProtectedBundle"] = RelativeBundle(protectedGroup);
                    report["p0OrdinaryBundle"] = RelativeBundle(remote);
                    report["p0Catalog"] = catalog;
                    report["p0PackedPlainBytes"] = sizeBuilder == null ? new FileInfo(Path.Combine(remoteRoot, (string)report["p0ProtectedBundle"])).Length : sizeBuilder.PackedPlainBytes;
                }
                Require(files.Any(file => (string)file["area"] == "aa" && ((string)file["path"]).EndsWith("settings.json", StringComparison.Ordinal)) &&
                    files.Any(file => (string)file["area"] == "aa" && ((string)file["path"]).Contains("catalog")) &&
                    files.Any(file => (string)file["area"] == "remote" && ((string)file["path"]).EndsWith(".bundle", StringComparison.Ordinal)),
                    "Official settings/catalog/remote bundle bytes are missing.");
                string fingerprint = ContentHash(files);
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Addressables).Assembly);
                Require(package != null && package.version == Text(request, "addressablesVersion") && package.source == PackageSource.Registry,
                    "The loaded official registry Addressables package does not match the requested profile.");
                // Only the isolated test assembly consumes this generated source. Editor compilation does not reference it.
                const string GeneratedNames = "Assets/Acceptance/Runtime/PublishedResourceNames.cs";
                var generatedPath = Path.Combine(project, GeneratedNames);
                Require(!File.Exists(generatedPath + ".meta"), "Refusing an unknown generated source meta.");
                WriteNew(generatedPath, names);
                AssetDatabase.ImportAsset(GeneratedNames, ImportAssetOptions.ForceSynchronousImport);
                var identity = new ResourceAddressablesPlayerProbe.BuildIdentity
                {
                    buildToken = token, inputHash = Text(request, "inputHash"), contentHash = fingerprint,
                    unityVersion = Application.unityVersion, addressablesVersion = package.version, stripping = Text(request, "stripping"),
                    playerRoot = playerRoot, graphics = (bool)request["graphics"], hostId = Text(request, "hostId"),
                    resultId = Text(request, "resultId"), workspaceId = Text(request, "workspaceId"), buildFlavor = BuildFlavor(request),
                    scopeCloseBenchmark = benchmarkClose, encryptedRemoteGroup = encryptedRemoteGroup,
                    verifiedSizeSnapshot = verifiedSizeSnapshot, heldDownload = heldDownload, publishedSourceSha256 = Hash(generatedPath),
                    guidOnlyKey = guidOnly.guid, suppressedGuidKey = addressOnly.guid, packedSpriteGuid = packedSprite.guid
                };
                var boot = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Resource Addressables IL2CPP acceptance").AddComponent<ResourceAddressablesPlayerProbe>().Configure(identity);
                Require(EditorSceneManager.SaveScene(boot, assets + "/Player.unity"), "Player boot scene save failed.");
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, (ManagedStrippingLevel)Enum.Parse(typeof(ManagedStrippingLevel), identity.stripping));
                string executable = Path.Combine(playerRoot, "P.exe");
                report.Merge(JObject.FromObject(identity));
                report["purpose"] = "YLResource.Addressables.PlayerBuild";
                report["player"] = executable;
                report["remoteRoot"] = remoteRoot;
                report["localContentRoot"] = localRoot;
                report["files"] = files;
                report["addressablesOutputPath"] = content.OutputPath;
                report["addressablesAssemblyVersion"] = typeof(Addressables).Assembly.GetName().Version.ToString();
                report["addressablesSource"] = package.source.ToString();
                report["configuredGraphicsApis"] = JArray.FromObject(PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64).Select(value => value.ToString()));
                report["nativeModule"] = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "windowsstandalonesupport");
                report["nativeModulePresent"] = Directory.Exists((string)report["nativeModule"]);
                string variations = Path.Combine((string)report["nativeModule"], "Variations");
                report["nativeVariations"] = JArray.FromObject(Directory.Exists(variations)
                    ? Directory.GetDirectories(variations).Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>());
                string il2cpp = Path.Combine(EditorApplication.applicationContentsPath, "il2cpp", "build", "deploy", "il2cpp.exe");
                report["il2cppExecutable"] = il2cpp;
                report["il2cppExecutablePresent"] = File.Exists(il2cpp);
                report["backend"] = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone).ToString();
                report["stripping"] = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Standalone).ToString();
                report["manifestSha256"] = Hash(Path.Combine(project, "Packages", "manifest.json"));
                report["packageLockSha256"] = Hash(Path.Combine(project, "Packages", "packages-lock.json"));
                Require((string)report["backend"] == "IL2CPP" && (string)report["stripping"] == identity.stripping,
                    "The actual Player backend/stripping settings differ from the acceptance request.");
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { assets + "/Player.unity" }, locationPathName = executable,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.StrictMode | (identity.buildFlavor == "Development" ? BuildOptions.Development : BuildOptions.None),
                    extraScriptingDefines = new[] { ResourceAddressablesPlayerProbe.Define }
                });
                report["buildResult"] = build.summary.result.ToString();
                report["buildErrors"] = build.summary.totalErrors;
                report["buildWarnings"] = build.summary.totalWarnings;
                report["buildDurationSeconds"] = build.summary.totalTime.TotalSeconds;
                report["buildMessages"] = JArray.FromObject(build.steps.SelectMany(step => step.messages)
                    .Where(message => message.type == LogType.Error || message.type == LogType.Exception).Select(message => message.content));
                Require(build.summary.result == BuildResult.Succeeded && build.summary.totalErrors == 0,
                    "Actual IL2CPP BuildPlayer failed; native SDK/compiler/license prerequisites must be resolved, never bypassed.");
                Require(Hash(generatedPath) == identity.publishedSourceSha256, "The generated consumer source changed during the build.");
                string streaming = Path.Combine(playerRoot, "P_Data", "StreamingAssets", "aa");
                foreach (var file in files.Where(file => (string)file["area"] == "aa"))
                {
                    string deployed = Path.Combine(streaming, (string)file["path"]);
                    Require(File.Exists(deployed) && Hash(deployed) == (string)file["sha256"],
                        "The official AA build processor did not deploy matching StreamingAssets/aa bytes: " + deployed);
                }
                string native = Path.Combine(playerRoot, "GameAssembly.dll");
                string metadata = Path.Combine(playerRoot, "P_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
                Require(File.Exists(executable) && File.Exists(native) && File.Exists(metadata), "Actual IL2CPP binary/metadata artifacts are missing.");
                report["streamingAssetsAa"] = streaming;
                report["playerSha256"] = Hash(executable);
                report["gameAssemblySha256"] = Hash(native);
                report["metadataSha256"] = Hash(metadata);
                report["runtimeAssembly"] = typeof(ResourceSystem).Assembly.GetName().Name;
                report["probeAssembly"] = typeof(ResourceAddressablesPlayerProbe).Assembly.GetName().Name;
                report["fixtureAssets"] = assets;
                WriteNew(Path.Combine(playerRoot, ResourceAddressablesPlayerProbe.ManifestName), JsonUtility.ToJson(identity, true));
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, beforeBackend);
                PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, beforeStripping);
                if (defaultObjectGuid != null)
                {
                    string path = AddressableAssetSettingsDefaultObject.kDefaultConfigFolder + "/DefaultObject.asset";
                    Require(AssetDatabase.AssetPathToGUID(path) == defaultObjectGuid, "The generated Addressables default object changed identity.");
                    AddressableAssetSettingsDefaultObject.Settings = null;
                    EditorBuildSettings.RemoveConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName);
                    Require(AssetDatabase.DeleteAsset(path), "The owned default-object output did not retire.");
                }
                AssetDatabase.SaveAssets();
            }
            VerifyHostInputs(request);
        }

        private static void AddScopeCloseContent(AddressableAssetSettings settings, string assets)
        {
            var folder = assets + "/ScopeClose";
            AssetDatabase.CreateFolder(assets, "ScopeClose");
            var group = settings.CreateGroup("AcceptanceScopeClose", false, false, false, null, typeof(BundledAssetGroupSchema));
            ConfigureGroup(settings, group, AddressableAssetSettings.kLocalBuildPath, AddressableAssetSettings.kLocalLoadPath);
            group.GetSchema<BundledAssetGroupSchema>().BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            for (int index = 0; index < ResourceAddressablesPlayerProbe.ScopeCloseAssetCount; index++)
            {
                string path = folder + "/" + ResourceAddressablesPlayerProbe.ScopeCloseAssetName(index) + ".txt";
                WriteNew(Path.GetFullPath(path), ResourceAddressablesPlayerProbe.ScopeCloseKey(index));
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            for (int index = 0; index < ResourceAddressablesPlayerProbe.ScopeCloseAssetCount; index++)
            {
                string path = folder + "/" + ResourceAddressablesPlayerProbe.ScopeCloseAssetName(index) + ".txt";
                Add(settings, group, path, ResourceAddressablesPlayerProbe.ScopeCloseKey(index));
            }
        }

        private static AddressableAssetEntry AddPackedSprite(AddressableAssetSettings settings, string assets, JObject report)
        {
            const string Name = ResourceAddressablesPlayerProbe.PackedSpriteName;
            string path = assets + "/" + Name + ".png";
            string fullPath = Path.GetFullPath(path);
            // Deterministic RGBA gradient PNG; no dependence on the batch Editor's graphics device.
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAA0AAAAHCAYAAADTcMcaAAAAjUlEQVR42hXLMQ0DIQAAQCzUApWABVIHWCCpAlIHDDWABZYKwALTG/oew40XQvjcDyKJTKHS6Awmi00IT4lIIlOoNDqDyWI/T3pJRBKZQqXRGUwW+3XSWyKSyBQqjc5gstjvk74SkUSmUGl0BpPF/p70k4gkMoVKozOYLPbvpEsiksgUKo3OYLLY1+f+Az/Uzz7AaIJtAAAAAElFTkSuQmCC");
            RejectLinks(fullPath);
            using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(png, 0, png.Length);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Require(importer != null, "Packed Sprite fixture must have a real PNG TextureImporter.");
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = ResourceAddressablesPlayerProbe.PackedSpritePixelsPerUnit;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();

            var imported = AssetDatabase.LoadAllAssetsAtPath(path);
            var texture = imported.OfType<Texture2D>().Single();
            var sprite = imported.OfType<Sprite>().Single();
            Require(texture && sprite && texture.name == Name && sprite.name == Name && sprite.texture == texture &&
                texture.width == ResourceAddressablesPlayerProbe.PackedSpriteWidth && texture.height == ResourceAddressablesPlayerProbe.PackedSpriteHeight &&
                sprite.pixelsPerUnit == ResourceAddressablesPlayerProbe.PackedSpritePixelsPerUnit,
                "Packed PNG did not import as the intended single Sprite with its Texture2D.");

            // Keep the fixture out of the text label and the existing benchmark bundles.
            var group = settings.CreateGroup("AcceptancePackedSprite", false, false, false, null, typeof(BundledAssetGroupSchema));
            ConfigureGroup(settings, group, AddressableAssetSettings.kLocalBuildPath, AddressableAssetSettings.kLocalLoadPath);
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.IncludeAddressInCatalog = true;
            schema.IncludeGUIDInCatalog = true;
            var entry = Add(settings, group, path, ResourceAddressablesPlayerProbe.PackedSpriteAddress);
            report["packedSpriteFixture"] = new JObject
            {
                ["assetPath"] = path, ["assetName"] = Name, ["address"] = entry.address, ["guid"] = entry.guid,
                ["pngSha256"] = Hash(fullPath), ["mainAssetType"] = AssetDatabase.GetMainAssetTypeAtPath(path).FullName,
                ["importedTypes"] = JArray.FromObject(imported.Select(asset => asset.GetType().FullName)),
                ["width"] = texture.width, ["height"] = texture.height, ["pixelsPerUnit"] = sprite.pixelsPerUnit
            };
            return entry;
        }

        private static (AddressableAssetEntry GuidOnly, AddressableAssetEntry AddressOnly) AddPublishedKeyContent(
            AddressableAssetSettings settings, string assets, string token)
        {
            BundledAssetGroupSchema Schema(AddressableAssetEntry entry) => entry.parentGroup.GetSchema<BundledAssetGroupSchema>();

            var guidOnly = AddText("GuidOnly", ResourceAddressablesPlayerProbe.SuppressedAddress, "guid-only:");
            Schema(guidOnly).IncludeAddressInCatalog = false;
            var addressOnly = AddText("AddressOnly", "acceptance/published/address-only", "address-only:");
            Schema(addressOnly).IncludeGUIDInCatalog = false;
            var noLabels = AddText("NoLabels", "acceptance/published/no-labels", "no-labels:");
            Schema(noLabels).IncludeLabelsInCatalog = false;
            noLabels.SetLabel(ResourceAddressablesPlayerProbe.BatchLabel, true);
            noLabels.SetLabel(ResourceAddressablesPlayerProbe.SuppressedLabel, true, true);
            var excluded = AddText("Excluded", ResourceAddressablesPlayerProbe.ExcludedAddress, "excluded:");
            Schema(excluded).IncludeInBuild = false;
            return (guidOnly, addressOnly);

            AddressableAssetEntry AddText(string name, string key, string payload)
            {
                var group = settings.CreateGroup("Acceptance" + name, false, false, false, null, typeof(BundledAssetGroupSchema));
                ConfigureGroup(settings, group, AddressableAssetSettings.kLocalBuildPath, AddressableAssetSettings.kLocalLoadPath);
                var path = assets + "/" + name + ".txt";
                WriteNew(Path.GetFullPath(path), payload + token);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                return Add(settings, group, path, key);
            }
        }

        private static void ConfigureGroup(AddressableAssetSettings settings, AddressableAssetGroup group, string build, string load)
        {
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            Require(schema != null, "Official packed bundle schema is missing.");
            using (var serialized = new SerializedObject(schema))
            {
                // Addressables 1.25 has no inherited-defaults toggle; configure only the real 2.x field.
                var defaults = serialized.FindProperty("m_UseDefaultSchemaSettings");
                if (defaults != null) { defaults.boolValue = false; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            }
            schema.IncludeInBuild = true;
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            schema.UseAssetBundleCache = true;
            schema.UseAssetBundleCrc = true;
            schema.UseAssetBundleCrcForCachedBundles = true;
            schema.BuildPath.SetVariableByName(settings, build);
            schema.LoadPath.SetVariableByName(settings, load);
        }

        private static AddressableAssetEntry Add(AddressableAssetSettings settings, AddressableAssetGroup group, string path, string key)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            Require(!string.IsNullOrEmpty(guid), "Fixture asset GUID is missing: " + path);
            var entry = settings.CreateOrMoveEntry(guid, group);
            entry.address = key;
            return entry;
        }

        private static JArray Inventory(string aa, string remote)
        {
            var result = new JArray();
            foreach (var pair in new[] { (Area: "aa", Root: aa), (Area: "remote", Root: remote) })
                foreach (string path in Directory.GetFiles(pair.Root, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
                {
                    RejectLinks(path);
                    result.Add(new JObject { ["area"] = pair.Area, ["path"] = path.Substring(pair.Root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'),
                        ["sha256"] = Hash(path), ["bytes"] = new FileInfo(path).Length });
                }
            return result;
        }

        private static string ContentHash(JArray files)
        {
            string text = string.Concat(files.OrderBy(file => (string)file["area"] + "/" + (string)file["path"], StringComparer.Ordinal)
                .Select(file => (string)file["area"] + "/" + (string)file["path"] + "\0" + (string)file["sha256"] + "\n"));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static void Validate(JObject request, string requestPath)
        {
            Require(Application.isBatchMode && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode,
                "Only a dedicated batch Editor host may perform this acceptance build.");
            Require(request["schemaVersion"]?.Type == JTokenType.Integer && (int)request["schemaVersion"] == 1 &&
                Text(request, "purpose") == "YLResource.Addressables.PlayerBuildRequest" && Text(request, "backend") == "IL2CPP" &&
                Text(request, "unityVersion") == Application.unityVersion && request["graphics"]?.Type == JTokenType.Boolean &&
                Regex.IsMatch(Text(request, "inputHash"), "^[0-9a-f]{64}$") &&
                new[] { "Minimal", "Low", "Medium", "High" }.Contains(Text(request, "stripping")) &&
                (BuildFlavor(request) == "Release" || BuildFlavor(request) == "Development"), "Invalid explicit IL2CPP Player request.");
            Require(request["scopeCloseBenchmark"] == null || request["scopeCloseBenchmark"].Type == JTokenType.Boolean,
                "Scope close measurement requires an explicit Boolean option.");
            Require((bool?)request["scopeCloseBenchmark"] != true || (BuildFlavor(request) == "Release" && (bool)request["graphics"]),
                "Scope close measurement uses the Release Player with an actual graphics device.");
            Require(request["encryptedRemoteGroup"] == null || request["encryptedRemoteGroup"].Type == JTokenType.Boolean,
                "Encryption experiment requires an explicit Boolean option.");
            Require((bool?)request["encryptedRemoteGroup"] != true || (BuildFlavor(request) == "Release" && (bool)request["graphics"] && (bool?)request["scopeCloseBenchmark"] != true),
                "Encryption experiment uses a dedicated graphics Release workload.");
            Require((request["verifiedSizeSnapshot"] == null || request["verifiedSizeSnapshot"].Type == JTokenType.Boolean) &&
                ((bool?)request["verifiedSizeSnapshot"] != true || (bool?)request["encryptedRemoteGroup"] == true),
                "Verified size snapshot requires the isolated experiment.");
            var held = (string)request["heldDownload"] ?? "";
            Require(held == "" || ((held == "Stock" || held == "Separated") && (bool?)request["encryptedRemoteGroup"] == true && (bool?)request["verifiedSizeSnapshot"] != true),
                "Only one isolated P0 workload may be selected.");
            string project = Project();
            string result = Path.GetFullPath(Text(request, "resultRoot"));
            string output = Path.GetFullPath(Text(request, "output"));
            Require(Inside(requestPath, result) && Inside(output, result) && Directory.Exists(Path.GetDirectoryName(output)) && !File.Exists(output),
                "Build request/report must be new owned result files.");
            RejectLinks(project); RejectLinks(result); RejectLinks(output);
            var host = Read(Path.Combine(project, ".yl-owner.json"));
            var owner = Read(Path.Combine(result, ".yl-owner.json"));
            var workspace = Read(Path.Combine(Directory.GetParent(project).Parent.FullName, ".yl-workspace.json"));
            foreach (var row in new[] { host, owner })
                Require((int)row["schemaVersion"] == 1 && Text(row, "purpose") == "YLTesting.Directory" &&
                    Text(row, "workspaceId") == Text(request, "workspaceId"), "Owned directory identity differs.");
            Require(Text(host, "kind") == "Host" && Text(host, "path") == project && Text(host, "id") == Text(request, "hostId") &&
                Text(owner, "kind") == "Result" && Text(owner, "path") == result && Text(owner, "id") == Text(request, "resultId") &&
                Text(workspace, "purpose") == "YLTesting.Workspace" && Text(workspace, "id") == Text(request, "workspaceId"),
                "Only matching existing Host/Result workspace ownership can authorize a build.");
            Require(!PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone).Split(';').Contains(ResourceAddressablesPlayerProbe.Define),
                "Acceptance must use BuildPlayerOptions.extraScriptingDefines, not a persistent production define.");
            VerifyHostInputs(request);
        }

        private static void VerifyHostInputs(JObject request)
        {
            var receipt = Read(Path.Combine(Project(), ".yl-inputs.json"));
            Require((int)receipt["schemaVersion"] == 1 && Text(receipt, "purpose") == "YLTesting.HostInputs" &&
                Text(receipt, "hostId") == Text(request, "hostId") && Text(receipt, "inputHash") == Text(request, "inputHash"),
                "The current input fingerprint does not match the prepared host.");
            foreach (var file in (JArray)receipt["files"])
            {
                string path = Path.GetFullPath(Path.Combine(Project(), (string)file["path"]));
                Require(Inside(path, Project()), "A frozen input escapes the host.");
                RejectLinks(path);
                Require(string.Equals(Hash(path), (string)file["sha256"], StringComparison.OrdinalIgnoreCase), "Prepared source input changed: " + path);
            }
        }

        private static JObject Read(string path)
        {
            RejectLinks(path);
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            Require(!reader.Read(), "Trailing build request JSON is refused.");
            return value;
        }
        private static string Project() => Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
        private static string BuildFlavor(JObject request) => request["buildFlavor"] == null ? "Release" : Text(request, "buildFlavor");
        private static bool Inside(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string Text(JObject row, string key) => row[key]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)row[key])
            ? (string)row[key] : throw new InvalidOperationException("Missing build identity: " + key);
        private static string Hash(string path)
        {
            RejectLinks(path);
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void WriteNew(string path, string text)
        {
            RejectLinks(path);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        private static void RejectLinks(string path)
        {
            for (string cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked Player build inputs/outputs are refused.");
        }
        private static void Require(bool condition, string error)
        {
            if (!condition) throw new InvalidOperationException(error);
        }
    }
}
#endif
