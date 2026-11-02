using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceCostBaselineTests
    {
        [UnityTest, Order(3)]
        public IEnumerator CEditorSharedConsumerAndPeakBaseline()
        {
            Assert.That(State.BuildsComplete, Is.True, "A must complete before entering the owned runtime scene.");
            yield return new EnterPlayMode();
            yield return ConsumerBody();
        }

        private static IEnumerator ConsumerBody()
        {
            var state = State;
            Assert.That(state.BuildsComplete, Is.True, "Run A before C by selecting the entire ordered fixture.");
            var counter = new AllocationCounter();
            ResourceCostEvidence.Write("f03-c-allocation-capability", new
            {
                counter.Available,
                counter.ProbeBefore,
                counter.ProbeAfter,
                RequiredKnownAllocation = 8192,
                Unavailable = counter.Available ? null : "Missing/unsupported/constant per-thread counter; all allocation deltas are null, never zero-allocation claims."
            });
            foreach (var mode in _modes)
            {
                var snapshot = Snapshot(state, mode);
                for (var sample = 0; sample <= Samples; sample++)
                {
                    yield return ConsumerSample(state, mode, sample, snapshot, counter);
                }
            }
        }

        private sealed class ConsumerResult
        {
            public string Mode;
            public string Digest;
            public string Step;
            public string Error;
            public int Sample;
            public int SharedConsumers = 12;
            public int HotOperations = 32 * 16;
            public int ExpectedBundleClosure;
            public bool Warmup;
            public bool Completed;
            public bool HotIdentityMatched = true;
            public bool AudioSourcePlayingObserved;
            public double? StartupMilliseconds;
            public double? ColdAcquireMilliseconds;
            public double? InstanceAndAudioStartMilliseconds;
            public double? HeldHotMilliseconds;
            public double? ReleaseMilliseconds;
            public double? StopMilliseconds;
            public long? ColdMainThreadAllocatedBytes;
            public long? HotMainThreadAllocatedBytes;
            public object Held;
            public object Released;
            public object Stopped;
            public object Memory;
            public string[] CleanupFailures;
            public string[] RequestFailures;
            public string Boundary = "Windows Editor baseline only. A fixed exported snapshot is strictly opened before timing. New ResourceStartup/native session each sample, not OS-cold disk. ColdAcquire covers 12 shared Prefab async consumers + two Texture consumers + two formal Audio-provider consumers, full formal SHA/input/native/materialization and request handoff. Hot is 32 passes of 16 acquire/dispose operations while original leases remain held; no IO/native rebuild is expected. AudioSource playback is muted and does not establish an underrun/glitch guarantee. Memory is main-thread tick sampling including the observer/test workload, not absolute instantaneous peaks or allocation attribution. Original G3 Windows64 Mono Player 5-sample x3 evidence is not rerun/replaced.";
        }

        private static IEnumerator ConsumerSample(FixtureState state, string mode, int sample, ResourceLocalSnapshot snapshot, AllocationCounter counter)
        {
            var result = new ConsumerResult { Mode = mode, Digest = snapshot.Document.CatalogDigest, Sample = sample, Warmup = sample == 0, Step = "Start" };
            var prefabRequests = new ResourceRequest<AssetLease<GameObject>>[12];
            var prefabLeases = new AssetLease<GameObject>[12];
            var textures = new AssetLease<Texture2D>[2];
            var textureRequests = new ResourceRequest<AssetLease<Texture2D>>[2];
            var audio = new AudioClipLease[2];
            var audioErrors = new Exception[2];
            var instances = new List<InstanceLease>();
            GameObject audioObject = null;
            AudioSource source = null;
            ResourceSystem system = null;
            var audioDone = 0;
            var closing = false;
            var released = false;
            var cleanupFailures = new List<Exception>();
            using var keys = new FixtureKeys(state.ProjectId);
            using var memory = new MemorySampler();
            var config = ResourceConfiguration.Create(ResourceLoadMode.Bundles,
                new[] { new ResourceProjectMount(state.ProjectId, Platform, AppId, snapshot) }, decryption: new ResourceDecryptionOptions(keys));
            _activeStartup = new ResourceStartup(config, audioProjects: new[] { state.ProjectId });
            var addresses = new[] { "A", "B", "C" }.Select(x => new ResourceAddress(state.ProjectId, x)).ToArray();
            var textureAddresses = new[] { new ResourceAddress(state.ProjectId, "large"), new ResourceAddress(state.ProjectId, "medium") };

            void Release(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception failure)
                {
                    cleanupFailures.Add(failure);
                }
            }
            void ReleaseConsumers()
            {
                if (released)
                {
                    return;
                }

                released = true;
                closing = true;
                if (source)
                {
                    Release(() => source.Stop());
                    Release(() => source.clip = null);
                }
                if (audioObject)
                {
                    Release(() => Object.Destroy(audioObject));
                }

                foreach (var instance in instances)
                {
                    Release(instance.Dispose);
                }

                if (system != null)
                {
                    Release(system.ClearInstanceCaches);
                }

                foreach (var lease in prefabLeases)
                {
                    if (lease != null)
                    {
                        Release(lease.Dispose);
                    }
                }

                foreach (var lease in textures)
                {
                    if (lease != null)
                    {
                        Release(lease.Dispose);
                    }
                }

                foreach (var lease in audio)
                {
                    if (lease != null)
                    {
                        Release(lease.Dispose);
                    }
                }

                foreach (var request in prefabRequests)
                {
                    if (request != null)
                    {
                        Release(request.Dispose);
                    }
                }

                foreach (var request in textureRequests)
                {
                    if (request != null)
                    {
                        Release(request.Dispose);
                    }
                }
            }

            try
            {
                var began = Stopwatch.GetTimestamp();
                using (var start = _activeStartup.StartAsync())
                {
                    var deadline = Now + TimeoutSeconds;
                    while (!start.IsDone && Now < deadline)
                    {
                        memory.Tick(_activeStartup.Resources);
                        yield return null;
                    }
                    result.StartupMilliseconds = Milliseconds(began, Stopwatch.GetTimestamp());
                    result.Error = start.Error?.ToString();
                    Assert.That(start.IsDone, Is.True);
                    start.TakeResult();
                }
                system = _lastSystem = _activeStartup.Resources;
                var provider = _activeStartup.AudioProviders[state.ProjectId];
                result.Step = "ColdAcquire";
                var allocated = counter.Read();
                began = Stopwatch.GetTimestamp();
                try
                {
                    for (var i = 0; i < prefabRequests.Length; i++)
                    {
                        prefabRequests[i] = system.AcquireAsync<GameObject>(addresses[i / 4]);
                    }

                    for (var i = 0; i < textureRequests.Length; i++)
                    {
                        textureRequests[i] = system.AcquireAsync<Texture2D>(textureAddresses[i]);
                    }

                    for (var i = 0; i < audio.Length; i++)
                    {
                        var index = i;
                        provider.LoadAsync("audio", (lease, error) =>
                        {
                            if (closing)
                            {
                                lease?.Dispose();
                            }
                            else
                            {
                                audio[index] = lease;
                                audioErrors[index] = error;
                            }
                            audioDone++;
                        }, null);
                    }
                    var deadline = Now + TimeoutSeconds;
                    while ((prefabRequests.Any(x => !x.IsDone) || textureRequests.Any(x => !x.IsDone) || audioDone != audio.Length) && Now < deadline)
                    {
                        memory.Tick(system);
                        yield return null;
                    }
                    for (var i = 0; i < prefabLeases.Length; i++)
                    {
                        prefabLeases[i] = prefabRequests[i].TakeResult();
                    }

                    for (var i = 0; i < textures.Length; i++)
                    {
                        textures[i] = textureRequests[i].TakeResult();
                    }
                }
                finally
                {
                    result.ColdAcquireMilliseconds = Milliseconds(began, Stopwatch.GetTimestamp());
                    result.ColdMainThreadAllocatedBytes = counter.Delta(allocated);
                }
                memory.Tick(system);
                // Correctness is checked after the timing/alloc window; no diagnostic formatting in that window.
                Assert.That(audioDone, Is.EqualTo(2));
                Assert.That(audioErrors, Is.All.Null);
                Assert.That(audio, Is.All.Not.Null);
                for (var i = 0; i < prefabLeases.Length; i++)
                {
                    Assert.That(prefabLeases[i].Asset, Is.SameAs(prefabLeases[i / 4 * 4].Asset));
                    Assert.That(prefabLeases[i].Asset.GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.SameAs(textures[0].Asset));
                }
                Assert.That(textures[0].Asset.width, Is.EqualTo(2048));
                Assert.That(textures[1].Asset.width, Is.EqualTo(1024));
                Assert.That(audio[0].Clip, Is.SameAs(audio[1].Clip));
                Assert.That(audio[0].Clip.channels, Is.EqualTo(2));
                Assert.That(audio[0].Clip.samples, Is.EqualTo(44100 * 20));
                result.ExpectedBundleClosure = ConsumerClosure(snapshot.Document);
                var beforeHotNative = system.GetDiagnosticsSnapshot().NativeBundleLoadCount;
                var hotLive = system.GetContentDiagnosticsSnapshot().LiveInputs;

                result.Step = "InstancesAndAudio";
                began = Stopwatch.GetTimestamp();
                for (var i = 0; i < 3; i++)
                {
                    instances.Add(system.Instantiate(prefabLeases[i * 4]));
                }

                audioObject = new GameObject("F03-OwnedAudio");
                source = audioObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.mute = true;
                source.loop = true;
                source.clip = audio[0].Clip;
                source.Play();
                result.InstanceAndAudioStartMilliseconds = Milliseconds(began, Stopwatch.GetTimestamp());
                result.Step = "HeldHot";
                allocated = counter.Read();
                began = Stopwatch.GetTimestamp();
                for (var pass = 0; pass < 32; pass++)
                {
                    for (var i = 0; i < 12; i++)
                    {
                        using var hot = system.Acquire<GameObject>(addresses[i / 4]);
                        result.HotIdentityMatched &= ReferenceEquals(hot.Asset, prefabLeases[i].Asset);
                    }
                    for (var i = 0; i < 2; i++)
                    {
                        using var hot = system.Acquire<Texture2D>(textureAddresses[i]);
                        result.HotIdentityMatched &= ReferenceEquals(hot.Asset, textures[i].Asset);
                        using var clip = provider.Load("audio");
                        result.HotIdentityMatched &= ReferenceEquals(clip.Clip, audio[i].Clip);
                    }
                }
                result.HeldHotMilliseconds = Milliseconds(began, Stopwatch.GetTimestamp());
                result.HotMainThreadAllocatedBytes = counter.Delta(allocated);
                // Keep all base leases, instances and clip consumers alive through the sampled residency window.
                for (var frame = 0; frame < 8; frame++)
                {
                    memory.Tick(system);
                    yield return null;
                }
                result.AudioSourcePlayingObserved = source.isPlaying;
                var held = system.GetDiagnosticsReport();
                result.Held = new
                {
                    held.Summary,
                    held.Content,
                    held.Bundles,
                    held.InstanceCaches,
                    TextureNativeBytes = Profiler.GetRuntimeMemorySizeLong(textures[0].Asset) + Profiler.GetRuntimeMemorySizeLong(textures[1].Asset),
                    AudioNativeBytes = Profiler.GetRuntimeMemorySizeLong(audio[0].Clip)
                };
                Assert.That(result.HotIdentityMatched, Is.True);
                Assert.That(held.Summary.NativeBundleLoadCount, Is.EqualTo(beforeHotNative));
                Assert.That(held.Content.LiveInputs, Is.EqualTo(hotLive));
                Assert.That(held.Bundles.Count, Is.EqualTo(result.ExpectedBundleClosure));
                Assert.That(held.Bundles.Any(x => x.DependencyPins >= 2), Is.True, "The actual shared dependency must remain resident, not duplicate per consumer.");
                Assert.That(held.Content.LiveInputs, Is.EqualTo(mode == "Encrypted" ? result.ExpectedBundleClosure : 0));

                result.Step = "ReleaseConsumers";
                began = Stopwatch.GetTimestamp();
                ReleaseConsumers();
                var releaseDeadline = Now + TimeoutSeconds;
                while (!IsReleased(system) && Now < releaseDeadline)
                {
                    memory.Tick(system);
                    yield return null;
                }
                memory.Tick(system);
                result.ReleaseMilliseconds = Milliseconds(began, Stopwatch.GetTimestamp());
                result.Released = new
                {
                    Content = system.GetContentDiagnosticsSnapshot(),
                    Summary = system.GetDiagnosticsSnapshot()
                };
                Assert.That(cleanupFailures, Is.Empty);
                AssertReleased(system);
                result.Step = "Stop";
                began = Stopwatch.GetTimestamp();
                using (var stop = _activeStartup.StopAsync())
                {
                    yield return Wait(stop);
                    result.StopMilliseconds = Milliseconds(began, Stopwatch.GetTimestamp());
                    Assert.That(stop.TakeResult(), Is.True, stop.Error?.ToString());
                }
                result.Stopped = system.GetContentDiagnosticsSnapshot();
                AssertReleased(system);
                Assert.That(ResourceSystem.Current, Is.Null);
                _activeStartup = null;
                result.Step = "Complete";
                result.Completed = true;
            }
            finally
            {
                ReleaseConsumers();
                result.Memory = memory.Capture();
                result.CleanupFailures = cleanupFailures.Select(x => x.ToString()).ToArray();
                result.RequestFailures = prefabRequests.Where(x => x?.Error != null).Select(x => x.Error.ToString())
                    .Concat(textureRequests.Where(x => x?.Error != null).Select(x => x.Error.ToString()))
                    .Concat(audioErrors.Where(x => x != null).Select(x => x.ToString())).ToArray();
                ResourceCostEvidence.Write("f03-c-" + mode.ToLowerInvariant() + "-" + sample, result);
                if (cleanupFailures.Count != 0)
                {
                    throw new AggregateException("F03 consumer release failed.", cleanupFailures);
                }
            }
        }

        private static int ConsumerClosure(ResourceCatalogDocument document)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id)
            {
                if (found.Add(id))
                {
                    foreach (var next in document.Bundles.Single(x => x.LogicalId == id).Dependencies)
                    {
                        Visit(next);
                    }
                }
            }
            foreach (var address in new[] { "A", "B", "C", "large", "medium", "audio" })
            {
                Visit(document.Assets.Single(x => x.Address == address && x.Kind != "SubAsset").BundleId);
            }

            return found.Count;
        }

        private static IEnumerator Wait<T>(ResourceRequest<T> request)
        {
            var deadline = Now + TimeoutSeconds;
            while (!request.IsDone && Now < deadline)
            {
                yield return null;
            }

            if (request.IsDone)
            {
                yield break;
            }

            request.Cancel();
            throw new TimeoutException("The owned F03 request exceeded its finite observation window.");
        }

        private static bool IsReleased(ResourceSystem system)
        {
            var content = system.GetContentDiagnosticsSnapshot();
            var summary = system.GetDiagnosticsSnapshot();
            return content.AllocatedBufferBytes == 0 && content.ReservedBufferBytes == 0 && content.LiveInputs == 0 && content.WorkCount == 0 &&
                content.RetainedCleanupOwners == 0 && summary.RecordCount == 0 && summary.BundleRecordCount == 0 && summary.LeaseCount == 0 &&
                summary.ProducerCount == 0 && summary.WaiterCount == 0 && summary.NativeBundleLoadCount == summary.NativeBundleUnloadCount;
        }

        private static void AssertReleased(ResourceSystem system)
        {
            Assert.That(IsReleased(system), Is.True, "The original authority still has resource/input/work/native responsibility.");
            var content = system.GetContentDiagnosticsSnapshot();
            Assert.That(content.DeclaredNativeReadBufferBytes, Is.Zero);
            Assert.That(content.ProcessCngProviderCount, Is.Zero);
            Assert.That(content.ProcessCngKeyCount, Is.Zero);
            Assert.That(content.ProcessCngKeyObjectBytes, Is.Zero);
            Assert.That(content.ProcessCngReleaseFailures, Is.Zero);
            Assert.That(system.GetDiagnosticsReport().InstanceCaches, Is.Empty);
        }

        [UnityTearDown]
        public IEnumerator EndOnlyTheOwnedRuntimeSession()
        {
            if (!Application.isPlaying)
            {
                yield break;
            }

            if (_activeStartup != null)
            {
                _lastSystem = _activeStartup.Resources ?? _lastSystem;
                using var stop = _activeStartup.StopAsync();
                yield return Wait(stop);
                var sequence = SessionState.GetInt(StateKey + ".cleanup", 0) + 1;
                SessionState.SetInt(StateKey + ".cleanup", sequence);
                ResourceCostEvidence.Write("f03-runtime-cleanup-" + sequence, new
                {
                    Status = stop.Status.ToString(),
                    Error = stop.Error?.ToString(),
                    Content = _lastSystem?.GetContentDiagnosticsSnapshot(),
                    Summary = _lastSystem?.GetDiagnosticsSnapshot()
                });
                Assert.That(stop.TakeResult(), Is.True, stop.Error?.ToString());
                _activeStartup = null;
            }
            if (_lastSystem != null)
            {
                AssertReleased(_lastSystem);
            }

            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty, "Never use UnloadAllAssetBundles to manufacture cleanup.");
            _lastSystem = null;
            yield return new ExitPlayMode();
        }

        private sealed class AllocationCounter
        {
            private readonly Func<long> _read;
            internal bool Available
            {
                get;
            }
            internal long? ProbeBefore
            {
                get;
            }
            internal long? ProbeAfter
            {
                get;
            }
            internal AllocationCounter()
            {
                var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    return;
                }

                _read = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
                try
                {
                    ProbeBefore = _read();
                    var known = new byte[8192];
                    known[0] = 1;
                    ProbeAfter = _read();
                    GC.KeepAlive(known);
                    Available = ProbeBefore.HasValue && ProbeAfter.HasValue && ProbeAfter.Value - ProbeBefore.Value >= 8192;
                }
                catch (PlatformNotSupportedException) { }
                catch (NotSupportedException) { }
            }
            internal long? Read() => Available ? _read() : (long?)null;
            internal long? Delta(long? before) => before.HasValue ? _read() - before.Value : (long?)null;
        }

        private readonly struct MemoryPoint
        {
            public readonly long Tick;
            public readonly long MonoUsedBytes;
            public readonly long MonoHeapBytes;
            public readonly long UnityAllocatedBytes;
            public readonly long UnityReservedBytes;
            public readonly long UnityUnusedReservedBytes;
            public readonly long? WorkingSetBytes;
            public readonly long? PrivateBytes;
            public readonly ResourceContentDiagnosticsSnapshot Content;
            internal MemoryPoint(ResourceSystem system, Process process, bool processAvailable)
            {
                Tick = Stopwatch.GetTimestamp();
                MonoUsedBytes = Profiler.GetMonoUsedSizeLong();
                MonoHeapBytes = Profiler.GetMonoHeapSizeLong();
                UnityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
                UnityReservedBytes = Profiler.GetTotalReservedMemoryLong();
                UnityUnusedReservedBytes = Profiler.GetTotalUnusedReservedMemoryLong();
                var workingSet = processAvailable ? process.WorkingSet64 : (long?)null;
                var privateBytes = processAvailable ? process.PrivateMemorySize64 : (long?)null;
                // 保留原调用频次，仅规范结果；Mono成功返回0不等于进程零占用。
                WorkingSetBytes = workingSet.HasValue && workingSet.Value > 0 ? workingSet : null;
                PrivateBytes = privateBytes.HasValue && privateBytes.Value > 0 ? privateBytes : null;
                Content = system == null ? default : system.GetContentDiagnosticsSnapshot();
            }
        }

        private sealed class MemorySampler : IDisposable
        {
            private readonly Process _process = Process.GetCurrentProcess();
            private readonly List<MemoryPoint> _points = new(4096);
            private bool _isProcessAvailable = true;
            private bool _isTruncated;
            private string _unavailable;
            internal void Tick(ResourceSystem system)
            {
                if (_points.Count >= 16384)
                {
                    _isTruncated = true;
                    return;
                }
                if (_isProcessAvailable)
                {
                    try
                    {
                        _process.Refresh();
                        _ = _process.WorkingSet64;
                        _ = _process.PrivateMemorySize64;
                    }
                    catch (Win32Exception error)
                    {
                        _isProcessAvailable = false;
                        _unavailable = error.GetType().Name;
                    }
                    catch (PlatformNotSupportedException error)
                    {
                        _isProcessAvailable = false;
                        _unavailable = error.GetType().Name;
                    }
                }
                _points.Add(new MemoryPoint(system, _process, _isProcessAvailable));
            }
            internal object Capture()
            {
                var workingSetAvailable = _isProcessAvailable && _points.Count > 0 && _points.All(x => x.WorkingSetBytes.HasValue);
                var privateBytesAvailable = _isProcessAvailable && _points.Count > 0 && _points.All(x => x.PrivateBytes.HasValue);
                return new
                {
                    Samples = _points.Count,
                    Truncated = _isTruncated,
                    ProcessCountersAvailable = workingSetAvailable && privateBytesAvailable,
                    WorkingSetCounterAvailable = workingSetAvailable,
                    PrivateCounterAvailable = privateBytesAvailable,
                    ProcessCounterUnavailable = _unavailable ?? (workingSetAvailable && privateBytesAvailable ? null : "Runtime process counters returned missing/non-positive values; these are not zero-memory measurements."),
                    PeakWorkingSetBytes = workingSetAvailable ? _points.Max(x => x.WorkingSetBytes) : null,
                    PeakPrivateBytes = privateBytesAvailable ? _points.Max(x => x.PrivateBytes) : null,
                    PeakMonoUsedBytes = _points.Select(x => x.MonoUsedBytes).DefaultIfEmpty().Max(),
                    PeakUnityAllocatedBytes = _points.Select(x => x.UnityAllocatedBytes).DefaultIfEmpty().Max(),
                    PeakUnityReservedBytes = _points.Select(x => x.UnityReservedBytes).DefaultIfEmpty().Max(),
                    PeakAllocatedContentBytes = _points.Select(x => x.Content.AllocatedBufferBytes).DefaultIfEmpty().Max(),
                    PeakReservedContentBytes = _points.Select(x => x.Content.ReservedBufferBytes).DefaultIfEmpty().Max(),
                    SubjectObservedPeakContentBytes = _points.Select(x => x.Content.PeakBufferBytes).DefaultIfEmpty().Max(),
                    PeakLiveInputs = _points.Select(x => x.Content.LiveInputs).DefaultIfEmpty().Max(),
                    PeakContentWork = _points.Select(x => x.Content.WorkCount).DefaultIfEmpty().Max(),
                    PeakDeclaredNativeReadBytes = _points.Select(x => x.Content.DeclaredNativeReadBufferBytes).DefaultIfEmpty().Max(),
                    PeakProcessCngProviders = _points.Select(x => x.Content.ProcessCngProviderCount).DefaultIfEmpty().Max(),
                    PeakProcessCngKeys = _points.Select(x => x.Content.ProcessCngKeyCount).DefaultIfEmpty().Max(),
                    PeakProcessCngKeyObjectBytes = _points.Select(x => x.Content.ProcessCngKeyObjectBytes).DefaultIfEmpty().Max(),
                    Points = _points,
                    Note = "Main-thread/tick sampled high-water marks, not exact instantaneous peaks. Profiler totals are logical process/Unity counters, not native allocation counts. No GC.Collect or OS cache flush. Allocation deltas, when capability-proven, include this observer and test consumers."
                };
            }
            public void Dispose() => _process.Dispose();
        }
    }
}
