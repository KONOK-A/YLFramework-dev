using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.Layout;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource.Encryption;
using YLFramework.Resource.Encryption.Editor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using static YLFramework.Testing.FrozenDeliveryProbe;

namespace YLFramework.Testing
{
    public static class FrozenDeliveryEntry
    {
        private const string Root = "Assets/FrozenData";

        public static void Build()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-ylFrozenDelivery");
            Require(index >= 0 && index + 1 < arguments.Length && Application.isBatchMode, "Expected an owned batch fixture request.");
            string host = Path.GetDirectoryName(Application.dataPath);
            string requestPath = Path.GetFullPath(arguments[index + 1]);
            Require(index == Array.LastIndexOf(arguments, "-ylFrozenDelivery") && requestPath == Path.Combine(host, "fixture.json"),
                "Expected the owned batch fixture request path.");
            var input = JObject.Parse(File.ReadAllText(requestPath));
            string mode = (string)input["mode"], token = (string)input["token"], prefix = (string)input["prefix"];
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var report = new JObject { ["status"] = "Failed", ["token"] = token, ["mode"] = mode, ["pid"] = process.Id,
                ["unity"] = Application.unityVersion, ["input"] = (string)input["input"], ["requestSha256"] = ProtectedAssetAudit.DigestFile(requestPath) };
            int code = 1;
            try
            {
                string backend = (string)input["backend"];
                string catalogName = (string)input["catalogName"];
                string inputHash = (string)input["input"];
                FrozenPlayerBuildEvidence.Configure(backend);
                FrozenPlayerBuildEvidence.CapturePackageInputs(report, host, ProtectedAssetAudit.DigestFile);
                var package = PackageInfo.FindForAssembly(typeof(Addressables).Assembly);
                Require(package.version == (string)input["addressables"] && package.source == UnityEditor.PackageManager.PackageSource.Registry &&
                    (package.version == "2.10.3" ? catalogName == "catalog.bin" : package.version == "1.25.0" && catalogName == "catalog.json"),
                    "Addressables fixture must use the planned exact Registry package.");
                report["addressables"] = package.version; report["source"] = package.source.ToString();
                var main = PackageInfo.FindForAssembly(typeof(YLFramework.Resource.ResourceStartup).Assembly);
                var plugin = PackageInfo.FindForAssembly(typeof(EncryptedDeliverySession).Assembly);
                Require(main.source.ToString() == (string)input["packageSource"] && plugin.source == main.source && main.version == plugin.version,
                    "The actual main and encryption package sources differ from the fixture.");
                string nativeFile = Path.Combine(plugin.resolvedPath, "Runtime", "Plugins", "Win64", "ylresource_tink.dll");
                string imported = ProtectedAssetAudit.DigestFile(nativeFile);
                Require(imported == (string)input["pluginSha256"], "Native plugin identity differs at import.");
                report["importedPluginSha256"] = imported;
                report["importerSha256"] = ProtectedAssetAudit.DigestFile(nativeFile + ".meta");
                report["mainSource"] = main.source.ToString(); report["encryptionSource"] = plugin.source.ToString();
                Require(Directory.Exists(Path.Combine(host, "Assets")) && !Directory.Exists(Root) && AddressableAssetSettingsDefaultObject.Settings == null &&
                    !UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, "This tool requires a fresh owned Unity host.");
                AssetDatabase.CreateFolder("Assets", "FrozenData");
                AssetDatabase.CreateFolder("Assets", "AddressableAssetsData");
                var settings = AddressableAssetSettings.Create("Assets/AddressableAssetsData", "AddressableAssetSettings", true, true);
                AddressableAssetSettingsDefaultObject.Settings = settings;
                string privateRoot = Path.Combine(host, "Private");
                settings.ContentStateBuildPath = Path.Combine(privateRoot, "State").Replace('\\','/');
                settings.BuildRemoteCatalog = false;
                settings.DisableCatalogUpdateOnStartup = true;
                settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                settings.AddLabel(Mixed);

                var local = settings.DefaultGroup;
                var localSchema = Configure(settings, local, AddressableAssetSettings.kLocalBuildPath, AddressableAssetSettings.kLocalLoadPath);
#if UNITY_6000_3_OR_NEWER
                if (mode == "Positive")
                {
                    localSchema.UseDefaultSchemaSettings = true;
                    Require(localSchema.StripDownloadOptions, "The stock Win64 local defaults must omit Bundle download metadata.");
                }
#endif
                AddText(settings, local, "Local.txt", "local:" + token, LocalKey);
                if (mode == "Disabled")
                {
                    BuildDisabled(host, token, backend, inputHash, catalogName, report);
                    code = 0;
                    return;
                }
                var plain = settings.CreateGroup("Plain", false, false, false, null, typeof(BundledAssetGroupSchema));
                var secret = settings.CreateGroup("Protected", false, false, false, null, typeof(BundledAssetGroupSchema), typeof(EncryptedBundleGroupSchema));
                settings.profileSettings.CreateValue("FrozenPlainBuild", Path.Combine(host, mode == "OutputOverlap" ? "Delivery" : "Ordinary").Replace('\\','/'));
                settings.profileSettings.CreateValue("FrozenPlainLoad", prefix + "o");
                Configure(settings, plain, "FrozenPlainBuild", "FrozenPlainLoad");
                settings.profileSettings.CreateValue("FrozenPrivateBuild", Path.Combine(privateRoot, "Packed").Replace('\\','/'));
                settings.profileSettings.CreateValue("FrozenPrivateLoad", prefix + "r");
                var schema = Configure(settings, secret, "FrozenPrivateBuild", "FrozenPrivateLoad");
                schema.UseAssetBundleCache = false;
                var bundleFields = new SerializedObject(schema);
                var provider = bundleFields.FindProperty("m_AssetBundleProviderType");
                provider.FindPropertyRelative("m_AssemblyName").stringValue = typeof(EncryptedAssetBundleProvider).Assembly.FullName;
                provider.FindPropertyRelative("m_ClassName").stringValue = typeof(EncryptedAssetBundleProvider).FullName;
                bundleFields.ApplyModifiedPropertiesWithoutUndo();
                var keyField = new SerializedObject(secret.GetSchema<EncryptedBundleGroupSchema>());
                keyField.FindProperty("_keyId").stringValue = "K1";
                keyField.ApplyModifiedPropertiesWithoutUndo();

                AddressableAssetEntry hidden = null;
                var ordinary = AddText(settings, plain, "Ordinary.txt", "ordinary:" + token, OrdinaryKey);
                Sprite sprite = null;
                if (mode == "Performance")
                {
                    schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
                    AddPayload(settings, secret, "Small", 8 * 1024 * 1024, 97, SmallKey);
                    AddPayload(settings, secret, "Large", 128 * 1024 * 1024, 173, LargeKey);
                }
                else
                {
                    hidden = AddText(settings, secret, "Secret.txt", token, ProtectedKey);
                    AddText(settings, secret, "Twin.txt", token, ProtectedTwinKey);
                    sprite = MakeSprite();
                    Prefab(settings, secret, "PrivatePrefab", sprite, "frozen/private-prefab");
                    if (mode == "PlainBundleCopy" || mode == "OutputOverlap") Prefab(settings, plain, "PublicCopy", sprite, "frozen/public-prefab");
                }
                string scenePath = CreateScene(token, backend, inputHash, catalogName, mode == "PlayerCopy" ? sprite : null, mode == "Positive");
#if !UNITY_6000_3_OR_NEWER
                if (mode == "Positive")
                {
                    EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
                    Require(settings.groups.Any(candidate => candidate && candidate.GetSchema<PlayerDataGroupSchema>()?.IncludeBuildSettingsScenes == true),
                        "This fixture must publish the default built-in scene aliases.");
                }
#endif
                string player = Path.Combine(host, "P", "P.exe");
                if (mode == "SourceChanged") PlaintextDriftFixture.Arm(secret, privateRoot);
                if (mode == "OfficialOutput")
                {
                    string foreign = Path.Combine(host, "ForeignOutput");
                    Directory.CreateDirectory(foreign);
                    File.WriteAllText(Path.Combine(foreign, "must-remain"), "unchanged");
                    Addressables.LibraryPath = "ForeignOutput/";
                }
                using var keys = new Keys(Path.Combine(host, "fixture.key"), "author",
                    mode == "PublishChanged" ? () => AddStagedFile(host) : (Action)null);
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { scenePath }, locationPathName = player, target = BuildTarget.StandaloneWindows64
                };
                string releaseRoot = Path.Combine(host, "Delivery");
                if (mode == "Positive")
                {
                    void AssertNoWrites(string expected)
                    {
                        bool rejected = false;
                        try { EncryptedBundleExporter.BuildCore(settings, secret, options, "yl.frozen.acceptance", privateRoot, releaseRoot, keys, true); }
                        catch (InvalidDataException error) when (error.Message.Contains(expected)) { rejected = true; }
                        Require(rejected && !Directory.Exists(privateRoot) && !Directory.Exists(releaseRoot) && !Directory.Exists(Path.GetDirectoryName(player)) && keys.Opens == 0,
                            "Preflight must reject before creating a build output or opening a key: " + expected);
                    }
                    string library = Addressables.LibraryPath;
                    try
                    {
                        Addressables.LibraryPath = "UnapprovedLibrary/";
                        AssertNoWrites("Official Addressables output path");
                        Require(!Directory.Exists(Path.Combine(host, "UnapprovedLibrary")), "The unapproved official output received files.");
                    }
                    finally { Addressables.LibraryPath = library; }
                    const string ordinaryVariable = "FrozenPlainBuild";
                    try
                    {
                        settings.profileSettings.SetValue(settings.activeProfileId, ordinaryVariable, releaseRoot.Replace('\\', '/'));
                        AssertNoWrites("Ordinary Bundle build path overlaps");
                    }
                    finally { settings.profileSettings.SetValue(settings.activeProfileId, ordinaryVariable, Path.Combine(host, "Ordinary").Replace('\\', '/')); }
                    report["preflightIsWriteFree"] = true;
                }
                string oldBundle = Path.Combine(Addressables.BuildPath, "superseded-local.bundle");
                if (mode == "Positive")
                {
                    Directory.CreateDirectory(Addressables.BuildPath);
                    File.WriteAllText(oldBundle, "unrelated output from a previous fixture build");
                }
                var description = EncryptedBundleExporter.BuildCore(settings, secret, options, "yl.frozen.acceptance", privateRoot, releaseRoot, keys, allowLoopback: true);
                string data = Path.Combine(host, "P", "P_Data");
                if (mode == "Positive")
                {
                    Require(File.Exists(oldBundle) && !File.Exists(Path.Combine(data, "StreamingAssets", "aa", Path.GetFileName(oldBundle))),
                        "The exporter must leave stale official output untouched and never copy it to the Player.");
                    report["excludedStaleAddressables"] = true;
                }
                report["catalogSha256"] = description.CatalogSha256;
                report["routesSha256"] = description.RoutesSha256;
                FrozenPlayerBuildEvidence.Capture(report, backend, player, ProtectedAssetAudit.DigestFile, "YLFramework.Resource.Encryption", typeof(FrozenDeliveryProbe));
                report["pluginSha256"] = ProtectedAssetAudit.DigestFile(Directory.GetFiles(data, "ylresource_tink.dll", SearchOption.AllDirectories).Single());
                report["protectedPaths"] = new JArray(description.Bundles.Select(row => row.Binding.RelativePath));
                report["protectedBytes"] = description.Bundles.Sum(row => row.CipherLength);
                report["ordinaryPaths"] = new JArray(Directory.GetFiles(Path.Combine(host, "Ordinary"), "*.bundle").Select(Path.GetFileName));
                report["auditPath"] = Path.Combine(privateRoot, "audit.json");
                if (mode == "SourceChanged") report["sameLengthReplacement"] = PlaintextDriftFixture.Changed;
                if (mode == "WrongRoute") ReplaceWithDifferentOfficialCatalog(settings, secret, hidden, ordinary, description, prefix, data);
                CaptureShippedContent(report, data, catalogName);
                report["deliverySha256"] = ProtectedAssetAudit.DigestFile(Path.Combine(releaseRoot, "delivery.json"));
                report["status"] = "Built";
                code = 0;
            }
            catch (EncryptedBundleAuditException error) when (mode == "PlainBundleCopy" || mode == "PlayerCopy")
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                code = 0;
            }
            catch (InvalidDataException error) when (mode == "OutputOverlap" && error.Message.Contains("Ordinary Bundle build path overlaps"))
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                code = 0;
            }
            catch (InvalidDataException error) when (mode == "SourceChanged" && error.Message.Contains("Audited plaintext Bundle changed"))
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                report["sameLengthReplacement"] = PlaintextDriftFixture.Changed;
                report["opaqueLayoutRejected"] = PlaintextDriftFixture.OpaqueLayoutRejected;
                code = 0;
            }
            catch (InvalidDataException error) when (mode == "OfficialOutput" && error.Message.Contains("Official Addressables output path"))
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                code = 0;
            }
            catch (InvalidDataException error) when (mode == "PublishChanged" && error.Message.Contains("Staged delivery inventory"))
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                report["stagedChanged"] = _stagedChanged;
                code = 0;
            }
            catch (Exception error) { report["error"] = error.ToString(); Debug.LogException(error); }
            finally
            {
                using (var output = new FileStream((string)input["output"], FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(output)) writer.Write(report.ToString(Formatting.Indented));
                EditorApplication.Exit(code);
            }
        }

        private static bool _stagedChanged;

        private static void AddStagedFile(string host)
        {
            string stage = Directory.GetDirectories(host, ".yl-protected-*", SearchOption.TopDirectoryOnly).Single();
            Require(Directory.GetFiles(stage, "*.bundle").Length == 1, "The export fixture has no staged ciphertext yet.");
            File.WriteAllText(Path.Combine(stage, "unexpected.txt"), "test-only publication drift");
            _stagedChanged = true;
        }

        internal sealed class PlaintextDriftFixture : IPreprocessBuildWithReport, IPostprocessBuildWithReport
        {
            private static AddressableAssetGroup _group;
            private static string _privateRoot;
            private static bool _armed;
            internal static bool Changed, OpaqueLayoutRejected;
            public int callbackOrder => 0;

            internal static void Arm(AddressableAssetGroup group, string privateRoot)
            {
                _group = group;
                _privateRoot = privateRoot;
                _armed = true;
            }

            public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
            {
                if (!_armed) return;
                _armed = false;
                string root = Path.Combine(_privateRoot, "Packed");
                string source = Directory.GetFiles(root, "*.bundle").Single();
                using var file = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                long size = file.Length;
                file.Position = size - 1;
                byte original = checked((byte)file.ReadByte());
                file.Position = size - 1;
                file.WriteByte((byte)(original ^ 1));
                file.Flush();
                Changed = file.Length == size;
            }

            public void OnPostprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
            {
                if (!Changed) return;
                // Calibrate the gate against one unattributed ordinary row in memory; do not alter the saved official Layout.
                var layout = BuildLayout.Open(Path.Combine(_privateRoot, "official-buildlayout.json"), readHeader: true, readFullFile: true);
                try
                {
                    var bundles = layout.Groups.SelectMany(group => group.Bundles).Concat(layout.BuiltInBundles).ToArray();
                    var @protected = new HashSet<string>(Directory.GetFiles(Path.Combine(_privateRoot, "Packed"), "*.bundle").Select(Path.GetFileName), StringComparer.Ordinal);
                    var physical = new HashSet<string>(bundles.Select(bundle => bundle.Name), StringComparer.Ordinal);
                    var ordinary = bundles.Single(bundle => bundle.Name.StartsWith("plain_", StringComparison.Ordinal));
                    const string unknown = "Library/UnidentifiedPayload.asset";
                    ordinary.Files[0].OtherAssets.Add(new BuildLayout.DataFromOtherAsset { AssetGuid = "", AssetPath = unknown });
                    try { new ProtectedAssetAudit(_group, Array.Empty<string>()).AuditLayout(layout, @protected, physical); }
                    catch (InvalidDataException error) when (error.Message.Contains(unknown) && error.Message.Contains("unattributed")) { OpaqueLayoutRejected = true; }
                    Require(OpaqueLayoutRejected, "The actual Layout with an unattributed packed row was permitted.");
                }
                finally { layout?.Close(); }
            }
        }

        private static string CreateScene(string token, string backend, string inputHash, string catalogName, Sprite leaked, bool checkStockDefaults = false)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Frozen encryption test").AddComponent<FrozenDeliveryProbe>().Configure(token, backend, inputHash, catalogName, checkStockDefaults);
            if (leaked) new GameObject("Scene duplicate").AddComponent<SpriteRenderer>().sprite = leaked;
            string path = Root + "/Boot.unity";
            Require(EditorSceneManager.SaveScene(scene, path), "Failed to save owned boot fixture.");
            AssetDatabase.SaveAssets();
            return path;
        }

        private static void BuildDisabled(string host, string token, string backend, string inputHash, string catalogName, JObject report)
        {
            string scene = CreateScene(token, backend, inputHash, catalogName, null);
            AddressableAssetSettings.BuildPlayerContent(out var content);
            Require(string.IsNullOrEmpty(content.Error), "Stock local build failed: " + content.Error);
            string player = Path.Combine(host, "P", "P.exe");
            var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, locationPathName = player,
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.StrictMode });
            Require(build.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded && build.summary.totalErrors == 0, "Disabled-extension Player build failed.");
            string data = Path.Combine(host, "P", "P_Data");
            CaptureShippedContent(report, data, catalogName);
            report["pluginSha256"] = ProtectedAssetAudit.DigestFile(Directory.GetFiles(data, "ylresource_tink.dll", SearchOption.AllDirectories).Single());
            FrozenPlayerBuildEvidence.Capture(report, backend, player, ProtectedAssetAudit.DigestFile, "YLFramework.Resource.Encryption", typeof(FrozenDeliveryProbe));
            report["status"] = "Built";
        }

        private static void CaptureShippedContent(JObject report, string data, string catalogName)
        {
            string aa = Path.Combine(data, "StreamingAssets", "aa");
            string description = Path.Combine(aa, EncryptedBundleDescription.FileName);
            report["shippedCatalogSha256"] = ProtectedAssetAudit.DigestFile(Path.Combine(aa, catalogName));
            report["descriptionSha256"] = File.Exists(description) ? ProtectedAssetAudit.DigestFile(description) : null;
        }

        private static Sprite MakeSprite()
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var pixels = Enumerable.Range(0, 64).Select(value => new Color32((byte)(value * 3), (byte)(37 * value), 231, 255)).ToArray();
            texture.SetPixels32(pixels); texture.Apply();
            string path = Root + "/private-texture.png";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Single();
        }

        private static void AddPayload(AddressableAssetSettings settings, AddressableAssetGroup group, string name, int count, byte sentinel, string key)
        {
            string path = Root + "/" + name + ".bytes";
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new byte[256 * 1024];
                var random = new System.Random(sentinel);
                for (int position = 0; position < count; position += bytes.Length)
                {
                    random.NextBytes(bytes);
                    if (position == 0) bytes[0] = sentinel;
                    if (position + bytes.Length == count) bytes[bytes.Length - 1] = (byte)(255 - sentinel);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group);
            entry.address = key;
            entry.SetLabel(Mixed, true);
        }

        private static void ReplaceWithDifferentOfficialCatalog(AddressableAssetSettings settings, AddressableAssetGroup group,
            AddressableAssetEntry oldProtected, AddressableAssetEntry oldPlain, EncryptedBundleDescription expected, string prefix, string playerData)
        {
            oldProtected.address = "frozen/original-asset";
            oldPlain.address = ProtectedKey;
            var builder = ScriptableObject.CreateInstance<EncryptedPackedBuilder>();
            try
            {
                builder.ProtectedGroup = group; builder.ApplicationNamespace = "yl.frozen.acceptance";
                builder.ReleasePrefix = prefix + "r/"; builder.KeyId = "K1"; builder.Loopback = true;
                var output = builder.BuildData<AddressablesPlayerBuildResult>(new AddressablesDataBuilderInput(settings));
                Require(string.IsNullOrEmpty(output.Error) && builder.RoutesSha256 != expected.RoutesSha256, "Could not emit the real changed-route default Catalog.");
                string aa = Path.Combine(playerData, "StreamingAssets", "aa");
                foreach (string name in new[] { expected.CatalogName, "settings.json" })
                    File.Copy(Path.Combine(Addressables.BuildPath, name), Path.Combine(aa, name), true);
                var changed = new EncryptedBundleDescription(expected.CatalogName,
                    ProtectedAssetAudit.DigestFile(Path.Combine(aa, expected.CatalogName)), ProtectedAssetAudit.DigestFile(Path.Combine(aa, "settings.json")),
                    expected.RoutesSha256, expected.Bundles);
                File.WriteAllText(Path.Combine(aa, EncryptedBundleDescription.FileName), changed.Encode());
            }
            finally { UnityEngine.Object.DestroyImmediate(builder); }
        }

        private static BundledAssetGroupSchema Configure(AddressableAssetSettings settings, AddressableAssetGroup group, string build, string load)
        {
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            schema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
            schema.UseAssetBundleCache = true;
            schema.UseAssetBundleCrc = true;
            schema.BuildPath.SetVariableByName(settings, build);
            schema.LoadPath.SetVariableByName(settings, load);
            return schema;
        }

        private static AddressableAssetEntry AddText(AddressableAssetSettings settings, AddressableAssetGroup group, string name, string text, string address)
        {
            string path = Root + "/" + name;
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group);
            entry.address = address;
            entry.SetLabel(Mixed, true);
            return entry;
        }

        private static void Prefab(AddressableAssetSettings settings, AddressableAssetGroup group, string name, Sprite sprite, string address)
        {
            var instance = new GameObject(name);
            try
            {
                instance.AddComponent<SpriteRenderer>().sprite = sprite;
                string path = Root + "/" + name + ".prefab";
                Require(PrefabUtility.SaveAsPrefabAsset(instance, path), "Prefab was not created.");
                settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group).address = address;
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
