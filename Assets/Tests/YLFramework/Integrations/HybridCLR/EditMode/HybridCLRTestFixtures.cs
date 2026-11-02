using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Integrations.HybridCLR;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public static class HybridCLRTestFixtures
    {
        public const string Root = "Assets/Tests/YLFramework/Integrations/HybridCLR/PlayMode/Resources/YLHybridCLRTests";
        private static FunctionalCompletion _functionalCompletion;

        [InitializeOnLoadMethod]
        private static void RegisterRunnerSceneSetup()
        {
            var arguments = Environment.GetCommandLineArgs();
            // 普通宿主及 PlayMode runner 完全不接管；只在显式独立 EditMode 进程建立前置。
            if (!ShouldRegisterRunnerSceneSetup(Application.isBatchMode, arguments))
            {
                return;
            }
            var setup = new RunnerSceneSetup(Argument(arguments, "-hclr-root"), Argument(arguments, "-hclr-run"), Argument(arguments, "-hclr-owner-sha256"));
            if (setup.BeginStartupAdmission())
            {
                TestRunnerApi.RegisterTestCallback(setup);
            }
        }

        internal static bool ShouldRegisterRunnerSceneSetup(bool batch, string[] arguments)
        {
            return batch && arguments.Contains("-runTests") && Argument(arguments, "-testPlatform") == "EditMode" &&
                Argument(arguments, "-hclr-root") != null && Argument(arguments, "-hclr-run") != null && Argument(arguments, "-hclr-owner-sha256") != null;
        }

        private static string Argument(string[] arguments, string name)
        {
            var index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterFunctionalCompletion()
        {
            var arguments = Environment.GetCommandLineArgs();
            if (!Application.isBatchMode || !arguments.Contains("-runTests") || Argument(arguments, "-hclr-acceptance-policy") != "Known2022EditorExit")
            {
                return;
            }
            var evidence = Argument(arguments, "-hclr-completion-root");
            var run = Argument(arguments, "-hclr-completion-run");
            var ownerSha = Argument(arguments, "-hclr-completion-owner-sha256");
            if (_functionalCompletion != null)
            {
                return;
            }
            var output = Path.Combine(evidence ?? "", run + ".completion.json");
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)))
            {
                RequireFunctionalContext(evidence, run, ownerSha, true);
                var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(output));
                var completed = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                var key = "YL.HybridCLR.Completed." + ownerSha + "." + run;
                if (SessionState.GetString(key, "") != HybridCLRManifest.Hash(bytes) ||
                    (string)completed["OwnerSha256"] != ownerSha || (string)completed["Run"] != run ||
                    (string)completed["Project"] != Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/..")) ||
                    (string)completed["Phase"] != "Tests" || completed.Value<bool>("Completed") != true)
                {
                    throw new InvalidOperationException("A domain reload cannot adopt another run's completion.");
                }
                // SessionState 只跨本进程的 Domain Reload 保留，不把已封闭运行再注册成新测试。
                _functionalCompletion = new FunctionalCompletion(evidence, run, ownerSha) { _completed = true };
                return;
            }
            RequireFunctionalContext(evidence, run, ownerSha);
            _functionalCompletion = new FunctionalCompletion(evidence, run, ownerSha);
            TestRunnerApi.RegisterTestCallback(_functionalCompletion, 1000);
        }

        private static JObject RequireFunctionalContext(string evidence, string run, string ownerSha, bool resume = false)
        {
            var project = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/.."));
            var root = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(evidence ?? throw new InvalidOperationException("An explicit completion root is required.")));
            var ownerPath = Path.Combine(root, "owner.json");
            var output = Path.Combine(root, run + ".completion.json");
            foreach (var input in new[] { project, root, ownerPath, output })
            {
                for (var current = input; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                {
                    if ((File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(current)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(current))) && (File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(current)) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Linked completion context refused.");
                    }
                }
            }
            var ownerBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath));
            var owner = JObject.Parse(System.Text.Encoding.UTF8.GetString(ownerBytes));
            if (!Application.isBatchMode || Application.unityVersion != "2022.3.62f3c1" ||
                System.Diagnostics.FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion != "2022.3.62f3c1_1623fc0bbb97" ||
                owner.Value<int>("schemaVersion") != 2 || owner.Value<bool>("simulation") ||
                (string)owner["purpose"] != "YLFramework-HybridCLR-9.0.3-independent-reception" ||
                (string)owner["deliveryRoot"] != @"G:\Unity\U63-20261027-c63a1" ||
                (string)owner["project"] != project || (string)owner["evidenceRoot"] != root ||
                Path.GetDirectoryName(project) != (string)owner["deliveryRoot"] || Path.GetDirectoryName(root) != (string)owner["deliveryRoot"] ||
                (string)owner["expectedUnityVersion"] != Application.unityVersion || (string)owner["expectedUnityRevision"] != "1623fc0bbb97" ||
                HybridCLRManifest.Hash(ownerBytes) != ownerSha || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(project + "/.git")) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(project + "/.git")) ||
                !System.Text.RegularExpressions.Regex.IsMatch(run ?? "", "^[A-Za-z0-9-]+$") || (!resume && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output))) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)))
            {
                throw new InvalidOperationException("The completion exception needs the exact new owned 2022 receiver.");
            }
            return owner;
        }

        private static void WriteFunctionalCompletion(string evidence, string run, string ownerSha, JObject report)
        {
            var owner = RequireFunctionalContext(evidence, run, ownerSha);
            report["SchemaVersion"] = 1;
            report["Purpose"] = "YLFramework-HybridCLR-functional-completion-v1";
            report["Policy"] = "Known2022EditorExit";
            report["KnownIssueId"] = "U63-KI-2022-EDITOR-EXIT-001";
            report["Project"] = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/.."));
            report["UnityVersion"] = Application.unityVersion;
            report["UnityRevision"] = (string)owner["expectedUnityRevision"];
            report["Receiver"] = (string)owner["receiver"];
            report["OwnerSha256"] = ownerSha;
            report["Run"] = run;
            report["Completed"] = true;
            report["LifecycleChecksCompleted"] = true;
            var path = Path.Combine(evidence, run + ".completion.json");
            var pending = path + ".pending";
            using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(pending), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(report.ToString(Formatting.Indented));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(pending), global::YLFramework.Testing.PackageTestPaths.Physical(path));
            SessionState.SetString("YL.HybridCLR.Completed." + ownerSha + "." + run, HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path))));
            Debug.Log("HYBRIDCLR_FUNCTIONAL_COMPLETED " + run);
        }

        private sealed class FunctionalCompletion : ICallbacks
        {
            private readonly string _evidence;
            private readonly string _run;
            private readonly string _ownerSha;
            internal bool _completed;

            public FunctionalCompletion(string evidence, string run, string ownerSha)
            {
                _evidence = evidence;
                _run = run;
                _ownerSha = ownerSha;
                EditorApplication.quitting += MarkExitRequested;
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                IEnumerable<JObject> Leaves(ITestResultAdaptor node)
                {
                    if (!node.HasChildren && !node.Test.IsSuite)
                    {
                        yield return new JObject { ["Name"] = node.Test.FullName, ["Result"] = node.ResultState };
                    }
                    foreach (var child in node.Children)
                    {
                        foreach (var leaf in Leaves(child))
                        {
                            yield return leaf;
                        }
                    }
                }
                try
                {
                    var leaves = Leaves(result).ToArray();
                    WriteFunctionalCompletion(_evidence, _run, _ownerSha, new JObject
                    {
                        ["Phase"] = "Tests",
                        ["CleanupProof"] = "NUnitRunFinishedIncludesFixtureTearDown",
                        ["SelectionSha256"] = HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_evidence, _run + ".selection.json")))),
                        ["Passed"] = result.ResultState == "Passed" && result.FailCount == 0 && result.SkipCount == 0 && result.InconclusiveCount == 0 && leaves.Length != 0,
                        ["Total"] = leaves.Length,
                        ["PassedCount"] = result.PassCount,
                        ["FailedCount"] = result.FailCount,
                        ["SkippedCount"] = result.SkipCount,
                        ["InconclusiveCount"] = result.InconclusiveCount,
                        ["Leaves"] = new JArray(leaves)
                    });
                    _completed = true;
                }
                finally
                {
                    TestRunnerApi.UnregisterTestCallback(this);
                }
            }

            private void MarkExitRequested()
            {
                EditorApplication.quitting -= MarkExitRequested;
                if (_completed)
                {
                    Debug.Log("HYBRIDCLR_EDITOR_EXIT_REQUESTED " + _run);
                }
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
            }
            public void TestStarted(ITestAdaptor test)
            {
            }
            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }

        private sealed class RunnerSceneSetup : ICallbacks
        {
            private readonly string _evidence;
            private readonly string _run;
            private readonly string _ownerSha256;

            public RunnerSceneSetup(string evidence, string run, string ownerSha256)
            {
                _evidence = evidence;
                _run = run;
                _ownerSha256 = ownerSha256;
            }

            public bool BeginStartupAdmission()
            {
                try
                {
                    RequireContext();
                    // UTF 将执行推迟到下一次 Editor update，准入必须早于其 SaveModified/CreateBootstrap。
                    ValidateRunnerScenes(null);
                    var probe = Argument(Environment.GetCommandLineArgs(), "-hclr-scene-admission-probe");
                    if (probe != null)
                    {
                        PrepareAdmissionProbe(probe);
                        ValidateRunnerScenes(null);
                    }
                    EditorSceneManager.sceneSaving += BeforeSceneSaving;
                    EditorSceneManager.sceneClosing += BeforeSceneClosing;
                    EditorSceneManager.sceneOpened += BeforeSceneOpened;
                    return true;
                }
                catch (Exception exception)
                {
                    Abort(exception);
                    return false;
                }
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                StopStartupAdmission();
                try
                {
                    var owner = RequireContext();
                    var project = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/.."));
                    var scenePath = "Assets/HybridCLRTestsBootstrap-" + _run + ".unity";
                    var sceneFile = Path.Combine(project, scenePath);
                    var receiptPath = Path.Combine(_evidence, _run + ".scene.json");
                    ValidateRunnerScenes(null);
                    // 官方 RunStarted 在 bootstrap 创建之后、fixture SetUp 之前；只保存新自有空场景。
                    var saved = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    if (!EditorSceneManager.SaveScene(saved, scenePath) || saved.isDirty || saved.rootCount != 0)
                    {
                        throw new InvalidOperationException("The owned saved empty test host was not established.");
                    }
                    var receipt = new
                    {
                        Purpose = "YLFramework-HybridCLR-owned-test-scene-v1",
                        Project = project,
                        UnityVersion = Application.unityVersion,
                        UnityRevision = (string)owner["expectedUnityRevision"],
                        Receiver = (string)owner["receiver"],
                        OwnerSha256 = _ownerSha256,
                        Run = _run,
                        Scene = scenePath,
                        SceneSha256 = HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sceneFile))),
                        SceneMetaSha256 = HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sceneFile + ".meta"))),
                        SceneGuid = AssetDatabase.AssetPathToGUID(scenePath)
                    };
                    using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(receiptPath), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(receipt, Formatting.Indented));
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    Debug.Log("HYBRIDCLR_OWNED_TEST_SCENE " + scenePath);
                }
                catch (Exception exception)
                {
                    Abort(exception);
                }
            }

            private JObject RequireContext()
            {
                var project = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/.."));
                var evidence = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(_evidence));
                var delivery = Path.GetDirectoryName(project);
                var ownerPath = Path.Combine(evidence, "owner.json");
                var parentPath = Path.Combine(delivery, "owner.json");
                var installationPath = Path.Combine(evidence, "tests-installation.json");
                var sceneFile = Path.Combine(project, "Assets/HybridCLRTestsBootstrap-" + _run + ".unity");
                var probeScene = Path.Combine(project, "Assets/HybridCLRAdmissionProbe-" + _run + ".unity");
                var receiptPath = Path.Combine(evidence, _run + ".scene.json");
                var probePath = Path.Combine(evidence, _run + ".admission.json");
                foreach (var input in new[] { project, ownerPath, parentPath, installationPath, sceneFile, sceneFile + ".meta", receiptPath, probePath, probeScene, probeScene + ".meta" })
                {
                    for (var current = input; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                    {
                        if ((File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(current)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(current))) && (File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(current)) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new IOException("Linked test scene context refused.");
                        }
                    }
                }
                var ownerBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath));
                var owner = JObject.Parse(System.Text.Encoding.UTF8.GetString(ownerBytes));
                var parent = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(parentPath)));
                var installation = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(installationPath)));
                var receiver = (string)owner["receiver"];
                var revision = Application.unityVersion == "2022.3.62f3c1" ? "1623fc0bbb97" :
                    Application.unityVersion == "6000.3.27f1" ? "672d562a42a7" : null;
                if (revision == null || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorUtility.scriptCompilationFailed ||
                    owner.Value<int>("schemaVersion") != 2 || owner.Value<bool>("simulation") ||
                    (string)owner["purpose"] != "YLFramework-HybridCLR-9.0.3-independent-reception" ||
                    (string)parent["purpose"] != "YLFramework-Stage9-independent-delivery" ||
                    (string)parent["runId"] != "U63-20261027-c63a1" || (string)owner["runId"] != (string)parent["runId"] ||
                    (string)parent["plan"] != ".kilo/plans/1793047106303-unity-63-compatibility.md" ||
                    (string)parent["repository"] != (string)owner["repository"] ||
                    !string.Equals((string)parent["root"], delivery, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((string)owner["deliveryRoot"], delivery, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((string)owner["project"], project, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.Combine(delivery, receiver), project, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((string)owner["evidenceRoot"], evidence, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetDirectoryName(evidence), delivery, StringComparison.OrdinalIgnoreCase) ||
                    (string)owner["expectedUnityVersion"] != Application.unityVersion || (string)owner["expectedUnityRevision"] != revision ||
                    System.Diagnostics.FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion != Application.unityVersion + "_" + revision ||
                    HybridCLRManifest.Hash(ownerBytes) != _ownerSha256 ||
                    (string)installation["Purpose"] != "YLFramework-HybridCLR-receiver-tests-v1" ||
                    (string)installation["SceneProfile"] != "Owned" || (string)installation["OwnerSha256"] != _ownerSha256 ||
                    (string)installation["Project"] != project || installation.Value<int>("OwnedSceneCases") != 8 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(_run ?? "", "^[A-Za-z0-9-]+$") ||
                    Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(project + "/.git")) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(project + "/.git")) ||
                    File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(sceneFile)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(sceneFile + ".meta")) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(receiptPath)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(probePath)))
                {
                    throw new InvalidOperationException("An exact new owned receiver scene and current engine binding are required.");
                }
                return owner;
            }

            private void PrepareAdmissionProbe(string probe)
            {
                if (probe is not "dirty" and not "unknown-saved")
                {
                    throw new InvalidOperationException("Unknown startup admission probe.");
                }
                var path = "Assets/HybridCLRAdmissionProbe-" + _run + ".unity";
                var proofPath = Path.Combine(_evidence, _run + ".admission.json");
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(proofPath)))
                {
                    throw new InvalidOperationException("Never overwrite admission probe inputs.");
                }
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (probe == "dirty")
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                }
                else if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new InvalidOperationException("The owned unknown-scene probe was not saved.");
                }
                var proof = new
                {
                    Purpose = "YLFramework-HybridCLR-startup-admission-probe-v1",
                    Project = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/..")),
                    UnityVersion = Application.unityVersion,
                    UnityRevision = Application.unityVersion == "6000.3.27f1" ? "672d562a42a7" : "1623fc0bbb97",
                    Receiver = Path.GetFileName(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/.."))),
                    OwnerSha256 = _ownerSha256,
                    Run = _run,
                    Probe = probe,
                    HostCreated = false,
                    Dirty = scene.isDirty,
                    SceneSha256 = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) ? HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path))) : null,
                    SceneMetaSha256 = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")) ? HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))) : null
                };
                using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(proofPath), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(proof, Formatting.Indented));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }

            private void BeforeSceneSaving(Scene scene, string path) => VerifyStartup(scene);
            private void BeforeSceneClosing(Scene scene, bool removing) => VerifyStartup(scene);
            private void BeforeSceneOpened(Scene scene, OpenSceneMode mode) => VerifyStartup(scene);

            private void VerifyStartup(Scene scene)
            {
                try
                {
                    ValidateRunnerScene(scene);
                    ValidateRunnerScenes(null);
                }
                catch (Exception exception)
                {
                    Abort(exception);
                }
            }

            private void StopStartupAdmission()
            {
                EditorSceneManager.sceneSaving -= BeforeSceneSaving;
                EditorSceneManager.sceneClosing -= BeforeSceneClosing;
                EditorSceneManager.sceneOpened -= BeforeSceneOpened;
            }

            private void Abort(Exception exception)
            {
                StopStartupAdmission();
                // 不能让回调异常被 runner 记录后继续执行 fixture；仅结束显式 owned batch 进程。
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                StopStartupAdmission();
                TestRunnerApi.UnregisterTestCallback(this);
            }
            public void TestStarted(ITestAdaptor test)
            {
            }
            public void TestFinished(ITestResultAdaptor result)
            {
            }
        }

        internal static void ValidateRunnerScenes(string savedScene)
        {
            if (SceneManager.sceneCount > 1)
            {
                throw new InvalidOperationException("Only one known runner bootstrap can be replaced.");
            }
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty || (!string.IsNullOrEmpty(scene.path) && scene.path != savedScene))
                {
                    throw new InvalidOperationException("Never replace dirty or unknown saved scenes in test setup.");
                }
                if (!string.IsNullOrEmpty(savedScene) && scene.path == savedScene && scene.rootCount == 0)
                {
                    continue;
                }
                ValidateRunnerScene(scene);
            }
        }

        internal static void ValidateRunnerScene(Scene scene)
        {
            if (scene.isDirty || !string.IsNullOrEmpty(scene.path))
            {
                throw new InvalidOperationException("Never replace dirty or unknown saved scenes in test setup.");
            }
            var roots = scene.GetRootGameObjects();
            if (roots.Length == 0)
            {
                return;
            }
            var camera = roots.Where(root => root.GetComponent<Camera>()).ToArray();
            var light = roots.Where(root => root.GetComponent<Light>()).ToArray();
            if (roots.Length != 2 || roots.Any(root => root.transform.childCount != 0) ||
                camera.Length != 1 || light.Length != 1 || camera[0] == light[0] || !camera[0].GetComponent<AudioListener>() ||
                camera[0].GetComponents<Component>().Any(component => !component ||
                    (component is not Transform && component is not Camera && component is not AudioListener)) ||
                light[0].GetComponents<Component>().Any(component => !component || (component is not Transform && component is not Light)))
            {
                throw new InvalidOperationException("Only the known clean official runner bootstrap may be replaced.");
            }
        }

        public static void Discover()
        {
            var arguments = Environment.GetCommandLineArgs();
            var rootIndex = Array.IndexOf(arguments, "-hclr-root");
            var runIndex = Array.IndexOf(arguments, "-hclr-run");
            if (rootIndex < 0 || rootIndex + 1 >= arguments.Length || runIndex < 0 || runIndex + 1 >= arguments.Length)
            {
                throw new InvalidOperationException("Explicit owned discovery root and run required.");
            }

            var evidence = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(arguments[rootIndex + 1]));
            var run = arguments[runIndex + 1];
            var project = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath + "/.."));
            var ownerPath = Path.Combine(evidence, "owner.json");
            var path = Path.Combine(evidence, run + ".json");
            foreach (var input in new[] { project, ownerPath, path })
            {
                for (var current = input; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                {
                    if ((File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(current)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(current))) && (File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(current)) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Linked discovery input refused.");
                    }
                }
            }
            var owner = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath)));
            if (!Application.isBatchMode || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed ||
                owner.Value<int>("schemaVersion") != 2 || owner.Value<bool>("simulation") ||
                owner.Value<string>("purpose") != "YLFramework-HybridCLR-9.0.3-independent-reception" ||
                !string.Equals(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(owner.Value<string>("project"))), project, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(owner.Value<string>("evidenceRoot"))), evidence, StringComparison.OrdinalIgnoreCase) ||
                Application.unityVersion != owner.Value<string>("expectedUnityVersion") ||
                System.Diagnostics.FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion != Application.unityVersion + "_" + owner.Value<string>("expectedUnityRevision") ||
                !System.Text.RegularExpressions.Regex.IsMatch(run, "^[A-Za-z0-9-]+$") || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(project + "/.git")) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
            {
                throw new InvalidOperationException("Actual idle owned receiver and new discovery output required.");
            }

            var names = new[] { "YLFramework.HybridCLR.Tests.EditMode", "YLFramework.HybridCLR.Tests.PlayMode" };
            var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            if (names.Any(name => graph.Count(assembly => assembly.name == name) != 1))
            {
                throw new InvalidOperationException("Both actual HybridCLR test assemblies must be compiled, not hidden by version defines.");
            }
            var installationPath = Path.Combine(evidence, "tests-installation.json");
            var installationBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(installationPath));
            var installation = JObject.Parse(System.Text.Encoding.UTF8.GetString(installationBytes));
            var sceneProfile = (string)installation["SceneProfile"];
            if ((string)installation["Purpose"] != "YLFramework-HybridCLR-receiver-tests-v1" ||
                (string)installation["Project"] != project || (string)installation["OwnerSha256"] != HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath))) ||
                sceneProfile is not "Common" and not "Owned" || installation.Value<int>("CommonSceneCases") != 5 ||
                installation.Value<int>("OwnedSceneCases") != (sceneProfile == "Owned" ? 8 : 0))
            {
                throw new InvalidOperationException("Explicit declared common and owned fixture sets are required.");
            }
            var testRoot = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(project, "Assets/Tests/YLFramework/Integrations/HybridCLR"))) + Path.DirectorySeparatorChar;
            foreach (var source in installation["Sources"])
            {
                var sourcePath = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical((string)source["Path"]));
                if (!sourcePath.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath)) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath + ".meta")) & FileAttributes.ReparsePoint) != 0 ||
                    HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath))) != (string)source["Sha256"] ||
                    HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sourcePath + ".meta"))) != (string)source["MetaSha256"])
                {
                    throw new InvalidOperationException("Discovery cannot rebaseline copied source or meta identity.");
                }
            }
            IEnumerable<JObject> Leaves(ITestAdaptor node, string assembly)
            {
                if (node.IsTestAssembly)
                {
                    assembly = Path.GetFileNameWithoutExtension(node.Name);
                }
                if (!node.HasChildren && !node.IsSuite && names.Contains(assembly))
                {
                    yield return JObject.FromObject(new
                    {
                        Name = node.FullName,
                        Assembly = assembly,
                        RunState = node.RunState.ToString()
                    });
                }
                foreach (var child in node.Children)
                {
                    foreach (var leaf in Leaves(child, assembly))
                    {
                        yield return leaf;
                    }
                }
            }

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RetrieveTestList(TestMode.EditMode, edit => api.RetrieveTestList(TestMode.PlayMode, play =>
            {
                var editRows = Leaves(edit, null).ToArray();
                var playRows = Leaves(play, null).ToArray();
                var commonCount = editRows.Count(row => ((string)row["Name"]).StartsWith("YLFramework.Tests.Integrations.HybridCLR.HybridCLRRunnerSceneTests.", StringComparison.Ordinal));
                var ownedCount = editRows.Count(row => ((string)row["Name"]).StartsWith("YLFramework.Tests.Integrations.HybridCLR.HybridCLROwnedRunnerSceneTests.", StringComparison.Ordinal));
                var passed = editRows.Length != 0 && playRows.Length != 0 && commonCount == 5 && ownedCount == (sceneProfile == "Owned" ? 8 : 0);
                var sources = graph.Where(assembly => names.Contains(assembly.name)).SelectMany(assembly => assembly.sourceFiles)
                    .Concat(names.Select((name, index) => $"Assets/Tests/YLFramework/Integrations/HybridCLR/{(index == 0 ? "EditMode" : "PlayMode")}/{name}.asmdef"))
                    .Select(source => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.IsPathRooted(source) ? source : Path.Combine(project, source))))
                    .Distinct(StringComparer.Ordinal).OrderBy(source => source, StringComparer.Ordinal)
                    .Select(source => new { Path = source, Sha256 = HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source))), MetaSha256 = HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta"))) }).ToArray();
                var result = new
                {
                    Passed = passed,
                    Project = project,
                    UnityVersion = Application.unityVersion,
                    UnityRevision = owner.Value<string>("expectedUnityRevision"),
                    Receiver = owner.Value<string>("receiver"),
                    OwnerSha256 = HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath))),
                    EditMode = editRows,
                    PlayMode = playRows,
                    Sources = sources,
                    SceneProfile = sceneProfile,
                    TestInstallationSha256 = HybridCLRManifest.Hash(installationBytes)
                };
                using (var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result, Formatting.Indented));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                EditorApplication.delayCall += () =>
                {
                    UnityEngine.Object.DestroyImmediate(api);
                    if (Argument(arguments, "-hclr-acceptance-policy") == "Known2022EditorExit")
                    {
                        WriteFunctionalCompletion(evidence, run, HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath))), new JObject
                        {
                            ["Phase"] = "Discovery",
                            ["Passed"] = passed,
                            ["CleanupProof"] = "DiscoveryApiDisposed",
                            ["OutputSha256"] = HybridCLRManifest.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                        });
                        Debug.Log("HYBRIDCLR_EDITOR_EXIT_REQUESTED " + run);
                    }
                    EditorApplication.Exit(passed ? 0 : 1);
                };
            }));
        }

        public static void Prepare()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Prepare owned test inputs before PlayMode.");
            }

            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "YLFramework.HybridCLR.Tests.PlayMode");
            var entryBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly.Location));
            var aot = typeof(HybridCLRManifest).Assembly;
            var metadata = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(aot.Location));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Root));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/Entry.bytes"), entryBytes);
            var conflictBytes = entryBytes.Concat(new byte[] { 1 }).ToArray();
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/ConflictEntry.bytes"), conflictBytes);
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/Metadata.bytes")))
            {
                AssetDatabase.DeleteAsset(Root + "/Metadata.bytes");
            }

            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/MetadataPayload.bytes"), metadata);
            foreach (var variant in new[] { "Good", "BadHash", "BadMvid", "Metadata", "Missing", "Conflict", "External" })
            {
                HybridCLRPayload Payload(System.Reflection.Assembly source, byte[] bytes, bool isAot) => new HybridCLRPayload
                {
                    Name = source.GetName().Name,
                    Identity = source.GetName().FullName,
                    Mvid = source.ManifestModule.ModuleVersionId.ToString("D"),
                    Address = HybridCLRManifest.PayloadAddress(source.GetName().Name, isAot),
                    Length = bytes.Length,
                    Sha256 = HybridCLRManifest.Hash(bytes)
                };
                var entry = Payload(assembly, variant == "Conflict" ? conflictBytes : entryBytes, false);
                if (variant == "BadHash")
                {
                    entry.Sha256 = new string('a', 64);
                }

                if (variant == "BadMvid")
                {
                    entry.Mvid = "74a011fb-8799-4f16-9d21-8fe27d01771d";
                }

                if (variant == "External")
                {
                    entry.Name = "Missing.External";
                    entry.Identity = "Missing.External, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
                    entry.Address = HybridCLRManifest.PayloadAddress(entry.Name, false);
                }
                var manifest = new HybridCLRManifest
                {
                    ProjectId = "hybridclr-test",
                    AppContentCompatibilityId = "base-test",
                    UnityVersion = Application.unityVersion,
                    Development = Debug.isDebugBuild,
                    HotUpdateAssemblies = new[] { entry },
                    AotMetadata = variant == "Metadata" ? new[] { Payload(aot, metadata, true) } : Array.Empty<HybridCLRPayload>()
                };
                manifest.Seal();
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "/" + variant + ".json"), manifest.ToJson());
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
