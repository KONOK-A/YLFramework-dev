using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using YLFramework.Events;
using YLFramework.Resource;
using Debug = UnityEngine.Debug;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Testing
{
    /// <summary>仅显式安装到分发接收宿主的 Player 冒烟，不进入正式包。</summary>
    public sealed class PlayerSmoke : MonoBehaviour
    {
        public string resultId;
        public string hostId;
        private readonly List<string> _stages = new();
        private string _output;
        private string _runtimeError = string.Empty;
        private ResourceSystem _resources;
        private int _firstFrame;

        private void Awake()
        {
            _firstFrame = Time.frameCount;
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
            var routine = Run();
            while (true)
            {
                object current = null;
                bool next = false;
                Exception failure = null;
                try
                {
                    next = routine.MoveNext();
                    if (next)
                    {
                        current = routine.Current;
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
                if (failure != null)
                {
                    Debug.LogException(failure);
                    WriteReport("Failed", failure.ToString());
                    Application.Quit(1);
                    yield break;
                }
                if (!next)
                {
                    ((IDisposable)routine).Dispose();
                    WriteReport("Passed", string.Empty);
                    Debug.Log("[YLPlayerSmoke] PASS " + resultId);
                    Application.Quit(0);
                    yield break;
                }
                yield return current;
            }
        }

        private IEnumerator Run()
        {
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, "-ylSmokeOutput");
            Check(index >= 0 && index + 1 < arguments.Length, "Explicit owned output is required.");
            _output = Path.GetFullPath(arguments[index + 1]);
            Check(Directory.Exists(_output), "Output must already exist.");
            for (var path = _output; path != null; path = Path.GetDirectoryName(path))
            {
                Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Linked output is refused.");
            }
            Check(!Application.isEditor && Application.isPlaying && Application.platform == RuntimePlatform.WindowsPlayer,
                "A real Windows Player must execute the scene.");
            Check(!string.IsNullOrEmpty(resultId) && !string.IsNullOrEmpty(hostId), "Build identity is missing.");
            Check(ResourceSystem.Current == null, "The Player must start fresh.");
            _stages.Add("Started");
            yield return null;

            var address = new ResourceAddress("player-smoke", "message");
            using (var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog("player-smoke", new[]
                {
                    new ResourceCatalogEntry("message", address,
                        new ResourceLocation(ResourceSourceId.Resources, "YLPlayerSmoke/Message", typeof(TextAsset)))
                })
            })))
            {
                yield return initialized;
                _resources = initialized.TakeResult();
            }
            AssetLease<TextAsset> released;
            using (var request = _resources.AcquireAsync<TextAsset>(address))
            {
                yield return request;
                using (var lease = request.TakeResult())
                {
                    released = lease;
                    Check(lease.IsValid && lease.Asset.text.Trim() == "yl-player-smoke-v1", "Real Resources payload did not load.");
                    _stages.Add("ResourceLoaded");
                    int changes = 0;
                    using (var channel = new EventChannel())
                    {
                        var subscription = channel.Register(() => changes++);
                        try
                        {
                            channel.TriggerAll();
                            Check(changes == 1, "The core event registration did not receive its notification.");
                            subscription.Unregister();
                            channel.TriggerAll();
                            Check(changes == 1, "The core event registration was not released.");
                        }
                        finally
                        {
                            subscription.Unregister();
                        }
                    }
                    _stages.Add("EventLifecycle");
                    using var busy = _resources.ShutdownAsync();
                    Check(busy.Status == ResourceRequestStatus.Failed && busy.Error.Kind == ResourceError.Busy && lease.IsValid,
                        "Shutdown must preserve a caller's live lease.");
                    _stages.Add("BusyOwnerProtected");
                }
            }
            Check(!released.IsValid && _resources.GetDiagnosticsSnapshot().LeaseCount == 0, "Resource lease release did not complete.");
            _stages.Add("ResourceReleased");
            using (var shutdown = _resources.ShutdownAsync())
            {
                yield return shutdown;
                Check(shutdown.TakeResult() && ResourceSystem.Current == null && _resources.State == ResourceSystemState.Closed,
                    "The real Resource authority did not close.");
            }
            yield return null;
            Check(_resources.GetDiagnosticsSnapshot().RecordCount == 0 && _runtimeError.Length == 0, "Runtime errors or resources remain.");
            _stages.Add("Shutdown");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private void WriteReport(string status, string error)
        {
            if (_output == null)
            {
                return;
            }
            var diagnostics = _resources?.GetDiagnosticsSnapshot();
            var report = new Report
            {
                resultId = resultId,
                hostId = hostId,
                status = status,
                error = error + _runtimeError,
                unityVersion = Application.unityVersion,
#if ENABLE_IL2CPP
                backend = "IL2CPP",
#else
                backend = "Mono",
#endif
                pid = Process.GetCurrentProcess().Id,
                isEditor = Application.isEditor,
                platform = Application.platform.ToString(),
                frames = Time.frameCount - _firstFrame,
                recordCount = diagnostics?.RecordCount ?? -1,
                leaseCount = diagnostics?.LeaseCount ?? -1,
                stages = _stages.ToArray()
            };
            using var stream = new FileStream(Path.Combine(_output, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(JsonUtility.ToJson(report, true));
        }

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 1;
            public string purpose = "YLTesting.PlayerSmoke";
            public string resultId;
            public string hostId;
            public string status;
            public string error;
            public string unityVersion;
            public string backend;
            public int pid;
            public bool isEditor;
            public string platform;
            public int frames;
            public int recordCount;
            public int leaseCount;
            public string[] stages;
        }
    }
}
