#if UNITY_EDITOR && YL_DOMAIN_EXCEL_PACKAGE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Equipment;
using YLFramework.Editor.Equipment;
using YLFramework.Integrations.DomainExcel.Editor;
using YLFramework.Integrations.Excel.Editor;

namespace YLFramework.Domain.Excel.Tests
{
    public class EquipmentExcelTests
    {
        public sealed class ScriptForExcel : IEquipment
        {
            public IEquippable Equipper
            {
                get; set;
            }
            public EquipmentConfigData EquipmentData
            {
                get; set;
            }
            public void OnLoaded()
            {
            }
            public void OnUnloaded()
            {
            }
            public void OnEquip()
            {
            }
            public void OnUnequip()
            {
            }
        }
        private string folder, path, entries;
        private EquipmentsConfig root;
        private readonly List<EquipmentConfigData> transient = new();
        private static void Own(string value)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Tests.EquipmentAcceptance.EquipmentSettings")).FirstOrDefault(t => t != null);
            type?.GetMethod("OwnPath").Invoke(null, new object[] { value });
        }
        private static object Call(string method, params object[] args)
        {
            var target = typeof(DomainExcelCommands).GetMethods().SingleOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(args[i])).All(v => v));
            Assert.That(target, Is.Not.Null, "Equipment Excel entry point is missing: " + method);
            try
            {
                return target.Invoke(null, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        private ExcelImportPlan Preview() => (ExcelImportPlan)Call("PreviewImport", root, path);
        [SetUp]
        public void Setup()
        {
            folder = "Assets/EquipmentExcelFixture-" + Guid.NewGuid().ToString("N");
            Own(folder);
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            root = ScriptableObject.CreateInstance<EquipmentsConfig>();
            AssetDatabase.CreateAsset(root, folder + "/Root.asset");
            entries = EquipmentsConfigController.GetEquipmentConfigDataPath(root);
            Own(entries);
            path = Path.Combine(Path.GetTempPath(), "YL-Equipment-" + Guid.NewGuid().ToString("N") + ".xlsx");
        }
        [TearDown]
        public void Cleanup()
        {
            EquipmentExcelSaveFailure.Blocked = null;
            if (root)
                EditorPrefs.DeleteKey("YLFramework.Equipment.Excel." + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(root)));
            if (AssetDatabase.IsValidFolder(folder))
                AssetDatabase.DeleteAsset(folder);
            if (AssetDatabase.IsValidFolder(entries))
                AssetDatabase.DeleteAsset(entries);
            foreach (var value in transient)
                if (value)
                    UnityEngine.Object.DestroyImmediate(value);
            transient.Clear();
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(path));
        }
        private EquipmentExcelDerivedData Row(int id, EquipmentType mode)
        {
            var data = ScriptableObject.CreateInstance<EquipmentExcelDerivedData>();
            transient.Add(data);
            data.id = id;
            data.equipmentName = "Equipment " + id;
            data.equipmentType = mode;
            data.bonus = id;
            data.projectName = "equipment-project";
            data.assetName = "prefab";
            data.icon.projectName = "icons";
            data.icon.iconName = "equipment.png";
            if (mode == EquipmentType.Script)
                data.equipmentClassFullName = typeof(ScriptForExcel).FullName;
            return data;
        }
        [Test]
        public void RealWorkbookPreservesAssetScriptDerivedFieldsAndCancellation()
        {
            var guid = AssetDatabase.FindAssets("EquipmentExcelDerivedData t:MonoScript").First(g => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(g)).GetClass() == typeof(EquipmentExcelDerivedData));
            root.typeScriptGuid = guid;
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
            var asset = Row(1001, EquipmentType.Asset);
            var script = Row(1002, EquipmentType.Script);
            ExcelAuthoring.ScriptableObjectToExcel(new[] { asset, script }, path, "equipments", typeof(EquipmentExcelDerivedData));
            using (var cancel = Preview())
            {
                Assert.That(cancel.Errors, Is.Empty);
                Assert.That(cancel.Commit(false), Is.EqualTo(ExcelCommitResult.Cancelled));
                Assert.That(root.Equipments, Is.Empty);
                Assert.That(AssetDatabase.IsValidFolder(entries), Is.False);
            }
            using (var plan = Preview())
            {
                Assert.That(plan.Errors, Is.Empty);
                Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            }
            Assert.That(root.Equipments.Count, Is.EqualTo(2));
            Assert.That(root.Equipments[0], Is.TypeOf<EquipmentExcelDerivedData>());
            Assert.That(((EquipmentExcelDerivedData)root.Equipments[0]).bonus, Is.EqualTo(1001));
            Assert.That(root.Equipments[0].equipmentType, Is.EqualTo(EquipmentType.Asset));
            Assert.That(root.Equipments[1].equipmentClassFullName, Is.EqualTo(typeof(ScriptForExcel).FullName));
            Assert.That(root.Equipments[0].icon.iconName, Is.EqualTo("equipment.png"));
            Assert.That(root.Equipments.All(d => d.parent == root && AssetDatabase.GetAssetPath(d).StartsWith(entries + "/")), Is.True);
        }
        [Test]
        public void InvalidClassAndDuplicateIdsNeverChangeAssets()
        {
            var data = Row(1001, EquipmentType.Script);
            data.equipmentClassFullName = "Missing.Equipment";
            ExcelAuthoring.ScriptableObjectToExcel(new[] { data }, path, "equipments");
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(root)));
            Assert.Throws<InvalidOperationException>(() => Preview());
            data.equipmentClassFullName = typeof(ScriptForExcel).FullName;
            // 正式SO导出器本身拒绝重复ID；用平表作者API模拟外部用户写入的坏工作簿。
            ExcelAuthoring.ListToExcel<EquipmentConfigData>(new[] { data, data }, path, "equipments");
            using var duplicate = Preview();
            Assert.That(duplicate.Errors, Is.Not.Empty);
            Assert.That(duplicate.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
            Assert.That(string.Join("\n", duplicate.Errors), Does.Contain("Duplicate ID: 1001"));
            Assert.That(root.Equipments, Is.Empty);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(AssetDatabase.GetAssetPath(root))));
        }
        [Test]
        public void WorkbookOrTargetMutationAfterPreviewCannotPartiallyApply()
        {
            var row = Row(1001, EquipmentType.Asset);
            ExcelAuthoring.ScriptableObjectToExcel(new[] { row }, path, "equipments");
            using (var seed = Preview())
                Assert.That(seed.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            row.equipmentName = "changed";
            ExcelAuthoring.ScriptableObjectToExcel(new[] { row }, path, "equipments");
            using (var stale = Preview())
            {
                row.equipmentName = "new workbook";
                ExcelAuthoring.ScriptableObjectToExcel(new[] { row }, path, "equipments");
                Assert.That(stale.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
                Assert.That(root.Equipments[0].equipmentName, Is.EqualTo("Equipment 1001"));
            }
            using (var targetChanged = Preview())
            {
                root.Equipments[0].equipmentName = "author edit";
                EditorUtility.SetDirty(root.Equipments[0]);
                Assert.That(targetChanged.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
                Assert.That(root.Equipments[0].equipmentName, Is.EqualTo("author edit"));
            }
        }
        [Test]
        public void WorkbookMemoryUsesRootGuidAndNeverSerializesMachinePath()
        {
            Call("ExportEquipment", root, path);
            Assert.That(Call("GetEquipmentWorkbook", root), Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path))));
            Assert.That(AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(root), "Renamed"), Is.Empty);
            Assert.That(Call("GetEquipmentWorkbook", root), Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path))));
            Assert.That(EditorJsonUtility.ToJson(root), Does.Not.Contain(path));
            using var cancel = Preview();
            cancel.Commit(false);
            Assert.That(Call("GetEquipmentWorkbook", root), Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path))));
        }

        [Test]
        public void EmptyWorkbookDeletesOnlyOwnedEntriesAfterConfirmation()
        {
            var row = Row(1001, EquipmentType.Asset);
            ExcelAuthoring.ScriptableObjectToExcel(new[] { row }, path, "equipments");
            using (var seed = Preview())
                Assert.That(seed.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            var saved = AssetDatabase.GetAssetPath(root.Equipments[0]);
            ExcelAuthoring.ScriptableObjectToExcel(Array.Empty<EquipmentConfigData>(), path, "equipments");
            using (var cancel = Preview())
            {
                Assert.That(cancel.Commit(false), Is.EqualTo(ExcelCommitResult.Cancelled));
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(saved)), Is.True);
            }
            using (var empty = Preview())
                Assert.That(empty.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            Assert.That(root.Equipments, Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(saved)), Is.False);
        }

        [Test]
        public void InvalidModeAndAssetAddressAreRejectedBeforeWriting()
        {
            var row = Row(1001, (EquipmentType)99);
            ExcelAuthoring.ScriptableObjectToExcel(new[] { row }, path, "equipments");
            using (var invalid = Preview())
            {
                Assert.That(invalid.Errors, Is.Not.Empty);
                Assert.That(invalid.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
            }
            row.equipmentType = EquipmentType.Asset;
            row.projectName = "";
            ExcelAuthoring.ScriptableObjectToExcel(new[] { row }, path, "equipments");
            Assert.Throws<InvalidOperationException>(() => Preview());
            Assert.That(root.Equipments, Is.Empty);
            Assert.That(AssetDatabase.IsValidFolder(entries), Is.False);
        }

        [Test]
        public void RealSaveFailureRollsBackAlreadyWrittenSiblingAndRuntimeMemory()
        {
            var a = Row(1001, EquipmentType.Asset);
            var b = Row(1002, EquipmentType.Asset);
            ExcelAuthoring.ScriptableObjectToExcel(new[] { a, b }, path, "equipments");
            using (var seed = Preview())
                Assert.That(seed.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            var files = root.Equipments.Select(AssetDatabase.GetAssetPath).Append(AssetDatabase.GetAssetPath(root)).ToDictionary(p => p, File.ReadAllBytes);
            a.equipmentName = b.equipmentName = "replacement";
            ExcelAuthoring.ScriptableObjectToExcel(new[] { a, b }, path, "equipments");
            using var plan = Preview();
            Assert.That(plan.Errors, Is.Empty);
            EquipmentExcelSaveFailure.Blocked = AssetDatabase.GetAssetPath(root.Equipments[0]);
            try
            {
                Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
                Assert.That(string.Join("\n", plan.Errors), Does.Contain("Unity did not save all imported assets"));
            }
            finally
            {
                EquipmentExcelSaveFailure.Blocked = null;
            }
            foreach (var file in files)
                CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file.Key)));
            Assert.That(root.Equipments[0].equipmentName, Is.EqualTo("Equipment 1001"));
            Assert.That(root.Equipments[1].equipmentName, Is.EqualTo("Equipment 1002"));
            Assert.That(root.GetEquipmentConfigData(1001), Is.SameAs(root.Equipments[0]));
        }

        [Test]
        public void UpdatingScriptClassAtomicallyRebindsGuidAndSurvivesRefresh()
        {
            root.startId = 1000;
            var id = EquipmentsConfigController.CreateEquipmentConfigData(typeof(EquipmentExcelDerivedData), root);
            var old = root.GetEquipmentConfigData(id);
            var a = typeof(EquipmentExcelScriptA);
            var b = typeof(EquipmentExcelScriptB);
            var aGuid = YLFramework.Editor.Equipment.ScriptBindController.GetScriptGuid(a);
            var bGuid = YLFramework.Editor.Equipment.ScriptBindController.GetScriptGuid(b);
            Assert.That(aGuid, Is.Not.Empty);
            Assert.That(bGuid, Is.Not.Empty);
            old.equipmentType = EquipmentType.Script;
            old.equipmentClassFullName = a.FullName;
            old.equipmentClassGuid = aGuid;
            EditorUtility.SetDirty(old);
            AssetDatabase.SaveAssetIfDirty(old);
            var imported = Row(id, EquipmentType.Script);
            imported.equipmentClassFullName = b.FullName;
            ExcelAuthoring.ScriptableObjectToExcel(new[] { imported }, path, "equipments");
            using (var plan = Preview())
                Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            Assert.That(root.Equipments[0].equipmentClassFullName, Is.EqualTo(b.FullName));
            Assert.That(root.Equipments[0].equipmentClassGuid, Is.EqualTo(bGuid));
            YLFramework.Editor.Equipment.ScriptBindController.RefreshBindings();
            Assert.That(root.Equipments[0].equipmentClassFullName, Is.EqualTo(b.FullName));
        }

        [Test]
        public void CrossRootIdConflictIsRejectedAtPreviewAndBeforeCommit()
        {
            var other = ScriptableObject.CreateInstance<EquipmentsConfig>();
            AssetDatabase.CreateAsset(other, folder + "/OtherRoot.asset");
            var row = Row(1001, EquipmentType.Asset);
            ExcelAuthoring.ScriptableObjectToExcel(new[] { row }, path, "equipments");
            using (var staged = Preview())
            {
                var occupied = ScriptableObject.CreateInstance<EquipmentConfigData>();
                occupied.id = 1001;
                occupied.parent = other;
                AssetDatabase.AddObjectToAsset(occupied, other);
                other.Equipments.Add(occupied);
                EditorUtility.SetDirty(other);
                AssetDatabase.SaveAssetIfDirty(other);
                Assert.That(staged.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
                Assert.That(root.Equipments, Is.Empty);
            }
            Assert.Throws<InvalidOperationException>(() => Preview());
            Assert.That(other.Equipments.Count, Is.EqualTo(1));
        }
    }

    // 仅测试程序集：实际拒绝保存一个自有文件，覆盖事务中途失败而非伪造 Commit 结果。
    internal sealed class EquipmentExcelSaveFailure : AssetModificationProcessor
    {
        internal static string Blocked;
        private static string[] OnWillSaveAssets(string[] paths) => Blocked == null ? paths : paths.Where(p => p != Blocked).ToArray();
    }
}
#endif
