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
            Directory.CreateDirectory(folder);
            try
            {
                var profiles = CompilationPipeline.GetAssemblies(AssembliesType.Player);
                var runtime = profiles.Single(x => x.name == "YLFramework.Runtime");
                var json = runtime.compiledAssemblyReferences.Single(x => Path.GetFileName(x) == "Newtonsoft.Json.dll");
                var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
                string runtimeOutput = null;
                foreach (var name in new[] { "YLFramework.Runtime", "YLFramework.ResourceStartup.Sample" })
                {
                    var graph = profiles.Single(x => x.name == name);
                    var root = name == "YLFramework.Runtime" ? "Assets/YLFramework/Runtime" : "Assets/YLFramework/Samples/ResourceStartup/Runtime";
                    var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
                    CollectionAssert.AreEquivalent(sources.Select(Path.GetFullPath), graph.sourceFiles.Select(Path.GetFullPath));
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
                    builder.excludeReferences = builder.defaultReferences.Where(x => !trusted.Contains(Path.GetFullPath(x), StringComparer.OrdinalIgnoreCase)).ToArray();
                    builder.additionalReferences = trusted.Concat(runtimeOutput == null ? Array.Empty<string>() : new[] { runtimeOutput }).ToArray();
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }

                    Assert.That(messages.Where(x => x.type == CompilerMessageType.Error).Select(x => x.message), Is.Empty);
                    Assert.That(File.Exists(output), Is.True);
                    TestContext.WriteLine("{0}: {1} complete sources; Windows Development, no Editor/optional plugin inputs.", name, sources.Length);
                    runtimeOutput = output;
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
