using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Actions;
using YLFramework.Editor.Help;

namespace YLFramework.Editor.Actions
{
    /// <summary>显式注册 ActionKit 帮助入口，并组合指南、公开 API 索引和 XML 文档路径。</summary>
    [InitializeOnLoad]
    internal static class ActionKitHelpProvider
    {
        /// <summary>帮助注册表和窗口定位 ActionKit 时使用的稳定标识。</summary>
        internal const string ModuleId = "actionkit";
        /// <summary>打开 ActionKit 帮助的编辑器菜单路径。</summary>
        internal const string MenuPath = "YLFramework/Action/帮助";

        /// <summary>在 Editor 程序集初始化时登记内容工厂，不提前创建帮助内容。</summary>
        static ActionKitHelpProvider() => HelpRegistry.Register(ModuleId, "ActionKit", CreateModule);

        /// <summary>打开帮助窗口并选中 ActionKit 模块。</summary>
        [MenuItem(MenuPath)]
        private static void Open() => HelpWindow.Open(ModuleId);

        /// <summary>从当前已加载的核心及可选包装程序集建立帮助定义，不执行示例或运行时属性。</summary>
        internal static HelpModule CreateModule()
        {
            var assemblies = ActionKitApiHelpContent.ApiAssemblies().ToArray();
            var types = assemblies.SelectMany(assembly => assembly.GetExportedTypes())
                .Where(type => type.Namespace == "YLFramework.Actions" ||
                    (type.Namespace != null && (type.Namespace.StartsWith("YLFramework.Actions.", StringComparison.Ordinal) ||
                        type.Namespace == "YLFramework.Integrations.DOTween")))
                .Where(type => type.Assembly == typeof(ActionKit).Assembly ||
                    type.FullName == "YLFramework.Integrations.DOTween.DOTweenAction" || type.FullName == "YLFramework.Integrations.DOTween.ActionKitDOTweenExtension")
                .OrderBy(ActionKitApiHelpReference.TypeGroup, StringComparer.Ordinal)
                .ThenBy(ActionKitApiHelpContent.FormatType, StringComparer.Ordinal).ToArray();
            return new HelpModule
            {
                Guides = ActionKitApiHelpContent.GuideGroups.SelectMany(group => ActionKitApiHelpContent.Topics
                    .Where(topic => topic.Group == group).Select(topic => CreateGuide(topic, types))).ToArray(),
                About = CreateAbout(),
                ApiTypes = types.Select(type => new HelpApiTypeDefinition(type, ActionKitApiHelpReference.TypeGroup(type))).ToArray(),
                XmlPaths = assemblies.ToDictionary(assembly => assembly, assembly => Path.GetFullPath(Path.Combine(
                    Application.dataPath, "..", "Library", assembly.GetName().Name + ".xml"))),
                Documentation = ActionKitApiHelpReference.Describe
            };
        }

        /// <summary>将主题内容和关联类型组成指南页，为未加载的可选程序集添加接入提示。</summary>
        /// <param name="topic">指南主题及其示例、说明和类型关联。</param>
        /// <param name="types">本次模块索引纳入的公开类型。</param>
        private static HelpPage CreateGuide(ActionKitApiHelpContent.Topic topic, Type[] types)
        {
            var blocks = new List<HelpBlock>();
            if (!topic.IsAvailable)
            {
                blocks.Add(HelpBlock.Notice("可选程序集未加载", "此指南使用可选程序集 " + topic.RequiredAssembly +
                    "，当前未加载。代码仅供查阅，接入前需要安装并引用该程序集。"));
            }

            if (topic.Example.Length != 0)
            {
                blocks.Add(HelpBlock.Code("常用用法", topic.ExampleContext, topic.Example));
            }
            else if (!string.IsNullOrWhiteSpace(topic.ExampleContext))
            {
                blocks.Add(HelpBlock.Paragraph("使用方式", topic.ExampleContext));
            }

            if (!string.IsNullOrWhiteSpace(topic.Behavior))
            {
                blocks.Add(HelpBlock.Paragraph("参数、执行与生命周期", topic.Behavior));
            }

            if (!string.IsNullOrWhiteSpace(topic.Notes))
            {
                blocks.Add(HelpBlock.Notice("注意事项", topic.Notes));
            }

            return new HelpPage(topic.Id, topic.Title, topic.Group, topic.Summary, blocks.ToArray())
            {
                Keywords = topic.TypeNames,
                RelatedTypes = types.Where(type => ActionKitApiHelpContent.RelatedTopics(type).Contains(topic)).ToArray(),
                FeaturedTypes = types.Where(type => (!type.IsNested && topic.FeaturedTypeNames.Contains(type.Name.Split('`')[0])) ||
                    topic.FeaturedTypeNames.Contains(ActionKitApiHelpContent.FormatType(type))).ToArray()
            };
        }

        /// <summary>建立模块来源、当前程序集版本和只读帮助边界的说明页。</summary>
        private static HelpPage[] CreateAbout() => new[]
        {
            new HelpPage("usage", "使用与版本", "关于", "",
                HelpBlock.Paragraph("", "YLFramework.Actions 是 YLFramework 内建的 Unity 动作编排模块，包含 BCL Observable 适配。使用方引用 YLFramework.Runtime 接入；" +
                    "沿用 YLFramework 的依赖契约，动作核心不要求 DOTween；可选包装程序集为 YLFramework.Actions.DOTween。\n\n" +
                    "Unity 版本：" + Application.unityVersion + "\nRuntime 程序集版本：" + typeof(ActionKit).Assembly.GetName().Version +
                    "\n程序集版本来自编译元数据，不代表单独的模块发布版本。\n\n" +
                    "帮助窗口只供查阅和复制，不执行示例、不创建动作宿主。API 声明来自当前编译程序集；说明按模块源码整理。"))
        };
    }
}
