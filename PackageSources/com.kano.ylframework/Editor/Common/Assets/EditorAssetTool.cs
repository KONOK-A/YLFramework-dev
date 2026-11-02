using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Common
{
    /// <summary>将作者工具的正式资源地址接到 Resource Editor 预览解析器，不初始化 Runtime 资源系统。</summary>
    public class EditorAssetTool
    {
        /// <summary>由 Resource Editor 装配。参数依次为 ProjectId、正式地址、可选的数字 localId；结果只借用。</summary>
        public static Func<string, string, string, Object> PreviewResolver
        {
            get; set;
        }

        /// <summary>取得正式地址主资产的纹理或编辑器缩略图；返回内容只供借用，不应自行销毁。</summary>
        /// <param name="projectName">正式资源项目的 ProjectId，不是显示名称。</param>
        /// <param name="assetName">正式资源地址，不是仅用于搜索的文件名。</param>
        /// <returns>资源纹理或 Unity 提供的预览图；输入为空或无可用图片时为 null。</returns>
        public static Texture2D GetTexture2D(string projectName, string assetName)
            => GetTexture2D(projectName, assetName, null);

        /// <summary>解析主资产或指定子资产，优先直接借用纹理，否则请求预览图并回退到小缩略图。</summary>
        /// <param name="projectId">正式资源项目的 ProjectId。</param>
        /// <param name="address">正式资源地址。</param>
        /// <param name="subAsset">可选的数字 localId；null 表示主资产。</param>
        /// <returns>借用的纹理或缩略图；输入为空或无可用图片时为 null。</returns>
        /// <exception cref="InvalidOperationException">未装配解析器、解析器拒绝解析，或解析结果已不存在。</exception>
        public static Texture2D GetTexture2D(string projectId, string address, string subAsset)
        {
            if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(address))
            {
                return null;
            }

            var resolve = PreviewResolver ?? throw new InvalidOperationException("No YL Core Editor preview resolver is installed.");
            var asset = resolve(projectId, address, subAsset);
            if (!asset)
            {
                throw new InvalidOperationException("The formal preview address did not resolve an asset.");
            }

            return asset is Texture2D texture ? texture : AssetPreview.GetAssetPreview(asset) ?? AssetPreview.GetMiniThumbnail(asset);
        }

        /// <summary>为资源主资产创建图标内容；参数或解析状态错误时显示替代图标，并把原因放入提示。</summary>
        /// <param name="projectName">正式资源项目的 ProjectId。</param>
        /// <param name="assetName">正式资源地址。</param>
        /// <returns>本次创建的 GUIContent；其中图片仍由资产或 Unity 编辑器持有。</returns>
        public static GUIContent GetTextureGuiContent(string projectName, string assetName)
        {
            try
            {
                return new GUIContent(GetTexture2D(projectName, assetName) ?? EditorGUIUtility.IconContent("BuildSettings.Broadcom").image);
            }
            catch (ArgumentException error)
            {
                return Unresolved(error);
            }
            catch (InvalidOperationException error)
            {
                return Unresolved(error);
            }
        }

        /// <summary>将无法解析的原因附到替代图标上，供 Inspector 提示而非隐藏错误。</summary>
        private static GUIContent Unresolved(Exception error) => new GUIContent(
            EditorGUIUtility.IconContent("BuildSettings.Broadcom").image, "YL resource preview unresolved: " + error.Message);
    }
}
