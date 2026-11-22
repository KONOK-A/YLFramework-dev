using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    var rows = new List<string>();
                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    // Both pinned Packed Builders serialize locations in this stable InternalId order.
                    foreach (var asset in _authorOrder.OrderBy(entry => entry.InternalId))
                    {
                        string identity = Node(asset);
                        string[] depends = _expected[asset].Select(Node).ToArray();
                        foreach (string key in asset.Keys.Cast<string>())
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
                foreach (var entry in _expected.Keys)
                {
                    if (!FrozenAddressablesRoutes.IsSupportedAssetProvider(entry.Provider, entry.Dependencies.Count) ||
                        entry.Keys.Any(key => !(key is string alias) || alias.StartsWith(FrozenAddressablesRoutes.MountPrefix, StringComparison.Ordinal)))
                        throw new InvalidDataException("An asset route uses a custom provider, scene, or reserved alias: " + entry.InternalId + " [" + entry.Provider + "].");
                }
            }

            string output = base.ConstructAssetBundleName(group, schema, info, name);
            if (group != ProtectedGroup)
                return output;
            string bundleName = Path.GetFileNameWithoutExtension(info.FileName);
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
