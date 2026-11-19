using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    /// <summary>业务生命周期内的借用入口；同键同类型仅持有一份官方资产引用。</summary>
    public sealed class ResourceScope : IDisposable
    {
        private readonly ResourceSystem _system;
        private readonly Dictionary<(string, Type), object> _entries = new();
        private readonly HashSet<IResourceLease> _leases = new();
        private readonly HashSet<IResourceRequest> _requests = new();
        private readonly HashSet<ResourceRequest<InstanceLease>> _pendingInstances = new();
        private int _pendingAssetWork;
        private IDisposable _pendingInstanceSweep;
        private readonly List<ResourceRequest<bool>> _closeWaiters = new();
        private IDisposable _destroyBinding;
        private GameObject _boundObject;
        private bool _bound;
        private bool _closing;
        private bool _settled;

        /// <summary>调用者明确给出的业务身份。</summary>
        public string OwnerLabel
        {
            get;
        }
        /// <summary>已开始关闭；不表示异步清理已经完成。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        internal ResourceException CloseError
        {
            get; private set;
        }
        internal ResourceScope(ResourceSystem system, string ownerLabel)
        {
            _system = system;
            OwnerLabel = ownerLabel;
        }

        /// <summary>加载并借读，Scope 自动持有结果；相同 Scope 内的重复取得不累积租约。</summary>
        public ResourceRequest<T> LoadAsync<T>(string key, CancellationToken cancellation = default) where T : UnityEngine.Object
        {
            CheckAvailable();
            if (string.IsNullOrWhiteSpace(key))
                return ResourceSystem.Failure<T>(new ArgumentException("An exact key is required."));
            var entryKey = (key, typeof(T));
            if (!_entries.TryGetValue(entryKey, out var value))
            {
                var entry = new Entry<T>(this, key);
                _entries.Add(entryKey, entry);
                var waiting = entry.Wait(cancellation);
                entry.Start();
                return waiting;
            }
            var existing = (Entry<T>)value;
            if (existing.CanReuse)
                return existing.Wait(cancellation);
            existing.ReleaseStale();
            var replacement = new Entry<T>(this, key);
            _entries[entryKey] = replacement;
            var result = replacement.Wait(cancellation);
            replacement.Start();
            return result;
        }

        /// <summary>以冻结的官方 RuntimeKey 借读，不占用 AssetReference 的操作槽。</summary>
        public ResourceRequest<T> LoadAsync<T>(AssetReferenceT<T> reference, CancellationToken cancellation = default) where T : UnityEngine.Object =>
            LoadAsync<T>(ResourceSystem.ReferenceKey(reference), cancellation);

        /// <summary>只借读本 Scope 已经持有的就绪内容，不加载、不增持。</summary>
        public bool TryGetLoaded<T>(string key, out T value) where T : UnityEngine.Object
        {
            CheckAvailable();
            if (_entries.TryGetValue((key, typeof(T)), out var entry) && ((Entry<T>)entry).Ready)
            {
                value = ((Entry<T>)entry).Value;
                return true;
            }
            value = null;
            return false;
        }

        /// <summary>接管一份真实资产租约，不新增引用；重复或跨 Scope 归属明确拒绝。</summary>
        public AssetLease<T> Own<T>(AssetLease<T> lease) where T : UnityEngine.Object
        {
            OwnLease(lease);
            return lease;
        }
        /// <summary>接管整批的官方引用，不增加引用。</summary>
        public AssetBatchLease<T> Own<T>(AssetBatchLease<T> lease) where T : UnityEngine.Object
        {
            OwnLease(lease);
            return lease;
        }
        /// <summary>接管本次实例 checkout。</summary>
        public InstanceLease Own(InstanceLease lease)
        {
            OwnLease(lease);
            return lease;
        }
        /// <summary>接管本次场景的卸载责任。</summary>
        public SceneLease Own(SceneLease lease)
        {
            OwnLease(lease);
            return lease;
        }

        private void OwnLease(IResourceLease lease)
        {
            CheckAvailable();
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));
            if (!ReferenceEquals(lease.System, _system) || lease.Scope != null || lease.IsDisposed)
                throw new InvalidOperationException("The lease is not an independent live responsibility of this service.");
            lease.Scope = this;
            _leases.Add(lease);
        }

        internal void ForgetLease(IResourceLease lease) => _leases.Remove(lease);
        internal void BeginAssetWork() => _pendingAssetWork++;
        internal void EndAssetWork() => _pendingAssetWork--;

        /// <summary>每次新建并由本 Scope 持有，不隐式复用旧组件状态。</summary>
        public ResourceRequest<GameObject> InstantiateAsync(string key, Transform parent = null, Scene scene = default, CancellationToken cancellation = default) =>
            BorrowInstanceAsync(key, nameof(InstantiateAsync), () => _system.InstantiateAsync(key, parent, scene, cancellation));

        /// <summary>显式借用可复用实例；Scope 关闭时归还本次 checkout。</summary>
        public ResourceRequest<GameObject> RentAsync(string key, Transform parent = null, Scene scene = default, CancellationToken cancellation = default) =>
            BorrowInstanceAsync(key, nameof(RentAsync), () => _system.RentAsync(key, parent, scene, cancellation));

        private ResourceRequest<GameObject> BorrowInstanceAsync(string key, string operation,
            Func<ResourceRequest<InstanceLease>> startLeaseRequest)
        {
            CheckAvailable();
            var borrowRequest = Track(new ResourceRequest<GameObject>(), key, operation);
            var leaseRequest = startLeaseRequest();
            _pendingInstances.Add(leaseRequest);
            _pendingInstanceSweep ??= _system.AddTick(PrunePendingInstances);
            borrowRequest.Attach(leaseRequest.Dispose, () => leaseRequest.Progress);
            leaseRequest.Completed += completed =>
            {
                try
                {
                    if (borrowRequest.IsDone || IsDisposed)
                    {
                        return;
                    }
                    if (completed.Status != ResourceRequestStatus.Succeeded)
                    {
                        borrowRequest.SetFailure(completed.Status, completed.Error);
                        return;
                    }
                    var lease = completed.TakeResult();
                    try
                    {
                        Own(lease);
                        borrowRequest.ValidateBeforeTake = () =>
                        {
                            CheckBorrowAvailable();
                            if (!lease.IsValid)
                            {
                                throw new ResourceException(ResourceError.SessionUnavailable,
                                    "The scoped instance checkout ended before delivery: " + key);
                            }
                        };
                        borrowRequest.SetSuccess(lease.GameObject);
                    }
                    catch
                    {
                        lease.Discard();
                        throw;
                    }
                }
                catch (Exception error)
                {
                    ResourceSystem.Fail(borrowRequest, error);
                }
                finally
                {
                    completed.Dispose();
                    borrowRequest.NotifyCompletion();
                }
            };
            return borrowRequest;
        }

        /// <summary>加载并激活本 Scope 拥有的场景；分阶段激活应使用显式 SceneLease。</summary>
        public ResourceRequest<Scene> LoadSceneAsync(string key, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken cancellation = default)
        {
            CheckAvailable();
            var borrowRequest = Track(new ResourceRequest<Scene>(), key, nameof(LoadSceneAsync));
            var leaseRequest = _system.LoadSceneAsync(key, mode, true, 100, cancellation);
            var pendingLease = _system.GetPendingSceneLease(leaseRequest);
            if (pendingLease != null)
            {
                Own(pendingLease);
            }
            borrowRequest.Attach(leaseRequest.Dispose, () => leaseRequest.Progress);
            leaseRequest.Completed += completed =>
            {
                try
                {
                    if (borrowRequest.IsDone || IsDisposed)
                    {
                        return;
                    }
                    if (completed.Status != ResourceRequestStatus.Succeeded)
                    {
                        borrowRequest.SetFailure(completed.Status, completed.Error);
                        return;
                    }
                    var lease = completed.TakeResult();
                    try
                    {
                        if (((IResourceLease)lease).Scope == null)
                        {
                            Own(lease);
                        }
                        else if (!ReferenceEquals(((IResourceLease)lease).Scope, this))
                        {
                            throw new InvalidOperationException("Scene responsibility was transferred to another scope.");
                        }
                        borrowRequest.ValidateBeforeTake = () =>
                        {
                            CheckBorrowAvailable();
                            if (!lease.IsValid)
                            {
                                throw new ResourceException(ResourceError.SceneUnloaded,
                                    "The scoped scene ended before delivery: " + key);
                            }
                        };
                        borrowRequest.SetSuccess(lease.Scene);
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                }
                catch (Exception error)
                {
                    ResourceSystem.Fail(borrowRequest, error);
                }
                finally
                {
                    completed.Dispose();
                    borrowRequest.NotifyCompletion();
                }
            };
            return borrowRequest;
        }

        /// <summary>可选地随真实场景 GameObject 销毁关闭；不随隐藏/停用关闭，也覆盖从未激活的对象。</summary>
        public ResourceScope BindToDestroy(GameObject owner)
        {
            CheckAvailable();
            if (!owner)
            {
                Dispose();
                return this;
            }
            if (!owner.scene.IsValid())
                throw new ArgumentException("Bind only to a scene object, not a prefab asset.", nameof(owner));
            if (_bound && !ReferenceEquals(_boundObject, owner))
                throw new InvalidOperationException("A scope is already bound to another object.");
            if (_bound)
                return this;
            _bound = true;
            _boundObject = owner;
            _destroyBinding = _system.AddTick(() =>
            {
                if (!IsDisposed && !_boundObject)
                {
                    Dispose();
                }
            });
            return this;
        }

        /// <summary>拒绝新工作，依次归还实例、卸载场景、释放资产；每次调用是独立观察者。</summary>
        public ResourceRequest<bool> CloseAsync()
        {
            _system.CheckThread();
            if (_settled)
                return ResourceSystem.Success(true);
            var request = new ResourceRequest<bool>();
            _closeWaiters.Add(request);
            request.Attach(() => _closeWaiters.Remove(request), null);
            if (_closing)
                return request;
            _closing = true;
            IsDisposed = true;
            CloseError = null;
            _destroyBinding?.Dispose();
            _destroyBinding = null;
            _boundObject = null;
            _pendingInstanceSweep?.Dispose();
            _pendingInstanceSweep = null;
            var pending = _requests.ToArray();
            _requests.Clear();
            foreach (var waiting in pending)
                waiting.Dispose();
            _system.Run(CloseRoutine(), FinishClose);
            return request;
        }

        /// <summary>发起异步收尾，不伪装已完成；调用 CloseAsync 可等待或观察失败。</summary>
        public void Dispose()
        {
            var observer = CloseAsync();
            observer.Dispose();
        }

        private IEnumerator CloseRoutine()
        {
            while (_pendingInstances.Any(input => _system.HasPendingInstanceRequest(input)))
                yield return null;
            _pendingInstances.Clear();
            var ordered = _leases.OrderBy(lease => lease is InstanceLease ? 0 : lease is SceneLease ? 1 : 2).ToArray();
            var gates = ordered.OfType<SceneLease>().Where(scene => scene.HasActivationGate).ToArray();
            bool gatesOpened = gates.Length == 0;
            foreach (var lease in ordered)
            {
                if (lease.IsSettled)
                    continue;
                if (!gatesOpened && lease is SceneLease)
                {
                    // Unity 的激活门会阻塞同一原生队列中的其他卸载；只解除本 Scope 的门。
                    foreach (var scene in gates)
                        scene.ReleaseActivationGateForCleanup();
                    foreach (var scene in gates)
                    {
                        while (scene.HasActivationGate && scene.CleanupError == null)
                            yield return null;
                        if (scene.CleanupError != null)
                            throw scene.CleanupError;
                    }
                    gatesOpened = true;
                }
                using var close = lease.CloseAsync();
                yield return close;
                if (close.Status != ResourceRequestStatus.Succeeded)
                    throw close.Error ?? new ResourceException(ResourceError.LoadFailed, "Lease cleanup did not succeed.");
                if (!lease.IsSettled)
                    throw new ResourceException(ResourceError.Busy, "Cleanup has not ended the owned responsibility.");
            }
            while (_pendingAssetWork != 0)
                yield return null;
            _leases.Clear();
            _entries.Clear();
            _settled = true;
            _system.ScopeSettled(this);
            FinishClose(null);
        }

        private void PrunePendingInstances()
        {
            _pendingInstances.RemoveWhere(input => !_system.HasPendingInstanceRequest(input));
            if (_pendingInstances.Count != 0)
                return;
            _pendingInstanceSweep?.Dispose();
            _pendingInstanceSweep = null;
        }

        private void FinishClose(Exception error)
        {
            _closing = false;
            var cause = error == null ? null : ResourceSystem.Wrap(error);
            CloseError = cause;
            var waiters = _closeWaiters.ToArray();
            _closeWaiters.Clear();
            foreach (var request in waiters)
            {
                if (cause == null)
                    request.SetSuccess(true);
                else
                    request.SetFailure(ResourceRequestStatus.Failed, cause);
            }
            foreach (var request in waiters)
                request.NotifyCompletion();
        }

        private ResourceRequest<T> Track<T>(ResourceRequest<T> request, string key, string operation)
        {
            _system.Track(request, key, OwnerLabel, operation);
            _requests.Add(request);
            request.OwnershipEnded += () => _requests.Remove(request);
            request.ValidateBeforeTake = CheckAvailable;
            return request;
        }

        private void CheckAvailable()
        {
            _system.CheckThread();
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(ResourceScope));
            _system.RequireAvailable();
        }

        private void CheckBorrowAvailable()
        {
            CheckAvailable();
            if (_bound && !_boundObject)
                throw new ResourceException(ResourceError.SessionUnavailable, "The scoped borrow owner has been destroyed.");
        }

        private sealed class Entry<T> where T : UnityEngine.Object
        {
            private readonly ResourceScope _scope;
            private readonly string _key;
            private readonly HashSet<ResourceRequest<T>> _waiting = new();
            private ResourceRequest<AssetLease<T>> _input;
            private AssetLease<T> _lease;
            internal bool Ready => _lease != null && _lease.IsValid;
            internal bool CanReuse => Ready || (_input != null && !_input.IsDone);
            internal T Value => _lease.Asset;
            internal Entry(ResourceScope scope, string key)
            {
                _scope = scope;
                _key = key;
            }
            internal void ReleaseStale()
            {
                _lease?.Dispose();
                Remove();
            }

            internal ResourceRequest<T> Wait(CancellationToken token)
            {
                var request = _scope.Track(new ResourceRequest<T>(), _key, nameof(LoadAsync));
                request.ValidateBeforeTake = CheckBorrow;
                if (Ready)
                {
                    _scope._system.BindCancellation(request, token);
                    if (!request.IsDone)
                    {
                        request.SetSuccess(Value);
                        request.NotifyCompletion();
                    }
                    return request;
                }
                _waiting.Add(request);
                request.Attach(() =>
                {
                    _waiting.Remove(request);
                    if (_waiting.Count == 0 && !Ready)
                    {
                        Remove();
                        _input?.Dispose();
                    }
                }, () => _input?.Progress ?? 0);
                _scope._system.BindCancellation(request, token);
                return request;
            }

            internal void Start()
            {
                if (_waiting.Count == 0)
                {
                    Remove();
                    return;
                }
                _input = _scope._system.AcquireAssetAsync<T>(_key, default, _scope);
                _input.Completed += done =>
                {
                    try
                    {
                        if (_scope.IsDisposed || _waiting.Count == 0)
                            return;
                        if (done.Status != ResourceRequestStatus.Succeeded)
                        {
                            Remove();
                            foreach (var request in _waiting.ToArray())
                            {
                                request.SetFailure(done.Status, done.Error);
                                request.NotifyCompletion();
                            }
                            return;
                        }
                        var lease = done.TakeResult();
                        try
                        {
                            _lease = _scope.Own(lease);
                        }
                        catch
                        {
                            lease.Dispose();
                            throw;
                        }
                        foreach (var request in _waiting.ToArray())
                        {
                            if (_scope.IsDisposed || request.IsDone)
                                continue;
                            request.SetSuccess(Value);
                            request.NotifyCompletion();
                        }
                    }
                    catch (Exception error)
                    {
                        Remove();
                        foreach (var request in _waiting.ToArray())
                            ResourceSystem.Fail(request, error);
                    }
                    finally
                    {
                        _waiting.Clear();
                        done.Dispose();
                    }
                };
            }

            private void CheckBorrow()
            {
                _scope.CheckBorrowAvailable();
                if (!Ready)
                    throw new ResourceException(ResourceError.LoadFailed, "The scoped asset ended before delivery: " + _key);
            }

            private void Remove()
            {
                var key = (_key, typeof(T));
                if (_scope._entries.TryGetValue(key, out var entry) && ReferenceEquals(entry, this))
                    _scope._entries.Remove(key);
            }
        }
    }
}
