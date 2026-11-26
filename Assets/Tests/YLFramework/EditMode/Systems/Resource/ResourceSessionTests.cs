using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
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
        private static ShutdownReleaseProbe _exitReleaseProbe;
        private static InitializationDataGate _initializationGate;
        private static AsyncOperationHandle<IResourceLocator> _officialStartup;
        private static ResourceRequest<ResourceStartup> _startupAtExit;
        private static Task<Exception> _startupAwaitAtExit;
        private static int _startupNotifications;
        private static int _startupResumes;
        private static Task<Exception> _assetAwaitAtExit;

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
                // If a prerequisite failed in Play, open the gate before destroying the official callback host.
                _initializationGate?.Unblock();
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
                _exitReleaseProbe = null;
                _startupAtExit?.Dispose();
                _startupAtExit = null;
                _startupAwaitAtExit = null;
                _assetAwaitAtExit = null;
                _initializationGate?.Dispose();
                _initializationGate = null;
                _officialStartup = default;
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
        public IEnumerator ActualExitStillNotifiesAndDrainsWhenReturningAResultThrows()
        {
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            SessionState.SetString(Key + "domain", DomainLifetime);
            yield return new EnterPlayMode(false);
            yield return BeginExitDrain(bindCoreResourceProvider: true);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_startup.CoreProvider));
            _exitReleaseProbe = new ShutdownReleaseProbe(_system);
            _exitReleaseProbe.ExpectFaultLog();
            yield return new ExitPlayMode();

            AssertDomainSurvives();
            _exitReleaseProbe.AssertSettled(_system);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(_lease.IsValid, Is.False);
            Assert.That(_exitNotifications, Is.All.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(_exitProvider.Releases, Is.Zero);
            _lateText = new TextAsset("late result after other release failed");
            _exitProvider.Complete(_lateText);
            yield return Wait(() => _exitProvider.Releases == 1 && !_startup.OwnsResourceSystem,
                "physical drain after a different result failed to return");
            var report = _system.GetDiagnostics();
            Assert.That(report.LeaseCount + report.ScopeCount + report.RequestCount +
                report.PendingWorkCount + report.DrainingHandleCount, Is.Zero);
            ClearExitProvider();
            yield return new EnterPlayMode(false);
            yield return LoadInNewSession();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator StopReportsLastProviderReleaseFaultAfterOfficialCompletionBeforeDelivery()
        {
            yield return new EnterPlayMode();
            yield return StopAfterOfficialCompletion();
            yield return new ExitPlayMode();
        }

        private static IEnumerator StopAfterOfficialCompletion()
        {
            using var startup = new ResourceStartup();
            yield return Start(startup);
            var system = startup.Resources;
            using var healthy = new GatedTextProvider();
            using var faulting = new GatedTextProvider();
            var scope = system.CreateScope("healthy asset during stop");
            using var scoped = scope.LoadAsync<TextAsset>(healthy.Address);
            yield return Wait(() => healthy.IsBlocked, "healthy provider entered Provide");
            healthy.Complete();
            yield return Wait(() => scoped.IsDone, "healthy scope holds the asset");
            Assert.That(scoped.TakeResult(), Is.SameAs(healthy.Value));

            using var load = system.AcquireAsync<TextAsset>(faulting.Address);
            int loadNotifications = 0;
            load.Completed += _ => loadNotifications++;
            yield return Wait(() => faulting.IsBlocked, "faulting provider entered Provide");
            using var other = system.AcquireAsync<TextAsset>(MessageKey);
            int otherNotifications = 0;
            other.Completed += _ => otherNotifications++;
            var fault = new InvalidOperationException("last provider release failed before YL delivery");
            faulting.ReleaseFault = fault;
            faulting.Complete();
            // Complete has returned, so Addressables' own in-flight reference is already gone.
            // No yield before Stop: the producer has not yet transferred its only reference to a lease.
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            Assert.That(other.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            Assert.That(faulting.Releases, Is.Zero);
            Assert.That(healthy.Releases, Is.Zero);
            LogAssert.Expect(LogType.Exception, new Regex(Regex.Escape(fault.Message)));
            using var stop = startup.StopAsync();
            yield return Wait(() => stop.IsDone, "stop after the last official provider release throws");

            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(other.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(load.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(other.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(loadNotifications, Is.EqualTo(1));
            Assert.That(otherNotifications, Is.EqualTo(1));
            Assert.That(healthy.Releases, Is.EqualTo(1));
            Assert.That(faulting.Releases, Is.EqualTo(1), "A throwing release cannot be retried.");
            var report = system.GetDiagnostics();
            Assert.That(report.LeaseCount + report.ScopeCount + report.RequestCount +
                report.PendingWorkCount + report.DrainingHandleCount, Is.Zero);
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(stop.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
            Assert.That(stop.Error.InnerException, Is.TypeOf<AggregateException>());
            Assert.That(((AggregateException)stop.Error.InnerException).Flatten().InnerExceptions.Single(), Is.SameAs(fault));
            Assert.That(report.Errors.Any(error => error.Contains(fault.Message)), Is.True);
        }

        [UnityTest]
        public IEnumerator PendingAcquireAwaitReceivesSessionErrorWhenCompletedObserverStopsOnSuccess()
        {
            yield return new EnterPlayMode();
            yield return AwaitAfterReentrantStop();
            yield return new ExitPlayMode();
        }

        private static IEnumerator AwaitAfterReentrantStop()
        {
            using var startup = new ResourceStartup();
            yield return Start(startup);
            var system = startup.Resources;
            ResourceEventHistory.SetRecording(true);
            using var provider = new GatedTextProvider();
            using var load = system.AcquireAsync<TextAsset>(provider.Address);
            ResourceRequest<bool> stop = null;
            int notifications = 0;
            int resumes = 0;
            bool stoppedBeforeResume = false;
            load.Completed += request =>
            {
                notifications++;
                if (request.Status == ResourceRequestStatus.Succeeded)
                {
                    stop = startup.StopAsync();
                    stoppedBeforeResume = resumes == 0 && request.IsDisposed;
                }
            };
            var awaiting = AwaitResult();
            try
            {
                Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Pending));
                Assert.That(awaiting.IsCompleted, Is.False);
                Assert.Throws<InvalidOperationException>(() => load.GetAwaiter());
                Assert.Throws<InvalidOperationException>(() => load.TakeResult());
                yield return Wait(() => provider.IsBlocked, "awaited asset entered Provide");
                provider.Complete();
                yield return Wait(() => awaiting.IsCompleted && stop?.IsDone == true, "reentrant stop and reserved await");

                Assert.That(stop.TakeResult(), Is.True);
                Assert.That(stoppedBeforeResume, Is.True);
                Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                Assert.That(load.IsDisposed, Is.True);
                Assert.That(notifications, Is.EqualTo(1));
                Assert.That(resumes, Is.EqualTo(1));
                Assert.That(provider.Releases, Is.EqualTo(1));
                Assert.That(system.GetDiagnostics().LeaseCount, Is.Zero);
                Assert.Throws<ObjectDisposedException>(() => load.GetAwaiter());
                Assert.That(() => load.TakeResult(), Throws.InstanceOf<InvalidOperationException>());
                Assert.That(awaiting.Result, Is.TypeOf<ResourceException>());
                Assert.That(((ResourceException)awaiting.Result).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
                var events = ResourceEventHistory.Read().Where(row => row.RequestId == load.EventTrace.Id &&
                    (row.Kind == ResourceEventKind.Terminal || row.Kind == ResourceEventKind.ClaimInvalidated)).ToArray();
                Assert.That(events.Select(row => row.Kind), Is.EqualTo(new[] { ResourceEventKind.Terminal, ResourceEventKind.ClaimInvalidated }));
                Assert.That(events[0].Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                Assert.That(events[1].Error, Is.EqualTo(ResourceError.SessionUnavailable));
            }
            finally
            {
                stop?.Dispose();
            }

            async Task<Exception> AwaitResult()
            {
                try
                {
                    using var result = await load;
                    return null;
                }
                catch (Exception error)
                {
                    return error;
                }
                finally
                {
                    resumes++;
                }
            }
        }

        [UnityTest]
        public IEnumerator ExitDuringOfficialInitializationSettlesTheObserverBeforeTheLoadFinishes()
        {
            DisableDomainReload();
            yield return ObserveEditorDelayCall();
            yield return new EnterPlayMode(false);
            yield return BeginDelayedInitialization();
            yield return new ExitPlayMode();

            Assert.That(Application.isPlaying, Is.False);
            AssertInitializationInvalidatedBeforeUnblock();
            _initializationGate.Unblock();
            yield return WaitForRetiredInitialization();
            Assert.That(Application.isPlaying, Is.False, "The retired operation must drain without entering Play again.");
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(_startup.Resources, Is.Null);
            Assert.That(_startup.AudioProviders, Is.Empty);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
        }

        [UnityTest]
        public IEnumerator NextPlayWithTheSameStartupSurvivesCompletionOfItsRetiredOfficialInitialization()
        {
            DisableDomainReload();
            yield return ObserveEditorDelayCall();
            yield return new EnterPlayMode(false);
            yield return BeginDelayedInitialization();
            yield return new ExitPlayMode();

            AssertInitializationInvalidatedBeforeUnblock();
            yield return ObserveEditorDelayCall();
            yield return new EnterPlayMode(false);
            yield return OverlapRetiredInitializationWithNextPlay(requireDistinctManager: true);
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ImmediateNextPlayCanStartWhileRetiredOfficialInitializationIsStillPending()
        {
            DisableDomainReload();
            yield return ObserveEditorDelayCall();
            yield return new EnterPlayMode(false);
            yield return BeginDelayedInitialization();
            yield return new ExitPlayMode();

            AssertInitializationInvalidatedBeforeUnblock();
            yield return new EnterPlayMode(false);
            yield return OverlapRetiredInitializationWithNextPlay(requireDistinctManager: false);
            yield return new ExitPlayMode();
        }

        private static IEnumerator ObserveEditorDelayCall()
        {
            // Allow Addressables' own pending exit notification to run; the fixture only observes this boundary.
            var observed = false;
            EditorApplication.CallbackFunction mark = () => observed = true;
            EditorApplication.delayCall += mark;
            try
            {
                yield return Wait(() => observed, "editor delayCall before entering Play");
            }
            finally
            {
                EditorApplication.delayCall -= mark;
            }
        }

        private static void DisableDomainReload()
        {
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            SessionState.SetString(Key + "domain", DomainLifetime);
        }

        private static IEnumerator BeginDelayedInitialization()
        {
            AssertDomainSurvives();
            _initializationGate = new InitializationDataGate(Addressables.ResourceManager);
            _startup = new ResourceStartup(audioProjects: new[] { "ownership" });
            _startupAtExit = _startup.StartAsync();
            _startupNotifications = 0;
            _startupResumes = 0;
            _startupAtExit.Completed += _ => _startupNotifications++;
            _startupAwaitAtExit = ObserveAwaitError(_startupAtExit, () => _startupResumes++);
            _system = ResourceSystem.GetOwnedSystem(_startup);
            yield return Wait(() => _initializationGate.IsBlocked, "official initialization requests its packed runtime data");
            // An initialization already in flight is returned as a borrowed handle by Addressables.
            _officialStartup = Addressables.InitializeAsync(false);
            Assert.That(_startupAtExit.IsDone, Is.False);
            Assert.That(_officialStartup.IsValid() && !_officialStartup.IsDone, Is.True);
            Assert.That(_initializationGate.Releases, Is.Zero);
        }

        private static void AssertInitializationInvalidatedBeforeUnblock()
        {
            AssertDomainSurvives();
            Assert.That(_system.State, Is.EqualTo(ResourceSystemState.Invalidated));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(_startup.Resources, Is.Null);
            Assert.That(_startup.CoreProvider, Is.Null);
            Assert.That(_startup.AudioProviders, Is.Empty);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(_startupAtExit.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(_startupAwaitAtExit.IsCompletedSuccessfully, Is.True);
            Assert.That(_startupAwaitAtExit.Result, Is.SameAs(_startupAtExit.Error));
            Assert.That(_startupAtExit.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(_startupNotifications, Is.EqualTo(1));
            Assert.That(_startupResumes, Is.EqualTo(1));
            AssertRetiredOfficialInitializationIsStillPending();
        }

        private static void AssertRetiredOfficialInitializationIsStillPending()
        {
            Assert.That(_initializationGate.IsBlocked, Is.True);
            Assert.That(_officialStartup.IsValid() && !_officialStartup.IsDone, Is.True);
            Assert.That(_initializationGate.Releases, Is.Zero);
            Assert.That(_system.GetDiagnostics().PendingWorkCount, Is.Positive);
        }

        private static IEnumerator WaitForRetiredInitialization()
        {
            yield return Wait(() => !_officialStartup.IsValid() && _system.GetDiagnostics().PendingWorkCount == 0 &&
                _system.GetDiagnostics().DrainingHandleCount == 0, "retired official initialization drained without a test pump");
            Assert.That(_initializationGate.Releases, Is.EqualTo(1), "The official runtime data was returned once.");
            Assert.That(_system.State, Is.EqualTo(ResourceSystemState.Invalidated));
            Assert.That(_startupAtExit.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(_startupNotifications, Is.EqualTo(1));
            Assert.That(_startupResumes, Is.EqualTo(1));
        }

        private static IEnumerator OverlapRetiredInitializationWithNextPlay(bool requireDistinctManager)
        {
            AssertDomainSurvives();
            AssertRetiredOfficialInitializationIsStillPending();
            using var starting = _startup.StartAsync();
            var next = ResourceSystem.GetOwnedSystem(_startup);
            Assert.That(next, Is.Not.Null.And.Not.SameAs(_system));
            Assert.That(next.State, Is.EqualTo(ResourceSystemState.Initializing));
            Assert.That(starting.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            var managerOnEntry = Addressables.ResourceManager;
            var ticked = false;
            using (next.AddTick(() => ticked = true))
            {
                yield return Wait(() => ticked, "first next-Play YL startup tick");
            }

            var managerAfterTick = Addressables.ResourceManager;
            if (requireDistinctManager)
            {
                Assert.That(managerOnEntry, Is.Not.SameAs(_initializationGate.Manager));
                Assert.That(managerAfterTick, Is.SameAs(managerOnEntry));
            }
            else if (ReferenceEquals(managerOnEntry, _initializationGate.Manager))
            {
                // Registry 1.25 may reuse the in-flight initialization before its delayed reset.
                if (ReferenceEquals(managerAfterTick, managerOnEntry))
                {
                    Assert.That(Addressables.InitializeAsync(false), Is.EqualTo(_officialStartup));
                    Assert.That(starting.Status, Is.EqualTo(ResourceRequestStatus.Pending));
                    Assert.That(next.GetDiagnostics().PendingWorkCount, Is.Positive);
                }

                AssertRetiredOfficialInitializationIsStillPending();
                _initializationGate.Unblock();
            }

            yield return Wait(() => starting.IsDone, "next Play official Resource startup");
            Assert.That(starting.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), starting.Error?.ToString());
            Assert.That(starting.TakeResult(), Is.SameAs(_startup));
            Assert.That(_startup.Resources, Is.SameAs(next));
            var core = _startup.CoreProvider;
            var audio = _startup.AudioProviders["ownership"];
            Assert.That(core, Is.Not.Null);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(core));
            using var request = next.AcquireAsync<TextAsset>(MessageKey);
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            if (_initializationGate.IsBlocked)
            {
                AssertRetiredOfficialInitializationIsStillPending();
                _initializationGate.Unblock();
            }
            yield return WaitForRetiredInitialization();
            yield return Wait(() => request.IsDone, "new Play request overlapping the retired official initialization");
            using (var lease = request.TakeResult())
            {
                Assert.That(lease.Asset.text, Is.EqualTo(MessageText));
                Assert.That(next.IsAvailable, Is.True);
                Assert.That(ResourceSystem.Current, Is.SameAs(next));
                Assert.That(_startup.Resources, Is.SameAs(next));
                Assert.That(_startup.CoreProvider, Is.SameAs(core));
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(core));
                Assert.That(_startup.AudioProviders["ownership"], Is.SameAs(audio));
            }
            yield return Stop(_startup);
        }

        internal static async Task<Exception> ObserveAwaitError<T>(ResourceRequest<T> request, Action resumed = null)
        {
            try
            {
                await request;
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
            finally
            {
                resumed?.Invoke();
            }
        }

        private sealed class InitializationDataGate : JsonAssetProvider, IDisposable
        {
            private readonly ResourceManager _manager;
            private ProvideHandle _pending;
            internal ResourceManager Manager => _manager;
            internal bool IsBlocked
            {
                get; private set;
            }
            internal int Releases
            {
                get; private set;
            }

            internal InitializationDataGate(ResourceManager manager)
            {
                _manager = manager;
                m_ProviderId = typeof(JsonAssetProvider).FullName;
                _manager.ResourceProviders.Insert(0, this);
            }

            public override bool CanProvide(Type type, IResourceLocation location) => type == typeof(ResourceManagerRuntimeData);

            public override void Provide(ProvideHandle handle)
            {
                Assert.That(IsBlocked, Is.False);
                _pending = handle;
                IsBlocked = true;
            }

            internal void Unblock()
            {
                if (IsBlocked)
                {
                    var pending = _pending;
                    IsBlocked = false;
                    _pending = default;
                    base.Provide(pending);
                }
            }

            public override void Release(IResourceLocation location, object asset)
            {
                Releases++;
                base.Release(location, asset);
            }

            public void Dispose()
            {
                try
                {
                    Unblock();
                }
                finally
                {
                    _manager.ResourceProviders.Remove(this);
                }
            }
        }

        private sealed class GatedTextProvider : ResourceProviderBase, IDisposable
        {
            private readonly ResourceManager _manager;
            private readonly ResourceLocationMap _locator;
            private ProvideHandle _pending;
            internal string Address => ProviderId;
            internal TextAsset Value
            {
                get;
            }
            internal bool IsBlocked
            {
                get; private set;
            }
            internal int Releases
            {
                get; private set;
            }
            internal Exception ReleaseFault
            {
                get; set;
            }

            internal GatedTextProvider()
            {
                m_ProviderId = "ownership/gated-text-" + Guid.NewGuid().ToString("N");
                _manager = Addressables.ResourceManager;
                _locator = new ResourceLocationMap(ProviderId);
                _locator.Add(Address, new ResourceLocationBase(Address, Address, ProviderId, typeof(TextAsset)));
                Value = new TextAsset(Address);
                _manager.ResourceProviders.Add(this);
                Addressables.AddResourceLocator(_locator);
            }

            public override void Provide(ProvideHandle handle)
            {
                _pending = handle;
                IsBlocked = true;
            }

            internal void Complete()
            {
                Assert.That(IsBlocked, Is.True);
                var pending = _pending;
                _pending = default;
                IsBlocked = false;
                pending.Complete(Value, true, null);
            }

            public override void Release(IResourceLocation location, object asset)
            {
                Releases++;
                if (ReleaseFault != null)
                {
                    throw ReleaseFault;
                }
            }

            public void Dispose()
            {
                ReleaseFault = null;
                try
                {
                    if (IsBlocked)
                    {
                        Complete();
                    }
                }
                finally
                {
                    Addressables.RemoveResourceLocator(_locator);
                    _manager.ResourceProviders.Remove(this);
                    UnityEngine.Object.DestroyImmediate(Value);
                }
            }
        }

        // Whichever result is visited first throws, so the test never depends on HashSet ordering.
        internal sealed class ShutdownReleaseProbe
        {
            internal readonly InvalidOperationException Fault = new("synthetic provider release failed");
            private readonly ResourceRequest<int> _pending;
            private readonly int[] _returned = new int[2];
            private int _totalReturns;
            private int _successNotifications;
            private int _pendingNotifications;

            internal ShutdownReleaseProbe(ResourceSystem system)
            {
                for (int i = 0; i < _returned.Length; i++)
                {
                    var request = system.Track(new ResourceRequest<int>(value =>
                    {
                        _returned[value]++;
                        if (++_totalReturns == 1)
                        {
                            throw Fault;
                        }
                    }));
                    request.Completed += _ => _successNotifications++;
                    request.SetSuccess(i);
                    request.NotifyCompletion();
                }
                _pending = system.Track(new ResourceRequest<int>());
                _pending.Completed += _ => _pendingNotifications++;
            }

            internal void ExpectFaultLog() => LogAssert.Expect(LogType.Exception, new Regex(Regex.Escape(Fault.Message)));

            internal void AssertSettled(ResourceSystem system)
            {
                Assert.That(_returned, Is.All.EqualTo(1), "The fault must not skip another release or retry itself.");
                Assert.That(_successNotifications, Is.EqualTo(2), "Already delivered notifications must not be repeated.");
                Assert.That(_pendingNotifications, Is.EqualTo(1));
                Assert.That(_pending.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
                Assert.That(_pending.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
                Assert.That(system.GetDiagnostics().Errors.Any(error => error.Contains(Fault.Message)), Is.True);
            }
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

        private static IEnumerator BeginExitDrain(bool bindCoreResourceProvider = false)
        {
            AssertDomainSurvives();
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider);
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
            _assetAwaitAtExit = ObserveAwaitError(independent);
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
            Assert.That(_assetAwaitAtExit.IsCompletedSuccessfully, Is.True);
            Assert.That((_assetAwaitAtExit.Result as ResourceException)?.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
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
