using UnityEditor;
using YLFramework.DataAssets;

namespace YLFramework.Editor.DataAssets
{
    /// <summary>将编辑器模式切换与程序集重载接入配置责任域清理，不保存或删除配置资产文件。</summary>
    [InitializeOnLoad]
    internal static class DataAssetsEditorLifetime
    {
        /// <summary>订阅离开编辑模式、离开运行模式、回到编辑模式及重载前的配置清理通知。</summary>
        static DataAssetsEditorLifetime()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                {
                    ScriptableObjectManager.Clear();
                }
            };
            AssemblyReloadEvents.beforeAssemblyReload += ScriptableObjectManager.Clear;
        }
    }
}
