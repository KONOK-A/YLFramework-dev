using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if !UNITY_6000_0_OR_NEWER
using PhysicsMaterial = UnityEngine.PhysicMaterial;
#endif

namespace YLFramework.Resource
{
    /// <summary>显式绑定的本地 v1 发布快照。完整验证只读目录，不选择版本、不写索引、不加载 native Bundle。</summary>
    /// <remarks>校验针对打开时的文件内容，不承诺目录此后不可变，也不将 SHA-256 当作签名授权；进入原生加载前仍须复验。</remarks>
    public sealed class ResourceLocalSnapshot
    {
        /// <summary>经规范化的绝对有效载荷目录；内置部署外壳不属于此路径。</summary>
        public string DirectoryPath
        {
            get;
        }
        /// <summary>通过明确摘要与兼容值校验的只读目录文档。</summary>
        public ResourceCatalogDocument Document
        {
            get;
        }
        internal readonly Dictionary<string, ResourceCatalogBundle> Bundles;
        private readonly string _builtInWrapper;

        /// <summary>保存已完成验证的目录及可选外壳来源，并建立 Bundle 逻辑标识索引。</summary>
        private ResourceLocalSnapshot(string directory, ResourceCatalogDocument document, string wrapper = null)
        {
            DirectoryPath = directory;
            Document = document;
            _builtInWrapper = wrapper;
            Bundles = document.Bundles.ToDictionary(x => x.LogicalId, StringComparer.Ordinal);
        }

        /// <summary>摘要来自调用方的明确发布选择，不能从待验证文件自身推导信任。仅支持普通可读本地目录。</summary>
        public static ResourceLocalSnapshot Open(string directory, string projectId, string catalogDigest, string platform, string appContentCompatibilityId)
            => Open(directory, projectId, catalogDigest, platform, appContentCompatibilityId, false);

        /// <summary>只读打开 YLFrameworkResources/project/platform/digest 内置目录；摘要仍须由应用明确绑定。</summary>
        /// <param name="projectId">明确选择的稳定项目标识。</param>
        /// <param name="catalogDigest">应用绑定的最终目录字节 SHA-256，不从待验证目录自行选取。</param>
        /// <param name="platform">三桌面目标平台之一，须与目录声明一致。</param>
        /// <param name="appContentCompatibilityId">应用预期的内容兼容标识。</param>
        /// <param name="resourcesRoot">已部署的YLFrameworkResources绝对目录；默认位于Application.streamingAssetsPath下。不创建、复制或更新任何文件。</param>
        /// <remarks>仅支持现有三桌面普通可读目录；自有部署外壳定位到不参与Unity导入的严格payload。不扫描版本，不提供jar/WebGL提取。</remarks>
        public static ResourceLocalSnapshot OpenBuiltIn(string projectId, string catalogDigest, string platform, string appContentCompatibilityId,
            string resourcesRoot = null)
        {
            ResourceAddress.ValidateId(projectId, nameof(projectId));
            ResourceCatalogCodec.Hex(catalogDigest, 64);
            if (platform != "StandaloneWindows64" && platform != "StandaloneOSX" && platform != "StandaloneLinux64")
            {
                throw new ResourceException(ResourceError.Unsupported, "Built-in snapshots require a supported local desktop platform.");
            }

            resourcesRoot ??= Path.Combine(Application.streamingAssetsPath, "YLFrameworkResources");
            if (string.IsNullOrWhiteSpace(resourcesRoot) || !Path.IsPathFullyQualified(resourcesRoot))
            {
                throw new ArgumentException("Built-in resources require an absolute readable local directory; jar/WebGL extraction is not supported.", nameof(resourcesRoot));
            }

            var directory = Path.Combine(resourcesRoot, projectId, platform, catalogDigest);
            RejectLinks(directory);
            // 已有完整导出布局仍走原Open；外壳任一特征出现即必须完整验证，不能失败后回退。
            if (File.Exists(Path.Combine(directory, ResourceBuiltInLayout.ReceiptFile)) || Directory.Exists(Path.Combine(directory, ResourceBuiltInLayout.PayloadFolder)))
            {
                return ResourceBuiltInLayout.Open(directory, projectId, catalogDigest, platform, appContentCompatibilityId);
            }

            return Open(directory, projectId, catalogDigest, platform, appContentCompatibilityId);
        }

        /// <summary>打开 Store 已接管的版本目录，将版本所有权标记纳入允许的文件集合。</summary>
        internal static ResourceLocalSnapshot OpenOwned(string directory, string projectId, string catalogDigest, string platform, string appContentCompatibilityId,
            string unityCompatibility = null, CancellationToken cancellation = default)
            => Open(directory, projectId, catalogDigest, platform, appContentCompatibilityId, true, unityCompatibility: unityCompatibility, cancellation: cancellation);

        /// <summary>验证尚未提交的暂存目录，允许暂存标记且不要求末级目录名已经等于摘要。</summary>
        internal static ResourceLocalSnapshot OpenStaging(string directory, string projectId, string catalogDigest, string platform, string appContentCompatibilityId,
            string unityCompatibility = null, CancellationToken cancellation = default)
            => Open(directory, projectId, catalogDigest, platform, appContentCompatibilityId, false, true, unityCompatibility: unityCompatibility, cancellation: cancellation);

        /// <summary>以外壳来源打开约定有效载荷，保留后续重开时必须复验的部署边界。</summary>
        internal static ResourceLocalSnapshot OpenBuiltInPayload(string wrapper, string projectId, string catalogDigest, string platform, string appContentCompatibilityId,
            string unityCompatibility = null, CancellationToken cancellation = default)
            => Open(ResourceBuiltInLayout.Payload(wrapper), projectId, catalogDigest, platform, appContentCompatibilityId, false, builtInWrapper: wrapper,
                unityCompatibility: unityCompatibility, cancellation: cancellation);

        /// <summary>按调用方捕获的 Unity 兼容值打开普通快照，供后台验证避免读取 Unity 状态。</summary>
        internal static ResourceLocalSnapshot OpenChecked(string directory, string projectId, string catalogDigest, string platform,
            string appContentCompatibilityId, string unityCompatibility, CancellationToken cancellation = default)
            => Open(directory, projectId, catalogDigest, platform, appContentCompatibilityId, false, unityCompatibility: unityCompatibility, cancellation: cancellation);

        /// <summary>Store 重开快照时保留原来源边界，连外壳一起复验，不能仅放宽有效载荷末级目录名。</summary>
        internal ResourceLocalSnapshot Reopen(CancellationToken cancellation = default) => _builtInWrapper == null
            ? OpenChecked(DirectoryPath, Document.ProjectId, Document.CatalogDigest, Document.Platform, Document.AppContentCompatibilityId, Document.UnityCompatibility, cancellation)
            : ResourceBuiltInLayout.Open(_builtInWrapper, Document.ProjectId, Document.CatalogDigest, Document.Platform, Document.AppContentCompatibilityId, Document.UnityCompatibility, cancellation);

        /// <summary>校验显式目录摘要、兼容值、完成记录、闭合文件集合及每个交付文件的长度与 SHA-256。</summary>
        private static ResourceLocalSnapshot Open(string directory, string projectId, string catalogDigest, string platform, string appContentCompatibilityId, bool owned,
            bool staging = false, string builtInWrapper = null, string unityCompatibility = null, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            ResourceCatalogCodec.Hex(catalogDigest, 64);
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                throw new ArgumentException("An absolute local publication directory is required.");
            }

            directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            RejectLinks(directory);
            if (!staging && builtInWrapper == null && Path.GetFileName(directory) != catalogDigest)
            {
                throw new InvalidDataException("The explicit version directory must equal CatalogDigest.");
            }

            var bytes = ReadBounded(Path.Combine(directory, "resource-catalog.json"));
            if (ResourceCatalogCodec.Digest(bytes) != catalogDigest)
            {
                throw new InvalidDataException("Explicit CatalogDigest mismatch.");
            }

            var document = ResourceCatalogCodec.Read(bytes, projectId, platform, unityCompatibility ?? Application.unityVersion, appContentCompatibilityId);
            var complete = ResourceCatalogCodec.ParseObject(ReadBounded(Path.Combine(directory, ".ylr-complete.json")));
            ValidateComplete(complete, projectId, platform, catalogDigest, document.BuildId);
            var files = document.Bundles.Cast<ResourceCatalogFile>().Concat(document.SupportFiles).ToArray();
            var expected = files.Select(x => x.RelativeFile).Concat(owned ? new[] { "resource-catalog.json", ".ylr-complete.json", ".ylr-version.json" } :
                staging ? new[] { "resource-catalog.json", ".ylr-complete.json", ".ylr-stage.json" } :
                new[] { "resource-catalog.json", ".ylr-complete.json" }).OrderBy(x => x, StringComparer.Ordinal);
            if (!Directory.GetFileSystemEntries(directory).All(File.Exists) || !Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected))
            {
                throw new InvalidDataException("Publication contains missing or unregistered content.");
            }

            var snapshot = new ResourceLocalSnapshot(directory, document, builtInWrapper);
            foreach (var file in files)
            {
                using (snapshot.OpenVerified(file, cancellation))
                {
                }
            }

            return snapshot;
        }

        /// <summary>检查完成记录的固定字段与发布身份；未提供 BuildId 时只检查其摘要格式。</summary>
        internal static void ValidateComplete(JObject complete, string projectId, string platform, string catalogDigest, string buildId = null)
        {
            ResourceCatalogCodec.Fields(complete, "Version,ProjectGuid,ProjectId,Platform,BuildId,CatalogDigest");
            ResourceCatalogCodec.Hex(ResourceCatalogCodec.Text(complete, "ProjectGuid"), 32);
            ResourceCatalogCodec.Hex(ResourceCatalogCodec.Text(complete, "BuildId"), 64);
            if (ResourceCatalogCodec.Integer(complete, "Version") != 1 || ResourceCatalogCodec.Text(complete, "ProjectId") != projectId ||
                ResourceCatalogCodec.Text(complete, "Platform") != platform || buildId != null && ResourceCatalogCodec.Text(complete, "BuildId") != buildId ||
                ResourceCatalogCodec.Text(complete, "CatalogDigest") != catalogDigest)
            {
                throw new InvalidDataException("Publication Complete/Catalog identity mismatch.");
            }
        }

        /// <summary>显式编译类型映射。默认包含常用 Unity 资产；自定义 ScriptableObject 等须提供 typeof，不反射扫描或从字符串创建类型。</summary>
        /// <remarks>场景不转换为普通资产条目；子资产逻辑键使用 localId，原生名称选择描述仍保留在条目中。</remarks>
        public ResourceCatalog CreateCatalog(IEnumerable<Type> assetTypes = null)
        {
            var types = new Dictionary<(string, string), Type>();
            foreach (var type in new[] { typeof(TextAsset), typeof(GameObject), typeof(Texture2D), typeof(Sprite), typeof(UnityEngine.U2D.SpriteAtlas), typeof(Material), typeof(Shader),
                typeof(AudioClip), typeof(AnimationClip), typeof(Mesh), typeof(Font), typeof(RuntimeAnimatorController), typeof(PhysicsMaterial), typeof(PhysicsMaterial2D) }
                .Concat(assetTypes ?? Array.Empty<Type>()))
            {
                if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type) || type.ContainsGenericParameters)
                {
                    throw new ArgumentException("A compiled Unity asset type is required.");
                }

                types[(type.FullName, type.Assembly.GetName().Name)] = type;
            }
            var entries = new List<ResourceCatalogEntry>();
            foreach (var asset in Document.Assets.Where(x => x.Kind != "Scene"))
            {
                if (!types.TryGetValue((asset.AssetType, asset.AssetAssembly), out var type))
                {
                    throw new ResourceException(ResourceError.TypeMismatch, $"No explicit compiled type for {asset.AssetType}, {asset.AssetAssembly}.");
                }

                var entry = new ResourceCatalogEntry(asset.AssetId, new ResourceAddress(Document.ProjectId, asset.Address, asset.SubAssetKey),
                    new ResourceLocation(ResourceSourceId.AssetBundle, asset.LoadName, type),
                    asset.Aliases.Select(x => new ResourceAddress(Document.ProjectId, x, asset.SubAssetKey)));
                entry.Snapshot = this;
                entry.Descriptor = asset;
                entries.Add(entry);
            }
            return new ResourceCatalog(Document.ProjectId, entries) { Snapshot = this, Groups = Document.Groups };
        }

        /// <summary>复验并返回位置归零的只读句柄；失败时关闭句柄，成功后由调用方持有至使用结束。</summary>
        /// <remarks>原生加载方持有句柄至卸载；FileShare.Read 限制遵守共享规则的写入方，不代表文件系统绝对不可变。</remarks>
        internal FileStream OpenVerified(ResourceCatalogFile file, CancellationToken cancellation = default)
        {
            var stream = OpenContentFile(file);
            try
            {
                VerifyContentFile(stream, file, cancellation);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>拒绝路径及祖先链接后打开交付文件，不在此计算内容摘要。</summary>
        internal FileStream OpenContentFile(ResourceCatalogFile file)
        {
            var path = Path.Combine(DirectoryPath, file.RelativeFile);
            RejectLinks(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        /// <summary>从头核对交付长度与完整 SHA-256，支持分块取消，成功后将流位置复位到零。</summary>
        internal static void VerifyContentFile(FileStream stream, ResourceCatalogFile file, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            stream.Position = 0;
            if (stream.Length != file.Length)
            {
                throw new InvalidDataException("Snapshot file integrity mismatch: " + file.RelativeFile);
            }

            using var sha = SHA256.Create();
            byte[] digest;
            if (!cancellation.CanBeCanceled)
            {
                digest = sha.ComputeHash(stream);
            }
            else
            {
                var buffer = new byte[32768];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                digest = sha.Hash;
            }
            cancellation.ThrowIfCancellationRequested();
            if (stream.Length != file.Length || BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant() != file.Sha256)
            {
                throw new InvalidDataException("Snapshot file integrity mismatch: " + file.RelativeFile);
            }

            stream.Position = 0;
        }

        /// <summary>拒绝链接并有界读取元数据文件，不在读取阶段解释其 Schema。</summary>
        private static byte[] ReadBounded(string path)
        {
            RejectLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > ResourceCatalogCodec.MaximumBytes)
            {
                throw new InvalidDataException("Publication metadata size limit exceeded.");
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        /// <summary>沿路径至根检查现存节点的 ReparsePoint，拒绝链接输入；不提供并发路径替换的原子性保证。</summary>
        internal static void RejectLinks(string path)
        {
            for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Linked resource input is not supported: " + cursor);
                }
            }
        }
    }
}
