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
using UnityEngine;
using CompiledAssembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Testing
{
    /// <summary>Installation/compilation evidence only; never installs native code or builds a Player.</summary>
    public static class IntegrationEntry
    {
        public static void Execute()
        {
            JObject request = null;
            JObject report = null;
            bool ownsInvocation = false;
            try
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-ylIntegrationRequest");
                if (index < 0 || index + 1 >= args.Length) { throw new ArgumentException("An explicit integration request is required."); }
                request = ReadJson(args[index + 1]);
                ValidateInvocation(request);
                if (Path.GetFullPath(args[index + 1]) != Path.Combine(Text(request, "output"), "request.json"))
                {
                    throw new InvalidOperationException("The request must be inside its owned output.");
                }
                ownsInvocation = true;
                report = JObject.FromObject(new
                {
                    schemaVersion = 1, purpose = "YLTesting.IntegrationPackages", integration = Text(request, "integration"),
                    hostId = Text(request, "hostId"), resultId = Text(request, "resultId"), inputHash = Text(request, "inputHash"),
                    packageOrigin = Text(request, "packageOrigin"), expectedTemplateHash = Text(request, "expectedTemplateHash"),
                    unityVersion = Application.unityVersion, pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    isBatchMode = Application.isBatchMode, scriptCompilationFailed = EditorUtility.scriptCompilationFailed,
                    capturedUtc = DateTime.UtcNow.ToString("o"), status = "Failed", packages = new object[0], assemblies = new object[0],
                    nativeInstallation = "NotRun", playerBuild = "NotRun", playerRuntime = "NotRun", hotUpdatePlayer = "NotVerified",
                    scope = "Actual Unity Editor compilation and Player compilation graph only. Not a native or hot-update Player pass."
                });
                if (EditorUtility.scriptCompilationFailed || EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    throw new InvalidOperationException("Unity compilation/import has not completed successfully.");
                }
                var installed = PackageInfo.GetAllRegisteredPackages();
                var packages = CapturePackages(request);
                report["packages"] = packages;
                // Physical roots come only from the registered packages, never from the local expected template.
                var selected = ((JArray)request["packages"]).Cast<JObject>().Select(expected =>
                {
                    var value = (JObject)expected.DeepClone();
                    value["root"] = Text(packages.Cast<JObject>().Single(package => Text(package, "name") == Text(expected, "name")), "resolvedPath");
                    return value;
                }).ToArray();
                string integration = Text(request, "integration");
                var manifest = ReadJson(Path.Combine(Project(), "Packages/manifest.json"));
                report["resolvedDependencies"] = JArray.FromObject(installed.OrderBy(package => package.name, StringComparer.Ordinal)
                    .Select(package => new { package.name, package.version, source = package.source.ToString(), package.resolvedPath, revision = package.git?.hash }));
                var editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var player = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
                report["editorGraph"] = JArray.FromObject(editor.Select(Graph));
                report["playerGraph"] = JArray.FromObject(player.Select(Graph));
                foreach (var assembly in editor.Concat(player))
                {
                    if (Regex.IsMatch(assembly.name, "TestRunner|Tests|Acceptance|Mcp|ReflectorNet", RegexOptions.IgnoreCase) ||
                        References(assembly).Any(reference => Regex.IsMatch(reference, "TestRunner|nunit|Mcp|ReflectorNet", RegexOptions.IgnoreCase)) ||
                        assembly.sourceFiles.Any(source => source.Replace('\\', '/').Contains("/Samples~/") || source.Replace('\\', '/').StartsWith("Assets/Samples/", StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException("Tests, MCP or default-imported Samples entered the consumer compilation graph.");
                    }
                    foreach (string source in assembly.sourceFiles.Where(source => source.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal)))
                    {
                        string path = source.Replace('\\', '/');
                        if (path != "Assets/YLTestingIntegration/Editor/IntegrationEntry.cs" &&
                            !(integration == "DOTween" && path.StartsWith("Assets/Plugins/Demigiant/DOTween/", StringComparison.Ordinal)))
                        {
                            throw new InvalidOperationException("A product source was copied into Assets or an unknown source entered the host: " + source);
                        }
                    }
                }
                if (player.Any(assembly => Regex.IsMatch(assembly.name, "Excel|MiniExcel", RegexOptions.IgnoreCase) ||
                    References(assembly).Any(reference => Regex.IsMatch(reference, "Excel|MiniExcel", RegexOptions.IgnoreCase))))
                {
                    throw new InvalidOperationException("Excel or MiniExcel leaked into the actual Player assembly graph.");
                }
                var required = new JArray();
                foreach (var expected in ((JArray)request["assemblies"]).Cast<JObject>())
                {
                    string name = Text(expected, "name");
                    var assembly = editor.Single(item => item.name == name);
                    var package = selected.Single(item => Text(item, "name") == Text(expected, "package"));
                    string root = Text(package, "root");
                    string directory = Text(expected, "directory") + "/";
                    var sources = ((JArray)package["files"]).Cast<JObject>().Select(file => Text(file, "path"))
                        .Where(path => path.StartsWith(directory, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal)).ToArray();
                    var compiledSources = assembly.sourceFiles.Select(source => Physical(source, selected)).ToArray();
                    var expectedSources = new HashSet<string>(sources.Select(source => Path.GetFullPath(Path.Combine(root, source))), StringComparer.OrdinalIgnoreCase);
                    if (sources.Length == 0 || !expectedSources.SetEquals(compiledSources) ||
                        !File.Exists(assembly.outputPath) || !string.Equals(Path.GetFullPath(assembly.outputPath), Path.Combine(Project(), "Library", "ScriptAssemblies", name + ".dll"), StringComparison.OrdinalIgnoreCase) ||
                        player.Any(item => item.name == name) != (bool)expected["player"])
                    {
                        throw new InvalidOperationException("A required assembly was excluded, lost sources or compiled outside its installed package: " + name +
                            "; expected=" + sources.Length + "; actual=" + compiledSources.Length +
                            "; missing=" + string.Join(",", expectedSources.Except(compiledSources, StringComparer.OrdinalIgnoreCase)) +
                            "; extra=" + string.Join(",", compiledSources.Except(expectedSources, StringComparer.OrdinalIgnoreCase)) +
                            "; output=" + Path.GetFullPath(assembly.outputPath) + "; outputExists=" + File.Exists(assembly.outputPath) +
                            "; playerGraph=" + player.Any(item => item.name == name) + "; expectedPlayer=" + expected["player"]);
                    }
                    var loaded = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == name);
                    if (!string.Equals(Path.GetFullPath(loaded.Location), Path.GetFullPath(assembly.outputPath), StringComparison.OrdinalIgnoreCase) || loaded.GetTypes().Length == 0)
                    {
                        throw new InvalidOperationException("The required compiled assembly is not actually loaded with types: " + name);
                    }
                    required.Add(JObject.FromObject(new { name, assembly.sourceFiles, assembly.defines, assembly.outputPath,
                        references = References(assembly), assembly.compiledAssemblyReferences, loadedPath = loaded.Location,
                        loadedReferences = loaded.GetReferencedAssemblies().Select(reference => reference.FullName).ToArray(), sha256 = Hash(loaded.Location) }));
                }
                report["assemblies"] = required;
                string[] global = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone).Split(';');
                report["globalDefines"] = JArray.FromObject(global);
                if (global.Contains("YL_DOMAIN_EXCEL") || global.Contains("YL_HYBRIDCLR_9_0_3"))
                {
                    throw new InvalidOperationException("Package presence/version gates must not be replaced by global integration symbols.");
                }
                if (integration == "DOTween")
                {
                    var adapter = editor.Single(assembly => assembly.name == "YLFramework.Actions.DOTween");
                    string dll = adapter.compiledAssemblyReferences.Single(path => Path.GetFileName(path) == "DOTween.dll");
                    if (!global.Contains("DOTWEEN") || !adapter.defines.Contains("DOTWEEN") ||
                        !string.Equals(Path.GetFullPath(dll), Path.GetFullPath(Path.Combine(Project(), "Assets/Plugins/Demigiant/DOTween/DOTween.dll")), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Hash(dll), Text((JObject)request["sdk"], "assemblySha256"), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("DOTween must compile against the approved consumer SDK and the owned DOTWEEN setting.");
                    }
                    string modules = Path.Combine(Project(), "Assets/Plugins/Demigiant/DOTween/Modules");
                    var sdkSources = Files(modules).Where(path => path.EndsWith(".cs", StringComparison.Ordinal)).Select(Path.GetFullPath).ToArray();
                    var compiled = new HashSet<string>(editor.SelectMany(assembly => assembly.sourceFiles).Select(source => Physical(source, selected)), StringComparer.OrdinalIgnoreCase);
                    if (sdkSources.Length == 0 || sdkSources.Any(path => !compiled.Contains(path)))
                    {
                        throw new InvalidOperationException("The complete SDK Modules did not enter actual compilation: " +
                            string.Join(",", sdkSources.Where(path => !compiled.Contains(path))));
                    }
                    report["sdk"] = JObject.FromObject(new { version = Text((JObject)request["sdk"], "version"), dll, sha256 = Hash(dll), modules = sdkSources });
                }
                if (integration == "Excel" || integration == "DomainExcel")
                {
                    var excel = editor.Single(assembly => assembly.name == "YLFramework.Excel.Editor");
                    string dll = excel.compiledAssemblyReferences.Single(path => Path.GetFileName(path) == "MiniExcel.dll");
                    string root = Text(selected.Single(package => Text(package, "name") == "com.kano.ylframework.excel"), "root");
                    var backend = (JObject)ReadJson(Path.Combine(root, "Editor/Dependencies.json"))["backend"];
                    var expectedSdk = (JObject)request["sdk"];
                    if (Text(backend, "version") != "1.31.2" || !string.Equals(Hash(dll), Text(backend, "assemblySha256"), StringComparison.OrdinalIgnoreCase) ||
                        Text(backend, "version") != Text(expectedSdk, "version") || Text(backend, "assemblySha256") != Text(expectedSdk, "assemblySha256") ||
                        !string.Equals(Path.GetFullPath(dll), Path.GetFullPath(Path.Combine(root, "Editor/Plugins/MiniExcel.dll")), StringComparison.OrdinalIgnoreCase) ||
                        new[] { "licenseFile", "attributionFile" }.Any(field =>
                            !Inside(Path.GetFullPath(Path.Combine(root, "Editor", Text(backend, field))), Path.Combine(root, "Editor")) ||
                            !File.Exists(Path.Combine(root, "Editor", Text(backend, field))) || new FileInfo(Path.Combine(root, "Editor", Text(backend, field))).Length == 0))
                    {
                        throw new InvalidOperationException("The actually referenced MiniExcel binary/version/license is incorrect.");
                    }
                    report["sdk"] = JObject.FromObject(new { version = Text(backend, "version"), assembly = System.Reflection.AssemblyName.GetAssemblyName(dll).FullName,
                        productVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion, dll, sha256 = Hash(dll), license = Text(backend, "license") });
                }
                if (integration == "HybridCLR")
                {
                    var sdk = installed.Single(package => package.name == "com.code-philosophy.hybridclr");
                    var expected = (JObject)request["sdk"];
                    string revision = GitRevision(sdk, Text(expected, "declaration"), Text(expected, "revision"), "v9.0.3");
                    if (sdk.version != "9.0.3" ||
                        Text((JObject)manifest["dependencies"], sdk.name) != Text(expected, "declaration"))
                    {
                        throw new InvalidOperationException("The actual official HybridCLR SDK source/version/commit is incorrect.");
                    }
                    foreach (string name in new[] { "YLFramework.HybridCLR", "YLFramework.HybridCLR.Editor" })
                    {
                        var assembly = editor.Single(item => item.name == name);
                        if (!assembly.defines.Contains("YL_HYBRIDCLR_9_0_3") || !References(assembly).Contains("HybridCLR.Runtime"))
                        {
                            throw new InvalidOperationException("The real SDK version gate did not enable the HybridCLR adapter: " + name);
                        }
                    }
                    var adapter = editor.Single(assembly => assembly.name == "YLFramework.HybridCLR.Editor");
                    string dnlib = adapter.compiledAssemblyReferences.Single(path => Path.GetFileName(path) == "dnlib.dll");
                    if (!References(adapter).Contains("HybridCLR.Editor") || !Inside(Path.GetFullPath(dnlib), Path.GetFullPath(sdk.resolvedPath)))
                    {
                        throw new InvalidOperationException("HybridCLR/dnlib references must come from the separately installed official SDK.");
                    }
                    report["sdk"] = JObject.FromObject(new { sdk.name, sdk.version, source = sdk.source.ToString(), sdk.resolvedPath,
                        revision, apiRevision = sdk.git?.revision, apiHash = sdk.git?.hash, dnlib, dnlibSha256 = Hash(dnlib) });
                }
                report["status"] = "Completed";
                WriteReport(request, report);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                if (!ownsInvocation) { throw; }
                report["error"] = error.ToString();
                WriteReport(request, report);
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static JObject ReadJson(string path)
        {
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) { throw new InvalidOperationException("Unexpected trailing integration JSON content."); }
            return value;
        }

        private static void ValidateGitSource(JObject request)
        {
            string repository = Text(request, "gitRepository");
            string url = repository.StartsWith("git+", StringComparison.Ordinal) ? repository.Substring(4) : repository;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
                !new[] { "http", "https", "ssh" }.Contains(uri.Scheme) || string.IsNullOrWhiteSpace(uri.Host) || uri.AbsolutePath == "/" ||
                Regex.IsMatch(repository, @"[\s?#\\]") || Regex.IsMatch(Uri.UnescapeDataString(url), @"[\s?#\\]") ||
                (!string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme != "ssh" || uri.UserInfo.Contains(":"))) ||
                (!repository.StartsWith("git+", StringComparison.Ordinal) && !uri.AbsolutePath.EndsWith(".git", StringComparison.Ordinal)) ||
                !Regex.IsMatch(Text(request, "gitRevision"), @"\A[0-9a-f]{40}\z"))
            {
                throw new InvalidOperationException("GitRemote requires an HTTP/HTTPS/SSH repository URL without query/fragment and a full 40hex revision.");
            }
        }

        private static string GitRevision(PackageInfo package, string locator, string revision, string declaredRevision)
        {
            string apiLocator = package.packageId;
            string prefix = package.name + "@";
            string expectedLocator = prefix + (locator.StartsWith("git+", StringComparison.Ordinal) ? locator.Substring(4) : locator);
            if (apiLocator != null && apiLocator.StartsWith(prefix + "git+", StringComparison.Ordinal)) { apiLocator = prefix + apiLocator.Substring(prefix.Length + 4); }
            var locked = ReadJson(Path.Combine(Project(), "Packages/packages-lock.json"))["dependencies"]?[package.name] as JObject;
            // Unity 2022 can omit PackageInfo.git. Never substitute the requested SHA for observed lock evidence.
            if (package.source != PackageSource.Git || apiLocator != expectedLocator ||
                (!string.IsNullOrEmpty(package.git?.hash) && package.git.hash != revision) ||
                (!string.IsNullOrEmpty(package.git?.revision) && package.git.revision != declaredRevision) ||
                locked == null || Text(locked, "source") != "git" || Text(locked, "version") != locator || Text(locked, "hash") != revision)
            {
                throw new InvalidOperationException("The actual Git source/API packageId/API revision/generated lock contradicts the pinned request: " + package.name);
            }
            return Text(locked, "hash");
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

        private static string PackageFileHash(string path, string relative, string name, bool git)
        {
            if (Regex.IsMatch(relative, @"^(?:Assets|Packages|ProjectSettings|Library|Temp|Tests|Tools|\.git|\.kilo|\.kilocode|\.opencode|obj|bin)(?:/|$)|(?:^|/)(?:AGENTS\.md|\.yl-[^/]+|[^/]+\.(?:csproj|sln|slnx))$|IvanMurzak|UnityMcp|ReflectorNet", RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException("Unauthorized installed product content: " + relative);
            }
            using (var stream = File.OpenRead(path))
            {
                var prefix = new byte[200];
                int length = stream.Read(prefix, 0, prefix.Length);
                if (Encoding.UTF8.GetString(prefix, 0, length).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal) ||
                    (relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        (length < 2 || prefix[0] != 77 || prefix[1] != 90 || name == "com.kano.ylframework.dotween" || name == "com.kano.ylframework.hybridclr")))
                {
                    throw new InvalidOperationException("Unmaterialized LFS or unapproved installed package binary: " + relative);
                }
            }
            if (relative != "package.json") { return Hash(path); }
            var manifest = ReadJson(path);
            var fingerprint = manifest.Property("_fingerprint");
            if (fingerprint != null)
            {
                if (!git || fingerprint.Value.Type != JTokenType.String || !Regex.IsMatch((string)fingerprint.Value, @"\A[0-9a-fA-F]{40}\z"))
                {
                    throw new InvalidOperationException("Untrusted package-manager fingerprint metadata.");
                }
                fingerprint.Remove();
            }
            if (!git) { return Hash(path); }
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(CanonicalToken(manifest).ToString(Formatting.None))).Select(value => value.ToString("x2")));
        }

        private static JArray CapturePackages(JObject request)
        {
            bool git = Text(request, "packageOrigin") == "GitRemote";
            string samples = Path.Combine(Project(), "Assets", "Samples");
            if (Directory.Exists(samples) && Files(samples).Any()) { throw new InvalidOperationException("The consumer must not default-import Samples, including non-script assets."); }
            var installed = PackageInfo.GetAllRegisteredPackages();
            var selected = ((JArray)request["packages"]).Cast<JObject>().ToArray();
            var names = selected.Select(package => Text(package, "name")).ToArray();
            if (installed.Any(package => package.errors.Length != 0 || (!package.name.StartsWith("com.unity.", StringComparison.Ordinal) &&
                !names.Contains(package.name) && !(Text(request, "integration") == "HybridCLR" && package.name == "com.code-philosophy.hybridclr"))))
            {
                throw new InvalidOperationException("The consumer resolved errors or undeclared non-Unity packages.");
            }
            var manifest = ReadJson(Path.Combine(Project(), "Packages/manifest.json"));
            var locked = ReadJson(Path.Combine(Project(), "Packages/packages-lock.json"));
            var result = new JArray();
            foreach (var expected in selected)
            {
                string name = Text(expected, "name");
                var matches = installed.Where(package => package.name == name).ToArray();
                if (matches.Length != 1) { throw new InvalidOperationException("A required registered package is missing or duplicated: " + name); }
                var package = matches[0];
                string root = Path.GetFullPath(package.resolvedPath).TrimEnd('\\', '/');
                string locator = git ? Text(request, "gitRepository") + "?path=/PackageSources/" + name + "#" + Text(request, "gitRevision") : "file:../PackageSources/" + name;
                if (package.source != (git ? PackageSource.Git : PackageSource.Local) || package.version != Text(expected, "version") || package.assetPath != "Packages/" + name ||
                    Text((JObject)manifest["dependencies"], name) != locator ||
                    (git ? (!Inside(root, Path.Combine(Project(), "Library", "PackageCache")) && !Inside(root, Text(request, "cacheRoot"))) || Directory.Exists(Path.Combine(Project(), "PackageSources")) :
                        !string.Equals(root, Text(expected, "root"), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("The package did not resolve from its exact owned File/GitRemote locator and resolvedRoot: " + name);
                }
                string revision = git ? GitRevision(package, locator, Text(request, "gitRevision"), Text(request, "gitRevision")) : null;
                var packageManifest = ReadJson(Path.Combine(root, "package.json"));
                var entry = locked["dependencies"]?[name] as JObject;
                if (Text(packageManifest, "name") != name || Text(packageManifest, "version") != package.version ||
                    entry == null || Text(entry, "source") != (git ? "git" : "local") || Text(entry, "version") != locator ||
                    packageManifest["dependencies"] is not JObject dependencies || entry["dependencies"] is not JObject lockDependencies ||
                    !JToken.DeepEquals(CanonicalToken(dependencies), CanonicalToken(lockDependencies)))
                {
                    throw new InvalidOperationException("Actual package manifest or full generated lock dependency closure is inconsistent.");
                }
                foreach (var dependency in dependencies.Properties())
                {
                    if (dependency.Value.Type != JTokenType.String || (dependency.Name.StartsWith("com.kano.", StringComparison.Ordinal) &&
                        (!names.Contains(dependency.Name) || (string)dependency.Value != package.version)))
                    {
                        throw new InvalidOperationException("The actual internal package dependency closure is incomplete.");
                    }
                }
                var files = ((JArray)expected["files"]).Cast<JObject>().ToArray();
                var actualFiles = Files(root).Select(path => Relative(root, path)).ToArray();
                if (files.Length != actualFiles.Length || !new HashSet<string>(files.Select(file => Text(file, "path")), StringComparer.Ordinal).SetEquals(actualFiles) ||
                    !File.Exists(Path.Combine(root, "LICENSE.md")) || new FileInfo(Path.Combine(root, "LICENSE.md")).Length == 0)
                {
                    throw new InvalidOperationException("Installed package file set differs from the explicit local release expected template: " + name);
                }
                var content = new StringBuilder();
                foreach (var file in files.OrderBy(file => Text(file, "path"), StringComparer.Ordinal))
                {
                    string relative = Text(file, "path");
                    string path = Path.GetFullPath(Path.Combine(root, relative));
                    if (!Inside(path, root)) { throw new InvalidOperationException("Installed package file escaped resolvedRoot."); }
                    string digest = PackageFileHash(path, relative, name, git);
                    if (!string.Equals(digest, Text(file, "sha256"), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Installed package bytes differ from the explicit expected template: " + name + "/" + relative);
                    }
                    content.Append(relative).Append('\0').Append(digest).Append('\n');
                }
                using var hash = SHA256.Create();
                string contentSha256 = string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(content.ToString())).Select(value => value.ToString("x2")));
                if (contentSha256 != Text(expected, "contentSha256")) { throw new InvalidOperationException("The complete installed content identity differs from the expected template."); }
                result.Add(JObject.FromObject(new { name, package.version, source = package.source.ToString(), package.assetPath, package.packageId,
                    resolvedPath = root, revision, apiHash = package.git?.hash, apiRevision = package.git?.revision, contentSha256, verifiedFiles = files.Length }));
            }
            return result;
        }

        private static string Text(JObject value, string field)
        {
            if (value?[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value[field]))
            {
                throw new InvalidOperationException("Integration string identity is invalid: " + field);
            }
            return (string)value[field];
        }

        private static string Project() => Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
        private static bool Inside(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string Relative(string root, string path) => path.Substring(root.Length + 1).Replace('\\', '/');
        private static string[] References(CompiledAssembly assembly) => assembly.assemblyReferences.Select(reference => reference.name)
            .Concat(assembly.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).Distinct(StringComparer.Ordinal).ToArray();
        private static object Graph(CompiledAssembly assembly) => new { assembly.name, assembly.sourceFiles, assembly.defines, assembly.outputPath,
            references = References(assembly), assembly.compiledAssemblyReferences };

        private static string Physical(string source, JObject[] packages)
        {
            string normalized = source.Replace('\\', '/');
            foreach (var package in packages)
            {
                string prefix = "Packages/" + Text(package, "name") + "/";
                if (normalized.StartsWith(prefix, StringComparison.Ordinal)) { return Path.GetFullPath(Path.Combine(Text(package, "root"), normalized.Substring(prefix.Length))); }
            }
            return Path.GetFullPath(source);
        }

        private static string Hash(string path)
        {
            using var hash = SHA256.Create();
            using var stream = File.OpenRead(path);
            return string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static void RejectLinks(string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw new InvalidOperationException("Linked integration input/output is refused."); }
            }
        }

        private static IEnumerable<string> Files(string root)
        {
            RejectLinks(root);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidOperationException("Linked integration content is refused."); }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); }
                    else { yield return path; }
                }
            }
        }

        private static void ValidateInvocation(JObject request)
        {
            if (request["schemaVersion"]?.Type != JTokenType.Integer || (long)request["schemaVersion"] != 1 ||
                Text(request, "purpose") != "YLTesting.IntegrationPackages" || !Application.isBatchMode ||
                !new[] { "DOTween", "Excel", "DomainExcel", "HybridCLR" }.Contains(Text(request, "integration")) ||
                !new[] { "File", "GitRemote" }.Contains(Text(request, "packageOrigin")) ||
                request["packages"] is not JArray packages || packages.Count == 0 || request["assemblies"] is not JArray assemblies || assemblies.Count < 3)
            {
                throw new InvalidOperationException("An explicit typed integration compilation request in a batch host is required.");
            }
            bool git = Text(request, "packageOrigin") == "GitRemote";
            if (git) { ValidateGitSource(request); }
            else if (new[] { "gitRepository", "gitRevision" }.Any(field => request[field] != null && request[field].Type != JTokenType.Null))
            {
                throw new InvalidOperationException("File refuses Git request parameters.");
            }
            string project = Project();
            string workspace = Directory.GetParent(project)?.Parent?.FullName;
            string result = Path.GetFullPath(Text(request, "resultRoot"));
            string output = Path.GetFullPath(Text(request, "output"));
            string cache = Path.GetFullPath(Text(request, "cacheRoot"));
            var owner = ReadJson(Path.Combine(project, ".yl-owner.json"));
            var resultOwner = ReadJson(Path.Combine(result, ".yl-owner.json"));
            var cacheOwner = ReadJson(Path.Combine(cache, ".yl-owner.json"));
            foreach (var record in new[] { owner, resultOwner, cacheOwner })
            {
                foreach (string field in new[] { "purpose", "kind", "path", "id", "workspaceId", "key" }) { Text(record, field); }
                if (record["schemaVersion"]?.Type != JTokenType.Integer || (long)record["schemaVersion"] != 1 || Text(record, "purpose") != "YLTesting.Directory" ||
                    !Regex.IsMatch(Text(record, "id"), "^[0-9a-f]{32}$") || !Regex.IsMatch(Text(record, "key"), "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")) { throw new InvalidOperationException("Invalid integration directory owner."); }
            }
            var workspaceOwner = ReadJson(Path.Combine(workspace ?? "", ".yl-workspace.json"));
            var inputs = ReadJson(Path.Combine(project, ".yl-inputs.json"));
            if (workspaceOwner["schemaVersion"]?.Type != JTokenType.Integer || (long)workspaceOwner["schemaVersion"] != 1 ||
                Text(workspaceOwner, "purpose") != "YLTesting.Workspace" || Text(workspaceOwner, "root") != workspace || Text(workspaceOwner, "id") != Text(request, "workspaceId") ||
                Text(owner, "workspaceId") != Text(request, "workspaceId") || Text(owner, "kind") != "Host" || Text(owner, "path") != project || Text(owner, "id") != Text(request, "hostId") ||
                project != Path.Combine(workspace, "Hosts", Text(owner, "key")) || Text(resultOwner, "workspaceId") != Text(request, "workspaceId") ||
                Text(resultOwner, "kind") != "Result" || Text(resultOwner, "id") != Text(request, "resultId") || Text(resultOwner, "path") != result ||
                !Regex.IsMatch(Path.GetFileName(result), "^[0-9a-f]{32}$") || result != Path.Combine(workspace, "Results", Text(resultOwner, "key"), Path.GetFileName(result)) ||
                Text(cacheOwner, "kind") != "Cache" || Text(cacheOwner, "workspaceId") != Text(request, "workspaceId") ||
                Text(cacheOwner, "id") != Text(request, "cacheId") || Text(cacheOwner, "path") != cache || cache != Path.Combine(workspace, "Cache", Text(cacheOwner, "key")) ||
                !Inside(output, result) || !Directory.Exists(output) || Application.unityVersion != Text(request, "unityVersion") ||
                inputs["schemaVersion"]?.Type != JTokenType.Integer || (long)inputs["schemaVersion"] != 1 || Text(inputs, "purpose") != "YLTesting.HostInputs" ||
                Text(inputs, "hostId") != Text(request, "hostId") || Text(inputs, "inputHash") != Text(request, "inputHash") || !Regex.IsMatch(Text(request, "inputHash"), "^[0-9a-f]{64}$") ||
                !Regex.IsMatch(Text(request, "expectedTemplateHash"), @"\A[0-9a-f]{64}\z"))
            {
                throw new InvalidOperationException("Integration host/result/workspace/version/input identity mismatch.");
            }
            RejectLinks(project); RejectLinks(output); RejectLinks(cache);
            if (!string.Equals(Hash(Path.Combine(project, "Packages/manifest.json")), Text(request, "manifestSha256"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The owned integration manifest was changed.");
            }
            var expected = new HashSet<string>(new[] { "com.kano.ylframework" }, StringComparer.Ordinal);
            switch (Text(request, "integration"))
            {
                case "DOTween": expected.Add("com.kano.ylframework.dotween"); break;
                case "Excel": expected.Add("com.kano.ylframework.excel"); break;
                case "DomainExcel": expected.Add("com.kano.ylframework.excel"); expected.Add("com.kano.ylframework.domain-excel"); break;
                case "HybridCLR": expected.Add("com.kano.ylframework.hybridclr"); break;
            }
            if (packages.Any(token => token is not JObject) || packages.Count != expected.Count ||
                !expected.SetEquals(packages.Cast<JObject>().Select(package => Text(package, "name")))) { throw new InvalidOperationException("Integration package selection is not the exact required closure."); }
            foreach (var package in packages.Cast<JObject>())
            {
                if (Text(package, "version") != "0.1.0-preview.1" || !Regex.IsMatch(Text(package, "contentSha256"), @"\A[0-9a-f]{64}\z") ||
                    (!git && Text(package, "root") != Path.Combine(project, "PackageSources", Text(package, "name"))) ||
                    (git && package["root"] != null && package["root"].Type != JTokenType.Null) ||
                    package["files"] is not JArray files || files.Count == 0) { throw new InvalidOperationException("Integration expected template/staging identity is invalid."); }
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in files)
                {
                    if (token is not JObject file || !Regex.IsMatch(Text(file, "sha256"), @"\A[0-9a-fA-F]{64}\z") ||
                        Path.IsPathRooted(Text(file, "path")) || Regex.IsMatch(Text(file, "path"), @"(?:^|/)\.\.?(?:/|$)|[\\:*?<>|""]") ||
                        !paths.Add(Text(file, "path"))) { throw new InvalidOperationException("Invalid or duplicate expected package file identity."); }
                }
            }
            var required = new HashSet<string>(new[] { "YLFramework.Runtime", "YLFramework.Editor" }, StringComparer.Ordinal);
            switch (Text(request, "integration"))
            {
                case "DOTween": required.Add("YLFramework.Actions.DOTween"); break;
                case "Excel": required.Add("YLFramework.Excel.Editor"); break;
                case "DomainExcel": required.Add("YLFramework.Excel.Editor"); required.Add("YLFramework.Domain.Excel.Editor"); break;
                case "HybridCLR": required.Add("YLFramework.HybridCLR"); required.Add("YLFramework.HybridCLR.Editor"); break;
            }
            if (assemblies.Any(token => token is not JObject) || assemblies.Count != required.Count ||
                !required.SetEquals(assemblies.Cast<JObject>().Select(assembly => Text(assembly, "name")))) { throw new InvalidOperationException("Required integration assemblies cannot be omitted."); }
            foreach (var assembly in assemblies.Cast<JObject>())
            {
                if (!expected.Contains(Text(assembly, "package")) || !new[] { "Editor", "Runtime" }.Contains(Text(assembly, "directory")) ||
                    assembly["player"]?.Type != JTokenType.Boolean) { throw new InvalidOperationException("Invalid required assembly source/Player graph contract."); }
            }
        }

        private static void WriteReport(JObject request, JObject report)
        {
            using var stream = new FileStream(Path.Combine(Text(request, "output"), "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(report.ToString(Formatting.Indented));
        }
    }
}
#endif
