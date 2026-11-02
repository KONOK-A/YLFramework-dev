using UnityEditor;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Editor.Common;

namespace YLFramework.Editor.DataAssets
{
    /// <summary>绘制图标资源定位字段及编辑器预览，不为运行时持有图标资源租约。</summary>
    [CustomPropertyDrawer(typeof(IconInfo))]
    public sealed class IconInfoDrawer : PropertyDrawer
    {
        /// <summary>为标题、项目标识和地址行预留固定绘制高度。</summary>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight * 3 + 6;

        /// <summary>在序列化属性作用域中编辑定位字段，点击有图像的预览时定位该编辑器资产。</summary>
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.PropertyScope(position, label, property))
            {
                var line = EditorGUIUtility.singleLineHeight;
                EditorGUI.LabelField(new Rect(position.x, position.y, position.width, line), label);
                var project = property.FindPropertyRelative("projectName");
                var address = property.FindPropertyRelative("iconName");
                var width = Mathf.Max(0, position.width - 48);
                EditorGUI.PropertyField(new Rect(position.x, position.y + line + 2, width, line), project, new GUIContent("ProjectId"));
                EditorGUI.PropertyField(new Rect(position.x, position.y + line * 2 + 4, width, line), address, new GUIContent("Address"));
                var preview = EditorAssetTool.GetTextureGuiContent(project.stringValue, address.stringValue);
                if (GUI.Button(new Rect(position.xMax - 42, position.y + line + 2, 40, 40), preview) && preview.image)
                {
                    EditorGUIUtility.PingObject(preview.image);
                }
            }
        }
    }
}
