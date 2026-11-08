using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using YLFramework.Integrations.HybridCLR.Editor;
using Assembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRBoundaryTests
    {
        private const string PackageName = "com.code-philosophy.hybridclr";
        private const string VersionDefine = "YL_HYBRIDCLR_9_0_3";
        private const string ProductRoot = "Packages/com.kano.ylframework.hybridclr/";
        private const string TestRoot = "Assets/Tests/YLFramework/Integrations/HybridCLR/";

        [Test]
        public void ResolvedPackageMatchesTheExactSupportedRelease()
        {
            var package = PackageInfo.GetAllRegisteredPackages().Single(p => p.name == PackageName);
            Assert.That(package.version, Is.EqualTo("9.0.3"));
            Assert.That(package.source, Is.EqualTo(PackageSource.Git));
            var declared = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/manifest.json")))["dependencies"][PackageName].Value<string>();
            var locked = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/packages-lock.json")))["dependencies"][PackageName];
            Assert.DoesNotThrow(() => HybridCLRBuildPipeline.ValidateGitProvenance(package.version, package.source.ToString(), declared,
                locked.Value<string>("source"), locked.Value<string>("version"), locked.Value<string>("hash"), package.git?.revision, package.git?.hash));
        }

        [Test]
        public void OfficialSdkAndOwnedBuildGuardHaveSingleIdentities()
        {
            Assert.That(PackageInfo.FindForAssembly(typeof(global::HybridCLR.Settings.HybridCLRSettings).Assembly).name, Is.EqualTo(PackageName));
            Assert.That(TypeCache.GetTypesDerivedFrom<UnityEditor.Build.IPreprocessBuildWithReport>()
                .Where(type => type.Assembly == typeof(HybridCLRBuildPipeline).Assembly),
                Is.EquivalentTo(new[] { typeof(HybridCLRPlayerBuildGuard) }));
        }

        [Test]
        public void PreparedResourceInputsMatchTheCurrentPlayModeAssembly()
        {
            var path = HybridCLRTestFixtures.Root + "/Good.json";
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.True,
                "The host must prepare current Resource inputs before running tests, not rely on an earlier session test.");
            var manifest = global::YLFramework.Integrations.HybridCLR.HybridCLRManifest.Parse(
                File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == "YLFramework.HybridCLR.Tests.PlayMode");
            var bytes = File.ReadAllBytes(assembly.Location);
            var payload = manifest.HotUpdateAssemblies.Single();
            Assert.That(payload.Identity, Is.EqualTo(assembly.GetName().FullName));
            Assert.That(payload.Mvid, Is.EqualTo(assembly.ManifestModule.ModuleVersionId.ToString("D")));
            Assert.That(payload.Sha256, Is.EqualTo(global::YLFramework.Integrations.HybridCLR.HybridCLRManifest.Hash(bytes)));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(HybridCLRTestFixtures.Root + "/Entry.bytes")), Is.EqualTo(bytes));
        }

        [TestCase(AssembliesType.Editor, "YLFramework.Runtime")]
        [TestCase(AssembliesType.Editor, "YLFramework.Editor")]
        [TestCase(AssembliesType.Player, "YLFramework.Runtime")]
        public void SubjectCompilationClosureUsesOnlyApprovedNativeDependencies(AssembliesType profile, string name)
        {
            Assert.That(EditorUtility.scriptCompilationFailed, Is.False);
            var root = CompilationPipeline.GetAssemblies(profile).Single(a => a.name == name);
            var pending = new Stack<Assembly>(new[] { root });
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                var assembly = pending.Pop();
                if (!visited.Add(assembly.name))
                {
                    continue;
                }

                Assert.That(IsApprovedSubjectDependency(assembly.name, profile == AssembliesType.Editor), Is.True, assembly.name);
                foreach (var reference in assembly.assemblyReferences)
                {
                    pending.Push(reference);
                }
            }
            foreach (var reference in root.compiledAssemblyReferences)
            {
                Assert.That(IsApprovedSubjectDependency(Path.GetFileNameWithoutExtension(reference), profile == AssembliesType.Editor) ||
                    (profile == AssembliesType.Editor && IsApprovedEditorPlatformReference(reference)), Is.True, reference);
            }
        }

        [TestCase(ProductRoot + "Runtime/YLFramework.HybridCLR.asmdef", false, false)]
        [TestCase(ProductRoot + "Editor/YLFramework.HybridCLR.Editor.asmdef", true, false)]
        [TestCase(TestRoot + "EditMode/YLFramework.HybridCLR.Tests.EditMode.asmdef", true, true)]
        [TestCase(TestRoot + "PlayMode/YLFramework.HybridCLR.Tests.PlayMode.asmdef", false, true)]
        public void OptionalAssembliesAreVersionConstrainedLeaves(string path, bool editorOnly, bool test)
        {
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.True, path);
            var definition = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.That(definition.Value<bool>("autoReferenced"), Is.False);
            Assert.That(definition.Value<bool>("overrideReferences"), Is.True);
            var constraints = definition["defineConstraints"].Values<string>().ToArray();
            var expectedConstraints = new List<string> { VersionDefine };
            if (test)
            {
                expectedConstraints.AddRange(new[] { "UNITY_INCLUDE_TESTS", "YL_HYBRIDCLR_PACKAGE" });
                if (editorOnly)
                {
                    expectedConstraints.Add("YL_TEST_HYBRIDCLR_SAMPLES");
                }
            }
            CollectionAssert.AreEquivalent(expectedConstraints, constraints);
            var versions = definition["versionDefines"].ToArray();
            Assert.That(versions.Length, Is.EqualTo(test ? 2 : 1));
            var version = versions.Single(value => value.Value<string>("name") == PackageName);
            Assert.That(version.Value<string>("name"), Is.EqualTo(PackageName));
            Assert.That(version.Value<string>("expression"), Is.EqualTo("[9.0.3,9.0.4)"));
            Assert.That(version.Value<string>("define"), Is.EqualTo(VersionDefine));
            if (test)
            {
                var product = versions.Single(value => value.Value<string>("name") == "com.kano.ylframework.hybridclr");
                Assert.That(product.Value<string>("expression"), Is.EqualTo("0.1.0-preview.1"));
                Assert.That(product.Value<string>("define"), Is.EqualTo("YL_HYBRIDCLR_PACKAGE"));
            }
            CollectionAssert.AreEqual(editorOnly ? new[] { "Editor" } : Array.Empty<string>(), definition["includePlatforms"].Values<string>());
            var expectedReferences = test
                ? (editorOnly
                    ? new[] { "YLFramework.Runtime", "YLFramework.Editor", "YLFramework.HybridCLR", "YLFramework.HybridCLR.Editor",
                        "YLFramework.HybridCLR.Sample.Editor", "HybridCLR.Editor", "UnityEngine.TestRunner", "UnityEditor.TestRunner", "YLFramework.Tests.Fixtures" }
                    : new[] { "YLFramework.Runtime", "YLFramework.HybridCLR", "UnityEngine.TestRunner", "YLFramework.Tests.Fixtures" })
                : (editorOnly
                    ? new[] { "YLFramework.Runtime", "YLFramework.Editor", "YLFramework.HybridCLR", "HybridCLR.Runtime", "HybridCLR.Editor" }
                    : new[] { "YLFramework.Runtime", "HybridCLR.Runtime" });
            Assert.That(definition["references"].Values<string>(), Is.EquivalentTo(expectedReferences));
            var expectedPrecompiled = new[] { "Newtonsoft.Json.dll" }
                .Concat(test ? new[] { "nunit.framework.dll" } : Array.Empty<string>())
                .Concat(editorOnly ? new[] { "dnlib.dll" } : Array.Empty<string>());
            Assert.That(definition["precompiledReferences"].Values<string>(), Is.EquivalentTo(expectedPrecompiled));

            if (!editorOnly)
            {
                foreach (var reference in definition["references"].Values<string>())
                {
                    Assert.That(reference.EndsWith(".Editor", StringComparison.Ordinal), Is.False, reference);
                }

                Assert.That(definition["precompiledReferences"].Values<string>(), Does.Not.Contain("dnlib.dll"));
            }
        }

        private static bool IsApprovedSubjectDependency(string name, bool editor) =>
            new[] { "YLFramework.Runtime", "mscorlib", "netstandard", "System", "UnityEngine", "Newtonsoft.Json",
                "BouncyCastle.Cryptography", "nunit.framework", "Microsoft.CSharp", "Microsoft.Win32.Primitives" }.Contains(name) ||
            name.StartsWith("System.", StringComparison.Ordinal) ||
            (editor && (name == "YLFramework.Editor" || name == "UnityEditor" || name.StartsWith("UnityEditor.", StringComparison.Ordinal))) ||
            name.StartsWith("UnityEngine.", StringComparison.Ordinal);

        private static bool IsApprovedEditorPlatformReference(string path)
        {
            var name = Path.GetFileName(path);
            var root = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines")))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return name.StartsWith("Unity.", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal) &&
                Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path)).StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path));
        }
    }
}
