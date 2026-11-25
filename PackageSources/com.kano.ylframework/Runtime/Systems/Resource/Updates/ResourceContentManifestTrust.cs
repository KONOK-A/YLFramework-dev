using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace YLFramework.Resource
{
    /// <summary>应用内预置的发布目标、公钥和可信传输根。</summary>
    public sealed class ResourceContentManifestTrust
    {
        internal const int MaximumIdLength = 128;
        internal const string RuntimePath = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/";
        internal const string InstallPrefix = "install:";
        private const int MaximumPathLength = 2048;
        private static readonly Regex _id = new Regex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant);
        private static readonly Regex _sha256 = new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex _reservedName = new Regex(@"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Dictionary<string, RSAParameters> _keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        private readonly List<string> _sourceRoots = new List<string>();

        public ResourceContentManifestTrust(string application, string channel, string platform, string compatibility,
            string settingsSha256, IReadOnlyDictionary<string, RSAParameters> publicKeys,
            IEnumerable<Uri> trustedSourceRoots, bool allowLoopbackHttp = false)
        {
            Application = CheckId(application, nameof(application));
            Channel = CheckId(channel, nameof(channel));
            Platform = CheckId(platform, nameof(platform));
            Compatibility = CheckId(compatibility, nameof(compatibility));
            SettingsSha256 = IsSha256(settingsSha256) ? settingsSha256 : throw new ArgumentException(
                "Expected a lowercase SHA-256 digest.", nameof(settingsSha256));
            AllowsLoopbackHttp = allowLoopbackHttp;

            if (publicKeys == null || publicKeys.Count == 0)
            {
                throw new ArgumentException("At least one pretrusted release key is required.", nameof(publicKeys));
            }

            foreach (var key in publicKeys)
            {
                _keys.Add(CheckId(key.Key, nameof(publicKeys)), ResourceContentManifestSignature.CopyPublicKey(key.Value));
            }

            if (trustedSourceRoots != null)
            {
                foreach (Uri root in trustedSourceRoots)
                {
                    if (!IsNetworkUri(root, allowLoopbackHttp, directory: true))
                    {
                        throw new ArgumentException("Expected a canonical trusted source directory.", nameof(trustedSourceRoots));
                    }
                    _sourceRoots.Add(root.AbsoluteUri);
                }
            }
        }

        public string Application
        {
            get;
        }
        public string Channel
        {
            get;
        }
        public string Platform
        {
            get;
        }
        public string Compatibility
        {
            get;
        }
        public string SettingsSha256
        {
            get;
        }
        internal bool AllowsLoopbackHttp
        {
            get;
        }

        internal static bool IsId(string value) => value != null && _id.IsMatch(value);
        internal static bool IsSha256(string value) => value != null && _sha256.IsMatch(value);

        internal bool TryGetPublicKey(string keyId, out RSAParameters key) => _keys.TryGetValue(keyId, out key);

        internal void RequireTrustedSource(Uri source)
        {
            if (IsNetworkUri(source, AllowsLoopbackHttp, directory: false))
            {
                foreach (string root in _sourceRoots)
                {
                    if (source.AbsoluteUri.StartsWith(root, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            throw new InvalidDataException("Resource content source is outside the configured trusted directories.");
        }

        internal string GetSourcePath(string source)
        {
            if (source.StartsWith(InstallPrefix, StringComparison.Ordinal))
            {
                return RequireInstallationPath(source.Substring(InstallPrefix.Length));
            }

            Uri.TryCreate(source, UriKind.Absolute, out Uri remote);
            RequireTrustedSource(remote);
            return remote.AbsolutePath;
        }

        internal static string RequireInstallationPath(string relative)
        {
            if (!IsRelativePath(relative))
            {
                throw new InvalidDataException("Invalid installation resource content source path: " + relative);
            }
            return relative;
        }

        internal static string GetRoutePath(string route, bool allowLoopbackHttp)
        {
            if (route != null && route.StartsWith(RuntimePath, StringComparison.Ordinal))
            {
                string relative = route.Substring(RuntimePath.Length);
                if (IsRelativePath(relative))
                {
                    return relative;
                }
            }
            else if (IsRelativePath(route))
            {
                return route;
            }
            else if (Uri.TryCreate(route, UriKind.Absolute, out Uri remote) &&
                IsNetworkUri(remote, allowLoopbackHttp, directory: false))
            {
                return remote.AbsolutePath;
            }

            throw new InvalidDataException("Invalid resource content logical route: " + route);
        }

        private static string CheckId(string value, string parameter)
        {
            return IsId(value) ? value : throw new ArgumentException("Invalid resource content identifier.", parameter);
        }

        internal static bool IsNetworkUri(Uri uri, bool allowLoopbackHttp, bool directory)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.OriginalString.Length > MaximumPathLength ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                    !(allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1")) ||
                uri.Host.Length == 0 || uri.Host.EndsWith(".", StringComparison.Ordinal) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.OriginalString != uri.AbsoluteUri || !uri.IsWellFormedOriginalString())
            {
                return false;
            }

            string path = uri.AbsolutePath;
            if (directory)
            {
                return path == "/" || path.EndsWith("/", StringComparison.Ordinal) &&
                    IsRelativePath(path.Substring(1, path.Length - 2));
            }
            return IsRelativePath(path.Substring(1));
        }

        private static bool IsRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaximumPathLength)
            {
                return false;
            }

            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment.Length > 255 || segment.EndsWith(".", StringComparison.Ordinal) ||
                    _reservedName.IsMatch(segment))
                {
                    return false;
                }

                foreach (char character in segment)
                {
                    if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.'))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
