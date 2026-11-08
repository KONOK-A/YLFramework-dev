using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace YLFramework.Testing.Editor
{
    /// <summary>Official UTF adapter for an explicitly owned CLI host, not the user's open project.</summary>
    public static class YLTestEntry
    {
        private const string PendingKey = "YLTesting.Request";
        private const string PhaseKey = "YLTesting.Phase";
        private static Request _request;
        private static TestRunnerApi _api;
        private static Callbacks _callbacks;
        private static readonly List<TestRow> _tests = new();
        private static readonly List<TestRow> _excludedTests = new();
        private static bool _finishing;
        private static bool _phaseFinished;
        private static int _exitCode;

        /// <summary>CLI entry. Returning leaves the Editor update loop available for asynchronous UTF jobs.</summary>
        public static void Execute()
        {
            VerifyOwnedProject();
            try
            {
                var arguments = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(arguments, "-ylTestRequest");
                if (index < 0 || index + 1 >= arguments.Length)
                {
                    throw new ArgumentException("Missing -ylTestRequest.");
                }
                _request = ReadRequest(arguments[index + 1]);
                PrepareHostProfile();
                if (_request.action == "PrepareSamples")
                {
                    PrepareSamples();
                    return;
                }
                if (_request.samples?.Any(sample => sample.package == "com.kano.ylframework.hybridclr" && sample.name == "HybridCLR Module") == true)
                {
                    // 导入和 define 已在上一进程完成；每个模式按当前编译结果准备资源，不借前一测试的副作用。
                    var fixture = Type.GetType("YLFramework.Tests.Integrations.HybridCLR.HybridCLRTestFixtures, YLFramework.HybridCLR.Tests.EditMode", true);
                    fixture.GetMethod("Prepare").Invoke(null, null);
                }
                SessionState.SetString(PendingKey, Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(arguments[index + 1])));
                EnsureApi();
                if (_request.action == "Discover")
                {
                    Discover();
                }
                else
                {
                    BeginRun(_request.tests?.Any(test => test.mode == "EditMode") == true ? "EditMode" : "PlayMode");
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        private static void PrepareSamples()
        {
            if (_request.samples == null || _request.samples.Length == 0)
            {
                throw new InvalidOperationException("Sample preparation requires a nonempty explicit sample array.");
            }
            var target = EditorUserBuildSettings.selectedBuildTargetGroup;
            var symbols = new HashSet<string>(PlayerSettings.GetScriptingDefineSymbolsForGroup(target).Split(';').Where(value => !string.IsNullOrEmpty(value)));
            foreach (var input in _request.samples)
            {
                string symbol;
                if (input.package == "com.kano.ylframework" && input.name == "Resource Startup")
                {
                    symbol = "YL_TEST_RESOURCE_STARTUP_SAMPLE";
                }
                else if (input.package == "com.kano.ylframework.hybridclr" && input.name == "HybridCLR Module")
                {
                    symbol = "YL_TEST_HYBRIDCLR_SAMPLES";
                }
                else
                {
                    throw new InvalidOperationException("Undeclared test sample input: " + input.package + "/" + input.name);
                }
                var package = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Single(value => value.name == input.package);
                var samples = UnityEditor.PackageManager.UI.Sample.FindByPackage(package.name, package.version).Where(value => value.displayName == input.name).ToArray();
                if (samples.Length != 1)
                {
                    throw new InvalidOperationException("The selected package does not expose its declared sample.");
                }
                if (!samples[0].isImported && !samples[0].Import())
                {
                    throw new InvalidOperationException("The selected sample was not imported.");
                }
                symbols.Add(symbol);
            }
            PlayerSettings.SetScriptingDefineSymbolsForGroup(target, string.Join(";", symbols.OrderBy(value => value, StringComparer.Ordinal)));
            AssetDatabase.SaveAssets();
            Complete("Prepared", null, 0);
            EditorApplication.Exit(0);
        }

        /// <summary>Returns the current owned UTF run output; never authorizes an unbound project.</summary>
        public static string RequireOwnedRunOutput()
        {
            var pending = SessionState.GetString(PendingKey, string.Empty);
            if (string.IsNullOrEmpty(pending))
            {
                throw new InvalidOperationException("This fixture requires a current owned UTF run.");
            }
            var request = ReadRequest(pending);
            if (request.action != "Run")
            {
                throw new InvalidOperationException("Discovery cannot own fixture output.");
            }
            return Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(request.output));
        }

        [InitializeOnLoadMethod]
        private static void RestoreAfterReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Detach;
            var pending = SessionState.GetString(PendingKey, string.Empty);
            if (string.IsNullOrEmpty(pending))
            {
                return;
            }
            VerifyOwnedProject();
            try
            {
                _request = ReadRequest(pending);
                EnsureApi();
                if (_request.action == "Discover")
                {
                    Discover();
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        private static Owner VerifyOwnedProject()
        {
            if (!Application.isBatchMode && Array.IndexOf(Environment.GetCommandLineArgs(), "-ylTestInteractive") < 0)
            {
                throw new InvalidOperationException("This entry cannot run or exit an interactive Editor.");
            }
            var project = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(Application.dataPath)));
            var owner = JsonUtility.FromJson<Owner>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(project, ".yl-owner.json"))));
            if (owner.purpose != "YLTesting.Directory" || owner.kind != "Host" ||
                !string.Equals(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(owner.path)), project, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The batch project is not an owned YL testing host.");
            }
            return owner;
        }

        private static void PrepareHostProfile()
        {
            if (Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", false) != null)
            {
                using (var inputSettings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]))
                {
                    var inputHandler = inputSettings.FindProperty("activeInputHandler");
                    if (inputHandler == null)
                    {
                        throw new InvalidOperationException("The owned Input System host has no active input handling setting.");
                    }
                    if (inputHandler.intValue != 2)
                    {
                        inputHandler.intValue = 2;
                        inputSettings.ApplyModifiedPropertiesWithoutUndo();
                        AssetDatabase.SaveAssetIfDirty(inputSettings.targetObject);
                    }
                }
            }
            if (_request.prepareSceneTemplates && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/SceneTemplateSettings.json")))
            {
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (UnityEngine.SceneManagement.SceneManager.sceneCount != 1 || active.isDirty ||
                    !string.IsNullOrEmpty(active.path) || active.rootCount != 0)
                {
                    throw new InvalidOperationException("Scene preparation requires the owned host's empty untouched startup scene.");
                }
                const string scenePath = "Assets/YLTestingProfile/Empty.unity";
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(scenePath)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(scenePath + ".meta")))
                {
                    throw new IOException("Do not replace an existing preparation scene.");
                }
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical("Assets/YLTestingProfile"));
                var owned = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
                if (!UnityEditor.SceneManagement.EditorSceneManager.SaveScene(owned, scenePath))
                {
                    throw new IOException("The owned preparation scene was not saved.");
                }
                if (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings/SceneTemplateSettings.json")))
                {
                    throw new InvalidOperationException("The actual scene-template settings were not initialized.");
                }
            }
            if (_request.disableDomainReload)
            {
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                EditorSettings.enterPlayModeOptionsEnabled = true;
            }
            if (_request.prepareSessionScene)
            {
                const string scenePath = "Assets/YLTestingProfile/Empty.unity";
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (!_request.prepareSceneTemplates || !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(scenePath)) ||
                    UnityEngine.SceneManagement.SceneManager.sceneCount != 1 || active.isDirty || active.rootCount != 0 ||
                    (!string.IsNullOrEmpty(active.path) && active.path != scenePath))
                {
                    throw new InvalidOperationException("Session preparation requires the owned empty startup or preparation scene.");
                }
                // UTF 对已保存场景追加空场景；无路径启动场景会被替换为含 Camera/Light 的场景。
                var prepared = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath);
                if (prepared.isDirty || prepared.rootCount != 0)
                {
                    throw new InvalidOperationException("The owned preparation scene is no longer empty and clean.");
                }
            }
            if (string.IsNullOrEmpty(_request.sortingLayer) || SortingLayer.layers.Any(layer => layer.name == _request.sortingLayer))
            {
                return;
            }
            if (!_request.sortingLayer.StartsWith("YL.Testing.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Only an explicitly named testing SortingLayer can be prepared.");
            }
            // ReadRequest has already verified this batch host; never mutate source-project settings.
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").First();
            using (var serialized = new SerializedObject(settings))
            {
                var layers = serialized.FindProperty("m_SortingLayers");
                const int Id = 1498159105;
                if (SortingLayer.layers.Any(layer => layer.id == Id))
                {
                    throw new InvalidOperationException("The testing SortingLayer identity is already occupied.");
                }
                int index = layers.arraySize;
                layers.InsertArrayElementAtIndex(index);
                var layer = layers.GetArrayElementAtIndex(index);
                layer.FindPropertyRelative("name").stringValue = _request.sortingLayer;
                layer.FindPropertyRelative("uniqueID").intValue = Id;
                layer.FindPropertyRelative("locked").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(settings);
            }
            if (!SortingLayer.layers.Any(layer => layer.name == _request.sortingLayer && layer.id == 1498159105))
            {
                throw new InvalidOperationException("The actual testing SortingLayer was not created.");
            }
        }

        private static Request ReadRequest(string path)
        {
            var owner = VerifyOwnedProject();
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            bool desktopFlag = Array.IndexOf(Environment.GetCommandLineArgs(), "-ylTestInteractive") >= 0;
            if (request.interactiveDesktop != desktopFlag || Application.isBatchMode == request.interactiveDesktop)
            {
                throw new InvalidOperationException("The explicit owned desktop/batch profile does not match the actual Editor.");
            }
            var workspace = Directory.GetParent(global::YLFramework.Testing.PackageTestPaths.Physical(owner.path))?.Parent;
            if (workspace == null || request.schemaVersion != 1 || request.hostId != owner.id ||
                request.workspaceId != owner.workspaceId || request.expectedUnityVersion != Application.unityVersion ||
                (request.action != "Discover" && request.action != "Run" && request.action != "PrepareSamples") ||
                (string.IsNullOrEmpty(request.category) == (request.assemblies == null || request.assemblies.Length == 0)))
            {
                throw new InvalidOperationException("Request identity, action or actual Unity version does not match.");
            }
            if (request.action == "Run" && (request.tests == null || request.tests.Length == 0 ||
                request.tests.Select(test => test.mode).Distinct(StringComparer.Ordinal).Count() != 1 ||
                request.tests.Any(test => test.mode != "EditMode" && test.mode != "PlayMode")))
            {
                throw new InvalidOperationException("A native process must execute exactly one nonempty supported mode.");
            }
            var output = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(request.output));
            var resultsRoot = Path.Combine(workspace.FullName, "Results") + Path.DirectorySeparatorChar;
            if (!output.StartsWith(resultsRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)))
            {
                throw new InvalidOperationException("Output must already exist under the owned workspace results.");
            }
            var resultDirectory = new DirectoryInfo(output);
            while (resultDirectory != null && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(resultDirectory.FullName, ".yl-owner.json"))))
            {
                resultDirectory = resultDirectory.Parent;
            }
            var resultOwner = resultDirectory == null ? null : JsonUtility.FromJson<Owner>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(resultDirectory.FullName, ".yl-owner.json"))));
            if (resultOwner == null || resultOwner.kind != "Result" || resultOwner.id != request.resultId || resultOwner.workspaceId != owner.workspaceId)
            {
                throw new InvalidOperationException("Output result identity does not match the caller.");
            }
            var inputs = JsonUtility.FromJson<InputReceipt>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(owner.path, ".yl-inputs.json"))));
            if (inputs.inputHash != request.inputHash)
            {
                throw new InvalidOperationException("Host inputs do not match the current request.");
            }
            for (var current = output; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(current)) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Linked output paths are refused.");
                }
            }
            return request;
        }

        private static void EnsureApi()
        {
            if (_api)
            {
                return;
            }
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.hideFlags = HideFlags.HideAndDontSave;
            _callbacks = new Callbacks();
            _api.RegisterCallbacks(_callbacks);
        }

        private static void Discover()
        {
            _tests.Clear();
            _excludedTests.Clear();
            _api.RetrieveTestList(TestMode.EditMode, edit =>
            {
                try
                {
                    Collect(edit, "EditMode", null, "Runnable", false, false);
                    _api.RetrieveTestList(TestMode.PlayMode, play =>
                    {
                        try
                        {
                            Collect(play, "PlayMode", null, "Runnable", false, false);
                            if (_tests.Count == 0)
                            {
                                throw new InvalidOperationException("Zero categorized UTF leaves were discovered.");
                            }
                            Complete("Completed", null, 0);
                        }
                        catch (Exception error) { Fail(error); }
                    });
                }
                catch (Exception error) { Fail(error); }
            });
        }

        private static void Collect(ITestAdaptor node, string mode, string assembly, string runState, bool categorized, bool excluded)
        {
            if (node.IsTestAssembly)
            {
                assembly = Path.GetFileName(node.Name);
                if (assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || assembly.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    assembly = assembly.Substring(0, assembly.Length - 4);
                }
                categorized = _request.assemblies != null && _request.assemblies.Contains(assembly, StringComparer.Ordinal);
            }
            if (node.RunState != RunState.Runnable)
            {
                runState = node.RunState.ToString();
            }
            categorized |= !string.IsNullOrEmpty(_request.category) && node.Categories != null && node.Categories.Contains(_request.category, StringComparer.Ordinal);
            excluded |= node.Categories != null && _request.excludedCategories != null && node.Categories.Intersect(_request.excludedCategories, StringComparer.Ordinal).Any();
            if (!node.IsSuite && categorized && assembly != null)
            {
                var row = new TestRow { assembly = assembly, fullName = node.FullName, mode = mode, runState = runState };
                if (excluded)
                {
                    _excludedTests.Add(row);
                }
                else
                {
                    _tests.Add(row);
                }
            }
            foreach (var child in node.Children)
            {
                Collect(child, mode, assembly, runState, categorized, excluded);
            }
        }

        private static void BeginRun(string mode)
        {
            var selected = _request.tests?.Where(test => test.mode == mode).ToArray();
            if (selected == null || selected.Length == 0)
            {
                throw new InvalidOperationException("Zero selected leaves for " + mode);
            }
            _phaseFinished = false;
            SessionState.SetString(PhaseKey, mode);
            _api.Execute(new ExecutionSettings(new Filter
            {
                testMode = mode == "EditMode" ? TestMode.EditMode : TestMode.PlayMode,
                assemblyNames = selected.Select(test => test.assembly).Distinct(StringComparer.Ordinal).ToArray(),
                testNames = selected.Select(test => test.fullName).ToArray()
            }));
        }

        private static void PrepareSessionRun()
        {
            if (!_request.prepareSessionScene)
            {
                return;
            }
            VerifyOwnedProject();
            const string scenePath = "Assets/YLTestingProfile/Empty.unity";
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (UnityEngine.SceneManagement.SceneManager.sceneCount == 1 && string.IsNullOrEmpty(active.path))
            {
                var roots = active.GetRootGameObjects();
                var allowed = new[] { typeof(Transform), typeof(Camera), typeof(AudioListener), typeof(Light) };
                if (active.isDirty || roots.Length != 2 || roots.Any(root => root.transform.childCount != 0 ||
                    root.GetComponents<Component>().Any(component => !component || !allowed.Contains(component.GetType()))) ||
                    roots.Count(root => root.name == "Main Camera" && root.GetComponent<Camera>() && root.GetComponent<AudioListener>()) != 1 ||
                    roots.Count(root => root.name == "Directional Light" && root.GetComponent<Light>()) != 1)
                {
                    throw new InvalidOperationException("Only UTF's untouched default bootstrap scene can be replaced.");
                }
                // UTF 1.6 总是重建 Single 默认场景；仅在测试尚未开始的自有回调中替换它。
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath);
                active = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Additive);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(active);
            }
            var scenes = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            if (UnityEngine.SceneManagement.SceneManager.sceneCount != 2 || active.isDirty || active.rootCount != 0 || !string.IsNullOrEmpty(active.path) ||
                scenes.Count(scene => scene.path == scenePath && scene.isLoaded) != 1)
            {
                throw new InvalidOperationException("The owned session run requires a saved preparation scene and an active empty runner scene: " +
                    UnityEngine.SceneManagement.SceneManager.sceneCount + ", " + active.path + ", roots=" + active.rootCount + ", dirty=" + active.isDirty);
            }
            WriteNew(Path.Combine(_request.output, "session-scenes.json"), JsonUtility.ToJson(new SessionScenes
            {
                savedScene = scenePath,
                sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount,
                activeRootCount = active.rootCount
            }, true));
        }

        private static void Detach()
        {
            if (_api)
            {
                _api.UnregisterCallbacks(_callbacks);
                UnityEngine.Object.DestroyImmediate(_api);
                _api = null;
            }
            _callbacks = null;
        }

        private static void WriteNew(string path, string content)
        {
            using var stream = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        private static void Complete(string status, string error, int exitCode)
        {
            if (_finishing)
            {
                return;
            }
            _finishing = true;
            SessionState.EraseString(PendingKey);
            SessionState.EraseString(PhaseKey);
            try
            {
                if (_request != null)
                {
                    var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                    var runtime = graph.FirstOrDefault(assembly => assembly.name == "YLFramework.Runtime");
                    var project = Path.GetDirectoryName(Application.dataPath);
                    var sourcePrefixes = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                        .Where(package => package.name.StartsWith("com.kano.ylframework", StringComparison.Ordinal))
                        .Select(package => Path.GetFullPath(package.resolvedPath) + Path.DirectorySeparatorChar)
                        .Append(Path.Combine(project, "Assets", "Tests", "YLFramework") + Path.DirectorySeparatorChar)
                        .Append(Path.Combine(project, "Assets", "Samples") + Path.DirectorySeparatorChar).ToArray();
                    Func<string, string> fullPath = path => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    var compiledInputs = graph.Where(assembly => assembly.sourceFiles.Any(source => sourcePrefixes.Any(prefix => fullPath(source).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                        .SelectMany(assembly => assembly.compiledAssemblyReferences.Concat(assembly.assemblyReferences.Select(reference => reference.outputPath)).Append(assembly.outputPath))
                        .Concat(string.IsNullOrEmpty(_request.sortingLayer) ? Array.Empty<string>() : new[] { Path.Combine(project, "ProjectSettings", "TagManager.asset") })
                        .Concat(_request.disableDomainReload ? new[] { Path.Combine(project, "ProjectSettings", "EditorSettings.asset") } : Array.Empty<string>())
                        .Concat(_request.prepareSceneTemplates ? new[] { Path.Combine(project, "ProjectSettings", "SceneTemplateSettings.json"),
                            Path.Combine(project, "Assets", "YLTestingProfile", "Empty.unity"), Path.Combine(project, "Assets", "YLTestingProfile", "Empty.unity.meta") } : Array.Empty<string>())
                        .Select(fullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                    WriteNew(Path.Combine(_request.output, "response.json"), JsonUtility.ToJson(new Response
                    {
                        schemaVersion = 1,
                        status = status,
                        error = error,
                        hostId = _request.hostId,
                        inputHash = _request.inputHash,
                        unityVersion = Application.unityVersion,
                        testFrameworkVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TestRunnerApi).Assembly).version,
                        isBatchMode = Application.isBatchMode,
                        tests = _tests.ToArray(),
                        excludedTests = _excludedTests.ToArray(),
                        compiledInputs = compiledInputs,
                        runtimeReferences = runtime?.assemblyReferences.Select(assembly => assembly.name).OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>()
                    }, true));
                }
            }
            catch (Exception persistenceError)
            {
                Debug.LogException(persistenceError);
                exitCode = 1;
            }
            _exitCode = exitCode;
            EditorApplication.update += ExitAfterCallbacks;
        }

        private static void ExitAfterCallbacks()
        {
            EditorApplication.update -= ExitAfterCallbacks;
            Detach();
            Debug.Log("[YLTesting] Requesting normal Editor exit after UTF callbacks returned, code " + _exitCode);
            EditorApplication.Exit(_exitCode);
        }

        private static void Fail(Exception error)
        {
            Debug.LogException(error);
            Complete("Failed", error.ToString(), 1);
        }

        private static void WriteProgress(string phase, string fullName)
        {
            try
            {
                File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_request.output, "progress.jsonl")), JsonUtility.ToJson(new Progress
                {
                    phase = phase,
                    fullName = fullName,
                    utc = DateTime.UtcNow.ToString("o")
                }) + Environment.NewLine);
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        [Serializable]
        private sealed class Progress
        {
            public string phase;
            public string fullName;
            public string utc;
        }

        [Serializable]
        private sealed class SessionScenes
        {
            public string savedScene;
            public int sceneCount;
            public int activeRootCount;
        }

        private sealed class Callbacks : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                try
                {
                    PrepareSessionRun();
                }
                catch (Exception error)
                {
                    Fail(error);
                }
            }
            public void TestStarted(ITestAdaptor test)
            {
                if (!test.IsSuite)
                {
                    WriteProgress("Started", test.FullName);
                }
            }
            public void TestFinished(ITestResultAdaptor result)
            {
                if (!result.Test.IsSuite)
                {
                    WriteProgress("Finished:" + result.TestStatus, result.Test.FullName);
                }
            }
            public void OnError(string message) => Fail(new InvalidOperationException(message));

            public void RunFinished(ITestResultAdaptor result)
            {
                if (_phaseFinished || _finishing)
                {
                    return;
                }
                _phaseFinished = true;
                try
                {
                    var mode = SessionState.GetString(PhaseKey, string.Empty);
                    WriteNew(Path.Combine(_request.output, "results-" + mode + ".xml"), result.ToXml().OuterXml);
                    if (result.TestStatus != TestStatus.Passed || result.PassCount == 0 || result.FailCount != 0 || result.SkipCount != 0 || result.InconclusiveCount != 0)
                    {
                        throw new InvalidOperationException("UTF did not pass all selected leaves in " + mode);
                    }
                    Complete("Passed", null, 0);
                }
                catch (Exception error) { Fail(error); }
            }
        }

        [Serializable]
        private sealed class Owner
        {
            public string purpose = string.Empty;
            public string kind = string.Empty;
            public string path = string.Empty;
            public string id = string.Empty;
            public string workspaceId = string.Empty;
        }

        [Serializable]
        private sealed class InputReceipt
        {
            public string inputHash = string.Empty;
        }

        [Serializable]
        private sealed class SampleInput
        {
            public string package;
            public string name;
        }

        [Serializable]
        private sealed class Request
        {
            public int schemaVersion = 0;
            public string hostId = string.Empty;
            public string workspaceId = string.Empty;
            public string resultId = string.Empty;
            public string expectedUnityVersion = string.Empty;
            public string inputHash = string.Empty;
            public string action = string.Empty;
            public string category = string.Empty;
            public string[] assemblies = Array.Empty<string>();
            public SampleInput[] samples = Array.Empty<SampleInput>();
            public string[] excludedCategories = Array.Empty<string>();
            public string sortingLayer = string.Empty;
            public bool disableDomainReload;
            public bool prepareSceneTemplates;
            public bool prepareSessionScene;
            public bool interactiveDesktop;
            public string output = string.Empty;
            public TestRow[] tests = Array.Empty<TestRow>();
        }

        [Serializable]
        private sealed class TestRow
        {
            public string assembly;
            public string fullName;
            public string mode;
            public string runState;
        }

        [Serializable]
        private sealed class Response
        {
            public int schemaVersion;
            public string status;
            public string error;
            public string hostId;
            public string inputHash;
            public string unityVersion;
            public string testFrameworkVersion;
            public bool isBatchMode;
            public TestRow[] tests;
            public TestRow[] excludedTests;
            public string[] runtimeReferences;
            public string[] compiledInputs;
        }
    }
}
