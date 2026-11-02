using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioNamedBorrowedTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;
        private AudioClip _originalClip;
        private GameObject _external;
        private AudioSource _source;
        private Provider _provider;

        private sealed class Owner : IDisposable
        {
            internal int Releases;
            internal Action Release;
            public void Dispose()
            {
                Releases++;
                Release?.Invoke();
            }
        }
        private sealed class Provider : IAudioClipProvider
        {
            internal readonly Dictionary<string, Action<AudioClipLease, Exception>> Pending = new Dictionary<string, Action<AudioClipLease, Exception>>();
            internal readonly List<Owner> Owners = new List<Owner>();
            private readonly AudioClip _clip;
            internal int Loads;
            internal Exception Failure;
            public bool CanCache => false;
            internal Provider(AudioClip clip)
            {
                _clip = clip;
            }
            public AudioClipLease Load(string key)
            {
                Loads++;
                if (Failure != null)
                {
                    throw Failure;
                }

                return Lease();
            }
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                Loads++;
                Pending.Add(key, completed);
                progress(.4f);
            }
            private AudioClipLease Lease()
            {
                var owner = new Owner();
                Owners.Add(owner);
                return new AudioClipLease(_clip, owner);
            }
            internal void Complete(string key, Exception failure = null) => Pending[key](failure == null ? Lease() : null, failure);
        }

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("named-borrowed", 44100, 1, 44100, false);
            _originalClip = AudioClip.Create("caller-original", 44100, 1, 44100, false);
            _external = new GameObject("named-borrowed-caller", typeof(AudioSource));
            _source = _external.GetComponent<AudioSource>();
            _source.clip = _originalClip;
            _source.volume = .37f;
            _source.pitch = .8f;
            _source.loop = true;
            _source.mute = true;
            _source.spatialBlend = .6f;
            _provider = new Provider(_clip);
        }
        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            Object.DestroyImmediate(_external);
            Object.DestroyImmediate(_clip);
            Object.DestroyImmediate(_originalClip);
        }
        private AudioClipAddress Address(string key) => new AudioClipAddress(_provider, key);

        // 校验正式公开入口的完整签名，同时保留原异常类型供行为断言。
        private object Play(AudioChannel channel, bool asynchronous, AudioClipAddress address,
            AudioSource source, Action<AudioClip, AudioHandle> started = null)
        {
            var name = asynchronous ? "PlayBorrowedAsync" : "PlayBorrowedNamed";
            var method = typeof(AudioChannel).GetMethod(name, new[]
            {
                typeof(AudioSource), typeof(AudioClipAddress), typeof(AudioSoundOptions),
                typeof(Action<AudioClip, AudioHandle>), typeof(Action<AudioClip, AudioHandle>)
            });
            Assert.NotNull(method, name + " must use the existing named request pipeline.");
            try
            {
                return method.Invoke(channel, new object[] { source, address, AudioSoundOptions.MusicDefaults, started, null });
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
        private AudioHandle Named(AudioChannel channel, string key, Action<AudioClip, AudioHandle> started = null) =>
            (AudioHandle)Play(channel, false, Address(key), _source, started);
        private AudioPlayRequest Async(AudioChannel channel, string key, Action<AudioClip, AudioHandle> started = null) =>
            (AudioPlayRequest)Play(channel, true, Address(key), _source, started);

        [Test]
        public void SynchronousBorrowBindsResourceAndRestoresOnlyUnchangedCallerFields()
        {
            var channel = AudioKit.RegisterChannel("borrowed-sync");
            var handle = Named(channel, "sync", (_, original) =>
            {
                Assert.AreSame(_source, original.Voice.Source);
                Assert.AreEqual(0, _provider.Owners[0].Releases);
            });
            Assert.IsTrue(handle.IsValid);
            Assert.AreSame(_clip, _source.clip);
            _source.pitch = 1.4f;
            handle.Stop();
            handle.Stop();
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(.37f, _source.volume);
            Assert.AreEqual(1.4f, _source.pitch);
            Assert.IsTrue(_source.loop);
            Assert.IsTrue(_source.mute);
            Assert.AreEqual(.6f, _source.spatialBlend);
            Assert.IsTrue(_source.enabled);
            Assert.IsTrue(_external.activeSelf);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void InvalidBorrowIsRejectedBeforeLoadingOrReplacingOldPlayback(int invalid)
        {
            var channel = AudioKit.RegisterChannel("validation", mode: invalid == 3 ? AudioChannelMode.Overlap : AudioChannelMode.Exclusive);
            var old = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            if (invalid == 1)
            {
                _source.enabled = false;
            }

            if (invalid == 2)
            {
                _source.Play();
            }

            if (invalid == 0)
            {
                Assert.Throws<ArgumentNullException>(() => Play(channel, true, Address("invalid"), null));
            }
            else if (invalid == 1)
            {
                Assert.Throws<ArgumentException>(() => Async(channel, "invalid"));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => Async(channel, "invalid"));
            }

            Assert.AreEqual(0, _provider.Loads);
            Assert.IsTrue(old.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
        }

        [Test]
        public void LoadingDoesNotReserveSourceAndOneCanceledWaiterCannotStopItsNewBorrowerOrSharedPeer()
        {
            var channel = AudioKit.RegisterChannel("waiting");
            var old = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            var waiting = Async(channel, "shared");
            Assert.AreEqual(AudioPlayState.Loading, waiting.State);
            Assert.IsFalse(waiting.Handle.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(.37f, _source.volume);
            Assert.IsTrue(old.IsValid);
            var borrower = AudioKit.RegisterChannel("other-borrower").PlayBorrowed(_source, _originalClip, AudioSoundOptions.MusicDefaults);
            var peer = AudioKit.PlaySoundAsync(Address("shared"), AudioSoundOptions.MusicDefaults);
            waiting.Cancel();
            _provider.Complete("shared");
            Assert.AreEqual(AudioPlayEnd.Canceled, waiting.EndReason);
            Assert.IsTrue(old.IsValid);
            Assert.IsTrue(borrower.IsValid);
            Assert.IsTrue(peer.Handle.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            peer.Cancel();
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.IsTrue(borrower.IsValid);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LoadingFailurePreservesOldRequestAndDoesNotModifySource(bool asynchronous)
        {
            var channel = AudioKit.RegisterChannel("failed-load");
            var old = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            var failure = new InvalidOperationException("expected borrowed load failure");
            if (asynchronous)
            {
                var request = Async(channel, "failure");
                _provider.Complete("failure", failure);
                Assert.AreEqual(AudioPlayEnd.Failed, request.EndReason);
                Assert.AreSame(failure, request.Error);
            }
            else
            {
                _provider.Failure = failure;
                Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() => Named(channel, "failure")));
            }
            Assert.IsTrue(old.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(.37f, _source.volume);
        }

        [Test]
        public void SubmissionRechecksSourceOccupancyBeforeStoppingOldRequest()
        {
            var channel = AudioKit.RegisterChannel("commit-validation");
            var old = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            var waiting = Async(channel, "occupied-later");
            var borrower = AudioKit.RegisterChannel("new-borrower").PlayBorrowed(_source, _originalClip, AudioSoundOptions.MusicDefaults);
            _provider.Complete("occupied-later");
            Assert.AreEqual(AudioPlayEnd.Failed, waiting.EndReason);
            Assert.IsInstanceOf<InvalidOperationException>(waiting.Error);
            Assert.IsFalse(waiting.HasStarted);
            Assert.IsFalse(waiting.Handle.IsValid);
            Assert.IsTrue(old.IsValid);
            Assert.IsTrue(borrower.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PendingSourceLossEndsEvenBeforeProducerCompletes(bool destroy)
        {
            var channel = AudioKit.RegisterChannel("lost-pending-source");
            var old = channel.Play(_clip, AudioSoundOptions.MusicDefaults);
            var waiting = Async(channel, "late-source-loss");
            if (destroy)
            {
                Object.DestroyImmediate(_source);
            }
            else
            {
                _source.enabled = false;
            }

            waiting.Tick();
            Assert.AreEqual(AudioPlayEnd.Unavailable, waiting.EndReason);
            Assert.IsFalse(waiting.Handle.IsValid);
            _provider.Complete("late-source-loss");
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.IsTrue(old.IsValid);
        }

        [Test]
        public void LatestExclusiveBorrowCommitsOnceAndLateResultCannotRestoreItsSource()
        {
            var channel = AudioKit.RegisterChannel("ordered-borrow");
            var slow = Async(channel, "slow");
            var fast = Async(channel, "fast");
            _provider.Complete("fast");
            _provider.Complete("slow");
            Assert.AreEqual(AudioPlayEnd.Replaced, slow.EndReason);
            Assert.IsTrue(fast.Handle.IsValid);
            Assert.AreSame(_source, fast.Handle.Voice.Source);
            Assert.AreSame(_clip, _source.clip);
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            Assert.AreEqual(1, _provider.Owners[1].Releases);
            fast.Cancel();
            Assert.AreEqual(AudioPlayEnd.Canceled, fast.EndReason);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.AreSame(_originalClip, _source.clip);
        }

        [Test]
        public void StartedSelfStopAndReborrowCannotRestoreOverNewLease()
        {
            var channel = AudioKit.RegisterChannel("started-reentry");
            AudioHandle next = default;
            var request = Async(channel, "reentry", (_, original) =>
            {
                original.Stop();
                next = channel.PlayBorrowed(_source, _originalClip, new AudioSoundOptions { Loop = true, Volume = .6f });
            });
            _provider.Complete("reentry");
            Assert.AreEqual(AudioPlayEnd.Stopped, request.EndReason);
            Assert.IsTrue(request.HasStarted);
            Assert.IsFalse(request.Handle.IsValid);
            Assert.IsTrue(next.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(.6f, _source.volume, .001f);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            request.Cancel();
            Assert.IsTrue(next.IsValid);
        }

        [Test]
        public void StartedFailureReturnsResourceAndRestoresBorrowedFields()
        {
            var channel = AudioKit.RegisterChannel("started-failure");
            var failure = new InvalidOperationException("expected borrowed started failure");
            var request = Async(channel, "started-failure", (_, __) => throw failure);
            _provider.Complete("started-failure");
            Assert.AreEqual(AudioPlayEnd.Failed, request.EndReason);
            Assert.AreSame(failure, request.Error);
            Assert.IsFalse(request.Handle.IsValid);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(.37f, _source.volume);
        }

        [Test]
        public void CancelDuringOldResourceReleasePreventsBorrowedCommit()
        {
            var channel = AudioKit.RegisterChannel("cancel-commit");
            channel.PlayNamed(Address("old"), AudioSoundOptions.MusicDefaults);
            var waiting = Async(channel, "new");
            _provider.Owners[0].Release = waiting.Cancel;
            _provider.Complete("new");
            Assert.AreEqual(AudioPlayEnd.Canceled, waiting.EndReason);
            Assert.IsFalse(waiting.HasStarted);
            Assert.IsFalse(waiting.Handle.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(1, _provider.Owners[1].Releases);
        }

        [Test]
        public void ReplacementReleaseReentryKeepsTheNewSourceOwnerWithoutRollingBackOldPlayback()
        {
            var channel = AudioKit.RegisterChannel("release-conflict");
            var nestedChannel = AudioKit.RegisterChannel("nested-source-owner");
            var old = channel.PlayNamed(Address("old"), AudioSoundOptions.MusicDefaults);
            var waiting = Async(channel, "new");
            AudioHandle nested = default;
            _provider.Owners[0].Release = () => nested = nestedChannel.PlayBorrowed(_source, _originalClip,
                new AudioSoundOptions { Loop = true, Volume = .55f });
            _provider.Complete("new");
            Assert.IsFalse(old.IsValid, "An irreversible replacement is not rolled back after resource release reentry.");
            Assert.AreEqual(AudioPlayEnd.Failed, waiting.EndReason);
            Assert.IsInstanceOf<InvalidOperationException>(waiting.Error);
            Assert.IsFalse(waiting.HasStarted);
            Assert.IsFalse(waiting.Handle.IsValid);
            Assert.IsTrue(nested.IsValid);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(.55f, _source.volume, .001f);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.AreEqual(1, _provider.Owners[1].Releases);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ActiveSourceLossReturnsResourceWithoutDestroyingCallerObject(bool destroy)
        {
            var channel = AudioKit.RegisterChannel("active-source-loss");
            var request = Async(channel, "active");
            _provider.Complete("active");
            if (destroy)
            {
                Object.DestroyImmediate(_source);
            }
            else
            {
                _source.enabled = false;
            }

            request.Handle.Voice.CheckFinished();
            Assert.AreEqual(AudioPlayEnd.Unavailable, request.EndReason);
            Assert.IsFalse(request.Handle.IsValid);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.IsTrue(_external);
            Assert.AreEqual(0, channel.RequestCount);
        }

        [Test]
        public void ShutdownAndThrowingReleaseCannotRestoreOrStopReplacementCallerPlayback()
        {
            var channel = AudioKit.RegisterChannel("shutdown-reentry");
            var request = Async(channel, "shutdown", (_, __) =>
            {
                AudioKit.Shutdown();
                _source.volume = .22f;
            });
            _provider.Complete("shutdown");
            Assert.AreEqual(AudioPlayEnd.Shutdown, request.EndReason);
            Assert.AreEqual(.22f, _source.volume);
            Assert.AreSame(_originalClip, _source.clip);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            channel = AudioKit.RegisterChannel("release-failure");
            var handle = Named(channel, "release-failure");
            _provider.Owners[1].Release = () => throw new InvalidOperationException("expected borrowed release failure");
            LogAssert.Expect(LogType.Exception, new Regex("expected borrowed release failure"));
            handle.Stop();
            handle.Stop();
            Assert.IsFalse(handle.IsValid);
            Assert.AreEqual(1, _provider.Owners[1].Releases);
            Assert.AreEqual(.22f, _source.volume);
            Assert.AreSame(_originalClip, _source.clip);
        }
    }
}
