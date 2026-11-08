using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceCostBaselineTests
    {
        [UnityTest, Order(2)]
        public IEnumerator BSignedHttpsFullAndResumeBaseline()
        {
            Assert.That(State.BuildsComplete, Is.True, "A must complete before entering the owned runtime scene.");
            yield return new EnterPlayMode();
            yield return NetworkBody();
        }

        private static IEnumerator NetworkBody()
        {
            var state = State;
            Assert.That(state.BuildsComplete, Is.True, "Select the entire ordered fixture; A creates the fixed releases.");
            // The caller supplies an isolated certificate by the existing helper contract. No environment/global TLS changes here.
            using var certificate = ResourceDownloadTests.Certificate();
            using var signing = certificate.GetRSAPrivateKey();
            var current = OpenSnapshot(state, state.Bootstrap);
            foreach (var mode in _modes)
            {
                var target = Snapshot(state, mode);
                var envelope = ResourceReleaseSigner.Create(new ResourceRelease(Path.GetDirectoryName(target.DirectoryPath), target.Document),
                    "test", 1, "isolated-test", signing, DateTimeOffset.UtcNow.AddDays(1));
                var files = target.Document.Bundles.Cast<ResourceCatalogFile>().Concat(target.Document.SupportFiles).Select(x => x.RelativeFile)
                    .Concat(new[] { "resource-catalog.json", ".ylr-complete.json" }).ToDictionary(x => "/" + target.Document.CatalogDigest + "/" + x,
                        x => File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(target.DirectoryPath, x))), StringComparer.Ordinal);
                // All requests/samples serve these same immutable byte arrays; there is no per-request encryption.
                for (var sample = 0; sample <= Samples; sample++)
                {
                    yield return NetworkSample(state, mode, sample, false, current, target, envelope, files, certificate);
                }

                for (var sample = 1; sample <= Samples; sample++)
                {
                    yield return NetworkSample(state, mode, sample, true, current, target, envelope, files, certificate);
                }
            }
            state.NetworkComplete = true;
            Save(state);
        }

        private sealed class NetworkResult
        {
            public string Mode;
            public string Store;
            public string Step;
            public string Error;
            public string Digest;
            public bool Resume;
            public bool Warmup;
            public bool Completed;
            public int Sample;
            public long PlanMissingBytes;
            public long PlanReusableBytes;
            public long RemainingPayloadBytes;
            public long Offset;
            public object Partial;
            public object Interrupted;
            public object Transfer;
            public object Activation;
            public object Released;
            public object Checkpoints;
            public double CheckMilliseconds;
            public object TimingBoundary = new
            {
                Payload = "Actual accepted-and-persisted HTTP body bytes, read from finalized .part length deltas. Excludes HTTP/TLS headers and release/catalog Check metadata.",
                Download = "First payload GET observed by the server through the first main-thread observation after all expected bodies and transfer disposal. Includes serial per-file SHA/IO and frame/TLS dispatch between GETs, not pure socket time.",
                Prepare = "PrepareUpdateAsync call through terminal request, including From retention, full SHA, stage install/readback and AEAD validation. Activate is separately timed.",
                Server = "Successful SslStream body WriteAsync bytes, not TLS wire bytes or proof of client receipt. Cancellation can over-send relative to committed partial.",
                InstrumentedStages = "Checkpoint pairs separately report PartSha, ReceiptSha, ContentSha and ContentAuthentication. StagingSnapshotValidation is a structure+SHA aggregate, not pure hash CPU. These cover only their named sites, not all repeated SHA in Open/cache/commit; do not sum overlapping categories or add them to Prepare.",
                Unavailable = "Pure SHA CPU time, all-framework SHA total, isolated socket-receive time and native Player measurements are not inferred from these boundaries.",
                Cache = "Each pair uses a new private Store; resumed half reuses only its own actual canceled partials. Not an OS-cold disk measurement.",
                ServerConditions = "IPv4 loopback HTTPS, fixed strong per-file SHA ETag, sequential requests, 64 KiB body writes, 2 ms requested inter-chunk delay. Interrupted largest file stalls at 6 MiB; cancellation is requested after disk length >=4 MiB."
            };
        }

        private static IEnumerator NetworkSample(FixtureState state, string mode, int sample, bool resume, ResourceLocalSnapshot current,
            ResourceLocalSnapshot target, byte[] envelope, Dictionary<string, byte[]> files, X509Certificate2 certificate)
        {
            var label = mode.ToLowerInvariant() + "-" + (resume ? "resume" : "full") + "-" + sample;
            var result = new NetworkResult
            {
                Mode = mode,
                Resume = resume,
                Warmup = sample == 0,
                Sample = sample,
                Digest = target.Document.CatalogDigest,
                Store = Path.Combine(state.CacheRoot, (mode == "None" ? "n" : "e") + (resume ? "r" : "f") + sample),
                Step = "Create"
            };
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(result.Store)), Is.False);
            var versionPath = Path.Combine(result.Store, state.ProjectId, Platform, "versions", target.Document.CatalogDigest);
            Assert.That(versionPath.Length, Is.LessThanOrEqualTo(210));
            Assert.That(Path.Combine(versionPath,
                target.Document.Bundles.OrderByDescending(x => x.RelativeFile.Length).First().RelativeFile).Length, Is.LessThan(255));
            state.CacheStores.Add(result.Store);
            Save(state);
            var largest = target.Document.Bundles.OrderByDescending(x => x.Length).First();
            var largestPath = "/" + target.Document.CatalogDigest + "/" + largest.RelativeFile;
            var etags = files.ToDictionary(x => x.Key, x => "\"" + ResourceCatalogCodec.Digest(x.Value) + "\"", StringComparer.Ordinal);
            var ledger = new WireLedger(target.Document.CatalogDigest);
            using var server = new ResourceTlsServer(certificate);
            var interrupt = resume;
            server.Responder = request =>
            {
                var began = Stopwatch.GetTimestamp();
                ResourceTlsServer.Response response;
                if (request.Path == "/release.json")
                {
                    response = new ResourceTlsServer.Response { Body = envelope };
                }
                else if (files.TryGetValue(request.Path, out var body))
                {
                    var fileName = request.Path.Substring(request.Path.LastIndexOf('/') + 1);
                    var etag = etags[request.Path];
                    response = new ResourceTlsServer.Response { Body = body, ChunkSize = 64 * 1024, ChunkDelay = TimeSpan.FromMilliseconds(2), Headers = { ["ETag"] = etag } };
                    if (request.Headers.TryGetValue("Range", out var range))
                    {
                        if (!request.Headers.TryGetValue("If-Range", out var validator) || validator != etag || !range.StartsWith("bytes=", StringComparison.Ordinal) || !range.EndsWith("-", StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("Invalid fixture range validator.");
                        }

                        var offset = int.Parse(range.Substring(6).TrimEnd('-'), CultureInfo.InvariantCulture);
                        if (offset <= 0 || offset >= body.Length)
                        {
                            throw new InvalidDataException("Invalid fixture offset.");
                        }

                        var suffix = new byte[body.Length - offset];
                        Buffer.BlockCopy(body, offset, suffix, 0, suffix.Length);
                        response.Body = suffix;
                        response.StatusCode = 206;
                        response.Headers["Content-Range"] = "bytes " + offset + "-" + (body.Length - 1) + "/" + body.Length;
                    }
                    if (interrupt && request.Path == largestPath)
                    {
                        response.StallAfterBytes = 6 * 1024 * 1024;
                    }

                    if (fileName == "resource-catalog.json")
                    {
                        response.ChunkDelay = TimeSpan.Zero;
                    }
                }
                else
                {
                    response = new ResourceTlsServer.Response { StatusCode = 404 };
                }

                ledger.Observe(request, response, began);
                return response;
            };
            var remote = ResourceDownloadTests.Source(server, certificate, attempts: 1, timeout: 120);
            var store = new ArtifactStore(result.Store, new[] { new ResourceProjectMount(state.ProjectId, Platform, AppId, current, remote: remote) });
            var checkpoints = new CheckpointLedger();
            var previousCheckpoint = store.Checkpoint;
            store.Checkpoint = checkpoints.Record;
            using var keys = new FixtureKeys(state.ProjectId);
            _activeStartup = new ResourceStartup(new ResourceConfiguration(store, decryption: new ResourceDecryptionOptions(keys)));
            try
            {
                result.Step = "Start";
                checkpoints.Operation = "Start";
                using (var start = _activeStartup.StartAsync())
                {
                    yield return Wait(start);
                    start.TakeResult();
                }
                var system = _lastSystem = _activeStartup.Resources;
                result.Step = "Check";
                checkpoints.Operation = "Check";
                var checkStarted = Stopwatch.GetTimestamp();
                UpdatePlan plan;
                using (var check = system.CheckRemoteUpdatesAsync())
                {
                    yield return Wait(check);
                    result.CheckMilliseconds = Milliseconds(checkStarted, Stopwatch.GetTimestamp());
                    result.Error = check.Error?.ToString();
                    plan = check.TakeResult();
                }
                result.PlanMissingBytes = plan.MissingBytes;
                result.PlanReusableBytes = plan.ReusableBytes;
                Assert.That(plan.Projects.Single().ToDigest, Is.EqualTo(target.Document.CatalogDigest));
                Assert.That(plan.Projects.Single().Files.Single(x => x.RelativeFile == largest.RelativeFile).IsReusable, Is.False);
                var expected = plan.Projects.Single().Files.Where(x => !x.IsReusable).ToDictionary(x => x.RelativeFile, x => x.Length, StringComparer.Ordinal);
                expected.Add(".ylr-complete.json", files["/" + target.Document.CatalogDigest + "/.ylr-complete.json"].LongLength);
                var partPaths = expected.Keys.ToDictionary(x => x, x => PartPath(store, remote, target, x, files["/" + target.Document.CatalogDigest + "/" + x]), StringComparer.Ordinal);
                var before = expected.Keys.ToDictionary(x => x, _ => 0L, StringComparer.Ordinal);
                if (resume)
                {
                    result.Step = "Interrupt";
                    checkpoints.Operation = "Interrupt";
                    using var window = new TransferWindow(ledger, expected.Values.Sum());
                    using (var interrupted = system.PrepareUpdateAsync(plan))
                    {
                        var deadline = Now + TimeoutSeconds;
                        var selectedPart = partPaths[largest.RelativeFile];
                        while (!interrupted.IsDone && (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(selectedPart)) || new FileInfo(selectedPart).Length < 4 * MiB) && Now < deadline)
                        {
                            window.Tick(system);
                            yield return null;
                        }
                        interrupted.Cancel();
                        while ((system.GetDiagnosticsReport().Updates.Count != 0 || system.GetContentDiagnosticsSnapshot().WorkCount != 0) && Now < deadline)
                        {
                            window.Tick(system);
                            yield return null;
                        }
                        window.Finish();
                        before = PartLengths(partPaths);
                        result.Offset = before[largest.RelativeFile];
                        result.Interrupted = window.Capture(before.Values.Sum(), interrupted.Status, interrupted.Error?.ToString());
                        Assert.That(interrupted.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                        Assert.That(system.GetDiagnosticsReport().Updates, Is.Empty, "The canceled producer must close its file and IO before reading the offset.");
                        Assert.That(system.GetContentDiagnosticsSnapshot().WorkCount, Is.Zero);
                    }
                    Assert.That(result.Offset, Is.GreaterThanOrEqualTo(4 * MiB).And.LessThan(largest.Length));
                    var metadata = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.ChangeExtension(partPaths[largest.RelativeFile], ".json"))));
                    var prefix = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(partPaths[largest.RelativeFile]));
                    result.Partial = new
                    {
                        File = largest.RelativeFile,
                        result.Offset,
                        Sha256 = Hash(partPaths[largest.RelativeFile]),
                        EntitySha256 = (string)metadata["Sha256"],
                        Length = (long)metadata["Length"],
                        ETag = (string)metadata["ETag"]
                    };
                    Assert.That((string)metadata["ETag"], Is.EqualTo("\"" + largest.Sha256 + "\""));
                    Assert.That((string)metadata["Sha256"], Is.EqualTo(largest.Sha256));
                    Assert.That((long)metadata["Length"], Is.EqualTo(largest.Length));
                    CollectionAssert.AreEqual(files[largestPath].Take(prefix.Length), prefix);
                    Assert.That(store.GetPreparedUpdates(), Is.Empty);
                    interrupt = false;
                }

                result.Step = resume ? "Resume" : "FullPrepare";
                checkpoints.Operation = result.Step;
                result.RemainingPayloadBytes = expected.Sum(x => x.Value - before[x.Key]);
                using var transfer = new TransferWindow(ledger, result.RemainingPayloadBytes);
                PreparedUpdate prepared;
                using (var preparing = system.PrepareUpdateAsync(plan))
                {
                    var deadline = Now + TimeoutSeconds;
                    while (!preparing.IsDone && Now < deadline)
                    {
                        transfer.Tick(system);
                        yield return null;
                    }
                    transfer.Tick(system);
                    transfer.Finish();
                    var after = PartLengths(partPaths);
                    var accepted = after.Sum(x => x.Value - before[x.Key]);
                    result.Transfer = transfer.Capture(accepted, preparing.Status, preparing.Error?.ToString());
                    result.Error = preparing.Error?.ToString();
                    Assert.That(preparing.IsDone, Is.True, "A finite prepare observation window expired; no automatic retry.");
                    prepared = preparing.TakeResult();
                    Assert.That(accepted, Is.EqualTo(result.RemainingPayloadBytes));
                    foreach (var file in expected)
                    {
                        Assert.That(after[file.Key], Is.EqualTo(file.Value), file.Key);
                    }
                }
                var rows = ledger.Capture(transfer.FirstRequest);
                Assert.That(rows.All(x => x.WrittenBodyBytes == x.DeclaredBodyBytes), Is.True);
                Assert.That(rows.Sum(x => x.WrittenBodyBytes), Is.EqualTo(result.RemainingPayloadBytes), "No hidden retry or installed-local reuse is counted as download.");
                if (resume)
                {
                    var range = rows.Single(x => x.Path == largestPath);
                    Assert.That(range.Range, Is.EqualTo("bytes=" + result.Offset + "-"));
                    Assert.That(range.IfRange, Is.EqualTo("\"" + largest.Sha256 + "\""));
                    Assert.That(range.Status, Is.EqualTo(206));
                }
                var installed = prepared.Projects.Single().Target;
                foreach (var file in target.Document.Bundles.Cast<ResourceCatalogFile>().Concat(target.Document.SupportFiles))
                {
                    Assert.That(Hash(Path.Combine(installed.DirectoryPath, file.RelativeFile)), Is.EqualTo(file.Sha256), file.RelativeFile);
                }

                result.Step = "ActivateAndConsume";
                checkpoints.Operation = "Activate";
                var activationStarted = Stopwatch.GetTimestamp();
                using (var activate = system.ActivatePreparedUpdateAsync(prepared))
                {
                    yield return Wait(activate);
                    result.Activation = new
                    {
                        Milliseconds = Milliseconds(activationStarted, Stopwatch.GetTimestamp()),
                        Status = activate.Status.ToString(),
                        Error = activate.Error?.ToString()
                    };
                    Assert.That(activate.TakeResult(), Is.True);
                }
                using (var request = system.AcquireAsync<TextAsset>(new ResourceAddress(state.ProjectId, "entry")))
                {
                    yield return Wait(request);
                    using var text = request.TakeResult();
                    Assert.That(text.Asset.text, Is.EqualTo(mode == "None" ? state.NoneText : state.EncryptedText));
                }
                result.Step = "Stop";
                checkpoints.Operation = "Stop";
                using (var stop = _activeStartup.StopAsync())
                {
                    yield return Wait(stop);
                    Assert.That(stop.TakeResult(), Is.True);
                }
                result.Released = system.GetContentDiagnosticsSnapshot();
                AssertReleased(system);
                _activeStartup = null;
                result.Completed = true;
                result.Step = "Complete";
            }
            finally
            {
                store.Checkpoint = previousCheckpoint;
                server.Dispose();
                result.Checkpoints = checkpoints.Capture();
                ResourceCostEvidence.Write("f03-b-" + label, new
                {
                    Result = result,
                    Requests = ledger.Capture(0),
                    ServerErrors = server.Errors.Select(x => x.ToString()).ToArray()
                });
            }
            Assert.That(server.Errors, Is.Empty);
        }

        private static string PartPath(ArtifactStore store, ResourceRemoteSource source, ResourceLocalSnapshot target, string file, byte[] bytes)
        {
            var uri = source.Resolve(target.Document.CatalogDigest + "/" + file).AbsoluteUri;
            var token = ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(uri + ":" + ResourceCatalogCodec.Digest(bytes))).Substring(0, 32);
            return Path.Combine(store.RootPath, target.Document.ProjectId, target.Document.Platform, "parts", token + ".part");
        }

        private static Dictionary<string, long> PartLengths(Dictionary<string, string> paths) => paths.ToDictionary(x => x.Key,
            x => File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(x.Value)) ? new FileInfo(x.Value).Length : 0L, StringComparer.Ordinal);

        private readonly struct CheckpointEvent
        {
            public readonly string Operation;
            public readonly string Phase;
            public readonly long Tick;
            public readonly int Thread;
            internal CheckpointEvent(string operation, string phase)
            {
                Operation = operation;
                Phase = phase;
                Tick = Stopwatch.GetTimestamp();
                Thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            }
        }

        private sealed class CheckpointLedger
        {
            private readonly object _sync = new();
            private readonly List<CheckpointEvent> _events = new(1024);
            private string _operation = "Create";
            private bool _isTruncated;
            internal string Operation
            {
                set
                {
                    lock (_sync)
                    {
                        _operation = value;
                    }
                }
            }
            internal void Record(string phase)
            {
                lock (_sync)
                {
                    if (_events.Count < 16384)
                    {
                        _events.Add(new CheckpointEvent(_operation, phase));
                    }
                    else
                    {
                        _isTruncated = true;
                    }
                }
            }
            internal object Capture()
            {
                CheckpointEvent[] rows;
                lock (_sync)
                {
                    rows = _events.ToArray();
                }

                var pending = new Dictionary<(string Operation, string Category, int Thread), Stack<long>>();
                var completed = new List<(string Operation, string Category, double Milliseconds)>();
                var unmatchedFinishes = new List<CheckpointEvent>();
                foreach (var row in rows)
                {
                    var start = row.Phase.EndsWith("Started", StringComparison.Ordinal);
                    var finish = row.Phase.EndsWith("Finished", StringComparison.Ordinal);
                    if (!start && !finish)
                    {
                        continue;
                    }

                    var category = row.Phase.Substring(0, row.Phase.Length - (start ? "Started".Length : "Finished".Length));
                    var key = (row.Operation, category, row.Thread);
                    if (start)
                    {
                        if (!pending.TryGetValue(key, out var starts))
                        {
                            pending.Add(key, starts = new Stack<long>());
                        }

                        starts.Push(row.Tick);
                    }
                    else if (pending.TryGetValue(key, out var pendingStarts) && pendingStarts.Count != 0)
                    {
                        completed.Add((row.Operation, category, Milliseconds(pendingStarts.Pop(), row.Tick)));
                    }
                    else
                    {
                        unmatchedFinishes.Add(row);
                    }
                }
                return new
                {
                    Events = rows,
                    Truncated = _isTruncated,
                    Stages = completed.GroupBy(x => (x.Operation, x.Category)).Select(group => new
                    {
                        group.Key.Operation,
                        group.Key.Category,
                        Count = group.Count(),
                        Milliseconds = group.Sum(x => x.Milliseconds)
                    }).ToArray(),
                    UnmatchedStarts = pending.Where(x => x.Value.Count != 0).Select(x => new { x.Key.Operation, x.Key.Category, x.Key.Thread, Count = x.Value.Count }).ToArray(),
                    UnmatchedFinishes = unmatchedFinishes,
                    Boundary = "Opt-in original Store checkpoints, paired by operation/category/executing thread. Elapsed wall durations at named sites, not pure SHA CPU, whole-system coverage or disjoint accounting. Never add this table to Prepare or sum overlapping categories."
                };
            }
        }

        private sealed class WireRow
        {
            public string Path;
            public string Range;
            public string IfRange;
            public int Status;
            public bool Payload;
            public long RequestedTick;
            public long LastWrittenTick;
            public long DeclaredBodyBytes;
            public long WrittenBodyBytes;
            internal WireRow Copy() => (WireRow)MemberwiseClone();
        }

        private sealed class WireLedger
        {
            private readonly string _prefix;
            private readonly object _sync = new();
            private readonly List<WireRow> _rows = new();
            internal WireLedger(string digest) => _prefix = "/" + digest + "/";
            internal int Count
            {
                get
                {
                    lock (_sync)
                    {
                        return _rows.Count;
                    }
                }
            }
            internal void Observe(ResourceTlsServer.Request request, ResourceTlsServer.Response response, long began)
            {
                request.Headers.TryGetValue("Range", out var range);
                request.Headers.TryGetValue("If-Range", out var validator);
                var row = new WireRow
                {
                    Path = request.Path,
                    Range = range,
                    IfRange = validator,
                    Status = response.StatusCode,
                    RequestedTick = began,
                    DeclaredBodyBytes = response.Body.LongLength,
                    Payload = request.Path.StartsWith(_prefix, StringComparison.Ordinal) && !request.Path.EndsWith("/resource-catalog.json", StringComparison.Ordinal)
                };
                lock (_sync)
                {
                    _rows.Add(row);
                }

                response.BodyBytesWritten = count =>
                {
                    lock (_sync)
                    {
                        row.WrittenBodyBytes += count;
                        row.LastWrittenTick = Stopwatch.GetTimestamp();
                    }
                };
            }
            internal WireRow[] Capture(int start)
            {
                lock (_sync)
                {
                    return _rows.Skip(start).Select(x => x.Copy()).ToArray();
                }
            }
        }

        private sealed class TransferWindow : IDisposable
        {
            private readonly WireLedger _ledger;
            private readonly long _expected;
            private readonly long _started = Stopwatch.GetTimestamp();
            private readonly List<object> _frames = new(1024);
            private readonly MemorySampler _memory = new();
            private long _completed;
            private long _downloadCompleted;
            private bool _isTruncated;
            internal readonly int FirstRequest;
            internal TransferWindow(WireLedger ledger, long expected)
            {
                this._ledger = ledger;
                this._expected = expected;
                FirstRequest = ledger.Count;
            }
            internal void Tick(ResourceSystem system)
            {
                _memory.Tick(system);
                var tick = Stopwatch.GetTimestamp();
                var updates = system.GetDiagnosticsReport().Updates;
                var downloading = updates.FirstOrDefault(x => x.Phase == "Downloading");
                if (_frames.Count < 16384)
                {
                    _frames.Add(new
                    {
                        Tick = tick,
                        Phase = downloading?.Phase ?? updates.FirstOrDefault()?.Phase,
                        downloading?.TransferredBytes,
                        downloading?.ExpectedBytes,
                        downloading?.RetryCount
                    });
                }
                else
                {
                    _isTruncated = true;
                }
                // This is an observed client-side upper boundary, not a fabricated isolated network stopwatch.
                if (_downloadCompleted == 0 && downloading == null)
                {
                    var requests = _ledger.Capture(FirstRequest).Where(x => x.Payload).ToArray();
                    if (requests.Length != 0 && requests.Sum(x => x.WrittenBodyBytes) == _expected)
                    {
                        _downloadCompleted = tick;
                    }
                }
            }
            internal void Finish() => _completed = Stopwatch.GetTimestamp();
            internal object Capture(long accepted, ResourceRequestStatus status, string error)
            {
                var requests = _ledger.Capture(FirstRequest).Where(x => x.Payload).ToArray();
                var first = requests.Length == 0 ? 0 : requests.Min(x => x.RequestedTick);
                var last = requests.Length == 0 ? 0 : requests.Max(x => x.LastWrittenTick);
                var written = requests.Sum(x => x.WrittenBodyBytes);
                var downloadMs = first != 0 && _downloadCompleted > first ? Milliseconds(first, _downloadCompleted) : (double?)null;
                var serverMs = first != 0 && last > first ? Milliseconds(first, last) : (double?)null;
                return new
                {
                    Status = status.ToString(),
                    Error = error,
                    ExpectedNewPayloadBytes = _expected,
                    AcceptedNewPayloadBytes = accepted,
                    ServerBodyBytesWritten = written,
                    RequestCount = requests.Length,
                    PrepareMilliseconds = Milliseconds(_started, _completed),
                    DownloadCompletedObservedTick = _downloadCompleted == 0 ? (long?)null : _downloadCompleted,
                    DownloadPipelineMilliseconds = downloadMs,
                    ServerGetToLastBodyWriteMilliseconds = serverMs,
                    DownloadPipelineMiBPerSecond = downloadMs.HasValue && downloadMs.Value > 0 ? accepted / (double)MiB / (downloadMs.Value / 1000) : (double?)null,
                    ServerBodyMiBPerSecond = serverMs.HasValue && serverMs.Value > 0 ? written / (double)MiB / (serverMs.Value / 1000) : (double?)null,
                    DiagnosticFrames = _frames,
                    FramesTruncated = _isTruncated,
                    Memory = _memory.Capture()
                };
            }
            public void Dispose() => _memory.Dispose();
        }
    }
}
