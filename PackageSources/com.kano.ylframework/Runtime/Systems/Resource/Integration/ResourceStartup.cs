using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using YLFramework.Core;
using YLFramework.Audio;

namespace YLFramework.Resource
{
    /// <summary>应用显式持有的接入责任。构造不初始化；StartAsync复用唯一ResourceSystem，StopAsync只结算本入口的责任。</summary>
    public sealed class ResourceStartup
    {
        private static readonly IReadOnlyDictionary<string, ResourceAudioClipProvider> _emptyAudio =
            new ReadOnlyDictionary<string, ResourceAudioClipProvider>(new Dictionary<string, ResourceAudioClipProvider>());
        private readonly ResourceConfiguration _configuration;
        private readonly bool _bindCore;
        private readonly IReadOnlyList<string> _audioProjects;
        private readonly List<ResourceRequest<ResourceStartup>> _starting = new();
        private bool _isStartingResources;
        private bool _isStopping;
        private bool _isRegistered;

        /// <summary>本入口已取得的资源权威；尚未启动或已完成本地清理时为 null，读取不会触发初始化。</summary>
        public ResourceSystem Resources
        {
            get; private set;
        }
        /// <summary>当前是否仍承担创建该资源会话后的关闭责任；借用已有会话时为 false。</summary>
        public bool OwnsResourceSystem
        {
            get; private set;
        }
        /// <summary>本入口请求绑定后借读的 Core 后端；未要求绑定或已清理时为 null。</summary>
        public ICoreResourceProvider CoreProvider
        {
            get; private set;
        }
        /// <summary>已按明确项目装配的只读 Audio 后端集合；提供后端不等于初始化或关闭 AudioKit。</summary>
        public IReadOnlyDictionary<string, ResourceAudioClipProvider> AudioProviders { get; private set; } = _emptyAudio;

        /// <summary>冻结启动配置、可选 Core 绑定意图与经唯一性校验的 Audio 项目列表，不触发资源初始化。</summary>
        public ResourceStartup(ResourceConfiguration configuration, bool bindCore = false, IEnumerable<string> audioProjects = null)
        {
            this._configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this._bindCore = bindCore;
            this._audioProjects = ResourceCatalog.Copy(audioProjects);
            foreach (var project in this._audioProjects)
            {
                ResourceAddress.ValidateId(project, nameof(audioProjects));
            }

            if (this._audioProjects.Distinct(StringComparer.Ordinal).Count() != this._audioProjects.Count)
            {
                throw new ArgumentException("Audio projects must be distinct.", nameof(audioProjects));
            }
        }

        /// <summary>同一入口的等待者独立取消；成功结果借读本入口，Dispose请求不等价Stop。配置冲突沿用ResourceSystem规则。</summary>
        /// <remarks>须在 Unity 主线程和有效执行会话中调用；复用要求同一配置对象，初始化与装配可能同步完成。</remarks>
        public ResourceRequest<ResourceStartup> StartAsync()
        {
            ResourceSystem.CheckStartupThread();
            var request = new ResourceRequest<ResourceStartup>();
            if (_isStopping)
            {
                return Failure(request, new ResourceException(ResourceError.Busy, "The startup is stopping."));
            }

            if (_isRegistered)
            {
                if (!Resources.IsAvailable)
                {
                    return Failure(request, new ResourceException(ResourceError.SessionUnavailable, "The resource session ended."));
                }

                request.SetSuccess(this);
                return request;
            }
            _starting.Add(request);
            request.Attach(() => _starting.Remove(request), null);
            if (_isStartingResources)
            {
                return request;
            }

            _isStartingResources = true;
            var initialized = ResourceSystem.InitializeStartup(_configuration, out var created);
            if (initialized.IsDisposed)
            {
                CleanupFailedStart(initialized.Error ?? new ResourceException(ResourceError.SessionUnavailable, "The session ended before startup assembly."));
                return request;
            }
            initialized.Completed += done =>
            {
                ResourceException error = null;
                try
                {
                    if (done.Status != ResourceRequestStatus.Succeeded)
                    {
                        error = done.Error;
                    }
                    else
                    {
                        var acquired = done.TakeResult();
                        OwnsResourceSystem = created || (ReferenceEquals(Resources, acquired) && OwnsResourceSystem);
                        Resources = acquired;
                        if (_starting.Count == 0)
                        {
                            error = new ResourceException(ResourceError.Canceled, "Every startup consumer canceled.");
                        }
                        else
                        {
                            // 先确认全部 Audio 项目可用再触碰 Core 绑定，避免可选接入校验失败后留下半套绑定。
                            var providers = _audioProjects.ToDictionary(id => id, id => Resources.GetAudioClipProvider(id), StringComparer.Ordinal);
                            CoreProvider = Resources.RegisterStartup(this, _bindCore);
                            _isRegistered = true;
                            AudioProviders = new ReadOnlyDictionary<string, ResourceAudioClipProvider>(providers);
                        }
                    }
                }
                catch (ResourceException failure)
                {
                    error = failure;
                }
                catch (Exception failure)
                {
                    error = new ResourceException(ResourceError.LoadFailed, "Resource startup assembly failed.", failure);
                }
                finally
                {
                    done.Dispose();
                }
                if (error == null)
                {
                    CompleteStart(null);
                }
                else
                {
                    CleanupFailedStart(error);
                }
            };
            return request;
        }

        /// <summary>结束本次启动等待批次，先登记所有终态再发通知，避免观察者重入改变兄弟请求的交付。</summary>
        private void CompleteStart(ResourceException error)
        {
            _isStartingResources = false;
            var requests = _starting.ToArray();
            _starting.Clear();
            foreach (var request in requests)
            {
                if (error == null)
                {
                    request.SetSuccess(this);
                }
                else
                {
                    request.SetFailure(error.Kind == ResourceError.SessionUnavailable ? ResourceRequestStatus.Invalidated : ResourceRequestStatus.Failed, error);
                }
            }

            foreach (var request in requests)
            {
                request.Notify();
            }
        }

        /// <summary>撤销失败装配的本入口绑定；仅尝试关闭自建会话，仍有其他接入者或关闭失败时保留责任供重试。</summary>
        private void CleanupFailedStart(ResourceException error)
        {
            if (_isRegistered)
            {
                Resources.UnregisterStartup(this);
                _isRegistered = false;
            }
            CoreProvider = null;
            AudioProviders = _emptyAudio;
            if (Resources == null || !OwnsResourceSystem)
            {
                ClearLocal();
                CompleteStart(error);
                return;
            }
            if (Resources.HasOtherStartups(this))
            {
                CompleteStart(new ResourceException(error.Kind, "Startup assembly failed; its created resource session remains owned for StopAsync retry because another startup joined.", error));
                return;
            }
            var cleanup = Resources.ShutdownAsync();
            cleanup.Completed += done =>
            {
                var failure = done.Error;
                if (done.Status == ResourceRequestStatus.Succeeded)
                {
                    ClearLocal();
                }

                done.Dispose();
                CompleteStart(failure == null ? error : new ResourceException(error.Kind,
                    "Startup assembly failed; created resources could not close. Release independent owners and retry StopAsync.", new AggregateException(error, failure)));
            };
        }

        /// <summary>借用会话只归还本入口的绑定；自建会话遇其他接入者或live owner返回Busy，保持可恢复，不关闭AudioKit或外部业务。</summary>
        /// <remarks>取消关闭请求只退出本次等待，已开始的native排空与入口责任收尾仍继续。</remarks>
        public ResourceRequest<bool> StopAsync()
        {
            ResourceSystem.CheckStartupThread();
            var request = new ResourceRequest<bool>();
            if (_isStartingResources)
            {
                return Failure(request, new ResourceException(ResourceError.Busy, "Startup initialization or failure cleanup is still in progress."));
            }

            if (_isStopping)
            {
                return Resources.ShutdownAsync();
            }

            if (Resources == null || !OwnsResourceSystem || !Resources.IsSessionActive)
            {
                ClearLocal();
                request.SetSuccess(true);
                return request;
            }
            if (Resources.HasOtherStartups(this))
            {
                return Failure(request, new ResourceException(ResourceError.Busy, "Other application startups still borrow the resource session."));
            }

            _isStopping = true;
            ResourceRequest<bool> shutdown;
            try
            {
                shutdown = Resources.ShutdownAsync();
            }
            catch (Exception error)
            {
                _isStopping = false;
                return Failure(request, error as ResourceException ?? new ResourceException(ResourceError.LoadFailed, "Resource shutdown failed.", error));
            }
            shutdown.Completed += done =>
            {
                if (done.Status == ResourceRequestStatus.Succeeded)
                {
                    ClearLocal();
                    request.SetSuccess(true);
                }
                else
                {
                    request.SetFailure(done.Status, done.Error);
                }

                _isStopping = false;
                done.Dispose();
                request.Notify();
            };
            return request;
        }

        /// <summary>注销本入口并清空借读引用和创建责任，不替业务归还独立资产或实例。</summary>
        private void ClearLocal()
        {
            if (_isRegistered)
            {
                Resources.UnregisterStartup(this);
            }

            _isRegistered = false;
            Resources = null;
            OwnsResourceSystem = false;
            CoreProvider = null;
            AudioProviders = _emptyAudio;
        }

        /// <summary>为尚未启动底层工作的请求登记失败，交由请求的终态订阅机制观察。</summary>
        private static ResourceRequest<T> Failure<T>(ResourceRequest<T> request, ResourceException error)
        {
            request.SetFailure(ResourceRequestStatus.Failed, error);
            return request;
        }
    }

    public sealed partial class ResourceSystem
    {
        private readonly HashSet<ResourceStartup> _startups = new();
        private readonly HashSet<ResourceStartup> _startupCoreUsers = new();
        private ICoreResourceProvider _startupCoreBinding;
        private bool _ownsStartupCoreBinding;

        /// <summary>复用资源系统统一的 Unity 主线程检查，不创建独立调度器。</summary>
        internal static void CheckStartupThread() => AssertThread();

        /// <summary>记录本次调用前是否已有资源权威，并复用唯一初始化入口，以确定启动方的关闭责任。</summary>
        internal static ResourceRequest<ResourceSystem> InitializeStartup(ResourceConfiguration config, out bool created)
        {
            created = _authority == null;
            return InitializeAsync(config);
        }

        /// <summary>登记接入者与可选 Core 使用责任；只接纳当前资源后端，不替换外部绑定。</summary>
        internal ICoreResourceProvider RegisterStartup(ResourceStartup startup, bool bindCore)
        {
            CheckThread();
            if (!IsAvailable)
            {
                throw new ResourceException(ResourceError.SessionUnavailable, "The resource session ended during startup assembly.");
            }

            if (bindCore)
            {
                var current = CoreResourceAccess.Provider;
                if (current != null && !ReferenceEquals(current, _coreProvider))
                {
                    throw new ResourceException(ResourceError.ConfigurationConflict, "A different Core resource provider is already bound; startup does not replace external bindings.");
                }

                if (_startupCoreUsers.Count == 0 || current == null)
                {
                    _startupCoreBinding = current ?? BindCoreResourceProvider();
                    _ownsStartupCoreBinding = current == null;
                }
                _startupCoreUsers.Add(startup);
            }
            _startups.Add(startup);
            return bindCore ? _startupCoreBinding : null;
        }

        /// <summary>检查除给定入口外是否仍有已登记的应用接入者，供自建会话关闭屏障使用。</summary>
        internal bool HasOtherStartups(ResourceStartup startup) => _startups.Any(x => !ReferenceEquals(x, startup));

        /// <summary>移除本入口责任，最后一个 Core 使用者离开时仅归还启动流程自己建立的绑定。</summary>
        internal void UnregisterStartup(ResourceStartup startup)
        {
            CheckThread();
            _startups.Remove(startup);
            if (!_startupCoreUsers.Remove(startup) || _startupCoreUsers.Count != 0)
            {
                return;
            }

            if (_ownsStartupCoreBinding)
            {
                CoreResourceAccess.Unbind(_startupCoreBinding);
            }

            _startupCoreBinding = null;
            _ownsStartupCoreBinding = false;
        }
    }
}
