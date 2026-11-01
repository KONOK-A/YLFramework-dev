using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using static YLFramework.Tests.TestDependencyGuard;

namespace YLFramework.Tests.Audio
{
    public class AudioSourceIndependenceTests
    {
        [UnityTest]
        public IEnumerator FormalAudioSourceClosureCompilesWithApprovedDependenciesInEditor()
        {
            return CompileSourceClosure(AssemblyBuilderFlags.EditorAssembly);
        }

        [UnityTest]
        public IEnumerator FormalAudioSourceClosureCompilesWithApprovedDependenciesInWindowsDevelopmentProfile()
        {
            return CompileSourceClosure(AssemblyBuilderFlags.DevelopmentBuild);
        }

        private static IEnumerator CompileSourceClosure(AssemblyBuilderFlags flags)
        {
            var runtime = Path.Combine(Application.dataPath, "YLFramework/Runtime");
            // Audio及Root的Core依赖全量收集；不能用缺失UIRootContext的伪闭包证明独立编译。
            var directories = new[]
            {
                "Systems/Audio", "Systems/Action", "Foundation", "Core",
                "Systems/Resource/Catalog", "Systems/Resource/Loading", "Systems/Resource/Lifetime",
                "Systems/Resource/Sources", "Systems/Resource/Instances", "Systems/Resource/Scenes", "Systems/Resource/Integration",
                "Systems/Resource/Content", "Systems/Resource/Updates", "Systems/Resource/Diagnostics"
            };
            var files = new[]
            {
                "YLFrameworkRoot.cs", "Foundation/Update/UpdateManager.cs", "Foundation/Singleton/Internal/SingletonSessionState.cs",
                "Foundation/Pool/Factories/CustomObjectFactory.cs", "Foundation/Pool/Factories/DefaultObjectFactory.cs",
                "Foundation/Singleton/ISingleton.cs", "Foundation/Singleton/Internal/SingletonCreator.cs",
                "Foundation/Singleton/Plain/Singleton.cs", "Foundation/Singleton/Plain/SingletonProperty.cs",
                "Foundation/Singleton/Mono/MonoSingleton.cs", "Foundation/Singleton/Mono/MonoSingletonProperty.cs",
                "Foundation/Singleton/Mono/MonoSingletonHost.cs", "Foundation/Singleton/Mono/MonoSingletonPathAttribute.cs",
                "Foundation/Pool/Object/SafeObjectPool.cs",
                "Foundation/Binding/BindableProperty.cs", "Foundation/Binding/Prefs/PlayerPrefsProperty.cs",
                "Foundation/Binding/Prefs/PlayerPrefsBoolProperty.cs", "Foundation/Binding/Prefs/PlayerPrefsFloatProperty.cs",
                "Systems/Resource/ResourceSystem.cs", "Systems/Resource/ResourceConfiguration.cs",
                "Utilities/Extensions/Unity/ComponentExtension.cs", "Utilities/Extensions/Unity/GameObjectExtension.cs",
                "Utilities/Extensions/Unity/ObjectExtension.cs", "Utilities/Extensions/Math/VectorExtension.cs",
                 "Foundation/Utilities/LayerMaskUtility.cs"
            };
            var sources = directories.SelectMany(directory => Directory.GetFiles(Path.Combine(runtime, directory),
                    "*.cs", SearchOption.AllDirectories))
                .Concat(files.Select(file => Path.Combine(runtime, file)))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            foreach (var source in sources)
            {
                Assert.That(File.Exists(source), Is.True, source);
            }

            Assert.That(sources.All(source => IsInsideFolder(source, runtime)), Is.True);
            Assert.That(sources.Where(source => IsInsideFolder(source, Path.Combine(runtime, "Core"))),
                Is.EquivalentTo(Directory.GetFiles(Path.Combine(runtime, "Core"), "*.cs", SearchOption.AllDirectories)));
            Assert.That(Directory.GetFiles(Path.Combine(runtime, "Systems/Audio"), "*.cs", SearchOption.AllDirectories), Is.Not.Empty);

            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.Audio.SourceClosure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var output = Path.Combine(folder, "AudioSourceClosure.dll");
                var builder = new AssemblyBuilder(output, sources)
                {
                    flags = flags,
                    buildTarget = BuildTarget.StandaloneWindows64,
                    buildTargetGroup = BuildTargetGroup.Standalone,
                    referencesOptions = ReferencesOptions.UseEngineModules,
                    additionalDefines = Array.Empty<string>()
                };
                var editor = (flags & AssemblyBuilderFlags.EditorAssembly) != 0;
                var profile = CompilationPipeline.GetAssemblies(editor ? AssembliesType.Editor : AssembliesType.Player).Single(x => x.name == "YLFramework.Runtime");
                builder.compilerOptions = new ScriptCompilerOptions
                {
                    ApiCompatibilityLevel = profile.compilerOptions.ApiCompatibilityLevel,
                    AllowUnsafeCode = profile.compilerOptions.AllowUnsafeCode
                };
                var json = profile.compiledAssemblyReferences.Single(x => Path.GetFileName(x) == "Newtonsoft.Json.dll");
                var contentCrypto = profile.compiledAssemblyReferences.Single(x => Path.GetFileName(x) == "BouncyCastle.Cryptography.dll");
                Assert.That(TestDependencyGuard.IsApprovedBouncyCastleReference(contentCrypto), Is.True);
                Assert.That(File.Exists(json), Is.True);
                Assert.That(builder.defaultDefines.Contains("UNITY_EDITOR"), Is.EqualTo(editor));
                var contents = EditorApplication.applicationContentsPath;
                var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
                var references = builder.defaultReferences;
                builder.excludeReferences = references.Where(path => !IsAllowedReference(path, contents, ui, editor, json)).ToArray();
                builder.additionalReferences = new[] { ui, json, contentCrypto };
                var effective = references.Except(builder.excludeReferences).Concat(builder.additionalReferences).Distinct().ToArray();
                Assert.That(effective.All(path => IsAllowedReference(path, contents, ui, editor, json)), Is.True);
                Assert.That(effective.Any(path => Path.GetFileName(path) == "UnityEngine.CoreModule.dll"), Is.True);
                Assert.That(effective.Any(path => Path.GetFileName(path) == "mscorlib.dll" || Path.GetFileName(path) == "netstandard.dll"), Is.True);
                if (editor)
                {
                    foreach (var type in new[] { typeof(PlayModeStateChange), typeof(InitializeOnLoadMethodAttribute), typeof(BuildTarget) })
                    {
                        Assert.That(effective.Select(Path.GetFullPath).Contains(Path.GetFullPath(type.Assembly.Location), StringComparer.OrdinalIgnoreCase), Is.True, type.FullName);
                    }
                }

                CompilerMessage[] messages = null;
                builder.buildFinished += (_, result) => messages = result;
                Assert.That(builder.Build(), Is.True, "AssemblyBuilder could not start; run this fixture without another compilation in progress.");
                while (builder.status != AssemblyBuilderStatus.Finished)
                {
                    yield return null;
                }

                Assert.That(messages, Is.Not.Null);
                Assert.That(messages.Where(message => message.type == CompilerMessageType.Error)
                    .Select(message => message.file + ":" + message.line + ": " + message.message), Is.Empty);
                Assert.That(File.Exists(output), Is.True);
                TestContext.WriteLine("Compiled {0} production source files with {1}; references:\n{2}",
                    sources.Length, flags, string.Join("\n", effective));
                foreach (var warning in messages.Where(message => message.type == CompilerMessageType.Warning))
                {
                    TestContext.WriteLine(warning.file + ":" + warning.line + ": " + warning.message);
                }
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }

        [Test]
        public void ReferenceAllowlistRejectsProjectAndOptionalFrameworkAssemblies()
        {
            var project = Directory.GetParent(Application.dataPath).FullName;
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            foreach (var name in new[] { "YLFramework.Runtime.dll", "YLFramework.Foundation.Runtime.dll",
                "YLFramework.Core.Runtime.dll", "YLFramework.Utilities.ZipTool.dll", "YLFramework.Actions.DOTween.dll",
                "Unrelated.Library.dll", "External.Resources.dll", "External.StateMachine.dll", "Assembly-CSharp.dll", "System.Fake.dll", "UnityEngine.Fake.dll" })
            {
                Assert.That(IsAllowedReference(Path.Combine(project, "Library/ScriptAssemblies", name), contents, ui, true), Is.False, name);
            }

            foreach (var name in new[] { "Sirenix.OdinInspector.Attributes.dll", "Sirenix.Serialization.dll", "Sirenix.Utilities.dll" })
            {
                Assert.That(IsAllowedReference(Path.Combine(Application.dataPath, "YLFramework/Plugins/Sirenix/Assemblies", name), contents, ui, true), Is.False, name);
            }

            Assert.That(IsAllowedReference(typeof(AudioSourceIndependenceTests).Assembly.Location, contents, ui, true), Is.False);
            Assert.That(IsAllowedReference(typeof(LogAssert).Assembly.Location, contents, ui, true), Is.False);
            Assert.That(IsAllowedReference(typeof(EditorApplication).Assembly.Location, contents, ui, false), Is.False);
            Assert.That(IsAllowedReference(typeof(EditorApplication).Assembly.Location, contents, ui, true), Is.True);
            Assert.That(IsAllowedReference(Path.Combine(contents, "Managed/UnityEngine/UnityEditor.TestRunner.dll"), contents, ui, true), Is.False);
            Assert.That(IsAllowedReference(Path.Combine(contents, "Managed/UnityEngine-Fake/UnityEditor.CoreModule.dll"), contents, ui, true), Is.False);
            Assert.That(IsAllowedReference(Path.Combine(contents, "Managed/UnityEngine/../Untrusted/UnityEditor.CoreModule.dll"), contents, ui, true), Is.False);
            Assert.That(IsAllowedReference(Path.Combine(project, "Library/ScriptAssemblies/UnityEditor.CoreModule.dll"), contents, ui, true), Is.False);
            Assert.That(IsAllowedReference(typeof(UnityEngine.Object).Assembly.Location, contents, ui, false), Is.True);
            Assert.That(IsAllowedReference(ui, contents, ui, false), Is.True);
        }

    }
}
