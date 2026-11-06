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
                    entryPackage = Text(request, "entryPackage"), entryPackages = EntryPackages(request, Catalog(request)), catalogSha256 = Text(request, "catalogSha256"), optionalAdapterScenario = (string)request["optionalAdapterScenario"], registryUrl = (string)request["registryUrl"],
                    registryVersion = (string)request["registryVersion"], releaseManifestSha256 = (string)request["releaseManifestSha256"],
                    cacheEnvironment = Artifact(request) ? CacheEnvironment() : null,
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
                    .Select(PackageRow));
                var editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var player = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
                report["editorGraph"] = JArray.FromObject(editor.Select(Graph));
                report["playerGraph"] = JArray.FromObject(player.Select(Graph));
                ValidateGraph(request, editor, player);
                var required = new JArray();
                foreach (var expected in AssemblyPlan(request).Cast<JObject>().Where(row => (bool)row["enabled"]))
                {
                    string name = Text(expected, "name");
                    var assembly = editor.Single(item => item.name == name);
                    var package = selected.Single(item => Text(item, "name") == Text(expected, "package"));
                    string root = Text(package, "root");
                    var sources = ((JArray)expected["sources"]).Values<string>().ToArray();
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
                    int loadedTypeCount = loaded.GetTypes().Length;
                    if (!string.Equals(Path.GetFullPath(loaded.Location), Path.GetFullPath(assembly.outputPath), StringComparison.OrdinalIgnoreCase) || loadedTypeCount == 0)
                    {
                        throw new InvalidOperationException("The required compiled assembly is not actually loaded with types: " + name);
                    }
                    required.Add(JObject.FromObject(new { name, assembly.sourceFiles, assembly.defines, assembly.outputPath,
                        references = References(assembly), assembly.compiledAssemblyReferences, loadedPath = loaded.Location, loadedTypeCount,
                        loadedReferences = loaded.GetReferencedAssemblies().Select(reference => reference.FullName).ToArray(), sha256 = Hash(loaded.Location) }));
                }
                report["assemblies"] = required;
                string[] global = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone).Split(';');
                report["globalDefines"] = JArray.FromObject(global);
                if (global.Contains("YL_DOMAIN_EXCEL") || global.Contains("YL_GAMEPLAY_PACKAGE") || global.Contains("YL_HYBRIDCLR_9_0_3"))
                {
                    throw new InvalidOperationException("Package presence/version gates must not be replaced by global integration symbols.");
                }
                if (integration == "OptionalAdapterFixture")
                {
                    if (global.Contains("YL_FIXTURE_BACKEND")) { throw new InvalidOperationException("The fixture must use a package Version Define, not a global substitute."); }
                    var declared = new HashSet<string>(((JArray)Catalog(request)["packages"]).Cast<JObject>().SelectMany(package => ((JArray)package["assemblies"]).Cast<JObject>()).Select(assembly => Text(assembly, "name")), StringComparer.Ordinal);
                    var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).Where(declared.Contains).ToArray();
                    var expectedLoaded = AssemblyPlan(request).Cast<JObject>().Where(assembly => (bool)assembly["enabled"]).Select(assembly => Text(assembly, "name"));
                    if (!new HashSet<string>(expectedLoaded, StringComparer.Ordinal).SetEquals(loaded)) { throw new InvalidOperationException("A disabled/uninstalled fixture assembly is still loaded, or a required one is missing."); }
                    report["loadedFixtureAssemblies"] = JArray.FromObject(loaded);
                    var adapter = editor.SingleOrDefault(assembly => assembly.name == "YLFramework.FixtureAdapter");
                    if (adapter != null && (!adapter.defines.Contains("YL_FIXTURE_BACKEND") || !adapter.assemblyReferences.Any(assembly => assembly.name == "YLFramework.FixtureBackend")))
                    {
                        throw new InvalidOperationException("The required fixture adapter lacks its actual package define/backend assembly reference.");
                    }
                    if (editor.Any(assembly => assembly.name == "YLFramework.FixtureFeature" && assembly.defines.Contains("YL_FIXTURE_BACKEND"))) { throw new InvalidOperationException("The adapter-only Version Define leaked into its parent Runtime."); }
                }
                if (integration == "DOTween")
                {
                    report["sdk"] = ValidateDotween(request, editor, player, selected, global);
                    report["dotweenSdkMode"] = (string)request["dotweenSdkMode"] ?? "Required";
                }
                if (integration == "Excel" || integration == "ExcelGameplay")
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
                    var adapter = editor.SingleOrDefault(assembly => assembly.name == "YLFramework.Domain.Excel.Editor");
                    if ((adapter != null) != (integration == "ExcelGameplay") ||
                        (adapter != null && (!adapter.defines.Contains("YL_GAMEPLAY_PACKAGE") || !References(adapter).Contains("YLFramework.Gameplay.Editor"))))
                    {
                        throw new InvalidOperationException("The actual Gameplay package presence did not control the Excel adapter assembly.");
                    }
                }
                if (integration == "HybridCLR")
                {
                    var sdk = installed.Single(package => package.name == "com.code-philosophy.hybridclr");
                    JObject sdkReport;
                    if (Artifact(request)) { sdkReport = CaptureRegistrySdk(request); }
                    else
                    {
                        var expected = (JObject)request["sdk"];
                        string revision = GitRevision(sdk, Text(expected, "declaration"), Text(expected, "revision"), "v9.0.3");
                        if (sdk.version != "9.0.3" || Text((JObject)manifest["dependencies"], sdk.name) != Text(expected, "declaration"))
                        {
                            throw new InvalidOperationException("The actual official HybridCLR SDK source/version/commit is incorrect.");
                        }
                        sdkReport = PackageRow(sdk); sdkReport["revision"] = revision;
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
                    sdkReport["dnlib"] = dnlib; sdkReport["dnlibSha256"] = Hash(dnlib); report["sdk"] = sdkReport;
                }
                report["status"] = "Completed";
                if (integration == "DOTween" && (string)request["dotweenSdkMode"] == "Absent") { report["status"] = "NegativeNoSdk"; }
                if (integration == "DOTween" && (string)request["dotweenSdkMode"] == "SdkOnly") { report["status"] = "NegativeAdapterDisabled"; }
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
            if (reader.Read()) { throw new InvalidOperationException("Unexpected trailing integration JSON content."); }
            return value;
        }

        private static JObject CacheEnvironment() => JObject.FromObject(new
        {
            UPM_CACHE_ROOT = Environment.GetEnvironmentVariable("UPM_CACHE_ROOT"),
            UPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_CACHE_PATH"),
            UPM_NPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_NPM_CACHE_PATH")
        });

        private static JObject ValidateDotween(JObject request, CompiledAssembly[] editor, CompiledAssembly[] player, JObject[] selected, string[] global)
        {
            string mode = (string)request["dotweenSdkMode"] ?? "Required";
            if (mode == "DefineOnly") { throw new InvalidOperationException("DOTWEEN without its SDK cannot be a positive integration."); }
            if ((string)request["dotweenSdkMode"] == "Absent")
            {
                if (global.Contains("DOTWEEN") || editor.Concat(player).Any(assembly => assembly.name == "YLFramework.Actions.DOTween" ||
                        References(assembly).Any(reference => Regex.IsMatch(reference, @"^DOTween(?:\.|$)", RegexOptions.IgnoreCase))) ||
                    Directory.Exists(Path.Combine(Project(), "Assets/Plugins/Demigiant/DOTween")) ||
                    Files(Application.dataPath).Any(path => Regex.IsMatch(Path.GetFileName(path), @"^DOTween.*\.(?:dll|cs|asmdef)$", RegexOptions.IgnoreCase)))
                {
                    throw new InvalidOperationException("DOTween Absent refuses the wrapper, global DOTWEEN and any deployed SDK.");
                }
                return JObject.FromObject(new { mode = "Absent", status = "NegativeNoSdk", sdkDeployed = false, defineEnabled = false, sdkEditorToolingEnabled = false, wrapperCompiled = false });
            }
            string dll = Path.GetFullPath(Path.Combine(Project(), "Assets/Plugins/Demigiant/DOTween/DOTween.dll"));
            if (!File.Exists(dll) || !string.Equals(Hash(dll), Text((JObject)request["sdk"], "assemblySha256"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The deployed DOTween SDK must match its approved binary identity.");
            }
            if (mode == "SdkOnly")
            {
                if (global.Contains("DOTWEEN") || editor.Concat(player).Any(assembly => assembly.name == "YLFramework.Actions.DOTween") ||
                    AppDomain.CurrentDomain.GetAssemblies().Any(assembly => new[] { "YLFramework.Actions.DOTween", "DOTweenEditor", "DOTweenUpgradeManager" }.Contains(assembly.GetName().Name)) ||
                    editor.Concat(player).SelectMany(assembly => assembly.compiledAssemblyReferences).Any(path => new[] { "DOTweenEditor.dll", "DOTweenUpgradeManager.dll" }.Contains(Path.GetFileName(path))))
                {
                    throw new InvalidOperationException("SdkOnly refuses DOTWEEN or any compiled/loaded adapter.");
                }
            }
            else
            {
                var adapter = editor.Single(assembly => assembly.name == "YLFramework.Actions.DOTween");
                string reference = adapter.compiledAssemblyReferences.Single(path => Path.GetFileName(path) == "DOTween.dll");
                if (!global.Contains("DOTWEEN") || !adapter.defines.Contains("DOTWEEN") ||
                    !string.Equals(Path.GetFullPath(reference), dll, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("DOTween must compile against the approved consumer SDK and the owned DOTWEEN setting.");
                }
            }
            string modules = Path.Combine(Project(), "Assets/Plugins/Demigiant/DOTween/Modules");
            var sdkSources = Files(modules).Where(path => path.EndsWith(".cs", StringComparison.Ordinal)).Select(Path.GetFullPath).ToArray();
            var compiled = new HashSet<string>(editor.SelectMany(assembly => assembly.sourceFiles).Select(source => Physical(source, selected)), StringComparer.OrdinalIgnoreCase);
            if (sdkSources.Length == 0 || sdkSources.Any(path => !compiled.Contains(path)))
            {
                throw new InvalidOperationException("The complete SDK Modules did not enter actual compilation: " + string.Join(",", sdkSources.Where(path => !compiled.Contains(path))));
            }
            return JObject.FromObject(new { mode, status = mode == "SdkOnly" ? "NegativeAdapterDisabled" : "Completed", sdkDeployed = true,
                defineEnabled = mode == "Required", sdkEditorToolingEnabled = mode == "Required", wrapperCompiled = mode == "Required", version = Text((JObject)request["sdk"], "version"), dll, sha256 = Hash(dll), modules = sdkSources });
        }

        private static void ValidateGraph(JObject request, CompiledAssembly[] editor, CompiledAssembly[] player)
        {
            var installed = PackageInfo.GetAllRegisteredPackages();
            var plan = AssemblyPlan(request);
            PackageCatalogContract.AssertGraph(plan, editor.Select(row => row.name), player.Select(row => row.name));
            var owners = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in plan.Cast<JObject>())
            {
                var package = installed.Single(item => item.name == Text(owner, "package"));
                foreach (string source in ((JArray)owner["sources"]).Values<string>())
                {
                    owners.Add(Path.GetFullPath(Path.Combine(package.resolvedPath, source)), owner);
                }
            }
            foreach (var assembly in editor.Concat(player))
            {
                var compiledSources = new List<string>();
                if (assembly.name == "YLFramework.Runtime")
                {
                    var forbidden = References(assembly).Where(reference =>
                        !(Regex.IsMatch(reference, @"^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)(?:\.dll)?$") ||
                            !player.Contains(assembly) && Regex.IsMatch(reference, @"^(?:UnityEditor(?:\.[A-Za-z0-9_]+)*|Unity\.Android\.(?:Types|Gradle|GradleProject))(?:\.dll)?$")) ||
                        Regex.IsMatch(reference, "TestRunner|Tests|Mcp|ReflectorNet|Analyzer", RegexOptions.IgnoreCase)).ToArray();
                    if (forbidden.Length != 0) { throw new InvalidOperationException("The main Runtime acquired an unapproved dependency: " + string.Join(", ", forbidden)); }
                }
                if (Regex.IsMatch(assembly.name, "TestRunner|Tests|Acceptance|Mcp|ReflectorNet", RegexOptions.IgnoreCase) ||
                    References(assembly).Any(reference => Regex.IsMatch(reference, "TestRunner|nunit|Mcp|ReflectorNet", RegexOptions.IgnoreCase)))
                {
                    throw new InvalidOperationException("Tests or MCP entered the consumer compilation graph.");
                }
                foreach (string source in assembly.sourceFiles)
                {
                    string normalized = source.Replace('\\', '/');
                    if (Regex.IsMatch(normalized, @"(?:^|/)(?:Samples~|Samples|Tests)(?:/|$)|(?:^|/)\.\.?(?:/|$)", RegexOptions.IgnoreCase))
                    {
                        throw new InvalidOperationException("Default Samples, Tests or traversal entered the compilation graph: " + source);
                    }
                    string path;
                    if (normalized.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        var package = installed.SingleOrDefault(item => normalized.StartsWith("Packages/" + item.name + "/", StringComparison.Ordinal));
                        if (package == null) { throw new InvalidOperationException("A source has no registered package owner: " + source); }
                        path = Path.GetFullPath(Path.Combine(package.resolvedPath, normalized.Substring(("Packages/" + package.name + "/").Length)));
                        if (!Inside(path, Path.GetFullPath(package.resolvedPath))) { throw new InvalidOperationException("A source escaped its registered package."); }
                    }
                    else
                    {
                        path = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Combine(Project(), source));
                        if (Inside(path, Application.dataPath))
                        {
                            if (!string.Equals(path, Path.GetFullPath(Path.Combine(Project(), "Assets/YLTestingIntegration/Editor/IntegrationEntry.cs")), StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(path, Path.GetFullPath(Path.Combine(Project(), "Assets/YLTestingIntegration/Editor/PackageCatalogContract.cs")), StringComparison.OrdinalIgnoreCase) &&
                                !(Text(request, "integration") == "DOTween" && (string)request["dotweenSdkMode"] != "Absent" &&
                                    Inside(path, Path.Combine(Project(), "Assets/Plugins/Demigiant/DOTween"))))
                            {
                                throw new InvalidOperationException("A product source was copied into Assets or an unknown source entered the host: " + source);
                            }
                        }
                        else if (!installed.Any(package => !string.IsNullOrEmpty(package.resolvedPath) && Inside(path, Path.GetFullPath(package.resolvedPath))))
                        {
                            throw new InvalidOperationException("A source compiled outside every actual registered package: " + source);
                        }
                    }
                    if (!File.Exists(path)) { throw new InvalidOperationException("A compiled source is missing from its actual owner: " + source); }
                    RejectLinks(path);
                    if (owners.TryGetValue(path, out var owner) && (!(bool)owner["enabled"] || Text(owner, "name") != assembly.name ||
                        player.Contains(assembly) && !(bool)owner["player"]))
                    {
                        throw new InvalidOperationException("A disabled, differently owned or Editor-only product source entered an actual assembly: " + source);
                    }
                    compiledSources.Add(path);
                }
                var required = plan.Cast<JObject>().SingleOrDefault(item => (bool)item["enabled"] && Text(item, "name") == assembly.name);
                if (required != null)
                {
                    var package = installed.Single(item => item.name == Text(required, "package"));
                    var sources = ((JArray)required["sources"]).Values<string>()
                        .Select(path => Path.GetFullPath(Path.Combine(package.resolvedPath, path))).ToArray();
                    if (sources.Length == 0 || sources.Length != compiledSources.Count || !new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase).SetEquals(compiledSources))
                    {
                        throw new InvalidOperationException("An Editor/Player assembly lost, duplicated or changed its exact installed source closure: " + assembly.name);
                    }
                }
            }
            if (player.Any(assembly => Regex.IsMatch(assembly.name, "Excel|MiniExcel", RegexOptions.IgnoreCase) ||
                References(assembly).Any(reference => Regex.IsMatch(reference, "Excel|MiniExcel", RegexOptions.IgnoreCase))))
            {
                throw new InvalidOperationException("Excel or MiniExcel leaked into the actual Player assembly graph.");
            }
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

        private static string PackageFileHash(string path, string relative, string name, bool git, bool localTarball = false)
        {
            if (Regex.IsMatch(relative, @"(?:^|/)(?:SDK|SDKs|ThirdPartySdk|Demigiant)(?:/|$)|(?:^|/)(?:DOTween[^/]*\.dll|HybridCLR[^/]*\.dll|dnlib\.dll)$", RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException("An external SDK was bundled in an owned package: " + relative);
            }
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
                        (length < 2 || prefix[0] != 77 || prefix[1] != 90 || name == "com.kano.ylframework.hybridclr")))
                {
                    throw new InvalidOperationException("Unmaterialized LFS or unapproved installed package binary: " + relative);
                }
            }
            if (relative != "package.json") { return Hash(path); }
            var manifest = ReadJson(path);
            if (manifest.Properties().Any(property => property.Name.StartsWith("_", StringComparison.Ordinal) && property.Name != "_fingerprint"))
            {
                throw new InvalidOperationException("Unknown package-manager manifest metadata.");
            }
            var fingerprint = manifest.Property("_fingerprint");
            if (fingerprint != null)
            {
                if (!git || fingerprint.Value.Type != JTokenType.String || !Regex.IsMatch((string)fingerprint.Value,
                    localTarball ? @"\A[0-9a-fA-F]{32}\z" : @"\A[0-9a-fA-F]{40}\z"))
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
            if (Artifact(request)) { return ValidateInstalledCatalog(request, CaptureArtifactPackages(request)); }
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
                var row = PackageRow(package);
                row["resolvedPath"] = root; row["revision"] = revision; row["contentSha256"] = contentSha256; row["verifiedFiles"] = files.Length;
                result.Add(row);
            }
            return ValidateInstalledCatalog(request, result);
        }

        private static JObject Catalog(JObject request) => PackageCatalogContract.Load(request, Project(), ReadJson);

        private static JArray AssemblyPlan(JObject request) => PackageCatalogContract.Plan(Catalog(request), (JArray)request["packages"],
            Text(request, "integration"), Text(request, "integration") == "DOTween" ? (string)request["dotweenSdkMode"] ?? "Required" : null);

        private static JArray ValidateInstalledCatalog(JObject request, JArray packages)
        {
            var manifests = new JObject();
            foreach (var package in packages.Cast<JObject>())
            {
                manifests.Add(Text(package, "name"), ReadJson(Path.Combine(Text(package, "resolvedPath"), "package.json")));
            }
            var catalog = Catalog(request);
            PackageCatalogContract.AssertClosure(catalog, manifests, EntryPackages(request, catalog));
            return packages;
        }

        private static string[] EntryPackages(JObject request, JObject catalog)
        {
            if (Text(request, "integration") != "OptionalAdapterFixture")
            {
                var roots = Text(request, "integration") == "ExcelGameplay"
                    ? new[] { "com.kano.ylframework.excel", "com.kano.ylframework.gameplay" }
                    : new[] { Text(request, "entryPackage") };
                if (Text(request, "entryPackage") != roots[0] ||
                    (Text(request, "integration") == "ExcelGameplay" && request.Property("entryPackages") == null) ||
                    (request.Property("entryPackages") != null && (request["entryPackages"] is not JArray declared ||
                        declared.Any(value => value.Type != JTokenType.String) || !declared.Values<string>().SequenceEqual(roots))))
                {
                    throw new InvalidOperationException("Integration entry roots are invalid, duplicated or differ from the explicit profile.");
                }
                return roots;
            }
            const string main = "com.kano.ylframework", feature = "com.kano.ylframework.fixture-feature", backend = "com.kano.ylframework.fixture-backend";
            string scenario = Text(request, "optionalAdapterScenario");
            if (Text(request, "packageOrigin") != "File" || Text(request, "entryPackage") != main || !new[] { "Neither", "Feature", "Backend", "Both" }.Contains(scenario) ||
                !new HashSet<string>(new[] { main, feature, backend }, StringComparer.Ordinal).SetEquals(((JArray)catalog["packages"]).Cast<JObject>().Select(package => Text(package, "id"))))
            {
                throw new InvalidOperationException("Unknown optional-adapter fixture scenario/catalog/source.");
            }
            return new[] { main }.Concat(scenario == "Feature" || scenario == "Both" ? new[] { feature } : Array.Empty<string>())
                .Concat(scenario == "Backend" || scenario == "Both" ? new[] { backend } : Array.Empty<string>()).ToArray();
        }

        private static bool Artifact(JObject request) => (string)request["packageOrigin"] == "Registry" || (string)request["packageOrigin"] == "LocalTarball";

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
            var roots = EntryPackages(request, Catalog(request));
            if (roots.Any(entry => !selected.ContainsKey(entry))) { throw new InvalidOperationException("An artifact root is not in the expected closure."); }
            var depths = roots.ToDictionary(entry => entry, _ => 0, StringComparer.Ordinal);
            var pending = new Queue<string>(roots);
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
                (Directory.Exists(samples) && Files(samples).Any())) { throw new InvalidOperationException("Artifact hosts refuse copied sources and default-imported Samples."); }
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
            var manifest = ReadJson(Path.Combine(Project(), "Packages/manifest.json"));
            var declared = manifest["dependencies"] as JObject;
            var roots = EntryPackages(request, Catalog(request));
            var direct = selected.Where(package => !registry || roots.Contains(Text(package, "name"))).ToArray();
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
                var files = ((JArray)expected["files"]).Cast<JObject>().ToArray();
                var actual = Files(root).Select(path => Relative(root, path)).ToArray();
                if (files.Length != actual.Length || !new HashSet<string>(files.Select(file => Text(file, "path")), StringComparer.Ordinal).SetEquals(actual) ||
                    !File.Exists(Path.Combine(root, "LICENSE.md")) || new FileInfo(Path.Combine(root, "LICENSE.md")).Length == 0)
                {
                    throw new InvalidOperationException("The installed artifact file set/license differs from the frozen release.");
                }
                var content = new StringBuilder();
                foreach (var file in files.OrderBy(file => Text(file, "path"), StringComparer.Ordinal))
                {
                    string relative = Text(file, "path");
                    if (Regex.IsMatch(relative, @"(?:^|/)(?:DOTween[^/]*\.dll|HybridCLR[^/]*\.dll|dnlib\.dll|Demigiant)(?:/|$)", RegexOptions.IgnoreCase))
                    {
                        throw new InvalidOperationException("An external SDK was bundled in an owned artifact.");
                    }
                    string digest = PackageFileHash(Path.Combine(root, relative), relative, name, true, !registry);
                    if (!string.Equals(digest, Text(file, "sha256"), StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Installed artifact semantic file digest mismatch."); }
                    content.Append(relative).Append('\0').Append(digest).Append('\n');
                }
                using var hash = SHA256.Create();
                string digestAll = string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(content.ToString())).Select(value => value.ToString("x2")));
                if (!string.Equals(digestAll, Text(expected, "contentSha256"), StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Installed artifact complete semantic digest mismatch."); }
                var row = PackageRow(package); row["resolvedPath"] = root; row["contentSha256"] = digestAll; row["verifiedFiles"] = files.Length; result.Add(row);
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

        private static string Text(JObject value, string field)
        {
            if (value?[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value[field]))
            {
                throw new InvalidOperationException("Integration string identity is invalid: " + field);
            }
            return (string)value[field];
        }

        private static string Project() => Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
        private static bool Inside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
                !new[] { "Package", "DOTween", "Excel", "ExcelGameplay", "HybridCLR", "OptionalAdapterFixture" }.Contains(Text(request, "integration")) ||
                !new[] { "File", "GitRemote", "Registry", "LocalTarball" }.Contains(Text(request, "packageOrigin")) ||
                request["packages"] is not JArray packages || packages.Count == 0 || request["assemblies"] is not JArray assemblies || assemblies.Count == 0)
            {
                throw new InvalidOperationException("An explicit typed integration compilation request in a batch host is required.");
            }
            bool git = Text(request, "packageOrigin") == "GitRemote";
            if (Artifact(request)) { ValidateArtifactRequest(request); }
            if ((request["dotweenSdkMode"] != null && request["dotweenSdkMode"].Type != JTokenType.Null &&
                    !new[] { "Required", "Absent", "SdkOnly", "DefineOnly" }.Contains(Text(request, "dotweenSdkMode"))) ||
                (request.Property("dotweenSdkMode") != null && Text(request, "integration") != "DOTween"))
            {
                throw new InvalidOperationException("Invalid explicit DOTween SDK mode.");
            }
            if (request.Property("optionalAdapterScenario") != null && Text(request, "integration") != "OptionalAdapterFixture") { throw new InvalidOperationException("A fixture scenario cannot alter a product smoke profile."); }
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
            var catalog = Catalog(request);
            var known = new HashSet<string>(((JArray)catalog["packages"]).Cast<JObject>().Select(package => Text(package, "id")), StringComparer.Ordinal);
            string entryPackage = Text(request, "entryPackage");
            string profileEntry = Text(request, "integration") switch
            {
                "DOTween" => "com.kano.ylframework", "Excel" => "com.kano.ylframework.excel",
                "ExcelGameplay" => "com.kano.ylframework.excel", "HybridCLR" => "com.kano.ylframework.hybridclr", _ => entryPackage
            };
            if (!known.Contains(entryPackage) || entryPackage != profileEntry || packages.Any(token => token is not JObject) ||
                packages.Cast<JObject>().Any(package => !known.Contains(Text(package, "name"))) ||
                packages.Cast<JObject>().Select(package => Text(package, "name")).Distinct(StringComparer.Ordinal).Count() != packages.Count)
            {
                throw new InvalidOperationException("Unknown, duplicate or mismatched integration package selection.");
            }
            var entryPackages = EntryPackages(request, catalog);
            if (Text(request, "integration") == "OptionalAdapterFixture" &&
                !new HashSet<string>(entryPackages, StringComparer.Ordinal).SetEquals(packages.Cast<JObject>().Select(package => Text(package, "name"))))
            {
                throw new InvalidOperationException("The fixture package selection differs from its explicit four-state scenario.");
            }
            foreach (var package in packages.Cast<JObject>())
            {
                if (!Regex.IsMatch(Text(package, "version"), @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z") || !Regex.IsMatch(Text(package, "contentSha256"), @"\A[0-9a-f]{64}\z") ||
                    (!git && !Artifact(request) && Text(package, "root") != Path.Combine(project, "PackageSources", Text(package, "name"))) ||
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
            var plan = AssemblyPlan(request).Cast<JObject>().Where(row => (bool)row["enabled"]).ToArray();
            var required = new HashSet<string>(plan.Select(row => Text(row, "name")), StringComparer.Ordinal);
            if (assemblies.Any(token => token is not JObject) || assemblies.Count != required.Count ||
                !required.SetEquals(assemblies.Cast<JObject>().Select(assembly => Text(assembly, "name")))) { throw new InvalidOperationException("Required integration assemblies cannot be omitted."); }
            foreach (var assembly in assemblies.Cast<JObject>())
            {
                var expected = plan.Single(row => Text(row, "name") == Text(assembly, "name"));
                if (Text(assembly, "package") != Text(expected, "package") || Text(assembly, "directory") != Text(expected, "directory") ||
                    assembly["player"]?.Type != JTokenType.Boolean || (bool)assembly["player"] != (bool)expected["player"]) { throw new InvalidOperationException("Invalid required assembly source/Player graph contract."); }
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
