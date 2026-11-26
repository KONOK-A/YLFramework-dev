using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace YLFramework.Resource
{
    /// <summary>发布协议允许的制品类别。</summary>
    public enum ResourceContentArtifactKind
    {
        Catalog,
        Bundle,
        EncryptedDescription,
        Ciphertext
    }

    /// <summary>一个逻辑路由的制品身份。验签后的清单包含已经核对的条目。</summary>
    public sealed class ResourceContentArtifact
    {
        public ResourceContentArtifact(string route, string originReleaseId, string source,
            ResourceContentArtifactKind kind, long length, string sha256, IEnumerable<string> dependencies = null)
        {
            Route = route;
            OriginReleaseId = originReleaseId;
            Source = source;
            Kind = kind;
            Length = length;
            Sha256 = sha256;
            Dependencies = Array.AsReadOnly(dependencies?.ToArray() ?? Array.Empty<string>());
        }

        public string Route
        {
            get;
        }
        public string OriginReleaseId
        {
            get;
        }
        public string Source
        {
            get;
        }
        public ResourceContentArtifactKind Kind
        {
            get;
        }
        public long Length
        {
            get;
        }
        public string Sha256
        {
            get;
        }
        public IReadOnlyList<string> Dependencies
        {
            get;
        }
    }

    /// <summary>公告序号与本地最高成功激活记录的关系。</summary>
    public enum ResourceContentSequenceComparison
    {
        Lower,
        Conflict,
        Same,
        New
    }

    /// <summary>经过签名、目标及制品表校验的只读发布清单。</summary>
    public sealed partial class ResourceContentManifest
    {
        public const int MaximumPayloadBytes = 8 * 1024 * 1024;
        public const int MaximumSignatureEnvelopeBytes = 4 + 1 + ResourceContentManifestTrust.MaximumIdLength + 384;
        internal const int ProtocolVersion = 1;
        internal const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

        private ResourceContentManifest(ResourceContentManifestTrust trust, string releaseId, long sequence,
            DateTimeOffset expiresAtUtc, string catalogRoute, string descriptionRoute, string digest,
            Dictionary<string, ResourceContentArtifact> artifacts, List<string> startupRoutes)
        {
            Application = trust.Application;
            Channel = trust.Channel;
            Platform = trust.Platform;
            Compatibility = trust.Compatibility;
            SettingsSha256 = trust.SettingsSha256;
            ReleaseId = releaseId;
            Sequence = sequence;
            ExpiresAtUtc = expiresAtUtc;
            CatalogRoute = catalogRoute;
            DescriptionRoute = descriptionRoute;
            Digest = digest;
            Artifacts = new ReadOnlyDictionary<string, ResourceContentArtifact>(artifacts);
            StartupRoutes = startupRoutes.AsReadOnly();
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
        public string ReleaseId
        {
            get;
        }
        public long Sequence
        {
            get;
        }
        public DateTimeOffset ExpiresAtUtc
        {
            get;
        }
        public string SettingsSha256
        {
            get;
        }
        public string CatalogRoute
        {
            get;
        }
        public string DescriptionRoute
        {
            get;
        }
        public string Digest
        {
            get;
        }
        public IReadOnlyDictionary<string, ResourceContentArtifact> Artifacts
        {
            get;
        }

        /// <summary>启动必需路由的传递闭包，包含 Catalog 和可选描述文件。</summary>
        public IReadOnlyList<string> StartupRoutes
        {
            get;
        }

        /// <summary>校验签名和目标。提供时间时，还要求新公告尚未过期。</summary>
        public static ResourceContentManifest Verify(byte[] payload, byte[] signatureEnvelope,
            ResourceContentManifestTrust trust, DateTimeOffset? announcementTimeUtc = null)
        {
            if (trust == null)
            {
                throw new ArgumentNullException(nameof(trust));
            }

            if (payload == null || payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            {
                throw new InvalidDataException("Resource content manifest payload is outside its size bound.");
            }

            // 输入归调用者所有。用同一份快照验签、取摘要和解析。
            byte[] snapshot = (byte[])payload.Clone();
            byte[] digest = ResourceContentManifestSignature.Verify(snapshot, signatureEnvelope, trust);
            var manifest = Parse(snapshot, BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant(), trust);
            if (announcementTimeUtc.HasValue)
            {
                manifest.RequireUnexpiredAnnouncement(announcementTimeUtc.Value);
            }
            return manifest;
        }

        /// <summary>仅在接纳新公告时检查截止时间。</summary>
        public void RequireUnexpiredAnnouncement(DateTimeOffset nowUtc)
        {
            if (nowUtc >= ExpiresAtUtc)
            {
                throw new InvalidDataException("Resource content announcement has expired.");
            }
        }

        /// <summary>首次安装的未提交状态传入 0 和 null。</summary>
        public ResourceContentSequenceComparison CompareSequence(long highestSequence, string highestDigest)
        {
            if (highestSequence < 0 || (highestSequence == 0 ? highestDigest != null :
                !ResourceContentManifestTrust.IsSha256(highestDigest)))
            {
                throw new ArgumentException("Invalid highest resource content sequence and digest.");
            }

            if (Sequence < highestSequence)
            {
                return ResourceContentSequenceComparison.Lower;
            }
            if (Sequence > highestSequence)
            {
                return ResourceContentSequenceComparison.New;
            }
            return Digest == highestDigest ? ResourceContentSequenceComparison.Same : ResourceContentSequenceComparison.Conflict;
        }
    }
}
