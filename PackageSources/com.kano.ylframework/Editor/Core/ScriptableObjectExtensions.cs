using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Core
{

    /// <summary>把调用方新建的 ScriptableObject 保存到 YL 专用生成目录，拒绝移动或覆盖已有资产。</summary>
    public static class ScriptableObjectExtensions
    {
#if UNITY_EDITOR

        // Fix编码
        /// <summary>本工具允许写入的唯一资产根目录，可在其下创建子目录。</summary>
        public const string GeneratedAssetRoot = "Assets/YLFrameworkData/ScriptableObjects";

        /// <summary>
        /// 在YL专用生成目录下创建资源文件
        /// </summary>
        /// <param name="scriptableObject">尚未归属 AssetDatabase 的有效新实例。</param>
        public static void CreateAsset(this ScriptableObject scriptableObject)
        {
            scriptableObject.CreateAsset(GeneratedAssetRoot);
        }


        /// <summary>创建所需子目录，以实例类型名生成 .asset 文件并选中它；不注册创建操作的 Undo。</summary>
        /// <param name="scriptableObject">调用方创建的新实例；成功后成为资产，失败时本方法不销毁它。</param>
        /// <param name="path">生成根目录或其子目录的绝对/项目相对路径；若指向已有文件，使用其所在目录。</param>
        /// <remarks>路径必须位于专用根目录内，且不经过链接。失败前已创建的目录不会整体回滚。</remarks>
        /// <exception cref="ArgumentNullException">实例为空或已被销毁。</exception>
        /// <exception cref="InvalidOperationException">实例已经属于 AssetDatabase。</exception>
        /// <exception cref="ArgumentException">路径为空或超出允许的根目录。</exception>
        /// <exception cref="IOException">路径经过链接、已有内容阻挡创建，或创建目录失败。</exception>
        public static void CreateAsset(this ScriptableObject scriptableObject, string path)
        {
            if (!scriptableObject)
            {
                throw new ArgumentNullException(nameof(scriptableObject));
            }

            if (AssetDatabase.Contains(scriptableObject))
            {
                throw new InvalidOperationException("Create a new ScriptableObject instance; existing assets must not be moved or replaced.");
            }

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("An explicit YL generated asset path is required.", nameof(path));
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath) + Path.DirectorySeparatorChar;
            string root = Path.GetFullPath(Path.Combine(projectRoot, GeneratedAssetRoot));
            string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Assets may only be created under " + GeneratedAssetRoot, nameof(path));
            }

            // 拒绝任一层链接，避免表面位于专用目录的路径实际把写入重定向到其他资源。
            for (string parent = fullPath; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if ((Directory.Exists(parent) || File.Exists(parent)) &&
                    (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Linked asset paths are not supported: " + parent);
                }
            }

            path = fullPath.Substring(projectRoot.Length).Replace('\\', '/');
            if (File.Exists(fullPath))
            {
                if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The YL generated asset root must be a folder: " + path);
                }

                path = Path.GetDirectoryName(path).Replace('\\', '/');
            }

            string[] folders = path.Split('/');
            string folder = folders[0];
            for (int i = 1; i < folders.Length; i++)
            {
                string next = folder + "/" + folders[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (File.Exists(next) || File.Exists(next + ".meta") || Directory.Exists(next))
                    {
                        throw new IOException("Refusing to replace an existing or unimported asset: " + next);
                    }

                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(folder, folders[i])))
                    {
                        throw new IOException("Could not create YL asset folder: " + next);
                    }
                }
                folder = next;
            }

            string name = GetName(scriptableObject.GetType().Name, path);
            string assetPath = string.Format("{0}/{1}.asset", path, name);
            if (File.Exists(assetPath) || Directory.Exists(assetPath) || File.Exists(assetPath + ".meta") ||
                AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
            {
                throw new IOException("Refusing to overwrite an existing asset: " + assetPath);
            }

            AssetDatabase.CreateAsset(scriptableObject, assetPath);
            Selection.activeObject = scriptableObject;
        }


        /// <summary>按 ScriptableObject 搜索结果递增候选名称；最终文件及 .meta 占用由创建入口另行检查。</summary>
        private static string GetName(string name, string path)
        {
            string tempName = name;


            string[] matchs = AssetDatabase.FindAssets(string.Format("t:ScriptableObject {0}", tempName), new string[] { path });

            int count = 0;

            while (matchs != null && matchs.Length > 0)
            {
                count++;


                tempName = string.Format("{0} ({1})", name, count);
                matchs = AssetDatabase.FindAssets(string.Format("t:ScriptableObject {0}", tempName), new string[] { path });
            }


            return tempName;
        }

#endif
    }

}
