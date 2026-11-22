#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    [Category("YL.Resource.Addressables.Regression")]
    public class AudioNamedResourcePlaybackTests
    {
        private string _folder;
        private GameObject _listener;
        private float _timeScale;
        private ResourceSystem _resources;
        private ResourceStartup _startup;
        private ResourceLocationMap _locator;
        private ClipProvider _provider;
        private AssetLease<AudioClip> _warm;
        private string _project;
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
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Never replace an external Resource authority.");
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
            _project = "audio86b-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures/Audio", Path.GetFileName(_folder));
            AssetDatabase.CreateFolder(_folder, "Clips");
            _nextClip = 0;
            _names = Enumerable.Range(0, 8).Select(i => "audio86b-" + Guid.NewGuid().ToString("N")).ToArray();
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using var start = _startup.StartAsync();
            yield return WaitFor(() => start.IsDone, "official Resource startup");
            Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), start.Error?.ToString());
            _resources = start.TakeResult().Resources;
            _locator = new ResourceLocationMap(_project);
            _provider = new ClipProvider(_project);
            Addressables.ResourceManager.ResourceProviders.Add(_provider);
            Addressables.AddResourceLocator(_locator);

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
            ReleaseWarm();
            _provider?.FinishPending();
            Time.timeScale = _timeScale;
            if (_startup != null)
            {
                using var shutdown = _startup.StopAsync();
                yield return WaitFor(() => shutdown.IsDone, "Resource stop after Audio shutdown");
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
                Assert.That(shutdown.TakeResult(), Is.True);
            }
            if (_resources != null)
            {
                var diagnostics = _resources.GetDiagnostics();
                Assert.That(diagnostics.LeaseCount, Is.Zero);
                Assert.That(diagnostics.ScopeCount, Is.Zero);
                Assert.That(diagnostics.RequestCount, Is.Zero);
                Assert.That(diagnostics.PendingWorkCount, Is.Zero);
                Assert.That(diagnostics.DrainingHandleCount, Is.Zero);
                Assert.That(diagnostics.Errors, Is.Empty);
            }
            if (_provider != null)
            {
                Assert.That(_provider.PendingCount, Is.Zero);
                Assert.That(_provider.LiveCount, Is.Zero);
                Assert.That(_provider.Releases, Is.EqualTo(_provider.Provides), "Every official provider operation must actually be released.");
            }
            if (_locator != null)
            {
                Addressables.RemoveResourceLocator(_locator);
                _locator = null;
            }
            if (_provider != null)
            {
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
                _provider = null;
            }
            if (_listener)
            {
                Object.Destroy(_listener);
            }

            AssetDatabase.DeleteAsset(_folder);
            _resources = null;
            _startup = null;
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
            var path = _folder + "/Clips/" + name + ".wav";
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
            Assert.That(AssetDatabase.LoadAssetAtPath<AudioClip>(path), Is.Not.Null);
            var key = _project + "/clip/" + index;
            _locator.Add(key, new ResourceLocationBase(key, path, _provider.ProviderId, typeof(AudioClip)) { Data = new ClipContent(path) });
            return new AudioClipAddress(_resources.GetAudioClipProvider(_project), "clip/" + index);
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
        private string OfficialKey(AudioClipAddress address) => _project + "/" + address.Key;

        private IEnumerator Warm(AudioClipAddress address)
        {
            Assert.That(_warm, Is.Null);
            using var request = _resources.AcquireAsync<AudioClip>(OfficialKey(address));
            yield return WaitFor(() => request.IsDone, "explicit async warmup");
            Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
            _warm = request.TakeResult();
        }

        private void ReleaseWarm()
        {
            _warm?.Dispose();
            _warm = null;
        }

        private IEnumerator AssertResourcesReturned(AudioClipAddress address, AudioClip clip, int otherLeases = 0)
        {
            var key = OfficialKey(address);
            yield return WaitFor(() => _resources.GetDiagnostics().LeaseCount == otherLeases &&
                _resources.GetDiagnostics().DrainingHandleCount == 0 && _provider.PendingCount == 0 && _provider.LiveFor(key) == 0,
                "official provider release for " + key);
            Assert.That(_resources.GetDiagnostics().LeaseCount, Is.EqualTo(otherLeases), address.Key);
            Assert.That(_provider.ReleasesFor(key), Is.EqualTo(_provider.ProvidesFor(key)).And.GreaterThan(0), address.Key);
            bool loaded = _resources.TryAcquireLoaded<AudioClip>(key, out var unexpected);
            using (unexpected)
            {
                Assert.That(loaded, Is.False, "The retired Audio entry must not leave a hidden hot YL lease.");
            }
            Assert.IsTrue(clip, "Returning the provider reference must not destroy the fixture's independently owned imported AudioClip.");
        }

        private static IEnumerator WaitFor(Func<bool> completed, string operation)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!completed() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }
            Assert.That(completed(), Is.True, operation + " timed out.");
        }

        private sealed class ClipContent
        {
            internal readonly string Path;
            internal AudioClip Clip;
            internal ClipContent(string path) => Path = path;
        }

        // The fixture owns the imported asset; the provider owns only its official operation reference.
        private sealed class ClipProvider : ResourceProviderBase
        {
            private readonly List<ProvideHandle> _pending = new();
            private readonly Dictionary<string, int> _provides = new();
            private readonly Dictionary<string, int> _releases = new();
            private readonly Dictionary<string, int> _live = new();
            internal bool Delay;
            internal int Provides => _provides.Values.Sum();
            internal int Releases => _releases.Values.Sum();
            internal int LiveCount => _live.Values.Sum();
            internal int PendingCount => _pending.Count;
            internal int ProvidesFor(string key) => _provides.TryGetValue(key, out var count) ? count : 0;
            internal int ReleasesFor(string key) => _releases.TryGetValue(key, out var count) ? count : 0;
            internal int LiveFor(string key) => _live.TryGetValue(key, out var count) ? count : 0;
            internal ClipProvider(string id) => m_ProviderId = id;

            public override void Provide(ProvideHandle handle)
            {
                var key = handle.Location.PrimaryKey;
                _provides[key] = ProvidesFor(key) + 1;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("Audio tests must not synchronously block an official producer."));
                if (Delay) _pending.Add(handle);
                else Complete(handle);
            }

            internal void FinishPending()
            {
                Delay = false;
                var pending = _pending.ToArray();
                _pending.Clear();
                foreach (var handle in pending) Complete(handle);
            }

            private void Complete(ProvideHandle handle)
            {
                var content = (ClipContent)handle.Location.Data;
                content.Clip = AssetDatabase.LoadAssetAtPath<AudioClip>(content.Path);
                Assert.That(content.Clip, Is.Not.Null, content.Path);
                var key = handle.Location.PrimaryKey;
                _live[key] = LiveFor(key) + 1;
                handle.Complete(content.Clip, true, null);
            }

            public override void Release(IResourceLocation location, object asset)
            {
                var key = location.PrimaryKey;
                Assert.That(asset, Is.SameAs(((ClipContent)location.Data).Clip));
                Assert.That(LiveFor(key), Is.GreaterThan(0), "Release must return a real outstanding official operation.");
                _live[key] = LiveFor(key) - 1;
                _releases[key] = ReleasesFor(key) + 1;
            }
        }

        [UnityTest]
        public IEnumerator RealResourcesSyncDisabledReplayPauseAndIdleRelease()
        {
            var address = CreateClip();
            var cold = Assert.Throws<ResourceException>(() => { using var unexpected = address.Provider.Load(address.Key); });
            Assert.That(cold.Kind, Is.EqualTo(ResourceError.InProgress));
            Assert.That(_provider.Provides, Is.Zero, "Cold synchronous access must not start an official producer.");
            yield return Warm(address);
            AudioKit.ClipCacheCapacity = 1;
            AudioKit.ClipCacheIdleSeconds = .1;
            var handle = AudioKit.PlayMusicNamed(address);
            ReleaseWarm();
            var clip = handle.Voice.Clip;
            Assert.IsTrue(handle.IsValid);
            Assert.Greater(_resources.GetDiagnostics().LeaseCount, 0);
            AudioKit.Settings.IsMusicOn.Value = false;
            Time.timeScale = 0;
            yield return new WaitForSecondsRealtime(.2f);
            Assert.IsTrue(clip);
            Assert.IsTrue(handle.IsValid);
            Assert.Greater(_resources.GetDiagnostics().LeaseCount, 0);
            Assert.That(_provider.ReleasesFor(OfficialKey(address)), Is.Zero);
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
            Assert.That(_provider.ProvidesFor(OfficialKey(address)), Is.EqualTo(1), "An idle cache hit must retain the original backend operation.");
            reused.Stop();
            yield return new WaitForSecondsRealtime(.2f);
            yield return AssertResourcesReturned(address, clip);
        }

        [UnityTest]
        public IEnumerator RealResourcesSharedCancelLateShutdownAndRetry()
        {
            var address = CreateClip();
            _provider.Delay = true;
            var canceled = AudioKit.PlaySoundAsync(address, AudioSoundOptions.MusicDefaults);
            var live = AudioKit.PlaySoundAsync(address, AudioSoundOptions.MusicDefaults);
            canceled.Cancel();
            yield return WaitFor(() => _provider.PendingCount == 1, "shared official Audio producer");
            Assert.That(_provider.ProvidesFor(OfficialKey(address)), Is.EqualTo(1));
            Assert.That(live.IsDone, Is.False, "Canceling a sibling must not consume the shared producer.");
            _provider.FinishPending();
            yield return WaitAccepted(live);
            Assert.AreEqual(AudioPlayEnd.Canceled, canceled.EndReason);
            var clip = live.Handle.Voice.Clip;
            Assert.IsTrue(clip);
            live.Cancel();
            yield return AssertResourcesReturned(address, clip);
            _provider.Delay = true;
            var lateStarts = 0;
            var late = AudioKit.PlayMusicAsync(address, onStarted: (_, _) => lateStarts++);
            yield return WaitFor(() => _provider.PendingCount == 1, "late Audio producer");
            AudioKit.Shutdown();
            Assert.AreEqual(AudioPlayEnd.Shutdown, late.EndReason);
            Assert.That(_resources.IsAvailable, Is.True, "Audio shutdown must not stop its externally owned Resource startup.");
            Assert.That(_provider.ReleasesFor(OfficialKey(address)), Is.EqualTo(1), "The second, pending operation still owes its own release.");
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
            Assert.That(_locator.Locate(OfficialKey(address), typeof(AudioClip), out var locations), Is.True);
            // Bridge the native completion until the new Audio request has accepted its independent lease.
            var independent = Addressables.ResourceManager.ProvideResource<AudioClip>(locations.Single());
            AudioPlayRequest retry = null;
            try
            {
                retry = AudioKit.PlayMusicAsync(address);
                Assert.That(retry.IsDone, Is.False);
                Assert.That(_provider.ProvidesFor(OfficialKey(address)), Is.EqualTo(2), "Restarting Audio must not replace the still-pending official operation.");
                _provider.FinishPending();
                yield return WaitAccepted(retry);
            }
            finally
            {
                Addressables.ResourceManager.Release(independent);
            }
            Assert.IsFalse(late.Handle.IsValid);
            Assert.That(lateStarts, Is.Zero);
            Assert.That(late.EndReason, Is.EqualTo(AudioPlayEnd.Shutdown));
            Assert.IsTrue(retry.Handle.IsPlaying);
            Assert.That(_resources.GetDiagnostics().LeaseCount, Is.EqualTo(1), "The retired Audio entry must return only its own late lease.");
            Assert.That(_provider.ReleasesFor(OfficialKey(address)), Is.EqualTo(1), "The new Audio owner still retains the shared official operation.");
            retry.Cancel();
            yield return AssertResourcesReturned(address, clip);
        }

        [UnityTest]
        public IEnumerator RealResourcesMissingAssetFailsAndCanRetryWithoutStoppingExistingMusic()
        {
            var valid = CreateClip();
            yield return Warm(valid);
            var playing = AudioKit.PlayMusicNamed(valid);
            ReleaseWarm();
            var invalid = new AudioClipAddress(_resources.GetAudioClipProvider(_project), "missing");
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
                Assert.Greater(_resources.GetDiagnostics().LeaseCount, 0);
                Assert.That(_provider.LiveFor(OfficialKey(address)), Is.EqualTo(1));
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
            yield return AssertResourcesReturned(address, finishedClip);
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
                yield return AssertResourcesReturned(address, loaded);
            }
        }

        [UnityTest]
        public IEnumerator CrossfadeTailAndCallerInactiveOneShotKeepActualResourcesAlive()
        {
            var first = CreateClip();
            var second = CreateClip();
            yield return Warm(first);
            var old = AudioKit.PlayMusicNamed(first);
            ReleaseWarm();
            var clip = old.Voice.Clip;
            // native 回调可在长导入帧的 Update 前完成；先冻结尾轨，避免用旧帧 delta 吞掉观察窗口。
            var next = AudioKit.CrossFadeMusicAsync(second, .2f, onStarted: (_, _) => old.Pause());
            yield return WaitAccepted(next);
            Assert.IsTrue(old.IsValid);
            Assert.IsTrue(clip);
            Assert.That(_resources.GetDiagnostics().LeaseCount, Is.EqualTo(2), "Foreground and fading tail retain separate backend leases.");
            Assert.That(_provider.LiveFor(OfficialKey(first)), Is.EqualTo(1));
            Assert.That(_provider.LiveFor(OfficialKey(second)), Is.EqualTo(1));
            old.Resume();
            yield return new WaitForSecondsRealtime(.3f);
            Assert.IsFalse(old.IsValid);
            yield return AssertResourcesReturned(first, clip, 1);
            Assert.IsTrue(next.Handle.IsValid);
            var nextClip = next.Handle.Voice.Clip;
            next.Cancel();
            yield return AssertResourcesReturned(second, nextClip);
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
                yield return AssertResourcesReturned(first, shotClip);
            }
            finally
            {
                Object.Destroy(caller);
            }
        }

    }
}
#endif
