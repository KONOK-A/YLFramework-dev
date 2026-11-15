using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Pooling.Acceptance;
using YLFramework.Testing.EditorFixtures;

namespace YLFramework.Pooling.Tests
{
    public sealed class PoolingPlayerTests
    {
        [TestCase(false)]
        [TestCase(true)]
        [Category("YL.Special.PoolingPlayer")]
        public void ActualMonoPlayersSeparateReleaseTimingFromDevelopmentAllocationEvents(bool developmentBuild)
            => VerifyActualPlayers(ScriptingImplementation.Mono2x, developmentBuild);

        [TestCase(false)]
        [TestCase(true)]
        [Category("YL.Special.PoolingIL2CPP")]
        public void ActualIl2CppPlayersSeparateReleaseTimingFromDevelopmentAllocationEvents(bool developmentBuild)
            => VerifyActualPlayers(ScriptingImplementation.IL2CPP, developmentBuild);

        private static void VerifyActualPlayers(ScriptingImplementation backend, bool developmentBuild)
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode &&
                !EditorApplication.isCompiling && !EditorApplication.isUpdating, Is.True);
            string root = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            var selection = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetParent(root).FullName, "selection.json")));
            string fingerprint = (string)selection["inputIdentity"]["sha256"];
            string repository = (string)selection["inputIdentity"]["repository"];
            Assert.That(fingerprint, Does.Match("^[0-9a-f]{64}$"));
            string commit = GetCommit(repository);
            string output = Path.Combine(root, developmentBuild ? "Pooling-Development" : "Pooling-Release");
            Assert.That(Directory.Exists(output) || File.Exists(output), Is.False);
            Directory.CreateDirectory(output);
            string token = Guid.NewGuid().ToString("N");
            string assets = "Assets/__YLPooling_" + token;
            Assert.That(Directory.Exists(assets) || File.Exists(assets) || File.Exists(assets + ".meta"), Is.False);
            string guid = AssetDatabase.CreateFolder("Assets", assets.Substring("Assets/".Length));
            Assert.That(Guid.TryParseExact(guid, "N", out _), Is.True);
            CollectionAssert.AreEquivalent(new[] { "com.kano.ylframework", "com.kano.ylframework.gameplay" },
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(value => value.name == "com.kano.ylframework" || value.name.StartsWith("com.kano.ylframework.", StringComparison.Ordinal))
                    .Select(value => value.name));
            try
            {
                StandaloneFixturePlayer.BuildAndRun(output, assets, "YLFramework.Runtime", null, scene =>
                {
                    string player = File.ReadAllText(Path.Combine(output, "standalone-player-location.txt")).Trim();
                    var owner = new PoolingPlayerProbe.Owner
                    {
                        token = token,
                        player = player,
                        inputFingerprint = fingerprint,
                        sourceCommit = commit
                    };
                    using (var file = new FileStream(Path.Combine(player, ".pooling-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(file))
                    {
                        writer.Write(JsonUtility.ToJson(owner, true));
                    }
                    var probe = new GameObject("PoolingPlayerProbe").AddComponent<PoolingPlayerProbe>();
                    probe.runToken = token;
                    probe.inputFingerprint = fingerprint;
                    probe.sourceCommit = commit;
                }, new[] { "YL_POOLING_ACCEPTANCE" }, scriptingBackend: backend, developmentBuild: developmentBuild);
            }
            finally
            {
                string locator = Path.Combine(output, "standalone-player-location.txt");
                if (File.Exists(locator))
                {
                    string player = File.ReadAllText(locator).Trim();
                    for (int round = 1; round <= 2; round++)
                    {
                        string original = Path.Combine(player, "result-" + round + ".json");
                        if (!File.Exists(original))
                        {
                            continue;
                        }
                        string archived = Path.Combine(output, "StandalonePlayer-" + round + ".json");
                        Assert.That(File.Exists(archived), Is.True, "A failed/partial Player report must be retained in Results.");
                        CollectionAssert.AreEqual(File.ReadAllBytes(original), File.ReadAllBytes(archived));
                        string log = Path.Combine(player, "player-" + round + ".log");
                        if (File.Exists(log))
                        {
                            string archivedLog = Path.Combine(output, "StandalonePlayer-" + round + ".log");
                            Assert.That(File.Exists(archivedLog), Is.True, "The actual failed Player log must be retained in Results.");
                            CollectionAssert.AreEqual(File.ReadAllBytes(log), File.ReadAllBytes(archivedLog));
                        }
                        TestContext.WriteLine("Player artifact archive verified byte-for-byte: Development={0}, run {1}.", developmentBuild, round);
                    }
                }
            }
            for (int round = 1; round <= 2; round++)
            {
                string content = File.ReadAllText(Path.Combine(output, "StandalonePlayer-" + round + ".json"));
                var report = JsonUtility.FromJson<PoolingPlayerProbe.Report>(content);
                var json = JObject.Parse(content);
                Assert.That(report.backend, Is.EqualTo(backend == ScriptingImplementation.IL2CPP ? "IL2CPP" : "Mono"));
                Assert.That(report.developmentBuild, Is.EqualTo(developmentBuild));
                Assert.That(report.probeAssembly, Is.EqualTo("YLFramework.Pooling.Acceptance"));
                Assert.That(report.token, Is.EqualTo(token));
                Assert.That(report.inputFingerprint, Is.EqualTo(fingerprint));
                Assert.That(report.sourceCommit, Is.EqualTo(commit));
                Assert.That(report.pointerBits, Is.EqualTo(64));
                Assert.That((string)json["measurementKind"], Is.EqualTo(developmentBuild ? "DevelopmentAllocationEvents" : "ReleaseTiming"));
                Assert.That((string)json["allocationMetric"], Is.EqualTo(developmentBuild ? "GC.Alloc.Count.CurrentThread" : "NotMeasured"));
                if (developmentBuild)
                {
                    Assert.That((long)json["calibration"]["emptyEvents"], Is.Zero);
                    Assert.That((long)json["calibration"]["smallAllocationEvents"], Is.EqualTo(1));
                    Assert.That((long)json["calibration"]["largeAllocationEvents"], Is.EqualTo(1));
                }
                else
                {
                    Assert.That((bool)json["calibration"]["performed"], Is.False);
                }
                Assert.That(report.workloadVersion, Is.EqualTo(4));
                Assert.That(report.leaseCasesSupported, Is.True);
                Assert.That(report.measurements, Has.Length.EqualTo(76));
                Assert.That(report.measurements.Select(value => value.caseId).Distinct().Count(), Is.EqualTo(76));
                foreach (var measurement in report.measurements)
                {
                    bool cold = measurement.caseId.StartsWith("GameObject.prewarm.", StringComparison.Ordinal) ||
                        measurement.caseId.StartsWith("GameObject.instantiate.", StringComparison.Ordinal) ||
                        measurement.caseId.StartsWith("GameObject.destroyRequest.", StringComparison.Ordinal);
                    int samples = cold ? 5 : 31;
                    Assert.That(measurement.ticksBefore, Has.Length.EqualTo(samples), measurement.caseId);
                    Assert.That(measurement.ticksAfter, Has.Length.EqualTo(samples), measurement.caseId);
                    var recorded = json["measurements"].Single(value => (string)value["caseId"] == measurement.caseId);
                    Assert.That(recorded["allocatedBefore"], Is.Null, "Do not publish unsupported byte readings.");
                    Assert.That(recorded["allocationEvents"].Count(), Is.EqualTo(developmentBuild ? samples : 0), measurement.caseId);
                    Assert.That((string)recorded["allocationStatus"], Is.EqualTo(developmentBuild ? "MeasuredEvents" : "NotMeasured"));
                    Assert.That(recorded["allocationEvents"].Values<long>(), Is.All.GreaterThanOrEqualTo(0), measurement.caseId);
                    Assert.That(measurement.checksums, Is.All.EqualTo(measurement.checksumPerIteration * measurement.iterations), measurement.caseId);
                    Assert.That(measurement.medianNanosecondsPerPair > 0 && measurement.p95NanosecondsPerPair >= measurement.medianNanosecondsPerPair, Is.True, measurement.caseId);
                }
                foreach (string implementation in new[] { "Self.Fifo", "Self.Lifo", "Unity" })
                {
                    string capacity = implementation == "Unity" ? ".max20.32" : ".default20.32";
                    var overflow = json["measurements"].Single(value => (string)value["caseId"] == "Object." + implementation + capacity);
                    int cycles = 8 + 31 * 256;
                    Assert.That((int)overflow["cachedCount"], Is.EqualTo(20));
                    Assert.That((int)overflow["factoryCalls"], Is.EqualTo(20 + cycles * 12));
                    Assert.That((int)overflow["discardCalls"], Is.EqualTo(cycles * 12));
                }
                foreach (int count in new[] { 32, 128, 512 })
                {
                    var destroy = json["measurements"].Single(value => (string)value["caseId"] == "GameObject.destroyRequest." + count);
                    Assert.That(destroy["completionTicks"].Count(), Is.EqualTo(5));
                    Assert.That(destroy["completionTicks"].Values<long>(), Is.All.GreaterThan(0));
                }
                if (developmentBuild)
                {
                    var allocated = json["measurements"].Single(value => (string)value["caseId"] == "Object.New.32");
                    Assert.That(allocated["allocationEvents"].Values<long>(), Is.All.GreaterThan(0), "The real allocating workload must be observed.");
                    foreach (var domain in json["measurements"].Where(value => ((string)value["caseId"]).StartsWith("Domain.", StringComparison.Ordinal)))
                    {
                        Assert.That(domain["allocationEvents"].Values<long>(), Is.All.EqualTo(0), (string)domain["caseId"]);
                    }
                    foreach (var lease in json["measurements"].Where(value => ((string)value["caseId"]).StartsWith("Lease.", StringComparison.Ordinal) &&
                        ((string)value["caseId"]).EndsWith(".steady", StringComparison.Ordinal)))
                    {
                        Assert.That(lease["allocationEvents"].Values<long>(), Is.All.EqualTo(0), (string)lease["caseId"]);
                        Assert.That((int)lease["cachedCount"], Is.EqualTo(1));
                    }
                    var discarded = json["measurements"].Single(value => (string)value["caseId"] == "Lease.Object.discard");
                    Assert.That(discarded["allocationEvents"].Values<long>(), Is.All.GreaterThan(0));
                    Assert.That((int)discarded["factoryCalls"], Is.EqualTo(8 + 31 * 8192));
                    Assert.That((int)discarded["discardCalls"], Is.EqualTo(8 + 31 * 8192));
                    foreach (int count in new[] { 0, 1, 24, 128 })
                    {
                        var pending = json["measurements"].Single(value => (string)value["caseId"] == "Audio.Pending." + count);
                        Assert.That(pending["allocationEvents"].Values<long>(), Is.All.EqualTo(0), "Calibrated actual manager updates must not allocate their warmed snapshot.");
                    }
                }
                Assert.That(report.frameworkAssemblies.Any(value => value.Contains("Editor") || value.Contains("Tests")), Is.False,
                    "Editor/Test assemblies leaked into an ordinary Player.");
            }
            Assert.That(AssetDatabase.AssetPathToGUID(assets), Is.EqualTo(guid));
            Assert.That(AssetDatabase.DeleteAsset(assets), Is.True);
        }

        private static string GetCommit(string repository)
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (!process.WaitForExit(10000))
            {
                process.Kill();
                process.WaitForExit(10000);
                Assert.Fail("Read-only source identity lookup timed out.");
            }
            string commit = process.StandardOutput.ReadToEnd().Trim();
            Assert.That(process.ExitCode, Is.Zero, process.StandardError.ReadToEnd());
            Assert.That(commit, Does.Match("^[0-9a-f]{40}$"));
            return commit;
        }
    }
}
