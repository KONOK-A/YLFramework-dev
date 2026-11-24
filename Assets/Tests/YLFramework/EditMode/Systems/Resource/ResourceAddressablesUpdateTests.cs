#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Special.ResourceUpdates")]
    public sealed class ResourceAddressablesUpdateTests
    {
        private const string StateKey = "YL.ResourceAddressablesUpdateTests.";
        private const string DisableStartupChecks = "DisableCatalogUpdateOnStartup";
        private const string PackedCatalogId = "updates/packed";
        private const string PackedCatalogHash = "11111111111111111111111111111111";
        private const string PublishedCatalogHash = "22222222222222222222222222222222";
        private readonly List<IDisposable> _owned = new();
        private readonly List<IResourceLocator> _locators = new();
        private ResourceStartup _startup;
        private HashProvider _provider;
        private string _temporaryRuntimeData;
        private string _packedCatalogDirectory;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            RequireOwnedHost();
            if (!Application.isPlaying)
            {
                Assert.That(Application.isPlaying, Is.False);
                Assert.That(SessionState.GetBool(StateKey + "saved", false), Is.False, "Do not overwrite an unfinished fixture's original PlayerPrefs.");
                SessionState.SetBool(StateKey + "hadPath", PlayerPrefs.HasKey(Addressables.kAddressablesRuntimeDataPath));
                SessionState.SetString(StateKey + "oldPath", PlayerPrefs.GetString(Addressables.kAddressablesRuntimeDataPath));
                SessionState.SetBool(StateKey + "saved", true);
                ResourceAddressablesTestContent.Build();
                if (Equals(TestContext.CurrentContext.Test.Properties.Get(DisableStartupChecks), 0))
                {
                    var settings = AddressableAssetSettingsDefaultObject.Settings;
                    settings.DisableCatalogUpdateOnStartup = false;
                    var build = settings.DataBuilders.OfType<BuildScriptPackedMode>().Single()
                        .BuildData<AddressablesPlayerBuildResult>(new AddressablesDataBuilderInput(settings));
                    Assert.That(build.Error, Is.Null.Or.Empty);
                }
                yield return new EnterPlayMode();
            }
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(SessionState.GetBool(StateKey + "saved", false), Is.True, "Only this fixture's owned Play session may be used.");
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RequireOwnedHost();
            if (Application.isPlaying && SessionState.GetBool(StateKey + "saved", false))
            {
                ResourceEventHistory.SetRecording(false);
                ResourceEventHistory.Clear();
                _provider?.CompleteDownload();
                _provider?.CompleteHashes();
                for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
                _owned.Clear();
                if (_startup != null)
                {
                    using var stop = _startup.StopAsync();
                    yield return Wait(stop);
                    Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
                }
                foreach (var locator in _locators)
                {
                    foreach (var current in Addressables.ResourceLocators.Where(value => value.LocatorId == locator.LocatorId).ToArray())
                        Addressables.RemoveResourceLocator(current);
                }
                if (_provider != null) Addressables.ResourceManager.ResourceProviders.Remove(_provider);
                if (_temporaryRuntimeData != null) File.Delete(_temporaryRuntimeData);
                if (_packedCatalogDirectory != null) Directory.Delete(_packedCatalogDirectory, true);
                yield return new ExitPlayMode();
            }
            if (SessionState.GetBool(StateKey + "saved", false))
            {
                var hadPath = SessionState.GetBool(StateKey + "hadPath", false);
                var oldPath = SessionState.GetString(StateKey + "oldPath", "");
                if (hadPath) PlayerPrefs.SetString(Addressables.kAddressablesRuntimeDataPath, oldPath);
                else PlayerPrefs.DeleteKey(Addressables.kAddressablesRuntimeDataPath);
                Assert.That(PlayerPrefs.HasKey(Addressables.kAddressablesRuntimeDataPath), Is.EqualTo(hadPath));
                if (hadPath) Assert.That(PlayerPrefs.GetString(Addressables.kAddressablesRuntimeDataPath), Is.EqualTo(oldPath));
                SessionState.EraseBool(StateKey + "saved");
                SessionState.EraseBool(StateKey + "hadPath");
                SessionState.EraseString(StateKey + "oldPath");
            }
        }

        private static void RequireOwnedHost()
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var marker = Path.Combine(project, ".yl-owner.json");
            Assert.That(File.Exists(marker), Is.True, "This fixture may enter/exit Play only in a tool-owned test host.");
            var owner = JObject.Parse(File.ReadAllText(marker));
            Assert.That((string)owner["kind"], Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath((string)owner["path"]), Is.EqualTo(project).IgnoreCase);
        }

        private T Own<T>(T value) where T : IDisposable
        {
            _owned.Add(value);
            return value;
        }

        private IEnumerator Start(bool enableUpdates = false)
        {
            _startup = new ResourceStartup(new ResourceConfiguration(enableStartupCatalogUpdates: enableUpdates), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return Wait(start);
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
            start.TakeResult();
        }

        [UnityTest]
        public IEnumerator DefaultPolicy_AllowsCheckAndDownload_ButRejectsApply()
        {
            yield return Start();
            var system = _startup.Resources;
            using var size = system.GetDownloadSizeAsync(new[] { "ownership/message" });
            using var download = system.DownloadDependenciesAsync(new[] { "ownership/message" });
            using var check = system.CheckForCatalogUpdatesAsync();
            using var apply = system.ApplyCatalogUpdatesAsync(new[] { "not-authorized" });
            yield return Wait(size);
            yield return Wait(download);
            yield return Wait(check);
            Assert.That(size.TakeResult(), Is.GreaterThanOrEqualTo(0));
            Assert.That(download.TakeResult(), Is.True);
            Assert.That(check.TakeResult(), Is.Empty);
            Assert.That(apply.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
        }

        [UnityTest]
        [NUnit.Framework.Property(DisableStartupChecks, 0)]
        public IEnumerator EnabledPolicy_RequiresTrueFlagInRealBuiltRuntimeData()
        {
            var providers = Addressables.ResourceManager.ResourceProviders.ToArray();
            Assert.That(Addressables.ResourceLocators, Is.Empty);
            _startup = new ResourceStartup(new ResourceConfiguration(enableStartupCatalogUpdates: true), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return Wait(start);
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(start.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            Assert.That(start.Error.ToString(), Does.Contain("DisableCatalogUpdateOnStartup"));
            Assert.That(Addressables.ResourceLocators, Is.Empty, "Reject before official initialization, not after its automatic update check.");
            CollectionAssert.AreEqual(providers, Addressables.ResourceManager.ResourceProviders.ToArray());
        }

        [UnityTest]
        public IEnumerator PlayerPrefsRuntimeDataOverride_IsTheValidatedInput()
        {
            _temporaryRuntimeData = Path.Combine(Application.temporaryCachePath, "yl-runtime-policy-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_temporaryRuntimeData, JsonUtility.ToJson(new ResourceManagerRuntimeData { DisableCatalogUpdateOnStartup = false }));
            PlayerPrefs.SetString(Addressables.kAddressablesRuntimeDataPath, _temporaryRuntimeData);
            _startup = new ResourceStartup(new ResourceConfiguration(enableStartupCatalogUpdates: true), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return Wait(start);
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(start.Error.ToString(), Does.Contain("DisableCatalogUpdateOnStartup"));
            Assert.That(Addressables.ResourceLocators, Is.Empty);
        }

        [UnityTest]
        [NUnit.Framework.Property(DisableStartupChecks, 0)]
        public IEnumerator LocationSensitiveRuntimeDataTransformCannotAuthorizeControlledStartup()
        {
            _temporaryRuntimeData = Path.Combine(Application.temporaryCachePath, "yl-runtime-policy-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(_temporaryRuntimeData, JsonUtility.ToJson(new ResourceManagerRuntimeData { DisableCatalogUpdateOnStartup = true }));
            var previous = Addressables.InternalIdTransformFunc;
            Func<IResourceLocation, string> transform = location => location.ResourceType == typeof(ResourceManagerRuntimeData) &&
                location.PrimaryKey != "RuntimeData" ? _temporaryRuntimeData : location.InternalId;
            Addressables.InternalIdTransformFunc = transform;
            try
            {
                _startup = new ResourceStartup(new ResourceConfiguration(enableStartupCatalogUpdates: true), bindCoreResourceProvider: false);
                using var start = _startup.StartAsync();
                yield return Wait(start);
                Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(start.Error.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
                Assert.That(Addressables.ResourceLocators, Is.Empty, "Do not launch official initialization on an unverified runtime data source.");
                Assert.That(Addressables.InternalIdTransformFunc, Is.SameAs(transform));
            }
            finally
            {
                Addressables.InternalIdTransformFunc = previous;
            }
        }

        [UnityTest]
        public IEnumerator MissingRuntimeData_FailsClosed_NotAnImplicitDisabledPolicy()
        {
            _temporaryRuntimeData = Path.Combine(Application.temporaryCachePath, "yl-runtime-missing-" + Guid.NewGuid().ToString("N") + ".json");
            PlayerPrefs.SetString(Addressables.kAddressablesRuntimeDataPath, _temporaryRuntimeData);
            _startup = new ResourceStartup(new ResourceConfiguration(enableStartupCatalogUpdates: true), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return Wait(start);
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(start.Error.Kind, Is.EqualTo(ResourceError.InvalidConfiguration));
            Assert.That(start.Error.ToString(), Does.Contain("runtime data could not be verified"));
            Assert.That(Addressables.ResourceLocators, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ExistingExternalLocators_RejectEnabledTakeover_ButLoadOnlyCoexists()
        {
            var native = Addressables.InitializeAsync(false);
            yield return native;
            Assert.That(native.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), native.OperationException?.ToString());
            Addressables.Release(native);
            var before = Addressables.ResourceLocators.ToArray();
            _startup = new ResourceStartup(new ResourceConfiguration(enableStartupCatalogUpdates: true), bindCoreResourceProvider: false);
            using var rejected = _startup.StartAsync();
            yield return Wait(rejected);
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
            CollectionAssert.AreEqual(before, Addressables.ResourceLocators.ToArray());
            using var closed = _startup.StopAsync();
            yield return Wait(closed);
            Assert.That(closed.TakeResult(), Is.True);
            yield return Start();
            using var loaded = _startup.Resources.AcquireAsync<TextAsset>("ownership/message");
            yield return Wait(loaded);
            var lease = Own(loaded.TakeResult());
            Assert.That(lease.Asset.text, Is.EqualTo("official addressables content"));
            CollectionAssert.AreEqual(before, Addressables.ResourceLocators.ToArray());
        }

        [UnityTest]
        public IEnumerator SizeAndUnionDownload_FreezeKeysWithoutChangingBusinessWindow()
        {
            yield return Start(true);
            var keys = new List<string> { "ownership/message", "ownership/prefab", "ownership/message" };
            var native = Addressables.GetDownloadSizeAsync(keys.ToArray());
            while (!native.IsDone) yield return null;
            Assert.That(native.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), native.OperationException?.ToString());
            var expected = native.Result;
            Addressables.Release(native);
            using var size = _startup.Resources.GetDownloadSizeAsync(keys);
            using var download = _startup.Resources.DownloadDependenciesAsync(keys);
            keys.Clear();
            yield return Wait(size);
            yield return Wait(download);
            Assert.That(size.TakeResult(), Is.EqualTo(expected));
            Assert.That(download.TakeResult(), Is.True);
            Assert.That(_startup.Resources.GetDiagnostics().LeaseCount, Is.Zero);
            using var invalid = _startup.Resources.ApplyCatalogUpdatesAsync(new[] { "unknown-explicit-id" });
            yield return Wait(invalid);
            Assert.That(invalid.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound), "Downloads must not close the startup business window.");
        }

        [UnityTest]
        public IEnumerator CanceledDownload_ObservesOfficialResultAndReturnsPinAfterProviderCompletes()
        {
            yield return Start();
            ResourceEventHistory.SetRecording(true);
            const string key = "updates/download";
            AddGatedDownload(key);

            var download = Own(_startup.Resources.DownloadDependenciesAsync(new[] { key }));
            yield return Until(() => _provider.DownloadPending);
            download.Cancel();
            Assert.That(download.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(_provider.DownloadReleases, Is.Zero);
            Assert.That(ResourceEventHistory.Read().Count(row => row.RequestId == download.EventTrace.Id), Is.EqualTo(1));
            _provider.CompleteDownload();
            yield return Until(() => _provider.DownloadReleases == 1);
            AssertLateDelivery(download.EventTrace.Id, ResourceEventSource.Download);
        }

        private void AddGatedDownload(string key, Action onLocate = null)
        {
            _provider = new HashProvider();
            Addressables.ResourceManager.ResourceProviders.Add(_provider);
            var locator = new DownloadLocationMap(key, onLocate);
            _locators.Add(locator);
            var bundle = new ResourceLocationBase(key + "/bundle", key + "/bundle", _provider.ProviderId, typeof(IAssetBundleResource));
            locator.Add(key, new ResourceLocationBase(key, key, _provider.ProviderId, typeof(TextAsset), bundle));
            Addressables.AddResourceLocator(locator);
        }

        // DownloadDependenciesAsync resolves through this real locator before returning its official handle.
        private sealed class DownloadLocationMap : ResourceLocationMap, IResourceLocator
        {
            private Action _onLocate;
            internal DownloadLocationMap(string id, Action onLocate) : base(id) => _onLocate = onLocate;
            bool IResourceLocator.Locate(object key, Type type, out IList<IResourceLocation> locations)
            {
                var found = Locate(key, type, out locations);
                if (found && _onLocate != null)
                {
                    var action = _onLocate;
                    _onLocate = null;
                    action();
                }
                return found;
            }
        }

        [UnityTest]
        public IEnumerator DeliveryWorkIncludesSynchronousOfficialStart()
        {
            yield return Start();
            ResourceEventHistory.SetRecording(true);
            const string key = "updates/synchronous-start";
            long nestedRequest = 0;
            var interval = new System.Diagnostics.Stopwatch();
            using var scope = _startup.Resources.CreateScope("timing");
            AddGatedDownload(key, () =>
            {
                interval.Start();
                using var marker = scope.LoadAsync<TextAsset>(string.Empty);
                nestedRequest = marker.EventTrace.Id;
                Thread.Sleep(20);
                interval.Stop();
            });
            var request = Own(_startup.Resources.DownloadDependenciesAsync(new[] { key }));
            yield return Until(() => _provider.DownloadPending);
            _provider.CompleteDownload();
            yield return Wait(request);
            Assert.That(request.TakeResult(), Is.True);
            var work = ResourceEventHistory.Read().Single(row => row.RequestId == request.EventTrace.Id && row.Kind == ResourceEventKind.Work);
            Assert.That(nestedRequest, Is.GreaterThan(0));
            Assert.That(work.WorkId, Is.LessThan(nestedRequest), "The work clock must start before the synchronous locator call.");
            Assert.That(work.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(interval.Elapsed.TotalMilliseconds));
        }

        [UnityTest]
        public IEnumerator CanceledDownloadReleaseFailureReachesStopWithoutHistory() => CanceledDownloadReleaseFailure(false);

        [UnityTest]
        public IEnumerator CanceledDownloadReleaseFailureReachesStopWithHistory() => CanceledDownloadReleaseFailure(true);

        private IEnumerator CanceledDownloadReleaseFailure(bool recording)
        {
            yield return Start();
            ResourceEventHistory.SetRecording(recording);
            const string key = "updates/download-fault";
            AddGatedDownload(key);
            var download = Own(_startup.Resources.DownloadDependenciesAsync(new[] { key }));
            yield return Until(() => _provider.DownloadPending);
            download.Cancel();
            const string expectedStopFailure = "download-final-pin-release";
            _provider.DownloadReleaseFault = new InvalidOperationException(expectedStopFailure);
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex(expectedStopFailure));
            _provider.CompleteDownload();
            using var stop = _startup.StopAsync();
            yield return Wait(stop);
            // This test observes the final shutdown itself, including its expected permanent cleanup error.
            _startup = null;
            Assert.That(download.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(stop.Error?.ToString(), Does.Contain(expectedStopFailure));
            Assert.That(_provider.DownloadReleases, Is.EqualTo(1));
            if (recording)
            {
                var release = ResourceEventHistory.Read().Single(row => row.RequestId == download.EventTrace.Id && row.Kind == ResourceEventKind.Release);
                Assert.That(release.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(release.Late, Is.True);
            }
            else
                Assert.That(ResourceEventHistory.Read(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator CatalogSnapshotAndReleaseFailuresSurviveWithoutHistory() => CatalogSnapshotAndReleaseFailures(false);

        [UnityTest]
        public IEnumerator CatalogSnapshotAndReleaseFailuresSurviveWithHistory() => CatalogSnapshotAndReleaseFailures(true);

        [UnityTest]
        public IEnumerator CatalogReleaseCancelsFailedSnapshotAndStopReceivesBothCauses()
        {
            yield return Start(true);
            ResourceEventHistory.SetRecording(false);
            AddControlledCatalog("updates/cancel-in-release");
            var system = _startup.Resources;
            var check = Own(system.CheckForCatalogUpdatesAsync());
            yield return Until(() => _provider.Pending != 0);
            const string releaseMessage = "reentrant-final-pin-release";
            _provider.BeforeHashRelease = check.Cancel;
            _provider.HashReleaseFault = new InvalidOperationException(releaseMessage);
            LogAssert.Expect(LogType.Exception, "ResourceException: Addressables could not read the remote catalog hash.");
            _provider.CompleteHashes(" ");
            yield return Wait(check);
            using var stop = _startup.StopAsync();
            yield return Wait(stop);
            _startup = null;
            Assert.That(check.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(_provider.Releases, Is.EqualTo(1));
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(stop.Error.ToString(), Does.Contain("could not read the remote catalog hash"));
            Assert.That(stop.Error.ToString(), Does.Contain(releaseMessage));
        }

        private IEnumerator CatalogSnapshotAndReleaseFailures(bool recording)
        {
            yield return Start(true);
            ResourceEventHistory.SetRecording(recording);
            AddControlledCatalog("updates/double-fault");
            var system = _startup.Resources;
            var check = Own(system.CheckForCatalogUpdatesAsync());
            yield return Until(() => _provider.Pending != 0);
            const string unknown = "updates/unknown";
            Assert.That(Addressables.GetLocatorInfo(unknown), Is.Null);
            var next = Own(system.ApplyCatalogUpdatesAsync(new[] { unknown }));
            Assert.That(next.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            _provider.HashReleaseFault = new InvalidOperationException("catalog-final-pin-release");
            _provider.CompleteHashes(" ");
            yield return Wait(check);
            Assert.That(check.Error?.ToString(), Does.Contain("could not read the remote catalog hash"));
            Assert.That(check.Error?.ToString(), Does.Contain("catalog-final-pin-release"));
            yield return Wait(next);
            Assert.That(next.Status, Is.EqualTo(ResourceRequestStatus.Failed),
                $"Queued Apply target={unknown}, progress={next.Progress}, work={system.GetDiagnostics().PendingWorkCount}, " +
                $"provides={_provider.Provides}, releases={_provider.Releases}, catalogs={string.Join(", ", _provider.CatalogsProvided)}");
            Assert.That(next.Error?.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            Assert.That(system.IsAvailable, Is.True);
            Assert.That(_provider.Releases, Is.EqualTo(1));
            if (recording)
            {
                var rows = ResourceEventHistory.Read().Where(row => row.RequestId == check.EventTrace.Id).ToArray();
                Assert.That(rows.Single(row => row.Kind == ResourceEventKind.Work).Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
                Assert.That(rows.Single(row => row.Kind == ResourceEventKind.Release).Status, Is.EqualTo(ResourceRequestStatus.Failed));
            }
            else
                Assert.That(ResourceEventHistory.Read(), Is.Empty);
        }

        private static void AssertLateDelivery(long requestId, ResourceEventSource source)
        {
            var rows = ResourceEventHistory.Read().Where(row => row.RequestId == requestId).ToArray();
            Assert.That(rows.Select(row => row.Kind), Is.EqualTo(new[] {
                ResourceEventKind.Terminal, ResourceEventKind.Work, ResourceEventKind.Release }));
            Assert.That(rows.All(row => row.Source == source), Is.True);
            Assert.That(rows[0].Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(rows[1].WorkId, Is.GreaterThan(0));
            Assert.That(rows[2].WorkId, Is.EqualTo(rows[1].WorkId));
            Assert.That(rows.Skip(1).All(row => row.Phase == ResourceEventPhase.Delivery && row.Late &&
                row.Status == ResourceRequestStatus.Succeeded), Is.True);
        }

        [UnityTest]
        public IEnumerator CheckResult_IsAReadOnlySnapshot_AndReentrantCheckWaitsForRelease()
        {
            yield return Start();
            AddControlledCatalog("updates/one");
            var first = Own(_startup.Resources.CheckForCatalogUpdatesAsync());
            ResourceRequest<IReadOnlyList<string>> second = null;
            first.Completed += _ => second = Own(_startup.Resources.CheckForCatalogUpdatesAsync());
            yield return Until(() => _provider.Pending != 0);
            _provider.CompleteHashes();
            yield return Wait(first);
            var snapshot = first.TakeResult();
            Assert.That(snapshot, Is.EqualTo(new[] { "updates/one" }));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot).Add("cannot-mutate"));
            Assert.That(second, Is.Not.Null);
            yield return Until(() => _provider.Provides == 2);
            Assert.That(_provider.Releases, Is.EqualTo(1), "The next check must not reuse the completed first operation or its hash dependency.");
            _provider.CompleteHashes();
            yield return Wait(second);
            Assert.That(second.TakeResult(), Is.EqualTo(snapshot));
            Assert.That(snapshot, Is.EqualTo(new[] { "updates/one" }));
        }

        [UnityTest]
        public IEnumerator CancelRunningCheck_OnlyExitsWaiting_QueuedCheckCannotOverlap()
        {
            yield return Start();
            ResourceEventHistory.SetRecording(true);
            AddControlledCatalog("updates/cancel");
            using var token = new CancellationTokenSource();
            var first = Own(_startup.Resources.CheckForCatalogUpdatesAsync(token.Token));
            yield return Until(() => _provider.Pending == 1);
            var second = Own(_startup.Resources.CheckForCatalogUpdatesAsync());
            token.Cancel();
            yield return Until(() => first.IsDone);
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            for (var i = 0; i < 3; i++) yield return null;
            Assert.That(_provider.Provides, Is.EqualTo(1));
            Assert.That(second.IsDone, Is.False);
            _provider.CompleteHashes();
            yield return Until(() => _provider.Provides == 2);
            Assert.That(_provider.Releases, Is.EqualTo(1));
            AssertLateDelivery(first.EventTrace.Id, ResourceEventSource.CatalogCheck);
            _provider.CompleteHashes();
            yield return Wait(second);
            Assert.That(second.TakeResult(), Is.EqualTo(new[] { "updates/cancel" }));
        }

        [UnityTest]
        public IEnumerator StopDuringCanceledCheck_WaitsForOfficialDrainAndDeferredGate()
        {
            yield return Start();
            AddControlledCatalog("updates/stop");
            var check = Own(_startup.Resources.CheckForCatalogUpdatesAsync());
            yield return Until(() => _provider.Pending == 1);
            check.Cancel();
            using var stop = _startup.StopAsync();
            yield return null;
            Assert.That(stop.IsDone, Is.False, "Cancel is not an abort of the official operation.");
            _provider.CompleteHashes();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
            Assert.That(_provider.Releases, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ApplyUsesOnlyExplicitIds_AndDoesNotClearOtherLocators()
        {
            yield return Start(true);
            AddControlledCatalog("updates/chosen");
            AddControlledCatalog("updates/untouched");
            var untouched = Addressables.ResourceLocators.Single(locator => locator.LocatorId == "updates/untouched");
            _provider.AutomaticHashes = true;
            using var apply = _startup.Resources.ApplyCatalogUpdatesAsync(new[] { "updates/chosen" });
            yield return Wait(apply);
            Assert.That(apply.TakeResult(), Is.True);
            Assert.That(_provider.CatalogsProvided, Is.EqualTo(new[] { "updates/chosen" }));
            Assert.That(Addressables.ResourceLocators.Any(locator => ReferenceEquals(locator, untouched)), Is.True);
        }

        [UnityTest]
        public IEnumerator CheckWithMissingPackedCatalogHash_FailsInsteadOfReportingNoUpdate()
        {
            yield return Start();
            var catalog = AddPackedCatalog();
            using var unchanged = _startup.Resources.CheckForCatalogUpdatesAsync();
            yield return Wait(unchanged);
            Assert.That(unchanged.TakeResult(), Is.Empty, "A readable equal hash is a valid no-update check.");

            File.Delete(catalog.HashLocation.InternalId);
            using var missing = _startup.Resources.CheckForCatalogUpdatesAsync();
            yield return Wait(missing);
            Assert.That(missing.Status, Is.EqualTo(ResourceRequestStatus.Failed),
                "The missing .hash must not look like a successful same-version check.");
            Assert.That(missing.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
        }

        [UnityTest]
        public IEnumerator ApplyAfterPublishedHashDisappears_RejectsPackedFallbackForExplicitId()
        {
            yield return Start(true);
            var catalog = AddPackedCatalog();
            var before = catalog.Locator;
            var remoteHashPath = catalog.HashLocation.InternalId;
            File.WriteAllText(remoteHashPath, PublishedCatalogHash);
            using var check = _startup.Resources.CheckForCatalogUpdatesAsync();
            yield return Wait(check);
            Assert.That(check.TakeResult(), Is.EqualTo(new[] { PackedCatalogId }));

            File.Delete(remoteHashPath);
            using var apply = _startup.Resources.ApplyCatalogUpdatesAsync(new[] { PackedCatalogId });
            yield return Wait(apply);
            Assert.That(catalog.Locator, Is.Not.SameAs(before), "Unity installed its fallback locator; YL does not roll it back.");
            Assert.That(catalog.Locator.Locate("ownership/message", typeof(TextAsset), out _), Is.True);
            Assert.That(catalog.LocalHash, Is.EqualTo(PackedCatalogHash));
            Assert.That(apply.Status, Is.EqualTo(ResourceRequestStatus.Failed),
                "Unity completed with the old packed catalog, not the advertised patch.");
            Assert.That(apply.Error.Kind, Is.EqualTo(ResourceError.LoadFailed));
        }

        [UnityTest]
        public IEnumerator ApplyWithReadableEqualHash_ReturnsNoUpdate()
        {
            yield return Start(true);
            var catalog = AddPackedCatalog();
            using var apply = _startup.Resources.ApplyCatalogUpdatesAsync(new[] { PackedCatalogId });
            yield return Wait(apply);
            Assert.That(apply.TakeResult(), Is.False, "Reopening the same cached version is not a catalog update.");
            Assert.That(catalog.LocalHash, Is.EqualTo(PackedCatalogHash));
        }

        [UnityTest]
        public IEnumerator ApplyWithChangedHash_InstallsPublishedCatalogForExplicitId()
        {
            yield return Start(true);
            var catalog = AddPackedCatalog();
            const string PublishedKey = "updates/published-entry";
            Assert.That(catalog.Locator.Locate(PublishedKey, typeof(string), out _), Is.False);
            var remoteHashPath = catalog.HashLocation.InternalId;
            var remoteCatalogPath = Path.ChangeExtension(remoteHashPath, Path.GetExtension(catalog.CatalogLocation.InternalId));
            var data = new ContentCatalogData(new[]
            {
                new ContentCatalogDataEntry(typeof(string), remoteHashPath, typeof(TextDataProvider).FullName,
                    new object[] { PublishedKey })
            }, PackedCatalogId) { BuildResultHash = PublishedCatalogHash };
            if (Path.GetExtension(remoteCatalogPath) == ".bin")
            {
                // The public serializer exists only in binary-catalog builds, which use different symbols in 1.25 and 2.10.
                var serialize = typeof(ContentCatalogData).GetMethod("SerializeToByteArray", Type.EmptyTypes);
                Assert.That(serialize, Is.Not.Null);
                File.WriteAllBytes(remoteCatalogPath, (byte[])serialize.Invoke(data, null));
            }
            else
            {
                File.WriteAllText(remoteCatalogPath, JsonUtility.ToJson(data));
            }
            File.WriteAllText(remoteHashPath, PublishedCatalogHash);

            using var apply = _startup.Resources.ApplyCatalogUpdatesAsync(new[] { PackedCatalogId });
            yield return Wait(apply);
            Assert.That(apply.TakeResult(), Is.True);
            Assert.That(catalog.LocalHash, Is.EqualTo(PublishedCatalogHash));
            Assert.That(catalog.Locator.Locate(PublishedKey, typeof(string), out _), Is.True);
        }

        [UnityTest]
        public IEnumerator CanceledApplyStillRefusesBusinessUntilItsOfficialOperationSettles()
        {
            yield return Start(true);
            ResourceEventHistory.SetRecording(true);
            AddControlledCatalog("updates/delayed-apply");
            var system = _startup.Resources;
            var apply = Own(system.ApplyCatalogUpdatesAsync(new[] { "updates/delayed-apply" }));
            yield return Until(() => _provider.Pending != 0);
            Assert.That(apply.IsDone, Is.False);
            var active = system.GetDiagnostics();
            using var before = system.AcquireAsync<TextAsset>("ownership/message");
            Assert.That(before.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(before.Error.Kind, Is.EqualTo(ResourceError.Busy));
            before.Dispose();
            var rejected = ResourceEventHistory.Read().Single(row => row.RequestId == before.EventTrace.Id);
            Assert.That(rejected.Source, Is.EqualTo(ResourceEventSource.Acquire));
            Assert.That(rejected.Kind, Is.EqualTo(ResourceEventKind.Terminal));
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rejected.Error, Is.EqualTo(ResourceError.Busy));
            Assert.That(rejected.WorkId, Is.Zero);
            using var rejectedCheck = system.CheckForCatalogUpdatesAsync();
            Assert.That(rejectedCheck.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(ResourceEventHistory.Read().Single(row => row.RequestId == rejectedCheck.EventTrace.Id).Kind,
                Is.EqualTo(ResourceEventKind.Terminal));
            var stillActive = system.GetDiagnostics();
            Assert.That(stillActive.RequestCount, Is.EqualTo(active.RequestCount));
            Assert.That(stillActive.PendingWorkCount, Is.EqualTo(active.PendingWorkCount));
            apply.Cancel();
            Assert.That(apply.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            using var afterCancel = system.AcquireAsync<TextAsset>("ownership/message");
            Assert.That(afterCancel.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(afterCancel.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(system.IsAvailable, Is.False);
            _provider.CompleteHashes();
            yield return Until(() => system.IsAvailable && system.GetDiagnostics().PendingWorkCount == 0);
            Assert.That(_provider.CatalogsProvided, Is.EqualTo(new[] { "updates/delayed-apply" }));
            AssertLateDelivery(apply.EventTrace.Id, ResourceEventSource.CatalogApply);
            using var accepted = system.AcquireAsync<TextAsset>("ownership/message");
            yield return Wait(accepted);
            using var lease = accepted.TakeResult();
            Assert.That(lease.Asset.text, Is.EqualTo("official addressables content"));
            using var tooLate = system.ApplyCatalogUpdatesAsync(new[] { "updates/delayed-apply" });
            Assert.That(tooLate.Error.Kind, Is.EqualTo(ResourceError.Busy));
        }

        [UnityTest]
        public IEnumerator CancelAfterOfficialCompletionKeepsObservedCatalogWorkOnTime()
        {
            yield return Start(true);
            const string id = "updates/completed-before-cancel";
            ResourceEventHistory.SetRecording(true);
            AddControlledCatalog(id);
            var system = _startup.Resources;
            var apply = Own(system.ApplyCatalogUpdatesAsync(new[] { id }));
            yield return Until(() => _provider.Pending != 0);
            // The official active UpdateCatalogs alias reports completion after YL's already registered callback.
            var active = Addressables.UpdateCatalogs(new[] { id }, false);
            bool canceledOnCompletion = false;
            active.Completed += _ =>
            {
                canceledOnCompletion = !apply.IsDone;
                apply.Cancel();
            };
            _provider.CompleteHashes();
            yield return Until(() => system.IsAvailable && system.GetDiagnostics().PendingWorkCount == 0);
            Assert.That(canceledOnCompletion, Is.True);
            Assert.That(apply.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(_provider.CatalogsProvided, Is.EqualTo(new[] { id }));
            var rows = ResourceEventHistory.Read().Where(row => row.RequestId == apply.EventTrace.Id).ToArray();
            Assert.That(rows.Select(row => row.Kind), Is.EqualTo(new[] {
                ResourceEventKind.Work, ResourceEventKind.Terminal, ResourceEventKind.Release }));
            Assert.That(rows[0].Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(rows[0].Late, Is.False);
            Assert.That(rows[2].Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(rows[2].WorkId, Is.EqualTo(rows[0].WorkId));
        }

        [UnityTest]
        public IEnumerator QueuedApply_RechecksBusinessWindowAtExecution()
        {
            yield return Start(true);
            AddControlledCatalog("updates/queued");
            var check = Own(_startup.Resources.CheckForCatalogUpdatesAsync());
            yield return Until(() => _provider.Pending == 1);
            using var apply = _startup.Resources.ApplyCatalogUpdatesAsync(new[] { "updates/queued" });
            using var load = _startup.Resources.AcquireAsync<TextAsset>("ownership/message");
            yield return Wait(load);
            Own(load.TakeResult());
            _provider.CompleteHashes();
            yield return Wait(check);
            yield return Wait(apply);
            Assert.That(apply.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(_provider.CatalogsProvided, Is.Empty);
        }

        [UnityTest]
        public IEnumerator StopStart_DoesNotReopenBusinessWindow()
        {
            yield return Start(true);
            using var load = _startup.Resources.AcquireAsync<TextAsset>("ownership/message");
            yield return Wait(load);
            Assert.That(load.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), load.Error?.ToString());
            using var lease = load.TakeResult();
            Assert.That(lease, Is.Not.Null);
            lease.Dispose();
            using var stop = _startup.StopAsync();
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
            yield return Start();
            using var rejected = _startup.Resources.ApplyCatalogUpdatesAsync(new[] { "not-a-new-startup-window" });
            yield return Wait(rejected);
            Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.Busy));
        }

        [UnityTest]
        public IEnumerator EmptyAndNullKeysOrCatalogIds_NeverMeanAllCatalogs()
        {
            yield return Start(true);
            var locators = Addressables.ResourceLocators.ToArray();
            var initial = _startup.Resources.GetDiagnostics();
            ResourceEventHistory.SetRecording(true);
            foreach (var input in new IEnumerable<string>[] { null, Array.Empty<string>(), new[] { "" }, new[] { " " } })
            {
                using var size = _startup.Resources.GetDownloadSizeAsync(input);
                using var download = _startup.Resources.DownloadDependenciesAsync(input);
                using var apply = _startup.Resources.ApplyCatalogUpdatesAsync(input);
                var snapshot = _startup.Resources.GetDiagnostics();
                Assert.That(new[] { size.DiagnosticId, download.DiagnosticId, apply.DiagnosticId }, Is.All.EqualTo(0));
                Assert.That(snapshot.RequestCount, Is.EqualTo(initial.RequestCount));
                Assert.That(snapshot.PendingWorkCount, Is.EqualTo(initial.PendingWorkCount));
                yield return Wait(size);
                yield return Wait(download);
                yield return Wait(apply);
                Assert.That(size.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(download.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(apply.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            }
            var rows = ResourceEventHistory.Read();
            Assert.That(rows.Length, Is.EqualTo(12));
            Assert.That(rows.All(row => row.Kind == ResourceEventKind.Terminal && row.Error == ResourceError.InvalidAddress), Is.True);
            CollectionAssert.AreEqual(locators, Addressables.ResourceLocators.ToArray());
        }

        [UnityTest]
        public IEnumerator StoppingInsideKeyEnumerationInvalidatesTheUnregisteredRequestEvenIfEnumerationThrows()
        {
            yield return Start(true);
            ResourceEventHistory.SetRecording(true);
            var system = _startup.Resources;
            int baseline = system.GetDiagnostics().RequestCount;
            int observed = -1;
            ResourceRequest<bool> stop = null;

            IEnumerable<string> Keys()
            {
                yield return "updates/first";
                observed = system.GetDiagnostics().RequestCount;
                stop = Own(_startup.StopAsync());
                throw new InvalidOperationException("Iterator failed after stopping its service.");
            }

            using var size = system.GetDownloadSizeAsync(Keys());
            Assert.That(observed, Is.EqualTo(baseline));
            Assert.That(size.DiagnosticId, Is.Zero);
            Assert.That(size.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(size.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(size.IsDisposed, Is.True);
            var row = ResourceEventHistory.Read().Single();
            Assert.That(row.Source, Is.EqualTo(ResourceEventSource.DownloadSize));
            Assert.That(row.Kind, Is.EqualTo(ResourceEventKind.Terminal));
            Assert.That(row.Status, Is.EqualTo(size.Status));
            Assert.That(row.Error, Is.EqualTo(size.Error.Kind));
            yield return Wait(stop);
            Assert.That(stop.TakeResult(), Is.True);
        }

        private ResourceLocatorInfo AddPackedCatalog()
        {
            _packedCatalogDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp",
                "yl-packed-catalog-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_packedCatalogDirectory);
            var builderInput = new AddressablesDataBuilderInput(AddressableAssetSettingsDefaultObject.Settings);
            var packedPath = Path.Combine(Addressables.RuntimePath, builderInput.RuntimeCatalogFilename);
            var extension = Path.GetExtension(packedPath);
            var hashes = Enum.GetNames(typeof(ContentCatalogProvider.DependencyHashIndex))
                .Take((int)ContentCatalogProvider.DependencyHashIndex.Count).Select(name =>
                {
                    var hashPath = Path.Combine(_packedCatalogDirectory, name + ".hash");
                    File.WriteAllText(hashPath, PackedCatalogHash);
                    if (name != nameof(ContentCatalogProvider.DependencyHashIndex.Remote))
                        File.Copy(packedPath, Path.ChangeExtension(hashPath, extension));
                    return (IResourceLocation)new ResourceLocationBase(hashPath, hashPath,
                        typeof(TextDataProvider).FullName, typeof(string))
                    {
                        Data = new ProviderLoadRequestOptions { IgnoreFailures = true }
                    };
                }).ToArray();
            var localPath = Path.Combine(_packedCatalogDirectory, "packed" + extension);
            File.Copy(packedPath, localPath);
            var location = new ResourceLocationBase(PackedCatalogId, localPath,
                typeof(ContentCatalogProvider).FullName, typeof(IResourceLocator), hashes);
            var locator = new ResourceLocationMap(PackedCatalogId);
            _locators.Add(locator);
            Addressables.AddResourceLocator(locator, PackedCatalogHash, location);
            var catalog = Addressables.GetLocatorInfo(PackedCatalogId);
            Assert.That(catalog.CanUpdateContent, Is.True);
            return catalog;
        }

        private void AddControlledCatalog(string id)
        {
            if (_provider == null)
            {
                _provider = new HashProvider();
                Addressables.ResourceManager.ResourceProviders.Add(_provider);
            }
            var hashes = Enumerable.Range(0, (int)ContentCatalogProvider.DependencyHashIndex.Count)
                .Select(index => (IResourceLocation)new ResourceLocationBase(id + "/hash/" + index,
                    id + "/hash/" + index, _provider.ProviderId, typeof(string))).ToArray();
            var location = new ResourceLocationBase(id, id, _provider.ProviderId, typeof(IResourceLocator), hashes);
            var locator = new ResourceLocationMap(id);
            _locators.Add(locator);
            Addressables.AddResourceLocator(locator, "old", location);
            Assert.That(Addressables.GetLocatorInfo(id).CanUpdateContent, Is.True,
                "The test catalog must satisfy this Addressables version's official dependency contract.");
        }

        private sealed class HashProvider : ResourceProviderBase, IAssetBundleResource
        {
            private ProvideHandle? _download;
            internal bool DownloadPending => _download.HasValue;
            internal int DownloadReleases;
            internal Exception DownloadReleaseFault;
            internal Exception HashReleaseFault;
            internal Action BeforeHashRelease;
            public AssetBundle GetAssetBundle() => null;

            internal void CompleteDownload()
            {
                if (!_download.HasValue)
                    return;
                var handle = _download.Value;
                _download = null;
                handle.Complete<IAssetBundleResource>(this, true, null);
            }

            private readonly Queue<ProvideHandle> _pending = new();
            internal int Provides;
            internal int Releases;
            internal bool AutomaticHashes;
            internal readonly List<string> CatalogsProvided = new();
            internal int Pending => _pending.Count;

            internal HashProvider() => m_ProviderId = "YL.UpdateTests.Hash." + Guid.NewGuid().ToString("N");
            public override bool CanProvide(Type type, IResourceLocation location) => true;
            public override Type GetDefaultType(IResourceLocation location) => location.ResourceType;
            public override void Provide(ProvideHandle handle)
            {
                if (handle.Type == typeof(IAssetBundleResource))
                    _download = handle;
                else if (handle.Type == typeof(string))
                {
                    Provides++;
                    if (AutomaticHashes) handle.Complete("new", true, null);
                    else _pending.Enqueue(handle);
                }
                else
                {
                    CatalogsProvided.Add(handle.Location.PrimaryKey);
                    handle.Complete<object>(new ResourceLocationMap(handle.Location.PrimaryKey), true, null);
                }
            }
            internal void CompleteHashes(string hash = "new")
            {
                while (_pending.Count != 0) _pending.Dequeue().Complete(hash, true, null);
            }
            public override void Release(IResourceLocation location, object asset)
            {
                if (asset is string)
                {
                    Releases++;
                    BeforeHashRelease?.Invoke();
                    if (HashReleaseFault != null)
                        throw HashReleaseFault;
                }
                if (ReferenceEquals(asset, this))
                {
                    DownloadReleases++;
                    if (DownloadReleaseFault != null)
                        throw DownloadReleaseFault;
                }
            }
        }

        private static IEnumerator Wait<T>(ResourceRequest<T> request) => Until(() => request.IsDone);
        private static IEnumerator Until(Func<bool> condition)
        {
            var end = Time.realtimeSinceStartupAsDouble + 30;
            while (!condition() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(condition(), Is.True, "The official operation did not settle within 30 seconds.");
        }
    }
}
#endif
