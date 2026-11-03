#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Testing
{
    // Positive-only probe: no static framework/Sample/SDK references and no native, Player or uninstall operations.
    public static class RegistryAuthoringEntry
    {
        private const string Flag = "-ylRegistryAuthoringRequest";
        private static Context current;
        private static int stableTicks;

        public static void Execute() => Begin(Environment.GetCommandLineArgs());

        private static void Begin(string[] arguments)
        {
            var context = Validate(arguments, false);
            Start(context);
        }

        [InitializeOnLoadMethod]
        private static void OnReload()
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains(Flag)) return;
            EditorApplication.delayCall += () =>
            {
                var request = Read(Argument(args, Flag));
                if (Text(request, "phase") == "Import" && File.Exists(Safe(Path.Combine(Text(request, "output"), "import.json"))) &&
                    !File.Exists(Safe(Path.Combine(Text(request, "output"), "report.json")))) Resume(args);
            };
        }

        private static void Resume(string[] arguments) => Start(Validate(arguments, true));

        private static void Start(Context context)
        {
            EditorApplication.update -= Tick;
            current = context;
            stableTicks = 0;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) { stableTicks = 0; return; }
                if (++stableTicks < 2) return;
                Authority(current);
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("The actual authoring host has compilation errors.");
                var packages = CapturePackages(current);
                var phase = Text(current.request, "phase");
                var sample = FindSample(current, packages);
                var assemblies = new JArray();
                JObject authoring = null;
                if (phase == "Inspect")
                {
                    AssertNotCompiled();
                    if ((bool)Property(sample, "isImported")) throw new InvalidOperationException("Default installation imported a Sample.");
                }
                else if (phase == "Import")
                {
                    if (current.import == null)
                    {
                        AssertNotCompiled();
                        if ((bool)Property(sample, "isImported")) throw new InvalidOperationException("Do not overwrite an imported/unknown Sample.");
                        var method = sample.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == "Import" && m.GetParameters().Length == 1);
                        var option = Enum.Parse(method.GetParameters()[0].ParameterType, "None");
                        if (Convert.ToInt32(option) != 0) throw new InvalidOperationException("Samples API None is not a non-overwriting option.");
                        current.import = new JObject { ["requestSha256"] = current.requestHash, ["pid"] = Process.GetCurrentProcess().Id,
                            ["api"] = "UnityEditor.PackageManager.UI.Sample", ["options"] = "None", ["callIssued"] = true,
                            ["returnObserved"] = false, ["returnValue"] = JValue.CreateNull(), ["completion"] = "IssuedBeforeDomainReload" };
                        SaveNew(Path.Combine(Text(current.request, "output"), "import.json"), current.import);
                        var result = method.Invoke(sample, new[] { option });
                        if (!(result is bool imported)) throw new InvalidOperationException("Samples API returned no boolean import result.");
                        current.import["returnObserved"] = true; current.import["returnValue"] = imported; current.import["completion"] = "ImportReturn";
                        ReplaceImport(current);
                        if (!imported) throw new InvalidOperationException("Samples API Import(None) returned false.");
                        stableTicks = 0;
                        return;
                    }
                    if (!(bool)Property(sample, "isImported")) throw new InvalidOperationException("The actual Samples API does not observe the imported Sample.");
                    AssertImportedSource(current, sample, packages);
                    var receipt = Receipt(current, "YLTesting.Samples", "Assets/Samples", false);
                    receipt["sample"] = SampleIdentity(current, sample, packages);
                    receipt["import"] = current.import.DeepClone();
                    SaveNew(Path.Combine(Text(current.request, "hostRoot"), ".yl-samples.json"), receipt);
                    current.sampleHash = Hash(Path.Combine(Text(current.request, "hostRoot"), ".yl-samples.json"));
                }
                else
                {
                    if (!(bool)Property(sample, "isImported")) throw new InvalidOperationException("The explicitly imported Sample is missing.");
                    AssertImportedSource(current, sample, packages);
                    assemblies = CaptureSampleAssemblies(current, sample);
                    if (phase == "Author")
                    {
                        current.authorIssued = true;
                        authoring = CreateAssets(current);
                        var receipt = Receipt(current, "YLTesting.RegistryAuthoring.Outputs", Text(current.request, "assetDirectory"), true);
                        receipt["authoring"] = authoring;
                        receipt["sample"] = SampleIdentity(current, sample, packages);
                        SaveNew(Path.Combine(Text(current.request, "hostRoot"), ".yl-authoring-outputs.json"), receipt);
                        current.managedHash = Hash(Path.Combine(Text(current.request, "hostRoot"), ".yl-authoring-outputs.json"));
                    }
                    else
                    {
                        var receipt = Read(Path.Combine(Text(current.request, "hostRoot"), ".yl-authoring-outputs.json"));
                        authoring = (JObject)receipt["authoring"].DeepClone();
                        AssertGeneratedTypes(current);
                    }
                }
                var sampleResult = SampleIdentity(current, sample, packages);
                sampleResult["status"] = phase == "Inspect" ? "NotImported" : "Imported";
                if (phase == "Import") foreach (var field in current.import.Properties()) sampleResult[field.Name] = field.Value.DeepClone();
                Finish("Completed", "", packages, sampleResult, assemblies, authoring);
            }
            catch (Exception error) { Finish("Failed", error.ToString(), null, null, null, null); }
        }

        private static JObject CreateAssets(Context context)
        {
            var request = context.request;
            var target = Text(request, "assetDirectory");
            var baseline = Text(request, "baselineDirectory");
            if (EditorApplication.isPlayingOrWillChangePlaymode || Directory.Exists(Child(Text(request, "hostRoot"), "Assets/YLFrameworkData")) ||
                File.Exists(Child(Text(request, "hostRoot"), "Assets/YLFrameworkData.meta"))) throw new InvalidOperationException("Preserve unknown authoring output/dirty Editor state.");
            var hybrid = Hybrid(context);
            var assemblyName = hybrid ? "YLFramework.HybridCLR.Sample.Editor" : "YLFramework.ResourceStartup.Sample.Editor";
            var typeName = hybrid ? "YLFramework.Samples.HybridCLR.Editor.HybridCLRExampleAssets" : "YLFramework.Samples.Resource.Editor.ResourceStartupExampleEditor";
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName);
            var type = assembly.GetType(typeName, true);
            var signature = hybrid ? new[] { typeof(string), typeof(string) } : Type.EmptyTypes;
            var factory = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, signature, null) ?? throw new InvalidOperationException("The real sample authoring factory is unavailable.");
            string creationPath = target;
            string creationGuid;
            if (!hybrid)
            {
                creationPath = "Assets/YLResourceStartupExample";
                if (Directory.Exists(Child(Text(request, "hostRoot"), creationPath)) || File.Exists(Child(Text(request, "hostRoot"), creationPath + ".meta")))
                    throw new InvalidOperationException("Preserve an unknown ResourceStartup factory output.");
                var result = factory.Invoke(null, System.Array.Empty<object>());
                if (!(result is string created) || created != creationPath) throw new InvalidOperationException("The actual ResourceStartup factory returned a different output.");
                creationGuid = AssetDatabase.AssetPathToGUID(creationPath);
                AssetDatabase.CreateFolder("Assets", "YLFrameworkData");
                var error = AssetDatabase.MoveAsset(created, target);
                if (!string.IsNullOrEmpty(error)) throw new IOException("Could not move the actual project-owned sample output: " + error);
                if (creationGuid != AssetDatabase.AssetPathToGUID(target)) throw new InvalidOperationException("Moving actual authored assets changed their GUID.");
            }
            else
            {
                AssetDatabase.CreateFolder("Assets", "YLFrameworkData");
                var result = factory.Invoke(null, new object[] { target, baseline }) as UnityEngine.Object;
                if (!result || result.GetType().FullName != "YLFramework.Integrations.HybridCLR.Editor.HybridCLRBuildProfile" || AssetDatabase.GetAssetPath(result) != target + "/Profile.asset")
                    throw new InvalidOperationException("The actual HybridCLR factory returned no saved sample profile.");
                if ((string)result.GetType().GetField("baselineDirectory").GetValue(result) != baseline ||
                    (string)result.GetType().GetField("generatedAssetDirectory").GetValue(result) != target + "/Generated") throw new InvalidOperationException("The real HybridCLR profile did not retain its explicit authoring paths.");
                creationGuid = AssetDatabase.AssetPathToGUID(target);
            }
            if (Directory.Exists(Safe(baseline)) || File.Exists(Safe(baseline))) throw new InvalidOperationException("Asset authoring unexpectedly created a native/build baseline.");
            AssertGeneratedTypes(context);
            return new JObject { ["status"] = "Generated", ["sentinel"] = false, ["factoryType"] = type.FullName, ["factoryAssembly"] = assembly.GetName().Name,
                ["factoryMethod"] = hybrid ? "Create(System.String,System.String)" : "Create()", ["creationPath"] = creationPath, ["creationGuid"] = creationGuid,
                ["assetDirectory"] = target, ["baselineDirectory"] = baseline, ["baselineCreated"] = false,
                ["methodArguments"] = hybrid ? new JArray(target, baseline) : new JArray() };
        }

        private static void AssertGeneratedTypes(Context context)
        {
            var root = Text(context.request, "assetDirectory");
            var expected = Hybrid(context)
                ? new[] { new[] { "Project.asset", "YLFramework.Editor.Resource.ResourceProject" }, new[] { "Recipe.asset", "YLFramework.Editor.Resource.ResourceBuildRecipe" },
                    new[] { "Profile.asset", "YLFramework.Integrations.HybridCLR.Editor.HybridCLRBuildProfile" }, new[] { "Data/message.json", "UnityEngine.TextAsset" }, new[] { "Data/HotView.prefab", "UnityEngine.GameObject" } }
                : new[] { new[] { "Project.asset", "YLFramework.Editor.Resource.ResourceProject" }, new[] { "Content/Data.asset", "YLFramework.Samples.Resource.ResourceStartupExampleData" },
                    new[] { "Content/Message.txt", "UnityEngine.TextAsset" }, new[] { "Content/Tone.wav", "UnityEngine.AudioClip" }, new[] { "Example.unity", "UnityEditor.SceneAsset" } };
            foreach (var pair in expected)
            {
                var path = root + "/" + pair[0];
                if (!File.Exists(Child(Text(context.request, "hostRoot"), path)) || AssetDatabase.GetMainAssetTypeAtPath(path)?.FullName != pair[1] ||
                    !AssetDatabase.LoadMainAssetAtPath(path)) throw new InvalidOperationException("Actual sample-generated asset/type is missing: " + path);
            }
            if (Hybrid(context))
            {
                var prefab = AssetDatabase.LoadMainAssetAtPath(root + "/Data/HotView.prefab") as GameObject;
                var behaviour = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "YLFramework.HybridCLR.Sample.Entry").GetType("YLFramework.Samples.HybridCLR.HotUpdate.ExampleBehaviour", true);
                if (!prefab || !prefab.GetComponent(behaviour) || prefab.GetComponentsInChildren<Transform>(true).Any(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) != 0))
                    throw new InvalidOperationException("The actual generated HybridCLR prefab is missing its compiled sample component.");
            }
        }

        private static JArray CaptureSampleAssemblies(Context context, object sample)
        {
            var prefix = Safe((string)Property(sample, "importPath"));
            var expected = Hybrid(context) ? new[] { "YLFramework.HybridCLR.Sample.Editor", "YLFramework.HybridCLR.Sample.Entry", "YLFramework.HybridCLR.Sample.Host", "YLFramework.HybridCLR.Sample.Support" }
                : new[] { "YLFramework.ResourceStartup.Sample", "YLFramework.ResourceStartup.Sample.Editor" };
            var editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            var player = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            var result = new JArray();
            foreach (var name in expected.OrderBy(n => n, StringComparer.Ordinal))
            {
                var definition = TreeFiles(prefix).Where(p => p.EndsWith(".asmdef", StringComparison.Ordinal)).Single(p => Text(Read(p), "name") == name);
                var sources = TreeFiles(Path.GetDirectoryName(definition)).Where(p => p.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                var candidates = editor.Where(a => a.name == name).ToArray();
                if (sources.Length == 0 || candidates.Length != 1) throw new InvalidOperationException("The imported Sample assembly was not actually compiled: " + name);
                var assembly = candidates[0];
                var actual = assembly.sourceFiles.Select(p => ResolveSource(context, p)).ToArray();
                if (actual.Length != sources.Length || !new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase).SetEquals(sources)) throw new InvalidOperationException("Sample compilation used missing/foreign/duplicate source files: " + name);
                var output = ResolveSource(context, assembly.outputPath);
                if (!Inside(output, Path.Combine(Text(context.request, "hostRoot"), "Library/ScriptAssemblies")) || !File.Exists(output)) throw new InvalidOperationException("Missing actual Sample assembly output: " + name);
                var loaded = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == name);
                if (loaded == null || string.IsNullOrEmpty(loaded.Location) || Hash(loaded.Location) != Hash(output)) throw new InvalidOperationException("The compiled Sample assembly is not the actually loaded assembly: " + name);
                var playerCandidates = player.Where(a => a.name == name).ToArray();
                if (name.EndsWith(".Editor", StringComparison.Ordinal))
                {
                    if (playerCandidates.Length != 0) throw new InvalidOperationException("Sample Editor authoring entered the Player graph.");
                }
                else if (playerCandidates.Length != 1 || playerCandidates[0].sourceFiles.Length != sources.Length ||
                    !new HashSet<string>(playerCandidates[0].sourceFiles.Select(p => ResolveSource(context, p)), StringComparer.OrdinalIgnoreCase).SetEquals(sources)) throw new InvalidOperationException("The Sample Player source graph is incomplete or foreign.");
                result.Add(new JObject { ["name"] = name, ["output"] = output, ["sha256"] = Hash(output), ["loadedAssembly"] = loaded.FullName,
                    ["loadedLocation"] = loaded.Location, ["sources"] = new JArray(actual.OrderBy(p => p, StringComparer.Ordinal)), ["playerIncluded"] = playerCandidates.Length == 1 });
            }
            return result;
        }

        private static void AssertNotCompiled()
        {
            foreach (var mode in new[] { AssembliesType.Editor, AssembliesType.Player })
                if (CompilationPipeline.GetAssemblies(mode).Any(a => a.name.IndexOf(".Sample", StringComparison.Ordinal) >= 0 ||
                    a.sourceFiles.Any(p => p.Replace('\\', '/').Contains("/Samples~/") || p.Replace('\\', '/').Contains("Assets/Samples/")))) throw new InvalidOperationException("Default installation compiled Sample source before explicit import.");
        }

        private static object FindSample(Context context, JArray packages)
        {
            var api = typeof(UnityEditor.PackageManager.UI.Sample);
            var package = packages.Cast<JObject>().Single(p => Text(p, "name") == Text(context.request, "entryPackage"));
            var samples = api.GetMethod("FindByPackage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null)?.Invoke(null,
                new object[] { Text(package, "name"), Text(package, "version") }) as IEnumerable;
            if (samples == null) throw new InvalidOperationException("Samples API returned no samples.");
            var sample = samples.Cast<object>().Single(s => (string)Property(s, "displayName") == SampleName(context));
            var resolved = Safe((string)Property(sample, "resolvedPath"));
            EqualPath(resolved, Path.Combine(Text(package, "resolvedPath"), SampleRelative(context)));
            var imported = Safe((string)Property(sample, "importPath"));
            if (!Inside(imported, Path.Combine(Text(context.request, "hostRoot"), "Assets/Samples"))) throw new InvalidOperationException("Samples API importPath escaped the owned Samples boundary.");
            if ((bool)Property(sample, "interactiveImport")) throw new InvalidOperationException("Interactive unitypackage samples are not allowed in the positive host.");
            return sample;
        }

        private static void AssertImportedSource(Context context, object sample, JArray packages)
        {
            var source = Safe((string)Property(sample, "resolvedPath"));
            var imported = Safe((string)Property(sample, "importPath"));
            var sourceFiles = TreeFiles(source).ToDictionary(p => Relative(source, p), p => Hash(p), StringComparer.Ordinal);
            var actual = TreeFiles(imported).ToArray();
            if (actual.Length != sourceFiles.Count || actual.Any(p => !sourceFiles.TryGetValue(Relative(imported, p), out var sha) || Hash(p) != sha))
                throw new InvalidOperationException("Imported Sample is not the exact installed package Sample source/meta bytes.");
            if (sourceFiles.Count == 0 || !sourceFiles.Keys.Any(p => p.EndsWith(".cs", StringComparison.Ordinal))) throw new InvalidOperationException("Empty Sample source cannot prove an import.");
            var manifest = Read(Path.Combine(Text(packages.Cast<JObject>().Single(p => Text(p, "name") == Text(context.request, "entryPackage")), "resolvedPath"), "package.json"));
            if (!Array(manifest, "samples").Cast<JObject>().Any(s => Text(s, "displayName") == SampleName(context) && Text(s, "path") == SampleRelative(context))) throw new InvalidOperationException("The actual package did not declare this exact Sample.");
        }

        private static JObject SampleIdentity(Context context, object sample, JArray packages) => new JObject
        {
            ["name"] = SampleName(context), ["resolvedPath"] = (string)Property(sample, "resolvedPath"), ["importPath"] = (string)Property(sample, "importPath"),
            ["api"] = "UnityEditor.PackageManager.UI.Sample", ["apiAssembly"] = sample.GetType().Assembly.FullName,
            ["package"] = packages.Cast<JObject>().Single(p => Text(p, "name") == Text(context.request, "entryPackage")).DeepClone()
        };

        private static JArray CapturePackages(Context context)
        {
            var request = context.request; var manifest = Read(Path.Combine(Text(request, "hostRoot"), "Packages/manifest.json"));
            var lockFile = Read(Path.Combine(Text(request, "hostRoot"), "Packages/packages-lock.json"));
            var expected = Closure(context); var registered = PackageInfo.GetAllRegisteredPackages();
            var rows = new JArray(); var files = new JArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            if (registered.Length == 0 || Object(lockFile, "dependencies").Count != registered.Length) throw new InvalidOperationException("Missing actual registered/locked package graph.");
            foreach (var package in registered.OrderBy(p => p.name, StringComparer.Ordinal))
            {
                if (package == null || !names.Add(package.name) || (package.errors != null && package.errors.Length != 0) || package.dependencies == null)
                    throw new InvalidOperationException("Invalid/duplicate/errored actual PackageInfo.");
                var own = expected.SingleOrDefault(p => Text(p, "id") == package.name);
                var sdk = package.name == "com.code-philosophy.hybridclr" && Hybrid(context);
                if (own == null && !sdk && !package.name.StartsWith("com.unity.", StringComparison.Ordinal)) throw new InvalidOperationException("Unauthorized installed package in the positive host.");
                var deps = new JObject(); foreach (var d in package.dependencies.OrderBy(d => d.name, StringComparer.Ordinal)) deps.Add(d.name, d.version);
                var locked = Object(Object(lockFile, "dependencies"), package.name);
                if (!Same(deps, Object(locked, "dependencies")) || package.isDirectDependency != (Object(manifest, "dependencies").Property(package.name) != null)) throw new InvalidOperationException("Actual direct/dependency graph differs from the minimal manifest/lock.");
                if (own != null || sdk)
                {
                    var registry = sdk || Text(request, "packageOrigin") == "Registry";
                    var locator = registry ? (own != null ? Text(own, "version") : "9.0.3") : Text(Object(manifest, "dependencies"), package.name);
                    if (package.source.ToString() != (registry ? "Registry" : "LocalTarball") || package.packageId.Replace('\\', '/') != package.name + "@" + locator ||
                        package.version != (own != null ? Text(own, "version") : "9.0.3") || Text(locked, "source") != (registry ? "registry" : "local-tarball") || Text(locked, "version") != locator ||
                        (registry && (package.registry?.url != Text(request, "resolutionRegistryUrl") || Text(locked, "url") != Text(request, "resolutionRegistryUrl"))) || (sdk && package.isDirectDependency))
                        throw new InvalidOperationException("Actual own package/SDK source, version, directness or locator differs.");
                    var root = Safe(package.resolvedPath);
                    if (!Inside(root, Path.Combine(Text(request, "hostRoot"), "Library/PackageCache")) && !Inside(root, Text(request, "cacheRoot"))) throw new InvalidOperationException("Package source is outside the owned installation/cache.");
                    var inventory = TreeFiles(root).OrderBy(p => Relative(root, p), StringComparer.Ordinal).Select(p => new JObject { ["path"] = Relative(root, p), ["sha256"] = Hash(p) }).ToArray();
                    if (own != null)
                    {
                        var frozen = Array(own, "files").Cast<JObject>().ToArray();
                        if (inventory.Length != frozen.Length) throw new InvalidOperationException("Installed package content differs from the frozen artifact file set.");
                        foreach (var file in frozen)
                        {
                            var relative = Text(file, "path"); var actual = Child(root, relative);
                            if (ContentHash(actual, relative, !registry) != Text(file, "contentSha256")) throw new InvalidOperationException("Installed package/Sample content differs from the frozen artifact: " + relative);
                        }
                        if (!Same(deps, Object(own, "dependencies"))) throw new InvalidOperationException("Frozen package dependencies differ from the actual package.");
                    }
                    files.Add(new JObject { ["package"] = package.name, ["resolvedPath"] = root, ["files"] = new JArray(inventory) });
                }
                rows.Add(new JObject { ["name"] = package.name, ["version"] = package.version, ["source"] = package.source.ToString(), ["packageId"] = package.packageId,
                    ["resolvedPath"] = package.resolvedPath, ["isDirectDependency"] = package.isDirectDependency, ["registryUrl"] = package.registry?.url, ["dependencies"] = deps });
            }
            if (expected.Any(p => !names.Contains(Text(p, "id"))) || (Hybrid(context) && !names.Contains("com.code-philosophy.hybridclr"))) throw new InvalidOperationException("The actual package closure is incomplete.");
            context.packageFiles = files;
            if (Text(request, "phase") != "Inspect")
            {
                var initial = Read(Path.Combine(Text(request, "authoringRoot"), "Inspect/report.json"));
                if (!Same(rows, initial["packages"]) || !Same(files, initial["packageFiles"])) throw new InvalidOperationException("Sample/authoring phase changed actual package identity or installation bytes.");
            }
            return rows;
        }

        private static JObject Receipt(Context context, string purpose, string root, bool generated)
        {
            var request = context.request; var receipt = new JObject { ["schemaVersion"] = 1, ["purpose"] = purpose };
            foreach (var field in new[] { "workspaceId", "hostId", "cacheId", "resultId", "inputHash", "entryPackage", "packageOrigin", "releaseManifestSha256" }) receipt[field] = request[field].DeepClone();
            receipt["sourceCommit"] = Text(context.release, "sourceCommit");
            receipt["unityVersion"] = Application.unityVersion;
            receipt["unityProductVersion"] = FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion;
            var snapshot = Snapshot(context, root, generated);
            receipt["files"] = snapshot["files"]; receipt["directories"] = snapshot["directories"];
            return receipt;
        }

        private static JObject Snapshot(Context context, string relative, bool generated)
        {
            var host = Text(context.request, "hostRoot"); var root = Child(host, relative);
            var directories = new List<string> { root }; directories.AddRange(TreeDirectories(root));
            if (generated) directories.Add(Child(host, "Assets/YLFrameworkData"));
            var files = new JArray(TreeFiles(root).OrderBy(p => p, StringComparer.Ordinal).Select(path =>
            {
                var asset = path.EndsWith(".meta", StringComparison.Ordinal) ? path.Substring(0, path.Length - 5) : path;
                var assetRelative = Relative(host, asset); var meta = asset + ".meta";
                var guid = GuidAt(meta); var type = AssetDatabase.GetMainAssetTypeAtPath(assetRelative)?.FullName;
                if (string.IsNullOrEmpty(type) || AssetDatabase.AssetPathToGUID(assetRelative) != guid) throw new InvalidOperationException("No real AssetDatabase type/GUID for authored/imported bytes: " + assetRelative);
                return new JObject { ["path"] = Relative(host, path), ["sha256"] = Hash(path), ["guid"] = guid, ["type"] = type, ["kind"] = path.EndsWith(".meta", StringComparison.Ordinal) ? "Meta" : "Asset" };
            }));
            var directoryRows = new JArray(directories.OrderBy(p => p, StringComparer.Ordinal).Select(path =>
            {
                var assetRelative = Relative(host, path); var guid = GuidAt(path + ".meta");
                if (AssetDatabase.GetMainAssetTypeAtPath(assetRelative)?.FullName != "UnityEditor.DefaultAsset" || AssetDatabase.AssetPathToGUID(assetRelative) != guid) throw new InvalidOperationException("No actual imported directory/meta identity.");
                return new JObject { ["path"] = assetRelative, ["guid"] = guid, ["metaSha256"] = Hash(path + ".meta"), ["type"] = "UnityEditor.DefaultAsset" };
            }));
            if (files.Count == 0) throw new InvalidOperationException("Empty output cannot prove authoring execution.");
            return new JObject { ["files"] = files, ["directories"] = directoryRows };
        }

        private static Context Validate(string[] args, bool resume)
        {
            var requestPath = Safe(Argument(args, Flag)); var request = Read(requestPath);
            var context = new Context { arguments = args, request = request, requestPath = requestPath, requestHash = Hash(requestPath) };
            Authority(context);
            var phase = Text(request, "phase");
            var pending = Path.Combine(Text(request, "output"), "import.json");
            if (resume)
            {
                context.import = Read(pending);
                if (phase != "Import" || Text(context.import, "requestSha256") != context.requestHash || Integer(context.import, "pid") != Process.GetCurrentProcess().Id ||
                    Text(context.import, "api") != "UnityEditor.PackageManager.UI.Sample" || Text(context.import, "options") != "None" ||
                    context.import["callIssued"]?.Type != JTokenType.Boolean || !(bool)context.import["callIssued"] || context.import["returnObserved"]?.Type != JTokenType.Boolean ||
                    ((bool)context.import["returnObserved"] ? context.import["returnValue"]?.Type != JTokenType.Boolean || !(bool)context.import["returnValue"] : context.import["returnValue"]?.Type != JTokenType.Null))
                    throw new InvalidOperationException("Import reload state is not bound to this owned request/process or records an actual failed return.");
                context.import["completion"] = (bool)context.import["returnObserved"] ? "ImportReturn" : "ImportedAfterDomainReloadNotObservedReturn";
            }
            else if (File.Exists(Safe(pending))) throw new InvalidOperationException("Do not adopt or reissue an existing Samples import.");
            AssertAssets(context, true);
            return context;
        }

        private static void Authority(Context context)
        {
            var request = context.request; var args = context.arguments;
            if (Hash(context.requestPath) != context.requestHash || Integer(request, "schemaVersion") != 1 || Text(request, "purpose") != "YLTesting.RegistryAuthoring" ||
                !new[] { "Inspect", "Import", "Author", "Reopen" }.Contains(Text(request, "phase")) || !new[] { "Registry", "LocalTarball" }.Contains(Text(request, "packageOrigin")) ||
                !new[] { "com.kano.ylframework", "com.kano.ylframework.hybridclr" }.Contains(Text(request, "entryPackage")) || Text(request, "resolutionRegistryUrl") != "https://package.openupm.com") throw new InvalidOperationException("Invalid explicit authoring request/source/phase.");
            if (Text(request, "packageOrigin") == "Registry" ? Text(request, "registryUrl") != Text(request, "resolutionRegistryUrl") || Text(request, "registryVersion") != "0.1.0-preview.1" :
                request["registryUrl"]?.Type != JTokenType.Null || request["registryVersion"]?.Type != JTokenType.Null) throw new InvalidOperationException("Own package Registry provenance must not be inferred from SDK/scoped Registry routing.");
            if (!Application.isBatchMode || !args.Contains("-batchmode") || args.Contains("-quit")) throw new InvalidOperationException("Interactive/non-batch requests have no Editor exit authority.");
            foreach (var field in new[] { "workspaceId", "hostId", "resultId", "cacheId", "requestId" }) Pattern(Text(request, field), "^[0-9a-f]{32}$");
            foreach (var field in new[] { "inputHash", "inputReceiptSha256", "originalManifestSha256", "releaseManifestSha256" }) Pattern(Text(request, field), "^[0-9a-f]{64}$");
            var host = Safe(Text(request, "hostRoot")); var workspaceRoot = Safe(Text(request, "workspaceRoot"));
            EqualPath(Path.GetDirectoryName(Application.dataPath), host); EqualPath(Argument(args, "-projectPath"), host);
            if (Argument(args, "-executeMethod") != "YLFramework.Testing.RegistryAuthoringEntry.Execute") throw new InvalidOperationException("Wrong authoring execute method.");
            EqualPath(EditorApplication.applicationPath, Text(request, "unityExecutable"), true);
            if (!new[] { "2022.3.62f3", "6000.3.27f1" }.Contains(Application.unityVersion) || Application.unityVersion != Text(request, "unityVersion") ||
                FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion != Text(request, "unityProductVersion")) throw new InvalidOperationException("The actual exact Editor differs from the owned request.");
            var workspace = Read(Path.Combine(workspaceRoot, ".yl-workspace.json"));
            if (Integer(workspace, "schemaVersion") != 1 || Text(workspace, "purpose") != "YLTesting.Workspace" || Text(workspace, "id") != Text(request, "workspaceId")) throw new InvalidOperationException("Wrong workspace owner.");
            EqualPath(Text(workspace, "root"), workspaceRoot); EqualPath(Text(workspace, "repository"), Text(request, "repository"));
            Owner(request, host, "Host", "hostId"); Owner(request, Text(request, "cacheRoot"), "Cache", "cacheId"); Owner(request, Text(request, "resultRoot"), "Result", "resultId");
            var root = Safe(Text(request, "authoringRoot")); EqualPath(Path.GetDirectoryName(root), Text(request, "resultRoot"));
            Pattern(Path.GetFileName(root), "^RegistryAuthoring-[0-9a-f]{32}$");
            EqualPath(Text(request, "output"), Path.Combine(root, Text(request, "phase"))); EqualPath(context.requestPath, Path.Combine(Text(request, "output"), "request.json"));
            if (!Directory.Exists(Safe(Text(request, "output"))) || File.Exists(Safe(Path.Combine(Text(request, "output"), "report.json")))) throw new InvalidOperationException("Preserve existing/unknown authoring output.");
            foreach (var pair in new[] { new[] { "releaseManifest", "release-manifest.json", "releaseManifestSha256" }, new[] { "originalManifest", "manifest.before.json", "originalManifestSha256" } })
            {
                EqualPath(Text(request, pair[0]), Path.Combine(root, pair[1]));
                if (Hash(Text(request, pair[0])) != Text(request, pair[2])) throw new InvalidOperationException("Frozen authoring release/manifest changed.");
            }
            context.release = Read(Text(request, "releaseManifest"));
            if (Integer(context.release, "schemaVersion") != 1 || Array(context.release, "packages").Count != 5) throw new InvalidOperationException("Invalid frozen five-package release.");
            Pattern(Text(context.release, "sourceCommit"), "^[0-9a-f]{40}$");
            var expectedManifest = new JObject { ["dependencies"] = new JObject() };
            foreach (var package in Closure(context))
            {
                var id = Text(package, "id");
                if (Text(request, "packageOrigin") == "Registry") { if (id == Text(request, "entryPackage")) ((JObject)expectedManifest["dependencies"])[id] = Text(package, "version"); }
                else ((JObject)expectedManifest["dependencies"])[id] = Text(Object(Read(Text(request, "originalManifest")), "dependencies"), id);
            }
            expectedManifest["scopedRegistries"] = new JArray(new JObject { ["name"] = "OpenUPM", ["url"] = "https://package.openupm.com",
                ["scopes"] = Hybrid(context) ? new JArray("com.kano.ylframework", "com.code-philosophy.hybridclr") : new JArray("com.kano.ylframework") });
            if (!Same(Read(Text(request, "originalManifest")), expectedManifest) || Hash(Path.Combine(host, "Packages/manifest.json")) != Text(request, "originalManifestSha256")) throw new InvalidOperationException("The original minimal manifest changed or prefilled official/SDK dependencies.");
            foreach (var property in Object(expectedManifest, "dependencies").Properties())
                if (Text(request, "packageOrigin") == "LocalTarball" && (property.Value.Type != JTokenType.String || !((string)property.Value).StartsWith("file:", StringComparison.Ordinal) || !((string)property.Value).EndsWith(".tgz", StringComparison.Ordinal))) throw new InvalidOperationException("LocalTarball requires actual tgz locators, not unpacked source.");
            var inputPath = Path.Combine(host, ".yl-inputs.json");
            if (Hash(inputPath) != Text(request, "inputReceiptSha256")) throw new InvalidOperationException("Producer input receipt drifted.");
            context.inputs = Read(inputPath);
            if (Integer(context.inputs, "schemaVersion") != 1 || Text(context.inputs, "purpose") != "YLTesting.HostInputs" || Text(context.inputs, "hostId") != Text(request, "hostId") || Text(context.inputs, "inputHash") != Text(request, "inputHash")) throw new InvalidOperationException("Wrong producer input identity.");
            foreach (var file in Array(context.inputs, "files").Cast<JObject>())
                if (!string.Equals(Hash(Child(host, Text(file, "path"))), Text(file, "sha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Producer input bytes changed.");
            if (Text(request, "assetDirectory") != "Assets/YLFrameworkData/" + Path.GetFileName(root)) throw new InvalidOperationException("Generated data escaped its planned Assets/YLFrameworkData directory.");
            EqualPath(Text(request, "baselineDirectory"), Path.Combine(host, "RegistryAuthoringBaseline"));
            if (Directory.Exists(Safe(Text(request, "baselineDirectory"))) || File.Exists(Safe(Text(request, "baselineDirectory")))) throw new InvalidOperationException("Native baseline/build output is outside this asset-only operation.");
            foreach (var pair in new[] { new[] { "UPM_CACHE_ROOT", "Upm" }, new[] { "UPM_CACHE_PATH", "Upm/packages" }, new[] { "UPM_NPM_CACHE_PATH", "Upm/npm" } }) EqualPath(Environment.GetEnvironmentVariable(pair[0]), Path.Combine(Text(request, "cacheRoot"), pair[1]));
            if (Text(request, "phase") != "Inspect")
            {
                var initial = Path.Combine(root, "Inspect/report.json");
                if (Hash(initial) != Text(request, "initialReportSha256")) throw new InvalidOperationException("The initial actual installation evidence changed.");
            }
        }

        private static void AssertAssets(Context context, bool initial)
        {
            var request = context.request; var host = Text(request, "hostRoot"); var phase = Text(request, "phase");
            var allowed = new HashSet<string>(Array(context.inputs, "files").Cast<JObject>().Where(f => Text(f, "path").StartsWith("Assets/", StringComparison.Ordinal)).Select(f => Child(host, Text(f, "path"))), StringComparer.OrdinalIgnoreCase);
            var allowedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in allowed)
                for (var directory = Path.GetDirectoryName(file); Inside(directory, Child(host, "Assets")); directory = Path.GetDirectoryName(directory)) allowedDirectories.Add(directory);
            foreach (var spec in new[] { new[] { ".yl-samples.json", "sampleReceiptSha256", "YLTesting.Samples", "Assets/Samples" }, new[] { ".yl-authoring-outputs.json", "managedReceiptSha256", "YLTesting.RegistryAuthoring.Outputs", Text(request, "assetDirectory") } })
            {
                var path = Child(host, spec[0]); var expected = spec[1] == "sampleReceiptSha256" ? context.sampleHash : context.managedHash;
                if (expected == null) expected = Text(request, spec[1]);
                if (expected == "")
                {
                    if (File.Exists(path)) throw new InvalidOperationException("Preserve an unknown pre-existing authoring receipt/output.");
                    continue;
                }
                if (Hash(path) != expected) throw new InvalidOperationException("Imported/generated receipt bytes changed.");
                var receipt = Read(path);
                foreach (var field in new[] { "workspaceId", "hostId", "cacheId", "resultId", "inputHash", "entryPackage", "packageOrigin", "releaseManifestSha256" })
                    if (Text(receipt, field) != Text(request, field)) throw new InvalidOperationException("Sample/generated receipt ownership/source changed.");
                if (Integer(receipt, "schemaVersion") != 1 || Text(receipt, "purpose") != spec[2]) throw new InvalidOperationException("Invalid managed authoring receipt.");
                var snapshot = Snapshot(context, spec[3], spec[0] != ".yl-samples.json");
                if (!Same(snapshot["files"], receipt["files"]) || !Same(snapshot["directories"], receipt["directories"])) throw new InvalidOperationException("Imported/generated actual bytes/meta/GUID/type changed.");
                foreach (var file in Array(receipt, "files").Cast<JObject>()) allowed.Add(Child(host, Text(file, "path")));
                foreach (var directory in Array(receipt, "directories").Cast<JObject>())
                {
                    allowedDirectories.Add(Child(host, Text(directory, "path")));
                    allowed.Add(Child(host, Text(directory, "path") + ".meta"));
                }
            }
            string importing = null;
            if (context.import != null && phase == "Import")
            {
                importing = Safe((string)Property(FindSample(context, CapturePackages(context)), "importPath"));
                for (var directory = importing; Inside(directory, Child(host, "Assets")); directory = Path.GetDirectoryName(directory))
                { allowedDirectories.Add(directory); allowed.Add(directory + ".meta"); }
            }
            foreach (var file in TreeFiles(Child(host, "Assets")))
            {
                if (allowed.Contains(file)) continue;
                if (importing != null && Inside(file, importing)) continue;
                if (!initial && context.authorIssued && (Inside(file, Child(host, Text(request, "assetDirectory"))) || file == Child(host, Text(request, "assetDirectory") + ".meta") ||
                    file == Child(host, "Assets/YLFrameworkData.meta") || Inside(file, Child(host, "Assets/YLResourceStartupExample")) || file == Child(host, "Assets/YLResourceStartupExample.meta"))) continue;
                throw new InvalidOperationException("Unknown Assets content must not be adopted, overwritten or deleted: " + Relative(host, file));
            }
            foreach (var directory in TreeDirectories(Child(host, "Assets")))
            {
                if (allowedDirectories.Contains(directory) || (importing != null && Inside(directory, importing))) continue;
                if (!initial && context.authorIssued && (directory == Child(host, "Assets/YLFrameworkData") || directory == Child(host, Text(request, "assetDirectory")) ||
                    Inside(directory, Child(host, Text(request, "assetDirectory"))) || directory == Child(host, "Assets/YLResourceStartupExample") || Inside(directory, Child(host, "Assets/YLResourceStartupExample")))) continue;
                throw new InvalidOperationException("Unknown empty/user directory must not be adopted or overwritten: " + Relative(host, directory));
            }
            if (initial && phase == "Author" && (Directory.Exists(Child(host, "Assets/YLFrameworkData")) || File.Exists(Child(host, "Assets/YLFrameworkData.meta")))) throw new InvalidOperationException("Preserve unknown output directory.");
        }

        private static void Finish(string status, string error, JArray packages, JObject sample, JArray assemblies, JObject authoring)
        {
            EditorApplication.update -= Tick;
            // Revalidate all authority before both successful and failed exits. Invalid owners never gain Exit permission.
            Authority(current); AssertAssets(current, false);
            if (status == "Completed") packages = CapturePackages(current);
            var request = current.request; var report = new JObject { ["schemaVersion"] = 1, ["status"] = status, ["error"] = error };
            foreach (var field in new[] { "purpose", "phase", "requestId", "workspaceId", "hostId", "cacheId", "resultId", "inputHash", "entryPackage", "packageOrigin", "releaseManifestSha256", "assetDirectory" }) report[field] = request[field].DeepClone();
            report["registryUrl"] = request["registryUrl"].DeepClone(); report["registryVersion"] = request["registryVersion"].DeepClone();
            report["resolutionRegistryUrl"] = Text(request, "resolutionRegistryUrl");
            report["requestSha256"] = current.requestHash; report["pid"] = Process.GetCurrentProcess().Id;
            report["unityExecutable"] = EditorApplication.applicationPath; report["unityVersion"] = Application.unityVersion;
            report["unityProductVersion"] = FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion;
            report["isBatchMode"] = Application.isBatchMode; report["scriptCompilationFailed"] = EditorUtility.scriptCompilationFailed;
            report["manifestSha256"] = Hash(Path.Combine(Text(request, "hostRoot"), "Packages/manifest.json")); report["lockSha256"] = Hash(Path.Combine(Text(request, "hostRoot"), "Packages/packages-lock.json"));
            report["cacheEnvironment"] = new JObject(); foreach (var key in new[] { "UPM_CACHE_ROOT", "UPM_CACHE_PATH", "UPM_NPM_CACHE_PATH" }) report["cacheEnvironment"][key] = Environment.GetEnvironmentVariable(key);
            report["packages"] = packages; report["packageFiles"] = current.packageFiles; report["sample"] = sample; report["sampleAssemblies"] = assemblies; report["authoring"] = authoring;
            report["sampleReceiptSha256"] = current.sampleHash ?? Text(request, "sampleReceiptSha256"); report["managedReceiptSha256"] = current.managedHash ?? Text(request, "managedReceiptSha256");
            report["boundaries"] = new JObject { ["native"] = "NotRun", ["playerHotUpdate"] = "NotVerified", ["warmReopen"] = "SameVersionNotUpgrade" };
            SaveNew(Path.Combine(Text(request, "output"), "report.json"), report);
            EditorApplication.Exit(status == "Completed" ? 0 : 1);
        }

        private static JObject[] Closure(Context context)
        {
            var all = Array(context.release, "packages").Cast<JObject>().ToArray(); var pending = new Queue<string>(); pending.Enqueue(Text(context.request, "entryPackage"));
            var result = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                var id = pending.Dequeue(); if (result.ContainsKey(id)) continue;
                var value = all.Single(p => Text(p, "id") == id); result.Add(id, value);
                foreach (var dependency in Object(value, "dependencies").Properties()) if (dependency.Name.StartsWith("com.kano.ylframework", StringComparison.Ordinal)) pending.Enqueue(dependency.Name);
            }
            return result.Values.ToArray();
        }

        private static void Owner(JObject request, string path, string kind, string idField)
        {
            path = Safe(path); var owner = Read(Path.Combine(path, ".yl-owner.json"));
            if (Integer(owner, "schemaVersion") != 1 || Text(owner, "purpose") != "YLTesting.Directory" || Text(owner, "kind") != kind || Text(owner, "id") != Text(request, idField) || Text(owner, "workspaceId") != Text(request, "workspaceId")) throw new InvalidOperationException("Wrong " + kind + " owner.");
            EqualPath(Text(owner, "path"), path); Pattern(Text(owner, "key"), "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$");
            var expected = Path.Combine(Text(request, "workspaceRoot"), kind == "Host" ? "Hosts" : kind == "Cache" ? "Cache" : "Results", Text(owner, "key"));
            if (kind == "Result") expected = Path.Combine(expected, Path.GetFileName(path));
            EqualPath(expected, path);
            if (!File.Exists(Safe(Path.Combine(path, ".lease")))) throw new InvalidOperationException("Missing existing owner lease.");
        }

        private static string ContentHash(string path, string relative, bool localTarball)
        {
            if (relative != "package.json") return Hash(path);
            var manifest = Read(path); var fingerprint = manifest.Property("_fingerprint");
            if (fingerprint != null) { Pattern(Text(manifest, "_fingerprint"), localTarball ? "^[0-9a-fA-F]{32}$" : "^[0-9a-fA-F]{40}$"); fingerprint.Remove(); }
            if (manifest.Properties().Any(p => p.Name.StartsWith("_", StringComparison.Ordinal))) throw new InvalidOperationException("Unknown package-manager metadata.");
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(manifest).ToString(Formatting.None))));
        }
        private static JToken Canonical(JToken value) => value is JObject obj ? new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value)))) :
            value is JArray array ? new JArray(array.Select(Canonical)) : value.DeepClone();
        private static bool Same(JToken left, JToken right) => left != null && right != null && JToken.DeepEquals(Canonical(left), Canonical(right));
        private static bool Hybrid(Context context) => Text(context.request, "entryPackage") == "com.kano.ylframework.hybridclr";
        private static string SampleName(Context context) => Hybrid(context) ? "HybridCLR Module" : "Resource Startup";
        private static string SampleRelative(Context context) => Hybrid(context) ? "Samples~/HybridCLR" : "Samples~/ResourceStartup";
        private static object Property(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) ?? throw new InvalidOperationException("Missing real Samples API property: " + name);
        private static string ResolveSource(Context context, string value) => Safe(Path.IsPathRooted(value) ? value : Child(Text(context.request, "hostRoot"), value));
        private static string GuidAt(string meta)
        {
            var matches = Regex.Matches(File.ReadAllText(Safe(meta)), "(?m)^guid: ([0-9a-f]{32})\\r?$");
            if (matches.Count != 1 || matches[0].Groups[1].Value == new string('0', 32)) throw new InvalidOperationException("Missing/invalid actual asset meta GUID.");
            return matches[0].Groups[1].Value;
        }
        private static string Argument(string[] args, string name)
        {
            var indices = Enumerable.Range(0, args.Length).Where(i => args[i] == name).ToArray();
            if (indices.Length != 1 || indices[0] + 1 >= args.Length || string.IsNullOrWhiteSpace(args[indices[0] + 1]) || args[indices[0] + 1].StartsWith("-", StringComparison.Ordinal)) throw new InvalidOperationException("An explicit unique request/argument is required: " + name);
            return args[indices[0] + 1];
        }
        private static string Safe(string path, bool readOnly = false)
        {
            if (string.IsNullOrWhiteSpace(path) || !Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]") || path.Substring(2).Contains(":")) throw new InvalidOperationException("An explicit local absolute path is required.");
            foreach (var part in path.Substring(3).Split(new[] { '/', '\\' }))
                if (part == "." || part == ".." || part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal) || Regex.IsMatch(part, "[<>\"|?*\\x00-\\x1f]|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)")) throw new InvalidOperationException("Ambiguous authoring path.");
            var full = Path.GetFullPath(path).TrimEnd('/', '\\');
            for (var cursor = full; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            {
                try { if (!readOnly && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked authoring path is refused."); }
                catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            }
            return full;
        }
        private static string Child(string root, string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Contains('\\')) throw new InvalidOperationException("Canonical relative managed paths are required.");
            var path = Safe(Path.Combine(root, relative)); if (!Inside(path, Safe(root))) throw new InvalidOperationException("Managed path escaped its owner."); return path;
        }
        private static bool Inside(string path, string root) => Safe(path).StartsWith(Safe(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string Relative(string root, string path) => path.Substring(root.Length + 1).Replace('\\', '/');
        private static IEnumerable<string> TreeFiles(string root)
        {
            foreach (var path in Directory.GetFileSystemEntries(Safe(root))) { var safe = Safe(path); if (Directory.Exists(safe)) { foreach (var file in TreeFiles(safe)) yield return file; } else yield return safe; }
        }
        private static IEnumerable<string> TreeDirectories(string root)
        {
            foreach (var path in Directory.GetDirectories(Safe(root))) { var safe = Safe(path); yield return safe; foreach (var directory in TreeDirectories(safe)) yield return directory; }
        }
        private static void EqualPath(string left, string right, bool readOnly = false) { if (!string.Equals(Safe(left, readOnly), Safe(right, readOnly), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Authoring owner/Editor/output/cache path mismatch."); }
        private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(Safe(path))) return Hex(sha.ComputeHash(stream)); }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        private static void Pattern(string value, string pattern) { if (!Regex.IsMatch(value, pattern)) throw new InvalidOperationException("Invalid authoring identity/hash."); }
        private static string Text(JObject value, string field) => value[field]?.Type == JTokenType.String ? (string)value[field] : throw new InvalidOperationException("String field required: " + field);
        private static long Integer(JObject value, string field) => value[field]?.Type == JTokenType.Integer ? (long)value[field] : throw new InvalidOperationException("Integer field required: " + field);
        private static JObject Object(JObject value, string field) => value[field] as JObject ?? throw new InvalidOperationException("Object field required: " + field);
        private static JArray Array(JObject value, string field) => value[field] as JArray ?? throw new InvalidOperationException("Array field required: " + field);
        private static JObject Read(string path)
        {
            path = Safe(path); if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidOperationException("Authoring JSON size limit exceeded.");
            var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
            const string tokens = @"""(?:[^""\\\x00-\x1f]|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|true|false|null|[{}\[\]:,]|[ \t\r\n]+";
            int offset = 0; string previous = ""; bool previousAtom = false;
            foreach (Match match in Regex.Matches(text, tokens))
            {
                if (match.Index != offset) throw new InvalidOperationException("Non-JSON authoring input syntax.");
                offset += match.Length; var token = match.Value; if (string.IsNullOrWhiteSpace(token)) continue;
                bool atom = token[0] == '"' || token[0] == '-' || char.IsDigit(token[0]) || token == "true" || token == "false" || token == "null";
                if (((token == "}" || token == "]") && previous == ",") || (token == ":" && !previous.StartsWith("\"", StringComparison.Ordinal)) || (atom && previousAtom)) throw new InvalidOperationException("Non-JSON authoring input separators.");
                previous = token; previousAtom = atom;
            }
            if (offset != text.Length) throw new InvalidOperationException("Non-JSON authoring input syntax.");
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
            {
                var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidOperationException("Trailing authoring JSON is refused."); return value;
            }
        }
        private static void SaveNew(string path, JObject value)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value.ToString(Formatting.Indented));
            using (var stream = new FileStream(Safe(path), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        private static void ReplaceImport(Context context)
        {
            var path = Safe(Path.Combine(Text(context.request, "output"), "import.json")); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            SaveNew(temp, context.import); File.Replace(temp, path, null);
        }
        private sealed class Context
        {
            public string[] arguments;
            public JObject request, release, inputs, import;
            public JArray packageFiles;
            public string requestPath, requestHash, sampleHash, managedHash;
            public bool authorIssued;
        }
    }
}
#endif
