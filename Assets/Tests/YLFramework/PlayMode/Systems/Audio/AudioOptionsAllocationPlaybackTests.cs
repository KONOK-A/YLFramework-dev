using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Audio;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace YLFramework.Tests.Audio
{
    public class AudioOptionsAllocationPlaybackTests
    {
        private const int Requests = 24;
        private const int Rounds = 4;
        private const ProfilerRecorderOptions Recording = ProfilerRecorderOptions.SumAllSamplesInFrame |
                                                          ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };

        private enum Path
        {
            SoundDefault,
            SoundReused,
            MusicSimple,
            MusicNull,
            MusicReused,
            VoiceSimple,
            VoiceNull,
            VoiceReused,
            CrossfadeDefault,
            CrossfadeReused,
            Emitter,
            MusicPlayer,
            MusicPlayerCrossfade,
            ActionAllocateDefault,
            ActionAllocateReused,
            ActionStartDefault,
            ActionStartReused,
            ChannelClosedDefault,
            ChannelClosedReused,
            DedupDefault,
            DedupReused,
            GlobalBudgetDefault,
            GlobalBudgetReused,
            ClipBudgetDefault,
            ClipBudgetReused
        }

        private struct Measurement
        {
            public bool Valid;
            public long Events;
            public long Ticks;
        }

        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private readonly AudioHandle[] _handles = new AudioHandle[Requests];
        private readonly PlaySoundAction[] _actions = new PlaySoundAction[Requests];
        private readonly HashSet<AudioVoice> _soundVoices = new HashSet<AudioVoice>();
        private readonly HashSet<int> _soundSources = new HashSet<int>();
        private readonly HashSet<PlaySoundAction> _pooledActions = new HashSet<PlaySoundAction>();
        private readonly HashSet<int> _slotSources = new HashSet<int>();
        private AudioClip _clip;
        private GameObject _listener;
        private GameObject _emitterRoot;
        private GameObject _musicRoot;
        private AudioEmitter _emitter;
        private AudioMusicPlayer _musicPlayer;
        private AudioSoundOptions _options;
        private Action<AudioClip, AudioHandle> _started;
        private ActionRecycleDriver _previousDriver;
        private AudioHandle _blocker;
        private Random.State _randomState;
        private int _starts;
        private int _emitterStarts;
        private bool _hasInvalidCallback;
        private byte[] _calibrationProbe;
        private AudioPlaybackOptions _lastSnapshot;
        private double _snapshotChecksum;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _randomState = Random.state;
            _previousDriver = Object.FindObjectOfType<ActionRecycleDriver>(true);
            ActionQueue.Drain();
            AudioKit.Shutdown();
            for (var i = 0; i < _keys.Length; i++)
            {
                _hadKeys[i] = PlayerPrefs.HasKey(_keys[i]);
            }

            for (var i = 0; i < 3; i++)
            {
                _switches[i] = PlayerPrefs.GetInt(_keys[i]);
                _volumes[i] = PlayerPrefs.GetFloat(_keys[i + 3]);
            }
            var settings = AudioKit.Settings;
            settings.IsSoundOn.SetValueWithoutCallback(true);
            settings.IsMusicOn.SetValueWithoutCallback(true);
            settings.IsVoiceOn.SetValueWithoutCallback(true);
            settings.SoundVolume.SetValueWithoutCallback(1f);
            settings.MusicVolume.SetValueWithoutCallback(1f);
            settings.VoiceVolume.SetValueWithoutCallback(1f);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            AudioKit.SoundCacheCapacity = Requests;
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = Requests };
            _clip = AudioClip.Create("options-allocation-playback", 220500, 1, 44100, false);
            _options = AudioSoundOptions.Defaults;
            _started = OnStarted;
            var hasListener = false;
            foreach (var listener in Object.FindObjectsOfType<AudioListener>())
            {
                if (listener.isActiveAndEnabled)
                {
                    hasListener = true;
                    break;
                }
            }

            if (!hasListener)
            {
                _listener = new GameObject("OptionsAllocation.Listener");
                _listener.AddComponent<AudioListener>();
            }

            _emitterRoot = new GameObject("OptionsAllocation.Emitter");
            _emitterRoot.SetActive(false);
            _emitter = _emitterRoot.AddComponent<AudioEmitter>();
            SetField(_emitter, "clips", new List<AudioClip> { _clip });
            SetField(_emitter, "playOnAwake", false);
            _emitter.Started.AddListener(OnEmitterStarted);
            _emitterRoot.SetActive(true);
            _musicRoot = new GameObject("OptionsAllocation.MusicPlayer");
            _musicRoot.SetActive(false);
            _musicPlayer = _musicRoot.AddComponent<AudioMusicPlayer>();
            SetField(_musicPlayer, "clip", _clip);
            SetField(_musicPlayer, "playOnAwake", false);
            SetField(_musicPlayer, "loop", false);
            _musicRoot.SetActive(true);
            _soundVoices.Clear();
            _soundSources.Clear();
            _pooledActions.Clear();
            _slotSources.Clear();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            CleanupBatch();
            AudioKit.Shutdown();
            if (_emitterRoot)
            {
                Object.Destroy(_emitterRoot);
            }

            if (_musicRoot)
            {
                Object.Destroy(_musicRoot);
            }

            if (_listener)
            {
                Object.Destroy(_listener);
            }

            if (_clip)
            {
                Object.Destroy(_clip);
            }

            var driver = Object.FindObjectOfType<ActionRecycleDriver>(true);
            if (driver && driver != _previousDriver)
            {
                Object.Destroy(driver.gameObject);
            }

            _calibrationProbe = null;
            for (var i = 0; i < 3; i++)
            {
                if (_hadKeys[i])
                {
                    PlayerPrefs.SetInt(_keys[i], _switches[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(_keys[i]);
                }

                if (_hadKeys[i + 3])
                {
                    PlayerPrefs.SetFloat(_keys[i + 3], _volumes[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(_keys[i + 3]);
                }
            }
            yield return null;
            Random.state = _randomState;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, name);
            field.SetValue(target, value);
        }

        private void OnStarted(AudioClip clip, AudioHandle handle)
        {
            _handles[_starts++] = handle;
            _hasInvalidCallback |= clip != _clip || !handle.IsPlaying || handle.Voice.Source.clip != _clip;
        }

        private void OnEmitterStarted() => _emitterStarts++;

        [UnityTest]
        public IEnumerator GlobalModeValidationDoesNotBoxEnumsAfterInitialization()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            long calibrationEvents, emptyEvents, modeEvents;
            var valid = true;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                recorder.Stop();
                valid &= recorder.Valid;
            }
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                _calibrationProbe = new byte[4096];
                recorder.Stop();
                calibrationEvents = AllocationEvents(recorder);
                valid &= recorder.Valid;
            }
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                recorder.Stop();
                emptyEvents = AllocationEvents(recorder);
                valid &= recorder.Valid;
            }
            var checksum = 0;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                for (var i = 0; i < 4096; i++)
                {
                    AudioKit.PlaySoundMode = (AudioKit.PlaySoundModes)(i % 3);
                    checksum += (int)AudioKit.PlaySoundMode;
                }
                recorder.Stop();
                modeEvents = AllocationEvents(recorder);
                valid &= recorder.Valid;
            }
            Assert.IsTrue(valid);
            Assert.Greater(calibrationEvents, 0);
            Assert.AreEqual(0, emptyEvents);
            Assert.AreEqual(4095, checksum);
            Assert.AreEqual(0, modeEvents, "Valid global mode changes must not allocate enum boxes.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapshotDefaultsCaptureValidationAndVoiceValueStorageAllocateNoManagedEvents()
        {
            _options.Volume = .5f;
            _options.Pitch = 1.5f;
            _options.AutoFade = new AudioAutoFade(.25f, .5f);
            var handle = AudioKit.PlayVoiceWithOptions(_clip, _options);
            handle.Pause();
            var voice = handle.Voice;
            _options.Priority = -7;
            _options.PlaySoundMode = (AudioKit.PlaySoundModes)999;
            Enum.IsDefined(typeof(AudioCrossfadeCurve), AudioCrossfadeCurve.EqualPower);
            RunSnapshotStages(voice, 1);
            long calibrationEvents, emptyEvents, snapshotEvents, enumEvents;
            var validRecorders = true;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                recorder.Stop();
                validRecorders &= recorder.Valid;
            }
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                _calibrationProbe = new byte[4096];
                recorder.Stop();
                calibrationEvents = AllocationEvents(recorder);
                validRecorders &= recorder.Valid;
            }
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                recorder.Stop();
                emptyEvents = AllocationEvents(recorder);
                validRecorders &= recorder.Valid;
            }
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                RunSnapshotStages(voice, 4096);
                recorder.Stop();
                snapshotEvents = AllocationEvents(recorder);
                validRecorders &= recorder.Valid;
            }
            var enumMatches = 0;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                for (var i = 0; i < Requests; i++)
                {
                    if (Enum.IsDefined(typeof(AudioCrossfadeCurve), AudioCrossfadeCurve.EqualPower))
                    {
                        enumMatches++;
                    }
                }

                recorder.Stop();
                enumEvents = AllocationEvents(recorder);
                validRecorders &= recorder.Valid;
            }
            Assert.IsTrue(validRecorders);
            Assert.AreEqual(Requests, enumMatches);
            Assert.AreEqual(4096, _calibrationProbe.Length);
            Assert.Greater(calibrationEvents, 0);
            Assert.AreEqual(0, emptyEvents);
            Assert.AreEqual(0, snapshotEvents, "Default values, Capture, normalization, validation and request value storage must not allocate.");
            Assert.Greater(_snapshotChecksum, 0d);
            Assert.AreEqual(.5f, _lastSnapshot.Volume);
            Assert.AreEqual(1.5f, voice.Options.Pitch);
            Assert.AreEqual(.5f, voice.Options.Volume);
            Assert.AreEqual(-7, _options.Priority, "Normalization must not modify the public class.");
            UnityEngine.Debug.Log("Audio snapshot strict benchmark: iterations=4096; calibration events=" + calibrationEvents +
                                  "; empty events=" + emptyEvents + "; snapshot/storage events=" + snapshotEvents +
                                  "; isolated Enum.IsDefined events/24 calls=" + enumEvents + ".");
            handle.Stop();
            Assert.AreEqual(default(AudioPlaybackOptions), voice.Options);
            yield return null;
        }

        private void RunSnapshotStages(AudioVoice voice, int iterations)
        {
            double checksum = 0;
            for (var i = 0; i < iterations; i++)
            {
                var defaults = AudioPlaybackOptions.Defaults;
                var music = AudioPlaybackOptions.MusicDefaults;
                var omitted = AudioPlaybackOptions.Capture(null, true);
                defaults.Validate(true);
                music.Validate(false);
                omitted.Validate(false);
                var captured = AudioPlaybackOptions.Capture(_options);
                captured.Priority = 0;
                captured.PlaySoundMode = null;
                captured.Validate(true);
                captured.ValidateClip(_clip);
                captured.AutoFade.Normalize(5d, captured.Loop, out var fadeIn, out var fadeOut);
                voice.SetVolumeScale(captured.Volume);
                voice.SetPitch(captured.Pitch);
                var stored = voice.Options;
                _lastSnapshot = captured;
                checksum += stored.Volume + stored.Pitch + defaults.MaxDistance +
                            (music.Loop && omitted.Loop ? 1d : 0d) + fadeIn + fadeOut;
                stored.Pitch = 3f;
            }
            _snapshotChecksum = checksum;
        }

        [UnityTest]
        public IEnumerator WarmedPlaybackEntryPointsReportCurrentThreadAllocationEvents()
        {
            // Warm instrumentation itself before testing its empty interval.
            Stopwatch.GetTimestamp();
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                recorder.Stop();
                AllocationEvents(recorder);
            }
            Measurement calibration;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                _calibrationProbe = new byte[4096];
                recorder.Stop();
                calibration = new Measurement { Valid = recorder.Valid, Events = AllocationEvents(recorder) };
            }
            Assert.IsTrue(calibration.Valid, "GC.Alloc recorder must be available on this PlayMode runner.");
            Assert.Greater(calibration.Events, 0, "A known byte[] allocation must be detected before accepting zero events.");
            Assert.AreEqual(4096, _calibrationProbe.Length);
            var empty = Measure(Path.SoundDefault, true);
            Assert.IsTrue(empty.Valid);
            Assert.AreEqual(0L, empty.Events, "The empty interval must not include harness allocations.");
            UnityEngine.Debug.Log("Audio options benchmark: calibration events=" + calibration.Events +
                                  "; empty events=" + empty.Events + "; requests/batch=" + Requests + "; units=events (not bytes).");
            TestContext.WriteLine("Audio options allocation baseline: Unity={0}; requests/batch={1}; rounds={2}; " +
                                  "calibration events={3}; empty events={4}; units=GC.Alloc event Count (not bytes).",
                Application.unityVersion, Requests, Rounds, calibration.Events, empty.Events);

            Assert.AreEqual(Requests, AudioKit.PrewarmSounds(Requests));
            // A full real burst warms active-list and per-clip dictionary capacity as well as sources.
            PrepareBatch(Path.SoundReused);
            RunBatch(Path.SoundReused);
            for (var i = 0; i < Requests; i++)
            {
                Assert.IsTrue(_handles[i].IsPlaying);
                Assert.IsTrue(_soundVoices.Add(_handles[i].Voice));
                Assert.IsTrue(_soundSources.Add(_handles[i].Voice.Source.GetInstanceID()));
            }
            VerifyBatch(Path.SoundReused, false);
            CleanupBatch();
            AudioKit.PlayMusic(_clip).Stop();
            AudioKit.PlayVoice(_clip).Stop();
            AudioKit.CrossFadeMusic(_clip, 10f);
            AudioKit.CrossFadeMusic(_clip, 10f);
            foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
            {
                if (!_soundSources.Contains(source.GetInstanceID()))
                {
                    _slotSources.Add(source.GetInstanceID());
                }
            }

            Assert.AreEqual(3, _slotSources.Count, "Warm both fixed Music sources and the fixed Voice source.");
            AudioKit.StopMusic();

            foreach (Path path in Enum.GetValues(typeof(Path)))
            {
                // First-use costs (component callback/list and Action pool growth) remain separate from steady playback.
                PrepareBatch(path);
                var first = Measure(path);
                VerifyBatch(path, false);
                if (IsAllocate(path))
                {
                    for (var i = 0; i < Requests; i++)
                    {
                        _pooledActions.Add(_actions[i]);
                    }
                }

                CleanupBatch();
                if (IsDedup(path))
                {
                    yield return null;
                }

                PrepareBatch(path);
                RunBatch(path);
                VerifyBatch(path, true);
                CleanupBatch();
                if (IsDedup(path))
                {
                    yield return null;
                }

                TestContext.WriteLine("{0}: preparatory batch events={1} (not steady-state).", path, first.Events);
                Assert.IsTrue(first.Valid);

                for (var round = 0; round < Rounds; round++)
                {
                    PrepareBatch(path);
                    var measurement = Measure(path);
                    // All NUnit, reflection, logging, cleanup and pool draining occur after recorder.Stop().
                    Assert.IsTrue(measurement.Valid);
                    VerifyBatch(path, true);
                    var stats = AudioKit.SoundStats;
                    UnityEngine.Debug.Log("Audio options benchmark: " + path + "; round=" + (round + 1) +
                                          "; events=" + measurement.Events + "; us/request=" +
                                          (measurement.Ticks * 1000000d / Stopwatch.Frequency / Requests).ToString("F3"));
                    TestContext.WriteLine("{0}: round={1}; requests={2}; allocation events={3}; events/request={4:F3}; " +
                                          "elapsed us/request={5:F3}; started={6}; emitter started={7}; " +
                                          "admitted={8}; dedup rejected={9}; global rejected={10}; clip rejected={11}; " +
                                          "cache hits={12}; cache misses={13}; source creations={14}.",
                        path, round + 1, Requests, measurement.Events, (double)measurement.Events / Requests,
                        measurement.Ticks * 1000000d / Stopwatch.Frequency / Requests, _starts, _emitterStarts,
                        stats.AdmittedCount, stats.DedupRejectedCount, stats.GlobalRejectedCount,
                        stats.ClipRejectedCount, stats.CacheHitCount, stats.CacheMissCount, stats.SourceCreatedCount);
                    if (IsCrossfade(path))
                    {
                        Assert.AreEqual(0, measurement.Events, "Prewarmed Crossfade must not allocate enum boxes in the API or component.");
                    }

                    CleanupBatch();
                    if (IsDedup(path))
                    {
                        yield return null;
                    }
                }
            }
            TestContext.WriteLine("Action Start includes the production completion/Started closures. " +
                                  "Only the calibrated prewarmed Crossfade paths assert zero events; other paths have no blanket zero-GC or timing threshold assertion.");
            yield return null;
        }

        private static long AllocationEvents(ProfilerRecorder recorder) =>
            recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;

        private Measurement Measure(Path path, bool empty = false)
        {
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                var start = Stopwatch.GetTimestamp();
                if (!empty)
                {
                    RunBatch(path);
                }

                var elapsed = Stopwatch.GetTimestamp() - start;
                recorder.Stop();
                return new Measurement { Valid = recorder.Valid, Events = AllocationEvents(recorder), Ticks = elapsed };
            }
        }

        private static bool IsAllocate(Path path) => path == Path.ActionAllocateDefault || path == Path.ActionAllocateReused;
        private static bool IsStart(Path path) => path == Path.ActionStartDefault || path == Path.ActionStartReused;
        private static bool IsRejected(Path path) => path >= Path.ChannelClosedDefault;
        private static bool IsDedup(Path path) => path == Path.DedupDefault || path == Path.DedupReused;
        private static bool IsSound(Path path) => path == Path.SoundDefault || path == Path.SoundReused ||
                                                path == Path.Emitter || IsStart(path);
        private static bool IsCrossfade(Path path) => path == Path.CrossfadeDefault || path == Path.CrossfadeReused ||
                                                    path == Path.MusicPlayerCrossfade;

        private void PrepareBatch(Path path)
        {
            AudioKit.Settings.IsSoundOn.Value = true;
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = Requests };
            SetField(_musicPlayer, "crossFadeOnPlay", path == Path.MusicPlayerCrossfade);
            Array.Clear(_handles, 0, _handles.Length);
            if (path == Path.ChannelClosedDefault || path == Path.ChannelClosedReused)
            {
                AudioKit.Settings.IsSoundOn.Value = false;
            }
            else if (IsDedup(path))
            {
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
                // Stop retains the dedup record; advance a frame outside recording between same-frame batches.
                AudioKit.SoundFrameCountForIgnoreSameSound = 0;
                _blocker = AudioKit.PlaySound(_clip, _options);
                Assert.IsTrue(_blocker.IsPlaying);
            }
            else if (path == Path.GlobalBudgetDefault || path == Path.GlobalBudgetReused ||
                     path == Path.ClipBudgetDefault || path == Path.ClipBudgetReused)
            {
                var clipBudget = path == Path.ClipBudgetDefault || path == Path.ClipBudgetReused;
                AudioKit.SoundConcurrency = new AudioSoundConcurrency
                {
                    Limit = clipBudget ? Requests : 1,
                    DefaultClipLimit = clipBudget ? 1 : 0,
                    OverflowPolicy = SoundOverflowPolicy.RejectNew
                };
                _blocker = AudioKit.PlaySound(_clip, _options);
                Assert.IsTrue(_blocker.IsPlaying);
            }
            if (IsStart(path))
            {
                for (var i = 0; i < Requests; i++)
                {
                    _actions[i] = path == Path.ActionStartDefault
                        ? PlaySoundAction.Allocate(_clip, onStarted: _started)
                        : PlaySoundAction.AllocateWithOptions(_clip, _options, onStarted: _started);
                }
            }

            _starts = _emitterStarts = 0;
            _hasInvalidCallback = false;
            AudioKit.ResetSoundStats();
        }

        private void RunBatch(Path path)
        {
            // No harness-created delegates, containers, assertions, reflection, logs, timers or yields here.
            for (var i = 0; i < Requests; i++)
            {
                switch (path)
                {
                    case Path.SoundDefault:
                    case Path.ChannelClosedDefault:
                    case Path.DedupDefault:
                    case Path.GlobalBudgetDefault:
                    case Path.ClipBudgetDefault:
                        _handles[i] = AudioKit.PlaySound(_clip, onStarted: _started);
                        break;
                    case Path.SoundReused:
                    case Path.ChannelClosedReused:
                    case Path.DedupReused:
                    case Path.GlobalBudgetReused:
                    case Path.ClipBudgetReused:
                        _handles[i] = AudioKit.PlaySound(_clip, _options, onStarted: _started);
                        break;
                    case Path.MusicSimple:
                        _handles[i] = AudioKit.PlayMusic(_clip, loop: false, onStarted: _started);
                        break;
                    case Path.MusicNull:
                        _handles[i] = AudioKit.PlayMusicWithOptions(_clip, null, onStarted: _started);
                        break;
                    case Path.MusicReused:
                        _handles[i] = AudioKit.PlayMusicWithOptions(_clip, _options, onStarted: _started);
                        break;
                    case Path.VoiceSimple:
                        _handles[i] = AudioKit.PlayVoice(_clip, onStarted: _started);
                        break;
                    case Path.VoiceNull:
                        _handles[i] = AudioKit.PlayVoiceWithOptions(_clip, null, onStarted: _started);
                        break;
                    case Path.VoiceReused:
                        _handles[i] = AudioKit.PlayVoiceWithOptions(_clip, _options, onStarted: _started);
                        break;
                    case Path.CrossfadeDefault:
                        _handles[i] = AudioKit.CrossFadeMusic(_clip, 10f, onStarted: _started);
                        break;
                    case Path.CrossfadeReused:
                        _handles[i] = AudioKit.CrossFadeMusic(_clip, 10f, _options, onStarted: _started);
                        break;
                    case Path.Emitter:
                        _emitter.Play();
                        break;
                    case Path.MusicPlayer:
                    case Path.MusicPlayerCrossfade:
                        _musicPlayer.Play();
                        _handles[i] = AudioKit.Music;
                        break;
                    case Path.ActionAllocateDefault:
                        _actions[i] = PlaySoundAction.Allocate(_clip, onStarted: _started);
                        break;
                    case Path.ActionAllocateReused:
                        _actions[i] = PlaySoundAction.AllocateWithOptions(_clip, _options, onStarted: _started);
                        break;
                    case Path.ActionStartDefault:
                    case Path.ActionStartReused:
                        _actions[i].Execute(0f);
                        break;
                }
            }
        }

        private void VerifyBatch(Path path, bool checkActionReuse)
        {
            Assert.IsFalse(_hasInvalidCallback, "Started must see the accepted, playing clip.");
            var stats = AudioKit.SoundStats;
            Assert.AreEqual(0L, stats.SourceCreatedCount, "Only warmed sources may be used.");
            Assert.AreEqual(0L, stats.SourceReleasedCount);
            Assert.AreEqual(0L, stats.CacheMissCount);
            Assert.AreEqual(0L, stats.StartFailureCount);
            Assert.AreEqual(0L, stats.StolenCount);
            Assert.AreEqual(Requests, stats.OwnedSourceCount);
            if (IsAllocate(path))
            {
                Assert.AreEqual(0, _starts);
                Assert.AreEqual(0, AudioKit.SoundCount);
                var unique = new HashSet<PlaySoundAction>();
                for (var i = 0; i < Requests; i++)
                {
                    Assert.IsNotNull(_actions[i]);
                    Assert.IsFalse(_actions[i].IsDeinitialized);
                    Assert.AreEqual(ActionStatus.NotStart, _actions[i].Status);
                    Assert.IsTrue(unique.Add(_actions[i]));
                    if (checkActionReuse)
                    {
                        Assert.IsTrue(_pooledActions.Contains(_actions[i]), "Allocate must reuse the warmed Action pool.");
                    }
                }
                return;
            }
            if (IsRejected(path))
            {
                Assert.AreEqual(0, _starts);
                for (var i = 0; i < Requests; i++)
                {
                    Assert.IsFalse(_handles[i].IsValid);
                }

                var closed = path == Path.ChannelClosedDefault || path == Path.ChannelClosedReused;
                Assert.AreEqual(closed ? 0 : 1, AudioKit.SoundCount);
                Assert.AreEqual(0L, stats.AdmittedCount);
                Assert.AreEqual(0L, stats.CacheHitCount);
                Assert.AreEqual(path == Path.DedupDefault || path == Path.DedupReused ? Requests : 0L, stats.DedupRejectedCount);
                Assert.AreEqual(path == Path.GlobalBudgetDefault || path == Path.GlobalBudgetReused ? Requests : 0L, stats.GlobalRejectedCount);
                Assert.AreEqual(path == Path.ClipBudgetDefault || path == Path.ClipBudgetReused ? Requests : 0L, stats.ClipRejectedCount);
                if (!closed)
                {
                    Assert.IsTrue(_blocker.IsPlaying);
                }

                return;
            }
            if (path == Path.Emitter)
            {
                Assert.AreEqual(Requests, _emitterStarts);
                var sounds = AudioKit.GetPlayingSounds();
                Assert.AreEqual(Requests, sounds.Length);
                Array.Copy(sounds, _handles, Requests);
            }
            else if (path != Path.MusicPlayer && path != Path.MusicPlayerCrossfade)
            {
                Assert.AreEqual(Requests, _starts);
            }

            if (IsSound(path))
            {
                Assert.AreEqual(Requests, stats.AdmittedCount);
                Assert.AreEqual(Requests, stats.CacheHitCount);
                Assert.AreEqual(Requests, AudioKit.GetSoundCount(_clip));
                var unique = new HashSet<AudioVoice>();
                for (var i = 0; i < Requests; i++)
                {
                    Assert.IsTrue(_handles[i].IsPlaying);
                    Assert.AreSame(_clip, _handles[i].Voice.Source.clip);
                    Assert.IsTrue(unique.Add(_handles[i].Voice));
                    Assert.IsTrue(_soundVoices.Contains(_handles[i].Voice));
                    Assert.IsTrue(_soundSources.Contains(_handles[i].Voice.Source.GetInstanceID()));
                    if (IsStart(path))
                    {
                        Assert.AreEqual(ActionStatus.Started, _actions[i].Status);
                    }
                }
            }
            else
            {
                Assert.AreEqual(0, AudioKit.SoundCount);
                Assert.AreEqual(0L, stats.AdmittedCount);
                for (var i = 0; i < Requests; i++)
                {
                    Assert.IsTrue(_slotSources.Contains(_handles[i].Voice.Source.GetInstanceID()));
                }

                Assert.IsTrue(_handles[Requests - 1].IsPlaying);
                Assert.AreSame(_clip, _handles[Requests - 1].Voice.Source.clip);
                for (var i = 0; i < Requests - (IsCrossfade(path) ? 2 : 1); i++)
                {
                    Assert.IsFalse(_handles[i].IsValid, "Replaced slot leases must be invalid.");
                }

                if (IsCrossfade(path))
                {
                    Assert.IsTrue(_handles[Requests - 2].IsPlaying, "Crossfade must retain a real outgoing participant.");
                    Assert.AreNotSame(_handles[Requests - 2].Voice.Source, _handles[Requests - 1].Voice.Source);
                    Assert.AreEqual(0f, _handles[Requests - 1].Voice.Source.volume, .001f);
                }
            }
        }

        private void CleanupBatch()
        {
            for (var i = 0; i < Requests; i++)
            {
                if (_actions[i] != null && !_actions[i].IsDeinitialized)
                {
                    _actions[i].Deinit();
                }

                _actions[i] = null;
            }
            ActionQueue.Drain();
            if (_emitter)
            {
                _emitter.Stop();
            }

            if (_musicPlayer)
            {
                _musicPlayer.Stop();
            }

            AudioKit.StopAllSound();
            AudioKit.StopMusic();
            AudioKit.StopVoice();
            _blocker = default;
            AudioKit.Settings.IsSoundOn.Value = true;
        }
    }
}
