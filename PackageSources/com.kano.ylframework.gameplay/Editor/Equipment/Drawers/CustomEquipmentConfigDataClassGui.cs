using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Equipment;
namespace YLFramework.Editor.Equipment
{
    /// <summary>为 Script 模式选择普通装备类，维护类型全名、脚本 GUID 并展示绑定诊断。</summary>
    internal sealed class CustomEquipmentConfigDataClassGui : CustomPropertyGui
    {
        /// <summary>仅在 Script 模式列出可构造装备类，按类型全名排序并提供脚本打开入口。</summary>
        public override void OnPropertyGui(SerializedProperty property)
        {
            var mode = property.serializedObject.FindProperty("equipmentType");
            if (mode.intValue != (int)EquipmentType.Script)
            {
                return;
            }

            var type = AssemblyTool.GetType(property.stringValue);
            if (EditorGUILayout.DropdownButton(new GUIContent(string.IsNullOrEmpty(property.stringValue) ? "选择装备类" : property.stringValue), FocusType.Passive))
            {
                var menu = new GenericMenu();
                foreach (var candidate in TypeCache.GetTypesDerivedFrom<IEquipment>().Where(ScriptBindController.IsEquipmentType).OrderBy(t => t.FullName))
                {
                    var selected = candidate;
                    menu.AddItem(new GUIContent(selected.FullName), type == selected, () =>
                    {
                        EquipmentAuthoring.SetString(property, selected.FullName);
                        using var serialized = new SerializedObject(property.serializedObject.targetObject);
                        serialized.FindProperty("equipmentClassGuid").stringValue = ScriptBindController.GetScriptGuid(selected);
                        serialized.ApplyModifiedProperties();
                    });
                }
                menu.ShowAsContext();
            }
            if (!ScriptBindController.IsEquipmentType(type))
            {
                EditorGUILayout.HelpBox("无效装备类: " + property.stringValue + "。需要普通 IEquipment 类及 public 无参构造。", MessageType.Error);
            }

            var guid = property.serializedObject.FindProperty("equipmentClassGuid").stringValue;
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
            if (!string.IsNullOrEmpty(guid) && !script)
            {
                EditorGUILayout.HelpBox("装备脚本 GUID 失效: " + guid, MessageType.Error);
            }

            using (new EditorGUI.DisabledScope(!script))
            {
                if (GUILayout.Button("打开装备脚本"))
                {
                    AssetDatabase.OpenAsset(script);
                }
            }
        }
    }
}
