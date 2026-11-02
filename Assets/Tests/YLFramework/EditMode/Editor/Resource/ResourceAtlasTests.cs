using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceAtlasTests
    {
        internal static string AddAtlas(ResourceReleaseFixture fixture, bool includeInBuild = false, string spritePath = null, bool legacy = false)
        {
            var path = fixture.Folder + "/Content/Explicit" + (legacy ? ".spriteatlas" : ".spriteatlasv2");
            var packables = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath ?? fixture.Folder + "/Content/Sprites.png") };
            if (legacy)
            {
                var atlas = new SpriteAtlas();
                atlas.Add(packables);
                atlas.SetIncludeInBuild(includeInBuild);
                AssetDatabase.CreateAsset(atlas, path);
                AssetDatabase.SaveAssetIfDirty(atlas);
            }
            else
            {
                var atlas = new SpriteAtlasAsset();
                try
                {
                    atlas.Add(packables);
                    atlas.SetIncludeInBuild(includeInBuild);
                    SpriteAtlasAsset.Save(atlas, path);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(path);
                    importer.includeInBuild = includeInBuild;
                    importer.SaveAndReimport();
                }
                finally
                {
                    if ((object)atlas is Object owned)
                    {
                        Object.DestroyImmediate(owned);
                    }
                }
            }
            return path;
        }

        [Test]
        public void FolderPackablesUseTheExistingGraphWithoutPullingInUnrelatedSceneOrPrefabAssets()
        {
            using var fixture = new ResourceReleaseFixture();
            var path = AddAtlas(fixture, legacy: true);
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            atlas.Remove(atlas.GetPackables());
            atlas.Add(new Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(fixture.Folder + "/Content") });
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssetIfDirty(atlas);
            var project = fixture.Project;
            project.groups.Clear();
            project.groups.Add(new ResourceGroup
            {
                id = "atlas",
                selections = new List<ResourceSelection> {
                new() { guid = AssetDatabase.AssetPathToGUID(path) } }
            });
            var map = ResourceBuildMap.Create(project);
            var sources = map.Bundles.SelectMany(value => value.AssetPaths).ToArray();
            Assert.That(sources, Does.Contain(fixture.Folder + "/Content/Sprites.png"));
            Assert.That(sources.Any(value => value.EndsWith(".prefab", StringComparison.Ordinal) || value.EndsWith(".unity", StringComparison.Ordinal)), Is.False);
            Assert.That(sources, Does.Not.Contain(fixture.Folder + "/Content/Shared.asset"));
            Assert.That(map.Bundles.Count, Is.EqualTo(1));
            fixture.AssertProtected();
        }

        [Test]
        public void DuplicateSpriteNamesAreRejectedBeforePackingRatherThanUsingAnAmbiguousFirstMatch()
        {
            using var fixture = new ResourceReleaseFixture();
            var other = fixture.Folder + "/Content/OtherSprites.png";
            Assert.That(AssetDatabase.CopyAsset(fixture.Folder + "/Content/Sprites.png", other), Is.True);
            var path = AddAtlas(fixture, legacy: true);
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            atlas.Add(new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(other) });
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssetIfDirty(atlas);
            var project = fixture.Project;
            project.groups.Clear();
            project.groups.Add(new ResourceGroup
            {
                id = "atlas",
                selections = new List<ResourceSelection> {
                new() { guid = AssetDatabase.AssetPathToGUID(path) } }
            });
            Assert.That(Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(project)).Message, Does.Contain("unique names"));
            fixture.AssertProtected();
        }

        [Test]
        public void UnselectedIncludedAtlasCannotSilentlyInfluenceASpriteBuild()
        {
            using var fixture = new ResourceReleaseFixture();
            AddAtlas(fixture, true);
            Assert.That(Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(fixture.Project)).Message,
                Does.Contain("SpriteAtlas"));
            fixture.AssertProtected();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExplicitAtlasBuildPublishesACompiledTypeAndOnlyDeclaredStableAssets(bool legacy)
        {
            using var fixture = new ResourceReleaseFixture();
            var path = AddAtlas(fixture, legacy: legacy);
            var project = fixture.Project;
            project.groups.Clear();
            project.groups.Add(new ResourceGroup
            {
                id = "atlas",
                selections = new List<ResourceSelection> {
                new() { guid = AssetDatabase.AssetPathToGUID(path) } }
            });
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            Assert.That(ResourceEditorOperations.PrepareAtlases(project), Is.EqualTo(1));
            project = fixture.Project;
            var sourceSprite = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path).GetSprite("Left");
            try
            {
                Assert.That(sourceSprite && sourceSprite.texture, Is.True);
            }
            finally
            {
                if (sourceSprite)
                {
                    Object.DestroyImmediate(sourceSprite);
                }
            }
            var build = fixture.Build();
            Assert.That(build.Map.Assets.Single().AssetType, Is.EqualTo(typeof(SpriteAtlas)));
            Assert.That(build.SubAssets, Is.Empty, "Packing-generated Atlas representations are not stable public Catalog subassets.");
            var release = ResourcePublisher.Publish(build, "fixture-v1");
            fixture.ReleaseRoot = release.RootPath;
            var snapshot = ResourceLocalSnapshot.Open(release.DirectoryPath, release.Catalog.ProjectId, release.Catalog.CatalogDigest, release.Catalog.Platform, "fixture-v1");
            var catalog = snapshot.CreateCatalog();
            Assert.That(catalog.Resolve(new ResourceAddress(build.Map.ProjectId, Path.GetFileName(path))).Location.AssetType, Is.EqualTo(typeof(SpriteAtlas)));
            fixture.AssertProtected();
        }
    }
}
