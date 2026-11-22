using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;
using YLFramework.Samples.Resource;
using CompilationAssembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupCompilationTests
    {
        private static readonly string[] RuntimeFiles = { "ResourceStartupExample.cs", "ResourceStartupExampleData.cs" };
        private static readonly string[] EditorFiles = { "ResourceStartupExampleEditor.cs", "ResourceBuildRecipeExample.cs" };

        [Test]
        public void SamplePlayerGraphExcludesEditorAndOptionalBackends()
        {
            var players = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            Assert.That(players.Any(assembly => assembly.name == "YLFramework.ResourceStartup.Sample.Editor"), Is.False);
            var sample = players.Single(assembly => assembly.name == "YLFramework.ResourceStartup.Sample");
            var direct = sample.assemblyReferences.Select(assembly => assembly.name).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "YLFramework.Runtime", "Unity.Addressables" }, direct);
            CollectionAssert.IsSubsetOf(direct, new[] { "YLFramework.Runtime", "Unity.Addressables", "Unity.ResourceManager", "Unity.Profiling.Core", "UnityEngine.UI" });
            var graph = players.Concat(CompilationPipeline.GetAssemblies(AssembliesType.Editor)).ToArray();
            var approved = ApprovedPackages(graph);
            var runtime = players.Single(assembly => assembly.name == "YLFramework.Runtime");
            var json = ApprovedJson(runtime);
            Assert.That(sample.compiledAssemblyReferences.All(path => Allowed(path, false, approved, json)), Is.True,
                "The sample must not inherit Editor, test, optional plugin or arbitrary Unity.* binaries.");
            var foreign = Path.Combine(Path.GetTempPath(), "kilo", "Sample-ReferenceLookalike-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(foreign);
            try
            {
                foreach (var source in approved.Where(path => Path.GetFileName(path) == "Unity.Addressables.dll" || Path.GetFileName(path) == "Unity.ResourceManager.dll")
                    .GroupBy(Path.GetFileName, StringComparer.Ordinal).Select(group => group.First()))
                {
                    var lookalike = Path.Combine(foreign, Path.GetFileName(source));
                    File.Copy(source, lookalike, false);
                    Assert.That(Allowed(lookalike, false, approved, json), Is.False,
                        "Even identical bytes and an official filename do not establish the actual compiler/package origin.");
                }
            }
            finally
            {
                Directory.Delete(foreign, true);
            }
            var importedRoot = ImportedRoot();
            AssertSampleSources(sample, importedRoot, "Runtime", RuntimeFiles);
            var editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.name == "YLFramework.ResourceStartup.Sample.Editor");
            AssertSampleSources(editor, importedRoot, "Editor", EditorFiles);
            Assert.That(typeof(ResourceStartup).Assembly, Is.SameAs(typeof(ResourceSystem).Assembly));
        }

        [UnityTest]
        public IEnumerator CompleteRuntimeAndExampleCompileTogetherInWindowsDevelopmentWithOnlyApprovedDependencies()
        {
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "Sample-PlayerClosure-" + Guid.NewGuid().ToString("N"));
            Assert.That(Directory.Exists(folder), Is.False);
            Directory.CreateDirectory(folder);
            try
            {
                var players = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                var editors = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var all = players.Concat(editors).ToArray();
                var approved = ApprovedPackages(all);
                var json = ApprovedJson(players.Single(assembly => assembly.name == "YLFramework.Runtime"));
                var importedRoot = ImportedRoot();
                var built = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var name in new[] { "YLFramework.Runtime", "YLFramework.ResourceStartup.Sample", "YLFramework.ResourceStartup.Sample.Editor" })
                {
                    var isEditor = name.EndsWith(".Editor", StringComparison.Ordinal);
                    var graph = (isEditor ? editors : players).Single(assembly => assembly.name == name);
                    string[] sources;
                    if (name == "YLFramework.Runtime")
                    {
                        var root = YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly, "Runtime");
                        const string Adapter = "Packages/com.kano.ylframework/Runtime/Systems/Action/Integration/DOTween";
                        var adapter = Physical(Adapter);
                        var adapterFiles = Directory.GetFiles(adapter, "*.cs", SearchOption.AllDirectories);
                        CollectionAssert.AreEquivalent(new[] { "DOTweenAction.cs" }, adapterFiles.Select(Path.GetFileName));
                        Assert.That(AssetDatabase.AssetPathToGUID(Adapter + "/YLFramework.Actions.DOTween.asmdef"), Is.EqualTo("269cc8620fbed2b4daa22533cd8731d1"));
                        CollectionAssert.AreEquivalent(new[] { "YLFramework.Runtime.asmdef", "YLFramework.Actions.DOTween.asmdef" },
                            Directory.GetFiles(root, "*.asmdef", SearchOption.AllDirectories).Select(Path.GetFileName));
                        var definition = JObject.Parse(File.ReadAllText(Path.Combine(adapter, "YLFramework.Actions.DOTween.asmdef")));
                        CollectionAssert.AreEqual(new[] { "DOTWEEN" }, ((JArray)definition["defineConstraints"]).Values<string>());
                        Assert.That(players.Any(assembly => assembly.name == "YLFramework.Actions.DOTween"), Is.False);
                        sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Except(adapterFiles, StringComparer.OrdinalIgnoreCase).ToArray();
                        CollectionAssert.AreEquivalent(sources.Select(Path.GetFullPath), graph.sourceFiles.Select(Physical));
                    }
                    else
                    {
                        sources = AssertSampleSources(graph, importedRoot, isEditor ? "Editor" : "Runtime", isEditor ? EditorFiles : RuntimeFiles);
                    }
                    var output = Path.Combine(folder, name + ".dll");
                    var builder = new AssemblyBuilder(output, sources)
                    {
                        flags = AssemblyBuilderFlags.DevelopmentBuild | (isEditor ? AssemblyBuilderFlags.EditorAssembly : 0),
                        buildTarget = BuildTarget.StandaloneWindows64,
                        buildTargetGroup = BuildTargetGroup.Standalone,
                        referencesOptions = ReferencesOptions.UseEngineModules,
                        compilerOptions = new ScriptCompilerOptions
                        {
                            ApiCompatibilityLevel = graph.compilerOptions.ApiCompatibilityLevel,
                            AllowUnsafeCode = graph.compilerOptions.AllowUnsafeCode
                        },
                        additionalDefines = Array.Empty<string>()
                    };
                    var frameworkEditor = editors.Single(assembly => assembly.name == "YLFramework.Editor");
                    var frameworkEditorPath = Path.GetFullPath(frameworkEditor.outputPath);
                    var trusted = graph.allReferences.Select(Physical).Where(path => Allowed(path, isEditor, approved, json) ||
                        isEditor && SamePath(path, frameworkEditorPath)).ToList();
                    foreach (var replacement in built)
                    {
                        trusted.RemoveAll(path => Path.GetFileNameWithoutExtension(path) == replacement.Key);
                        trusted.Add(replacement.Value);
                    }
                    Assert.That(trusted.Any(path => Path.GetFileName(path).IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetFileName(path).IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);
                    builder.excludeReferences = builder.defaultReferences.Where(path => !trusted.Contains(Physical(path), StringComparer.OrdinalIgnoreCase)).ToArray();
                    builder.additionalReferences = trusted.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }
                    Assert.That(messages, Is.Not.Null);
                    Assert.That(messages.Where(message => message.type == CompilerMessageType.Error).Select(message => message.message), Is.Empty);
                    Assert.That(File.Exists(output), Is.True);
                    TestContext.WriteLine("{0}: {1} complete sources; actual Windows Development closure; Editor={2}.", name, sources.Length, isEditor);
                    built.Add(name, output);
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static string ImportedRoot()
        {
            var package = PackageInfo.FindForAssembly(typeof(YLFrameworkRoot).Assembly);
            var sample = UnityEditor.PackageManager.UI.Sample.FindByPackage(package.name, package.version).Single(value => value.displayName == "Resource Startup");
            Assert.That(sample.isImported, Is.True);
            return Path.GetFullPath(sample.importPath);
        }

        private static string[] AssertSampleSources(CompilationAssembly graph, string importedRoot, string part, string[] names)
        {
            var sources = Directory.GetFiles(Path.Combine(importedRoot, part), "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray();
            CollectionAssert.AreEquivalent(names, sources.Select(Path.GetFileName), "No extra or omitted source is admitted to the exact four-file sample.");
            CollectionAssert.AreEquivalent(sources, graph.sourceFiles.Select(Physical));
            foreach (var source in sources)
            {
                var relative = Path.GetRelativePath(importedRoot, source).Replace('\\', '/');
                var shipped = YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly, "Samples~/ResourceStartup/" + relative);
                CollectionAssert.AreEqual(File.ReadAllBytes(shipped), File.ReadAllBytes(source), "Compiled sample bytes must match the actual selected package.");
                CollectionAssert.AreEqual(File.ReadAllBytes(shipped + ".meta"), File.ReadAllBytes(source + ".meta"), "Preserve the shipped source identity on sample import.");
                var virtualPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), source).Replace('\\', '/');
                var guid = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(shipped + ".meta"), @"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$");
                Assert.That(guid.Success, Is.True);
                Assert.That(AssetDatabase.AssetPathToGUID(virtualPath), Is.EqualTo(guid.Groups[1].Value.ToLowerInvariant()));
            }
            return sources;
        }

        private static HashSet<string> ApprovedPackages(CompilationAssembly[] all)
        {
            var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Unity.Addressables"] = "com.unity.addressables",
                ["Unity.ResourceManager"] = "com.unity.addressables",
                ["UnityEngine.UI"] = "com.unity.ugui",
                ["Unity.Addressables.Editor"] = "com.unity.addressables",
                ["Unity.ScriptableBuildPipeline.Editor"] = "com.unity.scriptablebuildpipeline"
            };
            var locked = JObject.Parse(File.ReadAllText("Packages/packages-lock.json"))["dependencies"];
            const string ProfilerPackage = "com.unity.profiling.core";
            const string ProfilerAssembly = "Unity.Profiling.Core";
            // Profiling Core is optional in 1.25. Never approve an unpinned assembly of the same name.
            if (locked[ProfilerPackage] != null)
            {
                expected.Add(ProfilerAssembly, ProfilerPackage);
            }
            else
            {
                Assert.That(all.Any(assembly => assembly.name == ProfilerAssembly || assembly.allReferences.Any(path =>
                    string.Equals(Path.GetFileNameWithoutExtension(path), ProfilerAssembly, StringComparison.OrdinalIgnoreCase))), Is.False,
                    "The compiler graph must not use an unpinned Profiling Core assembly.");
            }
            foreach (var pair in expected)
            {
                var graphs = all.Where(assembly => assembly.name == pair.Key).ToArray();
                Assert.That(graphs, Is.Not.Empty, pair.Key);
                var definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(pair.Key);
                var package = PackageInfo.FindForAssetPath(definition);
                Assert.That(package, Is.Not.Null);
                Assert.That(package.name, Is.EqualTo(pair.Value));
                var row = locked[pair.Value];
                Assert.That(row, Is.Not.Null);
                Assert.That((string)row["version"], Is.EqualTo(package.version));
                Assert.That(package.errors == null || package.errors.Length == 0, Is.True);
                Assert.That(package.source == PackageSource.Registry || pair.Key == "UnityEngine.UI" && package.source == PackageSource.BuiltIn, Is.True,
                    "Only the official locked registry packages (or actual builtin UGUI) are compiler inputs.");
                Assert.That((string)row["source"], Is.EqualTo(package.source == PackageSource.Registry ? "registry" : "builtin"));
                if (package.source == PackageSource.Registry)
                {
                    var url = new Uri((string)row["url"]);
                    Assert.That(url.Scheme == "https" && (url.Host == "packages.unity.com" || url.Host == "packages.unity.cn"), Is.True);
                    Assert.That(package.registry, Is.Not.Null);
                    var actualRegistry = new Uri(package.registry.url);
                    Assert.That(actualRegistry.Scheme, Is.EqualTo("https"));
                    Assert.That(actualRegistry.Host, Is.EqualTo(url.Host));
                }
                var root = Path.GetFullPath(package.resolvedPath);
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
                Assert.That((string)manifest["name"], Is.EqualTo(package.name));
                Assert.That((string)manifest["version"], Is.EqualTo(package.version));
                Assert.That(Inside(Physical(definition), root), Is.True);
                Assert.That((string)JObject.Parse(File.ReadAllText(Physical(definition)))["name"], Is.EqualTo(pair.Key));
                foreach (var graph in graphs)
                {
                    Assert.That(graph.sourceFiles, Is.Not.Empty);
                    foreach (var source in graph.sourceFiles)
                    {
                        var path = Physical(source);
                        Assert.That(Inside(path, root), Is.True, source);
                        NoLinks(path);
                        TestContext.WriteLine("Official source {0} {1}: {2} SHA256={3}", package.name, package.version, path, Hash(path));
                    }
                    foreach (var path in new[] { graph.outputPath, Path.Combine("Library/ScriptAssemblies", pair.Key + ".dll") }.Select(Physical).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(path))
                        {
                            continue;
                        }
                        NoLinks(path);
                        Assert.That(System.Reflection.AssemblyName.GetAssemblyName(path).Name, Is.EqualTo(pair.Key));
                        approved.Add(path);
                        TestContext.WriteLine("Official binary {0} {1}: {2} SHA256={3}; asmdef={4}", package.name, package.version, path, Hash(path), definition);
                    }
                }
            }
            return approved;
        }

        private static string ApprovedJson(CompilationAssembly runtime)
        {
            var path = runtime.compiledAssemblyReferences.Single(reference => Path.GetFileName(reference) == "Newtonsoft.Json.dll");
            var package = PackageInfo.FindForAssetPath("Packages/com.unity.nuget.newtonsoft-json/package.json");
            var row = JObject.Parse(File.ReadAllText("Packages/packages-lock.json"))["dependencies"]["com.unity.nuget.newtonsoft-json"];
            Assert.That(package.source, Is.EqualTo(PackageSource.Registry));
            Assert.That(package.version, Is.EqualTo((string)row["version"]));
            Assert.That((string)row["source"], Is.EqualTo("registry"));
            Assert.That(new[] { Path.Combine(package.resolvedPath, "Runtime/Newtonsoft.Json.dll"), Path.Combine(package.resolvedPath, "Runtime/AOT/Newtonsoft.Json.dll") }
                .Any(candidate => SamePath(candidate, path)), Is.True);
            return Physical(path);
        }

        private static bool Allowed(string path, bool editor, HashSet<string> packages, string json)
        {
            path = Physical(path);
            var name = Path.GetFileName(path);
            if (!File.Exists(path) || name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            if (packages.Contains(path))
            {
                return editor || name != "Unity.Addressables.Editor.dll" && name != "Unity.ScriptableBuildPipeline.Editor.dll";
            }
            if (SamePath(path, json))
            {
                return true;
            }
            var unity = EditorApplication.applicationContentsPath;
            if (name == "UnityEngine.dll" || name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                editor && (name == "UnityEditor.dll" || name.StartsWith("UnityEditor.", StringComparison.Ordinal)))
            {
                return Inside(path, Path.Combine(unity, "Managed")) || Inside(path, Path.Combine(unity, "PlaybackEngines"));
            }
            if (name == "mscorlib.dll" || name == "netstandard.dll" || name == "Microsoft.CSharp.dll" || name == "Microsoft.Win32.Primitives.dll" ||
                name == "System.dll" || name.StartsWith("System.", StringComparison.Ordinal))
            {
                return new[] { "NetStandard", "UnityReferenceAssemblies", "MonoBleedingEdge/lib/mono" }.Any(folder => Inside(path, Path.Combine(unity, folder)));
            }
            return false;
        }
        private static string Physical(string path)
        {
            if (path.Replace('\\', '/').StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = PackageInfo.FindForAssetPath(path.Replace('\\', '/'));
                Assert.That(package, Is.Not.Null, path);
                path = Path.Combine(package.resolvedPath, path.Replace('\\', '/').Substring(package.assetPath.Length).TrimStart('/'));
            }
            return Path.GetFullPath(path);
        }
        private static bool SamePath(string left, string right) => string.Equals(Physical(left), Physical(right), StringComparison.OrdinalIgnoreCase);
        private static bool Inside(string path, string root) => Physical(path).StartsWith(Physical(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void NoLinks(string path)
        {
            for (var cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                if (File.Exists(cursor) || Directory.Exists(cursor))
                {
                    Assert.That((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, Is.True);
                }
            }
        }
    }
}
