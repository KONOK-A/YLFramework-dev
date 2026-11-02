using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    [Category("YL.Module.Resource")]
    public class ResourceCatalogBuildTests
    {
        internal static ResourceCatalogDocument Create(ResourceBuildResult result, string app = "fixture-v1")
        {
            var creator = typeof(ResourceProject).Assembly.GetType("YLFramework.Editor.Resource.ResourceCatalogBuilder");
            Assert.That(creator, Is.Not.Null, "The catalog must be generated from verified native build results.");
            return (ResourceCatalogDocument)creator.GetMethod("Create").Invoke(null, new object[] { result, app });
        }

        [Test]
        public void RealManifestAssetsSubassetsGroupsAndArtifactIdentitiesRoundTrip()
        {
            using var fixture = new ResourceReleaseFixture();
            var result = fixture.Build();
            var document = Create(result);
            var bytes = ResourceCatalogCodec.Write(document);
            var read = ResourceCatalogCodec.Read(bytes, result.Map.ProjectId, EditorUserBuildSettings.activeBuildTarget.ToString(), Application.unityVersion, "fixture-v1");
            Assert.That(read.CatalogDigest, Is.EqualTo(ResourceCatalogCodecTests.Hash(bytes)));
            Assert.That(read.Bundles.Count, Is.EqualTo(result.Map.Bundles.Count));
            foreach (var bundle in read.Bundles)
            {
                var native = result.Bundles.Single(x => x.Name == bundle.LogicalId);
                CollectionAssert.AreEqual(native.Dependencies, bundle.Dependencies);
                Assert.That(bundle.UnityHash, Is.EqualTo(native.UnityHash));
                Assert.That(bundle.Sha256, Is.EqualTo(result.Files.Single(x => x.Name == bundle.NativeName).Sha256));
                Assert.That(bundle.RelativeFile, Is.EqualTo("b_" + ResourceCatalogCodecTests.Hash(System.Text.Encoding.UTF8.GetBytes(bundle.NativeName))[..32]));
            }
            foreach (var main in result.Map.Assets)
            {
                var asset = read.Assets.Single(x => x.AssetId == main.AssetId);
                Assert.That(asset.Guid, Is.EqualTo(main.Guid));
                Assert.That(asset.LocalId, Is.EqualTo(main.LocalId));
                Assert.That(asset.Address, Is.EqualTo(main.Address.Path));
                Assert.That(asset.BundleId, Is.EqualTo(main.BundleName));
                Assert.That(asset.Kind, Is.EqualTo(main.IsScene ? "Scene" : "Asset"));
                Assert.That(asset.AssetAssembly, Is.EqualTo(main.IsScene ? null : main.AssetType.Assembly.GetName().Name));
            }
            var sprites = read.Assets.Where(x => x.Kind == "SubAsset").ToArray();
            CollectionAssert.AreEquivalent(new[] { "Left", "Right" }, sprites.Select(x => x.SubAssetName));
            Assert.That(sprites.All(x => x.AssetType == typeof(Sprite).FullName && x.AssetAssembly == typeof(Sprite).Assembly.GetName().Name && x.SubAssetKey == x.LocalId.ToString()), Is.True);
            foreach (var group in read.Groups)
            {
                CollectionAssert.AreEqual(result.Map.Groups[group.Id], group.Members.Select(x => x.AssetId));
            }

            var loaded = new Dictionary<string, AssetBundle>();
            try
            {
                foreach (var bundle in read.Bundles.OrderBy(x => x.Dependencies.Count))
                {
                    loaded.Add(bundle.LogicalId, AssetBundle.LoadFromFile(Path.Combine(result.WorkspacePath, bundle.NativeName)));
                }

                foreach (var sprite in sprites)
                {
                    var matches = loaded[sprite.BundleId].LoadAssetWithSubAssets<Sprite>(sprite.LoadName).Where(x => x.name == sprite.SubAssetName).ToArray();
                    Assert.That(matches.Length, Is.EqualTo(1));
                    Assert.That(matches[0].rect.width, Is.EqualTo(2));
                }
                var scene = read.Assets.Single(x => x.Kind == "Scene");
                CollectionAssert.Contains(loaded[scene.BundleId].GetAllScenePaths(), scene.LoadName);
            }
            finally
            {
                foreach (var bundle in loaded.Values.Reverse())
                {
                    if (bundle)
                    {
                        bundle.Unload(true);
                    }
                }
            }
            fixture.AssertProtected();
        }
    }

    internal sealed class ResourceReleaseFixture : IDisposable
    {
        internal readonly string Folder;
        internal readonly string TextPath;
        internal ResourceProject Project => AssetDatabase.LoadAssetAtPath<ResourceProject>(Folder + "/Project.asset");
        private readonly HashSet<string> _ownedWorkspaces = new(StringComparer.Ordinal);
        private readonly string[] _protectedBefore;
        internal string ReleaseRoot;
        internal bool RetainRelease;

        internal ResourceReleaseFixture()
        {
            _protectedBefore = ProtectedSnapshot();
            const string Parent = "Assets/Tests/YLFramework/Fixtures";
            if (!AssetDatabase.IsValidFolder(Parent))
            {
                AssetDatabase.CreateFolder("Assets/Tests/YLFramework", "Fixtures");
            }

            Folder = Parent + "/Release82b-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder(Parent, Path.GetFileName(Folder)), Is.Not.Empty);
            AssetDatabase.CreateFolder(Folder, "Content");
            TextPath = Folder + "/Content/entry.txt";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(TextPath), "first release content");
            AssetDatabase.ImportAsset(TextPath, ImportAssetOptions.ForceSynchronousImport);
            var shared = new Texture2D(2, 2) { name = "Shared" };
            AssetDatabase.CreateAsset(shared, Folder + "/Content/Shared.asset");
            var prefabs = new[] { Prefab("A", shared), Prefab("B", shared) };
            var png = new Texture2D(4, 2);
            png.SetPixels(Enumerable.Repeat(Color.red, 8).ToArray());
            png.Apply();
            var spritePath = Folder + "/Content/Sprites.png";
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(spritePath), png.EncodeToPNG());
            Object.DestroyImmediate(png);
            AssetDatabase.ImportAsset(spritePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritesheet = new[] { new SpriteMetaData { name = "Left", rect = new Rect(0, 0, 2, 2), pivot = Vector2.one / 2 },
                new SpriteMetaData { name = "Right", rect = new Rect(2, 0, 2, 2), pivot = Vector2.one / 2 } };
            importer.SaveAndReimport();
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var scenePath = Folder + "/Content/Owned.unity";
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = shared };
                AssetDatabase.CreateAsset(material, Folder + "/Content/SceneMaterial.mat");
                GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<MeshRenderer>().sharedMaterial = material;
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
            }
            finally
            {
                if (setup.Length != 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "yl-release-" + Guid.NewGuid().ToString("N");
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(Folder + "/Content") });
            foreach (var path in new[] { TextPath }.Concat(prefabs).Concat(new[] { spritePath, scenePath }))
            {
                project.groups.Add(new ResourceGroup
                {
                    id = Path.GetFileNameWithoutExtension(path),
                    selections = new List<ResourceSelection> { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(path) } }
                });
            }

            AssetDatabase.CreateAsset(project, Folder + "/Project.asset");
        }

        internal ResourceBuildResult Build()
        {
            var expected = ResourceBundleBuilder.GetWorkspacePath(Project, EditorUserBuildSettings.activeBuildTarget);
            var root = Path.GetDirectoryName(expected);
            if (_ownedWorkspaces.Add(root))
            {
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False, "Do not adopt any existing fixture workspace.");
            }

            return ResourceBundleBuilder.Build(Project, EditorUserBuildSettings.activeBuildTarget);
        }
        private string Prefab(string name, Texture texture)
        {
            var material = new Material(Shader.Find("Unlit/Texture")) { name = name, mainTexture = texture };
            AssetDatabase.CreateAsset(material, Folder + "/Content/" + name + ".mat");
            var go = new GameObject(name);
            try
            {
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                var path = Folder + "/Content/" + name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(go, path);
                return path;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
        internal void AssertProtected() => CollectionAssert.AreEqual(_protectedBefore, ProtectedSnapshot());
        internal static string[] Snapshot(string root) => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)) ? Array.Empty<string>() :
            Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
                .Where(x => !Path.GetFileName(x).EndsWith(".lock", StringComparison.Ordinal))
                .Select(x => x.Substring(root.Length) + ":" + ResourceCatalogCodecTests.Hash(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(x)))).ToArray();
        private static string[] ProtectedSnapshot() => new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings" }
            .SelectMany(x => Snapshot(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(x))).Select(f => x + f)).ToArray();
        public void Dispose()
        {
            AssertProtected();
            var paths = DragAndDrop.paths ?? Array.Empty<string>();
            bool OwnsPath(string path) => !string.IsNullOrEmpty(path) && (path == Folder || path.StartsWith(Folder + "/", StringComparison.Ordinal));
            if (paths.Length != 0 && paths.All(OwnsPath) &&
                DragAndDrop.objectReferences.All(value => value && OwnsPath(AssetDatabase.GetAssetPath(value))))
            {
                // Empty arrays do not clear Unity's native drag state. Release only this fixture's drag.
                var cleanup = typeof(DragAndDrop).GetMethod("Cleanup", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                Assert.That(cleanup, Is.Not.Null);
                cleanup.Invoke(null, null);
                Assert.That(DragAndDrop.paths, Is.Null.Or.Empty);
                Assert.That(DragAndDrop.objectReferences, Is.Empty);
            }
            foreach (var root in _ownedWorkspaces)
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
                }
            }

            if (!RetainRelease && ReleaseRoot != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ReleaseRoot)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(ReleaseRoot), true);
            }

            AssetDatabase.DeleteAsset(Folder);
        }
    }
}
