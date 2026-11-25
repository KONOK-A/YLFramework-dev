using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    public sealed partial class ResourceContentManifest
    {
        private const int MaximumArtifacts = 32768;
        private static readonly UTF8Encoding _utf8 = new UTF8Encoding(false, true);
        private static readonly string[] _manifestFields =
        {
            "protocolVersion", "application", "channel", "platform", "compatibility", "releaseId", "sequence",
            "expiresAtUtc", "settingsSha256", "catalogRoute", "descriptionRoute", "artifacts", "startupRoutes"
        };

        private static readonly string[] _artifactFields =
        {
            "route", "originReleaseId", "source", "kind", "length", "sha256", "dependencies"
        };

        private static ResourceContentManifest Parse(byte[] payload, string digest, ResourceContentManifestTrust trust)
        {
            JObject root = ReadPayload(payload);
            RequireFields(root, _manifestFields);
            if (ReadInteger(root, "protocolVersion") != ProtocolVersion)
            {
                throw new InvalidDataException("Unsupported resource content manifest protocol version.");
            }

            if (ReadString(root, "application") != trust.Application || ReadString(root, "channel") != trust.Channel ||
                ReadString(root, "platform") != trust.Platform || ReadString(root, "compatibility") != trust.Compatibility ||
                ReadString(root, "settingsSha256") != trust.SettingsSha256)
            {
                throw new InvalidDataException("Resource content manifest does not match this Player or channel.");
            }

            string releaseId = ReadString(root, "releaseId");
            if (!ResourceContentManifestTrust.IsId(releaseId) || !DateTimeOffset.TryParseExact(ReadString(root, "expiresAtUtc"),
                UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset expiresAtUtc))
            {
                throw new InvalidDataException("Invalid resource content release identity or UTC expiration.");
            }

            string catalogRoute = ReadString(root, "catalogRoute");
            string descriptionRoute = root["descriptionRoute"].Type == JTokenType.Null
                ? null : ReadString(root, "descriptionRoute");
            var artifacts = ReadArtifacts(root, trust, catalogRoute, descriptionRoute);
            ValidateDependencies(artifacts);
            var startupRoutes = FindStartupRoutes(artifacts, ReadRouteList(root, "startupRoutes"), catalogRoute, descriptionRoute);
            return new ResourceContentManifest(trust, releaseId, ReadInteger(root, "sequence"), expiresAtUtc,
                catalogRoute, descriptionRoute, digest, artifacts, startupRoutes);
        }

        private static JObject ReadPayload(byte[] payload)
        {
            try
            {
                string json = _utf8.GetString(payload);
                RequireJsonSyntax(json);
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text) { MaxDepth = 6, DateParseHandling = DateParseHandling.None };
                var result = JObject.Load(reader,
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read())
                {
                    throw new InvalidDataException("Unexpected data after the resource content manifest.");
                }
                return result;
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Malformed resource content manifest.", error);
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("Resource content manifest requires UTF-8.", error);
            }
        }

        private static Dictionary<string, ResourceContentArtifact> ReadArtifacts(JObject root,
            ResourceContentManifestTrust trust, string catalogRoute, string descriptionRoute)
        {
            if (root["artifacts"] is not JArray rows || rows.Count is < 1 or > MaximumArtifacts)
            {
                throw new InvalidDataException("Resource content artifact count is outside its bounds.");
            }

            var artifacts = new Dictionary<string, ResourceContentArtifact>(rows.Count, StringComparer.Ordinal);
            var routePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, ResourceContentArtifact>(StringComparer.OrdinalIgnoreCase);
            bool hasCiphertext = false;
            foreach (JToken item in rows)
            {
                var row = item as JObject;
                RequireFields(row, _artifactFields);
                string route = ReadString(row, "route");
                string originReleaseId = ReadString(row, "originReleaseId");
                string source = ReadString(row, "source");
                string sha256 = ReadString(row, "sha256");
                if (!ResourceContentManifestTrust.IsId(originReleaseId) || !ResourceContentManifestTrust.IsSha256(sha256))
                {
                    throw new InvalidDataException("Invalid resource content artifact identity.");
                }

                var kind = ReadString(row, "kind") switch
                {
                    "Catalog" => ResourceContentArtifactKind.Catalog,
                    "Bundle" => ResourceContentArtifactKind.Bundle,
                    "EncryptedDescription" => ResourceContentArtifactKind.EncryptedDescription,
                    "Ciphertext" => ResourceContentArtifactKind.Ciphertext,
                    _ => throw new InvalidDataException("Unknown resource content artifact kind.")
                };
                if (kind == ResourceContentArtifactKind.Catalog && route != catalogRoute ||
                    kind == ResourceContentArtifactKind.EncryptedDescription && route != descriptionRoute)
                {
                    throw new InvalidDataException("Unexpected resource content Catalog or description artifact.");
                }

                string routePath = ResourceContentManifestTrust.GetRoutePath(route, trust.AllowsLoopbackHttp);
                string sourcePath = trust.GetSourcePath(source);
                RequireArtifactExtension(kind, routePath, sourcePath);
                string routeIdentity = route.StartsWith(ResourceContentManifestTrust.RuntimePath, StringComparison.Ordinal)
                    ? routePath : route;
                if (!routePaths.Add(routeIdentity))
                {
                    throw new InvalidDataException("Duplicate or equivalent resource content artifact routes.");
                }

                var artifact = new ResourceContentArtifact(route, originReleaseId, source, kind, ReadInteger(row, "length"),
                    sha256, ReadRouteList(row, "dependencies"));
                if (sources.TryGetValue(source, out var first))
                {
                    if (first.Source != source || first.Sha256 != sha256 || first.Length != artifact.Length ||
                        first.OriginReleaseId != originReleaseId || first.Kind != kind)
                    {
                        throw new InvalidDataException("Conflicting identities for an equivalent resource content source.");
                    }
                }
                else
                {
                    sources.Add(source, artifact);
                }

                artifacts.Add(route, artifact);
                hasCiphertext |= kind == ResourceContentArtifactKind.Ciphertext;
            }

            if (!artifacts.TryGetValue(catalogRoute, out var catalog) || catalog.Kind != ResourceContentArtifactKind.Catalog ||
                (descriptionRoute != null) != hasCiphertext || (descriptionRoute != null &&
                (!artifacts.TryGetValue(descriptionRoute, out var description) ||
                    description.Kind != ResourceContentArtifactKind.EncryptedDescription)))
            {
                throw new InvalidDataException("Missing resource content Catalog or encrypted description binding.");
            }
            return artifacts;
        }

        private static void RequireArtifactExtension(ResourceContentArtifactKind kind, string route, string source)
        {
            string extension = Path.GetExtension(route);
            bool matchesKind = kind switch
            {
                ResourceContentArtifactKind.Catalog => extension is ".bin" or ".json",
                ResourceContentArtifactKind.EncryptedDescription => extension == ".json",
                _ => extension == ".bundle"
            };
            if (!matchesKind || Path.GetExtension(source) != extension)
            {
                throw new InvalidDataException("Resource content artifact and source do not match the allowed file type.");
            }
        }

        private static void ValidateDependencies(Dictionary<string, ResourceContentArtifact> artifacts)
        {
            var unresolved = new Dictionary<string, int>(artifacts.Count, StringComparer.Ordinal);
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ready = new Queue<string>();
            foreach (var artifact in artifacts.Values)
            {
                unresolved.Add(artifact.Route, artifact.Dependencies.Count);
                if (artifact.Dependencies.Count == 0)
                {
                    ready.Enqueue(artifact.Route);
                }

                foreach (string dependency in artifact.Dependencies)
                {
                    if (!artifacts.TryGetValue(dependency, out var target) || target.Kind is not
                        (ResourceContentArtifactKind.Bundle or ResourceContentArtifactKind.Ciphertext))
                    {
                        throw new InvalidDataException("Missing or invalid resource content artifact dependency.");
                    }

                    if (!dependents.TryGetValue(dependency, out var referencingRoutes))
                    {
                        referencingRoutes = new List<string>();
                        dependents.Add(dependency, referencingRoutes);
                    }
                    referencingRoutes.Add(artifact.Route);
                }
            }

            int resolved = 0;
            while (ready.Count > 0)
            {
                string route = ready.Dequeue();
                resolved++;
                if (!dependents.TryGetValue(route, out var referencingRoutes))
                {
                    continue;
                }

                foreach (string referencingRoute in referencingRoutes)
                {
                    if (--unresolved[referencingRoute] == 0)
                    {
                        ready.Enqueue(referencingRoute);
                    }
                }
            }

            if (resolved != artifacts.Count)
            {
                throw new InvalidDataException("Cyclic resource content artifact dependencies.");
            }
        }

        private static List<string> FindStartupRoutes(Dictionary<string, ResourceContentArtifact> artifacts,
            List<string> roots, string catalogRoute, string descriptionRoute)
        {
            var closure = new List<string> { catalogRoute };
            var seen = new HashSet<string>(StringComparer.Ordinal) { catalogRoute };
            Add(descriptionRoute);
            foreach (string route in roots)
            {
                if (!artifacts.ContainsKey(route))
                {
                    throw new InvalidDataException("Missing startup resource content artifact.");
                }
                Add(route);
            }

            for (int index = 0; index < closure.Count; index++)
            {
                foreach (string dependency in artifacts[closure[index]].Dependencies)
                {
                    Add(dependency);
                }
            }
            return closure;

            void Add(string route)
            {
                if (route != null && seen.Add(route))
                {
                    closure.Add(route);
                }
            }
        }

        private static void RequireFields(JObject row, string[] fields)
        {
            if (row == null || row.Count != fields.Length)
            {
                throw new InvalidDataException("Unexpected resource content manifest fields.");
            }
            foreach (string field in fields)
            {
                if (row[field] == null)
                {
                    throw new InvalidDataException("Missing resource content manifest field: " + field);
                }
            }
        }

        private static long ReadInteger(JObject row, string field)
        {
            return row[field] is JValue { Value: long value } && value > 0 ? value :
                throw new InvalidDataException("Expected a positive resource content manifest integer: " + field);
        }

        private static string ReadString(JObject row, string field)
        {
            return row[field] is JValue { Type: JTokenType.String, Value: string value } ? value :
                throw new InvalidDataException("Expected a resource content manifest string: " + field);
        }

        private static List<string> ReadRouteList(JObject row, string field)
        {
            if (row[field] is not JArray array || array.Count > MaximumArtifacts)
            {
                throw new InvalidDataException("Expected a bounded resource content route list: " + field);
            }

            var result = new List<string>(array.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in array)
            {
                if (token is not JValue { Type: JTokenType.String, Value: string value } || !seen.Add(value))
                {
                    throw new InvalidDataException("Invalid or duplicate route in resource content manifest: " + field);
                }
                result.Add(value);
            }
            return result;
        }
    }
}
