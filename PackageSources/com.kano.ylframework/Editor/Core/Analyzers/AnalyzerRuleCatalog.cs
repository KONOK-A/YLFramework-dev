using System;
using System.Collections.Generic;

namespace YLFramework.Editor.Core.Analyzers
{
    // 该只读源文件也由独立 Analyzer 项目链接编译，不可引用 Unity 或 Roslyn。
    /// <summary>帮助页、规则设置与独立分析器共用的诊断身份和说明目录，不承载诊断执行逻辑。</summary>
    internal static class AnalyzerRuleCatalog
    {
        /// <summary>按诊断 ID 排列的只读规则集合；默认严重度由分析器声明，用户覆盖由 ruleset 管理。</summary>
        internal static IReadOnlyList<AnalyzerRuleInfo> Rules
        {
            get;
        } = Array.AsReadOnly(new[]
        {
            new AnalyzerRuleInfo("YL0001", "覆写方法缺少基类调用", "YLFramework.Usage",
                "MustCallBase：override 的直接方法体须存在正确 base 槽调用，不证明全路径、顺序或次数。"),
            new AnalyzerRuleInfo("YL0002", "受限方法调用位置不合法", "YLFramework.Usage",
                "RestrictedCall：只允许指定的直接调用者；Lambda/local 不借用外层 Awake 身份。"),
            new AnalyzerRuleInfo("YL0003", "指定类型中的方法调用警告", "YLFramework.Usage",
                "WarnInClasses：指定类型、基类或接口内的调用提示；View 事件优先使用 RegisterEvent。"),
            new AnalyzerRuleInfo("YL0004", "类型应通过正式工厂创建", "YLFramework.Usage",
                "DisableNew：受管类型改用 LoadViewArgs.New() 等正式工厂创建，保留真实内部 owner 例外。"),
            new AnalyzerRuleInfo("YL0005", "成员持有仅限方法借用的类型", "YLFramework.Usage",
                "MethodLocalOnly：字段、属性及实际容器不能持有仅限方法借用的类型；方法借用允许。"),
            new AnalyzerRuleInfo("YL0006", "禁止修改受保护实例", "YLFramework.Usage",
                "ProtectedHierarchy：指定类型层级不允许写受保护实例或暴露可写 ref/out；正常读取允许。"),
            new AnalyzerRuleInfo("YL0007", "View 框架消息被遮蔽", "YLFramework.Lifecycle",
                "View 派生不要遮蔽 OnEnable/OnDisable/OnDestroy，改用 OnViewEnabled/OnViewDisabled/OnClosed。"),
            new AnalyzerRuleInfo("YL0008", "不支持的成员注入声明", "YLFramework.Injection",
                "Inject 不支持 static/const/readonly 字段、static 属性、无 setter 属性或 indexer；private setter 允许。"),
            new AnalyzerRuleInfo("YL0009", "View 加载入口不能加载 Panel", "YLFramework.Usage",
                "LoadView/LoadViewAsync 的泛型类型确定为 Panel 时，改用 LoadPanel/LoadPanelAsync；parent 与 root 不等价。"),
            new AnalyzerRuleInfo("YL0010", "音频请求不是协程等待指令", "YLFramework.Audio",
                "非泛型 IEnumerator 协程不要直接 yield AudioPlayRequest；用 WaitUntil 显式等开始或结束，自定义调度器可局部抑制。")
        });
    }

    /// <summary>单项规则的不可变展示元数据，避免 Editor 与独立分析器维护不同的名称和说明。</summary>
    internal sealed class AnalyzerRuleInfo
    {
        /// <summary>供诊断、配置和文档关联使用的稳定规则标识。</summary>
        internal string Id
        {
            get;
        }
        /// <summary>诊断描述符和设置页使用的短标题。</summary>
        internal string Title
        {
            get;
        }
        /// <summary>诊断描述符所属的规则类别。</summary>
        internal string Category
        {
            get;
        }
        /// <summary>规则边界及建议改正方向，不作为运行时检查依据。</summary>
        internal string Description
        {
            get;
        }
        /// <summary>本地 Attributes 文档中与规则对应的小写锚点。</summary>
        internal string DocumentationAnchor => Id.ToLowerInvariant();

        /// <summary>保存一项规则的共享元数据，不注册规则或改变严重度。</summary>
        internal AnalyzerRuleInfo(string id, string title, string category, string description)
        {
            Id = id;
            Title = title;
            Category = category;
            Description = description;
        }
    }
}
