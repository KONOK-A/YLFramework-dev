using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioCoreTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("same-name", 44100, 1, 44100, false);
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_clip);
        }

        [Test]
        public void GlobalWindowIsSharedInclusiveAndOnlyRecordCommits()
        {
            var deduplicator = new SoundDeduplicator { GlobalFrameCountForIgnoreSameSound = 10 };
            var mode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 100));
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 100), "Validation must not consume a window.");
            deduplicator.Record(1, mode, 100, default);
            Assert.IsTrue(deduplicator.CanPlay(2, mode, 109));
            deduplicator.Record(2, mode, 109, default);
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 110));
            Assert.IsFalse(deduplicator.CanPlay(2, mode, 110));
            Assert.IsTrue(deduplicator.CanPlay(2, mode, 111));
            deduplicator.Record(2, mode, 111, default);
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 111));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void AllDeclaredGlobalPlayModesAreAccepted(int value)
        {
            AudioKit.PlaySoundMode = (AudioKit.PlaySoundModes)value;
            Assert.AreEqual((AudioKit.PlaySoundModes)value, AudioKit.PlaySoundMode);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [TestCase(-1)]
        [TestCase(3)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void InvalidGlobalPlayModesDoNotInitializeOrChangeConfiguration(int value)
        {
            AudioKit.Shutdown();
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySoundMode = (AudioKit.PlaySoundModes)value);
            Assert.AreEqual("value", error.ParamName);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySoundMode = (AudioKit.PlaySoundModes)value);
            Assert.AreEqual(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames, AudioKit.PlaySoundMode);
        }

        [Test]
        public void PerClipWindowDoesNotUseGlobalStartAndPrunePreservesBoundary()
        {
            var deduplicator = new SoundDeduplicator { SoundFrameCountForIgnoreSameSound = 10 };
            var mode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            deduplicator.Record(1, mode, 100, default);
            deduplicator.Record(2, mode, 109, default);
            deduplicator.Prune(110);
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 110));
            deduplicator.Prune(111);
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 111));
            Assert.IsFalse(deduplicator.CanPlay(2, mode, 111));
            Assert.IsFalse(deduplicator.CanPlay(2, mode, 119));
            deduplicator.Prune(120);
            Assert.IsTrue(deduplicator.CanPlay(2, mode, 120));
        }

        [Test]
        public void PruneActuallyReleasesExpiredIndependentClipRecords()
        {
            var deduplicator = new SoundDeduplicator { SoundFrameCountForIgnoreSameSound = 10 };
            var mode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            deduplicator.Record(1, mode, 100, default);
            deduplicator.Record(2, mode, 109, default);
            var field = typeof(SoundDeduplicator).GetField("_sounds", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            var records = (System.Collections.IDictionary)field.GetValue(deduplicator);
            deduplicator.Prune(110);
            Assert.AreEqual(2, records.Count, "The inclusive boundary must remain recorded.");
            deduplicator.Prune(121);
            Assert.AreEqual(0, records.Count, "Expired records must not accumulate indefinitely.");
        }

        [Test]
        public void EveryOneHasNoWindowAndOldFinishCannotRemoveNewGeneration()
        {
            var deduplicator = new SoundDeduplicator();
            var mode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            var old = AudioKit.PlaySound(_clip);
            old.Pause();
            old.Stop();
            var current = AudioKit.PlaySound(_clip);
            current.Pause();
            Assert.AreSame(old.Voice, current.Voice);
            Assert.AreNotEqual(old.Generation, current.Generation);
            deduplicator.Record(_clip.GetInstanceID(), mode, 100, old);
            deduplicator.Record(_clip.GetInstanceID(), mode, 111, current);
            deduplicator.Finished(_clip.GetInstanceID(), old);
            Assert.IsFalse(deduplicator.CanPlay(_clip.GetInstanceID(), mode, 111));
            Assert.IsTrue(deduplicator.CanPlay(_clip.GetInstanceID(), AudioKit.PlaySoundModes.EveryOne, 111));
            deduplicator.Finished(_clip.GetInstanceID(), current);
            Assert.IsTrue(deduplicator.CanPlay(_clip.GetInstanceID(), mode, 111));
        }

        [Test]
        public void ClipIdentityDoesNotDependOnNameAndSnapshotsDoNotExposePoolObjects()
        {
            var other = AudioClip.Create(_clip.name, 44100, 1, 44100, false);
            try
            {
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
                var first = AudioKit.PlaySound(_clip);
                first.Pause();
                _clip.name = "  ";
                Assert.IsFalse(AudioKit.PlaySound(_clip).IsValid);
                var second = AudioKit.PlaySound(other);
                second.Pause();
                Assert.IsTrue(second.IsValid);
                var snapshot = AudioKit.GetPlayingSounds();
                Assert.AreEqual(2, AudioKit.SoundCount);
                var reusable = new List<AudioHandle> { default };
                AudioKit.CopyPlayingSounds(reusable);
                CollectionAssert.AreEquivalent(snapshot, reusable);
                first.Stop();
                Assert.AreEqual(2, snapshot.Length);
                Assert.IsFalse(first.IsValid);
                AudioKit.CopyPlayingSounds(reusable);
                Assert.AreEqual(1, reusable.Count);
                Assert.AreEqual(second, reusable[0]);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void RandomSelectionIgnoresNullEntriesAndUsesSelectedClipDeduplication()
        {
            Assert.IsFalse(AudioKit.PlayRandomSound(null).IsValid);
            Assert.IsFalse(AudioKit.PlayRandomSound(Array.Empty<AudioClip>()).IsValid);
            Assert.IsFalse(AudioKit.PlayRandomSound(new AudioClip[] { null, null }).IsValid);
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            var first = AudioKit.PlayRandomSound(new[] { null, _clip, null });
            first.Pause();
            Assert.IsTrue(first.IsValid);
            Assert.AreSame(_clip, first.Voice.Source.clip);
            Assert.IsFalse(AudioKit.PlayRandomSound(new[] { _clip }).IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [Test]
        public void RandomSelectionCanReachEveryValidClip()
        {
            var other = AudioClip.Create("other", 44100, 1, 44100, false);
            var state = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(731);
                var seen = new HashSet<AudioClip>();
                for (var i = 0; i < 128; i++)
                {
                    var handle = AudioKit.PlayRandomSound(new[] { _clip, null, other });
                    Assert.IsTrue(handle.IsValid);
                    seen.Add(handle.Voice.Source.clip);
                    handle.Stop();
                }
                CollectionAssert.AreEquivalent(new[] { _clip, other }, seen);
            }
            finally
            {
                UnityEngine.Random.state = state;
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void RejectedRequestsDoNotAllocateOrConsumeDeduplication()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            Assert.IsFalse(AudioKit.PlaySound(null).IsValid);
            Assert.IsFalse(AudioKit.PlaySoundFollowing(_clip, null).IsValid);
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.IsFalse(AudioKit.PlaySound(_clip).IsValid);
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.Settings.IsSoundOn.Value = true;
            var accepted = AudioKit.PlaySound(_clip);
            accepted.Pause();
            Assert.IsTrue(accepted.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [Test]
        public void SettingsUseOriginalKeysAndDisconnectOldSubscriptionsAcrossRecreate()
        {
            AudioKit.Shutdown();
            foreach (var key in AudioTestSettings.Keys)
            {
                PlayerPrefs.DeleteKey(key);
            }

            var settings = AudioKit.Settings;
            var switches = new[] { settings.IsSoundOn, settings.IsMusicOn, settings.IsVoiceOn };
            var volumes = new[] { settings.SoundVolume, settings.MusicVolume, settings.VoiceVolume };
            Assert.IsTrue(settings.IsOn);
            Assert.AreSame(settings, AudioKit.Settings);
            Assert.AreEqual(5, AudioKit.GlobalFrameCountForIgnoreSameSound);
            Assert.AreEqual(5, AudioKit.SoundFrameCountForIgnoreSameSound);
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(switches[i].Value);
                Assert.AreEqual(1f, volumes[i].Value);
                Assert.IsFalse(PlayerPrefs.HasKey(AudioTestSettings.Keys[i]));
                Assert.IsFalse(PlayerPrefs.HasKey(AudioTestSettings.Keys[i + 3]));
            }
            settings.IsOn = false;
            var calls = 0;
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(0, PlayerPrefs.GetInt(AudioTestSettings.Keys[i]));
                volumes[i].Value = .2f + i * .1f;
                switches[i].Register(_ => calls++);
                volumes[i].Register(_ => calls++);
            }
            AudioKit.Shutdown();
            var recreated = AudioKit.Settings;
            Assert.AreNotSame(settings, recreated);
            Assert.AreEqual(5, AudioKit.GlobalFrameCountForIgnoreSameSound);
            Assert.AreEqual(5, AudioKit.SoundFrameCountForIgnoreSameSound);
            Assert.IsFalse(recreated.IsOn);
            var restored = new[] { recreated.SoundVolume, recreated.MusicVolume, recreated.VoiceVolume };
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(.2f + i * .1f, restored[i].Value);
            }

            settings.IsOn = true;
            foreach (var volume in volumes)
            {
                volume.Value = .8f;
            }

            Assert.AreEqual(0, calls, "Shutdown must detach all old callbacks.");
            Assert.IsFalse(recreated.IsOn, "Old properties cannot mutate the recreated in-memory settings.");
            Assert.IsFalse(AudioManager.HasInstance, "Settings alone must not create a playback host.");
        }

        [Test]
        public void MuteDefaultsAreTransientAndDoNotWriteExistingSaveKeys()
        {
            var comparer = YLFramework.Binding.BindableProperty<bool>.Comparer;
            AudioKit.Shutdown();
            foreach (var key in AudioTestSettings.Keys)
            {
                PlayerPrefs.DeleteKey(key);
            }

            var settings = AudioKit.Settings;
            var mutes = new[] { settings.IsMusicMuted, settings.IsVoiceMuted, settings.IsSoundMuted };
            foreach (var mute in mutes)
            {
                Assert.AreEqual(typeof(YLFramework.Binding.BindableProperty<bool>), mute.GetType());
                Assert.IsFalse(mute.Value);
                mute.Value = true;
            }
            Assert.IsTrue(settings.IsOn, "Mute must not change channel switches.");
            Assert.AreEqual(1f, settings.MusicVolume.Value);
            Assert.AreEqual(1f, settings.VoiceVolume.Value);
            Assert.AreEqual(1f, settings.SoundVolume.Value);
            foreach (var key in AudioTestSettings.Keys)
            {
                Assert.IsFalse(PlayerPrefs.HasKey(key), key);
            }

            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.Shutdown();
            var recreated = AudioKit.Settings;
            Assert.IsFalse(recreated.IsMusicMuted.Value);
            Assert.IsFalse(recreated.IsVoiceMuted.Value);
            Assert.IsFalse(recreated.IsSoundMuted.Value);
            Assert.AreSame(comparer, YLFramework.Binding.BindableProperty<bool>.Comparer);
            foreach (var key in AudioTestSettings.Keys)
            {
                Assert.IsFalse(PlayerPrefs.HasKey(key), key);
            }
        }

        [Test]
        public void ShutdownDetachesMuteSubscriptionsAndOldSettingsCannotMuteNewHost()
        {
            var oldSettings = AudioKit.Settings;
            var oldMutes = new[] { oldSettings.IsMusicMuted, oldSettings.IsVoiceMuted, oldSettings.IsSoundMuted };
            var calls = 0;
            foreach (var mute in oldMutes)
            {
                mute.Value = true;
                mute.Register(_ => calls++);
            }
            AudioKit.Shutdown();
            var settings = AudioKit.Settings;
            settings.IsOn = true;
            var music = AudioKit.PlayMusic(_clip);
            var voice = AudioKit.PlayVoice(_clip);
            var sound = AudioKit.PlaySound(_clip);
            music.Pause();
            voice.Pause();
            sound.Pause();
            foreach (var mute in oldMutes)
            {
                mute.Value = false;
                mute.Value = true;
            }
            Assert.AreEqual(0, calls, "Shutdown must detach external and manager subscriptions.");
            Assert.IsFalse(settings.IsMusicMuted.Value);
            Assert.IsFalse(settings.IsVoiceMuted.Value);
            Assert.IsFalse(settings.IsSoundMuted.Value);
            Assert.IsFalse(music.Voice.Source.mute);
            Assert.IsFalse(voice.Voice.Source.mute);
            Assert.IsFalse(sound.Voice.Source.mute);
        }

        [Test]
        public void MuteIsCategoryIsolatedAppliesToNewRequestsAndPoolReuseResetsIt()
        {
            var settings = AudioKit.Settings;
            var music = AudioKit.PlayMusic(_clip);
            var voice = AudioKit.PlayVoiceAt(_clip, Vector3.one);
            var sound = AudioKit.PlaySoundAt(_clip, Vector3.one);
            var otherSound = AudioKit.PlaySound(_clip);
            music.Pause();
            voice.Pause();
            sound.Pause();
            otherSound.Pause();
            var generation = sound.Generation;
            settings.IsMusicMuted.Value = true;
            Assert.IsTrue(music.Voice.Source.mute);
            Assert.IsFalse(voice.Voice.Source.mute);
            Assert.IsFalse(sound.Voice.Source.mute);
            settings.IsVoiceMuted.Value = true;
            Assert.IsTrue(voice.Voice.Source.mute);
            Assert.IsFalse(sound.Voice.Source.mute);
            settings.IsSoundMuted.Value = true;
            Assert.IsTrue(sound.Voice.Source.mute);
            Assert.IsTrue(otherSound.Voice.Source.mute, "Mute must update every active sound, not just one pooled request.");
            Assert.AreEqual(generation, sound.Generation);
            Assert.IsTrue(sound.IsPaused);
            var source = sound.Voice.Source;
            sound.Stop();
            Assert.IsFalse(source.mute, "Returning to the pool clears request state.");
            var muted = AudioKit.PlaySound(_clip);
            muted.Pause();
            Assert.AreSame(source, muted.Voice.Source);
            Assert.IsTrue(source.mute, "Reuse must apply the current category mute before playback.");
            muted.Stop();
            settings.IsSoundMuted.Value = false;
            Assert.IsFalse(otherSound.Voice.Source.mute);
            var unmuted = AudioKit.PlaySound(_clip);
            unmuted.Pause();
            Assert.AreSame(source, unmuted.Voice.Source);
            Assert.IsFalse(source.mute);
            Assert.IsTrue(music.Voice.Source.mute);
            Assert.IsTrue(voice.Voice.Source.mute);
            var replacement = AudioKit.PlayVoice(_clip);
            replacement.Pause();
            Assert.IsTrue(replacement.Voice.Source.mute);
            Assert.IsFalse(voice.IsValid);
        }

        [Test]
        public void UnmuteUsesLatestChannelVolumeAndRequestScaleWithoutChangingPauseOrGeneration()
        {
            var settings = AudioKit.Settings;
            settings.IsMusicMuted.Value = true;
            settings.IsVoiceMuted.Value = true;
            settings.IsSoundMuted.Value = true;
            var handles = new[] { AudioKit.PlayMusic(_clip), AudioKit.PlayVoice(_clip), AudioKit.PlaySound(_clip) };
            foreach (var handle in handles)
            {
                handle.Pause();
            }

            var generations = new[] { handles[0].Generation, handles[1].Generation, handles[2].Generation };
            settings.MusicVolume.Value = .4f;
            settings.VoiceVolume.Value = .6f;
            settings.SoundVolume.Value = .8f;
            foreach (var handle in handles)
            {
                handle.SetVolumeScale(.5f);
            }

            settings.IsMusicMuted.Value = false;
            settings.IsVoiceMuted.Value = false;
            settings.IsSoundMuted.Value = false;
            var volumes = new[] { .2f, .3f, .4f };
            for (var i = 0; i < handles.Length; i++)
            {
                Assert.IsFalse(handles[i].Voice.Source.mute);
                Assert.AreEqual(volumes[i], handles[i].Voice.Source.volume, .001f);
                Assert.AreEqual(.5f, handles[i].Voice.Options.Volume);
                Assert.AreEqual(generations[i], handles[i].Generation);
                Assert.IsTrue(handles[i].IsPaused);
            }
            Assert.IsTrue(settings.IsOn);
            Assert.AreEqual(.4f, settings.MusicVolume.Value);
            Assert.AreEqual(.6f, settings.VoiceVolume.Value);
            Assert.AreEqual(.8f, settings.SoundVolume.Value);
        }

        [Test]
        public void RejectedPlaybackNeverCallsContextCallbacksOrCreatesAHost()
        {
            var calls = 0;
            Action<AudioClip, AudioHandle> callback = (_, __) => calls++;
            Assert.IsFalse(AudioKit.PlayMusic(null, onStarted: callback, onFinished: callback).IsValid);
            Assert.IsFalse(AudioKit.PlayVoice(null, onStarted: callback, onFinished: callback).IsValid);
            Assert.IsFalse(AudioKit.PlayVoiceAt(null, Vector3.zero, onStarted: callback, onFinished: callback).IsValid);
            Assert.IsFalse(AudioKit.PlayVoiceFollowing(_clip, null, onStarted: callback, onFinished: callback).IsValid);
            Assert.IsFalse(AudioKit.PlaySound(null, onFinished: callback).IsValid);
            Assert.IsFalse(AudioKit.PlaySoundAt(null, Vector3.zero, onFinished: callback).IsValid);
            Assert.IsFalse(AudioKit.PlaySoundFollowing(_clip, null, onFinished: callback).IsValid);
            Assert.IsFalse(AudioKit.PlayRandomSound(new AudioClip[] { null }, onFinished: callback).IsValid);
            AudioKit.Settings.IsSoundOn.Value = false;
            AudioKit.Settings.IsSoundMuted.Value = true;
            Assert.IsFalse(AudioKit.PlaySound(_clip, onFinished: callback).IsValid, "Mute must not override IsOn rejection.");
            Assert.AreEqual(0, calls);
            Assert.IsFalse(AudioManager.HasInstance);
        }
    }

    internal sealed class AudioTestSettings : IDisposable
    {
        internal static readonly string[] Keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];

        internal AudioTestSettings()
        {
            AudioKit.Shutdown();
            for (var i = 0; i < Keys.Length; i++)
            {
                _hadKeys[i] = PlayerPrefs.HasKey(Keys[i]);
            }

            for (var i = 0; i < 3; i++)
            {
                _switches[i] = PlayerPrefs.GetInt(Keys[i]);
                _volumes[i] = PlayerPrefs.GetFloat(Keys[i + 3]);
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
        }

        public void Dispose()
        {
            AudioKit.Shutdown();
            for (var i = 0; i < 3; i++)
            {
                if (_hadKeys[i])
                {
                    PlayerPrefs.SetInt(Keys[i], _switches[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(Keys[i]);
                }

                if (_hadKeys[i + 3])
                {
                    PlayerPrefs.SetFloat(Keys[i + 3], _volumes[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(Keys[i + 3]);
                }
            }
        }
    }
}
