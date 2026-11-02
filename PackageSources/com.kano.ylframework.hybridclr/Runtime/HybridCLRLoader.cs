using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using YLFramework.Core;
using YLFramework.Foundation;
using YLFramework.Resource;
using Module = YLFramework.Core.Module;
using YLFramework.Async;

namespace YLFramework.Integrations.HybridCLR
{
    /// <summary>隔离原生调用与已加载程序集查询，供加载器按明确的执行模式使用。</summary>
    internal sealed class HybridCLRRuntimeCalls
    {
        internal bool IsSimulation;
        internal Func<byte[], int> LoadMetadata;
        internal Func<byte[], Assembly> LoadAssembly;
        internal Func<Assembly[]> LoadedAssemblies;
    }

    /// <summary>跨加载器共享的进程代码登记及互斥状态，不随实例释放而清除。</summary>
    internal sealed class HybridCLRProcessState
    {
        /// <summary>已成功登记的 DLL 身份与内容；metadata 记录不持有程序集实例。</summary>
        internal sealed class Loaded
        {
            internal string Identity;
            internal string Hash;
            internal string Mvid;
            internal bool IsMetadata;
            internal Assembly Assembly;
        }
        internal readonly Dictionary<string, Loaded> Assemblies = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Projects = new Dictionary<string, string>(StringComparer.Ordinal);
        internal object ActiveJob;
        internal bool RequiresRestart;
    }

    /// <summary>借用已启动的 ResourceSystem，在 Unity 主线程校验代码并衔接 Core Module 生命周期。</summary>
    /// <remarks>
    /// 由 asmdef 的 YL_HYBRIDCLR_9_0_3 门禁启用，不会自行安装运行库或启动资源服务。
    /// Editor 模式不证明目标 Player 可用；非 Editor 的默认调用仅开放 Windows IL2CPP 条件编译路径。
    /// </remarks>
    public sealed class HybridCLRLoader : IDisposable
    {
        private static HybridCLRProcessState _processState = new HybridCLRProcessState();
        private static int _mainThread;
        private static int _generation;
        private static bool _isExecutionStopping;
        private static readonly HashSet<HybridCLRLoader> _pendingLoaders = new HashSet<HybridCLRLoader>();
        private readonly ResourceSystem _resources;
        private readonly string _compatibilityId;
        private readonly HybridCLRRuntimeCalls _calls;
        private readonly HybridCLRProcessState _process;
        private readonly int _session;
        private readonly Dictionary<ResourceAddress, Session> _sessions = new Dictionary<ResourceAddress, Session>();
        private readonly Dictionary<(ResourceAddress, string, string), HybridCLRModuleRequest> _moduleRequests = new Dictionary<(ResourceAddress, string, string), HybridCLRModuleRequest>();
#if UNITY_EDITOR
        private readonly Dictionary<(ResourceAddress, string, string), HybridCLRModuleRequest> _editorModuleRequests = new Dictionary<(ResourceAddress, string, string), HybridCLRModuleRequest>();
#endif
        private readonly HashSet<Module> _modules = new HashSet<Module>();
        private Job _active;
        private bool _isDisposed;
        private bool _hasSessionEnded;

        /// <summary>成功加载后的结果与唯一 manifest 租约，用于保持活动代码资源闭包。</summary>
        private sealed class Session
        {
            internal AssetLease<TextAsset> Manifest;
            internal HybridCLRLoadResult Result;
        }

        /// <summary>一次加载中的资源请求、已校验字节及不可逆调用状态。</summary>
        private sealed class Job
        {
            internal ResourceAddress Address;
            internal HybridCLRLoadRequest Request;
            internal ResourceRequest<AssetLease<TextAsset>> Pending;
            internal AssetLease<TextAsset> ManifestLease;
            internal HybridCLRManifest Manifest;
            internal HybridCLRPayload[] Payloads;
            internal readonly Dictionary<string, byte[]> Bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            internal int Index;
            internal bool IsIrreversible;
            internal bool RequiresRestart;
            internal Coroutine ProgressRoutine;
        }

        private IEnumerable<HybridCLRModuleRequest> PendingModuleRequests =>
#if UNITY_EDITOR
            _moduleRequests.Values.Concat(_editorModuleRequests.Values);
#else
            _moduleRequests.Values;
#endif

        // 开始新 Play 会话并使上一轮未完成请求失效，重绑退出通知。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginSession()
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            InvalidatePendingSessions();
            _generation++;
            _isExecutionStopping = false;
            Application.quitting -= InvalidatePendingSessions;
            Application.quitting += InvalidatePendingSessions;
#if UNITY_EDITOR
            // Editor 仅模拟；重建会话引用，不声称卸载 AppDomain 中的程序集。
            _processState = new HybridCLRProcessState();
#endif
        }

#if UNITY_EDITOR
        // 记录 Editor 主线程并重绑 Play 状态通知，兼顾禁用 Domain Reload 的会话。
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeEditorThread()
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }
        // 在退出 Play 时终结待处理入口，不让回调越过会话边界。
        private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode || state == UnityEditor.PlayModeStateChange.EnteredEditMode)
            {
                InvalidatePendingSessions();
            }
        }
#endif

        // 先封闭新请求，再终结登记中的加载器请求并释放其资源屏障。
        private static void InvalidatePendingSessions()
        {
            _isExecutionStopping = true;
            var pending = _pendingLoaders.ToArray();
            foreach (var loader in pending)
            {
                loader._hasSessionEnded = true;
            }

            foreach (var loader in pending)
            {
                var error = new InvalidOperationException("The accepted HybridCLR request's Play/Resource session ended before completion.");
                if (loader._active != null)
                {
                    loader.Finish(loader._active, null, error);
                }

                foreach (var request in loader.PendingModuleRequests.ToArray())
                {
                    request.Stage = "SessionUnavailable";
                    request.Invalidate?.Invoke(error);
                }
                foreach (var owned in loader._sessions.Values)
                {
                    owned.Manifest.Dispose();
                }

                loader._sessions.Clear();
                loader._modules.Clear();
                _pendingLoaders.Remove(loader);
            }
        }

        // 仅在存在加载或 Module 请求时登记加载器，供会话终结统一处理。
        private void UpdatePendingRegistration()
        {
            if (_active == null && !PendingModuleRequests.Any())
            {
                _pendingLoaders.Remove(this);
            }
            else
            {
                _pendingLoaders.Add(this);
            }
        }

        /// <summary>在主线程创建加载器，借用当前可用的资源服务而不取得其关闭权。</summary>
        /// <param name="resources">已启动并挂载所需项目的 ResourceSystem。</param>
        /// <param name="appContentCompatibilityId">应用明确提供的基座内容兼容身份。</param>
        public HybridCLRLoader(ResourceSystem resources, string appContentCompatibilityId)
            : this(resources, appContentCompatibilityId, DefaultCalls(), _processState) { }

        // 校验借用服务和调用集，并固定本加载器所属的会话代次。
        internal HybridCLRLoader(ResourceSystem resources, string appContentCompatibilityId, HybridCLRRuntimeCalls calls, HybridCLRProcessState process)
        {
            AssertThread();
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            _ = new ResourceAddress(appContentCompatibilityId, "compatibility");
            if (_isExecutionStopping || !resources.IsAvailable)
            {
                throw new InvalidOperationException("Start the ResourceSystem in an active session before creating a HybridCLR loader.");
            }

            this._resources = resources;
            _compatibilityId = appContentCompatibilityId;
            this._calls = calls ?? throw new ArgumentNullException(nameof(calls));
            this._process = process ?? throw new ArgumentNullException(nameof(process));
            if (calls.LoadAssembly == null || calls.LoadMetadata == null || calls.LoadedAssemblies == null)
            {
                throw new ArgumentException("Incomplete runtime call set.", nameof(calls));
            }

            _session = _generation;
        }

        // 按编译路径选择 Editor 模拟或原生 SuperSet metadata 调用，其他路径直接拒绝。
        private static HybridCLRRuntimeCalls DefaultCalls()
        {
            AssertThread();
#if UNITY_EDITOR
            const bool Simulation = true;
#elif ENABLE_IL2CPP && UNITY_STANDALONE_WIN
            const bool Simulation = false;
#else
            throw new PlatformNotSupportedException("Native HybridCLR loading is supported only by the validated Windows64 IL2CPP base; Editor is Simulation only.");
#endif
#if UNITY_EDITOR || (ENABLE_IL2CPP && UNITY_STANDALONE_WIN)
            return new HybridCLRRuntimeCalls
            {
                IsSimulation = Simulation,
                LoadAssembly = Assembly.Load,
                LoadMetadata = bytes => (int)global::HybridCLR.RuntimeApi.LoadMetadataForAOTAssembly(bytes, global::HybridCLR.HomologousImageMode.SuperSet),
                LoadedAssemblies = () => AppDomain.CurrentDomain.GetAssemblies()
            };
#endif
        }

        /// <summary>读取并验证清单及全部 DLL 字节；原生路径先补充 metadata，再依赖优先加载热更程序集。</summary>
        /// <param name="manifestAddress">显式项目内的 hybridclr/manifest.json 地址。</param>
        /// <returns>可 yield 的请求；同实例同地址的进行中调用共享请求，成功会话复用结果。</returns>
        /// <remarks>
        /// 进程内其他加载任务占用时返回 Busy 终态，不排队；成功后只保留 manifest Lease，DLL 租约随读取释放。
        /// 进程代码登记冲突或不可逆调用失败通过 RequiresRestart 报告，释放加载器不能允许同进程换版。
        /// </remarks>
        public HybridCLRLoadRequest LoadAsync(ResourceAddress manifestAddress)
        {
            RequireAvailable(manifestAddress);
            if (_active != null && _active.Address == manifestAddress)
            {
                return _active.Request;
            }

            var request = new HybridCLRLoadRequest { ProjectId = manifestAddress.ProjectId };
            if (_process.RequiresRestart)
            {
                request.Finish(null, new InvalidOperationException("An earlier irreversible native load failed. Restart this process before loading code."), true);
                return request;
            }
            if (_sessions.TryGetValue(manifestAddress, out var previous))
            {
                if (!previous.Manifest.IsValid)
                {
                    request.Finish(null, new InvalidOperationException("The Resource session owning this code is no longer available."));
                }
                else
                {
                    request.Stage = "Complete";
                    request.Finish(previous.Result);
                }
                return request;
            }
            if (_process.ActiveJob != null)
            {
                request.Stage = "Busy";
                request.Finish(null, new InvalidOperationException("Another HybridCLR code load is in progress. Retry after it completes."));
                return request;
            }
            var job = new Job { Address = manifestAddress, Request = request };
            _active = job;
            _process.ActiveJob = job;
            UpdatePendingRegistration();
            try
            {
                var pending = _resources.AcquireAsync<TextAsset>(manifestAddress);
                job.Pending = pending;
                pending.Completed += r => ReadManifest(job, r);
                if (!request.IsDone)
                {
                    var routine = CoroutineStarter.Start(ObserveLoad(job));
                    if (request.IsDone)
                    {
                        CoroutineStarter.Stop(routine);
                    }
                    else if (routine == null)
                    {
                        Finish(job, null, new InvalidOperationException("Code loading requires an active coroutine host."));
                    }
                    else
                    {
                        job.ProgressRoutine = routine;
                    }
                }
            }
            catch (Exception exception)
            {
                Finish(job, null, exception);
            }
            return request;
        }

        // 持续检查资源会话并映射实际读取进度，不依赖调用方是否等待请求。
        private IEnumerator ObserveLoad(Job job)
        {
            while (!job.Request.IsDone)
            {
                try
                {
                    RequireAvailable(job.Address);
                    var pending = job.Pending?.Progress ?? 0;
                    job.Request.ReportProgress(job.Payloads == null ? 0.1f * pending :
                        0.1f + 0.6f * (job.Index + pending) / job.Payloads.Length);
                }
                catch (Exception error)
                {
                    Finish(job, null, error);
                }
                if (job.Request.IsDone)
                {
                    yield break;
                }

                yield return null;
            }
        }

        // 接收唯一有效的清单回调，取得屏障租约并核验运行身份。
        private void ReadManifest(Job job, ResourceRequest<AssetLease<TextAsset>> pending)
        {
            if (!ReferenceEquals(job.Pending, pending) || job.Request.IsDone)
            {
                return;
            }

            job.Pending = null;
            try
            {
                if (pending.Status != ResourceRequestStatus.Succeeded)
                {
                    throw (Exception)pending.Error ?? new InvalidOperationException("Manifest resource request did not succeed.");
                }

                job.ManifestLease = pending.TakeResult();
                var bytes = job.ManifestLease.Asset.bytes;
                if (bytes.Length > HybridCLRManifest.MaximumManifestBytes)
                {
                    throw new InvalidDataException("Manifest byte budget exceeded.");
                }

                job.Manifest = HybridCLRManifest.Parse(new UTF8Encoding(false, true).GetString(bytes));
                job.Manifest.Validate(job.Address.ProjectId, _compatibilityId, Application.unityVersion, Debug.isDebugBuild);
                job.Payloads = job.Manifest.AotMetadata.Concat(job.Manifest.HotUpdateAssemblies).ToArray();
                pending.Dispose();
                job.Request.Stage = "Payloads";
                job.Request.ReportProgress(0.1f);
                Pump(job);
            }
            catch (Exception exception)
            {
                pending.Dispose();
                Finish(job, null, exception);
            }
        }

        // 顺序读取代码资源，兼容同步完成及异步回调，全部验证后才进入加载阶段。
        private void Pump(Job job)
        {
            try
            {
                RequireAvailable(job.Address);
                while (job.Index < job.Payloads.Length)
                {
                    var payload = job.Payloads[job.Index];
                    job.Request.AssemblyName = payload.Name;
                    job.Request.ReportProgress(0.1f + 0.6f * job.Index / job.Payloads.Length);
                    var pending = _resources.AcquireAsync<TextAsset>(new ResourceAddress(job.Manifest.ProjectId, payload.Address));
                    job.Pending = pending;
                    if (pending.IsDone)
                    {
                        ReadPayload(job, pending);
                        continue;
                    }
                    pending.Completed += r =>
                    {
                        if (!ReferenceEquals(job.Pending, r) || job.Request.IsDone)
                        {
                            return;
                        }

                        try
                        {
                            ReadPayload(job, r);
                            Pump(job);
                        }
                        catch (Exception exception)
                        {
                            Finish(job, null, exception);
                        }
                    };
                    return;
                }
                LoadValidated(job);
            }
            catch (Exception exception)
            {
                Finish(job, null, exception);
            }
        }

        // 核对 DLL 长度、摘要及 PE MVID，留存字节后立即释放大资源租约。
        private static void ReadPayload(Job job, ResourceRequest<AssetLease<TextAsset>> pending)
        {
            job.Pending = null;
            using (pending)
            {
                if (pending.Status != ResourceRequestStatus.Succeeded)
                {
                    throw (Exception)pending.Error ?? new InvalidOperationException("Code payload resource request did not succeed.");
                }

                using var lease = pending.TakeResult();
                var bytes = lease.Asset.bytes;
                var payload = job.Payloads[job.Index];
                HybridCLRManifest.ValidatePayload(payload, bytes);
                if (HybridCLRMetadata.ReadMvid(bytes) != Guid.ParseExact(payload.Mvid, "D"))
                {
                    throw new InvalidDataException("Payload PE metadata MVID does not match the checked descriptor: " + payload.Name);
                }

                job.Bytes.Add(payload.Name, bytes);
                job.Index++;
                job.Request.ReportProgress(0.1f + 0.6f * job.Index / job.Payloads.Length);
            }
        }

        // 先核对进程共享登记，再模拟复用或执行不可逆加载，并提交成功会话。
        private void LoadValidated(Job job)
        {
            RequireAvailable(job.Address);
            var manifest = job.Manifest;
            job.Request.Stage = _calls.IsSimulation ? "Simulation" : "NativePreflight";
            job.Request.ReportProgress(0.7f);
            if (_process.Projects.TryGetValue(manifest.ProjectId, out var existingPackage) && existingPackage != manifest.CodePackageId)
            {
                job.RequiresRestart = true;
                throw new InvalidOperationException("Different code bytes/descriptors are already registered for this project. Restart to change code packages.");
            }
            var domain = _calls.LoadedAssemblies();
            foreach (var payload in job.Payloads)
            {
                if (_process.Assemblies.TryGetValue(payload.Name, out var record))
                {
                    if (record.Identity != payload.Identity || record.Hash != payload.Sha256 || record.Mvid != payload.Mvid ||
                        record.IsMetadata != manifest.AotMetadata.Contains(payload) || !record.IsMetadata && record.Assembly == null)
                    {
                        job.RequiresRestart = true;
                        throw new InvalidOperationException("A different or incomplete assembly is already registered: " + payload.Name);
                    }
                }
                else if (!_calls.IsSimulation && manifest.HotUpdateAssemblies.Contains(payload) && domain.Any(a => string.Equals(a.GetName().Name, payload.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    job.RequiresRestart = true;
                    throw new InvalidOperationException("An untracked hot-update assembly was already loaded by another owner: " + payload.Name);
                }
            }
            var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            if (_calls.IsSimulation)
            {
                foreach (var payload in manifest.HotUpdateAssemblies)
                {
                    var matches = domain.Where(a => HybridCLRManifest.CanonicalIdentity(a.GetName()) == payload.Identity).ToArray();
                    if (matches.Length != 1)
                    {
                        throw new NotSupportedException("Editor Simulation requires exactly one already-loaded assembly; external-only DLLs need native Player validation: " + payload.Name);
                    }

                    loaded.Add(payload.Name, matches[0]);
                    job.Request.AssemblyName = payload.Name;
                    job.Request.ReportProgress(0.7f + 0.3f * loaded.Count / manifest.HotUpdateAssemblies.Length);
                }
            }
            else
            {
                var completedCount = 0;
                foreach (var payload in manifest.AotMetadata)
                {
                    job.Request.Stage = "Metadata";
                    job.Request.AssemblyName = payload.Name;
                    job.Request.ReportProgress(0.7f + 0.3f * completedCount / job.Payloads.Length);
                    if (!_process.Assemblies.ContainsKey(payload.Name))
                    {
                        job.IsIrreversible = true;
                        var code = _calls.LoadMetadata(job.Bytes[payload.Name]);
                        if (code != (int)global::HybridCLR.LoadImageErrorCode.OK)
                        {
                            job.Request.NativeErrorCode = code;
                            throw new InvalidOperationException("LoadMetadataForAOTAssembly failed for " + payload.Name + ": " + code + ". Unknown-owner already-loaded codes are not success.");
                        }
                        _process.Assemblies.Add(payload.Name, Record(payload, true, null));
                    }
                    job.Request.ReportProgress(0.7f + 0.3f * ++completedCount / job.Payloads.Length);
                }
                foreach (var payload in manifest.HotUpdateAssemblies)
                {
                    job.Request.Stage = "AssemblyLoad";
                    job.Request.AssemblyName = payload.Name;
                    job.Request.ReportProgress(0.7f + 0.3f * completedCount / job.Payloads.Length);
                    if (_process.Assemblies.TryGetValue(payload.Name, out var existing))
                    {
                        loaded.Add(payload.Name, existing.Assembly);
                    }
                    else
                    {
                        job.IsIrreversible = true;
                        var assembly = _calls.LoadAssembly(job.Bytes[payload.Name]);
                        if (assembly == null || HybridCLRManifest.CanonicalIdentity(assembly.GetName()) != payload.Identity)
                        {
                            throw new InvalidDataException("Loaded assembly identity did not match its checked PE/manifest: " + payload.Name);
                        }

                        _process.Assemblies.Add(payload.Name, Record(payload, false, assembly));
                        loaded.Add(payload.Name, assembly);
                    }
                    job.Request.ReportProgress(0.7f + 0.3f * ++completedCount / job.Payloads.Length);
                }
            }
            _process.Projects[manifest.ProjectId] = manifest.CodePackageId;
            var result = new HybridCLRLoadResult
            {
                ProjectId = manifest.ProjectId,
                CodePackageId = manifest.CodePackageId,
                Mode = _calls.IsSimulation ? HybridCLRLoadMode.EditorSimulation : HybridCLRLoadMode.Native,
                Assemblies = new ReadOnlyDictionary<string, Assembly>(loaded)
            };
            _sessions.Add(job.Address, new Session { Manifest = job.ManifestLease, Result = result });
            job.ManifestLease = null;
            job.Request.Stage = "Complete";
            job.Request.AssemblyName = null;
            Finish(job, result, null);
        }

        // 从已验证描述创建进程登记，供后续加载器严格识别相同内容。
        private static HybridCLRProcessState.Loaded Record(HybridCLRPayload payload, bool metadata, Assembly assembly) =>
            new HybridCLRProcessState.Loaded { Identity = payload.Identity, Hash = payload.Sha256, Mvid = payload.Mvid, IsMetadata = metadata, Assembly = assembly };

        // 收尾资源与互斥占用；进入不可逆阶段后的错误污染整个进程的后续加载资格。
        private void Finish(Job job, HybridCLRLoadResult result, Exception error)
        {
            if (job.Request.IsDone)
            {
                return;
            }

            if (error != null && job.IsIrreversible)
            {
                _process.RequiresRestart = true;
            }

            var routine = job.ProgressRoutine;
            job.ProgressRoutine = null;
            CoroutineStarter.Stop(routine);
            var pending = job.Pending;
            job.Pending = null;
            pending?.Dispose();
            job.ManifestLease?.Dispose();
            job.ManifestLease = null;
            job.Bytes.Clear();
            if (ReferenceEquals(_active, job))
            {
                _active = null;
            }

            if (ReferenceEquals(_process.ActiveJob, job))
            {
                _process.ActiveJob = null;
            }

            UpdatePendingRegistration();
            // 所有责任和去重记录先提交；完成回调可以同步重入并创建后来请求。
            job.Request.Finish(result, error, job.RequiresRestart || _process.RequiresRestart);
        }

        /// <summary>先加载项目代码，再仅从精确入口程序集解析类型并使用 Core 启动 Module。</summary>
        /// <param name="manifestAddress">项目内规范代码清单地址。</param>
        /// <param name="entryAssemblyName">清单中入口程序集的精确简单名称。</param>
        /// <param name="moduleTypeFullName">入口程序集内具体 Module 类型的完整名称。</param>
        /// <returns>同一地址、程序集及类型的进行中调用共享的启动请求。</returns>
        /// <remarks>进度将代码加载映射到 0 至 0.8，Core 启动映射到 0.8 至 1；Module 关闭后才能释放入口。</remarks>
        public HybridCLRModuleRequest StartModuleAsync(ResourceAddress manifestAddress, string entryAssemblyName, string moduleTypeFullName)
        {
            RequireAvailable(manifestAddress);
            RequireModuleArguments(entryAssemblyName, moduleTypeFullName);
            return StartModuleRequest(_moduleRequests, manifestAddress, entryAssemblyName, moduleTypeFullName, false);
        }

#if UNITY_EDITOR
        /// <summary>显式使用 Editor 已加载的程序集启动 Module；不校验代码包，也不读取或回退到代码资源。</summary>
        /// <param name="projectId">Core 资源访问所属项目身份。</param>
        /// <param name="entryAssemblyName">AppDomain 中唯一匹配的入口程序集简单名称。</param>
        /// <param name="moduleTypeFullName">该程序集内具体 Module 类型的完整名称。</param>
        /// <returns>快路径启动请求；已解析的代码结果使用 EditorFast 模式，CodePackageId 为空。</returns>
        /// <remarks>要求已有 Core 资源绑定，不作加载失败回退；Module 所有权与正式入口相同。</remarks>
        public HybridCLRModuleRequest StartEditorModuleAsync(string projectId, string entryAssemblyName, string moduleTypeFullName)
        {
            AssertThread();
            var address = new ResourceAddress(projectId, HybridCLRManifest.ManifestPath);
            RequireAvailable(address);
            RequireModuleArguments(entryAssemblyName, moduleTypeFullName);
            if (CoreResourceAccess.Provider == null)
            {
                throw new InvalidOperationException("Bind the Core resource provider before starting an EditorFast Module.");
            }

            return StartModuleRequest(_editorModuleRequests, address, entryAssemblyName, moduleTypeFullName, true);
        }
#endif

        // 拒绝空入口名称，避免通过扫描其他程序集猜测 Module 类型。
        private static void RequireModuleArguments(string entryAssemblyName, string moduleTypeFullName)
        {
            if (string.IsNullOrWhiteSpace(entryAssemblyName))
            {
                throw new ArgumentException("An exact entry assembly is required.", nameof(entryAssemblyName));
            }

            if (string.IsNullOrWhiteSpace(moduleTypeFullName))
            {
                throw new ArgumentException("An exact Module type name is required.", nameof(moduleTypeFullName));
            }
        }

        // 按入口合并进行中请求，并统一连接代码、Core 启动及会话失效的收尾路径。
        private HybridCLRModuleRequest StartModuleRequest(Dictionary<(ResourceAddress, string, string), HybridCLRModuleRequest> requests,
            ResourceAddress manifestAddress, string entryAssemblyName, string moduleTypeFullName, bool editorFast)
        {
            var key = (manifestAddress, entryAssemblyName, moduleTypeFullName);
            if (requests.TryGetValue(key, out var existing) && !existing.IsDone)
            {
                return existing;
            }

            var request = new HybridCLRModuleRequest { ProjectId = manifestAddress.ProjectId, AssemblyName = entryAssemblyName };
            requests[key] = request;
            UpdatePendingRegistration();
            // 先解绑观察与去重记录，再通知终态，允许完成回调安全地发起后续请求。
            void FinishModule(Exception error, bool restart = false)
            {
                if (request.IsDone)
                {
                    return;
                }

                var detach = request.Detach;
                request.Detach = null;
                request.Invalidate = null;
                var routine = request.ProgressRoutine;
                request.ProgressRoutine = null;
                CoroutineStarter.Stop(routine);
                detach?.Invoke();
                if (requests.TryGetValue(key, out var current) && ReferenceEquals(current, request))
                {
                    requests.Remove(key);
                }

                UpdatePendingRegistration();
                request.Finish(error, restart);
            }
            request.Invalidate = error =>
            {
                request.Stage = "SessionUnavailable";
                FinishModule(error, !editorFast && _process.RequiresRestart);
            };
            try
            {
#if UNITY_EDITOR
                if (editorFast)
                {
                    request.Stage = "EditorFast";
                    var matches = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == entryAssemblyName).ToArray();
                    if (matches.Length != 1)
                    {
                        throw new TypeLoadException("EditorFast requires exactly one already-loaded entry assembly: " + entryAssemblyName + "; matches=" + matches.Length);
                    }

                    request.Code = new HybridCLRLoadResult
                    {
                        ProjectId = manifestAddress.ProjectId,
                        Mode = HybridCLRLoadMode.EditorFast,
                        Assemblies = new ReadOnlyDictionary<string, Assembly>(new Dictionary<string, Assembly>(StringComparer.Ordinal)
                            { { entryAssemblyName, matches[0] } })
                    };
                    StartCoreModule(manifestAddress, entryAssemblyName, moduleTypeFullName, request, FinishModule);
                    return request;
                }
#endif
                var load = LoadAsync(manifestAddress);
                // 将代码加载的阶段与进度转发到外层 Module 请求。
                void Progressed(HybridCLRLoadRequest source)
                {
                    if (request.IsDone)
                    {
                        return;
                    }

                    request.Stage = source.Stage;
                    request.AssemblyName = source.AssemblyName;
                    request.ReportProgress(source.Progress * 0.8f);
                }
                Action<HybridCLRLoadRequest> continuation = completed =>
                {
                    if (request.IsDone)
                    {
                        return;
                    }

                    var detach = request.Detach;
                    request.Detach = null;
                    detach?.Invoke();
                    request.Code = completed.Result;
                    if (completed.Exception != null)
                    {
                        request.Stage = completed.Stage;
                        request.NativeErrorCode = completed.NativeErrorCode;
                        FinishModule(completed.Exception, completed.RequiresRestart);
                        return;
                    }
                    try
                    {
                        StartCoreModule(manifestAddress, entryAssemblyName, moduleTypeFullName, request, FinishModule);
                    }
                    catch (Exception exception)
                    {
                        FinishModule(exception);
                    }
                };
                request.Detach = () =>
                {
                    load.ProgressChanged -= Progressed;
                    load.RemoveContinuation(continuation);
                };
                load.ProgressChanged += Progressed;
                Progressed(load);
                load.ContinueWith(continuation);
            }
            catch (Exception exception)
            {
                FinishModule(exception);
            }
            return request;
        }

        // 校验精确 Module 类型及所有权，并观察既有 Core 启动请求。
        private void StartCoreModule(ResourceAddress address, string entryAssemblyName, string moduleTypeFullName,
            HybridCLRModuleRequest request, Action<Exception, bool> finish)
        {
            RequireAvailable(address);
            request.Stage = "Module";
            request.AssemblyName = entryAssemblyName;
            var codeWeight = request.Code.Mode == HybridCLRLoadMode.EditorFast ? 0 : 0.8f;
            request.ReportProgress(codeWeight);
            if (!request.Code.Assemblies.TryGetValue(entryAssemblyName, out var entry))
            {
                throw new TypeLoadException("Entry assembly is not registered in this code package: " + entryAssemblyName);
            }

            var type = entry.GetType(moduleTypeFullName, true, false);
            if (type.Assembly != entry || !typeof(Module).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new TypeLoadException("A concrete YL Module type in the exact entry assembly is required: " + moduleTypeFullName);
            }

            var registered = ModuleManager.GetModule(type);
            if (registered != null && !_modules.Contains(registered))
            {
                throw new InvalidOperationException("This Module is already owned by another startup entry.");
            }

            var startup = ModuleManager.StartUpModule(type);
            request.Module = startup.Module;
            _modules.Add(startup.Module);
            // 转发 Core 终态，并在启动失败时移除加载器的 Module 登记。
            void Started(StartUpModuleRequest done)
            {
                if (request.IsDone)
                {
                    return;
                }

                if (done.Exception != null)
                {
                    _modules.Remove(done.Module);
                }

                request.Stage = done.Exception == null ? "Complete" : "Module/" + done.Step;
                finish(done.Exception, false);
            }
            // 以协程检查会话并转发 Core 的实际阶段与进度。
            IEnumerator ObserveCore()
            {
                while (!request.IsDone)
                {
                    try
                    {
                        RequireAvailable(address);
                        // 轮询真实 Core 请求并写入基类属性，不依赖调用方 yield/keepWaiting。
                        if (startup.IsDone)
                        {
                            Started(startup);
                        }
                        else
                        {
                            request.Stage = "Module/" + startup.Step;
                            request.ReportProgress(codeWeight + (1 - codeWeight) * startup.Progress);
                        }
                    }
                    catch (Exception error)
                    {
                        request.Invalidate?.Invoke(error);
                    }
                    if (request.IsDone)
                    {
                        yield break;
                    }

                    yield return null;
                }
            }
            request.Detach = () => startup.RemoveCompleteEvent(Started);
            startup.AddCompleteEvent(Started);
            if (request.IsDone)
            {
                return;
            }

            var routine = CoroutineStarter.Start(ObserveCore());
            if (request.IsDone)
            {
                CoroutineStarter.Stop(routine);
            }
            else if (routine == null)
            {
                finish(new InvalidOperationException("Module startup requires an active coroutine host."), false);
            }
            else
            {
                request.ProgressRoutine = routine;
            }
        }

        /// <summary>在请求结束且本入口 Module 已关闭后释放 manifest 屏障，重复释放无操作。</summary>
        /// <remarks>不关闭借用的 ResourceSystem、不卸载程序集、不清除进程代码身份或重启要求。</remarks>
        /// <exception cref="InvalidOperationException">仍有请求或当前会话内尚未关闭的 Module。</exception>
        public void Dispose()
        {
            AssertThread();
            if (_isDisposed)
            {
                return;
            }

            if (_active != null || PendingModuleRequests.Any(r => !r.IsDone) || !_isExecutionStopping && !_hasSessionEnded && _session == _generation && _modules.Any(m =>
                m.State == ModuleState.Starting || m.State == ModuleState.Running || m.State == ModuleState.Closing))
            {
                throw new InvalidOperationException("Finish pending code/Module startup and close this entry's Modules before disposing its manifest barrier.");
            }

            foreach (var owned in _sessions.Values)
            {
                owned.Manifest.Dispose();
            }

            _sessions.Clear();
            _modules.Clear();
            _isDisposed = true;
            _pendingLoaders.Remove(this);
        }

        // 在接受工作前核对主线程、实例与会话有效性，以及显式清单地址。
        private void RequireAvailable(ResourceAddress address)
        {
            AssertThread();
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(HybridCLRLoader));
            }

            if (_isExecutionStopping || _hasSessionEnded || _session != _generation || !_resources.IsAvailable)
            {
                throw new InvalidOperationException("This HybridCLR loader's Play/Resource session is unavailable.");
            }

            if (address.Path != HybridCLRManifest.ManifestPath || string.IsNullOrEmpty(address.ProjectId))
            {
                throw new ArgumentException("Use the explicit project's canonical HybridCLR manifest address.", nameof(address));
            }
        }

        // 将 Unity 对象、资源及 Module 操作限制在初始化记录的主线程。
        private static void AssertThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
            {
                throw new InvalidOperationException("HybridCLR/Resource/Module operations require the Unity main thread.");
            }
        }
    }
}
