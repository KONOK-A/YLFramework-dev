using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Actions;
using YLFramework.Audio;
using Kit = YLFramework.Actions.ActionKit;

namespace YLFramework.Tests.Audio
{
    public class PlaySoundActionTests
    {
        private readonly List<IAction> _actions = new List<IAction>();
        private AudioTestSettings _settings;
        private AudioClip _clip;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("action", 44100, 1, 44100, false);
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
            _settings.Dispose();
            Kit.Shutdown();
            UnityEngine.Object.DestroyImmediate(_clip);
        }

        private T Own<T>(T action) where T : IAction
        {
            _actions.Add(action);
            return action;
        }

        private static AudioHandle OnlySound()
        {
            var sounds = AudioKit.GetPlayingSounds();
            Assert.That(sounds, Has.Length.EqualTo(1));
            return sounds[0];
        }

        [Test]
        public void ClipAndInheritedAllocationUseTheRealActionContract()
        {
            var byClip = Own(PlaySoundAction.Allocate(_clip));
            var inherited = Own(PlaySoundAction.Allocate());
            Assert.That(byClip, Is.InstanceOf<AbstractAction<PlaySoundAction>>());
            Assert.AreNotEqual(byClip.ActionId, inherited.ActionId);
            Assert.AreEqual(ActionStatus.NotStart, inherited.Status);
            Assert.IsFalse(inherited.IsDeinitialized);
            Assert.AreEqual(0, AudioKit.SoundCount, "Allocation alone must not play.");
        }

        [Test]
        public void CancellationStopsOnlyItsPlaybackAndDoesNotNotifyCompletion()
        {
            var finishes = 0;
            var action = Own(PlaySoundAction.Allocate(_clip, () => finishes++));
            Assert.IsFalse(action.Execute(0));
            var sound = OnlySound();
            sound.Pause();
            action.Deinit();
            Assert.IsFalse(sound.IsValid);
            Assert.IsFalse(action.Execute(0));
            Assert.AreEqual(0, finishes);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidClipOrDisabledSoundCompletesExactlyOnce(bool disabled)
        {
            AudioKit.Settings.IsSoundOn.Value = !disabled;
            var finishes = 0;
            var action = Own(PlaySoundAction.Allocate(disabled ? _clip : null, () => finishes++));
            Assert.IsTrue(action.Execute(0));
            Assert.IsTrue(action.Execute(0));
            Assert.AreEqual(1, finishes);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void DeduplicationRejectionEndsWaiting()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            var existing = AudioKit.PlaySound(_clip);
            existing.Pause();
            var finishes = 0;
            var action = Own(PlaySoundAction.Allocate(_clip, () => finishes++));
            Assert.IsTrue(action.Execute(0));
            Assert.AreEqual(1, finishes);
            action.Deinit();
            Assert.IsTrue(existing.IsValid);
        }

        [Test]
        public void ResetStopsOldHandleRetainsClipAndDoesNotNotifyCancellation()
        {
            var finishes = 0;
            var action = Own(PlaySoundAction.Allocate(_clip, () => finishes++));
            action.Execute(0);
            var old = OnlySound();
            old.Pause();
            action.Reset();
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(ActionStatus.NotStart, action.Status);
            Assert.IsFalse(action.Execute(0));
            var current = OnlySound();
            current.Pause();
            Assert.AreSame(_clip, current.Voice.Source.clip);
            Assert.AreSame(old.Voice, current.Voice);
            Assert.AreNotEqual(old.Generation, current.Generation);
            old.Stop();
            Assert.IsFalse(action.Execute(0));
            Assert.AreEqual(0, finishes);
            current.Stop();
            Assert.IsTrue(action.Execute(0));
            Assert.AreEqual(1, finishes);
        }

        [Test]
        public void ExternalStopCompletesActionWithoutNaturalAudioCallback()
        {
            var finishes = 0;
            var action = Own(PlaySoundAction.Allocate(_clip, () => finishes++));
            action.Execute(0);
            OnlySound().Stop();
            Assert.IsTrue(action.Execute(0));
            Assert.IsTrue(action.Execute(0));
            Assert.AreEqual(1, finishes);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OldActionCannotWaitForOrStopNewGeneration(bool cancel)
        {
            var action = Own(PlaySoundAction.Allocate(_clip));
            action.Execute(0);
            var old = OnlySound();
            old.Stop();
            var current = AudioKit.PlaySound(_clip);
            current.Pause();
            Assert.AreSame(old.Voice, current.Voice, "Exercise actual pool reuse.");
            Assert.AreNotEqual(old.Generation, current.Generation);
            if (!cancel)
            {
                Assert.IsTrue(action.Execute(0));
            }

            action.Deinit();
            Assert.IsTrue(current.IsValid);
            Assert.IsTrue(current.IsPaused);
        }

        [Test]
        public void CompletionCallbackCanStartNewAudioWithoutOldActionCancellingIt()
        {
            var next = default(AudioHandle);
            var finishes = 0;
            var action = Own(PlaySoundAction.Allocate(_clip, () =>
            {
                finishes++;
                next = AudioKit.PlaySound(_clip);
                next.Pause();
            }));
            action.Execute(0);
            OnlySound().Stop();
            Assert.IsTrue(action.Execute(0));
            action.Deinit();
            Assert.AreEqual(1, finishes);
            Assert.IsTrue(next.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [Test]
        public void ClipExtensionAppendsToExistingSequenceAndContinuesAfterRejection()
        {
            var steps = 0;
            var sequence = Own(Kit.Sequence());
            Assert.AreSame(sequence, sequence.PlaySound((AudioClip)null));
            sequence.Append(Kit.Callback(() => steps++));
            Assert.IsTrue(sequence.Execute(0));
            Assert.AreEqual(1, steps);
        }

        [Test]
        public void CompletionResetReentryKeepsNewExecutionAlive()
        {
            var finishes = 0;
            PlaySoundAction action = null;
            action = Own(PlaySoundAction.Allocate(_clip, () =>
            {
                finishes++;
                if (finishes != 1)
                {
                    return;
                }

                action.Reset();
                Assert.IsFalse(action.Execute(0));
                OnlySound().Pause();
            }));
            action.Execute(0);
            OnlySound().Stop();
            Assert.IsFalse(action.Execute(0), "The expired execution must not finish the restarted action.");
            Assert.AreEqual(ActionStatus.Started, action.Status);
            Assert.AreEqual(1, finishes);
            Assert.IsTrue(OnlySound().IsValid);
            OnlySound().Stop();
            Assert.IsTrue(action.Execute(0));
            Assert.AreEqual(2, finishes);
        }

        [Test]
        public void PausingActionDoesNotPauseItsSound()
        {
            var action = Own(PlaySoundAction.Allocate(_clip));
            action.Execute(0);
            action.IsPaused = true;
            Assert.IsFalse(OnlySound().IsPaused);
            Assert.IsFalse(action.Execute(1));
            OnlySound().Pause();
            Assert.IsTrue(OnlySound().IsValid);
        }
    }
}
