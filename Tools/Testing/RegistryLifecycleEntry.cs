#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Testing
{
    // This assembly has no product, Sample, PlayerSmoke, Json.NET or optional SDK references.
    public static class RegistryLifecycleEntry
    {
        private const string RequestFlag = "-ylRegistryLifecycleRequest";
        private static Context current;
        private static ListRequest listing;
        private static RemoveRequest removing;
        private static AddAndRemoveRequest changing;
        private static Operation operation;
        private static int stableTicks;

        public static void Execute() => Begin(Environment.GetCommandLineArgs());

        private static void Begin(string[] arguments)
        {
            // Validation intentionally sits outside the owned failure/Exit handler.
            var validated = ValidateInvocation(arguments, false);
            Start(validated, null);
        }

        [InitializeOnLoadMethod]
        private static void OnReload()
        {
            var arguments = Environment.GetCommandLineArgs();
            if (!arguments.Contains(RequestFlag)) return;
            EditorApplication.delayCall += () =>
            {
                var path = Argument(arguments, RequestFlag);
                var request = Read(path);
                if (!IsMutation(Text(request, "phase"))) return;
                var state = SafePath(Path.Combine(Text(request, "output"), "operation.json"));
                if (File.Exists(state)) Resume(arguments);
            };
        }

        private static void Resume(string[] arguments)
        {
            var validated = ValidateInvocation(arguments, true);
            var saved = ReadOperation(validated);
            if (!saved.issued || (saved.stage != saved.requestKind + "Issued" && saved.stage != saved.requestKind + "Succeeded"))
                throw new InvalidOperationException("Indeterminate removal stage; do not reissue or claim success.");
            Start(validated, saved);
        }

        private static void Start(Context context, Operation saved)
        {
            EditorApplication.update -= Tick;
            current = context;
            listing = null;
            removing = null;
            changing = null;
            operation = saved;
            stableTicks = 0;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) { stableTicks = 0; return; }
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("The neutral host has actual compilation errors.");
                if (ValidateInvocation(current.arguments, operation != null).sha256 != current.sha256) throw new InvalidOperationException("The bound request digest changed during execution.");
                if (++stableTicks < 2) return;

                if (removing != null || changing != null)
                {
                    var completed = removing != null ? removing.IsCompleted : changing.IsCompleted;
                    if (!completed) return;
                    var status = removing != null ? removing.Status : changing.Status;
                    if (status != StatusCode.Success)
                        throw new InvalidOperationException("Client." + operation.requestKind + " failed: " + (removing != null ? removing.Error?.message : changing.Error?.message));
                    operation.stage = operation.requestKind + "Succeeded";
                    operation.requestStatus = status.ToString();
                    operation.requestStatusObserved = true;
                    operation.completion = operation.requestKind + "Request";
                    SaveOperation();
                    removing = null;
                    changing = null;
                    stableTicks = 0;
                    return;
                }
                if (listing == null) { listing = Client.List(true, true); return; }
                if (!listing.IsCompleted) return;
                if (listing.Status != StatusCode.Success)
                    throw new InvalidOperationException("Client.List failed: " + listing.Error?.message);
                var packages = Capture(listing.Result.ToArray());
                listing = null;
                var phase = Text(current.request, "phase");
                if (IsMutation(phase) && operation == null)
                {
                    AssertPackages(current, packages, current.before);
                    var remove = Object(Value(current.before, "dependencies")).Keys.Except(Object(Value(current.after, "dependencies")).Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    var add = Object(Value(current.after, "dependencies")).Keys.Except(Object(Value(current.before, "dependencies")).Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
                        .Select(id => Text(current.request, "packageOrigin") == "Registry" ? id + "@" + Text(Object(Value(current.original, "dependencies")), id) : Text(Object(Value(current.original, "dependencies")), id)).ToArray();
                    operation = new Operation
                    {
                        requestSha256 = current.sha256, hostId = Text(current.request, "hostId"), resultId = Text(current.request, "resultId"),
                        cacheId = Text(current.request, "cacheId"), entryPackage = Text(current.request, "entryPackage"), pid = Process.GetCurrentProcess().Id,
                        phase = phase, entryPackages = current.roots, removePackages = remove, addPackages = add,
                        requestKind = add.Length == 0 && remove.Length == 1 ? "Remove" : "AddAndRemove", issued = false
                    };
                    operation.stage = operation.requestKind + "Planned";
                    SaveOperation();
                    if (operation.requestKind == "Remove") removing = Client.Remove(remove[0]);
                    else changing = Client.AddAndRemove(add, remove);
                    if (removing == null && changing == null) throw new InvalidOperationException("UPM returned no asynchronous request.");
                    operation.issued = true;
                    operation.stage = operation.requestKind + "Issued";
                    SaveOperation();
                    stableTicks = 0;
                    return;
                }
                AssertManifest(current, false);
                AssertPackages(current, packages, current.after);
                if (IsMutation(phase))
                {
                    if (operation == null || !operation.issued) throw new InvalidOperationException("No bound removal was issued.");
                    // A lost async object is not an observed Request.Success. A completed List is separate evidence.
                    if (!operation.requestStatusObserved) operation.completion = "ResolvedAfterReload";
                    operation.stage = "Verified";
                    SaveOperation();
                }
                Finish("Completed", "", packages);
            }
            catch (Exception error) { Finish("Failed", error.ToString(), System.Array.Empty<PackageRow>()); }
        }

        private static PackageRow[] Capture(PackageInfo[] values)
        {
            var registered = PackageInfo.GetAllRegisteredPackages();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<PackageRow>();
            foreach (var package in values.OrderBy(p => p.name, StringComparer.Ordinal))
            {
                if (package == null || string.IsNullOrEmpty(package.name) || !names.Add(package.name) || package.errors == null || package.errors.Length != 0)
                    throw new InvalidOperationException("Invalid, duplicate or errored actual PackageInfo.");
                var installed = registered.Where(p => p.name == package.name).ToArray();
                if (installed.Length != 1 || installed[0].packageId != package.packageId || installed[0].resolvedPath != package.resolvedPath ||
                    installed[0].version != package.version || installed[0].source != package.source || installed[0].isDirectDependency != package.isDirectDependency)
                    throw new InvalidOperationException("Resolved PackageInfo is not the actually registered installation.");
                var dependencies = package.dependencies ?? throw new InvalidOperationException("Missing actual PackageInfo dependencies.");
                rows.Add(new PackageRow
                {
                    name = package.name, version = package.version, source = package.source.ToString(), packageId = package.packageId,
                    resolvedPath = package.resolvedPath, isDirectDependency = package.isDirectDependency, registryUrl = package.registry?.url,
                    dependencies = dependencies.OrderBy(d => d.name, StringComparer.Ordinal).Select(d => new DependencyRow { name = d.name, version = d.version }).ToArray()
                });
            }
            if (registered.Length != rows.Count) throw new InvalidOperationException("Registered and resolved package graph sets differ.");
            return rows.ToArray();
        }

        private static void Finish(string status, string error, PackageRow[] packages)
        {
            EditorApplication.update -= Tick;
            // A changed owner/request/input is never permission to write or exit, even in a failure path.
            if (ValidateInvocation(current.arguments, operation != null).sha256 != current.sha256) throw new InvalidOperationException("The bound request digest changed before report/exit.");
            var request = current.request;
            var report = new Report
            {
                schemaVersion = 1, purpose = Text(request, "purpose"), status = status, error = error, phase = Text(request, "phase"),
                workspaceId = Text(request, "workspaceId"), hostId = Text(request, "hostId"), resultId = Text(request, "resultId"), cacheId = Text(request, "cacheId"),
                inputHash = Text(request, "inputHash"), requestId = Text(request, "requestId"), requestSha256 = current.sha256,
                entryPackage = Text(request, "entryPackage"), entryPackages = current.roots, packageOrigin = Text(request, "packageOrigin"), releaseManifestSha256 = Text(request, "releaseManifestSha256"),
                unityExecutable = EditorApplication.applicationPath, unityVersion = Application.unityVersion,
                unityProductVersion = FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion,
                pid = Process.GetCurrentProcess().Id, isBatchMode = Application.isBatchMode, scriptCompilationFailed = EditorUtility.scriptCompilationFailed,
                manifestSha256 = Hash(Path.Combine(Text(request, "hostRoot"), "Packages/manifest.json")),
                lockSha256 = Hash(Path.Combine(Text(request, "hostRoot"), "Packages/packages-lock.json")), packages = packages, removal = operation,
                compilation = new CompilationGraph { editor = CaptureCompilation(AssembliesType.Editor), player = CaptureCompilation(AssembliesType.Player) },
                cacheEnvironment = new CacheEnvironment
                {
                    UPM_CACHE_ROOT = Environment.GetEnvironmentVariable("UPM_CACHE_ROOT"), UPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_CACHE_PATH"),
                    UPM_NPM_CACHE_PATH = Environment.GetEnvironmentVariable("UPM_NPM_CACHE_PATH")
                }
            };
            SaveNew(Path.Combine(Text(request, "output"), "report.json"), report);
            EditorApplication.Exit(status == "Completed" ? 0 : 1);
        }

        private static Context ValidateInvocation(string[] arguments, bool resume)
        {
            var requestPath = SafePath(Argument(arguments, RequestFlag));
            var request = Read(requestPath);
            var fields = new[] { "schemaVersion", "purpose", "requestId", "packageOrigin", "phase", "workspaceRoot", "workspaceId", "repository", "hostRoot", "hostId",
                "cacheRoot", "cacheId", "resultRoot", "resultId", "lifecycleRoot", "output", "unityExecutable", "unityVersion", "unityProductVersion", "inputHash",
                "inputReceiptSha256", "neutralReceipt", "neutralReceiptSha256", "originalManifest", "originalManifestSha256", "releaseManifest", "releaseManifestSha256", "entryPackage", "registryUrl" };
            var optional = new[] { "entryPackages", "verifyCombinations" };
            if (request.Count < fields.Length || request.Keys.Any(k => !fields.Contains(k) && !optional.Contains(k)) || Number(request, "schemaVersion") != 1)
                throw new InvalidOperationException("Invalid lifecycle request fields/schema.");
            foreach (var name in fields.Where(f => f != "schemaVersion")) Text(request, name);
            var phase = Text(request, "phase");
            var combinations = request.ContainsKey("verifyCombinations") && (Value(request, "verifyCombinations") is bool flag ? flag : throw new InvalidOperationException("Lifecycle boolean field required: verifyCombinations"));
            if (Text(request, "purpose") != "YLTesting.RegistryLifecycle" || !new[] { "Registry", "LocalTarball" }.Contains(Text(request, "packageOrigin")) ||
                Text(request, "registryUrl") != "https://package.openupm.com")
                throw new InvalidOperationException("Lifecycle requires an explicit Registry phase/source.");
            if (!arguments.Contains("-batchmode") || arguments.Contains("-quit") || !Application.isBatchMode)
                throw new InvalidOperationException("Only explicit batch lifecycle requests have process exit authority.");
            foreach (var name in new[] { "workspaceId", "hostId", "cacheId", "resultId", "requestId" }) Pattern(Text(request, name), "^[0-9a-f]{32}$");
            foreach (var name in new[] { "inputHash", "inputReceiptSha256", "neutralReceiptSha256", "originalManifestSha256", "releaseManifestSha256" }) Pattern(Text(request, name), "^[0-9a-f]{64}$");
            var root = SafePath(Text(request, "workspaceRoot"));
            var host = SafePath(Text(request, "hostRoot"));
            var result = SafePath(Text(request, "resultRoot"));
            var cache = SafePath(Text(request, "cacheRoot"));
            EqualPath(Path.GetDirectoryName(Application.dataPath), host);
            EqualPath(Argument(arguments, "-projectPath"), host);
            if (Argument(arguments, "-executeMethod") != "YLFramework.Testing.RegistryLifecycleEntry.Execute") throw new InvalidOperationException("Wrong explicit lifecycle execute method.");
            EqualPath(EditorApplication.applicationPath, Text(request, "unityExecutable"), true);
            if (Application.unityVersion != Text(request, "unityVersion") || FileVersionInfo.GetVersionInfo(EditorApplication.applicationPath).ProductVersion != Text(request, "unityProductVersion"))
                throw new InvalidOperationException("The actual exact Editor differs from the request.");
            var workspace = Read(Path.Combine(root, ".yl-workspace.json"));
            if (Number(workspace, "schemaVersion") != 1 || Text(workspace, "purpose") != "YLTesting.Workspace" || Text(workspace, "id") != Text(request, "workspaceId"))
                throw new InvalidOperationException("Invalid workspace owner.");
            EqualPath(Text(workspace, "root"), root);
            EqualPath(Text(workspace, "repository"), Text(request, "repository"));
            Owner(request, host, "Host", "hostId"); Owner(request, cache, "Cache", "cacheId"); Owner(request, result, "Result", "resultId");
            var lifecycle = SafePath(Text(request, "lifecycleRoot"));
            if (Path.GetDirectoryName(lifecycle) != result || !Regex.IsMatch(Path.GetFileName(lifecycle), "^RegistryLifecycle-[0-9a-f]{32}$"))
                throw new InvalidOperationException("Lifecycle output is outside its run owner.");
            var output = SafePath(Text(request, "output"));
            EqualPath(output, Path.Combine(lifecycle, phase));
            EqualPath(requestPath, Path.Combine(output, "request.json"));
            if (!Directory.Exists(output) || File.Exists(SafePath(Path.Combine(output, "report.json")))) throw new InvalidOperationException("Preserve existing lifecycle output/report.");
            foreach (var pair in new[] { new[] { "releaseManifest", "release-manifest.json", "releaseManifestSha256" },
                new[] { "originalManifest", "manifest.before.json", "originalManifestSha256" }, new[] { "neutralReceipt", "neutral-inputs.json", "neutralReceiptSha256" } })
            {
                EqualPath(Text(request, pair[0]), Path.Combine(lifecycle, pair[1]));
                if (Hash(Text(request, pair[0])) != Text(request, pair[2])) throw new InvalidOperationException("Frozen lifecycle source/input identity changed.");
            }
            var release = Read(Text(request, "releaseManifest"));
            if (Number(release, "schemaVersion") != 1) throw new InvalidOperationException("Invalid frozen release schema.");
            Pattern(Text(release, "sourceCommit"), "^[0-9a-f]{40}$");
            var entryId = Text(request, "entryPackage");
            var roots = request.ContainsKey("entryPackages") ? Array(request, "entryPackages").Select(value => value as string ?? throw new InvalidOperationException("Lifecycle entry roots require strings.")).ToArray() : new[] { entryId };
            var catalogPath = Child(host, "YLTesting/PackageCatalog.json");
            var catalog = Read(catalogPath);
            if (catalog.Count != 2 || Number(catalog, "schemaVersion") != 1) throw new InvalidOperationException("Invalid owned package catalog schema.");
            var known = Array(catalog, "packages").Select(Object).Select(package => Text(package, "id")).ToArray();
            foreach (var id in known) Pattern(id, "^com\\.kano\\.ylframework(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)?$");
            if (known.Length == 0 || known.Distinct(StringComparer.Ordinal).Count() != known.Length || !known.Contains("com.kano.ylframework"))
                throw new InvalidOperationException("Invalid/duplicate owned package catalog identities.");
            var combinationPhases = CombinationPhases(roots);
            bool fixedComposite = roots.SequenceEqual(new[] { "com.kano.ylframework.movement2d", "com.kano.ylframework.overlap" }, StringComparer.Ordinal) ||
                (entryId == "com.kano.ylframework" && roots.SequenceEqual(known, StringComparer.Ordinal));
            if (roots.Length == 0 || roots[0] != entryId || roots.Any(id => string.IsNullOrWhiteSpace(id) || !known.Contains(id)) || roots.Distinct(StringComparer.Ordinal).Count() != roots.Length ||
                (roots.Length > 1 && combinationPhases.Length == 0 && !fixedComposite) || (combinations && combinationPhases.Length == 0))
                throw new InvalidOperationException("Invalid primary/unknown/duplicate/composite lifecycle entry roots.");
            if (!new[] { "Inspect", "Reopen", "Remove", "VerifyRemoved" }.Contains(phase) && !(combinations && combinationPhases.Contains(phase)))
                throw new InvalidOperationException("Lifecycle phase differs from its explicit roots/combination profile.");
            var released = Array(release, "packages").Select(Object).ToArray();
            if (released.Length != known.Length || !new HashSet<string>(released.Select(p => Text(p, "id")), StringComparer.Ordinal).SetEquals(known) || !known.Contains(entryId))
                throw new InvalidOperationException("Incomplete frozen release/entry identity.");
            if (Text(request, "packageOrigin") == "Registry" && Text(Object(Value(release, "producer")), "kind") != "GitHubActions")
                throw new InvalidOperationException("Registry lifecycle requires the CI producer receipt.");
            var original = Read(Text(request, "originalManifest"));
            var dependencies = Object(Value(original, "dependencies"));
            var direct = Text(request, "packageOrigin") == "Registry" ? roots : Closure(released, roots);
            if (original.Count != 2 || dependencies.Count != direct.Length || direct.Any(id => !dependencies.ContainsKey(id)))
                throw new InvalidOperationException("The original artifact manifest must declare its exact frozen direct roots/closure.");
            foreach (var id in direct)
            {
                var row = released.Single(p => Text(p, "id") == id);
                var locator = Text(dependencies, id);
                if (Text(request, "packageOrigin") == "Registry") { if (locator != Text(row, "version")) throw new InvalidOperationException("Frozen Registry version differs."); }
                else
                {
                    if (!locator.StartsWith("file:", StringComparison.Ordinal)) throw new InvalidOperationException("LocalTarball requires original file locators.");
                    var archive = SafePath(locator.Substring(5));
                    if (Path.GetFileName(archive) != Text(row, "assetName") || Hash(archive) != Text(row, "sha256")) throw new InvalidOperationException("Frozen LocalTarball bytes/identity changed.");
                }
            }
            var scopes = Array(original, "scopedRegistries");
            if (scopes.Count != 1) throw new InvalidOperationException("Invalid original Registry scopes.");
            var scope = Object(scopes[0]);
            var expectedScopes = Closure(released, roots).Contains("com.kano.ylframework.hybridclr") ? new[] { "com.kano.ylframework", "com.code-philosophy.hybridclr" } : new[] { "com.kano.ylframework" };
            if (scope.Count != 3 || Text(scope, "name") != "OpenUPM" || Text(scope, "url") != Text(request, "registryUrl") ||
                !Array(scope, "scopes").SequenceEqual(expectedScopes.Cast<object>())) throw new InvalidOperationException("Invalid original scoped Registry configuration.");
            var inputPath = Path.Combine(host, ".yl-inputs.json");
            if (Hash(inputPath) != Text(request, "inputReceiptSha256")) throw new InvalidOperationException("Host input receipt changed.");
            var inputs = Read(inputPath);
            if (Number(inputs, "schemaVersion") != 1 || Text(inputs, "purpose") != "YLTesting.HostInputs" || Text(inputs, "hostId") != Text(request, "hostId") || Text(inputs, "inputHash") != Text(request, "inputHash"))
                throw new InvalidOperationException("Invalid host input identity/owner.");
            if (Array(inputs, "files").Select(Object).Count(item => Text(item, "path") == "YLTesting/PackageCatalog.json" &&
                string.Equals(Text(item, "sha256"), Hash(catalogPath), StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidOperationException("The package catalog is not bound to the immutable host input receipt.");
            var beforeRoots = roots; var afterRoots = roots;
            if (combinations)
            {
                var first = entryId == "com.kano.ylframework.reddot" ? "com.kano.ylframework.reddot" : "com.kano.ylframework.excel";
                var second = first == "com.kano.ylframework.reddot" ? "com.kano.ylframework.save" : "com.kano.ylframework.gameplay";
                if (phase == combinationPhases[0] || phase == combinationPhases[1]) afterRoots = new[] { first };
                if (phase == combinationPhases[2]) beforeRoots = new[] { first };
                if (phase == combinationPhases[3] || phase == combinationPhases[4]) afterRoots = new[] { second };
                if (phase == combinationPhases[5]) beforeRoots = new[] { second };
                if (phase == combinationPhases[1] || phase == combinationPhases[4]) beforeRoots = afterRoots;
            }
            if (phase == "Remove" || phase == "VerifyRemoved") afterRoots = System.Array.Empty<string>();
            if (phase == "VerifyRemoved") beforeRoots = afterRoots;
            var context = new Context { request = request, arguments = arguments, sha256 = Hash(requestPath), roots = roots, released = released, original = original,
                before = ManifestFor(original, released, beforeRoots, Text(request, "packageOrigin")), after = ManifestFor(original, released, afterRoots, Text(request, "packageOrigin")) };
            var savedState = SafePath(Path.Combine(output, "operation.json"));
            if (resume && IsMutation(phase)) ReadOperation(context);
            else if (File.Exists(savedState)) throw new InvalidOperationException("A new invocation cannot adopt existing removal state.");
            AssertManifest(context, IsMutation(phase), resume);
            foreach (var item in Array(inputs, "files").Select(Object))
            {
                var relative = Text(item, "path");
                var path = Child(host, relative);
                var actual = relative == "Packages/manifest.json" ? Hash(Text(request, "originalManifest")) : Hash(path);
                if (!string.Equals(actual, Text(item, "sha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Host producer input changed: " + relative);
            }
            var neutral = Read(Text(request, "neutralReceipt"));
            if (Number(neutral, "schemaVersion") != 1 || Text(neutral, "purpose") != "YLTesting.RegistryLifecycle.NeutralInputs" ||
                Text(neutral, "hostId") != Text(request, "hostId") || Text(neutral, "workspaceId") != Text(request, "workspaceId") ||
                Text(neutral, "resultId") != Text(request, "resultId") || Text(neutral, "cacheId") != Text(request, "cacheId") || Text(neutral, "inputHash") != Text(request, "inputHash"))
                throw new InvalidOperationException("Invalid neutral producer receipt ownership.");
            var neutralPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Array(neutral, "files").Select(Object))
            {
                var relative = Text(item, "path");
                if (!relative.StartsWith("Assets/", StringComparison.Ordinal) || Text(item, "ownerId") != Text(request, "hostId") ||
                    !neutralPaths.Add(Child(host, relative)) || Hash(Child(host, relative)) != Text(item, "sha256")) throw new InvalidOperationException("Neutral sentinel/probe/meta changed.");
            }
            if (!neutralPaths.SetEquals(TreeFiles(Path.Combine(host, "Assets")))) throw new InvalidOperationException("Unregistered neutral Assets content; do not delete code to make removal pass.");
            foreach (var item in new[] { new[] { "UPM_CACHE_ROOT", "Upm" }, new[] { "UPM_CACHE_PATH", "Upm/packages" }, new[] { "UPM_NPM_CACHE_PATH", "Upm/npm" } })
                EqualPath(Environment.GetEnvironmentVariable(item[0]), Path.Combine(cache, item[1]));
            return context;
        }

        private static void Owner(Dictionary<string, object> request, string path, string kind, string idField)
        {
            var owner = Read(Path.Combine(path, ".yl-owner.json"));
            if (Number(owner, "schemaVersion") != 1 || Text(owner, "purpose") != "YLTesting.Directory" || Text(owner, "kind") != kind ||
                Text(owner, "id") != Text(request, idField) || Text(owner, "workspaceId") != Text(request, "workspaceId")) throw new InvalidOperationException("Wrong " + kind + " owner.");
            EqualPath(Text(owner, "path"), path);
            Pattern(Text(owner, "key"), "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$");
            var area = kind == "Host" ? "Hosts" : kind == "Cache" ? "Cache" : "Results";
            var expected = Path.Combine(Text(request, "workspaceRoot"), area, Text(owner, "key"));
            if (kind == "Result") expected = Path.Combine(expected, Path.GetFileName(path));
            EqualPath(expected, path);
            if (!File.Exists(SafePath(Path.Combine(path, ".lease")))) throw new InvalidOperationException("Missing owner lease.");
        }

        private static string[] CombinationPhases(string[] roots)
        {
            if (roots.SequenceEqual(new[] { "com.kano.ylframework.reddot", "com.kano.ylframework.save" }, StringComparer.Ordinal))
                return new[] { "RemoveSave", "VerifyReddotOnly", "RestoreSave", "RemoveReddot", "VerifySaveOnly", "RestoreReddot" };
            if (roots.Length == 2 && new HashSet<string>(roots, StringComparer.Ordinal).SetEquals(new[] { "com.kano.ylframework.excel", "com.kano.ylframework.gameplay" }))
                return new[] { "RemoveGameplay", "VerifyExcelOnly", "RestoreGameplay", "RemoveExcel", "VerifyGameplayOnly", "RestoreExcel" };
            return System.Array.Empty<string>();
        }

        private static bool IsMutation(string phase) => new[] { "Remove", "RemoveGameplay", "RestoreGameplay", "RemoveExcel", "RestoreExcel", "RemoveSave", "RestoreSave", "RemoveReddot", "RestoreReddot" }.Contains(phase);

        private static string[] Closure(Dictionary<string, object>[] released, string[] roots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(roots);
            while (pending.Count != 0)
            {
                var id = pending.Dequeue();
                if (!seen.Add(id)) continue;
                var row = released.SingleOrDefault(p => Text(p, "id") == id) ?? throw new InvalidOperationException("Frozen closure package is missing.");
                foreach (var dependency in Object(Value(row, "dependencies")).Keys.Where(d => d.StartsWith("com.kano.", StringComparison.Ordinal))) pending.Enqueue(dependency);
            }
            return seen.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }

        private static Dictionary<string, object> ManifestFor(Dictionary<string, object> original, Dictionary<string, object>[] released, string[] roots, string origin)
        {
            var expected = new Dictionary<string, object>(original, StringComparer.Ordinal);
            var dependencies = Object(Value(original, "dependencies"));
            expected["dependencies"] = (origin == "Registry" ? roots : Closure(released, roots)).ToDictionary(id => id, id => Value(dependencies, id), StringComparer.Ordinal);
            return expected;
        }

        private static void AssertManifest(Context context, bool before, bool transitioning = false)
        {
            var actualPath = Path.Combine(Text(context.request, "hostRoot"), "Packages/manifest.json");
            var actual = Read(actualPath);
            if (!Same(actual, before ? context.before : context.after) && !(transitioning && Same(actual, context.after)))
                throw new InvalidOperationException("Manifest changed beyond the bound root removal/restoration.");
            if (new[] { "Inspect", "Reopen" }.Contains(Text(context.request, "phase")) && Hash(actualPath) != Text(context.request, "originalManifestSha256"))
                throw new InvalidOperationException("Initial/warm manifest bytes changed.");
        }

        private static void AssertPackages(Context context, PackageRow[] packages, Dictionary<string, object> manifest)
        {
            var direct = Object(Value(manifest, "dependencies"));
            var expected = Closure(context.released, direct.Keys.ToArray());
            var own = packages.Where(p => p.name.StartsWith("com.kano.", StringComparison.Ordinal)).ToArray();
            if (!new HashSet<string>(own.Select(p => p.name), StringComparer.Ordinal).SetEquals(expected))
                throw new InvalidOperationException("The actual resolved graph contains missing/remaining own packages.");
            var locked = Object(Value(Read(Path.Combine(Text(context.request, "hostRoot"), "Packages/packages-lock.json")), "dependencies"));
            if (locked.Count != packages.Length) throw new InvalidOperationException("Actual List and lock package sets differ.");
            foreach (var package in packages)
            {
                if (package.isDirectDependency != direct.ContainsKey(package.name) || !locked.ContainsKey(package.name)) throw new InvalidOperationException("Actual direct/lock package identity differs.");
                var row = Object(Value(locked, package.name));
                if (Number(row, "depth") < 0 || Text(row, "source") != (package.source == "LocalTarball" ? "local-tarball" : package.source.ToLowerInvariant())) throw new InvalidOperationException("Actual lock source/depth differs.");
                var dependencies = package.dependencies.ToDictionary(d => d.name, d => (object)d.version, StringComparer.Ordinal);
                if (!Same(dependencies, Object(Value(row, "dependencies")))) throw new InvalidOperationException("Actual List/lock dependencies differ.");
                if (package.name == "com.code-philosophy.hybridclr" && !expected.Contains("com.kano.ylframework.hybridclr")) throw new InvalidOperationException("Indirect SDK remains after removing its root.");
                if (!expected.Contains(package.name)) continue;
                var release = context.released.Single(p => Text(p, "id") == package.name);
                var origin = Text(context.request, "packageOrigin");
                var locator = origin == "Registry" ? Text(release, "version") : Text(direct, package.name);
                if (package.source != origin || package.version != Text(release, "version") || Text(row, "version") != locator || package.packageId.Replace('\\', '/') != package.name + "@" + locator.Replace('\\', '/') ||
                    !Same(dependencies, Object(Value(release, "dependencies"))) || (origin == "Registry" && (package.registryUrl != Text(context.request, "registryUrl") || Text(row, "url") != package.registryUrl)))
                    throw new InvalidOperationException("Actual package source/locator/dependencies differ from the frozen release.");
            }
        }

        private static CompilationRow[] CaptureCompilation(AssembliesType mode) => CompilationPipeline.GetAssemblies(mode).OrderBy(a => a.name, StringComparer.Ordinal).Select(a => new CompilationRow
        {
            name = a.name, sourceFiles = a.sourceFiles.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            assemblyReferences = a.assemblyReferences.Select(r => r.name).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            compiledAssemblyReferences = a.compiledAssemblyReferences.OrderBy(p => p, StringComparer.Ordinal).ToArray()
        }).ToArray();

        private static string[] Strings(Dictionary<string, object> value, string name) => Array(value, name).Select(item => item as string ?? throw new InvalidOperationException("Invalid operation target string.")).ToArray();

        private static Operation ReadOperation(Context context)
        {
            var state = Read(Path.Combine(Text(context.request, "output"), "operation.json"));
            var fields = new[] { "requestSha256", "hostId", "resultId", "cacheId", "entryPackage", "stage", "requestStatus", "completion", "pid", "issued", "requestStatusObserved", "phase", "entryPackages", "removePackages", "addPackages", "requestKind" };
            if (state.Count != fields.Length || state.Keys.Any(k => !fields.Contains(k))) throw new InvalidOperationException("Invalid removal state fields.");
            if (!IsMutation(Text(context.request, "phase")) || Text(state, "requestSha256") != context.sha256 || Number(state, "pid") != Process.GetCurrentProcess().Id ||
                Text(state, "hostId") != Text(context.request, "hostId") || Text(state, "resultId") != Text(context.request, "resultId") ||
                Text(state, "cacheId") != Text(context.request, "cacheId") || Text(state, "entryPackage") != Text(context.request, "entryPackage") || !(Value(state, "issued") is bool issued))
                throw new InvalidOperationException("Removal state is not bound to this request/owner/process.");
            var requestStatus = Text(state, "requestStatus"); var completion = Text(state, "completion");
            if (!(Value(state, "requestStatusObserved") is bool observed) || (observed ? requestStatus != "Success" : requestStatus != "")) throw new InvalidOperationException("Invalid removal observation types/status.");
            var remove = Object(Value(context.before, "dependencies")).Keys.Except(Object(Value(context.after, "dependencies")).Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var add = Object(Value(context.after, "dependencies")).Keys.Except(Object(Value(context.before, "dependencies")).Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => Text(context.request, "packageOrigin") == "Registry" ? id + "@" + Text(Object(Value(context.original, "dependencies")), id) : Text(Object(Value(context.original, "dependencies")), id)).ToArray();
            var kind = add.Length == 0 && remove.Length == 1 ? "Remove" : "AddAndRemove";
            if (Text(state, "phase") != Text(context.request, "phase") || Text(state, "requestKind") != kind || !Strings(state, "entryPackages").SequenceEqual(context.roots) ||
                !Strings(state, "removePackages").SequenceEqual(remove) || !Strings(state, "addPackages").SequenceEqual(add) ||
                !new[] { kind + "Planned", kind + "Issued", kind + "Succeeded", "Verified" }.Contains(Text(state, "stage"))) throw new InvalidOperationException("Operation state roots/targets/stage are not bound to this request.");
            return new Operation { requestSha256 = context.sha256, hostId = Text(state, "hostId"), resultId = Text(state, "resultId"), cacheId = Text(state, "cacheId"),
                entryPackage = Text(state, "entryPackage"), pid = (int)Number(state, "pid"), stage = Text(state, "stage"), issued = issued,
                phase = Text(state, "phase"), entryPackages = context.roots, removePackages = remove, addPackages = add, requestKind = kind,
                requestStatus = requestStatus, requestStatusObserved = observed, completion = completion };
        }

        private static void SaveOperation()
        {
            var path = SafePath(Path.Combine(Text(current.request, "output"), "operation.json"));
            var temporary = SafePath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
            SaveNew(temporary, operation);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static void SaveNew(string path, object value)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(JsonUtility.ToJson(value, true));
            using (var stream = new FileStream(SafePath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        private static string Argument(string[] arguments, string name)
        {
            var indices = Enumerable.Range(0, arguments.Length).Where(i => arguments[i] == name).ToArray();
            if (indices.Length != 1 || indices[0] + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[indices[0] + 1]) || arguments[indices[0] + 1].StartsWith("-", StringComparison.Ordinal))
                throw new InvalidOperationException("An explicit unique request/argument is required: " + name);
            return arguments[indices[0] + 1];
        }

        private static string SafePath(string path, bool readOnly = false)
        {
            if (string.IsNullOrWhiteSpace(path) || !Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]") || path.Substring(2).Contains(":")) throw new InvalidOperationException("Explicit local absolute paths are required.");
            foreach (var part in path.Substring(3).Split(new[] { '/', '\\' }))
                if (part == "." || part == ".." || part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal) ||
                    Regex.IsMatch(part, "[<>\"|?*\\x00-\\x1f]|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)")) throw new InvalidOperationException("Ambiguous lifecycle path.");
            var full = Path.GetFullPath(path).TrimEnd('/', '\\');
            for (var cursor = full; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            {
                try { if (!readOnly && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked lifecycle path: " + cursor); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return full;
        }
        private static string Child(string root, string relative)
        {
            if (Path.IsPathRooted(relative)) throw new InvalidOperationException("Relative producer inputs are required.");
            var path = SafePath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Producer input escaped its owner.");
            return path;
        }
        private static IEnumerable<string> TreeFiles(string root)
        {
            foreach (var path in Directory.GetFileSystemEntries(SafePath(root)))
            {
                var safe = SafePath(path);
                if (Directory.Exists(safe)) { foreach (var file in TreeFiles(safe)) yield return file; }
                else yield return safe;
            }
        }
        private static void EqualPath(string left, string right, bool readOnly = false)
        {
            if (!string.Equals(SafePath(left, readOnly), SafePath(right, readOnly), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Lifecycle owner/Editor/output/cache path mismatch.");
        }
        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(SafePath(path)))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void Pattern(string value, string pattern) { if (!Regex.IsMatch(value, pattern)) throw new InvalidOperationException("Invalid lifecycle identity/hash."); }
        private static Dictionary<string, object> Read(string path)
        {
            path = SafePath(path);
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidOperationException("Lifecycle JSON size limit exceeded.");
            return Object(new Json(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path))).Parse());
        }
        private static object Value(Dictionary<string, object> value, string name)
        {
            if (!value.TryGetValue(name, out var result)) throw new InvalidOperationException("Missing lifecycle field: " + name);
            return result;
        }
        private static string Text(Dictionary<string, object> value, string name) => Value(value, name) as string ?? throw new InvalidOperationException("Lifecycle string field required: " + name);
        private static long Number(Dictionary<string, object> value, string name) => Value(value, name) is long result ? result : throw new InvalidOperationException("Lifecycle integer field required: " + name);
        private static Dictionary<string, object> Object(object value) => value as Dictionary<string, object> ?? throw new InvalidOperationException("Lifecycle JSON object required.");
        private static List<object> Array(Dictionary<string, object> value, string name) => Value(value, name) as List<object> ?? throw new InvalidOperationException("Lifecycle JSON array required: " + name);
        private static bool Same(object left, object right)
        {
            if (left is Dictionary<string, object> a && right is Dictionary<string, object> b)
                return a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && Same(p.Value, value));
            if (left is List<object> x && right is List<object> y) return x.Count == y.Count && x.Zip(y, Same).All(equal => equal);
            return Equals(left, right);
        }

        // JsonUtility writes engine-native reports; this small strict reader handles map-shaped UPM/owner JSON without Json.NET.
        private sealed class Json
        {
            private readonly string text;
            private int position;
            private int depth;
            public Json(string value) { text = value; }
            public object Parse() { var value = ReadValue(); White(); if (position != text.Length) Bad(); return value; }
            private void White() { while (position < text.Length && " \t\r\n".IndexOf(text[position]) >= 0) position++; }
            private void Bad() => throw new InvalidOperationException("Invalid/duplicate/trailing lifecycle JSON at " + position.ToString(CultureInfo.InvariantCulture));
            private bool Eat(char value) { White(); if (position < text.Length && text[position] == value) { position++; return true; } return false; }
            private object ReadValue()
            {
                if (++depth > 64) Bad();
                White(); if (position >= text.Length) Bad();
                object value;
                if (text[position] == '"') value = String();
                else if (Eat('{'))
                {
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (!Eat('}'))
                    {
                        do { White(); var key = String(); if (!Eat(':') || map.ContainsKey(key)) Bad(); map.Add(key, ReadValue()); } while (Eat(','));
                        if (!Eat('}')) Bad();
                    }
                    value = map;
                }
                else if (Eat('['))
                {
                    var array = new List<object>();
                    if (!Eat(']')) { do { array.Add(ReadValue()); } while (Eat(',')); if (!Eat(']')) Bad(); }
                    value = array;
                }
                else
                {
                    var rest = text.Substring(position);
                    if (rest.StartsWith("true", StringComparison.Ordinal)) { position += 4; value = true; }
                    else if (rest.StartsWith("false", StringComparison.Ordinal)) { position += 5; value = false; }
                    else if (rest.StartsWith("null", StringComparison.Ordinal)) { position += 4; value = null; }
                    else
                    {
                        var match = Regex.Match(rest, "^-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?");
                        if (!match.Success) Bad();
                        position += match.Length;
                        if (long.TryParse(match.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) value = integer;
                        else if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number) && !double.IsInfinity(number)) value = number;
                        else { Bad(); value = null; }
                    }
                }
                depth--; return value;
            }
            private string String()
            {
                if (position >= text.Length || text[position++] != '"') { Bad(); return null; }
                var builder = new StringBuilder();
                while (position < text.Length)
                {
                    var value = text[position++];
                    if (value == '"') return builder.ToString();
                    if (value < 32) Bad();
                    if (value != '\\') { builder.Append(value); continue; }
                    if (position >= text.Length) Bad();
                    var escape = text[position++];
                    switch (escape)
                    {
                        case '"': case '\\': case '/': builder.Append(escape); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (position + 4 > text.Length || !ushort.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unicode)) { Bad(); break; }
                            builder.Append((char)unicode); position += 4; break;
                        default: Bad(); break;
                    }
                }
                Bad(); return null;
            }
        }

        private sealed class Context
        {
            public Dictionary<string, object> request, original, before, after;
            public Dictionary<string, object>[] released;
            public string[] arguments, roots;
            public string sha256;
        }
        [Serializable] private sealed class Operation
        {
            public string requestSha256, hostId, resultId, cacheId, entryPackage, stage, phase, requestKind;
            public string[] entryPackages, removePackages, addPackages;
            public string requestStatus = "", completion = "";
            public int pid;
            public bool issued, requestStatusObserved;
        }
        [Serializable] private sealed class DependencyRow { public string name, version; }
        [Serializable] private sealed class PackageRow
        {
            public string name, version, source, packageId, resolvedPath, registryUrl;
            public bool isDirectDependency;
            public DependencyRow[] dependencies;
        }
        [Serializable] private sealed class CacheEnvironment { public string UPM_CACHE_ROOT, UPM_CACHE_PATH, UPM_NPM_CACHE_PATH; }
        [Serializable] private sealed class CompilationRow { public string name; public string[] sourceFiles, assemblyReferences, compiledAssemblyReferences; }
        [Serializable] private sealed class CompilationGraph { public CompilationRow[] editor, player; }
        [Serializable] private sealed class Report
        {
            public int schemaVersion, pid;
            public string purpose, status, error, phase, workspaceId, hostId, resultId, cacheId, inputHash, requestId, requestSha256,
                entryPackage, packageOrigin, releaseManifestSha256, unityExecutable, unityVersion, unityProductVersion, manifestSha256, lockSha256;
            public bool isBatchMode, scriptCompilationFailed;
            public CacheEnvironment cacheEnvironment;
            public string[] entryPackages;
            public CompilationGraph compilation;
            public PackageRow[] packages;
            public Operation removal;
        }
    }
}
#endif
