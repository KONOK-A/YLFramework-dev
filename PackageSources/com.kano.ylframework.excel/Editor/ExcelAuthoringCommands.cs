using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Integrations.Excel.Editor
{
    /// <summary>可选领域Editor接线入口；关闭文件对话框不创建计划，关闭预览即取消。</summary>
    public static class ExcelAuthoringCommands
    {
        /// <summary>选择工作簿并显示普通列表的替换预览，不自动确认导入。</summary>
        /// <typeparam name="T">可通过公开无参构造创建的引用类型。</typeparam>
        /// <param name="values">确认后替换内容的目标列表。</param>
        public static void ImportList<T>(List<T> values) where T : class, new()
        {
            string path = EditorUtility.OpenFilePanel("Import .xlsx", string.Empty, "xlsx");
            if (path.Length != 0)
            {
                ShowPreview(ExcelAuthoring.PreviewList(path, values));
            }
        }
        /// <summary>选择输出路径并导出普通列表，覆盖已有工作簿前要求确认。</summary>
        /// <typeparam name="T">决定导出字段的声明行类型。</typeparam>
        /// <param name="values">不含 null 行的导出集合。</param>
        public static void ExportList<T>(IEnumerable<T> values)
        {
            string path = SavePath();
            if (path.Length != 0)
            {
                ExcelAuthoring.ListToExcel(values, path);
            }
        }
        /// <summary>选择工作簿并显示自有 ScriptableObject 资产的导入预览。</summary>
        /// <typeparam name="T">行资产的基类型。</typeparam>
        /// <param name="values">owner 实际持有的序列化列表字段。</param>
        /// <param name="owner">位于 Assets/ 下且已保存的主资产。</param>
        /// <param name="assetFolder">显式拥有的独立条目目录；null 仅管理 owner 同文件的子资产。</param>
        /// <param name="initialize">导入前仅初始化暂存对象的回调，不得修改外部状态。</param>
        public static void ImportScriptableObjects<T>(List<T> values, ScriptableObject owner, string assetFolder = null, Action<T> initialize = null) where T : ScriptableObject
        {
            string path = EditorUtility.OpenFilePanel("Import .xlsx", string.Empty, "xlsx");
            if (path.Length != 0)
            {
                ShowPreview(ExcelAuthoring.PreviewScriptableObjects(path, values, owner, assetFolder, initialize: initialize));
            }
        }
        /// <summary>选择输出路径并导出带具体类型列的资产集合，覆盖已有文件前要求确认。</summary>
        /// <typeparam name="T">行资产的基类型。</typeparam>
        /// <param name="values">具备唯一稳定 ID 的资产集合。</param>
        public static void ExportScriptableObjects<T>(IEnumerable<T> values) where T : ScriptableObject
        {
            string path = SavePath();
            if (path.Length != 0)
            {
                ExcelAuthoring.ScriptableObjectToExcel(values, path);
            }
        }
        /// <summary>为非空计划创建独立预览窗口，窗口停用时释放仍待处理的计划。</summary>
        /// <param name="plan">交由窗口持有的一次性导入计划。</param>
        /// <returns>已显示的窗口，可在其上订阅成功应用事件。</returns>
        public static ExcelPreviewWindow ShowPreview(ExcelImportPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var window = ScriptableObject.CreateInstance<ExcelPreviewWindow>();
            window.SetPlan(plan);
            window.ShowUtility();
            return window;
        }
        // 取得整本工作簿的输出路径，取消选择或拒绝覆盖时返回空字符串。
        private static string SavePath()
        {
            string path = EditorUtility.SaveFilePanel("Export .xlsx", string.Empty, "Data", "xlsx");
            if (path.Length != 0 && File.Exists(path) && !EditorUtility.DisplayDialog("Replace workbook", "Replace this entire workbook? Existing sheets and formatting will be replaced.\n" + path, "Replace", "Cancel"))
            {
                return string.Empty;
            }

            return path;
        }
    }

    /// <summary>显示导入错误和前后差异，并承接显式确认或取消的一次性计划窗口。</summary>
    public sealed class ExcelPreviewWindow : EditorWindow
    {
        private ExcelImportPlan _plan;
        private Vector2 _scroll;
        /// <summary>窗口当前持有的计划；脚本重载后可能为空。</summary>
        public ExcelImportPlan Plan => _plan;
        /// <summary>窗口确认提交且计划结果为 Applied 后、关闭窗口前同步触发的通知。</summary>
        /// <remarks>订阅者异常不回滚已提交结果；通知结束或抛出异常后均关闭窗口。</remarks>
        public event Action Applied;
        // 释放旧计划后接管新计划，并设置预览窗口的基本显示信息。
        internal void SetPlan(ExcelImportPlan value)
        {
            _plan?.Dispose();
            _plan = value;
            titleContent = new GUIContent("Excel Import Preview");
            minSize = new Vector2(620, 380);
        }
        // 展示计划诊断及差异，并处理显式确认、成功通知和取消。
        private void OnGUI()
        {
            if (_plan == null)
            {
                EditorGUILayout.HelpBox("The preview was closed or scripts reloaded. Preview again before importing.", MessageType.Info);
                return;
            }
            EditorGUILayout.HelpBox("No target is changed until Confirm Import. Review additions, updates and removals. Saved asset changes are destructive; keep version-control backups.", MessageType.Info);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var error in _plan.Errors)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            foreach (var change in _plan.Changes)
            {
                EditorGUILayout.LabelField(change.Kind + "  " + change.Key, EditorStyles.boldLabel);
                if (change.Before != null)
                {
                    EditorGUILayout.SelectableLabel("Before: " + change.Before, EditorStyles.textArea, GUILayout.MinHeight(35));
                }

                if (change.After != null)
                {
                    EditorGUILayout.SelectableLabel("After:  " + change.After, EditorStyles.textArea, GUILayout.MinHeight(35));
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Result", _plan.Result.ToString());
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_plan.CanCommit))
                {
                    if (GUILayout.Button("Confirm Import") && EditorUtility.DisplayDialog("Confirm Excel Import", "Apply this preview, including all listed removals?", "Import", "Cancel"))
                    {
                        if (_plan.Commit(true) == ExcelCommitResult.Applied)
                        {
                            try
                            {
                                Applied?.Invoke();
                            }
                            finally
                            {
                                Close();
                            }
                        }
                    }
                }
                if (GUILayout.Button("Cancel"))
                {
                    _plan.Commit(false);
                    Close();
                }
            }
        }
        // 在窗口停用时取消仍未处理的计划并释放暂存资源。
        private void OnDisable() => _plan?.Dispose();
    }

    /// <summary>收集工作簿、工作表及 JSON 输出选项，并将写入交给预览确认流程。</summary>
    public sealed class ExcelJsonWindow : EditorWindow
    {
        private string _workbook = string.Empty;
        private string _destination = string.Empty;
        private string _sheet = string.Empty;
        private string _excludedPrefix = string.Empty;
        // 打开工作簿转 JSON 的选项窗口。
        [MenuItem("YLFramework/Excel/Workbook To JSON")]
        private static void Open() => GetWindow<ExcelJsonWindow>("Excel To JSON");
        // 编辑输入选项，并按请求创建不立即写入目标的 JSON 预览。
        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Rows 1-3: field / type / description. Data starts at row 4. Enum values without a CLR target type are retained as strings. Object references remain GUID[:localFileID] strings.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                _workbook = EditorGUILayout.TextField("Workbook", _workbook);
                if (GUILayout.Button("Browse", GUILayout.Width(75)))
                {
                    string value = EditorUtility.OpenFilePanel("Workbook", string.Empty, "xlsx");
                    if (value.Length != 0)
                    {
                        _workbook = value;
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _destination = EditorGUILayout.TextField("JSON Output", _destination);
                if (GUILayout.Button("Browse", GUILayout.Width(75)))
                {
                    string value = EditorUtility.SaveFilePanel("JSON Output", string.Empty, "Data", "json");
                    if (value.Length != 0)
                    {
                        _destination = value;
                    }
                }
            }
            _sheet = EditorGUILayout.TextField("Sheet (blank = first)", _sheet);
            _excludedPrefix = EditorGUILayout.TextField("Excluded Field Prefix", _excludedPrefix);
            if (GUILayout.Button("Preview JSON Changes"))
            {
                ExcelAuthoringCommands.ShowPreview(ExcelAuthoring.PreviewJson(_workbook, _destination, _excludedPrefix, string.IsNullOrWhiteSpace(_sheet) ? null : _sheet));
            }
        }
    }
}
