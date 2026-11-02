using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <summary>只定位一个完整 Catalog 快照，不复制资产表，不表示运行时激活或生产签名授权。</summary>
    public sealed class ResourceReleaseIndex
    {
        /// <summary>索引采用的固定持久化 Schema 版本。</summary>
        public int SchemaVersion => 1;
        /// <summary>索引指向的资源项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>所选发布声明的目标平台。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>所选发布要求精确匹配的 Unity 兼容值。</summary>
        public string UnityCompatibility
        {
            get;
        }
        /// <summary>应用明确约定的内容兼容标识。</summary>
        public string AppContentCompatibilityId
        {
            get;
        }
        /// <summary>所选目录有效载荷的 BuildId，索引读取不会自行打开目录来复核。</summary>
        public string BuildId
        {
            get;
        }
        /// <summary>所选最终目录字节的 SHA-256；不等同于签名授权。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>由摘要目录与固定目录文件名组成的相对定位，不接受任意文件路径。</summary>
        public string CatalogFile => CatalogDigest + "/resource-catalog.json";

        /// <summary>保存已经由目录或索引读取流程校验的明确发布身份。</summary>
        private ResourceReleaseIndex(string project, string platform, string unity, string app, string build, string digest)
        {
            ProjectId = project;
            Platform = platform;
            UnityCompatibility = unity;
            AppContentCompatibilityId = app;
            BuildId = build;
            CatalogDigest = digest;
        }

        /// <summary>从已读取的目录文档建立索引，不写文件或激活运行时版本。</summary>
        public static ResourceReleaseIndex Create(ResourceCatalogDocument catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            return new ResourceReleaseIndex(catalog.ProjectId, catalog.Platform, catalog.UnityCompatibility,
                catalog.AppContentCompatibilityId, catalog.BuildId, catalog.CatalogDigest);
        }

        /// <summary>输出包含固定目录定位的规范 JSON 字节，不访问发布文件。</summary>
        public byte[] Write() => ResourceCatalogCodec.CanonicalBytes(new JObject
        {
            ["SchemaVersion"] = 1,
            ["ProjectId"] = ProjectId,
            ["Platform"] = Platform,
            ["UnityCompatibility"] = UnityCompatibility,
            ["AppContentCompatibilityId"] = AppContentCompatibilityId,
            ["BuildId"] = BuildId,
            ["CatalogDigest"] = CatalogDigest,
            ["CatalogFile"] = CatalogFile
        });

        /// <summary>有界读取索引，精确匹配兼容值并要求定位与全部字节符合规范；不验证目标文件存在或签名。</summary>
        public static ResourceReleaseIndex Read(byte[] bytes, string projectId, string platform, string unityCompatibility, string appContentCompatibilityId)
        {
            if (bytes == null || bytes.Length > 8192)
            {
                throw new InvalidDataException("Release Index byte limit exceeded.");
            }

            var data = ResourceCatalogCodec.ParseObject(bytes);
            ResourceCatalogCodec.Fields(data, "SchemaVersion,ProjectId,Platform,UnityCompatibility,AppContentCompatibilityId,BuildId,CatalogDigest,CatalogFile");
            if (ResourceCatalogCodec.Integer(data, "SchemaVersion") != 1)
            {
                throw new InvalidDataException("Unsupported Release Index schema.");
            }

            var project = ResourceCatalogCodec.Text(data, "ProjectId");
            ResourceCatalogCodec.Id(project);
            var actualPlatform = ResourceCatalogCodec.Text(data, "Platform");
            var unity = ResourceCatalogCodec.Text(data, "UnityCompatibility");
            var app = ResourceCatalogCodec.Text(data, "AppContentCompatibilityId");
            ResourceCatalogCodec.Id(app);
            var build = ResourceCatalogCodec.Text(data, "BuildId");
            ResourceCatalogCodec.Hex(build, 64);
            var digest = ResourceCatalogCodec.Text(data, "CatalogDigest");
            ResourceCatalogCodec.Hex(digest, 64);
            if (project != projectId || actualPlatform != platform || unity != unityCompatibility || app != appContentCompatibilityId)
            {
                throw new InvalidDataException("Release Index project/platform/Unity/app compatibility mismatch.");
            }

            var index = new ResourceReleaseIndex(project, actualPlatform, unity, app, build, digest);
            if (ResourceCatalogCodec.Text(data, "CatalogFile") != index.CatalogFile || !bytes.SequenceEqual(index.Write()))
            {
                throw new InvalidDataException("Invalid or noncanonical Catalog locator in Release Index.");
            }

            return index;
        }
    }
}
