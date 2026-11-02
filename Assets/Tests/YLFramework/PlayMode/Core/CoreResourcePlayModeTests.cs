#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Pooling;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Core.Tests
{
    public class CoreResourcePlayModeTests
    {
        private const float Timeout = 10f;
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly List<GameObject> _ownedObjects = new List<GameObject>();
        private readonly List<Object> _ownedAssets = new List<Object>();
        private readonly List<IDisposable> _assetLeases = new List<IDisposable>();
        private ResourceSystem _resources;
        private CoreResourceProvider _provider;
        private readonly List<Scene> _ownedSceneInstances = new List<Scene>();
        private CoreClassPoolScope _classPool;
        private CoreUpdateHostScope _updateHosts;
        private readonly HashSet<int> _prefabKeys = new HashSet<int>();
        private readonly HashSet<int> _previousEventSystems = new HashSet<int>();
        private readonly List<UIRootContext> _ownedRoots = new List<UIRootContext>();
        private YLCoreSmokeModule _module;
        private YLCoreSmokeRow _row;
        private object _moduleManager;
        private object _previousCurrent;
        private object _previousPanelManager;
        private GameObject _previousManagerEventSystem;
        private Scene _previousActive;
        private bool _ownsScene;
        private bool _isSingleSceneTest;
        private bool _isInitialized;
        private bool _hasTimedOut;

        [SetUp]
        public void SetUp()
        {
            _isInitialized = false;
            YLCoreSmokeRow.InitAction = null;
            YLCoreSmokeView.LoadingProbe = null;
            YLCoreSmokeView.ArgsProbe = null;
            YLCoreSmokePanel.ResetProbes();
            _hasTimedOut = false;
            _ownsScene = _isSingleSceneTest = false;
            _ownedObjects.Clear();
            _ownedAssets.Clear();
            _assetLeases.Clear();
            _ownedSceneInstances.Clear();
            _classPool = new CoreClassPoolScope();
            _updateHosts = new CoreUpdateHostScope();
            _prefabKeys.Clear();
            _previousEventSystems.Clear();
            _ownedRoots.Clear();
            _module = null;
            _row = null;
            _previousActive = SceneManager.GetActiveScene();
            foreach (var system in Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                _previousEventSystems.Add(system.gameObject.GetInstanceID());
            }

            _previousPanelManager = Singleton(typeof(YLFramework.Core.PanelManager));
            _previousManagerEventSystem = _previousPanelManager == null ? null :
                (GameObject)Field(_previousPanelManager, "_eventSystem");
            _moduleManager = typeof(YLFramework.Core.ModuleManager).GetProperty("Instance", StaticPrivate).GetValue(null);
            _previousCurrent = Field(_moduleManager, "_currentModule");
            _isInitialized = true;

            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.Null,
                "Never replace an externally registered fixture module.");
            Assert.That(SceneManager.GetSceneByPath(YLCoreSmokeModule.ScenePath).isLoaded, Is.False,
                "The fixture scene must not already be loaded.");
            Assert.That(ResourceSystem.Current, Is.Null, "Do not replace an external resource authority.");
            var fixture = Type.GetType("YLFramework.Core.Editor.Tests.CoreSmokeAssetFixture, YLFramework.Core.Tests.Fixtures.Editor", true);
            var config = (ResourceConfiguration)fixture.GetMethod("CreateConfiguration").Invoke(null, null);
            using var initialize = ResourceSystem.InitializeAsync(config);
            Assert.That(initialize.Error, Is.Null);
            _resources = initialize.TakeResult();
            _provider = _resources.BindCoreResourceProvider();
            foreach (string name in new[] { nameof(YLCoreSmokeView), nameof(YLCoreSmokePanel) })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(YLCoreSmokeModule.FixtureRoot + "/" + name + ".prefab");
                Assert.That(prefab, Is.Not.Null, name);
                int key = prefab.GetHashCode();
                _prefabKeys.Add(key);
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isInitialized)
            {
                yield break;
            }

            YLCoreSmokeRow.InitAction = null;
            YLCoreSmokeView.LoadingProbe = null;
            YLCoreSmokeView.ArgsProbe = null;
            YLCoreSmokePanel.ResetProbes();
            // A timeout exits this isolated PlayMode session. Producers are not cancellable by Abort.
            if (_hasTimedOut)
            {
                yield break;
            }

            var failures = new List<Exception>();
            void Attempt(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
            Attempt(() =>
            {
                if (_module == null || !ReferenceEquals(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), _module))
                {
                    return;
                }

                _row = _module.GetModel<YLCoreSmokeRow>(101);
                _module.Close();
            });
            Attempt(() => typeof(YLFramework.Core.ModuleManager).GetField("_currentModule", InstancePrivate).SetValue(_moduleManager, _previousCurrent));
            Attempt(CleanPanelManager);
            Attempt(() => _classPool.Restore(new Module[] { _module }));
            Attempt(_updateHosts.ReleaseOwnedIdleHosts);
            foreach (var obj in _ownedObjects)
            {
                if (obj)
                {
                    Attempt(() => Object.Destroy(obj));
                }
            }

            foreach (var lease in _assetLeases)
            {
                Attempt(lease.Dispose);
            }

            _assetLeases.Clear();
            if (_resources != null)
            {
                Attempt(_resources.ClearInstanceCaches);
            }

            if (_isSingleSceneTest && (!_previousActive.IsValid() || !_previousActive.isLoaded))
            {
                // Single is opt-in and only accepts this managed empty harness as the original scene.
                yield return AwaitUnity(EditorSceneManager.LoadSceneAsyncInPlayMode(YLCoreSmokeModule.HarnessPath,
                    new LoadSceneParameters(LoadSceneMode.Additive)), "restore fixture harness");
                _previousActive = SceneManager.GetSceneByPath(YLCoreSmokeModule.HarnessPath);
            }
            if (_previousActive.IsValid() && _previousActive.isLoaded)
            {
                SceneManager.SetActiveScene(_previousActive);
            }

            var scene = SceneManager.GetSceneByPath(YLCoreSmokeModule.ScenePath);
            if (_ownsScene && scene.IsValid() && scene.isLoaded)
            {
                yield return AwaitUnity(SceneManager.UnloadSceneAsync(scene), "unload fixture scene");
            }

            foreach (var instance in _ownedSceneInstances)
            {
                if (instance.IsValid() && instance.isLoaded)
                {
                    yield return AwaitUnity(SceneManager.UnloadSceneAsync(instance), "unload owned additive scene instance");
                }
            }

            yield return null;
            if (_resources != null)
            {
                using var shutdown = _resources.ShutdownAsync();
                yield return Await(() => shutdown.IsDone, "resource fixture shutdown");
                Assert.That(shutdown.Error, Is.Null, shutdown.Error?.ToString());
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
                Assert.That(CoreResourceAccess.Provider, Is.Null);
                _resources = null;
                _provider = null;
            }
            _isInitialized = false;
            if (failures.Count > 0)
            {
                throw new AggregateException("Core Resource fixture cleanup failed.", failures);
            }

            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.Null);
            foreach (var obj in _ownedObjects)
            {
                Assert.That(obj == null, Is.True, "Fixture object survived cleanup.");
            }

            // Framework hosts run shared pool/resource maintenance coroutines, even when first
            // created by this test. Keep those services alive; do not orphan or duplicate their checkers.
            yield return null;
        }

        [UnityTest]
        public IEnumerator RepeatedFixtureSessionsRemoveOwnedPoolsWithoutDestroyingMaintenanceHosts()
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                yield return NamedPanelsUseRealManagerCanvasEventSystemAsyncAndCacheIndexes();
                var foundationHost = Object.FindObjectsByType<YLFramework.Async.CoroutineStarter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                yield return TearDown();
                CollectionAssert.AreEquivalent(foundationHost,
                    Object.FindObjectsByType<YLFramework.Async.CoroutineStarter>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                Assert.That(ResourceSystem.Current, Is.Null);
                if (cycle == 0)
                {
                    SetUp();
                }
            }
        }

        [UnityTest]
        public IEnumerator ProviderReturningAfterOwnerCloseDisposesItsUnclaimedPrefabRequest()
        {
            yield return StartModule();
            var probe = new InspectingProvider(_provider) { AfterAcquire = _ => _module.Close() };
            CoreResourceAccess.Bind(probe);
            try
            {
                var request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: NewArgs());
                yield return Await(() => request.IsDone, "provider reentry owner close");
                Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Invalidated));
                Assert.That(probe.Captured.IsDisposed, Is.True, "A request returned after Core invalidation must be released locally.");
                Assert.That(Field(request, "_resourceRequest"), Is.Null);
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            }
            finally
            {
                probe.Captured?.Dispose();
                _resources.BindCoreResourceProvider();
            }
        }

        private sealed class InspectingProvider : ICoreResourceProvider
        {
            private readonly ICoreResourceProvider _inner;
            internal Action<CoreResourceRequest<CorePrefabLease>> AfterAcquire;
            internal CoreResourceRequest<CorePrefabLease> Captured;
            internal Action BeforePrefabDispose;
            internal int PrefabDisposals;
            internal Func<CorePrefabLease, CorePrefabLease> Decorate;
            internal Func<string, string> ResolveAddress;
            internal InspectingProvider(ICoreResourceProvider inner) => this._inner = inner;
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => _inner.ReadTextAsync(projectId, address);
            public CorePrefabLease AcquirePrefab(string projectId, string address)
            {
                var lease = _inner.AcquirePrefab(projectId, ResolveAddress == null ? address : ResolveAddress(address));
                if (Decorate != null)
                {
                    lease = Decorate(lease);
                }

                return BeforePrefabDispose == null ? lease : new ObservedPrefabLease(lease, () =>
                {
                    PrefabDisposals++;
                    BeforePrefabDispose();
                });
            }
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address)
            {
                Captured = _inner.AcquirePrefabAsync(projectId, ResolveAddress == null ? address : ResolveAddress(address));
                AfterAcquire?.Invoke(Captured);
                return Captured;
            }
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode)
                => _inner.LoadSceneAsync(projectId, address, mode);

            private sealed class ObservedPrefabLease : CorePrefabLease
            {
                private readonly CorePrefabLease _inner;
                private Action _beforeDispose;
                internal ObservedPrefabLease(CorePrefabLease inner, Action beforeDispose)
                {
                    this._inner = inner;
                    this._beforeDispose = beforeDispose;
                }
                public override GameObject Prefab => _inner.Prefab;
                public override bool IsValid => _inner.IsValid;
                public override CoreInstanceLease Instantiate(Transform parent, Scene scene) => _inner.Instantiate(parent, scene);
                public override void Dispose()
                {
                    var notify = _beforeDispose;
                    _beforeDispose = null;
                    try
                    {
                        notify?.Invoke();
                    }
                    finally
                    {
                        _inner.Dispose();
                    }
                }
            }
        }

        private sealed class RevocablePrefabLease : CorePrefabLease
        {
            private readonly CorePrefabLease _inner;
            internal bool Revoked;
            internal RevocableInstanceLease Created;
            internal RevocablePrefabLease(CorePrefabLease inner) => this._inner = inner;
            public override GameObject Prefab => _inner.Prefab;
            public override bool IsValid => !Revoked && _inner.IsValid;
            public override CoreInstanceLease Instantiate(Transform parent, Scene scene) => Created = new RevocableInstanceLease(_inner.Instantiate(parent, scene));
            public override void Dispose() => _inner.Dispose();
        }
        private sealed class RevocableInstanceLease : CoreInstanceLease
        {
            private readonly CoreInstanceLease _inner;
            internal bool Revoked;
            internal int Returns;
            internal int Discards;
            internal RevocableInstanceLease(CoreInstanceLease inner) => this._inner = inner;
            public override GameObject Instance => _inner.Instance;
            public override bool IsValid => !Revoked && _inner.IsValid;
            public override void Return()
            {
                Returns++;
                _inner.Return();
            }
            public override void Discard()
            {
                Discards++;
                _inner.Discard();
            }
        }

        [UnityTest] public IEnumerator RevokedBackendCannotRunReopenOnOpenWhileItsObjectStillLives() => RejectRevokedReopen(false);
        [UnityTest] public IEnumerator RevokedDirectSourcePinCannotRunReopenOnOpenWhileItsObjectStillLives() => RejectRevokedReopen(true);

        private IEnumerator RejectRevokedReopen(bool directSource)
        {
            yield return StartModule();
            _module.PanelRoot = PanelRoot();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var probe = new InspectingProvider(_provider);
            RevocablePrefabLease source = null;
            probe.Decorate = lease => source = new RevocablePrefabLease(lease);
            CoreResourceAccess.Bind(probe);
            try
            {
                var panel = Track(directSource ? _module.LoadPanel<YLCoreSmokePanel>(prefab) : _module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel)));
                if (directSource)
                {
                    Assert.That(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel)), Is.SameAs(panel));
                }

                int opens = panel.OpenCount;
                if (directSource)
                {
                    source.Revoked = true;
                }
                else
                {
                    source.Created.Revoked = true;
                }

                Assert.That(panel && panel.IsLoaded, Is.True, "仅撤销真实lease契约，保持真实Unity实例存活以暴露提交前漏检。");
                // 不 yield：下一轮 LateUpdate 尚未清理，必须由本次重开边界自己检查。
                Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab));
                Assert.That(panel.OpenCount, Is.EqualTo(opens), "不能先跑业务 OnOpen 再发现既有资源责任已失效。");
                Assert.That(panel.Module, Is.Null);
                Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty);
                if (!directSource)
                {
                    Assert.That(source.Created.Discards, Is.EqualTo(1));
                    Assert.That(source.Created.Returns, Is.Zero);
                }
            }
            finally
            {
                _resources.BindCoreResourceProvider();
            }
        }

        [UnityTest]
        public IEnumerator UnclaimedPrefabRequestDisposalReleasesOnlyItsLease()
        {
            using var independent = _resources.Acquire<GameObject>(new ResourceAddress(YLCoreSmokeModule.Project, nameof(YLCoreSmokeView)));
            using var request = _provider.AcquirePrefabAsync(YLCoreSmokeModule.Project, nameof(YLCoreSmokeView));
            yield return Await(() => request.Status != CoreResourceStatus.Pending, "unclaimed Core Prefab result");
            Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Succeeded));
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(2));
            int notified = 0;
            request.Completed += _ => notified++;
            Assert.That(notified, Is.EqualTo(1), "Late completion observers see the stable terminal exactly once.");
            request.Dispose();
            request.Dispose();
            Assert.Throws<ObjectDisposedException>(() => request.TakeResult());
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            Assert.That(independent.IsValid, Is.True);
        }

        [UnityTest]
        public IEnumerator PanelTypeShorthandUsesOnlyTheExactRegisteredAddressAndExplicitAliases()
        {
            yield return StartModule();
            _module.PanelRoot = PanelRoot();
            var addresses = new List<string>();
            var probe = new InspectingProvider(_provider)
            {
                ResolveAddress = address =>
                {
                    addresses.Add(address);
                    return address;
                }
            };
            CoreResourceAccess.Bind(probe);
            try
            {
                var shorthand = Track(_module.LoadPanel<YLCoreSmokePanel>());
                Assert.That(addresses, Is.Not.Empty);
                Assert.That(addresses.All(address => address == typeof(YLCoreSmokePanel).Name), Is.True);
                shorthand.Close();
                addresses.Clear();
                const string FullPath = "ui/panels/YLCoreSmokePanel.prefab";
                Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(FullPath));
                Assert.That(addresses, Is.Not.Empty);
                Assert.That(addresses.All(address => address == FullPath), Is.True, "目录未登记完整地址时不得猜 basename 或删除扩展名。");
                addresses.Clear();
                // 显式别名属于 provider 的目录配置，Panel 不建立自己的地址注册或归一化机制。
                probe.ResolveAddress = address =>
                {
                    addresses.Add(address);
                    return address == FullPath ? nameof(YLCoreSmokePanel) : address;
                };
                var aliased = Track(_module.LoadPanel<YLCoreSmokePanel>(FullPath));
                Assert.That(aliased.IsLoaded, Is.True);
                Assert.That(addresses.All(address => address == FullPath), Is.True);
            }
            finally
            {
                _resources.BindCoreResourceProvider();
            }
        }

        [UnityTest]
        public IEnumerator MissingProviderFailsNamedOperationsButLeavesDirectAndAttachPathsAvailable()
        {
            Assert.That(CoreResourceAccess.Unbind(_provider), Is.True);
            Assert.That(CoreResourceAccess.Unbind(_provider), Is.False);
            _module = new YLCoreSmokeModule();
            var failed = YLFramework.Core.ModuleManager.StartUpModule(_module);
            yield return Await(() => failed.IsDone, "unbound Module table failure");
            Assert.That(failed.Exception, Is.TypeOf<CoreResourceException>());
            Assert.That(((CoreResourceException)failed.Exception).Kind, Is.EqualTo(CoreResourceError.Unavailable));
            Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
            _module = new YLCoreSmokeModule { TableName = null };
            yield return StartWithoutTables();
            var args = NewArgs();
            var sync = Assert.Throws<CoreResourceException>(() => _module.LoadView(nameof(YLCoreSmokeView), loadViewArgs: args));
            Assert.That(sync.Kind, Is.EqualTo(CoreResourceError.Unavailable));
            AssertReleasedArgs(args);
            var request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: NewArgs());
            yield return Await(() => request.IsDone, "unbound named View failure");
            AssertTerminal(request, LoadViewStatus.Failed);
            Assert.That(((CoreResourceException)request.Exception).Kind, Is.EqualTo(CoreResourceError.Unavailable));
            var attached = Own(new GameObject("YLCore86.UnboundAttach")).AddComponent<YLCoreSmokeView>();
            _module.AttachView(attached);
            Assert.That(attached.IsLoaded, Is.True);
            attached.Close();
            var directPrefab = Own(new GameObject("YLCore86.Direct", typeof(YLCoreSmokeView)));
            directPrefab.SetActive(false);
            var direct = Track(_module.LoadView<YLCoreSmokeView>(directPrefab, loadViewArgs: null));
            Assert.That(direct.IsLoaded, Is.True);
            direct.Close();
            var pool = Object.FindFirstObjectByType<YLFramework.Pooling.GameObjectPool>();
            if (pool)
            {
                pool.Clear("YL.Core.Direct/" + directPrefab.GetInstanceID());
            }

            var root = PanelRoot();
            var panelArgs = NewArgs();
            panelArgs.SetObject("retained", _module);
            var namedPanelError = Assert.Throws<CoreResourceException>(() =>
                _module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: panelArgs, root: root));
            Assert.That(namedPanelError.Kind, Is.EqualTo(CoreResourceError.Unavailable));
            AssertReleasedArgs(panelArgs);
            var panelRequest = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root);
            yield return Await(() => panelRequest.IsDone, "unbound named Panel failure");
            AssertTerminal(panelRequest, LoadViewStatus.Failed);
            Assert.That(((CoreResourceException)panelRequest.Exception).Kind, Is.EqualTo(CoreResourceError.Unavailable));
            var panelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(YLCoreSmokeModule.FixtureRoot + "/" + nameof(YLCoreSmokePanel) + ".prefab");
            var directPanel = Track(_module.LoadPanel<YLCoreSmokePanel>(panelPrefab, root: root));
            Assert.That(directPanel.IsLoaded, Is.True);
            Assert.That(directPanel.Root, Is.SameAs(root));
            directPanel.Close();
            if (pool)
            {
                pool.Clear("YL.Core.Direct/" + panelPrefab.GetInstanceID());
            }

            var externalPanelObject = Own(new GameObject("YLCore86.UnboundPanelAttach", typeof(RectTransform)));
            externalPanelObject.SetActive(false);
            externalPanelObject.transform.SetParent(root.GetLayer(0), false);
            var externalPanel = _module.AttachView(externalPanelObject.AddComponent<YLCoreSmokePanel>());
            Assert.That(externalPanel.IsLoaded, Is.True);
            Assert.That(externalPanel.Root, Is.SameAs(root));
            externalPanel.Close();
            Assert.That(externalPanelObject != null, Is.True, "Attach never transfers ownership of the external object.");
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Direct/Attach must not silently bind a resource backend.");
            _resources.BindCoreResourceProvider();
        }

        private IEnumerator StartWithoutTables()
        {
            var startup = YLFramework.Core.ModuleManager.StartUpModule(_module);
            yield return Await(() => startup.IsDone, "resource-free Module startup");
            Assert.That(startup.Error, Is.Null.Or.Empty);
        }

        [UnityTest]
        public IEnumerator NamedInstanceBucketsDoNotCrossSceneOwnership()
        {
            yield return StartModule();
            var parent = Own(new GameObject("YLCore86.SceneParent")).transform;
            var first = Track(_module.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: null));
            first.Close();
            var otherScene = SceneManager.CreateScene("YLCore86.Other" + Guid.NewGuid().ToString("N"));
            _ownedSceneInstances.Add(otherScene);
            var otherParent = Own(new GameObject("YLCore86.OtherParent")).transform;
            SceneManager.MoveGameObjectToScene(otherParent.gameObject, otherScene);
            var second = Track(_module.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), otherParent, loadViewArgs: null));
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.gameObject.scene, Is.EqualTo(otherScene));
            second.Close();
            Assert.That(Track(_module.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: null)), Is.SameAs(first));
        }

        [UnityTest]
        public IEnumerator StartupDeserializesRealJsonIntoOwnedCoreModel()
        {
            _module = new YLCoreSmokeModule();
            var startup = YLFramework.Core.ModuleManager.StartUpModule(_module);
            Assert.That(startup.IsDone, Is.False, "The real table request yields during startup.");
            Assert.That(_module.State, Is.EqualTo(ModuleState.Starting));
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.SameAs(_module));
            Assert.That(YLFramework.Core.ModuleManager.StartUpModule(_module), Is.SameAs(startup));
            Assert.That(YLFramework.Core.ModuleManager.StartUpModule<YLCoreSmokeModule>(), Is.SameAs(startup));
            var rejected = new YLCoreSmokeModule();
            Assert.Throws<InvalidOperationException>(() => YLFramework.Core.ModuleManager.StartUpModule(rejected));
            Assert.Throws<InvalidOperationException>(() => _module.Close());
            YLFramework.Async.CustomAsyncOperation<StartUpModuleRequest> foundation = startup;
            Assert.Throws<NotSupportedException>(() => foundation.Abort());
            Assert.That(startup.IsDone, Is.False);
            var current = Field(_moduleManager, "_currentModule");
            Assert.That(current, Is.SameAs(_module));
            int callbacks = 0;
            startup.AddCompleteEvent(done =>
            {
                callbacks++;
                Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
                var repeated = YLFramework.Core.ModuleManager.StartUpModule(_module);
                Assert.That(repeated.IsDone, Is.True);
                Assert.That(repeated, Is.Not.SameAs(done));
                Assert.That(repeated.Error, Is.Null.Or.Empty);
                Assert.That(Field(_moduleManager, "_currentModule"), Is.SameAs(current));
            });
            startup.AddCompleteEvent(_ => throw new InvalidOperationException("YLCore completion subscriber failure"));
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLCore completion subscriber failure"));
            yield return Await(() => startup.IsDone, "Core startup with real table and duplicate request");
            Assert.That(startup.Error, Is.Null.Or.Empty, "Subscriber failure is not startup failure.");
            Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(rejected.State, Is.EqualTo(ModuleState.Created));
            Assert.That(rejected.StartCount, Is.Zero);
            Assert.That(_module.ProjectName, Is.EqualTo(YLCoreSmokeModule.Project));
            Assert.That(_module.RowReadyAtStart, Is.True);
            Assert.That(_module.StartCount, Is.EqualTo(1));
            _row = _module.GetModel<YLCoreSmokeRow>(101);
            Assert.That(_row, Is.InstanceOf<YLFramework.Core.Model>());
            Assert.That(_row.Marker, Is.EqualTo("yl-smoke"));
            Assert.That(_row.Module, Is.SameAs(_module));
            Assert.That(_row.InitCount, Is.EqualTo(1));
            Assert.That(_module.GetModels<YLCoreSmokeRow>(), Is.EqualTo(new[] { _row }));
            _module.Close();
            Assert.That(_row.ExitCount, Is.EqualTo(1));
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.Null);
        }

        [UnityTest]
        public IEnumerator StartupDistinguishesMissingBlankInvalidAndEmptyArrayTables()
        {
            foreach (string name in new[] { string.Empty, "YLCoreMissingRows", YLCoreSmokeModule.BlankTable, YLCoreSmokeModule.InvalidTable, YLCoreSmokeModule.EmptyTable })
            {
                _module = new YLCoreSmokeModule { TableName = name };
                Assert.That(_module.ProjectName, Is.EqualTo(YLCoreSmokeModule.Project), "Default identity exists before any resource request.");
                var request = YLFramework.Core.ModuleManager.StartUpModule(_module);
                int callbacks = 0;
                request.AddCompleteEvent(done =>
                {
                    callbacks++;
                    Assert.That(_module.State, Is.EqualTo(name == YLCoreSmokeModule.EmptyTable ? ModuleState.Running : ModuleState.Closed));
                    if (name != YLCoreSmokeModule.EmptyTable)
                    {
                        Assert.That(YLFramework.Core.ModuleManager.GetModule(_module.ModuleName), Is.Null);
                    }
                });
                yield return Await(() => request.IsDone, "Core startup table " + name);
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(_module.GetModels<Model>(), Is.Empty);
                if (name == YLCoreSmokeModule.EmptyTable)
                {
                    Assert.That(request.Error, Is.Null.Or.Empty);
                    Assert.That(request.Exception, Is.Null);
                    Assert.That(_module.StartCount, Is.EqualTo(1));
                    _module.Close();
                }
                else
                {
                    Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
                    Assert.That(request.Exception, Is.Not.Null);
                    Assert.That(_module.StartCount, Is.Zero);
                }
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator BaseStartupUsesItsExplicitResourceProjectInsteadOfCurrentModule()
        {
            var current = new YLCoreConsumerModule { ProjectName = "YLCoreUnrelatedCurrentProject" };
            var baseModule = new YLCoreResourceProjectModule { ProjectName = YLCoreSmokeModule.Project };
            try
            {
                Assert.That(YLFramework.Core.ModuleManager.StartUpModule(current).Error, Is.Null.Or.Empty);
                var startup = YLFramework.Core.ModuleManager.StartUpModule(baseModule);
                yield return Await(() => startup.IsDone, "base module explicit resource project");
                Assert.That(startup.Error, Is.Null.Or.Empty);
                Assert.That(baseModule.State, Is.EqualTo(ModuleState.Running));
                Assert.That(baseModule.ProjectName, Is.EqualTo(YLCoreSmokeModule.Project));
                Assert.That(baseModule.ModuleName, Is.EqualTo(typeof(YLCoreResourceProjectModule).FullName));
                var loaded = baseModule.GetModel<YLCoreSmokeRow>(101);
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.Module, Is.SameAs(baseModule));
                Assert.That(YLFramework.Core.ModuleManager.AllBaseModules[baseModule.ModuleName], Is.SameAs(baseModule));
                Assert.That(current.GetModels<Model>(), Is.Empty);
            }
            finally
            {
                // 仅fixture结束自己的基础模块，不对全工程调用Shutdown。
                baseModule.AllowClose = true;
                baseModule.Close();
                current.Close();
                _classPool.Restore(new Module[] { baseModule, current });
            }
        }

        [UnityTest]
        public IEnumerator TemporaryTableCacheEntryIsReturnedBeforeModelInitializationAndOnInvalidJson()
        {
            foreach (string name in new[] { YLCoreSmokeModule.Table, YLCoreSmokeModule.EmptyTable, YLCoreSmokeModule.BlankTable, YLCoreSmokeModule.InvalidTable })
            {
                using var independent = _resources.Acquire<TextAsset>(new ResourceAddress(YLCoreSmokeModule.Project, name));
                try
                {
                    bool returnedAtInit = false;
                    YLCoreSmokeRow.InitAction = _ => returnedAtInit = _resources.GetDiagnosticsSnapshot().LeaseCount == 1;
                    _module = new YLCoreSmokeModule { TableName = name };
                    var request = YLFramework.Core.ModuleManager.StartUpModule(_module);
                    yield return Await(() => request.IsDone, "temporary table cache return " + name);
                    Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
                    Assert.That(independent.IsValid, Is.True, "Core must not release another consumer's lease.");
                    if (name == YLCoreSmokeModule.Table)
                    {
                        Assert.That(returnedAtInit, Is.True, "Model initialization occurs after asset return.");
                    }

                    if (name == YLCoreSmokeModule.Table || name == YLCoreSmokeModule.EmptyTable)
                    {
                        Assert.That(request.Error, Is.Null.Or.Empty);
                    }
                    else
                    {
                        Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
                    }
                }
                finally
                {
                    YLCoreSmokeRow.InitAction = null;
                    _module?.Close();
                }
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator ReportedDefaultSceneFailureRollsBackInitializedModelsWithoutChangingScenes()
        {
            var before = new List<int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                before.Add(SceneManager.GetSceneAt(i).handle);
            }

            _module = new YLCoreSmokeModule { SceneOverride = "YLCoreMissingScene_" + Guid.NewGuid().ToString("N") };
            YLCoreSmokeRow.InitAction = value => _row = value;
            var startup = YLFramework.Core.ModuleManager.StartUpModule(_module);
            yield return Await(() => startup.IsDone, "reported default scene failure");
            YLCoreSmokeRow.InitAction = null;
            Assert.That(_row, Is.Not.Null);
            Assert.That(startup.Error, Does.Contain("Default scene load failed"));
            Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
            Assert.That(_module.StartCount, Is.Zero);
            Assert.That(YLFramework.Core.ModuleManager.GetModule(_module.ModuleName), Is.Null);
            Assert.That(_row.Module, Is.Null);
            Assert.That(_row.ExitCount, Is.EqualTo(1));
            var after = new List<int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                after.Add(SceneManager.GetSceneAt(i).handle);
            }

            Assert.That(after, Is.EqualTo(before));
            _module.SceneOverride = null;
            var restarted = YLFramework.Core.ModuleManager.StartUpModule(_module);
            yield return Await(() => restarted.IsDone, "restart after scene failure");
            Assert.That(restarted.Error, Is.Null.Or.Empty);
            Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
        }

        [UnityTest]
        public IEnumerator ResourceLoadsRealPrefabAndTextSynchronouslyAndAsynchronously()
        {
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            Assert.That(prefab.GetComponent<YLCoreSmokeView>(), Is.Not.Null);
            var text = LoadFixture<TextAsset>(YLCoreSmokeModule.Table);
            Assert.That(text.text, Is.EqualTo(YLCoreSmokeModule.Json));
            foreach (string name in new[] { nameof(YLCoreSmokeView), YLCoreSmokeModule.Table })
            {
                using var request = _resources.AcquireAsync<Object>(new ResourceAddress(YLCoreSmokeModule.Project, name));
                int callbacks = 0;
                bool callbackComplete = false;
                request.Completed += done =>
                {
                    callbacks++;
                    callbackComplete = done.IsDone && !done.keepWaiting && done.Progress == 1f && done.Error == null;
                };
                yield return Await(() => request.IsDone, "Resource AcquireAsync " + name);
                Assert.That(request.Error, Is.Null);
                using var lease = request.TakeResult();
                Assert.That(lease.Asset, Is.SameAs(name == YLCoreSmokeModule.Table ? (Object)text : prefab));
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(callbackComplete, Is.True);
                OwnAsset(lease.Asset);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator NamedViewLoadAndAsyncInitializeArgsCallbacksIndexesAndReuseCache()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var parent = Own(new GameObject("YLCoreSmoke.ViewParent")).transform;
            var args = NewArgs();
            args.SetInt("token", 41);
            var view = Track(_module.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: args));
            AssertView(view, parent, 41, 1);
            YLFramework.Core.EventManager.TriggerEvent(YLCoreSmokeView.ProbeEvent);
            Assert.That(view.EventCount, Is.EqualTo(1));
            view.Close();
            AssertRemoved(view, nameof(YLCoreSmokeView));
            var nextArgs = NewArgs();
            nextArgs.SetInt("token", 73);
            var request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: nextArgs);
            int callbacks = 0;
            bool completeState = false;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                completeState = done.IsDone && !done.keepWaiting && done.Progress == 1f && string.IsNullOrEmpty(done.Error);
                Track(done.View);
            });
            yield return Await(() => request.IsDone, "Core named LoadViewAsync");
            Assert.That(request.Error, Is.Null.Or.Empty);
            Assert.That(request.View, Is.SameAs(view));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(completeState, Is.True);
            AssertView(request.View, parent, 73, 2);
            int late = 0;
            request.AddCompleteEvent(_ => late++);
            Assert.That(late, Is.EqualTo(1));
            yield return null;
            Assert.That(view.LoadedCount, Is.EqualTo(2), "View.Start must not initialize a loader-owned view again.");
            _module.Close();
            Assert.That(view.RemovedCount, Is.EqualTo(2));
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator ParallelNamedViewsCancelLateProducerWithoutInstanceOrRecycledArgsLeak()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var parent = Own(new GameObject("YLCore.AsyncParallelParent")).transform;
            var firstArgs = NewArgs();
            firstArgs.SetInt("token", 11);
            var first = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: firstArgs);
            var canceledArgs = NewArgs();
            canceledArgs.SetObject("retained", _module);
            var canceled = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: canceledArgs);
            Assert.That(first.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.That(canceled.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.That(PendingViewCount(), Is.EqualTo(2));
            Assert.That(FixtureInstanceCount(prefab), Is.Zero);
            Assert.Throws<InvalidOperationException>(() => canceledArgs.GetObject("retained"));
            Assert.Throws<InvalidOperationException>(() => _module.LoadViewAsync<YLCoreSmokeView>(
                nameof(YLCoreSmokeView), parent, loadViewArgs: canceledArgs));
            Assert.That(PendingViewCount(), Is.EqualTo(2), "Duplicate Claim cannot disturb the original pending request.");
            int canceledCallbacks = 0;
            canceled.AddCompleteEvent(_ => canceledCallbacks++);
            YLFramework.Async.CustomAsyncOperation<LoadViewRequest<YLCoreSmokeView>> foundation = canceled;
            foundation.Abort();
            canceled.Abort();
            AssertTerminal(canceled, LoadViewStatus.Canceled);
            Assert.That(canceled.Exception, Is.TypeOf<OperationCanceledException>());
            Assert.That(canceledCallbacks, Is.EqualTo(1));
            Assert.That(PendingViewCount(), Is.EqualTo(1));
            AssertReleasedArgs(canceledArgs);

            // 立即重新租用真实池对象，旧producer仍在等待同一资源，不能读取或归还新租用。
            var secondArgs = NewArgs();
            Assert.That(secondArgs, Is.SameAs(canceledArgs));
            secondArgs.SetInt("token", 73);
            secondArgs.SetObject("retained", parent);
            var observedTokens = new List<int>();
            YLCoreSmokeView.ArgsProbe = (view, args) => observedTokens.Add(args.GetInt("token"));
            var second = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: secondArgs);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(PendingViewCount(), Is.EqualTo(2));
            yield return Await(() => first.IsDone && second.IsDone, "parallel named Views after cancellation");
            yield return null;
            AssertTerminal(first, LoadViewStatus.Succeeded);
            AssertTerminal(second, LoadViewStatus.Succeeded);
            AssertTerminal(canceled, LoadViewStatus.Canceled);
            var firstView = Track(first.View);
            var secondView = Track(second.View);
            Assert.That(firstView, Is.Not.SameAs(secondView));
            Assert.That(firstView.Token, Is.EqualTo(11));
            Assert.That(secondView.Token, Is.EqualTo(73));
            CollectionAssert.AreEquivalent(new[] { 11, 73 }, observedTokens);
            CollectionAssert.AreEquivalent(new[] { firstView, secondView }, _module.GetViews<YLCoreSmokeView>());
            Assert.That(firstView.Module, Is.SameAs(_module));
            Assert.That(secondView.Module, Is.SameAs(_module));
            Assert.That(firstView.transform.parent, Is.SameAs(parent));
            Assert.That(secondView.transform.parent, Is.SameAs(parent));
            Assert.That(FixtureInstanceCount(prefab), Is.EqualTo(2), "Canceled producer must never acquire an instance.");
            Assert.That(canceledCallbacks, Is.EqualTo(1));
            Assert.That(PendingViewCount(), Is.Zero);
            AssertReleasedArgs(firstArgs);
            AssertReleasedArgs(secondArgs);
        }

        [UnityTest]
        public IEnumerator CloseInvalidatesPendingBeforeNotificationAndAllowsSameInstanceRestart()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var oldView = Track(_module.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: null));
            var oldRow = _row;
            var args = NewArgs();
            args.SetObject("retained", _module);
            var request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
            var oldLifecycle = typeof(Module).GetField("ViewLifecycle", InstancePrivate).GetValue(_module);
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
            bool inputsReleasedBeforeViewExit = false;
            oldView.RemoveAction = () =>
            {
                inputsReleasedBeforeViewExit = _module.State == ModuleState.Closing && !request.IsDone &&
                    PendingViewCount() == 0 && Field(request, "_owner") == null && Field(request, "_args") == null;
                AssertReleasedArgs(args);
            };
            StartUpModuleRequest restart = null;
            LoadViewRequest<YLCoreSmokeView> fresh = null;
            int callbacks = 0;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                AssertTerminal(done, LoadViewStatus.Invalidated);
                Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.Null);
                Assert.That(oldRow.Module, Is.Null);
                Assert.That(oldRow.ExitCount, Is.EqualTo(1));
                Assert.That(PendingViewCount(), Is.Zero);
                AssertReleasedArgs(args);
                _module.StartAction = () => fresh = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: null);
                restart = YLFramework.Core.ModuleManager.StartUpModule(_module);
            });
            _module.Close();
            Assert.That(inputsReleasedBeforeViewExit, Is.True, "Close releases pending inputs before running View cleanup.");
            oldView.RemoveAction = null;
            Assert.That(callbacks, Is.EqualTo(1), "Invalidation publishes synchronously after unregistering the owner.");
            Assert.That(restart, Is.Not.Null);
            yield return Await(() => restart.IsDone && fresh != null && fresh.IsDone, "restart same Module after pending invalidation");
            Assert.That(restart.Error, Is.Null.Or.Empty);
            Assert.That(typeof(Module).GetField("ViewLifecycle", InstancePrivate).GetValue(_module), Is.Not.SameAs(oldLifecycle));
            AssertTerminal(request, LoadViewStatus.Invalidated);
            AssertTerminal(fresh, LoadViewStatus.Succeeded);
            var view = Track(fresh.View);
            Assert.That(view, Is.SameAs(oldView));
            Assert.That(view.Module, Is.SameAs(_module));
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.EqualTo(new[] { view }));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(PendingViewCount(), Is.Zero);
            Assert.That(FixtureInstanceCount(prefab), Is.EqualTo(1), "Old invalidated producer cannot create an extra instance in the restarted owner.");
        }

        [UnityTest]
        public IEnumerator OnStartPendingViewIsInvalidatedAfterStartupThrowRollback()
        {
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var args = NewArgs();
            var failure = new InvalidOperationException("YLCore OnStart pending View rollback");
            LoadViewRequest<YLCoreSmokeView> viewRequest = null;
            StartUpModuleRequest restart = null;
            int callbacks = 0;
            _module = new YLCoreSmokeModule();
            args.SetObject("retained", _module);
            _module.StartAction = () =>
            {
                _row = _module.GetModel<YLCoreSmokeRow>(101);
                viewRequest = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
                Assert.That(viewRequest.Status, Is.EqualTo(LoadViewStatus.Pending));
                viewRequest.AddCompleteEvent(done =>
                {
                    callbacks++;
                    AssertTerminal(done, LoadViewStatus.Invalidated);
                    Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
                    Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.Null);
                    Assert.That(_row.Module, Is.Null);
                    Assert.That(_row.ExitCount, Is.EqualTo(1));
                    Assert.That(_module.GetViews<View>(), Is.Empty);
                    Assert.That(PendingViewCount(), Is.Zero);
                    AssertReleasedArgs(args);
                    _module.StartAction = null;
                    restart = YLFramework.Core.ModuleManager.StartUpModule(_module);
                });
                throw failure;
            };
            var startup = YLFramework.Core.ModuleManager.StartUpModule(_module);
            yield return Await(() => startup.IsDone, "OnStart pending View rollback");
            Assert.That(startup.Exception, Is.SameAs(failure));
            Assert.That(callbacks, Is.EqualTo(1));
            AssertTerminal(viewRequest, LoadViewStatus.Invalidated);
            Assert.That(restart, Is.Not.Null, "Startup rollback removes its old startup record before pending callbacks can restart.");
            yield return Await(() => restart.IsDone, "restart from startup rollback pending callback");
            Assert.That(restart.Error, Is.Null.Or.Empty);
            Assert.That(_module.State, Is.EqualTo(ModuleState.Running));
            yield return DrainFixtureAssetProducer();
            Assert.That(FixtureInstanceCount(prefab), Is.Zero);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator PendingParentDestructionOrSessionEndInvalidatesButLiveSceneMigrationSucceeds()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var parent = Own(new GameObject("YLCore.PendingDestroyedParent"));
            var args = NewArgs();
            args.SetObject("retained", parent);
            var destroyed = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent.transform, loadViewArgs: args);
            Assert.That(destroyed.Status, Is.EqualTo(LoadViewStatus.Pending));
            Object.DestroyImmediate(parent);
            yield return Await(() => destroyed.IsDone, "pending parent destruction");
            AssertTerminal(destroyed, LoadViewStatus.Invalidated);
            AssertReleasedArgs(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(FixtureInstanceCount(prefab), Is.Zero);

            var parentObject = Own(new GameObject("YLCore.PendingParentSession"));
            parentObject.SetActive(false);
            var parentView = parentObject.AddComponent<YLCoreSmokeView>();
            _module.AttachView(parentView);
            var ended = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), parentView.transform, loadViewArgs: null);
            parentView.Close();
            _module.AttachView(parentView);
            yield return Await(() => ended.IsDone, "pending parent session replaced on live object");
            AssertTerminal(ended, LoadViewStatus.Invalidated);
            Assert.That(parentView.Module, Is.SameAs(_module));
            Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { parentView }));
            Assert.That(FixtureInstanceCount(prefab), Is.Zero);
            parentView.Close();

            var destination = SceneManager.CreateScene("YLCore.PendingParentDestination");
            _ownedSceneInstances.Add(destination);
            var moved = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), parentObject.transform, loadViewArgs: null);
            SceneManager.MoveGameObjectToScene(parentObject, destination);
            yield return Await(() => moved.IsDone, "pending live parent scene migration");
            AssertTerminal(moved, LoadViewStatus.Succeeded);
            var view = Track(moved.View);
            Assert.That(view.transform.parent, Is.SameAs(parentObject.transform));
            Assert.That(view.gameObject.scene, Is.EqualTo(destination));
            Assert.That(view.Module, Is.SameAs(_module));
            Assert.That(PendingViewCount(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator ParentlessPendingViewKeepsCapturedSceneAndInvalidatesWhenItUnloads()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var target = SceneManager.CreateScene("YLCore.PendingCapturedScene");
            _ownedSceneInstances.Add(target);
            SceneManager.SetActiveScene(target);
            var request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: null);
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
            SceneManager.SetActiveScene(_previousActive);
            yield return Await(() => request.IsDone, "parentless captured scene after active switch");
            AssertTerminal(request, LoadViewStatus.Succeeded);
            var view = Track(request.View);
            Assert.That(view.gameObject.scene, Is.EqualTo(target));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(_previousActive));
            view.Close();

            var unloading = SceneManager.CreateScene("YLCore.PendingUnloadedScene");
            _ownedSceneInstances.Add(unloading);
            SceneManager.SetActiveScene(unloading);
            var args = NewArgs();
            args.SetObject("retained", _module);
            var invalidated = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
            Assert.That(invalidated.Status, Is.EqualTo(LoadViewStatus.Pending));
            SceneManager.SetActiveScene(_previousActive);
            var unload = SceneManager.UnloadSceneAsync(unloading);
            yield return AwaitUnity(unload, "unload pending captured scene");
            yield return Await(() => invalidated.IsDone, "pending captured scene invalidation");
            AssertTerminal(invalidated, LoadViewStatus.Invalidated);
            AssertReleasedArgs(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(PendingViewCount(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator RealMissingAndArgsInitializationFailureCompleteWithoutOwnerOrInputResidue()
        {
            yield return StartModule();
            var args = NewArgs();
            args.SetObject("retained", _module);
            var missing = _module.LoadViewAsync<YLCoreSmokeView>("YLCoreMissingView_" + Guid.NewGuid().ToString("N"), loadViewArgs: args);
            int callbacks = 0;
            missing.AddCompleteEvent(_ => callbacks++);
            yield return Await(() => missing.IsDone, "real missing named prefab");
            AssertTerminal(missing, LoadViewStatus.Failed);
            Assert.That(missing.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(callbacks, Is.EqualTo(1));
            AssertReleasedArgs(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);

            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var failure = new InvalidOperationException("YLCore async args initialization failure");
            var cleanupFailure = new InvalidOperationException("YLCore async View rollback cleanup failure");
            YLCoreSmokeView failedView = null;
            LoadViewRequest<YLCoreSmokeView> failed = null;
            args = NewArgs();
            args.SetObject("retained", _row);
            YLCoreSmokeView.ArgsProbe = (view, borrowed) =>
            {
                failedView = Track(view);
                view.BoundModel = (Model)borrowed.GetObject("retained");
                view.RemoveAction = () =>
                {
                    failed.Abort();
                    throw cleanupFailure;
                };
                throw failure;
            };
            failed = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
            callbacks = 0;
            failed.AddCompleteEvent(_ => callbacks++);
            yield return Await(() => failed.IsDone, "real async hierarchy initialization failure");
            AssertTerminal(failed, LoadViewStatus.Failed);
            Assert.That(failed.Exception, Is.SameAs(failure));
            Assert.That(failed.Exception.Data["YLFramework.Core.CleanupException"], Is.SameAs(cleanupFailure));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(failedView, Is.Not.Null);
            Assert.That(failedView.Module, Is.Null);
            Assert.That(failedView.Model, Is.Null);
            Assert.That(failedView.RemovedCount, Is.EqualTo(1));
            Assert.That(failedView.ClosedCount, Is.Zero);
            Assert.That(_row.Module, Is.SameAs(_module));
            Assert.That(_row.ExitCount, Is.Zero);
            AssertReleasedArgs(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(PendingViewCount(), Is.Zero);
            yield return null;
            Assert.That(failedView == null, Is.True, "Failed initialization discards the obtained instance.");
        }

        [UnityTest]
        public IEnumerator ArgsCallbackAbortDefersNotificationUntilRollbackThenAllowsReentrantLoad()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var args = NewArgs();
            args.SetInt("token", 61);
            args.SetObject("retained", _row);
            LoadViewRequest<YLCoreSmokeView> request = null;
            LoadViewRequest<YLCoreSmokeView> next = null;
            YLCoreSmokeView canceledView = null;
            bool stillBorrowedAfterAbort = false;
            YLCoreSmokeView.ArgsProbe = (view, borrowed) =>
            {
                canceledView = Track(view);
                view.BoundModel = (Model)borrowed.GetObject("retained");
                request.Abort();
                request.Abort();
                stillBorrowedAfterAbort = !request.IsDone && request.Status == LoadViewStatus.Pending &&
                    borrowed.GetInt("token") == 61 && PendingViewCount() == 1;
                YLCoreSmokeView.ArgsProbe = null;
            };
            request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
            int callbacks = 0;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                AssertTerminal(done, LoadViewStatus.Canceled);
                Assert.That(canceledView.Module, Is.Null);
                Assert.That(canceledView.Model, Is.Null);
                Assert.That(canceledView.IsLoaded, Is.False);
                Assert.That(canceledView.RemovedCount, Is.EqualTo(1));
                Assert.That(canceledView.ClosedCount, Is.Zero);
                Assert.That(_module.GetViews<View>(), Is.Empty);
                Assert.That(PendingViewCount(), Is.Zero);
                AssertReleasedArgs(args);
                done.Abort();
                var nextArgs = NewArgs();
                nextArgs.SetInt("token", 62);
                next = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: nextArgs);
            });
            yield return Await(() => request.IsDone, "args Abort rollback");
            Assert.That(next, Is.Not.Null, "Completion callback starts a new request after the rollback transaction ends.");
            yield return Await(() => next.IsDone, "reentrant named load after args Abort");
            Assert.That(stillBorrowedAfterAbort, Is.True);
            Assert.That(callbacks, Is.EqualTo(1));
            AssertTerminal(request, LoadViewStatus.Canceled);
            Assert.That(request.Exception, Is.TypeOf<OperationCanceledException>());
            AssertTerminal(next, LoadViewStatus.Succeeded);
            var nextView = Track(next.View);
            Assert.That(nextView.Token, Is.EqualTo(62));
            Assert.That(nextView.Module, Is.SameAs(_module));
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.EqualTo(new[] { nextView }));
            Assert.That(PendingViewCount(), Is.Zero);
            Assert.That(_row.ExitCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ArgsModelRemovedWhilePendingOrReregisteredDuringInitializationCannotCommitBinding()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var model = _module.LoadModel<YLCoreProbeModel>();
            for (int phase = 0; phase < 2; phase++)
            {
                var args = NewArgs();
                args.SetObject("retained", model);
                YLCoreSmokeView failedView = null;
                YLCoreSmokeView.ArgsProbe = (view, borrowed) =>
                {
                    failedView = Track(view);
                    view.BoundModel = (Model)borrowed.GetObject("retained");
                    if (phase == 1)
                    {
                        _module.RemoveModel(model);
                        _module.AddModel(model);
                        Assert.That(view.Model, Is.Null, "A new registration cannot revive the old binding token.");
                    }
                };
                var request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
                Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
                if (phase == 0)
                {
                    _module.RemoveModel(model);
                }

                yield return Await(() => request.IsDone, "invalid args Model registration " + phase);
                AssertTerminal(request, LoadViewStatus.Failed);
                Assert.That(request.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(failedView, Is.Not.Null);
                Assert.That(failedView.Module, Is.Null);
                Assert.That(failedView.Model, Is.Null);
                Assert.That(failedView.IsLoaded, Is.False);
                Assert.That(failedView.RemovedCount, Is.EqualTo(1));
                Assert.That(failedView.ClosedCount, Is.Zero);
                Assert.That(failedView.UnboundCount, Is.EqualTo(phase));
                AssertReleasedArgs(args);
                Assert.That(_module.GetViews<View>(), Is.Empty);
                Assert.That(PendingViewCount(), Is.Zero);
                Assert.That(model.ExitCount, Is.EqualTo(phase + 1), "Only explicit business removal exits the Model.");
                if (phase == 0)
                {
                    _module.AddModel(model);
                }

                Assert.That(model.Module, Is.SameAs(_module));
                yield return null;
                Assert.That(failedView == null, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator CompletionThrowAndRepeatedAbortDoNotRewriteSuccessAndArgsSelectCurrentModel()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var args = NewArgs();
            args.SetInt("token", 95);
            YLCoreSmokeView.ArgsProbe = (view, _) => view.BoundModel = _module.GetModel<YLCoreSmokeRow>(101);
            var request = _module.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
            var oldRow = _row;
            _module.RemoveModel(oldRow);
            _row = new YLCoreSmokeRow { Id = 101, Marker = "replacement" };
            _module.AddModel(_row);
            int callbacks = 0;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                done.Abort();
                throw new InvalidOperationException("YLCore async View completion subscriber failure");
            });
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLCore async View completion subscriber failure"));
            yield return Await(() => request.IsDone, "async View success with throwing completion callback");
            AssertTerminal(request, LoadViewStatus.Succeeded);
            var view = Track(request.View);
            Assert.That(view.Model, Is.SameAs(_row), "Args callback binds the valid registration at callback time, not the old request-time Model.");
            Assert.That(oldRow.Module, Is.Null);
            Assert.That(oldRow.ExitCount, Is.EqualTo(1));
            Assert.That(view.Token, Is.EqualTo(95));
            Assert.That(callbacks, Is.EqualTo(1));
            AssertReleasedArgs(args);
            request.Abort();
            request.Abort();
            int late = 0;
            request.AddCompleteEvent(_ => late++);
            yield return null;
            AssertTerminal(request, LoadViewStatus.Succeeded);
            Assert.That(request.View, Is.SameAs(view));
            Assert.That(view.IsLoaded, Is.True);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(late, Is.EqualTo(1));
            Assert.That(PendingViewCount(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator NamedSynchronousFailuresAndColdCacheReuseKeepArgsAndOwnerBoundaries()
        {
            yield return StartModule();
            var args = NewArgs();
            args.SetObject("retained", _module);
            Assert.Throws<InvalidOperationException>(() => _module.LoadView("YLCoreMissingView", loadViewArgs: args));
            Assert.Throws<ObjectDisposedException>(() => args.GetObject("retained"));
            Assert.That(_module.GetViews<YLFramework.Core.View>(), Is.Empty);

            args = NewArgs();
            Assert.Throws<InvalidCastException>(() => _module.LoadView<YLCoreSmokePanel>(nameof(YLCoreSmokeView), loadViewArgs: args));
            Assert.Throws<ObjectDisposedException>(() => args.GetInt("token"));
#pragma warning disable CS0618
            Assert.Throws<InvalidCastException>(() => _module.LoadView<YLCoreSmokePanel>(nameof(YLCoreSmokeView), null, new object[0]));
#pragma warning restore CS0618
            Assert.That(_module.GetViews<YLFramework.Core.View>(), Is.Empty);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(YLCoreSmokeModule.FixtureRoot + "/" + nameof(YLCoreSmokeView) + ".prefab");
            Assert.That(FixtureInstanceCount(prefab), Is.Zero, "Prefab validation precedes instance checkout.");

            args = NewArgs();
            args.SetInt("token", 52);
            var parent = Own(new GameObject("YLCoreSmoke.ColdSyncParent")).transform;
            // 不预调用LoadFixture，实际走命名资产取得、校验、实例化的冷入口。
            var view = Track(_module.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, loadViewArgs: args));
            OwnAsset(prefab);
            AssertView(view, parent, 52, 1);
            Assert.Throws<ObjectDisposedException>(() => args.GetInt("token"));
            var model = _module.LoadModel<YLCoreProbeModel>();
            view.BoundModel = model;
            var removed = 0;
            view.ViewRemoved = () => removed++;
            view.Close();
#pragma warning disable CS0618
            var reused = Track(_module.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), parent, new object[] { "new session" }));
#pragma warning restore CS0618
            Assert.That(reused, Is.SameAs(view));
            AssertView(reused, parent, 0, 2);
            Assert.That(reused.Model, Is.Null);
            Assert.That(reused.ViewRemoved, Is.Null);
            Assert.That(reused.Parameter, Is.EqualTo("new session"));
            reused.Close();
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(model.Module, Is.SameAs(_module));
            Assert.That(model.ExitCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator NamedResourceFallbackUsesBaseProjectButKeepsConsumerOwnership()
        {
            yield return StartModule();
            var consumer = new YLCoreDirectViewModule { ProjectName = "YLCoreMissingViewProject" };
            var resourceModule = new YLCoreBaseProbeModule { ProjectName = YLCoreSmokeModule.Project };
            try
            {
                var startup = YLFramework.Core.ModuleManager.StartUpModule(resourceModule);
                yield return Await(() => startup.IsDone, "View resource base module");
                Assert.That(startup.Error, Is.Null.Or.Empty);
                startup = YLFramework.Core.ModuleManager.StartUpModule(consumer);
                yield return Await(() => startup.IsDone, "View consumer module");
                Assert.That(startup.Error, Is.Null.Or.Empty);
                var candidates = typeof(YLFramework.Core.ModuleManager).GetMethod("ViewResourceProjects", StaticPrivate);
                Assert.That(candidates.Invoke(null, new object[] { consumer }),
                    Is.EqualTo(new[] { consumer.ProjectName, YLCoreSmokeModule.Project }));
                consumer.ProjectName = YLCoreSmokeModule.Project;
                Assert.That(candidates.Invoke(null, new object[] { consumer }), Is.EqualTo(new[] { YLCoreSmokeModule.Project }));
                consumer.ProjectName = "YLCoreMissingViewProject";
                var args = NewArgs();
                args.SetInt("token", 84);
                var view = Track(consumer.LoadView<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args));
                OwnAsset(AssetDatabase.LoadAssetAtPath<GameObject>(YLCoreSmokeModule.FixtureRoot + "/" + nameof(YLCoreSmokeView) + ".prefab"));
                Assert.That(view.Module, Is.SameAs(consumer));
                Assert.That(view.Token, Is.EqualTo(84));
                Assert.That(view.IsLoaded, Is.True);
                Assert.That(consumer.GetView<YLCoreSmokeView>(), Is.SameAs(view));
                Assert.That(_module.GetViews<YLFramework.Core.View>(), Is.Empty);
                Assert.That(resourceModule.GetViews<YLFramework.Core.View>(), Is.Empty);
                Assert.Throws<ObjectDisposedException>(() => args.GetInt("token"));
                view.Close();
                args = NewArgs();
                args.SetInt("token", 85);
                var asyncFallback = consumer.LoadViewAsync<YLCoreSmokeView>(nameof(YLCoreSmokeView), loadViewArgs: args);
                yield return Await(() => asyncFallback.IsDone, "async View fallback across resource projects");
                AssertTerminal(asyncFallback, LoadViewStatus.Succeeded);
                Assert.That(asyncFallback.View, Is.SameAs(view));
                Assert.That(view.Module, Is.SameAs(consumer));
                Assert.That(view.Token, Is.EqualTo(85));
                Assert.That(resourceModule.GetViews<View>(), Is.Empty);
                Assert.That(_module.GetViews<View>(), Is.Empty);
                AssertReleasedArgs(args);
                consumer.Close();
                Assert.That(view.Module, Is.Null);
                Assert.That(resourceModule.State, Is.EqualTo(YLFramework.Core.ModuleState.Running));
            }
            finally
            {
                consumer.Close();
                resourceModule.AllowClose = true;
                resourceModule.Close();
                _classPool.Restore(new YLFramework.Core.Module[] { consumer, resourceModule });
            }
        }

        [UnityTest]
        public IEnumerator NamedPanelsUseRealManagerCanvasEventSystemAsyncAndCacheIndexes()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            Assert.That(prefab.GetComponent<Canvas>(), Is.Null);
            Assert.That(prefab.GetComponent<RectTransform>(), Is.Not.Null);
            var root = PanelRoot();
            int loadingProbes = 0;
            int argsProbes = 0;
            YLCoreSmokePanel.LoadingProbe = panel =>
            {
                loadingProbes++;
                AssertPreparedPanel(panel, root);
            };
            YLCoreSmokePanel.ArgsProbe = (panel, _) =>
            {
                argsProbes++;
                AssertPreparedPanel(panel, root);
            };
            var args = NewArgs();
            args.SetInt("token", 17);
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                loadViewArgs: args, root: root));
            AssertPanel(first, 17, 1);
            Assert.That(first.OpenCount, Is.EqualTo(1));
            first.UpdateDisplay(new PanelOverrides { Priority = -37 });
            Assert.That(first.EffectiveSettings.priority, Is.EqualTo(-37));
            Assert.That(PanelOrders(root)[first], Is.EqualTo(first.SortingOrder));
            Assert.That(first.Canvas.sortingOrder, Is.EqualTo(first.SortingOrder));
            first.gameObject.SetActive(false);
            var siblingObject = Own(new GameObject("YLCoreSmoke.InactiveOrderSibling", typeof(RectTransform)));
            siblingObject.SetActive(false);
            siblingObject.transform.SetParent(first.transform.parent);
            var sibling = _module.AttachView(siblingObject.AddComponent<YLCoreSmokePanel>());
            Assert.That(PanelOrders(root).Contains(first), Is.True,
                "An inactive but registered Panel must not be pruned while calculating the next order.");
            Assert.That(sibling.SortingOrder, Is.GreaterThan(first.SortingOrder));
            sibling.Close();
            first.gameObject.SetActive(true);
            Assert.That(first.Canvas.overrideSorting, Is.True);
            Assert.That(first.Canvas.sortingOrder, Is.EqualTo(first.SortingOrder));
            Assert.That(first.Root, Is.SameAs(root));
            Assert.That(first.transform.parent, Is.SameAs(root.GetLayer(0)));
            Assert.That(Singleton(typeof(YLFramework.Core.PanelManager)), Is.Not.Null);
            Assert.That(EventSystem.current, Is.Not.Null);
            var managerSystem = (GameObject)Field(Singleton(typeof(YLFramework.Core.PanelManager)), "_eventSystem");
            if (managerSystem && !_previousEventSystems.Contains(managerSystem.GetInstanceID()))
            {
                Assert.That(managerSystem.GetComponent<EventSystem>(), Is.SameAs(EventSystem.current));
                Assert.That(managerSystem.name, Is.EqualTo("EventSystem(Created By YLFramework.Core)"));
            }
            var canvasOnPanel = first.Canvas;
            first.Close();
            AssertRemoved(first, nameof(YLCoreSmokePanel));
            Assert.That(PanelOrders(root).Contains(first), Is.False);
            var nextArgs = NewArgs();
            nextArgs.SetInt("token", 29);
            var request = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                loadViewArgs: nextArgs, root: root);
            Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.That(PendingViewCount(), Is.EqualTo(1));
            int callbacks = 0;
            bool completeState = false;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                completeState = done.IsDone && done.Progress == 1f && !done.keepWaiting && string.IsNullOrEmpty(done.Error);
                Track(done.Panel);
            });
            yield return Await(() => request.IsDone, "Core named LoadPanelAsync");
            Assert.That(request.Error, Is.Null.Or.Empty);
            AssertTerminal(request, LoadViewStatus.Succeeded);
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(completeState, Is.True);
            Assert.That(request.Panel, Is.SameAs(first));
            Assert.That(first.Canvas, Is.SameAs(canvasOnPanel));
            Assert.That(first.GetComponents<Canvas>(), Has.Length.EqualTo(1));
            AssertPanel(first, 29, 2);
            Assert.That(first.GetComponents<UnityEngine.UI.GraphicRaycaster>(), Has.Length.EqualTo(1));
            Assert.That(loadingProbes, Is.EqualTo(3));
            Assert.That(argsProbes, Is.EqualTo(3));
            _module.Close();
            Assert.That(first.RemovedCount, Is.EqualTo(2));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty);
            Assert.That(PanelOrders(root).Contains(first), Is.False);
        }

        [UnityTest]
        public IEnumerator ParallelNamedSinglesUseLatestUncanceledArgsAndOnePendingRecordPerConsumer()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot();
            var firstArgs = NewArgs();
            firstArgs.SetInt("token", 11);
            var first = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: firstArgs, root: root);
            var secondArgs = NewArgs();
            secondArgs.SetInt("token", 22);
            var second = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: secondArgs, root: root);
            var canceledArgs = NewArgs();
            canceledArgs.SetInt("token", 33);
            var canceled = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: canceledArgs, root: root);
            Assert.That(first.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.That(second.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.That(PendingViewCount(), Is.EqualTo(3));
            var initializedTokens = new List<int>();
            var openedTokens = new List<int>();
            YLCoreSmokePanel.ArgsProbe = (_, borrowed) => initializedTokens.Add(borrowed.GetInt("token"));
            YLCoreSmokePanel.UpdateProbe = (panel, borrowed) =>
            {
                Assert.That(panel.IsLoaded, Is.True, "OnOpen follows View initialization but precedes request publication.");
                openedTokens.Add(borrowed.GetInt("token"));
            };
            int canceledCallbacks = 0;
            canceled.AddCompleteEvent(_ => canceledCallbacks++);
            canceled.Abort();
            canceled.Abort();
            AssertTerminal(canceled, LoadViewStatus.Canceled);
            AssertReleasedArgs(canceledArgs);
            Assert.That(PendingViewCount(), Is.EqualTo(2));
            yield return Await(() => first.IsDone && second.IsDone, "coalesced named Singles choose latest valid intent");
            AssertTerminal(first, LoadViewStatus.Succeeded);
            AssertTerminal(second, LoadViewStatus.Succeeded);
            var panel = Track(first.Panel);
            Assert.That(second.Panel, Is.SameAs(panel));
            Assert.That(panel.Token, Is.EqualTo(22));
            Assert.That(panel.EffectiveSettings.instanceMode, Is.EqualTo(PanelInstanceMode.Single));
            Assert.That(panel.LoadedCount, Is.EqualTo(1));
            Assert.That(panel.OpenCount, Is.EqualTo(1));
            Assert.That(initializedTokens, Is.EqualTo(new[] { 22 }));
            Assert.That(openedTokens, Is.EqualTo(new[] { 22 }));
            Assert.That(FixtureInstanceCount(prefab), Is.EqualTo(1));
            Assert.That(canceledCallbacks, Is.EqualTo(1));
            Assert.That(PendingViewCount(), Is.Zero);
            AssertReleasedArgs(firstArgs);
            AssertReleasedArgs(secondArgs);
        }

        [UnityTest]
        public IEnumerator CompletionCloseAndCachedObjectReuseInvalidateOtherConsumersOfTheOriginalSession()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot();
            var first = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root);
            var second = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root);
            YLCoreSmokePanel original = null;
            YLCoreSmokePanel fresh = null;
            int firstCallbacks = 0;
            int secondCallbacks = 0;
            first.AddCompleteEvent(done =>
            {
                firstCallbacks++;
                AssertTerminal(done, LoadViewStatus.Succeeded);
                Assert.That(PendingViewCount(), Is.Zero, "Every coalesced consumer releases its inputs before any publication callback.");
                original = Track(done.Panel);
                original.Close();
                var args = NewArgs();
                args.SetInt("token", 99);
                fresh = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: args, root: root));
                AssertReleasedArgs(args);
            });
            second.AddCompleteEvent(done =>
            {
                secondCallbacks++;
                AssertTerminal(done, LoadViewStatus.Invalidated);
            });
            yield return Await(() => first.IsDone && second.IsDone, "coalesced delivery after completion closes and reuses instance");
            Assert.That(firstCallbacks, Is.EqualTo(1));
            Assert.That(secondCallbacks, Is.EqualTo(1));
            Assert.That(fresh, Is.SameAs(original), "Reference equality must not substitute for the original session token.");
            Assert.That(fresh.IsLoaded, Is.True);
            Assert.That(fresh.Token, Is.EqualTo(99));
            Assert.That(fresh.LoadedCount, Is.EqualTo(2));
            Assert.That(fresh.OpenCount, Is.EqualTo(2));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EqualTo(new[] { fresh }));
            AssertTerminal(first, LoadViewStatus.Succeeded);
            AssertTerminal(second, LoadViewStatus.Invalidated);
        }

        [UnityTest]
        public IEnumerator NamedRequestsJoiningDirectSingleKeepSourceLeasesUntilDiscardAndReleaseOnlyTheirArgs()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            int independentLeases = _resources.GetDiagnosticsSnapshot().LeaseCount;
            var root = PanelRoot();
            _module.PanelRoot = root;
            var directArgs = NewArgs();
            directArgs.SetInt("token", 11);
            var direct = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, loadViewArgs: directArgs));
            var instance = (CoreInstanceLease)Field(direct, "BackendLease");
            Assert.That(instance.IsValid, Is.True);
            AssertReleasedArgs(directArgs);
            var probe = new InspectingProvider(_provider);
            probe.BeforePrefabDispose = () =>
            {
                Assert.That(instance.IsValid, Is.False, "The direct checkout must be discarded before releasing its named source pin.");
                Assert.That(direct.gameObject.activeSelf, Is.False);
                Assert.That(direct.Module, Is.Null);
            };
            CoreResourceAccess.Bind(probe);
            try
            {
                var namedArgs = NewArgs();
                namedArgs.SetInt("token", 22);
                Assert.That(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: namedArgs), Is.SameAs(direct));
                AssertReleasedArgs(namedArgs);
                Assert.That(probe.PrefabDisposals, Is.Zero, "A completed request cannot release the direct session's source pin.");
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(independentLeases + 1));

                var asyncArgs = NewArgs();
                asyncArgs.SetInt("token", 33);
                asyncArgs.SetObject("retained", _module);
                var request = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: asyncArgs);
                Assert.That(request.Status, Is.EqualTo(LoadViewStatus.Pending));
                Assert.Throws<InvalidOperationException>(() => _module.LoadPanel<YLCoreSmokePanel>(prefab, loadViewArgs: asyncArgs));
                Assert.Throws<InvalidOperationException>(() => asyncArgs.GetObject("retained"));
                Assert.That(PendingViewCount(), Is.EqualTo(1), "A failed duplicate Claim must leave the original consumer intact.");
                yield return Await(() => request.IsDone, "named request joins direct Single session");
                AssertTerminal(request, LoadViewStatus.Succeeded);
                Assert.That(request.Panel, Is.SameAs(direct));
                AssertReleasedArgs(asyncArgs);
                Assert.That(direct.Token, Is.EqualTo(33));
                Assert.That(direct.LoadedCount, Is.EqualTo(1));
                Assert.That(direct.ArgsCount, Is.EqualTo(1));
                Assert.That(direct.OpenCount, Is.EqualTo(3));
                Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EqualTo(new[] { direct }));
                Assert.That(probe.PrefabDisposals, Is.Zero);
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(independentLeases + 2));

                bool pinAliveDuringRemove = false;
                direct.RemoveAction = () => pinAliveDuringRemove = instance.IsValid && probe.PrefabDisposals == 0 &&
                    _resources.GetDiagnosticsSnapshot().LeaseCount == independentLeases + 2;
                direct.Close();
                Assert.That(pinAliveDuringRemove, Is.True, "Source responsibility covers the complete business removal phase.");
                Assert.That(probe.PrefabDisposals, Is.EqualTo(1));
                Assert.That(instance.IsValid, Is.False);
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(independentLeases));
                Assert.That(_module.GetViews<Panel>(), Is.Empty);
                yield return null;
                Assert.That(direct == null, Is.True, "A direct cache cannot retain an instance whose provider source pin was released.");
                Assert.That(FixtureInstanceCount(prefab), Is.Zero);
            }
            finally
            {
                _resources.BindCoreResourceProvider();
            }
        }

        [UnityTest]
        public IEnumerator CoordinatorResetCannotReleaseDirectSourcePinBeforeCoreInstanceRetirement()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            int baseline = _resources.GetDiagnosticsSnapshot().LeaseCount;
            _module.PanelRoot = PanelRoot();
            var direct = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab));
            var instance = (CoreInstanceLease)Field(direct, "BackendLease");
            var probe = new InspectingProvider(_provider);
            CoreResourceAccess.Bind(probe);
            try
            {
                probe.BeforePrefabDispose = () => Assert.That(instance.IsValid, Is.False);
                Assert.That(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel)), Is.SameAs(direct));
                typeof(PanelManager).GetMethod("ResetSession", StaticPrivate).Invoke(null, null);
                Assert.That(instance.IsValid, Is.True);
                Assert.That(probe.PrefabDisposals, Is.Zero, "协调器不是来源责任 owner，先 reset 不得先释放 pin。");
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(baseline + 1));
                direct.Close();
                Assert.That(probe.PrefabDisposals, Is.EqualTo(1));
                Assert.That(instance.IsValid, Is.False);
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(baseline));
            }
            finally
            {
                _resources.BindCoreResourceProvider();
            }
        }

        [UnityTest]
        public IEnumerator UnknownAliasResolvedAfterCloseCannotReopenOrAcquireTheReusedSession()
        {
            yield return StartModule();
            _module.PanelRoot = PanelRoot();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var original = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab));
            object originalSession = Field(original, "Session");
            YLCoreSmokePanel replacement = null;
            var probe = new InspectingProvider(_provider)
            {
                ResolveAddress = address => address == "ui/late-alias.prefab" ? nameof(YLCoreSmokePanel) : address
            };
            probe.AfterAcquire = _ =>
            {
                probe.AfterAcquire = null;
                original.Close();
                var args = NewArgs();
                args.SetInt("token", 91);
                replacement = Track(_module.LoadPanel<YLCoreSmokePanel>(prefab, args));
            };
            CoreResourceAccess.Bind(probe);
            try
            {
                var args = NewArgs();
                args.SetInt("token", 3);
                var old = _module.LoadPanelAsync<YLCoreSmokePanel>("ui/late-alias.prefab", args);
                Assert.That(old.Status, Is.EqualTo(LoadViewStatus.Pending));
                yield return Await(() => old.IsDone, "unknown alias resolved after Single scope Close");
                Assert.That(old.Status, Is.EqualTo(LoadViewStatus.Invalidated));
                Assert.That(old.Panel, Is.Null);
                Assert.That(replacement, Is.SameAs(original), "真实池复用同一对象才能覆盖原 Session 屏障。");
                Assert.That(Field(replacement, "Session"), Is.Not.SameAs(originalSession));
                Assert.That(replacement.Token, Is.EqualTo(91));
                Assert.That(replacement.OpenCount, Is.EqualTo(2));
                Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EqualTo(new[] { replacement }));
                AssertReleasedArgs(args);
            }
            finally
            {
                _resources.BindCoreResourceProvider();
            }
        }

        [UnityTest]
        public IEnumerator NamedInstanceOwnsIndependentPinWithoutKeepingTemporarySourceLease()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            int independentLeases = _resources.GetDiagnosticsSnapshot().LeaseCount;
            var root = PanelRoot();
            YLCoreSmokePanel prepared = null;
            YLCoreSmokePanel.UpdateProbe = (panel, _) => prepared = Track(panel);
            var probe = new InspectingProvider(_provider);
            probe.BeforePrefabDispose = () =>
            {
                Assert.That(prepared && prepared.IsLoaded, Is.True);
                Assert.That(((CoreInstanceLease)Field(prepared, "BackendLease")).IsValid, Is.True,
                    "Provider instances must keep an independent source pin after temporary Prefab lease disposal.");
            };
            CoreResourceAccess.Bind(probe);
            try
            {
                var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root));
                Assert.That(panel, Is.SameAs(prepared));
                Assert.That(probe.PrefabDisposals, Is.EqualTo(1), "Only direct sessions need to retain a joined named source lease.");
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(independentLeases + 1));
                panel.Close();
                Assert.That(panel.IsLoaded, Is.False);
                Assert.That(panel.gameObject.activeSelf, Is.False);
                Assert.That(probe.PrefabDisposals, Is.EqualTo(1));
                Assert.That(FixtureInstanceCount(prefab), Is.EqualTo(1), "Healthy provider instances remain eligible for their backend cache.");
                _resources.ClearInstanceCaches();
                yield return null;
                Assert.That(panel == null, Is.True);
                Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(independentLeases));
            }
            finally
            {
                _resources.BindCoreResourceProvider();
            }
        }

        [UnityTest]
        public IEnumerator OrdinaryViewRejectsPanelBeforeCheckoutAndBareCanvasAttachCanRetryUnderRealRoot()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            foreach (bool genericPanel in new[] { false, true })
            {
                var rejected = NewArgs();
                rejected.SetObject("retained", _module);
                var error = Assert.Throws<InvalidOperationException>(() =>
                {
                    if (genericPanel)
                    {
                        _module.LoadView<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: rejected);
                    }
                    else
                    {
                        _module.LoadView<View>(nameof(YLCoreSmokePanel), loadViewArgs: rejected);
                    }
                });
                StringAssert.Contains("LoadPanel", error.Message);
                AssertReleasedArgs(rejected);
            }
            var asyncArgs = NewArgs();
            asyncArgs.SetObject("retained", _module);
            var rejectedAsync = _module.LoadViewAsync<View>(nameof(YLCoreSmokePanel), loadViewArgs: asyncArgs);
            yield return Await(() => rejectedAsync.IsDone, "ordinary View rejects actual root Panel");
            AssertTerminal(rejectedAsync, LoadViewStatus.Failed);
            Assert.That(rejectedAsync.Exception, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("LoadPanel", rejectedAsync.Exception.Message);
            AssertReleasedArgs(asyncArgs);
            Assert.That(FixtureInstanceCount(prefab), Is.Zero, "Migration validation must precede instance checkout.");
            Assert.That(_module.GetViews<View>(), Is.Empty);
            var invalidObject = Own(new GameObject("YLCore.PanelInvalidCanvas", typeof(RectTransform), typeof(Canvas)));
            invalidObject.SetActive(false);
            var invalid = invalidObject.AddComponent<YLCoreSmokePanel>();
            var args = NewArgs();
            args.SetObject("retained", _module);
            var attachError = Assert.Throws<InvalidOperationException>(() => _module.AttachView(invalid, args));
            StringAssert.Contains("UIRootContext", attachError.Message);
            Assert.That(invalid.LoadedCount, Is.Zero);
            Assert.That(invalidObject && !invalidObject.activeSelf, Is.True);
            Assert.That(invalidObject.transform.parent, Is.Null);
            AssertReleasedArgs(args);
            AssertPanelReleased(invalid);
            var root = PanelRoot();
            invalidObject.transform.SetParent(root.GetLayer(0), false);
            YLCoreSmokePanel.LoadingProbe = panel => AssertPreparedPanel(panel, root);
            _module.AttachView(invalid);
            Assert.That(invalid.IsLoaded, Is.True);
            Assert.That(invalid.GetComponents<Canvas>(), Has.Length.EqualTo(1));
            invalid.Close();
            AssertPanelReleased(invalid);

            var secondRoot = PanelRoot("YLCore.SecondCanvasRoot");
            int probes = 0;
            UIRootContext expected = root;
            YLCoreSmokePanel.LoadingProbe = panel =>
            {
                probes++;
                AssertPreparedPanel(panel, expected);
                Assert.That(panel.Canvas, Is.Not.SameAs(expected.Canvas), "Each Panel must own its Canvas, not borrow its root's.");
            };
            var shared = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: null, root: root));
            var ownCanvas = shared.Canvas;
            shared.Close();
            AssertPanelReleased(shared);
            expected = secondRoot;
            var reused = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: null, root: secondRoot);
            yield return Await(() => reused.IsDone, "Panel cache reuse under a different actual root");
            AssertTerminal(reused, LoadViewStatus.Succeeded);
            Assert.That(reused.Panel, Is.SameAs(shared));
            Assert.That(shared.Canvas, Is.SameAs(ownCanvas));
            Assert.That(shared.Root, Is.SameAs(secondRoot));
            Assert.That(probes, Is.EqualTo(2));
            shared.Close();
            AssertPanelReleased(shared);
            Assert.That(PendingViewCount(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator PanelResourceAndGenericFailuresPublishTerminalWithoutResidue()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var root = PanelRoot();
            foreach (string name in new[] { "YLCoreMissingPanel_" + Guid.NewGuid().ToString("N"), nameof(YLCoreSmokeView) })
            {
                var args = NewArgs();
                args.SetObject("retained", _module);
                var request = _module.LoadPanelAsync<YLCoreSmokePanel>(name, loadViewArgs: args, root: root);
                Assert.That(PendingViewCount(), Is.EqualTo(1), "Each Panel consumer owns exactly one pending record.");
                int callbacks = 0;
                request.AddCompleteEvent(done =>
                {
                    callbacks++;
                    AssertTerminal(done, LoadViewStatus.Failed);
                    Assert.That(_module.GetViews<View>(), Is.Empty);
                    Assert.That(PendingViewCount(), Is.Zero);
                    AssertReleasedArgs(args);
                });
                yield return Await(() => request.IsDone, "Panel resource/type failure " + name);
                Assert.That(request.Exception, name == nameof(YLCoreSmokeView)
                    ? Is.TypeOf<InvalidCastException>() : Is.TypeOf<InvalidOperationException>());
                Assert.That(callbacks, Is.EqualTo(1));
                request.Abort();
                AssertTerminal(request, LoadViewStatus.Failed);
            }
            var syncArgs = NewArgs();
            syncArgs.SetObject("retained", _module);
            Assert.Throws<InvalidCastException>(() => _module.LoadPanel<YLCoreIncompatiblePanel>(nameof(YLCoreSmokePanel),
                loadViewArgs: syncArgs, root: root));
            AssertReleasedArgs(syncArgs);
            var asyncArgs = NewArgs();
            asyncArgs.SetObject("retained", _module);
            var mismatch = _module.LoadPanelAsync<YLCoreIncompatiblePanel>(nameof(YLCoreSmokePanel), loadViewArgs: asyncArgs, root: root);
            yield return Await(() => mismatch.IsDone, "Panel generic mismatch");
            AssertTerminal(mismatch, LoadViewStatus.Failed);
            Assert.That(mismatch.Exception, Is.TypeOf<InvalidCastException>());
            AssertReleasedArgs(asyncArgs);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(PendingViewCount(), Is.Zero);
            Assert.That(FixtureInstanceCount(AssetDatabase.LoadAssetAtPath<GameObject>(
                YLCoreSmokeModule.FixtureRoot + "/" + nameof(YLCoreSmokePanel) + ".prefab")), Is.Zero);
        }

        [UnityTest]
        public IEnumerator PanelSortingAndBusinessFailuresRollbackBeforeNotificationAndPreserveRootCause()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot();
            for (int phase = 0; phase < 5; phase++)
            {
                var failure = new InvalidOperationException("YLCore Panel transaction failure " + phase);
                var cleanupFailure = new InvalidOperationException("YLCore Panel rollback failure " + phase);
                var args = NewArgs();
                args.SetObject("retained", _module);
                YLCoreSmokePanel failedPanel = null;
                LoadPanelRequest<YLCoreSmokePanel> request = null;
                void Fail(YLCoreSmokePanel panel)
                {
                    failedPanel = Track(panel);
                    panel.RemoveAction = () =>
                    {
                        request.Abort();
                        throw cleanupFailure;
                    };
                    throw failure;
                }
                YLCoreSmokePanel.SortingProbe = (panel, order) =>
                {
                    if (phase == 0 || (phase == 3 && panel.EffectiveSettings.modal))
                    {
                        Fail(panel);
                    }
                };
                YLCoreSmokePanel.LoadingProbe = panel =>
                {
                    if (phase == 1)
                    {
                        Fail(panel);
                    }

                    if (phase == 3)
                    {
                        panel.UpdateDisplay(new PanelOverrides { Layer = 1, Modal = true });
                    }
                };
                YLCoreSmokePanel.ArgsProbe = phase == 2 ? (Action<YLCoreSmokePanel, LoadViewArgs>)((panel, _) => Fail(panel)) : null;
                YLCoreSmokePanel.UpdateProbe = phase == 4 ? (Action<YLCoreSmokePanel, LoadViewArgs>)((panel, _) => Fail(panel)) : null;
                request = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: args, root: root);
                int callbacks = 0;
                request.AddCompleteEvent(done =>
                {
                    callbacks++;
                    AssertTerminal(done, LoadViewStatus.Failed);
                    Assert.That(done.Exception, Is.SameAs(failure));
                    Assert.That(done.Exception.Data["YLFramework.Core.CleanupException"], Is.SameAs(cleanupFailure));
                    AssertPanelReleased(failedPanel);
                    Assert.That(failedPanel.IsLoaded, Is.False);
                    Assert.That(failedPanel.RemovedCount, Is.EqualTo(1));
                    Assert.That(failedPanel.ClosedCount, Is.Zero);
                    AssertReleasedArgs(args);
                    Assert.That(PendingViewCount(), Is.Zero);
                });
                yield return Await(() => request.IsDone, "Panel transaction rollback " + phase);
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(_module.GetViews<View>(), Is.Empty);
                yield return null;
                Assert.That(failedPanel == null, Is.True, "Failed initialization discards the acquired instance.");
                YLCoreSmokePanel.ResetProbes();
            }
        }

        [UnityTest]
        public IEnumerator SortingSnapshotCannotChangeReusedPanelSessionInAnotherActualRoot()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot();
            var multiple = new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple };
            var first = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root, overrides: multiple));
            var second = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root, overrides: multiple));
            var otherRoot = PanelRoot("YLCore.SortingReentryOtherRoot");
            otherRoot.Canvas.sortingOrder = 1000;
            YLCoreSmokePanel fresh = null;
            bool reentered = false;
            YLCoreSmokePanel.SortingProbe = (panel, _) =>
            {
                if (panel != second || reentered)
                {
                    return;
                }

                reentered = true;
                first.Close();
                fresh = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: otherRoot, overrides: multiple));
                Assert.That(fresh, Is.SameAs(first), "Exercise a new business session on the same healthy cached component.");
            };
            first.BringToFront();
            Assert.That(reentered, Is.True);
            Assert.That(fresh.Root, Is.SameAs(otherRoot));
            Assert.That(fresh.IsLoaded, Is.True);
            Assert.That(fresh.SortingOrder, Is.EqualTo(1001), "The old sorting snapshot cannot write the new root's session.");
            Assert.That(PanelOrders(root).Contains(fresh), Is.False);
            Assert.That(PanelOrders(otherRoot)[fresh], Is.EqualTo(fresh.SortingOrder));
            Assert.That(fresh.Canvas.sortingOrder, Is.EqualTo(fresh.SortingOrder));
            Assert.That(fresh.Module, Is.SameAs(_module));
            YLCoreSmokePanel.ResetProbes();
            fresh.Close();
            second.Close();
            Assert.That(_module.GetViews<Panel>(), Is.Empty);
            Assert.That(PanelOrders(root), Is.Empty);
            Assert.That(PanelOrders(otherRoot), Is.Empty);
        }

        [UnityTest]
        public IEnumerator SortingObserverFailureIsReportedWithoutChangingSession()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot();
            var multiple = new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple };
            var panel = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root, overrides: multiple));
            var second = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root, overrides: multiple));
            var failure = new InvalidOperationException("YLCore existing sorting observer failure");
            YLCoreSmokePanel.SortingProbe = (changed, _) =>
            {
                if (changed == panel)
                {
                    throw failure;
                }
            };
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLCore existing sorting observer failure"));
            Assert.DoesNotThrow(() => panel.BringToFront());
            Assert.That(panel.IsLoaded, Is.True);
            Assert.That(panel.Canvas.sortingOrder, Is.EqualTo(panel.SortingOrder));
            Assert.That(panel.SortingOrder, Is.GreaterThan(second.SortingOrder));
            Assert.That(PanelManager.GetTopPanel(root), Is.SameAs(panel));
            YLCoreSmokePanel.ResetProbes();
            panel.Close();
            second.Close();
            AssertPanelReleased(panel);
        }

        [UnityTest]
        public IEnumerator PanelVisibilityObserverFailureDoesNotRollbackUnrelatedLoad()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot();
            var multiple = new PanelOverrides { InstanceMode = PanelInstanceMode.Multiple, CoversRoot = true };
            var previous = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), root: root, overrides: multiple));
            var failure = new InvalidOperationException("YLCore Panel Invisible preparation failure");
            int restored = 0;
            YLCoreSmokePanel.VisibleProbe = panel =>
            {
                if (panel == previous)
                {
                    restored++;
                }
            };
            YLCoreSmokePanel failedPanel = null;
            YLCoreSmokePanel.SortingProbe = (panel, _) => failedPanel = Track(panel);
            YLCoreSmokePanel.InvisibleProbe = panel =>
            {
                Assert.That(panel, Is.SameAs(previous));
                throw failure;
            };
            var args = NewArgs();
            args.SetObject("retained", _module);
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLCore Panel Invisible preparation failure"));
            var request = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: args, root: root, overrides: multiple);
            yield return Await(() => request.IsDone, "Panel queue preparation rollback");
            AssertTerminal(request, LoadViewStatus.Succeeded);
            Assert.That(request.Exception, Is.Null);
            Assert.That(failedPanel, Is.Not.Null);
            Assert.That(request.Panel, Is.SameAs(failedPanel));
            Assert.That(failedPanel.LoadedCount, Is.EqualTo(1), "Queue activation runs after business initialization, within the final Loaded transaction.");
            Assert.That(failedPanel.ArgsCount, Is.EqualTo(1));
            Assert.That(failedPanel.IsLoaded, Is.True);
            Assert.That(previous.Module, Is.SameAs(_module));
            Assert.That(previous.IsLoaded, Is.True);
            Assert.That(previous.Canvas.enabled, Is.False);
            Assert.That(restored, Is.Zero);
            Assert.That(previous.IsVisible, Is.False);
            Assert.That(previous.IsInputBlocked, Is.True);
            Assert.That(PanelOrders(root).Contains(previous), Is.True, "Covered Panels retain their registered sessions.");
            Assert.That(PanelManager.GetTopPanel(root), Is.SameAs(failedPanel));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EquivalentTo(new[] { previous, failedPanel }));
            AssertReleasedArgs(args);
            Assert.That(PendingViewCount(), Is.Zero);
            YLCoreSmokePanel.ResetProbes();
            YLCoreSmokePanel.VisibleProbe = panel =>
            {
                Assert.That(panel, Is.SameAs(previous));
                restored++;
            };
            failedPanel.Close();
            Assert.That(previous.Canvas.enabled, Is.True);
            Assert.That(restored, Is.EqualTo(1));
            AssertPanelReleased(failedPanel);
            YLCoreSmokePanel.ResetProbes();
            previous.Close();
            AssertPanelReleased(previous);
        }

        [UnityTest]
        public IEnumerator PanelWaitingAbortDuplicateClaimAndLateBorrowedArgsAbortKeepStableOwnership()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot();
            var rejectedArgs = NewArgs();
            rejectedArgs.SetObject("retained", _module);
            Assert.Throws<ArgumentException>(() => _module.LoadPanelAsync<YLCoreSmokePanel>(string.Empty,
                loadViewArgs: rejectedArgs, root: root));
            AssertReleasedArgs(rejectedArgs);
            Assert.That(PendingViewCount(), Is.Zero);

            var args = NewArgs();
            args.SetObject("retained", _module);
            var waiting = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: args, root: root);
            Assert.That(waiting.Status, Is.EqualTo(LoadViewStatus.Pending));
            Assert.Throws<InvalidOperationException>(() => _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                loadViewArgs: args, root: root));
            Assert.Throws<InvalidOperationException>(() => args.GetObject("retained"));
            Assert.That(PendingViewCount(), Is.EqualTo(1));
            int waitingCallbacks = 0;
            waiting.AddCompleteEvent(_ => waitingCallbacks++);
            YLFramework.Async.CustomAsyncOperation<LoadPanelRequest<YLCoreSmokePanel>> foundation = waiting;
            foundation.Abort();
            waiting.Abort();
            AssertTerminal(waiting, LoadViewStatus.Canceled);
            Assert.That(waiting.Exception, Is.TypeOf<OperationCanceledException>());
            Assert.That(waitingCallbacks, Is.EqualTo(1));
            AssertReleasedArgs(args);
            Assert.That(PendingViewCount(), Is.Zero);
            Assert.That(FixtureInstanceCount(prefab), Is.Zero);

            args = NewArgs();
            args.SetInt("token", 61);
            args.SetObject("retained", _module);
            LoadPanelRequest<YLCoreSmokePanel> borrowedRequest = null;
            LoadPanelRequest<YLCoreSmokePanel> next = null;
            YLCoreSmokePanel openedPanel = null;
            bool borrowedAfterAbort = false;
            YLCoreSmokePanel.ArgsProbe = (panel, borrowed) =>
            {
                openedPanel = Track(panel);
                AssertPreparedPanel(panel, root);
                Assert.Throws<InvalidOperationException>(() => _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                    loadViewArgs: borrowed, root: root));
                borrowedRequest.Abort();
                borrowedRequest.Abort();
                borrowedAfterAbort = !borrowedRequest.IsDone && borrowedRequest.Status == LoadViewStatus.Pending &&
                    borrowed.GetInt("token") == 61 && PendingViewCount() == 1;
                YLCoreSmokePanel.ArgsProbe = null;
            };
            borrowedRequest = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: args, root: root);
            int callbacks = 0;
            borrowedRequest.AddCompleteEvent(done =>
            {
                callbacks++;
                AssertTerminal(done, LoadViewStatus.Succeeded);
                Assert.That(done.Panel, Is.SameAs(openedPanel));
                Assert.That(openedPanel.IsLoaded, Is.True, "Abort after the first business callback is past the Panel commit cutoff.");
                Assert.That(openedPanel.RemovedCount, Is.Zero);
                Assert.That(openedPanel.ClosedCount, Is.Zero);
                AssertReleasedArgs(args);
                Assert.That(PendingViewCount(), Is.Zero);
                var nextArgs = NewArgs();
                nextArgs.SetInt("token", 62);
                next = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), loadViewArgs: nextArgs, root: root);
            });
            yield return Await(() => borrowedRequest.IsDone && next != null && next.IsDone, "Panel borrowed Abort and reentrant load");
            Assert.That(borrowedAfterAbort, Is.True);
            Assert.That(callbacks, Is.EqualTo(1));
            AssertTerminal(borrowedRequest, LoadViewStatus.Succeeded);
            AssertTerminal(next, LoadViewStatus.Succeeded);
            var freshPanel = Track(next.Panel);
            Assert.That(freshPanel, Is.SameAs(openedPanel));
            Assert.That(freshPanel.Token, Is.EqualTo(62));
            Assert.That(freshPanel.LoadedCount, Is.EqualTo(1), "A valid Single reopen does not start a new View session.");
            Assert.That(freshPanel.ArgsCount, Is.EqualTo(1));
            Assert.That(freshPanel.OpenCount, Is.EqualTo(2));
            Assert.That(freshPanel.Module, Is.SameAs(_module));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EqualTo(new[] { freshPanel }));
            Assert.That(waitingCallbacks, Is.EqualTo(1));
            Assert.That(PendingViewCount(), Is.Zero);
        }

        [UnityTest]
        public IEnumerator DontDestroyPanelKeepsOwnerAndClosePublishesAfterClosedBeforeRestartedSession()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot("YLCore.DontDestroyPanelRoot");
            Object.DontDestroyOnLoad(root.gameObject);
            int probes = 0;
            YLCoreSmokePanel.LoadingProbe = panel =>
            {
                probes++;
                AssertPreparedPanel(panel, root);
            };
            var oldPanel = Track(_module.LoadPanel<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                loadViewArgs: null, root: root));
            Assert.That(oldPanel.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad"));
            var consumer = new YLCoreConsumerModule();
            try
            {
                var consumerStartup = ModuleManager.StartUpModule(consumer);
                yield return Await(() => consumerStartup.IsDone, "unrelated current Module for DDOL Panel");
                Assert.That(consumerStartup.Error, Is.Null.Or.Empty);
                Assert.That(oldPanel.Module, Is.SameAs(_module));
                Assert.That(consumer.GetViews<View>(), Is.Empty);
                var args = NewArgs();
                args.SetObject("retained", _module);
                var pending = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                    loadViewArgs: args, root: root);
                var operation = Field(pending, "_operation");
                var lifecycle = typeof(Module).GetField("ViewLifecycle", InstancePrivate).GetValue(_module);
                var oldRow = _row;
                bool releasedBeforeExit = false;
                oldPanel.RemoveAction = () =>
                {
                    releasedBeforeExit = _module.State == ModuleState.Closing && !pending.IsDone &&
                        PendingViewCount() == 0 && Field(operation, "Args") == null && Field(operation, "Lease") == null;
                    AssertReleasedArgs(args);
                };
                StartUpModuleRequest restarted = null;
                LoadPanelRequest<YLCoreSmokePanel> fresh = null;
                int callbacks = 0;
                pending.AddCompleteEvent(done =>
                {
                    callbacks++;
                    AssertTerminal(done, LoadViewStatus.Invalidated);
                    Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
                    Assert.That(ModuleManager.GetModule<YLCoreSmokeModule>(), Is.Null);
                    Assert.That(oldRow.Module, Is.Null);
                    Assert.That(oldRow.ExitCount, Is.EqualTo(1));
                    AssertPanelReleased(oldPanel);
                    AssertReleasedArgs(args);
                    Assert.That(PendingViewCount(), Is.Zero);
                    _module.StartAction = () => fresh = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                        loadViewArgs: null, root: root);
                    restarted = ModuleManager.StartUpModule(_module);
                });
                _module.Close();
                oldPanel.RemoveAction = null;
                Assert.That(releasedBeforeExit, Is.True);
                Assert.That(callbacks, Is.EqualTo(1));
                yield return Await(() => restarted != null && restarted.IsDone && fresh != null && fresh.IsDone,
                    "DDOL Panel restarted owner session");
                Assert.That(restarted.Error, Is.Null.Or.Empty);
                Assert.That(typeof(Module).GetField("ViewLifecycle", InstancePrivate).GetValue(_module), Is.Not.SameAs(lifecycle));
                AssertTerminal(pending, LoadViewStatus.Invalidated);
                AssertTerminal(fresh, LoadViewStatus.Succeeded);
                var panel = Track(fresh.Panel);
                Assert.That(panel, Is.SameAs(oldPanel));
                Assert.That(panel.Module, Is.SameAs(_module));
                Assert.That(panel.Root, Is.SameAs(root));
                Assert.That(panel.GetComponents<Canvas>(), Has.Length.EqualTo(1));
                Assert.That(PanelOrders(root)[panel], Is.EqualTo(panel.SortingOrder));
                Assert.That(probes, Is.EqualTo(2));
                Assert.That(PendingViewCount(), Is.Zero);
                Assert.That(consumer.GetViews<View>(), Is.Empty);
                yield return null;
                Assert.That(FixtureInstanceCount(prefab), Is.EqualTo(1), "Old producer must not acquire an instance in the new session.");
            }
            finally
            {
                oldPanel.RemoveAction = null;
                consumer.Close();
                _classPool.Restore(new Module[] { consumer });
            }
        }

        [UnityTest]
        public IEnumerator PanelDestroyedRootAndThrowingCompletionKeepTerminalAndReleasedInputs()
        {
            yield return StartModule();
            var prefab = LoadFixture<GameObject>(nameof(YLCoreSmokePanel));
            var root = PanelRoot("YLCore.PanelDestroyedRoot");
            var args = NewArgs();
            args.SetObject("retained", root);
            var invalidated = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                loadViewArgs: args, root: root);
            Object.DestroyImmediate(root.gameObject);
            yield return Await(() => invalidated.IsDone, "Panel destroyed pending root");
            AssertTerminal(invalidated, LoadViewStatus.Invalidated);
            AssertReleasedArgs(args);
            Assert.That(_module.GetViews<View>(), Is.Empty);
            Assert.That(PendingViewCount(), Is.Zero);
            Assert.That(FixtureInstanceCount(prefab), Is.Zero);

            args = NewArgs();
            args.SetInt("token", 95);
            var request = _module.LoadPanelAsync<YLCoreSmokePanel>(nameof(YLCoreSmokePanel),
                loadViewArgs: args, root: PanelRoot());
            int callbacks = 0;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                AssertTerminal(done, LoadViewStatus.Succeeded);
                Assert.That(PendingViewCount(), Is.Zero);
                AssertReleasedArgs(args);
                done.Abort();
                throw new InvalidOperationException("YLCore Panel completion subscriber failure");
            });
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLCore Panel completion subscriber failure"));
            yield return Await(() => request.IsDone, "Panel throwing completion subscriber");
            AssertTerminal(request, LoadViewStatus.Succeeded);
            var panel = Track(request.Panel);
            AssertPanel(panel, 95, 1);
            request.Abort();
            int late = 0;
            request.AddCompleteEvent(_ => late++);
            yield return null;
            AssertTerminal(request, LoadViewStatus.Succeeded);
            Assert.That(request.Panel, Is.SameAs(panel));
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(late, Is.EqualTo(1));
            Assert.That(panel.IsLoaded, Is.True);
            Assert.That(PendingViewCount(), Is.Zero);
            panel.Close();
            AssertPanelReleased(panel);
        }

        [UnityTest]
        public IEnumerator ResourceAdditiveSceneRunsIndependentEntryWithoutReplacingActiveScene()
        {
            yield return StartModule();
            Scene active = SceneManager.GetActiveScene();
            _ownsScene = true;
            using var request = _provider.LoadSceneAsync(YLCoreSmokeModule.Project,
                YLCoreSmokeModule.SceneName, LoadSceneMode.Additive);
            int callbacks = 0;
            request.Completed += _ => callbacks++;
            yield return Await(() => request.Status != CoreResourceStatus.Pending, "Resource additive scene");
            Assert.That(request.Error, Is.Null);
            Assert.That(request.Progress, Is.EqualTo(1f));
            Assert.That(request.keepWaiting, Is.False);
            Assert.That(callbacks, Is.EqualTo(1));
            var result = request.TakeResult();
            Assert.That(result.Scene.IsValid() && result.Scene.isLoaded, Is.True);
            Assert.That(result.Scene.path, Is.EqualTo(YLCoreSmokeModule.ScenePath));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(active));
            yield return AssertSceneEntry(result.Scene);
        }

        [UnityTest]
        public IEnumerator SceneControllerSingleUsesTheProviderAndDoesNotOwnTheLoadedScene()
        {
            yield return PrepareDefaultSceneTest();
            yield return StartModule();
            var controller = _module.LoadController<YLFramework.Core.SceneController>();
            using var request = controller.LoadSceneAsync(YLCoreSmokeModule.SceneName, LoadSceneMode.Single);
            yield return Await(() => !request.keepWaiting, "SceneController Single scene");
            Assert.That(request.Status, Is.EqualTo(CoreResourceStatus.Succeeded), request.Error?.ToString());
            Assert.That(request.Progress, Is.EqualTo(1));
            var scene = request.TakeResult().Scene;
            Assert.That(scene.path, Is.EqualTo(YLCoreSmokeModule.ScenePath));
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene));
            var entry = scene.GetRootGameObjects().Single().GetComponent<YLCoreSmokeSceneEntry>();
            Assert.That(entry.ModuleAtAwake, Is.SameAs(_module));
            Assert.That(entry.StateAtAwake, Is.EqualTo(ModuleState.Running));
            yield return Await(() => entry.StartCount == 1, "SceneController scene entry Start");
            Assert.That(entry.ModuleAtStart, Is.SameAs(_module));
            _module.Close();
            Assert.That(scene.isLoaded, Is.True, "Closing the Controller is not scene unload authority.");
            Assert.That(request.IsDisposed, Is.False);
        }

        [UnityTest]
        public IEnumerator IncompatibleExistingPrefabCompletesViewFailureCallbackWithoutRegisteringView()
        {
            yield return StartModule();
            LoadFixture<GameObject>(nameof(YLCoreSmokeView));
            var parent = Own(new GameObject("YLCoreSmoke.FailureParent")).transform;
            var args = NewArgs();
            args.SetObject("retained", _module);
            var request = _module.LoadViewAsync<YLCoreSmokePanel>(nameof(YLCoreSmokeView), parent, loadViewArgs: args);
            int callbacks = 0;
            string callbackError = null;
            request.AddCompleteEvent(done =>
            {
                callbacks++;
                callbackError = done.Error;
            });
            yield return Await(() => request.IsDone, "incompatible View request");
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(callbackError, Is.Not.Null.And.Not.Empty);
            StringAssert.Contains("View", callbackError);
            Assert.That(request.Error, Is.EqualTo(callbackError));
            Assert.That(request.Progress, Is.EqualTo(1f));
            Assert.That(request.keepWaiting, Is.False);
            Assert.That(request.View, Is.Null);
            AssertTerminal(request, LoadViewStatus.Failed);
            Assert.That(request.Exception, Is.TypeOf<InvalidCastException>());
            AssertReleasedArgs(args);
            Assert.That(PendingViewCount(), Is.Zero);
            Assert.That(_module.GetViews(nameof(YLCoreSmokeView)), Is.Empty);
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.Empty);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RepeatedAdditiveSceneAssetAttachesByHandleAndUnloadPreservesOtherInstance()
        {
            yield return StartModule();
            void Capture(Scene scene, LoadSceneMode mode)
            {
                if (scene.path == YLCoreSmokeModule.ScenePath)
                {
                    _ownedSceneInstances.Add(scene);
                }
            }
            SceneManager.sceneLoaded += Capture;
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    yield return AwaitUnity(EditorSceneManager.LoadSceneAsyncInPlayMode(YLCoreSmokeModule.ScenePath,
                        new LoadSceneParameters(LoadSceneMode.Additive)), "load repeated additive scene");
                }
            }
            finally
            {
                SceneManager.sceneLoaded -= Capture;
            }
            Assert.That(_ownedSceneInstances.Count, Is.EqualTo(2));
            var first = _ownedSceneInstances[0];
            var second = _ownedSceneInstances[1];
            Assert.That(first.name, Is.EqualTo(second.name));
            Assert.That(first.path, Is.EqualTo(second.path));
            Assert.That(first.handle, Is.Not.EqualTo(second.handle));
            var firstObject = Own(new GameObject("YLCore.FirstAdditiveView"));
            var secondObject = Own(new GameObject("YLCore.SecondAdditiveView"));
            firstObject.SetActive(false);
            secondObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(firstObject, first);
            SceneManager.MoveGameObjectToScene(secondObject, second);
            var firstView = firstObject.AddComponent<YLCoreSmokeView>();
            var secondView = secondObject.AddComponent<YLCoreSmokeView>();
            _module.AttachViews(first);
            Assert.That(secondView.Module, Is.Null);
            _module.AttachViews(second);
            yield return AwaitUnity(SceneManager.UnloadSceneAsync(first), "unload first additive instance");
            Assert.That(firstView == null, Is.True);
            Assert.That(firstView.Module, Is.Null);
            Assert.That(firstView.RemovedCount, Is.EqualTo(1));
            Assert.That(secondView.Module, Is.SameAs(_module));
            Assert.That(secondView.IsLoaded, Is.True);
            Assert.That(secondView.RemovedCount, Is.Zero);
            Assert.That(_module.GetViews<View>(), Is.EqualTo(new View[] { secondView }));
            Assert.That(second.isLoaded, Is.True);
        }

        [UnityTest]
        public IEnumerator DefaultStartupSceneUsesSingleAndRealFixtureEntry()
        {
            yield return PrepareDefaultSceneTest();
            YLCoreSmokeView sceneView = null;
            void InjectView(Scene scene, LoadSceneMode mode)
            {
                if (scene.path != YLCoreSmokeModule.ScenePath)
                {
                    return;
                }

                var obj = Own(new GameObject("YLCore.DefaultSceneView"));
                obj.SetActive(false);
                SceneManager.MoveGameObjectToScene(obj, scene);
                obj.transform.SetParent(scene.GetRootGameObjects()[0].transform);
                sceneView = obj.AddComponent<YLCoreSmokeView>();
            }
            SceneManager.sceneLoaded += InjectView;
            try
            {
                yield return StartModule(true, () =>
                {
                    Assert.That(sceneView.IsLoaded, Is.True, "Default scene attachment commits before Module.OnStart.");
                    Assert.That(sceneView.Module, Is.SameAs(_module));
                    Assert.That(_module.GetView<YLCoreSmokeView>(sceneView.Id), Is.SameAs(sceneView));
                    Assert.That(sceneView.AwakeCount, Is.Zero);
                });
            }
            finally
            {
                SceneManager.sceneLoaded -= InjectView;
            }
            var loaded = SceneManager.GetSceneByPath(YLCoreSmokeModule.ScenePath);
            Assert.That(loaded.IsValid() && loaded.isLoaded, Is.True);
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(loaded));
            Assert.That(SceneManager.GetSceneByPath(YLCoreSmokeModule.HarnessPath).isLoaded, Is.False);
            Assert.That(_module.SceneReadyAtStart, Is.True, "Startup validates the loaded scene before OnStart.");
            Assert.That(_module.RowReadyAtStart, Is.True);
            yield return AssertSceneEntry(loaded);
            _module.Close();
            Assert.That(sceneView.Module, Is.Null);
            Assert.That(sceneView.RemovedCount, Is.EqualTo(1));
            Assert.That(sceneView, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator DefaultSceneAttachFailureRollsBackModuleViewsAndModelsBeforeCompletion()
        {
            yield return PrepareDefaultSceneTest();
            YLCoreSmokeView first = null;
            YLCoreSmokeView failing = null;
            YLCoreSmokeRow initializedRow = null;
            var failure = new InvalidOperationException("default scene attach failure");
            YLCoreSmokeRow.InitAction = value => initializedRow = value;
            void InjectViews(Scene scene, LoadSceneMode mode)
            {
                if (scene.path != YLCoreSmokeModule.ScenePath)
                {
                    return;
                }

                var root = Own(new GameObject("YLCore.DefaultFailureRoot"));
                root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, scene);
                first = root.AddComponent<YLCoreSmokeView>();
                var child = Own(new GameObject("YLCore.DefaultFailureChild"));
                child.SetActive(false);
                child.transform.SetParent(root.transform);
                failing = child.AddComponent<YLCoreSmokeView>();
                failing.LoadedAction = () => throw failure;
            }
            SceneManager.sceneLoaded += InjectViews;
            try
            {
                _module = new YLCoreSmokeModule { StartWithScene = true };
                var request = YLFramework.Core.ModuleManager.StartUpModule(_module);
                yield return Await(() => request.IsDone, "default scene attachment failure");
                Assert.That(request.Exception, Is.SameAs(failure));
                Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
                Assert.That(_module.State, Is.EqualTo(ModuleState.Closed));
                Assert.That(_module.StartCount, Is.Zero);
                Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.Null);
                Assert.That(_module.GetViews<View>(), Is.Empty);
                Assert.That(first.Module, Is.Null);
                Assert.That(failing.Module, Is.Null);
                Assert.That(first.RemovedCount, Is.EqualTo(1));
                Assert.That(failing.RemovedCount, Is.EqualTo(1));
                Assert.That(first.ClosedCount, Is.Zero);
                Assert.That(first && failing, Is.True, "Startup owns business sessions, not the scene objects.");
                Assert.That(initializedRow.ExitCount, Is.EqualTo(1));
                Assert.That(initializedRow.Module, Is.Null);
                Assert.That(SceneManager.GetSceneByPath(YLCoreSmokeModule.ScenePath).isLoaded, Is.True,
                    "Logical startup rollback does not restore or unload the scene.");
            }
            finally
            {
                SceneManager.sceneLoaded -= InjectViews;
            }
        }

        private IEnumerator PrepareDefaultSceneTest()
        {
            Assert.That(YLFramework.Core.ModuleManager.AllModules, Is.Empty);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                Assert.That(scene.isDirty, Is.False);
                if (scene.path == YLCoreSmokeModule.HarnessPath)
                {
                    Assert.That(scene.GetRootGameObjects(), Is.Empty);
                }
                else
                {
                    // Unity Test Runner's own temporary scene is allowed, but not user objects.
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
                        Assert.That(behaviours, Is.Not.Empty, "Unexpected non-runner root: " + root.name);
                        foreach (var behaviour in behaviours)
                        {
                            Assert.That(behaviour && behaviour.GetType().Assembly.GetName().Name == "UnityEngine.TestRunner",
                                Is.True, "Single would unload a non-runner object: " + root.name);
                        }
                    }
                    Assert.That(string.IsNullOrEmpty(scene.path) || scene.name.StartsWith("InitTestScene", StringComparison.Ordinal),
                        Is.True, "Single would unload a non-fixture scene: " + scene.path);
                }
            }
            if (_previousActive.path != YLCoreSmokeModule.HarnessPath)
            {
                yield return AwaitUnity(EditorSceneManager.LoadSceneAsyncInPlayMode(YLCoreSmokeModule.HarnessPath,
                    new LoadSceneParameters(LoadSceneMode.Additive)), "load owned empty harness");
                _previousActive = SceneManager.GetSceneByPath(YLCoreSmokeModule.HarnessPath);
                Assert.That(_previousActive.GetRootGameObjects(), Is.Empty);
                SceneManager.SetActiveScene(_previousActive);
            }
            _isSingleSceneTest = _ownsScene = true;
        }

        private IEnumerator StartModule(bool withScene = false, Action onStart = null)
        {
            _module = new YLCoreSmokeModule { StartWithScene = withScene, StartAction = onStart };
            var request = YLFramework.Core.ModuleManager.StartUpModule(_module);
            Assert.That(request, Is.Not.Null);
            yield return Await(() => request.IsDone, "Core startup");
            Assert.That(request.Error, Is.Null.Or.Empty);
            Assert.That(request.Module, Is.SameAs(_module));
            Assert.That(request.Progress, Is.EqualTo(1f));
            Assert.That(request.keepWaiting, Is.False);
            Assert.That(YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>(), Is.SameAs(_module));
            _row = _module.GetModel<YLCoreSmokeRow>(101);
            Assert.That(_row, Is.Not.Null, "Assets requests can complete without an asset; startup success alone is insufficient.");
            yield return null;
        }

        private T LoadFixture<T>(string name) where T : Object
        {
            var lease = _resources.Acquire<T>(new ResourceAddress(YLCoreSmokeModule.Project, name));
            _assetLeases.Add(lease);
            var asset = lease.Asset;
            Assert.That(asset, Is.Not.Null, name);
            string path = AssetDatabase.GetAssetPath(asset);
            Assert.That(path.StartsWith(YLCoreSmokeModule.FixtureRoot + "/", StringComparison.Ordinal), Is.True);
            OwnAsset(asset);
            return asset;
        }

        private UIRootContext PanelRoot(string name = "YLCoreSmoke.PanelRoot")
        {
            var host = Own(new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.GraphicRaycaster)));
            host.SetActive(false);
            host.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var root = CorePanelTestRoots.Create(host);
            _ownedRoots.Add(root);
            return root;
        }

        private void AssertView(YLCoreSmokeView view, Transform parent, int token, int loads)
        {
            Assert.That(view, Is.Not.Null);
            Assert.That(view.Module, Is.SameAs(_module));
            Assert.That(view.transform.parent, Is.SameAs(parent));
            Assert.That(view.Token, Is.EqualTo(token));
            Assert.That(view.LoadedCount, Is.EqualTo(loads));
            Assert.That(view.ArgsCount, Is.EqualTo(loads));
            Assert.That(_module.GetView<YLCoreSmokeView>(nameof(YLCoreSmokeView), view.Id), Is.SameAs(view));
            Assert.That(_module.GetViews<YLCoreSmokeView>(), Is.EqualTo(new[] { view }));
        }

        private void AssertPanel(YLCoreSmokePanel panel, int token, int loads)
        {
            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.Module, Is.SameAs(_module));
            Assert.That(panel.Token, Is.EqualTo(token));
            Assert.That(panel.LoadedCount, Is.EqualTo(loads));
            Assert.That(panel.ArgsCount, Is.EqualTo(loads));
            Assert.That(_ownedRoots, Has.Member(panel.Root));
            Assert.That(panel.transform.parent, Is.SameAs(panel.Root.GetLayer(panel.EffectiveSettings.layer)));
            Assert.That(panel.Canvas, Is.SameAs(panel.GetComponent<Canvas>()));
            Assert.That(panel.Canvas.overrideSorting, Is.True);
            Assert.That(panel.GetComponent<UnityEngine.UI.GraphicRaycaster>(), Is.Not.Null);
            Assert.That(panel.Canvas.sortingOrder, Is.EqualTo(panel.SortingOrder));
            Assert.That(_module.GetView<YLCoreSmokePanel>(nameof(YLCoreSmokePanel), panel.Id), Is.SameAs(panel));
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Is.EqualTo(new[] { panel }));
            var orders = PanelOrders(panel.Root);
            Assert.That(orders.Contains(panel), Is.True);
            Assert.That(orders[panel], Is.EqualTo(panel.SortingOrder));
        }

        private static IDictionary PanelOrders(UIRootContext root)
        {
            var panels = new List<Panel>();
            PanelManager.GetAllPanels(root, panels);
            var orders = new Dictionary<Panel, int>();
            foreach (var panel in panels)
            {
                orders.Add(panel, panel.SortingOrder);
            }

            return orders;
        }

        private void AssertPreparedPanel(YLCoreSmokePanel panel, UIRootContext root)
        {
            Track(panel);
            Assert.That(panel.Module, Is.SameAs(_module));
            Assert.That(panel.IsLoaded, Is.False, "Business callbacks run before the View transaction commits.");
            Assert.That(panel.Root, Is.SameAs(root));
            Assert.That(panel.Canvas, Is.Not.Null);
            Assert.That(panel.Canvas, Is.SameAs(panel.GetComponent<Canvas>()));
            if (panel.gameObject.activeInHierarchy)
            {
                Assert.That(panel.Canvas.overrideSorting, Is.True);
            }

            Assert.That(panel.GetComponent<UnityEngine.UI.GraphicRaycaster>(), Is.Not.Null);
            Assert.That(panel.Canvas.sortingOrder, Is.EqualTo(panel.SortingOrder));
            Assert.That(_module.GetView<YLCoreSmokePanel>(panel.Id), Is.SameAs(panel));
            Assert.That(((IDictionary)Field(_module, "OwnedViews"))[panel.Id], Is.SameAs(panel));
            Assert.That(PanelOrders(root)[panel], Is.EqualTo(panel.SortingOrder));
            Assert.That(panel.IsVisible, Is.False, "Business initialization must not expose a partially opened Panel.");
        }

        private void AssertPanelReleased(YLCoreSmokePanel panel)
        {
            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.Module, Is.Null);
            Assert.That(_module.GetView<YLCoreSmokePanel>(panel.Id), Is.Null);
            Assert.That(((IDictionary)Field(_module, "OwnedViews")).Contains(panel.Id), Is.False);
            Assert.That(_module.GetViews<YLCoreSmokePanel>(), Has.No.Member(panel));
            Assert.That(_module.GetViews(nameof(YLCoreSmokePanel)), Has.No.Member(panel));
            foreach (var root in _ownedRoots)
            {
                Assert.That(PanelOrders(root).Contains(panel), Is.False);
            }

            Assert.That(panel.Root, Is.Null);
            var manager = Singleton(typeof(PanelManager));
            if (manager != null)
            {
                Assert.That(((IDictionary)Field(manager, "_states")).Contains(panel), Is.False,
                    "退出不仅排除查询，还必须释放旧会话策略记录。");
            }
        }

        private void AssertRemoved(YLFramework.Core.View view, string name)
        {
            Assert.That(view.gameObject.activeSelf, Is.False);
            Assert.That(_module.GetView(name, view.Id), Is.Null);
            Assert.That(_module.GetViews(name), Is.Empty);
            var smokeView = view as YLCoreSmokeView;
            var smokePanel = view as YLCoreSmokePanel;
            Assert.That(smokeView ? smokeView.RemovedCount : smokePanel.RemovedCount, Is.EqualTo(1));
        }

        private IEnumerator AssertSceneEntry(Scene scene)
        {
            var roots = scene.GetRootGameObjects();
            Assert.That(roots, Has.Length.EqualTo(1));
            var entry = roots[0].GetComponent<YLCoreSmokeSceneEntry>();
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry.ModuleAtAwake, Is.SameAs(_module));
            Assert.That(entry.StateAtAwake, Is.EqualTo(_isSingleSceneTest ? ModuleState.Starting : ModuleState.Running));
            yield return Await(() => entry && entry.StartCount == 1, "fixture scene entry Start");
            Assert.That(entry.ModuleAtStart, Is.SameAs(_module));
        }

        private void CleanPanelManager()
        {
            var manager = Singleton(typeof(YLFramework.Core.PanelManager));
            if (manager == null)
            {
                return;
            }

            foreach (var root in _ownedRoots)
            {
                if (root)
                {
                    typeof(PanelManager).GetMethod("UnregisterRoot", StaticPrivate).Invoke(null, new object[] { root });
                }
            }

            var system = (GameObject)Field(manager, "_eventSystem");
            if (system && !_previousEventSystems.Contains(system.GetInstanceID()))
            {
                Own(system);
                manager.GetType().GetField("_eventSystem", InstancePrivate).SetValue(manager, _previousManagerEventSystem);
            }
            if (_previousPanelManager != null)
            {
                return;
            }

            if (((IDictionary)Field(manager, "_states")).Count != 0 ||
                (int)Field(manager, "_registeredRoots").GetType().GetProperty("Count").GetValue(Field(manager, "_registeredRoots")) != 0)
            {
                return;
            }

            var type = typeof(YLFramework.Core.PanelManager);
            type.GetMethod("SyncLateSubscription", InstancePrivate).Invoke(manager, null);
            type.GetField("_instance", StaticPrivate).SetValue(null, null);
        }

        private YLFramework.Core.LoadViewArgs NewArgs()
        {
            var args = YLFramework.Core.LoadViewArgs.New();
            _classPool.Track(args);
            return args;
        }

        private int PendingViewCount()
        {
            var pending = typeof(Module).GetField("PendingViews", InstancePrivate).GetValue(_module);
            return (int)pending.GetType().GetProperty("Count").GetValue(pending);
        }

        private static void AssertTerminal<T>(LoadViewRequest<T> request, LoadViewStatus status) where T : View
        {
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.keepWaiting, Is.False);
            Assert.That(request.Progress, Is.EqualTo(1f));
            Assert.That(request.Status, Is.EqualTo(status));
            if (status == LoadViewStatus.Succeeded)
            {
                Assert.That(request.View, Is.Not.Null);
                Assert.That(request.Exception, Is.Null);
                Assert.That(request.Error, Is.Null.Or.Empty);
            }
            else
            {
                Assert.That(request.View, Is.Null);
                Assert.That(request.Exception, Is.Not.Null);
                Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
            }
            foreach (var name in new[] { "_owner", "_lifecycle", "_assetName", "_projects", "_parent", "_args", "_param" })
            {
                Assert.That(Field(request, name), Is.Null, "Terminal request retains " + name);
            }

            Assert.That((IList)Field(request, "_parentSessions"), Is.Empty);
            Assert.That(((Scene)Field(request, "_targetScene")).IsValid(), Is.False);
        }

        private static void AssertReleasedArgs(LoadViewArgs args)
        {
            Assert.Throws<ObjectDisposedException>(() => args.GetObject("retained"));
            Assert.That((IDictionary)Field(args, "_objects"), Is.Empty);
            Assert.That((IDictionary)Field(args, "_ints"), Is.Empty);
        }

        private static void AssertTerminal<T>(LoadPanelRequest<T> request, LoadViewStatus status) where T : Panel
        {
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.keepWaiting, Is.False);
            Assert.That(request.Progress, Is.EqualTo(1f));
            Assert.That(request.Status, Is.EqualTo(status));
            Assert.That(Field(request, "_operation"), Is.Null, "Terminal Panel request must release its owned operation.");
            if (status == LoadViewStatus.Succeeded)
            {
                Assert.That(request.Panel, Is.Not.Null);
                Assert.That(request.Exception, Is.Null);
                Assert.That(request.Error, Is.Null.Or.Empty);
            }
            else
            {
                Assert.That(request.Panel, Is.Null);
                Assert.That(request.Exception, Is.Not.Null);
                Assert.That(request.Error, Is.Not.Null.And.Not.Empty);
            }
        }

        private static int FixtureInstanceCount(GameObject prefab)
        {
            return Resources.FindObjectsOfTypeAll<View>().Count(view => view && view.gameObject.scene.IsValid() &&
                view.gameObject.name == prefab.name + "(Clone)");
        }

        private IEnumerator DrainFixtureAssetProducer()
        {
            // 等待同一真实后端的新请求结束，再留一帧让之前的Core producer退出；不使用假后端或改写注册。
            using var backend = _resources.AcquireAsync<GameObject>(new ResourceAddress(YLCoreSmokeModule.Project, nameof(YLCoreSmokeView)));
            yield return Await(() => backend.IsDone, "drain real fixture asset producer");
            Assert.That(backend.Error, Is.Null);
            using var lease = backend.TakeResult();
            Assert.That(lease.Asset, Is.Not.Null);
            OwnAsset(lease.Asset);
            yield return null;
        }

        private T Track<T>(T view) where T : YLFramework.Core.View
        {
            if (view)
            {
                Own(view.gameObject);
            }

            return view;
        }

        private GameObject Own(GameObject obj)
        {
            if (obj && !_ownedObjects.Contains(obj))
            {
                _ownedObjects.Add(obj);
            }

            return obj;
        }

        private void OwnAsset(Object asset)
        {
            if (asset && !_ownedAssets.Contains(asset))
            {
                _ownedAssets.Add(asset);
            }
        }

        private IEnumerator Await(Func<bool> done, string label)
        {
            float deadline = Time.realtimeSinceStartup + Timeout;
            while (!done() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (done())
            {
                yield break;
            }
            // Abort only marks these operations complete. Stop the entire test session instead of
            // allowing a still-running producer to mutate the next test after ownership cleanup.
            _hasTimedOut = true;
            EditorApplication.isPlaying = false;
            Assert.Fail(label + " exceeded the ten-second realtime timeout; isolated PlayMode is stopping.");
        }

        private IEnumerator AwaitUnity(AsyncOperation operation, string label)
        {
            Assert.That(operation, Is.Not.Null, label);
            yield return Await(() => operation.isDone, label);
        }

        private static object Singleton(Type type) => type.GetField("_instance", StaticPrivate).GetValue(null);
        private static object Field(object target, string name) => target.GetType().GetField(name, InstancePrivate).GetValue(target);

        private sealed class YLCoreIncompatiblePanel : Panel
        {
        }

    }
}
#endif
