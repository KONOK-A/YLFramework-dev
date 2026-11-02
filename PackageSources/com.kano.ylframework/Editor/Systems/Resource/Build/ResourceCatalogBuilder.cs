using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>只从本构建内核的完整 Workspace 快照生成目录，不读取旧 ResData 或外部资源映射。</summary>
    public static class ResourceCatalogBuilder
    {
        /// <summary>锁定并逐文件复验构建结果后创建正式 Catalog 文档，不发布或挂载资源。</summary>
        public static ResourceCatalogDocument Create(ResourceBuildResult result, string appContentCompatibilityId)
        {
            using var locked = LockWorkspace(result);
            return CreateLocked(result, appContentCompatibilityId);
        }

        /// <summary>从调用方已锁定验证的快照映射交付文件与资产身份，再经正式编解码器封口校验。</summary>
        internal static ResourceCatalogDocument CreateLocked(ResourceBuildResult result, string app)
        {
            JObject FileRecord(ResourceBuiltFile file) => new JObject { ["RelativeFile"] = PublishedFile(result, file.Name), ["Length"] = file.Length, ["Sha256"] = file.Sha256 };
            // 子资产沿用主资产地址与别名，以 localId 十进制键区分；原生查找仍使用源路径和唯一名称/编译类型。
            JObject Asset(ResourceBuildAsset main, ResourceBuiltSubAsset sub = null) => new JObject
            {
                ["AssetId"] = main.Guid + "_" + (sub?.LocalId ?? main.LocalId).ToString(CultureInfo.InvariantCulture),
                ["Guid"] = main.Guid,
                ["LocalId"] = sub?.LocalId ?? main.LocalId,
                ["Address"] = main.Address.Path,
                ["SubAssetKey"] = sub?.LocalId.ToString(CultureInfo.InvariantCulture),
                ["BundleId"] = main.BundleName,
                ["Aliases"] = new JArray(main.Aliases.Select(x => x.Path)),
                ["LoadName"] = main.IsScene ? main.SourcePath : main.SourcePath.ToLowerInvariant(),
                ["AssetType"] = main.IsScene ? "Scene" : (sub?.AssetType ?? main.AssetType).FullName,
                ["AssetAssembly"] = main.IsScene ? null : (sub?.AssetType ?? main.AssetType).Assembly.GetName().Name,
                ["Kind"] = main.IsScene ? "Scene" : sub == null ? "Asset" : "SubAsset",
                ["SubAssetName"] = sub?.Name
            };
            var bundles = new JArray();
            foreach (var bundle in result.Bundles)
            {
                var data = FileRecord(result.DeliveryFiles.Single(x => x.Name == bundle.Name));
                data["LogicalId"] = bundle.Name;
                data["NativeName"] = bundle.Name;
                data["UnityHash"] = bundle.UnityHash;
                data["IsScene"] = result.Map.Bundles.Single(x => x.Name == bundle.Name).IsScene;
                data["Dependencies"] = new JArray(bundle.Dependencies);
                bundles.Add(data);
            }
            var payload = new JObject
            {
                ["SchemaVersion"] = 1,
                ["ProjectId"] = result.Map.ProjectId,
                ["Platform"] = result.Platform,
                ["UnityCompatibility"] = result.UnityCompatibility,
                ["AppContentCompatibilityId"] = app,
                ["BuildSettingsFingerprint"] = result.BuildSettingsFingerprint,
                ["ManifestFile"] = "manifest",
                ["Bundles"] = bundles,
                ["Assets"] = new JArray(result.Map.Assets.Select(x => Asset(x)).Concat(result.SubAssets.Select(x => Asset(x.MainAsset, x)))),
                ["Groups"] = new JArray(result.Map.Groups.Select(x => new JObject
                {
                    ["Id"] = x.Key,
                    ["Members"] = new JArray(x.Value.Select(id => new JObject { ["AssetId"] = id, ["Required"] = true }))
                })),
                ["SupportFiles"] = new JArray(result.DeliveryFiles.Where(x => !result.Bundles.Any(b => b.Name == x.Name)).Select(FileRecord))
            };
            var bytes = ResourceCatalogCodec.Seal(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
            return ResourceCatalogCodec.Read(bytes, result.Map.ProjectId, result.Platform, result.UnityCompatibility, app);
        }

        // 物理名不是 native identity。短 token 防止摘要版本目录 + native 命名空间超过 Mono Windows 的文件路径限制。
        internal static string PublishedFile(ResourceBuildResult result, string workspaceFile)
        {
            var sidecar = workspaceFile.EndsWith(".manifest", StringComparison.Ordinal);
            var native = sidecar ? workspaceFile.Substring(0, workspaceFile.Length - 9) : workspaceFile;
            var file = native == result.ManifestBundleFile ? "manifest" : "b_" + ResourceBuildMap.Token(native);
            return file + (sidecar ? ".manifest" : "");
        }

        // 锁住可变缓存并逐文件复验；旧 result 不能悄悄改指向当前另一代 Workspace 产物。
        internal static FileStream LockWorkspace(ResourceBuildResult result)
        {
            if (result?.Map == null || result.Files == null || result.Bundles == null || result.SubAssets == null || result.DeliveryFiles == null)
            {
                throw new InvalidOperationException("A verified ResourceBuildResult is required.");
            }

            ResourceBundleBuilder.ValidateContentPolicy(result.ContentEncryption, result.ContentKeyId, result.Platform);
            var workspace = new ResourceBuildWorkspace(result.Map, result.BuildSettingsFingerprint, result.Platform, result.ContentEncryption, result.ContentKeyId);
            if (workspace.Output != result.WorkspacePath || workspace.Leaf != result.ManifestBundleFile)
            {
                throw new InvalidOperationException("Build result workspace identity mismatch.");
            }

            var record = workspace.Read();
            if (record == null || !record.Complete)
            {
                throw new InvalidOperationException("Workspace is not complete.");
            }

            var locked = workspace.AcquireLock(ref record);
            try
            {
                if (!record.Complete || record.Fingerprint != result.InputFingerprint ||
                    !result.Files.Select(x => x.Name).SequenceEqual(workspace.ExpectedFiles()) ||
                    !record.Files.Select(x => x.Name + ":" + x.Length + ":" + x.Sha256)
                        .SequenceEqual(result.Files.Select(x => x.Name + ":" + x.Length + ":" + x.Sha256)))
                {
                    throw new InvalidOperationException("Workspace changed since the verified build result.");
                }

                foreach (var file in result.Files)
                {
                    var path = Path.Combine(workspace.Output, file.Name);
                    using var input = ResourceBuildWorkspace.OpenVerified(path, file.Length, file.Sha256);
                }
                if (!result.DeliveryFiles.Select(x => x.Name).SequenceEqual(workspace.ExpectedFiles()))
                {
                    throw new InvalidOperationException("Build delivery file closure mismatch.");
                }

                if (record is ResourceBuildWorkspace.EncryptedOwnership encrypted)
                {
                    if (result.DeliveryPath != Path.Combine(workspace.Root, encrypted.DeliveryDirectory ?? "") ||
                        !encrypted.DeliveryFiles.Select(x => x.Name + ":" + x.Length + ":" + x.Sha256)
                            .SequenceEqual(result.DeliveryFiles.Select(x => x.Name + ":" + x.Length + ":" + x.Sha256)))
                    {
                        throw new InvalidOperationException("Workspace delivery snapshot changed since the verified build result.");
                    }

                    foreach (var file in result.DeliveryFiles)
                    {
                        var raw = result.Files.Single(x => x.Name == file.Name);
                        if (file.Name.EndsWith(".manifest", StringComparison.Ordinal) && (file.Length != raw.Length || file.Sha256 != raw.Sha256))
                        {
                            throw new InvalidDataException("Plain manifest sidecar delivery must remain byte-identical to its raw build.");
                        }

                        var candidate = encrypted.Deliveries.SingleOrDefault(x => x.RelativePath == encrypted.DeliveryDirectory + "/" + file.Name);
                        if (candidate == null || !candidate.Complete || candidate.RawLength != raw.Length || candidate.RawSha256 != raw.Sha256 ||
                            candidate.Length != file.Length || candidate.Sha256 != file.Sha256)
                        {
                            throw new InvalidOperationException("Delivery candidate no longer matches its raw build identity.");
                        }

                        using var input = ResourceBuildWorkspace.OpenVerified(Path.Combine(result.DeliveryPath, file.Name), file.Length, file.Sha256);
                    }
                }
                else if (result.DeliveryPath != result.WorkspacePath || !result.DeliveryFiles.Select(x => x.Name + ":" + x.Length + ":" + x.Sha256)
                    .SequenceEqual(result.Files.Select(x => x.Name + ":" + x.Length + ":" + x.Sha256)))
                {
                    throw new InvalidOperationException("None delivery must be the unchanged raw workspace.");
                }

                return locked;
            }
            catch
            {
                locked.Dispose();
                throw;
            }
        }
    }
}
