using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;

namespace YLFramework.Integrations.Excel.Editor
{
    /// <summary>只在可选Excel程序集依赖普通DataAssets作者工具，不建立反向依赖。</summary>
    public static class DataAssetsExcelCommands
    {
        /// <summary>校验 DataAssets 根及其绑定类型，为本根拥有的定义资产创建导入预览。</summary>
        /// <param name="root">已保存的 YL DataAssets 主资产根。</param>
        /// <param name="path">待读取的 .xlsx 工作簿路径。</param>
        /// <returns>包含诊断的一次性计划；根校验失败也以计划错误返回，不立即写入资产。</returns>
        public static ExcelImportPlan PreviewImport(ScriptableObjectConfigs root, string path)
        {
            try
            {
                var type = RequireValidRoot(root);
                return ExcelAuthoring.PreviewScriptableObjects(path, root.configs, root,
                    ScriptableObjectConfigsController.GetDataPath(root), initialize: item => item.parent = root, dataType: type);
            }
            catch (Exception e)
            {
                var plan = new ExcelImportPlan();
                plan.Error(e.Message);
                return plan;
            }
        }

        /// <summary>按根绑定的数据类型导出全部定义，成功后按根 GUID 记住工作簿路径。</summary>
        /// <param name="root">绑定类型和条目所有权均有效的已保存 DataAssets 主资产根。</param>
        /// <param name="path">父目录已存在的 .xlsx 输出路径，将替换整个工作簿。</param>
        /// <remarks>最近路径保存在编辑器偏好中，不序列化进运行时配置资产。</remarks>
        public static void Export(ScriptableObjectConfigs root, string path)
        {
            var type = RequireValidRoot(root);
            ExcelAuthoring.ScriptableObjectToExcel(root.configs, path, dataType: type);
            DataAssetsWorkbookMemory.Remember(root, path);
        }

        // 验证已保存的根、可用绑定类型及现有条目所有权，返回实际导出或导入基类型。
        private static Type RequireValidRoot(ScriptableObjectConfigs root)
        {
            if (root == null || !AssetDatabase.IsMainAsset(root) || !AssetDatabase.GetAssetPath(root).StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Select a saved YL DataAssets root under Assets/.");
            }

            var type = root.GetDataType();
            if (type != typeof(ScriptableObjectData) && !TypeCache.GetTypesDerivedFrom<ScriptableObjectData>().Contains(type))
            {
                throw new InvalidOperationException("Bound data type is unavailable in TypeCache: " + type.FullName);
            }

            var diagnostics = ScriptableObjectConfigsController.Validate(root);
            if (diagnostics.Count != 0)
            {
                throw new InvalidOperationException(string.Join("\n", diagnostics.Select(d => "Row " + d.Row + ": " + d.Message)));
            }

            foreach (var item in root.configs)
            {
                if (!ScriptableObjectConfigsController.IsOwned(root, item))
                {
                    throw new InvalidOperationException("Entry is not owned by this root: " + AssetDatabase.GetAssetPath(item));
                }
            }

            return type;
        }

        // 为当前根选择工作簿并显示预览，成功应用后清除此根的查询缓存。
        [MenuItem("Assets/YLFramework/Excel/Preview Import Selected Data Root", false, 2100)]
        private static void ImportSelected()
        {
            var root = Selection.activeObject as ScriptableObjectConfigs;
            string path = EditorUtility.OpenFilePanel("Import YL DataAssets .xlsx", string.Empty, "xlsx");
            if (path.Length == 0)
            {
                return;
            }

            var window = ExcelAuthoringCommands.ShowPreview(PreviewImport(root, path));
            window.Applied += () =>
            {
                if (root != null)
                {
                    root.ClearCache();
                }
            };
        }

        // 仅依据当前选择是否为 DataAssets 根决定导入菜单可用性，不替代导入预检。
        [MenuItem("Assets/YLFramework/Excel/Preview Import Selected Data Root", true)]
        private static bool CanImportSelected() => Selection.activeObject is ScriptableObjectConfigs;

        // 导出当前根并在覆盖前确认，将导出异常显示给用户。
        [MenuItem("Assets/YLFramework/Excel/Export Selected Data Root", false, 2101)]
        private static void ExportSelected()
        {
            var root = Selection.activeObject as ScriptableObjectConfigs;
            string path = EditorUtility.SaveFilePanel("Export YL DataAssets .xlsx", string.Empty, root == null ? "Data" : root.name, "xlsx");
            if (path.Length == 0)
            {
                return;
            }

            if (File.Exists(path) && !EditorUtility.DisplayDialog("Replace Workbook", "Replace this entire workbook, including its sheets and formatting?\n" + path, "Replace", "Cancel"))
            {
                return;
            }

            try
            {
                Export(root, path);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Excel Export Failed", e.Message, "Close");
            }
        }

        // 仅依据当前选择是否为 DataAssets 根决定导出菜单可用性。
        [MenuItem("Assets/YLFramework/Excel/Export Selected Data Root", true)]
        private static bool CanExportSelected() => Selection.activeObject is ScriptableObjectConfigs;
    }
}
