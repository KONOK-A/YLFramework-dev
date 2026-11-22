using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Build.Layout;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;

namespace YLFramework.Resource.Encryption.Editor
{
    internal sealed class ProtectedAssetAudit
    {
        private readonly Dictionary<string, string> _protected = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _placement = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private const string BuiltInExtra = "0000000000000000f000000000000000";
        private const string DefaultResources = "0000000000000000e000000000000000";

        internal ProtectedAssetAudit(AddressableAssetGroup group, string[] publicGuids)
        {
            var exclusions = new HashSet<string>((publicGuids ?? Array.Empty<string>()).Select(guid => EncryptedBundleBinding.Hex(guid, 32, "public asset GUID")), StringComparer.Ordinal);
            var entries = group.entries.ToArray();
            var explicitGuids = new HashSet<string>(entries.Select(entry => entry.guid), StringComparer.Ordinal);
            if (entries.Length == 0 || entries.Any(entry => string.IsNullOrEmpty(entry.AssetPath) || AssetDatabase.IsValidFolder(entry.AssetPath) ||
                !entry.AssetPath.StartsWith("Assets/", StringComparison.Ordinal) || entry.AssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                AssetDatabase.GetMainAssetTypeAtPath(entry.AssetPath) == typeof(MonoScript)) || exclusions.Overlaps(explicitGuids))
                throw new InvalidDataException("Select explicit non-scene project assets; an explicit protected asset cannot be a public exception.");

            foreach (string path in AssetDatabase.GetDependencies(entries.Select(entry => entry.AssetPath).ToArray(), true))
            {
                if (!IsProjectAsset(path)) continue;
                // Scripts are executable code, not serialized asset payloads.
                if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(MonoScript)) continue;
                string guid = AssetDatabase.AssetPathToGUID(path);
                EncryptedBundleBinding.Hex(guid, 32, "protected asset GUID");
                if (exclusions.Remove(guid)) continue;
                _protected.Add(guid, path);
                _placement.Add(guid, new HashSet<string>(StringComparer.Ordinal));
            }
            if (exclusions.Count != 0 || explicitGuids.Any(guid => !_protected.ContainsKey(guid)))
                throw new InvalidDataException("A configured public exception is not a dependency or a protected entry is unaccounted for.");
        }

        internal void AuditLayout(BuildLayout layout, HashSet<string> protectedBundles, HashSet<string> officialBundles)
        {
            if (layout == null || layout.BuildTarget != BuildTarget.StandaloneWindows64 || !string.IsNullOrEmpty(layout.BuildError))
                throw new InvalidDataException("The current official Addressables Build Layout is missing or invalid.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bundle in layout.Groups.SelectMany(group => group.Bundles).Concat(layout.BuiltInBundles))
            {
                if (!seen.Add(bundle.Name) || bundle.Files == null || bundle.Files.Count == 0)
                    throw new InvalidDataException("The official report contains an ambiguous Bundle placement.");
                foreach (var file in bundle.Files)
                {
                    foreach (var item in file.Assets) Record(item.Guid, item.AssetPath, "bundle " + bundle.Name + "/" + file.Name, protectedBundles.Contains(bundle.Name));
                    foreach (var item in file.OtherAssets) Record(item.AssetGuid, item.AssetPath, "bundle " + bundle.Name + "/" + file.Name, protectedBundles.Contains(bundle.Name));
                }
            }
            if (!officialBundles.SetEquals(seen)) throw new InvalidDataException("Official Bundle outputs and actual Layout inventory do not match.");
            Check();
        }

        internal void AuditPlayer(BuildReport build, string[] scenes, string streamingRoot, Dictionary<string, string> allowed)
        {
            if (build.packedAssets == null || build.packedAssets.Length == 0) throw new InvalidDataException("A detailed Player packed-assets report is required.");
            int identified = 0;
            foreach (var packed in build.packedAssets)
            {
                foreach (var asset in packed.contents)
                {
                    string guid = asset.sourceAssetGUID.ToString();
                    string path = asset.sourceAssetPath;
                    if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal)) identified++;
                    if (IsGeneratedPlayerMetadata(guid, path, packed.shortPath, asset.type)) continue;
                    Record(guid, path, "Player packed " + packed.shortPath, false);
                }
            }
            if (identified == 0) throw new InvalidDataException("The detailed Player report does not identify project assets.");
            var embedded = AssetDatabase.GetAllAssetPaths().Where(path => IsProjectAsset(path) &&
                path.Split('/').Contains("Resources", StringComparer.OrdinalIgnoreCase) && !AssetDatabase.IsValidFolder(path));
            foreach (string scene in scenes) AuditRoot(scene, "built-in scene " + scene);
            foreach (string path in embedded) AuditRoot(path, "Resources " + path);
            foreach (string path in EnumerateOwnedFiles(streamingRoot))
            {
                string relative = Relative(streamingRoot, path);
                if (!allowed.TryGetValue(relative, out string expected) || DigestFile(path) != expected)
                    throw new InvalidDataException("Cannot account for a file in actual StreamingAssets: " + relative);
                allowed.Remove(relative);
            }
            if (allowed.Count != 0) throw new InvalidDataException("Actual StreamingAssets missed expected official outputs.");
            Check();
        }

        private void AuditRoot(string path, string placement)
        {
            foreach (string dependency in AssetDatabase.GetDependencies(path, true))
                Record(AssetDatabase.AssetPathToGUID(dependency), dependency, placement, false);
        }

        private static bool IsProjectAsset(string path) => !string.IsNullOrEmpty(path) &&
            (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));

        private static bool HasGuid(string guid) => !string.IsNullOrEmpty(guid) && guid.Any(value => value != '0');

        private static bool IsGeneratedPlayerMetadata(string guid, string path, string packedPath, Type assetType)
        {
            if (HasGuid(guid)) return false;
            // These engine-created entries have no project GUID. Do not whitelist generated paths or arbitrary Unity asset types.
            if (string.IsNullOrEmpty(path) && assetType?.FullName == "UnityEngine.PreloadData") return true;
            return (packedPath == "globalgamemanagers.assets" || packedPath == "globalgamemanagers.assets.resS") &&
                ((path == "Built-in Texture2D: Splash Screen Unity Logo" && assetType == typeof(UnityEngine.Texture2D)) ||
                 (path == "Built-in Sprite: " && assetType == typeof(UnityEngine.Sprite)));
        }

        private void Record(string guid, string path, string placement, bool isProtected)
        {
            if (string.IsNullOrEmpty(path) && HasGuid(guid)) path = AssetDatabase.GUIDToAssetPath(guid);
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
                (path == "Resources/unity default resources" || path == "Library/unity default resources")) return;
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

        internal static string DigestFile(string file)
        {
            using var source = OwnedWindowsFiles.OpenRead(file);
            return AuthenticatedBundleStream.Hash(source, default);
        }

        internal static string Relative(string parent, string child)
        {
            string prefix = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(child);
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Output does not belong to its declared root: " + file);
            return file.Substring(prefix.Length).Replace('\\', '/');
        }

        internal static IEnumerable<string> EnumerateOwnedFiles(string root)
        {
            using var pin = OwnedWindowsFiles.PinDirectory(root, false);
            foreach (string path in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unowned link in build output: " + path);
                if ((attributes & FileAttributes.Directory) == 0) yield return path;
                else foreach (string file in EnumerateOwnedFiles(path)) yield return file;
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
