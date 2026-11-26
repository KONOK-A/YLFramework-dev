using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using YLFramework.Resource;
#if YL_CONTENT_UPDATE_ENCRYPTION
using YLFramework.Resource.Encryption;
#endif

namespace YLFramework.Testing
{
    public static class ContentUpdatePlayerProbe
    {
        private const string RequestArgument = "-ylContentUpdateRequest";
#if ENABLE_IL2CPP
        private const string Backend = "IL2CPP";
#elif ENABLE_MONO
        private const string Backend = "Mono";
#else
        private const string Backend = "Unknown";
#endif

        [Serializable]
        private sealed class Request
        {
            public string resultPath, userDataPath, baseUrl, announcementRoute, operation, stopAt, stopMarkerPath, releaseMarkerPath, stopArtifactSha256;
            public long stopArtifactLength;
            public string k1Path, k2Path, oldAddress, newAddress, preexisting, legacyCacheCatalog;
            public string optionalAddress, retainedAddress, sceneAddress, sceneRootName, sceneObjectName, atlasAddress, atlasSpriteName;
            public string[] optionalRoutes, retainedRoutes;
            public bool encrypted, observeTransfers;
            public string spaceFaultRoute;
            public long spaceBeforeBytes, spaceWriteLimit;
            public string reclaimTestPart, inspectStagingRoute, inspectStagingPath, holdRejectedCheckpoint;
            public long spaceAfterReclaimBytes;
            public long transferCancelAfterBytes;
            public string transferCancelRoute;
        }

        [Serializable]
        private sealed class PlayerTrust
        {
            public string application, channel, platform, compatibility, settingsSha256, baseUrl, keyId, modulus, exponent;
        }

        [Serializable]
        private sealed class Result
        {
            public int pid, bits, cleanupRemoved;
            public long generation, activeSequence, previousSequence, highestSequence;
            public long keyRents, keyReturns, key1Requests, key2Requests, authenticatedBundles, mountAttempts;
            public long prepareKeyRents, prepareMountAttempts, prepareKeyRequests, startupMountAttempts;
            public string status, error, operationStatus, operationDetail, bootStatus, bootError, bootDetail, unity;
            public string backend, architecture, graphics, activeDigest, previousDigest, highestDigest, legacyCacheSha256;
            public Material old, @new;
            public Held held;
            public OptionalContent optional;
            public ScopeGuard scopeGuard;
            public SceneRead scene;
            public AtlasRead atlas;
            public CacheReport cacheBefore, cacheAfter;
            public OfficialCacheEvidence officialCacheBefore, officialCacheAfter;
            public CacheFile inFlightStage;
            public long heldRejectedCheckpointBytes;
            public bool spacePreflightSawOwnedPart;
            public long spaceFirstAvailableBytes;
            public int spacePreflightChecks;
            public TransferRead[] transferReads;
            public long spaceFaultStageBytes;
            public int spaceFaultHResult;
        }

        [Serializable]
        private sealed class OfficialCacheEvidence
        {
            public string catalogPath, catalogSha256, catalogFileId, markerSha256, markerFileId;
        }

        [Serializable]
        private sealed class Material
        {
            public bool found;
            public string assetSha256, dependencySha256, ordinaryDependencySha256;
        }

        [Serializable]
        private sealed class Held
        {
            public Material before, after;
            public bool newFound;
        }

        [Serializable]
        private sealed class OptionalContent
        {
            public string manifestDigest, coldError, coldDetail, coldDownloadError, assetSha256;
            public string[] routes, closure;
            public long expectedSize, sizeBefore = -1, sizeAfter = -1;
            public long prepareKeyRents, prepareMountAttempts, prepareKeyRequests;
            public Material material;
        }

        [Serializable]
        private sealed class ScopeGuard
        {
            public string error;
            public int pendingWorkBefore, pendingWorkAfter;
            public Material control;
            public bool closed, stopped;
        }

        [Serializable]
        private sealed class SceneRead
        {
            public string name, rootName, textSha256;
            public bool unloaded;
        }

        [Serializable]
        private sealed class AtlasRead
        {
            public string spriteName, spriteAddress;
            public int width, height;
        }

        [Serializable]
        private sealed class Marker
        {
            public int pid;
            public string point;
            public string path, activeDigest;
            public long generation, offset;
        }

        [Serializable]
        private sealed class CacheCount
        {
            public string certainty;
            public string bytes;
            public bool hasBytes;
        }

        [Serializable]
        private sealed class TransferRead
        {
            public string target, firstUtc, lastUtc;
            public long bytes;
            public int reads;
        }

        [Serializable]
        private sealed class CacheFile
        {
            public string relativePath, kind, disposition, retention;
            public string bytes;
            public bool hasBytes, ownershipConfirmed;
        }

        [Serializable]
        private sealed class CacheReport
        {
            public string owner, root, application, channel, observedAt;
            public CacheCount owned, retained, cleanupCandidates, undetermined;
            public CacheFile[] files;
            public string[] issues;
        }

        private static CacheReport SnapshotCache(ResourceCacheUsageReport report) => new CacheReport
        {
            owner = report.Owner.ToString(), root = report.Root, application = report.Application, channel = report.Channel,
            observedAt = report.ObservedAtUtc.ToString("o"), owned = Count(report.Owned), retained = Count(report.Retained),
            cleanupCandidates = Count(report.CleanupCandidates), undetermined = Count(report.Undetermined),
            issues = report.Issues.ToArray(),
            files = report.Files.Select(file => new CacheFile
            {
                relativePath = file.RelativePath, kind = file.Kind.ToString(), disposition = file.Disposition.ToString(),
                retention = file.Retention.ToString(), bytes = file.Bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture), hasBytes = file.Bytes.HasValue,
                ownershipConfirmed = file.OwnershipConfirmed
            }).ToArray()
        };

        private static CacheCount Count(ResourceCacheByteCount count) => new CacheCount
        {
            certainty = count.Certainty.ToString(), bytes = count.Bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture), hasBytes = count.Bytes.HasValue
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static async void Start()
        {
            var arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, RequestArgument);
            if (index < 0)
                return;
            Request request = null;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var result = new Result
            {
                pid = process.Id,
                bits = IntPtr.Size * 8,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                graphics = SystemInfo.graphicsDeviceType.ToString(),
                unity = Application.unityVersion,
                backend = Backend
            };

            try
            {
                request = JsonUtility.FromJson<Request>(File.ReadAllText(arguments[index + 1]));
                Application.runInBackground = true;
                Require(!Application.isEditor && !Debug.isDebugBuild && Application.isBatchMode &&
                    Application.platform == RuntimePlatform.WindowsPlayer && result.architecture == "X64" &&
                    result.bits == 64 && result.graphics != "Null" && Backend != "Unknown",
                    "The probe requires a real Win64 Release graphics Player.");
                await RunAsync(request, result);
                result.status = "Passed";
                WriteAtomic(request.resultPath, JsonUtility.ToJson(result, true));
                Application.Quit(0);
            }
            catch (Exception failure)
            {
                result.status = "Failed";
                result.error = failure.ToString();
                Debug.LogException(failure);
                if (!string.IsNullOrEmpty(request?.resultPath))
                    WriteAtomic(request.resultPath, JsonUtility.ToJson(result, true));
                Application.Quit(1);
            }
        }

        private static async Task RunAsync(Request request, Result result)
        {
            var embedded = Resources.Load<TextAsset>("YLContentUpdateTrust");
            Require(embedded != null, "The installed Player has no embedded trust configuration.");
            var configuration = JsonUtility.FromJson<PlayerTrust>(embedded.text);
            Require(configuration.baseUrl == request.baseUrl, "The requested publisher differs from the sealed Player.");
            var publisher = new Uri(configuration.baseUrl);
            var publicKey = new RSAParameters
            {
                Modulus = Convert.FromBase64String(configuration.modulus),
                Exponent = Convert.FromBase64String(configuration.exponent)
            };
            var trust = new ResourceContentManifestTrust(configuration.application, configuration.channel, configuration.platform,
                configuration.compatibility, configuration.settingsSha256,
                new Dictionary<string, RSAParameters>(StringComparer.Ordinal) { [configuration.keyId] = publicKey },
                new[] { publisher }, allowLoopbackHttp: true);

            IResourceContentExtension extension = null;
#if YL_CONTENT_UPDATE_ENCRYPTION
            Require(request.encrypted, "The installed test Player contains the encryption extension.");
            var keys = new Keys(request.k1Path, request.k2Path);
            var encrypted = new EncryptedResourceContentExtension(keys, allowLoopback: true);
            extension = encrypted;
#else
            Require(!request.encrypted, "The ordinary Player cannot accept encrypted content.");
#endif
            var updates = new ResourceContentUpdates(trust, new Uri(publisher, request.announcementRoute), request.userDataPath, extension);
            using var cancelTransfer = request.transferCancelAfterBytes > 0 ? new CancellationTokenSource() : null;
            var transfers = new Dictionary<string, TransferRead>(StringComparer.Ordinal);
            if (request.observeTransfers)
            {
                updates.ArtifactReceived = (uri, count) =>
                {
                    string now = DateTime.UtcNow.ToString("o");
                    if (!transfers.TryGetValue(uri.AbsolutePath, out var row))
                    {
                        row = new TransferRead { target = uri.AbsolutePath, firstUtc = now };
                        transfers.Add(row.target, row);
                    }
                    row.bytes += count;
                    row.reads++;
                    row.lastUtc = now;
                    if (cancelTransfer != null && uri.AbsolutePath == request.transferCancelRoute &&
                        row.bytes >= request.transferCancelAfterBytes)
                        cancelTransfer.Cancel();
                };
            }
#if !YL_CONTENT_UPDATE_REPLAY && YL_CONTENT_UPDATE_TUNED
            if (!string.IsNullOrEmpty(request.reclaimTestPart))
            {
                updates.AvailableSpaceOverride = root =>
                {
                    Require(Path.GetDirectoryName(request.reclaimTestPart) == root, "The reclaim probe must observe its signed objects directory.");
                    bool exists = File.Exists(request.reclaimTestPart);
                    long available = exists ? request.spaceBeforeBytes : request.spaceAfterReclaimBytes;
                    if (result.spacePreflightChecks++ == 0)
                    {
                        result.spacePreflightSawOwnedPart = exists;
                        result.spaceFirstAvailableBytes = available;
                    }
                    return available;
                };
            }
#endif
            if (!string.IsNullOrEmpty(request.spaceFaultRoute))
            {
#if YL_CONTENT_UPDATE_REPLAY
                throw new InvalidOperationException("Space fault instrumentation is unavailable in this baseline replay.");
#else
#if YL_CONTENT_UPDATE_TUNED
                updates.AvailableSpaceOverride = _ => request.spaceBeforeBytes;
#endif
                updates.BeforeArtifactWrite = (uri, output, count) =>
                {
                    if (uri.AbsolutePath != request.spaceFaultRoute || output.Position + count <= request.spaceWriteLimit)
                        return;
                    // The prior writes and this stage are real. Inject disk full at the next write boundary.
                    output.Flush(true);
                    result.spaceFaultStageBytes = output.Length;
                    result.spaceFaultHResult = unchecked((int)0x80070070);
                    throw new IOException("Fixture exhausted the staging file space.", result.spaceFaultHResult);
                };
#endif
            }
#if !YL_CONTENT_UPDATE_REPLAY
            if (!string.IsNullOrEmpty(request.inspectStagingRoute))
            {
                updates.BeforeArtifactWrite = (uri, output, _) =>
                {
                    if (uri.AbsolutePath != request.inspectStagingRoute || output.Position == 0 || result.inFlightStage != null)
                        return;
                    output.Flush(true);
                    string path = string.IsNullOrEmpty(request.inspectStagingPath) ? output.Name : request.inspectStagingPath;
#if YL_CONTENT_UPDATE_TUNED
                    if (!string.IsNullOrEmpty(request.inspectStagingPath))
                        Require(ResourceContentFileIO.ReadFileIdentity(output) == ResourceContentFileIO.ReadFileIdentity(path),
                            "The inspected resume file did not match the receiving handle.");
#endif
                    string relative = "objects/" + Path.GetFileName(path);
                    var report = SnapshotCache(updates.InspectCacheAsync().GetAwaiter().GetResult());
                    var stage = report.files.SingleOrDefault(file => file.relativePath == relative);
                    Require(stage != null && stage.ownershipConfirmed && stage.disposition == "Retained" &&
                        stage.retention.Contains("Preparation") && stage.hasBytes && long.Parse(stage.bytes) == output.Length,
                        "The same service did not identify its active staging file by its actual bytes.");
                    result.inFlightStage = stage;
                };
            }
#endif
            if (request.operation == "cache-inspect")
            {
                Require(!string.IsNullOrEmpty(request.legacyCacheCatalog), "Inspection needs an official cache sentinel.");
                string sentinel = SeedOfficialCatalog(request.legacyCacheCatalog, Guid.NewGuid().ToString("N"));
                result.officialCacheBefore = SnapshotOfficialCatalog(sentinel);
                result.cacheBefore = SnapshotCache(await updates.InspectCacheAsync());
                result.cacheAfter = SnapshotCache(await updates.InspectCacheAsync());
                result.officialCacheAfter = SnapshotOfficialCatalog(sentinel);
                var state = updates.ReadState();
                result.generation = state?.Generation ?? 0;
                result.activeDigest = state?.ActiveDigest;
#if YL_CONTENT_UPDATE_ENCRYPTION
                result.prepareKeyRequests = keys.TotalRequests;
                result.mountAttempts = encrypted.MountAttempts;
                Require(result.prepareKeyRequests == 0 && result.mountAttempts == 0, "Inspection performed protected content work.");
#endif
                result.operationStatus = "Inspected";
                return;
            }
            if (request.stopAt == "before-ready")
            {
                updates.ArtifactPublishedBoundary = path =>
                {
                    updates.ArtifactPublishedBoundary = null;
                    var state = updates.ReadState();
                    WriteAtomic(request.stopMarkerPath, JsonUtility.ToJson(new Marker
                    {
                        pid = result.pid, point = request.stopAt, path = path, generation = state.Generation, activeDigest = state.ActiveDigest
                    }));
                    var limit = DateTime.UtcNow.AddSeconds(140);
                    while (!File.Exists(request.releaseMarkerPath))
                    {
                        if (DateTime.UtcNow >= limit)
                            throw new TimeoutException("The independent cache observer did not release the writer.");
                        Thread.Sleep(25);
                    }
                };
            }
            else if (request.stopAt == "before-verified-publish" || request.stopAt == "before-checkpoint-record")
            {
#if !YL_CONTENT_UPDATE_REPLAY && YL_CONTENT_UPDATE_TUNED
                if (request.stopAt == "before-checkpoint-record")
                {
                    updates.BeforeCheckpointRecordWrite = (path, offset) =>
                    {
                        if (offset == request.stopArtifactLength)
                            StopCheckpoint(path, offset);
                    };
                }
                else
                {
                    updates.BeforeVerifiedArtifactPublish = path => StopCheckpoint(path, request.stopArtifactLength);
                }

                void StopCheckpoint(string path, long offset)
                {
                    if (Path.GetFileName(path) != ".resume-" + request.stopArtifactSha256 + ".bundle.part")
                        return;
                    WriteAtomic(request.stopMarkerPath, JsonUtility.ToJson(new Marker
                    {
                        pid = result.pid, point = request.stopAt, path = path, offset = offset
                    }));
                    using var held = new ManualResetEvent(false);
                    if (!held.WaitOne(TimeSpan.FromSeconds(60)))
                        throw new TimeoutException("The controller did not observe the forced-exit checkpoint.");
                }
#else
                throw new InvalidOperationException("This Player did not compile the checkpoint test seam.");
#endif
            }
            else if (!string.IsNullOrEmpty(request.stopAt))
            {
                updates.StateCommitBoundary = after =>
                {
                    string point = after ? "after-commit" : "before-commit";
                    if (point != request.stopAt)
                        return;
                    WriteAtomic(request.stopMarkerPath, JsonUtility.ToJson(new Marker { pid = result.pid, point = point }));
                    using var held = new ManualResetEvent(false);
                    held.WaitOne();
                };
            }

            string cacheCatalog = null;
            string seededCacheDigest = null;
            if (!string.IsNullOrEmpty(request.legacyCacheCatalog))
            {
                cacheCatalog = SeedOfficialCatalog(request.legacyCacheCatalog, "v1");
                seededCacheDigest = Hash(File.ReadAllBytes(request.legacyCacheCatalog));
            }

            var startup = new ResourceStartup(null, false, null, updates);
            if (request.preexisting == "locator")
                Addressables.AddResourceLocator(new ResourceLocationMap("foreign-catalog"));
            if (request.preexisting == "transform")
                Addressables.InternalIdTransformFunc = location => location.InternalId;

            if (request.preexisting == "locator" || request.preexisting == "transform")
            {
                try
                {
                    await startup.StartAsync();
                }
                catch (ResourceException error) when (error.Kind == ResourceError.ConfigurationConflict)
                {
                    result.bootStatus = "GuardRejected";
                    result.operationStatus = "GuardRejected";
                    RecordState(updates, result);
                    return;
                }
                throw new InvalidOperationException("Signed startup accepted an incompatible Addressables owner.");
            }

            var start = startup.StartAsync();
            if (request.preexisting == "concurrent")
            {
                var concurrent = new ResourceStartup(null, false, null, updates);
                try
                {
                    await concurrent.StartAsync();
                    throw new InvalidOperationException("A second ResourceStartup took control during signed startup.");
                }
                catch (ResourceException error) when (error.Kind == ResourceError.ConfigurationConflict)
                {
                    result.operationStatus = "GuardRejected";
                }
            }
            if (request.operation == "missing-state")
            {
                try
                {
                    await start;
                }
                catch (ResourceException error) when (error.Kind == ResourceError.LoadFailed &&
                    error.InnerException is InvalidDataException invalid &&
                    invalid.Message.Contains("committed resource content state is missing", StringComparison.Ordinal))
                {
                    Require(!startup.OwnsResourceSystem && !Addressables.ResourceLocators.Any() &&
                        Addressables.InternalIdTransformFunc == null, "Missing committed state left startup routing changes.");
                    result.bootStatus = result.operationStatus = "StateRejected";
                    result.bootError = error.Kind.ToString();
                    result.bootDetail = error.ToString();
                    return;
                }
                throw new InvalidOperationException("Signed startup reset previously committed installation history.");
            }
            await start;

            result.bootStatus = updates.LastStartup.Status.ToString();
            result.bootError = updates.LastStartup.Rejection is ResourceException rejected
                ? rejected.Kind.ToString() : updates.LastStartup.Rejection?.GetType().Name;
            result.bootDetail = updates.LastStartup.Rejection?.Message;
#if YL_CONTENT_UPDATE_ENCRYPTION
            result.startupMountAttempts = encrypted.MountAttempts;
            Require(result.startupMountAttempts == 0, "An encrypted Bundle mounted before business access.");
#endif
            Require(Addressables.ResourceLocators.Count() == 2, "The Player did not register exactly one business Catalog.");

            using var scope = startup.Resources.CreateScope("signed content acceptance");
            result.old = Measure(await scope.LoadAsync<ContentUpdateMaterial>(request.oldAddress));
            bool found = await HasLocationAsync(request.newAddress);
            result.@new = found ? Measure(await scope.LoadAsync<ContentUpdateMaterial>(request.newAddress)) : new Material { found = false };
            Scene scene = await ReadSceneAsync(scope, request, result);
            if (!string.IsNullOrEmpty(request.atlasAddress))
                result.atlas = await ReadAtlasAsync(scope, request.atlasAddress, request.atlasSpriteName);

            switch (request.operation)
            {
                case "prepare":
                {
                    using var lease = await startup.Resources.AcquireAsync<ContentUpdateMaterial>(request.oldAddress);
                    result.held = new Held { before = Measure(lease.Asset) };
#if YL_CONTENT_UPDATE_ENCRYPTION
                    long keyRents = encrypted.KeyRents;
                    long mountAttempts = encrypted.MountAttempts;
                    long keyRequests = keys.TotalRequests;
#endif
                    var checkedUpdate = await updates.CheckAsync();
                    result.operationStatus = checkedUpdate.Status.ToString();
                    result.operationDetail = checkedUpdate.Rejection?.ToString() + " " + checkedUpdate.Detail;
                    if (checkedUpdate.Status == ResourceContentCheckStatus.Available)
                    {
                        FileStream heldRejected = null;
#if !YL_CONTENT_UPDATE_REPLAY && YL_CONTENT_UPDATE_TUNED
                        if (!string.IsNullOrEmpty(request.holdRejectedCheckpoint))
                        {
                            updates.AfterCheckpointIntegrityFailure = path =>
                            {
                                if (Path.GetFileName(path) != ".resume-" + request.holdRejectedCheckpoint + ".bundle.part")
                                    return;
                                heldRejected = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                                result.heldRejectedCheckpointBytes = heldRejected.Length;
                            };
                        }
#endif
                        try
                        {
                            await updates.PrepareAsync(checkedUpdate, cancelTransfer?.Token ?? default);
                            result.operationStatus = "Prepared";
                        }
                        catch (Exception error) when (request.observeTransfers &&
                            error is OperationCanceledException or IOException or System.Net.Http.HttpRequestException or InvalidDataException or ResourceException)
                        {
                            result.operationStatus = error is OperationCanceledException
                                ? (cancelTransfer?.IsCancellationRequested == true ? "TransferCanceled" : "TransferTimeout") :
                                error is ResourceException resourceFailure ? resourceFailure.Kind.ToString() : "TransferFailed";
                            result.operationDetail = error.ToString();
                        }
                        finally { heldRejected?.Dispose(); }
                    }
                    if (request.observeTransfers)
                        result.transferReads = transfers.Values.ToArray();
#if YL_CONTENT_UPDATE_ENCRYPTION
                    result.prepareKeyRents = encrypted.KeyRents - keyRents;
                    result.prepareMountAttempts = encrypted.MountAttempts - mountAttempts;
                    result.prepareKeyRequests = keys.TotalRequests - keyRequests;
#endif
                    result.held.after = Measure(lease.Asset);
                    result.held.newFound = await HasLocationAsync(request.newAddress);
                    break;
                }
                case "retry":
                    Require(await updates.RetryPreparedAsync(), "No local candidate was available for explicit retry.");
                    result.operationStatus = "RetryQueued";
                    break;
                case "rollback":
                    Require(await updates.RequestRollbackAsync(), "No previous release was available for explicit rollback.");
                    result.operationStatus = "RollbackQueued";
                    break;
                case "cleanup":
                    result.cleanupRemoved = await updates.CleanupAsync();
                    result.operationStatus = "CleanupComplete";
                    break;
                case "optional-cold":
                    result.optional = DescribeOptional(updates.LastStartup.Manifest, request);
                    await ReadColdOptionalAsync(startup.Resources, scope, request, result.optional);
                    result.operationStatus = "OptionalCold";
                    break;
                case "optional-prepare":
                {
                    result.optional = DescribeOptional(updates.LastStartup.Manifest, request);
                    using var lease = await startup.Resources.AcquireAsync<ContentUpdateMaterial>(found ? request.newAddress : request.oldAddress);
                    result.held = new Held { before = Measure(lease.Asset) };
                    await ReadColdOptionalAsync(startup.Resources, scope, request, result.optional);
#if YL_CONTENT_UPDATE_ENCRYPTION
                    long keyRents = encrypted.KeyRents;
                    long mountAttempts = encrypted.MountAttempts;
                    long keyRequests = keys.TotalRequests;
#endif
                    await updates.PrepareActiveAsync(result.optional.routes);
#if YL_CONTENT_UPDATE_ENCRYPTION
                    result.optional.prepareKeyRents = encrypted.KeyRents - keyRents;
                    result.optional.prepareMountAttempts = encrypted.MountAttempts - mountAttempts;
                    result.optional.prepareKeyRequests = keys.TotalRequests - keyRequests;
                    Require(result.optional.prepareKeyRents == 0 && result.optional.prepareMountAttempts == 0 &&
                        result.optional.prepareKeyRequests == 0, "Optional preparation requested a key or tried to mount a Bundle.");
#endif
                    await ReadPreparedOptionalAsync(startup.Resources, scope, request, result.optional);
                    result.held.after = Measure(lease.Asset);
                    result.held.newFound = await HasLocationAsync(request.newAddress);
                    result.operationStatus = "OptionalPrepared";
                    break;
                }
                case "optional-read":
                    result.optional = DescribeOptional(updates.LastStartup.Manifest, request);
                    await ReadPreparedOptionalAsync(startup.Resources, scope, request, result.optional);
                    result.operationStatus = "OptionalRead";
                    break;
                case "scope-guard":
                    result.scopeGuard = new ScopeGuard();
                    await VerifyScopeGuardAsync(startup.Resources, request.oldAddress, result.scopeGuard);
                    result.operationStatus = "ScopeGuarded";
                    break;
                case "read":
                    result.operationStatus ??= "Read";
                    break;
                default:
                    throw new ArgumentException("Unknown signed update probe operation.");
            }

            if (cacheCatalog != null)
            {
                result.legacyCacheSha256 = Hash(File.ReadAllBytes(cacheCatalog));
                Require(seededCacheDigest == result.legacyCacheSha256, "Startup changed the existing official Catalog cache.");
            }
            RecordState(updates, result);
#if YL_CONTENT_UPDATE_ENCRYPTION
            result.keyRents = encrypted.KeyRents;
            result.keyReturns = encrypted.KeyReturns;
            result.authenticatedBundles = encrypted.AuthenticatedBundles;
            result.mountAttempts = encrypted.MountAttempts;
            result.key1Requests = keys.Requests("K1");
            result.key2Requests = keys.Requests("K2");
#endif
            Require(await scope.CloseAsync(), "Acceptance scope did not finish closing.");
            if (result.scene != null)
            {
                result.scene.unloaded = !scene.isLoaded;
                Require(result.scene.unloaded, "Acceptance scope did not unload the scene.");
            }
            bool stopped = await startup.StopAsync();
            if (result.scopeGuard != null)
                result.scopeGuard.stopped = stopped;
            Require(stopped, "Resource startup did not finish stopping.");
        }

        private static string SeedOfficialCatalog(string source, string suffix)
        {
            Require(Application.companyName == "YLFramework Acceptance" &&
                Application.productName.StartsWith("YLResourceUpdates-", StringComparison.Ordinal),
                "The fixture must use the private Player application identity for its official cache sentinel.");
            string extension = Path.GetExtension(source);
            Require(extension == ".json" || extension == ".bin", "Expected a Packed Catalog as the official cache fixture.");
            string root = Path.Combine(Application.persistentDataPath, "com.unity.addressables");
            Directory.CreateDirectory(root);
            string cacheBase = Path.Combine(root, "catalog_yl-content-updates-" + suffix);
            string catalog = cacheBase + extension;
            var bytes = File.ReadAllBytes(source);
            using (var output = new FileStream(catalog, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            WriteAtomic(cacheBase + ".hash", Hash(bytes).Substring(0, 32));
            return catalog;
        }

        private static OfficialCacheEvidence SnapshotOfficialCatalog(string path)
        {
            string marker = Path.ChangeExtension(path, ".hash");
            var catalogId = ResourceContentFileIO.ReadFileStamp(path);
            var markerId = ResourceContentFileIO.ReadFileStamp(marker);
            return new OfficialCacheEvidence
            {
                catalogPath = path,
                catalogSha256 = Hash(File.ReadAllBytes(path)),
                catalogFileId = $"{catalogId.Volume:x8}:{catalogId.File:x16}",
                markerSha256 = Hash(File.ReadAllBytes(marker)),
                markerFileId = $"{markerId.Volume:x8}:{markerId.File:x16}"
            };
        }

        private static OptionalContent DescribeOptional(ResourceContentManifest manifest, Request request)
        {
            bool chapter = !string.IsNullOrEmpty(request.optionalAddress);
            Require(chapter != !string.IsNullOrEmpty(request.retainedAddress), "Expected one address for the optional content probe.");
            var routes = chapter ? request.optionalRoutes : request.retainedRoutes;
            Require(routes != null && routes.Length != 0, "Expected an optional signed artifact route.");
            var startupRoutes = new HashSet<string>(manifest.StartupRoutes, StringComparer.Ordinal);
            var closure = new HashSet<string>(StringComparer.Ordinal);
            var remaining = new Stack<string>(routes);
            while (remaining.Count != 0)
            {
                string route = remaining.Pop();
                Require(manifest.Artifacts.ContainsKey(route), "The optional route is absent from the selected signed manifest: " + route);
                if (startupRoutes.Contains(route) || !closure.Add(route))
                    continue;
                foreach (string dependency in manifest.Artifacts[route].Dependencies)
                    remaining.Push(dependency);
            }
            Require(routes.All(closure.Contains), "The requested optional artifact is already required for startup.");
            Require(!chapter || closure.Except(routes, StringComparer.Ordinal).Any(),
                "The optional chapter fixture needs a separate non-startup transitive dependency.");
            return new OptionalContent
            {
                manifestDigest = manifest.Digest,
                routes = routes,
                closure = closure.OrderBy(route => route, StringComparer.Ordinal).ToArray(),
                expectedSize = closure.Sum(route => manifest.Artifacts[route].Length)
            };
        }

        private static async Task ReadColdOptionalAsync(ResourceSystem resources, ResourceScope scope, Request request, OptionalContent observed)
        {
            string address = string.IsNullOrEmpty(request.optionalAddress) ? request.retainedAddress : request.optionalAddress;
            var keys = new[] { address };
            observed.sizeBefore = await resources.GetDownloadSizeAsync(keys);
            Require(observed.sizeBefore > 0 && observed.sizeBefore == observed.expectedSize,
                $"Cold optional size was {observed.sizeBefore}, expected {observed.expectedSize} signed bytes.");
            try
            {
                await resources.DownloadDependenciesAsync(keys);
            }
            catch (ResourceException error) when (error.Kind == ResourceError.SourceUnavailable)
            {
                observed.coldDownloadError = error.Kind.ToString();
            }
            Require(observed.coldDownloadError != null, "Downloading cold signed dependencies did not report SourceUnavailable.");
            try
            {
                await ReadOptionalAssetAsync(scope, request, observed);
            }
            catch (ResourceException error) when (error.Kind is ResourceError.SourceUnavailable or ResourceError.LoadFailed)
            {
                observed.coldError = error.Kind.ToString();
                observed.coldDetail = error.ToString();
                return;
            }
            throw new InvalidOperationException("A cold optional asset became available without explicit preparation.");
        }

        private static async Task ReadPreparedOptionalAsync(ResourceSystem resources, ResourceScope scope, Request request, OptionalContent observed)
        {
            string address = string.IsNullOrEmpty(request.optionalAddress) ? request.retainedAddress : request.optionalAddress;
            observed.sizeAfter = await resources.GetDownloadSizeAsync(new[] { address });
            Require(observed.sizeAfter == 0, $"Prepared optional content still reports {observed.sizeAfter} bytes to download.");
            await ReadOptionalAssetAsync(scope, request, observed);
        }

        private static async Task ReadOptionalAssetAsync(ResourceScope scope, Request request, OptionalContent observed)
        {
            if (!string.IsNullOrEmpty(request.optionalAddress))
            {
                observed.material = Measure(await scope.LoadAsync<ContentUpdateMaterial>(request.optionalAddress));
                return;
            }
            var asset = await scope.LoadAsync<TextAsset>(request.retainedAddress);
            Require(asset != null, "The retained TextAsset could not be read.");
            observed.assetSha256 = Hash(asset.bytes);
        }

        private static async Task VerifyScopeGuardAsync(ResourceSystem resources, string address, ScopeGuard observed)
        {
            using var scope = resources.CreateScope("signed content scope routing guard");
            int precedingWork = resources.GetDiagnostics().PendingWorkCount;
            using var queued = scope.LoadAsync<ContentUpdateMaterial>(address);
            observed.pendingWorkBefore = resources.GetDiagnostics().PendingWorkCount - precedingWork;
            Require(!queued.IsDone && observed.pendingWorkBefore == 1, "Expected a queued scoped asset load before changing the Locator.");

            var foreign = new ResourceLocationMap("foreign-catalog-after-scope-enqueue");
            Addressables.AddResourceLocator(foreign);
            try
            {
                await queued;
                throw new InvalidOperationException("A queued Scope load used a modified Catalog.");
            }
            catch (ResourceException error) when (error.Kind == ResourceError.ConfigurationConflict)
            {
                observed.error = error.Kind.ToString();
            }
            finally
            {
                Addressables.RemoveResourceLocator(foreign);
            }

            observed.control = Measure(await scope.LoadAsync<ContentUpdateMaterial>(address));
            using var close = scope.CloseAsync();
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!close.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                await Task.Yield();
            Require(close.IsDone, "The Scope retained work after its queued load was rejected.");
            observed.closed = await close;
            await Task.Yield();
            var remaining = resources.GetDiagnostics();
            observed.pendingWorkAfter = remaining.PendingWorkCount;
            Require(observed.closed && observed.pendingWorkAfter == 0 && remaining.DrainingHandles.Count == 0 &&
                !remaining.Scopes.Any(item => item.Owner == scope.OwnerLabel),
                "The queued Scope failure left requests or ownership after closing.");
        }

        private static async Task<Scene> ReadSceneAsync(ResourceScope scope, Request request, Result result)
        {
            if (string.IsNullOrEmpty(request.sceneAddress))
                return default;
            var scene = await scope.LoadSceneAsync(request.sceneAddress);
            Require(scene.IsValid() && scene.isLoaded, "The signed scene did not load.");
            var root = scene.GetRootGameObjects().Single(item => item.name == request.sceneRootName);
            string witnessName = string.IsNullOrEmpty(request.sceneObjectName) ? request.sceneRootName : request.sceneObjectName;
            var witness = root.GetComponentsInChildren<TextMesh>(true)
                .Single(item => item.gameObject.name == witnessName);
            result.scene = new SceneRead
            {
                name = scene.name,
                rootName = root.name,
                textSha256 = Hash(Encoding.UTF8.GetBytes(witness.text))
            };
            return scene;
        }

        private static async Task<AtlasRead> ReadAtlasAsync(ResourceScope scope, string address, string spriteName)
        {
            var atlas = await scope.LoadAsync<SpriteAtlas>(address);
            var sprite = atlas.GetSprite(spriteName);
            Require(sprite != null, "The signed Atlas did not contain the requested packed Sprite.");
            try
            {
                Require(sprite.texture != null, "The packed Sprite has no texture.");
                Require(sprite.name == spriteName || sprite.name == spriteName + "(Clone)", "The packed Sprite had an unexpected identity.");
                string spriteAddress = address + "[" + spriteName + "]";
                var addressed = await scope.LoadAsync<Sprite>(spriteAddress);
                Require(addressed != null && addressed.name == sprite.name && addressed.texture == sprite.texture && addressed.rect == sprite.rect,
                    "The signed Sprite sub-address did not match the packed Atlas Sprite.");
                return new AtlasRead
                {
                    spriteName = spriteName,
                    spriteAddress = spriteAddress,
                    width = (int)addressed.rect.width,
                    height = (int)addressed.rect.height
                };
            }
            finally
            {
                UnityEngine.Object.Destroy(sprite);
            }
        }

        private static void RecordState(ResourceContentUpdates updates, Result result)
        {
            var state = updates.ReadState();
            if (state == null)
                return;
            result.generation = state.Generation;
            result.activeSequence = state.ActiveSequence;
            result.previousSequence = state.PreviousSequence ?? 0;
            result.highestSequence = state.HighestSequence;
            result.activeDigest = state.ActiveDigest;
            result.previousDigest = state.PreviousDigest;
            result.highestDigest = state.HighestDigest;
        }

        private static async Task<bool> HasLocationAsync(string address)
        {
            if (string.IsNullOrEmpty(address))
                return false;
            var handle = Addressables.LoadResourceLocationsAsync(address, typeof(ContentUpdateMaterial));
            try
            {
                await handle.Task;
                Require(handle.Status == AsyncOperationStatus.Succeeded, "The exact Addressables location query failed.");
                Require(handle.Result.Count <= 1, "The exact address resolved to more than one Catalog location.");
                return handle.Result.Count == 1;
            }
            finally
            {
                Addressables.Release(handle);
            }
        }

        private static Material Measure(ContentUpdateMaterial material)
        {
            Require(material != null && material.Dependency != null && material.OrdinaryDependency != null,
                "The loaded material has an unresolved real Bundle dependency.");
            return new Material
            {
                found = true,
                assetSha256 = Hash(Encoding.UTF8.GetBytes(material.Value)),
                dependencySha256 = Hash(material.Dependency.bytes),
                ordinaryDependencySha256 = Hash(material.OrdinaryDependency.bytes)
            };
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteAtomic(string path, string text)
        {
            Require(Path.IsPathRooted(path), "An absolute result path is required.");
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            File.Move(temporary, path);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

#if YL_CONTENT_UPDATE_ENCRYPTION
        private sealed class Keys : IEncryptedBundleKeySource
        {
            private readonly string _k1;
            private readonly string _k2;
            private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);

            internal Keys(string k1, string k2)
            {
                _k1 = k1;
                _k2 = k2;
            }

            internal long Requests(string id) => _requests.TryGetValue(id, out var count) ? count : 0;
            internal long TotalRequests => _requests.Values.Sum();

            public EncryptedBundleKeyLease OpenReadKey(string keyId)
            {
                _requests.AddOrUpdate(keyId, 1, (_, count) => count + 1);
                var path = keyId == "K1" ? _k1 : keyId == "K2" ? _k2 : null;
                if (string.IsNullOrEmpty(path))
                    return null;
                byte[] bytes = File.ReadAllBytes(path);
                try
                {
                    return new EncryptedBundleKeyLease(bytes);
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                }
            }
        }
#endif
    }
}
