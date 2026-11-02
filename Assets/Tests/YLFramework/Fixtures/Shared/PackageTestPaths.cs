using System;
using System.IO;

namespace YLFramework.Testing
{
    /// <summary>File-system assertions must address the installed package, not Unity's virtual asset path.</summary>
    public static class PackageTestPaths
    {
        public static string MainSourceRoot(string repository)
        {
#if UNITY_EDITOR
            return Physical("Packages/com.kano.ylframework");
#else
            return Path.GetFullPath(Path.Combine(repository, "PackageSources/com.kano.ylframework"));
#endif
        }

        public static string Physical(string path)
        {
            if (path == null || !path.Replace('\\', '/').StartsWith("Packages/", StringComparison.Ordinal))
            {
                return path;
            }
            var normalized = path.Replace('\\', '/');
            if (normalized == "Packages/manifest.json" || normalized == "Packages/packages-lock.json")
            {
                return path;
            }
#if UNITY_EDITOR
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalized);
            if (package == null)
            {
                throw new InvalidOperationException("Test input has no installed package identity: " + normalized);
            }
            var relative = normalized.Substring(package.assetPath.Length).TrimStart('/');
            if (relative.Split('/').Length > 0 && Array.Exists(relative.Split('/'), part => part == ".." || part == "."))
            {
                throw new ArgumentException("Test package input escapes its owner.", nameof(path));
            }
            return Path.GetFullPath(Path.Combine(package.resolvedPath, relative));
#else
            throw new InvalidOperationException("Package file-system assertions require an Editor host.");
#endif
        }
    }
}
