using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource.Encryption
{
    /// <summary>Where the initial Player version obtains a protected group's ciphertext.</summary>
    public enum EncryptedBundleDeliveryMode
    {
        /// <summary>Fetch from the immutable HTTPS release into the dedicated cache.</summary>
        RemoteHttps = 0,
        /// <summary>Read directly from the selected content root, initially the installation.</summary>
        Embedded = 1
    }

    internal sealed class EncryptedBundleBinding
    {
        internal const int Version = 2;
        internal const string Format = "AES256_GCM_HKDF_1MB";
        internal const string Platform = "StandaloneWindows64";
        internal const string EmbeddedFolder = "yl-encrypted";
        internal const string EmbeddedPrefix = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/" + EmbeddedFolder + "/";
        private const long SegmentSize = 1024 * 1024;
        private const long HeaderSize = 40;
        private const long TagSize = 16;

        internal EncryptedBundleBinding(string application, string releaseId, string releasePrefix, EncryptedBundleDeliveryMode mode, string groupGuid, string bundleName,
            string bundleHash, string providerId, string relativePath, string keyId, long plainLength, string plainSha256,
            bool allowLoopback = false)
        {
            Application = Token(application, "application");
            ReleaseId = ValidateLeaf(releaseId);
            if (mode != EncryptedBundleDeliveryMode.RemoteHttps && mode != EncryptedBundleDeliveryMode.Embedded)
                throw new InvalidDataException("Unknown protected Bundle delivery mode.");
            if (mode == EncryptedBundleDeliveryMode.RemoteHttps || releasePrefix != null)
            {
                ReleasePrefix = ValidatePrefix(releasePrefix, allowLoopback);
                if (!ReleasePrefix.EndsWith("/" + ReleaseId + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("Protected release prefix does not end in the release identity.");
            }
            Mode = mode;
            GroupGuid = Hex(groupGuid, 32, "groupGuid");
            BundleName = Token(bundleName, "bundleName");
            BundleHash = Hex(bundleHash, 32, "bundleHash");
            ProviderId = Token(providerId, "providerId");
            RelativePath = ValidateLeaf(relativePath);
            KeyId = Token(keyId, "keyId");
            GetCipherLength(plainLength);
            PlainLength = plainLength;
            PlainSha256 = Hex(plainSha256, 64, "plainSha256");
        }

        internal string Application
        {
            get;
        }
        internal string ReleasePrefix
        {
            get;
        }
        internal string ReleaseId
        {
            get;
        }
        internal EncryptedBundleDeliveryMode Mode
        {
            get;
        }
        internal string GroupGuid
        {
            get;
        }
        internal string BundleName
        {
            get;
        }
        internal string BundleHash
        {
            get;
        }
        internal string ProviderId
        {
            get;
        }
        internal string RelativePath
        {
            get;
        }
        internal string KeyId
        {
            get;
        }
        internal long PlainLength
        {
            get;
        }
        internal string PlainSha256
        {
            get;
        }
        internal string GroupPath => GroupGuid + "/" + RelativePath;
        internal string SourceId => (Mode == EncryptedBundleDeliveryMode.Embedded ? EmbeddedPrefix : ReleasePrefix) + GroupPath;
        internal string Url => Mode == EncryptedBundleDeliveryMode.RemoteHttps ? SourceId : throw new InvalidOperationException("An embedded Bundle has no URL.");

        internal byte[] AssociatedData()
        {
            using var bytes = new MemoryStream(512);
            using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
            writer.Write(Version);
            foreach (string field in new[] { "YL.BundleDelivery.StreamingAEAD", Format, Application, Platform,
                ReleaseId, Mode.ToString(), SourceId, GroupGuid, BundleName, BundleHash, ProviderId, KeyId, RelativePath, PlainSha256 })
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(field);
                writer.Write(utf8.Length);
                writer.Write(utf8);
            }
            writer.Write(PlainLength);
            writer.Flush();
            return bytes.ToArray();
        }

        internal static long GetCipherLength(long plainLength)
        {
            long capacity = SegmentSize - TagSize;
            if (plainLength <= 0 || plainLength > (1L << 32) * capacity - HeaderSize)
            {
                throw new InvalidDataException("Bundle plaintext size is outside the streaming AEAD format.");
            }
            long decorated = plainLength + HeaderSize;
            return decorated + ((decorated - 1) / capacity + 1) * TagSize;
        }

        internal static string Hex(string value, int length, string field)
        {
            if (value == null || value.Length != length || value.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
            {
                throw new InvalidDataException("Invalid frozen hexadecimal identity: " + field);
            }
            return value;
        }

        internal static string Token(string value, string field)
        {
            if (value == null || value.Length > 256 || !Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9_.-]*\\z", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("Noncanonical frozen bundle field: " + field);
            }
            return value;
        }

        internal static string ValidatePrefix(string value, bool allowLoopback)
        {
            if (value == null || value.Length > 1024 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(allowLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1")) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri != value ||
                uri.AbsolutePath.Length <= 1 || !uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Expected a canonical HTTPS immutable release directory.");
            }
            foreach (var segment in uri.AbsolutePath.Substring(1, uri.AbsolutePath.Length - 2).Split('/'))
                ValidateLeaf(segment);
            return value;
        }

        internal static string ValidateLeaf(string name)
        {
            Token(name, "path");
            if (name.Length > 255 || name.EndsWith(".", StringComparison.Ordinal) || Regex.IsMatch(name,
                "\\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:[.]|\\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid delivery path component.");
            return name;
        }
    }

    internal sealed class EncryptedBundleRecord
    {
        internal EncryptedBundleRecord(EncryptedBundleBinding binding, long cipherLength, string cipherSha256)
        {
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            if (cipherLength != EncryptedBundleBinding.GetCipherLength(binding.PlainLength))
            {
                throw new InvalidDataException("Frozen ciphertext length disagrees with the format.");
            }
            CipherLength = cipherLength;
            CipherSha256 = EncryptedBundleBinding.Hex(cipherSha256, 64, "cipherSha256");
        }

        internal EncryptedBundleBinding Binding
        {
            get;
        }
        internal long CipherLength
        {
            get;
        }
        internal string CipherSha256
        {
            get;
        }
    }

    internal sealed class EncryptedBundleDescription
    {
        internal const string FileName = "yl-encrypted-bundles.json";
        internal const int MaximumBytes = 4 * 1024 * 1024;
        private const int MaximumBundles = 4096;

        internal EncryptedBundleDescription(string catalogName, string catalogSha256, string settingsSha256,
            string routesSha256, IEnumerable<EncryptedBundleRecord> bundles)
        {
            if (catalogName != "catalog.bin" && catalogName != "catalog.json")
            {
                throw new InvalidDataException("Only the default local Addressables catalog is eligible.");
            }
            CatalogName = catalogName;
            CatalogSha256 = EncryptedBundleBinding.Hex(catalogSha256, 64, "catalogSha256");
            SettingsSha256 = EncryptedBundleBinding.Hex(settingsSha256, 64, "settingsSha256");
            RoutesSha256 = EncryptedBundleBinding.Hex(routesSha256, 64, "routesSha256");
            var items = bundles.Take(MaximumBundles + 1).OrderBy(row => row?.Binding.GroupPath, StringComparer.Ordinal).ToArray();
            if (items.Length == 0 || items.Length > MaximumBundles || items.Any(row => row == null))
            {
                throw new InvalidDataException("Invalid protected bundle inventory.");
            }
            var first = items[0].Binding;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var digests = new HashSet<string>(StringComparer.Ordinal);
            var groups = new Dictionary<string, EncryptedBundleBinding>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var binding = item.Binding;
                if (binding.Application != first.Application || binding.ReleaseId != first.ReleaseId || binding.ReleasePrefix != first.ReleasePrefix ||
                    binding.ProviderId != first.ProviderId || !paths.Add(binding.GroupPath) || !names.Add(binding.BundleName) || !digests.Add(item.CipherSha256) ||
                    (groups.TryGetValue(binding.GroupGuid, out var other) && (other.Mode != binding.Mode || other.KeyId != binding.KeyId)))
                {
                    throw new InvalidDataException("Protected bundle identities conflict within the frozen delivery.");
                }
                groups[binding.GroupGuid] = binding;
            }
            if (!items.Any(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps) && first.ReleasePrefix != null)
                throw new InvalidDataException("An embedded-only delivery cannot declare a remote protected prefix.");
            Bundles = Array.AsReadOnly(items);
        }

        internal string CatalogName
        {
            get;
        }
        internal string CatalogSha256
        {
            get;
        }
        internal string SettingsSha256
        {
            get;
        }
        internal string RoutesSha256
        {
            get;
        }
        internal IReadOnlyList<EncryptedBundleRecord> Bundles
        {
            get;
        }
        internal string Application => Bundles[0].Binding.Application;
        internal string ReleaseId => Bundles[0].Binding.ReleaseId;
        internal string ReleasePrefix => Bundles[0].Binding.ReleasePrefix;
        internal string ProviderId => Bundles[0].Binding.ProviderId;

        internal string Encode()
        {
            return new JObject
            {
                ["version"] = EncryptedBundleBinding.Version,
                ["format"] = EncryptedBundleBinding.Format,
                ["platform"] = EncryptedBundleBinding.Platform,
                ["application"] = Application,
                ["releaseId"] = ReleaseId,
                ["releasePrefix"] = ReleasePrefix,
                ["providerId"] = ProviderId,
                ["catalog"] = CatalogName,
                ["catalogSha256"] = CatalogSha256,
                ["settingsSha256"] = SettingsSha256,
                ["routesSha256"] = RoutesSha256,
                ["bundles"] = new JArray(Bundles.Select(item => new JObject
                {
                    ["mode"] = item.Binding.Mode.ToString(),
                    ["groupGuid"] = item.Binding.GroupGuid,
                    ["bundleName"] = item.Binding.BundleName,
                    ["bundleHash"] = item.Binding.BundleHash,
                    ["path"] = item.Binding.RelativePath,
                    ["keyId"] = item.Binding.KeyId,
                    ["plainLength"] = item.Binding.PlainLength,
                    ["plainSha256"] = item.Binding.PlainSha256,
                    ["cipherLength"] = item.CipherLength,
                    ["cipherSha256"] = item.CipherSha256
                }))
            }.ToString(Formatting.None);
        }

        internal static EncryptedBundleDescription Parse(string json, bool allowLoopback = false)
        {
            if (json == null || json.Length > MaximumBytes || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            {
                throw new InvalidDataException("Frozen descriptor exceeds its size bound.");
            }
            try
            {
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text) { MaxDepth = 5, DateParseHandling = DateParseHandling.None, Culture = CultureInfo.InvariantCulture };
                var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                Fields(root, "version", "format", "platform", "application", "releaseId", "releasePrefix", "providerId",
                    "catalog", "catalogSha256", "settingsSha256", "routesSha256", "bundles");
                if (reader.Read() || Integer(root, "version") != EncryptedBundleBinding.Version ||
                    Text(root, "format") != EncryptedBundleBinding.Format || Text(root, "platform") != EncryptedBundleBinding.Platform ||
                    !(root["bundles"] is JArray rows) || rows.Count == 0 || rows.Count > MaximumBundles)
                {
                    throw new InvalidDataException("Unsupported encrypted bundle description.");
                }
                var records = new List<EncryptedBundleRecord>(rows.Count);
                string application = Text(root, "application"), releaseId = Text(root, "releaseId"), provider = Text(root, "providerId");
                string prefix = root["releasePrefix"].Type == JTokenType.Null ? null : Text(root, "releasePrefix");
                foreach (var item in rows)
                {
                    var row = item as JObject;
                    Fields(row, "mode", "groupGuid", "bundleName", "bundleHash", "path", "keyId", "plainLength", "plainSha256", "cipherLength", "cipherSha256");
                    var mode = Text(row, "mode") switch
                    {
                        "RemoteHttps" => EncryptedBundleDeliveryMode.RemoteHttps,
                        "Embedded" => EncryptedBundleDeliveryMode.Embedded,
                        _ => throw new InvalidDataException("Unknown protected Bundle delivery mode.")
                    };
                    var binding = new EncryptedBundleBinding(application, releaseId, prefix, mode, Text(row, "groupGuid"),
                        Text(row, "bundleName"), Text(row, "bundleHash"), provider, Text(row, "path"), Text(row, "keyId"),
                        Integer(row, "plainLength"), Text(row, "plainSha256"), allowLoopback);
                    records.Add(new EncryptedBundleRecord(binding, Integer(row, "cipherLength"), Text(row, "cipherSha256")));
                }
                return new EncryptedBundleDescription(Text(root, "catalog"), Text(root, "catalogSha256"),
                    Text(root, "settingsSha256"), Text(root, "routesSha256"), records);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Malformed frozen descriptor.", error);
            }
        }

        private static void Fields(JObject row, params string[] names)
        {
            if (row == null || row.Count != names.Length || names.Any(name => row[name] == null))
            {
                throw new InvalidDataException("Unexpected fields in frozen descriptor.");
            }
        }

        private static string Text(JObject row, string field) => row[field]?.Type == JTokenType.String
            ? (string)row[field] : throw new InvalidDataException("Invalid descriptor string: " + field);

        private static long Integer(JObject row, string field) => row[field] is JValue value && value.Value is long result
            ? result : throw new InvalidDataException("Invalid descriptor integer: " + field);
    }
}
