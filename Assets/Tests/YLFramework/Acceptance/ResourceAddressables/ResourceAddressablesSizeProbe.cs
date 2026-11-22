#if UNITY_EDITOR || YL_RESOURCE_ADDRESSABLES_ACCEPTANCE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using YLFramework.Resource;

namespace YLFramework.Acceptance
{
    public sealed partial class ResourceAddressablesPlayerProbe
    {
        public const string SizeMixedLabel = "acceptance/p0/mixed";
        public const string SizeProtectedA = "acceptance/p0/protected-a";
        public const string SizeProtectedB = "acceptance/p0/protected-b";

        [Serializable]
        private sealed class SizeFixture
        {
            public string buildToken, catalog, catalogSha256, protectedPath, ordinaryPath, cipherSha256;
            public long protectedPlainBytes, cipherBytes, ordinaryBytes;
        }

        [Serializable]
        private sealed class SizeExperimentEvidence
        {
            public string catalog, catalogSha256, locatorType;
            public SizeDependency protectedBundle, ordinaryBundle;
            public SizeQuery[] queries;
            public int assetsWithProtectedDependency;
            public bool ordinarySentinel;
        }

        [Serializable]
        private sealed class SizeDependency
        {
            public string primaryKey, internalId, providerId, dataType, locationType, bundleName, hash;
            public long catalogSize;
        }

        [Serializable]
        private sealed class SizeQuery
        {
            public string state, query, cacheSha256;
            public bool verifiedCipher, ordinaryUnityCache;
            public long expected, official, yl, cacheBytes;
        }

        private IEnumerator DownloadSize_UsesShippedCipherLengthAndVerifiedBytes()
        {
            Require(_report.round == 1, "P0-A uses one dedicated Player process per catalog.");
            string description = Path.Combine(_build.playerRoot, "p0-size.json");
            RejectLinks(description);
            var fixture = JsonUtility.FromJson<SizeFixture>(File.ReadAllText(description));
            Require(fixture != null && fixture.buildToken == _build.buildToken && fixture.protectedPlainBytes > 0 &&
                fixture.cipherBytes > fixture.protectedPlainBytes && fixture.ordinaryBytes > 0 &&
                fixture.catalog == (_build.addressablesVersion == "2.10.3" ? "catalog.bin" : "catalog.json") &&
                fixture.protectedPath == Path.GetFileName(fixture.protectedPath) && fixture.ordinaryPath == Path.GetFileName(fixture.ordinaryPath) &&
                fixture.protectedPath != fixture.ordinaryPath && fixture.cipherSha256?.Length == 64,
                "The isolated cipher fixture is not the matching frozen deployment description.");
            string catalog = Path.Combine(Addressables.RuntimePath, fixture.catalog);
            var catalogCheck = Task.Run(() => FileDigest(catalog));
            yield return Until(() => catalogCheck.IsCompleted);
            string catalogHash = catalogCheck.GetAwaiter().GetResult();
            Require(catalogHash == fixture.catalogSha256, "The shipped default catalog changed.");

            yield return Until(() => Caching.ready);
            string officialRoot = Path.Combine(_build.playerRoot, "Cache");
            string cipherRoot = Path.Combine(_build.playerRoot, "CipherCache");
            Require(!Directory.Exists(officialRoot) && !Directory.Exists(cipherRoot), "P0-A requires an empty owned cache.");
            Directory.CreateDirectory(officialRoot);
            Directory.CreateDirectory(cipherRoot);
            string cipherFile = Path.Combine(cipherRoot, fixture.cipherSha256 + ".bin");
            var oldCache = Caching.currentCacheForWriting;
            var cache = Caching.AddCache(officialRoot);
            Require(cache.valid, "Failed to create the ordinary group control cache.");
            Caching.currentCacheForWriting = cache;
            var previous = Addressables.InternalIdTransformFunc;
            Require(previous == null, "The owned probe must control its loopback ID transform.");
            string protectedId = RemotePrefix + fixture.protectedPath;
            string readyCipher = null;
            Addressables.InternalIdTransformFunc = location =>
            {
                if (readyCipher != null && location.InternalId == protectedId) return readyCipher;
                return location.InternalId.StartsWith(RemotePrefix, StringComparison.Ordinal)
                    ? _baseUrl + location.InternalId.Substring(RemotePrefix.Length) + "?round=1"
                    : location.InternalId;
            };
            var observations = new List<SizeQuery>();
            var evidence = _report.sizeExperiment = new SizeExperimentEvidence { catalog = fixture.catalog, catalogSha256 = catalogHash };
            try
            {
                _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                using var starting = _startup.StartAsync();
                yield return Wait(starting);
                Require(ReferenceEquals(starting.TakeResult(), _startup), "P0-A did not start the intended YL owner.");
                _system = _startup.Resources;
                evidence.locatorType = Addressables.ResourceLocators.Single(locator => locator.Locate(SizeMixedLabel, typeof(TextAsset), out _)).GetType().FullName;
                var locationHandle = Addressables.LoadResourceLocationsAsync(SizeMixedLabel, typeof(TextAsset));
                CachedAssetBundle ordinaryVersion;
                try
                {
                    yield return WaitHandle(locationHandle);
                    Require(locationHandle.Result.Count == 4, "The mixed label must resolve two protected, one ordinary remote and one local asset.");
                    string ordinaryId = RemotePrefix + fixture.ordinaryPath;
                    evidence.assetsWithProtectedDependency = locationHandle.Result.Count(location => location.Dependencies.Any(dep => dep.InternalId == protectedId));
                    Require(evidence.assetsWithProtectedDependency == 2, "The two assets must have one shared protected Bundle dependency.");
                    var dependencies = locationHandle.Result.SelectMany(location => location.Dependencies)
                        .Where(location => location.InternalId.StartsWith(RemotePrefix, StringComparison.Ordinal)).ToArray();
                    Require(dependencies.Select(location => location.InternalId).Distinct().Count() == 2, "Mixed label resolved an unexpected remote Bundle.");
                    evidence.protectedBundle = ObserveSizeDependency(dependencies.First(location => location.InternalId == protectedId));
                    evidence.ordinaryBundle = ObserveSizeDependency(dependencies.First(location => location.InternalId == ordinaryId));
                    Require(evidence.protectedBundle.catalogSize == (_build.verifiedSizeSnapshot ? fixture.cipherBytes : fixture.protectedPlainBytes) &&
                        evidence.ordinaryBundle.catalogSize == fixture.ordinaryBytes, "Shipped options differ from the actual source/delivery file length.");
                    ordinaryVersion = new CachedAssetBundle(evidence.ordinaryBundle.bundleName, Hash128.Parse(evidence.ordinaryBundle.hash));
                }
                finally { Addressables.Release(locationHandle); }

                string seed = Path.Combine(_build.playerRoot, "P0Transfer", fixture.protectedPath);
                foreach (var state in new[] { "Empty", "SameLengthCorrupt", "VerifiedCipher", "BothCaches" })
                {
                    if (state == "BothCaches")
                    {
                        var plain = Addressables.LoadAssetAsync<TextAsset>(CachedKey);
                        try
                        {
                            yield return WaitHandle(plain);
                            evidence.ordinarySentinel = plain.Result && plain.Result.text == "cached:" + _build.buildToken;
                            Require(evidence.ordinarySentinel, "The ordinary remote control sentinel differs.");
                        }
                        finally { Addressables.Release(plain); }
                    }
                    var check = Task.Run(() =>
                    {
                        if (state == "SameLengthCorrupt" || state == "VerifiedCipher")
                        {
                            RejectLinks(seed);
                            RejectLinks(cipherFile);
                            File.Copy(seed, cipherFile, state == "VerifiedCipher");
                            if (state == "SameLengthCorrupt")
                            {
                                using var tampered = new FileStream(cipherFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                                byte value = checked((byte)tampered.ReadByte());
                                tampered.Position = 0;
                                tampered.WriteByte((byte)(value ^ 1));
                            }
                        }
                        if (!File.Exists(cipherFile)) return (Bytes: 0L, Digest: "");
                        RejectLinks(cipherFile);
                        using var input = new FileStream(cipherFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var sha = SHA256.Create();
                        return (Bytes: input.Length, Digest: Hex(sha.ComputeHash(input)));
                    });
                    yield return Until(() => check.IsCompleted);
                    var snapshot = check.GetAwaiter().GetResult();
                    bool verified = snapshot.Bytes == fixture.cipherBytes && snapshot.Digest == fixture.cipherSha256;
                    Require(verified == (state == "VerifiedCipher" || state == "BothCaches") &&
                        snapshot.Bytes == (state == "Empty" ? 0L : fixture.cipherBytes), "Cipher snapshot calibration failed.");
                    // Official ComputeSize sees an actual local file, not a fabricated Unity bundle-cache hit.
                    readyCipher = _build.verifiedSizeSnapshot && verified ? cipherFile : null;
                    bool ordinaryCached = Caching.IsVersionCached(ordinaryVersion);
                    Require(ordinaryCached == (state == "BothCaches"), "The ordinary Unity cache control did not match its stage.");
                    long cipherMissing = verified ? 0 : fixture.cipherBytes;
                    long ordinaryMissing = ordinaryCached ? 0 : fixture.ordinaryBytes;
                    yield return Measure(new[] { SizeProtectedA }, "protected", cipherMissing);
                    yield return Measure(new[] { CachedKey }, "ordinary", ordinaryMissing);
                    yield return Measure(new[] { SizeMixedLabel }, "mixed-label", cipherMissing + ordinaryMissing);
                    yield return Measure(new[] { SizeMixedLabel, SizeProtectedA, SizeProtectedB }, "repeated-dependency", cipherMissing + ordinaryMissing);

                    IEnumerator Measure(string[] keys, string query, long expected)
                    {
                        var official = Addressables.GetDownloadSizeAsync(keys);
                        long value;
                        try { yield return WaitHandle(official); value = official.Result; }
                        finally { Addressables.Release(official); }
                        using var yl = _system.GetDownloadSizeAsync(keys);
                        yield return Wait(yl);
                        observations.Add(new SizeQuery { state = state, query = query, expected = expected, official = value, yl = yl.TakeResult(),
                            verifiedCipher = verified, ordinaryUnityCache = ordinaryCached, cacheBytes = snapshot.Bytes, cacheSha256 = snapshot.Digest });
                        evidence.queries = observations.ToArray();
                    }
                }

                using var stopping = _startup.StopAsync();
                yield return Wait(stopping);
                Require(stopping.TakeResult(), "P0-A YL startup owner did not stop.");
                Require(observations.All(row => row.official == row.expected && row.yl == row.expected),
                    "DownloadSize_UsesShippedCipherLengthAndVerifiedBytes: " + string.Join("; ", observations
                        .Where(row => row.official != row.expected || row.yl != row.expected)
                        .Select(row => $"{row.state}/{row.query} expected={row.expected} official={row.official} yl={row.yl}")));
                _checks.Add(nameof(DownloadSize_UsesShippedCipherLengthAndVerifiedBytes));
            }
            finally
            {
                Addressables.InternalIdTransformFunc = previous;
                Caching.currentCacheForWriting = oldCache;
            }
        }

        private static SizeDependency ObserveSizeDependency(IResourceLocation dependency)
        {
            var options = dependency.Data as AssetBundleRequestOptions;
            return new SizeDependency
            {
                primaryKey = dependency.PrimaryKey, internalId = dependency.InternalId, providerId = dependency.ProviderId,
                locationType = dependency.GetType().FullName, dataType = dependency.Data?.GetType().FullName,
                catalogSize = options?.BundleSize ?? -1, bundleName = options?.BundleName, hash = options?.Hash
            };
        }

        private static string FileDigest(string path)
        {
            RejectLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
#endif
