using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>用发布端持有的签名钥生成分离的清单载荷和签名封装。</summary>
    public static class ResourceContentManifestWriter
    {
        public static byte[] Serialize(string application, string channel, string platform, string compatibility,
            string releaseId, long sequence, DateTimeOffset expiresAtUtc, string settingsSha256, string catalogRoute,
            string descriptionRoute, IEnumerable<ResourceContentArtifact> artifacts, IEnumerable<string> startupRoutes)
        {
            if (artifacts == null)
            {
                throw new ArgumentNullException(nameof(artifacts));
            }
            if (startupRoutes == null)
            {
                throw new ArgumentNullException(nameof(startupRoutes));
            }

            ResourceContentManifestTrust.GetRoutePath(catalogRoute, allowLoopbackHttp: true);
            if (descriptionRoute != null)
            {
                ResourceContentManifestTrust.GetRoutePath(descriptionRoute, allowLoopbackHttp: true);
            }

            var rows = new JArray();
            foreach (var artifact in artifacts.OrderBy(value => value.Route, StringComparer.Ordinal))
            {
                ResourceContentManifestTrust.GetRoutePath(artifact.Route, allowLoopbackHttp: true);
                if (artifact.Source?.StartsWith(ResourceContentManifestTrust.InstallPrefix, StringComparison.Ordinal) == true)
                {
                    ResourceContentManifestTrust.RequireInstallationPath(artifact.Source.Substring(ResourceContentManifestTrust.InstallPrefix.Length));
                }
                else if (!Uri.TryCreate(artifact.Source, UriKind.Absolute, out var uri) ||
                    !ResourceContentManifestTrust.IsNetworkUri(uri, allowLoopbackHttp: true, directory: false))
                {
                    throw new InvalidDataException("Invalid resource content source URI: " + artifact.Source);
                }
                foreach (var dependency in artifact.Dependencies)
                {
                    ResourceContentManifestTrust.GetRoutePath(dependency, allowLoopbackHttp: true);
                }
                rows.Add(new JObject
                {
                    ["route"] = artifact.Route,
                    ["originReleaseId"] = artifact.OriginReleaseId,
                    ["source"] = artifact.Source,
                    ["kind"] = artifact.Kind.ToString(),
                    ["length"] = artifact.Length,
                    ["sha256"] = artifact.Sha256,
                    ["dependencies"] = new JArray(artifact.Dependencies.OrderBy(route => route, StringComparer.Ordinal))
                });
            }

            var roots = startupRoutes.OrderBy(route => route, StringComparer.Ordinal).ToArray();
            foreach (var route in roots)
            {
                ResourceContentManifestTrust.GetRoutePath(route, allowLoopbackHttp: true);
            }

            var manifest = new JObject
            {
                ["protocolVersion"] = ResourceContentManifest.ProtocolVersion,
                ["application"] = application,
                ["channel"] = channel,
                ["platform"] = platform,
                ["compatibility"] = compatibility,
                ["releaseId"] = releaseId,
                ["sequence"] = sequence,
                ["expiresAtUtc"] = expiresAtUtc.ToUniversalTime().ToString(ResourceContentManifest.UtcFormat,
                    CultureInfo.InvariantCulture),
                ["settingsSha256"] = settingsSha256,
                ["catalogRoute"] = catalogRoute,
                ["descriptionRoute"] = descriptionRoute,
                ["artifacts"] = rows,
                ["startupRoutes"] = new JArray(roots)
            };
            return new UTF8Encoding(false, true).GetBytes(manifest.ToString(Formatting.None));
        }

        public static byte[] Sign(byte[] payload, string keyId, RSA signingKey)
        {
            if (payload == null || payload.Length == 0 || payload.Length > ResourceContentManifest.MaximumPayloadBytes)
            {
                throw new ArgumentException("Resource content manifest payload is outside its size bound.", nameof(payload));
            }
            if (signingKey == null || signingKey.KeySize != 3072)
            {
                throw new ArgumentException("Expected an RSA-3072 private signing key.", nameof(signingKey));
            }

            byte[] signature = signingKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return ResourceContentManifestSignature.Envelope(keyId, signature);
        }
    }
}
