#if UNITY_EDITOR || YL_RESOURCE_ADDRESSABLES_ACCEPTANCE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource;

namespace YLFramework.Acceptance
{
    public sealed partial class ResourceAddressablesPlayerProbe
    {
        [Serializable]
        private sealed class HeldFixture
        {
            public string buildToken, catalog, catalogSha256, protectedPath, deliveredSha256;
            public long deliveredBytes;
        }

        [Serializable]
        private sealed class HeldDownloadEvidence
        {
            public string scenario, mode, catalogSha256, provider, transferStatus, officialStatus, ylStatus;
            public int before, afterDownload, whileAssetAlive, final, leasesAfterDownload, leasesAtEnd, downloadProvides, realProvides, createCalls, releases;
            public long deliveredBytes;
            public bool sawGate, realStartedBeforeRelease, cancelledBeforeRelease, heldDuringLoad, realSentinel, downloadReleasedFirst;
        }

        private IEnumerator HeldDownloadHandle_DoesNotSatisfyAssetLoad()
        {
            int round = _report.round;
            var row = _report.heldDownload = new HeldDownloadEvidence
            {
                scenario = new[] { "only", "overlap", "completed-held", "cancellation" }[round - 1], mode = _build.heldDownload
            };
            string definition = Path.Combine(_build.playerRoot, "p0-held.json");
            RejectLinks(definition);
            var fixture = JsonUtility.FromJson<HeldFixture>(File.ReadAllText(definition));
            Require(fixture != null && fixture.buildToken == _build.buildToken && fixture.protectedPath == Path.GetFileName(fixture.protectedPath) &&
                fixture.catalog == (_build.addressablesVersion == "2.10.3" ? "catalog.bin" : "catalog.json") && fixture.deliveredBytes > 0,
                "The P0-B fixture does not identify the shipped remote bundle.");
            row.catalogSha256 = FileDigest(Path.Combine(Addressables.RuntimePath, fixture.catalog));
            Require(row.catalogSha256 == fixture.catalogSha256, "P0-B did not open the shipped default catalog.");
            row.deliveredBytes = fixture.deliveredBytes;
            string gate = Path.Combine(_build.playerRoot, "P0Gates", "round-" + round);
            var previous = Addressables.InternalIdTransformFunc;
            Require(previous == null, "Expected sole control of the isolated loopback transform.");
            Addressables.InternalIdTransformFunc = location => location.InternalId.StartsWith(RemotePrefix, StringComparison.Ordinal)
                ? _baseUrl + location.InternalId.Substring(RemotePrefix.Length) + "?round=" + round : location.InternalId;
            AsyncOperationHandle<IList<IAssetBundleResource>> transfer = default;
            AsyncOperationHandle<TextAsset> official = default;
            Cache originalCache = default;
            try
            {
                Debug.Log("[P0-B] Start " + row.scenario);
                yield return Until(() => Caching.ready);
                string cachePath = Path.Combine(_build.playerRoot, "P0UnityCache-" + round);
                Require(!Directory.Exists(cachePath), "A stock control must use a fresh per-process Unity cache.");
                Directory.CreateDirectory(cachePath);
                originalCache = Caching.currentCacheForWriting;
                Cache probeCache = Caching.AddCache(cachePath);
                Require(probeCache.valid, "Could not register the ordinary Unity cache control.");
                Caching.currentCacheForWriting = probeCache;
                _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                using var beginning = _startup.StartAsync();
                yield return Wait(beginning);
                _system = beginning.TakeResult().Resources;
                var locations = Addressables.LoadResourceLocationsAsync(SizeProtectedA, typeof(TextAsset));
                try
                {
                    yield return WaitHandle(locations);
                    var location = locations.Result.Single().Dependencies.Single(dep => dep.InternalId == RemotePrefix + fixture.protectedPath);
                    row.provider = location.ProviderId;
                    ConfigureHeldCandidate(location, fixture, round);
                }
                finally { Addressables.Release(locations); }

                row.before = NativeBundles();
                Require(row.before == 0 && _system.GetDiagnostics().Leases.Count == 0, "P0-B must begin with zero loaded bundles and leases.");
                using var scope = _system.CreateScope("held download probe");
                using var cancellation = new CancellationTokenSource();
                using var cancelled = round == 4 ? _system.DownloadDependenciesAsync(new[] { SizeProtectedA }, cancellation.Token) : null;
                transfer = Addressables.DownloadDependenciesAsync(SizeProtectedA, autoReleaseHandle: false).Convert<IList<IAssetBundleResource>>();
                Debug.Log("[P0-B] Download issued " + round + " " + transfer.Status);
                if (round == 2 || round == 4)
                {
                    float deadline = Time.realtimeSinceStartup + 8f;
                    yield return Until(() => File.Exists(gate + ".arrived") || transfer.IsDone || Time.realtimeSinceStartup >= deadline);
                    Require(File.Exists(gate + ".arrived"), "No request reached the response gate: transfer=" + transfer.Status + " " + transfer.OperationException);
                    row.sawGate = true;
                    Require(!transfer.IsDone && NativeBundles() == 0, "The server gate must hold a genuinely in-flight predownload with no mounted Bundle.");
                }
                else
                {
                    yield return Until(() => transfer.IsDone);
                    row.transferStatus = transfer.Status.ToString();
                    row.afterDownload = NativeBundles();
                    row.leasesAfterDownload = _system.GetDiagnostics().Leases.Count;
                    Require(row.leasesAfterDownload == 0, "Download-only created a business lease.");
                }

                if (round != 1)
                {
                    official = Addressables.LoadAssetAsync<TextAsset>(SizeProtectedA);
                    using var yn = scope.LoadAsync<TextAsset>(SizeProtectedB);
                    if (row.sawGate)
                    {
                        row.realStartedBeforeRelease = transfer.IsValid() && !transfer.IsDone;
                        Require(row.realStartedBeforeRelease, "Overlap must issue a real official load before releasing the response.");
                        if (cancelled != null)
                        {
                            cancellation.Cancel();
                            yield return Until(() => cancelled.IsDone);
                            row.cancelledBeforeRelease = cancelled.Status == ResourceRequestStatus.Canceled && transfer.IsValid() && !transfer.IsDone;
                            Require(row.cancelledBeforeRelease, "One YL download observer must cancel while official work is still held.");
                        }
                        using (File.Open(gate + ".release", FileMode.CreateNew)) { }
                    }
                    yield return Until(() => transfer.IsDone && official.IsDone && yn.IsDone);
                    row.transferStatus = transfer.Status.ToString();
                    row.officialStatus = official.Status.ToString();
                    row.ylStatus = yn.Status.ToString();
                    row.heldDuringLoad = transfer.IsValid();
                    row.realSentinel = official.Status == AsyncOperationStatus.Succeeded && official.Result && official.Result.text == "SizeA:" + _build.buildToken &&
                        yn.Status == ResourceRequestStatus.Succeeded && yn.TakeResult() is TextAsset value && value && value.text == "SizeB:" + _build.buildToken;
                    row.whileAssetAlive = NativeBundles();
                    if (round != 3)
                    {
                        Addressables.Release(transfer);
                        transfer = default;
                        row.downloadReleasedFirst = true;
                        if (row.realSentinel) Require(official.Result.text == "SizeA:" + _build.buildToken && NativeBundles() == 1,
                            "Releasing only the download handle released the real owner.");
                    }
                    Addressables.Release(official);
                    official = default;
                    using var closing = scope.CloseAsync();
                    yield return Wait(closing);
                    closing.TakeResult();
                    if (round == 3 && row.realSentinel)
                    {
                        yield return Until(() => NativeBundles() == 0);
                        Require(transfer.IsValid(), "Releasing real owners unexpectedly invalidated the held transfer.");
                    }
                }
                if (transfer.IsValid()) Addressables.Release(transfer);
                transfer = default;
                using var ending = _startup.StopAsync();
                yield return Wait(ending);
                Require(ending.TakeResult(), "The YL owner did not drain.");
                yield return Until(() => NativeBundles() == 0);
                row.final = NativeBundles();
                row.leasesAtEnd = _system.GetDiagnostics().Leases.Count;
                CompleteHeldCandidate(row);
                Require(row.transferStatus == "Succeeded" && (round == 1 ? row.afterDownload == 0 : row.realSentinel && row.heldDuringLoad && row.whileAssetAlive == 1),
                    "HeldDownloadHandle_DoesNotSatisfyAssetLoad: " + row.scenario + " has incorrect transfer, mount or sentinel behavior.");
                _checks.Add(nameof(HeldDownloadHandle_DoesNotSatisfyAssetLoad));
            }
            finally
            {
                if (official.IsValid()) Addressables.Release(official);
                if (transfer.IsValid()) Addressables.Release(transfer);
                Addressables.InternalIdTransformFunc = previous;
                if (originalCache.valid) Caching.currentCacheForWriting = originalCache;
            }
        }

        private static int NativeBundles() => AssetBundle.GetAllLoadedAssetBundles().Count();
        partial void ConfigureHeldCandidate(IResourceLocation location, HeldFixture fixture, int round);
        partial void CompleteHeldCandidate(HeldDownloadEvidence row);
    }
}
#endif
