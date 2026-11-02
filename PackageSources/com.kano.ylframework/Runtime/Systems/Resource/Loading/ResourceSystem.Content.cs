using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private static readonly List<ResourceSystem> _contentCleanup = new();
        private readonly List<ContentWork> _contentWork = new();
        private readonly List<(IDisposable Owner, Exception Error)> _retainedContentCleanup = new();
        private readonly ConcurrentQueue<(BundleRecord Record, Exception Error)> _contentFaults = new();
        private readonly ResourceContentBudget _contentBudget;
        private ContentWork _initializationWork;
        private int _liveContentInputs;

        /// <summary>一份后台纯内容工作及其主线程交割责任，取消后仍等待实际完成。</summary>
        internal sealed class ContentWork
        {
            internal Task Task;
            internal CancellationTokenSource Cancellation;
            internal Action Complete;
            internal bool IsCompleting;
            internal ResourceLocalSnapshot[] Snapshots;
            /// <summary>请求后台工作取消，不移除任务或提前释放其输入责任。</summary>
            internal void Cancel() => Cancellation?.Cancel();
        }

        /// <summary>只在主线程登记/交割；worker不访问Unity、应用provider或Current。</summary>
        private ContentWork StartContentWork<T>(Func<CancellationToken, T> work, Action<T, Exception> completed,
            CancellationToken cancellation = default, IEnumerable<ResourceLocalSnapshot> snapshots = null)
        {
            CheckThread();
            var job = new ContentWork
            {
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation),
                Snapshots = snapshots?.Where(x => x != null).Distinct().ToArray() ?? Array.Empty<ResourceLocalSnapshot>()
            };
            var token = job.Cancellation.Token;
            var task = new Task<T>(() => work(token), CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            job.Task = task;
            job.Complete = () =>
            {
                T result = default;
                Exception failure = null;
                try
                {
                    result = task.GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    failure = error;
                }
                completed(result, failure);
            };
            _contentWork.Add(job);
            try
            {
                RegisterContentCleanup();
                task.Start(TaskScheduler.Default);
            }
            catch
            {
                _contentWork.Remove(job);
                job.Cancellation.Dispose();
                throw;
            }
            return job;
        }

        /// <summary>登记本系统的内容清理责任并确保统一帧驱动已安装。</summary>
        private void RegisterContentCleanup()
        {
            if (!_contentCleanup.Contains(this))
            {
                _contentCleanup.Add(this);
            }

            InstallInstanceCleanup();
        }

        /// <summary>在主线程交割已完成后台任务，采集输入故障并检查原系统是否排空。</summary>
        private static void TickContentWork()
        {
            for (var ownerIndex = _contentCleanup.Count - 1; ownerIndex >= 0; ownerIndex--)
            {
                if (ownerIndex >= _contentCleanup.Count)
                {
                    continue;
                }

                var owner = _contentCleanup[ownerIndex];
                for (var i = owner._contentWork.Count - 1; i >= 0; i--)
                {
                    if (i >= owner._contentWork.Count)
                    {
                        continue;
                    }

                    var job = owner._contentWork[i];
                    if (job.IsCompleting || !job.Task.IsCompleted)
                    {
                        continue;
                    }

                    job.IsCompleting = true;
                    // 完成回调及其输入交割也属于in-flight，重入不能先把旧主体判为已排空。
                    try
                    {
                        job.Complete();
                    }
                    catch (Exception error)
                    {
                        Debug.LogException(error);
                    }
                    finally
                    {
                        job.Cancellation.Dispose();
                        job.Cancellation = null;
                        job.Complete = null;
                        owner._contentWork.Remove(job);
                    }
                }
                for (var i = 0; i < 8 && owner._contentFaults.TryDequeue(out var fault); i++)
                {
                    if (fault.Record.State != BundleState.Released)
                    {
                        fault.Record.Error ??= ResourceContentValidation.Failure(fault.Error, "An authenticated Bundle input faulted; new dependency-closure acquisitions are refused.");
                    }
                }

                if (owner._contentWork.Count == 0 && owner._retainedContentCleanup.Count == 0 && owner._liveContentInputs == 0 && owner._contentFaults.IsEmpty)
                {
                    _contentCleanup.Remove(owner);
                }

                owner.CheckDrain();
            }
        }

        /// <summary>请求本系统的全部后台内容任务取消，不提前移走其清理账本。</summary>
        private void CancelContentWork()
        {
            foreach (var job in _contentWork)
            {
                job.Cancel();
            }
        }

        /// <summary>保留清理失败对象及错误，阻止未决责任被误判为已排空。</summary>
        private void RetainContentCleanup(IDisposable owner, Exception error)
        {
            if (_retainedContentCleanup.Any(x => ReferenceEquals(x.Owner, owner)))
            {
                return;
            }

            _retainedContentCleanup.Add((owner, error));
            RegisterContentCleanup();
            Debug.LogException(new ResourceException(ResourceError.LoadFailed,
                "Content cleanup failed; ownership is retained and no unconditional native retry is performed.", error));
        }

        /// <summary>沿异常链和聚合异常提取仍拥有未完成内容清理的对象。</summary>
        private void RetainContentFailureOwners(Exception error)
        {
            if (error == null)
            {
                return;
            }

            if (error is ResourceContentCleanupException cleanup)
            {
                RetainContentCleanup(cleanup.Owner, cleanup);
            }

            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    RetainContentFailureOwners(inner);
                }
            }
            else
            {
                RetainContentFailureOwners(error.InnerException);
            }
        }

        /// <summary>对 Bundle 资产检查其依赖闭包健康状况，非 Bundle 条目无需此检查。</summary>
        internal void RequireContentAdmission(ResourceCatalogEntry entry)
        {
            if (entry.Snapshot != null && entry.Descriptor != null)
            {
                RequireHealthyBundle(entry.Snapshot, entry.Descriptor.BundleId);
            }
        }

        /// <summary>遍历目标依赖闭包，拒绝正在卸载、清理未决或已故障的输入。</summary>
        private void RequireHealthyBundle(ResourceLocalSnapshot snapshot, string bundleId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(bundleId);
            while (pending.Count != 0)
            {
                var id = pending.Pop();
                if (!visited.Add(id))
                {
                    continue;
                }

                if (_bundleRecords.TryGetValue((snapshot.Document.CatalogDigest, id), out var record))
                {
                    if (record.IsCollecting)
                    {
                        throw new ResourceException(ResourceError.InProgress, "A Bundle input is being unloaded.");
                    }

                    if (record.CleanupError != null)
                    {
                        throw new ResourceException(ResourceError.LoadFailed, "A Bundle input has an unresolved cleanup failure.", record.CleanupError);
                    }

                    var error = record.Error ?? (record.Input?.Fault == null ? null :
                        ResourceContentValidation.Failure(record.Input.Fault, "A Bundle dependency input is faulted: " + id));
                    if (error != null)
                    {
                        throw error;
                    }
                }
                foreach (var dependency in snapshot.Bundles[id].Dependencies)
                {
                    pending.Push(dependency);
                }
            }
        }

        /// <summary>按内部有效图根租约检查其所属快照的依赖闭包。</summary>
        internal void RequireHealthyBundle(BundleLease lease)
            => RequireHealthyBundle(lease.Record.Snapshot, lease.Record.Descriptor.LogicalId);

        /// <summary>结算一次共享初始化，为全部仍等待的消费者设置终态后统一通知。</summary>
        private void FinishInitialization(Exception error)
        {
            _initializationWork = null;
            if (error == null && !IsSessionActive)
            {
                error = new ResourceException(ResourceError.SessionUnavailable, "Initialization session ended before authentication completed.");
            }

            if (error == null && _initialization.Count == 0)
            {
                error = new OperationCanceledException("Initialization no longer has any waiting consumer.");
            }

            var joined = _initialization.ToArray();
            _initialization.Clear();
            if (error == null)
            {
                State = ResourceSystemState.Ready;
                foreach (var item in joined)
                {
                    item.SetSuccess(this);
                }
            }
            else
            {
                RetainContentFailureOwners(error);
                var failure = error as ResourceException ?? ResourceContentValidation.Failure(error, "Resource initialization failed.");
                if (State != ResourceSystemState.Invalidated)
                {
                    State = ResourceSystemState.Failed;
                }

                if (ReferenceEquals(_authority, this))
                {
                    _authority = null;
                }

                if (_contentWork.Count != 0 || _retainedContentCleanup.Count != 0)
                {
                    _draining.Add(this);
                }

                foreach (var item in joined)
                {
                    item.SetFailure(failure.Kind == ResourceError.Canceled ? ResourceRequestStatus.Canceled : ResourceRequestStatus.Failed, failure);
                }
            }
            foreach (var item in joined)
            {
                item.Notify();
            }
        }

        /// <summary>加入独立初始化等待者，最后一人退出时请求取消后台初始化工作。</summary>
        private void JoinInitialization(ResourceRequest<ResourceSystem> request)
        {
            _initialization.Add(request);
            request.Attach(() =>
            {
                _initialization.Remove(request);
                if (_initialization.Count == 0)
                {
                    _initializationWork?.Cancel();
                }
            }, () => 0);
        }

        /// <summary>验证已挂载配置并捕获内容输入，按需后台认证后完成初始化。</summary>
        private void ContinueInitialization(IReadOnlyList<ResourceLocalSnapshot> mounted)
        {
            ResourceContentValidation validation = null;
            try
            {
                if (!IsSessionActive)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable, "Initialization session ended.");
                }

                try
                {
                    ValidateConfiguration(mounted);
                }
                catch (Exception error)
                {
                    throw error as ResourceException ?? new ResourceException(ResourceError.InvalidConfiguration, "Resource configuration validation failed.", error);
                }
                var snapshots = _catalogs.Values.Select(x => x.Snapshot).Where(x => x != null).Distinct().ToArray();
                validation = ResourceContentValidation.Capture(snapshots, _configuration.Decryption, true, _contentBudget);
                if (!IsSessionActive)
                {
                    throw new ResourceException(ResourceError.SessionUnavailable, "Initialization was invalidated by the key provider.");
                }

                if (!validation.HasEncryptedContent)
                {
                    validation.Dispose();
                    FinishInitialization(null);
                    return;
                }
                var owned = validation;
                _initializationWork = StartContentWork(token =>
                {
                    owned.Validate(token);
                    return true;
                }, (_, error) =>
                {
                    if (error == null)
                    {
                        try
                        {
                            owned.RequireStable();
                        }
                        catch (Exception cause)
                        {
                            error = cause;
                        }
                    }

                    try
                    {
                        owned.Dispose();
                    }
                    catch (Exception cleanup)
                    {
                        RetainContentCleanup(owned, cleanup);
                        error = error == null ? cleanup : new AggregateException(error, cleanup);
                    }
                    FinishInitialization(error);
                }, snapshots: snapshots);
            }
            catch (Exception error)
            {
                if (validation != null)
                {
                    try
                    {
                        validation.Dispose();
                    }
                    catch (Exception cleanup)
                    {
                        RetainContentCleanup(validation, cleanup);
                        error = new AggregateException(error, cleanup);
                    }
                }

                FinishInitialization(error);
            }
        }
    }
}
