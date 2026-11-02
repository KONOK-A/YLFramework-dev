using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Singletons;
using YLFramework.Events;
using CoroutineStarter = YLFramework.Async.CoroutineStarter;
using UpdateManager = YLFramework.Update.UpdateManager;

namespace YLFramework.Core
{

    internal partial class ViewManager
    {
        /// <summary>先使旧扫描轮次失效，再解除更新及场景卸载监听，避免在途回调继续使用旧跟踪状态。</summary>
        private static void EndPruneRound()
        {
            _pruneRound = null;
            var subscription = _pruneSubscription;
            _pruneSubscription = null;
            subscription?.Unregister();
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        /// <summary>按本轮模块快照清理死对象，跳过仍在 View 事务中的模块及已经结束的扫描轮次。</summary>
        private static void PruneDestroyedSessions(object round)
        {
            // 从未激活的对象没有OnDestroy。仅扫描受管索引，正常帧不分配View快照。
            _pruneOwners.Clear();
            _pruneOwners.AddRange(_sceneOwners);
            foreach (var module in _pruneOwners)
            {
                if (!ReferenceEquals(_pruneRound, round))
                {
                    break;
                }

                if (module.ViewTransactions != 0)
                {
                    continue;
                }

                try
                {
                    PruneDestroyed(module);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                }
            }
            _pruneOwners.Clear();
        }

        #region 添加 
        internal static void AddView(Module module, View view)
        {
            EnsureOwner(module);
            if (!view)
            {
                throw new ArgumentNullException(nameof(view));
            }

            if (view.IsRemoving || view.IsUnbinding)
            {
                throw new InvalidOperationException("Cannot register a View during exit or unbinding.");
            }

            if (view.Module != null || view.Session != null)
            {
                if (!ReferenceEquals(view.Module, module))
                {
                    throw new InvalidOperationException("View is already owned by another Module.");
                }

                if (view.IsInitializing || view.IsRemoving)
                {
                    throw new InvalidOperationException("Cannot register a View during its lifecycle callback.");
                }

                if (HasRegistration(module, view, view.Session))
                {
                    return;
                }

                throw new InvalidOperationException("View owner and registration do not match.");
            }
            int id = view.GetInstanceID();
            if (module.OwnedViews.ContainsKey(id) ||
                (!string.IsNullOrEmpty(view.AssetName) && module.Views.TryGetValue(view.AssetName, out var existing) && existing.ContainsKey(id)))
            {
                throw new InvalidOperationException($"View ID {id} is already registered.");
            }

            Type type = view.GetType();
            if (module.TypeViews.TryGetValue(type, out var typed) && typed.Exists(item => ReferenceEquals(item, view)))
            {
                throw new InvalidOperationException("View type index already contains this instance.");
            }

            view.RegisteredId = id;
            view.Session = new object();
            view.LastSession = view.Session;
            view.Module = module;
            module.OwnedViews.Add(id, view);
            if (!string.IsNullOrEmpty(view.AssetName))
            {
                if (!module.Views.TryGetValue(view.AssetName, out var named))
                {
                    module.Views.Add(view.AssetName, named = new Dictionary<int, View>());
                }

                named.Add(id, view);
            }
            if (typed == null)
            {
                module.TypeViews.Add(type, typed = new List<View>());
            }

            typed.Add(view);
            TrackScene(module, view);
        }

        internal static void EnsureOwner(Module module)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            if (!SingletonSessionState.CanRunExecution)
            {
                throw new InvalidOperationException("Views cannot be acquired outside an active execution session.");
            }

            module.EnsureCanAddObjects();
            if (!ReferenceEquals(ModuleManager.GetModule(module.ModuleName), module) ||
                (module.State != ModuleState.Starting && module.State != ModuleState.Running))
            {
                throw new InvalidOperationException("View requires a registered Starting or Running Module.");
            }
        }

        /// <summary>核对归属、会话及全部适用索引，并要求类型索引中恰有一个相同实例。</summary>
        private static bool HasRegistration(Module module, View view, object session)
        {
            if (!(session != null && ReferenceEquals(view.Module, module) && ReferenceEquals(view.Session, session) &&
                module.OwnedViews.TryGetValue(view.RegisteredId, out var registered) && ReferenceEquals(registered, view) &&
                (string.IsNullOrEmpty(view.AssetName) || (module.Views.TryGetValue(view.AssetName, out var named) &&
                    named.TryGetValue(view.RegisteredId, out var byName) && ReferenceEquals(byName, view))) &&
                module.TypeViews.TryGetValue(view.GetType(), out var typed)))
            {
                return false;
            }

            int count = 0;
            foreach (var item in typed)
            {
                if (ReferenceEquals(item, view))
                {
                    count++;
                }
            }

            return count == 1;
        }

        #endregion

        #region 移除

        internal static void RemoveView(Module module, View view)
        {
            if (ReferenceEquals(view, null))
            {
                return;
            }

            RemoveView(module, view, view.Session);
        }

        internal static void RemoveView(Module module, View view, object session, bool destroyed = false)
            => RemoveViewCore(module, view, session, destroyed, false, false);

        /// <summary>按精确会话清理视图；实例移除事件在解绑前通知，全局移除事件与关闭钩子在撤销索引后通知，清理错误逐项汇总。</summary>
        private static void RemoveViewCore(Module module, View view, object session, bool destroyed, bool preserveInstance, bool rollback)
        {
            if (module == null || ReferenceEquals(view, null) || session == null ||
                !ReferenceEquals(view.Module, module) || !ReferenceEquals(view.Session, session) ||
                !module.OwnedViews.TryGetValue(view.RegisteredId, out var registered) || !ReferenceEquals(registered, view))
            {
                return;
            }

            if (view.IsRemoving)
            {
                return;
            }

            if (view.IsInitializing || view.IsUnbinding || (view.IsAttaching && !rollback))
            {
                throw new InvalidOperationException("Cannot close a View during initialization, attachment or unbinding.");
            }

            if (!rollback && !destroyed)
            {
                EnsureNoOpeningPanelRetired(module, view, session);
            }

            bool notify = view.IsSessionLoaded;
            view.IsRemoving = true;
            view.IsSessionLoaded = false;
            module.ViewTransactions++;
            var errors = new List<Exception>();
            bool damaged = view.DiscardOnExit || (view is Panel policy && !policy.EffectiveSettings.cacheOnClose);
            bool backend = view.UsesBackend;
            bool nestedBackend = backend && !ReferenceEquals(view.BackendRoot, null);
            GameObject instance = backend ? view.BackendInstance : (!destroyed && view ? view.gameObject : null);
            var instanceLease = view.BackendLease;
            // 卸载中的场景实例可能尚未变为 fake-null，但已不能归还缓存。
            if (backend && (destroyed || (instance && (!instance.scene.IsValid() || !instance.scene.isLoaded))))
            {
                damaged = true;
            }

            if (backend)
            {
                foreach (var child in Snapshot(module))
                {
                    if (!ReferenceEquals(child.Key, view) && ReferenceEquals(child.Key.BackendRoot, view) &&
                        ReferenceEquals(child.Key.BackendRootSession, session))
                    {
                        try
                        {
                            RemoveViewCore(module, child.Key, child.Value, !child.Key, !child.Key.UsesBackend, rollback);
                        }
                        catch (Exception error)
                        {
                            errors.Add(error);
                        }
                    }
                }
            }

            try
            {
                view.StopEvents();
            }
            catch (Exception error)
            {
                errors.Add(error);
                damaged = true;
            }
            if (notify && view.ViewRemoved != null)
            {
                foreach (Action observer in view.ViewRemoved.GetInvocationList())
                {
                    try
                    {
                        observer();
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
            }

            try
            {
                view.OnRemove();
            }
            catch (Exception error)
            {
                errors.Add(error);
                damaged = true;
            }
            try
            {
                view.DetachModel();
            }
            catch (Exception error)
            {
                errors.Add(error);
                damaged = true;
            }
            List<CorePrefabLease> panelSources = null;
            try
            {
                PanelManager.ReleaseView(view, out panelSources, rollback);
            }
            catch (Exception error)
            {
                errors.Add(error);
                damaged = true;
            }
            damaged |= view.DiscardOnExit;
            if (damaged && !backend && !ReferenceEquals(view.BackendRoot, null) &&
                ReferenceEquals(view.BackendRoot.Session, view.BackendRootSession))
            {
                view.BackendRoot.DiscardOnExit = true;
            }

            // 撤销当前会话后仍保持Removing，直到实例处理结束，禁止OnDisable重入复活。
            DetachIndexes(module, view, session);
            try
            {
                if (!preserveInstance)
                {
                    // 独立取得、挂在另一取得根下的实例仍独立归还，不能留在外层缓存子树中被再次纳管。
                    if (nestedBackend && instance)
                    {
                        instance.transform.SetParent(null, true);
                    }

                    if (backend && instanceLease != null)
                    {
                        if (damaged)
                        {
                            instanceLease.Discard();
                        }
                        else
                        {
                            instanceLease.Return();
                        }
                    }
                    else if (instance)
                    {
                        instance.SetActive(false);
                        if (backend && damaged)
                        {
                            UnityEngine.Object.Destroy(instance);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
                try
                {
                    instanceLease?.Discard();
                }
                catch (Exception discardError)
                {
                    errors.Add(discardError);
                }
                if (backend && instance)
                {
                    try
                    {
                        UnityEngine.Object.Destroy(instance);
                    }
                    catch (Exception discardError)
                    {
                        errors.Add(discardError);
                    }
                }
            }
            try
            {
                PanelManager.ReleaseSources(panelSources);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            view.IsRemoving = false;
            module.ViewTransactions--;
            if (notify)
            {
                try
                {
                    EventManager.TriggerEventAll(CoreEvents.ViewRemoved, view);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                // 广播可以复用同一组件；不把旧完成钩子派发给已开启过的新会话。
                if (ReferenceEquals(view.LastSession, session))
                {
                    try
                    {
                        view.NotifyClosed();
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
            }
            ThrowCleanupErrors(errors);
        }

        /// <summary>只撤销仍匹配的旧会话索引与引用；保留 LastSession 以识别后续通知中的重新进入。</summary>
        private static void DetachIndexes(Module module, View view, object session)
        {
            if (!ReferenceEquals(view.Session, session) || !ReferenceEquals(view.Module, module))
            {
                return;
            }

            if (module.OwnedViews.TryGetValue(view.RegisteredId, out var owned) && ReferenceEquals(owned, view))
            {
                module.OwnedViews.Remove(view.RegisteredId);
            }

            if (!string.IsNullOrEmpty(view.AssetName) && module.Views.TryGetValue(view.AssetName, out var named))
            {
                if (named.TryGetValue(view.RegisteredId, out var registered) && ReferenceEquals(registered, view))
                {
                    named.Remove(view.RegisteredId);
                }

                if (named.Count == 0)
                {
                    module.Views.Remove(view.AssetName);
                }
            }
            Type type = view.GetType();
            if (module.TypeViews.TryGetValue(type, out var typed))
            {
                typed.RemoveAll(item => ReferenceEquals(item, view));
                if (typed.Count == 0)
                {
                    module.TypeViews.Remove(type);
                }
            }
            view.ViewRemoved = null;
            view.AssetName = null;
            view.UsesBackend = false;
            view.BackendInstance = null;
            view.BackendLease = null;
            view.BackendRoot = null;
            view.BackendRootSession = null;
            view.SceneHandle = 0;
            view.DiscardOnExit = false;
            view.Module = null;
            view.Session = null;
            if (view is Panel panel)
            {
                panel.EndPanelSession();
            }

            if (module.OwnedViews.Count == 0)
            {
                _sceneOwners.Remove(module);
                if (_sceneOwners.Count == 0)
                {
                    EndPruneRound();
                }
            }
        }

        internal static void ThrowCleanupErrors(List<Exception> errors)
        {
            if (errors.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }

            if (errors.Count > 1)
            {
                throw new AggregateException("View cleanup failed.", errors).Flatten();
            }
        }

        /// <summary>沿已记录的实例归属链检查 OnOpen 保护，拒绝退役正在打开的 Panel 或其取得根。</summary>
        private static void EnsureNoOpeningPanelRetired(Module module, View root, object session)
        {
            foreach (var candidate in module.OwnedViews.Values)
            {
                if (!(candidate is Panel panel) || panel.OpeningSession == null || !ReferenceEquals(panel.OpeningSession, panel.Session))
                {
                    continue;
                }

                var current = candidate;
                var currentSession = candidate.Session;
                while (!ReferenceEquals(current, null))
                {
                    if (ReferenceEquals(current, root) && ReferenceEquals(currentSession, session))
                    {
                        throw new InvalidOperationException("OnOpen 期间不能关闭当前 Panel 或退役其 backend 祖先。");
                    }

                    var parent = current.BackendRoot;
                    if (ReferenceEquals(parent, null) || !ReferenceEquals(parent.Session, current.BackendRootSession))
                    {
                        break;
                    }

                    currentSession = current.BackendRootSession;
                    current = parent;
                }
            }
        }

        #endregion

        #region 清空

        /// <summary>
        /// 清空某一个模块的所有View
        /// </summary>
        /// <param name="module"></param>
        internal static void ClearView(Module module)
        {
            ClearSessions(module, Snapshot(module));
        }

        internal static void ClearView(Module module, string assetName)
        {
            if (assetName == null)
            {
                throw new ArgumentNullException(nameof(assetName));
            }

            var sessions = Snapshot(module);
            sessions.RemoveAll(item => item.Key.AssetName != assetName);
            ClearSessions(module, sessions);
        }

        internal static void ClearView<T>(Module module) where T : View
        {
            var sessions = Snapshot(module);
            sessions.RemoveAll(item => !(item.Key is T));
            ClearSessions(module, sessions);
        }

        internal static List<KeyValuePair<View, object>> Snapshot(Module module)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            var sessions = new List<KeyValuePair<View, object>>(module.OwnedViews.Count);
            foreach (var view in module.OwnedViews.Values)
            {
                sessions.Add(new KeyValuePair<View, object>(view, view.Session));
            }
            // 取得根先退出，子会话不单独改变缓存子树的activeSelf。
            sessions.Sort((left, right) => right.Key.UsesBackend.CompareTo(left.Key.UsesBackend));
            return sessions;
        }

        /// <summary>先预检整批会话再逐项退出；快照携带会话身份，旧条目不能关闭后来复用的组件会话。</summary>
        private static void ClearSessions(Module module, List<KeyValuePair<View, object>> sessions)
        {
            // 批量出口先完整预检，不能在后面的受保护会话报错前先归还前面的 checkout。
            foreach (var session in sessions)
            {
                if (session.Key)
                {
                    EnsureNoOpeningPanelRetired(module, session.Key, session.Value);
                }
            }

            var errors = new List<Exception>();
            foreach (var session in sessions)
            {
                try
                {
                    RemoveView(module, session.Key, session.Value, !session.Key);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            ThrowCleanupErrors(errors);
        }

        /// <summary>只在发现 Unity 对象已销毁时建立死会话列表，并通过统一退出入口清除残留归属。</summary>
        private static void PruneDestroyed(Module module)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            List<KeyValuePair<View, object>> dead = null;
            foreach (var view in module.OwnedViews.Values)
            {
                if (!view)
                {
                    if (dead == null)
                    {
                        dead = new List<KeyValuePair<View, object>>();
                    }

                    dead.Add(new KeyValuePair<View, object>(view, view.Session));
                }
            }

            if (dead != null)
            {
                ClearSessions(module, dead);
            }
        }

        #endregion


    }
}
