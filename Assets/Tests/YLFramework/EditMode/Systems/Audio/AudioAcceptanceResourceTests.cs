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
}
