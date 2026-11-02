using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Resource
{
    /// <summary>
    /// Editor 专用的零打包取得/预览。每次操作从正式 Project 重建 Catalog 内存快照，不缓存 Unity 对象或第二份地址表。
    /// 可绑定已发布 Catalog，定义/身份不一致时明确失效。结果是 AssetDatabase 借用对象，不得 Destroy/Unload。
    /// 预览不初始化 Runtime；CreateConfiguration 可装配真实 Editor Scene 加载，但不模拟 Bundle 生命周期。
    /// </summary>
    [InitializeOnLoad]
    public sealed class ResourceEditorSimulation
    {
        private static readonly int _mainThread;
        private readonly Dictionary<string, string> _projects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ResourceCatalogDocument> _catalogs = new(StringComparer.Ordinal);
        /// <summary>记录编辑器主线程，并安装按正式项目身份解析的 Core 预览入口。</summary>
        static ResourceEditorSimulation()
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            YLFramework.Editor.Common.EditorAssetTool.PreviewResolver = ResolveCorePreview;
        }

        /// <summary>从当前资产数据库查找唯一项目后借用目标资产，不复用旧项目索引。</summary>
        private static Object ResolveCorePreview(string projectId, string address, string subAsset)
        {
            CheckEditor();
            var matches = AssetDatabase.FindAssets("t:ResourceProject")
                .Select(guid => AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(project => project && project.projectId == projectId).ToArray();
            if (matches.Length == 0)
            {
                throw new ArgumentException("No formal YL resource project matches ProjectId: " + projectId);
            }

            if (matches.Length != 1)
            {
                throw new InvalidOperationException("Ambiguous YL resource ProjectId: " + projectId);
            }

            return new ResourceEditorSimulation(matches).Load(new ResourceAddress(projectId, address, subAsset), typeof(Object));
        }

        /// <summary>登记显式项目身份及可选发布描述；后续访问仍从当前定义重建快照。</summary>
        /// <param name="projects">本模拟器允许解析的完整项目集合，不得重复。</param>
        /// <param name="catalogs">可选的发布描述约束，须匹配所选项目、Unity 版本和当前平台。</param>
        public ResourceEditorSimulation(ResourceProject[] projects, ResourceCatalogDocument[] catalogs = null)
        {
            CheckEditor();
            if (projects == null || projects.Length == 0 || projects.Length > 16384)
            {
                throw new ArgumentException("Explicit resource projects are required.");
            }

            foreach (var project in projects)
            {
                var map = ResourceBuildMap.Create(project);
                if (!this._projects.TryAdd(map.ProjectId, map.ProjectGuid))
                {
                    throw new ArgumentException("Duplicate simulation project.");
                }
            }
            foreach (var catalog in catalogs ?? Array.Empty<ResourceCatalogDocument>())
            {
                if (catalog == null || !this._projects.ContainsKey(catalog.ProjectId) || !this._catalogs.TryAdd(catalog.ProjectId, catalog) ||
                    catalog.UnityCompatibility != Application.unityVersion || catalog.Platform != EditorUserBuildSettings.activeBuildTarget.ToString())
                {
                    throw new ArgumentException("Simulation catalog identity/compatibility mismatch.");
                }
            }
        }

        /// <summary>按当前定义解析规范地址或别名；绑定发布描述时同时检查其是否失效。</summary>
        public ResourceCatalogEntry Resolve(ResourceAddress address) => Snapshot(address.ProjectId).Catalog.Resolve(address);

        /// <summary>调用方显式给出已编译类型；不根据 Catalog 类型字符串反射，也不回退到其他资源后端。</summary>
        public Object Load(ResourceAddress address, Type type)
        {
            if (type == null || !typeof(Object).IsAssignableFrom(type) || type.ContainsGenericParameters)
            {
                throw new ArgumentException("A compiled Unity asset type is required.");
            }

            var entry = Resolve(address);
            if (!type.IsAssignableFrom(entry.Location.AssetType))
            {
                throw new InvalidOperationException("Simulation asset type mismatch: " + address);
            }

            var path = entry.Location.Locator;
            var candidates = entry.Address.SubAsset == null ? new[] { AssetDatabase.LoadMainAssetAtPath(path) } : AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            var matches = candidates.Where(x => x && type.IsInstanceOfType(x) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(x, out string guid, out long id) &&
                guid + "_" + id.ToString(CultureInfo.InvariantCulture) == entry.AssetId).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException("Simulation asset identity is missing or ambiguous: " + address);
            }

            return matches[0];
        }

        /// <summary>借用与地址和编译类型匹配的 AssetDatabase 对象，不转移销毁或卸载责任。</summary>
        public T Load<T>(ResourceAddress address) where T : Object => (T)Load(address, typeof(T));

        /// <summary>只枚举 Catalog canonical 成员；Scene 成员是预览描述，不表示已加载场景。</summary>
        public IReadOnlyList<ResourceCatalogEntry> GetGroup(string projectId, string groupId)
        {
            var snapshot = Snapshot(projectId);
            if (!snapshot.Map.Groups.TryGetValue(groupId, out var members))
            {
                throw new ArgumentException("Unknown simulation Group: " + groupId);
            }

            return snapshot.Catalog.Entries.Where(x => members.Contains(x.AssetId)).ToList().AsReadOnly();
        }

        /// <summary>重建指定项目的内存资源目录，不创建发布制品或写入地址表。</summary>
        public ResourceCatalog GetCatalog(string projectId) => Snapshot(projectId).Catalog;

        /// <summary>Editor的显式模式选择接点。Bundles直接调用Runtime工厂，绝不因发布缺失或校验错误切到模拟。</summary>
        public static ResourceConfiguration CreateConfiguration(ResourceLoadMode mode, ResourceProject[] projects,
            IEnumerable<ResourceProjectMount> mounts = null, string cacheRoot = null, ResourceImageOptions images = null)
        {
            if (mode == ResourceLoadMode.Bundles)
            {
                return ResourceConfiguration.Create(mode, mounts, cacheRoot, images);
            }

            if (mode != ResourceLoadMode.EditorSimulation)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (cacheRoot != null)
            {
                throw new ArgumentException("Editor simulation does not mount or write an artifact store.", nameof(cacheRoot));
            }

            var versions = mounts?.ToArray();
            if (versions != null && (projects == null || versions.Length != projects.Length || versions.Any(x => x?.BuiltIn == null) ||
                !versions.Select(x => x.ProjectId).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                    projects.Select(x => x ? x.projectId : null).OrderBy(x => x, StringComparer.Ordinal))))
            {
                throw new ArgumentException("Explicit simulation versions must match the complete selected project set.", nameof(mounts));
            }

            if (versions != null)
            {
                foreach (var version in versions)
                {
                    _ = version.BuiltIn.CreateCatalog(version.AssetTypes);
                }
            }

            return new ResourceEditorSimulation(projects, versions?.Select(x => x.BuiltIn.Document).ToArray()).CreateConfiguration(images);
        }

        /// <summary>显式装配 Editor borrowed Source。地址/类型身份在每次生产时重验，不根据外部设置自动启用。</summary>
        public ResourceConfiguration CreateConfiguration() => CreateConfiguration(ResourceImageOptions.Default);

        /// <summary>模拟与Bundle配置共用同一图片策略；不因使用Editor Source而绕过编码/像素/并发边界。</summary>
        public ResourceConfiguration CreateConfiguration(ResourceImageOptions images) => new(_projects.Keys.Select(id => Snapshot(id).Catalog),
            new IResourceSource[] { new UnityResourcesSource(), new TextureResourceSource(true, images), new TextureResourceSource(false, images), new SimulationSource(this) }, images);

        /// <summary>将编辑器借用资产和真实场景加载接入统一资源协议，不模拟 Bundle 所有权。</summary>
        private sealed class SimulationSource : IResourceSource, IResourceSceneSource
        {
            private readonly ResourceEditorSimulation _simulation;
            /// <summary>绑定负责重验当前定义的模拟器。</summary>
            internal SimulationSource(ResourceEditorSimulation simulation) => this._simulation = simulation;
            /// <summary>此来源在统一协议中的编辑器身份。</summary>
            public ResourceSourceId Id => ResourceSourceId.Editor;
            /// <summary>允许同步取得 AssetDatabase 资产；异步入口也在调用时完成资产取得。</summary>
            public bool SupportsSynchronousLoad => true;
            /// <summary>按已声明类型区分场景描述与普通资产。</summary>
            public bool IsScene(ResourceCatalogEntry entry) => entry.Location.AssetType == typeof(SceneAsset);
            /// <summary>重新解析地址并核对资产身份和位置，拒绝过期来源快照。</summary>
            public void Validate(ResourceCatalogEntry entry)
            {
                var current = _simulation.Resolve(entry.Address);
                if (current.AssetId != entry.AssetId || !current.Location.Equals(entry.Location))
                {
                    throw new InvalidOperationException("Editor Source snapshot is stale: " + entry.Address);
                }
            }
            /// <summary>重验后借用普通资产；场景必须走场景协议，不能作为运行时资产取得。</summary>
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type)
            {
                Validate(entry);
                if (entry.Location.AssetType == typeof(SceneAsset))
                {
                    throw new ResourceException(ResourceError.Unsupported, "Scene previews are not Runtime asset acquisitions.");
                }

                return new ResourceSourceResult(_simulation.Load(entry.Address, type));
            }
            /// <summary>同步取得借用资产后返回已完成操作，不开启后台加载。</summary>
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => new Operation(Load(entry, type).Asset);
            /// <summary>借用对象归 AssetDatabase 管理，因此释放协议不销毁或卸载它。</summary>
            public void Release(Object asset)
            {
            }
            /// <summary>重验场景描述并以 GUID 核对当前路径，不加载场景。</summary>
            public string ResolveScenePath(ResourceCatalogEntry entry)
            {
                Validate(entry);
                if (!IsScene(entry))
                {
                    throw new ResourceException(ResourceError.TypeMismatch, "The Editor address is not a Scene.");
                }

                var path = AssetDatabase.GUIDToAssetPath(entry.AssetId.Substring(0, 32));
                if (path != entry.Location.Locator)
                {
                    throw new ResourceException(ResourceError.AddressNotFound, "Editor Scene GUID/path changed.");
                }

                return path;
            }
            /// <summary>调用编辑器的 Play Mode 场景加载，实际创建场景而非返回预览描述。</summary>
            public AsyncOperation LoadSceneAsync(string path, LoadSceneParameters parameters) => EditorSceneManager.LoadSceneAsyncInPlayMode(path, parameters);
            /// <summary>包装已取得的借用资产，满足统一异步来源的完成协议。</summary>
            private sealed class Operation : ResourceSourceOperation
            {
                /// <summary>立即以现成资产完成操作，不创建额外加载任务。</summary>
                internal Operation(Object asset) => Complete(asset, null);
            }
        }

        /// <summary>从当前项目重建主资产、子资产和组描述，并核对可选发布描述的身份约束。</summary>
        private (ResourceBuildMap Map, ResourceCatalog Catalog) Snapshot(string projectId)
        {
            CheckEditor();
            if (projectId == null || !_projects.TryGetValue(projectId, out var guid))
            {
                throw new ArgumentException("Unknown simulation ProjectId.");
            }

            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(guid));
            if (!project || project.projectId != projectId)
            {
                throw new InvalidOperationException("Simulation project removed or identity changed; select it again.");
            }

            var map = ResourceBuildMap.Create(project);
            var entries = new List<ResourceCatalogEntry>();
            // 主资产与子资产共用 GUID/localId 身份规则，别名沿用相同子资产键。
            void Add(ResourceBuildAsset main, ResourceBuiltSubAsset sub = null)
            {
                var key = sub?.LocalId.ToString(CultureInfo.InvariantCulture);
                entries.Add(new ResourceCatalogEntry(main.Guid + "_" + (sub?.LocalId ?? main.LocalId).ToString(CultureInfo.InvariantCulture),
                    new ResourceAddress(projectId, main.Address.Path, key), new ResourceLocation(new ResourceSourceId("editor"), main.SourcePath, sub?.AssetType ?? main.AssetType),
                    main.Aliases.Select(x => new ResourceAddress(projectId, x.Path, key))));
            }
            foreach (var main in map.Assets)
            {
                Add(main);
            }

            foreach (var sub in ResourceBundleBuilder.DescribeSubAssets(map))
            {
                Add(sub.MainAsset, sub);
            }

            var snapshot = new ResourceCatalog(projectId, entries)
            {
                Groups = map.Groups.Select(x => new ResourceCatalogGroup(x.Key,
                x.Value.Select(id => new ResourceCatalogGroupMember(id, true)))).ToList().AsReadOnly()
            };
            if (_catalogs.TryGetValue(projectId, out var published))
            {
                // immutable Catalog 不随源重命名/定义切换偷偷漂移；仅允许内容重导入而身份描述不变。
                if (published.Assets.Count != entries.Count || published.Groups.Count != map.Groups.Count)
                {
                    throw new InvalidOperationException("Published Catalog is stale; rebuild the resource project.");
                }

                foreach (var asset in published.Assets)
                {
                    var current = entries.SingleOrDefault(x => x.AssetId == asset.AssetId);
                    var main = map.Assets.SingleOrDefault(x => x.Guid == asset.Guid);
                    if (current == null || main == null || current.Address.Path != asset.Address || current.Address.SubAsset != asset.SubAssetKey ||
                        current.Location.AssetType.FullName != (asset.Kind == "Scene" ? typeof(SceneAsset).FullName : asset.AssetType) ||
                        (main.IsScene ? null : current.Location.AssetType.Assembly.GetName().Name) != asset.AssetAssembly ||
                        (main.IsScene ? main.SourcePath : main.SourcePath.ToLowerInvariant()) != asset.LoadName ||
                        !current.Aliases.Select(x => x.Path).SequenceEqual(asset.Aliases) ||
                        asset.SubAssetKey != null && !AssetDatabase.LoadAllAssetRepresentationsAtPath(main.SourcePath).Any(x => x && x.name == asset.SubAssetName &&
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(x, out string _, out long id) && id == asset.LocalId))
                    {
                        throw new InvalidOperationException("Published Catalog descriptor is stale: " + asset.AssetId);
                    }
                }
                foreach (var group in published.Groups)
                {
                    if (!map.Groups.TryGetValue(group.Id, out var members) || !members.SequenceEqual(group.Members.Select(x => x.AssetId)) || group.Members.Any(x => !x.Required))
                    {
                        throw new InvalidOperationException("Published Catalog Group is stale: " + group.Id);
                    }
                }
            }
            return (map, snapshot);
        }

        /// <summary>只允许编译和导入均稳定后的编辑器主线程访问资产数据库。</summary>
        private static void CheckEditor()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Simulation requires the settled Editor main thread.");
            }
        }
    }
}
