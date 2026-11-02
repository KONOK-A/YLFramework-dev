using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Module.Resource")]
    public class ResourceCatalogTests
    {
        [Test]
        public void ResourceSourceIdentityIsSeparateFromExceptionDiagnosticSource()
        {
            var address = new ResourceAddress("fixture", "Text/Message");
            var cause = new InvalidOperationException("Original failure");
            var error = new ResourceException(ResourceError.LoadFailed, "Resource failure", cause, address, ResourceSourceId.Resources);
            ((Exception)error).Source = "Caller.Component";
            Assert.That(error.Source, Is.EqualTo(ResourceSourceId.Resources));
            Assert.That(((Exception)error).Source, Is.EqualTo("Caller.Component"));
            Assert.That(error.Address, Is.EqualTo(address));
            Assert.That(error.InnerException, Is.SameAs(cause));
        }

        [Test]
        public void CatalogResolvesAliasesToOneEntryAndCopiesInputs()
        {
            var address = new ResourceAddress("fixture", "Text/Message");
            var alias = new ResourceAddress("fixture", "message");
            var aliases = new[] { alias };
            var entry = new ResourceCatalogEntry("message-id", address,
                new ResourceLocation(ResourceSourceId.Resources, "YLResource81/Message", typeof(TextAsset)), aliases);
            var entries = new[] { entry };
            var catalog = new ResourceCatalog("fixture", entries);
            aliases[0] = new ResourceAddress("fixture", "changed");
            entries[0] = null;
            Assert.That(catalog.Resolve(alias), Is.SameAs(entry));
            Assert.That(catalog.Resolve(address), Is.SameAs(entry));
            Assert.Throws<ArgumentException>(() => catalog.Resolve(new ResourceAddress("fixture", "changed")));
            Assert.That(new ResourceCatalog("fixture", new[] { entry }).SnapshotId, Is.Not.EqualTo(catalog.SnapshotId));
        }

        [Test]
        public void CatalogRejectsAddressCaseConflictsAndDuplicatePhysicalIdentity()
        {
            var location = new ResourceLocation(ResourceSourceId.Resources, "YLResource81/Message", typeof(TextAsset));
            var first = new ResourceCatalogEntry("one", new ResourceAddress("fixture", "Text"), location);
            var second = new ResourceCatalogEntry("two", new ResourceAddress("fixture", "text"),
                new ResourceLocation(ResourceSourceId.Resources, "YLResource81/Other", typeof(TextAsset)));
            Assert.Throws<ArgumentException>(() => new ResourceCatalog("fixture", new[] { first, second }));
            var duplicate = new ResourceCatalogEntry("two", new ResourceAddress("fixture", "other"), location);
            Assert.Throws<ArgumentException>(() => new ResourceCatalog("fixture", new[] { first, duplicate }));
            Assert.Throws<ArgumentException>(() => new ResourceCatalog("different-project", new[] { first }));
            Assert.Throws<ArgumentException>(() => new ResourceCatalog("fixture", new[] { first, first }));
        }

        [Test]
        public void SourceLocatorsAreExactAndTypesMustBeCompiledUnityObjects()
        {
            var upper = new ResourceLocation(new ResourceSourceId("remote"), "https://host/Image.PNG?Token=A", typeof(Texture2D));
            var lower = new ResourceLocation(new ResourceSourceId("remote"), "https://host/image.png?Token=A", typeof(Texture2D));
            var otherSource = new ResourceLocation(new ResourceSourceId("local"), upper.Locator, typeof(Texture2D));
            Assert.That(upper, Is.Not.EqualTo(lower));
            Assert.That(upper, Is.Not.EqualTo(otherSource));
            Assert.Throws<ArgumentException>(() => new ResourceLocation(ResourceSourceId.Resources, "path", typeof(string)));
            Assert.Throws<ArgumentException>(() => new ResourceLocation(default, "path", typeof(TextAsset)));
        }

        [Test]
        public void CatalogValidatesMissingDependenciesCyclesAndValidDiamond()
        {
            var diamond = new[]
            {
                new ResourceDependencyNode("root", new[] { "left", "right" }),
                new ResourceDependencyNode("left", new[] { "leaf" }),
                new ResourceDependencyNode("right", new[] { "leaf" }),
                new ResourceDependencyNode("leaf")
            };
            Assert.DoesNotThrow(() => new ResourceCatalog("fixture", Array.Empty<ResourceCatalogEntry>(), diamond));
            Assert.Throws<ArgumentException>(() => new ResourceCatalog("fixture", Array.Empty<ResourceCatalogEntry>(),
                new[] { new ResourceDependencyNode("root", new[] { "missing" }) }));
            var error = Assert.Throws<ArgumentException>(() => new ResourceCatalog("fixture", Array.Empty<ResourceCatalogEntry>(),
                new[] { new ResourceDependencyNode("one", new[] { "two" }), new ResourceDependencyNode("two", new[] { "one" }) }));
            Assert.That(error.Message, Does.Contain("one").And.Contain("two"));
        }

        [Test]
        public void AddressesPreserveOrdinalProjectPathAndSubAssetIdentity()
        {
            var type = Type.GetType("YLFramework.Resource.ResourceAddress, YLFramework.Runtime");
            Assert.That(type, Is.Not.Null, "The formal immutable resource address is missing.");
            var first = Activator.CreateInstance(type, "fixture", "Text/Message", null);
            var same = Activator.CreateInstance(type, "fixture", "Text/Message", null);
            var differentCase = Activator.CreateInstance(type, "fixture", "text/Message", null);
            var subAsset = Activator.CreateInstance(type, "fixture", "Text/Message", "part");
            Assert.That(first, Is.EqualTo(same));
            Assert.That(first, Is.Not.EqualTo(differentCase));
            Assert.That(first, Is.Not.EqualTo(subAsset));
            foreach (var property in type.GetProperties())
            {
                Assert.That(property.SetMethod, Is.Null);
            }
        }

        [TestCase("", "text")]
        [TestCase("../fixture", "text")]
        [TestCase("fixture", "")]
        [TestCase("fixture", "../text")]
        [TestCase("fixture", "folder\\text")]
        public void InvalidAddressesAreRejectedRatherThanNormalized(string project, string path)
        {
            var type = Type.GetType("YLFramework.Resource.ResourceAddress, YLFramework.Runtime");
            Assert.That(type, Is.Not.Null, "The formal immutable resource address is missing.");
            var error = Assert.Throws<TargetInvocationException>(() => Activator.CreateInstance(type, project, path, null));
            Assert.That(error.InnerException, Is.InstanceOf<ArgumentException>());
        }
    }
}
