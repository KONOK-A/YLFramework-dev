using UnityEditor;
using UnityEditor.AddressableAssets.Settings;

namespace YLFramework.Editor.Resource
{
    /// <summary>官方 Addressables 配置的导航入口，不写 Importer 或旧 Project。</summary>
    public static class ResServiceAssetsMenu
    {
        /// <summary>打开官方配置增强窗口，不创建或修改资源定义。</summary>
        [MenuItem("Assets/YLFramework/Resource Workbench")]
        public static void OpenWorkbench() => ResServiceEditorWindow.OpenResServiceWindow();
        /// <summary>只接受官方设置或分组，不把旧 Project 当作新版作者事实。</summary>
        [MenuItem("Assets/YLFramework/Resource Workbench", true)]
        private static bool ValidateWorkbench() => Selection.objects.Length == 1 &&
            (Selection.activeObject is AddressableAssetSettings || Selection.activeObject is AddressableAssetGroup);
    }
}
