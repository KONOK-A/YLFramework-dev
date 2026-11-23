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
        internal const string MountPrefix = "yl-encrypted-mount/";
        private const string BuiltInLocator = "DynamicResourceLocator";
        private const string LocalCatalogId = "AddressablesMainContentCatalog";

        internal static string MountId(EncryptedBundleBinding item) => MountPrefix + item.BundleName;
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
            if ((key is string alias && !alias.StartsWith(MountPrefix, StringComparison.Ordinal)) ||
                (key is int index && sceneIndex >= 0 && index == sceneIndex))
                return;
            throw new InvalidDataException("Unexpected Catalog alias or built-in scene index.");
        }

        internal static bool IsStockLocalBundle(string id, string provider)
        {
            const string prefix = RuntimeToken + "/";
            string stock = typeof(AssetBundleProvider).FullName;
            return (provider == stock || (provider.StartsWith(stock + "_", StringComparison.Ordinal) &&
                provider.Length == stock.Length + 33 && Guid.TryParseExact(provider.Substring(stock.Length + 1), "N", out _))) &&
                id.StartsWith(prefix, StringComparison.Ordinal) && id.EndsWith(".bundle", StringComparison.Ordinal) &&
                id.Substring(prefix.Length).Split('/').All(part => part.Length != 0 && !part.EndsWith(".", StringComparison.Ordinal) &&
                    !part.EndsWith(" ", StringComparison.Ordinal) && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
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
            string normalized = id.Replace('\\', '/');
            string prefix = localRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                normalized = RuntimeToken + "/" + normalized.Substring(prefix.Length);
            var row = new JArray(normalized, provider, type.FullName + ", " + type.Assembly.GetName().Name);
            if (type == typeof(IAssetBundleResource))
            {
                if (!(data is AssetBundleRequestOptions bundle))
                    throw new InvalidDataException("An official bundle location has no request options.");
                row.Add(new JArray(bundle.BundleName, NoUnityCacheVersion(bundle.Hash) ? "" : bundle.Hash,
                    bundle.BundleSize, bundle.Crc, bundle.UseUnityWebRequestForLocalBundles));
            }
            return row.ToString(Formatting.None);
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
            var rows = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var transfers = description.Bundles.ToDictionary(item => item.Binding.Url, StringComparer.Ordinal);
            var mounts = description.Bundles.ToDictionary(item => MountId(item.Binding), StringComparer.Ordinal);
            var name = description.Bundles[0].Binding.ProviderId;
            string localRoot = Addressables.RuntimePath;
            var scenes = Enumerable.Range(0, SceneManager.sceneCountInBuildSettings).Select(SceneUtility.GetScenePathByBuildIndex)
                .Select(BuiltInSceneId).ToArray();
            var manager = Addressables.ResourceManager;
            if (manager.ResourceProviders.Count(provider => provider.GetType() == typeof(ContentCatalogProvider)) != 1 ||
                manager.ResourceProviders.Count(provider => provider.GetType() == typeof(JsonAssetProvider)) != 1)
                throw new InvalidDataException("The official metadata providers were not registered by default startup.");
            foreach (object key in catalog.Keys)
            {
                if (!catalog.Locate(key, typeof(object), out var locations))
                    continue;
                int candidateIndex = 0;
                foreach (var location in locations)
                {
                    if (IsBundle(location.ResourceType))
                        continue;
                    var depends = location.Dependencies ?? Array.Empty<IResourceLocation>();
                    if (IsBuiltInScene(location.ProviderId, location.ResourceType, location.Data, depends.Count))
                    {
                        int index = Array.IndexOf(scenes, location.InternalId);
                        if (index < 0)
                            throw new InvalidDataException("A built-in scene route is absent from the actual Player scene list.");
                        CheckAlias(key, index);
                        rows.Add(Row(key, candidateIndex++, Node(location.InternalId, location.ProviderId, location.ResourceType, null, localRoot), Array.Empty<string>()));
                        continue;
                    }
                    CheckAlias(key, -1);
                    if (!IsSupportedAssetProvider(location.ProviderId, depends.Count))
                    {
                        throw new InvalidDataException("Asset routes must retain the official asset or legacy Resources provider.");
                    }
                    var assetProvider = manager.GetResourceProvider(location.ResourceType, location)?.GetType();
                    if (assetProvider == null || assetProvider.FullName != location.ProviderId || assetProvider.Assembly != typeof(BundledAssetProvider).Assembly)
                        throw new InvalidDataException("Asset route did not select its expected official asset provider.");
                    foreach (var dep in depends)
                    {
                        if (!IsBundle(dep.ResourceType) || dep.HasDependencies)
                            throw new InvalidDataException("Unsupported catalog dependency structure.");
                        if (dep.ProviderId == name)
                        {
                            if (dep.ResourceType == typeof(EncryptedMountedBundle))
                            {
                                if (!mounts.ContainsKey(dep.InternalId))
                                    throw new InvalidDataException("Unrecognized protected mount.");
                            }
                            else
                            {
                                if (!transfers.TryGetValue(dep.InternalId, out var item) || !(dep.Data is AssetBundleRequestOptions stock) ||
                                    stock.BundleSize != item.CipherLength || stock.BundleName != item.Binding.BundleName || !NoUnityCacheVersion(stock.Hash))
                                {
                                    throw new InvalidDataException("Protected transfer route does not match its frozen cipher identity: " + dep.PrimaryKey);
                                }
                                seen.Add(dep.InternalId);
                            }
                        }
                        Type expected = dep.ProviderId == name ? typeof(EncryptedAssetBundleProvider) : typeof(AssetBundleProvider);
                        if (manager.GetResourceProvider(typeof(IAssetBundleResource), dep)?.GetType() != expected)
                            throw new InvalidDataException("Bundle route did not select its expected provider.");
                    }
                    rows.Add(Row(key, candidateIndex++, Node(location.InternalId, location.ProviderId, location.ResourceType, location.Data, localRoot),
                        depends.Select(dep => Node(dep.InternalId, dep.ProviderId, dep.ResourceType, dep.Data, localRoot))));
                }
            }
            if (seen.Count != transfers.Count || Digest(rows) != description.RoutesSha256)
            {
                throw new InvalidDataException("Resolved Addressables asset and Bundle routes differ from the independent authoring audit.");
            }
        }
    }
}
