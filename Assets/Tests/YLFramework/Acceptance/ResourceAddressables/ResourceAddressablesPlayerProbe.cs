#if UNITY_EDITOR || YL_RESOURCE_ADDRESSABLES_ACCEPTANCE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;
using YLFramework.Resource;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace YLFramework.Acceptance
{
    [Preserve]
    public sealed partial class ResourceAddressablesPlayerProbe : MonoBehaviour
    {
        public const string Define = "YL_RESOURCE_ADDRESSABLES_ACCEPTANCE";
        public const string ManifestName = ".yl-resource-addressables-player.json";
        public const string RemotePrefix = "https://yl-resource-addressables.invalid/";
        public const string TextKey = "acceptance/text";
        public const string PrefabKey = "acceptance/prefab";
        public const string SceneKey = "acceptance/scene";
        public const string CachedKey = "acceptance/cached";
        public const string ParallelKey = "acceptance/parallel";
        public const string BatchLabel = "acceptance-performance-texts";
        public const string SuppressedAddress = "acceptance/published/guid-only";
        public const string SuppressedLabel = "unpublished-fixture-label";
        public const string ExcludedAddress = "acceptance/published/excluded";
        public const string PackedSpriteAddress = "acceptance/published/packed-sprite";
        public const string PackedSpriteName = "PackedSprite13x7";
        public const int PackedSpriteWidth = 13, PackedSpriteHeight = 7, PackedSpritePixelsPerUnit = 37;
        public const int ScopeCloseAssetCount = 1000;
        public static string ScopeCloseAssetName(int index) => index.ToString("D4", CultureInfo.InvariantCulture);
        public static string ScopeCloseKey(int index) => "acceptance/scope-close/" + ScopeCloseAssetName(index);
        private const int MaximumFrameSamples = 32768;
        private const int ImageSize = 32;

        [Serializable, Preserve]
        public sealed class BuildIdentity
        {
            public int schemaVersion = 1;
            public string purpose = "YLResource.Addressables.PlayerIdentity";
            public string buildToken, inputHash, contentHash, unityVersion, addressablesVersion;
            public string stripping, playerRoot, hostId, resultId, workspaceId, buildFlavor;
            public string publishedSourceSha256, guidOnlyKey, suppressedGuidKey, packedSpriteGuid;
            public bool graphics, scopeCloseBenchmark, encryptedRemoteGroup, verifiedSizeSnapshot;
            public string heldDownload;
        }

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 1;
            public string purpose = "YLResource.Addressables.PlayerRun";
            public string status, error, buildToken, inputHash, contentHash, unityVersion, addressablesVersion;
            public string addressablesAssemblyVersion, stripping, graphicsDeviceType, backend, platform, runtimeAssembly, probeAssembly;
            public string startedUtc, finishedUtc, cachePath, cachedBundleName, cachedBundleHash, finalState;
            public string[] loadedAssemblies, checks = Array.Empty<string>(), cleanupErrors = Array.Empty<string>();
            public int pid, round, pointerBits, leaseCount, scopeCount, requestCount, pendingWorkCount, drainingHandleCount;
            public int sceneUnloadEventFrame = -1, sceneCloseFrame = -1;
            public long downloadBytesBefore, downloadBytesAfter;
            public bool isEditor, isBatchMode, cacheBefore, cacheAfter;
            public string buildFlavor;
            public string publishedSourceSha256;
            public Performance performance;
            public ScopeCloseMeasurement scopeClose;
            public PackedSpriteEvidence packedSprite;
            public SizeExperimentEvidence sizeExperiment;
            public HeldDownloadEvidence heldDownload;
        }

        [Serializable]
        private sealed class PackedSpriteEvidence
        {
            public string unityVersion, addressablesVersion, address, guid, assetName, namedSpriteKey, expectedContract;
            public PackedSpriteQuery[] queries;
            public PackedSpriteAcquisition texture, sprite, broad, namedSprite;
            public bool spriteUsesAcquiredTexture, namedSpriteMatches, retainedSpriteSurvives;
            public int leasesBefore, leasesAfter = -1, requestsAfter = -1, pendingWorkAfter = -1, drainingHandlesAfter = -1;
        }

        [Serializable]
        private sealed class PackedSpriteQuery
        {
            public string key, requestedType, status, error;
            public int count = -1, distinctLocations;
            public PackedSpriteLocation[] locations;
        }

        [Serializable]
        private sealed class PackedSpriteLocation
        {
            public string resourceType, primaryKey, internalId, providerId;
            public string[] dependencyIds;
            public int referenceIndex = -1;
        }

        [Serializable]
        private sealed class PackedSpriteAcquisition
        {
            public string key, requestedType, status, errorKind, error, assetType, assetName;
            public int instanceId, textureInstanceId, leasesOnClaim = -1;
            public float width, height, pixelsPerUnit;
            public bool leaseValid;
        }

        [Serializable]
        private sealed class ScopeCloseMeasurement
        {
            public long stopwatchFrequency;
            public int vSyncCount, targetFrameRate;
            public string processor, graphicsDevice;
            public ScopeCloseSample[] cases;
        }

        [Serializable]
        private sealed class ScopeCloseSample
        {
            public int leases;
            public int distinctAssets;
            public long closeWallTicks;
            public long maxSliceReleaseTicks;
            public ScopeCloseSlice[] slices;
        }

        [Serializable]
        private struct ScopeCloseSlice
        {
            public int frame;
            public int released;
            // The sum charged by this service tick, not the whole Unity frame or the coroutine poll interval.
            public long releaseTicks;
        }

        [Serializable]
        private sealed class Performance
        {
            public int schemaVersion = 1;
            public string variant, sampleKind, imageUnavailableReason;
            public string nativeRefcounts = "Unsupported";
            public string coldPhase = "First local asset transaction in a new Player after catalog initialization; OS file cache not reset.";
            public string warmPhase = "Same key and process after first owner release; no retained warm-up pin.";
            public string sampling = "Whole Player frames, Unity unscaled delta, nearest rank; Development GC Allocated In Frame is not API-only.";
            public string ownership = "ownerKind counts explicit consumer owners (claimed YL leases, bare handles or Scopes). Instance cases count the prefab owner and live checkouts separately. Load calls are public API calls, not IO or native refcounts.";
            public string policyComparison = "Diagnostics: one explicit snapshot per Scope lifecycle vs no in-window snapshot; history off. Image cache on then off (new YL session, same local file); OS cache not reset.";
            public int maxTextureSize, finalCachedImages = -1;
            public bool supportsRgba32;
            public Workload[] workloads = Array.Empty<Workload>();
        }

        [Serializable]
        private sealed class Workload
        {
            public string name, ownerKind;
            public string status = "Measured", gcStatus = "NotCollectedRelease";
            public int iterations, loadCalls, delivered, canceledConsumers, canceledWhilePending;
            public int ownersAcquired, ownersReleased, peakOwned, finalOwned;
            public int createdInstances, reusedInstances, destroyedInstances, peakLiveInstances, finalInstances;
            public int imageCacheHits, peakIdle, diagnosticsSamples, frameSamples, gcFrameSamples;
            public float[] frameMs = Array.Empty<float>();
            public float p50Ms = -1, p95Ms = -1, p99Ms = -1;
            public long gcAllocationBytes = -1;

            public void AcquireOwner()
            {
                ownersAcquired++;
                peakOwned = Math.Max(peakOwned, ++finalOwned);
            }

            public void ReleaseOwner()
            {
                ownersReleased++;
                finalOwned--;
            }

            public void CreateInstance()
            {
                createdInstances++;
                peakLiveInstances = Math.Max(peakLiveInstances, ++finalInstances);
            }

            public void ObserveDeath(int count)
            {
                destroyedInstances += count;
                finalInstances -= count;
            }
        }

        [Serializable]
        private sealed class QuitReceipt
        {
            public int schemaVersion = 1;
            public string purpose = "YLResource.Addressables.PlayerQuit";
            public string buildToken, inputHash, contentHash, utc;
            public int pid, round, requestedExitCode;
            public bool onApplicationQuit = true;
        }

        [SerializeField] private BuildIdentity _build;
        private readonly List<string> _checks = new();
        private ResourceStartup _startup;
        private ResourceSystem _system;
        private Report _report;
        private readonly List<Workload> _workloads = new();
        private Workload _measuring;
        private List<float> _frameMs;
        private ProfilerRecorderHandle _allocationCounter;
        private ProfilerRecorder _allocationRecorder;
        private int _measurementFrame, _lastAllocationSample;
        private string _result, _baseUrl;
        private bool _finished;
        private int _exitCode = 1;

        public void Configure(BuildIdentity identity) => _build = identity;

        private IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
            var stack = new Stack<IEnumerator>();
            stack.Push(Run());
            bool resumed = false;
            while (stack.Count != 0)
            {
                object current = null;
                Exception failure = null;
                try
                {
                    if (resumed) { SampleMeasurementFrame(); resumed = false; }
                    if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                    current = stack.Peek().Current;
                }
                catch (Exception error) { failure = error; }
                if (failure != null)
                {
                    foreach (var frame in stack)
                    {
                        try { (frame as IDisposable)?.Dispose(); }
                        catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
                    }
                    Finish(failure);
                    yield break;
                }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return current;
                resumed = true;
            }
            Finish(null);
        }

        private IEnumerator Run()
        {
            Authorize();
            Require(_report.backend == "IL2CPP", "This acceptance requires an actual IL2CPP Player, not Mono or Editor.");
#if !ENABLE_CACHING
            Require(false, "The Windows Player must include Unity's actual AssetBundle cache support.");
#endif
            Require(!Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8,
                "The acceptance must execute in Windows x64 Player.");
            if (!string.IsNullOrEmpty(_build.heldDownload))
            {
                yield return HeldDownloadHandle_DoesNotSatisfyAssetLoad();
                yield break;
            }
            if (_build.encryptedRemoteGroup)
            {
                yield return DownloadSize_UsesShippedCipherLengthAndVerifiedBytes();
                yield break;
            }
            if (_report.round > 4)
            {
                yield return MeasureScopeClose();
                yield break;
            }
            if (_report.round > 2)
            {
                yield return MeasurePerformance(_report.round == 3);
                yield break;
            }
            yield return Until(() => Caching.ready);
            string cacheRoot = Path.Combine(_build.playerRoot, "Cache");
            string cacheOwner = Path.Combine(cacheRoot, ManifestName);
            if (_report.round == 1)
            {
                Require(!Directory.Exists(cacheRoot), "First launch refuses a pre-existing cache directory.");
                Directory.CreateDirectory(cacheRoot);
                WriteNew(cacheOwner, _build);
            }
            else
            {
                RejectLinks(cacheOwner);
                var owner = JsonUtility.FromJson<BuildIdentity>(File.ReadAllText(cacheOwner));
                Require(owner.buildToken == _build.buildToken && owner.contentHash == _build.contentHash && owner.inputHash == _build.inputHash,
                    "Second launch must reuse this exact build's owned cache.");
            }
            var cache = Caching.AddCache(cacheRoot);
            Require(cache.valid, "Unity did not accept the dedicated cache.");
            Caching.currentCacheForWriting = cache;
            _report.cachePath = cache.path;
            Func<IResourceLocation, string> previousTransform = Addressables.InternalIdTransformFunc;
            Addressables.InternalIdTransformFunc = location => location.InternalId.StartsWith(RemotePrefix, StringComparison.Ordinal)
                ? _baseUrl + location.InternalId.Substring(RemotePrefix.Length) + "?round=" + _report.round
                : previousTransform == null ? location.InternalId : previousTransform(location);
            try
            {
                Require(ResourceSystem.Current == null, "The probe must own startup rather than adopt another service.");
                _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                using var starting = _startup.StartAsync();
                yield return Wait(starting);
                Require(ReferenceEquals(starting.TakeResult(), _startup), "Startup returned another owner.");
                _system = _startup.Resources;
                Require(_system != null && _system.IsAvailable && Caching.currentCacheForWriting.path == cache.path,
                    "Startup must preserve the selected official cache.");
                yield return VerifyPublishedKeys();
                using var a = _system.CreateScope("player scope A");
                using var b = _system.CreateScope("player scope B");
                TextAsset first = null;
                for (int index = 0; index < 4; index++)
                {
                    using var reading = a.LoadAsync<TextAsset>(TextKey);
                    yield return Wait(reading);
                    var value = reading.TakeResult();
                    Require(value && value.text == "local:" + _build.buildToken, "The actual built local TextAsset differs.");
                    Require(index == 0 || ReferenceEquals(first, value), "Repeated reads must borrow the same asset.");
                    first = value;
                }
                Require(_system.GetDiagnostics().Leases.Count(item => item.Key == TextKey && item.Owner == a.OwnerLabel) == 1,
                    "Repeated Scope reads accumulated YL leases.");
                using var otherReading = b.LoadAsync<TextAsset>(TextKey);
                yield return Wait(otherReading);
                Require(ReferenceEquals(first, otherReading.TakeResult()), "Independent scopes must share the official asset, not a clone.");
                Require(_system.GetDiagnostics().Leases.Count(item => item.Key == TextKey && !item.Internal) == 2,
                    "Two scopes must own two independent references.");
                _checks.Add("ScopeReadOnce");

                using var acquiring = _system.AcquireAsync<TextAsset>(TextKey);
                yield return Wait(acquiring);
                using var independent = acquiring.TakeResult();
                using var retained = independent.Retain("player independent retained lease");
                independent.Dispose();
                Require(retained.IsValid && retained.Asset.text == first.text, "Retain must own a separate live reference.");

                using (var prefabRequest = _system.AcquireAsync<GameObject>(PrefabKey))
                {
                    yield return Wait(prefabRequest);
                    using var prefab = prefabRequest.TakeResult();
                    using var pool = _system.GetOrCreateInstanceCache(prefab);
                    Require(pool.MaxCapacity == 32 && pool.Prewarm(1) == 1, "The real scene pool default/prewarm differs.");
                    using var rented = pool.Rent();
                    var pooledObject = rented.GameObject;
                    rented.Return();
                    using var freshA = _system.Instantiate(prefab);
                    using var freshB = _system.Instantiate(prefab);
                    var objectA = freshA.GameObject;
                    var objectB = freshB.GameObject;
                    Require(objectA != pooledObject && objectB != objectA, "Instantiate must always create a fresh object.");
                    using var closeA = freshA.CloseAsync();
                    Require(!closeA.IsDone && objectA, "Destroy scheduling is not actual death.");
                    yield return Wait(closeA);
                    Require(closeA.TakeResult() && !objectA, "Fresh close completed before actual death.");
                    using var closeB = freshB.CloseAsync();
                    yield return Wait(closeB);
                    Require(closeB.TakeResult() && !objectB, "The second fresh checkout did not end.");
                    using var reused = pool.Rent();
                    Require(reused.GameObject == pooledObject, "Rent did not reuse the real idle instance.");
                    rented.Return();
                    rented.Discard();
                    Require(reused.IsValid && pooledObject.activeSelf, "An old lease affected the next checkout.");
                    reused.Return();
                    pool.Dispose();
                    prefab.Dispose();
                    yield return Until(() => !pooledObject && !_system.GetDiagnostics().Leases.Any(item => item.Key == PrefabKey));
                }
                _checks.Add("FreshVsRent");

                using (var sceneScope = _system.CreateScope("player additive scene"))
                {
                    int before = SceneManager.sceneCount;
                    using var loadingScene = _system.LoadSceneAsync(SceneKey, LoadSceneMode.Additive);
                    yield return Wait(loadingScene);
                    var lease = sceneScope.Own(loadingScene.TakeResult());
                    var scene = lease.Scene;
                    Require(scene.isLoaded && lease.Mode == LoadSceneMode.Additive && SceneManager.sceneCount == before + 1,
                        "The official Additive scene did not load alongside the probe.");
                    UnityEngine.Events.UnityAction<Scene> unloaded = value => { if (value == scene) _report.sceneUnloadEventFrame = Time.frameCount; };
                    SceneManager.sceneUnloaded += unloaded;
                    try
                    {
                        using var closing = sceneScope.CloseAsync();
                        Require(!closing.IsDone, "Scene close must observe actual native unload.");
                        yield return Wait(closing);
                        _report.sceneCloseFrame = Time.frameCount;
                        Require(closing.TakeResult() && lease.IsSettled && (!scene.IsValid() || !scene.isLoaded) &&
                            _report.sceneUnloadEventFrame >= 0 && _report.sceneCloseFrame > _report.sceneUnloadEventFrame,
                            "Scene close did not cross the native sceneUnloaded boundary.");
                    }
                    finally { SceneManager.sceneUnloaded -= unloaded; }
                }
                _checks.Add("AdditiveSceneClosed");

                AssetBundleRequestOptions bundle = null;
                var locations = Addressables.LoadResourceLocationsAsync(CachedKey, typeof(TextAsset));
                try
                {
                    yield return WaitHandle(locations);
                    Require(locations.Result.Count == 1, "The cached fixture must resolve one exact official location.");
                    var pending = new Stack<IResourceLocation>(locations.Result);
                    var visited = new HashSet<IResourceLocation>();
                    while (pending.Count != 0)
                    {
                        var location = pending.Pop();
                        if (!visited.Add(location)) continue;
                        if (location.Data is AssetBundleRequestOptions options && location.InternalId.StartsWith(RemotePrefix, StringComparison.Ordinal))
                        {
                            Require(bundle == null, "The cache probe must have exactly one remote bundle.");
                            bundle = options;
                        }
                        if (location.Dependencies != null)
                            foreach (var dependency in location.Dependencies) pending.Push(dependency);
                    }
                    Require(bundle != null && !string.IsNullOrEmpty(bundle.Hash) && !string.IsNullOrEmpty(bundle.BundleName),
                        "The official remote bundle has no cache version identity.");
                }
                finally { Addressables.Release(locations); }
                var version = new CachedAssetBundle(bundle.BundleName, Hash128.Parse(bundle.Hash));
                _report.cachedBundleName = bundle.BundleName;
                _report.cachedBundleHash = version.hash.ToString();
                _report.cacheBefore = Caching.IsVersionCached(version);
                Require(_report.cacheBefore == (_report.round == 2), "Actual first/repeated process cache state differs.");
                var size = Addressables.GetDownloadSizeAsync(CachedKey);
                try { yield return WaitHandle(size); _report.downloadBytesBefore = size.Result; }
                finally { Addressables.Release(size); }
                Require(_report.round == 1 ? _report.downloadBytesBefore > 0 : _report.downloadBytesBefore == 0,
                    "Official download sizing did not reflect the real persistent cache.");
                using (var cachedReading = a.LoadAsync<TextAsset>(CachedKey))
                {
                    yield return Wait(cachedReading);
                    Require(cachedReading.TakeResult().text == "cached:" + _build.buildToken, "The actual remote bundle bytes differ.");
                }
                _report.cacheAfter = Caching.IsVersionCached(version);
                size = Addressables.GetDownloadSizeAsync(CachedKey);
                try { yield return WaitHandle(size); _report.downloadBytesAfter = size.Result; }
                finally { Addressables.Release(size); }
                Require(_report.cacheAfter && _report.downloadBytesAfter == 0, "The official version was not cached after loading.");
                _checks.Add("OfficialCache");

                using var closeScope = a.CloseAsync();
                yield return Wait(closeScope);
                Require(closeScope.TakeResult() && retained.IsValid && !b.IsDisposed,
                    "Closing one Scope invalidated another owner's reference.");
                _checks.Add("IndependentLease");
                using var busy = _startup.StopAsync();
                yield return Wait(busy);
                Require(busy.Status == ResourceRequestStatus.Failed && busy.Error?.Kind == ResourceError.Busy &&
                    _startup.OwnsResourceSystem && ReferenceEquals(_startup.Resources, _system) && retained.IsValid && !b.IsDisposed,
                    "Stop must report Busy without destroying independent owners or their scope parents.");
                using var rejected = _system.AcquireAsync<TextAsset>(TextKey);
                yield return Wait(rejected);
                Require(rejected.Status != ResourceRequestStatus.Succeeded && !_system.IsAvailable,
                    "A Busy stop must freeze new work.");
                _checks.Add("StopBusy");
                retained.Dispose();
                using var stopping = _startup.StopAsync();
                yield return Wait(stopping);
                Require(stopping.TakeResult() && !_startup.OwnsResourceSystem && ResourceSystem.Current == null,
                    "The startup owner did not finish shutdown.");
                CaptureDiagnostics();
                Require(_system.State == ResourceSystemState.Closed && _report.leaseCount == 0 && _report.scopeCount == 0 &&
                    _report.requestCount == 0 && _report.pendingWorkCount == 0 && _report.drainingHandleCount == 0 && _report.cleanupErrors.Length == 0,
                    "YL ownership did not actually drain to zero.");
                _checks.Add("FinalOwnershipZero");
            }
            finally { Addressables.InternalIdTransformFunc = previousTransform; }
        }

        private IEnumerator VerifyPublishedKeys()
        {
#if YL_RESOURCE_ADDRESSABLES_ACCEPTANCE && !UNITY_EDITOR
            yield return VerifyPackedSprite(Published.ResourceNames.Addresses.AcceptancePublishedPackedSprite);

            using var exact = _system.AcquireAsync<UnityEngine.Object>(Published.ResourceNames.Addresses.AcceptanceText);
            yield return Wait(exact);
            using var main = exact.TakeResult();
            Require(main.Asset is TextAsset text && text.text == "local:" + _build.buildToken,
                "The generated address must resolve one exact TextAsset even when the entry also uses it as a label.");

            using var loadingBatch = _system.AcquireBatchAsync<TextAsset>(new[] { Published.ResourceNames.Labels.AcceptancePerformanceTexts });
            yield return Wait(loadingBatch);
            using var batch = loadingBatch.TakeResult();
            Require(batch.Assets.Count == 2 && batch.Assets.Any(asset => asset == main.Asset) &&
                batch.Assets.Any(asset => asset.text == "parallel:" + _build.buildToken),
                "The generated label must load both authored objects, excluding the disabled label output.");

            var reference = new AssetReferenceT<TextAsset>(_build.guidOnlyKey);
            using var loadingGuid = _system.AcquireAsync(reference);
            yield return Wait(loadingGuid);
            using var guidOnly = loadingGuid.TakeResult();
            Require(guidOnly.Asset.text == "guid-only:" + _build.buildToken, "The published AssetReference key resolved the wrong asset.");

            using var loadingAddress = _system.AcquireAsync<TextAsset>(Published.ResourceNames.Addresses.AcceptancePublishedAddressOnly);
            yield return Wait(loadingAddress);
            using var addressOnly = loadingAddress.TakeResult();
            Require(addressOnly.Asset.text == "address-only:" + _build.buildToken, "Suppressing a GUID must not suppress its published address.");

            foreach (var key in new[] { SuppressedAddress, new AssetReferenceT<TextAsset>(_build.suppressedGuidKey).RuntimeKey.ToString(),
                SuppressedLabel, ExcludedAddress, "unassigned-publication-label" })
            {
                using var missing = _system.AcquireAsync<TextAsset>(key);
                yield return Wait(missing);
                Require(missing.Status == ResourceRequestStatus.Failed && missing.Error?.Kind == ResourceError.AddressNotFound,
                    "A disabled or unused key must be absent from the packed Player: " + key);
            }
            _checks.Add("PackedPublishedKeys");
#else
            throw new InvalidOperationException("Generated publication keys are only compiled in the isolated acceptance Player.");
#endif
        }

        private IEnumerator VerifyPackedSprite(string address)
        {
            var evidence = _report.packedSprite = new PackedSpriteEvidence
            {
                unityVersion = _report.unityVersion, addressablesVersion = _report.addressablesVersion,
                address = address, guid = _build.packedSpriteGuid, assetName = PackedSpriteName,
                expectedContract = "Single imported PNG: address and GUID each resolve one Texture2D and one Sprite at distinct " +
                    "official locations; typed leases succeed; Object fails InvalidAddress; the named Sprite is the same imported object."
            };
            var named = new AssetReferenceT<Sprite>(evidence.guid) { SubObjectName = PackedSpriteName };
            evidence.namedSpriteKey = named.RuntimeKey as string;
            var types = new[] { typeof(Texture2D), typeof(Sprite), typeof(UnityEngine.Object) };
            var querySpecs = new[] { address, evidence.guid }.SelectMany(key => types.Select(type => (key, type)))
                .Append((evidence.namedSpriteKey, typeof(Sprite))).ToArray();
            var queries = evidence.queries = new PackedSpriteQuery[querySpecs.Length];
            for (int i = 0; i < querySpecs.Length; i++)
            {
                var (key, type) = querySpecs[i];
                yield return ObservePackedSpriteLocations(queries[i] = new PackedSpriteQuery { key = key, requestedType = type.FullName }, type);
            }

            var before = _system.GetDiagnostics();
            evidence.leasesBefore = before.LeaseCount;
            AssetLease<Texture2D> texture = null;
            AssetLease<Sprite> sprite = null, namedSprite = null;
            AssetLease<UnityEngine.Object> broad = null;
            try
            {
                yield return ObservePackedSpriteAcquisition(_system.AcquireAsync<Texture2D>(address),
                    evidence.texture = new PackedSpriteAcquisition { key = address }, lease => texture = lease);
                yield return ObservePackedSpriteAcquisition(_system.AcquireAsync<Sprite>(address),
                    evidence.sprite = new PackedSpriteAcquisition { key = address }, lease => sprite = lease);
                yield return ObservePackedSpriteAcquisition(_system.AcquireAsync<UnityEngine.Object>(address),
                    evidence.broad = new PackedSpriteAcquisition { key = address }, lease => broad = lease);
                yield return ObservePackedSpriteAcquisition(_system.AcquireAsync(named),
                    evidence.namedSprite = new PackedSpriteAcquisition { key = evidence.namedSpriteKey }, lease => namedSprite = lease);

                evidence.spriteUsesAcquiredTexture = sprite != null && texture != null && sprite.Asset.texture == texture.Asset;
                evidence.namedSpriteMatches = sprite != null && namedSprite != null && sprite.Asset == namedSprite.Asset;
                if (sprite != null)
                {
                    using var retained = sprite.Retain("packed Sprite retained pin");
                    var imported = retained.Asset;
                    // Return all other owners, including the named alias; it must be the retained Sprite that keeps the asset live.
                    ReleaseOriginals();
                    yield return null;
                    evidence.retainedSpriteSurvives = sprite.IsDisposed && retained.IsValid && retained.Asset == imported &&
                        retained.Asset.texture && _system.GetDiagnostics().LeaseCount == before.LeaseCount + 1;
                }
            }
            finally { ReleaseOriginals(); }

            void ReleaseOriginals()
            {
                namedSprite?.Dispose();
                broad?.Dispose();
                sprite?.Dispose();
                texture?.Dispose();
            }

            var after = _system.GetDiagnostics();
            evidence.leasesAfter = after.LeaseCount;
            evidence.requestsAfter = after.RequestCount;
            evidence.pendingWorkAfter = after.PendingWorkCount;
            evidence.drainingHandlesAfter = after.DrainingHandleCount;

            // First observed in the Win64 IL2CPP Unity 6000.3.27f1 / Addressables 2.10.3 baseline.
            Require(address == PackedSpriteAddress && evidence.namedSpriteKey == evidence.guid + "[" + PackedSpriteName + "]",
                "Packed PNG has a different generated address or official AssetReference RuntimeKey.");
            for (int i = 0; i < 6; i += 3)
            {
                var textureType = types[0].FullName;
                var spriteType = types[1].FullName;
                Require(HasPackedSpriteLocations(queries[i], textureType) && HasPackedSpriteLocations(queries[i + 1], spriteType) &&
                    HasPackedSpriteLocations(queries[i + 2], textureType, spriteType),
                    "Unexpected official packed PNG location contract: " + queries[i].key);
            }
            Require(HasPackedSpriteLocations(queries[6], types[1].FullName) &&
                queries[6].locations[0].internalId == queries[4].locations[0].internalId + "[" + PackedSpriteName + "]" &&
                queries[6].locations[0].providerId == queries[4].locations[0].providerId,
                "The named Sprite must use the packed subobject route, not an atlas or a built-in Sprite.");
            Require(IsImportedPackedSpriteAsset(evidence.texture, types[0], before.LeaseCount + 1) &&
                IsImportedPackedSpriteAsset(evidence.sprite, types[1], before.LeaseCount + 2) &&
                IsImportedPackedSpriteAsset(evidence.namedSprite, types[1], before.LeaseCount + 3) &&
                evidence.spriteUsesAcquiredTexture && evidence.namedSpriteMatches && evidence.retainedSpriteSurvives,
                "Packed PNG acquisitions did not return the same imported Sprite and backing Texture2D with independent ownership.");
            Require(evidence.broad.status == nameof(ResourceRequestStatus.Failed) &&
                evidence.broad.errorKind == nameof(ResourceError.InvalidAddress) && evidence.broad.leasesOnClaim == -1,
                "Broad acquisition must reject truly distinct packed locations rather than choose one by path.");
            string message = evidence.broad.error ?? string.Empty;
            Require(Regex.IsMatch(message, @"\bObject\b") && Regex.IsMatch(message, @"\bSprite\b") && Regex.IsMatch(message, @"\bTexture2D\b") &&
                Regex.IsMatch(message, @"\b(?:use|request|specify|choose|select|try)\b[^.!?\r\n]*\b(?:narrower|specific|concrete)\b[^.!?\r\n]*\btype\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                "Broad InvalidAddress must identify Object, Sprite and Texture2D and recommend a narrower concrete type.");
            Require(after.LeaseCount == before.LeaseCount && after.RequestCount == before.RequestCount &&
                after.PendingWorkCount == before.PendingWorkCount && after.DrainingHandleCount == before.DrainingHandleCount &&
                after.Errors.SequenceEqual(before.Errors), "Packed PNG acquisition did not return ownership to its starting state.");
            _checks.Add("PackedSpriteTypedOwnership");
            _checks.Add("PackedSpriteBroadAmbiguity");
        }

        private static IEnumerator ObservePackedSpriteLocations(PackedSpriteQuery row, Type type)
        {
            var handle = Addressables.LoadResourceLocationsAsync(row.key, type);
            try
            {
                yield return Until(() => handle.IsDone);
                row.status = handle.Status.ToString();
                row.error = handle.OperationException?.ToString();
                var locations = handle.Result;
                if (locations == null) yield break;
                row.count = locations.Count;
                var distinct = new List<IResourceLocation>();
                row.locations = locations.Select(location =>
                {
                    if (location == null) return new PackedSpriteLocation();
                    // Reference identity is intentional: different locations can use the very same PNG path.
                    int reference = distinct.FindIndex(other => ReferenceEquals(other, location));
                    if (reference < 0) { reference = distinct.Count; distinct.Add(location); }
                    return new PackedSpriteLocation
                    {
                        resourceType = location.ResourceType?.FullName, primaryKey = location.PrimaryKey,
                        internalId = location.InternalId, providerId = location.ProviderId, referenceIndex = reference,
                        dependencyIds = location.Dependencies?.Select(dependency => dependency.InternalId).ToArray()
                    };
                }).ToArray();
                row.distinctLocations = distinct.Count;
            }
            finally { Addressables.Release(handle); }
        }

        private IEnumerator ObservePackedSpriteAcquisition<T>(ResourceRequest<AssetLease<T>> request, PackedSpriteAcquisition row,
            Action<AssetLease<T>> claim) where T : UnityEngine.Object
        {
            using (request)
            {
                row.requestedType = typeof(T).FullName;
                yield return Wait(request);
                row.status = request.Status.ToString();
                row.errorKind = request.Error?.Kind.ToString();
                row.error = request.Error?.Message;
                if (request.Status != ResourceRequestStatus.Succeeded) yield break;
                var lease = request.TakeResult();
                claim(lease);
                row.leasesOnClaim = _system.GetDiagnostics().LeaseCount;
                row.leaseValid = lease.IsValid && lease.Key == row.key;
                var asset = lease.Asset;
                row.assetType = asset.GetType().FullName;
                row.assetName = asset.name;
                row.instanceId = asset.GetInstanceID();
                if (asset is Texture2D texture)
                {
                    row.width = texture.width;
                    row.height = texture.height;
                }
                else if (asset is Sprite sprite)
                {
                    row.width = sprite.rect.width;
                    row.height = sprite.rect.height;
                    row.pixelsPerUnit = sprite.pixelsPerUnit;
                    if (sprite.texture) row.textureInstanceId = sprite.texture.GetInstanceID();
                }
            }
        }

        private static bool HasPackedSpriteLocations(PackedSpriteQuery row, params string[] types) =>
            row.status == nameof(AsyncOperationStatus.Succeeded) && row.count == types.Length && row.distinctLocations == types.Length &&
            row.locations.Select(location => location.resourceType).OrderBy(type => type, StringComparer.Ordinal)
                .SequenceEqual(types.OrderBy(type => type, StringComparer.Ordinal));

        private static bool IsImportedPackedSpriteAsset(PackedSpriteAcquisition row, Type type, int owners) =>
            row.status == nameof(ResourceRequestStatus.Succeeded) && row.assetType == type.FullName && row.assetName == PackedSpriteName &&
            row.width == PackedSpriteWidth && row.height == PackedSpriteHeight && row.leaseValid && row.leasesOnClaim == owners &&
            (type != typeof(Sprite) || row.pixelsPerUnit == PackedSpritePixelsPerUnit);

        private IEnumerator MeasureScopeClose()
        {
#if YL_RESOURCE_ADDRESSABLES_ACCEPTANCE
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return StartMeasurementSystem(new ResourceConfiguration());
            var cases = new List<ScopeCloseSample>(3);
            foreach (var count in new[] { 1, 100, ScopeCloseAssetCount })
            {
                using var scope = _system.CreateScope("scope-close-" + count);
                var assets = new HashSet<int>();
                var requests = Enumerable.Range(0, count).Select(index => scope.LoadAsync<TextAsset>(ScopeCloseKey(index))).ToArray();
                try
                {
                    yield return Until(() => requests.All(request => request.IsDone));
                    for (var index = 0; index < requests.Length; index++)
                    {
                        var text = requests[index].TakeResult();
                        Require(text && text.text == ScopeCloseKey(index) && assets.Add(text.GetInstanceID()),
                            "The preloaded keys must resolve to distinct fixture objects with the exact payloads.");
                    }
                }
                finally
                {
                    foreach (var request in requests)
                    {
                        request.Dispose();
                    }
                }

                yield return Until(() => _system.GetDiagnostics().PendingWorkCount == 0);
                var ready = _system.GetDiagnostics();
                Require(ready.ScopeCount == 1 && ready.LeaseCount == count && ready.RequestCount == 0 &&
                    ready.DrainingHandleCount == 0 && ready.Errors.Count == 0 && ready.Leases.All(item => item.Owner == scope.OwnerLabel),
                    "Only the preloaded Scope's settled inputs may enter the close window.");
                var slices = new List<ScopeCloseSlice>(count);
                yield return null;
                var requestFrame = Time.frameCount;
                var remaining = _system.ActiveLeaseCount;
                var started = Stopwatch.GetTimestamp();
                using var close = scope.CloseAsync();
                var sampledCompletion = false;
                using (_system.AddTick(() =>
                {
                    if (sampledCompletion)
                    {
                        return;
                    }
                    var current = _system.ActiveLeaseCount;
                    slices.Add(new ScopeCloseSlice
                    {
                        frame = Time.frameCount, released = remaining - current, releaseTicks = _system.SynchronousCloseTicks
                    });
                    remaining = current;
                    sampledCompletion = close.IsDone;
                }))
                {
                    while (!sampledCompletion)
                    {
                        yield return null;
                        Require(Stopwatch.GetTimestamp() - started < 45 * Stopwatch.Frequency && slices.Count <= count,
                            "Scope close did not complete within the observation window.");
                    }
                }
                var closeWallTicks = Stopwatch.GetTimestamp() - started;
                Require(close.TakeResult() && slices.Count > 0 && slices[0].frame == requestFrame + 1 &&
                    remaining == 0 && IsMeasurementDrained(_system.GetDiagnostics()),
                    "Scope close did not settle every preloaded lease.");
                cases.Add(new ScopeCloseSample
                {
                    leases = count, distinctAssets = assets.Count, closeWallTicks = closeWallTicks,
                    maxSliceReleaseTicks = slices.Max(slice => slice.releaseTicks), slices = slices.ToArray()
                });
            }
            yield return StopMeasurementSystem();
            _report.scopeClose = new ScopeCloseMeasurement
            {
                stopwatchFrequency = Stopwatch.Frequency, vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                processor = SystemInfo.processorType, graphicsDevice = SystemInfo.graphicsDeviceName, cases = cases.ToArray()
            };
#else
            throw new InvalidOperationException("Scope close measurement requires an acceptance Player build.");
#endif
        }

        private IEnumerator MeasurePerformance(bool bare)
        {
            Require(ResourceSystem.Current == null, "Measurements need a fresh Player with no resource owner.");
            bool development = _report.buildFlavor == "Development";
            var evidence = _report.performance = new Performance
            {
                variant = bare ? "Bare" : "YL", sampleKind = development ? "DevelopmentAllocation" : "ReleaseFrameTiming",
                maxTextureSize = SystemInfo.maxTextureSize, supportsRgba32 = SystemInfo.SupportsTextureFormat(TextureFormat.RGBA32)
            };
            if (development)
            {
                var counters = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(counters);
                foreach (var counter in counters)
                {
                    var description = ProfilerRecorderHandle.GetDescription(counter);
                    if (description.Category == ProfilerCategory.Memory && description.Name == "GC Allocated In Frame" &&
                        description.UnitType == ProfilerMarkerDataUnit.Bytes)
                    {
                        _allocationCounter = counter;
                        break;
                    }
                }
            }
            if (bare)
            {
                var initializing = Addressables.InitializeAsync(false);
                try { yield return WaitHandle(initializing); }
                finally { Addressables.Release(initializing); }
                _report.leaseCount = _report.scopeCount = _report.requestCount = _report.pendingWorkCount = _report.drainingHandleCount = -1;
            }
            else
            {
                yield return StartMeasurementSystem(new ResourceConfiguration());
            }

            string assetOwner = bare ? "AddressablesHandle" : "AssetLease";
            string textOwner = bare ? "AddressablesHandle" : "Scope";
            yield return Measure("first-local", textOwner, 1, row => MeasureText(row, bare, 1));
            yield return Measure("warm-local", textOwner, 24, row => MeasureText(row, bare, 1));
            yield return Measure("parallel-cancel", assetOwner, 8, row => MeasureParallel(row, bare));
            yield return Measure("label-batch", bare ? "AddressablesHandle" : "BatchLease", 12, row => MeasureLabel(row, bare));
            yield return Measure("scene-roundtrip", bare ? "AddressablesHandle" : "SceneLease", 8, row => MeasureScenes(row, bare));
            yield return Measure("instances-fresh", assetOwner, 24, row => MeasureInstances(row, bare, false));
            yield return Measure("instances-repeat", assetOwner, 24, row => MeasureInstances(row, bare, !bare));
            yield return Measure("scope-churn", textOwner, 24, row => MeasureText(row, bare, 4));
            if (!bare) yield return Measure("diagnostics-on", textOwner, 24, row => MeasureText(row, false, 4, true));

            if (!bare && evidence.maxTextureSize >= ImageSize && evidence.supportsRgba32)
            {
                // A fixed 32x32 RGBA PNG; both cache policies read the same local file, not the HTTP gate's cache.
                string image = Path.Combine(_build.playerRoot, "performance.png");
                RejectLinks(image);
                using (var stream = new FileStream(image, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAMElEQVR4nO3OIQEAAAgDMOI8HenoBzEwE/Or9OynEhAQEBAQEBAQEBAQEBAQyHPgAAt/JIgwaX7HAAAAAElFTkSuQmCC");
                    stream.Write(bytes, 0, bytes.Length);
                }
                yield return Measure("images-cache-on", assetOwner, 16, row => MeasureImages(row, image, true));
                yield return StopMeasurementSystem();
                yield return StartMeasurementSystem(new ResourceConfiguration(images: new ResourceImageOptions(idleCapacity: 0)));
                yield return Measure("images-cache-off", assetOwner, 16, row => MeasureImages(row, image, false));
            }
            else if (!bare)
            {
                evidence.imageUnavailableReason = "Graphics device does not support the 32x32 RGBA32 fixture.";
            }

            if (bare)
            {
                Require(ResourceSystem.Current == null, "The bare baseline unexpectedly used a YL session.");
                _report.finalState = "WorkloadReleased";
            }
            else
            {
                yield return StopMeasurementSystem();
                evidence.finalCachedImages = _system.CachedImageCount;
                Require(evidence.finalCachedImages == 0, "The measurement left an idle image pin after Stop.");
            }
            evidence.workloads = _workloads.ToArray();
        }

        private IEnumerator StartMeasurementSystem(ResourceConfiguration configuration)
        {
            _startup = new ResourceStartup(configuration, bindCoreResourceProvider: false);
            using var starting = _startup.StartAsync();
            yield return Wait(starting);
            Require(ReferenceEquals(starting.TakeResult(), _startup), "Measurements did not own their YL startup.");
            _system = _startup.Resources;
        }

        private IEnumerator StopMeasurementSystem()
        {
            using var stopping = _startup.StopAsync();
            yield return Wait(stopping);
            Require(stopping.TakeResult() && !_startup.OwnsResourceSystem && ResourceSystem.Current == null &&
                _system.State == ResourceSystemState.Closed, "The measured YL session did not close.");
            var report = _system.GetDiagnostics();
            Require(IsMeasurementDrained(report), DescribeMeasurementResidue("Stop", report));
        }

        private IEnumerator WaitForMeasurementDrain(string workload)
        {
            if (_system == null) yield break;
            float deadline = Time.realtimeSinceStartup + 45;
            for (var report = _system.GetDiagnostics(); !IsMeasurementDrained(report); report = _system.GetDiagnostics())
            {
                Require(report.Errors.Count == 0 && Time.realtimeSinceStartup < deadline, DescribeMeasurementResidue(workload, report));
                yield return null;
            }
        }

        private bool IsMeasurementDrained(ResourceDiagnosticsReport report) => report.LeaseCount == 0 && report.ScopeCount == 0 &&
            report.RequestCount == 0 && report.PendingWorkCount == 0 && report.DrainingHandleCount == 0 &&
            report.Errors.Count == 0 && _system.CachedImageCount == 0;

        private string DescribeMeasurementResidue(string workload, ResourceDiagnosticsReport report) =>
            $"Measurement cleanup did not settle ({workload}): {report.LeaseCount} leases, {report.ScopeCount} scopes, " +
            $"{report.RequestCount} requests, {report.PendingWorkCount} work, {report.DrainingHandleCount} drains, " +
            $"{_system.CachedImageCount} cached images; leases: {string.Join(", ", report.Leases.Select(lease => lease.Key))}; " +
            $"errors: {string.Join("; ", report.Errors)}.";

        private IEnumerator Measure(string name, string ownerKind, int iterations, Func<Workload, IEnumerator> run)
        {
            var row = new Workload { name = name, ownerKind = ownerKind, iterations = iterations };
            bool development = _report.buildFlavor == "Development";
            var samples = development ? null : new List<float>(MaximumFrameSamples);
            var operation = run(row);
            // Keep initialization, the previous row's audit, and percentile sorting out of the next window.
            yield return null;
            using var recorder = _allocationCounter.Valid ? new ProfilerRecorder(_allocationCounter, MaximumFrameSamples + 1,
                ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame) : default;
            _measurementFrame = Time.frameCount;
            _lastAllocationSample = 0;
            _frameMs = samples;
            _allocationRecorder = recorder;
            _measuring = row;
            try
            {
                yield return operation;
                yield return null; // include the actual frame containing final release
            }
            finally
            {
                _measuring = null;
                _frameMs = null;
                _allocationRecorder = default;
            }
            if (samples != null)
            {
                row.frameMs = samples.ToArray();
                samples.Sort();
                float Percentile(double percentile) => samples[(int)Math.Ceiling(samples.Count * percentile) - 1];
                row.p50Ms = Percentile(0.50);
                row.p95Ms = Percentile(0.95);
                row.p99Ms = Percentile(0.99);
            }
            else
            {
                row.gcStatus = row.gcFrameSamples == 0 ? "Unavailable" : row.gcFrameSamples == row.frameSamples ? "Sampled" : "Partial";
            }
            Require(row.finalOwned == 0 && row.finalInstances == 0, "The measured fixture still owns work: " + name);
            // A checkout can be Unity fake-null one cleanup epoch before its prefab pin is released.
            yield return WaitForMeasurementDrain(name); // not part of diagnostic-off sampling
            _workloads.Add(row);
            _report.performance.workloads = _workloads.ToArray();
        }

        private void SampleMeasurementFrame()
        {
            var row = _measuring;
            if (row == null) return;
            int frame = Time.frameCount;
            if (_measurementFrame == frame) return;
            _measurementFrame = frame;
            // Uncapped Players can render thousands of frames during native image IO.
            if (++row.frameSamples > MaximumFrameSamples)
                throw new InvalidOperationException($"The frame-sample budget was exceeded: {row.name} ({row.delivered}/{row.iterations} delivered).");
            _frameMs?.Add(Time.unscaledDeltaTime * 1000f);
            if (!_allocationRecorder.Valid) return;
            int sampleCount = _allocationRecorder.Count;
            if (sampleCount <= _lastAllocationSample) return;
            _lastAllocationSample = sampleCount;
            long bytes = _allocationRecorder.LastValue;
            if (bytes < 0) return;
            row.gcAllocationBytes = row.gcFrameSamples == 0 ? bytes : row.gcAllocationBytes + bytes;
            row.gcFrameSamples++;
        }

        private IEnumerator MeasureText(Workload row, bool bare, int borrows, bool diagnostics = false)
        {
            for (int i = 0; i < row.iterations; i++)
            {
                if (bare)
                {
                    var handle = Addressables.LoadAssetAsync<TextAsset>(TextKey);
                    row.loadCalls++;
                    row.AcquireOwner();
                    try
                    {
                        yield return WaitHandle(handle);
                        for (int read = 0; read < borrows; read++)
                        {
                            Require(handle.Result, "Bare borrow did not return the local asset.");
                            row.delivered++;
                        }
                    }
                    finally { Addressables.Release(handle); row.ReleaseOwner(); }
                }
                else
                {
                    using var scope = _system.CreateScope("performance text");
                    row.AcquireOwner();
                    TextAsset value = null;
                    for (int read = 0; read < borrows; read++)
                    {
                        using var reading = scope.LoadAsync<TextAsset>(TextKey);
                        row.loadCalls++;
                        if (!reading.IsDone) yield return Wait(reading);
                        var next = reading.TakeResult();
                        Require(next && (read == 0 || next == value), "YL Scope did not borrow the same asset.");
                        value = next;
                        row.delivered++;
                    }
                    if (diagnostics)
                    {
                        Require(_system.GetDiagnostics().LeaseCount == 1, "Explicit diagnostic sample did not see the measured Scope lease.");
                        row.diagnosticsSamples++;
                    }
                    using var close = scope.CloseAsync();
                    yield return Wait(close);
                    Require(close.TakeResult(), "Measured Scope did not close.");
                    row.ReleaseOwner();
                }
                if (i + 1 < row.iterations) yield return null;
            }
        }

        private IEnumerator MeasureParallel(Workload row, bool bare)
        {
            const int fanout = 12, abandon = 4;
            var handles = bare ? new AsyncOperationHandle<TextAsset>[fanout] : null;
            var requests = bare ? null : new ResourceRequest<AssetLease<TextAsset>>[fanout];
            var leases = bare ? null : new AssetLease<TextAsset>[fanout];
            for (int iteration = 0; iteration < row.iterations; iteration++)
            {
                try
                {
                    for (int i = 0; i < fanout; i++)
                    {
                        if (bare)
                        {
                            handles[i] = Addressables.LoadAssetAsync<TextAsset>(ParallelKey);
                            row.AcquireOwner();
                        }
                        else requests[i] = _system.AcquireAsync<TextAsset>(ParallelKey);
                        row.loadCalls++;
                    }
                    for (int i = 0; i < abandon; i++)
                    {
                        if (bare)
                        {
                            if (!handles[i].IsDone) row.canceledWhilePending++;
                            Addressables.Release(handles[i]);
                            handles[i] = default;
                            row.ReleaseOwner();
                        }
                        else if (!requests[i].IsDone)
                        {
                            requests[i].Cancel();
                            Require(requests[i].Status == ResourceRequestStatus.Canceled, "The pending YL consumer did not cancel.");
                            row.canceledWhilePending++;
                        }
                        else
                        {
                            Require(requests[i].Status == ResourceRequestStatus.Succeeded, "An abandoned YL consumer failed before cancellation.");
                            requests[i].Dispose(); // already ready: drop the unclaimed result, not a pending cancellation
                        }
                        row.canceledConsumers++;
                    }
                    TextAsset shared = null;
                    for (int i = abandon; i < fanout; i++)
                    {
                        TextAsset text;
                        if (bare)
                        {
                            yield return WaitHandle(handles[i]);
                            text = handles[i].Result;
                        }
                        else
                        {
                            yield return Wait(requests[i]);
                            var lease = leases[i] = requests[i].TakeResult();
                            row.AcquireOwner();
                            text = lease.Asset;
                        }
                        Require(text && (i == abandon || text == shared), "Cancellation interrupted surviving consumers or same-key sharing.");
                        shared = text;
                        row.delivered++;
                    }
                }
                finally
                {
                    for (int i = 0; i < fanout; i++)
                    {
                        if (bare)
                        {
                            if (!handles[i].IsValid()) continue;
                            Addressables.Release(handles[i]); handles[i] = default; row.ReleaseOwner();
                        }
                        else
                        {
                            requests[i]?.Dispose(); requests[i] = null;
                            if (leases[i] == null) continue;
                            leases[i].Dispose(); leases[i] = null; row.ReleaseOwner();
                        }
                    }
                }
                if (iteration + 1 < row.iterations) yield return null;
            }
            Require(row.canceledWhilePending > 0, "The same-key workload did not exercise any pending consumer cancellation.");
        }

        private IEnumerator MeasureLabel(Workload row, bool bare)
        {
            var keys = new[] { BatchLabel };
            for (int i = 0; i < row.iterations; i++)
            {
                row.loadCalls++;
                if (bare)
                {
                    var handle = Addressables.LoadAssetsAsync<TextAsset>(BatchLabel, null, releaseDependenciesOnFailure: false);
                    row.AcquireOwner();
                    try
                    {
                        yield return WaitHandle(handle);
                        var assets = handle.Result;
                        Require(assets.Count == 2, "The real label did not deliver two assets.");
                        CheckLabel(row, assets[0], assets[1]);
                    }
                    finally { Addressables.Release(handle); row.ReleaseOwner(); }
                }
                else
                {
                    using var reading = _system.AcquireBatchAsync<TextAsset>(keys);
                    yield return Wait(reading);
                    using var batch = reading.TakeResult();
                    row.AcquireOwner();
                    var assets = batch.Assets;
                    Require(assets.Count == 2, "The real label did not deliver two assets.");
                    CheckLabel(row, assets[0], assets[1]);
                    batch.Dispose();
                    Require(batch.IsDisposed, "The measured batch lease was not returned.");
                    row.ReleaseOwner();
                }
                if (i + 1 < row.iterations) yield return null;
            }
        }

        private void CheckLabel(Workload row, TextAsset first, TextAsset second)
        {
            Require(first && second && first != second, "The real label did not deliver two distinct live assets.");
            string a = first.text, b = second.text, local = "local:" + _build.buildToken, parallel = "parallel:" + _build.buildToken;
            Require(a == local && b == parallel || a == parallel && b == local, "The batch returned unexpected built bytes.");
            row.delivered += 2;
        }

        private IEnumerator MeasureScenes(Workload row, bool bare)
        {
            int initial = SceneManager.sceneCount;
            for (int i = 0; i < row.iterations; i++)
            {
                Scene scene;
                row.loadCalls++;
                if (bare)
                {
                    var handle = Addressables.LoadSceneAsync(SceneKey, LoadSceneMode.Additive);
                    row.AcquireOwner();
                    bool transferred = false;
                    try
                    {
                        yield return WaitHandle(handle);
                        scene = handle.Result.Scene;
                        Require(scene.isLoaded && SceneManager.sceneCount == initial + 1, "The bare additive scene did not load.");
                        var unload = Addressables.UnloadSceneAsync(handle, autoReleaseHandle: false);
                        transferred = true; // the official unload operation consumes the scene load reference
                        try { yield return WaitHandle(unload); }
                        finally { Addressables.Release(unload); }
                    }
                    finally { if (!transferred && handle.IsValid()) Addressables.Release(handle); }
                }
                else
                {
                    using var loading = _system.LoadSceneAsync(SceneKey, LoadSceneMode.Additive);
                    yield return Wait(loading);
                    using var lease = loading.TakeResult();
                    row.AcquireOwner();
                    scene = lease.Scene;
                    Require(scene.isLoaded && SceneManager.sceneCount == initial + 1, "The YL additive scene did not load.");
                    using var closing = lease.UnloadAsync();
                    yield return Wait(closing);
                    Require(closing.TakeResult() && lease.IsSettled, "The measured scene lease did not settle.");
                }
                Require((!scene.IsValid() || !scene.isLoaded) && SceneManager.sceneCount == initial, "The measured scene did not actually unload.");
                row.ReleaseOwner();
                row.delivered++;
                if (i + 1 < row.iterations) yield return null;
            }
        }

        private IEnumerator MeasureInstances(Workload row, bool bare, bool reuse)
        {
            var handle = default(AsyncOperationHandle<GameObject>);
            AssetLease<GameObject> prefab = null;
            InstanceCache cache = null;
            const int batchSize = 4;
            var instances = new GameObject[batchSize];
            var checkouts = bare ? null : new InstanceLease[batchSize];
            var pooled = reuse ? new HashSet<GameObject>(batchSize) : null;
            try
            {
                row.loadCalls++;
                if (bare)
                {
                    handle = Addressables.LoadAssetAsync<GameObject>(PrefabKey);
                    yield return WaitHandle(handle);
                }
                else
                {
                    using var acquiring = _system.AcquireAsync<GameObject>(PrefabKey);
                    yield return Wait(acquiring);
                    prefab = acquiring.TakeResult();
                    if (reuse) cache = _system.GetOrCreateInstanceCache(prefab);
                }
                row.AcquireOwner();
                for (int i = 0; i < row.iterations; i++)
                {
                    for (int j = 0; j < batchSize; j++)
                    {
                        var instance = bare ? Instantiate(handle.Result) :
                            (checkouts[j] = reuse ? cache.Rent() : _system.Instantiate(prefab)).GameObject;
                        Require(instance && instance.activeInHierarchy && Array.IndexOf(instances, instance, 0, j) < 0,
                            "The measured checkout is not a distinct live active instance.");
                        instances[j] = instance;
                        if (!reuse || pooled.Add(instance)) row.CreateInstance();
                        else row.reusedInstances++;
                        row.delivered++;
                    }
                    for (int j = 0; j < batchSize; j++)
                    {
                        if (bare) Destroy(instances[j]);
                        else checkouts[j].Return();
                    }
                    if (reuse)
                    {
                        int idle = cache.CachedCount;
                        Require(idle == batchSize, "The real pool did not retain all returned instances.");
                        row.peakIdle = Math.Max(row.peakIdle, idle);
                        if (i + 1 < row.iterations) yield return null;
                    }
                    else
                    {
                        yield return Until(() => Destroyed(instances));
                        row.ObserveDeath(batchSize);
                    }
                    Array.Clear(instances, 0, instances.Length);
                }
                if (reuse)
                {
                    cache.Dispose();
                    cache = null;
                    yield return Until(() => Destroyed(pooled));
                    row.ObserveDeath(pooled.Count);
                    Require(row.reusedInstances > 0, "No actual instance identity was reused.");
                }
            }
            finally
            {
                if (bare)
                {
                    foreach (var remaining in instances) if (remaining) Destroy(remaining);
                    // On failure, do not drop the prefab pin ahead of deferred Destroy. This process is exiting.
                    if (Destroyed(instances) && handle.IsValid()) Addressables.Release(handle);
                }
                else
                {
                    foreach (var checkout in checkouts) checkout?.Dispose();
                    cache?.Dispose();
                    prefab?.Dispose();
                }
            }
            row.ReleaseOwner();
        }

        private static bool Destroyed(IEnumerable<GameObject> objects)
        {
            foreach (var instance in objects) if (instance) return false;
            return true;
        }

        private IEnumerator MeasureImages(Workload row, string path, bool caching)
        {
            Texture2D previous = null;
            for (int i = 0; i < row.iterations; i++)
            {
                using var reading = _system.AcquireImageAsync(path);
                row.loadCalls++;
                yield return Wait(reading);
                using var image = reading.TakeResult();
                row.AcquireOwner();
                var texture = image.Asset;
                Require(texture.width == ImageSize && texture.height == ImageSize, "The dynamic image does not match the fixture.");
                if (texture == previous) row.imageCacheHits++;
                previous = texture;
                row.delivered++;
                image.Dispose();
                row.ReleaseOwner();
                int idle = _system.CachedImageCount;
                Require(idle == (caching ? 1 : 0), "The actual image cache policy differs from the workload.");
                row.peakIdle = Math.Max(row.peakIdle, idle);
                if (caching)
                {
                    if (i + 1 < row.iterations) yield return null;
                }
                else yield return Until(() => !texture);
            }
            if (caching)
            {
                Require(row.imageCacheHits == row.iterations - 1, "A cached image was decoded into a different texture.");
                _system.ClearIdleImageCache();
                yield return Until(() => !previous);
            }
        }

        private void Authorize()
        {
            string root = Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
            Require(_build != null && root == Path.GetFullPath(_build.playerRoot), "The Player belongs to another build location.");
            RejectLinks(root);
            RejectLinks(Path.Combine(root, ManifestName));
            var owner = JsonUtility.FromJson<BuildIdentity>(File.ReadAllText(Path.Combine(root, ManifestName)));
            Require(owner.schemaVersion == 1 && owner.purpose == "YLResource.Addressables.PlayerIdentity" &&
                owner.buildToken == _build.buildToken && owner.inputHash == _build.inputHash && owner.contentHash == _build.contentHash &&
                owner.buildFlavor == _build.buildFlavor && owner.scopeCloseBenchmark == _build.scopeCloseBenchmark &&
                owner.encryptedRemoteGroup == _build.encryptedRemoteGroup && owner.verifiedSizeSnapshot == _build.verifiedSizeSnapshot &&
                owner.heldDownload == _build.heldDownload &&
                !string.IsNullOrEmpty(_build.publishedSourceSha256) && owner.publishedSourceSha256 == _build.publishedSourceSha256 &&
                owner.guidOnlyKey == _build.guidOnlyKey && owner.suppressedGuidKey == _build.suppressedGuidKey &&
                !string.IsNullOrEmpty(_build.packedSpriteGuid) && owner.packedSpriteGuid == _build.packedSpriteGuid &&
                _build.buildFlavor == (Debug.isDebugBuild ? "Development" : "Release"),
                "The Player identity is not the generated owned build.");
            var argv = Environment.GetCommandLineArgs();
            string Argument(string name)
            {
                int index = Array.IndexOf(argv, name);
                Require(index >= 0 && index + 1 < argv.Length && Array.LastIndexOf(argv, name) == index, "Missing/duplicate Player argument: " + name);
                return argv[index + 1];
            }
            int round = int.Parse(Argument("-ylResourceAddressablesPlayerRound"));
            Require(round >= 1 && round <= (_build.scopeCloseBenchmark ? 7 : 4), "The Player round is outside this owned build's workload.");
            string result = Path.GetFullPath(Argument("-ylResourceAddressablesPlayerResult"));
            Require(result == Path.Combine(root, "run-" + round + ".json") && !File.Exists(result) && !File.Exists(result + ".quit.json"),
                "Unknown or already-used Player output is refused.");
            var origin = new Uri(Argument("-ylResourceAddressablesContentBaseUrl"));
            Require(origin.Scheme == "http" && origin.Host == "127.0.0.1" && origin.AbsolutePath == "/" + _build.buildToken + "/" &&
                string.IsNullOrEmpty(origin.Query), "Only this build's owned loopback origin is allowed.");
            _baseUrl = origin.AbsoluteUri;
            _result = result;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _report = new Report
            {
                buildToken = _build.buildToken, inputHash = _build.inputHash, contentHash = _build.contentHash,
                publishedSourceSha256 = _build.publishedSourceSha256,
                unityVersion = Application.unityVersion, addressablesVersion = _build.addressablesVersion,
                addressablesAssemblyVersion = typeof(Addressables).Assembly.GetName().Version.ToString(), stripping = _build.stripping,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), platform = Application.platform.ToString(),
                isEditor = Application.isEditor, isBatchMode = Application.isBatchMode, pointerBits = IntPtr.Size * 8,
                round = round, pid = process.Id, runtimeAssembly = typeof(ResourceSystem).Assembly.GetName().Name, buildFlavor = _build.buildFlavor,
                probeAssembly = GetType().Assembly.GetName().Name, startedUtc = DateTime.UtcNow.ToString("o"),
#if ENABLE_IL2CPP
                backend = "IL2CPP"
#else
                backend = "Mono"
#endif
            };
            Require(_report.unityVersion == _build.unityVersion && Application.isBatchMode, "The Player version/batch contract differs.");
        }

        private void CaptureDiagnostics()
        {
            if (_system == null) return;
            var snapshot = _system.GetDiagnostics();
            _report.finalState = snapshot.State.ToString();
            _report.leaseCount = snapshot.LeaseCount;
            _report.scopeCount = snapshot.ScopeCount;
            _report.requestCount = snapshot.RequestCount;
            _report.pendingWorkCount = snapshot.PendingWorkCount;
            _report.drainingHandleCount = snapshot.DrainingHandleCount;
            _report.cleanupErrors = snapshot.Errors.ToArray();
        }

        private void Finish(Exception error)
        {
            if (_finished) return;
            _finished = true;
            if (_report != null)
            {
                try { CaptureDiagnostics(); }
                catch (Exception capture) { error = error == null ? capture : new AggregateException(error, capture); }
                _report.status = error == null ? "Passed" : "Failed";
                _report.error = error?.ToString();
                _report.checks = _checks.ToArray();
                _report.loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(value => value.GetName().Name).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                _report.finishedUtc = DateTime.UtcNow.ToString("o");
                if (_report.packedSprite != null) Debug.Log("[YLResource.PackedSprite] " + JsonUtility.ToJson(_report.packedSprite));
                try { WriteNew(_result, _report); }
                catch (Exception persistence) { error = error == null ? persistence : new AggregateException(error, persistence); }
            }
            if (error != null) Debug.LogException(error);
            _exitCode = error == null ? 0 : 1;
            Application.Quit(_exitCode);
        }

        private void OnApplicationQuit()
        {
            if (!_finished || _report == null || _result == null) return;
            WriteNew(_result + ".quit.json", new QuitReceipt
            {
                buildToken = _build.buildToken, inputHash = _build.inputHash, contentHash = _build.contentHash,
                pid = _report.pid, round = _report.round, requestedExitCode = _exitCode, utc = DateTime.UtcNow.ToString("o")
            });
        }

        private static IEnumerator Wait<T>(ResourceRequest<T> request) => Until(() => request.IsDone);
        private static IEnumerator WaitHandle<T>(AsyncOperationHandle<T> handle)
        {
            yield return Until(() => handle.IsDone);
            Require(handle.Status == AsyncOperationStatus.Succeeded, "Official Addressables operation failed: " + handle.OperationException);
        }

        private static IEnumerator Until(Func<bool> predicate)
        {
            float deadline = Time.realtimeSinceStartup + 45;
            while (!predicate())
            {
                Require(Time.realtimeSinceStartup < deadline, "The actual Player operation did not settle within 45 seconds.");
                yield return null;
            }
        }

        private static void WriteNew(string path, object value)
        {
            RejectLinks(path);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(value, true));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        private static void RejectLinks(string path)
        {
            for (string cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked Player identity/cache/output paths are refused.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
