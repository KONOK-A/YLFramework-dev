#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Testing
{
    /// <summary>One-shot UPM verifier installed only in an explicitly owned batch host.</summary>
    public static class DistributionEntry
    {
        public static void Execute()
        {
            Run(Environment.GetCommandLineArgs());
        }

        private static void Run(string[] args)
        {
            JObject request = null;
            bool ownsInvocation = false;
            try
            {
                int index = Array.IndexOf(args, "-ylDistributionRequest");
                if (index < 0 || index + 1 >= args.Length)
                {
                    throw new ArgumentException("An explicit distribution request is required.");
                }
                request = ReadJson(args[index + 1]);
                ValidateInvocation(request);
                ownsInvocation = true;

                var output = Text(request, "output");
                string phase = (string)request["phase"];
                object result;
                var packages = CapturePackages(request);
                if (phase == "Inspect")
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
                    if (graph.Any(assembly => Regex.IsMatch(assembly.name, "TestRunner|nunit|Tests|Acceptance", RegexOptions.IgnoreCase) ||
                        assembly.sourceFiles.Any(path => Regex.IsMatch(path.Replace('\\', '/'), @"(?:^|/)Assets/(?:Tests|YLFramework)/"))))
                    {
                        throw new InvalidOperationException("The UPM consumer contains tests or a duplicate legacy product source.");
                    }
                    var runtime = player.Single(assembly => assembly.name == "YLFramework.Runtime");
                    var references = runtime.assemblyReferences.Select(reference => reference.name)
                        .Concat(runtime.compiledAssemblyReferences.Select(Path.GetFileName)).ToArray();
                    RejectReferences(references);
                    var main = packages.Cast<JObject>().Single(package => Text(package, "name") == "com.kano.ylframework");
                    var requiredAssemblies = CaptureMainAssemblies(request, main, editor, player);
                    foreach (var package in packages.Cast<JObject>())
                    {
                        var root = Text(package, "resolvedPath");
                        foreach (var meta in PackageFiles(root).Where(path => path.EndsWith(".meta", StringComparison.Ordinal)))
                        {
                            var match = Regex.Match(File.ReadAllText(meta), @"(?m)^guid:\s*([0-9a-fA-F]{32})\s*\r?$");
                            if (!match.Success)
                            {
                                throw new InvalidOperationException("Installed asset metadata has no GUID: " + meta);
                            }
                            var relative = meta.Substring(root.Length + 1).Replace('\\', '/');
                            if (relative.Substring(0, relative.Length - 5).Split('/').Any(part => part.EndsWith("~", StringComparison.Ordinal)))
                            {
                                continue;
                            }
                            var asset = Text(package, "assetPath") + "/" + relative.Substring(0, relative.Length - 5);
                            if (AssetDatabase.AssetPathToGUID(asset) != match.Groups[1].Value)
                            {
                                throw new InvalidOperationException("Installed asset GUID differs from package metadata: " + asset);
                            }
                        }
                    }
                    var prefabs = AssetDatabase.FindAssets("t:Prefab", packages.Cast<JObject>().Select(package => Text(package, "assetPath")).ToArray())
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
                    foreach (var required in new[] { "Runtime/Core/Resources/YLFramework/Core/Canvas.prefab", "Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab", "Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab" })
                    {
                        if (!File.Exists(Path.Combine(Text(main, "resolvedPath"), required)))
                        {
                            throw new InvalidOperationException("The installed package omitted a required runtime resource: " + required);
                        }
                    }
                    result = new { status = "Passed", graph, requiredAssemblies, runtimeReferences = references, prefabCount = prefabs.Length, missingScripts = 0 };
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
                try { WriteReport(request, new { status = "Failed", error = error.ToString() }); }
                finally
                {
                    Debug.LogException(error);
                    EditorApplication.Exit(1);
                }
            }
        }

        private static JArray CaptureMainAssemblies(JObject request, JObject main, Assembly[] editor, Assembly[] player)
        {
            string root = Path.GetFullPath(Text(main, "resolvedPath"));
            string assetRoot = Text(main, "assetPath") + "/";
            var expected = ((JArray)request["packages"]).Cast<JObject>().Single(package => Text(package, "name") == "com.kano.ylframework");
            var installedFiles = PackageFiles(root).Select(path => path.Substring(root.Length + 1).Replace('\\', '/')).ToArray();
            // Existing File/Git v2 requests bind the installed inventory by digest/SHA without a files array.
            var files = expected["files"] is JArray frozen ? frozen.Cast<JObject>().Select(file => Text(file, "path")).ToArray() :
                installedFiles;
            string Physical(string source)
            {
                string normalized = source.Replace('\\', '/');
                return Path.GetFullPath(normalized.StartsWith(assetRoot, StringComparison.Ordinal) ? Path.Combine(root, normalized.Substring(assetRoot.Length)) :
                    Path.IsPathRooted(source) ? source : Path.Combine(Project(), source));
            }
            string[] Sources(Assembly assembly, string directory)
            {
                var expectedPaths = files.Where(path => path.StartsWith(directory + "/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
                    .Select(path => Path.GetFullPath(Path.Combine(root, path))).ToArray();
                var expectedSet = new HashSet<string>(expectedPaths, StringComparer.OrdinalIgnoreCase);
                var installedSources = installedFiles.Where(path => path.StartsWith(directory + "/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
                    .Select(path => Path.GetFullPath(Path.Combine(root, path))).ToArray();
                var actual = assembly.sourceFiles.Select(Physical).ToArray();
                if (expectedPaths.Length == 0 || expectedSet.Count != expectedPaths.Length || !expectedSet.SetEquals(installedSources) || actual.Length != expectedPaths.Length || !expectedSet.SetEquals(actual) ||
                    actual.Any(path => !Inside(path, Path.Combine(root, directory)) || !File.Exists(path)))
                {
                    throw new InvalidOperationException("A required main assembly lost, duplicated or changed its complete installed source set: " + assembly.name +
                        "; expected=" + expectedPaths.Length + "; installed=" + installedSources.Length + "; actual=" + actual.Length +
                        "; missing=" + string.Join(",", expectedSet.Except(actual, StringComparer.OrdinalIgnoreCase)) +
                        "; extra=" + string.Join(",", actual.Except(expectedSet, StringComparer.OrdinalIgnoreCase)));
                }
                return actual;
            }
            if (player.Any(assembly => assembly.name == "YLFramework.Editor" || assembly.sourceFiles.Any(source => Inside(Physical(source), Path.Combine(root, "Editor")))))
            {
                throw new InvalidOperationException("The main Editor assembly or its installed sources entered the Player graph.");
            }
            var result = new JArray();
            foreach (string directory in new[] { "Runtime", "Editor" })
            {
                string name = "YLFramework." + directory;
                var assembly = editor.Single(item => item.name == name);
                var sources = Sources(assembly, directory);
                var playerAssembly = directory == "Runtime" ? player.Single(item => item.name == name) : null;
                var playerSources = playerAssembly == null ? Array.Empty<string>() : Sources(playerAssembly, directory);
                var loaded = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(item => item.GetName().Name == name);
                string output = Path.Combine(Project(), "Library", "ScriptAssemblies", name + ".dll");
                if (!File.Exists(assembly.outputPath) || !string.Equals(Path.GetFullPath(assembly.outputPath), output, StringComparison.OrdinalIgnoreCase) ||
                    loaded == null || loaded.IsDynamic || !string.Equals(Path.GetFullPath(loaded.Location), output, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The required main assembly is not actually loaded from its compiled host output: " + name);
                }
                int loadedTypeCount = loaded.GetTypes().Length;
                if (loadedTypeCount == 0) { throw new InvalidOperationException("The required main assembly has no loaded types: " + name); }
                result.Add(JObject.FromObject(new { name, assembly.sourceFiles, resolvedSources = sources, assembly.outputPath, loadedPath = loaded.Location, loadedTypeCount,
                    playerSourceFiles = playerAssembly?.sourceFiles ?? Array.Empty<string>(), playerResolvedSources = playerSources }));
            }
            return result;
        }

        private static string Text(JObject value, string field)
        {
            if (value?[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value[field]))
            {
                throw new InvalidOperationException("Distribution string identity is invalid: " + field);
            }
            return (string)value[field];
        }

        private static bool Inside(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string Project() => Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
        private static bool Artifact(JObject request) => (string)request["packageOrigin"] == "Registry" || (string)request["packageOrigin"] == "LocalTarball";

        private static JObject ReadJson(string path)
        {
            string text = File.ReadAllText(path);
            // Json.NET accepts non-JSON syntax that must not disappear during semantic hashing.
            const string tokens = @"""(?:[^""\\\x00-\x1f]|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|true|false|null|[{}\[\]:,]|[ \t\r\n]+";
            int offset = 0; string previous = ""; bool previousAtom = false;
            foreach (Match match in Regex.Matches(text, tokens))
            {
                if (match.Index != offset) { throw new InvalidOperationException("Non-JSON manifest syntax is refused."); }
                offset += match.Length;
                string token = match.Value;
                if (string.IsNullOrWhiteSpace(token)) { continue; }
                bool atom = token[0] == '"' || token[0] == '-' || char.IsDigit(token[0]) || token == "true" || token == "false" || token == "null";
                if (((token == "}" || token == "]") && previous == ",") || (token == ":" && !previous.StartsWith("\"", StringComparison.Ordinal)) || (atom && previousAtom))
                {
                    throw new InvalidOperationException("Non-JSON manifest separators are refused.");
                }
                previous = token; previousAtom = atom;
            }
            if (offset != text.Length) { throw new InvalidOperationException("Non-JSON manifest syntax is refused."); }
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) { throw new InvalidOperationException("Unexpected trailing distribution JSON content."); }
            return value;
        }

        private static JObject CacheEnvironment() => JObject.FromObject(new
        {
            UPM_CACHE_ROOT = Environment.GetEnvironmentVariable("UPM_CACHE_ROOT"),
            UPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_CACHE_PATH"),
            UPM_NPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_NPM_CACHE_PATH")
        });

        private static void RejectLinks(string path)
        {
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Linked distribution input/output is refused.");
                }
            }
        }

        private static void ValidateInvocation(JObject request)
        {
            foreach (var field in new[] { "hostId", "resultId", "resultRoot", "output", "unityVersion", "phase", "packageOrigin", "backend", "manifestSha256" })
            {
                Text(request, field);
            }
            if (request["schemaVersion"]?.Type != JTokenType.Integer || (long)request["schemaVersion"] != 2 ||
                request["smoke"]?.Type != JTokenType.Boolean || request["hybridCLR"]?.Type != JTokenType.Boolean ||
                request["packages"] is not JArray selected || selected.Count == 0 ||
                !new[] { "Inspect", "Build", "HybridConfigure", "HybridBuild" }.Contains(Text(request, "phase")) ||
                !new[] { "File", "GitFixture", "GitRemote", "Registry", "LocalTarball" }.Contains(Text(request, "packageOrigin")) ||
                !new[] { "Mono", "IL2CPP" }.Contains(Text(request, "backend")))
            {
                throw new InvalidOperationException("Distribution requires a typed v2 UPM request.");
            }
            if (new[] { "GitFixture", "GitRemote" }.Contains(Text(request, "packageOrigin")) && !Regex.IsMatch(Text(request, "gitRevision"), "^[0-9a-f]{40}$"))
            {
                throw new InvalidOperationException("Distribution Git revision must be a full commit.");
            }
            if (Artifact(request)) { ValidateArtifactRequest(request); }
            if (Artifact(request) && Text(request, "phase").StartsWith("Hybrid", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Artifact verification does not install native HybridCLR; use the separate File/Git native workflow.");
            }
            foreach (var token in selected)
            {
                if (token is not JObject package)
                {
                    throw new InvalidOperationException("Distribution package requests must be objects.");
                }
                Text(package, "name");
                Text(package, "version");
                foreach (var field in new[] { "root", "contentSha256" })
                {
                    if (package[field] != null && package[field].Type != JTokenType.Null && package[field].Type != JTokenType.String)
                    {
                        throw new InvalidOperationException("Distribution package input identity has an invalid type: " + field);
                    }
                }
            }
            if (!Regex.IsMatch(Text(request, "manifestSha256"), "^[0-9a-fA-F]{64}$") ||
                (Text(request, "phase").StartsWith("Hybrid", StringComparison.Ordinal) &&
                    (!(bool)request["hybridCLR"] || !(bool)request["smoke"] || Text(request, "backend") != "IL2CPP")))
            {
                throw new InvalidOperationException("Invalid manifest identity or explicit HybridCLR mode.");
            }
            var project = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
            var workspace = Directory.GetParent(project)?.Parent?.FullName;
            var resultRoot = Path.GetFullPath(Text(request, "resultRoot"));
            var output = Path.GetFullPath(Text(request, "output"));
            var owner = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            var resultOwner = JObject.Parse(File.ReadAllText(Path.Combine(resultRoot, ".yl-owner.json")));
            foreach (var record in new[] { owner, resultOwner })
            {
                foreach (var field in new[] { "purpose", "kind", "path", "id", "workspaceId", "key" })
                {
                    Text(record, field);
                }
                if (record["schemaVersion"]?.Type != JTokenType.Integer || (long)record["schemaVersion"] != 1 ||
                    Text(record, "purpose") != "YLTesting.Directory" || !Regex.IsMatch(Text(record, "id"), "^[0-9a-f]{32}$") ||
                    !Regex.IsMatch(Text(record, "key"), "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$"))
                {
                    throw new InvalidOperationException("Invalid distribution directory owner.");
                }
            }
            if (workspace == null)
            {
                throw new InvalidOperationException("The distribution host has no owned workspace.");
            }
            var workspaceOwner = JObject.Parse(File.ReadAllText(Path.Combine(workspace, ".yl-workspace.json")));
            if (!Application.isBatchMode || Text(workspaceOwner, "purpose") != "YLTesting.Workspace" ||
                workspaceOwner["schemaVersion"]?.Type != JTokenType.Integer || (long)workspaceOwner["schemaVersion"] != 1 ||
                Text(workspaceOwner, "root") != workspace || Text(workspaceOwner, "id") != Text(owner, "workspaceId") ||
                Text(owner, "kind") != "Host" || Text(owner, "path") != project || Text(owner, "id") != Text(request, "hostId") ||
                project != Path.Combine(workspace, "Hosts", Text(owner, "key")) ||
                Text(resultOwner, "kind") != "Result" || Text(resultOwner, "path") != resultRoot || Text(resultOwner, "id") != Text(request, "resultId") ||
                Text(resultOwner, "workspaceId") != Text(owner, "workspaceId") ||
                !Regex.IsMatch(Path.GetFileName(resultRoot), "^[0-9a-f]{32}$") ||
                resultRoot != Path.Combine(workspace, "Results", Text(resultOwner, "key"), Path.GetFileName(resultRoot)) ||
                !Inside(output, resultRoot) || !Directory.Exists(output) || File.Exists(Path.Combine(output, "report.json")) ||
                Application.unityVersion != Text(request, "unityVersion") || EditorUtility.scriptCompilationFailed || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("The distribution host, result, version or compilation is not valid.");
            }
            RejectLinks(output);
            RejectLinks(project);
            if (Artifact(request))
            {
                var cache = Path.GetFullPath(Text(request, "cacheRoot"));
                var cacheOwner = ReadJson(Path.Combine(cache, ".yl-owner.json"));
                if (cacheOwner["schemaVersion"]?.Type != JTokenType.Integer || (long)cacheOwner["schemaVersion"] != 1 ||
                    Text(cacheOwner, "purpose") != "YLTesting.Directory" || Text(cacheOwner, "kind") != "Cache" ||
                    Text(cacheOwner, "workspaceId") != Text(owner, "workspaceId") || Text(cacheOwner, "path") != cache ||
                    !Regex.IsMatch(Text(cacheOwner, "id"), @"\A[0-9a-f]{32}\z") || !Regex.IsMatch(Text(cacheOwner, "key"), @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z") ||
                    cache != Path.Combine(workspace, "Cache", Text(cacheOwner, "key")))
                {
                    throw new InvalidOperationException("The artifact cache is not owned by this distribution workspace.");
                }
                RejectLinks(cache);
            }
            if (!string.Equals(HashFile(Path.Combine(project, "Packages/manifest.json")), Text(request, "manifestSha256"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The owned host package manifest identity changed.");
            }
        }

        private static IEnumerable<string> PackageFiles(string root)
        {
            RejectLinks(root);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("Linked package content is refused.");
                    }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); }
                    else { yield return path; }
                }
            }
        }

        private static string HashFile(string path)
        {
            using var hash = SHA256.Create();
            using var stream = File.OpenRead(path);
            return string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static JToken CanonicalToken(JToken token)
        {
            if (token is JObject obj)
            {
                return new JObject(obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(property.Name, CanonicalToken(property.Value))));
            }
            return token is JArray array ? new JArray(array.Select(CanonicalToken)) : token.DeepClone();
        }

        private static string ContentHash(string root, bool allowManagedFingerprint, JObject expected = null, bool localTarball = false)
        {
            if (!File.Exists(Path.Combine(root, "LICENSE.md")) || new FileInfo(Path.Combine(root, "LICENSE.md")).Length == 0)
            {
                throw new InvalidOperationException("Package LICENSE.md is required.");
            }
            var content = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in PackageFiles(root))
            {
                var relative = file.Substring(root.Length + 1).Replace('\\', '/');
                if (Regex.IsMatch(relative, @"^(?:Assets|Packages|ProjectSettings|Library|Temp|obj|bin|Tests|Tools|Settings|\.git|\.kilo|\.kilocode|\.opencode)(?:/|$)|(?:^|/)(?:AGENTS\.md|\.yl-[^/]+|[^/]+\.(?:csproj|sln|slnx))$", RegexOptions.IgnoreCase))
                {
                    throw new InvalidOperationException("Unauthorized product package content: " + relative);
                }
                if (expected != null && (Regex.IsMatch(relative, @"(?:^|/)(?:DOTween[^/]*\.dll|HybridCLR[^/]*\.dll|dnlib\.dll|Demigiant)(?:/|$)", RegexOptions.IgnoreCase) ||
                    (relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && new[] { "com.kano.ylframework.dotween", "com.kano.ylframework.hybridclr" }.Contains(Text(expected, "name")))))
                {
                    throw new InvalidOperationException("An external SDK was bundled in an owned artifact.");
                }
                using (var stream = File.OpenRead(file))
                {
                    var prefix = new byte[200];
                    var length = stream.Read(prefix, 0, prefix.Length);
                    if (Encoding.UTF8.GetString(prefix, 0, length).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal) ||
                        (relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && (length < 2 || prefix[0] != 77 || prefix[1] != 90)))
                    {
                        throw new InvalidOperationException("Unmaterialized LFS or invalid package binary: " + relative);
                    }
                }
                var digest = HashFile(file);
                if (relative == "package.json")
                {
                    var manifest = ReadJson(file);
                    if (manifest.Properties().Any(property => property.Name.StartsWith("_", StringComparison.Ordinal) && property.Name != "_fingerprint"))
                    {
                        throw new InvalidOperationException("Unknown package-manager manifest metadata.");
                    }
                    var fingerprint = manifest.Property("_fingerprint");
                    if (fingerprint != null)
                    {
                        if (!allowManagedFingerprint || fingerprint.Value.Type != JTokenType.String ||
                            !Regex.IsMatch((string)fingerprint.Value, localTarball ? @"\A[0-9a-fA-F]{32}\z" : @"\A[0-9a-fA-F]{40}\z"))
                        {
                            throw new InvalidOperationException("Untrusted package-manager fingerprint metadata.");
                        }
                        fingerprint.Remove();
                    }
                    using var manifestHash = SHA256.Create();
                    digest = string.Concat(manifestHash.ComputeHash(Encoding.UTF8.GetBytes(CanonicalToken(manifest).ToString(Formatting.None)))
                        .Select(value => value.ToString("x2")));
                }
                content.Add(relative, digest);
            }
            if (expected != null)
            {
                var files = ((JArray)expected["files"]).Cast<JObject>().ToArray();
                if (files.Length != content.Count || files.Any(file => !content.TryGetValue(Text(file, "path"), out var digest) ||
                    !string.Equals(digest, Text(file, "sha256"), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("The installed artifact file set/semantic digests differ from the frozen release.");
                }
            }
            var text = new StringBuilder();
            foreach (var pair in content) { text.Append(pair.Key).Append('\0').Append(pair.Value).Append('\n'); }
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())).Select(value => value.ToString("x2")));
        }

        private static JArray CapturePackages(JObject request)
        {
            if (Artifact(request)) { return CaptureArtifactPackages(request); }
            var all = PackageInfo.GetAllRegisteredPackages();
            var selected = (JArray)request["packages"];
            var names = selected.Cast<JObject>().Select(package => Text(package, "name")).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                all.Any(package => package.errors.Length != 0 || (!package.name.StartsWith("com.unity.", StringComparison.Ordinal) &&
                    !names.Contains(package.name) && !(package.name == "com.code-philosophy.hybridclr" && (bool)request["hybridCLR"]))))
            {
                throw new InvalidOperationException("The installed package graph has errors or unauthorized host dependencies.");
            }
            var result = new JArray();
            var hostManifest = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages/manifest.json")));
            foreach (var expected in selected.Cast<JObject>())
            {
                var name = Text(expected, "name");
                if (!new[] { "com.kano.ylframework", "com.kano.ylframework.dotween", "com.kano.ylframework.excel", "com.kano.ylframework.domain-excel", "com.kano.ylframework.hybridclr" }.Contains(name) ||
                    Text(expected, "version") != "0.1.0-preview.1")
                {
                    throw new InvalidOperationException("Unknown distribution package identity.");
                }
                var package = all.Single(item => item.name == name);
                bool local = Text(request, "packageOrigin") == "File";
                if (package.version != Text(expected, "version") || package.assetPath != "Packages/" + name ||
                    package.source != (local ? PackageSource.Local : PackageSource.Git) ||
                    (!local && ((!string.IsNullOrEmpty(package.git?.hash) && package.git.hash != Text(request, "gitRevision")) ||
                        (!string.IsNullOrEmpty(package.git?.revision) && package.git.revision != Text(request, "gitRevision")))))
                {
                    throw new InvalidOperationException("The actual installed package source/version/revision does not match: " + name +
                        "; version=" + package.version + "; source=" + package.source + "; assetPath=" + package.assetPath +
                        "; hash=" + package.git?.hash + "; revision=" + package.git?.revision + "; expected=" + (string)request["gitRevision"]);
                }
                var root = Path.GetFullPath(package.resolvedPath).TrimEnd('\\', '/');
                var manifest = ReadJson(Path.Combine(root, "package.json"));
                if (Text(manifest, "name") != name || Text(manifest, "version") != package.version)
                {
                    throw new InvalidOperationException("The actual installed package manifest differs from PackageInfo.");
                }
                foreach (var dependency in ((JObject)manifest["dependencies"]).Properties())
                {
                    if (dependency.Value.Type != JTokenType.String || (dependency.Name.StartsWith("com.kano.", StringComparison.Ordinal) &&
                        (!names.Contains(dependency.Name) || (string)dependency.Value != package.version)))
                    {
                        throw new InvalidOperationException("The installed internal package closure is incomplete.");
                    }
                }
                var locator = Text((JObject)hostManifest["dependencies"], name);
                var requestedLocator = local ? "file:" + Text(expected, "root").Replace('\\', '/') :
                    Text(request, "gitRepository") + "?path=/PackageSources/" + name + "#" + Text(request, "gitRevision");
                if (locator != requestedLocator || (local && !string.Equals(root, Text(expected, "root"), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("The actual installed package locator differs from the owned request.");
                }
                var idPrefix = name + "@";
                var apiLocator = package.packageId != null && package.packageId.StartsWith(idPrefix, StringComparison.Ordinal)
                    ? package.packageId.Substring(idPrefix.Length).Replace('\\', '/') : null;
                var expectedLocator = requestedLocator.Replace('\\', '/');
                if (!local)
                {
                    if (apiLocator != null && apiLocator.StartsWith("git+", StringComparison.Ordinal)) apiLocator = apiLocator.Substring(4);
                    if (expectedLocator.StartsWith("git+", StringComparison.Ordinal)) expectedLocator = expectedLocator.Substring(4);
                }
                if (!string.Equals(apiLocator, expectedLocator, local && Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The installed packageId contradicts the requested source: " + package.packageId + "; expected=" + idPrefix + requestedLocator);
                }
                string resolvedRevision = null;
                if (!local)
                {
                    // Unity 2022 can omit PackageInfo.git after a cached Editor restart; the generated lock remains authoritative.
                    var locked = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages/packages-lock.json")))["dependencies"]?[name] as JObject;
                    if (locked == null || Text(locked, "source") != "git" || Text(locked, "version") != requestedLocator ||
                        Text(locked, "hash") != Text(request, "gitRevision"))
                    {
                        throw new InvalidOperationException("The actual UPM lock does not bind the requested Git source and complete revision.");
                    }
                    resolvedRevision = Text(locked, "hash");
                }
                var digest = ContentHash(root, package.source == PackageSource.Git);
                if (expected["contentSha256"]?.Type == JTokenType.String && Text(expected, "contentSha256") != digest)
                {
                    throw new InvalidOperationException("Installed package content differs from the exact input bytes.");
                }
                var row = PackageRow(package); row["resolvedPath"] = root; row["contentSha256"] = digest; row["revision"] = resolvedRevision;
                result.Add(row);
            }
            return result;
        }

        private static JObject Dependencies(PackageInfo package)
        {
            var result = new JObject();
            foreach (var dependency in package.dependencies.OrderBy(item => item.name, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(dependency.name) || string.IsNullOrWhiteSpace(dependency.version) || result.Property(dependency.name) != null)
                {
                    throw new InvalidOperationException("Invalid or duplicate PackageInfo dependency: " + package.name);
                }
                result.Add(dependency.name, dependency.version);
            }
            return result;
        }

        private static JObject PackageRow(PackageInfo package)
        {
            string fingerprint = null;
            if (!string.IsNullOrEmpty(package.resolvedPath) && File.Exists(Path.Combine(package.resolvedPath, "package.json")))
            {
                var value = ReadJson(Path.Combine(package.resolvedPath, "package.json")).Property("_fingerprint");
                if (value != null)
                {
                    if ((!new[] { PackageSource.Git, PackageSource.Registry, PackageSource.LocalTarball }.Contains(package.source) &&
                            !(package.source == PackageSource.BuiltIn && package.name?.StartsWith("com.unity.", StringComparison.Ordinal) == true)) ||
                        value.Value.Type != JTokenType.String || !Regex.IsMatch((string)value.Value,
                            package.source == PackageSource.LocalTarball ? @"\A[0-9a-fA-F]{32}\z" : @"\A[0-9a-fA-F]{40}\z"))
                    {
                        throw new InvalidOperationException("Untrusted package-manager fingerprint metadata.");
                    }
                    fingerprint = (string)value.Value;
                }
            }
            return JObject.FromObject(new { package.name, package.version, source = package.source.ToString(), package.assetPath, package.packageId,
                package.resolvedPath, package.isDirectDependency, registryUrl = package.registry?.url, dependencies = Dependencies(package), fingerprint,
                revision = package.source == PackageSource.Git ? package.git?.hash : null, apiHash = package.git?.hash, apiRevision = package.git?.revision });
        }

        private static Dictionary<string, int> ArtifactDepths(JObject request)
        {
            var selected = ((JArray)request["packages"]).Cast<JObject>().ToDictionary(package => Text(package, "name"), StringComparer.Ordinal);
            string entry = Text(request, "entryPackage");
            if (!selected.ContainsKey(entry)) { throw new InvalidOperationException("The artifact entry is not in the expected closure."); }
            var depths = new Dictionary<string, int>(StringComparer.Ordinal) { [entry] = 0 };
            var pending = new Queue<string>(); pending.Enqueue(entry);
            while (pending.Count != 0)
            {
                string name = pending.Dequeue();
                foreach (var dependency in ((JObject)selected[name]["dependencies"]).Properties())
                {
                    if (!dependency.Name.StartsWith("com.kano.", StringComparison.Ordinal)) { continue; }
                    if (!selected.ContainsKey(dependency.Name) || Text(selected[dependency.Name], "version") != (string)dependency.Value)
                    {
                        throw new InvalidOperationException("The frozen own-package closure is incomplete.");
                    }
                    if (!depths.ContainsKey(dependency.Name)) { depths.Add(dependency.Name, depths[name] + 1); pending.Enqueue(dependency.Name); }
                }
            }
            if (depths.Count != selected.Count) { throw new InvalidOperationException("The expected artifacts include packages outside the entry closure."); }
            return depths;
        }

        private static void ValidateArtifactRequest(JObject request)
        {
            bool registry = Text(request, "packageOrigin") == "Registry";
            Text(request, "cacheRoot");
            if (!Regex.IsMatch(Text(request, "releaseManifestSha256"), @"\A[0-9a-fA-F]{64}\z") ||
                new[] { "gitRepository", "gitRevision" }.Any(field => request[field] != null && request[field].Type != JTokenType.Null))
            {
                throw new InvalidOperationException("Artifact requests require a release identity and refuse Git parameters.");
            }
            if (registry)
            {
                string url = Text(request, "registryUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() || uri.Scheme != "https" ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || Regex.IsMatch(url, @"[\s\\]") ||
                    !Regex.IsMatch(Text(request, "registryVersion"), @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z"))
                {
                    throw new InvalidOperationException("Registry requires an exact version and HTTPS registry URL.");
                }
            }
            else if (new[] { "registryUrl", "registryVersion" }.Any(field => request[field] != null && request[field].Type != JTokenType.Null))
            {
                throw new InvalidOperationException("LocalTarball refuses Registry request parameters.");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in (JArray)request["packages"])
            {
                if (token is not JObject package || !names.Add(Text(package, "name")) ||
                    !new[] { "com.kano.ylframework", "com.kano.ylframework.excel", "com.kano.ylframework.domain-excel", "com.kano.ylframework.dotween", "com.kano.ylframework.hybridclr" }.Contains(Text(package, "name")) ||
                    package["root"]?.Type != JTokenType.Null || !Regex.IsMatch(Text(package, "contentSha256"), @"\A[0-9a-fA-F]{64}\z") ||
                    package["dependencies"] is not JObject dependencies || dependencies.Properties().Any(property => property.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)property.Value)) ||
                    package["files"] is not JArray files || files.Count == 0 || (registry && Text(package, "version") != Text(request, "registryVersion")))
                {
                    throw new InvalidOperationException("Invalid artifact expected package identity.");
                }
                string locator = Text(package, "locator");
                if (registry ? locator != Text(package, "version") :
                    !locator.StartsWith("file:", StringComparison.Ordinal) || !Path.IsPathFullyQualified(locator.Substring(5)) ||
                    !locator.EndsWith(".tgz", StringComparison.Ordinal) || locator.Contains('\\') || !File.Exists(locator.Substring(5)))
                {
                    throw new InvalidOperationException("Invalid exact Registry/absolute tarball locator.");
                }
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fileToken in files)
                {
                    if (fileToken is not JObject file || !Regex.IsMatch(Text(file, "sha256"), @"\A[0-9a-fA-F]{64}\z") ||
                        Path.IsPathRooted(Text(file, "path")) || Regex.IsMatch(Text(file, "path"), @"(?:^|/)\.\.?(?:/|$)|[\\:*?<>|""]") || !paths.Add(Text(file, "path")))
                    {
                        throw new InvalidOperationException("Invalid or duplicate artifact expected file identity.");
                    }
                }
            }
            ArtifactDepths(request);
        }

        private static JArray CaptureArtifactPackages(JObject request)
        {
            ValidateArtifactRequest(request);
            bool registry = Text(request, "packageOrigin") == "Registry";
            string samples = Path.Combine(Project(), "Assets", "Samples");
            if (Directory.Exists(Path.Combine(Project(), "PackageSources")) || Directory.Exists(Path.Combine(Project(), "Source")) ||
                (Directory.Exists(samples) && PackageFiles(samples).Any())) { throw new InvalidOperationException("Artifact hosts refuse copied sources and default-imported Samples."); }
            var installed = PackageInfo.GetAllRegisteredPackages();
            var selected = ((JArray)request["packages"]).Cast<JObject>().ToArray();
            var names = selected.Select(package => Text(package, "name")).ToArray();
            bool hybrid = names.Contains("com.kano.ylframework.hybridclr");
            if (installed.Select(package => package.name).Distinct(StringComparer.Ordinal).Count() != installed.Length ||
                installed.Any(package => package.errors.Length != 0 || (!names.Contains(package.name) &&
                    !(hybrid && package.name == "com.code-philosophy.hybridclr") && !package.name.StartsWith("com.unity.", StringComparison.Ordinal)) ||
                    (package.name.StartsWith("com.unity.", StringComparison.Ordinal) && package.source != PackageSource.Registry && package.source != PackageSource.BuiltIn)))
            {
                throw new InvalidOperationException("The artifact consumer has an unauthorized or invalid registered package graph.");
            }
            var declared = ReadJson(Path.Combine(Project(), "Packages/manifest.json"))["dependencies"] as JObject;
            var direct = selected.Where(package => !registry || Text(package, "name") == Text(request, "entryPackage")).ToArray();
            if (declared == null || declared.Count != direct.Length || direct.Any(package => (string)declared[Text(package, "name")] != Text(package, "locator")))
            {
                throw new InvalidOperationException("The host manifest prefilled or omitted dependencies outside the exact artifact entry declarations.");
            }
            var locked = ReadJson(Path.Combine(Project(), "Packages/packages-lock.json"));
            var depths = ArtifactDepths(request);
            var result = new JArray();
            foreach (var expected in selected)
            {
                string name = Text(expected, "name");
                var package = installed.SingleOrDefault(item => item.name == name);
                if (package == null) { throw new InvalidOperationException("Missing registered artifact: " + name); }
                string root = Path.GetFullPath(package.resolvedPath).TrimEnd('\\', '/');
                string locator = Text(expected, "locator");
                int depth = registry ? depths[name] : 0;
                var entry = locked["dependencies"]?[name] as JObject;
                if (package.source != (registry ? PackageSource.Registry : PackageSource.LocalTarball) || package.version != Text(expected, "version") ||
                    package.assetPath != "Packages/" + name || (registry ? package.packageId : package.packageId?.Replace('\\', '/')) != name + "@" + locator || package.isDirectDependency != (depth == 0) ||
                    (registry && package.registry?.url != Text(request, "registryUrl")) ||
                    (!Inside(root, Path.Combine(Project(), "Library", "PackageCache")) && !Inside(root, Path.GetFullPath(Text(request, "cacheRoot")))) ||
                    entry == null || Text(entry, "source") != (registry ? "registry" : "local-tarball") || Text(entry, "version") != locator ||
                    entry["depth"]?.Type != JTokenType.Integer || (long)entry["depth"] != depth || (registry && Text(entry, "url") != Text(request, "registryUrl")))
                {
                    throw new InvalidOperationException("Artifact PackageInfo/source/directness/cache/generated lock identity mismatch: " + name);
                }
                RejectLinks(root);
                var payload = ReadJson(Path.Combine(root, "package.json"));
                var dependencies = (JObject)expected["dependencies"];
                if (Text(payload, "name") != name || Text(payload, "version") != package.version || payload["dependencies"] is not JObject || entry["dependencies"] is not JObject ||
                    !JToken.DeepEquals(CanonicalToken(dependencies), CanonicalToken(payload["dependencies"])) ||
                    !JToken.DeepEquals(CanonicalToken(dependencies), CanonicalToken(entry["dependencies"])) ||
                    !JToken.DeepEquals(CanonicalToken(dependencies), CanonicalToken(Dependencies(package))))
                {
                    throw new InvalidOperationException("Frozen/package/API/lock dependency declarations disagree: " + name);
                }
                string digest = ContentHash(root, true, expected, !registry);
                if (!string.Equals(digest, Text(expected, "contentSha256"), StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Installed artifact complete semantic digest mismatch."); }
                var row = PackageRow(package); row["resolvedPath"] = root; row["contentSha256"] = digest; row["verifiedFiles"] = ((JArray)expected["files"]).Count; result.Add(row);
            }
            foreach (var package in installed)
            {
                foreach (var dependency in package.dependencies)
                {
                    var target = installed.SingleOrDefault(item => item.name == dependency.name);
                    if (target == null || (!dependency.name.StartsWith("com.unity.", StringComparison.Ordinal) && target.version != dependency.version))
                    {
                        throw new InvalidOperationException("The observed PackageInfo graph has a missing or mismatched dependency.");
                    }
                }
            }
            if (hybrid) { CaptureRegistrySdk(request); }
            return result;
        }

        private static JObject CaptureRegistrySdk(JObject request)
        {
            var expected = request["sdk"] as JObject;
            if (Text(expected, "source") != "Registry" || Text(expected, "version") != "9.0.3" || Text(expected, "registryUrl") != "https://package.openupm.com" ||
                Text(expected, "repositoryRevision") != "6cadd24d101f4cbd00cd80190106f6621132d24d")
            {
                throw new InvalidOperationException("HybridCLR requires the approved Registry SDK metadata, not an actual Git revision assertion.");
            }
            var sdk = PackageInfo.GetAllRegisteredPackages().SingleOrDefault(package => package.name == "com.code-philosophy.hybridclr");
            var locked = ReadJson(Path.Combine(Project(), "Packages/packages-lock.json"))["dependencies"]?["com.code-philosophy.hybridclr"] as JObject;
            if (sdk == null || sdk.source != PackageSource.Registry || sdk.version != "9.0.3" || sdk.packageId != sdk.name + "@9.0.3" || sdk.isDirectDependency ||
                sdk.registry?.url != Text(expected, "registryUrl") || sdk.assetPath != "Packages/" + sdk.name ||
                (!Inside(Path.GetFullPath(sdk.resolvedPath), Path.Combine(Project(), "Library", "PackageCache")) && !Inside(Path.GetFullPath(sdk.resolvedPath), Path.GetFullPath(Text(request, "cacheRoot")))) ||
                ReadJson(Path.Combine(Project(), "Packages/manifest.json"))["dependencies"]?[sdk.name] != null ||
                locked == null || Text(locked, "source") != "registry" || Text(locked, "version") != sdk.version || Text(locked, "url") != Text(expected, "registryUrl") ||
                locked["depth"]?.Type != JTokenType.Integer || (long)locked["depth"] != (Text(request, "packageOrigin") == "Registry" ? ArtifactDepths(request)["com.kano.ylframework.hybridclr"] + 1 : 1))
            {
                throw new InvalidOperationException("The actual HybridCLR SDK is not the approved indirect Registry installation.");
            }
            RejectLinks(sdk.resolvedPath);
            var payload = ReadJson(Path.Combine(sdk.resolvedPath, "package.json"));
            var dependencies = payload["dependencies"] == null ? new JObject() : payload["dependencies"] as JObject;
            if (Text(payload, "name") != sdk.name || Text(payload, "version") != sdk.version || dependencies == null || locked["dependencies"] is not JObject ||
                payload.Properties().Any(property => property.Name.StartsWith("_", StringComparison.Ordinal) && property.Name != "_fingerprint") ||
                !JToken.DeepEquals(CanonicalToken(dependencies), CanonicalToken(Dependencies(sdk))) || !JToken.DeepEquals(CanonicalToken(dependencies), CanonicalToken(locked["dependencies"])))
            {
                throw new InvalidOperationException("The actual HybridCLR SDK manifest/API/lock dependency content differs.");
            }
            var row = PackageRow(sdk); row["repositoryRevision"] = Text(expected, "repositoryRevision"); return row;
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
            var outcome = JObject.FromObject(result);
            bool observationsUnavailable = (string)outcome["status"] == "Failed";
            var json = JsonConvert.SerializeObject(new
            {
                schemaVersion = 2,
                hostId = (string)request["hostId"],
                resultId = (string)request["resultId"],
                unityVersion = Application.unityVersion,
                phase = (string)request["phase"],
                capturedUtc = DateTime.UtcNow.ToString("o"),
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                packageOrigin = Text(request, "packageOrigin"),
                entryPackage = (string)request["entryPackage"], registryUrl = (string)request["registryUrl"], registryVersion = (string)request["registryVersion"],
                releaseManifestSha256 = (string)request["releaseManifestSha256"],
                cacheEnvironment = Artifact(request) ? CacheEnvironment() : null,
                observationsUnavailable,
                actualSource = observationsUnavailable ? "Unavailable" : null,
                packages = observationsUnavailable ? new JArray() : CapturePackages(request),
                resolvedDependencies = observationsUnavailable ? new JArray() : JArray.FromObject(PackageInfo.GetAllRegisteredPackages().OrderBy(package => package.name, StringComparer.Ordinal).Select(PackageRow)),
                sdk = !observationsUnavailable && Artifact(request) && ((JArray)request["packages"]).Cast<JObject>().Any(package => Text(package, "name") == "com.kano.ylframework.hybridclr") ? CaptureRegistrySdk(request) : null,
                result = outcome
            }, Formatting.Indented);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(json);
        }
    }
}
#endif
