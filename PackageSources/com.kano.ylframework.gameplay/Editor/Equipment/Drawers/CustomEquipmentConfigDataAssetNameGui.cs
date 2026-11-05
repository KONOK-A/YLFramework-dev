using UnityEditor;
using UnityEngine;
using YLFramework.Attributes;
using YLFramework.Equipment;
namespace YLFramework.Editor.Equipment
{
    /// <summary>为 Asset 模式选择装备 Prefab 的正式资源地址，不编辑 Script 模式的字段。</summary>
    internal sealed class CustomEquipmentConfigDataAssetNameGui : CustomPropertyGui
    {
        /// <summary>仅在 Asset 模式绘制 GameObject 地址选项，组件数量由运行时创建入口检查。</summary>
        public override void OnPropertyGui(SerializedProperty property)
        {
            if (property.serializedObject.FindProperty("equipmentType").intValue != (int)EquipmentType.Asset)
            {
                return;
            }

            EquipmentAuthoring.DrawAddress(property.serializedObject.FindProperty("projectName"), property, typeof(GameObject));
            EditorGUILayout.HelpBox("Prefab 根节点必须有且仅有一个 IEquipment；运行前初始化 ResourceSystem。", MessageType.Info);
        }
    }
}
