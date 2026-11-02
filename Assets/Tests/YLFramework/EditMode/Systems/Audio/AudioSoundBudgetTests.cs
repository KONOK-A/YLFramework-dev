using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Pooling;
using YLFramework.Audio;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace YLFramework.Tests.Audio
{
    public class AudioSoundBudgetTests
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
            _clip = AudioClip.Create("budget-same-name", 44100, 1, 44100, false);
            _other = AudioClip.Create("budget-same-name", 44100, 1, 44100, false);
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            if (_clip)
            {
                Object.DestroyImmediate(_clip);
            }

            if (_other)
            {
                Object.DestroyImmediate(_other);
            }

            Random.state = _randomState;
        }

        private static AudioSoundOptions Options(int priority = 0)
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            options.Priority = priority;
            return options;
        }

        private static AudioSoundConcurrency Concurrency(int global, int clip, SoundOverflowPolicy policy) =>
            new AudioSoundConcurrency { Limit = global, DefaultClipLimit = clip, OverflowPolicy = policy };

        private static IEnumerable<TestCaseData> AdmissionCases()
        {
            foreach (SoundOverflowPolicy policy in Enum.GetValues(typeof(SoundOverflowPolicy)))
            {
                for (var scope = 0; scope < 3; scope++)
                {
                    foreach (var priority in new[] { 1, 10, int.MaxValue })
                    {
                        yield return new TestCaseData(policy, scope, priority)
                            .SetName("JointAdmission_" + policy + "_" + new[] { "Global", "Clip", "Both" }[scope] + "_Priority" + priority);
                    }
                }
            }
        }

        [TestCaseSource(nameof(AdmissionCases))]
        public void ThreePoliciesUseTheJointVictimScopeAndStopAtMostOneRequest(SoundOverflowPolicy policy, int scope, int priority)
        {
            AudioKit.SoundConcurrency = Concurrency(scope == 1 ? 0 : 3, scope == 0 ? 0 : 2, policy);
            var finished = 0;
            var other = AudioKit.PlaySound(_other, Options(10), (_, __) => finished++);
            var oldestClip = AudioKit.PlaySound(_clip, Options(10), (_, __) => finished++);
            var newestClip = AudioKit.PlaySound(_clip, Options(20), (_, __) => finished++);
            other.Pause();
            oldestClip.Pause();
            newestClip.Pause();
            AudioKit.Settings.IsSoundMuted.Value = true;
            oldestClip.SetVolumeScale(.3f);
            oldestClip.SetPitch(2f);
            Assert.AreEqual(10, oldestClip.Voice.Priority);
            Assert.AreEqual(_clip.GetInstanceID(), oldestClip.Voice.ClipId);
            AudioKit.ResetSoundStats();
            var started = 0;
            var incoming = AudioKit.PlaySound(_clip, Options(priority), onStarted: (_, handle) =>
            {
                started++;
                Assert.AreEqual(3, AudioKit.SoundCount);
                Assert.AreEqual(scope == 0 ? 3 : 2, AudioKit.GetSoundCount(_clip));
                Assert.AreEqual(priority, handle.Voice.Priority);
                Assert.AreEqual(1L, AudioKit.SoundStats.AdmittedCount);
            });
            var accepted = policy != SoundOverflowPolicy.RejectNew &&
                (policy != SoundOverflowPolicy.StopLowestPriority || priority >= 10);
            Assert.AreEqual(accepted, incoming.IsValid);
            Assert.AreEqual(accepted ? 1 : 0, started);
            Assert.AreEqual(!accepted || scope != 0, other.IsValid, "A clip-limited admission must never stop a different clip.");
            Assert.AreEqual(!accepted || scope == 0, oldestClip.IsValid);
            Assert.IsTrue(newestClip.IsValid);
            Assert.AreEqual(3, AudioKit.SoundCount);
            Assert.AreEqual(scope == 0 && accepted ? 3 : 2, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(scope == 0 && accepted ? 0 : 1, AudioKit.GetSoundCount(_other));
            Assert.AreEqual(AudioKit.GetSoundCount(_clip), AudioManager.Instance.GetSoundCount(_clip.GetInstanceID()));
            Assert.AreEqual(0, finished);
            var stats = AudioKit.SoundStats;
            Assert.AreEqual(accepted ? 1L : 0L, stats.AdmittedCount);
            Assert.AreEqual(accepted ? 1L : 0L, stats.StolenCount);
            Assert.AreEqual(!accepted && policy == SoundOverflowPolicy.RejectNew && scope == 0 ? 1L : 0L, stats.GlobalRejectedCount);
            Assert.AreEqual(!accepted && policy == SoundOverflowPolicy.RejectNew && scope != 0 ? 1L : 0L, stats.ClipRejectedCount);
            Assert.AreEqual(!accepted && policy == SoundOverflowPolicy.StopLowestPriority ? 1L : 0L, stats.PriorityRejectedCount);
            Assert.AreEqual(0L, stats.DedupRejectedCount);
            Assert.AreEqual(0L, stats.TrimmedCount);
            Assert.AreEqual(3, stats.ActiveCount);
            Assert.AreEqual(3, stats.PeakActiveCount);
        }

        [Test]
        public void LowestPriorityChoosesPriorityBeforeAgeAndUsesRelationalComparisonAtIntegerMaximum()
        {
            AudioKit.SoundConcurrency = Concurrency(3, 0, SoundOverflowPolicy.StopLowestPriority);
            var oldest = AudioKit.PlaySound(_other, Options(int.MaxValue));
            var low = AudioKit.PlaySound(_clip, Options(0));
            var equalLater = AudioKit.PlaySound(_clip, Options(0));
            var incoming = AudioKit.PlaySound(_other, Options(int.MaxValue));
            Assert.IsTrue(oldest.IsValid);
            Assert.IsFalse(low.IsValid);
            Assert.IsTrue(equalLater.IsValid);
            Assert.IsTrue(incoming.IsValid);
            equalLater.Stop();
            var maximum = AudioKit.PlaySound(_clip, Options(int.MaxValue));
            Assert.IsFalse(AudioKit.PlaySound(_clip, Options(0)).IsValid);
            Assert.IsTrue(oldest.IsValid);
            Assert.IsTrue(maximum.IsValid);
            var replacement = AudioKit.PlaySound(_clip, Options(int.MaxValue));
            Assert.IsFalse(oldest.IsValid, "Equal highest priority replaces the oldest, without arithmetic overflow.");
            Assert.IsTrue(replacement.IsValid);
        }

        [TestCase(SoundOverflowPolicy.RejectNew)]
        [TestCase(SoundOverflowPolicy.StopOldest)]
        [TestCase(SoundOverflowPolicy.StopLowestPriority)]
        public void DefaultAndGlobalShrinkTrimClipFirstWithTheMinimumNumberOfStops(SoundOverflowPolicy policy)
        {
            var other = AudioKit.PlaySound(_other, Options(0));
            var first = AudioKit.PlaySound(_clip, Options(1));
            var second = AudioKit.PlaySound(_clip, Options(20));
            var third = AudioKit.PlaySound(_clip, Options(10));
            AudioKit.ResetSoundStats();
            AudioKit.SoundConcurrency = Concurrency(2, 1, policy);
            Assert.IsTrue(other.IsValid, "Trimming global first would unnecessarily stop this already-fitting clip.");
            Assert.AreEqual(policy == SoundOverflowPolicy.RejectNew, first.IsValid);
            Assert.AreEqual(policy == SoundOverflowPolicy.StopLowestPriority, second.IsValid);
            Assert.AreEqual(policy == SoundOverflowPolicy.StopOldest, third.IsValid);
            Assert.AreEqual(2, AudioKit.SoundCount);
            Assert.AreEqual(1, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(2L, AudioKit.SoundStats.TrimmedCount);
            Assert.AreEqual(0L, AudioKit.SoundStats.StolenCount);
        }

        [Test]
        public void PriorityShrinkKeepsHighPriorityAndNewerTiesEvenWhenPausedMutedAndLooping()
        {
            var oldest = AudioKit.PlaySound(_clip, Options(10));
            var lowest = AudioKit.PlaySound(_other, Options(0));
            var newest = AudioKit.PlaySound(_clip, Options(10));
            oldest.Pause();
            lowest.Pause();
            newest.Pause();
            AudioKit.Settings.IsSoundMuted.Value = true;
            AudioKit.SoundConcurrency = Concurrency(1, 0, SoundOverflowPolicy.StopLowestPriority);
            Assert.IsFalse(oldest.IsValid);
            Assert.IsFalse(lowest.IsValid);
            Assert.IsTrue(newest.IsPaused);
            Assert.IsTrue(newest.Voice.Source.mute);
            Assert.IsTrue(newest.Voice.Source.loop);
            Assert.AreEqual(2L, AudioKit.SoundStats.TrimmedCount);
        }

        [Test]
        public void ClipOverrideZeroIsDistinctFromAbsentAndRemoveAndClearImmediatelyRestoreDefault()
        {
            AudioKit.SoundConcurrency = Concurrency(0, 1, SoundOverflowPolicy.RejectNew);
            AudioKit.SetClipSoundLimit(_clip, 0);
            Assert.AreEqual(0, AudioKit.GetEffectiveClipSoundLimit(_clip));
            Assert.AreEqual(1, AudioKit.GetEffectiveClipSoundLimit(_other));
            var first = AudioKit.PlaySound(_clip, Options());
            var second = AudioKit.PlaySoundAt(_clip, Vector3.one, Options());
            var other = AudioKit.PlaySound(_other, Options());
            Assert.AreEqual(2, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(1, AudioKit.GetSoundCount(_other), "Same names must not share rules or counts.");
            Assert.IsTrue(AudioKit.RemoveClipSoundLimit(_clip));
            Assert.IsFalse(AudioKit.RemoveClipSoundLimit(_clip));
            Assert.AreEqual(1, AudioKit.GetEffectiveClipSoundLimit(_clip));
            Assert.IsTrue(first.IsValid);
            Assert.IsFalse(second.IsValid);
            Assert.IsTrue(other.IsValid);
            AudioKit.SetClipSoundLimit(_clip, 0);
            var third = AudioKit.PlaySound(_clip, Options());
            AudioKit.ClearClipSoundLimits();
            Assert.IsFalse(third.IsValid);
            Assert.AreEqual(1, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(2L, AudioKit.SoundStats.TrimmedCount);
            AudioKit.SetClipSoundLimit(_other, 0);
            AudioKit.SoundConcurrency = Concurrency(2, 1, SoundOverflowPolicy.RejectNew);
            Assert.IsFalse(AudioKit.PlaySound(_other, Options()).IsValid, "Explicit unlimited clip still obeys global capacity.");
        }

        [Test]
        public void ClipRuleChangesArePassiveAndDestroyedReferencesCanBeRemovedWithoutAffectingOtherRules()
        {
            AudioKit.Shutdown();
            var sources = Object.FindObjectsOfType<AudioSource>(true).Length;
            AudioKit.SetClipSoundLimit(_clip, 2);
            AudioKit.SetClipSoundLimit(_other, 3);
            Assert.AreEqual(2, AudioKit.GetEffectiveClipSoundLimit(_clip));
            Assert.AreEqual(3, AudioKit.GetEffectiveClipSoundLimit(_other));
            Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
            Object.DestroyImmediate(_clip);
            Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(0, AudioKit.GetEffectiveClipSoundLimit(_clip));
            AudioKit.RemoveClipSoundLimit(_clip);
            Assert.IsFalse(AudioKit.RemoveClipSoundLimit(_clip));
            Assert.AreEqual(3, AudioKit.GetEffectiveClipSoundLimit(_other));
            AudioKit.ClearClipSoundLimits();
            Assert.AreEqual(0, AudioKit.GetEffectiveClipSoundLimit(_other));
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(sources, Object.FindObjectsOfType<AudioSource>(true).Length);
        }

        [Test]
        public void ReusedRuleKeysCannotApplyOrRemoveADifferentManagedClipInstance()
        {
            AudioKit.Shutdown();
            AudioKit.SetClipSoundLimit(_other, 3);
            // Deterministically simulate native ID reuse without requiring Unity to recycle an ID.
            var dictionary = typeof(AudioKit).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Single(field =>
                field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                field.FieldType.GetGenericArguments()[0] == typeof(int) &&
                field.FieldType.GetGenericArguments()[1].GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(member => member.FieldType == typeof(AudioClip)));
            var rules = (System.Collections.IDictionary)dictionary.GetValue(null);
            var rule = rules[_other.GetInstanceID()];
            rules.Remove(_other.GetInstanceID());
            rules[_clip.GetInstanceID()] = rule;
            Assert.AreEqual(0, AudioKit.GetEffectiveClipSoundLimit(_clip), "An ID match alone must not apply another live instance's rule.");
            Assert.IsFalse(AudioKit.RemoveClipSoundLimit(_clip), "An old reference cannot delete a rule belonging to a different instance.");
            Assert.IsTrue(rules.Contains(_clip.GetInstanceID()));
            rules.Remove(_clip.GetInstanceID());
            rules[_other.GetInstanceID()] = rule;
            Assert.AreEqual(3, AudioKit.GetEffectiveClipSoundLimit(_other));
            Assert.IsTrue(AudioKit.RemoveClipSoundLimit(_other));
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void ColdQueriesResetAndZeroPrewarmDoNotInitializeSettingsHostOrRandom()
        {
            AudioKit.Shutdown();
            var sources = Object.FindObjectsOfType<AudioSource>(true).Length;
            var listeners = Object.FindObjectsOfType<AudioListener>(true).Length;
            var state = Random.state;
            Assert.AreEqual(10, AudioKit.SoundCacheCapacity);
            Assert.AreEqual(default(AudioSoundStats), AudioKit.SoundStats);
            Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(0, AudioKit.GetSoundCount(null));
            Assert.AreEqual(0, AudioKit.GetEffectiveClipSoundLimit(null));
            Assert.AreEqual(0, AudioKit.GetEffectiveClipSoundLimit(_clip));
            Assert.IsFalse(AudioKit.RemoveClipSoundLimit(null));
            AudioKit.ClearClipSoundLimits();
            AudioKit.ResetSoundStats();
            Assert.AreEqual(0, AudioKit.PrewarmSounds(0));
            foreach (var capacity in new[] { 0, 10, 24 })
            {
                AudioKit.SoundCacheCapacity = capacity;
            }

            Assert.AreEqual(default(AudioSoundStats), AudioKit.SoundStats);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(sources, Object.FindObjectsOfType<AudioSource>(true).Length);
            Assert.AreEqual(listeners, Object.FindObjectsOfType<AudioListener>(true).Length);
            Assert.AreEqual(state, Random.state);
        }

        [Test]
        public void NegativeBudgetsPriorityAndUnknownPolicyFailBeforeAnyColdSideEffects()
        {
            AudioKit.Shutdown();
            var config = Concurrency(0, -1, SoundOverflowPolicy.RejectNew);
            Assert.AreEqual("DefaultClipLimit", Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SoundConcurrency = config).ParamName);
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SoundCacheCapacity = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PrewarmSounds(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PrewarmSounds(11));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SetClipSoundLimit(_clip, -1));
            Assert.That(() => AudioKit.SetClipSoundLimit(null, 1), Throws.InstanceOf<ArgumentException>());
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SoundConcurrency = Concurrency(0, 0, (SoundOverflowPolicy)3));
            var invalid = Options(-1);
            Assert.AreEqual("Priority", Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySound(_clip, invalid)).ParamName);
            Assert.AreEqual(default(AudioSoundStats), AudioKit.SoundStats);
            Assert.AreEqual(10, AudioKit.SoundCacheCapacity);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.AreEqual(default(AudioSoundConcurrency), AudioKit.SoundConcurrency);
        }

        [Test]
        public void InvalidLiveChangesLeaveRulesCacheRequestsAndStatisticsUnchanged()
        {
            AudioKit.SoundCacheCapacity = 3;
            AudioKit.PrewarmSounds(3);
            AudioKit.SoundConcurrency = Concurrency(2, 2, SoundOverflowPolicy.StopLowestPriority);
            AudioKit.SetClipSoundLimit(_clip, 2);
            var first = AudioKit.PlaySound(_clip, Options(10));
            var second = AudioKit.PlaySound(_other, Options(10));
            var before = AudioKit.SoundStats;
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SoundCacheCapacity = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PrewarmSounds(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SetClipSoundLimit(_clip, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.SoundConcurrency = Concurrency(1, -1, SoundOverflowPolicy.StopOldest));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySound(_clip, Options(-1)));
            Assert.AreEqual(before, AudioKit.SoundStats);
            Assert.AreEqual(3, AudioKit.SoundCacheCapacity);
            Assert.AreEqual(2, AudioKit.SoundConcurrency.Limit);
            Assert.AreEqual(2, AudioKit.GetEffectiveClipSoundLimit(_clip));
            Assert.IsTrue(first.IsValid);
            Assert.IsTrue(second.IsValid);
        }

        [Test]
        public void MissingSerializedFieldsKeepOldValuesAndNewPriorityAndClipLimitAtZero()
        {
            var concurrency = JsonUtility.FromJson<AudioSoundConcurrency>("{\"Limit\":4,\"OverflowPolicy\":1}");
            Assert.AreEqual(4, concurrency.Limit);
            Assert.AreEqual(SoundOverflowPolicy.StopOldest, concurrency.OverflowPolicy);
            Assert.AreEqual(0, concurrency.DefaultClipLimit);
            var options = JsonUtility.FromJson<AudioSoundOptions>("{\"Loop\":true,\"Volume\":0.6,\"Pitch\":1.5}");
            Assert.IsTrue(options.Loop);
            Assert.AreEqual(.6f, options.Volume);
            Assert.AreEqual(1.5f, options.Pitch);
            Assert.AreEqual(0, options.Priority);
            var root = new GameObject("Budget.OldEmitter");
            try
            {
                var emitter = root.AddComponent<AudioEmitter>();
                JsonUtility.FromJsonOverwrite("{\"volume\":0.6,\"loop\":true}", emitter);
                var serialized = new UnityEditor.SerializedObject(emitter);
                Assert.AreEqual(0, serialized.FindProperty("priority").intValue);
                Assert.AreEqual(.6f, serialized.FindProperty("volume").floatValue);
                Assert.IsTrue(serialized.FindProperty("loop").boolValue);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames, 0)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames, 10)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames, 0)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames, 10)]
        public void DedupHasInclusiveBoundaryAndRejectedChecksNeverExtendTheWindow(AudioKit.PlaySoundModes mode, int frames)
        {
            AudioKit.GlobalFrameCountForIgnoreSameSound = frames;
            AudioKit.SoundFrameCountForIgnoreSameSound = frames;
            var dedup = new SoundDeduplicator
            {
                GlobalFrameCountForIgnoreSameSound = frames,
                SoundFrameCountForIgnoreSameSound = frames
            };
            dedup.Record(1, mode, 100, default);
            for (var frame = 100; frame <= 100 + frames; frame++)
            {
                Assert.IsFalse(dedup.CanPlay(1, mode, frame));
            }

            Assert.IsTrue(dedup.CanPlay(1, mode, 101 + frames));
            Assert.IsTrue(dedup.CanPlay(1, AudioKit.PlaySoundModes.EveryOne, 100));
            Assert.IsFalse(dedup.CanPlay(1, mode, 100 + frames));
        }

        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)]
        [TestCase(AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames)]
        public void DedupPrecedesPriorityAndBothBudgetsAndCapacityRejectionDoesNotRecordTheClip(AudioKit.PlaySoundModes mode)
        {
            AudioKit.PlaySoundMode = mode;
            AudioKit.SoundConcurrency = Concurrency(1, 1, SoundOverflowPolicy.StopLowestPriority);
            var first = AudioKit.PlaySound(_clip, Options(10));
            Assert.IsFalse(AudioKit.PlaySound(_clip, Options(int.MaxValue)).IsValid);
            Assert.IsTrue(first.IsValid);
            Assert.AreEqual(1L, AudioKit.SoundStats.DedupRejectedCount);
            Assert.AreEqual(0L, AudioKit.SoundStats.StolenCount);
            AudioKit.SoundConcurrency = Concurrency(1, 1, SoundOverflowPolicy.RejectNew);
            Assert.IsFalse(AudioKit.PlaySound(_other, Options()).IsValid);
            Assert.IsTrue(AudioKit.Channels.CanPlay(_other.GetInstanceID(), mode, Time.frameCount));
            first.Stop();
            Assert.IsFalse(AudioKit.PlaySound(_clip, Options()).IsValid, "Active stop preserves the committed cooldown.");
            Assert.IsTrue(AudioKit.PlaySound(_other, Options()).IsValid, "Freeing capacity permits a same-frame retry with no rejected record.");
            Assert.AreEqual(1L, AudioKit.SoundStats.GlobalRejectedCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateRandomCandidatesShareClipBudgetAndRejectAfterExactlyOneSelection(bool weighted)
        {
            AudioKit.SoundConcurrency = Concurrency(0, 1, SoundOverflowPolicy.RejectNew);
            var clips = new[] { _clip, _other, _clip };
            var entries = new[] { new AudioWeightedClip(_clip, 1f), new AudioWeightedClip(_other, 2f), new AudioWeightedClip(_clip, 3f) };
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

            var afterOne = Random.state;
            var occupying = AudioKit.PlaySound(selected, Options());
            Random.state = before;
            var rejected = weighted ? AudioKit.PlayWeightedRandomSound(entries, Options()) : AudioKit.PlayRandomSound(clips, Options());
            Assert.IsFalse(rejected.IsValid, "Do not reselect the other available clip.");
            Assert.AreEqual(afterOne, Random.state);
            Assert.AreEqual(1, AudioKit.GetSoundCount(selected));
            Assert.AreEqual(1L, AudioKit.SoundStats.ClipRejectedCount);
            occupying.Stop();
            var duplicates = new[] { new AudioWeightedClip(_clip, 1f), new AudioWeightedClip(_clip, 3f) };
            Assert.IsTrue(AudioKit.PlayWeightedRandomSound(duplicates, Options()).IsValid);
            Assert.IsFalse(AudioKit.PlayWeightedRandomSound(duplicates, Options()).IsValid);
            Assert.AreEqual(1, AudioKit.GetSoundCount(_clip));
        }

        [Test]
        public void ResetClearsHistoryButPreservesGaugesRulesDedupAndTheCurrentPeak()
        {
            AudioKit.SoundCacheCapacity = 3;
            AudioKit.PrewarmSounds(3);
            AudioKit.SetClipSoundLimit(_clip, 2);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
            var first = AudioKit.PlaySound(_clip, Options());
            var other = AudioKit.PlaySound(_other, Options());
            Assert.IsFalse(AudioKit.PlaySound(_clip, Options()).IsValid);
            other.Stop();
            var snapshot = AudioKit.SoundStats;
            Assert.AreEqual(2, snapshot.PeakActiveCount);
            AudioKit.ResetSoundStats();
            var reset = AudioKit.SoundStats;
            Assert.AreEqual(1, reset.ActiveCount);
            Assert.AreEqual(1, reset.PeakActiveCount);
            Assert.AreEqual(snapshot.CachedCount, reset.CachedCount);
            Assert.AreEqual(snapshot.OwnedSourceCount, reset.OwnedSourceCount);
            Assert.AreEqual(0L, reset.AdmittedCount);
            Assert.AreEqual(0L, reset.DedupRejectedCount);
            Assert.AreEqual(0L, reset.SourceCreatedCount);
            Assert.AreEqual(0L, reset.SourceReleasedCount);
            Assert.AreEqual(0L, reset.CacheHitCount);
            Assert.AreEqual(0L, reset.CacheMissCount);
            foreach (var property in typeof(AudioSoundStats).GetProperties())
            {
                if (property.PropertyType == typeof(long))
                {
                    Assert.AreEqual(0L, property.GetValue(reset), property.Name);
                }
            }

            Assert.IsTrue(first.IsValid);
            Assert.AreEqual(2, AudioKit.GetEffectiveClipSoundLimit(_clip));
            Assert.IsFalse(AudioKit.PlaySound(_clip, Options()).IsValid);
            Assert.AreEqual(2L, snapshot.AdmittedCount, "Snapshots must remain detached values after reset and later playback.");
            AudioKit.SoundCacheCapacity = 0;
            Assert.AreEqual(1, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(2L, AudioKit.SoundStats.SourceReleasedCount);
            first.Stop();
            Assert.AreEqual(0, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(3L, AudioKit.SoundStats.SourceReleasedCount, "Owned gauge cannot be derived from counters reset midway through ownership.");
        }

        [TestCase(0)]
        [TestCase(10)]
        [TestCase(24)]
        public void CacheCapacityIsIndependentOfActiveLimitAndReleasesUncachedRealSources(int capacity)
        {
            AudioKit.SoundCacheCapacity = capacity;
            AudioKit.SoundConcurrency = Concurrency(24, 0, SoundOverflowPolicy.RejectNew);
            var handles = new AudioHandle[24];
            var sources = new AudioSource[24];
            var roots = new GameObject[24];
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = AudioKit.PlaySound(_clip, Options());
                sources[i] = handles[i].Voice.Source;
                roots[i] = sources[i].gameObject;
            }
            Assert.AreEqual(24, AudioKit.SoundCount);
            Assert.AreEqual(24, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(0, AudioKit.SoundStats.CachedCount);
            AudioKit.SoundCacheCapacity = capacity;
            foreach (var handle in handles)
            {
                Assert.IsTrue(handle.IsValid);
            }

            foreach (var handle in handles)
            {
                handle.Stop();
            }

            var retained = 0;
            for (var i = 0; i < sources.Length; i++)
            {
                if (sources[i])
                {
                    retained++;
                    Assert.IsFalse(roots[i].activeSelf);
                    Assert.IsNull(sources[i].clip);
                    Assert.IsFalse(sources[i].isPlaying);
                }
                else
                {
                    Assert.IsFalse(roots[i], "Destroyed sources must not leave orphan GameObjects.");
                }
            }
            Assert.AreEqual(capacity, retained);
            Assert.AreEqual(capacity, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(capacity, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(24L, AudioKit.SoundStats.SourceCreatedCount);
            Assert.AreEqual(24 - capacity, AudioKit.SoundStats.SourceReleasedCount);
            AudioKit.SoundCacheCapacity = 24;
            Assert.AreEqual(capacity, AudioKit.SoundStats.CachedCount, "Increasing capacity must not automatically prewarm.");
        }

        [Test]
        public void PositiveShrinkAndShutdownPermanentlyRetireVoicesWithoutResettingIdleGenerations()
        {
            AudioKit.SoundCacheCapacity = 3;
            var handles = new AudioHandle[3];
            var roots = new GameObject[3];
            var generations = new ulong[3];
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = AudioKit.PlaySound(_clip, Options());
                roots[i] = handles[i].Voice.Source.gameObject;
            }
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i].Stop();
                generations[i] = handles[i].Voice.Generation;
            }
            var host = AudioManager.Instance;
            var pool = (ObjectPool<AudioVoice>)typeof(AudioManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(ObjectPool<AudioVoice>)).GetValue(host);
            AudioKit.SoundCacheCapacity = 1;
            Assert.IsTrue(roots[0], "LIFO shrinking discards the next allocation candidates first.");
            Assert.IsFalse(roots[1]);
            Assert.IsFalse(roots[2]);
            Assert.AreEqual(1, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(1, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(2L, AudioKit.SoundStats.SourceReleasedCount);
            for (var i = 0; i < handles.Length; i++)
            {
                Assert.AreEqual(generations[i], handles[i].Voice.Generation);
            }

            pool.Discard(handles[1].Voice);
            pool.Discard(handles[2].Voice);
            Assert.AreEqual(2L, AudioKit.SoundStats.SourceReleasedCount, "Retired voices cannot destroy their source twice.");

            var active = AudioKit.PlaySound(_clip, Options());
            Assert.AreSame(handles[0].Voice, active.Voice);
            AudioKit.SoundCacheCapacity = 2;
            var idle = AudioKit.PlaySound(_other, Options());
            var idleRoot = idle.Voice.Source.gameObject;
            idle.Stop();
            var idleGeneration = idle.Voice.Generation;
            AudioKit.Shutdown();
            Assert.IsFalse(active.IsValid);
            Assert.IsFalse(roots[0]);
            Assert.IsFalse(idleRoot);
            Assert.IsNull(active.Voice.Source);
            Assert.IsNull(idle.Voice.Source);
            Assert.AreEqual(idleGeneration, idle.Voice.Generation, "Shutdown discards idle sources without another Stop.");
            Assert.AreEqual(0, pool.CurStoreCount);
            Assert.Throws<ObjectDisposedException>(() => pool.Allocate());
            Assert.DoesNotThrow(() =>
            {
                pool.Discard(active.Voice);
                pool.Discard(idle.Voice);
                pool.Dispose();
            });
            Assert.IsTrue(_clip);
            Assert.IsTrue(_other);
        }

        [Test]
        public void PrewarmCreatesDistinctInactiveRealSourcesWithoutListenerPlaybackQuotaOrRandom()
        {
            AudioKit.SoundCacheCapacity = 24;
            AudioKit.SoundConcurrency = Concurrency(1, 1, SoundOverflowPolicy.RejectNew);
            AudioKit.Settings.IsSoundOn.Value = false;
            var listeners = Object.FindObjectsOfType<AudioListener>(true).Length;
            var state = Random.state;
            Assert.AreEqual(24, AudioKit.PrewarmSounds(24));
            Assert.AreEqual(0, AudioKit.PrewarmSounds(24));
            Assert.AreEqual(0, AudioKit.PrewarmSounds(10));
            var sources = AudioManager.Instance.GetComponentsInChildren<AudioSource>(true);
            var unique = new HashSet<int>();
            foreach (var source in sources)
            {
                Assert.IsTrue(unique.Add(source.GetInstanceID()));
                Assert.IsFalse(source.gameObject.activeSelf);
                Assert.IsFalse(source.playOnAwake);
                Assert.IsFalse(source.isPlaying);
                Assert.IsNull(source.clip);
                Assert.IsNull(source.outputAudioMixerGroup);
            }
            Assert.AreEqual(24, unique.Count, "Prewarm must not allocate/recycle the same LIFO item repeatedly.");
            var stats = AudioKit.SoundStats;
            Assert.AreEqual(24, stats.CachedCount);
            Assert.AreEqual(24, stats.OwnedSourceCount);
            Assert.AreEqual(24L, stats.SourceCreatedCount);
            Assert.AreEqual(0, stats.ActiveCount);
            Assert.AreEqual(0, stats.PeakActiveCount);
            Assert.AreEqual(0L, stats.AdmittedCount);
            Assert.AreEqual(0L, stats.CacheHitCount);
            Assert.AreEqual(0L, stats.CacheMissCount);
            Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
            Assert.IsTrue(AudioKit.Channels.CanPlay(_clip.GetInstanceID(), AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames, Time.frameCount));
            Assert.AreEqual(state, Random.state);
            Assert.AreEqual(listeners, Object.FindObjectsOfType<AudioListener>(true).Length);
            AudioKit.Settings.IsSoundOn.Value = true;
            var playing = AudioKit.PlaySound(_clip, Options());
            Assert.IsTrue(unique.Contains(playing.Voice.Source.GetInstanceID()));
            Assert.AreEqual(23, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(1, AudioKit.PrewarmSounds(24), "Target idle count excludes the active request.");
            Assert.AreEqual(25, AudioKit.SoundStats.OwnedSourceCount);
            AudioKit.SoundCacheCapacity = 0;
            Assert.IsTrue(playing.IsValid);
            Assert.AreEqual(1, AudioKit.SoundStats.OwnedSourceCount);
            playing.Stop();
            Assert.AreEqual(0, AudioKit.SoundStats.OwnedSourceCount);
        }

        [TestCase(0)]
        [TestCase(10)]
        public void DestroyedActiveSourceComponentStillReleasesItsRootAndOwnershipExactlyOnce(int capacity)
        {
            AudioKit.SoundCacheCapacity = capacity;
            var handle = AudioKit.PlaySound(_clip, Options());
            var source = handle.Voice.Source;
            var root = source.gameObject;
            Object.DestroyImmediate(source);
            Assert.IsTrue(root, "Destroying only the component leaves the owned root for Audio to clean.");
            handle.Stop();
            Assert.IsFalse(root);
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(0, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(0, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(1L, AudioKit.SoundStats.SourceCreatedCount);
            Assert.AreEqual(1L, AudioKit.SoundStats.SourceReleasedCount);
            handle.Stop();
            Assert.AreEqual(1L, AudioKit.SoundStats.SourceReleasedCount);
        }

        [Test]
        public void FailedPrewarmReturnsAllHeldVoicesWithoutFakeAdmissionsOrOrphanSources()
        {
            AudioKit.PrewarmSounds(2);
            var host = AudioManager.Instance;
            var pool = (ObjectPool<AudioVoice>)typeof(AudioManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(ObjectPool<AudioVoice>)).GetValue(host);
            var created = 0;
            pool.SetFactoryMethod(() =>
            {
                if (++created == 2)
                {
                    throw new InvalidOperationException("Injected prewarm factory failure.");
                }

                return new AudioVoice(host, AudioVoice.Kind.Sound);
            });
            try
            {
                Assert.Throws<InvalidOperationException>(() => AudioKit.PrewarmSounds(5));
                Assert.AreEqual(3, AudioKit.SoundStats.CachedCount, "The two original voices and the initialized new voice are returned once.");
                Assert.AreEqual(3, AudioKit.SoundStats.OwnedSourceCount);
                Assert.AreEqual(3L, AudioKit.SoundStats.SourceCreatedCount);
                Assert.AreEqual(0L, AudioKit.SoundStats.AdmittedCount);
                Assert.AreEqual(0L, AudioKit.SoundStats.CacheHitCount);
                Assert.AreEqual(0L, AudioKit.SoundStats.CacheMissCount);
                Assert.AreEqual(0, AudioKit.SoundCount);
                Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
                var ids = new HashSet<int>();
                foreach (var source in host.GetComponentsInChildren<AudioSource>(true))
                {
                    Assert.IsTrue(ids.Add(source.GetInstanceID()));
                    Assert.IsFalse(source.gameObject.activeSelf);
                    Assert.IsNull(source.clip);
                }
                Assert.AreEqual(3, ids.Count);
                Assert.AreEqual(3, host.transform.childCount);
            }
            finally
            {
                pool.SetFactoryMethod(() => new AudioVoice(host, AudioVoice.Kind.Sound));
            }
            Assert.AreEqual(2, AudioKit.PrewarmSounds(5));
            Assert.AreEqual(5, AudioKit.SoundStats.OwnedSourceCount);
            AudioKit.SoundCacheCapacity = 0;
            Assert.AreEqual(0, host.transform.childCount);
            Assert.AreEqual(5L, AudioKit.SoundStats.SourceReleasedCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DestroyedCachedSourceComponentCanBeRepairedOrShrunkWithoutLeavingOrphanRoots(bool repair)
        {
            AudioKit.PrewarmSounds(2);
            var sources = AudioManager.Instance.GetComponentsInChildren<AudioSource>(true);
            Assert.AreEqual(2, sources.Length);
            var oldRoot = sources[0].gameObject;
            Object.DestroyImmediate(sources[0]);
            if (repair)
            {
                Assert.AreEqual(0, AudioKit.PrewarmSounds(2), "Repair replaces a source without adding another idle voice.");
                Assert.IsFalse(oldRoot);
                Assert.AreEqual(2, AudioKit.SoundStats.CachedCount);
                Assert.AreEqual(2, AudioKit.SoundStats.OwnedSourceCount);
                Assert.AreEqual(3L, AudioKit.SoundStats.SourceCreatedCount);
                Assert.AreEqual(1L, AudioKit.SoundStats.SourceReleasedCount);
            }
            var roots = new List<GameObject> { oldRoot };
            foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
            {
                roots.Add(source.gameObject);
            }

            AudioKit.SoundCacheCapacity = 0;
            foreach (var root in roots)
            {
                Assert.IsFalse(root);
            }

            Assert.AreEqual(0, AudioKit.SoundStats.CachedCount);
            Assert.AreEqual(0, AudioKit.SoundStats.OwnedSourceCount);
            Assert.AreEqual(repair ? 3L : 2L, AudioKit.SoundStats.SourceReleasedCount);
            Assert.AreEqual(0, AudioManager.Instance.transform.childCount);
        }

        [Test]
        public void MusicVoiceAndCrossfadeIgnoreNegativePriorityAndNeverChangeSoundStats()
        {
            AudioKit.SoundConcurrency = Concurrency(1, 1, SoundOverflowPolicy.StopLowestPriority);
            var sound = AudioKit.PlaySound(_clip, Options(int.MaxValue));
            var before = AudioKit.SoundStats;
            var options = Options(-1);
            var target = new GameObject("Budget.VoiceTarget");
            try
            {
                var music = AudioKit.PlayMusicWithOptions(_clip, options);
                var voice = AudioKit.PlayVoiceWithOptions(_clip, options);
                var at = AudioKit.PlayVoiceAt(_clip, Vector3.one, options);
                var following = AudioKit.PlayVoiceFollowing(_clip, target.transform, options);
                var crossfade = AudioKit.CrossFadeMusic(_other, 1f, options);
                Assert.IsTrue(music.IsValid);
                Assert.AreEqual(0, music.Voice.Options.Priority);
                Assert.IsFalse(voice.IsValid);
                Assert.IsFalse(at.IsValid);
                Assert.IsTrue(following.IsValid);
                Assert.IsTrue(crossfade.IsValid);
                Assert.AreEqual(0, following.Voice.Options.Priority);
                Assert.AreEqual(0, crossfade.Voice.Options.Priority);
                Assert.IsTrue(sound.IsValid);
                Assert.AreEqual(before, AudioKit.SoundStats);
                AudioKit.StopMusic();
                AudioKit.StopVoice();
                Assert.AreEqual(before, AudioKit.SoundStats);
            }
            finally
            {
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void RecycleUsesCapturedClipIdentityEvenWhenNativeSourceClipWasCleared()
        {
            var first = AudioKit.PlaySound(_clip, Options());
            var clipId = _clip.GetInstanceID();
            first.Voice.Source.clip = null;
            first.Stop();
            Assert.AreEqual(0, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(0, AudioManager.Instance.GetSoundCount(clipId));
            var current = AudioKit.PlaySound(_other, Options(17));
            Assert.AreSame(first.Voice, current.Voice);
            Assert.AreEqual(_other.GetInstanceID(), current.Voice.ClipId);
            Assert.AreEqual(17, current.Voice.Priority);
            first.Stop();
            Assert.AreEqual(1, AudioKit.GetSoundCount(_other));
            current.Stop();
            Assert.AreEqual(0, AudioKit.SoundStats.ActiveCount);
            AudioKit.SetClipSoundLimit(_other, 1);
            AudioKit.SoundCacheCapacity = 24;
            AudioKit.Shutdown();
            Assert.AreEqual(default(AudioSoundStats), AudioKit.SoundStats);
            Assert.AreEqual(10, AudioKit.SoundCacheCapacity);
            Assert.AreEqual(0, AudioKit.GetEffectiveClipSoundLimit(_other));
            Assert.IsFalse(AudioKit.RemoveClipSoundLimit(_other));
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void StartedResetTrimAndNestedPlaybackObserveCommittedCountsAndKeepHistoricalAdmission()
        {
            AudioKit.SetClipSoundLimit(_clip, 2);
            var first = AudioKit.PlaySound(_clip, Options(10));
            var nested = default(AudioHandle);
            var returned = AudioKit.PlaySound(_clip, Options(1), onStarted: (_, handle) =>
            {
                Assert.AreEqual(2, AudioKit.GetSoundCount(_clip));
                Assert.AreEqual(2, AudioKit.SoundStats.PeakActiveCount);
                Assert.AreEqual(2L, AudioKit.SoundStats.AdmittedCount);
                AudioKit.ResetSoundStats();
                AudioKit.SetClipSoundLimit(_clip, 1);
                Assert.IsFalse(handle.IsValid);
                Assert.AreEqual(1L, AudioKit.SoundStats.TrimmedCount);
                nested = AudioKit.PlaySound(_other, Options(20));
                Assert.AreSame(handle.Voice, nested.Voice);
            });
            Assert.IsFalse(returned.IsValid);
            Assert.IsTrue(first.IsValid);
            Assert.IsTrue(nested.IsValid);
            Assert.AreEqual(1, AudioKit.GetSoundCount(_clip));
            Assert.AreEqual(1, AudioKit.GetSoundCount(_other));
            Assert.AreEqual(1L, AudioKit.SoundStats.AdmittedCount);
            Assert.AreEqual(2, AudioKit.SoundStats.PeakActiveCount);
        }
    }
}
