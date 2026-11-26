using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Build.Layout;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Reporting;
using UnityEngine.AddressableAssets;

namespace YLFramework.Resource.Encryption.Editor
{
    internal sealed class ProtectedAssetAudit
    {
        private readonly Dictionary<string, string> _protected = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _placement = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ExpectedAsset> _explicit = new Dictionary<string, ExpectedAsset>(StringComparer.Ordinal);
        private readonly (string Path, string Placement)[] _builtInDependencies;
        private readonly Dictionary<string, string[]> _publicByGroup = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private const string BuiltInExtra = "0000000000000000f000000000000000";
        private const string DefaultResources = "0000000000000000e000000000000000";

        internal ProtectedAssetAudit(IReadOnlyList<AddressableAssetGroup> groups, string[] scenes)
        {
            var explicitGuids = new HashSet<string>(groups.SelectMany(group => group.entries).Select(entry => entry.guid), StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var auth = group.GetSchema<EncryptedBundleGroupSchema>();
                var packed = group.GetSchema<BundledAssetGroupSchema>();
                var publicGuids = (auth.PublicDependencyGuids ?? Array.Empty<string>())
                    .Select(guid => EncryptedBundleBinding.Hex(guid, 32, "public asset GUID")).ToArray();
                var exclusions = new HashSet<string>(publicGuids, StringComparer.Ordinal);
                if (exclusions.Count != publicGuids.Length || exclusions.Overlaps(explicitGuids) || group.entries.Count == 0)
                    throw new InvalidDataException("Select explicit non-scene assets; protected entries cannot be public exceptions in any group.");
                _publicByGroup.Add(group.Guid, publicGuids);
                var remaining = new HashSet<string>(exclusions, StringComparer.Ordinal);
                foreach (var entry in group.entries)
                {
                    string path = entry.AssetPath;
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                        AssetDatabase.IsValidFolder(path) || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                        AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(MonoScript) || AssetDatabase.AssetPathToGUID(path) != entry.guid)
                        throw new InvalidDataException("Select explicit non-scene project assets with stable GUIDs: " + path);
                    var closure = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string dependency in AssetDatabase.GetDependencies(path, true).Append(path))
                    {
                        // Scripts are executable code, not serialized asset payloads.
                        if (!IsProjectAsset(dependency) || AssetDatabase.GetMainAssetTypeAtPath(dependency) == typeof(MonoScript))
                            continue;
                        string guid = EncryptedBundleBinding.Hex(AssetDatabase.AssetPathToGUID(dependency), 32, "protected asset GUID");
                        closure.Add(guid);
                        if (exclusions.Contains(guid))
                            remaining.Remove(guid);
                        else if (_protected.TryAdd(guid, dependency))
                            _placement.Add(guid, new HashSet<string>(StringComparer.Ordinal));
                    }
                    string[] labels = entry.labels.OrderBy(label => label, StringComparer.Ordinal).ToArray();
                    var keys = new List<string>();
                    if (packed.IncludeAddressInCatalog)
                        keys.Add(entry.address);
                    if (packed.IncludeGUIDInCatalog)
                        keys.Add(entry.guid);
                    if (packed.IncludeLabelsInCatalog)
                        keys.AddRange(labels);
                    if (keys.Count == 0 || labels.Except(group.Settings.GetLabels(), StringComparer.Ordinal).Any() ||
                        !_explicit.TryAdd(entry.guid, new ExpectedAsset(group.Guid, path, entry.address, labels, keys.ToArray(), closure, packed.IncludeAddressInCatalog)))
                        throw new InvalidDataException("Ambiguous explicit protected entry or Catalog keys: " + path);
                    foreach (string key in keys)
                        FrozenAddressablesRoutes.CheckAlias(key, -1);
                }
                if (remaining.Count != 0)
                    throw new InvalidDataException("A configured public exception is not a dependency of its protected group: " + group.Name);
            }

            var resources = AssetDatabase.GetAllAssetPaths().Where(path => IsProjectAsset(path) &&
                path.Split('/').Contains("Resources", StringComparer.OrdinalIgnoreCase) && !AssetDatabase.IsValidFolder(path));
            _builtInDependencies = scenes.SelectMany(scene => AssetDatabase.GetDependencies(scene, true)
                .Select(path => (path, "built-in scene " + scene)))
                .Concat(resources.SelectMany(resource => AssetDatabase.GetDependencies(resource, true)
                    .Select(path => (path, "Resources " + resource)))).ToArray();
        }

        internal void AuditLayout(BuildLayout layout, EncryptedPackedBuilder builder)
        {
            if (layout == null || layout.BuildTarget != BuildTarget.StandaloneWindows64 || !string.IsNullOrEmpty(layout.BuildError))
                throw new InvalidDataException("The current official Addressables Build Layout is missing or invalid.");
            var inventory = layout.Groups.SelectMany(group => group.Bundles.Select(bundle => (bundle, owner: group.Guid, shared: false)))
                .Concat(layout.BuiltInBundles.Select(bundle => (bundle, owner: bundle.Group?.Guid, shared: true))).ToArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byKey = builder.Bundles.Values.ToDictionary(row => row.OriginalKey, StringComparer.Ordinal);
            var byFile = builder.Bundles.Values.ToDictionary(row => row.FileName, StringComparer.Ordinal);
            var places = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var explicitSites = new Dictionary<string, (BuildLayout.ExplicitAsset Asset, EncryptedPackedBuilder.PackedBundle Bundle)>(StringComparer.Ordinal);
            foreach (var (bundle, owner, shared) in inventory)
            {
                if (!seen.Add(bundle.Name) || !byFile.TryGetValue(bundle.Name, out var packed) || packed.IsShared != shared ||
                    packed.GroupGuid != owner || bundle.Group?.Guid != owner || bundle.InternalName != packed.InternalName ||
                    bundle.Files == null || bundle.Files.Count == 0 ||
                    (packed.Binding != null && bundle.Compression != "Lz4HC" && bundle.Compression != "Lz4"))
                    throw new InvalidDataException("The official report contains an ambiguous Bundle placement: " + bundle.Name);
                // Layout looks up catalog entries by BundleName and reports the post-rename route.
                if (bundle.Provider != packed.ProviderId || FrozenAddressablesRoutes.CanonicalId(bundle.LoadPath ?? "", Addressables.RuntimePath) != packed.ReportSourceId)
                    throw new InvalidDataException("Build Layout Bundle route differs from official packing: " + bundle.Name);
                CheckLayoutEdges(bundle.Name, bundle.Dependencies, packed.Immediate, byKey);
                CheckLayoutEdges(bundle.Name, bundle.ExpandedDependencies, packed.Expanded, byKey);
                foreach (var file in bundle.Files)
                {
                    foreach (var item in file.Assets)
                    {
                        Record(item.Guid, item.AssetPath, "bundle " + bundle.Name + "/" + file.Name, packed.Binding != null);
                        AddSite(places, item.Guid, packed.OriginalKey);
                        if (_explicit.ContainsKey(item.Guid) && !explicitSites.TryAdd(item.Guid, (item, packed)))
                            throw new InvalidDataException("An explicitly protected GUID has more than one Layout owner: " + item.Guid);
                    }
                    foreach (var item in file.OtherAssets)
                    {
                        Record(item.AssetGuid, item.AssetPath, "bundle " + bundle.Name + "/" + file.Name, packed.Binding != null);
                        AddSite(places, item.AssetGuid, packed.OriginalKey);
                    }
                }
            }
            if (byFile.Count != seen.Count)
                throw new InvalidDataException("Official Bundle outputs and actual Layout inventory do not match.");
            Check();

            var aliasOwners = new Dictionary<object, HashSet<string>>();
            foreach (var pair in builder.Assets)
            {
                foreach (object key in pair.Value.SelectMany(route => route.Keys))
                {
                    if (!aliasOwners.TryGetValue(key, out var owners))
                        aliasOwners.Add(key, owners = new HashSet<string>(StringComparer.Ordinal));
                    owners.Add(pair.Key);
                }
            }
            foreach (var pair in _explicit)
            {
                string guid = pair.Key;
                var expected = pair.Value;
                if (!explicitSites.TryGetValue(guid, out var site) || site.Bundle.Binding == null ||
                    site.Bundle.GroupGuid != expected.GroupGuid || site.Asset.GroupGuid != expected.GroupGuid ||
                    site.Asset.AssetPath != expected.Path || site.Asset.AddressableName != expected.Address ||
                    !(site.Asset.Labels ?? Array.Empty<string>()).OrderBy(label => label, StringComparer.Ordinal).SequenceEqual(expected.Labels) ||
                    !builder.Assets.TryGetValue(guid, out var routes) || routes.Length == 0)
                    throw new InvalidDataException("Explicit protected asset GUID/group differs from authoring and official Catalog: " + expected.Path);
                if (expected.HasAddress && (!aliasOwners.TryGetValue(expected.Address, out var owners) || owners.Count != 1))
                    throw new InvalidDataException("An explicit protected address is ambiguous: " + expected.Address);

                var closure = BundleClosure(site.Bundle, byKey);
                if (!closure.SetEquals(site.Bundle.Immediate.Concat(site.Bundle.Expanded).Append(site.Bundle.OriginalKey)))
                    throw new InvalidDataException("The official direct and expanded Bundle graphs disagree: " + expected.Path);
                foreach (var route in routes)
                {
                    if (route.InternalId != site.Asset.InternalId || !new HashSet<object>(route.Keys).SetEquals(expected.Keys) ||
                        !closure.SetEquals(route.Dependencies))
                        throw new InvalidDataException("Official Catalog keys, asset identity or Bundle closure differ from authoring and Layout: " + expected.Path);
                }
                if (expected.Closure.Any(dependency => !places.TryGetValue(dependency, out var sites) || !sites.Overlaps(closure)))
                    throw new InvalidDataException("Cannot prove all authored dependencies in the shipped Bundle closure: " + expected.Path);
            }

            // Follow the verified direct graph, not the set of all remote groups in the project.
            foreach (var root in byKey.Values.Where(row => row.Binding?.Mode == EncryptedBundleDeliveryMode.Embedded))
            {
                foreach (string key in BundleClosure(root, byKey))
                {
                    var dependency = byKey[key];
                    if (dependency.Binding != null ? dependency.Binding.Mode != EncryptedBundleDeliveryMode.Embedded :
                        !FrozenAddressablesRoutes.IsStockLocalBundle(dependency.SourceId, dependency.ProviderId))
                        throw new InvalidDataException("Embedded Bundle closure contains a remote dependency: " + root.FileName + " -> " + dependency.SourceId);
                }
            }
        }

        private static void CheckLayoutEdges(string bundle, List<BuildLayout.Bundle> layout, string[] sbp,
            Dictionary<string, EncryptedPackedBuilder.PackedBundle> byKey)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if ((layout != null && layout.Any(dependency => dependency == null || !seen.Add(dependency.Name))) ||
                !seen.SetEquals(sbp.Select(key => byKey[key].FileName)))
                throw new InvalidDataException("Build Layout and SBP Bundle dependencies disagree: " + bundle);
        }

        private static HashSet<string> BundleClosure(EncryptedPackedBuilder.PackedBundle root,
            Dictionary<string, EncryptedPackedBuilder.PackedBundle> byKey)
        {
            var pending = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(root.OriginalKey);
            while (pending.Count != 0)
            {
                string key = pending.Pop();
                if (!seen.Add(key))
                    continue;
                if (!byKey.TryGetValue(key, out var dependency))
                    throw new InvalidDataException("A Bundle dependency has no attributed output: " + key);
                foreach (string next in dependency.Immediate)
                    pending.Push(next);
            }
            return seen;
        }

        private static void AddSite(Dictionary<string, HashSet<string>> places, string guid, string key)
        {
            if (!HasGuid(guid))
                return;
            if (!places.TryGetValue(guid, out var sites))
                places.Add(guid, sites = new HashSet<string>(StringComparer.Ordinal));
            sites.Add(key);
        }

        internal void AuditPlayer(BuildReport build, string streamingRoot, IReadOnlyDictionary<string, string> allowed)
        {
            if (build.packedAssets == null || build.packedAssets.Length == 0)
                throw new InvalidDataException("A detailed Player packed-assets report is required.");
            int identified = 0;
            foreach (var packed in build.packedAssets)
            {
                foreach (var asset in packed.contents)
                {
                    string guid = asset.sourceAssetGUID.ToString();
                    string path = asset.sourceAssetPath;
                    if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal))
                        identified++;
                    if (IsGeneratedPlayerMetadata(guid, path, packed.shortPath, asset.type))
                        continue;
                    Record(guid, path, "Player packed " + packed.shortPath, false);
                }
            }
            if (identified == 0)
                throw new InvalidDataException("The detailed Player report does not identify project assets.");
            AuditInstalledDependencies();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in EnumerateOwnedFiles(streamingRoot))
            {
                string relative = Relative(streamingRoot, path);
                if (!seen.Add(relative) || !allowed.TryGetValue(relative, out string expected) || DigestFile(path) != expected)
                    throw new InvalidDataException("Cannot account for a file in actual StreamingAssets: " + relative);
            }
            if (allowed.Count != seen.Count)
                throw new InvalidDataException("Actual StreamingAssets missed expected official outputs.");
        }

        internal void AuditInstalledDependencies()
        {
            foreach (var (path, placement) in _builtInDependencies)
                Record(AssetDatabase.AssetPathToGUID(path), path, placement, false);
            Check();
        }

        private static bool IsProjectAsset(string path) => !string.IsNullOrEmpty(path) &&
            (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));

        private static bool HasGuid(string guid) => !string.IsNullOrEmpty(guid) && guid.Any(value => value != '0');

        private static bool IsGeneratedPlayerMetadata(string guid, string path, string packedPath, Type assetType)
        {
            if (HasGuid(guid))
                return false;
            // These engine-created entries have no project GUID. Do not whitelist generated paths or arbitrary Unity asset types.
            if (string.IsNullOrEmpty(path) && assetType?.FullName == "UnityEngine.PreloadData")
                return true;
            return (packedPath == "globalgamemanagers.assets" || packedPath == "globalgamemanagers.assets.resS") &&
                ((path == "Built-in Texture2D: Splash Screen Unity Logo" && assetType == typeof(UnityEngine.Texture2D)) ||
                 (path == "Built-in Sprite: " && assetType == typeof(UnityEngine.Sprite)));
        }

        private void Record(string guid, string path, string placement, bool isProtected)
        {
            if (string.IsNullOrEmpty(path) && HasGuid(guid))
                path = AssetDatabase.GUIDToAssetPath(guid);
            if (IsProjectAsset(path))
            {
                string resolved = AssetDatabase.AssetPathToGUID(path);
                if (!HasGuid(resolved) || (HasGuid(guid) && !string.Equals(resolved, guid, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Conflicting or unresolved asset identity in " + placement + ": " + path + " [" + guid + "].");
                if (_placement.TryGetValue(resolved, out var entries))
                    entries.Add((isProtected ? "protected " : "plaintext ") + placement);
                return;
            }
            if (guid == BuiltInExtra && path == "Resources/unity_builtin_extra" || guid == DefaultResources &&
                (path == "Resources/unity default resources" || path == "Library/unity default resources"))
                return;
            throw new InvalidDataException("Build report contains an unattributed asset in " + placement + ": " + path + " [" + guid + "].");
        }

        private void Check()
        {
            foreach (var asset in _protected.OrderBy(pair => pair.Value, StringComparer.Ordinal))
            {
                var sites = _placement[asset.Key].OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!sites.Any(value => value.StartsWith("protected ", StringComparison.Ordinal)) || sites.Any(value => value.StartsWith("plaintext ", StringComparison.Ordinal)))
                    throw new EncryptedBundleAuditException(asset.Key, asset.Value, sites);
            }
        }

        internal JArray Report() => new JArray(_protected.OrderBy(asset => asset.Key, StringComparer.Ordinal)
            .Select(asset => new JObject { ["guid"] = asset.Key, ["path"] = asset.Value, ["locations"] = new JArray(_placement[asset.Key].OrderBy(value => value, StringComparer.Ordinal)) }));

        internal JArray Authors() => new JArray(_publicByGroup.OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new JObject
        {
            ["groupGuid"] = group.Key,
            ["publicDependencyGuids"] = new JArray(group.Value.OrderBy(guid => guid, StringComparer.Ordinal)),
            ["assets"] = new JArray(_explicit.Where(asset => asset.Value.GroupGuid == group.Key).OrderBy(asset => asset.Key, StringComparer.Ordinal)
                .Select(asset => new JObject
                {
                    ["guid"] = asset.Key,
                    ["path"] = asset.Value.Path,
                    ["keys"] = new JArray(asset.Value.Keys),
                    ["dependencies"] = new JArray(asset.Value.Closure.OrderBy(guid => guid, StringComparer.Ordinal))
                }))
        }));

        private sealed class ExpectedAsset
        {
            internal readonly string GroupGuid;
            internal readonly string Path;
            internal readonly string Address;
            internal readonly string[] Labels;
            internal readonly string[] Keys;
            internal readonly HashSet<string> Closure;
            internal readonly bool HasAddress;

            internal ExpectedAsset(string groupGuid, string path, string address, string[] labels, string[] keys,
                HashSet<string> closure, bool hasAddress)
            {
                GroupGuid = groupGuid;
                Path = path;
                Address = address;
                Labels = labels;
                Keys = keys;
                Closure = closure;
                HasAddress = hasAddress;
            }
        }

        internal static string DigestFile(string file)
        {
            using var source = OwnedWindowsFiles.OpenRead(file);
            return AuthenticatedBundleStream.Hash(source, default);
        }

        internal static void RequireWin64NativePlugin(Stream input)
        {
            const int DosHeaderBytes = 64;
            const int PeHeaderBytes = 24; // PE signature and COFF header.
            const ushort Amd64 = 0x8664;
            const ushort Dll = 0x2000;
            using var header = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            if (input.Length >= DosHeaderBytes && header.ReadUInt16() == 0x5a4d)
            {
                input.Position = 0x3c;
                int pe = header.ReadInt32();
                if (pe >= DosHeaderBytes && pe <= input.Length - PeHeaderBytes - sizeof(ushort))
                {
                    input.Position = pe;
                    if (header.ReadUInt32() == 0x00004550 && header.ReadUInt16() == Amd64)
                    {
                        input.Position = pe + 20L;
                        ushort optionalBytes = header.ReadUInt16();
                        ushort characteristics = header.ReadUInt16();
                        if ((characteristics & Dll) != 0 && optionalBytes >= sizeof(ushort) &&
                            optionalBytes <= input.Length - input.Position && header.ReadUInt16() == 0x20b)
                            return;
                    }
                }
            }
            throw new InvalidDataException("The installed native encryption plugin is not a Windows x64 DLL.");
        }

        internal static string Relative(string parent, string child)
        {
            string prefix = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(child);
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Output does not belong to its declared root: " + file);
            return file.Substring(prefix.Length).Replace('\\', '/');
        }

        internal static void CheckWindowsLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf) || leaf.Length > 255 || leaf.EndsWith(".", StringComparison.Ordinal) ||
                leaf.EndsWith(" ", StringComparison.Ordinal) || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(leaf, @"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9\u00b9\u00b2\u00b3]|LPT[0-9\u00b9\u00b2\u00b3])(?:[.]|\z)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Unsupported Windows output name: " + leaf);
        }

        internal static IEnumerable<string> EnumerateOwnedFiles(string root)
        {
            using var pin = OwnedWindowsFiles.PinDirectory(root, false);
            return EnumerateTree(root).ToArray();
        }

        // The final candidate root is already held by its rename handle, which must not be reopened without delete sharing.
        internal static IEnumerable<string> EnumerateTree(string root, Func<string, bool> include = null)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Unowned link in build output: " + path);
                CheckWindowsLeaf(Path.GetFileName(path));
                if (include != null && !include(path))
                    continue;
                if ((attributes & FileAttributes.Directory) == 0)
                    yield return path;
                else
                    foreach (string file in EnumerateTree(path, include))
                        yield return file;
            }
        }
    }

    /// <summary>One project asset has unproven or plaintext placement. The release is not eligible for publication.</summary>
    public sealed class EncryptedBundleAuditException : IOException
    {
        internal EncryptedBundleAuditException(string guid, string path, string[] sites)
            : base("Cannot prove exclusive encrypted placement for " + guid + " " + path + ": " + string.Join("; ", sites)) { }
    }
}
