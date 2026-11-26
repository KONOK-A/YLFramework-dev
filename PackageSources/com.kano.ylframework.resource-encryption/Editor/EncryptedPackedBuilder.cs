using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.Build.Pipeline;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Editor.Resource;

namespace YLFramework.Resource.Encryption.Editor
{
    internal sealed class EncryptedPackedBuilder : BuildScriptPackedMode
    {
        internal IReadOnlyList<AddressableAssetGroup> ProtectedGroups;
        internal string[] PlayerScenes;
        internal string ApplicationNamespace;
        internal string ReleaseId;
        internal string ReleasePrefix;
        internal bool Loopback;
        internal bool Versioned;
        internal EncryptedBundleDescription PreviousDelivery;
        internal string PreviousCatalogPath;
        internal string DeliveryCatalogPath;

        internal string RoutesSha256
        {
            get; private set;
        }
        internal string CatalogName
        {
            get; private set;
        }
        internal IReadOnlyDictionary<string, PackedBundle> Bundles => _bundles;
        internal IReadOnlyDictionary<string, AssetRoute[]> Assets => _assets;

        private readonly Dictionary<string, PackedBundle> _bundles = new Dictionary<string, PackedBundle>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private AddressableAssetsBuildContext _context;
        private Dictionary<string, (AddressableAssetGroup Group, ContentCatalogDataEntry Entry)> _original;
        private Dictionary<string, string> _originalKeys;
        private Dictionary<string, AssetRoute[]> _assets;
        private Dictionary<ContentCatalogDataEntry, ContentCatalogDataEntry[]> _dependencies;
        private Dictionary<ContentCatalogDataEntry, object[]> _keys;
        private IResourceLocator _previousCatalog;

        protected override string ProcessAllGroups(AddressableAssetsBuildContext context)
        {
            var embedded = ProtectedGroups.Where(group => group.GetSchema<EncryptedBundleGroupSchema>().DeliveryMode == EncryptedBundleDeliveryMode.Embedded)
                .Select(group => group.GetSchema<BundledAssetGroupSchema>()).ToArray();
            if (embedded.Length == 0)
                return base.ProcessAllGroups(context);

            // The official pair check assumes a RuntimePath route always copies plaintext into StreamingAssets.
            // Present the private path only during group processing, then restore the real routes before SBP sees them.
            // Both pinned versions' internal setter only changes the in-memory reference and does not dirty the asset.
            var setId = typeof(ProfileValueReference).GetProperty(nameof(ProfileValueReference.Id))?.GetSetMethod(true)
                ?? throw new InvalidDataException("Embedded delivery requires the pinned Addressables profile reference API.");
            var original = new List<(ProfileValueReference Reference, string Id)>(embedded.Length);
            try
            {
                foreach (var schema in embedded)
                {
                    if (NormalizeLoadPrefix(schema.LoadPath.GetValue(context.Settings)) != FrozenAddressablesRoutes.EmbeddedPrefix ||
                        !Path.IsPathRooted(schema.BuildPath.GetValue(context.Settings)) ||
                        string.IsNullOrEmpty(context.Settings.profileSettings.GetValueById(context.Settings.activeProfileId, schema.BuildPath.Id)))
                        throw new InvalidDataException("Embedded packing requires the canonical authored LoadPath and a private BuildPath profile reference.");
                    original.Add((schema.LoadPath, schema.LoadPath.Id));
                    setId.Invoke(schema.LoadPath, new object[] { schema.BuildPath.Id });
                }
                return base.ProcessAllGroups(context);
            }
            finally
            {
                foreach (var entry in original)
                    setId.Invoke(entry.Reference, new object[] { entry.Id });
            }
        }

        protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput input, AddressableAssetsBuildContext context)
        {
            if ((input.PreviousContentState != null) != (PreviousDelivery != null) ||
                (PreviousDelivery != null && (string.IsNullOrEmpty(PreviousCatalogPath) || string.IsNullOrEmpty(DeliveryCatalogPath))) ||
                ProtectedGroups == null || ProtectedGroups.Count == 0 || _original != null)
                throw new InvalidDataException("Expected one Packed Builder invocation for the selected delivery mode.");
            _context = context;
            CatalogName = input.RuntimeCatalogFilename;
            try
            {
                if (PreviousDelivery != null)
                    _previousCatalog = CreateLocator(ReadCatalog(PreviousCatalogPath));
                TResult result = base.DoBuild<TResult>(input, context);
                if (result is AddressablesPlayerBuildResult built && string.IsNullOrEmpty(built.Error))
                {
                    if (_original == null || _bundles.Count != _original.Count ||
                        ProtectedGroups.Any(group => !_bundles.Values.Any(bundle => bundle.Binding?.GroupGuid == group.Guid)))
                        throw new InvalidDataException("Official Packed Builder did not produce the attributed protected and ordinary Bundle inventory.");
                    if (PreviousDelivery != null)
                        FinalizeUpdateCatalog();
                    RoutesSha256 = FreezeRoutes();
                }
                return result;
            }
            finally
            {
                _context = null;
            }
        }

        protected override string ConstructAssetBundleName(AddressableAssetGroup group, BundledAssetGroupSchema schema,
            BundleDetails info, string name)
        {
            if (_original == null)
                CaptureSbpRoutes();

            string filename = base.ConstructAssetBundleName(group, schema, info, name);
            if (Versioned && (filename.Length > 64 || filename.Any(character =>
                character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))))
            {
                // PackSeparately can include the authored address, including separators in 1.25.
                // Use a short deterministic leaf for the Windows publication tree.
                string suffix = schema.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash ? "_" + info.Hash : "";
                string logicalName = filename.Substring(0, filename.Length - ".bundle".Length - suffix.Length);
                filename = "yl-" + Hash128.Compute(logicalName) + suffix + ".bundle";
            }
            string internalName = Path.GetFileNameWithoutExtension(info.FileName);
            if (!_originalKeys.TryGetValue(internalName, out string originalKey) || !_original.TryGetValue(originalKey, out var source) ||
                (group != null && source.Group != group) || source.Group.GetSchema<BundledAssetGroupSchema>() != schema ||
                !_filenames.Add(filename))
                throw new InvalidDataException("Cannot uniquely attribute official Bundle " + filename + " to its SBP group and internal name.");
            CheckBundleLeaf(filename);
            var location = source.Entry;
            string normalizedId = location.InternalId.Replace('\\', '/');
            string loadPrefix = NormalizeLoadPrefix(schema.LoadPath.GetValue(source.Group.Settings));
            if (normalizedId != loadPrefix + originalKey || location.Provider != schema.GetBundleCachedProviderId())
                throw new InvalidDataException("Bundle options or source route differ from the original SBP location: " + internalName);

            EncryptedBundleBinding binding = null;
            string hash = info.Hash.ToString();
            string installedName = filename;
            if (schema.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash)
            {
                // Both supported builders keep the temporary name as the Catalog key but strip the build hash from the file and route.
                string suffix = "_" + hash + ".bundle";
                if (!filename.EndsWith(suffix, StringComparison.Ordinal) || ProtectedGroups.Contains(source.Group) ||
                    !FrozenAddressablesRoutes.IsStockLocalBundle(loadPrefix + filename, location.Provider))
                    throw new InvalidDataException("The ordinary local NoHash Bundle has an unexpected Packed Builder name: " + filename);
                installedName = filename.Substring(0, filename.Length - suffix.Length) + ".bundle";
                CheckBundleLeaf(installedName);
                if (!_filenames.Add(installedName))
                    throw new InvalidDataException("The final ordinary local Bundle filename collides: " + installedName);
            }
            string expectedId = loadPrefix + installedName;
            if (ProtectedGroups.Contains(source.Group))
            {
                if (group == null || !(location.Data is AssetBundleRequestOptions options) || options.BundleName != internalName ||
                    options.BundleSize <= 0 || !FrozenAddressablesRoutes.NoUnityCacheVersion(options.Hash) ||
                    options.UseUnityWebRequestForLocalBundles)
                    throw new InvalidDataException("A protected Bundle needs official requested-asset options and a non-shared authoring group.");
                var auth = group.GetSchema<EncryptedBundleGroupSchema>();
                string expectedPrefix = auth.DeliveryMode == EncryptedBundleDeliveryMode.Embedded
                    ? FrozenAddressablesRoutes.EmbeddedPrefix : ReleasePrefix;
                if (loadPrefix != expectedPrefix)
                    throw new InvalidDataException("Protected Bundle LoadPath changed before official packing: " + group.Name);
                binding = new EncryptedBundleBinding(ApplicationNamespace, ReleaseId, ReleasePrefix, auth.DeliveryMode, group.Guid,
                    internalName, hash, location.Provider, filename, auth.KeyId, options.BundleSize, new string('0', 64), Loopback);
                expectedId = binding.SourceId;
                // The next Packed Builder step replaces exactly the old SBP key suffix, not the load prefix.
                // Insert the GUID in the logical path only; official filenames/source paths remain unchanged.
                location.InternalId = loadPrefix + group.Guid + "/" + originalKey;
                options.BundleSize = checked((uint)EncryptedBundleBinding.GetCipherLength(binding.PlainLength));
                if (PreviousDelivery == null)
                    SplitTransferAndMount(location, binding);
            }
            else
            {
#if UNITY_6000_3_OR_NEWER
                // Stock prefetch checks the request options before its local no-op for a mixed Union.
                if (location.Data == null && FrozenAddressablesRoutes.IsStockLocalBundle(expectedId, location.Provider))
                    location.Data = new AssetBundleRequestOptions { BundleName = internalName };
#endif
                if (!(location.Data is AssetBundleRequestOptions ordinary) || ordinary.BundleName != internalName)
                    throw new InvalidDataException("A stock Bundle has no attributable official options: " + filename);
            }

            var packed = new PackedBundle(source.Group.Guid, group == null, originalKey, installedName, filename, hash,
                expectedId, location.Provider, binding, ReadGraph(_context.bundleToImmediateBundleDependencies, originalKey),
                ReadGraph(_context.bundleToExpandedBundleDependencies, originalKey));
            if (binding == null && !FrozenAddressablesRoutes.IsStockLocalBundle(expectedId, location.Provider) &&
                _previousCatalog != null && _previousCatalog.Locate(filename, typeof(IAssetBundleResource), out var locations) &&
                locations.Count == 1 && locations[0].PrimaryKey == filename &&
                FrozenAddressablesRoutes.IsStockBundleProvider(locations[0].ProviderId) &&
                !FrozenAddressablesRoutes.IsStockLocalBundle(
                    FrozenAddressablesRoutes.CanonicalId(locations[0].InternalId, Addressables.RuntimePath), locations[0].ProviderId))
            {
                // Both official updaters can delete the new loose file when they restore A's route.
                string sbpPath = Path.GetFullPath(Path.Combine(source.Group.Settings.buildSettings.bundleBuildPath, originalKey));
                using var bytes = OwnedWindowsFiles.OpenRead(sbpPath);
                packed.PreviousOrdinary = locations[0];
                packed.SbpSha256 = AuthenticatedBundleStream.Hash(bytes, default);
            }
            if (!_bundles.TryAdd(internalName, packed))
                throw new InvalidDataException("Duplicate official internal Bundle name: " + internalName);
            return filename;
        }

        private void FinalizeUpdateCatalog()
        {
            // The official updater can retain A's exact route and request options after PostProcessBundles.
            // Take that decision before deriving B's encrypted transport and mount locations.
            string officialPath = Path.Combine(Addressables.BuildPath, CatalogName);
            ContentCatalogData official = ResourceContentUpdateAuthoring.ReadCatalogData(officialPath);
            CheckOfficialUpdateGraph(CreateLocator(official));
            var prior = PreviousDelivery.Bundles.ToDictionary(row => row.Binding.SourceId, StringComparer.Ordinal);
            var priorByGroupPath = PreviousDelivery.Bundles.ToDictionary(row => row.Binding.GroupPath, StringComparer.Ordinal);
            foreach (var asset in _keys.Keys)
            {
                // The legacy serializer replaces a multi-Bundle dependency list with one synthetic bucket key.
                // Expand the previously verified graph before serializing the protected mount routes.
                asset.Dependencies.Clear();
                asset.Dependencies.AddRange(_dependencies[asset].Select(dependency => dependency.Keys[0]));
            }
            foreach (var packed in _bundles.Values)
            {
                var location = _original[packed.OriginalKey].Entry;
                string id = FrozenAddressablesRoutes.CanonicalId(location.InternalId, Addressables.RuntimePath);
                if (!(location.Data is AssetBundleRequestOptions options) || options.BundleName != packed.InternalName ||
                    location.Provider != packed.ProviderId || !Equals(location.Keys[0], packed.CatalogKey))
                    throw new InvalidDataException("Official update retained Bundle metadata that differs from the audited SBP inventory: " + id);
                location.Keys.RemoveRange(1, location.Keys.Count - 1);
                if (packed.Binding == null)
                {
                    var previous = packed.PreviousOrdinary;
                    bool retained = previous != null &&
                        FrozenAddressablesRoutes.CanonicalId(previous.InternalId, Addressables.RuntimePath) == id;
                    if (retained)
                    {
                        string previousNode = FrozenAddressablesRoutes.Node(previous.InternalId, previous.ProviderId,
                            previous.ResourceType, previous.Data, Addressables.RuntimePath);
                        if (previousNode != Node(location))
                            throw new InvalidDataException("Official update changed ordinary Bundle metadata at an immutable A route: " + id);
                        packed.RetainedOrdinaryRoute = id;
                        packed.ReportSourceId = id;
                    }
                    else if (id != packed.SourceId)
                        throw new InvalidDataException("Official update retained a different ordinary Bundle than the audited SBP result: " + id);
                    continue;
                }
                var binding = packed.Binding;
                if (options.BundleSize != EncryptedBundleBinding.GetCipherLength(binding.PlainLength))
                    throw new InvalidDataException("Official update changed the protected Bundle length: " + id);
                if (priorByGroupPath.TryGetValue(binding.GroupPath, out var original) && original.Binding.BundleHash == binding.BundleHash)
                    packed.Retained = original;
                if (id != packed.SourceId)
                {
                    if (!prior.TryGetValue(id, out var retained) || retained.Binding.GroupGuid != binding.GroupGuid ||
                        retained.Binding.BundleName != binding.BundleName || retained.Binding.BundleHash != binding.BundleHash ||
                        retained.Binding.RelativePath != binding.RelativePath || retained.Binding.Mode != binding.Mode ||
                        retained.Binding.PlainLength != binding.PlainLength)
                        throw new InvalidDataException("Official update retained protected content that differs from the audited SBP result: " + id);
                    packed.Retained = retained;
                }
                packed.ReportSourceId = id;
                location.InternalId = binding.SourceId;
                SplitTransferAndMount(location, binding);
            }
            var delivery = new ContentCatalogData(_context.locations.OrderBy(entry => entry.InternalId).ToList(),
                ResourceManagerRuntimeData.kCatalogAddress)
            {
                InstanceProviderData = official.InstanceProviderData,
                SceneProviderData = official.SceneProviderData,
                ResourceProviderData = official.ResourceProviderData,
                BuildResultHash = official.BuildResultHash
            };
            using var output = OwnedWindowsFiles.CreateTemporaryPinned(DeliveryCatalogPath);
#if UNITY_6000_3_OR_NEWER
            byte[] bytes = delivery.SerializeToByteArray();
#else
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(delivery));
#endif
            output.Write(bytes, 0, bytes.Length);
            output.Flush(true);
        }

        private void CheckOfficialUpdateGraph(IResourceLocator locator)
        {
            var rows = new List<string>();
            foreach (object key in locator.Keys)
            {
                if (!locator.Locate(key, typeof(object), out var locations) || locations.Count == 0)
                    throw new InvalidDataException("An official update Catalog alias has no location.");
                if (locations.All(location => FrozenAddressablesRoutes.IsBundle(location.ResourceType)))
                    continue;
                for (int index = 0; index < locations.Count; index++)
                {
                    var asset = locations[index];
                    var dependencies = asset.Dependencies ?? Array.Empty<IResourceLocation>();
                    rows.Add(FrozenAddressablesRoutes.Row(key, index, LocationNode(asset), dependencies.Select(LocationNode)));
                }
            }
            if (FrozenAddressablesRoutes.Digest(rows) != FreezeRoutes())
                throw new InvalidDataException("Official update changed the audited SBP asset routes or dependency graph.");

            string LocationNode(IResourceLocation location) => FrozenAddressablesRoutes.Node(location.InternalId, location.ProviderId,
                location.ResourceType, location.Data, Addressables.RuntimePath);
        }

        private void CaptureSbpRoutes()
        {
            _original = new Dictionary<string, (AddressableAssetGroup, ContentCatalogDataEntry)>(StringComparer.Ordinal);
            _originalKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var owners = new Dictionary<string, AddressableAssetGroup>(StringComparer.Ordinal);
            foreach (var pair in _context.assetGroupToBundles)
            {
                foreach (string key in pair.Value)
                {
                    if (!owners.TryAdd(key, pair.Key) || (_context.bundleToAssetGroup.TryGetValue(key, out string guid) && guid != pair.Key.Guid))
                        throw new InvalidDataException("Conflicting official SBP Bundle group ownership: " + key);
                }
            }
            foreach (var entry in _context.locations.Where(entry => entry.ResourceType == typeof(IAssetBundleResource)))
            {
                if (entry.Keys.Count != 1 || !(entry.Keys[0] is string key) || entry.Dependencies.Count != 0 ||
                    !owners.TryGetValue(key, out var owner) || !_original.TryAdd(key, (owner, entry)) ||
                    !_originalKeys.TryAdd(Path.GetFileNameWithoutExtension(key), key))
                    throw new InvalidDataException("Ambiguous or unattributed official SBP Bundle location.");
            }
            if (_original.Count != owners.Count)
                throw new InvalidDataException("SBP Bundle owners and Bundle locations do not agree.");

            _dependencies = new Dictionary<ContentCatalogDataEntry, ContentCatalogDataEntry[]>();
            _keys = new Dictionary<ContentCatalogDataEntry, object[]>();
            foreach (var entry in _context.locations.Where(entry => entry.ResourceType != typeof(IAssetBundleResource)))
            {
                int sceneIndex = -1;
                if (FrozenAddressablesRoutes.IsBuiltInScene(entry.Provider, entry.ResourceType, entry.Data, entry.Dependencies.Count))
                {
                    var stock = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
                    if (PlayerScenes == null || !stock.SequenceEqual(PlayerScenes, StringComparer.Ordinal) ||
                        (sceneIndex = Array.FindIndex(stock, path => FrozenAddressablesRoutes.BuiltInSceneId(path) == entry.InternalId)) < 0)
                        throw new InvalidDataException("Built-in scene aliases require the selected Player scenes in enabled Build Settings order.");
                }
                else if (!FrozenAddressablesRoutes.IsSupportedAssetProvider(entry.Provider, entry.Dependencies.Count))
                    throw new InvalidDataException("An asset route uses an unsupported provider: " + entry.InternalId + " [" + entry.Provider + "].");

                foreach (object key in entry.Keys)
                    FrozenAddressablesRoutes.CheckAlias(key, sceneIndex);
                if (entry.Keys.Count != entry.Keys.Distinct().Count())
                    throw new InvalidDataException("Duplicate alias on official asset route: " + entry.InternalId);
                _keys.Add(entry, entry.Keys.ToArray());
                _dependencies.Add(entry, entry.Dependencies.Select(key => key is string bundle && _original.TryGetValue(bundle, out var dependency)
                    ? dependency.Entry : throw new InvalidDataException("Only official flat Bundle dependencies are supported.")).ToArray());
            }
            _assets = _context.GuidToCatalogLocation.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Select(entry =>
                new AssetRoute(entry.InternalId, _keys[entry], entry.Dependencies.Cast<string>().ToArray())).ToArray(), StringComparer.Ordinal);
        }

        private static string[] ReadGraph(Dictionary<string, List<string>> graph, string key) => graph.TryGetValue(key, out var names)
            ? names.Where(name => name != key).ToArray() : throw new InvalidDataException("SBP omitted Bundle dependencies for " + key);

        private void SplitTransferAndMount(ContentCatalogDataEntry transfer, EncryptedBundleBinding binding)
        {
            string id = FrozenAddressablesRoutes.MountId(binding);
            var mount = new ContentCatalogDataEntry(typeof(EncryptedMountedBundle), id, transfer.Provider, new[] { id });
            bool hasOwner = false;
            foreach (var asset in _keys.Keys)
            {
                var dependencies = _dependencies[asset];
                int index = Array.IndexOf(dependencies, transfer);
                if (index < 0)
                    continue;
                int catalogIndex = asset.Dependencies.IndexOf(transfer.Keys[0]);
                if (catalogIndex != index || Array.LastIndexOf(dependencies, transfer) != index)
                    throw new InvalidDataException("Cannot split the official protected Bundle dependency: " + binding.GroupPath);
                asset.Dependencies.Insert(index + 1, id);
                _dependencies[asset] = dependencies.Take(index + 1).Append(mount).Concat(dependencies.Skip(index + 1)).ToArray();
                hasOwner = true;
            }
            if (!hasOwner)
                throw new InvalidDataException("A protected Bundle has no supported asset route: " + binding.GroupPath);
            _context.locations.Add(mount);
        }

        private string FreezeRoutes()
        {
            var rows = new List<string>();
            var counts = new Dictionary<object, int>();
            var bundleKeys = new HashSet<string>(_filenames, StringComparer.OrdinalIgnoreCase);
            var uniqueAssets = new HashSet<string>(StringComparer.Ordinal);
            // Both pinned serializers keep this stable candidate order within each alias.
            foreach (var asset in _keys.Keys.OrderBy(entry => entry.InternalId))
            {
                string identity = Node(asset);
                if (!uniqueAssets.Add(identity))
                    throw new InvalidDataException("Ambiguous official asset location identity: " + asset.InternalId);
                string[] depends = _dependencies[asset].Select(Node).ToArray();
                foreach (object key in _keys[asset])
                {
                    if (key is string alias && bundleKeys.Contains(alias))
                        throw new InvalidDataException("An asset key collides with an official Bundle key: " + alias);
                    counts.TryGetValue(key, out int index);
                    rows.Add(FrozenAddressablesRoutes.Row(key, index, identity, depends));
                    counts[key] = index + 1;
                }
            }
            return FrozenAddressablesRoutes.Digest(rows);
        }

        private ContentCatalogData ReadCatalog(string path)
        {
            using var input = OwnedWindowsFiles.OpenRead(path);
#if UNITY_6000_3_OR_NEWER
            // The pinned binary reader is internal. Use it rather than reimplementing the serializer or registering a project Locator.
            var type = typeof(ContentCatalogData);
            var read = type.GetMethod("LoadFromFile", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(bool) }, null);
            if (CatalogName != "catalog.bin" || read == null)
                throw new InvalidDataException("Encrypted delivery requires the pinned Addressables binary Catalog reader.");
            return (ContentCatalogData)read.Invoke(null, new object[] { path, false });
#else
            if (CatalogName != "catalog.json")
                throw new InvalidDataException("Encrypted delivery requires the pinned Addressables JSON Catalog reader.");
            return JsonUtility.FromJson<ContentCatalogData>(EncryptedDeliverySession.ReadText(input));
#endif
        }

        internal Dictionary<string, IResourceLocation> VerifySerializedCatalog(string path, string[] scenes)
        {
            var locator = CreateLocator(ReadCatalog(path));
            var actual = FrozenAddressablesRoutes.CheckCatalog(locator, _bundles.Values.Where(bundle => bundle.Binding != null)
                .Select(bundle => bundle.Binding), RoutesSha256, scenes.Select(FrozenAddressablesRoutes.BuiltInSceneId).ToArray(), Addressables.RuntimePath);
            if (actual.Count != _bundles.Count + _bundles.Values.Count(bundle => bundle.Binding != null))
                throw new InvalidDataException("The serialized Catalog and official Bundle inventory do not agree.");
            foreach (var bundle in _bundles.Values)
            {
                if (!actual.TryGetValue(bundle.DeliverySourceId, out var location) || location.PrimaryKey != bundle.CatalogKey ||
                    location.ProviderId != bundle.ProviderId || !(location.Data is AssetBundleRequestOptions options) ||
                    options.BundleName != bundle.InternalName)
                    throw new InvalidDataException("A serialized Bundle route differs from its official result: " + bundle.DeliverySourceId);
            }
            return actual;
        }

        private static IResourceLocator CreateLocator(ContentCatalogData catalog)
        {
#if UNITY_6000_3_OR_NEWER
            var create = typeof(ContentCatalogData).GetMethod("CreateCustomLocator", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string) }, null);
            if (create == null)
                throw new InvalidDataException("Encrypted delivery requires the pinned Addressables binary Catalog reader.");
            return (IResourceLocator)create.Invoke(catalog, new object[] { "", null });
#else
            return catalog.CreateLocator();
#endif
        }

        internal static string NormalizeLoadPrefix(string value) => (value ?? "").Replace('\\', '/').TrimEnd('/') + "/";

        internal static void CheckBundleLeaf(string filename)
        {
            if (!filename.EndsWith(".bundle", StringComparison.Ordinal) || filename != Path.GetFileName(filename))
                throw new InvalidDataException("Nested or invalid official Bundle filename: " + filename);
            EncryptedBundleBinding.Token(filename, "official Bundle filename");
            ProtectedAssetAudit.CheckWindowsLeaf(filename);
        }

        private static string Node(ContentCatalogDataEntry entry) => FrozenAddressablesRoutes.Node(entry.InternalId, entry.Provider,
            entry.ResourceType, entry.Data, Addressables.RuntimePath);

        internal sealed class PackedBundle
        {
            internal readonly string GroupGuid;
            internal readonly bool IsShared;
            internal readonly string OriginalKey;
            internal readonly string FileName;
            internal readonly string CatalogKey;
            internal readonly string Hash;
            internal readonly string SourceId;
            internal readonly string ProviderId;
            internal readonly EncryptedBundleBinding Binding;
            internal readonly string[] Immediate;
            internal readonly string[] Expanded;
            internal string ReportSourceId;
            internal EncryptedBundleRecord Retained;
            internal IResourceLocation PreviousOrdinary;
            internal string SbpSha256;
            internal string RetainedOrdinaryRoute;

            internal string InternalName => Path.GetFileNameWithoutExtension(OriginalKey);
            internal string DeliverySourceId => RetainedOrdinaryRoute ?? SourceId;

            internal PackedBundle(string groupGuid, bool isShared, string originalKey, string fileName, string catalogKey, string hash,
                string sourceId, string providerId, EncryptedBundleBinding binding, string[] immediate, string[] expanded)
            {
                GroupGuid = groupGuid;
                IsShared = isShared;
                OriginalKey = originalKey;
                FileName = fileName;
                CatalogKey = catalogKey;
                Hash = hash;
                SourceId = sourceId;
                ReportSourceId = sourceId;
                ProviderId = providerId;
                Binding = binding;
                Immediate = immediate;
                Expanded = expanded;
            }
        }

        internal sealed class AssetRoute
        {
            internal readonly string InternalId;
            internal readonly object[] Keys;
            internal readonly string[] Dependencies;

            internal AssetRoute(string internalId, object[] keys, string[] dependencies)
            {
                InternalId = internalId;
                Keys = keys;
                Dependencies = dependencies;
            }
        }
    }
}
