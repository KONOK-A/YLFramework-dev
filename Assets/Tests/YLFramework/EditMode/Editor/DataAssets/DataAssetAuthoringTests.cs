using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.DataAssets;
using Object = UnityEngine.Object;
using YLFramework.Editor.DataAssets;

namespace YLFramework.Tests.DataAssets
{
    public class DataAssetAuthoringTests
    {
        private string _folder;
        private string _childFolder;
        private ScriptableObjectConfigs _root;
        private string[] _absentParents = Array.Empty<string>();

        [SetUp]
        public void SetUp()
        {
            CaptureAbsentParents("Assets/YLFrameworkData/DataAssets", "Assets/YLFrameworkData");
            _folder = "Assets/__YLDataAssets_" + Guid.NewGuid().ToString("N");
            DataAcceptanceOwnership.Own(_folder);
            AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));
            _root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            AssetDatabase.CreateAsset(_root, _folder + "/Items.asset");
            _childFolder = ScriptableObjectConfigsController.GetDataPath(_root);
            DataAcceptanceOwnership.Own(_childFolder);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root)
            {
                foreach (var item in _root.configs)
                {
                    if (item)
                    {
                        Undo.ClearUndo(item);
                    }
                }

                Undo.ClearUndo(_root);
            }
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Console.WriteLine("Preserved failed DataAssets fixture: " + _folder + " | " + _childFolder);
                return;
            }
            AssetDatabase.DeleteAsset(_folder);
            if (AssetDatabase.IsValidFolder(_childFolder))
            {
                AssetDatabase.DeleteAsset(_childFolder);
            }

            foreach (var path in _absentParents)
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Length == 0)
                {
                    Assert.That(AssetDatabase.DeleteAsset(path), Is.True, path);
                }
            }
        }

        private void CaptureAbsentParents(params string[] paths) => _absentParents = paths
            .Where(path => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [Category("YL.Test.ParentCleanup")]
        public void TearDownRemovesOnlyNewEmptyParentDirectories(bool existedBefore, bool containsForeignFile)
        {
            var scope = "Assets/__YLParentCleanup_" + Guid.NewGuid().ToString("N");
            var parents = new[] { scope + "/Parent/Leaf", scope + "/Parent" };
            var originalParents = _absentParents;
            var passed = false;
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(scope)), Is.Not.Empty);
            void CreateParents()
            {
                AssetDatabase.CreateFolder(scope, "Parent");
                AssetDatabase.CreateFolder(parents[1], "Leaf");
            }

            try
            {
                if (existedBefore)
                {
                    CreateParents();
                }

                CaptureAbsentParents(parents);
                if (!existedBefore)
                {
                    CreateParents();
                }

                var guids = parents.Select(AssetDatabase.AssetPathToGUID).ToArray();
                var metadata = parents.Select(path => File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();
                var foreignFile = parents[0] + "/Keep.txt";
                if (containsForeignFile)
                {
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(foreignFile), "preserve foreign content");
                    AssetDatabase.ImportAsset(foreignFile, ImportAssetOptions.ForceSynchronousImport);
                }

                TearDown();
                var shouldRemain = existedBefore || containsForeignFile;
                for (var index = 0; index < parents.Length; index++)
                {
                    var path = parents[index];
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(shouldRemain), path);
                    Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo(shouldRemain), path + ".meta");
                    if (shouldRemain)
                    {
                        Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guids[index]));
                        CollectionAssert.AreEqual(metadata[index], File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
                    }
                }

                if (containsForeignFile)
                {
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(foreignFile)), Is.EqualTo("preserve foreign content"));
                }

                passed = true;
            }
            finally
            {
                _absentParents = originalParents;
                if (passed)
                {
                    AssetDatabase.DeleteAsset(scope);
                }
                else
                {
                    Console.WriteLine("Preserved failed parent cleanup fixture: " + scope);
                }
            }
        }

        [Test]
        public void EmptyRangeMissingSlotsAndSameCountIdMutationDoNotUseStaleCache()
        {
            Assert.That(_root.MinId, Is.Zero);
            Assert.That(_root.MaxId, Is.Zero);
            var item = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            Assert.That(_root.GetScriptableObjectData(1), Is.SameAs(item));
            item.id = 17;
            Assert.That(_root.GetScriptableObjectData(1), Is.Null);
            Assert.That(_root.GetScriptableObjectData(17), Is.SameAs(item));
            Assert.That(_root.MinId, Is.EqualTo(17));
            _root.configs[0] = null;
            Assert.That(_root.TryGetIdRange(out _, out _), Is.False);
        }

        [Test]
        public void DuplicateFailureCannotPublishPartialCacheAndRepairRestoresQueries()
        {
            var first = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var second = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            second.id = first.id;
            Assert.Throws<InvalidOperationException>(() => _root.GetScriptableObjectData(first.id));
            Assert.Throws<InvalidOperationException>(() => _root.GetScriptableObjectData(first.id));
            second.id = 8;
            Assert.That(_root.GetScriptableObjectData(8), Is.SameAs(second));
            Assert.That(_root.MaxId, Is.EqualTo(8));
        }

        [Test]
        public void SameCountAndSameIdReferenceReplacementReturnsCurrentObject()
        {
            var first = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var second = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            _root.configs.RemoveAt(1);
            Assert.That(_root.GetScriptableObjectData(1), Is.SameAs(first));
            second.id = 1;
            _root.configs[0] = second;
            Assert.That(_root.GetScriptableObjectData(1), Is.SameAs(second));
        }

        [Test]
        public void CreateCopyAndSaveReopenPreserveConcreteDataParentAndTypeGuid()
        {
            var temporary = ScriptableObject.CreateInstance<DataAssetTestData>();
            var script = MonoScript.FromScriptableObject(temporary);
            Object.DestroyImmediate(temporary);
            ScriptableObjectConfigsController.SetDataType(_root, script);
            var guid = _root.typeScriptGuid;
            var first = (DataAssetTestData)ScriptableObjectConfigsController.CreateData(typeof(DataAssetTestData), _root);
            first.amount = 23;
            first.configName = "Potion";
            first.icon.projectName = "items";
            first.icon.iconName = "icons/potion";
            var copy = (DataAssetTestData)ScriptableObjectConfigsController.CopyData(_root, first);
            Assert.That(copy.amount, Is.EqualTo(23));
            Assert.That(copy.id, Is.EqualTo(2));
            Assert.That(copy.configName, Is.EqualTo("Potion (1)"));
            Assert.That(copy.parent, Is.SameAs(_root));
            Assert.That(_root.GetScriptableObjectData("Potion (1)"), Is.SameAs(copy));
            Assert.That(_root.GetIcon(copy.id).TryGetAddress(out var address), Is.True);
            Assert.That(address.ToString(), Is.EqualTo("items:icons/potion"));
            ScriptableObjectConfigsController.Save(_root);
            AssetDatabase.ImportAsset(_folder + "/Items.asset", ImportAssetOptions.ForceUpdate);
            var reopened = AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(_folder + "/Items.asset");
            Assert.That(reopened.typeScriptGuid, Is.EqualTo(guid));
            Assert.That(reopened.GetDataType(), Is.EqualTo(typeof(DataAssetTestData)));
            Assert.That(((DataAssetTestData)reopened.GetScriptableObjectData(2)).amount, Is.EqualTo(23));
            Assert.That(reopened.GetScriptableObjectData(2).parent, Is.SameAs(reopened));
            Assert.Throws<ArgumentException>(() => ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), reopened));
        }

        [Test]
        public void InvalidTypeBindingIsReportedRatherThanDowngradedToBaseData()
        {
            _root.typeScriptGuid = "ffffffffffffffffffffffffffffffff";
            Assert.That(ScriptableObjectConfigsController.Validate(_root).Any(x => x.Message.Contains("typeScriptGuid")), Is.True);
            Assert.Throws<InvalidOperationException>(() => ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root));
        }

        [Test]
        public void RenameRejectsDuplicateBeforeMutationAndSupportsUndo()
        {
            var first = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var second = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            Assert.Throws<InvalidOperationException>(() => ScriptableObjectConfigsController.RenameData(_root, second, first.id, "bad"));
            Assert.That(second.id, Is.EqualTo(2));
            ScriptableObjectConfigsController.RenameData(_root, second, 9, "Elixir");
            Assert.That(_root.GetName(9), Is.EqualTo("Elixir"));
            Undo.PerformUndo();
            Assert.That(_root.GetScriptableObjectData(9), Is.Null);
            Assert.That(_root.GetScriptableObjectData(2), Is.SameAs(second));
        }

        [Test]
        public void StableSortPreservesMissingSlotsDuplicateReferencesAndUndo()
        {
            var first = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var second = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            _root.configs = new System.Collections.Generic.List<ScriptableObjectData> { second, null, first, first };
            ScriptableObjectConfigsController.Sort(_root, DataAssetSort.Id, true);
            Assert.That(_root.configs, Is.EqualTo(new[] { first, first, second, null }));
            Undo.PerformUndo();
            Assert.That(_root.configs, Is.EqualTo(new[] { second, null, first, first }));
        }

        [Test]
        public void DeleteCancelAndForeignReferencesDoNotDeleteAnyAsset()
        {
            var owned = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var external = ScriptableObject.CreateInstance<ScriptableObjectData>();
            AssetDatabase.CreateAsset(external, _folder + "/External.asset");
            _root.configs.Add(external);
            Assert.That(ScriptableObjectConfigsController.DeleteData(_root, owned, false), Is.False);
            Assert.Throws<InvalidOperationException>(() => ScriptableObjectConfigsController.DeleteData(_root, external, true));
            Assert.That(AssetDatabase.LoadAssetAtPath<ScriptableObjectData>(_folder + "/External.asset"), Is.SameAs(external));
            Assert.That(_root.configs.Count, Is.EqualTo(2));
            ScriptableObjectConfigsController.RemoveReference(_root, 1, external);
            Assert.That(_root.configs.Count, Is.EqualTo(1));
            Undo.PerformUndo();
            Assert.That(_root.configs[1], Is.SameAs(external));
        }

        [Test]
        public void DeleteOwnedChildAndRootNeverCascadeIntoExternalAssets()
        {
            var item = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var path = AssetDatabase.GetAssetPath(item);
            Assert.That(ScriptableObjectConfigsController.DeleteData(_root, item, true), Is.True);
            Assert.That(_root, Is.Not.Null);
            Assert.That(_root.configs, Is.Empty);
            Assert.That(AssetDatabase.LoadAssetAtPath<ScriptableObjectData>(path), Is.Null);
            var external = ScriptableObject.CreateInstance<ScriptableObjectData>();
            AssetDatabase.CreateAsset(external, _folder + "/External.asset");
            _root.configs.Add(external);
            Assert.That(ScriptableObjectConfigsController.DeleteRoot(_root, false), Is.False);
            Assert.That(ScriptableObjectConfigsController.DeleteRoot(_root, true), Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<ScriptableObjectData>(_folder + "/External.asset"), Is.SameAs(external));
        }

        [Test]
        public void SubAssetDeletionOnlyDestroysChildAndCanUndo()
        {
            var child = ScriptableObject.CreateInstance<ScriptableObjectData>();
            child.parent = _root;
            child.id = 5;
            AssetDatabase.AddObjectToAsset(child, _root);
            _root.configs.Add(child);
            Assert.That(ScriptableObjectConfigsController.IsOwned(_root, child), Is.True, "Immediate sub-assets must be editable before their first save.");
            Assert.That(ScriptableObjectConfigsController.DeleteData(_root, child, true), Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<ScriptableObjectConfigs>(_folder + "/Items.asset"), Is.SameAs(_root));
            Undo.PerformUndo();
            Assert.That(_root.configs.Count, Is.EqualTo(1));
            Assert.That(_root.configs[0] && _root.configs[0].id == 5, Is.True);
        }

        [Test]
        public void DiagnosticsLocateMissingAndDuplicateAndDisappearAfterRepair()
        {
            var first = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var second = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            second.id = first.id;
            _root.configs.Add(null);
            var errors = ScriptableObjectConfigsController.Validate(_root);
            Assert.That(errors.Any(x => x.Row == 1 && x.Asset == second && x.Message.Contains("duplicate")), Is.True);
            Assert.That(errors.Any(x => x.Row == 2 && x.Message.Contains("Missing")), Is.True);
            second.id = 8;
            ScriptableObjectConfigsController.RemoveReference(_root, 2, null);
            Assert.That(ScriptableObjectConfigsController.Validate(_root), Is.Empty);
        }

        [Test]
        public void DuplicateBusinessIdsAndMissingRowsKeepDistinctSelectableTreeRows()
        {
            var first = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var second = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            second.id = first.id;
            _root.configs.Add(null);
            using var tree = new ScriptableObjectListTree(_root, new TreeViewState());
            Assert.That(tree.GetRows().Select(row => row.id), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(tree.GetData(1), Is.SameAs(first));
            Assert.That(tree.GetData(2), Is.SameAs(second));
            Assert.That(tree.GetData(3), Is.Null);
        }

        [Test]
        public void RefreshAfterAnotherWindowReordersKeepsSelectionOnActualObject()
        {
            var first = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var second = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            using var tree = new ScriptableObjectListTree(_root, new TreeViewState());
            tree.SetSelection(new[] { 2 });
            ScriptableObjectData selected = null;
            tree.Selected = value => selected = value;
            _root.configs = new System.Collections.Generic.List<ScriptableObjectData> { second, first };
            tree.RefreshKeepingSelection();
            Assert.That(tree.GetSelection(), Is.EqualTo(new[] { 1 }));
            Assert.That(selected, Is.SameAs(second));
        }

        [Test]
        public void MovingRootPreservesChildDirectoryAndOwnershipByGuid()
        {
            var item = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var before = ScriptableObjectConfigsController.GetDataPath(_root);
            Assert.That(AssetDatabase.MoveAsset(_folder + "/Items.asset", _folder + "/Moved.asset"), Is.Empty);
            Assert.That(ScriptableObjectConfigsController.GetDataPath(_root), Is.EqualTo(before));
            Assert.That(ScriptableObjectConfigsController.IsOwned(_root, item), Is.True);
        }

        [Test]
        public void RootDeleteRejectsUnownedSubAssetsBeforeDeletingOwnedChildren()
        {
            var owned = ScriptableObjectConfigsController.CreateData(typeof(ScriptableObjectData), _root);
            var path = AssetDatabase.GetAssetPath(owned);
            var unrelated = ScriptableObject.CreateInstance<ScriptableObjectData>();
            AssetDatabase.AddObjectToAsset(unrelated, _root);
            Assert.Throws<InvalidOperationException>(() => ScriptableObjectConfigsController.DeleteRoot(_root, true));
            Assert.That(AssetDatabase.LoadAssetAtPath<ScriptableObjectData>(path), Is.SameAs(owned));
            Assert.That(_root && unrelated, Is.True);
        }
    }
}
