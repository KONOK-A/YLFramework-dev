using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;
using YLFramework.Integrations.Excel.Editor;

namespace YLFramework.Tests
{
    [Category("YL.Module.Excel")]
    public class ExcelPackageBoundaryTests
    {
        [Test]
        public void WorkbookPreferencesBelongToExcelRatherThanTheOrdinaryDataAssetsInspector()
        {
            Assert.That(typeof(DataAssetsWorkbookMemory).Assembly.GetName().Name, Is.EqualTo("YLFramework.Excel.Editor"));
            Assert.That(typeof(ScriptableObjectConfigsInspector).Assembly.GetName().Name, Is.EqualTo("YLFramework.DataAssets.Editor"));
            Assert.That(typeof(ScriptableObjectConfigsInspector).Assembly.GetType(typeof(DataAssetsWorkbookMemory).FullName), Is.Null);
        }

        [Test]
        public void GenericBackendHasNoGameplayReferenceAndTheAdapterFollowsActualPackagePresence()
        {
            Assert.That(typeof(ExcelAuthoring).Assembly.GetReferencedAssemblies().Any(reference =>
                new[] { "YLFramework.Skill", "YLFramework.Buff", "YLFramework.Mission", "YLFramework.Equipment", "YLFramework.Gameplay.Editor" }.Contains(reference.Name)), Is.False);
            bool installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(package => package.name == "com.kano.ylframework.gameplay");
            var adapter = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly => assembly.GetName().Name == "YLFramework.Domain.Excel.Editor");
            Assert.That(adapter != null, Is.EqualTo(installed), "Do not force a missing optional package or retain a removed adapter assembly.");
        }

        [Test]
        public void WorkbookPanelRestoresGuidPreferencesWithoutModifyingTheConfiguration()
        {
            string name = "__YLExcelPanel_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + name;
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
            string folderGuid = AssetDatabase.CreateFolder("Assets", name);
            var root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            DataAssetsWorkbookWindow window = null;
            string path = Path.Combine(Path.GetTempPath(), "kilo", name + ".xlsx");
            try
            {
                AssetDatabase.CreateAsset(root, folder + "/Root.asset");
                AssetDatabase.SaveAssetIfDirty(root);
                var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/Root.asset"));
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.WriteByte(0);
                }
                Assert.That(DataAssetsWorkbookMemory.Remember(root, path), Is.True);
                window = ScriptableObject.CreateInstance<DataAssetsWorkbookWindow>();
                window.SetRoot(root);
                var state = new SerializedObject(window);
                Assert.That(state.FindProperty("_path").stringValue, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(EditorUtility.IsDirty(root), Is.False);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/Root.asset")));
                window.SetRoot(null);
                state.Update();
                Assert.That(state.FindProperty("_path").stringValue, Is.Empty);
                Assert.That(DataAssetsWorkbookMemory.TryGetPath(root, out var remembered), Is.True);
                Assert.That(remembered, Is.EqualTo(Path.GetFullPath(path)), "Changing the panel selection must not erase a root preference.");
            }
            finally
            {
                if (window)
                    UnityEngine.Object.DestroyImmediate(window);
                if (root)
                    DataAssetsWorkbookMemory.Forget(root);
                if (File.Exists(path))
                    File.Delete(path);
                Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(folderGuid));
                Assert.That(AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath),
                    Is.EquivalentTo(new[] { folder + "/Root.asset" }), "Never delete an unexpected asset added to the test-owned folder.");
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
