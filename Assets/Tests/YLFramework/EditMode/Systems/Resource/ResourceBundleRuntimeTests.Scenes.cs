using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceBundleRuntimeTests
    {
        private static void AddScene85Fixture(ResourceReleaseFixture fixture)
        {
            var folder = fixture.Folder + "/Content";
            var scene = EditorSceneManager.OpenScene(folder + "/Owned.unity", OpenSceneMode.Additive);
            try
            {
                scene.GetRootGameObjects().Single().AddComponent<InstanceLifecycleProbe>();
                Assert.That(EditorSceneManager.SaveScene(scene), Is.True);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.CreateFolder(folder, "Other");
            var other = folder + "/Other/Alternate.unity";
            Assert.That(AssetDatabase.CopyAsset(folder + "/Owned.unity", other), Is.True);
            fixture.Project.groups.Add(new ResourceGroup
            {
                id = "scene85-other",
                selections = new List<ResourceSelection>
                { new() { guid = AssetDatabase.AssetPathToGUID(other) } }
            });
            EditorUtility.SetDirty(fixture.Project);
            AssetDatabase.SaveAssetIfDirty(fixture.Project);
        }

        private static IEnumerator UntilScene85(Func<bool> condition)
        {
            var until = Time.realtimeSinceStartup + 15;
            while (!condition() && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "Scene operation must reach its real terminal/cleanup state.");
        }

        private static IEnumerator Scene85Drained(ResourceSystem system)
        {
            yield return UntilScene85(() => system.GetSceneRecords().Count == 0 && system.GetDiagnosticsSnapshot().BundleRecordCount == 0);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }

        private static IEnumerator DrainOwnedScene85Fixture()
        {
            var folder = SessionState.GetString(Key + "folder", "");
            if (string.IsNullOrEmpty(folder))
            {
                yield break;
            }

            var unloading = new HashSet<int>();
            var until = Time.realtimeSinceStartup + 15;
            do
            {
                var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                    .Where(x => x.isLoaded && x.path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var scene in scenes)
                {
                    if (!unloading.Add(scene.handle))
                    {
                        continue;
                    }

                    if (SceneManager.sceneCount <= 1)
                    {
                        SceneManager.CreateScene("YL85-Cleanup-" + Guid.NewGuid().ToString("N"));
                    }

                    SceneManager.UnloadSceneAsync(scene);
                }
                if (!AssetBundle.GetAllLoadedAssetBundles().Any())
                {
                    yield break;
                }

                yield return null;
            } while (Time.realtimeSinceStartup < until);
        }

        [UnityTest]
        public IEnumerator RealSceneLeaseReleaseDoesNotUnloadOrDestroyItsBundleDependencies()
        {
            yield return new EnterPlayMode();
            yield return Scene85OwnershipBody();
        }

        private static IEnumerator Scene85OwnershipBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var request = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            yield return Wait(request);
            using var lease = request.TakeResult();
            Assert.Throws<InvalidOperationException>(() => request.TakeResult());
            var scene = lease.Scene;
            var texture = scene.GetRootGameObjects().Single().GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            var record = lease.Record;
            Assert.That(record.Handle, Is.EqualTo((int)scene.handle));
            Assert.That(record.LoadSequence, Is.EqualTo(request.LoadSequence));
            Assert.That(record.CatalogDigest, Is.EqualTo(snapshot.Document.CatalogDigest));
            lease.Dispose();
            request.Dispose();
            Assert.Throws<ObjectDisposedException>(() => request.TakeResult());
            Assert.Throws<ObjectDisposedException>(() => system.UnloadSceneAsync(lease));
            yield return null;
            Assert.That(scene.isLoaded, Is.True);
            Assert.That(texture == null, Is.False, "Caller ownership is not Scene ownership.");
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.GreaterThan(0));
            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            InstanceLifecycleProbe.OnDestroyed = _ => Assert.That(texture == null, Is.False, "Scene dependencies must survive OnDestroy.");
            var unload = SceneManager.UnloadSceneAsync(scene);
            while (!unload.isDone)
            {
                yield return null;
            }

            InstanceLifecycleProbe.ResetCallbacks();
            yield return Scene85Drained(system);
            Assert.That(texture == null, Is.True);
            Assert.That(record.IsUnloaded, Is.True);
        }

        private static IEnumerator Scene85IdentityBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var first = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            using var second = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            using var other = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Additive);
            yield return Wait(first);
            using var a = first.TakeResult();
            yield return Wait(second);
            using var b = second.TakeResult();
            yield return Wait(other);
            var otherRecord = other.Scenes.Single();
            other.Dispose(); // 未领取只释放 caller，不卸载 scene。
            Assert.That(otherRecord.IsLoaded, Is.True);
            Assert.That(a.Scene.name, Is.EqualTo(b.Scene.name));
            Assert.That(otherRecord.Path, Is.Not.EqualTo(a.Record.Path));
            Assert.That(new[] { (int)a.Scene.handle, (int)b.Scene.handle, otherRecord.Handle }.Distinct().Count(), Is.EqualTo(3));
            Assert.That(a.Record.LoadSequence, Is.Not.EqualTo(b.Record.LoadSequence));
            Assert.That(a.Record.SnapshotId, Is.EqualTo(b.Record.SnapshotId));
            var old = a.Record;
            var bTexture = b.Scene.GetRootGameObjects().Single().GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            using var unloadA = system.UnloadSceneAsync(a);
            using var joinA = system.UnloadSceneAsync(a);
            yield return Wait(unloadA);
            yield return Wait(joinA);
            Assert.That(unloadA.TakeResult() && joinA.TakeResult(), Is.True);
            Assert.That(a.IsValid, Is.False);
            Assert.That(b.IsValid && otherRecord.IsLoaded, Is.True);
            Assert.That(bTexture == null, Is.False);
            using var fresh = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            yield return Wait(fresh);
            using var c = fresh.TakeResult();
            using (var stale = system.UnloadSceneAsync(old))
            {
                Assert.That(stale.Error.Kind, Is.EqualTo(ResourceError.SceneUnloaded));
            }

            Assert.That(c.IsValid && b.IsValid, Is.True);
            using var unloadB = system.UnloadSceneAsync(b);
            using var unloadC = system.UnloadSceneAsync(c);
            using var unloadOther = system.UnloadSceneAsync(otherRecord);
            yield return Wait(unloadB);
            yield return Wait(unloadC);
            yield return Wait(unloadOther);
            yield return Scene85Drained(system);
        }

        private static IEnumerator Scene85CancellationBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using (var missing = system.LoadSceneAsync(Address(snapshot, "missing.unity")))
            {
                Assert.That(missing.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            }

            using (var wrong = system.LoadSceneAsync(Address(snapshot, "entry.txt")))
            {
                Assert.That(wrong.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            }

            using (var early = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive))
            {
                Assert.That(early.HasNativeStarted, Is.False);
                early.Cancel();
                Assert.That(early.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                Assert.That(early.HasNativeStarted, Is.False);
            }
            yield return Scene85Drained(system);
            var id = snapshot.Document.Assets.Single(x => x.Address == "Owned.unity").BundleId;
            using var prepared = system.AcquireBundle(snapshot.Document.ProjectId, id);
            using var gated = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive, false);
            Assert.That(gated.HasNativeStarted, Is.True);
            using var queued = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            queued.Dispose();
            Assert.That(queued.HasNativeStarted, Is.False);
            yield return UntilScene85(() => gated.IsWaitingForActivation);
            var native = gated.Native;
            prepared.Dispose();
            Assert.That(system.GetDiagnosticsSnapshot().BundleRootPinCount, Is.GreaterThan(0));
            Assert.That(native.isDone, Is.False);
            gated.Cancel();
            Assert.That(native.allowSceneActivation, Is.True);
            Assert.That(gated.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            yield return Scene85Drained(system);
            Assert.That(gated.IsNativeCompleted, Is.True);
            Assert.That(gated.Scenes.Single().IsUnloaded, Is.True);
            Assert.That(queued.HasNativeStarted, Is.False);

            using var automatic = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive, false, 0.25f);
            yield return Wait(automatic);
            Assert.That(automatic.Error.Kind, Is.EqualTo(ResourceError.ActivationTimedOut));
            yield return Scene85Drained(system);
            Assert.That(automatic.IsNativeCompleted, Is.True);

            using var activated = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive, false);
            yield return UntilScene85(() => activated.IsWaitingForActivation);
            activated.Activate();
            activated.Activate();
            yield return Wait(activated);
            using var activeLease = activated.TakeResult();
            using var gate = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Additive, false);
            yield return UntilScene85(() => gate.IsWaitingForActivation);
            using (var blocked = system.UnloadSceneAsync(activeLease))
            {
                Assert.That(blocked.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            using var successor = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Additive);
            gate.Dispose();
            successor.Completed += result =>
            {
                Assert.That(result.TakeResult().IsValid, Is.True);
                throw new InvalidOperationException("scene85-observer");
            };
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: scene85-observer");
            yield return Wait(successor);
            yield return UntilScene85(() => gate.IsNativeCompleted && gate.Scenes.Count == 1 && gate.Scenes[0].IsUnloaded);
            foreach (var record in system.GetSceneRecords().ToArray())
            {
                using var unload = system.UnloadSceneAsync(record);
                yield return Wait(unload);
            }
            yield return Scene85Drained(system);
        }

        private static IEnumerator Scene85SingleBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var oldRequest = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Additive);
            yield return Wait(oldRequest);
            using var old = oldRequest.TakeResult();
            var oldRecord = old.Record;
            using var replacement = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Single, false);
            yield return UntilScene85(() => replacement.IsWaitingForActivation);
            Assert.That(old.IsValid, Is.True);
            replacement.Cancel();
            yield return UntilScene85(() => replacement.Scenes.Count == 1);
            Assert.That(replacement.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(replacement.HasNativeStarted && replacement.IsNativeCompleted, Is.True);
            Assert.That(old.IsValid, Is.False, "Canceled Single cannot pretend the previous Scene was restored.");
            yield return UntilScene85(() => oldRecord.IsUnloaded);
            var record = replacement.Scenes.Single();
            var texture = record.Scene.GetRootGameObjects().Single().GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            replacement.Dispose();
            Assert.That(record.IsLoaded, Is.True);
            using (var last = system.UnloadSceneAsync(record))
            {
                Assert.That(last.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            Assert.That(system.IsAvailable && texture, Is.True);
            using var single = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Single);
            yield return Wait(single);
            using var singleLease = single.TakeResult();
            Assert.That(singleLease.IsValid, Is.True);
            yield return UntilScene85(() => record.IsUnloaded);
            var neutral = SceneManager.CreateScene("YL85-Neutral-" + Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(neutral);
            using var unload = system.UnloadSceneAsync(singleLease);
            yield return Wait(unload);
            yield return Scene85Drained(system);
        }

        private static IEnumerator Scene85ExternalAmbiguityBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var descriptor = snapshot.Document.Assets.Single(x => x.Address == "Owned.unity");
            using var prepared = system.AcquireBundle(snapshot.Document.ProjectId, descriptor.BundleId);
            using var request = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive, false);
            yield return UntilScene85(() => request.IsWaitingForActivation);
            var issued = false;
            void Interfere(Scene scene, LoadSceneMode mode)
            {
                if (issued || !string.Equals(scene.path, descriptor.LoadName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                issued = true;
                SceneManager.LoadScene(descriptor.LoadName, LoadSceneMode.Additive);
            }
            SceneManager.sceneLoaded += Interfere;
            try
            {
                request.Activate();
                yield return Wait(request);
            }
            finally
            {
                SceneManager.sceneLoaded -= Interfere;
            }
            Assert.That(issued, Is.True);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(request.Error.Kind, Is.EqualTo(ResourceError.SceneIdentityAmbiguous));
            Assert.That(request.Scenes.Count, Is.EqualTo(2));
            Assert.That(request.Scenes.All(x => x.IsLoaded && x.IsAmbiguous), Is.True);
            prepared.Dispose();
            Assert.That(system.GetDiagnosticsSnapshot().BundleRootPinCount, Is.EqualTo(2));
            using (var rejected = system.UnloadSceneAsync(request.Scenes[0]))
            {
                Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.SceneIdentityAmbiguous));
            }

            foreach (var candidate in request.Scenes)
            {
                var unload = SceneManager.UnloadSceneAsync(candidate.Scene);
                while (!unload.isDone)
                {
                    yield return null;
                }
            }
            yield return Scene85Drained(system);
        }

        [UnityTest]
        public IEnumerator ExternalSceneLoadedBeforeNativeStartIsExcludedFromTheIdentityWindow()
        {
            yield return new EnterPlayMode();
            yield return Scene85PreparationWindowBody();
        }

        private static IEnumerator Scene85PreparationWindowBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var descriptor = snapshot.Document.Assets.Single(x => x.Address == "Owned.unity");
            SceneRequest request = null;
            var external = default(Scene);
            void Observe(Scene scene, LoadSceneMode mode)
            {
                if (request != null && !request.HasNativeStarted && string.Equals(scene.path, descriptor.LoadName, StringComparison.OrdinalIgnoreCase))
                {
                    external = scene;
                }
            }
            SceneManager.sceneLoaded += Observe;
            try
            {
                var earlier = EditorSceneManager.LoadSceneAsyncInPlayMode(descriptor.LoadName, new LoadSceneParameters(LoadSceneMode.Additive));
                request = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
                yield return Wait(request);
                Assert.That(earlier.isDone && external.IsValid() && external.isLoaded, Is.True, "The external Scene must really finish inside Bundle preparation, before this native operation.");
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), "A Scene already present in the native Before snapshot is not an ambiguous new candidate.");
                using var lease = request.TakeResult();
                Assert.That(lease.Scene.handle, Is.Not.EqualTo(external.handle));
                Assert.That(request.Scenes.Count, Is.EqualTo(1));
                using var unload = system.UnloadSceneAsync(lease);
                yield return Wait(unload);
                Assert.That(external.isLoaded, Is.True, "The external owner retains its pre-existing Scene.");
                var outside = SceneManager.UnloadSceneAsync(external);
                while (!outside.isDone)
                {
                    yield return null;
                }
            }
            finally
            {
                SceneManager.sceneLoaded -= Observe;
                request?.Dispose();
            }
            yield return Scene85Drained(system);
        }

        private static IEnumerator Scene85InstancesBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var pending = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            yield return Wait(pending);
            using var loaded = pending.TakeResult();
            var neutral = SceneManager.GetActiveScene();
            var cache = system.GetInstanceCache(Address(snapshot, "A.prefab"), InstanceCachePolicy.SceneScoped, loaded.Scene);
            cache.Prewarm(1);
            var moved = cache.Instantiate();
            var ddol = cache.Instantiate();
            var sceneBound = cache.Instantiate();
            var bound = sceneBound.GameObject;
            cache.Prewarm(1);
            var idle = CacheData(cache).PoolQueue.Single();
            var texture = moved.GameObject.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            SceneManager.MoveGameObjectToScene(moved.GameObject, neutral);
            UnityEngine.Object.DontDestroyOnLoad(ddol.GameObject);
            using var unloading = system.UnloadSceneAsync(loaded);
            yield return Wait(unloading);
            Assert.That(cache.IsRetired, Is.True);
            Assert.That(idle == null && bound == null, Is.True);
            Assert.That(sceneBound.IsValid, Is.False);
            sceneBound.Dispose();
            Assert.That(moved.IsValid && ddol.IsValid && texture, Is.True);
            var fresh = system.Instantiate(Address(snapshot, "A.prefab"));
            moved.Return();
            ddol.Return();
            fresh.Discard();
            system.ClearInstanceCaches();
            yield return Scene85Drained(system);
            Assert.That(texture == null, Is.True);
        }

        private static IEnumerator Scene85EditorBody()
        {
            var snapshot = Snapshot();
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(SessionState.GetString(Key + "folder", "") + "/Project.asset");
            var simulation = new ResourceEditorSimulation(new[] { project }, new[] { snapshot.Document });
            using var init = ResourceSystem.InitializeAsync(simulation.CreateConfiguration());
            var system = init.TakeResult();
            using var scope = system.CreateScope();
            var request = scope.Track(system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive));
            yield return Wait(request);
            var lease = scope.TakeResult(request);
            var record = lease.Record;
            var borrowed = AssetDatabase.LoadAssetAtPath<Material>(SessionState.GetString(Key + "folder", "") + "/Content/SceneMaterial.mat");
            Assert.That(record.CatalogDigest, Is.Null);
            scope.Dispose();
            Assert.That(record.IsLoaded, Is.True);
            using var unload = system.UnloadSceneAsync(record);
            yield return Wait(unload);
            Assert.That(borrowed == null, Is.False);
            yield return Scene85Drained(system);
        }

        private static IEnumerator Scene85EndedSessionBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var id = snapshot.Document.Assets.Single(x => x.Address == "Owned.unity").BundleId;
            using var prepared = system.AcquireBundle(snapshot.Document.ProjectId, id);
            using var request = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive, false);
            yield return UntilScene85(() => request.IsWaitingForActivation);
            var native = request.Native;
            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(request.IsDisposed && native.allowSceneActivation, Is.True);
            typeof(ResourceSystem).GetMethod("ResetSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            using (var blocked = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog() })))
            {
                Assert.That(blocked.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            yield return Scene85Drained(system);
            var next = Initialize(snapshot);
            using var reloaded = next.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
            yield return Wait(reloaded);
            using var lease = reloaded.TakeResult();
            request.Dispose();
            Assert.That(lease.IsValid, Is.True);
            using var unload = next.UnloadSceneAsync(lease);
            yield return Wait(unload);
            yield return Scene85Drained(next);
        }

        private static IEnumerator Scene85FailureBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var id = snapshot.Document.Assets.Single(x => x.Address == "Owned.unity").BundleId;
            var file = Path.Combine(snapshot.DirectoryPath, snapshot.Document.Bundles.Single(x => x.LogicalId == id).RelativeFile);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file));
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file), new byte[] { 0, 1 });
                using var failure = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Additive);
                using var next = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Additive);
                yield return Wait(failure);
                Assert.That(failure.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(failure.HasNativeStarted, Is.False);
                yield return Wait(next);
                using var loaded = next.TakeResult();
                using var unload = system.UnloadSceneAsync(loaded);
                yield return Wait(unload);
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file), bytes);
            }
            yield return Scene85Drained(system);
        }

        [UnityTest]
        public IEnumerator ConcurrentUnloadsReserveTheLastSceneBeforeNativeDestruction()
        {
            yield return new EnterPlayMode();
            yield return Scene85LastReservationBody();
        }

        private static IEnumerator Scene85LastReservationBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var first = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Single);
            yield return Wait(first);
            using var a = first.TakeResult();
            using var second = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Additive);
            yield return Wait(second);
            using var b = second.TakeResult();
            using var unload = system.UnloadSceneAsync(a);
            using var last = system.UnloadSceneAsync(b);
            Assert.That(last.Status, Is.EqualTo(ResourceRequestStatus.Failed), "In-flight unloads already consume their Scene slots.");
            Assert.That(last.Error.Kind, Is.EqualTo(ResourceError.Busy));
            yield return Wait(unload);
            Assert.That(b.IsValid, Is.True);
            SceneManager.SetActiveScene(SceneManager.CreateScene("YL85-Reserved-Neutral-" + Guid.NewGuid().ToString("N")));
            using var finish = system.UnloadSceneAsync(b);
            yield return Wait(finish);
            yield return Scene85Drained(system);
        }

        private static SceneLease _exitingScene85Lease;
        private static SceneRequest _exitingScene85Pending;
        private static void OnActualScene85Quit()
        {
            SessionState.SetBool(Key + "scene85-quit", !_exitingScene85Lease.IsValid && _exitingScene85Pending.IsDisposed &&
                _exitingScene85Pending.Status == ResourceRequestStatus.Invalidated && _exitingScene85Pending.Native.allowSceneActivation);
            Application.quitting -= OnActualScene85Quit;
        }

        private static IEnumerator StartActualScene85Exit()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var single = system.LoadSceneAsync(Address(snapshot, "Owned.unity"), LoadSceneMode.Single);
            yield return Wait(single);
            _exitingScene85Lease = single.TakeResult();
            _exitingScene85Pending = system.LoadSceneAsync(Address(snapshot, "Other/Alternate.unity"), LoadSceneMode.Additive, false);
            yield return UntilScene85(() => _exitingScene85Pending.IsWaitingForActivation);
            SessionState.SetBool(Key + "scene85-quit", false);
            Application.quitting += OnActualScene85Quit;
        }

        [UnityTest]
        public IEnumerator CompleteNative85SceneLifecycleRegression()
        {
            yield return new EnterPlayMode();
            yield return Scene85OwnershipBody();
            yield return CloseCurrent();
            yield return Scene85IdentityBody();
            yield return CloseCurrent();
            yield return Scene85CancellationBody();
            yield return CloseCurrent();
            yield return Scene85SingleBody();
            yield return CloseCurrent();
            yield return Scene85ExternalAmbiguityBody();
            yield return CloseCurrent();
            yield return Scene85PreparationWindowBody();
            yield return CloseCurrent();
            yield return Scene85InstancesBody();
            yield return CloseCurrent();
            yield return Scene85EditorBody();
            yield return CloseCurrent();
            yield return Scene85EndedSessionBody();
            yield return CloseCurrent();
            yield return Scene85FailureBody();
            yield return CloseCurrent();
            yield return Scene85LastReservationBody();
            yield return CloseCurrent();
            yield return StartActualScene85Exit();
            yield return new ExitPlayMode();
            Assert.That(SessionState.GetBool(Key + "scene85-quit", false), Is.True, "Actual exit must invalidate caller identities and release the native activation gate.");
            SessionState.EraseBool(Key + "scene85-quit");
            yield return new EnterPlayMode();
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero, "Old native Scene and Bundle identities must drain before the next session.");
            yield return Scene85OwnershipBody();
            yield return CloseCurrent();
        }
    }
}
