using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioCleanupTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("cleanup", 44100, 1, 44100, false);
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_clip);
        }

        [Test]
        public void InactiveOrDisabledSourceEndsLoopWithoutNaturalNotification()
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            var finishes = 0;
            var inactive = AudioKit.PlaySound(_clip, options, (_, __) => finishes++);
            inactive.Voice.Source.gameObject.SetActive(false);
            inactive.Voice.CheckFinished();
            Assert.IsFalse(inactive.IsValid);
            var disabled = AudioKit.PlaySound(_clip, options, (_, __) => finishes++);
            disabled.Voice.Source.enabled = false;
            disabled.Voice.CheckFinished();
            Assert.IsFalse(disabled.IsValid);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.AreEqual(0, finishes);
            Assert.IsTrue(_clip);
        }

        [Test]
        public void EmptyQueriesAndCleanupNeverCreateAHost()
        {
            AudioKit.Shutdown();
            var list = new List<AudioHandle> { default };
            AudioKit.CopyPlayingSounds(list);
            Assert.IsEmpty(list);
            Assert.IsEmpty(AudioKit.GetPlayingSounds());
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.IsFalse(AudioKit.Music.IsValid);
            Assert.IsFalse(AudioKit.Voice.IsValid);
            Assert.AreEqual(0f, AudioKit.Music.Time);
            Assert.AreEqual(0f, AudioKit.Music.Duration);
            Assert.AreEqual(0f, AudioKit.Music.Progress);
            Assert.AreEqual(0f, AudioKit.Voice.Time);
            Assert.AreEqual(0f, AudioKit.Voice.Duration);
            Assert.AreEqual(0f, AudioKit.Voice.Progress);
            var empty = default(AudioHandle);
            Assert.AreEqual(0f, empty.Time);
            Assert.AreEqual(0f, empty.Duration);
            Assert.AreEqual(0f, empty.Progress);
            Assert.IsNull(AudioKit.CurrentSettings, "Progress queries must not initialize settings.");
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.StopMusic();
            AudioKit.PauseMusic();
            AudioKit.ResumeMusic();
            AudioKit.StopVoice();
            AudioKit.PauseVoice();
            AudioKit.ResumeVoice();
            AudioKit.StopAllSound();
            AudioKit.Shutdown();
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsEmpty(Object.FindObjectsOfType<AudioManager>(true));
        }

        [Test]
        public void ShutdownInvalidatesHandlesAndDestroysOwnedSourcesButNotBorrowedClip()
        {
            var sound = AudioKit.PlaySound(_clip);
            sound.Pause();
            var music = AudioKit.PlayMusic(_clip);
            music.Pause();
            var voice = AudioKit.PlayVoice(_clip);
            voice.Pause();
            var sources = new[] { sound.Voice.Source, music.Voice.Source, voice.Voice.Source };
            AudioKit.Shutdown();
            AudioKit.Shutdown();
            Assert.IsFalse(sound.IsValid);
            Assert.IsFalse(music.IsValid);
            Assert.IsFalse(voice.IsValid);
            foreach (var handle in new[] { sound, music, voice })
            {
                Assert.AreEqual(0f, handle.Time);
                Assert.AreEqual(0f, handle.Duration);
                Assert.AreEqual(0f, handle.Progress);
            }
            foreach (var source in sources)
            {
                Assert.IsFalse(source);
            }

            Assert.IsTrue(_clip);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(0, AudioKit.SoundCount);
            // A new session reloads the user's save rather than the fixture's silent overrides.
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            var current = AudioKit.PlaySound(_clip);
            Assert.IsTrue(current.IsValid);
            current.Pause();
            sound.Stop();
            music.Stop();
            voice.Stop();
            Assert.IsTrue(current.IsValid, "Old session handles must not control a rebuilt host.");
            Assert.AreEqual(1, Object.FindObjectsOfType<AudioManager>(true).Length);
        }

        [Test]
        public void ExternalHostDestructionInvalidatesAllPlayback()
        {
            var root = new GameObject("AudioKit.ExternalOwner");
            root.SetActive(false);
            var manager = root.AddComponent<AudioManager>();
            try
            {
                Assert.AreSame(manager, AudioManager.Instance);
                var sound = AudioKit.PlaySound(_clip);
                sound.Pause();
                var source = sound.Voice.Source;
                Object.DestroyImmediate(root);
                AudioKit.Shutdown();
                Assert.IsFalse(sound.IsValid);
                Assert.IsFalse(source);
                Assert.IsFalse(AudioManager.HasInstance);
                Assert.IsTrue(_clip);
            }
            finally
            {
                if (root)
                {
                    Object.DestroyImmediate(root);
                }
            }
        }

        [Test]
        public void ShutdownFindsActualOwnerEvenAfterHostReferenceWasReset()
        {
            var sound = AudioKit.PlaySound(_clip);
            sound.Pause();
            var source = sound.Voice.Source;
            var reset = typeof(YLFramework.Singletons.MonoSingletonHost<AudioManager>)
                .GetMethod("ResetHostReference", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(reset);
            reset.Invoke(null, null);
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.Shutdown();
            Assert.IsFalse(sound.IsValid);
            Assert.IsFalse(source);
            Assert.IsEmpty(Object.FindObjectsOfType<AudioManager>(true));
        }

        [Test]
        public void IdlePoolIsBoundedLifoAndDuplicateStopCannotReturnTwice()
        {
            var sounds = new AudioHandle[12];
            var sources = new AudioSource[12];
            var callbacks = 0;
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            for (var i = 0; i < sounds.Length; i++)
            {
                sounds[i] = AudioKit.PlaySound(_clip, options, (_, __) => callbacks++);
                sounds[i].Pause();
                sources[i] = sounds[i].Voice.Source;
            }
            Assert.AreEqual(12, AudioKit.SoundCount, "Idle capacity is not an active concurrency limit.");
            foreach (var sound in sounds)
            {
                sound.Stop();
                sound.Stop();
            }
            for (var i = 0; i < 10; i++)
            {
                Assert.IsFalse(sounds[i].IsValid);
                Assert.IsTrue(sources[i]);
                Assert.IsNull(sources[i].clip);
                Assert.IsFalse(sources[i].gameObject.activeSelf);
                Assert.IsFalse(sources[i].isPlaying);
            }
            Assert.IsFalse(sources[10]);
            Assert.IsFalse(sources[11]);
            Assert.AreEqual(0, AudioKit.SoundCount);
            AudioKit.Settings.SoundVolume.Value = .3f;
            for (var i = 0; i < 10; i++)
            {
                Assert.AreEqual(1f, sources[i].volume);
            }

            var voices = new HashSet<AudioVoice>();
            for (var i = 0; i < 12; i++)
            {
                var current = AudioKit.PlaySound(_clip, options);
                current.Pause();
                Assert.IsTrue(voices.Add(current.Voice), "Duplicate Stop cannot cache one voice twice.");
                if (i < 10)
                {
                    Assert.AreSame(sounds[9 - i].Voice, current.Voice);
                }

                Assert.AreEqual(.3f, current.Voice.Source.volume, .001f);
            }
            AudioKit.Shutdown();
            foreach (var source in sources)
            {
                Assert.IsFalse(source);
            }

            Assert.AreEqual(0, callbacks);
            Assert.IsTrue(_clip);
        }

        [Test]
        public void OldHandleCannotStopPauseOrResumeReusedVoice()
        {
            var old = AudioKit.PlaySound(_clip);
            old.Pause();
            var oldGeneration = old.Generation;
            old.Stop();
            var current = AudioKit.PlaySound(_clip);
            current.Pause();
            Assert.AreSame(old.Voice, current.Voice);
            Assert.Greater(current.Generation, oldGeneration);
            Assert.AreEqual(current.Generation, current.Voice.Generation);
            old.Stop();
            old.Pause();
            old.Resume();
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(0f, old.Time);
            Assert.AreEqual(0f, old.Duration);
            Assert.AreEqual(0f, old.Progress);
            Assert.AreEqual(_clip.length, current.Duration, .001f);
            Assert.IsTrue(current.IsValid);
            Assert.IsTrue(current.IsPaused);
            Assert.AreEqual(1, AudioKit.SoundCount);
            current.Stop();
            Assert.AreEqual(0f, current.Time);
            Assert.AreEqual(0f, current.Duration);
            Assert.AreEqual(0f, current.Progress);
        }

        [Test]
        public void SoundReturnClearsPlaybackReferencesButKeepsBorrowedClipAndTargetAlive()
        {
            var target = new GameObject("AudioCleanup.BorrowedTarget");
            try
            {
                var callbacks = 0;
                var sound = AudioKit.PlaySoundFollowing(_clip, target.transform, AudioSoundOptions.Defaults,
                    (_, __) => callbacks++);
                var voice = sound.Voice;
                var source = voice.Source;
                sound.Stop();
                Assert.IsFalse(sound.IsValid);
                Assert.IsNull(voice.Clip);
                Assert.IsFalse(voice.IsFollowing);
                Assert.AreEqual(0, voice.ClipId);
                Assert.AreEqual(0, voice.Priority);
                foreach (var field in new[] { "_target", "_onStarted", "_onFinished" })
                {
                    Assert.IsNull(typeof(AudioVoice).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(voice), field);
                }

                Assert.IsTrue(source);
                Assert.IsNull(source.clip);
                Assert.IsNull(source.outputAudioMixerGroup);
                Assert.IsFalse(source.gameObject.activeSelf);
                AudioKit.SoundCacheCapacity = 0;
                Assert.IsFalse(source);
                Assert.IsTrue(_clip);
                Assert.IsTrue(target);
                Assert.AreEqual(0, callbacks);
            }
            finally
            {
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void ExplicitSlotStopClearsDisabledRequestAndReplacementChangesGeneration()
        {
            AudioKit.Settings.IsMusicOn.Value = false;
            var old = AudioKit.PlayMusic(_clip, false, .3f);
            var current = AudioKit.PlayMusic(_clip, true, .7f);
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(0f, old.Time);
            Assert.AreEqual(0f, old.Duration);
            Assert.AreEqual(0f, old.Progress);
            Assert.AreEqual(0f, current.Time);
            Assert.AreEqual(_clip.length, current.Duration, .001f);
            Assert.AreEqual(0f, current.Progress);
            Assert.AreNotEqual(old.Generation, current.Generation);
            old.Stop();
            Assert.IsTrue(current.IsValid);
            current.Stop();
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.IsFalse(AudioKit.Music.IsValid);
            Assert.IsNull(current.Voice.Source.clip);
        }

        [Test]
        public void ProgressIsZeroWhenBorrowedClipOrOwnedSourceIsDestroyed()
        {
            var clip = AudioClip.Create("destroyed-progress", 44100, 1, 44100, false);
            try
            {
                var missingClip = AudioKit.PlaySound(clip);
                missingClip.Pause();
                Assert.AreEqual(1f, missingClip.Duration, .001f);
                Object.DestroyImmediate(clip);
                Assert.AreEqual(0f, missingClip.Time);
                Assert.AreEqual(0f, missingClip.Duration);
                Assert.AreEqual(0f, missingClip.Progress);
                var clearedClip = AudioKit.PlaySound(_clip);
                clearedClip.Pause();
                clearedClip.Voice.Source.clip = null;
                Assert.AreEqual(0f, clearedClip.Time);
                Assert.AreEqual(0f, clearedClip.Duration);
                Assert.AreEqual(0f, clearedClip.Progress);
                var missingSource = AudioKit.PlayVoice(_clip);
                missingSource.Pause();
                Object.DestroyImmediate(missingSource.Voice.Source.gameObject);
                Assert.AreEqual(0f, missingSource.Time);
                Assert.AreEqual(0f, missingSource.Duration);
                Assert.AreEqual(0f, missingSource.Progress);
            }
            finally
            {
                if (clip)
                {
                    Object.DestroyImmediate(clip);
                }
            }
        }
    }
}
