using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;

namespace YLFramework.Tests.ResourceEditor
{
    [Category("YL.UPM.Generators")]
    public class ResourceCodeGenerationTests
    {
        private const string OutputKey = "YL.ResourceCodeGenerationTests.Output";
        private static string _output => SessionState.GetString(OutputKey, "");
        private static string Inputs => SessionState.GetString(OutputKey + ".Inputs", "");
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Start resource generator regression");
            Assert.That(_output, Is.Empty, "Do not take over an interrupted output reservation.");
            string output = "Assets/Names82c-" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(output) || File.Exists(output) || File.Exists(output + ".meta"), Is.False);
            SessionState.SetString(OutputKey, output);
            SessionState.SetString(OutputKey + ".Owner", TestContext.CurrentContext.Test.FullName);
            var inputs = ResourceAddressablesGeneratorFixture.InputPrefix + Guid.NewGuid().ToString("N");
            SessionState.SetString(OutputKey + ".Inputs", inputs);
            ResourceAddressablesGeneratorFixture.CreateInputs(inputs);
            SessionState.SetString(OutputKey + ".InputsHash", ResourceAddressablesGeneratorFixture.Fingerprint(inputs));
            yield return ResourceGeneratorTestWait.Clear("Import real generator input assets");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (string.IsNullOrEmpty(_output) || SessionState.GetString(OutputKey + ".Owner", "") != TestContext.CurrentContext.Test.FullName)
            {
                yield break;
            }
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_output), true);
            }

            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta")))
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta"));
            }
            ResourceAddressablesGeneratorFixture.DeleteInputs(Inputs, SessionState.GetString(OutputKey + ".InputsHash", ""));
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Remove resource generator regression output");
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta")), Is.False);
            SessionState.EraseString(OutputKey);
            SessionState.EraseString(OutputKey + ".Owner");
            SessionState.EraseString(OutputKey + ".Requested");
            SessionState.EraseString(OutputKey + ".Inputs");
            SessionState.EraseString(OutputKey + ".InputsHash");
        }

        private static string Generate(string[] addresses, string space = "YLFramework.Resources", string[] labels = null)
        {
            using var fixture = new ResourceAddressablesGeneratorFixture(Inputs, addresses, labels);
            return ResourceCodeGenerator.GenerateSource(fixture.Settings, space);
        }
        private static bool GenerateReferences(string[] addresses, string directory)
        {
            using var fixture = new ResourceAddressablesGeneratorFixture(Inputs, addresses);
            return ResourceCodeGenerator.GenerateReferences(fixture.Settings, directory);
        }
        private bool Publish(string source) => ResourceAddressablesGeneratorFixture.Publish(_output, source);

        [UnityTest]
        public IEnumerator ExplicitNestedOutputCreatesMissingParentsWithoutRedirectingTheRequestedDirectory()
        {
            yield return ResourceGeneratorTestWait.Clear("Generate explicit nested resource references");
            string requested = _output + "/Missing/References";
            Assert.That(GenerateReferences(new[] { "explicit.key" }, requested), Is.True);
            SessionState.SetString(OutputKey + ".Requested", requested);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(requested + "/YLResourceReferences.cs")), Is.True, requested);
            yield return new RecompileScripts();
            yield return ResourceGeneratorTestWait.Clear("Import explicit nested resource references");
            AssertExplicitNestedOutput();
        }

        private static void AssertExplicitNestedOutput()
        {
            string requested = SessionState.GetString(OutputKey + ".Requested", "");
            Assert.That(requested, Is.Not.Empty, "The authored request must survive the compilation boundary.");
            Assert.That(requested, Is.EqualTo(_output + "/Missing/References"), "A reload must not replace the output reservation.");
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(requested + "/YLResourceReferences.cs")), Is.True, requested);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(requested + "/YLFramework.Generated.ResourceNames.asmdef")), Is.True);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLResourceReferences.cs")), Is.False);
            Assert.That(GenerateReferences(new[] { "explicit.key" }, requested), Is.False);
        }

        [TestCase("file")]
        [TestCase("orphanMeta")]
        public void NestedOutputDoesNotAdoptBlockedAncestorIdentity(string kind)
        {
            string sentinel = _output + (kind == "orphanMeta" ? ".meta" : "");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(sentinel), "Existing ancestor identity.");
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sentinel));
            var time = File.GetLastWriteTimeUtc(sentinel);
            try
            {
                Assert.Throws<InvalidOperationException>(() => GenerateReferences(new[] { "key" }, _output + "/Missing/References"));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sentinel)));
                Assert.That(File.GetLastWriteTimeUtc(sentinel), Is.EqualTo(time));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output)), Is.False);
            }
            finally
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(sentinel));
            }
        }

        [Test]
        public void OfficialAddressAndLabelKeysKeepOriginalValuesWithoutLegacyRuntimeCollections()
        {
            var source = Generate(new[] { "hero.icon", "ui/9-item", "class" }, labels: new[] { "main" });
            StringAssert.Contains("public static class ResourceNames", source);
            StringAssert.Contains("public static class Addresses", source);
            StringAssert.Contains("public static class Labels", source);
            StringAssert.Contains("\"hero.icon\"", source);
            StringAssert.Contains("\"ui/9-item\"", source);
            StringAssert.Contains("\"class\"", source);
            StringAssert.Contains("\"main\"", source);
            StringAssert.DoesNotContain("BundleName", source);
            StringAssert.DoesNotContain("ylr_", source);
            foreach (var retired in new[] { "class Groups", "class SubAssets", "class Aliases", "ProjectId", "ResourceAddress", "AssetId" })
            {
                StringAssert.DoesNotContain(retired, source);
            }
        }

        [Test]
        public void OfficialLabelsAreIndependentOfGroupNamesAndPreserveTheirExactLiterals()
        {
            using var fixture = new ResourceAddressablesGeneratorFixture(Inputs, new[] { "sprites/main" }, new[] { "sprite-label", "quote\"label" });
            var entry = fixture.Settings.DefaultGroup.entries.Single();
            Assert.That(entry.labels, Does.Contain("sprite-label"));
            var source = ResourceCodeGenerator.GenerateSource(fixture.Settings);
            StringAssert.Contains("SpriteLabel = \"sprite-label\"", source);
            StringAssert.Contains("QuoteLabel = \"quote\\\"label\"", source);
            StringAssert.DoesNotContain("GeneratorInputs", source);
            StringAssert.DoesNotContain("class SubAssets", source);
            StringAssert.DoesNotContain("class Aliases", source);
        }

        [Test]
        public void CollisionsAndInvalidNamespaceFailBeforeWriterOrOutputChanges()
        {
            Publish(Generate(new[] { "valid.txt" }));
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLResourceReferences.cs"));
            using var emptyFixture = new ResourceAddressablesGeneratorFixture(Inputs, Array.Empty<string>());
            foreach (var label in emptyFixture.Settings.GetLabels().ToArray())
            {
                emptyFixture.Settings.RemoveLabel(label, false);
            }
            Assert.That(emptyFixture.Settings.GetLabels(), Is.Empty);
            var empty = ResourceCodeGenerator.GenerateSource(emptyFixture.Settings);
            StringAssert.Contains("class Addresses", empty);
            StringAssert.Contains("class Labels", empty);
            StringAssert.DoesNotContain("public const string ", empty);
            foreach (var addresses in new[] { new[] { "a-b", "a_b" }, new[] { "addresses" }, new[] { "same", "same" }, new[] { " " } })
            {
                Assert.Throws<InvalidOperationException>(() => Publish(Generate(addresses)));
            }
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "valid" }, labels: new[] { "a-b", "a_b" })));
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "valid" }, labels: new[] { "labels" })));
            Assert.Throws<ArgumentException>(() => Generate(new[] { "valid" }, "bad-namespace"));
            Assert.Throws<ArgumentNullException>(() => ResourceCodeGenerator.GenerateSource(null));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLResourceReferences.cs")));
        }

        [Test]
        public void ExistingGeneratedTypeAndDuplicateOfficialAddressesCannotBeShadowed()
        {
            Assert.Throws<InvalidOperationException>(() => Generate(new[] { "key" }, "YLFramework.Tests.ResourceEditor"));
            Assert.Throws<InvalidOperationException>(() => Generate(new[] { "key", "key" }));
        }

        [Test]
        public void VerbatimNamespaceCannotBypassCompiledTypeCollisionOrChangeOwnedOutput()
        {
            Publish(Generate(new[] { "Key" }));
            var path = _output + "/YLResourceReferences.cs";
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var time = File.GetLastWriteTimeUtc(path);
            Assert.Throws<ArgumentException>(() => Publish(Generate(new[] { "Key" }, "@YLFramework.Tests.ResourceEditor")));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(time));
        }

        [Test]
        public void SortingAndCultureKeepGeneratedBytesStable()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                var addresses = new[] { "item.icon", "9-item", "class", "quote\"key", "firstA", "firsta" };
                var labels = new[] { "labelA", "labela" };
                var first = Generate(addresses, labels: labels);
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                var second = Generate(addresses.Reverse().ToArray(), labels: labels.Reverse().ToArray());
                Assert.That(second, Is.EqualTo(first));
                StringAssert.Contains("_9Item", first);
                StringAssert.Contains("QuoteKey", first);
                StringAssert.Contains("QuoteKey = \"quote\\\"key\"", first);
                foreach (var exact in new[] { "firstA", "firsta", "labelA", "labela" })
                    StringAssert.Contains("\"" + exact + "\"", first);
                Assert.That(first.IndexOf("\"firstA\"", StringComparison.Ordinal), Is.LessThan(first.IndexOf("\"firsta\"", StringComparison.Ordinal)));
                Assert.That(first.IndexOf("\"labelA\"", StringComparison.Ordinal), Is.LessThan(first.IndexOf("\"labela\"", StringComparison.Ordinal)));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void CurrentIdentifiersKeepAddressLiteralsAndRegenerateOnlyOwnedSource()
        {
            var addresses = new[] { "HTTP_URL", "ui/data-file", "part_9_name", "Café_map", "alreadyPascal", "XML_document" };
            var source = Generate(addresses);
            foreach (var expected in new[] { "HTTPURL", "UiDataFile", "Part9Name", "CaféMap", "AlreadyPascal", "XMLDocument" })
            {
                StringAssert.Contains("public const string " + expected + " = ", source);
            }
            foreach (var address in addresses)
            {
                StringAssert.Contains("\"" + address + "\"", source);
            }
            var modern = Generate(new[] { "hero.icon" });
            StringAssert.Contains("HeroIcon = \"hero.icon\"", modern);
            var legacy = modern.Replace("HeroIcon =", "Hero_icon =");
            Assert.That(Publish(legacy), Is.True);
            var path = _output + "/YLResourceReferences.cs";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"), "legacy source identity");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLFramework.Generated.ResourceNames.asmdef.meta"), "legacy assembly identity");
            Assert.That(Publish(modern), Is.True, "A valid old generated body remains owned and can be regenerated.");
            Assert.That(Publish(modern), Is.False, "Second generation must be byte-stable.");
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo("legacy source identity"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLFramework.Generated.ResourceNames.asmdef.meta")), Is.EqualTo("legacy assembly identity"));
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "// handwritten change");
            var changed = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            Assert.Throws<InvalidOperationException>(() => Publish(legacy));
            CollectionAssert.AreEqual(changed, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
        }

        [Test]
        public void AtomicUpdatesPreserveMetaNoOpTimestampAndOldSourceOnRealIoFailure()
        {
            var first = Generate(new[] { "First" });
            Assert.That(Publish(first), Is.True);
            var path = _output + "/YLResourceReferences.cs";
            var asmdef = _output + "/YLFramework.Generated.ResourceNames.asmdef";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"), "source identity sentinel");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef + ".meta"), "assembly identity sentinel");
            var time = File.GetLastWriteTimeUtc(path);
            var old = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            Assert.That(Publish(first), Is.False);
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(time));
            using (var locked = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => Publish(Generate(new[] { "Second" })));
            }

            CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_output)).Count(x => !x.EndsWith(".meta")), Is.EqualTo(2));
            Publish(Generate(new[] { "Second" }));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo("source identity sentinel"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef + ".meta")), Is.EqualTo("assembly identity sentinel"));
        }

        [Test]
        public void UnknownEditedIncompleteAndLegacyOutputsCannotBeTakenOver()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_output));
            var user = _output + "/User.cs";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(user), "// user source");
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "Key" })));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(user)), Is.EqualTo("// user source"));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(user));
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "Key" })), "Even empty unknown directories must not be adopted.");
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_output));
            Publish(Generate(new[] { "Key" }));
            var path = _output + "/YLResourceReferences.cs";
            var assembly = _output + "/YLFramework.Generated.ResourceNames.asmdef";
            var assemblyBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly));
            var sourceBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(assembly));
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "Other" })), "An incomplete owned pair must not be repaired by taking over its surviving source.");
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(assembly)), Is.False);
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(assembly), assemblyBytes);
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "// handwritten change");
            var old = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "Other" })));
            CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
        }

        [Test]
        public void OrphanMetaAndUnknownAssemblyAreRejectedWithoutWrites()
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta"), "unknown identity");
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "Key" })));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output)), Is.False);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta")), Is.EqualTo("unknown identity"));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_output));
            var asmdef = _output + "/YLFramework.Generated.ResourceNames.asmdef";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef), "{\"name\":\"UserAssembly\"}");
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { "Key" })));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef)), Is.EqualTo("{\"name\":\"UserAssembly\"}"));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLResourceReferences.cs")), Is.False);
        }
    }
    internal class ResourceNames
    {
    }
}
