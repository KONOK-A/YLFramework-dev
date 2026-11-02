using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceUpdateTests
    {
        private static T Take<T>(ResourceRequest<T> request)
        {
            using (request)
            {
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
                return request.TakeResult();
            }
        }

        private static IEnumerator Initialize(ArtifactStore store)
        {
            using var request = ResourceSystem.InitializeAsync(new ResourceConfiguration(store));
            yield return Wait(request);
            var system = Take(request);
            Assert.That(system, Is.Not.Null, "Successful initialization must return the unique resource authority.");
            Assert.That(ResourceSystem.Current, Is.SameAs(system));
        }
        private static ResourceAddress Address(string path = "entry.txt", string project = "a") => new(Snapshot(project).Document.ProjectId, path);
        private static IEnumerator Succeed(ResourceRequest<bool> request)
        {
            using (request)
            {
                yield return Wait(request);
                Assert.That(Take(request), Is.True);
            }
        }

        private static IEnumerator Busy(ResourceRequest<bool> request, string phase = "activation")
        {
            using (request)
            {
                var before = ResourceSystem.Current.GetDiagnosticsReport();
                yield return Wait(request);
                Assert.That(request.Error?.Kind, Is.EqualTo(ResourceError.Busy),
                    phase + ": " + request.Status + "; " + request.Error + "\nbefore-wait=" + Newtonsoft.Json.JsonConvert.SerializeObject(before) +
                    "\nafter-wait=" + Newtonsoft.Json.JsonConvert.SerializeObject(ResourceSystem.Current.GetDiagnosticsReport()));
            }

            Assert.That(ResourceSystem.Current.IsAvailable, Is.True);
            using var old = ResourceSystem.Current.Acquire<TextAsset>(Address());
            Assert.That(old.Asset.text, Is.EqualTo("first release content"));
        }

        private static IEnumerator Wait<T>(ResourceRequest<T> request)
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (!request.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True, "A native operation did not settle within the fixture deadline.");
        }

        private static IEnumerator WaitForUpdates(ResourceSystem system)
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while ((system.GetDiagnosticsReport().Updates.Count != 0 || system.GetContentDiagnosticsSnapshot().WorkCount != 0) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(system.GetDiagnosticsReport().Updates, Is.Empty, "The update producer did not retire within the fixture deadline.");
            Assert.That(system.GetContentDiagnosticsSnapshot().WorkCount, Is.Zero);
            Assert.That(system.GetContentDiagnosticsSnapshot().RetainedCleanupOwners, Is.Zero);
        }

        private static IEnumerator Drain(ResourceSystem system)
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (system.GetDiagnosticsSnapshot().BundleRecordCount != 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator NativeAssetInstanceSceneAndCanceledOperationBlockUntilNeutralBoundaryThenLoadNewVersionAndRollback()
        {
            yield return new EnterPlayMode();
            yield return VerifyNativeGates();
        }

        // 捕获提交见证的闭包只在进入 Play 后创建，不跨 Domain Reload 序列化。
        private static IEnumerator VerifyNativeGates()
        {
            var store = NewStore();
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            PreparedUpdate update;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 2) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                update = Take(preparation);
            }
            var oldLease = system.Acquire<TextAsset>(Address());
            var oldObject = oldLease.Asset;
            yield return Busy(system.ActivatePreparedUpdateAsync(update), "asset-live");
            oldLease.Dispose();
            Assert.That(oldObject == null, Is.True, "The old native asset must actually be destroyed before switching generations.");

            var instance = system.Instantiate(Address("A.prefab"));
            var liveObject = instance.GameObject;
            yield return Busy(system.ActivatePreparedUpdateAsync(update), "instance-live");
            Assert.That(liveObject, Is.Not.Null, "Activation cannot destroy an external consumer's checkout.");
            instance.Return();
            yield return Drain(system);
            Assert.That(liveObject == null, Is.True);

            using var idle = system.Instantiate(Address("A.prefab"));
            var idleObject = idle.GameObject;
            idle.Return();
            var openCache = system.GetDiagnosticsReport().InstanceCaches.Single(cache => !cache.IsRetired);
            Assert.That(openCache.IdleCount, Is.GreaterThan(0), "Keep a real idle instance until the activation barrier retires its cache.");
            Assert.That(idleObject != null, Is.True);
            yield return Busy(system.ActivatePreparedUpdateAsync(update), "instance-idle-retirement");
            yield return Drain(system);
            Assert.That(idleObject == null, Is.True, "The barrier-retired idle instance must really die before retry.");

            using var sceneRequest = system.LoadSceneAsync(Address("Owned.unity"), LoadSceneMode.Additive);
            yield return Wait(sceneRequest);
            var sceneLease = sceneRequest.TakeResult();
            var scene = sceneLease.Scene;
            sceneLease.Dispose();
            yield return Busy(system.ActivatePreparedUpdateAsync(update), "scene-loaded");
            Assert.That(scene.isLoaded, Is.True);
            var unload = SceneManager.UnloadSceneAsync(scene);
            Assert.That(unload, Is.Not.Null);
            var unloadDeadline = Time.realtimeSinceStartup + 10f;
            while (!unload.isDone && Time.realtimeSinceStartup < unloadDeadline)
            {
                yield return null;
            }
            Assert.That(unload.isDone, Is.True, "The same native scene unload must complete before draining ownership.");
            yield return Drain(system);

            var provider = system.BindCoreResourceProvider();
            using (var prefab = provider.AcquirePrefab(Snapshot().Document.ProjectId, "A.prefab"))
            {
                yield return Busy(system.ActivatePreparedUpdateAsync(update), "core-prefab");
            }

            yield return Drain(system);
            yield return Succeed(system.ActivatePreparedUpdateAsync(update));
            Assert.That(system.GetActiveCatalogDigest(Snapshot().Document.ProjectId), Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v2"));
            }

            var active = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store)));
            store.Checkpoint = _ => Assert.Fail("already-active cannot perform another persistent transaction.");
            yield return Succeed(system.ActivatePreparedUpdateAsync(update));
            CollectionAssert.AreEqual(active, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))));
            store.Checkpoint = null;
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                using var busy = system.RollbackAsync();
                yield return Wait(busy);
                Assert.That(busy.Error?.Kind, Is.EqualTo(ResourceError.Busy));
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v2"));
            }
            yield return Succeed(system.RollbackAsync());
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("first release content"));
            }

            yield return Drain(system);
            PreparedUpdate canceledUpdate;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 2) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                canceledUpdate = Take(preparation);
            }

            var loadsBefore = system.GetDiagnosticsSnapshot().NativeBundleLoadCount;
            using var native = system.AcquireAsync<GameObject>(Address("B.prefab"));
            var deadline = Time.realtimeSinceStartup + 10;
            while (!system.GetDiagnosticsReport().Bundles.Any(bundle => bundle.IsNativePending) &&
                !native.IsDone && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(system.GetDiagnosticsReport().Bundles.Any(bundle => bundle.IsNativePending), Is.True,
                "The cancellation witness must start a real native Bundle operation, not only content preparation.");
            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.GreaterThan(loadsBefore));
            native.Cancel();
            Assert.That(native.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(system.GetDiagnosticsReport().Bundles.Any(bundle => bundle.IsNativePending), Is.True,
                "Cancel must retain the native responsibility until the real operation settles.");

            ResourceDiagnosticsReport boundary = null;
            string[] nativeAtBoundary = null;
            var knownNames = new[] { Snapshot(), Snapshot("a", 2), Snapshot("a", 3) }
                .SelectMany(snapshot => snapshot.Document.Bundles.Select(bundle => bundle.NativeName)).ToHashSet(StringComparer.Ordinal);
            store.Checkpoint = stage =>
            {
                if (stage == "JournalVerified")
                {
                    boundary = system.GetDiagnosticsReport();
                    nativeAtBoundary = AssetBundle.GetAllLoadedAssetBundles()
                        .Where(bundle => bundle && knownNames.Contains(bundle.name)).Select(bundle => bundle.name).ToArray();
                }
            };
            using (var activation = system.ActivatePreparedUpdateAsync(canceledUpdate))
            {
                yield return Wait(activation);
                // 验证期间 native 可以真实结算；提交只能发生在中立边界，而不是必须返回过时的 Busy。
                if (boundary == null)
                {
                    Assert.That(activation.Error?.Kind, Is.EqualTo(ResourceError.Busy));
                    Assert.That(system.GetActiveCatalogDigest(Snapshot().Document.ProjectId), Is.EqualTo(Snapshot().Document.CatalogDigest));
                }
                else
                {
                    Assert.That(activation.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), activation.Error?.ToString());
                    Assert.That(boundary.Summary.RecordCount, Is.Zero);
                    Assert.That(boundary.Summary.LeaseCount, Is.Zero);
                    Assert.That(boundary.Summary.BundleRecordCount, Is.Zero);
                    Assert.That(boundary.Summary.BundleRootPinCount, Is.Zero);
                    Assert.That(boundary.InstanceCaches, Is.Empty);
                    Assert.That(boundary.Scenes, Is.Empty);
                    Assert.That(nativeAtBoundary, Is.Empty, "No project-native namespace may survive a successful commit boundary.");
                }
            }
            store.Checkpoint = null;
            yield return Drain(system);
            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.EqualTo(system.GetDiagnosticsSnapshot().NativeBundleUnloadCount));
            yield return Succeed(system.ActivatePreparedUpdateAsync(canceledUpdate));
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v2"));
            }
            yield return Succeed(system.RollbackAsync());
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("first release content"));
            }
            yield return Succeed(system.ShutdownAsync());
        }

        [UnityTest]
        public IEnumerator CommitFailureRestoresOldAcquireAndPostDecisionFailureReconcilesNewMemoryAndDisk()
        {
            yield return new EnterPlayMode();
            yield return VerifyCommitRecovery();
        }

        // 不让 TestRunner 跨 Domain Reload 序列化编译器生成的捕获闭包。
        private static IEnumerator VerifyCommitRecovery()
        {
            var store = NewStore();
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            PreparedUpdate update;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 2) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                update = Take(preparation);
            }
            var hit = false;
            var blocked = new System.Collections.Generic.Dictionary<string, ResourceError?>();
            store.Checkpoint = stage =>
            {
                if (stage != "JournalVerified" || hit)
                {
                    return;
                }

                hit = true;
                using var asset = system.AcquireAsync<TextAsset>(Address());
                blocked["asset"] = asset.Error?.Kind;
                using var scene = system.LoadSceneAsync(Address("Owned.unity"), LoadSceneMode.Additive);
                blocked["scene"] = scene.Error?.Kind;
                using var group = system.AcquireGroupAsync(Snapshot().Document.ProjectId, "entry");
                blocked["group"] = group.Error?.Kind;
                using var reentry = system.ActivatePreparedUpdateAsync(update);
                blocked["reentry"] = reentry.Error?.Kind;
                using var shutdown = system.ShutdownAsync();
                blocked["shutdown"] = shutdown.Error?.Kind;
                throw new IOException("injected pre-commit interruption");
            };
            using (var failed = system.ActivatePreparedUpdateAsync(update))
            {
                yield return Wait(failed);
                Assert.That(failed.Error?.Kind, Is.EqualTo(ResourceError.UpdateFailed));
            }

            Assert.That(hit, Is.True);
            Assert.That(blocked.Count, Is.EqualTo(5));
            foreach (var pair in blocked)
            {
                Assert.That(pair.Value, Is.EqualTo(ResourceError.Busy), pair.Key);
            }

            using (var old = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(old.Asset.text, Is.EqualTo("first release content"));
            }

            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False);
            hit = false;
            store.Checkpoint = stage =>
            {
                if (stage == "CommitDecided" && !hit)
                {
                    hit = true;
                    throw new IOException("injected post-decision interruption");
                }
            };
            yield return Succeed(system.ActivatePreparedUpdateAsync(update));
            Assert.That(hit, Is.True);
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v2"));
            }

            Assert.That(Store(store.RootPath).Mount().Single().Document.CatalogDigest, Is.EqualTo(system.GetActiveCatalogDigest(Snapshot().Document.ProjectId)));
            store.Checkpoint = null;
            PreparedUpdate third;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 3) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                third = Take(preparation);
            }
            var unexpectedPath = Path.Combine(VersionPath(store, 3), "after-decision-" + Guid.NewGuid().ToString("N"));
            var injected = false;
            store.Checkpoint = stage =>
            {
                if (stage == "CommitDecided" && !injected)
                {
                    // receipt 持有原文件的只读输入；新增未登记文件可真实破坏目标闭包，不拆掉输入保护。
                    using var file = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(unexpectedPath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    file.WriteByte(1);
                    injected = true;
                }
            };
            try
            {
                using var failed = system.ActivatePreparedUpdateAsync(third);
                yield return Wait(failed);
                Assert.That(injected, Is.True, "The fault must actually occur after the durable commit decision.");
                CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(unexpectedPath)));
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed),
                    "Recovering From after the target closure becomes invalid must not report successful activation of To.");
                Assert.That(failed.Error?.Kind, Is.EqualTo(ResourceError.UpdateFailed));
            }
            finally
            {
                store.Checkpoint = null;
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(unexpectedPath)))
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(unexpectedPath));
                }
            }

            Assert.That(system.GetActiveCatalogDigest(Snapshot().Document.ProjectId), Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
            Assert.That(Store(store.RootPath).Mount().Single().Document.CatalogDigest, Is.EqualTo(system.GetActiveCatalogDigest(Snapshot().Document.ProjectId)));
            using (var old = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(old.Asset.text, Is.EqualTo("release87-a-v2"));
            }

            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 3) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                third = Take(preparation);
            }
            var activeBefore = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store)));
            var enteredCommit = false;
            store.Checkpoint = stage => enteredCommit |= stage == "JournalVerified" || stage == "CommitDecided";
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(VersionPath(store, 2), Snapshot("a", 2).Document.Bundles[0].RelativeFile)), new byte[] { 1 });
            using (var failed = system.ActivatePreparedUpdateAsync(third))
            {
                yield return Wait(failed);
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            }

            store.Checkpoint = null;
            Assert.That(enteredCommit, Is.False, "Invalid source bytes must fail before the persistent commit boundary.");
            CollectionAssert.AreEqual(activeBefore, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), "Prevalidation failure must not rewrite the active pointer.");
            Assert.That(system.GetActiveCatalogDigest(Snapshot().Document.ProjectId), Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
            yield return Succeed(system.ShutdownAsync());
            Assert.That(Store(store.RootPath).Mount().Single().Document.CatalogDigest, Is.EqualTo(Snapshot().Document.CatalogDigest));
            yield return Initialize(Store(store.RootPath));
            system = ResourceSystem.Current;
            Assert.That(system.GetActiveCatalogDigest(Snapshot().Document.ProjectId), Is.EqualTo(Store(store.RootPath).Mount().Single().Document.CatalogDigest),
                "Explicit restart recovery must reconcile memory with the complete fallback closure.");
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("first release content"));
            }

            yield return Succeed(system.ShutdownAsync());
        }

        [UnityTest]
        public IEnumerator CompleteAndActiveSurviveActualDomainReloadAndStalePreparedCannotDowngrade()
        {
            yield return new EnterPlayMode();
            var store = NewStore();
            SessionState.SetString(Key + "restart", store.RootPath);
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 2) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                Take(preparation);
            }
            yield return Succeed(system.ShutdownAsync());
            yield return new ExitPlayMode();
            yield return new EnterPlayMode();
            store = Store(SessionState.GetString(Key + "restart", ""));
            yield return Initialize(store);
            system = ResourceSystem.Current;
            var recovered = system.GetPreparedUpdates().Single();
            yield return Succeed(system.ActivatePreparedUpdateAsync(recovered));
            PreparedUpdate third;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 3) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                third = Take(preparation);
            }
            yield return Succeed(system.ActivatePreparedUpdateAsync(third));
            using (var stale = system.ActivatePreparedUpdateAsync(recovered))
            {
                yield return Wait(stale);
                Assert.That(stale.Error?.Kind, Is.EqualTo(ResourceError.StaleBase));
            }

            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v3"));
            }

            yield return Succeed(system.ShutdownAsync());
            yield return new ExitPlayMode();
            yield return new EnterPlayMode();
            store = Store(SessionState.GetString(Key + "restart", ""));
            yield return Initialize(store);
            system = ResourceSystem.Current;
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v3"));
            }

            yield return Succeed(system.RollbackAsync());
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v2"));
            }

            yield return Succeed(system.ShutdownAsync());
        }

        [UnityTest]
        public IEnumerator ExplicitMultiProjectClosureNeverPartiallyActivatesOnBusyOrCorruptTarget()
        {
            yield return new EnterPlayMode();
            var store = NewStore(true);
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            using (var incomplete = system.CheckUpdatesAsync(new[] { Snapshot("a", 2) }))
            {
                yield return Wait(incomplete);
                Assert.That(incomplete.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            }

            PreparedUpdate update;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 2), Snapshot("b", 2) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                update = Take(preparation);
            }
            using (var pin = system.Acquire<TextAsset>(Address(project: "b")))
            {
                yield return Busy(system.ActivatePreparedUpdateAsync(update));
            }

            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store))), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ProjectPath(store, project: "b"))), Is.False);
            var file = Path.Combine(VersionPath(store, 2, "b"), Snapshot("b", 2).Document.Bundles[0].RelativeFile);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file), new byte[] { 1 });
            using (var failed = system.ActivatePreparedUpdateAsync(update))
            {
                yield return Wait(failed);
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            }

            foreach (var project in new[] { "a", "b" })
            {
                Assert.That(system.GetActiveCatalogDigest(Snapshot(project).Document.ProjectId), Is.EqualTo(Snapshot(project).Document.CatalogDigest));
            }

            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file), bytes);
            yield return Succeed(system.ActivatePreparedUpdateAsync(update));
            foreach (var project in new[] { "a", "b" })
            {
                using var text = system.Acquire<TextAsset>(Address(project: project));
                Assert.That(text.Asset.text, Is.EqualTo("release87-" + project + "-v2"));
            }
            yield return Succeed(system.RollbackAsync());
            yield return Succeed(system.ShutdownAsync());
        }

        [UnityTest]
        public IEnumerator ExternalLoadedProjectBundleIsBusyAndIsNeverUnloadedOrAdoptedByActivation()
        {
            yield return new EnterPlayMode();
            yield return VerifyExternalNativeGate();
        }

        private static IEnumerator VerifyExternalNativeGate()
        {
            var store = NewStore();
            yield return Initialize(store);
            var system = ResourceSystem.Current;
            PreparedUpdate update;
            using (var check = system.CheckUpdatesAsync(new[] { Snapshot("a", 2) }))
            {
                yield return Wait(check);
                using var preparation = system.PrepareUpdateAsync(Take(check));
                yield return Wait(preparation);
                update = Take(preparation);
            }
            var snapshot = Snapshot();
            var asset = snapshot.Document.Assets.Single(x => x.Address == "A.prefab");
            var bundle = snapshot.Document.Bundles.Single(x => x.LogicalId == asset.BundleId);
            var external = AssetBundle.LoadFromFile(Path.Combine(snapshot.DirectoryPath, bundle.RelativeFile));
            Assert.That(external, Is.Not.Null);
            try
            {
                yield return Busy(system.ActivatePreparedUpdateAsync(update));
                Assert.That(external, Is.Not.Null, "The barrier must not take ownership of an external native bundle.");
                Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
            }
            finally
            {
                if (external)
                {
                    external.Unload(true);
                }
            }
            yield return Succeed(system.ActivatePreparedUpdateAsync(update));
            using (var text = system.Acquire<TextAsset>(Address()))
            {
                Assert.That(text.Asset.text, Is.EqualTo("release87-a-v2"));
            }

            yield return Succeed(system.ShutdownAsync());
        }
    }
}
