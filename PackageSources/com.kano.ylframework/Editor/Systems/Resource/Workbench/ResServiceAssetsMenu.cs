using UnityEditor;

namespace YLFramework.Editor.Resource
{
    /// <summary>Project 资产的工作台入口；退出全局 Importer 标记写入菜单。</summary>
    public static class ResServiceAssetsMenu
    {
        /// <summary>打开工作台并沿用当前所选的 ResourceProject，不直接修改资源定义。</summary>
        [MenuItem("Assets/YLFramework/Resource Workbench")]
        public static void OpenWorkbench() => ResServiceEditorWindow.OpenResServiceWindow();
        /// <summary>仅在 Project 窗口恰好选中一个资源项目时启用此菜单。</summary>
        [MenuItem("Assets/YLFramework/Resource Workbench", true)]
        private static bool ValidateWorkbench() => Selection.objects.Length == 1 && Selection.activeObject is ResourceProject;
    }
}
