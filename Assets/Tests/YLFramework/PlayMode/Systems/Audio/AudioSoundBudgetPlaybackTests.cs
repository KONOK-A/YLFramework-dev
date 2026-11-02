using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace YLFramework.Tests.Audio
{
    public class AudioSoundBudgetPlaybackTests
    {
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private readonly List<AudioClip> _burstClips = new List<AudioClip>();
        private AudioClip _clip;
        private GameObject _listener;
        private Random.State _randomState;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _randomState = Random.state;
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
            ConfigureSettings();
            _clip = AudioClip.Create("budget-playback", 220500, 1, 44100, false);
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
                _listener = new GameObject("BudgetPlayback.Listener");
                _listener.AddComponent<AudioListener>();
            }
            yield return null;
        }

        private static void ConfigureSettings()
        {
            var settings = AudioKit.Settings;
            settings.IsSoundOn.SetValueWithoutCallback(true);
            settings.IsMusicOn.SetValueWithoutCallback(true);
            settings.IsVoiceOn.SetValueWithoutCallback(true);
            settings.SoundVolume.SetValueWithoutCallback(1f);
            settings.MusicVolume.SetValueWithoutCallback(1f);
            settings.VoiceVolume.SetValueWithoutCallback(1f);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            if (_clip)
            {
                Object.Destroy(_clip);
            }

            foreach (var clip in _burstClips)
            {
                if (clip)
                {
                    Object.Destroy(clip);
                }
            }

            _burstClips.Clear();
            if (_listener)
            {
                Object.Destroy(_listener);
            }

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

        private static AudioSoundOptions Loop(int priority = 0)
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            options.Priority = priority;
            return options;
        }

        [UnityTest]
        public IEnumerator BothDedupModesKeepZeroAndTenFrameBoundariesAcrossGlobalClipAndJointBudgets()
        {
            foreach (var mode in new[] { AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames, AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames })
            {
                foreach (var frames in new[] { 0, 10 })
                {
                    for (var scope = 0; scope < 3; scope++)
                    {
                        AudioKit.Shutdown();
                        ConfigureSettings();
                        AudioKit.GlobalFrameCountForIgnoreSameSound = frames;
                        AudioKit.SoundFrameCountForIgnoreSameSound = frames;
                        AudioKit.PlaySoundMode = mode;
                        AudioKit.SoundConcurrency = new AudioSoundConcurrency
                        {
                            Limit = scope == 1 ? 0 : 1,
                            DefaultClipLimit = scope == 0 ? 0 : 1,
                            OverflowPolicy = SoundOverflowPolicy.RejectNew
                        };
                        var first = AudioKit.PlaySound(_clip, Loop(10));
                        first.Pause();
                        AudioKit.Settings.IsSoundMuted.Value = true;
                        var startFrame = Time.frameCount;
                        var checks = 0;
                        while (Time.frameCount - startFrame <= frames)
                        {
                            Assert.IsFalse(AudioKit.PlaySound(_clip, Loop(int.MaxValue)).IsValid);
                            checks++;
                            Assert.IsTrue(first.IsPaused);
                            Assert.AreEqual(1, AudioKit.GetSoundCount(_clip));
                            Assert.AreEqual(0L, AudioKit.SoundStats.GlobalRejectedCount);
                            Assert.AreEqual(0L, AudioKit.SoundStats.ClipRejectedCount);
                            yield return null;
                        }
                        Assert.AreEqual(checks, AudioKit.SoundStats.DedupRejectedCount);
                        Assert.IsFalse(AudioKit.PlaySound(_clip, Loop(int.MaxValue)).IsValid, "Passing dedup does not bypass active capacity.");
                        Assert.AreEqual(scope == 0 ? 1L : 0L, AudioKit.SoundStats.GlobalRejectedCount);
                        Assert.AreEqual(scope == 0 ? 0L : 1L, AudioKit.SoundStats.ClipRejectedCount);
                        Assert.IsTrue(AudioKit.Channels.CanPlay(_clip.GetInstanceID(), mode, Time.frameCount));
                        first.Stop();
                        var retry = AudioKit.PlaySound(_clip, Loop(10));
                        Assert.IsTrue(retry.IsValid, "Capacity rejection cannot commit or extend a dedup window.");
                        AudioKit.SoundConcurrency = new AudioSoundConcurrency
                        {
                            Limit = scope == 1 ? 0 : 1,
                            DefaultClipLimit = scope == 0 ? 0 : 1,
                            OverflowPolicy = SoundOverflowPolicy.StopLowestPriority
                        };
                        Assert.IsFalse(AudioKit.PlaySound(_clip, Loop(int.MaxValue)).IsValid);
                        Assert.IsTrue(retry.IsValid, "Higher priority still cannot steal through same-frame dedup.");
                        Assert.AreEqual(0L, AudioKit.SoundStats.StolenCount);
                        Assert.AreEqual(0L, AudioKit.SoundStats.PriorityRejectedCount);
                        retry.Stop();
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator EveryOneOverridesOnlyDedupAndNeverEitherCapacityBudget()
        {
            foreach (var mode in new[] { AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames, AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames })
            {
                AudioKit.Shutdown();
                ConfigureSettings();
                AudioKit.PlaySoundMode = mode;
                AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = 2, DefaultClipLimit = 2 };
                var first = AudioKit.PlaySound(_clip, Loop());
                var options = Loop(int.MaxValue);
                options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
                var second = AudioKit.PlaySound(_clip, options);
                Assert.IsTrue(first.IsValid);
                Assert.IsTrue(second.IsValid);
                Assert.IsFalse(AudioKit.PlaySound(_clip, options).IsValid);
                Assert.AreEqual(2, AudioKit.GetSoundCount(_clip));
                Assert.AreEqual(1L, AudioKit.SoundStats.ClipRejectedCount);
                Assert.AreEqual(0L, AudioKit.SoundStats.GlobalRejectedCount, "Joint rejection has exactly one clip-first reason.");
                first.Stop();
                Assert.IsTrue(AudioKit.PlaySound(_clip, options).IsValid);
                Assert.IsFalse(AudioKit.PlaySound(_clip, Loop(int.MaxValue)).IsValid, "EveryOne does not clear an earlier committed record.");
                Assert.AreEqual(1L, AudioKit.SoundStats.DedupRejectedCount);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator DisableCallbackChangingCapacityToZeroCannotCacheTheReturningSource()
        {
            AudioKit.SoundCacheCapacity = 2;
            var handle = AudioKit.PlaySound(_clip, Loop());
            var sourceRoot = handle.Voice.Source.gameObject;
            var relay = sourceRoot.AddComponent<AudioBudgetStopOnDisable>();
            relay.disableCache = true;
            handle.Stop();
            Assert.That(relay.calls, Is.EqualTo(1));
            Assert.That(AudioKit.SoundCacheCapacity, Is.Zero);
            Assert.That(AudioKit.SoundStats.CachedCount, Is.Zero);
            Assert.That(AudioKit.SoundStats.OwnedSourceCount, Is.Zero);
            Assert.That(AudioKit.SoundStats.SourceReleasedCount, Is.EqualTo(1));
            handle.Stop();
            Assert.That(AudioKit.SoundStats.SourceReleasedCount, Is.EqualTo(1));
            yield return null;
            Assert.That(sourceRoot == null, Is.True);
        }

        [UnityTest]
        public IEnumerator StealingRefreshesClipCountAfterSourceDisableStopsAnotherRequest()
        {
            foreach (var clipLimit in new[] { 0, 2 })
            {
                AudioKit.SoundConcurrency = new AudioSoundConcurrency
                {
                    Limit = 2,
                    DefaultClipLimit = clipLimit,
                    OverflowPolicy = SoundOverflowPolicy.StopOldest
                };
                var first = AudioKit.PlaySound(_clip, Loop());
                var second = AudioKit.PlaySound(_clip, Loop());
                var relay = first.Voice.Source.gameObject.AddComponent<AudioBudgetStopOnDisable>();
                relay.Other = second;
                var replacement = AudioKit.PlaySound(_clip, Loop());
                Assert.AreEqual(1, relay.calls);
                Assert.IsFalse(first.IsValid);
                Assert.IsFalse(second.IsValid);
                Assert.IsTrue(replacement.IsPlaying);
                Assert.AreEqual(1, AudioKit.SoundCount);
                Assert.AreEqual(1, AudioKit.GetSoundCount(_clip));
                replacement.Stop();
                Assert.AreEqual(0, AudioKit.SoundCount);
                Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
                Object.Destroy(relay);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator PrewarmedTwentyFourSourceBurstStopsAndReusesAllVoicesAndSourcesInTheNextBurst()
        {
            AudioKit.SoundCacheCapacity = 24;
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = 24 };
            var clips = new AudioClip[24];
            clips[0] = _clip;
            for (var i = 1; i < clips.Length; i++)
            {
                clips[i] = AudioClip.Create("budget-burst-" + i, 44100, 1, 44100, false);
                _burstClips.Add(clips[i]);
            }
            var random = Random.state;
            const ProfilerRecorderOptions Recording = ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
            long calibrationAllocations, prewarmAllocations, firstBurstAllocations, steadyBurstAllocations;
            bool validRecorder;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                validRecorder = recorder.Valid;
                var probe = new byte[4096];
                GC.KeepAlive(probe);
                recorder.Stop();
                calibrationAllocations = recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
            }
            Assert.IsTrue(validRecorder);
            Assert.Greater(calibrationAllocations, 0, "Verify the allocation recorder with a known allocation before accepting zero samples.");
            int added;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                added = AudioKit.PrewarmSounds(24);
                recorder.Stop();
                prewarmAllocations = recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
            }
            Assert.AreEqual(24, added);
            Assert.AreEqual(random, Random.state);
            var handles = new AudioHandle[24];
            var voices = new HashSet<AudioVoice>();
            var sourceIds = new HashSet<int>();
            foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
            {
                Assert.IsFalse(source.gameObject.activeSelf);
                Assert.IsNull(source.clip);
                Assert.IsFalse(source.isPlaying);
                Assert.IsFalse(source.playOnAwake);
                sourceIds.Add(source.GetInstanceID());
            }
            Assert.AreEqual(24, sourceIds.Count);
            var options = Loop();
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                for (var i = 0; i < handles.Length; i++)
                {
                    handles[i] = AudioKit.PlaySound(clips[i], options);
                }

                recorder.Stop();
                firstBurstAllocations = recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
            }
            foreach (var handle in handles)
            {
                Assert.IsTrue(handle.IsPlaying);
                Assert.IsTrue(voices.Add(handle.Voice));
                Assert.IsTrue(sourceIds.Contains(handle.Voice.Source.GetInstanceID()));
            }
            Assert.AreEqual(24, AudioKit.SoundStats.ActiveCount);
            foreach (var clip in clips)
            {
                Assert.AreEqual(1, AudioKit.GetSoundCount(clip));
            }

            Assert.AreEqual(24, AudioKit.SoundStats.PeakActiveCount);
            Assert.AreEqual(0, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(24L, AudioKit.SoundStats.CacheHitCount);
            Assert.AreEqual(0L, AudioKit.SoundStats.CacheMissCount);
            Assert.AreEqual(24L, AudioKit.SoundStats.SourceCreatedCount);
            yield return null;
            AudioKit.StopAllSound();
            foreach (var handle in handles)
            {
                Assert.IsFalse(handle.IsValid);
            }

            Assert.AreEqual(24, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(24, AudioKit.SoundStats.OwnedSourceCount);
            foreach (var clip in clips)
            {
                Assert.AreEqual(0, AudioKit.GetSoundCount(clip));
            }

            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1, Recording))
            {
                for (var i = 0; i < handles.Length; i++)
                {
                    handles[i] = AudioKit.PlaySound(clips[i], options);
                }

                recorder.Stop();
                steadyBurstAllocations = recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
            }
            var reusedVoices = new HashSet<AudioVoice>();
            var reusedSources = new HashSet<int>();
            foreach (var handle in handles)
            {
                Assert.IsTrue(handle.IsPlaying);
                reusedVoices.Add(handle.Voice);
                reusedSources.Add(handle.Voice.Source.GetInstanceID());
            }
            CollectionAssert.AreEquivalent(voices, reusedVoices);
            CollectionAssert.AreEquivalent(sourceIds, reusedSources);
            Assert.AreEqual(48L, AudioKit.SoundStats.AdmittedCount);
            Assert.AreEqual(48L, AudioKit.SoundStats.CacheHitCount);
            Assert.AreEqual(0L, AudioKit.SoundStats.CacheMissCount);
            Assert.AreEqual(24L, AudioKit.SoundStats.SourceCreatedCount);
            Assert.AreEqual(0L, AudioKit.SoundStats.SourceReleasedCount);
            TestContext.WriteLine("Current-thread GC.Alloc samples: calibration={0}, prewarm={1}, first 24-request burst={2}, steady 24-request burst={3}.",
                calibrationAllocations, prewarmAllocations, firstBurstAllocations, steadyBurstAllocations);
            Debug.Log("Audio budget benchmark (current-thread GC.Alloc samples): calibration=" + calibrationAllocations +
                "; prewarm (sources + containers)=" + prewarmAllocations + "; first 24-request burst=" + firstBurstAllocations +
                "; steady 24-request burst=" + steadyBurstAllocations + ".");
            yield return null;
            foreach (var handle in handles)
            {
                Assert.IsTrue(handle.IsPlaying);
            }
        }

        [UnityTest]
        public IEnumerator CacheConfigurationDoesNotInitializeAnAdoptedSceneHostBeforeExplicitPrewarm()
        {
            var root = new GameObject("BudgetPlayback.AdoptedHost");
            try
            {
                var host = root.AddComponent<AudioManager>();
                AudioKit.SoundCacheCapacity = 0;
                AudioKit.SoundCacheCapacity = 2;
                Assert.AreSame(host, AudioManager.TryGetInstance(out var cached) ? cached : null);
                Assert.IsNull(host.MusicPlayback, "Cache configuration must not initialize a lazily adopted scene host.");
                Assert.AreEqual(default(AudioSoundStats), AudioKit.SoundStats);
                Assert.AreEqual(2, AudioKit.PrewarmSounds(2));
                Assert.AreSame(host, AudioManager.Instance);
                Assert.AreEqual(2, AudioKit.SoundStats.OwnedSourceCount);
                Assert.AreEqual(0, AudioKit.SoundCount);
                AudioKit.Shutdown();
                yield return null;
                Assert.IsTrue(root, "The borrowed host GameObject is not owned by AudioKit.");
                Assert.AreEqual(0, root.transform.childCount);
            }
            finally
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }
        }

        [UnityTest]
        public IEnumerator LiveCacheShrinkReleasesOnlyIdleOwnershipThenDestroysSourcesAndTheirGameObjects()
        {
            AudioKit.PrewarmSounds(10);
            var roots = new List<GameObject>();
            foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
            {
                roots.Add(source.gameObject);
            }

            var playing = AudioKit.PlaySound(_clip, Loop());
            AudioKit.SoundCacheCapacity = 2;
            Assert.IsTrue(playing.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
            Assert.AreEqual(2, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(3, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(7L, AudioKit.SoundStats.SourceReleasedCount, "Release counts requests; native destruction may be deferred.");
            yield return null;
            var alive = 0;
            foreach (var root in roots)
            {
                if (root)
                {
                    alive++;
                }
            }

            Assert.AreEqual(3, alive);
            AudioKit.SoundCacheCapacity = 0;
            Assert.IsTrue(playing.IsValid);
            Assert.AreEqual(1, AudioKit.SoundStats.OwnedSourceCount);
            playing.Stop();
            Assert.AreEqual(0, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(0, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(10L, AudioKit.SoundStats.SourceReleasedCount);
            yield return null;
            foreach (var root in roots)
            {
                Assert.IsFalse(root, "Released Sound sources must not leave orphan roots.");
            }

            Assert.AreEqual(0, AudioManager.Instance.GetComponentsInChildren<AudioSource>(true).Length);
        }
    }
}
