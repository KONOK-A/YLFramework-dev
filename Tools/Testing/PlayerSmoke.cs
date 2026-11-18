using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using YLFramework.Events;
using YLFramework.Resource;

namespace YLFramework.Testing
{
    public sealed class PlayerSmoke : MonoBehaviour
    {
        public const string MessageKey = "distribution/message";
        [Serializable]
        private sealed class Result
        {
            public int schemaVersion = 2;
            public string purpose = "YLTesting.PlayerSmoke";
            public string status, error, resultId, hostId, unityVersion, runtimeAssembly, platform, backend, graphicsDevice;
            public string inputHash, contentHash, addressablesVersion, resourceState;
            public int pid, frames, leaseCount, scopeCount, requestCount, pendingWorkCount, drainingHandleCount;
            public bool isEditor, ledgerMeasured;
            public string[] stages, cleanupErrors = Array.Empty<string>();
        }

        public string resultId, hostId, resultRoot, inputHash, contentHash, addressablesVersion;
        private readonly System.Collections.Generic.List<string> _stages = new();
        private string _output;
        private ResourceStartup _startup;
        private ResourceSystem _system;
        private string _runtimeError = string.Empty;
        private int _firstFrame;

        private void Awake()
        {
            _firstFrame = Time.frameCount;
            Application.logMessageReceived += ObserveLog;
        }
        private void OnDestroy() => Application.logMessageReceived -= ObserveLog;
        private void ObserveLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception) _runtimeError += message + Environment.NewLine;
        }

        private IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-ylSmokeOutput");
            if (index < 0 || index + 1 >= args.Length) { Application.Quit(9); yield break; }
            _output = Path.Combine(Path.GetFullPath(args[index + 1]), "result.json");
            if (File.Exists(_output)) { Application.Quit(9); yield break; }
            var root = Path.GetFullPath(resultRoot);
            if (!_output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Application.Quit(9); yield break; }
            for (var path = Path.GetDirectoryName(_output); path != null; path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Application.Quit(9); yield break; }
            var work = Run();
            while (true)
            {
                object next = null;
                bool more;
                try { more = work.MoveNext(); if (more) next = work.Current; }
                catch (Exception error)
                {
                    (work as IDisposable)?.Dispose();
                    Finish(error);
                    yield break;
                }
                if (!more) break;
                yield return next;
            }
            Finish(null);
        }

        private IEnumerator Run()
        {
            Require(!Application.isEditor && Application.isPlaying && Application.platform == RuntimePlatform.WindowsPlayer && ResourceSystem.Current == null &&
                !string.IsNullOrEmpty(resultId) && !string.IsNullOrEmpty(hostId), "Smoke must run in a fresh actual Windows Player.");
            Require(ContentFingerprint() == contentHash, "Actual StreamingAssets/aa content differs from its build receipt.");
            _stages.Add("Started");
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = _startup.StartAsync();
            while (!starting.IsDone) yield return null;
            _ = starting.TakeResult();
            _system = _startup.Resources;
            using var scope = _system.CreateScope("distribution smoke scope");
            using var loading = scope.LoadAsync<TextAsset>(MessageKey);
            while (!loading.IsDone) yield return null;
            Require(loading.TakeResult().text == "YLFramework distribution AA smoke", "Official AA TextAsset payload differs.");
            using var acquiring = _system.AcquireAsync<TextAsset>(MessageKey);
            while (!acquiring.IsDone) yield return null;
            using var independent = acquiring.TakeResult();
            _stages.Add("ResourceLoaded");
            int observed = 0;
            using (var channel = new EventChannel())
            {
                var subscription = channel.Register(() => observed++);
                try
                {
                    channel.TriggerAll();
                    Require(observed == 1, "Event registration did not receive its notification.");
                    subscription.Unregister();
                    channel.TriggerAll();
                    Require(observed == 1, "Event unregistration did not take effect.");
                }
                finally { subscription.Unregister(); }
            }
            _stages.Add("EventLifecycle");
            using var busy = _startup.StopAsync();
            while (!busy.IsDone) yield return null;
            Require(busy.Status == ResourceRequestStatus.Failed && busy.Error?.Kind == ResourceError.Busy &&
                _startup.OwnsResourceSystem && independent.IsValid && !scope.IsDisposed && ReferenceEquals(_startup.Resources, _system),
                "Busy shutdown revoked another owner's lease/scope.");
            _stages.Add("BusyOwnerProtected");
            independent.Dispose();
            using var closing = scope.CloseAsync();
            while (!closing.IsDone) yield return null;
            Require(closing.TakeResult(), "Scope did not close.");
            Require(!independent.IsValid && _system.GetDiagnostics().LeaseCount == 0, "Actual resource lease release did not complete.");
            _stages.Add("ResourceReleased");
            using var stopping = _startup.StopAsync();
            while (!stopping.IsDone) yield return null;
            Require(stopping.TakeResult() && !_startup.OwnsResourceSystem, "Startup did not finish its owned shutdown.");
            yield return null;
            var diagnostics = _system.GetDiagnostics();
            Require(diagnostics.State == ResourceSystemState.Closed && diagnostics.LeaseCount == 0 &&
                diagnostics.ScopeCount == 0 && diagnostics.RequestCount == 0 && diagnostics.PendingWorkCount == 0 &&
                diagnostics.DrainingHandleCount == 0 && diagnostics.Errors.Count == 0 && _runtimeError.Length == 0 && ResourceSystem.Current == null,
                "The five schema2 ledgers or runtime errors did not drain.");
            Require(ContentFingerprint() == contentHash, "Player content changed during its execution.");
            _stages.Add("Shutdown");
        }

        private static string ContentFingerprint()
        {
            var root = Path.Combine(Application.streamingAssetsPath, "aa");
            var rows = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path => new
            {
                path = path.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'),
                hash = Hash(File.ReadAllBytes(path))
            }).OrderBy(row => row.path, StringComparer.Ordinal);
            return Hash(Encoding.UTF8.GetBytes(string.Concat(rows.Select(row => row.path + "\0" + row.hash + "\n"))));
        }
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private void Finish(Exception error)
        {
            var diagnostics = _system?.GetDiagnostics();
            var result = new Result
            {
                status = error == null && _runtimeError.Length == 0 ? "Passed" : "Failed",
                error = (error?.ToString() ?? "") + _runtimeError,
                resultId = resultId,
                hostId = hostId,
                inputHash = inputHash,
                contentHash = contentHash,
                addressablesVersion = addressablesVersion,
                unityVersion = Application.unityVersion,
                runtimeAssembly = typeof(ResourceSystem).Assembly.GetName().Name,
                platform = Application.platform.ToString(),
                isEditor = Application.isEditor,
                frames = Time.frameCount - _firstFrame,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                ledgerMeasured = diagnostics != null,
                resourceState = diagnostics?.State.ToString(),
                leaseCount = diagnostics?.LeaseCount ?? -1,
                scopeCount = diagnostics?.ScopeCount ?? -1,
                requestCount = diagnostics?.RequestCount ?? -1,
                pendingWorkCount = diagnostics?.PendingWorkCount ?? -1,
                drainingHandleCount = diagnostics?.DrainingHandleCount ?? -1,
                cleanupErrors = diagnostics?.Errors.ToArray() ?? new[] { "Diagnostics unavailable" },
                stages = _stages.ToArray(),
#if ENABLE_IL2CPP
                backend = "IL2CPP"
#else
                backend = "Mono"
#endif
            };
            using (var stream = new FileStream(_output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(result, true));
            if (result.status == "Passed") Debug.Log("[YLPlayerSmoke] PASS " + resultId); else if (error != null) Debug.LogException(error);
            Application.Quit(result.status == "Passed" ? 0 : 1);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
