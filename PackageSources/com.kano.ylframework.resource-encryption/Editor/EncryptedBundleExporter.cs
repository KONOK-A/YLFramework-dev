using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.Layout;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using Object = UnityEngine.Object;

namespace YLFramework.Resource.Encryption.Editor
{
    /// <summary>Audits and publishes one immutable Windows x64 encrypted delivery.</summary>
    public static partial class EncryptedBundleExporter
    {
        private const string ReceiptName = "delivery.json";
        private const string StateName = "addressables_content_state.bin";
        private const string LayoutName = "buildlayout.json";
        private const string SettingsName = "settings.json";
        private const string LocalLink = "AddressablesLink/link.xml";
        private const string ProtectedCategory = "protected";
        private const string OrdinaryCategory = "ordinary";
        private const string NativePluginName = "ylresource_tink.dll";
        private static bool _isBuilding;

        /// <summary>Builds the fixed Player and its upload inventory into a single, new release directory.</summary>
        /// <remarks>
        /// Use an exclusive publisher account. Build and key callbacks must finish all writes before returning and must not
        /// change staging permissions. The staged and published tree stays publisher-only; this is not a sandbox for callbacks.
        /// </remarks>
        public static void Build(AddressableAssetSettings settings, IReadOnlyList<AddressableAssetGroup> groups,
            BuildPlayerOptions player, string applicationNamespace, string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys)
        {
            BuildCore(settings, groups, player, applicationNamespace, privateRoot, releaseRoot, keys, false);
        }

        internal static EncryptedBundleDescription BuildCore(AddressableAssetSettings settings, IReadOnlyList<AddressableAssetGroup> groups,
            BuildPlayerOptions player, string applicationNamespace, string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys,
            bool allowLoopback)
        {
            return RunBuild(settings, groups, keys,
                () => BuildRelease(settings, groups.ToArray(), player, applicationNamespace, privateRoot, releaseRoot, keys, allowLoopback));
        }

        private static EncryptedBundleDescription BuildRelease(AddressableAssetSettings settings, AddressableAssetGroup[] groups,
            BuildPlayerOptions player, string application, string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys,
            bool allowLoopback, VersionedRelease versioned = null)
        {
            var backend = ValidatePlayer(settings, player, versioned != null);
            var nativePlugin = FreezeInstalledPlugin();
            player.scenes = (string[])player.scenes.Clone();
            privateRoot = LocalPath(privateRoot);
            releaseRoot = LocalPath(releaseRoot);
            player.locationPathName = LocalPath(player.locationPathName);
            string exe = Path.GetFileName(player.locationPathName);
            if (!exe.EndsWith(".exe", StringComparison.Ordinal) || exe.Length == 4 ||
                !SamePath(Path.GetDirectoryName(player.locationPathName), Path.Combine(releaseRoot, "Player")))
                throw new InvalidDataException("Player output must be <releaseRoot>/Player/<name>.exe.");
            string releaseId = EncryptedBundleBinding.Token(Path.GetFileName(releaseRoot), "releaseId");
            EncryptedBundleBinding.Token(application, "application");

            string project = Path.GetDirectoryName(Application.dataPath);
            string library = Path.Combine(project, "Library", "com.unity.addressables");
            string buildReports = Path.Combine(library, "BuildReports");
            string buildPath = Path.GetFullPath(Addressables.BuildPath);
            string scratch = Path.Combine(project, "Temp", "com.unity.addressables", "AssetBundles");
            if (!SamePath(Addressables.LibraryPath, library) || !SamePath(Addressables.BuildReportPath, buildReports) ||
                !SamePath(settings.buildSettings.bundleBuildPath, scratch) || !StrictlyBelow(buildPath, Path.Combine(library, "aa")))
                throw new InvalidDataException("Official Addressables output path is not the project's fixed Library/Temp layout.");
            string[] reserved = { Application.dataPath, Path.Combine(project, "Packages"), Path.Combine(project, "PackageSources"),
                Path.Combine(project, "ProjectSettings"), Path.Combine(project, "Library"), Path.Combine(project, "Temp") };
            using var paths = new BuildOutputPaths();
            string[] officialRoots = { library, buildReports, buildPath, scratch };
            foreach (string root in officialRoots)
                paths.Identity(root);
            foreach (string root in new[] { privateRoot, releaseRoot })
            {
                if (reserved.Any(forbidden => paths.Overlap(root, forbidden)))
                    throw new InvalidDataException("Encrypted output physically intersects project input or intermediate data: " + root);
                RequireAbsent(root);
            }
            string physicalRelease = paths.Identity(releaseRoot);
            if (paths.Overlap(privateRoot, releaseRoot) || Volume(paths.Identity(privateRoot)) != Volume(physicalRelease))
                throw new InvalidDataException("Use disjoint fresh private and release directories on one physical local volume.");
            using var privateParent = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(privateRoot), false);
            using var releaseParent = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(releaseRoot), false);

            string stateFolder = LocalPath(settings.profileSettings.EvaluateString(settings.activeProfileId, settings.ContentStateBuildPath));
            if (!StrictlyBelow(stateFolder, privateRoot) || !StrictlyBelow(paths.Identity(stateFolder), paths.Identity(privateRoot)))
                throw new InvalidDataException("Official content state must physically belong to the private root.");
            var plans = PlanGroups(settings, groups, paths, privateRoot, releaseRoot, stateFolder, buildPath, reserved,
                application, releaseId, allowLoopback, out string prefix);
            string officialCatalogRoot = versioned == null ? null : CheckOfficialCatalogRoot(settings, paths, privateRoot, stateFolder, plans, allowLoopback);
            string[] groupOrder = groups.Select(group => group.Guid).ToArray();
            var audit = new ProtectedAssetAudit(groups, player.scenes);
            foreach (string root in new[] { library, scratch })
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (string file in ProtectedAssetAudit.EnumerateOwnedFiles(root))
                    using (OwnedWindowsFiles.OpenRead(file))
                    {
                    }
            }

            // All profile/physical identity checks above are write-free. Do not take over an existing private or remote root.
            paths.CreateFresh(privateRoot);
            foreach (string root in plans.Values.Where(plan => plan.IsOrdinaryRemote).Select(plan => plan.Root).Distinct(StringComparer.OrdinalIgnoreCase))
                paths.CreateFresh(root);
            foreach (string root in officialRoots.Append(stateFolder).Concat(plans.Values.Select(plan => plan.Root)))
                paths.Prepare(root);
            if (officialCatalogRoot != null)
                paths.Prepare(officialCatalogRoot);
            string state = Path.GetFullPath(ContentUpdateScript.GetContentStateDataPath(false, settings));
            if (!SamePath(state, Path.Combine(stateFolder, StateName)))
                throw new InvalidDataException("Official content state resolved to an unsupported path.");

            string layoutPath = Path.Combine(library, LayoutName);
            string previousLayout = File.Exists(layoutPath) ? ProtectedAssetAudit.DigestFile(layoutPath) : null;
            string catalogName;
            string routesHash;
            var sources = new Dictionary<string, FrozenFile>(StringComparer.OrdinalIgnoreCase) { [nativePlugin.Path] = nativePlugin };
            var streaming = new Dictionary<string, FrozenFile>(StringComparer.Ordinal);
            List<BundleSource> bundles;
            var builder = ScriptableObject.CreateInstance<EncryptedPackedBuilder>();
            try
            {
                builder.ProtectedGroups = groups;
                builder.ApplicationNamespace = application;
                builder.ReleaseId = releaseId;
                builder.ReleasePrefix = prefix;
                builder.Loopback = allowLoopback;
                builder.Versioned = versioned != null;
                builder.PlayerScenes = player.scenes;
                var output = BuildPacked(settings, builder);
                if (output == null || !string.IsNullOrEmpty(output.Error) || output.IsUpdateContentBuild ||
                    !File.Exists(layoutPath) || ProtectedAssetAudit.DigestFile(layoutPath) == previousLayout || !File.Exists(state))
                    throw new InvalidDataException("Official Packed Builder did not produce current Bundle, content state and Build Layout outputs: " + output?.Error);

                catalogName = builder.CatalogName;
                routesHash = builder.RoutesSha256;
                string catalogPath = Path.Combine(buildPath, catalogName);
                string settingsPath = Path.Combine(buildPath, SettingsName);
                if (versioned != null)
                {
                    var official = ContentUpdateScript.LoadContentState(state);
                    VerifyOfficialCatalog(settings, official, officialCatalogRoot, catalogPath, catalogName);
                    string archiveRoot = Path.Combine(privateRoot, ".yl-state-archive");
                    paths.CreateFresh(archiveRoot);
                    string archivePath = Path.Combine(archiveRoot, StateName);
                    using (var archive = OwnedWindowsFiles.CreateTemporaryPinned(archivePath))
                        CopyVerified(ReadFile(state), archive);
                    versioned.Baseline = YLFramework.Editor.Resource.ResourceContentUpdateAuthoring.PrepareBaseline(
                        settings, settingsPath, catalogPath, archivePath);
                    versioned.Metadata = new JObject { ["playerBuilds"] = 1, ["scenes"] = new JArray(player.scenes) };
                    foreach (string archive in new[] { versioned.Baseline.ReceiptPath, versioned.Baseline.StartupSettingsPath, versioned.Baseline.CatalogPath })
                        sources.Add(archive, ReadFile(archive));
                }
                using (var input = OwnedWindowsFiles.OpenRead(settingsPath))
                {
                    sources.Add(settingsPath, new FrozenFile(settingsPath, input.Length, AuthenticatedBundleStream.Hash(input, default)));
                    input.Position = 0;
                    FrozenAddressablesRoutes.CheckRuntimeSettings(EncryptedDeliverySession.ReadText(input), catalogName);
                }
                foreach (string path in new[] { state, catalogPath, Path.Combine(buildPath, LocalLink) })
                    sources.Add(path, ReadFile(path));
                streaming.Add("aa/" + SettingsName, sources[settingsPath]);
                foreach (string relative in new[] { catalogName, LocalLink })
                    streaming.Add("aa/" + relative, sources[Path.Combine(buildPath, relative)]);

                using (var input = OwnedWindowsFiles.OpenRead(layoutPath))
                {
                    sources.Add(layoutPath, new FrozenFile(layoutPath, input.Length, AuthenticatedBundleStream.Hash(input, default)));
                    input.Position = 0;
                    using var text = new StreamReader(input, new UTF8Encoding(false, true));
                    var layout = new BuildLayout();
                    // BuildLayout.ReadFull uses this deserializer, but its default File.Open requests write access.
                    EditorJsonUtility.FromJsonOverwrite(text.ReadToEnd(), layout);
                    audit.AuditLayout(layout, builder);
                }
                bundles = FreezeBundles(output, builder, plans, player.scenes, buildPath, catalogPath, streaming, sources, allowLoopback);
                CheckLooseRoots(plans.Values, bundles);
            }
            finally
            {
                Object.DestroyImmediate(builder);
            }

            string rawPlayer = Path.Combine(privateRoot, ".yl-player-" + Guid.NewGuid().ToString("N"));
            string shippingLibrary = Path.Combine(privateRoot, ".yl-aa-" + Guid.NewGuid().ToString("N"));
            if (plans.Values.Select(plan => plan.Root).Append(stateFolder).Any(root => paths.Overlap(root, rawPlayer) || paths.Overlap(root, shippingLibrary)))
                throw new InvalidDataException("Private Player staging overlaps an authoring output.");
            paths.CreateFresh(rawPlayer);
            paths.CreateFresh(shippingLibrary);
            player.locationPathName = Path.Combine(rawPlayer, exe);
            string data = Path.GetFileNameWithoutExtension(exe) + "_Data";
            string stage = Path.Combine(privateRoot, ".yl-release-" + Guid.NewGuid().ToString("N"));
            using var ownedStage = OwnedWindowsFiles.CreatePublicationDirectory(stage);
            using var candidate = new CandidateFiles(stage);
            string aa = "Player/" + data + "/StreamingAssets/aa/";
            var expected = new Dictionary<string, FrozenFile>(StringComparer.Ordinal);
            var uploads = new List<Upload>();
            string descriptionPath = aa + EncryptedBundleDescription.FileName;
            EncryptedBundleDescription description = null;
            if (versioned != null)
            {
                foreach (var file in streaming)
                {
                    string relative = aa + file.Key.Substring("aa/".Length);
                    expected.Add(relative, candidate.Copy(relative, file.Value));
                }
                StageContent();
                versioned.Result = DescribeContent(releaseRoot, description, aa + catalogName, descriptionPath,
                    aa + SettingsName, "ContentState/" + StateName, "ContentState/" + LayoutName, bundles, aa, expected,
                    Path.Combine(buildPath, catalogName), allowLoopback);
                if (versioned.ConfigurePlayer != null)
                {
                    versioned.Result.VerifyStaged = () => candidate.Verify(expected);
                    try
                    {
                        var (payload, signature) = versioned.ConfigurePlayer(versioned.Result);
                        AddPlayerManifestFile("yl-content-manifest.json", payload, YLFramework.Resource.ResourceContentManifest.MaximumPayloadBytes);
                        AddPlayerManifestFile("yl-content-manifest.sig", signature, YLFramework.Resource.ResourceContentManifest.MaximumSignatureEnvelopeBytes);
                    }
                    finally
                    {
                        versioned.Result.VerifyStaged = null;
                    }
                }
            }
            var report = BuildPlayer(player, shippingLibrary, streaming);
            string streamingRoot = Path.Combine(rawPlayer, data, "StreamingAssets");
            audit.AuditPlayer(report, streamingRoot, streaming.ToDictionary(row => row.Key, row => row.Value.Sha256, StringComparer.Ordinal));
            var playerFiles = FreezePlayer(report, rawPlayer, exe, data, backend, streaming, nativePlugin);
            foreach (var file in playerFiles)
            {
                string relative = "Player/" + file.Key;
                if (expected.TryGetValue(relative, out var staged))
                {
                    if (staged.Bytes != file.Value.Bytes || staged.Sha256 != file.Value.Sha256)
                        throw new InvalidDataException("The built Player asset differs from the sealed content: " + relative);
                }
                else
                {
                    expected.Add(relative, candidate.Copy(relative, file.Value));
                }
            }
            if (versioned == null)
                StageContent();
            else
                versioned.Result.PlayerReport = report;

            // No application callback may run after this point. Already-frozen hashes, not a new baseline, validate each input and output.
            foreach (var bundle in bundles)
                RequireFile(bundle.File, bundle.Binding != null ? "Audited plaintext Bundle changed" : "Audited ordinary Bundle changed");
            foreach (var file in sources.Values.Concat(playerFiles.Values))
                RequireFile(file, "Audited delivery source changed");
            CheckLooseRoots(plans.Values, bundles);
            CheckUploadUrls(uploads);

            string receipt = EncodeReceipt(application, releaseId, prefix, description, expected[descriptionPath].Sha256, expected, uploads,
                versioned == null ? null : versioned.Metadata);
            expected.Add(ReceiptName, WriteFile(candidate.Create(ReceiptName), receipt));
            using (var auditFile = OwnedWindowsFiles.CreateTemporaryPinned(Path.Combine(privateRoot, "audit.json")))
            {
                WriteFile(auditFile, new JObject
                {
                    ["layoutSha256"] = sources[layoutPath].Sha256,
                    ["contentStateSha256"] = sources[state].Sha256,
                    ["catalogSha256"] = description.CatalogSha256,
                    ["settingsSha256"] = description.SettingsSha256,
                    ["routesSha256"] = routesHash,
                    ["playerResult"] = report.summary.result.ToString(),
                    ["playerBuildGuid"] = report.summary.guid.ToString(),
                    ["playerExeSha256"] = playerFiles[exe].Sha256,
                    ["protectedAssets"] = audit.Report(),
                    ["authorGroups"] = audit.Authors()
                }.ToString(Formatting.Indented));
            }
            candidate.Verify(expected);
            // Windows requires closing the child handles. The publisher-only DACL and both parent pins remain in force.
            candidate.Dispose();
            OwnedWindowsFiles.Publish(ownedStage, physicalRelease);
            return description;

            void StageContent()
            {
                expected.Add("ContentState/" + LayoutName, candidate.Copy("ContentState/" + LayoutName, sources[layoutPath]));
                expected.Add("ContentState/" + StateName, candidate.Copy("ContentState/" + StateName, sources[state]));
                if (versioned != null)
                {
                    foreach (string archive in new[] { versioned.Baseline.ReceiptPath, versioned.Baseline.StartupSettingsPath, versioned.Baseline.CatalogPath })
                    {
                        string relative = "ContentState/" + Path.GetFileName(archive);
                        expected.Add(relative, candidate.Copy(relative, sources[archive]));
                    }
                }
                foreach (var bundle in bundles.Where(bundle => bundle.Plan.IsOrdinaryRemote))
                {
                    expected.Add(bundle.Relative, candidate.Copy(bundle.Relative, bundle.File));
                    uploads.Add(new Upload(bundle.Relative, bundle.Url, OrdinaryCategory));
                }
                var descriptionOutput = candidate.Create(descriptionPath);
                var rows = SealGroups(groupOrder, bundles, keys, candidate, aa, expected, uploads);
                description = new EncryptedBundleDescription(catalogName, streaming["aa/" + catalogName].Sha256,
                    streaming["aa/" + SettingsName].Sha256, routesHash, rows);
                expected.Add(descriptionPath, WriteFile(descriptionOutput, description.Encode()));
            }

            void AddPlayerManifestFile(string name, byte[] bytes, int maximum)
            {
                if (bytes == null || bytes.Length == 0 || bytes.Length > maximum)
                    throw new InvalidDataException("The signed installation manifest file has an unsupported length: " + name);
                string path = Path.Combine(privateRoot, name);
                using (var output = OwnedWindowsFiles.CreateTemporaryPinned(path))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                    var file = new FrozenFile(path, bytes.Length, AuthenticatedBundleStream.Hash(output, default));
                    streaming.Add("aa/" + name, file);
                    sources.Add(path, file);
                }
            }
        }

        private static Dictionary<string, GroupPlan> PlanGroups(AddressableAssetSettings settings, AddressableAssetGroup[] groups,
            BuildOutputPaths paths, string privateRoot, string releaseRoot, string stateFolder, string buildPath, string[] reserved,
            string application, string releaseId, bool allowLoopback, out string releasePrefix, bool privateLocal = false)
        {
            if (groups.Any(group => !group || group.Settings != settings || !settings.groups.Contains(group) || !group.HasSchema<EncryptedBundleGroupSchema>()) ||
                groups.Distinct().Count() != groups.Length || groups.Contains(settings.DefaultGroup))
                throw new InvalidDataException("Select distinct protected groups in this project; the shared default group must remain ordinary.");
            releasePrefix = null;
            var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plans = new Dictionary<string, GroupPlan>(StringComparer.Ordinal);
            foreach (var group in settings.groups.Where(group => group))
            {
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                var auth = group.GetSchema<EncryptedBundleGroupSchema>();
                bool protect = groups.Contains(group);
                if ((auth != null) != protect || (schema?.AssetBundleProviderType.Value == typeof(EncryptedAssetBundleProvider)) != protect)
                    throw new InvalidDataException("All and only encrypted-provider groups must be selected: " + group.Name);
                if (!schema || !schema.IncludeInBuild)
                {
                    if (protect)
                        throw new InvalidDataException("A protected group is excluded from the build: " + group.Name);
                    continue;
                }
                string name = group.Name.Replace(" ", "").Replace('\\', '/').Replace("//", "/");
                if (name.Length == 0 || !allNames.Add(name))
                    throw new InvalidDataException("Groups have an ambiguous normalized official Bundle name: " + group.Name);
                if (schema.BundledAssetProviderType.Value != typeof(BundledAssetProvider))
                    throw new InvalidDataException("Encrypted delivery requires official asset providers: " + group.Name);
                string guid = EncryptedBundleBinding.Hex(group.Guid, 32, "groupGuid");
                string build = schema.BuildPath.GetValue(settings);
                string root = LocalPath(Path.GetFullPath(build));
                string load = EncryptedPackedBuilder.NormalizeLoadPrefix(schema.LoadPath.GetValue(settings));
                EncryptedBundleDeliveryMode? mode = null;
                bool local = false;
                if (protect)
                {
                    mode = auth.DeliveryMode;
                    if (schema.Compression != BundledAssetGroupSchema.BundleCompressionMode.LZ4 || schema.UseAssetBundleCache ||
                        schema.ForceUniqueProvider || schema.AssetLoadMode != AssetLoadMode.RequestedAssetAndDependencies ||
                        schema.UseUnityWebRequestForLocalBundles || schema.GetBundleCachedProviderId() != typeof(EncryptedAssetBundleProvider).FullName
#if UNITY_6000_3_OR_NEWER
                        || schema.StripDownloadOptions
#endif
                        )
                        throw new InvalidDataException("Protected groups require LZ4, one encrypted provider, requested-asset loading, and uncached download options.");
                    if (!SamePath(root, LocalPath(build)) || !StrictlyBelow(root, privateRoot) ||
                        !StrictlyBelow(paths.Identity(root), paths.Identity(privateRoot)) || paths.Overlap(root, stateFolder))
                        throw new InvalidDataException("Each protected BuildPath must be a distinct private plaintext staging directory: " + group.Name);
                    if (mode == EncryptedBundleDeliveryMode.RemoteHttps)
                    {
                        CheckRemoteUrl(load, allowLoopback);
                        if (releasePrefix != null && load != releasePrefix)
                            throw new InvalidDataException("Protected remote groups must use one immutable LoadPath prefix.");
                        releasePrefix = load;
                    }
                    else if (mode != EncryptedBundleDeliveryMode.Embedded || load != FrozenAddressablesRoutes.EmbeddedPrefix)
                        throw new InvalidDataException("An embedded group must use the canonical yl-encrypted LoadPath.");
                    EncryptedBundleBinding.Token(auth.KeyId, "keyId");
                }
                else
                {
                    if (schema.AssetBundleProviderType.Value != typeof(AssetBundleProvider))
                        throw new InvalidDataException("An ordinary group must use the stock Bundle provider: " + group.Name);
                    local = BelowOrEqual(root, buildPath) && BelowOrEqual(paths.Identity(root), paths.Identity(buildPath));
                    if (((!local || !privateLocal) && paths.Overlap(root, privateRoot)) || paths.Overlap(root, releaseRoot) ||
                        (!local && reserved.Any(forbidden => paths.Overlap(root, forbidden))))
                        throw new InvalidDataException("Ordinary Bundle build path overlaps protected, Player or non-owned output: " + group.Name);
                    if (local)
                    {
                        string subfolder = SamePath(buildPath, root) ? "" : ProtectedAssetAudit.Relative(buildPath, root) + "/";
                        if (load != FrozenAddressablesRoutes.RuntimeToken + "/" + subfolder || load.StartsWith(FrozenAddressablesRoutes.EmbeddedPrefix, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("A stock local Bundle path must match its official installed aa path: " + group.Name);
                    }
                    else
                    {
                        if (!SamePath(root, LocalPath(build)))
                            throw new InvalidDataException("Use an absolute ordinary remote output root: " + group.Name);
                        RequireAbsent(root);
                        using (OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(root), false))
                        {
                        }
                        CheckRemoteUrl(load, allowLoopback);
                    }
                }
                if (!local && schema.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash)
                    throw new InvalidDataException("Protected and ordinary remote groups require hashed Bundle filenames: " + group.Name);
                var plan = new GroupPlan(guid, root, load, schema.GetBundleCachedProviderId(), mode, auth?.KeyId, local);
                foreach (var prior in plans.Values.Where(prior => paths.Overlap(prior.Root, root)))
                {
                    bool sharedOrdinary = prior.Mode == null && mode == null && prior.IsOrdinaryLocal == local &&
                        (local || SamePath(prior.Root, root));
                    if (!sharedOrdinary)
                        throw new InvalidDataException("Ambiguous Bundle staging roots: " + group.Name);
                }
                if (!plans.TryAdd(guid, plan))
                    throw new InvalidDataException("Duplicate authoring group GUID: " + guid);
            }
            foreach (var plan in plans.Values.Where(plan => plan.Mode.HasValue))
                _ = new EncryptedBundleBinding(application, releaseId, releasePrefix, plan.Mode.Value, plan.Guid, "validate", new string('0', 32),
                    plan.ProviderId, "validate.bundle", plan.KeyId, 1, new string('0', 64), allowLoopback);
            return plans;
        }

        private static ScriptingImplementation ValidatePlayer(AddressableAssetSettings settings, BuildPlayerOptions player, bool versioned)
        {
            var serialized = new SerializedObject(settings);
            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            bool x64 = player.target == BuildTarget.StandaloneWindows64;
#if UNITY_6000_3_OR_NEWER
            x64 &= UnityEditor.WindowsStandalone.UserBuildSettings.architecture == OSArchitecture.x64;
#endif
            const BuildOptions Forbidden = BuildOptions.Development | BuildOptions.AutoRunPlayer | BuildOptions.BuildScriptsOnly |
                BuildOptions.AcceptExternalModificationsToPlayer | BuildOptions.AllowDebugging;
            if (AddressableAssetSettingsDefaultObject.Settings != settings || settings.BuildRemoteCatalog != versioned || !settings.DisableCatalogUpdateOnStartup ||
                settings.InitializationObjects.Count != 0 || serialized.FindProperty("m_ExtractTypeTreeData")?.boolValue == true ||
                serialized.FindProperty("m_BundleLocalCatalog")?.boolValue == true ||
                settings.BuildAddressablesWithPlayerBuild != AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer ||
                EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64 || !x64 || player.subtarget == (int)StandaloneBuildSubtarget.Server ||
                (backend != ScriptingImplementation.IL2CPP && backend != ScriptingImplementation.Mono2x) || (player.options & Forbidden) != 0 ||
                player.scenes == null || player.scenes.Length == 0 || player.scenes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != player.scenes.Length ||
                player.scenes.Any(scene => string.IsNullOrEmpty(scene) || AssetDatabase.GetMainAssetTypeAtPath(scene) != typeof(SceneAsset)))
                throw new InvalidDataException("Encrypted release requires the selected Catalog build mode and a Windows x64 Release Player without startup hooks or Player content rebuild.");
            if (Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                .Any(index => UnityEngine.SceneManagement.SceneManager.GetSceneAt(index).isDirty))
                throw new InvalidOperationException("Save the selected build scenes before the encrypted build.");
            return backend;
        }

        private static AddressablesPlayerBuildResult BuildPacked(AddressableAssetSettings settings, EncryptedPackedBuilder builder, string previousState = null)
        {
            bool oldLayout = ProjectConfigData.GenerateBuildLayout;
            var oldFormat = ProjectConfigData.BuildLayoutReportFileFormat;
            try
            {
                ProjectConfigData.GenerateBuildLayout = true;
                ProjectConfigData.BuildLayoutReportFileFormat = ProjectConfigData.ReportFileFormat.JSON;
                return previousState == null
                    ? builder.BuildData<AddressablesPlayerBuildResult>(new AddressablesDataBuilderInput(settings))
                    : BuildOfficialUpdate(settings, builder, previousState);
            }
            finally
            {
                ProjectConfigData.BuildLayoutReportFileFormat = oldFormat;
                ProjectConfigData.GenerateBuildLayout = oldLayout;
            }
        }

        private static List<BundleSource> FreezeBundles(AddressablesPlayerBuildResult output, EncryptedPackedBuilder builder,
            Dictionary<string, GroupPlan> plans, string[] scenes, string buildPath, string catalogPath,
            Dictionary<string, FrozenFile> streaming, Dictionary<string, FrozenFile> sources, bool loopback,
            IReadOnlyDictionary<string, (ResourceContentArtifact Artifact, FrozenFile File)> retainedOrdinary = null)
        {
            var actual = builder.VerifySerializedCatalog(catalogPath, scenes);
            var bundles = new List<BundleSource>(output.AssetBundleBuildResults.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in output.AssetBundleBuildResults)
            {
                if (!result.SourceAssetGroup || !plans.TryGetValue(result.SourceAssetGroup.Guid, out var plan) ||
                    !seen.Add(result.InternalBundleName) || !builder.Bundles.TryGetValue(result.InternalBundleName, out var packed) ||
                    packed.InternalName != result.InternalBundleName || packed.GroupGuid != plan.Guid || packed.Hash != result.Hash || packed.ProviderId != plan.ProviderId)
                    throw new InvalidDataException("Actual BuildResult Bundle does not match its attributed SBP group/name/hash.");
                string path = LocalPath(Path.GetFullPath(result.FilePath));
                if (ProtectedAssetAudit.Relative(plan.Root, path) != packed.FileName || packed.SourceId != plan.LoadPrefix +
                    (plan.Mode.HasValue ? plan.Guid + "/" : "") + packed.FileName)
                    throw new InvalidDataException("Actual BuildResult path or serialized route differs from the frozen group: " + packed.FileName);
                if (packed.RetainedOrdinaryRoute != null)
                {
                    if (!plan.IsOrdinaryRemote || retainedOrdinary == null ||
                        !retainedOrdinary.TryGetValue(packed.RetainedOrdinaryRoute, out var retained))
                        throw new InvalidDataException("The retained ordinary Bundle is missing its audited A artifact: " + packed.RetainedOrdinaryRoute);
                    string stagedPath = null;
                    if (File.Exists(path))
                    {
                        var staged = ReadFile(path);
                        if (staged.Bytes != retained.File.Bytes || staged.Sha256 != retained.File.Sha256)
                            throw new InvalidDataException("Ordinary Bundle build output differs at an immutable A route: " + packed.RetainedOrdinaryRoute);
                        sources.Add(path, staged);
                        stagedPath = path;
                    }
                    sources.Add(retained.File.Path, retained.File);
                    bundles.Add(new BundleSource(plan, retained.File, null, null, null, packed, retained.Artifact, stagedPath));
                    continue;
                }
                var file = ReadFile(path);
                sources.Add(path, file);
                if (packed.Retained != null && (file.Bytes != packed.Retained.Binding.PlainLength || file.Sha256 != packed.Retained.Binding.PlainSha256))
                    throw new InvalidDataException("Official update retained a protected route whose original plaintext digest differs from the audited SBP bytes: " + packed.SourceId);
                var options = (AssetBundleRequestOptions)actual[packed.DeliverySourceId].Data;
                EncryptedBundleBinding binding = null;
                string relative = null;
                string url = null;
                if (packed.Binding != null)
                {
                    var planned = packed.Binding;
                    if (planned.Mode != plan.Mode || planned.KeyId != plan.KeyId || planned.PlainLength != file.Bytes)
                        throw new InvalidDataException("Actual protected source length or group schema differs from its authoring identity.");
                    binding = new EncryptedBundleBinding(planned.Application, planned.ReleaseId, planned.ReleasePrefix, planned.Mode, planned.GroupGuid,
                        planned.BundleName, planned.BundleHash, planned.ProviderId, planned.RelativePath, planned.KeyId, file.Bytes, file.Sha256, loopback);
                    if (binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps)
                    {
                        url = packed.SourceId;
                        relative = "Upload/protected/" + binding.GroupPath;
                    }
                }
                else if (plan.IsOrdinaryLocal)
                {
                    if (options.BundleSize != 0 && options.BundleSize != file.Bytes)
                        throw new InvalidDataException("Stock local Bundle byte count differs from the official result: " + packed.SourceId);
                    streaming.Add("aa/" + ProtectedAssetAudit.Relative(buildPath, path), file);
                }
                else
                {
                    url = CheckRemoteUrl(packed.SourceId, loopback);
                    relative = "Upload/ordinary/" + packed.FileName;
                    if (options.BundleSize != file.Bytes)
                        throw new InvalidDataException("Ordinary remote Catalog Bundle byte count differs from its physical source: " + url);
                }
                bundles.Add(new BundleSource(plan, file, binding, relative, url, packed));
            }
            if (seen.Count != builder.Bundles.Count)
                throw new InvalidDataException("Official physical and serialized Bundle inventories differ.");
            return bundles;
        }

        private static BuildReport BuildPlayer(BuildPlayerOptions player, string shippingLibrary, Dictionary<string, FrozenFile> streaming)
        {
            string previous = Addressables.LibraryPath;
            BuildReport report;
            try
            {
                Addressables.LibraryPath = shippingLibrary + Path.DirectorySeparatorChar;
                string aa = Path.GetFullPath(Addressables.BuildPath);
                if (!StrictlyBelow(aa, shippingLibrary))
                    throw new InvalidDataException("Stock Player processor did not resolve the private Addressables input.");
                foreach (var file in streaming)
                {
                    string path = Path.Combine(aa, file.Key.Substring("aa/".Length));
                    using var pin = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(path), true);
                    using var output = OwnedWindowsFiles.CreateTemporaryPinned(path);
                    CopyVerified(file.Value, output);
                }
                player.options |= BuildOptions.DetailedBuildReport | BuildOptions.StrictMode;
                report = BuildPipeline.BuildPlayer(player);
            }
            finally
            {
                Addressables.LibraryPath = previous;
            }
            if (report == null || report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0 ||
                !SamePath(report.summary.outputPath, player.locationPathName))
                throw new InvalidDataException("The audited Windows x64 Player failed to build.");
            return report;
        }

        private static FrozenFile FreezeInstalledPlugin()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(EncryptedDeliverySession).Assembly);
            if (package?.name != "com.kano.ylframework.resource-encryption" || string.IsNullOrEmpty(package.resolvedPath))
                throw new InvalidDataException("Cannot resolve the installed encrypted delivery package.");
            string path = Path.Combine(package.resolvedPath, "Runtime/Plugins/Win64", NativePluginName);
            using var input = OwnedWindowsFiles.OpenRead(path);
            ProtectedAssetAudit.RequireWin64NativePlugin(input);
            return new FrozenFile(path, input.Length, AuthenticatedBundleStream.Hash(input, default));
        }

        private static Dictionary<string, FrozenFile> FreezePlayer(BuildReport report, string root, string exe, string data,
            ScriptingImplementation backend, Dictionary<string, FrozenFile> streaming, FrozenFile nativePlugin)
        {
            const string Streaming = "/StreamingAssets/";
            string outputName = Path.GetFileNameWithoutExtension(exe);
            string nativePath = data + "/Plugins/x86_64/" + NativePluginName;
            var reported = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in report.GetFiles())
            {
                string file = Path.GetFullPath(item.path);
                if (!StrictlyBelow(file, root))
                    continue;
                string relative = ProtectedAssetAudit.Relative(root, file);
                if (relative.Split('/').Any(component => IsNonShipping(component, outputName)))
                    continue;
                if (reported.TryGetValue(relative, out ulong size) && size != item.size)
                    throw new InvalidDataException("The audited Player report gives conflicting sizes: " + relative);
                reported[relative] = item.size;
            }
            var files = new Dictionary<string, FrozenFile>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var pin = OwnedWindowsFiles.PinDirectory(root, false);
            foreach (string file in ProtectedAssetAudit.EnumerateTree(root, path => !IsNonShipping(ProtectedAssetAudit.Relative(root, path), outputName)))
            {
                string path = ProtectedAssetAudit.Relative(root, file);
                if (Path.GetFileName(file).Equals(NativePluginName, StringComparison.OrdinalIgnoreCase) && path != nativePath)
                    throw new InvalidDataException("Player contains a native encryption plugin outside its canonical path: " + path);
                if (!names.Add(path) || !IsPlayerFile(file, path, exe, data, backend, reported.ContainsKey(path), streaming))
                    throw new InvalidDataException("Unrecognized file outside the Win64 Player release allowlist: " + path);
                var frozen = ReadFile(file);
                if (reported.TryGetValue(path, out ulong size) && size != (ulong)frozen.Bytes)
                    throw new InvalidDataException("The audited Player file size differs from the official build report: " + path);
                files.Add(path, frozen);
            }
            if (!files.TryGetValue(nativePath, out var actualPlugin) || actualPlugin.Bytes != nativePlugin.Bytes ||
                actualPlugin.Sha256 != nativePlugin.Sha256)
                throw new InvalidDataException("Player native plugin is missing or differs from the frozen installed Win64 plugin: " + nativePath);
            var backendFiles = backend == ScriptingImplementation.IL2CPP
                ? new[] { "GameAssembly.dll", "baselib.dll", data + "/il2cpp_data/Metadata/global-metadata.dat" }
                : new[] { "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll", data + "/Managed/mscorlib.dll",
                    data + "/Managed/" + typeof(EncryptedDeliverySession).Assembly.GetName().Name + ".dll" };
            foreach (string path in backendFiles)
                if (!files.TryGetValue(path, out var file) || file.Bytes <= 0)
                    throw new InvalidDataException("The audited Player missed a required backend file: " + path);
            foreach (string path in reported.Keys.Concat(new[] { exe, "UnityPlayer.dll", data + "/boot.config" })
                .Concat(streaming.Keys.Select(path => data + Streaming + path)))
                if (!files.ContainsKey(path))
                    throw new InvalidDataException("The audited Player missed an expected official file: " + path);
            if (!files.ContainsKey(data + "/globalgamemanagers") && !files.ContainsKey(data + "/data.unity3d"))
                throw new InvalidDataException("The audited Player missed its generated boot data.");
            if (files.Any(file => file.Key.StartsWith(data + Streaming, StringComparison.Ordinal) &&
                file.Value.Sha256 != streaming[file.Key.Substring(data.Length + Streaming.Length)].Sha256))
                throw new InvalidDataException("Actual Player StreamingAssets bytes differ from the frozen inputs.");
            return files;
        }

        private static bool IsNonShipping(string path, string outputName) =>
            path.Equals(outputName + "_BackUpThisFolder_ButDontShipItWithYourGame", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(outputName + "_BurstDebugInformation_DoNotShip", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(outputName + "_DebugInformation_DoNotShip", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase);

        private static bool IsPlayerFile(string file, string path, string exe, string data, ScriptingImplementation backend, bool reported,
            Dictionary<string, FrozenFile> streaming)
        {
            string streamingPrefix = data + "/StreamingAssets/";
            if (path.StartsWith(streamingPrefix, StringComparison.Ordinal))
                return streaming.ContainsKey(path.Substring(streamingPrefix.Length));
            if (path == "baselib.dll")
                return backend == ScriptingImplementation.IL2CPP && MatchesRuntimeTemplate(file, path, "il2cpp");
            if (path == exe || path == "UnityPlayer.dll" || path == "UnityCrashHandler64.exe" || path == "GameAssembly.dll" ||
                path == "D3D12/D3D12Core.dll")
                return true;
            if (path.StartsWith("MonoBleedingEdge/EmbedRuntime/", StringComparison.Ordinal) || path.StartsWith("MonoBleedingEdge/etc/mono/", StringComparison.Ordinal))
                return backend == ScriptingImplementation.Mono2x && MatchesRuntimeTemplate(file, path, "mono");
            if (!path.StartsWith(data + "/", StringComparison.Ordinal))
                return false;
            string relative = path.Substring(data.Length + 1);
            if (relative == "app.info" || relative == "boot.config" || relative == "RuntimeInitializeOnLoads.json" || relative == "ScriptingAssemblies.json" ||
                relative == "globalgamemanagers" || relative == "data.unity3d" || relative == "il2cpp_data/Metadata/global-metadata.dat" ||
                relative == "Resources/unity default resources" || relative == "Resources/unity_builtin_extra")
                return true;
            if ((relative.StartsWith("Managed/", StringComparison.Ordinal) || relative.StartsWith("Plugins/", StringComparison.Ordinal)) &&
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return reported;
            return reported && Regex.IsMatch(relative,
                @"\A(?:level\d+|(?:globalgamemanagers|resources|sharedassets\d+)\.assets|.+\.(?:resS|resource)|il2cpp_data/Resources/[A-Za-z0-9_.-]+\.dat)\z",
                RegexOptions.CultureInvariant);
        }

        private static bool MatchesRuntimeTemplate(string file, string relative, string backend)
        {
            string template = Path.Combine(EditorApplication.applicationContentsPath,
                "PlaybackEngines/WindowsStandaloneSupport/Variations/win64_player_nondevelopment_" + backend, relative);
            if (!File.Exists(template))
                return false;
            using var input = File.OpenRead(template);
            return ProtectedAssetAudit.DigestFile(file) == AuthenticatedBundleStream.Hash(input, default);
        }

        private static List<EncryptedBundleRecord> SealGroups(string[] groupOrder, List<BundleSource> bundles, IEncryptedBundleKeySource keys,
            CandidateFiles candidate, string aa, Dictionary<string, FrozenFile> expected, List<Upload> uploads)
        {
            var streams = new Dictionary<BundleSource, (string Relative, FileStream File)>();
            var rows = new List<EncryptedBundleRecord>();
            // Snapshot group order and all key/route identities before asking the application for its first lease.
            var byGroup = bundles.Where(bundle => bundle.Binding != null).ToLookup(bundle => bundle.Binding.GroupGuid, StringComparer.Ordinal);
            foreach (var bundle in byGroup.SelectMany(group => group))
            {
                string relative = bundle.Relative ?? aa + EncryptedBundleBinding.EmbeddedFolder + "/" + bundle.Binding.GroupPath;
                streams.Add(bundle, (relative, candidate.Create(relative)));
            }
            foreach (string guid in groupOrder)
            {
                string keyId = byGroup[guid].First().Binding.KeyId;
                using var lease = EncryptedBundleKeyLease.Rent(keys, keyId) ?? throw new EncryptedBundleKeyUnavailableException(keyId);
                foreach (var bundle in byGroup[guid])
                {
                    var (relative, output) = streams[bundle];
                    using var input = OwnedWindowsFiles.OpenRead(bundle.File.Path);
                    try
                    {
                        AuthenticatedBundleStream.Seal(input, output, bundle.Binding, lease);
                    }
                    catch (InvalidDataException error)
                    {
                        throw new InvalidDataException("Audited plaintext Bundle changed: " + bundle.Binding.GroupPath, error);
                    }
                    var row = new EncryptedBundleRecord(bundle.Binding, output.Length, AuthenticatedBundleStream.Hash(output, default));
                    rows.Add(row);
                    expected.Add(relative, new FrozenFile(null, row.CipherLength, row.CipherSha256));
                    if (bundle.Url != null)
                        uploads.Add(new Upload(relative, bundle.Url, ProtectedCategory));
                }
            }
            return rows;
        }

        private static void CheckLooseRoots(IEnumerable<GroupPlan> plans, List<BundleSource> bundles)
        {
            foreach (var group in plans.Where(plan => !plan.IsOrdinaryLocal).GroupBy(plan => plan.Root, StringComparer.OrdinalIgnoreCase))
            {
                var expected = new HashSet<string>(bundles.Where(bundle => bundle.StagedPath != null && SamePath(bundle.Plan.Root, group.Key))
                    .Select(bundle => bundle.StagedPath),
                    StringComparer.Ordinal);
                if (ProtectedAssetAudit.EnumerateOwnedFiles(group.Key).Any(file => !expected.Remove(file)) || expected.Count != 0)
                    throw new InvalidDataException("Loose Bundle roots contain missing or unowned output: " + group.Key);
            }
        }

        private static string CheckRemoteUrl(string value, bool allowLoopback)
        {
            if (value.Length > 1536 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.AbsoluteUri != value ||
                (uri.Scheme != Uri.UriSchemeHttps && !(allowLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1")) ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || uri.Host.Length == 0 ||
                (value.IndexOf('%') >= 0) || (uri.AbsolutePath.Length > 1 && uri.AbsolutePath.Substring(1).TrimEnd('/').Split('/').Any(part => part.Length == 0)))
                throw new InvalidDataException("Ordinary or protected Bundle route is not a canonical immutable HTTPS URL: " + value);
            return value;
        }

        private static void CheckUploadUrls(List<Upload> uploads)
        {
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (uploads.Any(row => !urls.Add(row.Url) || !files.Add(row.Relative)))
                throw new InvalidDataException("Upload files or exact target URLs are not unique.");
        }

        private static string EncodeReceipt(string application, string releaseId, string releasePrefix, EncryptedBundleDescription description,
            string descriptionHash, Dictionary<string, FrozenFile> files, List<Upload> uploads, JObject versioned = null)
        {
            var receipt = new JObject
            {
                ["version"] = EncryptedBundleBinding.Version,
                ["application"] = application,
                ["platform"] = EncryptedBundleBinding.Platform,
                ["releaseId"] = releaseId,
                ["releasePrefix"] = releasePrefix,
                ["catalog"] = description.CatalogName,
                ["catalogSha256"] = description.CatalogSha256,
                ["settingsSha256"] = description.SettingsSha256,
                ["routesSha256"] = description.RoutesSha256,
                ["descriptionSha256"] = descriptionHash,
                ["files"] = new JArray(files.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => FileRow(row.Key, row.Value))),
                ["uploads"] = new JArray(uploads.OrderBy(row => row.Relative, StringComparer.Ordinal).Select(row =>
                {
                    var json = FileRow(row.Relative, files[row.Relative]);
                    json.Add("url", row.Url);
                    json.Add("category", row.Category);
                    return json;
                }))
            };
            if (versioned != null)
                receipt.Add("contentUpdate", versioned);
            return receipt.ToString(Formatting.Indented);
        }

        private static JObject FileRow(string relative, FrozenFile file) => new JObject
        {
            ["path"] = relative,
            ["bytes"] = file.Bytes,
            ["sha256"] = file.Sha256
        };

        private sealed class CandidateFiles : IDisposable
        {
            private readonly string _root;
            private readonly Dictionary<string, FileStream> _files = new Dictionary<string, FileStream>(StringComparer.Ordinal);
            private readonly HashSet<string> _dirs = new HashSet<string>(new[] { "Upload", "ContentState" }, StringComparer.Ordinal);

            internal CandidateFiles(string root, bool includePlayer = true)
            {
                _root = root;
                if (includePlayer)
                    _dirs.Add("Player");
                foreach (string dir in _dirs)
                    Directory.CreateDirectory(Path.Combine(_root, dir));
            }

            internal FrozenFile Copy(string relative, FrozenFile source)
            {
                CopyVerified(source, Create(relative));
                return source;
            }

            internal FileStream Create(string relative)
            {
                int slash = relative.LastIndexOf('/');
                while (slash >= 0)
                {
                    string dir = relative.Substring(0, slash);
                    if (_dirs.Add(dir))
                        Directory.CreateDirectory(Path.Combine(_root, dir));
                    slash = dir.LastIndexOf('/');
                }
                var file = OwnedWindowsFiles.CreateTemporaryPinned(Path.Combine(_root, relative));
                _files.Add(relative, file);
                return file;
            }

            internal void Verify(Dictionary<string, FrozenFile> expectedFiles)
            {
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in ProtectedAssetAudit.EnumerateTree(_root, path =>
                {
                    if (Directory.Exists(path))
                    {
                        string relative = ProtectedAssetAudit.Relative(_root, path);
                        if (!_dirs.Contains(relative) || !seenDirs.Add(relative))
                            throw new InvalidDataException("Staged delivery inventory contains an unexpected directory: " + relative);
                    }
                    return true;
                }))
                {
                    string relative = ProtectedAssetAudit.Relative(_root, file);
                    if (!seenFiles.Add(relative) || !expectedFiles.TryGetValue(relative, out var expected) ||
                        !_files.TryGetValue(relative, out var input))
                        throw new InvalidDataException("Staged delivery inventory contains an unexpected file: " + relative);
                    OwnedWindowsFiles.CheckPublicationFile(input);
                    CheckBytes(input, expected, "Staged delivery inventory bytes changed: " + relative);
                }
                if (seenFiles.Count != expectedFiles.Count || seenDirs.Count != _dirs.Count)
                    throw new InvalidDataException("Staged delivery inventory missed a frozen output.");
            }

            public void Dispose()
            {
                foreach (var file in _files.Values)
                    file.Dispose();
                _files.Clear();
            }
        }

        private static void CopyVerified(FrozenFile source, FileStream output)
        {
            using var input = OwnedWindowsFiles.OpenRead(source.Path);
            input.CopyTo(output);
            output.Flush(true);
            CheckBytes(output, source, "Frozen build input changed before copying: " + source.Path);
        }

        private static FrozenFile ReadFile(string path)
        {
            using var file = OwnedWindowsFiles.OpenRead(path);
            return new FrozenFile(path, file.Length, AuthenticatedBundleStream.Hash(file, default));
        }

        private static FrozenFile WriteFile(FileStream file, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            file.Write(utf8, 0, utf8.Length);
            file.Flush(true);
            return new FrozenFile(null, utf8.Length, AuthenticatedBundleStream.Hash(file, default));
        }

        private static void RequireFile(FrozenFile file, string error)
        {
            using var input = OwnedWindowsFiles.OpenRead(file.Path);
            CheckBytes(input, file, error + ": " + file.Path);
        }

        private static void CheckBytes(Stream input, FrozenFile file, string error)
        {
            if (input.Length != file.Bytes || AuthenticatedBundleStream.Hash(input, default) != file.Sha256)
                throw new InvalidDataException(error);
        }

        private static void RequireAbsent(string path)
        {
            try
            {
                File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            throw new InvalidDataException("Encrypted delivery requires a new, unoccupied output: " + path);
        }

        private static string LocalPath(string path)
        {
            string full = OwnedWindowsFiles.LocalAbsolute(path).TrimEnd('\\', '/');
            if (full.Length <= 2)
                throw new InvalidDataException("A volume root cannot be a delivery output.");
            foreach (string part in full.Substring(3).Split('\\'))
                ProtectedAssetAudit.CheckWindowsLeaf(part);
            return full;
        }

        private static string Volume(string physical) => physical.Substring(0, physical.IndexOf('}') + 1).ToUpperInvariant();
        private static string WithSeparator(string path) => path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        private static bool BelowOrEqual(string candidate, string parent) => WithSeparator(candidate).StartsWith(WithSeparator(parent), StringComparison.OrdinalIgnoreCase);
        private static bool SamePath(string left, string right) => WithSeparator(Path.GetFullPath(left)).Equals(WithSeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        private static bool StrictlyBelow(string candidate, string parent) => BelowOrEqual(candidate, parent) &&
            !WithSeparator(candidate).Equals(WithSeparator(parent), StringComparison.OrdinalIgnoreCase);

        private sealed class BuildOutputPaths : IDisposable
        {
            private readonly List<IDisposable> _owned = new List<IDisposable>();
            private readonly Dictionary<string, string> _identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal string Identity(string path)
            {
                if (_identities.TryGetValue(path, out string physical))
                    return physical;
                _owned.Add(OwnedWindowsFiles.InspectDirectory(path, out physical));
                _identities.Add(path, physical);
                return physical;
            }

            internal bool Overlap(string first, string second) => BelowOrEqual(Identity(first), Identity(second)) || BelowOrEqual(Identity(second), Identity(first));

            internal void CreateFresh(string path)
            {
                using (OwnedWindowsFiles.CreatePublicationDirectory(path))
                {
                }
                Prepare(path);
            }

            internal void Prepare(string path)
            {
                string expected = Identity(path);
                _owned.Add(OwnedWindowsFiles.PinDirectory(path, true));
                using var pin = OwnedWindowsFiles.InspectDirectory(path, out string actual);
                if (!WithSeparator(expected).Equals(WithSeparator(actual), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A build output moved outside its audited physical identity: " + path);
            }

            public void Dispose()
            {
                for (int i = _owned.Count - 1; i >= 0; i--)
                    _owned[i].Dispose();
                _owned.Clear();
            }
        }

        private sealed class GroupPlan
        {
            internal readonly string Guid;
            internal readonly string Root;
            internal readonly string LoadPrefix;
            internal readonly string ProviderId;
            internal readonly EncryptedBundleDeliveryMode? Mode;
            internal readonly string KeyId;
            internal readonly bool IsOrdinaryLocal;

            internal bool IsOrdinaryRemote => Mode == null && !IsOrdinaryLocal;

            internal GroupPlan(string guid, string root, string load, string providerId, EncryptedBundleDeliveryMode? mode, string keyId, bool local)
            {
                Guid = guid;
                Root = root;
                LoadPrefix = load;
                ProviderId = providerId;
                Mode = mode;
                KeyId = keyId;
                IsOrdinaryLocal = local;
            }
        }

        private sealed class BundleSource
        {
            internal readonly GroupPlan Plan;
            internal readonly FrozenFile File;
            internal readonly EncryptedBundleBinding Binding;
            internal readonly string Relative;
            internal readonly string Url;
            internal readonly ResourceContentArtifact RetainedOrdinary;
            internal readonly string StagedPath;

            internal readonly EncryptedPackedBuilder.PackedBundle Packed;

            internal BundleSource(GroupPlan plan, FrozenFile file, EncryptedBundleBinding binding, string relative, string url,
                EncryptedPackedBuilder.PackedBundle packed, ResourceContentArtifact retainedOrdinary = null, string stagedPath = null)
            {
                Plan = plan;
                File = file;
                Binding = binding;
                Relative = relative;
                Url = url;
                Packed = packed;
                RetainedOrdinary = retainedOrdinary;
                StagedPath = retainedOrdinary == null ? file.Path : stagedPath;
            }
        }

        private readonly struct FrozenFile
        {
            internal readonly string Path;
            internal readonly long Bytes;
            internal readonly string Sha256;

            internal FrozenFile(string path, long bytes, string sha256)
            {
                Path = path;
                Bytes = bytes;
                Sha256 = sha256;
            }
        }

        private readonly struct Upload
        {
            internal readonly string Relative;
            internal readonly string Url;
            internal readonly string Category;

            internal Upload(string relative, string url, string category)
            {
                Relative = relative;
                Url = url;
                Category = category;
            }
        }
    }
}
