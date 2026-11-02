using UnityEditor;
using UnityEngine;


namespace YLFramework.Editor.Foundation
{
    /// <summary>在 Editor Inspector 中只读展示 DisableEdit 标记的字段，不限制运行时代码写入。</summary>
    [CustomPropertyDrawer(typeof(YLFramework.Attributes.DisableEdit))]
    public class DisableEditDrawer : PropertyDrawer
    {


        /// <summary>禁用控件后绘制字段及其子字段，离开作用域时恢复原有 GUI 启用状态。</summary>
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.PropertyField(position, property, label, true);
            }
        }

        /// <summary>沿用包含子字段的原生属性高度，使只读绘制与展开布局一致。</summary>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label, true);
        }

    }
}
