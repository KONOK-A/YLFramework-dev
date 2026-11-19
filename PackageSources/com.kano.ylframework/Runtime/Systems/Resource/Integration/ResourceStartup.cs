using System;
using System.Collections.Generic;
using System.Linq;
using YLFramework.Audio;

namespace YLFramework.Resource
{
    /// <summary>应用组合根唯一的启动/停止所有者；模块只借用 Resources 并创建自己的 Scope。</summary>
    public sealed class ResourceStartup : IDisposable
    {
        private readonly ResourceConfiguration _configuration;
        private readonly bool _bindCore;
        private readonly string[] _audioProjects;
        private readonly List<ResourceRequest<ResourceStartup>> _waiters = new();
        private bool _starting;
        private bool _started;

        /// <summary>成功初始化后借读资源服务，不自动取得任何业务资产。</summary>
        public ResourceSystem Resources
        {
            get; private set;
        }
        /// <summary>当前是否仍拥有本服务的停止责任。</summary>
        public bool OwnsResourceSystem => Resources != null;
        /// <summary>本入口明确请求的 Core 绑定。</summary>
        public CoreResourceProvider CoreProvider
        {
            get; private set;
        }
        /// <summary>按业务命名空间装配的音频提供者，不拥有 Audio 系统生命周期。</summary>
        public IReadOnlyDictionary<string, ResourceAudioClipProvider> AudioProviders
        {
            get; private set;
        } =
            new Dictionary<string, ResourceAudioClipProvider>();

        /// <summary>只记录选项，不在构造或读取属性时初始化。</summary>
        public ResourceStartup(ResourceConfiguration configuration = null, bool bindCoreResourceProvider = true, IEnumerable<string> audioProjects = null)
        {
            _configuration = configuration ?? new ResourceConfiguration();
            _bindCore = bindCoreResourceProvider;
            _audioProjects = (audioProjects ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        }

        /// <summary>同一入口共享一次启动工作，但每个等待者独立取消和领取观察值。</summary>
        public ResourceRequest<ResourceStartup> StartAsync()
        {
            ResourceSystem.CheckStartupThread();
            if (_started && Resources?.State == ResourceSystemState.Ready && Resources.IsSessionActive)
                return ResourceSystem.Success(this);
            var request = new ResourceRequest<ResourceStartup>();
            _waiters.Add(request);
            request.Attach(() => _waiters.Remove(request), null);
            if (_starting)
                return request;
            _starting = true;
            var initialization = ResourceSystem.InitializeAsync(_configuration, this);
            initialization.Completed += done =>
            {
                Exception error = null;
                try
                {
                    if (done.Status != ResourceRequestStatus.Succeeded)
                        throw done.Error;
                    Resources = done.TakeResult();
                    var audioProviders = _audioProjects.ToDictionary(project => project, project => Resources.GetAudioClipProvider(project), StringComparer.Ordinal);
                    if (_bindCore)
                        CoreProvider = Resources.BindCoreResourceProvider();
                    AudioProviders = audioProviders;
                    _started = true;
                }
                catch (Exception failure)
                {
                    error = failure;
                }
                finally
                {
                    done.Dispose();
                    _starting = false;
                }
                var waiters = _waiters.ToArray();
                _waiters.Clear();
                foreach (var waiter in waiters)
                {
                    if (error == null)
                        waiter.SetSuccess(this);
                    else
                        waiter.SetFailure(ResourceRequestStatus.Failed, ResourceSystem.Wrap(error));
                    waiter.NotifyCompletion();
                }
            };
            return request;
        }

        /// <summary>冻结新工作，独立租约未归还时返回 Busy，不强行销毁调用者的内容。</summary>
        public ResourceRequest<bool> StopAsync()
        {
            ResourceSystem.CheckStartupThread();
            if (Resources == null)
            {
                var current = ResourceSystem.GetOwnedSystem(this);
                if (current == null)
                    return ResourceSystem.Success(true);
                Resources = current;
            }
            var service = Resources;
            var request = service.ShutdownAsync(this);
            request.Completed += done =>
            {
                if (done.Status == ResourceRequestStatus.Succeeded)
                {
                    OnServiceStopped(service);
                }
            };
            return request;
        }

        internal void OnServiceStopped(ResourceSystem system)
        {
            if (!ReferenceEquals(Resources, system))
                return;
            _started = false;
            Resources = null;
            CoreProvider = null;
            AudioProviders = new Dictionary<string, ResourceAudioClipProvider>();
        }

        /// <summary>仅发起停止；需要确认清理完成或 Busy 时必须等待 StopAsync。</summary>
        public void Dispose()
        {
            var observer = StopAsync();
            observer.Dispose();
        }
    }
}
