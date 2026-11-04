using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;

namespace YLFramework.Editor.DataAssets
{
    /// <summary>配置条目检查器，只允许编辑由条目声明父根实际拥有的资产。</summary>
    [CustomEditor(typeof(ScriptableObjectData), true)]
    public sealed class ScriptableObjectDataInspector : UnityEditor.Editor
    {
        /// <summary>显示只读父根，校验所有权后编辑身份与序列化字段，并使相关根索引失效。</summary>
        public override void OnInspectorGUI()
        {
            var data = (ScriptableObjectData)target;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Parent", data.parent, typeof(ScriptableObjectConfigs), false);
            }

            var owned = ScriptableObjectConfigsController.IsOwned(data.parent, data);
            if (!owned)
            {
                EditorGUILayout.HelpBox("This entry is not owned by the referenced YL root. It is read-only here; unlinking never deletes external assets.", MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!owned))
            {
                EditorGUI.BeginChangeCheck();
                var id = EditorGUILayout.DelayedIntField("ID", data.id);
                var displayName = EditorGUILayout.DelayedTextField("Name", data.configName);
                if (EditorGUI.EndChangeCheck())
                {
                    try
                    {
                        ScriptableObjectConfigsController.RenameData(data.parent, data, id, displayName);
                    }
                    catch (System.InvalidOperationException error)
                    {
                        EditorUtility.DisplayDialog("DataAssets Identity", error.Message, "Close");
                    }
                }
                serializedObject.Update();
                DrawPropertiesExcluding(serializedObject, "m_Script", "parent", "id", "configName");
                if (serializedObject.ApplyModifiedProperties() && data.parent)
                {
                    data.parent.ClearCache();
                }
            }
        }
    }
}
