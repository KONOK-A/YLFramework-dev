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
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceBundleRuntimeTests
    {
        private const string Key = "YL.Resource83.Native.";
        [Serializable]
        private sealed class SceneList
        {
            public SavedScene[] Items;
        }
        [Serializable]
        private sealed class SavedScene
        {
            public string Path;
            public bool Loaded;
            public bool Active;
        }

        [OneTimeSetUp]
        public void BuildPublication()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            YLFramework.Tests.Resource.ResourceSessionTests.EnsureRestorableScenes();
            var savedScenes = EditorSceneManager.GetSceneManagerSetup();
            var fixture = new ResourceReleaseFixture();
            string scenePath = null;
            try
            {
                var project = fixture.Project;
                var instancePrefabPath = fixture.Folder + "/Content/A.prefab";
                var instancePrefab = PrefabUtility.LoadPrefabContents(instancePrefabPath);
                try
                {
                    instancePrefab.AddComponent<InstanceLifecycleProbe>();
                    PrefabUtility.SaveAsPrefabAsset(instancePrefab, instancePrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(instancePrefab);
                }
                var go = new GameObject("Diamond83");
                var diamondPath = fixture.Folder + "/Content/C.prefab";
                try
                {
                    go.AddComponent<MeshRenderer>().sharedMaterials = new[] { AssetDatabase.LoadAssetAtPath<Material>(fixture.Folder + "/Content/A.mat"),
                        AssetDatabase.LoadAssetAtPath<Material>(fixture.Folder + "/Content/B.mat") };
                    PrefabUtility.SaveAsPrefabAsset(go, diamondPath);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
                project.groups.Add(new ResourceGroup { id = "diamond83", selections = new List<ResourceSelection> { new() { guid = AssetDatabase.AssetPathToGUID(diamondPath) } } });
                foreach (var spec in new[] { ("bulk83", 2, ResourcePacking.Together), ("fault83", 6, ResourcePacking.PerAsset) })
                {
                    var group = new ResourceGroup { id = spec.Item1, packing = spec.Item3 };
                    for (var i = 0; i < spec.Item2; i++)
                    {
                        var text = fixture.Folder + "/Content/" + spec.Item1 + "-" + i + ".txt";
                        File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(text), "batch83-" + spec.Item1 + "-" + i);
                        AssetDatabase.ImportAsset(text, ImportAssetOptions.ForceSynchronousImport);
                        group.selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(text) });
                    }
                    project.groups.Add(group);
                }
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
                AddScene85Fixture(fixture);
                AddCore86Fixture(fixture);
                AddAtlas89Fixture(fixture);
                AddAutomaticAtlasFixture(fixture);
                var build = fixture.Build();
                fixture.ReleaseRoot = ResourcePublisher.GetReleaseRoot(build);
                var release = ResourcePublisher.Publish(build, "fixture-v1");
                SessionState.SetString(Key + "root", release.RootPath);
                SessionState.SetString(Key + "path", release.DirectoryPath);
                SessionState.SetString(Key + "project", release.Catalog.ProjectId);
                SessionState.SetString(Key + "digest", release.Catalog.CatalogDigest);
                SessionState.SetString(Key + "folder", fixture.Folder);
                SessionState.SetString(Key + "guid", AssetDatabase.AssetPathToGUID(fixture.Folder));
                SessionState.SetString(Key + "workspace", Path.GetDirectoryName(build.WorkspacePath));
                fixture.AssertProtected();
                SessionState.SetString(Key + "protected", ProtectedDigest());
                SessionState.SetString(Key + "scenes", JsonUtility.ToJson(new SceneList
                {
                    Items = savedScenes
                    .Where(x => !string.IsNullOrEmpty(x.path)).Select(x => new SavedScene { Path = x.path, Loaded = x.isLoaded, Active = x.isActive }).ToArray()
                }));
                scenePath = "Temp/YL83-Native-" + Guid.NewGuid().ToString("N") + ".unity";
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("Temp")), Is.True);
                SessionState.SetString(Key + "scene", scenePath);
                Assert.That(EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single), scenePath), Is.True);
                SessionState.SetBool(Key + "owned", true);
            }
            catch
            {
                EditorSceneManager.RestoreSceneManagerSetup(savedScenes);
                if (scenePath != null)
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(scenePath));
                }

                fixture.Dispose();
                ClearFixtureSession();
                throw;
            }
        }

        private static string ProtectedDigest() => ResourceCatalogCodecTests.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
            new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings" }
                .SelectMany(x => ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x))).Select(f => x + f)))));

        private static void ClearFixtureSession()
        {
            foreach (var key in new[] { "root", "path", "project", "digest", "scenes", "scene", "folder", "guid", "workspace", "protected", "core86fallback" })
            {
                SessionState.EraseString(Key + key);
            }

            SessionState.EraseBool(Key + "owned");
            SessionState.EraseBool(Key + "quit");
        }

        [OneTimeTearDown]
        public void CleanupPublication()
        {
            if (!SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero, "Do not delete a publication still used by native Bundles.");
            Assert.That(ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")), "Native runtime verification must preserve the protected file baseline.");
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "root", "")), true);
            var folder = SessionState.GetString(Key + "folder", "");
            Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(SessionState.GetString(Key + "guid", "")));
            AssetDatabase.DeleteAsset(folder);
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "workspace", "")), true);
            var scenes = JsonUtility.FromJson<SceneList>(SessionState.GetString(Key + "scenes", ""));
            EditorSceneManager.RestoreSceneManagerSetup(scenes.Items.Select(x => new SceneSetup { path = x.Path, isLoaded = x.Loaded, isActive = x.Active }).ToArray());
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "scene", "")));
            ClearFixtureSession();
        }

        private static ResourceLocalSnapshot Snapshot() => ResourceLocalSnapshot.Open(SessionState.GetString(Key + "path", ""),
            SessionState.GetString(Key + "project", ""), SessionState.GetString(Key + "digest", ""), "StandaloneWindows64", "fixture-v1");

        private static ResourceSystem Initialize(ResourceLocalSnapshot snapshot)
        {
            using var request = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog() }));
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            return request.TakeResult();
        }

        private static ResourceAddress Address(ResourceLocalSnapshot snapshot, string path, string sub = null) => new(snapshot.Document.ProjectId, path, sub);

        private static IEnumerator Wait<T>(ResourceRequest<T> request)
        {
            var until = Time.realtimeSinceStartup + 10;
            while (!request.IsDone && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(request.IsDone, Is.True, "Native/dependency failure must not leave the queue hanging.");
        }

        [UnityTearDown]
        public IEnumerator ExitOwnedSession()
        {
            InstanceLifecycleProbe.ResetCallbacks();
            if (!EditorApplication.isPlaying)
            {
                yield break;
            }

            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            yield return DrainOwnedScene85Fixture();
            var until = Time.realtimeSinceStartup + 10;
            while (AssetBundle.GetAllLoadedAssetBundles().Any() && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator RealTextPrefabSubassetsSharedScopesAndNativeUnloadReload()
        {
            yield return new EnterPlayMode();
            yield return NativeBody();
        }

        private static IEnumerator NativeBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using (var wrong = system.AcquireAsync<Sprite>(Address(snapshot, "entry.txt")))
            {
                Assert.That(wrong.Error.Kind, Is.EqualTo(ResourceError.TypeMismatch));
            }

            using (var unknown = system.AcquireAsync<Sprite>(Address(snapshot, "Sprites.png", "unknown-sub-key")))
            {
                Assert.That(unknown.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            }

            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.Zero);
            using var a = system.CreateScope();
            using var b = system.CreateScope();
            var first = a.AcquireAsync<GameObject>(Address(snapshot, "A.prefab"));
            var second = b.AcquireAsync<GameObject>(Address(snapshot, "B.prefab"));
            using var duplicate = system.AcquireAsync<GameObject>(Address(snapshot, "A.prefab"));
            using var diamondRequest = system.AcquireAsync<GameObject>(Address(snapshot, "C.prefab"));
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<GameObject>(Address(snapshot, "B.prefab"))).Kind, Is.EqualTo(ResourceError.InProgress));
            yield return Wait(first);
            yield return Wait(second);
            var ownedA = a.TakeResult(first);
            var ownedB = b.TakeResult(second);
            using var independent = duplicate.TakeResult();
            yield return Wait(diamondRequest);
            using var diamond = diamondRequest.TakeResult();
            Assert.That(independent.Asset, Is.SameAs(ownedA.Asset));
            var texture = ownedA.Asset.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
            Assert.That(ownedB.Asset.GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.SameAs(texture));
            Assert.That(diamond.Asset.GetComponent<MeshRenderer>().sharedMaterials.All(x => x.mainTexture == texture), Is.True);
            var graph = new HashSet<string>();
            var stack = new Stack<string>(snapshot.Document.Assets.Where(x => new[] { "A.prefab", "B.prefab", "C.prefab" }.Contains(x.Address)).Select(x => x.BundleId));
            while (stack.Count != 0)
            {
                var id = stack.Pop();
                if (graph.Add(id))
                {
                    foreach (var dep in snapshot.Document.Bundles.Single(x => x.LogicalId == id).Dependencies)
                    {
                        stack.Push(dep);
                    }
                }
            }
            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.EqualTo(graph.Count), "Every native graph node must be produced once, including the diamond leaf.");
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Select(x => x.name).Distinct().Count(), Is.EqualTo(AssetBundle.GetAllLoadedAssetBundles().Count()));
            a.Dispose();
            independent.Dispose();
            Assert.That(texture, Is.Not.Null);
            Assert.That(ownedB.Asset.GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.SameAs(texture));
            b.Dispose();
            Assert.That(diamond.IsValid, Is.True);
            diamond.Dispose();
            Assert.That(texture == null, Is.True, "Final graph ownership must actually Unload(true).");
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            using (var text = system.Acquire<TextAsset>(Address(snapshot, "entry.txt")))
            {
                Assert.That(text.Asset.text, Is.EqualTo("first release content"));
            }

            Sprite old = null;
            foreach (var sub in snapshot.Document.Assets.Where(x => x.Kind == "SubAsset"))
            {
                using var pending = system.AcquireAsync<Sprite>(Address(snapshot, sub.Address, sub.SubAssetKey));
                yield return Wait(pending);
                using var sprite = pending.TakeResult();
                Assert.That(sprite.Asset.name, Is.EqualTo(sub.SubAssetName));
                Assert.That(sprite.Asset.rect.width, Is.EqualTo(2));
                old = sprite.Asset;
            }
            Assert.That(old == null, Is.True);
            var left = snapshot.Document.Assets.Single(x => x.SubAssetName == "Left");
            using (var reload = system.Acquire<Sprite>(Address(snapshot, left.Address, left.SubAssetKey)))
            {
                Assert.That(reload.Asset.name, Is.EqualTo("Left"));
            }

            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            using var closed = system.ShutdownAsync();
            yield return Wait(closed);
            Assert.That(closed.TakeResult(), Is.True);
        }

        [UnityTest]
        public IEnumerator TypedBatchGroupAndLowLevelBundleHaveExplicitIndependentOwnership()
        {
            yield return new EnterPlayMode();
            yield return BatchBody();
        }

        private static IEnumerator BatchBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var bundle = snapshot.Document.Assets.Single(x => x.Address == "bulk83-0.txt").BundleId;
            using var all = system.AcquireAllAsync<TextAsset>(snapshot.Document.ProjectId, bundle);
            Assert.That(Assert.Throws<ResourceException>(() => system.Acquire<TextAsset>(Address(snapshot, "bulk83-1.txt"))).Kind, Is.EqualTo(ResourceError.InProgress));
            yield return Wait(all);
            using var batch = all.TakeResult();
            CollectionAssert.AreEquivalent(new[] { "batch83-bulk83-0", "batch83-bulk83-1" }, batch.Items.Select(x => x.Asset.text));
            using var group = system.AcquireGroupAsync(snapshot.Document.ProjectId, "bulk83");
            yield return Wait(group);
            using var members = group.TakeResult();
            Assert.That(members.Items.Count, Is.EqualTo(2));
            Assert.That(system.GetDiagnosticsSnapshot().NativeBundleLoadCount, Is.EqualTo(1));
            using var low = system.AcquireBundle(snapshot.Document.ProjectId, bundle);
            var native = batch.Items[0].Asset;
            batch.Dispose();
            Assert.That(members.Items.All(x => x.IsValid), Is.True);
            members.Dispose();
            Assert.That(native == null, Is.False, "Explicit Bundle lease pins native assets after asset consumers leave.");
            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            low.Dispose();
            Assert.That(native == null, Is.True);
            var spriteBundle = snapshot.Document.Assets.Single(x => x.SubAssetName == "Left").BundleId;
            using var sprites = system.AcquireAllAsync<Sprite>(snapshot.Document.ProjectId, spriteBundle);
            yield return Wait(sprites);
            using var spriteBatch = sprites.TakeResult();
            CollectionAssert.AreEquivalent(new[] { "Left", "Right" }, spriteBatch.Items.Select(x => x.Asset.name));
            spriteBatch.Dispose();
            using var scene = system.AcquireGroupAsync(snapshot.Document.ProjectId, "Owned");
            Assert.That(scene.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(scene.Error.InnerException, Is.InstanceOf<AggregateException>());
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DependencyAndQueuedFailuresTerminateAndRollbackOnlyTheirOwnGraph()
        {
            yield return new EnterPlayMode();
            yield return FaultBody();
        }

        private static IEnumerator FaultBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var shared = snapshot.Document.Bundles.Single(x => x.Dependencies.Count == 0 && x.LogicalId.Contains("_s_"));
            var path = Path.Combine(snapshot.DirectoryPath, shared.RelativeFile);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            try
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                using var failed = system.AcquireAsync<GameObject>(Address(snapshot, "C.prefab"));
                yield return Wait(failed);
                Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(failed.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
                Assert.That(failed.Error.ToString(), Does.Contain(shared.LogicalId));
                var missing = failed.Error.GetBaseException() as FileNotFoundException;
                Assert.That(missing, Is.Not.Null);
                Assert.That(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(missing.FileName)), Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path))));
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), bytes);
            }
            var damaged = snapshot.Document.Assets.Single(x => x.Address == "fault83-0.txt");
            path = Path.Combine(snapshot.DirectoryPath, snapshot.Document.Bundles.Single(x => x.LogicalId == damaged.BundleId).RelativeFile);
            bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), new byte[] { 0, 1 });
                using var group = system.AcquireGroupAsync(snapshot.Document.ProjectId, "fault83");
                Assert.That(system.GetDiagnosticsSnapshot().RunningBundleCount, Is.LessThanOrEqualTo(4));
                Assert.That(system.GetDiagnosticsSnapshot().QueuedBundleCount, Is.GreaterThan(0));
                using var unrelated = system.AcquireAsync<TextAsset>(Address(snapshot, "entry.txt"));
                yield return Wait(group);
                Assert.That(group.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                yield return Wait(unrelated);
                using var text = unrelated.TakeResult();
                Assert.That(text.Asset.text, Is.EqualTo("first release content"));
            }
            finally
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), bytes);
            }
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator CancelObserversAndEndedSessionsKeepLateNativeCleanupResponsibility()
        {
            yield return new EnterPlayMode();
            yield return CancelBody();
        }

        private static IEnumerator CancelBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            using var first = system.AcquireAsync<GameObject>(Address(snapshot, "C.prefab"));
            using var other = system.AcquireAsync<GameObject>(Address(snapshot, "C.prefab"));
            first.Cancel();
            other.Completed += value =>
            {
                using var owned = value.TakeResult();
                throw new InvalidOperationException("native83-observer");
            };
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: native83-observer");
            yield return Wait(other);
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            using var canceled = system.AcquireGroupAsync(snapshot.Document.ProjectId, "fault83");
            canceled.Dispose();
            using var shutdown = system.ShutdownAsync();
            yield return Wait(shutdown);
            Assert.That(shutdown.TakeResult(), Is.True);
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
            system = Initialize(snapshot);
            using var pending = system.AcquireAsync<GameObject>(Address(snapshot, "C.prefab"));
            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Assert.That(pending.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            typeof(ResourceSystem).GetMethod("ResetSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            using (var blocked = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog() })))
            {
                Assert.That(blocked.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            var until = Time.realtimeSinceStartup + 10;
            while ((system.GetDiagnosticsSnapshot().BundleRecordCount != 0 || system.GetDiagnosticsSnapshot().RecordCount != 0) && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            var next = Initialize(snapshot);
            using var reload = next.Acquire<GameObject>(Address(snapshot, "C.prefab"));
            Assert.That(reload.Asset.name, Is.EqualTo("C"));
            pending.Dispose();
            Assert.That(reload.IsValid, Is.True);
        }

        [UnityTest]
        public IEnumerator EditorBorrowedConfigurationUsesFormalScopesAndNeverDestroysExternalAssets()
        {
            yield return new EnterPlayMode();
            yield return EditorBody();
        }

        private static IEnumerator EditorBody()
        {
            var project = AssetDatabase.LoadAssetAtPath<ResourceProject>(SessionState.GetString(Key + "folder", "") + "/Project.asset");
            var simulation = new ResourceEditorSimulation(new[] { project }, new[] { Snapshot().Document });
            using var initialized = ResourceSystem.InitializeAsync(simulation.CreateConfiguration());
            var system = initialized.TakeResult();
            var external = AssetDatabase.LoadAssetAtPath<TextAsset>(SessionState.GetString(Key + "folder", "") + "/Content/entry.txt");
            using var scope = system.CreateScope();
            var request = scope.AcquireAsync<TextAsset>(new ResourceAddress(project.projectId, "entry.txt"));
            yield return Wait(request);
            Assert.That(scope.TakeResult(request).Asset, Is.SameAs(external));
            scope.Dispose();
            Assert.That(external == null, Is.False);
            Assert.That(external.text, Is.EqualTo("first release content"));
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator BundleConsumersAreTerminalAndPinnedBeforeReentrantObservers()
        {
            yield return new EnterPlayMode();
            yield return BundleObserversBody();
        }

        private static IEnumerator BundleObserversBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var id = snapshot.Document.Assets.Single(x => x.Address == "C.prefab").BundleId;
            using var first = system.AcquireBundleAsync(snapshot.Document.ProjectId, id);
            using var second = system.AcquireBundleAsync(snapshot.Document.ProjectId, id);
            var status = ResourceRequestStatus.Pending;
            var pins = 0;
            first.Completed += value =>
            {
                status = second.Status;
                pins = system.GetDiagnosticsSnapshot().BundleRootPinCount;
                value.Dispose();
                second.Cancel();
            };
            yield return Wait(second);
            Assert.That(status, Is.EqualTo(ResourceRequestStatus.Succeeded), "All shared Bundle consumers must be terminal before the first observer.");
            Assert.That(pins, Is.EqualTo(2));
            Assert.That(second.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            using var lease = second.TakeResult();
            Assert.That(lease.Bundle.name, Is.EqualTo(id));
        }

        [UnityTest]
        public IEnumerator ColdSyncFromAnInFlightFailureObserverHasNoSecondQueueAuthority()
        {
            yield return new EnterPlayMode();
            yield return SyncReentryBody();
        }

        private static IEnumerator SyncReentryBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var assets = snapshot.Document.Assets.Where(x => x.Address.StartsWith("fault83-", StringComparison.Ordinal)).OrderBy(x => x.Address).ToArray();
            var active = assets.Take(4).Select(x => system.AcquireBundleAsync(snapshot.Document.ProjectId, x.BundleId)).ToArray();
            var descriptor = snapshot.Document.Bundles.Single(x => x.LogicalId == assets[4].BundleId);
            var path = Path.Combine(snapshot.DirectoryPath, descriptor.RelativeFile);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            string nested = null;
            Exception nestedFailure = null;
            try
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), new byte[] { 0, 1 });
                using var queued = system.AcquireBundleAsync(snapshot.Document.ProjectId, descriptor.LogicalId);
                queued.Completed += _ =>
                {
                    try
                    {
                        using var value = system.Acquire<TextAsset>(Address(snapshot, "entry.txt"));
                        nested = value.Asset.text;
                    }
                    catch (Exception error)
                    {
                        nestedFailure = error;
                    }
                };
                var bulkId = snapshot.Document.Assets.Single(x => x.Address == "bulk83-0.txt").BundleId;
                using (var cold = system.AcquireBundle(snapshot.Document.ProjectId, bulkId))
                {
                    Assert.That(cold.IsValid, Is.True);
                }

                yield return Wait(queued);
                Assert.That(nestedFailure, Is.Null, nestedFailure?.ToString());
                Assert.That(nested, Is.EqualTo("first release content"));
            }
            finally
            {
                foreach (var request in active)
                {
                    request.Dispose();
                }

                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), bytes);
            }
            using var closing = system.ShutdownAsync();
            yield return Wait(closing);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }

        private static ResourceSystem _exitSystem;
        private static AssetLease<GameObject> _exitLease;
        private static ResourceRequest<AssetLease<Sprite>> _exitPending;

        private static void StartRealExit()
        {
            var snapshot = Snapshot();
            _exitSystem = Initialize(snapshot);
            _exitLease = _exitSystem.Acquire<GameObject>(Address(snapshot, "C.prefab"));
            var sub = snapshot.Document.Assets.Single(x => x.SubAssetName == "Left");
            using var prepared = _exitSystem.AcquireBundle(snapshot.Document.ProjectId, sub.BundleId);
            _exitPending = _exitSystem.AcquireAsync<Sprite>(Address(snapshot, sub.Address, sub.SubAssetKey));
            Assert.That(AssetNative(_exitSystem).isDone, Is.False, "Exit must include an already-started AssetBundleRequest, not only Bundle creation.");
            SessionState.SetBool(Key + "quit", false);
            Application.quitting += OnActualQuit;
        }
        private static void OnActualQuit()
        {
            SessionState.SetBool(Key + "quit", _exitSystem.State == ResourceSystemState.Invalidated && !_exitLease.IsValid && _exitPending.IsDisposed);
            Application.quitting -= OnActualQuit;
        }

        [UnityTest]
        public IEnumerator ActualPlayExitWithLiveAndInFlightBundlesAllowsCleanNextPlay()
        {
            yield return new EnterPlayMode();
            StartRealExit();
            yield return new ExitPlayMode();
            Assert.That(SessionState.GetBool(Key + "quit", false), Is.True, "Actual quitting must invalidate live and pending owning results.");
            yield return new EnterPlayMode();
            yield return AfterActualExitBody();
        }
        private static IEnumerator AfterActualExitBody()
        {
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero, "Old native identities must drain before a new authority can use them.");
            var snapshot = Snapshot();
            var next = Initialize(snapshot);
            using (var lease = next.Acquire<GameObject>(Address(snapshot, "C.prefab")))
            {
                Assert.That(lease.Asset.name, Is.EqualTo("C"));
            }

            using var closed = next.ShutdownAsync();
            yield return Wait(closed);
            Assert.That(closed.TakeResult(), Is.True);
        }

        private static AssetBundleRequest AssetNative(ResourceSystem system)
        {
            var records = (IDictionary)typeof(ResourceSystem).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(system);
            var producer = records.Values.Cast<ResourceSystem.AssetRecord>().Single(x => x.IsLoading).Operation;
            return (AssetBundleRequest)producer.GetType().GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(producer);
        }

        [UnityTest]
        public IEnumerator LastConsumerCancellationAfterAssetNativeStartRetainsItsGraphUntilCompletion()
        {
            yield return new EnterPlayMode();
            yield return AssetNativeCancelBody();
        }

        private static IEnumerator AssetNativeCancelBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var id = snapshot.Document.Assets.Single(x => x.Address == "C.prefab").BundleId;
            using var prepared = system.AcquireBundle(snapshot.Document.ProjectId, id);
            using var request = system.AcquireAsync<GameObject>(Address(snapshot, "C.prefab"));
            var native = AssetNative(system);
            Assert.That(native.isDone, Is.False);
            request.Cancel();
            prepared.Dispose();
            Assert.That(system.GetDiagnosticsSnapshot().ProducerCount, Is.EqualTo(1));
            Assert.That(system.GetDiagnosticsSnapshot().BundleRootPinCount, Is.EqualTo(1));
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Any(x => x.name == id), Is.True);
            using var closed = system.ShutdownAsync();
            Assert.That(closed.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            yield return Wait(closed);
            Assert.That(native.isDone, Is.True);
            Assert.That(closed.TakeResult(), Is.True);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
        }

        private static IEnumerator NativeSubassetFailureBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var left = snapshot.Document.Assets.Single(x => x.SubAssetName == "Left");
            using var prepared = system.AcquireBundle(snapshot.Document.ProjectId, left.BundleId);
            var sprites = prepared.Bundle.LoadAssetWithSubAssets<Sprite>(left.LoadName);
            Assert.That(sprites.Length, Is.EqualTo(2));
            // 只扰动本测试拥有的 native Sprite；不编辑 Catalog、源图片或持久化发布，验证绝不取“第一个匹配”。
            foreach (var sprite in sprites)
            {
                sprite.name = "Moved83";
            }

            using (var missing = system.AcquireAsync<Sprite>(Address(snapshot, left.Address, left.SubAssetKey)))
            {
                yield return Wait(missing);
                Assert.That(missing.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(missing.Error.ToString(), Does.Contain("0 native matches"));
            }
            foreach (var sprite in sprites)
            {
                sprite.name = "Left";
            }

            using (var ambiguous = system.AcquireAsync<Sprite>(Address(snapshot, left.Address, left.SubAssetKey)))
            {
                yield return Wait(ambiguous);
                Assert.That(ambiguous.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(ambiguous.Error.ToString(), Does.Contain("2 native matches"));
            }
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            prepared.Dispose();
            Assert.That(sprites.All(x => x == null), Is.True);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator CompleteNative83RegressionSharesOnePublicationAndPreservesReloadEvidence()
        {
            // MCP 的汇总跨 reload 会重置；此单一用例复用一份发布，任一合同失败即使整次结果失败。
            yield return new EnterPlayMode();
            yield return NativeBody();
            yield return CloseCurrent();
            yield return BatchBody();
            yield return CloseCurrent();
            yield return FaultBody();
            yield return CloseCurrent();
            yield return CancelBody();
            yield return CloseCurrent();
            yield return EditorBody();
            yield return CloseCurrent();
            yield return BundleObserversBody();
            yield return CloseCurrent();
            yield return SyncReentryBody();
            yield return CloseCurrent();
            yield return NativeSubassetFailureBody();
            yield return CloseCurrent();
            yield return AssetNativeCancelBody();
            yield return CloseCurrent();
            StartRealExit();
            yield return new ExitPlayMode();
            Assert.That(SessionState.GetBool(Key + "quit", false), Is.True);
            yield return new EnterPlayMode();
            yield return AfterActualExitBody();
        }

        private static IEnumerator CloseCurrent()
        {
            if (ResourceSystem.Current == null)
            {
                yield break;
            }

            using var closed = ResourceSystem.Current.ShutdownAsync();
            yield return Wait(closed);
            Assert.That(closed.TakeResult(), Is.True);
        }
    }
}
