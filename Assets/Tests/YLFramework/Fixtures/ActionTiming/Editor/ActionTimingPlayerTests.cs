using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.ActionTiming.Acceptance;
using YLFramework.Testing.EditorFixtures;

namespace YLFramework.ActionTiming.Tests
{
    [Category("YL.Special.ActionTimingIL2CPP")]
    public sealed class ActionTimingDiagnosticsTests
    {
        [Test]
        public void FailedSenderCannotBeMistakenForSuccessfulCompletion()
        {
            var observe = typeof(ActionTimingPlayerProbe).GetMethod("SenderCompleted", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(observe, Is.Not.Null);
            var pending = new TaskCompletionSource<int>();
            Assert.That(observe.Invoke(null, new object[] { pending.Task }), Is.False);
            Assert.That(observe.Invoke(null, new object[] { Task.CompletedTask }), Is.True);
            var fault = Task.FromException(new InvalidOperationException("Expected sender failure"));
            var error = Assert.Throws<TargetInvocationException>(() => observe.Invoke(null, new object[] { fault }));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Is.EqualTo("Expected sender failure"));
        }

        [Test]
        public void UnexpectedWorkerErrorIsCollectedBeforeThePlayerCanReportPassed()
        {
            global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            Assert.That(scene.path, Is.EqualTo("Assets/YLTestingProfile/Empty.unity").Or.Empty);
            Assert.That(scene.isDirty || scene.rootCount != 0, Is.False);
            var host = new GameObject("ActionTiming.WorkerDiagnostic");
            host.SetActive(false);
            var probe = host.AddComponent<ActionTimingPlayerProbe>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var begin = typeof(ActionTimingPlayerProbe).GetMethod("BeginDiagnostics", flags);
            var end = typeof(ActionTimingPlayerProbe).GetMethod("EndDiagnostics", flags);
            try
            {
                begin.Invoke(probe, null);
                const string marker = "Unexpected_ActionTiming_Worker_Error";
                LogAssert.Expect(LogType.Error, marker);
                var sender = Task.Run(() => UnityEngine.Debug.LogError(marker));
                Assert.That(sender.Wait(3000), Is.True);
                sender.GetAwaiter().GetResult();
                var recorded = typeof(ActionTimingPlayerProbe).GetField("_runtimeError", flags).GetValue(probe) as string;
                Assert.That(recorded, Is.Not.Null, "Background diagnostics were never collected.");
                Assert.That(recorded, Does.Contain(marker), "A background-only error must prevent a Passed report.");
            }
            finally
            {
                end.Invoke(probe, null);
                UnityEngine.Object.DestroyImmediate(host);
                EditorSceneManager.OpenScene("Assets/YLTestingProfile/Empty.unity", OpenSceneMode.Single);
            }
        }
    }

    public sealed class ActionTimingPlayerTests
    {
        private static readonly string[] ExpectedCases =
        {
            "Action.Arguments", "Action.Composition", "Action.GenericLeaseReuse", "Action.NamedReplace", "Action.Clocks",
            "Action.OwnerLifetime", "Action.SceneGlobalLifetime", "Action.Task", "Action.TaskCancelFault", "Action.Coroutine",
            "Action.Observable", "Diagnostics.ThreadedLog", "Diagnostics.SenderFault", "Action.FadeNumeric", "Timing.Frames", "Timing.ClocksPause", "Timing.FixedDelay",
            "Timing.ScopeReplaceGroupDispose", "Timing.ReusableWaitDispose", "Timing.FaultReentrancy", "Timing.ComponentLifetime",
            "Modules.ShutdownIsolation", "Cleanup.Ownership"
        };

        [Test]
        [Category("YL.Special.ActionTimingIL2CPP")]
        public void ActualReleaseIl2CppPlayersExerciseActionAndTimingWithMediumStripping()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode &&
                !EditorApplication.isCompiling && !EditorApplication.isUpdating, Is.True);
            var root = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            var selection = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetParent(root).FullName, "selection.json")));
            var fingerprint = (string)selection["inputIdentity"]["sha256"];
            var repository = (string)selection["inputIdentity"]["repository"];
            Assert.That(fingerprint, Does.Match("^[0-9a-f]{64}$"));
            var commit = GetCommit(repository);
            var output = Path.Combine(root, "ActionTiming-IL2CPP-Release");
            Assert.That(Directory.Exists(output) || File.Exists(output), Is.False);
            Directory.CreateDirectory(output);
            var token = Guid.NewGuid().ToString("N");
            var assets = "Assets/__YLActionTiming_" + token;
            Assert.That(Directory.Exists(assets) || File.Exists(assets) || File.Exists(assets + ".meta"), Is.False);
            var guid = AssetDatabase.CreateFolder("Assets", assets.Substring("Assets/".Length));
            Assert.That(Guid.TryParseExact(guid, "N", out _), Is.True);
            CollectionAssert.AreEquivalent(new[] { "com.kano.ylframework" }, UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(package => package.name == "com.kano.ylframework" || package.name.StartsWith("com.kano.ylframework.", StringComparison.Ordinal))
                .Select(package => package.name));
            var target = NamedBuildTarget.Standalone;
            var stripping = PlayerSettings.GetManagedStrippingLevel(target);
            var configuration = PlayerSettings.GetIl2CppCompilerConfiguration(target);
            try
            {
                PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Medium);
                PlayerSettings.SetIl2CppCompilerConfiguration(target, Il2CppCompilerConfiguration.Release);
                Assert.That(PlayerSettings.GetManagedStrippingLevel(target), Is.EqualTo(ManagedStrippingLevel.Medium));
                Assert.That(PlayerSettings.GetIl2CppCompilerConfiguration(target), Is.EqualTo(Il2CppCompilerConfiguration.Release));
                StandaloneFixturePlayer.BuildAndRun(output, assets, "YLFramework.Runtime", null, scene =>
                {
                    var player = File.ReadAllText(Path.Combine(output, "standalone-player-location.txt")).Trim();
                    var owner = new ActionTimingPlayerProbe.Owner
                    {
                        token = token,
                        player = player,
                        inputFingerprint = fingerprint,
                        sourceCommit = commit
                    };
                    using (var file = new FileStream(Path.Combine(player, ".action-timing-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(file))
                        writer.Write(JsonUtility.ToJson(owner, true));
                    var probe = new GameObject("ActionTimingPlayerProbe").AddComponent<ActionTimingPlayerProbe>();
                    probe.runToken = token;
                    probe.inputFingerprint = fingerprint;
                    probe.sourceCommit = commit;
                }, new[] { "YL_ACTIONTIMING_ACCEPTANCE" }, scriptingBackend: ScriptingImplementation.IL2CPP, developmentBuild: false);
            }
            finally
            {
                PlayerSettings.SetManagedStrippingLevel(target, stripping);
                PlayerSettings.SetIl2CppCompilerConfiguration(target, configuration);
                var locator = Path.Combine(output, "standalone-player-location.txt");
                if (File.Exists(locator))
                {
                    var player = File.ReadAllText(locator).Trim();
                    for (var run = 1; run <= 2; run++)
                    {
                        foreach (var extension in new[] { ".json", ".log" })
                        {
                            var original = Path.Combine(player, extension == ".json" ? "result-" + run + extension : "player-" + run + extension);
                            if (!File.Exists(original))
                                continue;
                            var archived = Path.Combine(output, "StandalonePlayer-" + run + extension);
                            Assert.That(File.Exists(archived), Is.True, "Failed or partial evidence must not be lost.");
                            CollectionAssert.AreEqual(File.ReadAllBytes(original), File.ReadAllBytes(archived));
                        }
                    }
                }
            }
            var directory = File.ReadAllText(Path.Combine(output, "standalone-player-location.txt")).Trim();
            Assert.That(File.Exists(Path.Combine(directory, "GameAssembly.dll")), Is.True);
            Assert.That(File.Exists(Path.Combine(directory, "P_Data/il2cpp_data/Metadata/global-metadata.dat")), Is.True);
            for (var run = 1; run <= 2; run++)
            {
                var report = JsonUtility.FromJson<ActionTimingPlayerProbe.Report>(File.ReadAllText(Path.Combine(output, "StandalonePlayer-" + run + ".json")));
                Assert.That(report.schemaVersion, Is.EqualTo(1));
                Assert.That(report.purpose, Is.EqualTo("YLTesting.ActionTimingPlayer"));
                Assert.That(report.backend, Is.EqualTo("IL2CPP"));
                Assert.That(report.developmentBuild, Is.False);
                Assert.That(report.pointerBits, Is.EqualTo(64));
                Assert.That(report.probeAssembly, Is.EqualTo("YLFramework.ActionTiming.Acceptance"));
                Assert.That(report.token, Is.EqualTo(token));
                Assert.That(report.inputFingerprint, Is.EqualTo(fingerprint));
                Assert.That(report.sourceCommit, Is.EqualTo(commit));
                CollectionAssert.AreEquivalent(ExpectedCases, report.cases);
                Assert.That(report.cases.Distinct().Count(), Is.EqualTo(ExpectedCases.Length));
                Assert.That(report.activeActions, Is.Zero);
                Assert.That(report.activeTimers, Is.Zero);
                Assert.That(report.frameworkAssemblies.Any(name => name.Contains("Editor") || name.Contains("Tests")), Is.False);
            }
            TestContext.WriteLine("IL2CPP Release, Medium stripping; two actual x64 Players, {0} completed cases each.", ExpectedCases.Length);
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
            var commit = process.StandardOutput.ReadToEnd().Trim();
            Assert.That(process.ExitCode, Is.Zero, process.StandardError.ReadToEnd());
            Assert.That(commit, Does.Match("^[0-9a-f]{40}$"));
            return commit;
        }
    }
}
