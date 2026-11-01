#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace YLFramework.Testing
{
    /// <summary>One-shot distribution verifier copied outside the product export roots into an owned batch host.</summary>
    public static class DistributionEntry
    {
        public static void Execute()
        {
            JObject request = null;
            bool ownsInvocation = false;
            try
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-ylDistributionRequest");
                if (index < 0 || index + 1 >= args.Length)
                {
                    throw new ArgumentException("An explicit distribution request is required.");
                }
                request = JObject.Parse(File.ReadAllText(args[index + 1]));
                var project = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                var owner = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
                var output = Path.GetFullPath((string)request["output"]);
                var workspace = Directory.GetParent(project)?.Parent?.FullName;
                var resultRoot = Path.GetFullPath((string)request["resultRoot"]);
                var resultOwner = JObject.Parse(File.ReadAllText(Path.Combine(resultRoot, ".yl-owner.json")));
                if (!Application.isBatchMode || (string)owner["purpose"] != "YLTesting.Directory" ||
                    (string)owner["kind"] != "Host" || (string)owner["path"] != project ||
                    (string)owner["id"] != (string)request["hostId"] ||
                    (string)resultOwner["kind"] != "Result" || (string)resultOwner["id"] != (string)request["resultId"] ||
                    (string)resultOwner["workspaceId"] != (string)owner["workspaceId"] || workspace == null ||
                    !resultRoot.StartsWith(Path.Combine(workspace, "Results") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !output.StartsWith(resultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(output) || Application.unityVersion != (string)request["unityVersion"] ||
                    EditorUtility.scriptCompilationFailed || EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    throw new InvalidOperationException("The distribution host, result, version or compilation is not valid.");
                }
                for (var current = output; current != null; current = Path.GetDirectoryName(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Linked verification output is refused.");
                    }
                }
                ownsInvocation = true;

                string phase = (string)request["phase"];
                object result;
                if (phase == "Export")
                {
                    string package = Path.Combine(output, "YLFramework.unitypackage");
                    var exporter = Type.GetType("YLFramework.Editor.Distribution.ExportUnityPackage, YLFramework.Editor", true);
                    exporter.GetMethod("Export", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { package, false });
                    result = new { status = "Completed", package, bytes = new FileInfo(package).Length };
                }
                else if (phase == "Inspect")
                {
                    var editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                    var player = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
                    var graph = editor.Concat(player).Select(assembly => new
                    {
                        assembly.name,
                        assembly.sourceFiles,
                        references = assembly.assemblyReferences.Select(reference => reference.name)
                            .Concat(assembly.compiledAssemblyReferences.Select(Path.GetFileName)).ToArray()
                    }).ToArray();
                    if (graph.Any(assembly => assembly.sourceFiles.Any(path => path.Replace('\\', '/').Contains("Assets/YLFramework/Tests/"))))
                    {
                        throw new InvalidOperationException("Bare import contains test sources.");
                    }
                    var runtime = player.Single(assembly => assembly.name == "YLFramework.Runtime");
                    var references = runtime.assemblyReferences.Select(reference => reference.name)
                        .Concat(runtime.compiledAssemblyReferences.Select(Path.GetFileName)).ToArray();
                    RejectReferences(references);
                    foreach (var row in (JArray)request["guids"])
                    {
                        if (AssetDatabase.AssetPathToGUID((string)row["path"]) != (string)row["guid"])
                        {
                            throw new InvalidOperationException("Imported asset identity changed: " + (string)row["path"]);
                        }
                    }
                    var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/YLFramework" })
                        .Select(guid => AssetDatabase.GUIDToAssetPath(guid)).ToArray();
                    foreach (var path in prefabs)
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab.GetComponentsInChildren<Transform>(true).Sum(transform =>
                                GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject)) != 0)
                        {
                            throw new InvalidOperationException("Imported prefab has missing scripts: " + path);
                        }
                    }
                    result = new { status = "Passed", graph, runtimeReferences = references, prefabCount = prefabs.Length, missingScripts = 0 };
                }
                else if (phase == "HybridConfigure" || phase == "HybridBuild")
                {
                    var type = Type.GetType("YLFramework.Testing.HybridCLREntry, YLTesting.HybridCLR.Editor", true);
                    result = type.GetMethod("Execute").Invoke(null, new object[] { request });
                }
                else if (phase == "Build")
                {
                    if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                    {
                        WriteReport(request, new { status = "PrerequisiteNotMet", reason = "Windows64 Player support is not installed." });
                        EditorApplication.Exit(2);
                        return;
                    }
                    if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                    {
                        throw new InvalidOperationException("The verifier will not implicitly switch build targets.");
                    }
                    var backend = (string)request["backend"];
                    if (backend != "Mono" && backend != "IL2CPP")
                    {
                        throw new InvalidOperationException("An explicit supported Player backend is required.");
                    }
                    PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,
                        backend == "IL2CPP" ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
                    if (backend == "IL2CPP")
                    {
                        PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Standalone, Il2CppCompilerConfiguration.Release);
                        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Low);
                    }
                    var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    bool smoke = (bool?)request["smoke"] == true;
                    if (smoke)
                    {
                        var type = Type.GetType("YLFramework.Testing.PlayerSmoke, YLTesting.PlayerSmoke", true);
                        var probe = new GameObject("YL.PlayerSmoke").AddComponent(type);
                        type.GetField("resultId").SetValue(probe, (string)request["resultId"]);
                        type.GetField("hostId").SetValue(probe, (string)request["hostId"]);
                    }
                    const string scenePath = "Assets/YLTestingDistribution/DistributionScene.unity";
                    if (!EditorSceneManager.SaveScene(scene, scenePath))
                    {
                        throw new IOException("The owned build scene was not saved.");
                    }
                    var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = new[] { scenePath },
                        target = BuildTarget.StandaloneWindows64,
                        options = BuildOptions.None,
                        locationPathName = Path.Combine(output, "Player", "YLDistribution.exe")
                    });
                    if (build.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded || build.summary.totalErrors != 0)
                    {
                        throw new InvalidOperationException("The ordinary Player build failed: " + build.summary.result);
                    }
                    var managed = backend == "IL2CPP" ? "Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped" :
                        Path.Combine(output, "Player", "YLDistribution_Data", "Managed");
                    var files = Directory.GetFiles(managed, "*.dll");
                    if (files.Any(path => Regex.IsMatch(Path.GetFileName(path), "TestRunner|nunit|YLFramework.*(Tests|Acceptance|Editor|Analyzer)", RegexOptions.IgnoreCase)))
                    {
                        throw new InvalidOperationException("Player output contains test, acceptance, Editor or Analyzer assemblies.");
                    }
                    var assembly = System.Reflection.Assembly.ReflectionOnlyLoadFrom(Path.Combine(managed, "YLFramework.Runtime.dll"));
                    var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
                    RejectReferences(references);
                    if (backend == "IL2CPP" && (!File.Exists(Path.Combine(output, "Player/GameAssembly.dll")) ||
                        !File.Exists(Path.Combine(output, "Player/YLDistribution_Data/il2cpp_data/Metadata/global-metadata.dat"))))
                    {
                        throw new InvalidOperationException("Actual IL2CPP native assembly/metadata artifacts are missing.");
                    }
                    if (smoke && !files.Any(path => Path.GetFileName(path) == "YLTesting.PlayerSmoke.dll"))
                    {
                        throw new InvalidOperationException("The explicit Player smoke assembly is missing from the actual build.");
                    }
                    result = new { status = "Passed", backend, buildOptions = "None", smoke, managed,
                        executable = Path.Combine(output, "Player", "YLDistribution.exe"),
                        errors = build.summary.totalErrors, warnings = build.summary.totalWarnings, files, runtimeReferences = references };
                }
                else
                {
                    throw new ArgumentException("Unknown distribution phase.");
                }
                WriteReport(request, result);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                if (!ownsInvocation)
                {
                    throw;
                }
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void RejectReferences(string[] references)
        {
            var rejectedReferences = references.Where(reference => !Regex.IsMatch(reference,
                @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)(?:\.dll)?$") ||
                Regex.IsMatch(reference, "TestRunner|Tests|Editor|Analyzer", RegexOptions.IgnoreCase)).ToArray();
            if (rejectedReferences.Length != 0)
            {
                throw new InvalidOperationException("Production Runtime has forbidden references: " + string.Join(", ", rejectedReferences));
            }
        }

        private static void WriteReport(JObject request, object result)
        {
            var path = Path.Combine((string)request["output"], "report.json");
            if (File.Exists(path))
            {
                throw new IOException("Do not overwrite distribution evidence.");
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(new
            {
                schemaVersion = 1,
                hostId = (string)request["hostId"],
                resultId = (string)request["resultId"],
                unityVersion = Application.unityVersion,
                phase = (string)request["phase"],
                capturedUtc = DateTime.UtcNow.ToString("o"),
                result
            }, Formatting.Indented));
        }
    }
}
#endif
