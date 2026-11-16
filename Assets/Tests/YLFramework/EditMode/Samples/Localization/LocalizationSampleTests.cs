using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Tests.Localization.Samples
{
    public class LocalizationSampleTests
    {
        private readonly List<string> _preparedScenes = new List<string>();

        [SetUp]
        public void PrepareOnlyUtfOwnedEmptyScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!string.IsNullOrEmpty(scene.path))
                    continue;
                var root = Directory.GetParent(Application.dataPath).FullName;
                if (!Application.isBatchMode || !File.Exists(Path.Combine(root, ".yl-owner.json")) || scene.rootCount != 0)
                    throw new InvalidOperationException("The sample fixture may only save a UTF-created empty scene in its owned host.");
                var path = AssetDatabase.GenerateUniqueAssetPath("Assets/YLTestingProfile/LocalizationSampleFixture.unity");
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
                _preparedScenes.Add(path);
            }
        }

        [TearDown]
        public void CleanupOwnedSceneFiles()
        {
            foreach (var path in _preparedScenes)
                AssetDatabase.DeleteAsset(path);
            _preparedScenes.Clear();
        }

        [Test]
        public void ImportedSampleBuildsItsOwnSceneWithoutOverwritingOrSavingOtherScenes()
        {
            var type = Type.GetType("YLFramework.Localization.Samples.Editor.LocalizationSampleBuilder, YLFramework.Localization.Sample.Editor");
            Assert.That(type, Is.Not.Null, "The actual imported sample must provide a complete UI builder, not only snippets.");
            var folder = "Assets/__YLLocalizationSample_" + Guid.NewGuid().ToString("N");
            var original = SceneManager.GetActiveScene();
            try
            {
                var path = (string)type.GetMethod("Create").Invoke(null, new object[] { folder, null, null });
                Assert.That(path, Is.EqualTo(folder + "/LocalizationDemo.unity"));
                Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(path), Is.Not.Null);
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(original));
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    var groups = scene.GetRootGameObjects()[0].GetComponentsInChildren<YLFramework.Localization.LocalizationGroup>(true);
                    Assert.That(groups.Length, Is.EqualTo(1));
                    Assert.That(groups[0].Content.transform, Is.Not.EqualTo(groups[0].transform));
                    Assert.That(groups[0].GetComponentsInChildren<YLFramework.Localization.LocalizeSprite>(true).Length, Is.EqualTo(1));
                    Assert.That(groups[0].GetComponentsInChildren<YLFramework.Localization.LocalizeText>(true).Length, Is.GreaterThanOrEqualTo(2));
                }
                finally { EditorSceneManager.CloseScene(scene, true); }
                var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => type.GetMethod("Create").Invoke(null, new object[] { folder, null, null }));
                Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void UntitledScenesAreRejectedBeforeAnyAuthoringOutputIsCreated()
        {
            var type = Type.GetType("YLFramework.Localization.Samples.Editor.LocalizationSampleBuilder, YLFramework.Localization.Sample.Editor");
            Assert.That(type, Is.Not.Null);
            var folder = "Assets/__YLLocalizationUntitled_" + Guid.NewGuid().ToString("N");
            var previous = SceneManager.GetActiveScene();
            var untitled = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => type.GetMethod("Create").Invoke(null, new object[] { folder, null, null }));
                Assert.That(error.InnerException.Message, Does.Contain("untitled"));
                Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
                Assert.That(untitled.path, Is.Empty);
                Assert.That(untitled.isLoaded, Is.True);
            }
            finally
            {
                EditorSceneManager.CloseScene(untitled, true);
                SceneManager.SetActiveScene(previous);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test, Order(-10)]
        public void UnrelatedDirtyPersistentAssetsCannotBeSavedIndirectlyByOfficialGroupCreation()
        {
            var type = Type.GetType("YLFramework.Localization.Samples.Editor.LocalizationSampleBuilder, YLFramework.Localization.Sample.Editor");
            Assert.That(type, Is.Not.Null);
            var scope = "Assets/__YLLocalizationDirty_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(scope));
            var path = scope + "/Unrelated.asset";
            var asset = ScriptableObject.CreateInstance<DirtySampleAsset>();
            asset.value = 17;
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssetIfDirty(asset);
            var before = File.ReadAllBytes(path);
            asset.value = 99;
            EditorUtility.SetDirty(asset);
            var output = scope + "/Generated";
            try
            {
                var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => type.GetMethod("Create").Invoke(null, new object[] { output, null, null }));
                Assert.That(error.InnerException.Message, Does.Contain(path));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
                Assert.That(EditorUtility.IsDirty(asset), Is.True);
                Assert.That(AssetDatabase.IsValidFolder(output), Is.False);
            }
            finally { AssetDatabase.DeleteAsset(scope); }
        }

        public sealed class DirtySampleAsset : ScriptableObject
        {
            public int value;
        }
    }
}
