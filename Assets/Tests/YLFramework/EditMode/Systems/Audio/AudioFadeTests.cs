using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioFadeTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;
        private GameObject _target;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("fade-edit", 441000, 1, 44100, false);
            _target = new GameObject("fade-target");
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_clip);
            Object.DestroyImmediate(_target);
        }

        private AudioHandle Play(int category, float volume = .8f)
        {
            if (category == 1)
            {
                return AudioKit.PlayMusic(_clip, volume: volume);
            }

            if (category == 2)
            {
                return AudioKit.PlayVoice(_clip, loop: true, volume: volume);
            }

            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            options.Volume = volume;
            return AudioKit.PlaySound(_clip, options);
        }

        private void SetCategoryVolume(int category, float value)
        {
            if (category == 1)
            {
                AudioKit.Settings.MusicVolume.Value = value;
            }
            else if (category == 2)
            {
                AudioKit.Settings.VoiceVolume.Value = value;
            }
            else
            {
                AudioKit.Settings.SoundVolume.Value = value;
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void UnusedEnvelopeKeepsOriginalVolumeFormula(int category)
        {
            SetCategoryVolume(category, .6f);
            var handle = Play(category);
            Assert.AreEqual(.48f, handle.Voice.Source.volume, .0001f);
            handle.Voice.TickFade(10f, 10f);
            Assert.AreEqual(.48f, handle.Voice.Source.volume, .0001f);
            handle.SetVolumeScale(3f);
            Assert.AreEqual(1f, handle.Voice.Source.volume);
            Assert.AreEqual(3f, handle.Voice.Options.Volume);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void FirstFadeInStartsAtSilenceAndPreservesRequestAndCategoryVolume(int category)
        {
            SetCategoryVolume(category, .5f);
            var handle = Play(category);
            var generation = handle.Generation;
            var source = handle.Voice.Source;
            handle.FadeIn(2f);
            Assert.AreEqual(0f, source.volume);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.1f, source.volume, .0001f);
            handle.SetVolumeScale(.4f);
            Assert.AreEqual(.05f, source.volume, .0001f);
            SetCategoryVolume(category, .8f);
            Assert.AreEqual(.08f, source.volume, .0001f);
            handle.Voice.TickFade(1.5f, 1.5f);
            Assert.AreEqual(.32f, source.volume, .0001f);
            Assert.AreEqual(.4f, handle.Voice.Options.Volume);
            Assert.AreEqual(generation, handle.Generation);
            Assert.IsTrue(handle.IsValid);
            Assert.AreSame(_clip, source.clip);
            Assert.IsTrue(source.loop);
        }

        [Test]
        public void NewFadesReplacePriorFadeWithoutJumpingOrRetainingItsStop()
        {
            var handle = Play(0);
            var source = handle.Voice.Source;
            handle.FadeIn(2f);
            handle.Voice.TickFade(1f, 1f);
            Assert.AreEqual(.4f, source.volume, .0001f);
            handle.FadeOut(2f);
            Assert.AreEqual(.4f, source.volume, .0001f);
            handle.Voice.TickFade(1f, 1f);
            Assert.AreEqual(.2f, source.volume, .0001f);
            handle.FadeIn(1f);
            Assert.AreEqual(.2f, source.volume, .0001f);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.5f, source.volume, .0001f);
            handle.Voice.TickFade(10f, 10f);
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(.8f, source.volume, .0001f);
            handle.FadeIn(2f);
            Assert.AreEqual(.8f, source.volume, "A later fade-in at full gain must not jump back to silence.");
            handle.Voice.TickFade(1f, 1f);
            Assert.AreEqual(.8f, source.volume);
        }

        [Test]
        public void FadeOutStopsOnlyItsLeaseWithoutNaturalCompletion()
        {
            var callbacks = 0;
            var handle = AudioKit.PlayMusic(_clip, onFinished: (_, __) => callbacks++);
            handle.FadeOut(2f);
            handle.Voice.TickFade(1f, 1f);
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(.5f, handle.Voice.Source.volume, .0001f);
            handle.Voice.TickFade(1f, 1f);
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, callbacks);
            Assert.IsFalse(handle.Voice.Source.gameObject.activeSelf);
            Assert.IsNull(handle.Voice.Source.clip);
        }

        [Test]
        public void FadeOutWithoutStopKeepsRequestAliveAndCanFadeBackIn()
        {
            var handle = Play(0);
            handle.FadeOut(1f, stopWhenDone: false);
            handle.Voice.TickFade(1f, 1f);
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            handle.SetVolumeScale(.2f);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            handle.FadeIn(1f);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.1f, handle.Voice.Source.volume, .0001f);
        }

        [Test]
        public void ZeroDurationHasImmediateAndExplicitStopSemanticsEvenWhenPaused()
        {
            var handle = Play(0);
            handle.Pause();
            handle.FadeIn(0f);
            Assert.AreEqual(.8f, handle.Voice.Source.volume);
            handle.FadeOut(0f, stopWhenDone: false);
            Assert.IsTrue(handle.IsValid);
            Assert.IsTrue(handle.IsPaused);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            handle.FadeIn(0f);
            Assert.AreEqual(.8f, handle.Voice.Source.volume);
            handle.FadeOut(0f);
            Assert.IsFalse(handle.IsValid);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void InvalidDurationDoesNotReplaceTheCurrentFade(float duration)
        {
            var handle = Play(0);
            handle.FadeIn(2f);
            handle.Voice.TickFade(.5f, .5f);
            Assert.Throws<ArgumentOutOfRangeException>(() => handle.FadeIn(duration));
            Assert.Throws<ArgumentOutOfRangeException>(() => handle.FadeOut(duration));
            Assert.AreEqual(.2f, handle.Voice.Source.volume, .0001f);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.4f, handle.Voice.Source.volume, .0001f);
        }

        [Test]
        public void ZeroDurationOperationCountsAsAnExplicitEnvelopeAndLaterFadeInContinuesFromIt()
        {
            var handle = Play(0);
            handle.FadeIn(0f);
            handle.FadeIn(1f);
            Assert.AreEqual(.8f, handle.Voice.Source.volume, "An explicitly established full gain must not jump back to zero.");
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.8f, handle.Voice.Source.volume);
            handle.FadeOut(0f, stopWhenDone: false);
            handle.FadeIn(1f);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.4f, handle.Voice.Source.volume, .0001f);
        }

        [Test]
        public void DefaultAndExpiredHandlesDoNotValidateOrCreatePlaybackState()
        {
            AudioKit.Shutdown();
            var empty = default(AudioHandle);
            Assert.DoesNotThrow(() => empty.FadeIn(float.NaN));
            Assert.DoesNotThrow(() => empty.FadeOut(float.NaN));
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
            // Shutdown discarded the fixture's transient values; do not depend on the user's saved switches or volume.
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            AudioKit.Settings.SoundVolume.SetValueWithoutCallback(1f);
            var old = Play(0);
            old.FadeOut(5f);
            old.Stop();
            var current = Play(0);
            Assert.AreSame(old.Voice, current.Voice);
            Assert.DoesNotThrow(() => old.FadeIn(float.NaN));
            Assert.DoesNotThrow(() => old.FadeOut(0f));
            current.Voice.TickFade(10f, 10f);
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(.8f, current.Voice.Source.volume);
        }

        [Test]
        public void PendingDisabledRequestAndPauseFreezeClockThenResumeRemainingFade()
        {
            AudioKit.Settings.IsMusicOn.Value = false;
            var handle = Play(1);
            handle.FadeIn(2f);
            handle.Voice.TickFade(100f, 100f);
            Assert.AreEqual(0f, handle.Voice.Source.volume);
            AudioKit.Settings.IsMusicOn.Value = true;
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.2f, handle.Voice.Source.volume, .0001f);
            handle.Pause();
            handle.Voice.TickFade(100f, 100f);
            Assert.AreEqual(.2f, handle.Voice.Source.volume, .0001f);
            handle.Resume();
            AudioKit.Settings.IsMusicOn.Value = false;
            handle.Voice.TickFade(100f, 100f);
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(.2f, handle.Voice.Source.volume, .0001f);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(.2f, handle.Voice.Source.volume, .0001f);
            handle.Voice.TickFade(1.5f, 1.5f);
            Assert.AreEqual(.8f, handle.Voice.Source.volume, .0001f);
        }

        [Test]
        public void MuteDoesNotFreezeFadeAndSoundDisableCancelsIt()
        {
            var handle = Play(0);
            handle.FadeIn(2f);
            AudioKit.Settings.IsSoundMuted.Value = true;
            handle.Voice.TickFade(1f, 1f);
            Assert.IsTrue(handle.Voice.Source.mute);
            Assert.AreEqual(.4f, handle.Voice.Source.volume, .0001f);
            AudioKit.Settings.IsSoundMuted.Value = false;
            Assert.AreEqual(.4f, handle.Voice.Source.volume, .0001f);
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.IsFalse(handle.IsValid);
            AudioKit.Settings.IsSoundOn.Value = true;
            var current = Play(0);
            Assert.AreEqual(.8f, current.Voice.Source.volume);
            current.Voice.TickFade(5f, 5f);
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(.8f, current.Voice.Source.volume);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CallerChoosesUnscaledOrScaledClock(bool ignoreTimeScale)
        {
            var handle = Play(0);
            handle.FadeIn(1f, ignoreTimeScale);
            handle.Voice.TickFade(0f, .5f);
            Assert.AreEqual(ignoreTimeScale ? .4f : 0f, handle.Voice.Source.volume, .0001f);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(ignoreTimeScale ? .8f : .4f, handle.Voice.Source.volume, .0001f);
        }

        [TestCase(float.Epsilon)]
        [TestCase(float.MaxValue)]
        public void FiniteDurationBoundariesRemainFiniteAndReachTarget(float duration)
        {
            var handle = Play(0);
            handle.FadeIn(duration);
            handle.Voice.TickFade(duration, duration);
            Assert.AreEqual(.8f, handle.Voice.Source.volume, .0001f);
            Assert.IsFalse(float.IsNaN(handle.Voice.Source.volume));
            Assert.IsTrue(handle.IsValid);
        }

        [Test]
        public void EnvelopeMultipliesAfterOriginalBaseVolumeClamping()
        {
            AudioKit.Settings.SoundVolume.Value = .8f;
            var handle = Play(0, 3f);
            Assert.AreEqual(1f, handle.Voice.Source.volume);
            handle.FadeOut(2f, stopWhenDone: false);
            handle.Voice.TickFade(1f, 1f);
            Assert.AreEqual(.5f, handle.Voice.Source.volume, .0001f);
            Assert.AreEqual(3f, handle.Voice.Options.Volume);
        }

        [Test]
        public void SlotReplacementAndShutdownCannotCarryOldFadeIntoNewGeneration()
        {
            var old = Play(1);
            old.FadeOut(.1f);
            var current = Play(1);
            Assert.AreSame(old.Voice, current.Voice);
            old.FadeOut(0f);
            current.Voice.TickFade(5f, 5f);
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(.8f, current.Voice.Source.volume);
            AudioKit.Shutdown();
            Assert.IsFalse(current.IsValid);
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
            AudioKit.Settings.MusicVolume.SetValueWithoutCallback(1f);
            var fresh = Play(1);
            current.FadeOut(0f);
            Assert.IsTrue(fresh.IsValid);
            Assert.AreEqual(.8f, fresh.Voice.Source.volume);
        }

        [Test]
        public void NaturalCompletionReentryDoesNotTickNewRequestUsingOldSnapshot()
        {
            var callbacks = 0;
            AudioHandle current = default;
            var old = AudioKit.PlayMusic(_clip, loop: false, onFinished: (_, __) =>
            {
                callbacks++;
                current = AudioKit.PlayMusic(_clip);
                current.FadeIn(1f);
            });
            old.FadeOut(10f);
            old.Voice.Source.Stop();
            typeof(AudioManager).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(AudioManager.Instance, null);
            Assert.AreEqual(1, callbacks);
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(0f, current.Voice.Source.volume, "The old snapshot must not advance or stop the new generation's fade.");
        }

        [Test]
        public void FollowingTargetLossStopsRatherThanCompletesFadeAndRecyclingResetsEnvelope()
        {
            var callbacks = 0;
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            var old = AudioKit.PlaySoundFollowing(_clip, _target.transform, options, (_, __) => callbacks++);
            old.FadeOut(1f);
            _target.SetActive(false);
            old.Voice.CheckFinished();
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(0, callbacks);
            var current = Play(0);
            Assert.AreSame(old.Voice, current.Voice);
            Assert.AreEqual(.8f, current.Voice.Source.volume);
            current.FadeIn(1f);
            Assert.AreEqual(0f, current.Voice.Source.volume);
        }

        [Test]
        public void MixerRouteChangesDoNotReplaceOrResetRequestEnvelope()
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            Assert.IsTrue(mixer);
            var music = mixer.FindMatchingGroups("Music")[0];
            var ui = mixer.FindMatchingGroups("UI")[0];
            AudioKit.Settings.MusicOutputGroup.Value = music;
            var handle = Play(1);
            handle.FadeIn(2f);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.2f, handle.Voice.Source.volume, .0001f);
            AudioKit.Settings.MusicOutputGroup.Value = ui;
            Assert.AreSame(ui, handle.Voice.Source.outputAudioMixerGroup);
            Assert.AreEqual(.2f, handle.Voice.Source.volume, .0001f);
            AudioKit.ClearMixerRouting();
            Assert.IsNull(handle.Voice.Source.outputAudioMixerGroup);
            handle.Voice.TickFade(.5f, .5f);
            Assert.AreEqual(.4f, handle.Voice.Source.volume, .0001f);
        }
    }
}
