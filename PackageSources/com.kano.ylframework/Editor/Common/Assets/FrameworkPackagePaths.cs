using System;
using System.IO;
using System.Reflection;

namespace YLFramework.Editor.Common
{
    /// <summary>按实际安装身份区分 Unity 资产路径与包的只读磁盘路径，不猜测缓存目录。</summary>
    public static class FrameworkPackagePaths
    {
        /// <summary>使用工程拥有的配置与生成输出根，不属于安装包。</summary>
        public const string UserDataRoot = "Assets/YLFrameworkData";

        /// <summary>返回相对于所属包的 Unity 虚拟资产路径。</summary>
        public static string AssetPath(Assembly ownerAssembly, string relativePath)
        {
            var package = Package(ownerAssembly);
            return Combine(package.assetPath, relativePath, false);
        }

        /// <summary>返回相对于所属包的物理路径，也可读取隐藏的文档及未导入示例。</summary>
        public static string FullPath(Assembly ownerAssembly, string relativePath)
        {
            var package = Package(ownerAssembly);
            return Combine(package.resolvedPath, relativePath, true);
        }

        private static UnityEditor.PackageManager.PackageInfo Package(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }
            return UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly) ??
                throw new InvalidOperationException("Assembly is not installed as a UPM package: " + assembly.GetName().Name);
        }

        private static string Combine(string root, string relative, bool physical)
        {
            if (relative == null)
            {
                throw new ArgumentNullException(nameof(relative));
            }
            var path = relative.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || path.StartsWith("/", StringComparison.Ordinal) || path.Contains(":"))
            {
                throw new ArgumentException("A package-relative path is required.", nameof(relative));
            }
            foreach (var segment in path.Split('/'))
            {
                if (segment == "." || segment == "..")
                {
                    throw new ArgumentException("Package paths must not escape their owner.", nameof(relative));
                }
            }
            return physical ? Path.GetFullPath(Path.Combine(root, path)) : root.TrimEnd('/') + (path.Length == 0 ? "" : "/" + path);
        }
    }
}
