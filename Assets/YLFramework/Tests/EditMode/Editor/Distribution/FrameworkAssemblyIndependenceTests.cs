using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;
using Assembly = UnityEditor.Compilation.Assembly;
using static YLFramework.Tests.TestDependencyGuard;

namespace YLFramework.Tests.Resource
{
    public class FrameworkAssemblyIndependenceTests
    {
        [Test]
        public void AcceptanceAssemblyUsesOnlyApprovedFrameworkAndPlatformDependencies()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .Single(value => value.GetName().Name == "YLFramework.UI.Acceptance");
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                Assert.IsFalse(reference.Name.StartsWith("Project.Game.", StringComparison.Ordinal), reference.FullName);
                var dependency = System.Reflection.Assembly.Load(reference);
                Assert.That(IsApprovedBinary(dependency, true) ||
                    (reference.Name == "Unity.InputSystem" && IsGraphOutput(dependency)), Is.True, reference.FullName);
            }
        }

        [TestCase("YLFramework.Audio.AudioKit", "YLFramework.Services." + "Audio.AudioKit")]
        [TestCase("YLFramework.Resource.ResourceSystem", "YLFramework.Services." + "Resource.ResourceSystem")]
        [TestCase("YLFramework.Save.SaveManager", "YLFramework.Services." + "Save.SaveManager")]
        [TestCase("YLFramework.Http.HttpManager", "YLFramework.Services." + "Http.HttpManager")]
        [TestCase("YLFramework.Skill.SkillManager", "YLFramework." + "SkillSystem.SkillManager")]
        [TestCase("YLFramework.Buff.BuffManager", "YLFramework." + "BuffSystem.BuffManager")]
        [TestCase("YLFramework.Mission.MissionManager", "YLFramework." + "MissionSystem.MissionManager")]
        [TestCase("YLFramework.Equipment.EquipmentManager", "YLFramework." + "EquipmentSystem.EquipmentManager")]
        [TestCase("YLFramework.RedDots.ReddotManager", "YLFramework." + "ReddotSystem.ReddotManager")]
        [TestCase("YLFramework.Character2D.CharacterController2D", "YLFramework.Character." + "Controller2D.CharacterController2D")]
        [TestCase("YLFramework.Extensions.GameObjectExtension", "YLFramework.Utilities." + "Extensions.GameObjectExtension")]
        [TestCase("YLFramework.Grids.NormalGrid`1", "YLFramework.Utilities." + "Grid.NormalGrid`1")]
        [TestCase("YLFramework.Tables.Table`1", "YLFramework.Utilities." + "Table.Table`1")]
        public void FunctionalDomainsKeepOneRuntimeIdentity(string fullName, string legacyName)
        {
            var runtime = typeof(ResourceSystem).Assembly;
            Assert.That(runtime.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            var type = runtime.GetType(fullName);
            Assert.That(type, Is.Not.Null, fullName);
            Assert.That(type.IsPublic, Is.True, fullName);
            Assert.That(runtime.GetType(legacyName), Is.Null, legacyName);
        }

        [Test]
        public void CompressionWrapperMovesWithoutRenamingTheBundledVendor()
        {
            var runtime = typeof(ResourceSystem).Assembly;
            Assert.That(runtime.GetType("YLFramework.Compression.ZipUtility"), Is.Not.Null);
            Assert.That(runtime.GetType("YLFramework.Utilities." + "ZipTool.ZipUtility"), Is.Null);
            Assert.That(runtime.GetType("YLFramework.Utilities.ZipTool.Zip.ZipFile"), Is.Not.Null);
        }

        [TestCase(AssembliesType.Editor, "YLFramework.Runtime")]
        [TestCase(AssembliesType.Editor, "YLFramework.Editor")]
        [TestCase(AssembliesType.Player, "YLFramework.Runtime")]
        public void ActualCompilationAndBinaryClosuresUseOnlyApprovedDependencies(AssembliesType profile, string name)
        {
            Assert.That(EditorUtility.scriptCompilationFailed, Is.False);
            var graph = CompilationPipeline.GetAssemblies(profile).Single(a => a.name == name);
            Assert.That(graph.sourceFiles, Is.Not.Empty);
            var editor = profile == AssembliesType.Editor;
            var testInputs = graph.defines.Contains("UNITY_INCLUDE_TESTS");
            var allowedGraphNames = new[] { "YLFramework.Runtime", "UnityEngine.UI" }
                .Concat(editor ? new[] { "YLFramework.Editor", "UnityEditor.UI" } : Array.Empty<string>())
                .Concat(testInputs ? new[] { "UnityEngine.TestRunner", "UnityEditor.TestRunner" } : Array.Empty<string>()).ToArray();
            var pending = new Stack<Assembly>(new[] { graph });
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                var assembly = pending.Pop();
                if (!visited.Add(assembly.name))
                {
                    continue;
                }

                CollectionAssert.Contains(allowedGraphNames, assembly.name, assembly.name);
                foreach (var reference in assembly.assemblyReferences)
                {
                    pending.Push(reference);
                }
            }
            // UGUI 自身也会收到工程全局自动插件输入；主体必须白名单隔离这些输入，
            // 第三方程序集的真实必要依赖则由下面的已编译 AssemblyRef 闭包核对。
            foreach (var reference in graph.compiledAssemblyReferences)
            {
                Assert.That(IsAllowedReference(reference, EditorApplication.applicationContentsPath,
                    typeof(UnityEngine.UI.Image).Assembly.Location, editor, typeof(Newtonsoft.Json.JsonConvert).Assembly.Location) ||
                    (testInputs && Path.GetFullPath(reference) == Path.GetFullPath(typeof(TestAttribute).Assembly.Location)),
                    Is.True, reference);
            }

            var binaries = new Stack<System.Reflection.Assembly>(new[] { System.Reflection.Assembly.Load(name) });
            visited.Clear();
            while (binaries.Count != 0)
            {
                var assembly = binaries.Pop();
                if (!visited.Add(assembly.FullName))
                {
                    continue;
                }

                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    var dependency = System.Reflection.Assembly.Load(reference);
                    // 当前 AppDomain 装载的是 Editor 二进制；Player 隔离由对应编译图及下方独立源码编译验证。
                    Assert.That(IsApprovedBinary(dependency, true) || IsAllowedSdkBinaryDependency(assembly.Location,
                        dependency.Location, EditorApplication.applicationContentsPath), Is.True,
                        assembly.GetName().Name + " -> " + reference + " @ " + dependency.Location);
                    binaries.Push(dependency);
                }
            }
            TestContext.WriteLine("{0}: {1} sources; binary closure: {2}", profile, graph.sourceFiles.Length,
                string.Join(", ", visited.OrderBy(x => x, StringComparer.Ordinal)));
        }

        private static bool IsGraphOutput(System.Reflection.Assembly assembly)
        {
            var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Single(value => value.name == assembly.GetName().Name);
            return string.Equals(Path.GetFullPath(assembly.Location), Path.GetFullPath(graph.outputPath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsApprovedBinary(System.Reflection.Assembly assembly, bool editor)
        {
            var name = assembly.GetName().Name;
            if (name == "YLFramework.Runtime" || name == "UnityEngine.UI" ||
                (editor && (name == "YLFramework.Editor" || name == "UnityEditor.UI")))
            {
                return IsGraphOutput(assembly);
            }

            return IsAllowedReference(assembly.Location, EditorApplication.applicationContentsPath,
                typeof(UnityEngine.UI.Image).Assembly.Location, editor, typeof(Newtonsoft.Json.JsonConvert).Assembly.Location);
        }

        [Test]
        public void ReferenceAllowlistOnlyAddsTheExactApprovedBouncyCastleFile()
        {
            var project = Directory.GetParent(Application.dataPath).FullName;
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            var approved = Path.Combine(Application.dataPath, "YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll");
            bool Allowed(string path) => IsApprovedBouncyCastleReference(path) ||
                IsAllowedReference(path, contents, ui, true);
            Assert.That(IsApprovedBouncyCastleReference(approved), Is.True, "The official 2.7.0 netstandard2.0 file must match both its path and SHA-256.");
            foreach (var path in new[]
            {
                Path.Combine(project, "Library/ScriptAssemblies/BouncyCastle.Cryptography.dll"),
                Path.Combine(Application.dataPath, "Plugins/BouncyCastle.Cryptography.dll"),
                Path.Combine(Application.dataPath, "YLFramework/Plugins/Other/BouncyCastle.Cryptography.dll"),
                Path.Combine(Application.dataPath, "YLFramework/Plugins/BouncyCastle/BouncyCastle.Crypto.dll"),
                Path.Combine(Application.dataPath, "YLFramework/Plugins/BouncyCastle/System.Fake.dll"),
                Path.Combine(Application.dataPath, "YLFramework/Plugins/BouncyCastle/UnityEngine.Fake.dll"),
                Path.Combine(Application.dataPath, "YLFramework/Plugins/BouncyCastle/Unrelated.Library.dll"),
                Path.Combine(Application.dataPath, "YLFramework/Plugins/Sirenix/Sirenix.OdinInspector.Attributes.dll")
            })
            {
                Assert.That(IsApprovedBouncyCastleReference(path), Is.False, path);
                Assert.That(Allowed(path), Is.False, path);
            }
        }

        [Test]
        public void EditorRootReferencesBelongToTheActiveInstallation()
        {
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            var references = Directory.GetFiles(Path.Combine(contents, "Managed"), "UnityEditor*.dll", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path) != "UnityEditor.TestRunner.dll").ToArray();
            Assert.That(references, Is.Not.Empty);
            foreach (var reference in references)
            {
                Assert.That(IsAllowedReference(reference, contents, ui, true), Is.True, reference);
                Assert.That(IsAllowedReference(reference, contents, ui, false), Is.False, reference);
            }

            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.EditorReference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var fake = Path.Combine(folder, Path.GetFileName(references[0]));
                File.Copy(references[0], fake);
                Assert.That(IsAllowedReference(fake, contents, ui, true), Is.False, fake);
                Assert.That(IsAllowedReference(Path.Combine(contents, "Managed/UnityEditor.TestRunner.dll"), contents, ui, true), Is.False);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void EditorPlatformReferencesBelongToTheActiveInstallation()
        {
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            var references = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.name == "YLFramework.Editor")
                .compiledAssemblyReferences.Where(path => IsInsideFolder(path, Path.Combine(contents, "PlaybackEngines")) &&
                    (Path.GetFileName(path).StartsWith("Unity.", StringComparison.Ordinal) ||
                     Path.GetFileName(path).StartsWith("UnityEditor.", StringComparison.Ordinal))).ToArray();
            Assert.That(references, Is.Not.Empty);
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.PlatformReference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var reference in references)
                {
                    Assert.That(IsAllowedReference(reference, contents, ui, true), Is.True, reference);
                    Assert.That(IsAllowedReference(reference, contents, ui, false), Is.False, reference);
                    var fake = Path.Combine(folder, Path.GetFileName(reference));
                    File.Copy(reference, fake);
                    Assert.That(IsAllowedReference(fake, contents, ui, true), Is.False, fake);
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void JsonVariantsBelongToTheSameLockedOfficialPackage()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.nuget.newtonsoft-json/Runtime/Newtonsoft.Json.dll");
            Assert.That(package, Is.Not.Null);
            Assert.That(package.name, Is.EqualTo("com.unity.nuget.newtonsoft-json"));
            Assert.That(package.version, Is.EqualTo("3.2.1"));
            Assert.That(package.source, Is.EqualTo(UnityEditor.PackageManager.PackageSource.Registry));
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            var variants = new[] { Path.Combine(package.resolvedPath, "Runtime/Newtonsoft.Json.dll"),
                Path.Combine(package.resolvedPath, "Runtime/AOT/Newtonsoft.Json.dll") };
            foreach (var reference in variants)
            {
                Assert.That(File.Exists(reference), Is.True, reference);
                foreach (var json in variants)
                {
                    Assert.That(IsAllowedReference(reference, contents, ui, true, json), Is.True, reference);
                    Assert.That(IsAllowedReference(reference, contents, ui, false, json), Is.True, reference);
                }
                Assert.That(IsAllowedReference(reference, contents, ui, true), Is.False, "Json must be explicitly requested.");
            }

            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.JsonReference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var fake = Path.Combine(folder, "Newtonsoft.Json.dll");
                File.Copy(variants[0], fake);
                Assert.That(IsAllowedReference(fake, contents, ui, true, variants[0]), Is.False);
                Assert.That(IsAllowedReference(fake, contents, ui, false, fake), Is.False);
                Assert.That(IsAllowedReference(variants[0], contents, ui, true, fake), Is.False);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void Win32PrimitiveReferencesBelongToTheActiveInstallation()
        {
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            var references = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(assembly => assembly.name == "YLFramework.Runtime" || assembly.name == "YLFramework.Editor")
                .SelectMany(assembly => assembly.compiledAssemblyReferences)
                .Where(path => Path.GetFileName(path) == "Microsoft.Win32.Primitives.dll").Distinct().ToArray();
            Assert.That(references, Is.Not.Empty);
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.Win32Reference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var reference in references)
                {
                    Assert.That(IsAllowedReference(reference, contents, ui, true), Is.True, reference);
                    Assert.That(IsAllowedReference(reference, contents, ui, false), Is.True, reference);
                    foreach (var name in new[] { "Microsoft.Win32.Unrelated.dll", "Microsoft.Win32.Primitives.Extra.dll" })
                    {
                        Assert.That(IsAllowedReference(Path.Combine(Path.GetDirectoryName(reference), name), contents, ui, true), Is.False, name);
                    }
                }
                var fake = Path.Combine(folder, "Microsoft.Win32.Primitives.dll");
                File.Copy(references[0], fake);
                Assert.That(IsAllowedReference(fake, contents, ui, true), Is.False, fake);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void SdkInternalReferencesDoNotAuthorizeProjectCopiesOrDirectProductDependencies()
        {
            var contents = EditorApplication.applicationContentsPath;
            var parent = typeof(UnityEditor.Editor).Assembly;
            var dependency = System.Reflection.Assembly.Load(parent.GetReferencedAssemblies().Single(reference => reference.Name == "Unity.Cecil"));
            Assert.That(IsAllowedSdkBinaryDependency(parent.Location, dependency.Location, contents), Is.True);
            Assert.That(IsApprovedBinary(dependency, true), Is.False, "An SDK private library is not a direct product dependency.");
            Assert.That(IsAllowedSdkBinaryDependency(typeof(ResourceSystem).Assembly.Location, dependency.Location, contents), Is.False);
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.SdkReference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var fake = Path.Combine(folder, Path.GetFileName(dependency.Location));
                File.Copy(dependency.Location, fake);
                Assert.That(IsAllowedSdkBinaryDependency(parent.Location, fake, contents), Is.False);
                Assert.That(IsAllowedSdkBinaryDependency(fake, dependency.Location, contents), Is.False);
                foreach (var name in new[] { "Newtonsoft.Json.dll", "BouncyCastle.Cryptography.dll", "UnityEngine.TestRunner.dll",
                    "UnityEditor.TestRunner.dll", "nunit.framework.dll" })
                {
                    Assert.That(IsAllowedSdkBinaryDependency(parent.Location, Path.Combine(contents, "Managed", name), contents), Is.False, name);
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void FormalResourceHasNoSecondLegacyAuthorityOrImporterMetadataEntry()
        {
            foreach (var name in new[] { "ResManager", "ServiceManager", "ResLoader", "IResLoader", "ResFactory", "IResCreator",
                "IRes", "Res", "ResKey", "ResTable", "GameObjectLoader", "SubProject", "ResData", "AssetBundleSetting",
                "AssetBundleHelper", "HotUpdateManager", "FakeResServer", "BinarySerializer", "ZipFileHelper" })
            {
                Assert.That(typeof(ResourceSystem).Assembly.GetType("YLFramework.Resource." + name), Is.Null, name);
            }

            Assert.That(typeof(YLFramework.Editor.Resource.ResourceProject).Assembly
                .GetType("YLFramework.Editor.Resource.SubProjectData"), Is.Null);
        }

        [UnityTest]
        public IEnumerator CompleteRuntimeAndEditorSourcesCompileWithOnlyApprovedDependencies()
        {
            return CompileSubjects(true);
        }

        [UnityTest]
        public IEnumerator CompleteRuntimeSourcesCompileWithOnlyApprovedDependenciesInWindowsDevelopmentProfile()
        {
            return CompileSubjects(false);
        }

        private static IEnumerator CompileSubjects(bool editor)
        {
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.SubjectClosure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var profiles = CompilationPipeline.GetAssemblies(editor ? AssembliesType.Editor : AssembliesType.Player);
                string runtimeOutput = null;
                foreach (var name in editor ? new[] { "YLFramework.Runtime", "YLFramework.Editor" } : new[] { "YLFramework.Runtime" })
                {
                    var graph = profiles.Single(a => a.name == name);
                    var root = "Assets/YLFramework/" + (name == "YLFramework.Runtime" ? "Runtime/" : "Editor/");
                    var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
                    CollectionAssert.AreEquivalent(sources.Select(Path.GetFullPath), graph.sourceFiles.Select(Path.GetFullPath),
                        "Compile every subject source, not a hand-picked closure.");
                    var output = Path.Combine(folder, name + ".dll");
                    var builder = new AssemblyBuilder(output, sources)
                    {
                        flags = editor ? AssemblyBuilderFlags.EditorAssembly : AssemblyBuilderFlags.DevelopmentBuild,
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
                    var json = graph.compiledAssemblyReferences.Single(p => Path.GetFileName(p) == "Newtonsoft.Json.dll");
                    var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
                    bool Allowed(string path) => IsApprovedBouncyCastleReference(path) ||
                        IsAllowedReference(path,
                            EditorApplication.applicationContentsPath, ui, editor, json);
                    // AssemblyBuilder 的 defaultReferences 在本 Editor 仍返回 netstandard shims；
                    // 使用真实 profile 的 BCL/Unity 输入，避免把 .NET 4.8 的 CodeDom 错当成缺失产品依赖。
                    var trusted = graph.allReferences.Select(Path.GetFullPath).Where(Allowed).ToArray();
                    // excludeReferences 会在合并 additionalReferences 之后过滤，不能排除仍要提供的 BCL。
                    builder.excludeReferences = builder.defaultReferences
                        .Where(p => !trusted.Contains(Path.GetFullPath(p), StringComparer.OrdinalIgnoreCase)).ToArray();
                    builder.additionalReferences = trusted.Concat(runtimeOutput == null ? Array.Empty<string>() : new[] { runtimeOutput }).ToArray();
                    var effective = builder.defaultReferences.Except(builder.excludeReferences).Concat(builder.additionalReferences)
                        .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    Assert.That(effective.All(p => Allowed(p) || p == runtimeOutput), Is.True);
                    Assert.That(effective.Any(p => new[] { "ICSharpCode.SharpZipLib.dll", "EPPlus.dll", "MiniExcel.dll" }
                        .Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)), Is.False);
                    Assert.That(effective.Count(p => Path.GetFileName(p) == "UnityEngine.UI.dll"), Is.EqualTo(1));
                    Assert.That(effective.Count(p => Path.GetFileName(p) == "Newtonsoft.Json.dll"), Is.EqualTo(1));
                    if (name == "YLFramework.Runtime")
                    {
                        Assert.That(effective.Count(IsApprovedBouncyCastleReference), Is.EqualTo(1));
                    }

                    Assert.That(effective.Any(p => Path.GetFileName(p) == "mscorlib.dll"), Is.True);
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }

                    Assert.That(messages, Is.Not.Null);
                    Assert.That(messages.Where(m => m.type == CompilerMessageType.Error)
                        .Select(m => m.file + ":" + m.line + ": " + m.message), Is.Empty);
                    Assert.That(File.Exists(output), Is.True);
                    TestContext.WriteLine("Compiled {0}: {1} sources, {2}; references:\n{3}", name, sources.Length, builder.flags,
                        string.Join("\n", effective));
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
