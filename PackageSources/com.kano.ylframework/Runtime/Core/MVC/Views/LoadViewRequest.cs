using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Core
{
    /// <summary>单次 View 加载请求的状态，与交付后 View 自身的会话状态分离。</summary>
    public enum LoadViewStatus
    {
        /// <summary>尚未提交终态，包含等待、同步初始化及其回滚阶段。</summary>
        Pending,
        /// <summary>加载已成功提交；之后关闭 View 不会改写请求结果。</summary>
        Succeeded,
        /// <summary>资源、初始化或收尾发生错误，具体原因见请求的 Exception。</summary>
        Failed,
        /// <summary>调用方通过 Abort 逻辑取消本次请求，不代表共享后端加载已停止。</summary>
        Canceled,
        /// <summary>所属模块、父会话或目标场景等接受请求时的上下文已经失效。</summary>
        Invalidated
    }

    /// <summary>将模块关闭时的输入释放与完成通知分开，允许关闭管线先撤销全部业务归属。</summary>
    internal interface IPendingViewRequest
    {
        /// <summary>标记所属模块失效并释放输入，暂不派发完成通知。</summary>
        void Invalidate();
        /// <summary>在模块收尾后提交此前保存的失效结果。</summary>
        void PublishInvalidation();
    }

    /// <summary>主线程表现请求；Abort仅取消Core业务结果，不停止或卸载共享后端加载。</summary>
    /// <typeparam name="T">要求根 View 可赋给的组件类型。</typeparam>
    public sealed class LoadViewRequest<T> : YLFramework.Async.CustomAsyncOperation<LoadViewRequest<T>>, IPendingViewRequest where T : View
    {
        /// <summary>请求交付的实例引用；是否成功以 Status 为准，之后关闭该实例不会清空此引用。</summary>
        public T View
        {
            get; private set;
        }
        /// <summary>本次请求的稳定结果状态；初始化回调中的 Abort 要等事务回滚后才离开 Pending。</summary>
        public LoadViewStatus Status
        {
            get; private set;
        }
        /// <summary>原始失败原因；附加收尾错误保存在Data["YLFramework.Core.CleanupException"]。</summary>
        public Exception Exception
        {
            get; private set;
        }

        private Module _owner;
        private object _lifecycle;
        private string _assetName;
        private List<string> _projects;
        private ICoreResourceProvider _provider;
        private CoreResourceRequest<CorePrefabLease> _resourceRequest;
        private CorePrefabLease _prefabLease;
        private Transform _parent;
        private bool _hasParent;
        private Scene _targetScene;
        private readonly List<KeyValuePair<View, object>> _parentSessions = new List<KeyValuePair<View, object>>();
        private LoadViewArgs _args;
        private object[] _param;
        private GameObject _directPrefab;
        private bool _hasDirectPrefab;
        private bool _isExecuting;
        private bool _isClosingOwner;
        private bool _isTerminal;
        private Exception _interruption;
        private LoadViewStatus _interruptedStatus;
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>捕获模块、父会话及资源查找上下文，接收已接管参数并登记退出监听，不立即加载实例。</summary>
        internal LoadViewRequest(Module module, string name, Transform target, LoadViewArgs claimedArgs, object[] parameters, GameObject prefab = null)
        {
            _owner = module;
            _lifecycle = module.ViewLifecycle;
            _assetName = name;
            _projects = ModuleManager.ViewResourceProjects(module);
            _parent = target;
            _hasParent = !ReferenceEquals(target, null);
            _targetScene = _hasParent ? target.gameObject.scene : SceneManager.GetActiveScene();
            for (var ancestor = target; ancestor; ancestor = ancestor.parent)
            {
                var view = ancestor.GetComponent<View>();
                if (view && view.Session != null)
                {
                    _parentSessions.Add(new KeyValuePair<View, object>(view, view.Session));
                }
            }
            _args = claimedArgs;
            _param = parameters;
            _directPrefab = prefab;
            _hasDirectPrefab = !ReferenceEquals(prefab, null);
            _provider = _hasDirectPrefab ? null : CoreResourceAccess.Provider;
            Validate();
            _owner.PendingViews.Add(this);
            Application.quitting += OnExecutionEnded;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        }

        /// <summary>执行会话结束时主动提交失效结果，不依赖 Unity 停止协程时执行迭代器的 finally。</summary>
        private void OnExecutionEnded()
            => Finish(null, new InvalidOperationException("View execution session has ended."), LoadViewStatus.Invalidated);

#if UNITY_EDITOR
        /// <summary>离开 Play Mode 时复用执行结束入口，释放尚未完成请求的监听与输入。</summary>
        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                OnExecutionEnded();
            }
        }
#endif

        /// <summary>复检接受请求时的模块和父会话身份，并确认目标场景仍可承载实例。</summary>
        private void Validate()
        {
            if (_interruption != null)
            {
                throw _interruption;
            }

            if (_isTerminal)
            {
                throw new InvalidOperationException("View request has already completed.");
            }

            if (_hasDirectPrefab && !_directPrefab)
            {
                throw new InvalidOperationException("View prefab has been destroyed.");
            }

            ViewManager.EnsureOwner(_owner);
            if (!ReferenceEquals(_owner.ViewLifecycle, _lifecycle))
            {
                throw new InvalidOperationException("View owner lifecycle has ended.");
            }

            if (_hasParent)
            {
                if (!_parent)
                {
                    throw new InvalidOperationException("View parent has been destroyed.");
                }

                foreach (var entry in _parentSessions)
                {
                    if (!entry.Key || !ReferenceEquals(entry.Key.Session, entry.Value))
                    {
                        throw new InvalidOperationException("View parent session has ended.");
                    }
                }
                // 有父节点时跟随其合法场景迁移；无父节点时仍使用接受请求时捕获的场景。
                _targetScene = _parent.gameObject.scene;
            }
            if (!_targetScene.IsValid() || !_targetScene.isLoaded)
            {
                throw new InvalidOperationException("View target scene has unloaded.");
            }
        }

        /// <summary>等待期立即结束；同步初始化中的重入取消在该事务回滚后通知。</summary>
        public override void Abort()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("View request Abort must run on its accepting thread.");
            }

            if (_isTerminal || _interruption != null)
            {
                return;
            }

            _interruption = new OperationCanceledException("View request canceled.");
            _interruptedStatus = LoadViewStatus.Canceled;
            if (!_isExecuting)
            {
                Finish(null, _interruption, _interruptedStatus);
            }
        }

        /// <summary>记录所属生命周期失效并释放输入责任，终态通知留待所属模块完成清理后发布。</summary>
        void IPendingViewRequest.Invalidate()
        {
            if (_isTerminal)
            {
                return;
            }

            _isClosingOwner = true;
            _interruption = new InvalidOperationException("View owner lifecycle has ended.");
            _interruptedStatus = LoadViewStatus.Invalidated;
            try
            {
                ReleaseInputs();
            }
            catch (Exception cleanupError)
            {
                StartUpModuleRequest.AttachCleanupError(_interruption, cleanupError);
                throw;
            }
        }

        /// <summary>在所属模块清理后发布已记录的失效终态。</summary>
        void IPendingViewRequest.PublishInvalidation()
        {
            _isClosingOwner = false;
            Finish(null, _interruption, _interruptedStatus);
        }

        /// <summary>先撤销监听和持有引用，再释放本请求的资源租约及参数；不接管已交付实例的退出。</summary>
        private void ReleaseInputs()
        {
            Application.quitting -= OnExecutionEnded;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
#endif
            if (_owner != null)
            {
                _owner.PendingViews.Remove(this);
            }

            var previousArgs = _args;
            var previousRequest = _resourceRequest;
            var previousPrefab = _prefabLease;
            _resourceRequest = null;
            _prefabLease = null;
            _provider = null;
            _args = null;
            _owner = null;
            _lifecycle = null;
            _parent = null;
            _parentSessions.Clear();
            _projects = null;
            _assetName = null;
            _param = null;
            _directPrefab = null;
            _targetScene = default;
            try
            {
                previousRequest?.Dispose();
            }
            finally
            {
                try
                {
                    previousPrefab?.Dispose();
                }
                finally
                {
                    if (previousArgs != null)
                    {
                        previousArgs.Release();
                    }
                }
            }
        }

        /// <summary>至多提交一次终态，在释放输入后通知完成；通知异常只记录，不改写已提交结果。</summary>
        private void Finish(T result, Exception failure, LoadViewStatus status)
        {
            if (_isTerminal || _isClosingOwner)
            {
                return;
            }

            _isTerminal = true;
            try
            {
                ReleaseInputs();
            }
            catch (Exception cleanupError)
            {
                if (failure != null)
                {
                    StartUpModuleRequest.AttachCleanupError(failure, cleanupError);
                }
                else
                {
                    failure = cleanupError;
                    status = LoadViewStatus.Failed;
                }
            }
            View = result;
            Exception = failure;
            Status = status;
            try
            {
                Complete(failure?.ToString());
            }
            catch (Exception notificationError)
            {
                Debug.LogException(notificationError);
            }
        }

        /// <summary>推进加载迭代器并记录同步执行边界，使回调中的取消等待初始化回滚后再通知。</summary>
        internal IEnumerator Run()
        {
            var routine = Load();
            Exception failure = null;
            try
            {
                while (!_isTerminal && !_isClosingOwner)
                {
                    bool next;
                    _isExecuting = true;
                    try
                    {
                        next = routine.MoveNext();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }
                    finally
                    {
                        _isExecuting = false;
                    }
                    if (!next)
                    {
                        break;
                    }

                    yield return routine.Current;
                }
            }
            finally
            {
                (routine as IDisposable)?.Dispose();
            }
            if (!_isTerminal && !_isClosingOwner)
            {
                Finish(null, failure ?? _interruption ?? new InvalidOperationException("View producer stopped without a result."),
                    _interruption != null && (failure == null || ReferenceEquals(failure, _interruption))
                        ? _interruptedStatus : LoadViewStatus.Failed);
            }
        }

        /// <summary>从下一帧开始取得 Prefab，按允许的错误类别回退项目，最后同步初始化并交付实例。</summary>
        private IEnumerator Load()
        {
            GameObject prefab = _directPrefab;
            Exception resourceError = null;
            // 即时完成的后端与直接 Prefab 同样保留接受/取消边界。
            yield return null;
            ValidateWaiting();
            if (!_hasDirectPrefab)
            {
                CoreResourceAccess.RequireProvider(_provider);
            }

            for (int i = 0; !prefab && i < _projects.Count; i++)
            {
                var project = _projects[i];
                Validate();
                CoreResourceRequest<CorePrefabLease> acquired;
                try
                {
                    acquired = _provider.AcquirePrefabAsync(project, _assetName);
                }
                catch (Exception error) when (CoreResourceAccess.CanFallback(error))
                {
                    resourceError = error;
                    continue;
                }
                // Provider/Source 可同步重入关闭 owner，返回值仍须由本调用接管或归还。
                try
                {
                    ValidateWaiting();
                }
                catch (Exception error)
                {
                    try
                    {
                        acquired?.Dispose();
                    }
                    catch (Exception cleanup)
                    {
                        StartUpModuleRequest.AttachCleanupError(error, cleanup);
                    }
                    throw;
                }
                _resourceRequest = acquired;
                var request = _resourceRequest ?? throw new InvalidOperationException("View asset request is null.");
                while (request.Status == CoreResourceStatus.Pending)
                {
                    ValidateWaiting();
                    Progress = request.Progress;
                    yield return null;
                }
                ValidateWaiting();
                if (request.Status != CoreResourceStatus.Succeeded)
                {
                    resourceError = request.Error ?? new CoreResourceException(CoreResourceError.LoadFailed, $"View asset load failed: {project}/{_assetName}.");
                    request.Dispose();
                    _resourceRequest = null;
                    if (request.Status == CoreResourceStatus.Canceled || request.Status == CoreResourceStatus.Invalidated ||
                        !CoreResourceAccess.CanFallback(resourceError))
                    {
                        throw resourceError;
                    }

                    continue;
                }
                _prefabLease = request.TakeResult();
                request.Dispose();
                _resourceRequest = null;
                prefab = _prefabLease.Prefab;
                if (!prefab)
                {
                    throw new InvalidOperationException($"View prefab is no longer valid: {project}/{_assetName}.");
                }
            }
            if (!prefab)
            {
                throw new InvalidOperationException($"View prefab not found: {_assetName}.", resourceError);
            }

            ValidateWaiting();
            var result = ViewManager.CompleteAsync<T>(_owner, prefab, _assetName, _parent, _targetScene, _args, _param, ValidateWaiting, _prefabLease);
            // 初始化已结束再提交；完成通知可以关闭或开启下一笔会话。
            _isExecuting = false;
            Finish(result, null, LoadViewStatus.Succeeded);
        }

        /// <summary>将上下文校验失败记为首次失效中断，同时保留原异常交给初始化或等待管线回滚。</summary>
        private void ValidateWaiting()
        {
            try
            {
                Validate();
            }
            catch (Exception error)
            {
                if (_interruption == null)
                {
                    _interruption = error;
                    _interruptedStatus = LoadViewStatus.Invalidated;
                }
                throw;
            }
        }

        internal void FailToStart(Exception error) => Finish(null, error, LoadViewStatus.Failed);
    }
}
