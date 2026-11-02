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
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceStartupSerializationTests
    {
        private string _folder;
        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/Tests/YLFramework/EditMode/ResourceSample-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode", Path.GetFileName(_folder));
        }
        [TearDown] public void TearDown() => AssetDatabase.DeleteAsset(_folder);

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyStartupSampleYamlRetainsSettingsFlagsDataAndIdentity(bool enabled)
        {
            var fields = "Mode ProjectId Platform AppCompatibility SnapshotDirectory CatalogDigest UseBuiltIn BindCore UseAudio".Split(' ');
            foreach (var old in fields)
            {
                var current = char.ToLowerInvariant(old[0]) + old.Substring(1);
                var field = typeof(ResourceStartupExample).GetField(current);
                Assert.That(field, Is.Not.Null, current);
                Assert.That(field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>().Any(alias => alias.oldName == old), Is.True, old);
            }
            Assert.That(typeof(ResourceStartupExampleData).GetField("text"), Is.Not.Null);
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var root = new GameObject("LegacyStartupSample");
            root.SetActive(false);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var sample = root.AddComponent<ResourceStartupExample>();
                Assert.That(sample.mode, Is.EqualTo(ResourceLoadMode.EditorSimulation));
                Assert.That(sample.useBuiltIn, Is.False);
                Assert.That(sample.bindCore && sample.useAudio, Is.True);
                using (var serialized = new SerializedObject(sample))
                {
                    Assert.That(serialized.FindProperty("ContentKeyProvider"), Is.Null, "The injected runtime key provider must remain nonserialized.");
                }
                sample.mode = ResourceLoadMode.Bundles;
                sample.projectId = "legacy-sample";
                sample.platform = "LegacyPlatform";
                sample.appCompatibility = "legacy-app";
                sample.snapshotDirectory = "Legacy/Snapshot";
                sample.catalogDigest = "legacy-digest";
                sample.useBuiltIn = enabled;
                sample.bindCore = !enabled;
                sample.useAudio = enabled;
                var data = ScriptableObject.CreateInstance<ResourceStartupExampleData>();
                data.text = "Legacy sample data";
                AssetDatabase.CreateAsset(data, _folder + "/SampleData.asset");
                var prefabPath = _folder + "/Sample.prefab";
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                foreach (var path in new[] { AssetDatabase.GetAssetPath(data), prefabPath })
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    string Snapshot(Object value) => EditorJsonUtility.ToJson(value is GameObject go ? go.GetComponent<ResourceStartupExample>() : value);
                    var snapshot = Snapshot(asset);
                    var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
                    var yaml = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    foreach (var old in fields.Concat(new[] { "Text" }))
                    {
                        var current = char.ToLowerInvariant(old[0]) + old.Substring(1);
                        yaml = System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^(\s*)" + current + ":", "$1" + old + ":");
                    }
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), yaml, new System.Text.UTF8Encoding(false));
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    var restored = AssetDatabase.LoadMainAssetAtPath(path);
                    Assert.That(Snapshot(restored), Is.EqualTo(snapshot), path);
                    if (restored is GameObject prefab)
                    {
                        PrefabUtility.SavePrefabAsset(prefab);
                    }
                    else
                    {
                        EditorUtility.SetDirty(restored);
                        AssetDatabase.SaveAssetIfDirty(restored);
                    }
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    Assert.That(Snapshot(AssetDatabase.LoadMainAssetAtPath(path)), Is.EqualTo(snapshot), path);
                    CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
                }
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
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
