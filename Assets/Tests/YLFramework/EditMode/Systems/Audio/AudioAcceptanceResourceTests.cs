using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.Audio
{
    [Category("YL.Special.AudioSession")]
    public class AudioAcceptanceResourceTests
    {
        private const string Key = "YL.Audio86b.Acceptance.";

        [OneTimeSetUp]
        public void CreateOwnedFixture()
        {
            if (SessionState.GetString(Key + "folder", "") != "")
            {
                return;
            }

            ResourceSessionTests.EnsureRestorableScenes();
            var folder = "Assets/Tests/YLFramework/Fixtures/Audio/Acceptance-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures/Audio", Path.GetFileName(folder));
            AssetDatabase.CreateFolder(folder, "Resources");
            var name = "acceptance86b-" + Guid.NewGuid().ToString("N");
            AudioResourceBundleTests.WriteWave(folder + "/Resources/" + name + ".wav");
            AssetDatabase.ImportAsset(folder + "/Resources/" + name + ".wav", ImportAssetOptions.ForceSynchronousImport);
            SessionState.SetString(Key + "folder", folder);
            SessionState.SetString(Key + "name", name);
        }

        [OneTimeTearDown]
        public void DeleteOwnedFixture()
        {
            AssetDatabase.DeleteAsset(SessionState.GetString(Key + "folder", ""));
            SessionState.EraseString(Key + "folder");
            SessionState.EraseString(Key + "name");
        }

        [UnityTearDown]
        public IEnumerator RestoreSession()
        {
            AudioKit.Shutdown();
            var cleanup = new ResourceSessionTests().TearDown();
            while (cleanup.MoveNext())
            {
                yield return cleanup.Current;
            }
        }

        [UnityTest]
        public IEnumerator AcceptanceInitializesTheOnlyAuthorityBeforeFirstNamedGroupSubmission()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return Body(false);
        }

        [UnityTest]
        public IEnumerator InterruptedAcceptanceRestoresPreferencesAndClosesAudioBeforeResource()
        {
            new ResourceSessionTests().SetUp();
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
            IEnumerator run = null;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("YLFramework.Core.Tests.AudioAcceptanceEntry")).First(x => x != null);
                var entry = (MonoBehaviour)root.AddComponent(type);
                entry.enabled = false;
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
                type.GetField("resourceKey").SetValue(entry, SessionState.GetString(Key + "name", ""));
                run = (IEnumerator)type.GetMethod("RunAudio", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(entry, null);
                Assert.That(run.MoveNext(), Is.True);
                Assert.That(ResourceSystem.Current, Is.Not.Null, "The first named Group submission must already have an initialized Resource authority.");
                Assert.That(ResourceSystem.Current.IsAvailable, Is.True);
                Assert.That(group.LastHandle.IsValid, Is.True);
                if (!interruptWithActiveGroup)
                {
                    group.Stop();
                }
                else
                {
                    Time.timeScale = .125f;
                }

                Assert.DoesNotThrow(() => ((IDisposable)run).Dispose(), "Owner exit must not replace an original failure with Busy or skip restoration.");
                run = null;
                Assert.That(Time.timeScale, Is.EqualTo(originalScale));
                Assert.That(PlayerPrefs.GetInt("KEY_AUDIO_MANAGER_MUSIC_ON"), Is.Zero);
                var until = Time.realtimeSinceStartupAsDouble + 5;
                while (ResourceSystem.Current != null && Time.realtimeSinceStartupAsDouble < until)
                {
                    yield return null;
                }

                Assert.That(ResourceSystem.Current, Is.Null);
                Assert.That(group.LastHandle.IsValid, Is.False);
            }
            finally
            {
                try
                {
                    (run as IDisposable)?.Dispose();
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
    }

    [Category("YL.Special.AudioNative")]
    public class AudioCurrentPlayerTests
    {
        [Test]
        public void CurrentSavedAudioInputsExecuteOriginalDspAcceptanceInTwoNativeMonoPlayers()
        {
            global::YLFramework.Tests.Resource.ResourceSessionTests.EnsureRestorableScenes();
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
                var resourceKey = "AudioCurrentSignal-" + token;
                Assert.That(AssetDatabase.CreateFolder(folder, "Resources"), Is.Not.Empty);
                var signalPath = folder + "/Resources/" + resourceKey + ".wav";
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
                global::YLFramework.Testing.EditorFixtures.StandaloneFixturePlayer.BuildAndRun(
                    output, folder, typeof(AudioKit).Assembly.GetName().Name, null, scene =>
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
                        entryType.GetField("resourceKey").SetValue(entry, resourceKey);
                        entryType.GetField("emitter").SetValue(entry, emitter);
                        entryType.GetField("musicPlayer").SetValue(entry, music);
                        entryType.GetField("runToken").SetValue(entry, token);
                        var player = File.ReadAllText(Path.Combine(output, "standalone-player-location.txt"));
                        entryType.GetField("ownedPlayerDirectory").SetValue(entry, player);
                        root.SetActive(true);
                        var scenePath = folder + "/Player.unity";
                        Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                        identities.Add(scenePath, AssetDatabase.AssetPathToGUID(scenePath));
                        foreach (var pair in identities)
                        {
                            Assert.That(AssetDatabase.AssetPathToGUID(pair.Key), Is.EqualTo(pair.Value));
                            var target = System.IO.Path.Combine(output, "Audio-player-" + System.IO.Path.GetFileName(pair.Key));
                            System.IO.File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(pair.Key), target, false);
                            System.IO.File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(pair.Key + ".meta"), target + ".meta", false);
                        }
                    }, graphics: true);
                completed = true;
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(previous);
                if (completed)
                {
                    Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(folder));
                    Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(guid));
                    Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
                }
            }
        }
    }
}
