using UnityEditor;
using UnityEngine;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
namespace YLFramework.Editor.Equipment
{
    /// <summary>维护装备显示图标的独立资源地址，并通过 Editor 资产工具显示预览。</summary>
    internal sealed class CustomEquipmentConfigDataIconGui : CustomPropertyGui
    {
        /// <summary>绘制图标资源项目与路径，二者非空时尝试显示纹理预览。</summary>
        public override void OnPropertyGui(SerializedProperty property)
        {
            EditorGUILayout.LabelField("Icon", EditorStyles.boldLabel);
            var project = property.FindPropertyRelative("projectName");
            var path = property.FindPropertyRelative("iconName");
            EquipmentAuthoring.DrawProject(project);
            EquipmentAuthoring.DrawAddress(project, path, typeof(UnityEngine.Object));
            if (!string.IsNullOrEmpty(project.stringValue) && !string.IsNullOrEmpty(path.stringValue))
            {
                GUILayout.Label(EditorAssetTool.GetTextureGuiContent(project.stringValue, path.stringValue), GUILayout.Width(48), GUILayout.Height(48));
            }
        }
    }
}
