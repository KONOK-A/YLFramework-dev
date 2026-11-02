using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioChannelTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;
        private GameObject _external;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("channel", 44100, 1, 44100, false);
            _external = new GameObject("caller-source");
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_external);
            Object.DestroyImmediate(_clip);
        }

        [Test]
        public void NamedRegistrationRejectsDuplicateOwnerAndExpiresAtShutdown()
        {
            var channel = AudioKit.RegisterChannel("ambient");
            Assert.Throws<InvalidOperationException>(() => AudioKit.RegisterChannel("ambient"));
            Assert.IsTrue(channel.IsValid);
            AudioKit.Shutdown();
            Assert.IsFalse(channel.IsValid);
            var current = AudioKit.RegisterChannel("ambient");
            var handle = current.Play(_clip);
            channel.Stop();
            channel.Dispose();
            channel.Pause();
            Assert.IsTrue(handle.IsValid);
            Assert.Throws<InvalidOperationException>(() => channel.Play(_clip));
            Assert.IsTrue(current.IsValid);
        }

        [Test]
        public void BorrowedSourceUsesOriginalHandleAndLeavesCallerObjectAlive()
        {
            var channel = AudioKit.RegisterChannel("borrowed");
            var source = _external.AddComponent<AudioSource>();
            source.volume = 0.37f;
            var handle = channel.PlayBorrowed(source, _clip);
            Assert.IsTrue(handle.IsValid);
            Assert.AreSame(source, handle.Voice.Source);
            handle.Stop();
            Assert.IsTrue(source);
            Assert.IsTrue(_external.activeSelf);
            Assert.AreEqual(0.37f, source.volume);
        }

        [Test]
        public void AutomaticPauseIsExplicitAndCannotResumeManualPause()
        {
            var options = AudioSoundOptions.MusicDefaults;
            Assert.IsFalse(options.AutoPauseWhenTimeScaleZero);
            options.AutoPauseWhenTimeScaleZero = true;
            var handle = AudioKit.PlaySound(_clip, options);
            handle.Voice.RefreshAutomaticPause(true);
            Assert.IsTrue(handle.IsPaused);
            handle.Pause();
            handle.Voice.RefreshAutomaticPause(false);
            Assert.IsTrue(handle.IsPaused);
            handle.Resume();
            Assert.IsFalse(handle.IsPaused);
        }

        [Test]
        public void IndependentLoopsAndLocalSettingsPreserveDefaultMusicAndCategorySemantics()
        {
            var first = AudioKit.RegisterChannel("wind");
            var second = AudioKit.RegisterChannel("rain");
            var bgm = AudioKit.PlayMusic(_clip);
            var wind = first.Play(_clip, AudioSoundOptions.MusicDefaults);
            var rain = second.Play(_clip, AudioSoundOptions.MusicDefaults);
            first.Volume = .4f;
            first.IsMuted = true;
            Assert.AreEqual(.4f, wind.Voice.Source.volume, .001f);
            Assert.IsTrue(wind.Voice.Source.mute);
            Assert.AreEqual(1f, rain.Voice.Source.volume);
            Assert.IsFalse(rain.Voice.Source.mute);
            Assert.AreEqual(1f, bgm.Voice.Source.volume);
            Assert.AreEqual(1f, AudioKit.Settings.MusicVolume.Value);
            AudioKit.Settings.MusicVolume.Value = .5f;
            Assert.AreEqual(.2f, wind.Voice.Source.volume, .001f);
            Assert.AreEqual(.5f, rain.Voice.Source.volume, .001f);
            AudioKit.Settings.IsMusicOn.Value = false;
            Assert.IsTrue(wind.IsValid);
            Assert.IsTrue(rain.IsValid);
            Assert.IsTrue(bgm.IsValid);
            Assert.IsFalse(wind.IsPlaying);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.IsTrue(wind.IsPlaying);
            first.Stop();
            Assert.IsFalse(wind.IsValid);
            Assert.IsTrue(rain.IsValid);
            Assert.IsTrue(bgm.IsValid);
        }

        [Test]
        public void SoundOverlapUsesExistingBudgetAndLocalPauseDoesNotReleaseManualPause()
        {
            var channel = AudioKit.RegisterChannel("steps", AudioCategory.Sound, AudioChannelMode.Overlap);
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = 2 };
            var first = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            var second = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            Assert.AreNotSame(first.Voice.Source, second.Voice.Source);
            Assert.IsFalse(AudioKit.PlaySound(_clip).IsValid);
            Assert.AreEqual(2, channel.RequestCount);
            first.Pause();
            channel.Pause();
            channel.Resume();
            Assert.IsTrue(first.IsPaused);
            Assert.IsFalse(second.IsPaused);
            first.Resume();
            AudioKit.Settings.IsSoundOn.Value = false;
            Assert.AreEqual(0, channel.RequestCount);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.IsFalse(channel.Play(_clip).IsValid);
        }

        [Test]
        public void InvalidOptionsAndIdentitiesDoNotReplaceOrCreateSettings()
        {
            AudioKit.Shutdown();
            Assert.Throws<ArgumentException>(() => AudioKit.RegisterChannel(" "));
            Assert.Throws<ArgumentException>(() => AudioKit.RegisterChannel("ok", persistenceId: "bad:identity"));
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.RegisterChannel("ok", (AudioCategory)99));
            Assert.IsNull(AudioKit.CurrentSettings);
            var channel = AudioKit.RegisterChannel("valid");
            var original = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            Assert.Throws<ArgumentOutOfRangeException>(() => channel.Play(_clip, new AudioSoundOptions { Pitch = float.NaN }));
            Assert.Throws<ArgumentOutOfRangeException>(() => channel.Volume = float.NaN);
            Assert.IsFalse(channel.Play(null).IsValid);
            Assert.IsTrue(original.IsValid);
        }

        [Test]
        public void PersistenceIsExplicitStableAndNeverWritesLegacyKeys()
        {
            var id = "tests/" + Guid.NewGuid().ToString("N");
            var volumeKey = "YLFramework.Audio.Channel:" + id + ":Volume";
            var muteKey = "YLFramework.Audio.Channel:" + id + ":Mute";
            var oldKey = "AudioConfigVolume:" + id;
            try
            {
                var temporary = AudioKit.RegisterChannel(id);
                temporary.Volume = .25f;
                temporary.IsMuted = true;
                temporary.Dispose();
                Assert.IsFalse(PlayerPrefs.HasKey(volumeKey));
                Assert.IsFalse(PlayerPrefs.HasKey(muteKey));
                var persistent = AudioKit.RegisterChannel("stored", persistenceId: id);
                Assert.Throws<InvalidOperationException>(() => AudioKit.RegisterChannel("alias", persistenceId: id));
                persistent.Volume = .6f;
                persistent.IsMuted = true;
                persistent.Dispose();
                var restored = AudioKit.RegisterChannel("renamed", persistenceId: id);
                Assert.AreEqual(.6f, restored.Volume);
                Assert.IsTrue(restored.IsMuted);
                Assert.IsFalse(PlayerPrefs.HasKey(oldKey));
                Assert.IsFalse(PlayerPrefs.HasKey("YLFramework.Core.Audio.Volume:" + id));
                Assert.IsFalse(PlayerPrefs.HasKey("AudioConfigMute:" + id));
                Assert.IsFalse(PlayerPrefs.HasKey("YLFramework.Core.Audio.Mute:" + id));
            }
            finally
            {
                PlayerPrefs.DeleteKey(volumeKey);
                PlayerPrefs.DeleteKey(muteKey);
            }
        }

        [Test]
        public void BorrowingRejectsOccupancyAndRestoresOnlyUnchangedLeaseFields()
        {
            var first = AudioKit.RegisterChannel("one");
            var second = AudioKit.RegisterChannel("two");
            var source = _external.AddComponent<AudioSource>();
            source.clip = _clip;
            source.loop = true;
            source.pitch = .8f;
            source.volume = .7f;
            source.spatialBlend = .6f;
            source.dopplerLevel = 1.7f;
            var old = first.PlayBorrowed(source, _clip);
            old.Pause();
            Assert.Throws<InvalidOperationException>(() => second.PlayBorrowed(source, _clip));
            Assert.Throws<InvalidOperationException>(() => first.PlayBorrowed(source, _clip));
            source.pitch = 1.4f;
            old.Stop();
            Assert.IsTrue(source.loop);
            Assert.AreEqual(.7f, source.volume);
            Assert.AreEqual(1.4f, source.pitch);
            Assert.AreEqual(.6f, source.spatialBlend);
            Assert.AreEqual(1.7f, source.dopplerLevel);
            Assert.AreSame(_clip, source.clip);
            var next = second.PlayBorrowed(source, _clip);
            old.Stop();
            Assert.IsTrue(next.IsValid);
            Assert.AreSame(_clip, source.clip);
            AudioKit.Shutdown();
            Assert.IsTrue(source);
            Assert.IsTrue(_external.activeSelf);
            Assert.AreEqual(.7f, source.volume);
            Assert.AreEqual(1.4f, source.pitch);
        }

        [Test]
        public void CallerClipReplacementAndSourceLossCannotStopUnrelatedPlayback()
        {
            var channel = AudioKit.RegisterChannel("external");
            var source = _external.AddComponent<AudioSource>();
            var other = AudioClip.Create("caller-replacement", 44100, 1, 44100, false);
            try
            {
                var handle = channel.PlayBorrowed(source, _clip);
                source.clip = other;
                source.volume = .23f;
                source.Play();
                handle.Voice.CheckFinished();
                Assert.IsFalse(handle.IsValid);
                Assert.AreSame(other, source.clip);
                Assert.IsTrue(source.isPlaying);
                Assert.AreEqual(.23f, source.volume);
                source.Stop();
                var destroyed = channel.PlayBorrowed(source, _clip);
                Object.DestroyImmediate(source);
                destroyed.Voice.CheckFinished();
                Assert.IsFalse(destroyed.IsValid);
                Assert.AreEqual(0, channel.RequestCount);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        private sealed class Resource : IDisposable
        {
            internal Action Release;
            internal int Releases;
            public void Dispose()
            {
                Releases++;
                Release?.Invoke();
            }
        }

        [Test]
        public void BorrowedCleanupCompletesBeforeResourceReentryCreatesANewLease()
        {
            var channel = AudioKit.RegisterChannel("reentry", AudioCategory.Sound);
            var source = _external.AddComponent<AudioSource>();
            source.volume = .38f;
            AudioHandle next = default;
            var resource = new Resource { Release = () => next = channel.PlayBorrowed(source, _clip, new AudioSoundOptions { Volume = .6f }) };
            var old = channel.PlayWithSnapshot(_clip, AudioPlaybackOptions.Defaults, resource: new AudioResourceLease(resource), source: source, borrowed: true);
            channel.Stop();
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(next.IsValid);
            Assert.AreEqual(.6f, source.volume, .001f);
            Assert.AreEqual(1, resource.Releases);
            Assert.AreEqual(1, channel.RequestCount);
            next.Stop();
            Assert.AreEqual(.38f, source.volume);
            Assert.AreEqual(0, AudioKit.SoundStats.CachedCount);
        }

        [Test]
        public void CrossfadeDoesNotConvertAutomaticPauseIntoManualPause()
        {
            var scale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;
                var options = AudioSoundOptions.MusicDefaults;
                options.AutoPauseWhenTimeScaleZero = true;
                var old = AudioKit.PlayMusicWithOptions(_clip, options);
                var next = AudioKit.CrossFadeMusic(_clip, 1f, options);
                Time.timeScale = 1f;
                old.Voice.RefreshAutomaticPause(false);
                next.Voice.RefreshAutomaticPause(false);
                Assert.IsFalse(next.IsPaused, "Automatic pause is not inherited as a manual pause reason.");
                Assert.IsTrue(next.IsPlaying);
            }
            finally
            {
                Time.timeScale = scale;
            }
        }

        [Test]
        public void ReplacementReleaseCannotOverwriteSourceBorrowedByNestedRequest()
        {
            var first = AudioKit.RegisterChannel("outer");
            var second = AudioKit.RegisterChannel("nested");
            var source = _external.AddComponent<AudioSource>();
            AudioHandle nested = default;
            var resource = new Resource { Release = () => nested = second.PlayBorrowed(source, _clip, new AudioSoundOptions { Loop = true, Volume = .55f }) };
            first.PlayWithSnapshot(_clip, AudioPlaybackOptions.Defaults, resource: new AudioResourceLease(resource));
            Assert.Throws<InvalidOperationException>(() => first.PlayBorrowed(source, _clip));
            Assert.IsTrue(nested.IsValid);
            Assert.IsTrue(nested.IsPlaying);
            Assert.AreEqual(.55f, source.volume, .001f);
            Assert.AreEqual(1, second.RequestCount);
        }

        [Test]
        public void BudgetVictimReleaseCannotCommitIntoDisposedChannel()
        {
            var channel = AudioKit.RegisterChannel("admission", AudioCategory.Sound);
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = 1, OverflowPolicy = SoundOverflowPolicy.StopOldest };
            var resource = new Resource { Release = channel.Dispose };
            AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults, resource: new AudioResourceLease(resource));
            var result = channel.Play(_clip);
            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [Test]
        public void OverlapResumeSnapshotDoesNotLeaveOtherOriginalRequestsPausedAfterNestedPlay()
        {
            var channel = AudioKit.RegisterChannel("resume", mode: AudioChannelMode.Overlap);
            channel.Pause();
            AudioHandle nested = default;
            var first = channel.Play(_clip, onStarted: (_, __) => nested = channel.Play(_clip));
            var second = channel.Play(_clip);
            channel.Resume();
            Assert.IsTrue(nested.IsValid);
            Assert.IsFalse(nested.IsPaused);
            Assert.IsFalse(first.IsPaused);
            Assert.IsFalse(second.IsPaused);
        }

        [Test]
        public void ShutdownUsesUnifiedShutdownTerminalForNamedAndBorrowedRequests()
        {
            var channel = AudioKit.RegisterChannel("shutdown");
            var source = _external.AddComponent<AudioSource>();
            var handle = channel.PlayBorrowed(source, _clip);
            AudioKit.Shutdown();
            Assert.AreEqual(AudioRequestEnd.Shutdown, handle.Voice.EndReason);
            Assert.IsFalse(channel.IsValid);
            Assert.IsFalse(handle.IsValid);
            Assert.IsTrue(source);
        }

        [Test]
        public void ResumeContinuesOriginalSnapshotAfterOneStartedCallbackFails()
        {
            var channel = AudioKit.RegisterChannel("resume-failure", mode: AudioChannelMode.Overlap);
            channel.Pause();
            var failed = channel.Play(_clip, onStarted: (_, __) => throw new InvalidOperationException("expected start failure"));
            var other = channel.Play(_clip);
            Assert.Catch<Exception>(channel.Resume);
            Assert.IsFalse(failed.IsValid);
            Assert.IsTrue(other.IsPlaying);
            Assert.IsFalse(other.IsPaused);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CategoryEnableRefreshContinuesOtherNamedRequestsAfterStartedFailure(bool defaultSlotFails)
        {
            var failing = AudioKit.RegisterChannel("failing");
            var otherChannel = AudioKit.RegisterChannel("other");
            AudioKit.Settings.IsMusicOn.Value = false;
            Action<AudioClip, AudioHandle> throwStarted = (_, __) => throw new InvalidOperationException("expected category start failure");
            var failed = defaultSlotFails ? AudioKit.PlayMusic(_clip, onStarted: throwStarted) : failing.Play(_clip, onStarted: throwStarted);
            var other = otherChannel.Play(_clip);
            Assert.Catch<Exception>(() => AudioKit.Settings.IsMusicOn.Value = true);
            Assert.IsFalse(failed.IsValid);
            Assert.IsTrue(other.IsPlaying);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExclusiveReleaseRechecksSoundSwitchAndDedupBeforeAdmission(bool dedup)
        {
            var channel = AudioKit.RegisterChannel("switch-reentry", AudioCategory.Sound);
            AudioHandle nested = default;
            var resource = new Resource
            {
                Release = () =>
            {
                if (dedup)
                {
                    nested = AudioKit.PlaySound(_clip, new AudioSoundOptions { PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames });
                }
                else
                {
                    AudioKit.Settings.IsSoundOn.Value = false;
                }
            }
            };
            channel.PlayWithSnapshot(_clip, AudioPlaybackOptions.Defaults, resource: new AudioResourceLease(resource));
            var rejected = channel.Play(_clip, new AudioSoundOptions { PlaySoundMode = dedup ? AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames : AudioKit.PlaySoundModes.EveryOne });
            Assert.IsFalse(rejected.IsValid);
            Assert.AreEqual(dedup ? 1 : 0, AudioKit.SoundCount);
            if (dedup)
            {
                Assert.IsTrue(nested.IsValid);
            }
        }

        [TestCase(AudioCategory.Music)]
        [TestCase(AudioCategory.Voice)]
        [TestCase(AudioCategory.Sound)]
        public void NamedAndBorrowedRoutesInheritCategoryWhileExplicitRouteStaysIndependent(AudioCategory category)
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            var music = mixer.FindMatchingGroups("Music")[0];
            var ui = mixer.FindMatchingGroups("UI")[0];
            var source = _external.AddComponent<AudioSource>();
            source.outputAudioMixerGroup = ui;
            var channel = AudioKit.RegisterChannel("route", category);
            var explicitChannel = AudioKit.RegisterChannel("override", category);
            var inherited = channel.PlayBorrowed(source, _clip);
            var explicitRequest = explicitChannel.Play(_clip, new AudioSoundOptions { OutputRoute = AudioOutputRoute.ToMixer(ui) });
            switch (category)
            {
                case AudioCategory.Music:
                    AudioKit.Settings.MusicOutputGroup.Value = music;
                    break;
                case AudioCategory.Voice:
                    AudioKit.Settings.VoiceOutputGroup.Value = music;
                    break;
                default:
                    AudioKit.Settings.SoundOutputGroup.Value = music;
                    break;
            }
            Assert.AreSame(music, source.outputAudioMixerGroup);
            Assert.AreSame(ui, explicitRequest.Voice.Source.outputAudioMixerGroup);
            inherited.Stop();
            Assert.AreSame(ui, source.outputAudioMixerGroup);
        }

        [Test]
        public void BorrowValidationCannotReplaceOldRequestOrCaptureFrameworkOwnedSource()
        {
            var channel = AudioKit.RegisterChannel("validation");
            var old = channel.Play(_clip);
            var source = _external.AddComponent<AudioSource>();
            Assert.Throws<ArgumentNullException>(() => channel.PlayBorrowed(null, _clip));
            source.enabled = false;
            Assert.Throws<ArgumentException>(() => channel.PlayBorrowed(source, _clip));
            source.enabled = true;
            Assert.Throws<InvalidOperationException>(() => channel.PlayBorrowed(old.Voice.Source, _clip));
            Assert.Throws<ArgumentOutOfRangeException>(() => channel.PlayBorrowed(source, _clip, new AudioSoundOptions { Pitch = -1f }));
            var overlap = AudioKit.RegisterChannel("overlap", mode: AudioChannelMode.Overlap);
            Assert.Throws<InvalidOperationException>(() => overlap.PlayBorrowed(source, _clip));
            Assert.IsTrue(old.IsValid);
            Assert.IsNull(source.clip);
        }

        [Test]
        public void DisposedRegistrationCannotControlSameNameRecreatedDuringRelease()
        {
            var old = AudioKit.RegisterChannel("owned", AudioCategory.Sound);
            AudioChannel current = null;
            AudioHandle next = default;
            var resource = new Resource
            {
                Release = () =>
                {
                    current = AudioKit.RegisterChannel("owned", AudioCategory.Sound);
                    next = current.Play(_clip);
                }
            };
            var original = old.PlayWithSnapshot(_clip, AudioPlaybackOptions.Defaults, resource: new AudioResourceLease(resource));
            old.Dispose();
            Assert.AreSame(original.Voice, next.Voice, "Existing Sound pool may reuse the same Voice.");
            old.Stop();
            old.Pause();
            old.Resume();
            old.Dispose();
            original.Stop();
            Assert.IsTrue(current.IsValid);
            Assert.IsTrue(next.IsValid);
            Assert.IsFalse(next.IsPaused);
        }
    }
}
