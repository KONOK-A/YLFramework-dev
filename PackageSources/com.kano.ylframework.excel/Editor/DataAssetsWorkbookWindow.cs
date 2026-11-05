using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.DataAssets;

namespace YLFramework.Integrations.Excel.Editor
{
    /// <summary>Excel拥有的DataAssets工作簿面板；不要求Gameplay，也不向普通Inspector添加后端依赖。</summary>
    public sealed class DataAssetsWorkbookWindow : EditorWindow
    {
        [SerializeField] private ScriptableObjectConfigs _root;
        [SerializeField] private string _path = string.Empty;

        /// <summary>打开面板并读取根的既有GUID偏好，不改配置资产。</summary>
        public static DataAssetsWorkbookWindow Open(ScriptableObjectConfigs root)
        {
            var window = GetWindow<DataAssetsWorkbookWindow>();
            window.titleContent = new GUIContent("Data Workbook");
            window.SetRoot(root);
            return window;
        }

        /// <summary>切换根并恢复该根的本机工作簿路径。</summary>
        public void SetRoot(ScriptableObjectConfigs root)
        {
            _root = root;
            _path = DataAssetsWorkbookMemory.TryGetPath(root, out var remembered) ? remembered : string.Empty;
        }

        [MenuItem("YLFramework/Excel/Data Assets Workbook")]
        private static void OpenSelected() => Open(Selection.activeObject as ScriptableObjectConfigs);

        [MenuItem("Assets/YLFramework/Excel/Edit Selected Data Root Workbook", false, 2099)]
        private static void OpenSelectedAsset() => OpenSelected();

        [MenuItem("Assets/YLFramework/Excel/Edit Selected Data Root Workbook", true)]
        private static bool HasRoot() => Selection.activeObject is ScriptableObjectConfigs;

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            var selected = (ScriptableObjectConfigs)EditorGUILayout.ObjectField("Data Root", _root, typeof(ScriptableObjectConfigs), false);
            if (EditorGUI.EndChangeCheck())
                SetRoot(selected);
            _path = EditorGUILayout.TextField("Workbook", _path);
            using (new EditorGUI.DisabledScope(!_root))
            {
                if (GUILayout.Button("Choose Existing Workbook"))
                {
                    var path = EditorUtility.OpenFilePanel("DataAssets Workbook", string.Empty, "xlsx");
                    if (!string.IsNullOrEmpty(path))
                        _path = path;
                }
                using (new EditorGUI.DisabledScope(!File.Exists(_path)))
                {
                    if (GUILayout.Button("Preview Import"))
                    {
                        var root = _root;
                        var path = _path;
                        var preview = ExcelAuthoringCommands.ShowPreview(DataAssetsExcelCommands.PreviewImport(root, path));
                        preview.Applied += () =>
                        {
                            if (!root)
                                return;
                            root.ClearCache();
                            DataAssetsWorkbookMemory.Remember(root, path);
                        };
                    }
                    if (GUILayout.Button("Open Workbook Folder"))
                        EditorUtility.RevealInFinder(_path);
                }
                if (GUILayout.Button("Export Workbook"))
                {
                    var path = EditorUtility.SaveFilePanel("Export DataAssets Workbook", string.Empty, _root.name, "xlsx");
                    if (!string.IsNullOrEmpty(path) && (!File.Exists(path) || EditorUtility.DisplayDialog("Replace Workbook",
                        "Replace the complete workbook, including its existing sheets and formatting?\n" + path, "Replace", "Cancel")))
                    {
                        try
                        {
                            DataAssetsExcelCommands.Export(_root, path);
                            _path = path;
                        }
                        catch (Exception error) { EditorUtility.DisplayDialog("Excel Export Failed", error.Message, "Close"); }
                    }
                }
                if (GUILayout.Button("Forget Remembered Workbook"))
                {
                    DataAssetsWorkbookMemory.Forget(_root);
                    _path = string.Empty;
                }
            }
            EditorGUILayout.HelpBox("Preferences are local and keyed by the saved root GUID. Preview does not commit assets; forgetting a path does not delete files.", MessageType.Info);
        }
    }
}
