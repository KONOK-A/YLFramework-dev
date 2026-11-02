using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Common;

namespace YLFramework.Editor.Domain
{
    /// <summary>领域配置图标的共享绘制器，选择正式资源项目身份并借用现有解析器显示预览。</summary>
    /// <remarks>
    /// 仅编辑 ProjectId 与资源地址，不把输入拼成输出文件路径或自行写盘；预览解析与资产校验由 Resource 入口负责。
    /// 序列化属性的生命周期由调用 Inspector 管理，预览纹理只借用，不承担销毁或卸载责任。
    /// </remarks>
    internal sealed class DomainResourceIconGui : YLFramework.Attributes.CustomPropertyGui
    {
        /// <summary>绘制图标字段和项目下拉菜单，选择后提交序列化修改，点击预览只定位资产。</summary>
        /// <param name="property">包含 projectName 与 iconName 子字段的当前序列化属性，由宿主 Inspector 准备。</param>
        public override void OnPropertyGui(SerializedProperty property)
        {
            EditorGUILayout.PropertyField(property, true);
            var project = property.FindPropertyRelative("projectName");
            var icon = property.FindPropertyRelative("iconName");
            if (EditorGUILayout.DropdownButton(new GUIContent("选择YL资源项目"), FocusType.Passive))
            {
                var menu = new GenericMenu();
                foreach (var guid in AssetDatabase.FindAssets("t:ResourceProject"))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<YLFramework.Editor.Resource.ResourceProject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (!asset)
                    {
                        continue;
                    }

                    var id = asset.projectId;
                    menu.AddItem(new GUIContent(id), id == project.stringValue, () =>
                    {
                        project.stringValue = id;
                        // 通过序列化提交接入 Undo，而不是直接修改资产对象或保存整个项目。
                        project.serializedObject.ApplyModifiedProperties();
                    });
                }
                menu.ShowAsContext();
            }
            if (!string.IsNullOrEmpty(project.stringValue) && !string.IsNullOrEmpty(icon.stringValue))
            {
                var preview = EditorAssetTool.GetTextureGuiContent(project.stringValue, icon.stringValue);
                if (GUILayout.Button(preview, GUILayout.Width(48), GUILayout.Height(48)))
                {
                    EditorGUIUtility.PingObject(preview.image);
                }
            }
            EditorGUILayout.HelpBox("projectName填写YL Resource ProjectId，iconName填写资源地址。预览使用既有Resource预览解析器。", MessageType.Info);
        }
    }
}
