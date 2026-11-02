using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YLFramework.Editor.Help
{
    /// <summary>一个模块提供的帮助内容，仅在编辑器中使用。</summary>
    /// <remarks>提供方配置页面、类型和文档来源；窗口读取这些数据，不执行示例代码或 API 成员。</remarks>
    public sealed class HelpModule
    {
        /// <summary>按展示顺序排列的使用指南。</summary>
        public HelpPage[] Guides = Array.Empty<HelpPage>();
        /// <summary>可选的模块介绍页面。</summary>
        public HelpPage[] About = Array.Empty<HelpPage>();
        /// <summary>明确属于本模块的 API 类型，不自动收集依赖程序集。</summary>
        public HelpApiTypeDefinition[] ApiTypes = Array.Empty<HelpApiTypeDefinition>();
        /// <summary>程序集对应的 XML 文件；未指定或文件缺失时尝试 DLL 同名邻接文件。</summary>
        /// <remarks>使用实际 Assembly 作为键；已有文件无效或不可读时保留 API 声明，不继续回退。</remarks>
        public Dictionary<Assembly, string> XmlPaths = new Dictionary<Assembly, string>();
        /// <summary>经模块核实的文档覆盖；空白字段由 XML 补足，不能根据名称猜测行为。</summary>
        /// <remarks>回调可返回 null；参数与类型参数按名称合并，回调异常不会被读取器吞掉。</remarks>
        public Func<MemberInfo, HelpDocumentation> Documentation;
        /// <summary>首次打开时折叠的指南组名。</summary>
        public string[] CollapsedGuideGroups = Array.Empty<string>();
        /// <summary>首次打开时折叠的 API 组名。</summary>
        public string[] CollapsedApiGroups = Array.Empty<string>();
    }

    /// <summary>指南或关于页面的有序内容与 API 关联。</summary>
    public sealed class HelpPage
    {
        /// <summary>模块内稳定且唯一的页面标识。</summary>
        public readonly string Id;
        /// <summary>中文页面标题。</summary>
        public readonly string Title;
        /// <summary>导航分组，组顺序由页面出现顺序决定。</summary>
        public readonly string Group;
        /// <summary>标题下的摘要。</summary>
        public readonly string Summary;
        /// <summary>按阅读顺序排列的内容块。</summary>
        public readonly HelpBlock[] Blocks;
        /// <summary>参与搜索的额外 API 名称或历史关键词。</summary>
        public string[] Keywords = Array.Empty<string>();
        /// <summary>本页关联的 API，供 API 搜索与反向指南导航使用。</summary>
        public Type[] RelatedTypes = Type.EmptyTypes;
        /// <summary>正文底部展示的精选 API。</summary>
        public Type[] FeaturedTypes = Type.EmptyTypes;

        /// <summary>建立一个页面；内容块不会被执行。</summary>
        /// <param name="id">模块内非空且稳定的页面标识。</param>
        /// <param name="title">导航与正文使用的标题；null 按空文本处理。</param>
        /// <param name="group">指南导航分组；null 按空文本处理。</param>
        /// <param name="summary">标题下的摘要；null 按空文本处理。</param>
        /// <param name="blocks">按阅读顺序排列的内容块；null 按空数组处理，不复制传入数组。</param>
        /// <exception cref="ArgumentException">id 为 null、空或仅由空白字符组成。</exception>
        public HelpPage(string id, string title, string group, string summary, params HelpBlock[] blocks)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("页面标识不能为空。", nameof(id));
            }

            Id = id;
            Title = title ?? "";
            Group = group ?? "";
            Summary = summary ?? "";
            Blocks = blocks ?? Array.Empty<HelpBlock>();
        }

        /// <summary>可搜索的正文、代码和关键词。</summary>
        public string SearchText => Title + "\n" + Summary + "\n" + string.Join("\n", Blocks.Select(block =>
            block.Title + "\n" + block.Content + "\n" + block.Context + "\n" + block.Url)) + "\n" + string.Join(" ", Keywords);
        /// <summary>按空白分词，同时匹配所有关键词。</summary>
        /// <param name="search">查询文本；null、空或全空白匹配所有页面。</param>
        /// <returns>所有分词都出现在 <see cref="SearchText"/> 中时为 true。</returns>
        public bool Matches(string search) => HelpSearch.Contains(SearchText, search);

        /// <summary>把标题、分组、摘要、关键词与内容块按权重加入评分字段；代码块权重最低。</summary>
        internal void AppendSearchFields(List<HelpSearchField> fields)
        {
            fields.Add(new HelpSearchField(Title, HelpSearch.TitleWeight));
            fields.Add(new HelpSearchField(Group, HelpSearch.KeywordWeight));
            fields.Add(new HelpSearchField(Summary, HelpSearch.SummaryWeight));
            foreach (var keyword in Keywords)
            {
                fields.Add(new HelpSearchField(keyword, HelpSearch.KeywordWeight));
            }

            foreach (var block in Blocks)
            {
                fields.Add(new HelpSearchField(block.Title, HelpSearch.BodyWeight));
                if (block.Kind == HelpBlockKind.Code)
                {
                    fields.Add(new HelpSearchField(block.Context, HelpSearch.TechnicalWeight));
                    fields.Add(new HelpSearchField(block.Content, HelpSearch.TechnicalWeight));
                }
                else
                {
                    fields.Add(new HelpSearchField(block.Content, HelpSearch.BodyWeight));
                    if (block.Kind == HelpBlockKind.Link)
                    {
                        fields.Add(new HelpSearchField(block.Url, HelpSearch.TechnicalWeight));
                    }
                }
            }
        }
    }

    /// <summary>公共阅读器支持的四种内容形式。</summary>
    public enum HelpBlockKind
    {
        /// <summary>可带标题的正文。</summary>
        Text,
        /// <summary>带复制按钮的只读 C# 代码。</summary>
        Code,
        /// <summary>提示或注意事项。</summary>
        Notice,
        /// <summary>由用户点击打开的外部链接。</summary>
        Link
    }

    /// <summary>轻量内容块，不包含渲染回调或运行时逻辑。</summary>
    public sealed class HelpBlock
    {
        /// <summary>内容形式。</summary>
        public readonly HelpBlockKind Kind;
        /// <summary>段落标题或链接文本。</summary>
        public readonly string Title;
        /// <summary>正文、提示或代码文本。</summary>
        public readonly string Content;
        /// <summary>代码块前的使用上下文。</summary>
        public readonly string Context;
        /// <summary>外部链接地址。</summary>
        public readonly string Url;

        /// <summary>保存内容块的展示数据，将可选 null 文本规范为空串。</summary>
        private HelpBlock(HelpBlockKind kind, string title, string content, string context = "", string url = "")
        {
            Kind = kind;
            Title = title ?? "";
            Content = content ?? "";
            Context = context ?? "";
            Url = url ?? "";
        }

        /// <summary>创建正文段落。</summary>
        /// <param name="title">可选段落标题；null 按空文本处理。</param>
        /// <param name="text">正文文本；null 按空文本处理。</param>
        /// <returns>供阅读器按正文样式展示的内容块。</returns>
        public static HelpBlock Paragraph(string title, string text) => new HelpBlock(HelpBlockKind.Text, title, text);
        /// <summary>创建只读 C# 代码块。</summary>
        /// <param name="title">代码块标题。</param>
        /// <param name="context">代码前的使用上下文。</param>
        /// <param name="code">仅展示和复制的代码，不会被帮助窗口执行。</param>
        /// <returns>供阅读器按代码样式展示的内容块。</returns>
        public static HelpBlock Code(string title, string context, string code) => new HelpBlock(HelpBlockKind.Code, title, code, context);
        /// <summary>创建提示或注意事项。</summary>
        /// <param name="title">提示块标题。</param>
        /// <param name="text">提示正文。</param>
        /// <returns>供阅读器突出展示的提示内容块。</returns>
        public static HelpBlock Notice(string title, string text) => new HelpBlock(HelpBlockKind.Notice, title, text);
        /// <summary>创建外部链接，不自动访问网络。</summary>
        /// <param name="title">链接显示文本。</param>
        /// <param name="url">点击后打开的地址；此处不校验可达性。</param>
        /// <returns>只保存显示文本与地址的链接块。</returns>
        public static HelpBlock Link(string title, string url) => new HelpBlock(HelpBlockKind.Link, title, "", url: url);
    }

    /// <summary>模块明确选定的类型与其导航分组。</summary>
    public sealed class HelpApiTypeDefinition
    {
        /// <summary>用于读取编译元数据的实际类型。</summary>
        public readonly Type Type;
        /// <summary>模块决定的导航分组。</summary>
        public readonly string Group;
        /// <summary>选择一个 API 类型，不构造该类型的实例。</summary>
        /// <param name="type">非 null 的实际类型；不会自动添加其依赖类型。</param>
        /// <param name="group">API 导航分组；null 按空文本处理。</param>
        /// <exception cref="ArgumentNullException">type 为 null。</exception>
        public HelpApiTypeDefinition(Type type, string group)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Group = group ?? "";
        }
    }

    /// <summary>API 说明与 C# 声明分开保存；空字段表示未提供说明。</summary>
    public sealed class HelpDocumentation
    {
        /// <summary>简要说明。</summary>
        public readonly string Summary;
        /// <summary>返回值说明。</summary>
        public readonly string Returns;
        /// <summary>备注或使用约束。</summary>
        public readonly string Remarks;
        /// <summary>参数说明。</summary>
        public readonly HelpParameter[] Parameters;
        /// <summary>泛型类型参数说明。</summary>
        public readonly HelpParameter[] TypeParameters;
        /// <summary>不包含推断说明的空文档。</summary>
        public static readonly HelpDocumentation Empty = new HelpDocumentation(null, null, null, null);

        /// <summary>建立 XML 说明或模块的稀疏覆盖数据。</summary>
        /// <remarks>null 文本和数组按空值处理；保存传入数组的引用。作为模块覆盖时，空白说明不会清空 XML 内容。</remarks>
        /// <param name="summary">实际提供的摘要。</param>
        /// <param name="returns">返回值或属性值说明。</param>
        /// <param name="remarks">实际提供的约束与备注。</param>
        /// <param name="parameters">按参数名称关联的说明。</param>
        /// <param name="typeParameters">按泛型类型参数名称关联的说明。</param>
        public HelpDocumentation(string summary, string returns, string remarks, HelpParameter[] parameters, HelpParameter[] typeParameters = null)
        {
            Summary = summary ?? "";
            Returns = returns ?? "";
            Remarks = remarks ?? "";
            Parameters = parameters ?? Array.Empty<HelpParameter>();
            TypeParameters = typeParameters ?? Array.Empty<HelpParameter>();
        }
    }

    /// <summary>参数或类型参数的名称与说明。</summary>
    public sealed class HelpParameter
    {
        /// <summary>声明中的参数名称。</summary>
        public readonly string Name;
        /// <summary>实际提供的说明，不根据参数名称猜测。</summary>
        public readonly string Description;
        /// <summary>保存一个具名说明。</summary>
        /// <param name="name">声明中的参数或类型参数名称；null 按空文本处理。</param>
        /// <param name="description">实际说明；null 按空文本处理。</param>
        public HelpParameter(string name, string description)
        {
            Name = name ?? "";
            Description = description ?? "";
        }
    }

    /// <summary>帮助页面与 API 共用的简单搜索。</summary>
    /// <remarks>匹配为大小写不敏感的字面子串；多词按空白分词后同时命中（AND），不引入模糊或拼音算法。</remarks>
    public static class HelpSearch
    {
        /// <summary>空查询匹配全部；空白分词不区分大小写，所有词均须匹配。</summary>
        /// <param name="text">被搜索的文本；null 按空文本处理。</param>
        /// <param name="search">查询文本；null、空或全空白时直接视为匹配。</param>
        /// <returns>查询的每个分词都包含在 text 中（不区分大小写）时为 true。</returns>
        public static bool Contains(string text, string search) => string.IsNullOrWhiteSpace(search) ||
            search.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).All(term =>
                (text ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>查询按空白分词并去重（不区分大小写）；null、空或全空白返回空数组。</summary>
        internal static string[] Tokenize(string search) => string.IsNullOrWhiteSpace(search) ? Array.Empty<string>() :
            search.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>按权重评分：每个词都须命中任一字段（AND），返回总分；任一词未命中返回 -1。</summary>
        /// <remarks>命中取字段权重加匹配等级（整体等于 +30、前缀 +20、包含 +10）；含点号的词同时比较整体与分段 AND 匹配，分段扣分后取较高值。</remarks>
        internal static int Score(IReadOnlyList<HelpSearchField> fields, string[] terms)
        {
            if (terms == null || terms.Length == 0)
            {
                return 0;
            }

            var total = 0;
            foreach (var term in terms)
            {
                var score = MatchTerm(fields, term);
                if (score < 0)
                {
                    return -1;
                }

                total += score;
            }
            return total;
        }

        /// <summary>为单个查询词取最佳分数；限定名允许各段分布在不同字段中，以最弱段得分扣分计入。</summary>
        private static int MatchTerm(IReadOnlyList<HelpSearchField> fields, string term)
        {
            var best = MatchSingle(fields, term);
            if (term.IndexOf('.') > 0)
            {
                var parts = term.Split('.');
                if (parts.Length > 1)
                {
                    var split = int.MaxValue;
                    foreach (var part in parts)
                    {
                        var partScore = MatchSingle(fields, part);
                        if (partScore < 0)
                        {
                            split = -1;
                            break;
                        }
                        if (partScore < split)
                        {
                            split = partScore;
                        }
                    }
                    if (split >= 0)
                    {
                        best = Math.Max(best, Math.Max(1, split - SplitPenalty));
                    }
                }
            }
            return best;
        }

        /// <summary>在所有非空字段中按相等、前缀和子串等级取得最高得分；无匹配返回 -1。</summary>
        private static int MatchSingle(IReadOnlyList<HelpSearchField> fields, string term)
        {
            var best = -1;
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                if (field.Text.Length == 0)
                {
                    continue;
                }

                int bonus;
                if (field.Text.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    bonus = ExactBonus;
                }
                else if (field.Text.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                {
                    bonus = PrefixBonus;
                }
                else if (field.Text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bonus = ContainsBonus;
                }
                else
                {
                    continue;
                }

                if (field.Weight + bonus > best)
                {
                    best = field.Weight + bonus;
                }
            }
            return best;
        }

        internal const int TitleWeight = 100;
        internal const int MemberNameWeight = 90;
        internal const int KeywordWeight = 90;
        internal const int SummaryWeight = 80;
        internal const int BodyWeight = 70;
        internal const int DocWeight = 60;
        internal const int TechnicalWeight = 40;
        private const int ExactBonus = 30;
        private const int PrefixBonus = 20;
        private const int ContainsBonus = 10;
        private const int SplitPenalty = 10;
    }

    /// <summary>一条带权重的可搜索字段；评分时按权重区分名称、说明与签名等来源。</summary>
    internal readonly struct HelpSearchField
    {
        internal readonly string Text;
        internal readonly int Weight;

        /// <summary>保存评分字段与来源权重，将 null 文本规范为空串。</summary>
        internal HelpSearchField(string text, int weight)
        {
            Text = text ?? "";
            Weight = weight;
        }
    }
}
