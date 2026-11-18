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
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
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

        [Test]
        public void CompressionWrapperMovesWithoutRenamingTheBundledVendor()
        {
            var runtime = typeof(ResourceSystem).Assembly;
            Assert.That(runtime.GetType("YLFramework.Compression.ZipUtility"), Is.Not.Null);
            Assert.That(runtime.GetType("YLFramework.Utilities." + "ZipTool.ZipUtility"), Is.Null);
            Assert.That(runtime.GetType("YLFramework.Utilities.ZipTool.Zip.ZipFile"), Is.Not.Null);
        }

        [TestCase("Unity.Addressables", false)]
        [TestCase("Unity.ResourceManager", false)]
        [TestCase("Unity.Profiling.Core", false)]
        [TestCase("Unity.Addressables", true)]
        [TestCase("Unity.ResourceManager", true)]
        [TestCase("Unity.Profiling.Core", true)]
        [TestCase("Unity.Addressables.Editor", true)]
        [TestCase("Unity.ScriptableBuildPipeline", true)]
        [TestCase("Unity.ScriptableBuildPipeline.Editor", true)]
        public void OfficialAddressablesReferencesUseTheActualCompiledPackageOutput(string name, bool editor)
        {
            var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.name == name);
            var loaded = System.Reflection.Assembly.Load(name);
            Assert.That(Path.GetFullPath(loaded.Location), Is.EqualTo(Path.GetFullPath(graph.outputPath)).IgnoreCase);
            Assert.That(graph.sourceFiles, Is.Not.Empty);
            Assert.That(IsAllowedReference(loaded.Location, EditorApplication.applicationContentsPath,
                typeof(UnityEngine.UI.Image).Assembly.Location, editor), Is.True, name);
            TestContext.WriteLine(name + " actualOutput=" + graph.outputPath + " loaded=" + loaded.Location);
        }

        [Test]
        public void OfficialAddressablesNamesDoNotAuthorizeCopiedUnknownEditorOrTestReferences()
        {
            var directory = Path.Combine(Path.GetTempPath(), "YLDependencyGuard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var contents = EditorApplication.applicationContentsPath;
                var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
                var names = new[] { "Unity.Addressables", "Unity.ResourceManager", "Unity.Profiling.Core", "Unity.Addressables.Editor",
                    "Unity.ScriptableBuildPipeline", "Unity.ScriptableBuildPipeline.Editor" };
                foreach (var name in names)
                {
                    var actual = System.Reflection.Assembly.Load(name).Location;
                    var copy = Path.Combine(directory, name + ".dll");
                    File.Copy(actual, copy);
                    Assert.That(IsAllowedReference(copy, contents, ui, true), Is.False, "Even byte-identical local copies are not compiler provenance: " + copy);
                    Assert.That(IsAllowedReference(copy, contents, ui, false), Is.False, copy);
                    if (name == "Unity.Addressables.Editor" || name.StartsWith("Unity.ScriptableBuildPipeline", StringComparison.Ordinal))
                    {
                        Assert.That(IsAllowedReference(actual, contents, ui, false), Is.False, name);
                    }
                }
                foreach (var name in new[] { "Unity.Addressables.Tests", "Unity.AddressablesExtra", "Unity.ResourceManager.Editor",
                    "Unity.Profiling.Core.Tests", "Unity.Unrelated", "UnityEditor.TestRunner", "UnityEngine.TestRunner", "nunit.framework" })
                {
                    var copy = Path.Combine(directory, name + ".dll");
                    File.Copy(Path.Combine(directory, "Unity.Addressables.dll"), copy);
                    Assert.That(IsAllowedReference(copy, contents, ui, true), Is.False, name);
                    Assert.That(IsAllowedReference(copy, contents, ui, false), Is.False, name);
                }
                foreach (var name in new[] { "UnityEditor.TestRunner", "UnityEngine.TestRunner", "nunit.framework" })
                {
                    var actual = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == name).Location;
                    Assert.That(IsAllowedReference(actual, contents, ui, false), Is.False, name);
                    Assert.That(IsAllowedReference(actual, contents, ui, true), Is.False, "Test SDK dependencies are not direct product references: " + name);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void MainSubjectsCompileEverySourceFromTheirActualResolvedPackage()
        {
            var package = PackageInfo.GetAllRegisteredPackages().Single(value => value.name == "com.kano.ylframework");
            var installed = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(package.resolvedPath, "package.json")));
            Assert.That((string)installed["name"], Is.EqualTo(package.name));
            Assert.That((string)installed["version"], Is.EqualTo(package.version));
            foreach (var name in new[] { "YLFramework.Runtime", "YLFramework.Editor" })
            {
                var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.name == name);
                VerifySubjectSources(graph, package.resolvedPath);
            }
        }

        private static string[] VerifySubjectSources(Assembly graph, string resolvedRoot)
        {
            bool runtime = graph.name == "YLFramework.Runtime";
            var root = Path.Combine(resolvedRoot, runtime ? "Runtime" : "Editor");
            var actual = graph.sourceFiles.Select(source => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(source))).ToArray();
            var expected = new HashSet<string>(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            if (runtime)
            {
                var adapter = Path.Combine(root, "Systems/Action/Integration/DOTween");
                var definition = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(adapter, "YLFramework.Actions.DOTween.asmdef")));
                Assert.That((string)definition["name"], Is.EqualTo("YLFramework.Actions.DOTween"));
                Assert.That(definition["defineConstraints"].Values<string>(), Is.EquivalentTo(new[] { "DOTWEEN" }));
                var adapterSources = Directory.GetFiles(adapter, "*.cs", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray();
                Assert.That(adapterSources, Is.Not.Empty);
                foreach (var source in adapterSources)
                    Assert.That(expected.Remove(source), Is.True, source);
            }
            Assert.That(actual, Is.Not.Empty);
            Assert.That(actual.All(source => File.Exists(source) && IsInsideFolder(source, root)), Is.True, graph.name);
            Assert.That(actual.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(actual.Length), graph.name);
            Assert.That(expected.SetEquals(actual), Is.True, $"{graph.name} missing: {string.Join(", ", expected.Except(actual, StringComparer.OrdinalIgnoreCase))}; " +
                $"foreign: {string.Join(", ", actual.Where(source => !expected.Contains(source)))}");
            return actual;
        }

        [TestCase("lock-source")]
        [TestCase("lock-version")]
        [TestCase("registry-url")]
        [TestCase("manifest-reference")]
        [TestCase("depth")]
        [TestCase("dependencies")]
        [TestCase("missing-lock")]
        public void OfficialRegistryIdentityRejectsAlteredProfileEvidence(string alteration)
        {
            var package = PackageInfo.GetAllRegisteredPackages().Single(value => value.name == "com.unity.addressables");
            var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "../Packages/manifest.json")));
            var locked = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "../Packages/packages-lock.json")));
            Assert.That(IsOfficialRegistryIdentity(package, manifest, locked), Is.True, "The positive control must be this profile's actual installed package.");
            var entry = (Newtonsoft.Json.Linq.JObject)locked["dependencies"][package.name];
            switch (alteration)
            {
                case "lock-source":
                    entry["source"] = "local";
                    break;
                case "lock-version":
                    entry["version"] = "0.0.0";
                    break;
                case "registry-url":
                    entry["url"] = "https://registry.example.invalid";
                    break;
                case "manifest-reference":
                    manifest["dependencies"][package.name] = "file:../LocalAddressables";
                    break;
                case "depth":
                    entry["depth"] = package.isDirectDependency ? 1 : 0;
                    break;
                case "dependencies":
                    entry["dependencies"]["com.unity.unrelated"] = "99.0.0";
                    break;
                case "missing-lock":
                    ((Newtonsoft.Json.Linq.JObject)locked["dependencies"]).Remove(package.name);
                    break;
                default:
                    Assert.Fail(alteration);
                    break;
            }
            Assert.That(IsOfficialRegistryIdentity(package, manifest, locked), Is.False, alteration);
        }

        [TestCase(AssembliesType.Editor, "YLFramework.Runtime")]
        [TestCase(AssembliesType.Editor, "YLFramework.Editor")]
        [TestCase(AssembliesType.PlayerWithoutTestAssemblies, "YLFramework.Runtime")]
        public void ActualCompilationAndBinaryClosuresUseOnlyApprovedDependencies(AssembliesType profile, string name)
        {
            Assert.That(EditorUtility.scriptCompilationFailed, Is.False);
            var graph = CompilationPipeline.GetAssemblies(profile).Single(a => a.name == name);
            Assert.That(graph.sourceFiles, Is.Not.Empty);
            var editor = profile == AssembliesType.Editor;
            var allowedGraphNames = new[] { "YLFramework.Runtime", "UnityEngine.UI" }
                .Concat(editor ? new[] { "YLFramework.Editor", "UnityEditor.UI" } : Array.Empty<string>()).ToArray();
            if (!editor)
            {
                Assert.That(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(value => value.name)
                    .Where(value => value.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        value.IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0 || value.EndsWith(".Editor", StringComparison.Ordinal)), Is.Empty);
            }
            var pending = new Stack<Assembly>(new[] { graph });
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                var assembly = pending.Pop();
                if (!visited.Add(assembly.name))
                {
                    continue;
                }

                Assert.That(allowedGraphNames.Contains(assembly.name) || IsApprovedPackageGraph(assembly, editor) ||
                    (editor && IsApprovedPackageGraph(assembly, true, true)), Is.True, assembly.name);
                if (assembly.name == "YLFramework.Runtime")
                {
                    Assert.That(assembly.assemblyReferences.Any(reference => reference.name.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        reference.name.IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0), Is.False, "Runtime cannot acquire compiler test inputs.");
                }
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
                    (editor && name == "YLFramework.Editor" && IsApprovedEditorSdkReference(reference)),
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
            return string.Equals(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(assembly.Location)), Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(graph.outputPath)),
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
        public void ReferenceAllowlistRejectsRetiredBouncyCastleFilesAndCopies()
        {
            var project = Directory.GetParent(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath)).FullName;
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            foreach (var path in new[]
            {
                Path.Combine(FrameworkPackageRoot(), "Plugins/BouncyCastle/BouncyCastle.Cryptography.dll"),
                Path.Combine(Application.dataPath, "YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll"),
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
                Assert.That(IsAllowedReference(path, contents, ui, true), Is.False, path);
                Assert.That(IsAllowedReference(path, contents, ui, false), Is.False, path);
            }
            var folder = Path.Combine(Path.GetTempPath(), "YLRetiredCrypto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var fake = Path.Combine(folder, "BouncyCastle.Cryptography.dll");
                File.Copy(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location, fake);
                Assert.That(IsAllowedReference(fake, contents, ui, true, typeof(Newtonsoft.Json.JsonConvert).Assembly.Location), Is.False);
                Assert.That(IsAllowedReference(fake, contents, ui, false), Is.False);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void EditorRootReferencesBelongToTheActiveInstallation()
        {
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            var references = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(contents, "Managed")), "UnityEditor*.dll", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path) != "UnityEditor.TestRunner.dll").ToArray();
            Assert.That(references, Is.Not.Empty);
            foreach (var reference in references)
            {
                Assert.That(IsAllowedReference(reference, contents, ui, true), Is.True, reference);
                Assert.That(IsAllowedReference(reference, contents, ui, false), Is.False, reference);
            }

            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.EditorReference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                var fake = Path.Combine(folder, Path.GetFileName(references[0]));
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(references[0]), global::YLFramework.Testing.PackageTestPaths.Physical(fake));
                Assert.That(IsAllowedReference(fake, contents, ui, true), Is.False, fake);
                Assert.That(IsAllowedReference(Path.Combine(contents, "Managed/UnityEditor.TestRunner.dll"), contents, ui, true), Is.False);
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
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
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                foreach (var reference in references)
                {
                    Assert.That(IsAllowedReference(reference, contents, ui, true), Is.True, reference);
                    Assert.That(IsAllowedReference(reference, contents, ui, false), Is.False, reference);
                    var fake = Path.Combine(folder, Path.GetFileName(reference));
                    File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(reference), global::YLFramework.Testing.PackageTestPaths.Physical(fake));
                    Assert.That(IsAllowedReference(fake, contents, ui, true), Is.False, fake);
                }
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
            }
        }

        [Test]
        public void JsonVariantsBelongToTheSameLockedOfficialPackage()
        {
            var package = PackageInfo.GetAllRegisteredPackages().Single(value => value.name == "com.unity.nuget.newtonsoft-json");
            Assert.That(package, Is.Not.Null);
            Assert.That(package.name, Is.EqualTo("com.unity.nuget.newtonsoft-json"));
            var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "../Packages/manifest.json")));
            var locked = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "../Packages/packages-lock.json")));
            Assert.That(package.version, Is.EqualTo((string)locked["dependencies"]?[package.name]?["version"]));
            Assert.That(IsOfficialRegistryIdentity(package, manifest, locked), Is.True);
            Assert.That(package.source, Is.EqualTo(UnityEditor.PackageManager.PackageSource.Registry));
            var contents = EditorApplication.applicationContentsPath;
            var ui = typeof(UnityEngine.UI.Image).Assembly.Location;
            var variants = new[] { Path.Combine(package.resolvedPath, "Runtime/Newtonsoft.Json.dll"),
                Path.Combine(package.resolvedPath, "Runtime/AOT/Newtonsoft.Json.dll") };
            foreach (var reference in variants)
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(reference)), Is.True, reference);
                foreach (var json in variants)
                {
                    Assert.That(IsAllowedReference(reference, contents, ui, true, json), Is.True, reference);
                    Assert.That(IsAllowedReference(reference, contents, ui, false, json), Is.True, reference);
                }
                Assert.That(IsAllowedReference(reference, contents, ui, true), Is.False, "Json must be explicitly requested.");
            }

            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.JsonReference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                var fake = Path.Combine(folder, "Newtonsoft.Json.dll");
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(variants[0]), global::YLFramework.Testing.PackageTestPaths.Physical(fake));
                Assert.That(IsAllowedReference(fake, contents, ui, true, variants[0]), Is.False);
                Assert.That(IsAllowedReference(fake, contents, ui, false, fake), Is.False);
                Assert.That(IsAllowedReference(variants[0], contents, ui, true, fake), Is.False);
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
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
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
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
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(references[0]), global::YLFramework.Testing.PackageTestPaths.Physical(fake));
                Assert.That(IsAllowedReference(fake, contents, ui, true), Is.False, fake);
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
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
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                var fake = Path.Combine(folder, Path.GetFileName(dependency.Location));
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(dependency.Location), global::YLFramework.Testing.PackageTestPaths.Physical(fake));
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
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
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
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                var profiles = CompilationPipeline.GetAssemblies(editor ? AssembliesType.Editor : AssembliesType.PlayerWithoutTestAssemblies);
                string runtimeOutput = null;
                foreach (var name in editor ? new[] { "YLFramework.Runtime", "YLFramework.Editor" } : new[] { "YLFramework.Runtime" })
                {
                    var graph = profiles.Single(a => a.name == name);
                    var sources = VerifySubjectSources(graph, FrameworkPackageRoot());
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
                    bool Allowed(string path) => IsAllowedReference(path,
                            EditorApplication.applicationContentsPath, ui, editor, json);
                    // AssemblyBuilder 的 defaultReferences 在本 Editor 仍返回 netstandard shims；
                    // 使用真实 profile 的 BCL/Unity 输入，避免把 .NET 4.8 的 CodeDom 错当成缺失产品依赖。
                    var inputs = new List<string>(graph.allReferences);
                    var pending = new Stack<Assembly>(graph.assemblyReferences);
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (pending.Count != 0)
                    {
                        var dependency = pending.Pop();
                        if (!visited.Add(dependency.name))
                        {
                            continue;
                        }
                        if (IsApprovedPackageGraph(dependency, editor))
                        {
                            inputs.Add(dependency.outputPath);
                        }
                        foreach (var reference in dependency.assemblyReferences)
                        {
                            pending.Push(reference);
                        }
                    }
                    var trusted = inputs.Select(path => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                        .Where(Allowed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    // excludeReferences 会在合并 additionalReferences 之后过滤，不能排除仍要提供的 BCL。
                    builder.excludeReferences = builder.defaultReferences
                        .Where(p => !trusted.Contains(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(p)), StringComparer.OrdinalIgnoreCase)).ToArray();
                    builder.additionalReferences = trusted.Concat(runtimeOutput == null ? Array.Empty<string>() : new[] { runtimeOutput }).ToArray();
                    var effective = builder.defaultReferences.Except(builder.excludeReferences).Concat(builder.additionalReferences)
                        .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    Assert.That(effective.All(p => Allowed(p) || p == runtimeOutput), Is.True);
                    Assert.That(effective.Any(p => new[] { "ICSharpCode.SharpZipLib.dll", "EPPlus.dll", "MiniExcel.dll" }
                        .Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)), Is.False);
                    Assert.That(effective.Count(p => Path.GetFileName(p) == "UnityEngine.UI.dll"), Is.EqualTo(1));
                    Assert.That(effective.Count(p => Path.GetFileName(p) == "Newtonsoft.Json.dll"), Is.EqualTo(1));
                    foreach (var required in new[] { "Unity.Addressables.dll", "Unity.ResourceManager.dll", "Unity.Profiling.Core.dll" }
                        .Concat(name == "YLFramework.Editor" ? new[] { "Unity.Addressables.Editor.dll", "Unity.ScriptableBuildPipeline.dll", "Unity.ScriptableBuildPipeline.Editor.dll" } : Array.Empty<string>()))
                    {
                        Assert.That(effective.Count(path => Path.GetFileName(path) == required), Is.EqualTo(1), required);
                    }
                    Assert.That(effective.Any(path => Path.GetFileName(path) == "BouncyCastle.Cryptography.dll" ||
                        Path.GetFileName(path).IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetFileName(path).IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);
                    if (!editor)
                    {
                        Assert.That(effective.Any(path => Path.GetFileName(path).StartsWith("UnityEditor", StringComparison.Ordinal) ||
                            Path.GetFileName(path).EndsWith(".Editor.dll", StringComparison.Ordinal)), Is.False);
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
                    Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), Is.True);
                    TestContext.WriteLine("Compiled {0}: {1} sources, {2}; references:\n{3}", name, sources.Length, builder.flags,
                        string.Join("\n", effective));
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
