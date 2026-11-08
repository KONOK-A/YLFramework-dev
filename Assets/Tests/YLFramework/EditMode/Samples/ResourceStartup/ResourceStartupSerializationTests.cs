using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Samples.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupSerializationTests
    {
        private string _folder;
        private string _folderGuid;
        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/Tests/YLFramework/EditMode/ResourceSample-" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists(_folder) || File.Exists(_folder + ".meta"), Is.False);
            _folderGuid = AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode", Path.GetFileName(_folder));
        }
        [TearDown]
        public void TearDown()
        {
            Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(_folderGuid));
            AssetDatabase.DeleteAsset(_folder);
        }

        [TestCase(typeof(ResourceStartupExample), "mode")]
        [TestCase(typeof(ResourceStartupExample), "projectId")]
        [TestCase(typeof(ResourceStartupExample), "platform")]
        [TestCase(typeof(ResourceStartupExample), "appCompatibility")]
        [TestCase(typeof(ResourceStartupExample), "snapshotDirectory")]
        [TestCase(typeof(ResourceStartupExample), "catalogDigest")]
        [TestCase(typeof(ResourceStartupExample), "useBuiltIn")]
        [TestCase(typeof(ResourceStartupExample), "bindCore")]
        [TestCase(typeof(ResourceStartupExample), "useAudio")]
        [TestCase(typeof(ResourceStartupExampleData), "text")]
        public void CurrentSampleFieldsDoNotRetainLegacySerializedReaders(Type type, string name)
        {
            var field = type.GetField(name);
            Assert.That(field, Is.Not.Null);
            Assert.That(field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>(), Is.Empty,
                "Current formally saved sample inputs must not require the retired field reader: " + name);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentStartupSamplePrefabAndDataColdReadSettingsFlagsAndIdentity(bool enabled)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var root = new GameObject("CurrentStartupSample");
            root.SetActive(false);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var prefabPath = _folder + "/Sample.prefab";
            var dataPath = _folder + "/SampleData.asset";
            ResourceStartupExample sample;
            ResourceStartupExampleData data;
            try
            {
                sample = root.AddComponent<ResourceStartupExample>();
                Assert.That(sample.mode, Is.EqualTo(ResourceLoadMode.EditorSimulation));
                Assert.That(sample.useBuiltIn, Is.False);
                Assert.That(sample.bindCore && sample.useAudio, Is.True);
                Assert.That(sample.platform, Is.EqualTo("StandaloneWindows64"));
                Assert.That(sample.appCompatibility, Is.EqualTo("startup-sample-v1"));
                using (var serialized = new SerializedObject(sample))
                {
                    Assert.That(serialized.FindProperty("ContentKeyProvider"), Is.Null, "The injected runtime key provider must remain nonserialized.");
                }
                sample.mode = enabled ? ResourceLoadMode.Bundles : ResourceLoadMode.EditorSimulation;
                sample.projectId = "current-sample-project";
                sample.appCompatibility = "current-sample-v1";
                sample.snapshotDirectory = Path.GetFullPath(_folder + "/ExplicitSnapshot");
                sample.catalogDigest = new string('a', 64);
                sample.useBuiltIn = enabled;
                sample.bindCore = !enabled;
                sample.useAudio = enabled;
                data = ScriptableObject.CreateInstance<ResourceStartupExampleData>();
                data.text = enabled ? "Current sample data" : "";
                AssetDatabase.CreateAsset(data, dataPath);
                AssetDatabase.SaveAssetIfDirty(data);
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, prefabPath), Is.Not.Null);
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
            Assert.That((bool)root || (bool)sample, Is.False, "The authoring scene must be unloaded before reopening the prefab.");
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out string dataGuid, out long dataId), Is.True);
            Resources.UnloadAsset(data);
            Assert.That((bool)data, Is.False);
            var paths = new[] { dataPath, prefabPath };
            var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            var bytes = paths.Select(File.ReadAllBytes).ToArray();
            var metas = paths.Select(path => File.ReadAllBytes(path + ".meta")).ToArray();
            var restoredData = AssetDatabase.LoadAssetAtPath<ResourceStartupExampleData>(dataPath);
            Assert.That(ReferenceEquals(restoredData, data), Is.False);
            Assert.That(restoredData.text, Is.EqualTo(enabled ? "Current sample data" : ""));
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(restoredData, out string restoredGuid, out long restoredId), Is.True);
            Assert.That(restoredGuid, Is.EqualTo(dataGuid));
            Assert.That(restoredId, Is.EqualTo(dataId));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(restoredData))), Is.EqualTo("a8a69b199e86111478bb1ebeee38034e"));
            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var restored = contents.GetComponent<ResourceStartupExample>();
                Assert.That(contents.activeSelf, Is.False);
                Assert.That(restored.mode, Is.EqualTo(enabled ? ResourceLoadMode.Bundles : ResourceLoadMode.EditorSimulation));
                Assert.That(restored.projectId, Is.EqualTo("current-sample-project"));
                Assert.That(restored.platform, Is.EqualTo("StandaloneWindows64"));
                Assert.That(restored.appCompatibility, Is.EqualTo("current-sample-v1"));
                Assert.That(restored.snapshotDirectory, Is.EqualTo(Path.GetFullPath(_folder + "/ExplicitSnapshot")));
                Assert.That(restored.catalogDigest, Is.EqualTo(new string('a', 64)));
                Assert.That(restored.useBuiltIn, Is.EqualTo(enabled));
                Assert.That(restored.bindCore, Is.EqualTo(!enabled));
                Assert.That(restored.useAudio, Is.EqualTo(enabled));
                Assert.That(restored.ContentKeyProvider, Is.Null);
                Assert.That(restored.Startup, Is.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(restored))), Is.EqualTo("6efb2386a5492c0449d3f50a48627a32"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            var output = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
            Assert.That(Directory.Exists(output), Is.True, "Run this contract through the declared owned native host.");
            var evidence = Path.Combine(output, "ResourceStartupCurrent-" + enabled);
            Assert.That(Directory.Exists(evidence), Is.False);
            Directory.CreateDirectory(evidence);
            for (var i = 0; i < paths.Length; i++)
            {
                Assert.That(AssetDatabase.AssetPathToGUID(paths[i]), Is.EqualTo(guids[i]));
                CollectionAssert.AreEqual(bytes[i], File.ReadAllBytes(paths[i]));
                CollectionAssert.AreEqual(metas[i], File.ReadAllBytes(paths[i] + ".meta"));
                File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(paths[i])), bytes[i]);
                File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(paths[i]) + ".meta"), metas[i]);
                TestContext.WriteLine("Current sample input: {0}; GUID={1}", paths[i], guids[i]);
            }
        }

        [Test]
        public void SampleBuiltInSelectionUsesExplicitIdentityInsteadOfDeveloperPublicationPath()
        {
            using var source = new ResourceBuiltInDeploymentTests.BuiltInSource();
            ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(source.Snapshot));
            var go = new GameObject("C02 sample mount fixture");
            try
            {
                var example = go.AddComponent<ResourceStartupExample>();
                example.useBuiltIn = true;
                example.projectId = source.ProjectId;
                example.platform = source.Snapshot.Document.Platform;
                example.appCompatibility = "app-v1";
                example.catalogDigest = source.Snapshot.Document.CatalogDigest;
                example.snapshotDirectory = Path.Combine(source.Root, "not-deployed-on-this-machine");
                var mount = example.CreateMount();
                Assert.That(mount.BuiltIn.DirectoryPath, Does.StartWith(ResourceBuiltInDeployer.ResourcesRoot));
                Assert.That(mount.BuiltIn.Document.CatalogDigest, Is.EqualTo(example.catalogDigest));
                Assert.That(ResourceSystem.Current, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

    }
}
