using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// <summary>Builds and audits a fixed Player plus a separate immutable encrypted release directory.</summary>
    public static class EncryptedBundleExporter
    {
        /// <summary>Call from the author's Win64 release entry point. The key source must only provide current local CI key material.</summary>
        public static void Build(AddressableAssetSettings settings, AddressableAssetGroup group, BuildPlayerOptions player,
            string applicationNamespace, string privateRoot, string releaseRoot, IEncryptedBundleKeySource keys)
        {
            BuildCore(settings, group, player, applicationNamespace, privateRoot, releaseRoot, keys, false);
        }

        internal static EncryptedBundleDescription BuildCore(AddressableAssetSettings settings, AddressableAssetGroup group,
            BuildPlayerOptions player, string applicationNamespace, string privateRoot, string releaseRoot,
            IEncryptedBundleKeySource keys, bool allowLoopback)
        {
            if (!settings || !group || group.Settings != settings || keys == null)
                throw new ArgumentException("A group, its settings and an authoring key source are required.");
            var auth = group.GetSchema<EncryptedBundleGroupSchema>();
            var packed = group.GetSchema<BundledAssetGroupSchema>();
            if (!auth || !packed || !packed.IncludeInBuild || packed.Compression != BundledAssetGroupSchema.BundleCompressionMode.LZ4 ||
                packed.AssetBundleProviderType.Value != typeof(EncryptedAssetBundleProvider) || packed.BundledAssetProviderType.Value != typeof(BundledAssetProvider) ||
                packed.UseAssetBundleCache || packed.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash ||
                packed.AssetLoadMode != AssetLoadMode.RequestedAssetAndDependencies ||
                settings.groups.Any(other => other && other != group && (other.HasSchema<EncryptedBundleGroupSchema>() ||
                    other.GetSchema<BundledAssetGroupSchema>()?.AssetBundleProviderType.Value == typeof(EncryptedAssetBundleProvider))))
                throw new InvalidDataException("Choose exactly one remote LZ4 group with EncryptedAssetBundleProvider, hashed Bundle names, stock requested-asset loading, and no Unity Bundle cache.");

            string buildPath = Path.GetFullPath(Addressables.BuildPath);
            var serialized = new SerializedObject(settings);
            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            bool x64 = player.target == BuildTarget.StandaloneWindows64;
#if UNITY_6000_3_OR_NEWER
            x64 &= UnityEditor.WindowsStandalone.UserBuildSettings.architecture == OSArchitecture.x64;
#endif
            if (AddressableAssetSettingsDefaultObject.Settings != settings || settings.BuildRemoteCatalog || !settings.DisableCatalogUpdateOnStartup || settings.InitializationObjects.Count != 0 ||
                serialized.FindProperty("m_ExtractTypeTreeData")?.boolValue == true || serialized.FindProperty("m_BundleLocalCatalog")?.boolValue == true ||
                settings.BuildAddressablesWithPlayerBuild != AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer ||
                EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64 || !x64 ||
                (backend != ScriptingImplementation.IL2CPP && backend != ScriptingImplementation.Mono2x) ||
                (player.options & (BuildOptions.Development | BuildOptions.AutoRunPlayer | BuildOptions.BuildScriptsOnly | BuildOptions.AcceptExternalModificationsToPlayer)) != 0 ||
                player.scenes == null || player.scenes.Length == 0)
                throw new InvalidDataException("Encrypted release requires a fixed local Catalog, no startup update or initialization hooks, no Player content rebuild, and Windows x64 Release Mono or IL2CPP.");
            bool hasDirtyScene = Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                .Any(index => UnityEngine.SceneManagement.SceneManager.GetSceneAt(index).isDirty);
            if (hasDirtyScene)
                throw new InvalidOperationException("Save the selected build scenes before the encrypted build.");

            privateRoot = OwnedWindowsFiles.LocalAbsolute(privateRoot);
            releaseRoot = OwnedWindowsFiles.LocalAbsolute(releaseRoot);
            player.locationPathName = OwnedWindowsFiles.LocalAbsolute(player.locationPathName);
            string playerRoot = Path.GetDirectoryName(player.locationPathName);
            string project = Path.GetDirectoryName(Application.dataPath);
            using var paths = new BuildOutputPaths();
            string library = Path.Combine(project, "Library", "com.unity.addressables");
            string buildReports = Path.Combine(library, "BuildReports");
            string bundleScratch = Path.Combine(project, "Temp", "com.unity.addressables", "AssetBundles");
            if (!SamePath(Addressables.LibraryPath, library) || !SamePath(Addressables.BuildReportPath, buildReports) ||
                !SamePath(settings.buildSettings.bundleBuildPath, bundleScratch) || !StrictlyBelow(buildPath, Path.Combine(library, "aa")))
                throw new InvalidDataException("Official Addressables output path is not the project's fixed Library/Temp layout.");
            // Inspect even when there is no ordinary local group. The official builder always writes here.
            string[] officialRoots = { library, buildReports, buildPath, bundleScratch };
            foreach (string root in officialRoots)
                paths.Identity(root);
            string[] destinations = { privateRoot, releaseRoot, playerRoot };
            string[] reserved = { Application.dataPath, Path.Combine(project, "Packages"), Path.Combine(project, "ProjectSettings"),
                Path.Combine(project, "Library"), Path.Combine(project, "Temp") };
            foreach (string root in destinations)
            {
                foreach (string forbidden in reserved)
                    if (Overlaps(paths.Identity(root), paths.Identity(forbidden)))
                        throw new InvalidDataException("Encrypted output must not physically intersect project input or intermediate data: " + root);
            }
            if (paths.Overlap(privateRoot, releaseRoot) || paths.Overlap(privateRoot, playerRoot) || paths.Overlap(releaseRoot, playerRoot) ||
                Directory.Exists(privateRoot) || File.Exists(privateRoot) || Directory.Exists(releaseRoot) || File.Exists(releaseRoot) ||
                Directory.Exists(playerRoot) || File.Exists(playerRoot))
                throw new InvalidDataException("Use separate, fresh private, Player and immutable release paths.");
            using var privateParent = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(privateRoot), false);
            using var releaseParent = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(releaseRoot), false);
            using var playerParent = OwnedWindowsFiles.PinDirectory(Path.GetDirectoryName(playerRoot), false);
            string inputRoot = Path.GetFullPath(packed.BuildPath.GetValue(settings));
            ProtectedAssetAudit.Relative(privateRoot, inputRoot);
            if (!StrictlyBelow(paths.Identity(inputRoot), paths.Identity(privateRoot)))
                throw new InvalidDataException("Protected staging must physically belong to the private root.");
            string stateFolder = OwnedWindowsFiles.LocalAbsolute(settings.profileSettings.EvaluateString(settings.activeProfileId, settings.ContentStateBuildPath));
            ProtectedAssetAudit.Relative(privateRoot, stateFolder);
            if (!StrictlyBelow(paths.Identity(stateFolder), paths.Identity(privateRoot)))
                throw new InvalidDataException("Official content state must physically belong to the private root.");
            string prefix = packed.LoadPath.GetValue(settings);
            if (!prefix.EndsWith("/", StringComparison.Ordinal))
                prefix += "/";
            _ = new EncryptedBundleBinding(applicationNamespace, prefix, group.Guid, "validate", new string('0', 32), packed.GetBundleCachedProviderId(),
                "validate.bundle", auth.KeyId, 1, new string('0', 64), allowLoopback);
            if (!allowLoopback && !string.Equals(new Uri(prefix).Segments.Last().TrimEnd('/'), Path.GetFileName(releaseRoot), StringComparison.Ordinal))
                throw new InvalidDataException("The final folder must name the new immutable URL release identity.");

            var ordinaryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var other in settings.groups.Where(candidate => candidate && candidate != group))
            {
                var schema = other.GetSchema<BundledAssetGroupSchema>();
                if (!schema || !schema.IncludeInBuild)
                    continue;
                string ordinary = Path.GetFullPath(schema.BuildPath.GetValue(settings));
                string load = schema.LoadPath.GetValue(settings).TrimEnd('/') + "/";
                bool local = BelowOrEqual(ordinary, buildPath) && BelowOrEqual(paths.Identity(ordinary), paths.Identity(buildPath));
                if (destinations.Any(root => paths.Overlap(ordinary, root)) ||
                    (!local && reserved.Any(root => paths.Overlap(ordinary, root))) ||
                    load.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || prefix.StartsWith(load, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Ordinary Bundle build path overlaps a protected, Player, or non-owned root: " + other.Name);
                if (!local && (Directory.Exists(ordinary) || File.Exists(ordinary)))
                    throw new InvalidDataException("Ordinary remote bundles also require a fresh private staging path for this audited release: " + other.Name);
                ordinaryRoots.Add(ordinary);
            }

            var audit = new ProtectedAssetAudit(group, auth.PublicDependencyGuids);
            foreach (string root in new[] { library, bundleScratch })
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (string file in ProtectedAssetAudit.EnumerateOwnedFiles(root))
                    using (OwnedWindowsFiles.OpenRead(file))
                    {
                    }
            }
            foreach (string root in officialRoots.Concat(ordinaryRoots).Append(privateRoot).Append(inputRoot).Append(stateFolder))
                paths.Prepare(root);
            // The official accessor creates the configured folder, so only invoke it after the write-free preflight.
            string state = Path.GetFullPath(ContentUpdateScript.GetContentStateDataPath(false, settings));
            if (!SamePath(state, Path.Combine(stateFolder, "addressables_content_state.bin")))
                throw new InvalidDataException("Official content state resolved to an unsupported path.");
            string reportPath = Path.GetFullPath(Path.Combine(Addressables.LibraryPath, "buildlayout.json"));
            string priorLayout = File.Exists(reportPath) ? ProtectedAssetAudit.DigestFile(reportPath) : null;
            var builder = ScriptableObject.CreateInstance<EncryptedPackedBuilder>();
            try
            {
                builder.ProtectedGroup = group;
                builder.ApplicationNamespace = applicationNamespace;
                builder.ReleasePrefix = prefix;
                builder.KeyId = auth.KeyId;
                builder.Loopback = allowLoopback;
                bool oldLayout = ProjectConfigData.GenerateBuildLayout;
                var oldFormat = ProjectConfigData.BuildLayoutReportFileFormat;
                AddressablesPlayerBuildResult output;
                try
                {
                    ProjectConfigData.GenerateBuildLayout = true;
                    ProjectConfigData.BuildLayoutReportFileFormat = ProjectConfigData.ReportFileFormat.JSON;
                    output = builder.BuildData<AddressablesPlayerBuildResult>(new AddressablesDataBuilderInput(settings));
                }
                finally
                {
                    ProjectConfigData.BuildLayoutReportFileFormat = oldFormat;
                    ProjectConfigData.GenerateBuildLayout = oldLayout;
                }
                if (output == null || !string.IsNullOrEmpty(output.Error) || output.IsUpdateContentBuild ||
                    !File.Exists(reportPath) || ProtectedAssetAudit.DigestFile(reportPath) == priorLayout || !File.Exists(state))
                    throw new InvalidDataException("Official Packed Builder did not produce current Bundle, content state and Build Layout outputs: " + output?.Error);

                string archivedLayout = Path.Combine(privateRoot, "official-buildlayout.json");
                string archivedState = Path.Combine(privateRoot, "official-addressables-content-state.bin");
                File.Copy(reportPath, archivedLayout, false);
                File.Copy(state, archivedState, false);
                var layout = BuildLayout.Open(archivedLayout, readHeader: true, readFullFile: true);
                try
                {
                    var protectedNames = new HashSet<string>(builder.Bindings.Values.Select(row => row.RelativePath), StringComparer.Ordinal);
                    var allNames = new HashSet<string>(output.AssetBundleBuildResults.Select(bundle => Path.GetFileName(bundle.FilePath)), StringComparer.Ordinal);
                    if (allNames.Count != output.AssetBundleBuildResults.Count || protectedNames.Count != builder.Bindings.Count)
                        throw new InvalidDataException("Ambiguous or nested official Bundle names cannot be audited.");
                    audit.AuditLayout(layout, protectedNames, allNames);
                }
                finally { layout?.Close(); }

                var protectedInputs = new Dictionary<string, (string Path, EncryptedBundleBinding Binding)>(StringComparer.Ordinal);
                var ordinaryInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var expectedStreaming = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string settingsPath = Path.Combine(buildPath, "settings.json");
                string catalogPath = Path.Combine(buildPath, builder.CatalogName);
                using (var input = OwnedWindowsFiles.OpenRead(settingsPath))
                    FrozenAddressablesRoutes.CheckRuntimeSettings(EncryptedDeliverySession.ReadText(input), builder.CatalogName);
                string settingsHash = ProtectedAssetAudit.DigestFile(settingsPath);
                string catalogHash = ProtectedAssetAudit.DigestFile(catalogPath);
                expectedStreaming.Add("aa/settings.json", settingsHash);
                expectedStreaming.Add("aa/" + builder.CatalogName, catalogHash);
                foreach (string metadata in new[] { "catalog.hash", "AddressablesLink/link.xml" })
                {
                    string officialFile = Path.Combine(buildPath, metadata);
                    if (File.Exists(officialFile))
                        expectedStreaming.Add("aa/" + metadata, ProtectedAssetAudit.DigestFile(officialFile));
                }
                foreach (var result in output.AssetBundleBuildResults)
                {
                    string file = Path.GetFullPath(result.FilePath);
                    using var source = OwnedWindowsFiles.OpenRead(file);
                    string digest = AuthenticatedBundleStream.Hash(source, default);
                    if (result.SourceAssetGroup == group)
                    {
                        if (!builder.Bindings.TryGetValue(result.InternalBundleName, out var planned) || result.Hash != planned.BundleHash ||
                            ProtectedAssetAudit.Relative(inputRoot, file) != planned.RelativePath || source.Length != planned.PlainLength)
                            throw new InvalidDataException("Actual source Bundle does not match its protected Packed Builder record.");
                        var binding = new EncryptedBundleBinding(planned.Application, planned.ReleasePrefix, planned.GroupGuid, planned.BundleName,
                            planned.BundleHash, planned.ProviderId, planned.RelativePath, planned.KeyId, planned.PlainLength, digest, builder.Loopback);
                        protectedInputs.Add(planned.RelativePath, (file, binding));
                    }
                    else if (BelowOrEqual(file, buildPath))
                        expectedStreaming.Add("aa/" + ProtectedAssetAudit.Relative(buildPath, file), digest);
                    else
                        ordinaryInputs.Add(file, digest);
                }
                if (protectedInputs.Count != builder.Bindings.Count)
                    throw new InvalidDataException("The audited protected inventory is incomplete.");

                paths.Prepare(playerRoot);
                player.options |= BuildOptions.DetailedBuildReport | BuildOptions.StrictMode;
                var report = BuildPipeline.BuildPlayer(player);
                if (report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0)
                    throw new InvalidDataException("The Win64 Player failed to build.");
                string streaming = Path.Combine(playerRoot, Path.GetFileNameWithoutExtension(player.locationPathName) + "_Data", "StreamingAssets");
                audit.AuditPlayer(report, player.scenes, streaming, expectedStreaming);
                if (ProtectedAssetAudit.DigestFile(state) != ProtectedAssetAudit.DigestFile(archivedState))
                    throw new InvalidDataException("Official Addressables state changed during Player build.");
                foreach (var ordinary in ordinaryInputs)
                    if (ProtectedAssetAudit.DigestFile(ordinary.Key) != ordinary.Value)
                        throw new InvalidDataException("Audited ordinary Bundle changed during Player build: " + Path.GetFileName(ordinary.Key));
                return Export(protectedInputs, builder, keys, audit, report, privateRoot, releaseRoot, streaming, catalogHash, settingsHash, archivedLayout, archivedState);
            }
            finally { Object.DestroyImmediate(builder); }
        }

        private static EncryptedBundleDescription Export(Dictionary<string, (string Path, EncryptedBundleBinding Binding)> inputs, EncryptedPackedBuilder builder,
            IEncryptedBundleKeySource keys, ProtectedAssetAudit audit, BuildReport report,
            string privateRoot, string releaseRoot, string streaming, string catalogHash, string settingsHash, string layout, string state)
        {
            string stage = Path.Combine(Path.GetDirectoryName(releaseRoot), ".yl-protected-" + Guid.NewGuid().ToString("N"));
            string aa = Path.Combine(streaming, "aa");
            using var playerPin = OwnedWindowsFiles.PinDirectory(aa, false);
            using var shippedCatalog = OwnedWindowsFiles.ReadPinned(Path.Combine(aa, builder.CatalogName));
            using var shippedSettings = OwnedWindowsFiles.ReadPinned(Path.Combine(aa, "settings.json"));
            if (AuthenticatedBundleStream.Hash(shippedCatalog, default) != catalogHash || AuthenticatedBundleStream.Hash(shippedSettings, default) != settingsHash)
                throw new InvalidDataException("Final Player metadata differs after auditing.");
            using var key = EncryptedBundleKeyLease.Rent(keys, builder.KeyId) ?? throw new EncryptedBundleKeyUnavailableException(builder.KeyId);
            var rows = new List<EncryptedBundleRecord>();
            using var ownedStage = OwnedWindowsFiles.CreatePublicationDirectory(stage);
            foreach (var item in inputs.Values)
            {
                var binding = item.Binding;
                using var source = OwnedWindowsFiles.OpenRead(item.Path);
                using var cipher = OwnedWindowsFiles.CreateTemporaryPinned(Path.Combine(stage, binding.RelativePath));
                try
                {
                    AuthenticatedBundleStream.Seal(source, cipher, binding, key);
                }
                catch (InvalidDataException error) { throw new InvalidDataException("Audited plaintext Bundle changed: " + binding.RelativePath, error); }
                rows.Add(new EncryptedBundleRecord(binding, cipher.Length, AuthenticatedBundleStream.Hash(cipher, default)));
            }

            key.Dispose(); // Run application callbacks before checking or publishing the finished bytes.
            string[] names = rows.Select(row => row.Binding.RelativePath).ToArray();
            CheckInventory(stage, names);
            var description = new EncryptedBundleDescription(builder.CatalogName, catalogHash, settingsHash, builder.RoutesSha256, rows);
            // Windows will not rename a directory with locked children. Move the ciphertext first, then commit the
            // deployment manifest while the verified children are locked. A directory without it is incomplete.
            OwnedWindowsFiles.Publish(ownedStage, releaseRoot);
            var verified = new List<FileStream>(rows.Count);
            FileStream playerDescription = null, manifest = null;
            bool committed = false;
            try
            {
                foreach (var row in rows)
                {
                    var cipher = OwnedWindowsFiles.ReadPinned(Path.Combine(releaseRoot, row.Binding.RelativePath));
                    verified.Add(cipher);
                    AuthenticatedBundleStream.ValidateCipher(cipher, row, default);
                }
                string pending = ".yl-manifest-" + Guid.NewGuid().ToString("N") + ".part";
                playerDescription = WritePending(Path.Combine(aa, pending), description.Encode());
                manifest = WritePending(Path.Combine(releaseRoot, pending), new JObject
                {
                    ["releasePrefix"] = rows[0].Binding.ReleasePrefix,
                    ["descriptionSha256"] = AuthenticatedBundleStream.Hash(playerDescription, default),
                    ["catalogSha256"] = catalogHash,
                    ["files"] = new JArray(rows.Select(row => new JObject
                    {
                        ["path"] = row.Binding.RelativePath,
                        ["bytes"] = row.CipherLength,
                        ["sha256"] = row.CipherSha256
                    }))
                }.ToString(Formatting.Indented));
                WriteNew(Path.Combine(privateRoot, "audit.json"), new JObject
                {
                    ["layoutSha256"] = ProtectedAssetAudit.DigestFile(layout),
                    ["contentStateSha256"] = ProtectedAssetAudit.DigestFile(state),
                    ["catalogSha256"] = catalogHash,
                    ["settingsSha256"] = settingsHash,
                    ["routesSha256"] = description.RoutesSha256,
                    ["playerResult"] = report.summary.result.ToString(),
                    ["playerBuildGuid"] = report.summary.guid.ToString(),
                    ["playerExeSha256"] = ProtectedAssetAudit.DigestFile(report.summary.outputPath),
                    ["protectedAssets"] = audit.Report()
                }.ToString(Formatting.Indented));
                CheckInventory(releaseRoot, names.Append(pending));
                OwnedWindowsFiles.Publish(playerDescription, Path.Combine(aa, EncryptedBundleDescription.FileName));
                OwnedWindowsFiles.Publish(manifest, Path.Combine(releaseRoot, "delivery.json"));
                committed = true;
                return description;
            }
            finally
            {
                try
                {
                    FinishPending(manifest, committed);
                }
                finally
                {
                    try
                    {
                        FinishPending(playerDescription, committed);
                    }
                    finally { foreach (var cipher in verified) cipher.Dispose(); }
                }
            }
        }

        private static void CheckInventory(string root, IEnumerable<string> expected)
        {
            var names = new HashSet<string>(expected, StringComparer.Ordinal);
            if (Directory.EnumerateFileSystemEntries(root).Any(path => !names.Remove(Path.GetFileName(path))) || names.Count != 0)
                throw new InvalidDataException("Staged delivery inventory differs from the audited Bundle list.");
        }

        private static FileStream WritePending(string path, string content)
        {
            var file = OwnedWindowsFiles.CreateTemporaryPinned(path);
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
                return file;
            }
            catch { FinishPending(file, false); throw; }
        }

        private static void FinishPending(FileStream file, bool keep)
        {
            if (file == null)
                return;
            using (file)
                if (!keep)
                    OwnedWindowsFiles.DeleteOpen(file);
        }

        private static bool Overlaps(string first, string second)
        {
            first = Path.GetFullPath(first).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            second = Path.GetFullPath(second).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
        }

        private static bool BelowOrEqual(string candidate, string parent) =>
            (candidate.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static bool StrictlyBelow(string candidate, string parent) => BelowOrEqual(candidate, parent) &&
            !string.Equals(candidate.TrimEnd('\\', '/'), parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

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

            internal bool Overlap(string first, string second) => Overlaps(Identity(first), Identity(second));

            internal void Prepare(string path)
            {
                string expected = Identity(path);
                _owned.Add(OwnedWindowsFiles.PinDirectory(path, true));
                using var pin = OwnedWindowsFiles.InspectDirectory(path, out string actual);
                if (!string.Equals(expected.TrimEnd('\\'), actual.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A build output moved outside its audited physical identity: " + path);
            }

            public void Dispose()
            {
                for (int i = _owned.Count - 1; i >= 0; i--)
                    _owned[i].Dispose();
                _owned.Clear();
            }
        }

        private static void WriteNew(string path, string content)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
