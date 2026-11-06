using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Samples.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupCompilationTests
    {
        [Test]
        public void SampleAcceptsAnApplicationContentKeyProviderWithoutSerializingIt()
        {
            var provider = typeof(ResourceStartupExample).GetField("ContentKeyProvider");
            Assert.That(provider, Is.Not.Null, "The same startup sample must accept an application-owned content key provider.");
            Assert.That(provider.FieldType, Is.EqualTo(typeof(IResourceContentKeyProvider)));
            Assert.That(provider.IsStatic || provider.IsInitOnly, Is.False);
            Assert.That(provider.IsNotSerialized, Is.True, "A sample scene must never persist its application content key provider.");
        }

        [Test]
        public void SamplePlayerGraphExcludesEditorAndOptionalBackends()
        {
            var players = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            Assert.That(players.Any(x => x.name == "YLFramework.ResourceStartup.Sample.Editor"), Is.False);
            var runtime = players.Single(x => x.name == "YLFramework.ResourceStartup.Sample");
            Assert.That(runtime.assemblyReferences.Select(x => x.name), Is.EquivalentTo(new[] { "YLFramework.Runtime", "UnityEngine.UI" }));
            var subject = players.Single(x => x.name == "YLFramework.Runtime");
            var json = subject.compiledAssemblyReferences.Single(x => Path.GetFileName(x) == "Newtonsoft.Json.dll");
            Assert.That(runtime.compiledAssemblyReferences.All(path => TestDependencyGuard.IsAllowedReference(path,
                EditorApplication.applicationContentsPath, typeof(UnityEngine.UI.Image).Assembly.Location, false, json)), Is.True);
            Assert.That(typeof(ResourceStartup).Assembly, Is.SameAs(typeof(ResourceSystem).Assembly));
        }

        [UnityTest]
        public IEnumerator CompleteRuntimeAndExampleCompileTogetherInWindowsDevelopmentWithOnlyApprovedDependencies()
        {
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "C05-PlayerClosure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                var profiles = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                var runtime = profiles.Single(x => x.name == "YLFramework.Runtime");
                var json = runtime.compiledAssemblyReferences.Single(x => Path.GetFileName(x) == "Newtonsoft.Json.dll");
                var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
                string runtimeOutput = null;
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(YLFrameworkRoot).Assembly);
                var imported = UnityEditor.PackageManager.UI.Sample.FindByPackage(package.name, package.version)
                    .Single(value => value.displayName == "Resource Startup");
                Assert.That(imported.isImported, Is.True);
                var importedRoot = Path.GetFullPath(imported.importPath);
                foreach (var name in new[] { "YLFramework.Runtime", "YLFramework.ResourceStartup.Sample" })
                {
                    var graph = profiles.Single(x => x.name == name);
                    var root = name == "YLFramework.Runtime" ? "Packages/com.kano.ylframework/Runtime" : Path.Combine(importedRoot, "Runtime");
                    var sources = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*.cs", SearchOption.AllDirectories);
                    if (name == "YLFramework.Runtime")
                    {
                        const string Adapter = "Packages/com.kano.ylframework/Runtime/Systems/Action/Integration/DOTween";
                        var adapter = global::YLFramework.Testing.PackageTestPaths.Physical(Adapter);
                        var adapterFiles = Directory.GetFiles(adapter, "*.cs", SearchOption.AllDirectories);
                        CollectionAssert.AreEquivalent(new[] { "DOTweenAction.cs" }, adapterFiles.Select(Path.GetFileName));
                        Assert.That(AssetDatabase.AssetPathToGUID(Adapter + "/YLFramework.Actions.DOTween.asmdef"), Is.EqualTo("269cc8620fbed2b4daa22533cd8731d1"));
                        CollectionAssert.AreEquivalent(new[] { "YLFramework.Runtime.asmdef", "YLFramework.Actions.DOTween.asmdef" },
                            Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*.asmdef", SearchOption.AllDirectories).Select(Path.GetFileName));
                        var definition = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(adapter, "YLFramework.Actions.DOTween.asmdef")));
                        Assert.That((string)definition["name"], Is.EqualTo("YLFramework.Actions.DOTween"));
                        CollectionAssert.AreEqual(new[] { "DOTWEEN" }, ((Newtonsoft.Json.Linq.JArray)definition["defineConstraints"]).Values<string>());
                        Assert.That(profiles.Any(value => value.name == "YLFramework.Actions.DOTween"), Is.False);
                        sources = sources.Except(adapterFiles, StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    CollectionAssert.AreEquivalent(sources.Select(Path.GetFullPath), graph.sourceFiles.Select(Path.GetFullPath));
                    if (name == "YLFramework.ResourceStartup.Sample")
                    {
                        foreach (var source in sources)
                        {
                            var relative = Path.GetRelativePath(importedRoot, source).Replace('\\', '/');
                            var shipped = global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly,
                                "Samples~/ResourceStartup/" + relative);
                            CollectionAssert.AreEqual(File.ReadAllBytes(shipped), File.ReadAllBytes(source), "Compiled sample bytes must match the selected package copy.");
                        }
                    }
                    var output = Path.Combine(folder, name + ".dll");
                    var builder = new AssemblyBuilder(output, sources)
                    {
                        flags = AssemblyBuilderFlags.DevelopmentBuild,
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
                    bool Allowed(string path) => TestDependencyGuard.IsAllowedReference(
                        path, EditorApplication.applicationContentsPath, ui, false, json);
                    var trusted = graph.allReferences.Select(Path.GetFullPath).Where(Allowed).ToArray();
                    builder.excludeReferences = builder.defaultReferences.Where(x => !trusted.Contains(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x)), StringComparer.OrdinalIgnoreCase)).ToArray();
                    builder.additionalReferences = trusted.Concat(runtimeOutput == null ? Array.Empty<string>() : new[] { runtimeOutput }).ToArray();
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }

                    Assert.That(messages.Where(x => x.type == CompilerMessageType.Error).Select(x => x.message), Is.Empty);
                    Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), Is.True);
                    TestContext.WriteLine("{0}: {1} complete sources; Windows Development, no Editor/optional plugin inputs.", name, sources.Length);
                    runtimeOutput = output;
                }
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
            }
        }
    }
}
