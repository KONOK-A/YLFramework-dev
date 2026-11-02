using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YLFramework.Integrations.HybridCLR;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRManifestTests
    {
        private static HybridCLRPayload Payload(string name) => new HybridCLRPayload
        {
            Name = name,
            Identity = name + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            Mvid = "ad0f4606-e521-4d86-a7fe-32dd7c840132",
            Address = "hybridclr/assemblies/" + name + ".dll.bytes",
            Length = 3,
            Sha256 = "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81"
        };

        private static HybridCLRManifest Manifest() => new HybridCLRManifest
        {
            ProjectId = "hotupdate-test",
            UnityVersion = "2022.3.62f3c1",
            AppContentCompatibilityId = "base-v1",
            HotUpdateAssemblies = new[] { Payload("Hot.Entry") }
        };

        [Test]
        public void ExplicitNeutralCultureHasOneCanonicalIdentityWhenIl2CppRepresentsCultureAsNull()
        {
            const string Expected = "Hot.Entry, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
            var identity = new System.Reflection.AssemblyName(Expected) { CultureInfo = null };
            Assert.That(identity.FullName, Is.Not.EqualTo(Expected), "The actual CLR object must reproduce the native omitted-culture representation.");
            Assert.That(HybridCLRManifest.CanonicalIdentity(identity), Is.EqualTo(Expected));
        }

        [Test]
        public void CanonicalRoundTripPreservesIdentityAndVerifiesPayload()
        {
            var manifest = Manifest();
            manifest.Seal();
            var json = manifest.ToJson();
            var parsed = HybridCLRManifest.Parse(json);
            parsed.Validate("hotupdate-test", "base-v1", "2022.3.62f3c1", false);
            Assert.That(parsed.ToJson(), Is.EqualTo(json));
            Assert.That(parsed.CodePackageId, Has.Length.EqualTo(64));
            Assert.That(parsed.HotUpdateAssemblies.Single().Name, Is.EqualTo("Hot.Entry"));
            HybridCLRManifest.ValidatePayload(parsed.HotUpdateAssemblies[0], new byte[] { 1, 2, 3 });
        }

        [Test]
        public void InputOrderDoesNotChangeCanonicalPackageIdentity()
        {
            var first = Manifest();
            var entry = first.HotUpdateAssemblies[0];
            entry.Dependencies = new[] { "Hot.Z", "Hot.A" };
            entry.References = new[] { Payload("Hot.Z").Identity, Payload("Hot.A").Identity };
            first.HotUpdateAssemblies = new[] { entry, Payload("Hot.Z"), Payload("Hot.A") };
            var second = Manifest();
            second.HotUpdateAssemblies = new[] { Payload("Hot.A"), Payload("Hot.Z"), new HybridCLRPayload
            {
                Name = entry.Name, Identity = entry.Identity, Mvid = entry.Mvid, Address = entry.Address,
                Length = entry.Length, Sha256 = entry.Sha256, Dependencies = new[] { "Hot.A", "Hot.Z" },
                References = entry.References.Reverse().ToArray()
            } };
            first.Seal();
            second.Seal();
            CollectionAssert.AreEqual(new[] { "Hot.A", "Hot.Z", "Hot.Entry" }, first.HotUpdateAssemblies.Select(p => p.Name));
            Assert.That(second.ToJson(), Is.EqualTo(first.ToJson()));
        }

        [TestCase("project")]
        [TestCase("base")]
        [TestCase("unity")]
        [TestCase("development")]
        public void LoadingAnotherBaseOrBuildContextIsRejected(string mismatch)
        {
            var manifest = Manifest();
            manifest.Seal();
            Assert.Throws<InvalidDataException>(() => manifest.Validate(mismatch == "project" ? "other" : "hotupdate-test",
                mismatch == "base" ? "base-v2" : "base-v1", mismatch == "unity" ? "2022.3.61f1" : "2022.3.62f3c1",
                mismatch == "development"));
        }

        [TestCase("schema")]
        [TestCase("version")]
        [TestCase("platform")]
        [TestCase("empty")]
        [TestCase("duplicate")]
        [TestCase("case")]
        [TestCase("address")]
        [TestCase("identity")]
        [TestCase("mvid")]
        [TestCase("size")]
        [TestCase("missing-dependency")]
        [TestCase("cycle")]
        public void InvalidManifestStructureCannotBeSealed(string invalid)
        {
            var manifest = Manifest();
            var entry = manifest.HotUpdateAssemblies[0];
            switch (invalid)
            {
                case "schema":
                    manifest.SchemaVersion = 2;
                    break;
                case "version":
                    manifest.HybridCLRVersion = "9.0.4";
                    break;
                case "platform":
                    manifest.Platform = "Android";
                    break;
                case "empty":
                    manifest.HotUpdateAssemblies = Array.Empty<HybridCLRPayload>();
                    break;
                case "duplicate":
                    manifest.HotUpdateAssemblies = new[] { entry, Payload(entry.Name) };
                    break;
                case "case":
                    manifest.HotUpdateAssemblies = new[] { entry, Payload("hot.entry") };
                    break;
                case "address":
                    entry.Address = "hybridclr/assemblies/../Hot.Entry.dll.bytes";
                    break;
                case "identity":
                    entry.Identity = Payload("Other").Identity;
                    break;
                case "mvid":
                    entry.Mvid = "not-a-module-id";
                    break;
                case "size":
                    entry.Length = 64L * 1024 * 1024 + 1;
                    break;
                case "missing-dependency":
                    entry.Dependencies = new[] { "Absent" };
                    break;
                case "cycle":
                    entry.Dependencies = new[] { "Hot.Other" };
                    entry.References = new[] { Payload("Hot.Other").Identity };
                    var other = Payload("Hot.Other");
                    other.Dependencies = new[] { entry.Name };
                    other.References = new[] { entry.Identity };
                    manifest.HotUpdateAssemblies = new[] { entry, other };
                    break;
            }
            Assert.Throws<InvalidDataException>(() => manifest.Seal());
        }

        [Test]
        public void HashAndLengthAreCheckedAgainstRealBytes()
        {
            var manifest = Manifest();
            manifest.Seal();
            Assert.Throws<InvalidDataException>(() => HybridCLRManifest.ValidatePayload(manifest.HotUpdateAssemblies[0], new byte[] { 1, 2, 4 }));
            Assert.Throws<InvalidDataException>(() => HybridCLRManifest.ValidatePayload(manifest.HotUpdateAssemblies[0], new byte[] { 1, 2 }));
            Assert.That(HybridCLRManifest.Hash(new byte[] { 1, 2, 3 }), Is.EqualTo(manifest.HotUpdateAssemblies[0].Sha256));
        }

        [Test]
        public void UnknownDuplicateOrTrailingJsonMembersAreRejected()
        {
            var manifest = Manifest();
            manifest.Seal();
            var json = manifest.ToJson();
            Assert.Throws<InvalidDataException>(() => HybridCLRManifest.Parse(json.Insert(1, "\"unknown\":1,")));
            Assert.Throws<InvalidDataException>(() => HybridCLRManifest.Parse(json.Insert(1, "\"schemaVersion\":1,")));
            Assert.Throws<InvalidDataException>(() => HybridCLRManifest.Parse(json + "{}"));
        }

        [Test]
        public void ModifiedDescriptorDoesNotRetainAValidCodePackageIdentity()
        {
            var manifest = Manifest();
            manifest.Seal();
            manifest.HotUpdateAssemblies[0].Sha256 = new string('a', 64);
            Assert.Throws<InvalidDataException>(() => manifest.Validate("hotupdate-test", "base-v1", "2022.3.62f3c1", false));
        }
    }
}
