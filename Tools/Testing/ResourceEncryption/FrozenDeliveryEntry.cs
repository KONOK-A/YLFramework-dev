using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
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
        private const string ReleaseId = "Delivery";

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
            string layout = (string)input["deliveryLayout"];
            bool ordinaryControl = mode == "Performance" && ((string)input["experiment"] ?? "Default") == "Default";
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var report = new JObject { ["status"] = "Failed", ["token"] = token, ["mode"] = mode, ["pid"] = process.Id,
                ["unity"] = Application.unityVersion, ["input"] = (string)input["input"], ["requestSha256"] = ProtectedAssetAudit.DigestFile(requestPath) };
            int code = 1;
            try
            {
                report["deliveryLayout"] = layout;
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
                if (mode is "LocalNoHash" or "LocalNoHashCollision")
                {
                    local.Name = "Plain Local";
                    localSchema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.NoHash;
                    if (mode == "LocalNoHashCollision")
                    {
                        var collision = settings.CreateGroup("PlainLocal", false, false, false, null, typeof(BundledAssetGroupSchema));
                        var collisionSchema = Configure(settings, collision, AddressableAssetSettings.kLocalBuildPath, AddressableAssetSettings.kLocalLoadPath);
                        collisionSchema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.NoHash;
                        AddText(settings, collision, "Colliding.txt", "colliding:" + token, "frozen/nohash-collision");
                    }
                }
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
                settings.profileSettings.CreateValue("FrozenPlainBuild", Path.Combine(host, mode == "OutputOverlap" ? "Delivery" : "Ordinary").Replace('\\','/'));
                settings.profileSettings.CreateValue("FrozenPlainLoad", prefix + "o");
                Configure(settings, plain, "FrozenPlainBuild", "FrozenPlainLoad");
                string remotePrefix = layout == "Embedded" ? null : prefix + "r/" + ReleaseId + "/";
                var secret = MakeProtected(settings, mode == "AmbiguousGroups" ? "A B" : "Protected", "K1", layout != "Remote", remotePrefix, privateRoot);
                var peer = layout == "Remote" && mode is not ("PublishChanged" or "LateKeyFailure" or "AmbiguousGroups" or "PrePublishAbort") ? secret :
                    MakeProtected(settings, mode == "AmbiguousGroups" ? "AB" : "ProtectedPeer", "K2", layout == "Embedded", remotePrefix, privateRoot);
                var groups = peer == secret ? new[] { secret } : new[] { secret, peer };

                AddressableAssetEntry hidden = null;
                var ordinary = AddText(settings, plain, "Ordinary.txt", "ordinary:" + token, OrdinaryKey);
                Sprite sprite = null;
                if (mode == "Performance")
                {
                    foreach (var group in groups) group.GetSchema<BundledAssetGroupSchema>().BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
                    AddPayload(settings, secret, "Small", 8 * 1024 * 1024, 97, SmallKey);
                    AddPayload(settings, peer, "Large", 128 * 1024 * 1024, 173, LargeKey);
                }
                else
                {
                    hidden = AddText(settings, secret, "Secret.txt", token, ProtectedKey);
                    var twin = AddText(settings, peer, "Twin.txt", peer == secret ? token : "peer:" + token, ProtectedTwinKey);
                    if (mode == "MultiGroup")
                    {
                        var secondLocal = MakeProtected(settings, "SecondEmbedded", "K1", true, remotePrefix, privateRoot);
                        var secondRemote = MakeProtected(settings, "SecondRemote", "K2", false, remotePrefix, privateRoot);
                        groups = new[] { secret, peer, secondLocal, secondRemote };
                        settings.AddLabel(FourGroupLabel);
                        hidden.SetLabel(FourGroupLabel, true);
                        twin.SetLabel(FourGroupLabel, true);
                        AddText(settings, secondLocal, "SecondEmbedded.txt", "local-2:" + token, "frozen/second-local").SetLabel(FourGroupLabel, true);
                        AddText(settings, secondRemote, "SecondRemote.txt", "remote-2:" + token, "frozen/second-remote").SetLabel(FourGroupLabel, true);
                    }
                    sprite = MakeSprite();
                    Prefab(settings, secret, "PrivatePrefab", sprite, "frozen/private-prefab");
                    if (mode == "PlainBundleCopy" || mode == "OutputOverlap") Prefab(settings, plain, "PublicCopy", sprite, "frozen/public-prefab");
                    if (mode == "BadDependency")
                    {
                        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sprite));
                        settings.CreateOrMoveEntry(guid, secret).address = "frozen/pinned-sprite";
                        Prefab(settings, peer, "RemoteWithEmbedded", sprite, "frozen/remote-with-embedded");
                    }
                    if (mode is "RemoteClosure" or "OrdinaryDependency")
                    {
                        Require(layout == "Mixed", "The fixture requires a mixed layout.");
                        var publicSprite = MakeSprite("public-texture");
                        string publicGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(publicSprite));
                        settings.CreateOrMoveEntry(publicGuid, plain).address = "frozen/public-sprite";
                        if (mode == "RemoteClosure")
                        {
                            var bridge = Prefab(settings, local, "LocalBridge", publicSprite, "frozen/local-bridge");
                            var root = ScriptableObject.CreateInstance<FrozenDependencyFixture>();
                            root.Target = AssetDatabase.LoadAssetAtPath<GameObject>(bridge.AssetPath);
                            const string path = Root + "/EmbeddedRoot.asset";
                            AssetDatabase.CreateAsset(root, path);
                            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), secret).address = "frozen/embedded-root";
                            PublicDependencies(secret, bridge.guid, publicGuid);
                        }
                        else
                        {
                            Prefab(settings, peer, "RemoteWithOrdinary", publicSprite, "frozen/remote-with-ordinary");
                            PublicDependencies(peer, publicGuid);
                        }
                    }
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
                string releaseRoot = Path.Combine(host, ReleaseId);
                string player = Path.Combine(releaseRoot, "Player", "P.exe");
                if (mode == "SourceChanged") PlaintextDriftFixture.Arm(secret, privateRoot);
                if (mode == "OfficialOutput")
                {
                    string foreign = Path.Combine(host, "ForeignOutput");
                    Directory.CreateDirectory(foreign);
                    File.WriteAllText(Path.Combine(foreign, "must-remain"), "unchanged");
                    Addressables.LibraryPath = "ForeignOutput/";
                }
                using var keys = new Keys(Path.Combine(host, "fixture.key"), mode == "LateKeyFailure" ? mode : "author",
                    peerKeyFile: Path.Combine(host, "fixture-peer.key"));
                if (mode == "PublishChanged") keys.OnReturn = () =>
                {
                    if (keys.Returns == groups.Length) ChangeOrdinaryFile(host);
                };
                if (mode == "LateKeyFailure") keys.OnReturn = () => report["firstKeyReturned"] = keys.Returns == 1 && !Directory.Exists(releaseRoot);
                if (mode == "PrePublishAbort") keys.OnReturn = () =>
                {
                    if (keys.Returns != groups.Length) return;
                    var stage = Directory.GetDirectories(privateRoot, ".yl-release-*").Single();
                    Require(keys.Opens == groups.Length && File.Exists(Path.Combine(stage, "Player", "P.exe")) && !Directory.Exists(releaseRoot),
                        "The final key return did not observe a private candidate.");
                    report["interruptedAtFinalKeyReturn"] = true;
                    throw new OperationCanceledException("Fixture stopped before publication.");
                };
                if (mode == "TargetConflict") keys.OnReturn = () =>
                {
                    Directory.CreateDirectory(releaseRoot);
                    File.WriteAllText(Path.Combine(releaseRoot, "must-remain"), "unchanged");
                };
                var options = new BuildPlayerOptions
                {
                    scenes = new[] { scenePath }, locationPathName = player, target = BuildTarget.StandaloneWindows64
                };
                if (mode is "MissingPlayerRuntime" or "ChangedPlayerOutput") PlayerOutputDriftFixture.Arm(privateRoot, backend, mode);
                if (mode == "Positive")
                {
                    void AssertNoWrites(string expected)
                    {
                        bool rejected = false;
                        try { EncryptedBundleExporter.BuildCore(settings, groups, options, "yl.frozen.acceptance", privateRoot, releaseRoot, keys, true); }
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
                if (ordinaryControl) BuildPlainControl(host, settings, groups, prefix);
                var description = EncryptedBundleExporter.BuildCore(settings, groups, options, "yl.frozen.acceptance", privateRoot, releaseRoot, keys, allowLoopback: true);
                if (mode is "MissingPlayerRuntime" or "ChangedPlayerOutput")
                    throw new InvalidOperationException("The exporter published after the Player callback changed " + PlayerOutputDriftFixture.Changed);
                string data = Path.Combine(Path.GetDirectoryName(player), "P_Data");
                if (mode == "LocalNoHash")
                    Require(File.Exists(Path.Combine(data, "StreamingAssets", "aa", "StandaloneWindows64", FrozenDeliveryProbe.NoHashBundle)),
                        "The actual installed local Bundle must retain its stock NoHash name.");
                if (mode == "Positive")
                {
                    Require(File.Exists(oldBundle) && !File.Exists(Path.Combine(data, "StreamingAssets", "aa", Path.GetFileName(oldBundle))),
                        "The exporter must leave stale official output untouched and never copy it to the Player.");
                    report["excludedStaleAddressables"] = true;
                }
                report["catalogSha256"] = description.CatalogSha256;
                report["settingsSha256"] = description.SettingsSha256;
                report["routesSha256"] = description.RoutesSha256;
                FrozenPlayerBuildEvidence.Capture(report, backend, player, ProtectedAssetAudit.DigestFile, "YLFramework.Resource.Encryption", typeof(FrozenDeliveryProbe));
                report["pluginSha256"] = ProtectedAssetAudit.DigestFile(Directory.GetFiles(data, "ylresource_tink.dll", SearchOption.AllDirectories).Single());
                report["protectedPaths"] = new JArray(description.Bundles.Select(row => row.Binding.GroupPath));
                report["remoteProtectedPaths"] = new JArray(description.Bundles.Where(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps).Select(row => row.Binding.GroupPath));
                report["embeddedProtectedPaths"] = new JArray(description.Bundles.Where(row => row.Binding.Mode == EncryptedBundleDeliveryMode.Embedded).Select(row => row.Binding.GroupPath));
                report["protectedBytes"] = description.Bundles.Sum(row => row.CipherLength);
                report["remoteProtectedBytes"] = description.Bundles.Where(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps).Sum(row => row.CipherLength);
                report["ordinaryPaths"] = new JArray(Directory.GetFiles(Path.Combine(host, "Ordinary"), "*.bundle").Select(Path.GetFileName));
                report["auditPath"] = Path.Combine(privateRoot, "audit.json");
                if (mode == "SourceChanged") report["sameLengthReplacement"] = PlaintextDriftFixture.Changed;
                CaptureShippedContent(report, data, catalogName);
                if (mode == "WrongRoute")
                {
                    string target = Path.Combine(host, "PWrong");
                    CopyFixtureTree(Path.GetDirectoryName(player), target);
                    string changedData = Path.Combine(target, "P_Data");
                    ReplaceWithDifferentOfficialCatalog(settings, groups, hidden, ordinary, description, changedData);
                    var changed = new JObject();
                    CaptureShippedContent(changed, changedData, catalogName);
                    report["changedCatalogSha256"] = changed["shippedCatalogSha256"];
                    report["changedDescriptionSha256"] = changed["descriptionSha256"];
                }
                if (ordinaryControl) StagePlainControl(host, Path.GetDirectoryName(player), catalogName, description, report);
                else if (mode == "Performance") report["performanceInputs"] = new JArray(description.Bundles.OrderBy(row => row.Binding.PlainLength).Select(PerformanceInput));
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
                code = 0;
            }
            catch (InvalidDataException error) when ((mode is "MissingPlayerRuntime" or "ChangedPlayerOutput") &&
                PlayerOutputDriftFixture.Changed != null && error.Message.Contains(PlayerOutputDriftFixture.Changed) &&
                error.Message.StartsWith("The audited Player ", StringComparison.Ordinal))
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                report["changedPlayerFile"] = PlayerOutputDriftFixture.Changed;
                code = 0;
            }
            catch (InvalidDataException error) when (mode == "OfficialOutput" && error.Message.Contains("Official Addressables output path"))
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                code = 0;
            }
            catch (Exception error) when (mode == "PublishChanged" && _stagedChanged && (error is IOException || error is InvalidDataException))
            {
                report["status"] = "Rejected";
                report["error"] = error.Message;
                report["stagedChanged"] = _stagedChanged;
                code = 0;
            }
            catch (EncryptedBundleKeyUnavailableException error) when (mode == "LateKeyFailure" && (bool?)report["firstKeyReturned"] == true)
            {
                report["error"] = error.Message;
                report["status"] = "Rejected";
                code = 0;
            }
            catch (EncryptedBundleKeySourceException error) when (mode == "PrePublishAbort" && (bool?)report["interruptedAtFinalKeyReturn"] == true)
            {
                report["error"] = error.Message;
                report["status"] = "Rejected";
                code = 0;
            }
            catch (IOException error) when (mode == "TargetConflict" && File.Exists(Path.Combine(host, ReleaseId, "must-remain")))
            {
                report["error"] = error.Message;
                report["status"] = "Rejected";
                code = 0;
            }
            catch (InvalidDataException error) when ((mode == "RemoteClosure" && error.Message.Contains("Embedded Bundle closure contains a remote dependency")) ||
                ((mode is "AmbiguousGroups" or "LocalNoHashCollision") && error.Message.Contains("ambiguous normalized official Bundle name")))
            {
                report["error"] = error.Message;
                report["status"] = "Rejected";
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

        public static void RebuildForReplay()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-ylFrozenDelivery");
            string host = Path.GetDirectoryName(Application.dataPath);
            string request = Path.Combine(host, "fixture.json");
            Require(Application.isBatchMode && index >= 0 && index + 1 < arguments.Length &&
                index == Array.LastIndexOf(arguments, "-ylFrozenDelivery") && Path.GetFullPath(arguments[index + 1]) == request,
                "Expected the owned replay fixture request.");
            var input = JObject.Parse(File.ReadAllText(request));
            string backend = (string)input["backend"], catalogName = (string)input["catalogName"];
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var report = new JObject { ["status"] = "Failed", ["token"] = (string)input["token"], ["pid"] = process.Id,
                ["unity"] = Application.unityVersion, ["input"] = (string)input["input"], ["requestSha256"] = ProtectedAssetAudit.DigestFile(request) };
            int code = 1;
            try
            {
                FrozenPlayerBuildEvidence.Configure(backend);
                FrozenPlayerBuildEvidence.CapturePackageInputs(report, host, ProtectedAssetAudit.DigestFile);
                var package = PackageInfo.FindForAssembly(typeof(Addressables).Assembly);
                Require(package.version == (string)input["addressables"] && package.source == UnityEditor.PackageManager.PackageSource.Registry,
                    "The replay Player must resolve the same Addressables package.");
                report["addressables"] = package.version;
                AssetDatabase.CreateFolder("Assets", "FrozenData");
                AssetDatabase.CreateFolder("Assets", "AddressableAssetsData");
                var settings = AddressableAssetSettings.Create("Assets/AddressableAssetsData", "AddressableAssetSettings", true, true);
                AddressableAssetSettingsDefaultObject.Settings = settings;
                settings.BuildRemoteCatalog = false;
                settings.DisableCatalogUpdateOnStartup = true;
                settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                string scene = CreateScene((string)input["token"], backend, (string)input["input"], catalogName, null);
                string player = Path.Combine(host, "ReplayPlayer", "P.exe");
                string library = Path.Combine(host, "ReplayContent") + Path.DirectorySeparatorChar;
                Addressables.LibraryPath = library;
                string aa = Path.GetFullPath(Addressables.BuildPath);
                Require(aa.StartsWith(library, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(aa),
                    "Expected a new stock build location inside the owned replay library.");
                CopyFixtureTree(Path.Combine(host, "ReplayFixture"), aa);
                Require(File.Exists(Path.Combine(aa, "AddressablesLink", "link.xml")) && File.Exists(Path.Combine(aa, catalogName)) &&
                    File.Exists(Path.Combine(aa, EncryptedBundleDescription.FileName)), "The stock builder did not resolve the common frozen content.");
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, locationPathName = player,
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.StrictMode | BuildOptions.DetailedBuildReport });
                Require(result.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded && result.summary.totalErrors == 0,
                    "The comparison Player build failed.");
                string data = Path.Combine(host, "ReplayPlayer", "P_Data");
                FrozenPlayerBuildEvidence.Capture(report, backend, player, ProtectedAssetAudit.DigestFile,
                    "YLFramework.Resource.Encryption", typeof(FrozenDeliveryProbe));
                report["pluginSha256"] = ProtectedAssetAudit.DigestFile(Directory.GetFiles(data, "ylresource_tink.dll", SearchOption.AllDirectories).Single());
                CaptureShippedContent(report, data, catalogName);
                report["status"] = "Built";
                code = 0;
            }
            catch (Exception error) { report["error"] = error.ToString(); Debug.LogException(error); }
            finally
            {
                using var output = new FileStream((string)input["output"], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(output);
                writer.Write(report.ToString(Formatting.Indented));
                writer.Flush();
                EditorApplication.Exit(code);
            }
        }

        private static bool _stagedChanged;

        private static void ChangeOrdinaryFile(string host)
        {
            string path = Directory.GetFiles(Path.Combine(host, "Ordinary"), "*.bundle").Single();
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            int original = file.ReadByte();
            Require(original >= 0, "No ordinary source bytes to change.");
            file.Position = 0;
            file.WriteByte((byte)(original ^ 1));
            _stagedChanged = true;
        }

        internal sealed class PlaintextDriftFixture : IPreprocessBuildWithReport
        {
            private static AddressableAssetGroup _group;
            private static string _privateRoot;
            private static bool _armed;
            internal static bool Changed;
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
                string root = Path.Combine(_privateRoot, "Packed", _group.Guid);
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

        }

        internal sealed class PlayerOutputDriftFixture : IPostprocessBuildWithReport
        {
            private static string _privateRoot, _backend, _mode;
            internal static string Changed;
            public int callbackOrder => 1000;

            internal static void Arm(string privateRoot, string backend, string mode)
            {
                _privateRoot = privateRoot;
                _backend = backend;
                _mode = mode;
            }

            public void OnPostprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
            {
                if (_mode == null) return;
                string mode = _mode;
                _mode = null;
                string root = Path.GetDirectoryName(report.summary.outputPath);
                Require(Path.GetDirectoryName(root) == _privateRoot && Path.GetFileName(root).StartsWith(".yl-player-", StringComparison.Ordinal),
                    "Only the fixture's staged Player may change.");
                string relative = mode == "ChangedPlayerOutput" ? "UnityPlayer.dll" :
                    _backend == "IL2CPP" ? "GameAssembly.dll" : "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll";
                string file = Path.Combine(root, relative);
                long size = new FileInfo(file).Length;
                Require(size > 0 && report.GetFiles().Any(item => Path.GetFullPath(item.path).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase) &&
                    item.size == (ulong)size), "The real Player report must already identify the subject file.");
                if (mode == "MissingPlayerRuntime") File.Delete(file);
                else
                {
                    using (var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.None)) output.WriteByte(0);
                    Require(new FileInfo(file).Length == size + 1, "The reported Player file did not change length.");
                }
                Changed = relative;
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

        private const string ControlLibrary = "Library/PlainControl/";

        private static void BuildPlainControl(string host, AddressableAssetSettings settings, AddressableAssetGroup[] groups, string prefix)
        {
            string plainPacked = Path.Combine(host, "PlainPacked");
            string library = Addressables.LibraryPath;
            string state = settings.ContentStateBuildPath;
            var ordinary = settings.FindGroup("Plain").GetSchema<BundledAssetGroupSchema>();
            settings.profileSettings.CreateValue("ControlBuild", plainPacked.Replace('\\', '/'));
            settings.profileSettings.CreateValue("ControlLoad", prefix + "p");
            settings.profileSettings.CreateValue("ControlOrdinaryBuild", Path.Combine(host, "PlainOrdinary").Replace('\\', '/'));
            var builder = ScriptableObject.CreateInstance<BuildScriptPackedMode>();
            try
            {
                Addressables.LibraryPath = ControlLibrary;
                AddressablesRuntimeProperties.ClearCachedPropertyValues();
                settings.ContentStateBuildPath = Path.Combine(host, "PlainState").Replace('\\', '/');
                foreach (var group in groups)
                {
                    var schema = group.GetSchema<BundledAssetGroupSchema>();
                    bool embedded = group.GetSchema<EncryptedBundleGroupSchema>().DeliveryMode == EncryptedBundleDeliveryMode.Embedded;
                    schema.BuildPath.SetVariableByName(settings, embedded ? AddressableAssetSettings.kLocalBuildPath : "ControlBuild");
                    schema.LoadPath.SetVariableByName(settings, embedded ? AddressableAssetSettings.kLocalLoadPath : "ControlLoad");
                    schema.UseAssetBundleCache = true;
                    SetProvider(schema, typeof(AssetBundleProvider));
                }
                ordinary.BuildPath.SetVariableByName(settings, "ControlOrdinaryBuild");
                var content = builder.BuildData<AddressablesPlayerBuildResult>(new AddressablesDataBuilderInput(settings));
                Require(string.IsNullOrEmpty(content.Error), "Stock performance control failed: " + content.Error);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(builder);
                foreach (var group in groups)
                {
                    var schema = group.GetSchema<BundledAssetGroupSchema>();
                    schema.BuildPath.SetVariableByName(settings, "ProtectedBuild" + group.Guid);
                    schema.LoadPath.SetVariableByName(settings, "ProtectedLoad" + group.Guid);
                    schema.UseAssetBundleCache = false;
                    SetProvider(schema, typeof(EncryptedAssetBundleProvider));
                }
                ordinary.BuildPath.SetVariableByName(settings, "FrozenPlainBuild");
                settings.ContentStateBuildPath = state;
                Addressables.LibraryPath = library;
                AddressablesRuntimeProperties.ClearCachedPropertyValues();
            }
        }

        private static void StagePlainControl(string host, string original, string catalogName, EncryptedBundleDescription description, JObject report)
        {
            const string aa = "P_Data/StreamingAssets/aa";
            string control = Path.Combine(host, "PPlain"), local = Path.Combine(control, aa);
            string library = Addressables.LibraryPath;
            CopyFixtureTree(original, control, aa + "/");
            Addressables.LibraryPath = ControlLibrary;
            try { CopyFixtureTree(Addressables.BuildPath, local); }
            finally { Addressables.LibraryPath = library; }
            report["performanceInputs"] = new JArray(description.Bundles.OrderBy(row => row.Binding.PlainLength).Select(row =>
            {
                bool embedded = row.Binding.Mode == EncryptedBundleDeliveryMode.Embedded;
                string source = Directory.GetFiles(embedded ? local : Path.Combine(host, "PlainPacked"), row.Binding.RelativePath, SearchOption.AllDirectories).Single();
                Require(new FileInfo(source).Length == row.Binding.PlainLength && ProtectedAssetAudit.DigestFile(source) == row.Binding.PlainSha256,
                    "The stock Catalog did not build byte-identical Bundle input: " + row.Binding.RelativePath);
                var input = PerformanceInput(row);
                input["plainPath"] = Path.GetRelativePath(host, source).Replace('\\', '/');
                return input;
            }));
            report["plainCatalogSha256"] = ProtectedAssetAudit.DigestFile(Path.Combine(control, aa, catalogName));
        }

        private static JObject PerformanceInput(EncryptedBundleRecord row) => new JObject { ["path"] = row.Binding.RelativePath,
            ["clearBytes"] = row.Binding.PlainLength, ["clearSha256"] = row.Binding.PlainSha256,
            ["cipherBytes"] = row.CipherLength, ["cipherSha256"] = row.CipherSha256, ["mode"] = row.Binding.Mode.ToString() };

        private static void CopyFixtureTree(string from, string to, string skip = null)
        {
            foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(from, source).Replace('\\', '/');
                if (skip != null && relative.StartsWith(skip, StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination);
            }
        }

        private static Sprite MakeSprite(string name = "private-texture")
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var pixels = Enumerable.Range(0, 64).Select(value => new Color32((byte)(value * 3), (byte)(37 * value), 231, 255)).ToArray();
            texture.SetPixels32(pixels); texture.Apply();
            string path = Root + "/" + name + ".png";
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

        private static void ReplaceWithDifferentOfficialCatalog(AddressableAssetSettings settings, AddressableAssetGroup[] groups,
            AddressableAssetEntry oldProtected, AddressableAssetEntry oldPlain, EncryptedBundleDescription expected, string playerData)
        {
            oldProtected.address = "frozen/original-asset";
            oldPlain.address = ProtectedKey;
            var builder = ScriptableObject.CreateInstance<EncryptedPackedBuilder>();
            try
            {
                builder.ProtectedGroups = groups; builder.ApplicationNamespace = expected.Application;
                builder.ReleaseId = expected.ReleaseId; builder.ReleasePrefix = expected.ReleasePrefix; builder.Loopback = true;
                builder.PlayerScenes = new[] { Root + "/Boot.unity" };
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

        private static AddressableAssetGroup MakeProtected(AddressableAssetSettings settings, string name, string keyId, bool embedded,
            string remotePrefix, string privateRoot)
        {
            var group = settings.CreateGroup(name, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(EncryptedBundleGroupSchema));
            string build = "ProtectedBuild" + group.Guid, load = "ProtectedLoad" + group.Guid;
            settings.profileSettings.CreateValue(build, Path.Combine(privateRoot, "Packed", group.Guid).Replace('\\', '/'));
            settings.profileSettings.CreateValue(load, embedded ? FrozenAddressablesRoutes.RuntimeToken + "/yl-encrypted" : remotePrefix);
            var schema = Configure(settings, group, build, load);
            schema.UseAssetBundleCache = false;
            schema.ForceUniqueProvider = false;
            SetProvider(schema, typeof(EncryptedAssetBundleProvider));
            var fields = new SerializedObject(group.GetSchema<EncryptedBundleGroupSchema>());
            fields.FindProperty("_keyId").stringValue = keyId;
            fields.FindProperty("_deliveryMode").enumValueIndex = embedded ? 1 : 0;
            fields.ApplyModifiedPropertiesWithoutUndo();
            return group;
        }

        private static void SetProvider(BundledAssetGroupSchema schema, Type type)
        {
            // A fresh SerializedType also clears the type cached by the official builder during the prior pack.
            var provider = new UnityEngine.ResourceManagement.Util.SerializedType { Value = type };
#if UNITY_6000_3_OR_NEWER
            schema.AssetBundleProviderType = provider;
#else
            var setter = typeof(BundledAssetGroupSchema).GetProperty(nameof(schema.AssetBundleProviderType)).GetSetMethod(true);
            Require(setter != null, "The pinned Addressables 1.25 provider setter is unavailable.");
            setter.Invoke(schema, new object[] { provider });
#endif
            Require(schema.AssetBundleProviderType.Value == type, "The official bundle provider did not change.");
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

        private static void PublicDependencies(AddressableAssetGroup group, params string[] guids)
        {
            var schema = new SerializedObject(group.GetSchema<EncryptedBundleGroupSchema>());
            var values = schema.FindProperty("_publicDependencyGuids");
            values.arraySize = guids.Length;
            for (int i = 0; i < guids.Length; i++) values.GetArrayElementAtIndex(i).stringValue = guids[i];
            schema.ApplyModifiedPropertiesWithoutUndo();
        }

        private static AddressableAssetEntry Prefab(AddressableAssetSettings settings, AddressableAssetGroup group, string name, Sprite sprite, string address)
        {
            var instance = new GameObject(name);
            try
            {
                instance.AddComponent<SpriteRenderer>().sprite = sprite;
                string path = Root + "/" + name + ".prefab";
                Require(PrefabUtility.SaveAsPrefabAsset(instance, path), "Prefab was not created.");
                var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group);
                entry.address = address;
                return entry;
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
