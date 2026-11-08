using System;
using System.Collections;
using System.Collections.Generic;
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
using Random = UnityEngine.Random;

namespace YLFramework.Tests.Audio
{
    public class AudioWeightedPlaybackTests
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
        private AudioClip _first;
        private AudioClip _second;
        private AudioClip _short;
        private Random.State _randomState;

        [SetUp]
        public void SetUp()
        {
            _randomState = Random.state;
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
            AudioKit.GlobalFrameCountForIgnoreSameSound = 10;
            AudioKit.SoundFrameCountForIgnoreSameSound = 10;
            _first = AudioClip.Create("weighted-play-first", 88200, 1, 44100, false);
            _second = AudioClip.Create("weighted-play-second", 88200, 1, 44100, false);
            _short = AudioClip.Create("weighted-play-short", 4410, 1, 44100, false);
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
                OwnObject("WeightedPlayback.Listener").AddComponent<AudioListener>();
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            foreach (var root in _objects)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }

            _objects.Clear();
            Object.Destroy(_first);
            Object.Destroy(_second);
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

        private static void SetField(AudioEmitter emitter, string name, object value)
        {
            var field = typeof(AudioEmitter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            field.SetValue(emitter, value);
        }

        private AudioEmitter Emitter(AudioEmitter.PositionMode mode, bool weighted = true, bool loop = true)
        {
            var root = OwnObject("WeightedPlayback.Emitter");
            root.SetActive(false);
            root.transform.position = new Vector3(3f, 4f, 5f);
            var emitter = root.AddComponent<AudioEmitter>();
            SetField(emitter, "clips", new List<AudioClip> { _first });
            SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_second) });
            SetField(emitter, "useWeightedRandom", weighted);
            SetField(emitter, "loop", loop);
            SetField(emitter, "positionMode", mode);
            root.SetActive(true);
            return emitter;
        }

        private static AudioSoundOptions LoopOptions()
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            return options;
        }

        [UnityTest]
        public IEnumerator WeightedPlaybackPassesOptionsAndOriginalNaturalCompletionContext()
        {
            var clips = new[]
            {
                new AudioWeightedClip(null, 100f), new AudioWeightedClip(_short, 2f), new AudioWeightedClip(_first, 0f)
            };
            var original = (AudioWeightedClip[])clips.Clone();
            AudioKit.Settings.SoundVolume.SetValueWithoutCallback(.5f);
            var options = AudioSoundOptions.Defaults;
            options.Volume = .6f;
            options.Pitch = 1.25f;
            options.Priority = 19;
            options.OutputRoute = AudioOutputRoute.Direct;
            options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            var calls = 0;
            var callbackHandle = default(AudioHandle);
            var handle = AudioKit.PlayWeightedRandomSound(clips, options, (clip, lease) =>
            {
                calls++;
                Assert.AreSame(_short, clip);
                Assert.IsTrue(lease.IsValid, "Natural completion runs before lease cleanup.");
                Assert.AreSame(clip, lease.Voice.Source.clip);
                callbackHandle = lease;
            });
            Assert.IsTrue(handle.IsValid);
            Assert.AreSame(_short, handle.Voice.Source.clip);
            Assert.AreEqual(.3f, handle.Voice.Source.volume, .001f);
            Assert.AreEqual(1.25f, handle.Voice.Source.pitch);
            Assert.IsFalse(handle.Voice.Source.loop);
            Assert.AreEqual(0f, handle.Voice.Source.spatialBlend);
            Assert.AreEqual(AudioOutputRoute.Direct, handle.Voice.Options.OutputRoute);
            Assert.AreEqual(options.PlaySoundMode, handle.Voice.Options.PlaySoundMode);
            Assert.AreEqual(19, handle.Voice.Priority);
            var deadline = Time.realtimeSinceStartup + 2f;
            while (handle.IsValid && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsFalse(handle.IsValid, "The real sound must complete within the deadline.");
            Assert.AreEqual(1, calls);
            Assert.AreEqual(handle, callbackHandle);
            CollectionAssert.AreEqual(original, clips);
            yield return null;
            Assert.AreEqual(1, calls);
        }

        [UnityTest]
        public IEnumerator EmptyInvalidAndDisabledWeightedRequestsDoNotCreateAHost()
        {
            AudioKit.Shutdown();
            var calls = 0;
            var state = Random.state;
            Assert.IsFalse(AudioKit.PlayWeightedRandomSound(null, onFinished: (_, __) => calls++).IsValid);
            Assert.IsFalse(AudioKit.PlayWeightedRandomSound(new[] { new AudioWeightedClip(_first, 0f) }).IsValid);
            Assert.AreEqual(state, Random.state);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlayWeightedRandomSound(new[]
            {
                new AudioWeightedClip(_first), new AudioWeightedClip(null, float.NaN)
            }));
            Assert.AreEqual(state, Random.state);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(false);
            var before = Random.state;
            _ = Random.value;
            var afterOne = Random.state;
            Random.state = before;
            Assert.IsFalse(AudioKit.PlayWeightedRandomSound(new[] { new AudioWeightedClip(_first) },
                onFinished: (_, __) => calls++).IsValid);
            Assert.AreEqual(afterOne, Random.state);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(0, calls);
            yield return null;
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [UnityTest]
        public IEnumerator DeduplicationRejectsTheSelectedClipWithoutSamplingAgain()
        {
            var clips = new[] { new AudioWeightedClip(_first, 1f), new AudioWeightedClip(_second, 3f) };
            Random.InitState(2719);
            var before = Random.state;
            Assert.IsTrue(AudioKit.TrySelectWeightedRandomClip(clips, out var selected));
            var afterOne = Random.state;
            var options = LoopOptions();
            options.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            var current = AudioKit.PlaySound(selected, options);
            Assert.IsTrue(current.IsValid);
            var calls = 0;
            Random.state = before;
            var rejected = AudioKit.PlayWeightedRandomSound(clips, options, (_, __) => calls++);
            Assert.IsFalse(rejected.IsValid);
            Assert.AreEqual(afterOne, Random.state, "Rejection must not draw another candidate.");
            Assert.AreEqual(1, AudioKit.SoundCount);
            Assert.AreSame(selected, current.Voice.Source.clip);
            Assert.AreEqual(0, calls);
            options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            Random.state = before;
            var allowed = AudioKit.PlayWeightedRandomSound(clips, options);
            Assert.IsTrue(allowed.IsValid);
            Assert.AreSame(selected, allowed.Voice.Source.clip);
            Assert.AreEqual(afterOne, Random.state);
            yield return null;
            Assert.IsTrue(current.IsPlaying);
            Assert.IsTrue(allowed.IsPlaying);
        }

        [UnityTest]
        public IEnumerator SelectorComposesWithFixedFollowingAndVoiceWithoutAnotherRandomDraw()
        {
            AudioKit.Shutdown();
            Random.InitState(287);
            var before = Random.state;
            _ = Random.value;
            var afterOne = Random.state;
            Random.state = before;
            Assert.IsTrue(AudioKit.TrySelectWeightedRandomClip(new[] { new AudioWeightedClip(_second) }, out var clip));
            Assert.AreEqual(afterOne, Random.state);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            var settings = AudioKit.Settings;
            settings.IsSoundOn.SetValueWithoutCallback(true);
            settings.IsVoiceOn.SetValueWithoutCallback(true);
            settings.SoundVolume.SetValueWithoutCallback(1f);
            settings.VoiceVolume.SetValueWithoutCallback(1f);
            var target = OwnObject("WeightedPlayback.Target").transform;
            target.position = new Vector3(7f, 8f, 9f);
            var options = LoopOptions();
            options.Volume = .4f;
            options.Pitch = 1.5f;
            options.MinDistance = 2f;
            options.MaxDistance = 30f;
            options.RolloffMode = AudioRolloffMode.Logarithmic;
            var fixedSound = AudioKit.PlaySoundAt(clip, Vector3.one, options);
            var following = AudioKit.PlaySoundFollowing(clip, target, options);
            var voice = AudioKit.PlayVoiceFollowing(clip, target, options);
            Assert.AreEqual(afterOne, Random.state);
            foreach (var handle in new[] { fixedSound, following, voice })
            {
                Assert.IsTrue(handle.IsValid);
                Assert.AreSame(clip, handle.Voice.Source.clip);
                Assert.AreEqual(1f, handle.Voice.Source.spatialBlend);
                Assert.AreEqual(.4f, handle.Voice.Source.volume, .001f);
                Assert.AreEqual(1.5f, handle.Voice.Source.pitch);
                Assert.AreEqual(2f, handle.Voice.Source.minDistance);
                Assert.AreEqual(30f, handle.Voice.Source.maxDistance);
                Assert.AreEqual(AudioRolloffMode.Logarithmic, handle.Voice.Source.rolloffMode);
            }
            Assert.AreEqual(2, AudioKit.SoundCount);
            target.position = new Vector3(10f, 11f, 12f);
            yield return null;
            Assert.AreEqual(Vector3.one, fixedSound.Voice.Source.transform.position);
            Assert.AreEqual(target.position, following.Voice.Source.transform.position);
            Assert.AreEqual(target.position, voice.Voice.Source.transform.position);
            voice = AudioKit.PlayVoiceAt(clip, Vector3.left, options);
            Assert.AreEqual(Vector3.left, voice.Voice.Source.transform.position);
            Assert.AreEqual(2, AudioKit.SoundCount);
        }

        [UnityTest]
        public IEnumerator RuntimeAddedEmitterInheritsGlobalDeduplicationWithoutEditorReset()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
            var emitter = Emitter(AudioEmitter.PositionMode.TwoDimensional, false, false);
            var starts = 0;
            emitter.Started.AddListener(() => starts++);
            emitter.Play();
            emitter.Play();
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(AudioKit.SoundCount, Is.EqualTo(1),
                "Runtime and Editor creation must both inherit the current global policy unless explicitly overridden.");
            var handle = AudioKit.GetPlayingSounds()[0];
            Assert.That(handle.Voice.Options.PlaySoundMode, Is.Null);
            SetField(emitter, "useGlobalPlaySoundMode", false);
            emitter.Play();
            emitter.Play();
            Assert.That(starts, Is.EqualTo(3));
            Assert.That(AudioKit.SoundCount, Is.EqualTo(3), "Explicit false still selects the stored EveryOne policy.");
            Assert.That(handle.IsValid, Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator EmitterDefaultOldListAndOptInWeightedListRemainIsolated()
        {
            var emitter = Emitter(AudioEmitter.PositionMode.TwoDimensional, false);
            var inherit = typeof(AudioEmitter).GetField("useGlobalPlaySoundMode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(inherit);
            Assert.IsTrue((bool)inherit.GetValue(emitter), "Runtime AddComponent shares the current inherited-policy default with the Editor.");
            SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_second, float.NaN) });
            Assert.DoesNotThrow(emitter.Play, "Disabled weighted data must not be validated.");
            var old = AudioKit.GetPlayingSounds()[0];
            Assert.AreSame(_first, old.Voice.Source.clip);
            SetField(emitter, "clips", new List<AudioClip>());
            SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_second) });
            SetField(emitter, "useWeightedRandom", true);
            emitter.Play();
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
            var current = AudioKit.GetPlayingSounds()[0];
            Assert.AreSame(_second, current.Voice.Source.clip, "Weighted mode must not consult the empty legacy list.");
            SetField(emitter, "weightedClips", new List<AudioWeightedClip>());
            emitter.Play();
            Assert.IsTrue(current.IsValid, "No eligible weighted selection must not replace the current loop.");
            Assert.AreEqual(1, AudioKit.SoundCount);
            yield return null;
            Assert.IsTrue(current.IsPlaying);
        }

        [UnityTest]
        public IEnumerator EmitterExplicitEveryOneOverridesGlobalDeduplicationAndCanReturnToInheritance()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
            foreach (var weighted in new[] { false, true })
            {
                var emitter = Emitter(AudioEmitter.PositionMode.TwoDimensional, weighted, false);
                SetField(emitter, "useGlobalPlaySoundMode", false);
                var starts = 0;
                emitter.Started.AddListener(() => starts++);
                emitter.Play();
                emitter.Play();
                Assert.AreEqual(2, starts);
                Assert.AreEqual(2, AudioKit.SoundCount, "Explicit false preserves the stored EveryOne policy rather than the current global policy.");
                foreach (var handle in AudioKit.GetPlayingSounds())
                {
                    Assert.AreEqual(AudioKit.PlaySoundModes.EveryOne, handle.Voice.Options.PlaySoundMode);
                }

                SetField(emitter, "useGlobalPlaySoundMode", true);
                emitter.Play();
                Assert.AreEqual(3, starts);
                Assert.AreEqual(3, AudioKit.SoundCount);
                var inherited = Array.Find(AudioKit.GetPlayingSounds(), handle => handle.Voice.Options.PlaySoundMode == null);
                Assert.IsTrue(inherited.IsValid);
                var admitted = AudioKit.SoundStats.AdmittedCount;
                var rejected = AudioKit.SoundStats.DedupRejectedCount;
                emitter.Play();
                Assert.AreEqual(3, starts);
                Assert.AreEqual(3, AudioKit.SoundCount);
                Assert.IsTrue(inherited.IsValid);
                Assert.AreEqual(admitted, AudioKit.SoundStats.AdmittedCount);
                Assert.AreEqual(rejected + 1, AudioKit.SoundStats.DedupRejectedCount);
                Assert.AreEqual(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames, AudioKit.PlaySoundMode);
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
                emitter.Play();
                Assert.AreEqual(4, starts);
                Assert.AreEqual(4, AudioKit.SoundCount, "An inheriting emitter resolves the latest global mode for each request.");
                emitter.Stop();
                Assert.AreEqual(0, AudioKit.SoundCount);
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator WeightedEmitterAllThreePositionModesPassSelectedClipAndOptionsOnce()
        {
            foreach (AudioEmitter.PositionMode mode in Enum.GetValues(typeof(AudioEmitter.PositionMode)))
            {
                var emitter = Emitter(mode);
                var target = OwnObject("WeightedPlayback.EmitterTarget").transform;
                target.position = new Vector3(7f, 6f, 5f);
                SetField(emitter, "followTarget", target);
                SetField(emitter, "volume", .6f);
                SetField(emitter, "pitch", 1.4f);
                SetField(emitter, "priority", 31);
                SetField(emitter, "minDistance", 2f);
                SetField(emitter, "maxDistance", 40f);
                SetField(emitter, "rolloffMode", AudioRolloffMode.Logarithmic);
                SetField(emitter, "outputRoute", AudioOutputRoute.Direct);
                SetField(emitter, "clips", new List<AudioClip>());
                Random.InitState(1327);
                var before = Random.state;
                _ = Random.value;
                var afterOne = Random.state;
                Random.state = before;
                emitter.Play();
                Assert.AreEqual(afterOne, Random.state);
                Assert.AreEqual(1, AudioKit.SoundCount);
                var old = AudioKit.GetPlayingSounds()[0];
                var source = old.Voice.Source;
                Assert.AreSame(_second, source.clip);
                Assert.AreEqual(.6f, source.volume, .001f);
                Assert.AreEqual(1.4f, source.pitch);
                Assert.AreEqual(31, old.Voice.Priority);
                Assert.AreEqual(31, old.Voice.Options.Priority);
                Assert.IsTrue(source.loop);
                Assert.IsNull(old.Voice.Options.PlaySoundMode, "The default emitter preserves policy inheritance in its submitted snapshot.");
                Assert.AreEqual(AudioOutputRoute.Direct, old.Voice.Options.OutputRoute);
                Assert.AreEqual(mode == AudioEmitter.PositionMode.TwoDimensional ? 0f : 1f, source.spatialBlend);
                if (mode != AudioEmitter.PositionMode.TwoDimensional)
                {
                    Assert.AreEqual(2f, source.minDistance);
                    Assert.AreEqual(40f, source.maxDistance);
                    Assert.AreEqual(AudioRolloffMode.Logarithmic, source.rolloffMode);
                    Assert.AreEqual(mode == AudioEmitter.PositionMode.FixedPosition ? emitter.transform.position : target.position,
                        source.transform.position);
                }
                var originalPosition = emitter.transform.position;
                emitter.transform.position += Vector3.one;
                target.position += Vector3.right;
                yield return null;
                Assert.IsTrue(old.IsPlaying);
                if (mode == AudioEmitter.PositionMode.FixedPosition)
                {
                    Assert.AreEqual(originalPosition, source.transform.position);
                }

                if (mode == AudioEmitter.PositionMode.FollowTransform)
                {
                    Assert.AreEqual(target.position, source.transform.position);
                }

                emitter.Play();
                Assert.IsFalse(old.IsValid);
                Assert.AreEqual(1, AudioKit.SoundCount);
                var current = AudioKit.GetPlayingSounds()[0];
                emitter.enabled = false;
                Assert.IsFalse(current.IsValid, "Disabling the component still stops its loop.");
                Assert.AreEqual(0, AudioKit.SoundCount);
            }
        }

        [UnityTest]
        public IEnumerator InvalidWeightedEmitterDataNeverStopsTheExistingLoopAndRouteValidationStaysFirst()
        {
            foreach (AudioEmitter.PositionMode mode in Enum.GetValues(typeof(AudioEmitter.PositionMode)))
            {
                var emitter = Emitter(mode);
                emitter.Play();
                var current = AudioKit.GetPlayingSounds()[0];
                var generation = current.Generation;
                var source = current.Voice.Source;
                var stats = AudioKit.SoundStats;
                var invalids = new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
                foreach (var weight in invalids)
                {
                    SetField(emitter, "weightedClips", new List<AudioWeightedClip>
                    {
                        new AudioWeightedClip(_first), new AudioWeightedClip(null, weight)
                    });
                    var state = Random.state;
                    Assert.Throws<ArgumentOutOfRangeException>(emitter.Play);
                    Assert.AreEqual(state, Random.state);
                    Assert.AreEqual(generation, current.Generation);
                    Assert.IsTrue(current.IsValid);
                    Assert.AreSame(_second, current.Voice.Source.clip);
                    Assert.AreEqual(1, AudioKit.SoundCount);
                }
                SetField(emitter, "outputRoute", new AudioOutputRoute { Mode = (AudioOutputRoute.RouteMode)99 });
                var routeError = Assert.Throws<ArgumentOutOfRangeException>(emitter.Play);
                Assert.AreEqual("Mode", routeError.ParamName, "Keep the existing outputRoute validation ahead of weighted data.");
                Assert.IsTrue(current.IsValid);
                SetField(emitter, "outputRoute", AudioOutputRoute.Direct);
                SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_first) });
                SetField(emitter, "pitch", 0f);
                var optionError = Assert.Throws<ArgumentOutOfRangeException>(emitter.Play);
                Assert.AreEqual("Pitch", optionError.ParamName, "Selected clips still enter the ordinary playback validation chain.");
                Assert.IsTrue(current.IsValid, "Invalid selected-clip options must be rejected before replacing the owned loop.");
                Assert.AreSame(source, current.Voice.Source);
                Assert.AreEqual(generation, current.Voice.Generation);
                Assert.AreEqual(1, AudioKit.SoundCount);
                Assert.AreEqual(stats, AudioKit.SoundStats, "Validation failures must not submit or release a request.");
                emitter.Stop();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator EmitterInvalidOptionsAndEmptyCandidatesPreserveOwnedLoopWithoutSubmittingRequests()
        {
            foreach (var weighted in new[] { false, true })
            {
                foreach (AudioEmitter.PositionMode mode in Enum.GetValues(typeof(AudioEmitter.PositionMode)))
                {
                    var emitter = Emitter(mode, weighted);
                    SetField(emitter, "useGlobalPlaySoundMode", false);
                    var starts = 0;
                    emitter.Started.AddListener(() => starts++);
                    emitter.Play();
                    var current = AudioKit.GetPlayingSounds()[0];
                    var source = current.Voice.Source;
                    var generation = current.Generation;
                    var stats = AudioKit.SoundStats;
                    var fields = new[] { "volume", "pitch", "priority", "playSoundMode", "positionMode", "outputRoute",
                    "minDistance", "maxDistance", "rolloffMode" };
                    var values = new object[] { float.NaN, 0f, -1, (AudioKit.PlaySoundModes)99, (AudioEmitter.PositionMode)99,
                    new AudioOutputRoute { Mode = (AudioOutputRoute.RouteMode)99 }, -1f, 0f, (AudioRolloffMode)99 };
                    var parameters = new[] { "Volume", "Pitch", "Priority", "PlaySoundMode", "positionMode", "Mode",
                    "MinDistance", "MaxDistance", "RolloffMode" };

                    void AssertPreserved()
                    {
                        Assert.IsTrue(current.IsValid);
                        Assert.IsTrue(current.IsPlaying);
                        Assert.AreSame(source, current.Voice.Source);
                        Assert.AreSame(weighted ? _second : _first, source.clip);
                        Assert.IsTrue(source.loop);
                        Assert.AreEqual(generation, current.Voice.Generation);
                        CollectionAssert.AreEqual(new[] { current }, AudioKit.GetPlayingSounds());
                        Assert.AreEqual(stats, AudioKit.SoundStats, "Rejected inputs cannot allocate, admit, release or commit deduplication.");
                        Assert.AreEqual(1, starts, "Rejected inputs must not invoke Started.");
                    }

                    for (var i = 0; i < (mode == AudioEmitter.PositionMode.TwoDimensional ? 6 : fields.Length); i++)
                    {
                        var field = typeof(AudioEmitter).GetField(fields[i], BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.IsNotNull(field, fields[i]);
                        var original = field.GetValue(emitter);
                        SetField(emitter, fields[i], values[i]);
                        var random = Random.state;
                        Assert.AreEqual(parameters[i], Assert.Throws<ArgumentOutOfRangeException>(emitter.Play).ParamName);
                        Assert.AreEqual(random, Random.state, "Invalid options must be rejected before selection.");
                        AssertPreserved();
                        SetField(emitter, fields[i], original);
                    }
                    SetField(emitter, "outputRoute", new AudioOutputRoute { Mode = AudioOutputRoute.RouteMode.MixerGroup });
                    Assert.Catch<ArgumentException>(emitter.Play);
                    AssertPreserved();
                    SetField(emitter, "outputRoute", AudioOutputRoute.Inherit);

                    if (mode == AudioEmitter.PositionMode.FollowTransform)
                    {
                        var target = OwnObject("WeightedPlayback.InactiveTarget");
                        target.SetActive(false);
                        SetField(emitter, "followTarget", target.transform);
                        Assert.DoesNotThrow(emitter.Play);
                        AssertPreserved();
                        Object.DestroyImmediate(target);
                        Assert.DoesNotThrow(emitter.Play);
                        AssertPreserved();
                        SetField(emitter, "followTarget", null);
                    }
                    SetField(emitter, "clips", new List<AudioClip>());
                    SetField(emitter, "weightedClips", new List<AudioWeightedClip>());
                    var state = Random.state;
                    Assert.DoesNotThrow(emitter.Play);
                    Assert.AreEqual(state, Random.state);
                    AssertPreserved();
                    SetField(emitter, "clips", new List<AudioClip> { null });
                    SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_first, 0f) });
                    Assert.DoesNotThrow(emitter.Play);
                    Assert.AreEqual(state, Random.state);
                    AssertPreserved();
                    emitter.Stop();
                    Assert.IsFalse(current.IsValid, "Input protection must not break explicit ownership cleanup.");
                    Assert.AreEqual(0, AudioKit.SoundCount);
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator WeightedEmitterFollowingWithoutAnExplicitTargetUsesItsOwnTransform()
        {
            var emitter = Emitter(AudioEmitter.PositionMode.FollowTransform);
            emitter.Play();
            var handle = AudioKit.GetPlayingSounds()[0];
            emitter.transform.position = new Vector3(9f, 8f, 7f);
            yield return null;
            Assert.AreEqual(emitter.transform.position, handle.Voice.Source.transform.position);
            emitter.gameObject.SetActive(false);
            Assert.IsFalse(handle.IsValid);
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator WeightedPlaybackAndEmitterForwardInheritedOverrideAndDirectMixerRoutes()
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            Assert.IsNotNull(mixer, "Import the existing mixer fixture before running this test.");
            var soundGroups = mixer.FindMatchingGroups("Sound");
            var uiGroups = mixer.FindMatchingGroups("UI");
            Assert.That(soundGroups, Has.Length.EqualTo(1));
            Assert.That(uiGroups, Has.Length.EqualTo(1));
            var sound = soundGroups[0];
            var ui = uiGroups[0];
            AudioKit.Settings.SoundOutputGroup.Value = sound;
            var clips = new[] { new AudioWeightedClip(_first) };
            var routes = new[] { AudioOutputRoute.Inherit, AudioOutputRoute.ToMixer(ui), AudioOutputRoute.Direct };
            var groups = new[] { sound, ui, null };
            for (var i = 0; i < routes.Length; i++)
            {
                var options = LoopOptions();
                options.OutputRoute = routes[i];
                var handle = AudioKit.PlayWeightedRandomSound(clips, options);
                Assert.AreSame(groups[i], handle.Voice.Source.outputAudioMixerGroup);
                yield return null;
                Assert.IsTrue(handle.IsPlaying);
                handle.Stop();
                Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
                foreach (AudioEmitter.PositionMode mode in Enum.GetValues(typeof(AudioEmitter.PositionMode)))
                {
                    var emitter = Emitter(mode);
                    SetField(emitter, "outputRoute", routes[i]);
                    emitter.Play();
                    var emitted = AudioKit.GetPlayingSounds()[0];
                    Assert.AreSame(_second, emitted.Voice.Source.clip);
                    Assert.AreSame(groups[i], emitted.Voice.Source.outputAudioMixerGroup);
                    yield return null;
                    Assert.IsTrue(emitted.IsPlaying);
                    emitter.Stop();
                    Assert.IsFalse(emitted.IsValid);
                }
            }
            Assert.IsTrue(mixer, "Routing borrows the existing fixture and must not destroy it.");
        }
#endif
    }
}
