using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine.SceneManagement;

namespace YLFramework.Core
{
    /// <summary>通过显式 Core provider 加载场景；只管理本 Controller 的在途请求，不拥有成功场景的卸载权。</summary>
    /// <remarks>主线程使用。完成后调用方负责消费/释放原请求；退出会取消并释放仍在途的请求，不能回滚原生 Single。</remarks>
    public sealed class SceneController : Controller
    {
        /// <summary>一笔在途加载的占位与跟踪订阅，身份独立于相同地址的后续请求。</summary>
        private sealed class PendingLoad
        {
            internal readonly (string Project, string Address) Key;
            internal readonly string Context;
            internal CoreResourceRequest<CoreSceneResult> Request;
            internal Action<CoreResourceRequest<CoreSceneResult>> Tracker;
            internal bool IsSubscribed;

            /// <summary>捕获去重键和错误上下文，不在构造时请求后端。</summary>
            internal PendingLoad(string project, string address, LoadSceneMode mode, Module owner)
            {
                Key = (project, address);
                Context = $"Module={owner.ModuleName}, project={project}, address={address}, mode={mode}";
            }
        }

        private readonly Dictionary<(string Project, string Address), PendingLoad> _pending = new();
        private object _registration;
        private int _threadId;

        /// <summary>为本次 Module 注册建立新的身份与线程约束，不延用上次注册的加载意图。</summary>
        public override void OnInit()
        {
            CheckThread();
            if (Module == null || _registration != null)
            {
                throw new InvalidOperationException("SceneController requires a fresh Module registration.");
            }

            _threadId = Thread.CurrentThread.ManagedThreadId;
            _registration = new object();
        }

        /// <summary>使用当前 Module.ProjectName；address 必须是后端目录中登记的地址或显式别名。</summary>
        public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string address, LoadSceneMode mode = LoadSceneMode.Single)
        {
            var owner = RequireOwner();
            var stamp = _registration;
            var project = owner.ProjectName;
            EnsureRegistration(stamp, owner);
            return LoadSceneAsync(project, address, mode);
        }

        /// <summary>提交新的加载意图；相同项目/地址在本实例仍在途时拒绝，不跨 owner 合并或按短名复用场景。</summary>
        public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address,
            LoadSceneMode mode = LoadSceneMode.Single)
        {
            var owner = RequireOwner();
            var stamp = _registration;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new ArgumentException("A resource project is required.", nameof(projectId));
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("A scene address is required.", nameof(address));
            }

            if (mode != LoadSceneMode.Single && mode != LoadSceneMode.Additive)
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported scene load mode.");
            }

            var entry = new PendingLoad(projectId, address, mode, owner);
            var provider = CoreResourceAccess.Provider ?? throw new CoreResourceException(CoreResourceError.Unavailable,
                "Scene loading requires an explicitly bound resource provider. " + entry.Context);
            if (_pending.TryGetValue(entry.Key, out var previous) && previous.Request != null &&
                (previous.Request.IsDisposed || previous.Request.Status != CoreResourceStatus.Pending))
            {
                Detach(previous);
            }

            EnsureRegistration(stamp, owner);
            if (_pending.ContainsKey(entry.Key))
            {
                throw new CoreResourceException(CoreResourceError.InProgress, "A scene load is already pending. " + entry.Context);
            }

            // 后端和 Completed 订阅都可能同步重入：先占位，后附请求，再订阅。
            _pending.Add(entry.Key, entry);
            try
            {
                entry.Request = provider.LoadSceneAsync(projectId, address, mode) ??
                    throw new InvalidOperationException("The scene provider returned null. " + entry.Context);
                EnsureRegistration(stamp, owner);
                if (!_pending.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                {
                    throw new InvalidOperationException("The scene load reservation was canceled. " + entry.Context);
                }

                entry.Tracker = _ => Detach(entry);
                entry.IsSubscribed = true;
                entry.Request.Completed += entry.Tracker;
                EnsureRegistration(stamp, owner);
                if (entry.Request.IsDisposed)
                {
                    throw new InvalidOperationException("The scene request was disposed before delivery. " + entry.Context);
                }

                return entry.Request;
            }
            catch (Exception error)
            {
                error.Data["YLFramework.SceneController.Context"] = entry.Context;
                var cleanup = new List<Exception>();
                Release(entry, false, cleanup);
                if (cleanup.Count == 0)
                {
                    throw;
                }

                cleanup.Insert(0, error);
                throw new AggregateException("Scene load and cleanup failed. " + entry.Context, cleanup);
            }
        }

        /// <summary>结束调用时的在途快照；取消回调中新提交的请求不属于旧快照。逐项收尾，失败汇总报告。</summary>
        public void CancelPendingLoads()
        {
            CheckThread();
            if (_pending.Count == 0)
            {
                return;
            }

            var snapshot = new List<PendingLoad>(_pending.Values);
            _pending.Clear();
            var errors = new List<Exception>();
            foreach (var entry in snapshot)
            {
                Release(entry, true, errors);
            }

            if (errors.Count != 0)
            {
                throw new AggregateException("SceneController request cleanup failed.", errors);
            }
        }

        /// <summary>先使本次注册失效，再取消在途请求；已交付的完成结果仍由调用方负责。</summary>
        public override void OnExit()
        {
            CheckThread();
            _registration = null;
            CancelPendingLoads();
        }

        /// <summary>在首次注册已确定线程后，拒绝从其他线程操作加载跟踪状态。</summary>
        private void CheckThread()
        {
            if (_threadId != 0 && _threadId != Thread.CurrentThread.ManagedThreadId)
            {
                throw new InvalidOperationException("SceneController must run on its Module registration thread.");
            }
        }

        /// <summary>取得仍可接收业务对象的有效归属，并拒绝未注册或正在退出的 Controller。</summary>
        private Module RequireOwner()
        {
            CheckThread();
            var owner = Module;
            if (_registration == null || owner == null || IsExiting)
            {
                throw new InvalidOperationException("SceneController is not in an active Module registration.");
            }

            owner.EnsureCanAddObjects();
            return owner;
        }

        /// <summary>跨越可能重入的后端或订阅调用后，确认仍属于接受请求时的同一次注册。</summary>
        private void EnsureRegistration(object stamp, Module owner)
        {
            if (!ReferenceEquals(_registration, stamp) || !ReferenceEquals(Module, owner) || IsExiting)
            {
                throw new InvalidOperationException("The scene Controller registration changed during loading. Module=" + owner.ModuleName);
            }

            owner.EnsureCanAddObjects();
        }

        /// <summary>仅移除匹配身份的占位并退订跟踪回调，不取消请求或消费结果。</summary>
        private void Detach(PendingLoad entry)
        {
            if (_pending.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                _pending.Remove(entry.Key);
            }

            if (!entry.IsSubscribed)
            {
                return;
            }

            entry.IsSubscribed = false;
            entry.Request.Completed -= entry.Tracker;
        }

        /// <summary>解除跟踪后按策略取消并释放本笔请求，将各步失败写入共享错误列表。</summary>
        private void Release(PendingLoad entry, bool pendingOnly, List<Exception> errors)
        {
            try
            {
                Detach(entry);
            }
            catch (Exception error)
            {
                error.Data["YLFramework.SceneController.Context"] = entry.Context;
                errors.Add(error);
            }
            var request = entry.Request;
            if (request == null || request.IsDisposed || (pendingOnly && request.Status != CoreResourceStatus.Pending))
            {
                return;
            }

            try
            {
                request.Cancel();
            }
            catch (Exception error)
            {
                error.Data["YLFramework.SceneController.Context"] = entry.Context;
                errors.Add(error);
            }
            try
            {
                request.Dispose();
            }
            catch (Exception error)
            {
                error.Data["YLFramework.SceneController.Context"] = entry.Context;
                errors.Add(error);
            }
        }
    }
}
