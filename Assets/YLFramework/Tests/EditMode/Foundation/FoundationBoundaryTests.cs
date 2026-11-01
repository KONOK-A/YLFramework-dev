using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Pooling;
using YLFramework.Async;
using YLFramework.Serialization;
using YLFramework.Update;

namespace YLFramework.Foundation.Tests
{
    public class FoundationBoundaryTests
    {
        [Test]
        public void FoundationFamiliesHaveOneRuntimeOwnerAndTheKernelContainerIsRetired()
        {
            const string RuntimeRoot = "Assets/YLFramework/Runtime/";
            const string Kernel = RuntimeRoot + "Foundation/Kernel";
            Assert.That(Directory.Exists(Kernel), Is.False);
            Assert.That(File.Exists(Kernel + ".meta"), Is.False);
            Assert.That(File.Exists(Kernel + "/YLFramework.Foundation.Runtime.asmdef"), Is.False);
            Assert.That(File.Exists(Kernel + "/YLFramework.Foundation.Runtime.asmdef.meta"), Is.False);
            Assert.That(Directory.Exists(RuntimeRoot + "Shared"), Is.False);
            Assert.That(File.Exists(RuntimeRoot + "Shared.meta"), Is.False);
            foreach (var retired in new[] { "Tools/AssemblyTool", "Tools/Attributes" })
            {
                Assert.That(Directory.Exists(RuntimeRoot + retired), Is.False, retired);
                Assert.That(File.Exists(RuntimeRoot + retired + ".meta"), Is.False, retired);
            }
            const string ConvenienceHelper = RuntimeRoot + "Foundation/Utilities/UnityObjectUtility.cs";
            Assert.That(File.Exists(ConvenienceHelper), Is.False);
            Assert.That(File.Exists(ConvenienceHelper + ".meta"), Is.False);
            Assert.That(typeof(ApplicationTool).Assembly.GetType("YLFramework.Foundation.UnityObjectUtility"), Is.Null);

            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            var foundation = assemblies.Single(a => a.name == "YLFramework.Runtime");
            Assert.That(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(foundation.name),
                Is.EqualTo(RuntimeRoot + "YLFramework.Runtime.asmdef"));
            Assert.That(foundation.sourceFiles.Select(Path.GetFullPath), Is.SupersetOf(
                Directory.GetFiles(RuntimeRoot + "Foundation", "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath)));
            var adapters = new[] { RuntimeRoot + "Foundation/Timing/Components/TimerObject.cs",
                RuntimeRoot + "Foundation/Events/Unity/AnimationEventListener.cs",
                RuntimeRoot + "Core/MVC/Views/Components/CloseView.cs" }.Select(Path.GetFullPath).ToArray();
            Assert.That(adapters, Is.Not.Empty);
            Assert.That(assemblies.Single(a => a.name == "YLFramework.Runtime").sourceFiles.Select(Path.GetFullPath),
                Is.SupersetOf(adapters));
        }

        [UnityTest]
        public IEnumerator FoundationHasNoFrameworkResourceOrPluginAssemblyDependencies()
            => CompileNeutralSources(AssemblyBuilderFlags.EditorAssembly);

        [UnityTest]
        public IEnumerator NeutralFoundationSourcesCompileForDevelopmentPlayerWithoutEditorReferences()
            => CompileNeutralSources(AssemblyBuilderFlags.DevelopmentBuild);

        private static IEnumerator CompileNeutralSources(AssemblyBuilderFlags flags)
        {
            Assert.That(typeof(ApplicationTool).Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            var root = Path.Combine(Application.dataPath, "YLFramework/Runtime/Foundation");
            // Root已消费Core.UIRootContext，由CoreArchitectureBoundaryTests连同全量Core覆盖；
            // 中立探针仍收集完整Foundation，不能为上层入口附加Core实现或预编译产品。
            var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Assert.That(sources.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(sources.Length));
            Assert.That(sources.Where(path => IsInsideFolder(path, root)), Is.EquivalentTo(
                Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath)));
            Assert.That(sources.Where(path => !IsInsideFolder(path, root)), Is.Empty);
            foreach (var source in sources)
            {
                Assert.That(File.Exists(source), Is.True, source);
            }

            var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            var uiAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "UnityEngine.UI");
            Assert.That(uiAssembly.GetType("UnityEngine.EventSystems.EventSystem"), Is.Not.Null);
            var ui = Path.GetFullPath(uiAssembly.Location);
            Assert.That(ui, Is.EqualTo(Path.GetFullPath(graph.Single(a => a.name == "UnityEngine.UI").outputPath)).IgnoreCase);
            var inputDefines = graph.Single(a => a.name == "YLFramework.Runtime").defines
                .Where(define => define == "ENABLE_INPUT_SYSTEM" || define == "ENABLE_LEGACY_INPUT_MANAGER").ToArray();
            Assert.That(inputDefines, Is.Not.Empty, "The probe must use the product input configuration.");
            var parent = Path.GetTempPath();
            Assert.That(Directory.Exists(parent), Is.True);
            var folder = Path.Combine(parent, "kilo", "YLFramework.Foundation.SourceClosure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var legal = Path.Combine(folder, "LegalFoundationConsumer.cs");
                File.WriteAllText(legal, "internal class LegalFoundationConsumer { " +
                    "public global::YLFramework.Pooling.ObjectPool<object> pool; " +
                    "public global::UnityEngine.UI.Image image; }");
                var forbidden = Path.Combine(folder, "ForbiddenCoreConsumer.cs");
                File.WriteAllText(forbidden, "using Domain = global::YLFramework.Core.Module; " +
                    "internal class ForbiddenCoreConsumer { public Domain module; " +
                    "public global::System.Collections.Generic.List<global::YLFramework.Core.Module> modules; }");
                foreach (var consumer in new[] { legal, forbidden })
                {
                    var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(consumer) + ".dll");
                    var editor = (flags & AssemblyBuilderFlags.EditorAssembly) != 0;
                    var builder = new AssemblyBuilder(output, sources.Append(consumer).ToArray())
                    {
                        flags = flags,
                        buildTarget = BuildTarget.StandaloneWindows64,
                        buildTargetGroup = BuildTargetGroup.Standalone,
                        referencesOptions = ReferencesOptions.UseEngineModules
                    };
                    Assert.That(builder.defaultDefines.Contains("UNITY_EDITOR"), Is.EqualTo(editor));
                    builder.additionalDefines = inputDefines.Except(builder.defaultDefines).ToArray();
                    var defines = builder.defaultDefines.Concat(builder.additionalDefines).Distinct().ToArray();
                    Assert.That(defines.Contains("DEVELOPMENT_BUILD"), Is.EqualTo(!editor));
                    Assert.That(defines.Where(define => define == "ENABLE_INPUT_SYSTEM" || define == "ENABLE_LEGACY_INPUT_MANAGER"),
                        Is.EquivalentTo(inputDefines));
                    var contents = EditorApplication.applicationContentsPath;
                    builder.excludeReferences = builder.defaultReferences.Where(path => !IsAllowedReference(path, contents, ui, editor)).ToArray();
                    builder.additionalReferences = new[] { ui };
                    var references = builder.defaultReferences.Except(builder.excludeReferences)
                        .Concat(builder.additionalReferences).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    Assert.That(references, Is.Not.Empty);
                    Assert.That(references.All(path => IsAllowedReference(path, contents, ui, editor)), Is.True);
                    Assert.That(references.Any(path => Path.GetFileName(path) == "UnityEngine.CoreModule.dll"), Is.True);
                    Assert.That(references.Any(path => Path.GetFileName(path) == "mscorlib.dll" || Path.GetFileName(path) == "netstandard.dll"), Is.True);
                    Assert.That(references.Any(path => Path.GetFileName(path).StartsWith("YLFramework") ||
                        Path.GetFileName(path) == "Newtonsoft.Json.dll"), Is.False);
                    TestContext.WriteLine("Compiling {0} complete Foundation sources with {1}, {2}; defines:\n{3}\nReferences:\n{4}",
                        sources.Length, flags, consumer, string.Join("\n", defines), string.Join("\n", references));
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
                            (System.Text.RegularExpressions.Regex.IsMatch(m.message, @"\bModule\b") ||
                             (System.Text.RegularExpressions.Regex.IsMatch(m.message, @"\bCore\b") && m.message.Contains("YLFramework"))) &&
                            (m.message.Contains("CS0234") || m.message.Contains("CS0246"))), Is.True,
                            "负例必须因Core命名空间或指定Module符号不可解析失败，不能借缺失基础参考制造红态。\n" +
                            string.Join("\n", errors.Select(m => m.message)));
                        Assert.That(File.Exists(output), Is.False);
                    }
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static bool IsAllowedReference(string path, string contents, string ui, bool editor)
        {
            var absolute = Path.GetFullPath(path);
            if (!File.Exists(absolute))
            {
                return false;
            }

            if (string.Equals(absolute, Path.GetFullPath(ui), StringComparison.OrdinalIgnoreCase))
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

        [Test]
        public void FoundationReferencesRejectPremadeProductsAndSubstitutedUGUI()
        {
            var uiAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "UnityEngine.UI");
            var ui = Path.GetFullPath(uiAssembly.Location);
            var contents = EditorApplication.applicationContentsPath;
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.Foundation.ReferenceRejection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var name in new[] { "UnityEngine.UI.dll", "UnityEngine.UI.Fake.dll", "System.Fake.dll",
                    "Unrelated.Library.dll", "External.Resources.dll", "External.StateMachine.dll" })
                {
                    var fake = Path.Combine(folder, name);
                    File.Copy(ui, fake);
                    foreach (bool editor in new[] { false, true })
                    {
                        Assert.That(IsAllowedReference(fake, contents, ui, editor), Is.False, fake);
                    }
                }
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a =>
                    a.GetName().Name.StartsWith("YLFramework", StringComparison.Ordinal) ||
                    a.GetName().Name == "Newtonsoft.Json"))
                {
                    Assert.That(IsAllowedReference(assembly.Location, contents, ui, true), Is.False, assembly.FullName);
                }

                Assert.That(IsAllowedReference(typeof(LogAssert).Assembly.Location, contents, ui, true), Is.False);
                Assert.That(IsAllowedReference(typeof(EditorApplication).Assembly.Location, contents, ui, false), Is.False);
                Assert.That(IsAllowedReference(ui, contents, ui, false), Is.True);
                Assert.That(IsAllowedReference(typeof(UnityEngine.Object).Assembly.Location, contents, ui, false), Is.True);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void NeutralLookupMaskAndDisplayMetadataHaveOneFoundationOwner()
        {
            var foundation = typeof(ApplicationTool).Assembly;
            foreach (string name in new[] { "YLFramework.Foundation.AssemblyTool", "YLFramework.Foundation.LayerMaskUtility",
                "YLFramework.Attributes.CustomClassNameGui", "YLFramework.Attributes.CustomPropertyGui",
                "YLFramework.Attributes.DisableEdit" })
            {
                Assert.That(foundation.GetType(name), Is.Not.Null, name);
                Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType(name) != null), Is.EqualTo(1), name);
            }
            foreach (string oldName in new[] { "YLFramework.Core.AssemblyTools", "YLFramework.Utilities.AssemblyTool.AssemblyTool",
                "YLFramework.Extensions.LayerMaskUtility", "YLFramework.Core.CustomClassNameGUI",
                "YLFramework.Core.CustomPropertyGUI", "YLFramework.Core.DisableEdit",
                "YLFramework.Core.Attributes.CustomClassNameGUI", "YLFramework.Core.Attributes.CustomPropertyGUI",
                "YLFramework.Core.CustomClassNameGui", "YLFramework.Core.CustomPropertyGui",
                "YLFramework.Core.Attributes.CustomClassNameGui", "YLFramework.Core.Attributes.CustomPropertyGui",
                "YLFramework.Attributes.CustomClassNameGUI", "YLFramework.Attributes.CustomPropertyGUI" })
            {
                Assert.That(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType(oldName) != null), Is.False, oldName);
            }
        }

        [Test]
        public void NewFoundationTypesHaveUniqueFullyQualifiedNames()
        {
            foreach (var (type, expectedNamespace) in new[]
            {
                (typeof(ApplicationTool), "YLFramework.Foundation"),
                (typeof(CoroutineStarter), "YLFramework.Async"),
                (typeof(CustomAsyncOperation<>), "YLFramework.Async")
            })
            {
                Assert.That(type.Namespace, Is.EqualTo(expectedNamespace));
                Assert.That(type.Assembly, Is.SameAs(typeof(ApplicationTool).Assembly));
                var definitions = AppDomain.CurrentDomain.GetAssemblies()
                    .Count(assembly => assembly.GetType(type.FullName, false) != null);
                Assert.That(definitions, Is.EqualTo(1), type.FullName);
            }
        }

        [Test]
        public void PoolingUsesFunctionalNamespaceWithoutLegacyAliases()
        {
            var runtime = typeof(ApplicationTool).Assembly;
            Assert.That(runtime.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            foreach (var name in new[] { "ObjectPool`1", "IPool`1", "ListPool`1", "DictionaryPool`2" })
            {
                var fullName = "YLFramework.Pooling." + name;
                var type = runtime.GetType(fullName);
                Assert.That(type, Is.Not.Null, fullName);
                Assert.That(type.IsPublic, Is.True, fullName);
                Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetType(fullName) != null),
                    Is.EqualTo(1), fullName);
                Assert.That(runtime.GetType("YLFramework.Core." + "Pool." + name), Is.Null, name);
            }
        }

        [Test]
        public void SharedPoolTypesHaveOneFoundationOwnerAndOnlyNarrowSessionFriends()
        {
            var foundation = typeof(ApplicationTool).Assembly;
            foreach (var type in new[] { typeof(ObjectPool<>), typeof(IPool<>), typeof(IObjectFactory<>),
                typeof(DefaultObjectFactory<>), typeof(CustomObjectFactory<>), typeof(ListPool<>),
                typeof(DictionaryPool<,>), typeof(PoolCacheOrder), typeof(PoolPrewarmMode) })
            {
                Assert.That(type.Assembly, Is.SameAs(foundation), type.FullName);
                Assert.That(AppDomain.CurrentDomain.GetAssemblies()
                    .Count(assembly => assembly.GetType(type.FullName, false) != null), Is.EqualTo(1), type.FullName);
            }
            var session = foundation.GetType("YLFramework.Singletons.SingletonSessionState", true);
            Assert.That(session.IsNotPublic, Is.True);
            Assert.That(foundation.GetCustomAttributes(typeof(InternalsVisibleToAttribute), false)
                .Cast<InternalsVisibleToAttribute>().Select(attribute => attribute.AssemblyName),
                Is.EquivalentTo(new[] { "YLFramework.Tests.EditMode", "YLFramework.Tests.PlayMode", "YLFramework.Core.Tests.EditMode", "YLFramework.Editor",
                    "YLFramework.Actions.DOTween", "YLFramework.Actions.DOTween.Tests.EditMode", "YLFramework.Actions.DOTween.Tests.PlayMode" }));
        }

        [TestCase("YLFramework.Singletons.Singleton`1", "YLFramework.Core." + "Singleton.Singleton`1")]
        [TestCase("YLFramework.Singletons.MonoSingleton`1", "YLFramework.Core." + "Singleton.MonoSingleton`1")]
        [TestCase("YLFramework.DependencyInjection.IOCContainer", "YLFramework.Core." + "IOC.IOCContainer")]
        [TestCase("YLFramework.DependencyInjection.IIOCContainer", "YLFramework.Core." + "IOC.IIOCContainer")]
        [TestCase("YLFramework.Attributes.DisableEdit", "YLFramework.Foundation." + "Attributes.DisableEdit")]
        [TestCase("YLFramework.Attributes.CustomClassNameGui", "YLFramework.Foundation." + "Attributes.CustomClassNameGui")]
        [TestCase("YLFramework.Attributes.CustomPropertyGui", "YLFramework.Foundation." + "Attributes.CustomPropertyGui")]
        [TestCase("YLFramework.Async.CoroutineStarter", "YLFramework.Foundation." + "CoroutineStarter")]
        [TestCase("YLFramework.Async.CoroutineObservation", "YLFramework.Foundation." + "CoroutineObservation")]
        [TestCase("YLFramework.Async.CustomAsyncOperation`1", "YLFramework.Foundation." + "CustomAsyncOperation`1")]
        [TestCase("YLFramework.Update.UpdateManager", "YLFramework.Foundation." + "UpdateManager")]
        [TestCase("YLFramework.Serialization.SerializedDictionary`2", "YLFramework.Foundation." + "SerializedDictionary`2")]
        [TestCase("YLFramework.Events.TriggerEventListener", "YLFramework.Core." + "TriggerEventListener")]
        [TestCase("YLFramework.Events.CollisionEventListener", "YLFramework.Core." + "CollisionEventListener")]
        [TestCase("YLFramework.Events.AnimationEventListener", "YLFramework.Core." + "AnimationEventListener")]
        [TestCase("YLFramework.Timing.TimerObject", "YLFramework.Core." + "TimerObject")]
        public void FoundationContractsUseFunctionalNamespaces(string fullName, string legacyName)
        {
            var runtime = typeof(ApplicationTool).Assembly;
            var type = runtime.GetType(fullName);
            Assert.That(type, Is.Not.Null, fullName);
            Assert.That(type.IsPublic, Is.True, fullName);
            Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetType(fullName) != null),
                Is.EqualTo(1), fullName);
            Assert.That(runtime.GetType(legacyName), Is.Null, legacyName);
        }

        [Test]
        public void ApplicationStateUsesEditorPlaymodeTransitionState()
        {
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
            Assert.That(ApplicationTool.IsPlaying, Is.EqualTo(EditorApplication.isPlayingOrWillChangePlaymode));
        }

        [Test]
        public void StartingOutsidePlayModeDoesNotCreateHostOrAdvanceEnumerator()
        {
            var before = UnityEngine.Object.FindObjectsByType<CoroutineStarter>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            Assert.That(before, Is.Empty);
            Assert.That(CoroutineStarter.Start(MustNotRun()), Is.Null);
            CoroutineStarter.Stop(null);
            Assert.That(UnityEngine.Object.FindObjectsByType<CoroutineStarter>(FindObjectsInactive.Include,
                FindObjectsSortMode.None), Is.Empty);
        }

        private static IEnumerator MustNotRun()
        {
            Assert.Fail("EditMode must not advance the coroutine.");
            yield break;
        }
    }
}
