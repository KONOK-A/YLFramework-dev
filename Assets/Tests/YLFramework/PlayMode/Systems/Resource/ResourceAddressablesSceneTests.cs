#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Resource.Addressables.Runtime")]
    public sealed class ResourceAddressablesSceneTests
    {
        private const string SceneAddress = "ownership/scene";
        private const string NeutralPath = "Assets/Tests/YLFramework/Fixtures/CommonSystem/Scenes/CommonNeutral.unity";
        private ResourceStartup _startup;
        private ResourceSystem _system;
        private ResourceLocationMap _extraLocator;
        private string _entryScenePath;
        private SceneDependencyGate _dependencyGate;
        private readonly List<Scene> _neutralScenes = new();
        private readonly List<AsyncOperationHandle<SceneInstance>> _externalScenes = new();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Tests must not take over an existing startup.");
            _entryScenePath = SceneManager.GetActiveScene().path;
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return Wait(() => start.IsDone);
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
            _system = start.TakeResult().Resources;

            using var locations = new LocationQuery(SceneAddress);
            yield return locations.Handle;
            Assert.That(locations.Handle.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
            Assert.That(locations.Handle.Result.Count, Is.EqualTo(1), "The official built-content fixture must contain one scene address.");
            Assert.That(locations.Handle.Result[0].HasDependencies, Is.True, "Scene tests require a real built Bundle, not an Editor-only locator.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            ResourceEventHistory.SetRecording(false);
            ResourceEventHistory.Clear();
            _dependencyGate?.Open();
            if (_system != null)
            {
                SetUnloader(null);
                _system.SceneActivator = null;
                _neutralScenes.Add(SceneManager.CreateScene("YL.SceneTest.Cleanup." + Guid.NewGuid().ToString("N")));
                foreach (var lease in Owners().ToArray())
                {
                    using var close = lease.UnloadAsync();
                    yield return Wait(() => close.IsDone);
                    Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), close.Error?.ToString());
                    Assert.That(lease.IsSettled, Is.True);
                }
            }
            foreach (var external in _externalScenes)
            {
                if (!external.IsValid()) continue;
                if (external.Status != AsyncOperationStatus.Succeeded || !external.Result.Scene.IsValid())
                {
                    Addressables.Release(external);
                    continue;
                }
                var unloading = Addressables.UnloadSceneAsync(external, autoReleaseHandle: false);
                yield return Wait(() => unloading.IsDone);
                Assert.That(unloading.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), unloading.OperationException?.ToString());
                Addressables.Release(unloading);
            }
            _externalScenes.Clear();
            if (_startup != null)
            {
                using var stop = _startup.StopAsync();
                yield return Wait(() => stop.IsDone);
                Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            }
            if (_extraLocator != null)
            {
                Addressables.RemoveResourceLocator(_extraLocator);
                _extraLocator = null;
            }
            if (_dependencyGate != null)
            {
                Addressables.ResourceManager.ResourceProviders.Remove(_dependencyGate);
                _dependencyGate = null;
            }
            var restorePath = string.IsNullOrEmpty(_entryScenePath) ? NeutralPath : _entryScenePath;
            var restored = SceneManager.GetSceneByPath(restorePath);
            if (!restored.IsValid() || !restored.isLoaded)
            {
                // UTF's injected scene contains a tests controller. Reloading it after Single creates
                // a second runner and can overwrite the active run's result with an empty report.
                if (System.IO.Path.GetFileName(restorePath).StartsWith("InitTestScene", StringComparison.Ordinal))
                    restorePath = NeutralPath;
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(restorePath, new LoadSceneParameters(LoadSceneMode.Additive));
                restored = SceneManager.GetSceneByPath(restorePath);
            }
            SceneManager.SetActiveScene(restored);
            foreach (var scene in _neutralScenes)
            {
                if (scene != restored && scene.IsValid() && scene.isLoaded && LoadedSceneCount() > 1)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }
            _neutralScenes.Clear();
            _system = null;
            _startup = null;
        }

        [UnityTest]
        public IEnumerator ScopeSceneLoad_ReturnsActivatedScene()
        {
            using var scope = _system.CreateScope("scene scope");
            using var load = scope.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), load.Error?.ToString());
            var scene = load.TakeResult();
            Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
            using var close = scope.CloseAsync();
            yield return Wait(() => close.IsDone);
            Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), close.Error?.ToString());
            Assert.That(scene.IsValid(), Is.False);
        }

        [UnityTest]
        public IEnumerator CompletedScopedSceneUnloadedBeforeTake_RefusesBorrowAndForgetsItsLease()
        {
            using var scope = _system.CreateScope("unloaded before scoped claim");
            using var load = scope.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), load.Error?.ToString());
            var scene = Owners().Single().Scene;
            var native = SceneManager.UnloadSceneAsync(scene);
            Assert.That(native, Is.Not.Null);
            yield return native;
            yield return Wait(() => !Owners().Any());
            Assert.That(scope.IsDisposed, Is.False);
            Assert.Throws<ResourceException>(() => load.TakeResult());
            var owned = (ICollection<IResourceLease>)typeof(ResourceScope)
                .GetField("_leases", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(scope);
            Assert.That(owned, Is.Empty, "A long-lived scope must forget a settled scene owner.");
        }

        [UnityTest]
        public IEnumerator ScopeClosesItsDeferredGateBeforeUnloadingAnEarlierOwnedScene()
        {
            using var scope = _system.CreateScope("two owned scene identities");
            using var loadingText = _system.AcquireAsync<TextAsset>("ownership/message");
            yield return Wait(() => loadingText.IsDone);
            var text = scope.Own(loadingText.TakeResult());
            using var creating = scope.InstantiateAsync("ownership/prefab");
            yield return Wait(() => creating.IsDone);
            var instance = creating.TakeResult();
            using var earlier = scope.LoadSceneAsync(SceneAddress);
            yield return Wait(() => earlier.IsDone);
            var first = earlier.TakeResult();
            using var later = _system.LoadSceneAsync(SceneAddress, activateOnLoad: false);
            yield return Wait(() => later.IsDone);
            var secondLease = scope.Own(Take(later));
            var second = secondLease.Scene;
            Assert.That(first.isLoaded, Is.True);
            Assert.That(second.isLoaded, Is.False);
            var unloads = new List<(bool InstanceGone, bool TextHeld)>();
            void ObserveUnload(Scene scene)
            {
                if (scene == first || scene == second)
                {
                    unloads.Add((!instance, text.IsValid));
                }
            }
            SceneManager.sceneUnloaded += ObserveUnload;
            try
            {
                using var close = scope.CloseAsync();
                yield return Wait(() => close.IsDone);
                Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), close.Error?.ToString());
                Assert.That(close.TakeResult(), Is.True);
            }
            finally
            {
                SceneManager.sceneUnloaded -= ObserveUnload;
            }
            Assert.That(unloads, Is.EqualTo(new[] { (true, true), (true, true) }),
                "Both native unloads must happen after instance destruction and before ordinary asset release.");
            Assert.That(text.IsDisposed, Is.True);
            Assert.That(first.isLoaded || second.isLoaded, Is.False);
            Assert.That(Owners(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator ExplicitSceneLease_ActivatesAndUnloads()
        {
            using var load = _system.LoadSceneAsync(key: SceneAddress, activateOnLoad: false);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            Assert.That(lease.Key, Is.EqualTo(SceneAddress));
            var scene = lease.Scene;
            var handle = LoadHandle(lease);
            var bundle = SceneBundle(handle);
            Assert.That(scene.isLoaded, Is.False, "A deferred request must not silently activate.");
            Assert.That(bundle, Is.Not.Null);
            using var activate = lease.ActivateAsync();
            yield return Wait(() => activate.IsDone);
            Assert.That(activate.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), activate.Error?.ToString());
            Assert.That(scene.isLoaded, Is.True);
            Assert.That(handle.IsValid(), Is.True);
            using var unload = lease.UnloadAsync();
            Assert.That(lease.IsDisposed, Is.True);
            Assert.That(lease.IsSettled, Is.False, "Requesting unload is not native completion.");
            Assert.That(handle.IsValid(), Is.True);
            yield return Wait(() => unload.IsDone);
            Assert.That(unload.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), unload.Error?.ToString());
            Assert.That(scene.IsValid(), Is.False);
            Assert.That(handle.IsValid(), Is.False);
            Assert.That(lease.IsSettled, Is.True);
            yield return Wait(() => bundle == null);
        }

        [UnityTest]
        public IEnumerator ScopeClose_UnloadsOnlyOwnedAdditiveScene()
        {
            using var independentLoad = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => independentLoad.IsDone);
            var independent = Take(independentLoad);
            using var scope = _system.CreateScope("only this additive scene");
            using var ownedLoad = scope.LoadSceneAsync(SceneAddress);
            yield return Wait(() => ownedLoad.IsDone);
            var owned = ownedLoad.TakeResult();
            Assert.That(owned, Is.Not.EqualTo(independent.Scene));
            using var close = scope.CloseAsync();
            yield return Wait(() => close.IsDone);
            Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), close.Error?.ToString());
            Assert.That(owned.IsValid(), Is.False);
            Assert.That(independent.Scene.isLoaded, Is.True);
            Assert.That(LoadHandle(independent).IsValid(), Is.True);
        }

        [UnityTest]
        public IEnumerator SceneUnloadedBetweenCompletionAndTake_DoesNotTransferADeadLease()
        {
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), load.Error?.ToString());
            var owner = Owners().Single();
            yield return SceneManager.UnloadSceneAsync(LoadHandle(owner).Result.Scene);
            yield return Wait(() => owner.IsSettled);
            Assert.Throws<ResourceException>(() => load.TakeResult());
        }

        [UnityTest]
        public IEnumerator EarlierExternalAddressablesSceneSurvivesOwnUnloadAndServiceStop()
        {
            var external = Addressables.LoadSceneAsync(SceneAddress, LoadSceneMode.Additive);
            _externalScenes.Add(external);
            yield return Wait(() => external.IsDone);
            Assert.That(external.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), external.OperationException?.ToString());
            var externalScene = external.Result.Scene;
            Assert.That(externalScene.isLoaded, Is.True);
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var owned = Take(load);
            var ownScene = owned.Scene;
            Assert.That(ownScene.handle, Is.Not.EqualTo(externalScene.handle));
            using var unload = owned.UnloadAsync();
            yield return Wait(() => unload.IsDone);
            Assert.That(unload.TakeResult(), Is.True);
            Assert.That(ownScene.IsValid(), Is.False);
            Assert.That(external.IsValid() && externalScene.IsValid() && externalScene.isLoaded, Is.True);
            using var stop = _startup.StopAsync();
            yield return Wait(() => stop.IsDone);
            Assert.That(stop.TakeResult(), Is.True);
            Assert.That(external.IsValid() && externalScene.IsValid() && externalScene.isLoaded, Is.True,
                "YL may unload its own scene identity, not another owner's earlier load of the same path.");
        }

        [UnityTest]
        public IEnumerator ScopeCloseDuringNativeLoad_WaitsForTheCanceledSceneOwner()
        {
            using var scope = _system.CreateScope("in-flight additive scene");
            using var load = scope.LoadSceneAsync(SceneAddress);
            yield return Wait(() => Owners().Any(lease => LoadHandle(lease).IsValid()) && !load.IsDone);
            var owner = Owners().Single();
            var handle = LoadHandle(owner);
            using var close = scope.CloseAsync();
            yield return Wait(() =>
            {
                Assert.That(owner.IsSettled || handle.IsValid(), Is.True, "A pending scope drain must keep the original scene load handle.");
                return close.IsDone;
            });
            Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), close.Error?.ToString());
            Assert.That(owner.IsSettled, Is.True, "Scope close cannot finish ahead of its canceled native scene load.");
            Assert.That(handle.IsValid(), Is.False);
        }

        [UnityTest]
        public IEnumerator ScopeCloseDuringSingleLoad_RetainsFailedOwnerForRetry()
        {
            using var scope = _system.CreateScope("in-flight Single scene");
            using var load = scope.LoadSceneAsync(SceneAddress, LoadSceneMode.Single);
            yield return Wait(() => Owners().Any(lease => LoadHandle(lease).IsValid()) && !load.IsDone);
            var owner = Owners().Single();
            var handle = LoadHandle(owner);
            using var close = scope.CloseAsync();
            yield return Wait(() => close.IsDone);
            Assert.That(close.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(close.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(owner.IsSettled, Is.False);
            Assert.That(handle.IsValid(), Is.True);
            _neutralScenes.Add(SceneManager.CreateScene("YL.SceneTest.ScopeRetry"));
            using var retry = scope.CloseAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
            Assert.That(owner.IsSettled, Is.True);
            Assert.That(handle.IsValid(), Is.False);
        }

        [UnityTest]
        public IEnumerator CanceledDeferredScene_DrainsQueue()
        {
            ResourceEventHistory.SetRecording(true);
            using var query = new LocationQuery(SceneAddress);
            yield return query.Handle;
            var original = query.Handle.Result.Single();
            _dependencyGate = new SceneDependencyGate();
            Addressables.ResourceManager.ResourceProviders.Add(_dependencyGate);
            const string key = "ownership/gated-scene";
            var gate = new ResourceLocationBase(key + "/gate", key + "/gate", _dependencyGate.ProviderId, typeof(IAssetBundleResource));
            var dependencies = original.Dependencies.Concat(new[] { gate }).ToArray();
            _extraLocator = new ResourceLocationMap(key);
            _extraLocator.Add(key, new ResourceLocationBase(key, original.InternalId, original.ProviderId, typeof(SceneInstance), dependencies));
            Addressables.AddResourceLocator(_extraLocator);
            using var cancellation = new CancellationTokenSource();
            using var load = _system.LoadSceneAsync(key, activateOnLoad: false, cancellation: cancellation.Token);
            yield return Wait(() => _dependencyGate.Pending && Owners().Any(lease => LoadHandle(lease).IsValid()));
            var owner = Owners().Single();
            var handle = LoadHandle(owner);
            cancellation.Cancel();
            yield return Wait(() => load.IsDone);
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(handle.IsValid() && !handle.IsDone, Is.True);
            var id = load.EventTrace.Id;
            Assert.That(ResourceEventHistory.Read().Any(row => row.RequestId == id && row.Phase == ResourceEventPhase.SceneLoad), Is.False);
            _dependencyGate.Open();
            yield return Wait(() => owner.IsSettled);
            Assert.That(handle.IsValid(), Is.False);
            Assert.That(_dependencyGate.Releases, Is.EqualTo(1));
            var rows = ResourceEventHistory.Read().Where(row => row.RequestId == id).ToArray();
            Assert.That(rows.All(row => row.Source == ResourceEventSource.Scene && row.LoadId == id), Is.True);
            var canceled = rows.Single(row => row.Kind == ResourceEventKind.Terminal);
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            var loadResult = rows.Single(row => row.Phase == ResourceEventPhase.SceneLoad && row.Kind == ResourceEventKind.Work);
            var release = rows.Single(row => row.Phase == ResourceEventPhase.SceneLoad && row.Kind == ResourceEventKind.Release);
            Assert.That(release.WorkId, Is.EqualTo(loadResult.WorkId));
            var unload = rows.Single(row => row.Phase == ResourceEventPhase.SceneUnload && row.Kind == ResourceEventKind.Work);
            var cleanup = rows.Single(row => row.Phase == ResourceEventPhase.SceneCleanup && row.Kind == ResourceEventKind.Work);
            Assert.That(new[] { canceled.Sequence, loadResult.Sequence, unload.Sequence, release.Sequence, cleanup.Sequence }, Is.Ordered);
            Assert.That(new[] { loadResult, unload, release, cleanup }.All(row => row.Status == ResourceRequestStatus.Succeeded && row.Late), Is.True);
            using var next = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => next.IsDone);
            Assert.That(Take(next).Scene.isLoaded, Is.True, "The canceled activation gate must not block the next native scene operation.");
        }

        [UnityTest]
        public IEnumerator UnclaimedDeferredScene_DisposeDrainsQueue()
        {
            var load = _system.LoadSceneAsync(SceneAddress, activateOnLoad: false);
            yield return Wait(() => load.IsDone);
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), load.Error?.ToString());
            var owner = Owners().Single();
            var handle = LoadHandle(owner);
            load.Dispose();
            Assert.That(owner.IsSettled, Is.False);
            Assert.That(handle.IsValid(), Is.True);
            yield return Wait(() => owner.IsSettled);
            using var next = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => next.IsDone);
            Assert.That(Take(next).Scene.isLoaded, Is.True);
        }

        [UnityTest]
        public IEnumerator DeferredActivationTimeout_ReleasesTheGateAndDrainsItsHandle()
        {
            using var load = _system.LoadSceneAsync(SceneAddress, activateOnLoad: false, activationTimeoutSeconds: 0.1f);
            var owner = Owners().Single();
            yield return Wait(() => load.IsDone);
            if (load.Status == ResourceRequestStatus.Succeeded)
            {
                Assert.That(Take(load), Is.SameAs(owner));
            }
            else
            {
                Assert.That(load.Error.Kind, Is.EqualTo(ResourceError.ActivationTimedOut));
            }
            yield return Wait(() => owner.IsSettled);
            Assert.That(owner.LastError.Kind, Is.EqualTo(ResourceError.ActivationTimedOut));
            using var next = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => next.IsDone);
            Assert.That(Take(next).Scene.isLoaded, Is.True);
        }

        [UnityTest]
        public IEnumerator InvalidActivationLimits_RejectBeforeStartingNativeWork()
        {
            ResourceEventHistory.SetRecording(true);
            var before = _system.GetDiagnostics();
            var rejectedIds = new List<long>();
            foreach (var limit in new[] { 0f, -1f, 301f, float.NaN, float.PositiveInfinity })
            {
                using var load = _system.LoadSceneAsync(SceneAddress, activateOnLoad: false, activationTimeoutSeconds: limit);
                Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(load.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
                rejectedIds.Add(load.EventTrace.Id);
            }
            Assert.That(Owners(), Is.Empty);
            var after = _system.GetDiagnostics();
            Assert.That(after.RequestCount, Is.EqualTo(before.RequestCount));
            Assert.That(after.PendingWorkCount, Is.EqualTo(before.PendingWorkCount));
            var rows = ResourceEventHistory.Read().Where(row => row.Source == ResourceEventSource.Scene).ToArray();
            CollectionAssert.AreEqual(rejectedIds, rows.Select(row => row.RequestId));
            Assert.That(rejectedIds.Distinct().Count(), Is.EqualTo(rejectedIds.Count));
            Assert.That(rows.All(row => row.Kind == ResourceEventKind.Terminal && row.Status == ResourceRequestStatus.Failed &&
                row.Error == ResourceError.InvalidConfiguration && row.WorkId == 0), Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator LastSceneUnload_KeepsHandleUntilNeutralSceneAllowsRetry()
        {
            using var load = _system.LoadSceneAsync(SceneAddress, LoadSceneMode.Single);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            var scene = lease.Scene;
            var handle = LoadHandle(lease);
            var bundle = SceneBundle(handle);
            Assert.That(LoadedSceneCount(), Is.EqualTo(1));
            using var rejected = lease.UnloadAsync();
            yield return Wait(() => rejected.IsDone);
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(lease.LastError, Is.SameAs(rejected.Error));
            Assert.That(scene.isLoaded && handle.IsValid() && bundle != null, Is.True);
            Assert.That(lease.IsSettled, Is.False);

            _neutralScenes.Add(SceneManager.CreateScene("YL.SceneTest.Retry"));
            using var retry = lease.UnloadAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
            Assert.That(scene.IsValid() || handle.IsValid(), Is.False);
            Assert.That(lease.IsSettled, Is.True);
        }

        [UnityTest]
        public IEnumerator PendingUnload_IsNotAnAnchorForASecondLastSceneUnload()
        {
            using var firstLoad = _system.LoadSceneAsync(SceneAddress, LoadSceneMode.Single);
            yield return Wait(() => firstLoad.IsDone);
            var first = Take(firstLoad);
            using var secondLoad = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => secondLoad.IsDone);
            var second = Take(secondLoad);
            Assert.That(LoadedSceneCount(), Is.EqualTo(2));
            using var firstClose = first.UnloadAsync();
            using var secondClose = second.UnloadAsync();
            yield return Wait(() => firstClose.IsDone && secondClose.IsDone);
            var statuses = new[] { firstClose.Status, secondClose.Status };
            Assert.That(statuses.Count(status => status == ResourceRequestStatus.Succeeded), Is.EqualTo(1));
            Assert.That(statuses.Count(status => status == ResourceRequestStatus.Failed), Is.EqualTo(1));
            var survivor = first.IsSettled ? second : first;
            Assert.That(survivor.LastError.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(LoadHandle(survivor).IsValid(), Is.True);
            _neutralScenes.Add(SceneManager.CreateScene("YL.SceneTest.PendingRetry"));
            using var retry = survivor.UnloadAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
        }

        [UnityTest]
        public IEnumerator CanceledDeferredActivationRecordsEachCleanupFailureBeforeRetry()
        {
            ResourceEventHistory.SetRecording(true);
            var probe = SceneManager.CreateScene("YL.SceneTest.CompletedActivationProbe");
            var completed = SceneManager.UnloadSceneAsync(probe);
            yield return completed;
            using var load = _system.LoadSceneAsync(SceneAddress, activateOnLoad: false);
            yield return Wait(() => Owners().Any(lease => LoadHandle(lease).IsValid()) && !load.IsDone);
            var owner = Owners().Single();
            var handle = LoadHandle(owner);
            int attempts = 0;
            _system.SceneActivator = _ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("activation submission failed");
                return attempts == 2 ? null : completed;
            };
            load.Cancel();
            foreach (var expected in new[] { ResourceError.LoadFailed, ResourceError.LoadFailed, ResourceError.SceneUnloaded })
            {
                using var close = owner.UnloadAsync();
                yield return Wait(() => close.IsDone);
                Assert.That(close.Error.Kind, Is.EqualTo(expected));
                Assert.That(owner.IsSettled, Is.False);
                Assert.That(handle.IsValid(), Is.True);
                var rows = ResourceEventHistory.Read().Where(row => row.RequestId == load.EventTrace.Id).ToArray();
                var failures = rows.Where(row => row.Phase == ResourceEventPhase.SceneCleanup).ToArray();
                Assert.That(failures.Length, Is.EqualTo(attempts));
                Assert.That(failures.Last().Error, Is.EqualTo(expected));
                Assert.That(failures.All(row => row.Kind == ResourceEventKind.Work && row.Status == ResourceRequestStatus.Failed &&
                    row.Late && row.LoadId == load.EventTrace.Id && row.WorkId != 0), Is.True);
                Assert.That(failures.Select(row => row.WorkId).Distinct().Count(), Is.EqualTo(attempts));
                Assert.That(rows.Any(row => row.Phase == ResourceEventPhase.SceneUnload), Is.False);
            }
            _system.SceneActivator = null;
            using var retry = owner.UnloadAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.TakeResult(), Is.True);
            Assert.That(owner.IsSettled && !handle.IsValid(), Is.True);
            var final = ResourceEventHistory.Read().Where(row => row.RequestId == load.EventTrace.Id).ToArray();
            Assert.That(final.Single(row => row.Phase == ResourceEventPhase.SceneUnload).Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(final.Last(row => row.Phase == ResourceEventPhase.SceneCleanup).Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
        }

        [UnityTest]
        public IEnumerator ManualDeferredActivationFailureRecordsOnlyItsWaiter()
        {
            ResourceEventHistory.SetRecording(true);
            using var load = _system.LoadSceneAsync(SceneAddress, activateOnLoad: false);
            yield return Wait(() => load.IsDone);
            var owner = Take(load);
            _system.SceneActivator = _ => null;
            using var activation = owner.ActivateAsync();
            yield return Wait(() => activation.IsDone);
            Assert.That(activation.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            var rows = ResourceEventHistory.Read();
            var terminal = rows.Single(row => row.RequestId == activation.EventTrace.Id);
            Assert.That(terminal.Source, Is.EqualTo(ResourceEventSource.SceneActivate));
            Assert.That(terminal.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rows.Any(row => row.Phase == ResourceEventPhase.SceneCleanup || row.Phase == ResourceEventPhase.SceneUnload), Is.False);
            _system.SceneActivator = null;
            using var retry = owner.ActivateAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.TakeResult() && owner.Scene.isLoaded, Is.True);
            using var close = owner.UnloadAsync();
            yield return Wait(() => close.IsDone);
            Assert.That(close.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator NullNativeUnload_KeepsSceneAndHandleForRetry() => RejectedNativeUnload(false);

        [UnityTest]
        public IEnumerator ThrowingNativeUnload_KeepsSceneAndHandleForRetry() => RejectedNativeUnload(true);

        private IEnumerator RejectedNativeUnload(bool throws)
        {
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            var scene = lease.Scene;
            var handle = LoadHandle(lease);
            var bundle = SceneBundle(handle);
            var cause = new InvalidOperationException("native scene unload failed");
            SetUnloader((_, __) => throws ? throw cause : null);
            using var rejected = lease.UnloadAsync();
            yield return Wait(() => rejected.IsDone);
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            if (throws) Assert.That(rejected.Error.InnerException, Is.SameAs(cause));
            Assert.That(lease.LastError, Is.SameAs(rejected.Error));
            Assert.That(scene.isLoaded && handle.IsValid() && bundle != null, Is.True);
            Assert.That(lease.IsSettled, Is.False);
            SetUnloader(null);
            using var retry = lease.UnloadAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
            Assert.That(handle.IsValid(), Is.False);
            Assert.That(lease.IsSettled, Is.True);
        }

        [UnityTest]
        public IEnumerator NativeFailureCallback_CanRetryWithoutLosingTheNewWaiter()
        {
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            var handle = LoadHandle(lease);
            var submissions = 0;
            SetUnloader((scene, options) => ++submissions == 1 ? null : SceneManager.UnloadSceneAsync(scene, options));
            ResourceRequest<bool> retry = null;
            var failureKeptHandle = false;
            using var first = lease.UnloadAsync();
            first.Completed += done =>
            {
                failureKeptHandle = handle.IsValid();
                retry = lease.UnloadAsync();
            };
            try
            {
                yield return Wait(() => retry != null && retry.IsDone);
                Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(failureKeptHandle, Is.True);
                Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
                Assert.That(submissions, Is.EqualTo(2));
                Assert.That(lease.IsSettled, Is.True);
                Assert.That(handle.IsValid(), Is.False);
            }
            finally
            {
                retry?.Dispose();
                SetUnloader(null);
            }
        }

        [UnityTest]
        public IEnumerator ScopeCloseFailurePreservesCauseForEveryWaiterWhenFirstRetries()
        {
            using var scope = _system.CreateScope("retry from a close observer");
            using var load = scope.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var scene = load.TakeResult();
            var cause = new InvalidOperationException("first unload submission failed");
            var submissions = 0;
            SetUnloader((value, options) => ++submissions == 1 ? throw cause : SceneManager.UnloadSceneAsync(value, options));
            ResourceRequest<bool> retry = null;
            using var first = scope.CloseAsync();
            using var second = scope.CloseAsync();
            first.Completed += _ => retry = scope.CloseAsync();
            try
            {
                yield return Wait(() => second.IsDone && retry != null && retry.IsDone);
                Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(first.Error.InnerException, Is.SameAs(cause));
                Assert.That(second.Error, Is.SameAs(first.Error));
                Assert.That(Assert.Throws<ResourceException>(() => second.TakeResult()), Is.SameAs(first.Error));
                Assert.That(retry.TakeResult(), Is.True);
                Assert.That(scene.isLoaded, Is.False);
                Assert.That(submissions, Is.EqualTo(2));
            }
            finally
            {
                retry?.Dispose();
                SetUnloader(null);
            }
        }

        [UnityTest]
        public IEnumerator NativeCompletionAndSceneEvent_SettleOnceOutsideCallbacks()
        {
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            var scene = lease.Scene;
            var handle = LoadHandle(lease);
            var destroyed = 0;
            var eventObserved = false;
            var eventKeptHandle = false;
            var eventKeptOwner = false;
            AsyncOperation native = null;
            handle.Destroyed += _ => destroyed++;
            void OnUnloaded(Scene value)
            {
                if (value != scene) return;
                eventObserved = true;
                eventKeptHandle = handle.IsValid();
                eventKeptOwner = !lease.IsSettled;
            }
            SceneManager.sceneUnloaded += OnUnloaded;
            SetUnloader((value, options) => native = SceneManager.UnloadSceneAsync(value, options));
            try
            {
                using var first = lease.UnloadAsync();
                using var sibling = lease.UnloadAsync();
                lease.Dispose();
                first.Cancel();
                Assert.That(sibling, Is.Not.SameAs(first));
                yield return Wait(() => sibling.IsDone);
                Assert.That(sibling.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), sibling.Error?.ToString());
                Assert.That(eventObserved && eventKeptHandle && eventKeptOwner, Is.True);
                Assert.That(native != null && native.isDone, Is.True);
                Assert.That(scene.IsValid(), Is.False);
                Assert.That(destroyed, Is.EqualTo(1));
                lease.Dispose();
                using var repeated = lease.UnloadAsync();
                Assert.That(repeated.TakeResult(), Is.True);
                Assert.That(destroyed, Is.EqualTo(1));
            }
            finally
            {
                SetUnloader(null);
                SceneManager.sceneUnloaded -= OnUnloaded;
            }
        }

        [UnityTest]
        public IEnumerator ExternalUnload_SettlesOnlyItsOwnHandle()
        {
            using var firstLoad = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => firstLoad.IsDone);
            var first = Take(firstLoad);
            using var secondLoad = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => secondLoad.IsDone);
            var second = Take(secondLoad);
            var handle = LoadHandle(first);
            var destroyed = 0;
            handle.Destroyed += _ => destroyed++;
            var native = SceneManager.UnloadSceneAsync(first.Scene);
            yield return native;
            yield return Wait(() => first.IsSettled);
            Assert.That(handle.IsValid(), Is.False);
            Assert.That(destroyed, Is.EqualTo(1));
            Assert.That(second.Scene.isLoaded && LoadHandle(second).IsValid(), Is.True);
        }

        [UnityTest]
        public IEnumerator ExternalUnloadEvent_DoesNotOutrunTheSubmittedNativeOperation()
        {
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            var scene = lease.Scene;
            var handle = LoadHandle(lease);
            AsyncOperation completionBarrier = null;
            void OnLoaded(Scene value, LoadSceneMode mode)
            {
                if (value.path == NeutralPath) _neutralScenes.Add(value);
            }
            SceneManager.sceneLoaded += OnLoaded;
            SetUnloader((value, _) =>
            {
                SceneManager.UnloadSceneAsync(value);
                completionBarrier = EditorSceneManager.LoadSceneAsyncInPlayMode(NeutralPath, new LoadSceneParameters(LoadSceneMode.Additive));
                completionBarrier.allowSceneActivation = false;
                return completionBarrier;
            });
            try
            {
                using var unload = lease.UnloadAsync();
                yield return Wait(() => !scene.IsValid() && completionBarrier != null && completionBarrier.progress >= 0.9f);
                yield return null;
                Assert.That(completionBarrier.isDone, Is.False);
                Assert.That(unload.IsDone || lease.IsSettled, Is.False, "The scene event cannot complete a still-running submitted operation.");
                Assert.That(handle.IsValid(), Is.True);
                completionBarrier.allowSceneActivation = true;
                yield return Wait(() => unload.IsDone);
                Assert.That(unload.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), unload.Error?.ToString());
                Assert.That(completionBarrier.isDone, Is.True);
                Assert.That(handle.IsValid(), Is.False);
            }
            finally
            {
                if (completionBarrier != null) completionBarrier.allowSceneActivation = true;
                SetUnloader(null);
                SceneManager.sceneLoaded -= OnLoaded;
            }
        }

        [UnityTest]
        public IEnumerator ExternalSingle_SettlesOldSceneWithoutAddressablesTracking()
        {
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            var handle = LoadHandle(lease);
            var destroyed = 0;
            handle.Destroyed += _ => destroyed++;
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(NeutralPath, new LoadSceneParameters(LoadSceneMode.Single));
            _neutralScenes.Add(SceneManager.GetSceneByPath(NeutralPath));
            yield return Wait(() => lease.IsSettled);
            Assert.That(handle.IsValid(), Is.False);
            Assert.That(destroyed, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SingleCancel_DoesNotClaimRollback()
        {
            ResourceEventHistory.SetRecording(true);
            var oldScene = SceneManager.GetActiveScene();
            using var load = _system.LoadSceneAsync(SceneAddress, LoadSceneMode.Single, activateOnLoad: false);
            yield return Wait(() => Owners().Any(lease => LoadHandle(lease).IsValid()) && !load.IsDone);
            var owner = Owners().Single();
            var handle = LoadHandle(owner);
            load.Cancel();
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(handle.IsValid(), Is.True);
            yield return Wait(() => owner.LastError != null);
            Assert.That(oldScene.IsValid(), Is.False, "Cancel cannot restore a scene replaced by an already started Single load.");
            Assert.That(owner.LastError.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(owner.IsSettled, Is.False);
            Assert.That(handle.IsValid(), Is.True, "A last-scene drain failure must remain owned even though the request was canceled.");
            var rows = ResourceEventHistory.Read().Where(row => row.RequestId == load.EventTrace.Id).ToArray();
            var busy = rows.Single(row => row.Phase == ResourceEventPhase.SceneCleanup);
            Assert.That(busy.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(busy.Error, Is.EqualTo(ResourceError.Busy));
            Assert.That(busy.Late, Is.True);
            Assert.That(rows.Any(row => row.Phase == ResourceEventPhase.SceneUnload), Is.False);
            _neutralScenes.Add(SceneManager.CreateScene("YL.SceneTest.SingleRetry"));
            using var retry = owner.UnloadAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
            Assert.That(handle.IsValid(), Is.False);
            rows = ResourceEventHistory.Read().Where(row => row.RequestId == load.EventTrace.Id).ToArray();
            Assert.That(rows.Where(row => row.Phase == ResourceEventPhase.SceneCleanup).Select(row => row.Status),
                Is.EqualTo(new[] { ResourceRequestStatus.Failed, ResourceRequestStatus.Succeeded }));
            Assert.That(rows.Single(row => row.Phase == ResourceEventPhase.SceneUnload).Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
        }

        [UnityTest]
        public IEnumerator StopAfterCanceledSingle_ReportsRetainedFailureAndCanRetry()
        {
            using var load = _system.LoadSceneAsync(SceneAddress, LoadSceneMode.Single, activateOnLoad: false);
            yield return Wait(() => Owners().Any(lease => LoadHandle(lease).IsValid()) && !load.IsDone);
            var owner = Owners().Single();
            var handle = LoadHandle(owner);
            load.Cancel();
            yield return Wait(() => owner.LastError != null);
            using var stop = _startup.StopAsync();
            yield return Wait(() => stop.IsDone);
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(owner.LastError.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(owner.IsSettled, Is.False);
            Assert.That(handle.IsValid(), Is.True);
            _neutralScenes.Add(SceneManager.CreateScene("YL.SceneTest.StopRetry"));
            using var retry = _startup.StopAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
            Assert.That(owner.IsSettled, Is.True);
            Assert.That(handle.IsValid(), Is.False);
        }

        [UnityTest]
        public IEnumerator AmbiguousOrCustomSceneLocation_DoesNotStartNativeLoad()
        {
            using var query = new LocationQuery(SceneAddress);
            yield return query.Handle;
            var original = query.Handle.Result.Single();
            var prefix = "YL.SceneTest." + Guid.NewGuid().ToString("N");
            _extraLocator = new ResourceLocationMap(prefix);
            _extraLocator.Add(prefix + ".ambiguous", original);
            _extraLocator.Add(prefix + ".ambiguous", new ResourceLocationBase(prefix, original.InternalId,
                typeof(SceneProvider).FullName, typeof(SceneInstance), original.Dependencies.ToArray()));
            _extraLocator.Add(prefix + ".custom", new ResourceLocationBase(prefix, original.InternalId,
                "Unsupported.Custom.SceneProvider", typeof(SceneInstance), original.Dependencies.ToArray()));
            Addressables.AddResourceLocator(_extraLocator);
            var count = LoadedSceneCount();
            using var ambiguous = _system.LoadSceneAsync(prefix + ".ambiguous");
            yield return Wait(() => ambiguous.IsDone);
            Assert.That(ambiguous.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            ResourceEventHistory.SetRecording(true);
            using var missing = _system.LoadSceneAsync(prefix + ".missing");
            yield return Wait(() => missing.IsDone);
            Assert.That(missing.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(missing.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            var rows = ResourceEventHistory.Read().Where(row => row.RequestId == missing.EventTrace.Id).ToArray();
            var locate = rows.Where(row => row.Phase == ResourceEventPhase.SceneLocate).ToArray();
            Assert.That(locate.Select(row => row.Kind), Is.EqualTo(new[] { ResourceEventKind.Work, ResourceEventKind.Release }));
            Assert.That(locate.All(row => row.Status == ResourceRequestStatus.Succeeded && !row.Late), Is.True);
            Assert.That(locate[1].WorkId, Is.EqualTo(locate[0].WorkId));
            Assert.That(rows.Any(row => row.Phase == ResourceEventPhase.SceneLoad), Is.False);
            using var custom = _system.LoadSceneAsync(prefix + ".custom");
            yield return Wait(() => custom.IsDone);
            Assert.That(custom.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(custom.Error.Kind, Is.EqualTo(ResourceError.Unsupported));
            Assert.That(LoadedSceneCount(), Is.EqualTo(count));
            Assert.That(Owners(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator StopWithIndependentScene_ReturnsBusyWithoutUnloadingIt()
        {
            using var scope = _system.CreateScope("stop must not close this scope before checking external owners");
            using var ownedLoad = scope.LoadSceneAsync(SceneAddress);
            yield return Wait(() => ownedLoad.IsDone);
            Assert.That(ownedLoad.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), ownedLoad.Error?.ToString());
            var owned = ownedLoad.TakeResult();
            using var load = _system.LoadSceneAsync(SceneAddress);
            yield return Wait(() => load.IsDone);
            var lease = Take(load);
            var scene = lease.Scene;
            var handle = LoadHandle(lease);
            using var stop = _startup.StopAsync();
            yield return Wait(() => stop.IsDone);
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(stop.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(scene.isLoaded && handle.IsValid(), Is.True);
            Assert.That(owned.isLoaded, Is.True, "Stop Busy must precede destructive cleanup of application-owned scenes.");
            using var unload = lease.UnloadAsync();
            yield return Wait(() => unload.IsDone);
            Assert.That(unload.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), unload.Error?.ToString());
            using var retry = _startup.StopAsync();
            yield return Wait(() => retry.IsDone);
            Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), retry.Error?.ToString());
            Assert.That(owned.IsValid(), Is.False);
        }

        private static SceneLease Take(ResourceRequest<SceneLease> request)
        {
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            return request.TakeResult();
        }

        private IEnumerable<SceneLease> Owners() => (IEnumerable<SceneLease>)typeof(ResourceSystem)
            .GetField("_sceneLeases", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_system);

        private static AsyncOperationHandle<SceneInstance> LoadHandle(SceneLease lease) =>
            (AsyncOperationHandle<SceneInstance>)typeof(SceneLease)
                .GetField("_loadHandle", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lease);

        private void SetUnloader(Func<Scene, UnloadSceneOptions, AsyncOperation> unload) => typeof(ResourceSystem)
            .GetField("SceneUnloader", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_system, unload);

        private static AssetBundle SceneBundle(AsyncOperationHandle handle)
        {
            if (handle.Result is IAssetBundleResource bundle) return bundle.GetAssetBundle();
            var dependencies = new List<AsyncOperationHandle>();
            handle.GetDependencies(dependencies);
            foreach (var dependency in dependencies)
            {
                var result = SceneBundle(dependency);
                if (result != null) return result;
            }
            return null;
        }

        private static int LoadedSceneCount()
        {
            var count = 0;
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                if (SceneManager.GetSceneAt(index).isLoaded) count++;
            }
            return count;
        }

        private static IEnumerator Wait(Func<bool> completed)
        {
            var deadline = Time.realtimeSinceStartup + 30;
            while (!completed())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "The Scene owner did not reach its expected state.");
                yield return null;
            }
        }

        private sealed class SceneDependencyGate : ResourceProviderBase, IAssetBundleResource
        {
            private ProvideHandle? _pending;
            private bool _open;
            internal bool Pending => _pending.HasValue;
            internal int Releases { get; private set; }

            internal SceneDependencyGate() => m_ProviderId = "YL.SceneTest.Gate." + Guid.NewGuid().ToString("N");
            public override Type GetDefaultType(IResourceLocation location) => typeof(IAssetBundleResource);
            public override void Provide(ProvideHandle handle)
            {
                _pending = handle;
                if (_open)
                    Open();
            }
            public AssetBundle GetAssetBundle() => null;
            public override void Release(IResourceLocation location, object asset) => Releases++;

            internal void Open()
            {
                _open = true;
                if (!_pending.HasValue)
                    return;
                var handle = _pending.Value;
                _pending = null;
                handle.Complete<IAssetBundleResource>(this, true, null);
            }
        }

        private sealed class LocationQuery : IDisposable
        {
            internal readonly AsyncOperationHandle<IList<IResourceLocation>> Handle;
            internal LocationQuery(string key) => Handle = Addressables.LoadResourceLocationsAsync(key, typeof(SceneInstance));
            public void Dispose() => Addressables.Release(Handle);
        }
    }
}
#endif
