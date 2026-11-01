#if UNITY_EDITOR

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Distribution
{
    /// <summary>按固定产品根导出框架源码、资源及随附许可，不遍历整个宿主工程。</summary>
    /// <remarks>根外依赖和可选集成须另行配置；导出不重建或核验随附 DLL 与源码的一致性。</remarks>
    public static class ExportUnityPackage
    {
        private const string FrameworkRoot = "Assets/YLFramework";
        private const string PackageVersion = "1.0.0";

        // 只递归分发产品根；商业 Editor Enhancers 留在宿主工程，不随框架再分发。
        private static readonly string[] _exportRoots =
        {
            "Assets/YLFramework/Editor",
            "Assets/YLFramework/Plugins/BouncyCastle",
            "Assets/YLFramework/Prefabs",
            "Assets/YLFramework/Runtime",
            "Assets/YLFramework/Samples",
            "Assets/YLFramework/Documentation",
            "Assets/YLFramework/LICENSE.md",
        };

        /// <summary>向明确的现有本地父目录导出固定框架根；不保存资产、创建目录或自动覆盖已有交付。</summary>
        /// <param name="packagePath">父目录已存在的本地绝对 .unitypackage 文件路径。</param>
        /// <param name="overwrite">仅在调用方已确认目标归属及覆盖时显式启用。</param>
        /// <remarks>
        /// 要求 Editor 不在运行、切换运行、编译或导入中，且目标及现存祖先不是链接。
        /// 成功返回仅确认包文件存在且非空，不替代包内容、依赖闭合、导入或运行验证。
        /// </remarks>
        /// <exception cref="ArgumentException">目标不是绝对本地 .unitypackage 路径，或为网络、设备路径。</exception>
        /// <exception cref="DirectoryNotFoundException">目标父目录不存在。</exception>
        /// <exception cref="IOException">路径经过链接、目标为目录、已有文件未授权覆盖，或未生成非空包文件。</exception>
        /// <exception cref="InvalidOperationException">Editor 尚未处于空闲状态。</exception>
        /// <exception cref="FileNotFoundException">固定导出根缺失。</exception>
        public static void Export(string packagePath, bool overwrite = false)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !Path.IsPathFullyQualified(packagePath) ||
                !string.Equals(Path.GetExtension(packagePath), ".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Select an explicit absolute local .unitypackage destination.", nameof(packagePath));
            }

            var full = Path.GetFullPath(packagePath);
            if (full.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new ArgumentException("Network and device export paths are not supported.", nameof(packagePath));
            }

            if (!Directory.Exists(Path.GetDirectoryName(full)))
            {
                throw new DirectoryNotFoundException("The explicit export parent must already exist.");
            }

            for (var cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Refusing linked export paths.");
                }
            }

            if (Directory.Exists(full) || File.Exists(full) && !overwrite)
            {
                throw new IOException("The export destination already exists.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Export requires an idle Editor.");
            }

            foreach (var root in _exportRoots)
            {
                if (!AssetDatabase.LoadMainAssetAtPath(root))
                {
                    throw new FileNotFoundException("Missing framework export root.", root);
                }
            }

            // 不启用 IncludeDependencies，避免引用把未授权分发的宿主内容带入包。
            AssetDatabase.ExportPackage(_exportRoots, full, ExportPackageOptions.Recurse);
            if (!File.Exists(full) || new FileInfo(full).Length == 0)
            {
                throw new IOException("No complete framework package was produced.");
            }
        }

        /// <summary>保存全部脏资产后尝试向工程根导出版本化包，仅交互模式询问同名覆盖。</summary>
        [MenuItem("YLFramework/Toolkit/导出框架包", priority = 1)]
        private static void MenuClicked()
        {
            if (!AssetDatabase.IsValidFolder(FrameworkRoot))
            {
                Debug.LogError("导出框架包失败：找不到框架目录 " + FrameworkRoot);
                return;
            }

            var hasMissingRoot = false;
            foreach (var exportRoot in _exportRoots)
            {
                if (!AssetDatabase.LoadMainAssetAtPath(exportRoot))
                {
                    Debug.LogError("导出框架包失败：找不到导出资源 " + exportRoot);
                    hasMissingRoot = true;
                }
            }

            if (hasMissingRoot)
            {
                return;
            }

            AssetDatabase.SaveAssets();

            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var packageName = $"YLFramework_{PackageVersion}_{DateTime.Now:yyyyMMdd_HHmmss}.unitypackage";
            var packagePath = Path.Combine(projectRoot, packageName);

            if (File.Exists(packagePath) && !Application.isBatchMode)
            {
                if (!EditorUtility.DisplayDialog("覆盖确认", "已存在同名包文件，是否覆盖？\n" + packagePath, "覆盖", "取消"))
                {
                    return;
                }
            }

            try
            {
                Export(packagePath, overwrite: true);
            }
            catch (Exception e)
            {
                Debug.LogError("导出框架包失败：" + e);
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("导出失败", "导出框架包时发生异常：\n" + e.Message, "确定");
                }
                return;
            }

            if (!File.Exists(packagePath) || new FileInfo(packagePath).Length <= 0)
            {
                Debug.LogError("导出框架包失败：未生成有效的包文件 " + packagePath);
                return;
            }

            GUIUtility.systemCopyBuffer = packageName;
            Debug.Log("已导出框架的Unity Package：" + packagePath);
            EditorApplication.ExecuteMenuItem("YLFramework/Toolkit/打开目录/Unity Package");
        }
    }
}

#endif
