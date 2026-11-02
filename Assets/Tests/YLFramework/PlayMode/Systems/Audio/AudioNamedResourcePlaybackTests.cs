#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioNamedResourcePlaybackTests
    {
        private string _folder;
        private GameObject _listener;
        private float _timeScale;
        private ResourceSystem _resources;
        private string[] _names;
        private int _nextClip;
        private readonly bool[] _hadKeys = new bool[6];
        private readonly int[] _switches = new int[3];
        private readonly float[] _volumes = new float[3];
        private static readonly string[] _keys =
        {
            "KEY_AUDIO_MANAGER_SOUND_ON", "KEY_AUDIO_MANAGER_MUSIC_ON", "KEY_AUDIO_MANAGER_VOICE_ON",
            "KEY_AUDIO_MANAGER_SOUND_VOLUME", "KEY_AUDIO_MANAGER_MUSIC_VOLUME", "KEY_AUDIO_MANAGER_VOICE_VOLUME"
        };
        [SetUp]
        public void SetUp()
        {
            AudioKit.Shutdown();
            _timeScale = Time.timeScale;
            for (var i = 0; i < 6; i++)
            {
                _hadKeys[i] = PlayerPrefs.HasKey(_keys[i]);
            }

            for (var i = 0; i < 3; i++)
            {
                _switches[i] = PlayerPrefs.GetInt(_keys[i]);
                _volumes[i] = PlayerPrefs.GetFloat(_keys[i + 3]);
            }
            AudioKit.Settings.IsSoundOn.SetValueWithoutCallback(true);
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
            AudioKit.Settings.IsVoiceOn.SetValueWithoutCallback(true);
            AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
            _folder = "Assets/Tests/YLFramework/Fixtures/Audio/Named-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures/Audio", Path.GetFileName(_folder));
            AssetDatabase.CreateFolder(_folder, "Resources");
            _nextClip = 0;
            _names = Enumerable.Range(0, 8).Select(i => "audio86b-" + Guid.NewGuid().ToString("N")).ToArray();
            var entries = _names.Select((name, i) => new ResourceCatalogEntry("clip-" + i, new ResourceAddress("audio86b", "clip/" + i),
                new YLFramework.Resource.ResourceLocation(ResourceSourceId.Resources, name, typeof(AudioClip)))).ToList();
            entries.Add(new ResourceCatalogEntry("missing", new ResourceAddress("audio86b", "missing"),
                new YLFramework.Resource.ResourceLocation(ResourceSourceId.Resources, "missing-" + Guid.NewGuid().ToString("N"), typeof(AudioClip))));
            using (var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("audio86b", entries) })))
            {
                _resources = initialize.TakeResult();
            }

            if (Object.FindObjectsOfType<AudioListener>().Length == 0)
            {
                _listener = new GameObject("Named.Listener");
                _listener.AddComponent<AudioListener>();
            }
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            AudioKit.Shutdown();
            Time.timeScale = _timeScale;
            var deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (_resources.GetDiagnosticsSnapshot().ProducerCount != 0 && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            using (var shutdown = _resources.ShutdownAsync())
            {
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            if (_listener)
            {
                Object.Destroy(_listener);
            }

            AssetDatabase.DeleteAsset(_folder);
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
        private AudioClipAddress CreateClip(AudioClipLoadType loadType = AudioClipLoadType.DecompressOnLoad,
            AudioCompressionFormat compression = AudioCompressionFormat.PCM, bool background = false, int seconds = 2)
        {
            var index = _nextClip++;
            var name = _names[index];
            var path = _folder + "/Resources/" + name + ".wav";
            WriteWave(path, seconds);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            importer.loadInBackground = background;
            var settings = importer.defaultSampleSettings;
            settings.preloadAudioData = false;
            settings.loadType = loadType;
            settings.compressionFormat = compression;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
            return new AudioClipAddress(_resources.GetAudioClipProvider("audio86b"), "clip/" + index);
        }
        internal static void WriteWave(string path, int seconds)
        {
            Assert.IsTrue(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path))), "Only write under an existing owned fixture directory.");
            var samples = 44100 * seconds;
            using (var writer = new BinaryWriter(File.Create(global::YLFramework.Testing.PackageTestPaths.Physical(path))))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(44100);
                writer.Write(88200);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(samples * 2);
                for (var i = 0; i < samples; i++)
                {
                    writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 4000));
                }
            }
        }
        private static IEnumerator WaitAccepted(AudioPlayRequest request)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!request.Handle.IsValid && !request.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(request.Handle.IsValid, request.Error?.ToString() ?? request.EndReason.ToString());
            Assert.IsTrue(request.IsAssetReady);
            Assert.IsTrue(request.IsDataReady);
        }
        private void AssertResourcesReturned(AudioClipAddress address, AudioClip clip, int otherLeases = 0)
        {
            Assert.That(_resources.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(otherLeases), address.Key);
            Assert.IsTrue(clip, "Resources is a shared borrowed source; returning a lease must not unload another user's Clip.");
        }

        [UnityTest]
        public IEnumerator RealResourcesSyncDisabledReplayPauseAndIdleRelease()
        {
            var address = CreateClip();
            AudioKit.ClipCacheCapacity = 1;
            AudioKit.ClipCacheIdleSeconds = .1;
            var handle = AudioKit.PlayMusicNamed(address);
            var clip = handle.Voice.Clip;
            Assert.IsTrue(handle.IsValid);
            Assert.Greater(_resources.GetDiagnosticsSnapshot().LeaseCount, 0);
            AudioKit.Settings.IsMusicOn.Value = false;
            Time.timeScale = 0;
            yield return new WaitForSecondsRealtime(.2f);
            Assert.IsTrue(clip);
            Assert.IsTrue(handle.IsValid);
            Assert.Greater(_resources.GetDiagnosticsSnapshot().LeaseCount, 0);
            AudioKit.Settings.IsMusicOn.Value = true;
            Assert.IsTrue(handle.IsPlaying);
            handle.Pause();
            yield return null;
            Assert.IsTrue(clip);
            Assert.IsTrue(handle.IsPaused);
            handle.Stop();
            Assert.IsTrue(clip, "Idle cache owns the backend lease.");
            var reused = AudioKit.PlayMusicNamed(address);
            Assert.AreSame(clip, reused.Voice.Clip);
            reused.Stop();
            yield return new WaitForSecondsRealtime(.2f);
            AssertResourcesReturned(address, clip);
        }

        [UnityTest]
        public IEnumerator RealResourcesSharedCancelLateShutdownAndRetry()
        {
            var address = CreateClip();
            var canceled = AudioKit.PlaySoundAsync(address, AudioSoundOptions.MusicDefaults);
            var live = AudioKit.PlaySoundAsync(address, AudioSoundOptions.MusicDefaults);
            canceled.Cancel();
            yield return WaitAccepted(live);
            Assert.AreEqual(AudioPlayEnd.Canceled, canceled.EndReason);
            var clip = live.Handle.Voice.Clip;
            Assert.IsTrue(clip);
            live.Cancel();
            AssertResourcesReturned(address, clip);
            var late = AudioKit.PlayMusicAsync(address);
            AudioKit.Shutdown();
            Assert.AreEqual(AudioPlayEnd.Shutdown, late.EndReason);
            yield return null;
            yield return null;
            yield return null;
            Assert.IsFalse(late.Handle.IsValid);
            var retry = AudioKit.PlayMusicAsync(address);
            yield return WaitAccepted(retry);
            Assert.IsTrue(retry.Handle.IsPlaying);
            retry.Cancel();
            yield return null;
            Assert.That(_resources.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RealResourcesMissingAssetFailsAndCanRetryWithoutStoppingExistingMusic()
        {
            var valid = CreateClip();
            var playing = AudioKit.PlayMusicNamed(valid);
            var invalid = new AudioClipAddress(_resources.GetAudioClipProvider("audio86b"), "missing");
            var request = AudioKit.PlayMusicAsync(invalid);
            var deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!request.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.AreEqual(AudioPlayEnd.Failed, request.EndReason);
            Assert.NotNull(request.Error);
            Assert.IsTrue(playing.IsValid);
            var retry = AudioKit.PlayVoiceAsync(invalid);
            deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!retry.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.AreEqual(AudioPlayEnd.Failed, retry.EndReason);
            Assert.IsTrue(playing.IsValid);
        }

        [UnityTest]
        public IEnumerator NaturalFinishIsTerminalBeforeCallbackButResourceRemainsHeldUntilReturn()
        {
            var address = CreateClip(seconds: 1);
            AudioPlayRequest request = null;
            AudioClip finishedClip = null;
            var callbacks = 0;
            request = AudioKit.PlaySoundAsync(address, onFinished: (clip, handle) =>
            {
                Assert.IsTrue(request.IsDone);
                Assert.AreEqual(AudioPlayEnd.Completed, request.EndReason);
                Assert.IsTrue(handle.IsValid, "Preserve the existing natural callback window; the original Voice is finalized after return.");
                Assert.AreEqual(request.Handle, handle);
                Assert.Greater(_resources.GetDiagnosticsSnapshot().LeaseCount, 0);
                Assert.AreEqual(AudioDataLoadState.Loaded, clip.loadState);
                request.Cancel();
                Assert.AreEqual(AudioPlayEnd.Completed, request.EndReason);
                finishedClip = clip;
                callbacks++;
            });
            yield return WaitAccepted(request);
            var deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!request.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.AreEqual(1, callbacks);
            Assert.AreEqual(AudioPlayEnd.Completed, request.EndReason);
            AssertResourcesReturned(address, finishedClip);
        }

        [UnityTest]
        public IEnumerator NamedBackgroundCompressedAndStreamingWaitForDataWithoutChangingGameImporter()
        {
            foreach (var type in new[] { AudioClipLoadType.DecompressOnLoad, AudioClipLoadType.CompressedInMemory, AudioClipLoadType.Streaming })
            {
                var address = CreateClip(type, type == AudioClipLoadType.DecompressOnLoad ? AudioCompressionFormat.PCM : AudioCompressionFormat.Vorbis, true, type == AudioClipLoadType.DecompressOnLoad ? 120 : 3);
                var starts = 0;
                var request = AudioKit.PlayMusicAsync(address, onStarted: (clip, original) =>
                {
                    Assert.AreEqual(AudioDataLoadState.Loaded, clip.loadState);
                    Assert.IsTrue(original.IsValid);
                    starts++;
                });
                yield return WaitAccepted(request);
                var loaded = request.Handle.Voice.Clip;
                Assert.AreEqual(type, loaded.loadType);
                Assert.IsFalse(loaded.preloadAudioData);
                Assert.IsTrue(loaded.loadInBackground);
                var source = request.Handle.Voice.Source;
                yield return new WaitForSecondsRealtime(.1f);
                Assert.IsTrue(source.isPlaying);
                Assert.Greater(source.timeSamples, 0);
                Assert.AreEqual(1, starts);
                TestContext.WriteLine("Named Resource {0}: Loaded before Started, timeSamples={1}", type, source.timeSamples);
                request.Cancel();
                AssertResourcesReturned(address, loaded);
            }
        }

        [UnityTest]
        public IEnumerator CrossfadeTailAndCallerInactiveOneShotKeepActualResourcesAlive()
        {
            var first = CreateClip();
            var second = CreateClip();
            var old = AudioKit.PlayMusicNamed(first);
            var clip = old.Voice.Clip;
            // native 回调可在长导入帧的 Update 前完成；先冻结尾轨，避免用旧帧 delta 吞掉观察窗口。
            var next = AudioKit.CrossFadeMusicAsync(second, .2f, onStarted: (_, _) => old.Pause());
            yield return WaitAccepted(next);
            Assert.IsTrue(old.IsValid);
            Assert.IsTrue(clip);
            old.Resume();
            yield return new WaitForSecondsRealtime(.3f);
            Assert.IsFalse(old.IsValid);
            AssertResourcesReturned(first, clip, 1);
            Assert.IsTrue(next.Handle.IsValid);
            next.Cancel();
            var caller = new GameObject("named-one-shot-owner");
            try
            {
                var shot = AudioKit.PlaySoundAsync(first);
                caller.SetActive(false);
                yield return WaitAccepted(shot);
                var shotClip = shot.Handle.Voice.Clip;
                Assert.IsTrue(shotClip);
                Assert.IsTrue(shot.Handle.IsPlaying);
                var deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (!shot.IsDone && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    yield return null;
                }

                Assert.AreEqual(AudioPlayEnd.Completed, shot.EndReason);
                AssertResourcesReturned(first, shotClip);
            }
            finally
            {
                Object.Destroy(caller);
            }
        }

    }
}
#endif
