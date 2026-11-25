using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
#if UNITY_6000_3_OR_NEWER
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
#endif
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Editor.Resource
{
    /// <summary>封存的 A 构建身份和官方原始制品，供后续内容构建读取。</summary>
    public sealed class ResourceContentBaseline
    {
        internal ResourceContentBaseline(ResourceEditorBuildReport build, string receiptPath, string contentStatePath,
            string contentStateSha256, string startupSettingsSha256, string catalogRoute, string catalogSha256,
            string editorVersion, string addressablesVersion, string playerVersion, string remoteCatalogLoadPath,
            string settingsGuid, string profileId, BuildTarget target)
        {
            Build = build;
            ReceiptPath = receiptPath;
            ContentStatePath = contentStatePath;
            ContentStateSha256 = contentStateSha256;
            StartupSettingsSha256 = startupSettingsSha256;
            CatalogRoute = catalogRoute;
            CatalogSha256 = catalogSha256;
            EditorVersion = editorVersion;
            AddressablesVersion = addressablesVersion;
            PlayerVersion = playerVersion;
            RemoteCatalogLoadPath = remoteCatalogLoadPath;
            SettingsGuid = settingsGuid;
            ProfileId = profileId;
            Target = target;
        }

        /// <summary>本进程的官方构建回执。从磁盘打开已封存基线时为空。</summary>
        public ResourceEditorBuildReport Build
        {
            get;
        }
        public string ReceiptPath
        {
            get;
        }
        public string ContentStatePath
        {
            get;
        }
        public string ContentStateSha256
        {
            get;
        }
        public string StartupSettingsPath => Path.Combine(Path.GetDirectoryName(ReceiptPath), "settings.json");
        public string StartupSettingsSha256
        {
            get;
        }
        public string CatalogRoute
        {
            get;
        }
        public string CatalogPath => Path.Combine(Path.GetDirectoryName(ReceiptPath), Path.GetFileName(CatalogRoute));
        public string CatalogSha256
        {
            get;
        }
        public string EditorVersion
        {
            get;
        }
        public string AddressablesVersion
        {
            get;
        }
        public string PlayerVersion
        {
            get;
        }
        public string RemoteCatalogLoadPath
        {
            get;
        }
        public string SettingsGuid
        {
            get;
        }
        public string ProfileId
        {
            get;
        }
        public BuildTarget Target
        {
            get;
        }
    }

    /// <summary>一次官方内容更新的实际结果和用于发布的远端 Catalog 路径。</summary>
    public sealed class ResourceContentUpdateBuild
    {
        internal ResourceContentUpdateBuild(ResourceEditorBuildReport build, ResourceContentBaseline baseline, string catalogPath)
        {
            Build = build;
            Baseline = baseline;
            CatalogPath = catalogPath;
        }

        public ResourceEditorBuildReport Build
        {
            get;
        }
        public ResourceContentBaseline Baseline
        {
            get;
        }
        public string CatalogPath
        {
            get;
        }
    }

    /// <summary>为事务启动封存官方内容基线，按同一 Player 版本构建后续内容。</summary>
    public static partial class ResourceContentUpdateAuthoring
    {
        private const string StartupSettingsName = "settings.json";
        private const string BaselineReceiptName = "resource-content-baseline.json";
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>先构建并封存内容，再由调用方用该启动配置构建 Player。</summary>
        public static ResourceContentBaseline BuildBaseline(AddressableAssetSettings settings, string archivePath,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            ResourceEditorOperations.ValidateBuild(settings);
            RequirePlayerBuildSettings(settings);
            RequireArchivePaths(archivePath);
            var build = ResourceEditorOperations.BuildContent(settings, archivePath, cancellation);
            return PrepareBaseline(settings, build);
        }

        /// <summary>封存本次官方 Packed 基线，原位改写 Player 将复制的临时 settings.json。</summary>
        public static ResourceContentBaseline PrepareBaseline(AddressableAssetSettings settings, ResourceEditorBuildReport build)
        {
            if (!settings || build == null || build.IsContentUpdate || build.Result == null ||
                build.Target != EditorUserBuildSettings.activeBuildTarget || build.ProfileId != settings.activeProfileId ||
                build.SettingsGuid != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)))
            {
                throw new ArgumentException("Select the official full content build for the current settings, Profile and target.", nameof(build));
            }

            RequirePlayerBuildSettings(settings);
            var settingsPath = Path.GetFullPath(build.Result.OutputPath);
            var data = ReadSettings(settingsPath);
            var catalog = FindStartupCatalog(data, null);
            return PrepareBaseline(settings, settingsPath,
                Path.Combine(Path.GetDirectoryName(settingsPath), Path.GetFileName(catalog.InternalId)), build.ContentStatePath, build);
        }

        /// <summary>封存选装构建器已生成的正式制品。制品所在目录可由该构建器私有管理。</summary>
        public static ResourceContentBaseline PrepareBaseline(AddressableAssetSettings settings, string builtSettingsPath,
            string builtCatalogPath, string contentStateArchivePath)
        {
            return PrepareBaseline(settings, builtSettingsPath, builtCatalogPath, contentStateArchivePath, null);
        }

        private static ResourceContentBaseline PrepareBaseline(AddressableAssetSettings settings, string builtSettingsPath,
            string builtCatalogPath, string contentStateArchivePath, ResourceEditorBuildReport build)
        {
            if (!settings || !settings.BuildRemoteCatalog)
            {
                throw new InvalidOperationException("The sealed baseline must come from an official Build Remote Catalog content build.");
            }

            var statePath = Path.GetFullPath(contentStateArchivePath);
            var receiptPath = RequireArchivePaths(statePath);
            var state = ReadOfficialState(statePath);
            var target = EditorUserBuildSettings.activeBuildTarget;
            var packageVersion = GetAddressablesVersion();
            if (state.editorVersion != Application.unityVersion ||
                state.remoteCatalogLoadPath != settings.RemoteCatalogLoadPath.GetValue(settings))
            {
                throw new InvalidDataException("The official state does not match the current Editor or logical remote Catalog path.");
            }

            var originalSettings = ReadSettings(builtSettingsPath);
            if (originalSettings.BuildTarget != target.ToString() || originalSettings.AddressablesVersion != packageVersion)
            {
                throw new InvalidDataException("The official startup settings have a different build target or Addressables version.");
            }

            var catalogName = Path.GetFileName(builtCatalogPath);
            var catalogRoute = StartupCatalogRoute(catalogName);
            var settingsBytes = CreateFrozenSettings(originalSettings, catalogName);
            ResourcePathValidation.RejectLinks(builtCatalogPath);
            var catalogBytes = File.ReadAllBytes(builtCatalogPath);
            if (catalogBytes.Length == 0)
            {
                throw new InvalidDataException("The official packed Catalog is empty.");
            }

            var baseline = new ResourceContentBaseline(build, receiptPath, statePath, Sha256File(statePath),
                Sha256(settingsBytes), catalogRoute, Sha256(catalogBytes), state.editorVersion, packageVersion,
                state.playerVersion, state.remoteCatalogLoadPath,
                AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)), settings.activeProfileId, target);

            WriteFile(baseline.StartupSettingsPath, settingsBytes);
            WriteFile(baseline.CatalogPath, catalogBytes);
            WriteFile(builtSettingsPath, settingsBytes, replace: true);
            WriteFile(receiptPath, StrictUtf8.GetBytes(new JObject
            {
                ["schemaVersion"] = 1,
                ["contentStateName"] = Path.GetFileName(statePath),
                ["contentStateSha256"] = baseline.ContentStateSha256,
                ["settingsSha256"] = baseline.StartupSettingsSha256,
                ["catalogRoute"] = baseline.CatalogRoute,
                ["catalogSha256"] = baseline.CatalogSha256,
                ["editorVersion"] = baseline.EditorVersion,
                ["addressablesVersion"] = baseline.AddressablesVersion,
                ["playerVersion"] = baseline.PlayerVersion,
                ["remoteCatalogLoadPath"] = baseline.RemoteCatalogLoadPath,
                ["settingsGuid"] = baseline.SettingsGuid,
                ["profileId"] = baseline.ProfileId,
                ["target"] = baseline.Target.ToString()
            }.ToString(Formatting.Indented)));
            return baseline;
        }

        /// <summary>生成只含官方本地 Catalog 入口且无 hash 依赖的启动设置，可原位改写生成物。</summary>
        public static string FreezeStartupSettings(string settingsPath, string catalogName, string outputPath)
        {
            var data = ReadSettings(settingsPath);
            var bytes = CreateFrozenSettings(data, catalogName);
            WriteFile(outputPath, bytes, replace: SamePath(settingsPath, outputPath));
            return StartupCatalogRoute(catalogName);
        }

        /// <summary>重新读取单独归档的 A，验证官方状态、启动设置及 Catalog 未变化。</summary>
        public static ResourceContentBaseline ReadBaseline(string receiptPath)
        {
            ResourcePathValidation.RejectLinks(receiptPath);
            var full = Path.GetFullPath(receiptPath);
            var receipt = JObject.Parse(File.ReadAllText(full, StrictUtf8),
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if ((int?)receipt["schemaVersion"] != 1 ||
                !Enum.TryParse<BuildTarget>(Field("target"), out var target) ||
                Field("target") != target.ToString())
            {
                throw new InvalidDataException("Unsupported content baseline receipt.");
            }

            var stateName = Field("contentStateName");
            if (stateName != Path.GetFileName(stateName) || !stateName.EndsWith(".bin", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid archived official content state filename.");
            }

            var catalogRoute = Field("catalogRoute");
            if (catalogRoute != StartupCatalogRoute(Path.GetFileName(catalogRoute)))
            {
                throw new InvalidDataException("Invalid archived startup Catalog route.");
            }

            var baseline = new ResourceContentBaseline(null, full, Path.Combine(Path.GetDirectoryName(full), stateName),
                Field("contentStateSha256"), Field("settingsSha256"), catalogRoute, Field("catalogSha256"),
                Field("editorVersion"), Field("addressablesVersion"), Field("playerVersion"), Field("remoteCatalogLoadPath"),
                Field("settingsGuid"), Field("profileId"), target);
            VerifyArchive(baseline);
            return baseline;

            string Field(string name)
            {
                if (receipt[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)receipt[name]))
                {
                    throw new InvalidDataException("Missing content baseline receipt field: " + name);
                }
                return (string)receipt[name];
            }
        }

        /// <summary>仅用封存的 A 官方状态执行更新，远端输出 Profile 路径须指向新的私有目录。</summary>
        public static ResourceContentUpdateBuild BuildContentUpdate(AddressableAssetSettings settings, ResourceContentBaseline baseline,
            string remoteBuildRoot, CancellationToken cancellation = default)
        {
            if (baseline == null)
            {
                throw new ArgumentNullException(nameof(baseline));
            }

            cancellation.ThrowIfCancellationRequested();
            ResourceEditorOperations.ValidateBuild(settings);
            VerifyArchive(baseline);
            if (baseline.EditorVersion != Application.unityVersion || baseline.AddressablesVersion != GetAddressablesVersion() ||
                baseline.Target != EditorUserBuildSettings.activeBuildTarget || baseline.ProfileId != settings.activeProfileId ||
                baseline.SettingsGuid != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)) ||
                !settings.BuildRemoteCatalog || baseline.RemoteCatalogLoadPath != settings.RemoteCatalogLoadPath.GetValue(settings))
            {
                throw new InvalidDataException("The selected Editor, Addressables, target, Profile or remote Catalog path differs from the Player baseline.");
            }
            var root = Path.GetFullPath(remoteBuildRoot);
            ResourcePathValidation.RejectLinks(root);
            if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any() ||
                !SamePath(root, settings.RemoteCatalogBuildPath.GetValue(settings)) ||
                WithinOrEqual(root, Application.dataPath) || WithinOrEqual(root, Addressables.BuildPath) ||
                WithinOrEqual(root, Path.GetDirectoryName(baseline.ReceiptPath)))
            {
                throw new InvalidOperationException("Set Remote Catalog Build Path to the selected empty private update output directory.");
            }

            foreach (var group in settings.groups.Where(group => group))
            {
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (!schema || !schema.IncludeInBuild)
                {
                    continue;
                }
                var path = schema.BuildPath.GetValue(settings);
                if (string.IsNullOrEmpty(path) ||
                    (!WithinOrEqual(path, root) && !WithinOrEqual(path, Addressables.BuildPath)))
                {
                    throw new InvalidOperationException("Bundle build path must use the private update output or official local build scratch: " + group.Name);
                }
                ResourcePathValidation.RejectLinks(path);
            }

            var versionedName = settings.profileSettings.EvaluateString(settings.activeProfileId, "catalog_" + baseline.PlayerVersion);
            if (versionedName != Path.GetFileName(versionedName) || versionedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("The official versioned Catalog filename must be a single file inside the update directory.");
            }
            var catalogPath = Path.Combine(root, versionedName + Path.GetExtension(baseline.CatalogPath));
            var build = ResourceEditorOperations.BuildContentUpdate(settings, baseline.ContentStatePath, cancellation);
            VerifyArchive(baseline);
            ResourcePathValidation.RejectLinks(catalogPath);
            if (!build.Result.IsUpdateContentBuild || !File.Exists(catalogPath) || new FileInfo(catalogPath).Length == 0)
            {
                throw new InvalidDataException("Official content update did not produce the expected versioned Catalog: " + catalogPath);
            }
            var generated = ReadSettings(build.Result.OutputPath);
            if (generated.BuildTarget != baseline.Target.ToString())
            {
                throw new InvalidDataException("The official content update generated settings for a different build target.");
            }
            // 只审计更新构建的临时输出。Player 的启动配置仍来自已封存的 A。
            CreateFrozenSettings(generated, Path.GetFileName(baseline.CatalogRoute));
            return new ResourceContentUpdateBuild(build, baseline, catalogPath);
        }

        private static void RequirePlayerBuildSettings(AddressableAssetSettings settings)
        {
            if (!settings.BuildRemoteCatalog ||
                settings.BuildAddressablesWithPlayerBuild != AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer)
            {
                throw new InvalidOperationException(
                    "Enable Build Remote Catalog for content builds and explicitly disable rebuilding Addressables with the Player.");
            }
        }

        private static string StartupCatalogRoute(string name)
        {
            if (name != "catalog.json" && name != "catalog.bin")
            {
                throw new InvalidDataException("Transactional startup requires an unpacked official catalog.json or catalog.bin.");
            }
            return ResourceContentCatalog.RuntimeToken + "/" + name;
        }

        private static ResourceManagerRuntimeData ReadSettings(string path)
        {
            ResourcePathValidation.RejectLinks(path);
            return JsonUtility.FromJson<ResourceManagerRuntimeData>(File.ReadAllText(path, StrictUtf8)) ??
                throw new InvalidDataException("Missing official Addressables runtime settings.");
        }

        private static ResourceLocationData FindStartupCatalog(ResourceManagerRuntimeData data, string catalogName)
        {
            var catalog = data.CatalogLocations?.SingleOrDefault(location => location?.Keys != null &&
                location.Keys.Contains(ResourceManagerRuntimeData.kCatalogAddress));
            if (data.IsLocalCatalogInBundle || data.InitializationObjects == null || data.InitializationObjects.Count != 0 ||
                data.CertificateHandlerType != null || catalog == null || catalog.Keys.Length != 1 ||
                catalog.Provider != typeof(ContentCatalogProvider).FullName || catalog.ResourceType != typeof(ContentCatalogData) ||
                catalog.InternalId != StartupCatalogRoute(catalogName ?? Path.GetFileName(catalog.InternalId)) ||
                (catalog.Data != null && !(catalog.Data is ProviderLoadRequestOptions)))
            {
                throw new InvalidDataException("Expected a single official unpacked startup Catalog and no custom startup objects.");
            }
            return catalog;
        }

        private static byte[] CreateFrozenSettings(ResourceManagerRuntimeData data, string catalogName)
        {
            var catalog = FindStartupCatalog(data, catalogName);
            var dependencies = catalog.Dependencies ?? Array.Empty<string>();
            var catalogKey = ResourceManagerRuntimeData.kCatalogAddress;
            foreach (var location in data.CatalogLocations.Where(location => location != catalog))
            {
                if (location?.Keys == null || location.Keys.Length != 1 || location.Provider != typeof(TextDataProvider).FullName ||
                    location.ResourceType != typeof(string) || !dependencies.Contains(location.Keys[0]) ||
                    (location.Keys[0] != catalogKey + "RemoteHash" && location.Keys[0] != catalogKey + "CacheHash" &&
                     location.Keys[0] != catalogKey + "LocalHash"))
                {
                    throw new InvalidDataException("The official startup settings contain an unsupported location.");
                }
            }
            if (dependencies.Length != data.CatalogLocations.Count - 1 ||
                dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Length ||
                data.CatalogLocations.Where(location => location != catalog).Select(location => location.Keys[0])
                    .Distinct(StringComparer.Ordinal).Count() != dependencies.Length)
            {
                throw new InvalidDataException("Official startup Catalog hash dependencies are inconsistent.");
            }

            data.DisableCatalogUpdateOnStartup = true;
            data.CatalogLocations.Clear();
            data.CatalogLocations.Add(new ResourceLocationData(catalog.Keys, catalog.InternalId,
                typeof(ContentCatalogProvider), typeof(ContentCatalogData), Array.Empty<string>()));
            var bytes = StrictUtf8.GetBytes(JsonUtility.ToJson(data, true));
            using var stream = new MemoryStream(bytes, writable: false);
            ResourceContentCatalog.RequireStartupSettings(stream, catalog.InternalId, data.BuildTarget);
            return bytes;
        }

        private static AddressablesContentState ReadOfficialState(string path)
        {
            ResourcePathValidation.RejectLinks(path);
            var state = ContentUpdateScript.LoadContentState(path);
            if (state == null || string.IsNullOrWhiteSpace(state.playerVersion) || string.IsNullOrWhiteSpace(state.remoteCatalogLoadPath))
            {
                throw new InvalidDataException("Could not read an official remote Catalog content state archive.");
            }
            return state;
        }

        private static void VerifyArchive(ResourceContentBaseline baseline)
        {
            if (Sha256File(baseline.ContentStatePath) != baseline.ContentStateSha256 ||
                Sha256File(baseline.StartupSettingsPath) != baseline.StartupSettingsSha256 ||
                Sha256File(baseline.CatalogPath) != baseline.CatalogSha256)
            {
                throw new InvalidDataException("The archived Player content state, startup settings or Catalog changed after sealing.");
            }

            using (var stream = File.OpenRead(baseline.StartupSettingsPath))
            {
                ResourceContentCatalog.RequireStartupSettings(stream, baseline.CatalogRoute, baseline.Target.ToString());
            }
            var state = ReadOfficialState(baseline.ContentStatePath);
            if (state.editorVersion != baseline.EditorVersion || state.playerVersion != baseline.PlayerVersion ||
                state.remoteCatalogLoadPath != baseline.RemoteCatalogLoadPath ||
                ReadSettings(baseline.StartupSettingsPath).AddressablesVersion != baseline.AddressablesVersion)
            {
                throw new InvalidDataException("The sealed baseline receipt does not describe the archived official files.");
            }
        }

        private static string RequireArchivePaths(string archivePath)
        {
            var archive = Path.GetFullPath(archivePath);
            var root = Path.GetDirectoryName(archive);
            var receipt = Path.Combine(root, BaselineReceiptName);
            ResourcePathValidation.RejectLinks(archive);
            if (!Directory.Exists(root) || WithinOrEqual(archive, Application.dataPath) ||
                WithinOrEqual(archive, Addressables.BuildPath) ||
                new[] { receipt, Path.Combine(root, StartupSettingsName), Path.Combine(root, "catalog.json"),
                    Path.Combine(root, "catalog.bin") }.Any(path => File.Exists(path) || Directory.Exists(path) || SamePath(path, archive)))
            {
                throw new IOException("Choose a private baseline archive directory with no existing sealed settings, Catalog or receipt.");
            }
            return receipt;
        }

        private static string GetAddressablesVersion()
        {
            return PackageInfo.FindForAssembly(typeof(AddressableAssetSettings).Assembly)?.version ??
                throw new InvalidOperationException("Cannot identify the loaded Addressables package.");
        }

        private static string Sha256File(string path)
        {
            ResourcePathValidation.RejectLinks(path);
            using var stream = File.OpenRead(path);
            return ResourceContentFileIO.Hash(stream);
        }

        private static string Sha256(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return ResourceContentFileIO.Hex(hash.ComputeHash(bytes));
        }

        private static bool SamePath(string first, string second)
        {
            return Path.GetFullPath(first).TrimEnd('/', '\\').Equals(
                Path.GetFullPath(second).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool WithinOrEqual(string path, string root)
        {
            return SamePath(path, root) || Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteFile(string path, byte[] bytes, bool replace = false)
        {
            var full = Path.GetFullPath(path);
            ResourcePathValidation.RejectLinks(full);
            var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (replace)
                {
                    File.Replace(temporary, full, null);
                }
                else
                {
                    File.Move(temporary, full);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

#if UNITY_6000_3_OR_NEWER
    internal sealed class ResourceContentCatalogLinker : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64 ||
                PackageInfo.FindForAssembly(typeof(ContentCatalogData).Assembly)?.version != "2.10.3")
                return null;

            // Unity 不会直接消费 UPM 包目录里的 link.xml。通过正式构建回调交给 UnityLinker。
            var package = PackageInfo.FindForAssembly(typeof(ResourceContentCatalogLinker).Assembly);
            if (package == null)
                return null;
            return Path.Combine(package.resolvedPath, "Runtime/Systems/Resource/Updates/link.xml");
        }
    }
#endif
}
