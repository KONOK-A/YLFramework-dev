using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace YLFramework.Core.Tests
{
    public class CoreArchitectureBoundaryTests
    {
        [UnityTest]
        public IEnumerator CompleteFoundationAndCoreCompileInEditorAndRejectSystemsSymbols()
            => Compile(AssemblyBuilderFlags.EditorAssembly);

        [UnityTest]
        public IEnumerator CompleteFoundationAndCoreCompileInDevelopmentAndRejectSystemsSymbols()
            => Compile(AssemblyBuilderFlags.DevelopmentBuild);

        private static IEnumerator Compile(AssemblyBuilderFlags flags)
        {
            var root = Path.Combine(Application.dataPath, "YLFramework/Runtime");
            var foundation = Directory.GetFiles(Path.Combine(root, "Foundation"), "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray();
            var core = Directory.GetFiles(Path.Combine(root, "Core"), "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray();
            var entry = Path.GetFullPath(Path.Combine(root, "YLFrameworkRoot.cs"));
            var sources = foundation.Concat(core).Append(entry).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.That(sources.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(sources.Length));
            Assert.That(sources.Where(p => IsInsideFolder(p, Path.Combine(root, "Foundation"))), Is.EquivalentTo(foundation));
            Assert.That(sources.Where(p => IsInsideFolder(p, Path.Combine(root, "Core"))), Is.EquivalentTo(core));
            Assert.That(sources.Where(p => !foundation.Contains(p) && !core.Contains(p)), Is.EqualTo(new[] { entry }));
            foreach (var source in sources)
            {
                Assert.That(File.Exists(source), Is.True, source);
            }

            Assert.That(core, Is.Not.Empty);

            var editor = (flags & AssemblyBuilderFlags.EditorAssembly) != 0;
            var extra = GetExternalReferences(editor);
            var runtime = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(a => a.name == "YLFramework.Runtime");
            var input = runtime.defines.Where(d => d == "ENABLE_INPUT_SYSTEM" || d == "ENABLE_LEGACY_INPUT_MANAGER").ToArray();
            Assert.That(input, Is.Not.Empty);
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.Core.SourceClosure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var legal = Path.Combine(folder, "LegalCoreConsumer.cs");
                File.WriteAllText(legal, "internal static class LegalCoreConsumer { " +
                    "static global::YLFramework.Pooling.ObjectPool<object> pool; " +
                    "static void Use() { var module = global::YLFramework.Core.ModuleManager.GetModule(\"X\"); " +
                    "var provider = global::YLFramework.Core.CoreResourceAccess.Provider; " +
                    "var settings = new global::Newtonsoft.Json.JsonSerializerSettings { CheckAdditionalContent = true }; " +
                    "var json = global::Newtonsoft.Json.JsonConvert.DeserializeObject(\"{}\", typeof(object), settings); " +
                    "var ui = typeof(global::UnityEngine.UI.Image); } }");
                var forbidden = Path.Combine(folder, "ForbiddenSystemsConsumer.cs");
                File.WriteAllText(forbidden, "using Upper = global::YLFramework.Audio.AudioKit; " +
                    "internal static class ForbiddenSystemsConsumer { static global::System.Type Type() => typeof(Upper); " +
                    "static void Use() => Upper.PrewarmSounds(0); }");
                foreach (var consumer in new[] { legal, forbidden })
                {
                    var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(consumer) + ".dll");
                    var builder = new AssemblyBuilder(output, sources.Append(consumer).ToArray())
                    {
                        flags = flags,
                        buildTarget = BuildTarget.StandaloneWindows64,
                        buildTargetGroup = BuildTargetGroup.Standalone,
                        referencesOptions = ReferencesOptions.UseEngineModules
                    };
                    builder.additionalDefines = input.Except(builder.defaultDefines).ToArray();
                    var defines = builder.defaultDefines.Concat(builder.additionalDefines).Distinct().ToArray();
                    Assert.That(defines.Contains("UNITY_EDITOR"), Is.EqualTo(editor));
                    Assert.That(defines.Contains("DEVELOPMENT_BUILD"), Is.EqualTo(!editor));
                    Assert.That(defines.Where(d => d == "ENABLE_INPUT_SYSTEM" || d == "ENABLE_LEGACY_INPUT_MANAGER"), Is.EquivalentTo(input));
                    var contents = EditorApplication.applicationContentsPath;
                    builder.excludeReferences = builder.defaultReferences.Where(p => !IsAllowedReference(p, contents, extra, editor)).ToArray();
                    builder.additionalReferences = extra;
                    var effective = builder.defaultReferences.Except(builder.excludeReferences).Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    Assert.That(effective.All(p => IsAllowedReference(p, contents, extra, editor)), Is.True);
                    Assert.That(effective.Any(p => Path.GetFileName(p) == "UnityEngine.CoreModule.dll"), Is.True);
                    Assert.That(effective.Any(p => Path.GetFileName(p) == "mscorlib.dll" || Path.GetFileName(p) == "netstandard.dll"), Is.True);
                    Assert.That(effective.Any(p => Path.GetFileName(p).StartsWith("YLFramework", StringComparison.Ordinal)), Is.False);
                    TestContext.WriteLine("Compiling {0} complete Foundation+Root+Core sources, {1}, {2}; defines:\n{3}\nReferences:\n{4}",
                        sources.Length, flags, consumer, string.Join("\n", defines), string.Join("\n", effective));
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }

                    Assert.That(messages, Is.Not.Null);
                    foreach (var message in messages)
                    {
                        TestContext.WriteLine("{0}:{1}:{2}: {3}: {4}", message.file, message.line, message.column, message.type, message.message);
                    }

                    var errors = messages.Where(m => m.type == CompilerMessageType.Error).ToArray();
                    if (consumer == legal)
                    {
                        Assert.That(errors.Select(m => m.file + ":" + m.line + ": " + m.message), Is.Empty);
                        Assert.That(File.Exists(output), Is.True);
                    }
                    else
                    {
                        Assert.That(errors, Is.Not.Empty);
                        Assert.That(errors.Any(m => Path.GetFullPath(m.file) == forbidden && m.line > 0 &&
                            (System.Text.RegularExpressions.Regex.IsMatch(m.message, @"\bAudio\b") || m.message.Contains("AudioKit")) &&
                            (m.message.Contains("CS0234") || m.message.Contains("CS0246"))), Is.True,
                            "负例必须因指定上层符号失败，不能用基础参考缺失代替边界违规。");
                        Assert.That(File.Exists(output), Is.False);
                    }
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static string[] GetExternalReferences(bool editor)
        {
            var ui = ResolveLoadedGraphAssembly("UnityEngine.UI", "UnityEngine.EventSystems.EventSystem");
            var loadedJson = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Newtonsoft.Json");
            Assert.That(loadedJson.GetType("Newtonsoft.Json.JsonSerializerSettings"), Is.Not.Null);
            var editorRuntime = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(a => a.name == "YLFramework.Runtime");
            var editorJson = editorRuntime.compiledAssemblyReferences.Where(p => Path.GetFileName(p) == "Newtonsoft.Json.dll")
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Single();
            Assert.That(editorJson, Is.EqualTo(Path.GetFullPath(loadedJson.Location)).IgnoreCase);
            // 同一包的Editor/AOT插件由运行中编译图选择，不能猜缓存路径或错用Editor DLL检验Player分支。
            var runtime = CompilationPipeline.GetAssemblies(editor ? AssembliesType.Editor : AssembliesType.Player)
                .Single(a => a.name == "YLFramework.Runtime");
            var json = runtime.compiledAssemblyReferences.Where(p => Path.GetFileName(p) == "Newtonsoft.Json.dll")
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Single();
            Assert.That(File.Exists(json), Is.True);
            Assert.That(AssemblyName.GetAssemblyName(json).FullName, Is.EqualTo(loadedJson.FullName));
            return new[] { ui, json };
        }

        [Test]
        public void CoreReferencesRejectPremadeOptionalAndSubstitutedPackagesInBothProfiles()
        {
            var contents = EditorApplication.applicationContentsPath;
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.Core.ReferenceRejection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (bool editor in new[] { true, false })
                {
                    var extra = GetExternalReferences(editor);
                    foreach (var reference in extra)
                    {
                        Assert.That(IsAllowedReference(reference, contents, extra, editor), Is.True, reference);
                        foreach (var name in new[] { Path.GetFileName(reference), Path.GetFileNameWithoutExtension(reference) + ".Fake.dll" })
                        {
                            var fake = Path.Combine(folder, name);
                            File.Copy(reference, fake, true);
                            Assert.That(IsAllowedReference(fake, contents, extra, editor), Is.False, fake);
                        }
                    }
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a =>
                        a.GetName().Name.StartsWith("YLFramework", StringComparison.Ordinal) ||
                        a.GetName().Name == "Unity.InputSystem"))
                    {
                        Assert.That(IsAllowedReference(assembly.Location, contents, extra, editor), Is.False, assembly.FullName);
                    }

                    foreach (var name in new[] { "Unrelated.Library.dll", "External.Resources.dll", "External.StateMachine.dll" })
                    {
                        var fake = Path.Combine(folder, name);
                        File.Copy(extra[0], fake, true);
                        Assert.That(IsAllowedReference(fake, contents, extra, editor), Is.False, fake);
                    }

                    Assert.That(IsAllowedReference(typeof(LogAssert).Assembly.Location, contents, extra, editor), Is.False);
                    Assert.That(IsAllowedReference(typeof(TestAttribute).Assembly.Location, contents, extra, editor), Is.False);
                    Assert.That(IsAllowedReference(typeof(UnityEngine.Object).Assembly.Location, contents, extra, editor), Is.True);
                    Assert.That(IsAllowedReference(typeof(EditorApplication).Assembly.Location, contents, extra, editor), Is.EqualTo(editor));
                    var otherJson = GetExternalReferences(!editor).Single(p => Path.GetFileName(p) == "Newtonsoft.Json.dll");
                    Assert.That(IsAllowedReference(otherJson, contents, extra, editor), Is.False, "拒绝错profile的Json.NET插件。");
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static string ResolveLoadedGraphAssembly(string name, string marker)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == name);
            Assert.That(assembly.GetType(marker), Is.Not.Null, marker);
            var path = Path.GetFullPath(assembly.Location);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(Path.GetFullPath(CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(a => a.name == name).outputPath),
                Is.EqualTo(path).IgnoreCase);
            return path;
        }

        private static bool IsAllowedReference(string path, string contents, string[] extra, bool editor)
        {
            var absolute = Path.GetFullPath(path);
            if (!File.Exists(absolute))
            {
                return false;
            }

            if (extra.Select(Path.GetFullPath).Contains(absolute, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            var name = Path.GetFileName(absolute);
            if (name.Contains("TestRunner"))
            {
                return false;
            }

            if (name == "UnityEngine.dll" || name.StartsWith("UnityEngine.", StringComparison.Ordinal))
            {
                return IsInsideFolder(absolute, Path.Combine(contents, "Managed/UnityEngine")) ||
                    IsInsideFolder(absolute, Path.Combine(contents, "PlaybackEngines")) ||
                    string.Equals(absolute, Path.GetFullPath(Path.Combine(contents, "Managed/UnityEngine.dll")), StringComparison.OrdinalIgnoreCase);
            }

            if (editor && (name == "UnityEditor.dll" || name.StartsWith("UnityEditor.", StringComparison.Ordinal)))
            {
                return IsInsideFolder(absolute, Path.Combine(contents, "Managed/UnityEngine")) ||
                    IsInsideFolder(absolute, Path.Combine(contents, "Managed/UnityEditor")) ||
                    string.Equals(absolute, Path.GetFullPath(Path.Combine(contents, "Managed/UnityEditor.dll")), StringComparison.OrdinalIgnoreCase);
            }

            if (name == "mscorlib.dll" || name == "netstandard.dll" || name == "System.dll" || name.StartsWith("System.", StringComparison.Ordinal))
            {
                return IsInsideFolder(absolute, Path.Combine(contents, "NetStandard")) ||
                    IsInsideFolder(absolute, Path.Combine(contents, "UnityReferenceAssemblies")) ||
                    IsInsideFolder(absolute, Path.Combine(contents, "MonoBleedingEdge/lib/mono"));
            }

            return false;
        }

        private static bool IsInsideFolder(string path, string folder)
        {
            var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
