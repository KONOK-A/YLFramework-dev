using System;
using UnityEditor;

namespace YLFramework.Editor.Core
{
    /// <summary>拦截 Unity 资产删除流程中的默认资源及其 Core 内祖先目录，不监控文件系统外部删除。</summary>
    public class ListenerFileChange : AssetModificationProcessor
    {
        /// <summary>按精确路径判断两个默认 Prefab、资源目录及 Core 子树内祖先是否受保护，不依赖资产已导入。</summary>
        public static bool IsProtectedDefaultResource(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            if (assetPath == ListenerInitOnLoad.CanvasPath || assetPath == ListenerInitOnLoad.EventSystemPath ||
                assetPath == ListenerInitOnLoad.ResourceRoot)
            {
                return true;
            }

            return (assetPath == "Packages/com.kano.ylframework/Runtime/Core" ||
                assetPath.StartsWith("Packages/com.kano.ylframework/Runtime/Core/", StringComparison.Ordinal)) &&
                ListenerInitOnLoad.ResourceRoot.StartsWith(assetPath + "/", StringComparison.Ordinal);
        }

        /// <summary>阻止受保护路径删除并提示用户；其他路径交还 Unity 处理，不主动删除或保存任何资产。</summary>
        public static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions option)
        {
            if (!IsProtectedDefaultResource(assetPath))
            {
                return AssetDeleteResult.DidNotDelete;
            }

            EditorUtility.DisplayDialog("YL Core Resource", "Required YL Core default resource cannot be deleted: " + assetPath, "OK");
            return AssetDeleteResult.FailedDelete;
        }
    }
}
