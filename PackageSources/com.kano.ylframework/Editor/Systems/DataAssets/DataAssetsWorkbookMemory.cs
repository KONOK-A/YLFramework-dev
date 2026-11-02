using System;
using System.IO;
using UnityEditor;
using YLFramework.DataAssets;

namespace YLFramework.Editor.DataAssets
{
    /// <summary>
    /// 按资产GUID记住最近导出的工作簿，只保存在本机 EditorPrefs，不写入配置资产或仓库。
    /// 资产改名/移动仍沿用同一GUID；删除资产后可调用 Forget 清理。
    /// </summary>
    public static class DataAssetsWorkbookMemory
    {
        private const string Prefix = "YLFramework.DataAssets.LastWorkbook.";

        /// <summary>将工作簿路径规范化为绝对路径并记入根的本机偏好，不要求文件已存在。</summary>
        /// <param name="root">能解析出资产 GUID 的配置根。</param>
        /// <param name="path">扩展名为 .xlsx 的非空路径。</param>
        /// <returns>已记录时为真；无法取得根键或扩展名不符时为假，路径规范化错误仍会抛出。</returns>
        public static bool Remember(ScriptableObjectConfigs root, string path)
        {
            if (!TryKey(root, out var key) || !IsWorkbookPath(path))
            {
                return false;
            }

            EditorPrefs.SetString(key, Path.GetFullPath(path));
            return true;
        }

        /// <summary>读取根记住的工作簿路径，仅检查非空及扩展名，不检查文件是否存在。</summary>
        /// <param name="root">用于取得本机偏好键的配置根。</param>
        /// <param name="path">成功时为保存的路径，未找到有效记录时为空引用。</param>
        public static bool TryGetPath(ScriptableObjectConfigs root, out string path)
        {
            path = null;
            if (!TryKey(root, out var key))
            {
                return false;
            }

            var stored = EditorPrefs.GetString(key, string.Empty);
            if (!IsWorkbookPath(stored))
            {
                return false;
            }

            path = stored;
            return true;
        }

        /// <summary>检查根是否记住了工作簿路径，且该路径当前存在文件。</summary>
        public static bool CanReveal(ScriptableObjectConfigs root) => TryGetPath(root, out var path) && File.Exists(path);

        /// <summary>仅在记住的工作簿仍存在时调用系统文件定位，不创建文件或修复失效路径。</summary>
        /// <returns>已发出定位调用时为真，缺少有效记录或文件时为假。</returns>
        public static bool Reveal(ScriptableObjectConfigs root)
        {
            if (!TryGetPath(root, out var path) || !File.Exists(path))
            {
                return false;
            }

            EditorUtility.RevealInFinder(path);
            return true;
        }

        /// <summary>按根当前可解析的 GUID 删除本机路径记录，不删除工作簿文件。</summary>
        public static void Forget(ScriptableObjectConfigs root)
        {
            if (TryKey(root, out var key))
            {
                EditorPrefs.DeleteKey(key);
            }
        }

        /// <summary>由存活根的资产 GUID 构造本机偏好键，不以可变名称或路径作为记录身份。</summary>
        private static bool TryKey(ScriptableObjectConfigs root, out string key)
        {
            key = null;
            if (!root)
            {
                return false;
            }

            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(root));
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }

            key = Prefix + guid;
            return true;
        }

        /// <summary>按非空及不区分大小写的 .xlsx 扩展名识别候选路径，不访问文件内容。</summary>
        private static bool IsWorkbookPath(string path) =>
            !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);
    }
}
