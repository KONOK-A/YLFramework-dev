using System;
using UnityEditor;
using UnityEngine;
using YLFramework.Equipment;

namespace YLFramework.Editor.Equipment
{
    /// <summary>编辑装备配表的显示名与数据类型绑定，提供配表窗口及本根目录刷新入口。</summary>
    [CustomEditor(typeof(EquipmentsConfig), true)]
    public sealed class EquipmentsConfigInspector : UnityEditor.Editor
    {
        /// <summary>仅在未运行且未切换 PlayMode 时编辑配表设置，检查候选数据脚本并提示失效绑定。</summary>
        public override void OnInspectorGUI()
        {
            var root = (EquipmentsConfig)target;
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displayName"));
                var guid = serializedObject.FindProperty("typeScriptGuid");
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid.stringValue));
                var next = (MonoScript)EditorGUILayout.ObjectField("数据类型脚本", script, typeof(MonoScript), false);
                if (next != script)
                {
                    var type = next ? next.GetClass() : typeof(EquipmentConfigData);
                    if (type != null && typeof(EquipmentConfigData).IsAssignableFrom(type) && !type.IsAbstract && !type.ContainsGenericParameters)
                    {
                        guid.stringValue = next ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(next)) : "";
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("装备配置类型", "必须选择 EquipmentConfigData 的具体派生类脚本。", "关闭");
                    }
                }
                try
                {
                    EditorGUILayout.LabelField("当前数据类型", root.GetDataType().FullName);
                }
                catch (InvalidOperationException error)
                {
                    EditorGUILayout.HelpBox(error.Message, MessageType.Error);
                    if (GUILayout.Button("清除失效绑定"))
                    {
                        guid.stringValue = "";
                    }
                }
                serializedObject.ApplyModifiedProperties();
                if (GUILayout.Button("编辑装备配表"))
                {
                    OpenEquipmentsConfig(root);
                }

                if (GUILayout.Button("从本根目录刷新条目"))
                {
                    EquipmentsConfigController.RefreshEntries(root);
                }
            }
            EditorGUILayout.HelpBox("Excel 导入/导出位于选配 Domain Excel 菜单；默认核心不加载 xlsx 后端。", MessageType.Info);
        }
        /// <summary>显示装备配表窗口并切换到指定配表。</summary>
        public static void OpenEquipmentsConfig(EquipmentsConfig root)
        {
            var window = EditorWindow.GetWindow<ConfigsWindow>();
            window.SetCurrentConfigs(root);
            window.Show();
        }
    }
}
