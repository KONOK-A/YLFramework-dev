using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Attributes;
namespace YLFramework.Editor.Equipment
{
    /// <summary>延迟编辑装备 ID，并用项目内其他定义的占用情况决定是否接受输入。</summary>
    internal sealed class CustomEquipmentConfigDataIdGui : CustomPropertyGui
    {
        private string _error;
        /// <summary>排除当前目标后检查全项目 ID 冲突，仅写入合法候选值，应用与保存交由检查器。</summary>
        public override void OnPropertyGui(SerializedProperty property)
        {
            var id = EditorGUILayout.DelayedIntField("ID", property.intValue);
            if (id != property.intValue)
            {
                var target = property.serializedObject.targetObject;
                var duplicate = EquipmentsConfigController.GetAllEquipmentsConfig().SelectMany(c => c.Equipments).Any(d => d && d != target && d.id == id);
                _error = duplicate ? "装备 ID 已占用: " + id : null;
                if (!duplicate)
                {
                    property.intValue = id;
                }
            }
            if (_error != null)
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }
        }
    }
}
