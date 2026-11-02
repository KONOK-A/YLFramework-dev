using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <remarks>同身份远端请求共享一个生产者；消费者独立取消，生产者及其输入须等实际工作退出后收尾。</remarks>
    public sealed partial class ResourceSystem
    {
        private readonly Dictionary<string, IRemoteJob> _remoteJobs = new(StringComparer.Ordinal);
        private static readonly List<IRemoteJob> _remoteTicks = new();
        /// <summary>为共享远端作业提供结果交接、消费者存活检查和内容 IO 调度入口。</summary>
        private delegate IEnumerator RemoteOperation<T>(Action<T> complete, Action ensureAlive, Func<Action<CancellationToken>, IEnumerator> runIO);

        /// <summary>只读取得并验证签名/Catalog，产生既有UpdatePlan；不创建store、partial、Prepared或active。</summary>
        /// <param name="cancellation">仅取消本消费者的等待，其他消费者仍可继续共享检查。</param>
        public ResourceRequest<UpdatePlan> CheckRemoteUpdatesAsync(CancellationToken cancellation = default)
        {
            CheckThread();
            if (!CanStartRemote(out var error))
            {
                return Fail(new ResourceRequest<UpdatePlan>(), error.Kind, error.Message);
            }

            var current = StoreSnapshots().ToArray();
            var store = _configuration.ArtifactStore;
            var paths = new List<string>();
            return RemoteRequest<UpdatePlan>("check", (done, alive, io) => store.CheckRemote(current, plan =>
            {
                RequireCheckedFrom(plan);
                done(plan);
            }, alive, io, (project, digest) => paths.Add(store.VersionPath(project, digest))), cancellation, current, paths.AsReadOnly());
        }

        // 用完整转换与授权信封摘要识别共享准备任务，并登记其来源和目标路径保护。
        private ResourceRequest<PreparedUpdate> PrepareRemoteUpdate(UpdatePlan plan, CancellationToken cancellation)
        {
            if (!CanStartRemote(out var error))
            {
                return Fail(new ResourceRequest<PreparedUpdate>(), error.Kind, error.Message);
            }

            var key = "prepare:" + string.Join(";", plan.Projects.OrderBy(x => x.ProjectId, StringComparer.Ordinal).Select(x =>
                x.ProjectId + ":" + x.FromDigest + ":" + x.ToDigest + ":" + (x.Remote == null ? "" : ResourceCatalogCodec.Digest(x.Remote.Envelope))));
            var store = _configuration.ArtifactStore;
            if (plan.Root != store.RootPath)
            {
                return Fail(new ResourceRequest<PreparedUpdate>(), ResourceError.InvalidConfiguration, "The remote plan belongs to another store.");
            }

            var projects = new HashSet<string>(store.Projects.Select(x => x.ProjectId), StringComparer.Ordinal);
            var paths = plan.Projects.Where(x => projects.Contains(x.ProjectId)).SelectMany(x => new[] {
                store.VersionPath(x.ProjectId, x.FromDigest), store.VersionPath(x.ProjectId, x.ToDigest) });
            return RemoteRequest<PreparedUpdate>(key, (done, alive, io) => PrepareRemoteContent(plan, done, alive, io), cancellation,
                plan.Projects.SelectMany(x => new[] { x.From, x.Target }), paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        // 安装后捕获并验证完整输入，再交付准备记录；收据清理失败仍由主体保留责任。
        private IEnumerator PrepareRemoteContent(UpdatePlan plan, Action<PreparedUpdate> complete, Action ensureAlive,
            Func<Action<CancellationToken>, IEnumerator> runIO)
        {
            var store = _configuration.ArtifactStore;
            yield return store.InstallRemotePreparation(plan, ensureAlive, runIO);
            ArtifactStore.ValidationReceipt receipt = null;
            try
            {
                ensureAlive();
                receipt = store.CapturePreparation(plan, _configuration.Decryption, _contentBudget);
                ensureAlive();
                yield return runIO(token => receipt.Validate(token));
                ensureAlive();
                complete(store.CompletePreparation(plan, receipt));
            }
            finally
            {
                var cleanup = ReleaseUpdateValidation(receipt, null);
                if (cleanup != null)
                {
                    throw cleanup;
                }
            }
        }

        // 检查远端入口的会话和 Store 前提，任务身份冲突由共享请求入口另行判断。
        private bool CanStartRemote(out ResourceException error)
        {
            error = !IsAvailable ? new ResourceException(ResourceError.SessionUnavailable, "ResourceSystem is not available.") :
                _configuration.ArtifactStore == null ? new ResourceException(ResourceError.Unsupported, "No ArtifactStore is configured.") :
                _isUpdating ? new ResourceException(ResourceError.Busy, "A version transaction is in progress.") : null;
            return error == null;
        }

        // 相同身份加入既有作业，不同身份返回 Busy；无作业时创建串行远端流水线。
        private ResourceRequest<T> RemoteRequest<T>(string key, RemoteOperation<T> routine, CancellationToken cancellation,
            IEnumerable<ResourceLocalSnapshot> snapshots = null, IReadOnlyList<string> contentPaths = null)
        {
            if (cancellation.IsCancellationRequested)
            {
                var canceled = new ResourceRequest<T>();
                canceled.SetFailure(ResourceRequestStatus.Canceled, new ResourceException(ResourceError.Canceled, "The remote consumer canceled its wait."));
                return canceled;
            }
            if (_remoteJobs.TryGetValue(key, out var existing))
            {
                return existing.IsDone
                ? Fail(new ResourceRequest<T>(), ResourceError.Busy, "The previous remote producer is still closing its canceled IO.")
                : ((RemoteJob<T>)existing).Join(cancellation);
            }
            // 一个store一次只驱动一个网络流水线；重复请求join，文件GET串行，避免另造并发HTTP调度平台。
            if (_remoteJobs.Count != 0)
            {
                return Fail(new ResourceRequest<T>(), ResourceError.Busy, "A different remote update is already in progress.");
            }

            var job = new RemoteJob<T>(this, key, routine, snapshots, contentPaths);
            _remoteJobs.Add(key, job);
            _remoteTicks.Add(job);
            InstallInstanceCleanup();
            return job.Join(cancellation);
        }

        // 通过列表副本推进各主体的远端作业，允许推进过程中完成并移除作业。
        private static void TickRemoteUpdates()
        {
            foreach (var job in _remoteTicks.ToArray())
            {
                job.Tick();
            }
        }

        /// <summary>远端作业调度与观测接口，不暴露消费者的结果类型。</summary>
        private interface IRemoteJob
        {
            /// <summary>生产者是否已结束推进，内容清理可能仍在等待实际 IO 退出。</summary>
            bool IsDone
            {
                get;
            }
            /// <summary>作业仍保护的内容路径，只读视图可随主线程检查追加目标。</summary>
            IReadOnlyList<string> ContentPaths
            {
                get;
            }
            /// <summary>推进一次主线程远端流水线。</summary>
            void Tick();
            /// <summary>复制作业当前阶段与等待者信息。</summary>
            ResourceUpdateDiagnostics CaptureDiagnostics();
        }

        /// <summary>共享一个远端生产流水线，维护独立消费者、嵌套枚举器和内容工作退出责任。</summary>
        /// <typeparam name="T">共享作业交付给各消费者的结果类型。</typeparam>
        private sealed partial class RemoteJob<T> : IRemoteJob
        {
            private readonly ResourceSystem _owner;
            private readonly string _key;
            private readonly Stack<IEnumerator> _stack = new();
            private readonly Dictionary<ResourceRequest<T>, CancellationToken> _waiters = new();
            private T _result;
            private bool _hasResult;
            private bool _isDone;
            private bool _isFinalized;
            private bool _isExecuting;
            private ContentWork _activeContent;
            private Exception _finishFailure;
            private readonly ResourceLocalSnapshot[] _snapshots;
            /// <summary>是否已停止生产推进；不等同于所有后台输入已关闭。</summary>
            public bool IsDone => _isDone;
            /// <summary>作业涉及的受保护版本路径视图，可在远端检查确认目标后追加。</summary>
            public IReadOnlyList<string> ContentPaths
            {
                get;
            }

            /// <summary>保存作业身份和输入保护，并创建尚未推进的生产枚举器。</summary>
            internal RemoteJob(ResourceSystem owner, string key, RemoteOperation<T> routine,
                IEnumerable<ResourceLocalSnapshot> snapshots, IReadOnlyList<string> contentPaths)
            {
                this._owner = owner;
                this._key = key;
                this._snapshots = snapshots?.Where(x => x != null).Distinct().ToArray() ?? Array.Empty<ResourceLocalSnapshot>();
                // 远端Check在主线程收到可信Catalog后追加目标；只读视图让该IO开始前的保护立即可见。
                ContentPaths = contentPaths ?? Array.Empty<string>();
                _stack.Push(routine(value =>
                {
                    _result = value;
                    _hasResult = true;
                }, EnsureAlive, RunIO));
            }

            // 用作业自有内容工作执行磁盘步骤，消费者取消后仍等待其真实退出再释放外层输入。
            private IEnumerator RunIO(Action<CancellationToken> action)
            {
                EnsureAlive();
                var completed = false;
                Exception failure = null;
                _activeContent = _owner.StartContentWork(token =>
                {
                    action(token);
                    return true;
                }, (_, error) =>
                {
                    failure = error;
                    completed = true;
                    _activeContent = null;
                    _owner.RetainContentFailureOwners(error);
                    if (_isDone)
                    {
                        FinishAfterContent(error);
                    }
                }, snapshots: _snapshots);
                try
                {
                    while (!completed)
                    {
                        EnsureAlive();
                        yield return null;
                    }
                    EnsureAlive();
                    if (failure != null)
                    {
                        throw failure;
                    }
                }
                finally
                {
                    if (!completed)
                    {
                        _activeContent?.Cancel();
                    }
                }
            }

            // 主线程逐个结算取消的等待者；会话结束或无人等待才终止共享生产者。
            private void EnsureAlive()
            {
                // 只在主线程观察消费者token；每块复制及发布边界重检共享所有权，不能将任一waiter的token直接交给producer。
                foreach (var waiter in _waiters.ToArray())
                {
                    if (waiter.Value.IsCancellationRequested)
                    {
                        waiter.Key.Cancel();
                    }
                }

                if (!_owner.IsSessionActive || _waiters.Count == 0)
                {
                    throw new OperationCanceledException("The remote producer no longer has an active consumer.");
                }
            }

            /// <summary>加入一份独立等待责任，取消只移除此等待者，最后一个离开才触发生产收尾。</summary>
            internal ResourceRequest<T> Join(CancellationToken cancellation)
            {
                var request = new ResourceRequest<T>();
                _owner.Track(request);
                _waiters.Add(request, cancellation);
                request.Attach(() =>
                {
                    _waiters.Remove(request);
                    if (_waiters.Count == 0 && !_isExecuting)
                    {
                        Finish(null);
                    }
                }, () => _stack.Count != 0 && _stack.Peek() is ResourceTransfer transfer ? transfer.Progress : 0);
                return request;
            }

            /// <summary>在主线程推进最多 64 个嵌套枚举步骤，观察消费者状态并触发终态收尾。</summary>
            public void Tick()
            {
                if (_isDone || _isExecuting)
                {
                    return;
                }

                _isExecuting = true;
                Exception failure = null;
                var settled = false;
                try
                {
                    foreach (var waiter in _waiters.ToArray())
                    {
                        if (waiter.Value.IsCancellationRequested)
                        {
                            waiter.Key.Cancel();
                        }
                    }

                    if (!_owner.IsSessionActive || _waiters.Count == 0)
                    {
                        settled = true;
                    }
                    else
                    {
                        for (var steps = 0; steps < 64 && _stack.Count != 0 && _waiters.Count != 0; steps++)
                        {
                            var routine = _stack.Peek();
                            if (!routine.MoveNext())
                            {
                                _stack.Pop();
                                (routine as IDisposable)?.Dispose();
                                continue;
                            }
                            if (routine.Current is IEnumerator child)
                            {
                                _stack.Push(child);
                                continue;
                            }
                            break;
                        }
                        settled = _stack.Count == 0 || _waiters.Count == 0;
                        if (_stack.Count == 0 && !_hasResult)
                        {
                            throw new InvalidOperationException("Remote update ended without a verified result.");
                        }
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                    settled = true;
                }
                finally
                {
                    _isExecuting = false;
                }
                if (settled)
                {
                    Finish(failure);
                }
            }

            // 标记生产结束；若内容工作仍运行，仅取消它并延迟外层枚举器及 Store 锁释放。
            private void Finish(Exception failure)
            {
                if (_isDone)
                {
                    return;
                }

                _isDone = true;
                _finishFailure = failure;
                // 取消消费者立即终止，但producer的Store锁、枚举器和输入要等实际worker退出再关闭。
                if (_activeContent != null)
                {
                    _activeContent.Cancel();
                    return;
                }
                FinishAfterContent(null);
            }

            // 实际内容工作退出后释放枚举栈和调度登记，再分别交付剩余消费者的终态。
            private void FinishAfterContent(Exception contentFailure)
            {
                if (_isFinalized)
                {
                    return;
                }

                _isFinalized = true;
                var failure = _finishFailure;
                if (failure == null)
                {
                    failure = contentFailure;
                }
                else if (contentFailure != null && contentFailure is not OperationCanceledException && contentFailure is not ResourceException { Kind: ResourceError.Canceled })
                {
                    failure = new AggregateException(failure, contentFailure);
                }

                while (_stack.Count != 0)
                {
                    try
                    {
                        (_stack.Pop() as IDisposable)?.Dispose();
                    }
                    catch (Exception cleanup)
                    {
                        failure = failure == null ? cleanup : new AggregateException(failure, cleanup);
                    }
                }

                _owner.RetainContentFailureOwners(failure);
                _owner._remoteJobs.Remove(_key);
                _remoteTicks.Remove(this);
                var completed = _waiters.ToArray();
                _waiters.Clear();
                foreach (var waiter in completed)
                {
                    if (!_owner.IsSessionActive)
                    {
                        waiter.Key.SetFailure(ResourceRequestStatus.Invalidated, new ResourceException(ResourceError.SessionUnavailable, "The remote update session ended."));
                    }
                    else if (waiter.Value.IsCancellationRequested)
                    {
                        waiter.Key.SetFailure(ResourceRequestStatus.Canceled, new ResourceException(ResourceError.Canceled, "The remote consumer canceled its wait."));
                    }
                    else if (failure != null)
                    {
                        waiter.Key.SetFailure(ResourceRequestStatus.Failed, AsUpdateError(failure));
                    }
                    else
                    {
                        waiter.Key.SetSuccess(_result);
                    }
                }

                _owner.CheckDrain();
                foreach (var waiter in completed)
                {
                    waiter.Key.Notify();
                }

                if (failure != null && failure is not OperationCanceledException && completed.Length == 0)
                {
                    Debug.LogException(failure);
                }
            }
        }
    }
}
