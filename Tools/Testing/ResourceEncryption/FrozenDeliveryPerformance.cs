using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
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

        [Serializable]
        private sealed class PerformanceEvidence
        {
            public string mode, startGateKind, bundleReadKind, enginePeakMemory = "unavailable";
            public bool concurrent;
            public double startGateMs, startGateMainThreadCpuMs, elapsedMs, mainThreadCpuMs, largestObservedFrameMs;
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
            public long readPlain = -1, readCipher = -1, pages = -1, payloadBufferBound = -1, engineReadPlain = -1, engineReadCipher = -1;
            public long preAuthenticationCipherBytes = -1, preAuthenticationPlainBytes = -1, unityReadBufferBytes = -1;
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
            Require(Directory.Exists(cache) == warm, "Performance cache roots must begin empty for cold and persist across warm processes.");
            var evidence = _row.performance = new PerformanceEvidence { mode = _row.scenario };
            var desc = EncryptedDeliverySession.ReadShippedDescription(Addressables.RuntimePath, true, default);
            var sorted = desc.Bundles.OrderBy(row => row.Binding.PlainLength).ToArray();
            Require(sorted.Length == 2 && sorted[0].Binding.PlainLength >= 8L * MiB && sorted[0].Binding.PlainLength < 9L * MiB &&
                sorted[1].Binding.PlainLength >= 128L * MiB && sorted[1].Binding.PlainLength < 130L * MiB,
                "Performance input must preserve two low-compressibility physical Bundles.");
            evidence.bundles = sorted.Select(row => new BundleMeasure { clearBytes = row.Binding.PlainLength,
                clearSha256 = row.Binding.PlainSha256, cipherBytes = row.CipherLength, cipherSha256 = row.CipherSha256 }).ToArray();
            _row.catalogSha256 = desc.CatalogSha256;
            using var keys = new Keys(Path.GetFullPath(Path.Combine(player, "..", "fixture.key")), _row.scenario);
            if (clear)
            {
                yield return PlainPerformance(sorted, cache, warm, evidence);
                CheckKeyReturns(keys);
                yield break;
            }
            evidence.bundleReadKind = "native-measured";
            evidence.startGateKind = "frozen-catalog-and-cipher-cache";
            long gateCpu = MainThread100ns();
            var gateClock = System.Diagnostics.Stopwatch.StartNew();
            var open = EncryptedDeliverySession.StartCoreAsync(cache, keys, true);
            yield return Until(() => open.IsCompleted);
            var session = open.GetAwaiter().GetResult();
            evidence.startGateMs = gateClock.Elapsed.TotalMilliseconds;
            evidence.startGateMainThreadCpuMs = (MainThread100ns() - gateCpu) / 10000d;
            evidence.startGateCipherHashBytes = session.Cache.VerifiedCipherHashBytes;
            var size = Addressables.GetDownloadSizeAsync(new[] { SmallKey, LargeKey });
            yield return Until(() => size.IsDone);
            try
            {
                Require(size.Status == AsyncOperationStatus.Succeeded && size.Result == (warm ? 0 : sorted.Sum(bundle => bundle.CipherLength)),
                    "Cold and warm disk states were not distinct.");
            }
            finally { Addressables.Release(size); }
            var before = ReadMemory();
            long cpu = MainThread100ns();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = Addressables.LoadAssetAsync<TextAsset>(SmallKey);
            var second = Addressables.LoadAssetAsync<TextAsset>(LargeKey);
            evidence.concurrent = !first.IsDone && !second.IsDone;
            try
            {
                yield return MeasureUntil(() => first.IsDone && second.IsDone, evidence);
                StopMeasurement(evidence, before, cpu, clock);
                evidence.loadCipherHashBytes = session.Cache.VerifiedCipherHashBytes - evidence.startGateCipherHashBytes;
                Require(first.Status == AsyncOperationStatus.Succeeded && second.Status == AsyncOperationStatus.Succeeded,
                    "Two protected Bundles must load concurrently: " + first.OperationException + "; " + second.OperationException);
                AssertPayload(first.Result, 8 * MiB, 97);
                AssertPayload(second.Result, 128 * MiB, 173);
                CaptureAssetsAndCache(evidence, cache, first.Result, second.Result);
                yield return InspectReader(SmallKey, evidence.bundles[0]);
                yield return InspectReader(LargeKey, evidence.bundles[1]);
                Require(session.Provider.Mounted == 2 && session.Provider.Creates == 2 && keys.Opens == 2, "Measured operations did not have two concurrent owners.");
                _row.sentinel = true;
            }
            finally { Addressables.Release(second); Addressables.Release(first); }
            yield return Until(() => session.Provider.Unloading == 0 && !AssetBundle.GetAllLoadedAssetBundles().Any());
            var close = session.CloseAsync();
            yield return Until(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            Require(Directory.EnumerateFiles(cache, "*.bin").Count() == 2 && !Directory.EnumerateFiles(cache, "*.part").Any() && keys.Returns == 2 && session.Provider.Unloads == 2,
                "Concurrent read cleanup did not leave just the expected ciphertexts.");
            CheckKeyReturns(keys);
            Require(evidence.cacheLogicalBytesAtCompletion == sorted.Sum(bundle => bundle.CipherLength) && CacheBytes(cache) == evidence.cacheLogicalBytesAtCompletion,
                "The dedicated cipher cache must contain only the two retained ciphertexts.");
            // Successful writes grow a single part per Bundle and atomically rename it without a second copy.
            evidence.cacheLogicalPeak = evidence.cacheLogicalBytesAtCompletion;
        }

        private IEnumerator PlainPerformance(EncryptedBundleRecord[] sorted, string cache, bool warm, PerformanceEvidence evidence)
        {
            evidence.bundleReadKind = "engine-opaque";
            evidence.startGateKind = "ordinary-unity-cache-readiness";
            long gateCpu = MainThread100ns();
            var gateClock = System.Diagnostics.Stopwatch.StartNew();
            yield return Until(() => Caching.ready);
            Directory.CreateDirectory(cache);
            var prior = Caching.currentCacheForWriting;
            var current = Caching.AddCache(cache);
            Require(current.valid, "Could not allocate the equivalent ordinary Unity cache.");
            Caching.currentCacheForWriting = current;
            var versions = sorted.Select(row => new CachedAssetBundle("perf-control-" + row.Binding.BundleName, Hash128.Parse(row.Binding.BundleHash))).ToArray();
            Require(versions.All(version => Caching.IsVersionCached(version) == warm), "Ordinary cold/warm cache selection differs.");
            evidence.startGateMs = gateClock.Elapsed.TotalMilliseconds;
            evidence.startGateMainThreadCpuMs = (MainThread100ns() - gateCpu) / 10000d;
            string Url(EncryptedBundleRecord row) => new Uri(new Uri(row.Binding.ReleasePrefix), "../p/" + row.Binding.RelativePath).AbsoluteUri;
            using var first = UnityWebRequestAssetBundle.GetAssetBundle(Url(sorted[0]), versions[0]);
            using var second = UnityWebRequestAssetBundle.GetAssetBundle(Url(sorted[1]), versions[1]);
            var before = ReadMemory();
            long cpu = MainThread100ns();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var a = first.SendWebRequest();
            var b = second.SendWebRequest();
            evidence.concurrent = !a.isDone && !b.isDone;
            AssetBundle small = null, large = null;
            try
            {
                yield return MeasureUntil(() => a.isDone && b.isDone, evidence);
                Require(first.result == UnityWebRequest.Result.Success && second.result == UnityWebRequest.Result.Success,
                    "Stock Bundle download control failed: " + first.error + "; " + second.error);
                small = DownloadHandlerAssetBundle.GetContent(first);
                large = DownloadHandlerAssetBundle.GetContent(second);
                Require(small && large, "Stock Bundle control mounted no content.");
                // Internal IDs may be GUIDs on the binary catalog profile. Each physical fixture Bundle contains one explicit asset.
                var x = small.LoadAssetAsync<TextAsset>(small.GetAllAssetNames().Single());
                var y = large.LoadAssetAsync<TextAsset>(large.GetAllAssetNames().Single());
                yield return MeasureUntil(() => x.isDone && y.isDone, evidence);
                StopMeasurement(evidence, before, cpu, clock);
                var assetA = x.asset as TextAsset;
                var assetB = y.asset as TextAsset;
                AssertPayload(assetA, 8 * MiB, 97);
                AssertPayload(assetB, 128 * MiB, 173);
                CaptureAssetsAndCache(evidence, cache, assetA, assetB);
                _row.sentinel = true;
                Require(versions.All(version => Caching.IsVersionCached(version)), "Stock control did not cache the exact source versions.");
            }
            finally
            {
                if (small) small.Unload(true);
                if (large) large.Unload(true);
                Caching.currentCacheForWriting = prior;
            }
            yield return Until(() => !AssetBundle.GetAllLoadedAssetBundles().Any());
        }

        private static IEnumerator InspectReader(string key, BundleMeasure row)
        {
            var assets = Addressables.LoadResourceLocationsAsync(key, typeof(TextAsset));
            yield return Until(() => assets.IsDone);
            var mount = assets.Result.Single().Dependencies.Single(dep => dep.ResourceType == typeof(EncryptedMountedBundle));
            var bundle = Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(mount);
            yield return Until(() => bundle.IsDone);
            try
            {
                var reader = ((EncryptedMountedBundle)bundle.Result).Reader;
                var stats = reader.ReadStatistics();
                row.readPlain = stats.Logical; row.readCipher = stats.Physical; row.pages = stats.Pages; row.payloadBufferBound = stats.PayloadBound;
                row.preAuthenticationPlainBytes = row.clearBytes; // OpenVerified authenticated and hashed the complete plaintext.
                row.preAuthenticationCipherBytes = reader.PreAuthenticationCipherBytes;
                row.engineReadPlain = stats.Logical - row.preAuthenticationPlainBytes;
                row.engineReadCipher = stats.Physical - row.preAuthenticationCipherBytes;
                row.unityReadBufferBytes = EncryptedAssetBundleProvider.UnityReadBufferBytes;
                Require(row.payloadBufferBound <= 4L * MiB && row.engineReadPlain > 0 && row.engineReadCipher > 0, "No separable native authentication/read counters.");
            }
            finally { Addressables.Release(bundle); Addressables.Release(assets); }
        }

        private static void AssertPayload(TextAsset asset, int length, byte sentinel)
        {
            Require(asset, "A measured TextAsset was absent.");
            var buffer = asset.GetData<byte>();
            Require(buffer.Length == length && buffer[0] == sentinel && buffer[buffer.Length - 1] == 255 - sentinel, "Payload or sentinel differs.");
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

        private static void StopMeasurement(PerformanceEvidence evidence, ProcessCounters before, long cpu, System.Diagnostics.Stopwatch clock)
        {
            clock.Stop();
            evidence.elapsedMs = clock.Elapsed.TotalMilliseconds;
            evidence.mainThreadCpuMs = (MainThread100ns() - cpu) / 10000d;
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
            Require(evidence.concurrent && evidence.elapsedMs > 0 && evidence.processPrivatePeak > 0 && evidence.cacheLogicalBytesAtCompletion > 0, "Missing performance facts.");
        }

        private static void ObserveMemory(PerformanceEvidence evidence)
        {
            var now = ReadMemory();
            evidence.processPrivatePeak = Math.Max(evidence.processPrivatePeak, (long)now.Private.ToUInt64());
            evidence.processWorkingSetPeak = Math.Max(evidence.processWorkingSetPeak, (long)now.WorkingSet.ToUInt64());
        }

        private static long CacheBytes(string cache) => Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

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
