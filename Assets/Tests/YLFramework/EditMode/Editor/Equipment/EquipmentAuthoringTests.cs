using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Equipment;
using YLFramework.Editor.Equipment;

namespace YLFramework.Tests.Extensions.Equipment
{
    public class EquipmentAuthoringTests
    {
        private EquipmentsConfig _root;
        private string _folder;
        private string _entries;
        private string[] _absentParents = Array.Empty<string>();
        private static Type Controller => typeof(YLFramework.Editor.Common.SplitWindow).Assembly.GetType("YLFramework.Editor.Equipment.EquipmentsConfigController");
        private static object Call(string name, params object[] args)
        {
            Assert.That(Controller, Is.Not.Null, "YL equipment authoring has not been migrated.");
            var method = Controller.GetMethods(BindingFlags.Static | BindingFlags.Public).Single(m => m.Name == name && m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(args[i])).All(v => v));
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        [SetUp]
        public void SetUp()
        {
            _absentParents = new[] { "Assets/YLFrameworkData/Equipment", "Assets/YLFrameworkData" }
                .Where(path => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();
            Assert.That(Controller, Is.Not.Null, "YL equipment authoring has not been migrated.");
            var parent = "Assets/Tests/YLFramework/Fixtures/Extensions";
            _folder = parent + "/EqAuthor-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(parent, Path.GetFileName(_folder));
            _root = ScriptableObject.CreateInstance<EquipmentsConfig>();
            _root.startId = 1000;
            AssetDatabase.CreateAsset(_root, _folder + "/Root.asset");
            _entries = "Assets/YLFrameworkData/Equipment/" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_root));
            Assert.That(AssetDatabase.IsValidFolder(_entries), Is.False);
        }
        [TearDown]
        public void TearDown()
        {
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Console.WriteLine("Preserved failed equipment fixture: " + _folder + " | " + _entries);
                return;
            }

            if (_root)
            {
                _root.Equipments.Clear();
            }

            if (_entries != null && AssetDatabase.IsValidFolder(_entries))
            {
                AssetDatabase.DeleteAsset(_entries);
            }

            if (_folder != null)
            {
                AssetDatabase.DeleteAsset(_folder);
            }

            foreach (var path in _absentParents)
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Length == 0)
                {
                    Assert.That(AssetDatabase.DeleteAsset(path), Is.True, path);
                }
            }
        }

        [Test, Order(-1), Category("YL.Test.ParentCleanup")]
        public void TearDownReturnsNewEmptyEquipmentParents()
        {
            Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            TearDown();
            foreach (var path in _absentParents)
            {
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.False, path);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.False, path + ".meta");
            }
        }

        [Test]
        public void CreateAndCopyKeepDerivedFieldsWithoutSavingUnrelatedDirtyAsset()
        {
            var unrelated = ScriptableObject.CreateInstance<EquipmentDerivedData>();
            AssetDatabase.CreateAsset(unrelated, _folder + "/Unrelated.asset");
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Unrelated.asset"));
            unrelated.bonus = 90;
            EditorUtility.SetDirty(unrelated);
            var id = (int)Call("CreateEquipmentConfigData", typeof(EquipmentDerivedData), _root);
            Assert.That(id, Is.EqualTo(1001));
            var first = _root.GetEquipmentConfigData(id) as EquipmentDerivedData;
            Assert.That(first.bonus, Is.EqualTo(17));
            first.bonus = 33;
            var copyId = (int)Call("CopyEquipmentConfigData", first, _root);
            var copy = _root.GetEquipmentConfigData(copyId) as EquipmentDerivedData;
            Assert.That(copyId, Is.EqualTo(1002));
            Assert.That(copy.bonus, Is.EqualTo(33));
            Assert.That(copy.parent, Is.SameAs(_root));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(copy)), Is.Not.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(first))));
            Assert.That(EditorUtility.IsDirty(unrelated), Is.True);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Unrelated.asset")));
        }

        [Test]
        public void DeleteExternalReferenceDoesNotDeleteItsFileOrOtherSubassets()
        {
            var external = ScriptableObject.CreateInstance<EquipmentConfigData>();
            external.id = 1010;
            var marker = ScriptableObject.CreateInstance<EquipmentDerivedData>();
            var path = _folder + "/External.asset";
            AssetDatabase.CreateAsset(external, path);
            AssetDatabase.AddObjectToAsset(marker, external);
            AssetDatabase.SaveAssetIfDirty(external);
            _root.Equipments.Add(external);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            Call("DeleteEquipmentConfigData", external, _root);
            Assert.That(_root.Equipments, Is.Empty);
            Assert.That(external && marker, Is.True);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
        }

        [Test]
        public void ExplicitRefreshFindsNewRowsInNonEmptyOwnerFolder()
        {
            Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            var extra = ScriptableObject.CreateInstance<EquipmentConfigData>();
            extra.id = 1008;
            extra.parent = _root;
            AssetDatabase.CreateAsset(extra, _entries + "/Extra.asset");
            Assert.That(_root.Equipments.Count, Is.EqualTo(1));
            Call("RefreshEntries", _root);
            Assert.That(_root.GetEquipmentConfigData(1008), Is.SameAs(extra));
            Assert.That(_root.Equipments.Count, Is.EqualTo(2));
        }

        [Test]
        public void InvalidDataBindingIsDiagnosedAndIdEditUndoRefreshesLookup()
        {
            _root.typeScriptGuid = "invalid-guid";
            Assert.That(Assert.Throws<InvalidOperationException>(() => Call("GetDataType", _root)).Message, Does.Contain("invalid-guid"));
            _root.typeScriptGuid = "";
            Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            var data = _root.GetEquipmentConfigData(1001);
            Undo.IncrementCurrentGroup();
            using (var serialized = new SerializedObject(data))
            {
                serialized.FindProperty("id").intValue = 1005;
                serialized.ApplyModifiedProperties();
            }
            Undo.FlushUndoRecordObjects();
            Assert.That(_root.GetEquipmentConfigData(1005), Is.SameAs(data));
            Undo.PerformUndo();
            Assert.That(_root.GetEquipmentConfigData(1001), Is.SameAs(data));
            Assert.That(_root.GetEquipmentConfigData(1005), Is.Null);
        }

        [Test]
        public void WindowsOwnSeparateTreeAndSelectionState()
        {
            Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            var type = Controller.Assembly.GetType("YLFramework.Editor.Equipment.ConfigsWindow");
            var a = (EditorWindow)ScriptableObject.CreateInstance(type);
            var b = (EditorWindow)ScriptableObject.CreateInstance(type);
            try
            {
                type.GetMethod("SetCurrentConfigs").Invoke(a, new object[] { _root });
                type.GetMethod("SetCurrentConfigs").Invoke(b, new object[] { _root });
                type.GetMethod("SetSelection").Invoke(a, new object[] { 1001 });
                var field = type.GetField("EquipmentListTree", BindingFlags.NonPublic | BindingFlags.Instance);
                var first = field.GetValue(a);
                var second = field.GetValue(b);
                Assert.That(first, Is.Not.SameAs(second));
                Assert.That(first.GetType().GetProperty("Selected").GetValue(first), Is.SameAs(_root.GetEquipmentConfigData(1001)));
                Assert.That(second.GetType().GetProperty("Selected").GetValue(second), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void DeletingOwnedEntrySupportsUndoRedoWithoutLosingGuid()
        {
            var id = (int)Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            var row = _root.GetEquipmentConfigData(id);
            var path = AssetDatabase.GetAssetPath(row);
            var guid = AssetDatabase.AssetPathToGUID(path);
            Undo.IncrementCurrentGroup();
            Call("DeleteEquipmentConfigData", row, _root);
            Undo.FlushUndoRecordObjects();
            Assert.That(_root.Equipments, Is.Empty);
            Undo.PerformUndo();
            var restored = AssetDatabase.LoadAssetAtPath<EquipmentConfigData>(path);
            Assert.That(restored, Is.Not.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(_root.GetEquipmentConfigData(id), Is.SameAs(restored));
            Undo.PerformRedo();
            Assert.That(_root.Equipments, Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.False);
        }

        [Test]
        public void TreeSelectionFollowsObjectAcrossReorderAndUndo()
        {
            var id = (int)Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            var selected = _root.GetEquipmentConfigData(id);
            using var tree = new EquipmentListTree(new TreeViewState(), _root);
            tree.SelectEquipment(id);
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_root, "Reorder fixture");
            _root.Equipments.Reverse();
            EditorUtility.SetDirty(_root);
            Undo.FlushUndoRecordObjects();
            tree.Reload();
            Assert.That(tree.Selected, Is.SameAs(selected));
            Undo.PerformUndo();
            Assert.That(tree.Selected, Is.SameAs(selected));
        }

        [Test]
        public void OldDeletionDoesNotTouchReplacementFileDuringUnrelatedUndo()
        {
            var id = (int)Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            var row = _root.GetEquipmentConfigData(id);
            var path = AssetDatabase.GetAssetPath(row);
            Call("DeleteEquipmentConfigData", row, _root);
            var replacement = ScriptableObject.CreateInstance<EquipmentConfigData>();
            AssetDatabase.CreateAsset(replacement, path);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var unrelated = ScriptableObject.CreateInstance<EquipmentDerivedData>();
            try
            {
                Undo.IncrementCurrentGroup();
                Undo.RecordObject(unrelated, "Unrelated fixture edit");
                unrelated.bonus = 77;
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unrelated);
            }
        }

        [Test]
        public void SameObjectMetadataChangesRefreshTreeLabelsAndSearch()
        {
            var id = (int)Call("CreateEquipmentConfigData", typeof(EquipmentConfigData), _root);
            var row = _root.GetEquipmentConfigData(id);
            var window = ScriptableObject.CreateInstance<ConfigsWindow>();
            try
            {
                window.position = new Rect(80, 80, 900, 600);
                window.SetCurrentConfigs(_root);
                window.Show();
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                row.equipmentName = "UpdatedEquipmentMetadata";
                var tree = (EquipmentListTree)typeof(ConfigsWindow).GetField("EquipmentListTree", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                Assert.That(tree.GetRows().Any(item => item.displayName.Contains("UpdatedEquipmentMetadata")), Is.True);
                tree.searchString = "UpdatedEquipmentMetadata";
                Assert.That(tree.GetRows().Count, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }
    }
}
