using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace YLFramework.Tests.Resource
{
    /// <summary>显式武装的本轮测试回执；客户端超时不重启测试，也不把发现树计为已执行叶。</summary>
    [InitializeOnLoad]
    public static class ResourceE04ValidationResults
    {
        private const string Root = @"C:\Users\Kano\AppData\Local\Temp\kilo\e04-product-20261016-2103";
        private const string Owner = "ses_eb976ffe9ffePUz27Uq1etyOtE";
        private const string StateKey = "YL.E04.Validation.20261016.Run";
        private static readonly Observer _callback = new();

        [Serializable]
        private sealed class OwnerReceipt
        {
            public string ownerSessionId;
            public string ownedRoot;
        }
        [Serializable]
        private sealed class Run
        {
            public string Id;
            public string Fixture;
            public string Directory;
            public int ExpectedLeaves;
            public string ArmedAtUtc;
            public string StartedAtUtc;
            public string EvidenceFailure;
            public bool Completed;
        }
        [Serializable]
        private sealed class Leaf
        {
            public string Name;
            public string Status;
            public string ResultState;
            public double DurationSeconds;
            public string StartedAtUtc;
            public string FinishedAtUtc;
            public string Message;
            public string StackTrace;
            public string Output;
        }
        [Serializable]
        private sealed class Summary
        {
            public Run Run;
            public string FinishedAtUtc;
            public string RunnerResultState;
            public Leaf[] SuiteFailures;
            public int ExecutedLeaves;
            public int Passed;
            public int Failed;
            public int Other;
            public string[] UnexpectedLeaves;
            public bool CompleteEvidence;
            public bool PassedGate;
        }

        static ResourceE04ValidationResults()
        {
            var run = ReadRun();
            if (run != null && !run.Completed)
            {
                Register();
            }

            AssemblyReloadEvents.beforeAssemblyReload += () => TestRunnerApi.UnregisterTestCallback(_callback);
        }

        /// <summary>协调者在一次 tests-run 前调用；不会启动测试、清旧结果、保存场景或抢占其他运行。</summary>
        public static string Arm(string runId, string fixture, int expectedLeaves)
        {
            CheckOwner();
            if (string.IsNullOrEmpty(runId) || runId.Any(c => !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-'))
            {
                throw new ArgumentException("A lowercase filename-safe run id is required.", nameof(runId));
            }

            if (string.IsNullOrWhiteSpace(fixture) || expectedLeaves <= 0)
            {
                throw new ArgumentException("An exact fixture and positive expected leaf count are required.");
            }

            var previous = ReadRun();
            if (previous != null && !previous.Completed)
            {
                throw new InvalidOperationException("The previous armed run has not completed; do not overwrite or restart it.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorUtility.scriptCompilationFailed || EditorSceneManager.GetSceneManagerSetup().Any(x => string.IsNullOrEmpty(x.path)) ||
                Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                    .Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty))
            {
                throw new InvalidOperationException("Arm only in an idle, compiled Editor with saved scenes. This helper never saves user scenes.");
            }

            if (!string.IsNullOrEmpty(SessionState.GetString("Unity_MCP_TestRunner_TestCallRequestID", "")) ||
                !string.IsNullOrEmpty(SessionState.GetString("MCP_PendingTestRun", "")))
            {
                throw new InvalidOperationException("An MCP test request is still active or pending. Recover that run instead of starting another.");
            }

            var directory = Path.Combine(Root, "evidence", "test-results", runId);
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory)))
            {
                throw new IOException("Refusing to adopt an existing result directory.");
            }

            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
            var run = new Run
            {
                Id = runId,
                Fixture = fixture,
                Directory = directory,
                ExpectedLeaves = expectedLeaves,
                ArmedAtUtc = DateTime.UtcNow.ToString("O")
            };
            SaveRun(run);
            WriteNew(Path.Combine(directory, "armed.json"), run);
            Register();
            return directory;
        }

        public static string Status() => SessionState.GetString(StateKey, "");

        private static void Register()
        {
            TestRunnerApi.UnregisterTestCallback(_callback);
            TestRunnerApi.RegisterTestCallback(_callback);
        }

        private static Run ReadRun()
        {
            var json = SessionState.GetString(StateKey, "");
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json);
        }

        private static void SaveRun(Run run) => SessionState.SetString(StateKey, JsonUtility.ToJson(run));

        private static void CheckOwner()
        {
            var owner = JsonUtility.FromJson<OwnerReceipt>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Root, "owner.json"))));
            if (owner.ownerSessionId != Owner || !string.Equals(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(owner.ownedRoot)), Root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The validation root owner does not match this helper.");
            }
        }

        private static void WriteNew(string path, object value)
        {
            CheckOwner();
            var absolute = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            if (!absolute.StartsWith(Path.Combine(Root, "evidence", "test-results") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Result path escaped this run's owned evidence root.");
            }

            for (var cursor = Path.GetDirectoryName(absolute); cursor != null && cursor.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
                cursor = Path.GetDirectoryName(cursor))
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(cursor)) && (File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(cursor)) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Refusing linked result directories.");
                }
            }

            var bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(value, true) + "\n");
            using var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
        }

        private sealed class Observer : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) => Observe(run =>
            {
                if (!string.IsNullOrEmpty(run.StartedAtUtc))
                {
                    return;
                }

                run.StartedAtUtc = DateTime.UtcNow.ToString("O");
                SaveRun(run);
                WriteNew(Path.Combine(run.Directory, "started.json"), run);
            });

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.IsSuite)
                {
                    return;
                }

                Observe(run =>
                {
                    using var sha = SHA256.Create();
                    var name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(result.FullName))).Replace("-", "").ToLowerInvariant();
                    WriteNew(Path.Combine(run.Directory, "leaf-" + name + ".json"), new Leaf
                    {
                        Name = result.FullName,
                        Status = result.TestStatus.ToString(),
                        ResultState = result.ResultState,
                        DurationSeconds = result.Duration,
                        StartedAtUtc = result.StartTime.ToUniversalTime().ToString("O"),
                        FinishedAtUtc = result.EndTime.ToUniversalTime().ToString("O"),
                        Message = result.Message,
                        StackTrace = result.StackTrace,
                        Output = result.Output
                    });
                });
            }

            public void RunFinished(ITestResultAdaptor result) => Observe(run =>
            {
                var leaves = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(run.Directory), "leaf-*.json")
                    .Select(path => JsonUtility.FromJson<Leaf>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)))).ToArray();
                var unexpected = leaves.Where(x => !x.Name.StartsWith(run.Fixture + ".", StringComparison.Ordinal) &&
                    !x.Name.StartsWith(run.Fixture + "(", StringComparison.Ordinal)).Select(x => x.Name).ToArray();
                var complete = leaves.Length == run.ExpectedLeaves && unexpected.Length == 0 &&
                    !string.IsNullOrEmpty(run.StartedAtUtc) && string.IsNullOrEmpty(run.EvidenceFailure);
                // suite收尾错误不会反写已通过的leaf；保留真实cause，不能仅凭leaf全绿放行。
                var pending = new Stack<ITestResultAdaptor>();
                var failedSuites = new List<Leaf>();
                pending.Push(result);
                while (pending.Count != 0)
                {
                    var item = pending.Pop();
                    if (!item.Test.IsSuite)
                    {
                        continue;
                    }

                    foreach (var child in item.Children)
                    {
                        pending.Push(child);
                    }

                    if (item.TestStatus.ToString() == "Failed")
                    {
                        failedSuites.Add(new Leaf
                        {
                            Name = item.FullName,
                            Status = item.TestStatus.ToString(),
                            ResultState = item.ResultState,
                            Message = item.Message,
                            StackTrace = item.StackTrace,
                            Output = item.Output
                        });
                    }
                }
                var summary = new Summary
                {
                    Run = run,
                    FinishedAtUtc = DateTime.UtcNow.ToString("O"),
                    RunnerResultState = result.ResultState,
                    SuiteFailures = failedSuites.ToArray(),
                    ExecutedLeaves = leaves.Length,
                    Passed = leaves.Count(x => x.Status == "Passed"),
                    Failed = leaves.Count(x => x.Status == "Failed"),
                    Other = leaves.Count(x => x.Status != "Passed" && x.Status != "Failed"),
                    UnexpectedLeaves = unexpected,
                    CompleteEvidence = complete,
                    PassedGate = complete && leaves.All(x => x.Status == "Passed") && result.TestStatus.ToString() == "Passed"
                };
                run.Completed = true;
                WriteNew(Path.Combine(run.Directory, "finished.json"), summary);
                SaveRun(run);
                TestRunnerApi.UnregisterTestCallback(this);
            });

            private static void Observe(Action<Run> action)
            {
                var run = ReadRun();
                if (run == null || run.Completed)
                {
                    return;
                }

                try
                {
                    action(run);
                }
                catch (Exception error)
                {
                    run.EvidenceFailure = error.GetType().FullName;
                    SaveRun(run);
                    Debug.LogError("E04 validation evidence could not be saved; no gate may be claimed from this receipt.");
                }
            }
        }
    }
}
