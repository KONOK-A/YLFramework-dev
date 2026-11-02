using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Audio;
using YLFramework.Editor.Audio;
using YLFramework.Audio;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Audio
{
    public class AudioComponentInspectorTests
    {
        private readonly List<Object> _objects = new List<Object>();

        private T Own<T>(T value) where T : Object
        {
            _objects.Add(value);
            return value;
        }

        private AudioEmitter Emitter()
        {
            var root = Own(new GameObject("AudioComponentInspectorTests"));
            root.SetActive(false);
            return Own(root.AddComponent<AudioEmitter>());
        }

        private static void SetWeights(AudioEmitter emitter, params float[] weights)
        {
            using (var serialized = new SerializedObject(emitter))
            {
                var list = serialized.FindProperty("weightedClips");
                list.arraySize = weights.Length;
                for (var i = 0; i < weights.Length; i++)
                {
                    list.GetArrayElementAtIndex(i).FindPropertyRelative("weight").floatValue = weights[i];
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static float[] Weights(AudioEmitter emitter)
        {
            using (var serialized = new SerializedObject(emitter))
            {
                var list = serialized.FindProperty("weightedClips");
                var result = new float[list.arraySize];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = list.GetArrayElementAtIndex(i).FindPropertyRelative("weight").floatValue;
                }

                return result;
            }
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
                if (_objects[i])
                {
                    Object.DestroyImmediate(_objects[i]);
                }
            }

            _objects.Clear();
        }

        [Test]
        public void EditorAddedEmitterAndResetEnableGlobalPolicyWithoutRewritingExplicitMode()
        {
            var emitter = Emitter();
            using (var serialized = new SerializedObject(emitter))
            {
                Assert.IsTrue(serialized.FindProperty("useGlobalPlaySoundMode").boolValue, "Editor AddComponent invokes Reset.");
                serialized.FindProperty("useGlobalPlaySoundMode").boolValue = false;
                serialized.FindProperty("playSoundMode").intValue = (int)AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                typeof(AudioEmitter).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(emitter, null);
                serialized.Update();
                Assert.IsTrue(serialized.FindProperty("useGlobalPlaySoundMode").boolValue);
                Assert.AreEqual((int)AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames, serialized.FindProperty("playSoundMode").intValue);
                Assert.IsNull(AudioInspectorUtility.Options(serialized).PlaySoundMode);
            }
        }

        [TestCase("Assets/Tests/YLFramework/Fixtures/Audio/LegacyEmitter2D.prefab")]
        [TestCase("Assets/Tests/YLFramework/Fixtures/Audio/LegacyEmitter3D.prefab")]
        public void RealLegacyPrefabsKeepExplicitPolicyWhenTheNewFieldIsMissing(string path)
        {
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Does.Not.Contain("  useGlobalPlaySoundMode:"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(prefab, path);
            var emitter = prefab.GetComponentInChildren<AudioEmitter>(true);
            Assert.IsNotNull(emitter, path);
            using (var serialized = new SerializedObject(emitter))
            {
                Assert.IsFalse(serialized.FindProperty("useGlobalPlaySoundMode").boolValue, "Missing serialized fields retain the compatibility initializer, not Reset.");
                Assert.AreEqual((int)AudioKit.PlaySoundModes.EveryOne, serialized.FindProperty("playSoundMode").intValue);
                Assert.AreEqual(AudioKit.PlaySoundModes.EveryOne, AudioInspectorUtility.Options(serialized).PlaySoundMode);
            }
        }

        [Test]
        public void AllFourAudioTargetsUseMultiObjectCustomEditors()
        {
            var first = Emitter();
            var second = Emitter();
            var root = first.gameObject;
            var music = root.AddComponent<AudioMusicPlayer>();
            var mixer = root.AddComponent<AudioMixerController>();
            var config = Own(ScriptableObject.CreateInstance<AudioMixerConfig>());
            var objects = new Object[][] { new Object[] { first, second }, new Object[] { music }, new Object[] { mixer }, new Object[] { config } };
            var types = new[] { typeof(AudioEmitterEditor), typeof(AudioMusicPlayerEditor), typeof(AudioMixerControllerEditor), typeof(AudioMixerConfigEditor) };
            for (var i = 0; i < objects.Length; i++)
            {
                var editor = Own(UnityEditor.Editor.CreateEditor(objects[i]));
                Assert.AreEqual(types[i], editor.GetType());
                Assert.IsTrue(types[i].IsDefined(typeof(CanEditMultipleObjects), false));
            }
        }

        [Test]
        public void NewEditorComponentsChooseSafeOptInDefaultsAndResetDoesNotApplyRouting()
        {
            var root = Emitter().gameObject;
            var music = root.AddComponent<AudioMusicPlayer>();
            var controller = root.AddComponent<AudioMixerController>();
            using (var serialized = new SerializedObject(music))
            {
                Assert.IsTrue(serialized.FindProperty("loop").boolValue);
                Assert.IsTrue(serialized.FindProperty("playOnAwake").boolValue);
                Assert.IsFalse(serialized.FindProperty("crossFadeOnPlay").boolValue);
                Assert.AreEqual(1f, serialized.FindProperty("crossFadeDuration").floatValue);
                Assert.IsTrue(serialized.FindProperty("crossFadeIgnoreTimeScale").boolValue);
                Assert.AreEqual((int)AudioCrossfadeCurve.EqualPower, serialized.FindProperty("crossFadeCurve").intValue);
            }
            using (var serialized = new SerializedObject(controller))
            {
                Assert.IsFalse(serialized.FindProperty("applyOnAwake").boolValue);
                Assert.AreEqual(.5f, serialized.FindProperty("transitionDuration").floatValue);
                serialized.FindProperty("applyOnAwake").boolValue = true;
                serialized.FindProperty("transitionDuration").floatValue = 2f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                typeof(AudioMixerController).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
                serialized.Update();
                Assert.IsFalse(serialized.FindProperty("applyOnAwake").boolValue);
                Assert.AreEqual(.5f, serialized.FindProperty("transitionDuration").floatValue);
            }
            Assert.IsFalse(AudioManager.HasInstance, "Reset selects defaults without applying shared audio configuration.");
        }

        [Test]
        public void LegacyMusicPrefabKeepsLinearCurveAndNoAutomaticTransition()
        {
            const string path = "Assets/Tests/YLFramework/Fixtures/Audio/LegacyMusicPlayer.prefab";
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Does.Not.Contain("  crossFadeCurve:"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Does.Not.Contain("  crossFadeOnPlay:"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(prefab);
            var music = prefab.GetComponentInChildren<AudioMusicPlayer>(true);
            Assert.IsNotNull(music);
            using (var serialized = new SerializedObject(music))
            {
                Assert.AreEqual((int)AudioCrossfadeCurve.Linear, serialized.FindProperty("crossFadeCurve").intValue);
                Assert.IsFalse(serialized.FindProperty("crossFadeOnPlay").boolValue);
            }
        }

        [Test]
        public void ExplicitSerializedMixerDefaultsAreNotRewrittenByTheNewPreset()
        {
            var root = Emitter().gameObject;
            var controller = root.AddComponent<AudioMixerController>();
            JsonUtility.FromJsonOverwrite("{\"applyOnAwake\":true,\"transitionDuration\":0}", controller);
            Own(UnityEditor.Editor.CreateEditor(controller));
            using (var serialized = new SerializedObject(controller))
            {
                Assert.IsTrue(serialized.FindProperty("applyOnAwake").boolValue);
                Assert.AreEqual(0f, serialized.FindProperty("transitionDuration").floatValue);
            }
        }

        [Test]
        public void WeightedAddPreservesExcludedEntriesAndAppendsAnEmptyWeightOneEntry()
        {
            var emitter = Emitter();
            var clip = Own(AudioClip.Create("InspectorCandidate", 100, 1, 1000, false));
            SetWeights(emitter, 0f, 3f);
            using (var serialized = new SerializedObject(emitter))
            {
                var list = serialized.FindProperty("weightedClips");
                list.GetArrayElementAtIndex(0).FindPropertyRelative("clip").objectReferenceValue = clip;
                list.GetArrayElementAtIndex(1).FindPropertyRelative("clip").objectReferenceValue = clip;
                list.InsertArrayElementAtIndex(0);
                var clips = serialized.FindProperty("clips");
                clips.arraySize = 1;
                clips.GetArrayElementAtIndex(0).objectReferenceValue = clip;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AudioInspectorUtility.AddWeightedClip(serialized);
                CollectionAssert.AreEqual(new[] { 0f, 0f, 3f, 1f }, Weights(emitter), "An explicitly duplicated zero-weight entry stays excluded.");
                Assert.AreSame(clip, serialized.FindProperty("weightedClips").GetArrayElementAtIndex(0).FindPropertyRelative("clip").objectReferenceValue);
                Assert.IsNull(serialized.FindProperty("weightedClips").GetArrayElementAtIndex(3).FindPropertyRelative("clip").objectReferenceValue);
                Assert.AreEqual(1, serialized.FindProperty("clips").arraySize);
                Assert.AreSame(clip, serialized.FindProperty("clips").GetArrayElementAtIndex(0).objectReferenceValue);
                serialized.FindProperty("useWeightedRandom").boolValue = true;
                serialized.ApplyModifiedProperties();
                serialized.FindProperty("useWeightedRandom").boolValue = false;
                serialized.ApplyModifiedProperties();
                CollectionAssert.AreEqual(new[] { 0f, 0f, 3f, 1f }, Weights(emitter));
            }
        }

        [Test]
        public void MultiObjectWeightedAddAndRemovePreserveUnequalListsAndSupportUndo()
        {
            var first = Emitter();
            var second = Emitter();
            SetWeights(first, 0f);
            SetWeights(second, 7f, 0f);
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            using (var serialized = new SerializedObject(new Object[] { first, second }))
            {
                AudioInspectorUtility.AddWeightedClip(serialized);
            }

            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            CollectionAssert.AreEqual(new[] { 0f, 1f }, Weights(first));
            CollectionAssert.AreEqual(new[] { 7f, 0f, 1f }, Weights(second));
            Undo.PerformUndo();
            CollectionAssert.AreEqual(new[] { 0f }, Weights(first));
            CollectionAssert.AreEqual(new[] { 7f, 0f }, Weights(second));
            Undo.PerformRedo();
            CollectionAssert.AreEqual(new[] { 0f, 1f }, Weights(first));
            CollectionAssert.AreEqual(new[] { 7f, 0f, 1f }, Weights(second));
            Undo.IncrementCurrentGroup();
            var removeGroup = Undo.GetCurrentGroup();
            using (var serialized = new SerializedObject(new Object[] { first, second }))
            {
                AudioInspectorUtility.RemoveWeightedClip(serialized, 0);
            }

            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(removeGroup);
            CollectionAssert.AreEqual(new[] { 1f }, Weights(first));
            CollectionAssert.AreEqual(new[] { 0f, 1f }, Weights(second));
            Undo.PerformUndo();
            CollectionAssert.AreEqual(new[] { 0f, 1f }, Weights(first));
            CollectionAssert.AreEqual(new[] { 7f, 0f, 1f }, Weights(second));
        }

        [Test]
        public void WeightedRemoveCallbackKeepsSelectionValidWhenDeletingTheLastEntry()
        {
            var emitter = Emitter();
            SetWeights(emitter, 0f, 2f);
            var editor = Own(UnityEditor.Editor.CreateEditor(emitter));
            var list = (ReorderableList)typeof(AudioEmitterEditor).GetField("_weightedClips", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
            list.index = 1;
            list.onRemoveCallback(list);
            CollectionAssert.AreEqual(new[] { 0f }, Weights(emitter));
            Assert.AreEqual(0, list.index, "Deleting the last row must select a remaining row, not retain an out-of-range index.");
            list.onRemoveCallback(list);
            Assert.IsEmpty(Weights(emitter));
            Assert.AreEqual(-1, list.index, "An empty list has no selection.");
            var other = Emitter();
            var multiEditor = Own(UnityEditor.Editor.CreateEditor(new Object[] { emitter, other }));
            var multiList = (ReorderableList)typeof(AudioEmitterEditor).GetField("_weightedClips", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(multiEditor);
            Assert.IsFalse(multiList.draggable, "Multi-selection must not reorder mixed-length lists.");
        }

        [Test]
        public void WeightedAddRecordsPrefabOverridesWithoutChangingTheSourceAsset()
        {
            var borrowedScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var wasDirty = borrowedScene.isDirty;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Tests/YLFramework/Fixtures/Audio/LegacyEmitter2D.prefab");
            Assert.IsNotNull(prefab);
            var source = prefab.GetComponentInChildren<AudioEmitter>(true);
            var original = Weights(source);
            var instance = Own((GameObject)PrefabUtility.InstantiatePrefab(prefab));
            var emitter = Own(instance.GetComponentInChildren<AudioEmitter>(true));
            using (var serialized = new SerializedObject(emitter))
            {
                AudioInspectorUtility.AddWeightedClip(serialized);
            }

            Assert.AreEqual(original.Length + 1, Weights(emitter).Length);
            Assert.AreEqual(1f, Weights(emitter)[original.Length]);
            Assert.IsTrue(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false));
            using (var serialized = new SerializedObject(emitter))
            {
                Assert.IsTrue(serialized.FindProperty("weightedClips").prefabOverride);
            }

            CollectionAssert.AreEqual(original, Weights(source));
            Assert.AreEqual(wasDirty, borrowedScene.isDirty, "Inspector fixtures must not dirty the user's scene.");
        }

        [Test]
        public void InspectorReadsPreserveHiddenRoutingAndFadeValuesAndVolumeIsNotRangeClamped()
        {
            var emitter = Emitter();
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>("Assets/Tests/YLFramework/Fixtures/Audio/AudioMixerFixture.mixer");
            Assert.IsNotNull(mixer);
            var groups = mixer.FindMatchingGroups("UI");
            Assert.That(groups, Has.Length.EqualTo(1));
            using (var serialized = new SerializedObject(emitter))
            {
                serialized.FindProperty("volume").floatValue = 2f;
                serialized.FindProperty("loop").boolValue = true;
                serialized.FindProperty("autoFade").FindPropertyRelative("FadeOutSeconds").floatValue = .5f;
                serialized.FindProperty("outputRoute").FindPropertyRelative("Mode").intValue = (int)AudioOutputRoute.RouteMode.Direct;
                serialized.FindProperty("outputRoute").FindPropertyRelative("Group").objectReferenceValue = groups[0];
                serialized.FindProperty("pitch").floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.IsTrue(AudioInspectorUtility.HasRouteOrFade(serialized));
                var options = AudioInspectorUtility.Options(serialized);
                Assert.Throws<System.ArgumentException>(() => options.Validate(false));
                options.OutputRoute = AudioOutputRoute.Direct;
                Assert.Throws<System.ArgumentException>(() => options.Validate(false), "Loop tail fade is also invalid, not silently cleared.");
                options.AutoFade = default;
                Assert.Throws<System.ArgumentOutOfRangeException>(() => options.Validate(false), "Pitch 0 remains invalid.");
                Assert.AreEqual(2f, serialized.FindProperty("volume").floatValue);
                Assert.AreEqual(0f, serialized.FindProperty("pitch").floatValue);
                Assert.AreEqual(.5f, serialized.FindProperty("autoFade").FindPropertyRelative("FadeOutSeconds").floatValue);
                Assert.AreEqual((int)AudioOutputRoute.RouteMode.Direct, serialized.FindProperty("outputRoute").FindPropertyRelative("Mode").intValue);
                Assert.AreSame(groups[0], serialized.FindProperty("outputRoute").FindPropertyRelative("Group").objectReferenceValue);
            }
            foreach (var type in new[] { typeof(AudioEmitter), typeof(AudioMusicPlayer) })
            {
                Assert.IsFalse(type.GetField("volume", BindingFlags.Instance | BindingFlags.NonPublic).IsDefined(typeof(UnityEngine.RangeAttribute), false));
            }
        }
    }
}
