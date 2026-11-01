using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Editor.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceBuildMapTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            const string Parent = "Assets/YLFramework/Tests/Fixtures";
            if (!AssetDatabase.IsValidFolder(Parent))
            {
                AssetDatabase.CreateFolder("Assets/YLFramework/Tests", "Fixtures");
            }

            _folder = Parent + "/Build82-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(Path.GetDirectoryName(_folder).Replace('\\', '/'), Path.GetFileName(_folder));
            AssetDatabase.CreateFolder(_folder, "Content");
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(_folder);

        [Test]
        public void ExplicitGuidSelectionProducesProjectRelativeAddressesWithoutImporterMarks()
        {
            var path = Text("entry");
            var map = Map(Project(Group("main", Select(path))));
            var asset = Items(map, "Assets").Single();
            Assert.That(Value(asset, "Address").ToString(), Does.EndWith(":entry.txt"));
            Assert.That(Value(asset, "Guid"), Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
            Assert.That(Value(asset, "LocalId"), Is.Not.EqualTo(0));
            Assert.That(AssetImporter.GetAtPath(path).assetBundleName, Is.Empty);
        }

        [Test]
        public void FolderSelectionIsFilteredAndDeterministicAndPerAssetNamesSurviveRename()
        {
            string a;
            AssetDatabase.StartAssetEditing();
            try
            {
                a = Text("a");
                Text("b");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            var project = Project(new
            {
                id = "files",
                packing = 1,
                selections = new[] { new { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content"), extensions = new[] { ".txt" } } }
            });
            var first = Map(project);
            var asset = Items(first, "Assets").Single(x => (string)Value(x, "SourcePath") == a);
            var name = Value(asset, "BundleName");
            var names = Items(first, "Bundles").Select(x => (string)Value(x, "Name")).ToArray();
            CollectionAssert.AreEqual(names, Items(Map(project), "Bundles").Select(x => (string)Value(x, "Name")).ToArray());
            Assert.That(AssetDatabase.RenameAsset(a, "renamed"), Is.Empty);
            var renamed = Items(Map(project), "Assets").Single(x => ((string)Value(x, "SourcePath")).EndsWith("renamed.txt"));
            Assert.That(Value(renamed, "BundleName"), Is.EqualTo(name));
            Assert.That(Value(renamed, "Address").ToString(), Does.EndWith(":renamed.txt"));
        }

        [Test]
        public void BundleAnalysisSeparatesSourceBytesAndPlacementFromNativeSizesAndShowsReverseEdges()
        {
            var texture = new Texture2D(2, 2) { name = "Shared" };
            AssetDatabase.CreateAsset(texture, _folder + "/Content/Shared.asset");
            var a = Prefab("A", Material("A", texture));
            var b = Prefab("B", Material("B", texture));
            var map = Map(Project(Group("a", Select(a)), Group("b", Select(b))));
            var bundles = Items(map, "Bundles");
            var shared = bundles.Single(value => (bool)Value(value, "IsShared"));
            Assert.That(shared.GetType().GetProperty("SourceBytes"), Is.Not.Null, "Source estimates must be available as immutable data, not inferred from compressed file bytes.");
            foreach (var bundle in bundles)
            {
                Assert.That(Value(bundle, "SourceBytes"), Is.EqualTo(((string[])Value(bundle, "AssetPaths")).Sum(path => new FileInfo(path).Length)));
                Assert.That(Value(bundle, "Placement").ToString(), Is.EqualTo(ReferenceEquals(bundle, shared) ? "SharedDependencies" : "Group"));
            }
            var dependents = (string[])Value(shared, "Dependents");
            CollectionAssert.AreEquivalent(bundles.Where(value => !ReferenceEquals(value, shared)).Select(value => Value(value, "Name")), dependents);
            dependents[0] = "must not change the snapshot";
            Assert.That((string[])Value(shared, "Dependents"), Does.Not.Contain("must not change the snapshot"));
        }

        [Test]
        public void SharedDependenciesAreAssignedOnceAndManifestGraphHasOneSharedNode()
        {
            var texture = new Texture2D(2, 2) { name = "Shared" };
            AssetDatabase.CreateAsset(texture, _folder + "/Content/Shared.asset");
            var a = Prefab("A", Material("A", texture));
            var b = Prefab("B", Material("B", texture));
            var map = Map(Project(Group("a", Select(a)), Group("b", Select(b))));
            var bundles = Items(map, "Bundles");
            Assert.That(bundles.Length, Is.EqualTo(3));
            var shared = bundles.Single(x => (bool)Value(x, "IsShared"));
            CollectionAssert.AreEqual(new[] { _folder + "/Content/Shared.asset" }, (string[])Value(shared, "AssetPaths"));
            foreach (var bundle in bundles.Where(x => !ReferenceEquals(x, shared)))
            {
                CollectionAssert.AreEqual(new[] { (string)Value(shared, "Name") }, (string[])Value(bundle, "Dependencies"));
            }

            var allPaths = bundles.SelectMany(x => (string[])Value(x, "AssetPaths")).ToArray();
            Assert.That(allPaths.Distinct().Count(), Is.EqualTo(allPaths.Length));
            Assert.That(Value(map, "AvoidedDuplicateSourceBytes"), Is.GreaterThan(0));
        }

        [Test]
        public void CrossProjectDependencyAndOverlappingProjectRootsAreRejected()
        {
            var material = Material("outside", null, _folder + "/Outside.mat");
            var prefab = Prefab("External", material);
            var project = Project(Group("main", Select(prefab)));
            AssertRejected(() => Map(project), "outside");
            AssetDatabase.DeleteAsset(prefab);
            var text = Text("safe");
            JsonUtility.FromJsonOverwrite(JsonConvert.SerializeObject(new
            {
                groups = new[] { Group("main", Select(text)) }
            }), project);
            Project(Group("other", Select(text)));
            AssertRejected(() => Map(project), "overlap");
        }

        [Test]
        public void CaseFoldedAddressesAndDuplicateSelectionsAreRejected()
        {
            var a = Text("a");
            var b = Text("b");
            var project = Project(Group("main", Select(a, "alias"), Select(b, "ALIAS")));
            AssertRejected(() => Map(project), "address");
            JsonUtility.FromJsonOverwrite(JsonConvert.SerializeObject(new
            {
                groups = new[] { Group("main", Select(a), Select(a)) }
            }), project);
            AssertRejected(() => Map(project), "selected");
        }

        [Test]
        public void BundleCyclesAreMergedWithoutRejectingLegalAssetDependencies()
        {
            var ma = Material("A", null);
            var mb = Material("B", null);
            var a = Prefab("A", mb);
            var b = Prefab("B", ma);
            var map = Map(Project(Group("a", Select(a), Select(AssetDatabase.GetAssetPath(ma))), Group("b", Select(b), Select(AssetDatabase.GetAssetPath(mb)))));
            var bundle = Items(map, "Bundles").Single();
            Assert.That(((string[])Value(bundle, "AssetPaths")).Length, Is.EqualTo(4));
            Assert.That((string[])Value(bundle, "Dependencies"), Is.Empty);
            Assert.That(Value(map, "MergedBundleCount"), Is.EqualTo(1));
        }

        [Test]
        public void ScenePrivateOrdinaryDependenciesUseTheirOwnBundle()
        {
            var texture = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(texture, _folder + "/Content/SceneTexture.asset");
            var material = Material("SceneMaterial", texture);
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var path = _folder + "/Content/NonEmpty.unity";
            try
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            var map = Map(Project(Group("scene", Select(path))));
            var bundles = Items(map, "Bundles");
            Assert.That(bundles.Length, Is.EqualTo(2));
            var sceneBundle = bundles.Single(x => (bool)Value(x, "IsScene"));
            var ordinary = bundles.Single(x => !(bool)Value(x, "IsScene"));
            CollectionAssert.AreEqual(new[] { (string)Value(ordinary, "Name") }, (string[])Value(sceneBundle, "Dependencies"));
            Assert.That(((string[])Value(ordinary, "AssetPaths")).Length, Is.EqualTo(2));
            Assert.That(Value(ordinary, "IsShared"), Is.False);
        }

        [Test]
        public void AnimatorControllersHaveRuntimeTypesAndCanBePrefabDependencies()
        {
            var path = _folder + "/Content/Owned.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var go = new GameObject("Animated");
            var prefab = _folder + "/Content/Animated.prefab";
            try
            {
                go.AddComponent<Animator>().runtimeAnimatorController = controller;
                PrefabUtility.SaveAsPrefabAsset(go, prefab);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            var map = Map(Project(Group("animation", Select(prefab), Select(path))));
            var descriptor = Items(map, "Assets").Single(x => (string)Value(x, "SourcePath") == path);
            Assert.That(Value(descriptor, "AssetType"), Is.EqualTo(typeof(RuntimeAnimatorController)));
            Assert.That(Items(map, "Bundles").Single().GetType().GetProperty("AssetPaths").GetValue(Items(map, "Bundles").Single()), Does.Contain(path));
        }

        [Test]
        public void EditorAssemblyAssetsCannotEnterAnExplicitBuildMap()
        {
            var project = Project(Group("main", Select(Text("entry"))));
            var path = _folder + "/Content/EditorProject.asset";
            Assert.That(AssetDatabase.MoveAsset(AssetDatabase.GetAssetPath(project), path), Is.Empty);
            JsonUtility.FromJsonOverwrite(JsonConvert.SerializeObject(new
            {
                groups = new[] { Group("main", Select(path)) }
            }), project);
            AssertRejected(() => Map(project), "unsupported selected");
        }

        [Test]
        public void ScenesAndOrdinaryAssetsAreNeverMixedAndGroupMembersStayCanonical()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                Assert.That(EditorSceneManager.GetSceneAt(i).isDirty, Is.False);
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scenePath = _folder + "/Content/Owned.unity";
            try
            {
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            var map = Map(Project(Group("main", Select(scenePath), Select(Text("entry")))));
            var bundles = Items(map, "Bundles");
            Assert.That(bundles.Length, Is.EqualTo(2));
            Assert.That(bundles.Count(x => (bool)Value(x, "IsScene")), Is.EqualTo(1));
            var groups = (System.Collections.IDictionary)Value(map, "Groups");
            Assert.That(((System.Collections.IEnumerable)groups["main"]).Cast<object>().Count(), Is.EqualTo(2));
        }

        private string Text(string name)
        {
            var path = _folder + "/Content/" + name + ".txt";
            File.WriteAllText(path, "YL 8.2a owned fixture");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private Material Material(string name, Texture texture, string path = null)
        {
            var material = new Material(Shader.Find("Unlit/Texture")) { name = name, mainTexture = texture };
            AssetDatabase.CreateAsset(material, path ?? _folder + "/Content/" + name + ".mat");
            return material;
        }

        private string Prefab(string name, Material material)
        {
            var go = new GameObject(name);
            try
            {
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                var path = _folder + "/Content/" + name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(go, path);
                return path;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static object Select(string path, string address = null) => new { guid = AssetDatabase.AssetPathToGUID(path), address = address };
        private static object Group(string id, params object[] selections) => new { id = id, selections = selections };

        private ScriptableObject Project(params object[] groups)
        {
            var assembly = typeof(ResourceProject).Assembly;
            var projectType = assembly.GetType("YLFramework.Editor.Resource.ResourceProject");
            Assert.That(projectType, Is.Not.Null, "The resource build map requires its own formal project definition.");
            var project = ScriptableObject.CreateInstance(projectType);
            JsonUtility.FromJsonOverwrite(JsonConvert.SerializeObject(new
            {
                projectId = "yl-test-" + Guid.NewGuid().ToString("N"),
                groups = groups
            }), project);
            ((ResourceProject)project).sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content") });
            AssetDatabase.CreateAsset(project, _folder + "/Project-" + Guid.NewGuid().ToString("N") + ".asset");
            return project;
        }

        private static object Map(ScriptableObject project)
        {
            var assembly = typeof(ResourceProject).Assembly;
            var mapType = assembly.GetType("YLFramework.Editor.Resource.ResourceBuildMap");
            Assert.That(mapType, Is.Not.Null);
            return mapType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { project });
        }

        private static object Value(object value, string property) => value.GetType().GetProperty(property).GetValue(value);
        private static object[] Items(object value, string property) => ((System.Collections.IEnumerable)Value(value, property)).Cast<object>().ToArray();
        private static void AssertRejected(Action action, string message)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => action());
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException.Message.ToLowerInvariant(), Does.Contain(message));
        }
    }
}
