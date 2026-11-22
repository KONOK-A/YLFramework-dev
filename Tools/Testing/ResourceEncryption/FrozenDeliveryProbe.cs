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
        public const string Mixed = "frozen/mixed";

        [SerializeField] private string _token;
        [SerializeField] private string _backend;
        [SerializeField] private string _inputHash;
        [SerializeField] private string _catalogName;
        public void Configure(string token, string backend, string inputHash, string catalogName)
        {
            _token = token;
            _backend = backend;
            _inputHash = inputHash;
            _catalogName = catalogName;
        }
        private string _output;
        private Row _row;

        [Serializable]
        private sealed class Row
        {
            public string token, scenario, status, error, rejection, unity, backend, architecture, graphics, catalogSha256, inputHash, descriptionSha256;
            public int pid, bits, creates, unloads, streamsClosed, openKeys, issuedKeys, returnKeys, bundlesAfterStop, leasesAfterStop;
            public int[] nativeBeforeStop, nativeAfterStop;
            public PerformanceEvidence performance;
            public long coldBytes, mixedBytes, cacheBytes;
            public bool quit, sentinel, verifiedStartup, externalAfterStop, stableFile, exclusiveAfterUnload, remount, remountDuringUnload, noParts, predownloadOnly;
            public bool sawOwnedPart, rejectedBeforeCreate, invalidatedSize, refusedCloseWithExternalOwner;
            public bool rejectedReorderedCandidates, matchedFirstCandidate, rejectedAfterDrift, leftForeignRegistration, reopenedCacheLock;
            public bool observerStartedTransfer, loadedWhileTransferHeld, observerCanceledWhileTransferHeld, downloadReleasedBeforeAsset, keyLeasesReturned;
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
            using (var catalog = OwnedWindowsFiles.OpenRead(Path.Combine(Addressables.RuntimePath, _catalogName)))
                _row.catalogSha256 = AuthenticatedBundleStream.Hash(catalog, default);
            string descriptionPath = Path.Combine(Addressables.RuntimePath, EncryptedBundleDescription.FileName);
            if (File.Exists(descriptionPath))
                using (var description = OwnedWindowsFiles.OpenRead(descriptionPath))
                    _row.descriptionSha256 = AuthenticatedBundleStream.Hash(description, default);
            string player = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (scenario == "disabled") { yield return Disabled(); yield break; }
            if (scenario.StartsWith("performance-", StringComparison.Ordinal)) { yield return Performance(player); yield break; }
            string cacheName = scenario == "fault-abort" || scenario == "fault-recovery" ? "Cache-abort" :
                scenario == "no-cache-offline" || scenario == "held-download" ? "Cache-" + scenario : "CipherCache";
            string cache = Path.Combine(player, cacheName);
            using var keys = new Keys(Path.GetFullPath(Path.Combine(player, "..", "fixture.key")), scenario);
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
            long initial = scenario == "cold" || scenario == "no-cache-offline" || scenario == "held-download" ||
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
            else if (scenario == "tamper")
            {
                using (var data = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    byte original = checked((byte)data.ReadByte());
                    data.Position = 0; data.WriteByte((byte)(original ^ 1));
                }
                var invalid = Addressables.LoadAssetAsync<TextAsset>(ProtectedKey);
                yield return Until(() => invalid.IsDone);
                var error = invalid.OperationException?.ToString();
                _row.rejectedBeforeCreate = invalid.Status == AsyncOperationStatus.Failed && !invalid.Result && error != null && error.Contains("EncryptedBundleIntegrityException") &&
                    session.Provider.Creates == 0 && keys.Opens == 0 && system.GetDiagnostics().LeaseCount == 0;
                Addressables.Release(invalid);
                Require(_row.rejectedBeforeCreate && !File.Exists(final), "Same-length replacement was not retired before native creation.");
                yield return Sizes(system, new[] { ProtectedKey }, record.CipherLength, false);
                _row.invalidatedSize = true;
            }
            else if (scenario == "fault-recovery" || scenario == "repair")
            {
                Require(!Directory.EnumerateFiles(cache, "*.part").Any(), "Owned abandoned attempt was not removed on restart.");
                yield return GoodOfficial(ProtectedKey);
            }
            else if (scenario == "cold")
            {
                var predownload = Addressables.DownloadDependenciesAsync(ProtectedKey, false);
                yield return Until(() => predownload.IsDone);
                Require(predownload.Status == AsyncOperationStatus.Succeeded && !AssetBundle.GetAllLoadedAssetBundles().Any() && session.Provider.Creates == 0 && keys.Opens == 0 &&
                    system.GetDiagnostics().LeaseCount == 0, "Download-only was not a pure ciphertext transfer: " + predownload.OperationException);
                _row.predownloadOnly = true;
                yield return GoodOfficial(ProtectedKey);
                Addressables.Release(predownload);
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
                    using (File.Open(Path.Combine(held, "release"), FileMode.CreateNew)) { }
                    yield return Until(() => transfer.IsDone && load.IsDone);
                    Require(transfer.IsValid() && transfer.Status == AsyncOperationStatus.Succeeded, "A held download handle was invalidated.");
                    CheckAsset(load);
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
                Addressables.Release(remount);
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

        private void CheckKeyReturns(Keys keys)
        {
            _row.openKeys = keys.Opens; _row.issuedKeys = keys.Issued; _row.returnKeys = keys.Returns;
            Require(_row.issuedKeys == _row.returnKeys && keys.DuplicateReturns == 0,
                $"Not every issued key lease was returned once: issued {_row.issuedKeys}, returned {_row.returnKeys}, duplicates {keys.DuplicateReturns}.");
            _row.keyLeasesReturned = true;
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
                (next.OperationException?.ToString().Contains("must not change after startup") ?? false) && session.Provider.Creates == creates && keys.Returns == returns + 1;
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
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            Require(index >= 0 && index == Array.LastIndexOf(args, name) && index + 1 < args.Length, "Required fixture argument " + name);
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
            private readonly string _mode;
            private readonly Action _onAuthorReturn;
            public int Opens, Issued, Returns, DuplicateReturns;

            public Keys(string file, string mode, Action onAuthorReturn = null)
            {
                using var input = OwnedWindowsFiles.OpenRead(file);
                Require(input.Length == _bytes.Length && input.Read(_bytes, 0, _bytes.Length) == _bytes.Length, "Missing ephemeral fixture key.");
                _mode = mode;
                _onAuthorReturn = onAuthorReturn;
                if (mode == "wrong-key") _bytes[0] ^= 1;
            }

            public EncryptedBundleKeyLease OpenReadKey(string keyId)
            {
                System.Threading.Interlocked.Increment(ref Opens);
                if (_mode == "missing-key" ? keyId != "K2" : keyId != "K1") return null;
                int returned = 0;
                var lease = new EncryptedBundleKeyLease(_bytes, () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref returned, 1) != 0)
                    {
                        System.Threading.Interlocked.Increment(ref DuplicateReturns);
                        return;
                    }
                    if (System.Threading.Interlocked.Increment(ref Returns) == 1 && _mode == "key-return")
                        throw new InvalidOperationException("Fixture key return callback failed.");
                    _onAuthorReturn?.Invoke();
                });
                System.Threading.Interlocked.Increment(ref Issued);
                return lease;
            }
            public void Dispose() => Array.Clear(_bytes, 0, _bytes.Length);
        }
    }
}
