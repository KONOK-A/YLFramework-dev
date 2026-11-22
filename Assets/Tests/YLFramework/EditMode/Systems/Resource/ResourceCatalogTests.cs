using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;

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
            var error = new ResourceException(ResourceError.LoadFailed, "Resource failure", cause, address,
                sourceId: ResourceSourceId.Resources);
            ((Exception)error).Source = "Caller.Component";
            Assert.That(error.SourceId, Is.EqualTo(ResourceSourceId.Resources));
            Assert.That(error.Source, Is.EqualTo("Caller.Component"));
            Assert.That(((Exception)error).Source, Is.EqualTo("Caller.Component"));
            Assert.That(error.Address, Is.EqualTo(address));
            Assert.That(error.InnerException, Is.SameAs(cause));
        }

        // Private Catalog alias/physical identity/dependency-graph protocols are retired, not emulated here.
        // Their exact originals and hashes are preserved in LocalArchives/ResourceLegacy/T9SessionTests-20261117-e16c1562.
        [Test]
        public void AssetLeaseTypesMustBeCompiledUnityObjects()
        {
            var argument = typeof(AssetLease<>).GetGenericArguments()[0];
            Assert.That(argument.GetGenericParameterConstraints(), Does.Contain(typeof(UnityEngine.Object)));
            Assert.DoesNotThrow(() => typeof(AssetLease<>).MakeGenericType(typeof(TextAsset)));
            Assert.Throws<ArgumentException>(() => typeof(AssetLease<>).MakeGenericType(typeof(string)));
        }

        [Test]
        public void AddressesPreserveOrdinalProjectPathAndSubAssetIdentity()
        {
            var type = Type.GetType("YLFramework.Resource.ResourceAddress, YLFramework.Runtime");
            Assert.That(type, Is.Not.Null, "The formal immutable resource address is missing.");
            var first = Activator.CreateInstance(type, "fixture", "Text/Message", null);
            var same = Activator.CreateInstance(type, "fixture", "Text/Message", null);
            var differentCase = Activator.CreateInstance(type, "fixture", "text/Message", null);
            var differentProjectCase = Activator.CreateInstance(type, "Fixture", "Text/Message", null);
            var subAsset = Activator.CreateInstance(type, "fixture", "Text/Message", "part");
            Assert.That(first, Is.EqualTo(same));
            Assert.That(first, Is.Not.EqualTo(differentCase));
            Assert.That(first, Is.Not.EqualTo(differentProjectCase));
            Assert.That(first, Is.Not.EqualTo(subAsset));
            Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(type.IsValueType, Is.True);
            Assert.That(default(ResourceAddress).IsValid, Is.False);
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
