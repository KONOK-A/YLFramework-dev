using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Scripting;
using YLFramework.Buff;
using YLFramework.Audio;
using YLFramework.Skill;
using Object = UnityEngine.Object;

namespace YLFramework.Pooling.Acceptance
{
    public static class PoolBenchmarkWorkload
    {
        [Serializable]
        public sealed class Measurement
        {
            public string caseId;
            public int iterations;
            public int pairsPerIteration;
            public int requestedCapacity;
            public long checksumPerIteration;
            public long[] ticksBefore;
            public long[] ticksAfter;
            public string allocationStatus;
            public long[] allocationEvents;
            public long[] completionTicks = Array.Empty<long>();
            public long[] checksums;
            public double medianNanosecondsPerPair;
            public double p95NanosecondsPerPair;
            public int cachedCount = -1;
            public int retainedCapacity = -1;
            public int factoryCalls = -1;
            public int resetCalls = -1;
            public int discardCalls = -1;

            internal Measurement(string id, int count, int pairs, long checksum, int samples = 31)
            {
                caseId = id;
                iterations = count;
                pairsPerIteration = pairs;
                checksumPerIteration = checksum;
                ticksBefore = new long[samples];
                ticksAfter = new long[samples];
                allocationStatus = _collectEvents ? "MeasuredEvents" : "NotMeasured";
                allocationEvents = _collectEvents ? new long[samples] : Array.Empty<long>();
                checksums = new long[samples];
            }

            internal void Sample(int sample, Func<long> work)
            {
                long checksum = 0;
                var recorder = _collectEvents ? ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording) : default;
                try
                {
                    ticksBefore[sample] = Stopwatch.GetTimestamp();
                    for (int index = 0; index < iterations; index++)
                    {
                        checksum += work();
                    }
                    ticksAfter[sample] = Stopwatch.GetTimestamp();
                }
                finally
                {
                    if (_collectEvents)
                    {
                        recorder.Stop();
                        try
                        {
                            allocationEvents[sample] = AllocationEvents(recorder);
                        }
                        finally { recorder.Dispose(); }
                    }
                }
                checksums[sample] = checksum;
                Require(checksum == checksumPerIteration * iterations, "Work/checksum changed: " + caseId);
                Require(ticksAfter[sample] > ticksBefore[sample], "Invalid monotonic timer: " + caseId);
            }

            internal void Finish()
            {
                var samples = new double[ticksBefore.Length];
                for (int index = 0; index < samples.Length; index++)
                {
                    samples[index] = (ticksAfter[index] - ticksBefore[index]) * (1e9 / Stopwatch.Frequency) /
                        ((long)iterations * pairsPerIteration);
                }
                Array.Sort(samples);
                medianNanosecondsPerPair = samples[samples.Length / 2];
                p95NanosecondsPerPair = samples[(int)Math.Ceiling(samples.Length * .95) - 1];
            }
        }

        private sealed class Cell
        {
            internal int value;
        }

        private sealed class Host : IEquippableSkill, IAddableBuff
        {
            public Dictionary<int, SkillBase> Skills { get; } = new();
            public List<BuffBase> Buffs { get; } = new();
            public bool CanRelease() => true;
            public bool CanAdd(int id) => true;
            public void OnAddBuff(BuffBase buff)
            {
            }
            public void OnRemoveBuff(BuffBase buff)
            {
            }
        }

        [Preserve]
        public sealed class MeasuredSkill : SkillBase
        {
            [Preserve]
            public MeasuredSkill()
            {
            }
            protected override void OnUpdate() => RecordDomain(0);
            protected override void OnFixedUpdate() => RecordDomain(1);
            protected override void OnLateUpdate() => RecordDomain(2);
        }

        [Preserve]
        public sealed class MeasuredBuff : BuffBase
        {
            [Preserve]
            public MeasuredBuff()
            {
            }
            protected override void OnUpdate() => RecordDomain(0);
            public override void OnFixedUpdate() => RecordDomain(1);
            public override void OnLateUpdate() => RecordDomain(2);
        }

        [Serializable]
        public sealed class Calibration
        {
            public bool performed;
            public long emptyEvents = -1;
            public long smallAllocationEvents = -1;
            public long largeAllocationEvents = -1;
        }

        private const ProfilerRecorderOptions Recording = ProfilerRecorderOptions.SumAllSamplesInFrame |
            ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
        private static bool _collectEvents;
        private static byte[] _calibration;
        private static readonly long[] _domainCalls = new long[3];
        private static Action<int> _dispatch;
        private static bool _nestNextUpdate;

        public static Calibration Calibrate(bool collectEvents)
        {
            _collectEvents = collectEvents;
            Stopwatch.GetTimestamp();
            var result = new Calibration();
            if (!collectEvents)
            {
                return result;
            }
            Require(!Application.isEditor && UnityEngine.Debug.isDebugBuild, "GC allocation events are only measured in a Development Player.");
            ReadCalibration(32);
            ReadCalibration(8192);
            ReadCalibration(0);
            result.emptyEvents = ReadCalibration(0);
            result.smallAllocationEvents = ReadCalibration(32);
            result.largeAllocationEvents = ReadCalibration(8192);
            Require(result.emptyEvents == 0, "The empty measurement interval includes harness allocations.");
            Require(result.smallAllocationEvents == 1 && result.largeAllocationEvents == 1,
                "Known small/large allocations must each produce one event, not a byte count.");
            result.performed = true;
            GC.KeepAlive(_calibration);
            return result;
        }

        private static long ReadCalibration(int size)
        {
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording);
            if (size > 0)
            {
                _calibration = new byte[size];
                _calibration[size - 1] = 1;
            }
            Stopwatch.GetTimestamp();
            recorder.Stop();
            return AllocationEvents(recorder);
        }

        private static long AllocationEvents(ProfilerRecorder recorder)
        {
            Require(recorder.Valid && recorder.Count <= 1, "GC.Alloc recording is unavailable or its sample window changed.");
            return recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
        }

        public static IEnumerator Run(List<Measurement> results)
        {
            foreach (int width in new[] { 1, 32, 512 })
            {
                foreach (var order in new[] { PoolCacheOrder.Fifo, PoolCacheOrder.Lifo })
                {
                    results.Add(MeasureObjects(width, "Self." + order, order));
                }
                results.Add(MeasureObjects(width, "Unity", PoolCacheOrder.Lifo));
                results.Add(MeasureObjects(width, "New", PoolCacheOrder.Lifo));
            }
            results.Add(MeasureObjects(32, "Self.Fifo", PoolCacheOrder.Fifo, true));
            results.Add(MeasureObjects(32, "Self.Lifo", PoolCacheOrder.Lifo, true));
            results.Add(MeasureObjects(32, "Unity", PoolCacheOrder.Lifo, true));
            foreach (string implementation in new[] { "Self", "Unity", "New" })
            {
                foreach (bool dictionary in new[] { false, true })
                {
                    MeasureCollections(results, implementation, dictionary);
                }
            }
            foreach (bool skill in new[] { true, false })
            {
                foreach (bool nested in new[] { false, true })
                {
                    results.Add(MeasureDomain(skill, nested));
                }
            }
            foreach (var measurement in MeasureLeases())
            {
                results.Add(measurement);
            }
            foreach (int count in new[] { 1, 32, 512 })
            {
                MeasureGameObjects(results, count);
            }
            MeasureGameObjects(results, 128, false);
            foreach (int count in new[] { 32, 128, 512 })
            {
                var direct = MeasureDirectGameObjects(results, count);
                try
                {
                    while (direct.MoveNext())
                    {
                        yield return direct.Current;
                    }
                }
                finally { (direct as IDisposable)?.Dispose(); }
            }
            var parent = new GameObject("PoolingPressureRoot");
            var source = new GameObject("PoolingPressureSource");
            source.SetActive(false);
            var pool = parent.AddComponent<GameObjectPool>();
            const string Key = "PoolingPressure";
            try
            {
                pool.InitOneGoData(Key, 32, source, 32);
                var buffer = new GameObject[31];
                for (int index = 0; index < buffer.Length; index++)
                {
                    buffer[index] = pool.Allocate(Key);
                }
                foreach (var instance in buffer)
                {
                    pool.Recycle(Key, instance);
                }
                Func<long> work = () =>
                {
                    long valid = 0;
                    for (int index = 0; index < buffer.Length; index++)
                    {
                        buffer[index] = pool.Allocate(Key);
                        if (buffer[index])
                        {
                            valid++;
                        }
                    }
                    foreach (var instance in buffer)
                    {
                        pool.Recycle(Key, instance);
                    }
                    return valid;
                };
                var measured = new Measurement("GameObject.fakeNullPressure.32", 1, 31, 31);
                for (int sample = 0; sample < measured.ticksBefore.Length; sample++)
                {
                    Object.Destroy(buffer[16]);
                    yield return null;
                    measured.Sample(sample, work);
                    Require(pool.GetCachedCount(Key) == 31, "Pressure checkout lost surviving instances.");
                    pool.InitOneGoData(Key, 32, source, 32);
                }
                measured.cachedCount = pool.GetCachedCount(Key);
                measured.Finish();
                results.Add(measured);
            }
            finally
            {
                pool.ClearAll();
                Object.Destroy(source);
                Object.Destroy(parent);
            }
            yield return null;
            yield return null;
            var audio = MeasureAudioPending(results);
            try
            {
                while (audio.MoveNext())
                {
                    yield return audio.Current;
                }
            }
            finally { (audio as IDisposable)?.Dispose(); }
        }

        private sealed class PendingOwner : IDisposable
        {
            internal int Releases;
            public void Dispose() => Releases++;
        }

        private sealed class PendingProvider : IAudioClipProvider
        {
            internal int Loads;
            private Action<AudioClipLease, Exception> _completion;
            public bool CanCache => true;
            public AudioClipLease Load(string key) => throw new InvalidOperationException("This workload uses actual asynchronous waiting.");
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                Loads++;
                _completion = completed;
                progress?.Invoke(.4f);
            }
            internal PendingOwner Complete(AudioClip clip)
            {
                var owner = new PendingOwner();
                var completion = _completion;
                _completion = null;
                completion(new AudioClipLease(clip, owner), null);
                return owner;
            }
        }

        private static IEnumerator MeasureAudioPending(List<Measurement> results)
        {
            AudioKit.Shutdown();
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = 0 };
            AudioKit.ClipCacheCapacity = 0;
            var channel = AudioKit.RegisterChannel("Pooling.Pending", AudioCategory.Sound, AudioChannelMode.Overlap);
            var clip = AudioClip.Create("pooling-pending-provider", 256, 1, 44100, false);
            try
            {
                // 诊断只在计时外反射，不扩大Audio内部可见性或在采样中调用MethodInfo.Invoke。
                var type = typeof(AudioKit).Assembly.GetType("YLFramework.Audio.AudioManager", true);
                var managers = Object.FindObjectsOfType(type);
                Require(managers.Length == 1, "Use the real, unique owned Audio host.");
                var update = (Action)Delegate.CreateDelegate(typeof(Action), managers[0],
                    type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic));
                foreach (int count in new[] { 0, 1, 24, 128 })
                {
                    var provider = new PendingProvider();
                    var address = new AudioClipAddress(provider, "pooling-pending");
                    var requests = new AudioPlayRequest[count];
                    var measured = new Measurement("Audio.Pending." + count, 64, 1, count);
                    Func<long> work = () => { update(); return count; };
                    try
                    {
                        for (int index = 0; index < count; index++)
                        {
                            requests[index] = AudioKit.PlaySoundAsync(address);
                        }
                        yield return null;
                        yield return null;
                        Require(provider.Loads == (count == 0 ? 0 : 1), "Each request is real; only its shared asynchronous provider completion is deferred.");
                        foreach (var request in requests)
                        {
                            Require(request.State == AudioPlayState.Loading && !request.IsAssetReady && !request.IsDone, "Do not sample an empty/completed workload.");
                        }
                        for (int warm = 0; warm < 8; warm++)
                        {
                            update();
                        }
                        for (int sample = 0; sample < measured.ticksBefore.Length; sample++)
                        {
                            measured.Sample(sample, work);
                        }
                        foreach (var request in requests)
                        {
                            Require(request.State == AudioPlayState.Loading && !request.IsDone, "Pending working set changed during sampling.");
                        }
                        Require(AudioKit.SoundCount == 0, "Waiting requests must not consume active playback voices.");
                        measured.Finish();
                        results.Add(measured);
                    }
                    finally { foreach (var request in requests) { request?.Cancel(); } }
                    foreach (var request in requests)
                    {
                        Require(request.EndReason == AudioPlayEnd.Canceled, "Each waiting request must terminate.");
                    }
                    if (count > 0)
                    {
                        Require(provider.Complete(clip).Releases == 1, "Late backend responsibility must be released exactly once.");
                    }
                }
            }
            finally
            {
                channel.Dispose();
                AudioKit.Shutdown();
                Object.Destroy(clip);
            }
            yield return null;
        }

        private static Measurement Measure(string id, int iterations, int pairs, long checksum, Func<long> work,
            int samples = 31, Action prepare = null, Action validate = null)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int warm = 0; warm < 8; warm++)
            {
                work();
            }
            var measured = new Measurement(id, iterations, pairs, checksum, samples);
            for (int sample = 0; sample < samples; sample++)
            {
                prepare?.Invoke();
                measured.Sample(sample, work);
                validate?.Invoke();
            }
            measured.Finish();
            return measured;
        }

        private static Measurement MeasureObjects(int width, string implementation, PoolCacheOrder order, bool defaultCapacity = false)
        {
            int created = 0;
            int resets = 0;
            int discarded = 0;
            Func<Cell> create = () => { created++; return new Cell(); };
            Action<Cell> reset = value => { value.value = 0; resets++; };
            Action<Cell> discard = value => discarded++;
            ObjectPool<Cell> self = implementation.StartsWith("Self", StringComparison.Ordinal)
                ? defaultCapacity ? new ObjectPool<Cell>(create, reset: reset, onDiscard: discard, cacheOrder: order)
                    : new ObjectPool<Cell>(create, reset: reset, onDiscard: discard, maxPoolSize: width, cacheOrder: order) : null;
            int limit = defaultCapacity ? 20 : width;
            UnityEngine.Pool.ObjectPool<Cell> official = implementation == "Unity"
                ? new UnityEngine.Pool.ObjectPool<Cell>(create, actionOnRelease: reset, actionOnDestroy: discard,
                    collectionCheck: true, defaultCapacity: limit, maxSize: limit) : null;
            Func<Cell> get = self != null ? self.Allocate : official != null ? official.Get : create;
            Action<Cell> release = self != null ? value => self.Recycle(value) : official != null ? official.Release : reset;
            var buffer = new Cell[width];
            try
            {
                long Work()
                {
                    long checksum = 0;
                    for (int index = 0; index < buffer.Length; index++)
                    {
                        buffer[index] = get();
                        checksum += buffer[index].value * 1000000L;
                        buffer[index].value = index + 1;
                    }
                    foreach (var item in buffer)
                    {
                        checksum += item.value;
                        release(item);
                    }
                    return checksum;
                }
                int iterations = width == 1 ? 8192 : width == 32 ? 256 : 16;
                long expected = width == 1 ? 1 : width == 32 ? 528 : 131328;
                string capacity = defaultCapacity ? implementation == "Unity" ? ".max20" : ".default20" : string.Empty;
                var measured = Measure("Object." + implementation + capacity + "." + width, iterations, width, expected, Work);
                measured.cachedCount = self?.CurStoreCount ?? official?.CountInactive ?? 0;
                measured.factoryCalls = created;
                measured.resetCalls = resets;
                measured.discardCalls = discarded;
                Require(resets == (8 + 31 * iterations) * width, "Object reset count changed.");
                if (defaultCapacity)
                {
                    Require(measured.cachedCount == 20 && created == 20 + (8 + 31 * iterations) * 12 &&
                        discarded == (8 + 31 * iterations) * 12, "Default-20 overflow must recreate and discard precisely its excess working set.");
                }
                else
                {
                    Require(implementation == "New" || created == width, "Warmed object working set was not retained.");
                }
                return measured;
            }
            finally
            {
                self?.Dispose();
                official?.Dispose();
            }
        }

        private static IEnumerable<Measurement> MeasureLeases()
        {
            foreach (bool discard in new[] { false, true })
            {
                int created = 0;
                int resets = 0;
                int discarded = 0;
                using var pool = new ObjectPool<Cell>(() => { created++; return new Cell(); },
                    reset: value => { resets++; value.value = 0; }, onDiscard: _ => discarded++);
                long Work()
                {
                    using var rental = pool.Rent(out var value);
                    long checksum = 1 + value.value * 1000000L;
                    value.value = 17;
                    if (discard)
                    {
                        rental.Discard();
                    }
                    return checksum;
                }
                var measurement = Measure("Lease.Object." + (discard ? "discard" : "steady"), 8192, 1, 1, Work);
                int cycles = 8 + 31 * 8192;
                Require(resets == cycles && created == (discard ? cycles : 1) && discarded == (discard ? cycles : 0),
                    "Lease reset/discard counts or warmed identity reuse changed.");
                measurement.factoryCalls = created;
                measurement.resetCalls = resets;
                measurement.discardCalls = discarded;
                measurement.cachedCount = pool.CurStoreCount;
                yield return measurement;
            }
            ListPool<int>.Clear();
            DictionaryPool<int, int>.Clear();
            List<int> lastList = null;
            Dictionary<int, int> lastDictionary = null;
            long ListWork()
            {
                using var rental = ListPool<int>.Rent(out var value, 64);
                lastList = value;
                value.Add(1);
                return value.Count;
            }
            long DictionaryWork()
            {
                using var rental = DictionaryPool<int, int>.Rent(out var value, 64);
                lastDictionary = value;
                value.Add(1, 1);
                return value.Count;
            }
            var list = Measure("Lease.List.steady", 8192, 1, 1, ListWork);
            list.cachedCount = CollectionCachedCount(false);
            list.retainedCapacity = lastList.Capacity;
            list.requestedCapacity = 64;
            yield return list;
            var dictionary = Measure("Lease.Dictionary.steady", 8192, 1, 1, DictionaryWork);
            dictionary.cachedCount = CollectionCachedCount(true);
            dictionary.retainedCapacity = lastDictionary.EnsureCapacity(0);
            dictionary.requestedCapacity = 64;
            yield return dictionary;
            ListPool<int>.Clear();
            DictionaryPool<int, int>.Clear();
        }

        private static void MeasureCollections(List<Measurement> results, string implementation, bool dictionary)
        {
            UnityEngine.Pool.ObjectPool<List<int>> lists = null;
            UnityEngine.Pool.ObjectPool<Dictionary<int, int>> dictionaries = null;
            List<int> lastList = null;
            Dictionary<int, int> lastDictionary = null;
            if (implementation == "Self")
            {
                ListPool<int>.Clear();
                DictionaryPool<int, int>.Clear();
            }
            try
            {
                foreach (int requested in new[] { 0, 16, 256, 4096, 65536 })
                {
                    lists?.Dispose();
                    dictionaries?.Dispose();
                    if (implementation == "Self")
                    {
                        ListPool<int>.Clear();
                        DictionaryPool<int, int>.Clear();
                    }
                    if (implementation == "Unity")
                    {
                        lists = new UnityEngine.Pool.ObjectPool<List<int>>(() => new List<int>(requested), actionOnRelease: value => value.Clear());
                        dictionaries = new UnityEngine.Pool.ObjectPool<Dictionary<int, int>>(() => new Dictionary<int, int>(requested), actionOnRelease: value => value.Clear());
                    }
                    Measurement Run(int capacity, bool recovery)
                    {
                        long Work()
                        {
                            if (dictionary)
                            {
                                var value = implementation == "Self" ? DictionaryPool<int, int>.Allocate(capacity) :
                                    implementation == "Unity" ? dictionaries.Get() : new Dictionary<int, int>(capacity);
                                lastDictionary = value;
                                for (int index = 0; index < capacity; index++)
                                {
                                    value.Add(index, index + 1);
                                }
                                long checksum = value.Count + (capacity > 0 ? value[capacity - 1] : 0);
                                if (implementation == "Self")
                                {
                                    DictionaryPool<int, int>.Recycle(value);
                                }
                                else if (implementation == "Unity")
                                {
                                    dictionaries.Release(value);
                                }
                                else
                                {
                                    value.Clear();
                                }
                                return checksum;
                            }
                            else
                            {
                                var value = implementation == "Self" ? ListPool<int>.Allocate(capacity) :
                                    implementation == "Unity" ? lists.Get() : new List<int>(capacity);
                                lastList = value;
                                for (int index = 0; index < capacity; index++)
                                {
                                    value.Add(index + 1);
                                }
                                long checksum = value.Count + (capacity > 0 ? value[capacity - 1] : 0);
                                if (implementation == "Self")
                                {
                                    ListPool<int>.Recycle(value);
                                }
                                else if (implementation == "Unity")
                                {
                                    lists.Release(value);
                                }
                                else
                                {
                                    value.Clear();
                                }
                                return checksum;
                            }
                        }
                        int iterations = capacity == 0 ? 1024 : capacity == 16 ? 256 : capacity == 256 ? 64 : capacity == 4096 ? 8 : 1;
                        string id = (dictionary ? "Dictionary." : "List.") + implementation + "." +
                            (recovery ? "recovery16" : capacity.ToString());
                        var measured = Measure(id, iterations, 1, 2L * capacity, Work);
                        measured.requestedCapacity = capacity;
                        measured.cachedCount = implementation == "New" ? 0 : dictionary
                            ? implementation == "Self" ? CollectionCachedCount(true) : dictionaries.CountInactive
                            : implementation == "Self" ? CollectionCachedCount(false) : lists.CountInactive;
                        measured.retainedCapacity = measured.cachedCount == 0 ? 0 : dictionary
                            ? lastDictionary.EnsureCapacity(0) : lastList.Capacity;
                        return measured;
                    }
                    results.Add(Run(requested, false));
                    if (requested == 65536)
                    {
                        results.Add(Run(16, true));
                    }
                }
            }
            finally
            {
                lists?.Dispose();
                dictionaries?.Dispose();
                ListPool<int>.Clear();
                DictionaryPool<int, int>.Clear();
            }
        }

        private static Measurement MeasureDomain(bool skill, bool nested)
        {
            SkillManager.Shutdown();
            BuffManager.Shutdown();
            var skills = ScriptableObject.CreateInstance<SkillsConfig>();
            var buffs = ScriptableObject.CreateInstance<BuffsConfig>();
            skills.name = "PoolingSkills";
            buffs.name = "PoolingBuffs";
            var hosts = new Host[32];
            try
            {
                for (int index = 0; index < 8; index++)
                {
                    var definition = ScriptableObject.CreateInstance<SkillConfigData>();
                    definition.id = index;
                    definition.duration = 1000000000;
                    definition.skillClassFullName = typeof(MeasuredSkill).FullName;
                    definition.parent = skills;
                    for (int id = 0; id < 8; id++)
                    {
                        definition.simultaneousSkillIds.Add(id);
                    }
                    skills.skills.Add(definition);
                    var buff = ScriptableObject.CreateInstance<BuffConfigData>();
                    buff.id = index;
                    buff.duration = 1000000000;
                    buff.buffClassFullName = typeof(MeasuredBuff).FullName;
                    buff.parent = buffs;
                    buffs.buffs.Add(buff);
                }
                if (skill)
                {
                    SkillManager.AddConfig(skills);
                }
                else
                {
                    BuffManager.AddConfig(buffs);
                }
                for (int index = 0; index < hosts.Length; index++)
                {
                    hosts[index] = new Host();
                    for (int id = 0; id < 8; id++)
                    {
                        if (skill)
                        {
                            var value = SkillManager.AddSkills(hosts[index], id);
                            Require(value != null && value.Release() == ReleaseSkillStatus.Success, "Skill setup did not actually release.");
                        }
                        else
                        {
                            Require(BuffManager.AddBuff(hosts[index], id) != null, "Buff setup did not attach.");
                        }
                    }
                }
                var method = (skill ? typeof(SkillManager) : typeof(BuffManager)).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Static);
                Require(method != null, "The real domain dispatcher is unavailable.");
                _dispatch = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), method);
                Array.Clear(_domainCalls, 0, _domainCalls.Length);
                long Frame()
                {
                    long before = _domainCalls[0] + _domainCalls[1] + _domainCalls[2];
                    _nestNextUpdate = nested;
                    _dispatch(0);
                    _dispatch(1);
                    _dispatch(2);
                    return _domainCalls[0] + _domainCalls[1] + _domainCalls[2] - before;
                }
                var measured = Measure("Domain." + (skill ? "Skill" : "Buff") + (nested ? ".nested" : ".steady"),
                    64, 1, nested ? 1024 : 768, Frame);
                long frames = 8 + 31L * 64;
                Require(_domainCalls[0] == frames * (nested ? 512 : 256) && _domainCalls[1] == frames * 256 &&
                    _domainCalls[2] == frames * 256, "A measured domain phase skipped callbacks.");
                Require((skill ? SkillManager.HostCount : BuffManager.HostCount) == 32, "A measured domain lost hosts.");
                foreach (var host in hosts)
                {
                    Require((skill ? host.Skills.Count : host.Buffs.Count) == 8, "A measured domain lost active instances.");
                }
                return measured;
            }
            finally
            {
                _nestNextUpdate = false;
                _dispatch = null;
                SkillManager.Shutdown();
                BuffManager.Shutdown();
                foreach (var definition in skills.skills)
                {
                    Object.Destroy(definition);
                }
                foreach (var definition in buffs.buffs)
                {
                    Object.Destroy(definition);
                }
                Object.Destroy(skills);
                Object.Destroy(buffs);
            }
        }

        private static void RecordDomain(int phase)
        {
            _domainCalls[phase]++;
            if (phase == 0 && _nestNextUpdate)
            {
                _nestNextUpdate = false;
                _dispatch(0);
            }
        }

        private static int CollectionCachedCount(bool dictionary)
        {
            // Diagnostics are outside measured regions; do not widen product visibility for a benchmark.
            var type = dictionary ? typeof(DictionaryPool<int, int>) : typeof(ListPool<int>);
            var field = type.GetField("_pool", BindingFlags.Static | BindingFlags.NonPublic);
            Require(field != null, "Collection diagnostics cannot identify the actual backing pool.");
            return dictionary ? ((ObjectPool<Dictionary<int, int>>)field.GetValue(null)).CurStoreCount :
                ((ObjectPool<List<int>>)field.GetValue(null)).CurStoreCount;
        }

        private static void MeasureGameObjects(List<Measurement> results, int count, bool measureSteady = true)
        {
            var parent = new GameObject("PoolingBucketRoot");
            var source = new GameObject("PoolingBucketSource");
            source.SetActive(false);
            var pool = parent.AddComponent<GameObjectPool>();
            const string Key = "PoolingBucket";
            try
            {
                pool.InitOneGoData(Key, count, source, count);
                long Borrow()
                {
                    var instance = pool.Allocate(Key);
                    long valid = instance ? 1 : 0;
                    pool.Recycle(Key, instance);
                    return valid;
                }
                if (measureSteady)
                {
                    var steady = Measure("GameObject.steady." + count, 128, 1, 1, Borrow);
                    Require(pool.GetCachedCount(Key) == count, "A healthy bucket lost idle instances.");
                    steady.cachedCount = pool.GetCachedCount(Key);
                    results.Add(steady);
                }
                if (count > 1)
                {
                    var cold = Measure("GameObject.prewarm." + count, 1, count, count,
                        () => { pool.InitOneGoData(Key, count, source, count); return count; }, 5, () => pool.Clear(Key),
                        () => Require(pool.GetCachedCount(Key) == count, "Prewarm did not create its requested live working set."));
                    cold.cachedCount = pool.GetCachedCount(Key);
                    results.Add(cold);
                }
            }
            finally
            {
                pool.ClearAll();
                Object.Destroy(source);
                Object.Destroy(parent);
            }
        }

        private static IEnumerator MeasureDirectGameObjects(List<Measurement> results, int count)
        {
            var parent = new GameObject("PoolingDirectRoot");
            var source = new GameObject("PoolingDirectSource");
            source.SetActive(false);
            var buffer = new GameObject[count];
            long Spawn()
            {
                long valid = 0;
                for (int index = 0; index < buffer.Length; index++)
                {
                    buffer[index] = Object.Instantiate(source, parent.transform);
                    if (buffer[index])
                    {
                        valid++;
                    }
                }
                return valid;
            }
            long DestroyBatch()
            {
                long valid = 0;
                foreach (var instance in buffer)
                {
                    if (instance)
                    {
                        valid++;
                    }
                    Object.Destroy(instance);
                }
                return valid;
            }
            Func<long> spawn = Spawn;
            Func<long> destroy = DestroyBatch;
            var create = new Measurement("GameObject.instantiate." + count, 1, count, count, 5);
            var dispose = new Measurement("GameObject.destroyRequest." + count, 1, count, count, 5)
            {
                // Includes Unity's end-of-frame wait, not just the synchronous Destroy call.
                completionTicks = new long[5]
            };
            try
            {
                spawn();
                destroy();
                yield return null;
                for (int sample = 0; sample < 5; sample++)
                {
                    create.Sample(sample, spawn);
                    dispose.Sample(sample, destroy);
                    yield return null;
                    dispose.completionTicks[sample] = Stopwatch.GetTimestamp() - dispose.ticksBefore[sample];
                    foreach (var instance in buffer)
                    {
                        Require(!instance, "Direct Destroy did not complete at the frame boundary.");
                    }
                }
                create.Finish();
                dispose.Finish();
                results.Add(create);
                results.Add(dispose);
            }
            finally
            {
                foreach (var instance in buffer)
                {
                    if (instance)
                    {
                        Object.Destroy(instance);
                    }
                }
                Object.Destroy(source);
                Object.Destroy(parent);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
