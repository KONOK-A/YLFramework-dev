using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YLFramework.Audio;
using YLFramework.Core;
using YLFramework.Resource;

namespace YLFramework.Samples.Resource
{
    /// <summary>中立启动场景中的应用 owner；只在 Play/Player 显式启动，不在 Awake、属性或 Editor 工具中初始化。</summary>
    /// <remarks>正常退出须先 await StopExampleAsync 再销毁 owner。OnDestroy 仅作异步兜底，不代表关闭完成。</remarks>
    public sealed class ResourceStartupExample : MonoBehaviour
    {
        public AssetReferenceT<TextAsset> message;
        public AssetReferenceT<ResourceStartupExampleData> data;
        public AssetReferenceGameObject prefab;
        public AssetReferenceT<Texture2D> preview;
        public AssetReferenceT<AudioClip> audioCue;
        [Tooltip("可选的业务场景；以 Additive 加载并自动激活，不替换中立启动场景。")]
        public AssetReference scene;
        [Tooltip("False creates a fresh instance. True prewarms and resets an instance before reusing it.")]
        public bool reusePrefab;
        public bool bindCoreResourceProvider = true;
        [Tooltip("Exact prefix of the authored message and cue addresses. Leave empty to omit the named bridges.")]
        public string contentProjectId;
        [Tooltip("Optional exact Catalog ID. Set only when this owner initializes Addressables first and automatic startup checks are disabled.")]
        public string startupCatalogId;
        [Tooltip("可选音频地址命名空间；登记 Provider 不代表已经播放或拥有 Audio 系统。")]
        public string[] audioProjects = Array.Empty<string>();

        private static readonly HashSet<Session> _pendingShutdowns = new HashSet<Session>();
        private Session _session;

        public ResourceStartup Startup => _session?.Startup;
        public string Result => _session?.Result ?? "Not started";
        public Exception LastFailure => _session?.CleanupError ?? _session?.BusinessError;
        public static int PendingShutdownCount => _pendingShutdowns.Count;
        public static string PendingShutdownDetails => string.Join("\n", _pendingShutdowns.Select(session => session.Result));

        /// <summary>只检查明确序列化的引用，不初始化、不加载、不修复官方配置。</summary>
        public void ValidateReferences()
        {
            Key(message, nameof(message));
            Key(data, nameof(data));
            Key(prefab, nameof(prefab));
            Key(scene, nameof(scene), true);
            Key(preview, nameof(preview), true);
            ValidateNamedInputs();
        }

        /// <summary>冻结本轮官方 RuntimeKey 后启动；成功后内容仍由业务 Scope 持有，直到显式关闭。</summary>
        public Task<string> StartExampleAsync(CancellationToken cancellation = default)
        {
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException("Run native initialization in Play Mode or a Player, not an Editor authoring command.");
            }
            if (_session != null && !_session.Closed || _pendingShutdowns.Count != 0)
            {
                throw new InvalidOperationException("Await the previous shutdown, or retry the retained pending shutdown owner first.");
            }
            var messageKey = Key(message, nameof(message));
            var dataKey = Key(data, nameof(data));
            var prefabKey = Key(prefab, nameof(prefab));
            var sceneKey = Key(scene, nameof(scene), true);
            var previewKey = Key(preview, nameof(preview), true);
            var hasCue = ValidateNamedInputs();
            var projectId = string.IsNullOrEmpty(contentProjectId) ? null : contentProjectId;
            var catalogId = string.IsNullOrEmpty(startupCatalogId) ? null : startupCatalogId;
            var projects = (audioProjects ?? Array.Empty<string>()).ToArray();
            foreach (var project in projects)
            {
                _ = new ResourceAddress(project, "audio");
            }
            var session = new Session
            {
                Startup = new ResourceStartup(new ResourceConfiguration(enableStartupCatalogUpdates: catalogId != null), bindCoreResourceProvider, projects),
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, CancellationToken.None),
                Owner = gameObject,
                Label = "ResourceStartupExample/" + GetInstanceID()
            };
            _session = session;
            session.Business = LoadBusinessAsync(session, messageKey, dataKey, prefabKey, sceneKey, reusePrefab, (projectId, catalogId, previewKey, hasCue));
            return ObserveStartAsync(session);
        }

        /// <summary>取消本例等待，先等业务结束和 Scope 真正关闭，再停止本例拥有的 Root 服务；失败可原 owner 重试。</summary>
        public Task StopExampleAsync() => _session == null ? Task.CompletedTask : CloseSessionAsync(_session);

        /// <summary>显式重试已销毁组件留下的真实关闭责任，不丢 owner、不强制释放外部租约。</summary>
        public static Task RetryPendingShutdownsAsync() => Task.WhenAll(_pendingShutdowns.ToArray().Select(CloseSessionAsync));

        [ContextMenu("Start Addressables Example (Play Only)")]
        public void Begin() => Observe(StartExampleAsync(), _session);

        [ContextMenu("Stop Or Retry Example Shutdown")]
        public void StopExample() => Observe(StopExampleAsync(), _session);

        [ContextMenu("Retry Retained Shutdown Owners")]
        private void RetryShutdowns() => Observe(RetryPendingShutdownsAsync());

        private static async Task<string> LoadBusinessAsync(Session session, string messageKey, string dataKey,
            string prefabKey, string sceneKey, bool reuse, (string Project, string Catalog, string Preview, bool Cue) options)
        {
            // 先发布 Business Task，再允许用户组件回调重入 Stop/OnDestroy；所有 await 保留 Unity 主线程上下文。
            await Task.Yield();
            var token = session.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            await session.Startup.StartAsync();
            token.ThrowIfCancellationRequested();
            var resources = session.Startup.Resources;
            if (options.Catalog != null)
            {
                var available = await resources.CheckForCatalogUpdatesAsync(token);
                if (available.Contains(options.Catalog, StringComparer.Ordinal))
                {
                    await resources.ApplyCatalogUpdatesAsync(new[] { options.Catalog }, token);
                }
            }
            token.ThrowIfCancellationRequested();
            session.Scope = resources.CreateScope(session.Label).BindToDestroy(session.Owner);
            var text = await session.Scope.LoadAsync<TextAsset>(messageKey, token);
            var sameText = await session.Scope.LoadAsync<TextAsset>(messageKey, token);
            if (!ReferenceEquals(text, sameText))
            {
                throw new InvalidOperationException("The example scope did not reuse its borrowed text.");
            }
            token.ThrowIfCancellationRequested();
            var configuration = await session.Scope.LoadAsync<ResourceStartupExampleData>(dataKey, token);
            token.ThrowIfCancellationRequested();
            var destination = default(Scene);
            if (sceneKey != null)
            {
                destination = await session.Scope.LoadSceneAsync(sceneKey, cancellation: token);
                token.ThrowIfCancellationRequested();
            }
            var instance = reuse
                ? await RentPrewarmedAsync(session, prefabKey, destination, token)
                : await session.Scope.InstantiateAsync(prefabKey, scene: destination, cancellation: token);
            token.ThrowIfCancellationRequested();

            // 默认借用不需要租约。只有另一段独立业务需要越出当前借用时才 Acquire/Retain，并各自归还。
            using var independent = await resources.AcquireAsync<TextAsset>(messageKey, token);
            using var retained = independent.Retain(session.Label + "/independent-read");
            independent.Dispose();
            token.ThrowIfCancellationRequested();
            var core = "off";
            if (options.Project != null && session.Startup.CoreProvider != null)
            {
                using var read = session.Startup.CoreProvider.ReadTextAsync(options.Project, "message");
                while (read.keepWaiting)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (read.TakeResult() != text.text)
                {
                    throw new InvalidOperationException("Core did not read the authored message.");
                }
                core = "ok";
            }
            token.ThrowIfCancellationRequested();
            if (options.Preview != null || options.Cue)
            {
                await PresentAsync(session, options.Project, options.Preview, options.Cue, token);
            }
            token.ThrowIfCancellationRequested();
            var result = text.text + " / " + configuration.text + " / " + instance.name + " / retained=" + retained.Asset.text.Length +
                " / core=" + core + " / audio=" + (options.Cue ? "ok" : "off") + " / image=" + (options.Preview != null ? "ok" : "off") +
                " / instance=" + (reuse ? "prewarmed, reset and reused" : "fresh");
            session.Result = "Running: " + result + ". Await StopExampleAsync before destroying the owner.";
            return result;
        }

        private static async Task<GameObject> RentPrewarmedAsync(Session session, string key, Scene destination, CancellationToken token)
        {
            var resources = session.Startup.Resources;
            using var prefabLease = await resources.AcquireAsync<GameObject>(key, token);
            token.ThrowIfCancellationRequested();
            var pool = resources.GetOrCreateInstanceCache(prefabLease, scene: destination);
            pool.Prewarm(1);
            GameObject previous;
            string name;
            using (var first = pool.Rent())
            {
                previous = first.GameObject;
                name = previous.name;
                // Application-owned per-borrow state is reset before the checkout returns to the pool.
                previous.name = "Sample first checkout";
                previous.name = name;
            }
            var next = pool.Rent();
            try
            {
                if (next.GameObject != previous || next.GameObject.name != name)
                {
                    throw new InvalidOperationException("Prewarmed instance was not reset and reused.");
                }
                return session.Scope.Own(next).GameObject;
            }
            catch
            {
                next.Discard();
                throw;
            }
        }

        private static async Task PresentAsync(Session session, string projectId, string previewKey, bool hasCue, CancellationToken token)
        {
            session.View = new GameObject("Resource Startup Preview");
            session.View.transform.SetParent(session.Owner.transform, false);
            if (previewKey != null)
            {
                var canvas = new GameObject("Preview Canvas", typeof(Canvas));
                canvas.transform.SetParent(session.View.transform, false);
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var image = new GameObject("Addressable Preview", typeof(RawImage));
                image.transform.SetParent(canvas.transform, false);
                var target = image.GetComponent<RawImage>();
                target.raycastTarget = false;
                target.rectTransform.sizeDelta = new Vector2(96, 96);
                var binding = session.Image = image.AddComponent<ResourceImage>();
                binding.SetAddress(previewKey);
                while (binding && binding.Status == ResourceRequestStatus.Pending)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                token.ThrowIfCancellationRequested();
                if (!binding || binding.Status != ResourceRequestStatus.Succeeded || !target.texture)
                {
                    throw binding?.Error ?? new ResourceException(ResourceError.LoadFailed, "The example image was not displayed.");
                }
            }
            if (hasCue)
            {
                // Warm the exact address, then borrow an independent Clip from the Audio bridge. No global Audio ownership.
                var clip = await session.Scope.LoadAsync<AudioClip>(projectId + "/cue", token);
                token.ThrowIfCancellationRequested();
                session.Cue = session.Startup.Resources.GetAudioClipProvider(projectId).Load("cue");
                if (session.Cue.Clip != clip)
                {
                    throw new InvalidOperationException("Audio did not borrow the warmed clip.");
                }
                var source = session.Audio = session.View.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.clip = session.Cue.Clip;
                source.Play();
            }
        }

        private static async Task<string> ObserveStartAsync(Session session)
        {
            try
            {
                return await session.Business;
            }
            catch (Exception error)
            {
                session.BusinessError = error;
                try
                {
                    await CloseSessionAsync(session);
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Business failed and shutdown remains incomplete. Retry the original owner.", error, cleanup);
                }
                throw;
            }
        }

        private static Task CloseSessionAsync(Session session)
        {
            if (session.Shutdown == null || session.Shutdown.IsFaulted || session.Shutdown.IsCanceled)
            {
                session.Shutdown = CloseCoreAsync(session);
                session.Cancellation.Cancel();
            }
            return session.Shutdown;
        }

        private static async Task CloseCoreAsync(Session session)
        {
            await Task.Yield();
            session.Result = "Closing; native cleanup is not yet complete.";
            session.CleanupError = null;
            try
            {
                try
                {
                    await session.Business;
                }
                catch (Exception error)
                {
                    // 原业务 Task 仍向 StartExampleAsync 的调用方交付原异常；关闭只结算所有权，不改写失败为成功。
                    session.BusinessError = error;
                }
                if (session.Audio)
                {
                    session.Audio.Stop();
                    session.Audio.clip = null;
                    session.Audio = null;
                }
                session.Cue?.Dispose();
                session.Cue = null;
                if (session.Image)
                {
                    session.Image.Clear();
                }
                session.Image = null;
                if (session.View)
                {
                    Destroy(session.View);
                }
                session.View = null;
                if (session.Scope != null)
                {
                    await session.Scope.CloseAsync();
                    session.Scope = null;
                }
                // 真实应用也应在这里之前完成全部业务 Module/Audio 播放的退出，而不是由 Root 抢先强关。
                await session.Startup.StopAsync();
                session.Closed = true;
                session.Cancellation.Dispose();
                _pendingShutdowns.Remove(session);
                session.Result = "Stopped: owned cleanup completed." + (session.BusinessError == null ? "" : "\nBusiness outcome: " + session.BusinessError);
            }
            catch (Exception error)
            {
                session.CleanupError = error;
                session.Result = "Shutdown incomplete; retain this owner and retry after returning the listed responsibilities.\n" +
                    error + (session.BusinessError == null ? "" : "\nBusiness outcome: " + session.BusinessError) +
                    "\n" + Responsibilities(session.Startup.Resources);
                throw;
            }
        }

        private static string Responsibilities(ResourceSystem resources)
        {
            if (resources == null)
            {
                return "No published ResourceSystem; the original error above remains authoritative.";
            }
            var report = resources.GetDiagnostics();
            return "YL state=" + report.State + ", requests=" + report.RequestCount + ", work=" + report.PendingWorkCount +
                ", draining=" + report.DrainingHandleCount + "\n" +
                string.Join("\n", report.Leases.Select(lease => lease.Kind + " key=" + lease.Key + " owner=" + lease.Owner +
                    " internal=" + lease.Internal + " settled=" + lease.Settled)) + "\n" +
                string.Join("\n", report.Scopes.Select(scope => "Scope owner=" + scope.Owner + " closing=" + scope.Closing + " error=" + scope.Error)) +
                "\n" + string.Join("\n", report.Errors);
        }

        private static string Key(AssetReference reference, string name, bool optional = false)
        {
            if (optional && (reference == null || string.IsNullOrEmpty(reference.AssetGUID)))
            {
                return null;
            }
            if (reference == null || !reference.RuntimeKeyIsValid() || !(reference.RuntimeKey is string key) || string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Select an explicit official AssetReference: " + name, name);
            }
            return key;
        }

        private bool ValidateNamedInputs()
        {
            var cue = Key(audioCue, nameof(audioCue), true);
            if (!string.IsNullOrEmpty(contentProjectId) || cue != null)
            {
                _ = new ResourceAddress(contentProjectId, "cue");
            }
            if (!string.IsNullOrEmpty(startupCatalogId) && string.IsNullOrWhiteSpace(startupCatalogId))
            {
                throw new ArgumentException("Select an exact Catalog ID or leave it empty.", nameof(startupCatalogId));
            }
            return cue != null;
        }

        private void OnDestroy()
        {
            var session = _session;
            if (session == null || session.Closed)
            {
                return;
            }
            // 纯托管 owner 和 Task 不依赖已销毁组件的协程。失败时保留供下一位应用 owner 显式重试。
            _pendingShutdowns.Add(session);
            Observe(CloseSessionAsync(session), session);
        }

        private static async void Observe(Task task, Session session = null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                Debug.Log("Resource startup example wait canceled. Cleanup state: " + session?.Result + "\n" + PendingShutdownDetails);
            }
            catch (Exception error)
            {
                Debug.LogError("Resource startup example did not complete: " + error + "\n" + session?.Result +
                    "\nRetained shutdown owners: " + PendingShutdownDetails);
            }
        }

        private sealed class Session
        {
            public ResourceStartup Startup;
            public ResourceScope Scope;
            public GameObject Owner;
            public GameObject View;
            public ResourceImage Image;
            public AudioSource Audio;
            public AudioClipLease Cue;
            public CancellationTokenSource Cancellation;
            public Task<string> Business;
            public Task Shutdown;
            public Exception BusinessError;
            public Exception CleanupError;
            public string Label;
            public string Result = "Starting";
            public bool Closed;
        }
    }
}
