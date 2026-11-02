using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioRequestLifecycleTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;

        private sealed class Resource : IDisposable
        {
            internal int Releases;
            internal Action OnRelease;
            public void Dispose()
            {
                Releases++;
                OnRelease?.Invoke();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("request-lifecycle", 44100, 1, 44100, false);
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_clip);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ResponsibilityIsBoundBeforeStartedSelfStopShutdownOrFailure(int terminal)
        {
            var resource = new Resource();
            var lease = new AudioResourceLease(resource);
            var finished = 0;
            AudioHandle observed = default;
            AudioHandle Play() => AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults,
                (_, __) => finished++, (_, handle) =>
                {
                    observed = handle;
                    Assert.IsTrue(lease.IsBound);
                    Assert.AreEqual(0, resource.Releases);
                    if (terminal == 0)
                    {
                        handle.Stop();
                    }
                    else if (terminal == 1)
                    {
                        AudioKit.Shutdown();
                    }
                    else
                    {
                        throw new InvalidOperationException("expected started failure");
                    }
                }, resource: lease);
            if (terminal == 2)
            {
                Assert.Throws<InvalidOperationException>(() => Play());
            }
            else
            {
                var returned = Play();
                Assert.AreEqual(observed, returned);
            }
            observed.Stop();
            lease.Dispose();
            Assert.IsFalse(observed.IsValid);
            Assert.AreEqual(1, resource.Releases);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.AreEqual(0, finished);
            Assert.IsTrue(_clip);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NaturalCallbackRetainsOldResourceAcrossSameVoiceReplacement(bool music)
        {
            var resource = new Resource();
            var lease = new AudioResourceLease(resource);
            var callbacks = 0;
            AudioHandle next = default;
            Action<AudioClip, AudioHandle> finished = (_, old) =>
            {
                callbacks++;
                Assert.IsTrue(old.IsValid);
                old.Stop();
                next = music ? AudioKit.PlayMusic(_clip) : AudioKit.PlaySound(_clip);
                Assert.AreSame(old.Voice, next.Voice);
                Assert.AreEqual(0, resource.Releases, "Natural notification still owns its original resource.");
            };
            var handle = music
                ? AudioKit.PlaySlotWithSnapshot(_clip, true, AudioPlaybackOptions.Defaults, null, finished, lease)
                : AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults, finished, resource: lease);
            handle.Voice.Source.Stop();
            handle.Voice.CheckFinished();
            Assert.AreEqual(1, callbacks);
            Assert.AreEqual(1, resource.Releases);
            Assert.IsTrue(next.IsValid);
            handle.Stop();
            Assert.IsTrue(next.IsValid);
        }

        [Test]
        public void ThrowingNaturalNotificationAndResourceReleaseDoNotPreventOtherCleanup()
        {
            var resource = new Resource { OnRelease = () => throw new InvalidOperationException("expected release failure") };
            var lease = new AudioResourceLease(resource);
            var handle = AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults,
                (_, __) => throw new InvalidOperationException("expected natural failure"), resource: lease);
            handle.Voice.Source.Stop();
            LogAssert.Expect(LogType.Exception, new Regex("expected release failure"));
            Assert.Throws<InvalidOperationException>(() => handle.Voice.CheckFinished());
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(1, resource.Releases);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.IsNull(handle.Voice.Source.clip);
            lease.Dispose();
            Assert.AreEqual(1, resource.Releases);
            Assert.IsTrue(AudioKit.PlaySound(_clip).IsValid);
        }

        [Test]
        public void RejectionValidationAndOldSessionReleaseWithoutBuildingHost()
        {
            AudioKit.Settings.IsSoundOn.Value = false;
            var rejected = new Resource();
            Assert.IsFalse(AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults,
                resource: new AudioResourceLease(rejected)).IsValid);
            Assert.AreEqual(1, rejected.Releases);
            var invalid = AudioPlaybackOptions.Defaults;
            invalid.Pitch = float.NaN;
            var failed = new Resource();
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.PlaySoundWithSnapshot(_clip, invalid,
                resource: new AudioResourceLease(failed)));
            Assert.AreEqual(1, failed.Releases);
            var late = new Resource();
            var stale = new AudioResourceLease(late);
            AudioKit.Shutdown();
            Assert.Throws<InvalidOperationException>(() => AudioKit.PlaySoundWithSnapshot(_clip,
                AudioPlaybackOptions.Defaults, resource: stale));
            Assert.AreEqual(1, late.Releases);
            Assert.IsNull(AudioKit.CurrentSettings);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void DisabledSlotsAndCrossfadeTailRetainResponsibilityUntilTheirOwnEnd()
        {
            var first = new Resource();
            var second = new Resource();
            AudioKit.Settings.IsMusicOn.Value = false;
            var old = AudioKit.PlaySlotWithSnapshot(_clip, true, AudioPlaybackOptions.MusicDefaults,
                null, null, new AudioResourceLease(first));
            Assert.AreEqual(0, first.Releases);
            AudioKit.Settings.IsMusicOn.Value = true;
            var next = AudioKit.CrossFadeMusicWithSnapshot(_clip, 1f, AudioPlaybackOptions.MusicDefaults,
                resource: new AudioResourceLease(second));
            Assert.IsTrue(old.IsValid);
            Assert.AreEqual(0, first.Releases);
            AudioManager.Instance.MusicPlayback.Tick(1f, 1f);
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(1, first.Releases);
            Assert.AreEqual(0, second.Releases);
            AudioKit.Shutdown();
            AudioKit.Shutdown();
            Assert.IsFalse(next.IsValid);
            Assert.AreEqual(1, second.Releases);
            Assert.IsTrue(_clip);
        }

        [Test]
        public void StopAllSnapshotAndReplacementReleaseCannotStopLaterRequests()
        {
            AudioHandle nested = default;
            var resource = new Resource { OnRelease = () => nested = AudioKit.PlaySound(_clip) };
            var old = AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults,
                resource: new AudioResourceLease(resource));
            AudioKit.StopAllSound();
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(nested.IsValid);
            Assert.AreEqual(1, AudioKit.SoundCount);
            var musicResource = new Resource { OnRelease = () => nested = AudioKit.PlayMusic(_clip) };
            AudioKit.PlaySlotWithSnapshot(_clip, true, AudioPlaybackOptions.MusicDefaults,
                null, null, new AudioResourceLease(musicResource));
            Assert.IsFalse(AudioKit.PlayMusic(_clip).IsValid, "Latest nested replacement wins.");
            Assert.AreEqual(nested, AudioKit.Music);
            Assert.IsTrue(nested.IsValid);
        }

        [Test]
        public void ReleaseReentrySoundOffRejectsTheUncommittedReplacement()
        {
            AudioKit.SoundConcurrency = new AudioSoundConcurrency { Limit = 1, OverflowPolicy = SoundOverflowPolicy.StopOldest };
            var oldResource = new Resource { OnRelease = () => AudioKit.Settings.IsSoundOn.Value = false };
            var old = AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults,
                resource: new AudioResourceLease(oldResource));
            var newResource = new Resource();
            var starts = 0;
            var next = AudioKit.PlaySoundWithSnapshot(_clip, AudioPlaybackOptions.Defaults, onStarted: (_, __) => starts++,
                resource: new AudioResourceLease(newResource));
            Assert.IsFalse(old.IsValid);
            Assert.IsFalse(next.IsValid);
            Assert.AreEqual(0, starts);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.AreEqual(1, oldResource.Releases);
            Assert.AreEqual(1, newResource.Releases);
        }

        [Test]
        public void ReleaseReentryMusicOnAndReplacementCannotBeDisabledByOldSettingDelivery()
        {
            AudioHandle nested = default;
            var resource = new Resource
            {
                OnRelease = () =>
            {
                AudioKit.Settings.IsMusicOn.Value = true;
                nested = AudioKit.PlayMusic(_clip);
            }
            };
            AudioKit.PlaySlotWithSnapshot(_clip, true, AudioPlaybackOptions.MusicDefaults, null, null, new AudioResourceLease(resource));
            AudioKit.CrossFadeMusic(_clip, 1f);
            AudioKit.Settings.IsMusicOn.Value = false;
            Assert.IsTrue(AudioKit.Settings.IsMusicOn.Value);
            Assert.IsTrue(nested.IsValid);
            Assert.IsTrue(nested.IsPlaying);
            Assert.AreEqual(nested, AudioKit.Music);
            Assert.AreEqual(1, resource.Releases);
        }

        [Test]
        public void OldHostLateQuitCannotCloseReplacementSession()
        {
            var old = AudioManager.Instance;
            AudioKit.Shutdown();
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            var current = AudioKit.PlaySound(_clip);
            var quit = typeof(AudioManager).GetMethod("OnApplicationQuit", BindingFlags.Instance | BindingFlags.NonPublic);
            quit.Invoke(old, null);
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(current, AudioKit.GetPlayingSounds()[0]);
            Assert.IsTrue(AudioKit.PlaySound(_clip).IsValid);
        }

        [Test]
        public void PartialHostInitializationFailureReleasesSubscriptionsAndAllowsCleanRetry()
        {
            var settings = AudioKit.Settings;
            var field = typeof(AudioKitSettings).GetField("<MusicOutputGroup>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = field.GetValue(settings);
            field.SetValue(settings, null);
            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("NullReferenceException"));
                Assert.Throws<NullReferenceException>(() =>
                {
                    _ = AudioManager.Instance;
                });
                Assert.IsFalse(AudioManager.HasInstance);
                Assert.IsNull(AudioKit.CurrentSettings);
                Assert.IsEmpty(Object.FindObjectsOfType<AudioManager>(true));
            }
            finally
            {
                field.SetValue(settings, original);
            }
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            var current = AudioKit.PlaySound(_clip);
            settings.IsSoundOn.Value = false;
            Assert.IsTrue(current.IsValid);
        }
    }
}
