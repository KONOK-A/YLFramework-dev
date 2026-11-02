using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using YLFramework.Actions;
using YLFramework.Binding;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioMixerTests
    {
        private const string FixturePath = "Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer";
        private const string ForeignPath = "Assets/Tests/YLFramework/Fixtures/Audio/ForeignAudioMixerFixture.mixer";
        private AudioTestSettings _settings;
        private AudioMixer _mixer;
        private AudioMixer _foreignMixer;
        private AudioMixerGroup _music;
        private AudioMixerGroup _voice;
        private AudioMixerGroup _sound;
        private AudioMixerGroup _ui;
        private AudioMixerConfig _config;
        private AudioClip _clip;
        private GameObject _target;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(FixturePath);
            _foreignMixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(ForeignPath);
            Assert.IsNotNull(_mixer, "Import the independent test mixer before running these tests.");
            Assert.IsNotNull(_foreignMixer);
            _music = Group("Music");
            _voice = Group("Voice");
            _sound = Group("Sound");
            _ui = Group("UI");
            _config = ScriptableObject.CreateInstance<AudioMixerConfig>();
            Configure(_mixer, _music, _voice, _sound);
            _clip = AudioClip.Create("mixer-test", 44100, 1, 44100, false);
            _target = new GameObject("MixerTest.Target");
        }

        [TearDown]
        public void TearDown()
        {
            _settings?.Dispose();
            Object.DestroyImmediate(_config);
            Object.DestroyImmediate(_clip);
            Object.DestroyImmediate(_target);
        }

        private AudioMixerGroup Group(string name)
        {
            var matches = _mixer.FindMatchingGroups(name).Where(group => group.name == name).ToArray();
            Assert.That(matches, Has.Length.EqualTo(1), name);
            return matches[0];
        }

        private void Configure(AudioMixer mixer, AudioMixerGroup music, AudioMixerGroup voice, AudioMixerGroup sound)
        {
            var serialized = new SerializedObject(_config);
            serialized.FindProperty("mixer").objectReferenceValue = mixer;
            serialized.FindProperty("musicGroup").objectReferenceValue = music;
            serialized.FindProperty("voiceGroup").objectReferenceValue = voice;
            serialized.FindProperty("soundGroup").objectReferenceValue = sound;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private AudioHandle Play(int entry, AudioOutputRoute route)
        {
            var options = AudioSoundOptions.Defaults;
            options.OutputRoute = route;
            options.Loop = true;
            switch (entry)
            {
                case 0:
                    return AudioKit.PlayMusic(_clip, outputRoute: route);
                case 1:
                    return AudioKit.PlayVoice(_clip, true, outputRoute: route);
                case 2:
                    return AudioKit.PlayVoiceAt(_clip, Vector3.one, options);
                case 3:
                    return AudioKit.PlayVoiceFollowing(_clip, _target.transform, options);
                case 4:
                    return AudioKit.PlaySound(_clip, options);
                case 5:
                    return AudioKit.PlaySoundAt(_clip, Vector3.one, options);
                case 6:
                    return AudioKit.PlaySoundFollowing(_clip, _target.transform, options);
                case 7:
                    return AudioKit.PlayRandomSound(new[] { null, _clip }, options);
                default:
                    throw new ArgumentOutOfRangeException(nameof(entry));
            }
        }

        [Test]
        public void FixtureHasRealGroupsSnapshotsAndExposedParameters()
        {
            CollectionAssert.AreEquivalent(new[] { "Master", "Music", "Voice", "Sound", "UI" },
                _mixer.FindMatchingGroups(string.Empty).Select(group => group.name));
            foreach (var group in new[] { _music, _voice, _sound, _ui })
            {
                Assert.AreSame(_mixer, group.audioMixer);
            }

            var baseline = _mixer.FindSnapshot("Default");
            var quiet = _mixer.FindSnapshot("Quiet");
            Assert.IsNotNull(baseline);
            Assert.IsNotNull(quiet);
            Assert.AreNotSame(baseline, quiet);
            Assert.AreSame(_mixer, baseline.audioMixer);
            Assert.AreSame(_mixer, quiet.audioMixer);
            Assert.IsTrue(_mixer.GetFloat("MusicVolume", out _), "An exposed name must map to an actual group parameter.");
            Assert.IsTrue(_mixer.GetFloat("SnapshotVolume", out _));
        }

        [Test]
        public void RouteFactoriesDefaultAndOptionsHaveExactThreeStateContract()
        {
            Assert.IsTrue(typeof(AudioOutputRoute).IsSerializable);
            Assert.AreEqual(0, (int)AudioOutputRoute.RouteMode.Inherit);
            Assert.AreEqual(1, (int)AudioOutputRoute.RouteMode.MixerGroup);
            Assert.AreEqual(2, (int)AudioOutputRoute.RouteMode.Direct);
            var inherited = default(AudioOutputRoute);
            var direct = AudioOutputRoute.Direct;
            var explicitRoute = AudioOutputRoute.ToMixer(_ui);
            Assert.AreEqual(AudioOutputRoute.RouteMode.Inherit, inherited.Mode);
            Assert.IsNull(inherited.Group);
            Assert.AreEqual(inherited, AudioOutputRoute.Inherit);
            Assert.AreEqual(inherited, AudioSoundOptions.Defaults.OutputRoute);
            Assert.AreEqual(AudioOutputRoute.RouteMode.Direct, direct.Mode);
            Assert.IsNull(direct.Group);
            Assert.AreEqual(AudioOutputRoute.RouteMode.MixerGroup, explicitRoute.Mode);
            Assert.AreSame(_ui, explicitRoute.Group);
            Assert.DoesNotThrow(() => inherited.Validate());
            Assert.DoesNotThrow(() => direct.Validate());
            Assert.DoesNotThrow(() => explicitRoute.Validate());
            Assert.That(() => AudioOutputRoute.ToMixer(null), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void SerializedIllegalRouteCombinationsAreRejected()
        {
            var invalid = new[]
            {
                new AudioOutputRoute { Mode = (AudioOutputRoute.RouteMode)99 },
                new AudioOutputRoute { Mode = AudioOutputRoute.RouteMode.MixerGroup },
                new AudioOutputRoute { Mode = AudioOutputRoute.RouteMode.Inherit, Group = _ui },
                new AudioOutputRoute { Mode = AudioOutputRoute.RouteMode.Direct, Group = _ui }
            };
            foreach (var route in invalid)
            {
                Assert.That(() => route.Validate(), Throws.InstanceOf<ArgumentException>());
                var options = AudioSoundOptions.Defaults;
                options.OutputRoute = route;
                Assert.That(() => options.Validate(false), Throws.InstanceOf<ArgumentException>());
                Assert.That(() => options.Validate(true), Throws.InstanceOf<ArgumentException>());
            }
        }

        [TestCase(AudioOutputRoute.RouteMode.Inherit)]
        [TestCase(AudioOutputRoute.RouteMode.Direct)]
        public void InspectorEmptyGroupIsValidForInheritedAndDirectRoutes(AudioOutputRoute.RouteMode mode)
        {
            var emitter = _target.AddComponent<AudioEmitter>();
            var serialized = new SerializedObject(emitter);
            var property = serialized.FindProperty("outputRoute");
            property.FindPropertyRelative("Mode").intValue = (int)mode;
            property.FindPropertyRelative("Group").objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var route = (AudioOutputRoute)typeof(AudioEmitter).GetField("outputRoute",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(emitter);
            Assert.IsTrue(route.Group == null, "An empty Inspector reference is Unity-null.");
            Assert.DoesNotThrow(() => route.Validate());
            var options = AudioSoundOptions.Defaults;
            options.OutputRoute = route;
            AudioKit.Settings.SoundOutputGroup.Value = _sound;
            var handle = AudioKit.PlaySound(_clip, options);
            handle.Pause();
            Assert.AreSame(mode == AudioOutputRoute.RouteMode.Inherit ? _sound : null, handle.Voice.Source.outputAudioMixerGroup);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        public void EveryEntryValidatesRoutingBeforeHostAllocationOrSlotReplacement(int entry)
        {
            var invalid = new AudioOutputRoute { Mode = AudioOutputRoute.RouteMode.MixerGroup };
            Assert.That(() => Play(entry, invalid), Throws.InstanceOf<ArgumentException>());
            Assert.IsFalse(AudioManager.HasInstance);
            var old = Play(entry, AudioOutputRoute.ToMixer(_ui));
            old.Pause();
            var generation = old.Generation;
            var source = old.Voice.Source;
            var count = AudioKit.SoundCount;
            Assert.That(() => Play(entry, invalid), Throws.InstanceOf<ArgumentException>());
            Assert.IsTrue(old.IsValid, "Invalid replacement must not stop the existing request.");
            Assert.IsTrue(old.IsPaused);
            Assert.AreEqual(generation, old.Generation);
            Assert.AreSame(source, old.Voice.Source);
            Assert.AreSame(_ui, source.outputAudioMixerGroup);
            Assert.AreEqual(count, AudioKit.SoundCount);
        }

        [Test]
        public void InvalidRouteDoesNotConsumeDeduplicationOrBypassValidationOnRejectedSound()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            var options = AudioSoundOptions.Defaults;
            options.OutputRoute = new AudioOutputRoute { Mode = (AudioOutputRoute.RouteMode)9 };
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.That(() => AudioKit.PlaySound(_clip, options), Throws.InstanceOf<ArgumentException>());
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.Settings.IsSoundOn.Value = true;
            Assert.That(() => AudioKit.PlaySound(_clip, options), Throws.InstanceOf<ArgumentException>());
            var accepted = AudioKit.PlaySound(_clip);
            accepted.Pause();
            Assert.IsTrue(accepted.IsValid);
            Assert.That(() => AudioKit.PlaySound(_clip, options), Throws.InstanceOf<ArgumentException>(),
                "Deduplication rejection must not hide a malformed route.");
            Assert.IsFalse(AudioKit.PlaySound(_clip).IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [Test]
        public void ActionRejectsInvalidRouteBeforeAllocationOrPlayback()
        {
            var initialized = YLFramework.Actions.ActionKit.IsInitialized;
            var invalid = new AudioOutputRoute { Mode = AudioOutputRoute.RouteMode.Direct, Group = _ui };
            Assert.That(() => PlaySoundAction.Allocate(_clip, outputRoute: invalid), Throws.InstanceOf<ArgumentException>());
            Assert.AreEqual(initialized, YLFramework.Actions.ActionKit.IsInitialized);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [Test]
        public void CaptureCopiesRouteValueAndBorrowsTheExistingMixerGroup()
        {
            var options = new AudioSoundOptions { OutputRoute = AudioOutputRoute.ToMixer(_ui) };
            var captured = AudioPlaybackOptions.Capture(options);
            var copy = captured;
            Assert.AreSame(_ui, captured.OutputRoute.Group, "Capture borrows the asset instead of cloning it.");
            options.OutputRoute.Group = _sound;
            copy.OutputRoute.Group = _voice;
            Assert.AreSame(_ui, captured.OutputRoute.Group);
            Assert.AreSame(_sound, options.OutputRoute.Group);
            Assert.AreSame(_voice, copy.OutputRoute.Group);
            var handle = AudioKit.PlaySound(_clip, options);
            handle.Pause();
            Assert.AreSame(_sound, handle.Voice.Options.OutputRoute.Group);
            handle.Stop();
            Assert.AreEqual(default(AudioPlaybackOptions), handle.Voice.Options);
            Assert.IsTrue(ReferenceEquals(null, handle.Voice.Options.OutputRoute.Group));
            Assert.AreSame(_sound, options.OutputRoute.Group);
            Assert.IsTrue(_ui);
            Assert.IsTrue(_sound);
            Assert.IsTrue(_voice);
        }

        [Test]
        public void ActionResetRetainsBorrowedTemplateAndDeinitClearsTemplateAndRequestReferences()
        {
            var initialized = YLFramework.Actions.ActionKit.IsInitialized;
            var field = typeof(PlaySoundAction).GetField("_options",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            Assert.AreEqual(typeof(AudioPlaybackOptions), field.FieldType);
            var options = new AudioSoundOptions { Loop = true, OutputRoute = AudioOutputRoute.ToMixer(_ui) };
            var action = PlaySoundAction.AllocateWithOptions(_clip, options);
            try
            {
                var template = (AudioPlaybackOptions)field.GetValue(action);
                Assert.AreSame(_ui, template.OutputRoute.Group);
                options.OutputRoute = AudioOutputRoute.Direct;
                Assert.IsFalse(action.Execute(0f));
                var old = AudioKit.GetPlayingSounds()[0];
                old.Pause();
                Assert.AreSame(_ui, old.Voice.Options.OutputRoute.Group);
                Assert.AreSame(_ui, old.Voice.Source.outputAudioMixerGroup);
                old.SetVolumeScale(.2f);
                action.Reset();
                Assert.IsFalse(old.IsValid);
                Assert.AreEqual(default(AudioPlaybackOptions), old.Voice.Options);
                Assert.IsTrue(ReferenceEquals(null, old.Voice.Options.OutputRoute.Group));
                Assert.AreEqual(template, (AudioPlaybackOptions)field.GetValue(action));
                Assert.IsFalse(action.Execute(0f));
                var current = AudioKit.GetPlayingSounds()[0];
                current.Pause();
                Assert.AreEqual(template, current.Voice.Options);
                Assert.AreSame(_ui, current.Voice.Source.outputAudioMixerGroup);
                action.Deinit();
                var cleared = (AudioPlaybackOptions)field.GetValue(action);
                Assert.AreEqual(default(AudioPlaybackOptions), cleared);
                Assert.IsTrue(ReferenceEquals(null, cleared.OutputRoute.Group));
                Assert.AreEqual(default(AudioPlaybackOptions), current.Voice.Options);
                Assert.IsTrue(ReferenceEquals(null, current.Voice.Options.OutputRoute.Group));
                Assert.IsNull(current.Voice.Source.outputAudioMixerGroup);
                Assert.IsTrue(_ui, "Request cleanup must not destroy a borrowed mixer group.");
                Assert.IsTrue(_mixer);
                Assert.IsTrue(_clip);
            }
            finally
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }

                if (!initialized)
                {
                    YLFramework.Actions.ActionKit.Shutdown();
                }
            }
        }

        [Test]
        public void ConfigurationValidatesAllCategoriesBeforeApplyingAnything()
        {
            Assert.AreSame(_mixer, _config.Mixer);
            Assert.AreSame(_music, _config.MusicGroup);
            Assert.AreSame(_voice, _config.VoiceGroup);
            Assert.AreSame(_sound, _config.SoundGroup);
            Assert.DoesNotThrow(() => _config.Validate());
            AudioKit.ApplyMixerConfig(_config);
            var foreign = _foreignMixer.FindMatchingGroups("Master").Single();
            for (var category = 0; category < 3; category++)
            {
                Configure(_mixer, category == 0 ? foreign : _ui, category == 1 ? foreign : _ui, category == 2 ? foreign : _ui);
                Assert.That(() => _config.Validate(), Throws.InstanceOf<ArgumentException>());
                Assert.That(() => AudioKit.ApplyMixerConfig(_config), Throws.InstanceOf<ArgumentException>());
                Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
                Assert.AreSame(_voice, AudioKit.Settings.VoiceOutputGroup.Value);
                Assert.AreSame(_sound, AudioKit.Settings.SoundOutputGroup.Value);
            }
            Configure(null, _music, null, null);
            Assert.That(() => AudioKit.ApplyMixerConfig(_config), Throws.InstanceOf<ArgumentException>());
            Configure(null, null, null, null);
            Assert.DoesNotThrow(() => _config.Validate());
            AudioKit.ApplyMixerConfig(_config);
            Assert.IsNull(AudioKit.Settings.MusicOutputGroup.Value);
            Assert.IsNull(AudioKit.Settings.VoiceOutputGroup.Value);
            Assert.IsNull(AudioKit.Settings.SoundOutputGroup.Value);
            Assert.That(() => AudioKit.ApplyMixerConfig(null), Throws.InstanceOf<ArgumentException>());
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void ApplyCopiesConfigurationAndClearIsNonCreatingEvenBeforeInitialization()
        {
            AudioKit.Shutdown();
            Assert.IsNull(AudioKit.CurrentSettings);
            AudioKit.ClearMixerRouting();
            Assert.IsNull(AudioKit.CurrentSettings);
            var sourceCount = Object.FindObjectsOfType<AudioSource>(true).Length;
            var listenerCount = Object.FindObjectsOfType<AudioListener>(true).Length;
            AudioKit.ApplyMixerConfig(_config);
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
            Configure(_mixer, _ui, null, null);
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value, "Asset edits do not automatically reapply routing.");
            AudioKit.ApplyMixerConfig(_config);
            Assert.AreSame(_ui, AudioKit.Settings.MusicOutputGroup.Value);
            Assert.IsNull(AudioKit.Settings.VoiceOutputGroup.Value);
            Assert.IsNull(AudioKit.Settings.SoundOutputGroup.Value);
            AudioKit.ClearMixerRouting();
            Assert.IsNull(AudioKit.Settings.MusicOutputGroup.Value);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(sourceCount, Object.FindObjectsOfType<AudioSource>(true).Length);
            Assert.AreEqual(listenerCount, Object.FindObjectsOfType<AudioListener>(true).Length);
            Assert.AreSame(_mixer, _config.Mixer, "Clear must not modify the borrowed configuration.");
            Assert.AreSame(_ui, _config.MusicGroup);
        }

        [Test]
        public void RoutingIsTransientPreservesSixSaveKeysAndDoesNotChangeGlobalComparer()
        {
            var comparer = BindableProperty<AudioMixerGroup>.Comparer;
            AudioKit.Shutdown();
            foreach (var key in AudioTestSettings.Keys)
            {
                PlayerPrefs.DeleteKey(key);
            }

            var settings = AudioKit.Settings;
            var groups = new[] { settings.MusicOutputGroup, settings.VoiceOutputGroup, settings.SoundOutputGroup };
            foreach (var property in groups)
            {
                Assert.AreEqual(typeof(BindableProperty<AudioMixerGroup>), property.GetType());
                Assert.IsNull(property.Value);
            }
            AudioKit.ApplyMixerConfig(_config);
            foreach (var key in AudioTestSettings.Keys)
            {
                Assert.IsFalse(PlayerPrefs.HasKey(key), key);
            }

            AudioKit.Shutdown();
            foreach (var property in groups)
            {
                Assert.IsNull(property.Value, "Shutdown releases borrowed group references.");
            }

            var recreated = AudioKit.Settings;
            Assert.IsNull(recreated.MusicOutputGroup.Value);
            Assert.IsNull(recreated.VoiceOutputGroup.Value);
            Assert.IsNull(recreated.SoundOutputGroup.Value);
            Assert.AreSame(comparer, BindableProperty<AudioMixerGroup>.Comparer);
            foreach (var key in AudioTestSettings.Keys)
            {
                Assert.IsFalse(PlayerPrefs.HasKey(key), key);
            }

            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void ShutdownDisconnectsOldRoutingSubscriptionsAndOldHandles()
        {
            AudioKit.ApplyMixerConfig(_config);
            var settings = AudioKit.Settings;
            var calls = 0;
            settings.MusicOutputGroup.Register(_ => calls++);
            settings.VoiceOutputGroup.Register(_ => calls++);
            settings.SoundOutputGroup.Register(_ => calls++);
            var old = AudioKit.PlaySound(_clip);
            old.Pause();
            AudioKit.Shutdown();
            var callsAfterShutdown = calls;
            var fresh = AudioKit.Settings;
            fresh.IsSoundOn.SetValueWithoutCallback(true);
            fresh.SoundOutputGroup.Value = _sound;
            var current = AudioKit.PlaySound(_clip);
            current.Pause();
            settings.MusicOutputGroup.Value = _ui;
            settings.VoiceOutputGroup.Value = _ui;
            settings.SoundOutputGroup.Value = _ui;
            Assert.AreEqual(callsAfterShutdown, calls, "Old subscriptions must be detached.");
            Assert.AreSame(_sound, current.Voice.Source.outputAudioMixerGroup);
            Assert.IsNull(fresh.MusicOutputGroup.Value);
            Assert.IsNull(fresh.VoiceOutputGroup.Value);
            old.Stop();
            old.Resume();
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(current.IsValid);
            Assert.IsTrue(current.IsPaused);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ReentrantDefaultChangesUseLatestSettingsRatherThanStaleNotificationPayload(int category)
        {
            var settings = AudioKit.Settings;
            var property = category == 0 ? settings.MusicOutputGroup : category == 1 ? settings.VoiceOutputGroup : settings.SoundOutputGroup;
            var foreign = _foreignMixer.FindMatchingGroups("Master").Single();
            var calls = 0;
            // Subscribe before host creation so the outer manager notification runs after our nested change.
            property.Register(group =>
            {
                calls++;
                if (group == _ui)
                {
                    property.Value = foreign;
                }
            });
            var handle = Play(category == 0 ? 0 : category == 1 ? 1 : 4, AudioOutputRoute.Inherit);
            handle.Pause();
            var generation = handle.Generation;
            property.Value = _ui;
            Assert.AreEqual(2, calls, "Exercise both the outer and nested notifications.");
            Assert.AreSame(foreign, property.Value);
            Assert.AreSame(property.Value, handle.Voice.Source.outputAudioMixerGroup,
                "The stale outer payload must not overwrite the newer default.");
            Assert.AreEqual(generation, handle.Generation);
            Assert.IsTrue(handle.IsPaused);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShutdownDuringApplyDoesNotRestoreReferencesInDisposedSettingsOrContinueIntoNewSession(bool recreate)
        {
            var oldSettings = AudioKit.Settings;
            var called = false;
            oldSettings.MusicOutputGroup.Register(_ =>
            {
                Assert.IsFalse(called);
                called = true;
                AudioKit.Shutdown();
                if (recreate)
                {
                    AudioKit.Initialize();
                }
            });
            AudioKit.ApplyMixerConfig(_config);
            Assert.IsTrue(called);
            Assert.IsNull(oldSettings.MusicOutputGroup.Value);
            Assert.IsNull(oldSettings.VoiceOutputGroup.Value);
            Assert.IsNull(oldSettings.SoundOutputGroup.Value, "An interrupted Apply must not retain assets in a disposed session.");
            if (recreate)
            {
                var current = AudioKit.CurrentSettings;
                Assert.IsNotNull(current);
                Assert.AreNotSame(oldSettings, current);
                Assert.IsNull(current.MusicOutputGroup.Value);
                Assert.IsNull(current.VoiceOutputGroup.Value);
                Assert.IsNull(current.SoundOutputGroup.Value);
            }
            else
            {
                Assert.IsNull(AudioKit.CurrentSettings);
            }

            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreSame(_music, _config.MusicGroup);
            Assert.AreSame(_voice, _config.VoiceGroup);
            Assert.AreSame(_sound, _config.SoundGroup);
        }

        [Test]
        public void DestroyedConfigurationIsRejectedWithoutChangingDefaults()
        {
            AudioKit.ApplyMixerConfig(_config);
            Object.DestroyImmediate(_config);
            Assert.That(() => AudioKit.ApplyMixerConfig(_config), Throws.InstanceOf<ArgumentException>());
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
            Assert.AreSame(_voice, AudioKit.Settings.VoiceOutputGroup.Value);
            Assert.AreSame(_sound, AudioKit.Settings.SoundOutputGroup.Value);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [TestCase(0f, -80f)]
        [TestCase(.000001f, -80f)]
        [TestCase(.5f, -6.0206f)]
        [TestCase(1f, 0f)]
        public void LinearVolumeConversionHasFloorAndCorrectLogarithmicMapping(float volume, float expected)
        {
            Assert.AreEqual(expected, AudioMixerControl.LinearVolumeToDecibels(volume), .0001f);
        }

        [TestCase(-100f, 0f)]
        [TestCase(-80f, 0f)]
        [TestCase(-6.0206f, .5f)]
        [TestCase(0f, 1f)]
        [TestCase(20f, 1f)]
        public void DecibelConversionClampsAndTreatsFloorAsZero(float decibels, float expected)
        {
            Assert.AreEqual(expected, AudioMixerControl.DecibelsToLinearVolume(decibels), .0001f);
        }

        [TestCase(-.1f)]
        [TestCase(1.1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void LinearVolumeRejectsInvalidInputs(float value)
        {
            Assert.That(() => AudioMixerControl.LinearVolumeToDecibels(value), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TrySetVolume(_mixer, "MusicVolume", value), Throws.InstanceOf<ArgumentException>());
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void RawFloatAndDecibelConversionRejectNonFiniteInputs(float value)
        {
            Assert.That(() => AudioMixerControl.TrySetFloat(_mixer, "MusicVolume", value), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.DecibelsToLinearVolume(value), Throws.InstanceOf<ArgumentException>());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t")]
        public void ParameterMethodsRejectMissingNames(string name)
        {
            Assert.That(() => AudioMixerControl.TrySetFloat(_mixer, name, 0f), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TryGetFloat(_mixer, name, out _), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TryClearFloat(_mixer, name), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TrySetVolume(_mixer, name, .5f), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TryGetVolume(_mixer, name, out _), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void ControlMethodsRejectNullMixerWithoutInitializingAudioKit()
        {
            AudioKit.Shutdown();
            Assert.That(() => AudioMixerControl.TrySetFloat(null, "MusicVolume", 0), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TryGetFloat(null, "MusicVolume", out _), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TryClearFloat(null, "MusicVolume"), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TrySetVolume(null, "MusicVolume", .5f), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TryGetVolume(null, "MusicVolume", out _), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TransitionToSnapshot(null, 0), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TransitionToSnapshots(null, new[] { _mixer.FindSnapshot("Default") }, new[] { 1f }, 0),
                Throws.InstanceOf<ArgumentException>());
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void SnapshotTransitionsRejectInvalidDuration(float duration)
        {
            var snapshot = _mixer.FindSnapshot("Default");
            Assert.That(() => AudioMixerControl.TransitionToSnapshot(snapshot, duration), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TransitionToSnapshots(_mixer, new[] { snapshot }, new[] { 1f }, duration),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void SnapshotMixRejectsMalformedArraysForeignSnapshotsAndInvalidWeightsWithoutMutation()
        {
            var baseline = _mixer.FindSnapshot("Default");
            var quiet = _mixer.FindSnapshot("Quiet");
            var foreign = _foreignMixer.FindSnapshot("Default");
            Assert.That(() => AudioMixerControl.TransitionToSnapshots(_mixer, null, new[] { 1f }, 0), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TransitionToSnapshots(_mixer, new[] { baseline }, null, 0), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TransitionToSnapshots(_mixer, Array.Empty<AudioMixerSnapshot>(), Array.Empty<float>(), 0),
                Throws.InstanceOf<ArgumentException>());
            Assert.That(() => AudioMixerControl.TransitionToSnapshots(_mixer, new[] { baseline, quiet }, new[] { 1f }, 0),
                Throws.InstanceOf<ArgumentException>());
            foreach (var snapshots in new[] { new[] { baseline, (AudioMixerSnapshot)null }, new[] { baseline, foreign } })
            {
                var copy = (AudioMixerSnapshot[])snapshots.Clone();
                Assert.That(() => AudioMixerControl.TransitionToSnapshots(_mixer, snapshots, new[] { 1f, 1f }, 0),
                    Throws.InstanceOf<ArgumentException>());
                CollectionAssert.AreEqual(copy, snapshots);
            }
            foreach (var weights in new[]
            {
                new[] { 0f, 0f }, new[] { 1f, -1f }, new[] { float.NaN, 1f },
                new[] { 1f, float.PositiveInfinity }, new[] { float.NegativeInfinity, 1f }
            })
            {
                var copy = (float[])weights.Clone();
                var snapshots = new[] { baseline, quiet };
                Assert.That(() => AudioMixerControl.TransitionToSnapshots(_mixer, snapshots, weights, 0), Throws.InstanceOf<ArgumentException>());
                CollectionAssert.AreEqual(copy, weights);
                CollectionAssert.AreEqual(new[] { baseline, quiet }, snapshots);
            }
        }
    }
}
