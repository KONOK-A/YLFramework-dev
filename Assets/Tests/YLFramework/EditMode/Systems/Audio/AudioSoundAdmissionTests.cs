using System;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Audio;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace YLFramework.Tests.Audio
{
    public class AudioSoundAdmissionTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;
        private AudioClip _other;
        private Random.State _randomState;

        [SetUp]
        public void SetUp()
        {
            _randomState = Random.state;
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("admission", 44100, 1, 44100, false);
            _other = AudioClip.Create("admission", 44100, 1, 44100, false);
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_clip);
            Object.DestroyImmediate(_other);
            Random.state = _randomState;
        }

        private static AudioSoundConcurrency Concurrency(int limit, SoundOverflowPolicy policy = SoundOverflowPolicy.RejectNew) =>
            new AudioSoundConcurrency { Limit = limit, OverflowPolicy = policy };

        private static AudioSoundOptions Loop()
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            return options;
        }

        [Test]
        public void DefaultIsSerializableUnlimitedRejectNewAndConfigurationDoesNotCreatePlaybackObjects()
        {
            AudioKit.Shutdown();
            var sources = Object.FindObjectsOfType<AudioSource>(true).Length;
            var listeners = Object.FindObjectsOfType<AudioListener>(true).Length;
            Assert.IsTrue(typeof(AudioSoundConcurrency).IsValueType);
            Assert.IsTrue(typeof(AudioSoundConcurrency).IsSerializable);
            Assert.AreEqual(0, (int)SoundOverflowPolicy.RejectNew);
            Assert.AreEqual(1, (int)SoundOverflowPolicy.StopOldest);
            Assert.AreEqual(2, (int)SoundOverflowPolicy.StopLowestPriority);
            var original = AudioKit.SoundConcurrency;
            Assert.AreEqual(0, original.Limit);
            Assert.AreEqual(0, original.DefaultClipLimit);
            Assert.AreEqual(SoundOverflowPolicy.RejectNew, original.OverflowPolicy);
            original.Validate();
            AudioKit.SoundConcurrency = Concurrency(int.MaxValue, SoundOverflowPolicy.StopOldest);
            var copy = AudioKit.SoundConcurrency;
            copy.Limit = 1;
            copy.OverflowPolicy = SoundOverflowPolicy.RejectNew;
            Assert.AreEqual(int.MaxValue, AudioKit.SoundConcurrency.Limit);
            Assert.AreEqual(SoundOverflowPolicy.StopOldest, AudioKit.SoundConcurrency.OverflowPolicy);
            Assert.IsNotNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(sources, Object.FindObjectsOfType<AudioSource>(true).Length);
            Assert.AreEqual(listeners, Object.FindObjectsOfType<AudioListener>(true).Length);
        }

        [TestCase(-1, 0, "Limit")]
        [TestCase(0, -1, "OverflowPolicy")]
        [TestCase(0, 3, "OverflowPolicy")]
        public void InvalidConfigurationIsRejectedBeforeInitializingSettingsOrHost(int limit, int policy, string parameter)
        {
            AudioKit.Shutdown();
            var invalid = Concurrency(limit, (SoundOverflowPolicy)policy);
            Assert.AreEqual(parameter, Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate()).ParamName);
            Assert.AreEqual(parameter, Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SoundConcurrency = invalid).ParamName);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [Test]
        public void InvalidRunningConfigurationLeavesBothFieldsAndEveryRequestUnchanged()
        {
            AudioKit.SoundConcurrency = Concurrency(2, SoundOverflowPolicy.StopOldest);
            var first = AudioKit.PlaySound(_clip, Loop());
            var second = AudioKit.PlaySound(_other, Loop());
            foreach (var invalid in new[] { Concurrency(-1), Concurrency(1, (SoundOverflowPolicy)7) })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SoundConcurrency = invalid);
                Assert.AreEqual(2, AudioKit.SoundConcurrency.Limit);
                Assert.AreEqual(SoundOverflowPolicy.StopOldest, AudioKit.SoundConcurrency.OverflowPolicy);
                Assert.IsTrue(first.IsValid);
                Assert.IsTrue(second.IsValid);
                Assert.AreEqual(2, AudioKit.SoundCount);
            }
        }

        [Test]
        public void ZeroIsUnlimitedAndMusicAndVoiceNeverConsumeSoundCapacity()
        {
            var music = AudioKit.PlayMusic(_clip);
            var voice = AudioKit.PlayVoice(_clip, true);
            var sounds = new AudioHandle[12];
            for (var i = 0; i < sounds.Length; i++)
            {
                sounds[i] = AudioKit.PlaySound(_clip, Loop());
            }

            Assert.AreEqual(12, AudioKit.SoundCount, "The idle pool size is not an active sound limit.");
            foreach (var sound in sounds)
            {
                Assert.IsTrue(sound.IsValid);
            }

            AudioKit.SoundConcurrency = Concurrency(1);
            Assert.IsTrue(sounds[0].IsValid);
            for (var i = 1; i < sounds.Length; i++)
            {
                Assert.IsFalse(sounds[i].IsValid);
            }

            Assert.IsTrue(music.IsValid);
            Assert.IsTrue(voice.IsValid);
            sounds[0].Stop();
            var admitted = AudioKit.PlaySound(_other, Loop());
            Assert.IsTrue(admitted.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames)]
        public void RejectNewDoesNotGrowSourcesNotifyOrRecordTheRejectedClip(AudioKit.PlaySoundModes mode)
        {
            AudioKit.SoundConcurrency = Concurrency(1);
            AudioKit.PlaySoundMode = mode;
            var first = AudioKit.PlaySound(_clip, Loop());
            first.Pause();
            var host = AudioManager.Instance;
            var sources = host.GetComponentsInChildren<AudioSource>(true).Length;
            var started = 0;
            var finished = 0;
            for (var i = 0; i < 8; i++)
            {
                Assert.IsFalse(AudioKit.PlaySound(_other, Loop(), (_, __) => finished++, (_, __) => started++).IsValid);
            }

            Assert.IsTrue(first.IsPaused);
            Assert.AreEqual(1, AudioKit.SoundCount);
            Assert.AreEqual(sources, host.GetComponentsInChildren<AudioSource>(true).Length);
            Assert.AreEqual(0, started);
            Assert.AreEqual(0, finished);
            Assert.IsTrue(AudioKit.Channels.CanPlay(_other.GetInstanceID(), mode, Time.frameCount));
            first.Stop();
            var admitted = AudioKit.PlaySound(_other, Loop(), (_, __) => finished++, (_, __) => started++);
            Assert.IsTrue(admitted.IsValid, "Different clips with the same name have independent deduplication records.");
            Assert.AreEqual(1, started);
            Assert.AreEqual(0, finished);
            Assert.AreEqual(sources, host.GetComponentsInChildren<AudioSource>(true).Length);
        }

        [Test]
        public void StopOldestUsesAdmissionOrderEvenWhenSameFrameSoundsArePausedMutedAndLooping()
        {
            AudioKit.SoundConcurrency = Concurrency(2, SoundOverflowPolicy.StopOldest);
            var finished = 0;
            var first = AudioKit.PlaySound(_clip, Loop(), (_, __) => finished++);
            first.Pause();
            AudioKit.Settings.IsSoundMuted.Value = true;
            var second = AudioKit.PlaySound(_other, Loop(), (_, __) => finished++);
            var third = AudioKit.PlaySound(_clip, Loop(), (_, __) => finished++);
            Assert.IsFalse(first.IsValid, "The oldest admitted request is evicted, not a HashSet-dependent candidate.");
            Assert.IsTrue(second.IsValid);
            Assert.IsTrue(third.IsValid);
            Assert.IsTrue(second.Voice.Source.mute);
            Assert.IsTrue(third.Voice.Source.loop);
            first.Stop();
            var fourth = AudioKit.PlaySound(_other, Loop());
            Assert.IsFalse(second.IsValid);
            Assert.IsTrue(third.IsValid);
            Assert.IsTrue(fourth.IsValid);
            Assert.AreEqual(2, AudioKit.SoundCount);
            Assert.AreEqual(0, finished);
        }

        [TestCase(SoundOverflowPolicy.RejectNew)]
        [TestCase(SoundOverflowPolicy.StopOldest)]
        public void LoweringLimitImmediatelyRetainsThePolicySpecifiedEndOfAdmissionOrder(SoundOverflowPolicy policy)
        {
            var sounds = new AudioHandle[5];
            var finished = 0;
            for (var i = 0; i < sounds.Length; i++)
            {
                sounds[i] = AudioKit.PlaySound(i % 2 == 0 ? _clip : _other, Loop(), (_, __) => finished++);
                sounds[i].Pause();
            }
            AudioKit.SoundConcurrency = Concurrency(2, policy);
            Assert.AreEqual(2, AudioKit.SoundCount);
            for (var i = 0; i < sounds.Length; i++)
            {
                var retained = policy == SoundOverflowPolicy.RejectNew ? i < 2 : i >= 3;
                Assert.AreEqual(retained, sounds[i].IsValid, "Admission index " + i);
                if (retained)
                {
                    Assert.IsTrue(sounds[i].IsPaused);
                }
            }
            Assert.AreEqual(0, finished);
        }

        [Test]
        public void RaisingRemovingOrChangingAFittingLimitDoesNotStopRequests()
        {
            AudioKit.SoundConcurrency = Concurrency(2);
            var first = AudioKit.PlaySound(_clip, Loop());
            var second = AudioKit.PlaySound(_other, Loop());
            foreach (var config in new[] { Concurrency(2, SoundOverflowPolicy.StopOldest), Concurrency(4), Concurrency(0) })
            {
                AudioKit.SoundConcurrency = config;
                Assert.IsTrue(first.IsValid);
                Assert.IsTrue(second.IsValid);
                Assert.AreEqual(2, AudioKit.SoundCount);
            }
            Assert.IsTrue(AudioKit.PlaySound(_clip, Loop()).IsValid);
            Assert.AreEqual(3, AudioKit.SoundCount);
        }

        [Test]
        public void AllFiveSoundEntrypointsUseTheSameCapacityGateEvenWithEveryOneOverride()
        {
            var target = new GameObject("Admission.Target");
            try
            {
                AudioKit.SoundConcurrency = Concurrency(1);
                AudioKit.SetClipSoundLimit(_other, 1);
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
                var options = Loop();
                options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
                options.Priority = 17;
                var first = AudioKit.PlaySound(_clip, options);
                var started = 0;
                Action<AudioClip, AudioHandle> callback = (_, __) => started++;
                for (var entry = 0; entry < 5; entry++)
                {
                    Assert.IsFalse(PlayEntry(entry, target.transform, options, callback).IsValid);
                }

                Assert.AreEqual(0, started);
                Assert.IsTrue(first.IsValid);
                Assert.AreEqual(1, AudioKit.SoundCount);
                first.Stop();
                for (var entry = 0; entry < 5; entry++)
                {
                    var admitted = PlayEntry(entry, target.transform, options, callback);
                    Assert.IsTrue(admitted.IsValid);
                    Assert.AreEqual(1, AudioKit.SoundCount);
                    Assert.AreEqual(1, AudioKit.GetSoundCount(_other));
                    Assert.AreEqual(17, admitted.Voice.Priority);
                    admitted.Stop();
                }
                Assert.AreEqual(5, started);
            }
            finally
            {
                Object.DestroyImmediate(target);
            }
        }

        private AudioHandle PlayEntry(int entry, Transform target, AudioSoundOptions options, Action<AudioClip, AudioHandle> started)
        {
            switch (entry)
            {
                case 0:
                    return AudioKit.PlaySound(_other, options, onStarted: started);
                case 1:
                    return AudioKit.PlaySoundAt(_other, Vector3.one, options, onStarted: started);
                case 2:
                    return AudioKit.PlaySoundFollowing(_other, target, options, onStarted: started);
                case 3:
                    return AudioKit.PlayRandomSound(new[] { null, _other }, options, onStarted: started);
                default:
                    return AudioKit.PlayWeightedRandomSound(new[] { new AudioWeightedClip(_other) }, options, onStarted: started);
            }
        }

        [Test]
        public void InvalidRequestsAreValidatedBeforeCapacityAndCannotStealExistingSounds()
        {
            AudioKit.SoundConcurrency = Concurrency(1, SoundOverflowPolicy.StopOldest);
            var first = AudioKit.PlaySound(_clip, Loop());
            var invalid = Loop();
            invalid.Pitch = float.NaN;
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySound(_other, invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySoundAt(_other, new Vector3(float.PositiveInfinity, 0f, 0f)));
            Assert.IsFalse(AudioKit.PlaySound(null).IsValid);
            Assert.IsFalse(AudioKit.PlaySoundFollowing(_other, null).IsValid);
            AudioKit.SoundConcurrency = Concurrency(1);
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySound(_other, invalid));
            Assert.IsTrue(first.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [Test]
        public void DeduplicationRejectionHappensBeforeStopOldestChoosesAVictim()
        {
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
            AudioKit.SoundConcurrency = Concurrency(1, SoundOverflowPolicy.StopOldest);
            var first = AudioKit.PlaySound(_clip, Loop());
            Assert.IsFalse(AudioKit.PlaySound(_clip, Loop()).IsValid);
            Assert.IsTrue(first.IsValid);
            var different = AudioKit.PlaySound(_other, Loop());
            Assert.IsFalse(first.IsValid);
            Assert.IsTrue(different.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CapacityRejectedRandomPlaybackSamplesExactlyOnceAndNeverRerolls(bool weighted)
        {
            AudioKit.SoundConcurrency = Concurrency(1);
            var first = AudioKit.PlaySound(_clip, Loop());
            var clips = new[] { _clip, null, _other };
            var entries = new[] { new AudioWeightedClip(_clip, 1f), new AudioWeightedClip(null, 10f), new AudioWeightedClip(_other, 3f) };
            Random.InitState(713);
            var before = Random.state;
            AudioClip selected;
            if (weighted)
            {
                Assert.IsTrue(AudioKit.TrySelectWeightedRandomClip(entries, out selected));
            }
            else
            {
                Assert.IsTrue(AudioKit.TrySelectRandomClip(clips, out selected));
            }

            var afterOneSelection = Random.state;
            Random.state = before;
            var started = 0;
            var rejected = weighted
                ? AudioKit.PlayWeightedRandomSound(entries, Loop(), onStarted: (_, __) => started++)
                : AudioKit.PlayRandomSound(clips, Loop(), onStarted: (_, __) => started++);
            Assert.IsFalse(rejected.IsValid);
            Assert.AreEqual(afterOneSelection, Random.state);
            Assert.AreEqual(0, started);
            Assert.IsTrue(first.IsValid);
            first.Stop();
            Random.state = before;
            var admitted = weighted ? AudioKit.PlayWeightedRandomSound(entries, Loop()) : AudioKit.PlayRandomSound(clips, Loop());
            Assert.IsTrue(admitted.IsValid);
            Assert.AreSame(selected, admitted.Voice.Source.clip);
            Assert.AreEqual(afterOneSelection, Random.state);
        }

        [Test]
        public void ExplicitStopFadeStopStopAllAndSoundOffReleaseCapacityWithoutNaturalNotification()
        {
            AudioKit.SoundConcurrency = Concurrency(1);
            var finished = 0;
            var first = AudioKit.PlaySound(_clip, Loop(), (_, __) => finished++);
            first.Stop();
            Assert.AreEqual(0, AudioKit.SoundCount);
            var faded = AudioKit.PlaySound(_clip, Loop(), (_, __) => finished++);
            faded.FadeOut(0f);
            Assert.IsFalse(faded.IsValid);
            Assert.AreEqual(0, AudioKit.SoundCount);
            var stopped = AudioKit.PlaySound(_clip, Loop(), (_, __) => finished++);
            AudioKit.StopAllSound();
            Assert.IsFalse(stopped.IsValid);
            Assert.AreEqual(0, AudioKit.SoundCount);
            var disabled = AudioKit.PlaySound(_clip, Loop(), (_, __) => finished++);
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.IsFalse(disabled.IsValid);
            Assert.AreEqual(0, AudioKit.SoundCount);
            AudioKit.Settings.IsSoundOn.Value = true;
            Assert.IsTrue(AudioKit.PlaySound(_other, Loop()).IsValid);
            Assert.AreEqual(0, finished);
        }

        [Test]
        public void ShutdownResetsConfigurationAndOldLeaseAndSettingsCannotAffectTheNewSession()
        {
            AudioKit.SoundConcurrency = Concurrency(1, SoundOverflowPolicy.StopOldest);
            var oldSettings = AudioKit.Settings;
            var old = AudioKit.PlaySound(_clip, Loop());
            AudioKit.Shutdown();
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(default(AudioSoundConcurrency), AudioKit.SoundConcurrency);
            Assert.IsFalse(AudioManager.HasInstance);
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            AudioKit.SoundConcurrency = Concurrency(2);
            var first = AudioKit.PlaySound(_clip, Loop());
            var second = AudioKit.PlaySound(_other, Loop());
            oldSettings.IsSoundOn.Value = false;
            old.Stop();
            Assert.IsTrue(first.IsValid);
            Assert.IsTrue(second.IsValid);
            Assert.AreEqual(2, AudioKit.SoundCount);
            Assert.AreEqual(2, AudioKit.SoundConcurrency.Limit);
        }

        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames)]
        public void RollbackRequiresBothVoiceIdentityAndGenerationAndPreservesOtherRecords(AudioKit.PlaySoundModes mode)
        {
            var deduplicator = new SoundDeduplicator();
            var first = AudioKit.PlaySound(_clip, Loop());
            var other = AudioKit.PlaySound(_other, Loop());
            Assert.AreEqual(first.Generation, other.Generation);
            Assert.AreNotSame(first.Voice, other.Voice);
            deduplicator.Record(1, mode, 100, first);
            deduplicator.Record(2, mode, 100, other);
            deduplicator.Rollback(1, mode, other);
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 100));
            first.Stop();
            var current = AudioKit.PlaySound(_clip, Loop());
            Assert.AreSame(first.Voice, current.Voice);
            Assert.AreNotEqual(first.Generation, current.Generation);
            deduplicator.Record(1, mode, 100, current);
            deduplicator.Rollback(1, mode, first);
            Assert.IsFalse(deduplicator.CanPlay(1, mode, 100), "An old operation cannot erase a nested owner's replacement record.");
            deduplicator.Rollback(1, mode, current);
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 100));
            deduplicator.Rollback(2, AudioKit.PlaySoundModes.EveryOne, other);
            Assert.IsFalse(deduplicator.CanPlay(2, mode, 100));
            deduplicator.Finished(2, other);
            Assert.AreEqual(mode == AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames, deduplicator.CanPlay(2, mode, 100));
        }

        [Test]
        public void GlobalRollbackDoesNotResetTheInclusiveSharedWindowOrMakeFinishClearIt()
        {
            var deduplicator = new SoundDeduplicator { GlobalFrameCountForIgnoreSameSound = 10 };
            var mode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
            deduplicator.Record(1, mode, 100, default);
            deduplicator.Record(2, mode, 109, default);
            deduplicator.Rollback(1, mode, default);
            Assert.IsTrue(deduplicator.CanPlay(1, mode, 110));
            deduplicator.Finished(2, default);
            Assert.IsFalse(deduplicator.CanPlay(2, mode, 110));
            deduplicator.Record(3, mode, 110, default);
            Assert.IsFalse(deduplicator.CanPlay(3, mode, 110));
            Assert.IsTrue(deduplicator.CanPlay(2, mode, 111));
            Assert.IsTrue(deduplicator.CanPlay(3, mode, 111), "Rollback must not move the original frame 100 window anchor.");
            deduplicator.Record(3, mode, 111, default);
            Assert.IsTrue(deduplicator.CanPlay(2, mode, 111));
            Assert.IsFalse(deduplicator.CanPlay(3, mode, 111));
        }
    }
}
