using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Resource
{
    internal static class Resource89TestApi
    {
        internal static object Call(object target, string method, params object[] arguments)
        {
            var member = target.GetType().GetMethods().SingleOrDefault(x => x.Name == method && x.GetParameters().Length == arguments.Length);
            Assert.That(member, Is.Not.Null, method + " must expose the formal 8.9 API.");
            return member.Invoke(target, arguments);
        }

        internal static T Read<T>(object target, string name)
        {
            var member = target.GetType().GetProperty(name);
            Assert.That(member, Is.Not.Null, "Missing diagnostic/result property: " + name);
            return (T)member.GetValue(target);
        }

        internal static object[] Rows(object target, string name) => Read<IEnumerable>(target, name).Cast<object>().ToArray();
        internal static void EndSession(string method) => typeof(ResourceSystem).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    }

    public class ResourceDiagnosticsTests
    {
        private TextAsset _asset;
        private ResourceSystemTests.ControlledSource _source;
        private ResourceSystem _system;
        private readonly ResourceAddress _address = new("diagnostics89", "text");
        private readonly ResourceAddress _alias = new("diagnostics89", "alias");

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another resource authority.");
            _asset = new TextAsset("diagnostics89-owned");
            _source = new ResourceSystemTests.ControlledSource("diagnostics89") { Asset = _asset };
            using var request = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog("diagnostics89", new[]
                {
                    new ResourceCatalogEntry("text-id", _address, new ResourceLocation(_source.Id, "Exact/Locator", typeof(TextAsset)), new[] { _alias })
                })
            }, new IResourceSource[] { _source }));
            _system = request.TakeResult();
        }

        [TearDown]
        public void TearDown()
        {
            Resource89TestApi.EndSession("EndSession");
            foreach (var operation in _source.Operations)
            {
                if (!operation.IsDone)
                {
                    operation.Finish(_asset);
                }
            }

            Resource89TestApi.EndSession("ResetSession");
            if (_asset)
            {
                Object.DestroyImmediate(_asset);
            }
        }

        [Test]
        public void ReportCopiesCanonicalProducerWaitersAndLeaseOwnersWithoutKeepingObjectsAlive()
        {
            using var first = _system.AcquireAsync<TextAsset>(_address);
            using var second = _system.AcquireAsync<TextAsset>(_alias);
            var before = Resource89TestApi.Call(_system, "GetDiagnosticsReport");
            var loading = Resource89TestApi.Rows(before, "Assets").Single();
            Assert.That(Resource89TestApi.Read<ResourceAddress>(loading, "Address"), Is.EqualTo(_address));
            Assert.That(Resource89TestApi.Read<int>(loading, "WaiterCount"), Is.EqualTo(2));
            Assert.That(Resource89TestApi.Read<bool>(loading, "IsLoading"), Is.True);
            Assert.That(Resource89TestApi.Read<bool>(loading, "IsObjectAlive"), Is.False);
            _source.Operations.Single().Finish(_asset);
            using var a = first.TakeResult();
            using var b = second.TakeResult();
            var label = a.GetType().GetProperty("OwnerLabel");
            Assert.That(label, Is.Not.Null, "A real lease must accept an explicit diagnostic owner label.");
            label.SetValue(a, "HUD title");
            var ready = Resource89TestApi.Call(_system, "GetDiagnosticsReport");
            var row = Resource89TestApi.Rows(ready, "Assets").Single();
            Assert.That(Resource89TestApi.Read<int>(row, "LeaseCount"), Is.EqualTo(2));
            Assert.That(Resource89TestApi.Read<int>(row, "ObjectInstanceId"), Is.EqualTo(_asset.GetInstanceID()));
            Assert.That(Resource89TestApi.Read<bool>(row, "IsObjectAlive"), Is.True);
            var owners = Resource89TestApi.Rows(row, "Owners");
            Assert.That(owners.All(x => Resource89TestApi.Read<ResourceAddress>(x, "Address") == _address), Is.True);
            Assert.That(owners.Count(x => Resource89TestApi.Read<ResourceAddress>(x, "RequestedAddress") == _alias), Is.EqualTo(1));
            Assert.That(owners.Count(x => Resource89TestApi.Read<string>(x, "OwnerLabel") == "HUD title"), Is.EqualTo(1));
            Assert.That(owners.Count(x => !Resource89TestApi.Read<bool>(x, "IsLabeled")), Is.EqualTo(1), "Unlabeled responsibility must not be guessed.");
            Assert.That(Resource89TestApi.Read<IList>(ready, "Assets").IsReadOnly, Is.True);
            Assert.That(Resource89TestApi.Read<int>(loading, "WaiterCount"), Is.EqualTo(2), "Earlier snapshots cannot follow mutable live records.");
            a.Dispose();
            b.Dispose();
            Assert.That(Resource89TestApi.Rows(Resource89TestApi.Call(_system, "GetDiagnosticsReport"), "Assets"), Is.Empty);
            Assert.That(Resource89TestApi.Read<int>(row, "LeaseCount"), Is.EqualTo(2));
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void ReportUsesUnityFakeNullRatherThanManagedReferenceForDestroyedAssets()
        {
            using var lease = _system.Acquire<TextAsset>(_address);
            Object.DestroyImmediate(_asset);
            var row = Resource89TestApi.Rows(Resource89TestApi.Call(_system, "GetDiagnosticsReport"), "Assets").Single();
            Assert.That(Resource89TestApi.Read<bool>(row, "IsObjectAlive"), Is.False);
            Assert.That(Resource89TestApi.Read<int>(row, "LeaseCount"), Is.EqualTo(1));
            Assert.That(_source.Releases, Is.Zero, "Reading diagnostics must not collect or repair resource ownership.");
        }

        [Test]
        public void HistoryIsOptInBoundedAndReportsCancellationFailureAndLateCleanup()
        {
            using (var ignored = _system.AcquireAsync<TextAsset>(_address))
            {
                ignored.Cancel();
            }

            _source.Operations[0].Finish(_asset);
            Assert.That(Resource89TestApi.Rows(Resource89TestApi.Call(_system, "GetDiagnosticsReport"), "History"), Is.Empty);
            Resource89TestApi.Call(_system, "ConfigureDiagnosticsHistory", 3);
            using (var late = _system.AcquireAsync<TextAsset>(_address))
            {
                late.Cancel();
            }

            var canceled = Resource89TestApi.Rows(Resource89TestApi.Call(_system, "GetDiagnosticsReport"), "History").Single();
            Assert.That(Resource89TestApi.Read<ResourceRequestStatus?>(canceled, "Status"), Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(Resource89TestApi.Read<bool>(canceled, "HasDuration"), Is.True);
            Assert.That(Resource89TestApi.Read<double>(canceled, "DurationMilliseconds"), Is.GreaterThan(0));
            _source.Operations[1].Finish(_asset);
            using var failed = _system.AcquireAsync<TextAsset>(_address);
            _source.Operations[2].Finish(null, new InvalidOperationException("diagnostics89-cause"));
            var history = Resource89TestApi.Rows(Resource89TestApi.Call(_system, "GetDiagnosticsReport"), "History");
            Assert.That(history.Length, Is.EqualTo(3));
            Assert.That(history.Any(x => Resource89TestApi.Read<string>(x, "Kind") == "LateResult"), Is.True);
            Assert.That(history.Any(x => Resource89TestApi.Read<string>(x, "Cause")?.Contains("diagnostics89-cause") == true &&
                Resource89TestApi.Read<bool>(x, "HasDuration")), Is.True);
            Assert.That(history.All(x => Resource89TestApi.Read<double>(x, "DurationMilliseconds") >= 0), Is.True);
            Resource89TestApi.Call(_system, "ConfigureDiagnosticsHistory", 0);
            Assert.That(Resource89TestApi.Rows(Resource89TestApi.Call(_system, "GetDiagnosticsReport"), "History"), Is.Empty);
        }
    }
}
