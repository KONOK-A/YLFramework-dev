using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace YLFramework.Resource
{
    /// <summary>配置工厂的显式来源模式，不按当前环境自动选择或回退。</summary>
    public enum ResourceLoadMode
    {
        /// <summary>消费正式发布快照，由唯一 ResourceSystem 装配 Bundle 来源。</summary>
        Bundles,
        /// <summary>使用 Editor 专用工厂装配模拟来源；Runtime 工厂拒绝此模式。</summary>
        EditorSimulation
    }

    /// <summary>显式只读配置。默认装配 Resources、本地/URL Texture；已验证快照由唯一 ResourceSystem 装配 Bundle 来源，不读取旧设置。</summary>
    public sealed class ResourceConfiguration
    {
        /// <summary>显式配置的内存目录副本；Store 挂载得到的目录由初始化流程另行合入。</summary>
        public IReadOnlyList<ResourceCatalog> Catalogs
        {
            get;
        }
        /// <summary>可选的本地版本存储；为 null 时不执行 Store 挂载与更新流程。</summary>
        public ArtifactStore ArtifactStore
        {
            get;
        }
        /// <summary>本地与 URL 图片输入、并发和闲置缓存策略，未提供时使用默认值。</summary>
        public ResourceImageOptions Images
        {
            get;
        }
        /// <summary>可选应用key来源与显式数据预算；None来源不因此执行密码处理。</summary>
        public ResourceDecryptionOptions Decryption
        {
            get;
        }
        internal IReadOnlyList<IResourceSource> Sources
        {
            get;
        }

        /// <summary>只读枚举明确配置的 Store；无需初始化 ResourceSystem。必须在 Unity 主线程逐步驱动并在中止时 Dispose 枚举器。</summary>
        public IEnumerator QueryArtifactsAsync(Action<ArtifactStoreReport> completed, int maxEntries = 10000, CancellationToken cancellation = default)
            => global::YLFramework.Resource.ArtifactStore.Query(ArtifactStore, completed, maxEntries, cancellation);

        /// <summary>没有 Catalog 时仍可显式使用本地/URL 图片，不扫描或挂载任何项目。</summary>
        public ResourceConfiguration() : this(Array.Empty<ResourceCatalog>()) { }

        /// <summary>复制明确目录并装配 Resources、本地图片及 URL 图片来源；构造本身不加载资源或获取密钥。</summary>
        public ResourceConfiguration(IEnumerable<ResourceCatalog> catalogs, ResourceImageOptions images = null, ResourceDecryptionOptions decryption = null)
            : this(catalogs, new IResourceSource[] { new UnityResourcesSource(), new TextureResourceSource(true, images), new TextureResourceSource(false, images) })
        {
            Images = images ?? ResourceImageOptions.Default;
            Decryption = decryption;
        }

        /// <summary>在默认来源配置上绑定应用明确提供的 Store，不在构造时执行挂载或下载。</summary>
        public ResourceConfiguration(ArtifactStore artifactStore, IEnumerable<ResourceCatalog> catalogs = null, ResourceImageOptions images = null,
            ResourceDecryptionOptions decryption = null)
            : this(catalogs ?? Array.Empty<ResourceCatalog>(), images, decryption)
            => ArtifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));

        /// <summary>为内部装配复制目录与来源列表；来源身份冲突和条目合法性由 ResourceSystem 初始化检查。</summary>
        internal ResourceConfiguration(IEnumerable<ResourceCatalog> catalogs, IEnumerable<IResourceSource> sources, ResourceImageOptions images = null)
        {
            Catalogs = ResourceCatalog.Copy(catalogs ?? throw new ArgumentNullException(nameof(catalogs)));
            Sources = ResourceCatalog.Copy(sources ?? throw new ArgumentNullException(nameof(sources)));
            Images = images ?? ResourceImageOptions.Default;
        }

        /// <summary>将明确项目闭包装配为正式Bundle配置；无cacheRoot时只读消费快照，有cacheRoot时复用原ArtifactStore。</summary>
        /// <remarks>只创建配置，不Mount、不初始化或下载。EditorSimulation须使用Editor工厂，任何错误都不回退来源。</remarks>
        public static ResourceConfiguration Create(ResourceLoadMode mode, IEnumerable<ResourceProjectMount> projects,
            string cacheRoot = null, ResourceImageOptions images = null, ResourceDecryptionOptions decryption = null)
        {
            if (mode != ResourceLoadMode.Bundles)
            {
                throw new ResourceException(ResourceError.Unsupported, "Runtime configuration only accepts the explicit Bundles mode; use the Editor simulation factory for EditorSimulation.");
            }

            var mounts = ResourceCatalog.Copy(projects ?? throw new ArgumentNullException(nameof(projects)));
            if (mounts.Count == 0 || mounts.Any(x => x == null) ||
                mounts.Select(x => x.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mounts.Count)
            {
                throw new ArgumentException("An explicit nonempty set of distinct resource projects is required.", nameof(projects));
            }

            if (cacheRoot != null)
            {
                return new ResourceConfiguration(new ArtifactStore(cacheRoot, mounts), images: images, decryption: decryption);
            }

            if (mounts.Any(x => x.BuiltIn == null || x.Remote != null))
            {
                throw new ArgumentException("Direct snapshots require a complete local snapshot for every project and no remote source; configure an explicit cacheRoot for the update store.", nameof(projects));
            }

            return new ResourceConfiguration(mounts.Select(x => x.BuiltIn.CreateCatalog(x.AssetTypes)), images, decryption);
        }
    }
}
