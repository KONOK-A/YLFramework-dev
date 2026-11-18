using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
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
    // Both real cross-Play modes use only the owned empty scene and restore the exact original Editor options.
    [Category("YL.Module.Resource")]
    [Category("YL.Special.ResourceSession")]
    public class ResourceSessionTests
    {
        private const string Key = "YL.Resource.8.1.Session.";
        internal const string MessageKey = "ownership/message";
        internal const string MessageText = "official addressables content";
        private static readonly string DomainLifetime = Guid.NewGuid().ToString("N");
        [Serializable]
        private sealed class Scenes
        {
            public SceneRecord[] Items;
        }
        [Serializable]
        private sealed class SceneRecord
        {
            public string Path;
            public bool Loaded;
            public bool Active;
        }
        private static ResourceSystem _system;
        private static ResourceStartup _startup;
        private static AssetLease<TextAsset> _lease;
        private static ResourceRequest<AssetLease<TextAsset>> _unclaimed;
        private static TextAsset _external;
        private static AsyncOperationHandle<TextAsset> _externalHandle;
        private static ExitProvider _exitProvider;
        private static ResourceLocationMap _exitLocator;
        private static TextAsset _lateText;
        private static ResourceRequestStatus[] _exitNotifications;
        private static ResourceRequest<bool> _stoppingAtExit;
        private static int _stopNotifications;

        [SetUp]
        public void SetUp()
        {
            if (SessionState.GetBool(Key + "configured", false))
            {
                return;
            }

            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.False, "Begin from normal reload; the dedicated no-reload case restores its temporary options.");
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
            EnsureRestorableScenes();
            var snapshot = new Scenes
            {
                Items = EditorSceneManager.GetSceneManagerSetup().Where(scene => !string.IsNullOrEmpty(scene.path))
                    .Select(scene => new SceneRecord { Path = scene.path, Loaded = scene.isLoaded, Active = scene.isActive }).ToArray()
            };
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("Temp")), Is.True);
            var path = "Temp/YL.Resource81.Session." + Guid.NewGuid().ToString("N") + ".unity";
            SessionState.SetString(Key + "scenes", JsonUtility.ToJson(snapshot));
            SessionState.SetString(Key + "path", path);
            SessionState.SetBool(Key + "configured", true);
            SessionState.SetBool(Key + "quit", false);
            SessionState.SetBool(Key + "exit", false);
            SessionState.SetBool(Key + "optionsEnabled", EditorSettings.enterPlayModeOptionsEnabled);
            SessionState.SetInt(Key + "options", (int)EditorSettings.enterPlayModeOptions);
            SessionState.SetString(Key + "probeError", "");
            var empty = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(empty, path), Is.True);
        }

        internal static void EnsureRestorableScenes()
        {
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                Assert.That(scene.isDirty, Is.False, "Do not close dirty scenes: " + scene.name);
                if (string.IsNullOrEmpty(scene.path))
                {
                    Assert.That(scene.rootCount, Is.Zero, "Only discard a clean empty runner scene.");
                }
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!SessionState.GetBool(Key + "configured", false))
            {
                yield break;
            }

            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            try
            {
                Application.quitting -= OnQuit;
                EditorApplication.playModeStateChanged -= OnExit;
                _lease?.Dispose();
                _unclaimed?.Dispose();
                _stoppingAtExit?.Dispose();
                _stoppingAtExit = null;
                if (_externalHandle.IsValid())
                    Addressables.Release(_externalHandle);
                _externalHandle = default;
                ClearExitProvider();
                EnsureRestorableScenes();
                var snapshot = JsonUtility.FromJson<Scenes>(SessionState.GetString(Key + "scenes", ""));
                if (snapshot.Items.Length != 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(snapshot.Items.Select(scene => new SceneSetup
                    {
                        path = scene.Path,
                        isLoaded = scene.Loaded,
                        isActive = scene.Active
                    }).ToArray());
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }

                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "path", "")));
            }
            finally
            {
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(Key + "options", 0);
                EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(Key + "optionsEnabled", false);
                SessionState.EraseString(Key + "scenes");
                SessionState.EraseString(Key + "path");
                SessionState.EraseString(Key + "domain");
                SessionState.EraseString(Key + "probeError");
                SessionState.EraseBool(Key + "configured");
                SessionState.EraseBool(Key + "quit");
                SessionState.EraseBool(Key + "exit");
                SessionState.EraseBool(Key + "optionsEnabled");
                SessionState.EraseInt(Key + "options");
            }
        }

        private static void OnExit(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            try
            {
                using var denied = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                using var rejected = denied.StartAsync();
                SessionState.SetBool(Key + "exit", rejected.Status == ResourceRequestStatus.Failed &&
                    rejected.Error?.Kind == ResourceError.SessionUnavailable && denied.Resources == null &&
                    ResourceSystem.Current == null && _lease != null && !_lease.IsValid);
            }
            catch (Exception error)
            {
                SessionState.SetString(Key + "probeError", error.ToString());
                SessionState.SetBool(Key + "exit", false);
            }
            finally { EditorApplication.playModeStateChanged -= OnExit; }
        }

        private static void OnQuit()
        {
            try
            {
                var report = _system.GetDiagnostics();
                bool rejected = false;
                try
                {
                    _unclaimed.TakeResult();
                }
                catch (ObjectDisposedException) { rejected = true; }
                SessionState.SetBool(Key + "quit", _system.State == ResourceSystemState.Invalidated &&
                    report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 &&
                    _unclaimed.IsDisposed && _unclaimed.Status == ResourceRequestStatus.Succeeded && rejected && !_lease.IsValid &&
                    _externalHandle.IsValid() && _externalHandle.Status == AsyncOperationStatus.Succeeded &&
                    _external && _externalHandle.Result == _external);
            }
            catch (Exception error)
            {
                SessionState.SetString(Key + "probeError", error.ToString());
                SessionState.SetBool(Key + "quit", false);
            }
            finally
            {
                _lease?.Dispose();
                _unclaimed?.Dispose();
                if (_externalHandle.IsValid())
                    Addressables.Release(_externalHandle);
                _externalHandle = default;
                Application.quitting -= OnQuit;
            }
        }

        [UnityTest]
        public IEnumerator RealPlayExitInvalidatesOwnershipRejectsReentryAndNextPlayCanInitialize()
            => RunTwoPlaySessions(false);

        [UnityTest]
        public IEnumerator DisabledDomainReloadInvalidatesOldOwnershipAndKeepsTheNextStartupIndependent()
            => RunTwoPlaySessions(true);

        [UnityTest]
        public IEnumerator ActualExitFreezesReentrantWaitersAndDrainsAnOfficialLateResultBeforeRestart()
        {
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            SessionState.SetString(Key + "domain", DomainLifetime);
            yield return new EnterPlayMode(false);
            yield return BeginExitDrain();
            yield return new ExitPlayMode();
            yield return DrainExitedSession();
            yield return new EnterPlayMode(false);
            yield return LoadInNewSession();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ExitInvalidatesAWaitingStopObserverBeforeLateCleanupCompletes()
        {
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            SessionState.SetString(Key + "domain", DomainLifetime);
            yield return new EnterPlayMode(false);
            yield return BeginExitDrain();
            _lease.Dispose();
            _stoppingAtExit = _startup.StopAsync();
            _stopNotifications = 0;
            _stoppingAtExit.Completed += _ => _stopNotifications++;
            Assert.That(_stoppingAtExit.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            yield return new ExitPlayMode();

            AssertDomainSurvives();
            Assert.That(_system.State, Is.EqualTo(ResourceSystemState.Invalidated));
            Assert.That(_stoppingAtExit.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(_stoppingAtExit.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(_stopNotifications, Is.EqualTo(1));
            Assert.That(_exitProvider.Releases, Is.Zero, "Observer invalidation is not physical cleanup.");
            using (var refused = _startup.StopAsync())
            {
                Assert.That(refused.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(refused.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            }
            _lateText = new TextAsset("late stop result");
            _exitProvider.Complete(_lateText);
            yield return Wait(() => _exitProvider.Releases == 1 && !_startup.OwnsResourceSystem, "retired startup physical drain");
            Assert.That(_stopNotifications, Is.EqualTo(1));
            Assert.That(_system.State, Is.EqualTo(ResourceSystemState.Invalidated));
            Assert.That(_system.GetDiagnostics().DrainingHandles, Is.Empty);
            ClearExitProvider();
            yield return new EnterPlayMode(false);
            yield return LoadInNewSession();
            yield return new ExitPlayMode();
        }

        private static IEnumerator BeginExitDrain()
        {
            AssertDomainSurvives();
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return Start(_startup);
            _system = _startup.Resources;
            using var acquired = _system.AcquireAsync<TextAsset>(MessageKey);
            yield return acquired;
            _lease = acquired.TakeResult();

            _exitProvider = new ExitProvider();
            _exitLocator = new ResourceLocationMap("yl-owned-exit-drain");
            const string Address = "ownership/exit-drain";
            _exitLocator.Add(Address, new ResourceLocationBase(Address, Address, _exitProvider.ProviderId, typeof(TextAsset)));
            Addressables.ResourceManager.ResourceProviders.Add(_exitProvider);
            Addressables.AddResourceLocator(_exitLocator);
            var scope = _system.CreateScope("exit-drain");
            var scoped = scope.LoadAsync<TextAsset>(Address);
            var independent = _system.AcquireAsync<TextAsset>(Address);
            _exitNotifications = new ResourceRequestStatus[4];
            scoped.Completed += value =>
            {
                _exitNotifications[0] = value.Status;
                _exitNotifications[1] = independent.Status;
                independent.Cancel();
            };
            independent.Completed += value =>
            {
                _exitNotifications[2] = value.Status;
                _exitNotifications[3] = scoped.Status;
                scope.Dispose();
            };
            yield return Wait(() => _exitProvider.Provides == 1, "real ResourceManager provide before scene exit");
            Assert.That(_lease.IsValid && !scoped.IsDone && !independent.IsDone, Is.True);
            Assert.That(_exitProvider.Releases, Is.Zero);
        }

        private static IEnumerator DrainExitedSession()
        {
            AssertDomainSurvives();
            Assert.That(Application.isPlaying, Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(_system.State, Is.EqualTo(ResourceSystemState.Invalidated));
            Assert.That(_lease.IsValid, Is.False);
            Assert.That(_exitNotifications, Is.All.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(_system.GetDiagnostics().PendingWorkCount + _system.GetDiagnostics().DrainingHandleCount, Is.Positive);
            Assert.That(_exitProvider.Releases, Is.Zero);
            _lateText = new TextAsset("old session late result");
            _exitProvider.Complete(_lateText);
            yield return Wait(() => _exitProvider.Releases == 1 && _system.GetDiagnostics().DrainingHandleCount == 0 &&
                _system.GetDiagnostics().PendingWorkCount == 0, "retired session returns its late official operation");
            var snapshot = _system.GetDiagnostics();
            Assert.That(snapshot.LeaseCount + snapshot.ScopeCount + snapshot.RequestCount, Is.Zero);
            Assert.That(snapshot.Errors, Is.Empty);
            Assert.That(_lateText.text, Is.EqualTo("old session late result"));
            ClearExitProvider();
        }

        private static IEnumerator LoadInNewSession()
        {
            AssertDomainSurvives();
            var next = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return Start(next);
            Assert.That(next.Resources, Is.Not.SameAs(_system));
            using var request = next.Resources.AcquireAsync<TextAsset>(MessageKey);
            yield return request;
            using var lease = request.TakeResult();
            Assert.That(lease.Asset.text, Is.EqualTo(MessageText));
            lease.Dispose();
            yield return Stop(next);
        }

        private static void ClearExitProvider()
        {
            if (_exitLocator != null)
                Addressables.RemoveResourceLocator(_exitLocator);
            if (_exitProvider != null)
            {
                if (_exitProvider.HasPending)
                {
                    _lateText = new TextAsset("cleanup late result");
                    _exitProvider.Complete(_lateText);
                }
                Addressables.ResourceManager.ResourceProviders.Remove(_exitProvider);
            }
            if (_lateText)
                UnityEngine.Object.DestroyImmediate(_lateText);
            _exitLocator = null;
            _exitProvider = null;
            _lateText = null;
        }

        private sealed class ExitProvider : ResourceProviderBase
        {
            private ProvideHandle _pending;
            internal bool HasPending;
            internal int Provides;
            internal int Releases;

            internal ExitProvider() => m_ProviderId = "yl-exit-drain-" + Guid.NewGuid().ToString("N");

            public override void Provide(ProvideHandle handle)
            {
                Assert.That(HasPending, Is.False);
                Provides++;
                _pending = handle;
                HasPending = true;
            }

            internal void Complete(TextAsset value)
            {
                Assert.That(HasPending, Is.True);
                HasPending = false;
                _pending.Complete(value, true, null);
            }

            public override void Release(IResourceLocation location, object asset) => Releases++;
        }

        private static IEnumerator RunTwoPlaySessions(bool disableDomainReload)
        {
            if (disableDomainReload)
            {
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                EditorSettings.enterPlayModeOptionsEnabled = true;
                if (SessionState.GetString(Key + "domain", "") == "")
                    SessionState.SetString(Key + "domain", DomainLifetime);
            }
            yield return new EnterPlayMode(!disableDomainReload);
            if (disableDomainReload)
                AssertDomainSurvives();
            Assert.That(ResourceSystem.Current, Is.Null);
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return Start(_startup);
            _system = _startup.Resources;
            Application.quitting += OnQuit;
            EditorApplication.playModeStateChanged += OnExit;
            _externalHandle = Addressables.LoadAssetAsync<TextAsset>(MessageKey);
            yield return Wait(() => _externalHandle.IsDone, "independent official asset before exit");
            Assert.That(_externalHandle.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), _externalHandle.OperationException?.ToString());
            _external = _externalHandle.Result;
            Assert.That(_external.text, Is.EqualTo(MessageText));
            using (var acquired = _system.AcquireAsync<TextAsset>(MessageKey))
            {
                yield return Wait(() => acquired.IsDone, "leased asset before exit");
                Assert.That(acquired.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), acquired.Error?.ToString());
                _lease = acquired.TakeResult();
            }
            _unclaimed = _system.AcquireAsync<TextAsset>(MessageKey);
            yield return Wait(() => _unclaimed.IsDone, "successful unclaimed result before exit");
            Assert.That(_unclaimed.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), _unclaimed.Error?.ToString());
            Assert.That(_system.GetDiagnostics().LeaseCount, Is.EqualTo(2));
            yield return new ExitPlayMode();
            string probeError = SessionState.GetString(Key + "probeError", "");
            Assert.That(SessionState.GetBool(Key + "exit", false), Is.True, "Actual exit must reject reentry and invalidate old leases. " + probeError);
            Assert.That(SessionState.GetBool(Key + "quit", false), Is.True,
                "Quit must return only YL ownership, preserve the successful terminal status, and leave the independent AA owner alive. " + probeError);
            var previous = disableDomainReload ? _system : null;
            var oldStartup = disableDomainReload ? _startup : null;
            yield return new EnterPlayMode(!disableDomainReload);
            if (disableDomainReload)
                AssertDomainSurvives();
            Assert.That(ResourceSystem.Current, Is.Null);
            var nextStartup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return Start(nextStartup);
            var fresh = nextStartup.Resources;
            if (disableDomainReload)
            {
                Assert.That(fresh, Is.Not.SameAs(previous));
                Assert.That(previous.IsAvailable || previous.IsSessionActive, Is.False);
                Assert.That(_lease.IsValid, Is.False);
                Assert.That(_unclaimed.IsDisposed, Is.True);
                yield return Stop(oldStartup);
                Assert.That(ResourceSystem.Current, Is.SameAs(fresh), "A stale owner may only finish its original service.");
                Assert.That(fresh.IsAvailable, Is.True);
            }
            using (var next = fresh.AcquireAsync<TextAsset>(MessageKey))
            {
                yield return Wait(() => next.IsDone, "new Play session asset");
                Assert.That(next.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), next.Error?.ToString());
                using var lease = next.TakeResult();
                Assert.That(lease.Asset.text, Is.EqualTo(MessageText));
            }
            yield return Stop(nextStartup);
            yield return new ExitPlayMode();
        }

        private static void AssertDomainSurvives()
        {
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(DomainLifetime, Is.EqualTo(SessionState.GetString(Key + "domain", "")), "Both entries must retain the real managed domain.");
        }

        internal static IEnumerator Start(ResourceStartup startup)
        {
            using var request = startup.StartAsync();
            yield return Wait(() => request.IsDone, "official Resource startup");
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            Assert.That(request.TakeResult(), Is.SameAs(startup));
            Assert.That(startup.OwnsResourceSystem, Is.True);
            Assert.That(startup.Resources.IsAvailable, Is.True);
        }

        internal static IEnumerator Stop(ResourceStartup startup)
        {
            var resources = startup.Resources;
            using var request = startup.StopAsync();
            yield return Wait(() => request.IsDone, "owned Resource shutdown");
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            Assert.That(request.TakeResult(), Is.True);
            Assert.That(startup.Resources, Is.Null);
            if (resources == null)
                yield break;
            var report = resources.GetDiagnostics();
            Assert.That(report.State, Is.EqualTo(ResourceSystemState.Closed));
            Assert.That(report.LeaseCount, Is.Zero);
            Assert.That(report.ScopeCount, Is.Zero);
            Assert.That(report.RequestCount, Is.Zero);
            Assert.That(report.PendingWorkCount, Is.Zero);
            Assert.That(report.DrainingHandleCount, Is.Zero);
            Assert.That(report.Errors, Is.Empty);
        }

        internal static IEnumerator Wait(Func<bool> completed, string operation)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!completed() && EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.That(completed(), Is.True, operation + " timed out.");
        }
    }

    [Category("YL.Module.Resource")]
    [Category("YL.Special.ResourceSession")]
    public class ResourceSessionSafetyTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ExitFixtureRejectsDirtyUnsavedScenesWithoutClosingOrSavingThem(bool withContent)
        {
            ResourceSessionTests.EnsureRestorableScenes();
            var previous = SceneManager.GetActiveScene();
            var replaceRunner = string.IsNullOrEmpty(previous.path);
            if (replaceRunner)
            {
                string project = Path.GetDirectoryName(Application.dataPath);
                string marker = Path.Combine(project, ".yl-owner.json");
                Assert.That(Application.isBatchMode && File.Exists(marker), Is.True, "Do not close an untitled user scene.");
                var owner = JObject.Parse(File.ReadAllText(marker));
                Assert.That((string)owner["purpose"], Is.EqualTo("YLTesting.Directory"));
                Assert.That((string)owner["kind"], Is.EqualTo("Host"));
                Assert.That(Path.GetFullPath((string)owner["path"]), Is.EqualTo(Path.GetFullPath(project)).IgnoreCase);
                var anchor = SceneManager.GetSceneByPath("Assets/YLTestingProfile/Empty.unity");
                Assert.That(SceneManager.sceneCount == 2 && previous.isLoaded && anchor.IsValid() && anchor.isLoaded && anchor.rootCount == 0,
                    Is.True, "Additive authoring requires the saved, empty preparation scene beside the clean UTF runner.");
                Activate(anchor);
                Assert.That(EditorSceneManager.CloseScene(previous, true), Is.True);
                previous = anchor;
            }

            Scene owned = default;
            GameObject content = null;
            try
            {
                owned = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.That(owned.path, Is.Empty);
                Assert.That(owned.rootCount, Is.Zero);
                Assert.That(owned.isDirty, Is.False);
                if (withContent)
                {
                    Activate(owned);
                    content = new GameObject("Resource81.UnsavedSafetyFixture");
                }
                else
                {
                    EditorSceneManager.MarkSceneDirty(owned);
                }

                Assert.Throws<AssertionException>(() => ResourceSessionTests.EnsureRestorableScenes());
                Assert.That(owned.isLoaded, Is.True);
                Assert.That(owned.path, Is.Empty);
                if (withContent)
                {
                    Assert.That(content, Is.Not.Null);
                }
            }
            finally
            {
                if (owned.IsValid() && owned.isLoaded)
                    Assert.That(EditorSceneManager.CloseScene(owned, true), Is.True);
                if (previous.IsValid() && previous.isLoaded)
                    Activate(previous);
                if (replaceRunner)
                    Activate(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive));
            }

            static void Activate(Scene scene)
            {
                if (SceneManager.GetActiveScene().handle != scene.handle)
                    SceneManager.SetActiveScene(scene);
                Assert.That(SceneManager.GetActiveScene().handle, Is.EqualTo(scene.handle));
            }
        }
    }
}
