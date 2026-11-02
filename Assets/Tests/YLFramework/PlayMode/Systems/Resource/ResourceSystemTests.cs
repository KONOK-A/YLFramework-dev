using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Resource
{
    public class ResourceSystemTests
    {
        private TextAsset _asset;
        private ControlledSource _source;
        private ResourceConfiguration _config;
        private readonly ResourceAddress _address = new("fixture81", "text");
        private readonly ResourceAddress _alias = new("fixture81", "alias");

        internal sealed class ControlledOperation : ResourceSourceOperation
        {
            public float Fraction;
            public override float Progress => IsDone ? 1 : Fraction;
            public void Finish(Object value, Exception error = null) => Complete(value, error);
        }

        internal sealed class ControlledSource : IResourceSource
        {
            public ResourceSourceId Id
            {
                get;
            }
            public bool SupportsSynchronousLoad => Synchronous;
            public bool Synchronous = true;
            public bool Immediate;
            public bool ThrowOnStart;
            public Action OnValidate;
            public Action OnLoad;
            public Object Asset;
            public int Loads;
            public int Releases;
            public readonly List<ControlledOperation> Operations = new();
            public ControlledSource(string id) => Id = new ResourceSourceId(id);
            public void Validate(ResourceCatalogEntry entry) => OnValidate?.Invoke();
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type)
            {
                Loads++;
                OnLoad?.Invoke();
                if (ThrowOnStart)
                {
                    throw new InvalidOperationException("controlled-load-start");
                }

                return new ResourceSourceResult(Asset);
            }
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type)
            {
                Loads++;
                OnLoad?.Invoke();
                if (ThrowOnStart)
                {
                    throw new InvalidOperationException("controlled-load-start");
                }

                var operation = new ControlledOperation();
                Operations.Add(operation);
                if (Immediate)
                {
                    operation.Finish(Asset);
                }

                return operation;
            }
            public void Release(Object value) => Releases++;
        }

        private sealed class CancelableOperation : ResourceSourceOperation
        {
            public override bool SupportsCancellation => true;
            public override void Cancel()
            {
            }
            internal void Finish(Object asset = null, Exception error = null) => Complete(asset, error);
        }

        private sealed class CancelableSource : IResourceSource
        {
            public ResourceSourceId Id => new("cancelable");
            public bool SupportsSynchronousLoad => false;
            internal readonly List<CancelableOperation> Operations = new();
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => throw new NotSupportedException();
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type)
            {
                var operation = new CancelableOperation();
                Operations.Add(operation);
                return operation;
            }
            public void Release(Object asset) => new TextureResourceSource(true).Release(asset);
        }

        [UnityTest]
        public IEnumerator LastCancellationDrainsBeforeSameIdentityConsumerRestartsItsOwnProducer()
        {
            var source = new CancelableSource();
            var key = new ResourceAddress("cancel83", "image");
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("cancel83", new[]
            {
                new ResourceCatalogEntry("image", key, new ResourceLocation(source.Id, "controlled-native-drain-window", typeof(Texture2D)))
            }) }, new IResourceSource[] { source }));
            var system = initialized.TakeResult();
            using var canceled = system.AcquireAsync<Texture2D>(key);
            canceled.Cancel();
            using var replacement = system.AcquireAsync<Texture2D>(key);
            Assert.That(source.Operations.Count, Is.EqualTo(1), "No overlapping IO while the abandoned producer drains.");
            source.Operations[0].Finish(error: new OperationCanceledException("old private IO ended"));
            Assert.That(replacement.Status, Is.EqualTo(ResourceRequestStatus.Pending), "A new consumer cannot inherit an earlier consumer's Abort failure.");
            Assert.That(source.Operations.Count, Is.EqualTo(2));
            var texture = new Texture2D(2, 3);
            try
            {
                source.Operations[1].Finish(texture);
                using var lease = replacement.TakeResult();
                Assert.That(lease.Asset, Is.SameAs(texture));
                lease.Dispose();
                yield return null;
                Assert.That(texture == null, Is.True);
            }
            finally
            {
                if (texture)
                {
                    Object.DestroyImmediate(texture);
                }
            }
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void OptionalGroupFailurePreservesItsCauseAndHealthyIndependentLease()
        {
            _source.Immediate = true;
            var failed = new ControlledSource("optional83") { ThrowOnStart = true };
            _config = Configure(_source, failed);
            _config.Catalogs[0].Groups = new[] { new ResourceCatalogGroup("optional83", new[]
            {
                new ResourceCatalogGroupMember("text-id", true), new ResourceCatalogGroupMember("other-id", false)
            }) };
            var system = Initialize();
            using var request = system.AcquireGroupAsync("fixture81", "optional83");
            using var group = request.TakeResult();
            Assert.That(group.Items.Count, Is.EqualTo(1));
            Assert.That(group.Items[0].Asset, Is.SameAs(_asset));
            Assert.That(group.Errors.Count, Is.EqualTo(1));
            Assert.That(group.Errors[0].InnerException, Is.InstanceOf<InvalidOperationException>());
            Assert.That(_source.Releases, Is.Zero);
            group.Dispose();
            Assert.That(_source.Releases, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [SetUp]
        public void SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another resource authority.");
            _asset = new TextAsset("resource81-owned-fixture");
            _source = new ControlledSource("controlled81") { Asset = _asset };
            _config = Configure(_source);
        }

        private ResourceConfiguration Configure(ControlledSource first, ControlledSource second = null)
        {
            var entries = new List<ResourceCatalogEntry>
            {
                new("text-id", _address, new ResourceLocation(first.Id, "Exact/Locator", typeof(TextAsset)), new[] { _alias })
            };
            if (second != null)
            {
                entries.Add(new ResourceCatalogEntry("other-id", new ResourceAddress("fixture81", "other"),
                new ResourceLocation(second.Id, "Exact/Locator", typeof(TextAsset))));
            }

            return new ResourceConfiguration(new[] { new ResourceCatalog("fixture81", entries) },
                second == null ? new IResourceSource[] { first } : new IResourceSource[] { first, second });
        }

        private ResourceSystem Initialize()
        {
            using var request = ResourceSystem.InitializeAsync(_config);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            return request.TakeResult();
        }

        private static void SessionEvent(string method) => typeof(ResourceSystem)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

        [TearDown]
        public void TearDown()
        {
            SessionEvent("EndSession");
            foreach (var operation in _source.Operations)
            {
                if (!operation.IsDone)
                {
                    operation.Finish(_asset);
                }
            }

            SessionEvent("ResetSession");
            Object.DestroyImmediate(_asset);
        }

        [Test]
        public void FailedInitializationCanRetryWithoutHalfReadyOrSecondAuthority()
        {
            _source.OnValidate = () => throw new InvalidOperationException("controlled-config");
            using (var failed = ResourceSystem.InitializeAsync(_config))
            {
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(failed.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
                Assert.That(failed.Error.InnerException, Is.InstanceOf<InvalidOperationException>());
                Assert.That(ResourceSystem.Current, Is.Null);
            }
            _source.OnValidate = null;
            var system = Initialize();
            using var same = ResourceSystem.InitializeAsync(_config);
            Assert.That(same.TakeResult(), Is.SameAs(system));
            using var conflict = ResourceSystem.InitializeAsync(Configure(_source));
            Assert.That(conflict.Error.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
            Assert.That(ResourceSystem.Current, Is.SameAs(system));
        }

        [Test]
        public void ReentrantInitializationJoinsTheSameConfiguration()
        {
            ResourceRequest<ResourceSystem> joined = null;
            _source.OnValidate = () =>
            {
                _source.OnValidate = null;
                joined = ResourceSystem.InitializeAsync(_config);
                Assert.That(joined.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            };
            var system = Initialize();
            using (joined)
            {
                Assert.That(joined.TakeResult(), Is.SameAs(system));
            }
        }

        [Test]
        public void AliasesShareProductionWhileRequestsAndLeasesAreIndependent()
        {
            var system = Initialize();
            using var first = system.AcquireAsync<TextAsset>(_address);
            using var second = system.AcquireAsync<TextAsset>(_alias);
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(_source.Loads, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().WaiterCount, Is.EqualTo(2));
            _source.Operations[0].Finish(_asset);
            using var a = first.TakeResult();
            using var b = second.TakeResult();
            Assert.That(a, Is.Not.SameAs(b));
            Assert.That(a.Asset, Is.SameAs(b.Asset));
            a.Dispose();
            Assert.That(b.Asset.text, Is.EqualTo("resource81-owned-fixture"));
            Assert.That(_source.Releases, Is.Zero);
            b.Dispose();
            Assert.That(_source.Releases, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void CancelingOneWaiterDoesNotHarmTheOtherOrDuplicateSynchronousProduction()
        {
            var system = Initialize();
            using var first = system.AcquireAsync<TextAsset>(_address);
            using var second = system.AcquireAsync<TextAsset>(_address);
            first.Cancel();
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<TextAsset>(_address)).Kind, Is.EqualTo(ResourceError.InProgress));
            Assert.That(_source.Loads, Is.EqualTo(1));
            _source.Operations[0].Finish(_asset);
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            using var lease = second.TakeResult();
            Assert.That(lease.Asset, Is.SameAs(_asset));
        }

        [Test]
        public void LastWaiterExitKeepsCleanupOwnerAndGracefulShutdownDrainsNativeWork()
        {
            var system = Initialize();
            using var request = system.AcquireAsync<TextAsset>(_address);
            request.Dispose();
            using var shutdown = system.ShutdownAsync();
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            Assert.That(system.State, Is.EqualTo(ResourceSystemState.ShuttingDown));
            _source.Operations[0].Finish(_asset);
            Assert.That(shutdown.TakeResult(), Is.True);
            Assert.That(_source.Releases, Is.EqualTo(1));
            Assert.That(_source.Operations[0].IsDisposed, Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [Test]
        public void ShutdownBusyRestoresAvailabilityForLiveAndUnclaimedLeases()
        {
            var system = Initialize();
            _source.Immediate = true;
            using var request = system.AcquireAsync<TextAsset>(_address);
            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            Assert.That(system.IsAvailable, Is.True);
            using var lease = request.TakeResult();
            request.Dispose();
            using (var other = system.Acquire<TextAsset>(_address))
            {
                Assert.That(other.Asset, Is.SameAs(_asset));
            }

            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            lease.Dispose();
            using var closed = system.ShutdownAsync();
            Assert.That(closed.TakeResult(), Is.True);
        }

        [Test]
        public void AllConsumersArePinnedAndTerminalBeforeTheFirstObserverRuns()
        {
            var system = Initialize();
            using var first = system.AcquireAsync<TextAsset>(_address);
            using var second = system.AcquireAsync<TextAsset>(_address);
            var observed = false;
            first.Completed += value =>
            {
                Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(2));
                value.Dispose();
                throw new InvalidOperationException("resource-system-observer");
            };
            second.Completed += value =>
            {
                observed = true;
                Assert.That(value.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            };
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: resource-system-observer");
            _source.Operations[0].Finish(_asset);
            Assert.That(observed, Is.True);
            using var lease = second.TakeResult();
            Assert.That(lease.Asset, Is.SameAs(_asset));
            Assert.That(system.GetDiagnosticsSnapshot().ProducerCount, Is.Zero);
        }

        [Test]
        public void ScopeClaimsOwnedResultsAndOnlyStopsItsOwnWaitersAndPins()
        {
            var system = Initialize();
            using var scope = system.CreateScope();
            using var outside = system.AcquireAsync<TextAsset>(_address);
            var inside = scope.AcquireAsync<TextAsset>(_address);
            _source.Operations[0].Finish(_asset);
            var taken = scope.TakeResult(inside);
            using var separate = outside.TakeResult();
            scope.Dispose();
            Assert.That(taken.IsDisposed, Is.True);
            Assert.That(separate.Asset, Is.SameAs(_asset));
            using var newScope = system.CreateScope();
            var own = newScope.Acquire<TextAsset>(_address);
            Assert.That(own.Asset, Is.SameAs(_asset));
        }

        [Test]
        public void DisposingPendingScopeDoesNotCancelOtherConsumers()
        {
            var system = Initialize();
            using var scope = system.CreateScope();
            var inside = scope.AcquireAsync<TextAsset>(_address);
            using var outside = system.AcquireAsync<TextAsset>(_address);
            scope.Dispose();
            _source.Operations[0].Finish(_asset);
            Assert.That(inside.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            using var lease = outside.TakeResult();
            Assert.That(lease.Asset, Is.SameAs(_asset));
        }

        [Test]
        public void FailureWrongTypeAndThrownStartHaveCausesAndReleaseTheirOwnResult()
        {
            var system = Initialize();
            using (var wrongRequest = system.AcquireAsync<TextAsset>(_address))
            {
                var wrong = new Texture2D(1, 1);
                _source.Operations[0].Finish(wrong);
                Assert.That(wrongRequest.Error.Kind, Is.EqualTo(ResourceError.TypeMismatch));
                Assert.That(_source.Releases, Is.EqualTo(1));
                Object.DestroyImmediate(wrong);
            }
            var cause = new InvalidOperationException("controlled-native-failure");
            using (var failed = system.AcquireAsync<TextAsset>(_address))
            {
                _source.Operations[1].Fraction = 0.5f;
                Assert.That(failed.Progress, Is.EqualTo(0.5f));
                _source.Operations[1].Finish(null, cause);
                Assert.That(failed.Error.InnerException, Is.SameAs(cause));
                Assert.That(failed.Progress, Is.EqualTo(1), "Completion must not reset the observed producer progress to zero.");
            }
            _source.ThrowOnStart = true;
            using var thrown = system.AcquireAsync<TextAsset>(_address);
            Assert.That(thrown.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(thrown.Error.Address, Is.EqualTo(_address));
            Assert.That(thrown.Error.Source, Is.EqualTo(_source.Id));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void AddressTypeAndSourceValidationNeverUseFuzzyFallback()
        {
            var other = new ControlledSource("second81") { Asset = _asset };
            _config = Configure(_source, other);
            var system = Initialize();
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<GameObject>(_address)).Kind, Is.EqualTo(ResourceError.TypeMismatch));
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<TextAsset>(default)).Kind, Is.EqualTo(ResourceError.InvalidAddress));
            using var missing = system.AcquireAsync<TextAsset>(new ResourceAddress("fixture81", "Text"));
            Assert.That(missing.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            using var a = system.Acquire<TextAsset>(_address);
            using var b = system.Acquire<TextAsset>(new ResourceAddress("fixture81", "other"));
            Assert.That(_source.Loads, Is.EqualTo(1));
            Assert.That(other.Loads, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.EqualTo(2));
        }

        [Test]
        public void SessionExitInvalidatesOldHandlesAndBlocksNewAuthorityUntilLateCleanup()
        {
            var system = Initialize();
            using var pending = system.AcquireAsync<TextAsset>(_address);
            SessionEvent("EndSession");
            Assert.That(pending.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(ResourceSystem.Current, Is.Null);
            SessionEvent("ResetSession");
            using (var blocked = ResourceSystem.InitializeAsync(_config))
            {
                Assert.That(blocked.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            _source.Operations[0].Finish(_asset);
            Assert.That(_source.Releases, Is.EqualTo(1));
            var newer = Initialize();
            Assert.That(newer, Is.Not.SameAs(system));
            pending.Dispose();
            using var lease = newer.Acquire<TextAsset>(_address);
            SessionEvent("EndSession");
            Assert.That(lease.IsValid, Is.False);
            Assert.Throws<ResourceException>(() => _ = lease.Asset);
            lease.Dispose();
            Assert.That(newer.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void SynchronousAndImmediateAsyncReentrancyCannotReviveExitedSession()
        {
            var system = Initialize();
            _source.OnLoad = () => SessionEvent("EndSession");
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<TextAsset>(_address)).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(_source.Releases, Is.EqualTo(1));
            SessionEvent("ResetSession");
            system = Initialize();
            _source.Immediate = true;
            using var request = system.AcquireAsync<TextAsset>(_address);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(_source.Releases, Is.EqualTo(2));
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        private sealed class ContractProvider : ICoreResourceProvider
        {
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode) => throw new NotSupportedException();
        }

        [Test]
        public void CoreBindingIsExplicitAndOldUnbindCannotClearReplacement()
        {
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            var first = new ContractProvider();
            var second = new ContractProvider();
            try
            {
                CoreResourceAccess.Bind(first);
                CoreResourceAccess.Bind(second);
                Assert.That(CoreResourceAccess.Unbind(first), Is.False);
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(second));
                Assert.That(CoreResourceAccess.Unbind(second), Is.True);
                Assert.That(CoreResourceAccess.Provider, Is.Null);
            }
            finally
            {
                CoreResourceAccess.Unbind(second);
            }
        }

        [Test]
        public void InitializationRejectsNullDuplicateProjectsAndUnknownSources()
        {
            using (var invalid = ResourceSystem.InitializeAsync(null))
            {
                Assert.That(invalid.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            }

            using (var invalid = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { _config.Catalogs[0], _config.Catalogs[0] })))
            {
                Assert.That(invalid.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            }

            using (var invalid = ResourceSystem.InitializeAsync(new ResourceConfiguration(_config.Catalogs)))
            {
                Assert.That(invalid.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            }

            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(Initialize().IsAvailable, Is.True);
        }

        [Test]
        public void UnsupportedColdSyncDoesNotBlockSyncUseOfAnAsyncReadyAsset()
        {
            var system = Initialize();
            _source.Synchronous = false;
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<TextAsset>(_address)).Kind, Is.EqualTo(ResourceError.Unsupported));
            Assert.That(_source.Loads, Is.Zero);
            using var pending = system.AcquireAsync<TextAsset>(_address);
            _source.Operations[0].Finish(_asset);
            using var ready = system.Acquire<TextAsset>(_address);
            Assert.That(ready.Asset, Is.SameAs(_asset));
            Assert.That(_source.Loads, Is.EqualTo(1));
        }

        [Test]
        public void ImmediateScopeDisposalDuringSourceStartCannotLeakUnreturnedResult()
        {
            var system = Initialize();
            using var scope = system.CreateScope();
            _source.Immediate = true;
            _source.OnLoad = scope.Dispose;
            Assert.Throws<ObjectDisposedException>(() => scope.AcquireAsync<TextAsset>(_address));
            Assert.That(_source.Releases, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void ExternallyDestroyedObjectIsRejectedWithoutReleasingOtherConsumers()
        {
            var system = Initialize();
            using var first = system.Acquire<TextAsset>(_address);
            using var second = system.Acquire<TextAsset>(_address);
            Object.DestroyImmediate(_asset);
            Assert.That(first.IsValid, Is.False);
            Assert.That(Assert.Throws<ResourceException>(() => _ = first.Asset).Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<TextAsset>(_address)).Kind, Is.EqualTo(ResourceError.LoadFailed));
            first.Dispose();
            Assert.That(_source.Releases, Is.Zero);
            second.Dispose();
            Assert.That(_source.Releases, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [Test]
        public void SessionExitFreezesAllPendingConsumersBeforeReentrantCancellation()
        {
            var system = Initialize();
            using var first = system.AcquireAsync<TextAsset>(_address);
            using var second = system.AcquireAsync<TextAsset>(_address);
            var firstObserved = ResourceRequestStatus.Pending;
            var secondObserved = ResourceRequestStatus.Pending;
            first.Completed += _ =>
            {
                firstObserved = second.Status;
                second.Cancel();
            };
            second.Completed += _ =>
            {
                secondObserved = first.Status;
                first.Cancel();
            };
            SessionEvent("EndSession");
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(firstObserved, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(secondObserved, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(system.GetDiagnosticsSnapshot().WaiterCount, Is.Zero);
            _source.Operations[0].Finish(_asset);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        [Test]
        public void SessionExitScopeDisposalCannotTurnAnotherInvalidatedWaiterIntoCanceled()
        {
            var system = Initialize();
            using var scope = system.CreateScope();
            var first = scope.AcquireAsync<TextAsset>(_address);
            using var second = system.AcquireAsync<TextAsset>(_address);
            first.Completed += _ => scope.Dispose();
            second.Completed += _ => scope.Dispose();
            SessionEvent("EndSession");
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            _source.Operations[0].Finish(_asset);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(_source.Releases, Is.EqualTo(1));
        }

        private sealed class EqualOwner : IDisposable
        {
            public int Releases;
            public void Dispose() => Releases++;
            public override bool Equals(object other) => other is EqualOwner;
            public override int GetHashCode() => 1;
        }

        private sealed class FailingOwner : IDisposable
        {
            internal readonly Exception Error = new InvalidOperationException("scope-cleanup-failure");
            internal int Releases;
            public void Dispose()
            {
                Releases++;
                throw Error;
            }
        }

        [Test]
        public void ScopeCleanupPreservesEveryFailureReleasesHealthyLeasesAndNeverReplays()
        {
            var system = Initialize();
            using var external = system.Acquire<TextAsset>(_address);
            using var scope = system.CreateScope();
            var first = scope.Own(new FailingOwner());
            var second = scope.Own(new FailingOwner());
            var owned = scope.Acquire<TextAsset>(_address);
            var errors = Assert.Throws<AggregateException>(() => scope.Dispose());
            CollectionAssert.AreEquivalent(new[] { first.Error, second.Error }, errors.InnerExceptions);
            Assert.That(owned.IsValid, Is.False);
            Assert.That(external.Asset.text, Is.EqualTo("resource81-owned-fixture"));
            Assert.That(_source.Releases, Is.Zero);
            Assert.That(scope.IsDisposed, Is.True);
            scope.Dispose();
            Assert.That(first.Releases, Is.EqualTo(1));
            Assert.That(second.Releases, Is.EqualTo(1));
            external.Dispose();
            Assert.That(_source.Releases, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            using var shutdown = system.ShutdownAsync();
            Assert.That(shutdown.TakeResult(), Is.True);
        }

        [Test]
        public void ScopeOwnUsesReferenceIdentityRatherThanDisposableValueEquality()
        {
            var system = Initialize();
            using var scope = system.CreateScope();
            var first = new EqualOwner();
            var second = new EqualOwner();
            scope.Own(first);
            scope.Own(second);
            scope.Own(first);
            scope.Dispose();
            Assert.That(first.Releases, Is.EqualTo(1));
            Assert.That(second.Releases, Is.EqualTo(1));
        }
    }
}
