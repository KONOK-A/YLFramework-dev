using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Actions;
using YLFramework.Audio;
using Kit = YLFramework.Actions.ActionKit;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace YLFramework.Tests.Audio
{
    public class AudioStartedComponentTests
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
        private readonly List<IAction> _actions = new List<IAction>();
        private AudioClip _clip;
        private AudioClip _other;
        private AudioClip _short;
        private Random.State _randomState;
        private float _timeScale;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _randomState = Random.state;
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
            var settings = AudioKit.Settings;
            settings.IsSoundOn.SetValueWithoutCallback(true);
            settings.IsMusicOn.SetValueWithoutCallback(true);
            settings.IsVoiceOn.SetValueWithoutCallback(true);
            settings.SoundVolume.SetValueWithoutCallback(1f);
            settings.MusicVolume.SetValueWithoutCallback(1f);
            settings.VoiceVolume.SetValueWithoutCallback(1f);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            _clip = AudioClip.Create("started-component-long", 220500, 1, 44100, false);
            _other = AudioClip.Create("started-component-other", 220500, 1, 44100, false);
            _short = AudioClip.Create("started-component-natural", 4410, 1, 44100, false);
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
                OwnObject("StartedComponent.Listener").AddComponent<AudioListener>();
            }

            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }

            _actions.Clear();
            Kit.Shutdown();
            AudioKit.Shutdown();
            Time.timeScale = _timeScale;
            foreach (var root in _objects)
            {
                if (root)
                {
                    Object.Destroy(root);
                }
            }

            _objects.Clear();
            Object.Destroy(_clip);
            Object.Destroy(_other);
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

        private T OwnAction<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        private static FieldInfo Field(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, name);
            return field;
        }

        private static void SetField(object target, string name, object value) => Field(target, name).SetValue(target, value);
        private static T GetField<T>(object target, string name) => (T)Field(target, name).GetValue(target);
        private static IList EmitterPlaybacks(AudioEmitter emitter) => GetField<IList>(emitter, "_playbacks");
        private static List<AudioHandle> OwnedMusic(AudioMusicPlayer player) => GetField<List<AudioHandle>>(player, "_owned");

        private static AudioHandle OnlySound()
        {
            var sounds = AudioKit.GetPlayingSounds();
            Assert.AreEqual(1, sounds.Length);
            return sounds[0];
        }

        private static void AssertLease(AudioHandle expected, AudioHandle actual)
        {
            Assert.AreSame(expected.Voice, actual.Voice);
            Assert.AreEqual(expected.Generation, actual.Generation);
        }

        private AudioEmitter NewEmitter(AudioEmitter.PositionMode mode = AudioEmitter.PositionMode.TwoDimensional, bool loop = false)
        {
            var root = OwnObject("StartedComponent.Emitter");
            root.SetActive(false);
            var emitter = root.AddComponent<AudioEmitter>();
            SetField(emitter, "clips", new List<AudioClip> { _clip });
            SetField(emitter, "weightedClips", new List<AudioWeightedClip> { new AudioWeightedClip(_clip) });
            SetField(emitter, "positionMode", mode);
            SetField(emitter, "loop", loop);
            SetField(emitter, "playOnAwake", false);
            root.SetActive(true);
            return emitter;
        }

        private AudioMusicPlayer NewMusic(bool crossFade = false)
        {
            var root = OwnObject("StartedComponent.Music");
            root.SetActive(false);
            var player = root.AddComponent<AudioMusicPlayer>();
            SetField(player, "clip", _clip);
            SetField(player, "playOnAwake", false);
            SetField(player, "crossFadeOnPlay", crossFade);
            root.SetActive(true);
            return player;
        }

        [UnityTest]
        public IEnumerator EmitterStartedSeesCommittedLeaseAndReusesWrapperAcrossSelectors()
        {
            foreach (AudioEmitter.PositionMode mode in Enum.GetValues(typeof(AudioEmitter.PositionMode)))
            {
                foreach (var weighted in new[] { false, true })
                {
                    foreach (var automatic in new[] { false, true })
                    {
                        var emitter = NewEmitter(mode);
                        SetField(emitter, "useWeightedRandom", weighted);
                        if (automatic)
                        {
                            SetField(emitter, "autoFade", new AudioAutoFade(.1f, 0f));
                        }

                        var starts = 0;
                        emitter.Started.AddListener(() =>
                        {
                            starts++;
                            var handle = OnlySound();
                            Assert.IsTrue(handle.IsValid);
                            Assert.IsTrue(handle.IsPlaying);
                            Assert.AreSame(_clip, handle.Voice.Source.clip);
                            Assert.AreEqual(1, EmitterPlaybacks(emitter).Count);
                            var playback = EmitterPlaybacks(emitter)[0];
                            AssertLease(handle, (AudioHandle)playback.GetType().GetField("Handle").GetValue(playback));
                            Assert.AreEqual(automatic ? 0f : 1f, handle.Voice.Source.volume, .001f);
                            handle.Pause();
                            handle.Resume();
                        });
                        emitter.Play();
                        Assert.AreEqual(1, starts);
                        var wrapper = GetField<Action<AudioClip, AudioHandle>>(emitter, "_startedCallback");
                        Assert.IsNotNull(wrapper);
                        emitter.Stop();
                        emitter.Play();
                        Assert.AreEqual(2, starts, "Pause/Resume must not emit another Started event.");
                        Assert.AreSame(wrapper, GetField<Action<AudioClip, AudioHandle>>(emitter, "_startedCallback"));
                        emitter.Stop();
                    }
                }
            }

            var emptyEventEmitter = NewEmitter();
            emptyEventEmitter.Play();
            Assert.AreEqual(1, EmitterPlaybacks(emptyEventEmitter).Count, "An empty default UnityEvent still tracks its sound.");
            Assert.IsTrue(OnlySound().IsPlaying);
            emptyEventEmitter.Stop();
            yield return null;
        }

        [UnityTest]
        public IEnumerator EmitterStartedStopAndRejectedPlaybackNeverReaddTheOriginalLease()
        {
            var emitter = NewEmitter();
            var starts = 0;
            emitter.Started.AddListener(() =>
            {
                starts++;
                Assert.AreEqual(1, EmitterPlaybacks(emitter).Count);
                emitter.Stop();
            });
            emitter.Play();
            Assert.AreEqual(1, starts);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.AreEqual(0, EmitterPlaybacks(emitter).Count);
            AudioKit.Settings.IsSoundOn.Value = false;
            emitter.Play();
            Assert.AreEqual(1, starts);
            AudioKit.Settings.IsSoundOn.Value = true;
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = 1, OverflowPolicy = SoundOverflowPolicy.RejectNew };
            var other = AudioKit.PlaySound(_other);
            emitter.Play();
            Assert.AreEqual(1, starts);
            Assert.AreEqual(0, EmitterPlaybacks(emitter).Count);
            Assert.IsTrue(other.IsValid);
            emitter.Stop();
            Assert.IsTrue(other.IsValid);
            other.Stop();
            yield return null;
        }

        [UnityTest]
        public IEnumerator EmitterDisableUsesStartedRequestSnapshotForOneShotsLoopsAndFollowing()
        {
            foreach (AudioEmitter.PositionMode mode in Enum.GetValues(typeof(AudioEmitter.PositionMode)))
            {
                foreach (var loop in new[] { false, true })
                {
                    var emitter = NewEmitter(mode, loop);
                    AudioHandle original = default;
                    emitter.Started.AddListener(() =>
                    {
                        original = OnlySound();
                        SetField(emitter, "loop", !loop);
                        SetField(emitter, "positionMode", mode == AudioEmitter.PositionMode.FollowTransform
                            ? AudioEmitter.PositionMode.TwoDimensional : AudioEmitter.PositionMode.FollowTransform);
                        emitter.enabled = false;
                    });
                    emitter.Play();
                    var stops = loop || mode == AudioEmitter.PositionMode.FollowTransform;
                    Assert.AreEqual(!stops, original.IsValid);
                    Assert.AreEqual(stops ? 0 : 1, EmitterPlaybacks(emitter).Count);
                    Assert.AreEqual(stops ? 0 : 1, AudioKit.SoundCount);
                    emitter.Stop();
                    Assert.IsFalse(original.IsValid);
                }
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator EmitterNestedPlayAndThrowKeepOnlyTheNestedLeaseEvenWithVoiceReuse()
        {
            foreach (var throws in new[] { false, true })
            {
                var emitter = NewEmitter(loop: true);
                var starts = 0;
                var failure = new InvalidOperationException("emitter-started");
                AudioHandle original = default, nested = default;
                emitter.Started.AddListener(() =>
                {
                    starts++;
                    if (starts != 1)
                    {
                        nested = OnlySound();
                        return;
                    }
                    original = OnlySound();
                    SetField(emitter, "clips", new List<AudioClip> { _other });
                    emitter.Play();
                    if (throws)
                    {
                        throw failure;
                    }
                });
                if (throws)
                {
                    Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() => emitter.Play()));
                }
                else
                {
                    emitter.Play();
                }

                Assert.AreEqual(2, starts);
                Assert.IsFalse(original.IsValid);
                Assert.IsTrue(nested.IsValid);
                Assert.AreSame(original.Voice, nested.Voice, "The stale lease must be harmless when its pooled voice is reused.");
                Assert.AreNotEqual(original.Generation, nested.Generation);
                AssertLease(nested, OnlySound());
                Assert.AreEqual(1, EmitterPlaybacks(emitter).Count);
                emitter.enabled = false;
                Assert.IsFalse(nested.IsValid);
                Assert.AreEqual(0, EmitterPlaybacks(emitter).Count);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ActionStartedResetAndDeinitCancelTheRegisteredLeaseWithoutCompletion()
        {
            foreach (var deinit in new[] { false, true })
            {
                AudioKit.ResetSoundStats();
                AudioKit.SetClipSoundLimit(_clip, 1);
                var starts = 0;
                var finishes = 0;
                AudioHandle original = default;
                PlaySoundAction action = null;
                Action<AudioClip, AudioHandle> started = (clip, handle) =>
                {
                    starts++;
                    Assert.AreSame(_clip, clip);
                    AssertLease(handle, GetField<AudioHandle>(action, "_handle"));
                    Assert.IsTrue(handle.IsPlaying);
                    Assert.AreEqual(1, AudioKit.GetSoundCount(_clip));
                    Assert.AreEqual(starts, AudioKit.SoundStats.AdmittedCount);
                    Assert.AreEqual(1, AudioKit.SoundStats.PeakActiveCount);
                    if (starts != 1)
                    {
                        return;
                    }

                    original = handle;
                    if (deinit)
                    {
                        action.Deinit();
                    }
                    else
                    {
                        action.Reset();
                    }
                };
                action = OwnAction(deinit
                    ? PlaySoundAction.Allocate(_clip, () => finishes++, onStarted: started)
                    : PlaySoundAction.AllocateWithOptions(_clip, AudioSoundOptions.Defaults, () => finishes++, started));
                Assert.IsFalse(action.Execute(0f));
                Assert.AreEqual(1, starts);
                Assert.AreEqual(0, finishes);
                Assert.IsFalse(original.IsValid);
                Assert.IsFalse(GetField<AudioHandle>(action, "_handle").IsValid);
                Assert.AreEqual(0, AudioKit.SoundCount);
                Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
                Assert.AreEqual(1L, AudioKit.SoundStats.AdmittedCount);
                Assert.AreEqual(deinit, action.IsDeinitialized);
                if (deinit)
                {
                    Assert.IsNull(GetField<Action<AudioClip, AudioHandle>>(action, "_onStarted"));
                }
                else
                {
                    Assert.AreEqual(ActionStatus.NotStart, action.Status);
                    Assert.IsFalse(action.Execute(0f));
                    Assert.AreEqual(2, starts, "Reset retains the configured Started callback.");
                    Assert.IsTrue(GetField<AudioHandle>(action, "_handle").IsValid);
                    action.Deinit();
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ActionStartedRecursiveExecuteAndResetExecuteCannotOverwriteNewLease()
        {
            foreach (var reset in new[] { false, true })
            {
                var starts = 0;
                var finishes = 0;
                AudioHandle original = default, current = default;
                PlaySoundAction action = null;
                action = OwnAction(PlaySoundAction.Allocate(_clip, () => finishes++, onStarted: (_, handle) =>
                {
                    starts++;
                    current = handle;
                    if (starts != 1)
                    {
                        return;
                    }

                    original = handle;
                    if (reset)
                    {
                        action.Reset();
                    }

                    Assert.IsFalse(action.Execute(0f));
                }));
                Assert.IsFalse(action.Execute(0f));
                Assert.AreEqual(reset ? 2 : 1, starts);
                Assert.AreEqual(!reset, original.IsValid);
                Assert.IsTrue(current.IsValid);
                AssertLease(current, GetField<AudioHandle>(action, "_handle"));
                AssertLease(current, OnlySound());
                Assert.AreEqual(ActionStatus.Started, action.Status);
                Assert.AreEqual(0, finishes);
                current.Stop();
                Assert.IsTrue(action.Execute(0f));
                Assert.AreEqual(1, finishes);
                action.Deinit();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ActionStartedThrowCleansOnlyTheOriginalExecution()
        {
            foreach (var nestedExecution in new[] { false, true })
            {
                var starts = 0;
                var finishes = 0;
                var failure = new InvalidOperationException("action-started");
                AudioHandle original = default, nested = default;
                PlaySoundAction action = null;
                action = OwnAction(PlaySoundAction.AllocateWithOptions(_clip, AudioSoundOptions.Defaults,
                    () => finishes++, (_, handle) =>
                    {
                        starts++;
                        if (starts != 1)
                        {
                            nested = handle;
                            return;
                        }
                        original = handle;
                        if (nestedExecution)
                        {
                            action.Reset();
                            Assert.IsFalse(action.Execute(0f));
                        }
                        throw failure;
                    }));
                Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() => action.Execute(0f)));
                Assert.IsFalse(original.IsValid);
                Assert.AreEqual(0, finishes);
                Assert.AreEqual(nestedExecution ? 1 : 0, AudioKit.SoundCount);
                if (nestedExecution)
                {
                    Assert.IsTrue(nested.IsValid);
                    AssertLease(nested, GetField<AudioHandle>(action, "_handle"));
                    Assert.AreEqual(ActionStatus.Started, action.Status);
                }
                else
                {
                    Assert.IsFalse(GetField<AudioHandle>(action, "_handle").IsValid);
                    Assert.AreEqual(ActionStatus.NotStart, action.Status);
                }
                action.Deinit();
                Assert.AreEqual(0, AudioKit.SoundCount);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ActionNaturalCompletionAndSequenceExtensionsKeepTheirOriginalFinishContract()
        {
            var starts = 0;
            var finishes = 0;
            var action = OwnAction(PlaySoundAction.Allocate(_short, () => finishes++, onStarted: (_, __) => starts++));
            Assert.IsFalse(action.Execute(0f));
            action.IsPaused = true;
            var deadline = Time.realtimeSinceStartup + 3f;
            while (action.Status != ActionStatus.Finished && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.AreEqual(ActionStatus.Finished, action.Status, "Paused actions still observe natural sound completion.");
            Assert.AreEqual(1, starts);
            Assert.AreEqual(0, finishes, "The sound's natural callback marks Finished; Execute emits the action notification.");
            Assert.IsTrue(action.Execute(0f));
            Assert.IsTrue(action.Execute(0f));
            Assert.AreEqual(1, finishes);
            action.Deinit();

            foreach (var withOptions in new[] { false, true })
            {
                var sequence = OwnAction(Kit.Sequence());
                AudioHandle lease = default;
                var sequenceStarts = 0;
                Action<AudioClip, AudioHandle> started = (clip, handle) =>
                {
                    sequenceStarts++;
                    lease = handle;
                    Assert.AreSame(_clip, clip);
                    Assert.IsTrue(handle.IsPlaying);
                    Assert.AreEqual(AudioOutputRoute.RouteMode.Direct, handle.Voice.Options.OutputRoute.Mode);
                };
                var options = AudioSoundOptions.Defaults;
                options.OutputRoute = AudioOutputRoute.Direct;
                Assert.AreSame(sequence, withOptions
                    ? sequence.PlaySoundWithOptions(_clip, options, started)
                    : sequence.PlaySound(_clip, AudioOutputRoute.Direct, started));
                Assert.IsFalse(sequence.Execute(0f));
                Assert.AreEqual(1, sequenceStarts);
                sequence.Deinit();
                Assert.IsFalse(lease.IsValid);
            }
        }

        [UnityTest]
        public IEnumerator MusicDefaultsUseLegacyReplacementAndIgnoreDisabledCrossFadeDuration()
        {
            var player = NewMusic();
            Assert.IsFalse(GetField<bool>(player, "crossFadeOnPlay"));
            Assert.AreEqual(1f, GetField<float>(player, "crossFadeDuration"));
            Assert.IsTrue(GetField<bool>(player, "crossFadeIgnoreTimeScale"));
            SetField(player, "crossFadeDuration", float.NaN);
            SetField(player, "crossFadeCurve", (AudioCrossfadeCurve)99);
            player.Play();
            var first = AudioKit.Music;
            Assert.IsTrue(first.IsPlaying);
            Assert.IsTrue(first.Voice.Source.loop);
            Assert.AreEqual(1f, first.Voice.Source.volume);
            SetField(player, "clip", _other);
            player.Play();
            var second = AudioKit.Music;
            Assert.IsFalse(first.IsValid);
            Assert.IsTrue(second.IsPlaying);
            Assert.AreEqual(1, OwnedMusic(player).Count);
            SetField(player, "loop", false);
            SetField(player, "autoFade", new AudioAutoFade(.1f, .1f));
            player.Play();
            Assert.IsFalse(second.IsValid);
            Assert.IsFalse(AudioKit.Music.Voice.Source.loop);
            Assert.AreEqual(0f, AudioKit.Music.Voice.Source.volume);
            var automatic = AudioKit.Music;
            var source = automatic.Voice.Source;
            SetField(player, "clip", null);
            player.Play();
            Assert.IsTrue(automatic.IsValid, "A missing clip must not stop the component's owned music.");
            AssertLease(automatic, AudioKit.Music);
            Assert.AreSame(source, AudioKit.Music.Voice.Source);
            CollectionAssert.AreEqual(new[] { automatic }, OwnedMusic(player));
            player.Stop();
            Assert.IsFalse(automatic.IsValid);
            Assert.AreEqual(0, OwnedMusic(player).Count);
            yield return null;
        }

        [UnityTest]
        public IEnumerator MusicCrossFadeKeepsOwnTailsAndStopOrDisableReleasesAllOwnedLeases()
        {
            foreach (var disable in new[] { false, true })
            {
                var player = NewMusic(true);
                SetField(player, "crossFadeDuration", 10f);
                SetField(player, "loop", !disable);
                player.Play();
                var first = AudioKit.Music;
                Assert.AreEqual(!disable, first.Voice.Options.Loop, "The component explicitly supplies its own loop value, not the core's omitted-options default.");
                SetField(player, "clip", _other);
                player.Play();
                var second = AudioKit.Music;
                Assert.IsTrue(first.IsValid, "CrossFade Play must not call the component's Stop first.");
                Assert.IsTrue(first.IsPlaying);
                Assert.IsTrue(second.IsPlaying);
                Assert.AreEqual(0f, second.Voice.Source.volume);
                Assert.AreEqual(2, OwnedMusic(player).Count);
                SetField(player, "clip", _clip);
                player.Play();
                var third = AudioKit.Music;
                Assert.IsFalse(first.IsValid);
                Assert.IsTrue(second.IsValid);
                Assert.IsTrue(third.IsValid);
                Assert.AreEqual(2, OwnedMusic(player).Count, "Expired tails are removed after replacement, not retained as owned leases.");
                if (disable)
                {
                    player.enabled = false;
                }
                else
                {
                    player.Stop();
                }

                Assert.IsFalse(second.IsValid);
                Assert.IsFalse(third.IsValid);
                Assert.AreEqual(0, OwnedMusic(player).Count);
                Assert.IsFalse(AudioKit.Music.IsValid);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator MusicDisablingPreviousOwnersNeverStopsAnotherOwnersTarget()
        {
            var firstOwner = NewMusic();
            firstOwner.Play();
            var first = AudioKit.Music;
            var secondOwner = NewMusic(true);
            SetField(secondOwner, "clip", _other);
            SetField(secondOwner, "crossFadeDuration", 10f);
            secondOwner.Play();
            var second = AudioKit.Music;
            Assert.IsTrue(first.IsValid);
            firstOwner.enabled = false;
            Assert.IsFalse(first.IsValid);
            Assert.IsTrue(second.IsValid);
            AssertLease(second, AudioKit.Music);
            var external = AudioKit.CrossFadeMusic(_clip, 10f);
            Assert.IsTrue(second.IsValid);
            secondOwner.enabled = false;
            Assert.IsFalse(second.IsValid);
            Assert.IsTrue(external.IsValid);
            Assert.IsTrue(external.IsPlaying);
            AssertLease(external, AudioKit.Music);
            firstOwner.Stop();
            secondOwner.Stop();
            Assert.IsTrue(external.IsValid);
            external.Stop();
            yield return null;
        }

        [UnityTest]
        public IEnumerator MusicInvalidConfigurationAndMissingClipDoNotCutOrReplaceOldLease()
        {
            foreach (var crossFade in new[] { false, true })
            {
                var player = NewMusic(crossFade);
                player.Play();
                var original = AudioKit.Music;
                var source = original.Voice.Source;
                var sourceCount = AudioManager.Instance.GetComponentsInChildren<AudioSource>(true).Length;
                SetField(player, "volume", float.NaN);
                Assert.Throws<ArgumentOutOfRangeException>(() => player.Play());
                Assert.IsTrue(original.IsValid);
                SetField(player, "volume", 1f);
                SetField(player, "outputRoute", new AudioOutputRoute { Mode = (AudioOutputRoute.RouteMode)99 });
                Assert.Throws<ArgumentOutOfRangeException>(() => player.Play());
                Assert.IsTrue(original.IsValid);
                SetField(player, "outputRoute", default(AudioOutputRoute));
                SetField(player, "autoFade", new AudioAutoFade(float.NaN, 0f));
                Assert.Throws<ArgumentOutOfRangeException>(() => player.Play());
                Assert.IsTrue(original.IsValid);
                SetField(player, "autoFade", default(AudioAutoFade));
                if (crossFade)
                {
                    foreach (var duration in new[] { -1f, float.NaN, float.PositiveInfinity })
                    {
                        SetField(player, "crossFadeDuration", duration);
                        Assert.Throws<ArgumentOutOfRangeException>(() => player.Play());
                        Assert.IsTrue(original.IsValid);
                        AssertLease(original, AudioKit.Music);
                        Assert.AreEqual(1, OwnedMusic(player).Count);
                    }
                    SetField(player, "crossFadeDuration", 1f);
                    SetField(player, "crossFadeCurve", (AudioCrossfadeCurve)99);
                    Assert.Throws<ArgumentOutOfRangeException>(() => player.Play());
                    Assert.IsTrue(original.IsValid);
                    AssertLease(original, AudioKit.Music);
                    Assert.AreEqual(1, OwnedMusic(player).Count);
                    SetField(player, "crossFadeCurve", AudioCrossfadeCurve.Linear);
                }
                var destroyed = AudioClip.Create("started-component-destroyed", 441, 1, 44100, false);
                Object.DestroyImmediate(destroyed);
                foreach (var clip in new[] { null, destroyed })
                {
                    SetField(player, "clip", clip);
                    player.Play();
                    Assert.IsTrue(original.IsValid);
                    AssertLease(original, AudioKit.Music);
                    Assert.AreSame(source, AudioKit.Music.Voice.Source);
                    Assert.IsTrue(original.IsPlaying);
                    CollectionAssert.AreEqual(new[] { original }, OwnedMusic(player));
                    Assert.AreEqual(sourceCount, AudioManager.Instance.GetComponentsInChildren<AudioSource>(true).Length,
                        "Invalid options and missing clips must not allocate another music source.");
                }
                player.Stop();
                Assert.IsFalse(original.IsValid);
            }
            yield return null;
        }
    }
}
