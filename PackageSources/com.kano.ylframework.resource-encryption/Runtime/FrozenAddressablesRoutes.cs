using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource.Encryption
{
    internal static class FrozenAddressablesRoutes
    {
        internal const string RuntimeToken = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";
        internal const string EmbeddedPrefix = EncryptedBundleBinding.EmbeddedPrefix;
        internal const string MountPrefix = "yl-encrypted-mount/";
        private const string BuiltInLocator = "DynamicResourceLocator";
        private const string LocalCatalogId = "AddressablesMainContentCatalog";

        internal static string MountId(EncryptedBundleBinding item) => MountPrefix + item.GroupPath;
        internal static bool IsBundle(Type type) => type == typeof(IAssetBundleResource) || type == typeof(EncryptedMountedBundle);
        internal static bool NoUnityCacheVersion(string hash) => string.IsNullOrEmpty(hash) || hash == "00000000000000000000000000000000";
        internal static bool IsSupportedAssetProvider(string provider, int dependencies) =>
            provider == typeof(BundledAssetProvider).FullName ||
            (provider == "UnityEngine.ResourceManagement.ResourceProviders.LegacyResourcesProvider" && dependencies == 0);

        internal static bool IsBuiltInScene(string provider, Type type, object data, int dependencies) =>
            provider == "" && type == typeof(SceneInstance) && data == null && dependencies == 0;

        internal static string BuiltInSceneId(string path)
        {
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A built-in scene has no Unity asset path.");
            int start = path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? "Assets/".Length : 0;
            return path.Substring(start, path.Length - start - ".unity".Length);
        }

        internal static void CheckAlias(object key, int sceneIndex)
        {
            if ((key is string alias && alias.Length != 0 && !alias.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase)) ||
                (key is int index && sceneIndex >= 0 && index == sceneIndex))
                return;
            throw new InvalidDataException("Unexpected Catalog alias or built-in scene index.");
        }

        internal static bool IsStockLocalBundle(string id, string provider)
        {
            const string prefix = RuntimeToken + "/";
            return IsStockBundleProvider(provider) && id.StartsWith(prefix, StringComparison.Ordinal) &&
                !id.StartsWith(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase) && id.EndsWith(".bundle", StringComparison.Ordinal) &&
                id.Substring(prefix.Length).Split('/').All(part => part.Length != 0 && !part.EndsWith(".", StringComparison.Ordinal) &&
                    !part.EndsWith(" ", StringComparison.Ordinal) && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }

        internal static bool IsStockBundleProvider(string provider)
        {
            string stock = typeof(AssetBundleProvider).FullName;
            return provider == stock || (provider != null && provider.StartsWith(stock + "_", StringComparison.Ordinal) &&
                provider.Length == stock.Length + 33 && Guid.TryParseExact(provider.Substring(stock.Length + 1), "N", out _));
        }

        internal static void CheckRuntimeSettings(string settings, string catalogName)
        {
            var data = JObject.Parse(settings, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (data["m_buildTarget"]?.Value<string>() != EncryptedBundleBinding.Platform ||
                data["m_DisableCatalogUpdateOnStart"]?.Type != JTokenType.Boolean || !data["m_DisableCatalogUpdateOnStart"].Value<bool>() ||
                data["m_IsLocalCatalogInBundle"]?.Value<bool>() != false ||
                data["m_CertificateHandlerType"]?["m_ClassName"]?.Value<string>() != "" ||
                !(data["m_ExtraInitializationData"] is JArray initializers) || initializers.Count != 0 ||
                !(data["m_CatalogLocations"] is JArray locations) || locations.Count != 1 ||
                !(locations[0]["m_Keys"] is JArray keys) || keys.Count != 1 || keys[0].Value<string>() != LocalCatalogId ||
                !(locations[0]["m_Dependencies"] is JArray dependencies) || dependencies.Count != 0 ||
                locations[0]["m_Provider"]?.Value<string>() != "UnityEngine.AddressableAssets.ResourceProviders.ContentCatalogProvider" ||
                locations[0]["m_InternalId"]?.Value<string>() != RuntimeToken + "/" + catalogName ||
                data["m_AddressablesVersion"]?.Value<string>() != (catalogName == "catalog.bin" ? "2.10.3" : "1.25.0"))
            {
                throw new InvalidDataException("Encrypted delivery requires the fixed default local catalog and no catalog initialization or update hooks.");
            }
        }

        internal static IResourceLocator[] CheckLocators(IResourceLocator shipped)
        {
            var locators = Addressables.ResourceLocators.ToArray();
            if (locators.Length != 2 || shipped == null || shipped.LocatorId != LocalCatalogId || !locators.Any(locator => ReferenceEquals(locator, shipped)) ||
                locators.Count(locator => locator.LocatorId == BuiltInLocator && locator.GetType().Assembly == typeof(Addressables).Assembly && !locator.Keys.Any()) != 1)
            {
                throw new InvalidDataException("An unexpected Addressables Locator is registered in the fixed delivery session.");
            }
            return locators;
        }

        internal static string Node(string id, string provider, Type type, object data, string localRoot)
        {
            if (id == null || type == null || (string.IsNullOrEmpty(provider) && !IsBuiltInScene(provider, type, data, 0)))
            {
                throw new InvalidDataException("A catalog location has no complete identity.");
            }
            var row = new JArray(CanonicalId(id, localRoot), provider, type.FullName + ", " + type.Assembly.GetName().Name);
            if (type == typeof(IAssetBundleResource))
            {
                if (!(data is AssetBundleRequestOptions bundle))
                    throw new InvalidDataException("An official bundle location has no request options.");
                row.Add(new JArray(bundle.BundleName, NoUnityCacheVersion(bundle.Hash) ? "" : bundle.Hash,
                    bundle.BundleSize, bundle.Crc, bundle.UseUnityWebRequestForLocalBundles));
            }
            return row.ToString(Formatting.None);
        }

        internal static string CanonicalId(string id, string localRoot)
        {
            string normalized = id.Replace('\\', '/');
            string prefix = localRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return normalized.StartsWith(prefix, StringComparison.Ordinal)
                ? RuntimeToken + "/" + normalized.Substring(prefix.Length) : normalized;
        }

        internal static string Row(object key, int candidateIndex, string asset, IEnumerable<string> dependencies)
        {
            return new JArray(key, candidateIndex, asset, new JArray(dependencies)).ToString(Formatting.None);
        }

        internal static string Digest(IEnumerable<string> rows)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string row in rows.OrderBy(value => value, StringComparer.Ordinal))
            {
                byte[] data = Encoding.UTF8.GetBytes(row);
                hash.AppendData(BitConverter.GetBytes(data.Length));
                hash.AppendData(data);
            }
            return AuthenticatedBundleStream.Hex(hash.GetHashAndReset());
        }

        internal static void CheckResolved(IResourceLocator catalog, EncryptedBundleDescription description)
        {
            var scenes = Enumerable.Range(0, SceneManager.sceneCountInBuildSettings).Select(SceneUtility.GetScenePathByBuildIndex)
                .Select(BuiltInSceneId).ToArray();
            var manager = Addressables.ResourceManager;
            if (manager.ResourceProviders.Count(provider => provider.GetType() == typeof(ContentCatalogProvider)) != 1 ||
                manager.ResourceProviders.Count(provider => provider.GetType() == typeof(JsonAssetProvider)) != 1)
                throw new InvalidDataException("The official metadata providers were not registered by default startup.");

            CheckCatalog(catalog, description.Bundles.Select(item => item.Binding), description.RoutesSha256,
                scenes, Addressables.RuntimePath, (location, expected) =>
            {
                var actual = manager.GetResourceProvider(IsBundle(location.ResourceType) ? typeof(IAssetBundleResource) : location.ResourceType,
                    location)?.GetType();
                if (expected != null ? actual != expected : actual == null || actual.FullName != location.ProviderId ||
                    actual.Assembly != typeof(BundledAssetProvider).Assembly)
                    throw new InvalidDataException("Catalog route did not select its expected provider: " + location.PrimaryKey);
            });
        }

        // Audit the official serialized Catalog at build time and again after runtime resolves it.
        internal static Dictionary<string, IResourceLocation> CheckCatalog(IResourceLocator catalog,
            IEnumerable<EncryptedBundleBinding> bindings, string expectedDigest, string[] scenes, string localRoot,
            Action<IResourceLocation, Type> checkProvider = null)
        {
            var rows = new List<string>();
            var seenTransfers = new HashSet<string>(StringComparer.Ordinal);
            var seenMounts = new HashSet<string>(StringComparer.Ordinal);
            var transfers = bindings.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
            var mounts = transfers.Values.ToDictionary(MountId, StringComparer.Ordinal);
            string encryptedProvider = transfers.Values.First().ProviderId;
            var bundles = new Dictionary<string, IResourceLocation>(StringComparer.Ordinal);
            var bundleNodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var bundleKeys = new HashSet<string>(StringComparer.Ordinal);
#if UNITY_6000_3_OR_NEWER
            var officialIdentities = new Dictionary<IResourceLocation, string>(new ResourceLocationComparer());
#else
            var compactBuckets = new HashSet<string>(StringComparer.Ordinal);
            var dependencySets = new HashSet<string>(StringComparer.Ordinal);
#endif

            void CheckEquality(IResourceLocation location, string identity)
            {
#if UNITY_6000_3_OR_NEWER
                // The official 2.10 comparer considers equal hashes the same location during dependency and download deduplication.
                if (officialIdentities.TryGetValue(location, out string previous) && previous != identity)
                    throw new InvalidDataException("Addressables conflates different frozen Catalog locations: " + location.PrimaryKey);
                officialIdentities[location] = identity;
#endif
            }

            string CheckBundle(IResourceLocation bundle)
            {
                if (!IsBundle(bundle.ResourceType) || bundle.HasDependencies)
                    throw new InvalidDataException("Unsupported catalog dependency structure.");
                string id = CanonicalId(bundle.InternalId, localRoot);
                string node = Node(bundle.InternalId, bundle.ProviderId, bundle.ResourceType, bundle.Data, localRoot);
                if (bundleNodes.TryGetValue(id, out string previous) && previous != node)
                    throw new InvalidDataException("Conflicting frozen Bundle route: " + id);
                if (bundles.TryGetValue(id, out var existing) && existing.PrimaryKey != bundle.PrimaryKey)
                    throw new InvalidDataException("A frozen Bundle has conflicting Catalog keys: " + id);
                if (!bundles.ContainsKey(id) && bundleNodes.ContainsKey(id))
                    throw new InvalidDataException("Windows-equivalent frozen Bundle routes: " + id);
                bundles[id] = bundle;
                bundleNodes[id] = node;
                CheckEquality(bundle, node);
                if (bundle.ProviderId == encryptedProvider)
                {
                    if (bundle.ResourceType == typeof(EncryptedMountedBundle))
                    {
                        if (!mounts.ContainsKey(id) || bundle.PrimaryKey != id || bundle.Data != null)
                            throw new InvalidDataException("Unrecognized protected mount: " + id);
                    }
                    else if (!transfers.TryGetValue(id, out var item) || bundle.PrimaryKey != item.RelativePath ||
                        !(bundle.Data is AssetBundleRequestOptions stock) || stock.AssetLoadMode != AssetLoadMode.RequestedAssetAndDependencies ||
                        stock.BundleSize != EncryptedBundleBinding.GetCipherLength(item.PlainLength) || stock.BundleName != item.BundleName ||
                        !NoUnityCacheVersion(stock.Hash) || stock.UseUnityWebRequestForLocalBundles)
                        throw new InvalidDataException("Protected transfer route does not match its frozen cipher identity: " + id);
                    checkProvider?.Invoke(bundle, typeof(EncryptedAssetBundleProvider));
                }
                else
                {
                    if (bundle.ResourceType != typeof(IAssetBundleResource) || !IsStockBundleProvider(bundle.ProviderId) ||
                        id.StartsWith(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith(MountPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("An ordinary Bundle route uses a reserved source or unsupported provider: " + id);
                    checkProvider?.Invoke(bundle, typeof(AssetBundleProvider));
                }
                return node;
            }

            foreach (object key in catalog.Keys)
            {
                if (!catalog.Locate(key, typeof(object), out var locations) || locations.Count == 0)
                    throw new InvalidDataException("An official Catalog key does not resolve: " + key);
                if (locations.Any(location => IsBundle(location.ResourceType)))
                {
                    foreach (var location in locations)
                        CheckBundle(location);
                    if (key is string bundleKey && locations.Count == 1 && locations[0].PrimaryKey == bundleKey)
                    {
                        bundleKeys.Add(bundleKey);
                        continue;
                    }
#if !UNITY_6000_3_OR_NEWER
                    // 1.25 adds a numeric bucket for each distinct dependency list before writing JSON.
                    if (locations.Count >= 2 && key is string compact && int.TryParse(compact, out _) &&
                        compactBuckets.Add(DependencyKeys(locations)))
                        continue;
#endif
                    throw new InvalidDataException("Unexpected or conflicting Catalog Bundle key: " + key);
                }
                int candidateIndex = 0;
                foreach (var location in locations)
                {
                    var depends = location.Dependencies ?? Array.Empty<IResourceLocation>();
                    if (IsBuiltInScene(location.ProviderId, location.ResourceType, location.Data, depends.Count))
                    {
                        int index = Array.IndexOf(scenes, location.InternalId);
                        if (index < 0)
                            throw new InvalidDataException("A built-in scene route is absent from the actual Player scene list.");
                        CheckAlias(key, index);
                        string scene = Node(location.InternalId, location.ProviderId, location.ResourceType, null, localRoot);
                        CheckEquality(location, scene);
                        rows.Add(Row(key, candidateIndex++, scene, Array.Empty<string>()));
                        continue;
                    }
                    CheckAlias(key, -1);
                    if (!IsSupportedAssetProvider(location.ProviderId, depends.Count))
                    {
                        throw new InvalidDataException("Asset routes must retain the official asset or legacy Resources provider.");
                    }
                    checkProvider?.Invoke(location, null);
                    string asset = Node(location.InternalId, location.ProviderId, location.ResourceType, location.Data, localRoot);
                    var nodes = new List<string>(depends.Count);
                    for (int i = 0; i < depends.Count; i++)
                    {
                        var dep = depends[i];
                        nodes.Add(CheckBundle(dep));
                        if (dep.ProviderId != encryptedProvider)
                            continue;
                        string id = CanonicalId(dep.InternalId, localRoot);
                        if (dep.ResourceType == typeof(EncryptedMountedBundle))
                        {
                            if (i == 0 || CanonicalId(depends[i - 1].InternalId, localRoot) != mounts[id].SourceId)
                                throw new InvalidDataException("A protected mount lost its adjacent transfer: " + id);
                            seenMounts.Add(id);
                        }
                        else
                        {
                            if (i + 1 == depends.Count || CanonicalId(depends[i + 1].InternalId, localRoot) != MountId(transfers[id]))
                                throw new InvalidDataException("A protected transfer lost its adjacent mount: " + id);
                            seenTransfers.Add(id);
                        }
                    }
                    if (nodes.Distinct(StringComparer.Ordinal).Count() != nodes.Count)
                        throw new InvalidDataException("A Catalog asset repeats a Bundle dependency: " + location.PrimaryKey);
#if !UNITY_6000_3_OR_NEWER
                    if (depends.Count >= 2)
                        dependencySets.Add(DependencyKeys(depends));
#endif
                    CheckEquality(location, new JArray(asset, new JArray(nodes)).ToString(Formatting.None));
                    rows.Add(Row(key, candidateIndex++, asset, nodes));
                }
            }
            if (seenTransfers.Count != transfers.Count || seenMounts.Count != mounts.Count || Digest(rows) != expectedDigest ||
                bundles.Values.Any(bundle => !bundleKeys.Contains(bundle.PrimaryKey))
#if !UNITY_6000_3_OR_NEWER
                || !compactBuckets.SetEquals(dependencySets)
#endif
                )
            {
                throw new InvalidDataException("Resolved Addressables asset and Bundle routes differ from the independent authoring audit.");
            }
            return bundles;
        }

#if !UNITY_6000_3_OR_NEWER
        private static string DependencyKeys(IEnumerable<IResourceLocation> dependencies) =>
            new JArray(dependencies.Select(location => location.PrimaryKey)).ToString(Formatting.None);
#endif
    }
}
