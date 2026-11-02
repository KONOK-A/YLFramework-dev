using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    /// <summary>沿用 Request 的单次领取协议。取消终态不代表 native 操作撤销；Scenes 可在迟到完成后更新。</summary>
    public sealed class SceneRequest : ResourceRequest<SceneLease>
    {
        private readonly ResourceSystem _owner;
        internal AsyncOperation Native;
        internal bool IsActivationRequested;
        /// <summary>本次请求的 Single 或 Additive 加载意图。</summary>
        public LoadSceneMode Mode
        {
            get;
        }
        /// <summary>所属资源系统分配的本次加载序号，不按场景路径合并。</summary>
        public long LoadSequence
        {
            get;
        }
        /// <summary>一旦为 true，Single 可能替换旧场景，Cancel/Dispose 均不承诺回滚。</summary>
        public bool HasNativeStarted
        {
            get; internal set;
        }
        /// <summary>是否已观察到原生加载完成；请求终态可能先于它发生。</summary>
        public bool IsNativeCompleted
        {
            get; internal set;
        }
        /// <summary>原生加载是否已经到达激活门控，且尚未放行。</summary>
        public bool IsWaitingForActivation => Native != null && !IsActivationRequested && Native.progress >= 0.9f;
        /// <summary>本次实际观察到的身份。歧义结果只保活候选，不授予卸载它们的权利。</summary>
        public IReadOnlyList<SceneRecord> Scenes { get; internal set; } = Array.Empty<SceneRecord>();

        /// <summary>保存加载意图与激活策略，未领取结果只释放调用方租约身份。</summary>
        internal SceneRequest(ResourceSystem owner, LoadSceneMode mode, long sequence, bool activate) : base(value => value.Dispose())
        {
            this._owner = owner;
            Mode = mode;
            LoadSequence = sequence;
            IsActivationRequested = activate;
        }

        /// <summary>允许激活，不等同 SetActiveScene。幂等；门控超时、取消和退出也会强制放行 native queue。</summary>
        /// <exception cref="ObjectDisposedException">请求已经释放或被会话关闭。</exception>
        /// <exception cref="InvalidOperationException">未在 Unity 主线程调用。</exception>
        public void Activate()
        {
            _owner.CheckThread();
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(SceneRequest));
            }

            if (IsDone)
            {
                return;
            }

            IsActivationRequested = true;
            if (Native != null)
            {
                Native.allowSceneActivation = true;
            }
        }
    }

    /// <summary>只为 Editor 的真实场景加载提供窄接点，不建立另一份 Scene/Bundle authority。</summary>
    internal interface IResourceSceneSource
    {
        /// <summary>判断目录条目是否表示本来源支持的场景。</summary>
        bool IsScene(ResourceCatalogEntry entry);
        /// <summary>验证并解析目录条目的真实场景路径，不加载场景。</summary>
        string ResolveScenePath(ResourceCatalogEntry entry);
        /// <summary>按明确路径和参数发起本来源的真实异步场景加载。</summary>
        AsyncOperation LoadSceneAsync(string path, LoadSceneParameters parameters);
    }
}
