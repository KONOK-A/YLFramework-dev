using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;
using YLFramework.Editor.Localization.DataAssets;
using YLFramework.Integrations.Excel.Editor;

namespace YLFramework.Editor.Localization.Excel
{
    /// <summary>业务工作簿只交换官方引用；所有写入仍走既有一次性 Excel 预览事务。</summary>
    public static class LocalizationExcelCommands
    {
        /// <summary>在暂存副本规范化身份，预览及提交前均检查官方表项，不写翻译或源配置。</summary>
        public static ExcelImportPlan PreviewImport(ScriptableObjectConfigs root, string path)
        {
            ExcelImportPlan plan = null;
            try
            {
                var type = RequireRoot(root, false);
                var staged = new List<ScriptableObjectData>();
                plan = ExcelAuthoring.PreviewScriptableObjects(path, root.configs, root,
                    ScriptableObjectConfigsController.GetDataPath(root), null, row => row.parent = root, type, row =>
                    {
                        staged.Add(row);
                        LocalizationReferenceAuthoring.NormalizeObject(row);
                    });
                if (plan.Errors.Count == 0)
                    plan.AddValidation(() =>
                    {
                        foreach (var row in staged)
                        {
                            var errors = LocalizationReferenceAuthoring.ValidateObject(row);
                            if (errors.Count != 0)
                                throw new InvalidOperationException(string.Join("\n", errors));
                        }
                    });
                return plan;
            }
            catch (Exception error)
            {
                plan ??= new ExcelImportPlan();
                plan.Error(error.Message);
                return plan;
            }
        }

        /// <summary>克隆并规范化导出行；原配置仍保持原身份表达和脏状态，不隐式保存。</summary>
        public static void Export(ScriptableObjectConfigs root, string path)
        {
            var type = RequireRoot(root);
            var copies = new List<ScriptableObjectData>();
            try
            {
                foreach (var row in root.configs)
                {
                    var copy = UnityEngine.Object.Instantiate(row);
                    copy.hideFlags = HideFlags.HideAndDontSave;
                    copies.Add(copy);
                    LocalizationReferenceAuthoring.NormalizeObject(copy);
                }
                ExcelAuthoring.ScriptableObjectToExcel(copies, path, dataType: type);
            }
            finally
            {
                foreach (var copy in copies)
                    if (copy)
                        UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static Type RequireRoot(ScriptableObjectConfigs root, bool validateExistingReferences = true)
        {
            if (!root || !AssetDatabase.IsMainAsset(root) || !AssetDatabase.GetAssetPath(root).StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Select a saved YL DataAssets root under Assets/.");
            var errors = new List<string>();
            foreach (var diagnostic in ScriptableObjectConfigsController.Validate(root))
                errors.Add(diagnostic.Message);
            if (root.configs != null)
                foreach (var row in root.configs)
                    if (!ScriptableObjectConfigsController.IsOwned(root, row))
                        errors.Add("Entry is not owned by this root.");
            if (validateExistingReferences)
                errors.AddRange(LocalizationDataAssetsCommands.Validate(root));
            if (errors.Count != 0)
                throw new InvalidOperationException(string.Join("\n", errors));
            return root.GetDataType();
        }

        [MenuItem("Assets/YLFramework/Localization/Preview Reference Workbook", false, 2111)]
        private static void ImportSelected()
        {
            var root = Selection.activeObject as ScriptableObjectConfigs;
            var path = EditorUtility.OpenFilePanel("Import Localization References", "", "xlsx");
            if (path.Length != 0)
            {
                var window = ExcelAuthoringCommands.ShowPreview(PreviewImport(root, path));
                window.Applied += () => { if (root) root.ClearCache(); };
            }
        }

        [MenuItem("Assets/YLFramework/Localization/Export Reference Workbook", false, 2112)]
        private static void ExportSelected()
        {
            var root = Selection.activeObject as ScriptableObjectConfigs;
            var path = EditorUtility.SaveFilePanel("Export Localization References", "", root ? root.name : "References", "xlsx");
            if (path.Length == 0 || (File.Exists(path) && !EditorUtility.DisplayDialog("Replace Workbook", path, "Replace", "Cancel")))
                return;
            try
            {
                Export(root, path);
            }
            catch (Exception error) { EditorUtility.DisplayDialog("Localization Export Failed", error.Message, "Close"); }
        }

        [MenuItem("Assets/YLFramework/Localization/Preview Reference Workbook", true)]
        [MenuItem("Assets/YLFramework/Localization/Export Reference Workbook", true)]
        private static bool CanUseSelected() => Selection.activeObject is ScriptableObjectConfigs;
    }
}
