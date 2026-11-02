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
                    foreach (var source in runtime.sourceFiles)
                    {
                        var normalized = source.Replace('\\', '/');
                        var assetRoot = Text(main, "assetPath") + "/Runtime/";
                        var physicalRoot = Path.Combine(Text(main, "resolvedPath"), "Runtime");
                        if (!normalized.StartsWith(assetRoot, StringComparison.Ordinal) && !Inside(Path.GetFullPath(source), physicalRoot))
                        {
                            throw new InvalidOperationException("Runtime compiled from outside the actual installed package: " + source);
                        }
                    }
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

        private static string Text(JObject value, string field)
        {
            if (value?[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value[field]))
            {
                throw new InvalidOperationException("Distribution string identity is invalid: " + field);
            }
            return (string)value[field];
        }

        private static bool Inside(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
                !new[] { "File", "GitFixture", "GitRemote" }.Contains(Text(request, "packageOrigin")) ||
                !new[] { "Mono", "IL2CPP" }.Contains(Text(request, "backend")))
            {
                throw new InvalidOperationException("Distribution requires a typed v2 UPM request.");
            }
            if (Text(request, "packageOrigin") != "File" && !Regex.IsMatch(Text(request, "gitRevision"), "^[0-9a-f]{40}$"))
            {
                throw new InvalidOperationException("Distribution Git revision must be a full commit.");
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
                !Inside(output, resultRoot) || !Directory.Exists(output) ||
                Application.unityVersion != Text(request, "unityVersion") || EditorUtility.scriptCompilationFailed || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("The distribution host, result, version or compilation is not valid.");
            }
            RejectLinks(output);
            RejectLinks(project);
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

        private static string ContentHash(string root, bool allowManagedFingerprint)
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
                    using var reader = new JsonTextReader(new StringReader(File.ReadAllText(file))) { DateParseHandling = DateParseHandling.None };
                    var manifest = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidOperationException("Unexpected trailing package manifest content.");
                    var fingerprint = manifest.Property("_fingerprint");
                    if (fingerprint != null)
                    {
                        if (!allowManagedFingerprint || fingerprint.Value.Type != JTokenType.String ||
                            !Regex.IsMatch((string)fingerprint.Value, "^[0-9a-f]{40}$"))
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
            var text = new StringBuilder();
            foreach (var pair in content) { text.Append(pair.Key).Append('\0').Append(pair.Value).Append('\n'); }
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())).Select(value => value.ToString("x2")));
        }

        private static JArray CapturePackages(JObject request)
        {
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
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
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
                result.Add(new JObject { ["name"] = name, ["version"] = package.version, ["source"] = package.source.ToString(), ["assetPath"] = package.assetPath,
                    ["resolvedPath"] = root, ["contentSha256"] = digest, ["revision"] = resolvedRevision, ["packageId"] = package.packageId });
            }
            return result;
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
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(JsonConvert.SerializeObject(new
            {
                schemaVersion = 2,
                hostId = (string)request["hostId"],
                resultId = (string)request["resultId"],
                unityVersion = Application.unityVersion,
                phase = (string)request["phase"],
                capturedUtc = DateTime.UtcNow.ToString("o"),
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                packageOrigin = Text(request, "packageOrigin"),
                packages = CapturePackages(request),
                resolvedDependencies = PackageInfo.GetAllRegisteredPackages().OrderBy(package => package.name, StringComparer.Ordinal)
                    .Select(package => new { package.name, package.version, source = package.source.ToString() }).ToArray(),
                result
            }, Formatting.Indented));
        }
    }
}
#endif
