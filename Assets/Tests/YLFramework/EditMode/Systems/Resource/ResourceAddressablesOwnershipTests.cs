#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using UnityEngine.U2D;
using YLFramework.Core;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    public sealed partial class ResourceAddressablesOwnershipTests
    {
        [UnitySetUp]
        public IEnumerator Prepare()
        {
            if (!Application.isPlaying) ResourceAddressablesTestContent.Build();
            if (!Application.isPlaying) yield return new EnterPlayMode();
        }

        [UnityTearDown]
        public IEnumerator Close()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SameScopeSharesAHoldButIndependentAcquisitionsDoNot()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync();
            yield return Wait(start);
            start.TakeResult();
            start.Dispose();
            var system = startup.Resources;
            using var scope = system.CreateScope("ownership-test");
            using var first = scope.LoadAsync<TextAsset>("ownership/message");
            using var second = scope.LoadAsync<TextAsset>("ownership/message");
            yield return Wait(first);
            yield return Wait(second);
            Assert.That(first.TakeResult(), Is.SameAs(second.TakeResult()));
            Assert.That(Count(system), Is.EqualTo(1));

            using var independent = system.AcquireAsync<TextAsset>("ownership/message");
            yield return Wait(independent);
            using var lease = independent.TakeResult();
            Assert.That(Count(system), Is.EqualTo(2));
            using var retained = lease.Retain("retained");
            Assert.That(Count(system), Is.EqualTo(3));
            using var close = scope.CloseAsync();
            yield return Wait(close);
            Assert.That(close.TakeResult(), Is.True);
            Assert.That(lease.Asset.text, Is.EqualTo("official addressables content"));
            lease.Dispose();
            retained.Dispose();
            var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator ScopeCloseBetweenCompletionAndClaimRejectsBorrowedDelivery()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync();
            yield return Wait(start);
            start.TakeResult();
            var scope = startup.Resources.CreateScope("close-before-claim");
            var request = scope.LoadAsync<TextAsset>("ownership/message");
            request.Completed += _ => scope.Dispose();
            yield return request;

            Assert.Throws<ObjectDisposedException>(() => request.TakeResult());
            request.Dispose();
            var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator ScopeCloseWaitsForItsAbandonedProducerButNotForOtherOwners()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            using var producer = new DeferredBatchProvider();
            using var unrelated = new DeferredBatchProvider();
            using var scope = system.CreateScope("closing consumer");
            using var pending = scope.LoadAsync<TextAsset>(producer.Key);
            using var other = system.AcquireAsync<TextAsset>(unrelated.Key);
            var external = Addressables.LoadAssetAsync<TextAsset>(producer.Key);
            try
            {
                yield return Wait(() => producer.Provides == 1 && unrelated.Provides == 1);
                using var close = scope.CloseAsync();
                yield return Wait(() => system.GetDiagnostics().DrainingHandles.Any(row => row.Key == producer.Key));
                Assert.That(pending.IsDisposed, Is.True);
                Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Pending));
                Assert.That(system.GetDiagnostics().Scopes.Single().Closing, Is.True);
                producer.Complete();
                yield return Wait(close);
                Assert.That(close.TakeResult(), Is.True);
                Assert.That(other.IsDone, Is.False);
                Assert.That(external.Result, Is.SameAs(producer.Asset));
                Assert.That(producer.Releases, Is.Zero, "Closing does not consume the other official owner's pin.");
                unrelated.Complete();
                yield return Wait(other);
                using (var lease = other.TakeResult()) Assert.That(lease.Asset, Is.SameAs(unrelated.Asset));
                using var stop = startup.StopAsync();
                yield return Wait(stop);
                Assert.That(stop.TakeResult(), Is.True);
            }
            finally { Addressables.Release(external); }
            yield return Wait(() => producer.Releases == 1);
            Assert.That(unrelated.Releases, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CancelOneConsumerDoesNotCancelAnotherAndBusyDoesNotWaitForCaller()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync();
            yield return Wait(start);
            start.TakeResult();
            var canceled = startup.Resources.AcquireAsync<TextAsset>("ownership/message");
            var live = startup.Resources.AcquireAsync<TextAsset>("ownership/message");
            canceled.Cancel();
            yield return Wait(live);
            using var lease = live.TakeResult();
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(stop.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(lease.Asset.text, Is.EqualTo("official addressables content"));
            lease.Dispose();
            var retried = startup.StopAsync();
            yield return Wait(retried);
            Assert.That(retried.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator NeverActivatedOwnerDestroyedClosesItsScope()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync(); yield return Wait(start); start.TakeResult();
            var owner = new GameObject("never active owner");
            owner.SetActive(false);
            var scope = startup.Resources.CreateScope("destroy binding").BindToDestroy(owner);
            var load = scope.LoadAsync<TextAsset>("ownership/message");
            yield return Wait(load); load.TakeResult();
            UnityEngine.Object.Destroy(owner);
            float deadline = Time.realtimeSinceStartup + 30;
            while (!scope.IsDisposed && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(scope.IsDisposed, Is.True);
            var close = scope.CloseAsync(); yield return Wait(close); Assert.That(close.TakeResult(), Is.True);
            Assert.That(Count(startup.Resources), Is.Zero);
            var stop = startup.StopAsync(); yield return Wait(stop); Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator DisableDoesNotCloseScopeAndCachedPreCancellationDoesNotTakeAHold()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync(); yield return Wait(start); start.TakeResult();
            var owner = new GameObject("disabled owner");
            var scope = startup.Resources.CreateScope("disable binding").BindToDestroy(owner);
            var loaded = scope.LoadAsync<TextAsset>("ownership/message"); yield return Wait(loaded); loaded.TakeResult();
            owner.SetActive(false);
            yield return null;
            Assert.That(scope.IsDisposed, Is.False);
            using var cancellation = new System.Threading.CancellationTokenSource();
            cancellation.Cancel();
            var canceled = scope.LoadAsync<TextAsset>("ownership/message", cancellation.Token);
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            var error = Assert.Throws<OperationCanceledException>(() => canceled.GetAwaiter().GetResult());
            Assert.That(error.CancellationToken, Is.EqualTo(cancellation.Token));
            Assert.That(Count(startup.Resources), Is.EqualTo(1));
            var close = scope.CloseAsync(); yield return Wait(close); close.TakeResult();
            UnityEngine.Object.Destroy(owner);
            var stop = startup.StopAsync(); yield return Wait(stop); Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator SecondStartupCannotJoinOrStopTheExistingOwner()
        {
            var first = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var starting = first.StartAsync(); yield return Wait(starting); starting.TakeResult();
            var second = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var rejected = second.StartAsync(); yield return Wait(rejected);
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
            var nonOwnerStop = second.StopAsync(); yield return Wait(nonOwnerStop); nonOwnerStop.TakeResult();
            Assert.That(ResourceSystem.Current, Is.SameAs(first.Resources));
            Assert.That(first.Resources.IsAvailable, Is.True);
            var stop = first.StopAsync(); yield return Wait(stop); Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator TwoScopesHoldIndependentReferencesForTheSameKey()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync(); yield return Wait(start); start.TakeResult();
            var first = startup.Resources.CreateScope("first business");
            var second = startup.Resources.CreateScope("second business");
            var a = first.LoadAsync<TextAsset>(key: "ownership/message");
            var b = second.LoadAsync<TextAsset>("ownership/message");
            yield return Wait(a); yield return Wait(b);
            Assert.That(a.TakeResult(), Is.SameAs(b.TakeResult()));
            Assert.That(Count(startup.Resources), Is.EqualTo(2));
            var closeFirst = first.CloseAsync(); yield return Wait(closeFirst); closeFirst.TakeResult();
            Assert.That(Count(startup.Resources), Is.EqualTo(1));
            Assert.That(second.TryGetLoaded<TextAsset>("ownership/message", out var stillBorrowed), Is.True);
            Assert.That(stillBorrowed.text, Is.EqualTo("official addressables content"));
            var closeSecond = second.CloseAsync(); yield return Wait(closeSecond); closeSecond.TakeResult();
            var stop = startup.StopAsync(); yield return Wait(stop); Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator StopDuringStartupDoesNotResurrectTheService()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var starting = startup.StartAsync();
            var stopping = startup.StopAsync();
            yield return Wait(stopping);
            Assert.That(stopping.TakeResult(), Is.True);
            Assert.That(starting.IsDone, Is.True);
            Assert.That(starting.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [UnityTest]
        public IEnumerator DisposingAStopObserverDoesNotLeaveStartupOwningAClosedService()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync();
            yield return Wait(start);
            start.TakeResult();
            var scope = startup.Resources.CreateScope("stop observer");
            var loaded = scope.LoadAsync<TextAsset>("ownership/message");
            yield return Wait(loaded);
            loaded.TakeResult();
            var system = startup.Resources;
            var stop = startup.StopAsync();
            stop.Dispose();
            float deadline = Time.realtimeSinceStartup + 30;
            while (system.State != ResourceSystemState.Closed && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(system.State, Is.EqualTo(ResourceSystemState.Closed));
            Assert.That(startup.Resources, Is.Null);
            Assert.That(startup.OwnsResourceSystem, Is.False);
        }

        [UnityTest]
        public IEnumerator MissingAndAmbiguousKeysNeverSelectAnArbitraryAsset()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            var start = startup.StartAsync();
            yield return Wait(start);
            start.TakeResult();
            var missing = startup.Resources.AcquireAsync<TextAsset>("ownership/missing");
            var ambiguous = startup.Resources.AcquireAsync<TextAsset>("ownership/ambiguous");
            using var exact = startup.Resources.AcquireAsync<TextAsset>("ownership/aliased");
            yield return Wait(missing);
            yield return Wait(ambiguous);
            yield return Wait(exact);
            Assert.That(missing.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            Assert.That(ambiguous.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            using (var lease = exact.TakeResult())
            {
                Assert.That(lease.Asset.text, Is.EqualTo("same entry uses both key roles"));
            }
            Assert.That(Count(startup.Resources), Is.Zero);
            var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator RequiredBatchHoldsOneGroupAndScopeCloseDoesNotConsumeAnExternalPin()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            var external = Addressables.LoadAssetAsync<TextAsset>("ownership/message");
            try
            {
                yield return external;
                Assert.That(external.Result.text, Is.EqualTo("official addressables content"));
                using var scope = system.CreateScope("required-texts");
                var keys = new[] { "ownership/batch-texts", "ownership/message", "ownership/message" };
                using var request = system.AcquireBatchAsync<TextAsset>(keys);
                keys[0] = "ownership/batch-missing";
                yield return Wait(request);
                using var batch = request.TakeResult();
                CollectionAssert.AreEquivalent(new[] { "official addressables content", "second required entry" }, batch.Assets.Select(value => value.text));
                Assert.That(batch.Assets.Single(value => value.text.StartsWith("official", StringComparison.Ordinal)), Is.SameAs(external.Result));
                Assert.That(system.GetDiagnostics().LeaseCount, Is.EqualTo(1));
                Assert.That(scope.Own(batch), Is.SameAs(batch));
                Assert.Throws<InvalidOperationException>(() => scope.Own(batch));
                using var close = scope.CloseAsync();
                yield return Wait(close);
                Assert.That(close.TakeResult(), Is.True);
                Assert.That(batch.IsValid, Is.False);
                Assert.Throws<ObjectDisposedException>(() => _ = batch.Assets);
                Assert.That(external.IsValid() && external.Result, Is.True);
                using var stop = startup.StopAsync();
                yield return Wait(stop);
                Assert.That(stop.TakeResult(), Is.True);
                Assert.That(external.Result.text, Is.EqualTo("official addressables content"));
            }
            finally { Addressables.Release(external); }
        }

        [UnityTest]
        public IEnumerator MissingRequiredBatchKeyCannotSucceedWithTheValidSubset()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            using var request = system.AcquireBatchAsync<TextAsset>(new[] { "ownership/batch-texts", "ownership/batch-missing" });
            yield return Wait(request);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(request.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            Assert.That(system.GetDiagnostics().LeaseCount, Is.Zero);
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator CancelingOneRequiredBatchDrainsItsGroupWithoutRevokingTheOtherBatch()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            using var provider = new DeferredBatchProvider();
            var keys = new[] { "ownership/batch-texts", provider.Key };
            using var canceled = system.AcquireBatchAsync<TextAsset>(keys);
            using var live = system.AcquireBatchAsync<TextAsset>(keys);
            yield return Wait(() => provider.Provides == 1);
            yield return null;
            canceled.Cancel();
            yield return Wait(canceled);
            yield return null;
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(system.GetDiagnostics().DrainingHandleCount, Is.EqualTo(1));
            var drain = system.GetDiagnostics().DrainingHandles.Single();
            Assert.That(drain.Operation, Is.EqualTo(nameof(ResourceSystem.AcquireBatchAsync)));
            Assert.That(drain.Key, Does.Contain(provider.Key));
            Assert.That(live.IsDone, Is.False);
            provider.Complete();
            yield return Wait(live);
            using var batch = live.TakeResult();
            Assert.That(batch.Assets, Does.Contain(provider.Asset));
            Assert.That(provider.Releases, Is.Zero);
            using var busy = startup.StopAsync();
            yield return Wait(busy);
            Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            batch.Dispose();
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
            Assert.That(provider.Provides, Is.EqualTo(1));
            Assert.That(provider.Releases, Is.EqualTo(1));
            Assert.That(system.GetDiagnostics().DrainingHandleCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RequiredBatchProviderFailureKeepsItsCauseAndNeverPublishesSuccessfulItems()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            using var good = new DeferredBatchProvider();
            using var bad = new DeferredBatchProvider();
            using var loading = system.AcquireBatchAsync<TextAsset>(new[] { good.Key, bad.Key });
            yield return Wait(() => good.Provides == 1 && bad.Provides == 1);
            good.Complete();
            Assert.That(loading.IsDone, Is.False, "No partial delivery before every required operation finishes.");
            LogAssert.Expect(LogType.Error, new Regex(@"^System\.InvalidOperationException: required-batch-fault$"));
            LogAssert.Expect(LogType.Error, new Regex(@"^OperationException : GroupOperation failed because one of its dependencies failed\r?\nSystem\.InvalidOperationException: required-batch-fault"));
            LogAssert.Expect(LogType.Error, new Regex(@"^ResourceManagerException : Partial success in ProvideResources[\s\S]*required-batch-fault"));
            LogAssert.Expect(LogType.Error, new Regex(@"^OperationException : ChainOperation failed because dependent operation failed[\s\S]*required-batch-fault"));
            bad.Complete(new InvalidOperationException("required-batch-fault"));
            yield return Wait(loading);
            Assert.That(loading.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(loading.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(loading.Error.ToString(), Does.Contain("required-batch-fault"));
            Assert.Throws<ResourceException>(() => loading.TakeResult());
            Assert.That(system.GetDiagnostics().LeaseCount, Is.Zero);
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
            var closed = system.GetDiagnostics();
            Assert.That(closed.LeaseCount + closed.ScopeCount + closed.RequestCount + closed.PendingWorkCount + closed.DrainingHandleCount, Is.Zero);
            // Addressables 2.x defers its own failed-operation completion reference to its next update.
            yield return Wait(() => good.Releases != 0);
            yield return null;
            Assert.That(good.Releases, Is.EqualTo(1), "The official group releases the successful member exactly once.");
        }

        [UnityTest]
        public IEnumerator RequiredBatchRejectsDistinctLocationsThatCanShareAnOfficialCacheIdentity()
        {
            using var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Wait(starting);
            var system = starting.TakeResult().Resources;
            using var first = new DeferredBatchProvider();
            using var second = new DeferredBatchProvider();
            var echo = new BatchDependencyEchoProvider();
            var locator = new ResourceLocationMap(echo.ProviderId);
            var firstKey = echo.ProviderId + "/first";
            var secondKey = echo.ProviderId + "/second";
            var firstLocation = new ResourceLocationBase(echo.ProviderId, echo.ProviderId, echo.ProviderId, typeof(TextAsset),
                new CollidingDependencyLocation(first.Key));
            var secondLocation = new ResourceLocationBase(echo.ProviderId, echo.ProviderId, echo.ProviderId, typeof(TextAsset),
                new CollidingDependencyLocation(second.Key));
            Assert.That(firstLocation.DependencyHashCode, Is.EqualTo(secondLocation.DependencyHashCode));
            locator.Add(firstKey, firstLocation);
            locator.Add(secondKey, secondLocation);
            Addressables.ResourceManager.ResourceProviders.Add(echo);
            Addressables.AddResourceLocator(locator);
            try
            {
                using var request = system.AcquireBatchAsync<TextAsset>(new[] { firstKey, secondKey });
                yield return Wait(() => request.IsDone || first.Provides != 0 || second.Provides != 0);
                Assert.That(first.Provides + second.Provides, Is.Zero, "The ambiguous official batch must not submit any provider work.");
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(request.Error.Kind, Is.EqualTo(ResourceError.InvalidAddress));
                Assert.That(request.Error.Message, Does.Contain(secondKey));
                Assert.That(system.GetDiagnostics().LeaseCount, Is.Zero);
            }
            finally
            {
                Addressables.RemoveResourceLocator(locator);
                Addressables.ResourceManager.ResourceProviders.Remove(echo);
            }
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        private sealed class CollidingDependencyLocation : ResourceLocationBase, IResourceLocation
        {
            internal CollidingDependencyLocation(string key) : base(key, key, key, typeof(TextAsset)) { }
            int IResourceLocation.Hash(Type type) => 42;
        }

        private sealed class BatchDependencyEchoProvider : ResourceProviderBase
        {
            internal BatchDependencyEchoProvider() => m_ProviderId = "batch-echo/" + Guid.NewGuid().ToString("N");

            public override void Provide(ProvideHandle handle) => handle.Complete(handle.GetDependency<TextAsset>(0), true, null);
        }

        private sealed class DeferredBatchProvider : ResourceProviderBase, IDisposable
        {
            private readonly ResourceLocationMap _locations;
            private ProvideHandle _handle;
            private bool _pending;
            internal string Key { get; } = "required-batch/" + Guid.NewGuid().ToString("N");
            internal TextAsset Asset { get; } = new TextAsset("deferred required entry");
            internal int Provides { get; private set; }
            internal int Releases { get; private set; }
            internal Exception ReleaseFault { get; set; }

            internal DeferredBatchProvider()
            {
                m_ProviderId = Key;
                _locations = new ResourceLocationMap(Key);
                _locations.Add(Key, new ResourceLocationBase(Key, Key, ProviderId, typeof(TextAsset)));
                Addressables.ResourceManager.ResourceProviders.Add(this);
                Addressables.AddResourceLocator(_locations);
            }

            public override void Provide(ProvideHandle handle)
            {
                Assert.That(_pending, Is.False, "The provider must not be called twice for the shared in-flight location.");
                Provides++;
                _handle = handle;
                _pending = true;
            }

            internal void Complete(Exception error = null)
            {
                Assert.That(_pending, Is.True);
                _pending = false;
                _handle.Complete(error == null ? Asset : null, error == null, error);
            }

            public override void Release(IResourceLocation location, object asset)
            {
                Assert.That(asset, Is.SameAs(Asset));
                Releases++;
                if (ReleaseFault != null)
                {
                    throw ReleaseFault;
                }
            }

            public void Dispose()
            {
                if (_pending) Complete();
                Addressables.RemoveResourceLocator(_locations);
                Addressables.ResourceManager.ResourceProviders.Remove(this);
                UnityEngine.Object.Destroy(Asset);
            }
        }

        [UnityTest]
        public IEnumerator FailedAudioAssemblyCannotTurnIntoSuccessfulStartupOnRetry()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false, audioProjects: new[] { "invalid/project" });
            using var first = startup.StartAsync();
            yield return Wait(first);
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            var resources = startup.Resources;
            Assert.That(resources, Is.Not.Null);
            Assert.That(resources.IsAvailable && startup.OwnsResourceSystem, Is.True);
            Assert.That(startup.AudioProviders, Is.Empty);
            using var retried = startup.StartAsync();
            yield return Wait(retried);
            Assert.That(retried.Status, Is.EqualTo(ResourceRequestStatus.Failed), "A ready service is not proof that the requested startup assembly completed.");
            Assert.That(retried.Error.Kind, Is.EqualTo(first.Error.Kind));
            Assert.That(startup.Resources, Is.SameAs(resources));
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
            Assert.That(startup.OwnsResourceSystem, Is.False);
        }

        [UnityTest]
        public IEnumerator ThrowingObserverReleasesOnlyItsOwnLeaseAndDoesNotLoseAnotherConsumer()
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = startup.StartAsync();
            yield return Wait(start);
            var resources = start.TakeResult().Resources;
            using var first = resources.AcquireAsync<TextAsset>("ownership/message");
            using var independent = resources.AcquireAsync<TextAsset>("ownership/message");
            var observed = false;
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: resource-owner-observer");
            first.Completed += request =>
            {
                using var own = request.TakeResult();
                Assert.That(own.Asset.text, Is.EqualTo("official addressables content"));
                observed = true;
                throw new InvalidOperationException("resource-owner-observer");
            };
            yield return Wait(independent);
            using var retained = independent.TakeResult();
            Assert.That(observed, Is.True);
            Assert.That(retained.IsValid, Is.True);
            Assert.That(retained.Asset.text, Is.EqualTo("official addressables content"));
            Assert.That(Count(resources), Is.EqualTo(1));
            retained.Dispose();
            using var stop = startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
            Assert.That(resources.GetDiagnostics().Errors, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FailedAudioAssemblyDoesNotPublishOrRemoveAnExternalCoreBinding()
        {
            var foreign = new ForeignCoreBinding();
            CoreResourceAccess.Bind(foreign);
            try
            {
                var startup = new ResourceStartup(new ResourceConfiguration(), audioProjects: new[] { "invalid/project" });
                using var failed = startup.StartAsync();
                yield return Wait(failed);
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                var afterFailure = CoreResourceAccess.Provider;
                using var retried = startup.StartAsync();
                yield return Wait(retried);
                Assert.That(retried.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                var afterRetry = CoreResourceAccess.Provider;
                using var stop = startup.StopAsync();
                yield return Wait(stop);
                Assert.That(stop.TakeResult(), Is.True);
                Assert.That(afterFailure, Is.SameAs(foreign), "Failed consumer composition must not publish a replacement Core binding.");
                Assert.That(afterRetry, Is.SameAs(foreign));
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(foreign), "Cleanup belongs only to this startup, not the previous external binding.");
            }
            finally { CoreResourceAccess.Unbind(foreign); }
        }

        private sealed class ForeignCoreBinding : ICoreResourceProvider
        {
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, UnityEngine.SceneManagement.LoadSceneMode mode) => throw new NotSupportedException();
        }

        private static IEnumerator Wait<T>(ResourceRequest<T> request) => Wait(() => request.IsDone);

        private static IEnumerator Wait(Func<bool> complete)
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!complete() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(complete(), Is.True, "The operation did not settle within 30 seconds.");
        }

        private static int Count(ResourceSystem system) => system.GetDiagnostics().LeaseCount;
    }

    [Category("YL.Resource.Addressables")]
    public sealed class ResourceAddressablesFastBatchTests
    {
        [UnityTearDown]
        public IEnumerator Close()
        {
            if (Application.isPlaying)
            {
                yield return new ExitPlayMode();
            }
        }

        [UnityTest]
        public IEnumerator RequiredBatchMergesSeparateFastModeLocationsForTheSameEntry()
        {
            if (!Application.isPlaying)
            {
                ResourceAddressablesTestContent.BuildFastMode();
                yield return new EnterPlayMode();
            }

            using var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = startup.StartAsync();
            yield return start;
            var system = start.TakeResult().Resources;
            var locator = Addressables.ResourceLocators.Single(value => value.GetType().Name == "AddressableAssetSettingsLocator");
            const string Address = "ownership/message";
            const string Label = "ownership/batch-texts";
            Assert.That(locator.Locate(Address, typeof(TextAsset), out var byAddress), Is.True);
            Assert.That(locator.Locate(Label, typeof(TextAsset), out var byLabel), Is.True);
            var original = byAddress.Single();
            var alias = byLabel.Single(value => value.PrimaryKey == Address);
            Assert.That(original.ProviderId, Is.EqualTo(typeof(AssetDatabaseProvider).FullName));
            Assert.That(alias, Is.Not.SameAs(original), "Fast Mode allocates separately for the address and label lookups.");
            Assert.That(alias.InternalId, Is.EqualTo(original.InternalId));

            using var request = system.AcquireBatchAsync<TextAsset>(new[] { Address, Label });
            yield return request;
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            using (var batch = request.TakeResult())
            {
                CollectionAssert.AreEquivalent(new[] { "official addressables content", "second required entry" },
                    batch.Assets.Select(value => value.text));
                Assert.That(system.GetDiagnostics().LeaseCount, Is.EqualTo(1));
            }
            using var stop = startup.StopAsync();
            yield return stop;
            Assert.That(stop.TakeResult(), Is.True);
        }
    }

    public static class ResourceAddressablesTestContent
    {
        public const string Root = "Assets/YLAddressablesOwnershipContent";

        public static void Build(bool includeImages = false)
        {
            BuildContent(includeImages, true);
        }

        internal static void BuildFastMode()
        {
            BuildContent(false, true, fastMode: true);
        }

        public static void BuildForConsumers()
        {
            BuildContent(false, false);
        }

        public static void BuildForSessions()
        {
            BuildContent(true, true, restoreSessionAnchor: true);
        }

        private static void BuildContent(bool includeImages, bool includeAmbiguous, bool restoreSessionAnchor = false, bool fastMode = false)
        {
            const string PreparationScene = "Assets/YLTestingProfile/Empty.unity";
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string owner = Path.Combine(project, ".yl-owner.json");
            if (!File.Exists(owner) || (string)JObject.Parse(File.ReadAllText(owner))["kind"] != "Host")
                throw new InvalidOperationException("Addressables test content may only be built in a tool-owned host.");
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Preserve the dirty owned-host scene rather than saving it implicitly.");
            var prepared = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(PreparationScene);
            var restorePreparation = (!includeAmbiguous || restoreSessionAnchor) && prepared.IsValid() && prepared.isLoaded && prepared.rootCount == 0;
            if (AssetDatabase.IsValidFolder(Root))
            {
                if (!File.Exists(Root + "/.yl-owned")) throw new InvalidOperationException("Preserve the unknown fixture output.");
                AssetDatabase.DeleteAsset(Root);
            }
            AssetDatabase.CreateFolder("Assets", "YLAddressablesOwnershipContent");
            File.WriteAllText(Root + "/.yl-owned", "YL.Resource.Addressables.NativeContent");
            var bootstrap = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string bootstrapPath = Root + "/Bootstrap.unity";
            EditorSceneManager.SaveScene(bootstrap, bootstrapPath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(bootstrapPath, true) };
            var settings = AddressableAssetSettings.Create(Root, "Settings", true, true);
            settings.DisableCatalogUpdateOnStartup = true;
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            if (!AssetDatabase.IsValidFolder("Assets/AddressableAssetsData"))
                AssetDatabase.CreateFolder("Assets", "AddressableAssetsData");
            AddressableAssetSettingsDefaultObject.Settings = settings;
            Add(settings, "Message", "ownership/message", "official addressables content");
            Add(settings, "BatchSecond", "ownership/batch-second", "second required entry");
            settings.AddLabel("ownership/batch-texts");
            foreach (var name in new[] { "Message", "BatchSecond" })
                settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(Root + "/" + name + ".txt")).SetLabel("ownership/batch-texts", true);
            if (includeAmbiguous)
            {
                Add(settings, "One", "ownership/ambiguous", "one");
                Add(settings, "Two", "ownership/ambiguous", "two");
                Add(settings, "Aliased", "ownership/aliased", "same entry uses both key roles");
                settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(Root + "/Aliased.txt"))
                    .SetLabel("ownership/aliased", true, true);
            }
            var prefab = new GameObject("OwnershipPrefab");
            prefab.SetActive(false);
            prefab.AddComponent<YLFramework.Tests.InstanceLifecycleProbe>();
            var equipmentProbe = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("YLFramework.Tests.Extensions.Domain.AddressablesEquipmentConsumerProbe", false))
                .SingleOrDefault(type => type != null);
            if (equipmentProbe != null) prefab.AddComponent(equipmentProbe);
            prefab.SetActive(true);
            string prefabPath = Root + "/Ownership.prefab";
            PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
            UnityEngine.Object.DestroyImmediate(prefab);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(prefabPath), settings.DefaultGroup).SetAddress("ownership/prefab");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            string scenePath = Root + "/OwnedScene.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            EditorSceneManager.CloseScene(scene, true);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(scenePath), settings.DefaultGroup).SetAddress("ownership/scene");
            string audioPath = Root + "/Sample.wav";
            using (var file = File.Create(audioPath))
            using (var writer = new BinaryWriter(file))
            {
                const int samples = 2205;
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(22050); writer.Write(44100);
                writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(samples * 2);
                for (int i = 0; i < samples; i++) writer.Write((short)0);
            }
            AssetDatabase.ImportAsset(audioPath);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(audioPath), settings.DefaultGroup).SetAddress("ownership/audio");
            if (includeImages)
            {
                AddAnimatedPrefab(settings);
                EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
                AddAtlas(settings, "Manual", "ownership/atlas", AddAtlasSprite("Pixels", Color.cyan));
                AddAtlas(settings, "Colors", "ownership/color-atlas", AddAtlasSprite("Red", Color.red), AddAtlasSprite("Blue", Color.blue));
            }
            if (bootstrap.rootCount != 0) throw new InvalidOperationException("The generated bootstrap was unexpectedly modified.");
            EditorSceneManager.SaveScene(bootstrap, bootstrapPath);
            if (fastMode)
            {
                settings.ActivePlayModeDataBuilderIndex = settings.DataBuilders.IndexOf(settings.DataBuilders.OfType<BuildScriptFastMode>().Single());
            }
            else
            {
                var builder = settings.DataBuilders.OfType<BuildScriptPackedMode>().Single();
                var result = builder.BuildData<AddressablesPlayerBuildResult>(new AddressablesDataBuilderInput(settings));
                if (!string.IsNullOrEmpty(result.Error)) throw new InvalidOperationException(result.Error);
                settings.ActivePlayModeDataBuilderIndex = settings.DataBuilders.FindIndex(value => value is BuildScriptPackedPlayMode);
            }
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
            if (restorePreparation)
            {
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (UnityEngine.SceneManagement.SceneManager.sceneCount != 1 || active.path != bootstrapPath || active.isDirty || active.rootCount != 0)
                    throw new InvalidOperationException("Do not replace an unexpected scene after the fixture build.");
                var restored = EditorSceneManager.OpenScene(PreparationScene, OpenSceneMode.Single);
                if (restored.isDirty || restored.rootCount != 0)
                    throw new InvalidOperationException("The restored owned preparation scene is not empty and clean.");
            }
        }

        private static void AddAnimatedPrefab(AddressableAssetSettings settings)
        {
            const string Parameter = "OwnershipReady";
            var controllerPath = Root + "/OwnershipAnimator.controller";
            var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            controller.AddParameter(Parameter, AnimatorControllerParameterType.Bool);
            controller.layers[0].stateMachine.AddState("Idle");
            AssetDatabase.SaveAssetIfDirty(controller);
            var source = new GameObject("AnimatedPrefab");
            var path = Root + "/Animated.prefab";
            try
            {
                source.AddComponent<Animator>().runtimeAnimatorController = controller;
                PrefabUtility.SaveAsPrefabAsset(source, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(controllerPath), settings.DefaultGroup).SetAddress("ownership/animator");
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), settings.DefaultGroup).SetAddress("ownership/animated-prefab");
        }

        private static void Add(AddressableAssetSettings settings, string file, string key, string value)
        {
            string path = Root + "/" + file + ".txt";
            File.WriteAllText(path, value);
            AssetDatabase.ImportAsset(path);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), settings.DefaultGroup).SetAddress(key);
        }

        private static Texture2D AddAtlasSprite(string name, Color color)
        {
            var path = Root + "/" + name + ".png";
            var pixels = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                pixels.SetPixels(Enumerable.Repeat(color, 64).ToArray());
                pixels.Apply();
                File.WriteAllBytes(path, pixels.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void AddAtlas(AddressableAssetSettings settings, string name, string address, params Texture2D[] sources)
        {
            var path = Root + "/" + name + ".spriteatlasv2";
            var authoring = new SpriteAtlasAsset();
            try
            {
                authoring.Add(sources);
                var platform = authoring.GetPlatformSettings("Standalone");
                platform.overridden = true;
                platform.format = TextureImporterFormat.RGBA32;
                platform.textureCompression = TextureImporterCompression.Uncompressed;
                platform.crunchedCompression = false;
                authoring.SetPlatformSettings(platform);
                SpriteAtlasAsset.Save(authoring, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(authoring); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(path);
            importer.includeInBuild = false;
            importer.SaveAndReimport();
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (!atlas || atlas.IsIncludeInBuild() || atlas.spriteCount != sources.Length) throw new InvalidDataException("The official Atlas importer did not pack every fixture sprite: " + path);
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), settings.DefaultGroup).SetAddress(address);
        }
    }
}
#endif
