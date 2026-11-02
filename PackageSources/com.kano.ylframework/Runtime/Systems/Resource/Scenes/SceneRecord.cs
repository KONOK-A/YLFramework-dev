using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    /// <summary>独立于 caller Lease 的真实场景责任。handle + 加载序号 + Catalog 快照共同标识一次加载。</summary>
    public sealed class SceneRecord
    {
        internal readonly ResourceSystem Owner;
        internal BundleLease Retention;
        internal AsyncOperation UnloadOperation;
        internal readonly List<ResourceRequest<bool>> UnloadWaiters = new();
        internal int UnloadedFrame = -1;
        /// <summary>本次实际观察到的 Unity 场景身份，不保证当前仍已加载。</summary>
        public Scene Scene
        {
            get;
        }
        /// <summary>该次加载的原生场景 handle；不能单独据此识别后续复用的场景。</summary>
        public int Handle => Scene.handle;
        /// <summary>与创建本记录的场景请求对应的加载序号。</summary>
        public long LoadSequence
        {
            get;
        }
        /// <summary>本次加载使用的资源目录地址。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>本次加载捕获的资源目录快照身份。</summary>
        public System.Guid SnapshotId
        {
            get;
        }
        /// <summary>冻结目录的内容摘要；非本地快照目录可为 null。</summary>
        public string CatalogDigest
        {
            get;
        }
        /// <summary>本次观察到的真实场景路径，供身份存活检查使用。</summary>
        public string Path
        {
            get;
        }
        /// <summary>创建本记录的加载请求模式。</summary>
        public LoadSceneMode Mode
        {
            get;
        }
        /// <summary>本记录是否属于无法唯一归因的候选；框架不授予对候选的主动卸载权。</summary>
        public bool IsAmbiguous
        {
            get;
        }
        /// <summary>资源系统是否已将本身份标记为卸载并移出场景账本。</summary>
        public bool IsUnloaded
        {
            get; internal set;
        }
        /// <summary>记录未被移除且同一 Unity 场景仍有效、已加载并匹配原路径；不判断资源会话状态。</summary>
        public bool IsLoaded => !IsUnloaded && Scene.IsValid() && Scene.isLoaded && Scene.path == Path;

        /// <summary>捕获真实场景及目录身份，并接管独立于调用方租约的 Bundle 保活责任。</summary>
        internal SceneRecord(ResourceSystem owner, Scene scene, ResourceCatalog catalog, SceneRequest request,
            ResourceAddress address, string path, bool ambiguous, BundleLease retention)
        {
            Owner = owner;
            Scene = scene;
            LoadSequence = request.LoadSequence;
            Address = address;
            SnapshotId = catalog.SnapshotId;
            CatalogDigest = catalog.CatalogDigest;
            Path = path;
            Mode = request.Mode;
            IsAmbiguous = ambiguous;
            Retention = retention;
        }
    }
}
