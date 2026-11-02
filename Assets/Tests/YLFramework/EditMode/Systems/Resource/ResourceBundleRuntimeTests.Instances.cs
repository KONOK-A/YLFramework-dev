using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Pooling;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceBundleRuntimeTests
    {
        [UnityTest]
        public IEnumerator PrefabCheckoutReusesNativeInstanceWithoutReusingOwnership()
        {
            yield return new EnterPlayMode();
            yield return InstanceBaselineBody();
        }

        private static IEnumerator InstanceBaselineBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            var first = system.Instantiate(address);
            var instance = first.GameObject;
            var texture = instance.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            first.Dispose();
            Assert.That(instance.activeSelf, Is.False);
            Assert.That(texture == null, Is.False, "Idle instances still depend on the Prefab graph.");
            var second = system.Instantiate(address);
            Assert.That(second.GameObject, Is.SameAs(instance));
            first.Dispose();
            first.Discard();
            Assert.Throws<ObjectDisposedException>(() => _ = first.GameObject);
            Assert.That(instance.activeSelf, Is.True, "An old checkout cannot return the current checkout.");
            second.Dispose();
            system.ClearInstanceCaches();
            Assert.That(texture == null, Is.False, "Destroy scheduling is not native destruction.");
            yield return null;
            yield return null;
            Assert.That(instance == null, Is.True);
            Assert.That(texture == null, Is.True);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }

        private static GameObjectPoolData CacheData(InstanceCache cache) => (GameObjectPoolData)typeof(InstanceCache)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache);

        private static IEnumerator InstancesDrained(ResourceSystem system)
        {
            var until = Time.realtimeSinceStartup + 10;
            while (system.GetDiagnosticsSnapshot().RecordCount != 0 && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }

        private static IEnumerator InstanceCapacityBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var prefab = system.Acquire<GameObject>(Address(snapshot, "A.prefab"));
            var nativePrefab = prefab.Asset;
            var texture = nativePrefab.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            var cache = system.GetInstanceCache(prefab);
            var data = CacheData(cache);
            var awake = 0;
            var destroyed = 0;
            InstanceLifecycleProbe.OnAwake = _ => awake++;
            InstanceLifecycleProbe.OnDestroyed = _ =>
            {
                Assert.That(texture == null, Is.False, "Prefab dependencies must remain alive throughout OnDestroy.");
                destroyed++;
            };
            Assert.That(cache.Prewarm(2), Is.EqualTo(2));
            Assert.That(cache.Prewarm(2), Is.Zero);
            var neverActive = data.PoolQueue.ToArray();
            Assert.That(awake, Is.Zero);
            prefab.Dispose();
            Assert.That(nativePrefab == null, Is.False);
            cache.MaxCapacity = 1;
            Assert.That(cache.CachedCount, Is.EqualTo(1));
            var first = cache.Instantiate();
            var live = first.GameObject;
            Assert.That(awake, Is.EqualTo(1));
            live.GetComponent<MeshRenderer>().sharedMaterial = null;
            first.Discard();
            Assert.That(texture == null, Is.False);
            var next = cache.Instantiate();
            Assert.That(next.GameObject, Is.Not.SameAs(live));
            Assert.That(next.GameObject.GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.SameAs(texture));
            cache.Dispose();
            Assert.That(data.IsRecycled, Is.False, "Retired Data must not return to Safe while a checkout/Destroy-pending instance still owns it.");
            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            Assert.That(system.IsAvailable, Is.True);
            var replacement = system.Instantiate(Address(snapshot, "A.prefab"));
            var replacementCache = system.GetInstanceCache(Address(snapshot, "A.prefab"));
            Assert.That(CacheData(replacementCache), Is.Not.SameAs(data));
            next.Return();
            yield return null;
            yield return null;
            Assert.That(data.IsRecycled, Is.True);
            Assert.That(neverActive.All(x => x == null), Is.True);
            Assert.That(destroyed, Is.EqualTo(2), "The never-activated prewarm does not issue OnDestroy; cleanup must not depend on that callback.");
            Assert.That(replacement.IsValid, Is.True);
            replacement.Return();
            replacementCache.MaxCapacity = 0;
            using var closing = system.ShutdownAsync();
            Assert.That(closing.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            Assert.That(texture == null, Is.False);
            yield return Wait(closing);
            Assert.That(closing.TakeResult(), Is.True);
            Assert.That(nativePrefab == null, Is.True);
            Assert.That(texture == null, Is.True);
            InstanceLifecycleProbe.ResetCallbacks();
        }

        private static IEnumerator InstanceExternalDestroyBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            var lease = system.Instantiate(address);
            var obj = lease.GameObject;
            var texture = obj.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            UnityEngine.Object.Destroy(obj);
            Assert.That(texture == null, Is.False);
            yield return null;
            Assert.That(lease.IsValid, Is.False);
            var cache = system.GetInstanceCache(address);
            Assert.That(cache.Prewarm(1), Is.EqualTo(1));
            var idle = CacheData(cache).PoolQueue.Single();
            UnityEngine.Object.Destroy(idle);
            yield return null;
            Assert.That(cache.CachedCount, Is.Zero);
            var next = system.Instantiate(address);
            lease.Discard();
            Assert.That(next.IsValid, Is.True);
            next.Discard();
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);
            Assert.That(texture == null, Is.True);
            lease.Return();
        }

        private static IEnumerator EmptyInstanceCacheShutdownBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var cache = system.GetInstanceCache(Address(snapshot, "A.prefab"));
            using var shutdown = system.ShutdownAsync();
            Assert.That(shutdown.IsDone, Is.True, "Flushing an empty cache must not complete CheckDrain before the shutdown request is registered.");
            Assert.That(shutdown.TakeResult(), Is.True);
            Assert.That(cache.IsRetired, Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
            yield break;
        }

        private static IEnumerator DestroyedInstanceCacheRootBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            var old = system.GetInstanceCache(address);
            old.Prewarm(1);
            UnityEngine.Object.DestroyImmediate(CacheData(old).RootTransform.gameObject);
            var replacement = system.Instantiate(address);
            Assert.That(old.IsRetired, Is.True);
            Assert.That(replacement.IsValid, Is.True, "A destroyed idle root cannot remain the authoritative cache.");
            replacement.Discard();
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);
        }

        [UnityTest]
        public IEnumerator EmptyInstanceCacheShutdownSettlesItsRequest()
        {
            yield return new EnterPlayMode();
            yield return EmptyInstanceCacheShutdownBody();
        }

        private static IEnumerator InstanceReentryBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            GameObject failed = null;
            Texture texture = null;
            InstanceLifecycleProbe.OnAwake = obj =>
            {
                InstanceLifecycleProbe.OnAwake = null;
                failed = obj;
                texture = obj.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
                using var busy = system.ShutdownAsync();
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy), "Creation pin/operation must be established before Awake.");
                Assert.That(texture == null, Is.False);
            };
            Assert.Throws<ResourceException>(() => system.Instantiate(address));
            Assert.That(texture == null, Is.False);
            yield return InstancesDrained(system);
            Assert.That(failed == null, Is.True);
            Assert.That(texture == null, Is.True);

            var first = system.Instantiate(address);
            var oldObject = first.GameObject;
            var oldCache = system.GetInstanceCache(address);
            InstanceLease nested = null;
            InstanceLifecycleProbe.OnDisabled = _ =>
            {
                InstanceLifecycleProbe.OnDisabled = null;
                first.Return();
                oldCache.Dispose();
                nested = system.Instantiate(address);
            };
            first.Return();
            Assert.That(nested.IsValid, Is.True);
            Assert.That(nested.GameObject, Is.Not.SameAs(oldObject));
            first.Discard();
            nested.Return();
            var current = system.GetInstanceCache(address);
            InstanceLifecycleProbe.OnEnabled = _ =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                current.Dispose();
                nested = system.Instantiate(address);
            };
            Assert.Throws<ResourceException>(() => system.Instantiate(address));
            Assert.That(nested.IsValid, Is.True);
            nested.Discard();
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);

            InstanceLifecycleProbe.OnAwake = _ => throw new InvalidOperationException("instance84-awake");
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: instance84-awake");
            var throwing = system.Instantiate(address);
            InstanceLifecycleProbe.ResetCallbacks();
            throwing.Discard();
            throwing = system.Instantiate(address);
            InstanceLifecycleProbe.OnDisabled = _ => throw new InvalidOperationException("instance84-disable");
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: instance84-disable");
            throwing.Return();
            InstanceLifecycleProbe.ResetCallbacks();
            InstanceLifecycleProbe.OnEnabled = _ => throw new InvalidOperationException("instance84-enable");
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: instance84-enable");
            throwing = system.Instantiate(address);
            InstanceLifecycleProbe.ResetCallbacks();
            throwing.Discard();
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);
        }

        private static IEnumerator InstanceAsyncBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            using (var failed = system.InstantiateAsync(Address(snapshot, "unknown.prefab")))
            {
                Assert.That(failed.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            }

            using (var wrong = system.InstantiateAsync(Address(snapshot, "entry.txt")))
            {
                Assert.That(wrong.Error.Kind, Is.EqualTo(ResourceError.TypeMismatch));
            }

            using (var disposed = system.Acquire<GameObject>(address))
            {
                disposed.Dispose();
                Assert.Throws<ObjectDisposedException>(() => system.Instantiate(disposed));
            }
            using (var canceled = system.InstantiateAsync(address))
            {
                canceled.Cancel();
            }

            using var delivered = system.InstantiateAsync(address);
            yield return Wait(delivered);
            var lease = delivered.TakeResult();
            delivered.Dispose();
            Assert.That(lease.IsValid, Is.True);
            Assert.Throws<ObjectDisposedException>(() => delivered.TakeResult());
            lease.Discard();
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);

            ResourceRequest<InstanceLease> reentrant = null;
            InstanceLifecycleProbe.OnAwake = _ =>
            {
                InstanceLifecycleProbe.OnAwake = null;
                reentrant.Cancel();
            };
            reentrant = system.InstantiateAsync(address);
            yield return Wait(reentrant);
            Assert.That(reentrant.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            reentrant.Dispose();
            yield return InstancesDrained(system);
            using var unclaimed = system.InstantiateAsync(address);
            yield return Wait(unclaimed);
            unclaimed.Dispose();
            var cache = system.GetInstanceCache(address);
            Assert.That(cache.CachedCount, Is.EqualTo(1));
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);
        }

        private static IEnumerator FailedImplicitInstanceCacheBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            var parent = new GameObject("YL84-parent");
            using (var failed = system.InstantiateAsync(address, parent.transform))
            {
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Pending));
                UnityEngine.Object.DestroyImmediate(parent);
                yield return Wait(failed);
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            }
            yield return InstancesDrained(system);
            InstanceLifecycleProbe.OnEnabled = obj =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                obj.SetActive(false);
            };
            Assert.Throws<ResourceException>(() => system.Instantiate(address));
            yield return InstancesDrained(system);

            // 显式缓存即使尚未成功checkout也有明确持有者，失败不能代为退休它。
            var explicitCache = system.GetInstanceCache(address);
            InstanceLifecycleProbe.OnEnabled = obj =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                obj.SetActive(false);
            };
            Assert.Throws<ResourceException>(() => system.Instantiate(address));
            Assert.That(explicitCache.IsRetired, Is.False);
            explicitCache.Dispose();
            yield return InstancesDrained(system);

            InstanceLease nested = null;
            InstanceLifecycleProbe.OnEnabled = obj =>
            {
                InstanceLifecycleProbe.OnEnabled = null;
                nested = system.Instantiate(address);
                obj.SetActive(false);
            };
            Assert.Throws<ResourceException>(() => system.Instantiate(address));
            Assert.That(nested.IsValid, Is.True, "A failed sibling cannot retire a cache already committed by a successful nested checkout.");
            nested.Return();
            Assert.That(system.GetInstanceCache(address).CachedCount, Is.EqualTo(1));
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);
        }

        [UnityTest]
        public IEnumerator FailedImplicitCreationReleasesItsGraphWithoutExplicitClear()
        {
            yield return new EnterPlayMode();
            yield return FailedImplicitInstanceCacheBody();
        }

        private static IEnumerator InstanceSceneBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            var original = SceneManager.GetActiveScene();
            var scene = SceneManager.CreateScene("YL84-" + Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(scene);
            using var pending = system.InstantiateAsync(address);
            SceneManager.SetActiveScene(original);
            yield return Wait(pending);
            var moved = pending.TakeResult();
            Assert.That(moved.GameObject.scene, Is.EqualTo(scene), "Async checkout uses its captured destination, not the later active scene.");
            var cache = system.GetInstanceCache(address, InstanceCachePolicy.SceneScoped, scene);
            Assert.That(cache.Prewarm(1), Is.EqualTo(1));
            var idle = CacheData(cache).PoolQueue.Single();
            var ddol = cache.Instantiate();
            Assert.That(ddol.GameObject.scene, Is.EqualTo(scene), "Cached checkout must use the explicit scope scene before OnEnable, not the current active scene.");
            var texture = ddol.GameObject.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            var sceneBound = cache.Instantiate();
            var sceneObject = sceneBound.GameObject;
            UnityEngine.Object.DontDestroyOnLoad(ddol.GameObject);
            SceneManager.MoveGameObjectToScene(moved.GameObject, original);
            cache.Prewarm(1);
            idle = CacheData(cache).PoolQueue.Single();
            var persistent = system.GetInstanceCache(address, InstanceCachePolicy.Persistent);
            persistent.Prewarm(1);
            var persistentIdle = CacheData(persistent).PoolQueue.Single();
            var unload = SceneManager.UnloadSceneAsync(scene);
            while (!unload.isDone)
            {
                yield return null;
            }

            Assert.That(cache.IsRetired, Is.True);
            Assert.That(idle == null, Is.True);
            Assert.That(sceneObject == null, Is.True);
            Assert.That(sceneBound.IsValid, Is.False);
            sceneBound.Return();
            Assert.That(persistentIdle == null, Is.False);
            Assert.That(moved.IsValid && ddol.IsValid, Is.True);
            var fresh = system.Instantiate(address);
            Assert.That(fresh.GameObject, Is.Not.SameAs(moved.GameObject));
            moved.Return();
            ddol.Return();
            yield return null;
            Assert.That(fresh.IsValid, Is.True);
            Assert.That(texture == null, Is.False);
            fresh.Discard();
            persistent.Dispose();
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);
        }

        private static IEnumerator InstanceEndSessionBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var address = Address(snapshot, "A.prefab");
            var external = system.Acquire<GameObject>(address);
            var texture = external.Asset.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            var instance = system.Instantiate(external);
            var obj = instance.GameObject;
            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            external.Dispose();
            instance.Return();
            Assert.That(texture == null, Is.False, "Stale public AssetLease/InstanceLease cannot subtract the Destroy-pending pin.");
            typeof(ResourceSystem).GetMethod("ResetSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            using (var blocked = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog() })))
            {
                Assert.That(blocked.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            yield return InstancesDrained(system);
            Assert.That(obj == null, Is.True);
            Assert.That(texture == null, Is.True);
            var next = Initialize(snapshot);
            var current = next.Instantiate(address);
            instance.Discard();
            external.Dispose();
            Assert.That(current.IsValid, Is.True);
            current.Discard();
            next.ClearInstanceCaches();
            yield return InstancesDrained(next);
            InstanceLifecycleProbe.OnAwake = created =>
            {
                InstanceLifecycleProbe.OnAwake = null;
                obj = created;
                texture = created.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
                typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                Assert.That(texture == null, Is.False);
            };
            Assert.Throws<ResourceException>(() => next.Instantiate(address));
            Assert.That(texture == null, Is.False);
            typeof(ResourceSystem).GetMethod("ResetSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            yield return InstancesDrained(next);
            Assert.That(obj == null, Is.True);
            Assert.That(texture == null, Is.True);
        }

        private static ResourceSystem _exitingInstanceSystem;
        private static InstanceLease _exitingInstance;
        private static ResourceRequest<InstanceLease> _exitingInstanceRequest;

        private static void StartActualInstanceExit()
        {
            var snapshot = Snapshot();
            _exitingInstanceSystem = Initialize(snapshot);
            _exitingInstance = _exitingInstanceSystem.Instantiate(Address(snapshot, "A.prefab"));
            _exitingInstanceSystem.GetInstanceCache(Address(snapshot, "A.prefab")).Prewarm(1);
            _exitingInstanceRequest = _exitingInstanceSystem.InstantiateAsync(Address(snapshot, "B.prefab"));
            Assert.That(_exitingInstanceRequest.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            SessionState.SetBool(Key + "instance-quit", false);
            Application.quitting += OnActualInstanceQuit;
        }

        private static void OnActualInstanceQuit()
        {
            SessionState.SetBool(Key + "instance-quit", _exitingInstanceSystem.State == ResourceSystemState.Invalidated &&
                !_exitingInstance.IsValid && _exitingInstanceRequest.IsDisposed);
            Application.quitting -= OnActualInstanceQuit;
        }

        private static IEnumerator AfterActualInstanceExit()
        {
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var lease = system.Instantiate(Address(snapshot, "A.prefab"));
            Assert.That(lease.IsValid, Is.True);
            lease.Discard();
            system.ClearInstanceCaches();
            yield return InstancesDrained(system);
        }

        [UnityTest]
        public IEnumerator CompleteNative84InstanceLifecycleRegression()
        {
            yield return new EnterPlayMode();
            yield return InstanceBaselineBody();
            yield return CloseCurrent();
            yield return InstanceCapacityBody();
            yield return CloseCurrent();
            yield return InstanceExternalDestroyBody();
            yield return CloseCurrent();
            yield return EmptyInstanceCacheShutdownBody();
            yield return CloseCurrent();
            yield return DestroyedInstanceCacheRootBody();
            yield return CloseCurrent();
            yield return InstanceReentryBody();
            yield return CloseCurrent();
            yield return FailedImplicitInstanceCacheBody();
            yield return CloseCurrent();
            yield return InstanceAsyncBody();
            yield return CloseCurrent();
            yield return InstanceSceneBody();
            yield return CloseCurrent();
            yield return InstanceEndSessionBody();
            yield return CloseCurrent();
            StartActualInstanceExit();
            yield return new ExitPlayMode();
            Assert.That(SessionState.GetBool(Key + "instance-quit", false), Is.True);
            SessionState.EraseBool(Key + "instance-quit");
            yield return new EnterPlayMode();
            yield return AfterActualInstanceExit();
            yield return CloseCurrent();
        }
    }
}
