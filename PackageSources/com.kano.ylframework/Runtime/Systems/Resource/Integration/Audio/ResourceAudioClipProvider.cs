using System;
using UnityEngine;
using YLFramework.Events;
using YLFramework.Resource;
using YLFramework.Update;

namespace YLFramework.Audio
{
    /// <summary>正式 Audio 资源后端。ProjectId/key 投影为官方地址，不是 Bundle 名或 Resources 物理路径。</summary>
    /// <remarks>由显式初始化的 ResourceSystem.GetAudioClipProvider 装配。每次成功交付独立 AssetLease，
    /// Audio 只管理播放/闲置使用；取消等待者、Audio Shutdown 不关闭 Resource，会话退出由原 Resource 请求完成收尾。</remarks>
    public sealed class ResourceAudioClipProvider : IAudioClipProvider
    {
        private readonly ResourceSystem _resources;
        /// <summary>固定地址命名空间；不要求存在旧 YL Catalog。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>资源权威仍可用时允许 Audio 以真实资产租约保留闲置资源，不承诺音频数据已经解码。</summary>
        public bool CanCache => _resources.IsAvailable;
        /// <summary>借用资源权威并固定项目身份，不初始化资源或创建独立资产缓存。</summary>
        internal ResourceAudioClipProvider(ResourceSystem resources, string projectId)
        {
            this._resources = resources;
            ProjectId = projectId;
        }

        /// <summary>只同步保留已加载的 AudioClip；未命中时要求预加载或异步取得，不阻塞官方生产。</summary>
        public AudioClipLease Load(string key)
        {
            _resources.RequireAvailable();
            var address = new ResourceAddress(ProjectId, key);
            string resourceKey = address.ProjectId + "/" + address.Path;
            if (!_resources.TryAcquireLoaded<AudioClip>(resourceKey, out var lease))
            {
                throw new ResourceException(ResourceError.InProgress,
                    "The AudioClip is not loaded. Preload it or use LoadAsync: " + resourceKey);
            }
            return Wrap(lease);
        }

        /// <summary>在主线程启动并交付独立音频租约或错误，允许完成回调在本方法返回前执行。</summary>
        /// <param name="key">固定项目内的精确逻辑地址，不解析 Bundle 名或附加子资产片段。</param>
        /// <param name="completed">必须提供的交付回调；成功接收后负责归还租约，回调抛错时本适配器回收该租约。</param>
        /// <param name="progress">可选的主线程展示进度观察者；其异常被记录，不阻断资源交付。</param>
        public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
        {
            if (completed == null)
            {
                throw new ArgumentNullException(nameof(completed));
            }

            var request = _resources.AcquireAsync<AudioClip>(new ResourceAddress(ProjectId, key));
            IUnregister observer = null;
            var finished = false;
            var lastProgress = -1f;
            // 普通过程进度去重，终态仍投递一次；进度不是结果所有权的交割信号。
            void ReportProgress(float value, bool terminal = false)
            {
                if (progress == null || (!terminal && value == lastProgress))
                {
                    return;
                }

                lastProgress = value;
                try
                {
                    progress(value);
                }
                catch (Exception failure)
                {
                    Debug.LogException(failure);
                }
            }
            // 原 ResourceRequest 持有尚未交割的结果；不依赖 Audio 宿主或等待协程观察 native 完成。
            request.Completed += done =>
            {
                finished = true;
                observer?.Unregister();
                observer = null;
                AudioClipLease lease = null;
                Exception error = null;
                try
                {
                    if (done.Status != ResourceRequestStatus.Succeeded)
                    {
                        error = done.Error;
                    }
                    else if (done.IsDisposed)
                    {
                        error = new ResourceException(ResourceError.SessionUnavailable, "The Audio resource session ended.");
                    }
                    else
                    {
                        lease = Wrap(done.TakeResult());
                    }
                }
                catch (Exception failure)
                {
                    error = failure;
                }
                finally
                {
                    done.Dispose();
                }
                // 进度观察者的异常不能阻断资源交付和缓存 producer 收尾。
                ReportProgress(done.Progress, true);
                // 进度回调可能重入会话退出；必须在最后一个用户回调之后重新确认交付责任仍有效。
                if (lease != null && (!_resources.IsSessionActive || !lease.Clip))
                {
                    lease.ReleaseSafely();
                    lease = null;
                    error = new ResourceException(_resources.IsSessionActive ? ResourceError.LoadFailed : ResourceError.SessionUnavailable,
                        "The Audio resource became unavailable before delivery.");
                }
                if (lease != null)
                {
                    try
                    {
                        lease.ValidateAcquisition();
                    }
                    catch (Exception failure)
                    {
                        lease.ReleaseSafely();
                        lease = null;
                        error = failure;
                    }
                }
                try
                {
                    completed(lease, error);
                }
                catch
                {
                    lease?.ReleaseSafely();
                    throw;
                }
            };
            if (!finished && progress != null)
            {
                try
                {
                    // UpdateManager 仅观察进度，不拥有完成/资源清理；关闭观察宿主也不会遗失 native 结果。
                    observer = UpdateManager.AddUpdateEvent(() =>
                    {
                        if (!finished)
                        {
                            ReportProgress(request.Progress);
                        }
                    });
                    if (finished)
                    {
                        observer.Unregister();
                        observer = null;
                    }
                    else
                    {
                        ReportProgress(request.Progress);
                    }
                }
                catch
                {
                    request.Dispose();
                    throw;
                }
            }
        }

        /// <summary>将独立资产责任交给 Audio，交付时复核原租约而不访问旧资源记录；包装失败立即归还。</summary>
        private static AudioClipLease Wrap(AssetLease<AudioClip> lease)
        {
            try
            {
                return new AudioClipLease(lease.Asset, lease, () =>
                {
                    if (!lease.IsValid)
                    {
                        throw new ResourceException(ResourceError.SessionUnavailable, "The Audio asset lease is no longer valid.");
                    }
                    _ = lease.Asset;
                });
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }
}
