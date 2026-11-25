using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.BuildPipelineTasks;
using UnityEditor.AddressableAssets.Build.Layout;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Editor.Resource;
using UnityPackage = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Resource.Encryption.Editor
{
    /// <summary>Audited files ready for content manifest signing and placement.</summary>
    public sealed class EncryptedContentBuild
    {
        private readonly IReadOnlyList<(ResourceContentArtifact Artifact, string File, string InstalledPath)> _files;
        private readonly bool _loopback;

        internal EncryptedContentBuild(string root, string releaseId, string settingsHash, string catalogRoute,
            string catalogPath, string descriptionRoute, string descriptionPath, string settingsPath, string statePath,
            string layoutPath, List<(ResourceContentArtifact, string, string)> files, bool loopback)
        {
            ReleaseRoot = root;
            ReleaseId = releaseId;
            SettingsSha256 = settingsHash;
            CatalogRoute = catalogRoute;
            CatalogPath = Path.Combine(root, catalogPath);
            DescriptionRoute = descriptionRoute;
            DescriptionPath = Path.Combine(root, descriptionPath);
            StartupSettingsPath = settingsPath == null ? null : Path.Combine(root, settingsPath);
            ContentStatePath = statePath == null ? null : Path.Combine(root, statePath);
            BuildLayoutPath = Path.Combine(root, layoutPath);
            BundleRoutes = Array.AsReadOnly(files.Where(file => file.Item1.Kind is ResourceContentArtifactKind.Bundle or ResourceContentArtifactKind.Ciphertext)
                .Select(file => file.Item1.Route).ToArray());
            _files = files.AsReadOnly();
            _loopback = loopback;
        }

        public string ReleaseRoot
        {
            get;
        }
        public string ReleaseId
        {
            get;
        }
        public string SettingsSha256
        {
            get;
        }
        public string CatalogRoute
        {
            get;
        }
        public string CatalogPath
        {
            get;
        }
        public string DescriptionRoute
        {
            get;
        }
        public string DescriptionPath
        {
            get;
        }
        public string StartupSettingsPath
        {
            get;
        }
        public string ContentStatePath
        {
            get;
        }
        public string BuildLayoutPath
        {
            get;
        }
        public BuildReport PlayerReport
        {
            get; internal set;
        }
        public string UploadRoot => Path.Combine(ReleaseRoot, "Upload");
        public IReadOnlyList<string> BundleRoutes
        {
            get;
        }
        internal Action VerifyStaged;

        /// <summary>Rechecks the published bytes and maps installation files and immutable upload sources.</summary>
        public ResourceContentArtifact[] CreateArtifacts(string immutableReleaseUrl = null)
        {
            string prefix = immutableReleaseUrl == null ? null :
                EncryptedBundleBinding.ValidatePrefix(immutableReleaseUrl, _loopback);
            if (prefix != null && !prefix.EndsWith("/" + ReleaseId + "/", StringComparison.Ordinal))
                throw new InvalidDataException("The immutable artifact directory must end in its release identity.");
            var verify = VerifyStaged;
            verify?.Invoke();
            var artifacts = new List<ResourceContentArtifact>(_files.Count);
            foreach (var row in _files)
            {
                var artifact = row.Artifact;
                if (verify == null)
                {
                    // A retained artifact carries the absolute path of its sealed baseline file.
                    using var input = OwnedWindowsFiles.OpenRead(Path.Combine(ReleaseRoot, row.File));
                    if (input.Length != artifact.Length || AuthenticatedBundleStream.Hash(input, default) != artifact.Sha256)
                        throw new InvalidDataException("Audited release artifact bytes changed: " + row.File);
                }
                string source = artifact.Source ?? (row.InstalledPath != null
                    ? "install:" + row.InstalledPath
                    : prefix == null
                        ? throw new InvalidDataException("Supply the immutable upload directory for " + row.File)
                        : prefix + row.File.Substring("Upload/".Length));
                artifacts.Add(new ResourceContentArtifact(artifact.Route, artifact.OriginReleaseId, source,
                    artifact.Kind, artifact.Length, artifact.Sha256, artifact.Dependencies));
            }
            return artifacts.ToArray();
        }
    }

    public static partial class EncryptedBundleExporter
    {
        private const string BaselineReceiptName = "resource-content-baseline.json";

        /// <summary>Builds and freezes one transaction-capable encrypted Player installation and its original official state.</summary>
        public static EncryptedContentBuild BuildVersionedBaseline(AddressableAssetSettings settings,
            IReadOnlyList<AddressableAssetGroup> groups, BuildPlayerOptions player, string applicationNamespace,
            string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys,
            Func<EncryptedContentBuild, (byte[] Payload, byte[] Signature)> configurePlayer) =>
            BuildVersionedBaselineCore(settings, groups, player, applicationNamespace, privateRoot, releaseRoot, keys, false,
                configurePlayer ?? throw new ArgumentNullException(nameof(configurePlayer)));

        internal static EncryptedContentBuild BuildVersionedBaselineCore(AddressableAssetSettings settings,
            IReadOnlyList<AddressableAssetGroup> groups, BuildPlayerOptions player, string applicationNamespace,
            string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys, bool allowLoopback,
            Func<EncryptedContentBuild, (byte[] Payload, byte[] Signature)> configurePlayer = null) =>
            RunBuild(settings, groups, keys, () =>
            {
                ResourceEditorOperations.ValidateBuild(settings);
                var versioned = new VersionedRelease { ConfigurePlayer = configurePlayer };
                BuildRelease(settings, groups.ToArray(), player, applicationNamespace, privateRoot, releaseRoot, keys, allowLoopback, versioned);
                return versioned.Result;
            });

        /// <summary>Builds content from an encrypted Player baseline when no ordinary remote Bundles are retained.</summary>
        public static EncryptedContentBuild BuildVersionedUpdate(AddressableAssetSettings settings,
            IReadOnlyList<AddressableAssetGroup> groups, string baselineReleaseRoot, string privateRoot,
            string releaseRoot, IEncryptedBundleKeySource keys, IEnumerable<Type> additionalPlayerAssetTypes = null) =>
            BuildVersionedUpdateCore(settings, groups, baselineReleaseRoot, privateRoot, releaseRoot, keys, false, additionalPlayerAssetTypes);

        /// <summary>Builds content from an encrypted Player baseline and verifies the signed sources for retained ordinary remote Bundles.</summary>
        public static EncryptedContentBuild BuildVersionedUpdate(AddressableAssetSettings settings,
            IReadOnlyList<AddressableAssetGroup> groups, ResourceContentManifestTrust baselineTrust, string baselineReleaseRoot,
            string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys, IEnumerable<Type> additionalPlayerAssetTypes = null) =>
            BuildVersionedUpdateCore(settings, groups, baselineReleaseRoot, privateRoot, releaseRoot, keys, false,
                additionalPlayerAssetTypes, baselineTrust ?? throw new ArgumentNullException(nameof(baselineTrust)));

        internal static EncryptedContentBuild BuildVersionedUpdateCore(AddressableAssetSettings settings,
            IReadOnlyList<AddressableAssetGroup> groups, string baselineReleaseRoot, string privateRoot,
            string releaseRoot, IEncryptedBundleKeySource keys, bool allowLoopback, IEnumerable<Type> additionalPlayerAssetTypes = null,
            ResourceContentManifestTrust baselineTrust = null) =>
            RunBuild(settings, groups, keys,
                () => BuildUpdate(settings, groups.ToArray(), baselineReleaseRoot, privateRoot, releaseRoot, keys, allowLoopback,
                    additionalPlayerAssetTypes, baselineTrust));

        private static TResult RunBuild<TResult>(AddressableAssetSettings settings, IReadOnlyList<AddressableAssetGroup> groups,
            IEncryptedBundleKeySource keys, Func<TResult> build)
        {
            if (!settings || groups == null || groups.Count == 0 || groups.Count > 4096 || keys == null)
                throw new ArgumentException("Select the settings, protected groups and authoring key source.");
            if (_isBuilding)
                throw new InvalidOperationException("Encrypted delivery does not allow nested builds.");
            _isBuilding = true;
            try
            {
                return build();
            }
            finally
            {
                _isBuilding = false;
            }
        }

        private static EncryptedContentBuild BuildUpdate(AddressableAssetSettings settings, AddressableAssetGroup[] groups,
            string baselineReleaseRoot, string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys, bool allowLoopback,
            IEnumerable<Type> additionalPlayerAssetTypes, ResourceContentManifestTrust baselineTrust)
        {
            ResourceEditorOperations.ValidateBuild(settings);
            baselineReleaseRoot = LocalPath(baselineReleaseRoot);
            privateRoot = LocalPath(privateRoot);
            releaseRoot = LocalPath(releaseRoot);
            string releaseId = EncryptedBundleBinding.Token(Path.GetFileName(releaseRoot), "releaseId");
            string project = Path.GetDirectoryName(Application.dataPath);
            string[] reserved = { Application.dataPath, Path.Combine(project, "Packages"), Path.Combine(project, "PackageSources"),
                Path.Combine(project, "ProjectSettings"), Path.Combine(project, "Library"), Path.Combine(project, "Temp"), baselineReleaseRoot };
            using var paths = new BuildOutputPaths();
            if (reserved.Any(root => paths.Overlap(root, privateRoot) || paths.Overlap(root, releaseRoot)) ||
                paths.Overlap(privateRoot, releaseRoot) || Volume(paths.Identity(privateRoot)) != Volume(paths.Identity(releaseRoot)))
                throw new InvalidDataException("Use fresh disjoint private and release roots on one local volume, outside the archived A and project inputs.");
            RequireAbsent(privateRoot);
            RequireAbsent(releaseRoot);
            using var privateParent = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(privateRoot), false);
            using var releaseParent = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(releaseRoot), false);
            using var baselinePin = OwnedWindowsFiles.PinDirectory(baselineReleaseRoot, false);
            string physicalRelease = paths.Identity(releaseRoot);
            string baselinePath = Path.Combine(baselineReleaseRoot, "ContentState", BaselineReceiptName);
            var baseline = ResourceContentUpdateAuthoring.ReadBaseline(baselinePath);
            using var receiptInput = OwnedWindowsFiles.OpenRead(Path.Combine(baselineReleaseRoot, ReceiptName));
            var receipt = JObject.Parse(EncryptedDeliverySession.ReadText(receiptInput),
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (!settings.BuildRemoteCatalog || baseline.Target != EditorUserBuildSettings.activeBuildTarget ||
                baseline.EditorVersion != Application.unityVersion || baseline.AddressablesVersion != UnityPackage.FindForAssembly(typeof(Addressables).Assembly)?.version ||
                baseline.ProfileId != settings.activeProfileId || baseline.SettingsGuid != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)) ||
                baseline.RemoteCatalogLoadPath != settings.RemoteCatalogLoadPath.GetValue(settings) ||
                (int?)receipt["contentUpdate"]?["playerBuilds"] != 1 || (string)receipt["releaseId"] == releaseId ||
                (string)receipt["settingsSha256"] != baseline.StartupSettingsSha256 || (string)receipt["catalogSha256"] != baseline.CatalogSha256)
                throw new InvalidDataException("Select the original versioned encrypted Player baseline for these settings, Profile, target, Editor and logical Catalog path.");
            var scenes = ((JArray)receipt["contentUpdate"]["scenes"]).Values<string>().ToArray();
            string catalogName = Path.GetFileName(baseline.CatalogPath);
            var descriptionRow = ((JArray)receipt["files"]).Single(row => ((string)row["path"]).EndsWith("/aa/" + EncryptedBundleDescription.FileName, StringComparison.Ordinal));
            string descriptionRelative = (string)descriptionRow["path"];
            string installedAa = descriptionRelative.Substring(0, descriptionRelative.Length - EncryptedBundleDescription.FileName.Length);
            if (!installedAa.StartsWith("Player/", StringComparison.Ordinal) || installedAa.Split('/').Any(segment => segment is "." or "..") ||
                installedAa.IndexOf('\\') >= 0 || installedAa.IndexOf(':') >= 0)
                throw new InvalidDataException("The archived descriptor is outside the Player installation.");
            string descriptionPath = Path.Combine(baselineReleaseRoot, descriptionRelative);
            EncryptedBundleDescription original;
            using (var source = OwnedWindowsFiles.OpenRead(descriptionPath))
            {
                CheckBytes(source, new FrozenFile(descriptionPath, (long)descriptionRow["bytes"], (string)receipt["descriptionSha256"]),
                    "The original encrypted descriptor changed.");
                source.Position = 0;
                original = EncryptedBundleDescription.Parse(EncryptedDeliverySession.ReadText(source), allowLoopback);
            }
            string installedSettings = Path.Combine(baselineReleaseRoot, installedAa + SettingsName);
            string installedCatalog = Path.Combine(baselineReleaseRoot, installedAa + catalogName);
            using var catalogPin = OwnedWindowsFiles.OpenRead(installedCatalog);
            if (ReadFile(installedSettings).Sha256 != baseline.StartupSettingsSha256 || ReadFile(installedCatalog).Sha256 != baseline.CatalogSha256 ||
                original.CatalogName != catalogName || original.CatalogSha256 != baseline.CatalogSha256 ||
                original.SettingsSha256 != baseline.StartupSettingsSha256 ||
                original.ReleaseId != (string)receipt["releaseId"] || original.Application != (string)receipt["application"])
                throw new InvalidDataException("The original encrypted descriptor no longer matches the installed A Catalog and settings.");

            string library = Path.Combine(privateRoot, "Official");
            string relativeLibrary = Path.GetRelativePath(project, library);
            if (Path.IsPathRooted(relativeLibrary))
                throw new InvalidDataException("Official content updates require private staging on the project's local volume.");
            string buildReports = Path.Combine(library, "BuildReports");
            string scratch = Path.Combine(privateRoot, "Sbp");
            string previousLibrary = Addressables.LibraryPath;
            string previousReports = Addressables.BuildReportPath;
            string previousScratch = settings.buildSettings.bundleBuildPath;
            const string buildPathProperty = "UnityEngine.AddressableAssets.Addressables.BuildPath";
            const string runtimePathProperty = "UnityEngine.AddressableAssets.Addressables.RuntimePath";
            string previousBuildPath = AddressablesRuntimeProperties.EvaluateProperty(buildPathProperty);
            string previousRuntimePath = AddressablesRuntimeProperties.EvaluateProperty(runtimePathProperty);
            var task = typeof(BuildLayoutGenerationTask);
            var layoutFilePath = RequireField(task, "m_LayoutFilePath", BindingFlags.Static);
            var reportsFilePath = RequireField(task, "m_ReportsFilePath", BindingFlags.Static);
            var scratchPath = RequireField(settings.buildSettings.GetType(), "m_BundleBuildPath", BindingFlags.Instance);
            object previousLayoutFile = layoutFilePath.GetValue(null);
            object previousReportsFile = reportsFilePath.GetValue(null);
            try
            {
                // Addressables 2.10 prepends the project directory even when writing an absolute LibraryPath.
                Addressables.LibraryPath = WithSeparator(relativeLibrary).Replace('\\', '/');
                Addressables.BuildReportPath = WithSeparator(buildReports).Replace('\\', '/');
                AddressablesRuntimeProperties.SetPropertyValue(buildPathProperty, Addressables.BuildPath);
                AddressablesRuntimeProperties.SetPropertyValue(runtimePathProperty, Addressables.RuntimePath);
                layoutFilePath.SetValue(null, Path.Combine(library, "buildlayout"));
                reportsFilePath.SetValue(null, Path.Combine(buildReports, "buildlayout"));
                scratchPath.SetValue(settings.buildSettings, scratch);
                string buildPath = Path.GetFullPath(Addressables.BuildPath);
                if (!StrictlyBelow(buildPath, library))
                    throw new InvalidDataException("The official Addressables output did not resolve to the private update root.");
                string stateFolder = LocalPath(settings.profileSettings.EvaluateString(settings.activeProfileId, settings.ContentStateBuildPath));
                if (!StrictlyBelow(stateFolder, privateRoot) || !StrictlyBelow(paths.Identity(stateFolder), paths.Identity(privateRoot)))
                    throw new InvalidDataException("Use a fresh content state build directory within the private update root.");
                var plans = PlanGroups(settings, groups, paths, privateRoot, releaseRoot, stateFolder, buildPath, reserved,
                    original.Application, releaseId, allowLoopback, out string releasePrefix, privateLocal: true);
                string officialCatalogRoot = CheckOfficialCatalogRoot(settings, paths, privateRoot, stateFolder, plans, allowLoopback);
                string deliveryFolder = Path.Combine(privateRoot, ".yl-delivery-catalog");
                var privateIntermediateRoots = new[] { library, scratch, deliveryFolder };
                if (privateIntermediateRoots.Any(root => paths.Overlap(root, stateFolder) || paths.Overlap(root, officialCatalogRoot) ||
                    plans.Values.Any(plan => !plan.IsOrdinaryLocal && paths.Overlap(root, plan.Root))))
                    throw new InvalidDataException("An authored B staging path intersects reserved official or delivery output.");
                var removalDirectories = groups.Select(group =>
                {
                    var schema = group.GetSchema<BundledAssetGroupSchema>();
                    var plan = plans[group.Guid];
                    // Both pinned updaters form a deletion path by replacing the authored LoadPath string.
                    // Keep the group-qualified path private and empty. The audited SBP bytes are staged separately.
                    string directory = LocalPath((plan.LoadPrefix + plan.Guid + "/")
                        .Replace(schema.LoadPath.GetValue(settings), schema.BuildPath.GetValue(settings)));
                    if (!StrictlyBelow(paths.Identity(directory), paths.Identity(privateRoot)) ||
                        privateIntermediateRoots.Append(stateFolder).Append(officialCatalogRoot).Any(root => paths.Overlap(root, directory)) ||
                        plans.Values.Any(other => other != plan && paths.Overlap(other.Root, directory)) ||
                        !RequireDistinctRemovalPath(directory, plan.Root, paths))
                        throw new InvalidDataException("The protected group would resolve an ambiguous official update deletion path: " + group.Name);
                    return directory;
                }).ToArray();
                string layoutPath = Path.Combine(library, LayoutName);
                var audit = new ProtectedAssetAudit(groups, scenes);
                paths.CreateFresh(privateRoot);
                paths.CreateFresh(deliveryFolder);
                foreach (string root in plans.Values.Where(plan => plan.IsOrdinaryRemote).Select(plan => plan.Root).Distinct(StringComparer.OrdinalIgnoreCase))
                    paths.CreateFresh(root);
                foreach (string root in new[] { library, buildReports, scratch, buildPath, stateFolder, officialCatalogRoot }
                    .Concat(plans.Values.Select(plan => plan.Root)).Concat(removalDirectories))
                    paths.Prepare(root);
                var builder = ScriptableObject.CreateInstance<EncryptedPackedBuilder>();
                var sources = new Dictionary<string, FrozenFile>(StringComparer.OrdinalIgnoreCase);
                List<BundleSource> bundles;
                string versionedCatalog = Path.Combine(deliveryFolder, catalogName);
                FrozenFile layoutFile;
                string routesHash;
                try
                {
                    builder.ProtectedGroups = groups;
                    builder.PlayerScenes = scenes;
                    builder.ApplicationNamespace = original.Application;
                    builder.ReleaseId = releaseId;
                    builder.ReleasePrefix = releasePrefix;
                    builder.Loopback = allowLoopback;
                    builder.Versioned = true;
                    builder.PreviousDelivery = original;
                    builder.PreviousCatalogPath = installedCatalog;
                    builder.DeliveryCatalogPath = versionedCatalog;
                    var output = BuildPacked(settings, builder, baseline.ContentStatePath);
                    if (output == null || !output.IsUpdateContentBuild || !string.IsNullOrEmpty(output.Error) ||
                        builder.CatalogName != catalogName || !File.Exists(layoutPath) || !File.Exists(versionedCatalog))
                        throw new InvalidDataException("Official update did not produce the expected Catalog and Build Layout.");
                    var official = ContentUpdateScript.LoadContentState(baseline.ContentStatePath);
                    VerifyOfficialCatalog(settings, official, officialCatalogRoot, Path.Combine(buildPath, catalogName), catalogName);
                    layoutFile = ReadFile(layoutPath);
                    var layout = new BuildLayout();
                    using (var input = OwnedWindowsFiles.OpenRead(layoutPath))
                    using (var text = new StreamReader(input, new UTF8Encoding(false, true)))
                        EditorJsonUtility.FromJsonOverwrite(text.ReadToEnd(), layout);
                    audit.AuditLayout(layout, builder);
                    audit.AuditInstalledDependencies();
                    var retained = builder.Bundles.Values.Where(bundle => bundle.RetainedOrdinaryRoute != null).ToArray();
                    var retainedOrdinary = retained.Length == 0 ? null : VerifyRetainedOrdinaryBundles(retained, baselineTrust,
                        baselineReleaseRoot, installedAa, receipt, original, allowLoopback);
                    bundles = FreezeBundles(output, builder, plans, scenes, buildPath, versionedCatalog,
                        new Dictionary<string, FrozenFile>(StringComparer.Ordinal), sources, allowLoopback, retainedOrdinary);
                    CheckLooseRoots(plans.Values, bundles);
                    routesHash = builder.RoutesSha256;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(builder);
                }

                string stage = Path.Combine(privateRoot, ".yl-release-" + Guid.NewGuid().ToString("N"));
                using var ownedStage = OwnedWindowsFiles.CreatePublicationDirectory(stage);
                using var candidate = new CandidateFiles(stage, includePlayer: false);
                string catalogRelative = "Upload/" + catalogName;
                string descriptorRelative = "Upload/" + EncryptedBundleDescription.FileName;
                string layoutRelative = "ContentState/" + LayoutName;
                var expected = new Dictionary<string, FrozenFile>(StringComparer.Ordinal)
                {
                    [catalogRelative] = candidate.Copy(catalogRelative, ReadFile(versionedCatalog)),
                    [layoutRelative] = candidate.Copy(layoutRelative, layoutFile)
                };
                var uploads = new List<Upload>();
                foreach (var bundle in bundles.Where(bundle => bundle.Binding == null && bundle.RetainedOrdinary == null))
                {
                    string relative = VersionedBundlePath(bundle, "Upload/");
                    expected.Add(relative, candidate.Copy(relative, bundle.File));
                    if (bundle.Url != null)
                        uploads.Add(new Upload(relative, bundle.Url, OrdinaryCategory));
                }
                using var descriptorOutput = candidate.Create(descriptorRelative);
                var rows = SealGroups(groups.Select(group => group.Guid).ToArray(), bundles, keys, candidate, "Upload/", expected, uploads);
                var description = new EncryptedBundleDescription(catalogName, expected[catalogRelative].Sha256,
                    baseline.StartupSettingsSha256, routesHash, rows);
                expected.Add(descriptorRelative, WriteFile(descriptorOutput, description.Encode()));
                ResourceContentUpdateAuthoring.RequireCompatibleCatalog(versionedCatalog, installedCatalog, Addressables.RuntimePath,
                    additionalPlayerAssetTypes, CatalogTransferRoutes(description), CatalogTransferRoutes(original));
                foreach (var file in sources.Values.Append(layoutFile).Append(expected[catalogRelative]))
                    RequireFile(file, "Audited update source changed");
                CheckLooseRoots(plans.Values, bundles);
                CheckUploadUrls(uploads);
                ResourceContentUpdateAuthoring.ReadBaseline(baselinePath);
                if (ReadFile(installedSettings).Sha256 != baseline.StartupSettingsSha256 || ReadFile(installedCatalog).Sha256 != baseline.CatalogSha256)
                    throw new InvalidDataException("The installed A settings or Catalog changed during the update.");
                var metadata = new JObject
                {
                    ["playerBuilds"] = 0,
                    ["baselineReleaseId"] = original.ReleaseId,
                    ["baselineContentStateSha256"] = baseline.ContentStateSha256,
                    ["officialPlayerVersion"] = baseline.PlayerVersion,
                    ["officialCatalogLoadPath"] = baseline.RemoteCatalogLoadPath,
                    ["protectedAssets"] = audit.Report()
                };
                var content = DescribeContent(releaseRoot, description, catalogRelative, descriptorRelative, null, null,
                    layoutRelative, bundles, "Upload/", expected, versionedCatalog, allowLoopback);
                string result = EncodeReceipt(original.Application, releaseId, releasePrefix, description, expected[descriptorRelative].Sha256,
                    expected, uploads, metadata);
                expected.Add(ReceiptName, WriteFile(candidate.Create(ReceiptName), result));
                candidate.Verify(expected);
                candidate.Dispose();
                OwnedWindowsFiles.Publish(ownedStage, physicalRelease);
                return content;
            }
            finally
            {
                scratchPath.SetValue(settings.buildSettings, previousScratch);
                reportsFilePath.SetValue(null, previousReportsFile);
                layoutFilePath.SetValue(null, previousLayoutFile);
                Addressables.BuildReportPath = previousReports;
                Addressables.LibraryPath = previousLibrary;
                AddressablesRuntimeProperties.SetPropertyValue(buildPathProperty, previousBuildPath);
                AddressablesRuntimeProperties.SetPropertyValue(runtimePathProperty, previousRuntimePath);
            }
        }

        private static AddressablesPlayerBuildResult BuildOfficialUpdate(AddressableAssetSettings settings, EncryptedPackedBuilder builder, string state)
        {
            // ActivePlayerDataBuilderIndex's setter dirties the authored settings and raises a project event.
            // Scope the transient builder to this official call without writing the settings asset.
            var index = RequireField(typeof(AddressableAssetSettings), "m_ActivePlayerDataBuilderIndex", BindingFlags.Instance);
            var builders = settings.DataBuilders;
            int previous = settings.ActivePlayerDataBuilderIndex;
            int position = builders.Count;
            builders.Add(builder);
            try
            {
                index.SetValue(settings, position);
                return ResourceEditorOperations.BuildContentUpdate(settings, state).Result;
            }
            finally
            {
                index.SetValue(settings, previous);
                builders.Remove(builder);
            }
        }

        private static FieldInfo RequireField(Type type, string name, BindingFlags flags) =>
            type.GetField(name, flags | BindingFlags.NonPublic)
                ?? throw new InvalidDataException("The pinned Addressables field is unavailable: " + name);

        private static bool RequireDistinctRemovalPath(string removal, string packed, BuildOutputPaths paths) =>
            !paths.Overlap(removal, packed) || StrictlyBelow(paths.Identity(removal), paths.Identity(packed));

        private static Dictionary<string, (ResourceContentArtifact Artifact, FrozenFile File)> VerifyRetainedOrdinaryBundles(
            IEnumerable<EncryptedPackedBuilder.PackedBundle> bundles, ResourceContentManifestTrust trust,
            string baselineRoot, string installedAa, JObject receipt, EncryptedBundleDescription original, bool allowLoopback)
        {
            if (trust == null)
                throw new InvalidDataException("Supply baselineTrust to verify signed A artifacts for retained ordinary remote Bundles.");
            string aa = Path.Combine(baselineRoot, installedAa);
            var manifest = ResourceContentManifest.Verify(
                ReadManifestFile("yl-content-manifest.json", ResourceContentManifest.MaximumPayloadBytes),
                ReadManifestFile("yl-content-manifest.sig", ResourceContentManifest.MaximumSignatureEnvelopeBytes), trust);
            string catalogPath = Path.Combine(aa, original.CatalogName);
            if (manifest.ReleaseId != original.ReleaseId || manifest.Application != original.Application ||
                manifest.Platform != EncryptedBundleBinding.Platform || manifest.SettingsSha256 != original.SettingsSha256 ||
                manifest.CatalogRoute != FrozenAddressablesRoutes.RuntimeToken + "/" + original.CatalogName ||
                manifest.DescriptionRoute != EncryptedBundleDescription.FileName)
                throw new InvalidDataException("The signed manifest is not the original encrypted Player baseline.");
            RequireInstalledArtifact(manifest.CatalogRoute, original.CatalogName, original.CatalogSha256);
            RequireInstalledArtifact(manifest.DescriptionRoute, EncryptedBundleDescription.FileName, (string)receipt["descriptionSha256"]);
            var routes = ResourceContentUpdateAuthoring.InspectCatalog(catalogPath, Addressables.RuntimePath, CatalogTransferRoutes(original));
            if (receipt["uploads"] is not JArray uploadRows)
                throw new InvalidDataException("The archived encrypted release is missing its upload inventory.");
            var uploads = uploadRows.Where(row => (string)row["category"] == OrdinaryCategory)
                .ToLookup(row => (string)row["url"], StringComparer.Ordinal);
            var result = new Dictionary<string, (ResourceContentArtifact, FrozenFile)>(StringComparer.Ordinal);
            foreach (var bundle in bundles)
            {
                string route = CheckRemoteUrl(bundle.RetainedOrdinaryRoute, allowLoopback);
                string relative = "Upload/ordinary/" + bundle.FileName;
                var upload = uploads[route].SingleOrDefault();
                if (!manifest.Artifacts.TryGetValue(route, out var artifact) || artifact.Kind != ResourceContentArtifactKind.Bundle ||
                    artifact.OriginReleaseId != original.ReleaseId || artifact.Sha256 != bundle.SbpSha256 ||
                    bundle.PreviousOrdinary.Data is not AssetBundleRequestOptions options ||
                    options.BundleSize != artifact.Length ||
                    !routes.TryGetValue(route, out var dependencies) ||
                    !artifact.Dependencies.OrderBy(dependency => dependency, StringComparer.Ordinal)
                        .SequenceEqual(dependencies.OrderBy(dependency => dependency, StringComparer.Ordinal)) ||
                    upload == null || (string)upload["path"] != relative || (long?)upload["bytes"] != artifact.Length ||
                    (string)upload["sha256"] != artifact.Sha256 ||
                    !CheckRemoteUrl(artifact.Source, allowLoopback).EndsWith("/" + original.ReleaseId + "/" +
                        relative.Substring("Upload/".Length), StringComparison.Ordinal))
                    throw new InvalidDataException("The retained ordinary Bundle differs from the signed A artifact or audited SBP bytes: " + route);
                string path = Path.Combine(baselineRoot, relative);
                var file = new FrozenFile(path, artifact.Length, artifact.Sha256);
                RequireFile(file, "Archived ordinary A Bundle changed");
                result.Add(route, (artifact, file));
            }
            return result;

            byte[] ReadManifestFile(string name, int maximumBytes)
            {
                using var input = OwnedWindowsFiles.OpenRead(Path.Combine(aa, name));
                if (input.Length <= 0 || input.Length > maximumBytes)
                    throw new InvalidDataException("The archived A manifest or signature is outside its size bound: " + name);
                using var reader = new BinaryReader(input);
                return reader.ReadBytes((int)input.Length);
            }

            void RequireInstalledArtifact(string route, string name, string sha256)
            {
                var artifact = manifest.Artifacts[route];
                if (artifact.OriginReleaseId != original.ReleaseId || artifact.Source != "install:" + name || artifact.Sha256 != sha256)
                    throw new InvalidDataException("The signed A manifest differs from the archived Player artifact: " + name);
                RequireFile(new FrozenFile(Path.Combine(aa, name), artifact.Length, artifact.Sha256), "Archived A Player artifact changed");
            }
        }

        private static string CheckOfficialCatalogRoot(AddressableAssetSettings settings, BuildOutputPaths paths, string privateRoot,
            string stateFolder, Dictionary<string, GroupPlan> plans, bool allowLoopback)
        {
            string root = LocalPath(settings.RemoteCatalogBuildPath.GetValue(settings));
            if (!StrictlyBelow(root, privateRoot) || !StrictlyBelow(paths.Identity(root), paths.Identity(privateRoot)) ||
                paths.Overlap(root, stateFolder) || plans.Values.Any(plan => paths.Overlap(root, plan.Root)))
                throw new InvalidDataException("The official Remote Catalog BuildPath must be a fresh private directory disjoint from the Bundle outputs and state.");
            CheckRemoteUrl(settings.RemoteCatalogLoadPath.GetValue(settings), allowLoopback);
            RequireAbsent(root);
            return root;
        }

        private static void VerifyOfficialCatalog(AddressableAssetSettings settings, AddressablesContentState state,
            string remoteRoot, string localCatalog, string catalogName)
        {
            if (state == null || state.editorVersion != Application.unityVersion ||
                state.remoteCatalogLoadPath != settings.RemoteCatalogLoadPath.GetValue(settings) || string.IsNullOrEmpty(state.playerVersion))
                throw new InvalidDataException("The official state does not identify the original Player content version and logical Catalog path.");
            string name = settings.profileSettings.EvaluateString(settings.activeProfileId, "catalog_" + state.playerVersion);
            if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("The official remote Catalog name is not a file within the private output.");
            var remote = ReadFile(Path.Combine(remoteRoot, name + Path.GetExtension(catalogName)));
            var local = ReadFile(localCatalog);
            if (remote.Bytes != local.Bytes || remote.Sha256 != local.Sha256 || ReadFile(Path.Combine(remoteRoot, name + ".hash")).Bytes == 0)
                throw new InvalidDataException("The official remote and local Catalog outputs differ.");
        }

        private static string VersionedBundlePath(BundleSource bundle, string aa)
        {
            if (bundle.Relative != null)
                return bundle.Relative;
            if (bundle.Binding != null)
                return aa + EncryptedBundleBinding.EmbeddedFolder + "/" + bundle.Binding.GroupPath;
            string local = bundle.Packed.SourceId.Substring((FrozenAddressablesRoutes.RuntimeToken + "/").Length);
            return aa == "Upload/" ? aa + "ordinary-local/" + local : aa + local;
        }

        private static EncryptedContentBuild DescribeContent(string releaseRoot, EncryptedBundleDescription description,
            string catalogFile, string descriptorFile, string settingsFile, string stateFile, string layoutFile,
            List<BundleSource> bundles, string aa, Dictionary<string, FrozenFile> expected, string catalogInput, bool allowLoopback)
        {
            var files = new List<(ResourceContentArtifact, string, string)>();
            string catalogRoute = FrozenAddressablesRoutes.RuntimeToken + "/" + description.CatalogName;
            string descriptorRoute = EncryptedBundleDescription.FileName;
            var routes = ResourceContentUpdateAuthoring.InspectCatalog(catalogInput, Addressables.RuntimePath, CatalogTransferRoutes(description));
            if (routes.Count != bundles.Count)
                throw new InvalidDataException("The release artifacts and audited Catalog Bundle routes differ.");
            Add(catalogRoute, catalogFile, ResourceContentArtifactKind.Catalog, description.ReleaseId, Array.Empty<string>());
            Add(descriptorRoute, descriptorFile, ResourceContentArtifactKind.EncryptedDescription, description.ReleaseId, Array.Empty<string>());
            foreach (var bundle in bundles)
            {
                var packed = bundle.Packed;
                string route = packed.DeliverySourceId;
                if (!routes.TryGetValue(route, out var dependencies))
                    throw new InvalidDataException("The audited Catalog does not describe this Bundle: " + route);
                if (bundle.RetainedOrdinary is ResourceContentArtifact retained)
                {
                    files.Add((new ResourceContentArtifact(route, retained.OriginReleaseId, retained.Source, retained.Kind,
                        retained.Length, retained.Sha256, dependencies), bundle.File.Path, null));
                    continue;
                }
                string relative = VersionedBundlePath(bundle, aa);
                if (bundle.Binding == null)
                    Add(route, relative, ResourceContentArtifactKind.Bundle, description.ReleaseId, dependencies);
                else
                    Add(route, relative, ResourceContentArtifactKind.Ciphertext, bundle.Binding.ReleaseId, dependencies);
            }
            return new EncryptedContentBuild(releaseRoot, description.ReleaseId, description.SettingsSha256, catalogRoute,
                catalogFile, descriptorRoute, descriptorFile, settingsFile, stateFile, layoutFile, files, allowLoopback);

            void Add(string route, string relative, ResourceContentArtifactKind kind, string origin, string[] dependencies)
            {
                var file = expected[relative];
                string installed = settingsFile != null && relative.StartsWith(aa, StringComparison.Ordinal) ? relative.Substring(aa.Length) : null;
                files.Add((new ResourceContentArtifact(route, origin, null, kind, file.Bytes, file.Sha256, dependencies), relative, installed));
            }
        }

        private static Func<IResourceLocation, string> CatalogTransferRoutes(EncryptedBundleDescription description)
        {
            var bindings = description.Bundles.Select(row => row.Binding).ToArray();
            var routes = bindings.ToDictionary(binding => binding.SourceId, binding => binding.SourceId, StringComparer.Ordinal);
            foreach (var binding in bindings)
                routes.Add(FrozenAddressablesRoutes.MountId(binding), binding.SourceId);
            return location =>
            {
                if (location.ProviderId != description.ProviderId)
                    return null;
                string id = FrozenAddressablesRoutes.CanonicalId(location.InternalId, Addressables.RuntimePath);
                return routes.TryGetValue(id, out string route) ? route :
                    throw new InvalidDataException("Catalog declared an unbound protected transfer or mount: " + id);
            };
        }

        private sealed class VersionedRelease
        {
            internal ResourceContentBaseline Baseline;
            internal JObject Metadata;
            internal EncryptedContentBuild Result;
            internal Func<EncryptedContentBuild, (byte[] Payload, byte[] Signature)> ConfigurePlayer;
        }
    }
}
