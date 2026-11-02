using UnityEditor;
using YLFramework.Attributes;
using YLFramework.Equipment;
namespace YLFramework.Editor.Equipment
{
    /// <summary>为 Asset 装备定义选择 Prefab 所属资源项目，Script 模式不显示该字段。</summary>
    internal sealed class CustomEquipmentConfigDataProjectNameGui : CustomPropertyGui
    {
        /// <summary>仅在 Asset 模式绘制资源项目字段与项目选择菜单。</summary>
        public override void OnPropertyGui(SerializedProperty property)
        {
            if (property.serializedObject.FindProperty("equipmentType").intValue == (int)EquipmentType.Asset)
            {
                EquipmentAuthoring.DrawProject(property);
            }
        }
    }
}
