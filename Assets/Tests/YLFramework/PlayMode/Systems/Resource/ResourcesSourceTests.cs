using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Resource
{
    public class ResourcesSourceTests
    {
        private const string Locator = "YLResource81/Message";
        private readonly ResourceAddress _address = new("resources81", "message");

        private ResourceConfiguration Configuration(string locator = Locator, Type type = null) => new(new[]
        {
            new ResourceCatalog("resources81", new[]
            {
                new ResourceCatalogEntry("message", _address, new ResourceLocation(ResourceSourceId.Resources, locator, type ?? typeof(TextAsset)))
            })
        });

        private ResourceSystem Initialize(ResourceConfiguration config)
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another resource authority.");
            using var request = ResourceSystem.InitializeAsync(config);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            return request.TakeResult();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (ResourceSystem.Current == null)
            {
                yield break;
            }

            using var shutdown = ResourceSystem.Current.ShutdownAsync();
            for (var frame = 0; frame < 120 && !shutdown.IsDone; frame++)
            {
                yield return null;
            }

            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
        }

        [Test]
        public void RealResourcesSyncReleaseDropsRecordsButPreservesExternalBorrow()
        {
            var external = Resources.Load<TextAsset>(Locator);
            Assert.That(external, Is.Not.Null);
            var system = Initialize(Configuration());
            using (var lease = system.Acquire<TextAsset>(_address))
            {
                Assert.That(lease.Asset, Is.SameAs(external));
                Assert.That(lease.Asset.text.Trim(), Is.EqualTo("YL Resource 8.1 isolated Resources fixture."));
            }
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            Assert.That(external, Is.Not.Null);
            Assert.That(external.text, Does.Contain("isolated Resources fixture"));
        }

        [UnityTest]
        public IEnumerator RealResourcesAsyncRequestsHaveIndependentClaimAndScopeOwnership()
        {
            var system = Initialize(Configuration());
            using var scope = system.CreateScope();
            var first = scope.AcquireAsync<TextAsset>(_address);
            using var second = system.AcquireAsync<TextAsset>(_address);
            for (var frame = 0; frame < 120 && !second.IsDone; frame++)
            {
                yield return null;
            }

            Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), second.Error?.ToString());
            var a = scope.TakeResult(first);
            using var b = second.TakeResult();
            Assert.That(a, Is.Not.SameAs(b));
            Assert.That(a.Asset, Is.SameAs(b.Asset));
            scope.Dispose();
            Assert.That(b.Asset, Is.Not.Null);
            second.Dispose();
            Assert.That(b.Asset.text, Does.Contain("isolated Resources fixture"));
            b.Dispose();
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RealResourcesLastWaiterDisposeRetainsNativeCleanupWithoutHost()
        {
            var external = Resources.Load<TextAsset>(Locator);
            var system = Initialize(Configuration());
            var request = system.AcquireAsync<TextAsset>(_address);
            request.Dispose();
            using var shutdown = system.ShutdownAsync();
            for (var frame = 0; frame < 120 && !shutdown.IsDone; frame++)
            {
                yield return null;
            }

            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            Assert.That(system.GetDiagnosticsSnapshot().ProducerCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(external, Is.Not.Null);
            Assert.That(external.text, Does.Contain("isolated Resources fixture"));
        }

        [UnityTest]
        public IEnumerator MissingRealResourcesFailAndInvalidLocatorsNeverInitialize()
        {
            using (var invalid = ResourceSystem.InitializeAsync(Configuration("Assets/Resources/YLResource81/Message.txt")))
            {
                Assert.That(invalid.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(invalid.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
                Assert.That(ResourceSystem.Current, Is.Null);
            }
            var system = Initialize(Configuration("YLResource81/DoesNotExist"));
            using var request = system.AcquireAsync<TextAsset>(_address);
            for (var frame = 0; frame < 120 && !request.IsDone; frame++)
            {
                yield return null;
            }

            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(request.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        private ResourceConfiguration ProjectionConfiguration() => new(new[]
        {
            new ResourceCatalog("resources81", new[]
            {
                new ResourceCatalogEntry("sprite", _address,
                    new ResourceLocation(ResourceSourceId.Resources, "YLResource81/ProjectionSprite81", typeof(Sprite))),
                new ResourceCatalogEntry("texture", new ResourceAddress("resources81", "texture"),
                    new ResourceLocation(ResourceSourceId.Resources, "YLResource81/ProjectionSprite81", typeof(Texture2D)))
            })
        });

        [Test]
        public void CatalogDeclaredNativeTypeDisambiguatesBaseTypeSyncProjection()
        {
            var system = Initialize(ProjectionConfiguration());
            using var broad = system.Acquire<UnityEngine.Object>(_address);
            using var specific = system.Acquire<Sprite>(_address);
            using var texture = system.Acquire<UnityEngine.Object>(new ResourceAddress("resources81", "texture"));
            Assert.That(broad.Asset, Is.InstanceOf<Sprite>());
            Assert.That(broad.Asset, Is.SameAs(specific.Asset));
            Assert.That(texture.Asset, Is.InstanceOf<Texture2D>());
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator BaseTypeAsyncProjectionSharesTheDeclaredNativeProducer()
        {
            var system = Initialize(ProjectionConfiguration());
            using var broad = system.AcquireAsync<UnityEngine.Object>(_address);
            using var specific = system.AcquireAsync<Sprite>(_address);
            Assert.That(system.GetDiagnosticsSnapshot().ProducerCount, Is.LessThanOrEqualTo(1));
            for (var frame = 0; frame < 120 && !broad.IsDone; frame++)
            {
                yield return null;
            }

            Assert.That(broad.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), broad.Error?.ToString());
            using var a = broad.TakeResult();
            using var b = specific.TakeResult();
            Assert.That(a.Asset, Is.InstanceOf<Sprite>());
            Assert.That(a.Asset, Is.SameAs(b.Asset));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.EqualTo(1));
        }
    }
}
