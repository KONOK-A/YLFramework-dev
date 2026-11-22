using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using YLFramework.Editor.Resource;
using YLFramework.Testing;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Resource.Addressables.Build")]
    public sealed class ResourceAddressablesBuildTests
    {
        private const string PackedContract = "YL.Resource.Addressables.PackedContract";
        private const string SceneLabel = "packed-scene-shared-label";
        private const string DefaultRegistration = AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName;
        private const string LegacyRegistration = AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName;
        private string _root;
        private AddressableAssetSettings _settings;
        private FieldInfo _defaultCache;
        private object _previousCache;
        private UnityEngine.Object _previousRegistration;
        private UnityEngine.Object _previousLegacyRegistration;
        private bool _restoreRegistration;

        [SetUp]
        public void SetUp()
        {
            // The official setter rewrites the registered asset and saves all dirty assets, even when restoring null.
            _defaultCache = typeof(AddressableAssetSettingsDefaultObject).GetField("s_DefaultSettingsObject", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(_defaultCache, Is.Not.Null);
            _previousCache = _defaultCache.GetValue(null);
            EditorBuildSettings.TryGetConfigObject(DefaultRegistration, out _previousRegistration);
            EditorBuildSettings.TryGetConfigObject(LegacyRegistration, out _previousLegacyRegistration);
            _restoreRegistration = true;
            try
            {
                EditorBuildSettings.RemoveConfigObject(DefaultRegistration);
                EditorBuildSettings.RemoveConfigObject(LegacyRegistration);
                _defaultCache.SetValue(null, null);

                var folder = "YLAddressablesBuild-" + Guid.NewGuid().ToString("N");
                var folderGuid = AssetDatabase.CreateFolder("Assets", folder);
                Assert.That(folderGuid, Is.Not.Empty);
                _root = AssetDatabase.GUIDToAssetPath(folderGuid);
                AssetDatabase.CreateFolder(_root, "Configuration");
                _settings = AddressableAssetSettings.Create(_root + "/Configuration", "Settings", true, true);
                _settings.DisableCatalogUpdateOnStartup = true;
                SaveSettings();

                var registration = ScriptableObject.CreateInstance<AddressableAssetSettingsDefaultObject>();
                using var serialized = new SerializedObject(registration);
                serialized.FindProperty("m_AddressableAssetSettingsGuid").stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_settings));
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(registration, _root + "/Configuration/DefaultObject.asset");
                EditorBuildSettings.AddConfigObject(DefaultRegistration, registration, true);
                Assert.That(AddressableAssetSettingsDefaultObject.SettingsExists, Is.True);
                Assert.That(AddressableAssetSettingsDefaultObject.Settings, Is.SameAs(_settings));
            }
            catch
            {
                TearDown();
                throw;
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (!_restoreRegistration)
            {
                return;
            }
            _restoreRegistration = false;
            EditorBuildSettings.RemoveConfigObject(DefaultRegistration);
            _defaultCache.SetValue(null, null);
            try
            {
                if (!string.IsNullOrEmpty(_root))
                {
                    AssetDatabase.DeleteAsset(_root);
                    _root = null;
                }
            }
            finally
            {
                if (_previousLegacyRegistration)
                {
                    EditorBuildSettings.AddConfigObject(LegacyRegistration, _previousLegacyRegistration, true);
                }
                if (_previousRegistration)
                {
                    EditorBuildSettings.AddConfigObject(DefaultRegistration, _previousRegistration, true);
                }
                _defaultCache.SetValue(null, _previousCache);
            }
        }

        [Test]
        public void AutomaticCatalogUpdatesBlockOfficialBuildWithoutChangingTheSetting()
        {
            _settings.DisableCatalogUpdateOnStartup = false;
            SaveSettings();
            var before = EditorJsonUtility.ToJson(_settings);

            Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.ValidateBuild(_settings));

            Assert.That(_settings.DisableCatalogUpdateOnStartup, Is.False);
            Assert.That(EditorJsonUtility.ToJson(_settings), Is.EqualTo(before));
        }

        [Test]
        public void NonDefaultSettingsCannotAccidentallyBuildAnotherOfficialConfiguration()
        {
            var other = ScriptableObject.CreateInstance<AddressableAssetSettings>();
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.ValidateBuild(other));
                Assert.That(error.Message, Does.Contain("official default Addressables settings"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void ExistingContentStateArchiveIsNeverOverwritten()
        {
            var path = Path.Combine(Path.GetTempPath(), "kilo", "YLContentState-" + Guid.NewGuid().ToString("N") + ".bin");
            var bytes = new byte[] { 3, 1, 4, 1, 5 };
            File.WriteAllBytes(path, bytes);
            try
            {
                Assert.Throws<IOException>(() => ResourceEditorOperations.BuildContent(_settings, path));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ContentUpdateCannotUseTheLiveOverwritableBuildStateAsItsFrozenBaseline()
        {
            var liveState = Path.Combine(ContentUpdateScript.GetContentStateDataPath(false, _settings), "addressables_content_state.bin");
            Assert.Throws<ArgumentException>(() => ResourceEditorOperations.BuildContentUpdate(_settings, liveState));
        }

        [TestCase("Settings")]
        [TestCase("Group")]
        [TestCase("Schema")]
        [TestCase("Canceled")]
        public void RejectedBuildDoesNotSaveAuthoringInputsOrCreateAStateArchive(string rejection)
        {
            Add("original", "demo/text/original");
            SaveSettings();
            var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
            string archive = Path.Combine(Path.GetTempPath(), "kilo", "YLUnwrittenState-" + Guid.NewGuid().ToString("N") + ".bin");
            UnityEngine.Object changed = rejection switch
            {
                "Settings" => _settings,
                "Group" => _settings.DefaultGroup,
                "Schema" => _settings.DefaultGroup.Schemas.First(),
                _ => null
            };
            if (changed)
            {
                Undo.RecordObject(changed, "Unsaved Addressables authoring fixture");
                changed.name += " unsaved";
                EditorUtility.SetDirty(changed);
                var failure = Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.BuildContent(_settings, archive));
                Assert.That(failure.Message, Does.Contain("Explicitly save"));
                Assert.That(EditorUtility.IsDirty(changed), Is.True);
                Assert.That(changed.name, Does.EndWith(" unsaved"));
                Undo.ClearUndo(changed);
            }
            else
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Assert.That(Assert.Throws<OperationCanceledException>(() => ResourceEditorOperations.BuildContent(_settings, archive, cancellation.Token))
                    .CancellationToken, Is.EqualTo(cancellation.Token));
            }
            Assert.That(File.Exists(archive) || Directory.Exists(archive) || File.Exists(archive + ".meta"), Is.False);
            CollectionAssert.AreEquivalent(files.Keys, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
            foreach (var (path, bytes) in files)
            {
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
            }
        }

        [Test]
        public void EditorOnlyAssetCannotBeSilentlyOmittedFromAnExplicitOfficialBuild()
        {
            var path = _root + "/EditorOnly.asset";
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            AssetDatabase.CreateAsset(recipe, path);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var group = _settings.DefaultGroup;
            var entry = _settings.CreateOrMoveEntry(guid, group);
            Assert.That(entry, Is.Not.Null, "The official settings must actually contain the selected Editor-only asset.");
            entry.SetAddress("demo/editor-only");
            Assert.That(entry.MainAssetType, Is.EqualTo(typeof(ResourceBuildRecipe)));
            SaveSettings();
            var bytes = File.ReadAllBytes(path);
            var meta = File.ReadAllBytes(path + ".meta");
            var archive = Path.Combine(Path.GetTempPath(), "kilo", "YLUnwrittenEditorOnlyState-" + Guid.NewGuid().ToString("N") + ".bin");
            var error = Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.BuildContent(_settings, archive));
            Assert.That(error.Message, Does.Contain("Editor-only").And.Contain(path));
            Assert.That(File.Exists(archive), Is.False);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
        }

        [Test]
        public void FormalBuildAndAuthoringUseOneEditorAssemblyWithoutLegacyExecutors()
        {
            const string package = "Packages/com.kano.ylframework/Editor/Systems/Resource/";
            foreach (var file in new[] { "ResourceEditorOperations.cs", "ResourceAddressablesAuthoring.cs", "Build/ResourcePathValidation.cs",
                "Build/ResourceAtlasGenerator.cs", "CodeGeneration/ResourceCodeGenerator.cs", "CodeGeneration/ResourceReferenceOutput.cs",
                "Workbench/ResourceWorkbenchAssetsMenu.cs", "Workbench/ResourceWorkbenchWindow.cs" })
            {
                var asset = package + file;
                Assert.That(File.Exists(PackageTestPaths.Physical(asset)) && File.Exists(PackageTestPaths.Physical(asset + ".meta")), Is.True, file);
                Assert.That(Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(asset)), Is.EqualTo("YLFramework.Editor"), file);
            }
            var assembly = typeof(ResourceEditorOperations).Assembly;
            foreach (string type in new[] { "BuildScript", "AssetBundleExporter", "ResourceBuildMap", "ResourceBundleBuilder", "ResourceCatalogBuilder", "ResourcePublisher", "ResourceEditorSimulation" })
            {
                Assert.That(assembly.GetType("YLFramework.Editor.Resource." + type), Is.Null, type);
            }
            Assert.That(typeof(ResourceEditorOperations).GetMethods().Select(method => method.Name), Does.Not.Contain("ExecuteRecipe"));
        }

        [Test]
        [Category("YL.Resource.Addressables.ContentBuild")]
        public void FullBuildArchivesTheActualOfficialContentState()
        {
            Add("content", "demo/text/content");
            SaveSettings();
            var path = Path.Combine(Path.GetTempPath(), "kilo", "YLContentState-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var report = ResourceEditorOperations.BuildContent(_settings, path);

                Assert.That(report.IsContentUpdate, Is.False);
                Assert.That(report.Target, Is.EqualTo(EditorUserBuildSettings.activeBuildTarget));
                Assert.That(report.ProfileId, Is.EqualTo(_settings.activeProfileId));
                Assert.That(report.Result.Error, Is.Null.Or.Empty);
                Assert.That(File.Exists(report.Result.OutputPath), Is.True);
                Assert.That(File.Exists(report.ContentStatePath), Is.True);
                Assert.That(new FileInfo(report.ContentStatePath).Length, Is.GreaterThan(0));
                CollectionAssert.AreEqual(File.ReadAllBytes(report.Result.ContentStateFilePath), File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ConstantsUseExactOfficialAddressesAndLabelsAndReflectEdits()
        {
            var entry = Add("message", "demo/text/message");
            _settings.AddLabel("preload-ui");
            entry.SetLabel("preload-ui", true);

            var source = ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames");

            StringAssert.Contains("\"demo/text/message\"", source);
            StringAssert.Contains("\"preload-ui\"", source);
            StringAssert.DoesNotContain("class Groups", source);
            entry.SetAddress("demo/text/renamed");
            var changed = ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames");
            StringAssert.Contains("\"demo/text/renamed\"", changed);
            StringAssert.DoesNotContain("\"demo/text/message\"", changed);
        }

        [Test]
        public void ConstantsRejectIdentifierCollisionsInsteadOfDroppingAnEntry()
        {
            Add("first", "demo/a-b");
            Add("second", "demo/a_b");
            Assert.Throws<InvalidOperationException>(() => ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames"));
        }

        [Test]
        [Category("YL.Resource.Addressables.ContentBuild")]
        [Category(PackedContract)]
        public void PublishedKeysAgreeWithTheOfficialPackedCatalog()
        {
            _settings.BuildRemoteCatalog = false;
            _settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>().InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath;
            var main = Add("main", "packed/main");
            var peer = Add("peer", "packed/peer");
            _settings.AddLabel("shared-pack");
            _settings.AddLabel("unassigned-pack");
            main.SetLabel("shared-pack", true);
            peer.SetLabel("shared-pack", true);
            main.SetLabel(main.address, true, true); // The same asset may use a key in two roles.

            var guidOnly = Group("GuidOnly");
            guidOnly.GetSchema<BundledAssetGroupSchema>().IncludeAddressInCatalog = false;
            var hiddenAddress = Add("guid-only", "packed/hidden-address", guidOnly);
            var addressOnly = Group("NoGuid");
            addressOnly.GetSchema<BundledAssetGroupSchema>().IncludeGUIDInCatalog = false;
            var hiddenGuid = Add("no-guid", "packed/no-guid", addressOnly);
            var noLabels = Group("NoLabels");
            noLabels.GetSchema<BundledAssetGroupSchema>().IncludeLabelsInCatalog = false;
            var hiddenLabel = Add("no-label", "packed/no-label", noLabels);
            hiddenLabel.SetLabel("excluded-label", true, true);

            var excluded = Group("Excluded");
            excluded.GetSchema<BundledAssetGroupSchema>().IncludeInBuild = false;
            Add("duplicate-excluded", main.address, excluded);
            var excludedEntry = Add("excluded", "packed/excluded", excluded);
            var noSchema = _settings.CreateGroup("NoSchema", false, false, false, null);
            Add("no-schema", "packed/no-schema", noSchema);

            AssetDatabase.CreateFolder(_root, "Folder");
            var childPath = _root + "/Folder/child.txt";
            File.WriteAllText(childPath, "packed folder content");
            AssetDatabase.ImportAsset(childPath, ImportAssetOptions.ForceSynchronousImport);
            var folder = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(_root + "/Folder"), _settings.DefaultGroup);
            folder.SetAddress("packed/folder");
            folder.SetLabel("folder-pack", true, true);

            var typed = AddPrefab("packed/prefab");
            var prefabPath = typed.AssetPath;
            const string mixedLabel = "mixed-types";
            main.SetLabel(mixedLabel, true, true);
            typed.SetLabel(mixedLabel, true);
            SaveSettings();
            var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var authoringAssets = _settings.groups.Where(group => group).SelectMany(group => group.Schemas.Cast<UnityEngine.Object>().Append(group))
                .Append(_settings).Where(asset => asset).ToArray();
            var dirtyBefore = authoringAssets.Select(EditorUtility.IsDirty).ToArray();

            Assert.That(ResourceAddressablesAuthoring.Validate(_settings), Is.Empty);
            var source = ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames");
            foreach (var key in new[] { main.address, peer.address, hiddenGuid.address, hiddenLabel.address, typed.address,
                "packed/folder/child.txt", "shared-pack", "folder-pack", mixedLabel })
            {
                StringAssert.Contains("\"" + key + "\"", source);
            }
            foreach (var key in new[] { hiddenAddress.address, excludedEntry.address, "packed/no-schema", folder.address,
                "excluded-label", "unassigned-pack", hiddenAddress.guid })
            {
                StringAssert.DoesNotContain("\"" + key + "\"", source);
            }
            CollectionAssert.AreEqual(dirtyBefore, authoringAssets.Select(EditorUtility.IsDirty).ToArray());
            CollectionAssert.AreEquivalent(before.Keys, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
            foreach (var (path, bytes) in before)
            {
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
            }

            var report = ResourceEditorOperations.BuildContent(_settings, _root + "/packed-state.bin");
            WithPackedLocator(report.Result, locator =>
            {
                AssertLocations(locator, main.address, typeof(TextAsset), main.AssetPath);
                AssertLocations(locator, typed.address, typeof(GameObject), prefabPath);
                AssertLocations(locator, mixedLabel, typeof(UnityEngine.Object), main.AssetPath, prefabPath);
                AssertLocations(locator, mixedLabel, typeof(GameObject), prefabPath);
                AssertLocations(locator, mixedLabel, typeof(TextAsset), main.AssetPath);
                AssertLocations(locator, peer.address, typeof(TextAsset), peer.AssetPath);
                AssertLocations(locator, "shared-pack", typeof(TextAsset), main.AssetPath, peer.AssetPath);
                AssertLocations(locator, "folder-pack", typeof(TextAsset), childPath);
                AssertLocations(locator, "packed/folder/child.txt", typeof(TextAsset), childPath);
                AssertLocations(locator, new AssetReference(hiddenAddress.guid).RuntimeKey, typeof(TextAsset), hiddenAddress.AssetPath);
                AssertLocations(locator, hiddenGuid.address, typeof(TextAsset), hiddenGuid.AssetPath);
                AssertLocations(locator, hiddenLabel.address, typeof(TextAsset), hiddenLabel.AssetPath);
                foreach (var absent in new[] { hiddenAddress.address, hiddenGuid.guid, "excluded-label", "unassigned-pack",
                    excludedEntry.address, excludedEntry.guid, "packed/no-schema", folder.address })
                {
                    AssertLocations(locator, absent, typeof(TextAsset));
                }
            });
        }

        [Test]
        [Category(PackedContract)]
        public void SingleSpritePngPublishesTypedLocationsForTheSameAddressAndGuid()
        {
            const string address = "packed/single-sprite";
            var path = _root + "/single.png";
            var pixels = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                pixels.SetPixels32(Enumerable.Repeat(new Color32(40, 120, 200, 255), 64).ToArray());
                File.WriteAllBytes(path, pixels.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(pixels);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var entry = _settings.CreateOrMoveEntry(guid, _settings.DefaultGroup);
            entry.SetAddress(address);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid).And.Not.Empty);
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Count(), Is.EqualTo(1));
            _settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>().InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath;
            SaveSettings();

            WithPackedLocator(BuildOfficialOracle(), locator =>
            {
                var guidKey = new AssetReference(guid).RuntimeKey;
                var byAddress = QuerySpriteLocations(locator, address);
                var byGuid = QuerySpriteLocations(locator, guidKey);
                AssertSpriteLocations(address, byAddress);
                AssertSpriteLocations(guidKey, byGuid);
                CollectionAssert.AreEquivalent(byAddress.broad.Select(LocationDescription), byGuid.broad.Select(LocationDescription));
            });

            Assert.That(ResourceAddressablesAuthoring.Validate(_settings), Is.Empty);
            StringAssert.Contains("\"" + address + "\"", ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames"));
            Assert.DoesNotThrow(() => ResourceEditorOperations.ValidateBuild(_settings));
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [Category(PackedContract)]
        public void OnlyEmittedBuiltInResourcesKeysCanConflictWithBundles(bool includeResources, bool overlap)
        {
            var version = PackedPackageVersion();
            var legacy = version == "1.25.0";
            var builtIn = _settings.groups.SingleOrDefault(group => group && group.GetSchema<PlayerDataGroupSchema>());
            if (legacy)
            {
                Assert.That(builtIn, Is.Not.Null);
                Assert.That(builtIn.Name, Is.EqualTo("Built In Data"));
                CollectionAssert.AreEquivalent(new[] { "Resources", "EditorSceneList" }, builtIn.entries.Select(entry => entry.guid));
                var schema = builtIn.GetSchema<PlayerDataGroupSchema>();
                schema.IncludeResourcesFolders = includeResources;
                schema.IncludeBuildSettingsScenes = false;
            }
            else
            {
                Assert.That(builtIn, Is.Null, "2.10.3 does not create the legacy pseudo-group.");
            }

            AssetDatabase.CreateFolder(_root, "Resources");
            var prefix = Path.GetFileName(_root) + "-";
            var addressKey = prefix + "x";
            var aliasKey = prefix + "alias";
            var builtinOnly = prefix + "builtin-only";
            var builtinAddressPath = WriteText("Resources/" + addressKey, "built-in address");
            var builtinAliasPath = WriteText("Resources/" + aliasKey, "built-in alias");
            WriteText("Resources/" + builtinOnly, "only built-in");
            var bundleAddress = Add("bundle-address", overlap ? addressKey : "packed/non-colliding");
            var bundleAlias = Add("bundle-alias", "packed/alias-source");
            _settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>().InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath;
            var shared = prefix + "shared-label";
            bundleAddress.SetLabel(shared, true, true);
            bundleAlias.SetLabel(shared, true);
            if (overlap)
            {
                bundleAlias.SetLabel(aliasKey, true, true);
            }

            var noKeys = Group("No emitted keys");
            var noKeysSchema = noKeys.GetSchema<BundledAssetGroupSchema>();
            noKeysSchema.IncludeAddressInCatalog = false;
            noKeysSchema.IncludeGUIDInCatalog = false;
            noKeysSchema.IncludeLabelsInCatalog = false;
            AssetDatabase.CreateFolder(_root, "Unpublished");
            var unpublishedPath = WriteText("Unpublished/child", "expanded but unpublished");
            var unpublished = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(_root + "/Unpublished"), noKeys);
            // Without its disabled address key, this expanded child cannot shadow the Resources child.
            var folderKey = prefix + "folder";
            unpublished.SetAddress(folderKey);
            AssetDatabase.CreateFolder(_root + "/Resources", folderKey);
            WriteText("Resources/" + folderKey + "/child.txt", "built-in child with .txt in its Resources key");
            SaveSettings();

            var emitsResources = legacy && includeResources;
            WithPackedLocator(BuildOfficialOracle(), locator =>
            {
                CheckTextKey(locator, addressKey, emitsResources, overlap ? bundleAddress : null);
                CheckTextKey(locator, aliasKey, emitsResources, overlap ? bundleAlias : null);
                CheckTextKey(locator, builtinOnly, emitsResources);
                CheckTextKey(locator, folderKey + "/child.txt", emitsResources);
                AssertLocations(locator, AssetDatabase.AssetPathToGUID(unpublishedPath), typeof(TextAsset));
                AssertLocations(locator, shared, typeof(TextAsset), bundleAddress.AssetPath, bundleAlias.AssetPath);
            });

            if (emitsResources && overlap)
            {
                var diagnostics = string.Join("\n", ResourceAddressablesAuthoring.Validate(_settings));
                var generation = CaptureInvalidOperation(() => ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames"));
                var build = CaptureInvalidOperation(() => ResourceEditorOperations.ValidateBuild(_settings));
                var details = new[] { addressKey, aliasKey, builtIn.Name, bundleAddress.parentGroup.Name,
                    builtinAddressPath, builtinAliasPath, bundleAddress.AssetPath, bundleAlias.AssetPath };
                var missing = new List<string>();
                foreach (var (operation, message) in new[] { ("Validate", diagnostics), ("GenerateSource", generation), ("ValidateBuild", build) })
                {
                    missing.AddRange(details.Where(detail => !message.Contains(detail)).Select(detail => $"{operation}: missing '{detail}'"));
                }
                Assert.That(missing, Is.Empty, $"Validate: {diagnostics}\nGenerateSource: {generation}\nValidateBuild: {build}");
            }
            else
            {
                Assert.That(ResourceAddressablesAuthoring.Validate(_settings), Is.Empty);
                var source = ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames");
                StringAssert.Contains("\"" + bundleAddress.address + "\"", source);
                StringAssert.Contains("\"" + shared + "\"", source);
                StringAssert.DoesNotContain("\"" + builtinOnly + "\"", source);
                StringAssert.DoesNotContain("\"" + folderKey + "/child.txt\"", source);
                Assert.DoesNotThrow(() => ResourceEditorOperations.ValidateBuild(_settings));
            }
        }

        [Flags]
        public enum SharedSceneKeys
        {
            None = 0,
            BundledScene = 1,
            BundledText = 2,
            Both = BundledScene | BundledText
        }

        private static IEnumerable<TestCaseData> SceneKeyCases()
        {
            yield return new TestCaseData(true, SharedSceneKeys.None);
            yield return new TestCaseData(true, SharedSceneKeys.BundledScene);
            yield return new TestCaseData(true, SharedSceneKeys.BundledText);
            yield return new TestCaseData(true, SharedSceneKeys.Both);
            yield return new TestCaseData(false, SharedSceneKeys.Both);
        }

        [TestCaseSource(nameof(SceneKeyCases))]
        [Category(PackedContract)]
        public void ScenePublicationPreflightRejectsOnlyAnEmittedSameTypeCollision(bool includeScenes, SharedSceneKeys shared)
        {
            WithSceneKeys(includeScenes, shared, assets =>
            {
                var errors = ResourceAddressablesAuthoring.Validate(_settings);
                if (assets.EmitsBuiltInScenes && shared.HasFlag(SharedSceneKeys.BundledScene))
                {
                    var generation = CaptureInvalidOperation(() => ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames"));
                    var build = CaptureInvalidOperation(() => ResourceEditorOperations.ValidateBuild(_settings));
                    Assert.That(errors, Has.Length.EqualTo(1), string.Join("\n", errors));
                    foreach (var (operation, message) in new[] { ("Validate", errors[0]), ("GenerateSource", generation), ("ValidateBuild", build) })
                    {
                        Debug.Log($"[PackedContract] {operation}: {message}");
                        foreach (var detail in new[] { assets.SceneKey, nameof(SceneInstance), "Built In Data", assets.Bundled.parentGroup.Name,
                            assets.ScenePath, AssetDatabase.AssetPathToGUID(assets.ScenePath), assets.Bundled.AssetPath, assets.Bundled.guid })
                        {
                            Assert.That(message, Does.Contain(detail), operation);
                        }
                        Assert.That(message, Does.Not.Contain(assets.CrossKey).And.Not.Contain(assets.DisabledKey).And.Not.Contain(SceneLabel), operation);
                    }
                }
                else
                {
                    Assert.That(errors, Is.Empty);
                    var source = ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames");
                    foreach (var key in new[] { assets.Bundled.address, assets.Text.address, SceneLabel })
                    {
                        StringAssert.Contains("\"" + key + "\"", source);
                    }
                    StringAssert.DoesNotContain("\"" + assets.DisabledKey + "\"", source);
                    Assert.DoesNotThrow(() => ResourceEditorOperations.ValidateBuild(_settings));
                }
            });
        }

        [TestCaseSource(nameof(SceneKeyCases))]
        [Category(PackedContract)]
        public void OfficialPackedSceneKeysResolveSceneInstances(bool includeScenes, SharedSceneKeys shared)
        {
            WithSceneKeys(includeScenes, shared, assets => WithPackedLocator(BuildOfficialOracle(), locator =>
            {
                var sceneIds = new List<string>();
                if (shared.HasFlag(SharedSceneKeys.BundledScene))
                {
                    sceneIds.Add(assets.Bundled.AssetPath);
                }
                if (assets.EmitsBuiltInScenes)
                {
                    sceneIds.Add(BuiltInSceneId(assets.ScenePath));
                }
                AssertSceneKeyLocations(locator, assets.SceneKey, typeof(SceneInstance), sceneIds.ToArray());
                AssertSceneKeyLocations(locator, assets.CrossKey, typeof(SceneInstance), assets.EmitsBuiltInScenes ? new[] { BuiltInSceneId(assets.CrossPath) } : Array.Empty<string>());
                if (!shared.HasFlag(SharedSceneKeys.BundledScene))
                {
                    AssertSceneKeyLocations(locator, assets.Bundled.address, typeof(SceneInstance), assets.Bundled.AssetPath);
                }
                AssertSceneKeyLocations(locator, assets.DisabledKey, typeof(SceneInstance));
                AssertSceneKeyLocations(locator, SceneLabel, typeof(SceneInstance), assets.Bundled.AssetPath);
            }));

            // Addressables 1.25 strips Assets/ and the .unity extension for Build Settings scene locations.
            static string BuiltInSceneId(string path) => path.Substring("Assets/".Length, path.Length - "Assets/".Length - ".unity".Length);
        }

        [TestCaseSource(nameof(SceneKeyCases))]
        [Category(PackedContract)]
        public void OfficialPackedSceneKeysResolveOrdinaryAssets(bool includeScenes, SharedSceneKeys shared)
        {
            WithSceneKeys(includeScenes, shared, assets => WithPackedLocator(BuildOfficialOracle(), locator =>
            {
                AssertSceneKeyLocations(locator, assets.Text.address, typeof(TextAsset), assets.Text.AssetPath);
                AssertSceneKeyLocations(locator, assets.CrossKey, typeof(UnityEngine.Object), shared.HasFlag(SharedSceneKeys.BundledText) ? new[] { assets.Text.AssetPath } : Array.Empty<string>());
                AssertSceneKeyLocations(locator, assets.SceneKey, typeof(UnityEngine.Object));
                AssertSceneKeyLocations(locator, SceneLabel, typeof(UnityEngine.Object), assets.Text.AssetPath, assets.Peer.AssetPath);
            }));
        }

        [Test]
        [Category(PackedContract)]
        public void InspectingAPreExpandedFolderDoesNotChangePersistentAuthoringInputs()
        {
            AssetDatabase.CreateFolder(_root, "Expanded");
            var alpha = WriteText("Expanded/alpha", "first");
            var beta = WriteText("Expanded/beta", "second");
            var group = _settings.DefaultGroup;
            var folder = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(_root + "/Expanded"), group);
            folder.SetAddress("packed/expanded");
            folder.SetLabel("folder-label", true, true);
            SaveSettings();
            var expansion = new List<AddressableAssetEntry>();
            folder.GatherAllAssets(expansion, true, true, false);
            Assert.That(folder.IsFolder, Is.True);
            CollectionAssert.AreEquivalent(new[] { alpha, beta }, expansion.Select(entry => entry.AssetPath));
            CollectionAssert.AreEqual(expansion, folder.SubAssets);
            var members = folder.SubAssets.ToArray();
            var memberData = members.Select(EntryMetadata).ToArray();
            var explicitEntries = group.entries.ToArray();
            var persistent = _settings.groups.Where(item => item).SelectMany(item => item.Schemas.Cast<UnityEngine.Object>().Append(item))
                .Append(_settings).ToArray();
            var json = persistent.Select(asset => EditorJsonUtility.ToJson(asset)).ToArray();
            var dirty = persistent.Select(EditorUtility.IsDirty).ToArray();
            var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

            Assert.That(ResourceAddressablesAuthoring.Validate(_settings), Is.Empty);
            var source = ResourceCodeGenerator.GenerateSource(_settings, "BuildTest.ResourceNames");
            StringAssert.Contains("\"packed/expanded/alpha.txt\"", source);
            StringAssert.Contains("\"packed/expanded/beta.txt\"", source);
            Assert.DoesNotThrow(() => ResourceEditorOperations.ValidateBuild(_settings));

            Assert.That(folder.IsFolder, Is.True);
            // The official cache may refresh; original members and the refreshed ordered projection must keep their metadata.
            Debug.Log($"[PackedContract] folder cache retained member references in order: " +
                $"{folder.SubAssets.Count == members.Length && folder.SubAssets.Zip(members, ReferenceEquals).All(same => same)}");
            CollectionAssert.AreEqual(memberData, members.Select(EntryMetadata));
            CollectionAssert.AreEqual(memberData, folder.SubAssets.Select(EntryMetadata));
            CollectionAssert.AreEqual(explicitEntries, group.entries);
            CollectionAssert.AreEqual(json, persistent.Select(asset => EditorJsonUtility.ToJson(asset)));
            CollectionAssert.AreEqual(dirty, persistent.Select(EditorUtility.IsDirty));
            CollectionAssert.AreEquivalent(files.Keys, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
            foreach (var (path, bytes) in files)
            {
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void AddressOrGuidCollidingWithAnotherAssetsLabelBlocksBroadAssetQueries(bool useGuid, bool differentType)
        {
            var first = Add("first", "packed/first");
            var other = differentType ? AddPrefab("packed/second") : Add("second", "packed/second");
            var key = useGuid ? first.guid : first.address;
            other.SetLabel(key, true, true);
            SaveSettings();
            AssertPublicationRejected(key, first.guid, other.guid);
        }

        [Test]
        public void AStaleRawEntryBlocksPublicationEvenWhenOfficialExpansionOmitsIt()
        {
            var guid = Guid.NewGuid().ToString("N");
            var stale = _settings.CreateOrMoveEntry(guid, _settings.DefaultGroup, false, false);
            stale.SetAddress("packed/stale");
            SaveSettings();
            Assert.That(_settings.DefaultGroup.entries, Does.Contain(stale));
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.Empty);
            AssertPublicationRejected(stale.address, guid, _settings.DefaultGroup.Name);
        }

        private static string PackedPackageVersion()
        {
            var version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AddressableAssetSettings).Assembly)?.version;
            if (version != "1.25.0" && version != "2.10.3")
            {
                Assert.Ignore("Packed contract is characterized only for Addressables 1.25.0 and 2.10.3; installed: " + version);
            }
            return version;
        }

        private AddressablesPlayerBuildResult BuildOfficialOracle()
        {
            Assert.That(AddressableAssetSettingsDefaultObject.Settings, Is.SameAs(_settings));
            Assert.That(_settings.ActivePlayerDataBuilder, Is.InstanceOf<UnityEditor.AddressableAssets.Build.DataBuilders.BuildScriptPackedMode>());
            // The reference artifact must exist even when YL correctly refuses this configuration.
            AddressableAssetSettings.BuildPlayerContent(out var result);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Error, Is.Null.Or.Empty);
            Assert.That(File.Exists(result.OutputPath), Is.True, result.OutputPath);
            return result;
        }

        private static void WithPackedLocator(AddressablesPlayerBuildResult result, Action<IResourceLocator> inspect)
        {
            var version = PackedPackageVersion();
#if ENABLE_BINARY_CATALOG
            const bool enableBinaryCatalog = true;
#else
            const bool enableBinaryCatalog = false;
#endif
#if ENABLE_JSON_CATALOG
            const bool enableJsonCatalog = true;
#else
            const bool enableJsonCatalog = false;
#endif
            var binary = version == "1.25.0" ? enableBinaryCatalog : !enableJsonCatalog;
            var runtime = JsonUtility.FromJson<ResourceManagerRuntimeData>(File.ReadAllText(result.OutputPath));
            var catalog = Path.Combine(Path.GetDirectoryName(result.OutputPath), Path.GetFileName(runtime.CatalogLocations.Single().InternalId));
            Debug.Log($"[PackedContract] Addressables {version}, ENABLE_BINARY_CATALOG={enableBinaryCatalog}, " +
                $"ENABLE_JSON_CATALOG={enableJsonCatalog}, runtime catalog={catalog}");
            Assert.That(Path.GetExtension(catalog), Is.EqualTo(binary ? ".bin" : ".json"));
            Assert.That(File.Exists(catalog), Is.True, catalog);
            Assert.That(File.ReadAllText(catalog).TrimStart().StartsWith("{", StringComparison.Ordinal), Is.EqualTo(!binary), catalog);
            using var manager = new ResourceManager();
            var provider = new ContentCatalogProvider(manager) { DisableCatalogUpdateOnStart = true };
            manager.ResourceProviders.Add(provider);
            manager.ResourceProviders.Add(new JsonAssetProvider());
            var load = manager.ProvideResource<ContentCatalogData>(new ResourceLocationBase(catalog, catalog, provider.ProviderId, typeof(ContentCatalogData)));
            try
            {
                var data = load.WaitForCompletion();
                Assert.That(load.Status, Is.EqualTo(AsyncOperationStatus.Succeeded), load.OperationException?.ToString());
                var create = typeof(ContentCatalogData).GetMethod("CreateCustomLocator", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(create, Is.Not.Null, "The official ContentCatalogData locator factory is missing.");
                var parameters = create.GetParameters();
                var signature = parameters.Select(parameter => parameter.ParameterType).ToArray();
                Assert.That(signature.SequenceEqual(new[] { typeof(string), typeof(string) }) ||
                    signature.SequenceEqual(new[] { typeof(string), typeof(string), typeof(int) }), Is.True,
                    "Unexpected locator factory signature: " + create);
                foreach (var parameter in parameters.Skip(1))
                {
                    Assert.That(parameter.HasDefaultValue, Is.True, $"Unexpected mandatory locator factory parameter: {parameter} in {create}");
                }
                var args = parameters.Select(_ => (object)Type.Missing).ToArray();
                args[0] = "PackedContract";
                inspect((IResourceLocator)create.Invoke(data, args));
            }
            finally
            {
                load.Release();
            }
        }

        private static (IResourceLocation[] texture, IResourceLocation[] sprite, IResourceLocation[] broad) QuerySpriteLocations(IResourceLocator locator, object key)
        {
            var texture = ReadLocations(locator, key, typeof(Texture2D));
            var sprite = ReadLocations(locator, key, typeof(Sprite));
            var broad = ReadLocations(locator, key, typeof(UnityEngine.Object));
            Debug.Log($"[PackedContract] single PNG key '{key}': Texture2D=[{string.Join(", ", texture.Select(LocationDescription))}], " +
                $"Sprite=[{string.Join(", ", sprite.Select(LocationDescription))}], Object=[{string.Join(", ", broad.Select(LocationDescription))}]");
            return (texture, sprite, broad);
        }

        private static void AssertSpriteLocations(object key, (IResourceLocation[] texture, IResourceLocation[] sprite, IResourceLocation[] broad) locations)
        {
            var (texture, sprite, broad) = locations;
            CollectionAssert.AreEqual(new[] { typeof(Texture2D) }, texture.Select(location => location.ResourceType), key.ToString());
            CollectionAssert.AreEqual(new[] { typeof(Sprite) }, sprite.Select(location => location.ResourceType), key.ToString());
            CollectionAssert.AreEquivalent(new[] { typeof(Texture2D), typeof(Sprite) }, broad.Select(location => location.ResourceType), key.ToString());
            Assert.That(broad, Has.Length.EqualTo(DistinctReferences(broad).Count()), key.ToString());
            CollectionAssert.AreEquivalent(texture.Concat(sprite).Select(LocationDescription), broad.Select(LocationDescription), key.ToString());
        }

        private static void CheckTextKey(IResourceLocator locator, string key, bool builtin, AddressableAssetEntry bundle = null)
        {
            const string builtinProvider = "UnityEngine.ResourceManagement.ResourceProviders.LegacyResourcesProvider";
            var locations = ReadLocations(locator, key, typeof(UnityEngine.Object));
            var typed = ReadLocations(locator, key, typeof(TextAsset));
            var expected = new List<(string, string)>();
            if (builtin)
            {
                expected.Add((builtinProvider, key));
            }
            if (bundle != null)
            {
                expected.Add((typeof(BundledAssetProvider).FullName, bundle.AssetPath));
            }
            Debug.Log($"[PackedContract] Resources key '{key}': Object=[{string.Join(", ", locations.Select(LocationDescription))}]");
            Assert.That(locations, Has.Length.EqualTo(expected.Count), key);
            Assert.That(locations, Has.Length.EqualTo(DistinctReferences(locations).Count()), key);
            Assert.That(locations.All(location => location.ResourceType == typeof(TextAsset)), Is.True, key);
            CollectionAssert.AreEquivalent(expected, locations.Select(location => (location.ProviderId, location.InternalId)), key);
            CollectionAssert.AreEquivalent(locations.Select(LocationDescription), typed.Select(LocationDescription), key);
        }

        private static IResourceLocation[] ReadLocations(IResourceLocator locator, object key, Type type) =>
            locator.Locate(key, type, out var locations) ? locations.ToArray() : Array.Empty<IResourceLocation>();

        private static IEnumerable<IResourceLocation> DistinctReferences(IList<IResourceLocation> locations) =>
            locations.Where((location, index) => !locations.Take(index).Any(other => ReferenceEquals(other, location)));

        private static string LocationDescription(IResourceLocation location) =>
            $"{location.ResourceType.FullName} | {location.ProviderId} | {location.InternalId}";

        private static void AssertSceneKeyLocations(IResourceLocator locator, string key, Type type, params string[] expectedIds)
        {
            var query = $"{type.Name} at '{key}'";
            IList<IResourceLocation> locations = null;
            var found = false;
            Assert.DoesNotThrow(() => found = locator.Locate(key, type, out locations), query);
            Assert.That(found, Is.EqualTo(expectedIds.Length != 0), query);
            if (!found)
            {
                return;
            }
            Assert.That(locations, Is.Not.Null, query);
            Debug.Log($"[PackedContract] {query}: [{string.Join(", ", locations.Select(LocationDescription))}]");
            Assert.That(locations.All(location => type.IsAssignableFrom(location.ResourceType)), Is.True, query);
            Assert.That(DistinctReferences(locations).Count(), Is.EqualTo(locations.Count), query);
            CollectionAssert.AreEquivalent(expectedIds, locations.Select(location => location.InternalId), query);
        }

        private static (string, string, string, string) EntryMetadata(AddressableAssetEntry entry) =>
            (entry.guid, entry.address, entry.AssetPath, string.Join(",", entry.labels.OrderBy(label => label, StringComparer.Ordinal)));

        private static string CaptureInvalidOperation(Action action)
        {
            try
            {
                action();
                return "<did not refuse>";
            }
            catch (InvalidOperationException error)
            {
                return error.Message;
            }
        }

        private void AssertPublicationRejected(params string[] details)
        {
            var archive = _root + "/unwritten.bin";
            var generated = _root + "/References";
            var errors = string.Join("\n", ResourceAddressablesAuthoring.Validate(_settings));
            var generation = Assert.Throws<InvalidOperationException>(() => ResourceCodeGenerator.GenerateReferences(_settings, generated));
            var build = Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.BuildContent(_settings, archive));
            foreach (var detail in details)
            {
                Assert.That(errors, Does.Contain(detail));
                Assert.That(generation.Message, Does.Contain(detail));
                Assert.That(build.Message, Does.Contain(detail));
            }
            Assert.That(Directory.Exists(generated) || File.Exists(archive), Is.False);
        }

        private static void AssertLocations(IResourceLocator locator, object key, Type type, params string[] paths)
        {
            var found = locator.Locate(key, type, out var locations);
            Assert.That(found, Is.EqualTo(paths.Length != 0), key.ToString());
            if (found)
            {
                Assert.That(locations.All(location => type.IsAssignableFrom(location.ResourceType)), Is.True);
                var distinct = DistinctReferences(locations);
                CollectionAssert.AreEquivalent(paths, distinct.Select(location => location.InternalId), key.ToString());
                if (paths.Length == 1)
                {
                    Assert.That(YLFramework.Resource.ResourceSystem.HasOneDistinctLocation(locations), Is.True, key.ToString());
                }
            }
        }

        private AddressableAssetGroup Group(string name)
        {
            var group = _settings.CreateGroup(name, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(_settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(_settings, AddressableAssetSettings.kLocalLoadPath);
            schema.InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath;
            return group;
        }

        private AddressableAssetEntry Add(string file, string address, AddressableAssetGroup group = null)
        {
            var path = WriteText(file, file);
            var entry = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group ?? _settings.DefaultGroup);
            entry.SetAddress(address);
            return entry;
        }

        private string WriteText(string file, string text)
        {
            var path = _root + "/" + file + ".txt";
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private sealed class SceneKeyAssets
        {
            public bool EmitsBuiltInScenes;
            public string ScenePath;
            public string CrossPath;
            public string DisabledPath;
            public AddressableAssetEntry Bundled;
            public AddressableAssetEntry Text;
            public AddressableAssetEntry Peer;
            public string SceneKey => Path.GetFileNameWithoutExtension(ScenePath);
            public string CrossKey => Path.GetFileNameWithoutExtension(CrossPath);
            public string DisabledKey => Path.GetFileNameWithoutExtension(DisabledPath);
        }

        private void WithSceneKeys(bool includeScenes, SharedSceneKeys shared, Action<SceneKeyAssets> inspect)
        {
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(scene => scene.isDirty),
                Is.Empty, "The packed scene test does not save or close pre-existing scenes.");
            var legacy = PackedPackageVersion() == "1.25.0";
            var builtinGroup = _settings.groups.SingleOrDefault(group => group && group.GetSchema<PlayerDataGroupSchema>());
            Assert.That(builtinGroup != null, Is.EqualTo(legacy));
            if (builtinGroup)
            {
                var schema = builtinGroup.GetSchema<PlayerDataGroupSchema>();
                schema.IncludeBuildSettingsScenes = includeScenes;
                schema.IncludeResourcesFolders = false;
            }

            var prefix = Path.GetFileName(_root) + "-";
            var assets = new SceneKeyAssets
            {
                EmitsBuiltInScenes = legacy && includeScenes,
                ScenePath = CopySceneFixture(prefix + "scene"),
                CrossPath = CopySceneFixture(prefix + "cross-domain"),
                DisabledPath = CopySceneFixture(prefix + "disabled-scene")
            };
            var bundledPath = CopySceneFixture("bundled-scene");
            assets.Bundled = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(bundledPath), Group("Packed scenes"));
            assets.Bundled.SetAddress(shared.HasFlag(SharedSceneKeys.BundledScene) ? assets.SceneKey : "packed/distinct-scene");
            assets.Text = Add("cross-domain", shared.HasFlag(SharedSceneKeys.BundledText) ? assets.CrossKey : "packed/distinct-text");
            assets.Peer = Add("label-peer", "packed/scene-label-peer");
            _settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>().InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath;
            assets.Bundled.SetLabel(SceneLabel, true, true);
            assets.Text.SetLabel(SceneLabel, true);
            assets.Peer.SetLabel(SceneLabel, true);

            var oldScenes = EditorBuildSettings.scenes;
            try
            {
                EditorBuildSettings.scenes = oldScenes.Append(new EditorBuildSettingsScene(assets.ScenePath, true))
                    .Append(new EditorBuildSettingsScene(assets.CrossPath, true)).Append(new EditorBuildSettingsScene(assets.DisabledPath, false)).ToArray();
                SaveSettings();
                inspect(assets);
            }
            finally
            {
                EditorBuildSettings.scenes = oldScenes;
            }
        }

        private string CopySceneFixture(string name)
        {
            const string template = "Assets/Tests/YLFramework/Fixtures/CommonSystem/Scenes/CommonNeutral.unity";
            var path = _root + "/" + name + ".unity";
            // A fresh batch host can have an untitled scene. Copy the asset without opening or saving any scene.
            Assert.That(AssetDatabase.CopyAsset(template, path), Is.True, path);
            return path;
        }

        private AddressableAssetEntry AddPrefab(string address)
        {
            var path = _root + "/OtherType.prefab";
            var prefab = new GameObject("other typed location");
            try
            {
                Assert.That(PrefabUtility.SaveAsPrefabAsset(prefab, path), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(prefab);
            }
            var entry = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), _settings.DefaultGroup);
            entry.SetAddress(address);
            return entry;
        }

        private void SaveSettings()
        {
            EditorUtility.SetDirty(_settings);
            AssetDatabase.SaveAssetIfDirty(_settings);
            foreach (var group in _settings.groups)
            {
                if (group)
                {
                    AssetDatabase.SaveAssetIfDirty(group);
                    foreach (var schema in group.Schemas.Where(schema => schema))
                    {
                        AssetDatabase.SaveAssetIfDirty(schema);
                    }
                }
            }
        }
    }
}
