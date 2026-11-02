using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Actions;
using YLFramework.Audio;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace YLFramework.Tests.Audio
{
    public class AudioAutoFadeTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;
        private GameObject _target;
        private Random.State _randomState;
        private readonly List<IAction> _actions = new List<IAction>();

        [SetUp]
        public void SetUp()
        {
            _randomState = Random.state;
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("auto-fade-edit", 441000, 1, 44100, false);
            _target = new GameObject("auto-fade-edit-target");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var action in _actions)
            {
                if (!action.IsDeinitialized)
                {
                    action.Deinit();
                }
            }

            _actions.Clear();
            YLFramework.Actions.ActionKit.Shutdown();
            _settings.Dispose();
            Object.DestroyImmediate(_clip);
            Object.DestroyImmediate(_target);
            Random.state = _randomState;
        }

        private AudioHandle PlayEntry(int entry, AudioSoundOptions options, AudioClip clip)
        {
            switch (entry)
            {
                case 0:
                    return AudioKit.PlaySound(clip, options);
                case 1:
                    return AudioKit.PlaySoundAt(clip, Vector3.one, options);
                case 2:
                    return AudioKit.PlaySoundFollowing(clip, _target.transform, options);
                case 3:
                    return AudioKit.PlayVoiceAt(clip, Vector3.one, options);
                case 4:
                    return AudioKit.PlayVoiceFollowing(clip, _target.transform, options);
                case 5:
                    return AudioKit.PlayRandomSound(new[] { clip }, options);
                case 6:
                    return AudioKit.PlayWeightedRandomSound(new[] { new AudioWeightedClip(clip) }, options);
                case 7:
                    return AudioKit.PlayMusicWithOptions(clip, options);
                default:
                    return AudioKit.PlayVoiceWithOptions(clip, options);
            }
        }

        [Test]
        public void DataConstructionAndDefaultsArePassiveAndPreserveExactFieldValues()
        {
            AudioKit.Shutdown();
            var fade = new AudioAutoFade(float.NaN, -1f);
            Assert.IsTrue(float.IsNaN(fade.FadeInSeconds));
            Assert.AreEqual(-1f, fade.FadeOutSeconds, "Construction only assigns; validation belongs to playback.");
            Assert.IsTrue(typeof(AudioAutoFade).IsSerializable);
            Assert.AreEqual(typeof(float), typeof(AudioAutoFade).GetField("FadeInSeconds").FieldType);
            Assert.AreEqual(typeof(float), typeof(AudioAutoFade).GetField("FadeOutSeconds").FieldType);
            Assert.IsFalse(default(AudioAutoFade).IsEnabled);
            Assert.IsTrue(new AudioAutoFade(float.Epsilon, 0f).IsEnabled);
            Assert.IsTrue(new AudioAutoFade(0f, float.Epsilon).IsEnabled);
            var defaults = AudioSoundOptions.Defaults;
            Assert.AreEqual(default(AudioAutoFade), defaults.AutoFade);
            Assert.IsFalse(defaults.Loop);
            Assert.AreEqual(1f, defaults.Volume);
            Assert.AreEqual(1f, defaults.Pitch);
            Assert.IsNull(defaults.PlaySoundMode);
            Assert.AreEqual(1f, defaults.MinDistance);
            Assert.AreEqual(50f, defaults.MaxDistance);
            Assert.AreEqual(AudioRolloffMode.Linear, defaults.RolloffMode);
            Assert.AreEqual(AudioOutputRoute.Inherit, defaults.OutputRoute);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void EveryInvalidSideIsRejectedByConfigAndOptions(float value)
        {
            foreach (var fade in new[] { new AudioAutoFade(value, 0f), new AudioAutoFade(0f, value) })
            {
                foreach (var loop in new[] { false, true })
                {
                    Assert.Catch<ArgumentException>(() => fade.Validate(loop));
                }

                var options = AudioSoundOptions.Defaults;
                options.AutoFade = fade;
                Assert.Catch<ArgumentException>(() => options.Validate(false));
                Assert.Catch<ArgumentException>(() => options.Validate(true));
            }
        }

        [TestCase(0f)]
        [TestCase(float.Epsilon)]
        [TestCase(1f)]
        [TestCase(float.MaxValue)]
        public void AllFiniteNonnegativeDurationsAreAcceptedWithoutEpsilonThreshold(float value)
        {
            Assert.DoesNotThrow(() => new AudioAutoFade(value, value).Validate(false));
            Assert.DoesNotThrow(() => new AudioAutoFade(value, 0f).Validate(true));
            if (value > 0f)
            {
                Assert.Catch<ArgumentException>(() => new AudioAutoFade(0f, value).Validate(true));
            }
        }

        [Test]
        public void EnabledClipValidationUsesLivePcmMetadataAndDisabledConfigNeedsNoClip()
        {
            var disabled = default(AudioAutoFade);
            var enabled = new AudioAutoFade(float.Epsilon, 0f);
            Assert.DoesNotThrow(() => disabled.ValidateClip(null));
            Assert.Catch<ArgumentException>(() => enabled.ValidateClip(null));
            Assert.Greater(_clip.samples, 0);
            Assert.Greater(_clip.frequency, 0);
            Assert.DoesNotThrow(() => enabled.ValidateClip(_clip));
            var destroyed = AudioClip.Create("auto-fade-destroyed", 441, 1, 44100, false);
            Object.DestroyImmediate(destroyed);
            Assert.DoesNotThrow(() => disabled.ValidateClip(destroyed));
            Assert.Catch<ArgumentException>(() => enabled.ValidateClip(destroyed));
            var options = AudioSoundOptions.Defaults;
            options.AutoFade = enabled;
            Assert.Catch<ArgumentException>(() => options.ValidateClip(destroyed));
            Assert.DoesNotThrow(() => options.ValidateClip(_clip));
        }

        [TestCase(0f, 0f, 10d, 0d, 0d)]
        [TestCase(2f, 3f, 10d, 2d, 3d)]
        [TestCase(4f, 6f, 1d, .4d, .6d)]
        [TestCase(0f, 8f, 2d, 0d, 2d)]
        [TestCase(8f, 0f, 2d, 2d, 0d)]
        [TestCase(4f, 6f, 10d, 4d, 6d)]
        public void NormalizeCompressesBothSidesProportionallyOnlyWhenTheirSumExceedsLength(
            float inputIn, float inputOut, double length, double expectedIn, double expectedOut)
        {
            new AudioAutoFade(inputIn, inputOut).Normalize(length, false, out var fadeIn, out var fadeOut);
            Assert.AreEqual(expectedIn, fadeIn, 1e-12);
            Assert.AreEqual(expectedOut, fadeOut, 1e-12);
            Assert.LessOrEqual(fadeIn + fadeOut, length + 1e-12);
            if (fadeIn > 0d && fadeOut > 0d && inputIn + (double)inputOut > length)
            {
                Assert.AreEqual(1f, AudioAutoFade.Evaluate(fadeIn, length, fadeIn, fadeOut), 1e-6f);
            }
        }

        [Test]
        public void NormalizeUsesDoubleForHugeSumAndRetainsSubnormalPositiveSides()
        {
            new AudioAutoFade(float.MaxValue, float.MaxValue).Normalize(10d, false, out var a, out var b);
            Assert.AreEqual(5d, a, 1e-12);
            Assert.AreEqual(5d, b, 1e-12);
            new AudioAutoFade(float.MaxValue, float.MaxValue / 2f).Normalize(3d, false, out a, out b);
            Assert.AreEqual(2d, a, 1e-12);
            Assert.AreEqual(1d, b, 1e-12);
            new AudioAutoFade(float.Epsilon, float.Epsilon).Normalize(1d, false, out a, out b);
            Assert.AreEqual((double)float.Epsilon, a);
            Assert.AreEqual((double)float.Epsilon, b);
            Assert.AreEqual(.5f, AudioAutoFade.Evaluate(a / 2d, 1d, a, b));
            new AudioAutoFade(float.MaxValue, float.Epsilon).Normalize(1d, false, out a, out b);
            Assert.AreEqual(1d, a, 1e-12);
            Assert.Greater(b, 0d, "A tiny positive side must not be rounded through float storage.");
            Assert.IsFalse(double.IsInfinity(b));
            new AudioAutoFade(float.Epsilon, float.Epsilon).Normalize(1e-100d, false, out a, out b);
            Assert.AreEqual(5e-101d, a, 1e-112d);
            Assert.AreEqual(5e-101d, b, 1e-112d);
            Assert.AreEqual(1f, AudioAutoFade.Evaluate(a, 1e-100d, a, b), 1e-6f);
        }

        [TestCase(0f, 0d)]
        [TestCase(2f, 2d)]
        [TestCase(20f, 10d)]
        [TestCase(float.MaxValue, 10d)]
        public void LoopNormalizeClampsOnlyTheInitialFadeIn(float input, double expected)
        {
            new AudioAutoFade(input, 0f).Normalize(10d, true, out var fadeIn, out var fadeOut);
            Assert.AreEqual(expected, fadeIn);
            Assert.AreEqual(0d, fadeOut);
        }

        [TestCase(0d)]
        [TestCase(-1d)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void NormalizeRejectsInvalidClipLengthsRatherThanProducingAnInvalidEnvelope(double length)
        {
            var fade = new AudioAutoFade(1f, 1f);
            Assert.Catch<ArgumentException>(() => fade.Normalize(length, false, out _, out _));
        }

        [TestCase(-1d, 0f)]
        [TestCase(0d, 0f)]
        [TestCase(1d, .5f)]
        [TestCase(2d, 1f)]
        [TestCase(5d, 1f)]
        [TestCase(7d, 1f)]
        [TestCase(8.5d, .5f)]
        [TestCase(10d, 0f)]
        [TestCase(11d, 0f)]
        public void EvaluateTakesMinimumOfClampedHeadAndTail(double time, float expected)
        {
            Assert.AreEqual(expected, AudioAutoFade.Evaluate(time, 10d, 2d, 3d), 1e-6f);
        }

        [Test]
        public void EvaluateZeroAndSingleSidesKeepTheirExactEndpointSemantics()
        {
            foreach (var time in new[] { -1d, 0d, 5d, 10d, 11d })
            {
                Assert.AreEqual(1f, AudioAutoFade.Evaluate(time, 10d, 0d, 0d));
            }

            Assert.AreEqual(0f, AudioAutoFade.Evaluate(0d, 10d, 2d, 0d));
            Assert.AreEqual(1f, AudioAutoFade.Evaluate(10d, 10d, 2d, 0d));
            Assert.AreEqual(1f, AudioAutoFade.Evaluate(0d, 10d, 0d, 3d));
            Assert.AreEqual(0f, AudioAutoFade.Evaluate(10d, 10d, 0d, 3d));
            Assert.AreEqual(.5f, AudioAutoFade.Evaluate(.2d, 1d, .4d, .6d), 1e-6f);
            Assert.AreEqual(1f, AudioAutoFade.Evaluate(.4d, 1d, .4d, .6d), 1e-6f);
            Assert.AreEqual(.5f, AudioAutoFade.Evaluate(.7d, 1d, .4d, .6d), 1e-6f);
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
        public void InvalidAutoConfigIsRejectedBeforeCreatingHostOrReplacingSlot(int entry)
        {
            var current = AudioKit.PlayMusic(_clip);
            var voice = AudioKit.PlayVoice(_clip, loop: true);
            var options = AudioSoundOptions.Defaults;
            foreach (var fade in new[] { new AudioAutoFade(-1f, 0f), new AudioAutoFade(0f, float.NaN), new AudioAutoFade(1f, 1f) })
            {
                options.AutoFade = fade;
                options.Loop = true;
                Assert.Catch<ArgumentException>(() => PlayEntry(entry, options, _clip));
                Assert.IsTrue(current.IsValid);
                Assert.IsTrue(voice.IsValid);
                Assert.AreEqual(current, AudioKit.Music);
                Assert.AreEqual(voice, AudioKit.Voice);
                Assert.AreEqual(0, AudioKit.SoundCount);
            }
            AudioKit.Shutdown();
            Assert.Catch<ArgumentException>(() => PlayEntry(entry, options, _clip));
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
        }

        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames)]
        public void InvalidRequestsNeitherConsumeDeduplicationNorHideBehindItsRejection(AudioKit.PlaySoundModes mode)
        {
            var options = AudioSoundOptions.Defaults;
            options.PlaySoundMode = mode;
            options.Loop = true;
            options.AutoFade = new AudioAutoFade(1f, 1f);
            Assert.Catch<ArgumentException>(() => AudioKit.PlaySound(_clip, options));
            Assert.IsFalse(AudioManager.HasInstance);
            options.AutoFade = new AudioAutoFade(1f, 0f);
            var current = AudioKit.PlaySound(_clip, options);
            Assert.IsTrue(current.IsValid);
            options.AutoFade = new AudioAutoFade(float.NaN, 0f);
            Assert.Catch<ArgumentException>(() => AudioKit.PlaySound(_clip, options));
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
            options.AutoFade = new AudioAutoFade(1f, 0f);
            Assert.IsFalse(AudioKit.PlaySound(_clip, options).IsValid);
        }

        [Test]
        public void DisabledSoundStillValidatesAutoConfigurationBeforeRejectingPlayback()
        {
            AudioKit.Settings.IsSoundOn.Value = false;
            var options = AudioSoundOptions.Defaults;
            options.AutoFade = new AudioAutoFade(float.NaN, 0f);
            Assert.Catch<ArgumentException>(() => AudioKit.PlaySound(_clip, options));
            Assert.IsFalse(AudioManager.HasInstance);
            options.AutoFade = new AudioAutoFade(1f, 1f);
            Assert.IsFalse(AudioKit.PlaySound(_clip, options).IsValid);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WithOptionsSlotsIgnoreSoundPolicyAndSpatialFieldsButHonorPitchAndVolume(bool music)
        {
            var options = AudioSoundOptions.Defaults;
            options.AutoFade = new AudioAutoFade(0f, 2f);
            options.Volume = .6f;
            options.Pitch = 1.5f;
            options.PlaySoundMode = (AudioKit.PlaySoundModes)999;
            options.MinDistance = float.NaN;
            options.MaxDistance = -1f;
            options.RolloffMode = (AudioRolloffMode)999;
            var handle = music ? AudioKit.PlayMusicWithOptions(_clip, options) : AudioKit.PlayVoiceWithOptions(_clip, options);
            Assert.IsTrue(handle.IsValid);
            Assert.IsFalse(handle.Voice.Source.loop);
            Assert.AreEqual(1.5f, handle.Voice.Source.pitch);
            Assert.AreEqual(.6f, handle.Voice.Source.volume, .0001f);
            Assert.AreEqual(0f, handle.Voice.Source.spatialBlend);
            var replacement = music ? AudioKit.PlayMusicWithOptions(_clip, options) : AudioKit.PlayVoiceWithOptions(_clip, options);
            Assert.IsFalse(handle.IsValid);
            Assert.IsTrue(replacement.IsValid);
        }

        [Test]
        public void NullNewSlotRequestsAreNoOpsEvenWithInvalidOptions()
        {
            AudioKit.Shutdown();
            var options = AudioSoundOptions.Defaults;
            options.AutoFade = new AudioAutoFade(float.NaN, -1f);
            Assert.IsFalse(AudioKit.PlayMusicWithOptions(null, options).IsValid);
            Assert.IsFalse(AudioKit.PlayVoiceWithOptions(null, options).IsValid);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void NewEntrypointsDoNotOverloadOrReplaceTheOldPublicContracts()
        {
            var callback = typeof(Action<AudioClip, AudioHandle>);
            var oldTypes = new[] { typeof(AudioClip), typeof(bool), typeof(float), callback, callback, typeof(AudioOutputRoute) };
            foreach (var name in new[] { "PlayMusic", "PlayVoice" })
            {
                var method = typeof(AudioKit).GetMethod(name, oldTypes);
                Assert.IsNotNull(method, name);
                var count = 0;
                foreach (var candidate in typeof(AudioKit).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (candidate.Name == name)
                    {
                        count++;
                    }
                }

                Assert.AreEqual(1, count, "Keep the original method group unambiguous.");
            }
            foreach (var name in new[] { "PlayMusicWithOptions", "PlayVoiceWithOptions" })
            {
                var method = typeof(AudioKit).GetMethod(name, new[] { typeof(AudioClip), typeof(AudioSoundOptions), callback, callback });
                Assert.IsNotNull(method, name);
                Assert.AreEqual(typeof(AudioHandle), method.ReturnType);
                var parameters = method.GetParameters();
                Assert.IsFalse(parameters[1].IsOptional);
                Assert.AreEqual("options", parameters[1].Name);
                Assert.IsTrue(parameters[2].IsOptional);
                Assert.IsTrue(parameters[3].IsOptional);
                Assert.IsNull(parameters[2].DefaultValue);
                Assert.IsNull(parameters[3].DefaultValue);
            }
            Assert.IsNotNull(typeof(PlaySoundAction).GetMethod("Allocate", new[] { typeof(AudioClip), typeof(Action), typeof(AudioOutputRoute), callback }));
            Assert.IsNotNull(typeof(PlaySoundAction).GetMethod("AllocateWithOptions", new[] { typeof(AudioClip), typeof(AudioSoundOptions), typeof(Action), callback }));
            Assert.IsNotNull(typeof(PlaySoundExtension).GetMethod("PlaySound", new[] { typeof(ISequence), typeof(AudioClip), typeof(AudioOutputRoute), callback }));
            Assert.IsNotNull(typeof(PlaySoundExtension).GetMethod("PlaySoundWithOptions", new[] { typeof(ISequence), typeof(AudioClip), typeof(AudioSoundOptions), callback }));
            foreach (var method in typeof(AudioHandle).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(method.Name.Contains("AutoFade"), "Do not introduce a runtime automatic-envelope handle API.");
            }
        }

        [Test]
        public void ActionValidatesBeforeAllocationAndResetRetainsCompleteOptionsWhileDeinitClearsThem()
        {
            var options = AudioSoundOptions.Defaults;
            options.Priority = -1;
            Assert.Throws<ArgumentOutOfRangeException>(() => PlaySoundAction.AllocateWithOptions(_clip, options));
            Assert.IsFalse(AudioManager.HasInstance);
            options.Priority = 0;
            options.AutoFade = new AudioAutoFade(1f, 1f);
            options.Loop = true;
            Assert.Catch<ArgumentException>(() => PlaySoundAction.AllocateWithOptions(_clip, options));
            Assert.IsFalse(AudioManager.HasInstance);
            options.Loop = false;
            options.Volume = .4f;
            options.Pitch = 1.5f;
            options.OutputRoute = AudioOutputRoute.Direct;
            options.Priority = 23;
            var calls = 0;
            var action = PlaySoundAction.AllocateWithOptions(_clip, options, () => calls++);
            _actions.Add(action);
            var field = typeof(PlaySoundAction).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "The new and old allocation paths share one options record.");
            Assert.AreEqual(typeof(AudioPlaybackOptions), field.FieldType);
            var allocated = (AudioPlaybackOptions)field.GetValue(action);
            AssertOptionsSnapshot(options, allocated);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.IsFalse(action.Execute(0f));
            var old = AudioKit.GetPlayingSounds()[0];
            Assert.AreEqual(allocated, old.Voice.Options);
            var oldOptions = old.Voice.Options;
            old.SetVolumeScale(.2f);
            old.SetPitch(.8f);
            Assert.AreEqual(.2f, old.Voice.Options.Volume);
            Assert.AreEqual(.8f, old.Voice.Options.Pitch);
            AssertOptionsSnapshot(options, allocated);
            AssertOptionsSnapshot(options, (AudioPlaybackOptions)field.GetValue(action));
            AssertOptionsSnapshot(options, oldOptions);
            old.FadeOut(0f, stopWhenDone: false);
            action.Reset();
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(0, calls);
            Assert.IsFalse(action.Execute(0f));
            var current = AudioKit.GetPlayingSounds()[0];
            AssertOptionsSnapshot(options, current.Voice.Options);
            Assert.AreEqual(allocated, current.Voice.Options);
            AssertOptionsSnapshot(options, oldOptions);
            old.SetVolumeScale(.1f);
            old.SetPitch(2f);
            Assert.AreEqual(.4f, current.Voice.Options.Volume, "An expired lease must not modify the replayed snapshot.");
            Assert.AreEqual(1.5f, current.Voice.Options.Pitch);
            Assert.AreEqual(23, current.Voice.Priority);
            Assert.AreEqual(0f, current.Voice.Source.volume);
            action.Deinit();
            Assert.IsFalse(current.IsValid);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(default(AudioPlaybackOptions), (AudioPlaybackOptions)field.GetValue(action),
                "Deinit clears the captured value rather than constructing valid playback defaults.");
            Assert.AreEqual(default(AudioPlaybackOptions), current.Voice.Options);
            Assert.IsTrue(ReferenceEquals(null, current.Voice.Options.OutputRoute.Group));
        }

        private static void AssertOptionsSnapshot(AudioSoundOptions expected, AudioPlaybackOptions actual)
        {
            Assert.AreEqual(expected.Loop, actual.Loop);
            Assert.AreEqual(expected.Volume, actual.Volume);
            Assert.AreEqual(expected.Pitch, actual.Pitch);
            Assert.AreEqual(expected.PlaySoundMode, actual.PlaySoundMode);
            Assert.AreEqual(expected.MinDistance, actual.MinDistance);
            Assert.AreEqual(expected.MaxDistance, actual.MaxDistance);
            Assert.AreEqual(expected.RolloffMode, actual.RolloffMode);
            Assert.AreEqual(expected.OutputRoute, actual.OutputRoute);
            Assert.AreEqual(expected.AutoFade, actual.AutoFade);
            Assert.AreEqual(expected.Priority, actual.Priority);
        }
    }
}
