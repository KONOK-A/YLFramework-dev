using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Events;
using CoroutineStarter = YLFramework.Async.CoroutineStarter;
using UpdateManager = YLFramework.Update.UpdateManager;

namespace YLFramework.Core
{

    internal partial class ViewManager
    {
        #region 加载

        internal static View LoadView(Module module, string assetName, Transform parent = null, params object[] param)
        {
            return LoadViewLegacy<View>(module, assetName, parent, param);
        }

        internal static View LoadView(Module module, GameObject prefab, Transform parent = null, params object[] param)
        {
            return LoadViewLegacy<View>(module, prefab, parent, param);
        }

        internal static T LoadViewLegacy<T>(Module module, string assetName, Transform parent, object[] param) where T : View
            => LoadViewSync<T>(module, assetName, null, parent, null, param, true);

        internal static T LoadViewLegacy<T>(Module module, GameObject prefab, Transform parent, object[] param) where T : View
            => LoadViewSync<T>(module, null, prefab, parent, null, param, false);

        /// <summary>同步取得并初始化实例层级；参数接管后由本调用归还，实例失败时丢弃而非归还缓存。</summary>
        private static T LoadViewSync<T>(Module module, string assetName, GameObject prefab, Transform parent,
            LoadViewArgs args, object[] param, bool named) where T : View
        {
            // 接管失败不进入finally，不能归还另一笔操作正在使用的参数。
            if (args != null)
            {
                args.Claim();
            }

            GameObject instance = null;
            CorePrefabLease prefabLease = null;
            CoreInstanceLease instanceLease = null;
            bool transaction = false;
            try
            {
                EnsureOwner(module);
                if (!ReferenceEquals(parent, null) && !parent)
                {
                    throw new ArgumentException("View parent has been destroyed.", nameof(parent));
                }

                FindBackendRoot(module, parent);
                if (named)
                {
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        throw new ArgumentException("View asset name is required.", nameof(assetName));
                    }

                    var provider = CoreResourceAccess.RequireProvider(CoreResourceAccess.Provider);
                    Exception resourceError = null;
                    foreach (var project in ModuleManager.ViewResourceProjects(module))
                    {
                        try
                        {
                            prefabLease = provider.AcquirePrefab(project, assetName);
                        }
                        catch (Exception error) when (CoreResourceAccess.CanFallback(error))
                        {
                            resourceError = error;
                        }
                        if (prefabLease != null)
                        {
                            prefab = prefabLease.Prefab;
                            break;
                        }
                    }
                    if (!prefab)
                    {
                        throw new InvalidOperationException($"View prefab not found: {assetName}.", resourceError);
                    }
                }
                else
                {
                    if (!prefab)
                    {
                        throw new ArgumentNullException(nameof(prefab));
                    }

                    assetName = prefab.name;
                }
                var sourceView = ValidateView<T>(prefab);
                if (sourceView is Panel)
                {
                    throw new InvalidOperationException("根 View 是 Panel，请使用 Module.LoadPanel/LoadPanelAsync。");
                }
                // 后端可能先触发Awake/OnEnable；取得实例也属于同步事务，不能在此关闭owner。
                module.ViewTransactions++;
                transaction = true;
                var scene = parent ? parent.gameObject.scene : SceneManager.GetActiveScene();
                // Prefab 租约只负责资源借用；取得的实例租约在初始化时交给根 View，会独立存活到退出。
                instanceLease = named ? prefabLease.Instantiate(parent, scene) : AcquireDirectInstance(prefab, parent, scene);
                instance = instanceLease.Instance;
                if (!instance)
                {
                    throw new InvalidOperationException($"View instance load failed: {assetName}.");
                }

                var view = ValidateView<T>(instance);
                EnsureOwner(module);
                if (!ReferenceEquals(parent, null) && !parent)
                {
                    throw new ArgumentException("View parent was destroyed during acquisition.", nameof(parent));
                }

                instance.transform.localPosition = Vector3.zero;
                InitializeHierarchy(module, new List<View>(instance.GetComponentsInChildren<View>(true)), view, assetName, args, param, instanceLease: instanceLease);
                return view;
            }
            catch (Exception error)
            {
                if (instanceLease != null)
                {
                    try
                    {
                        instanceLease.Discard();
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanupError);
                    }
                }
                throw;
            }
            finally
            {
                prefabLease?.Dispose();
                if (transaction)
                {
                    module.ViewTransactions--;
                }

                if (args != null)
                {
                    args.Release();
                }
            }
        }

        /// <summary>要求对象根节点具有 View 且可赋给请求类型，不以子节点组件代替根 View。</summary>
        private static T ValidateView<T>(GameObject obj) where T : View
        {
            var view = obj.GetComponent<View>();
            if (!view)
            {
                throw new InvalidOperationException($"Object {obj.name} has no root View component.");
            }

            if (view is T typed)
            {
                return typed;
            }

            throw new InvalidCastException($"View {view.GetType().FullName} cannot be loaded as {typeof(T).FullName}.");
        }

        /// <summary>先尝试停用损坏实例，并在停用抛错时仍请求销毁，不把它放回缓存。</summary>
        private static void DiscardInstance(GameObject instance)
        {
            try
            {
                instance.SetActive(false);
            }
            finally
            {
                UnityEngine.Object.Destroy(instance);
            }
        }

        /// <summary>按直接 Prefab 身份复用或创建实例，设置场景并激活，失败时由本次租约丢弃。</summary>
        private static CoreInstanceLease AcquireDirectInstance(GameObject prefab, Transform parent, Scene scene, bool panelCheckout = false)
        {
            var pool = GameObjectPool.Instance;
            string key = "YL.Core.Direct/" + prefab.GetInstanceID();
            var instance = pool.Allocate(key, parent);
            if (panelCheckout && instance)
            {
                var previous = instance.GetComponent<View>();
                if (!previous || !previous.DirectSource || !ReferenceEquals(previous.DirectSource, prefab))
                {
                    DiscardInstance(instance);
                    instance = null;
                }
            }
            if (!instance)
            {
                instance = UnityEngine.Object.Instantiate(prefab, parent);
            }

            if (!instance)
            {
                throw new InvalidOperationException("Direct Prefab checkout failed.");
            }

            var lease = new DirectInstanceLease(pool, key, instance);
            try
            {
                if (!parent && instance.scene != scene)
                {
                    SceneManager.MoveGameObjectToScene(instance, scene);
                }

                if (panelCheckout)
                {
                    instance.GetComponent<View>().DirectSource = prefab;
                }

                instance.SetActive(true);
                return lease;
            }
            catch
            {
                lease.Discard();
                throw;
            }
        }

        internal static bool IsDirectLease(CoreInstanceLease lease) => lease is DirectInstanceLease;

        /// <summary>仅负责本次直接 Prefab 实例的归还或丢弃，不拥有源 Prefab，也不操作其他租约。</summary>
        private sealed class DirectInstanceLease : CoreInstanceLease
        {
            private readonly GameObjectPool _pool;
            private readonly string _key;
            private GameObject _instance;
            /// <summary>保存本次取得的实例及其缓存归还位置，不在构造时改变实例状态。</summary>
            internal DirectInstanceLease(GameObjectPool pool, string key, GameObject instance)
            {
                this._pool = pool;
                this._key = key;
                this._instance = instance;
            }
            /// <summary>尚未交还的实例；归还或丢弃后为 null。</summary>
            public override GameObject Instance => _instance;
            /// <summary>本租约是否仍持有 Unity 中存活的实例。</summary>
            public override bool IsValid => _instance;
            /// <summary>消费本次租约并归还缓存；池已销毁时改为丢弃实例。</summary>
            public override void Return()
            {
                var owned = _instance;
                _instance = null;
                if (!owned)
                {
                    return;
                }

                if (_pool)
                {
                    _pool.Recycle(_key, owned);
                }
                else
                {
                    DiscardInstance(owned);
                }
            }
            /// <summary>消费本次租约并销毁仍存活的实例；重复调用不再处理。</summary>
            public override void Discard()
            {
                var owned = _instance;
                _instance = null;
                if (owned)
                {
                    DiscardInstance(owned);
                }
            }
        }

        // 异步加载
        internal static LoadViewRequest<T> LoadViewAsync<T>(Module module, string assetName, Transform parent = null, bool sameScene = false, params object[] param) where T : View
        {
            return StartAsync<T>(module, assetName, parent, null, param);
        }

        /// <summary>接管参数并建立独立异步请求，入口拒绝时只归还本次成功接管的参数。</summary>
        internal static LoadViewRequest<T> StartAsync<T>(Module module, string assetName, Transform parent, LoadViewArgs args,
            object[] param, GameObject directPrefab = null) where T : View
        {
            if (args != null)
            {
                args.Claim();
            }

            LoadViewRequest<T> request;
            try
            {
                EnsureOwner(module);
                if (string.IsNullOrWhiteSpace(assetName))
                {
                    throw new ArgumentException("View asset name is required.", nameof(assetName));
                }

                if (!ReferenceEquals(parent, null) && !parent)
                {
                    throw new ArgumentException("View parent has been destroyed.", nameof(parent));
                }

                FindBackendRoot(module, parent);
                request = new LoadViewRequest<T>(module, assetName, parent, args, param, directPrefab);
            }
            catch (Exception error)
            {
                if (args != null)
                {
                    try
                    {
                        args.Release();
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanupError);
                    }
                }
                throw;
            }
            try
            {
                if (CoroutineStarter.Start(request.Run()) == null && !request.IsDone)
                {
                    request.FailToStart(new InvalidOperationException("View loading requires an active coroutine host."));
                }
            }
            catch (Exception error)
            {
                request.FailToStart(error);
            }
            return request;
        }

        /// <summary>在异步等待结束后同步取得及初始化实例，跨业务回调复检请求有效性，失败丢弃本次实例。</summary>
        internal static T CompleteAsync<T>(Module module, GameObject prefab, string assetName, Transform parent, Scene scene,
            LoadViewArgs args, object[] param, Action validate, CorePrefabLease prefabLease = null, PanelManager.PanelOperation panelOperation = null) where T : View
        {
            var sourceView = ValidateView<T>(prefab);
            if (sourceView is Panel && panelOperation == null)
            {
                throw new InvalidOperationException("根 View 是 Panel，请使用 Module.LoadPanel/LoadPanelAsync。");
            }

            GameObject instance = null;
            CoreInstanceLease instanceLease = null;
            module.ViewTransactions++;
            try
            {
                validate();
                FindBackendRoot(module, parent);
                instanceLease = prefabLease != null ? prefabLease.Instantiate(parent, scene) : AcquireDirectInstance(prefab, parent, scene, panelOperation != null);
                instance = instanceLease.Instance;
                if (!instance)
                {
                    throw new InvalidOperationException($"View instance load failed: {assetName}.");
                }

                if (!instanceLease.IsValid)
                {
                    throw new InvalidOperationException("View 实例租约已失效。");
                }

                validate();
                var view = ValidateView<T>(instance);
                panelOperation?.ValidateAcquired(view);
                if (!parent && instance.scene != scene)
                {
                    SceneManager.MoveGameObjectToScene(instance, scene);
                }

                instance.transform.localPosition = Vector3.zero;
                InitializeHierarchy(module, new List<View>(instance.GetComponentsInChildren<View>(true)), view, assetName, args, param, validate, instanceLease, panelOperation);
                return view;
            }
            catch (Exception error)
            {
                panelOperation?.MarkFailing();
                if (instanceLease != null)
                {
                    try
                    {
                        instanceLease.Discard();
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanupError);
                    }
                }
                throw;
            }
            finally
            {
                module.ViewTransactions--;
            }
        }


        internal static void InitView(Module module, View view, string prefabName, LoadViewArgs loadViewArgs, params object[] param)
        {
            if (loadViewArgs != null)
            {
                loadViewArgs.Claim();
            }

            try
            {
                if (!view)
                {
                    throw new ArgumentNullException(nameof(view));
                }

                if (view.Module != null || view.Session != null || view.IsRemoving || view.IsAttaching)
                {
                    throw new InvalidOperationException("Cannot initialize an existing View session.");
                }

                InitializeHierarchy(module, new List<View>(view.GetComponentsInChildren<View>(true)), view, prefabName, loadViewArgs, param);
            }
            finally
            {
                if (loadViewArgs != null)
                {
                    loadViewArgs.Release();
                }
            }
        }

        /// <summary>为单个组件建立会话并执行加载钩子；失败只回滚本次会话，参数读取权限限于回调窗口。</summary>
        private static void InitializeView(Module module, View view, string prefabName, LoadViewArgs args, object[] param,
            View backendRoot = null, bool backend = false, Action validate = null, CoreInstanceLease instanceLease = null, PanelManager.PanelOperation panelOperation = null, Action onFailure = null)
        {
            if (!view)
            {
                throw new ArgumentNullException(nameof(view));
            }

            if (view.Module != null || view.Session != null || view.IsRemoving)
            {
                throw new InvalidOperationException("Cannot initialize an existing View session.");
            }

            object session = null;
            GameObject instance = view.gameObject;
            bool transaction = false;
            bool defaultArgs = false;
            var previousInitialization = InitializingView;
            try
            {
                EnsureOwner(module);
                view.AssetName = prefabName;
                AddView(module, view);
                session = view.Session;
                view.UsesBackend = backend;
                view.BackendInstance = backend ? instance : null;
                view.BackendLease = backend ? instanceLease : null;
                view.BackendRoot = backendRoot;
                view.BackendRootSession = backendRoot != null ? backendRoot.Session : null;
                view.IsInitializing = true;
                InitializingView = new KeyValuePair<View, object>(view, session);
                module.ViewTransactions++;
                transaction = true;
                if (view is Panel panel)
                {
                    panel.DefersEvents = true;
                    panel.InitialOpen = panelOperation;
                    panel.Configure(panelOperation != null ? panelOperation.Root : PanelManager.RequireAttachedRoot(panel),
                        panelOperation != null ? panelOperation.Settings : panel.Defaults);
                }
                view.BeforeOnLoaded();
                validate?.Invoke();
                ValidateInitialization(module, view, session);
                if (view is Panel committing)
                {
                    committing.InitialOpen?.BeginCommit?.Invoke();
                }

                view.OnLoaded(param ?? Array.Empty<object>());
                validate?.Invoke();
                ValidateInitialization(module, view, session);
                if (args == null)
                {
                    args = LoadViewArgs.New();
                    args.Claim();
                    defaultArgs = true;
                }
                args.BeginRead();
                try
                {
                    view.OnLoaded(args);
                }
                finally
                {
                    args.EndRead();
                }
                validate?.Invoke();
                ValidateInitialization(module, view, session);
                view.IsSessionLoaded = true;
                if (view is Panel opened)
                {
                    opened.Open(args);
                    validate?.Invoke();
                    ValidateInitialization(module, view, session);
                    opened.DefersEvents = false;
                }
                view.ResumeEvents();
                validate?.Invoke();
                ValidateInitialization(module, view, session);
            }
            catch (Exception initializationError)
            {
                onFailure?.Invoke();
                view.IsInitializing = false;
                if (view is Panel ended)
                {
                    ended.DefersEvents = false;
                    ended.InitialOpen = null;
                }
                view.IsSessionLoaded = false;
                view.DiscardOnExit = true;
                try
                {
                    if (session != null)
                    {
                        RemoveViewCore(module, view, session, !view, !backend, true);
                    }
                    else
                    {
                        view.AssetName = null;
                        if (backend && view)
                        {
                            DiscardInstance(view.gameObject);
                        }
                    }
                }
                catch (Exception cleanupError)
                {
                    StartUpModuleRequest.AttachCleanupError(initializationError, cleanupError);
                }
                // 组件可能已销毁而取得根对象仍存活；不能因fake-null跳过根实例处理。
                if (backend && !view && instance)
                {
                    try
                    {
                        DiscardInstance(instance);
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(initializationError, cleanupError);
                    }
                }
                throw;
            }
            finally
            {
                if (ReferenceEquals(InitializingView.Key, view) && ReferenceEquals(InitializingView.Value, session))
                {
                    InitializingView = previousInitialization;
                }

                view.IsInitializing = false;
                if (view is Panel completedPanel)
                {
                    completedPanel.DefersEvents = false;
                    completedPanel.InitialOpen = null;
                }
                if (transaction)
                {
                    module.ViewTransactions--;
                }

                if (defaultArgs)
                {
                    args.Release();
                }
            }
        }

        /// <summary>业务回调返回后复检实例、会话、数据绑定及 Panel 准备结果，拒绝提交已失效的初始化。</summary>
        private static void ValidateInitialization(Module module, View view, object session)
        {
            if (!view || !HasRegistration(module, view, session) || !view.HasValidModelBinding ||
                (view is Panel panel && (!panel.Canvas || !PanelManager.IsPreparedValid(panel))))
            {
                throw new InvalidOperationException("View initialization invalidated its instance, registration, Model binding or Panel Canvas.");
            }
        }

        internal static void AttachView(Module module, View view, LoadViewArgs args)
        {
            if (args != null)
            {
                args.Claim();
            }

            try
            {
                InitializeHierarchy(module, new List<View> { view }, null, null, args, null);
            }
            finally
            {
                if (args != null)
                {
                    args.Release();
                }
            }
        }

        internal static List<View> AttachViews(Module module, GameObject root)
        {
            EnsureOwner(module);
            if (!root)
            {
                throw new ArgumentNullException(nameof(root));
            }

            var views = new List<View>(root.GetComponentsInChildren<View>(true));
            InitializeHierarchy(module, views, null, null, null, null);
            return views;
        }

        internal static List<View> AttachViews(Module module, Scene scene)
        {
            EnsureOwner(module);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new ArgumentException("View scene must be loaded.", nameof(scene));
            }

            var views = new List<View>();
            foreach (var root in scene.GetRootGameObjects())
            {
                views.AddRange(root.GetComponentsInChildren<View>(true));
            }

            InitializeHierarchy(module, views, null, null, null, null);
            return views;
        }

        /// <summary>沿父链寻找同模块的取得实例根；拒绝跨模块归属或嵌入尚未结束的生命周期事务。</summary>
        private static View FindBackendRoot(Module module, Transform target)
        {
            for (var parent = target; parent; parent = parent.parent)
            {
                var ancestor = parent.GetComponent<View>();
                if (!ancestor)
                {
                    continue;
                }

                if (ancestor.IsRemoving || ancestor.IsAttaching || ancestor.IsInitializing)
                {
                    throw new InvalidOperationException("Cannot attach under a View lifecycle transaction.");
                }

                if (!ancestor.UsesBackend)
                {
                    continue;
                }

                if (!ReferenceEquals(ancestor.Module, module))
                {
                    throw new InvalidOperationException("Acquired hierarchy belongs to another Module.");
                }

                return ancestor;
            }
            return null;
        }

        /// <summary>预检整批层级后只初始化未注册会话；失败逆序撤销本批新增会话，不撤销已有会话。</summary>
        private static void InitializeHierarchy(Module module, List<View> views, View acquiredRoot, string assetName,
            LoadViewArgs args, object[] param, Action validate = null, CoreInstanceLease instanceLease = null, PanelManager.PanelOperation panelOperation = null)
        {
            var added = new List<KeyValuePair<View, object>>();
            var roots = new Dictionary<View, View>();
            View outerRoot = null;
            bool guarded = false;
            try
            {
                EnsureOwner(module);
                if (acquiredRoot != null)
                {
                    outerRoot = FindBackendRoot(module, acquiredRoot.transform);
                }

                foreach (var view in views)
                {
                    if (!view)
                    {
                        throw new ArgumentNullException(nameof(views));
                    }

                    if (!view.gameObject.scene.IsValid() || !view.gameObject.scene.isLoaded)
                    {
                        throw new ArgumentException("Only live scene instances can be attached.", nameof(views));
                    }

                    if (view.IsAttaching || view.IsInitializing || view.IsRemoving || view.IsUnbinding)
                    {
                        throw new InvalidOperationException("Cannot attach during a View lifecycle transaction.");
                    }

                    if (view is Panel attached)
                    {
                        PanelManager.RequireAttachedRoot(attached).GetLayer(ReferenceEquals(view, acquiredRoot) && panelOperation != null ?
                            panelOperation.Settings.layer : attached.Session != null ? attached.EffectiveSettings.layer : attached.Defaults.layer);
                    }

                    if (view.Session != null || view.Module != null)
                    {
                        if (!ReferenceEquals(view.Module, module) || !view.IsLoaded || !HasRegistration(module, view, view.Session))
                        {
                            throw new InvalidOperationException("View already has a conflicting or incomplete registration.");
                        }
                    }
                    roots.Add(view, acquiredRoot != null ? acquiredRoot : FindBackendRoot(module, view.transform));
                }
                foreach (var view in views)
                {
                    view.IsAttaching = true;
                }

                module.ViewTransactions++;
                guarded = true;
                foreach (var view in views)
                {
                    if (view.Session != null)
                    {
                        continue;
                    }

                    bool root = ReferenceEquals(view, acquiredRoot);
                    InitializeView(module, view, root ? assetName : null,
                        root || views.Count == 1 ? args : null, root ? param : null,
                          root ? outerRoot : roots[view], root, validate, root ? instanceLease : null, root ? panelOperation : null,
                          panelOperation != null ? (Action)panelOperation.MarkFailing : null);
                    added.Add(new KeyValuePair<View, object>(view, view.Session));
                }
                foreach (var entry in added)
                {
                    ValidateInitialization(module, entry.Key, entry.Value);
                }

                validate?.Invoke();
            }
            catch (Exception error)
            {
                panelOperation?.MarkFailing();
                // 批次没有提交，不派发成功退出通知；已有绑定与activeSelf保持不变。
                foreach (var entry in added)
                {
                    entry.Key.IsSessionLoaded = false;
                }

                if (acquiredRoot != null)
                {
                    acquiredRoot.DiscardOnExit = true;
                }

                for (int i = added.Count - 1; i >= 0; i--)
                {
                    var entry = added[i];
                    try
                    {
                        RemoveViewCore(module, entry.Key, entry.Value, !entry.Key, !entry.Key.UsesBackend, true);
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanupError);
                    }
                }
                if (acquiredRoot && acquiredRoot.Session == null)
                {
                    try
                    {
                        DiscardInstance(acquiredRoot.gameObject);
                    }
                    catch (Exception cleanupError)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanupError);
                    }
                }
                throw;
            }
            finally
            {
                if (guarded)
                {
                    foreach (var view in views)
                    {
                        view.IsAttaching = false;
                    }

                    module.ViewTransactions--;
                }
            }
        }

        #endregion

        #region 加载(With LoadViewArgs)

        // 加载
        internal static T LoadView<T>(Module module, string assetName, Transform parent = null, LoadViewArgs loadViewArgs = null) where T : View
        {
            return LoadViewSync<T>(module, assetName, null, parent, loadViewArgs, null, true);
        }

        internal static T LoadView<T>(Module module, GameObject prefab, Transform parent = null, LoadViewArgs loadViewArgs = null) where T : View
        {
            return LoadViewSync<T>(module, null, prefab, parent, loadViewArgs, null, false);
        }

        // 异步加载
        internal static LoadViewRequest<T> LoadViewAsync<T>(Module module, string assetName, Transform parent = null, bool sameScene = false, LoadViewArgs loadViewArgs = null) where T : View
        {
            return StartAsync<T>(module, assetName, parent, loadViewArgs, null);
        }

        #endregion



    }
}
