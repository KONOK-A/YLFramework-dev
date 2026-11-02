using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Audio;
#endif
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioMusicCrossfadePlaybackTests
    {
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private AudioClip _a;
        private AudioClip _b;
        private AudioClip _c;
        private AudioClip _short;
        private GameObject _listener;
        private float _timeScale;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
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
            _a = AudioClip.Create("crossfade-play-a", 441000, 1, 44100, false);
            _b = AudioClip.Create("crossfade-play-b", 441000, 1, 44100, false);
            _c = AudioClip.Create("crossfade-play-c", 441000, 1, 44100, false);
            _short = AudioClip.Create("crossfade-native-end", 13230, 1, 44100, false);
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
                _listener = new GameObject("Crossfade.Listener");
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
            Time.timeScale = _timeScale;
            if (_listener)
            {
                Object.Destroy(_listener);
            }

            Object.Destroy(_a);
            Object.Destroy(_b);
            Object.Destroy(_c);
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
        }

        private static AudioSoundOptions Options(bool loop = true, float volume = 1f)
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = loop;
            options.Volume = volume;
            return options;
        }

        private static void AssertVolume(AudioHandle handle, float expected, string message = null)
        {
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(expected, handle.Voice.Source.volume, .01f, message);
        }

        private static IEnumerator WaitUntil(Func<bool> condition, string message, float timeout = 3f)
        {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(condition(), message);
        }

        private static void UpdateHost(AudioManager host)
        {
            var update = typeof(AudioManager).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(update);
            update.Invoke(host, null);
        }

        private static void TickTransition(AudioManager host, float deltaTime, float unscaledDeltaTime)
        {
            var property = typeof(AudioManager).GetProperty("MusicPlayback", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(property);
            var playback = property.GetValue(host);
            var tick = playback.GetType().GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(float), typeof(float) }, null);
            Assert.IsNotNull(tick);
            tick.Invoke(playback, new object[] { deltaTime, unscaledDeltaTime });
        }

        private static void Seek(AudioHandle handle, float seconds)
        {
            Assert.IsTrue(handle.IsValid);
            handle.Pause();
            var source = handle.Voice.Source;
            source.timeSamples = (int)(seconds * source.clip.frequency);
            handle.Resume();
            Assert.IsTrue(source.isPlaying);
            Assert.AreEqual(seconds, source.timeSamples / (double)source.clip.frequency, .025d);
            handle.Voice.TickFade(0f, 0f);
        }

        [UnityTest]
        public IEnumerator RealSourcesOverlapAdvanceAndExposeLatestTargetBeforeStartNotification()
        {
            var finishes = 0;
            var old = AudioKit.PlayMusic(_a, onFinished: (_, __) => finishes++);
            yield return new WaitForSecondsRealtime(.08f);
            var oldSamples = old.Voice.Source.timeSamples;
            var starts = 0;
            var incoming = AudioKit.CrossFadeMusic(_b, .6f, curve: AudioCrossfadeCurve.Linear, onStarted: (clip, lease) =>
            {
                starts++;
                Assert.AreSame(_b, clip);
                Assert.AreEqual(lease, AudioKit.Music);
                Assert.IsTrue(old.IsPlaying);
                Assert.IsTrue(lease.IsPlaying);
                AssertVolume(lease, 0f);
                Assert.AreNotSame(old.Voice.Source, lease.Voice.Source);
            }, onFinished: (_, __) => finishes++);
            Assert.AreEqual(1, starts);
            Assert.IsTrue(incoming.Voice.Source.loop);
            Assert.IsTrue(old.IsValid);
            yield return WaitUntil(() => incoming.Voice.Source.volume > .2f, "The real Update must advance crossfade.");
            Assert.Less(incoming.Voice.Source.volume, 1f);
            Assert.Greater(old.Voice.Source.volume, 0f);
            Assert.AreEqual(1f, old.Voice.Source.volume + incoming.Voice.Source.volume, .01f);
            Assert.Greater(old.Voice.Source.timeSamples, oldSamples);
            Assert.Greater(incoming.Voice.Source.timeSamples, 0);
            Assert.Greater(incoming.Time, 0f);
            Assert.Greater(incoming.Progress, 0f);
            Assert.AreEqual(_b.length, incoming.Duration);
            yield return WaitUntil(() => !old.IsValid, "The outgoing lease must expire at transition completion.");
            AssertVolume(incoming, 1f);
            Assert.AreEqual(incoming, AudioKit.Music);
            Assert.IsTrue(incoming.IsPlaying);
            Assert.AreEqual(0, finishes);
        }

        [UnityTest]
        public IEnumerator NoOutgoingStillFadesInAndSameClipStartsANewIndependentPlayback()
        {
            var first = AudioKit.CrossFadeMusic(_a, .15f);
            AssertVolume(first, 0f);
            Assert.IsTrue(first.IsPlaying);
            yield return WaitUntil(() => first.Voice.Source.volume >= .99f, "A sole incoming track must reach full gain.");
            yield return new WaitForSecondsRealtime(.12f);
            var oldSamples = first.Voice.Source.timeSamples;
            Assert.Greater(oldSamples, 0);
            var second = AudioKit.CrossFadeMusic(_a, .2f, AudioSoundOptions.Defaults);
            Assert.AreNotEqual(first, second);
            Assert.AreNotSame(first.Voice.Source, second.Voice.Source);
            Assert.IsTrue(first.IsPlaying);
            Assert.IsTrue(second.IsPlaying);
            Assert.IsFalse(second.Voice.Source.loop);
            Assert.Less(second.Voice.Source.timeSamples, oldSamples, "Same clip means a new request from the beginning.");
            AssertVolume(second, 0f);
            yield return WaitUntil(() => !first.IsValid, "Same-clip outgoing lease must retire normally.");
            AssertVolume(second, 1f);
            var immediate = AudioKit.CrossFadeMusic(_c, 0f, onStarted: (_, lease) =>
            {
                Assert.IsFalse(second.IsValid);
                Assert.IsTrue(lease.IsPlaying);
                AssertVolume(lease, 1f);
            });
            yield return null;
            Assert.AreEqual(immediate, AudioKit.Music);
            Assert.IsTrue(immediate.IsPlaying);
        }

        [UnityTest]
        public IEnumerator DefaultClockContinuesAtZeroTimeScaleButExplicitScaledClockWaits()
        {
            Time.timeScale = 0f;
            var unscaled = AudioKit.CrossFadeMusic(_a, .15f);
            yield return WaitUntil(() => unscaled.Voice.Source.volume >= .99f, "Default crossfade uses unscaled time.");
            var scaled = AudioKit.CrossFadeMusic(_b, .2f, ignoreTimeScale: false);
            yield return new WaitForSecondsRealtime(.12f);
            AssertVolume(unscaled, 1f);
            AssertVolume(scaled, 0f);
            Assert.Greater(scaled.Voice.Source.timeSamples, 0, "Native audio advances even while scaled transition time is frozen.");
            Time.timeScale = 1f;
            yield return WaitUntil(() => !unscaled.IsValid, "Scaled crossfade must continue after timeScale resumes.");
            AssertVolume(scaled, 1f);
        }

        [UnityTest]
        public IEnumerator WholePauseStopsBothSourcesWhileSinglePauseOnlyFreezesTheirJointTransition()
        {
            var old = AudioKit.PlayMusic(_a);
            var current = AudioKit.CrossFadeMusic(_b, 1f);
            yield return WaitUntil(() => current.Voice.Source.volume >= .15f, "Transition must first make progress.");
            AudioKit.PauseMusic();
            var aVolume = old.Voice.Source.volume;
            var bVolume = current.Voice.Source.volume;
            var aSamples = old.Voice.Source.timeSamples;
            var bSamples = current.Voice.Source.timeSamples;
            yield return new WaitForSecondsRealtime(.1f);
            Assert.IsTrue(old.IsPaused);
            Assert.IsTrue(current.IsPaused);
            Assert.AreEqual(aSamples, old.Voice.Source.timeSamples);
            Assert.AreEqual(bSamples, current.Voice.Source.timeSamples);
            AssertVolume(old, aVolume);
            AssertVolume(current, bVolume);
            AudioKit.ResumeMusic();
            old.Pause();
            bSamples = current.Voice.Source.timeSamples;
            yield return new WaitForSecondsRealtime(.1f);
            Assert.Greater(current.Voice.Source.timeSamples, bSamples);
            Assert.IsTrue(current.IsPlaying);
            AssertVolume(old, aVolume);
            AssertVolume(current, bVolume);
            old.Resume();
            current.Pause();
            aSamples = old.Voice.Source.timeSamples;
            yield return new WaitForSecondsRealtime(.1f);
            Assert.Greater(old.Voice.Source.timeSamples, aSamples);
            AssertVolume(old, aVolume);
            AssertVolume(current, bVolume);
            current.Resume();
            old.Stop();
            yield return WaitUntil(() => current.Voice.Source.volume >= .99f, "An invalid paused participant must not freeze the survivor.");
            Assert.IsTrue(current.IsPlaying);
            Assert.IsFalse(old.IsValid);
        }

        [UnityTest]
        public IEnumerator PausedReplacementRemainsPendingUntilResumeAndFirstStartIsNotRepeated()
        {
            var old = AudioKit.PlayMusic(_a);
            AudioKit.PauseMusic();
            var starts = 0;
            var current = AudioKit.CrossFadeMusic(_b, .3f, onStarted: (_, lease) =>
            {
                starts++;
                Assert.IsTrue(lease.IsPlaying);
                AssertVolume(lease, 0f);
                Assert.AreEqual(lease, AudioKit.Music);
            });
            yield return new WaitForSecondsRealtime(.08f);
            Assert.AreEqual(0, starts);
            Assert.IsTrue(old.IsPaused);
            Assert.IsTrue(current.IsPaused);
            Assert.IsFalse(current.IsPlaying);
            Assert.AreEqual(0, current.Voice.Source.timeSamples);
            AudioKit.ResumeMusic();
            Assert.AreEqual(1, starts);
            yield return new WaitForSecondsRealtime(.05f);
            AudioKit.PauseMusic();
            AudioKit.ResumeMusic();
            AudioKit.Settings.IsMusicOn.Value = false;
            Assert.IsFalse(old.IsValid);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(1, starts);
            Assert.IsTrue(current.IsPlaying);
            AudioKit.PauseMusic();
            var legacy = AudioKit.PlayMusic(_c);
            Assert.IsFalse(current.IsValid);
            Assert.IsFalse(legacy.IsPaused);
            Assert.IsTrue(legacy.IsPlaying);
        }

        [UnityTest]
        public IEnumerator MusicOffCancelsTheTailAndReplaysOnlyTheLatestPendingTarget()
        {
            var old = AudioKit.PlayMusic(_a);
            var starts = 0;
            var current = AudioKit.CrossFadeMusic(_b, 1f, onStarted: (_, __) => starts++);
            yield return new WaitForSecondsRealtime(.08f);
            Assert.Greater(current.Voice.Source.timeSamples, 0);
            AudioKit.Settings.IsMusicOn.Value = false;
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(current.IsValid);
            Assert.IsFalse(current.IsPlaying);
            AssertVolume(current, 1f);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.IsTrue(current.IsPlaying);
            AssertVolume(current, 1f);
            Assert.AreEqual(1, starts);
            AudioKit.Settings.IsMusicOn.Value = false;
            var pendingStarts = 0;
            var pending = AudioKit.CrossFadeMusic(_c, 2f, onStarted: (_, lease) =>
            {
                pendingStarts++;
                AssertVolume(lease, 1f);
            });
            Assert.IsFalse(current.IsValid);
            Assert.IsFalse(pending.IsPlaying);
            AssertVolume(pending, 1f);
            yield return new WaitForSecondsRealtime(.08f);
            Assert.AreEqual(0, pendingStarts);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(1, pendingStarts);
            yield return new WaitForSecondsRealtime(.08f);
            AssertVolume(pending, 1f);
            Assert.IsTrue(pending.IsPlaying);
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(pending, AudioKit.Music);
        }

        [UnityTest]
        public IEnumerator MutingBothParticipantsKeepsPcmAndTransitionRunningInMusicCategory()
        {
            AudioKit.Settings.MusicVolume.Value = .8f;
            AudioKit.Settings.VoiceVolume.Value = .1f;
            var old = AudioKit.PlayMusic(_a, volume: .5f);
            var current = AudioKit.CrossFadeMusic(_b, .5f, Options(volume: .5f), curve: AudioCrossfadeCurve.Linear);
            AudioKit.Settings.IsMusicMuted.Value = true;
            Assert.IsTrue(old.Voice.Source.mute);
            Assert.IsTrue(current.Voice.Source.mute);
            yield return WaitUntil(() => current.Voice.Source.volume > .1f, "Mute must not freeze the transition.");
            Assert.AreEqual(.4f, old.Voice.Source.volume + current.Voice.Source.volume, .01f);
            Assert.Greater(old.Voice.Source.timeSamples, 0);
            Assert.Greater(current.Voice.Source.timeSamples, 0);
            var oldVolume = old.Voice.Source.volume;
            AudioKit.Settings.VoiceVolume.Value = 0f;
            AudioKit.Settings.IsVoiceMuted.Value = true;
            AssertVolume(old, oldVolume);
            AudioKit.Settings.MusicVolume.Value = .4f;
            AssertVolume(old, oldVolume * .5f);
            Assert.AreEqual(.2f, old.Voice.Source.volume + current.Voice.Source.volume, .01f);
            AudioKit.Settings.IsMusicMuted.Value = false;
            Assert.IsFalse(old.Voice.Source.mute);
            Assert.IsFalse(current.Voice.Source.mute);
            yield return WaitUntil(() => !old.IsValid, "Muted transition must still retire its outgoing track.");
            AssertVolume(current, .2f);
        }

        [UnityTest]
        public IEnumerator NativeClipEndsBeforeTransitionSuppressOutgoingButNotifyIncomingExactlyOnce()
        {
            var outgoingFinishes = 0;
            var old = AudioKit.PlayMusic(_short, loop: false, onFinished: (_, __) => outgoingFinishes++);
            var current = AudioKit.CrossFadeMusic(_b, .9f);
            var host = AudioManager.Instance;
            host.enabled = false;
            try
            {
                Assert.IsTrue(old.IsPlaying);
                yield return WaitUntil(() => !old.Voice.Source.isPlaying, "Wait for real outgoing native completion.");
                Assert.IsTrue(old.IsValid, "No Update has yet cleaned the native-ended outgoing lease.");
                UpdateHost(host);
                Assert.IsFalse(old.IsValid);
                Assert.Less(current.Voice.Source.volume, 1f, "The old clip, not the transition clock, ended first.");
            }
            finally
            {
                if (host)
                {
                    host.enabled = true;
                }
            }
            Assert.AreEqual(0, outgoingFinishes);
            Assert.IsTrue(current.IsPlaying);
            yield return WaitUntil(() => current.Voice.Source.volume >= .99f, "Incoming must continue after native outgoing end.");
            AudioKit.StopMusic();
            var incomingFinishes = 0;
            old = AudioKit.PlayMusic(_a);
            AudioHandle observed = default;
            var shortTarget = AudioKit.CrossFadeMusic(_short, .9f, AudioSoundOptions.Defaults,
                onFinished: (clip, lease) =>
                {
                    incomingFinishes++;
                    observed = lease;
                    Assert.AreSame(_short, clip);
                    Assert.IsTrue(lease.IsValid);
                    Assert.IsTrue(old.IsValid);
                    Assert.IsFalse(lease.Voice.Source.isPlaying);
                });
            yield return WaitUntil(() => !shortTarget.IsValid, "Native incoming clip must finish before the transition.");
            Assert.AreEqual(shortTarget, observed);
            Assert.AreEqual(1, incomingFinishes);
            Assert.IsTrue(old.IsValid);
            Assert.IsFalse(AudioKit.Music.IsValid, "Never promote the outgoing track after the target ends.");
            var tailVolume = old.Voice.Source.volume;
            yield return new WaitForSecondsRealtime(.1f);
            Assert.Less(old.Voice.Source.volume, tailVolume);
            yield return WaitUntil(() => !old.IsValid, "The surviving outgoing track still completes its remaining fade.");
            Assert.IsFalse(AudioKit.Music.IsValid);
            Assert.AreEqual(1, incomingFinishes);
        }

        [UnityTest]
        public IEnumerator NativeNaturalCallbackRetargetCannotAdvanceTheNewEpochFromOldUpdateSnapshot()
        {
            var old = AudioKit.PlayMusic(_a);
            var finishes = 0;
            AudioHandle nested = default;
            var current = AudioKit.CrossFadeMusic(_short, 2f, AudioSoundOptions.Defaults,
                onFinished: (_, lease) =>
                {
                    finishes++;
                    Assert.IsTrue(lease.IsValid);
                    nested = AudioKit.CrossFadeMusic(_c, 1f);
                    AssertVolume(nested, 0f);
                });
            Assert.IsTrue(current.IsPlaying);
            var host = AudioManager.Instance;
            host.enabled = false;
            try
            {
                yield return WaitUntil(() => !current.Voice.Source.isPlaying,
                    "Wait for the native source to finish; do not simulate natural completion with Stop.");
                Assert.IsTrue(current.IsValid);
                UpdateHost(host);
                Assert.AreEqual(1, finishes);
                Assert.IsFalse(old.IsValid);
                Assert.IsFalse(current.IsValid);
                Assert.AreEqual(nested, AudioKit.Music);
                Assert.IsTrue(nested.IsPlaying);
                AssertVolume(nested, 0f, "Old Update must not tick the nested transition using its elapsed frame.");
                yield return null;
                UpdateHost(host);
                Assert.Greater(nested.Voice.Source.volume, 0f);
                Assert.AreEqual(1, finishes);
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
        public IEnumerator StartedReentryThenThrowAndStopOrShutdownDoNotLeakNativeTailOrStopNewTarget()
        {
            var old = AudioKit.PlayMusic(_a);
            AudioHandle nested = default;
            var exception = new InvalidOperationException("crossfade-native-reentry");
            Assert.AreSame(exception, Assert.Throws<InvalidOperationException>(() => AudioKit.CrossFadeMusic(_b, .3f,
                onStarted: (_, __) =>
                {
                    nested = AudioKit.CrossFadeMusic(_c, .2f);
                    throw exception;
                })));
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(nested, AudioKit.Music);
            AssertVolume(nested, 0f);
            yield return WaitUntil(() => nested.Voice.Source.volume >= .99f, "Newer epoch must survive the older callback exception.");
            Assert.IsTrue(nested.IsPlaying);
            var stopped = AudioKit.CrossFadeMusic(_a, 1f, onStarted: (_, __) => AudioKit.StopMusic());
            Assert.IsFalse(stopped.IsValid);
            Assert.IsFalse(nested.IsValid);
            yield return null;
            foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
            {
                Assert.IsFalse(source.isPlaying);
                Assert.IsNull(source.clip);
            }
            old = AudioKit.PlayMusic(_a);
            var oldSource = old.Voice.Source;
            var closed = AudioKit.CrossFadeMusic(_b, 1f, onStarted: (_, __) => AudioKit.Shutdown());
            Assert.IsFalse(old.IsValid);
            Assert.IsFalse(closed.IsValid);
            Assert.IsFalse(AudioManager.HasInstance);
            ConfigureSettings();
            var fresh = AudioKit.PlayMusic(_c);
            old.Stop();
            closed.Stop();
            yield return null;
            Assert.IsFalse(oldSource);
            Assert.IsTrue(fresh.IsPlaying);
            AssertVolume(fresh, 1f);
        }

        [UnityTest]
        public IEnumerator PcmHeadTailRuntimePitchAndManualTakeoverMultiplyTheIndependentTransitionGain()
        {
            AudioKit.Settings.MusicVolume.Value = .8f;
            var automatic = Options(false);
            automatic.AutoFade = new AudioAutoFade(2f, 2f);
            var old = AudioKit.PlayMusicWithOptions(_a, automatic);
            Seek(old, 1f);
            AssertVolume(old, .4f);
            automatic.Volume = 3f;
            automatic.Pitch = .5f;
            var current = AudioKit.CrossFadeMusic(_b, 4f, automatic, (_, lease) =>
            {
                AssertVolume(lease, 0f);
                Assert.AreEqual(.5f, lease.Voice.Source.pitch);
                Assert.IsFalse(lease.Voice.Source.loop);
            }, curve: AudioCrossfadeCurve.Linear);
            var host = AudioManager.Instance;
            host.enabled = false;
            try
            {
                Seek(current, 1f);
                TickTransition(host, 1f, 1f);
                AssertVolume(old, .3f);
                AssertVolume(current, .125f);
                Seek(old, 9f);
                AssertVolume(old, .3f, "Outgoing automatic tail stays active after it becomes a replaced music lease.");
                var generation = current.Generation;
                current.SetPitch(2f);
                Assert.AreEqual(generation, current.Generation);
                AssertVolume(current, .125f);
                var samples = current.Voice.Source.timeSamples;
                yield return new WaitForSecondsRealtime(.08f);
                Assert.Greater(current.Voice.Source.timeSamples, samples);
                current.Voice.TickFade(0f, 0f);
                var source = current.Voice.Source;
                var automaticGain = AudioAutoFade.Evaluate(source.timeSamples / (double)_b.frequency, 10d, 2d, 2d);
                AssertVolume(current, automaticGain * .25f);
                Seek(current, 9f);
                AssertVolume(current, .125f);
                TickTransition(host, 1f, 1f);
                AssertVolume(current, .25f);
                AssertVolume(old, .2f);
                current.FadeOut(2f, stopWhenDone: false);
                current.Voice.TickFade(1f, 1f);
                AssertVolume(current, .125f);
                Seek(current, 5f);
                AssertVolume(current, .125f, "Manual takeover cancels only automatic request control, not the transition.");
                TickTransition(host, 2f, 2f);
                Assert.IsFalse(old.IsValid);
                AssertVolume(current, .25f);
                AudioKit.Settings.IsMusicOn.Value = false;
                AudioKit.Settings.IsMusicOn.Value = true;
                AssertVolume(current, .25f, "Restart must not reactivate the automatic head after manual takeover.");
                Seek(current, 1f);
                AssertVolume(current, .25f);
                current.Voice.TickFade(1f, 1f);
                AssertVolume(current, 0f);
                current.Stop();
                old = AudioKit.PlayMusic(_a);
                current = AudioKit.CrossFadeMusic(_c, 2f, curve: AudioCrossfadeCurve.Linear);
                current.FadeOut(.1f);
                current.Voice.TickFade(.1f, .1f);
                Assert.IsFalse(current.IsValid);
                TickTransition(host, 1f, 1f);
                AssertVolume(old, .4f);
                Assert.IsFalse(AudioKit.Music.IsValid);
                TickTransition(host, 1f, 1f);
                Assert.IsFalse(old.IsValid);
                Assert.IsFalse(AudioKit.Music.IsValid);
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
        public IEnumerator DefaultEqualPowerRealSourcesKeepUnitPowerNearMidpoint()
        {
            var old = AudioKit.PlayMusic(_a);
            yield return new WaitForSecondsRealtime(.05f);
            var incoming = AudioKit.CrossFadeMusic(_b, .6f);
            yield return WaitUntil(() => incoming.Voice.Source.volume >= .35f, "Equal-power transition must advance.");
            Assert.IsTrue(old.IsValid);
            var oldVolume = old.Voice.Source.volume;
            var incomingVolume = incoming.Voice.Source.volume;
            Assert.Greater(incomingVolume, 0f);
            Assert.Less(incomingVolume, .95f);
            Assert.AreEqual(1f, oldVolume * oldVolume + incomingVolume * incomingVolume, .04f,
                "Both volumes come from the same tick, so sine/cosine squares must stay near one.");
            yield return WaitUntil(() => !old.IsValid, "The outgoing lease must expire at transition completion.");
            AssertVolume(incoming, 1f);
            Assert.AreEqual(incoming, AudioKit.Music);
        }

        [UnityTest]
        public IEnumerator RuntimeMusicComponentKeepsLegacyLinearInitializerAndDrivesExplicitEqualPower()
        {
            var root = new GameObject("Crossfade.Curve.Component");
            try
            {
                var player = root.AddComponent<AudioMusicPlayer>();
                var clipField = typeof(AudioMusicPlayer).GetField("clip", BindingFlags.NonPublic | BindingFlags.Instance);
                var playOnAwake = typeof(AudioMusicPlayer).GetField("playOnAwake", BindingFlags.NonPublic | BindingFlags.Instance);
                var crossFadeOnPlay = typeof(AudioMusicPlayer).GetField("crossFadeOnPlay", BindingFlags.NonPublic | BindingFlags.Instance);
                var crossFadeDuration = typeof(AudioMusicPlayer).GetField("crossFadeDuration", BindingFlags.NonPublic | BindingFlags.Instance);
                var crossFadeCurve = typeof(AudioMusicPlayer).GetField("crossFadeCurve", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(clipField);
                Assert.IsNotNull(playOnAwake);
                Assert.IsNotNull(crossFadeOnPlay);
                Assert.IsNotNull(crossFadeDuration);
                Assert.IsNotNull(crossFadeCurve);
                Assert.AreEqual(AudioCrossfadeCurve.Linear, crossFadeCurve.GetValue(player),
                    "Runtime AddComponent preserves the legacy field initializer; Editor Reset selects EqualPower separately.");
                clipField.SetValue(player, _a);
                playOnAwake.SetValue(player, false);
                crossFadeOnPlay.SetValue(player, true);
                crossFadeDuration.SetValue(player, .6f);
                crossFadeCurve.SetValue(player, AudioCrossfadeCurve.EqualPower);
                player.Play();
                var first = AudioKit.Music;
                Assert.IsTrue(first.IsValid);
                yield return WaitUntil(() => first.Voice.Source.volume >= .999f,
                    "The first component playback must reach full gain before the overlap starts.");
                player.Play();
                var second = AudioKit.Music;
                Assert.AreNotEqual(first, second);
                Assert.IsTrue(first.IsValid);
                yield return WaitUntil(() => second.Voice.Source.volume >= .35f, "The component must drive a real equal-power overlap.");
                Assert.Greater(second.Voice.Source.volume, 0f);
                Assert.Less(second.Voice.Source.volume, .95f);
                Assert.AreEqual(1f, first.Voice.Source.volume * first.Voice.Source.volume + second.Voice.Source.volume * second.Voice.Source.volume,
                    .04f, "EqualPower component playback must keep sine/cosine squares near one.");
                player.Stop();
                Assert.IsFalse(AudioKit.Music.IsValid);
            }
            finally
            {
                Object.Destroy(root);
            }
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator MixerInheritanceOverridesAndDirectStayIndependentAcrossNativeTwoSourceReuse()
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            Assert.IsNotNull(mixer);
            var music = mixer.FindMatchingGroups("Music")[0];
            var voice = mixer.FindMatchingGroups("Voice")[0];
            var ui = mixer.FindMatchingGroups("UI")[0];
            var routes = new[] { AudioOutputRoute.Inherit, AudioOutputRoute.ToMixer(ui), AudioOutputRoute.Direct };
            AudioSource firstSource = null, secondSource = null;
            for (var route = 0; route < routes.Length; route++)
            {
                AudioKit.Settings.MusicOutputGroup.Value = music;
                AudioKit.Settings.VoiceOutputGroup.Value = voice;
                AudioKit.Settings.MusicVolume.Value = .8f;
                var old = AudioKit.PlayMusic(_a);
                var options = Options(volume: .5f);
                options.OutputRoute = routes[route];
                var expectedGroup = route == 0 ? music : route == 1 ? ui : null;
                var current = AudioKit.CrossFadeMusic(_b, 2f, options, curve: AudioCrossfadeCurve.Linear, onStarted: (_, lease) =>
                {
                    Assert.IsTrue(old.IsPlaying);
                    Assert.IsTrue(lease.IsPlaying);
                    Assert.AreSame(expectedGroup, lease.Voice.Source.outputAudioMixerGroup);
                    AssertVolume(lease, 0f);
                });
                var host = AudioManager.Instance;
                host.enabled = false;
                try
                {
                    if (!firstSource)
                    {
                        firstSource = old.Voice.Source;
                        secondSource = current.Voice.Source;
                    }
                    TickTransition(host, .5f, .5f);
                    AssertVolume(old, .6f);
                    AssertVolume(current, .1f);
                    var oldGeneration = old.Generation;
                    var currentGeneration = current.Generation;
                    var oldSamples = old.Voice.Source.timeSamples;
                    var currentSamples = current.Voice.Source.timeSamples;
                    AudioKit.Settings.VoiceVolume.Value = .1f;
                    AudioKit.Settings.VoiceOutputGroup.Value = ui;
                    Assert.AreSame(music, old.Voice.Source.outputAudioMixerGroup);
                    AudioKit.Settings.MusicOutputGroup.Value = ui;
                    Assert.AreSame(ui, old.Voice.Source.outputAudioMixerGroup);
                    Assert.AreSame(route == 2 ? null : ui, current.Voice.Source.outputAudioMixerGroup);
                    AudioKit.Settings.MusicVolume.Value = .4f;
                    AssertVolume(old, .3f);
                    AssertVolume(current, .05f);
                    yield return new WaitForSecondsRealtime(.05f);
                    Assert.Greater(old.Voice.Source.timeSamples, oldSamples);
                    Assert.Greater(current.Voice.Source.timeSamples, currentSamples);
                    Assert.AreEqual(oldGeneration, old.Generation);
                    Assert.AreEqual(currentGeneration, current.Generation);
                    AudioKit.ClearMixerRouting();
                    Assert.IsNull(old.Voice.Source.outputAudioMixerGroup);
                    Assert.AreSame(route == 1 ? ui : null, current.Voice.Source.outputAudioMixerGroup);
                    for (var i = 0; i < 8; i++)
                    {
                        var previousTail = old;
                        old = current;
                        current = AudioKit.CrossFadeMusic(i % 2 == 0 ? _c : _a, 1f, options, curve: AudioCrossfadeCurve.Linear);
                        Assert.IsFalse(previousTail.IsValid);
                        Assert.IsTrue(old.IsPlaying);
                        Assert.IsTrue(current.IsPlaying);
                        Assert.AreEqual(current, AudioKit.Music);
                        Assert.AreEqual(2, host.GetComponentsInChildren<AudioSource>(true).Length);
                        Assert.IsTrue(ReferenceEquals(current.Voice.Source, firstSource) || ReferenceEquals(current.Voice.Source, secondSource));
                        Assert.AreSame(route == 1 ? ui : null, current.Voice.Source.outputAudioMixerGroup);
                        AssertVolume(current, 0f);
                        TickTransition(host, .25f, .25f);
                    }
                    AudioKit.StopMusic();
                    foreach (var source in host.GetComponentsInChildren<AudioSource>(true))
                    {
                        Assert.IsFalse(source.isPlaying);
                        Assert.IsNull(source.clip);
                        Assert.IsNull(source.outputAudioMixerGroup);
                    }
                }
                finally
                {
                    if (host)
                    {
                        host.enabled = true;
                    }
                }
            }
        }
#endif
    }
}
