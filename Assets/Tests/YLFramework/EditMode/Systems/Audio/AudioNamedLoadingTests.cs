using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Audio;

namespace YLFramework.Tests.Audio
{
    public class AudioNamedLoadingTests
    {
        private AudioTestSettings _settings;
        private AudioClip _clip;
        private Provider _provider;
        private string _backgroundPath;

        [SetUp]
        public void SetUp()
        {
            _settings = new AudioTestSettings();
            _clip = AudioClip.Create("named", 44100, 1, 44100, false);
            _provider = new Provider(_clip);
        }
        [TearDown]
        public void TearDown()
        {
            _settings.Dispose();
            if (_backgroundPath != null)
            {
                AssetDatabase.DeleteAsset(_backgroundPath);
            }

            UnityEngine.Object.DestroyImmediate(_clip);
        }

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
            internal bool Immediate;
            internal Action OnLoad;
            public bool CanCache => true;
            internal Provider(AudioClip clip)
            {
                _clip = clip;
            }
            public AudioClipLease Load(string key)
            {
                Loads++;
                OnLoad?.Invoke();
                var owner = new Owner();
                Owners.Add(owner);
                return new AudioClipLease(_clip, owner);
            }
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                if (Immediate)
                {
                    completed(Load(key), null);
                    return;
                }
                Loads++;
                Pending.Add(key, completed);
                progress(.4f);
            }
            internal AudioClipLease Complete(string key, Exception error = null)
            {
                var owner = new Owner();
                Owners.Add(owner);
                var lease = error == null ? new AudioClipLease(_clip, owner) : null;
                Pending[key](lease, error);
                return lease;
            }
        }
        private AudioClipAddress Address(string key) => new AudioClipAddress(_provider, key);

        private AudioPlayRequest Waiting(AudioCategory category, AudioChannel channel = null)
        {
            _backgroundPath = "Assets/Tests/YLFramework/Fixtures/Audio/ReentryData-" + Guid.NewGuid().ToString("N") + ".wav";
            const int Samples = 44100 * 120;
            using (var writer = new BinaryWriter(File.Create(global::YLFramework.Testing.PackageTestPaths.Physical(_backgroundPath))))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + Samples * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(44100);
                writer.Write(88200);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(Samples * 2);
                writer.Write(new byte[Samples * 2]);
            }
            AssetDatabase.ImportAsset(_backgroundPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (AudioImporter)AssetImporter.GetAtPath(_backgroundPath);
            importer.loadInBackground = true;
            var settings = importer.defaultSampleSettings;
            settings.preloadAudioData = false;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.PCM;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(_backgroundPath);
            if (clip.loadState == AudioDataLoadState.Loaded)
            {
                Assert.IsTrue(clip.UnloadAudioData());
            }

            _provider = new Provider(clip);
            var request = channel != null ? channel.PlayAsync(Address("waiting")) : category == AudioCategory.Sound ?
                AudioKit.PlaySoundAsync(Address("waiting")) : AudioKit.PlayMusicAsync(Address("waiting"));
            _provider.Complete("waiting");
            Assert.AreEqual(AudioPlayState.Ready, request.State, "Real background audio must remain in the data-wait phase for this synchronous reentry test.");
            return request;
        }

        [Test]
        public void StopSoundUsesEntrySnapshotsBeforePendingResourceRelease()
        {
            var pending = Waiting(AudioCategory.Sound);
            AudioHandle created = default;
            _provider.Owners[0].Release = () => created = AudioKit.PlaySound(_clip, AudioSoundOptions.MusicDefaults);
            AudioKit.StopAllSound();
            Assert.AreEqual(AudioPlayEnd.Stopped, pending.EndReason);
            Assert.IsTrue(created.IsValid);
        }

        [Test]
        public void OldStopCannotEnterSessionCreatedDuringPendingRelease()
        {
            var pending = Waiting(AudioCategory.Music);
            AudioHandle current = default;
            _provider.Owners[0].Release = () =>
            {
                AudioKit.Shutdown();
                current = AudioKit.PlayMusic(_clip);
            };
            AudioKit.StopMusic();
            Assert.IsTrue(current.IsValid);
            Assert.AreEqual(AudioPlayEnd.Stopped, pending.EndReason);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ChannelStopAndDirectReplacementDoNotOverrideReleaseReentry(bool replace)
        {
            var channel = AudioKit.RegisterChannel("pending-stop");
            var pending = Waiting(AudioCategory.Music, channel);
            AudioHandle created = default;
            _provider.Owners[0].Release = () => created = channel.Play(_clip);
            if (replace)
            {
                Assert.IsFalse(channel.Play(_clip).IsValid);
            }
            else
            {
                channel.Stop();
            }

            Assert.AreEqual(replace ? AudioPlayEnd.Replaced : AudioPlayEnd.Stopped, pending.EndReason);
            Assert.IsTrue(created.IsValid);
        }

        [Test]
        public void UnrelatedVoiceStopCannotInvalidateNewMusicLoading()
        {
            Waiting(AudioCategory.Music);
            _provider.Owners[0].Release = AudioKit.StopVoice;
            var next = AudioKit.PlayMusicAsync(Address("next"));
            Assert.AreEqual(AudioPlayState.Loading, next.State);
            Assert.IsTrue(_provider.Pending.ContainsKey("next"));
        }

        [Test]
        public void SameTopologyStopDuringReplacementCleanupInvalidatesOuterPending()
        {
            Waiting(AudioCategory.Music);
            _provider.Owners[0].Release = AudioKit.StopMusic;
            var next = AudioKit.PlayMusicAsync(Address("stopped-during-admission"));
            Assert.IsTrue(next.IsDone);
            Assert.IsFalse(_provider.Pending.ContainsKey("stopped-during-admission"));
        }

        [Test]
        public void SynchronousProducerFailureNotifiesItsReenteredWaiter()
        {
            AudioPlayRequest inner = null;
            var failure = new InvalidOperationException("sync producer failure");
            _provider.OnLoad = () =>
            {
                inner = AudioKit.PlaySoundAsync(Address("sync-failure"));
                throw failure;
            };
            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() => AudioKit.AcquireClip(Address("sync-failure"))));
            Assert.AreEqual(AudioPlayEnd.Failed, inner.EndReason);
            Assert.AreSame(failure, inner.Error);
            Assert.IsFalse(inner.Handle.IsValid);
        }

        [Test]
        public void CancelDuringOldResourceReleasePreventsNewCommit()
        {
            AudioKit.PlayMusicNamed(Address("old"));
            var next = AudioKit.PlayMusicAsync(Address("new"));
            _provider.Owners[0].Release = next.Cancel;
            _provider.Complete("new");
            Assert.AreEqual(AudioPlayEnd.Canceled, next.EndReason);
            Assert.IsFalse(next.Handle.IsValid);
            Assert.IsFalse(next.HasStarted);
            Assert.AreEqual(1, _provider.Owners[1].Releases);
        }

        [Test]
        public void IdleEvictionCannotUnloadResourceReacquiredByEarlierRelease()
        {
            AudioKit.ClipCacheCapacity = 2;
            AudioKit.ClipCacheIdleSeconds = 100;
            AudioKit.AcquireClip(Address("one")).Dispose();
            AudioKit.AcquireClip(Address("two")).Dispose();
            AudioClipLease reentered = null;
            _provider.Owners[0].Release = () => reentered = AudioKit.AcquireClip(Address("two"));
            AudioKit.ClipCacheCapacity = 0;
            Assert.NotNull(reentered);
            Assert.AreEqual(0, _provider.Owners[1].Releases);
            reentered.Dispose();
            Assert.AreEqual(1, _provider.Owners[1].Releases);
        }

        [Test]
        public void SynchronousProducerNotifiesReenteredAsynchronousWaiter()
        {
            AudioPlayRequest inner = null;
            _provider.OnLoad = () => inner = AudioKit.PlaySoundAsync(Address("sync"), AudioSoundOptions.MusicDefaults);
            var acquired = AudioKit.AcquireClip(Address("sync"));
            Assert.AreEqual(1, _provider.Loads);
            Assert.IsTrue(inner.Handle.IsValid);
            acquired.Dispose();
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            inner.Cancel();
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [Test]
        public void StartedFailureKeepsStableErrorAndSharedPeerWhileEachOwnerReturnsOnce()
        {
            var expected = new InvalidOperationException("started failure");
            var failed = AudioKit.PlaySoundAsync(Address("shared-failure"), onStarted: (_, __) => throw expected);
            var peer = AudioKit.PlaySoundAsync(Address("shared-failure"), AudioSoundOptions.MusicDefaults);
            _provider.Complete("shared-failure");
            Assert.AreEqual(AudioPlayEnd.Failed, failed.EndReason);
            Assert.AreSame(expected, failed.Error);
            Assert.IsTrue(peer.Handle.IsValid);
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            peer.Cancel();
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [Test]
        public void DisposedChannelEndsPermanentPendingAndOriginalCategoryIdentityRemainsIndependent()
        {
            var old = AudioKit.RegisterChannel("owner");
            var pending = old.PlayAsync(Address("permanent"));
            old.Dispose();
            Assert.AreEqual(AudioPlayEnd.Stopped, pending.EndReason);
            var current = AudioKit.RegisterChannel("owner");
            var handle = current.Play(_clip);
            _provider.Complete("permanent");
            Assert.IsTrue(handle.IsValid);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [Test]
        public void ProviderAndFullKeyKeepSameClipOwnershipSeparate()
        {
            var other = new Provider(_clip);
            var first = AudioKit.PlaySoundNamed(Address("one"), AudioSoundOptions.MusicDefaults);
            var second = AudioKit.PlaySoundNamed(Address("two"), AudioSoundOptions.MusicDefaults);
            var third = AudioKit.PlaySoundNamed(new AudioClipAddress(other, "one"), AudioSoundOptions.MusicDefaults);
            first.Stop();
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.AreEqual(0, _provider.Owners[1].Releases);
            Assert.AreEqual(0, other.Owners[0].Releases);
            Assert.IsTrue(second.IsValid);
            Assert.IsTrue(third.IsValid);
            second.Stop();
            third.Stop();
            Assert.AreEqual(1, other.Owners[0].Releases);
        }

        [Test]
        public void SlowExclusiveRequestCannotReplaceFastSuccessOrDirectPlayback()
        {
            var channel = AudioKit.RegisterChannel("ordered");
            var a = channel.PlayAsync(Address("a"));
            var b = channel.PlayAsync(Address("b"));
            Assert.AreEqual(AudioPlayEnd.Replaced, a.EndReason);
            _provider.Complete("b");
            Assert.IsTrue(b.Handle.IsValid);
            _provider.Complete("a");
            Assert.AreEqual(1, _provider.Owners[1].Releases);
            Assert.IsTrue(b.Handle.IsValid);
            var c = channel.PlayAsync(Address("c"));
            var direct = channel.Play(_clip);
            _provider.Complete("c");
            Assert.AreEqual(AudioPlayEnd.Replaced, c.EndReason);
            Assert.IsTrue(direct.IsValid);
        }

        [Test]
        public void OneCanceledWaiterDoesNotCancelSharedProducerOrOtherPlayback()
        {
            var a = AudioKit.PlaySoundAsync(Address("shared"));
            var b = AudioKit.PlaySoundAsync(Address("shared"));
            Assert.AreEqual(1, _provider.Loads);
            a.Cancel();
            Assert.AreEqual(AudioPlayEnd.Canceled, a.EndReason);
            Assert.IsFalse(a.Handle.IsValid);
            _provider.Complete("shared");
            Assert.IsTrue(b.Handle.IsValid);
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            b.Handle.Stop();
            Assert.AreEqual(AudioPlayEnd.Stopped, b.EndReason);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [Test]
        public void ShutdownAndLateDuplicateResultCannotEnterNewSession()
        {
            var old = AudioKit.PlayMusicAsync(Address("old"));
            AudioKit.Shutdown();
            Assert.AreEqual(AudioPlayEnd.Shutdown, old.EndReason);
            var current = AudioKit.PlayMusic(_clip);
            var lease = _provider.Complete("old");
            _provider.Pending["old"](lease, null);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
            Assert.IsTrue(current.IsValid);
            Assert.IsFalse(old.Handle.IsValid);
        }

        [Test]
        public void FailureDoesNotStopOldMusicAndPermanentPendingCanLogicallyExit()
        {
            var original = AudioKit.PlayMusic(_clip);
            var failed = AudioKit.PlayMusicAsync(Address("fail"));
            var error = new InvalidOperationException("load failure");
            _provider.Complete("fail", error);
            Assert.AreEqual(AudioPlayEnd.Failed, failed.EndReason);
            Assert.AreSame(error, failed.Error);
            Assert.IsTrue(original.IsValid);
            var never = AudioKit.PlayMusicAsync(Address("never"));
            AudioKit.StopMusic();
            Assert.AreEqual(AudioPlayEnd.Stopped, never.EndReason);
            Assert.IsTrue(never.IsDone);
        }

        [Test]
        public void ImmediateCompletionBindsBeforeStartedStopAndPreservesOriginalIdentity()
        {
            _provider.Immediate = true;
            AudioHandle started = default;
            var request = AudioKit.PlaySoundAsync(Address("immediate"), onStarted: (clip, handle) =>
            {
                started = handle;
                handle.Stop();
            });
            Assert.AreEqual(started, request.Handle);
            Assert.IsTrue(request.HasStarted);
            Assert.AreEqual(AudioPlayEnd.Stopped, request.EndReason);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [Test]
        public void CacheHoldsOnlyIdleOwnedEntriesAndNeverExpiresActiveLease()
        {
            AudioKit.ClipCacheCapacity = 1;
            AudioKit.ClipCacheIdleSeconds = 2;
            var handle = AudioKit.PlayMusicNamed(Address("cache"));
            AudioKit.Settings.IsMusicOn.Value = false;
            AudioManager.Instance.ClipCache.Tick(double.MaxValue);
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            handle.Stop();
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            var next = AudioKit.PlayMusicNamed(Address("cache"));
            Assert.AreEqual(1, _provider.Loads);
            AudioKit.ClearClipCache();
            Assert.AreEqual(0, _provider.Owners[0].Releases);
            next.Stop();
            AudioManager.Instance.ClipCache.Tick(double.MaxValue);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [Test]
        public void ExternallyDestroyedCachedAssetFailsRequestAndReturnsItsReservation()
        {
            AudioKit.ClipCacheCapacity = 1;
            AudioKit.ClipCacheIdleSeconds = 10;
            AudioKit.AcquireClip(Address("destroyed")).Dispose();
            UnityEngine.Object.DestroyImmediate(_clip);
            AudioPlayRequest request = null;
            Assert.DoesNotThrow(() => request = AudioKit.PlaySoundAsync(Address("destroyed")));
            Assert.AreEqual(AudioPlayEnd.Failed, request.EndReason);
            Assert.NotNull(request.Error);
            Assert.IsFalse(request.Handle.IsValid);
            Assert.AreEqual(1, _provider.Owners[0].Releases);
        }

        [Test]
        public void NamedLoadingHasNarrowProviderAndStableCancelableRequest()
        {
            var assembly = typeof(AudioKit).Assembly;
            Assert.NotNull(assembly.GetType("YLFramework.Audio.IAudioClipProvider"));
            Assert.NotNull(assembly.GetType("YLFramework.Audio.AudioPlayRequest"));
            Assert.NotNull(typeof(AudioKit).GetMethod("PlayMusicAsync"));
            Assert.NotNull(assembly.GetType("YLFramework.Audio.ResourceAudioClipProvider"));
        }
    }
}
