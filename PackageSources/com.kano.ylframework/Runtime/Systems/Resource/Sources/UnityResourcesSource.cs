using System;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>Resources 为共享借用来源。释放逻辑记录不表示原生内存已卸载。</summary>
    /// <remarks>加载和完成观察按 Unity 主线程协议使用，不拥有系统外部借用者的释放权。</remarks>
    internal sealed class UnityResourcesSource : IResourceSource
    {
        /// <summary>本来源的固定 Resources 身份。</summary>
        public ResourceSourceId Id => ResourceSourceId.Resources;
        /// <summary>允许通过 Resources.Load 同步取得对象。</summary>
        public bool SupportsSynchronousLoad => true;

        /// <summary>要求 Resources 相对无扩展名路径，并拒绝子资产选择与显式依赖执行。</summary>
        public void Validate(ResourceCatalogEntry entry)
        {
            var path = entry.Location.Locator;
            ResourceAddress.ValidatePath(path, nameof(entry.Location.Locator));
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.IndexOf(':') >= 0 ||
                path.LastIndexOf('.') > path.LastIndexOf('/'))
            {
                throw new ArgumentException($"Resources requires an extension-free path relative to Resources: '{path}'.");
            }

            if (entry.Address.SubAsset != null || entry.Dependencies.Count != 0)
            {
                throw new ArgumentException("Resources subasset selection and explicit dependency execution are not supported in 8.1.");
            }
        }

        /// <summary>按声明定位与类型同步借用 Resources 对象，不附加独占销毁责任。</summary>
        public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new(Resources.Load(entry.Location.Locator, type));
        /// <summary>启动 Resources 原生异步请求，由操作对象持续观察至完成。</summary>
        public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => new Operation(entry.Location.Locator, type);

        /// <summary>不卸载或销毁共享对象：外部借用者不属于本系统，单份租约结束不能代表无人使用。</summary>
        public void Release(UnityEngine.Object asset)
        {
        }

        /// <summary>独占观察一个不可撤销的 Resources 请求，并将结果交给资源系统。</summary>
        private sealed class Operation : ResourceSourceOperation
        {
            private UnityEngine.ResourceRequest _native;
            /// <summary>原生请求仍在时读取其进度，释放观察状态后使用基础终态进度。</summary>
            public override float Progress => _native == null ? base.Progress : _native.progress;

            /// <summary>启动原生加载并处理订阅前已同步完成的情况。</summary>
            internal Operation(string path, Type type)
            {
                _native = Resources.LoadAsync(path, type);
                _native.completed += OnCompleted;
                if (_native.isDone)
                {
                    OnCompleted(_native);
                }
            }

            /// <summary>恰一次解除原生完成订阅，读取资产或错误后发布来源结果。</summary>
            private void OnCompleted(AsyncOperation operation)
            {
                if (IsDone)
                {
                    return;
                }

                _native.completed -= OnCompleted;
                UnityEngine.Object result = null;
                Exception error = null;
                try
                {
                    result = _native.asset;
                }
                catch (Exception cause)
                {
                    error = cause;
                }
                Complete(result, error);
            }

            /// <summary>在基类确认操作完成后丢弃原生请求引用，不卸载共享资产。</summary>
            public override void Dispose()
            {
                base.Dispose();
                _native = null;
            }
        }
    }
}
