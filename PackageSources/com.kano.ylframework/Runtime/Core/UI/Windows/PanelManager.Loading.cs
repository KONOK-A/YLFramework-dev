using System;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Async;

namespace YLFramework.Core
{
    public partial class PanelManager
    {
        /// <summary>同步解析并提交打开请求，交付前复核原会话及资源；无论成败都释放本请求输入。</summary>
        internal static T Load<T>(Module owner, string name, GameObject prefab, LoadViewArgs args, UIRootContext root, PanelOverrides overrides) where T : Panel
        {
            var operation = Accept(owner, name, prefab, args, root, overrides, typeof(T), false);
            try
            {
                operation.ResolveSync();
                Instance.Execute(operation);
                if (operation.Failure != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operation.Failure).Throw();
                }

                if (!operation.Result || !operation.Result.IsLoaded || !ReferenceEquals(operation.Result.Session, operation.DeliveredSession) ||
                    operation.Result.Module != owner || !Instance._states.TryGetValue(operation.Result, out var current) || !AreResourcesValid(current))
                {
                    throw new InvalidOperationException("同步 Panel 交付前原 Session 已结束。");
                }

                return (T)operation.Result;
            }
            finally
            {
                operation.ReleaseInputs();
            }
        }

        /// <summary>接受独立异步消费者并启动协程；启动失败也通过该消费者的终态交付。</summary>
        internal static LoadPanelRequest<T> Start<T>(Module owner, string name, GameObject prefab, LoadViewArgs args, UIRootContext root, PanelOverrides overrides) where T : Panel
        {
            var operation = Accept(owner, name, prefab, args, root, overrides, typeof(T), true);
            var request = new LoadPanelRequest<T>(operation);
            try
            {
                if (CoroutineStarter.Start(operation.Run()) == null)
                {
                    operation.Finish(null, null, new InvalidOperationException("Panel 加载需要有效协程宿主。"), LoadViewStatus.Failed);
                }
            }
            catch (Exception error)
            {
                operation.Finish(null, null, error, LoadViewStatus.Failed);
            }
            return request;
        }

        /// <summary>接管参数、校验打开入口，并固定本次请求的 owner 代次、根与规则快照。</summary>
        private static PanelOperation Accept(Module owner, string name, GameObject prefab, LoadViewArgs args, UIRootContext root,
            PanelOverrides overrides, Type expected, bool asynchronous)
        {
            args = args ?? LoadViewArgs.New();
            args.Claim(); // Claim 失败不进入清理，不能释放另一请求的输入。
            try
            {
                ViewManager.EnsureOwner(owner);
                foreach (var view in owner.OwnedViews.Values)
                {
                    if (view is Panel panel && panel.OpeningSession != null && ReferenceEquals(panel.OpeningSession, panel.Session))
                    {
                        throw new InvalidOperationException("OnOpen 期间不能重入本 Module 的 Panel 打开。");
                    }
                }

                bool named = name != null;
                if (named ? string.IsNullOrWhiteSpace(name) || !ReferenceEquals(prefab, null) : !prefab)
                {
                    throw new ArgumentException("Panel 入口需要非空完整地址或存活 Prefab。");
                }

                overrides.Validate();
                owner.ViewTransactions++;
                try
                {
                    // 优先显式根，再用 Module 绑定；只有引用真正为 null 才继续回退，fake-null 必须报错。
                    var selected = !ReferenceEquals(root, null) ? root : !ReferenceEquals(owner.PanelRoot, null) ? owner.PanelRoot :
                        YLFrameworkRoot.Instance.GetPanelRoot();
                    if (!selected)
                    {
                        throw new InvalidOperationException("显式或 Module 绑定的 Panel 根已销毁，不能回退默认根。");
                    }

                    selected.ValidateStructure();
                    RegisterRoot(selected);
                    return new PanelOperation(owner, name, prefab, args, selected, owner.PanelRules, overrides,
                        expected, checked(++Instance._sequence), asynchronous);
                }
                finally
                {
                    owner.ViewTransactions--;
                }
            }
            catch (Exception error)
            {
                try
                {
                    args.Release();
                }
                catch (Exception cleanup)
                {
                    StartUpModuleRequest.AttachCleanupError(error, cleanup);
                }
                throw;
            }
        }

        /// <summary>登记现有实际根，不抢占宿主默认角色、不改变场景或对象寿命。</summary>
        public static void RegisterRoot(UIRootContext root)
        {
            if (!root)
            {
                throw new ArgumentNullException(nameof(root));
            }

            root.ValidateStructure();
            var manager = Instance;
            manager._registeredRoots.Add(root);
            manager.RefreshInput();
            manager.SyncLateSubscription();
        }
        /// <summary>撤销根登记并协调依赖它的面板，不创建新的协调器或替代根。</summary>
        internal static void UnregisterRoot(UIRootContext root)
        {
            var manager = _instance;
            if (manager == null)
            {
                return;
            }

            manager._registeredRoots.Remove(root);
            manager.Reconcile();
        }
        internal static void RootChanged() => _instance?.Reconcile();
        /// <summary>从已有面板的真实祖先查找根；显式挂载不使用 Module 绑定或宿主默认根。</summary>
        internal static UIRootContext RequireAttachedRoot(Panel panel)
        {
            var root = panel.GetComponentInParent<UIRootContext>(true);
            if (!root)
            {
                throw new InvalidOperationException("已有 Panel 必须位于真实 UIRootContext 下；不会补根或搬到默认根。");
            }

            root.ValidateRoot();
            return root;
        }
    }
}
