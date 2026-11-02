using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>按图片来源展示 ResourceImage 配置，并提供运行时请求状态与显式刷新入口。</summary>
    [CustomEditor(typeof(ResourceImage)), CanEditMultipleObjects]
    internal sealed class ResourceImageEditor : UnityEditor.Editor
    {
        /// <summary>绘制来源相关字段；运行时操作仍由组件管理租约，不接管原有图片资源。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("target"), new GUIContent("显示组件", "选择 Image 或 RawImage；为空时使用同一 GameObject 上的组件。"));
            var source = serializedObject.FindProperty("source");
            EditorGUILayout.PropertyField(source, new GUIContent("图片来源", "Location 使用 URL 或本地文件，Catalog 使用资源地址，Atlas 使用图集地址和 Sprite 名称。"));
            if (source.hasMultipleDifferentValues || source.enumValueIndex == (int)ResourceImage.ImageSource.Location)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("location"), new GUIContent("URL / 本地文件", "填写完整 URL 或本地图片文件的绝对路径。"));
            }

            if (source.hasMultipleDifferentValues || source.enumValueIndex != (int)ResourceImage.ImageSource.Location)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("projectId"), new GUIContent("资源项目标识", "已挂载资源目录中的 ProjectId。"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("address"), new GUIContent("资源地址", "资源配置中登记的加载地址，不是源文件路径。"));
                if (source.hasMultipleDifferentValues || source.enumValueIndex == (int)ResourceImage.ImageSource.Catalog)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("subAsset"), new GUIContent("子资源键（可选）", "须与工作台中登记的子资源键一致；留空时使用主资源。"));
                }

                if (source.hasMultipleDifferentValues || source.enumValueIndex == (int)ResourceImage.ImageSource.Atlas)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("spriteName"), new GUIContent("图集 Sprite 名称", "填写图集中唯一的 Sprite 名称；图集来源只支持 Image。"));
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("loadOnEnable"), new GUIContent("启用时加载", "组件启用时自动发起图片请求。"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("setNativeSize"), new GUIContent("使用原始尺寸", "如布局由 LayoutGroup 控制，通常不启用。"));
            serializedObject.ApplyModifiedProperties();

            if (targets.Length != 1)
            {
                return;
            }

            var image = (ResourceImage)target;
            var graphic = image.Target ? image.Target : image.GetComponent<Graphic>();
            if (!(graphic is Image) && !(graphic is RawImage))
            {
                EditorGUILayout.HelpBox("请选择 Image 或 RawImage。组件只释放自己取得的租约，不销毁显示组件原有的 Texture 或 Sprite。", MessageType.Warning);
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("应用启动时需先初始化 ResourceSystem。URL 和本地图片无需 Catalog；资源目录支持 Texture2D 和 Sprite。图集使用明确地址和唯一 Sprite 名称，只支持 Image。组件停用或销毁时归还租约，并销毁自己创建的克隆。", MessageType.Info);
                return;
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("图片请求", ResourceEditorText.RequestStatus(image.Status), EditorStyles.boldLabel);
            if (image.Error != null)
            {
                EditorGUILayout.HelpBox("图片加载未完成。错误详情：\n" + ResourceEditorText.FailureDetails(image.Error), MessageType.Error);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("重新加载"))
                {
                    foreach (ResourceImage value in targets)
                    {
                        value.Refresh();
                    }
                }

                if (GUILayout.Button("清除图片"))
                {
                    foreach (ResourceImage value in targets)
                    {
                        value.Clear();
                    }
                }
            }
            if (image.IsLoading)
            {
                Repaint();
            }
        }
    }
}
