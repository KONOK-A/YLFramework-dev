using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Audio;
using YLFramework.Resource;
using YLFramework.Tests.Resource;
using YLFramework.Tests.ResourceBuild;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    [Category("YL.Special.AudioNative")]
    public class AudioResourceBundleTests
    {
        private const string Key = "YL.Audio86b.Native.";

        [OneTimeSetUp]
        public void BuildIndependentPublication()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            ResourceSessionTests.EnsureRestorableScenes();
            var protectedBefore = ProtectedDigest();
            var folder = "Assets/Tests/YLFramework/Fixtures/Audio/Resource-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures/Audio", Path.GetFileName(folder)), Is.Not.Empty);
            string workspace = null, root = null;
            try
            {
                AssetDatabase.CreateFolder(folder, "Content");
                var project = ScriptableObject.CreateInstance<ResourceProject>();
                project.projectId = "yl-audio86b-" + Guid.NewGuid().ToString("N");
                project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(folder + "/Content") });
                var shared = new Material(Shader.Find("Unlit/Color")) { name = "SharedAudioDependency" };
                AssetDatabase.CreateAsset(shared, folder + "/Content/Shared.mat");
                foreach (var name in new[] { "first", "second" })
                {
                    var wave = folder + "/Content/" + name + ".wav";
                    WriteWave(wave);
                    AssetDatabase.ImportAsset(wave, ImportAssetOptions.ForceSynchronousImport);
                    var holder = new GameObject("AudioDependency-" + name, typeof(AudioSource), typeof(MeshRenderer));
                    var prefab = folder + "/Content/" + name + ".prefab";
                    try
                    {
                        holder.GetComponent<AudioSource>().clip = AssetDatabase.LoadAssetAtPath<AudioClip>(wave);
                        holder.GetComponent<MeshRenderer>().sharedMaterial = shared;
                        PrefabUtility.SaveAsPrefabAsset(holder, prefab);
                    }
                    finally
                    {
                        Object.DestroyImmediate(holder);
                    }
                    project.groups.Add(new ResourceGroup
                    {
                        id = name,
                        selections = new List<ResourceSelection>
                    {
                        new() { guid = AssetDatabase.AssetPathToGUID(wave) },
                        new() { guid = AssetDatabase.AssetPathToGUID(prefab) }
                    }
                    });
                }
                AssetDatabase.CreateAsset(project, folder + "/Project.asset");
                var expectedWorkspace = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, EditorUserBuildSettings.activeBuildTarget));
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(expectedWorkspace)), Is.False, "Never adopt an unknown workspace.");
                workspace = expectedWorkspace;
                var build = ResourceBundleBuilder.Build(project, EditorUserBuildSettings.activeBuildTarget);
                root = ResourcePublisher.GetReleaseRoot(build);
                var release = ResourcePublisher.Publish(build, "audio86b-v1");
                var first = release.Catalog.Assets.Single(x => x.Address == "first.wav");
                Assert.That(release.Catalog.Bundles.Single(x => x.LogicalId == first.BundleId).Dependencies, Is.Not.Empty,
                    "The fixture must exercise a real manifest dependency, not a fabricated reference count.");
                SessionState.SetString(Key + "folder", folder);
                SessionState.SetString(Key + "guid", AssetDatabase.AssetPathToGUID(folder));
                SessionState.SetString(Key + "workspace", workspace);
                SessionState.SetString(Key + "root", root);
                SessionState.SetString(Key + "path", release.DirectoryPath);
                SessionState.SetString(Key + "project", project.projectId);
                SessionState.SetString(Key + "digest", release.Catalog.CatalogDigest);
                SessionState.SetBool(Key + "owned", true);
                SessionState.SetString(Key + "protected", protectedBefore);
            }
            catch
            {
                AssetDatabase.DeleteAsset(folder);
                if (workspace != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(workspace), true);
                }

                if (root != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
                }

                throw;
            }
        }

        [OneTimeTearDown]
        public void CleanupPublication()
        {
            if (!SessionState.GetBool(Key + "owned", false))
            {
                return;
            }

            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            Assert.That(ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")), "Protect the game, historical Bundles and project settings during native audio verification.");
            var folder = SessionState.GetString(Key + "folder", "");
            Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(SessionState.GetString(Key + "guid", "")));
            AssetDatabase.DeleteAsset(folder);
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "workspace", "")), true);
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(SessionState.GetString(Key + "root", "")), true);
            foreach (var key in new[] { "folder", "guid", "workspace", "root", "path", "project", "digest", "protected" })
            {
                SessionState.EraseString(Key + key);
            }

            SessionState.EraseBool(Key + "owned");
        }

        private static ResourceLocalSnapshot Snapshot() => ResourceLocalSnapshot.Open(SessionState.GetString(Key + "path", ""),
            SessionState.GetString(Key + "project", ""), SessionState.GetString(Key + "digest", ""), "StandaloneWindows64", "audio86b-v1");

        private static string ProtectedDigest() => ResourceCatalogCodecTests.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
            new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings" }
                .SelectMany(x => ResourceReleaseFixture.Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x))).Select(file => x + file)))));

        private static ResourceSystem Initialize(ResourceLocalSnapshot snapshot)
        {
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { snapshot.CreateCatalog() }));
            return initialized.TakeResult();
        }

        private static IEnumerator Accepted(AudioPlayRequest request)
        {
            var until = Time.realtimeSinceStartupAsDouble + 10;
            while (!request.Handle.IsValid && !request.IsDone && Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
            }

            Assert.That(request.Handle.IsPlaying, Is.True, request.Error?.ToString() ?? request.EndReason.ToString());
        }

        [UnityTearDown]
        public IEnumerator RestoreSession()
        {
            if (EditorApplication.isPlaying)
            {
                AudioKit.Shutdown();
                typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var until = Time.realtimeSinceStartupAsDouble + 10;
                while (AssetBundle.GetAllLoadedAssetBundles().Any() && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }

                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            }
            var cleanup = new ResourceSessionTests().TearDown();
            while (cleanup.MoveNext())
            {
                yield return cleanup.Current;
            }
        }

        [UnityTest]
        public IEnumerator RealAssetBundleSyncAsyncDependenciesAndFinalPhysicalUnload()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return PlaybackBody();
        }

        private static IEnumerator PlaybackBody()
        {
            using var audio = new AudioState();
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var provider = system.GetAudioClipProvider(snapshot.Document.ProjectId);
            var first = new AudioClipAddress(provider, "first.wav");
            var second = new AudioClipAddress(provider, "second.wav");
            using var independent = system.Acquire<AudioClip>(new ResourceAddress(provider.ProjectId, first.Key));
            var clip = independent.Asset;
            var music = AudioKit.PlayMusicNamed(first);
            var sound = AudioKit.PlaySoundNamed(first, AudioSoundOptions.MusicDefaults);
            var voice = AudioKit.PlayVoiceNamed(first, AudioSoundOptions.MusicDefaults);
            using var channel = AudioKit.RegisterChannel("audio86b/named", AudioCategory.Sound, AudioChannelMode.Overlap);
            var named = channel.PlayNamed(first, AudioSoundOptions.MusicDefaults);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(2), "Audio shares one backend lease while Resource consumers keep independent leases.");
            independent.Dispose();
            var native = AssetBundle.GetAllLoadedAssetBundles().ToArray();
            Assert.That(native.Length, Is.GreaterThan(1));
            yield return new WaitForSecondsRealtime(.15f);
            Assert.That(music.Voice.Source.timeSamples, Is.GreaterThan(0));
            var samples = new float[1024];
            var outputDeadline = Time.realtimeSinceStartupAsDouble + 1;
            do
            {
                AudioListener.GetOutputData(samples, 0);
                if (samples.Sum(x => x * x) > .0001f)
                {
                    break;
                }

                yield return null;
            } while (Time.realtimeSinceStartupAsDouble < outputDeadline);
            var sourceSamples = new float[1024];
            music.Voice.Source.GetOutputData(sourceSamples, 0);
            TestContext.WriteLine($"AudioNative actual device: batch={Application.isBatchMode}, graphics={SystemInfo.graphicsDeviceType}, sampleRate={AudioSettings.outputSampleRate}, speakers={AudioSettings.driverCapabilities}, editorMute={EditorUtility.audioMasterMute}, sourceEnergy={sourceSamples.Sum(x => x * x)}, listenerEnergy={samples.Sum(x => x * x)}, listeners={Object.FindObjectsOfType<AudioListener>().Length}.");
            Assert.That(samples.Sum(x => x * x), Is.GreaterThan(.0001f),
                $"Actual AudioSource output must contain the fixture signal: volume={music.Voice.Source.volume}, mute={music.Voice.Source.mute}, virtual={music.Voice.Source.isVirtual}, listenerPause={AudioListener.pause}, listenerVolume={AudioListener.volume}, data={clip.loadState}, time={music.Voice.Source.timeSamples}.");
            music.Pause();
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(false);
            yield return null;
            Assert.That(clip && sound.IsPlaying && voice.IsPlaying && named.IsPlaying, Is.True);
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
            music.Resume();
            var next = AudioKit.CrossFadeMusicAsync(second, .25f, onStarted: (_, _) => music.Pause());
            yield return Accepted(next);
            sound.Stop();
            voice.Stop();
            named.Stop();
            AudioKit.ClearClipCache();
            Assert.That(music.IsValid && clip, Is.True, "The crossfade tail is now the only first-Clip user.");
            music.Resume();
            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            Assert.That(system.IsAvailable, Is.True);
            yield return new WaitForSecondsRealtime(.4f);
            Assert.That(music.IsValid, Is.False);
            Assert.That(clip == null, Is.True, "Tail completion must physically unload its root Bundle, even while the new track retains a shared dependency.");
            Assert.That(native.Any(x => x), Is.True);
            var nextClip = next.Handle.Voice.Clip;
            next.Cancel();
            Assert.That(nextClip == null, Is.True);
            Assert.That(native.All(x => !x), Is.True);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);

            AudioKit.ClipCacheCapacity = 1;
            AudioKit.ClipCacheIdleSeconds = 60;
            var cached = AudioKit.AcquireClip(first);
            var cachedClip = cached.Clip;
            cached.Dispose();
            cached.Dispose();
            Assert.IsTrue(cachedClip);
            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            AudioKit.ClearClipCache();
            Assert.That(cachedClip == null, Is.True);
            var explicitOwner = AudioKit.AcquireClip(first);
            var explicitClip = explicitOwner.Clip;
            AudioKit.Shutdown();
            Assert.IsTrue(explicitClip, "An explicit caller lease survives Audio Shutdown.");
            using (var busy = system.ShutdownAsync())
            {
                Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            }

            explicitOwner.Dispose();
            explicitOwner.Dispose();
            Assert.That(explicitClip == null, Is.True);
            var borrowed = AudioClip.Create("external86b", 44100, 1, 44100, false);
            try
            {
                AudioState.EnablePlayback();
                Assert.That(AudioKit.PlayMusic(borrowed).IsPlaying, Is.True);
                AudioKit.Shutdown();
                Assert.IsTrue(borrowed);
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            }
            finally
            {
                Object.Destroy(borrowed);
            }
            using var shutdown = system.ShutdownAsync();
            Assert.That(shutdown.TakeResult(), Is.True);
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator NativeCancelAudioShutdownRetryAndResourceExitDrainLateResults()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return PendingBody();
        }

        private static IEnumerator PendingBody()
        {
            using var preferences = new AudioTestSettings();
            // 模拟用户持久化关闭 Voice；每次 Audio Shutdown 后测试须显式恢复临时播放条件。
            PlayerPrefs.SetInt("KEY_AUDIO_MANAGER_VOICE_ON", 0);
            using var audio = new AudioState();
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var provider = system.GetAudioClipProvider(snapshot.Document.ProjectId);
            var address = new AudioClipAddress(provider, "first.wav");
            var canceled = AudioKit.PlaySoundAsync(address, AudioSoundOptions.MusicDefaults);
            var old = AudioKit.PlayVoiceAsync(address, AudioSoundOptions.MusicDefaults);
            Assert.That(system.GetDiagnosticsSnapshot().ProducerCount, Is.GreaterThan(0));
            canceled.Cancel();
            AudioKit.Shutdown();
            Assert.That(canceled.EndReason, Is.EqualTo(AudioPlayEnd.Canceled));
            Assert.That(old.EndReason, Is.EqualTo(AudioPlayEnd.Shutdown));
            AudioState.EnablePlayback();
            var retry = AudioKit.PlayVoiceAsync(address, AudioSoundOptions.MusicDefaults);
            yield return Accepted(retry);
            Assert.That(canceled.Handle.IsValid || old.Handle.IsValid, Is.False);
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            retry.Cancel();
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);

            var pending = AudioKit.PlayMusicAsync(new AudioClipAddress(provider, "second.wav"));
            Assert.That(pending.IsAssetReady, Is.False);
            Assert.That(system.GetDiagnosticsSnapshot().ProducerCount, Is.GreaterThan(0));
            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Assert.That(pending.IsDone, Is.True);
            Assert.That(pending.Error, Is.TypeOf<ResourceException>());
            Assert.That(((ResourceException)pending.Error).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            AudioKit.Shutdown();
            var until = Time.realtimeSinceStartupAsDouble + 10;
            while ((system.GetDiagnosticsSnapshot().ProducerCount != 0 || system.GetDiagnosticsSnapshot().BundleRecordCount != 0) &&
                Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
            }

            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
            Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            Assert.That(pending.Handle.IsValid, Is.False);
            Assert.That(Assert.Throws<ResourceException>(() => provider.Load(address.Key)).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator ProviderProgressReentrantExitNeverPublishesInvalidClip()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return ProgressExitBody();
        }

        private static IEnumerator ProgressExitBody()
        {
            var snapshot = Snapshot();
            var system = Initialize(snapshot);
            var provider = system.GetAudioClipProvider(snapshot.Document.ProjectId);
            var calls = 0;
            var exitedWithClaimedLease = false;
            AudioClipLease delivered = null;
            Exception failure = null;
            provider.LoadAsync("first.wav", (lease, error) =>
            {
                delivered = lease;
                failure = error;
                calls++;
            },
                value =>
                {
                    if (value < 1 || system.GetDiagnosticsSnapshot().LeaseCount == 0)
                    {
                        return;
                    }

                    exitedWithClaimedLease = true;
                    typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                });
            var until = Time.realtimeSinceStartupAsDouble + 10;
            while (calls == 0 && Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
            }

            try
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(exitedWithClaimedLease, Is.True, "Exercise the final delivery window, not just an earlier pending progress sample.");
                Assert.That(delivered, Is.Null, "A progress observer ended the Resource session before delivery; no invalid success lease may escape.");
                Assert.That(failure, Is.TypeOf<ResourceException>());
                Assert.That(((ResourceException)failure).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
                while ((system.GetDiagnosticsSnapshot().RecordCount != 0 || system.GetDiagnosticsSnapshot().BundleRecordCount != 0) &&
                    Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }

                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
                Assert.That(system.GetDiagnosticsSnapshot().BundleRecordCount, Is.Zero);
                Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            }
            finally
            {
                delivered?.Dispose();
            }
        }

        private sealed class AudioState : IDisposable
        {
            private readonly bool _wasMusicOn;
            private readonly bool _wasVoiceOn;
            private readonly bool _wasSoundOn;
            private readonly bool _editorMute;
            private readonly float _musicVolume;
            private readonly float _voiceVolume;
            private readonly float _soundVolume;
            private readonly GameObject _listener;
            internal AudioState()
            {
                AudioKit.Shutdown();
                var settings = AudioKit.Settings;
                _wasMusicOn = settings.IsMusicOn.Value;
                _wasVoiceOn = settings.IsVoiceOn.Value;
                _wasSoundOn = settings.IsSoundOn.Value;
                _musicVolume = settings.MusicVolume.Value;
                _voiceVolume = settings.VoiceVolume.Value;
                _soundVolume = settings.SoundVolume.Value;
                _editorMute = EditorUtility.audioMasterMute;
                EditorUtility.audioMasterMute = false;
                EnablePlayback();
                AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
                _listener = new GameObject("Audio86b.Listener", typeof(AudioListener));
            }
            internal static void EnablePlayback()
            {
                var settings = AudioKit.Settings;
                settings.IsMusicOn.SetValueWithoutCallback(true);
                settings.IsVoiceOn.SetValueWithoutCallback(true);
                settings.IsSoundOn.SetValueWithoutCallback(true);
                settings.MusicVolume.SetValueWithoutCallback(1);
                settings.VoiceVolume.SetValueWithoutCallback(1);
                settings.SoundVolume.SetValueWithoutCallback(1);
            }
            public void Dispose()
            {
                EditorUtility.audioMasterMute = _editorMute;
                AudioKit.Shutdown();
                var settings = AudioKit.Settings;
                settings.IsMusicOn.SetValueWithoutCallback(_wasMusicOn);
                settings.IsVoiceOn.SetValueWithoutCallback(_wasVoiceOn);
                settings.IsSoundOn.SetValueWithoutCallback(_wasSoundOn);
                settings.MusicVolume.SetValueWithoutCallback(_musicVolume);
                settings.VoiceVolume.SetValueWithoutCallback(_voiceVolume);
                settings.SoundVolume.SetValueWithoutCallback(_soundVolume);
                Object.Destroy(_listener);
            }
        }

        internal static void WriteWave(string path)
        {
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path))), Is.True);
            const int Samples = 44100 * 3;
            using var writer = new BinaryWriter(File.Create(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
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
            for (var i = 0; i < Samples; i++)
            {
                writer.Write((short)(Math.Sin(i * 2 * Math.PI * 330 / 44100) * 4000));
            }
        }
    }
}
