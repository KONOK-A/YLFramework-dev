using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource;
using YLFramework.Resource.Encryption;

namespace YLFramework.Testing
{
    public sealed partial class FrozenDeliveryProbe : MonoBehaviour
    {
        public const string ProtectedKey = "frozen/secret";
        public const string ProtectedTwinKey = "frozen/secret-twin";
        public const string OrdinaryKey = "frozen/plain";
        public const string LocalKey = "frozen/local";
        public const string NoHashBundle = "plainlocal_assets_all.bundle";
        public const string Mixed = "frozen/mixed";
        public const string FourGroupLabel = "frozen/four-groups";

        [SerializeField] private string _token;
        [SerializeField] private string _backend;
        [SerializeField] private string _inputHash;
        [SerializeField] private string _catalogName;
        [SerializeField] private bool _checkStockDefaults;
        public void Configure(string token, string backend, string inputHash, string catalogName, bool checkStockDefaults)
        {
            _token = token;
            _backend = backend;
            _inputHash = inputHash;
            _catalogName = catalogName;
            _checkStockDefaults = checkStockDefaults;
        }
        private string _output;
        private Row _row;

        [Serializable]
        private sealed class Row
        {
            public string token, scenario, status, error, rejection, unity, backend, architecture, graphics, catalogSha256, inputHash, descriptionSha256;
            public string retryAfterUtc;
            public int pid, bits, creates, unloads, streamsClosed, openKeys, issuedKeys, returnKeys, bundlesAfterStop, leasesAfterStop;
            public int[] nativeBeforeStop, nativeAfterStop;
            public PerformanceEvidence performance;
            public long coldBytes, mixedBytes, cacheBytes;
            public long heldTransferBytes, heldLoadBytes, completedTransferBytes, completedLoadBytes;
            public bool quit, sentinel, verifiedStartup, externalAfterStop, stableFile, exclusiveAfterUnload, remount, remountDuringUnload, noParts, predownloadOnly;
            public bool sawOwnedPart, rejectedBeforeCreate, invalidatedSize, refusedCloseWithExternalOwner;
            public bool rejectedReorderedCandidates, matchedFirstCandidate, rejectedAfterDrift, leftForeignRegistration, reopenedCacheLock;
            public bool observerStartedTransfer, loadedWhileTransferHeld, observerCanceledWhileTransferHeld, downloadReleasedBeforeAsset, keyLeasesReturned;
            public bool retriedWithPrefetchHeld, singleLoadAttempt, refusedCloseWithPrefetchOwner;
            public bool closeWaitedForUnload, sharedClose, reenteredCloseDuringKeyReturn;
            public bool stockDefaultsVerified, mixedPrefetchVerified;
            public bool localBeforeRemoteReady, remoteCacheWasBlocked, refusedUnpreparedRemote, installedPrefetchValidated, remotePrepared, readySnapshotZero;
            public bool localBeforePreparationReleased;
            public bool installedRootWriteDenied, installedContentWriteDenied;
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            _output = Argument("-ylFrozenResult");
            _row = new Row { token = _token, inputHash = _inputHash, scenario = Argument("-ylFrozenScenario"), unity = Application.unityVersion,
                graphics = SystemInfo.graphicsDeviceType.ToString(), bits = IntPtr.Size * 8,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() };
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) _row.pid = process.Id;
#if ENABLE_IL2CPP
            _row.backend = "IL2CPP";
#elif ENABLE_MONO
            _row.backend = "Mono";
#else
            _row.backend = "Unknown";
#endif
            var stack = new Stack<IEnumerator>();
            stack.Push(Run());
            while (stack.Count != 0)
            {
                object next;
                try
                {
                    if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                    next = stack.Peek().Current;
                }
                catch (Exception error)
                {
                    foreach (var iterator in stack) (iterator as IDisposable)?.Dispose();
                    Finish(error);
                    yield break;
                }
                if (next is IEnumerator nested) stack.Push(nested); else yield return next;
            }
            Finish(null);
        }

        private IEnumerator Run()
        {
            string scenario = _row.scenario;
            Require(!Application.isEditor && Application.isBatchMode && !Debug.isDebugBuild && Application.platform == RuntimePlatform.WindowsPlayer &&
                _row.backend == _backend && _row.architecture == "X64" && _row.bits == 64 && _row.graphics != "Null",
                "The fixture must use a native Win64 Release graphics Player.");
            string player = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (scenario.StartsWith("performance-", StringComparison.Ordinal))
            {
                yield return Performance(player);
                CaptureInstalledIdentity();
                yield break;
            }
            CaptureInstalledIdentity();
            if (scenario == "disabled") { yield return Disabled(); yield break; }
            if (scenario.StartsWith("layout-", StringComparison.Ordinal)) { yield return EmbeddedLayout(scenario); yield break; }
            if (scenario == "ordinary-dependency") { yield return OrdinaryDependency(); yield break; }
            if (scenario == "bad-dependency") { yield return BadDependency(); yield break; }
            if (scenario == "multi-group") { yield return MultiGroup(); yield break; }
            bool isolated = scenario == "no-cache-offline" || scenario == "held-download" || scenario == "held-load" || scenario == "tamper-prefetched";
            string cacheName = scenario == "fault-abort" || scenario == "fault-recovery" ? "Cache-abort" :
                isolated ? "Cache-" + scenario : "CipherCache";
            string cache = Path.Combine(player, cacheName);
            Action onKeyReturn = null;
            using var keys = new Keys(Argument("-ylFrozenKey"), scenario, () => onKeyReturn?.Invoke());
            if (scenario == "shadow")
            {
                var shadow = new ResourceLocationMap("unexpected test locator");
                shadow.Add(ProtectedKey, new ResourceLocationBase(ProtectedKey, "wrong-file", typeof(BundledAssetProvider).FullName, typeof(TextAsset)));
                Addressables.AddResourceLocator(shadow);
            }
            if (scenario == "provider-shadow") Addressables.ResourceManager.ResourceProviders.Insert(0, new UnexpectedAssetProvider());
            var gate = EncryptedDeliverySession.StartCoreAsync(cache, keys, allowLoopback: true);
            yield return Until(() => gate.IsCompleted);
            if (scenario == "shadow" || scenario == "wrong-route" || scenario == "provider-shadow")
            {
                string error = gate.Exception?.GetBaseException().Message;
                string message = scenario == "shadow" ? "before Addressables startup" : scenario == "provider-shadow" ? "provider already registered" : "independent authoring audit";
                Require(gate.IsFaulted && error != null && error.Contains(message) &&
                    !AssetBundle.GetAllLoadedAssetBundles().Any(), "Startup failed to reject an asset route before a business handle exists: " + error);
                _row.rejection = error;
                CheckKeyReturns(keys);
                yield break;
            }
            var session = gate.GetAwaiter().GetResult();
            _row.verifiedStartup = true;
            var desc = EncryptedDeliverySession.ReadShippedDescription(Addressables.RuntimePath, true, default);
            _row.catalogSha256 = desc.CatalogSha256;
            var record = desc.Bundles.Single();
            if (scenario == "head-injection")
            {
                using var head = UnityEngine.Networking.UnityWebRequest.Head(record.Binding.Url);
                yield return head.SendWebRequest();
                Require(head.result == UnityEngine.Networking.UnityWebRequest.Result.Success, "The HEAD counterexample did not reach the same endpoint.");
            }
            string final = Path.Combine(cache, record.CipherSha256 + ".bin");
            string ordinary = Path.Combine(player, "OfficialCache");
            Directory.CreateDirectory(ordinary);
            yield return Until(() => Caching.ready);
            var old = Caching.currentCacheForWriting;
            var own = Caching.AddCache(ordinary);
            Require(own.valid, "Ordinary Unity cache is invalid.");
            Caching.currentCacheForWriting = own;
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Until(() => starting.IsDone);
            var system = starting.TakeResult().Resources;

            var remoteLocations = Addressables.LoadResourceLocationsAsync(OrdinaryKey, typeof(TextAsset));
            yield return Until(() => remoteLocations.IsDone);
            var stock = (AssetBundleRequestOptions)remoteLocations.Result.Single().Dependencies.Single().Data;
            var officialVersion = new CachedAssetBundle(stock.BundleName, Hash128.Parse(stock.Hash));
            Addressables.Release(remoteLocations);
            long initial = scenario == "cold" || isolated ||
                scenario == "repair" || scenario == "fault-abort" || scenario == "fault-recovery" || scenario == "sync-wait" ? record.CipherLength : 0;
            yield return Sizes(system, new[] { ProtectedKey, ProtectedTwinKey }, initial, false);
            yield return Sizes(system, new[] { Mixed, ProtectedKey }, initial + (Caching.IsVersionCached(officialVersion) ? 0 : stock.BundleSize), true);

            if (scenario == "fault-abort")
            {
                var transfer = Addressables.DownloadDependenciesAsync(ProtectedKey, false);
                yield return Until(() => Directory.EnumerateFiles(cache, "*.part").Any());
                _row.sawOwnedPart = !transfer.IsDone && !File.Exists(final) && session.Provider.Creates == 0 && keys.Opens == 0 &&
                    Directory.EnumerateFiles(cache, "*.part").Count() == 1;
                Require(_row.sawOwnedPart, "Crash was not during an owned ciphertext attempt.");
                _row.status = "BeforeUnexpectedExit";
                Write(_output);
                TerminateProcess(GetCurrentProcess(), 73);
                throw new InvalidOperationException("Test-only self-termination did not exit.");
            }
            if (scenario == "sync-wait")
            {
                var asynchronous = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
                try { asynchronous.WaitForCompletion(); }
                catch (NotSupportedException error) { _row.rejection = error.Message; }
                Require(_row.rejection != null && _row.rejection.Contains("asynchronous"), "An in-flight protected load must reject a synchronous wait immediately.");
                yield return Until(() => asynchronous.IsDone);
                CheckAsset(asynchronous);
                Addressables.Release(asynchronous);
            }
            else if (scenario == "tamper" || scenario == "tamper-prefetched")
            {
                var prefetch = default(AsyncOperationHandle);
                try
                {
                    if (scenario == "tamper-prefetched")
                    {
                        prefetch = Addressables.DownloadDependenciesAsync(ProtectedKey, false);
                        yield return Until(() => prefetch.IsDone);
                        Require(prefetch.Status == AsyncOperationStatus.Succeeded && keys.Opens == 0 && session.Provider.Creates == 0,
                            "The held download must have committed ciphertext without opening a key or a Bundle.");
                        yield return Sizes(system, new[] { ProtectedKey }, 0, false);
                    }
                    using (var data = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Require(data.Length == record.CipherLength, "The tamper control requires the exact frozen ciphertext length.");
                        byte original = checked((byte)data.ReadByte());
                        data.Position = 0;
                        data.WriteByte((byte)(original ^ 1));
                    }
                    var invalid = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
                    try
                    {
                        yield return Until(() => invalid.IsDone);
                        var error = invalid.OperationException?.ToString();
                        _row.rejectedBeforeCreate = invalid.Status == AsyncOperationStatus.Failed && !invalid.Result &&
                            error != null && error.Contains("EncryptedBundleIntegrityException") && session.Provider.Creates == 0 &&
                            keys.Opens == 0 && system.GetDiagnostics().LeaseCount == 0;
                    }
                    finally { Addressables.Release(invalid); }
                    Require(_row.rejectedBeforeCreate && !File.Exists(final) && !Directory.EnumerateFiles(cache, "*.part").Any(),
                        "Same-length replacement was not retired before native creation, or the failed request started a replacement transfer.");
                    yield return Sizes(system, new[] { ProtectedKey }, record.CipherLength, false);
                    _row.invalidatedSize = true;
                    if (prefetch.IsValid())
                    {
                        Require(prefetch.Status == AsyncOperationStatus.Succeeded, "The older download handle was not retained across tamper.");
                        _row.retryAfterUtc = DateTime.UtcNow.ToString("o");
                        yield return GoodOfficial(ProtectedKey);
                        _row.retriedWithPrefetchHeld = prefetch.IsValid() && prefetch.Status == AsyncOperationStatus.Succeeded &&
                            session.Provider.Creates == 1 && keys.Issued == 1 && File.Exists(final);
                        Require(_row.retriedWithPrefetchHeld, "A new official load could not download and mount while the old prefetch was still held.");
                        yield return Sizes(system, new[] { ProtectedKey }, 0, false);
                    }
                }
                finally { if (prefetch.IsValid()) Addressables.Release(prefetch); }
            }
            else if (scenario == "fault-recovery" || scenario == "repair")
            {
                Require(!Directory.EnumerateFiles(cache, "*.part").Any(), "Owned abandoned attempt was not removed on restart.");
                yield return GoodOfficial(ProtectedKey);
            }
            else if (scenario == "cold")
            {
                if (_checkStockDefaults)
                {
                    var local = Addressables.LoadAssetAsync<TextAsset>(LocalKey);
                    try
                    {
                        yield return Until(() => local.IsDone);
                        Require(local.Status == AsyncOperationStatus.Succeeded && local.Result && local.Result.text == "local:" + _token,
                            "The official local Bundle could not load alongside encryption: " + local.OperationException);
                    }
                    finally { Addressables.Release(local); }
                    if (_catalogName == "catalog.json")
                    {
                        var catalog = Addressables.ResourceLocators.Single(locator => locator.LocatorId == "AddressablesMainContentCatalog");
                        Require(catalog.Locate(0, typeof(SceneInstance), out var byIndex) && byIndex.Count == 1 &&
                            catalog.Locate("Boot", typeof(SceneInstance), out var byName) && byName.Count == 1 &&
                            ReferenceEquals(byIndex[0], byName[0]) && byIndex[0].ProviderId == "" && !byIndex[0].HasDependencies &&
                            byIndex[0].InternalId == "FrozenData/Boot" && UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(0) == "Assets/FrozenData/Boot.unity",
                            "The official built-in scene must retain both its numeric and named aliases in the shipped Catalog.");
                    }
                    else
                    {
                        var locations = Addressables.LoadResourceLocationsAsync(LocalKey, typeof(TextAsset));
                        try
                        {
                            yield return Until(() => locations.IsDone);
                            Require(locations.Status == AsyncOperationStatus.Succeeded && locations.Result.Single().Dependencies.Any(dep =>
                                dep.ProviderId == typeof(AssetBundleProvider).FullName && dep.ResourceType == typeof(IAssetBundleResource)),
                                "The fixture must have an independent stock local Bundle.");
                        }
                        finally { Addressables.Release(locations); }
                    }
                    _row.stockDefaultsVerified = true;
                }
                var predownload = _checkStockDefaults
                    ? Addressables.DownloadDependenciesAsync((IEnumerable)new[] { LocalKey, ProtectedKey }, Addressables.MergeMode.Union, false)
                    : Addressables.DownloadDependenciesAsync(ProtectedKey, false);
                try
                {
                    yield return Until(() => predownload.IsDone);
                    Require(predownload.Status == AsyncOperationStatus.Succeeded && !AssetBundle.GetAllLoadedAssetBundles().Any() && session.Provider.Creates == 0 && keys.Opens == 0 &&
                        system.GetDiagnostics().LeaseCount == 0, "Local and protected prefetch did not complete without a mount or key: " + predownload.OperationException);
                    _row.predownloadOnly = true;
                    _row.completedTransferBytes = CompletedDownloadBytes(predownload, record.CipherLength);
                    if (_checkStockDefaults)
                    {
                        using var mixed = system.DownloadDependenciesAsync(new[] { Mixed, ProtectedKey });
                        yield return Until(() => mixed.IsDone);
                        Require(mixed.TakeResult() && Caching.IsVersionCached(officialVersion) && !AssetBundle.GetAllLoadedAssetBundles().Any() &&
                            session.Provider.Creates == 0 && keys.Opens == 0,
                            "The YL mixed-label prefetch did not preserve the stock remote cache and ciphertext-only transfer.");
                        _row.mixedPrefetchVerified = true;
                    }
                    try { session.CloseAsync(); }
                    catch (InvalidOperationException) { _row.refusedCloseWithPrefetchOwner = true; }
                    Require(_row.refusedCloseWithPrefetchOwner, "Close accepted an outstanding official predownload owner.");
                    yield return GoodOfficial(ProtectedKey);
                }
                finally { Addressables.Release(predownload); }
            }
            else if (scenario == "held-download")
            {
                using var cancellation = new System.Threading.CancellationTokenSource();
                using var observer = system.DownloadDependenciesAsync(new[] { ProtectedKey }, cancellation.Token);
                string held = Path.Combine(player, "HeldGet");
                var transfer = default(AsyncOperationHandle);
                var load = default(AsyncOperationHandle<TextAsset>);
                try
                {
                    // Attribute the gated GET to the YL observer before starting either official consumer.
                    yield return Until(() => File.Exists(Path.Combine(held, "in-flight")));
                    _row.observerStartedTransfer = !observer.IsDone && !File.Exists(final) && session.Provider.Pending == 1 &&
                        session.Provider.Creates == 0 && keys.Opens == 0;
                    Require(_row.observerStartedTransfer, "The YL observer must start the protected transfer before key use or mount.");
                    transfer = Addressables.DownloadDependenciesAsync(ProtectedKey, false);
                    Require(transfer.IsValid() && !transfer.IsDone, "The official download handle did not join the held transfer.");
                    int pendingDownloads = session.Provider.Pending;
                    load = Addressables.LoadAssetAsync<TextAsset>(ProtectedTwinKey);
                    yield return Until(() => session.Provider.Pending > pendingDownloads || load.IsDone);
                    _row.loadedWhileTransferHeld = load.IsValid() && !load.IsDone && !transfer.IsDone && session.Provider.Creates == 0;
                    Require(_row.loadedWhileTransferHeld, "The real load did not enter the provider while the response was held.");
                    cancellation.Cancel();
                    yield return Until(() => observer.IsDone);
                    _row.observerCanceledWhileTransferHeld = observer.Status == ResourceRequestStatus.Canceled && !load.IsDone && !transfer.IsDone;
                    Require(_row.observerCanceledWhileTransferHeld, "Only the canceled observer may finish before releasing the protected response.");
                    yield return PartialDownload(transfer, record.CipherLength);
                    yield return PartialDownload(load, record.CipherLength);
                    _row.heldTransferBytes = transfer.GetDownloadStatus().DownloadedBytes;
                    _row.heldLoadBytes = load.GetDownloadStatus().DownloadedBytes;
                    using (File.Open(Path.Combine(held, "release"), FileMode.CreateNew)) { }
                    yield return Until(() => transfer.IsDone && load.IsDone);
                    Require(transfer.IsValid() && transfer.Status == AsyncOperationStatus.Succeeded, "A held download handle was invalidated.");
                    CheckAsset(load);
                    _row.completedTransferBytes = CompletedDownloadBytes(transfer, record.CipherLength);
                    _row.completedLoadBytes = CompletedDownloadBytes(load, record.CipherLength);
                    var mounted = LoadedBundleIds();
                    Addressables.Release(transfer);
                    transfer = default;
                    yield return null;
                    // The stock built-in dependency can also be mounted. The protected provider must retain exactly one owner.
                    var afterDownloadRelease = LoadedBundleIds();
                    Require(load.IsValid() && mounted.Length > 0 && afterDownloadRelease.SequenceEqual(mounted) &&
                        session.Provider.Mounted == 1 && session.Provider.Unloading == 0 && session.Provider.StreamsClosed == 0 &&
                        keys.Issued == 1 && keys.Returns == 0,
                        $"Download release changed Bundle ownership: native [{string.Join(",", mounted)}] -> [{string.Join(",", afterDownloadRelease)}], " +
                        $"mounted {session.Provider.Mounted}, unloading {session.Provider.Unloading}, streams closed {session.Provider.StreamsClosed}, keys {keys.Issued}/{keys.Returns}.");
                    CheckAsset(load);
                    _row.downloadReleasedBeforeAsset = true;
                }
                finally
                {
                    if (load.IsValid()) Addressables.Release(load);
                    if (transfer.IsValid()) Addressables.Release(transfer);
                }
            }
            else if (scenario == "held-load")
            {
                string held = Path.Combine(player, "HeldLoad");
                var load = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
                try
                {
                    yield return Until(() => File.Exists(Path.Combine(held, "in-flight")));
                    _row.singleLoadAttempt = !load.IsDone && session.Provider.Pending == 1 &&
                        session.Provider.Creates == 0 && keys.Opens == 0 && !File.Exists(final);
                    Require(_row.singleLoadAttempt, "One official load must have exactly one active protected check/transfer, not independent siblings.");
                    yield return PartialDownload(load, record.CipherLength);
                    _row.heldLoadBytes = load.GetDownloadStatus().DownloadedBytes;
                    using (File.Open(Path.Combine(held, "release"), FileMode.CreateNew)) { }
                    yield return Until(() => load.IsDone);
                    CheckAsset(load);
                    _row.completedLoadBytes = CompletedDownloadBytes(load, record.CipherLength);
                }
                finally { Addressables.Release(load); }
            }
            else if (scenario == "post-start-guard")
            {
                yield return CheckGuardAndClose(session, startup, desc, cache, keys);
                Caching.currentCacheForWriting = old;
                CheckKeyReturns(keys);
                yield break;
            }
            else if (scenario == "no-cache-offline" || scenario == "missing-key" || scenario == "wrong-key")
            {
                var bad = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
                yield return Until(() => bad.IsDone);
                string failure = bad.OperationException?.ToString();
                Require(bad.Status == AsyncOperationStatus.Failed && !bad.Result && session.Provider.Creates == 0 && system.GetDiagnostics().LeaseCount == 0,
                    "Protected lookup should fail without creating or leasing an asset.");
                string expected = scenario == "missing-key" ? "EncryptedBundleKeyUnavailableException" : scenario == "wrong-key" ? "EncryptedBundleAuthenticationException" : "network failure";
                Require(failure != null && failure.Contains(expected) && (scenario == "no-cache-offline" ? !File.Exists(final) : File.Exists(final)), "Unexpected failure: " + failure);
                if (scenario != "no-cache-offline")
                    Require(keys.Opens == 1 && keys.Issued == (scenario == "wrong-key" ? 1 : 0), "Missing-key and wrong-key paths did not exercise distinct lease outcomes.");
                _row.rejection = expected;
                Addressables.Release(bad);
                if (scenario != "no-cache-offline") yield return Sizes(system, new[] { ProtectedKey }, 0, false);
            }
            else if (scenario == "external-owner" || scenario == "key-return")
            {
                var official = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
                yield return Until(() => official.IsDone);
                CheckAsset(official);
                using var first = system.CreateScope("first");
                using var second = system.CreateScope("second");
                using var a = first.LoadAsync<TextAsset>(ProtectedKey);
                using var b = second.LoadAsync<TextAsset>(ProtectedTwinKey);
                using var independent = system.AcquireAsync<TextAsset>(ProtectedTwinKey);
                yield return Until(() => a.IsDone && b.IsDone && independent.IsDone);
                Require(a.TakeResult().text == _token && b.TakeResult().text == _token, "Scope sentinel differs.");
                independent.TakeResult().Dispose();
                using var closeA = first.CloseAsync();
                using var closeB = second.CloseAsync();
                yield return Until(() => closeA.IsDone && closeB.IsDone);
                closeA.TakeResult(); closeB.TakeResult();
                _row.nativeBeforeStop = LoadedBundleIds();
                yield return Stop(startup);
                _row.leasesAfterStop = system.GetDiagnostics().LeaseCount;
                _row.nativeAfterStop = LoadedBundleIds();
                _row.bundlesAfterStop = _row.nativeAfterStop.Length;
                _row.externalAfterStop = _row.leasesAfterStop == 0 && _row.nativeAfterStop.Length > 0 &&
                    _row.nativeAfterStop.SequenceEqual(_row.nativeBeforeStop) && session.Provider.Mounted == 1 && session.Provider.Creates == 1 &&
                    session.Provider.Unloads == 0 && keys.Opens == 1 && keys.Returns == 0 && official.IsValid() && official.Result.text == _token;
                Require(_row.externalAfterStop, "YL Stop consumed an independently owned Bundle or key.");
                try { session.CloseAsync(); }
                catch (InvalidOperationException) { _row.refusedCloseWithExternalOwner = true; }
                Require(_row.refusedCloseWithExternalOwner && official.Result.text == _token, "Closing must not consume an external owner.");
                try { using var invalid = File.Open(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { _row.stableFile = true; }
                Require(_row.stableFile, "Native stream did not retain a stable file.");
                var oldInstances = _row.nativeAfterStop;
                Addressables.Release(official);
                _row.remountDuringUnload = session.Provider.Unloading == 1 && keys.Returns == 0;
                Require(_row.remountDuringUnload, "The remount probe missed the first native unload window.");
                var remount = Addressables.LoadAssetAsync<TextAsset>(ProtectedTwinKey);
                yield return Until(() => remount.IsDone);
                CheckAsset(remount);
                _row.remount = session.Provider.Creates == 2 && session.Provider.Unloads == 1 && keys.Returns == 1 &&
                    session.Provider.StreamsClosed == 1 && LoadedBundleIds().Except(oldInstances).Any();
                Require(_row.remount, "Reload did not wait for the old native unload.");
                Task reentered = null;
                bool cacheLockedDuringReturn = false;
                string cacheLock = Path.Combine(cache, ".yl-cache.lock");
                onKeyReturn = () =>
                {
                    reentered = session.CloseAsync();
                    _row.reenteredCloseDuringKeyReturn = !reentered.IsCompleted && session.Provider.Unloading == 1 &&
                        session.Provider.StreamsClosed == 1;
                    try { using var unexpected = OwnedWindowsFiles.LockPinned(cacheLock); }
                    catch (IOException) { cacheLockedDuringReturn = true; }
                };
                Addressables.Release(remount);
                bool draining = session.Provider.Unloading != 0;
                var closingSession = session.CloseAsync();
                // If native completion was immediate, the callback observed the same drain before Release returned.
                _row.closeWaitedForUnload = draining ? !closingSession.IsCompleted : _row.reenteredCloseDuringKeyReturn;
                Require(_row.closeWaitedForUnload && ReferenceEquals(closingSession, session.CloseAsync()),
                    "Close must be shared and stay pending after the last handle has started unload.");
                yield return Until(() => closingSession.IsCompleted);
                closingSession.GetAwaiter().GetResult();
                _row.sharedClose = ReferenceEquals(closingSession, reentered) && ReferenceEquals(closingSession, session.CloseAsync());
                using (var reopened = OwnedWindowsFiles.LockPinned(cacheLock)) _row.reopenedCacheLock = !reopened.IsInvalid;
                Require(_row.sharedClose && _row.reenteredCloseDuringKeyReturn && cacheLockedDuringReturn && _row.reopenedCacheLock &&
                    keys.Returns == 2 && session.Provider.StreamsClosed == 2,
                    "The close must stay pending through key return, then release the stream and cache lock exactly once.");
            }
            else
            {
                using var scope = system.CreateScope("offline/warm");
                using var loading = scope.LoadAsync<TextAsset>(ProtectedTwinKey);
                yield return Until(() => loading.IsDone);
                _row.sentinel = loading.TakeResult().text == _token;
                Require(_row.sentinel, "YL warm sentinel differs.");
                using var closing = scope.CloseAsync();
                yield return Until(() => closing.IsDone);
                closing.TakeResult();
            }

            if (scenario == "cold")
            {
                var control = Addressables.LoadAssetAsync<TextAsset>(OrdinaryKey);
                yield return Until(() => control.IsDone);
                Require(control.Status == AsyncOperationStatus.Succeeded && control.Result.text == "ordinary:" + _token && Caching.IsVersionCached(officialVersion),
                    "Ordinary remote group did not use its normal Unity cache.");
                Addressables.Release(control);
                yield return Sizes(system, new[] { Mixed }, 0, true);
            }
            if (scenario != "external-owner" && scenario != "key-return") yield return Stop(startup);
            Caching.currentCacheForWriting = old;
            yield return Until(() => session.Provider.Pending == 0 && session.Provider.Mounted == 0 && session.Provider.Unloading == 0 && !AssetBundle.GetAllLoadedAssetBundles().Any());
            _row.creates = session.Provider.Creates; _row.unloads = session.Provider.Unloads; _row.streamsClosed = session.Provider.StreamsClosed;
            Require(_row.creates == _row.unloads && _row.unloads == _row.streamsClosed, "Native unload and managed owner totals differ.");
            if (File.Exists(final))
            {
                using var released = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                _row.exclusiveAfterUnload = true;
                _row.cacheBytes = released.Length;
                Require(released.Length == record.CipherLength && AuthenticatedBundleStream.Hash(released, default) == record.CipherSha256,
                    "Verified ciphertext was not retained unchanged.");
            }
            _row.noParts = !Directory.EnumerateFiles(cache, "*.part").Any();
            Require(_row.noParts, "A transfer left temporary ciphertext.");
            var stop = session.CloseAsync();
            yield return Until(() => stop.IsCompleted);
            stop.GetAwaiter().GetResult();
            CheckKeyReturns(keys);
        }

        private void CaptureInstalledIdentity()
        {
            using (var catalog = OwnedWindowsFiles.OpenRead(Path.Combine(Addressables.RuntimePath, _catalogName)))
                _row.catalogSha256 = AuthenticatedBundleStream.Hash(catalog, default);
            string path = Path.Combine(Addressables.RuntimePath, EncryptedBundleDescription.FileName);
            if (File.Exists(path))
                using (var description = OwnedWindowsFiles.OpenRead(path))
                    _row.descriptionSha256 = AuthenticatedBundleStream.Hash(description, default);
        }

        private IEnumerator EmbeddedLayout(string scenario)
        {
            var description = EncryptedDeliverySession.ReadShippedDescription(Addressables.RuntimePath, true, default);
            var local = description.Bundles.Single(row => row.Binding.KeyId == "K1");
            Require(local.Binding.Mode == EncryptedBundleDeliveryMode.Embedded, "First chapter must be installed.");
            bool mixed = description.ReleasePrefix != null, offline = scenario == "layout-offline", bad = scenario == "layout-bad-embedded";
            if (offline)
            {
                AssertInstalledDirectoryReadOnly(Path.GetDirectoryName(Application.dataPath));
                _row.installedRootWriteDenied = true;
                AssertInstalledDirectoryReadOnly(Path.Combine(Addressables.RuntimePath, EncryptedBundleBinding.EmbeddedFolder, local.Binding.GroupGuid));
                _row.installedContentWriteDenied = true;
            }
            string cache = Argument("-ylFrozenCache");
            Require(!Directory.Exists(cache), "The fresh installation must not have a cipher cache.");
            IDisposable heldLock = null;
            using var keys = new Keys(Argument("-ylFrozenKey"), scenario, peerKeyFile: OptionalArgument("-ylFrozenPeerKey"));
            try
            {
                if (mixed && offline)
                {
                    Directory.CreateDirectory(cache);
                    heldLock = OwnedWindowsFiles.LockPinned(Path.Combine(cache, ".yl-cache.lock"));
                }
                var starting = EncryptedDeliverySession.StartCoreAsync(mixed ? cache : null, keys, true);
                yield return Until(() => starting.IsCompleted);
                var session = starting.GetAwaiter().GetResult();
                Require(!session.IsRemoteReady && keys.Opens == 0 && session.Provider.Creates == 0 && session.EmbeddedCipherHashBytes == 0,
                    "Local readiness touched bundle bytes, mounted content, or prepared remote.");
                long transfer = mixed ? description.Bundles.Where(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps).Sum(row => row.CipherLength) : 0;
                var barrier = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                System.Threading.Tasks.Task preparing = null;
                var size = Addressables.GetDownloadSizeAsync(new[] { ProtectedKey, ProtectedTwinKey, ProtectedKey });
                try
                {
                    yield return Until(() => size.IsDone);
                    Require(size.Status == AsyncOperationStatus.Succeeded && size.Result == transfer, "The protected Union size is not the sum of unique remote locations.");
                    _row.mixedBytes = size.Result;
                }
                finally { Addressables.Release(size); }
                if (mixed)
                {
                    var blocked = Addressables.DownloadDependenciesAsync(ProtectedTwinKey, false);
                    try
                    {
                        yield return Until(() => blocked.IsDone);
                        Require(blocked.Status == AsyncOperationStatus.Failed && blocked.OperationException.ToString().Contains("not ready") &&
                            keys.Opens == 0 && session.Provider.Creates == 0, "Remote prefetch must reject before any work while unprepared.");
                        _row.refusedUnpreparedRemote = true;
                    }
                    finally { Addressables.Release(blocked); }
                    if (offline)
                    {
                        var blockedPreparation = session.PrepareRemoteAsync();
                        yield return Until(() => blockedPreparation.IsCompleted);
                        Require(blockedPreparation.IsFaulted && blockedPreparation.Exception.GetBaseException() is IOException && !session.IsRemoteReady,
                            "A held cache lock must prevent remote preparation without invalidating the installed chapter.");
                        _row.remoteCacheWasBlocked = true;
                    }
                    else if (!bad)
                        preparing = session.PrepareRemoteAsync(default, barrier.Task);
                }
                var prefetch = Addressables.DownloadDependenciesAsync(ProtectedKey, false);
                try
                {
                    yield return Until(() => prefetch.IsDone);
                    Require(prefetch.Status == (bad ? AsyncOperationStatus.Failed : AsyncOperationStatus.Succeeded) && keys.Opens == 0 &&
                        session.Provider.Creates == 0 && session.EmbeddedCipherHashBytes == (bad ? 0 : local.CipherLength),
                        "Installed prefetch must check ciphertext without renting keys or mounting.");
                    _row.installedPrefetchValidated = true;
                    var first = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
                    try
                    {
                        yield return Until(() => first.IsDone);
                        if (bad)
                        {
                            Require(first.Status == AsyncOperationStatus.Failed && keys.Opens == 0 && session.Provider.Creates == 0,
                                "A bad installed Bundle reached key rental or Unity mount.");
                            _row.rejectedBeforeCreate = true;
                        }
                        else
                        {
                            Require(first.Status == AsyncOperationStatus.Succeeded && first.Result.text == _token && !session.IsRemoteReady && keys.Opens == 1,
                                "First chapter was not material-ready independently of the protected remote cache.");
                            _row.sentinel = _row.localBeforeRemoteReady = true;
                            if (preparing != null)
                            {
                                Require(!preparing.IsCompleted && !barrier.Task.IsCompleted && !Directory.Exists(cache),
                                    "Installed content waited for, or implicitly completed, the held remote preparation.");
                                _row.localBeforePreparationReleased = true;
                            }
                        }
                    }
                    finally { Addressables.Release(first); }
                }
                finally { Addressables.Release(prefetch); }
                if (mixed && !offline && !bad)
                {
                    using var canceled = new System.Threading.CancellationTokenSource();
                    var firstWaiter = session.PrepareRemoteAsync(canceled.Token);
                    var shared = session.PrepareRemoteAsync();
                    Require(ReferenceEquals(shared, preparing), "Overlapping preparations did not coalesce.");
                    canceled.Cancel();
                    barrier.SetResult(true);
                    yield return Until(() => firstWaiter.IsCompleted && shared.IsCompleted);
                    shared.GetAwaiter().GetResult();
                    Require(firstWaiter.IsCanceled && session.IsRemoteReady, "A canceled waiter changed shared readiness.");
                    _row.remotePrepared = true;
                    var download = Addressables.DownloadDependenciesAsync(ProtectedTwinKey, false);
                    try
                    {
                        yield return Until(() => download.IsDone);
                        Require(download.Status == AsyncOperationStatus.Succeeded && keys.Opens == 1 && session.Provider.Creates == 1,
                            "A remote prefetch borrowed the peer key or mounted its bundle.");
                        _row.completedTransferBytes = CompletedDownloadBytes(download, transfer);
                        var peer = Addressables.LoadAssetAsync<TextAsset>(ProtectedTwinKey);
                        try
                        {
                            yield return Until(() => peer.IsDone);
                            Require(peer.Status == AsyncOperationStatus.Succeeded && peer.Result.text == "peer:" + _token,
                                "The remote peer did not load from the verified cache with its group key.");
                            _row.readySnapshotZero = session.Cache.VerifiedCipherHashBytes == 2 * transfer;
                        }
                        finally { Addressables.Release(peer); }
                    }
                    finally { Addressables.Release(download); }
                }
                if (!mixed && !offline && !bad)
                {
                    var peer = Addressables.LoadAssetAsync<TextAsset>(ProtectedTwinKey);
                    try
                    {
                        yield return Until(() => peer.IsDone);
                        Require(peer.Status == AsyncOperationStatus.Succeeded && peer.Result.text == "peer:" + _token,
                            "The second installed group did not use its own Bundle and key.");
                    }
                    finally { Addressables.Release(peer); }
                }
                if (scenario == "layout-nohash")
                {
                    var location = Addressables.LoadResourceLocationsAsync(LocalKey, typeof(TextAsset));
                    try
                    {
                        yield return Until(() => location.IsDone);
                        Require(location.Status == AsyncOperationStatus.Succeeded, "The stock local asset did not resolve.");
                        var bundle = location.Result.Single().Dependencies.Single(dep => Path.GetFileName(dep.InternalId) == NoHashBundle);
                        Require(bundle.ProviderId == typeof(AssetBundleProvider).FullName, "The local NoHash Bundle must use the stock provider.");
                    }
                    finally { Addressables.Release(location); }
                    var stock = Addressables.LoadAssetAsync<TextAsset>(LocalKey);
                    try
                    {
                        yield return Until(() => stock.IsDone);
                        Require(stock.Status == AsyncOperationStatus.Succeeded && stock.Result.text == "local:" + _token && keys.Opens == 2 && session.Provider.Creates == 2,
                            "The stock local NoHash group did not coexist with the two protected groups.");
                    }
                    finally { Addressables.Release(stock); }
                }
                if (!session.IsRemoteReady)
                    Require(!Directory.Exists(cache) || (mixed && offline && Directory.GetFiles(cache).Select(Path.GetFileName).SequenceEqual(new[] { ".yl-cache.lock" })),
                        "Content not using the protected CDN wrote remote cache bytes.");
                var close = session.CloseAsync();
                yield return Until(() => close.IsCompleted);
                close.GetAwaiter().GetResult();
                yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
                CheckKeyReturns(keys);
            }
            finally { heldLock?.Dispose(); }
        }

        private static void AssertInstalledDirectoryReadOnly(string directory)
        {
            Require(Directory.Exists(directory), "The read-only probe needs an installed directory.");
            string path = Path.Combine(directory, ".yl-write-probe-" + Guid.NewGuid().ToString("N"));
            bool denied = false;
            try { using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied)
            {
                File.Delete(path);
                throw new InvalidOperationException("The installed directory allows the Player to create files: " + directory);
            }
            denied = false;
            try { Directory.CreateDirectory(path); }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied)
            {
                Directory.Delete(path);
                throw new InvalidOperationException("The installed directory allows the Player to create subdirectories: " + directory);
            }
        }

        private void CheckKeyReturns(Keys keys)
        {
            _row.openKeys = keys.Opens; _row.issuedKeys = keys.Issued; _row.returnKeys = keys.Returns;
            Require(_row.issuedKeys == _row.returnKeys && keys.DuplicateReturns == 0,
                $"Not every issued key lease was returned once: issued {_row.issuedKeys}, returned {_row.returnKeys}, duplicates {keys.DuplicateReturns}.");
            _row.keyLeasesReturned = true;
        }

        private IEnumerator OrdinaryDependency()
        {
            using var keys = new Keys(Argument("-ylFrozenKey"), _row.scenario, peerKeyFile: Argument("-ylFrozenPeerKey"));
            var opening = EncryptedDeliverySession.StartCoreAsync(Argument("-ylFrozenCache"), keys, true);
            yield return Until(() => opening.IsCompleted);
            var session = opening.GetAwaiter().GetResult();
            var prepare = session.PrepareRemoteAsync();
            yield return Until(() => prepare.IsCompleted);
            prepare.GetAwaiter().GetResult();
            var handle = Addressables.LoadAssetAsync<GameObject>("frozen/remote-with-ordinary");
            try
            {
                yield return Until(() => handle.IsDone);
                var sprite = handle.Result ? handle.Result.GetComponent<SpriteRenderer>().sprite : null;
                Require(handle.Status == AsyncOperationStatus.Succeeded && sprite && sprite.name == "public-texture" && sprite.rect.width == 8 &&
                    keys.Opens == 1 && session.Provider.Creates == 1, "The remote protected prefab did not resolve its stock remote dependency.");
                _row.sentinel = true;
            }
            finally { Addressables.Release(handle); }
            var close = session.CloseAsync();
            yield return Until(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
            CheckKeyReturns(keys);
        }

        private IEnumerator MultiGroup()
        {
            using var keys = new Keys(Argument("-ylFrozenKey"), _row.scenario, peerKeyFile: Argument("-ylFrozenPeerKey"));
            var description = EncryptedDeliverySession.ReadShippedDescription(Addressables.RuntimePath, true, default);
            var remotes = description.Bundles.Where(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps).ToArray();
            Require(description.Bundles.Count == 4 && description.Bundles.Select(row => row.Binding.GroupGuid).Distinct().Count() == 4 && remotes.Length == 2,
                "The mixed test must have two distinct groups in each layout.");
            string cache = Argument("-ylFrozenCache");
            var starting = EncryptedDeliverySession.StartCoreAsync(cache, keys, true);
            yield return Until(() => starting.IsCompleted);
            var session = starting.GetAwaiter().GetResult();
            long bytes = remotes.Sum(row => row.CipherLength);
            var size = Addressables.GetDownloadSizeAsync(new[] { FourGroupLabel, ProtectedTwinKey, ProtectedKey });
            try
            {
                yield return Until(() => size.IsDone);
                Require(!session.IsRemoteReady && !Directory.Exists(cache) && size.Status == AsyncOperationStatus.Succeeded && size.Result == bytes,
                    "The mixed Union must count both remote groups exactly once and leave the cache untouched.");
            }
            finally { Addressables.Release(size); }
            var prepare = session.PrepareRemoteAsync();
            yield return Until(() => prepare.IsCompleted);
            prepare.GetAwaiter().GetResult();
            var prefetch = Addressables.DownloadDependenciesAsync((object)FourGroupLabel, false);
            try
            {
                yield return Until(() => prefetch.IsDone);
                Require(prefetch.Status == AsyncOperationStatus.Succeeded && keys.Opens == 0 && session.Provider.Creates == 0,
                    "The four-group prefetch must not borrow keys or mount content.");
                _row.completedTransferBytes = CompletedDownloadBytes(prefetch, bytes);
                var load = Addressables.LoadAssetsAsync<TextAsset>((object)FourGroupLabel, null);
                try
                {
                    yield return Until(() => load.IsDone);
                    var expected = new HashSet<string>(new[] { _token, "peer:" + _token, "local-2:" + _token, "remote-2:" + _token }, StringComparer.Ordinal);
                    Require(load.Status == AsyncOperationStatus.Succeeded && load.Result.Count == 4 && expected.SetEquals(load.Result.Select(asset => asset.text)) &&
                        session.Provider.Mounted == 4 && keys.Opens == 4, "The shared Label did not load the correct asset from all four groups.");
                    _row.sentinel = true;
                }
                finally { Addressables.Release(load); }
            }
            finally { Addressables.Release(prefetch); }
            var close = session.CloseAsync();
            yield return Until(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
            _row.creates = session.Provider.Creates;
            _row.streamsClosed = session.Provider.StreamsClosed;
            CheckKeyReturns(keys);
        }

        private IEnumerator BadDependency()
        {
            const string address = "frozen/remote-with-embedded";
            using var keys = new Keys(Argument("-ylFrozenKey"), _row.scenario, peerKeyFile: Argument("-ylFrozenPeerKey"));
            var starting = EncryptedDeliverySession.StartCoreAsync(Argument("-ylFrozenCache"), keys, true);
            yield return Until(() => starting.IsCompleted);
            var session = starting.GetAwaiter().GetResult();
            var prepare = session.PrepareRemoteAsync();
            yield return Until(() => prepare.IsCompleted);
            prepare.GetAwaiter().GetResult();
            var locations = Addressables.LoadResourceLocationsAsync(address, typeof(GameObject));
            var localLocations = Addressables.LoadResourceLocationsAsync(ProtectedKey, typeof(TextAsset));
            EncryptedBundleRecord remote;
            try
            {
                yield return Until(() => locations.IsDone && localLocations.IsDone);
                Require(locations.Status == AsyncOperationStatus.Succeeded && localLocations.Status == AsyncOperationStatus.Succeeded,
                    "The installed Catalog did not resolve the test assets.");
                EncryptedBundleRecord[] Transfers(IResourceLocation asset) => asset.Dependencies
                    .Where(dep => dep.ProviderId == session.Provider.ProviderId && dep.ResourceType == typeof(IAssetBundleResource))
                    .Select(session.ResolveTransfer).ToArray();
                var pair = Transfers(locations.Result.Single());
                var local = Transfers(localLocations.Result.Single()).Single();
                Require(pair.Length == 2 && local.Binding.Mode == EncryptedBundleDeliveryMode.Embedded && pair.Contains(local),
                    "The Catalog must give the remote asset the very same installed Bundle as the independent owner.");
                remote = pair.Single(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps);
            }
            finally
            {
                Addressables.Release(localLocations);
                Addressables.Release(locations);
            }
            var owner = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
            try
            {
                yield return Until(() => owner.IsDone);
                Require(owner.Status == AsyncOperationStatus.Succeeded && owner.Result.text == _token && keys.Opens == 1 && session.Provider.Creates == 1,
                    "The independent installed owner failed to mount A.");
                var prefetch = Addressables.DownloadDependenciesAsync(address, false);
                try
                {
                    yield return Until(() => prefetch.IsDone);
                    Require(prefetch.Status == AsyncOperationStatus.Succeeded && keys.Opens == 1 && session.Provider.Creates == 1,
                        "Prefetch of the shared dependency set borrowed a new key or mounted B.");
                    _row.completedTransferBytes = CompletedDownloadBytes(prefetch, remote.CipherLength);
                    Require(session.Cache.TryGetVerifiedPath(remote, out string path), "Prefetch did not leave verified remote bytes.");
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        int first = file.ReadByte();
                        Require(first >= 0, "No fixture ciphertext to damage.");
                        file.Position = 0;
                        file.WriteByte((byte)(first ^ 1));
                    }
                    var failed = Addressables.LoadAssetAsync<GameObject>(address);
                    try
                    {
                        yield return Until(() => failed.IsDone);
                        Require(failed.Status == AsyncOperationStatus.Failed && (failed.OperationException?.ToString().Contains(nameof(EncryptedBundleIntegrityException)) ?? false) &&
                            keys.Opens == 1 && session.Provider.Creates == 1 && session.Provider.Mounted == 1 && owner.Result.text == _token,
                            "The bad Bundle must not mount or displace the independent owner of the good Bundle. " + failed.OperationException);
                        _row.rejectedBeforeCreate = _row.sentinel = true;
                    }
                    finally { Addressables.Release(failed); }
                }
                finally { Addressables.Release(prefetch); }
            }
            finally { Addressables.Release(owner); }
            var close = session.CloseAsync();
            yield return Until(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
            _row.creates = session.Provider.Creates;
            _row.unloads = session.Provider.Unloads;
            _row.streamsClosed = session.Provider.StreamsClosed;
            Require(_row.creates == 1 && _row.unloads == 1 && _row.streamsClosed == 1,
                "Only the good Bundle should mount and release a native stream.");
            CheckKeyReturns(keys);
        }

        private IEnumerator Sizes(ResourceSystem system, string[] keys, long expected, bool mixed)
        {
            var official = Addressables.GetDownloadSizeAsync(keys);
            using var yl = system.GetDownloadSizeAsync(keys);
            yield return Until(() => official.IsDone && yl.IsDone);
            try
            {
                Require(official.Status == AsyncOperationStatus.Succeeded && official.Result == expected && yl.TakeResult() == expected,
                    $"Download size for {string.Join(",", keys)} differs: official {official.Status} {official.Result}, expected {expected}. {official.OperationException}");
                if (mixed) _row.mixedBytes = expected; else _row.coldBytes = expected;
            }
            finally { Addressables.Release(official); }
        }

        private IEnumerator GoodOfficial(string key)
        {
            var asset = Addressables.LoadAssetAsync<TextAsset>(key);
            yield return Until(() => asset.IsDone);
            try { CheckAsset(asset); } finally { Addressables.Release(asset); }
        }

        private static IEnumerator PartialDownload(AsyncOperationHandle handle, long total)
        {
            Require(handle.GetDownloadStatus().TotalBytes == total, "The official operation did not report exactly one ciphertext transfer.");
            yield return Until(() => handle.IsDone || handle.GetDownloadStatus().DownloadedBytes > 0);
            var received = handle.GetDownloadStatus();
            Require(!handle.IsDone && !received.IsDone && received.TotalBytes == total && received.DownloadedBytes > 0 && received.DownloadedBytes < total,
                "The official operation did not report partial ciphertext bytes while the GET was held.");
        }

        private static long CompletedDownloadBytes(AsyncOperationHandle handle, long total)
        {
            var received = handle.GetDownloadStatus();
            Require(received.IsDone && received.TotalBytes == total && received.DownloadedBytes == total,
                $"Completed official byte progress differs: {received.DownloadedBytes}/{received.TotalBytes}, expected {total}.");
            return received.DownloadedBytes;
        }

        private void CheckAsset(AsyncOperationHandle<TextAsset> asset)
        {
            _row.sentinel = asset.Status == AsyncOperationStatus.Succeeded && asset.Result && asset.Result.text == _token;
            Require(_row.sentinel, "Official Addressables did not receive the actual protected sentinel: " + asset.OperationException);
        }

        private static IEnumerator Stop(ResourceStartup startup)
        {
            using var stop = startup.StopAsync();
            yield return Until(() => stop.IsDone);
            Require(stop.TakeResult(), "YL resource owner did not stop.");
        }

        private static int[] LoadedBundleIds() => AssetBundle.GetAllLoadedAssetBundles().Select(bundle => bundle.GetInstanceID()).OrderBy(id => id).ToArray();

        private IEnumerator CheckGuardAndClose(EncryptedDeliverySession session, ResourceStartup startup,
            EncryptedBundleDescription description, string cache, Keys keys)
        {
            var locator = Addressables.ResourceLocators.Single(candidate => candidate.LocatorId == "AddressablesMainContentCatalog");
            Require(locator.Locate(Mixed, typeof(TextAsset), out var candidates) && candidates.Count == 4, "The mixed Label lost one or more ordinary/protected routes.");
            var reordered = new ReorderedAlias(locator);
            try { FrozenAddressablesRoutes.CheckResolved(reordered, description); }
            catch (InvalidDataException error) when (error.Message.Contains("independent authoring audit")) { _row.rejectedReorderedCandidates = true; }

            var chosen = Addressables.LoadAssetAsync<TextAsset>(Mixed);
            var byLocation = Addressables.LoadAssetAsync<TextAsset>(candidates[0]);
            yield return Until(() => chosen.IsDone && byLocation.IsDone);
            _row.matchedFirstCandidate = chosen.Status == AsyncOperationStatus.Succeeded && byLocation.Status == AsyncOperationStatus.Succeeded &&
                chosen.Result && ReferenceEquals(chosen.Result, byLocation.Result);
            Addressables.Release(chosen);
            Addressables.Release(byLocation);
            Require(_row.matchedFirstCandidate, "The ordinary official Label no longer picks its first same-type candidate.");
            yield return Until(() => session.Provider.Mounted == 0 && session.Provider.Unloading == 0);

            var held = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
            yield return Until(() => held.IsDone);
            CheckAsset(held);
            var added = new ResourceLocationMap("fixture-owned extra locator");
            Addressables.AddResourceLocator(added);
            Func<IResourceLocation, string> otherTransform = location => location.InternalId;
            Addressables.InternalIdTransformFunc = otherTransform;
            int returns = keys.Returns;
            int creates = session.Provider.Creates;
            Require(held.IsValid() && held.Result.text == _token && keys.Opens == returns + 1, "The old owner was invalidated by configuration drift.");
            Addressables.Release(held);
            yield return Until(() => session.Provider.Mounted == 0 && session.Provider.Unloading == 0);
            var next = Addressables.LoadAssetAsync<TextAsset>(ProtectedTwinKey);
            yield return Until(() => next.IsDone);
            _row.rejectedAfterDrift = next.Status == AsyncOperationStatus.Failed && !next.Result &&
                (next.OperationException?.ToString().Contains("must remain fixed throughout the session") ?? false) && session.Provider.Creates == creates && keys.Returns == returns + 1;
            Addressables.Release(next);
            Require(_row.rejectedAfterDrift, "A new protected operation or old owner did not settle correctly after drift.");
            yield return Stop(startup);
            bool closeReturned = false;
            Task stopped = null;
            try { stopped = session.CloseAsync(); }
            catch (InvalidOperationException) { }
            if (stopped != null)
            {
                yield return Until(() => stopped.IsCompleted);
                stopped.GetAwaiter().GetResult();
                closeReturned = true;
            }
            _row.leftForeignRegistration = Addressables.ResourceLocators.Contains(added) && Addressables.InternalIdTransformFunc == otherTransform;
            if (closeReturned)
                using (var reopened = OwnedWindowsFiles.LockPinned(Path.Combine(cache, ".yl-cache.lock"))) _row.reopenedCacheLock = !reopened.IsInvalid;
            Require(_row.rejectedReorderedCandidates && _row.leftForeignRegistration && _row.reopenedCacheLock,
                "Startup must reject candidate reordering and Close must release its own lock without removing foreign registrations.");
        }

        private sealed class ReorderedAlias : IResourceLocator
        {
            private readonly IResourceLocator _source;
            internal ReorderedAlias(IResourceLocator source) => _source = source;
            public string LocatorId => _source.LocatorId;
            public IEnumerable<object> Keys => _source.Keys;
            public IEnumerable<IResourceLocation> AllLocations => _source.Keys.SelectMany(key =>
                _source.Locate(key, typeof(object), out var locations) ? locations : Array.Empty<IResourceLocation>()).Distinct();

            public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
            {
                if (!_source.Locate(key, type, out locations)) return false;
                if (Equals(key, Mixed)) locations = locations.Reverse().ToArray();
                return true;
            }
        }

        private sealed class UnexpectedAssetProvider : ResourceProviderBase
        {
            public override string ProviderId => typeof(BundledAssetProvider).FullName;
            public override void Provide(ProvideHandle handle) => throw new InvalidOperationException("Unexpected test provider must never be invoked.");
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            float deadline = Time.realtimeSinceStartup + 35;
            while (!condition()) { Require(Time.realtimeSinceStartup < deadline, "Fixture operation timed out."); yield return null; }
        }

        private static string Argument(string name)
            => OptionalArgument(name) ?? throw new InvalidOperationException("Required fixture argument " + name);

        private static string OptionalArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            if (index < 0) return null;
            Require(index == Array.LastIndexOf(args, name) && index + 1 < args.Length, "Invalid fixture argument " + name);
            return args[index + 1];
        }

        private void Finish(Exception error)
        {
            _row.status = error == null ? "Passed" : "Failed";
            _row.error = error?.ToString();
            if (error != null) Debug.LogException(error);
            Write(_output);
            Application.Quit(error == null ? 0 : 1);
        }

        private void OnApplicationQuit()
        {
            if (_row?.status == null || _row.status == "BeforeUnexpectedExit") return;
            _row.quit = true;
            Write(_output + ".quit");
        }

        private void Write(string path)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(JsonUtility.ToJson(_row, true));
        }

        public static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        public sealed class Keys : IEncryptedBundleKeySource, IDisposable
        {
            private readonly byte[] _bytes = new byte[32];
            private readonly byte[] _peer;
            private readonly string _mode;
            public Action OnReturn;
            public int Opens, Issued, Returns, DuplicateReturns;

            public Keys(string file, string mode, Action onAuthorReturn = null, string peerKeyFile = null)
            {
                Read(file, _bytes);
                if (peerKeyFile != null)
                {
                    _peer = new byte[_bytes.Length];
                    Read(peerKeyFile, _peer);
                }
                _mode = mode;
                OnReturn = onAuthorReturn;
                if (mode == "wrong-key") _bytes[0] ^= 1;
            }

            private static void Read(string file, byte[] key)
            {
                using var input = OwnedWindowsFiles.OpenRead(file);
                Require(input.Length == key.Length && input.Read(key, 0, key.Length) == key.Length, "Missing ephemeral fixture key.");
            }

            public EncryptedBundleKeyLease OpenReadKey(string keyId)
            {
                System.Threading.Interlocked.Increment(ref Opens);
                var material = keyId == "K1" ? _bytes : keyId == "K2" ? _peer : null;
                if (_mode == "missing-key" || (_mode == "LateKeyFailure" && Opens == 2) || material == null) return null;
                int returned = 0;
                var lease = new EncryptedBundleKeyLease(material, () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref returned, 1) != 0)
                    {
                        System.Threading.Interlocked.Increment(ref DuplicateReturns);
                        return;
                    }
                    System.Threading.Interlocked.Increment(ref Returns);
                    OnReturn?.Invoke();
                    if (_mode == "key-return")
                        throw new InvalidOperationException("Fixture key return callback failed.");
                });
                System.Threading.Interlocked.Increment(ref Issued);
                return lease;
            }
            public void Dispose()
            {
                Array.Clear(_bytes, 0, _bytes.Length);
                if (_peer != null) Array.Clear(_peer, 0, _peer.Length);
            }
        }
    }
}
