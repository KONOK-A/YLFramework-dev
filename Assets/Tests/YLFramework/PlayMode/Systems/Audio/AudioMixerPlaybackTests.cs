#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    // The PlayMode assembly also targets players. Fixtures are editor-loaded, never runtime Resources.
    public class AudioMixerPlaybackTests
    {
        private const string FixturePath = "Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer";
        private const string ForeignPath = "Assets/Tests/YLFramework/Fixtures/Audio/ForeignAudioMixerFixture.mixer";
        private AudioMixer _mixer;
        private AudioMixer _foreignMixer;
        private AudioMixerGroup _music;
        private AudioMixerGroup _voice;
        private AudioMixerGroup _sound;
        private AudioMixerGroup _ui;
        private AudioMixerGroup _foreign;
        private AudioMixerSnapshot _default;
        private AudioMixerSnapshot _quiet;
        private AudioMixerConfig _config;
        private AudioClip _clip;
        private GameObject _target;
        private MixerPlaybackSettings _settings;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private bool _ownsActionSession;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _settings = new MixerPlaybackSettings();
            _ownsActionSession = false;
            _mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(FixturePath);
            _foreignMixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(ForeignPath);
            Assert.IsNotNull(_mixer, "Import the independent mixer fixture before running PlayMode tests.");
            Assert.IsNotNull(_foreignMixer);
            _music = Group("Music");
            _voice = Group("Voice");
            _sound = Group("Sound");
            _ui = Group("UI");
            _foreign = _foreignMixer.FindMatchingGroups("Master").Single();
            _default = _mixer.FindSnapshot("Default");
            _quiet = _mixer.FindSnapshot("Quiet");
            Assert.IsNotNull(_default);
            Assert.IsNotNull(_quiet);
            _config = ScriptableObject.CreateInstance<AudioMixerConfig>();
            var config = new SerializedObject(_config);
            config.FindProperty("mixer").objectReferenceValue = _mixer;
            config.FindProperty("musicGroup").objectReferenceValue = _music;
            config.FindProperty("voiceGroup").objectReferenceValue = _voice;
            config.FindProperty("soundGroup").objectReferenceValue = _sound;
            config.ApplyModifiedPropertiesWithoutUndo();
            _clip = AudioClip.Create("mixer-playback", 220500, 1, 44100, false);
            _target = OwnObject("MixerPlayback.Target");
            _target.transform.position = new Vector3(3f, 4f, 5f);
            if (!Object.FindObjectsOfType<AudioListener>().Any(listener => listener.isActiveAndEnabled))
            {
                OwnObject("MixerPlayback.Listener").AddComponent<AudioListener>();
            }
            // Native writes must never run in Awake/OnEnable or the first initialization frame.
            yield return null;
            Assert.IsTrue(AudioMixerControl.TryClearFloat(_mixer, "MusicVolume"));
            Assert.IsTrue(AudioMixerControl.TryClearFloat(_mixer, "SnapshotVolume"));
            AudioMixerControl.TransitionToSnapshot(_default, 0f);
            yield return WaitForParameter("MusicVolume", 0f);
            yield return WaitForParameter("SnapshotVolume", -3f);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            if (_ownsActionSession)
            {
                YLFramework.Actions.ActionKit.Shutdown();
            }

            foreach (var root in _objects)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }

            _objects.Clear();
            if (_config)
            {
                Object.Destroy(_config);
            }

            if (_clip)
            {
                Object.Destroy(_clip);
            }

            _settings?.Dispose();
            yield return null;
            if (_mixer && _default)
            {
                Assert.IsTrue(AudioMixerControl.TryClearFloat(_mixer, "MusicVolume"));
                Assert.IsTrue(AudioMixerControl.TryClearFloat(_mixer, "SnapshotVolume"));
                AudioMixerControl.TransitionToSnapshot(_default, 0f);
                yield return WaitForParameter("MusicVolume", 0f);
                yield return WaitForParameter("SnapshotVolume", -3f);
            }
        }

        private GameObject OwnObject(string name)
        {
            var root = new GameObject(name);
            _objects.Add(root);
            return root;
        }

        private AudioMixerGroup Group(string name)
        {
            var groups = _mixer.FindMatchingGroups(name).Where(group => group.name == name).ToArray();
            Assert.That(groups, Has.Length.EqualTo(1), name);
            return groups[0];
        }

        private IEnumerator WaitForParameter(string name, float expected)
        {
            var deadline = Time.realtimeSinceStartup + 2f;
            do
            {
                Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, name, out var value), name);
                if (Mathf.Abs(value - expected) < .01f)
                {
                    yield break;
                }

                yield return null;
            } while (Time.realtimeSinceStartup < deadline);
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, name, out var actual), name);
            Assert.AreEqual(expected, actual, .01f, "The real native snapshot/parameter must reach its expected value: " + name);
        }

        private AudioHandle PlayEntry(int entry, AudioOutputRoute route, AudioMixerGroup expected, Action started = null)
        {
            var options = AudioSoundOptions.Defaults;
            options.OutputRoute = route;
            options.Loop = true;
            options.Volume = .5f;
            options.Pitch = 1.25f;
            options.MinDistance = 2f;
            options.MaxDistance = 40f;
            options.RolloffMode = AudioRolloffMode.Logarithmic;
            Action<AudioClip, AudioHandle> callback = (clip, handle) =>
            {
                Assert.AreSame(_clip, clip);
                Assert.AreSame(expected, handle.Voice.Source.outputAudioMixerGroup,
                    "Routing must already be configured when onStarted runs.");
                Assert.AreSame(clip, handle.Voice.Source.clip);
                Assert.AreEqual(.5f, handle.Voice.Source.volume, .001f);
                if (entry == 2 || entry == 3)
                {
                    Assert.AreEqual(1f, handle.Voice.Source.spatialBlend);
                    Assert.AreEqual(1.25f, handle.Voice.Source.pitch);
                    Assert.AreEqual(2f, handle.Voice.Source.minDistance);
                    Assert.AreEqual(40f, handle.Voice.Source.maxDistance);
                }
                started?.Invoke();
            };
            switch (entry)
            {
                case 0:
                    return AudioKit.PlayMusic(_clip, true, .5f, callback, outputRoute: route);
                case 1:
                    return AudioKit.PlayVoice(_clip, true, .5f, callback, outputRoute: route);
                case 2:
                    return AudioKit.PlayVoiceAt(_clip, Vector3.one, options, callback);
                case 3:
                    return AudioKit.PlayVoiceFollowing(_clip, _target.transform, options, callback);
                case 4:
                    return AudioKit.PlaySound(_clip, options);
                case 5:
                    return AudioKit.PlaySoundAt(_clip, Vector3.one, options);
                case 6:
                    return AudioKit.PlaySoundFollowing(_clip, _target.transform, options);
                case 7:
                    return AudioKit.PlayRandomSound(new[] { null, _clip, null }, options);
                default:
                    throw new ArgumentOutOfRangeException(nameof(entry));
            }
        }

        [UnityTest]
        public IEnumerator AllEightEntriesUseDefaultsOverridesAndDirectBeforePlayback()
        {
            AudioKit.ApplyMixerConfig(_config);
            var starts = 0;
            for (var entry = 0; entry < 8; entry++)
            {
                var category = entry == 0 ? _music : entry < 4 ? _voice : _sound;
                var routes = new[] { AudioOutputRoute.Inherit, AudioOutputRoute.ToMixer(_foreign), AudioOutputRoute.Direct };
                var groups = new[] { category, _foreign, null };
                for (var mode = 0; mode < routes.Length; mode++)
                {
                    var handle = PlayEntry(entry, routes[mode], groups[mode], () => starts++);
                    Assert.IsTrue(handle.IsValid, "Entry " + entry + ", mode " + mode);
                    Assert.AreSame(groups[mode], handle.Voice.Source.outputAudioMixerGroup);
                    Assert.AreEqual(routes[mode], handle.Voice.Options.OutputRoute);
                    Assert.AreEqual(entry == 2 || entry == 3 || entry == 5 || entry == 6 ? 1f : 0f,
                        handle.Voice.Source.spatialBlend);
                    yield return null;
                    Assert.IsTrue(handle.IsPlaying);
                    handle.Stop();
                    Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup, "Stop must clear both slots and pooled sources.");
                    Assert.AreEqual(default(AudioPlaybackOptions), handle.Voice.Options,
                        "Stop clears the entire request snapshot, including its route.");
                    Assert.IsTrue(ReferenceEquals(null, handle.Voice.Options.OutputRoute.Group),
                        "Stop releases the managed borrowed group reference, not just its native routing.");
                }
            }
            Assert.AreEqual(12, starts, "Four slot entry points, each exercised in all three route modes.");
        }

        [UnityTest]
        public IEnumerator AllEightEntriesStillPlayWithoutAnyMixerConfiguration()
        {
            Assert.IsNull(AudioKit.Settings.MusicOutputGroup.Value);
            Assert.IsNull(AudioKit.Settings.VoiceOutputGroup.Value);
            Assert.IsNull(AudioKit.Settings.SoundOutputGroup.Value);
            for (var entry = 0; entry < 8; entry++)
            {
                var handle = PlayEntry(entry, default, null);
                Assert.IsTrue(handle.IsValid);
                Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
                yield return null;
                Assert.IsTrue(handle.IsPlaying);
                handle.Stop();
            }
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator ApplyingNewConfigurationUpdatesRunningSourcesWithoutRestartingOrRepeatingCallbacks()
        {
            AudioKit.ApplyMixerConfig(_config);
            var starts = 0;
            var finishes = 0;
            var music = AudioKit.PlayMusic(_clip, true, .5f, (_, __) => starts++, (_, __) => finishes++);
            var voice = AudioKit.PlayVoice(_clip, true, .5f, (_, __) => starts++, (_, __) => finishes++);
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            options.Volume = .5f;
            var sound = AudioKit.PlaySound(_clip, options, (_, __) => finishes++);
            var handles = new[] { music, voice, sound };
            yield return new WaitForSecondsRealtime(.1f);
            var generations = handles.Select(handle => handle.Generation).ToArray();
            var samples = handles.Select(handle => handle.Voice.Source.timeSamples).ToArray();
            foreach (var sample in samples)
            {
                Assert.Greater(sample, 0);
            }

            var config = new SerializedObject(_config);
            config.FindProperty("musicGroup").objectReferenceValue = _ui;
            config.FindProperty("voiceGroup").objectReferenceValue = _sound;
            config.FindProperty("soundGroup").objectReferenceValue = _voice;
            config.ApplyModifiedPropertiesWithoutUndo();
            AudioKit.ApplyMixerConfig(_config);
            var groups = new[] { _ui, _sound, _voice };
            for (var i = 0; i < handles.Length; i++)
            {
                Assert.AreSame(groups[i], handles[i].Voice.Source.outputAudioMixerGroup);
                Assert.AreEqual(generations[i], handles[i].Generation);
                Assert.GreaterOrEqual(handles[i].Voice.Source.timeSamples, samples[i], "Routing must not restart running playback.");
                Assert.IsTrue(handles[i].IsPlaying);
                Assert.IsFalse(handles[i].IsPaused);
                Assert.AreEqual(.5f, handles[i].Voice.Source.volume, .001f);
            }
            Assert.AreEqual(2, starts);
            Assert.AreEqual(0, finishes);
        }

        [UnityTest]
        public IEnumerator DefaultChangesUpdateAllInheritedPausedAndMutedRequestsWithoutRestarting()
        {
            AudioKit.ApplyMixerConfig(_config);
            var starts = 0;
            var finishes = 0;
            var music = AudioKit.PlayMusic(_clip, true, .5f, (_, __) => starts++, (_, __) => finishes++);
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            options.Volume = .5f;
            options.Pitch = 1.25f;
            var voice = AudioKit.PlayVoiceFollowing(_clip, _target.transform, options, (_, __) => starts++, (_, __) => finishes++);
            var sound = AudioKit.PlaySoundAt(_clip, Vector3.one, options, (_, __) => finishes++);
            var secondSound = AudioKit.PlaySound(_clip, options);
            options.OutputRoute = AudioOutputRoute.ToMixer(_foreign);
            var overridden = AudioKit.PlaySound(_clip, options);
            options.OutputRoute = AudioOutputRoute.Direct;
            var direct = AudioKit.PlaySound(_clip, options);
            yield return new WaitForSecondsRealtime(.1f);
            var inherited = new[] { music, voice, sound, secondSound };
            foreach (var handle in inherited)
            {
                handle.Pause();
            }

            var generations = inherited.Select(handle => handle.Generation).ToArray();
            var samples = inherited.Select(handle => handle.Voice.Source.timeSamples).ToArray();
            Assert.Greater(samples[0], 0);
            var settings = AudioKit.Settings;
            settings.IsMusicMuted.Value = true;
            settings.IsVoiceMuted.Value = true;
            settings.IsSoundMuted.Value = true;
            settings.MusicVolume.Value = .4f;
            settings.VoiceVolume.Value = .6f;
            settings.SoundVolume.Value = .8f;
            music.SetPitch(1.5f);
            settings.MusicOutputGroup.Value = _ui;
            Assert.AreSame(_ui, music.Voice.Source.outputAudioMixerGroup);
            Assert.AreSame(_voice, voice.Voice.Source.outputAudioMixerGroup, "Default updates must be category-isolated.");
            Assert.AreSame(_sound, sound.Voice.Source.outputAudioMixerGroup);
            settings.VoiceOutputGroup.Value = _ui;
            settings.SoundOutputGroup.Value = _ui;
            yield return null;
            for (var i = 0; i < inherited.Length; i++)
            {
                var handle = inherited[i];
                Assert.AreSame(_ui, handle.Voice.Source.outputAudioMixerGroup);
                Assert.AreEqual(generations[i], handle.Generation);
                Assert.AreEqual(samples[i], handle.Voice.Source.timeSamples, "Routing refresh must not seek or restart a paused source.");
                Assert.IsTrue(handle.IsPaused);
                Assert.IsTrue(handle.Voice.Source.mute);
                Assert.AreSame(_clip, handle.Voice.Source.clip);
                Assert.IsTrue(handle.Voice.Source.loop);
                Assert.AreEqual(i == 0 ? .2f : i == 1 ? .3f : .4f, handle.Voice.Source.volume, .001f);
                Assert.AreEqual(i == 0 ? 1.5f : 1.25f, handle.Voice.Source.pitch);
            }
            Assert.AreEqual(_target.transform.position, voice.Voice.Source.transform.position);
            Assert.AreEqual(Vector3.one, sound.Voice.Source.transform.position);
            Assert.AreSame(_foreign, overridden.Voice.Source.outputAudioMixerGroup);
            Assert.IsNull(direct.Voice.Source.outputAudioMixerGroup);
            Assert.AreEqual(2, starts);
            Assert.AreEqual(0, finishes);
            settings.IsMusicOn.Value = false;
            settings.IsVoiceOn.Value = false;
            settings.MusicOutputGroup.Value = _music;
            settings.VoiceOutputGroup.Value = _voice;
            Assert.AreSame(_music, music.Voice.Source.outputAudioMixerGroup);
            Assert.AreSame(_voice, voice.Voice.Source.outputAudioMixerGroup);
            Assert.AreEqual(generations[0], music.Generation);
            Assert.AreEqual(generations[1], voice.Generation);
            settings.IsMusicOn.Value = true;
            settings.IsVoiceOn.Value = true;
            Assert.IsTrue(music.IsPaused);
            Assert.IsTrue(voice.IsPaused);
            AudioKit.ClearMixerRouting();
            foreach (var handle in inherited)
            {
                Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
            }

            Assert.AreSame(_foreign, overridden.Voice.Source.outputAudioMixerGroup);
            Assert.IsNull(direct.Voice.Source.outputAudioMixerGroup);
            settings.IsMusicMuted.Value = false;
            settings.IsVoiceMuted.Value = false;
            settings.IsSoundMuted.Value = false;
            foreach (var handle in inherited)
            {
                Assert.IsFalse(handle.Voice.Source.mute);
                Assert.IsTrue(handle.IsPaused);
                handle.Resume();
            }
            yield return null;
            foreach (var handle in inherited)
            {
                Assert.IsTrue(handle.IsPlaying);
            }

            Assert.AreEqual(2, starts);
            Assert.AreEqual(0, finishes);
        }

        [UnityTest]
        public IEnumerator PendingAndSwitchReplayedSlotsResolveLatestDefaultsButPreserveOverrides()
        {
            for (var mode = 0; mode < 3; mode++)
            {
                AudioKit.ApplyMixerConfig(_config);
                var route = mode == 0 ? AudioOutputRoute.Inherit : mode == 1 ? AudioOutputRoute.ToMixer(_foreign) : AudioOutputRoute.Direct;
                var expected = mode == 0 ? _ui : mode == 1 ? _foreign : null;
                var settings = AudioKit.Settings;
                settings.IsMusicOn.Value = false;
                settings.IsVoiceOn.Value = false;
                var starts = 0;
                Action<AudioClip, AudioHandle> callback = (_, handle) =>
                {
                    starts++;
                    Assert.AreSame(expected, handle.Voice.Source.outputAudioMixerGroup);
                    Assert.AreEqual(.5f, handle.Voice.Source.volume, .001f);
                };
                var music = AudioKit.PlayMusic(_clip, true, .5f, callback, outputRoute: route);
                var voice = AudioKit.PlayVoice(_clip, true, .5f, callback, outputRoute: route);
                var musicGeneration = music.Generation;
                var voiceGeneration = voice.Generation;
                Assert.AreEqual(0, starts);
                Assert.IsFalse(music.IsPlaying);
                Assert.IsFalse(voice.IsPlaying);
                settings.MusicOutputGroup.Value = _ui;
                settings.VoiceOutputGroup.Value = _ui;
                settings.IsMusicOn.Value = true;
                settings.IsVoiceOn.Value = true;
                yield return null;
                Assert.AreEqual(2, starts);
                Assert.IsTrue(music.IsPlaying);
                Assert.IsTrue(voice.IsPlaying);
                settings.IsMusicOn.Value = false;
                settings.IsVoiceOn.Value = false;
                settings.MusicOutputGroup.Value = _sound;
                settings.VoiceOutputGroup.Value = _sound;
                settings.IsMusicOn.Value = true;
                settings.IsVoiceOn.Value = true;
                yield return null;
                Assert.AreSame(mode == 0 ? _sound : expected, music.Voice.Source.outputAudioMixerGroup);
                Assert.AreSame(mode == 0 ? _sound : expected, voice.Voice.Source.outputAudioMixerGroup);
                Assert.AreEqual(musicGeneration, music.Generation);
                Assert.AreEqual(voiceGeneration, voice.Generation);
                Assert.IsTrue(music.IsPlaying);
                Assert.IsTrue(voice.IsPlaying);
                Assert.AreEqual(2, starts, "Switch replay must not repeat the first-start callback.");
                music.Stop();
                voice.Stop();
            }
        }

        [UnityTest]
        public IEnumerator PoolReuseClearsRouteSpatialStateAndOldSettingsCannotAffectNewSession()
        {
            AudioKit.ApplyMixerConfig(_config);
            var options = AudioSoundOptions.Defaults;
            options.OutputRoute = AudioOutputRoute.ToMixer(_ui);
            options.Loop = true;
            options.Pitch = 1.5f;
            options.MinDistance = 3f;
            options.MaxDistance = 20f;
            var old = AudioKit.PlaySoundFollowing(_clip, _target.transform, options);
            var source = old.Voice.Source;
            old.Stop();
            Assert.IsNull(source.outputAudioMixerGroup);
            Assert.IsNull(source.clip);
            Assert.AreEqual(default(AudioPlaybackOptions), old.Voice.Options,
                "A pooled voice must not retain the ended request's configuration or borrowed group.");
            Assert.IsTrue(ReferenceEquals(null, old.Voice.Options.OutputRoute.Group));
            AudioKit.Settings.SoundOutputGroup.Value = _foreign;
            var current = AudioKit.PlaySound(_clip);
            Assert.AreSame(source, current.Voice.Source);
            Assert.AreNotEqual(old.Generation, current.Generation);
            Assert.AreSame(_foreign, source.outputAudioMixerGroup);
            Assert.AreEqual(0f, source.spatialBlend);
            Assert.AreEqual(Vector3.zero, source.transform.localPosition);
            Assert.AreEqual(1f, source.pitch);
            Assert.AreEqual(1f, source.volume);
            Assert.AreEqual(1f, source.minDistance);
            Assert.AreEqual(50f, source.maxDistance);
            Assert.IsFalse(source.loop);
            var oldSettings = AudioKit.Settings;
            current.Stop();
            AudioKit.Shutdown();
            Assert.IsNull(oldSettings.MusicOutputGroup.Value);
            Assert.IsNull(oldSettings.VoiceOutputGroup.Value);
            Assert.IsNull(oldSettings.SoundOutputGroup.Value);
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            var fresh = AudioKit.PlaySound(_clip);
            oldSettings.SoundOutputGroup.Value = _ui;
            old.Stop();
            current.Stop();
            yield return null;
            Assert.IsFalse(source, "Shutdown destroys cached native sources.");
            Assert.IsTrue(fresh.IsPlaying);
            Assert.IsNull(fresh.Voice.Source.outputAudioMixerGroup);
            Assert.IsNull(AudioKit.Settings.SoundOutputGroup.Value);
            Assert.IsTrue(_mixer);
            Assert.IsTrue(_foreign);
            Assert.IsTrue(_config);
        }

        [UnityTest]
        public IEnumerator NativeFloatVolumeAndMissingParameterResultsAreNotFabricated()
        {
            yield return null;
            AudioKit.Shutdown();
            Assert.IsTrue(AudioMixerControl.TrySetFloat(_mixer, "MusicVolume", -17f));
            Assert.IsTrue(_mixer.GetFloat("MusicVolume", out var native));
            Assert.AreEqual(-17f, native, .001f);
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, "MusicVolume", out var wrapped));
            Assert.AreEqual(native, wrapped);
            const string Missing = "MissingMixerTestParameter";
            Assert.IsFalse(_mixer.GetFloat(Missing, out _));
            Assert.IsFalse(AudioMixerControl.TryGetFloat(_mixer, Missing, out var failedFloat));
            Assert.AreEqual(0f, failedFloat);
            Assert.IsFalse(AudioMixerControl.TryGetVolume(_mixer, Missing, out var failedVolume));
            Assert.AreEqual(0f, failedVolume);
            Assert.IsFalse(AudioMixerControl.TrySetFloat(_mixer, Missing, -4f));
            Assert.IsFalse(AudioMixerControl.TrySetVolume(_mixer, Missing, .5f));
            Assert.IsFalse(_mixer.ClearFloat(Missing));
            Assert.IsFalse(AudioMixerControl.TryClearFloat(_mixer, Missing));
            var volumes = new[] { 0f, .5f, 1f };
            var decibels = new[] { -80f, -6.0206f, 0f };
            for (var i = 0; i < volumes.Length; i++)
            {
                Assert.IsTrue(AudioMixerControl.TrySetVolume(_mixer, "MusicVolume", volumes[i]));
                Assert.IsTrue(_mixer.GetFloat("MusicVolume", out native));
                Assert.AreEqual(decibels[i], native, .001f);
                Assert.IsTrue(AudioMixerControl.TryGetVolume(_mixer, "MusicVolume", out var roundtrip));
                Assert.AreEqual(volumes[i], roundtrip, .001f);
            }
            Assert.IsNull(AudioKit.CurrentSettings, "Mixer control alone must not initialize AudioKit.");
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [UnityTest]
        public IEnumerator ClearFloatRestoresActualSnapshotControlAndRoutingClearDoesNotReleaseParameters()
        {
            yield return null;
            AudioKit.ApplyMixerConfig(_config);
            Assert.IsTrue(AudioMixerControl.TrySetFloat(_mixer, "MusicVolume", -7f));
            Assert.IsTrue(AudioMixerControl.TrySetFloat(_mixer, "SnapshotVolume", -9f));
            AudioMixerControl.TransitionToSnapshot(_quiet, 0f);
            yield return null;
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, "SnapshotVolume", out var locked));
            Assert.AreEqual(-9f, locked, .01f, "SetFloat owns this parameter until ClearFloat.");
            Assert.IsTrue(AudioMixerControl.TryClearFloat(_mixer, "SnapshotVolume"));
            AudioMixerControl.TransitionToSnapshot(_default, 0f);
            yield return WaitForParameter("SnapshotVolume", -3f);
            AudioMixerControl.TransitionToSnapshot(_quiet, .05f);
            yield return WaitForParameter("SnapshotVolume", -18f);
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, "MusicVolume", out locked));
            Assert.AreEqual(-7f, locked, .01f);
            AudioKit.ClearMixerRouting();
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, "MusicVolume", out locked));
            Assert.AreEqual(-7f, locked, .01f);
            AudioKit.Shutdown();
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, "MusicVolume", out locked));
            Assert.AreEqual(-7f, locked, .01f, "Shutdown must not revoke another owner's native parameter control.");
        }

        [UnityTest]
        public IEnumerator NativeSnapshotMixturesRespectWeightsWithoutChangingCallerArrays()
        {
            yield return null;
            var snapshots = new[] { _default, _quiet };
            var weights = new[] { 0f, 3f };
            AudioMixerControl.TransitionToSnapshots(_mixer, snapshots, weights, .05f);
            yield return WaitForParameter("SnapshotVolume", -18f);
            CollectionAssert.AreEqual(new[] { _default, _quiet }, snapshots);
            CollectionAssert.AreEqual(new[] { 0f, 3f }, weights);
            weights[0] = 2f;
            weights[1] = 0f;
            AudioMixerControl.TransitionToSnapshots(_mixer, snapshots, weights, 0f);
            yield return WaitForParameter("SnapshotVolume", -3f);
            CollectionAssert.AreEqual(new[] { 2f, 0f }, weights);
            weights[0] = 2f;
            weights[1] = 2f;
            AudioMixerControl.TransitionToSnapshots(_mixer, snapshots, weights, 0f);
            yield return new WaitForSecondsRealtime(.1f);
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, "SnapshotVolume", out var blended));
            Assert.Greater(blended, -18f);
            Assert.Less(blended, -3f, "A positive mixture of different snapshots must differ from both endpoints.");
            CollectionAssert.AreEqual(new[] { 2f, 2f }, weights);
            CollectionAssert.AreEqual(new[] { _default, _quiet }, snapshots);
        }

        private AudioMixerController Controller(bool applyOnAwake)
        {
            var root = OwnObject("MixerPlayback.Controller");
            root.SetActive(false);
            var controller = root.AddComponent<AudioMixerController>();
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("config").objectReferenceValue = _config;
            serialized.FindProperty("applyOnAwake").boolValue = applyOnAwake;
            serialized.FindProperty("parameterName").stringValue = "MusicVolume";
            serialized.FindProperty("snapshot").objectReferenceValue = _quiet;
            serialized.FindProperty("transitionDuration").floatValue = .05f;
            var snapshots = serialized.FindProperty("snapshots");
            snapshots.arraySize = 2;
            snapshots.GetArrayElementAtIndex(0).objectReferenceValue = _default;
            snapshots.GetArrayElementAtIndex(1).objectReferenceValue = _quiet;
            var weights = serialized.FindProperty("weights");
            weights.arraySize = 2;
            weights.GetArrayElementAtIndex(0).floatValue = 1f;
            weights.GetArrayElementAtIndex(1).floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return controller;
        }

        [UnityTest]
        public IEnumerator ControllerAwakeOnlyAppliesRoutingAndExplicitUnityEventsControlMixer()
        {
            yield return null;
            Assert.IsTrue(AudioMixerControl.TrySetFloat(_mixer, "MusicVolume", -9f));
            var controller = Controller(true);
            var player = controller.gameObject.AddComponent<AudioMusicPlayer>();
            var serialized = new SerializedObject(player);
            serialized.FindProperty("clip").objectReferenceValue = _clip;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            controller.gameObject.SetActive(true);
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
            Assert.AreSame(_voice, AudioKit.Settings.VoiceOutputGroup.Value);
            Assert.AreSame(_sound, AudioKit.Settings.SoundOutputGroup.Value);
            Assert.IsFalse(AudioManager.HasInstance);
            yield return null;
            Assert.IsTrue(AudioKit.Music.IsPlaying, "The playback component starts after Mixer routing has been applied in Awake.");
            Assert.AreSame(_music, AudioKit.Music.Voice.Source.outputAudioMixerGroup);
            Assert.IsTrue(AudioMixerControl.TryGetFloat(_mixer, "MusicVolume", out var untouched));
            Assert.AreEqual(-9f, untouched, .01f);
            yield return WaitForParameter("SnapshotVolume", -3f);
            var parameterEvent = new UnityEvent<float>();
            parameterEvent.AddListener(controller.SetParameter);
            parameterEvent.Invoke(-7f);
            yield return WaitForParameter("MusicVolume", -7f);
            var volumeEvent = new UnityEvent<float>();
            volumeEvent.AddListener(controller.SetVolume);
            volumeEvent.Invoke(.5f);
            yield return WaitForParameter("MusicVolume", -6.0206f);
            Assert.AreEqual(1f, AudioKit.Settings.MusicVolume.Value, "Mixer volume must not overwrite AudioSource settings.");
            controller.ClearParameter();
            yield return WaitForParameter("MusicVolume", 0f);
            controller.TransitionToSnapshot();
            yield return WaitForParameter("SnapshotVolume", -18f);
            yield return WaitForParameter("MusicVolume", -12f);
            controller.TransitionToSnapshots();
            yield return WaitForParameter("SnapshotVolume", -3f);
            controller.SetParameter(-5f);
            controller.enabled = false;
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
            Object.Destroy(controller.gameObject);
            yield return null;
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value, "Component lifetime does not own global routing.");
            yield return WaitForParameter("MusicVolume", -5f);
        }

        [UnityTest]
        public IEnumerator ControllerManualApplyAndClearAffectOnlyInheritedRoutes()
        {
            var controller = Controller(false);
            controller.gameObject.SetActive(true);
            yield return null;
            Assert.IsNull(AudioKit.Settings.MusicOutputGroup.Value);
            Assert.IsFalse(AudioManager.HasInstance);
            controller.Apply();
            var inherited = AudioKit.PlayMusic(_clip);
            var overridden = AudioKit.PlayVoice(_clip, true, outputRoute: AudioOutputRoute.ToMixer(_foreign));
            controller.SetParameter(-11f);
            controller.ClearRouting();
            Assert.IsNull(inherited.Voice.Source.outputAudioMixerGroup);
            Assert.AreSame(_foreign, overridden.Voice.Source.outputAudioMixerGroup);
            Assert.IsTrue(inherited.IsValid);
            Assert.IsTrue(overridden.IsValid);
            yield return WaitForParameter("MusicVolume", -11f);
        }

        [UnityTest]
        public IEnumerator ControllerReportsInvalidNativeParametersAndForeignSnapshotsWithoutMutatingRouting()
        {
            var controller = Controller(false);
            controller.gameObject.SetActive(true);
            yield return null;
            controller.Apply();
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("parameterName").stringValue = "MissingMixerTestParameter";
            serialized.FindProperty("snapshot").objectReferenceValue = _foreignMixer.FindSnapshot("Default");
            serialized.ApplyModifiedPropertiesWithoutUndo();
            LogAssert.Expect(LogType.Error, new Regex("AudioMixerController\\.SetParameter:.*MissingMixerTestParameter"));
            controller.SetParameter(-5f);
            LogAssert.Expect(LogType.Error, new Regex("AudioMixerController\\.ClearParameter:.*MissingMixerTestParameter"));
            controller.ClearParameter();
            LogAssert.Expect(LogType.Error, new Regex("AudioMixerController\\.TransitionToSnapshot:.*belong"));
            controller.TransitionToSnapshot();
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
            yield return WaitForParameter("SnapshotVolume", -3f);
            serialized.Update();
            serialized.FindProperty("config").objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            LogAssert.Expect(LogType.Error, new Regex("AudioMixerController\\.Apply:.*AudioMixerConfig"));
            controller.Apply();
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
        }

        [UnityTest]
        public IEnumerator UnconfiguredControllerAwakeIsSilentButExplicitCallsStillReportMissingConfig()
        {
            AudioKit.ApplyMixerConfig(_config);
            var music = AudioKit.PlayMusic(_clip);
            var source = music.Voice.Source;
            var controller = Controller(true);
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("config").objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            controller.gameObject.SetActive(true);
            yield return null;
            LogAssert.NoUnexpectedReceived();
            Assert.IsTrue(music.IsPlaying);
            Assert.AreEqual(music, AudioKit.Music);
            Assert.AreSame(source, music.Voice.Source);
            Assert.AreSame(_music, source.outputAudioMixerGroup);
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
            Assert.AreSame(_voice, AudioKit.Settings.VoiceOutputGroup.Value);
            Assert.AreSame(_sound, AudioKit.Settings.SoundOutputGroup.Value);
            LogAssert.Expect(LogType.Error, new Regex("AudioMixerController\\.Apply:.*AudioMixerConfig"));
            controller.Apply();
            LogAssert.Expect(LogType.Error, new Regex("AudioMixerController\\.SetParameter:.*AudioMixerConfig"));
            controller.SetParameter(-5f);
            LogAssert.Expect(LogType.Error, new Regex("AudioMixerController\\.TransitionToSnapshot:.*AudioMixerConfig"));
            controller.TransitionToSnapshot();
            Assert.AreEqual(music, AudioKit.Music);
            Assert.IsTrue(music.IsPlaying);
            Assert.AreSame(_music, source.outputAudioMixerGroup);
            Assert.AreSame(_music, AudioKit.Settings.MusicOutputGroup.Value);
            Assert.AreSame(_voice, AudioKit.Settings.VoiceOutputGroup.Value);
            Assert.AreSame(_sound, AudioKit.Settings.SoundOutputGroup.Value);
        }

        [UnityTest]
        public IEnumerator ExistingPlaybackComponentsForwardSerializedRoutes()
        {
            AudioKit.ApplyMixerConfig(_config);
            var root = OwnObject("MixerPlayback.Emitter");
            root.SetActive(false);
            var emitter = root.AddComponent<AudioEmitter>();
            var serialized = new SerializedObject(emitter);
            var clips = serialized.FindProperty("clips");
            clips.arraySize = 1;
            clips.GetArrayElementAtIndex(0).objectReferenceValue = _clip;
            serialized.FindProperty("loop").boolValue = true;
            serialized.FindProperty("outputRoute").FindPropertyRelative("Mode").intValue = (int)AudioOutputRoute.RouteMode.MixerGroup;
            serialized.FindProperty("outputRoute").FindPropertyRelative("Group").objectReferenceValue = _ui;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            root.SetActive(true);
            emitter.Play();
            var sounds = AudioKit.GetPlayingSounds();
            Assert.That(sounds, Has.Length.EqualTo(1));
            Assert.AreSame(_ui, sounds[0].Voice.Source.outputAudioMixerGroup);
            AudioKit.Settings.SoundOutputGroup.Value = _foreign;
            Assert.AreSame(_ui, sounds[0].Voice.Source.outputAudioMixerGroup);
            var musicRoot = OwnObject("MixerPlayback.MusicPlayer");
            musicRoot.SetActive(false);
            var player = musicRoot.AddComponent<AudioMusicPlayer>();
            serialized = new SerializedObject(player);
            serialized.FindProperty("clip").objectReferenceValue = _clip;
            serialized.FindProperty("playOnAwake").boolValue = false;
            serialized.FindProperty("outputRoute").FindPropertyRelative("Mode").intValue = (int)AudioOutputRoute.RouteMode.Direct;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            musicRoot.SetActive(true);
            player.Play();
            Assert.IsNull(AudioKit.Music.Voice.Source.outputAudioMixerGroup);
            AudioKit.Settings.MusicOutputGroup.Value = _ui;
            yield return null;
            Assert.IsTrue(AudioKit.Music.IsPlaying);
            Assert.IsNull(AudioKit.Music.Voice.Source.outputAudioMixerGroup);
            emitter.Stop();
            player.Stop();
        }

        [UnityTest]
        public IEnumerator ActionResetCancellationPoolReuseAndSequencePreserveOnlyTheirOwnRoute()
        {
            Assert.IsTrue(YLFramework.Tests.Actions.ActionFixtureSession.CanOwnUninitializedSession(),
                "Do not release another owner's ActionKit session.");
            _ownsActionSession = true;
            AudioKit.ApplyMixerConfig(_config);
            var finishes = 0;
            var action = PlaySoundAction.Allocate(_clip, () => finishes++, AudioOutputRoute.ToMixer(_ui));
            try
            {
                Assert.IsFalse(action.Execute(0f));
                var old = AudioKit.GetPlayingSounds().Single();
                Assert.AreSame(_ui, old.Voice.Source.outputAudioMixerGroup);
                action.Reset();
                Assert.IsFalse(old.IsValid);
                Assert.IsFalse(action.Execute(0f));
                var current = AudioKit.GetPlayingSounds().Single();
                Assert.AreSame(old.Voice, current.Voice);
                Assert.AreNotEqual(old.Generation, current.Generation);
                Assert.AreSame(_ui, current.Voice.Source.outputAudioMixerGroup);
                AudioKit.Settings.SoundOutputGroup.Value = _foreign;
                Assert.AreSame(_ui, current.Voice.Source.outputAudioMixerGroup);
                old.Stop();
                Assert.IsTrue(current.IsValid);
                action.Deinit();
                Assert.IsFalse(current.IsValid);
                Assert.IsNull(current.Voice.Source.outputAudioMixerGroup);
                Assert.AreEqual(0, finishes, "Reset and cancellation are not completion.");
            }
            finally
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }
            // Actions return to their LIFO pool through the Update-driven deferred recycle queue.
            yield return null;
            yield return null;
            Assert.AreEqual(0, ActionQueue.PendingCount);
            var reused = PlaySoundAction.Allocate(_clip);
            try
            {
                Assert.AreSame(action, reused, "Exercise actual action-pool reuse after deinit.");
                Assert.IsFalse(reused.Execute(0f));
                var handle = AudioKit.GetPlayingSounds().Single();
                Assert.AreSame(_foreign, handle.Voice.Source.outputAudioMixerGroup, "Reused action must not retain its old UI override.");
                yield return null;
                Assert.IsTrue(handle.IsPlaying);
            }
            finally
            {
                reused.Deinit();
            }
            var sequence = YLFramework.Actions.ActionKit.Sequence();
            try
            {
                Assert.AreSame(sequence, sequence.PlaySound(_clip, AudioOutputRoute.Direct));
                Assert.IsFalse(sequence.Execute(0f));
                var handle = AudioKit.GetPlayingSounds().Single();
                Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
                AudioKit.Settings.SoundOutputGroup.Value = _ui;
                Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
                handle.Stop();
                Assert.IsTrue(sequence.Execute(0f));
            }
            finally
            {
                sequence.Deinit();
            }
        }

        private sealed class MixerPlaybackSettings : IDisposable
        {
            private static readonly string[] _keys =
            {
                "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
                "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
            };
            private readonly bool[] _hadKeys = new bool[6];
            private readonly int[] _switches = new int[3];
            private readonly float[] _volumes = new float[3];

            internal MixerPlaybackSettings()
            {
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
                var settings = AudioKit.Settings;
                settings.IsSoundOn.SetValueWithoutCallback(true);
                settings.IsMusicOn.SetValueWithoutCallback(true);
                settings.IsVoiceOn.SetValueWithoutCallback(true);
                settings.SoundVolume.SetValueWithoutCallback(1f);
                settings.MusicVolume.SetValueWithoutCallback(1f);
                settings.VoiceVolume.SetValueWithoutCallback(1f);
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            }

            public void Dispose()
            {
                AudioKit.Shutdown();
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
            }
        }
    }
}
#endif
