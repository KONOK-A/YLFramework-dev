using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Audio;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    [Category("YL.Special.AudioNative")]
    public class AudioResourceBundleTests
    {
        private const string Key = "YL.AudioAA.Native.";

        [OneTimeSetUp]
        public void BuildOfficialAudioContent()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                AudioNativeFixture.VerifyFolder(Key);
                return;
            }
            AudioNativeFixture.RequireBuildEnvironment();
            SessionState.SetString(Key + "protected", AudioNativeFixture.ProtectedDigest());
            var folder = AudioNativeFixture.CreateFolder("__YLAudioNative", Key);
            var project = "yl-audio-native-" + Guid.NewGuid().ToString("N");
            SessionState.SetString(Key + "project", project);
            AssetDatabase.CreateFolder(folder, "Content");
            var content = folder + "/Content";
            var shared = new Material(Shader.Find("Unlit/Color")) { name = "SharedAudioDependency" };
            var sharedPath = content + "/Shared.mat";
            AssetDatabase.CreateAsset(shared, sharedPath);
            AudioNativeFixture.InOwnAuthoringScene(() =>
            {
                foreach (var name in new[] { "first", "second" })
                {
                    var wave = content + "/" + name + ".wav";
                    WriteWave(wave);
                    AssetDatabase.ImportAsset(wave, ImportAssetOptions.ForceSynchronousImport);
                    var holder = new GameObject("AudioDependency-" + name, typeof(AudioSource), typeof(MeshRenderer));
                    try
                    {
                        holder.GetComponent<AudioSource>().clip = AssetDatabase.LoadAssetAtPath<AudioClip>(wave);
                        holder.GetComponent<MeshRenderer>().sharedMaterial = shared;
                        Assert.That(PrefabUtility.SaveAsPrefabAsset(holder, content + "/" + name + ".prefab"), Is.Not.Null);
                    }
                    finally
                    {
                        Object.DestroyImmediate(holder);
                    }
                }
            });
            var groups = new List<string>
            {
                AudioNativeFixture.AddGroup(project + "-shared", new Dictionary<string, string> { [sharedPath] = project + "/shared.mat" })
            };
            SessionState.SetString(Key + "groups", string.Join(";", groups));
            foreach (var name in new[] { "first", "second" })
            {
                groups.Add(AudioNativeFixture.AddGroup(project + "-" + name, new Dictionary<string, string>
                {
                    [content + "/" + name + ".wav"] = project + "/" + name + ".wav",
                    [content + "/" + name + ".prefab"] = project + "/" + name + ".prefab"
                }));
                SessionState.SetString(Key + "groups", string.Join(";", groups));
            }
            var built = AudioNativeFixture.Build();
            SessionState.SetString(Key + "state", built.ContentStatePath);
            SessionState.SetBool(Key + "owned", true);
        }

        [OneTimeTearDown]
        public void CleanupOfficialAudioContent()
        {
            if (SessionState.GetString(Key + "folder", "") == "")
            {
                return;
            }
            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(AudioNativeFixture.ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")),
                "Keep original game, scene, package, historical content and project-setting bytes unchanged.");
            AudioNativeFixture.DeleteFolderAndGroups(Key);
            foreach (var name in new[] { "project", "protected", "state" })
            {
                SessionState.EraseString(Key + name);
            }
            SessionState.EraseBool(Key + "owned");
        }

        private static string Project => SessionState.GetString(Key + "project", "");

        [UnityTearDown]
        public IEnumerator RestoreSession() => AudioNativeFixture.RestorePlay();

        [UnityTest]
        public IEnumerator RealAssetBundleSyncAsyncDependenciesAndFinalPhysicalUnload()
        {
            AudioNativeFixture.PreparePlay();
            yield return new EnterPlayMode();
            yield return PlaybackBody();
        }

        private static IEnumerator PlaybackBody()
        {
            using var preferences = new AudioTestSettings();
            using var audio = new AudioState();
            ResourceStartup startup = null;
            yield return AudioNativeFixture.Start(Project, value => startup = value);
            var system = startup.Resources;
            var provider = system.GetAudioClipProvider(Project);
            var first = new AudioClipAddress(provider, "first.wav");
            var second = new AudioClipAddress(provider, "second.wav");
            var firstLocation = AudioNativeFixture.Location<AudioClip>(Project + "/first.wav");
            var secondLocation = AudioNativeFixture.Location<AudioClip>(Project + "/second.wav");
            var firstBundles = AudioNativeFixture.BundleDependencies(firstLocation);
            var secondBundles = AudioNativeFixture.BundleDependencies(secondLocation);
            var materialBundles = AudioNativeFixture.BundleDependencies(AudioNativeFixture.Location<Material>(Project + "/shared.mat"));
            Assert.That(materialBundles, Is.Not.Empty);
            Assert.That(materialBundles.All(item => firstBundles.Any(other => other.InternalId == item.InternalId) &&
                secondBundles.Any(other => other.InternalId == item.InternalId)), Is.True,
                "The real official content graph must share the separately authored material dependency.");
            var firstRoot = firstBundles.Where(item => !secondBundles.Any(other => other.InternalId == item.InternalId)).ToArray();
            Assert.That(firstRoot.Length, Is.EqualTo(1), "The first authored group must have its own physical root operation.");

            using var firstOperation = AudioNativeFixture.Pin<AudioClip>(firstLocation);
            using var rootOperation = AudioNativeFixture.Pin<IAssetBundleResource>(firstRoot[0]);
            using var sharedOperation = AudioNativeFixture.Pin<IAssetBundleResource>(materialBundles[0]);
            yield return AudioNativeFixture.Completed(firstOperation);
            yield return AudioNativeFixture.Completed(rootOperation);
            yield return AudioNativeFixture.Completed(sharedOperation);
            var nativeRoot = rootOperation.Handle.Result.GetAssetBundle();
            var nativeShared = sharedOperation.Handle.Result.GetAssetBundle();
            Assert.That(nativeRoot && nativeShared, Is.True);
            var wasHot = system.TryAcquireLoaded<AudioClip>(Project + "/first.wav", out var unexpected);
            unexpected?.Dispose();
            Assert.That(wasHot, Is.False,
                "An independent official pin does not create an implicit YL synchronous owner.");
            using var acquired = system.AcquireAsync<AudioClip>(Project + "/first.wav");
            yield return AudioNativeFixture.Wait(() => acquired.IsDone, "explicit YL preload");
            using var independent = acquired.TakeResult();
            var clip = independent.Asset;
            Assert.That(clip, Is.SameAs(firstOperation.Handle.Result), "YL must consume the same real official asset operation.");
            var music = AudioKit.PlayMusicNamed(first);
            var sound = AudioKit.PlaySoundNamed(first, AudioSoundOptions.MusicDefaults);
            var voice = AudioKit.PlayVoiceNamed(first, AudioSoundOptions.MusicDefaults);
            using var channel = AudioKit.RegisterChannel("audio/native/named", AudioCategory.Sound, AudioChannelMode.Overlap);
            var named = channel.PlayNamed(first, AudioSoundOptions.MusicDefaults);
            Assert.That(system.GetDiagnostics().LeaseCount, Is.GreaterThan(1),
                "Audio playback and the independent Resource consumer must both retain actual YL ownership.");
            independent.Dispose();
            firstOperation.Dispose();
            rootOperation.Dispose();
            sharedOperation.Dispose();
            Assert.That(firstOperation.Handle.IsValid() && rootOperation.Handle.IsValid() && sharedOperation.Handle.IsValid(), Is.True,
                "Returning only our official pins must not invalidate operations retained by live YL/Audio users.");
            yield return new WaitForSecondsRealtime(.15f);
            Assert.That(music.Voice.Source.timeSamples, Is.GreaterThan(0));
            var samples = new float[1024];
            var outputDeadline = Time.realtimeSinceStartupAsDouble + 1;
            do
            {
                AudioListener.GetOutputData(samples, 0);
                if (samples.Sum(value => value * value) > .0001f)
                {
                    break;
                }
                yield return null;
            } while (Time.realtimeSinceStartupAsDouble < outputDeadline);
            var sourceSamples = new float[1024];
            music.Voice.Source.GetOutputData(sourceSamples, 0);
            TestContext.WriteLine($"AudioNative actual device: batch={Application.isBatchMode}, graphics={SystemInfo.graphicsDeviceType}, sampleRate={AudioSettings.outputSampleRate}, speakers={AudioSettings.driverCapabilities}, editorMute={EditorUtility.audioMasterMute}, sourceEnergy={sourceSamples.Sum(value => value * value)}, listenerEnergy={samples.Sum(value => value * value)}, listeners={Object.FindObjectsOfType<AudioListener>().Length}.");
            Assert.That(samples.Sum(value => value * value), Is.GreaterThan(.0001f),
                $"Actual AudioSource output must contain the fixture signal: volume={music.Voice.Source.volume}, mute={music.Voice.Source.mute}, virtual={music.Voice.Source.isVirtual}, listenerPause={AudioListener.pause}, listenerVolume={AudioListener.volume}, data={clip.loadState}, time={music.Voice.Source.timeSamples}.");
            music.Pause();
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(false);
            yield return null;
            Assert.That(clip && sound.IsPlaying && voice.IsPlaying && named.IsPlaying, Is.True);
            AudioKit.Settings.IsMusicOn.SetValueWithoutCallback(true);
            music.Resume();
            var next = AudioKit.CrossFadeMusicAsync(second, .25f, onStarted: (_, _) => music.Pause());
            using var nextOperation = AudioNativeFixture.Pin<AudioClip>(secondLocation);
            yield return AudioNativeFixture.Accepted(next);
            yield return AudioNativeFixture.Completed(nextOperation);
            nextOperation.Dispose();
            sound.Stop();
            voice.Stop();
            named.Stop();
            AudioKit.ClearClipCache();
            Assert.That(music.IsValid && clip, Is.True, "The crossfade tail is now the only first-Clip playback owner.");
            music.Resume();
            yield return AudioNativeFixture.Busy(startup);
            yield return new WaitForSecondsRealtime(.4f);
            Assert.That(music.IsValid, Is.False);
            yield return AudioNativeFixture.Wait(() => firstOperation.Destructions == 1 && rootOperation.Destructions == 1 && !clip && !nativeRoot,
                "official first-root destruction and physical Clip unload after tail completion");
            Assert.That(nativeShared && nextOperation.Handle.IsValid(), Is.True,
                "The next track still retains the actual shared official dependency after the first root unloads.");
            var nextClip = next.Handle.Voice.Clip;
            next.Cancel();
            AudioKit.ClearClipCache();
            yield return AudioNativeFixture.Wait(() => nextOperation.Destructions == 1 && sharedOperation.Destructions == 1 && !nextClip && !nativeShared,
                "official next/shared destruction and physical unload");
            yield return AudioNativeFixture.Drained(system);
            using (var stop = startup.StopAsync())
            {
                yield return AudioNativeFixture.Wait(() => stop.IsDone, "retry quiescing tail session stop");
                Assert.That(stop.TakeResult(), Is.True);
            }
            AudioNativeFixture.AssertEmpty(system);
            yield return AudioNativeFixture.Start(Project, value => startup = value);
            system = startup.Resources;
            first = new AudioClipAddress(system.GetAudioClipProvider(Project), "first.wav");

            AudioKit.ClipCacheCapacity = 1;
            AudioKit.ClipCacheIdleSeconds = 60;
            using var cacheScope = system.CreateScope("AudioNative/cache-prewarm");
            using (var load = cacheScope.LoadAsync<AudioClip>(Project + "/first.wav"))
            {
                yield return AudioNativeFixture.Wait(() => load.IsDone, "cache prewarm");
                Assert.That(load.TakeResult(), Is.Not.Null);
            }
            using var cacheOperation = AudioNativeFixture.Pin<AudioClip>(firstLocation);
            yield return AudioNativeFixture.Completed(cacheOperation);
            var cached = AudioKit.AcquireClip(first);
            var cachedClip = cached.Clip;
            cached.Dispose();
            cached.Dispose();
            using (var close = cacheScope.CloseAsync())
            {
                yield return AudioNativeFixture.Wait(() => close.IsDone, "cache prewarm Scope close");
                Assert.That(close.TakeResult(), Is.True);
            }
            cacheOperation.Dispose();
            Assert.IsTrue(cachedClip);
            yield return AudioNativeFixture.Busy(startup);
            AudioKit.ClearClipCache();
            yield return AudioNativeFixture.Wait(() => cacheOperation.Destructions == 1 && !cachedClip, "idle Audio cache releases official ownership");
            using (var stop = startup.StopAsync())
            {
                yield return AudioNativeFixture.Wait(() => stop.IsDone, "retry quiescing cache session stop");
                Assert.That(stop.TakeResult(), Is.True);
            }
            AudioNativeFixture.AssertEmpty(system);
            yield return AudioNativeFixture.Start(Project, value => startup = value);
            system = startup.Resources;
            first = new AudioClipAddress(system.GetAudioClipProvider(Project), "first.wav");

            using var explicitScope = system.CreateScope("AudioNative/explicit-prewarm");
            using (var load = explicitScope.LoadAsync<AudioClip>(Project + "/first.wav"))
            {
                yield return AudioNativeFixture.Wait(() => load.IsDone, "explicit caller prewarm");
                Assert.That(load.TakeResult(), Is.Not.Null);
            }
            using var explicitOwner = AudioKit.AcquireClip(first);
            var explicitClip = explicitOwner.Clip;
            using (var close = explicitScope.CloseAsync())
            {
                yield return AudioNativeFixture.Wait(() => close.IsDone, "explicit prewarm Scope close");
                Assert.That(close.TakeResult(), Is.True);
            }
            AudioKit.Shutdown();
            Assert.IsTrue(explicitClip, "An explicit caller lease survives Audio Shutdown.");
            yield return AudioNativeFixture.Busy(startup);
            explicitOwner.Dispose();
            explicitOwner.Dispose();
            yield return AudioNativeFixture.Wait(() => !explicitClip, "explicit caller returns final native ownership");
            var borrowed = AudioClip.Create("external-audio-native", 44100, 1, 44100, false);
            try
            {
                AudioState.EnablePlayback();
                Assert.That(AudioKit.PlayMusic(borrowed).IsPlaying, Is.True);
                AudioKit.Shutdown();
                Assert.IsTrue(borrowed);
                yield return AudioNativeFixture.Drained(system);
            }
            finally
            {
                Object.Destroy(borrowed);
            }
            using var stopped = startup.StopAsync();
            yield return AudioNativeFixture.Wait(() => stopped.IsDone, "actual Resource shutdown");
            Assert.That(stopped.TakeResult(), Is.True);
            AudioNativeFixture.AssertEmpty(system);
            Assert.That(system.State, Is.EqualTo(ResourceSystemState.Closed));
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator NativeCancelAudioShutdownRetryAndResourceExitDrainLateResults()
        {
            AudioNativeFixture.PreparePlay();
            yield return new EnterPlayMode();
            yield return PendingBody();
        }

        private static IEnumerator PendingBody()
        {
            using var preferences = new AudioTestSettings();
            PlayerPrefs.SetInt("KEY_AUDIO_MANAGER_VOICE_ON", 0);
            using var audio = new AudioState();
            ResourceStartup startup = null;
            yield return AudioNativeFixture.Start(Project, value => startup = value);
            var system = startup.Resources;
            var provider = system.GetAudioClipProvider(Project);
            var address = new AudioClipAddress(provider, "first.wav");
            using var first = AudioNativeFixture.Pin<AudioClip>(AudioNativeFixture.Location<AudioClip>(Project + "/first.wav"));
            Assert.That(first.Handle.IsDone, Is.False, "Exercise a real cold official operation, not a pre-completed replacement provider.");
            var canceled = AudioKit.PlaySoundAsync(address, AudioSoundOptions.MusicDefaults);
            var old = AudioKit.PlayVoiceAsync(address, AudioSoundOptions.MusicDefaults);
            Assert.That(system.GetDiagnostics().PendingWorkCount, Is.GreaterThan(0));
            canceled.Cancel();
            AudioKit.Shutdown();
            Assert.That(canceled.EndReason, Is.EqualTo(AudioPlayEnd.Canceled));
            Assert.That(old.EndReason, Is.EqualTo(AudioPlayEnd.Shutdown));
            AudioState.EnablePlayback();
            var retry = AudioKit.PlayVoiceAsync(address, AudioSoundOptions.MusicDefaults);
            yield return AudioNativeFixture.Accepted(retry);
            yield return AudioNativeFixture.Completed(first);
            first.Dispose();
            Assert.That(canceled.Handle.IsValid || old.Handle.IsValid, Is.False);
            yield return AudioNativeFixture.Wait(() => system.GetDiagnostics().LeaseCount == 1, "old-session audio producers return only their own leases");
            retry.Cancel();
            AudioKit.ClearClipCache();
            yield return AudioNativeFixture.Wait(() => first.Destructions == 1, "canceled/retried official operation settles");
            yield return AudioNativeFixture.Drained(system);

            using var second = AudioNativeFixture.Pin<AudioClip>(AudioNativeFixture.Location<AudioClip>(Project + "/second.wav"));
            Assert.That(second.Handle.IsDone, Is.False);
            var pending = AudioKit.PlayMusicAsync(new AudioClipAddress(provider, "second.wav"));
            Assert.That(pending.IsAssetReady, Is.False);
            Assert.That(system.GetDiagnostics().PendingWorkCount, Is.GreaterThan(0));
            // This is forced execution-session invalidation, not the ordered application shutdown path.
            AudioNativeFixture.EndOwnedSession(system);
            Assert.That(pending.IsDone, Is.True);
            Assert.That(pending.Error, Is.TypeOf<ResourceException>());
            Assert.That(((ResourceException)pending.Error).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            AudioKit.Shutdown();
            ResourceStartup replacement = null;
            yield return AudioNativeFixture.Start(Project, value => replacement = value);
            var replacementSystem = replacement.Resources;
            using var scope = replacementSystem.CreateScope("AudioNative/replacement");
            AudioClip fresh = null;
            using (var loaded = scope.LoadAsync<AudioClip>(Project + "/second.wav"))
            {
                yield return AudioNativeFixture.Wait(() => loaded.IsDone, "replacement session joins real official production");
                fresh = loaded.TakeResult();
                Assert.That(fresh, Is.Not.Null);
            }
            yield return AudioNativeFixture.Completed(second);
            Assert.That(second.Handle.Result, Is.SameAs(fresh));
            second.Dispose();
            yield return AudioNativeFixture.Drained(system);
            Assert.That(system.State, Is.EqualTo(ResourceSystemState.Invalidated));
            Assert.That(fresh && second.Handle.IsValid(), Is.True, "Old cleanup must not release a newer Scope's official pin.");
            Assert.That(pending.Handle.IsValid, Is.False);
            Assert.That(Assert.Throws<ResourceException>(() => provider.Load(address.Key)).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            using (var close = scope.CloseAsync())
            {
                yield return AudioNativeFixture.Wait(() => close.IsDone, "replacement Scope close");
                Assert.That(close.TakeResult(), Is.True);
            }
            yield return AudioNativeFixture.Wait(() => second.Destructions == 1 && !fresh, "all real official pins returned");
            using var stop = replacement.StopAsync();
            yield return AudioNativeFixture.Wait(() => stop.IsDone, "replacement Resource stop");
            Assert.That(stop.TakeResult(), Is.True);
            AudioNativeFixture.AssertEmpty(replacementSystem);
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator ProviderProgressReentrantExitNeverPublishesInvalidClip()
        {
            AudioNativeFixture.PreparePlay();
            yield return new EnterPlayMode();
            yield return ProgressExitBody();
        }

        private static IEnumerator ProgressExitBody()
        {
            ResourceStartup startup = null;
            yield return AudioNativeFixture.Start(Project, value => startup = value);
            var system = startup.Resources;
            var provider = system.GetAudioClipProvider(Project);
            using var operation = AudioNativeFixture.Pin<AudioClip>(AudioNativeFixture.Location<AudioClip>(Project + "/first.wav"));
            var calls = 0;
            var exitedWithClaimedLease = false;
            AudioClipLease delivered = null;
            Exception failure = null;
            try
            {
                provider.LoadAsync("first.wav", (lease, error) =>
                {
                    delivered = lease;
                    failure = error;
                    calls++;
                }, value =>
                {
                    if (value < 1 || system.GetDiagnostics().LeaseCount == 0)
                    {
                        return;
                    }
                    exitedWithClaimedLease = true;
                    operation.Dispose();
                    AudioNativeFixture.EndOwnedSession(system);
                });
                yield return AudioNativeFixture.Wait(() => calls != 0, "final delivery progress callback");
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(exitedWithClaimedLease, Is.True, "Exercise the final claimed-lease window, not an earlier pending progress sample.");
                Assert.That(delivered, Is.Null, "No invalid success lease may escape after the progress observer ends the session.");
                Assert.That(failure, Is.TypeOf<ResourceException>());
                Assert.That(((ResourceException)failure).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
                yield return AudioNativeFixture.Drained(system);
                yield return AudioNativeFixture.Wait(() => operation.Destructions == 1, "official native operation destroyed after reentrant exit");
                Assert.That(operation.Completions, Is.EqualTo(1));
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
                _listener = new GameObject("AudioNative.Listener", typeof(AudioListener));
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
            var physical = global::YLFramework.Testing.PackageTestPaths.Physical(path);
            Assert.That(Directory.Exists(Path.GetDirectoryName(physical)), Is.True);
            Assert.That(File.Exists(physical) || File.Exists(physical + ".meta"), Is.False, "Never overwrite an unknown fixture.");
            const int Samples = 44100 * 3;
            using var writer = new BinaryWriter(new FileStream(physical, FileMode.CreateNew));
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + Samples * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(44100);
            writer.Write(88200);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(Samples * 2);
            for (var index = 0; index < Samples; index++)
            {
                writer.Write((short)(Math.Sin(index * 2 * Math.PI * 330 / 44100) * 4000));
            }
        }
    }

    /// <summary>Only the two Audio test files share this owned official-content and scene lifecycle helper.</summary>
    internal static class AudioNativeFixture
    {
        private const string SceneKey = "YL.AudioAA.Play.";
        internal const string AcceptanceAddress = "yl-audio-acceptance/signal";
        private static readonly List<ResourceStartup> _startups = new List<ResourceStartup>();
        private static readonly List<ResourceSystem> _systems = new List<ResourceSystem>();
        private static readonly List<IDisposable> _pins = new List<IDisposable>();
        private static readonly List<Task> _shutdowns = new List<Task>();

        [Serializable]
        private sealed class SavedScene
        {
            public string path;
            public bool loaded;
            public bool active;
        }
        [Serializable]
        private sealed class SavedScenes
        {
            public SavedScene[] scenes;
        }
        [Serializable]
        private sealed class HostOwner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
        }

        internal static void RequireBuildEnvironment()
        {
            var project = Directory.GetParent(Application.dataPath).FullName;
            var ownerPath = Path.Combine(project, ".yl-owner.json");
            Assert.That(File.Exists(ownerPath), Is.True, "Run this fixture only in the existing isolated owned test host.");
            var owner = JsonUtility.FromJson<HostOwner>(File.ReadAllText(ownerPath));
            Assert.That(owner.schemaVersion == 1 && owner.purpose == "YLTesting.Directory" && owner.kind == "Host" &&
                string.Equals(Path.GetFullPath(owner.path), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase), Is.True);
            EnsureRestorableScenes();
            ResourceEditorOperations.ValidateBuild(Settings);
            Assert.That(Settings.BuildAddressablesWithPlayerBuild, Is.EqualTo(AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer));
        }

        private static AddressableAssetSettings Settings
        {
            get
            {
                var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
                Assert.That(settings, Is.Not.Null, "Prepare the official default AA settings first.");
                return settings;
            }
        }

        internal static void EnsureRestorableScenes()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                Assert.That(scene.isDirty, Is.False, "Do not close dirty scenes: " + scene.name);
                if (string.IsNullOrEmpty(scene.path))
                {
                    Assert.That(scene.rootCount, Is.Zero, "Only discard a clean empty runner scene.");
                }
            }
        }

        internal static string CreateFolder(string prefix, string key)
        {
            var folder = "Assets/" + prefix + "-" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(folder) || File.Exists(folder) || File.Exists(folder + ".meta"), Is.False);
            var guid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            Assert.That(guid, Is.Not.Empty);
            SessionState.SetString(key + "folder", folder);
            SessionState.SetString(key + "guid", guid);
            return folder;
        }

        internal static void VerifyFolder(string key)
        {
            var folder = SessionState.GetString(key + "folder", "");
            Assert.That(folder.StartsWith("Assets/__YLAudio", StringComparison.Ordinal), Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(SessionState.GetString(key + "guid", "")));
        }

        internal static string AddGroup(string name, IDictionary<string, string> inputs)
        {
            var settings = Settings;
            Assert.That(settings.groups.Any(group => group && group.Name == name), Is.False);
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            foreach (var input in inputs)
            {
                var guid = AssetDatabase.AssetPathToGUID(input.Key);
                Assert.That(guid, Is.Not.Empty);
                Assert.That(entries.Any(entry => entry.guid == guid || entry.address == input.Value || entry.labels.Contains(input.Value)), Is.False,
                    "Never move or overwrite another official entry: " + input.Value);
            }
            foreach (var path in new[] { settings.GroupFolder + "/" + name + ".asset",
                settings.GroupSchemaFolder + "/" + name + "_BundledAssetGroupSchema.asset",
                settings.GroupSchemaFolder + "/" + name + "_ContentUpdateGroupSchema.asset" })
            {
                Assert.That(File.Exists(path) || File.Exists(path + ".meta") || Directory.Exists(path), Is.False);
            }
            var created = settings.CreateGroup(name, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var bundled = created.GetSchema<BundledAssetGroupSchema>();
            bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bundled.IncludeInBuild = bundled.IncludeAddressInCatalog = bundled.IncludeGUIDInCatalog = true;
            foreach (var input in inputs)
            {
                settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(input.Key), created).SetAddress(input.Value);
            }
            foreach (var schema in created.Schemas)
            {
                AssetDatabase.SaveAssetIfDirty(schema);
            }
            AssetDatabase.SaveAssetIfDirty(created);
            AssetDatabase.SaveAssetIfDirty(settings);
            var groupGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(created));
            SessionState.SetString("YL.AudioAA.Group." + groupGuid, GroupIdentity(created));
            return groupGuid;
        }

        internal static ResourceEditorBuildReport Build(string archivePath = null)
        {
            if (archivePath == null)
            {
                var directory = Path.GetFullPath("Temp/YLAudioContent-" + Guid.NewGuid().ToString("N"));
                NoLinks(directory);
                Directory.CreateDirectory(directory);
                archivePath = Path.Combine(directory, "addressables_content_state.bin");
            }
            return ResourceEditorOperations.BuildContent(Settings, archivePath);
        }

        internal static void DeleteFolderAndGroups(string key)
        {
            EnsureRestorableScenes();
            VerifyFolder(key);
            var folder = SessionState.GetString(key + "folder", "");
            var owned = SessionState.GetString(key + "groups", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var settings = Settings;
            Assert.That(EditorUtility.IsDirty(settings), Is.False);
            var foreign = EntryValues(settings, owned);
            foreach (var guid in owned)
            {
                var group = settings.groups.Single(item => item && AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item)) == guid);
                Assert.That(GroupIdentity(group), Is.EqualTo(SessionState.GetString("YL.AudioAA.Group." + guid, "")),
                    "Unknown group/schema/entry identities must not be deleted.");
                Assert.That(group.entries.All(entry => entry.AssetPath.StartsWith(folder + "/", StringComparison.Ordinal)), Is.True);
                settings.RemoveGroup(group);
                SessionState.EraseString("YL.AudioAA.Group." + guid);
            }
            AssetDatabase.SaveAssetIfDirty(settings);
            CollectionAssert.AreEqual(foreign, EntryValues(settings, Array.Empty<string>()), "Only remove this fixture's official entries.");
            Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            foreach (var name in new[] { "folder", "guid", "groups" })
            {
                SessionState.EraseString(key + name);
            }
        }

        private static string[] EntryValues(AddressableAssetSettings settings, string[] excluded) => settings.groups.Where(group => group &&
            !excluded.Contains(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group)))).SelectMany(group => group.entries.Select(entry =>
                group.Guid + "|" + entry.guid + "|" + entry.address + "|" + string.Join(",", entry.labels.OrderBy(value => value, StringComparer.Ordinal))))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

        private static string GroupIdentity(AddressableAssetGroup group) => string.Join("\n", new[]
        {
            AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group)),
            group.Name,
            string.Join(";", group.Schemas.Select(schema => schema.GetType().FullName + "=" +
                AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(schema))).OrderBy(value => value, StringComparer.Ordinal)),
            string.Join(";", group.entries.Select(entry => entry.guid + "=" + entry.address + "=" +
                string.Join(",", entry.labels.OrderBy(label => label, StringComparer.Ordinal))).OrderBy(value => value, StringComparer.Ordinal))
        });

        internal static void InOwnAuthoringScene(Action action)
        {
            var previous = SceneManager.GetActiveScene();
            var own = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(own);
                action();
            }
            finally
            {
                EditorSceneManager.CloseScene(own, true);
                if (previous.IsValid() && previous.isLoaded)
                {
                    SceneManager.SetActiveScene(previous);
                }
            }
        }

        internal static void PreparePlay()
        {
            RequireBuildEnvironment();
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.False, "Do not change the user's domain reload policy.");
            Assert.That(Settings.ActivePlayModeDataBuilder, Is.InstanceOf<BuildScriptPackedPlayMode>(),
                "The parent prepare must select official Use Existing Build; this test does not silently switch builders.");
            Assert.That(SessionState.GetString(SceneKey + "path", ""), Is.Empty);
            var saved = new SavedScenes
            {
                scenes = EditorSceneManager.GetSceneManagerSetup().Where(scene => !string.IsNullOrEmpty(scene.path)).Select(scene =>
                    new SavedScene { path = scene.path, loaded = scene.isLoaded, active = scene.isActive }).ToArray()
            };
            SessionState.SetString(SceneKey + "saved", JsonUtility.ToJson(saved));
            var folder = CreateFolder("__YLAudioScene", SceneKey);
            var path = folder + "/Neutral.unity";
            SessionState.SetString(SceneKey + "path", path);
            var neutral = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(neutral, path), Is.True);
        }

        internal static IEnumerator RestorePlay()
        {
            if (SessionState.GetString(SceneKey + "path", "") == "")
            {
                yield break;
            }
            VerifyFolder(SceneKey);
            var cleanup = CleanupNative();
            Exception error = null;
            while (true)
            {
                bool next = false;
                try
                {
                    next = cleanup.MoveNext();
                }
                catch (Exception failure)
                {
                    error = failure;
                }
                if (error != null || !next)
                {
                    break;
                }
                yield return cleanup.Current;
            }
            (cleanup as IDisposable)?.Dispose();
            if (error != null)
            {
                SessionState.SetString(SceneKey + "cleanupError", error.ToString());
            }
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
            var path = SessionState.GetString(SceneKey + "path", "");
            if (path != "")
            {
                EnsureRestorableScenes();
                var saved = JsonUtility.FromJson<SavedScenes>(SessionState.GetString(SceneKey + "saved", ""));
                if (saved.scenes.Length != 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(saved.scenes.Select(scene => new SceneSetup
                    {
                        path = scene.path,
                        isLoaded = scene.loaded,
                        isActive = scene.active
                    }).ToArray());
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                VerifyFolder(SceneKey);
                Assert.That(AssetDatabase.DeleteAsset(SessionState.GetString(SceneKey + "folder", "")), Is.True);
                foreach (var name in new[] { "folder", "guid", "path", "saved" })
                {
                    SessionState.EraseString(SceneKey + name);
                }
            }
            var failureText = SessionState.GetString(SceneKey + "cleanupError", "");
            SessionState.EraseString(SceneKey + "cleanupError");
            Assert.That(failureText, Is.Empty, "Cleanup was not completed: " + failureText);
        }

        private static IEnumerator CleanupNative()
        {
            if (!EditorApplication.isPlaying)
            {
                yield break;
            }
            AudioKit.Shutdown();
            foreach (var pin in _pins)
            {
                pin.Dispose();
            }
            foreach (var task in _shutdowns)
            {
                var until = Time.realtimeSinceStartupAsDouble + 30;
                while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }
                Assert.That(task.IsCompleted, Is.True, "The original entry owner still has shutdown responsibility.");
                task.GetAwaiter().GetResult();
            }
            if (ResourceSystem.Current == null && _systems.Any(system => !Empty(system)))
            {
                // A real current owner ticks retired work while Play continues; do not manually complete old native producers.
                var driver = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                _startups.Add(driver);
                using var started = driver.StartAsync();
                var until = Time.realtimeSinceStartupAsDouble + 30;
                while (!started.IsDone && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }
                Assert.That(started.IsDone, Is.True);
                Assert.That(started.TakeResult(), Is.SameAs(driver));
                _systems.Add(driver.Resources);
            }
            foreach (var system in _systems.Where(system => system.State == ResourceSystemState.Invalidated))
            {
                var until = Time.realtimeSinceStartupAsDouble + 30;
                while (!Empty(system) && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }
                AssertEmpty(system);
            }
            foreach (var startup in _startups.AsEnumerable().Reverse())
            {
                using var stop = startup.StopAsync();
                var until = Time.realtimeSinceStartupAsDouble + 30;
                while (!stop.IsDone && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }
                Assert.That(stop.IsDone, Is.True);
                Assert.That(stop.TakeResult(), Is.True, stop.Error?.ToString());
            }
            foreach (var system in _systems)
            {
                var until = Time.realtimeSinceStartupAsDouble + 30;
                while (!Empty(system) && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }
                AssertEmpty(system);
            }
            Assert.That(ResourceSystem.Current, Is.Null);
            _pins.Clear();
            _startups.Clear();
            _systems.Clear();
            _shutdowns.Clear();
        }

        internal static void ObserveShutdown(Task shutdown, ResourceSystem system)
        {
            Assert.That(shutdown, Is.Not.Null, "Disposing the iterator must publish its real shutdown Task.");
            _shutdowns.Add(shutdown);
            if (system != null && !_systems.Contains(system))
            {
                _systems.Add(system);
            }
        }

        internal static IEnumerator Start(string project, Action<ResourceStartup> initialized)
        {
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false, audioProjects: new[] { project });
            _startups.Add(startup);
            using var request = startup.StartAsync();
            yield return Wait(() => request.IsDone, "actual official Resource startup");
            Assert.That(request.TakeResult(), Is.SameAs(startup));
            _systems.Add(startup.Resources);
            initialized(startup);
        }

        internal static IEnumerator Busy(ResourceStartup startup)
        {
            var system = startup.Resources;
            using var stop = startup.StopAsync();
            yield return Wait(() => stop.IsDone, "busy preflight");
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(stop.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(system.IsAvailable, Is.False, "Busy retains responsibility in quiescing state, not permission to accept new work.");
            Assert.That(system.State, Is.EqualTo(ResourceSystemState.ShuttingDown));
            Assert.That(system.IsSessionActive, Is.True);
            Assert.That(ResourceSystem.Current, Is.SameAs(system));
        }

        internal static void EndOwnedSession(ResourceSystem system)
        {
            Assert.That(ResourceSystem.Current, Is.SameAs(system));
            typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        internal static IEnumerator Wait(Func<bool> condition, string label)
        {
            var until = Time.realtimeSinceStartupAsDouble + 30;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
            }
            Assert.That(condition(), Is.True, label);
        }

        internal static IEnumerator Accepted(AudioPlayRequest request)
        {
            yield return Wait(() => request.Handle.IsPlaying || request.IsDone, "actual Audio submission");
            Assert.That(request.Handle.IsPlaying, Is.True, request.Error?.ToString() ?? request.EndReason.ToString());
        }

        private static bool Empty(ResourceSystem system)
        {
            var report = system.GetDiagnostics();
            return report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 &&
                report.PendingWorkCount == 0 && report.DrainingHandleCount == 0;
        }
        internal static IEnumerator Drained(ResourceSystem system)
        {
            yield return Wait(() => Empty(system), "actual YL responsibilities drained");
            AssertEmpty(system);
        }
        internal static void AssertEmpty(ResourceSystem system)
        {
            Assert.That(Empty(system), Is.True);
            Assert.That(system.GetDiagnostics().Errors, Is.Empty);
        }

        internal static IResourceLocation Location<T>(string key)
        {
            var locations = new List<IResourceLocation>();
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (locator.Locate(key, typeof(T), out var found))
                {
                    locations.AddRange(found);
                }
            }
            var matches = locations.Distinct().ToArray();
            Assert.That(matches.Length, Is.EqualTo(1), key);
            Assert.That(Addressables.ResourceManager.ResourceProviders.Single(provider => provider.ProviderId == matches[0].ProviderId),
                Is.InstanceOf<BundledAssetProvider>(), "Use the actually built official content, never an Editor asset substitute.");
            return matches[0];
        }

        internal static IResourceLocation[] BundleDependencies(IResourceLocation location)
        {
            var visited = new HashSet<IResourceLocation>();
            var pending = new Stack<IResourceLocation>();
            pending.Push(location);
            while (pending.Count != 0)
            {
                var item = pending.Pop();
                if (!visited.Add(item))
                {
                    continue;
                }
                foreach (var dependency in item.Dependencies ?? Array.Empty<IResourceLocation>())
                {
                    pending.Push(dependency);
                }
            }
            return visited.Where(item => item.Data is AssetBundleRequestOptions).ToArray();
        }

        internal sealed class OfficialPin<T> : IDisposable
        {
            internal readonly AsyncOperationHandle<T> Handle;
            internal int Completions;
            internal int Destructions;
            private bool _owns = true;
            internal OfficialPin(IResourceLocation location)
            {
                Handle = Addressables.ResourceManager.ProvideResource<T>(location);
                Handle.Completed += _ => Completions++;
                Handle.Destroyed += _ => Destructions++;
            }
            public void Dispose()
            {
                if (!_owns)
                {
                    return;
                }
                Assert.That(Handle.IsValid(), Is.True, "No other owner may release this test's official pin.");
                _owns = false;
                Addressables.Release(Handle);
            }
        }
        internal static OfficialPin<T> Pin<T>(IResourceLocation location)
        {
            var pin = new OfficialPin<T>(location);
            _pins.Add(pin);
            return pin;
        }
        internal static IEnumerator Completed<T>(OfficialPin<T> pin)
        {
            yield return Wait(() => pin.Completions != 0, "official operation completion");
            Assert.That(pin.Handle.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), pin.Handle.OperationException?.ToString());
            Assert.That(pin.Completions, Is.EqualTo(1));
        }

        internal static string ProtectedDigest()
        {
            var values = new List<string>();
            foreach (var root in new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings" })
            {
                var directory = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(root));
                NoLinks(directory);
                values.Add(root + "=" + Directory.Exists(directory));
                if (!Directory.Exists(directory))
                {
                    continue;
                }
                foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
                {
                    NoLinks(path);
                    values.Add(root + "/" + path.Substring(directory.Length + 1).Replace('\\', '/') + "=" + Hash(path));
                }
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", values)))).Replace("-", "").ToLowerInvariant();
        }

        internal static string Hash(string path)
        {
            using var stream = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void NoLinks(string path)
        {
            for (var cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                if (File.Exists(cursor) || Directory.Exists(cursor))
                {
                    Assert.That((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, Is.True);
                }
            }
        }
    }
}
