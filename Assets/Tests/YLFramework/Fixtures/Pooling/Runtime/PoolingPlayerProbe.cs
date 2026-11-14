using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Pooling.Acceptance
{
    public sealed class PoolingPlayerProbe : MonoBehaviour
    {
        [Serializable]
        public sealed class Owner
        {
            public int schemaVersion = 1;
            public string token;
            public string player;
            public string inputFingerprint;
            public string sourceCommit;
        }

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 2;
            public int workloadVersion = 3;
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public bool developmentBuild;
            public string backend;
            public string runtimeAssembly;
            public string probeAssembly;
            public string catalogDigest = string.Empty;
            public int pid;
            public int leaseCount = -1;
            public int recordCount = -1;
            public string token;
            public string inputFingerprint;
            public string sourceCommit;
            public string machine;
            public string operatingSystem;
            public string processor;
            public int processorCount;
            public int pointerBits;
            public long stopwatchFrequency;
            public string measurementKind;
            public string allocationMetric;
            public PoolBenchmarkWorkload.Calibration calibration;
            public bool leaseCasesSupported;
            public string[] frameworkAssemblies;
            public PoolBenchmarkWorkload.Measurement[] measurements;
        }

        public string runToken;
        public string inputFingerprint;
        public string sourceCommit;
        private string _runtimeError;

        private IEnumerator Start()
        {
            var report = new Report();
            var measurements = new List<PoolBenchmarkWorkload.Measurement>();
            IEnumerator exercise = null;
            string result = null;
            float oldCapture = Time.captureDeltaTime;
            float oldFixed = Time.fixedDeltaTime;
            Application.logMessageReceived += CaptureError;
            try
            {
                try
                {
                    report.unityVersion = Application.unityVersion;
                    report.platform = Application.platform.ToString();
                    report.isEditor = Application.isEditor;
                    report.developmentBuild = UnityEngine.Debug.isDebugBuild;
#if ENABLE_IL2CPP
                    report.backend = "IL2CPP";
#elif ENABLE_MONO
                    report.backend = "Mono";
#else
                    report.backend = "Unsupported";
#endif
                    report.runtimeAssembly = typeof(ObjectPool<object>).Assembly.GetName().Name;
                    report.probeAssembly = typeof(PoolingPlayerProbe).Assembly.GetName().Name;
                    report.leaseCasesSupported = true;
                    report.pid = Process.GetCurrentProcess().Id;
                    report.token = runToken;
                    report.inputFingerprint = inputFingerprint;
                    report.sourceCommit = sourceCommit;
                    report.machine = Environment.MachineName;
                    report.operatingSystem = Environment.OSVersion.ToString();
                    report.processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                    report.processorCount = Environment.ProcessorCount;
                    report.pointerBits = IntPtr.Size * 8;
                    report.stopwatchFrequency = Stopwatch.Frequency;
                    var args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "-ylFeatureResult");
                    Require(index > 0 && index + 1 < args.Length, "Missing owned Player result argument.");
                    string player = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                    string candidate = Path.GetFullPath(args[index + 1]);
                    Require(string.Equals(Path.GetDirectoryName(candidate), player, StringComparison.OrdinalIgnoreCase) &&
                        (Path.GetFileName(candidate) == "result-1.json" || Path.GetFileName(candidate) == "result-2.json") &&
                        !File.Exists(candidate) && !Directory.Exists(candidate), "Result must be new and within this owned Player.");
                    NoLinks(player);
                    string ownerPath = Path.Combine(player, ".pooling-owner.json");
                    NoLinks(ownerPath);
                    var owner = JsonUtility.FromJson<Owner>(File.ReadAllText(ownerPath));
                    Require(owner != null && owner.schemaVersion == 1 && Guid.TryParseExact(runToken, "N", out _) &&
                        owner.token == runToken && owner.inputFingerprint == inputFingerprint && owner.sourceCommit == sourceCommit &&
                        string.Equals(Path.GetFullPath(owner.player), player, StringComparison.OrdinalIgnoreCase), "Player owner/serialized input mismatch.");
                    result = candidate;
                    Require(!report.isEditor && report.platform == "WindowsPlayer" && report.pointerBits == 64 &&
                        (report.backend == "Mono" || report.backend == "IL2CPP"), "Only actual x64 Mono/IL2CPP Players qualify.");
                    Require(ResourceSystem.Current == null, "A pool benchmark cannot initialize or adopt Resource.");
                    report.measurementKind = report.developmentBuild ? "DevelopmentAllocationEvents" : "ReleaseTiming";
                    report.allocationMetric = report.developmentBuild ? "GC.Alloc.Count.CurrentThread" : "NotMeasured";
                    report.calibration = PoolBenchmarkWorkload.Calibrate(report.developmentBuild);
                    Time.captureDeltaTime = 1f / 60;
                    Time.fixedDeltaTime = 1f / 60;
                }
                catch (Exception error) { report.error = error.ToString(); }
                if (report.error == null)
                {
                    yield return null;
                    yield return null;
                    exercise = PoolBenchmarkWorkload.Run(measurements);
                    while (report.error == null)
                    {
                        bool more;
                        try
                        {
                            Require(_runtimeError == null, _runtimeError);
                            Require(Math.Abs(Time.deltaTime - Time.fixedDeltaTime) < 0.0000001f, "Domain timing inputs are not fixed.");
                            more = exercise.MoveNext();
                            Require(_runtimeError == null, _runtimeError);
                        }
                        catch (Exception error) { report.error = error.ToString(); break; }
                        if (!more)
                        {
                            Require(ResourceSystem.Current == null, "Pool work created a Resource authority.");
                            report.leaseCount = report.recordCount = 0;
                            report.status = "Passed";
                            break;
                        }
                        yield return exercise.Current;
                    }
                }
            }
            finally
            {
                try
                {
                    (exercise as IDisposable)?.Dispose();
                    if (_runtimeError != null)
                    {
                        report.error ??= _runtimeError;
                    }
                }
                catch (Exception error) { report.error = report.error == null ? error.ToString() : report.error + "\nCleanup: " + error; }
                Application.logMessageReceived -= CaptureError;
                Time.captureDeltaTime = oldCapture;
                Time.fixedDeltaTime = oldFixed;
                report.frameworkAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(value => value.GetName().Name)
                    .Where(value => value.StartsWith("YLFramework", StringComparison.Ordinal)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                report.measurements = measurements.ToArray();
                report.status = report.status == "Passed" && report.error == null ? "Passed" : "Failed";
                bool persisted = false;
                try
                {
                    if (result != null)
                    {
                        using var stream = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                        writer.Write(JsonUtility.ToJson(report, true));
                        writer.Flush();
                        persisted = true;
                    }
                }
                finally { Application.Quit(persisted && report.status == "Passed" ? 0 : 1); }
            }
        }

        private void CaptureError(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _runtimeError ??= message + "\n" + trace;
            }
        }

        private static void NoLinks(string path)
        {
            for (string cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "Linked Player paths are refused.");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
