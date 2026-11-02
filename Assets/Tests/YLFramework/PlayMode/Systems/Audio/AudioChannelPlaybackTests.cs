using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioChannelPlaybackTests
    {
        private AudioClip _clip;
        private AudioClip _shortClip;
        private GameObject _external;
        private GameObject _listener;
        private float _timeScale;
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        private sealed class Resource : IDisposable
        {
            internal int Releases;
            internal Action Releasing;
            public void Dispose()
            {
                Releases++;
                Releasing?.Invoke();
            }
        }
        [SetUp]
        public void SetUp()
        {
            AudioKit.Shutdown();
            _timeScale = Time.timeScale;
            Time.timeScale = 1f;
            for (var i = 0; i < 6; i++)
            {
                _hadKeys[i] = PlayerPrefs.HasKey(_keys[i]);
            }

            for (var i = 0; i < 3; i++)
            {
                _switches[i] = PlayerPrefs.GetInt(_keys[i]);
                _volumes[i] = PlayerPrefs.GetFloat(_keys[i + 3]);
            }
            var settings = AudioKit.Settings;
            settings.IsSoundOn.SetValueWithoutCallback(true);
            settings.IsMusicOn.SetValueWithoutCallback(true);
            settings.IsVoiceOn.SetValueWithoutCallback(true);
            settings.SoundVolume.SetValueWithoutCallback(1f);
            settings.MusicVolume.SetValueWithoutCallback(1f);
            settings.VoiceVolume.SetValueWithoutCallback(1f);
            _clip = AudioClip.Create("channel-loop", 44100 * 4, 1, 44100, false);
            _shortClip = AudioClip.Create("channel-short", 2205, 1, 44100, false);
            _external = new GameObject("caller-source");
            if (Object.FindObjectsOfType<AudioListener>().Length == 0)
            {
                _listener = new GameObject("Channel.Listener");
                _listener.AddComponent<AudioListener>();
            }
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            Time.timeScale = _timeScale;
            Object.Destroy(_external);
            Object.Destroy(_clip);
            Object.Destroy(_shortClip);
            if (_listener)
            {
                Object.Destroy(_listener);
            }

            for (var i = 0; i < 3; i++)
            {
                if (_hadKeys[i])
                {
                    PlayerPrefs.SetInt(_keys[i], _switches[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(_keys[i]);
                }

                if (_hadKeys[i + 3])
                {
                    PlayerPrefs.SetFloat(_keys[i + 3], _volumes[i]);
                }
                else
                {
                    PlayerPrefs.DeleteKey(_keys[i + 3]);
                }
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator IndependentLoopsAdvanceAlongsideBgmAndStopLocally()
        {
            var windChannel = AudioKit.RegisterChannel("wind");
            var rainChannel = AudioKit.RegisterChannel("rain");
            var bgm = AudioKit.PlayMusic(_clip);
            var wind = windChannel.Play(_clip, AudioSoundOptions.MusicDefaults);
            var rain = rainChannel.Play(_clip, AudioSoundOptions.MusicDefaults);
            yield return new WaitForSecondsRealtime(.12f);
            Assert.IsTrue(bgm.IsPlaying);
            Assert.IsTrue(wind.IsPlaying);
            Assert.IsTrue(rain.IsPlaying);
            Assert.Greater(bgm.Time, 0f);
            Assert.Greater(wind.Time, 0f);
            Assert.Greater(rain.Time, 0f);
            windChannel.Volume = .3f;
            windChannel.IsMuted = true;
            Assert.AreEqual(.3f, wind.Voice.Source.volume, .001f);
            Assert.IsTrue(wind.IsPlaying);
            Assert.AreEqual(1f, rain.Voice.Source.volume);
            Assert.AreEqual(1f, bgm.Voice.Source.volume);
            windChannel.Stop();
            yield return null;
            Assert.IsFalse(wind.IsValid);
            Assert.IsTrue(bgm.IsPlaying);
            Assert.IsTrue(rain.IsPlaying);
            rainChannel.Pause();
            var time = rain.Time;
            yield return new WaitForSecondsRealtime(.08f);
            Assert.AreEqual(time, rain.Time, .01f);
            Assert.IsTrue(bgm.IsPlaying);
        }

        [UnityTest]
        public IEnumerator BorrowedSourcePreservesHierarchySpatialCurvesAndCallerOverrides()
        {
            var source = _external.AddComponent<AudioSource>();
            var parent = new GameObject("caller-parent");
            try
            {
                _external.transform.SetParent(parent.transform, false);
                _external.transform.localPosition = new Vector3(2f, 3f, 4f);
                source.volume = .42f;
                source.pitch = .8f;
                source.playOnAwake = true;
                source.spatialBlend = .7f;
                source.dopplerLevel = 1.5f;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Linear(0f, 1f, 1f, .1f));
                var curve = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                var channel = AudioKit.RegisterChannel("borrowed");
                var handle = channel.PlayBorrowed(source, _clip, AudioSoundOptions.MusicDefaults);
                yield return new WaitForSecondsRealtime(.1f);
                Assert.IsTrue(source.isPlaying);
                Assert.Greater(handle.Time, 0f);
                Assert.Throws<InvalidOperationException>(() => channel.PlayBorrowed(source, _clip));
                source.pitch = 1.3f;
                handle.Stop();
                Assert.AreSame(parent.transform, _external.transform.parent);
                Assert.AreEqual(new Vector3(2f, 3f, 4f), _external.transform.localPosition);
                Assert.AreEqual(.42f, source.volume);
                Assert.AreEqual(1.3f, source.pitch);
                Assert.IsTrue(source.playOnAwake);
                Assert.IsTrue(source.enabled);
                Assert.IsTrue(_external.activeSelf);
                Assert.AreEqual(.7f, source.spatialBlend);
                Assert.AreEqual(1.5f, source.dopplerLevel);
                Assert.AreEqual(curve.keys, source.GetCustomCurve(AudioSourceCurveType.CustomRolloff).keys);
                Assert.IsNull(source.clip);
                Assert.IsFalse(source.isPlaying);
                AudioKit.Shutdown();
                yield return null;
                Assert.IsTrue(source);
                Assert.IsTrue(_external.activeSelf);
            }
            finally
            {
                _external.transform.SetParent(null);
                Object.Destroy(parent);
            }
        }

        [UnityTest]
        public IEnumerator BorrowedNaturalCompletionReentryAndSourceDestructionReleaseExactlyOnce()
        {
            var source = _external.AddComponent<AudioSource>();
            source.volume = .31f;
            var channel = AudioKit.RegisterChannel("short", AudioCategory.Sound);
            var resource = new Resource();
            var finishes = 0;
            AudioHandle next = default;
            var old = channel.PlayWithSnapshot(_shortClip, AudioPlaybackOptions.Defaults, finished: (_, original) =>
            {
                finishes++;
                Assert.IsTrue(original.IsValid);
                Assert.AreEqual(0, resource.Releases);
                original.Stop();
                next = channel.PlayBorrowed(source, _clip, AudioSoundOptions.MusicDefaults);
            }, resource: new AudioResourceLease(resource), source: source, borrowed: true);
            var deadline = Time.realtimeSinceStartup + 2f;
            while (old.IsValid && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.AreEqual(1, finishes);
            Assert.AreEqual(1, resource.Releases);
            Assert.IsTrue(next.IsPlaying);
            old.Stop();
            Assert.IsTrue(next.IsPlaying);
            Object.Destroy(source);
            yield return null;
            yield return null;
            Assert.IsFalse(next.IsValid);
            Assert.AreEqual(0, channel.RequestCount);
            Assert.AreEqual(0, AudioKit.SoundCount);
            Assert.IsTrue(_external.activeSelf);
            AudioKit.Shutdown();
            Assert.AreEqual(1, resource.Releases);
        }

        [UnityTest]
        public IEnumerator OptInAutomaticPausePreservesManualPauseAndDefaultBgmAtZeroTimeScale()
        {
            var listenerPause = AudioListener.pause;
            var bgm = AudioKit.PlayMusic(_clip);
            var channel = AudioKit.RegisterChannel("auto");
            var options = AudioSoundOptions.MusicDefaults;
            options.AutoPauseWhenTimeScaleZero = true;
            var automatic = channel.Play(_clip, options);
            yield return new WaitForSecondsRealtime(.08f);
            Time.timeScale = 0f;
            yield return null;
            yield return null;
            Assert.IsTrue(automatic.IsPaused);
            Assert.IsFalse(automatic.IsPlaying);
            Assert.IsTrue(bgm.IsPlaying);
            var frozen = automatic.Time;
            var bgmTime = bgm.Time;
            yield return new WaitForSecondsRealtime(.08f);
            Assert.AreEqual(frozen, automatic.Time, .01f);
            Assert.Greater(bgm.Time, bgmTime);
            automatic.Pause();
            Time.timeScale = 1f;
            yield return null;
            yield return null;
            Assert.IsTrue(automatic.IsPaused);
            Assert.IsFalse(automatic.IsPlaying);
            automatic.Resume();
            yield return new WaitForSecondsRealtime(.08f);
            Assert.IsTrue(automatic.IsPlaying);
            Assert.Greater(automatic.Time, frozen);
            Time.timeScale = 0f;
            var starts = 0;
            var waiting = AudioKit.PlaySound(_clip, options, onStarted: (_, __) => starts++);
            Assert.IsTrue(waiting.IsPaused);
            Assert.AreEqual(0, starts);
            Time.timeScale = 1f;
            yield return null;
            yield return null;
            Assert.IsTrue(waiting.IsPlaying);
            Assert.AreEqual(1, starts);
            Assert.AreEqual(listenerPause, AudioListener.pause);
        }

        [UnityTest]
        public IEnumerator BorrowedStartedStopReplacementAndShutdownCannotRestoreOverNewLease()
        {
            var source = _external.AddComponent<AudioSource>();
            source.volume = .35f;
            var channel = AudioKit.RegisterChannel("callback");
            AudioHandle next = default;
            var old = channel.PlayBorrowed(source, _clip, onStarted: (_, handle) =>
            {
                handle.Stop();
                next = channel.PlayBorrowed(source, _clip, new AudioSoundOptions { Loop = true, Volume = .6f });
            });
            Assert.IsFalse(old.IsValid);
            Assert.IsTrue(next.IsPlaying);
            Assert.AreEqual(.6f, source.volume, .001f);
            next.Stop();
            Assert.AreEqual(.35f, source.volume);
            channel.PlayBorrowed(source, _clip, onStarted: (_, __) =>
            {
                AudioKit.Shutdown();
                source.volume = .22f;
            });
            yield return null;
            Assert.AreEqual(.22f, source.volume);
            Assert.IsTrue(_external.activeSelf);
            Assert.IsTrue(source);
        }

        [UnityTest]
        public IEnumerator BorrowedChannelProgressUsesUnscaledTimeAndStopAllowsReplay()
        {
            var source = _external.AddComponent<AudioSource>();
            var channel = AudioKit.RegisterChannel("migrated-core-progress");
            Time.timeScale = 0;
            var first = channel.PlayBorrowed(source, _clip, AudioSoundOptions.MusicDefaults);
            yield return new WaitForSecondsRealtime(.08f);
            Assert.Greater(first.Time, 0);
            channel.Pause();
            var paused = first.Time;
            yield return new WaitForSecondsRealtime(.08f);
            Assert.AreEqual(paused, first.Time, .01f);
            channel.Stop();
            channel.Resume();
            Assert.IsFalse(first.IsValid);
            var second = channel.PlayBorrowed(source, _clip, AudioSoundOptions.MusicDefaults);
            yield return new WaitForSecondsRealtime(.08f);
            Assert.Greater(second.Time, 0);
            Object.Destroy(source);
            yield return null;
            yield return null;
            Assert.IsFalse(second.IsValid);
            Assert.AreEqual(0, channel.RequestCount);
            channel.Dispose();
            Assert.IsTrue(_external, "Retiring a channel cannot destroy the caller's GameObject.");
        }

        [UnityTest]
        public IEnumerator BorrowedCleanupFailureRevokesOwnershipAndCannotRepeatAcrossFrames()
        {
            var source = _external.AddComponent<AudioSource>();
            source.volume = .37f;
            var channel = AudioKit.RegisterChannel("migrated-core-cleanup");
            var otherChannel = AudioKit.RegisterChannel("unrelated-cleanup");
            var other = otherChannel.Play(_clip, AudioSoundOptions.MusicDefaults);
            var resource = new Resource { Releasing = () => throw new InvalidOperationException("migrated-cleanup-failure") };
            var original = channel.PlayWithSnapshot(_clip, AudioPlaybackOptions.Capture(AudioSoundOptions.MusicDefaults),
                resource: new AudioResourceLease(resource), source: source, borrowed: true);
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("migrated-cleanup-failure"));
            channel.Dispose();
            Assert.IsFalse(channel.IsValid);
            Assert.IsFalse(original.IsValid);
            Assert.AreEqual(1, resource.Releases);
            Assert.IsTrue(source);
            Assert.IsTrue(_external.activeSelf);
            Assert.AreEqual(.37f, source.volume);
            Assert.IsNull(source.clip);
            yield return null;
            yield return null;
            Assert.AreEqual(1, resource.Releases);
            Assert.IsTrue(other.IsPlaying);
            channel.Dispose();
            Assert.AreEqual(1, resource.Releases);
        }

        private static void ConfigureGroupPlayer(AudioGroupPlayer player, string field, object value) =>
            typeof(AudioGroupPlayer).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(player, value);

        [UnityTest]
        public IEnumerator GroupAutoPlayDefaultsToManualAndEnableReplayIsExplicit()
        {
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            library.groups.Add(new AudioClipGroup { name = "loop", entries = new System.Collections.Generic.List<AudioClipEntry> { new AudioClipEntry { clip = _clip } } });
            var root = new GameObject("group-auto");
            root.SetActive(false);
            try
            {
                var player = root.AddComponent<AudioGroupPlayer>();
                ConfigureGroupPlayer(player, "library", library);
                ConfigureGroupPlayer(player, "groupName", "loop");
                ConfigureGroupPlayer(player, "channelName", "group-auto");
                ConfigureGroupPlayer(player, "category", AudioCategory.Music);
                ConfigureGroupPlayer(player, "options", AudioSoundOptions.MusicDefaults);
                root.SetActive(true);
                yield return null;
                Assert.IsNull(player.Channel, "Manual default cannot create an audio owner in Start.");
                player.Play();
                var first = player.LastHandle;
                yield return new WaitForSecondsRealtime(.08f);
                Assert.IsTrue(first.IsPlaying);
                Assert.Greater(first.Time, 0);
                root.SetActive(false);
                Assert.IsFalse(first.IsValid);
                root.SetActive(true);
                yield return null;
                Assert.IsFalse(player.LastHandle.IsValid, "Manual does not replay on Enable.");
                ConfigureGroupPlayer(player, "autoPlay", AudioGroupAutoPlay.EveryEnable);
                root.SetActive(false);
                root.SetActive(true);
                yield return new WaitForSecondsRealtime(.08f);
                Assert.IsTrue(player.LastHandle.IsPlaying);
                var replay = player.LastHandle;
                root.SetActive(false);
                root.SetActive(true);
                yield return null;
                Assert.IsFalse(replay.IsValid);
                Assert.IsTrue(player.LastHandle.IsValid);
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(library);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator GroupFirstStartRunsOnceWithoutEnableReplay()
        {
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            library.groups.Add(new AudioClipGroup { name = "loop", entries = new System.Collections.Generic.List<AudioClipEntry> { new AudioClipEntry { clip = _clip } } });
            var root = new GameObject("group-start");
            root.SetActive(false);
            try
            {
                var player = root.AddComponent<AudioGroupPlayer>();
                ConfigureGroupPlayer(player, "library", library);
                ConfigureGroupPlayer(player, "groupName", "loop");
                ConfigureGroupPlayer(player, "channelName", "group-start");
                ConfigureGroupPlayer(player, "autoPlay", AudioGroupAutoPlay.FirstStart);
                var starts = 0;
                player.Started.AddListener(() => starts++);
                root.SetActive(true);
                yield return null;
                yield return null;
                Assert.AreEqual(1, starts);
                Assert.IsTrue(player.LastHandle.IsValid);
                root.SetActive(false);
                root.SetActive(true);
                yield return null;
                Assert.AreEqual(1, starts);
                Assert.IsFalse(player.LastHandle.IsValid);
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(library);
            }
            yield return null;
        }

        private sealed class GroupProvider : IAudioClipProvider
        {
            public AudioClip Clip;
            public Resource Resource = new Resource();
            public Action<AudioClipLease, Exception> Completed;
            public bool CanCache => false;
            public AudioClipLease Load(string key) => new AudioClipLease(Clip, Resource);
            public void LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)
            {
                Completed = completed;
            }
        }

        [UnityTest]
        public IEnumerator NamedGroupNaturalCompletionReturnsLeaseAndDisableCannotReviveLateRequest()
        {
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            library.groups.Add(new AudioClipGroup { name = "named", entries = new System.Collections.Generic.List<AudioClipEntry> { new AudioClipEntry { source = AudioClipSource.ExternalProvider, key = "clip" } } });
            var root = new GameObject("group-named");
            try
            {
                var player = root.AddComponent<AudioGroupPlayer>();
                ConfigureGroupPlayer(player, "library", library);
                ConfigureGroupPlayer(player, "groupName", "named");
                ConfigureGroupPlayer(player, "channelName", "group-named");
                var provider = new GroupProvider { Clip = _shortClip };
                player.SetProvider(provider);
                player.Play();
                var request = player.LastRequest;
                Assert.AreEqual(AudioPlayState.Loading, request.State);
                provider.Completed(provider.Load("clip"), null);
                Assert.IsTrue(request.Handle.IsPlaying);
                var deadline = Time.realtimeSinceStartup + 2;
                while (!request.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.AreEqual(AudioPlayEnd.Completed, request.EndReason);
                Assert.AreEqual(1, provider.Resource.Releases);
                provider = new GroupProvider { Clip = _clip };
                player.SetProvider(provider);
                player.Play();
                var late = player.LastRequest;
                root.SetActive(false);
                root.SetActive(true);
                provider.Completed(provider.Load("clip"), null);
                yield return null;
                Assert.IsTrue(late.IsDone);
                Assert.IsFalse(late.Handle.IsValid);
                Assert.AreEqual(1, provider.Resource.Releases);
                Assert.AreEqual(0, player.Channel.RequestCount);
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(library);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator GroupComponentUsesTheBorrowedSourceForDirectAndNamedPlayback()
        {
            var source = _external.AddComponent<AudioSource>();
            source.clip = _clip;
            source.volume = .27f;
            source.spatialBlend = .5f;
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            library.groups.Add(new AudioClipGroup { name = "direct", entries = new System.Collections.Generic.List<AudioClipEntry> { new AudioClipEntry { clip = _clip } } });
            library.groups.Add(new AudioClipGroup { name = "named", entries = new System.Collections.Generic.List<AudioClipEntry> { new AudioClipEntry { source = AudioClipSource.ExternalProvider, key = "clip" } } });
            var root = new GameObject("group-borrowed");
            try
            {
                var player = root.AddComponent<AudioGroupPlayer>();
                ConfigureGroupPlayer(player, "library", library);
                ConfigureGroupPlayer(player, "channelName", "group-borrowed");
                ConfigureGroupPlayer(player, "borrowedSource", source);
                ConfigureGroupPlayer(player, "options", AudioSoundOptions.MusicDefaults);
                player.PlayGroup("direct");
                Assert.AreSame(source, player.LastHandle.Voice.Source);
                yield return new WaitForSecondsRealtime(.08f);
                Assert.IsTrue(source.isPlaying);
                Assert.Greater(player.LastHandle.Time, 0);
                player.Stop();
                Assert.AreEqual(.27f, source.volume);
                Assert.AreSame(_clip, source.clip);
                var provider = new GroupProvider { Clip = _shortClip };
                player.SetProvider(provider);
                ConfigureGroupPlayer(player, "options", AudioSoundOptions.Defaults);
                player.PlayGroup("named");
                var request = player.LastRequest;
                Assert.AreSame(_clip, source.clip);
                Assert.IsFalse(source.isPlaying);
                provider.Completed(provider.Load("clip"), null);
                Assert.AreSame(source, request.Handle.Voice.Source);
                var deadline = Time.realtimeSinceStartup + 2;
                while (!request.IsDone && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.AreEqual(AudioPlayEnd.Completed, request.EndReason);
                Assert.AreEqual(1, provider.Resource.Releases);
                Assert.AreEqual(.27f, source.volume);
                Assert.AreSame(_clip, source.clip);
                Assert.AreEqual(.5f, source.spatialBlend);
                provider = new GroupProvider { Clip = _clip };
                player.SetProvider(provider);
                ConfigureGroupPlayer(player, "loadAsync", false);
                ConfigureGroupPlayer(player, "options", AudioSoundOptions.MusicDefaults);
                player.PlayGroup("named");
                Assert.AreSame(source, player.LastHandle.Voice.Source);
                root.SetActive(false);
                Assert.IsFalse(player.LastHandle.IsValid);
                Assert.AreEqual(1, provider.Resource.Releases);
                Assert.IsTrue(source);
                Assert.IsTrue(_external.activeSelf);
                Assert.AreEqual(.27f, source.volume);
            }
            finally
            {
                Object.Destroy(root);
                Object.Destroy(library);
            }
            yield return null;
        }
    }
}
