using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Behavior.Acceptance
{
    public static class BehaviorUnityBatch
    {
        [Serializable]
        private sealed class FileRecord
        {
            public string path;
            public string sha256;
            public string guid;
        }
        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        [Serializable]
        private sealed class Cleanup
        {
            public string[] folders;
            public FileRecord[] archived;
        }

        public static void ArchiveAndCleanGenerated()
        {
            if (!Application.isBatchMode || EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Use isolated A EditMode BatchMode.");
            }

            string output = Output();
            const string Parent = "Assets/YLFramework/Tests/Fixtures/Extensions/Behavior";
            var folders = Directory.GetDirectories(Parent, "GeneratedAcceptance_*", SearchOption.TopDirectoryOnly).Select(path => path.Replace('\\', '/')).ToArray();
            var files = new List<FileRecord>();
            string archive = Path.Combine(output, "fixture-archive");
            Directory.CreateDirectory(archive);
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^GeneratedAcceptance_[0-9a-f]{32}$"))
                {
                    throw new InvalidOperationException("Unexpected generated folder name: " + folder);
                }

                foreach (string path in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Concat(File.Exists(folder + ".meta") ? new[] { folder + ".meta" } : Array.Empty<string>()))
                {
                    string relative = path.Replace('\\', '/').Substring(Parent.Length + 1);
                    string target = Path.Combine(archive, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(path, target, false);
                    files.Add(new FileRecord { path = path.Replace('\\', '/'), sha256 = Hash(path), guid = AssetDatabase.AssetPathToGUID(path.EndsWith(".meta", StringComparison.Ordinal) ? path.Substring(0, path.Length - 5) : path) });
                }
            }
            // 仅删除上面已逐文件归档的本入口GUID临时目录，不扫描/删除其他用户内容。
            foreach (string folder in folders)
            {
                if (!AssetDatabase.DeleteAsset(folder))
                {
                    throw new InvalidOperationException("Could not remove owned temporary fixture: " + folder);
                }
            }

            File.WriteAllText(Path.Combine(output, "fixture-cleanup.json"), JsonUtility.ToJson(new Cleanup { folders = folders, archived = files.ToArray() }, true), new UTF8Encoding(false));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"T3-A archived {files.Count} generated fixture files and removed {folders.Length} owned temporary folders.");
        }

        internal static string Output()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-behaviorOutput");
            if (index < 0 || index + 1 >= args.Length)
            {
                throw new ArgumentException("Missing -behaviorOutput.");
            }

            string path = Path.GetFullPath(args[index + 1]);
            string parent = Path.GetFullPath("C:/Users/Kano/AppData/Local/Temp/kilo/BA26") + Path.DirectorySeparatorChar;
            if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
            {
                throw new InvalidOperationException("Use the existing dedicated A run directory.");
            }

            return path;
        }
    }
}
