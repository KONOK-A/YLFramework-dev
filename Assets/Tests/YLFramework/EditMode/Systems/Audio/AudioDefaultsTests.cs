using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Actions;
using YLFramework.Binding;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioDefaultsTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;
        private GameObject _target;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("defaults", 44100, 1, 44100, false);
            _target = new GameObject("AudioDefaults.Target");
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_clip);
            Object.DestroyImmediate(_target);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyAudioOptionsJsonRetainsItsPublishedFieldKeys(bool loop)
        {
            string input = "{\"Loop\":" + (loop ? "true" : "false") +
                ",\"Volume\":0.37,\"Pitch\":1.7,\"MinDistance\":3,\"MaxDistance\":41," +
                "\"RolloffMode\":0,\"Priority\":19,\"AutoPauseWhenTimeScaleZero\":true," +
                "\"OutputRoute\":{\"Mode\":0,\"Group\":null},\"AutoFade\":{\"FadeInSeconds\":0.45,\"FadeOutSeconds\":0}}";
            var options = JsonUtility.FromJson<AudioSoundOptions>(input);
            Assert.That(options.Loop, Is.EqualTo(loop));
            Assert.That(options.Volume, Is.EqualTo(.37f));
            Assert.That(options.Pitch, Is.EqualTo(1.7f));
            Assert.That(options.AutoFade.FadeInSeconds, Is.EqualTo(.45f));
            AudioPlaybackOptions.Capture(options).Validate(false);
            var json = Newtonsoft.Json.Linq.JObject.Parse(JsonUtility.ToJson(options));
            CollectionAssert.AreEquivalent("Loop Volume Pitch MinDistance MaxDistance RolloffMode OutputRoute AutoFade Priority AutoPauseWhenTimeScaleZero".Split(' '),
                json.Properties().Select(property => property.Name));
            CollectionAssert.AreEquivalent(new[] { "Mode", "Group" }, ((Newtonsoft.Json.Linq.JObject)json["OutputRoute"]).Properties().Select(property => property.Name));
            CollectionAssert.AreEquivalent(new[] { "FadeInSeconds", "FadeOutSeconds" }, ((Newtonsoft.Json.Linq.JObject)json["AutoFade"]).Properties().Select(property => property.Name));
            var defaults = AudioSoundOptions.Defaults;
            JsonUtility.FromJsonOverwrite("{\"Volume\":0}", defaults);
            Assert.That(defaults.Volume, Is.Zero);
            Assert.That(defaults.Pitch, Is.EqualTo(1f));
        }

        private AudioHandle Play(int entry, AudioSoundOptions options,
            Action<AudioClip, AudioHandle> started = null)
        {
            switch (entry)
            {
                case 0:
                    return AudioKit.PlaySound(_clip, options, onStarted: started);
                case 1:
                    return AudioKit.PlaySoundAt(_clip, Vector3.one, options, onStarted: started);
                case 2:
                    return AudioKit.PlaySoundFollowing(_clip, _target.transform, options, onStarted: started);
                case 3:
                    return AudioKit.PlayRandomSound(new[] { _clip }, options, onStarted: started);
                case 4:
                    return AudioKit.PlayWeightedRandomSound(new[] { new AudioWeightedClip(_clip) }, options, onStarted: started);
                case 5:
                    return AudioKit.PlayVoiceAt(_clip, Vector3.one, options, onStarted: started);
                case 6:
                    return AudioKit.PlayVoiceFollowing(_clip, _target.transform, options, onStarted: started);
                case 7:
                    return AudioKit.PlayVoiceWithOptions(_clip, options, onStarted: started);
                case 8:
                    return AudioKit.PlayMusicWithOptions(_clip, options, onStarted: started);
                default:
                    return AudioKit.CrossFadeMusic(_clip, 0f, options, onStarted: started);
            }
        }

        [Test]
        public void InternalReadonlyStagesBorrowSnapshotsButSubmissionBoundariesStillCopy()
        {
            var owners = new[] { typeof(AudioManager), typeof(AudioManager), typeof(AudioMusicPlayback), typeof(AudioMusicPlayback), typeof(AudioVoice) };
            var names = new[] { "PlaySlot", "PlaySound", "Play", "CrossFade", "Begin" };
            var indices = new[] { 2, 1, 1, 2, 1 };
            for (var i = 0; i < owners.Length; i++)
            {
                var parameter = owners[i].GetMethod(names[i], BindingFlags.NonPublic | BindingFlags.Instance).GetParameters()[indices[i]];
                Assert.AreEqual(typeof(AudioPlaybackOptions).MakeByRefType(), parameter.ParameterType, names[i]);
                Assert.IsTrue(parameter.IsIn, names[i]);
                Assert.IsFalse(parameter.IsOut, names[i]);
            }
            var boundaries = new[] { "PlaySlotWithSnapshot", "CrossFadeMusicWithSnapshot", "PlaySoundWithSnapshot" };
            var boundaryIndices = new[] { 2, 2, 1 };
            for (var i = 0; i < boundaries.Length; i++)
            {
                var parameter = typeof(AudioKit).GetMethod(boundaries[i], BindingFlags.NonPublic | BindingFlags.Static).GetParameters()[boundaryIndices[i]];
                Assert.AreEqual(typeof(AudioPlaybackOptions), parameter.ParameterType, boundaries[i]);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SubmissionLocalSurvivesCallbackChangesToTheCallerValue(int entry)
        {
            var snapshot = AudioPlaybackOptions.Defaults;
            snapshot.Volume = .4f;
            snapshot.Pitch = 1.5f;
            Action<AudioClip, AudioHandle> started = (_, handle) =>
            {
                snapshot.Volume = 0f;
                snapshot.Pitch = 0f;
                snapshot.AutoFade = new AudioAutoFade(1f, 1f);
                handle.Pause();
            };
            var handle = entry == 0
                ? AudioKit.PlaySoundWithSnapshot(_clip, snapshot, onStarted: started)
                : entry == 1
                    ? AudioKit.PlaySlotWithSnapshot(_clip, false, snapshot, started, null)
                    : AudioKit.CrossFadeMusicWithSnapshot(_clip, 0f, snapshot, onStarted: started);
            Assert.IsTrue(handle.IsValid);
            Assert.IsTrue(handle.IsPaused);
            Assert.AreEqual(0f, snapshot.Volume);
            Assert.AreEqual(0f, snapshot.Pitch);
            Assert.AreEqual(.4f, handle.Voice.Options.Volume);
            Assert.AreEqual(1.5f, handle.Voice.Options.Pitch);
            Assert.AreEqual(default(AudioAutoFade), handle.Voice.Options.AutoFade);
        }

        [Test]
        public void NewAndPresetsAreValidIndependentConfigurations()
        {
            Assert.IsTrue(typeof(AudioSoundOptions).IsClass);
            Assert.IsTrue(typeof(AudioSoundOptions).IsSealed);
            Assert.IsNull(default(AudioSoundOptions));
            var plain = new AudioSoundOptions();
            var defaults = AudioSoundOptions.Defaults;
            var music = AudioSoundOptions.MusicDefaults;
            Assert.AreEqual(1f, plain.Volume);
            Assert.AreEqual(1f, plain.Pitch);
            Assert.AreEqual(1f, plain.MinDistance);
            Assert.AreEqual(50f, plain.MaxDistance);
            Assert.AreEqual(AudioRolloffMode.Linear, plain.RolloffMode);
            Assert.IsFalse(plain.Loop);
            Assert.IsTrue(music.Loop);
            Assert.IsNull(plain.PlaySoundMode);
            Assert.DoesNotThrow(() => plain.Validate(true));
            defaults.Volume = 0f;
            music.Loop = false;
            Assert.AreEqual(1f, AudioSoundOptions.Defaults.Volume);
            Assert.IsTrue(AudioSoundOptions.MusicDefaults.Loop);
            Assert.AreNotSame(defaults, AudioSoundOptions.Defaults);
            Assert.AreNotSame(music, AudioSoundOptions.MusicDefaults);
        }

        private static void AssertOptionsSnapshot(AudioSoundOptions expected, AudioPlaybackOptions actual)
        {
            var fields = typeof(AudioSoundOptions).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Assert.AreEqual(11, fields.Length);
            Assert.AreEqual(11, typeof(AudioPlaybackOptions).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length);
            foreach (var field in fields)
            {
                var snapshotField = typeof(AudioPlaybackOptions).GetField(field.Name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(snapshotField, field.Name);
                Assert.AreEqual(field.FieldType, snapshotField.FieldType, field.Name);
                Assert.AreEqual(field.GetValue(expected), snapshotField.GetValue(actual), field.Name);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InternalDefaultsAndNullCaptureMatchPublicDefaults(bool music)
        {
            Assert.IsTrue(typeof(AudioPlaybackOptions).IsValueType);
            Assert.IsFalse(typeof(AudioPlaybackOptions).IsVisible);
            var expected = music ? AudioSoundOptions.MusicDefaults : AudioSoundOptions.Defaults;
            var defaults = music ? AudioPlaybackOptions.MusicDefaults : AudioPlaybackOptions.Defaults;
            AssertOptionsSnapshot(expected, defaults);
            AssertOptionsSnapshot(expected, AudioPlaybackOptions.Capture(null, music));
            if (!music)
            {
                AssertOptionsSnapshot(new AudioSoundOptions(), defaults);
            }

            Assert.DoesNotThrow(() => defaults.Validate(true));
            Assert.DoesNotThrow(() => defaults.ValidateClip(_clip));
            defaults.Volume = 0f;
            defaults.Pitch = 0f;
            defaults.Loop = !music;
            AssertOptionsSnapshot(expected, music ? AudioPlaybackOptions.MusicDefaults : AudioPlaybackOptions.Defaults);
        }

        [Test]
        public void CaptureMapsAllTenFieldsAndNestedValuesWithoutRetainingMutableConfiguration()
        {
            var options = new AudioSoundOptions
            {
                Loop = true,
                Volume = .4f,
                Pitch = 2.5f,
                PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames,
                MinDistance = 3f,
                MaxDistance = 23f,
                RolloffMode = AudioRolloffMode.Logarithmic,
                OutputRoute = AudioOutputRoute.Direct,
                AutoFade = new AudioAutoFade(.3f, 0f),
                Priority = 17
            };
            var captured = AudioPlaybackOptions.Capture(options);
            var second = AudioPlaybackOptions.Capture(options);
            AssertOptionsSnapshot(options, captured);
            Assert.DoesNotThrow(() => captured.Validate(true));
            Assert.DoesNotThrow(() => captured.ValidateClip(_clip));
            options.Loop = false;
            options.Volume = 0f;
            options.Pitch = 0f;
            options.PlaySoundMode = null;
            options.MinDistance = 0f;
            options.MaxDistance = 0f;
            options.RolloffMode = AudioRolloffMode.Linear;
            options.Priority = 0;
            options.OutputRoute.Mode = AudioOutputRoute.RouteMode.Inherit;
            options.AutoFade.FadeInSeconds = 0f;
            options.AutoFade.FadeOutSeconds = 1f;
            Assert.AreEqual(second, captured, "Mutating every caller field must not change either captured value.");
            captured.AutoFade.FadeInSeconds = .8f;
            captured.OutputRoute.Mode = AudioOutputRoute.RouteMode.Inherit;
            captured.Volume = .2f;
            Assert.AreEqual(.3f, second.AutoFade.FadeInSeconds);
            Assert.AreEqual(AudioOutputRoute.Direct, second.OutputRoute);
            Assert.AreEqual(.4f, second.Volume);
            Assert.AreEqual(0f, options.AutoFade.FadeInSeconds);
            Assert.AreEqual(0f, options.Volume);
        }

        [Test]
        public void CapturePreservesExplicitMusicNonloopSilenceAndInvalidPitchInsteadOfFillingZeros()
        {
            var options = new AudioSoundOptions { Loop = false, Volume = 0f };
            var captured = AudioPlaybackOptions.Capture(options, music: true);
            AssertOptionsSnapshot(options, captured);
            Assert.IsFalse(captured.Loop);
            Assert.AreEqual(0f, captured.Volume);
            Assert.DoesNotThrow(() => captured.Validate(true));
            options.Pitch = 0f;
            captured = AudioPlaybackOptions.Capture(options, music: true);
            Assert.AreEqual(0f, captured.Pitch);
            Assert.AreEqual("Pitch", Assert.Throws<ArgumentOutOfRangeException>(() => captured.Validate(false)).ParamName);
            var cleared = default(AudioPlaybackOptions);
            Assert.AreEqual("Pitch", Assert.Throws<ArgumentOutOfRangeException>(() => cleared.Validate(false)).ParamName,
                "The zero value is a cleared state, not a valid playback default.");
            captured = AudioPlaybackOptions.Defaults;
            Assert.DoesNotThrow(() => captured.ValidateClip(null), "A disabled envelope does not require a live clip.");
            captured.AutoFade = new AudioAutoFade(.1f, 0f);
            Assert.AreEqual("clip", Assert.Throws<ArgumentException>(() => captured.ValidateClip(null)).ParamName);
            captured.Loop = true;
            captured.AutoFade.FadeOutSeconds = .1f;
            Assert.AreEqual("FadeOutSeconds", Assert.Throws<ArgumentException>(() => captured.Validate(false)).ParamName);
        }

        [Test]
        public void VoiceOptionsGetterReturnsIndependentValueAndHandleChangesOnlyOwnedSnapshot()
        {
            var property = typeof(AudioVoice).GetProperty("Options", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(property);
            Assert.AreEqual(typeof(AudioPlaybackOptions), property.PropertyType);
            Assert.IsNull(property.GetSetMethod(true));
            var field = typeof(AudioVoice).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            Assert.IsTrue(field.IsPrivate);
            Assert.AreEqual(typeof(AudioPlaybackOptions), field.FieldType);
            var options = new AudioSoundOptions { Loop = true, Volume = .4f, Pitch = 1.5f };
            var handle = AudioKit.PlaySound(_clip, options);
            handle.Pause();
            var original = handle.Voice.Options;
            var copy = handle.Voice.Options;
            copy.Volume = .2f;
            copy.Pitch = 2f;
            copy.Priority = 31;
            copy.AutoFade.FadeInSeconds = .3f;
            copy.OutputRoute = AudioOutputRoute.Direct;
            AssertOptionsSnapshot(options, handle.Voice.Options);
            handle.SetVolumeScale(.1f);
            handle.SetPitch(.8f);
            Assert.AreEqual(.1f, handle.Voice.Options.Volume);
            Assert.AreEqual(.8f, handle.Voice.Options.Pitch);
            Assert.AreEqual(handle.Voice.Options, (AudioPlaybackOptions)field.GetValue(handle.Voice));
            AssertOptionsSnapshot(options, original);
            Assert.AreEqual(.2f, copy.Volume);
            Assert.AreEqual(2f, copy.Pitch);
            handle.Stop();
            Assert.AreEqual(default(AudioPlaybackOptions), handle.Voice.Options);
            Assert.AreEqual(default(AudioPlaybackOptions), (AudioPlaybackOptions)field.GetValue(handle.Voice));
            AssertOptionsSnapshot(options, original);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        public void PartialConfigurationIsCopiedBeforeStartedCallback(int entry)
        {
            var options = new AudioSoundOptions { Volume = .5f };
            var calls = 0;
            var handle = Play(entry, options, (_, started) =>
            {
                calls++;
                started.Pause();
                options.Volume = 0f;
                options.Pitch = 0f;
                options.MaxDistance = 0f;
            });
            Assert.AreEqual(1, calls);
            Assert.IsTrue(handle.IsValid);
            Assert.IsTrue(handle.IsPaused);
            Assert.AreEqual(.5f, handle.Voice.Options.Volume);
            Assert.AreEqual(1f, handle.Voice.Options.Pitch);
            Assert.AreEqual(.5f, handle.Voice.Source.volume, .001f);
            Assert.AreEqual(1f, handle.Voice.Source.pitch);
            Assert.AreEqual(50f, handle.Voice.Options.MaxDistance);
            handle.SetVolumeScale(.25f);
            handle.SetPitch(2f);
            Assert.AreEqual(0f, options.Volume);
            Assert.AreEqual(0f, options.Pitch);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        public void NullResolvesToCategoryDefaults(int entry)
        {
            var handle = Play(entry, null);
            handle.Pause();
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(entry >= 8, handle.Voice.Options.Loop);
            Assert.AreEqual(1f, handle.Voice.Options.Volume);
            Assert.AreEqual(1f, handle.Voice.Options.Pitch);
            Assert.AreEqual(default(AudioAutoFade), handle.Voice.Options.AutoFade);
            Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
        }

        [Test]
        public void SharedConfigurationAndHandleChangesDoNotLeakAcrossRequests()
        {
            var options = new AudioSoundOptions { Volume = .5f, Pitch = 1.5f, Loop = true, Priority = 7 };
            var first = AudioKit.PlaySound(_clip, options);
            var second = AudioKit.PlaySound(_clip, options);
            first.Pause();
            second.Pause();
            AssertOptionsSnapshot(options, first.Voice.Options);
            AssertOptionsSnapshot(options, second.Voice.Options);
            first.SetVolumeScale(.25f);
            first.SetPitch(2f);
            Assert.AreEqual(.25f, first.Voice.Options.Volume);
            Assert.AreEqual(2f, first.Voice.Options.Pitch);
            Assert.AreEqual(.5f, second.Voice.Options.Volume);
            Assert.AreEqual(1.5f, second.Voice.Options.Pitch);
            Assert.AreEqual(.5f, options.Volume);
            Assert.AreEqual(1.5f, options.Pitch);
            var music = AudioKit.PlayMusicWithOptions(_clip, options);
            music.Pause();
            Assert.AreEqual(7, options.Priority, "Ignoring Sound fields must not modify the caller's configuration.");
            Assert.AreEqual(0, music.Voice.Options.Priority);
            first.Stop();
            var reused = AudioKit.PlaySound(_clip, new AudioSoundOptions());
            reused.Pause();
            Assert.AreSame(first.Voice, reused.Voice);
            Assert.AreEqual(1f, reused.Voice.Options.Volume);
            Assert.AreEqual(1f, reused.Voice.Options.Pitch);
            Assert.AreEqual(.5f, second.Voice.Options.Volume);
        }

        [Test]
        public void CrossfadeParticipantsHaveIndependentSnapshots()
        {
            var options = new AudioSoundOptions { Volume = .4f, Pitch = 1.5f, Loop = true };
            var outgoing = AudioKit.PlayMusicWithOptions(_clip, options);
            var incoming = AudioKit.CrossFadeMusic(_clip, 1f, options, curve: AudioCrossfadeCurve.Linear);
            options.Volume = 0f;
            options.Pitch = 0f;
            Assert.AreEqual(.4f, outgoing.Voice.Options.Volume);
            Assert.AreEqual(.4f, incoming.Voice.Options.Volume);
            AudioManager.Instance.MusicPlayback.Tick(.5f, .5f);
            Assert.AreEqual(.2f, outgoing.Voice.Source.volume, .001f);
            Assert.AreEqual(.2f, incoming.Voice.Source.volume, .001f);
            incoming.SetVolumeScale(.2f);
            incoming.SetPitch(2f);
            Assert.AreEqual(.2f, incoming.Voice.Options.Volume);
            Assert.AreEqual(2f, incoming.Voice.Options.Pitch);
            Assert.AreEqual(.4f, outgoing.Voice.Options.Volume);
            Assert.AreEqual(1.5f, outgoing.Voice.Options.Pitch);
            Assert.AreEqual(.1f, incoming.Voice.Source.volume, .001f);
            Assert.AreEqual(.2f, outgoing.Voice.Source.volume, .001f);
            Assert.AreEqual(1.5f, outgoing.Voice.Source.pitch);
            Assert.AreEqual(0f, options.Volume);
            Assert.AreEqual(0f, options.Pitch);
            outgoing.Pause();
            incoming.Pause();
        }

        [Test]
        public void DisabledSlotsKeepSnapshotWhenCallerChangesConfigurationBeforeFirstStart()
        {
            var settings = AudioKit.Settings;
            settings.IsMusicOn.Value = false;
            settings.IsVoiceOn.Value = false;
            var options = new AudioSoundOptions { Volume = .4f, Pitch = 1.5f, Loop = true };
            var starts = 0;
            var music = AudioKit.PlayMusicWithOptions(_clip, options, onStarted: (_, __) => starts++);
            var voice = AudioKit.PlayVoiceWithOptions(_clip, options, onStarted: (_, __) => starts++);
            options.Volume = 0f;
            options.Pitch = 0f;
            options.Loop = false;
            Assert.IsFalse(music.IsPlaying);
            Assert.IsFalse(voice.IsPlaying);
            Assert.AreEqual(0, starts);
            settings.IsMusicOn.Value = true;
            settings.IsVoiceOn.Value = true;
            Assert.AreEqual(2, starts);
            foreach (var handle in new[] { music, voice })
            {
                handle.Pause();
                Assert.IsTrue(handle.IsValid);
                Assert.AreEqual(.4f, handle.Voice.Source.volume, .001f);
                Assert.AreEqual(1.5f, handle.Voice.Source.pitch);
                Assert.IsTrue(handle.Voice.Source.loop);
            }
        }

        [Test]
        public void InitialRuntimeDefaultsPlayWithoutAdvancedConfiguration()
        {
            AudioKit.Shutdown();
            foreach (var key in AudioTestSettings.Keys)
            {
                PlayerPrefs.DeleteKey(key);
            }

            var settings = AudioKit.Settings;
            Assert.IsTrue(settings.IsOn);
            foreach (var volume in new[] { settings.SoundVolume, settings.MusicVolume, settings.VoiceVolume })
            {
                Assert.AreEqual(1f, volume.Value);
            }

            Assert.IsFalse(settings.IsSoundMuted.Value);
            Assert.IsFalse(settings.IsMusicMuted.Value);
            Assert.IsFalse(settings.IsVoiceMuted.Value);
            Assert.IsNull(settings.SoundOutputGroup.Value);
            Assert.IsNull(settings.MusicOutputGroup.Value);
            Assert.IsNull(settings.VoiceOutputGroup.Value);
            Assert.AreEqual(AudioKit.PlaySoundModes.EveryOne, AudioKit.PlaySoundMode);
            Assert.AreEqual(5, AudioKit.GlobalFrameCountForIgnoreSameSound);
            Assert.AreEqual(5, AudioKit.SoundFrameCountForIgnoreSameSound);
            Assert.AreEqual(0, AudioKit.SoundConcurrency.Limit);
            Assert.AreEqual(0, AudioKit.SoundConcurrency.DefaultClipLimit);
            Assert.AreEqual(SoundOverflowPolicy.RejectNew, AudioKit.SoundConcurrency.OverflowPolicy);
            Assert.AreEqual(10, AudioKit.SoundCacheCapacity);
            Assert.IsFalse(AudioManager.HasInstance, "Reading defaults must not prewarm or create a host.");
            foreach (var key in AudioTestSettings.Keys)
            {
                Assert.IsFalse(PlayerPrefs.HasKey(key));
            }
        }

        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames)]
        public void InitialDeduplicationWindowSuppressesDuplicatesThroughFiveFrameBoundary(AudioKit.PlaySoundModes mode)
        {
            var deduplicator = new SoundDeduplicator();
            Assert.AreEqual(AudioKit.PlaySoundModes.EveryOne, deduplicator.DefaultPlaySoundMode);
            Assert.AreEqual(5, deduplicator.GlobalFrameCountForIgnoreSameSound);
            Assert.AreEqual(5, deduplicator.SoundFrameCountForIgnoreSameSound);
            deduplicator.Record(1, mode, 100, default);
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 100));
            Assert.IsTrue(deduplicator.CanPlay(2, mode, 100));
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 101));
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 105), "The final frame of the window is included.");
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 106));
            deduplicator.Prune(105);
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 105), "Pruning must retain records through the boundary.");
            deduplicator.Prune(106);
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 106));
            Assert.IsTrue(deduplicator.CanPlay(1, AudioKit.PlaySoundModes.EveryOne, 100));
        }

        [Test]
        public void DefaultMusicCrossfadeUsesEqualPowerAndLinearRemainsExplicitlyAvailable()
        {
            var outgoing = AudioKit.PlayMusic(_clip);
            var incoming = AudioKit.CrossFadeMusic(_clip, 1f);
            Assert.AreEqual(AudioCrossfadeCurve.EqualPower, AudioManager.Instance.MusicPlayback.CurrentCurve);
            AudioManager.Instance.MusicPlayback.Tick(.5f, .5f);
            var a = outgoing.Voice.Source.volume;
            var b = incoming.Voice.Source.volume;
            Assert.AreEqual(Mathf.Sqrt(.5f), a, .001f);
            Assert.AreEqual(Mathf.Sqrt(.5f), b, .001f);
            Assert.AreEqual(1f, a * a + b * b, .001f);
            AudioKit.StopMusic();
            outgoing = AudioKit.PlayMusic(_clip);
            incoming = AudioKit.CrossFadeMusic(_clip, 1f, curve: AudioCrossfadeCurve.Linear);
            AudioManager.Instance.MusicPlayback.Tick(.5f, .5f);
            Assert.AreEqual(AudioCrossfadeCurve.Linear, AudioManager.Instance.MusicPlayback.CurrentCurve);
            Assert.AreEqual(.5f, outgoing.Voice.Source.volume, .001f);
            Assert.AreEqual(.5f, incoming.Voice.Source.volume, .001f);
        }

        [Test]
        public void ActionCapturesConfigurationAtAllocationAndResetDoesNotModifyTemplate()
        {
            var options = new AudioSoundOptions { Volume = .4f, Pitch = 1.5f };
            var action = PlaySoundAction.AllocateWithOptions(_clip, options);
            try
            {
                options.Volume = 0f;
                options.Pitch = 0f;
                Assert.IsFalse(action.Execute(0f));
                var first = AudioKit.GetPlayingSounds()[0];
                first.Pause();
                Assert.AreEqual(.4f, first.Voice.Options.Volume);
                Assert.AreEqual(1.5f, first.Voice.Options.Pitch);
                first.SetVolumeScale(.2f);
                first.SetPitch(2f);
                action.Reset();
                Assert.IsFalse(action.Execute(0f));
                var second = AudioKit.GetPlayingSounds()[0];
                second.Pause();
                Assert.AreEqual(.4f, second.Voice.Options.Volume);
                Assert.AreEqual(1.5f, second.Voice.Options.Pitch);
            }
            finally
            {
                action.Deinit();
            }
        }

        [Test]
        public void ExplicitSilenceAndInvalidPitchRemainDistinctFromDefaults()
        {
            var silence = AudioKit.PlaySound(_clip, new AudioSoundOptions { Volume = 0f });
            silence.Pause();
            Assert.IsTrue(silence.IsValid);
            Assert.AreEqual(0f, silence.Voice.Source.volume);
            var before = AudioKit.SoundStats;
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySound(_clip, new AudioSoundOptions { Pitch = 0f }));
            Assert.AreEqual(before, AudioKit.SoundStats);
            Assert.IsTrue(silence.IsValid);
        }

        [Test]
        public void PartialJsonOverwritesInitializedDefaultsWithoutChangingExplicitZero()
        {
            var options = AudioSoundOptions.MusicDefaults;
            JsonUtility.FromJsonOverwrite("{\"Volume\":0,\"Loop\":false}", options);
            Assert.AreEqual(0f, options.Volume);
            Assert.IsFalse(options.Loop);
            Assert.AreEqual(1f, options.Pitch);
            Assert.AreEqual(50f, options.MaxDistance);
            Assert.DoesNotThrow(() => options.Validate(true));
        }

        private PlayerPrefsFloatProperty[] Volumes() => new[]
        {
            AudioKit.Settings.SoundVolume, AudioKit.Settings.MusicVolume, AudioKit.Settings.VoiceVolume
        };

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void InvalidStoredVolumeFallsBackWithoutWritingAndReloadUsesSameRule(float invalid)
        {
            AudioKit.Shutdown();
            for (var i = 3; i < 6; i++)
            {
                PlayerPrefs.SetFloat(AudioTestSettings.Keys[i], invalid);
            }

            var volumes = Volumes();
            foreach (var volume in volumes)
            {
                Assert.AreEqual(1f, volume.Value);
                Assert.AreEqual(invalid, PlayerPrefs.GetFloat(volume.Key));
                volume.Value = .5f;
                PlayerPrefs.SetFloat(volume.Key, invalid);
                var calls = 0;
                volume.Register(_ => calls++);
                volume.Reload();
                Assert.AreEqual(1f, volume.Value);
                Assert.AreEqual(1, calls);
                Assert.AreEqual(invalid, PlayerPrefs.GetFloat(volume.Key));
                volume.Delete();
                Assert.IsFalse(PlayerPrefs.HasKey(volume.Key));
                Assert.AreEqual(1f, volume.Value);
            }
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void EveryVolumeWritePathRejectsInvalidValuesWithoutSideEffects(float invalid)
        {
            foreach (var volume in Volumes())
            {
                volume.Value = .5f;
                var calls = 0;
                volume.Register(_ => calls++);
                Assert.Throws<ArgumentOutOfRangeException>(() => volume.Value = invalid);
                Assert.Throws<ArgumentOutOfRangeException>(() => volume.TrySetValue(invalid));
                Assert.Throws<ArgumentOutOfRangeException>(() => volume.SetValueWithoutCallback(invalid));
                Assert.AreEqual(.5f, volume.Value);
                Assert.AreEqual(.5f, PlayerPrefs.GetFloat(volume.Key));
                Assert.AreEqual(0, calls);
            }
        }

        [TestCase(0f)]
        [TestCase(2f)]
        public void ValidStoredVolumesAndMultipliersRemainUnchanged(float value)
        {
            AudioKit.Shutdown();
            for (var i = 3; i < 6; i++)
            {
                PlayerPrefs.SetFloat(AudioTestSettings.Keys[i], value);
            }

            foreach (var volume in Volumes())
            {
                Assert.AreEqual(value, volume.Value);
                volume.Value = 1f;
                volume.Value = value;
                Assert.AreEqual(value, volume.Value);
                Assert.AreEqual(value, PlayerPrefs.GetFloat(volume.Key));
            }
        }

        [Test]
        public void CallerComparerCannotSuppressInvalidVolumeValidation()
        {
            foreach (var volume in Volumes())
            {
                volume.Value = 0f;
                volume.WithComparer(Mathf.Approximately);
                Assert.IsTrue(Mathf.Approximately(0f, -float.Epsilon));
                Assert.Throws<ArgumentOutOfRangeException>(() => volume.Value = -float.Epsilon);
                Assert.Throws<ArgumentOutOfRangeException>(() => volume.TrySetValue(-float.Epsilon));
                Assert.Throws<ArgumentOutOfRangeException>(() => volume.SetValueWithoutCallback(-float.Epsilon));
                Assert.AreEqual(0f, volume.Value);
                Assert.AreEqual(0f, PlayerPrefs.GetFloat(volume.Key));
            }
        }
    }
}
