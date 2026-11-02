using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using NUnit.Framework;
using UnityEditor;
using YLFramework.Integrations.HybridCLR.Editor;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRAssemblyAnalysisTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "kilo", "YLHybridCLRAnalysis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_directory));
        }

        [TearDown]
        public void TearDown() => Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_directory), true);

        private string WriteAssembly(string file, string name, params string[] dependencies)
        {
            var path = Path.Combine(_directory, file + ".dll");
            using var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll };
            var assembly = new AssemblyDefUser(name, new Version(1, 0, 0, 0));
            assembly.Modules.Add(module);
            foreach (var dependency in dependencies)
            {
                module.Types.Add(new TypeDefUser("Fixture", "Uses" + module.Types.Count,
                    new TypeRefUser(module, "Fixture", "Dependency", new AssemblyRefUser(dependency, new Version(1, 0, 0, 0)))));
            }

            module.Write(path);
            return path;
        }

        [Test]
        public void ReadsMetadataIdentityRatherThanGuessingFromTheFileName()
        {
            var record = HybridCLRAssemblyAnalysis.Read(WriteAssembly("NotTheAssemblyName", "Hot.Real"));
            Assert.That(record.Name, Is.EqualTo("Hot.Real"));
            Assert.That(record.Identity, Is.EqualTo("Hot.Real, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
            Assert.That(record.Length, Is.GreaterThan(0));
            Assert.That(record.Sha256, Has.Length.EqualTo(64));
            Assert.That(Guid.TryParse(record.Mvid, out _), Is.True);
        }

        [Test]
        public void RealDllReferencesProduceAStableDependencyFirstOrder()
        {
            var first = WriteAssembly("First", "Hot.First");
            var second = WriteAssembly("Second", "Hot.Second", "Hot.First");
            var entry = WriteAssembly("Entry", "Hot.Entry", "Hot.Second", "Hot.First");
            var paths = new[] { entry, second, first };
            var result = HybridCLRAssemblyAnalysis.Analyze(paths, Array.Empty<string>());
            CollectionAssert.AreEqual(new[] { "Hot.First", "Hot.Second", "Hot.Entry" }, result.HotUpdate.Select(r => r.Name));
            CollectionAssert.AreEqual(result.HotUpdate.Select(r => r.Sha256),
                HybridCLRAssemblyAnalysis.Analyze(paths.Reverse(), Array.Empty<string>()).HotUpdate.Select(r => r.Sha256));
        }

        [TestCase("missing")]
        [TestCase("cycle")]
        [TestCase("duplicate")]
        [TestCase("case")]
        [TestCase("editor")]
        [TestCase("reverse")]
        public void InvalidRealAssemblyGraphsAreRejected(string invalid)
        {
            string[] hot;
            var aot = Array.Empty<string>();
            switch (invalid)
            {
                case "missing":
                    hot = new[] { WriteAssembly("Entry", "Hot.Entry", "Absent") };
                    break;
                case "cycle":
                    hot = new[] { WriteAssembly("A", "Hot.A", "Hot.B"), WriteAssembly("B", "Hot.B", "Hot.A") };
                    break;
                case "duplicate":
                    hot = new[] { WriteAssembly("A", "Hot.Entry"), WriteAssembly("B", "Hot.Entry") };
                    break;
                case "case":
                    hot = new[] { WriteAssembly("A", "Hot.Entry"), WriteAssembly("B", "hot.entry") };
                    break;
                case "editor":
                    hot = new[] { WriteAssembly("Entry", "Hot.Entry", "UnityEditor") };
                    aot = new[] { WriteAssembly("Editor", "UnityEditor") };
                    break;
                default:
                    hot = new[] { WriteAssembly("Entry", "Hot.Entry") };
                    aot = new[] { WriteAssembly("Aot", "Base", "Hot.Entry") };
                    break;
            }
            Assert.Throws<InvalidDataException>(() => HybridCLRAssemblyAnalysis.Analyze(hot, aot));
        }

        [Test]
        public void VerifiedUnityFrameworkReferenceIdentityCanBindOnlyItsInstalledNativeAotImplementation()
        {
            var data = EditorApplication.applicationContentsPath;
            var referencePath = data + "/UnityReferenceAssemblies/unity-4.8-api/System.dll";
            var implementation = data + "/MonoBleedingEdge/lib/mono/unityaot-win32/System.dll";
            var referenceIdentity = HybridCLRAssemblyAnalysis.Read(referencePath).Identity;
            var implementationIdentity = HybridCLRAssemblyAnalysis.Read(implementation).Identity;
            Assert.That(referenceIdentity, Is.Not.EqualTo(implementationIdentity), "The regression covers the real Unity reference/native public-key retargeting, not arbitrary version substitution.");
            var path = _directory + "/FrameworkUser.dll";
            using (var module = new ModuleDefUser("FrameworkUser.dll") { Kind = ModuleKind.Dll })
            {
                new AssemblyDefUser("Hot.FrameworkUser", new Version(1, 0, 0, 0)).Modules.Add(module);
                var reference = new AssemblyRefUser("System", new Version(4, 0, 0, 0))
                {
                    PublicKeyOrToken = new PublicKeyToken(new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 }),
                    HasPublicKey = false
                };
                module.Types.Add(new TypeDefUser("Fixture", "UsesFramework", new TypeRefUser(module, "System", "Uri", reference)));
                module.Write(path);
            }
            Assert.That(HybridCLRAssemblyAnalysis.Read(path).References.Single(), Is.EqualTo(referenceIdentity));
            Assert.Throws<InvalidDataException>(() => HybridCLRAssemblyAnalysis.Analyze(new[] { path }, new[] { implementation }));
            var aliases = HybridCLRBuildPipeline.UnityAotAliases();
            Assert.That(aliases.TryGetValue(referenceIdentity, out var bound), Is.True);
            Assert.That(bound, Is.EqualTo(implementationIdentity));
            Assert.DoesNotThrow(() => HybridCLRAssemblyAnalysis.Analyze(new[] { path }, new[] { implementation }, aliases));
        }

        [Test]
        public void OfficialAnalyzerCollectionIncludesReferencedAotEvenWithoutGenericCalls()
        {
            var last = WriteAssembly("Last", "Base.Last");
            var first = WriteAssembly("First", "Base.First", "Base.Last");
            var entry = WriteAssembly("Entry", "Hot.Entry", "Base.First");
            var unrelated = WriteAssembly("Unrelated", "Base.Unused");
            var analyzed = HybridCLRAssemblyAnalysis.Analyze(new[] { entry }, new[] { unrelated, first, last });
            var required = HybridCLRAssemblyAnalysis.CollectRequiredAotMetadata(analyzed);
            CollectionAssert.AreEqual(new[] { "Base.First", "Base.Last" }, required.Select(r => r.Name));
        }

        [Test]
        public void CorruptDllBytesAreRejectedByTheRealMetadataReader()
        {
            var path = Path.Combine(_directory, "broken.dll");
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), new byte[] { 1, 2, 3, 4 });
            Assert.Throws<InvalidDataException>(() => HybridCLRAssemblyAnalysis.Read(path));
        }
    }
}
