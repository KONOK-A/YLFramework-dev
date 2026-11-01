using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace YLFramework.Tests.Extensions.Domain
{
    public static class DomainBatchAcceptance
    {
        private static TestRunnerApi _api;
        private static readonly string[] _runtimeRoots = { "Reddot", "Logging", "Skill", "Buff", "Mission" };
        private static double _exitAfter;
        private static int _exitCode;

        public static void CleanupRecordedResidue()
        {
            var guid = Argument("-domainOrphanGuid");
            if (guid != "a2f9e79d32360ba4ba5496083c952b7c" || !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
            {
                throw new InvalidOperationException("Only the recorded deleted test root is eligible.");
            }

            var path = "Assets/YLFrameworkData/Mission/" + guid;
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*.asset"))
                {
                    var data = AssetDatabase.LoadAssetAtPath<YLFramework.Mission.MissionConfigData>(file.Replace('\\', '/'));
                    if (!data || !data.missionName.StartsWith("mission-", StringComparison.Ordinal) ||
                        !File.ReadAllText(file).Contains("guid: " + guid))
                    {
                        throw new InvalidOperationException("Not a recorded test artifact: " + file);
                    }
                }
                if (!AssetDatabase.DeleteAsset(path))
                {
                    throw new InvalidOperationException("Test residue cleanup failed.");
                }
            }
            var dataRoot = "Assets/YLFrameworkData";
            if (Directory.Exists(dataRoot) && !Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories).Any(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                AssetDatabase.DeleteAsset(dataRoot);
            }

            Debug.Log("YL_DOMAIN_OWNED_TEST_RESIDUE_REMOVED");
        }

        public static void RunSuite()
        {
            var output = Argument("-domainEvidence");
            if (string.IsNullOrEmpty(output) || !Directory.Exists(output))
            {
                throw new InvalidOperationException("Explicit evidence directory required.");
            }

            var mode = (TestMode)Enum.Parse(typeof(TestMode), Argument("-domainMode") ?? "EditMode");
            var filter = Argument("-domainFilter") ?? "YLFramework.Tests.Extensions.Domain";
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(new SuiteResult(output));
            _api.Execute(new ExecutionSettings(new Filter { testMode = mode, groupNames = new[] { filter } }));
        }
        private sealed class SuiteResult : ICallbacks
        {
            private readonly string _output;
            internal SuiteResult(string output) => _output = output;
            public void RunStarted(ITestAdaptor testsToRun)
            {
            }
            public void TestStarted(ITestAdaptor test)
            {
            }
            public void TestFinished(ITestResultAdaptor result)
            {
            }
            public void RunFinished(ITestResultAdaptor result)
            {
                File.WriteAllText(Path.Combine(_output, "results.xml"), result.ToXml().OuterXml);
                Debug.Log($"YL_DOMAIN_SUITE_RESULT passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount}");
                _exitCode = result.FailCount == 0 && result.SkipCount == 0 && result.PassCount > 0 ? 0 : 2;
                _exitAfter = EditorApplication.timeSinceStartup + 3;
                EditorApplication.update += FinishSuite;
            }
        }
        private static void FinishSuite()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _exitAfter = EditorApplication.timeSinceStartup + 3;
                return;
            }
            if (EditorApplication.timeSinceStartup < _exitAfter)
            {
                return;
            }

            EditorApplication.update -= FinishSuite;
            if (_api)
            {
                UnityEngine.Object.DestroyImmediate(_api);
            }

            EditorApplication.Exit(_exitCode);
        }

        public static void Audit()
        {
            try
            {
                var output = Argument("-domainEvidence");
                if (string.IsNullOrEmpty(output) || !Directory.Exists(output))
                {
                    throw new InvalidOperationException("Explicit existing C evidence directory required.");
                }

                var editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var player = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                var runtime = editor.Single(assembly => assembly.name == "YLFramework.Runtime");
                var playerRuntime = player.Single(assembly => assembly.name == "YLFramework.Runtime");
                var editorProduct = editor.Single(assembly => assembly.name == "YLFramework.Editor");
                var covered = new HashSet<string>(runtime.sourceFiles.Select(Normalize), StringComparer.OrdinalIgnoreCase);
                var modules = new Dictionary<string, string[]>();
                foreach (var module in _runtimeRoots)
                {
                    var files = Directory.GetFiles("Assets/YLFramework/Runtime/Systems/" + module, "*.cs", SearchOption.AllDirectories).Select(Normalize).ToArray();
                    if (files.Length == 0 || files.Any(file => !covered.Contains(file)))
                    {
                        throw new InvalidOperationException("Missing actual compilation source: " + module);
                    }

                    modules.Add(module, files);
                }
                var playerRefs = playerRuntime.assemblyReferences.Select(assembly => assembly.name)
                    .Concat(playerRuntime.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).Distinct().OrderBy(name => name).ToArray();
                if (playerRefs.Any(name => !new[] { "mscorlib", "netstandard", "System", "System.Core", "Microsoft.CSharp",
                    "Newtonsoft.Json", "BouncyCastle.Cryptography" }.Contains(name) && !name.StartsWith("UnityEngine.", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Forbidden Player assembly edge.");
                }

                if (runtime.defines.Contains("YL_DOMAIN_EXCEL"))
                {
                    throw new InvalidOperationException("C standalone audit must not enable optional Excel.");
                }

                var raw = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles("Assets", "*.meta", SearchOption.AllDirectories))
                {
                    var full = Path.GetFullPath(path);
                    var ioPath = Application.platform == RuntimePlatform.WindowsEditor ? "\\\\?\\" + full : full;
                    var match = Regex.Match(File.ReadAllText(ioPath), "(?m)^guid: ([a-fA-F0-9]{32})\\s*$");
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (!raw.TryGetValue(match.Groups[1].Value, out var paths))
                    {
                        raw.Add(match.Groups[1].Value, paths = new List<string>());
                    }

                    paths.Add(path.Replace('\\', '/'));
                }
                var domainPaths = AssetDatabase.GetAllAssetPaths().Where(IsDomainAsset).OrderBy(path => path).ToArray();
                var metas = domainPaths.Select(path => new { path, guid = AssetDatabase.AssetPathToGUID(path) }).ToArray();
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in metas)
                {
                    if (entry.guid.Length != 32 || !identities.Add(entry.guid) ||
                        AssetDatabase.GUIDToAssetPath(entry.guid) != entry.path ||
                        !raw.TryGetValue(entry.guid, out var paths) || paths.Count != 1 || paths[0] != entry.path + ".meta")
                    {
                        throw new InvalidOperationException("Missing/duplicate C identity: " + entry.path);
                    }
                }

                var record = new
                {
                    unity = Application.unityVersion,
                    projectPath = Path.GetFullPath("."),
                    runtimeSources = runtime.sourceFiles.OrderBy(path => path).ToArray(),
                    editorSources = editorProduct.sourceFiles.OrderBy(path => path).ToArray(),
                    runtimeEditorReferences = runtime.assemblyReferences.Select(assembly => assembly.name).ToArray(),
                    playerReferences = playerRefs,
                    modules,
                    metas,
                    ownedIdentityCount = identities.Count,
                    symbols = runtime.defines,
                    enterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                    enterPlayModeOptions = EditorSettings.enterPlayModeOptions.ToString(),
                    domainEditModeSources = editor.Single(assembly => assembly.name == "YLFramework.Tests.EditMode").sourceFiles.Where(IsDomainAsset).ToArray(),
                    domainPlayModeSources = editor.Single(assembly => assembly.name == "YLFramework.Tests.PlayMode").sourceFiles.Where(IsDomainAsset).ToArray()
                };
                File.WriteAllText(Path.Combine(output, "unity-input-guid-audit.json"), JsonConvert.SerializeObject(record, Formatting.Indented));
                _api = ScriptableObject.CreateInstance<TestRunnerApi>();
                _api.RetrieveTestList(TestMode.EditMode, edit =>
                {
                    var editTests = Leaves(edit).ToArray();
                    _api.RetrieveTestList(TestMode.PlayMode, play =>
                    {
                        var playTests = Leaves(play).ToArray();
                        File.WriteAllText(Path.Combine(output, "test-discovery.json"), JsonConvert.SerializeObject(new
                        {
                            editMode = editTests,
                            playMode = playTests
                        }, Formatting.Indented));
                        Debug.Log($"YL_DOMAIN_AUDIT_PASS EditMode={editTests.Length} PlayMode={playTests.Length} Meta={metas.Length}");
                        UnityEngine.Object.DestroyImmediate(_api);
                        EditorApplication.Exit(editTests.Length >= 57 && playTests.Length >= 12 ? 0 : 4);
                    });
                });
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(3);
            }
        }

        private static IEnumerable<string> Leaves(ITestAdaptor node)
        {
            if (!node.IsSuite && node.FullName.StartsWith("YLFramework.Tests.Extensions.Domain.", StringComparison.Ordinal))
            {
                yield return node.FullName;
            }

            foreach (var child in node.Children)
            {
                foreach (var name in Leaves(child))
                {
                    yield return name;
                }
            }
        }
        private static bool IsDomainAsset(string path) => _runtimeRoots.Any(module =>
            new[] { "Runtime/Systems/", "Tests/EditMode/Systems/", "Tests/PlayMode/Systems/" }.Any(prefix =>
                path == "Assets/YLFramework/" + prefix + module || path.StartsWith("Assets/YLFramework/" + prefix + module + "/", StringComparison.Ordinal))) ||
            new[] { "Editor/Systems/Skill", "Editor/Systems/Buff", "Editor/Systems/Mission", "Editor/Systems/Shared", "Tests/EditMode/Editor/Domain", "Tests/PlayMode/Systems/Domain", "Tests/Fixtures/Extensions/Domain", "Tests/Integrations/DomainExcel", "Integrations/DomainExcel" }
                .Any(root => path == "Assets/YLFramework/" + root || path.StartsWith("Assets/YLFramework/" + root + "/", StringComparison.Ordinal));
        private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');
        private static string Argument(string name)
        {
            var values = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(values, name);
            return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
        }
    }
}
