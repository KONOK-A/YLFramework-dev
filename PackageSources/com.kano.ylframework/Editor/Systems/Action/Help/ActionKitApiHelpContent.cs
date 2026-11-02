using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using YLFramework.Actions;
using YLFramework.Editor.Help;

namespace YLFramework.Editor.Actions
{
    /// <summary>连接 ActionKit 主题、反射索引与示例源码，供帮助界面及只读验收入口使用。</summary>
    internal static class ActionKitApiHelpContent
    {
        /// <summary>帮助导航中的指南分组顺序。</summary>
        internal static readonly string[] GuideGroups = { "入门与生命周期", "基础动作", "组合与执行", "扩展与工具" };

        /// <summary>一篇指南的文本、示例和类型关联配置，不持有运行时动作。</summary>
        internal sealed class Topic
        {
            /// <summary>指南页的稳定标识。</summary>
            internal readonly string Id;
            /// <summary>指南页标题。</summary>
            internal readonly string Title;
            /// <summary>指南所属的导航分组。</summary>
            internal readonly string Group;
            /// <summary>主题的简要介绍。</summary>
            internal readonly string Summary;
            /// <summary>参数、执行和生命周期说明文本。</summary>
            internal readonly string Behavior;
            /// <summary>使用限制和注意事项文本。</summary>
            internal readonly string Notes;
            /// <summary>示例的放置位置、前置条件和调用方式。</summary>
            internal readonly string ExampleContext;
            /// <summary>只供展示或由测试另行编译的示例源码文本。</summary>
            internal readonly string Example;
            /// <summary>关联类型及搜索使用的类型名，不带泛型元数后缀。</summary>
            internal readonly string[] TypeNames;
            /// <summary>指南优先展示的类型名；默认沿用关联类型，可单独指定嵌套类型名。</summary>
            internal string[] FeaturedTypeNames;
            /// <summary>示例要求已加载的可选程序集简单名称；null 表示无此要求。</summary>
            internal string RequiredAssembly;
            /// <summary>此主题是否提供完整入门脚本，而非宿主类内的方法片段。</summary>
            internal bool IsCompleteExample => Id == "overview";
            /// <summary>当前已加载程序集是否满足本主题的可选依赖要求。</summary>
            internal bool IsAvailable => RequiredAssembly == null || ApiAssemblies().Any(assembly => assembly.GetName().Name == RequiredAssembly);

            /// <summary>保存指南配置，并将关联类型作为默认的优先展示类型。</summary>
            /// <param name="id">稳定页面标识。</param>
            /// <param name="title">页面标题。</param>
            /// <param name="group">导航分组。</param>
            /// <param name="summary">主题简介。</param>
            /// <param name="behavior">行为与生命周期说明。</param>
            /// <param name="notes">注意事项。</param>
            /// <param name="exampleContext">示例使用上下文。</param>
            /// <param name="example">示例源码文本。</param>
            /// <param name="typeNames">关联 API 的类型名。</param>
            internal Topic(string id, string title, string group, string summary, string behavior, string notes,
                string exampleContext, string example, params string[] typeNames)
            {
                Id = id;
                Title = title;
                Group = group;
                Summary = summary;
                Behavior = behavior;
                Notes = notes;
                ExampleContext = exampleContext;
                Example = example;
                TypeNames = typeNames;
                FeaturedTypeNames = typeNames;
            }

            /// <summary>在本主题的聚合文本中匹配帮助搜索条件。</summary>
            /// <param name="search">帮助搜索条件。</param>
            internal bool Matches(string search) => Contains(SearchText, search);
            /// <summary>用于全文搜索的标题、说明、示例和关联类型文本。</summary>
            internal string SearchText => Title + "\n" + Summary + "\n" + Behavior + "\n" + Notes + "\n" + ExampleContext + "\n" +
                Example + "\n" + string.Join(" ", TypeNames);
        }

        /// <summary>取得共享的静态指南配置。</summary>
        internal static Topic[] Topics => ActionKitApiHelpGuides.Topics;

        /// <summary>枚举核心和已经加载的精确 DOTween 包装程序集，不主动加载可选集成。</summary>
        internal static IEnumerable<Assembly> ApiAssemblies() => new[] { typeof(ActionKit).Assembly }
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(assembly =>
                assembly.GetName().Name == "YLFramework.Actions.DOTween")).Distinct();

        /// <summary>取得示例编译所需的模块程序集路径，并在包装已加载时核对其实际 DOTween 依赖身份。</summary>
        /// <remarks>供应商程序集仅作为编译引用，不因此进入 ActionKit 的公开 API 索引。</remarks>
        internal static string[] GetReferenceAssemblies()
        {
            var assemblies = ApiAssemblies().ToList();
            var wrapper = assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "YLFramework.Actions.DOTween");
            if (wrapper != null)
            {
                var dependency = wrapper.GetReferencedAssemblies().Single(reference => reference.Name == "DOTween");
                var dotween = AppDomain.CurrentDomain.GetAssemblies().Single(assembly =>
                    assembly.GetName().Name == "DOTween" && assembly.GetName().FullName == dependency.FullName);
                var tween = dotween.GetType("DG.Tweening.Tween", true);
                if (tween.Assembly != dotween)
                {
                    throw new InvalidOperationException("DG.Tweening.Tween 必须由 wrapper 引用的 DOTween 程序集定义。");
                }

                assemblies.Add(tween.Assembly);
            }
            return assemblies.Select(assembly => assembly.Location).Distinct().ToArray();
        }

        /// <summary>由当前模块定义建立帮助 API 索引，不启动动作。</summary>
        internal static HelpApiType[] BuildIndex() => HelpApiIndex.Build(ActionKitHelpProvider.CreateModule());

        /// <summary>按根类型名查找关联主题，并为屏幕过渡嵌套类型保留专属主题。</summary>
        /// <param name="type">要查找指南关联的 API 类型。</param>
        internal static IEnumerable<Topic> RelatedTopics(Type type)
        {
            var root = type;
            while (root.IsNested && root != typeof(ActionKit.ScreenTransition))
            {
                root = root.DeclaringType;
            }

            var name = root.Name.Split('`')[0];
            return Topics.Where(topic => topic.TypeNames.Contains(name));
        }

        /// <summary>从既有索引中筛选主题指定的优先展示类型。</summary>
        /// <param name="topic">提供优先类型配置的主题。</param>
        /// <param name="index">已建立的 API 索引。</param>
        internal static IEnumerable<HelpApiType> FeaturedTypes(Topic topic, HelpApiType[] index) => index.Where(entry =>
            (!entry.Type.IsNested && topic.FeaturedTypeNames.Contains(entry.Type.Name.Split('`')[0])) ||
            topic.FeaturedTypeNames.Contains(entry.Name));

        /// <summary>沿用共享帮助搜索规则匹配文本。</summary>
        /// <param name="text">待匹配的文本。</param>
        /// <param name="search">搜索条件。</param>
        internal static bool Contains(string text, string search) => HelpSearch.Contains(text, search);
        /// <summary>取得类型自身声明、可供帮助展示的 API 成员。</summary>
        /// <param name="type">待索引类型。</param>
        internal static IEnumerable<MemberInfo> GetDeclaredApi(Type type) => HelpApiFormatter.GetDeclaredApi(type);
        /// <summary>按 Action 命名空间的展示规则格式化类型名。</summary>
        /// <param name="type">待格式化类型。</param>
        internal static string FormatType(Type type) => HelpApiFormatter.FormatType(type, "YLFramework.Actions");
        /// <summary>按 Action 命名空间的展示规则格式化类型声明。</summary>
        /// <param name="type">待格式化类型。</param>
        internal static string FormatDeclaration(Type type) => HelpApiFormatter.FormatDeclaration(type, "YLFramework.Actions");
        /// <summary>按 Action 命名空间的展示规则格式化成员签名。</summary>
        /// <param name="member">待格式化成员。</param>
        internal static string FormatSignature(MemberInfo member) => HelpApiFormatter.FormatSignature(member, "YLFramework.Actions");

        /// <summary>将当前可用的方法片段拼为验收用源码文本，不写文件、不编译或执行。</summary>
        internal static string ExampleCompilationSource() =>
            "using System;\nusing System.Collections;\nusing System.Threading.Tasks;\nusing UnityEngine;\n" +
            "using YLFramework.Actions;\n" +
            "using YLFramework.Events;\n" +
            (ApiAssemblies().Any(assembly => assembly.GetName().Name == "YLFramework.Actions.DOTween")
                ? "using YLFramework.Integrations.DOTween;\nusing DG.Tweening;\n" : "") +
            "namespace ActionKitHelpExamples\n{\npublic static class Usage\n{\n" +
            string.Join("\n\n", Topics.Where(topic => topic.IsAvailable && !topic.IsCompleteExample && topic.Example.Length != 0).Select(topic => topic.Example)) +
            "\n}\n}\n";

        /// <summary>取得唯一完整入门脚本的源码文本，供展示和独立验收使用。</summary>
        internal static string GettingStartedSource => Topics.Single(topic => topic.IsCompleteExample).Example;
    }
}
