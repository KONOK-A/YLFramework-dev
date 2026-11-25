using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Profiling;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource;
using YLFramework.Resource.Encryption;

namespace YLFramework.Testing
{
    public sealed partial class FrozenDeliveryProbe
    {
        public const string SmallKey = "frozen-small";
        public const string LargeKey = "frozen-large";
        private const int MiB = 1024 * 1024;
        private static System.Diagnostics.Stopwatch _performanceStartup;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void MarkStartup() => _performanceStartup = System.Diagnostics.Stopwatch.StartNew();

        [Serializable]
        private sealed class PerformanceEvidence
        {
            public string mode, startGateKind, bundleReadKind, nativeBufferEvidence = "unavailable", enginePeakMemory = "unavailable";
            public string deliveryLayout = "Remote", entryPoint = "Addressables.LoadAssetAsync<TextAsset>";
            public int metricsVersion = 3, preLoadMounted, preLoadKeys, preLoadAuthentications;
            public bool concurrent;
            public double startGateMs, startGateMainThreadCpuMs, elapsedMs, mainThreadCpuMs, largestObservedFrameMs;
            public double requestedAtMs, smallReadyAtMs, largeReadyAtMs, firstAssetMs, bothAssetsMs, remotePrepareMs;
            public long cacheLogicalPeak = -1, cacheLogicalBytesAtCompletion, processPrivateBefore, processPrivatePeak, processWorkingSetBefore, processWorkingSetPeak;
            public long startGateCipherHashBytes = -1, loadCipherHashBytes = -1;
            public long engineAssetReportedBytes = -1, fixturePayloadBytes;
            public BundleMeasure[] bundles;
        }

        [Serializable]
        private sealed class BundleMeasure
        {
            public string clearSha256, cipherSha256;
            public long clearBytes, cipherBytes;
            public long officialDownloadBytes = -1;
            public long readPlain = -1, readCipher = -1, pages = -1, payloadBufferBound = -1, engineReadPlain = -1, engineReadCipher = -1;
            public long preAuthenticationCipherBytes = -1, preAuthenticationPlainBytes = -1, unityReadBufferBytes = -1;
            public double cipherShaMs = -1, authenticationMs = -1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessCounters
        {
            public uint Size, Faults;
            public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool, QuotaPeakNonPagedPool, QuotaNonPagedPool,
                Pagefile, PeakPagefile, Private;
        }

        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string name);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
        [DllImport("psapi.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessCounters counters, uint size);

        [Serializable]
        private sealed class LargeDownloadEvidence
        {
            public string termination, error, cipherSha256, firstUtc, lastUtc;
            public long expectedBytes, receivedBytes, stagedBytes = -1;
            public int readCalls, keysOpened, mounts;
            public int writeHResult;
            public double elapsedSeconds;
        }

        private IEnumerator MeasureLargeDownload()
        {
            string cache = Argument("-ylFrozenCache");
            var description = EncryptedDeliverySession.ReadShippedDescription(Addressables.RuntimePath, true, default);
            string scenario = _row.scenario;
            bool offline = scenario == "large-download-space-offline";
            bool spaceFault = scenario == "large-download-space-fault";
            bool small = offline || scenario == "large-download-space-prime";
            var byLength = description.Bundles.OrderBy(record => record.CipherLength);
            var target = small ? byLength.First() : byLength.Last();
            var evidence = _row.largeDownload = new LargeDownloadEvidence
            {
                expectedBytes = target.CipherLength, cipherSha256 = target.CipherSha256
            };
            using var keys = new Keys(Argument("-ylFrozenKey"), _row.scenario, peerKeyFile: OptionalArgument("-ylFrozenPeerKey"));
            var open = EncryptedDeliverySession.StartCoreAsync(cache, keys, true);
            yield return Until(() => open.IsCompleted);
            var session = open.GetAwaiter().GetResult();
            bool warm = scenario == "large-download-warm" || offline;
            string path = Path.Combine(cache, target.CipherSha256 + ".bin");
            Require(File.Exists(path) == warm, "Unexpected starting cache state.");
            if (offline)
            {
                var asset = Addressables.LoadAssetAsync<TextAsset>(SmallKey);
                try
                {
                    yield return Until(() => asset.IsDone);
                    double readyTime = 0;
                    ReadReady(asset, 8 * MiB, 97, ref readyTime);
                    evidence.termination = "OldAssetReadOffline";
                    evidence.keysOpened = keys.Opens;
                    evidence.mounts = session.Provider.Creates;
                    Require(evidence.keysOpened == 1 && evidence.mounts == 1, "The old offline asset did not authenticate and mount once.");
                }
                finally { if (asset.IsValid()) Addressables.Release(asset); }
                yield return Until(() => session.Provider.Mounted == 0 && session.Provider.Unloading == 0);
            }
            else
            {
                yield return ObserveColdDownload(session, evidence, target, path, small ? SmallKey : LargeKey, keys, spaceFault);
            }
            var close = session.CloseAsync();
            yield return Until(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            CheckKeyReturns(keys);
        }

        private IEnumerator ObserveColdDownload(EncryptedDeliverySession session, LargeDownloadEvidence evidence, EncryptedBundleRecord target,
            string path, string assetKey, Keys keys, bool spaceFault)
        {
            const int diskFull = unchecked((int)0x80070070);
            session.Cache.CiphertextReceived = (record, stage, bytes) =>
            {
                Require(record.CipherSha256 == target.CipherSha256, "The experiment received bytes from a different ciphertext.");
                string now = DateTime.UtcNow.ToString("o");
                evidence.firstUtc ??= now;
                evidence.lastUtc = now;
                evidence.receivedBytes += bytes;
                evidence.readCalls++;
                if (spaceFault && stage.Position >= MiB)
                {
                    stage.Flush(true);
                    evidence.stagedBytes = stage.Length;
                    evidence.writeHResult = diskFull;
                    throw new IOException("Fixture disk full at the next ciphertext write.", diskFull);
                }
            };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var download = Addressables.DownloadDependenciesAsync(assetKey, false);
            try
            {
                yield return Until(() => download.IsDone, 205);
                evidence.elapsedSeconds = watch.Elapsed.TotalSeconds;
                evidence.error = download.OperationException?.ToString();
                evidence.termination = download.Status == AsyncOperationStatus.Succeeded ? "Complete" :
                    (evidence.error?.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ? "TransferTimeout" : "TransferFailed");
                if (spaceFault)
                {
                    Require(evidence.termination == "TransferFailed" && evidence.writeHResult == diskFull &&
                        evidence.stagedBytes >= MiB && evidence.receivedBytes > evidence.stagedBytes &&
                        evidence.error.Contains("Fixture disk full at the next ciphertext write"),
                        "The ciphertext write did not fail after a flushed real prefix.");
                }
                bool found = File.Exists(path);
                Require(found == (evidence.termination == "Complete"), "Cache publication differs from transfer completion.");
                if (found)
                {
                    using var published = OwnedWindowsFiles.ReadPinned(path);
                    AuthenticatedBundleStream.ValidateCipher(published, target, default);
                }
                evidence.keysOpened = keys.Opens;
                evidence.mounts = session.Provider.Creates;
                Require(evidence.keysOpened == 0 && evidence.mounts == 0, "Protected prefetch initialized key or mount work.");
            }
            finally
            {
                session.Cache.CiphertextReceived = null;
                if (download.IsValid()) Addressables.Release(download);
            }
        }

        [DllImport("ylresource_tink", EntryPoint = "yl_diagnostic_buffer_delta", CallingConvention = CallingConvention.Cdecl)]
        private static extern void BufferDelta(int kind, long bytes);
        [DllImport("ylresource_tink", EntryPoint = "yl_diagnostic_buffer_phase", CallingConvention = CallingConvention.Cdecl)]
        private static extern void BufferPhase(int phase);
        [DllImport("ylresource_tink", EntryPoint = "yl_diagnostic_buffer_reset", CallingConvention = CallingConvention.Cdecl)]
        private static extern void BufferReset();
        [DllImport("ylresource_tink", EntryPoint = "yl_diagnostic_buffer_read", CallingConvention = CallingConvention.Cdecl)]
        private static extern void BufferRead([Out] long[] values, int count);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetModuleFileNameW(IntPtr module, StringBuilder path, int capacity);

        [Serializable]
        private sealed class BufferEvidence
        {
            public string nativeSha256, scope = "Managed hash payload, native bridge pages, upstream header and header-copy buffers, and segment payload vectors.";
            public string exclusions = "Unity read buffers, engine assets, CNG/crypto state, runtime bookkeeping.";
            public Calibration[] calibrations;
            public BufferWorkload[] cases;
            public long describedCipherBytes, preparationKeys, preparationMounts;
        }

        [Serializable]
        private sealed class Capacity
        {
            public long current, managed, bridge, upstream, currentReaders;
            public long peak, peakManaged, peakBridge, peakUpstream, peakReaders, phase, events;
        }

        [Serializable]
        private sealed class Calibration
        {
            public long x, y;
            public bool overlapping;
            public Capacity start, first, beforeSecond, second, afterFirst, end;
        }

        [Serializable]
        private sealed class BufferWorkload
        {
            public string name;
            public int readerCreations, mountedWhenHeld;
            public long singleReaderBound;
            public Capacity whenHeld, afterUnload;
        }

        private static Capacity ReadCapacity()
        {
            var values = new long[11];
            BufferRead(values, values.Length);
            return new Capacity
            {
                current = checked(values[0] + values[1] + values[2]), managed = values[0], bridge = values[1],
                upstream = values[2], peakManaged = values[3], peakBridge = values[4], peakUpstream = values[5],
                currentReaders = values[6], peakReaders = values[7], peak = values[8], phase = values[9], events = values[10]
            };
        }

        private static Calibration CalibrateBuffers(string small, string large, bool overlap)
        {
            using var firstAllocated = new ManualResetEventSlim();
            using var secondAllocated = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseSecond = new ManualResetEventSlim();
            using var sample = new ThreadLocal<int>();
            var evidence = new Calibration { overlapping = overlap };
            BufferReset();
            evidence.start = ReadCapacity();
            AuthenticatedBundleStream.HashBufferBoundary = (length, allocated) =>
            {
                BufferDelta(0, allocated ? length : -length);
                if (!allocated) return;
                if (sample.Value == 1)
                {
                    evidence.x = length;
                    firstAllocated.Set();
                    Require(releaseFirst.Wait(TimeSpan.FromSeconds(8)), "First calibration buffer was not released.");
                }
                else
                {
                    evidence.y = length;
                    secondAllocated.Set();
                    Require(releaseSecond.Wait(TimeSpan.FromSeconds(8)), "Second calibration buffer was not released.");
                }
            };
            Task first = null, second = null;
            try
            {
                first = Begin(small, 1);
                Require(firstAllocated.Wait(TimeSpan.FromSeconds(8)), "First real buffer was not allocated.");
                evidence.first = ReadCapacity();
                if (!overlap)
                {
                    releaseFirst.Set();
                    first.GetAwaiter().GetResult();
                }
                evidence.beforeSecond = ReadCapacity();
                second = Begin(large, 2);
                Require(secondAllocated.Wait(TimeSpan.FromSeconds(8)), "Second real buffer was not allocated.");
                evidence.second = ReadCapacity();
                releaseFirst.Set();
                first.GetAwaiter().GetResult();
                evidence.afterFirst = ReadCapacity();
                releaseSecond.Set();
                second.GetAwaiter().GetResult();
                evidence.end = ReadCapacity();
                long b = evidence.start.current;
                Require(evidence.x > 0 && evidence.y > 0 &&
                    evidence.first.current - b == evidence.x &&
                    evidence.second.current - b == (overlap ? evidence.x + evidence.y : evidence.y) &&
                    evidence.afterFirst.current - b == evidence.y && evidence.end.current == b &&
                    evidence.end.peak - b == (overlap ? evidence.x + evidence.y : Math.Max(evidence.x, evidence.y)) &&
                    evidence.end.events == 4, "Real buffer overlap did not calibrate.");
                return evidence;
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
                try { Task.WhenAll(first ?? Task.CompletedTask, second ?? Task.CompletedTask).GetAwaiter().GetResult(); }
                finally { AuthenticatedBundleStream.HashBufferBoundary = null; }
            }

            Task Begin(string path, int id) => Task.Run(() =>
            {
                sample.Value = id;
                using var contents = OwnedWindowsFiles.ReadPinned(path);
                AuthenticatedBundleStream.Hash(contents, default, clearBuffer: true);
            });
        }

        private IEnumerator MeasureBufferCapacity(bool injectPrefetchFault)
        {
            var evidence = _row.buffers = new BufferEvidence();
            BufferReset();
            var module = new StringBuilder(32768);
            int chars = GetModuleFileNameW(GetModuleHandleW("ylresource_tink.dll"), module, module.Capacity);
            Require(chars > 0 && chars < module.Capacity, "Cannot resolve the loaded diagnostics DLL.");
            using (var file = File.OpenRead(module.ToString()))
                evidence.nativeSha256 = AuthenticatedBundleStream.Hash(file, default);

            string cache = Argument("-ylFrozenCache");
            string gate = injectPrefetchFault ? Argument("-ylFrozenCacheGate") : null;
            var open = OpenExperimentSession(cache);
            yield return Until(() => open.IsCompleted);
            var session = open.GetAwaiter().GetResult();
            var preload = injectPrefetchFault ? Addressables.DownloadDependenciesAsync(SmallKey, false) :
                Addressables.DownloadDependenciesAsync(new[] { SmallKey, LargeKey }, Addressables.MergeMode.Union, false);
            try
            {
                if (injectPrefetchFault)
                {
                    yield return AwaitHeldCiphertextPrefix(cache, gate, preload, null);
                    Require(!preload.IsDone, "The fault requires a pending Addressables prefetch.");
                    throw new ExperimentPrefetchFault();
                }
                yield return Until(() => preload.IsDone);
                Require(preload.Status == AsyncOperationStatus.Succeeded, "Prefetch failed: " + preload.OperationException);
                Require(_experimentKeys.Opens == 0 && session.Provider.Creates == 0, "Prefetch invoked keys or mounts.");
            }
            finally
            {
                try { if (gate != null) ReleaseCacheGate(gate); }
                finally { if (preload.IsValid()) Addressables.Release(preload); }
            }

            var files = EncryptedDeliverySession.ReadShippedDescription(Addressables.RuntimePath, true, default).Bundles
                .OrderBy(record => record.CipherLength).ToArray();
            evidence.describedCipherBytes = files.Sum(record => record.CipherLength);
            evidence.preparationKeys = _experimentKeys.Opens;
            evidence.preparationMounts = session.Provider.Creates;

            string small = Path.Combine(cache, files[0].CipherSha256 + ".bin");
            string large = Path.Combine(cache, files[1].CipherSha256 + ".bin");
            var calibrate = Task.Run(() => new[] { CalibrateBuffers(small, large, false), CalibrateBuffers(small, large, true) });
            yield return Until(() => calibrate.IsCompleted);
            evidence.calibrations = calibrate.GetAwaiter().GetResult();
            AuthenticatedBundleStream.HashBufferBoundary = (length, added) => BufferDelta(0, added ? length : -length);
            AuthenticatedBundleStream.DiagnosticPhase = BufferPhase;
            evidence.cases = new[] { new BufferWorkload { name = "single" }, new BufferWorkload { name = "shared" },
                new BufferWorkload { name = "parallel" } };
            try
            {
                yield return MeasureCapacityCase(session, evidence.cases[0], null, 1);
                yield return MeasureCapacityCase(session, evidence.cases[1], SmallKey, 1);
                yield return MeasureCapacityCase(session, evidence.cases[2], LargeKey, 2);
            }
            finally
            {
                AuthenticatedBundleStream.HashBufferBoundary = null;
                AuthenticatedBundleStream.DiagnosticPhase = null;
            }
        }

        private static IEnumerator MeasureCapacityCase(EncryptedDeliverySession session, BufferWorkload sample,
            string secondKey, int expectedReaders)
        {
            BufferReset();
            Require(ReadCapacity().current == 0, "Previous payloads did not drain.");
            int creates = session.Provider.Creates;
            var first = Addressables.LoadAssetAsync<TextAsset>(SmallKey);
            AsyncOperationHandle<TextAsset> second = default;
            try
            {
                if (secondKey != null)
                    second = Addressables.LoadAssetAsync<TextAsset>(secondKey);
                yield return Until(() => first.IsDone && (secondKey == null || second.IsDone));
                double time = 0;
                ReadReady(first, 8 * MiB, 97, ref time);
                if (secondKey != null)
                {
                    time = 0;
                    ReadReady(second, secondKey == LargeKey ? 128 * MiB : 8 * MiB,
                        (byte)(secondKey == LargeKey ? 173 : 97), ref time);
                }
                if (secondKey == SmallKey) Require(first.Result == second.Result, "The second request did not share its Bundle.");
                sample.readerCreations = session.Provider.Creates - creates;
                sample.mountedWhenHeld = session.Provider.Mounted;
                sample.whenHeld = ReadCapacity();
                Require(sample.readerCreations == expectedReaders && sample.mountedWhenHeld == expectedReaders &&
                    sample.whenHeld.currentReaders == expectedReaders, "The held Bundle readers did not match the workload.");
                var bounds = new BundleMeasure { clearBytes = 8L * MiB };
                yield return InspectReader(SmallKey, bounds);
                sample.singleReaderBound = bounds.payloadBufferBound;
            }
            finally
            {
                if (second.IsValid()) Addressables.Release(second);
                Addressables.Release(first);
            }
            yield return Until(() => session.Provider.Mounted == 0 && session.Provider.Unloading == 0 && ReadCapacity().current == 0);
            sample.afterUnload = ReadCapacity();
            Require(sample.afterUnload.peak > 0 && sample.afterUnload.peakReaders == expectedReaders &&
                sample.afterUnload.peak == sample.afterUnload.peakManaged + sample.afterUnload.peakBridge + sample.afterUnload.peakUpstream,
                "The capacity trace missed a live reader or component of the peak.");
        }

        private IEnumerator Disabled()
        {
            Require(GetModuleHandleW("ylresource_tink.dll") == IntPtr.Zero && !File.Exists(Path.Combine(Addressables.RuntimePath, EncryptedBundleDescription.FileName)),
                "Installation alone must not activate the native plugin or frozen catalog path.");
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var starting = startup.StartAsync();
            yield return Until(() => starting.IsDone);
            var system = starting.TakeResult().Resources;
            using var scope = system.CreateScope("installed but disabled");
            using var load = scope.LoadAsync<TextAsset>(LocalKey);
            yield return Until(() => load.IsDone);
            _row.sentinel = load.TakeResult().text == "local:" + _token;
            Require(_row.sentinel && Addressables.InternalIdTransformFunc == null && !Addressables.ResourceManager.ResourceProviders.Any(provider => provider is EncryptedAssetBundleProvider),
                "The default source path touched encryption or changed ordinary loading.");
            using var close = scope.CloseAsync();
            yield return Until(() => close.IsDone);
            close.TakeResult();
            yield return Stop(startup);
            yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
            Require(GetModuleHandleW("ylresource_tink.dll") == IntPtr.Zero, "Ordinary assets triggered native encryption plugin initialization.");
        }

        private IEnumerator Performance(string player)
        {
            bool clear = _row.scenario.Contains("-plain-");
            bool warm = _row.scenario.Contains("-warm-");
            string cache = Path.Combine(player, clear ? "PerfPlain" : "PerfCipher");
            var evidence = _row.performance = new PerformanceEvidence { mode = _row.scenario };
            if (clear)
            {
                // The harness audits the stock files independently. This Player is not given the other installation's paths or keys.
                evidence.deliveryLayout = Argument("-ylFrozenLayout");
                bool hasRemote = evidence.deliveryLayout != "Embedded";
                Require((evidence.deliveryLayout is "Remote" or "Embedded" or "Mixed") &&
                    Directory.Exists(cache) == (warm && hasRemote), "Wrong stock performance install or cache state.");
                long download = long.Parse(Argument("-ylFrozenPlainDownloadBytes"), NumberStyles.None, CultureInfo.InvariantCulture);
                evidence.bundles = Array.Empty<BundleMeasure>();
                yield return PlainPerformance(cache, hasRemote, download, evidence);
                Require(!File.Exists(Path.Combine(Addressables.RuntimePath, EncryptedBundleDescription.FileName)) &&
                    GetModuleHandleW("ylresource_tink.dll") == IntPtr.Zero, "The stock control touched the encryption backend.");
                _row.keyLeasesReturned = true;
                yield break;
            }
            var desc = EncryptedDeliverySession.ReadShippedDescription(Addressables.RuntimePath, true, default);
            var sorted = desc.Bundles.OrderBy(row => row.Binding.PlainLength).ToArray();
            int remote = sorted.Count(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps);
            evidence.deliveryLayout = remote == sorted.Length ? "Remote" : remote == 0 ? "Embedded" : "Mixed";
            Require(Directory.Exists(cache) == (warm && remote > 0), "Performance cache starts empty for cold and persists only for remote warm processes.");
            Require(sorted.Length == 2 && sorted[0].Binding.PlainLength >= 8L * MiB && sorted[0].Binding.PlainLength < 9L * MiB &&
                sorted[1].Binding.PlainLength >= 128L * MiB && sorted[1].Binding.PlainLength < 130L * MiB,
                "Performance input must preserve two low-compressibility physical Bundles.");
            evidence.bundles = sorted.Select(row => new BundleMeasure { clearBytes = row.Binding.PlainLength,
                clearSha256 = row.Binding.PlainSha256, cipherBytes = row.CipherLength, cipherSha256 = row.CipherSha256 }).ToArray();
            using var keys = new Keys(Argument("-ylFrozenKey"), _row.scenario, peerKeyFile: OptionalArgument("-ylFrozenPeerKey"));
            evidence.bundleReadKind = "native-measured";
            evidence.nativeBufferEvidence = "source-bound";
            evidence.startGateKind = "frozen-catalog-and-cipher-cache";
            long gateCpu = MainThread100ns();
            var gateClock = System.Diagnostics.Stopwatch.StartNew();
            var open = EncryptedDeliverySession.StartCoreAsync(cache, keys, true);
            yield return MeasureUntil(() => open.IsCompleted, evidence);
            var session = open.GetAwaiter().GetResult();
            evidence.startGateMs = gateClock.Elapsed.TotalMilliseconds;
            evidence.startGateMainThreadCpuMs = (MainThread100ns() - gateCpu) / 10000d;
            if (evidence.deliveryLayout == "Mixed")
            {
                var prepareClock = System.Diagnostics.Stopwatch.StartNew();
                var prepare = session.PrepareRemoteAsync();
                yield return MeasureUntil(() => prepare.IsCompleted, evidence);
                prepare.GetAwaiter().GetResult();
                evidence.remotePrepareMs = prepareClock.Elapsed.TotalMilliseconds;
            }
            evidence.startGateCipherHashBytes = session.Cache?.VerifiedCipherHashBytes ?? 0;
            long remoteBytes = sorted.Where(row => row.Binding.Mode == EncryptedBundleDeliveryMode.RemoteHttps).Sum(row => row.CipherLength);
            yield return CheckPairSize(warm ? 0 : remoteBytes);
            evidence.preLoadKeys = keys.Opens;
            // The only full authentication path borrows a key before opening the reader.
            evidence.preLoadAuthentications = keys.Opens;
            Require(session.Provider.Creates == 0 && keys.Opens == 0, "Protected work started before business loading.");
            yield return MeasurePair(evidence, cache, session, sorted, warm);
            Require(session.Provider.Creates == 2 && keys.Opens == 2, "Measured operations did not have two concurrent owners.");
            var close = session.CloseAsync();
            yield return Until(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            Require((remote == 0 ? !Directory.Exists(cache) : Directory.EnumerateFiles(cache, "*.bin").Count() == remote && !Directory.EnumerateFiles(cache, "*.part").Any()) &&
                keys.Returns == 2 && session.Provider.Unloads == 2 && session.Provider.StreamsClosed == 2 && session.Provider.Unloading == 0,
                "Concurrent read cleanup did not leave just the expected ciphertexts.");
            yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
            CheckKeyReturns(keys);
            Require(evidence.cacheLogicalBytesAtCompletion == remoteBytes && CacheBytes(cache) == evidence.cacheLogicalBytesAtCompletion,
                "The dedicated cipher cache must contain only the retained remote ciphertexts.");
        }

        private IEnumerator PlainPerformance(string cache, bool remote, long expectedSize, PerformanceEvidence evidence)
        {
            evidence.bundleReadKind = "engine-opaque";
            evidence.startGateKind = "ordinary-unity-cache-readiness";
            long gateCpu = MainThread100ns();
            var gateClock = System.Diagnostics.Stopwatch.StartNew();
            var prior = Caching.currentCacheForWriting;
            if (remote)
            {
                yield return Until(() => Caching.ready);
                Directory.CreateDirectory(cache);
                var current = Caching.AddCache(cache);
                Require(current.valid, "Could not allocate the equivalent ordinary Unity cache.");
                Caching.currentCacheForWriting = current;
            }
            var initialized = Addressables.InitializeAsync(false);
            try
            {
                yield return MeasureUntil(() => initialized.IsDone, evidence);
                Require(initialized.Status == AsyncOperationStatus.Succeeded, "Stock Catalog initialization failed.");
                evidence.startGateMs = gateClock.Elapsed.TotalMilliseconds;
                evidence.startGateMainThreadCpuMs = (MainThread100ns() - gateCpu) / 10000d;
                yield return CheckPairSize(expectedSize);
                yield return MeasurePair(evidence, cache, null, null, false);
                yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
            }
            finally
            {
                Addressables.Release(initialized);
                if (remote) Caching.currentCacheForWriting = prior;
            }
        }

        private static IEnumerator CheckPairSize(long expected)
        {
            var size = Addressables.GetDownloadSizeAsync(new[] { SmallKey, LargeKey });
            try
            {
                yield return Until(() => size.IsDone);
                Require(size.Status == AsyncOperationStatus.Succeeded && size.Result == expected,
                    $"Cold and warm disk states were not distinct: expected {expected}, got {size.Result}.");
            }
            finally { Addressables.Release(size); }
        }

        private IEnumerator MeasurePair(PerformanceEvidence evidence, string cache, EncryptedDeliverySession session, EncryptedBundleRecord[] sorted, bool warm)
        {
            evidence.preLoadMounted = AssetBundle.GetAllLoadedAssetBundles().Count();
            Require(evidence.preLoadMounted == 0, "A Bundle was already mounted before the measurement.");
            var before = ReadMemory();
            long cpu = MainThread100ns();
            evidence.requestedAtMs = _performanceStartup.Elapsed.TotalMilliseconds;
            var first = Addressables.LoadAssetAsync<TextAsset>(SmallKey);
            AsyncOperationHandle<TextAsset> second = default;
            try
            {
                second = Addressables.LoadAssetAsync<TextAsset>(LargeKey);
                evidence.concurrent = !first.IsDone && !second.IsDone;
                yield return MeasureUntil(() =>
                {
                    ReadReady(first, 8 * MiB, 97, ref evidence.smallReadyAtMs);
                    ReadReady(second, 128 * MiB, 173, ref evidence.largeReadyAtMs);
                    return evidence.smallReadyAtMs > 0 && evidence.largeReadyAtMs > 0;
                }, evidence);
                evidence.firstAssetMs = Math.Min(evidence.smallReadyAtMs, evidence.largeReadyAtMs);
                evidence.bothAssetsMs = Math.Max(evidence.smallReadyAtMs, evidence.largeReadyAtMs);
                StopMeasurement(evidence, before, cpu);
                CaptureAssetsAndCache(evidence, cache, first.Result, second.Result);
                if (session != null)
                {
                    evidence.loadCipherHashBytes = (session.Cache?.VerifiedCipherHashBytes ?? 0) + session.EmbeddedCipherHashBytes - evidence.startGateCipherHashBytes;
                    evidence.bundles[0].officialDownloadBytes = CompletedDownloadBytes(first, warm || sorted[0].Binding.Mode == EncryptedBundleDeliveryMode.Embedded ? 0 : evidence.bundles[0].cipherBytes);
                    evidence.bundles[1].officialDownloadBytes = CompletedDownloadBytes(second, warm || sorted[1].Binding.Mode == EncryptedBundleDeliveryMode.Embedded ? 0 : evidence.bundles[1].cipherBytes);
                    yield return InspectReader(SmallKey, evidence.bundles[0]);
                    yield return InspectReader(LargeKey, evidence.bundles[1]);
                    Require(session.Provider.Mounted == 2, "Both authenticated Bundles must still have owners.");
                }
                _row.sentinel = true;
            }
            finally
            {
                if (second.IsValid()) Addressables.Release(second);
                Addressables.Release(first);
            }
        }

        private static IEnumerator InspectReader(string key, BundleMeasure row)
        {
            var assets = Addressables.LoadResourceLocationsAsync(key, typeof(TextAsset));
            AsyncOperationHandle<IAssetBundleResource> bundle = default;
            try
            {
                yield return Until(() => assets.IsDone);
                var mount = assets.Result.Single().Dependencies.Single(dep => dep.ResourceType == typeof(EncryptedMountedBundle));
                bundle = Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(mount);
                yield return Until(() => bundle.IsDone);
                var reader = ((EncryptedMountedBundle)bundle.Result).Reader;
                var stats = reader.ReadStatistics();
                row.readPlain = stats.Logical; row.readCipher = stats.Physical; row.pages = stats.Pages; row.payloadBufferBound = stats.PayloadBound;
                row.preAuthenticationPlainBytes = row.clearBytes; // OpenVerified authenticated and hashed the complete plaintext.
                row.preAuthenticationCipherBytes = reader.PreAuthenticationCipherBytes;
                row.cipherShaMs = reader.CipherShaMs;
                row.authenticationMs = reader.AuthenticationMs;
                row.engineReadPlain = stats.Logical - row.preAuthenticationPlainBytes;
                row.engineReadCipher = stats.Physical - row.preAuthenticationCipherBytes;
                row.unityReadBufferBytes = EncryptedAssetBundleProvider.UnityReadBufferBytes;
                Require(row.payloadBufferBound <= 4L * MiB && row.engineReadPlain > 0 && row.engineReadCipher > 0, "No separable native authentication/read counters.");
            }
            finally
            {
                if (bundle.IsValid()) Addressables.Release(bundle);
                Addressables.Release(assets);
            }
        }

        private static void ReadReady(AsyncOperationHandle<TextAsset> request, int length, byte sentinel, ref double readyAtMs)
        {
            if (readyAtMs > 0 || !request.IsDone) return;
            Require(request.Status == AsyncOperationStatus.Succeeded, "Paired business load failed: " + request.OperationException);
            var asset = request.Result;
            Require(asset, "A measured TextAsset was absent.");
            var buffer = asset.GetData<byte>();
            Require(buffer.Length == length && buffer[0] == sentinel && buffer[buffer.Length - 1] == 255 - sentinel, "Payload or sentinel differs.");
            readyAtMs = _performanceStartup.Elapsed.TotalMilliseconds;
        }

        private static IEnumerator MeasureUntil(Func<bool> finished, PerformanceEvidence evidence)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long sampledAt = -50;
            while (!finished())
            {
                Require(watch.Elapsed.TotalSeconds < 100, "Performance run did not settle.");
                if (watch.ElapsedMilliseconds - sampledAt >= 50)
                {
                    ObserveMemory(evidence);
                    sampledAt = watch.ElapsedMilliseconds;
                }
                evidence.largestObservedFrameMs = Math.Max(evidence.largestObservedFrameMs, Time.unscaledDeltaTime * 1000d);
                yield return null;
            }
        }

        private static void StopMeasurement(PerformanceEvidence evidence, ProcessCounters before, long cpu)
        {
            evidence.elapsedMs = evidence.bothAssetsMs - evidence.requestedAtMs;
            evidence.mainThreadCpuMs = (MainThread100ns() - cpu) / 10000d;
            evidence.largestObservedFrameMs = Math.Max(evidence.largestObservedFrameMs, Time.unscaledDeltaTime * 1000d);
            evidence.processPrivateBefore = (long)before.Private.ToUInt64();
            evidence.processWorkingSetBefore = (long)before.WorkingSet.ToUInt64();
            evidence.processPrivatePeak = Math.Max(evidence.processPrivatePeak, evidence.processPrivateBefore);
            evidence.processWorkingSetPeak = Math.Max(evidence.processWorkingSetPeak, evidence.processWorkingSetBefore);
            ObserveMemory(evidence);
        }

        private static void CaptureAssetsAndCache(PerformanceEvidence evidence, string cache, TextAsset small, TextAsset large)
        {
            evidence.cacheLogicalBytesAtCompletion = CacheBytes(cache);
            evidence.fixturePayloadBytes = 136L * MiB;
            long smallBytes = Profiler.GetRuntimeMemorySizeLong(small);
            long largeBytes = Profiler.GetRuntimeMemorySizeLong(large);
            if (smallBytes > 0 && largeBytes > 0) evidence.engineAssetReportedBytes = smallBytes + largeBytes;
            Require(evidence.concurrent && evidence.elapsedMs > 0 && evidence.processPrivatePeak > 0, "Missing performance facts.");
        }

        private static void ObserveMemory(PerformanceEvidence evidence)
        {
            var now = ReadMemory();
            evidence.processPrivatePeak = Math.Max(evidence.processPrivatePeak, (long)now.Private.ToUInt64());
            evidence.processWorkingSetPeak = Math.Max(evidence.processWorkingSetPeak, (long)now.WorkingSet.ToUInt64());
        }

        private static long CacheBytes(string cache) => Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;

        private static long MainThread100ns()
        {
            Require(GetThreadTimes(GetCurrentThread(), out _, out _, out long system, out long user), "Cannot measure Win64 main-thread CPU.");
            return system + user;
        }

        private static ProcessCounters ReadMemory()
        {
            var counters = new ProcessCounters { Size = (uint)Marshal.SizeOf<ProcessCounters>() };
            Require(GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size), "Cannot read Win64 process memory.");
            return counters;
        }
    }
}
