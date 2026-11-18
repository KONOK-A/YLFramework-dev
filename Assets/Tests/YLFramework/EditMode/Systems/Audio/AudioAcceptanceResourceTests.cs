using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Tests.Audio
{
    [Category("YL.Special.AudioSession")]
    public class AudioAcceptanceResourceTests
    {
        private const string Key = "YL.AudioAA.Acceptance.";

        [OneTimeSetUp]
        public void CreateOwnedFixture()
        {
            if (SessionState.GetBool(Key + "owned", false))
            {
                AudioNativeFixture.VerifyFolder(Key);
                return;
            }
            AudioNativeFixture.RequireBuildEnvironment();
            SessionState.SetString(Key + "protected", AudioNativeFixture.ProtectedDigest());
            var folder = AudioNativeFixture.CreateFolder("__YLAudioAcceptance", Key);
            var path = folder + "/Signal.wav";
            AudioResourceBundleTests.WriteWave(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var group = AudioNativeFixture.AddGroup("audio-acceptance-" + Guid.NewGuid().ToString("N"),
                new Dictionary<string, string> { [path] = AudioNativeFixture.AcceptanceAddress });
            SessionState.SetString(Key + "groups", group);
            var built = AudioNativeFixture.Build();
            SessionState.SetString(Key + "state", built.ContentStatePath);
            SessionState.SetBool(Key + "owned", true);
        }

        [OneTimeTearDown]
        public void DeleteOwnedFixture()
        {
            if (SessionState.GetString(Key + "folder", "") == "")
            {
                return;
            }
            Assert.That(AudioNativeFixture.ProtectedDigest(), Is.EqualTo(SessionState.GetString(Key + "protected", "")));
            AudioNativeFixture.DeleteFolderAndGroups(Key);
            SessionState.EraseBool(Key + "owned");
            SessionState.EraseString(Key + "state");
            SessionState.EraseString(Key + "protected");
        }

        [UnityTearDown]
        public IEnumerator RestoreSession() => AudioNativeFixture.RestorePlay();

        [UnityTest]
        public IEnumerator AcceptanceInitializesTheOnlyAuthorityBeforeFirstNamedGroupSubmission()
        {
            AudioNativeFixture.PreparePlay();
            yield return new EnterPlayMode();
            yield return Body(false);
        }

        [UnityTest]
        public IEnumerator InterruptedAcceptanceRestoresPreferencesAndClosesAudioBeforeResource()
        {
            AudioNativeFixture.PreparePlay();
            yield return new EnterPlayMode();
            yield return Body(true);
        }

        private static IEnumerator Body(bool interruptWithActiveGroup)
        {
            using var settings = new AudioTestSettings();
            PlayerPrefs.SetInt("KEY_AUDIO_MANAGER_MUSIC_ON", 0);
            AudioKit.Shutdown();
            var originalScale = Time.timeScale;
            var root = new GameObject("Audio86b.Acceptance");
            var groupRoot = new GameObject("Audio86b.Group");
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            var mixer = ScriptableObject.CreateInstance<AudioMixerConfig>();
            using (var serialized = new SerializedObject(mixer))
            {
                serialized.FindProperty("mixer").objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Audio.AudioMixer>(
                    "Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var direct = AudioClip.Create("AcceptanceDirect", 44100, 1, 44100, false);
            var stack = new Stack<IEnumerator>();
            Component entry = null;
            Type type = null;
            ResourceSystem system = null;
            var observed = false;
            try
            {
                type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("YLFramework.Core.Tests.AudioAcceptanceEntry")).First(x => x != null);
                entry = root.AddComponent(type);
                ((MonoBehaviour)entry).enabled = false;
                var group = groupRoot.AddComponent<AudioGroupPlayer>();
                library.groups.Add(new AudioClipGroup
                {
                    name = "signal",
                    entries = new List<AudioClipEntry>
                { new() { source = AudioClipSource.Resource, projectId = "yl-audio-acceptance", key = "signal" } }
                });
                using (var serialized = new SerializedObject(group))
                {
                    serialized.FindProperty("library").objectReferenceValue = library;
                    serialized.FindProperty("groupName").stringValue = "signal";
                    serialized.FindProperty("channelName").stringValue = "audio86b/acceptance";
                    serialized.FindProperty("loadAsync").boolValue = false;
                    serialized.FindProperty("options").FindPropertyRelative("Loop").boolValue = true;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                type.GetField("group").SetValue(entry, group);
                type.GetField("signal").SetValue(entry, direct);
                type.GetField("secondSignal").SetValue(entry, direct);
                type.GetField("mixerConfig").SetValue(entry, mixer);
                type.GetField("resourceKey").SetValue(entry, AudioNativeFixture.AcceptanceAddress);
                stack.Push((IEnumerator)type.GetMethod("RunAudio", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entry, null));
                var deadline = Time.realtimeSinceStartupAsDouble + 30;
                while (!group.LastHandle.IsPlaying && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    Assert.That(stack, Is.Not.Empty, "The acceptance routine ended before its first real Group submission.");
                    if (!stack.Peek().MoveNext())
                    {
                        (stack.Pop() as IDisposable)?.Dispose();
                    }
                    else if (stack.Peek().Current is IEnumerator nested)
                    {
                        stack.Push(nested);
                    }
                    else
                    {
                        yield return stack.Peek().Current;
                    }
                }
                system = ResourceSystem.Current;
                Assert.That(system, Is.Not.Null, "Wait for real startup and preload, not merely the first MoveNext.");
                Assert.That(system.IsAvailable, Is.True);
                Assert.That(group.LastHandle.IsPlaying, Is.True);
                Assert.That(group.LastHandle.IsValid, Is.True);
                AudioNativeFixture.Location<AudioClip>(AudioNativeFixture.AcceptanceAddress);
                if (!interruptWithActiveGroup)
                {
                    group.Stop();
                }
                else
                {
                    Time.timeScale = .125f;
                }

                Assert.DoesNotThrow(() => DisposeFrames(stack), "Owner exit must not replace an original failure with Busy or skip restoration.");
                Assert.That(Time.timeScale, Is.EqualTo(originalScale));
                Assert.That(PlayerPrefs.GetInt("KEY_AUDIO_MANAGER_MUSIC_ON"), Is.Zero);
                var shutdown = (Task)type.GetProperty("ShutdownCompletion").GetValue(entry);
                AudioNativeFixture.ObserveShutdown(shutdown, system);
                observed = true;
                yield return AudioNativeFixture.Wait(() => shutdown.IsCompleted, "the original acceptance owner actually closed");
                shutdown.GetAwaiter().GetResult();
                AudioNativeFixture.AssertEmpty(system);
                Assert.That(system.State, Is.EqualTo(ResourceSystemState.Closed));
                Assert.That(ResourceSystem.Current, Is.Null);
                Assert.That(group.LastHandle.IsValid, Is.False);
            }
            finally
            {
                try
                {
                    DisposeFrames(stack);
                    if (!observed && entry != null)
                    {
                        AudioNativeFixture.ObserveShutdown((Task)type.GetProperty("ShutdownCompletion").GetValue(entry), system ?? ResourceSystem.Current);
                    }
                }
                finally
                {
                    Time.timeScale = originalScale;
                    AudioKit.Shutdown();
                    UnityEngine.Object.Destroy(root);
                    UnityEngine.Object.Destroy(groupRoot);
                    UnityEngine.Object.Destroy(library);
                    UnityEngine.Object.Destroy(mixer);
                    UnityEngine.Object.Destroy(direct);
                }
            }
        }

        private static void DisposeFrames(Stack<IEnumerator> stack)
        {
            var failures = new List<Exception>();
            while (stack.Count != 0)
            {
                try
                {
                    (stack.Pop() as IDisposable)?.Dispose();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
            if (failures.Count != 0)
            {
                throw new AggregateException(failures);
            }
        }
    }

    [Category("YL.Special.AudioNative")]
    public class AudioCurrentPlayerTests
    {
        [Test]
        public void CurrentSavedAudioInputsExecuteOriginalDspAcceptanceInTwoNativeMonoPlayers()
        {
            AudioNativeFixture.RequireBuildEnvironment();
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling, Is.False);
            var previous = EditorSceneManager.GetSceneManagerSetup();
            var output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            var token = Guid.NewGuid().ToString("N");
            const string Parent = "Assets";
            var folder = Parent + "/__YLAudioCurrent-" + token;
            Assert.That(AssetDatabase.IsValidFolder(Parent), Is.True);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(folder), Is.Null);
            Assert.That(System.IO.Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(folder)), Is.False);
            var guid = AssetDatabase.CreateFolder(Parent, "__YLAudioCurrent-" + token);
            Assert.That(guid, Is.Not.Empty);
            var completed = false;
            var fixtureKey = "YL.AudioAA.Player." + token + ".";
            SessionState.SetString(fixtureKey + "folder", folder);
            SessionState.SetString(fixtureKey + "guid", guid);
            try
            {
                var mixerPath = folder + "/Current.mixer";
                Assert.That(AssetDatabase.CopyAsset("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer", mixerPath), Is.True);
                var mixer = AssetDatabase.LoadAssetAtPath<UnityEngine.Audio.AudioMixer>(mixerPath);
                var config = ScriptableObject.CreateInstance<AudioMixerConfig>();
                using (var serialized = new SerializedObject(config))
                {
                    serialized.FindProperty("mixer").objectReferenceValue = mixer;
                    foreach (var name in new[] { "music", "voice", "sound" })
                    {
                        var groupName = char.ToUpperInvariant(name[0]) + name.Substring(1);
                        serialized.FindProperty(name + "Group").objectReferenceValue = mixer.FindMatchingGroups(groupName).Single(group =>
                            string.Equals(group.name, name, StringComparison.OrdinalIgnoreCase));
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                var configPath = folder + "/CurrentMixerConfig.asset";
                AssetDatabase.CreateAsset(config, configPath);
                Assert.That(AssetDatabase.CreateFolder(folder, "Content"), Is.Not.Empty);
                var signalPath = folder + "/Content/Signal.wav";
                AudioResourceBundleTests.WriteWave(signalPath);
                AssetDatabase.ImportAsset(signalPath, ImportAssetOptions.ForceSynchronousImport);
                var signal = AssetDatabase.LoadAssetAtPath<AudioClip>(signalPath);
                Assert.That(signal, Is.Not.Null);
                var secondSignalPath = folder + "/CurrentSecond.wav";
                AudioResourceBundleTests.WriteWave(secondSignalPath);
                AssetDatabase.ImportAsset(secondSignalPath, ImportAssetOptions.ForceSynchronousImport);
                var secondSignal = AssetDatabase.LoadAssetAtPath<AudioClip>(secondSignalPath);
                Assert.That(secondSignal, Is.Not.Null);
                var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
                library.groups.Add(new AudioClipGroup
                {
                    name = "current",
                    entries = new List<AudioClipEntry> { new AudioClipEntry { source = AudioClipSource.Resource, projectId = "yl-audio-acceptance", key = "signal" } }
                });
                var libraryPath = folder + "/CurrentLibrary.asset";
                AssetDatabase.CreateAsset(library, libraryPath);
                var identities = new[] { libraryPath, configPath, mixerPath, signalPath, secondSignalPath }.ToDictionary(path => path, AssetDatabase.AssetPathToGUID);
                var originalHashes = identities.Keys.Concat(identities.Keys.Select(path => path + ".meta"))
                    .ToDictionary(path => path, AudioNativeFixture.Hash);
                var groupGuid = AudioNativeFixture.AddGroup("audio-player-" + token, new Dictionary<string, string>
                {
                    [signalPath] = AudioNativeFixture.AcceptanceAddress,
                    [secondSignalPath] = "yl-audio-acceptance/second"
                });
                SessionState.SetString(fixtureKey + "groups", groupGuid);
                var content = AudioNativeFixture.Build(Path.Combine(output, "Audio-content-state-" + token + ".bin"));
                var fingerprint = global::YLFramework.Testing.EditorFixtures.StandaloneFixturePlayer.ContentHash(content);
                global::YLFramework.Testing.EditorFixtures.StandaloneFixturePlayer.BuildAndRun(
                    output, folder, typeof(AudioKit).Assembly.GetName().Name, content, scene =>
                    {
                        library = AssetDatabase.LoadAssetAtPath<AudioClipLibrary>(libraryPath);
                        config = AssetDatabase.LoadAssetAtPath<AudioMixerConfig>(configPath);
                        signal = AssetDatabase.LoadAssetAtPath<AudioClip>(signalPath);
                        secondSignal = AssetDatabase.LoadAssetAtPath<AudioClip>(secondSignalPath);
                        Assert.That(library && config && signal && secondSignal, Is.True,
                            "Populate must cold-read saved inputs after the shared helper switches to its empty owned scene.");
                        var root = new GameObject("CurrentAudioAcceptance");
                        root.SetActive(false);
                        root.AddComponent<AudioListener>();
                        root.AddComponent<Camera>();
                        var group = root.AddComponent<AudioGroupPlayer>();
                        using (var serialized = new SerializedObject(group))
                        {
                            serialized.FindProperty("library").objectReferenceValue = library;
                            serialized.FindProperty("groupName").stringValue = "current";
                            serialized.FindProperty("channelName").stringValue = "audio/current";
                            serialized.FindProperty("options").FindPropertyRelative("Loop").boolValue = true;
                            serialized.ApplyModifiedPropertiesWithoutUndo();
                        }
                        var emitter = root.AddComponent<AudioEmitter>();
                        using (var serialized = new SerializedObject(emitter))
                        {
                            serialized.FindProperty("clips").arraySize = 1;
                            serialized.FindProperty("clips").GetArrayElementAtIndex(0).objectReferenceValue = signal;
                            serialized.FindProperty("useGlobalPlaySoundMode").boolValue = false;
                            serialized.FindProperty("playSoundMode").intValue = (int)AudioKit.PlaySoundModes.EveryOne;
                            serialized.FindProperty("playOnAwake").boolValue = false;
                            serialized.FindProperty("loop").boolValue = false;
                            serialized.ApplyModifiedPropertiesWithoutUndo();
                        }
                        var music = root.AddComponent<AudioMusicPlayer>();
                        using (var serialized = new SerializedObject(music))
                        {
                            serialized.FindProperty("clip").objectReferenceValue = signal;
                            serialized.FindProperty("playOnAwake").boolValue = false;
                            serialized.FindProperty("loop").boolValue = true;
                            serialized.FindProperty("crossFadeOnPlay").boolValue = true;
                            serialized.FindProperty("crossFadeDuration").floatValue = 1f;
                            serialized.FindProperty("crossFadeCurve").intValue = (int)AudioCrossfadeCurve.Linear;
                            serialized.ApplyModifiedPropertiesWithoutUndo();
                        }
                        var entryType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YLFramework.Core.Tests.AudioAcceptanceEntry")).Single(type => type != null);
                        var entry = root.AddComponent(entryType);
                        entryType.GetField("signal").SetValue(entry, signal);
                        entryType.GetField("secondSignal").SetValue(entry, secondSignal);
                        entryType.GetField("group").SetValue(entry, group);
                        entryType.GetField("mixerConfig").SetValue(entry, config);
                        entryType.GetField("resourceKey").SetValue(entry, AudioNativeFixture.AcceptanceAddress);
                        entryType.GetField("contentHash").SetValue(entry, fingerprint);
                        entryType.GetField("emitter").SetValue(entry, emitter);
                        entryType.GetField("musicPlayer").SetValue(entry, music);
                        entryType.GetField("runToken").SetValue(entry, token);
                        var player = File.ReadAllText(Path.Combine(output, "standalone-player-location.txt"));
                        entryType.GetField("ownedPlayerDirectory").SetValue(entry, player);
                        root.SetActive(true);
                        var scenePath = folder + "/Player.unity";
                        Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                        identities.Add(scenePath, AssetDatabase.AssetPathToGUID(scenePath));
                        originalHashes.Add(scenePath, AudioNativeFixture.Hash(scenePath));
                        originalHashes.Add(scenePath + ".meta", AudioNativeFixture.Hash(scenePath + ".meta"));
                        foreach (var pair in identities)
                        {
                            Assert.That(AssetDatabase.AssetPathToGUID(pair.Key), Is.EqualTo(pair.Value));
                            var target = System.IO.Path.Combine(output, "Audio-player-" + System.IO.Path.GetFileName(pair.Key));
                            System.IO.File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(pair.Key), target, false);
                            System.IO.File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(pair.Key + ".meta"), target + ".meta", false);
                        }
                    }, extraScriptingDefines: new[] { "YL_UI_ACCEPTANCE" }, graphics: true);
                foreach (var pair in originalHashes)
                {
                    Assert.That(AudioNativeFixture.Hash(pair.Key), Is.EqualTo(pair.Value), "Saved Audio input bytes changed: " + pair.Key);
                }
                foreach (var pair in identities)
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(pair.Key), Is.EqualTo(pair.Value));
                }
                for (var run = 1; run <= 2; run++)
                {
                    var result = JObject.Parse(File.ReadAllText(Path.Combine(output, "StandalonePlayer-" + run + ".json")));
                    Assert.That((string)result["token"], Is.EqualTo(token));
                    Assert.That((string)result["status"], Is.EqualTo("Passed"), "The original quit verifier owns the successful report write.");
                    Assert.That((string)result["contentHash"], Is.EqualTo(fingerprint));
                    var measurements = (JObject)result["measurements"];
                    foreach (var label in new[] { "group-signal", "half-volume", "muted", "paused", "resumed", "faded-out", "faded-in",
                        "stopped", "mixer-full", "mixer-half", "crossfade", "spatial-near", "spatial-far" })
                    {
                        Assert.That(measurements[label], Is.Not.Null, "Retain each actual DSP/PCM observation: " + label);
                    }
                }
                completed = true;
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(previous);
                if (completed)
                {
                    Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(folder));
                    Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(guid));
                    AudioNativeFixture.DeleteFolderAndGroups(fixtureKey);
                }
            }
        }
    }
}
