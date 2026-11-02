using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.ResourceBuild;

namespace YLFramework.Tests.ResourceEditor
{
    [Category("YL.UPM.Generators")]
    public class ResourceCodeGenerationTests
    {
        private const string OutputKey = "YL.ResourceCodeGenerationTests.Output";
        private static string _output => SessionState.GetString(OutputKey, "");
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
            yield return new RecompileScripts(false);
            yield return ResourceGeneratorTestWait.Clear("Remove resource generator regression output");
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta")), Is.False);
            SessionState.EraseString(OutputKey);
            SessionState.EraseString(OutputKey + ".Owner");
            SessionState.EraseString(OutputKey + ".Requested");
        }

        internal static ResourceCatalogDocument Catalog(params string[] addresses)
        {
            var data = ResourceCatalogCodecTests.Payload();
            var original = data["Assets"][0];
            data["Assets"] = new JArray(addresses.Select((address, index) =>
            {
                var asset = original.DeepClone();
                var guid = (index + 1).ToString("x32", CultureInfo.InvariantCulture);
                asset["Guid"] = guid;
                asset["AssetId"] = guid + "_11400000";
                asset["Address"] = address;
                asset["LoadName"] = "assets/owned/file" + index + ".txt";
                return asset;
            }));
            data["Groups"][0]["Members"] = new JArray(((JArray)data["Assets"]).Select(x => new JObject { ["AssetId"] = (string)x["AssetId"], ["Required"] = true }));
            return Read(data);
        }
        internal static ResourceCatalogDocument Read(JObject data)
        {
            var bytes = ResourceCatalogCodec.Seal(Encoding.UTF8.GetBytes(data.ToString(Formatting.None)));
            return ResourceCatalogCodec.Read(bytes, (string)data["ProjectId"], (string)data["Platform"], (string)data["UnityCompatibility"], (string)data["AppContentCompatibilityId"]);
        }
        private static string Generate(ResourceCatalogDocument[] documents, string space = "YLFramework.Resources") =>
            (string)ResourceEditorTests.Invoke(typeof(ResourceCodeGenerator), "GenerateCatalogs", documents, space);
        private bool Publish(string source) => (bool)ResourceEditorTests.Invoke(typeof(ResourceCodeGenerator).Assembly.GetType(
            "YLFramework.Editor.Resource.ResourceReferenceOutput"), "Publish", _output, source);

        [UnityTest]
        public IEnumerator ExplicitNestedOutputCreatesMissingParentsWithoutRedirectingTheRequestedDirectory()
        {
            yield return ResourceGeneratorTestWait.Clear("Generate explicit nested resource references");
            string requested = _output + "/Missing/References";
            var catalogs = new[] { Catalog("explicit.key") };
            Assert.That(ResourceCodeGenerator.GenerateReferences(catalogs, requested), Is.True);
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
            Assert.That(ResourceCodeGenerator.GenerateReferences(new[] { Catalog("explicit.key") }, requested), Is.False);
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
                Assert.Throws<InvalidOperationException>(() => ResourceCodeGenerator.GenerateReferences(new[] { Catalog("key") }, _output + "/Missing/References"));
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
        public void FormalAddressGroupAndProjectKeysKeepOriginalValuesAndNoBundleNames()
        {
            var source = Generate(new[] { Catalog("hero.icon", "ui/9-item", "class") });
            StringAssert.Contains("public static class Catalog", source);
            StringAssert.Contains("public static class Addresses", source);
            StringAssert.Contains("public static class Groups", source);
            StringAssert.Contains("\"hero.icon\"", source);
            StringAssert.Contains("\"ui/9-item\"", source);
            StringAssert.Contains("\"class\"", source);
            StringAssert.Contains("\"main\"", source);
            StringAssert.DoesNotContain("BundleName", source);
            StringAssert.DoesNotContain("ylr_", source);
        }

        [Test]
        public void SubAssetKeysAndAliasesRemainExplicitAndCanonical()
        {
            var data = JObject.Parse(Encoding.UTF8.GetString(ResourceCatalogCodec.Write(Catalog("Sprites.png"))));
            data.Remove("BuildId");
            data["Assets"][0]["AssetType"] = "UnityEngine.Texture2D";
            data["Assets"][0]["Aliases"] = new JArray("sprite-alias");
            var sub = data["Assets"][0].DeepClone();
            sub["LocalId"] = 21300000;
            sub["AssetId"] = (string)sub["Guid"] + "_21300000";
            sub["AssetType"] = "UnityEngine.Sprite";
            sub["Kind"] = "SubAsset";
            sub["SubAssetName"] = "Left";
            sub["SubAssetKey"] = "21300000";
            ((JArray)data["Assets"]).Add(sub);
            var source = Generate(new[] { Read(data) });
            StringAssert.Contains("public static class SubAssets", source);
            StringAssert.Contains("public static class SpritesPngLeft", source);
            StringAssert.Contains("Key = \"21300000\"", source);
            StringAssert.Contains("Address = \"Sprites.png\"", source);
            StringAssert.Contains("SpriteAlias = \"sprite-alias\"", source);
        }

        [Test]
        public void CollisionsAndInvalidNamespaceFailBeforeWriterOrOutputChanges()
        {
            Publish(Generate(new[] { Catalog("valid.txt") }));
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLResourceReferences.cs"));
            foreach (var documents in new[] { new[] { Catalog("a-b", "a_b") }, new[] { Catalog("addresses") }, Array.Empty<ResourceCatalogDocument>(), new ResourceCatalogDocument[] { null } })
            {
                Assert.Throws<InvalidOperationException>(() => Publish(Generate(documents)));
            }

            Assert.Throws<ArgumentException>(() => Generate(new[] { Catalog("valid") }, "bad-namespace"));
            Assert.Throws<ArgumentNullException>(() => Generate(null));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLResourceReferences.cs")));
        }

        [Test]
        public void ExistingTypesNamespacesAndDuplicateProjectsCannotBeShadowed()
        {
            Assert.Throws<InvalidOperationException>(() => Generate(new[] { Catalog("key") }, "YLFramework.Tests.ResourceEditor"));
            Assert.Throws<InvalidOperationException>(() => Generate(new[] { Catalog("key"), Catalog("other") }));
        }

        [Test]
        public void VerbatimNamespaceCannotBypassCompiledTypeCollisionOrChangeOwnedOutput()
        {
            Publish(Generate(new[] { Catalog("Key") }));
            var path = _output + "/YLResourceReferences.cs";
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var time = File.GetLastWriteTimeUtc(path);
            Assert.Throws<ArgumentException>(() => Publish(Generate(new[] { Catalog("Key") }, "@YLFramework.Tests.ResourceEditor")));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(time));
        }

        [Test]
        public void SortingAndCultureKeepGeneratedBytesStable()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                var first = Generate(new[] { Catalog("item.icon", "9-item", "class", "quote\"key") });
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                var second = Generate(new[] { Catalog("quote\"key", "class", "9-item", "item.icon") });
                Assert.That(second, Is.EqualTo(first));
                StringAssert.Contains("_9Item", first);
                StringAssert.Contains("QuoteKey", first);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Test]
        public void IdentifiersUseInvariantPascalCaseWhileAddressesAndOwnedLegacyOutputStayIntact()
        {
            var catalog = Catalog("HTTP_URL", "ui/data-file", "part_9_name", "Café_map", "alreadyPascal", "XML_document");
            var source = Generate(new[] { catalog });
            foreach (var expected in new[] { "HttpUrl", "UIDataFile", "Part9Name", "CaféMap", "AlreadyPascal", "XmlDocument" })
            {
                StringAssert.Contains("public const string " + expected + " = ", source);
            }
            foreach (var address in catalog.Assets.Select(asset => asset.Address))
            {
                StringAssert.Contains("\"" + address + "\"", source);
            }
            var modern = Generate(new[] { Catalog("hero.icon") });
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
            var first = Generate(new[] { Catalog("First") });
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
                Assert.Throws<IOException>(() => Publish(Generate(new[] { Catalog("Second") })));
            }

            CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_output)).Count(x => !x.EndsWith(".meta")), Is.EqualTo(2));
            Publish(Generate(new[] { Catalog("Second") }));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo("source identity sentinel"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef + ".meta")), Is.EqualTo("assembly identity sentinel"));
        }

        [Test]
        public void UnknownEditedIncompleteAndLegacyOutputsCannotBeTakenOver()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_output));
            var user = _output + "/User.cs";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(user), "// user source");
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { Catalog("Key") })));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(user)), Is.EqualTo("// user source"));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(user));
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { Catalog("Key") })), "Even empty unknown directories must not be adopted.");
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_output));
            Publish(Generate(new[] { Catalog("Key") }));
            var path = _output + "/YLResourceReferences.cs";
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "// handwritten change");
            var old = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { Catalog("Other") })));
            CollectionAssert.AreEqual(old, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
        }

        [Test]
        public void OrphanMetaAndUnknownAssemblyAreRejectedWithoutWrites()
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta"), "unknown identity");
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { Catalog("Key") })));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output)), Is.False);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta")), Is.EqualTo("unknown identity"));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_output + ".meta"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_output));
            var asmdef = _output + "/YLFramework.Generated.ResourceNames.asmdef";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef), "{\"name\":\"UserAssembly\"}");
            Assert.Throws<InvalidOperationException>(() => Publish(Generate(new[] { Catalog("Key") })));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(asmdef)), Is.EqualTo("{\"name\":\"UserAssembly\"}"));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_output + "/YLResourceReferences.cs")), Is.False);
        }
    }
    internal class Catalog
    {
    }
}
