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
using YLFramework.Core;
using YLFramework.Core.Tests;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using Object = UnityEngine.Object;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceBundleRuntimeTests
    {
        private static void AddCore86Fixture(ResourceReleaseFixture fixture)
        {
            var folder = fixture.Folder + "/Content";
            var prefabPath = folder + "/A.prefab";
            var prefab = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                prefab.AddComponent<YLCoreSmokeView>();
                PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefab);
            }
            var panel = new GameObject("Core86Panel", typeof(RectTransform), typeof(YLCoreSmokePanel));
            try
            {
                PrefabUtility.SaveAsPrefabAsset(panel, folder + "/Core86Panel.prefab");
            }
            finally
            {
                Object.DestroyImmediate(panel);
            }
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/Core86Rows.json"), YLCoreSmokeModule.Json);
            AssetDatabase.ImportAsset(folder + "/Core86Rows.json", ImportAssetOptions.ForceSynchronousImport);
            foreach (var path in new[] { folder + "/Core86Panel.prefab", folder + "/Core86Rows.json" })
            {
                fixture.Project.groups.Add(new ResourceGroup
                {
                    id = Path.GetFileNameWithoutExtension(path),
                    selections = new List<ResourceSelection> { new() { guid = AssetDatabase.AssetPathToGUID(path) } }
                });
            }

            var scene = EditorSceneManager.OpenScene(folder + "/Owned.unity", OpenSceneMode.Additive);
            try
            {
                scene.GetRootGameObjects().Single().AddComponent<YLCoreSmokeView>();
                Assert.That(EditorSceneManager.SaveScene(scene), Is.True);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.CreateFolder(folder, "Resources");
            var fallbackName = "YLCore86Base-" + Guid.NewGuid().ToString("N");
            var fallback = new GameObject("Core86BaseFallback", typeof(YLCoreSmokeView));
            try
            {
                PrefabUtility.SaveAsPrefabAsset(fallback, folder + "/Resources/" + fallbackName + ".prefab");
            }
            finally
            {
                Object.DestroyImmediate(fallback);
            }
            SessionState.SetString(Key + "core86fallback", fallbackName);
            EditorUtility.SetDirty(fixture.Project);
            AssetDatabase.SaveAssetIfDirty(fixture.Project);
        }

        [UnityTest]
        public IEnumerator CoreNativePrefabJoinInProgressCancelAndPanelOwnership()
        {
            yield return new EnterPlayMode();
            yield return CoreNativeInstancesBody();
        }

        private static IEnumerator CoreNativeInstancesBody()
        {
            var snapshot = Snapshot();
            var fallbackCatalog = new ResourceCatalog("core86-base", new[]
            {
                new ResourceCatalogEntry("fallback", new ResourceAddress("core86-base", "A.prefab"),
                    new ResourceLocation(ResourceSourceId.Resources, SessionState.GetString(Key + "core86fallback", ""), typeof(GameObject)))
            });
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog(), fallbackCatalog }));
            var system = initialize.TakeResult();
            var provider = system.BindCoreResourceProvider();
            var consumer = new Core86Module { ProjectName = snapshot.Document.ProjectId };
            var baseOwner = new Core86BaseModule { ProjectName = "core86-base" };
            CoreInstanceLease held = null;
            try
            {
                yield return YLFramework.Core.ModuleManager.StartUpModule(consumer);
                yield return YLFramework.Core.ModuleManager.StartUpModule(baseOwner);
                using var pending = provider.AcquirePrefabAsync(snapshot.Document.ProjectId, "A.prefab");
                Assert.That(pending.Status, Is.EqualTo(CoreResourceStatus.Pending));
                using var canceledInterest = provider.AcquirePrefabAsync(snapshot.Document.ProjectId, "A.prefab");
                canceledInterest.Cancel();
                Assert.That(canceledInterest.Status, Is.EqualTo(CoreResourceStatus.Canceled));
                Assert.That(pending.Status, Is.EqualTo(CoreResourceStatus.Pending), "Cancel only one bridge interest, not the shared native producer.");
                var error = Assert.Throws<CoreResourceException>(() => consumer.LoadView<YLCoreSmokeView>("A.prefab", loadViewArgs: null));
                Assert.That(error.Kind, Is.EqualTo(CoreResourceError.InProgress));
                Assert.That(Resources.FindObjectsOfTypeAll<YLCoreSmokeView>().Any(v => v && v.gameObject.scene.IsValid() &&
                    v.gameObject.name == "Core86BaseFallback(Clone)"), Is.False, "InProgress must not choose a base fallback.");
                var canceled = consumer.LoadViewAsync<YLCoreSmokeView>("A.prefab", loadViewArgs: null);
                canceled.Abort();
                var joined = consumer.LoadViewAsync<YLCoreSmokeView>("A.prefab", loadViewArgs: null);
                yield return UntilScene85(() => joined.IsDone && pending.Status != CoreResourceStatus.Pending);
                Assert.That(canceled.Status, Is.EqualTo(LoadViewStatus.Canceled));
                Assert.That(joined.Status, Is.EqualTo(LoadViewStatus.Succeeded), joined.Error);
                Assert.That(joined.View.gameObject.name, Is.EqualTo("A(Clone)"));
                Assert.That(consumer.GetViews<YLCoreSmokeView>().Count, Is.EqualTo(1), "Canceled/late producers must not create extra instances.");
                using var prefab = pending.TakeResult();
                pending.Dispose();
                Assert.That(prefab.IsValid, Is.True, "Taking transfers ownership out of the request.");
                var texture = joined.View.GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
                var first = joined.View;
                first.Close();
                Assert.That(consumer.LoadView<YLCoreSmokeView>("A.prefab", loadViewArgs: null), Is.SameAs(first));
                Assert.That(texture == null, Is.False);
                var parent = new GameObject("Core86.NativePanelParent", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
                parent.SetActive(false);
                var panelRoot = CorePanelTestRoots.Create(parent);
                try
                {
                    var panelRequest = consumer.LoadPanelAsync<YLCoreSmokePanel>("Core86Panel.prefab", root: panelRoot);
                    yield return UntilScene85(() => panelRequest.IsDone);
                    Assert.That(panelRequest.Status, Is.EqualTo(LoadViewStatus.Succeeded), panelRequest.Error);
                    var panel = panelRequest.Panel;
                    panel.Close();
                    Assert.That(consumer.LoadPanel<YLCoreSmokePanel>("Core86Panel.prefab", root: panelRoot), Is.SameAs(panel));
                    consumer.Close();
                    Assert.That(panel.Module, Is.Null);
                }
                finally
                {
                    Object.Destroy(parent);
                }
                held = prefab.Instantiate(null, SceneManager.GetActiveScene());
                prefab.Dispose();
                system.ClearInstanceCaches();
                using (var busy = system.ShutdownAsync())
                {
                    Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
                }

                Assert.That(held.IsValid, Is.True);
                Assert.That(texture == null, Is.False, "Retired active checkout pins dependencies.");
                held.Discard();
                held = null;
                yield return UntilScene85(() => system.GetDiagnosticsSnapshot().LeaseCount == 0);
                using var shutdown = system.ShutdownAsync();
                yield return Wait(shutdown);
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
                Assert.That(CoreResourceAccess.Provider, Is.Null);
            }
            finally
            {
                held?.Discard();
                consumer.Close();
                baseOwner.AllowClose = true;
                baseOwner.Close();
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator CoreNativeWaitingAbortOwnerCloseAndPendingSessionExitDrainLateResults()
        {
            yield return new EnterPlayMode();
            yield return CoreNativeWaitingBody();
        }

        private static IEnumerator CoreNativeWaitingBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var provider = system.BindCoreResourceProvider();
            var module = new Core86Module { ProjectName = snapshot.Document.ProjectId };
            var observed = new ObservedCoreProvider(provider);
            var panelHost = new GameObject("Core86.WaitingPanelRoot", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster));
            panelHost.SetActive(false);
            module.PanelRoot = CorePanelTestRoots.Create(panelHost);
            try
            {
                yield return YLFramework.Core.ModuleManager.StartUpModule(module);
                CoreResourceAccess.Bind(observed);
                LoadViewRequest<YLCoreSmokeView> aborted = null;
                CoreResourceRequest<CorePrefabLease> captured = null;
                observed.OnRequest = request =>
                {
                    captured = request;
                    Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Pending), "This must execute a cold native request.");
                    request.Completed += _ =>
                    {
                        Assert.That(typeof(LoadViewRequest<YLCoreSmokeView>).GetField("_resourceRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(aborted), Is.SameAs(request), "Core must already own and await this native result.");
                        aborted.Abort();
                    };
                };
                aborted = module.LoadViewAsync<YLCoreSmokeView>("A.prefab", loadViewArgs: null);
                yield return UntilScene85(() => aborted.IsDone);
                Assert.That(aborted.Status, Is.EqualTo(LoadViewStatus.Canceled));
                Assert.That(captured.IsDisposed, Is.True);
                Assert.That(module.GetViews<View>(), Is.Empty);
                yield return Scene85Drained(system);
                LoadPanelRequest<YLCoreSmokePanel> invalidated = null;
                observed.OnRequest = request =>
                {
                    captured = request;
                    Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Pending));
                    request.Completed += _ =>
                    {
                        var operation = typeof(LoadPanelRequest<YLCoreSmokePanel>).GetField("_operation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(invalidated);
                        Assert.That(operation.GetType().GetField("_resource", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(operation), Is.SameAs(request));
                        module.Close();
                    };
                };
                invalidated = module.LoadPanelAsync<YLCoreSmokePanel>("Core86Panel.prefab");
                yield return UntilScene85(() => invalidated.IsDone);
                Assert.That(invalidated.Status, Is.EqualTo(LoadViewStatus.Invalidated));
                Assert.That(captured.IsDisposed, Is.True);
                yield return Scene85Drained(system);
                system.BindCoreResourceProvider();
                using var pending = provider.AcquirePrefabAsync(snapshot.Document.ProjectId, "B.prefab");
                Assert.That(pending.Status, Is.EqualTo(CoreResourceStatus.Pending));
                int notifications = 0;
                pending.Completed += _ => notifications++;
                typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                Assert.That(pending.Status, Is.EqualTo(CoreResourceStatus.Invalidated));
                Assert.That(pending.IsDisposed, Is.True);
                Assert.That(notifications, Is.EqualTo(1));
                Assert.That(CoreResourceAccess.Provider, Is.Null);
                Assert.Throws<ObjectDisposedException>(() => pending.TakeResult());
                yield return UntilScene85(() => system.GetDiagnosticsSnapshot().RecordCount == 0 &&
                    system.GetDiagnosticsSnapshot().BundleRecordCount == 0);
                Assert.That(AssetBundle.GetAllLoadedAssetBundles().Count(), Is.Zero);
                Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            }
            finally
            {
                module.Close();
                CoreResourceAccess.Unbind(observed);
                Object.Destroy(panelHost);
            }
        }

        private sealed class ObservedCoreProvider : ICoreResourceProvider
        {
            private readonly ICoreResourceProvider _inner;
            internal Action<CoreResourceRequest<CorePrefabLease>> OnRequest;
            internal ObservedCoreProvider(ICoreResourceProvider inner) => this._inner = inner;
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => _inner.ReadTextAsync(projectId, address);
            public CorePrefabLease AcquirePrefab(string projectId, string address) => _inner.AcquirePrefab(projectId, address);
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address)
            {
                var request = _inner.AcquirePrefabAsync(projectId, address);
                OnRequest?.Invoke(request);
                return request;
            }
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode)
                => _inner.LoadSceneAsync(projectId, address, mode);
        }

        [UnityTest]
        public IEnumerator CoreNativeModuleTextSingleSceneCloseAndRestart()
        {
            yield return new EnterPlayMode();
            yield return CoreNativeModuleBody();
        }

        private static IEnumerator CoreNativeModuleBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            system.BindCoreResourceProvider();
            var module = new Core86Module { ProjectName = snapshot.Document.ProjectId, WithTables = true, WithScene = true };
            YLCoreSmokeRow row = null;
            YLCoreSmokeRow.InitAction = value =>
            {
                row = value;
                Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.Zero, "Config text is released before Model.OnInit.");
            };
            try
            {
                var startup = YLFramework.Core.ModuleManager.StartUpModule(module);
                yield return UntilScene85(() => startup.IsDone);
                Assert.That(startup.Error, Is.Null.Or.Empty);
                Assert.That(module.ReadyAtStart, Is.True);
                var scene = SceneManager.GetSceneByPath(snapshot.Document.Assets.Single(a => a.Address == "Owned.unity").LoadName);
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
                Assert.That(system.GetSceneRecords().Single().Handle, Is.EqualTo((int)scene.handle));
                var texture = scene.GetRootGameObjects().Single().GetComponent<MeshRenderer>().sharedMaterial.mainTexture;
                module.Close();
                Assert.That(row.ExitCount, Is.EqualTo(1));
                Assert.That(row.Module, Is.Null);
                Assert.That(scene.isLoaded && texture, Is.True, "Core shutdown does not own native scene unload.");
                using (var busy = system.ShutdownAsync())
                {
                    Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
                }

                module.WithScene = false;
                startup = YLFramework.Core.ModuleManager.StartUpModule(module);
                yield return UntilScene85(() => startup.IsDone);
                Assert.That(startup.Error, Is.Null.Or.Empty);
                Assert.That(module.StartCount, Is.EqualTo(2));
                module.Close();
                SceneManager.CreateScene("Core86.NativeSceneExit");
                var unload = SceneManager.UnloadSceneAsync(scene);
                while (!unload.isDone)
                {
                    yield return null;
                }

                yield return Scene85Drained(system);
                using var shutdown = system.ShutdownAsync();
                yield return Wait(shutdown);
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
            finally
            {
                module.Close();
                YLCoreSmokeRow.InitAction = null;
            }
        }

        public class Core86Module : YLFramework.Core.Module
        {
            public bool WithTables;
            public bool WithScene;
            public int StartCount;
            public bool ReadyAtStart;
            public override string DefaultStartUpScene => WithScene ? "Owned.unity" : string.Empty;
            [Obsolete("Compatibility preload table contract.")]
            public override Dictionary<string, Type> PreloadConfigTables => WithTables
                ? new() { { "Core86Rows.json", typeof(YLCoreSmokeRow[]) } } : new();
            public override void OnStart()
            {
                StartCount++;
                ReadyAtStart = GetModel<YLCoreSmokeRow>(101) != null && GetViews<View>().Count == 1;
            }
        }

        public sealed class Core86BaseModule : Core86Module
        {
            public bool AllowClose;
            public override bool IsBaseModule => !AllowClose;
        }
    }
}
