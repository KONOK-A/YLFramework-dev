using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Core;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private CoreResourceProvider _coreProvider;

        /// <summary>应用在显式初始化后、启动 Module 前装配。成功关闭或会话退出时仅解绑自己的绑定。</summary>
        /// <returns>借用当前资源权威的 Core 适配器；绑定本身不增加资产或实例持有责任。</returns>
        public CoreResourceProvider BindCoreResourceProvider()
        {
            CheckThread();
            if (!IsAvailable)
            {
                throw new ResourceException(ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            _coreProvider ??= new CoreResourceProvider(this);
            CoreResourceAccess.Bind(_coreProvider);
            return _coreProvider;
        }

        /// <summary>按引用身份解绑本会话的 Core 适配器，不清除后来显式替换的外部绑定。</summary>
        private void UnbindCoreResourceProvider()
        {
            CoreResourceAccess.Unbind(_coreProvider);
            _coreProvider = null;
        }
    }

    /// <summary>Core 的唯一正式资源后端；只消费显式资源配置，不持有第二份资源表或实例池。</summary>
    public sealed class CoreResourceProvider : ICoreResourceProvider
    {
        private readonly ResourceSystem _owner;
        /// <summary>借用已初始化的唯一资源权威，不接管其关闭责任。</summary>
        internal CoreResourceProvider(ResourceSystem owner) => this._owner = owner;

        /// <summary>取得 TextAsset 后读出文本值并归还临时资产租约，不把原资产交给 Core 消费者。</summary>
        public CoreResourceRequest<string> ReadTextAsync(string projectId, string address)
            => Convert(() => _owner.AcquireAsync<TextAsset>(new ResourceAddress(projectId, address)), lease =>
            {
                using (lease)
                {
                    return lease.Asset.text;
                }
            });

        /// <summary>同步取得独立 Prefab 租约并包装为 Core 合同，失败转换为 Core 可识别的错误。</summary>
        public CorePrefabLease AcquirePrefab(string projectId, string address)
        {
            try
            {
                return new PrefabLease(_owner, _owner.Acquire<GameObject>(new ResourceAddress(projectId, address)));
            }
            catch (Exception error)
            {
                throw Translate(error);
            }
        }

        /// <summary>异步取得 Prefab 租约；未领取结果随请求释放，领取后由 Core 消费者独立归还。</summary>
        public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address)
            => Convert<AssetLease<GameObject>, CorePrefabLease>(
                () => _owner.AcquireAsync<GameObject>(new ResourceAddress(projectId, address)),
                lease => new PrefabLease(_owner, lease), lease => lease.Dispose());

        /// <summary>加载真实场景后归还临时场景租约，只向 Core 交付副作用观察值，不交付卸载权。</summary>
        public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode)
            => Convert<SceneLease, CoreSceneResult>(
                () => _owner.LoadSceneAsync(new ResourceAddress(projectId, address), mode), lease =>
                {
                    using (lease)
                    {
                        return new CoreSceneResult(lease.Scene, true);
                    }
                });

        /// <summary>连接后端请求与 Core 结果转换，转交成功结果或失败状态，并始终释放已观察的输入请求。</summary>
        private CoreResourceRequest<TResult> Convert<TSource, TResult>(Func<ResourceRequest<TSource>> start,
            Func<TSource, TResult> convert, Action<TResult> release = null)
        {
            _owner.CheckThread();
            var output = new ResourceRequest<TResult>(release);
            var core = new Request<TResult>(output);
            ResourceRequest<TSource> input = null;
            _owner.Track(output);
            // 输出请求只负责未交割结果和本次等待；TakeResult 交割后的 Prefab 租约由领取方持有。
            output.Attach(() => input?.Dispose(), () => input?.Progress ?? 0);
            try
            {
                input = start();
                input.Completed += done =>
                {
                    try
                    {
                        if (output.IsDone)
                        {
                            return;
                        }

                        if (done.Status == ResourceRequestStatus.Succeeded)
                        {
                            output.SetSuccess(convert(done.TakeResult()));
                        }
                        else
                        {
                            output.SetFailure(done.Status, done.Error);
                        }
                    }
                    catch (Exception error)
                    {
                        if (!output.IsDone)
                        {
                            output.SetFailure(ResourceRequestStatus.Failed, Failure(error));
                        }
                    }
                    finally
                    {
                        done.Dispose();
                    }
                    output.Notify();
                };
            }
            catch (Exception error)
            {
                input?.Dispose();
                if (!output.IsDone)
                {
                    output.SetFailure(ResourceRequestStatus.Failed, Failure(error));
                }

                output.Notify();
            }
            return core;
        }

        /// <summary>保留已有资源错误，否则将输入错误或生产异常归为资源侧失败。</summary>
        private static ResourceException Failure(Exception error) => error as ResourceException ??
            new ResourceException(error is ArgumentException ? ResourceError.InvalidAddress : ResourceError.LoadFailed,
                "Core resource operation failed.", error);

        /// <summary>将资源侧错误类别映射为 Core 合同，保留原异常作为原因，不将缺后端伪装成资源缺失。</summary>
        private static CoreResourceException Translate(Exception error)
        {
            if (error is CoreResourceException core)
            {
                return core;
            }

            var kind = error is ArgumentException ? CoreResourceError.InvalidInput : CoreResourceError.LoadFailed;
            if (error is ResourceException resource)
            {
                kind = resource.Kind switch
                {
                    ResourceError.AddressNotFound => CoreResourceError.Missing,
                    ResourceError.InProgress => CoreResourceError.InProgress,
                    ResourceError.InvalidAddress => CoreResourceError.InvalidInput,
                    ResourceError.SessionUnavailable or ResourceError.SourceUnavailable or ResourceError.Unsupported => CoreResourceError.Unavailable,
                    _ => CoreResourceError.LoadFailed
                };
            }

            return new CoreResourceException(kind, error.Message, error);
        }

        /// <summary>Core 请求视图，共用原 ResourceRequest 的终态与单次结果交割，不建立第二份所有权。</summary>
        private sealed class Request<T> : CoreResourceRequest<T>
        {
            private readonly ResourceRequest<T> _request;
            private Action<CoreResourceRequest<T>> _observers;
            private CoreResourceException _error;
            /// <summary>订阅原请求完成，将通知转投给当前 Core 观察者。</summary>
            internal Request(ResourceRequest<T> request)
            {
                this._request = request;
                request.Completed += _ =>
                {
                    var snapshot = _observers;
                    _observers = null;
                    Notify(snapshot);
                };
            }
            /// <summary>把后端请求终态映射为 Core 状态，不因领取结果而另行改变终态。</summary>
            public override CoreResourceStatus Status => _request.Status switch
            {
                ResourceRequestStatus.Succeeded => CoreResourceStatus.Succeeded,
                ResourceRequestStatus.Failed => CoreResourceStatus.Failed,
                ResourceRequestStatus.Canceled => CoreResourceStatus.Canceled,
                ResourceRequestStatus.Invalidated => CoreResourceStatus.Invalidated,
                _ => CoreResourceStatus.Pending
            };
            /// <summary>透传原请求展示进度。</summary>
            public override float Progress => _request.Progress;
            /// <summary>按需缓存转换后的 Core 错误；后端没有错误时为 null。</summary>
            public override CoreResourceException Error => _request.Error == null ? null : _error ??= Translate(_request.Error);
            /// <summary>透传原请求是否已释放。</summary>
            public override bool IsDisposed => _request.IsDisposed;
            /// <summary>终态后订阅即时回放，释放后拒绝订阅；观察者异常独立记录。</summary>
            public override event Action<CoreResourceRequest<T>> Completed
            {
                add
                {
                    if (IsDisposed)
                    {
                        throw new ObjectDisposedException(nameof(CoreResourceRequest<T>));
                    }

                    if (_request.IsDone)
                    {
                        Notify(value);
                    }
                    else
                    {
                        _observers += value;
                    }
                }
                remove => _observers -= value;
            }
            /// <summary>逐个通知快照中的观察者，隔离用户异常，避免阻断其余通知。</summary>
            private void Notify(Action<CoreResourceRequest<T>> snapshot)
            {
                if (snapshot == null)
                {
                    return;
                }

                foreach (Action<CoreResourceRequest<T>> observer in snapshot.GetInvocationList())
                {
                    try
                    {
                        observer(this);
                    }
                    catch (Exception failure)
                    {
                        Debug.LogException(failure);
                    }
                }
            }
            /// <summary>单次移交成功结果；失败终态抛转换后的 Core 错误，其他交割约束沿用后端请求。</summary>
            public override T TakeResult()
            {
                if (!IsDisposed && _request.IsDone && _request.Status != ResourceRequestStatus.Succeeded)
                {
                    throw Error;
                }

                return _request.TakeResult();
            }
            /// <summary>只取消本消费者等待，不撤销共享生产或已发生的场景副作用。</summary>
            public override void Cancel() => _request.Cancel();
            /// <summary>释放原请求和未领取结果，并清空观察者；已领取租约不随请求一起归还。</summary>
            public override void Dispose()
            {
                try
                {
                    _request.Dispose();
                }
                finally
                {
                    _observers = null;
                }
            }
        }

        /// <summary>将一份独立 Prefab 资产租约适配为 Core 合同，实例取得另有自己的归还责任。</summary>
        private sealed class PrefabLease : CorePrefabLease
        {
            private readonly ResourceSystem _owner;
            private readonly AssetLease<GameObject> _lease;
            /// <summary>接管已取得的 Prefab 租约并借用所属资源权威。</summary>
            internal PrefabLease(ResourceSystem owner, AssetLease<GameObject> lease)
            {
                this._owner = owner;
                this._lease = lease;
            }
            /// <summary>在原租约有效时借读 Prefab，不复制资产或延长租约寿命。</summary>
            public override GameObject Prefab
            {
                get
                {
                    try
                    {
                        return _lease.Asset;
                    }
                    catch (ResourceException error)
                    {
                        throw Translate(error);
                    }
                }
            }
            /// <summary>透传资产租约、会话与 Unity 对象的联合有效性。</summary>
            public override bool IsValid => _lease.IsValid;
            /// <summary>从原资源权威取得独立实例租约，不消费当前 Prefab 租约。</summary>
            public override CoreInstanceLease Instantiate(Transform parent, Scene scene)
            {
                try
                {
                    return new InstanceHandle(_owner.Instantiate(_lease, parent, scene));
                }
                catch (Exception error)
                {
                    throw Translate(error);
                }
            }
            /// <summary>归还本份 Prefab 资产租约，不替代已经借出实例的归还。</summary>
            public override void Dispose() => _lease.Dispose();
        }

        /// <summary>Core 实例句柄，仅转发本次实例租约，不另建实例池或按 GameObject 猜测归还身份。</summary>
        private sealed class InstanceHandle : CoreInstanceLease
        {
            private readonly InstanceLease _lease;
            /// <summary>接管已经取得的独立实例租约。</summary>
            internal InstanceHandle(InstanceLease lease) => this._lease = lease;
            /// <summary>仅在本次实例租约有效时借读场景对象。</summary>
            public override GameObject Instance => _lease.GameObject;
            /// <summary>透传本次实例借用与所属会话的有效性。</summary>
            public override bool IsValid => _lease.IsValid;
            /// <summary>正常归还本次实例，由资源系统决定缓存或销毁。</summary>
            public override void Return() => _lease.Return();
            /// <summary>将本次实例标记为不可复用并交给资源系统清理。</summary>
            public override void Discard() => _lease.Discard();
        }
    }
}
