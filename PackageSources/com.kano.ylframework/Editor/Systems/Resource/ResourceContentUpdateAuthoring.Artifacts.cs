using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.AddressableAssets.Build;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    public static partial class ResourceContentUpdateAuthoring
    {
        /// <summary>读取正式 Catalog 的官方序列化数据，包括选装构建器所需的 Provider 注册。</summary>
        public static ContentCatalogData ReadCatalogData(string catalogPath)
        {
            ResourcePathValidation.RejectLinks(catalogPath);
            return ResourceContentCatalog.ReadCatalogData(catalogPath);
        }

        /// <summary>从官方 Catalog 读取全部 Bundle 路由和直接依赖，可解析选装包拥有的挂载路由。</summary>
        public static IReadOnlyDictionary<string, string[]> InspectCatalog(string catalogPath, string runtimeRoot,
            Func<IResourceLocation, string> extensionRoute = null)
        {
            ResourcePathValidation.RejectLinks(catalogPath);
            return ResourceContentCatalog.ReadLocal(catalogPath).Inspect(runtimeRoot, extensionRoute);
        }

        /// <summary>检查候选使用的引擎项、Provider 和资产类型可在同一 Player 中加载。</summary>
        public static void RequireCompatibleCatalog(string catalogPath, string baselineCatalogPath, string runtimeRoot,
            IEnumerable<Type> additionalAssetTypes = null, Func<IResourceLocation, string> extensionRoute = null,
            Func<IResourceLocation, string> baselineExtensionRoute = null)
        {
            ResourcePathValidation.RejectLinks(catalogPath);
            ResourcePathValidation.RejectLinks(baselineCatalogPath);
            var baseline = ResourceContentCatalog.ReadLocal(baselineCatalogPath);
            baseline.Inspect(runtimeRoot, baselineExtensionRoute);
            var candidate = ResourceContentCatalog.ReadLocal(catalogPath);
            candidate.Inspect(runtimeRoot, extensionRoute);
            candidate.RequireCompatibleWith(baseline, additionalAssetTypes);
        }

        /// <summary>从 A 的实际安装树生成普通交付的完整制品表。所有路径相对同一安装源根。</summary>
        public static IReadOnlyList<ResourceContentArtifact> DescribeInstalledArtifacts(ResourceContentBaseline baseline,
            string runtimeRoot, string installRoot, string releaseId, string catalogRelativePath,
            IReadOnlyDictionary<string, string> bundleRelativePaths)
        {
            if (baseline == null)
            {
                throw new ArgumentNullException(nameof(baseline));
            }
            if (bundleRelativePaths == null)
            {
                throw new ArgumentNullException(nameof(bundleRelativePaths));
            }
            if (baseline.Build == null)
            {
                throw new InvalidOperationException("Describe installed baseline artifacts in the Editor process that produced A.");
            }

            RequireReleaseId(releaseId);
            VerifyArchive(baseline);
            var catalogPath = InstalledPath(installRoot, catalogRelativePath);
            using var catalogRead = File.OpenRead(catalogPath);
            var catalog = ResourceContentCatalog.ReadLocal(catalogPath);
            var routes = catalog.Inspect(runtimeRoot);
            var outputs = OfficialBundleOutputs(baseline.Build.Result);
            if (routes.Count != bundleRelativePaths.Count)
            {
                throw new InvalidDataException("The installed bundle inventory differs from the baseline Catalog.");
            }

            var installedCatalog = InstalledArtifact(baseline.CatalogRoute, catalogRelativePath, catalogPath,
                releaseId, ResourceContentArtifactKind.Catalog, null);
            if (installedCatalog.Sha256 != baseline.CatalogSha256)
            {
                throw new InvalidDataException("The installed baseline Catalog differs from the sealed official Catalog.");
            }
            var artifacts = new List<ResourceContentArtifact> { installedCatalog };
            foreach (var pair in routes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!bundleRelativePaths.TryGetValue(pair.Key, out var relative))
                {
                    throw new InvalidDataException("An installed baseline Bundle is missing: " + pair.Key);
                }
                var path = InstalledPath(installRoot, relative);
                var official = FindOfficialBundle(outputs, pair.Key, catalog.GetOrdinaryBundleRequest(pair.Key));
                var artifact = InstalledArtifact(pair.Key, relative, path, releaseId, ResourceContentArtifactKind.Bundle, pair.Value);
                if (!SamePath(path, official) && artifact.Sha256 != Sha256File(official))
                {
                    throw new InvalidDataException("An installed baseline Bundle differs from the official build output: " + pair.Key);
                }
                artifacts.Add(artifact);
            }
            return artifacts.AsReadOnly();
        }

        /// <summary>清点 B 完整普通 Catalog，并把变化的对象复制到内容寻址的私有发布目录。</summary>
        public static IReadOnlyList<ResourceContentArtifact> StageUpdateArtifacts(ResourceContentUpdateBuild update,
            ResourceContentManifest installedBaseline, string runtimeRoot, string releaseId, string stagingRoot,
            Uri immutableDownloadRoot, IEnumerable<Type> additionalAssetTypes = null)
        {
            if (update == null)
            {
                throw new ArgumentNullException(nameof(update));
            }
            if (installedBaseline == null)
            {
                throw new ArgumentNullException(nameof(installedBaseline));
            }

            RequireReleaseId(releaseId);
            if (!ResourceContentManifestTrust.IsNetworkUri(immutableDownloadRoot, allowLoopbackHttp: true, directory: true))
            {
                throw new ArgumentException("Provide the canonical immutable HTTPS object root.", nameof(immutableDownloadRoot));
            }
            var objectDirectory = "objects/" + releaseId + "/";
            // Check the longest generated source URI before staging any object.
            if (!ResourceContentManifestTrust.IsNetworkUri(new Uri(immutableDownloadRoot,
                    objectDirectory + new string('0', 64) + ".bundle"), allowLoopbackHttp: true, directory: false))
            {
                throw new ArgumentException("The download root or release ID produces an invalid resource content source path.",
                    nameof(immutableDownloadRoot));
            }
            var stage = Path.GetFullPath(stagingRoot);
            ResourcePathValidation.RejectLinks(stage);
            if (!Directory.Exists(stage) || WithinOrEqual(stage, Application.dataPath) ||
                WithinOrEqual(stage, Addressables.BuildPath) || WithinOrEqual(stage, Path.GetDirectoryName(update.CatalogPath)) ||
                WithinOrEqual(stage, Path.GetDirectoryName(update.Baseline.ReceiptPath)))
            {
                throw new IOException("Choose an existing private staging directory separated from the build and Player baseline outputs.");
            }

            var baseline = update.Baseline;
            VerifyArchive(baseline);
            if (installedBaseline.DescriptionRoute != null || installedBaseline.SettingsSha256 != baseline.StartupSettingsSha256 ||
                installedBaseline.CatalogRoute != baseline.CatalogRoute ||
                installedBaseline.Artifacts[installedBaseline.CatalogRoute].Sha256 != baseline.CatalogSha256 ||
                installedBaseline.ReleaseId == releaseId)
            {
                throw new InvalidDataException("Use the signed ordinary Player baseline and a distinct release identity.");
            }
            ResourcePathValidation.RejectLinks(update.CatalogPath);
            using var catalogRead = File.OpenRead(update.CatalogPath);
            var previous = ResourceContentCatalog.ReadLocal(baseline.CatalogPath);
            previous.Inspect(runtimeRoot);
            previous.RequireManifest(installedBaseline);
            var candidate = ResourceContentCatalog.ReadLocal(update.CatalogPath);
            var routes = candidate.Inspect(runtimeRoot);
            candidate.RequireCompatibleWith(previous, additionalAssetTypes);
            foreach (var route in routes.Keys)
            {
                ResourceContentManifestTrust.GetRoutePath(route, allowLoopbackHttp: true);
            }
            var outputs = OfficialBundleOutputs(update.Build.Result);

            var artifacts = new List<ResourceContentArtifact>
            {
                StageFile(update.CatalogPath, baseline.CatalogRoute, ResourceContentArtifactKind.Catalog, null)
            };
            foreach (var pair in routes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var request = candidate.GetOrdinaryBundleRequest(pair.Key);
                installedBaseline.Artifacts.TryGetValue(pair.Key, out var prior);
                if (prior != null && MatchesRequest(request, previous.GetOrdinaryBundleRequest(pair.Key)))
                {
                    artifacts.Add(ReuseArtifact(prior, pair.Value));
                    continue;
                }

                var path = FindOfficialBundle(outputs, pair.Key, request);
                // With stripped download options, only the actual SBP output can establish that a same-name Bundle is unchanged.
                artifacts.Add(StageFile(path, pair.Key, ResourceContentArtifactKind.Bundle, pair.Value,
                    request == null ? prior : null));
            }
            return artifacts.AsReadOnly();

            ResourceContentArtifact StageFile(string source, string route, ResourceContentArtifactKind kind,
                IEnumerable<string> dependencies, ResourceContentArtifact prior = null)
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length == 0)
                {
                    throw new InvalidDataException("An empty file cannot be staged for content delivery: " + source);
                }
                var sha256 = ResourceContentFileIO.Hash(input);
                if (prior != null && prior.Length == input.Length && prior.Sha256 == sha256)
                {
                    return ReuseArtifact(prior, dependencies);
                }
                var relative = objectDirectory + sha256 + Path.GetExtension(route);
                var destination = Path.Combine(stage, relative);
                ResourcePathValidation.RejectLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (!File.Exists(destination))
                    {
                        input.Position = 0;
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                            output.Flush(flushToDisk: true);
                        }
                        if (new FileInfo(temporary).Length != input.Length || Sha256File(temporary) != sha256)
                        {
                            throw new IOException("The copied content object differs from the build output.");
                        }
                        File.Move(temporary, destination);
                    }
                    else if (new FileInfo(destination).Length != input.Length || Sha256File(destination) != sha256)
                    {
                        throw new IOException("An existing immutable content object differs from the staged bytes.");
                    }
                }
                finally
                {
                    File.Delete(temporary);
                }

                return new ResourceContentArtifact(route, releaseId, new Uri(immutableDownloadRoot, relative).AbsoluteUri,
                    kind, input.Length, sha256, dependencies);
            }
        }

        private static ResourceContentArtifact ReuseArtifact(ResourceContentArtifact prior, IEnumerable<string> dependencies) =>
            new ResourceContentArtifact(prior.Route, prior.OriginReleaseId, prior.Source, prior.Kind, prior.Length, prior.Sha256, dependencies);

        private static bool MatchesRequest(AssetBundleRequestOptions next, AssetBundleRequestOptions previous)
        {
            return next != null && previous != null && !string.IsNullOrEmpty(next.Hash) && next.Hash == previous.Hash && next.Crc == previous.Crc &&
                next.BundleSize == previous.BundleSize && next.BundleName == previous.BundleName;
        }

        private static ILookup<string, AddressablesPlayerBuildResult.BundleBuildResult> OfficialBundleOutputs(
            AddressablesPlayerBuildResult build)
        {
            return build.AssetBundleBuildResults.Where(output => File.Exists(output.FilePath))
                .ToLookup(output => Path.GetFileName(output.FilePath), StringComparer.OrdinalIgnoreCase);
        }

        private static string FindOfficialBundle(ILookup<string, AddressablesPlayerBuildResult.BundleBuildResult> outputs,
            string route, AssetBundleRequestOptions request)
        {
            // Packed/SBP 在 BuildResult 记录 CRC，Unity 原生 CRC 查询并不读取所有 SBP 归档。
            var matching = outputs[Path.GetFileName(route)].Where(output =>
                request == null || (string.IsNullOrEmpty(request.Hash) || string.Equals(output.Hash, request.Hash, StringComparison.OrdinalIgnoreCase)) &&
                (request.Crc == 0 || request.Crc == output.Crc)).ToArray();
            if (matching.Length != 1)
            {
                throw new InvalidDataException("Expected one official output matching the Catalog Bundle identity: " + route);
            }
            var path = matching[0].FilePath;
            ResourcePathValidation.RejectLinks(path);
            var size = new FileInfo(path).Length;
            if (size == 0 || (request != null && request.BundleSize > 0 && request.BundleSize != size))
            {
                throw new InvalidDataException("The official Bundle size differs from the Catalog: " + route);
            }
            return path;
        }

        private static void RequireReleaseId(string releaseId)
        {
            if (!ResourceContentManifestTrust.IsId(releaseId) || releaseId.EndsWith(".", StringComparison.Ordinal))
            {
                throw new ArgumentException("Provide a valid immutable release identifier.", nameof(releaseId));
            }
        }

        private static string InstalledPath(string installRoot, string relative)
        {
            ResourceContentManifestTrust.RequireInstallationPath(relative);
            string full = Path.GetFullPath(Path.Combine(installRoot, relative));
            if (!WithinOrEqual(full, installRoot) || SamePath(full, installRoot))
            {
                throw new InvalidDataException("The installation artifact is outside the selected installation root.");
            }
            ResourcePathValidation.RejectLinks(full);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException("Missing installation artifact.", full);
            }
            return full;
        }

        private static ResourceContentArtifact InstalledArtifact(string route, string relative, string path, string releaseId,
            ResourceContentArtifactKind kind, IEnumerable<string> dependencies)
        {
            var routePath = ResourceContentManifestTrust.GetRoutePath(route, allowLoopbackHttp: true);
            if (Path.GetExtension(routePath) != Path.GetExtension(relative))
            {
                throw new InvalidDataException("The installation artifact extension does not match its Catalog route.");
            }
            return new ResourceContentArtifact(route, releaseId, ResourceContentManifestTrust.InstallPrefix + relative, kind,
                new FileInfo(path).Length, Sha256File(path), dependencies);
        }
    }
}
