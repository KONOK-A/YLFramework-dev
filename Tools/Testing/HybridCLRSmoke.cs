using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.AddressableAssets.Initialization;
using YLFramework.Core;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Resource;
using YLFramework.Samples.HybridCLR;
using Debug = UnityEngine.Debug;

namespace YLFramework.Testing
{
    /// <summary>产品根外的显式原生消费者，不静态引用热更类型，不采用 Editor 模拟。</summary>
    public sealed class HybridCLRSmoke : MonoBehaviour
    {
        public string resultId;
        public string hostId;
        private readonly List<string> _stages = new();
        private string _output;
        private string _runtimeError = string.Empty;
        private HybridCLRExample _sample;
        private ResourceSystem _resources;
        private Detail _detail;
        private int _firstFrame;
        private Receipt _receipt;

        private void Awake()
        {
            _firstFrame = Time.frameCount;
            Application.runInBackground = true;
            Application.logMessageReceived += ObserveLog;
        }

        private void OnDestroy() => Application.logMessageReceived -= ObserveLog;

        private void ObserveLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
            {
                _runtimeError += message + Environment.NewLine;
            }
        }

        private IEnumerator Start()
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(Run());
            Exception failure = null;
            while (stack.Count != 0 && failure == null)
            {
                bool next = false;
                object current = null;
                try
                {
                    next = stack.Peek().MoveNext();
                    if (next)
                    {
                        current = stack.Peek().Current;
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
                if (failure != null)
                {
                    break;
                }
                if (!next)
                {
                    ((IDisposable)stack.Pop()).Dispose();
                }
                else if (current is IEnumerator nested && !(current is CustomYieldInstruction))
                {
                    stack.Push(nested);
                }
                else
                {
                    yield return current;
                }
            }
            foreach (var remaining in stack)
            {
                try
                {
                    ((IDisposable)remaining).Dispose();
                }
                catch (Exception error)
                {
                    failure ??= error;
                    Debug.LogException(error);
                }
            }
            if (failure == null && _runtimeError.Length != 0)
            {
                failure = new InvalidOperationException(_runtimeError);
            }
            if (failure != null)
            {
                Debug.LogException(failure);
            }
            try
            {
                if (_output != null)
                {
                    var diagnostics = (_resources ?? _sample?.Startup?.Resources)?.GetDiagnostics();
                    var report = new Report
                    {
                        resultId = resultId,
                        hostId = hostId,
                        status = failure == null ? "Passed" : "Failed",
                        error = failure == null ? string.Empty : failure.ToString() + _runtimeError,
                        unityVersion = Application.unityVersion,
                        pid = Process.GetCurrentProcess().Id,
                        isEditor = Application.isEditor,
                        platform = Application.platform.ToString(),
                        graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                        frames = Time.frameCount - _firstFrame,
                        inputHash = _receipt?.inputHash,
                        contentHash = _receipt?.contentHash,
                        addressablesVersion = _receipt?.addressablesVersion,
                        ledgerMeasured = diagnostics != null,
                        resourceState = diagnostics?.State.ToString(),
                        scopeCount = diagnostics?.ScopeCount ?? -1,
                        requestCount = diagnostics?.RequestCount ?? -1,
                        pendingWorkCount = diagnostics?.PendingWorkCount ?? -1,
                        drainingHandleCount = diagnostics?.DrainingHandleCount ?? -1,
                        cleanupErrors = diagnostics?.Errors.ToArray() ?? new[] { "Diagnostics unavailable" },
                        leaseCount = diagnostics?.LeaseCount ?? -1,
                        stages = _stages.ToArray(),
                        moduleStage = _sample?.ModuleRequest?.Stage,
                        moduleException = _sample?.ModuleRequest?.Exception?.ToString(),
                        observedCodePackageId = _sample?.ModuleRequest?.Code?.CodePackageId,
                        startupCatalogUpdated = _sample?.StartupCatalogUpdated ?? false,
                        hybridClr = _detail
                    };
                    using var stream = new FileStream(Path.Combine(_output, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.Write(JsonUtility.ToJson(report, true));
                }
            }
            catch (Exception error)
            {
                failure ??= error;
                Debug.LogException(error);
            }
            if (failure == null)
            {
                Debug.Log("[YLPlayerSmoke] PASS " + resultId);
            }
            Application.Quit(failure == null ? 0 : 1);
        }

        private IEnumerator Run()
        {
#if !UNITY_EDITOR && ENABLE_IL2CPP
            bool native = true;
#else
            bool native = false;
#endif
            Check(native && Application.isPlaying && Application.platform == RuntimePlatform.WindowsPlayer, "A real Windows64 IL2CPP Player is required.");
            var output = Path.GetFullPath(Argument("-ylSmokeOutput"));
            Check(Directory.Exists(output), "An existing owned output is required.");
            CheckNoLinks(output);
            _output = output;
            var receiptPath = Path.GetFullPath(Argument("-ylHybridCLRReceipt"));
            CheckNoLinks(receiptPath);
            Check(receiptPath.StartsWith(Path.GetDirectoryName(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "The runtime receipt must belong to this result root.");
            var receipt = JsonUtility.FromJson<Receipt>(File.ReadAllText(receiptPath));
            _receipt = receipt;
            Check(receipt.resultId == resultId && receipt.hostId == hostId && receipt.unityVersion == Application.unityVersion,
                "A foreign native build receipt is refused.");
            CheckNoLinks(receipt.cacheRoot);
            var parent = Path.GetDirectoryName(Path.GetFullPath(receipt.cacheRoot));
            var owner = JsonUtility.FromJson<CacheOwner>(File.ReadAllText(Path.Combine(parent, ".yl-owner.json")));
            Check(owner.purpose == "YLTesting.Directory" && owner.kind == "Cache" && owner.id == receipt.cacheOwnerId &&
                string.Equals(owner.path, parent, StringComparison.OrdinalIgnoreCase), "An explicit leased runtime cache owner is required.");
            Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && ResourceSystem.Current == null,
                "A fresh native process and real graphics device are required for the Bundle sample.");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => receipt.assemblies.Contains(assembly.GetName().Name)),
                "Hot assemblies must not be statically linked or preloaded.");
            _stages.Add("Started");
            Check(ContentHash(receipt.cacheRoot) == receipt.contentHash, "Actual official AA publication bytes differ from the receipt.");
            Check(receipt.sampleVersion == "v1" || receipt.sampleVersion == "v2", "An exact sample revision is required.");
            var patch = receipt.sampleVersion == "v2";
            _sample = new HybridCLRExample();
            yield return _sample.Run(new ResourceConfiguration(enableStartupCatalogUpdates: patch), receipt.projectId, receipt.compatibilityId,
                patch ? ResourceManagerRuntimeData.kCatalogAddress : null);
            _resources = _sample.Startup.Resources;
            Check(_sample.StartupCatalogUpdated == patch, "Only the patch run must apply an explicit official startup catalog update.");
            var code = _sample.ModuleRequest.Code;
            Check(_sample.ModuleRequest.Exception == null && code != null && code.Mode == HybridCLRLoadMode.Native && !code.IsSimulation &&
                code.CodePackageId == receipt.codePackageId,
                $"Native code identity mismatch: stage={_sample.ModuleRequest.Stage}, expected={receipt.codePackageId}, observed={code?.CodePackageId}.");
            HybridCLRManifest manifest;
            using (var request = _sample.Startup.Resources.AcquireAsync<TextAsset>(receipt.manifestAddress))
            {
                yield return request;
                using var lease = request.TakeResult();
                Check(Hash(lease.Asset.bytes) == receipt.manifestSha256, "The loaded official Manifest bytes differ from publication.");
                manifest = HybridCLRManifest.Parse(lease.Asset.text);
            }
            Check(manifest.SchemaVersion == 2 && manifest.CodePackageId == receipt.codePackageId && manifest.AotMetadata.Length > 0 &&
                manifest.AotMetadata.Select(payload => payload.Name).SequenceEqual(receipt.metadataAssemblies), "The loaded metadata manifest differs from the final base.");
            _stages.Add("HybridCLRMetadata");
            _detail = new Detail
            {
                mode = code.Mode.ToString(),
                isSimulation = code.IsSimulation,
                codePackageId = code.CodePackageId,
                nativeVariant = receipt.nativeVariant,
                sampleVersion = receipt.sampleVersion,
                manifestSchemaVersion = manifest.SchemaVersion,
                manifestAddress = receipt.manifestAddress,
                manifestSha256 = receipt.manifestSha256,
                metadataAssemblies = manifest.AotMetadata.Select(payload => payload.Name).ToArray(),
                assemblies = code.Assemblies.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                message = HybridCLRExampleState.Message,
                asyncResult = HybridCLRExampleState.Async.GetAwaiter().GetResult(),
                prefabMarker = _sample.PrefabMarker
            };
            var payloads = new List<Payload>();
            using (var observed = _resources.CreateScope("native byte observations"))
            {
                foreach (var payload in manifest.AotMetadata.Concat(manifest.HotUpdateAssemblies))
                {
                    using var reading = observed.LoadAsync<TextAsset>(HybridCLRManifest.ResourceKey(receipt.projectId, payload.Address));
                    yield return reading;
                    var bytes = reading.TakeResult().bytes;
                    HybridCLRManifest.ValidatePayload(payload, bytes);
                    payloads.Add(new Payload
                    {
                        role = manifest.AotMetadata.Contains(payload) ? "AotMetadata" : "HotUpdate",
                        name = payload.Name,
                        address = payload.Address,
                        sha256 = Hash(bytes),
                        length = bytes.LongLength,
                        mvid = payload.Mvid,
                        fullName = payload.Identity
                    });
                }
                using var closed = observed.CloseAsync();
                yield return closed;
                Check(closed.TakeResult(), "Native byte observation scope did not close.");
            }
            _detail.payloads = payloads.ToArray();
            Check((receipt.sampleVersion == "v1" || receipt.sampleVersion == "v2") && _sample.ModuleRequest.Module.State == ModuleState.Running &&
                _detail.message == receipt.sampleVersion + ": YL Resource configuration; dependency-count=2" &&
                _detail.asyncResult == receipt.sampleVersion + "-async" && _detail.prefabMarker == receipt.sampleVersion + "-prefab", "The real versioned hot Module, async or prefab did not execute.");
            _stages.Add("HybridCLRModule");
            var support = code.Assemblies["YLFramework.HybridCLR.Sample.Support"].GetType("YLFramework.Samples.HybridCLR.HotUpdate.VersionInfo", true);
            _detail.reflectionCount = (int)support.GetMethod("Count").MakeGenericMethod(typeof(string)).Invoke(null,
                new object[] { new List<string> { "a", "b", "c" } });
            Check(_detail.reflectionCount == 3, "The interpreted generic method was not callable by reflection.");
            _stages.Add("HybridCLRReflection");
            var repeated = _sample.Loader.LoadAsync(new ResourceAddress(receipt.projectId, HybridCLRManifest.ManifestPath));
            yield return repeated;
            _detail.repeatedLoadReused = repeated.Exception == null && ReferenceEquals(repeated.Result, code);
            using (var wrongBase = new HybridCLRLoader(_sample.Startup.Resources, "wrong-base"))
            {
                var rejected = wrongBase.LoadAsync(new ResourceAddress(receipt.projectId, HybridCLRManifest.ManifestPath));
                yield return rejected;
                _detail.wrongBaseRejected = rejected.Exception != null && rejected.Stage == "Manifest" && !rejected.RequiresRestart;
            }
            Check(_detail.repeatedLoadReused && _detail.wrongBaseRejected, "Code identity reuse or wrong-base protection did not hold.");
            using var shutdown = _sample.CloseAsync();
            yield return shutdown;
            Check(shutdown.TakeResult() && ResourceSystem.Current == null, "The owned native sample did not close.");
            var final = _resources.GetDiagnostics();
            Check(final.State == ResourceSystemState.Closed && final.LeaseCount == 0 && final.ScopeCount == 0 &&
                final.RequestCount == 0 && final.PendingWorkCount == 0 && final.DrainingHandleCount == 0 && final.Errors.Count == 0 && _runtimeError.Length == 0,
                "Native schema2 ownership ledgers or runtime errors remain.");
            Check(ContentHash(receipt.cacheRoot) == receipt.contentHash, "Official AA publication changed during execution.");
            _stages.Add("HybridCLRShutdown");
        }

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            Check(index >= 0 && index + 1 < args.Length && index == Array.LastIndexOf(args, name), "One explicit " + name + " is required.");
            return args[index + 1];
        }

        private static void CheckNoLinks(string path)
        {
            for (var cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                Check(!File.Exists(cursor) && !Directory.Exists(cursor) || (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0,
                    "Linked runtime paths are refused.");
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        [Serializable]
        private sealed class CacheOwner
        {
            public string purpose;
            public string kind;
            public string id;
            public string path;
        }

        [Serializable]
        private sealed class Receipt
        {
            public string resultId;
            public string hostId;
            public string unityVersion;
            public string projectId;
            public string compatibilityId;
            public string inputHash, contentHash, addressablesVersion, sampleVersion, manifestAddress, manifestSha256;
            public string codePackageId;
            public string nativeVariant;
            public string cacheRoot;
            public string cacheOwnerId;
            public string[] metadataAssemblies;
            public string[] assemblies;
        }

        [Serializable]
        private sealed class Detail
        {
            public string mode;
            public bool isSimulation;
            public string codePackageId;
            public string nativeVariant;
            public string[] metadataAssemblies;
            public string[] assemblies;
            public string message;
            public string asyncResult;
            public string prefabMarker;
            public int reflectionCount;
            public bool repeatedLoadReused;
            public bool wrongBaseRejected;
            public string sampleVersion, manifestAddress, manifestSha256;
            public int manifestSchemaVersion;
            public Payload[] payloads;
        }

        [Serializable]
        private sealed class Payload
        {
            public string role, name, address, sha256, mvid, fullName;
            public long length;
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static string ContentHash(string root)
        {
            var rows = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path => new
            {
                path = path.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'),
                hash = Hash(File.ReadAllBytes(path))
            }).OrderBy(row => row.path, StringComparer.Ordinal);
            return Hash(Encoding.UTF8.GetBytes(string.Concat(rows.Select(row => row.path + "\0" + row.hash + "\n"))));
        }

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 2;
            public string purpose = "YLTesting.PlayerSmoke";
#if ENABLE_IL2CPP
            public string backend = "IL2CPP";
#else
            public string backend = "Mono";
#endif
            public string resultId;
            public string hostId;
            public string status;
            public string error;
            public string unityVersion;
            public int pid;
            public bool isEditor;
            public string platform;
            public string graphicsDevice;
            public int frames;
            public string inputHash, contentHash, addressablesVersion, resourceState;
            public int scopeCount, requestCount, pendingWorkCount, drainingHandleCount;
            public bool ledgerMeasured;
            public string[] cleanupErrors;
            public int leaseCount;
            public string[] stages;
            public string moduleStage;
            public string moduleException;
            public string observedCodePackageId;
            public bool startupCatalogUpdated;
            public Detail hybridClr;
        }
    }
}
