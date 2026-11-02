using System;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Binding;
using YLFramework.Audio;

namespace YLFramework.Tests.Audio
{
    public class AudioUtilityTests
    {
        [Test]
        public void DefaultsAreExplicitAndDefaultHandleControlIsSafe()
        {
            var options = AudioSoundOptions.Defaults;
            Assert.IsFalse(options.Loop);
            Assert.AreEqual(1f, options.Volume);
            Assert.AreEqual(1f, options.Pitch);
            Assert.AreEqual(0, options.Priority);
            Assert.IsNull(options.PlaySoundMode);
            Assert.AreEqual(1f, options.MinDistance);
            Assert.AreEqual(50f, options.MaxDistance);
            Assert.AreEqual(AudioRolloffMode.Linear, options.RolloffMode);
            Assert.DoesNotThrow(() => options.Validate(false));
            Assert.DoesNotThrow(() => options.Validate(true));
            var handle = default(AudioHandle);
            Assert.IsFalse(handle.IsValid);
            Assert.IsFalse(handle.IsPlaying);
            Assert.IsFalse(handle.IsPaused);
            Assert.AreEqual(0f, handle.Time);
            Assert.AreEqual(0f, handle.Duration);
            Assert.AreEqual(0f, handle.Progress);
            Assert.DoesNotThrow(handle.Pause);
            Assert.DoesNotThrow(handle.Resume);
            Assert.DoesNotThrow(handle.Stop);
            Assert.DoesNotThrow(() => handle.SetVolumeScale(float.NaN));
            Assert.DoesNotThrow(() => handle.SetPitch(float.NaN));
        }

        [Test]
        public void ProgressIsReadOnlyAndEveryPlaybackCallbackUsesClipThenOriginalHandle()
        {
            foreach (var name in new[] { "Time", "Duration", "Progress" })
            {
                var property = typeof(AudioHandle).GetProperty(name);
                Assert.IsNotNull(property, name);
                Assert.AreEqual(typeof(float), property.PropertyType);
                Assert.IsTrue(property.CanRead);
                Assert.IsFalse(property.CanWrite);
            }
            foreach (var name in new[] { "PlayMusic", "PlayVoice", "PlayVoiceAt", "PlayVoiceFollowing",
                "PlaySound", "PlaySoundAt", "PlaySoundFollowing", "PlayRandomSound", "PlayWeightedRandomSound" })
            {
                var method = typeof(AudioKit).GetMethod(name);
                Assert.IsNotNull(method, name);
                var callbacks = 0;
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.Name != "onStarted" && parameter.Name != "onFinished")
                    {
                        continue;
                    }

                    callbacks++;
                    Assert.AreEqual(typeof(Action<AudioClip, AudioHandle>), parameter.ParameterType, name + "." + parameter.Name);
                    Assert.IsTrue(parameter.HasDefaultValue);
                    Assert.IsNull(parameter.DefaultValue);
                }
                Assert.AreEqual(2, callbacks, name);
            }
            Assert.IsNotNull(typeof(PlaySoundAction).GetMethod("Allocate", new[] { typeof(AudioClip), typeof(Action), typeof(AudioOutputRoute),
                    typeof(Action<AudioClip, AudioHandle>) }),
                "The Action completion notification must keep its parameterless delegate contract.");
        }

        [Test]
        public void RejectedAndDisabledRequestsReportZeroPositionWithoutLosingPendingDuration()
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("pending-progress", 88200, 1, 44100, false);
                try
                {
                    var rejected = AudioKit.PlaySound(null);
                    Assert.AreEqual(0f, rejected.Time);
                    Assert.AreEqual(0f, rejected.Duration);
                    Assert.AreEqual(0f, rejected.Progress);
                    Assert.IsFalse(AudioManager.HasInstance);
                    AudioKit.Settings.IsSoundOn.Value = false;
                    rejected = AudioKit.PlaySound(clip);
                    Assert.AreEqual(0f, rejected.Time);
                    Assert.AreEqual(0f, rejected.Duration);
                    Assert.AreEqual(0f, rejected.Progress);
                    Assert.IsFalse(AudioManager.HasInstance);
                    AudioKit.Settings.IsMusicOn.Value = false;
                    AudioKit.Settings.IsVoiceOn.Value = false;
                    var music = AudioKit.PlayMusic(clip);
                    var voice = AudioKit.PlayVoiceAt(clip, Vector3.one);
                    foreach (var handle in new[] { music, voice })
                    {
                        Assert.IsTrue(handle.IsValid);
                        Assert.IsFalse(handle.IsPlaying);
                        Assert.AreEqual(0f, handle.Time);
                        Assert.AreEqual(clip.length, handle.Duration, .001f);
                        Assert.AreEqual(0f, handle.Progress);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(-.1f)]
        public void InvalidRuntimeVolumeDoesNotChangeTheRequest(float volume)
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("runtime-volume", 44100, 1, 44100, false);
                try
                {
                    var handle = AudioKit.PlaySound(clip);
                    handle.Pause();
                    var generation = handle.Generation;
                    Assert.Throws<ArgumentOutOfRangeException>(() => handle.SetVolumeScale(volume));
                    Assert.AreEqual(1f, handle.Voice.Options.Volume);
                    Assert.AreEqual(1f, handle.Voice.Source.volume);
                    Assert.AreEqual(generation, handle.Generation);
                    Assert.IsTrue(handle.IsValid);
                    Assert.IsTrue(handle.IsPaused);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(-.1f)]
        [TestCase(0f)]
        [TestCase(3.1f)]
        public void InvalidRuntimePitchDoesNotChangeTheRequest(float pitch)
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("runtime-pitch", 44100, 1, 44100, false);
                try
                {
                    var handle = AudioKit.PlaySound(clip);
                    handle.Pause();
                    Assert.Throws<ArgumentOutOfRangeException>(() => handle.SetPitch(pitch));
                    Assert.AreEqual(1f, handle.Voice.Options.Pitch);
                    Assert.AreEqual(1f, handle.Voice.Source.pitch);
                    Assert.IsTrue(handle.IsValid);
                    Assert.IsTrue(handle.IsPaused);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [Test]
        public void RuntimeParameterBoundariesUseCurrentChannelVolumeAndRetainUnclampedScale()
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("runtime-boundaries", 44100, 1, 44100, false);
                try
                {
                    var handle = AudioKit.PlaySound(clip);
                    handle.Pause();
                    handle.SetVolumeScale(0f);
                    Assert.AreEqual(0f, handle.Voice.Source.volume);
                    AudioKit.Settings.SoundVolume.SetValueWithoutCallback(.4f);
                    handle.SetVolumeScale(2f);
                    Assert.AreEqual(.8f, handle.Voice.Source.volume, .001f);
                    Assert.AreEqual(2f, handle.Voice.Options.Volume);
                    AudioKit.Settings.SoundVolume.Value = .75f;
                    Assert.AreEqual(1f, handle.Voice.Source.volume);
                    AudioKit.Settings.SoundVolume.Value = .2f;
                    Assert.AreEqual(.4f, handle.Voice.Source.volume, .001f);
                    handle.SetPitch(3f);
                    Assert.AreEqual(3f, handle.Voice.Source.pitch);
                    handle.SetPitch(.01f);
                    Assert.AreEqual(.01f, handle.Voice.Source.pitch, .001f);
                    Assert.IsTrue(handle.IsPaused);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [Test]
        public void OldPoolSlotAndSessionHandlesCannotModifyNewPlaybackParameters()
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("runtime-old-handles", 44100, 1, 44100, false);
                try
                {
                    var oldSound = AudioKit.PlaySound(clip);
                    oldSound.Stop();
                    var current = AudioKit.PlaySound(clip);
                    current.Pause();
                    Assert.AreSame(oldSound.Voice, current.Voice);
                    oldSound.SetVolumeScale(float.NaN);
                    oldSound.SetPitch(2f);
                    Assert.AreEqual(1f, current.Voice.Source.volume);
                    Assert.AreEqual(1f, current.Voice.Source.pitch);

                    var oldMusic = AudioKit.PlayMusic(clip);
                    var music = AudioKit.PlayMusic(clip, volume: .6f);
                    music.Pause();
                    var oldVoice = AudioKit.PlayVoice(clip);
                    var voice = AudioKit.PlayVoice(clip, volume: .7f);
                    voice.Pause();
                    oldMusic.SetVolumeScale(2f);
                    oldMusic.SetPitch(float.NaN);
                    oldVoice.SetVolumeScale(2f);
                    oldVoice.SetPitch(2f);
                    Assert.AreEqual(.6f, music.Voice.Options.Volume);
                    Assert.AreEqual(.7f, voice.Voice.Options.Volume);
                    Assert.AreEqual(1f, music.Voice.Source.pitch);
                    Assert.AreEqual(1f, voice.Voice.Source.pitch);

                    AudioKit.Shutdown();
                    current.SetVolumeScale(2f);
                    current.SetPitch(2f);
                    Assert.IsFalse(AudioManager.HasInstance, "Expired controls must not create a host.");
                    AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
                    var next = AudioKit.PlaySound(clip);
                    next.Pause();
                    current.SetVolumeScale(2f);
                    current.SetPitch(2f);
                    Assert.AreEqual(1f, next.Voice.Options.Volume);
                    Assert.AreEqual(1f, next.Voice.Source.pitch);
                    Assert.IsTrue(next.IsValid);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-.1f)]
        public void InvalidVolumeFailsValidation(float volume)
        {
            var options = AudioSoundOptions.Defaults;
            options.Volume = volume;
            Assert.That(() => options.Validate(false), Throws.InstanceOf<ArgumentException>());
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(0f)]
        [TestCase(3.1f)]
        public void InvalidPitchFailsValidation(float pitch)
        {
            var options = AudioSoundOptions.Defaults;
            options.Pitch = pitch;
            Assert.That(() => options.Validate(false), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void SpatialDistanceAndRolloffMustBeValid()
        {
            var options = AudioSoundOptions.Defaults;
            options.MinDistance = -1f;
            Assert.That(() => options.Validate(true), Throws.InstanceOf<ArgumentException>());
            options = AudioSoundOptions.Defaults;
            options.MaxDistance = .5f;
            Assert.That(() => options.Validate(true), Throws.InstanceOf<ArgumentException>());
            options = AudioSoundOptions.Defaults;
            options.MaxDistance = float.PositiveInfinity;
            Assert.That(() => options.Validate(true), Throws.InstanceOf<ArgumentException>());
            options = AudioSoundOptions.Defaults;
            options.RolloffMode = AudioRolloffMode.Custom;
            Assert.That(() => options.Validate(true), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void InvalidPerRequestModeIsRejectedAndBoundaryValuesRemainValid()
        {
            var options = AudioSoundOptions.Defaults;
            options.PlaySoundMode = (AudioKit.PlaySoundModes)99;
            Assert.That(() => options.Validate(false), Throws.InstanceOf<ArgumentException>());
            options = AudioSoundOptions.Defaults;
            options.Volume = 0f;
            options.Pitch = 3f;
            options.MinDistance = 0f;
            options.MaxDistance = .1f;
            Assert.DoesNotThrow(() => options.Validate(true));
        }

        [Test]
        public void InvalidParametersFailBeforeAllocationOrDeduplicationCommit()
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("invalid-options", 44100, 1, 44100, false);
                try
                {
                    AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
                    var options = AudioSoundOptions.Defaults;
                    options.Pitch = float.NaN;
                    Assert.That(() => AudioKit.PlaySound(clip, options), Throws.InstanceOf<ArgumentException>());
                    Assert.IsFalse(AudioManager.HasInstance);
                    Assert.AreEqual(0, AudioKit.SoundCount);
                    Assert.That(() => AudioKit.PlaySoundAt(clip, new Vector3(float.NaN, 0f, 0f)),
                        Throws.InstanceOf<ArgumentException>());
                    Assert.IsFalse(AudioManager.HasInstance);
                    var handle = AudioKit.PlaySound(clip);
                    handle.Pause();
                    Assert.IsTrue(handle.IsValid);
                    Assert.AreEqual(1f, handle.Voice.Source.volume);
                    Assert.AreEqual(1f, handle.Voice.Source.pitch);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [Test]
        public void FoundationPropertiesPersistBeforeNotificationAndSilentSetDoesNotWrite()
        {
            var key = "YLFramework.Audio.UtilityTests." + Guid.NewGuid().ToString("N");
            try
            {
                var volume = new PlayerPrefsFloatProperty(key + ".volume", 1f);
                var enabled = new PlayerPrefsBoolProperty(key + ".enabled", true);
                Assert.IsFalse(volume.HasKey);
                Assert.IsFalse(enabled.HasKey);
                var calls = 0;
                volume.Register(value =>
                {
                    calls++;
                    Assert.AreEqual(value, volume.Value);
                    Assert.AreEqual(value, PlayerPrefs.GetFloat(volume.Key));
                });
                volume.Value = .25f;
                enabled.Value = false;
                volume.SetValueWithoutCallback(.5f);
                enabled.SetValueWithoutCallback(true);
                Assert.AreEqual(.25f, PlayerPrefs.GetFloat(volume.Key));
                Assert.AreEqual(0, PlayerPrefs.GetInt(enabled.Key));
                Assert.AreEqual(1, calls);
                volume.UnregisterAll();
                volume.Value = .75f;
                Assert.AreEqual(1, calls);
                Assert.AreEqual(.75f, new PlayerPrefsFloatProperty(volume.Key).Value);
                Assert.IsFalse(new PlayerPrefsBoolProperty(enabled.Key).Value);
            }
            finally
            {
                PlayerPrefs.DeleteKey(key + ".volume");
                PlayerPrefs.DeleteKey(key + ".enabled");
            }
        }

        [Test]
        public void SpatialVoiceRejectsInvalidInputWithoutCreatingOrReplacingTheSlot()
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("spatial-voice-validation", 44100, 1, 44100, false);
                var target = new GameObject("SpatialVoice.ValidationTarget");
                try
                {
                    target.SetActive(false);
                    Assert.IsFalse(AudioKit.PlayVoiceFollowing(clip, null).IsValid);
                    Assert.IsFalse(AudioKit.PlayVoiceFollowing(clip, target.transform).IsValid);
                    Assert.IsFalse(AudioKit.PlayVoiceAt(null, Vector3.zero).IsValid);
                    Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlayVoiceAt(clip,
                        new Vector3(float.NaN, 0, 0)));
                    var invalid = AudioSoundOptions.Defaults;
                    invalid.MaxDistance = 0;
                    Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlayVoiceAt(clip, Vector3.zero, invalid));
                    Assert.IsFalse(AudioManager.HasInstance);
                    var current = AudioKit.PlayVoice(clip);
                    current.Pause();
                    Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlayVoiceAt(clip, Vector3.zero, invalid));
                    Assert.IsFalse(AudioKit.PlayVoiceFollowing(clip, target.transform).IsValid);
                    Assert.AreEqual(current, AudioKit.Voice);
                    Assert.IsTrue(current.IsPaused);
                    Assert.AreEqual(0f, current.Voice.Source.spatialBlend);
                    Assert.AreEqual(0, AudioKit.SoundCount);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(target);
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [Test]
        public void SpatialVoiceUsesVoiceSettingsAndNeverSoundDeduplication()
        {
            using (new AudioTestSettings())
            {
                var clip = AudioClip.Create("spatial-voice-channel", 44100, 1, 44100, false);
                try
                {
                    AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
                    var sound = AudioKit.PlaySound(clip);
                    sound.Pause();
                    AudioKit.Settings.IsSoundOn.Value = false;
                    AudioKit.Settings.VoiceVolume.Value = .4f;
                    var options = AudioSoundOptions.Defaults;
                    options.Volume = .5f;
                    options.PlaySoundMode = (AudioKit.PlaySoundModes)99;
                    var first = AudioKit.PlayVoiceAt(clip, Vector3.one, options);
                    first.Pause();
                    var current = AudioKit.PlayVoiceAt(clip, Vector3.zero, options);
                    current.Pause();
                    Assert.IsFalse(first.IsValid);
                    Assert.IsTrue(current.IsValid);
                    Assert.IsNull(current.Voice.Options.PlaySoundMode);
                    Assert.AreEqual(.2f, current.Voice.Source.volume, .001f);
                    AudioKit.Settings.SoundVolume.Value = .9f;
                    Assert.AreEqual(.2f, current.Voice.Source.volume, .001f);
                    AudioKit.Settings.VoiceVolume.Value = .6f;
                    Assert.AreEqual(.3f, current.Voice.Source.volume, .001f);
                    AudioKit.StopAllSound();
                    Assert.IsTrue(current.IsValid);
                    Assert.AreEqual(0, AudioKit.SoundCount);
                    first.SetPitch(2f);
                    Assert.AreEqual(1f, current.Voice.Source.pitch);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [Test]
        public void InstanceComparerDoesNotChangeFoundationGlobalFloatPolicy()
        {
            var previous = BindableProperty<float>.Comparer;
            var key = "YLFramework.Audio.ComparerTests." + Guid.NewGuid().ToString("N");
            try
            {
                var property = new PlayerPrefsFloatProperty(key, 1f);
                property.WithComparer((a, b) => a == b);
                var calls = 0;
                property.Register(_ => calls++);
                var changed = 1f + .0000001f;
                property.Value = changed;
                property.Value = changed;
                Assert.AreEqual(1, calls);
                Assert.AreEqual(changed, PlayerPrefs.GetFloat(key));
                Assert.AreSame(previous, BindableProperty<float>.Comparer);
                property.UnregisterAll();
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
            }
        }

        [TestCase(-1, false)]
        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        public void SettingsReadOnlyOneAsTrueFromExistingSave(int stored, bool expected)
        {
            using (new AudioTestSettings())
            {
                AudioKit.Shutdown();
                for (var i = 0; i < 3; i++)
                {
                    PlayerPrefs.SetInt(AudioTestSettings.Keys[i], stored);
                }

                var settings = AudioKit.Settings;
                Assert.AreEqual(expected, settings.IsSoundOn.Value);
                Assert.AreEqual(expected, settings.IsMusicOn.Value);
                Assert.AreEqual(expected, settings.IsVoiceOn.Value);
                Assert.AreEqual(expected, settings.IsOn);
                settings.IsOn = !expected;
                for (var i = 0; i < 3; i++)
                {
                    Assert.AreEqual(expected ? 0 : 1, PlayerPrefs.GetInt(AudioTestSettings.Keys[i]));
                }
            }
        }
    }
}
