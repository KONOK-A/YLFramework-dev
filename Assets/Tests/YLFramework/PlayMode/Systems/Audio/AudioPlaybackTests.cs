using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioPlaybackTests
    {
        private AudioClip _longClip;
        private AudioClip _shortClip;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private bool _ownsActionSession;

        [SetUp]
        public void SetUp()
        {
            AudioKit.Shutdown();
            _ownsActionSession = false;
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
            AudioKit.GlobalFrameCountForIgnoreSameSound = 10;
            AudioKit.SoundFrameCountForIgnoreSameSound = 10;
            _longClip = AudioClip.Create("long", 44100, 1, 44100, false);
            _shortClip = AudioClip.Create("short", 2205, 1, 44100, false);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            if (_ownsActionSession)
            {
                YLFramework.Actions.ActionKit.Shutdown();
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
            foreach (var root in _objects)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }

            _objects.Clear();
            Object.Destroy(_longClip);
            Object.Destroy(_shortClip);
            yield return null;
        }

        private GameObject OwnObject(string name)
        {
            var root = new GameObject(name);
            _objects.Add(root);
            return root;
        }

        private static AudioSoundOptions Options(bool loop = false, float volume = 1f)
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = loop;
            options.Volume = volume;
            return options;
        }

        private void AcquireActionSession()
        {
            Assert.IsFalse(YLFramework.Actions.ActionKit.IsInitialized);
            var executor = typeof(YLFramework.Actions.ActionKit).Assembly.GetType("YLFramework.Actions.MonoUpdateActionExecutor", true);
            var driver = typeof(YLFramework.Actions.ActionKit).Assembly.GetType("YLFramework.Actions.ActionRecycleDriver", true);
            Assert.IsEmpty(Object.FindObjectsOfType(executor, true));
            Assert.IsEmpty(Object.FindObjectsOfType(driver, true), "Do not release another owner's Action session.");
            _ownsActionSession = true;
        }

        [UnityTest]
        public IEnumerator SameFrameRepeatedShutdownRecreatesHostWithoutOldCallbacksOrSources()
        {
            var calls = 0;
            var old = AudioKit.PlaySound(_longClip, Options(true), (_, __) => calls++);
            var first = AudioManager.Instance;
            var oldSource = old.Voice.Source;
            var oldSettings = AudioKit.Settings;
            AudioKit.Shutdown();
            Assert.IsFalse(old.IsValid);
            var middle = AudioManager.Instance;
            AudioKit.Shutdown();
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            var current = AudioKit.PlaySound(_longClip, Options(true));
            var host = AudioManager.Instance;
            Assert.AreNotSame(first, host);
            Assert.AreNotSame(middle, host);
            Assert.AreEqual("Audio", host.transform.parent.name);
            Assert.IsNotNull(host.transform.root.GetComponent<YLFramework.YLFrameworkRoot>());
            oldSettings.IsSoundOn.Value = false;
            old.Stop();
            yield return null;
            Assert.IsFalse(first);
            Assert.IsFalse(middle);
            Assert.IsFalse(oldSource);
            Assert.AreSame(host, AudioManager.Instance);
            Assert.IsTrue(current.IsPlaying);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(1, Object.FindObjectsOfType<AudioManager>(true).Length);
        }

        [UnityTest]
        public IEnumerator BorrowedManagerShutdownPreservesExternalGameObjectAndListener()
        {
            var root = OwnObject("AudioKit.BorrowedHost");
            root.SetActive(false);
            var listener = root.AddComponent<AudioListener>();
            var manager = root.AddComponent<AudioManager>();
            Assert.AreSame(manager, AudioManager.Instance);
            root.SetActive(true);
            var handle = AudioKit.PlaySound(_longClip, Options(true));
            var source = handle.Voice.Source;
            AudioKit.Shutdown();
            yield return null;
            Assert.IsTrue(root);
            Assert.IsTrue(listener);
            Assert.IsFalse(manager);
            Assert.IsFalse(source);
            Assert.IsFalse(handle.IsValid);
        }

        [UnityTest]
        public IEnumerator StopAndPoolReuseDiscardCallbacksAndIsolateOldHandle()
        {
            var callback = 0;
            var old = AudioKit.PlaySound(_longClip, onFinished: (_, __) => callback++);
            var source = old.Voice.Source;
            yield return null;
            Assert.IsTrue(old.IsPlaying);
            old.Stop();
            Assert.IsFalse(old.IsValid);
            Assert.IsNull(source.clip);
            Assert.IsTrue(_longClip);
            var current = AudioKit.PlaySound(_shortClip);
            Assert.AreSame(old.Voice, current.Voice);
            Assert.AreNotEqual(old.Generation, current.Generation);
            old.Pause();
            old.Stop();
            Assert.IsTrue(current.IsValid);
            yield return WaitForFinish(current);
            Assert.AreEqual(0, callback);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator FullIdlePoolDestroysOverflowAndShutdownDestroysCachedSources()
        {
            var handles = new AudioHandle[12];
            var sources = new AudioSource[12];
            var callbacks = 0;
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = AudioKit.PlaySound(_longClip, Options(true), (_, __) => callbacks++);
                sources[i] = handles[i].Voice.Source;
            }
            Assert.AreEqual(12, AudioKit.SoundCount);
            foreach (var handle in handles)
            {
                handle.Stop();
                handle.Stop();
            }
            for (var i = 0; i < 10; i++)
            {
                Assert.IsFalse(sources[i].gameObject.activeSelf);
                Assert.IsNull(sources[i].clip);
                Assert.IsFalse(handles[i].IsValid);
            }
            yield return null;
            Assert.IsFalse(sources[10]);
            Assert.IsFalse(sources[11]);
            var current = AudioKit.PlaySound(_shortClip);
            Assert.AreSame(handles[9].Voice, current.Voice);
            Assert.AreSame(sources[9], current.Voice.Source);
            AudioKit.Shutdown();
            yield return null;
            foreach (var source in sources)
            {
                Assert.IsFalse(source);
            }

            Assert.IsFalse(current.IsValid);
            Assert.AreEqual(0, callbacks);
            Assert.IsTrue(_longClip);
            Assert.IsTrue(_shortClip);
        }

        [UnityTest]
        public IEnumerator PauseResumePreservesPositionAndSuppressesCompletion()
        {
            var finished = 0;
            var handle = AudioKit.PlaySound(_longClip, onFinished: (_, __) => finished++);
            yield return new WaitForSecondsRealtime(.12f);
            handle.Pause();
            var sample = handle.Voice.Source.timeSamples;
            var position = handle.Time;
            var generation = handle.Generation;
            Assert.Greater(sample, 0);
            Assert.Greater(position, 0f);
            Assert.AreEqual(_longClip.length, handle.Duration, .001f);
            Assert.AreEqual(handle.Voice.Source.time, position, .04f);
            Assert.AreEqual(position / handle.Duration, handle.Progress, .04f);
            yield return new WaitForSecondsRealtime(.12f);
            Assert.IsTrue(handle.IsPaused);
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(0, finished);
            Assert.AreEqual(position, handle.Time, .04f);
            Assert.AreEqual(generation, handle.Generation);
            handle.Resume();
            Assert.IsFalse(handle.IsPaused);
            Assert.GreaterOrEqual(handle.Voice.Source.timeSamples, sample - 512);
            yield return null;
            Assert.IsTrue(handle.IsPlaying);
            yield return new WaitForSecondsRealtime(.12f);
            Assert.Greater(handle.Time, position + .04f);
            Assert.That(handle.Progress, Is.InRange(0f, 1f));
            Assert.AreEqual(_longClip.length, handle.Duration, .001f);
            handle.Stop();
            Assert.AreEqual(0, finished);
            Assert.AreEqual(0f, handle.Time);
            Assert.AreEqual(0f, handle.Duration);
            Assert.AreEqual(0f, handle.Progress);
        }

        [UnityTest]
        public IEnumerator LoopRequiresStopAndDoesNotNotifyFinish()
        {
            var finished = 0;
            var handle = AudioKit.PlaySound(_shortClip, Options(true), (_, __) => finished++);
            yield return new WaitForSecondsRealtime(.2f);
            Assert.IsTrue(handle.IsPlaying);
            Assert.IsTrue(handle.Voice.Source.loop);
            Assert.AreEqual(0, finished);
            handle.Stop();
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, finished);
        }

        [UnityTest]
        public IEnumerator SharedVolumeUpdatesActiveSourcesButNotIdleSources()
        {
            var music = AudioKit.PlayMusic(_longClip, volume: .5f);
            var voice = AudioKit.PlayVoice(_longClip, true, .25f);
            var first = AudioKit.PlaySound(_longClip, Options(true, .5f));
            var second = AudioKit.PlaySound(_longClip, Options(true, .25f));
            Assert.AreEqual(.5f, first.Voice.Source.volume, .001f);
            AudioKit.Settings.SoundVolume.Value = .4f;
            Assert.AreEqual(.2f, first.Voice.Source.volume, .001f);
            Assert.AreEqual(.1f, second.Voice.Source.volume, .001f);
            AudioKit.Settings.MusicVolume.Value = .6f;
            AudioKit.Settings.VoiceVolume.Value = .8f;
            Assert.AreEqual(.3f, music.Voice.Source.volume, .001f);
            Assert.AreEqual(.2f, voice.Voice.Source.volume, .001f);
            yield return null;
            var idleSource = first.Voice.Source;
            AudioKit.StopAllSound();
            Assert.IsFalse(first.IsValid);
            Assert.IsFalse(second.IsValid);
            var idleVolume = idleSource.volume;
            AudioKit.Settings.SoundVolume.Value = .7f;
            Assert.AreEqual(idleVolume, idleSource.volume);
            var current = AudioKit.PlaySound(_longClip, Options(true, .5f));
            Assert.AreEqual(.35f, current.Voice.Source.volume, .001f);
            AudioKit.Settings.SoundVolume.Value = .2f;
            Assert.AreEqual(.1f, current.Voice.Source.volume, .001f);
        }

        [UnityTest]
        public IEnumerator MusicVoiceSoundUseSeparateSlotsAndReplacementInvalidatesOldMusic()
        {
            var music = AudioKit.PlayMusic(_longClip);
            var voice = AudioKit.PlayVoice(_longClip, true, .3f);
            var sound = AudioKit.PlaySound(_longClip, Options(true));
            yield return null;
            Assert.AreEqual(music, AudioKit.Music);
            Assert.AreEqual(voice, AudioKit.Voice);
            Assert.IsTrue(music.IsPlaying);
            Assert.IsTrue(voice.IsPlaying);
            Assert.IsTrue(sound.IsPlaying);
            Assert.AreNotSame(music.Voice.Source, voice.Voice.Source);
            AudioKit.PauseMusic();
            Assert.IsTrue(music.IsPaused);
            Assert.IsTrue(voice.IsPlaying);
            AudioKit.ResumeMusic();
            var replacement = AudioKit.PlayMusic(_longClip, false, .4f);
            Assert.IsFalse(music.IsValid);
            Assert.AreNotEqual(music.Generation, replacement.Generation);
            music.Stop();
            Assert.IsTrue(replacement.IsValid);
            AudioKit.StopVoice();
            Assert.IsFalse(voice.IsValid);
            Assert.IsTrue(sound.IsPlaying);
            AudioKit.StopMusic();
            Assert.IsFalse(replacement.IsValid);
        }

        [UnityTest]
        public IEnumerator RuntimeVolumeAndPitchControlOnlyTheirRequestWithoutRestarting()
        {
            var starts = 0;
            var finishes = 0;
            var music = AudioKit.PlayMusic(_longClip, true, .5f, (_, __) => starts++, (_, __) => finishes++);
            var voice = AudioKit.PlayVoice(_longClip, true, .5f, (_, __) => starts++, (_, __) => finishes++);
            var sound = AudioKit.PlaySound(_longClip, Options(true), (_, __) => finishes++);
            var other = AudioKit.PlaySound(_longClip, Options(true, .2f));
            yield return new WaitForSecondsRealtime(.12f);
            var musicSample = music.Voice.Source.timeSamples;
            var voiceSample = voice.Voice.Source.timeSamples;
            var soundSample = sound.Voice.Source.timeSamples;
            var generation = sound.Generation;
            Assert.Greater(soundSample, 0);
            AudioKit.Settings.MusicVolume.Value = .8f;
            AudioKit.Settings.VoiceVolume.Value = .6f;
            AudioKit.Settings.SoundVolume.Value = .4f;
            music.SetVolumeScale(.25f);
            voice.SetVolumeScale(.5f);
            sound.SetVolumeScale(.75f);
            music.SetPitch(1.5f);
            voice.SetPitch(.5f);
            sound.SetPitch(2f);
            Assert.AreEqual(.2f, music.Voice.Source.volume, .001f);
            Assert.AreEqual(.3f, voice.Voice.Source.volume, .001f);
            Assert.AreEqual(.3f, sound.Voice.Source.volume, .001f);
            Assert.AreEqual(.08f, other.Voice.Source.volume, .001f);
            Assert.AreEqual(1f, other.Voice.Source.pitch);
            Assert.AreEqual(1.5f, music.Voice.Source.pitch);
            Assert.AreEqual(.5f, voice.Voice.Source.pitch);
            Assert.AreEqual(2f, sound.Voice.Source.pitch);
            Assert.GreaterOrEqual(music.Voice.Source.timeSamples, musicSample);
            Assert.GreaterOrEqual(voice.Voice.Source.timeSamples, voiceSample);
            Assert.GreaterOrEqual(sound.Voice.Source.timeSamples, soundSample);
            Assert.AreEqual(generation, sound.Generation);
            Assert.AreEqual(2, starts);
            Assert.AreEqual(0, finishes);
            AudioKit.Settings.SoundVolume.Value = .2f;
            Assert.AreEqual(.15f, sound.Voice.Source.volume, .001f);
            Assert.AreEqual(.04f, other.Voice.Source.volume, .001f);
            sound.SetVolumeScale(0f);
            yield return null;
            Assert.IsTrue(sound.IsPlaying, "A zero multiplier mutes without stopping the request.");
            Assert.IsTrue(music.IsPlaying);
            Assert.IsTrue(voice.IsPlaying);
            Assert.AreEqual(0f, sound.Voice.Source.volume);
        }

        [UnityTest]
        public IEnumerator ParameterChangesKeepManualPauseAndSurviveSwitchReplay()
        {
            var starts = 0;
            var music = AudioKit.PlayMusic(_longClip, onStarted: (_, __) => starts++);
            var voice = AudioKit.PlayVoice(_longClip, true, onStarted: (_, __) => starts++);
            var sound = AudioKit.PlaySound(_longClip, Options(true));
            yield return new WaitForSecondsRealtime(.05f);
            music.Pause();
            voice.Pause();
            sound.Pause();
            music.SetVolumeScale(.3f);
            music.SetPitch(1.2f);
            voice.SetVolumeScale(.4f);
            voice.SetPitch(1.4f);
            sound.SetVolumeScale(.5f);
            sound.SetPitch(1.6f);
            yield return null;
            Assert.IsTrue(music.IsPaused);
            Assert.IsTrue(voice.IsPaused);
            Assert.IsTrue(sound.IsPaused);
            Assert.IsFalse(sound.IsPlaying);
            AudioKit.Settings.IsMusicOn.Value = false;
            AudioKit.Settings.IsVoiceOn.Value = false;
            music.SetVolumeScale(.25f);
            music.SetPitch(2f);
            voice.SetVolumeScale(.5f);
            voice.SetPitch(.75f);
            AudioKit.Settings.MusicVolume.Value = .4f;
            AudioKit.Settings.VoiceVolume.Value = .6f;
            AudioKit.Settings.IsMusicOn.Value = true;
            AudioKit.Settings.IsVoiceOn.Value = true;
            Assert.IsTrue(music.IsPaused);
            Assert.IsTrue(voice.IsPaused);
            music.Resume();
            voice.Resume();
            sound.Resume();
            yield return null;
            Assert.IsTrue(music.IsPlaying);
            Assert.IsTrue(voice.IsPlaying);
            Assert.IsTrue(sound.IsPlaying);
            Assert.AreEqual(.1f, music.Voice.Source.volume, .001f);
            Assert.AreEqual(.3f, voice.Voice.Source.volume, .001f);
            Assert.AreEqual(2f, music.Voice.Source.pitch);
            Assert.AreEqual(.75f, voice.Voice.Source.pitch);
            Assert.AreEqual(.5f, sound.Voice.Source.volume, .001f);
            Assert.AreEqual(1.6f, sound.Voice.Source.pitch);
            Assert.AreEqual(2, starts);
        }

        [UnityTest]
        public IEnumerator DisabledPendingSlotsApplyNewParametersAtFirstStart()
        {
            AudioKit.Settings.IsMusicOn.Value = false;
            AudioKit.Settings.IsVoiceOn.Value = false;
            var starts = 0;
            var music = AudioKit.PlayMusic(_longClip, onStarted: (_, __) => starts++);
            var voice = AudioKit.PlayVoice(_longClip, true, onStarted: (_, __) => starts++);
            music.SetVolumeScale(.25f);
            music.SetPitch(1.25f);
            voice.SetVolumeScale(.5f);
            voice.SetPitch(.75f);
            AudioKit.Settings.MusicVolume.Value = .4f;
            AudioKit.Settings.VoiceVolume.Value = .6f;
            Assert.IsFalse(music.IsPlaying);
            Assert.IsFalse(voice.IsPlaying);
            Assert.AreEqual(0, starts);
            AudioKit.Settings.IsMusicOn.Value = true;
            AudioKit.Settings.IsVoiceOn.Value = true;
            yield return null;
            Assert.IsTrue(music.IsPlaying);
            Assert.IsTrue(voice.IsPlaying);
            Assert.AreEqual(.1f, music.Voice.Source.volume, .001f);
            Assert.AreEqual(.3f, voice.Voice.Source.volume, .001f);
            Assert.AreEqual(1.25f, music.Voice.Source.pitch);
            Assert.AreEqual(.75f, voice.Voice.Source.pitch);
            Assert.AreEqual(2, starts);
        }

        [UnityTest]
        public IEnumerator PoolReuseResetsRuntimeParametersAndIgnoresOldSetterCalls()
        {
            var old = AudioKit.PlaySound(_longClip, Options(true));
            var source = old.Voice.Source;
            old.SetVolumeScale(.25f);
            old.SetPitch(2f);
            old.Stop();
            var current = AudioKit.PlaySound(_longClip, Options(true));
            Assert.AreSame(source, current.Voice.Source);
            old.SetVolumeScale(0f);
            old.SetPitch(.5f);
            Assert.AreEqual(1f, source.volume);
            Assert.AreEqual(1f, source.pitch);
            Assert.AreEqual(1f, current.Voice.Options.Volume);
            Assert.AreEqual(1f, current.Voice.Options.Pitch);
            yield return null;
            Assert.IsTrue(current.IsPlaying);
        }

        [UnityTest]
        public IEnumerator NaturalCompletionNotifiesExactlyOnceAndCanStartAnotherSound()
        {
            var finished = 0;
            var next = default(AudioHandle);
            var completed = default(AudioHandle);
            var handle = AudioKit.PlaySound(_shortClip, onFinished: (clip, callbackHandle) =>
            {
                finished++;
                completed = callbackHandle;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                Assert.IsFalse(callbackHandle.IsPlaying);
                Assert.AreEqual(_shortClip.length, callbackHandle.Duration, .001f);
                next = AudioKit.PlaySound(_longClip, Options(true));
            });
            yield return WaitForFinish(handle);
            Assert.AreEqual(1, finished);
            Assert.AreEqual(handle, completed);
            Assert.IsFalse(completed.IsValid);
            Assert.AreEqual(0f, completed.Time);
            Assert.AreEqual(0f, completed.Duration);
            Assert.AreEqual(0f, completed.Progress);
            Assert.IsTrue(next.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
            yield return null;
            Assert.AreEqual(1, finished);
            Assert.IsTrue(next.IsPlaying);
        }

        [UnityTest]
        public IEnumerator FinishCallbackCanReplaceMusicWithoutOldCleanupClearingIt()
        {
            var finished = 0;
            var next = default(AudioHandle);
            var completed = default(AudioHandle);
            var old = AudioKit.PlayMusic(_shortClip, false, onFinished: (clip, callbackHandle) =>
            {
                finished++;
                completed = callbackHandle;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                Assert.AreEqual(callbackHandle, AudioKit.Music);
                next = AudioKit.PlayMusic(_longClip);
                Assert.IsFalse(callbackHandle.IsValid);
                Assert.AreSame(_shortClip, clip, "Replacement must not rewrite the captured clip.");
            });
            yield return WaitForFinish(old);
            Assert.AreEqual(1, finished);
            Assert.AreEqual(old, completed);
            Assert.AreNotEqual(completed.Generation, next.Generation);
            Assert.IsTrue(next.IsValid);
            Assert.AreEqual(next, AudioKit.Music);
            Assert.AreSame(_longClip, next.Voice.Source.clip);
            Assert.IsTrue(next.Voice.Source.loop);
            yield return null;
            Assert.IsTrue(next.IsPlaying);
        }

        [UnityTest]
        public IEnumerator StartCallbackReplacementCannotBeClearedByExpiredReturnedHandle()
        {
            var started = 0;
            var next = default(AudioHandle);
            var startedHandle = default(AudioHandle);
            var old = AudioKit.PlayMusic(_shortClip, false, onStarted: (clip, callbackHandle) =>
            {
                started++;
                startedHandle = callbackHandle;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                next = AudioKit.PlayMusic(_longClip, true, .6f);
                Assert.IsFalse(callbackHandle.IsValid);
            });
            Assert.AreEqual(old, startedHandle);
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(next.IsValid);
            Assert.AreEqual(next, AudioKit.Music);
            old.Stop();
            yield return null;
            Assert.AreEqual(1, started);
            Assert.IsTrue(next.IsPlaying);
            Assert.AreSame(_longClip, next.Voice.Source.clip);
            Assert.AreEqual(.6f, next.Voice.Source.volume, .001f);
        }

        [UnityTest]
        public IEnumerator FinishCallbackShutdownCleansOtherPlaybackAndPermitsNewSession()
        {
            var other = AudioKit.PlaySound(_longClip, Options(true));
            var calls = 0;
            var finishing = AudioKit.PlaySound(_shortClip, onFinished: (clip, callbackHandle) =>
            {
                calls++;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                AudioKit.Shutdown();
                Assert.IsFalse(callbackHandle.IsValid);
            });
            yield return WaitForFinish(finishing);
            yield return null;
            Assert.AreEqual(1, calls);
            Assert.IsFalse(other.IsValid);
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            var current = AudioKit.PlaySound(_longClip, Options(true));
            Assert.IsTrue(current.IsValid);
            finishing.Stop();
            other.Stop();
            yield return null;
            Assert.IsTrue(current.IsPlaying);
        }

        [UnityTest]
        public IEnumerator ThrowingFinishCallbackStillReleasesPlayback()
        {
            LogAssert.Expect(LogType.Exception, new Regex("expected audio callback failure"));
            var handle = AudioKit.PlaySound(_shortClip, onFinished: (clip, callbackHandle) =>
            {
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                throw new InvalidOperationException("expected audio callback failure");
            });
            yield return WaitForFinish(handle);
            Assert.AreEqual(0, AudioKit.SoundCount);
            var next = AudioKit.PlaySound(_longClip, Options(true));
            yield return null;
            Assert.IsTrue(next.IsPlaying);
        }

        [UnityTest]
        public IEnumerator PerClipRecordIsRemovedByNaturalFinishButNotStop()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            AudioKit.SoundFrameCountForIgnoreSameSound = 10000;
            var first = AudioKit.PlaySound(_shortClip);
            yield return WaitForFinish(first);
            var second = AudioKit.PlaySound(_shortClip, Options(true));
            Assert.IsTrue(second.IsValid);
            second.Stop();
            Assert.IsFalse(AudioKit.PlaySound(_shortClip).IsValid);
        }

        [UnityTest]
        public IEnumerator MusicSwitchRestartsFullRequestWithoutRepeatingStartCallback()
        {
            var started = 0;
            var finished = 0;
            var handle = AudioKit.PlayMusic(_longClip, false, .35f, (_, __) => started++, (_, __) => finished++);
            yield return new WaitForSecondsRealtime(.12f);
            Assert.Greater(handle.Voice.Source.timeSamples, 0);
            AudioKit.Settings.IsMusicOn.Value = false;
            Assert.IsTrue(handle.IsValid);
            Assert.IsFalse(handle.IsPlaying);
            Assert.AreEqual(0f, handle.Time);
            Assert.AreEqual(_longClip.length, handle.Duration, .001f);
            Assert.AreEqual(0f, handle.Progress);
            yield return new WaitForSecondsRealtime(.05f);
            Assert.AreEqual(0, finished);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(handle, AudioKit.Music);
            Assert.IsFalse(handle.Voice.Source.loop);
            Assert.AreEqual(.35f, handle.Voice.Source.volume, .001f);
            Assert.Less(handle.Voice.Source.timeSamples, 4096, "Reopening starts at the beginning, not at the suspended position.");
            Assert.AreEqual(1, started);
            yield return WaitForFinish(handle);
            Assert.AreEqual(1, finished);
            AudioKit.Settings.IsMusicOn.Value = false;
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.IsFalse(AudioKit.Music.IsValid, "Naturally finished requests cannot be resurrected.");
        }

        [UnityTest]
        public IEnumerator VoiceSwitchPreservesLoopAndVolumeAndDoesNotRepeatCallbacks()
        {
            var started = 0;
            var finished = 0;
            var handle = AudioKit.PlayVoice(_shortClip, true, .25f, (_, __) => started++, (_, __) => finished++);
            AudioKit.Settings.IsVoiceOn.Value = false;
            Assert.IsTrue(handle.IsValid);
            Assert.IsFalse(handle.IsPlaying);
            AudioKit.Settings.IsVoiceOn.Value = true;
            yield return new WaitForSecondsRealtime(.15f);
            Assert.IsTrue(handle.IsPlaying);
            Assert.IsTrue(handle.Voice.Source.loop);
            Assert.AreEqual(.25f, handle.Voice.Source.volume, .001f);
            Assert.AreEqual(1, started);
            Assert.AreEqual(0, finished);
            handle.Stop();
            AudioKit.Settings.IsVoiceOn.Value = false;
            AudioKit.Settings.IsVoiceOn.Value = true;
            Assert.IsFalse(AudioKit.Voice.IsValid);
        }

        [UnityTest]
        public IEnumerator VoicePendingRequestCanBeStoppedBeforeFirstStart()
        {
            AudioKit.Settings.IsVoiceOn.Value = false;
            var started = 0;
            var finished = 0;
            var handle = AudioKit.PlayVoice(_longClip, false, .45f, (_, __) => started++, (_, __) => finished++);
            Assert.IsTrue(handle.IsValid);
            Assert.IsFalse(handle.IsPlaying);
            handle.Stop();
            AudioKit.Settings.IsVoiceOn.Value = true;
            yield return null;
            Assert.IsFalse(handle.IsValid);
            Assert.IsFalse(AudioKit.Voice.IsValid);
            Assert.AreEqual(0, started);
            Assert.AreEqual(0, finished);
        }

        [UnityTest]
        public IEnumerator DisabledMusicRequestsReplacePendingItemAndExplicitStopDoesNotResurrect()
        {
            AudioKit.Settings.IsMusicOn.Value = false;
            var oldStarted = 0;
            var started = 0;
            var old = AudioKit.PlayMusic(_longClip, true, .2f, (_, __) => oldStarted++);
            var current = AudioKit.PlayMusic(_shortClip, false, .7f, (_, __) => started++);
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(current.IsValid);
            Assert.IsFalse(current.IsPlaying);
            old.Stop();
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(0, oldStarted);
            Assert.AreEqual(1, started);
            Assert.AreSame(_shortClip, current.Voice.Source.clip);
            Assert.AreEqual(.7f, current.Voice.Source.volume, .001f);
            Assert.IsFalse(current.Voice.Source.loop);
            current.Stop();
            AudioKit.Settings.IsMusicOn.Value = false;
            AudioKit.Settings.IsMusicOn.Value = true;
            yield return null;
            Assert.IsFalse(current.IsValid);
            Assert.IsFalse(AudioKit.Music.IsValid);
        }

        [UnityTest]
        public IEnumerator ManuallyPausedMusicIsNotResumedBySwitchAndStartCallbackReentryRunsOnce()
        {
            var started = 0;
            var startedHandle = default(AudioHandle);
            var handle = AudioKit.PlayMusic(_longClip, onStarted: (clip, callbackHandle) =>
            {
                started++;
                startedHandle = callbackHandle;
                Assert.AreSame(_longClip, clip);
                callbackHandle.Pause();
                callbackHandle.Resume();
                callbackHandle.Pause();
            });
            Assert.AreEqual(handle, startedHandle);
            Assert.IsTrue(handle.IsPaused);
            AudioKit.Settings.IsMusicOn.Value = false;
            AudioKit.Settings.IsMusicOn.Value = true;
            yield return null;
            Assert.IsTrue(handle.IsPaused);
            Assert.IsFalse(handle.IsPlaying);
            handle.Resume();
            yield return null;
            Assert.IsTrue(handle.IsPlaying);
            Assert.AreEqual(1, started);
        }

        [UnityTest]
        public IEnumerator SoundSwitchStopsAllWithoutRestoringOldPlayback()
        {
            var finished = 0;
            var old = AudioKit.PlaySound(_longClip, Options(true), (_, __) => finished++);
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.IsFalse(AudioKit.PlaySound(_longClip).IsValid);
            AudioKit.Settings.IsSoundOn.Value = true;
            yield return null;
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.AreEqual(0, finished);
        }

        [UnityTest]
        public IEnumerator SpatialPlaybackIsConfiguredBeforePlayAndReuseResetsTo2D()
        {
            var options = Options(true, .4f);
            options.Pitch = 1.7f;
            options.MinDistance = 2f;
            options.MaxDistance = 30f;
            options.RolloffMode = AudioRolloffMode.Logarithmic;
            var position = new Vector3(7f, 3f, -2f);
            var spatial = AudioKit.PlaySoundAt(_longClip, position, options);
            var source = spatial.Voice.Source;
            Assert.AreEqual(position, source.transform.position);
            Assert.AreEqual(1f, source.spatialBlend);
            Assert.AreEqual(2f, source.minDistance);
            Assert.AreEqual(30f, source.maxDistance);
            Assert.AreEqual(AudioRolloffMode.Logarithmic, source.rolloffMode);
            Assert.AreEqual(0f, source.dopplerLevel);
            Assert.AreEqual(1.7f, source.pitch, .001f);
            spatial.Pause();
            spatial.Stop();
            var current = AudioKit.PlaySound(_longClip);
            Assert.AreSame(source, current.Voice.Source);
            Assert.AreEqual(0f, source.spatialBlend);
            Assert.AreEqual(Vector3.zero, source.transform.localPosition);
            Assert.AreEqual(1f, source.minDistance);
            Assert.AreEqual(50f, source.maxDistance);
            Assert.AreEqual(AudioRolloffMode.Linear, source.rolloffMode);
            Assert.AreEqual(0f, source.dopplerLevel);
            Assert.AreEqual(1f, source.pitch);
            Assert.AreEqual(1f, source.volume);
            Assert.IsFalse(source.loop);
            Assert.IsFalse(current.IsPaused);
            spatial.Stop();
            yield return null;
            Assert.IsTrue(current.IsPlaying);
        }

        [UnityTest]
        public IEnumerator FollowingTracksWorldPositionWithoutParentingAndStopsOnDisable()
        {
            var target = OwnObject("AudioKit.FollowTarget");
            target.transform.position = new Vector3(2f, 4f, 6f);
            var finished = 0;
            var handle = AudioKit.PlaySoundFollowing(_longClip, target.transform, Options(true), (_, __) => finished++);
            var source = handle.Voice.Source;
            Assert.AreEqual(target.transform.position, source.transform.position);
            Assert.AreEqual(1f, source.spatialBlend);
            Assert.AreNotSame(target.transform, source.transform.parent);
            target.transform.position = new Vector3(-3f, 1f, 8f);
            yield return null;
            yield return null;
            Assert.AreEqual(target.transform.position, source.transform.position);
            target.SetActive(false);
            yield return null;
            yield return null;
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, finished);
            Assert.IsNull(source.clip);
            Assert.IsFalse(AudioKit.PlaySoundFollowing(_longClip, target.transform).IsValid);
        }

        [UnityTest]
        public IEnumerator FixedWorldPositionSurvivesBorrowedHostAndAncestorMovement()
        {
            var parent = OwnObject("AudioKit.MovingAncestor");
            var root = OwnObject("AudioKit.MovingBorrowedHost");
            root.SetActive(false);
            root.transform.SetParent(parent.transform, false);
            var manager = root.AddComponent<AudioManager>();
            Assert.AreSame(manager, AudioManager.Instance);
            root.SetActive(true);
            var position = new Vector3(7f, 3f, -2f);
            var handle = AudioKit.PlaySoundAt(_longClip, position, Options(true));
            handle.Pause();
            parent.transform.SetPositionAndRotation(new Vector3(13f, -7f, 2f), Quaternion.Euler(0f, 90f, 0f));
            root.transform.localPosition = new Vector3(2f, 1f, 4f);
            yield return null;
            yield return null;
            Assert.Less(Vector3.Distance(position, handle.Voice.Source.transform.position), .0001f,
                "A fixed world sound must not follow its manager or the manager's ancestors.");
            Assert.IsTrue(handle.IsPaused);
            handle.Stop();
            var reused = AudioKit.PlaySound(_longClip, Options(true));
            Assert.AreEqual(Vector3.zero, reused.Voice.Source.transform.localPosition);
            parent.transform.position += Vector3.one;
            yield return null;
            Assert.AreEqual(root.transform.position, reused.Voice.Source.transform.position,
                "Reusing the source in 2D must discard the old fixed position.");
        }

        [UnityTest]
        public IEnumerator DestroyedFollowTargetStopsButFixedPositionOneShotOutlivesCaller()
        {
            var target = OwnObject("AudioKit.DestroyTarget");
            var followFinished = 0;
            var fixedFinished = 0;
            var following = AudioKit.PlaySoundFollowing(_longClip, target.transform, Options(true), (_, __) => followFinished++);
            var completed = default(AudioHandle);
            var fixedSound = AudioKit.PlaySoundAt(_shortClip, target.transform.position, onFinished: (clip, callbackHandle) =>
            {
                fixedFinished++;
                completed = callbackHandle;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
            });
            Object.Destroy(target);
            yield return null;
            yield return null;
            Assert.IsFalse(following.IsValid);
            Assert.AreEqual(0, followFinished);
            yield return WaitForFinish(fixedSound);
            Assert.AreEqual(1, fixedFinished);
            Assert.AreEqual(fixedSound, completed);
        }

        [UnityTest]
        public IEnumerator FollowTargetSceneUnloadStopsRequestWithoutNaturalFinish()
        {
            var scene = SceneManager.CreateScene("AudioKit.FollowScene." + Guid.NewGuid().ToString("N"));
            var target = OwnObject("AudioKit.SceneTarget");
            SceneManager.MoveGameObjectToScene(target, scene);
            var finished = 0;
            var handle = AudioKit.PlaySoundFollowing(_longClip, target.transform, Options(true), (_, __) => finished++);
            try
            {
                yield return SceneManager.UnloadSceneAsync(scene);
                yield return null;
                Assert.IsFalse(handle.IsValid);
                Assert.AreEqual(0, finished);
                Assert.AreEqual(0, AudioKit.SoundCount);
            }
            finally
            {
                if (scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }
        }

        [UnityTest]
        public IEnumerator ActionSequenceWaitsForNaturalFinish()
        {
            AcquireActionSession();
            var callback = 0;
            var sequence = YLFramework.Actions.ActionKit.Sequence().PlaySound(_shortClip).Callback(() => callback++);
            try
            {
                Assert.IsFalse(sequence.Execute(0f));
                Assert.AreEqual(0, callback);
                var deadline = Time.realtimeSinceStartup + 2f;
                while (sequence.Status != ActionStatus.Finished && Time.realtimeSinceStartup < deadline)
                {
                    sequence.Execute(Time.deltaTime);
                    yield return null;
                }
                Assert.AreEqual(ActionStatus.Finished, sequence.Status);
                Assert.AreEqual(1, callback);
            }
            finally
            {
                sequence.Deinit();
            }
        }

        [UnityTest]
        public IEnumerator SpatialVoiceIsConfiguredBeforeStartAndReplacementResetsTo2D()
        {
            AudioKit.Settings.IsSoundOn.Value = false;
            AudioKit.Settings.VoiceVolume.Value = .4f;
            var music = AudioKit.PlayMusic(_longClip);
            var options = Options(true, .5f);
            options.Pitch = 1.25f;
            options.MinDistance = 2f;
            options.MaxDistance = 30f;
            options.RolloffMode = AudioRolloffMode.Logarithmic;
            var position = new Vector3(3, 4, 5);
            var starts = 0;
            var startedHandle = default(AudioHandle);
            var spatial = AudioKit.PlayVoiceAt(_longClip, position, options, onStarted: (clip, callbackHandle) =>
            {
                starts++;
                startedHandle = callbackHandle;
                Assert.AreSame(_longClip, clip);
                Assert.AreEqual(position, callbackHandle.Voice.Source.transform.position);
                Assert.AreEqual(1f, callbackHandle.Voice.Source.spatialBlend);
                Assert.AreEqual(2f, callbackHandle.Voice.Source.minDistance);
                Assert.AreEqual(30f, callbackHandle.Voice.Source.maxDistance);
                Assert.AreEqual(AudioRolloffMode.Logarithmic, callbackHandle.Voice.Source.rolloffMode);
                Assert.AreEqual(1.25f, callbackHandle.Voice.Source.pitch);
            });
            Assert.AreEqual(spatial, startedHandle);
            var source = spatial.Voice.Source;
            yield return null;
            Assert.IsTrue(spatial.IsPlaying);
            Assert.AreEqual(.2f, source.volume, .001f);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.AreEqual(0f, music.Voice.Source.spatialBlend);
            Assert.IsTrue(music.IsPlaying);
            var current = AudioKit.PlayVoice(_longClip, true);
            spatial.Stop();
            spatial.SetPitch(2f);
            Assert.IsFalse(spatial.IsValid);
            Assert.AreSame(source, current.Voice.Source);
            Assert.AreEqual(0f, source.spatialBlend);
            Assert.AreEqual(Vector3.zero, source.transform.localPosition);
            Assert.AreEqual(1f, source.minDistance);
            Assert.AreEqual(50f, source.maxDistance);
            Assert.AreEqual(AudioRolloffMode.Linear, source.rolloffMode);
            Assert.AreEqual(1f, source.pitch);
            Assert.AreEqual(1, starts);
            Assert.IsTrue(current.IsValid);
        }

        [UnityTest]
        public IEnumerator SpatialVoiceFixedPositionSurvivesBorrowedHostMovement()
        {
            var root = OwnObject("AudioKit.SpatialVoiceHost");
            root.SetActive(false);
            var manager = root.AddComponent<AudioManager>();
            Assert.AreSame(manager, AudioManager.Instance);
            root.SetActive(true);
            var position = new Vector3(3, 2, 1);
            var handle = AudioKit.PlayVoiceAt(_longClip, position, Options(true));
            handle.Pause();
            root.transform.SetPositionAndRotation(Vector3.one * 10f, Quaternion.Euler(0, 90, 0));
            yield return null;
            yield return null;
            Assert.Less(Vector3.Distance(position, handle.Voice.Source.transform.position), .0001f);
            Assert.IsTrue(handle.IsPaused);
        }

        [UnityTest]
        public IEnumerator FollowingVoiceReplaysAtLatestTargetPositionWithLatestParameters()
        {
            var target = OwnObject("AudioKit.VoiceTarget");
            target.transform.position = Vector3.one;
            var starts = 0;
            var finished = 0;
            var handle = AudioKit.PlayVoiceFollowing(_longClip, target.transform, Options(true),
                (_, __) => starts++, (_, __) => finished++);
            var source = handle.Voice.Source;
            Assert.AreNotSame(target.transform, source.transform.parent);
            target.transform.position = new Vector3(2, 3, 4);
            yield return null;
            yield return null;
            Assert.AreEqual(target.transform.position, source.transform.position);
            AudioKit.Settings.IsVoiceOn.Value = false;
            handle.SetVolumeScale(.5f);
            handle.SetPitch(1.5f);
            AudioKit.Settings.VoiceVolume.Value = .4f;
            target.transform.position = new Vector3(7, 8, 9);
            AudioKit.Settings.IsVoiceOn.Value = true;
            Assert.AreEqual(target.transform.position, source.transform.position,
                "Replaying must update the target position before Play, not a frame later.");
            yield return null;
            Assert.IsTrue(handle.IsPlaying);
            Assert.AreEqual(1f, source.spatialBlend);
            Assert.AreEqual(.2f, source.volume, .001f);
            Assert.AreEqual(1.5f, source.pitch);
            Assert.AreEqual(1, starts);
            target.SetActive(false);
            yield return null;
            yield return null;
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, finished);
            AudioKit.Settings.IsVoiceOn.Value = false;
            AudioKit.Settings.IsVoiceOn.Value = true;
            Assert.IsFalse(AudioKit.Voice.IsValid);
        }

        [UnityTest]
        public IEnumerator LostPendingVoiceTargetCannotStartWhenEnabledInTheSameFrame()
        {
            AudioKit.Settings.IsVoiceOn.Value = false;
            var target = OwnObject("AudioKit.PendingVoiceTarget");
            var starts = 0;
            var finishes = 0;
            var handle = AudioKit.PlayVoiceFollowing(_longClip, target.transform, onStarted: (_, __) => starts++,
                onFinished: (_, __) => finishes++);
            Assert.IsTrue(handle.IsValid);
            target.SetActive(false);
            AudioKit.Settings.IsVoiceOn.Value = true;
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, starts);
            Assert.AreEqual(0, finishes);
            yield return null;
            Assert.IsFalse(AudioKit.Voice.IsValid);
        }

        [UnityTest]
        public IEnumerator FollowingVoiceStopsOnDestroyedTargetAndSceneUnloadWithoutNaturalFinish()
        {
            var finished = 0;
            var target = OwnObject("AudioKit.DestroyedVoiceTarget");
            var handle = AudioKit.PlayVoiceFollowing(_longClip, target.transform, Options(true), onFinished: (_, __) => finished++);
            handle.Pause();
            Object.Destroy(target);
            yield return null;
            yield return null;
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, finished);
            var scene = SceneManager.CreateScene("AudioKit.VoiceScene." + Guid.NewGuid().ToString("N"));
            var sceneTarget = OwnObject("AudioKit.SceneVoiceTarget");
            SceneManager.MoveGameObjectToScene(sceneTarget, scene);
            var next = AudioKit.PlayVoiceFollowing(_longClip, sceneTarget.transform, Options(true), onFinished: (_, __) => finished++);
            try
            {
                yield return SceneManager.UnloadSceneAsync(scene);
                yield return null;
                Assert.IsFalse(next.IsValid);
                Assert.AreEqual(0, finished);
                Assert.AreEqual(0, AudioKit.SoundCount);
            }
            finally
            {
                if (scene.isLoaded)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
            }
        }

        [UnityTest]
        public IEnumerator SpatialVoiceNaturalFinishCanReplaceTheSameSlotWithoutOldCleanup()
        {
            var target = OwnObject("AudioKit.FinishVoiceTarget");
            var next = default(AudioHandle);
            var finished = 0;
            var completed = default(AudioHandle);
            var first = AudioKit.PlayVoiceAt(_shortClip, Vector3.one, onFinished: (clip, callbackHandle) =>
            {
                finished++;
                completed = callbackHandle;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                Assert.AreEqual(callbackHandle, AudioKit.Voice);
                next = AudioKit.PlayVoiceFollowing(_longClip, target.transform, Options(true));
                Assert.IsFalse(callbackHandle.IsValid);
            });
            yield return WaitForFinish(first);
            Assert.AreEqual(1, finished);
            Assert.AreEqual(first, completed);
            Assert.IsTrue(next.IsValid);
            Assert.AreEqual(next, AudioKit.Voice);
            Assert.AreEqual(1f, next.Voice.Source.spatialBlend);
            first.SetVolumeScale(0f);
            first.Stop();
            yield return null;
            Assert.IsTrue(next.IsPlaying);
            Assert.AreEqual(1f, next.Voice.Options.Volume);
        }

        [UnityTest]
        public IEnumerator ActionResetWaitsForNewPlaybackAndPreservesCompletionCallback()
        {
            AcquireActionSession();
            var finishes = 0;
            var action = PlaySoundAction.Allocate(_longClip, () => finishes++);
            try
            {
                action.Execute(0f);
                var old = AudioKit.GetPlayingSounds()[0];
                action.Reset();
                Assert.IsFalse(old.IsValid);
                action.Execute(0f);
                var current = AudioKit.GetPlayingSounds()[0];
                Assert.AreSame(old.Voice, current.Voice);
                Assert.AreNotEqual(old.Generation, current.Generation);
                old.Stop();
                yield return WaitForFinish(current);
                Assert.AreEqual(ActionStatus.Finished, action.Status);
                Assert.IsTrue(action.Execute(0));
                Assert.AreEqual(1, finishes);
                action.Reset();
                Assert.IsFalse(action.Execute(0));
                var final = AudioKit.GetPlayingSounds()[0];
                final.Stop();
                Assert.IsTrue(action.Execute(0));
                Assert.AreEqual(2, finishes, "Reset preserves the configured callback for the next execution.");
            }
            finally
            {
                action.Deinit();
            }
        }

        [UnityTest]
        public IEnumerator PausedActionDoesNotPauseSoundAndCanFinishNaturally()
        {
            AcquireActionSession();
            var finishes = 0;
            var action = PlaySoundAction.Allocate(_shortClip, () => finishes++);
            try
            {
                action.Execute(0f);
                action.IsPaused = true;
                var handle = AudioKit.GetPlayingSounds()[0];
                Assert.IsFalse(handle.IsPaused);
                yield return WaitForFinish(handle);
                Assert.AreEqual(ActionStatus.Finished, action.Status);
                Assert.AreEqual(0, finishes);
                Assert.IsTrue(action.Execute(0f));
                Assert.IsTrue(action.Execute(0f));
                Assert.AreEqual(ActionStatus.Finished, action.Status);
                Assert.AreEqual(1, finishes);
            }
            finally
            {
                action.Deinit();
            }
        }

        [UnityTest]
        public IEnumerator ProgressAdvancesForMusicVoiceAndFixedAndFollowingSpatialRequests()
        {
            var target = OwnObject("AudioKit.ProgressTarget");
            var music = AudioKit.PlayMusic(_longClip);
            var voice = AudioKit.PlayVoice(_longClip, true);
            var sound = AudioKit.PlaySound(_longClip, Options(true));
            var fixedSound = AudioKit.PlaySoundAt(_longClip, Vector3.one, Options(true));
            var followingSound = AudioKit.PlaySoundFollowing(_longClip, target.transform, Options(true));
            yield return new WaitForSecondsRealtime(.18f);
            foreach (var handle in new[] { music, voice, sound, fixedSound, followingSound })
            {
                handle.Pause();
                Assert.IsTrue(handle.IsValid);
                Assert.Greater(handle.Time, .03f);
                Assert.AreEqual(_longClip.length, handle.Duration, .001f);
                Assert.That(handle.Progress, Is.InRange(0f, 1f));
                Assert.AreEqual(handle.Time / handle.Duration, handle.Progress, .001f);
            }
            var fixedVoice = AudioKit.PlayVoiceAt(_longClip, Vector3.one, Options(true));
            yield return new WaitForSecondsRealtime(.18f);
            fixedVoice.Pause();
            Assert.Greater(fixedVoice.Time, .03f);
            Assert.AreEqual(_longClip.length, fixedVoice.Duration, .001f);
            Assert.IsFalse(voice.IsValid);
            Assert.AreEqual(0f, voice.Time);
            Assert.AreEqual(0f, voice.Duration);
            Assert.AreEqual(0f, voice.Progress);
            var followingVoice = AudioKit.PlayVoiceFollowing(_longClip, target.transform, Options(true));
            yield return new WaitForSecondsRealtime(.18f);
            followingVoice.Pause();
            Assert.Greater(followingVoice.Time, .03f);
            Assert.AreEqual(_longClip.length, followingVoice.Duration, .001f);
            Assert.AreEqual(followingVoice.Time / followingVoice.Duration, followingVoice.Progress, .001f);
        }

        [UnityTest]
        public IEnumerator MutedPlaybackAdvancesAndUnmuteDoesNotRestartOrRestoreStaleVolume()
        {
            var settings = AudioKit.Settings;
            var music = AudioKit.PlayMusic(_longClip);
            var voice = AudioKit.PlayVoice(_longClip, true);
            var sound = AudioKit.PlaySound(_longClip, Options(true));
            yield return new WaitForSecondsRealtime(.12f);
            var positions = new[] { music.Time, voice.Time, sound.Time };
            var handles = new[] { music, voice, sound };
            settings.IsMusicMuted.Value = true;
            settings.IsVoiceMuted.Value = true;
            settings.IsSoundMuted.Value = true;
            settings.MusicVolume.Value = .4f;
            settings.VoiceVolume.Value = .6f;
            settings.SoundVolume.Value = .8f;
            foreach (var handle in handles)
            {
                handle.SetVolumeScale(.5f);
            }

            yield return new WaitForSecondsRealtime(.12f);
            for (var i = 0; i < handles.Length; i++)
            {
                Assert.IsTrue(handles[i].IsPlaying);
                Assert.IsFalse(handles[i].IsPaused);
                Assert.IsTrue(handles[i].Voice.Source.mute);
                Assert.Greater(handles[i].Time, positions[i] + .04f);
                Assert.AreEqual(_longClip.length, handles[i].Duration, .001f);
                handles[i].Pause();
                positions[i] = handles[i].Time;
            }
            settings.IsMusicMuted.Value = false;
            settings.IsVoiceMuted.Value = false;
            settings.IsSoundMuted.Value = false;
            var volumes = new[] { .2f, .3f, .4f };
            for (var i = 0; i < handles.Length; i++)
            {
                Assert.IsTrue(handles[i].IsPaused);
                Assert.AreEqual(positions[i], handles[i].Time, .04f);
                Assert.IsFalse(handles[i].Voice.Source.mute);
                Assert.AreEqual(volumes[i], handles[i].Voice.Source.volume, .001f);
                handles[i].Resume();
            }
            yield return new WaitForSecondsRealtime(.12f);
            for (var i = 0; i < handles.Length; i++)
            {
                Assert.Greater(handles[i].Time, positions[i] + .04f);
            }

            Assert.IsTrue(settings.IsOn);
        }

        [UnityTest]
        public IEnumerator MutedRequestsStartWithContextAndCompleteNaturallyBeforeCleanup()
        {
            var settings = AudioKit.Settings;
            settings.IsMusicMuted.Value = true;
            settings.IsVoiceMuted.Value = true;
            settings.IsSoundMuted.Value = true;
            var starts = new List<AudioHandle>();
            var completed = new List<AudioHandle>();
            Action<AudioClip, AudioHandle> onStarted = (clip, callbackHandle) =>
            {
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                Assert.IsTrue(callbackHandle.Voice.Source.mute);
                starts.Add(callbackHandle);
            };
            Action<AudioClip, AudioHandle> onFinished = (clip, callbackHandle) =>
            {
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                Assert.IsFalse(callbackHandle.IsPlaying);
                Assert.IsTrue(callbackHandle.Voice.Source.mute);
                Assert.AreEqual(_shortClip.length, callbackHandle.Duration, .001f);
                Assert.That(callbackHandle.Progress, Is.InRange(0f, 1f));
                completed.Add(callbackHandle);
            };
            var music = AudioKit.PlayMusic(_shortClip, false, onStarted: onStarted, onFinished: onFinished);
            var voice = AudioKit.PlayVoice(_shortClip, onStarted: onStarted, onFinished: onFinished);
            var sound = AudioKit.PlaySound(_shortClip, onFinished: onFinished);
            CollectionAssert.AreEquivalent(new[] { music, voice }, starts, "Starts are synchronous, before API return.");
            foreach (var handle in new[] { music, voice, sound })
            {
                yield return WaitForFinish(handle);
            }

            CollectionAssert.AreEquivalent(new[] { music, voice, sound }, completed);
            foreach (var handle in completed)
            {
                Assert.IsFalse(handle.IsValid);
                Assert.AreEqual(0f, handle.Time);
                Assert.AreEqual(0f, handle.Duration);
                Assert.AreEqual(0f, handle.Progress);
            }
            yield return null;
            Assert.AreEqual(3, completed.Count);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator LoopProgressWrapsWithinTheCurrentCycleEvenWhileMuted()
        {
            var clip = AudioClip.Create("loop-progress", 11025, 1, 44100, false);
            var handle = default(AudioHandle);
            try
            {
                AudioKit.Settings.IsSoundMuted.Value = true;
                var finished = 0;
                handle = AudioKit.PlaySound(clip, Options(true), (_, __) => finished++);
                var previous = 0f;
                var advanced = false;
                var wrapped = false;
                var deadline = Time.realtimeSinceStartup + 2f;
                while (!wrapped && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    var position = handle.Time;
                    Assert.That(position, Is.InRange(0f, clip.length));
                    Assert.That(handle.Progress, Is.InRange(0f, 1f));
                    Assert.AreEqual(clip.length, handle.Duration, .001f);
                    advanced |= position > .04f;
                    wrapped |= previous > position + .03f;
                    previous = position;
                }
                Assert.IsTrue(advanced, "A muted loop must still advance.");
                Assert.IsTrue(wrapped, "Loop progress must return to the beginning of the clip rather than accumulate.");
                Assert.IsTrue(handle.IsPlaying);
                Assert.AreEqual(0, finished);
            }
            finally
            {
                handle.Stop();
                Object.Destroy(clip);
            }
        }

        [UnityTest]
        public IEnumerator PitchChangesPositionSpeedButNeverClipDuration()
        {
            var clip = AudioClip.Create("pitch-progress", 176400, 1, 44100, false);
            var slow = default(AudioHandle);
            var fast = default(AudioHandle);
            try
            {
                var options = Options(true);
                options.Pitch = .5f;
                slow = AudioKit.PlaySound(clip, options);
                options.Pitch = 2f;
                fast = AudioKit.PlaySound(clip, options);
                yield return new WaitForSecondsRealtime(.25f);
                slow.Pause();
                fast.Pause();
                Assert.Greater(slow.Time, .03f);
                Assert.Greater(fast.Time, slow.Time + .1f);
                Assert.AreEqual(clip.length, slow.Duration, .001f);
                Assert.AreEqual(clip.length, fast.Duration, .001f);
                Assert.AreEqual(slow.Time / clip.length, slow.Progress, .001f);
                Assert.AreEqual(fast.Time / clip.length, fast.Progress, .001f);
            }
            finally
            {
                slow.Stop();
                fast.Stop();
                Object.Destroy(clip);
            }
        }

        [UnityTest]
        public IEnumerator PendingFollowingVoiceUsesOriginalContextAndCurrentMuteOnlyOnceOnReplay()
        {
            var target = OwnObject("AudioKit.PendingContextTarget");
            var settings = AudioKit.Settings;
            settings.IsVoiceOn.Value = false;
            settings.IsVoiceMuted.Value = true;
            var calls = 0;
            var started = default(AudioHandle);
            var completed = default(AudioHandle);
            var finishes = 0;
            var handle = AudioKit.PlayVoiceFollowing(_longClip, target.transform, onStarted: (clip, callbackHandle) =>
            {
                calls++;
                started = callbackHandle;
                Assert.AreSame(_longClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                Assert.IsTrue(callbackHandle.Voice.Source.mute);
                Assert.AreEqual(target.transform.position, callbackHandle.Voice.Source.transform.position);
            }, onFinished: (clip, callbackHandle) =>
            {
                finishes++;
                completed = callbackHandle;
                Assert.AreSame(_longClip, clip);
                Assert.AreEqual(started, callbackHandle);
                Assert.IsTrue(callbackHandle.IsValid);
                Assert.IsFalse(callbackHandle.IsPlaying);
            });
            Assert.AreEqual(0, calls);
            Assert.AreEqual(0f, handle.Time);
            Assert.AreEqual(_longClip.length, handle.Duration, .001f);
            Assert.AreEqual(0f, handle.Progress);
            target.transform.position = Vector3.one;
            settings.IsVoiceOn.Value = true;
            Assert.AreEqual(handle, started);
            Assert.AreEqual(1, calls);
            yield return new WaitForSecondsRealtime(.12f);
            Assert.Greater(handle.Time, .03f);
            settings.IsVoiceOn.Value = false;
            Assert.AreEqual(0f, handle.Time);
            Assert.AreEqual(_longClip.length, handle.Duration, .001f);
            settings.IsVoiceOn.Value = true;
            yield return null;
            Assert.AreEqual(1, calls);
            Assert.IsTrue(handle.Voice.Source.mute);
            Assert.IsTrue(handle.IsPlaying);
            yield return WaitForFinish(handle);
            Assert.AreEqual(handle, completed);
            Assert.AreEqual(1, finishes);
            Assert.AreEqual(1, calls);
        }

        [UnityTest]
        public IEnumerator RandomFinishContextUsesSelectedClipIdentityAndOriginalGeneration()
        {
            var other = AudioClip.Create(_shortClip.name, 2205, 1, 44100, false);
            var state = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(731);
                for (var i = 0; i < 8; i++)
                {
                    var calls = 0;
                    var handle = default(AudioHandle);
                    AudioClip selected = null;
                    handle = AudioKit.PlayRandomSound(new[] { _shortClip, null, other }, onFinished: (clip, callbackHandle) =>
                    {
                        calls++;
                        Assert.AreSame(selected, clip, "Same-name clips must be distinguished by object identity.");
                        Assert.AreSame(clip, callbackHandle.Voice.Source.clip);
                        Assert.AreEqual(handle, callbackHandle);
                        Assert.IsTrue(callbackHandle.IsValid);
                        Assert.IsFalse(callbackHandle.IsPlaying);
                    });
                    selected = handle.Voice.Source.clip;
                    CollectionAssert.Contains(new[] { _shortClip, other }, selected);
                    yield return WaitForFinish(handle);
                    Assert.AreEqual(1, calls);
                    Assert.IsFalse(handle.IsValid);
                }
            }
            finally
            {
                UnityEngine.Random.state = state;
                Object.Destroy(other);
            }
        }

        [UnityTest]
        public IEnumerator FollowingSoundFinishCanStopAndReuseItsOwnGenerationBeforeOldCleanup()
        {
            var target = OwnObject("AudioKit.FinishReuseTarget");
            var next = default(AudioHandle);
            var completed = default(AudioHandle);
            var calls = 0;
            var first = AudioKit.PlaySoundFollowing(_shortClip, target.transform, onFinished: (clip, callbackHandle) =>
            {
                calls++;
                completed = callbackHandle;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                callbackHandle.Stop();
                Assert.IsFalse(callbackHandle.IsValid);
                next = AudioKit.PlaySound(_longClip, Options(true));
                Assert.AreSame(callbackHandle.Voice, next.Voice);
                Assert.AreNotEqual(callbackHandle.Generation, next.Generation);
                callbackHandle.Pause();
                callbackHandle.Stop();
                Assert.IsTrue(next.IsValid);
            });
            yield return WaitForFinish(first);
            Assert.AreEqual(first, completed);
            yield return null;
            Assert.AreEqual(1, calls);
            Assert.IsTrue(next.IsPlaying);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator FinishCallbackCanShutdownAndStartANewSessionBeforeReturning()
        {
            var next = default(AudioHandle);
            var calls = 0;
            var first = AudioKit.PlaySound(_shortClip, onFinished: (clip, callbackHandle) =>
            {
                calls++;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                AudioKit.Shutdown();
                Assert.IsFalse(callbackHandle.IsValid);
                AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
                next = AudioKit.PlaySound(_longClip, Options(true));
                callbackHandle.Stop();
            });
            yield return WaitForFinish(first);
            yield return null;
            Assert.AreEqual(1, calls);
            Assert.IsTrue(next.IsPlaying);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator ThrowingFinishAfterStopAndReuseDoesNotCleanTheNewRequest()
        {
            var next = default(AudioHandle);
            var calls = 0;
            LogAssert.Expect(LogType.Exception, new Regex("expected reentrant audio callback failure"));
            var first = AudioKit.PlaySound(_shortClip, onFinished: (clip, callbackHandle) =>
            {
                calls++;
                Assert.AreSame(_shortClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                callbackHandle.Stop();
                next = AudioKit.PlaySound(_longClip, Options(true));
                Assert.AreSame(callbackHandle.Voice, next.Voice);
                throw new InvalidOperationException("expected reentrant audio callback failure");
            });
            yield return WaitForFinish(first);
            yield return null;
            Assert.AreEqual(1, calls);
            Assert.IsTrue(next.IsPlaying);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator StartCallbackCanStopOrShutdownUsingItsHandleBeforeApiReturns()
        {
            var calls = 0;
            var started = default(AudioHandle);
            var first = AudioKit.PlayVoice(_longClip, onStarted: (clip, callbackHandle) =>
            {
                calls++;
                started = callbackHandle;
                Assert.AreSame(_longClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                callbackHandle.Stop();
                Assert.IsFalse(callbackHandle.IsValid);
            });
            Assert.AreEqual(first, started);
            Assert.IsFalse(first.IsValid);
            var next = AudioKit.PlayVoice(_longClip, true);
            first.Stop();
            Assert.IsTrue(next.IsValid);
            var last = AudioKit.PlayMusic(_longClip, onStarted: (clip, callbackHandle) =>
            {
                calls++;
                started = callbackHandle;
                Assert.AreSame(_longClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                AudioKit.Shutdown();
                Assert.IsFalse(callbackHandle.IsValid);
            });
            Assert.AreEqual(last, started);
            Assert.IsFalse(next.IsValid);
            Assert.IsFalse(last.IsValid);
            Assert.IsFalse(AudioManager.HasInstance);
            yield return null;
            Assert.AreEqual(2, calls);
        }

        [UnityTest]
        public IEnumerator ThrowingStartCallbackCleansOnlyItsOriginalGeneration()
        {
            var failed = default(AudioHandle);
            Assert.Throws<InvalidOperationException>(() => AudioKit.PlayVoice(_longClip, onStarted: (clip, callbackHandle) =>
            {
                failed = callbackHandle;
                Assert.AreSame(_longClip, clip);
                Assert.IsTrue(callbackHandle.IsValid);
                throw new InvalidOperationException("expected start failure");
            }));
            Assert.IsFalse(failed.IsValid);
            Assert.IsFalse(AudioKit.Voice.IsValid);
            var next = default(AudioHandle);
            Assert.Throws<InvalidOperationException>(() => AudioKit.PlayMusic(_shortClip, false, onStarted: (clip, callbackHandle) =>
            {
                failed = callbackHandle;
                Assert.AreSame(_shortClip, clip);
                next = AudioKit.PlayMusic(_longClip);
                Assert.IsFalse(callbackHandle.IsValid);
                throw new InvalidOperationException("expected replacement start failure");
            }));
            Assert.IsFalse(failed.IsValid);
            Assert.AreEqual(next, AudioKit.Music);
            yield return null;
            Assert.IsTrue(next.IsPlaying);
        }

        private static IEnumerator WaitForFinish(AudioHandle handle)
        {
            var deadline = Time.realtimeSinceStartup + 2f;
            while (handle.IsValid && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsFalse(handle.IsValid, "Playback did not complete within the test window.");
        }
    }
}
