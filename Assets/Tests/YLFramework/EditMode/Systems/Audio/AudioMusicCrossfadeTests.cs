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
    public class AudioMusicCrossfadeTests
    {
        private AudioTestSettings _settings;
        private AudioClip _a;
        private AudioClip _b;
        private AudioClip _c;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _a = AudioClip.Create("crossfade-edit-a", 441000, 1, 44100, false);
            _b = AudioClip.Create("crossfade-edit-b", 441000, 1, 44100, false);
            _c = AudioClip.Create("crossfade-edit-c", 441000, 1, 44100, false);
        }

        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_a);
            Object.DestroyImmediate(_b);
            Object.DestroyImmediate(_c);
        }

        private static AudioSoundOptions Options(float volume = 1f)
        {
            var options = AudioSoundOptions.Defaults;
            options.Loop = true;
            options.Volume = volume;
            return options;
        }

        private static void AssertVolume(AudioHandle handle, float expected, string message = null)
        {
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(expected, handle.Voice.Source.volume, .0001f, message);
        }

        private static int SourceCount(AudioManager host) => host.GetComponentsInChildren<AudioSource>(true).Length;

        [TestCase(-1)]
        [TestCase(2)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void InvalidCurvesPreserveValidationOrderAndBothMusicParticipants(int value)
        {
            AudioKit.Shutdown();
            var curve = (AudioCrossfadeCurve)value;
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.CrossFadeMusic(null, 1f, curve: curve));
            Assert.AreEqual("curve", error.ParamName);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
            var durationError = Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.CrossFadeMusic(null, -1f, curve: curve));
            Assert.AreEqual("duration", durationError.ParamName);
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
            var outgoing = AudioKit.PlayMusic(_a);
            var incoming = AudioKit.CrossFadeMusic(_b, 2f);
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.CrossFadeMusic(_c, 1f, curve: curve));
            Assert.IsTrue(outgoing.IsValid);
            Assert.IsTrue(incoming.IsValid);
            Assert.AreEqual(incoming, AudioKit.Music);
            Assert.AreEqual(2, SourceCount(AudioManager.Instance));
        }

        [TestCase(-1)]
        [TestCase(2)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void ComponentRejectsInvalidCurveWithoutReplacingItsOwnedMusic(int value)
        {
            var root = new GameObject("Crossfade.InvalidCurve");
            try
            {
                var player = root.AddComponent<AudioMusicPlayer>();
                using (var serialized = new SerializedObject(player))
                {
                    serialized.FindProperty("clip").objectReferenceValue = _a;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    player.Play();
                    var original = AudioKit.Music;
                    serialized.FindProperty("clip").objectReferenceValue = _b;
                    serialized.FindProperty("crossFadeOnPlay").boolValue = true;
                    serialized.FindProperty("crossFadeCurve").intValue = value;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    var error = Assert.Throws<ArgumentOutOfRangeException>(player.Play);
                    Assert.AreEqual("crossFadeCurve", error.ParamName);
                    Assert.IsTrue(original.IsValid);
                    Assert.AreEqual(original, AudioKit.Music);
                    Assert.AreSame(_a, original.Voice.Source.clip);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void Tick(float deltaTime, float unscaledDeltaTime)
        {
            var property = typeof(AudioManager).GetProperty("MusicPlayback", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(property);
            var playback = property.GetValue(AudioManager.Instance);
            var tick = playback.GetType().GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(float), typeof(float) }, null);
            Assert.IsNotNull(tick);
            // Transition only: request envelopes are advanced explicitly when the test needs them.
            tick.Invoke(playback, new object[] { deltaTime, unscaledDeltaTime });
        }

        [Test]
        public void InvalidDurationThrowsBeforeHostCreationOrEitherParticipantChanges()
        {
            foreach (var duration in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                AudioKit.Shutdown();
                Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.CrossFadeMusic(_b, duration));
                Assert.IsFalse(AudioManager.HasInstance);
                Assert.IsNull(AudioKit.CurrentSettings);
            }
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
            AudioKit.Settings.MusicVolume.SetValueWithoutCallback(1f);
            var old = AudioKit.PlayMusic(_a);
            var current = AudioKit.CrossFadeMusic(_b, 2f);
            var host = AudioManager.Instance;
            foreach (var duration in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.CrossFadeMusic(_c, duration));
                Assert.IsTrue(old.IsValid);
                Assert.AreEqual(current, AudioKit.Music);
                AssertVolume(old, 1f);
                AssertVolume(current, 0f);
                Assert.AreEqual(2, SourceCount(host));
            }
        }

        [Test]
        public void InvalidOptionsAreValidatedBeforeStoppingEitherMusicOrAllocatingSources()
        {
            var invalid = new AudioSoundOptions[5];
            invalid[0] = Options();
            invalid[0].Pitch = 0f;
            invalid[1] = Options();
            invalid[1].Volume = float.NaN;
            invalid[2] = Options();
            invalid[2].OutputRoute.Mode = (AudioOutputRoute.RouteMode)999;
            invalid[3] = Options();
            invalid[3].AutoFade = new AudioAutoFade(-1f, 0f);
            invalid[4] = Options();
            invalid[4].AutoFade = new AudioAutoFade(0f, 1f);
            var old = AudioKit.PlayMusic(_a);
            var current = AudioKit.CrossFadeMusic(_b, 2f);
            foreach (var options in invalid)
            {
                Assert.Catch<ArgumentException>(() => AudioKit.CrossFadeMusic(_c, 1f, options));
                Assert.IsTrue(old.IsValid);
                AssertVolume(current, 0f);
                Assert.AreEqual(current, AudioKit.Music);
                Assert.AreEqual(2, SourceCount(AudioManager.Instance));
            }
            AudioKit.Shutdown();
            foreach (var options in invalid)
            {
                Assert.Catch<ArgumentException>(() => AudioKit.CrossFadeMusic(_c, 1f, options));
                Assert.IsFalse(AudioManager.HasInstance);
                Assert.IsNull(AudioKit.CurrentSettings);
            }
        }

        [Test]
        public void NullOrDestroyedClipIsANoOpAndMusicControlsArePassiveWithoutHost()
        {
            var destroyed = AudioClip.Create("crossfade-destroyed", 441, 1, 44100, false);
            Object.DestroyImmediate(destroyed);
            var old = AudioKit.PlayMusic(_a);
            var current = AudioKit.CrossFadeMusic(_b, 2f);
            var invalid = Options();
            invalid.Pitch = float.NaN;
            foreach (var clip in new[] { null, destroyed })
            {
                Assert.AreEqual(default(AudioHandle), AudioKit.CrossFadeMusic(clip, 1f, invalid));
                Assert.IsTrue(old.IsValid);
                AssertVolume(current, 0f);
                Assert.AreEqual(current, AudioKit.Music);
            }
            AudioKit.Shutdown();
            Assert.IsFalse(AudioKit.CrossFadeMusic(null, 1f).IsValid);
            AudioKit.StopMusic();
            AudioKit.PauseMusic();
            AudioKit.ResumeMusic();
            Assert.AreEqual(default(AudioHandle), AudioKit.Music);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
        }

        [Test]
        public void OmittedOptionsLoopButExplicitDefaultsAndComplete2DOptionsAreRespected()
        {
            var omitted = AudioKit.CrossFadeMusic(_a, 0f);
            Assert.IsTrue(omitted.Voice.Source.loop);
            var explicitDefaults = AudioKit.CrossFadeMusic(_b, 0f, AudioSoundOptions.Defaults);
            Assert.IsFalse(omitted.IsValid);
            Assert.IsFalse(explicitDefaults.Voice.Source.loop);
            var options = Options(.6f);
            options.Pitch = 1.5f;
            options.PlaySoundMode = (AudioKit.PlaySoundModes)999;
            options.MinDistance = float.NaN;
            options.MaxDistance = -1f;
            options.RolloffMode = (AudioRolloffMode)999;
            options.OutputRoute = AudioOutputRoute.Direct;
            var started = 0;
            var target = AudioKit.CrossFadeMusic(_c, 1f, options, (clip, lease) =>
            {
                started++;
                Assert.AreSame(_c, clip);
                Assert.AreEqual(lease, AudioKit.Music);
                AssertVolume(lease, 0f);
                Assert.AreEqual(1.5f, lease.Voice.Source.pitch);
                Assert.AreEqual(0f, lease.Voice.Source.spatialBlend);
                Assert.IsNull(lease.Voice.Source.outputAudioMixerGroup);
                Assert.IsTrue(lease.Voice.Source.loop);
            });
            Assert.AreEqual(1, started);
            Assert.AreEqual(.6f, target.Voice.Options.Volume);
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [Test]
        public void ZeroDurationStopsBothPriorParticipantsAndSetsFullGainBeforeCallback()
        {
            var finishes = 0;
            var old = AudioKit.PlayMusic(_a, onFinished: (_, __) => finishes++);
            var target = AudioKit.CrossFadeMusic(_b, 3f, onFinished: (_, __) => finishes++);
            var starts = 0;
            var immediate = AudioKit.CrossFadeMusic(_c, 0f, Options(.4f), (_, lease) =>
            {
                starts++;
                Assert.IsFalse(old.IsValid);
                Assert.IsFalse(target.IsValid);
                Assert.AreEqual(lease, AudioKit.Music);
                AssertVolume(lease, .4f);
            });
            Assert.IsTrue(immediate.IsValid);
            Assert.AreEqual(1, starts);
            Assert.AreEqual(0, finishes);
            Assert.AreEqual(2, SourceCount(AudioManager.Instance));
        }

        [Test]
        public void BothLegacyEntrypointsAndStopMusicCancelAllCrossfadeParticipants()
        {
            for (var entry = 0; entry < 2; entry++)
            {
                var old = AudioKit.PlayMusic(_a);
                var target = AudioKit.CrossFadeMusic(_b, 5f);
                AudioKit.PauseMusic();
                var replacement = entry == 0 ? AudioKit.PlayMusic(_c) : AudioKit.PlayMusicWithOptions(_c, Options());
                Assert.IsFalse(old.IsValid);
                Assert.IsFalse(target.IsValid);
                Assert.IsFalse(replacement.IsPaused, "Legacy replacement does not inherit crossfade pause.");
                AssertVolume(replacement, 1f);
                var next = AudioKit.CrossFadeMusic(_a, 5f);
                AudioKit.StopMusic();
                Assert.IsFalse(replacement.IsValid);
                Assert.IsFalse(next.IsValid);
                Assert.IsFalse(AudioKit.Music.IsValid);
                foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
                {
                    Assert.IsNull(source.clip);
                    Assert.IsFalse(source.gameObject.activeSelf);
                    Assert.AreEqual(1f, source.volume);
                }
            }
        }

        [Test]
        public void PausedCrossfadeRequestStartsOnlyOnceAfterWholeChannelResume()
        {
            foreach (var duration in new[] { 0f, 1f })
            {
                AudioKit.StopMusic();
                var old = AudioKit.PlayMusic(_a);
                AudioKit.PauseMusic();
                var starts = 0;
                var target = AudioKit.CrossFadeMusic(_b, duration, onStarted: (_, lease) =>
                {
                    starts++;
                    Assert.AreEqual(lease, AudioKit.Music);
                    AssertVolume(lease, duration == 0f ? 1f : 0f);
                });
                if (duration > 0f)
                {
                    Assert.IsTrue(old.IsPaused);
                }
                else
                {
                    Assert.IsFalse(old.IsValid);
                }

                Assert.IsTrue(target.IsPaused);
                Assert.IsFalse(target.IsPlaying);
                Assert.AreEqual(0, starts);
                Tick(100f, 100f);
                AudioKit.ResumeMusic();
                Assert.IsFalse(old.IsPaused);
                Assert.IsFalse(target.IsPaused);
                Assert.AreEqual(1, starts);
                AudioKit.PauseMusic();
                AudioKit.ResumeMusic();
                Assert.AreEqual(1, starts);
            }
            var outgoing = AudioKit.PlayMusic(_a);
            AudioKit.PauseMusic();
            var reentrantStarts = 0;
            var reentrant = AudioKit.CrossFadeMusic(_b, 1f, onStarted: (_, __) =>
            {
                reentrantStarts++;
                AudioKit.PauseMusic();
            });
            AudioKit.ResumeMusic();
            Assert.AreEqual(1, reentrantStarts);
            Assert.IsTrue(outgoing.IsPaused, "Outer Resume must not undo PauseMusic requested by the start callback.");
            Assert.IsTrue(reentrant.IsPaused);
            Tick(100f, 100f);
            AssertVolume(outgoing, 1f);
            AssertVolume(reentrant, 0f);
            AudioKit.ResumeMusic();
            Assert.AreEqual(1, reentrantStarts);
            Assert.IsFalse(outgoing.IsPaused);
            Assert.IsFalse(reentrant.IsPaused);
        }

        [Test]
        public void StartedExceptionCleansOnlyItsOwnedEpochAndDoesNotUndoNestedPlayback()
        {
            for (var nestedKind = 0; nestedKind < 3; nestedKind++)
            {
                AudioKit.StopMusic();
                var old = AudioKit.PlayMusic(_a);
                AudioHandle failed = default, nested = default;
                var exception = new InvalidOperationException("crossfade-start-failure");
                var kind = nestedKind;
                var thrown = Assert.Throws<InvalidOperationException>(() => AudioKit.CrossFadeMusic(_b, 2f,
                    onStarted: (_, lease) =>
                    {
                        failed = lease;
                        if (kind == 1)
                        {
                            nested = AudioKit.CrossFadeMusic(_c, 2f);
                        }
                        else if (kind == 2)
                        {
                            nested = AudioKit.PlayMusic(_c);
                        }

                        throw exception;
                    }));
                Assert.AreSame(exception, thrown);
                Assert.IsFalse(old.IsValid);
                if (kind == 0)
                {
                    Assert.IsFalse(failed.IsValid);
                    Assert.IsFalse(AudioKit.Music.IsValid);
                    foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
                    {
                        Assert.IsNull(source.clip);
                    }
                }
                else
                {
                    Assert.IsTrue(nested.IsValid);
                    Assert.AreEqual(nested, AudioKit.Music);
                    AssertVolume(nested, kind == 1 ? 0f : 1f);
                    if (kind == 2)
                    {
                        Assert.IsFalse(failed.IsValid);
                    }

                    Assert.AreSame(_c, nested.Voice.Source.clip);
                    Tick(2f, 2f);
                    AssertVolume(nested, 1f);
                    Assert.IsFalse(failed.IsValid);
                }
            }
            for (var directResume = 0; directResume < 2; directResume++)
            {
                AudioKit.StopMusic();
                var old = AudioKit.PlayMusic(_a);
                AudioKit.PauseMusic();
                var starts = 0;
                var exception = new InvalidOperationException("crossfade-deferred-start-failure");
                var pending = AudioKit.CrossFadeMusic(_b, 1f, onStarted: (_, __) =>
                {
                    starts++;
                    throw exception;
                });
                Assert.AreEqual(0, starts);
                var thrown = directResume == 0 ? Assert.Throws<InvalidOperationException>(AudioKit.ResumeMusic)
                    : Assert.Throws<InvalidOperationException>(() => pending.Resume());
                Assert.AreSame(exception, thrown);
                Assert.AreEqual(1, starts);
                Assert.IsFalse(old.IsValid, "Deferred first-start failure must not leave a paused outgoing lease behind.");
                Assert.IsFalse(pending.IsValid);
                Assert.IsFalse(AudioKit.Music.IsValid);
                Tick(100f, 100f);
                foreach (var source in AudioManager.Instance.GetComponentsInChildren<AudioSource>(true))
                {
                    Assert.IsNull(source.clip);
                }
            }
            AudioKit.Settings.IsMusicOn.Value = false;
            var outgoingStarts = 0;
            var outgoingException = new InvalidOperationException("crossfade-outgoing-deferred-start-failure");
            var outgoingPending = AudioKit.PlayMusic(_a, onStarted: (_, __) =>
            {
                outgoingStarts++;
                throw outgoingException;
            });
            AudioKit.PauseMusic();
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(0, outgoingStarts);
            var latestStarts = 0;
            var latest = AudioKit.CrossFadeMusic(_b, 1f, onStarted: (_, __) => latestStarts++);
            Assert.AreSame(outgoingException, Assert.Throws<InvalidOperationException>(AudioKit.ResumeMusic));
            Assert.AreEqual(1, outgoingStarts);
            Assert.AreEqual(1, latestStarts);
            Assert.IsFalse(outgoingPending.IsValid);
            Assert.AreEqual(latest, AudioKit.Music);
            Assert.IsFalse(latest.IsPaused);
            AssertVolume(latest, 0f, "An outgoing deferred callback failure must not clear the newer incoming epoch.");
            Tick(1f, 1f);
            AssertVolume(latest, 1f);
        }

        [Test]
        public void StartedStopOrShutdownCannotBeOverwrittenAndOldSessionCannotControlFreshMusic()
        {
            for (var shutdown = 0; shutdown < 2; shutdown++)
            {
                AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
                AudioKit.Settings.MusicVolume.SetValueWithoutCallback(1f);
                var oldSettings = AudioKit.Settings;
                var old = AudioKit.PlayMusic(_a);
                var closeSession = shutdown != 0;
                var stopped = AudioKit.CrossFadeMusic(_b, 2f, onStarted: (_, __) =>
                {
                    if (closeSession)
                    {
                        AudioKit.Shutdown();
                    }
                    else
                    {
                        AudioKit.StopMusic();
                    }
                });
                Assert.IsFalse(old.IsValid);
                Assert.IsFalse(stopped.IsValid);
                Assert.IsFalse(AudioKit.Music.IsValid);
                if (closeSession)
                {
                    Assert.IsFalse(AudioManager.HasInstance);
                    Assert.IsNull(AudioKit.CurrentSettings);
                }
                AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
                AudioKit.Settings.MusicVolume.SetValueWithoutCallback(1f);
                var fresh = AudioKit.PlayMusic(_c);
                old.Stop();
                stopped.FadeOut(0f);
                if (closeSession)
                {
                    oldSettings.IsMusicOn.Value = false;
                    oldSettings.MusicVolume.Value = .1f;
                    oldSettings.IsMusicMuted.Value = true;
                }
                AssertVolume(fresh, 1f);
                Assert.IsFalse(fresh.Voice.Source.mute);
                Assert.AreEqual(fresh, AudioKit.Music);
                AudioKit.StopMusic();
            }
        }

        [Test]
        public void LinearTransitionHasExactStartMidpointEndpointAndFiniteDurationBoundaries()
        {
            var finishes = 0;
            var old = AudioKit.PlayMusic(_a, onFinished: (_, __) => finishes++);
            var target = AudioKit.CrossFadeMusic(_b, 2f, onFinished: (_, __) => finishes++, curve: AudioCrossfadeCurve.Linear);
            Assert.AreEqual(target, AudioKit.Music);
            AssertVolume(old, 1f);
            AssertVolume(target, 0f);
            Tick(.5f, .5f);
            AssertVolume(old, .75f);
            AssertVolume(target, .25f);
            Tick(.5f, .5f);
            AssertVolume(old, .5f);
            AssertVolume(target, .5f);
            Tick(1f, 1f);
            Assert.IsFalse(old.IsValid);
            AssertVolume(target, 1f);
            Assert.AreEqual(0, finishes);
            Tick(10f, 10f);
            AssertVolume(target, 1f);
            foreach (var duration in new[] { float.Epsilon, float.MaxValue })
            {
                AudioKit.StopMusic();
                target = AudioKit.CrossFadeMusic(_c, duration, curve: AudioCrossfadeCurve.Linear);
                AssertVolume(target, 0f, "Positive duration fades in even without an outgoing track.");
                Tick(duration, duration);
                AssertVolume(target, 1f);
                Assert.IsFalse(float.IsNaN(target.Voice.Source.volume));
            }
        }

        [Test]
        public void RepeatedRetargetingCapturesCurrentFractionAndReusesAtMostTwoSources()
        {
            var a = AudioKit.PlayMusic(_a);
            var host = AudioManager.Instance;
            Assert.AreEqual(1, SourceCount(host), "Second music source is lazy.");
            var b = AudioKit.CrossFadeMusic(_b, 2f, curve: AudioCrossfadeCurve.Linear);
            Tick(.5f, .5f);
            AssertVolume(a, .75f);
            AssertVolume(b, .25f);
            var c = AudioKit.CrossFadeMusic(_c, 2f, curve: AudioCrossfadeCurve.Linear);
            Assert.IsFalse(a.IsValid);
            Assert.IsTrue(b.IsValid);
            Assert.AreEqual(c, AudioKit.Music);
            AssertVolume(b, .25f);
            AssertVolume(c, 0f);
            Tick(.5f, .5f);
            AssertVolume(b, .1875f, "Outgoing starts at its captured transition fraction, not full gain.");
            AssertVolume(c, .25f);
            var firstSource = b.Voice.Source;
            var secondSource = c.Voice.Source;
            for (var i = 0; i < 24; i++)
            {
                var previousTail = b;
                b = c;
                var capturedGain = b.Voice.Source.volume;
                c = AudioKit.CrossFadeMusic(i % 2 == 0 ? _a : _b, 1f, curve: AudioCrossfadeCurve.Linear);
                Assert.IsFalse(previousTail.IsValid);
                Assert.IsTrue(b.IsValid);
                AssertVolume(b, capturedGain);
                AssertVolume(c, 0f);
                Assert.AreEqual(c, AudioKit.Music);
                Assert.AreEqual(2, SourceCount(host));
                Assert.IsTrue(ReferenceEquals(c.Voice.Source, firstSource) || ReferenceEquals(c.Voice.Source, secondSource));
                Tick(.25f, .25f);
                AssertVolume(b, capturedGain * .75f);
                AssertVolume(c, .25f);
            }
            Tick(.75f, .75f);
            Assert.IsFalse(b.IsValid);
            AssertVolume(c, 1f);
            a.Stop();
            b.Stop();
            AssertVolume(c, 1f, "Expired leases cannot stop a source reused by the latest target.");
            Assert.AreEqual(0, AudioKit.SoundCount);
        }

        [Test]
        public void ClockSelectionPauseAndInvalidParticipantHaveIndependentSemantics()
        {
            for (var scaled = 0; scaled < 2; scaled++)
            {
                AudioKit.StopMusic();
                var target = scaled == 0 ? AudioKit.CrossFadeMusic(_a, 2f, curve: AudioCrossfadeCurve.Linear)
                    : AudioKit.CrossFadeMusic(_a, 2f, ignoreTimeScale: false, curve: AudioCrossfadeCurve.Linear);
                Tick(0f, .5f);
                AssertVolume(target, scaled == 0 ? .25f : 0f);
                Tick(.5f, .5f);
                AssertVolume(target, scaled == 0 ? .5f : .25f);
            }
            for (var stopIncoming = 0; stopIncoming < 2; stopIncoming++)
            {
                AudioKit.StopMusic();
                var old = AudioKit.PlayMusic(_a);
                var current = AudioKit.CrossFadeMusic(_b, 2f, curve: AudioCrossfadeCurve.Linear);
                Tick(.5f, .5f);
                AudioKit.PauseMusic();
                Tick(100f, 100f);
                AssertVolume(old, .75f);
                AssertVolume(current, .25f);
                AudioKit.ResumeMusic();
                var paused = stopIncoming == 0 ? old : current;
                paused.Pause();
                Tick(100f, 100f);
                AssertVolume(old, .75f);
                AssertVolume(current, .25f);
                paused.Stop();
                Tick(.5f, .5f);
                if (stopIncoming == 0)
                {
                    AssertVolume(current, .5f);
                }
                else
                {
                    AssertVolume(old, .5f);
                    Assert.IsFalse(AudioKit.Music.IsValid, "A stopped incoming request must not resurrect its outgoing track.");
                }
                Tick(1f, 1f);
                Assert.IsFalse(old.IsValid);
                if (stopIncoming == 0)
                {
                    AssertVolume(current, 1f);
                }
                else
                {
                    Assert.IsFalse(AudioKit.Music.IsValid);
                }
            }
        }

        [Test]
        public void MusicOffCancelsOnlyTransitionAndPreservesManualEnvelopeForLatestPendingRequest()
        {
            var old = AudioKit.PlayMusic(_a);
            var starts = 0;
            var current = AudioKit.CrossFadeMusic(_b, 2f, onStarted: (_, __) => starts++, curve: AudioCrossfadeCurve.Linear);
            current.FadeOut(2f, stopWhenDone: false);
            current.Voice.TickFade(.5f, .5f);
            Tick(.5f, .5f);
            AssertVolume(current, .1875f);
            AudioKit.Settings.IsMusicOn.Value = false;
            Assert.IsFalse(old.IsValid);
            AssertVolume(current, .75f, "Off restores transition gain, not the request's manual gain.");
            Tick(100f, 100f);
            current.Voice.TickFade(100f, 100f);
            AssertVolume(current, .75f);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(1, starts);
            AssertVolume(current, .75f);
            current.Voice.TickFade(.5f, .5f);
            AssertVolume(current, .5f);
            AudioKit.Settings.IsMusicOn.Value = false;
            var pendingStarts = 0;
            var pending = AudioKit.CrossFadeMusic(_c, 2f, Options(.6f), (_, __) => pendingStarts++);
            Assert.IsFalse(current.IsValid);
            AssertVolume(pending, .6f);
            Tick(100f, 100f);
            Assert.AreEqual(0, pendingStarts);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.AreEqual(1, pendingStarts);
            AssertVolume(pending, .6f);
            Tick(.5f, .5f);
            AssertVolume(pending, .6f, "A request made while off is normal pending replacement, not a queued transition.");
        }

        [Test]
        public void ManualRequestEnvelopeMultipliesTransitionAfterCategoryClampingAndMuteDoesNotFreeze()
        {
            AudioKit.Settings.MusicVolume.Value = .8f;
            var old = AudioKit.PlayMusic(_a, volume: 3f);
            old.FadeOut(4f, stopWhenDone: false);
            old.Voice.TickFade(1f, 1f);
            AssertVolume(old, .75f);
            var current = AudioKit.CrossFadeMusic(_b, 2f, Options(3f), (_, lease) => lease.FadeIn(2f), curve: AudioCrossfadeCurve.Linear);
            current.Voice.TickFade(.5f, .5f);
            Tick(.5f, .5f);
            AssertVolume(old, .5625f);
            AssertVolume(current, .0625f, "Clamp(category * request), then multiply both independent gains.");
            AudioKit.Settings.MusicVolume.Value = .2f;
            AssertVolume(old, .3375f);
            AssertVolume(current, .0375f);
            AudioKit.Settings.VoiceVolume.Value = 0f;
            old.SetVolumeScale(.5f);
            AssertVolume(old, .05625f, "An outgoing music lease still reads the Music category.");
            AudioKit.Settings.IsMusicMuted.Value = true;
            Assert.IsTrue(old.Voice.Source.mute);
            Assert.IsTrue(current.Voice.Source.mute);
            Tick(.5f, .5f);
            AssertVolume(old, .0375f);
            AssertVolume(current, .075f);
            current.SetVolumeScale(.5f);
            AssertVolume(current, .0125f);
            current.FadeOut(1f, stopWhenDone: false);
            current.Voice.TickFade(.5f, .5f);
            AssertVolume(current, .00625f);
            Tick(1f, 1f);
            Assert.IsFalse(old.IsValid);
            AssertVolume(current, .0125f, "Transition completion does not cancel the remaining manual envelope.");
            AudioKit.Settings.IsMusicMuted.Value = false;
            current.Voice.TickFade(.5f, .5f);
            AssertVolume(current, 0f);
            Assert.IsTrue(current.IsValid);
        }

        [Test]
        public void TailInheritsMusicRoutingWhileTargetOverridesAndDirectRoutesStayIndependent()
        {
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            Assert.IsNotNull(mixer);
            var music = mixer.FindMatchingGroups("Music")[0];
            var voice = mixer.FindMatchingGroups("Voice")[0];
            var ui = mixer.FindMatchingGroups("UI")[0];
            var routes = new[] { AudioOutputRoute.Inherit, AudioOutputRoute.ToMixer(ui), AudioOutputRoute.Direct };
            for (var i = 0; i < routes.Length; i++)
            {
                AudioKit.StopMusic();
                AudioKit.Settings.MusicOutputGroup.Value = music;
                AudioKit.Settings.VoiceOutputGroup.Value = voice;
                AudioKit.Settings.MusicVolume.Value = .6f;
                AudioKit.Settings.VoiceVolume.Value = .1f;
                var old = AudioKit.PlayMusic(_a);
                var options = Options();
                options.OutputRoute = routes[i];
                var current = AudioKit.CrossFadeMusic(_b, 2f, options, curve: AudioCrossfadeCurve.Linear);
                Tick(.5f, .5f);
                Assert.AreSame(music, old.Voice.Source.outputAudioMixerGroup);
                Assert.AreSame(i == 0 ? music : i == 1 ? ui : null, current.Voice.Source.outputAudioMixerGroup);
                AssertVolume(old, .45f);
                AssertVolume(current, .15f);
                AudioKit.Settings.VoiceOutputGroup.Value = music;
                AudioKit.Settings.MusicOutputGroup.Value = ui;
                Assert.AreSame(ui, old.Voice.Source.outputAudioMixerGroup);
                Assert.AreSame(i == 2 ? null : ui, current.Voice.Source.outputAudioMixerGroup);
                AudioKit.Settings.MusicVolume.Value = .4f;
                AssertVolume(old, .3f);
                AssertVolume(current, .1f);
                AudioKit.ClearMixerRouting();
                Assert.IsNull(old.Voice.Source.outputAudioMixerGroup);
                Assert.AreSame(i == 1 ? ui : null, current.Voice.Source.outputAudioMixerGroup);
                AssertVolume(old, .3f);
                AssertVolume(current, .1f);
            }
        }

        private static AudioCrossfadeCurve CurrentCurve()
        {
            var property = typeof(AudioManager).GetProperty("MusicPlayback", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(property);
            var playback = property.GetValue(AudioManager.Instance);
            var curve = playback.GetType().GetProperty("CurrentCurve", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(curve);
            return (AudioCrossfadeCurve)curve.GetValue(playback);
        }

        [Test]
        public void EqualPowerMatchesQuarterWaveGainsAndResetsCurveAfterCompletion()
        {
            var old = AudioKit.PlayMusic(_a);
            var target = AudioKit.CrossFadeMusic(_b, 4f, curve: AudioCrossfadeCurve.EqualPower);
            Assert.AreEqual(AudioCrossfadeCurve.EqualPower, CurrentCurve());
            AssertVolume(old, 1f);
            AssertVolume(target, 0f);
            Tick(1f, 1f);
            AssertVolume(old, .923880f);
            AssertVolume(target, .382683f);
            Tick(1f, 1f);
            AssertVolume(old, .707107f);
            AssertVolume(target, .707107f);
            Assert.AreEqual(1f, old.Voice.Source.volume * old.Voice.Source.volume + target.Voice.Source.volume * target.Voice.Source.volume, .0001f);
            Tick(1f, 1f);
            AssertVolume(old, .382683f);
            AssertVolume(target, .923880f);
            Tick(1f, 1f);
            Assert.IsFalse(old.IsValid);
            AssertVolume(target, 1f);
            Assert.AreEqual(AudioCrossfadeCurve.Linear, CurrentCurve(), "A completed transition resets the reported curve.");
            var immediateOld = AudioKit.PlayMusic(_a);
            var immediate = AudioKit.CrossFadeMusic(_b, 0f, curve: AudioCrossfadeCurve.EqualPower);
            Assert.IsFalse(immediateOld.IsValid);
            AssertVolume(immediate, 1f, "Zero duration replaces immediately and ignores the curve.");
        }

        [Test]
        public void ChainedEqualPowerOutgoingContinuesFromCapturedGainWithoutJump()
        {
            var a = AudioKit.PlayMusic(_a);
            var b = AudioKit.CrossFadeMusic(_b, 4f, curve: AudioCrossfadeCurve.EqualPower);
            Tick(1f, 1f);
            AssertVolume(a, .923880f);
            AssertVolume(b, .382683f);
            var c = AudioKit.CrossFadeMusic(_c, 2f, curve: AudioCrossfadeCurve.EqualPower);
            Assert.IsFalse(a.IsValid);
            AssertVolume(b, .382683f, "The captured outgoing gain must continue without a jump.");
            AssertVolume(c, 0f);
            Assert.AreEqual(c, AudioKit.Music);
            Tick(.5f, .5f);
            AssertVolume(b, .353553f);
            AssertVolume(c, .382683f);
            Tick(1.5f, 1.5f);
            Assert.IsFalse(b.IsValid);
            AssertVolume(c, 1f);
        }

        [Test]
        public void OmittedCurveUsesEqualPowerAndExplicitLinearKeepsItsMathBeforeInvalidCurveThrows()
        {
            var defaultOld = AudioKit.PlayMusic(_a);
            var defaultTarget = AudioKit.CrossFadeMusic(_b, 2f);
            Assert.AreEqual(AudioCrossfadeCurve.EqualPower, CurrentCurve());
            AssertVolume(defaultOld, 1f);
            AssertVolume(defaultTarget, 0f);
            Tick(1f, 1f);
            AssertVolume(defaultOld, .707107f);
            AssertVolume(defaultTarget, .707107f);
            Assert.AreEqual(1f, defaultOld.Voice.Source.volume * defaultOld.Voice.Source.volume +
                defaultTarget.Voice.Source.volume * defaultTarget.Voice.Source.volume, .0001f,
                "Omitting curve keeps unit power at the midpoint, rather than the Linear amplitude sum.");
            AudioKit.StopMusic();
            var old = AudioKit.PlayMusic(_a);
            var firstLinear = AudioKit.CrossFadeMusic(_b, 2f, curve: AudioCrossfadeCurve.Linear);
            Tick(1f, 1f);
            AssertVolume(old, .5f);
            AssertVolume(firstLinear, .5f);
            var explicitLinear = AudioKit.CrossFadeMusic(_c, 2f, curve: AudioCrossfadeCurve.Linear);
            Assert.IsFalse(old.IsValid);
            AssertVolume(firstLinear, .5f);
            AssertVolume(explicitLinear, 0f);
            Assert.AreEqual(explicitLinear, AudioKit.Music);
            Tick(1f, 1f);
            AssertVolume(firstLinear, .25f);
            AssertVolume(explicitLinear, .5f);

            var currentCurve = CurrentCurve();
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.CrossFadeMusic(_a, 1f, curve: (AudioCrossfadeCurve)2));
            Assert.AreEqual("curve", thrown.ParamName);
            Assert.IsTrue(firstLinear.IsValid);
            Assert.IsTrue(explicitLinear.IsValid);
            Assert.AreEqual(explicitLinear, AudioKit.Music);
            Assert.AreEqual(currentCurve, CurrentCurve());

            AudioKit.Shutdown();
            Assert.Throws<ArgumentOutOfRangeException>(() => AudioKit.CrossFadeMusic(null, 1f, curve: (AudioCrossfadeCurve)2));
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
        }
    }
}
