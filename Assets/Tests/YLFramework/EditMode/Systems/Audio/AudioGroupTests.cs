using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Audio;

namespace YLFramework.Tests.Audio
{
    public class AudioGroupTests
    {
        private AudioTestSettings _settings;
        private AudioClipLibrary _library;
        private AudioClip _clip;
        private GameObject _root;
        private AudioGroupPlayer _player;
        private UnityEngine.Random.State _random;

        [SetUp]
        public void SetUp()
        {
            _random = UnityEngine.Random.state;
            _settings = new AudioTestSettings();
            _library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            _clip = AudioClip.Create("group", 44100, 1, 44100, false);
            _library.groups.Add(new AudioClipGroup { name = "direct", entries = new List<AudioClipEntry> { new AudioClipEntry { clip = _clip } } });
            _root = new GameObject("AudioGroupTests");
            _player = _root.AddComponent<AudioGroupPlayer>();
            using (var serialized = new SerializedObject(_player))
            {
                serialized.FindProperty("library").objectReferenceValue = _library;
                serialized.FindProperty("groupName").stringValue = "direct";
                serialized.FindProperty("channelName").stringValue = "test-group";
                serialized.FindProperty("category").intValue = (int)AudioCategory.Music;
                serialized.FindProperty("options").FindPropertyRelative("Loop").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_root);
            _settings?.Dispose();
            UnityEngine.Object.DestroyImmediate(_library);
            UnityEngine.Object.DestroyImmediate(_clip);
            UnityEngine.Random.state = _random;
        }

        private sealed class Owner : IDisposable
        {
            public int Releases;
            public Action Releasing;
            public void Dispose()
            {
                Releases++;
                Releasing?.Invoke();
            }
        }
        private sealed class Provider : IAudioClipProvider
        {
            public bool CanCache => false;
            public readonly Dictionary<string, Action<AudioClipLease, Exception>> Pending = new Dictionary<string, Action<AudioClipLease, Exception>>();
            public AudioClip Clip;
            public readonly List<Owner> Owners = new List<Owner>();
            public AudioClipLease Load(string key)
            {
                var owner = new Owner();
                Owners.Add(owner);
                return new AudioClipLease(Clip, owner);
            }
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress) => Pending.Add(key, completed);
            public void Complete(string key, Exception error = null) => Pending[key](error == null ? Load(key) : null, error);
        }
        private Provider Named()
        {
            var provider = new Provider { Clip = _clip };
            _player.SetProvider(provider);
            _library.groups.Add(new AudioClipGroup { name = "named", entries = new List<AudioClipEntry> { new AudioClipEntry { source = AudioClipSource.ExternalProvider, key = "a" } } });
            return provider;
        }

        [Test]
        public void FormalGroupAuthoringLivesInTheExistingRuntimeAssembly()
        {
            var assembly = typeof(AudioKit).Assembly;
            Assert.IsNotNull(assembly.GetType("YLFramework.Audio.AudioClipLibrary"), "The formal named selection library is not integrated.");
            Assert.IsNotNull(assembly.GetType("YLFramework.Audio.AudioGroupPlayer"), "The formal group component is not integrated.");
        }

        [Test]
        public void SelectionConsumesExactlyOneRandomDrawAndSnapshotsTheAddress()
        {
            var provider = Named();
            UnityEngine.Random.InitState(812);
            var before = UnityEngine.Random.state;
            Assert.IsTrue(_library.TrySelect("named", out var selected, provider));
            var after = UnityEngine.Random.state;
            UnityEngine.Random.state = before;
            _ = UnityEngine.Random.value;
            Assert.AreEqual(UnityEngine.Random.state, after);
            _library.groups[1].entries[0].key = "changed";
            Assert.AreEqual("a", selected.Address.Key);
            Assert.AreSame(provider, selected.Address.Provider);
            Assert.IsTrue(selected.IsNamed);
        }

        [Test]
        public void WeightedZeroExcludesAndUniformIgnoresStoredWeights()
        {
            var group = _library.groups[0];
            group.selection = AudioClipSelectionMode.Weighted;
            group.entries[0].weight = 0;
            Assert.IsFalse(_library.TrySelect("direct", out _));
            var provider = new Provider { Clip = _clip };
            group.entries.Add(new AudioClipEntry { source = AudioClipSource.ExternalProvider, key = "audio/selected", weight = 100 });
            for (var i = 0; i < 20; i++)
            {
                Assert.IsTrue(_library.TrySelect("direct", out var selected, provider));
                Assert.IsTrue(selected.IsNamed);
                Assert.AreSame(provider, selected.Address.Provider);
            }
            group.entries.RemoveAt(1);
            group.selection = AudioClipSelectionMode.Uniform;
            group.entries[0].weight = float.NaN;
            Assert.IsTrue(_library.TrySelect("direct", out var direct));
            Assert.AreSame(_clip, direct.Clip);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void InvalidWeightedGroupDoesNotReplaceOldPlayback(float weight)
        {
            _player.Play();
            var previous = _player.LastHandle;
            var group = _library.groups[0];
            group.selection = AudioClipSelectionMode.Weighted;
            group.entries[0].weight = weight;
            _player.Play();
            Assert.IsTrue(previous.IsValid);
            Assert.AreEqual(previous, _player.LastHandle);
            Assert.AreEqual(1, _player.Channel.RequestCount);
        }

        [Test]
        public void MissingEmptyDuplicateOrInvalidAddressGroupsDoNotCreateAHost()
        {
            _player.PlayGroup("missing");
            _library.groups[0].entries.Clear();
            _player.Play();
            _library.groups[0].entries.Add(new AudioClipEntry { source = AudioClipSource.AssetBundle, key = "resources://wrong", bundleName = "bundle" });
            _player.Play();
            _library.groups[0].entries[0] = new AudioClipEntry { clip = _clip };
            _library.groups.Add(new AudioClipGroup { name = "direct" });
            _player.Play();
            Assert.IsNull(_player.Channel);
            Assert.IsFalse(AudioManager.HasInstance);
        }

        [Test]
        public void LegacyBundleNeverGuessesProjectIdentityOrCreatesAProvider()
        {
            _library.groups[0].entries[0] = new AudioClipEntry { source = AudioClipSource.AssetBundle, key = "ambience.wav", bundleName = "ambience" };
            Assert.IsFalse(_library.TrySelect("direct", out _));
            Assert.IsFalse(_library.TrySelect("direct", out _));
            Assert.IsNull(YLFramework.Resource.ResourceSystem.Current);
        }

        [Test]
        public void LoadFailureProtectsOldPlayingHandleAndLatestSelectionWins()
        {
            var provider = Named();
            _player.Play();
            var old = _player.LastHandle;
            _player.PlayGroup("named");
            var failed = _player.LastRequest;
            provider.Complete("a", new InvalidOperationException("missing group asset"));
            Assert.AreEqual(AudioPlayEnd.Failed, failed.EndReason);
            Assert.IsTrue(old.IsValid);
            _library.groups[1].entries[0].key = "slow";
            _player.PlayGroup("named");
            var slow = _player.LastRequest;
            _library.groups[1].entries[0].key = "fast";
            _player.PlayGroup("named");
            var fast = _player.LastRequest;
            provider.Complete("fast");
            provider.Complete("slow");
            Assert.AreEqual(AudioPlayEnd.Replaced, slow.EndReason);
            Assert.IsTrue(fast.Handle.IsValid);
            Assert.AreEqual(fast.Handle, _player.LastHandle);
            Assert.IsFalse(old.IsValid);
            Assert.AreEqual(1, provider.Owners[1].Releases, "Late slow producer is returned exactly once.");
            _player.Stop();
            Assert.AreEqual(1, provider.Owners[0].Releases);
        }

        [Test]
        public void DisableCancelsPendingAndLateCompletionCannotStartOrRetainResources()
        {
            var provider = Named();
            var starts = 0;
            _player.Started.AddListener(() => starts++);
            _player.PlayGroup("named");
            var request = _player.LastRequest;
            _root.SetActive(false);
            provider.Complete("a");
            Assert.IsTrue(request.IsDone);
            Assert.IsFalse(request.Handle.IsValid);
            Assert.AreEqual(0, starts);
            Assert.AreEqual(1, provider.Owners[0].Releases);
            Assert.AreEqual(0, _player.Channel.RequestCount);
        }

        [Test]
        public void SynchronousStartedDisableAndThrowUseTheOriginalLeaseCleanup()
        {
            var provider = Named();
            _player.Started.AddListener(() => _root.SetActive(false));
            _player.PlayGroup("named");
            var request = _player.LastRequest;
            provider.Complete("a");
            Assert.IsFalse(request.Handle.IsValid);
            Assert.AreEqual(1, provider.Owners[0].Releases);
            _root.SetActive(true);
            _player.Started.RemoveAllListeners();
            _player.Started.AddListener(() => throw new InvalidOperationException("group event"));
            Assert.Throws<InvalidOperationException>(() => _player.Play());
            Assert.AreEqual(0, _player.Channel.RequestCount);
        }

        [Test]
        public void GroupSelectionNameAndControlChannelNameRemainIndependent()
        {
            _player.Play();
            Assert.AreEqual("test-group", _player.Channel.Name);
            Assert.AreEqual(AudioCategory.Music, _player.Channel.Category);
            var background = AudioKit.PlayMusic(_clip);
            _player.Channel.Volume = .2f;
            _player.Stop();
            Assert.IsTrue(background.IsValid);
            Assert.IsFalse(_player.LastHandle.IsValid);
        }

        [Test]
        public void ChannelChangesNotifyAfterApplyingValuesAndDoNotNotifyIdenticalWrites()
        {
            var changed = typeof(AudioChannel).GetEvent("VolumeChanged");
            var muted = typeof(AudioChannel).GetEvent("MutedChanged");
            Assert.IsNotNull(changed, "Core channel observation has not been integrated.");
            Assert.IsNotNull(muted);
            _player.Play();
            var channel = _player.Channel;
            var volumes = 0;
            var mutes = 0;
            Action<float> onVolume = value =>
            {
                volumes++;
                Assert.AreEqual(value, channel.Volume);
            };
            Action<bool> onMute = value =>
            {
                mutes++;
                Assert.AreEqual(value, channel.IsMuted);
            };
            changed.AddEventHandler(channel, onVolume);
            muted.AddEventHandler(channel, onMute);
            channel.Volume = .4f;
            channel.Volume = .4f;
            channel.IsMuted = true;
            channel.IsMuted = true;
            Assert.AreEqual(1, volumes);
            Assert.AreEqual(1, mutes);
        }

        [Test]
        public void ChannelNotificationReentryRevokesStaleDeliveryButExceptionsDoNotHideOtherObservers()
        {
            var channel = AudioKit.RegisterChannel("observed");
            var observed = new List<float>();
            channel.VolumeChanged += value =>
            {
                if (value == .5f)
                {
                    channel.Volume = .25f;
                }
            };
            channel.VolumeChanged += value => observed.Add(value);
            channel.Volume = .5f;
            CollectionAssert.AreEqual(new[] { .25f }, observed);
            var later = 0;
            channel.MutedChanged += value => throw new InvalidOperationException("observer-failure");
            channel.MutedChanged += value => later++;
            Assert.Throws<AggregateException>(() => channel.IsMuted = true);
            Assert.IsTrue(channel.IsMuted);
            Assert.AreEqual(1, later);
            channel.Dispose();
            var replacement = AudioKit.RegisterChannel("observed");
            replacement.IsMuted = true;
            replacement.Volume = .5f;
            Assert.AreEqual(1, later);
            Assert.AreEqual(1, observed.Count);
        }
    }
}
