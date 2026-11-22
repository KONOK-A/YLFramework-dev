using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using YLFramework.Resource;
using YLFramework.Samples.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupSerializationTests
    {
        private string _folder;
        private string _folderGuid;
        private readonly HashSet<string> _owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] SampleFields =
        {
            "message", "data", "prefab", "preview", "audioCue", "audioPreloadLabel", "scene", "reusePrefab",
            "bindCoreResourceProvider", "contentProjectId", "startupCatalogId", "audioProjects"
        };

        [SetUp]
        public void SetUp()
        {
            ResourceStartupExampleTests.UseSavedAuthoringAnchor();
            EnsureSavedScenes();
            _owned.Clear();
            _folder = "Assets/ResourceSampleSerialization-" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(_folder) || File.Exists(_folder) || File.Exists(_folder + ".meta"), Is.False);
            _folderGuid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            Assert.That(_folderGuid, Is.Not.Empty);
        }

        [TearDown]
        public void TearDown()
        {
            EnsureSavedScenes();
            Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(_folderGuid));
            CollectionAssert.AreEquivalent(_owned, Directory.GetFiles(_folder, "*", SearchOption.AllDirectories).Select(Path.GetFullPath),
                "Do not delete an unknown asset or output added to the owned folder.");
            Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
        }

        [TestCase(typeof(ResourceStartupExample), "message")]
        [TestCase(typeof(ResourceStartupExample), "data")]
        [TestCase(typeof(ResourceStartupExample), "prefab")]
        [TestCase(typeof(ResourceStartupExample), "scene")]
        [TestCase(typeof(ResourceStartupExample), "preview")]
        [TestCase(typeof(ResourceStartupExample), "audioCue")]
        [TestCase(typeof(ResourceStartupExample), "audioPreloadLabel")]
        [TestCase(typeof(ResourceStartupExample), "reusePrefab")]
        [TestCase(typeof(ResourceStartupExample), "bindCoreResourceProvider")]
        [TestCase(typeof(ResourceStartupExample), "contentProjectId")]
        [TestCase(typeof(ResourceStartupExample), "startupCatalogId")]
        [TestCase(typeof(ResourceStartupExample), "audioProjects")]
        [TestCase(typeof(ResourceStartupExampleData), "text")]
        public void CurrentSampleFieldsDoNotRetainLegacySerializedReaders(Type type, string name)
        {
            var field = type.GetField(name);
            Assert.That(field, Is.Not.Null);
            Assert.That(field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>(), Is.Empty,
                "Current formally saved sample inputs must not require a retired field reader: " + name);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentStartupSamplePrefabAndDataColdReadSettingsFlagsAndIdentity(bool enabled)
        {
            var preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("CurrentStartupSample");
            root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, preview);
            var prefabPath = _folder + "/Sample.prefab";
            var visualName = enabled ? "VisualEnabled" : "VisualDisabled";
            var visualPath = _folder + "/" + visualName + ".prefab";
            var dataPath = _folder + "/SampleData.asset";
            var messagePath = _folder + "/Message.txt";
            var scenePath = _folder + "/Target.unity";
            ResourceStartupExample sample = null;
            ResourceStartupExampleData data = null;
            var projects = enabled ? new[] { "audio-primary", "audio-secondary" } : Array.Empty<string>();
            var originalScene = SceneManager.GetActiveScene();
            try
            {
                using (var stream = new FileStream(messagePath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(enabled ? "Persistent official message" : "");
                }
                AssetDatabase.ImportAsset(messagePath, ImportAssetOptions.ForceSynchronousImport);
                Own(messagePath);
                data = ScriptableObject.CreateInstance<ResourceStartupExampleData>();
                data.text = enabled ? "Current sample data" : "";
                AssetDatabase.CreateAsset(data, dataPath);
                AssetDatabase.SaveAssetIfDirty(data);
                Own(dataPath);
                var visual = new GameObject(visualName);
                SceneManager.MoveGameObjectToScene(visual, preview);
                visual.transform.localPosition = new Vector3(1, enabled ? 2 : -2, 3);
                try
                {
                    Assert.That(PrefabUtility.SaveAsPrefabAsset(visual, visualPath), Is.Not.Null);
                    Own(visualPath);
                }
                finally
                {
                    Object.DestroyImmediate(visual);
                }
                var target = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                try
                {
                    SceneManager.SetActiveScene(target);
                    new GameObject(enabled ? "TargetEnabled" : "TargetDisabled");
                    Assert.That(EditorSceneManager.SaveScene(target, scenePath), Is.True);
                    Own(scenePath);
                }
                finally
                {
                    EditorSceneManager.CloseScene(target, true);
                    if (originalScene.IsValid() && originalScene.isLoaded)
                    {
                        SceneManager.SetActiveScene(originalScene);
                    }
                }
                sample = root.AddComponent<ResourceStartupExample>();
                Assert.That(sample.reusePrefab, Is.False);
                Assert.That(sample.bindCoreResourceProvider, Is.True);
                Assert.That(sample.audioPreloadLabel, Is.Null.Or.Empty);
                Assert.That(sample.audioProjects, Is.Empty);
                sample.message = new AssetReferenceT<TextAsset>(AssetDatabase.AssetPathToGUID(messagePath));
                sample.data = new AssetReferenceT<ResourceStartupExampleData>(AssetDatabase.AssetPathToGUID(dataPath));
                sample.prefab = new AssetReferenceGameObject(AssetDatabase.AssetPathToGUID(visualPath));
                sample.scene = new AssetReference(AssetDatabase.AssetPathToGUID(scenePath));
                using (var serialized = new SerializedObject(sample))
                {
                    serialized.Update();
                    serialized.FindProperty("reusePrefab").boolValue = enabled;
                    serialized.FindProperty("bindCoreResourceProvider").boolValue = !enabled;
                    serialized.FindProperty("contentProjectId").stringValue = enabled ? "sample-content" : "";
                    serialized.FindProperty("startupCatalogId").stringValue = enabled ? "sample-catalog" : "";
                    var audio = serialized.FindProperty("audioProjects");
                    audio.arraySize = projects.Length;
                    for (var index = 0; index < projects.Length; index++)
                    {
                        audio.GetArrayElementAtIndex(index).stringValue = projects[index];
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                sample.ValidateReferences();
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, prefabPath), Is.Not.Null);
                Own(prefabPath);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
            Assert.That((bool)root || (bool)sample, Is.False, "The authoring scene must be unloaded before the prefab cold read.");
            var paths = new[] { dataPath, messagePath, visualPath, scenePath, prefabPath };
            var identities = paths.ToDictionary(path => path, path => Identity(AssetDatabase.LoadMainAssetAtPath(path)));
            Resources.UnloadAsset(data);
            Assert.That((bool)data, Is.False);
            var bytes = paths.ToDictionary(path => path, File.ReadAllBytes);
            var metas = paths.ToDictionary(path => path, path => File.ReadAllBytes(path + ".meta"));
            var restoredData = AssetDatabase.LoadAssetAtPath<ResourceStartupExampleData>(dataPath);
            Assert.That(ReferenceEquals(restoredData, data), Is.False);
            Assert.That(restoredData.text, Is.EqualTo(enabled ? "Current sample data" : ""));
            Assert.That(Identity(restoredData), Is.EqualTo(identities[dataPath]));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(restoredData))), Is.EqualTo("a8a69b199e86111478bb1ebeee38034e"));
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var restored = contents.GetComponent<ResourceStartupExample>();
                Assert.That(contents.activeSelf, Is.False);
                Assert.That(restored.reusePrefab, Is.EqualTo(enabled));
                Assert.That(restored.bindCoreResourceProvider, Is.EqualTo(!enabled));
                Assert.That(restored.contentProjectId, Is.EqualTo(enabled ? "sample-content" : ""));
                Assert.That(restored.startupCatalogId, Is.EqualTo(enabled ? "sample-catalog" : ""));
                Assert.That(restored.audioPreloadLabel, Is.Null.Or.Empty, "A sample without a label keeps exact-key warmup by default.");
                CollectionAssert.AreEqual(projects, restored.audioProjects);
                Assert.That(restored.Startup, Is.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(restored))), Is.EqualTo("6efb2386a5492c0449d3f50a48627a32"));
                var references = new AssetReference[] { restored.message, restored.data, restored.prefab, restored.scene };
                var targets = new[] { messagePath, dataPath, visualPath, scenePath };
                for (var index = 0; index < references.Length; index++)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(targets[index]);
                    Assert.That(references[index].AssetGUID, Is.EqualTo(AssetDatabase.AssetPathToGUID(targets[index])));
                    Assert.That(references[index].editorAsset, Is.SameAs(asset));
                    Assert.That(Identity(references[index].editorAsset), Is.EqualTo(identities[targets[index]]));
                    Assert.That(references[index].RuntimeKeyIsValid(), Is.True);
                }
                Assert.That(((TextAsset)restored.message.editorAsset).text, Is.EqualTo(enabled ? "Persistent official message" : ""));
                Assert.That(((GameObject)restored.prefab.editorAsset).name, Is.EqualTo(visualName));
                Assert.That(((GameObject)restored.prefab.editorAsset).transform.localPosition, Is.EqualTo(new Vector3(1, enabled ? 2 : -2, 3)));
                restored.ValidateReferences();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            var coldScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                Assert.That(coldScene.GetRootGameObjects().Single().name, Is.EqualTo(enabled ? "TargetEnabled" : "TargetDisabled"));
            }
            finally
            {
                EditorSceneManager.CloseScene(coldScene, true);
            }
            var output = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
            Assert.That(Directory.Exists(output), Is.True, "Run through the declared owned test output.");
            var evidence = Path.Combine(output, "ResourceStartupCurrent-" + enabled);
            Assert.That(Directory.Exists(evidence), Is.False);
            Directory.CreateDirectory(evidence);
            foreach (var path in paths)
            {
                Assert.That(Identity(AssetDatabase.LoadMainAssetAtPath(path)), Is.EqualTo(identities[path]));
                CollectionAssert.AreEqual(bytes[path], File.ReadAllBytes(path));
                CollectionAssert.AreEqual(metas[path], File.ReadAllBytes(path + ".meta"));
                File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(path)), bytes[path]);
                File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(path) + ".meta"), metas[path]);
                TestContext.WriteLine("Persistent sample input {0}; identity={1}; SHA256={2}; metaSHA256={3}", path, identities[path], Hash(bytes[path]), Hash(metas[path]));
            }
            Assert.That(ResourceSystem.Current, Is.Null, "Persistence does not initialize native resource services.");
        }

        [Test]
        public void SerializedSurfaceHasOnlyCurrentOfficialInputsAndNoForeignSourceReader()
        {
            var preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("CurrentSampleSerializedSurface");
            SceneManager.MoveGameObjectToScene(root, preview);
            try
            {
                using var serialized = new SerializedObject(root.AddComponent<ResourceStartupExample>());
                var names = new List<string>();
                var iterator = serialized.GetIterator();
                var children = true;
                while (iterator.NextVisible(children))
                {
                    children = false;
                    if (iterator.name != "m_Script")
                    {
                        names.Add(iterator.name);
                    }
                }
                CollectionAssert.AreEquivalent(SampleFields, names);
                foreach (var foreign in new[] { "source", "Sources", "UnknownSource", "mode", "projectId", "platform", "appCompatibility", "snapshotDirectory",
                    "catalogDigest", "useBuiltIn", "bindCore", "useAudio", "ContentKeyProvider" })
                {
                    Assert.That(serialized.FindProperty(foreign), Is.Null, "There is no serialized reader or fallback for " + foreign);
                    Assert.That(typeof(ResourceStartupExample).GetField(foreign), Is.Null);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        private void Own(string path)
        {
            Assert.That(File.Exists(path) && File.Exists(path + ".meta"), Is.True);
            _owned.Add(Path.GetFullPath(path));
            _owned.Add(Path.GetFullPath(path + ".meta"));
        }
        private static string Identity(Object value)
        {
            Assert.That(value && EditorUtility.IsPersistent(value), Is.True);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id), Is.True);
            return guid + ":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value.GetType().FullName;
        }
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static void EnsureSavedScenes()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                Assert.That(scene.isDirty, Is.False, "Never save or close a dirty caller scene: " + scene.name);
                Assert.That(!string.IsNullOrEmpty(scene.path) || scene.rootCount == 0, Is.True, "Do not discard unsaved caller content.");
            }
        }
    }
}
