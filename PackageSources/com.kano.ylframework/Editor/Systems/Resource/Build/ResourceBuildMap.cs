using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>本次解析的有限来源副本；不持有可编辑Project/规则，也不进入Catalog或持久身份。</summary>
    public sealed class ResourceBuildSource
    {
        /// <summary>来源项目配置的 Unity GUID。</summary>
        public string ProjectGuid
        {
            get;
        }
        /// <summary>来源项目的技术标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>产生此次选择的 Group 标识。</summary>
        public string GroupId
        {
            get;
        }
        /// <summary>解析时来源 Group 的分包意图。</summary>
        public ResourcePacking Packing
        {
            get;
        }
        /// <summary>所选文件、文件夹或自动图集输出的 GUID。</summary>
        public string SelectionGuid
        {
            get;
        }
        /// <summary>解析时选择项对应的源路径，不是单个展开资产的必然路径。</summary>
        public string SelectionPath
        {
            get;
        }
        /// <summary>选择项的地址覆写，未设置时为空字符串。</summary>
        public string SelectionAddress
        {
            get;
        }
        /// <summary>选择项别名的只读副本。</summary>
        public IReadOnlyList<string> Aliases
        {
            get;
        }
        /// <summary>选择项扩展名筛选的只读副本。</summary>
        public IReadOnlyList<string> Extensions
        {
            get;
        }
        /// <summary>自动图集来源的配置 GUID；普通选择时为 null。</summary>
        public string AtlasConfigurationGuid
        {
            get;
        }
        /// <summary>自动图集来源的稳定规则 Id；普通选择时为 null。</summary>
        public string AtlasRuleId
        {
            get;
        }
        internal string Definition
        {
            get;
        }
        internal string DirectoryIdentity
        {
            get;
        }

        /// <summary>复制选择或图集规则的来源信息，供报告展示及后续定位复验。</summary>
        internal ResourceBuildSource(ResourceProject project, string projectGuid, ResourceGroup group,
            ResourceSelection selection, string path, ResourceAtlasChange generated)
        {
            ProjectGuid = projectGuid;
            ProjectId = project.projectId;
            GroupId = group.id;
            Packing = group.packing;
            SelectionGuid = selection.guid;
            SelectionPath = path;
            SelectionAddress = selection.address ?? "";
            Aliases = Array.AsReadOnly((string[])(selection.aliases ?? Array.Empty<string>()).Clone());
            Extensions = Array.AsReadOnly((string[])(selection.extensions ?? Array.Empty<string>()).Clone());
            AtlasConfigurationGuid = generated == null ? null : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project.automaticAtlases));
            AtlasRuleId = generated?.Rule.id;
            Definition = generated == null ? JsonUtility.ToJson(selection) : JsonUtility.ToJson(generated.Rule);
            DirectoryIdentity = ResourceProjectPaths.SemanticIdentity(project);
        }
    }

    /// <summary>Editor 主资产身份和加载描述。子资产的正式描述与持久化 Catalog 不在此定义。</summary>
    public sealed class ResourceBuildAsset
    {
        /// <summary>主资产源文件的 Unity GUID。</summary>
        public string Guid
        {
            get;
        }
        /// <summary>主资产在源文件内的持久 localId。</summary>
        public long LocalId
        {
            get;
        }
        /// <summary>GUID 与 localId 组合的规范主资产身份，不随加载地址变化。</summary>
        public string AssetId => Guid + "_" + LocalId;
        /// <summary>Unity 构建和原生加载使用的源文件路径。</summary>
        public string SourcePath
        {
            get;
        }
        /// <summary>应用消费的主逻辑地址。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>同一主资产的额外逻辑地址，按地址路径排序。</summary>
        public IReadOnlyList<ResourceAddress> Aliases
        {
            get;
        }
        /// <summary>预期运行时资产类型，已转换 Unity 特有的编辑类型。</summary>
        public Type AssetType
        {
            get;
        }
        /// <summary>依赖布局和环归并完成后分配的原生 Bundle 名称。</summary>
        public string BundleName
        {
            get; internal set;
        }
        /// <summary>产生本主资产的显式选择或图集规则来源。</summary>
        public ResourceBuildSource Source
        {
            get;
        }
        /// <summary>是否为通过场景路径消费的 Scene 资产。</summary>
        public bool IsScene => AssetType == typeof(SceneAsset);

        /// <summary>从 Unity 主资产解析持久身份、编译类型和逻辑地址。</summary>
        internal ResourceBuildAsset(string path, ResourceAddress address, IEnumerable<string> aliases, ResourceBuildSource source)
        {
            Source = source;
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (!asset || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId))
            {
                throw new InvalidOperationException("Cannot resolve Unity asset identity: " + path);
            }

            Guid = guid;
            LocalId = localId;
            SourcePath = path;
            Address = address;
            Aliases = aliases.Select(x => new ResourceAddress(address.ProjectId, x)).OrderBy(x => x.Path, StringComparer.Ordinal).ToList().AsReadOnly();
            AssetType = ResourceBuildMap.BuildType(asset.GetType());
        }
    }

    /// <summary>最终 Bundle 的布局来源，不代表运行时加载顺序。</summary>
    public enum ResourceBundlePlacement
    {
        /// <summary>按 Group 合包，场景与普通资产仍隔离。</summary>
        Group,
        /// <summary>由逐主资产分包产生。</summary>
        PerAsset,
        /// <summary>由多个初始 Bundle 共用的依赖组成。</summary>
        SharedDependencies,
        /// <summary>独立承载场景 Bundle 的非场景私有依赖。</summary>
        SceneDependencies,
        /// <summary>为消除 Bundle 依赖环而合并产生。</summary>
        CycleMerge
    }

    /// <summary>每个源文件恰好属于一个 Bundle；数组按 ordinal 排序并以副本公开。</summary>
    public sealed class ResourceBuildBundle
    {
        private readonly string[] _paths;
        private readonly string[] _dependencies;
        private readonly string[] _dependents;
        /// <summary>包含项目命名空间的稳定原生 Bundle 名称。</summary>
        public string Name
        {
            get;
        }
        /// <summary>是否仅包含场景文件，场景与普通资产不得混包。</summary>
        public bool IsScene
        {
            get;
        }
        /// <summary>是否承载多包共享依赖，环归并后保留该标记。</summary>
        public bool IsShared
        {
            get;
        }
        /// <summary>最终布局的归类原因。</summary>
        public ResourceBundlePlacement Placement
        {
            get;
        }
        /// <summary>源文件磁盘字节之和，不含meta，不是压缩Bundle大小或native内存。</summary>
        public long SourceBytes
        {
            get;
        }
        /// <summary>本包独占的源文件路径副本。</summary>
        public string[] AssetPaths => (string[])_paths.Clone();
        /// <summary>本包直接依赖的 Bundle 名称副本。</summary>
        public string[] Dependencies => (string[])_dependencies.Clone();
        /// <summary>直接依赖本包的 Bundle 名称副本。</summary>
        public string[] Dependents => (string[])_dependents.Clone();

        /// <summary>复制最终文件归属和图边，验证场景隔离并统计源文件字节。</summary>
        internal ResourceBuildBundle(string name, IEnumerable<string> assets, IEnumerable<string> edges, IEnumerable<string> incoming,
            bool shared, ResourceBundlePlacement placement)
        {
            Name = name;
            _paths = assets.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            _dependencies = edges.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            _dependents = incoming.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            IsScene = _paths.All(x => Path.GetExtension(x).Equals(".unity", StringComparison.OrdinalIgnoreCase));
            if (!IsScene && _paths.Any(x => Path.GetExtension(x).Equals(".unity", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Scene and ordinary assets cannot share a Bundle: " + name);
            }

            IsShared = shared;
            Placement = placement;
            SourceBytes = _paths.Sum(path => new FileInfo(ResourceBuildMap.Absolute(path)).Length);
        }
    }

    /// <summary>从自有 Project 解析稳定显式 BuildMap；不修改任何源资源或 Importer，未保存定义会另行标记。</summary>
    public sealed class ResourceBuildMap
    {
        private const int Limit = 16384;
        /// <summary>项目配置的持久 Unity GUID。</summary>
        public string ProjectGuid
        {
            get;
        }
        /// <summary>此次分包采用的项目技术标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>项目标识摘要的短 token，用于 Bundle 名称而非替代完整归属校验。</summary>
        public string ProjectToken
        {
            get;
        }
        /// <summary>本次解析的来源目录身份及地址前缀快照。</summary>
        public IReadOnlyList<ResourceProjectPaths.DirectoryEntry> SourceDirectories
        {
            get;
        }
        /// <summary>按序排列的来源目录路径，不表示目录内所有文件都会入包。</summary>
        public IReadOnlyList<string> AssetRoots
        {
            get;
        }
        /// <summary>分包快照创建时的 UTC 时间，不参与持久内容身份。</summary>
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        /// <summary>解析时项目或自动图集配置是否存在尚未保存的编辑。</summary>
        public bool ContainsUnsavedDefinitions
        {
            get;
        }
        /// <summary>解析时 Editor 的活动构建平台。</summary>
        public string Platform
        {
            get;
        }
        /// <summary>显式选择及规则接入的主资产，按逻辑地址排序，不含隐式依赖和子资产条目。</summary>
        public IReadOnlyList<ResourceBuildAsset> Assets
        {
            get;
        }
        /// <summary>消除可合并依赖环后的最终 Bundle 布局。</summary>
        public IReadOnlyList<ResourceBuildBundle> Bundles
        {
            get;
        }
        /// <summary>Group 标识到规范主资产身份的映射，不直接映射 Bundle 或图集内 Sprite。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups
        {
            get;
        }
        /// <summary>基于源文件字节的重复避免估计，不代表压缩产物或 native memory 的测量。</summary>
        public long AvoidedDuplicateSourceBytes
        {
            get;
        }
        /// <summary>环归并所减少的 Bundle 数量，不是环的数量。</summary>
        public int MergedBundleCount
        {
            get;
        }
        internal IReadOnlyList<string> FingerprintPaths
        {
            get;
        }

        /// <summary>封装最终归属与双向依赖快照，并复用 Catalog 约束检查有界无环依赖。</summary>
        private ResourceBuildMap(ResourceProject project, string guid, ResourceProjectPaths paths, List<ResourceBuildAsset> assets,
            Dictionary<string, IReadOnlyList<string>> groups, Dictionary<string, string[]> graph,
            HashSet<string> fingerprintPaths, Dictionary<string, string> assignments, HashSet<string> shared,
            long avoidedBytes, int merged)
        {
            ProjectGuid = guid;
            ProjectId = project.projectId;
            ProjectToken = Token(ProjectId);
            SourceDirectories = paths.Directories;
            AssetRoots = Array.AsReadOnly(paths.Directories.Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            ContainsUnsavedDefinitions = EditorUtility.IsDirty(project) || project.automaticAtlases && EditorUtility.IsDirty(project.automaticAtlases);
            Platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            Assets = assets.OrderBy(x => x.Address.Path, StringComparer.Ordinal).ToList().AsReadOnly();
            Groups = new ReadOnlyDictionary<string, IReadOnlyList<string>>(groups);
            FingerprintPaths = fingerprintPaths.OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly();
            var edges = BundleEdges(assignments, graph);
            var incoming = edges.Keys.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                foreach (var dependency in edge.Value)
                {
                    incoming[dependency].Add(edge.Key);
                }
            }

            var prefixLength = ("ylr_" + ProjectToken + "_").Length;
            ResourceBundlePlacement Placement(string name) => name.Substring(prefixLength, 2) switch
            {
                "g_" => ResourceBundlePlacement.Group,
                "a_" => ResourceBundlePlacement.PerAsset,
                "s_" => ResourceBundlePlacement.SharedDependencies,
                "d_" => ResourceBundlePlacement.SceneDependencies,
                "c_" => ResourceBundlePlacement.CycleMerge,
                _ => throw new InvalidOperationException("Unknown Bundle placement: " + name)
            };
            Bundles = assignments.GroupBy(x => x.Value).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new ResourceBuildBundle(x.Key, x.Select(a => a.Key), edges[x.Key], incoming[x.Key], shared.Contains(x.Key), Placement(x.Key)))
                .ToList().AsReadOnly();
            foreach (var asset in assets)
            {
                asset.BundleName = assignments[asset.SourcePath];
            }

            AvoidedDuplicateSourceBytes = avoidedBytes;
            MergedBundleCount = merged;
            // 复用 8.1 有界 DAG 合同，仅验证，不挂载 Runtime 或引入另一个目录权威。
            _ = new ResourceCatalog(ProjectId, Array.Empty<ResourceCatalogEntry>(),
                Bundles.Select(x => new ResourceDependencyNode(x.Name, x.Dependencies)));
        }

        /// <summary>
        /// 展开选择和已有效生成的图集，检查地址冲突并为依赖建立唯一文件归属。
        /// 来源范围只授予输入准入；此处不冻结完整文件字节，也不创建工作区或更新待生成图集。
        /// </summary>
        public static ResourceBuildMap Create(ResourceProject project)
        {
            if (!project)
            {
                throw new ArgumentNullException(nameof(project));
            }

            ValidateId(project.projectId);
            var projectPath = AssetDatabase.GetAssetPath(project);
            var guid = AssetDatabase.AssetPathToGUID(projectPath);
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("ResourceProject must be saved with its own Unity GUID.");
            }

            var directories = ResourceProjectPaths.Resolve(project);
            var generatedAtlases = ResourceAtlasGenerator.CurrentOutputs(project);
            var atlasDirectory = project.automaticAtlases ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(project.automaticAtlases))?.Replace('\\', '/') : null;
            var assets = new List<ResourceBuildAsset>();
            var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
            var foldedAddresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var foldedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prefix = "ylr_" + Token(project.projectId) + "_";
            var editorAssemblies = new HashSet<string>(CompilationPipeline.GetAssemblies()
                .Where(x => (x.flags & AssemblyFlags.EditorAssembly) != 0).Select(x => x.name), StringComparer.Ordinal);
            if (project.groups == null || project.groups.Count == 0 || project.groups.Count > Limit)
            {
                throw new InvalidOperationException("A bounded nonempty Group collection is required.");
            }

            foreach (var group in project.groups.OrderBy(x => x?.id, StringComparer.Ordinal))
            {
                if (group == null)
                {
                    throw new InvalidOperationException("Null Group.");
                }

                ValidateId(group.id);
                if (!foldedGroups.Add(group.id))
                {
                    throw new InvalidOperationException("Duplicate/case-folded Group: " + group.id);
                }

                if (!Enum.IsDefined(typeof(ResourcePacking), group.packing) || group.selections == null)
                {
                    throw new InvalidOperationException("Invalid Group packing or empty selection: " + group.id);
                }

                var members = new List<string>();
                var selections = group.selections.Concat(generatedAtlases.Where(x => x.GroupId == group.id)
                    .Select(x => new ResourceSelection { guid = x.Guid, address = x.Address }));
                foreach (var selection in selections)
                {
                    if (selection == null)
                    {
                        throw new InvalidOperationException("Null resource selection.");
                    }

                    var selected = ResolveGuid(selection.guid);
                    var generated = generatedAtlases.FirstOrDefault(x => x.Guid == selection.guid && x.GroupId == group.id && x.Address == selection.address);
                    if (generated == null)
                    {
                        directories.RequireSource(selected);
                    }

                    if (atlasDirectory != null && (selected == atlasDirectory || Within(atlasDirectory, selected)) &&
                        generated == null)
                    {
                        throw new InvalidOperationException("Generated Atlas assets are selected by their rules, not manual selections: " + selected);
                    }

                    var folder = AssetDatabase.IsValidFolder(selected);
                    var aliases = selection.aliases ?? Array.Empty<string>();
                    if (aliases.Length > Limit)
                    {
                        throw new InvalidOperationException("Resource alias limit exceeded.");
                    }

                    if (folder && (!string.IsNullOrEmpty(selection.address) || aliases.Length != 0))
                    {
                        throw new InvalidOperationException("Folder selections cannot override individual addresses.");
                    }

                    var extensions = selection.extensions ?? Array.Empty<string>();
                    if (extensions.Any(x => string.IsNullOrEmpty(x) || x[0] != '.' || x.IndexOfAny(new[] { '/', '\\', '*' }) >= 0))
                    {
                        throw new InvalidOperationException("Selection extensions must be exact dotted extensions.");
                    }

                    var source = new ResourceBuildSource(project, guid, group, selection, selected,
                        generated);
                    var paths = folder ? AssetDatabase.FindAssets("", new[] { selected }).Select(AssetDatabase.GUIDToAssetPath)
                        .Where(x => !AssetDatabase.IsValidFolder(x) && (atlasDirectory == null || !Within(atlasDirectory, x)) && CanBundle(x, editorAssemblies)) : new[] { selected };
                    foreach (var path in paths.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        if (extensions.Length != 0 && !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!CanBundle(path, editorAssemblies))
                        {
                            throw new InvalidOperationException("Unsupported selected asset: " + path);
                        }

                        if (assignments.ContainsKey(path))
                        {
                            throw new InvalidOperationException("Asset selected more than once: " + path);
                        }

                        if (assets.Count == Limit)
                        {
                            throw new InvalidOperationException("Resource selection limit exceeded.");
                        }

                        ResourceBuildOperation.RejectLinks(Absolute(path));
                        var addressText = string.IsNullOrEmpty(selection.address) ? directories.DefaultAddress(path) : selection.address;
                        ResourceAddress address;
                        try
                        {
                            address = new ResourceAddress(project.projectId, addressText);
                        }
                        catch (ArgumentException error)
                        {
                            throw new InvalidOperationException("Invalid resource address: " + addressText, error);
                        }
                        foreach (var key in new[] { addressText }.Concat(aliases))
                        {
                            try
                            {
                                _ = new ResourceAddress(project.projectId, key);
                            }
                            catch (ArgumentException error)
                            {
                                throw new InvalidOperationException("Invalid resource alias/address: " + key, error);
                            }
                            if (foldedAddresses.TryGetValue(key, out var previous))
                            {
                                throw new InvalidOperationException("两个资源使用了相同的加载地址或别名 (Duplicate/case-folded address)：" + key + "\n" + previous + "\n" + path +
                                    "\n请为其中一个设置不同的地址或目录地址前缀。");
                            }

                            if (foldedAddresses.Count == Limit)
                            {
                                throw new InvalidOperationException("Resource address limit exceeded.");
                            }

                            foldedAddresses.Add(key, path);
                        }
                        var asset = new ResourceBuildAsset(path, address, aliases, source);
                        var bundle = prefix + (group.packing == ResourcePacking.PerAsset ? "a_" + asset.Guid :
                            "g_" + Token(group.id) + (asset.IsScene ? "_scenes" : "_assets"));
                        assets.Add(asset);
                        members.Add(asset.AssetId);
                        assignments.Add(path, bundle);
                    }
                }
                if (members.Count == 0)
                {
                    throw new InvalidOperationException("Group selects no buildable assets: " + group.id);
                }

                groups.Add(group.id, members.OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly());
            }
            var fingerprints = new HashSet<string>(StringComparer.Ordinal) { projectPath };
            if (project.automaticAtlases)
            {
                fingerprints.Add(AssetDatabase.GetAssetPath(project.automaticAtlases));
            }

            var graph = CollectDependencies(directories, generatedAtlases.Select(x => x.Path), atlasDirectory, assignments.Keys, fingerprints, editorAssemblies);
            ResourceAtlasRules.Validate(graph.Keys, assets);
            var original = new Dictionary<string, string>(assignments, StringComparer.Ordinal);
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var bundle in original.GroupBy(x => x.Value).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var queue = new Queue<string>(bundle.Select(x => x.Key));
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (queue.Count != 0)
                {
                    var path = queue.Dequeue();
                    if (!visited.Add(path))
                    {
                        continue;
                    }
                    // 已显式归属的另一 Bundle 是图边，不再把它的私有依赖重新归属给当前包。
                    if (original.TryGetValue(path, out var explicitOwner) && explicitOwner != bundle.Key)
                    {
                        continue;
                    }

                    if (!original.ContainsKey(path))
                    {
                        if (!owners.TryGetValue(path, out var set))
                        {
                            owners.Add(path, set = new HashSet<string>(StringComparer.Ordinal));
                        }

                        set.Add(bundle.Key);
                    }
                    foreach (var dependency in graph[path])
                    {
                        queue.Enqueue(dependency);
                    }
                }
            }
            var shared = new HashSet<string>(StringComparer.Ordinal);
            var sceneBundles = new HashSet<string>(assets.Where(x => x.IsScene).Select(x => original[x.SourcePath]), StringComparer.Ordinal);
            long avoided = 0;
            foreach (var bucket in owners.GroupBy(x => string.Join("\n", x.Value.OrderBy(a => a, StringComparer.Ordinal))).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var entries = bucket.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
                var users = entries[0].Value;
                var name = users.Count == 1 && !sceneBundles.Contains(users.Single()) ? users.Single() :
                    prefix + (users.Count == 1 ? "d_" : "s_") + Token(bucket.Key + "\n" +
                    string.Join("\n", entries.Select(x => AssetDatabase.AssetPathToGUID(x.Key)).OrderBy(x => x, StringComparer.Ordinal)));
                if (users.Count > 1)
                {
                    shared.Add(name);
                }

                foreach (var entry in entries)
                {
                    assignments.Add(entry.Key, name);
                    avoided += new FileInfo(Absolute(entry.Key)).Length * (users.Count - 1);
                }
            }
            var merged = MergeCycles(prefix, assignments, graph, shared);
            return new ResourceBuildMap(project, guid, directories, assets, groups, graph, fingerprints, assignments, shared, avoided, merged);
        }

        /// <summary>收集有界资产依赖图与指纹输入；代码仅参与指纹，不作为 Bundle 内容。</summary>
        private static Dictionary<string, string[]> CollectDependencies(ResourceProjectPaths directories, IEnumerable<string> generatedPaths, string atlasDirectory,
            IEnumerable<string> selected, HashSet<string> fingerprints, HashSet<string> editorAssemblies)
        {
            var generated = new HashSet<string>(generatedPaths, StringComparer.Ordinal);
            void RequirePath(string path)
            {
                if (generated.Contains(path))
                {
                    return;
                }

                if (ResourceProjectPaths.Contains(atlasDirectory, path))
                {
                    throw new InvalidOperationException("不能将生成目录中的配置或失效输出作为依赖：" + path);
                }

                directories.RequireSource(path);
            }
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var queue = new Queue<string>(selected);
            var foldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var edgeCount = 0;
            while (queue.Count != 0)
            {
                var path = queue.Dequeue();
                if (result.ContainsKey(path))
                {
                    continue;
                }

                if (result.Count == Limit || !foldedPaths.Add(path))
                {
                    throw new InvalidOperationException("Asset graph limit or case-folded path conflict: " + path);
                }

                RequirePath(path);
                ResourceBuildOperation.RejectLinks(Absolute(path));
                fingerprints.Add(path);
                var dependencies = new List<string>();
                var atlas = AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(UnityEngine.U2D.SpriteAtlas) ?
                    AssetDatabase.LoadAssetAtPath<UnityEngine.U2D.SpriteAtlas>(path) : null;
                var inputs = AssetDatabase.GetDependencies(path, false).AsEnumerable();
                if (atlas)
                {
                    inputs = inputs.Where(value => !AssetDatabase.IsValidFolder(value)).Concat(ResourceAtlasRules.SpritePaths(atlas));
                }

                foreach (var dependency in inputs.Distinct().OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (dependency == path || IsBuiltIn(dependency))
                    {
                        continue;
                    }

                    fingerprints.Add(dependency);
                    if (IsCode(dependency))
                    {
                        continue;
                    }

                    RequirePath(dependency);
                    if (!CanBundle(dependency, editorAssemblies))
                    {
                        throw new InvalidOperationException("Unsupported asset dependency: " + path + " -> " + dependency);
                    }

                    if (++edgeCount > Limit * 4)
                    {
                        throw new InvalidOperationException("Asset dependency edge limit exceeded.");
                    }

                    dependencies.Add(dependency);
                    queue.Enqueue(dependency);
                }
                result.Add(path, dependencies.ToArray());
            }
            return result;
        }

        /// <summary>将资产依赖投影为去重的跨 Bundle 直接依赖边。</summary>
        private static Dictionary<string, HashSet<string>> BundleEdges(Dictionary<string, string> assignments, Dictionary<string, string[]> graph)
        {
            var edges = assignments.Values.Distinct().ToDictionary(x => x, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var asset in assignments)
            {
                foreach (var dependency in graph[asset.Key])
                {
                    var to = assignments[dependency];
                    if (to != asset.Value)
                    {
                        edges[asset.Value].Add(to);
                    }
                }
            }

            return edges;
        }

        /// <summary>合并 Bundle 强连通分量以消除环，若合并会混入场景和普通资产则拒绝。</summary>
        private static int MergeCycles(string prefix, Dictionary<string, string> assignments, Dictionary<string, string[]> graph, HashSet<string> shared)
        {
            var edges = BundleEdges(assignments, graph);
            var reverse = edges.Keys.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                foreach (var dependency in edge.Value)
                {
                    reverse[dependency].Add(edge.Key);
                }
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var root in edges.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var stack = new Stack<(string Id, bool Exit)>();
                stack.Push((root, false));
                while (stack.Count != 0)
                {
                    var item = stack.Pop();
                    if (item.Exit)
                    {
                        order.Add(item.Id);
                        continue;
                    }
                    if (!visited.Add(item.Id))
                    {
                        continue;
                    }

                    stack.Push((item.Id, true));
                    foreach (var dependency in edges[item.Id].OrderByDescending(x => x, StringComparer.Ordinal))
                    {
                        stack.Push((dependency, false));
                    }
                }
            }
            visited.Clear();
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            var merged = 0;
            for (var i = order.Count - 1; i >= 0; i--)
            {
                var component = new List<string>();
                var stack = new Stack<string>();
                stack.Push(order[i]);
                while (stack.Count != 0)
                {
                    var id = stack.Pop();
                    if (!visited.Add(id))
                    {
                        continue;
                    }

                    component.Add(id);
                    foreach (var dependent in reverse[id])
                    {
                        stack.Push(dependent);
                    }
                }
                if (component.Count <= 1)
                {
                    continue;
                }

                var paths = assignments.Where(x => component.Contains(x.Value)).Select(x => x.Key).ToArray();
                var scenes = paths.Count(x => Path.GetExtension(x).Equals(".unity", StringComparison.OrdinalIgnoreCase));
                if (scenes != 0 && scenes != paths.Length)
                {
                    throw new InvalidOperationException("Scene/asset Bundle cycle cannot be merged: " + string.Join(" -> ", component));
                }

                var name = prefix + "c_" + Token(string.Join("\n", component.OrderBy(x => x, StringComparer.Ordinal)));
                if (component.Any(shared.Contains))
                {
                    shared.Add(name);
                }

                foreach (var id in component)
                {
                    replacements.Add(id, name);
                }

                merged += component.Count - 1;
            }
            foreach (var path in assignments.Keys.ToArray())
            {
                if (replacements.TryGetValue(assignments[path], out var replacement))
                {
                    assignments[path] = replacement;
                }
            }

            return merged;
        }

        /// <summary>验证稳定资源标识，并将参数错误转换为构建定义错误。</summary>
        private static void ValidateId(string id)
        {
            try
            {
                ResourceAddress.ValidateId(id, nameof(id));
            }
            catch (ArgumentException error)
            {
                throw new InvalidOperationException("Invalid stable resource identifier: " + id, error);
            }
        }

        /// <summary>将精确 Unity GUID 解析为现存资产路径，拒绝缺失身份。</summary>
        private static string ResolveGuid(string guid)
        {
            if (guid == null || guid.Length != 32 || guid.Any(x => !Uri.IsHexDigit(x)))
            {
                throw new InvalidOperationException("An exact Unity GUID is required: " + guid);
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("Unresolved Unity GUID: " + guid);
            }

            return path;
        }

        /// <summary>忽略大小写判断严格子路径，不把根目录自身计入。</summary>
        private static bool Within(string root, string path) => path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        /// <summary>识别无需登记为项目来源的 Unity 内置资源路径。</summary>
        private static bool IsBuiltIn(string path) => path == "Resources/unity_builtin_extra" || path == "Library/unity default resources";
        /// <summary>识别只参与输入指纹、不直接入包的代码及程序集文件。</summary>
        private static bool IsCode(string path) => new[] { ".cs", ".dll", ".asmdef", ".asmref" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        /// <summary>排除目录、代码和仅编辑器资产，保留具有合法运行时产物的 Unity 编辑类型。</summary>
        private static bool CanBundle(string path, HashSet<string> editorAssemblies)
        {
            if (AssetDatabase.IsValidFolder(path) || IsCode(path) || path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var sourceType = AssetDatabase.GetMainAssetTypeAtPath(path);
            var type = BuildType(sourceType);
            return type != null && type != typeof(DefaultAsset) && type != typeof(MonoScript) &&
                (type == typeof(SceneAsset) || !type.Assembly.GetName().Name.StartsWith("UnityEditor", StringComparison.Ordinal) &&
                    !editorAssemblies.Contains(type.Assembly.GetName().Name));
        }

        // Unity 的 Controller/BlendTree 编辑类型有合法的运行时产物；不能把 Editor 类型统一视为不可构建。
        internal static Type BuildType(Type source) => source == typeof(UnityEditor.Animations.AnimatorController) ? typeof(RuntimeAnimatorController) :
            source == typeof(UnityEditor.Animations.BlendTree) ? typeof(Motion) : source;

        /// <summary>以 Unity 工程根解析绝对路径，不创建目录或授予路径准入。</summary>
        internal static string Absolute(string path) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), path));
        /// <summary>截取 UTF-8 文本 SHA-256 的前 128 位作为稳定路径或名称 token。</summary>
        internal static string Token(string value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant().Substring(0, 32);
        }

        /// <summary>将最终布局转换为 Unity 显式构建输入，不依赖源 Importer 的 Bundle 标记。</summary>
        internal AssetBundleBuild[] UnityBuilds() => Bundles.Select(x => new AssetBundleBuild { assetBundleName = x.Name, assetNames = x.AssetPaths }).ToArray();
    }
}
