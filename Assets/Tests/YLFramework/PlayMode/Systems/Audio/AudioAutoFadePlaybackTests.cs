using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Audio;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Audio;
#endif
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
using Kit = YLFramework.Actions.ActionKit;

namespace YLFramework.Tests.Audio
{
    public class AudioAutoFadePlaybackTests
    {
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<IAction> _actions = new List<IAction>();
        private AudioClip _clip;
        private AudioClip _other;
        private AudioClip _short;
        private Random.State _randomState;
        private float _timeScale;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _randomState = Random.state;
            _timeScale = Time.timeScale;
            Time.timeScale = 1f;
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
            _clip = AudioClip.Create("auto-fade-ten-seconds", 441000, 1, 44100, false);
            _other = AudioClip.Create("auto-fade-four-seconds", 88200, 2, 22050, false);
            _short = AudioClip.Create("auto-fade-natural", 17640, 1, 44100, false);
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
                OwnObject("AutoFade.Listener").AddComponent<AudioListener>();
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
            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }

            _actions.Clear();
            Kit.Shutdown();
            AudioKit.Shutdown();
            Time.timeScale = _timeScale;
            foreach (var root in _objects)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }

            _objects.Clear();
            Object.Destroy(_clip);
            Object.Destroy(_other);
            Object.Destroy(_short);
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

        private GameObject OwnObject(string name)
        {
            var root = new GameObject(name);
            _objects.Add(root);
            return root;
        }

        private T OwnAction<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        private static AudioSoundOptions Options(float fadeIn = 2f, float fadeOut = 3f, bool loop = false)
        {
            var options = AudioSoundOptions.Defaults;
            options.AutoFade = new AudioAutoFade(fadeIn, fadeOut);
            options.Loop = loop;
            options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            return options;
        }

        private AudioHandle PlayEntry(int entry, AudioSoundOptions options, AudioClip clip, Transform target)
        {
            switch (entry)
            {
                case 0:
                    return AudioKit.PlaySound(clip, options);
                case 1:
                    return AudioKit.PlaySoundAt(clip, Vector3.one, options);
                case 2:
                    return AudioKit.PlaySoundFollowing(clip, target, options);
                case 3:
                    return AudioKit.PlayVoiceAt(clip, Vector3.one, options);
                case 4:
                    return AudioKit.PlayVoiceFollowing(clip, target, options);
                case 5:
                    return AudioKit.PlayRandomSound(new[] { clip }, options);
                case 6:
                    return AudioKit.PlayWeightedRandomSound(new[] { new AudioWeightedClip(clip) }, options);
                case 7:
                    return AudioKit.PlayMusicWithOptions(clip, options);
                default:
                    return AudioKit.PlayVoiceWithOptions(clip, options);
            }
        }

        private static void Seek(AudioHandle handle, double seconds, bool tick = true)
        {
            Assert.IsTrue(handle.IsValid);
            handle.Pause();
            var source = handle.Voice.Source;
            source.timeSamples = (int)(seconds * source.clip.frequency);
            handle.Resume();
            Assert.IsTrue(source.isPlaying, "Resume must preserve the real playback request.");
            Assert.AreEqual(seconds, source.timeSamples / (double)source.clip.frequency, .025d);
            if (tick)
            {
                handle.Voice.TickFade(0f, 0f);
            }
        }

        private static void AssertVolume(AudioHandle handle, float expected, string message = null)
        {
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(expected, handle.Voice.Source.volume, .01f, message);
        }

        private static void AssertMatchesSamples(AudioHandle handle, float baseVolume = 1f)
        {
            var source = handle.Voice.Source;
            var length = source.clip.samples / (double)source.clip.frequency;
            handle.Voice.Options.AutoFade.Normalize(length, false, out var a, out var b);
            var expected = AudioAutoFade.Evaluate(source.timeSamples / (double)source.clip.frequency, length, a, b);
            AssertVolume(handle, Mathf.Clamp01(baseVolume) * expected);
        }

        private static IEnumerator WaitForEnd(AudioHandle handle)
        {
            var deadline = Time.realtimeSinceStartup + 3f;
            while (handle.IsValid && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsFalse(handle.IsValid, "The source must finish naturally within the real-time deadline.");
        }

        private static void SetEnabled(bool music, bool enabled)
        {
            if (music)
            {
                AudioKit.Settings.IsMusicOn.Value = enabled;
            }
            else
            {
                AudioKit.Settings.IsVoiceOn.Value = enabled;
            }
        }

        private static void SetField(object component, string name, object value)
        {
            var field = component.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, name);
            field.SetValue(component, value);
        }

        [UnityTest]
        public IEnumerator AllExistingOptionsEntrypointsAndNewSlotsReadPcmPositionInsteadOfDeltaTime()
        {
            var target = OwnObject("AutoFade.Target").transform;
            for (var entry = 0; entry < 9; entry++)
            {
                var options = Options();
                options.Volume = .8f;
                options.Pitch = 2f;
                var handle = PlayEntry(entry, options, _clip, target);
                AssertVolume(handle, 0f);
                Assert.AreEqual(2f, handle.Voice.Source.pitch);
                Assert.AreEqual(entry >= 1 && entry <= 4 ? 1f : 0f, handle.Voice.Source.spatialBlend);
                Assert.IsFalse(handle.Voice.Source.loop);
                Seek(handle, 1d);
                AssertVolume(handle, .4f);
                handle.Voice.TickFade(1000f, 1000f);
                AssertVolume(handle, .4f);
                Seek(handle, 5d);
                AssertVolume(handle, .8f);
                Seek(handle, 8.5d);
                AssertVolume(handle, .4f);
                Assert.IsTrue(handle.IsPlaying, "The envelope is not a stop timer.");
                handle.Stop();
            }
            foreach (var handle in new[] { AudioKit.PlaySound(_clip), AudioKit.PlayMusic(_clip), AudioKit.PlayVoice(_clip) })
            {
                Assert.AreEqual(default(AudioAutoFade), handle.Voice.Options.AutoFade);
                AssertVolume(handle, 1f);
                handle.Voice.TickFade(1000f, 1000f);
                AssertVolume(handle, 1f);
                handle.Stop();
            }
            var differentFrequency = AudioKit.PlaySound(_other, Options(8f, 12f));
            Seek(differentFrequency, .8d);
            AssertVolume(differentFrequency, .5f);
            Seek(differentFrequency, 1.6d);
            AssertVolume(differentFrequency, 1f);
            Seek(differentFrequency, 2.8d);
            AssertVolume(differentFrequency, .5f);
            differentFrequency.Stop();
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartCallbacksSeeCompleteOptionsAndInitialGainBeforeAnyAutomaticTick()
        {
            foreach (var music in new[] { false, true })
            {
                foreach (var fadeIn in new[] { 0f, 2f })
                {
                    var calls = 0;
                    var options = Options(fadeIn, 3f);
                    options.Volume = .6f;
                    options.Pitch = 1.5f;
                    options.OutputRoute = AudioOutputRoute.Direct;
                    Action<AudioClip, AudioHandle> started = (clip, lease) =>
                    {
                        calls++;
                        Assert.AreSame(_clip, clip);
                        Assert.IsTrue(lease.IsValid);
                        Assert.AreSame(clip, lease.Voice.Source.clip);
                        Assert.AreEqual(fadeIn > 0f ? 0f : .6f, lease.Voice.Source.volume, .0001f);
                        Assert.AreEqual(1.5f, lease.Voice.Source.pitch);
                        Assert.IsNull(lease.Voice.Source.outputAudioMixerGroup);
                        Assert.AreEqual(0f, lease.Voice.Source.spatialBlend);
                        Assert.IsFalse(lease.Voice.Source.loop);
                    };
                    var handle = music ? AudioKit.PlayMusicWithOptions(_clip, options, started) : AudioKit.PlayVoiceWithOptions(_clip, options, started);
                    Assert.AreEqual(1, calls);
                    Assert.IsTrue(handle.IsPlaying);
                    handle.Stop();
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator RandomAndWeightedPlaybackChooseOnlyOnceAndCompressAgainstTheActualSelectedLength()
        {
            foreach (var weighted in new[] { false, true })
            {
                Random.InitState(weighted ? 927 : 518);
                var before = Random.state;
                AudioClip selected;
                if (weighted)
                {
                    Assert.IsTrue(AudioKit.TrySelectWeightedRandomClip(new[] { new AudioWeightedClip(_clip), new AudioWeightedClip(_other, 3f) }, out selected));
                }
                else
                {
                    Assert.IsTrue(AudioKit.TrySelectRandomClip(new[] { _clip, _other }, out selected));
                }

                var afterOne = Random.state;
                Random.state = before;
                var options = Options(8f, 12f);
                var handle = weighted
                    ? AudioKit.PlayWeightedRandomSound(new[] { new AudioWeightedClip(_clip), new AudioWeightedClip(_other, 3f) }, options)
                    : AudioKit.PlayRandomSound(new[] { _clip, _other }, options);
                Assert.AreEqual(afterOne, Random.state);
                Assert.AreSame(selected, handle.Voice.Source.clip);
                var length = selected.samples / (double)selected.frequency;
                Seek(handle, length * .2d);
                AssertVolume(handle, .5f);
                Seek(handle, length * .4d);
                AssertVolume(handle, 1f);
                Seek(handle, length * .7d);
                AssertVolume(handle, .5f);
                handle.Stop();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator RuntimePitchChangesPreserveGainAndAutomaticTickRunsAtZeroTimeScale()
        {
            Time.timeScale = 0f;
            var options = Options(4f, 3f);
            options.Pitch = .5f;
            var handle = AudioKit.PlaySound(_clip, options);
            Seek(handle, 1d);
            AssertVolume(handle, .25f);
            var before = handle.Voice.Source.timeSamples;
            yield return new WaitForSecondsRealtime(.12f);
            Assert.Greater(handle.Voice.Source.timeSamples, before);
            handle.Voice.TickFade(0f, 0f);
            AssertMatchesSamples(handle);
            var gain = handle.Voice.Source.volume;
            var generation = handle.Generation;
            handle.SetPitch(2f);
            Assert.AreEqual(gain, handle.Voice.Source.volume);
            Assert.AreEqual(generation, handle.Generation);
            Assert.AreEqual(2f, handle.Voice.Options.Pitch);
            before = handle.Voice.Source.timeSamples;
            yield return new WaitForSecondsRealtime(.12f);
            Assert.Greater(handle.Voice.Source.timeSamples, before);
            handle.Voice.TickFade(0f, 0f);
            AssertMatchesSamples(handle);
            Seek(handle, 8.5d);
            AssertVolume(handle, .5f);
        }

        [UnityTest]
        public IEnumerator PendingPauseResumeMuteAndChannelRestartHaveDistinctAutomaticSemantics()
        {
            foreach (var music in new[] { false, true })
            {
                SetEnabled(music, false);
                var calls = 0;
                var options = Options();
                var handle = music
                    ? AudioKit.PlayMusicWithOptions(_clip, options, (_, __) => calls++)
                    : AudioKit.PlayVoiceWithOptions(_clip, options, (_, __) => calls++);
                handle.Voice.TickFade(100f, 100f);
                yield return new WaitForSecondsRealtime(.05f);
                AssertVolume(handle, 0f);
                Assert.AreEqual(0, calls);
                SetEnabled(music, true);
                Assert.AreEqual(1, calls);
                AssertVolume(handle, 0f);
                Seek(handle, 1d);
                handle.Pause();
                var frozen = handle.Voice.Source.volume;
                handle.Voice.Source.timeSamples = (int)(1.5d * _clip.frequency);
                handle.Voice.TickFade(100f, 100f);
                yield return new WaitForSecondsRealtime(.05f);
                Assert.AreEqual(frozen, handle.Voice.Source.volume);
                Assert.IsTrue(handle.IsPaused);
                handle.Resume();
                handle.Voice.TickFade(0f, 0f);
                AssertVolume(handle, .75f);
                SetEnabled(music, false);
                var disabledGain = handle.Voice.Source.volume;
                handle.Voice.TickFade(100f, 100f);
                Assert.AreEqual(disabledGain, handle.Voice.Source.volume);
                SetEnabled(music, true);
                AssertVolume(handle, 0f);
                Assert.AreEqual(1, calls, "Channel restart is not another first-start notification.");
                if (music)
                {
                    AudioKit.Settings.IsMusicMuted.Value = true;
                }
                else
                {
                    AudioKit.Settings.IsVoiceMuted.Value = true;
                }

                Seek(handle, 1d);
                Assert.IsTrue(handle.Voice.Source.mute);
                AssertVolume(handle, .5f);
                if (music)
                {
                    AudioKit.Settings.IsMusicMuted.Value = false;
                }
                else
                {
                    AudioKit.Settings.IsVoiceMuted.Value = false;
                }

                AssertVolume(handle, .5f);
                handle.Stop();
            }
        }

        [UnityTest]
        public IEnumerator LoopFadeRunsOnlyOnItsFirstPassAndRealRestartResetsTheLatch()
        {
            foreach (var music in new[] { false, true })
            {
                var options = Options(20f, 0f, true);
                var handle = music ? AudioKit.PlayMusicWithOptions(_clip, options) : AudioKit.PlayVoiceWithOptions(_clip, options);
                Seek(handle, 9d);
                AssertVolume(handle, .9f);
                Seek(handle, 1d);
                AssertVolume(handle, 1f, "A first-pass sample wrap latches complete, instead of fading in every loop.");
                Seek(handle, .25d);
                AssertVolume(handle, 1f);
                SetEnabled(music, false);
                SetEnabled(music, true);
                AssertVolume(handle, 0f);
                Seek(handle, 2d);
                AssertVolume(handle, .2f);
                handle.Pause();
                var gain = handle.Voice.Source.volume;
                handle.Resume();
                Assert.AreEqual(gain, handle.Voice.Source.volume, "UnPause is not a new StartSource.");
                handle.Stop();
                options.AutoFade = new AudioAutoFade(2f, 0f);
                handle = music ? AudioKit.PlayMusicWithOptions(_clip, options) : AudioKit.PlayVoiceWithOptions(_clip, options);
                Seek(handle, 3d);
                AssertVolume(handle, 1f);
                Seek(handle, .25d);
                AssertVolume(handle, 1f, "Reaching fade-in duration also latches complete.");
                handle.Stop();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator AutomaticLoopObservesARealPcmWrapWithoutRestartingItsEnvelope()
        {
            var handle = AudioKit.PlayMusicWithOptions(_short, Options(1f, 0f, true));
            Seek(handle, .3d);
            AssertVolume(handle, .75f);
            var deadline = Time.realtimeSinceStartup + 1f;
            while (handle.Voice.Source.volume < .99f && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            AssertVolume(handle, 1f);
            yield return new WaitForSecondsRealtime(.15f);
            AssertVolume(handle, 1f);
            Assert.IsTrue(handle.IsPlaying);
        }

        [UnityTest]
        public IEnumerator ManualFadesRefreshAutomaticGainThenOwnTheSingleEnvelopeAcrossRestart()
        {
            foreach (var time in new[] { 1d, 5d, 8.5d })
            {
                foreach (var fadeIn in new[] { false, true })
                {
                    var handle = AudioKit.PlayMusicWithOptions(_clip, Options());
                    Seek(handle, time, false);
                    var expected = time == 5d ? 1f : .5f;
                    if (fadeIn)
                    {
                        handle.FadeIn(2f);
                    }
                    else
                    {
                        handle.FadeOut(2f, stopWhenDone: false);
                    }

                    AssertVolume(handle, expected);
                    handle.Voice.TickFade(1f, 1f);
                    var middle = fadeIn ? (expected + 1f) * .5f : expected * .5f;
                    AssertVolume(handle, middle);
                    Seek(handle, 9d);
                    AssertVolume(handle, middle, "The automatic tail must not multiply or resume after manual takeover.");
                    SetEnabled(true, false);
                    SetEnabled(true, true);
                    AssertVolume(handle, middle, "Options.AutoFade remains data, not permission to reactivate a canceled envelope.");
                    handle.Voice.TickFade(1f, 1f);
                    AssertVolume(handle, fadeIn ? 1f : 0f);
                    Assert.IsTrue(handle.IsPlaying);
                    handle.Stop();
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator InvalidManualDurationsNeverCancelAutomaticControlAndZeroDurationDoes()
        {
            var handle = AudioKit.PlayMusicWithOptions(_clip, Options());
            Seek(handle, 1d);
            foreach (var duration in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => handle.FadeIn(duration));
                Assert.Throws<ArgumentOutOfRangeException>(() => handle.FadeOut(duration));
            }
            Seek(handle, 1.5d);
            AssertVolume(handle, .75f);
            handle.FadeIn(0f);
            AssertVolume(handle, 1f);
            Seek(handle, 9d);
            AssertVolume(handle, 1f);
            handle.FadeOut(0f, stopWhenDone: false);
            AssertVolume(handle, 0f);
            SetEnabled(true, false);
            SetEnabled(true, true);
            Seek(handle, 5d);
            AssertVolume(handle, 0f);
            handle.FadeOut(0f);
            Assert.IsFalse(handle.IsValid);
            yield return null;
        }

        [UnityTest]
        public IEnumerator PausedAndDisabledManualTakeoverPreserveCachedAutomaticGain()
        {
            var handle = AudioKit.PlayMusicWithOptions(_clip, Options());
            Seek(handle, 1d);
            handle.Pause();
            var frozen = handle.Voice.Source.volume;
            handle.Voice.Source.timeSamples = 5 * _clip.frequency;
            handle.FadeIn(2f);
            Assert.AreEqual(frozen, handle.Voice.Source.volume);
            handle.Voice.TickFade(100f, 100f);
            Assert.AreEqual(frozen, handle.Voice.Source.volume);
            handle.Resume();
            handle.Voice.TickFade(1f, 1f);
            AssertVolume(handle, (frozen + 1f) * .5f);
            handle.Stop();
            handle = AudioKit.PlayMusicWithOptions(_clip, Options());
            Seek(handle, 1d);
            SetEnabled(true, false);
            frozen = handle.Voice.Source.volume;
            handle.FadeOut(2f, stopWhenDone: false);
            Assert.AreEqual(frozen, handle.Voice.Source.volume);
            handle.Voice.TickFade(100f, 100f);
            Assert.AreEqual(frozen, handle.Voice.Source.volume);
            SetEnabled(true, true);
            Assert.AreEqual(frozen, handle.Voice.Source.volume);
            handle.Voice.TickFade(1f, 1f);
            AssertVolume(handle, frozen * .5f);
            yield return null;
        }

        [UnityTest]
        public IEnumerator NaturalCompletionWritesTheEndpointBeforeOneCallbackWithTheOriginalLiveLease()
        {
            for (var category = 0; category < 3; category++)
            {
                foreach (var tail in new[] { 0f, .2f })
                {
                    var calls = 0;
                    var options = Options(.1f, tail);
                    AudioHandle observed = default;
                    Action<AudioClip, AudioHandle> finished = (clip, lease) =>
                    {
                        calls++;
                        observed = lease;
                        Assert.AreSame(_short, clip);
                        Assert.IsTrue(lease.IsValid);
                        Assert.AreSame(clip, lease.Voice.Source.clip);
                        Assert.AreEqual(tail > 0f ? 0f : 1f, lease.Voice.Source.volume, .0001f);
                        Assert.IsFalse(lease.Voice.Source.loop);
                    };
                    var handle = category == 0 ? AudioKit.PlaySound(_short, options, finished)
                        : category == 1 ? AudioKit.PlayMusicWithOptions(_short, options, onFinished: finished)
                        : AudioKit.PlayVoiceWithOptions(_short, options, onFinished: finished);
                    yield return WaitForEnd(handle);
                    Assert.AreEqual(handle, observed);
                    Assert.AreEqual(1, calls);
                    yield return null;
                    Assert.AreEqual(1, calls);
                }
            }
        }

        [UnityTest]
        public IEnumerator TrueNaturalCompletionReentryCannotTickTheReplacementUsingAnExpiredSnapshot()
        {
            var calls = 0;
            AudioHandle current = default;
            var old = AudioKit.PlayMusicWithOptions(_short, Options(.1f, .2f), onFinished: (clip, lease) =>
            {
                calls++;
                Assert.AreSame(_short, clip);
                Assert.IsTrue(lease.IsValid);
                Assert.AreEqual(0f, lease.Voice.Source.volume);
                current = AudioKit.PlayMusic(_clip);
                current.FadeIn(1f);
                Assert.AreEqual(0f, current.Voice.Source.volume);
            });
            var host = AudioManager.Instance;
            host.enabled = false;
            try
            {
                var deadline = Time.realtimeSinceStartup + 3f;
                while (old.Voice.Source.isPlaying && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsFalse(old.Voice.Source.isPlaying, "Wait for native completion; do not emulate it with Source.Stop.");
                Assert.IsTrue(old.IsValid);
                var update = typeof(AudioManager).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(update);
                update.Invoke(host, null);
                Assert.AreEqual(1, calls);
                Assert.IsFalse(old.IsValid);
                Assert.IsTrue(current.IsValid);
                Assert.AreSame(old.Voice, current.Voice);
                Assert.AreEqual(0f, current.Voice.Source.volume, "The old Update snapshot must not tick the new manual fade.");
                Assert.IsTrue(current.IsPlaying);
            }
            finally
            {
                if (host)
                {
                    host.enabled = true;
                }
            }
        }

        [UnityTest]
        public IEnumerator TargetLossSoundDisablePoolReuseAndShutdownClearAutomaticStateWithoutNaturalCallbacks()
        {
            var target = OwnObject("AutoFade.Followed");
            var calls = 0;
            var old = AudioKit.PlaySoundFollowing(_clip, target.transform, Options(), (_, __) => calls++);
            Seek(old, 1d);
            target.SetActive(false);
            old.Voice.CheckFinished();
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(0, calls);
            var current = AudioKit.PlaySound(_clip);
            Assert.AreSame(old.Voice, current.Voice);
            AssertVolume(current, 1f);
            Assert.AreEqual(default(AudioAutoFade), current.Voice.Options.AutoFade);
            Assert.DoesNotThrow(() => old.FadeIn(float.NaN));
            old.FadeOut(0f);
            AssertVolume(current, 1f);
            current.Stop();
            old = AudioKit.PlaySound(_clip, Options());
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.IsFalse(old.IsValid);
            AudioKit.Settings.IsSoundOn.Value = true;
            current = AudioKit.PlaySound(_clip);
            AssertVolume(current, 1f);
            var source = current.Voice.Source;
            var oldSettings = AudioKit.Settings;
            AudioKit.Shutdown();
            ConfigureSettings();
            var fresh = AudioKit.PlaySound(_clip, Options());
            Seek(fresh, 1d);
            oldSettings.SoundVolume.Value = .1f;
            current.FadeOut(0f);
            AssertVolume(fresh, .5f);
            yield return null;
            Assert.IsFalse(source);
            Assert.IsFalse(current.IsValid);
            Assert.IsTrue(fresh.IsValid);
            Assert.AreEqual(0, calls);
        }

        [UnityTest]
        public IEnumerator ManualStopCompletionCannotTurnIntoAnAutomaticNaturalCallback()
        {
            var calls = 0;
            var handle = AudioKit.PlaySound(_clip, Options(), (_, __) => calls++);
            Seek(handle, 1d);
            handle.FadeOut(.1f);
            handle.Voice.TickFade(.1f, .1f);
            Assert.IsFalse(handle.IsValid);
            yield return null;
            Assert.AreEqual(0, calls);
        }

        [UnityTest]
        public IEnumerator EmittersForwardAutoFadeInAllPositionModesAndSelectOneActualClip()
        {
            foreach (var weighted in new[] { false, true })
            {
                foreach (AudioEmitter.PositionMode mode in Enum.GetValues(typeof(AudioEmitter.PositionMode)))
                {
                    var root = OwnObject("AutoFade.Emitter");
                    root.SetActive(false);
                    var emitter = root.AddComponent<AudioEmitter>();
                    SetField(emitter, "clips", new List<AudioClip> { _clip, _other });
                    SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_clip), new AudioWeightedClip(_other, 3f) });
                    SetField(emitter, "useWeightedRandom", weighted);
                    SetField(emitter, "positionMode", mode);
                    SetField(emitter, "autoFade", new AudioAutoFade(8f, 12f));
                    SetField(emitter, "volume", .6f);
                    root.SetActive(true);
                    var before = Random.state;
                    AudioClip selected;
                    if (weighted)
                    {
                        Assert.IsTrue(AudioKit.TrySelectWeightedRandomClip(new[] { new AudioWeightedClip(_clip), new AudioWeightedClip(_other, 3f) }, out selected));
                    }
                    else
                    {
                        Assert.IsTrue(AudioKit.TrySelectRandomClip(new[] { _clip, _other }, out selected));
                    }

                    var afterOne = Random.state;
                    Random.state = before;
                    emitter.Play();
                    Assert.AreEqual(afterOne, Random.state);
                    var handle = AudioKit.GetPlayingSounds()[0];
                    Assert.AreSame(selected, handle.Voice.Source.clip);
                    Assert.AreEqual(mode == AudioEmitter.PositionMode.TwoDimensional ? 0f : 1f, handle.Voice.Source.spatialBlend);
                    var length = selected.samples / (double)selected.frequency;
                    Seek(handle, length * .2d);
                    AssertVolume(handle, .3f);
                    emitter.Stop();
                    Object.Destroy(root);
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator InvalidComponentAutoFadeAndEmptySelectionNeverStopAnExistingLoop()
        {
            foreach (var weighted in new[] { false, true })
            {
                var root = OwnObject("AutoFade.LoopEmitter");
                root.SetActive(false);
                var emitter = root.AddComponent<AudioEmitter>();
                SetField(emitter, "clips", new List<AudioClip> { _clip });
                SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_clip) });
                SetField(emitter, "useWeightedRandom", weighted);
                SetField(emitter, "loop", true);
                SetField(emitter, "autoFade", new AudioAutoFade(2f, 0f));
                root.SetActive(true);
                emitter.Play();
                var current = AudioKit.GetPlayingSounds()[0];
                foreach (var invalid in new[] { new AudioAutoFade(1f, 1f), new AudioAutoFade(float.NaN, 0f), new AudioAutoFade(0f, -1f) })
                {
                    SetField(emitter, "autoFade", invalid);
                    var state = Random.state;
                    Assert.Catch<ArgumentException>(emitter.Play);
                    Assert.AreEqual(state, Random.state);
                    Assert.IsTrue(current.IsValid);
                }
                SetField(emitter, "autoFade", new AudioAutoFade(2f, 0f));
                SetField(emitter, "clips", new List<AudioClip>());
                SetField(emitter, "weightedClips", new List<AudioWeightedClip>());
                emitter.Play();
                Assert.IsTrue(current.IsValid);
                emitter.Stop();
            }
            var musicRoot = OwnObject("AutoFade.MusicPlayer");
            musicRoot.SetActive(false);
            var player = musicRoot.AddComponent<AudioMusicPlayer>();
            SetField(player, "playOnAwake", false);
            SetField(player, "clip", _clip);
            musicRoot.SetActive(true);
            player.Play();
            var legacy = AudioKit.Music;
            Assert.IsTrue(legacy.Voice.Source.loop);
            AssertVolume(legacy, 1f);
            SetField(player, "autoFade", new AudioAutoFade(2f, 0f));
            player.Play();
            var automatic = AudioKit.Music;
            Assert.IsFalse(legacy.IsValid);
            AssertVolume(automatic, 0f);
            SetField(player, "autoFade", new AudioAutoFade(2f, 1f));
            Assert.Catch<ArgumentException>(player.Play);
            Assert.IsTrue(automatic.IsValid);
            SetField(player, "loop", false);
            player.Play();
            var tail = AudioKit.Music;
            Seek(tail, 9.5d);
            AssertVolume(tail, .5f);
            player.enabled = false;
            Assert.IsFalse(tail.IsValid);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ActionsAndSequenceRetainOptionsOnResetButCancelOnlyTheirOwnLease()
        {
            var options = Options();
            options.Volume = .4f;
            options.Pitch = 1.5f;
            options.OutputRoute = AudioOutputRoute.Direct;
            var calls = 0;
            var action = OwnAction(PlaySoundAction.AllocateWithOptions(_clip, options, () => calls++));
            Assert.IsFalse(action.Execute(0f));
            var old = AudioKit.GetPlayingSounds()[0];
            Seek(old, 1d);
            var oldOptions = old.Voice.Options;
            old.SetVolumeScale(.2f);
            old.SetPitch(.8f);
            Assert.AreEqual(.2f, old.Voice.Options.Volume);
            Assert.AreEqual(.8f, old.Voice.Options.Pitch);
            AssertOptionsSnapshot(options, oldOptions);
            old.FadeOut(0f, stopWhenDone: false);
            action.Reset();
            Assert.IsFalse(old.IsValid);
            Assert.IsFalse(action.Execute(0f));
            var current = AudioKit.GetPlayingSounds()[0];
            AssertOptionsSnapshot(options, current.Voice.Options);
            AssertOptionsSnapshot(options, oldOptions);
            old.SetVolumeScale(.1f);
            old.SetPitch(2f);
            Assert.AreEqual(.4f, current.Voice.Options.Volume);
            Assert.AreEqual(1.5f, current.Voice.Options.Pitch);
            Assert.AreEqual(.4f, options.Volume);
            Assert.AreEqual(1.5f, options.Pitch);
            AssertVolume(current, 0f);
            var unrelated = AudioKit.PlaySound(_other);
            action.Deinit();
            Assert.IsFalse(current.IsValid);
            Assert.AreEqual(default(AudioPlaybackOptions), current.Voice.Options);
            Assert.IsTrue(ReferenceEquals(null, current.Voice.Options.OutputRoute.Group));
            Assert.IsTrue(unrelated.IsValid);
            Assert.AreEqual(0, calls);
            unrelated.Stop();
            yield return null;
            yield return null;
            var reused = OwnAction(PlaySoundAction.Allocate(_clip));
            Assert.AreSame(action, reused, "Exercise actual delayed Action pool reuse.");
            Assert.IsFalse(reused.Execute(0f));
            var plain = AudioKit.GetPlayingSounds()[0];
            AssertOptionsSnapshot(AudioSoundOptions.Defaults, plain.Voice.Options);
            AssertVolume(plain, 1f);
            reused.Deinit();
            var steps = 0;
            var sequence = OwnAction(Kit.Sequence());
            Assert.AreSame(sequence, sequence.PlaySoundWithOptions(_clip, options));
            sequence.Append(Kit.Callback(() => steps++));
            Assert.IsFalse(sequence.Execute(0f));
            var sequenced = AudioKit.GetPlayingSounds()[0];
            AssertOptionsSnapshot(options, sequenced.Voice.Options);
            Seek(sequenced, 1d);
            AssertVolume(sequenced, .2f);
            sequence.Deinit();
            Assert.IsFalse(sequenced.IsValid);
            Assert.AreEqual(0, steps);
        }

        private static void AssertOptionsSnapshot(AudioSoundOptions expected, AudioPlaybackOptions actual)
        {
            Assert.AreEqual(expected.Loop, actual.Loop);
            Assert.AreEqual(expected.Volume, actual.Volume);
            Assert.AreEqual(expected.Pitch, actual.Pitch);
            Assert.AreEqual(expected.PlaySoundMode, actual.PlaySoundMode);
            Assert.AreEqual(expected.MinDistance, actual.MinDistance);
            Assert.AreEqual(expected.MaxDistance, actual.MaxDistance);
            Assert.AreEqual(expected.RolloffMode, actual.RolloffMode);
            Assert.AreEqual(expected.OutputRoute, actual.OutputRoute);
            Assert.AreEqual(expected.AutoFade, actual.AutoFade);
            Assert.AreEqual(expected.Priority, actual.Priority);
        }

        [UnityTest]
        public IEnumerator PausedActionStillFinishesNaturallyOnceAndResetReentryProtectsTheNewExecution()
        {
            var calls = 0;
            var action = OwnAction(PlaySoundAction.AllocateWithOptions(_short, Options(.1f, .2f), () => calls++));
            Assert.IsFalse(action.Execute(0f));
            action.IsPaused = true;
            var sound = AudioKit.GetPlayingSounds()[0];
            Assert.IsFalse(sound.IsPaused);
            yield return WaitForEnd(sound);
            Assert.AreEqual(ActionStatus.Finished, action.Status);
            Assert.IsTrue(action.Execute(0f));
            Assert.IsTrue(action.Execute(0f));
            Assert.AreEqual(1, calls);
            action.Deinit();
            PlaySoundAction restarted = null;
            calls = 0;
            restarted = OwnAction(PlaySoundAction.AllocateWithOptions(_clip, Options(), () =>
            {
                calls++;
                if (calls != 1)
                {
                    return;
                }

                restarted.Reset();
                Assert.IsFalse(restarted.Execute(0f));
            }));
            restarted.Execute(0f);
            AudioKit.GetPlayingSounds()[0].Stop();
            Assert.IsFalse(restarted.Execute(0f));
            Assert.AreEqual(ActionStatus.Started, restarted.Status);
            var fresh = AudioKit.GetPlayingSounds()[0];
            AssertVolume(fresh, 0f);
            Assert.AreEqual(1, calls);
            restarted.Deinit();
            Assert.IsFalse(fresh.IsValid);
            var steps = 0;
            var sequence = OwnAction(Kit.Sequence());
            sequence.PlaySoundWithOptions(_short, Options(.1f, .2f));
            sequence.Append(Kit.Callback(() => steps++));
            Assert.IsFalse(sequence.Execute(0f));
            var sequenced = AudioKit.GetPlayingSounds()[0];
            yield return WaitForEnd(sequenced);
            Assert.IsTrue(sequence.Execute(0f));
            Assert.AreEqual(1, steps);
            sequence.Deinit();
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator MixerAndVolumeChangesPreserveAutomaticGainAcrossInheritedOverrideAndDirectRoutes()
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            Assert.IsNotNull(mixer);
            var music = mixer.FindMatchingGroups("Music")[0];
            var ui = mixer.FindMatchingGroups("UI")[0];
            var routes = new[] { AudioOutputRoute.Inherit, AudioOutputRoute.ToMixer(ui), AudioOutputRoute.Direct };
            var groups = new[] { music, ui, null };
            for (var category = 0; category < 3; category++)
            {
                for (var i = 0; i < routes.Length; i++)
                {
                    AudioKit.Settings.SoundOutputGroup.Value = music;
                    AudioKit.Settings.MusicOutputGroup.Value = music;
                    AudioKit.Settings.VoiceOutputGroup.Value = music;
                    AudioKit.Settings.SoundVolume.Value = 1f;
                    AudioKit.Settings.MusicVolume.Value = 1f;
                    AudioKit.Settings.VoiceVolume.Value = 1f;
                    var options = Options();
                    options.OutputRoute = routes[i];
                    var handle = category == 0 ? AudioKit.PlaySound(_clip, options)
                        : category == 1 ? AudioKit.PlayMusicWithOptions(_clip, options)
                        : AudioKit.PlayVoiceWithOptions(_clip, options);
                    Seek(handle, 1d);
                    handle.Pause();
                    var gain = handle.Voice.Source.volume;
                    var generation = handle.Generation;
                    Assert.AreSame(groups[i], handle.Voice.Source.outputAudioMixerGroup);
                    AudioKit.Settings.SoundOutputGroup.Value = ui;
                    AudioKit.Settings.MusicOutputGroup.Value = ui;
                    AudioKit.Settings.VoiceOutputGroup.Value = ui;
                    Assert.AreSame(i == 2 ? null : ui, handle.Voice.Source.outputAudioMixerGroup);
                    Assert.AreEqual(gain, handle.Voice.Source.volume);
                    handle.SetVolumeScale(3f);
                    AudioKit.Settings.SoundVolume.Value = .8f;
                    AudioKit.Settings.MusicVolume.Value = .8f;
                    AudioKit.Settings.VoiceVolume.Value = .8f;
                    Assert.AreEqual(gain, handle.Voice.Source.volume, .0001f, "Clamp category*request before multiplying the one envelope.");
                    handle.SetVolumeScale(.5f);
                    Assert.AreEqual(.4f * gain, handle.Voice.Source.volume, .0001f);
                    handle.SetPitch(2f);
                    Assert.AreEqual(.4f * gain, handle.Voice.Source.volume, .0001f);
                    AudioKit.ClearMixerRouting();
                    Assert.AreSame(i == 1 ? ui : null, handle.Voice.Source.outputAudioMixerGroup);
                    Assert.AreEqual(generation, handle.Generation);
                    handle.Resume();
                    Seek(handle, 8.5d);
                    AssertVolume(handle, .2f);
                    handle.Stop();
                }
            }
            yield return null;
        }
#endif
    }
}
