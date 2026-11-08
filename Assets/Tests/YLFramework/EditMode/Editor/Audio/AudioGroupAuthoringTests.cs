using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Editor.Audio;
using YLFramework.Audio;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioGroupAuthoringTests
    {
        private string _folder;
        private string _folderGuid;
        private Scene _scene;
        private readonly List<Object> _objects = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _folderGuid = null;
            _folder = "Assets/Tests/YLFramework/EditMode/Editor/Audio/Authoring-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.LoadMainAssetAtPath(_folder), Is.Null);
            Assert.That(System.IO.Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_folder)), Is.False);
            _folderGuid = AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode/Editor/Audio", _folder.Substring(_folder.LastIndexOf('/') + 1));
            Assert.That(_folderGuid, Is.Not.Empty);
            _scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var owned in _objects)
            {
                if (owned)
                {
                    Undo.ClearUndo(owned);
                }
            }

            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i] is AudioGroupInspectorTestWindow window && window)
                {
                    window.Close();
                }

                if (_objects[i] && !EditorUtility.IsPersistent(_objects[i]))
                {
                    Object.DestroyImmediate(_objects[i]);
                }
            }
            _objects.Clear();
            if (_scene.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(_scene);
            }

            if (!string.IsNullOrEmpty(_folderGuid) && AssetDatabase.IsValidFolder(_folder))
            {
                Assert.That(AssetDatabase.GUIDToAssetPath(_folderGuid), Is.EqualTo(_folder));
                Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(_folderGuid));
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }
        }

        private T Own<T>(T value) where T : Object
        {
            _objects.Add(value);
            return value;
        }

        private AudioClipLibrary Library(string name)
        {
            var library = Own(ScriptableObject.CreateInstance<AudioClipLibrary>());
            AssetDatabase.CreateAsset(library, _folder + "/" + name + ".asset");
            return library;
        }

        private AudioGroupPlayer Player()
        {
            var root = Own(new GameObject("AudioGroupAuthoring"));
            root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, _scene);
            return Own(root.AddComponent<AudioGroupPlayer>());
        }

        private static void FlushUndo(int group)
        {
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
        }

        [TestCase(typeof(AudioWeightedClip), "clip")]
        [TestCase(typeof(AudioWeightedClip), "weight")]
        [TestCase(typeof(AudioClipEntry), "source")]
        [TestCase(typeof(AudioClipEntry), "clip")]
        [TestCase(typeof(AudioClipEntry), "key")]
        [TestCase(typeof(AudioClipEntry), "projectId")]
        [TestCase(typeof(AudioClipEntry), "weight")]
        [TestCase(typeof(AudioClipGroup), "name")]
        [TestCase(typeof(AudioClipGroup), "selection")]
        [TestCase(typeof(AudioClipGroup), "entries")]
        [TestCase(typeof(AudioClipLibrary), "groups")]
        public void CurrentSerializedFieldsDoNotRetainLegacyNameReaders(Type type, string name)
        {
            var field = type.GetField(name);
            Assert.That(field, Is.Not.Null);
            Assert.That(field.GetCustomAttributes(typeof(UnityEngine.Serialization.FormerlySerializedAsAttribute), false), Is.Empty);
        }

        [Test]
        public void CurrentSourceDeclarationRetainsOnlyZeroThreeFourWithoutBundleFieldOrOneTimeWindow()
        {
            CollectionAssert.AreEqual(new[] { "Clip", "ExternalProvider", "Resource" }, Enum.GetNames(typeof(AudioClipSource)));
            CollectionAssert.AreEqual(new[] { 0, 3, 4 }, Enum.GetValues(typeof(AudioClipSource)).Cast<AudioClipSource>().Select(source => (int)source).ToArray());
            Assert.That(typeof(AudioClipEntry).GetField("bundleName"), Is.Null);
            Assert.That(typeof(AudioClipLibraryEditor).Assembly.GetType("YLFramework.Editor.Audio.AudioClipLibraryMigration"), Is.Null);
        }

        [TestCase(typeof(AudioEmitter))]
        [TestCase(typeof(AudioMusicPlayer))]
        public void ComponentCreationDoesNotHaveASeparateLegacyResetBranch(Type type)
        {
            Assert.That(type.GetMethod("Reset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentAudioInputsColdReadAllSourcesExplicitZeroPoliciesAndIndependentVariantOverrides(bool enabled)
        {
            var folderGuid = AssetDatabase.AssetPathToGUID(_folder);
            var clip = Own(AudioClip.Create("CurrentClip", 100, 1, 1000, false));
            AssetDatabase.CreateAsset(clip, _folder + "/CurrentClip.asset");
            var library = Library("CurrentLibrary");
            library.groups.Add(new AudioClipGroup
            {
                name = "CurrentGroup",
                selection = AudioClipSelectionMode.Weighted,
                entries = new List<AudioClipEntry>
                {
                    new AudioClipEntry { source = (AudioClipSource)0, clip = clip, weight = 0f },
                    new AudioClipEntry { source = (AudioClipSource)3, key = "external/full-key", weight = 2.5f },
                    new AudioClipEntry { source = (AudioClipSource)4, projectId = "current-audio", key = "music/theme", weight = 4.25f }
                }
            });
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssetIfDirty(library);
            var player = Player();
            var mixer = AssetDatabase.LoadAssetAtPath<UnityEngine.Audio.AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            var options = new AudioSoundOptions
            {
                Loop = enabled,
                Volume = .37f,
                Pitch = 1.7f,
                MinDistance = 3,
                MaxDistance = 41,
                RolloffMode = AudioRolloffMode.Logarithmic,
                OutputRoute = AudioOutputRoute.ToMixer(mixer.FindMatchingGroups("").First()),
                AutoFade = new AudioAutoFade(.45f, enabled ? 0f : .85f),
                Priority = 19,
                AutoPauseWhenTimeScaleZero = !enabled
            };
            var optionsField = typeof(AudioGroupPlayer).GetField("options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            optionsField.SetValue(player, options);
            var emitter = Own(player.gameObject.AddComponent<AudioEmitter>());
            var music = Own(player.gameObject.AddComponent<AudioMusicPlayer>());
            using (var serialized = new SerializedObject(player))
            {
                serialized.FindProperty("library").objectReferenceValue = library;
                serialized.FindProperty("groupName").stringValue = "CurrentGroup";
                serialized.FindProperty("channelName").stringValue = "audio/current";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var serialized = new SerializedObject(emitter))
            {
                serialized.FindProperty("useGlobalPlaySoundMode").boolValue = false;
                serialized.FindProperty("playSoundMode").intValue = (int)AudioKit.PlaySoundModes.EveryOne;
                serialized.FindProperty("volume").floatValue = .37f;
                serialized.FindProperty("weightedClips").arraySize = 1;
                var weighted = serialized.FindProperty("weightedClips").GetArrayElementAtIndex(0);
                weighted.FindPropertyRelative("clip").objectReferenceValue = clip;
                weighted.FindPropertyRelative("weight").floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var serialized = new SerializedObject(music))
            {
                serialized.FindProperty("clip").objectReferenceValue = clip;
                serialized.FindProperty("playOnAwake").boolValue = false;
                serialized.FindProperty("crossFadeOnPlay").boolValue = true;
                serialized.FindProperty("crossFadeDuration").floatValue = .75f;
                serialized.FindProperty("crossFadeCurve").intValue = (int)AudioCrossfadeCurve.Linear;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var prefabPath = _folder + "/CurrentPolicies.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(player.gameObject, prefabPath);
            Assert.That(prefab, Is.Not.Null);
            var instance = Own((GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene));
            using (var serialized = new SerializedObject(instance.GetComponent<AudioEmitter>()))
            {
                serialized.FindProperty("useGlobalPlaySoundMode").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var serialized = new SerializedObject(instance.GetComponent<AudioMusicPlayer>()))
            {
                serialized.FindProperty("crossFadeCurve").intValue = (int)AudioCrossfadeCurve.EqualPower;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var variantPath = _folder + "/CurrentPoliciesVariant.prefab";
            Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, variantPath), Is.Not.Null);
            var paths = new[] { AssetDatabase.GetAssetPath(library), prefabPath, variantPath };
            var identities = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            Assert.That(identities.Distinct().Count(), Is.EqualTo(3));
            var metadata = paths.Select(path => System.IO.File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();
            Resources.UnloadAsset(library);
            foreach (var path in paths)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            var reopened = Own(AssetDatabase.LoadAssetAtPath<AudioClipLibrary>(paths[0]));
            CollectionAssert.AreEqual(new[] { 0, 3, 4 }, reopened.groups[0].entries.Select(entry => (int)entry.source));
            CollectionAssert.AreEqual(new[] { 0f, 2.5f, 4.25f }, reopened.groups[0].entries.Select(entry => entry.weight));
            Assert.That(AssetDatabase.GetAssetPath(reopened.groups[0].entries[0].clip), Is.EqualTo(_folder + "/CurrentClip.asset"));
            Assert.That(reopened.groups[0].entries[1].key, Is.EqualTo("external/full-key"));
            Assert.That(reopened.groups[0].entries[2].projectId, Is.EqualTo("current-audio"));
            Assert.That(reopened.groups[0].entries[2].key, Is.EqualTo("music/theme"));
            for (var index = 0; index < 2; index++)
            {
                var contents = PrefabUtility.LoadPrefabContents(paths[index + 1]);
                try
                {
                    Assert.That(contents.GetComponent<AudioGroupPlayer>(), Is.Not.Null);
                    Assert.That(JsonUtility.ToJson(optionsField.GetValue(contents.GetComponent<AudioGroupPlayer>())), Is.EqualTo(JsonUtility.ToJson(options)),
                        "Nested options, explicit false/zero values, mixer routing, fades and priorities survive the current Unity save path.");
                    using (var serialized = new SerializedObject(contents.GetComponent<AudioGroupPlayer>()))
                    {
                        Assert.That(AssetDatabase.GetAssetPath(serialized.FindProperty("library").objectReferenceValue), Is.EqualTo(paths[0]));
                        Assert.That(serialized.FindProperty("groupName").stringValue, Is.EqualTo("CurrentGroup"));
                    }
                    using (var serialized = new SerializedObject(contents.GetComponent<AudioEmitter>()))
                    {
                        Assert.That(serialized.FindProperty("useGlobalPlaySoundMode").boolValue, Is.EqualTo(index == 1));
                        Assert.That(serialized.FindProperty("playSoundMode").intValue, Is.EqualTo((int)AudioKit.PlaySoundModes.EveryOne));
                        Assert.That(serialized.FindProperty("volume").floatValue, Is.EqualTo(.37f));
                        Assert.That(serialized.FindProperty("weightedClips").GetArrayElementAtIndex(0).FindPropertyRelative("weight").floatValue, Is.Zero);
                    }
                    using (var serialized = new SerializedObject(contents.GetComponent<AudioMusicPlayer>()))
                    {
                        Assert.That(serialized.FindProperty("crossFadeCurve").intValue, Is.EqualTo(index));
                        Assert.That(serialized.FindProperty("crossFadeOnPlay").boolValue, Is.True);
                        Assert.That(serialized.FindProperty("playOnAwake").boolValue, Is.False);
                        Assert.That(serialized.FindProperty("crossFadeDuration").floatValue, Is.EqualTo(.75f));
                        Assert.That(AssetDatabase.GetAssetPath(serialized.FindProperty("clip").objectReferenceValue), Is.EqualTo(_folder + "/CurrentClip.asset"));
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(folderGuid));
            for (var index = 0; index < paths.Length; index++)
            {
                Assert.That(AssetDatabase.AssetPathToGUID(paths[index]), Is.EqualTo(identities[index]));
                CollectionAssert.AreEqual(metadata[index], System.IO.File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(paths[index] + ".meta")));
            }
            var output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            foreach (var path in paths.Concat(new[] { _folder + "/CurrentClip.asset" }))
            {
                var target = System.IO.Path.Combine(output, "Audio-authoring-" + enabled + "-" + System.IO.Path.GetFileName(path));
                System.IO.File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(path), target, false);
                System.IO.File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"), target + ".meta", false);
            }
        }

        [Test]
        public void LibraryAssetRoundTripPreservesSelectionAddressesAndClipReferences()
        {
            var library = Library("Library");
            var clip = Own(AudioClip.Create("Borrowed", 100, 1, 1000, false));
            AssetDatabase.CreateAsset(clip, _folder + "/clip.asset");
            using (var serialized = new SerializedObject(library))
            {
                AudioClipLibraryEditor.AddGroup(serialized);
                var group = serialized.FindProperty("groups").GetArrayElementAtIndex(0);
                group.FindPropertyRelative("name").stringValue = "UI/Click";
                group.FindPropertyRelative("selection").intValue = (int)AudioClipSelectionMode.Weighted;
                serialized.ApplyModifiedProperties();
                for (var i = 0; i < 3; i++)
                {
                    AudioClipLibraryEditor.AddEntry(serialized, 0);
                }

                var entries = serialized.FindProperty("groups").GetArrayElementAtIndex(0).FindPropertyRelative("entries");
                var sources = new[] { 0, 3, 4 };
                for (var i = 0; i < sources.Length; i++)
                {
                    var entry = entries.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("source").intValue = sources[i];
                    entry.FindPropertyRelative("weight").floatValue = sources[i];
                    entry.FindPropertyRelative("key").stringValue = "Audio/Click";
                    entry.FindPropertyRelative("projectId").stringValue = i == 2 ? "audio-ui" : "";
                }
                entries.GetArrayElementAtIndex(0).FindPropertyRelative("clip").objectReferenceValue = clip;
                serialized.ApplyModifiedProperties();
            }
            var path = AssetDatabase.GetAssetPath(library);
            AssetDatabase.SaveAssetIfDirty(library);
            Undo.ClearUndo(library);
            Resources.UnloadAsset(library);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var reopened = Own(AssetDatabase.LoadAssetAtPath<AudioClipLibrary>(path));
            Assert.IsNotNull(reopened);
            Assert.AreEqual(typeof(AudioClipLibrary), reopened.GetType());
            Assert.AreEqual("UI/Click", reopened.groups[0].name);
            Assert.AreEqual(AudioClipSelectionMode.Weighted, reopened.groups[0].selection);
            CollectionAssert.AreEqual(new[] { 0f, 3f, 4f }, reopened.groups[0].entries.Select(entry => entry.weight));
            CollectionAssert.AreEqual(new[] { AudioClipSource.Clip, AudioClipSource.ExternalProvider, AudioClipSource.Resource },
                reopened.groups[0].entries.Select(entry => entry.source));
            Assert.AreEqual(_folder + "/clip.asset", AssetDatabase.GetAssetPath(reopened.groups[0].entries[0].clip));
            Assert.AreEqual("Audio/Click", reopened.groups[0].entries[1].key);
            Assert.AreEqual("audio-ui", reopened.groups[0].entries[2].projectId);
            Assert.AreEqual("Audio/Click", reopened.groups[0].entries[2].key);
        }

        [Test]
        public void MultiObjectLibraryAuthoringPreservesUnequalListsAndSupportsUndo()
        {
            var first = Library("First");
            var second = Library("Second");
            using (var serialized = new SerializedObject(second))
            {
                AudioClipLibraryEditor.AddGroup(serialized);
            }

            var editor = Own(UnityEditor.Editor.CreateEditor(new Object[] { first, second }));
            Assert.AreEqual(typeof(AudioClipLibraryEditor), editor.GetType());
            Assert.IsTrue(editor.GetType().IsDefined(typeof(CanEditMultipleObjects), false));
            Undo.IncrementCurrentGroup();
            var undo = Undo.GetCurrentGroup();
            AudioClipLibraryEditor.AddGroup(editor.serializedObject);
            FlushUndo(undo);
            Assert.AreEqual(1, first.groups.Count);
            Assert.AreEqual(2, second.groups.Count);
            Undo.PerformUndo();
            Assert.IsEmpty(first.groups);
            Assert.AreEqual(1, second.groups.Count);
            Undo.PerformRedo();
            using (var serialized = new SerializedObject(second))
            {
                AudioClipLibraryEditor.AddEntry(serialized, 0);
                serialized.FindProperty("groups").GetArrayElementAtIndex(0).FindPropertyRelative("entries")
                    .GetArrayElementAtIndex(0).FindPropertyRelative("weight").floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Undo.IncrementCurrentGroup();
            undo = Undo.GetCurrentGroup();
            AudioClipLibraryEditor.AddEntry(editor.serializedObject, 0);
            FlushUndo(undo);
            CollectionAssert.AreEqual(new[] { 1f }, first.groups[0].entries.Select(entry => entry.weight));
            CollectionAssert.AreEqual(new[] { 0f, 1f }, second.groups[0].entries.Select(entry => entry.weight));
            Assert.IsNull(second.groups[0].entries[1].clip);
            Assert.IsEmpty(second.groups[0].entries[1].key);
            Undo.PerformUndo();
            Assert.IsEmpty(first.groups[0].entries);
            CollectionAssert.AreEqual(new[] { 0f }, second.groups[0].entries.Select(entry => entry.weight));
            Undo.PerformRedo();
            Undo.IncrementCurrentGroup();
            undo = Undo.GetCurrentGroup();
            AudioClipLibraryEditor.RemoveEntry(editor.serializedObject, 0, 0);
            FlushUndo(undo);
            Assert.IsEmpty(first.groups[0].entries);
            CollectionAssert.AreEqual(new[] { 1f }, second.groups[0].entries.Select(entry => entry.weight));
            Undo.PerformUndo();
            CollectionAssert.AreEqual(new[] { 0f, 1f }, second.groups[0].entries.Select(entry => entry.weight));
            Undo.IncrementCurrentGroup();
            undo = Undo.GetCurrentGroup();
            AudioClipLibraryEditor.RemoveGroup(editor.serializedObject, 1);
            FlushUndo(undo);
            Assert.AreEqual(1, first.groups.Count);
            Assert.AreEqual(1, second.groups.Count);
            Undo.PerformUndo();
            Assert.AreEqual(2, second.groups.Count);
        }

        [Test]
        public void GroupPrefabRoundTripPreservesFieldsAndPersistentUnityEvent()
        {
            var library = Library("Library");
            var player = Player();
            var source = Own(player.gameObject.AddComponent<AudioSource>());
            source.playOnAwake = false;
            using (var serialized = new SerializedObject(player))
            {
                serialized.FindProperty("library").objectReferenceValue = library;
                serialized.FindProperty("groupName").stringValue = "UI/Click";
                serialized.FindProperty("channelName").stringValue = "ui/click";
                serialized.FindProperty("category").intValue = (int)AudioCategory.Voice;
                serialized.FindProperty("channelMode").intValue = (int)AudioChannelMode.Exclusive;
                serialized.FindProperty("borrowedSource").objectReferenceValue = source;
                serialized.FindProperty("persistenceId").stringValue = "settings/ui";
                serialized.FindProperty("autoPlay").intValue = (int)AudioGroupAutoPlay.EveryEnable;
                serialized.FindProperty("loadAsync").boolValue = false;
                var options = serialized.FindProperty("options");
                options.FindPropertyRelative("Volume").floatValue = 2f;
                options.FindPropertyRelative("Pitch").floatValue = 1.25f;
                options.FindPropertyRelative("Loop").boolValue = true;
                options.FindPropertyRelative("AutoPauseWhenTimeScaleZero").boolValue = true;
                options.FindPropertyRelative("OutputRoute").FindPropertyRelative("Mode").intValue = (int)AudioOutputRoute.RouteMode.Direct;
                options.FindPropertyRelative("AutoFade").FindPropertyRelative("FadeInSeconds").floatValue = .2f;
                serialized.ApplyModifiedProperties();
            }
            UnityEventTools.AddPersistentListener(player.Started, new UnityAction(player.Stop));
            var path = _folder + "/group.prefab";
            Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(player.gameObject, path));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var restored = contents.GetComponent<AudioGroupPlayer>();
                Assert.IsNotNull(restored);
                using (var serialized = new SerializedObject(restored))
                {
                    Assert.AreEqual(library, serialized.FindProperty("library").objectReferenceValue);
                    Assert.AreEqual("UI/Click", serialized.FindProperty("groupName").stringValue);
                    Assert.AreEqual("ui/click", serialized.FindProperty("channelName").stringValue);
                    Assert.AreEqual((int)AudioCategory.Voice, serialized.FindProperty("category").intValue);
                    Assert.AreEqual((int)AudioChannelMode.Exclusive, serialized.FindProperty("channelMode").intValue);
                    Assert.AreEqual(contents.GetComponent<AudioSource>(), serialized.FindProperty("borrowedSource").objectReferenceValue,
                        "The borrowed reference must remap to the AudioSource inside the reopened prefab, not the original scene object.");
                    Assert.AreEqual("settings/ui", serialized.FindProperty("persistenceId").stringValue);
                    Assert.AreEqual((int)AudioGroupAutoPlay.EveryEnable, serialized.FindProperty("autoPlay").intValue);
                    Assert.IsFalse(serialized.FindProperty("loadAsync").boolValue);
                    var options = serialized.FindProperty("options");
                    Assert.AreEqual(2f, options.FindPropertyRelative("Volume").floatValue);
                    Assert.AreEqual(1.25f, options.FindPropertyRelative("Pitch").floatValue);
                    Assert.IsTrue(options.FindPropertyRelative("Loop").boolValue);
                    Assert.IsTrue(options.FindPropertyRelative("AutoPauseWhenTimeScaleZero").boolValue);
                    Assert.AreEqual((int)AudioOutputRoute.RouteMode.Direct, options.FindPropertyRelative("OutputRoute").FindPropertyRelative("Mode").intValue);
                    Assert.AreEqual(.2f, options.FindPropertyRelative("AutoFade").FindPropertyRelative("FadeInSeconds").floatValue);
                }
                Assert.AreEqual(1, restored.Started.GetPersistentEventCount());
                Assert.AreEqual(restored, restored.Started.GetPersistentTarget(0));
                Assert.AreEqual("Stop", restored.Started.GetPersistentMethodName(0));
                Assert.AreEqual(UnityEventCallState.RuntimeOnly, restored.Started.GetPersistentListenerState(0));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void MultiObjectPlayerEditorRecordsUndoAndPrefabOverridesWithoutChangingSource()
        {
            var original = Player();
            using (var serialized = new SerializedObject(original))
            {
                serialized.FindProperty("channelName").stringValue = "original";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var path = _folder + "/override.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(original.gameObject, path);
            var instance = Own((GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene));
            var first = Own(instance.GetComponent<AudioGroupPlayer>());
            var second = Player();
            var editor = Own(UnityEditor.Editor.CreateEditor(new Object[] { first, second }));
            Assert.AreEqual(typeof(AudioGroupPlayerEditor), editor.GetType());
            Assert.IsTrue(editor.GetType().IsDefined(typeof(CanEditMultipleObjects), false));
            Undo.IncrementCurrentGroup();
            var undo = Undo.GetCurrentGroup();
            editor.serializedObject.Update();
            editor.serializedObject.FindProperty("channelName").stringValue = "edited/channel";
            editor.serializedObject.FindProperty("options").FindPropertyRelative("Volume").floatValue = 2f;
            editor.serializedObject.ApplyModifiedProperties();
            FlushUndo(undo);
            using (var serialized = new SerializedObject(first))
            {
                Assert.AreEqual("edited/channel", serialized.FindProperty("channelName").stringValue);
                Assert.IsTrue(serialized.FindProperty("channelName").prefabOverride);
                Assert.IsTrue(serialized.FindProperty("options").FindPropertyRelative("Volume").prefabOverride);
            }
            using (var serialized = new SerializedObject(prefab.GetComponent<AudioGroupPlayer>()))
            {
                Assert.AreEqual("original", serialized.FindProperty("channelName").stringValue);
            }

            Undo.PerformUndo();
            using (var serialized = new SerializedObject(first))
            {
                Assert.AreEqual("original", serialized.FindProperty("channelName").stringValue);
            }

            using (var serialized = new SerializedObject(second))
            {
                Assert.IsTrue(string.IsNullOrEmpty(serialized.FindProperty("channelName").stringValue));
            }

            Undo.PerformRedo();
            PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
            using (var serialized = new SerializedObject(first))
            {
                serialized.FindProperty("channelName").stringValue = "temporary";
                serialized.ApplyModifiedProperties();
            }
            PrefabUtility.RevertPrefabInstance(instance, InteractionMode.AutomatedAction);
            using (var serialized = new SerializedObject(first))
            {
                Assert.AreEqual("edited/channel", serialized.FindProperty("channelName").stringValue);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                using (var serialized = new SerializedObject(contents.GetComponent<AudioGroupPlayer>()))
                {
                    Assert.AreEqual("edited/channel", serialized.FindProperty("channelName").stringValue);
                    Assert.AreEqual(2f, serialized.FindProperty("options").FindPropertyRelative("Volume").floatValue);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void UnsupportedSourceDiagnosticsRejectMixedGroupsWithoutRepairingTheirStoredValues()
        {
            var library = Library("UnsupportedSources");
            var clip = Own(AudioClip.Create("SupportedCandidate", 100, 1, 1000, false));
            foreach (var source in new[] { 1, 2, -1, 5, 99 })
            {
                library.groups.Clear();
                library.groups.Add(new AudioClipGroup
                {
                    name = "mixed",
                    entries = new List<AudioClipEntry>
                    {
                        new AudioClipEntry { source = AudioClipSource.Clip, clip = clip },
                        new AudioClipEntry { source = (AudioClipSource)source, key = "audio/valid", projectId = "explicit-project" }
                    }
                });
                var before = EditorJsonUtility.ToJson(library);
                var random = UnityEngine.Random.state;
                Assert.That(AudioClipLibraryEditor.GetWarnings(library).Any(message => message.Contains("Unsupported Source")), Is.True,
                    "Retired numeric values must be diagnosed as unsupported, never offered an inferred project or skipped as an empty candidate.");
                Assert.That(EditorJsonUtility.ToJson(library), Is.EqualTo(before));
                Assert.That(UnityEngine.Random.state, Is.EqualTo(random));
            }
            Assert.That(AudioManager.HasInstance, Is.False);
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        [Test]
        public void LibraryDiagnosticsIdentifyInvalidGroupsWithoutChangingAssetsOrRandom()
        {
            var library = Library("Invalid");
            library.groups.Add(new AudioClipGroup
            {
                name = "duplicate",
                selection = AudioClipSelectionMode.Weighted,
                entries = new List<AudioClipEntry> { new AudioClipEntry { source = (AudioClipSource)1, key = "wrong", weight = -1f } }
            });
            library.groups.Add(new AudioClipGroup { name = "duplicate" });
            var before = EditorJsonUtility.ToJson(library);
            var random = UnityEngine.Random.state;
            var warnings = AudioClipLibraryEditor.GetWarnings(library);
            Assert.That(warnings.Any(message => message.Contains("Duplicate")));
            Assert.That(warnings.Any(message => message.Contains("finite and nonnegative")));
            Assert.That(warnings.Any(message => message.Contains("Unsupported Source")));
            Assert.That(warnings.Any(message => message.Contains("No selectable")));
            Assert.AreEqual(before, EditorJsonUtility.ToJson(library));
            Assert.AreEqual(random, UnityEngine.Random.state);
            library.groups[0].selection = AudioClipSelectionMode.Uniform;
            Assert.IsFalse(AudioClipLibraryEditor.GetWarnings(library, "duplicate").Any(message => message.Contains("finite and nonnegative")),
                "Uniform ignores weights; diagnostics must not reject ignored values.");
        }

        [Test]
        public void PlayerDiagnosticsIdentifyMissingGroupInvalidChannelAndOptionsWithoutRepairingThem()
        {
            var library = Library("Library");
            var player = Player();
            var source = Own(player.gameObject.AddComponent<AudioSource>());
            source.playOnAwake = false;
            using (var serialized = new SerializedObject(player))
            {
                serialized.FindProperty("library").objectReferenceValue = library;
                serialized.FindProperty("groupName").stringValue = "missing";
                serialized.FindProperty("channelName").stringValue = "bad channel";
                serialized.FindProperty("persistenceId").stringValue = "bad id";
                serialized.FindProperty("options").FindPropertyRelative("Pitch").floatValue = 0f;
                serialized.FindProperty("borrowedSource").objectReferenceValue = source;
                serialized.FindProperty("channelMode").intValue = (int)AudioChannelMode.Overlap;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var before = EditorJsonUtility.ToJson(player);
                var warnings = AudioGroupPlayerEditor.GetWarnings(serialized);
                Assert.That(warnings.Any(message => message.Contains("not found")));
                Assert.That(warnings.Any(message => message.Contains("channelName")));
                Assert.That(warnings.Any(message => message.Contains("persistenceId")));
                Assert.That(warnings.Any(message => message.Contains("Pitch")));
                Assert.That(warnings.Any(message => message.Contains("Exclusive") && message.Contains("Overlap")));
                Assert.That(warnings.Any(message => message.Contains("active and enabled")));
                Assert.AreEqual(before, EditorJsonUtility.ToJson(player));
                serialized.FindProperty("channelMode").intValue = (int)AudioChannelMode.Exclusive;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.IsFalse(AudioGroupPlayerEditor.GetWarnings(serialized).Any(message => message.Contains("Overlap")));
            }
        }

        [UnityTest]
        public IEnumerator CustomEditorGuiDrawsValidInvalidAndMultiObjectTargetsWithoutMutation()
        {
            var valid = Library("ValidGui");
            var invalid = Library("InvalidGui");
            var clip = Own(AudioClip.Create("GuiClip", 100, 1, 1000, false));
            valid.groups.Add(new AudioClipGroup { name = "click", entries = new List<AudioClipEntry> { new AudioClipEntry { clip = clip } } });
            invalid.groups.Add(new AudioClipGroup
            {
                name = "click",
                selection = AudioClipSelectionMode.Weighted,
                entries = new List<AudioClipEntry> { new AudioClipEntry { source = (AudioClipSource)1, key = "bad", weight = -1f }, new AudioClipEntry { weight = 0f } }
            });
            var validPlayer = Player();
            var invalidPlayer = Player();
            using (var serialized = new SerializedObject(validPlayer))
            {
                serialized.FindProperty("library").objectReferenceValue = valid;
                serialized.FindProperty("groupName").stringValue = "click";
                serialized.FindProperty("channelName").stringValue = "gui/valid";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var serialized = new SerializedObject(invalidPlayer))
            {
                serialized.FindProperty("library").objectReferenceValue = invalid;
                serialized.FindProperty("groupName").stringValue = "click";
                serialized.FindProperty("channelName").stringValue = "bad channel";
                serialized.FindProperty("channelMode").intValue = (int)AudioChannelMode.Overlap;
                var source = Own(invalidPlayer.gameObject.AddComponent<AudioSource>());
                source.playOnAwake = false;
                serialized.FindProperty("borrowedSource").objectReferenceValue = source;
                serialized.FindProperty("options").FindPropertyRelative("Pitch").floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var editors = new[]
            {
                Own(UnityEditor.Editor.CreateEditor(valid)), Own(UnityEditor.Editor.CreateEditor(invalid)),
                Own(UnityEditor.Editor.CreateEditor(new Object[] { valid, invalid })),
                Own(UnityEditor.Editor.CreateEditor(validPlayer)), Own(UnityEditor.Editor.CreateEditor(invalidPlayer)),
                Own(UnityEditor.Editor.CreateEditor(new Object[] { validPlayer, invalidPlayer }))
            };
            for (var i = 0; i < editors.Length; i++)
            {
                Assert.AreEqual(i < 3 ? typeof(AudioClipLibraryEditor) : typeof(AudioGroupPlayerEditor), editors[i].GetType());
                var groups = editors[i].serializedObject.FindProperty("groups");
                if (groups != null)
                {
                    for (var j = 0; j < groups.arraySize; j++)
                    {
                        var group = groups.GetArrayElementAtIndex(j);
                        group.isExpanded = true;
                        var entries = group.FindPropertyRelative("entries");
                        for (var k = 0; k < entries.arraySize; k++)
                        {
                            entries.GetArrayElementAtIndex(k).isExpanded = true;
                        }
                    }
                }
                else
                {
                    editors[i].serializedObject.FindProperty("options.OutputRoute").isExpanded = true;
                    editors[i].serializedObject.FindProperty("options.AutoFade").isExpanded = true;
                    editors[i].serializedObject.FindProperty("onStarted").isExpanded = true;
                }
            }
            var targets = new Object[] { valid, invalid, validPlayer, invalidPlayer };
            var before = targets.Select(value => EditorJsonUtility.ToJson(value)).ToArray();
            var hadHost = AudioManager.HasInstance;
            var settings = AudioKit.CurrentSettings;
            var window = Own(ScriptableObject.CreateInstance<AudioGroupInspectorTestWindow>());
            window.Editors = editors;
            window.LayoutCounts = new int[editors.Length];
            window.RepaintCounts = new int[editors.Length];
            window.position = new Rect(100, 100, 700, 700);
            window.ShowUtility();
            for (var frame = 0; frame < 10 && (window.LayoutCounts.Any(count => count == 0) || window.RepaintCounts.Any(count => count == 0)); frame++)
            {
                window.Repaint();
                yield return null;
            }
            for (var i = 0; i < editors.Length; i++)
            {
                Assert.Greater(window.LayoutCounts[i], 0, "Layout was not observed for editor " + i);
                Assert.Greater(window.RepaintCounts[i], 0, "Repaint was not observed for editor " + i);
            }
            CollectionAssert.AreEqual(before, targets.Select(value => EditorJsonUtility.ToJson(value)).ToArray(), "Drawing must not repair invalid data or truncate mixed lists.");
            Assert.AreEqual(hadHost, AudioManager.HasInstance, "Drawing must not initialize a playback host.");
            Assert.AreSame(settings, AudioKit.CurrentSettings, "Drawing must not initialize Settings.");
            LogAssert.NoUnexpectedReceived();
        }
    }

    internal class AudioGroupInspectorTestWindow : EditorWindow
    {
        internal UnityEditor.Editor[] Editors;
        internal int[] LayoutCounts;
        internal int[] RepaintCounts;
        private Vector2 _scroll;

        private void OnGUI()
        {
            if (Editors == null)
            {
                return;
            }

            var type = Event.current.type;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < Editors.Length; i++)
            {
                if (!Editors[i])
                {
                    continue;
                }

                Editors[i].OnInspectorGUI();
                if (type == EventType.Layout)
                {
                    LayoutCounts[i]++;
                }

                if (type == EventType.Repaint)
                {
                    RepaintCounts[i]++;
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
