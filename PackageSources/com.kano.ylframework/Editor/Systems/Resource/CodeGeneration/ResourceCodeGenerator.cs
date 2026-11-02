using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;
using UnityEditor;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>根据正式 Catalog 生成消费者常量；不读取旧 ResData 或 Importer 标记。</summary>
    public static class ResourceCodeGenerator
    {
        /// <summary>
        /// 从明确提供的正式 Catalog 集合生成独立常量程序集。值仅含 ProjectId、地址、alias、子资产 key 和 GroupId。
        /// 业务 asmdef 引用 YLFramework.Generated.ResourceNames；生成本身不等于 Unity 编译或 Runtime 挂载。
        /// 默认写入独立叶目录；显式目录保持原义，旧位置的同名程序集必须先人工迁移，不能重复创建。
        /// 返回 false 表示字节未变；碰撞/篡改/IO 失败抛异常，不改 Bundle 或发布版本。
        /// </summary>
        public static bool GenerateReferences(IEnumerable<ResourceCatalogDocument> catalogs, string directory = ResourceReferenceOutput.DirectoryPath, string @namespace = "YLFramework.Resources")
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("请在非播放、非编译、非导入状态生成资源常量。");
            }

            return ResourceReferenceOutput.Publish(directory, GenerateCatalogs(catalogs, @namespace));
        }

        /// <summary>验证明确 Catalog 集合及已加载类型的命名冲突，按稳定顺序生成源码文本，不写文件。</summary>
        internal static string GenerateCatalogs(IEnumerable<ResourceCatalogDocument> catalogs, string @namespace)
        {
            if (catalogs == null)
            {
                throw new ArgumentNullException(nameof(catalogs));
            }

            using var provider = new CSharpCodeProvider();
            if (string.IsNullOrWhiteSpace(@namespace) || @namespace.Contains('@') || @namespace.Split('.').Any(x => !provider.IsValidIdentifier(x)))
            {
                throw new ArgumentException("无效的 C# namespace：" + @namespace, nameof(@namespace));
            }

            var inputs = catalogs.ToArray();
            if (inputs.Length == 0 || inputs.Length > 16384 || inputs.Any(x => x == null))
            {
                throw new InvalidOperationException("需要明确、非空的正式 Catalog 集合。");
            }

            var occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(x => x.GetName().Name != ResourceReferenceOutput.AssemblyName))
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.FullName != null)
                    {
                        occupied.Add(type.FullName);
                    }

                    for (var space = type.Namespace; space != null && space.StartsWith(@namespace + ".", StringComparison.Ordinal); space = space.Substring(0, space.LastIndexOf('.')))
                    {
                        occupied.Add(space);
                    }
                }
            }

            var prefix = "";
            foreach (var part in @namespace.Split('.'))
            {
                prefix = prefix.Length == 0 ? part : prefix + "." + part;
                if (occupied.Contains(prefix))
                {
                    throw new InvalidOperationException("namespace 与已有类型冲突：" + prefix);
                }
            }
            var classes = new HashSet<string>(StringComparer.Ordinal);
            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder("// 来自正式 YL Resource Catalog 的生成常量，请勿手改。\nnamespace " + @namespace + "\n{\n");
            string Literal(string value)
            {
                using var writer = new StringWriter(CultureInfo.InvariantCulture);
                provider.GenerateCodeFromExpression(new CodePrimitiveExpression(value), writer, new CodeGeneratorOptions());
                return writer.ToString();
            }
            void Constant(string indent, string member, string value) => text.Append(indent).Append("public const string ").Append(member).Append(" = ").Append(Literal(value)).Append(";\n");
            foreach (var catalog in inputs.OrderBy(x => x.ProjectId, StringComparer.Ordinal))
            {
                var name = Identifier(catalog.ProjectId, provider);
                if (!projects.Add(catalog.ProjectId) || !classes.Add(name) || occupied.Contains(@namespace + "." + name))
                {
                    throw new InvalidOperationException("Project 常量类与项目/类型/namespace 冲突：" + catalog.ProjectId + " -> " + name);
                }

                if (new[] { "ProjectId", "Addresses", "Aliases", "SubAssets", "Groups" }.Contains(name))
                {
                    throw new InvalidOperationException("Project 常量类与保留成员冲突：" + name);
                }

                text.Append("    public static class ").Append(name).Append("\n    {\n");
                Constant("        ", "ProjectId", catalog.ProjectId);
                void Keys(string nested, IEnumerable<(string Name, string Value)> keys)
                {
                    text.Append("        public static class ").Append(nested).Append("\n        {\n");
                    var names = new HashSet<string>(StringComparer.Ordinal) { nested };
                    foreach (var key in keys.OrderBy(x => x.Name, StringComparer.Ordinal))
                    {
                        var member = Identifier(key.Name, provider);
                        if (!names.Add(member))
                        {
                            throw new InvalidOperationException("常量成员碰撞：" + catalog.ProjectId + "." + nested + " / " + key.Name + " -> " + member);
                        }

                        Constant("            ", member, key.Value);
                    }
                    text.Append("        }\n");
                }
                Keys("Addresses", catalog.Assets.Where(x => x.SubAssetKey == null).Select(x => (x.Address, x.Address)));
                Keys("Aliases", catalog.Assets.Where(x => x.SubAssetKey == null).SelectMany(x => x.Aliases.Select(a => (a, a))));
                Keys("Groups", catalog.Groups.Select(x => (x.Id, x.Id)));
                text.Append("        public static class SubAssets\n        {\n");
                var subNames = new HashSet<string>(StringComparer.Ordinal) { "SubAssets" };
                foreach (var sub in catalog.Assets.Where(x => x.SubAssetKey != null).OrderBy(x => x.Address, StringComparer.Ordinal).ThenBy(x => x.SubAssetKey, StringComparer.Ordinal))
                {
                    var member = Identifier(sub.Address, provider) + Identifier(sub.SubAssetName, provider).TrimStart('_');
                    if (!subNames.Add(member) || member == "Address" || member == "Key")
                    {
                        throw new InvalidOperationException("子资产常量类碰撞：" + sub.Address + "#" + sub.SubAssetKey);
                    }

                    text.Append("            public static class ").Append(member).Append("\n            {\n");
                    Constant("                ", "Address", sub.Address);
                    Constant("                ", "Key", sub.SubAssetKey);
                    text.Append("            }\n");
                }
                text.Append("        }\n    }\n");
            }
            return text.Append("}\n").ToString();
        }

        /// <summary>将资源名称转换为固定文化的合法 C# 标识符，保留常量原值不变。</summary>
        private static string Identifier(string value, CSharpCodeProvider provider)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Any(char.IsLetterOrDigit) || value.Any(char.IsControl))
            {
                throw new InvalidOperationException("资源名称为空或不能生成有效标识符：" + value);
            }

            var identifier = string.Concat(Regex.Split(value, @"[^\p{L}\p{Nd}]+").Where(part => part.Length != 0).Select(part =>
            {
                if (string.Equals(part, "ui", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "io", StringComparison.OrdinalIgnoreCase))
                {
                    return part.ToUpperInvariant();
                }
                if (part.All(character => !char.IsLetter(character) || char.IsUpper(character)))
                {
                    part = part.ToLowerInvariant();
                }
                return char.ToUpperInvariant(part[0]) + part.Substring(1);
            }));
            identifier = Regex.Replace(identifier, @"(GUID|MVID|HTTP|JSON|XML|URL|API|FSM)(?=$|[A-Z])",
                match => char.ToUpperInvariant(match.Value[0]) + match.Value.Substring(1).ToLowerInvariant(), RegexOptions.CultureInvariant);
            if (char.IsDigit(identifier[0]))
            {
                identifier = "_" + identifier;
            }

            if (!provider.IsValidIdentifier(identifier))
            {
                throw new InvalidOperationException("不能生成有效 C# 标识符：" + value);
            }

            return identifier;
        }

    }
}
