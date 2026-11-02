using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    public class ResourceGroupPreloadTests
    {
        private TextAsset _asset;
        private ResourceSystemTests.ControlledSource _healthy;
        private ResourceSystemTests.ControlledSource _other;
        private ResourceSystem _system;
        private readonly ResourceAddress _address = new("preload89", "text");

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another resource authority.");
            _asset = new TextAsset("preload89-owned");
            _healthy = new ResourceSystemTests.ControlledSource("preload89") { Asset = _asset };
            _other = new ResourceSystemTests.ControlledSource("other89") { Asset = _asset };
            var catalog = new ResourceCatalog("preload89", new[]
            {
                new ResourceCatalogEntry("text-id", _address, new ResourceLocation(_healthy.Id, "Text", typeof(TextAsset))),
                new ResourceCatalogEntry("other-id", new ResourceAddress("preload89", "other"), new ResourceLocation(_other.Id, "Other", typeof(TextAsset)))
            });
            catalog.Groups = new[]
            {
                new ResourceCatalogGroup("one", new[] { new ResourceCatalogGroupMember("text-id", true) }),
                new ResourceCatalogGroup("required", new[] { new ResourceCatalogGroupMember("text-id", true), new ResourceCatalogGroupMember("other-id", true) }),
                new ResourceCatalogGroup("optional", new[] { new ResourceCatalogGroupMember("text-id", true), new ResourceCatalogGroupMember("other-id", false) })
            };
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { catalog }, new IResourceSource[] { _healthy, _other }));
            _system = initialized.TakeResult();
        }

        private IDisposable Preload(string group) => (IDisposable)Resource89TestApi.Call(_system, "PreloadGroupAsync", "preload89", group);

        [TearDown]
        public void TearDown()
        {
            Resource89TestApi.EndSession("EndSession");
            foreach (var source in new[] { _healthy, _other })
            {
                foreach (var operation in source.Operations)
                {
                    if (!operation.IsDone)
                    {
                        operation.Finish(_asset);
                    }
                }
            }

            Resource89TestApi.EndSession("ResetSession");
            Object.DestroyImmediate(_asset);
        }

        [Test]
        public void PreloadAcquiresRealAssetsSharesProducersAndTransfersAggregateResponsibility()
        {
            using var first = Preload("one");
            using var second = Preload("one");
            using var outside = _system.AcquireAsync<TextAsset>(_address);
            Assert.That(_healthy.Loads, Is.EqualTo(1));
            _healthy.Operations.Single().Finish(_asset);
            using var group = (IDisposable)Resource89TestApi.Call(first, "TakeResult");
            var members = Resource89TestApi.Rows(group, "Assets");
            Assert.That(members.Length, Is.EqualTo(1));
            Assert.That(Resource89TestApi.Read<Object>(members[0], "Asset"), Is.SameAs(_asset), "Bundle warming alone is not an asset preload.");
            using var ready = Preload("one");
            Assert.That(_healthy.Loads, Is.EqualTo(1));
            first.Dispose();
            second.Dispose();
            outside.Dispose();
            ready.Dispose();
            Assert.That(_healthy.Releases, Is.Zero, "The claimed aggregate must retain its asset after all request owners leave.");
            group.Dispose();
            Assert.That(_healthy.Releases, Is.EqualTo(1));
            Assert.That(_system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void CancelingOnePreloadDoesNotCancelAnotherAndLastLateResultIsReleased()
        {
            using var canceled = Preload("one");
            using var survivor = Preload("one");
            Resource89TestApi.Call(canceled, "Cancel");
            _healthy.Operations.Single().Finish(_asset);
            Assert.That(Resource89TestApi.Read<ResourceRequestStatus>(canceled, "Status"), Is.EqualTo(ResourceRequestStatus.Canceled));
            using var held = (IDisposable)Resource89TestApi.Call(survivor, "TakeResult");
            Assert.That(_healthy.Releases, Is.Zero);
            held.Dispose();
            using var abandoned = Preload("one");
            abandoned.Dispose();
            _healthy.Operations[1].Finish(_asset);
            Assert.That(_healthy.Releases, Is.EqualTo(2));
            Assert.That(_system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void RequiredFailureRollsBackWholeBatchAndOptionalFailurePreservesMemberCause()
        {
            _healthy.Immediate = true;
            _other.ThrowOnStart = true;
            using var outside = _system.Acquire<TextAsset>(_address);
            using (var failed = Preload("required"))
            {
                Assert.That(Resource89TestApi.Read<ResourceRequestStatus>(failed, "Status"), Is.EqualTo(ResourceRequestStatus.Failed));
                var error = Resource89TestApi.Read<ResourceException>(failed, "Error");
                Assert.That(error.InnerException, Is.InstanceOf<AggregateException>());
                Assert.That(error.ToString(), Does.Contain("controlled-load-start"));
            }
            Assert.That(outside.Asset, Is.SameAs(_asset), "Rolling back one batch cannot release an independent consumer's lease.");
            Assert.That(_healthy.Releases, Is.Zero);
            outside.Dispose();
            Assert.That(_healthy.Releases, Is.EqualTo(1));
            using var optional = Preload("optional");
            using var group = (IDisposable)Resource89TestApi.Call(optional, "TakeResult");
            var items = Resource89TestApi.Rows(group, "Items");
            Assert.That(items.Length, Is.EqualTo(2));
            var failedItem = items.Single(x => Resource89TestApi.Read<string>(x, "AssetId") == "other-id");
            Assert.That(Resource89TestApi.Read<ResourceException>(failedItem, "Error").InnerException, Is.InstanceOf<InvalidOperationException>());
            Assert.That(Resource89TestApi.Rows(group, "Assets").Length, Is.EqualTo(1));
            group.Dispose();
            Assert.That(_system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void RequiredFailureStopsItsPendingChildrenAndTheirLateResultCannotLeak()
        {
            _other.ThrowOnStart = true;
            using var failed = Preload("required");
            Assert.That(Resource89TestApi.Read<ResourceRequestStatus>(failed, "Status"), Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(_system.GetDiagnosticsSnapshot().WaiterCount, Is.Zero);
            Assert.That(_system.GetDiagnosticsSnapshot().ProducerCount, Is.EqualTo(1), "An uncancelable native producer remains responsible for its own late cleanup.");
            _healthy.Operations.Single().Finish(_asset);
            Assert.That(_healthy.Releases, Is.EqualTo(1));
            Assert.That(_system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void ScopeCanClaimTheAggregateAndDisposalOnlyReleasesItsOwnPreload()
        {
            _healthy.Immediate = true;
            using var scope = _system.CreateScope();
            using var external = _system.Acquire<TextAsset>(_address);
            using var preload = Preload("one");
            var resultType = preload.GetType().GetGenericArguments().Single();
            typeof(ResourceScope).GetMethod("Track").MakeGenericMethod(resultType).Invoke(scope, new object[] { preload });
            var owned = typeof(ResourceScope).GetMethod("TakeResult").MakeGenericMethod(resultType).Invoke(scope, new object[] { preload });
            scope.Dispose();
            Assert.That(Resource89TestApi.Read<bool>(owned, "IsDisposed"), Is.True);
            Assert.That(external.Asset, Is.SameAs(_asset));
            Assert.That(_healthy.Releases, Is.Zero);
            external.Dispose();
            Assert.That(_healthy.Releases, Is.EqualTo(1));
        }
    }
}
