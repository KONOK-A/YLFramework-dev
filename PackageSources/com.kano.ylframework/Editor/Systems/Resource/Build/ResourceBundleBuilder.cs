using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Resource
{
    /// <summary>已验证文件的只读字节身份副本，不持有文件句柄或冻结磁盘内容。</summary>
    public sealed class ResourceBuiltFile
    {
        /// <summary>构建工作区内的文件名。</summary>
        public string Name
        {
            get;
        }
        /// <summary>已验证的文件字节长度。</summary>
        public long Length
        {
            get;
        }
        /// <summary>已验证的全部文件字节 SHA-256。</summary>
        public string Sha256
        {
            get;
        }
        /// <summary>复制工作区摘要，避免公开可变的持久记录。</summary>
        internal ResourceBuiltFile(ResourceBuildWorkspace.FileDigest value)
        {
            Name = value.Name;
            Length = value.Length;
            Sha256 = value.Sha256;
        }
    }

    /// <summary>实际 Bundle 读回结果及其源布局统计，区分 UnityHash 与文件字节摘要。</summary>
    public sealed class ResourceBuiltBundle
    {
        /// <summary>经过读回验证的原生 Bundle 名称。</summary>
        public string Name
        {
            get;
        }
        /// <summary>根 Manifest 记录的 Unity 内容 hash，不替代文件 SHA-256。</summary>
        public string UnityHash
        {
            get;
        }
        /// <summary>构建图中最终分包的来源归类。</summary>
        public ResourceBundlePlacement Placement
        {
            get;
        }
        /// <summary>本包源文件字节之和，不含 meta，不是压缩文件大小。</summary>
        public long SourceBytes
        {
            get;
        }
        /// <summary>明确归属于本包的源文件数量。</summary>
        public int SourceFileCount
        {
            get;
        }
        /// <summary>已读回的真实Bundle文件字节，不含manifest旁车，不代表native内存。</summary>
        public long FileBytes
        {
            get;
        }
        /// <summary>读回 Manifest 后确认的直接依赖 Bundle 名称。</summary>
        public IReadOnlyList<string> Dependencies
        {
            get;
        }
        /// <summary>构建图中直接依赖本包的 Bundle 名称。</summary>
        public IReadOnlyList<string> Dependents
        {
            get;
        }
        /// <summary>合并源布局与实际 Manifest、文件大小读回结果。</summary>
        internal ResourceBuiltBundle(ResourceBuildBundle source, string hash, string[] dependencies, long fileBytes)
        {
            Name = source.Name;
            UnityHash = hash;
            Dependencies = Array.AsReadOnly(dependencies);
            Placement = source.Placement;
            SourceBytes = source.SourceBytes;
            SourceFileCount = source.AssetPaths.Length;
            Dependents = Array.AsReadOnly(source.Dependents);
            FileBytes = fileBytes;
        }
    }

    /// <summary>与同一兼容 Workspace 上次已验证记录比较，不把强制 native 重建等同于内容变化。</summary>
    public sealed class ResourceBundleChange
    {
        /// <summary>本次比较的 Bundle 文件名。</summary>
        public string Name
        {
            get;
        }
        /// <summary>当前原始 Bundle 文件的字节长度。</summary>
        public long FileBytes
        {
            get;
        }
        /// <summary>当前原始 Bundle 文件的字节摘要。</summary>
        public string Sha256
        {
            get;
        }
        /// <summary>兼容工作区先前记录中的摘要，没有对应项时为 null。</summary>
        public string PreviousSha256
        {
            get;
        }
        /// <summary>有已验证基线时比较长度与摘要；无完整基线时为 null，不能据重建推断变化。</summary>
        public bool? ContentChanged
        {
            get;
        }
        /// <summary>内容变化、未变化或未知的比较依据。</summary>
        public string Reason
        {
            get;
        }
        /// <summary>依据完整基线及同名文件的字节身份生成变化说明。</summary>
        internal ResourceBundleChange(ResourceBuiltFile file, ResourceBuildWorkspace.FileDigest previous, bool baselineAvailable)
        {
            Name = file.Name;
            FileBytes = file.Length;
            Sha256 = file.Sha256;
            PreviousSha256 = previous?.Sha256;
            ContentChanged = baselineAvailable ? previous == null || previous.Sha256 != file.Sha256 || previous.Length != file.Length : (bool?)null;
            Reason = !baselineAvailable ? "No verified baseline in this compatible Workspace; content change is unknown." :
                previous == null ? "New Bundle compared with the previous verified Workspace." :
                ContentChanged == true ? "SHA-256 or file length changed compared with the previous verified Workspace." :
                "SHA-256 and length unchanged; a safe native rebuild can still be required.";
        }
    }

    /// <summary>已在真实 Bundle 中按唯一 name+type 验证过的公开子资产；localId 只作稳定身份。</summary>
    public sealed class ResourceBuiltSubAsset
    {
        /// <summary>承载该子资产的主资产及逻辑地址。</summary>
        public ResourceBuildAsset MainAsset
        {
            get;
        }
        /// <summary>源文件内持久 localId，转为十进制字符串后用作 Catalog 子资产键。</summary>
        public long LocalId
        {
            get;
        }
        /// <summary>原生具名类型查找已确认唯一的子资产名称。</summary>
        public string Name
        {
            get;
        }
        /// <summary>原生加载时明确使用的编译类型。</summary>
        public Type AssetType
        {
            get;
        }
        /// <summary>复制公开子资产的持久身份和实际加载描述。</summary>
        internal ResourceBuiltSubAsset(ResourceBuildAsset main, long localId, string name, Type type)
        {
            MainAsset = main;
            LocalId = localId;
            Name = name;
            AssetType = type;
        }
    }

    /// <summary>已验证的 Editor 构建结果及真实工作量；Workspace 是可变缓存，不是已发布版本或持久化 Catalog。</summary>
    public sealed class ResourceBuildResult
    {
        /// <summary>本次验证所对应的显式资产与 Bundle 布局快照。</summary>
        public ResourceBuildMap Map
        {
            get; internal set;
        }
        /// <summary>可变原始缓存的叶目录；后续使用必须复验，不能当作已发布版本。</summary>
        public string WorkspacePath
        {
            get; internal set;
        }
        /// <summary>原始叶目录中的二进制根 Manifest Bundle 文件名。</summary>
        public string ManifestBundleFile
        {
            get; internal set;
        }
        /// <summary>本次源文件、meta、依赖和配置的综合输入指纹，已在构建后复验。</summary>
        public string InputFingerprint
        {
            get; internal set;
        }
        /// <summary>构建策略、目录定义、项目设置和编译输入组成的完整兼容分区指纹。</summary>
        public string BuildSettingsFingerprint
        {
            get; internal set;
        }
        /// <summary>本次明确构建并读回的平台名称。</summary>
        public string Platform
        {
            get; internal set;
        }
        /// <summary>本次构建使用的完整 Unity 版本。</summary>
        public string UnityCompatibility
        {
            get; internal set;
        }
        /// <summary>本次实际采用的 Bundle 压缩策略。</summary>
        public ResourceCompression Compression
        {
            get; internal set;
        }
        /// <summary>当前原始 Bundle、根 Manifest 及文本旁车的完整文件摘要，不含过时文件。</summary>
        public IReadOnlyList<ResourceBuiltFile> Files
        {
            get; internal set;
        }
        /// <summary>本次交付编码策略，不表示发布签名策略。</summary>
        public ResourceContentEncryption ContentEncryption
        {
            get; internal set;
        }
        /// <summary>非秘密的内容密钥标识；不加密时为 null，结果不保留密钥或 provider。</summary>
        public string ContentKeyId
        {
            get; internal set;
        }
        /// <summary>交付快照目录；None 时与 raw WorkspacePath 相同，不产生派生文件。</summary>
        public string DeliveryPath
        {
            get; internal set;
        }
        /// <summary>实际交付文件的完整字节摘要；加密时与原始文件摘要区分。</summary>
        public IReadOnlyList<ResourceBuiltFile> DeliveryFiles
        {
            get; internal set;
        }
        /// <summary>完整交付文件集的总字节数，包含文本旁车。</summary>
        public long DeliveryBytes
        {
            get; internal set;
        }
        /// <summary>本次新编码的二进制交付字节数，不计原样复制的文本旁车。</summary>
        public long EncodedDeliveryBytes
        {
            get; internal set;
        }
        /// <summary>本次复用的既有二进制交付字节数，不计文本旁车。</summary>
        public long ReusedDeliveryBytes
        {
            get; internal set;
        }
        /// <summary>交付候选验证、复用或编码的累计耗时，单位毫秒。</summary>
        public double ContentEncodingMilliseconds
        {
            get; internal set;
        }
        /// <summary>从原始产物读回并验证的 Bundle 集合。</summary>
        public IReadOnlyList<ResourceBuiltBundle> Bundles
        {
            get; internal set;
        }
        /// <summary>已按唯一名称与编译类型验证的公开子资产，不包含图集内部表示。</summary>
        public IReadOnlyList<ResourceBuiltSubAsset> SubAssets
        {
            get; internal set;
        }
        /// <summary>是否因完整输入与产物均匹配而跳过 Unity 构建；仍执行原生读回验证。</summary>
        public bool IsCacheHit
        {
            get; internal set;
        }
        /// <summary>本次是否实际调用过 Unity 构建管线。</summary>
        public bool WasUnityBuildInvoked
        {
            get; internal set;
        }
        /// <summary>是否因显式请求、产物不完整或未跟踪回调而使用原生强制重建选项。</summary>
        public bool ForceRebuild
        {
            get; internal set;
        }
        /// <summary>无法冻结完整输入的 Editor 回调；有此类回调时禁用整体跳构建并强制刷新 native 产物。</summary>
        public IReadOnlyList<string> CacheReuseBlockedBy
        {
            get; internal set;
        }
        /// <summary>构建前准备、输入及缓存检查的耗时，单位毫秒。</summary>
        public double PreflightMilliseconds
        {
            get; internal set;
        }
        /// <summary>本次构建分支的耗时，单位毫秒，包含工作区尝试登记等开销。</summary>
        public double BuildMilliseconds
        {
            get; internal set;
        }
        /// <summary>构建分支后读回、后验、交付处理及封口的耗时，单位毫秒。</summary>
        public double ValidationMilliseconds
        {
            get; internal set;
        }
        /// <summary>本次成功构建流程记录的总耗时，单位毫秒，不包含后续发布。</summary>
        public double ElapsedMilliseconds
        {
            get; internal set;
        }
        /// <summary>前置准备与分包计算的累计耗时，含后验重算，单位毫秒。</summary>
        public double MapMilliseconds
        {
            get; internal set;
        }
        /// <summary>Settings 和源指纹收集/哈希的实际总耗时，含构建后复验；不是纯磁盘 Hash 算法基准。</summary>
        public double InputFingerprintMilliseconds
        {
            get; internal set;
        }
        /// <summary>仅实际同步构建管线调用的耗时，单位毫秒。</summary>
        public double BuildPipelineMilliseconds
        {
            get; internal set;
        }
        /// <summary>原始产物完整性哈希的累计耗时，单位毫秒，不含交付候选的另行校验。</summary>
        public double ArtifactHashMilliseconds
        {
            get; internal set;
        }
        /// <summary>原始 Bundle 原生读回及子资产验证的耗时，单位毫秒。</summary>
        public double ReadbackMilliseconds
        {
            get; internal set;
        }
        /// <summary>普通及场景 Bundle 的原始文件字节总量，不含根 Manifest 和文本旁车。</summary>
        public long BundleFileBytes
        {
            get; internal set;
        }
        /// <summary>与兼容工作区先前完整基线比较的 Bundle 字节变化，不等同于原生重建情况。</summary>
        public IReadOnlyList<ResourceBundleChange> BundleChanges { get; internal set; } = Array.Empty<ResourceBundleChange>();
        /// <summary>本次重建或整体跳过构建的实际判断依据。</summary>
        public IReadOnlyList<string> ExecutionReasons { get; internal set; } = Array.Empty<string>();
        /// <summary>本次冻结输入前执行的自动 Atlas 计划；为空表示项目未配置自动图集。</summary>
        public ResourceAtlasPlan AtlasGeneration
        {
            get; internal set;
        }
        /// <summary>输入文件与设置哈希累计读取字节，包含后验重复读取，不是唯一源文件体积。</summary>
        public long SourceHashBytes
        {
            get; internal set;
        }
        /// <summary>原始产物哈希累计读取字节，不计交付编码中的独立校验。</summary>
        public long ArtifactHashBytes
        {
            get; internal set;
        }
        /// <summary>构建不复制发布快照，此统计固定为零。</summary>
        public long CopiedBytes => 0;
        /// <summary>已登记但不在本次预期原始文件集中的保留项数，不表示文件实际存在或已被删除。</summary>
        public int RetainedObsoleteFiles
        {
            get; internal set;
        }
    }

    /// <summary>主线程显式 BuildMap 构建入口。压缩白名单+StrictMode，仅写独立 owned Workspace，不发布或写入 StreamingAssets。</summary>
    [InitializeOnLoad]
    public static class ResourceBundleBuilder
    {
        private static readonly int _mainThread;
        private static bool _isBuilding;

        /// <summary>在 Editor 初始化时记录允许调用构建的主线程身份。</summary>
        static ResourceBundleBuilder() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        /// <summary>将压缩白名单映射为 Unity 选项，并始终启用 StrictMode。</summary>
        private static BuildAssetBundleOptions Options(ResourceProject project) => BuildAssetBundleOptions.StrictMode | (project.compression switch
        {
            ResourceCompression.Lz4 => BuildAssetBundleOptions.ChunkBasedCompression,
            ResourceCompression.Lzma => BuildAssetBundleOptions.None,
            ResourceCompression.Uncompressed => BuildAssetBundleOptions.UncompressedAssetBundle,
            _ => throw new ArgumentOutOfRangeException(nameof(project.compression), "Unsupported resource compression choice.")
        });

        /// <summary>预览兼容分区路径，不创建或认领目录；目标必须已是当前活动平台，避免隐式改动项目设置。</summary>
        public static string GetWorkspacePath(ResourceProject project, BuildTarget target)
        {
            ValidateEditor(target);
            var map = ResourceBuildMap.Create(project);
            long bytes = 0;
            return new ResourceBuildWorkspace(map, Settings(map, project, target, ref bytes), target.ToString(), project.contentEncryption, project.contentKeyId).Output;
        }

        /// <summary>只读解析预期工作区并检查已有归属与内容，不创建目录或获取锁。</summary>
        internal static string PreflightWorkspace(ResourceProject project, BuildTarget target)
        {
            var map = ResourceBuildMap.Create(project);
            long bytes = 0;
            var workspace = new ResourceBuildWorkspace(map, Settings(map, project, target, ref bytes), target.ToString(), project.contentEncryption, project.contentKeyId);
            _ = workspace.Read();
            return workspace.Output;
        }

        /// <summary>取消在 native 构建前后及验证检查点生效；Unity 同步 BuildPipeline 本身不可由 token 中断。</summary>
        public static ResourceBuildResult Build(ResourceProject project, BuildTarget target, bool forceRebuild = false,
            CancellationToken cancellation = default) => Execute(project, target, forceRebuild, cancellation, BuildPipeline.BuildAssetBundles);

        /// <summary>显式内容密钥来源；provider 仍归应用，结果不保存 provider 或密钥。</summary>
        public static ResourceBuildResult BuildWithContentKey(ResourceProject project, BuildTarget target, IResourceContentKeyProvider provider,
            bool forceRebuild = false, CancellationToken cancellation = default) =>
            ExecuteWithContentKey(project, target, forceRebuild, cancellation, BuildPipeline.BuildAssetBundles, provider);

        /// <summary>以明确构建管线执行不附带内容密钥来源的构建。</summary>
        internal static ResourceBuildResult Execute(ResourceProject project, BuildTarget target, bool forceRebuild,
            CancellationToken cancellation, Func<string, AssetBundleBuild[], BuildAssetBundleOptions, BuildTarget, AssetBundleManifest> pipeline) =>
            ExecuteWithContentKey(project, target, forceRebuild, cancellation, pipeline, null);

        /// <summary>
        /// 准备图集后收集输入指纹，在自有工作区内构建、读回并复验，再标记缓存完整。
        /// 持有锁不使源文件或结果路径不可变；失败保留已登记的部分产物，密钥副本在离开入口时清零。
        /// </summary>
        internal static ResourceBuildResult ExecuteWithContentKey(ResourceProject project, BuildTarget target, bool forceRebuild,
            CancellationToken cancellation, Func<string, AssetBundleBuild[], BuildAssetBundleOptions, BuildTarget, AssetBundleManifest> pipeline,
            IResourceContentKeyProvider provider)
        {
            ResourceBuildProgress.RejectReentry();
            if (_isBuilding)
            {
                throw new InvalidOperationException("An AssetBundle build is already running; reentrant builds are not allowed.");
            }

            ValidateEditor(target);
            cancellation.ThrowIfCancellationRequested();
            if (pipeline == null)
            {
                throw new ArgumentNullException(nameof(pipeline));
            }

            _isBuilding = true;
            AssetBundleManifest generated = null;
            byte[] contentKey = null;
            try
            {
                var elapsed = Stopwatch.StartNew();
                var watch = Stopwatch.StartNew();
                if (!project)
                {
                    throw new ArgumentNullException(nameof(project));
                }

                var options = Options(project);
                ValidateContentPolicy(project.contentEncryption, project.contentKeyId, target.ToString());
                var encryption = project.contentEncryption;
                var keyId = encryption == ResourceContentEncryption.None ? null : project.contentKeyId;
                var keyProject = project.projectId;
                var directoryIdentity = ResourceProjectPaths.SemanticIdentity(project);
                void RequireDirectoryIdentity()
                {
                    if (ResourceProjectPaths.SemanticIdentity(project) != directoryIdentity)
                    {
                        throw new InvalidOperationException("Resource source directories/Atlas configuration changed during key acquisition/preparation.");
                    }
                }
                if (encryption != ResourceContentEncryption.None)
                {
                    contentKey = CopyContentKey(provider, keyProject, keyId);
                }

                RequireDirectoryIdentity();
                RejectLoadedBundles();
                ResourceBuildProgress.Notify(ResourceBuildPhase.AtlasPreparationStarted, project, target);
                RequireDirectoryIdentity();
                var atlasGeneration = ResourceAtlasGenerator.Generate(project, cancellation);
                ResourceBuildProgress.Notify(ResourceBuildPhase.MapStarted, project, target);
                var map = ResourceBuildMap.Create(project);
                RequireDirectoryIdentity();
                if (map.ProjectId != keyProject || project.contentEncryption != encryption ||
                    encryption != ResourceContentEncryption.None && project.contentKeyId != keyId)
                {
                    throw new InvalidOperationException("Resource content identity changed during key acquisition/preparation.");
                }

                var mapMilliseconds = watch.Elapsed.TotalMilliseconds;
                long sourceBytes = 0;
                var inputWatch = Stopwatch.StartNew();
                ResourceBuildProgress.Notify(ResourceBuildPhase.FingerprintStarted, map.ProjectGuid, map.ProjectId, target.ToString());
                RequireDirectoryIdentity();
                var settings = Settings(map, project, target, ref sourceBytes);
                inputWatch.Stop();
                var callbacks = UntrackedCallbacks();
                var workspace = new ResourceBuildWorkspace(map, settings, target.ToString(), encryption, keyId);
                var record = workspace.Read();
                RejectLoadedBundles();
                inputWatch.Start();
                var fingerprint = Fingerprint(map, settings, ref sourceBytes);
                RequireDirectoryIdentity();
                inputWatch.Stop();
                cancellation.ThrowIfCancellationRequested();
                using var locked = workspace.AcquireLock(ref record);
                var result = new ResourceBuildResult
                {
                    Map = map,
                    WorkspacePath = workspace.Output,
                    InputFingerprint = fingerprint,
                    BuildSettingsFingerprint = settings,
                    Platform = target.ToString(),
                    UnityCompatibility = Application.unityVersion,
                    Compression = project.compression,
                    ContentEncryption = encryption,
                    ContentKeyId = keyId,
                    CacheReuseBlockedBy = Array.AsReadOnly(callbacks),
                    AtlasGeneration = atlasGeneration
                };
                var expected = workspace.ExpectedFiles();
                var baselineAvailable = record.Complete;
                var previous = record.Files.ToDictionary(x => x.Name, StringComparer.Ordinal);
                long artifactBytes = 0;
                var hashWatch = new Stopwatch();
                ResourceBuildWorkspace.FileDigest HashArtifact(string path)
                {
                    hashWatch.Start();
                    try
                    {
                        return HashFile(path, ref artifactBytes);
                    }
                    finally
                    {
                        hashWatch.Stop();
                    }
                }
                var integrity = record.Complete && record.Files.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected);
                if (integrity)
                {
                    foreach (var file in record.Files)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var path = Path.Combine(workspace.Output, file.Name);
                        if (!File.Exists(path))
                        {
                            integrity = false;
                            break;
                        }
                        var digest = HashArtifact(path);
                        if (digest.Length != file.Length || digest.Sha256 != file.Sha256)
                        {
                            integrity = false;
                            break;
                        }
                    }
                }
                result.IsCacheHit = CanSkipBuild(forceRebuild, integrity, record.Fingerprint == fingerprint, callbacks);
                result.ForceRebuild = forceRebuild || !integrity || callbacks.Length != 0;
                var reasons = new List<string>();
                if (forceRebuild)
                {
                    reasons.Add("User explicitly requested a forced rebuild.");
                }

                if (!integrity)
                {
                    reasons.Add("Workspace is new, incomplete, or its artifact integrity does not match the verified record.");
                }

                if (record.Fingerprint != fingerprint)
                {
                    reasons.Add("Complete input fingerprint differs from the previous compatible Workspace record.");
                }

                if (callbacks.Length != 0)
                {
                    reasons.Add("Untracked build callbacks require safe forced native rebuilding (" + callbacks.Length + ").");
                }

                if (result.IsCacheHit)
                {
                    reasons.Add("Complete input and every registered artifact digest verified; no untracked callbacks.");
                }

                result.ExecutionReasons = reasons.AsReadOnly();
                result.PreflightMilliseconds = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                if (!result.IsCacheHit)
                {
                    workspace.BeginAttempt(record);
                    cancellation.ThrowIfCancellationRequested();
                    result.WasUnityBuildInvoked = true;
                    ResourceBuildProgress.Notify(ResourceBuildPhase.NativeBuildStarted, map.ProjectGuid, map.ProjectId, target.ToString());
                    RequireDirectoryIdentity();
                    var pipelineWatch = Stopwatch.StartNew();
                    generated = pipeline(workspace.Output, map.UnityBuilds(), options |
                        (result.ForceRebuild ? BuildAssetBundleOptions.ForceRebuildAssetBundle : BuildAssetBundleOptions.None), target);
                    result.BuildPipelineMilliseconds = pipelineWatch.Elapsed.TotalMilliseconds;
                    cancellation.ThrowIfCancellationRequested();
                    if (!generated)
                    {
                        throw new InvalidOperationException("BuildPipeline returned no AssetBundle Manifest.");
                    }

                    if (!generated.GetAllAssetBundles().OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(map.Bundles.Select(x => x.Name)))
                    {
                        throw new InvalidOperationException("BuildPipeline Manifest does not match the explicit BuildMap.");
                    }
                }
                result.BuildMilliseconds = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                workspace.ValidateContents(record);
                ResourceBuildProgress.Notify(ResourceBuildPhase.ReadbackStarted, map.ProjectGuid, map.ProjectId, target.ToString());
                var readback = Stopwatch.StartNew();
                var built = ReadBack(map, workspace, cancellation, out var subAssets);
                result.ReadbackMilliseconds = readback.Elapsed.TotalMilliseconds;
                result.ManifestBundleFile = workspace.Leaf;
                result.Bundles = built.AsReadOnly();
                result.SubAssets = subAssets.AsReadOnly();
                ResourceBuildWorkspace.FileDigest[] files;
                if (result.IsCacheHit)
                {
                    files = record.Files;
                }
                else
                {
                    files = expected.Select(name =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    var digest = HashArtifact(Path.Combine(workspace.Output, name));
                    digest.Name = name;
                    return digest;
                }).ToArray();
                }
                // BuildPipeline 会卸载未使用的 SO，局部变量不会保证 Unity 对象存活；按持久 GUID 重取配置后复验。
                var currentProject = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(map.ProjectGuid));
                if (!currentProject)
                {
                    throw new InvalidOperationException("Resource project definition was removed during the build.");
                }

                var mapWatch = Stopwatch.StartNew();
                var afterMap = ResourceBuildMap.Create(currentProject);
                mapMilliseconds += mapWatch.Elapsed.TotalMilliseconds;
                inputWatch.Start();
                var afterSettings = Settings(afterMap, currentProject, target, ref sourceBytes);
                if (afterSettings != settings || Fingerprint(afterMap, afterSettings, ref sourceBytes) != fingerprint)
                {
                    throw new InvalidOperationException("Resource sources/configuration changed during the build; workspace remains unverified.");
                }

                inputWatch.Stop();
                cancellation.ThrowIfCancellationRequested();
                result.Files = files.Select(x => new ResourceBuiltFile(x)).ToList().AsReadOnly();
                if (encryption == ResourceContentEncryption.None)
                {
                    result.DeliveryPath = result.WorkspacePath;
                    result.DeliveryFiles = result.Files;
                }
                else
                {
                    ResourceBuildProgress.Notify(ResourceBuildPhase.ContentEncodingStarted, map.ProjectGuid, map.ProjectId, target.ToString());
                    var encoding = Stopwatch.StartNew();
                    if (record.Complete)
                    {
                        record.Complete = false;
                        workspace.Write(record);
                    }
                    BuildDelivery(result, workspace, (ResourceBuildWorkspace.EncryptedOwnership)record, files, contentKey, cancellation);
                    result.ContentEncodingMilliseconds = encoding.Elapsed.TotalMilliseconds;
                    // 编码可能耗时，封口前再次覆盖这段时间的源/配置变化，而不是延用raw读回前的结论。
                    mapWatch.Restart();
                    afterMap = ResourceBuildMap.Create(currentProject);
                    mapMilliseconds += mapWatch.Elapsed.TotalMilliseconds;
                    inputWatch.Start();
                    afterSettings = Settings(afterMap, currentProject, target, ref sourceBytes);
                    if (afterSettings != settings || Fingerprint(afterMap, afterSettings, ref sourceBytes) != fingerprint)
                    {
                        throw new InvalidOperationException("Resource sources/configuration changed during content encoding; workspace remains unverified.");
                    }

                    inputWatch.Stop();
                }
                result.DeliveryBytes = result.DeliveryFiles.Sum(x => x.Length);
                cancellation.ThrowIfCancellationRequested();
                if (!result.IsCacheHit || encryption != ResourceContentEncryption.None)
                {
                    record.Files = files;
                    record.Fingerprint = fingerprint;
                    record.Complete = true;
                    workspace.Write(record);
                }
                var bundleNames = new HashSet<string>(built.Select(x => x.Name), StringComparer.Ordinal);
                result.BundleFileBytes = result.Files.Where(x => bundleNames.Contains(x.Name)).Sum(x => x.Length);
                result.BundleChanges = result.Files.Where(x => bundleNames.Contains(x.Name)).Select(x =>
                    new ResourceBundleChange(x, previous.TryGetValue(x.Name, out var prior) ? prior : null, baselineAvailable)).ToList().AsReadOnly();
                result.RetainedObsoleteFiles = record.KnownFiles.Except(expected, StringComparer.Ordinal).Count();
                result.SourceHashBytes = sourceBytes;
                result.ArtifactHashBytes = artifactBytes;
                result.ValidationMilliseconds = watch.Elapsed.TotalMilliseconds;
                result.MapMilliseconds = mapMilliseconds;
                result.InputFingerprintMilliseconds = inputWatch.Elapsed.TotalMilliseconds;
                result.ArtifactHashMilliseconds = hashWatch.Elapsed.TotalMilliseconds;
                result.ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds;
                ResourceBuildProgress.Notify(ResourceBuildPhase.WorkspaceVerified, map.ProjectGuid, map.ProjectId, target.ToString());
                return result;
            }
            finally
            {
                if (contentKey != null)
                {
                    Array.Clear(contentKey, 0, contentKey.Length);
                }

                if (generated)
                {
                    Object.DestroyImmediate(generated);
                }

                _isBuilding = false;
            }
        }

        /// <summary>取得并验证独立内容密钥副本，调用方负责在使用后清零，不接管 provider。</summary>
        internal static byte[] CopyContentKey(IResourceContentKeyProvider provider, string projectId, string keyId) =>
            ResourceContentValidation.CopyKey(provider == null ? null : new ResourceDecryptionOptions(provider), projectId, keyId);

        /// <summary>校验交付模式、非秘密 KeyId 及实际支持的平台，不读取密钥。</summary>
        internal static void ValidateContentPolicy(ResourceContentEncryption mode, string keyId, string platform)
        {
            if (mode == ResourceContentEncryption.None)
            {
                return;
            }

            if (mode != ResourceContentEncryption.Aes256GcmHkdfSegmentedV1)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            ResourceBundleEnvelope.ValidateKeyId(keyId);
            if (!ResourceBundleEnvelope.IsSupported || platform != "StandaloneWindows64")
            {
                throw new PlatformNotSupportedException("Encrypted Resource content requires Windows x64 Editor/Mono.");
            }
        }

        /// <summary>以项目、平台、Unity 版本和 Bundle 或根 Manifest 身份绑定内容封装上下文。</summary>
        internal static ResourceBundleContext ContentContext(ResourceBuildResult result, string name) => new(result.Map.ProjectId,
            result.Platform, result.UnityCompatibility, name == result.ManifestBundleFile ? "Manifest" : "Bundle",
            name == result.ManifestBundleFile ? "manifest" : name);

        /// <summary>核对旁车原字节，或验证二进制封装身份及全部明文字节与原始构建一致，不接管输入流。</summary>
        internal static void ValidateDelivery(Stream input, ResourceBuildResult result, string name, byte[] key,
            long rawLength, string rawSha256, CancellationToken token)
        {
            if (name.EndsWith(".manifest", StringComparison.Ordinal))
            {
                input.Position = 0;
                using var hash = SHA256.Create();
                if (input.Length != rawLength || ResourceBundleEnvelope.Digest(hash.ComputeHash(input)) != rawSha256)
                {
                    throw new InvalidDataException("Support delivery differs from the original raw build.");
                }

                input.Position = 0;
                token.ThrowIfCancellationRequested();
                return;
            }
            var context = ContentContext(result, name);
            var header = ResourceBundleEnvelope.Inspect(input, context);
            if (result.ContentEncryption != ResourceContentEncryption.None && (header == null || header.KeyId != result.ContentKeyId) ||
                result.ContentEncryption == ResourceContentEncryption.None && header != null)
            {
                throw new InvalidDataException("Delivery content encoding/key identity does not match the verified build.");
            }

            ResourceBundleEnvelope.ValidateAll(input, context, key, token, rawSha256, rawLength);
        }

        /// <summary>复验可复用候选，必要时在新登记目录生成交付集，并逐文件保存完成记录。</summary>
        private static void BuildDelivery(ResourceBuildResult result, ResourceBuildWorkspace workspace,
            ResourceBuildWorkspace.EncryptedOwnership record, ResourceBuildWorkspace.FileDigest[] files, byte[] key, CancellationToken token)
        {
            var candidates = new Dictionary<string, ResourceBuildWorkspace.DeliveryRecord>(StringComparer.Ordinal);
            foreach (var raw in files)
            {
                token.ThrowIfCancellationRequested();
                var candidate = record.Deliveries.FirstOrDefault(x => x.Complete && x.Name == raw.Name &&
                    x.RawLength == raw.Length && x.RawSha256 == raw.Sha256 && File.Exists(Path.Combine(workspace.Root, x.RelativePath)));
                if (candidate == null)
                {
                    continue;
                }

                using var input = ResourceBuildWorkspace.OpenVerified(Path.Combine(workspace.Root, candidate.RelativePath), candidate.Length, candidate.Sha256, token);
                ValidateDelivery(input, result, raw.Name, key, raw.Length, raw.Sha256, token);
                candidates.Add(raw.Name, candidate);
            }
            var directories = candidates.Values.Select(x => x.RelativePath.Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();
            ResourceBuildWorkspace.FileDigest[] delivery;
            if (candidates.Count == files.Length && directories.Length == 1)
            {
                record.DeliveryDirectory = directories[0];
                delivery = files.Select(x => candidates[x.Name].Digest()).ToArray();
                result.ReusedDeliveryBytes = delivery.Where(x => !x.Name.EndsWith(".manifest", StringComparison.Ordinal)).Sum(x => x.Length);
            }
            else
            {
                var directory = workspace.RegisterDelivery(record, files, out var pending);
                foreach (var file in pending)
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(workspace.Root, file.RelativePath);
                    ResourceBuildOperation.RejectLinks(path);
                    var reused = candidates.TryGetValue(file.Name, out var candidate);
                    using (var source = reused
                        ? ResourceBuildWorkspace.OpenVerified(Path.Combine(workspace.Root, candidate.RelativePath), candidate.Length, candidate.Sha256, token)
                        : ResourceBuildWorkspace.OpenVerified(Path.Combine(workspace.Output, file.Name), file.RawLength, file.RawSha256, token))
                    using (var output = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (reused || file.Name.EndsWith(".manifest", StringComparison.Ordinal))
                        {
                            CopyContent(source, output, token);
                        }
                        else
                        {
                            ResourceBundleEnvelope.Encrypt(source, output, ContentContext(result, file.Name), result.ContentKeyId, key, token);
                        }

                        output.Flush(true);
                    }
                    long hashed = 0;
                    var digest = HashFile(path, ref hashed);
                    using (var input = ResourceBuildWorkspace.OpenVerified(path, digest.Length, digest.Sha256, token))
                    {
                        ValidateDelivery(input, result, file.Name, key, file.RawLength, file.RawSha256, token);
                    }

                    file.Length = digest.Length;
                    file.Sha256 = digest.Sha256;
                    file.Complete = true;
                    if (!file.Name.EndsWith(".manifest", StringComparison.Ordinal))
                    {
                        if (reused)
                        {
                            result.ReusedDeliveryBytes += file.Length;
                        }
                        else
                        {
                            result.EncodedDeliveryBytes += file.Length;
                        }
                    }
                    workspace.Write(record);
                }
                record.DeliveryDirectory = directory;
                delivery = pending.Select(x => x.Digest()).ToArray();
            }
            record.DeliveryFiles = delivery;
            result.DeliveryPath = Path.Combine(workspace.Root, record.DeliveryDirectory);
            result.DeliveryFiles = delivery.Select(x => new ResourceBuiltFile(x)).ToList().AsReadOnly();
        }

        /// <summary>按取消检查点复制流字节，不关闭流或刷新输出，由调用方管理其生命周期。</summary>
        internal static void CopyContent(Stream source, Stream output, CancellationToken token)
        {
            var buffer = new byte[32768];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
        }

        /// <summary>仅在无强制请求、完整输入和产物匹配且无未跟踪回调时允许整体跳过构建。</summary>
        internal static bool CanSkipBuild(bool force, bool integrity, bool matchingInput, string[] callbacks) =>
            !force && integrity && matchingInput && callbacks.Length == 0;

        /// <summary>列出可能读取任意外部状态的非 Unity 内置构建回调，用于阻止不可靠的缓存跳过。</summary>
        private static string[] UntrackedCallbacks()
        {
            var engine = typeof(EditorApplication).Assembly;
            var contracts = new[] { typeof(IPreprocessShaders), typeof(IPreprocessComputeShaders), typeof(IProcessSceneWithReport),
                typeof(IProcessSceneWithReport).Assembly.GetType("UnityEditor.Build.IProcessScene") };
            // Unity 自带版本固定的基础流程仍由原生依赖系统负责；包/用户回调可能读取任意配置、EditorPrefs 或外部状态。
            // 8.2a 不猜这些输入，也不提供不真实的“可缓存”开关；有此类回调就每次刷新，未来再接明确定义的输入协议。
            return contracts.Where(x => x != null).SelectMany(x => TypeCache.GetTypesDerivedFrom(x).AsEnumerable())
                .Where(x => !x.IsAbstract && !x.IsInterface && x.Assembly != engine).Select(x => x.FullName)
                .Concat(TypeCache.GetMethodsWithAttribute<UnityEditor.Callbacks.PostProcessSceneAttribute>()
                    .Where(x => x.DeclaringType != null && x.DeclaringType.Assembly != engine)
                    .Select(x => x.DeclaringType.FullName + "." + x.Name))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        /// <summary>要求稳定主线程 Editor、已激活且与主机匹配的平台及无脏场景，不隐式保存或切平台。</summary>
        internal static void ValidateEditor(BuildTarget target)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Resource builds require a settled Editor on its main thread, outside Play mode.");
            }

            if (target != EditorUserBuildSettings.activeBuildTarget ||
                !BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
            {
                throw new InvalidOperationException("The build target must already be active and installed; implicit platform switching is not allowed.");
            }

            var hostTarget = Application.platform switch
            {
                RuntimePlatform.WindowsEditor => BuildTarget.StandaloneWindows64,
                RuntimePlatform.OSXEditor => BuildTarget.StandaloneOSX,
                RuntimePlatform.LinuxEditor => BuildTarget.StandaloneLinux64,
                _ => BuildTarget.NoTarget
            };
            if (target != hostTarget)
            {
                throw new InvalidOperationException("Native readback requires the active desktop target matching this Editor host.");
            }

            for (var i = 0; i < UnityEditor.SceneManagement.EditorSceneManager.sceneCount; i++)
            {
                if (UnityEditor.SceneManagement.EditorSceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("Dirty scenes must not be saved or replaced by the resource builder.");
                }
            }
        }

        /// <summary>拒绝在存在外部已加载 Bundle 时启动可能卸载它们的原生流程。</summary>
        internal static void RejectLoadedBundles()
        {
            // 实测 native build 会卸载其他项目已经加载的 Bundle；只防同 namespace 不足以保护外部所有权。
            if (AssetBundle.GetAllLoadedAssetBundles().Any(x => x))
            {
                throw new InvalidOperationException("AssetBundles are already loaded; native building could unload externally owned resources.");
            }
        }

        /// <summary>按依赖顺序加载本次原始产物并核对布局、类型与子资产，最终只卸载本方法取得的 Bundle。</summary>
        private static List<ResourceBuiltBundle> ReadBack(ResourceBuildMap map, ResourceBuildWorkspace workspace,
            CancellationToken cancellation, out List<ResourceBuiltSubAsset> subAssets)
        {
            subAssets = DescribeSubAssets(map);
            var descriptions = subAssets;
            var loaded = new List<AssetBundle>();
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var root = AssetBundle.LoadFromFile(Path.Combine(workspace.Output, workspace.Leaf));
                if (!root)
                {
                    throw new InvalidOperationException("Cannot read the generated Manifest Bundle.");
                }

                loaded.Add(root);
                if (!root.GetAllAssetNames().SequenceEqual(new[] { "assetbundlemanifest" }))
                {
                    throw new InvalidOperationException("Unexpected contents in the root Manifest Bundle.");
                }

                var manifest = root.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                if (!manifest || !manifest.GetAllAssetBundles().OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(map.Bundles.Select(x => x.Name)))
                {
                    throw new InvalidOperationException("Persisted Manifest Bundle does not match the BuildMap.");
                }

                var result = new List<ResourceBuiltBundle>();
                var done = new HashSet<string>(StringComparer.Ordinal);
                while (done.Count < map.Bundles.Count)
                {
                    var batch = map.Bundles.Where(x => !done.Contains(x.Name) && x.Dependencies.All(done.Contains)).ToArray();
                    if (batch.Length == 0)
                    {
                        throw new InvalidOperationException("Bundle dependency cycle during native readback.");
                    }

                    foreach (var entry in batch)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var dependencies = manifest.GetDirectDependencies(entry.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                        if (!dependencies.SequenceEqual(entry.Dependencies))
                        {
                            throw new InvalidOperationException("Manifest dependency mismatch: " + entry.Name + " => " + string.Join(", ", dependencies));
                        }

                        var bundle = AssetBundle.LoadFromFile(Path.Combine(workspace.Output, entry.Name));
                        if (!bundle)
                        {
                            throw new InvalidOperationException("Cannot read generated Bundle: " + entry.Name);
                        }

                        loaded.Add(bundle);
                        if (bundle.name != entry.Name)
                        {
                            throw new InvalidOperationException("Bundle native identity mismatch: " + bundle.name);
                        }

                        var actual = entry.IsScene ? bundle.GetAllScenePaths() : bundle.GetAllAssetNames();
                        // GetAllScenePaths 保留源路径大小写，GetAllAssetNames 则小写；BuildMap 已拒绝 case-folded 路径冲突。
                        if (!actual.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(entry.AssetPaths.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)))
                        {
                            throw new InvalidOperationException("Bundle asset/Scene contents differ from explicit BuildMap: " + entry.Name + " => " + string.Join(", ", actual));
                        }

                        foreach (var asset in map.Assets.Where(x => x.BundleName == entry.Name && !x.IsScene))
                        {
                            var value = bundle.LoadAsset(asset.SourcePath, asset.AssetType);
                            if (!value || !asset.AssetType.IsInstanceOfType(value))
                            {
                                throw new InvalidOperationException("Bundle resource identity/type readback failed: " + asset.Address);
                            }

                            if (value is UnityEngine.U2D.SpriteAtlas atlas)
                            {
                                ResourceAtlasRules.ValidateReadback(atlas, asset.SourcePath);
                            }

                            foreach (var sub in descriptions.Where(x => x.MainAsset == asset))
                            {
                                var type = sub.AssetType;
                                var matches = bundle.LoadAssetWithSubAssets(asset.SourcePath, type).Where(x => x && x.name == sub.Name && type.IsInstanceOfType(x)).ToArray();
                                if (matches.Length != 1)
                                {
                                    throw new InvalidOperationException("Subasset name/type cannot uniquely locate native content: " + asset.Address + "#" + sub.LocalId.ToString(CultureInfo.InvariantCulture));
                                }
                            }
                        }
                        result.Add(new ResourceBuiltBundle(entry, manifest.GetAssetBundleHash(entry.Name).ToString(), dependencies,
                            new FileInfo(Path.Combine(workspace.Output, entry.Name)).Length));
                        done.Add(entry.Name);
                    }
                }
                return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
            }
            finally
            {
                for (var i = loaded.Count - 1; i >= 0; i--)
                {
                    if (loaded[i])
                    {
                        loaded[i].Unload(true);
                    }
                }
            }
        }

        // 构建读回与零打包模拟共用身份/歧义规则，只公开 Project view 可见、可运行时消费的子资产。
        internal static List<ResourceBuiltSubAsset> DescribeSubAssets(ResourceBuildMap map)
        {
            var result = new List<ResourceBuiltSubAsset>();
            foreach (var asset in map.Assets.Where(x => !x.IsScene))
            {
                // Atlas打包产生的内部Texture/表示不是稳定公开子资产；消费端持Atlas租约并显式GetSprite。
                if (asset.AssetType == typeof(UnityEngine.U2D.SpriteAtlas))
                {
                    continue;
                }
                // native 的 typed 查询包含 main 和隐藏对象，并按可赋值关系匹配；localId 不能掩盖其名称歧义。
                var candidates = AssetDatabase.LoadAllAssetsAtPath(asset.SourcePath).Where(x => x)
                    .Select(x => (x.name, Type: ResourceBuildMap.BuildType(x.GetType()))).Where(x => x.Type != null)
                    .GroupBy(x => x.name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(v => v.Type).ToArray(), StringComparer.Ordinal);
                var keys = new HashSet<(string, Type)>();
                var ids = new HashSet<long> { asset.LocalId };
                foreach (var source in AssetDatabase.LoadAllAssetRepresentationsAtPath(asset.SourcePath))
                {
                    if (!source)
                    {
                        continue;
                    }

                    var type = ResourceBuildMap.BuildType(source.GetType());
                    if (type == null || type.Assembly.GetName().Name.StartsWith("UnityEditor", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!candidates.TryGetValue(source.name, out var matches) || matches.Count(type.IsAssignableFrom) != 1)
                    {
                        throw new InvalidOperationException("Subasset name/type cannot uniquely locate native content: " + asset.Address + "#" + source.name);
                    }

                    if (string.IsNullOrWhiteSpace(source.name) || !keys.Add((source.name, type)) ||
                        !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId) ||
                        guid != asset.Guid || localId == 0 || !ids.Add(localId))
                    {
                        throw new InvalidOperationException("Ambiguous subasset identity: " + asset.Address);
                    }

                    if (map.Assets.Count + result.Count >= 16384)
                    {
                        throw new InvalidOperationException("Catalog asset limit exceeded.");
                    }

                    result.Add(new ResourceBuiltSubAsset(asset, localId, source.name, type));
                }
            }
            return result;
        }

        /// <summary>汇总构建策略、目录定义、项目设置及实际编译输入，生成完整兼容分区指纹。</summary>
        private static string Settings(ResourceBuildMap map, ResourceProject project, BuildTarget target, ref long bytes)
        {
            ValidateContentPolicy(project.contentEncryption, project.contentKeyId, target.ToString());
            var data = new StringBuilder();
            Add(data, "ylr-build-workspace-v1");
            Add(data, Application.unityVersion);
            Add(data, target.ToString());
            Add(data, ((int)Options(project)).ToString());
            Add(data, map.ProjectGuid);
            Add(data, map.ProjectId);
            Add(data, ResourceProjectPaths.SemanticIdentity(project));
            foreach (var group in project.groups.OrderBy(x => x.id, StringComparer.Ordinal))
            {
                Add(data, group.id);
                Add(data, group.packing.ToString());
                Add(data, group.selections.Count.ToString());
                foreach (var selection in group.selections.OrderBy(x => x.guid, StringComparer.Ordinal))
                {
                    Add(data, selection.guid);
                    Add(data, selection.address ?? "");
                    Add(data, (selection.aliases?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
                    foreach (var alias in (selection.aliases ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        Add(data, alias);
                    }

                    Add(data, (selection.extensions?.Length ?? 0).ToString());
                    foreach (var extension in (selection.extensions ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        Add(data, extension);
                    }
                }
            }
            // 保守隔离所有实际设置与编译输入，包括 Editor 构建回调；不尝试靠有限 PlayerSettings 字段猜缓存兼容性。
            var settingsRoot = ResourceBuildMap.Absolute("ProjectSettings");
            ResourceBuildOperation.RejectLinks(settingsRoot);
            var assemblies = CompilationPipeline.GetAssemblies();
            foreach (var assembly in assemblies.OrderBy(x => x.name, StringComparer.Ordinal))
            {
                Add(data, assembly.name);
                Add(data, assembly.flags.ToString());
                foreach (var define in assembly.defines.OrderBy(x => x, StringComparer.Ordinal))
                {
                    Add(data, define);
                }

                foreach (var reference in assembly.assemblyReferences.OrderBy(x => x.name, StringComparer.Ordinal))
                {
                    Add(data, reference.name);
                }
            }
            var generated = new[] { "Library/ScriptAssemblies/", "Library/Bee/", "Temp/" }.Select(ResourceBuildMap.Absolute).ToArray();
            var files = Directory.GetFiles(settingsRoot, "*", SearchOption.AllDirectories)
                .Concat(new[] { ResourceBuildMap.Absolute("Packages/manifest.json"), ResourceBuildMap.Absolute("Packages/packages-lock.json") })
                .Concat(assemblies.SelectMany(x => x.sourceFiles))
                .Concat(assemblies.SelectMany(x => x.compiledAssemblyReferences).Where(x => !generated.Any(p => ResourceBuildMap.Absolute(x).StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                .Concat(assemblies.Select(x => CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(x.name)).Where(x => !string.IsNullOrEmpty(x)))
                .Concat(AssetDatabase.GetAllAssetPaths().Where(x => Path.GetExtension(x).Equals(".asmref", StringComparison.OrdinalIgnoreCase)));
            Add(data, InputFilesFingerprint(files, out var readBytes));
            bytes += readBytes;
            foreach (var callback in UntrackedCallbacks())
            {
                Add(data, callback);
            }

            if (project.contentEncryption != ResourceContentEncryption.None)
            {
                Add(data, "ylr-content-envelope-v1");
                Add(data, project.contentEncryption.ToString());
                Add(data, project.contentKeyId);
            }
            return HashText(data.ToString());
        }

        /// <summary>按稳定路径顺序哈希输入文件及现有 meta，并返回实际累计读取字节。</summary>
        internal static string InputFilesFingerprint(IEnumerable<string> files, out long bytes)
        {
            bytes = 0;
            var data = new StringBuilder();
            foreach (var path in files.Select(ResourceBuildMap.Absolute).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
            {
                ResourceBuildOperation.RejectLinks(path);
                Add(data, path);
                Add(data, HashFile(path, ref bytes).Sha256);
                if (!File.Exists(path + ".meta"))
                {
                    continue;
                }

                ResourceBuildOperation.RejectLinks(path + ".meta");
                Add(data, HashFile(path + ".meta", ref bytes).Sha256);
            }
            return HashText(data.ToString());
        }

        /// <summary>冻结布局、来源目录和源字节的综合身份，供构建前后及缓存复用时比较。</summary>
        private static string Fingerprint(ResourceBuildMap map, string settings, ref long bytes)
        {
            var data = new StringBuilder();
            Add(data, settings);
            foreach (var directory in map.SourceDirectories.OrderBy(x => x.Guid, StringComparer.Ordinal).ThenBy(x => x.AddressPrefix, StringComparer.Ordinal))
            {
                Add(data, directory.Guid);
                Add(data, directory.Path);
                Add(data, directory.AddressPrefix);
            }
            foreach (var asset in map.Assets)
            {
                Add(data, asset.AssetId);
                Add(data, asset.Address.ToString());
                foreach (var alias in asset.Aliases)
                {
                    Add(data, alias.ToString());
                }

                Add(data, asset.AssetType.AssemblyQualifiedName);
                Add(data, asset.BundleName);
            }
            foreach (var bundle in map.Bundles)
            {
                Add(data, bundle.Name);
                foreach (var path in bundle.AssetPaths)
                {
                    Add(data, path);
                }

                foreach (var dependency in bundle.Dependencies)
                {
                    Add(data, dependency);
                }
            }
            foreach (var group in map.Groups)
            {
                Add(data, group.Key);
                foreach (var member in group.Value)
                {
                    Add(data, member);
                }
            }
            foreach (var path in map.FingerprintPaths)
            {
                Add(data, path);
                var definition = path == AssetDatabase.GUIDToAssetPath(map.ProjectGuid);
                // Project 的依赖 hash 含目录展示顺序；其真实字段/引用仍由定向规范化字节及各输入单独冻结。
                if (!definition)
                {
                    Add(data, AssetDatabase.GetAssetDependencyHash(path).ToString());
                }

                var full = ResourceBuildMap.Absolute(path);
                ResourceBuildOperation.RejectLinks(full);
                Add(data, definition ? ProjectFileHash(full, ref bytes) : HashFile(full, ref bytes).Sha256);
                if (File.Exists(full + ".meta"))
                {
                    Add(data, HashFile(full + ".meta", ref bytes).Sha256);
                }
            }
            return HashText(data.ToString());
        }

        /// <summary>生成用于内存定义复验的 JSON 表示，按目录身份排列来源定义，不替代磁盘字节哈希。</summary>
        internal static string CanonicalDefinition(ResourceProject project)
        {
            if (!project)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (project.sourceDirectories == null || project.sourceDirectories.Count == 0 || project.sourceDirectories.Any(x => x == null))
            {
                throw new InvalidOperationException("资源目录列表缺失、为空或包含空条目。");
            }

            var serialized = JObject.Parse(EditorJsonUtility.ToJson(project));
            // EditorJsonUtility 在当前Unity为 ScriptableObject 输出 MonoBehaviour 包装层。
            var definition = serialized["MonoBehaviour"] as JObject ?? serialized;
            definition[nameof(ResourceProject.sourceDirectories)] = new JArray(project.sourceDirectories.OrderBy(x => x.guid, StringComparer.Ordinal)
                .ThenBy(x => x.addressPrefix ?? "", StringComparer.Ordinal)
                .Select(x => new JObject { [nameof(ResourceSourceDirectory.guid)] = x.guid, [nameof(ResourceSourceDirectory.addressPrefix)] = x.addressPrefix ?? "" }));
            return serialized.ToString(Formatting.None);
        }

        /// <summary>仅对完整识别的来源目录记录排序后哈希项目文件；未知布局保留原始字节参与身份。</summary>
        internal static string ProjectFileHash(string path, ref long bytes)
        {
            ResourceBuildOperation.RejectLinks(path);
            var raw = File.ReadAllBytes(path);
            bytes += raw.LongLength;
            var text = Encoding.UTF8.GetString(raw);
            var fields = Regex.Matches(text, @"(?m)^  (?:sourceDirectories|SourceDirectories):[^\r\n]*(?:\r?\n|$)(?:  -[^\r\n]*(?:\r?\n|$)(?:    [^\r\n]*(?:\r?\n|$))*)*");
            if (raw.SequenceEqual(Encoding.UTF8.GetBytes(text)) && fields.Count == 1)
            {
                var sources = fields[0];
                var header = Regex.Match(sources.Value, @"\A  (?:sourceDirectories|SourceDirectories):\r?\n").Value;
                var records = Regex.Matches(sources.Value, @"(?m)^  - (?:guid|Guid): [0-9a-fA-F]{32}\r?\n    (?:addressPrefix|AddressPrefix):(?: (?:""(?:[^""\\\r\n]|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""|[A-Za-z0-9_./-]*))?\r?\n")
                    .Cast<Match>().Select(x => x.Value).ToArray();
                var end = sources.Index + sources.Length;
                // 只排序完整识别的当前记录；未知记录布局回退原始字节，其余 YAML 字节不作删减或规范化。
                if (header.Length != 0 && records.Length != 0 && sources.Value == header + string.Concat(records) &&
                    (end == text.Length || Regex.IsMatch(text.Substring(end), @"\A  [A-Za-z_][A-Za-z0-9_]*:")))
                {
                    raw = Encoding.UTF8.GetBytes(text.Substring(0, sources.Index) + header +
                        string.Concat(records.OrderBy(x => x, StringComparer.Ordinal)) + text.Substring(end));
                }
            }
            using var hash = SHA256.Create();
            return Hex(hash.ComputeHash(raw));
        }

        /// <summary>以长度前缀追加指纹字段，避免字段拼接产生歧义。</summary>
        private static void Add(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value).Append('\n');
        /// <summary>计算指纹描述文本的 UTF-8 SHA-256。</summary>
        private static string HashText(string value)
        {
            using var hash = SHA256.Create();
            return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        /// <summary>将摘要字节编码为无分隔符的小写十六进制。</summary>
        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        /// <summary>计算文件长度和全部字节摘要，并累计本次读取量。</summary>
        private static ResourceBuildWorkspace.FileDigest HashFile(string path, ref long bytes)
        {
            using var file = File.OpenRead(path);
            using var hash = SHA256.Create();
            var length = file.Length;
            var digest = Hex(hash.ComputeHash(file));
            bytes += length;
            return new ResourceBuildWorkspace.FileDigest { Length = length, Sha256 = digest };
        }
    }
}
