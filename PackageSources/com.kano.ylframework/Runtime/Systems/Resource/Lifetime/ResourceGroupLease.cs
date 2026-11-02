using System;
using System.Collections.Generic;
using System.Threading;

namespace YLFramework.Resource
{
    /// <summary>组预加载成员取得的责任种类，不代表已创建实例或加载场景。</summary>
    public enum ResourceGroupItemKind
    {
        /// <summary>普通资产，由资产租约保活。</summary>
        Asset,
        /// <summary>场景 Bundle，由图根租约保活但不加载场景。</summary>
        SceneBundle,
        /// <summary>Editor 场景路径验证，不产生 Bundle 或场景租约。</summary>
        ScenePreview
    }

    /// <summary>SceneBundle成功只表示图已保活；ScenePreview只验证Editor路径，没有native Bundle或Scene租约。两者均不加载场景。</summary>
    public sealed class ResourceGroupItemResult
    {
        /// <summary>组成员在目录中的规范资产标识。</summary>
        public string AssetId
        {
            get;
        }
        /// <summary>已解析的成员地址；无法解析成员时可为 null。</summary>
        public ResourceAddress? Address
        {
            get;
        }
        /// <summary>该成员失败是否阻止整个组预加载交付。</summary>
        public bool Required
        {
            get;
        }
        /// <summary>成员的预加载责任种类。</summary>
        public ResourceGroupItemKind Kind
        {
            get;
        }
        /// <summary>目录描述中关联的 Bundle 标识；非 Bundle 来源可为 null。</summary>
        public string BundleId
        {
            get;
        }
        /// <summary>该成员的解析或取得错误；为 null 表示本项成功。</summary>
        public ResourceException Error
        {
            get;
        }
        /// <summary>本项是否成功；不表示资源此刻仍有效。</summary>
        public bool HasSucceeded => Error == null;

        /// <summary>保存单项交付结果，不持有或新增底层资源责任。</summary>
        internal ResourceGroupItemResult(string assetId, ResourceAddress? address, bool required,
            ResourceGroupItemKind kind, string bundleId, ResourceException error)
        {
            AssetId = assetId;
            Address = address;
            Required = required;
            Kind = kind;
            BundleId = bundleId;
            Error = error;
        }
    }

    /// <summary>必需成员失败时保留逐项结果，外层 ResourceException 仍保留标准资源域错误合同。</summary>
    public sealed class ResourceGroupFailure : AggregateException
    {
        /// <summary>失败结算时的逐项结果，包括成功、失败及已取消的成员。</summary>
        public IReadOnlyList<ResourceGroupItemResult> Items
        {
            get;
        }

        /// <summary>复制失败批次的成员结果，并保留取得或清理异常链。</summary>
        internal ResourceGroupFailure(IEnumerable<ResourceGroupItemResult> items, IEnumerable<Exception> causes)
            : base("Required group preload member failed; batch ownership was released.", causes)
            => Items = ResourceCatalog.Copy(items);
    }

    /// <summary>同一 ResourceSystem 的正式 Asset/Bundle 租约聚合，不缓存对象、不拥有第二份资源状态。</summary>
    public sealed class ResourceGroupLease : IDisposable
    {
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        /// <summary>本组所属的资源项目标识。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>本次预加载的目录组标识。</summary>
        public string GroupId
        {
            get;
        }
        /// <summary>全部成员的结果快照，保留可选项失败，不转移其中任何资源责任。</summary>
        public IReadOnlyList<ResourceGroupItemResult> Items
        {
            get;
        }
        /// <summary>本组持有的成功资产租约。</summary>
        public IReadOnlyList<AssetLease<UnityEngine.Object>> Assets
        {
            get;
        }
        /// <summary>本组持有的场景 Bundle 图根租约；同一 Bundle 的多个成员共享一份组内责任。</summary>
        public IReadOnlyList<BundleLease> SceneBundles
        {
            get;
        }
        /// <summary>本组是否已经开始归还；不代表底层资源已同步卸载。</summary>
        public bool IsDisposed
        {
            get; private set;
        }

        /// <summary>复制结果集合并接管成功成员租约，不额外保留另一份资源引用。</summary>
        internal ResourceGroupLease(string projectId, string groupId, IEnumerable<ResourceGroupItemResult> items,
            IEnumerable<AssetLease<UnityEngine.Object>> assets, IEnumerable<BundleLease> sceneBundles)
        {
            ProjectId = projectId;
            GroupId = groupId;
            Items = ResourceCatalog.Copy(items);
            Assets = ResourceCatalog.Copy(assets);
            SceneBundles = ResourceCatalog.Copy(sceneBundles);
        }

        /// <summary>逐项归还本组资产及场景 Bundle 租约，不影响其他消费者。</summary>
        /// <exception cref="InvalidOperationException">未在接受本组的 Unity 主线程调用。</exception>
        /// <exception cref="AggregateException">已尝试归还所有成员，但部分清理失败。</exception>
        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("Resource groups must be released on their accepting Unity thread.");
            }

            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            List<Exception> errors = null;
            foreach (var lease in Assets)
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
            }

            foreach (var lease in SceneBundles)
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception error)
                {
                    (errors ??= new List<Exception>()).Add(error);
                }
            }

            if (errors != null)
            {
                throw new AggregateException("Group preload cleanup failed.", errors);
            }
        }
    }
}
