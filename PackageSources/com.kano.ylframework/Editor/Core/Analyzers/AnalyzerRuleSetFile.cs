using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace YLFramework.Editor.Core.Analyzers
{
    /// <summary>一次规则编辑的内存预览与读取快照；只有显式 Apply 才会写入目标文件。</summary>
    internal sealed class RuleSetPreview
    {
        /// <summary>用于限定配置路径的规范化工程根目录。</summary>
        internal string ProjectRoot
        {
            get; set;
        }
        /// <summary>本次唯一可写的叶子 ruleset 完整路径。</summary>
        internal string TargetPath
        {
            get; set;
        }
        /// <summary>预览时读取的目标原文；目标尚不存在时为空。</summary>
        internal string OriginalText { get; set; } = "";
        /// <summary>供界面检查的拟写入文本；实际落盘使用 ProposedBytes。</summary>
        internal string ProposedText { get; set; } = "";
        /// <summary>无法安全读取或编辑时的阻断原因；null 表示未发现阻断。</summary>
        internal string BlockedReason
        {
            get; set;
        }
        /// <summary>已知 YL ID 的本地覆盖是否发生变化。</summary>
        internal bool HasChanges
        {
            get; set;
        }
        /// <summary>预览无阻断且有变更；应用时仍须重新核对磁盘快照。</summary>
        internal bool CanApply => BlockedReason == null && HasChanges;
        /// <summary>目标原文中的本地 YL 覆盖，不是展开继承后的最终严重度。</summary>
        internal IReadOnlyDictionary<string, string> LocalActions
        {
            get; set;
        } =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        /// <summary>已读取的目标、asmdef 与 Include 文件哈希；null 哈希记录文件当时不存在。</summary>
        internal Dictionary<string, string> ObservedFiles
        {
            get;
        } =
            new Dictionary<string, string>(AnalyzerRuleSetFile.PathComparer);
        /// <summary>目标目录中除全局 Default.ruleset 外的局部规则集路径快照。</summary>
        internal string[] Candidates
        {
            get; set;
        }
        /// <summary>预览生成的待写入字节，不在 Apply 时重新解释编辑意图。</summary>
        internal byte[] ProposedBytes
        {
            get; set;
        }
        /// <summary>从已有目标检测的编码；新文件默认严格 UTF-8 无 BOM。</summary>
        internal Encoding Encoding { get; set; } = new UTF8Encoding(false, true);
        /// <summary>目标是否为 Unity 支持的预定义程序集，而非业务 asmdef。</summary>
        internal bool IsPredefined
        {
            get; set;
        }
    }

    /// <summary>保守读取、预览和替换业务叶子 ruleset，保留继承关系及非自有规则。</summary>
    /// <remarks>不模拟编译器的最终严重度；遇到不能核实的结构或路径时保持只读。</remarks>
    internal static class AnalyzerRuleSetFile
    {
        private const string AnalyzerId = "YLFramework.Analyzers";
        private const int MaximumIncludeDepth = 64;
        private static readonly string[] _predefinedNames =
        {
            "Assembly-CSharp", "Assembly-CSharp-firstpass", "Assembly-CSharp-Editor", "Assembly-CSharp-Editor-firstpass"
        };
        private static readonly HashSet<string> _knownIds =
            new HashSet<string>(AnalyzerRuleCatalog.Rules.Select(rule => rule.Id), StringComparer.Ordinal);
        private static readonly HashSet<string> _ruleActions =
            new HashSet<string>(new[] { "Error", "Warning", "Info", "Hidden", "None" }, StringComparer.Ordinal);
        private static readonly HashSet<string> _includeActions =
            new HashSet<string>(_ruleActions.Concat(new[] { "Default" }), StringComparer.Ordinal);
        internal static StringComparer PathComparer
        {
            get;
        } =
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>按精确名称识别支持配置的四种 Unity 预定义程序集。</summary>
        internal static bool IsPredefined(string name) => _predefinedNames.Contains(name, StringComparer.Ordinal);

        /// <summary>只读磁盘并在内存中生成叶子规则集变更；预期的读取或格式失败写入阻断原因。</summary>
        /// <param name="projectRoot">Unity 工程根目录。</param>
        /// <param name="assemblyName">目标程序集名称。</param>
        /// <param name="asmdefPath">业务 asmdef 路径；预定义程序集使用 null。</param>
        /// <param name="changes">已知 YL ID 到 Warning、Error、None 的覆盖；null 值表示移除本地覆盖。</param>
        internal static RuleSetPreview Preview(string projectRoot, string assemblyName, string asmdefPath,
            IReadOnlyDictionary<string, string> changes)
        {
            var preview = new RuleSetPreview();
            try
            {
                preview.ProjectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
                if (changes == null)
                {
                    throw new ArgumentNullException(nameof(changes));
                }
                foreach (var change in changes)
                {
                    if (!_knownIds.Contains(change.Key) ||
                        (change.Value != null && change.Value != "Warning" && change.Value != "Error" && change.Value != "None"))
                    {
                        throw new InvalidOperationException("只能修改已知 YL ID 的 Warning / Error / None 或继承状态。");
                    }
                }

                Locate(preview, assemblyName, asmdefPath);
                preview.Candidates = Candidates(preview);
                if (!preview.IsPredefined && preview.Candidates.Length > 1)
                {
                    throw new InvalidOperationException("同目录存在多份局部 ruleset，无法安全确定生效文件。");
                }
                if (!preview.IsPredefined && preview.Candidates.Length == 1)
                {
                    preview.TargetPath = preview.Candidates[0];
                }
                ValidatePath(preview.ProjectRoot, preview.TargetPath);
                if (File.Exists(preview.TargetPath) && (File.GetAttributes(preview.TargetPath) & FileAttributes.ReadOnly) != 0)
                {
                    throw new InvalidOperationException("目标 ruleset 为只读，未修改文件。");
                }
                preview.ObservedFiles[preview.TargetPath] = HashFile(preview.TargetPath);
                var cache = new Dictionary<string, XDocument>(PathComparer);
                var active = new HashSet<string>(PathComparer);
                var existing = File.Exists(preview.TargetPath);
                XDocument document;
                if (existing)
                {
                    document = ReadDocument(preview, preview.TargetPath, cache, active, 0);
                }
                else
                {
                    document = new XDocument(new XDeclaration("1.0", "utf-8", null),
                        new XElement("RuleSet", new XAttribute("Name", assemblyName), new XAttribute("ToolsVersion", "12.0")));
                    var global = Path.Combine(preview.ProjectRoot, "Assets", "Default.ruleset");
                    ValidatePath(preview.ProjectRoot, global);
                    preview.ObservedFiles[global] = HashFile(global);
                    if (File.Exists(global))
                    {
                        ReadDocument(preview, global, cache, active, 0);
                        document.Root.Add(new XElement("Include",
                            new XAttribute("Path", Path.GetRelativePath(Path.GetDirectoryName(preview.TargetPath), global).Replace('\\', '/')),
                            new XAttribute("Action", "Default")));
                    }
                }

                // 只编辑叶子文件的自有规则；Include 仅校验并纳入快照，不扁平化继承或重写来源文件。
                var ownedRules = document.Root.Elements("Rules").Where(OwnSection).SelectMany(section => section.Elements("Rule")).ToArray();
                var local = ownedRules.Where(rule => _knownIds.Contains((string)rule.Attribute("Id")))
                    .ToDictionary(rule => (string)rule.Attribute("Id"), rule => (string)rule.Attribute("Action"), StringComparer.Ordinal);
                preview.LocalActions = new ReadOnlyDictionary<string, string>(local);
                foreach (var change in changes)
                {
                    var rule = ownedRules.SingleOrDefault(item => (string)item.Attribute("Id") == change.Key);
                    if (change.Value == null)
                    {
                        if (rule != null)
                        {
                            rule.Remove();
                            preview.HasChanges = true;
                        }
                        continue;
                    }
                    if (rule == null)
                    {
                        var section = document.Root.Elements("Rules").FirstOrDefault(OwnSection);
                        if (section == null)
                        {
                            section = new XElement("Rules", new XAttribute("AnalyzerId", AnalyzerId),
                                new XAttribute("RuleNamespace", AnalyzerId));
                            document.Root.Add(section);
                        }
                        section.Add(new XElement("Rule", new XAttribute("Id", change.Key), new XAttribute("Action", change.Value)));
                        preview.HasChanges = true;
                    }
                    else if ((string)rule.Attribute("Action") != change.Value)
                    {
                        rule.SetAttributeValue("Action", change.Value);
                        preview.HasChanges = true;
                    }
                }

                if (preview.HasChanges)
                {
                    using var stream = new MemoryStream();
                    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
                    {
                        Encoding = preview.Encoding,
                        Indent = !existing,
                        NewLineChars = "\n",
                        NewLineHandling = NewLineHandling.None,
                        OmitXmlDeclaration = document.Declaration == null,
                        CloseOutput = false
                    }))
                    {
                        document.Save(writer);
                    }
                    preview.ProposedBytes = stream.ToArray();
                    preview.ProposedText = preview.Encoding.GetString(preview.ProposedBytes).TrimStart('\uFEFF');
                }
                else
                {
                    preview.ProposedText = preview.OriginalText;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is XmlException || exception is ArgumentException || exception is InvalidOperationException ||
                exception is NotSupportedException || exception is System.Security.SecurityException)
            {
                preview.BlockedReason = exception.Message;
                preview.HasChanges = false;
            }
            return preview;
        }

        /// <summary>核对预览依赖后，以已生成的字节替换唯一目标；快照变化时拒绝覆盖。</summary>
        internal static void Apply(RuleSetPreview preview)
        {
            if (preview == null)
            {
                throw new ArgumentNullException(nameof(preview));
            }
            if (preview.BlockedReason != null)
            {
                throw new InvalidOperationException(preview.BlockedReason);
            }
            if (!preview.HasChanges)
            {
                return;
            }
            VerifySnapshot(preview);
            var temporary = Path.Combine(preview.ProjectRoot, "Temp", "YLFramework.Analyzers", "RuleSets");
            ValidatePath(preview.ProjectRoot, temporary);
            Directory.CreateDirectory(temporary);
            var staged = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(staged, preview.ProposedBytes);
                // 暂存期间文件仍可能变化，替换前再次确认所有已观察输入和候选集合未变。
                VerifySnapshot(preview);
                if (File.Exists(preview.TargetPath))
                {
                    File.Replace(staged, preview.TargetPath, null);
                }
                else
                {
                    File.Move(staged, preview.TargetPath);
                }
            }
            finally
            {
                if (File.Exists(staged))
                {
                    File.Delete(staged);
                }
            }
        }

        /// <summary>定位业务程序集的叶子配置路径，拒绝接管框架、Packages 或全局规则集。</summary>
        private static void Locate(RuleSetPreview preview, string assemblyName, string asmdefPath)
        {
            if (!Directory.Exists(preview.ProjectRoot) || string.IsNullOrWhiteSpace(assemblyName) ||
                assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || assemblyName.Contains('/') ||
                assemblyName.Contains('\\') || assemblyName != assemblyName.TrimEnd('.', ' '))
            {
                throw new InvalidOperationException("工程目录或程序集名称无效。");
            }
            var assets = Path.Combine(preview.ProjectRoot, "Assets");
            string directory;
            preview.IsPredefined = asmdefPath == null;
            if (preview.IsPredefined)
            {
                if (!IsPredefined(assemblyName))
                {
                    throw new InvalidOperationException("不是受支持的 Unity 预定义程序集名称。");
                }
                directory = assets;
            }
            else
            {
                var path = Path.GetFullPath(Path.IsPathRooted(asmdefPath) ? asmdefPath : Path.Combine(preview.ProjectRoot, asmdefPath));
                ValidatePath(preview.ProjectRoot, path);
                if (!Within(assets, path) || Within(Path.Combine(assets, "YLFramework"), path) ||
                    !string.Equals(Path.GetExtension(path), ".asmdef", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    throw new InvalidOperationException("仅支持 Assets 内可写业务 asmdef；Packages、框架与工程外目标只读。");
                }
                preview.ObservedFiles[path] = HashFile(path);
                directory = Path.GetDirectoryName(path);
            }
            if (!Directory.Exists(directory))
            {
                throw new IOException("目标程序集目录不存在。");
            }
            preview.TargetPath = Path.Combine(directory, assemblyName + ".ruleset");
            if (PathComparer.Equals(preview.TargetPath, Path.Combine(assets, "Default.ruleset")))
            {
                throw new InvalidOperationException("不能接管 Assets/Default.ruleset。");
            }
        }

        /// <summary>按平台路径比较规则稳定列出同目录的局部规则集，排除全局 Default.ruleset。</summary>
        private static string[] Candidates(RuleSetPreview preview)
        {
            var global = Path.Combine(preview.ProjectRoot, "Assets", "Default.ruleset");
            return Directory.GetFiles(Path.GetDirectoryName(preview.TargetPath), "*.ruleset", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath).Where(path => !PathComparer.Equals(path, global))
                .OrderBy(path => path, PathComparer).ToArray();
        }

        /// <summary>递归校验 ruleset 与 Include，记录原始字节哈希；循环、歧义或未知结构一律阻断编辑。</summary>
        private static XDocument ReadDocument(RuleSetPreview preview, string path, Dictionary<string, XDocument> cache,
            HashSet<string> active, int depth)
        {
            ValidatePath(preview.ProjectRoot, path);
            if (depth > MaximumIncludeDepth || !active.Add(path))
            {
                throw new InvalidOperationException("Include 存在循环或超出可核实的深度，保持只读。");
            }
            if (cache.TryGetValue(path, out var cached))
            {
                active.Remove(path);
                return cached;
            }
            var bytes = File.ReadAllBytes(path);
            preview.ObservedFiles[path] = HashBytes(bytes);
            string text;
            using (var stream = new MemoryStream(bytes))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
            {
                text = reader.ReadToEnd();
                if (PathComparer.Equals(path, preview.TargetPath))
                {
                    preview.OriginalText = text;
                    preview.Encoding = reader.CurrentEncoding;
                }
            }
            using var xml = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024
            });
            var document = XDocument.Load(xml, LoadOptions.PreserveWhitespace);
            if (document.Root?.Name != "RuleSet" || UnexpectedText(document.Root))
            {
                throw new InvalidOperationException("不支持的 ruleset 根结构。");
            }
            RequiredAttribute(document.Root, "Name");
            RequiredAttribute(document.Root, "ToolsVersion");
            var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var editableIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in document.Root.Elements())
            {
                if (UnexpectedText(element) || (element.Name != "Rules" && element.HasElements))
                {
                    throw new InvalidOperationException("不支持的 ruleset 嵌套结构或文本，保持只读。");
                }
                if (element.Name == "Rules")
                {
                    RequiredAttribute(element, "AnalyzerId");
                    RequiredAttribute(element, "RuleNamespace");
                    foreach (var rule in element.Elements())
                    {
                        var id = (string)rule.Attribute("Id");
                        var action = (string)rule.Attribute("Action");
                        if (rule.Name != "Rule" || rule.HasElements || UnexpectedText(rule) ||
                            string.IsNullOrEmpty(id) || !_ruleActions.Contains(action))
                        {
                            throw new InvalidOperationException("不支持的 Rule 结构或 Action。");
                        }
                        if (seenIds.TryGetValue(id, out var previous) && previous != action)
                        {
                            throw new InvalidOperationException("同一诊断 ID 存在冲突 Action；原生规则集不按 analyzer 隔离 ID。");
                        }
                        seenIds[id] = action;
                        if (_knownIds.Contains(id) && (!OwnSection(element) || !editableIds.Add(id)))
                        {
                            throw new InvalidOperationException("已知 YL ID 重复或位于其他 analyzer 节，无法安全编辑。");
                        }
                    }
                }
                else if (element.Name == "Include")
                {
                    var include = (string)element.Attribute("Path");
                    if (string.IsNullOrEmpty(include) || !_includeActions.Contains((string)element.Attribute("Action")))
                    {
                        throw new InvalidOperationException("Include 缺少可核实的 Path 或 Action。");
                    }
                    if (Regex.IsMatch(include, "%[^%]+%"))
                    {
                        throw new InvalidOperationException("不支持环境变量 Include；不能用字面路径替代编译器的变量展开结果，保持只读。");
                    }
                    var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), include));
                    ReadDocument(preview, target, cache, active, depth + 1);
                }
                else if (element.Name != "IncludeAll" || !_ruleActions.Contains((string)element.Attribute("Action")))
                {
                    throw new InvalidOperationException("不支持的 ruleset 结构，未重建文件。");
                }
            }
            if (document.Root.Elements("IncludeAll").Count() > 1)
            {
                throw new InvalidOperationException("多份 IncludeAll 无法可靠解析，保持只读。");
            }
            active.Remove(path);
            cache.Add(path, document);
            return document;
        }

        /// <summary>确认规则节同时使用本分析器的身份与规则命名空间。</summary>
        private static bool OwnSection(XElement section) =>
            (string)section.Attribute("AnalyzerId") == AnalyzerId && (string)section.Attribute("RuleNamespace") == AnalyzerId;

        /// <summary>要求原生 ruleset 必需的属性存在且非空，避免靠重建文档掩盖非法输入。</summary>
        private static void RequiredAttribute(XElement element, string name)
        {
            if (string.IsNullOrEmpty((string)element.Attribute(name)))
            {
                throw new InvalidOperationException(element.Name + " 缺少非空 " + name + "，原生 ruleset 无法读取。");
            }
        }

        /// <summary>识别规则结构中不能安全保留语义的非空白直接文本。</summary>
        private static bool UnexpectedText(XElement element) =>
            element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value));

        /// <summary>复核候选集合、所有已观察文件哈希与目标可写性，防止预览后覆盖外部修改。</summary>
        private static void VerifySnapshot(RuleSetPreview preview)
        {
            ValidatePath(preview.ProjectRoot, preview.TargetPath);
            if (!Candidates(preview).SequenceEqual(preview.Candidates, PathComparer))
            {
                throw new IOException("局部规则集集合在预览后变化，请刷新后重新预览。");
            }
            foreach (var pair in preview.ObservedFiles)
            {
                ValidatePath(preview.ProjectRoot, pair.Key);
                if (HashFile(pair.Key) != pair.Value)
                {
                    throw new IOException("配置或继承文件在预览后变化，请刷新后重新预览：" + pair.Key);
                }
            }
            if (File.Exists(preview.TargetPath) && (File.GetAttributes(preview.TargetPath) & FileAttributes.ReadOnly) != 0)
            {
                throw new IOException("目标在预览后变为只读，未修改文件。");
            }
        }

        /// <summary>将读写限制在工程内，拒绝路径及其现存祖先中的文件系统链接。</summary>
        private static void ValidatePath(string root, string path)
        {
            if (!Within(root, path))
            {
                throw new InvalidOperationException("配置路径越出工程范围，保持只读。");
            }
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("配置路径含链接目标，保持只读。");
                }
                if (PathComparer.Equals(current, root))
                {
                    break;
                }
            }
        }

        /// <summary>按目录分隔边界判断完整路径是否为根或其后代，避免同前缀目录被误认为工程内。</summary>
        private static bool Within(string root, string path) => PathComparer.Equals(root, path) ||
            Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// <summary>取得文件的字节哈希；文件不存在时用 null 标记该快照状态。</summary>
        private static string HashFile(string path) => File.Exists(path) ? HashBytes(File.ReadAllBytes(path)) : null;

        /// <summary>计算稳定的 SHA256 十六进制摘要，用于比较预览与应用时的原始字节。</summary>
        private static string HashBytes(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
