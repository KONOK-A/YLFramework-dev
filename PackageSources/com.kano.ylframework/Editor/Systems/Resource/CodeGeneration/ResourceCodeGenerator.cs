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
using UnityEditor.AddressableAssets.Settings;

namespace YLFramework.Editor.Resource
{
    /// <summary>直接从官方 Entries/Labels 生成区分大小写的地址常量，不生成平行目录或 Group 运行集合。</summary>
    public static class ResourceCodeGenerator
    {
        public static bool GenerateReferences(AddressableAssetSettings settings,
            string directory = ResourceReferenceOutput.DirectoryPath, string @namespace = "YLFramework.Resources")
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Generate references in a settled Editor.");
            }
            return ResourceReferenceOutput.Publish(directory, GenerateSource(settings, @namespace));
        }

        /// <summary>只生成文本；所有值直接读取当前官方配置，冲突时拒绝而非裁掉条目。</summary>
        public static string GenerateSource(AddressableAssetSettings settings, string @namespace = "YLFramework.Resources")
        {
            if (!settings)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            using var provider = new CSharpCodeProvider();
            if (string.IsNullOrWhiteSpace(@namespace) || @namespace.Contains('@') ||
                @namespace.Split('.').Any(part => !provider.IsValidIdentifier(part)))
            {
                throw new ArgumentException("Invalid C# namespace: " + @namespace, nameof(@namespace));
            }
            var published = ResourceAddressablesAuthoring.Inspect(settings);
            if (published.Errors.Count != 0)
            {
                throw new InvalidOperationException(string.Join("\n", published.Errors));
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(value => value.GetName().Name != ResourceReferenceOutput.AssemblyName))
            {
                if (assembly.GetType(@namespace + ".ResourceNames", false) != null)
                {
                    throw new InvalidOperationException("The generated ResourceNames type is already owned by another assembly.");
                }
            }
            var text = new StringBuilder("// Generated from official Addressables Entries and Labels. Do not edit.\nnamespace ")
                .Append(@namespace).Append("\n{\n    public static class ResourceNames\n    {\n");
            Append("Addresses", published.Addresses);
            Append("Labels", published.Labels);
            return text.Append("    }\n}\n").ToString();

            void Append(string name, IEnumerable<string> values)
            {
                text.Append("        public static class ").Append(name).Append("\n        {\n");
                var names = new HashSet<string>(StringComparer.Ordinal) { name };
                foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal))
                {
                    var member = Identifier(value, provider);
                    if (!names.Add(member))
                    {
                        throw new InvalidOperationException("Generated identifier collision: " + name + "/" + value + " -> " + member);
                    }
                    using var literal = new StringWriter(CultureInfo.InvariantCulture);
                    provider.GenerateCodeFromExpression(new CodePrimitiveExpression(value), literal, new CodeGeneratorOptions());
                    text.Append("            public const string ").Append(member).Append(" = ").Append(literal).Append(";\n");
                }
                text.Append("        }\n");
            }
        }

        private static string Identifier(string value, CSharpCodeProvider provider)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Any(char.IsLetterOrDigit) || value.Any(char.IsControl))
            {
                throw new InvalidOperationException("Address or label cannot form a C# identifier: " + value);
            }
            var identifier = string.Concat(Regex.Split(value, @"[^\p{L}\p{Nd}]+").Where(part => part.Length != 0)
                .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
            if (char.IsDigit(identifier[0]))
            {
                identifier = "_" + identifier;
            }
            if (!provider.IsValidIdentifier(identifier))
            {
                throw new InvalidOperationException("Address or label cannot form a C# identifier: " + value);
            }
            return identifier;
        }
    }
}
