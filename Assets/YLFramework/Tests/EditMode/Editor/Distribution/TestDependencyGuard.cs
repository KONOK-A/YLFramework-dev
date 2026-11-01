using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor.PackageManager;
using UnityEngine;

namespace YLFramework.Tests
{
    internal static class TestDependencyGuard
    {
        internal static bool IsApprovedBouncyCastleReference(string path)
        {
            var approved = Path.Combine(Application.dataPath, "YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll");
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(approved), StringComparison.OrdinalIgnoreCase) || !File.Exists(approved))
            {
                return false;
            }
            using var stream = File.OpenRead(approved);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() ==
                "d61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495";
        }

        internal static bool IsAllowedReference(string path, string unityContents, string ui, bool editor, string json = null)
        {
            var absolute = Path.GetFullPath(path);
            if (IsApprovedBouncyCastleReference(absolute) ||
                (json != null && IsApprovedJsonReference(absolute, json)) ||
                string.Equals(absolute, Path.GetFullPath(ui), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var name = Path.GetFileName(absolute);
            if ((name == "UnityEngine.dll" || name.StartsWith("UnityEngine.", StringComparison.Ordinal)) && name != "UnityEngine.TestRunner.dll")
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "Managed/UnityEngine")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "PlaybackEngines")) ||
                    string.Equals(absolute, Path.GetFullPath(Path.Combine(unityContents, "Managed/UnityEngine.dll")), StringComparison.OrdinalIgnoreCase);
            }
            if (editor && (name == "UnityEditor.dll" || name.StartsWith("UnityEditor.", StringComparison.Ordinal)) &&
                name.EndsWith(".dll", StringComparison.Ordinal) && name != "UnityEditor.TestRunner.dll")
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "Managed/UnityEngine")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "Managed/UnityEditor")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "PlaybackEngines")) ||
                    (string.Equals(Path.GetDirectoryName(absolute), Path.GetFullPath(Path.Combine(unityContents, "Managed")),
                        StringComparison.OrdinalIgnoreCase) && File.Exists(absolute));
            }
            if (editor && name.StartsWith("Unity.", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.Ordinal))
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "PlaybackEngines")) && File.Exists(absolute);
            }
            if (name == "mscorlib.dll" || name == "netstandard.dll" || name == "Microsoft.CSharp.dll" ||
                name == "Microsoft.Win32.Primitives.dll" || name == "System.dll" || name.StartsWith("System.", StringComparison.Ordinal))
            {
                return IsInsideFolder(absolute, Path.Combine(unityContents, "NetStandard")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "UnityReferenceAssemblies")) ||
                    IsInsideFolder(absolute, Path.Combine(unityContents, "MonoBleedingEdge/lib/mono"));
            }
            return false;
        }

        private static bool IsApprovedJsonReference(string path, string json)
        {
            if (Path.GetFileName(path) != "Newtonsoft.Json.dll" || Path.GetFileName(json) != "Newtonsoft.Json.dll")
            {
                return false;
            }
            var package = PackageInfo.FindForAssetPath("Packages/com.unity.nuget.newtonsoft-json/Runtime/Newtonsoft.Json.dll");
            if (package == null || package.name != "com.unity.nuget.newtonsoft-json" || package.version != "3.2.1" ||
                package.source != PackageSource.Registry)
            {
                return false;
            }
            // 同一已解析官方包分别为 Editor 和 Player 提供 CLR/AOT 变体，不能靠文件名或缓存目录标签推断身份。
            var editor = Path.GetFullPath(Path.Combine(package.resolvedPath, "Runtime/Newtonsoft.Json.dll"));
            var aot = Path.GetFullPath(Path.Combine(package.resolvedPath, "Runtime/AOT/Newtonsoft.Json.dll"));
            var requested = Path.GetFullPath(json);
            return File.Exists(path) && File.Exists(requested) &&
                (string.Equals(path, editor, StringComparison.OrdinalIgnoreCase) || string.Equals(path, aot, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(requested, editor, StringComparison.OrdinalIgnoreCase) || string.Equals(requested, aot, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsAllowedSdkBinaryDependency(string parent, string reference, string unityContents)
        {
            var name = Path.GetFileName(reference);
            if (Array.IndexOf(new[] { "Newtonsoft.Json.dll", "BouncyCastle.Cryptography.dll", "UnityEngine.TestRunner.dll",
                "UnityEditor.TestRunner.dll", "nunit.framework.dll", "ICSharpCode.SharpZipLib.dll", "EPPlus.dll", "MiniExcel.dll" }, name) >= 0)
            {
                return false;
            }
            var folders = new[] { "Managed", "NetStandard", "UnityReferenceAssemblies", "MonoBleedingEdge/lib/mono", "PlaybackEngines" };
            bool IsSdkBinary(string path)
            {
                if (Path.GetExtension(path) != ".dll" || !File.Exists(path))
                {
                    return false;
                }
                foreach (var folder in folders)
                {
                    if (IsInsideFolder(path, Path.Combine(unityContents, folder)))
                    {
                        return true;
                    }
                }
                return false;
            }
            // SDK 内部边可以跨其私有支持库；这不授权产品直接引用它们或加载项目中的同名副本。
            return IsSdkBinary(parent) && IsSdkBinary(reference);
        }

        internal static bool IsInsideFolder(string path, string folder)
        {
            var prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
