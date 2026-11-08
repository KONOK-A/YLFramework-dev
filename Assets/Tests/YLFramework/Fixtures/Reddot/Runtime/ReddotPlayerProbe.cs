using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.RedDots.Acceptance
{
    public sealed class ReddotPlayerProbe : MonoBehaviour
    {
        [Serializable]
        public sealed class Measurement
        {
            public bool batch;
            public int groups = 4;
            public int branchesPerGroup = 4;
            public int leavesPerBranch = 4;
            public int pathDepth = 4;
            public int sourcesPerLeaf = 3;
            public int echoSources = 1;
            public int warmIterations = 8;
            public int iterations = 128;
            public int updates = 24576;
            public int warmedReadPairs = 100000;
            public long finalValue;
            public int rootNotifications;
            public int leafNotifications;
            public int maximumCallbackDepth;
            public bool allocationCounterReliable;
            public long allocationCalibrationBytes;
            public double updateMilliseconds;
            public long updateAllocatedBytes;
            public long readChecksum;
            public double readMilliseconds;
            public long readAllocatedBytes;
            public int diagnosticNodes;
            public int diagnosticSources;
            public int diagnosticSubscribers;
            public double diagnosticMilliseconds;
            public long diagnosticAllocatedBytes;
        }

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string backend;
            public string runtimeAssembly;
            public string probeAssembly;
            public string catalogDigest = string.Empty;
            public int pid;
            public int round;
            public string token;
            public int leaseCount = -1;
            public int recordCount = -1;
            public bool resourceWasNull;
            public string machine;
            public string operatingSystem;
            public string processor;
            public int processorCount;
            public int pointerBits;
            public string clrVersion;
            public string[] frameworkAssemblies;
            public bool serializedIndicator;
            public bool indicatorLifecycle;
            public bool aggregationAndBatch;
            public bool readStateRoundTrip;
            public bool oldGenerationRejected;
            public long generation;
            public long captureSequence;
            public int diagnosticNodes;
            public int diagnosticSources;
            public int diagnosticSubscribers;
            public int dirtyNodeCount;
            public int batchDepth;
            public Measurement[] measurements;
        }

        [Serializable]
        public sealed class Owner
        {
            public int schemaVersion = 1;
            public string token;
            public string player;
        }

        public string runToken;
        public Reddot binding;
        private readonly Report _report = new();
        private string _runtimeError;

        private IEnumerator Start()
        {
            string result = null;
            var exercise = Exercise();
            Application.logMessageReceived += CaptureError;
            try
            {
                try
                {
                    _report.unityVersion = Application.unityVersion;
                    _report.platform = Application.platform.ToString();
                    _report.isEditor = Application.isEditor;
#if ENABLE_MONO
                    _report.backend = "Mono";
#else
                    _report.backend = "NotMono";
#endif
                    _report.pid = Process.GetCurrentProcess().Id;
                    _report.token = runToken;
                    _report.runtimeAssembly = typeof(ReddotManager).Assembly.GetName().Name;
                    _report.probeAssembly = typeof(ReddotPlayerProbe).Assembly.GetName().Name;
                    _report.machine = Environment.MachineName;
                    _report.operatingSystem = Environment.OSVersion.ToString();
                    _report.processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                    _report.processorCount = Environment.ProcessorCount;
                    _report.pointerBits = IntPtr.Size * 8;
                    _report.clrVersion = Environment.Version.ToString();
                    var args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "-ylFeatureResult");
                    Require(index > 0 && index + 1 < args.Length, "Missing shared fixture result argument.");
                    var directory = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                    var candidate = Path.GetFullPath(args[index + 1]);
                    Require(string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase) &&
                        (Path.GetFileName(candidate) == "result-1.json" || Path.GetFileName(candidate) == "result-2.json") &&
                        !File.Exists(candidate) && !Directory.Exists(candidate), "Result must be new and inside this owned Player.");
                    NoLinks(directory);
                    string ownerPath = Path.Combine(directory, ".reddot-owner.json");
                    NoLinks(ownerPath);
                    var owner = JsonUtility.FromJson<Owner>(File.ReadAllText(ownerPath));
                    Require(Guid.TryParseExact(runToken, "N", out _) && owner != null && owner.schemaVersion == 1 &&
                        owner.token == runToken && string.Equals(Path.GetFullPath(owner.player), directory, StringComparison.OrdinalIgnoreCase),
                        "Player owner must match the built scene's serialized run token.");
                    result = candidate;
                    _report.round = Path.GetFileName(candidate) == "result-1.json" ? 1 : 2;
                    Require(!_report.isEditor && _report.platform == "WindowsPlayer" && _report.backend == "Mono", "Only an actual Windows Mono Player qualifies.");
                }
                catch (Exception error)
                {
                    _report.error = error.ToString();
                }
                while (_report.error == null)
                {
                    bool more;
                    try
                    {
                        Require(_runtimeError == null, _runtimeError);
                        more = exercise.MoveNext();
                        Require(_runtimeError == null, _runtimeError);
                    }
                    catch (Exception error)
                    {
                        _report.error = error.ToString();
                        break;
                    }
                    if (!more)
                    {
                        _report.status = "Passed";
                        break;
                    }
                    yield return exercise.Current;
                }
            }
            finally
            {
                Application.logMessageReceived -= CaptureError;
                (exercise as IDisposable)?.Dispose();
                _report.frameworkAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name)
                    .Where(name => name.StartsWith("YLFramework", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                _report.status = _report.status == "Passed" ? "Passed" : "Failed";
                var persisted = false;
                try
                {
                    if (result != null)
                    {
                        using var stream = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                        writer.Write(JsonUtility.ToJson(_report, true));
                        writer.Flush();
                        persisted = true;
                    }
                }
                finally
                {
                    Application.Quit(_report.status == "Passed" && persisted ? 0 : 1);
                }
            }
        }

        private IEnumerator Exercise()
        {
            Require(_report.runtimeAssembly == "YLFramework.RedDots" && _report.probeAssembly == "YLFramework.RedDots.Acceptance", "Incorrect runtime ownership.");
            Require(ResourceSystem.Current == null, "Do not initialize or take over Resource for a red-dot test.");
            Require(binding && binding.Path == "UI/Mail" && binding.Indicator && !binding.Indicator.activeSelf, "The built scene lost its serialized path or indicator.");
            Require(ReddotManager.ReddotPath.Count == 0 && ReddotManager.GetValue(null) == 0, "A UI subscriber must not own business sources or imply a root.");
            _report.serializedIndicator = true;
            using (var source = ReddotManager.RegisterSource("UI/Mail/Gift", "gift"))
            {
                source.SetValue(3);
                Require(binding.Indicator.activeSelf, "The actual indicator did not respond to its path subscription.");
                binding.enabled = false;
                source.SetValue(0);
                Require(binding.Indicator.activeSelf && ReddotManager.CaptureDiagnostics().Nodes.Single(node => node.Path == "UI/Mail/").SubscriberCount == 0,
                    "Disabled indicator must unsubscribe without silently resetting its old visual state.");
                binding.enabled = true;
                Require(!binding.Indicator.activeSelf, "Re-enabling must catch up immediately.");
                source.SetValue(1);
                binding.Path = "UI/Other";
                Require(!binding.Indicator.activeSelf, "Changing paths must stop displaying the previous path.");
                using var other = ReddotManager.RegisterSource("UI/Other", "other", 2);
                Require(binding.Indicator.activeSelf, "The replacement path was not subscribed.");
                Destroy(binding.gameObject);
                yield return null;
                Require(!binding && ReddotManager.CaptureDiagnostics().Nodes.Where(node => node.Path.StartsWith("UI/", StringComparison.Ordinal))
                    .Sum(node => node.SubscriberCount) == 0, "Destroyed UI leaked a subscription.");
                _report.indicatorLifecycle = true;
            }

            using (var first = ReddotManager.RegisterSource("Core/A/X", "first", 2))
            using (var second = ReddotManager.RegisterSource("Core/A/X", "second", 3))
            using (var third = ReddotManager.RegisterSource("Core/B", "third", 7))
            {
                Require(ReddotManager.GetValue("Core") == 12, "Real Sum propagation failed.");
                ReddotManager.SetAggregation("Core/A", ReddotAggregation.Any);
                Require(ReddotManager.GetValue("Core") == 8, "Real Any propagation failed.");
                ReddotManager.SetAggregation("Core", ReddotAggregation.Max);
                Require(ReddotManager.GetSnapshot("Core").Value == 7, "Real Max propagation failed.");
                var values = new List<long>();
                using var subscription = ReddotManager.Subscribe("Core", snapshot => values.Add(snapshot.Value), false);
                using (ReddotManager.BeginBatch())
                {
                    first.SetValue(4);
                    second.SetValue(1);
                    third.SetValue(9);
                    var diagnostic = ReddotManager.CaptureDiagnostics();
                    Require(values.Count == 0 && diagnostic.BatchDepth == 1 && diagnostic.DirtyNodeCount > 0, "Capture must not flush an active Batch.");
                }
                ReddotManager.SetAggregation("Core", ReddotAggregation.Sum);
                Require(values.SequenceEqual(new long[] { 9, 10 }), "Batch notifications or final aggregation changed.");
                _report.aggregationAndBatch = true;
            }
            using (var state = new ReddotReadState("reddot-player-slot"))
            using (var unread = state.BindUnreadSource("Read/Mail", "mail", "mail", "v1"))
            {
                var values = new List<long>();
                using var subscription = ReddotManager.Subscribe("Read/Mail", snapshot => values.Add(snapshot.Value), false);
                state.MarkRead("mail", "v1");
                state.MarkRead("mail", "v1");
                unread.SetVersion("v2");
                state.MarkRead("mail", "v1");
                Require(ReddotManager.GetValue("Read/Mail") == 1 && !state.IsRead("mail", "v2"), "An old confirmation suppressed a newer unread version.");
                var exported = state.Export();
                using var reopened = new ReddotReadState("reddot-player-slot");
                reopened.Import(exported);
                exported.records[0].versions[0] = "mutated-copy";
                Require(reopened.IsRead("mail", "v1") && state.IsRead("mail", "v1"), "Export/Import leaked mutable record arrays.");
                using var rebound = reopened.BindUnreadSource("Read/Reloaded", "mail", "mail", "v1");
                Require(ReddotManager.GetValue("Read/Reloaded") == 0, "Explicit imported confirmation was not used.");
                rebound.SetVersion("v2");
                reopened.MarkRead("mail", "v2");
                Require(ReddotManager.GetValue("Read/Reloaded") == 0 && ReddotManager.GetValue("Read/Mail") == 1, "Instance read-state ownership was lost.");
                state.MarkRead("mail", "v2");
                Require(values.SequenceEqual(new long[] { 0, 1, 0 }), "Read notifications drifted or duplicate confirmation notified.");
                _report.readStateRoundTrip = true;
            }
            using (var old = ReddotManager.RegisterSource("Generation/Mail", "mail", 1))
            using (var oldBatch = ReddotManager.BeginBatch())
            {
                var before = ReddotManager.CaptureDiagnostics();
                ReddotManager.Reset();
                using var current = ReddotManager.RegisterSource("Generation/Mail", "mail", 7);
                old.Dispose();
                oldBatch.Dispose();
                var rejected = false;
                try
                {
                    old.SetValue(1);
                }
                catch (ObjectDisposedException)
                {
                    rejected = true;
                }
                var after = ReddotManager.CaptureDiagnostics();
                Require(rejected && ReddotManager.GetValue("Generation/Mail") == 7 && after.Generation > before.Generation && after.BatchDepth == 0,
                    "Old generation credentials affected a replacement source or Batch.");
                _report.oldGenerationRejected = true;
            }
            _report.measurements = new[] { Measure(false), Measure(true) };
            Require(_report.measurements[0].finalValue == _report.measurements[1].finalValue, "Batch and non-Batch performed different work.");
            var final = ReddotManager.CaptureDiagnostics();
            _report.generation = final.Generation;
            _report.captureSequence = final.CaptureSequence;
            _report.diagnosticNodes = final.Nodes.Count;
            _report.diagnosticSources = final.Nodes.Sum(node => node.Sources.Count);
            _report.diagnosticSubscribers = final.Nodes.Sum(node => node.SubscriberCount);
            _report.dirtyNodeCount = final.DirtyNodeCount;
            _report.batchDepth = final.BatchDepth;
            Require(final.Nodes.All(node => node.Value == 0) && _report.diagnosticSources == 0 && _report.diagnosticSubscribers == 0 &&
                final.BatchDepth == 0 && final.DirtyNodeCount == 0 && !final.IsFlushing, "Probe left sources, listeners or notifications behind.");
            var resource = ResourceSystem.Current;
            var resources = resource?.GetDiagnosticsSnapshot();
            _report.resourceWasNull = resource == null;
            _report.leaseCount = resources?.LeaseCount ?? 0;
            _report.recordCount = resources?.RecordCount ?? 0;
            Require(_report.resourceWasNull && _report.leaseCount == 0 && _report.recordCount == 0 && string.IsNullOrEmpty(_report.catalogDigest),
                "RedDots must remain genuinely resource-free, not report a fake successful catalog.");
        }

        // Same fixed work, warm-up and listener/reentry pattern as ReddotPerformanceTests, now inside the real Player.
        private static Measurement Measure(bool batch)
        {
            ReddotManager.Reset();
            var counterMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            Func<long> allocated = counterMethod == null ? () => -1 : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), counterMethod);
            var result = new Measurement { batch = batch };
            allocated();
            long calibrationBefore = allocated();
            var calibrationAllocation = new byte[65536];
            calibrationAllocation[0] = 1;
            result.allocationCalibrationBytes = counterMethod == null ? -1 : allocated() - calibrationBefore;
            GC.KeepAlive(calibrationAllocation);
            // Some Unity Mono implementations expose the method but return a constant zero.
            result.allocationCounterReliable = result.allocationCalibrationBytes >= 65536;
            var sources = new List<ReddotSource>();
            var subscriptions = new List<IDisposable>();
            using var echo = ReddotManager.RegisterSource("Perf/Echo/", "echo");
            var echoed = false;
            var depth = 0;
            using var root = ReddotManager.Subscribe("Perf/", snapshot =>
            {
                depth++;
                result.maximumCallbackDepth = Math.Max(result.maximumCallbackDepth, depth);
                result.rootNotifications++;
                Require(snapshot.Value == ReddotManager.GetValue("Perf/"), "The representative observer saw an uncommitted root value.");
                if (!echoed && snapshot.Value > 0)
                {
                    echoed = true;
                    echo.SetValue(1);
                }
                depth--;
            }, false);
            try
            {
                for (var index = 0; index < 64; index++)
                {
                    var path = "Perf/G" + index / 16 + "/B" + index / 4 % 4 + "/L" + index % 4 + "/";
                    for (var source = 0; source < 3; source++)
                    {
                        sources.Add(ReddotManager.RegisterSource(path, "s" + source));
                    }
                    subscriptions.Add(ReddotManager.Subscribe(path, snapshot =>
                    {
                        result.leafNotifications++;
                        Require(snapshot.Value > 0, "Warm-up leaf value was not positive.");
                    }, false));
                }
                void Update(int iterations)
                {
                    for (var iteration = 1; iteration <= iterations; iteration++)
                    {
                        using var scope = batch ? ReddotManager.BeginBatch() : null;
                        foreach (var source in sources)
                        {
                            source.SetValue(iteration);
                        }
                    }
                }
                Update(8);
                foreach (var subscription in subscriptions)
                {
                    subscription.Dispose();
                }
                subscriptions.Clear();
                using (ReddotManager.BeginBatch())
                {
                    foreach (var source in sources)
                    {
                        source.SetValue(0);
                    }
                    echo.SetValue(0);
                }
                for (var index = 0; index < 64; index++)
                {
                    subscriptions.Add(ReddotManager.Subscribe("Perf/G" + index / 16 + "/B" + index / 4 % 4 + "/L" + index % 4 + "/",
                        _ => result.leafNotifications++, false));
                }
                result.rootNotifications = result.leafNotifications = result.maximumCallbackDepth = 0;
                echoed = false;
                allocated();
                var watch = Stopwatch.StartNew();
                var before = allocated();
                Update(128);
                result.updateAllocatedBytes = result.allocationCounterReliable ? allocated() - before : -1;
                watch.Stop();
                result.updateMilliseconds = watch.Elapsed.TotalMilliseconds;
                result.finalValue = ReddotManager.GetValue("Perf/");
                const string ReadPath = "Perf/G0/B0/L0/";
                for (var index = 0; index < 1024; index++)
                {
                    ReddotManager.GetValue(ReadPath);
                    ReddotManager.GetSnapshot(ReadPath);
                }
                watch.Restart();
                before = allocated();
                for (var index = 0; index < 100000; index++)
                {
                    result.readChecksum += ReddotManager.GetValue(ReadPath) + ReddotManager.GetSnapshot(ReadPath).Value;
                }
                result.readAllocatedBytes = result.allocationCounterReliable ? allocated() - before : -1;
                watch.Stop();
                result.readMilliseconds = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                before = allocated();
                var diagnostic = ReddotManager.CaptureDiagnostics();
                result.diagnosticAllocatedBytes = result.allocationCounterReliable ? allocated() - before : -1;
                watch.Stop();
                result.diagnosticMilliseconds = watch.Elapsed.TotalMilliseconds;
                var nodes = diagnostic.Nodes.Where(node => node.Path.StartsWith("Perf/", StringComparison.Ordinal)).ToArray();
                result.diagnosticNodes = nodes.Length;
                result.diagnosticSources = nodes.Sum(node => node.Sources.Count);
                result.diagnosticSubscribers = nodes.Sum(node => node.SubscriberCount);
                Require(result.finalValue == 24577 && sources.All(source => source.Value == 128) && result.readChecksum == 76800000,
                    "Measured workload values or warmed read checksum changed.");
                Require(result.rootNotifications == (batch ? 129 : 24577) &&
                    result.leafNotifications == (batch ? 8192 : 24576) && result.maximumCallbackDepth == 1, "Measured notification/reentry counts changed.");
                Require(result.readAllocatedBytes == (result.allocationCounterReliable ? 0 : -1) && result.diagnosticNodes == 86 && result.diagnosticSources == 193 && result.diagnosticSubscribers == 65,
                    "Canonical reads allocated or diagnostic ownership counts changed.");
                return result;
            }
            finally
            {
                foreach (var subscription in subscriptions)
                {
                    subscription.Dispose();
                }
                root.Dispose();
                using var scope = ReddotManager.BeginBatch();
                foreach (var source in sources)
                {
                    source.Dispose();
                }
                ReddotManager.Reset();
            }
        }

        private void CaptureError(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _runtimeError ??= message + "\n" + trace;
            }
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition)
            {
                throw new InvalidOperationException(reason);
            }
        }

        private static void NoLinks(string path)
        {
            if (File.Exists(path))
            {
                Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Linked file is not owned Player output.");
            }
            for (var parent = new DirectoryInfo(File.Exists(path) ? Path.GetDirectoryName(path) : path); parent != null; parent = parent.Parent)
            {
                if (parent.Exists)
                {
                    Require((parent.Attributes & FileAttributes.ReparsePoint) == 0, "Linked directory is not owned Player output.");
                }
            }
        }
    }
}
