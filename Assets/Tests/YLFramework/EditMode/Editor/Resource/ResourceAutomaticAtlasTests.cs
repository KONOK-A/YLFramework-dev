using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using YLFramework.Editor.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceAutomaticAtlasTests
    {
        [Test]
        public void BuildKeepsItsNullProjectArgumentContractBeforeAutomaticGeneration()
        {
            Assert.Throws<ArgumentNullException>(() => ResourceBundleBuilder.Build(null, EditorUserBuildSettings.activeBuildTarget));
        }

        [Test]
        public void RegistrationCreatesANewOwnedScopeAndPreviewDoesNotWriteAssets()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = ResourceAtlasGenerator.CreateConfiguration(fixture.Project, fixture.Folder + "/Content", "GeneratedAtlases");
            Assert.That(configuration, Is.Not.Null);
            var directory = fixture.Folder + "/Content/GeneratedAtlases";
            Assert.That(AssetDatabase.IsValidFolder(directory), Is.True);
            var before = ResourceReleaseFixture.Snapshot(directory);
            ResourceAtlasGenerator.Preview(fixture.Project);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(directory));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(directory), "*.spriteatlas*"), Is.Empty);
            fixture.AssertProtected();
        }

        internal static ResourceAtlasConfiguration Configure(ResourceReleaseFixture fixture, bool folder = false, ResourceAtlasGrouping grouping = ResourceAtlasGrouping.Together)
        {
            var project = fixture.Project;
            project.groups.Clear();
            project.groups.Add(new ResourceGroup { id = "generated" });
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            var configuration = ResourceAtlasGenerator.CreateConfiguration(project, fixture.Folder + "/Content", "GeneratedAtlases");
            var sourcePath = fixture.Folder + "/Content/Sprites.png";
            Object source = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
            if (folder)
            {
                Assert.That(AssetDatabase.CreateFolder(fixture.Folder + "/Content", "AutoSources"), Is.Not.Empty);
                Assert.That(AssetDatabase.MoveAsset(sourcePath, fixture.Folder + "/Content/AutoSources/Sprites.png"), Is.Empty);
                source = AssetDatabase.LoadAssetAtPath<DefaultAsset>(fixture.Folder + "/Content/AutoSources");
            }
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "ui",
                groupId = "generated",
                address = "atlases/ui",
                grouping = grouping,
                readable = true,
                sources = new[] { source }
            });
            Save(configuration);
            return configuration;
        }

        private static void Save(ResourceAtlasConfiguration configuration)
        {
            EditorUtility.SetDirty(configuration);
            AssetDatabase.SaveAssetIfDirty(configuration);
        }

        internal static string AddSprite(string folder, string name, Color color)
        {
            var path = folder + "/" + name + ".png";
            var texture = new Texture2D(4, 4);
            try
            {
                texture.SetPixels(Enumerable.Repeat(color, 16).ToArray());
                texture.Apply();
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            return path;
        }

        [Test]
        public void GeneratedAtlasIsAFormalGroupAssetAndRepeatPreservesGuidAndEveryByte()
        {
            using var fixture = new ResourceReleaseFixture();
            Configure(fixture);
            var source = fixture.Folder + "/Content/Sprites.png";
            var sourceMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta"));
            var before = ResourceAtlasGenerator.Preview(fixture.Project);
            Assert.That(before.Changes.Single().Kind, Is.EqualTo(ResourceAtlasChangeKind.Create));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(before.Changes.Single().Path)), Is.False);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var current = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            Assert.That(current.Kind, Is.EqualTo(ResourceAtlasChangeKind.Current));
            var guid = current.Guid;
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(current.Path);
            Assert.That(atlas.spriteCount, Is.EqualTo(2));
            Assert.That(atlas.IsIncludeInBuild(), Is.False);
            var bytes = ResourceReleaseFixture.Snapshot(before.DirectoryPath);
            ResourceAtlasGenerator.Generate(fixture.Project);
            CollectionAssert.AreEqual(bytes, ResourceReleaseFixture.Snapshot(before.DirectoryPath));
            Assert.That(AssetDatabase.AssetPathToGUID(current.Path), Is.EqualTo(guid));
            var map = ResourceBuildMap.Create(fixture.Project);
            Assert.That(map.Assets.Single().AssetType, Is.EqualTo(typeof(SpriteAtlas)));
            Assert.That(map.Assets.Single().Address.Path, Is.EqualTo("atlases/ui"));
            Assert.That(map.Groups["generated"], Does.Contain(map.Assets.Single().AssetId));
            Assert.That(map.Bundles.SelectMany(x => x.AssetPaths), Does.Contain(source));
            CollectionAssert.AreEqual(sourceMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta")));
            fixture.AssertProtected();
        }

        [Test]
        public void FolderChangesSettingsAndMovesUpdateTheSameAtlasIdentity()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture, folder: true);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var initial = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            var folder = fixture.Folder + "/Content/AutoSources";
            var extra = AddSprite(folder, "Blue", Color.blue);
            Assert.That(ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single().Kind, Is.EqualTo(ResourceAtlasChangeKind.Update));
            ResourceAtlasGenerator.Generate(fixture.Project);
            Assert.That(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(initial.Path).spriteCount, Is.EqualTo(3));
            Assert.That(AssetDatabase.MoveAsset(folder, fixture.Folder + "/Content/MovedSources"), Is.Empty);
            configuration.rules[0].padding = 8;
            configuration.rules[0].address = "atlases/renamed";
            Save(configuration);
            ResourceAtlasGenerator.Generate(fixture.Project);
            Assert.That(AssetDatabase.AssetPathToGUID(initial.Path), Is.EqualTo(initial.Guid));
            Assert.That(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(initial.Path).GetPackingSettings().padding, Is.EqualTo(8));
            Assert.That(AssetDatabase.DeleteAsset(extra.Replace("AutoSources", "MovedSources")), Is.True);
            ResourceAtlasGenerator.Generate(fixture.Project);
            Assert.That(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(initial.Path).spriteCount, Is.EqualTo(2));
            Assert.That(ResourceBuildMap.Create(fixture.Project).Assets.Single().Address.Path, Is.EqualTo("atlases/renamed"));
            Assert.That(AssetDatabase.AssetPathToGUID(initial.Path), Is.EqualTo(initial.Guid));
            fixture.AssertProtected();
        }

        [Test]
        public void PerFolderIdentityFollowsFolderGuidAndObsoleteOutputsRequireExplicitUnreferencedDeletion()
        {
            using var fixture = new ResourceReleaseFixture();
            Configure(fixture, folder: true, grouping: ResourceAtlasGrouping.PerFolder);
            var folder = fixture.Folder + "/Content/AutoSources";
            AssetDatabase.CreateFolder(folder, "Icons");
            var extra = AddSprite(folder + "/Icons", "Blue", Color.blue);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var first = ResourceAtlasGenerator.Preview(fixture.Project).Changes.ToArray();
            Assert.That(first.Length, Is.EqualTo(2));
            var icon = first.Single(x => x.Address == "atlases/ui/AutoSources/Icons");
            Assert.That(AssetDatabase.MoveAsset(folder + "/Icons", folder + "/Renamed"), Is.Empty);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var moved = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single(x => x.Key == icon.Key);
            Assert.That(moved.Guid, Is.EqualTo(icon.Guid));
            Assert.That(moved.Address, Is.EqualTo("atlases/ui/AutoSources/Renamed"));
            Assert.That(AssetDatabase.DeleteAsset(extra.Replace("/Icons/", "/Renamed/")), Is.True);
            var obsolete = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single(x => x.Key == icon.Key);
            Assert.That(obsolete.Kind, Is.EqualTo(ResourceAtlasChangeKind.Obsolete));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(icon.Path)), Is.True);
            ResourceAtlasGenerator.Generate(fixture.Project);
            Assert.That(ResourceBuildMap.Create(fixture.Project).Assets.Any(x => x.Guid == icon.Guid), Is.False);
            var selected = fixture.Project;
            selected.groups[0].selections.Add(new ResourceSelection { guid = icon.Guid });
            EditorUtility.SetDirty(selected);
            AssetDatabase.SaveAssetIfDirty(selected);
            Assert.That(Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.DeleteObsolete(fixture.Project, icon.Key)).Message, Does.Contain("selected"));
            selected.groups[0].selections.Clear();
            EditorUtility.SetDirty(selected);
            AssetDatabase.SaveAssetIfDirty(selected);
            ResourceAtlasGenerator.DeleteObsolete(fixture.Project, icon.Key);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(icon.Path)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(icon.Path + ".meta")), Is.False);
            fixture.AssertProtected();
        }

        [Test]
        public void ReadOnlyPreflightReportsGenerationNeededAndFormalBuildGeneratesBeforeFreezingInputs()
        {
            using var fixture = new ResourceReleaseFixture();
            Configure(fixture);
            var directory = AssetDatabase.GetAssetPath(fixture.Project.automaticAtlases);
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(directory));
            var text = ResourceEditorOperations.Preflight(fixture.Project, "fixture-v1");
            Assert.That(text, Does.Contain("Create"));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(directory)));
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(fixture.Project));
            var report = ResourceEditorOperations.BuildAndPublish(fixture.Project, "fixture-v1");
            Assert.That(report.BuildError, Is.Null, report.BuildError?.ToString());
            Assert.That(report.PublicationError, Is.Null, report.PublicationError?.ToString());
            fixture.ReleaseRoot = report.Release.RootPath;
            Assert.That(report.Build.Map.Assets.Single().Address.Path, Is.EqualTo("atlases/ui"));
            Assert.That(report.Release.Catalog.Assets.Single().Address, Is.EqualTo("atlases/ui"));
            // Register the already built workspace for the fixture's ownership-based cleanup.
            var workspace = Path.GetDirectoryName(report.Build.WorkspacePath);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)), Is.True);
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(workspace), true);
            fixture.AssertProtected();
        }

        [Test]
        public void ConflictsInvalidRulesAndExternalChangesNeverOverwriteExistingContent()
        {
            using var fixture = new ResourceReleaseFixture();
            AssetDatabase.CreateFolder(fixture.Folder + "/Content", "Unknown");
            var unknown = fixture.Folder + "/Content/Unknown";
            var unknownMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(unknown + ".meta"));
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.CreateConfiguration(fixture.Project, fixture.Folder + "/Content", "Unknown"));
            CollectionAssert.AreEqual(unknownMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(unknown + ".meta")));
            var configuration = Configure(fixture);
            configuration.rules[0].sources = new Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(fixture.Folder + "/Content") };
            Save(configuration);
            Assert.That(Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project)).Message, Does.Contain("overlap"));
            configuration.rules[0].sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(fixture.Folder + "/Content/Sprites.png") };
            Save(configuration);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var change = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            var importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(change.Path);
            var packing = importer.packingSettings;
            packing.padding = 8;
            importer.packingSettings = packing;
            importer.SaveAndReimport();
            var bytes = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(change.Path));
            Assert.That(Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project)).Message, Does.Contain("externally modified"));
            CollectionAssert.AreEqual(bytes, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(change.Path)));
            fixture.AssertProtected();
        }

        [Test]
        public void CancellationAfterImportRollsBackNewOutputsAndPreviousAtlasBytesAndGuid()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var initial = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            configuration.rules[0].padding = 8;
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "other",
                groupId = "generated",
                address = "atlases/other",
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(AddSprite(fixture.Folder + "/Content", "Blue", Color.blue)) }
            });
            Save(configuration);
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(initial.Path));
            using var cancellation = new CancellationTokenSource();
            AutomaticAtlasImportProbe.Imported = paths =>
            {
                if (paths.Contains(initial.Path) && ((SpriteAtlasImporter)AssetImporter.GetAtPath(initial.Path)).packingSettings.padding == 8)
                {
                    cancellation.Cancel();
                }
            };
            try
            {
                Assert.Throws<OperationCanceledException>(() => ResourceAtlasGenerator.Generate(fixture.Project, cancellation.Token));
            }
            finally
            {
                AutomaticAtlasImportProbe.Imported = null;
            }
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(initial.Path)));
            Assert.That(AssetDatabase.AssetPathToGUID(initial.Path), Is.EqualTo(initial.Guid));
            ResourceAtlasGenerator.Generate(fixture.Project);
            Assert.That(ResourceAtlasGenerator.Preview(fixture.Project).Changes.All(x => x.Kind == ResourceAtlasChangeKind.Current), Is.True);
            fixture.AssertProtected();
        }

        [Test]
        public void DisconnectingRegisteredConfigurationCannotSilentlyPublishWithoutItsAtlases()
        {
            using var fixture = new ResourceReleaseFixture();
            Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var project = fixture.Project;
            project.groups[0].selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(fixture.TextPath) });
            project.automaticAtlases = null;
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(project));
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(project));
            fixture.AssertProtected();
        }

        [TestCase("orphan")]
        [TestCase("unknown-file")]
        [TestCase("missing-output")]
        [TestCase("duplicate-name")]
        [TestCase("invalid-settings")]
        public void InvalidOrUnknownInputsAreRejectedBeforeAnyOwnedOutputIsChanged(string scenario)
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture, folder: true);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var plan = ResourceAtlasGenerator.Preview(fixture.Project);
            var output = plan.Changes.Single();
            switch (scenario)
            {
                case "orphan":
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(plan.DirectoryPath + "/orphan.spriteatlasv2.meta"), "not owned");
                    break;
                case "unknown-file":
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(plan.DirectoryPath + "/unknown.txt"), "not owned");
                    break;
                case "missing-output":
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(output.Path));
                    break;
                case "duplicate-name":
                    Assert.That(AssetDatabase.CopyAsset(fixture.Folder + "/Content/AutoSources/Sprites.png", fixture.Folder + "/Content/AutoSources/Duplicate.png"), Is.True);
                    break;
                case "invalid-settings":
                    configuration.rules[0].maxTextureSize = 513;
                    Save(configuration);
                    break;
            }
            var before = ResourceReleaseFixture.Snapshot(plan.DirectoryPath);
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(plan.DirectoryPath));
            fixture.AssertProtected();
        }

        [Test]
        public void SourceChangesDuringGenerationRollBackOutputsAndNeverProceedToBuildAnOldResult()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var initial = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            configuration.rules[0].padding = 8;
            Save(configuration);
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(initial.Path));
            var source = fixture.Folder + "/Content/Sprites.png";
            var touched = false;
            AutomaticAtlasImportProbe.Imported = paths =>
            {
                if (touched || !paths.Contains(initial.Path) || ((SpriteAtlasImporter)AssetImporter.GetAtPath(initial.Path)).packingSettings.padding != 8)
                {
                    return;
                }

                touched = true;
                // A valid external source byte change during import must invalidate the frozen generation intent.
                var png = new Texture2D(4, 2);
                try
                {
                    png.SetPixels(Enumerable.Repeat(Color.blue, 8).ToArray());
                    png.Apply();
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source), png.EncodeToPNG());
                }
                finally
                {
                    Object.DestroyImmediate(png);
                }
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project));
            }
            finally
            {
                AutomaticAtlasImportProbe.Imported = null;
            }
            Assert.That(touched, Is.True);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(initial.Path)));
            Assert.That(AssetDatabase.AssetPathToGUID(initial.Path), Is.EqualTo(initial.Guid));
            Assert.Throws<InvalidOperationException>(() => ResourceBuildMap.Create(fixture.Project));
            fixture.AssertProtected();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ImportCallbacksCannotMakeTheNextCreatePathOwnedOrCauseRollbackToDeleteUnknownAssets(bool cancel)
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var initial = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(initial.Path));
            var originalMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(initial.Path + ".meta"));
            configuration.rules[0].padding = 8;
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "other",
                groupId = "generated",
                address = "atlases/other",
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(AddSprite(fixture.Folder + "/Content", "Blue", Color.blue)) }
            });
            Save(configuration);
            var next = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single(x => x.Kind == ResourceAtlasChangeKind.Create).Path;
            var unknownMeta = System.Text.Encoding.UTF8.GetString(originalMeta).Replace(initial.Guid, Guid.NewGuid().ToString("N"));
            var injected = false;
            using var cancellation = new CancellationTokenSource();
            AutomaticAtlasImportProbe.Imported = paths =>
            {
                if (injected || !paths.Contains(initial.Path) || ((SpriteAtlasImporter)AssetImporter.GetAtPath(initial.Path)).packingSettings.padding != 8)
                {
                    return;
                }

                injected = true;
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(next), original);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(next + ".meta"), unknownMeta);
                if (cancel)
                {
                    cancellation.Cancel();
                }
            };
            try
            {
                if (cancel)
                {
                    Assert.Throws<OperationCanceledException>(() => ResourceAtlasGenerator.Generate(fixture.Project, cancellation.Token));
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project));
                }
            }
            finally
            {
                AutomaticAtlasImportProbe.Imported = null;
            }
            Assert.That(injected, Is.True);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(next)), Is.True, "The next path was never written by this generation and must not be deleted by rollback.");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(next)));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(next + ".meta")), Is.EqualTo(unknownMeta));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(initial.Path)));
            CollectionAssert.AreEqual(originalMeta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(initial.Path + ".meta")));
            fixture.AssertProtected();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void UnsavedTextureImporterChangesAreRejectedBeforeFolderFiltering(bool wasSprite)
        {
            using var fixture = new ResourceReleaseFixture();
            Configure(fixture, folder: true);
            var source = AddSprite(fixture.Folder + "/Content/AutoSources", "Blue", Color.blue);
            var importer = (TextureImporter)AssetImporter.GetAtPath(source);
            if (!wasSprite)
            {
                importer.textureType = TextureImporterType.Default;
                importer.SaveAndReimport();
            }
            ResourceAtlasGenerator.Generate(fixture.Project);
            var output = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(output.Path));
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta"));
            importer.textureType = wasSprite ? TextureImporterType.Default : TextureImporterType.Sprite;
            EditorUtility.SetDirty(importer);
            try
            {
                Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Preview(fixture.Project));
                Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project));
                CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(output.Path)));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(source + ".meta")));
            }
            finally
            {
                importer.textureType = wasSprite ? TextureImporterType.Sprite : TextureImporterType.Default;
                importer.SaveAndReimport();
            }
            fixture.AssertProtected();
        }

        [Test]
        public void DifferentRuleKeysCannotProduceTheSameOutputPathInAReadOnlyPlan()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture, folder: true, grouping: ResourceAtlasGrouping.PerFolder);
            var folderGuid = AssetDatabase.AssetPathToGUID(fixture.Folder + "/Content/AutoSources");
            configuration.rules.Add(new ResourceAtlasRule
            {
                id = "ui_" + folderGuid,
                groupId = "generated",
                address = "atlases/other",
                sources = new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(AddSprite(fixture.Folder + "/Content", "Blue", Color.blue)) }
            });
            Save(configuration);
            var directory = Path.GetDirectoryName(AssetDatabase.GetAssetPath(configuration));
            var before = ResourceReleaseFixture.Snapshot(directory);
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Preview(fixture.Project));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(directory));
            fixture.AssertProtected();
        }

        [Test]
        public void ExternalMetaGuidChangesDuringTheActiveImportAreNeitherAdoptedNorDeletedByRollback()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture);
            configuration.rules[0].padding = 8;
            Save(configuration);
            var path = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single().Path;
            string unknownMeta = null;
            AutomaticAtlasImportProbe.Imported = paths =>
            {
                if (unknownMeta != null || !paths.Contains(path) || ((SpriteAtlasImporter)AssetImporter.GetAtPath(path)).packingSettings.padding != 8)
                {
                    return;
                }

                var actual = AssetDatabase.AssetPathToGUID(path);
                unknownMeta = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")).Replace(actual, Guid.NewGuid().ToString("N"));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"), unknownMeta);
            };
            try
            {
                Assert.Catch(() => ResourceAtlasGenerator.Generate(fixture.Project));
            }
            finally
            {
                AutomaticAtlasImportProbe.Imported = null;
            }
            Assert.That(unknownMeta, Is.Not.Null);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.True);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo(unknownMeta));
            fixture.AssertProtected();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingConfigurationFilesOrMetaAreRejectedEvenWhileUnityKeepsTheObjectResident(bool meta)
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var path = AssetDatabase.GetAssetPath(configuration);
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path + (meta ? ".meta" : "")));
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(path));
            Assert.That(configuration, Is.Not.Null);
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Preview(fixture.Project));
            Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.Generate(fixture.Project));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(path)));
            fixture.AssertProtected();
        }

        [Test]
        public void AnAncestorFolderSelectionThatExcludesAutomaticOutputsDoesNotBlockObsoleteDeletion()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture, folder: true);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var output = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            configuration.rules.Clear();
            Save(configuration);
            var project = fixture.Project;
            project.groups[0].selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(fixture.Folder + "/Content"), extensions = new[] { ".txt", ".spriteatlasv2" } });
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            Assert.That(ResourceBuildMap.Create(project).Assets.Any(x => x.Guid == output.Guid), Is.False);
            ResourceAtlasGenerator.DeleteObsolete(project, output.Key);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output.Path)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output.Path + ".meta")), Is.False);
            fixture.AssertProtected();
        }

        [Test]
        public void SerializedReferencesProtectObsoleteOutputsAndReferenceCandidatesIncludeReadOnlyPackages()
        {
            using var fixture = new ResourceReleaseFixture();
            var configuration = Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var output = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            configuration.rules.Clear();
            Save(configuration);
            var reference = ScriptableObject.CreateInstance<ResourceAtlasConfiguration>();
            reference.rules.Add(new ResourceAtlasRule { sources = new Object[] { AssetDatabase.LoadAssetAtPath<SpriteAtlas>(output.Path) } });
            AssetDatabase.CreateAsset(reference, fixture.Folder + "/Reference.asset");
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(output.Path));
            Assert.That(Assert.Throws<InvalidOperationException>(() => ResourceAtlasGenerator.DeleteObsolete(fixture.Project, output.Key)).Message, Does.Contain("referenced"));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(output.Path)));
            // Verify the real candidate boundary without writing to or fabricating a package.
            var packagePath = AssetDatabase.GetAllAssetPaths().First(x => x.StartsWith("Packages/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(x));
            var method = typeof(ResourceAtlasGenerator).GetMethod("ReferenceCandidates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "The reference scan must include package candidates, not only Assets.");
            Assert.That((System.Collections.Generic.IEnumerable<string>)method.Invoke(null, null), Does.Contain(packagePath));
            fixture.AssertProtected();
        }

        [Test]
        public void RawSpriteChangesBeforeGenerationAreImportedIntoThePackedImage()
        {
            using var fixture = new ResourceReleaseFixture();
            Configure(fixture);
            ResourceAtlasGenerator.Generate(fixture.Project);
            var output = ResourceAtlasGenerator.Preview(fixture.Project).Changes.Single();
            var texture = new Texture2D(4, 2);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.blue, 8).ToArray());
                texture.Apply();
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Folder + "/Content/Sprites.png"), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
            ResourceAtlasGenerator.Generate(fixture.Project);
            var method = typeof(SpriteAtlasExtensions).GetMethod("GetPreviewTextures", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var packed = ((Texture2D[])method.Invoke(null, new object[] { AssetDatabase.LoadAssetAtPath<SpriteAtlas>(output.Path) })).Single();
            var previous = RenderTexture.active;
            var render = RenderTexture.GetTemporary(packed.width, packed.height, 0);
            var pixels = new Texture2D(packed.width, packed.height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(packed, render);
                RenderTexture.active = render;
                pixels.ReadPixels(new Rect(0, 0, packed.width, packed.height), 0, 0);
                pixels.Apply();
                var opaque = pixels.GetPixels().Where(x => x.a > .95f).ToArray();
                Assert.That(opaque, Is.Not.Empty);
                Assert.That(opaque.All(x => x.b > .95f && x.r < .05f), Is.True, "The real packed preview must contain the changed blue source, not stale imported red pixels.");
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(render);
                Object.DestroyImmediate(pixels);
            }
            fixture.AssertProtected();
        }
    }

    internal sealed class AutomaticAtlasImportProbe : AssetPostprocessor
    {
        internal static Action<string[]> Imported;
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths) => Imported?.Invoke(imported);
    }
}
