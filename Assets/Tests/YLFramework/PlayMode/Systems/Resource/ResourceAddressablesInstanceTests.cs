using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Resource.Addressables.Runtime")]
    public sealed class ResourceAddressablesInstanceTests
    {
        // The owned test host builds this real Addressables Prefab with InstanceLifecycleProbe.
        private const string PrefabKey = "ownership/prefab";
        private readonly List<IDisposable> _owners = new();
        private readonly List<GameObject> _objects = new();
        private readonly List<Scene> _scenes = new();
        private ResourceStartup _startup;
        private ResourceSystem _system;
        private AsyncOperationHandle<GameObject> _observedPrefab;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            InstanceLifecycleProbe.ResetCallbacks();
            _observedPrefab = default;
            Assert.That(ResourceSystem.Current, Is.Null, "Do not replace an external resource session.");
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = _startup.StartAsync();
            yield return Wait(starting);
            Assert.That(starting.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), starting.Error?.ToString());
            _system = starting.TakeResult().Resources;
            using var acquiring = _system.AcquireAsync<GameObject>(PrefabKey);
            yield return Wait(acquiring);
            using var prefab = acquiring.TakeResult();
            Assert.That(prefab.Asset.GetComponent<InstanceLifecycleProbe>(), Is.Not.Null,
                "The built Addressables fixture must contain the existing lifecycle probe.");
            prefab.Dispose();
            yield return Until(() => !HasPrefab());
            _observedPrefab = default;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            InstanceLifecycleProbe.ResetCallbacks();
            for (var i = _owners.Count - 1; i >= 0; i--)
            {
                _owners[i].Dispose();
            }
            _owners.Clear();
            foreach (var obj in _objects)
            {
                if (obj)
                {
                    Object.Destroy(obj);
                }
            }
            _objects.Clear();
            _system?.RetireInstanceCaches();
            yield return null;
            if (_startup != null)
            {
                using var stopping = _startup.StopAsync();
                yield return Wait(stopping);
                Assert.That(stopping.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stopping.Error?.ToString());
                Assert.That(stopping.TakeResult(), Is.True);
            }
            foreach (var scene in _scenes)
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    var unloading = SceneManager.UnloadSceneAsync(scene);
                    Assert.That(unloading, Is.Not.Null);
                    yield return unloading;
                }
            }
            _scenes.Clear();
            _startup = null;
            _system = null;
        }

        [UnityTest]
        public IEnumerator OfficialAnimatorControllerAndPrefabDependencyResolveToTheSameRuntimeAsset()
        {
            using var acquiring = _system.AcquireAsync<RuntimeAnimatorController>("ownership/animator");
            yield return Wait(acquiring);
            var controller = Keep(acquiring.TakeResult());
            var creating = Keep(_system.InstantiateAsync("ownership/animated-prefab"));
            yield return Until(() => creating.IsDone);
            Assert.That(creating.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), creating.Error?.ToString());
            var instance = Keep(creating.TakeResult());
            var animator = instance.GameObject.GetComponent<Animator>();
            Assert.That(animator.runtimeAnimatorController, Is.SameAs(controller.Asset));
            Assert.That(animator.parameterCount, Is.EqualTo(1));
            controller.Dispose();
            animator.SetBool("OwnershipReady", true);
            Assert.That(animator.GetBool("OwnershipReady"), Is.True, "The Prefab dependency must outlive the independent controller lease.");
            var clone = instance.GameObject;
            instance.Discard();
            yield return Until(() => !clone);
        }

        [UnityTest]
        public IEnumerator InstantiateAlwaysFresh_LeaseReleaseDestroys()
        {
            using var acquiring = _system.AcquireAsync<GameObject>(PrefabKey);
            yield return Wait(acquiring);
            using var prefab = acquiring.TakeResult();
            var pooled = Keep(_system.Rent(prefab));
            var idle = pooled.GameObject;
            pooled.Return();
            var first = Keep(_system.Instantiate(prefab));
            var second = Keep(_system.Instantiate(prefab));
            var a = first.GameObject;
            var b = second.GameObject;
            Assert.That(a, Is.Not.SameAs(idle), "Instantiate must not consume a Rent idle item.");
            Assert.That(b, Is.Not.SameAs(a));
            first.Return();
            second.Dispose();
            Assert.That(a && b, Is.True, "Destroy scheduling is not actual death.");
            yield return Until(() => !a && !b);
            var rentedAgain = Keep(_system.Rent(prefab));
            Assert.That(rentedAgain.GameObject, Is.SameAs(idle));
        }

        [UnityTest]
        public IEnumerator InstantiateScopeClose_ReleasesPrefabHandle()
        {
            var scope = Keep(_system.CreateScope("instance fresh scope"));
            using var creating = scope.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            var obj = creating.TakeResult();
            using var closing = scope.CloseAsync();
            Assert.That(closing.IsDone, Is.False);
            AssertPrefabHeld(true);
            yield return Wait(closing);
            Assert.That(closing.TakeResult(), Is.True);
            Assert.That(obj == null, Is.True);
            AssertPrefabHeld(false);
        }

        [UnityTest]
        public IEnumerator ScopeCloseDuringReservedCreate_WaitsForActualDestruction()
        {
            var scope = Keep(_system.CreateScope("close reserved instance"));
            GameObject obj = null;
            ResourceRequest<bool> closing = null;
            var destroyed = 0;
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                obj = value;
                ObservePrefab();
                closing = Keep(scope.CloseAsync());
                Assert.That(closing.IsDone, Is.False);
            };
            InstanceLifecycleProbe.OnDestroyed = value =>
            {
                if (ReferenceEquals(value, obj))
                {
                    Assert.That(closing.IsDone, Is.False, "OnDestroy is still inside the cleanup boundary.");
                    AssertPrefabHeld(true);
                    destroyed++;
                }
            };
            using var creating = scope.InstantiateAsync(PrefabKey);
            yield return Until(() => closing != null);
            yield return Wait(closing);
            Assert.That(closing.TakeResult(), Is.True);
            Assert.That(creating.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(obj == null, Is.True);
            Assert.That(destroyed, Is.EqualTo(1));
            AssertPrefabHeld(false);
        }

        [UnityTest]
        public IEnumerator ScopeCloseDuringReservedCreate_DoesNotWaitForOtherScopeCheckout()
        {
            var other = Keep(_system.CreateScope("unrelated live instance"));
            using var unrelatedRequest = other.InstantiateAsync(PrefabKey);
            yield return Wait(unrelatedRequest);
            var unrelated = unrelatedRequest.TakeResult();
            var scope = Keep(_system.CreateScope("close only own reserved instance"));
            GameObject obj = null;
            ResourceRequest<bool> closing = null;
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                obj = value;
                closing = Keep(scope.CloseAsync());
            };
            using var creating = scope.InstantiateAsync(PrefabKey);
            yield return Until(() => closing != null);
            yield return Wait(closing);
            Assert.That(closing.TakeResult(), Is.True);
            Assert.That(obj == null, Is.True);
            Assert.That(unrelated && unrelated.activeSelf, Is.True,
                "Closing a request must not wait for the global instance pool to become empty.");
        }

        [UnityTest]
        public IEnumerator InstanceRequestCanceledBeforeFirstTick_RemainsPendingUntilRoutineEnds()
        {
            var enabled = 0;
            InstanceLifecycleProbe.OnEnabled = _ => enabled++;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var alreadyCanceled = _system.InstantiateAsync(PrefabKey, cancellation: cancellation.Token);
            Assert.That(alreadyCanceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(HasPendingInstance(alreadyCanceled), Is.True);
            using var creating = _system.InstantiateAsync(PrefabKey);
            Assert.That(HasPendingInstance(creating), Is.True, "Register before returning, not on first MoveNext.");
            creating.Dispose();
            Assert.That(HasPendingInstance(creating), Is.True, "Cancellation is not the routine cleanup boundary.");
            yield return Until(() => !HasPendingInstance(creating) && !HasPendingInstance(alreadyCanceled));
            Assert.That(enabled, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CanceledReservedRentWithRejectedCandidate_RemainsPendingThroughDeathAndCleanupEpoch()
        {
            using var initial = _system.RentAsync(PrefabKey);
            yield return Wait(initial);
            var first = Keep(initial.TakeResult());
            var obj = first.GameObject;
            first.Return();
            var parent = new GameObject("rejected pending checkout parent");
            _objects.Add(parent);
            using var cancellation = new CancellationTokenSource();
            ResourceRequest<InstanceLease> renting = null;
            var destroyed = 0;
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                Assert.That(HasPendingInstance(renting), Is.True);
                value.transform.SetParent(parent.transform);
                cancellation.Cancel();
            };
            InstanceLifecycleProbe.OnDestroyed = value =>
            {
                if (ReferenceEquals(value, obj))
                {
                    Assert.That(HasPendingInstance(renting), Is.True);
                    AssertPrefabHeld(true);
                    destroyed++;
                }
            };
            using (renting = _system.RentAsync(PrefabKey, cancellation: cancellation.Token))
            {
                yield return Wait(renting);
                Assert.That(renting.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                if (obj)
                {
                    Assert.That(HasPendingInstance(renting), Is.True,
                        "A canceled request must retain its own discarded checkout until settlement.");
                }
                yield return Until(() => !HasPendingInstance(renting));
                Assert.That(obj == null, Is.True);
                Assert.That(destroyed, Is.EqualTo(1));
                AssertPrefabHeld(false);
            }
        }

        [UnityTest]
        public IEnumerator ReturnThenReuse_OldLeaseCannotReturnNewCheckout()
        {
            using var firstRequest = _system.RentAsync(PrefabKey);
            yield return Wait(firstRequest);
            var first = Keep(firstRequest.TakeResult());
            var obj = first.GameObject;
            first.Return();
            using var secondRequest = _system.RentAsync(PrefabKey);
            yield return Wait(secondRequest);
            var second = Keep(secondRequest.TakeResult());
            Assert.That(second.GameObject, Is.SameAs(obj));
            Assert.That(second, Is.Not.SameAs(first));
            first.Return();
            first.Discard();
            Assert.That(second.IsValid, Is.True);
            Assert.That(obj.activeSelf, Is.True);
            Assert.Throws<ObjectDisposedException>(() => _ = first.GameObject);
        }

        [UnityTest]
        public IEnumerator Discard_NeverEntersIdle()
        {
            using var acquiring = _system.AcquireAsync<GameObject>(key: PrefabKey);
            yield return Wait(acquiring);
            using var prefab = acquiring.TakeResult();
            using var cache = _system.GetOrCreateInstanceCache(prefab);
            Assert.That(_system.GetOrCreateInstanceCache(prefab), Is.SameAs(cache));
            var first = Keep(_system.Rent(prefab));
            var obj = first.GameObject;
            first.Discard();
            Assert.That(cache.CachedCount, Is.Zero);
            var second = Keep(_system.Rent(prefab));
            Assert.That(second.GameObject, Is.Not.SameAs(obj));
            yield return Until(() => !obj);
        }

        [UnityTest]
        public IEnumerator ScopeClose_ReturnsOnlyItsInstances()
        {
            var a = Keep(_system.CreateScope("instance scope a"));
            var b = Keep(_system.CreateScope("instance scope b"));
            using var firstRequest = a.RentAsync(PrefabKey);
            using var secondRequest = b.RentAsync(PrefabKey);
            yield return Wait(firstRequest);
            yield return Wait(secondRequest);
            var first = firstRequest.TakeResult();
            var second = secondRequest.TakeResult();
            using var closing = a.CloseAsync();
            yield return Wait(closing);
            Assert.That(closing.TakeResult(), Is.True);
            Assert.That(first.activeSelf, Is.False);
            Assert.That(second && second.activeSelf, Is.True);
            using var renting = _system.RentAsync(PrefabKey);
            yield return Wait(renting);
            var reused = Keep(renting.TakeResult());
            Assert.That(reused.GameObject, Is.SameAs(first));
        }

        [UnityTest]
        public IEnumerator DestroyPending_KeepsPrefabHandle()
        {
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            var lease = Keep(creating.TakeResult());
            var obj = lease.GameObject;
            var destroyed = 0;
            InstanceLifecycleProbe.OnDestroyed = value =>
            {
                if (!ReferenceEquals(value, obj))
                {
                    return;
                }
                AssertPrefabHeld(true);
                destroyed++;
            };
            using var closing = lease.CloseAsync();
            using var sibling = lease.CloseAsync();
            Assert.That(closing, Is.Not.SameAs(sibling));
            Assert.That(lease.IsDisposed, Is.True);
            Assert.That(closing.IsDone, Is.False);
            Assert.That(obj != null, Is.True);
            AssertPrefabHeld(true);
            yield return Wait(closing);
            yield return Wait(sibling);
            Assert.That(closing.TakeResult() && sibling.TakeResult(), Is.True);
            Assert.That(obj == null, Is.True);
            Assert.That(destroyed, Is.EqualTo(1));
            AssertPrefabHeld(false);
        }

        [UnityTest]
        public IEnumerator CancelCloseObserver_DoesNotCancelInstanceDestruction()
        {
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            var lease = Keep(creating.TakeResult());
            var obj = lease.GameObject;
            using var canceled = lease.CloseAsync();
            canceled.Cancel();
            using var observing = lease.CloseAsync();
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            yield return Wait(observing);
            Assert.That(observing.TakeResult(), Is.True);
            Assert.That(obj == null, Is.True);
            AssertPrefabHeld(false);
        }

        [UnityTest]
        public IEnumerator RetiredPool_LateReturnDestroys()
        {
            using var renting = _system.RentAsync(PrefabKey);
            yield return Wait(renting);
            var lease = Keep(renting.TakeResult());
            var obj = lease.GameObject;
            _system.RetireInstanceCaches();
            Assert.That(lease.IsValid, Is.True, "Retirement must not revoke an independent checkout.");
            lease.Return();
            Assert.That(obj != null, Is.True);
            AssertPrefabHeld(true);
            yield return Until(() => !obj);
            yield return Until(() => !HasPrefab());
        }

        [UnityTest]
        public IEnumerator InstantiateExternalDestroy_SettlesWithoutExplicitReturn()
        {
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            var lease = Keep(creating.TakeResult());
            var obj = lease.GameObject;
            Object.Destroy(obj);
            AssertPrefabHeld(true);
            yield return Until(() => !obj && lease.IsDisposed && !HasPrefab());
            lease.Return();
            lease.Discard();
        }

        [UnityTest]
        public IEnumerator RentExternalDestroy_SettlesWithoutExplicitReturn()
        {
            using var renting = _system.RentAsync(PrefabKey);
            yield return Wait(renting);
            var lease = Keep(renting.TakeResult());
            var obj = lease.GameObject;
            Object.Destroy(obj);
            AssertPrefabHeld(true);
            yield return Until(() => !obj && lease.IsDisposed && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator OnDestroyReentersReturnAndClear_KeepsPrefabHandleUntilCallbackEnds()
        {
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            var lease = Keep(creating.TakeResult());
            var obj = lease.GameObject;
            var callbacks = 0;
            InstanceLifecycleProbe.OnDestroyed = value =>
            {
                if (ReferenceEquals(value, obj))
                {
                    lease.Return();
                    _system.RetireInstanceCaches();
                    AssertPrefabHeld(true);
                    callbacks++;
                }
            };
            Object.Destroy(obj);
            yield return Until(() => !obj && lease.IsDisposed && !HasPrefab());
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AwakeClosesScope_RefusesDeliveryAndDrainsReservation()
        {
            var scope = Keep(_system.CreateScope("awake closes instance scope"));
            GameObject obj = null;
            InstanceLifecycleProbe.OnAwake = value =>
            {
                InstanceLifecycleProbe.OnAwake = null;
                obj = value;
                ObservePrefab();
                scope.Dispose();
                AssertPrefabHeld(true);
            };
            using var creating = scope.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            Assert.That(ReferenceEquals(obj, null), Is.False, "The real Prefab must contain InstanceLifecycleProbe.");
            Assert.That(creating.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
            yield return Until(() => !obj && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator OnEnableClosesScope_RefusesDelivery()
        {
            var scope = Keep(_system.CreateScope("enable closes instance scope"));
            GameObject obj = null;
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                obj = value;
                ObservePrefab();
                scope.Dispose();
            };
            using var creating = scope.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            Assert.That(creating.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
            yield return Until(() => !obj && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator ReusedOnEnableClosesScope_DestroysInsteadOfDelivering()
        {
            using var initial = _system.RentAsync(PrefabKey);
            yield return Wait(initial);
            var first = Keep(initial.TakeResult());
            var obj = first.GameObject;
            first.Return();
            var scope = Keep(_system.CreateScope("reuse enable closes scope"));
            InstanceLifecycleProbe.OnEnabled = _ =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                scope.Dispose();
                AssertPrefabHeld(true);
            };
            using var renting = scope.RentAsync(PrefabKey);
            yield return Wait(renting);
            Assert.That(renting.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
            yield return Until(() => !obj && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator OnDisableStopsService_ReturnDrainsBeforeStopSucceeds()
        {
            using var renting = _system.RentAsync(PrefabKey);
            yield return Wait(renting);
            var lease = Keep(renting.TakeResult());
            var obj = lease.GameObject;
            ResourceRequest<bool> stopping = null;
            InstanceLifecycleProbe.OnDisabled = _ =>
            {
                InstanceLifecycleProbe.OnDisabled = null;
                stopping = Keep(_startup.StopAsync());
                AssertPrefabHeld(true);
            };
            lease.Return();
            Assert.That(stopping, Is.Not.Null);
            yield return Wait(stopping);
            Assert.That(stopping.TakeResult(), Is.True);
            Assert.That(obj == null, Is.True);
        }

        [UnityTest]
        public IEnumerator OnDisableRetiresOldPoolAndRentsReplacementWithoutRevokingNewCheckout()
        {
            using var acquiring = _system.AcquireAsync<GameObject>(PrefabKey);
            yield return Wait(acquiring);
            using var prefab = acquiring.TakeResult();
            using var oldCache = _system.GetOrCreateInstanceCache(prefab);
            var first = Keep(oldCache.Rent());
            var oldObject = first.GameObject;
            InstanceLease nested = null;
            InstanceLifecycleProbe.OnDisabled = _ =>
            {
                InstanceLifecycleProbe.OnDisabled = null;
                first.Return();
                oldCache.Dispose();
                nested = Keep(_system.Rent(prefab));
            };
            first.Return();
            Assert.That(oldCache.IsRetired, Is.True);
            Assert.That(nested, Is.Not.Null);
            var replacement = nested.GameObject;
            Assert.That(replacement && replacement.activeSelf, Is.True);
            Assert.That(replacement, Is.Not.SameAs(oldObject));
            first.Discard();
            yield return Until(() => !oldObject);
            Assert.That(nested.GameObject == replacement && replacement.activeSelf, Is.True,
                "Finishing an old checkout must not retire or destroy the replacement pool's live checkout.");
            nested.Discard();
            _system.RetireInstanceCaches();
            prefab.Dispose();
            yield return Until(() => !replacement && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator StopBusy_DoesNotDestroyOwnedParentOfIndependentInstance()
        {
            var scope = Keep(_system.CreateScope("owned instance parent"));
            using var parentRequest = scope.InstantiateAsync(PrefabKey);
            yield return Wait(parentRequest);
            var parent = parentRequest.TakeResult();
            using var renting = _system.RentAsync(PrefabKey, parent.transform);
            yield return Wait(renting);
            var independent = Keep(renting.TakeResult());
            var obj = independent.GameObject;
            using var busy = _startup.StopAsync();
            yield return Wait(busy);
            Assert.That(busy.Error?.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(parent && obj, Is.True);
            Assert.That(independent.IsValid, Is.True);
            using var rejected = _system.InstantiateAsync(PrefabKey);
            Assert.That(rejected.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
            independent.Return();
            using var stopping = _startup.StopAsync();
            yield return Wait(stopping);
            Assert.That(stopping.TakeResult(), Is.True);
            Assert.That(parent == null && obj == null, Is.True);
        }

        [UnityTest]
        public IEnumerator AwakeStopsService_RefusesDeliveryUntilActualDeath()
        {
            GameObject obj = null;
            InstanceLifecycleProbe.OnAwake = value =>
            {
                InstanceLifecycleProbe.OnAwake = null;
                obj = value;
                ObservePrefab();
                Keep(_startup.StopAsync());
                AssertPrefabHeld(true);
            };
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            Assert.That(creating.Status, Is.Not.EqualTo(ResourceRequestStatus.Succeeded));
            yield return Until(() => !obj && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator CreateCallbackChangesParent_RefusesDelivery()
        {
            var other = new GameObject("instance callback replacement parent");
            _objects.Add(other);
            GameObject obj = null;
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                obj = value;
                ObservePrefab();
                value.transform.SetParent(other.transform);
            };
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            Assert.That(creating.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            yield return Until(() => !obj && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator ReuseCallbackDestroysInstance_RefusesDelayedDeadObject()
        {
            using var initial = _system.RentAsync(PrefabKey);
            yield return Wait(initial);
            var first = Keep(initial.TakeResult());
            var obj = first.GameObject;
            first.Return();
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                Object.Destroy(value);
                AssertPrefabHeld(true);
            };
            using var renting = _system.RentAsync(PrefabKey);
            yield return Wait(renting);
            Assert.That(renting.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(obj == null, Is.True);
            yield return Until(() => !HasPrefab());
        }

        [UnityTest]
        public IEnumerator CreateCallbackDestroysInstance_RefusesDelayedDeadObject()
        {
            GameObject obj = null;
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                ObservePrefab();
                obj = value;
                Object.Destroy(value);
                AssertPrefabHeld(true);
            };
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            Assert.That(creating.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(obj == null, Is.True);
            yield return Until(() => !HasPrefab());
        }

        [UnityTest]
        public IEnumerator ReuseCallbackChangesParent_RefusesDelivery()
        {
            using var initial = _system.RentAsync(PrefabKey);
            yield return Wait(initial);
            var first = Keep(initial.TakeResult());
            var obj = first.GameObject;
            first.Return();
            var parent = new GameObject("reuse replacement parent");
            _objects.Add(parent);
            InstanceLifecycleProbe.OnEnabled = value =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                value.transform.SetParent(parent.transform);
            };
            using var renting = _system.RentAsync(PrefabKey);
            yield return Wait(renting);
            Assert.That(renting.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            yield return Until(() => !obj && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator CancellationDuringOnEnable_DoesNotLeaveIdleOrPin()
        {
            using var cancellation = new CancellationTokenSource();
            InstanceLifecycleProbe.OnEnabled = _ =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                ObservePrefab();
                cancellation.Cancel();
            };
            using var creating = _system.InstantiateAsync(PrefabKey, cancellation: cancellation.Token);
            yield return Wait(creating);
            Assert.That(creating.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            yield return Until(() => !HasPrefab());
        }

        [UnityTest]
        public IEnumerator UnclaimedRentResult_IsDiscardedWithoutIdlePin()
        {
            GameObject obj = null;
            InstanceLifecycleProbe.OnEnabled = value => obj = value;
            using var renting = _system.RentAsync(PrefabKey);
            yield return Wait(renting);
            Assert.That(renting.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(obj, Is.Not.Null);
            Assert.That(HasPendingInstance(renting), Is.False, "A delivered result is now a registered lease.");
            renting.Dispose();
            Assert.That(HasPendingInstance(renting), Is.False, "Unclaimed result cleanup belongs to its lease.");
            AssertPrefabHeld(true);
            yield return Until(() => !obj && !HasPrefab());
        }

        [UnityTest]
        public IEnumerator CompletedInstanceDestroyedBeforeTake_RefusesLease()
        {
            GameObject obj = null;
            InstanceLifecycleProbe.OnEnabled = value => obj = value;
            using var creating = _system.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            Assert.That(creating.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Object.Destroy(obj);
            yield return Until(() => !obj);
            Assert.Throws<ResourceException>(() => creating.TakeResult());
            Assert.That(creating.IsDisposed, Is.True);
            yield return Until(() => !HasPrefab());
        }

        [UnityTest]
        public IEnumerator CompletedScopedInstanceDestroyedBeforeTake_RefusesBorrowWhileScopeRemainsOpen()
        {
            using var scope = _system.CreateScope("scoped late claim");
            GameObject obj = null;
            InstanceLifecycleProbe.OnEnabled = value => obj = value;
            using var creating = scope.InstantiateAsync(PrefabKey);
            yield return Wait(creating);
            Assert.That(creating.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(obj, Is.Not.Null);
            Object.Destroy(obj);
            yield return Until(() => !obj);
            Assert.That(scope.IsDisposed, Is.False);
            Assert.Throws<ResourceException>(() => creating.TakeResult());
            Assert.That(creating.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), "Rejection at handoff does not rewrite the original outcome.");
            using var closing = scope.CloseAsync();
            yield return Wait(closing);
            Assert.That(closing.TakeResult(), Is.True);
            yield return Until(() => !HasPrefab());
        }

        [UnityTest]
        public IEnumerator Prewarm_IsBoundedInactiveAndUsesCapturedScene()
        {
            using var acquiring = _system.AcquireAsync<GameObject>(PrefabKey);
            yield return Wait(acquiring);
            using var prefab = acquiring.TakeResult();
            var scene = SceneManager.CreateScene("instance scene " + Guid.NewGuid().ToString("N"));
            _scenes.Add(scene);
            using var cache = _system.GetOrCreateInstanceCache(prefab, InstanceCachePolicy.SceneScoped, scene);
            var enabled = 0;
            InstanceLifecycleProbe.OnEnabled = _ => enabled++;
            Assert.That(cache.Prewarm(2), Is.EqualTo(2));
            Assert.That(enabled, Is.Zero);
            Assert.That(cache.Prewarm(2), Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => cache.Prewarm(33));
            var lease = Keep(cache.Rent());
            Assert.That(lease.GameObject.scene, Is.EqualTo(scene));
            Assert.That(enabled, Is.EqualTo(1));
            lease.Return();
            Assert.That(cache.CachedCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SceneCacheRetirement_PreservesMovedCheckoutAndPersistentIdle()
        {
            using var acquiring = _system.AcquireAsync<GameObject>(PrefabKey);
            yield return Wait(acquiring);
            using var prefab = acquiring.TakeResult();
            var scene = SceneManager.CreateScene("retiring instance scene " + Guid.NewGuid().ToString("N"));
            _scenes.Add(scene);
            using var sceneCache = _system.GetOrCreateInstanceCache(prefab, InstanceCachePolicy.SceneScoped, scene);
            using var persistent = _system.GetOrCreateInstanceCache(prefab, InstanceCachePolicy.Persistent);
            Assert.That(sceneCache.Prewarm(2), Is.EqualTo(2));
            Assert.That(persistent.Prewarm(1), Is.EqualTo(1));
            var moved = Keep(sceneCache.Rent());
            var obj = moved.GameObject;
            SceneManager.MoveGameObjectToScene(obj, SceneManager.GetActiveScene());
            var unloading = SceneManager.UnloadSceneAsync(scene);
            Assert.That(unloading, Is.Not.Null);
            yield return unloading;
            Assert.That(sceneCache.IsRetired, Is.True);
            Assert.That(moved.IsValid, Is.True);
            Assert.That(persistent.IsRetired, Is.False);
            Assert.That(persistent.CachedCount, Is.EqualTo(1));
            moved.Return();
            yield return Until(() => !obj);
        }

        private T Keep<T>(T owner) where T : IDisposable
        {
            _owners.Add(owner);
            return owner;
        }

        private bool HasPrefab()
        {
            return _observedPrefab.IsValid();
        }

        private bool HasPendingInstance(ResourceRequest<InstanceLease> input)
        {
            var query = typeof(ResourceSystem).GetMethod("HasPendingInstanceRequest",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(query, Is.Not.Null, "The per-input Scope cleanup bridge must exist.");
            return (bool)query.Invoke(_system, new object[] { input });
        }

        private void ObservePrefab()
        {
            // Acquire a real public AA reference, then keep only its non-owning identity for lifetime assertions.
            // This observes the operation even while Stopping forbids a new YL TryAcquireLoaded/Retain.
            var handle = Addressables.LoadAssetAsync<GameObject>(PrefabKey);
            try
            {
                Assert.That(handle.IsDone, Is.True, "YL must already hold the same official Prefab operation.");
                Assert.That(handle.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
                _observedPrefab = handle;
            }
            finally
            {
                Addressables.Release(handle);
            }
        }

        private void AssertPrefabHeld(bool expected) => Assert.That(HasPrefab(), Is.EqualTo(expected),
            "Only the instance/pool pin should keep this fixture ready after the caller's Prefab lease ends.");

        private IEnumerator Wait<T>(ResourceRequest<T> request)
        {
            yield return Until(() => request.IsDone);
            if (request.Status == ResourceRequestStatus.Succeeded &&
                (typeof(T) == typeof(InstanceLease) || typeof(T) == typeof(GameObject) ||
                    typeof(T) == typeof(AssetLease<GameObject>)))
            {
                ObservePrefab();
            }
        }

        private static IEnumerator Until(Func<bool> predicate)
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (!predicate() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(predicate(), Is.True, "The owned resource operation did not settle within 10 seconds.");
        }
    }
}
