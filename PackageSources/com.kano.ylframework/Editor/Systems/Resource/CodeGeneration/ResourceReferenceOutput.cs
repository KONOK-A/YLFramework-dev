using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace YLFramework.Editor.Resource
{
    /// <summary>项目生成物的窄承载契约，不为未知业务源码创建/改写程序集。</summary>
    internal static class ResourceReferenceOutput
    {
        internal const string DirectoryPath = "Assets/YLFrameworkData/Generated/ResourceReferences";
        internal const string AssemblyName = "YLFramework.Generated.ResourceNames";
        internal const string SourceName = "YLResourceReferences.cs";
        private const string Header = "// YL Catalog references v1 SHA256: ";
        internal const string AssemblyDefinition = "{\n  \"name\": \"" + AssemblyName + "\",\n  \"references\": [],\n  \"autoReferenced\": true,\n  \"overrideReferences\": true,\n  \"precompiledReferences\": [],\n  \"noEngineReferences\": true\n}\n";

        /// <summary>
        /// 验证源码正文摘要、固定 asmdef 和目录闭包后写入受管常量；无变化返回 false。
        /// 摘要用于识别既有生成正文是否改变，不是签名或任意目录的接管许可；两份文件不构成跨文件事务。
        /// </summary>
        internal static bool Publish(string directory, string source)
        {
            directory = Path.GetFullPath(directory).TrimEnd('/', '\\');
            var content = Header + Hash(source) + "\n" + source;
            var path = Path.Combine(directory, SourceName);
            var asmdef = Path.Combine(directory, AssemblyName + ".asmdef");
            ResourceBuildOperation.ValidateAssetPath(directory);
            // 根级 asmdef/asmref 会改变新目录的身份；不能静默改为本工具的子程序集。
            var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
            while (parent != null && parent.Length >= Path.GetFullPath("Assets").Length)
            {
                if (!Directory.Exists(parent) && (File.Exists(parent) || File.Exists(parent + ".meta")))
                {
                    throw new InvalidOperationException("生成位置的上级目录被文件或未知孤立 meta 占用，拒绝接管：" + parent);
                }
                if (Directory.Exists(parent))
                {
                    foreach (var configuration in Directory.EnumerateFiles(parent).Where(file =>
                        file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (configuration.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) && JsonUtilityName(File.ReadAllText(configuration)) == AssemblyName)
                        {
                            throw new InvalidOperationException("生成程序集 " + AssemblyName + " 已存在于其他位置：" + configuration +
                                "\n保留原生成物及 .meta；请按 Documentation~/Migration-UPM.md 人工迁移，不能创建第二份同名 asmdef。");
                        }
                        throw new InvalidOperationException("生成位置已有上级程序集配置，请先确定消费者承载：" + configuration);
                    }
                }

                parent = Path.GetDirectoryName(parent);
            }
            if (File.Exists(directory))
            {
                throw new InvalidOperationException("生成目录被文件占用：" + directory);
            }

            var existed = Directory.Exists(directory);
            if (!existed && File.Exists(directory + ".meta"))
            {
                throw new InvalidOperationException("生成目录存在未知孤立 meta，拒绝接管：" + directory);
            }

            if (existed)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (entry != path && entry != asmdef && entry != path + ".meta" && entry != asmdef + ".meta")
                    {
                        throw new InvalidOperationException("生成目录含未知源码或资产，拒绝改变程序集身份：" + entry);
                    }
                }
            }

            var hadSource = File.Exists(path);
            var hadAssembly = File.Exists(asmdef);
            if (existed && !hadSource)
            {
                throw new InvalidOperationException("生成目录没有本工具的完整归属，拒绝接管：" + directory);
            }

            if (hadSource != hadAssembly || (!hadSource && (File.Exists(path + ".meta") || File.Exists(asmdef + ".meta"))))
            {
                throw new InvalidOperationException("生成物/程序集不完整或属于旧承载，请先确认迁移：" + directory);
            }

            if (hadSource)
            {
                var old = File.ReadAllText(path);
                var newline = old.IndexOf('\n');
                if (newline < 0 || old.Substring(0, newline) != Header + Hash(old.Substring(newline + 1)) ||
                    File.ReadAllText(asmdef) != AssemblyDefinition)
                {
                    throw new InvalidOperationException("已有源码被手写/修改或程序集配置不匹配，拒绝覆盖：" + directory);
                }
            }
            foreach (var guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
            {
                var other = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(asmdef), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<UnityEditorInternal.AssemblyDefinitionAsset>(other);
                if (!asset)
                {
                    throw new InvalidOperationException("程序集资产不可读取：" + other);
                }

                if (JsonUtilityName(asset.text) == AssemblyName)
                {
                    throw new InvalidOperationException("生成程序集 " + AssemblyName + " 已存在于其他位置：" + other +
                        "\n保留原生成物及 .meta；请按 Documentation~/Migration-UPM.md 人工迁移，不能创建第二份同名 asmdef。");
                }
            }
            // 无变化也必须先查程序集占用，不能因提前返回接受重复身份。
            if (hadSource && File.ReadAllText(path) == content)
            {
                return false;
            }

            var temporary = Path.Combine(directory, ".yl-names-" + Guid.NewGuid().ToString("N"));
            var assemblyTemporary = temporary + ".asmdef-tmp";
            var createdAssembly = false;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (!hadAssembly)
                {
                    File.WriteAllText(assemblyTemporary, AssemblyDefinition, new UTF8Encoding(false));
                    File.Move(assemblyTemporary, asmdef);
                    createdAssembly = true;
                }
                // 同目录暂存后替换文件本体，绝不删除/重建已有 .meta。
                if (hadSource)
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return true;
            }
            catch
            {
                if (createdAssembly)
                {
                    File.Delete(asmdef);
                }

                throw;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }

                if (File.Exists(assemblyTemporary))
                {
                    File.Delete(assemblyTemporary);
                }

                if (!existed && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }

        /// <summary>计算 UTF-8 正文的 SHA-256 并编码为生成头使用的 Base64。</summary>
        private static string Hash(string text)
        {
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>只读取 asmdef 名称的最小 JSON 映射，不改写程序集定义。</summary>
        [Serializable]
        private sealed class AssemblyIdentity
        {
            /// <summary>asmdef JSON 中原样映射的程序集名称。</summary>
            public string name;
        }
        /// <summary>从程序集 JSON 中提取名称以检测其他位置的身份占用。</summary>
        private static string JsonUtilityName(string json) => UnityEngine.JsonUtility.FromJson<AssemblyIdentity>(json)?.name;
    }
}
