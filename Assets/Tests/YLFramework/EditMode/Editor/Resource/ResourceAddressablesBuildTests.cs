using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Testing;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Resource.Addressables.Build")]
    public sealed class ResourceAddressablesBuildTests
    {
        private string _root;
        private AddressableAssetSettings _settings;
        private AddressableAssetSettings _previous;

        [SetUp]
        public void SetUp()
        {
            var folder = "YLAddressablesBuild-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder);
            _root = "Assets/" + folder;
            _previous = AddressableAssetSettingsDefaultObject.Settings;
            AssetDatabase.CreateFolder(_root, "Configuration");
            _settings = AddressableAssetSettings.Create(_root + "/Configuration", "Settings", true, true);
            _settings.DisableCatalogUpdateOnStartup = true;
            AddressableAssetSettingsDefaultObject.Settings = _settings;
            SaveSettings();
        }

        [TearDown]
        public void TearDown()
        {
            AddressableAssetSettingsDefaultObject.Settings = _previous;
            AssetDatabase.DeleteAsset(_root);
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
            AddressableAssetSettingsDefaultObject.Settings = _previous;
            Assert.Throws<InvalidOperationException>(() => ResourceEditorOperations.ValidateBuild(_settings));
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

        private AddressableAssetEntry Add(string file, string address)
        {
            var path = _root + "/" + file + ".txt";
            File.WriteAllText(path, file);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
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
                }
            }
        }
    }
}
