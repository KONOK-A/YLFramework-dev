using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;

namespace YLFramework.Resource
{
    internal sealed class ResourceContentCatalog
    {
        internal const string RuntimeToken = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";
        internal const string CatalogId = "AddressablesMainContentCatalog";
        private const string LegacyProvider = "UnityEngine.ResourceManagement.ResourceProviders.LegacyResourcesProvider";
#if UNITY_6000_3_OR_NEWER
        internal const string CatalogName = "catalog.bin";
        private const string AddressablesVersion = "2.10.3";
#else
        internal const string CatalogName = "catalog.json";
        private const string AddressablesVersion = "1.25.0";
#endif

        private readonly ContentCatalogData _data;
        private readonly Dictionary<string, string[]> _bundleRoutes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Identity, AssetBundleRequestOptions Request)> _ordinaryBundles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Type> _assetTypes = new();
        private readonly HashSet<string> _extensionProviders = new(StringComparer.Ordinal);
        private readonly HashSet<string> _suppliedProviders = new(StringComparer.Ordinal);
        private readonly HashSet<string> _builtInRoutes = new(StringComparer.Ordinal);
        private readonly HashSet<(string Id, string Provider, string Key)> _sceneRoutes = new();
        private string[] _rows;

        private ResourceContentCatalog(IResourceLocator locator, ContentCatalogData data = null)
        {
            Locator = locator ?? throw new InvalidDataException("The Addressables Catalog has no business locator.");
            _data = data;
        }

        internal IResourceLocator Locator
        {
            get;
        }
        internal IReadOnlyDictionary<string, string[]> BundleRoutes => _bundleRoutes;
        internal AssetBundleRequestOptions GetOrdinaryBundleRequest(string route) => _ordinaryBundles[route].Request;

        internal static ResourceContentCatalog ReadLocal(string path)
        {
            var data = ReadCatalogData(path);
#if UNITY_6000_3_OR_NEWER
            var create = typeof(ContentCatalogData).GetMethod("CreateCustomLocator", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string) }, null);
            if (create == null)
                throw new InvalidDataException("The installed binary Catalog locator is unsupported.");
            try
            {
                return new ResourceContentCatalog((IResourceLocator)create.Invoke(data, new object[] { CatalogId, null }), data);
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidDataException("The binary Catalog has no usable business locator.", error.InnerException ?? error);
            }
#else
            return new ResourceContentCatalog(data.CreateLocator(), data);
#endif
        }

        internal static ContentCatalogData ReadCatalogData(string path)
        {
            if (Path.GetExtension(path) != Path.GetExtension(CatalogName))
                throw new InvalidDataException("The selected Catalog format does not match the installed Addressables version.");
#if UNITY_6000_3_OR_NEWER
            // 使用当前 Addressables 的二进制读取器。它不会实例化 Provider 或登记全局 Locator。
            var type = typeof(ContentCatalogData);
            var load = type.GetMethod("LoadFromFile", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(bool) }, null);
            var readerField = type.GetField("m_Reader", BindingFlags.Instance | BindingFlags.NonPublic);
            if (load == null || readerField == null)
                throw new InvalidDataException("The installed binary Catalog reader is unsupported.");
            try
            {
                // LoadFromFile 只包装 Addressables 的 Reader。与官方 BinaryAssetProvider 一样从偏移零读取完整对象。
                var wrapper = load.Invoke(null, new object[] { path, false });
                var reader = readerField.GetValue(wrapper);
                var deserialize = readerField.FieldType.GetMethod("ReadObject", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(Type), typeof(uint), typeof(uint).MakeByRefType(), typeof(bool) }, null);
                if (deserialize == null)
                    throw new InvalidDataException("The installed binary Catalog deserializer is unsupported.");
                return (ContentCatalogData)deserialize.Invoke(reader, new object[] { type, 0u, 0u, false }) ??
                    throw new InvalidDataException("The binary Catalog is missing its content data.");
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidDataException("The binary Catalog could not be read.", error.InnerException ?? error);
            }
#else
            var data = JsonUtility.FromJson<ContentCatalogData>(File.ReadAllText(path));
            if (data == null)
                throw new InvalidDataException("The JSON Catalog is missing its content data.");
            return data;
#endif
        }

        internal static void RequireStartupSettings(Stream settings, string catalogRoute, string targetPlatform)
        {
            using var streamReader = new StreamReader(settings, System.Text.Encoding.UTF8, true, 1024, true);
            using var json = new JsonTextReader(streamReader) { MaxDepth = 32 };
            var data = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (json.Read() || catalogRoute != RuntimeToken + "/" + CatalogName ||
                data["m_buildTarget"]?.Value<string>() != targetPlatform ||
                data["m_AddressablesVersion"]?.Value<string>() != AddressablesVersion ||
                !HasBoolean(data, "m_DisableCatalogUpdateOnStart", true) ||
                !HasBoolean(data, "m_IsLocalCatalogInBundle", false) ||
                data["m_CertificateHandlerType"]?["m_ClassName"]?.Value<string>() != "" ||
                !(data["m_ExtraInitializationData"] is JArray initializers) || initializers.Count != 0 ||
                !(data["m_CatalogLocations"] is JArray locations) || locations.Count != 1 ||
                !(locations[0]["m_Keys"] is JArray keys) || keys.Count != 1 || keys[0].Value<string>() != CatalogId ||
                !(locations[0]["m_Dependencies"] is JArray dependencies) || dependencies.Count != 0 ||
                locations[0]["m_Provider"]?.Value<string>() != typeof(ContentCatalogProvider).FullName ||
                locations[0]["m_InternalId"]?.Value<string>() != catalogRoute)
            {
                throw new InvalidDataException("The installed settings must register one local Catalog without hash or initialization hooks.");
            }
            var runtimeData = JsonUtility.FromJson<ResourceManagerRuntimeData>(data.ToString(Formatting.None));
            var location = new ResourceLocationMap("SettingsAudit", runtimeData.CatalogLocations);
            if (!location.Locate(CatalogId, typeof(ContentCatalogData), out var catalogs) || catalogs.Count != 1)
                throw new InvalidDataException("The runtime settings do not register the official Catalog type.");
        }

        private static bool HasBoolean(JObject data, string name, bool expected) =>
            data[name]?.Type == JTokenType.Boolean && data[name].Value<bool>() == expected;

        internal static ResourceContentCatalog InspectLocator(IResourceLocator locator, string runtimeRoot,
            Func<IResourceLocation, string> extensionRoute = null, ResourceManager manager = null)
        {
            var catalog = new ResourceContentCatalog(locator);
            catalog.Inspect(runtimeRoot, extensionRoute, manager);
            return catalog;
        }

        internal IReadOnlyDictionary<string, string[]> Inspect(string runtimeRoot,
            Func<IResourceLocation, string> extensionRoute = null, ResourceManager manager = null)
        {
            _bundleRoutes.Clear();
            _ordinaryBundles.Clear();
            _assetTypes.Clear();
            _builtInRoutes.Clear();
            _sceneRoutes.Clear();
            _extensionProviders.Clear();
            _suppliedProviders.Clear();
            var rows = new List<string>();
            var visited = new Dictionary<IResourceLocation, (string Row, string Route)>();
            var visiting = new HashSet<IResourceLocation>();
            foreach (object key in Locator.Keys)
            {
                if (!(key is string text && text.Length != 0) && !(key is int))
                    throw new InvalidDataException("The Catalog contains an unsupported lookup key.");
                if (!Locator.Locate(key, null, out var locations) || locations == null || locations.Count == 0)
                    throw new InvalidDataException("A Catalog alias has no resource location.");
                for (var index = 0; index < locations.Count; index++)
                {
                    var node = Visit(locations[index]);
                    rows.Add(new JArray(key, index, node.Row).ToString(Formatting.None));
                }
            }

            _rows = rows.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            RequireProviderRegistrations();
            return _bundleRoutes;

            (string Row, string Route) Visit(IResourceLocation location)
            {
                if (location?.ResourceType == null || string.IsNullOrEmpty(location.InternalId) ||
                    string.IsNullOrEmpty(location.PrimaryKey))
                    throw new InvalidDataException("An Addressables location has no complete identity.");
                if (visited.TryGetValue(location, out var saved))
                    return saved;
                if (!visiting.Add(location) || visiting.Count > 128)
                    throw new InvalidDataException("The Catalog dependency graph has a cycle or exceeds the depth limit.");

                var dependencies = (location.Dependencies ?? Array.Empty<IResourceLocation>()).Select(Visit).ToArray();
                string internalId = CanonicalId(location.InternalId, runtimeRoot);
                string suppliedRoute = extensionRoute?.Invoke(location);
                bool isStockBundle = location.ResourceType == typeof(IAssetBundleResource) &&
                    IsStockId(location.ProviderId, typeof(AssetBundleProvider));
                bool isBuiltInScene = IsBuiltInScene(location);
                bool isPackedProvider = IsStockId(location.ProviderId, typeof(BundledAssetProvider));
                bool isScene = location.ResourceType == typeof(SceneInstance) && location.Data == null &&
                    (isBuiltInScene || location.ProviderId == typeof(SceneProvider).FullName || isPackedProvider && dependencies.Length > 0) &&
                    dependencies.All(item => item.Route != null);
                bool isLegacy = location.ProviderId == LegacyProvider && dependencies.Length == 0;

                string route = suppliedRoute ?? (isStockBundle ? internalId : null);
                var identity = new JArray(internalId, location.ProviderId, TypeName(location.ResourceType), location.PrimaryKey);
                if (location.Data is AssetBundleRequestOptions options)
                {
                    identity.Add(new JArray(options.BundleName, options.Hash, options.BundleSize, options.Crc,
                        options.UseUnityWebRequestForLocalBundles, options.UseCrcForCachedBundle));
                }

                if (suppliedRoute != null)
                    _extensionProviders.Add(location.ProviderId);
                else if (isStockBundle)
                {
                    bool localFileTransport = location.Data is AssetBundleRequestOptions stock && !stock.UseUnityWebRequestForLocalBundles ||
                        location.Data == null && internalId.StartsWith(RuntimeToken + "/", StringComparison.Ordinal);
                    if (!localFileTransport || !internalId.EndsWith(".bundle", StringComparison.Ordinal))
                        throw new InvalidDataException("A verified ordinary Bundle must use the official local-file transport.");
                    string bundleIdentity = identity.ToString(Formatting.None);
                    if (_ordinaryBundles.TryGetValue(route, out var previous) && previous.Identity != bundleIdentity)
                        throw new InvalidDataException("Equivalent Bundle routes disagree on request identity.");
                    _ordinaryBundles[route] = (bundleIdentity, location.Data as AssetBundleRequestOptions);
                }
                else if (isBuiltInScene)
                {
                    if (manager != null)
                        RequireBuiltInScenePath(internalId);
                    _builtInRoutes.Add(identity.ToString(Formatting.None));
                }
                else
                {
                    bool isBundledAsset = isPackedProvider &&
                        dependencies.Length > 0 && dependencies.All(item => item.Route != null);
                    if (!isBundledAsset && !isScene && !isLegacy)
                        throw new InvalidDataException("An unsupported Catalog provider or business dependency was declared.");
                    if (location.ResourceType == typeof(object) ||
                        (!typeof(UnityEngine.Object).IsAssignableFrom(location.ResourceType) && !isScene))
                        throw new InvalidDataException("The Catalog declares an unavailable asset type.");
                    _assetTypes.Add(location.ResourceType);
                    // 官方 Packed Catalog 把拥有者排在资产依赖首位，其后是已展开的 Bundle 依赖。
                    if ((isBundledAsset || isScene) && dependencies.Length > 0)
                        AddBundleEdges(dependencies[0].Route, dependencies.Skip(1).Select(item => item.Route));
                    if (isLegacy)
                        _builtInRoutes.Add(identity.ToString(Formatting.None));
                }

                if (isScene)
                    _sceneRoutes.Add((internalId, location.ProviderId, location.PrimaryKey));

                if (manager != null && !isBuiltInScene && location.ProviderId != typeof(SceneProvider).FullName)
                {
                    // SceneProvider owns scene loading. The packed location's asset provider is only a Catalog identity.
                    var provider = manager.GetResourceProvider(isScene ? null :
                        suppliedRoute == null ? location.ResourceType : typeof(IAssetBundleResource), location);
                    if (provider == null || provider.ProviderId != location.ProviderId ||
                        (suppliedRoute == null && (provider.GetType().Assembly != typeof(BundledAssetProvider).Assembly ||
                            isStockBundle && provider.GetType() != typeof(AssetBundleProvider) ||
                            isPackedProvider && provider.GetType() != typeof(BundledAssetProvider))))
                        throw new InvalidDataException("The selected Catalog location resolved to an unexpected provider.");
                }

                if (!isBuiltInScene && location.ProviderId != typeof(SceneProvider).FullName)
                    _suppliedProviders.Add(location.ProviderId);
                if (route != null)
                {
                    var children = dependencies.Select(item => item.Route).Where(child => child != route).ToArray();
                    if (children.Any(child => child == null))
                        throw new InvalidDataException("A Bundle depends on a non-Bundle resource.");
                    AddBundleEdges(route, children);
                }

                identity.Add(new JArray(dependencies.Select(item => item.Row)));
                using var hash = SHA256.Create();
                var result = (ResourceContentFileIO.Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString(Formatting.None)))), route);
                visiting.Remove(location);
                visited[location] = result;
                return result;
            }
        }

        private void AddBundleEdges(string route, IEnumerable<string> children)
        {
            if (_bundleRoutes.TryGetValue(route, out var existing))
                children = existing.Concat(children);
            _bundleRoutes[route] = children.Where(child => child != route).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        internal void RequireCompatibleWith(ResourceContentCatalog baseline, IEnumerable<Type> additionalAssetTypes = null)
        {
            if (_rows == null)
                throw new InvalidOperationException("Inspect the candidate Catalog graph before checking compatibility.");
            if (baseline._rows == null)
                baseline.ReadBaselineCapabilities();
            var allowedTypes = new HashSet<Type>(baseline._assetTypes);
            if (additionalAssetTypes != null)
                allowedTypes.UnionWith(additionalAssetTypes);
            if (!_assetTypes.IsSubsetOf(allowedTypes) || !_builtInRoutes.IsSubsetOf(baseline._builtInRoutes) ||
                !ProviderTypes().IsSubsetOf(baseline.ProviderTypes()) ||
                ProviderData(_data.InstanceProviderData) != ProviderData(baseline._data.InstanceProviderData) ||
                ProviderData(_data.SceneProviderData) != ProviderData(baseline._data.SceneProviderData))
                throw new InvalidDataException("The candidate Catalog requires asset types or provider capabilities absent from its Player baseline.");
        }

        internal void RequireManifest(ResourceContentManifest manifest)
        {
            var bundles = manifest.Artifacts.Values.Where(artifact =>
                artifact.Kind == ResourceContentArtifactKind.Bundle || artifact.Kind == ResourceContentArtifactKind.Ciphertext).ToArray();
            if (bundles.Length != _bundleRoutes.Count || bundles.Any(artifact =>
                !_bundleRoutes.TryGetValue(artifact.Route, out var dependencies) ||
                !dependencies.SequenceEqual(artifact.Dependencies.OrderBy(route => route, StringComparer.Ordinal))))
                throw new InvalidDataException("The Catalog Bundle routes or dependency graph differ from the signed manifest.");
        }

        internal void RequireOfficialLocator(IResourceLocator official, string runtimeRoot,
            Func<IResourceLocation, string> extensionRoute)
        {
            var locators = Addressables.ResourceLocators.ToArray();
            if (!locators.Any(locator => ReferenceEquals(locator, official)) || official?.LocatorId != CatalogId ||
                locators.Length != 2 || locators.Count(locator => locator.LocatorId == "DynamicResourceLocator" &&
                    locator.GetType().Assembly == typeof(Addressables).Assembly && !locator.Keys.Any()) != 1)
                throw new InvalidDataException("The installed session did not initialize exactly one business Catalog.");
            var actual = InspectLocator(official, runtimeRoot, extensionRoute, Addressables.ResourceManager);
            if (!_rows.SequenceEqual(actual._rows, StringComparer.Ordinal))
                throw new InvalidDataException("The official business Catalog resolves differently from the verified Catalog.");
        }

        internal static string CanonicalId(string id, string runtimeRoot)
        {
            string normalized = id.Replace('\\', '/');
            string prefix = runtimeRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? RuntimeToken + "/" + normalized.Substring(prefix.Length) : normalized;
        }

        internal static bool IsStockId(string id, Type type)
        {
            var name = type.FullName;
            return id == name || (id != null && id.Length == name.Length + 33 &&
                id.StartsWith(name + "_", StringComparison.Ordinal) && Guid.TryParseExact(id.Substring(name.Length + 1), "N", out _));
        }

        internal static bool IsBuiltInScene(IResourceLocation location) => location?.ResourceType == typeof(SceneInstance) &&
            string.IsNullOrEmpty(location.ProviderId) && location.Data == null && (location.Dependencies?.Count ?? 0) == 0;

        internal string GetScenePath(IResourceLocation location, string runtimeRoot)
        {
            var id = CanonicalId(location.InternalId, runtimeRoot);
            if (!_sceneRoutes.Contains((id, location.ProviderId, location.PrimaryKey)) || location.Data != null)
                throw new InvalidDataException("A scene route is absent from the verified Catalog.");
            return IsBuiltInScene(location) ? RequireBuiltInScenePath(id) : location.InternalId;
        }

        internal string GetAtlasSpriteId(IResourceLocation location, string runtimeRoot, ResourceManager manager)
        {
            // Addressables creates these locations on demand as <alias>[<sprite>] with one SpriteAtlas dependency.
            if (location.ResourceType != typeof(Sprite) || location.Data != null || location.Dependencies?.Count != 1 ||
                manager.GetResourceProvider(typeof(Sprite), location)?.GetType() != typeof(AtlasSpriteProvider) ||
                !ResourceManagerConfig.ExtractKeyAndSubKey(location.PrimaryKey, out var alias, out var sprite) ||
                string.IsNullOrEmpty(sprite) || !Locator.Locate(alias, typeof(SpriteAtlas), out var signedAtlases))
                throw new InvalidDataException("A Sprite route has no verified Catalog atlas.");

            var atlas = location.Dependencies[0];
            if (atlas?.ResourceType != typeof(SpriteAtlas) || !IsStockId(atlas.ProviderId, typeof(BundledAssetProvider)) ||
                location.InternalId != $"{atlas.InternalId}[{sprite}]" || !signedAtlases.Any(signed =>
                    signed.ProviderId == atlas.ProviderId && signed.PrimaryKey == atlas.PrimaryKey &&
                    CanonicalId(signed.InternalId, runtimeRoot) == CanonicalId(atlas.InternalId, runtimeRoot)))
                throw new InvalidDataException("A Sprite route has no verified Catalog atlas.");

            return location.InternalId;
        }

        private void ReadBaselineCapabilities()
        {
            foreach (object key in Locator.Keys)
            {
                if (!Locator.Locate(key, null, out var locations) || locations == null)
                    continue;
                foreach (var location in locations)
                {
                    var type = location.ResourceType;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(type) || type == typeof(SceneInstance))
                        _assetTypes.Add(type);
                    if (location.ProviderId == LegacyProvider ||
                        (string.IsNullOrEmpty(location.ProviderId) && type == typeof(SceneInstance)))
                    {
                        var identity = new JArray(location.InternalId.Replace('\\', '/'), location.ProviderId,
                            TypeName(type), location.PrimaryKey);
                        _builtInRoutes.Add(identity.ToString(Formatting.None));
                    }
                }
            }
        }

        private void RequireProviderRegistrations()
        {
            if (_data == null)
                return;
            var declared = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var provider in _data.ResourceProviderData ?? Enumerable.Empty<ObjectInitializationData>())
            {
                var type = provider.ObjectType.Value;
                if (string.IsNullOrEmpty(provider.Id) || type == null ||
                    !(type == typeof(AssetBundleProvider) && IsStockId(provider.Id, type) ||
                      type == typeof(BundledAssetProvider) && IsStockId(provider.Id, type) ||
                      type == typeof(AtlasSpriteProvider) && provider.Id == typeof(AtlasSpriteProvider).FullName ||
                      provider.Id == LegacyProvider && type.FullName == LegacyProvider ||
                      _extensionProviders.Contains(provider.Id) && typeof(IResourceProvider).IsAssignableFrom(type)))
                    throw new InvalidDataException("The Catalog registers an unsupported resource provider.");

                // 1.25 官方 Catalog 可重复输出相同注册。区分同一项与同 ID 的不同配置。
                string identity = ProviderData(provider);
                if (declared.TryGetValue(provider.Id, out var existing) && existing != identity)
                    throw new InvalidDataException("The Catalog has ambiguous registrations for one resource provider.");
                declared[provider.Id] = identity;
            }
            if (!_suppliedProviders.IsSubsetOf(declared.Keys))
                throw new InvalidDataException("The Catalog refers to unregistered providers: " +
                    string.Join(", ", _suppliedProviders.Except(declared.Keys, StringComparer.Ordinal)));

            if (_data.SceneProviderData.ObjectType.Value != typeof(SceneProvider) ||
                _data.InstanceProviderData.ObjectType.Value != typeof(InstanceProvider))
                throw new InvalidDataException("The Catalog must use the default scene and instance providers.");
        }

        private HashSet<string> ProviderTypes() =>
            new(_data.ResourceProviderData.Select(provider =>
                TypeName(provider.ObjectType.Value) + "\n" + (provider.Data ?? "")), StringComparer.Ordinal);

        private static string ProviderData(ObjectInitializationData provider) =>
            TypeName(provider.ObjectType.Value) + "\n" + provider.Id + "\n" + (provider.Data ?? "");
        private static string TypeName(Type type) => type?.FullName + ", " + type?.Assembly.GetName().Name;
        private static string RequireBuiltInScenePath(string id)
        {
            for (int index = 0; index < SceneManager.sceneCountInBuildSettings; index++)
            {
                var path = SceneUtility.GetScenePathByBuildIndex(index);
                if (BuiltInSceneId(path) == id)
                    return path;
            }
            throw new InvalidDataException("The Catalog references a built-in scene absent from the Player.");
        }

        private static string BuiltInSceneId(string path)
        {
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return null;
            var start = path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? "Assets/".Length : 0;
            return path.Substring(start, path.Length - start - ".unity".Length);
        }
    }
}
