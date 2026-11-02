using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <summary>只适配自有部署外壳；有效载荷的闭合文件集与完整性仍由原快照验证。</summary>
    internal static class ResourceBuiltInLayout
    {
        internal const string PayloadFolder = "payload~";
        internal const string ReceiptFile = "ylr-deployment.json~";
        internal const string OwnerFile = "ylr-owner.json~";

        /// <summary>拒绝链接并有界读取控制记录，只接受与规范序列化完全一致的 JSON 字节。</summary>
        internal static JObject ReadObject(string path)
        {
            ResourceLocalSnapshot.RejectLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > ResourceCatalogCodec.MaximumBytes)
            {
                throw new InvalidDataException("Invalid built-in control record size: " + path);
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var data = ResourceCatalogCodec.ParseObject(bytes);
            if (!bytes.SequenceEqual(ResourceCatalogCodec.CanonicalBytes(data)))
            {
                throw new InvalidDataException("Noncanonical built-in control record: " + path);
            }

            return data;
        }

        /// <summary>生成将部署方 GUID 与明确目录摘要绑定的规范回执字节，不写入文件。</summary>
        internal static byte[] Receipt(ResourceCatalogDocument catalog, string projectGuid) => ResourceCatalogCodec.CanonicalBytes(new JObject
        {
            ["Version"] = 1,
            ["ProjectGuid"] = projectGuid,
            ["ProjectId"] = catalog.ProjectId,
            ["Platform"] = catalog.Platform,
            ["CatalogDigest"] = catalog.CatalogDigest
        });

        /// <summary>定位部署外壳内约定的有效载荷目录，不创建或探测目录。</summary>
        internal static string Payload(string wrapper) => Path.Combine(wrapper, PayloadFolder);

        /// <summary>严格检查外壳内容与回执身份，再打开有效载荷并确认完成标记和回执属于同一部署方。</summary>
        internal static ResourceLocalSnapshot Open(string wrapper, string projectId, string digest, string platform, string app,
            string unityCompatibility = null, CancellationToken cancellation = default)
        {
            ResourceLocalSnapshot.RejectLinks(wrapper);
            var expected = new[] { PayloadFolder, ReceiptFile }.OrderBy(x => x, StringComparer.Ordinal);
            if (!Directory.GetFileSystemEntries(wrapper).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected))
            {
                throw new InvalidDataException("Built-in deployment contains unregistered wrapper content: " + wrapper);
            }

            var payloadRoot = Path.Combine(wrapper, PayloadFolder);
            ResourceLocalSnapshot.RejectLinks(payloadRoot);
            if (!Directory.Exists(payloadRoot))
            {
                throw new InvalidDataException("Built-in payload directory is missing.");
            }

            var receipt = ReadObject(Path.Combine(wrapper, ReceiptFile));
            ResourceCatalogCodec.Fields(receipt, "Version,ProjectGuid,ProjectId,Platform,CatalogDigest");
            var guid = ResourceCatalogCodec.Text(receipt, "ProjectGuid");
            ResourceCatalogCodec.Hex(guid, 32);
            if (ResourceCatalogCodec.Integer(receipt, "Version") != 1 || ResourceCatalogCodec.Text(receipt, "ProjectId") != projectId ||
                ResourceCatalogCodec.Text(receipt, "Platform") != platform || ResourceCatalogCodec.Text(receipt, "CatalogDigest") != digest)
            {
                throw new InvalidDataException("Built-in deployment ownership/identity conflict.");
            }

            var snapshot = ResourceLocalSnapshot.OpenBuiltInPayload(wrapper, projectId, digest, platform, app, unityCompatibility, cancellation);
            if (ResourceCatalogCodec.Text(ReadObject(Path.Combine(snapshot.DirectoryPath, ".ylr-complete.json")), "ProjectGuid") != guid)
            {
                throw new InvalidDataException("Built-in deployment and Complete owners differ.");
            }

            return snapshot;
        }
    }
}
