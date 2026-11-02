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

    /// <summary>协调 View 的取得、显式挂载、会话索引及退出；实例租约与 Model 借用分别管理。</summary>
    /// <remarks>场景卸载和销毁扫描只清理已纳管的失效会话，不自动接管场景中的其他对象。</remarks>
    internal partial class ViewManager
    {
        private static readonly HashSet<Module> _sceneOwners = new HashSet<Module>();
        private static readonly List<Module> _pruneOwners = new List<Module>();
        private static IUnregister _pruneSubscription;
        private static object _pruneRound;
        // 同步初始化调用栈的当前事务身份，仅用于区分 UI 自身准备失败与其他窗口观察异常。
        internal static KeyValuePair<View, object> InitializingView;

        /// <summary>注册执行会话重置入口，使禁用 Domain Reload 时也能撤销旧场景跟踪。</summary>
        static ViewManager() => SingletonSessionState.RegisterSessionReset(ResetSession);

        /// <summary>结束旧扫描订阅并清空跟踪状态，不替代各模块的正常关闭流程。</summary>
        private static void ResetSession()
        {
            EndPruneRound();
            _sceneOwners.Clear();
            _pruneOwners.Clear();
            InitializingView = default;
        }

        /// <summary>记录场景位置并按需开启共享的卸载监听与销毁扫描，不改变模块归属。</summary>
        private static void TrackScene(Module module, View view)
        {
            view.SceneHandle = view.gameObject.scene.handle;
            if (_sceneOwners.Count == 0)
            {
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                var round = _pruneRound = new object();
                _pruneSubscription = UpdateManager.AddUpdateEvent(() =>
                {
                    if (ReferenceEquals(_pruneRound, round))
                    {
                        PruneDestroyedSessions(round);
                    }
                });
            }
            _sceneOwners.Add(module);
        }

        /// <summary>场景卸载后复查所有受管会话，保留迁移后仍存活的对象并清理已销毁对象。</summary>
        private static void OnSceneUnloaded(Scene scene)
        {
            var errors = new List<Exception>();
            foreach (var module in new List<Module>(_sceneOwners))
            {
                foreach (var entry in Snapshot(module))
                {
                    var view = entry.Key;
                    // 迁移及DDOL不改变owner；死对象只能使用保存的会话及实例身份。
                    if (view)
                    {
                        view.SceneHandle = view.gameObject.scene.handle;
                    }
                    else
                    {
                        try
                        {
                            RemoveView(module, view, entry.Value, true);
                        }
                        catch (Exception error)
                        {
                            errors.Add(error);
                        }
                    }
                }
            }

            if (errors.Count != 0)
            {
                Debug.LogException(new AggregateException($"View cleanup after scene unload ({scene.handle}) failed.", errors).Flatten());
            }
        }


    }
}
