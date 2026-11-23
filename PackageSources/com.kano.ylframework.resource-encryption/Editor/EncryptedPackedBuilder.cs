using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.Build.Pipeline;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace YLFramework.Resource.Encryption.Editor
{
    internal sealed class EncryptedPackedBuilder : BuildScriptPackedMode
    {
        internal AddressableAssetGroup ProtectedGroup;
        internal string[] PlayerScenes;
        internal string ApplicationNamespace, ReleasePrefix, KeyId;
        internal bool Loopback;
        internal readonly Dictionary<string, EncryptedBundleBinding> Bindings = new Dictionary<string, EncryptedBundleBinding>(StringComparer.Ordinal);
        internal string RoutesSha256
        {
            get; private set;
        }
        internal string CatalogName
        {
            get; private set;
        }
        private AddressableAssetsBuildContext _context;
        private Dictionary<ContentCatalogDataEntry, ContentCatalogDataEntry[]> _expected;
        private ContentCatalogDataEntry[] _authorOrder;
#if UNITY_6000_3_OR_NEWER
        private Dictionary<string, ContentCatalogDataEntry> _localPrefetchDependencies;
#endif

        protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput input, AddressableAssetsBuildContext context)
        {
            if (input.PreviousContentState != null)
                throw new InvalidDataException("Encrypted delivery does not support content updates.");
            _context = context;
            CatalogName = input.RuntimeCatalogFilename;
            try
            {
                TResult result = base.DoBuild<TResult>(input, context);
                if (result is AddressablesPlayerBuildResult built && string.IsNullOrEmpty(built.Error))
                {
                    if (_expected == null || Bindings.Count == 0)
                        throw new InvalidDataException("Official Packed Builder produced no protected bundles.");
#if UNITY_6000_3_OR_NEWER
                    if (_localPrefetchDependencies.Values.Any(entry => !(entry.Data is AssetBundleRequestOptions)))
                        throw new InvalidDataException("Mixed prefetch requires stock options for shipped local Bundles.");
#endif
                    var rows = new List<string>();
                    var counts = new Dictionary<object, int>();
                    // Both pinned Packed Builders serialize locations in this stable InternalId order.
                    foreach (var asset in _authorOrder.OrderBy(entry => entry.InternalId))
                    {
                        string identity = Node(asset);
                        string[] depends = _expected[asset].Select(Node).ToArray();
                        foreach (object key in asset.Keys)
                        {
                            counts.TryGetValue(key, out int index);
                            rows.Add(FrozenAddressablesRoutes.Row(key, index, identity, depends));
                            counts[key] = index + 1;
                        }
                    }
                    RoutesSha256 = FrozenAddressablesRoutes.Digest(rows);
                }
                return result;
            }
            finally { _context = null; }
        }

        protected override string ConstructAssetBundleName(AddressableAssetGroup group, BundledAssetGroupSchema schema, BundleDetails info, string name)
        {
            if (_expected == null)
            {
                // Snapshot original dependencies by object before the legacy serializer compacts their keys.
                var bundleKeys = _context.locations.Where(entry => entry.ResourceType == typeof(IAssetBundleResource))
                    .ToDictionary(entry => entry.Keys[0]);
                _authorOrder = _context.locations.Where(entry => entry.ResourceType != typeof(IAssetBundleResource)).ToArray();
                _expected = _authorOrder.ToDictionary(entry => entry,
                    entry => entry.Dependencies.Select(key => bundleKeys.TryGetValue(key, out var dependency) ? dependency :
                        throw new InvalidDataException("Only official direct Bundle dependencies are supported in encrypted delivery.")).ToArray());
#if UNITY_6000_3_OR_NEWER
                _localPrefetchDependencies = bundleKeys.Values
                    .Where(dep => FrozenAddressablesRoutes.IsStockLocalBundle(dep.InternalId.Replace('\\', '/'), dep.Provider))
                    .ToDictionary(dep => (string)dep.Keys[0], StringComparer.Ordinal);
#endif
                foreach (var entry in _expected.Keys)
                {
                    int index = -1;
                    if (FrozenAddressablesRoutes.IsBuiltInScene(entry.Provider, entry.ResourceType, entry.Data, entry.Dependencies.Count))
                    {
                        var stock = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
                        if (PlayerScenes == null || !stock.SequenceEqual(PlayerScenes, StringComparer.Ordinal) ||
                            (index = Array.FindIndex(stock, path => FrozenAddressablesRoutes.BuiltInSceneId(path) == entry.InternalId)) < 0)
                            throw new InvalidDataException("Built-in scene aliases require the selected Player scenes in enabled Build Settings order.");
                    }
                    else if (!FrozenAddressablesRoutes.IsSupportedAssetProvider(entry.Provider, entry.Dependencies.Count))
                        throw new InvalidDataException("An asset route uses an unsupported provider: " + entry.InternalId + " [" + entry.Provider + "].");
                    foreach (object key in entry.Keys)
                        FrozenAddressablesRoutes.CheckAlias(key, index);
                }
            }

            string output = base.ConstructAssetBundleName(group, schema, info, name);
            string bundleName = Path.GetFileNameWithoutExtension(info.FileName);
            if (group != ProtectedGroup)
            {
#if UNITY_6000_3_OR_NEWER
                // Stock prefetch rejects stripped options before the local no-op, even on unrelated local keys in a Union.
                if (_localPrefetchDependencies.TryGetValue(Path.GetFileName(info.FileName), out var local) && local.Data == null)
                    local.Data = new AssetBundleRequestOptions { BundleName = bundleName };
#endif
                return output;
            }
            var location = _context.locations.Single(entry => entry.ResourceType == typeof(IAssetBundleResource) &&
                entry.Data is AssetBundleRequestOptions option && option.BundleName == bundleName);
            var options = (AssetBundleRequestOptions)location.Data;
            if (options.BundleSize <= 0 || location.Provider != schema.GetBundleCachedProviderId())
                throw new InvalidDataException("Protected Bundle source/options differ from official packing.");
            var binding = new EncryptedBundleBinding(ApplicationNamespace, ReleasePrefix, group.Guid, bundleName, info.Hash.ToString(),
                location.Provider, output.Replace('\\', '/'), KeyId, options.BundleSize, new string('0', 64), Loopback);
            Bindings.Add(bundleName, binding);
            // Binary Addressables 2.10.3 serializes BundleSize as a uint.
            options.BundleSize = checked((uint)EncryptedBundleBinding.GetCipherLength(binding.PlainLength));

            string mountId = FrozenAddressablesRoutes.MountId(binding);
            var mount = new ContentCatalogDataEntry(typeof(EncryptedMountedBundle), mountId, location.Provider, new[] { mountId });
            bool hasOwner = false;
            foreach (var asset in _expected.Keys.ToArray())
            {
                var dependencies = _expected[asset];
                int index = Array.IndexOf(dependencies, location);
                if (index < 0)
                    continue;
                hasOwner = true;
                object key = location.Keys[0];
                int catalogIndex = asset.Dependencies.IndexOf(key);
                if (catalogIndex < 0)
                    throw new InvalidDataException("Cannot split the official protected Bundle dependency.");
                asset.Dependencies.Insert(catalogIndex + 1, mountId);
                _expected[asset] = dependencies.Take(index + 1).Append(mount).Concat(dependencies.Skip(index + 1)).ToArray();
            }
            if (!hasOwner)
                throw new InvalidDataException("A protected Bundle has no supported asset route.");
            _context.locations.Add(mount);
            return output;
        }

        private static string Node(ContentCatalogDataEntry entry) => FrozenAddressablesRoutes.Node(entry.InternalId, entry.Provider,
            entry.ResourceType, entry.Data, Addressables.RuntimePath);
    }
}
