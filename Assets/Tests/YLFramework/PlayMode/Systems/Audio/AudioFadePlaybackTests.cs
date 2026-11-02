using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioFadePlaybackTests
    {
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private AudioClip _clip;
        private GameObject _listener;
        private float _timeScale;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            AudioKit.Shutdown();
            _timeScale = Time.timeScale;
            Time.timeScale = 1f;
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
            _clip = AudioClip.Create("fade-live", 441000, 1, 44100, false);
            _listener = new GameObject("fade-test-listener");
            _listener.AddComponent<AudioListener>();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            Time.timeScale = _timeScale;
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
            Object.Destroy(_clip);
            Object.Destroy(_listener);
            yield return null;
        }

        private AudioHandle PlaySound()
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            return AudioKit.PlaySound(_clip, options);
        }

        private static IEnumerator WaitForVolume(AudioHandle handle, float volume)
        {
            var end = Time.realtimeSinceStartup + 2f;
            while (handle.IsValid && Mathf.Abs(handle.Voice.Source.volume - volume) > .0001f && Time.realtimeSinceStartup < end)
            {
                yield return null;
            }

            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(volume, handle.Voice.Source.volume, .0001f);
        }

        private static IEnumerator WaitForStop(AudioHandle handle)
        {
            var end = Time.realtimeSinceStartup + 2f;
            while (handle.IsValid && Time.realtimeSinceStartup < end)
            {
                yield return null;
            }

            Assert.IsFalse(handle.IsValid, "Fade must release its request through the normal AudioManager update.");
        }

        [UnityTest]
        public IEnumerator ActualUpdateFadesAllCategoriesAndFadeStopIsNotNaturalCompletion()
        {
            var callbacks = 0;
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            var handles = new[]
            {
                AudioKit.PlaySound(_clip, options, (_, __) => callbacks++),
                AudioKit.PlayMusic(_clip, onFinished: (_, __) => callbacks++),
                AudioKit.PlayVoice(_clip, loop: true, onFinished: (_, __) => callbacks++)
            };
            foreach (var handle in handles)
            {
                handle.FadeIn(.05f);
                Assert.AreEqual(0f, handle.Voice.Source.volume);
                yield return WaitForVolume(handle, 1f);
                Assert.IsTrue(handle.IsPlaying);
                handle.FadeOut(.05f);
                yield return WaitForStop(handle);
            }
            Assert.AreEqual(0, callbacks);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator UnscaledDefaultProgressesAtZeroTimeScaleWhileScaledFadeWaits()
        {
            Time.timeScale = 0f;
            var unscaled = PlaySound();
            var scaled = PlaySound();
            unscaled.FadeIn(.05f);
            scaled.FadeIn(.05f, ignoreTimeScale: false);
            yield return WaitForVolume(unscaled, 1f);
            Assert.AreEqual(0f, scaled.Voice.Source.volume);
            Assert.IsTrue(scaled.IsPlaying);
            Time.timeScale = 1f;
            yield return WaitForVolume(scaled, 1f);
        }

        [UnityTest]
        public IEnumerator PausedFadeFreezesEvenWithUnscaledTimeAndResumes()
        {
            var handle = PlaySound();
            handle.FadeIn(.1f);
            handle.Pause();
            yield return new WaitForSecondsRealtime(.15f);
            Assert.IsTrue(handle.IsValid);
            Assert.IsTrue(handle.IsPaused);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            handle.Resume();
            yield return WaitForVolume(handle, 1f);
            Assert.IsTrue(handle.IsPlaying);
        }

        [UnityTest]
        public IEnumerator DisabledPendingMusicDoesNotSpendFadeBeforeItsFirstStart()
        {
            AudioKit.Settings.IsMusicOn.Value = false;
            var starts = 0;
            var handle = AudioKit.PlayMusic(_clip, onStarted: (_, __) => starts++);
            handle.FadeIn(.05f);
            yield return new WaitForSecondsRealtime(.1f);
            Assert.AreEqual(0, starts);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(1, starts);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            yield return WaitForVolume(handle, 1f);
        }

        [UnityTest]
        public IEnumerator MutedRequestStillFadesAndCanRemainActiveAtSilence()
        {
            var handle = PlaySound();
            AudioKit.Settings.IsSoundMuted.Value = true;
            handle.FadeIn(.05f);
            yield return WaitForVolume(handle, 1f);
            Assert.IsTrue(handle.Voice.Source.mute);
            handle.FadeOut(.05f, stopWhenDone: false);
            yield return WaitForVolume(handle, 0f);
            Assert.IsTrue(handle.IsPlaying);
            AudioKit.Settings.IsSoundMuted.Value = false;
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            handle.FadeIn(.05f);
            yield return WaitForVolume(handle, 1f);
        }

        [UnityTest]
        public IEnumerator ReplacingFadeCancelsItsScheduledStopAndKeepsBaseVolumeChanges()
        {
            var handle = PlaySound();
            handle.FadeOut(2f);
            yield return null;
            var before = handle.Voice.Source.volume;
            handle.FadeIn(.05f);
            Assert.AreEqual(before, handle.Voice.Source.volume);
            handle.SetVolumeScale(.4f);
            AudioKit.Settings.SoundVolume.Value = .5f;
            yield return WaitForVolume(handle, .2f);
            Assert.AreEqual(.4f, handle.Voice.Options.Volume);
            Assert.IsTrue(handle.IsPlaying);
        }

        [UnityTest]
        public IEnumerator OldSlotFadeCannotStopOrSilenceReplacementMusic()
        {
            var old = AudioKit.PlayMusic(_clip);
            old.FadeOut(.03f);
            var current = AudioKit.PlayMusic(_clip, volume: .6f);
            old.FadeIn(10f);
            old.FadeOut(0f);
            yield return new WaitForSecondsRealtime(.1f);
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(current.IsPlaying);
            Assert.AreEqual(.6f, current.Voice.Source.volume);
        }

        [UnityTest]
        public IEnumerator OldPoolLeaseCannotAffectReusedSourceAndShutdownCancelsFade()
        {
            var old = PlaySound();
            old.FadeOut(10f);
            var source = old.Voice.Source;
            old.Stop();
            var current = PlaySound();
            Assert.AreSame(source, current.Voice.Source);
            old.FadeOut(0f);
            yield return null;
            Assert.IsTrue(current.IsPlaying);
            Assert.AreEqual(1f, source.volume);
            current.FadeIn(10f);
            AudioKit.Shutdown();
            yield return null;
            Assert.IsFalse(current.IsValid);
            Assert.IsFalse(source);
        }

        [UnityTest]
        public IEnumerator NaturalCompletionWinsBeforeAnUnfinishedFadeAndCallbackKeepsLeaseContract()
        {
            var shortClip = AudioClip.Create("fade-short", 2205, 1, 44100, false);
            var callbacks = 0;
            AudioHandle callbackHandle = default;
            AudioClip callbackClip = null;
            var wasValidInCallback = false;
            try
            {
                var handle = AudioKit.PlaySound(shortClip, onFinished: (clip, lease) =>
                {
                    callbacks++;
                    callbackHandle = lease;
                    callbackClip = clip;
                    wasValidInCallback = lease.IsValid;
                });
                handle.FadeOut(10f);
                yield return WaitForStop(handle);
                Assert.AreEqual(1, callbacks);
                Assert.AreEqual(handle, callbackHandle);
                Assert.AreSame(shortClip, callbackClip);
                Assert.IsTrue(wasValidInCallback);
            }
            finally
            {
                Object.Destroy(shortClip);
            }
        }

        [UnityTest]
        public IEnumerator StartCallbackCanBeginFadeAndSoundSwitchCancelsPendingStop()
        {
            AudioHandle callbackHandle = default;
            var music = AudioKit.PlayMusic(_clip, onStarted: (_, handle) =>
            {
                callbackHandle = handle;
                handle.FadeIn(.05f);
            });
            Assert.AreEqual(music, callbackHandle);
            Assert.AreEqual(0f, music.Voice.Source.volume);
            yield return WaitForVolume(music, 1f);
            var old = PlaySound();
            old.FadeOut(.05f);
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.IsFalse(old.IsValid);
            AudioKit.Settings.IsSoundOn.Value = true;
            var current = PlaySound();
            yield return new WaitForSecondsRealtime(.1f);
            Assert.IsTrue(current.IsPlaying);
            Assert.AreEqual(1f, current.Voice.Source.volume);
        }
    }
}
