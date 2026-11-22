using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.TestTools;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables.Authoring")]
    [Category("YL.Resource.Addressables")]
    public sealed class ResourceAddressablesAuthoringTests
    {
        private string _root;
        private string _rootGuid;
        private AddressableAssetSettings _settings;
        private string _spritePath;
        private string AtlasDirectory => _root + "/Atlases";

        [SetUp]
        public void SetUp()
        {
            string folder = "YLAddressablesAuthoring-" + Guid.NewGuid().ToString("N");
            Assert.That(Directory.Exists("Assets/" + folder) || File.Exists("Assets/" + folder) || File.Exists("Assets/" + folder + ".meta"), Is.False);
            _rootGuid = AssetDatabase.CreateFolder("Assets", folder);
            Assert.That(_rootGuid, Has.Length.EqualTo(32));
            _root = "Assets/" + folder;
            _settings = AddressableAssetSettings.Create(_root, "Settings", true, true);
            _settings.DisableCatalogUpdateOnStartup = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings)
            {
                Undo.ClearUndo(_settings);
                foreach (var group in _settings.groups.Where(group => group))
                    Undo.ClearUndo(group);
            }
            if (AssetDatabase.IsValidFolder(_root))
            {
                Assert.That(AssetDatabase.AssetPathToGUID(_root), Is.EqualTo(_rootGuid), "Do not delete a replaced test root.");
                Assert.That(AssetDatabase.DeleteAsset(_root), Is.True);
            }
        }

        [Test]
        public void ValidationAndLookupUseOfficialEntriesWithoutChangingThem()
        {
            var entry = Add("greeting", "app/text/greeting");
            var before = EditorJsonUtility.ToJson(_settings);

            var errors = (string[])Invoke("Validate", _settings);
            var found = Invoke("FindUniqueEntry", _settings, "app/text/greeting");

            Assert.That(errors, Is.Empty);
            Assert.That(found, Is.SameAs(entry));
            Assert.That(Invoke("FindUniqueEntry", _settings, entry.guid), Is.SameAs(entry));
            Assert.That(EditorJsonUtility.ToJson(_settings), Is.EqualTo(before));
        }

        [Test]
        public void AmbiguousAddressesAreRejectedRatherThanTakingTheFirstEntry()
        {
            Add("one", "ambiguous");
            Add("two", "ambiguous");

            Assert.That((string[])Invoke("Validate", _settings), Has.Some.Contains("ambiguous"));
            Assert.Throws<InvalidOperationException>(() => Invoke("FindUniqueEntry", _settings, "ambiguous"));
        }

        [Test]
        public void AutomaticStartupUpdatesAreReportedButNeverSilentlyDisabled()
        {
            _settings.DisableCatalogUpdateOnStartup = false;

            Assert.That((string[])Invoke("Validate", _settings), Has.Some.Contains("Catalog"));
            Assert.That(_settings.DisableCatalogUpdateOnStartup, Is.False);
        }

        [Test]
        public void MissingKeysFailAndMissingSettingsDoNotCreateConfiguration()
        {
            Assert.Throws<InvalidOperationException>(() => Invoke("FindUniqueEntry", _settings, "missing"));
            Assert.That((string[])Invoke("Validate", new object[] { null }), Has.Some.Contains("settings"));
        }

        [UnityTest]
        public IEnumerator ExplicitAtlasUsesPersistedTextureSpriteOrFolderAndRegistersExactOfficialReferences()
        {
            yield return PrepareAtlasInputs();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_spritePath);
            var sprite = AssetDatabase.LoadAllAssetsAtPath(_spritePath).OfType<Sprite>().Single();
            var folder = AssetDatabase.LoadMainAssetAtPath(_root + "/Sprites");
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string sourceGuid, out long sourceLocalId), Is.True);
            Assert.That(sourceGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(_spritePath)));
            Assert.That(sourceLocalId, Is.Not.Zero);

            foreach (var source in new Object[] { texture, sprite, folder })
            {
                SaveOwnedSettings();
                var before = Capture();
                string kind = source is Sprite ? "Sprite" : source is Texture2D ? "Texture" : "Folder";
                string path = AtlasDirectory + "/" + kind + ".spriteatlasv2";
                string address = "atlas/Explicit." + kind;
                var atlas = CreateAtlas(path, new[] { source }, _settings.DefaultGroup, address);
                Assert.That(atlas, Is.Not.Null);
                Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.SameAs(atlas));
                Assert.That(AssetImporter.GetAtPath(path), Is.TypeOf<SpriteAtlasImporter>());
                Assert.That(atlas.IsIncludeInBuild(), Is.False);
                CollectionAssert.AreEquivalent(new[] { source }, atlas.GetPackables());

                string guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(guid, Has.Length.EqualTo(32));
                Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(path));
                var entry = _settings.FindAssetEntry(guid);
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry.address, Is.EqualTo(address));
                Assert.That(entry.parentGroup, Is.SameAs(_settings.DefaultGroup));
                Assert.That(entry.MainAssetType, Is.EqualTo(typeof(SpriteAtlas)));
                Assert.That(Invoke("FindUniqueEntry", _settings, address), Is.SameAs(entry));
                Assert.That(Invoke("FindUniqueEntry", _settings, guid), Is.SameAs(entry));
                var reference = new AssetReferenceT<SpriteAtlas>(guid);
                Assert.That(reference.ValidateAsset(atlas), Is.True);
                Assert.That(reference.editorAsset, Is.SameAs(atlas));
                Assert.That(reference.RuntimeKey.ToString(), Is.EqualTo(guid));

                // Labels are an explicit official authoring action, not an automatic Create promise.
                string label = "atlas.label." + kind;
                _settings.AddLabel(label, false);
                Assert.That(entry.SetLabel(label, true, false, false), Is.True);
                var entries = new List<AddressableAssetEntry>();
                _settings.GetAllAssets(entries, false);
                Assert.That(entries.Single(value => value.labels.Contains(label)), Is.SameAs(entry));
                SaveOwnedSettings();
                AssertProtected(before, new[] { AssetDatabase.GetAssetPath(_settings), AssetDatabase.GetAssetPath(_settings.DefaultGroup) },
                    new[] { path, path + ".meta" });
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string currentGuid, out long currentLocalId), Is.True);
                Assert.That(currentGuid, Is.EqualTo(sourceGuid));
                Assert.That(currentLocalId, Is.EqualTo(sourceLocalId));
                yield return Settled("import explicit " + kind + " atlas");
            }
        }

        [UnityTest]
        public IEnumerator ExistingAtlasIsNeverAdoptedAndItsBytesAndGuidRemainUnchanged()
        {
            yield return PrepareAtlasInputs();
            string path = AtlasDirectory + "/Existing.spriteatlasv2";
            SaveNativeAtlas(path);
            yield return Settled("import the independently authored atlas");
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Has.Length.EqualTo(32));
            Assert.That(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path), Is.Not.Null);
            Assert.That(_settings.FindAssetEntry(guid), Is.Null);
            var before = Capture();
            Assert.Throws<IOException>(() => CreateAtlas(path, SpriteSources(), _settings.DefaultGroup, "atlas/do-not-adopt"));
            AssertProtected(before);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(_settings.FindAssetEntry(guid), Is.Null);
        }

        [UnityTest]
        public IEnumerator ExistingSameNameDirectoryIsNeverAdoptedOrEmptied()
        {
            yield return PrepareAtlasInputs();
            string path = AtlasDirectory + "/Directory.spriteatlasv2";
            string guid = AssetDatabase.CreateFolder(AtlasDirectory, "Directory.spriteatlasv2");
            Assert.That(guid, Has.Length.EqualTo(32));
            File.WriteAllText(Physical(path + "/Keep.txt"), "independent directory payload");
            AssetDatabase.ImportAsset(path + "/Keep.txt", ImportAssetOptions.ForceSynchronousImport);
            yield return Settled("import the occupied output directory");
            var before = Capture();
            Assert.Throws<IOException>(() => CreateAtlas(path, SpriteSources(), _settings.DefaultGroup, "atlas/directory"));
            AssertProtected(before);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(AssetDatabase.IsValidFolder(path), Is.True);
        }

        [UnityTest]
        public IEnumerator OrphanAtlasMetaIsNeverAdoptedAndRetainsItsActualOriginalGuidBytes()
        {
            yield return PrepareAtlasInputs();
            string path = AtlasDirectory + "/Orphan.spriteatlasv2";
            SaveNativeAtlas(path);
            yield return Settled("import the atlas before making its meta orphaned");
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Has.Length.EqualTo(32));
            Assert.That(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path), Is.Not.Null);
            var atlasBytes = File.ReadAllBytes(Physical(path));
            var metaBytes = File.ReadAllBytes(Physical(path + ".meta"));
            File.Delete(Physical(path));
            try
            {
                var before = Capture();
                Assert.Throws<IOException>(() => CreateAtlas(path, SpriteSources(), _settings.DefaultGroup, "atlas/orphan"));
                AssertProtected(before);
                Assert.That(File.Exists(Physical(path)), Is.False);
                CollectionAssert.AreEqual(metaBytes, File.ReadAllBytes(Physical(path + ".meta")));
            }
            finally
            {
                // Restore only the file this test deliberately removed, without replacing a late writer.
                CollectionAssert.AreEqual(metaBytes, File.ReadAllBytes(Physical(path + ".meta")));
                using var restore = new FileStream(Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                restore.Write(atlasBytes, 0, atlasBytes.Length);
            }
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
        }

        [UnityTest]
        public IEnumerator NullAndReadOnlyGroupsAreRejectedBeforeCreatingAnyOutput()
        {
            yield return PrepareAtlasInputs();
            var locked = _settings.CreateGroup("ReadOnlyAtlasTarget", false, true, false, null);
            SaveOwnedSettings();
            yield return Settled("save the read-only official group");
            Assert.That(locked.ReadOnly, Is.True);
            foreach (var group in new[] { null, locked })
            {
                var before = Capture();
                Assert.Throws<InvalidOperationException>(() => CreateAtlas(AtlasDirectory + "/InvalidGroup.spriteatlasv2", SpriteSources(), group, "atlas/invalid-group"));
                AssertProtected(before);
            }
        }

        [UnityTest]
        public IEnumerator InvalidPathsEmptyAddressesAndOccupiedOfficialKeysAreRejectedBeforeWrites()
        {
            yield return PrepareAtlasInputs();
            var occupied = Add("Occupied", "atlas/occupied");
            SaveOwnedSettings();
            yield return Settled("save the occupied official keys");
            foreach (string path in new[] { AtlasDirectory + "/Wrong.spriteatlas", AtlasDirectory + "/Missing/New.spriteatlasv2" })
            {
                var before = Capture();
                Assert.Throws<IOException>(() => CreateAtlas(path, SpriteSources(), _settings.DefaultGroup, "atlas/valid"));
                AssertProtected(before);
            }
            var boundary = Capture();
            string outside = "Packages/Unowned-" + _rootGuid + ".spriteatlasv2";
            Assert.That(File.Exists(Physical(outside)) || Directory.Exists(Physical(outside)) || File.Exists(Physical(outside + ".meta")), Is.False);
            Assert.Throws<InvalidOperationException>(() => CreateAtlas(outside, SpriteSources(), _settings.DefaultGroup, "atlas/outside"));
            AssertProtected(boundary);
            Assert.That(File.Exists(Physical(outside)) || Directory.Exists(Physical(outside)) || File.Exists(Physical(outside + ".meta")), Is.False);
            foreach (string address in new[] { null, "", " " })
            {
                var before = Capture();
                Assert.Throws<ArgumentException>(() => CreateAtlas(AtlasDirectory + "/EmptyKey.spriteatlasv2", SpriteSources(), _settings.DefaultGroup, address));
                AssertProtected(before);
            }
            foreach (string key in new[] { occupied.address, occupied.guid })
            {
                var before = Capture();
                Assert.That(Assert.Throws<InvalidOperationException>(() => CreateAtlas(AtlasDirectory + "/Duplicate.spriteatlasv2", SpriteSources(), _settings.DefaultGroup, key)).Message,
                    Does.Contain(key));
                AssertProtected(before);
            }
        }

        [UnityTest]
        public IEnumerator NonPersistentEmptyNullAndUnsupportedSourcesAreRejectedBeforeWrites()
        {
            yield return PrepareAtlasInputs();
            var unsupported = Add("NotASprite", "text/not-a-sprite");
            SaveOwnedSettings();
            yield return Settled("import the unsupported persistent source");
            var transientTexture = new Texture2D(2, 2);
            var transientSprite = Sprite.Create(transientTexture, new Rect(0, 0, 2, 2), Vector2.zero);
            try
            {
                var beforeNull = Capture();
                Assert.Throws<ArgumentNullException>(() => CreateAtlas(AtlasDirectory + "/NullSources.spriteatlasv2", null, _settings.DefaultGroup, "atlas/null"));
                AssertProtected(beforeNull);
                foreach (var sources in new[] { Array.Empty<Object>(), new Object[] { null }, new Object[] { transientTexture },
                    new Object[] { transientSprite }, new[] { AssetDatabase.LoadMainAssetAtPath(unsupported.AssetPath) } })
                {
                    var before = Capture();
                    Assert.Throws<ArgumentException>(() => CreateAtlas(AtlasDirectory + "/InvalidSources.spriteatlasv2", sources, _settings.DefaultGroup, "atlas/invalid-source"));
                    AssertProtected(before);
                }
            }
            finally
            {
                Object.DestroyImmediate(transientSprite);
                Object.DestroyImmediate(transientTexture);
            }
        }

        [UnityTest]
        public IEnumerator UndoAndRedoAffectRegistrationButNeverDeleteTheCreatedAtlas()
        {
            yield return PrepareAtlasInputs();
            string path = AtlasDirectory + "/Undo.spriteatlasv2";
            string address = "atlas/undo-registration";
            var before = Capture();
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            var atlas = CreateAtlas(path, SpriteSources(), _settings.DefaultGroup, address);
            string guid = AssetDatabase.AssetPathToGUID(path);
            var atlasBytes = File.ReadAllBytes(Physical(path));
            var metaBytes = File.ReadAllBytes(Physical(path + ".meta"));
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(undoGroup);
            Assert.That(_settings.DefaultGroup.entries.Single(entry => entry.guid == guid).address, Is.EqualTo(address));
            Undo.PerformUndo();
            Assert.That(_settings.DefaultGroup.entries.Any(entry => entry.guid == guid), Is.False, "Create records the official registration for Undo, not the atlas file's lifetime.");
            Assert.That(_settings.FindAssetEntry(guid), Is.Null);
            Assert.That(atlas && AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path), Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            CollectionAssert.AreEqual(atlasBytes, File.ReadAllBytes(Physical(path)));
            CollectionAssert.AreEqual(metaBytes, File.ReadAllBytes(Physical(path + ".meta")));
            Undo.PerformRedo();
            Assert.That(_settings.DefaultGroup.entries.Single(entry => entry.guid == guid).address, Is.EqualTo(address));
            Assert.That(_settings.FindAssetEntry(guid).MainAssetType, Is.EqualTo(typeof(SpriteAtlas)));
            AssertProtected(before, new[] { AssetDatabase.GetAssetPath(_settings), AssetDatabase.GetAssetPath(_settings.DefaultGroup) }, new[] { path, path + ".meta" });
            CollectionAssert.AreEqual(atlasBytes, File.ReadAllBytes(Physical(path)));
            CollectionAssert.AreEqual(metaBytes, File.ReadAllBytes(Physical(path + ".meta")));
        }

        private IEnumerator PrepareAtlasInputs()
        {
            Assert.That(EditorSettings.spritePackerMode, Is.EqualTo(SpritePackerMode.SpriteAtlasV2), "The owned test host must explicitly enable V2; this fixture never changes global packer settings.");
            Assert.That(AssetDatabase.CreateFolder(_root, "Sprites"), Is.Not.Empty);
            Assert.That(AssetDatabase.CreateFolder(_root, "Atlases"), Is.Not.Empty);
            _spritePath = _root + "/Sprites/Source.png";
            var pixels = new Texture2D(4, 4);
            try
            {
                pixels.SetPixels(Enumerable.Repeat(Color.cyan, 16).ToArray());
                pixels.Apply();
                File.WriteAllBytes(Physical(_spritePath), pixels.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(pixels);
            }
            AssetDatabase.ImportAsset(_spritePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(_spritePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(_spritePath).OfType<Sprite>().Count(), Is.EqualTo(1));
            SaveOwnedSettings();
            yield return Settled("import the real persisted sprite inputs");
        }

        [UnityTest]
        public IEnumerator DistinctSpritesWithTheSameNameCannotCreateAnAmbiguousAtlas()
        {
            yield return PrepareAtlasInputs();
            var folder = _root + "/OtherSprites";
            Assert.That(AssetDatabase.CreateFolder(_root, "OtherSprites"), Is.Not.Empty);
            var other = folder + "/Source.png";
            Assert.That(AssetDatabase.CopyAsset(_spritePath, other), Is.True);
            yield return Settled("import both distinct same-name sprites");
            var first = AssetDatabase.LoadAllAssetsAtPath(_spritePath).OfType<Sprite>().Single();
            var second = AssetDatabase.LoadAllAssetsAtPath(other).OfType<Sprite>().Single();
            Assert.That(first.name, Is.EqualTo(second.name));
            Assert.That(AssetDatabase.AssetPathToGUID(other), Is.Not.EqualTo(AssetDatabase.AssetPathToGUID(_spritePath)));

            string output = AtlasDirectory + "/Ambiguous.spriteatlasv2";
            var before = Capture();
            var sourceFolder = AssetDatabase.LoadMainAssetAtPath(folder);
            Assert.That(Assert.Throws<InvalidOperationException>(() => CreateAtlas(output, new[] { first, sourceFolder }, _settings.DefaultGroup, "atlas/ambiguous")).Message,
                Does.Contain("unique names").And.Contain("Source"));
            AssertProtected(before);
            Assert.That(File.Exists(Physical(output)) || File.Exists(Physical(output + ".meta")), Is.False);
        }

        private Object[] SpriteSources() => new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(_spritePath) };

        private void SaveNativeAtlas(string path)
        {
            var authoring = new SpriteAtlasAsset();
            try
            {
                authoring.Add(SpriteSources());
                SpriteAtlasAsset.Save(authoring, path);
            }
            finally
            {
                Object.DestroyImmediate(authoring);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private void SaveOwnedSettings()
        {
            foreach (var group in _settings.groups.Where(group => group))
            {
                EditorUtility.SetDirty(group);
                AssetDatabase.SaveAssetIfDirty(group);
            }
            EditorUtility.SetDirty(_settings);
            AssetDatabase.SaveAssetIfDirty(_settings);
        }

        private static IEnumerator Settled(string stage)
        {
            double deadline = EditorApplication.timeSinceStartup + 60;
            int idle = 0;
            while (idle < 2)
            {
                Assert.That(EditorApplication.timeSinceStartup, Is.LessThan(deadline), stage);
                Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
                idle = EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer ? 0 : idle + 1;
                yield return null;
            }
        }

        private static string Physical(string path) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));

        private string[] ProtectedFilePaths() => Directory.GetFiles(Physical(_root), "*", SearchOption.AllDirectories)
            .Concat(new[] { Physical(_root + ".meta"), Physical("Packages/manifest.json"), Physical("Packages/packages-lock.json"),
                Physical("ProjectSettings/ProjectSettings.asset"), Physical("ProjectSettings/EditorSettings.asset") })
            .Select(Path.GetFullPath).OrderBy(path => path, StringComparer.Ordinal).ToArray();

        private sealed class Protection
        {
            internal Dictionary<string, byte[]> Files;
            internal string[] Directories;
            internal Dictionary<string, string> Guids;
            internal Dictionary<Object, string> Objects;
            internal SpritePackerMode PackerMode;
        }

        private Protection Capture() => new()
        {
            Files = ProtectedFilePaths().ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal),
            Directories = Directory.GetDirectories(Physical(_root), "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray(),
            Guids = AssetDatabase.GetAllAssetPaths().Where(path => path == _root || path.StartsWith(_root + "/", StringComparison.Ordinal))
                .Select(path => (Path: path, Guid: AssetDatabase.AssetPathToGUID(path))).Where(value => !string.IsNullOrEmpty(value.Guid))
                .ToDictionary(value => value.Path, value => value.Guid, StringComparer.Ordinal),
            Objects = _settings.groups.Where(group => group).SelectMany(group => group.Schemas.Cast<Object>()).Concat(_settings.groups.Cast<Object>())
                .Append(_settings).Where(value => value).Distinct().ToDictionary(value => value, value => EditorJsonUtility.ToJson(value)),
            PackerMode = EditorSettings.spritePackerMode
        };

        private void AssertProtected(Protection before, string[] mutable = null, string[] added = null)
        {
            var mutableFiles = (mutable ?? Array.Empty<string>()).Select(Physical).ToHashSet(StringComparer.Ordinal);
            foreach (string path in mutableFiles)
                Assert.That(before.Files.ContainsKey(path), Is.True, "Only existing owned authoring assets may be mutable.");
            CollectionAssert.AreEquivalent(before.Files.Keys.Concat((added ?? Array.Empty<string>()).Select(Physical)), ProtectedFilePaths(), "Unexpected output or input disappearance.");
            CollectionAssert.AreEquivalent(before.Directories, Directory.GetDirectories(Physical(_root), "*", SearchOption.AllDirectories).Select(Path.GetFullPath), "Creation may not add or take over an output directory.");
            foreach (var pair in before.Files)
            {
                if (!mutableFiles.Contains(pair.Key))
                    CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key), pair.Key);
            }
            foreach (var pair in before.Guids)
            {
                Assert.That(AssetDatabase.AssetPathToGUID(pair.Key), Is.EqualTo(pair.Value), pair.Key);
                Assert.That(AssetDatabase.GUIDToAssetPath(pair.Value), Is.EqualTo(pair.Key), pair.Key);
            }
            foreach (var pair in before.Objects)
            {
                if (!mutableFiles.Contains(Physical(AssetDatabase.GetAssetPath(pair.Key))))
                    Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value));
            }
            Assert.That(EditorSettings.spritePackerMode, Is.EqualTo(before.PackerMode));
        }

        private static SpriteAtlas CreateAtlas(string path, IEnumerable<Object> sources, AddressableAssetGroup group, string address) =>
            (SpriteAtlas)InvokeTool("ResourceAtlasGenerator", "Create", path, sources, group, address);

        private AddressableAssetEntry Add(string file, string address)
        {
            string path = _root + "/" + file + ".txt";
            File.WriteAllText(path, file);
            AssetDatabase.ImportAsset(path);
            var entry = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), _settings.DefaultGroup);
            entry.SetAddress(address);
            return entry;
        }

        private static object Invoke(string name, params object[] arguments)
            => InvokeTool("ResourceAddressablesAuthoring", name, arguments);

        private static object InvokeTool(string tool, string name, params object[] arguments)
        {
            Type authoring = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("YLFramework.Editor.Resource." + tool, false))
                .SingleOrDefault(type => type != null);
            Assert.That(authoring, Is.Not.Null, "Official authoring API is not implemented: " + tool);
            try
            {
                return authoring.GetMethod(name, BindingFlags.Static | BindingFlags.Public).Invoke(null, arguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
