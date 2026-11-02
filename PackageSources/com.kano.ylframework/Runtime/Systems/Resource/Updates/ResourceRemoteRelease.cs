using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <summary>经过可信公钥认证的发布授权；不代表Catalog/文件已下载、Prepared或Active。</summary>
    internal sealed class ResourceRemoteRelease
    {
        internal readonly ResourceRemoteSource Source;
        internal readonly byte[] Envelope;
        internal readonly string ProjectId;
        internal readonly string Platform;
        internal readonly string Channel;
        internal readonly string CatalogDigest;
        internal readonly string BuildId;
        internal readonly long Sequence;
        internal readonly long CatalogLength;
        internal readonly long CompleteLength;
        internal readonly string CompleteSha256;
        internal readonly long Expires;
        internal ResourceCatalogDocument Catalog
        {
            get; private set;
        }

        /// <summary>使用挂载配置的公钥验签，并校验发布范围、序号和有效期结构；可选择是否要求尚未过期。</summary>
        internal ResourceRemoteRelease(byte[] envelope, ResourceProjectMount mount, string unity, DateTimeOffset now, bool fresh = true)
        {
            Source = mount.Remote ?? throw new InvalidDataException("The project has no explicitly trusted remote source.");
            var header = ResourceReleaseSignature.ReadEnvelope(envelope);
            var key = Source.Keys.SingleOrDefault(x => x.KeyId == (string)header["KeyId"]) ?? throw new InvalidDataException("Release key is not authorized by this project.");
            var data = ResourceReleaseSignature.Verify(envelope, key.KeyId, key.Parameters);
            ResourceCatalogCodec.Fields(data, "ProjectId,Platform,UnityCompatibility,AppContentCompatibilityId,Channel,ReleaseSequence,CatalogDigest,CatalogLength,BuildId,CompleteLength,CompleteSha256,IssuedUnixSeconds,ExpiresUnixSeconds");
            ProjectId = ResourceCatalogCodec.Text(data, "ProjectId");
            Platform = ResourceCatalogCodec.Text(data, "Platform");
            Channel = ResourceCatalogCodec.Text(data, "Channel");
            if (ProjectId != mount.ProjectId || Platform != mount.Platform || Channel != Source.Channel || ResourceCatalogCodec.Text(data, "UnityCompatibility") != unity ||
                ResourceCatalogCodec.Text(data, "AppContentCompatibilityId") != mount.AppContentCompatibilityId)
            {
                throw new InvalidDataException("Release project/platform/channel/Unity/app authorization mismatch.");
            }

            CatalogDigest = ResourceCatalogCodec.Text(data, "CatalogDigest");
            ResourceCatalogCodec.Hex(CatalogDigest, 64);
            BuildId = ResourceCatalogCodec.Text(data, "BuildId");
            ResourceCatalogCodec.Hex(BuildId, 64);
            CompleteSha256 = ResourceCatalogCodec.Text(data, "CompleteSha256");
            ResourceCatalogCodec.Hex(CompleteSha256, 64);
            Sequence = ResourceCatalogCodec.Integer(data, "ReleaseSequence");
            CatalogLength = ResourceCatalogCodec.Integer(data, "CatalogLength");
            CompleteLength = ResourceCatalogCodec.Integer(data, "CompleteLength");
            Expires = ResourceCatalogCodec.Integer(data, "ExpiresUnixSeconds");
            var issued = ResourceCatalogCodec.Integer(data, "IssuedUnixSeconds");
            if (Sequence < Source.MinimumReleaseSequence || CatalogLength < 1 || CatalogLength > ResourceCatalogCodec.MaximumBytes || CompleteLength < 1 || CompleteLength > ResourceReleaseSignature.MaximumBytes ||
                issued < 0 || issued > now.ToUnixTimeSeconds() + 300 || Expires <= issued || Expires - issued > 31L * 86400)
            {
                throw new InvalidDataException("Release sequence, file bound or validity interval rejected.");
            }

            if (fresh)
            {
                RequireFresh(now);
            }

            Envelope = (byte[])envelope.Clone();
        }

        /// <summary>按给定本地时间拒绝已到期的发布授权，不替代已确认序号检查。</summary>
        internal void RequireFresh(DateTimeOffset now)
        {
            if (Expires <= now.ToUnixTimeSeconds())
            {
                throw new InvalidDataException("Release metadata expired according to the local clock.");
            }
        }

        /// <summary>核对 Catalog 的签名绑定摘要、长度与 BuildId，并检查整个发布的下载预算。</summary>
        internal void ReadCatalog(byte[] bytes, ResourceProjectMount mount, string unity)
        {
            if (bytes.LongLength != CatalogLength || ResourceCatalogCodec.Digest(bytes) != CatalogDigest)
            {
                throw new InvalidDataException("Signed catalog length/digest mismatch.");
            }

            var catalog = ResourceCatalogCodec.Read(bytes, ProjectId, Platform, unity, mount.AppContentCompatibilityId);
            if (catalog.BuildId != BuildId)
            {
                throw new InvalidDataException("Signed BuildId/Catalog mismatch.");
            }

            long total = checked(CatalogLength + CompleteLength);
            foreach (var file in catalog.Bundles.Cast<ResourceCatalogFile>().Concat(catalog.SupportFiles))
            {
                if (file.Length > Source.MaximumFileBytes)
                {
                    throw new InvalidDataException("Release file exceeds the configured download bound.");
                }

                total = checked(total + file.Length);
            }
            if (total > Source.MaximumDownloadBytes)
            {
                throw new InvalidDataException("Release exceeds the configured total download bound.");
            }

            Catalog = catalog;
        }

        /// <summary>生成此发布的历史门槛描述；生成描述本身不表示发布已提交确认。</summary>
        internal JObject Floor() => new() { ["ProjectId"] = ProjectId, ["Platform"] = Platform, ["Channel"] = Channel, ["Sequence"] = Sequence, ["CatalogDigest"] = CatalogDigest };
    }
}
