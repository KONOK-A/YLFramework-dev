using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Common
{
    /// <summary>为作者工具按资产文件名搜索同名项，生成带递增序号的候选名称，不创建或预留资源。</summary>
    public class AssetNameTools
    {


        /// <summary>
        /// 在指定目录及其子目录中避开同名资产；目录无效时原样返回预期名称。
        /// </summary>
        /// <param name="name">预期资源名称</param>
        /// <param name="forder">AssetDatabase 使用的项目相对目录。</param>
        /// <returns>未命中同名文件的候选名称，不含扩展名。</returns>
        public static string GetName(string name, string forder)
        {
            if (!AssetDatabase.IsValidFolder(forder))
            {
                return name;
            }

            string tempName = name;

            int count = 0;

            while (IsContainName(tempName, forder))
            {
                count++;
                tempName = string.Format("{0} ({1})", name, count);
            }

            return tempName;
        }

        /// <summary>在搜索结果中精确比较不含扩展名的文件名，排除仅部分匹配的结果。</summary>
        private static bool IsContainName(string name, string forder)
        {
            string[] matchs = AssetDatabase.FindAssets(name, new string[] { forder });

            foreach (var item in matchs)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(item);
                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                if (System.IO.Path.GetFileNameWithoutExtension(assetPath) == name)
                {
                    return true;
                }
            }

            return false;
        }


    }

}
