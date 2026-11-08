using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;
using Object = UnityEngine.Object;
using YLFramework.Editor.DataAssets;

namespace YLFramework.Tests.Extensions.Data
{
    [TestFixture]
    [Category("YL.Module.Excel")]
    public class ExcelDataAssetsBindingTests
    {
        private ScriptableObjectConfigs _root;
        private string _folder;
        private string _workbook;
        private string _entries;
        private byte[] _original;
        [SetUp]
        public void SetUp()
        {
            string name = "YLExcelBinding-" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
            _root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            AssetDatabase.CreateAsset(_root, _folder + "/Root.asset");
            var sample = ScriptableObject.CreateInstance<ExcelBoundDataFixture>();
            try
            {
                var script = MonoScript.FromScriptableObject(sample);
                Assert.That(script, Is.Not.Null, "Fixture MonoScript must be genuinely imported by Unity.");
                ScriptableObjectConfigsController.SetDataType(_root, script);
                ScriptableObjectConfigsController.Save(_root);
            }
            finally
            {
                Object.DestroyImmediate(sample);
            }
            _entries = ScriptableObjectConfigsController.GetDataPath(_root);
            _original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Root.asset"));
            string output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp/Extensions.Data.Excel/Unity", name);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(output));
            _workbook = Path.Combine(output, "bound.xlsx");
            ExcelAuthoringTests.WriteWorkbook(_workbook, new[] { "id", "config_name", "power" }, new[] { "int", "string", "int" }, new object[] { 1, "bound data", 42 });
        }
        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_entries) && AssetDatabase.IsValidFolder(_entries))
            {
                AssetDatabase.DeleteAsset(_entries);
            }

            if (!string.IsNullOrEmpty(_folder))
            {
                AssetDatabase.DeleteAsset(_folder);
            }
        }
        private static object Adapter(string name, params object[] args)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Integrations.Excel.Editor.DataAssetsExcelCommands")).Single(t => t != null);
            try
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }
        [Test]
        public void BaseListUsesBoundDerivedFieldsAndCreatesOwnedFolderOnlyAfterConfirmation()
        {
            using (var plan = (IDisposable)Adapter("PreviewImport", _root, _workbook))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_entries)), Is.False);
                Assert.That(ExcelAuthoringTests.Commit(plan, false), Is.EqualTo("Cancelled"));
            }
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Root.asset")), Is.EqualTo(_original));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_entries)), Is.False);
            using (var plan = (IDisposable)Adapter("PreviewImport", _root, _workbook))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Applied"));
            }
            Assert.That(_root.configs, Has.Count.EqualTo(1));
            Assert.That(_root.configs[0], Is.TypeOf<ExcelBoundDataFixture>());
            Assert.That(_root.configs[0].configName, Is.EqualTo("bound data"));
            Assert.That(((ExcelBoundDataFixture)_root.configs[0]).power, Is.EqualTo(42));
            Assert.That(_root.configs[0].parent, Is.SameAs(_root));
            Assert.That(AssetDatabase.GetAssetPath(_root.configs[0]), Is.EqualTo(_entries + "/1.asset"));
            Assert.That(_root.GetScriptableObjectData(1), Is.SameAs(_root.configs[0]));
        }
        [Test]
        public void BuiltInDataColumnsKeepTheirExistingWorkbookNames()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("YLFramework.Integrations.Excel.Editor.ExcelValues")).Single(value => value != null);
            var columnName = type.GetMethod("ColumnName", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(columnName, Is.Not.Null);
            Assert.That(columnName.Invoke(null, new object[] { typeof(ScriptableObjectData).GetField("configName") }), Is.EqualTo("config_name"));
            Assert.That(columnName.Invoke(null, new object[] { typeof(ScriptableObjectData).GetField("description") }), Is.EqualTo("des"));
        }

        [Test]
        public void InvalidTypeScriptGuidProducesVisibleFailureAndLeavesRootUnchanged()
        {
            _root.typeScriptGuid = "ffffffffffffffffffffffffffffffff";
            ScriptableObjectConfigsController.Save(_root);
            byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Root.asset"));
            using (var plan = (IDisposable)Adapter("PreviewImport", _root, _workbook))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan).Any(e => e.Contains("typeScriptGuid")), Is.True);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(_root.configs, Is.Empty);
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Root.asset")), Is.EqualTo(before));
        }
        [Test]
        public void OptionalSelectedRootMenusAreRegisteredWithoutOrdinaryEditorReferenceToExcel()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Integrations.Excel.Editor.DataAssetsExcelCommands")).Single(t => t != null);
            var menus = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).SelectMany(m => m.GetCustomAttributes<MenuItem>()).Where(m => !m.validate).Select(m => m.menuItem).ToArray();
            Assert.That(menus, Is.EquivalentTo(new[] { "Assets/YLFramework/Excel/Preview Import Selected Data Root", "Assets/YLFramework/Excel/Export Selected Data Root" }));
            Assert.That(typeof(ScriptableObjectConfigsController).Assembly.GetReferencedAssemblies().Any(a => a.Name == "YLFramework.Excel.Editor" || a.Name == "MiniExcel"), Is.False);
        }
        [Test]
        public void ExplicitPreviewDoesNotPersistWorkbookPathInRuntimeRoot()
        {
            Assert.That(typeof(ScriptableObjectConfigs).GetField("excel_path"), Is.Null);
            byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Root.asset"));
            using (var plan = (IDisposable)Adapter("PreviewImport", _root, _workbook))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(ExcelAuthoringTests.Commit(plan, false), Is.EqualTo("Cancelled"));
            }
            Assert.That(EditorJsonUtility.ToJson(_root), Does.Not.Contain("excel_path"));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_folder + "/Root.asset")), Is.EqualTo(before));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_entries)), Is.False);
        }
        [Test]
        public void ExportRemembersWorkbookForRevealByAssetGuid()
        {
            string output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp/Extensions.Data.Excel/Unity",
                Path.GetFileName(_folder), "exported.xlsx");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(output)));
            try
            {
                Adapter("Export", _root, output);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), Is.True);
                Assert.That(DataAssetsWorkbookMemory.TryGetPath(_root, out var remembered), Is.True);
                Assert.That(remembered, Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(output))));
                Assert.That(DataAssetsWorkbookMemory.CanReveal(_root), Is.True);
                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_root));
                Assert.That(AssetDatabase.MoveAsset(AssetDatabase.GetAssetPath(_root), _folder + "/Renamed.asset"), Is.Empty);
                Assert.That(AssetDatabase.AssetPathToGUID(_folder + "/Renamed.asset"), Is.EqualTo(guid));
                Assert.That(DataAssetsWorkbookMemory.TryGetPath(_root, out var afterMove), Is.True);
                Assert.That(afterMove, Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(output))));
            }
            finally
            {
                DataAssetsWorkbookMemory.Forget(_root);
            }
        }
    }
}
