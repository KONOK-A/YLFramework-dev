using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;
using YLFramework.Excel;
using YLFramework.Integrations.Excel.Editor;

namespace YLFramework.Tests.Localization.Excel
{
    public class LocalizationExcelTests
    {
        public sealed class WorkbookRow
        {
            [ExcelScriptableObjectId] public int id;
            public object title;
            public object image;
            public object _inheritedTitle;
        }

        private string _folder;
        private string _output;
        private ScriptableObjectConfigs _root;
        private LocalizationExcelRow _row;
        private StringTableCollection _collection;
        private Locale _locale;
        private string _dataFolder;
        private const long _key = 9007199254740993L;

        [SetUp]
        public void SetUp()
        {
            _folder = "Assets/__YLLocalizationExcel_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            _output = Path.Combine(Environment.GetEnvironmentVariable("YL_EXCEL_TEST_OUTPUT") ?? Path.GetTempPath(), "Locale-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_output);
            _locale = Locale.CreateLocale("en");
            AssetDatabase.CreateAsset(_locale, _folder + "/en.asset");
            _collection = LocalizationEditorSettings.CreateStringTableCollection("Items-" + Guid.NewGuid().ToString("N"), _folder, new[] { _locale });
            _collection.SharedData.AddKey("name.sword", _key);
            EditorUtility.SetDirty(_collection.SharedData);
            AssetDatabase.SaveAssetIfDirty(_collection.SharedData);
            _root = ScriptableObject.CreateInstance<ScriptableObjectConfigs>();
            AssetDatabase.CreateAsset(_root, _folder + "/root.asset");
            var script = AssetDatabase.FindAssets("LocalizationExcelRow t:MonoScript").Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<MonoScript>).Single(item => item.GetClass() == typeof(LocalizationExcelRow));
            _root.typeScriptGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
            EditorUtility.SetDirty(_root);
            AssetDatabase.SaveAssetIfDirty(_root);
            _row = (LocalizationExcelRow)ScriptableObjectConfigsController.CreateData(typeof(LocalizationExcelRow), _root);
            _dataFolder = ScriptableObjectConfigsController.GetDataPath(_root);
            _row.title = new LocalizedString(_collection.SharedData.TableCollectionNameGuid, _key);
            EditorUtility.SetDirty(_row);
            AssetDatabase.SaveAssetIfDirty(_row);
            AssetDatabase.SaveAssetIfDirty(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root)
                Undo.ClearUndo(_root);
            if (_row)
                Undo.ClearUndo(_row);
            if (!string.IsNullOrEmpty(_dataFolder))
                AssetDatabase.DeleteAsset(_dataFolder);
            if (!string.IsNullOrEmpty(_folder))
                AssetDatabase.DeleteAsset(_folder);
            if (Directory.Exists(_output))
                Directory.Delete(_output, true);
        }

        private static object Call(string method, params object[] arguments)
        {
            var type = Type.GetType("YLFramework.Editor.Localization.Excel.LocalizationExcelCommands, YLFramework.Localization.Excel.Editor");
            Assert.That(type, Is.Not.Null, "The optional Excel integration must validate and normalize official references.");
            try
            {
                return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments);
            }
            catch (TargetInvocationException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private string Workbook(bool stable = false)
        {
            var json = stable
                ? new JObject { ["tableGuid"] = _collection.SharedData.TableCollectionNameGuid.ToString("N"), ["entryId"] = _key.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                : new JObject { ["table"] = _collection.TableCollectionName, ["entry"] = "name.sword" };
            var path = Path.Combine(_output, "input.xlsx");
            ExcelAuthoring.ListToExcel(new[] { new WorkbookRow { id = _row.id, title = json } }, path);
            return path;
        }

        [Test]
        public void ReadableImportResolvesOnlyStagedDataAndCancellationWritesNothing()
        {
            var before = EditorJsonUtility.ToJson(_row);
            using var plan = (ExcelImportPlan)Call("PreviewImport", _root, Workbook());
            Assert.That(plan.Errors, Is.Empty);
            Assert.That(EditorJsonUtility.ToJson(_row), Is.EqualTo(before));
            Assert.That(plan.Commit(false), Is.EqualTo(ExcelCommitResult.Cancelled));
            Assert.That(EditorJsonUtility.ToJson(_row), Is.EqualTo(before));
        }

        [Test]
        public void ImportCommitsStableIdentityAndExistingReferencesSurviveRenames()
        {
            using (var plan = (ExcelImportPlan)Call("PreviewImport", _root, Workbook()))
            {
                Assert.That(plan.Errors, Is.Empty);
                Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            }
            Assert.That(_row.title.TableEntryReference.KeyId, Is.EqualTo(_key));
            var guid = _row.title.TableReference.TableCollectionNameGuid;
            _collection.SharedData.RenameKey(_key, "renamed.sword");
            _collection.SharedData.TableCollectionName += "-renamed";
            EditorUtility.SetDirty(_collection.SharedData);
            AssetDatabase.SaveAssetIfDirty(_collection.SharedData);
            Assert.That(_row.title.TableReference.TableCollectionNameGuid, Is.EqualTo(guid));
            var type = Type.GetType("YLFramework.Editor.Localization.DataAssets.LocalizationDataAssetsCommands, YLFramework.Localization.DataAssets.Editor");
            Assert.That(type, Is.Not.Null);
            var errors = (IReadOnlyList<string>)type.GetMethod("Validate").Invoke(null, new object[] { _root });
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void DeletingAKeyAfterPreviewBlocksCommitBeforeAnyTargetWrite()
        {
            var before = EditorJsonUtility.ToJson(_row);
            using var plan = (ExcelImportPlan)Call("PreviewImport", _root, Workbook());
            Assert.That(plan.Errors, Is.Empty);
            _collection.SharedData.RemoveKey(_key);
            _collection.SharedData.AddKey("name.sword", _key + 1);
            Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
            Assert.That(EditorJsonUtility.ToJson(_row), Is.EqualTo(before));
        }

        [Test]
        public void ExportUsesNormalizedClonesAndDoesNotRewriteTheSourceConfiguration()
        {
            _row.title = new LocalizedString(_collection.TableCollectionName, "name.sword");
            EditorUtility.SetDirty(_row);
            AssetDatabase.SaveAssetIfDirty(_row);
            var before = EditorJsonUtility.ToJson(_row);
            var path = Path.Combine(_output, "export.xlsx");
            Call("Export", _root, path);
            Assert.That(EditorJsonUtility.ToJson(_row), Is.EqualTo(before));
            var jsonPath = Path.Combine(_output, "export.json");
            using var json = ExcelAuthoring.PreviewJson(path, jsonPath);
            Assert.That(json.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            var title = JArray.Parse(File.ReadAllText(jsonPath))[0]["title"];
            Assert.That((string)title["entryId"], Is.EqualTo(_key.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That((string)title["tableGuid"], Is.EqualTo(_collection.SharedData.TableCollectionNameGuid.ToString("N")));
        }

        [Test]
        public void AssetReferenceCannotTargetAStringCollection()
        {
            var path = Path.Combine(_output, "wrong-kind.xlsx");
            var reference = new JObject { ["table"] = _collection.TableCollectionName, ["entry"] = "name.sword" };
            ExcelAuthoring.ListToExcel(new[] { new WorkbookRow { id = _row.id, title = reference, image = reference } }, path);
            var before = EditorJsonUtility.ToJson(_row);
            using var plan = (ExcelImportPlan)Call("PreviewImport", _root, path);
            Assert.That(plan.Errors, Is.Not.Empty);
            Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
            Assert.That(EditorJsonUtility.ToJson(_row), Is.EqualTo(before));
        }

        [Test]
        public void DeclaredAssetTypeMustMatchTheBusinessField()
        {
            var assets = LocalizationEditorSettings.CreateAssetTableCollection("Icons-" + Guid.NewGuid().ToString("N"), _folder, new[] { _locale });
            assets.SharedData.AddKey("icon");
            assets.SetEntryAssetType("icon", typeof(Texture2D));
            var path = Path.Combine(_output, "wrong-type.xlsx");
            ExcelAuthoring.ListToExcel(new[] { new WorkbookRow
            {
                id = _row.id,
                title = new JObject { ["table"] = _collection.TableCollectionName, ["entry"] = "name.sword" },
                image = new JObject { ["table"] = assets.TableCollectionName, ["entry"] = "icon" }
            } }, path);
            using var plan = (ExcelImportPlan)Call("PreviewImport", _root, path);
            Assert.That(plan.Errors.Any(error => error.Contains("Sprite") && error.Contains("Texture2D")), Is.True);
            Assert.That(plan.CanCommit, Is.False);
        }

        [Test]
        public void RawReferenceMetadataChangedAfterPreviewCannotBeHiddenByTheWorkbookProjection()
        {
            using var plan = (ExcelImportPlan)Call("PreviewImport", _root, Workbook(true));
            Assert.That(plan.Errors, Is.Empty);
            _row.title.FallbackState = UnityEngine.Localization.Settings.FallbackBehavior.DontUseFallback;
            Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
            Assert.That(_row.title.FallbackState, Is.EqualTo(UnityEngine.Localization.Settings.FallbackBehavior.DontUseFallback));
        }

        [Test]
        public void AWorkbookCanRepairAnExistingBrokenReferenceWithoutWeakeningOwnershipChecks()
        {
            _collection.SharedData.RemoveKey(_key);
            _collection.SharedData.AddKey("new.name", _key + 2);
            var path = Path.Combine(_output, "repair.xlsx");
            ExcelAuthoring.ListToExcel(new[] { new WorkbookRow
            {
                id = _row.id,
                title = new JObject { ["table"] = _collection.TableCollectionName, ["entry"] = "new.name" }
            } }, path);
            using var plan = (ExcelImportPlan)Call("PreviewImport", _root, path);
            Assert.That(plan.Errors, Is.Empty);
            Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            Assert.That(_row.title.TableEntryReference.KeyId, Is.EqualTo(_key + 2));
        }

        [Test]
        public void AmbiguousReadableCollectionNamesAreRejectedInsteadOfChoosingTheFirst()
        {
            var second = LocalizationEditorSettings.CreateStringTableCollection("Other-" + Guid.NewGuid().ToString("N"), _folder, new[] { _locale });
            second.SharedData.AddKey("name.sword");
            second.SharedData.TableCollectionName = _collection.TableCollectionName;
            using var plan = (ExcelImportPlan)Call("PreviewImport", _root, Workbook());
            Assert.That(plan.Errors.Any(error => error.Contains("ambiguous")), Is.True);
            Assert.That(plan.CanCommit, Is.False);
        }

        [Test]
        public void PrivateSerializedBaseReferencesAreNormalizedAndRevalidatedBeforeCommit()
        {
            var path = Path.Combine(_output, "inherited.xlsx");
            var reference = new JObject { ["table"] = _collection.TableCollectionName, ["entry"] = "name.sword" };
            ExcelAuthoring.ListToExcel(new[] { new WorkbookRow { id = _row.id, title = reference, _inheritedTitle = reference } }, path);
            using (var plan = (ExcelImportPlan)Call("PreviewImport", _root, path))
            {
                Assert.That(plan.Errors, Is.Empty);
                Assert.That(plan.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
            }
            Assert.That(_row.InheritedTitle.TableEntryReference.KeyId, Is.EqualTo(_key));
            Assert.That(_row.InheritedTitle.TableReference.TableCollectionNameGuid, Is.EqualTo(_collection.SharedData.TableCollectionNameGuid));
            _row.InheritedTitle.SetReference("missing-inherited-collection", "missing");
            // 只导入必需字段，验证未被本次工作簿替换的存量继承字段同样不能漏检。
            var data = new[] { new InheritedValidationWorkbook { id = _row.id, title = reference } };
            var badPath = Path.Combine(_output, "invalid-inherited.xlsx");
            ExcelAuthoring.ListToExcel(data, badPath);
            using var invalid = (ExcelImportPlan)Call("PreviewImport", _root, badPath);
            Assert.That(invalid.Errors.Any(error => error.Contains("_inheritedTitle")), Is.True);
        }

        public sealed class InheritedValidationWorkbook
        {
            [ExcelScriptableObjectId] public int id;
            public object title;
        }
    }
}
