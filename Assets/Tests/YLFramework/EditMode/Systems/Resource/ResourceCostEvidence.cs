using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    /// <summary>显式启动的自有验证回执；逐叶记录与suite终态分开，超时后只回收同一运行。</summary>
    [InitializeOnLoad]
    public static class ResourceCostEvidence
    {
        private const string StateKey = "YL.Resource.B02F03.Evidence";
        private const string EnvironmentKey = "YL.Resource.B02F03.LoopbackEnvironment";
        private static readonly Observer _callback = new();
        public static string DirectoryPath => SessionState.GetString(StateKey, "");
        private static string RepositoryPath => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/.."))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>仅配置本批自有非生产回环凭据；终态回调恢复原进程环境，原值从不进入证据。</summary>
        public static void UseLoopbackEnvironment(string ownedRoot, string scope, string purpose)
        {
            if (SessionState.GetString(EnvironmentKey, "").Length != 0)
            {
                throw new InvalidOperationException("Loopback environment is already owned.");
            }

            ownedRoot = ValidateOwner(ownedRoot, scope, purpose);
            var pfx = Path.Combine(ownedRoot, "loopback.pfx");
            ResourceLocalSnapshot.RejectLinks(pfx);
            if (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(pfx)))
            {
                throw new FileNotFoundException("Generate the owned loopback credential before measuring.");
            }

            var previous = new Dictionary<string, string>
            {
                ["YL_RESOURCE_TEST_PFX"] = Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PFX"),
                ["YL_RESOURCE_TEST_PASSWORD"] = Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD")
            };
            SessionState.SetString(EnvironmentKey, JsonConvert.SerializeObject(previous));
            Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PFX", pfx);
            Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD", "b02f03-loopback-only");
        }

        public static void RestoreLoopbackEnvironment()
        {
            var json = SessionState.GetString(EnvironmentKey, "");
            if (json.Length == 0)
            {
                return;
            }

            var previous = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            foreach (var item in previous)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }

            if (previous.Any(item => !string.Equals(Environment.GetEnvironmentVariable(item.Key), item.Value, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The original loopback environment was not restored exactly.");
            }

            SessionState.EraseString(EnvironmentKey);
        }

        public static bool OwnsLoopbackEnvironment => SessionState.GetString(EnvironmentKey, "").Length != 0;

        private static string ValidateOwner(string ownedRoot, string scope, string purpose)
        {
            if (string.IsNullOrWhiteSpace(ownedRoot) || !Path.IsPathFullyQualified(ownedRoot) ||
                string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(purpose))
            {
                throw new ArgumentException("An absolute owned root and explicit scope and purpose are required.");
            }

            ownedRoot = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(ownedRoot)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var repository = RepositoryPath;
            if (string.Equals(ownedRoot, repository, StringComparison.OrdinalIgnoreCase) ||
                ownedRoot.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Evidence and loopback credentials must stay outside the repository.");
            }

            var ownerPath = Path.Combine(ownedRoot, "owner.json");
            ResourceLocalSnapshot.RejectLinks(ownerPath);
            var owner = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath)));
            var declaredRoot = (string)owner["ownedRoot"];
            var declaredRepository = (string)owner["repository"];
            if ((string)owner["scope"] != scope || (string)owner["purpose"] != purpose ||
                string.IsNullOrWhiteSpace(declaredRoot) || !Path.IsPathFullyQualified(declaredRoot) ||
                string.IsNullOrWhiteSpace(declaredRepository) || !Path.IsPathFullyQualified(declaredRepository) ||
                !string.Equals(ownedRoot, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(declaredRoot)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(repository, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(declaredRepository)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Evidence repository, root, scope or purpose does not match the caller.");
            }

            return ownedRoot;
        }

        /// <summary>发布fixture仅使用已验证的调用方身份和本次run，清理时复核同一标记，不推断历史归属。</summary>
        public static string RequireRunOwner()
        {
            var path = DirectoryPath;
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) ||
                File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, "finished.json"))))
            {
                throw new InvalidOperationException("An active explicit evidence run is required.");
            }

            var requestPath = Path.Combine(path, "request.json");
            ResourceLocalSnapshot.RejectLinks(requestPath);
            var request = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(requestPath)));
            var root = ValidateOwner((string)request["OwnedRoot"], (string)request["Scope"], (string)request["Purpose"]);
            var runId = (string)request["RunId"];
            if (!string.Equals((string)request["Repository"], RepositoryPath, StringComparison.OrdinalIgnoreCase) ||
                runId != Path.GetFileName(path) ||
                !string.Equals(path, Path.Combine(root, runId), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The request does not own this repository and run directory.");
            }

            return new JObject
            {
                ["repository"] = RepositoryPath,
                ["ownedRoot"] = root,
                ["scope"] = request["Scope"],
                ["purpose"] = request["Purpose"],
                ["runId"] = runId,
                ["runDirectory"] = path
            }.ToString(Formatting.None);
        }

        static ResourceCostEvidence()
        {
            if (DirectoryPath.Length != 0 && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DirectoryPath, "finished.json"))))
            {
                Register();
            }

            AssemblyReloadEvents.beforeAssemblyReload += () => TestRunnerApi.UnregisterTestCallback(_callback);
        }

        /// <summary>不保存场景、不覆盖已有run，不并发测试；路径、scope、purpose及测试选择均由调用方明确绑定。</summary>
        public static string Run(string ownedRoot, string runId, string[] testNames, string scope, string purpose)
        {
            if (IsRunnerActive())
            {
                throw new InvalidOperationException("Never nest an evidence runner inside an active TestRunner.");
            }
            PrepareRun(ownedRoot, runId, testNames, scope, purpose);
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            try
            {
                return api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, testNames = testNames }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(api);
            }
        }

        /// <summary>为已由官方CLI启动的同一次运行准备证据与终态观察者，不嵌套启动TestRunner。</summary>
        public static void BeginRun(string ownedRoot, string runId, string[] testNames, string scope, string purpose)
        {
            if (!IsRunnerActive())
            {
                throw new InvalidOperationException("Only the active official runner may attach evidence without starting a new run.");
            }
            PrepareRun(ownedRoot, runId, testNames, scope, purpose);
        }

        private static bool IsRunnerActive()
        {
            var method = typeof(TestRunnerApi).GetMethod("IsRunActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException("The fixed Test Framework has no active-run query.");
            return (bool)method.Invoke(null, null);
        }

        private static void PrepareRun(string ownedRoot, string runId, string[] testNames, string scope, string purpose)
        {
            if (DirectoryPath.Length != 0 && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(DirectoryPath, "finished.json"))))
            {
                throw new InvalidOperationException("An owned run is still active; recover it instead of restarting.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorUtility.scriptCompilationFailed ||
                Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) ||
                !string.IsNullOrEmpty(SessionState.GetString("Unity_MCP_TestRunner_TestCallRequestID", "")) ||
                !string.IsNullOrEmpty(SessionState.GetString("MCP_PendingTestRun", "")))
            {
                throw new InvalidOperationException("A settled Editor with saved scenes and no active tests is required.");
            }

            if (string.IsNullOrEmpty(runId) || runId.Any(x => !(x >= 'a' && x <= 'z' || x >= '0' && x <= '9' || x == '-')) ||
                testNames == null || testNames.Length == 0 || testNames.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("An explicit run id and test selection are required.");
            }

            ownedRoot = ValidateOwner(ownedRoot, scope, purpose);
            if (OwnsLoopbackEnvironment &&
                (!string.Equals(Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PFX"), Path.Combine(ownedRoot, "loopback.pfx"), StringComparison.OrdinalIgnoreCase) ||
                    Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD") != "b02f03-loopback-only"))
            {
                throw new InvalidOperationException("The owned loopback environment must belong to this evidence root.");
            }

            var path = Path.Combine(ownedRoot, runId);
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
            {
                throw new IOException("Refusing an existing run directory.");
            }

            ResourceLocalSnapshot.RejectLinks(path);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            SessionState.SetString(StateKey, path);
            Write("request", new
            {
                StartedAtUtc = DateTime.UtcNow,
                Unity = Application.unityVersion,
                Repository = RepositoryPath,
                OwnedRoot = ownedRoot,
                Scope = scope,
                Purpose = purpose,
                RunId = runId,
                TestNames = testNames
            });
            using (var sha = SHA256.Create())
            {
                var files = new[] { "Packages/com.kano.ylframework/Runtime", "Packages/com.kano.ylframework/Editor", "Packages/com.kano.ylframework/Samples~", "Assets/Tests/YLFramework" }
                    .SelectMany(root => Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories))
                    .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".asmdef", StringComparison.Ordinal))
                    .Concat(new[] { "Packages/com.kano.ylframework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll", "ProjectSettings/ProjectVersion.txt", "Packages/manifest.json", "Packages/packages-lock.json" })
                    .OrderBy(file => file, StringComparer.Ordinal).Select(file =>
                    {
                        using var stream = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(file));
                        return new
                        {
                            Path = file.Replace('\\', '/'),
                            Length = stream.Length,
                            Sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant()
                        };
                    }).ToArray();
                var binaries = new[] { typeof(YLFramework.Resource.ResourceSystem).Assembly,
                    typeof(YLFramework.Editor.Resource.ResourceEditorOperations).Assembly, typeof(ResourceCostEvidence).Assembly,
                    Type.GetType("YLFramework.Samples.Resource.Editor.ResourceBuildRecipeExample, YLFramework.ResourceStartup.Sample.Editor", true).Assembly }.Select(assembly =>
                    {
                        using var stream = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(assembly.Location));
                        return new
                        {
                            Name = assembly.GetName().Name,
                            Length = stream.Length,
                            Sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant()
                        };
                    }).ToArray();
                Write("identity", new
                {
                    CapturedAtUtc = DateTime.UtcNow,
                    SourceFiles = files,
                    LoadedAssemblies = binaries,
                    SystemInfo.operatingSystem,
                    SystemInfo.processorType,
                    SystemInfo.processorCount,
                    SystemInfo.processorFrequency,
                    SystemInfo.systemMemorySize,
                    SystemInfo.graphicsDeviceName,
                    SystemInfo.graphicsMemorySize,
                    Backend = "Windows Editor / Mono",
                    Target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    AllocationCounter = "Unavailable unless a separately recorded capability probe succeeds",
                    CacheCondition = "Existing Editor/OS caches; no system cache, Library or user data cleared"
                });
            }
            Register();
        }

        public static void Write(string name, object value)
        {
            if (DirectoryPath.Length == 0 || name.Any(x => !(char.IsLetterOrDigit(x) || x == '-')))
            {
                throw new InvalidOperationException("No explicit evidence run, or invalid evidence name.");
            }

            var bytes = new System.Text.UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented) + "\n");
            var path = Path.Combine(DirectoryPath, name + ".json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
            {
                throw new IOException("Refusing to replace existing evidence.");
            }

            using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".pending"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".pending"), global::YLFramework.Testing.PackageTestPaths.Physical(path));
        }

        private static void Register()
        {
            TestRunnerApi.UnregisterTestCallback(_callback);
            TestRunnerApi.RegisterTestCallback(_callback);
        }

        private sealed class Observer : ICallbacks
        {
            public void RunStarted(ITestAdaptor test)
            {
            }
            public void TestStarted(ITestAdaptor test)
            {
                if (!test.IsSuite)
                {
                    Write("test-start-" + Guid.NewGuid().ToString("N"), new
                    {
                        ObservedAtUtc = DateTime.UtcNow,
                        Name = test.FullName
                    });
                }
            }
            public void TestFinished(ITestResultAdaptor result)
            {
                if (!result.Test.IsSuite)
                {
                    Write("test-finish-" + Guid.NewGuid().ToString("N"), new
                    {
                        ObservedAtUtc = DateTime.UtcNow,
                        Name = result.FullName,
                        Status = result.TestStatus.ToString(),
                        result.ResultState
                    });
                }
            }
            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    var stack = new Stack<ITestResultAdaptor>();
                    stack.Push(result);
                    var leaves = new List<object>();
                    var failures = new List<object>();
                    while (stack.Count != 0)
                    {
                        var item = stack.Pop();
                        var row = new
                        {
                            Name = item.FullName,
                            Status = item.TestStatus.ToString(),
                            item.ResultState,
                            item.Duration,
                            item.Message,
                            item.StackTrace,
                            item.Output
                        };
                        if (!item.Test.IsSuite)
                        {
                            leaves.Add(row);
                        }
                        else
                        {
                            if (item.TestStatus.ToString() == "Failed")
                            {
                                failures.Add(row);
                            }

                            foreach (var child in item.Children)
                            {
                                stack.Push(child);
                            }
                        }
                    }
                    Exception environmentFailure = null;
                    try
                    {
                        RestoreLoopbackEnvironment();
                    }
                    catch (Exception error)
                    {
                        environmentFailure = error;
                    }
                    Write("finished", new
                    {
                        FinishedAtUtc = DateTime.UtcNow,
                        PassedGate = result.TestStatus.ToString() == "Passed" && leaves.Count > 0 && failures.Count == 0 && environmentFailure == null,
                        result.ResultState,
                        Leaves = leaves,
                        SuiteFailures = failures,
                        EnvironmentRestored = environmentFailure == null,
                        EnvironmentFailure = environmentFailure == null ? null : new
                        {
                            Type = environmentFailure.GetType().FullName,
                            environmentFailure.HResult
                        }
                    });
                }
                finally
                {
                    TestRunnerApi.UnregisterTestCallback(this);
                }
            }
        }
    }
}
