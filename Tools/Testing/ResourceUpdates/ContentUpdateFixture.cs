using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.U2D;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
#if YL_CONTENT_UPDATE_ENCRYPTION
using YLFramework.Resource.Encryption;
using YLFramework.Resource.Encryption.Editor;
#endif
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Testing
{
    public static class ContentUpdateFixture
    {
        private const string Root = "Assets/ContentUpdate";
        private const string TrustPath = Root + "/Resources/YLContentUpdateTrust.json";
        private const string ScenePath = Root + "/Boot.unity";
        private const string ApplicationId = "yl-content-update";
        private const string Channel = "acceptance";
        private const string Platform = "StandaloneWindows64";
        private const string Compatibility = "fixture-1";
        private const string KeyId = "release";
        private const string ManifestName = "yl-content-manifest.json";
        private const string SignatureName = "yl-content-manifest.sig";
        private const string StateName = "addressables_content_state.bin";
        private const string RemoteBuildVariable = "ContentUpdateRemoteBuild";
        private const string RemoteLoadVariable = "ContentUpdateRemoteLoad";
        private const string RetainedBuildVariable = "ContentUpdateRetainedBuild";
        private const string RetainedLoadVariable = "ContentUpdateRetainedLoad";
        private const string RetainedAddress = "acceptance/ordinary-remote";
        private const string RetainedGroup = "RetainedOrdinary";
        private const string OptionalGroup = "OptionalChapter";
        private const string OptionalDependencyGroup = "OptionalDependency";
        private const string AtlasGroup = "LocalAtlas";
        private const string OrdinaryPath = Root + "/Ordinary.txt";
        private const string LayoutName = "buildlayout.json";
        private static readonly UTF8Encoding Utf8 = new(false, true);

        public static void Build()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            const string flag = "-ylContentUpdateRequest";
            int index = Array.IndexOf(arguments, flag);
            Require(Application.isBatchMode && index >= 0 && index == Array.LastIndexOf(arguments, flag) &&
                index + 1 < arguments.Length, "Expected one owned batch fixture request.");
            string requestPath = Path.GetFullPath(arguments[index + 1]);
            var request = ReadJson(requestPath);
            string resultPath = Path.GetFullPath(Required(request, "resultPath"));
            string phase = Required(request, "phase");
            var host = Path.GetDirectoryName(Application.dataPath);
            Require(phase is "A" or "B" or "C" or "Replay" && Path.GetDirectoryName(requestPath) == Path.GetDirectoryName(resultPath) &&
                !File.Exists(resultPath) && !Directory.Exists(resultPath), "Invalid owned fixture receipt path.");
            string artifacts = Path.GetFullPath(Required(request, "artifactRoot"));
            string install = Path.GetFullPath(Required(request, "installRoot"));
            Require(artifacts == Path.Combine(host, phase) && install == Path.Combine(host, "P") &&
                Directory.Exists(artifacts) && !Directory.EnumerateFileSystemEntries(artifacts).Any(),
                "Expected a fresh phase directory in the owned Unity fixture host.");
            var baseUri = new Uri(Required(request, "baseUrl"));
            Require(baseUri.Scheme == "http" && baseUri.Host == "127.0.0.1" &&
                baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal), "Expected the controller's loopback origin.");
            string backend = Required(request, "backend");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var receipt = new JObject
            {
                ["status"] = "Failed",
                ["phase"] = phase,
                ["pid"] = process.Id,
                ["unity"] = Application.unityVersion,
                ["backend"] = backend,
                ["inputHash"] = Required(request, "inputHash"),
                ["requestSha256"] = HashFile(requestPath),
                ["packageLockPath"] = Path.Combine(host, "Packages", "packages-lock.json"),
                ["contentBuildCalls"] = 0,
                ["playerBuildCalls"] = 0
            };
            try
            {
                bool encrypted = (bool)request["encrypted"];
#if YL_CONTENT_UPDATE_ENCRYPTION
                Require(encrypted, "Compiled encrypted fixture received an ordinary build request.");
#else
                Require(!encrypted, "Encrypted fixture package is missing.");
#endif
                var package = PackageInfo.FindForAssembly(typeof(Addressables).Assembly);
                Require(package != null && package.version == Required(request, "addressables") &&
                    package.source == UnityEditor.PackageManager.PackageSource.Registry &&
                    EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64,
                    "Fixture requires the requested registry Addressables and Win64 target.");
                receipt["addressables"] = package.version;
                FrozenPlayerBuildEvidence.CapturePackageInputs(receipt, host, HashFile);
                if (phase == "Replay")
                {
                    FrozenPlayerBuildEvidence.Configure(backend);
                    BuildReplay(request, receipt, artifacts, install, backend);
                }
                else
                {
                    var publicKey = ReadRsaParameters(OwnedKey(request, host, "signingPublicKeyPath"));
                    using var signer = OpenRsa(OwnedKey(request, host, "signingPrivateKeyPath"));
                    if (phase == "A")
                    {
                        FrozenPlayerBuildEvidence.Configure(backend);
                        BuildBaseline(request, receipt, artifacts, install, baseUri, publicKey, signer);
                    }
                    else
                    {
                        string baselinePath = Path.GetFullPath(Required(request, "baselineReceiptPath"));
                        Require(baselinePath == Path.Combine(Path.GetDirectoryName(resultPath), "A-build.json"),
                            "Expected the controller's sealed A receipt.");
                        if (phase == "B")
                        {
                            using var unknown = OpenRsa(OwnedKey(request, host, "unknownPrivateKeyPath"));
                            BuildUpdate(request, receipt, ReadJson(baselinePath), null, artifacts, install, baseUri,
                                publicKey, signer, unknown);
                        }
                        else
                        {
                            var prior = ReadJson(Path.Combine(Path.GetDirectoryName(resultPath), "B-build.json"));
                            BuildUpdate(request, receipt, ReadJson(baselinePath), prior, artifacts, install, baseUri,
                                publicKey, signer, null);
                        }
                    }
                }

                receipt["status"] = "Built";
                WriteNew(resultPath, Bytes(receipt));
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                receipt["error"] = error.ToString();
                WriteNew(resultPath, Bytes(receipt));
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void BuildReplay(JObject request, JObject receipt, string artifacts, string install, string backend)
        {
            Require(!Directory.Exists(install), "The comparison Player requires a fresh install.");
            Require(HashFile(TrustPath) == Required(request, "trustSha256"), "The embedded public trust input differs.");
            string aa = "Assets/StreamingAssets/aa";
            string catalog = Required(request, "catalogFile");
            Require(Path.GetFileName(catalog) == catalog, "The replay Catalog must be a file under aa.");
            Require(HashFile(Path.Combine(aa, catalog)) == Required(request, "catalogSha256"),
                "The pinned A Catalog input differs.");
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Require(settings && settings.BuildAddressablesWithPlayerBuild == AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer,
                "The replay install must consume the sealed A content.");
            PlayerSettings.productName = "YLResourceUpdates-" + Path.GetFileName(Path.GetDirectoryName(Application.dataPath));
            string player = Path.Combine(install, "P.exe");
            receipt["playerBuildCalls"] = 1;
            var report = BuildPipeline.BuildPlayer(PlayerOptions(player));
            Require(report != null && report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0,
                "The replay Win64 Release Player build failed.");
            receipt["playerExe"] = player;
            receipt["trustSha256"] = HashFile(TrustPath);
            receipt["catalogSha256"] = HashFile(Path.Combine(install, "P_Data", "StreamingAssets", "aa", catalog));
            receipt["playerBuildGuid"] = report.summary.guid.ToString();
            receipt["buildReportPath"] = ArchivePlayerReport(artifacts);
#if YL_CONTENT_UPDATE_ENCRYPTION
            const string extension = "YLFramework.Resource.Encryption";
#else
            const string extension = null;
#endif
            FrozenPlayerBuildEvidence.Capture(receipt, backend, player, HashFile, extension, typeof(ContentUpdateMaterial));
        }

        private static void BuildBaseline(JObject request, JObject receipt, string root, string install, Uri baseUri,
            RSAParameters publicKey, RSA signer)
        {
            Require(!Directory.Exists(Root) && !Directory.Exists(install) &&
                AddressableAssetSettingsDefaultObject.GetSettings(false) == null &&
                (string)request["newAddress"] == "", "A requires a fresh fixture with no B input.");
            PlayerSettings.companyName = "YLFramework Acceptance";
            PlayerSettings.productName = "YLResourceUpdates-" + Path.GetFileName(Path.GetDirectoryName(Application.dataPath));
            AssetDatabase.CreateFolder("Assets", "ContentUpdate");
            AssetDatabase.CreateFolder(Root, "Resources");
            // Addressables 1.25 indexes Resources before the trust payload can bind the actual startup settings.
            WriteNew(TrustPath, Utf8.GetBytes("{}"));
            AssetDatabase.ImportAsset(TrustPath, ImportAssetOptions.ForceSynchronousImport);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save the fixture's startup scene.");
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            Require(settings, "Could not create the official default Addressables config and groups.");
            settings.BuildRemoteCatalog = true;
            settings.DisableCatalogUpdateOnStartup = true;
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            settings.OverridePlayerVersion = "yl-content-updates-v1";
            int packed = settings.DataBuilders.FindIndex(builder => builder is BuildScriptPackedMode);
            Require(packed >= 0, "Missing the official Packed Player builder.");
            settings.ActivePlayerDataBuilderIndex = packed;
            var ordinary = AddGroup(settings, "Ordinary");
            var material = AddGroup(settings, "Material");
            var dependency = AddGroup(settings, "Dependency");
            AddText(settings, ordinary, "Ordinary", Required(request, "ordinaryDependencyText"));
            var oldDependency = AddText(settings, dependency, "A-Dependency", Required(request, "oldDependencyText"));
            AddMaterial(settings, material, "A-Material", Required(request, "oldAddress"),
                Required(request, "oldAssetText"), oldDependency);
            receipt["oldAddress"] = Required(request, "oldAddress");
            receipt["oldGuid"] = AssetDatabase.AssetPathToGUID(Root + "/A-Material.asset");
            receipt["oldDependencyGuid"] = AssetDatabase.AssetPathToGUID(Root + "/A-Dependency.txt");

            string privateRoot = Path.Combine(root, "Private");
            ConfigurePaths(settings, privateRoot, baseUri, isBaseline: true);
#if YL_CONTENT_UPDATE_ENCRYPTION
            var retainedGroup = AddGroup(settings, RetainedGroup);
            var retainedText = AddText(settings, retainedGroup, "RetainedOrdinary", Guid.NewGuid().ToString("N"), RetainedAddress);
            receipt["retainedAuthoredLoadPrefix"] = ConfigureRetainedPaths(settings, retainedGroup, privateRoot, baseUri, "A");
            receipt["retainedAddress"] = RetainedAddress;
            receipt["retainedAssetSha256"] = Hash(Utf8.GetBytes(retainedText.text));
            receipt["retainedGuid"] = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(retainedText));
#else
            AddSceneAndAtlas(settings, receipt);
#endif
            byte[] aPayload = null;
            byte[] aSignature = null;
#if YL_CONTENT_UPDATE_ENCRYPTION
            ResourceContentArtifact retained = null;
#endif
            string player = Path.Combine(install, "P.exe");
            string deployed = Path.Combine(install, "P_Data", "StreamingAssets", "aa");
            string backend = Required(request, "backend");
            string archivePath;
            string layout;
            string settingsHash;
            BuildReport playerReport;
            string extensionAssembly = null;
            (byte[] Payload, byte[] Signature) SignBaseline(string sha, string catalog, string description,
                IReadOnlyList<ResourceContentArtifact> artifacts)
            {
                Require(artifacts.Any(artifact => artifact.Dependencies.Count >= 2) &&
                    artifacts.Any(artifact => artifact.Kind == ResourceContentArtifactKind.Bundle),
                    "A must contain a real cross-Bundle dependency and an ordinary Bundle.");
                var remote = artifacts.Where(artifact => !IsInstalled(artifact)).ToArray();
#if YL_CONTENT_UPDATE_ENCRYPTION
                Require(remote.Length == 1, "Encrypted A must have exactly one optional remote Bundle.");
                retained = remote[0];
                Require(retained.Kind == ResourceContentArtifactKind.Bundle && retained.Dependencies.Count == 0 &&
                    retained.OriginReleaseId == "A" && retained.Route == retained.Source &&
                    retained.Source.StartsWith(Required(receipt, "retainedAuthoredLoadPrefix"), StringComparison.Ordinal),
                    "Encrypted A must have exactly one independent optional ordinary remote Bundle.");
                RecordRetainedArtifact(receipt, retained);
#else
                Require(remote.Length == 0, "Ordinary A must have only installed artifacts.");
#endif
                aPayload = Manifest("A", 10, DateTimeOffset.UtcNow.AddDays(30), Channel, sha, catalog, description,
                    artifacts, remote.Select(artifact => artifact.Route));
                aSignature = ResourceContentManifestWriter.Sign(aPayload, KeyId, signer);
                var manifest = ResourceContentManifest.Verify(aPayload, aSignature, Trust(baseUri, sha, publicKey));
                Require(manifest.StartupRoutes.All(route => IsInstalled(manifest.Artifacts[route])),
                    "A startup must be completely independent of remote content.");
                WriteTrust(baseUri, sha, publicKey);
                return (aPayload, aSignature);
            }

#if YL_CONTENT_UPDATE_ENCRYPTION
            SetProtected(settings, material, privateRoot, "K1", AssetDatabase.AssetPathToGUID(OrdinaryPath));
            SetProtected(settings, dependency, privateRoot, "K1");
            AssetDatabase.SaveAssets();
            string release = Path.Combine(root, "A");
            using var keys = new AuthorKey("K1", ReadKey(request, "k1Path"));
            receipt["contentBuildCalls"] = 1;
            var build = EncryptedBundleExporter.BuildVersionedBaselineCore(settings, new[] { material, dependency },
                PlayerOptions(Path.Combine(release, "Player", "P.exe")), ApplicationId, privateRoot, release, keys,
                allowLoopback: true, configurePlayer: audited =>
                {
                    var signed = SignBaseline(audited.SettingsSha256, audited.CatalogRoute, audited.DescriptionRoute,
                        audited.CreateArtifacts(new Uri(baseUri, "A/").AbsoluteUri));
                    receipt["playerBuildCalls"] = 1;
                    return signed;
                });
            playerReport = build.PlayerReport;
            CopyTree(Path.Combine(release, "Player"), install);
            archivePath = build.ContentStatePath;
            layout = build.BuildLayoutPath;
            settingsHash = build.SettingsSha256;
            receipt["baselineReleaseRoot"] = release;
            receipt["baselineDescriptionPath"] = build.DescriptionPath;
            var baselineFiles = new JArray();
            receipt["files"] = baselineFiles;
            PublishArtifact(baselineFiles, baseUri, build.UploadRoot, "A/", retained);
            extensionAssembly = "YLFramework.Resource.Encryption";
#else
            AssetDatabase.SaveAssets();
            string archive = CreateDirectory(Path.Combine(root, "ContentState"));
            receipt["contentBuildCalls"] = 1;
            var baseline = WithLayout(() => ResourceContentUpdateAuthoring.BuildBaseline(settings,
                Path.Combine(archive, StateName)));
            string builtAa = Path.GetFullPath(Addressables.BuildPath);
            var routes = ResourceContentUpdateAuthoring.InspectCatalog(baseline.CatalogPath, Addressables.RuntimePath);
            var localFiles = baseline.Build.Result.AssetBundleBuildResults.ToDictionary(
                file => Path.GetFileName(file.FilePath), file => RelativeBuiltFile(builtAa, file.FilePath),
                StringComparer.Ordinal);
            var bundlePaths = routes.Keys.ToDictionary(route => route, route => localFiles[Path.GetFileName(route)],
                StringComparer.Ordinal);
            var artifacts = ResourceContentUpdateAuthoring.DescribeInstalledArtifacts(baseline, Addressables.RuntimePath,
                builtAa, "A", Path.GetFileName(baseline.CatalogPath), bundlePaths);
#if UNITY_6000_3_OR_NEWER
            var stripped = BundleForGroup(artifacts, settings.FindGroup(AtlasGroup));
            var data = ResourceContentUpdateAuthoring.ReadCatalogData(baseline.CatalogPath);
            var createLocator = typeof(ContentCatalogData).GetMethod("CreateCustomLocator", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(string) }, null);
            Require(createLocator != null, "Missing the pinned Addressables binary Catalog locator factory.");
            var locator = (IResourceLocator)createLocator.Invoke(data, new object[] { "AddressablesMainContentCatalog", null });
            Require(locator.Keys.SelectMany(key => locator.Locate(key, typeof(IAssetBundleResource), out var locations)
                    ? locations : Array.Empty<UnityEngine.ResourceManagement.ResourceLocations.IResourceLocation>())
                .Any(location => location.Data == null && Path.GetFileName(location.InternalId) == Path.GetFileName(stripped.Route)),
                "The official local Atlas Bundle did not strip download options from the Catalog.");
            receipt["strippedBundleRoute"] = stripped.Route;
#endif
            SignBaseline(baseline.StartupSettingsSha256, baseline.CatalogRoute, null, artifacts);
            WriteNew(Path.Combine(builtAa, ManifestName), aPayload);
            WriteNew(Path.Combine(builtAa, SignatureName), aSignature);
            receipt["playerBuildCalls"] = 1;
            playerReport = BuildPipeline.BuildPlayer(PlayerOptions(player));
            archivePath = baseline.ContentStatePath;
            settingsHash = baseline.StartupSettingsSha256;
            receipt["baselineArchiveReceipt"] = baseline.ReceiptPath;
            layout = ArchiveLayout(root);
#endif
            Require(playerReport != null && playerReport.summary.result == BuildResult.Succeeded &&
                playerReport.summary.totalErrors == 0, "The single real Win64 Release Player build failed.");
            Require(HashFile(Path.Combine(deployed, "settings.json")) == settingsHash &&
                File.ReadAllBytes(Path.Combine(deployed, ManifestName)).SequenceEqual(aPayload) &&
                File.ReadAllBytes(Path.Combine(deployed, SignatureName)).SequenceEqual(aSignature),
                "The installed A settings and signed files differ from the sealed inputs.");
            var installed = ResourceContentManifest.Verify(aPayload, aSignature, Trust(baseUri, settingsHash, publicKey));
            foreach (var artifact in installed.Artifacts.Values.Where(IsInstalled))
            {
                Require(HashFile(Path.Combine(deployed, artifact.Source.Substring("install:".Length))) == artifact.Sha256,
                    "A does not contain the exact signed offline dependencies.");
            }

            receipt["playerExe"] = player;
            receipt["baselineDigest"] = installed.Digest;
            receipt["baselineAnnouncementPath"] = WriteNew(Path.Combine(root, "baseline-announcement.json"),
                Envelope(aPayload, aSignature));
            receipt["settingsSha256"] = settingsHash;
            receipt["contentStatePath"] = archivePath;
            receipt["contentStateSha256"] = HashFile(archivePath);
            receipt["buildLayoutPath"] = layout;
            receipt["playerBuildGuid"] = playerReport.summary.guid.ToString();
            receipt["buildReportPath"] = ArchivePlayerReport(root);
            FrozenPlayerBuildEvidence.Capture(receipt, backend, player, HashFile, extensionAssembly,
                typeof(ContentUpdateMaterial));
        }

        private static void BuildUpdate(JObject request, JObject receipt, JObject baseline, JObject prior, string root,
            string install, Uri baseUri, RSAParameters publicKey, RSA signer, RSA unknown)
        {
            bool isC = prior != null;
            string releaseId = isC ? "C" : "B";
            Require((string)baseline["phase"] == "A" && (string)baseline["status"] == "Built" &&
                (string)baseline["inputHash"] == (string)receipt["inputHash"] &&
                (string)baseline["packageLockSha256"] == (string)receipt["packageLockSha256"] &&
                (int)baseline["playerBuildCalls"] == 1 &&
                Required(baseline, "playerExe") == Path.Combine(install, "P.exe"), "Content updates require the matching sealed A.");
            string oldState = Required(baseline, "contentStatePath");
            string stateSha = Required(baseline, "contentStateSha256");
            Require(HashFile(oldState) == stateSha, "A official content state has changed.");
            receipt["contentStateSha256"] = stateSha;
            receipt["stripping"] = Required(baseline, "stripping");
            string settingsHash = Required(baseline, "settingsSha256");
            var trust = Trust(baseUri, settingsHash, publicKey);
            var oldAnnouncement = File.ReadAllBytes(Required(baseline, "baselineAnnouncementPath"));
            var originalEnvelope = JObject.Parse(Utf8.GetString(oldAnnouncement));
            var original = ResourceContentManifest.Verify(Convert.FromBase64String((string)originalEnvelope["payload"]),
                Convert.FromBase64String((string)originalEnvelope["signature"]), trust);
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            var material = settings.FindGroup("Material");
            var dependency = settings.FindGroup("Dependency");
            Require(AssetDatabase.LoadAssetAtPath<TextAsset>(OrdinaryPath).text == Required(request, "ordinaryDependencyText"),
                "The common ordinary dependency must be the sealed A input.");
            string address = Required(request, "newAddress");
            string optionalAddress = Required(request, "optionalAddress");
            Require(address != Required(request, "oldAddress") && address != optionalAddress &&
                optionalAddress != Required(request, "oldAddress"), "The three chapter addresses must be distinct.");
            var optional = settings.FindGroup(OptionalGroup);
            var optionalDependency = settings.FindGroup(OptionalDependencyGroup);
            if (isC)
            {
                Require((string)prior["phase"] == "B" && (string)prior["status"] == "Built" &&
                    (string)prior["inputHash"] == (string)receipt["inputHash"] &&
                    (string)prior["packageLockSha256"] == (string)receipt["packageLockSha256"] &&
                    (string)prior["contentStateSha256"] == stateSha && (int)prior["contentBuildCalls"] == 1 &&
                    (int)prior["playerBuildCalls"] == 0 && (string)prior["newAddress"] == address &&
                    (string)prior["optionalAddress"] == optionalAddress && optional && optionalDependency,
                    "C requires the matching content-only B receipt and authoring inputs.");
            }
            else
            {
                Require(!optional && !optionalDependency &&
                    !AssetDatabase.LoadAssetAtPath<ContentUpdateMaterial>(Root + "/B-Material.asset"),
                    "B's new content should never have existed in A.");
                optional = AddGroup(settings, OptionalGroup);
                optionalDependency = AddGroup(settings, OptionalDependencyGroup);
            }

            var replacement = AddText(settings, dependency, "A-Dependency", Required(request, "replacementDependencyText"));
            UpdateMaterial(settings, "A-Material", Required(request, "oldAddress"), Required(request, "replacementAssetText"), replacement);
            var newDependency = AddText(settings, dependency, "B-Dependency", Required(request, "newDependencyText"));
            var optionalText = AddText(settings, optionalDependency, "Optional-Dependency", Required(request, "optionalDependencyText"));
            string newValue = Required(request, "newAssetText");
            string optionalValue = Required(request, "optionalAssetText");
            if (isC)
            {
                UpdateMaterial(settings, "B-Material", address, newValue, newDependency);
                UpdateMaterial(settings, "Optional-Material", optionalAddress, optionalValue, optionalText);
            }
            else
            {
                AddMaterial(settings, material, "B-Material", address, newValue, newDependency);
                AddMaterial(settings, optional, "Optional-Material", optionalAddress, optionalValue, optionalText);
            }
            receipt["newAddress"] = address;
            receipt["newGuid"] = AssetDatabase.AssetPathToGUID(Root + "/B-Material.asset");
            receipt["newDependencyGuid"] = AssetDatabase.AssetPathToGUID(Root + "/B-Dependency.txt");
            receipt["optionalAddress"] = optionalAddress;
            receipt["optionalGuid"] = AssetDatabase.AssetPathToGUID(Root + "/Optional-Material.asset");
            receipt["optionalDependencyGuid"] = AssetDatabase.AssetPathToGUID(Root + "/Optional-Dependency.txt");
            receipt["optionalSourceAssetSha256"] = Hash(Utf8.GetBytes(optionalValue));
            receipt["optionalSourceDependencySha256"] = Hash(Utf8.GetBytes(optionalText.text));
            string privateRoot = Path.Combine(root, "Private");
            ConfigurePaths(settings, privateRoot, baseUri, isBaseline: false);
            string upload;
            string uploadPrefix;
            string catalog;
            string description = null;
            IReadOnlyList<ResourceContentArtifact> artifacts;
#if YL_CONTENT_UPDATE_ENCRYPTION
            string ordinaryGuid = AssetDatabase.AssetPathToGUID(OrdinaryPath);
            SetProtected(settings, material, privateRoot, "K2", ordinaryGuid);
            SetProtected(settings, dependency, privateRoot, "K2");
            SetProtected(settings, optional, privateRoot, "K2", ordinaryGuid);
            SetProtected(settings, optionalDependency, privateRoot, "K2");
            receipt["retainedAuthoredLoadPrefix"] = ConfigureRetainedPaths(settings, settings.FindGroup(RetainedGroup), privateRoot,
                baseUri, releaseId);
            AssetDatabase.SaveAssets();
            string release = Path.Combine(root, releaseId);
            using var keys = new AuthorKey("K2", ReadKey(request, "k2Path"));
            receipt["contentBuildCalls"] = 1;
            var build = EncryptedBundleExporter.BuildVersionedUpdateCore(settings,
                new[] { material, dependency, optional, optionalDependency }, Required(baseline, "baselineReleaseRoot"),
                privateRoot, release, keys, allowLoopback: true, baselineTrust: trust);
            upload = build.UploadRoot;
            uploadPrefix = releaseId + "/";
            catalog = build.CatalogRoute;
            description = build.DescriptionRoute;
            artifacts = build.CreateArtifacts(new Uri(baseUri, uploadPrefix).AbsoluteUri);
            receipt["buildLayoutPath"] = build.BuildLayoutPath;
#else
            AssetDatabase.SaveAssets();
            string officialRoot = CreateDirectory(Path.Combine(privateRoot, "Remote"));
            var archive = ResourceContentUpdateAuthoring.ReadBaseline(Required(baseline, "baselineArchiveReceipt"));
            receipt["contentBuildCalls"] = 1;
            var update = WithLayout(() => ResourceContentUpdateAuthoring.BuildContentUpdate(settings, archive, officialRoot));
            upload = CreateDirectory(Path.Combine(root, "Upload"));
            uploadPrefix = "";
            catalog = archive.CatalogRoute;
            artifacts = ResourceContentUpdateAuthoring.StageUpdateArtifacts(update, original, Addressables.RuntimePath,
                releaseId, upload, baseUri, new[] { typeof(ContentUpdateMaterial) });
            receipt["buildLayoutPath"] = ArchiveLayout(root);
#endif
            Require(HashFile(oldState) == stateSha && catalog == original.CatalogRoute,
                "The update changed its frozen A content state or startup Catalog route.");

            var optionalRoot = BundleForGroup(artifacts, optional);
            var optionalBundle = BundleForGroup(artifacts, optionalDependency);
            var realDependency = BundleForGroup(artifacts, dependency);
            var requiredMaterial = BundleForGroup(artifacts, material);
            var optionalRoutes = new[] { optionalRoot.Route, optionalBundle.Route };
            var expectedKind = description == null ? ResourceContentArtifactKind.Bundle : ResourceContentArtifactKind.Ciphertext;
            Require(optionalRoot.Dependencies.Contains(optionalBundle.Route) && requiredMaterial.Dependencies.Contains(realDependency.Route) &&
                new[] { optionalRoot, optionalBundle, realDependency, requiredMaterial }.All(artifact =>
                    artifact.Kind == expectedKind && artifact.OriginReleaseId == releaseId && !IsInstalled(artifact)),
                "The required and optional chapters must use distinct, real remote Bundle pairs.");
            receipt["optionalRoutes"] = new JArray(optionalRoot.Route);
            receipt["optionalClosure"] = new JArray(optionalRoutes);

            ResourceContentArtifact retained = null;
#if YL_CONTENT_UPDATE_ENCRYPTION
            var earlier = original.Artifacts.Values.Single(artifact => !IsInstalled(artifact));
            Require(baseline["retainedRoutes"].Values<string>().Single() == earlier.Route,
                "The retained ordinary Bundle route differs from the signed A manifest.");
            retained = artifacts.Single(artifact => artifact.Route == earlier.Route);
            string retainedPrefix = new Uri(baseUri, "A/").AbsoluteUri;
            Require(retained.Kind == ResourceContentArtifactKind.Bundle && retained.Dependencies.Count == 0 &&
                retained.OriginReleaseId == "A" && retained.Source == earlier.Source && retained.Length == earlier.Length &&
                retained.Sha256 == earlier.Sha256 && retained.Source.StartsWith(retainedPrefix, StringComparison.Ordinal) &&
                !File.Exists(Path.Combine(upload, retained.Source.Substring(retainedPrefix.Length))),
                "The unchanged ordinary remote Bundle must preserve its signed A source without a new upload.");
            RecordRetainedArtifact(receipt, retained);
            receipt["retainedAddress"] = baseline["retainedAddress"].DeepClone();
            receipt["retainedAssetSha256"] = baseline["retainedAssetSha256"].DeepClone();
            receipt["retainedGuid"] = baseline["retainedGuid"].DeepClone();
#endif
            var excludedRoutes = retained == null ? optionalRoutes : optionalRoutes.Append(retained.Route).ToArray();
            WriteVariants(receipt, root, baseUri, upload, uploadPrefix, catalog, description, settingsHash, releaseId,
                artifacts, excludedRoutes, retained, realDependency, oldAnnouncement, baseline, signer, unknown, trust);
            if (isC)
            {
                Require((string)prior["catalogArtifactSha256"] != (string)receipt["catalogArtifactSha256"] &&
                    (string)prior["dependencyArtifactSha256"] != realDependency.Sha256 &&
                    (string)prior["optionalSourceAssetSha256"] != (string)receipt["optionalSourceAssetSha256"] &&
                    (string)prior["optionalSourceDependencySha256"] != (string)receipt["optionalSourceDependencySha256"],
                    "C must produce real replacement contents and preserve a B-only downloaded object for recovery.");
            }
        }

        private static void WriteVariants(JObject receipt, string root, Uri baseUri, string upload, string uploadPrefix,
            string catalog, string description, string settingsHash, string releaseId,
            IReadOnlyList<ResourceContentArtifact> artifacts, IReadOnlyList<string> excludedRoutes,
            ResourceContentArtifact retained, ResourceContentArtifact realDependency, byte[] baselineAnnouncement,
            JObject baseline, RSA signer, RSA unknown, ResourceContentManifestTrust trust)
        {
            var files = new JArray();
            var announcements = new JObject();
            receipt["files"] = files;
            receipt["announcements"] = announcements;
            string RemotePath(ResourceContentArtifact artifact)
            {
                Require(artifact.Source.StartsWith(baseUri.AbsoluteUri + uploadPrefix, StringComparison.Ordinal),
                    "An update artifact escaped the upload prefix.");
                return Path.Combine(upload, artifact.Source.Substring(baseUri.AbsoluteUri.Length + uploadPrefix.Length));
            }

            var remote = artifacts.Where(artifact => !IsInstalled(artifact) && artifact != retained);
            foreach (var artifact in remote.GroupBy(artifact => artifact.Source, StringComparer.Ordinal).Select(group => group.First()))
            {
                PublishArtifact(files, baseUri, upload, uploadPrefix, artifact);
            }

            receipt["transferRoute"] = realDependency.Source.Substring(baseUri.AbsoluteUri.Length);
            receipt["dependencyArtifactSha256"] = realDependency.Sha256;
            receipt["catalogArtifactSha256"] = artifacts.Single(artifact => artifact.Route == catalog).Sha256;
            var expiration = DateTimeOffset.UtcNow.AddDays(30);
            Directory.CreateDirectory(Path.Combine(root, "Announcements"));
            void Announcement(string name, byte[] payload, byte[] signature)
            {
                announcements[name] = WriteNew(Path.Combine(root, "Announcements", name + ".json"),
                    Envelope(payload, signature));
            }

            byte[] Payload(long sequence, IEnumerable<ResourceContentArtifact> inventory, DateTimeOffset expires,
                string channel = Channel) => Manifest(releaseId, sequence, expires, channel, settingsHash,
                    catalog, description, inventory, excludedRoutes);
            void Sign(string name, byte[] payload) =>
                Announcement(name, payload, ResourceContentManifestWriter.Sign(payload, KeyId, signer));
            ResourceContentArtifact Variant(string folder, ResourceContentArtifact artifact, byte[] bytes)
            {
                string relative = folder + "/" + Path.GetFileName(RemotePath(artifact));
                string file = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                files.Add(new JObject { ["route"] = relative, ["path"] = WriteNew(file, bytes) });
                return new ResourceContentArtifact(artifact.Route, artifact.OriginReleaseId,
                    new Uri(baseUri, relative).AbsoluteUri, artifact.Kind, bytes.LongLength, Hash(bytes), artifact.Dependencies);
            }
            IEnumerable<ResourceContentArtifact> Replace(params ResourceContentArtifact[] variants) =>
                artifacts.Select(artifact => variants.FirstOrDefault(variant => variant.Route == artifact.Route) ?? artifact);

            long sequence = releaseId == "B" ? 11 : 14;
            var valid = Payload(sequence, artifacts, expiration);
            var signature = ResourceContentManifestWriter.Sign(valid, KeyId, signer);
            var manifest = ResourceContentManifest.Verify(valid, signature, trust);
            Require(excludedRoutes.All(route => !manifest.StartupRoutes.Contains(route)) &&
                manifest.StartupRoutes.Contains(realDependency.Route),
                "The signed startup closure must include the required dependency and exclude the optional chapters.");
            Announcement("valid", valid, signature);
            var altered = File.ReadAllBytes(RemotePath(realDependency));
            altered[altered.Length - 1] ^= 1;
            if (releaseId == "C")
            {
                var orphan = Variant("orphan-c", realDependency, altered);
                receipt["orphanArtifactSha256"] = orphan.Sha256;
                receipt["orphanTransferRoute"] = orphan.Source.Substring(baseUri.AbsoluteUri.Length);
                Sign("orphan", Payload(sequence, Replace(orphan), expiration));
                return;
            }

            Sign("high", Payload(12, artifacts, expiration));
            Sign("successor", Payload(13, artifacts, expiration));
            Sign("conflict", Payload(11, artifacts, expiration.AddMinutes(1)));
            Sign("wrongChannel", Payload(11, artifacts, expiration, "other"));
            Sign("expired", Payload(11, artifacts, DateTimeOffset.UtcNow.AddDays(-1)));
            announcements["stale"] = WriteNew(Path.Combine(root, "Announcements", "stale.json"), baselineAnnouncement);
            Announcement("unknown", valid, ResourceContentManifestWriter.Sign(valid, "unknown", unknown));

            var alteredDependency = Variant("tampered", realDependency, altered);
            Announcement("tampered", Payload(11, Replace(alteredDependency), expiration),
                ResourceContentManifestWriter.Sign(valid, KeyId, signer));
            if (description != null)
            {
                receipt["cipherArtifactSha256"] = realDependency.Sha256;
                var binding = artifacts.Single(artifact => artifact.Route == description);
                byte[] wrongCatalog = File.ReadAllBytes(Required(baseline, "baselineDescriptionPath"));
                Sign("wrongCatalog", Payload(11, Replace(Variant("wrong-catalog", binding, wrongCatalog)), expiration));
                var badCipher = Variant("wrong-cipher", realDependency, altered);
                var badDescription = ReadJson(RemotePath(binding));
                var record = ((JArray)badDescription["bundles"]).OfType<JObject>()
                    .Single(row => (string)row["cipherSha256"] == realDependency.Sha256);
                record["cipherSha256"] = badCipher.Sha256;
                Sign("wrongCipher", Payload(11,
                    Replace(badCipher, Variant("wrong-cipher", binding, Bytes(badDescription))), expiration));
            }
        }

        private static bool IsInstalled(ResourceContentArtifact artifact) =>
            artifact.Source.StartsWith("install:", StringComparison.Ordinal);

        private static ResourceContentArtifact BundleForGroup(IEnumerable<ResourceContentArtifact> artifacts, AddressableAssetGroup group)
        {
#if YL_CONTENT_UPDATE_ENCRYPTION
            string load = group.GetSchema<BundledAssetGroupSchema>().LoadPath.GetValue(group.Settings);
            string prefix = load.TrimEnd('/') + "/" + group.Guid + "/";
            return artifacts.Single(artifact => artifact.Kind == ResourceContentArtifactKind.Ciphertext &&
                artifact.Route.StartsWith(prefix, StringComparison.Ordinal));
#else
            string prefix = group.Name.ToLowerInvariant() + "_assets_";
            return artifacts.Single(artifact => artifact.Kind == ResourceContentArtifactKind.Bundle &&
                Path.GetFileName(artifact.Route).StartsWith(prefix, StringComparison.Ordinal));
#endif
        }

        private static void RecordRetainedArtifact(JObject receipt, ResourceContentArtifact artifact)
        {
            receipt["retainedRoutes"] = new JArray(artifact.Route);
            receipt["retainedSource"] = artifact.Source;
            receipt["retainedArtifactSha256"] = artifact.Sha256;
            receipt["retainedOriginReleaseId"] = artifact.OriginReleaseId;
        }

        private static void PublishArtifact(JArray files, Uri baseUri, string upload, string prefix, ResourceContentArtifact artifact)
        {
            string remote = baseUri.AbsoluteUri + prefix;
            Require(artifact.Source.StartsWith(remote, StringComparison.Ordinal), "A published artifact escaped its upload prefix.");
            string path = Path.Combine(upload, artifact.Source.Substring(remote.Length));
            Require(new FileInfo(path).Length == artifact.Length && HashFile(path) == artifact.Sha256,
                "Published bytes differ from the signed artifact.");
            files.Add(new JObject { ["route"] = artifact.Source.Substring(baseUri.AbsoluteUri.Length), ["path"] = path });
        }

        private static AddressableAssetGroup AddGroup(AddressableAssetSettings settings, string name)
        {
            var group = settings.CreateGroup(name, false, false, false, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            // 节点按职责拆到不同 Bundle，实际随机地址不参与 Windows 安装文件名。
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            schema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
            schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            return group;
        }

        private static TextAsset AddText(AddressableAssetSettings settings, AddressableAssetGroup group, string name,
            string text, string address = null)
        {
            string path = Root + "/" + name + ".txt";
            File.WriteAllText(path, text, Utf8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group).address = address ?? name;
            return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        }

        private static void AddMaterial(AddressableAssetSettings settings, AddressableAssetGroup group, string name,
            string address, string value, TextAsset dependency)
        {
            string path = Root + "/" + name + ".asset";
            var asset = ScriptableObject.CreateInstance<ContentUpdateMaterial>();
            asset.Value = value;
            asset.Dependency = dependency;
            asset.OrdinaryDependency = AssetDatabase.LoadAssetAtPath<TextAsset>(OrdinaryPath);
            Require(asset.Dependency && asset.OrdinaryDependency, "Could not author the real inter-Bundle dependency.");
            AssetDatabase.CreateAsset(asset, path);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group).address = address;
        }

        private static void UpdateMaterial(AddressableAssetSettings settings, string name, string address, string value,
            TextAsset dependency)
        {
            string path = Root + "/" + name + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<ContentUpdateMaterial>(path);
            Require(asset && dependency && AssetDatabase.GetAssetPath(asset.OrdinaryDependency) == OrdinaryPath &&
                settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(path))?.address == address,
                "The asset and its ordinary dependency must preserve their authored identities: " + name);
            asset.Value = value;
            asset.Dependency = dependency;
            EditorUtility.SetDirty(asset);
        }

#if !YL_CONTENT_UPDATE_ENCRYPTION
        private static void AddSceneAndAtlas(AddressableAssetSettings settings, JObject receipt)
        {
            const string sceneAddress = "acceptance/scene";
            const string sceneRootName = "ContentUpdateSceneRoot";
            const string sceneObjectName = "ContentUpdateSceneText";
            const string atlasAddress = "acceptance/atlas";
            const string spriteName = "ContentUpdateSprite";
            const int spriteSize = 8;
            string sceneText = Guid.NewGuid().ToString("N");
            string scenePath = Root + "/PackedScene.unity";
            var sceneGroup = AddGroup(settings, "LocalScene");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(sceneRootName);
            var marker = new GameObject(sceneObjectName, typeof(TextMesh));
            marker.transform.SetParent(root.transform, false);
            marker.GetComponent<TextMesh>().text = sceneText;
            Require(EditorSceneManager.SaveScene(scene, scenePath), "Could not save the packed fixture scene.");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(scenePath), sceneGroup).address = sceneAddress;

            var atlasGroup = AddGroup(settings, AtlasGroup);
#if UNITY_6000_3_OR_NEWER
            var schema = atlasGroup.GetSchema<BundledAssetGroupSchema>();
            schema.UseDefaultSchemaSettings = false;
            schema.UseUnityWebRequestForLocalBundles = false;
            schema.StripDownloadOptions = true;
#endif
            EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
            string spritePath = Root + "/" + spriteName + ".png";
            var pixels = new Texture2D(spriteSize, spriteSize, TextureFormat.RGBA32, false);
            try
            {
                pixels.SetPixels(Enumerable.Repeat(Color.magenta, spriteSize * spriteSize).ToArray());
                pixels.Apply();
                WriteNew(spritePath, pixels.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
            AssetDatabase.ImportAsset(spritePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            string atlasPath = Root + "/PackedAtlas.spriteatlasv2";
            var authoring = new SpriteAtlasAsset();
            try
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath);
                authoring.Add(new UnityEngine.Object[] { texture });
                SpriteAtlasAsset.Save(authoring, atlasPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(authoring); }
            AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceSynchronousImport);
            var atlasImporter = (SpriteAtlasImporter)AssetImporter.GetAtPath(atlasPath);
            atlasImporter.includeInBuild = false;
            var platform = atlasImporter.GetPlatformSettings("Standalone");
            platform.overridden = true;
            platform.format = TextureImporterFormat.RGBA32;
            platform.textureCompression = TextureImporterCompression.Uncompressed;
            platform.crunchedCompression = false;
            atlasImporter.SetPlatformSettings(platform);
            atlasImporter.SaveAndReimport();
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            Require(atlas && !atlas.IsIncludeInBuild() && atlas.spriteCount == 1,
                "The official Atlas importer must pack the fixture sprite for Addressables only.");
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(atlasPath), atlasGroup).address = atlasAddress;

            receipt["sceneAddress"] = sceneAddress;
            receipt["sceneRootName"] = sceneRootName;
            receipt["sceneObjectName"] = sceneObjectName;
            receipt["sceneTextSha256"] = Hash(Utf8.GetBytes(sceneText));
            receipt["atlasAddress"] = atlasAddress;
            receipt["atlasSpriteName"] = spriteName;
            receipt["atlasWidth"] = spriteSize;
            receipt["atlasHeight"] = spriteSize;
        }
#endif

        private static void ConfigurePaths(AddressableAssetSettings settings, string privateRoot, Uri baseUri, bool isBaseline)
        {
            string build = Path.Combine(privateRoot, "Remote").Replace('\\', '/');
            if (isBaseline)
            {
                settings.profileSettings.CreateValue(RemoteBuildVariable, build);
                settings.profileSettings.CreateValue(RemoteLoadVariable, new Uri(baseUri, "catalog/").AbsoluteUri);
            }
            else
            {
                settings.profileSettings.SetValue(settings.activeProfileId, RemoteBuildVariable, build);
            }

            settings.RemoteCatalogBuildPath.SetVariableByName(settings, RemoteBuildVariable);
            settings.RemoteCatalogLoadPath.SetVariableByName(settings, RemoteLoadVariable);
            settings.ContentStateBuildPath = Path.Combine(privateRoot, "State").Replace('\\', '/');
            EditorUtility.SetDirty(settings);
        }

#if YL_CONTENT_UPDATE_ENCRYPTION
        private static string ConfigureRetainedPaths(AddressableAssetSettings settings, AddressableAssetGroup group,
            string privateRoot, Uri baseUri, string releaseId)
        {
            Require(group, "The optional ordinary remote group must be present in each content build.");
            string build = Path.Combine(Path.GetDirectoryName(privateRoot), "OrdinaryRemote").Replace('\\', '/');
            string load = new Uri(baseUri, releaseId + "/ordinary/").AbsoluteUri;
            if (releaseId == "A")
            {
                settings.profileSettings.CreateValue(RetainedBuildVariable, build);
                settings.profileSettings.CreateValue(RetainedLoadVariable, load);
            }
            else
            {
                settings.profileSettings.SetValue(settings.activeProfileId, RetainedBuildVariable, build);
                settings.profileSettings.SetValue(settings.activeProfileId, RetainedLoadVariable, load);
            }

            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(settings, RetainedBuildVariable);
            schema.LoadPath.SetVariableByName(settings, RetainedLoadVariable);
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = true;
            EditorUtility.SetDirty(schema);
            EditorUtility.SetDirty(settings);
            return schema.LoadPath.GetValue(settings);
        }

        private static void SetProtected(AddressableAssetSettings settings, AddressableAssetGroup group,
            string privateRoot, string key, params string[] ordinary)
        {
            var encryption = group.GetSchema<EncryptedBundleGroupSchema>();
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            string buildVariable = "ContentUpdateProtectedBuild" + group.Guid;
            string loadVariable = "ContentUpdateProtectedLoad" + group.Guid;
            string buildPath = Path.Combine(privateRoot, "Packed", group.Guid).Replace('\\', '/');
            if (!encryption)
            {
                encryption = group.AddSchema<EncryptedBundleGroupSchema>();
                settings.profileSettings.CreateValue(buildVariable, buildPath);
                settings.profileSettings.CreateValue(loadVariable,
                    "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/yl-encrypted");
            }
            else
            {
                settings.profileSettings.SetValue(settings.activeProfileId, buildVariable, buildPath);
            }

            schema.BuildPath.SetVariableByName(settings, buildVariable);
            schema.LoadPath.SetVariableByName(settings, loadVariable);
            schema.UseAssetBundleCache = false;
            schema.ForceUniqueProvider = false;
            var provider = new UnityEngine.ResourceManagement.Util.SerializedType
            {
                Value = typeof(EncryptedAssetBundleProvider)
            };
#if UNITY_6000_3_OR_NEWER
            schema.AssetBundleProviderType = provider;
#else
            var setter = typeof(BundledAssetGroupSchema).GetProperty(nameof(schema.AssetBundleProviderType)).GetSetMethod(true);
            Require(setter != null, "Missing Addressables 1.25 bundle Provider setter.");
            setter.Invoke(schema, new object[] { provider });
#endif
            var fields = new SerializedObject(encryption);
            fields.FindProperty("_keyId").stringValue = key;
            fields.FindProperty("_deliveryMode").enumValueIndex = (int)EncryptedBundleDeliveryMode.Embedded;
            var allowed = fields.FindProperty("_publicDependencyGuids");
            allowed.arraySize = ordinary.Length;
            for (int i = 0; i < ordinary.Length; i++)
            {
                allowed.GetArrayElementAtIndex(i).stringValue = ordinary[i];
            }
            fields.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(schema);
            EditorUtility.SetDirty(settings);
        }

        private static byte[] ReadKey(JObject request, string field)
        {
            var bytes = File.ReadAllBytes(OwnedKey(request, Path.GetDirectoryName(Application.dataPath), field));
            Require(bytes.Length == 32, "Expected an ephemeral 32-byte authoring key.");
            return bytes;
        }

        private sealed class AuthorKey : IEncryptedBundleKeySource, IDisposable
        {
            private readonly string _keyId;
            private readonly byte[] _key;

            internal AuthorKey(string keyId, byte[] key)
            {
                _keyId = keyId;
                _key = key;
            }

            public EncryptedBundleKeyLease OpenReadKey(string keyId) =>
                keyId == _keyId ? new EncryptedBundleKeyLease(_key, () => { }) : null;

            public void Dispose() => Array.Clear(_key, 0, _key.Length);
        }
#endif

        private static BuildPlayerOptions PlayerOptions(string player) => new()
        {
            scenes = new[] { ScenePath },
            locationPathName = player,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.StrictMode
        };

        private static TResult WithLayout<TResult>(Func<TResult> build)
        {
            bool oldEnabled = ProjectConfigData.GenerateBuildLayout;
            var oldFormat = ProjectConfigData.BuildLayoutReportFileFormat;
            try
            {
                ProjectConfigData.GenerateBuildLayout = true;
                ProjectConfigData.BuildLayoutReportFileFormat = ProjectConfigData.ReportFileFormat.JSON;
                return build();
            }
            finally
            {
                ProjectConfigData.BuildLayoutReportFileFormat = oldFormat;
                ProjectConfigData.GenerateBuildLayout = oldEnabled;
            }
        }

        private static string ArchiveLayout(string root)
        {
            string file = Path.Combine(root, LayoutName);
            File.Copy(Path.Combine(Addressables.LibraryPath, LayoutName), file, false);
            return file;
        }

        private static string ArchivePlayerReport(string root)
        {
            string output = Path.Combine(root, "player.buildreport");
            string persisted = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "LastBuild.buildreport");
            File.Copy(persisted, output, false);
            return output;
        }

        private static void WriteTrust(Uri baseUri, string settingsHash, RSAParameters publicKey)
        {
            Require(File.ReadAllText(TrustPath, Utf8) == "{}", "The owned public trust fixture input changed before signing.");
            File.WriteAllBytes(TrustPath, Bytes(new JObject
            {
                ["application"] = ApplicationId,
                ["channel"] = Channel,
                ["platform"] = Platform,
                ["compatibility"] = Compatibility,
                ["settingsSha256"] = settingsHash,
                ["modulus"] = Convert.ToBase64String(publicKey.Modulus),
                ["exponent"] = Convert.ToBase64String(publicKey.Exponent),
                ["keyId"] = KeyId,
                ["baseUrl"] = baseUri.AbsoluteUri
            }));
            AssetDatabase.ImportAsset(TrustPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static ResourceContentManifestTrust Trust(Uri baseUri, string sha, RSAParameters publicKey) =>
            new(ApplicationId, Channel, Platform, Compatibility, sha, new Dictionary<string, RSAParameters> { [KeyId] = publicKey },
                new[] { baseUri }, allowLoopbackHttp: true);

        private static byte[] Manifest(string release, long sequence, DateTimeOffset expires, string channel, string settings,
            string catalog, string description, IEnumerable<ResourceContentArtifact> artifacts, IEnumerable<string> excludedRoutes) =>
            ResourceContentManifestWriter.Serialize(ApplicationId, channel, Platform, Compatibility, release, sequence,
                expires, settings, catalog, description, artifacts, artifacts.Select(artifact => artifact.Route).Except(excludedRoutes));

        private static byte[] Envelope(byte[] payload, byte[] signature) =>
            Bytes(new JObject { ["payload"] = Convert.ToBase64String(payload), ["signature"] = Convert.ToBase64String(signature) });

        private static JObject ReadJson(string path) => JObject.Parse(File.ReadAllText(path, Utf8),
            new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

        private static byte[] Bytes(JObject document) => Utf8.GetBytes(document.ToString(Formatting.None));

        private static string Required(JObject json, string field)
        {
            string value = (string)json[field];
            return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Missing fixture field " + field);
        }

        private static string OwnedKey(JObject request, string host, string name)
        {
            string path = Path.GetFullPath(Required(request, name));
            Require(Path.GetDirectoryName(path) == Path.Combine(host, "keys"), "Expected an ephemeral fixture key path.");
            return path;
        }

        private static RSAParameters ReadRsaParameters(string path)
        {
            var json = ReadJson(path);
            byte[] Field(string name) => json[name] == null ? null : Convert.FromBase64String((string)json[name]);
            return new RSAParameters
            {
                Modulus = Field("Modulus"), Exponent = Field("Exponent"), D = Field("D"), P = Field("P"), Q = Field("Q"),
                DP = Field("DP"), DQ = Field("DQ"), InverseQ = Field("InverseQ")
            };
        }

        private static RSA OpenRsa(string path)
        {
            var rsa = RSA.Create();
            var parameters = ReadRsaParameters(path);
            try
            {
                rsa.ImportParameters(parameters);
                return rsa;
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
            finally
            {
                foreach (byte[] bytes in new[] { parameters.D, parameters.P, parameters.Q, parameters.DP, parameters.DQ, parameters.InverseQ })
                {
                    if (bytes != null)
                    {
                        Array.Clear(bytes, 0, bytes.Length);
                    }
                }
            }
        }

        private static string WriteNew(string path, byte[] bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            return path;
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string Hash(byte[] contents)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(contents));
        }

        private static string Hex(byte[] digest) => BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();

        private static string CreateDirectory(string path) => Directory.CreateDirectory(path).FullName;

        private static string RelativeBuiltFile(string root, string path)
        {
            string relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
            Require(!relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative),
                "The official local Bundle was built outside the Player's Addressables root.");
            return relative;
        }

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy));
                File.Copy(file, copy, false);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
