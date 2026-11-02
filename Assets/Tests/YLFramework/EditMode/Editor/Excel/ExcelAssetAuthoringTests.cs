using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Excel;
using AssetRow = YLFramework.Tests.Extensions.Data.ExcelAssetRowFixture;
using Object = UnityEngine.Object;
using Owner = YLFramework.Tests.Extensions.Data.ExcelAssetOwnerFixture;

namespace YLFramework.Tests.Extensions.Data
{
    /// <summary>必须在主Unity运行；离线runner仅编译本类，不执行AssetDatabase或GUI。</summary>
    [TestFixture]
    [Category("YL.Module.Excel")]
    public class ExcelAssetAuthoringTests
    {
        private string _assetFolder;
        private string _diskFolder;
        private Owner _owner;
        public sealed class ReferenceRow
        {
            [ExcelScriptableObjectId] public int Id;
            public Object Reference;
        }
        [SetUp]
        public void SetUp()
        {
            string name = "YLExcel-" + Guid.NewGuid().ToString("N");
            _assetFolder = "Assets/" + name;
            YLFramework.Tests.DataAssets.DataAcceptanceOwnership.Own(_assetFolder);
            AssetDatabase.CreateFolder("Assets", name);
            _diskFolder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp/Extensions.Data.Excel/Unity", name);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_diskFolder));
            _owner = ScriptableObject.CreateInstance<Owner>();
            AssetDatabase.CreateAsset(_owner, _assetFolder + "/Owner.asset");
            Add(1, "old");
            Add(9, "remove");
            EditorUtility.SetDirty(_owner);
            AssetDatabase.SaveAssetIfDirty(_owner);
        }
        [TearDown]
        public void TearDown()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w.GetType().FullName == "YLFramework.Integrations.Excel.Editor.ExcelPreviewWindow" && w.titleContent.text == WindowTitle))
            {
                window.Close();
            }

            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Console.WriteLine("Preserved failed Excel asset fixture: " + _assetFolder + " | " + _diskFolder);
                return;
            }
            if (!string.IsNullOrEmpty(_assetFolder))
            {
                AssetDatabase.DeleteAsset(_assetFolder);
            }
        }
        private const string WindowTitle = "YLExcel-Test-Preview";
        private AssetRow Add(int id, string text)
        {
            var row = ScriptableObject.CreateInstance<AssetRow>();
            row.Id = id;
            row.Text = text;
            row.name = "Row-" + id;
            row.Parent = _owner;
            AssetDatabase.AddObjectToAsset(row, _owner);
            _owner.rows.Add(row);
            return row;
        }
        private static void Export(List<AssetRow> rows, string path) => ExcelAuthoringTests.Call("ScriptableObjectToExcel", typeof(AssetRow), rows, path, "Sheet1");
        private object Preview(string path, string folder = null, Action<AssetRow> initialize = null) => ExcelAuthoringTests.Call("PreviewScriptableObjects", typeof(AssetRow), path, _owner.rows, _owner, folder, null, initialize);
        private byte[] OwnerBytes() => File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_assetFolder + "/Owner.asset"));
        private string WriteNewWorkbook()
        {
            string path = Path.Combine(_diskFolder, "data.xlsx");
            var changed = Object.Instantiate(_owner.rows[0]);
            var added = ScriptableObject.CreateInstance<AssetRow>();
            try
            {
                changed.Text = "updated";
                changed.State = ExcelAssetState.On;
                changed.Numbers = new List<int> { 2, 5 };
                changed.Reference = _owner.rows[0];
                added.Id = 2;
                added.Text = "added";
                Export(new List<AssetRow> { changed, added }, path);
            }
            finally
            {
                Object.DestroyImmediate(changed);
                Object.DestroyImmediate(added);
            }
            return path;
        }

        [Test]
        public void PreviewAndCancelPreserveAssetBytesAndReferences()
        {
            string path = WriteNewWorkbook();
            byte[] before = OwnerBytes();
            var rows = _owner.rows.ToArray();
            using (var plan = (IDisposable)Preview(path))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(OwnerBytes(), Is.EqualTo(before));
                Assert.That(ExcelAuthoringTests.Commit(plan, false), Is.EqualTo("Cancelled"));
            }
            Assert.That(OwnerBytes(), Is.EqualTo(before));
            Assert.That(_owner.rows, Is.EqualTo(rows));
            Assert.That(_owner.rows[0].Text, Is.EqualTo("old"));
        }
        [Test]
        public void ConfirmUpdatesInPlaceAndSavesNewSubassetAndTypedGuidReference()
        {
            // 参照资产不参与待删除列表，验证GUID:localFileID而非同GUID主资产回退。
            var reference = ScriptableObject.CreateInstance<AssetRow>();
            reference.Id = 42;
            AssetDatabase.AddObjectToAsset(reference, _owner);
            EditorUtility.SetDirty(_owner);
            AssetDatabase.SaveAssetIfDirty(_owner);
            var old = _owner.rows[0];
            var changed = Object.Instantiate(old);
            var added = ScriptableObject.CreateInstance<AssetRow>();
            string path = Path.Combine(_diskFolder, "confirmed.xlsx");
            try
            {
                changed.Text = "updated";
                changed.State = ExcelAssetState.On;
                changed.Reference = reference;
                changed.Numbers = new List<int> { 2, 5 };
                added.Id = 2;
                Export(new List<AssetRow> { changed, added }, path);
            }
            finally
            {
                Object.DestroyImmediate(changed);
                Object.DestroyImmediate(added);
            }
            using (var plan = (IDisposable)Preview(path, initialize: row => row.Parent = _owner))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Applied"));
            }
            Assert.That(_owner.rows[0], Is.SameAs(old));
            Assert.That(old.Text, Is.EqualTo("updated"));
            Assert.That(old.name, Is.EqualTo("Row-1"));
            Assert.That(old.State, Is.EqualTo(ExcelAssetState.On));
            Assert.That(old.Numbers, Is.EqualTo(new[] { 2, 5 }));
            Assert.That(old.Reference, Is.SameAs(reference));
            Assert.That(_owner.rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(_owner.rows.All(r => r.Parent == _owner), Is.True);
            AssetDatabase.ImportAsset(_assetFolder + "/Owner.asset", ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<Owner>(_assetFolder + "/Owner.asset");
            Assert.That(reloaded.rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(reloaded.rows[0].Reference, Is.SameAs(reference));
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(_assetFolder + "/Owner.asset").OfType<AssetRow>().Select(r => r.Id), Is.EquivalentTo(new[] { 1, 2, 42 }));
        }
        [Test]
        public void MissingGuidAndUnavailableTypeBlockAllAssetChanges()
        {
            string path = Path.Combine(_diskFolder, "invalid-guid.xlsx");
            ExcelAuthoringTests.WriteWorkbook(path, new[] { "Id", "Reference" }, new[] { "int", "objectreference" }, new object[] { 1, "ffffffffffffffffffffffffffffffff" });
            byte[] before = OwnerBytes();
            using (var plan = (IDisposable)Preview(path))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan).Any(e => e.Contains("B4") && e.Contains("GUID")), Is.True);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
            }
            string unavailable = Path.Combine(_diskFolder, "unavailable-type.xlsx");
            ExcelAuthoringTests.WriteWorkbook(unavailable, new[] { "DATA_TYPE_FULL_CLASS_NAME", "Id" }, new[] { "string", "int" }, new object[] { "Missing.Type", 1 });
            using (var plan = (IDisposable)Preview(unavailable))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan).Any(e => e.Contains("Missing.Type")), Is.True);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(OwnerBytes(), Is.EqualTo(before));
            Assert.That(_owner.rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 9 }));
        }

        [Test]
        public void LegacyBuffIdWorkbookUpdatesTheSameOwnedSubassetWithoutChangingItsIdentity()
        {
            var owner = ScriptableObject.CreateInstance<YLFramework.Buff.BuffsConfig>();
            var ownerPath = _assetFolder + "/Buffs.asset";
            AssetDatabase.CreateAsset(owner, ownerPath);
            var row = ScriptableObject.CreateInstance<YLFramework.Buff.BuffConfigData>();
            row.id = 7;
            row.buffName = "previous";
            row.parent = owner;
            AssetDatabase.AddObjectToAsset(row, owner);
            owner.buffs.Add(row);
            EditorUtility.SetDirty(row);
            EditorUtility.SetDirty(owner);
            AssetDatabase.SaveAssetIfDirty(owner);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(row, out string guid, out long fileId), Is.True);
            var meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath + ".meta"));
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath));
            var path = Path.Combine(_diskFolder, "legacy-buff-id.xlsx");
            ExcelAuthoringTests.WriteWorkbook(path, new[] { "buff_id", "buff_name", "duration" },
                new[] { "int", "string", "float" }, new object[] { 7, "legacy updated", 2.5f });
            using (var plan = (IDisposable)ExcelAuthoringTests.Call("PreviewScriptableObjects",
                typeof(YLFramework.Buff.BuffConfigData), path, owner.buffs, owner, null, null,
                (Action<YLFramework.Buff.BuffConfigData>)(value => value.parent = owner)))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(row.buffName, Is.EqualTo("previous"));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath)));
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Applied"));
            }
            Assert.That(owner.buffs.Single(), Is.SameAs(row));
            Assert.That(row.id, Is.EqualTo(7));
            Assert.That(row.buffName, Is.EqualTo("legacy updated"));
            Assert.That(row.duration, Is.EqualTo(2.5f));
            Assert.That(row.parent, Is.SameAs(owner));
            AssetDatabase.ImportAsset(ownerPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<YLFramework.Buff.BuffsConfig>(ownerPath);
            Assert.That(reloaded.buffs.Single().buffName, Is.EqualTo("legacy updated"));
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reloaded.buffs.Single(),
                out string restoredGuid, out long restoredFileId), Is.True);
            Assert.That(restoredGuid, Is.EqualTo(guid));
            Assert.That(restoredFileId, Is.EqualTo(fileId));
            CollectionAssert.AreEqual(meta, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(ownerPath + ".meta")));
        }

        [Test]
        public void DomainInitializationFailureAndDirtyOwnerNeverWriteAssets()
        {
            string path = WriteNewWorkbook();
            byte[] before = OwnerBytes();
            using (var plan = (IDisposable)Preview(path, initialize: row => throw new InvalidOperationException("domain binding rejected")))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan).Any(e => e.Contains("domain binding rejected")), Is.True);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
            }
            _owner.rows[0].Text = "unsaved user edit";
            EditorUtility.SetDirty(_owner.rows[0]);
            using (var plan = (IDisposable)Preview(path))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan).Any(e => e.Contains("Save the item")), Is.True);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(_owner.rows[0].Text, Is.EqualTo("unsaved user edit"));
            Assert.That(OwnerBytes(), Is.EqualTo(before));
        }
        [Test]
        public void ClosingRealPreviewWindowCancelsAndReleasesStagedObjects()
        {
            string path = WriteNewWorkbook();
            byte[] before = OwnerBytes();
            int originalCount = Resources.FindObjectsOfTypeAll<AssetRow>().Length;
            var plan = Preview(path);
            var commands = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Integrations.Excel.Editor.ExcelAuthoringCommands")).Single(t => t != null);
            var window = (EditorWindow)commands.GetMethod("ShowPreview").Invoke(null, new[] { plan });
            window.titleContent = new GUIContent(WindowTitle);
            window.Repaint();
            window.Close();
            Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Cancelled"));
            Assert.That(OwnerBytes(), Is.EqualTo(before));
            Assert.That(Resources.FindObjectsOfTypeAll<AssetRow>().Length, Is.EqualTo(originalCount));
        }
        private string UseStandaloneRows()
        {
            AssetDatabase.CreateFolder(_assetFolder, "Entries");
            string folder = _assetFolder + "/Entries";
            var rows = _owner.rows.Select(Object.Instantiate).ToArray();
            _owner.rows.Clear();
            foreach (var row in rows)
            {
                row.name = "Standalone-" + row.Id;
                AssetDatabase.CreateAsset(row, folder + "/" + row.Id + ".asset");
                _owner.rows.Add(row);
            }
            EditorUtility.SetDirty(_owner);
            AssetDatabase.SaveAssetIfDirty(_owner);
            return folder;
        }
        [Test]
        public void ExplicitOwnedFolderUpdatesAddsDeletesStandaloneAssetsAndKeepsGuid()
        {
            string folder = UseStandaloneRows();
            string guid = AssetDatabase.AssetPathToGUID(folder + "/1.asset");
            var changed = Object.Instantiate(_owner.rows[0]);
            var added = ScriptableObject.CreateInstance<AssetRow>();
            string path = Path.Combine(_diskFolder, "standalone.xlsx");
            try
            {
                changed.Text = "standalone updated";
                added.Id = 2;
                Export(new List<AssetRow> { changed, added }, path);
            }
            finally
            {
                Object.DestroyImmediate(changed);
                Object.DestroyImmediate(added);
            }
            using (var plan = (IDisposable)Preview(path, folder, row => row.Parent = _owner))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Applied"));
            }
            Assert.That(AssetDatabase.AssetPathToGUID(folder + "/1.asset"), Is.EqualTo(guid));
            Assert.That(AssetDatabase.LoadAssetAtPath<AssetRow>(folder + "/1.asset").Text, Is.EqualTo("standalone updated"));
            Assert.That(AssetDatabase.LoadAssetAtPath<AssetRow>(folder + "/2.asset").Parent, Is.SameAs(_owner));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/9.asset")), Is.False);
            Assert.That(_owner.rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 2 }));
        }
        [Test]
        public void MainAssetsOutsideExplicitFolderAreNeverUpdatedOrDeleted()
        {
            string folder = UseStandaloneRows();
            string path = WriteNewWorkbook();
            byte[] rootBefore = OwnerBytes();
            byte[] itemBefore = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/1.asset"));
            using (var plan = (IDisposable)Preview(path, _assetFolder))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan).Any(e => e.Contains("ownership boundary")), Is.True);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(OwnerBytes(), Is.EqualTo(rootBefore));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/1.asset")), Is.EqualTo(itemBefore));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/9.asset")), Is.True);
        }
        [Test]
        public void ReferenceToPendingRemovalIsRejectedBeforeAnyAssetWrite()
        {
            string path = Path.Combine(_diskFolder, "deleted-reference.xlsx");
            var changed = Object.Instantiate(_owner.rows[0]);
            try
            {
                changed.Reference = _owner.rows[1];
                Export(new List<AssetRow> { changed }, path);
            }
            finally
            {
                Object.DestroyImmediate(changed);
            }
            byte[] before = OwnerBytes();
            using (var plan = (IDisposable)Preview(path))
            {
                Assert.That(ExcelAuthoringTests.Errors(plan).Any(e => e.Contains("Reference") && e.Contains("deletion")), Is.True);
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
            }
            Assert.That(OwnerBytes(), Is.EqualTo(before));
            Assert.That(_owner.rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 9 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DeletingNewReferenceAfterPreviewRejectsCommitAndPreservesOriginalTarget(bool plainList)
        {
            string referencePath = _assetFolder + "/NewReference.asset";
            var reference = ScriptableObject.CreateInstance<AssetRow>();
            reference.Id = 42;
            AssetDatabase.CreateAsset(reference, referencePath);
            string path = Path.Combine(_diskFolder, "stale-reference.xlsx");
            byte[] before = OwnerBytes();
            var original = new ReferenceRow { Id = 1 };
            var values = new List<ReferenceRow> { original };
            object preview;
            if (plainList)
            {
                ExcelAuthoringTests.Call("ListToExcel", typeof(ReferenceRow), new List<ReferenceRow> { new ReferenceRow { Id = 1, Reference = reference } }, path, "Sheet1");
                preview = ExcelAuthoringTests.Call("PreviewList", typeof(ReferenceRow), path, values, null);
            }
            else
            {
                var changed = Object.Instantiate(_owner.rows[0]);
                try
                {
                    changed.Reference = reference;
                    Export(new List<AssetRow> { changed, _owner.rows[1] }, path);
                }
                finally
                {
                    Object.DestroyImmediate(changed);
                }
                preview = Preview(path, initialize: row => row.Parent = _owner);
            }
            using (var plan = (IDisposable)preview)
            {
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Empty);
                Assert.That(AssetDatabase.DeleteAsset(referencePath), Is.True);
                Assert.That(OwnerBytes(), Is.EqualTo(before));
                Assert.That(ExcelAuthoringTests.Commit(plan, true), Is.EqualTo("Failed"));
                Assert.That(ExcelAuthoringTests.Errors(plan), Is.Not.Empty);
            }
            Assert.That(OwnerBytes(), Is.EqualTo(before));
            Assert.That(_owner.rows.Select(r => r.Id), Is.EqualTo(new[] { 1, 9 }));
            Assert.That(_owner.rows[0].Reference, Is.Null);
            Assert.That(values[0], Is.SameAs(original));
            Assert.That(values[0].Reference, Is.Null);
        }

        [Test]
        public void InjectedFailureAfterCreatingAnAssetRestoresOriginalsAndRemovesOwnedNewAsset()
        {
            string folder = UseStandaloneRows();
            var previous = _owner.rows.ToArray();
            var originalFiles = new Dictionary<string, byte[]>();
            foreach (var path in previous.Select(AssetDatabase.GetAssetPath).Concat(new[] { AssetDatabase.GetAssetPath(_owner) }))
            {
                originalFiles.Add(path, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
                originalFiles.Add(path + ".meta", File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")));
            }
            var staged = new List<AssetRow> { Object.Instantiate(previous[0]), ScriptableObject.CreateInstance<AssetRow>(), ScriptableObject.CreateInstance<AssetRow>() };
            try
            {
                staged[0].Text = "partially updated before failure";
                staged[1].Id = 2;
                staged[2].Id = 3;
                foreach (var row in staged)
                {
                    row.Parent = _owner;
                    row.hideFlags = HideFlags.HideAndDontSave;
                }
                var existing = previous.ToDictionary(row => row.Id.ToString());
                var destinations = new Dictionary<string, string> { ["1"] = folder + "/1.asset", ["2"] = folder + "/2.asset" };
                // 受控缺键故障在更新1、实际创建2后触发；不把此测试冒称为真实Unity IO故障复现。
                var api = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Integrations.Excel.Editor.ExcelAuthoring")).Single(t => t != null);
                var apply = api.GetMethod("ApplyAssets", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(typeof(AssetRow));
                var failure = Assert.Throws<TargetInvocationException>(() => apply.Invoke(null, new object[]
                {
                    _owner.rows, _owner, typeof(Owner).GetField(nameof(Owner.rows)), AssetDatabase.GetAssetPath(_owner), previous, staged, existing, destinations, originalFiles, folder
                }));
                Assert.That(failure.InnerException, Is.TypeOf<KeyNotFoundException>(), "A rollback failure must not be hidden by weakening this assertion.");
                foreach (var file in originalFiles)
                {
                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(file.Key)), Is.EqualTo(file.Value), file.Key);
                }

                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/2.asset")), Is.False);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(folder + "/2.asset.meta")), Is.False);
                Assert.That(_owner.rows.Select(row => row.Id), Is.EqualTo(new[] { 1, 9 }));
                Assert.That(AssetDatabase.LoadAssetAtPath<AssetRow>(folder + "/1.asset").Text, Is.EqualTo("old"));
            }
            finally
            {
                foreach (var row in staged)
                {
                    if (row != null)
                    {
                        Object.DestroyImmediate(row);
                    }
                }
            }
        }
    }
}
