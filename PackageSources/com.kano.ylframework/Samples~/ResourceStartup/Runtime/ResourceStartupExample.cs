using System;
using System.Collections;
using UnityEngine;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Samples.Resource
{
    /// <summary>显式调用Begin/Run，不在Awake/Start/属性读取时启动。Editor配套入口只负责配置选择，业务共用Run。</summary>
    /// <remarks>
    /// 项目需配置 message 文本、data 示例资产及可选 tone 音频地址；创建配置不等于启动资源会话。
    /// Player 使用明确的 Bundle 发布快照；Editor 模拟配置由配套编辑器入口装配，不作为加载失败的回退来源。
    /// </remarks>
    public sealed class ResourceStartupExample : MonoBehaviour
    {
        /// <summary>显式选择资源来源；Editor 模拟模式须由配套编辑器工厂装配。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Mode")]
        public ResourceLoadMode mode = ResourceLoadMode.EditorSimulation;
        /// <summary>资源项目的稳定标识，用于本轮地址解析和可选 Audio 后端选择。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ProjectId")]
        public string projectId;
        /// <summary>所选 Bundle 发布的平台名称，须与快照和内置部署的平台一致。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Platform")]
        public string platform = "StandaloneWindows64";
        /// <summary>应用预期的资源兼容标识，打开快照时须与发布目录声明一致。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("AppCompatibility")]
        public string appCompatibility = "startup-sample-v1";
        /// <summary>非内置模式下明确选择的本地发布绝对目录，其末级目录名须为所选摘要。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("SnapshotDirectory")]
        public string snapshotDirectory;
        /// <summary>应用明确绑定的发布 Catalog 字节 SHA-256，不通过扫描目录自动选择版本。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("CatalogDigest")]
        public string catalogDigest;
        /// <summary>Bundle 模式是否读取已部署的内置版本；此开关不执行部署。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("UseBuiltIn")]
        public bool useBuiltIn;
        /// <summary>本轮启动是否请求绑定 Core 资源后端，不替换应用已有的其他后端。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("BindCore")]
        public bool bindCore = true;
        /// <summary>本轮是否装配所选项目的 Audio 后端并尝试播放 tone，不改共享音效偏好。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("UseAudio")]
        public bool useAudio = true;

        /// <summary>应用在运行前注入并拥有其生命周期；不保存到Scene，生产密钥来源由应用提供。</summary>
        [NonSerialized] public IResourceContentKeyProvider ContentKeyProvider;

        /// <summary>最近一轮显式创建的启动入口；未运行时为空，读取不会初始化资源。</summary>
        public ResourceStartup Startup
        {
            get; private set;
        }
        /// <summary>最近一轮运行或关闭的展示状态；业务失败详情仍须查看原始异常。</summary>
        public string Result { get; private set; } = "Not started";
        private Coroutine _running;
        private ResourceRequest<ResourceStartup> _initializing;
        private ResourceScope _scope;
        private bool _isClosing;
        private bool _isBusinessCompleted;
        private string _message;

        /// <summary>验证明确选择的本地或内置快照，并声明示例所需的编译资产类型，不启动资源系统。</summary>
        /// <returns>仅包含所选项目、快照与示例类型映射的挂载描述。</returns>
        public ResourceProjectMount CreateMount() => new(projectId, platform, appCompatibility,
            useBuiltIn ? ResourceLocalSnapshot.OpenBuiltIn(projectId, catalogDigest, platform, appCompatibility) :
                ResourceLocalSnapshot.Open(snapshotDirectory, projectId, catalogDigest, platform, appCompatibility),
            new[] { typeof(ResourceStartupExampleData) });

        /// <summary>从当前样例选择创建只读 Bundle 配置，不初始化资源或获取内容密钥。</summary>
        /// <remarks>只接受 Bundles 模式；本例不配置缓存根或远程来源，因此不会装配 ArtifactStore 更新流程。</remarks>
        /// <returns>包含明确快照及可选应用密钥来源的资源配置。</returns>
        public ResourceConfiguration CreateBundleConfiguration() => ResourceConfiguration.Create(mode, new[] { CreateMount() },
            decryption: ContentKeyProvider == null ? null : new ResourceDecryptionOptions(ContentKeyProvider));

        /// <summary>Player显式入口；模拟模式在此明确拒绝，不因为Bundle错误切换来源。</summary>
        [ContextMenu("Run Bundles Example")]
        public void BeginBundles()
        {
            if (mode != ResourceLoadMode.Bundles)
            {
                throw new InvalidOperationException("Player startup requires Bundles mode; use the Editor sample inspector for simulation.");
            }

            Begin(CreateBundleConfiguration());
        }

        /// <summary>在 Play Mode 显式启动本组件托管的业务协程，前一轮责任未结清时拒绝重复启动。</summary>
        /// <param name="configuration">调用方已明确选择来源的配置；选择配置本身不启动会话。</param>
        public void Begin(ResourceConfiguration configuration)
        {
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException("Run the example in Play Mode.");
            }

            if (_running != null || _isClosing || Startup?.Resources != null)
            {
                throw new InvalidOperationException("Stop the previous startup before running again.");
            }

            _running = StartCoroutine(Run(configuration));
        }

        /// <summary>同一业务消费Text、自定义Data及可选Core/Audio；示例只归还自己的Scope、请求和播放。</summary>
        /// <param name="configuration">本轮资源会话使用的明确配置，不因加载失败改变来源。</param>
        /// <returns>由调用方驱动的业务与关闭等待迭代器。</returns>
        /// <remarks>
        /// 在 Play Mode 的 Unity 主线程驱动；提前中止时须释放迭代器或显式调用 StopExample。
        /// ResourceStartup 按自身归属处理资源会话和 Core 绑定；共享 AudioKit 与应用密钥提供者仍由应用管理。
        /// </remarks>
        public IEnumerator Run(ResourceConfiguration configuration)
        {
            if (_isClosing || Startup?.Resources != null)
            {
                throw new InvalidOperationException("The previous startup still owns resources.");
            }

            // 冻结本轮项目与可选接入，避免运行中修改 Inspector 改变已开始的业务。
            var projectId = this.projectId;
            var useCore = bindCore;
            var useAudio = this.useAudio;
            _isBusinessCompleted = false;
            Result = "Running";
            Startup = new ResourceStartup(configuration, useCore, useAudio ? new[] { projectId } : null);
            try
            {
                _initializing = Startup.StartAsync();
                yield return _initializing;
                _initializing.TakeResult();
                _initializing.Dispose();
                _initializing = null;
                _scope = Startup.Resources.CreateScope();
                var data = _scope.AcquireAsync<ResourceStartupExampleData>(new ResourceAddress(projectId, "data"));
                yield return data;
                var caption = _scope.TakeResult(data).Asset.text;
                if (useCore)
                {
                    var text = _scope.Own(Startup.CoreProvider.ReadTextAsync(projectId, "message"));
                    yield return text;
                    _message = text.TakeResult() + " / " + caption;
                }
                else
                {
                    var text = _scope.AcquireAsync<TextAsset>(new ResourceAddress(projectId, "message"));
                    yield return text;
                    _message = _scope.TakeResult(text).Asset.text + " / " + caption;
                }
                if (useAudio)
                {
                    var sound = _scope.Own(AudioKit.PlaySoundAsync(new AudioClipAddress(Startup.AudioProviders[projectId], "tone")));
                    // 只等播放开始，避免共享音效设置拒绝请求时把本例误判为成功。
                    var until = Time.realtimeSinceStartup + 10;
                    while (!sound.HasStarted && !sound.IsDone && Time.realtimeSinceStartup < until)
                    {
                        yield return null;
                    }

                    if (!sound.HasStarted)
                    {
                        throw sound.Error ?? new InvalidOperationException(
                        "The sample Audio request did not start: " + sound.EndReason +
                        ". Check application Sound settings/admission, or disable UseAudio; this example does not override shared Audio preferences.");
                    }
                }
                _isBusinessCompleted = true;
            }
            finally
            {
                CloseOwnedResponsibilities();
            }
            while (_isClosing)
            {
                yield return null;
            }
        }

        /// <summary>停止本组件托管的协程并尝试结算样例责任；关闭失败后可再次显式调用以重试。</summary>
        [ContextMenu("Stop Or Retry Example Shutdown")]
        public void StopExample()
        {
            if (_running != null)
            {
                var current = _running;
                _running = null;
                StopCoroutine(current);
            }
            // Unity停协程不保证执行iterator finally；字段中的Scope仍由显式关闭接点结算。
            CloseOwnedResponsibilities();
        }

        /// <summary>归还本例的初始化等待和资源域，再请求启动入口结算并记录异步关闭结果。</summary>
        private void CloseOwnedResponsibilities()
        {
            _running = null;
            _initializing?.Dispose();
            _initializing = null;
            try
            {
                // 先归还样例的租约和播放，避免自身持有责任挡住资源会话关闭。
                _scope?.Dispose();
            }
            finally
            {
                _scope = null;
                if (!_isClosing && Startup != null)
                {
                    _isClosing = true;
                    var shutdown = Startup.StopAsync();
                    shutdown.Completed += done =>
                    {
                        _isClosing = false;
                        Result = done.Status == ResourceRequestStatus.Succeeded
                            ? _isBusinessCompleted ? "Passed: " + _message : "Stopped or failed; inspect the original error."
                            : "Shutdown requires retry: " + done.Error;
                        done.Dispose();
                    };
                }
            }
        }

        /// <summary>组件在运行中停用时发起样例清理，不接管应用其他业务的生命周期。</summary>
        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                StopExample();
            }
        }
    }
}
