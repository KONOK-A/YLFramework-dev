using UnityEditor;
using YLFramework.Attributes;
namespace YLFramework.Editor.Equipment
{
    /// <summary>只读显示装备定义的所属配表，归属变化由作者操作维护。</summary>
    internal sealed class CustomEquipmentConfigDataParentGui : CustomPropertyGui
    {
        /// <summary>在禁用编辑的控件组中绘制所属配表引用。</summary>
        public override void OnPropertyGui(SerializedProperty property)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(property);
            }
        }
    }
}
