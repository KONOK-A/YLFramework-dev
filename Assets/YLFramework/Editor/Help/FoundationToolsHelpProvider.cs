using System;
using System.IO;
using System.Linq;
using UnityEditor;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Serialization;

namespace YLFramework.Editor.Help
{
    /// <summary>显式提供 Foundation 与工具精选帮助，不自动收集全工程 API 或运行工具操作。</summary>
    [InitializeOnLoad]
    internal static class FoundationToolsHelpProvider
    {
        private const string ModuleId = "foundation-tools";

        /// <summary>在 Editor 初始化时注册内容工厂，内容留到模块被选中时才建立。</summary>
        static FoundationToolsHelpProvider() => HelpRegistry.Register(ModuleId, "Foundation / Tools", CreateModule);

        /// <summary>从 Toolkit 菜单打开公共帮助窗口并选中本模块。</summary>
        [MenuItem("YLFramework/Toolkit/帮助")]
        private static void Open() => HelpWindow.Open(ModuleId);

        /// <summary>建立指南与明确选定的类型，并按实际程序集指定 XML 读取路径；不生成缺失 XML。</summary>
        private static HelpModule CreateModule()
        {
            var guides = new[]
            {
                Guide("foundation", "Foundation 基础机制", "按需使用池、事件、绑定、计时与序列化，不要求创建 Module 或公共 Root。",
                    new[] { typeof(Pooling.ObjectPool<>), typeof(Events.EventBus), typeof(Binding.BindableProperty<>),
                        typeof(Timing.Timer), typeof(Timing.TimerScope), typeof(SerializedDictionary<,>) },
                    HelpBlock.Paragraph("引用与所有权", "业务 asmdef 显式引用 YLFramework.Runtime。Foundation与Core是同主体的职责分区；Core 与 Audio 已通过消费合同接入唯一 ResourceSystem；ObjectPool<T>的借用对象由调用方归还；" +
                        "EventBus 的 IUnregister 令牌由订阅所有者注销，独立 EventBus 实例须自行 Dispose。BindableProperty<T> 的监听沿同一注销协议。" +
                        "Timer/TimerScope 管理到期回调与生命周期，不替代 Action 的逐帧插值/组合。"),
                    HelpBlock.Paragraph("编辑与导航", "SerializedDictionary<TKey,TValue> 由普通 Editor 的现有 Drawer 编辑并提示重复键等问题。" +
                        "Singleton、Pool、Events、Binding、IOC、Timing、AssemblyTool 的完整契约见 Documentation/Modules 下对应说明；" +
                        "这里仅展示精选 API，不扫描整个工程或执行成员。"),
                    Document("基础模块导航", "README.md")),
                Guide("tools", "Tools 与 Editor 工具", "按需选用数据与 UI 便利工具，不增加统一工具宿主或配置中心。",
                    new[] { typeof(global::YLFramework.Utilities.StringTools), typeof(FSM.Fsm<>), typeof(Grids.NormalGrid<>), typeof(global::YLFramework.Utilities.Json.JSONObject),
                        typeof(global::YLFramework.UI.ColorSelector), typeof(global::YLFramework.UI.ColorSelectorWindow) },
                    HelpBlock.Paragraph("选用工具", "StringTools/Extensions、FSM、Grid、Json/JSONObject、Table 位于 YLFramework.Runtime；FSM 由调用方驱动及回收，" +
                         "不替代 Systems/StateMachine 的可视化状态机。ZipTool也归Runtime主体，源码仍BCL-only；Zip/GZip的reader以using管理寿命。" +
                         "这些工具不强制依赖 Core Module，也不把 Save/HTTP 改为另一套 JSON 内核。"),
                    HelpBlock.Paragraph("取色便利工具", "ColorSelector 的控件、渲染与窗口统一位于 Utilities/UI/ColorSelector，不是 Foundation 基础机制。" +
                        "窗口通过 Module.LoadPanel/LoadPanelAsync 显式打开，按 Prefab 默认、规则快照和本次覆盖合成；控件读取最近 Panel 的 IsLoaded/IsInputBlocked，无 Panel 时沿默认输入行为。" +
                        "IUIInputGate 与自定义宿主门禁已退出，不新增 Manager 或要求控件脱离 Core 编译。"),
                    HelpBlock.Paragraph("Editor 入口", "YLFramework/Resource/Workbench 用于配置资源项目、分析分包、构建与本地发布、版本交付和诊断。一个资源项目可登记多个已有来源目录及各自的地址前缀，无需把资源搬到统一专用根；组的选择项决定构建内容，图集生成目录按需单独登记。现有Editor配置已统一为此模型；从外部导入的旧单根配置不保证自动读取或迁移。" +
                        "YLFramework/Toolkit 下保留日志清理、打开目录与导出框架包。生成物与构建操作不从 Help 执行。" +
                        "Action/Audio 保持各自帮助和成熟工具，不在此复制其 API 文档。"),
                    HelpBlock.Notice("交付边界", "默认导出 Editor、Plugins/BouncyCastle、Prefabs、Runtime、Samples、Documentation 六个目录及框架根级文件。" +
                        "不含 Tests、根 Integrations、SDK 工具项目、NuGet/中间产物、游戏资源或 Assets/YLFrameworkData。" +
                         "框架仍需 UGUI、Newtonsoft 等既有依赖，主体不依赖外部资源框架。EventListener 使用原生 Inspector，Sirenix 插件已退出，宿主商业插件不分发。"),
                    Document("工具导航与导出说明", "README.md"),
                    Document("取色工具接入", "Modules/UI.md")),
                Guide("attributes", "Attributes 与显示边界", "Foundation 展示元数据与 Core 编译器诊断是不同机制。",
                    new[] { typeof(DisableEdit), typeof(CustomClassNameGui), typeof(CustomPropertyGui) },
                    HelpBlock.Paragraph("必要 Drawer", "DisableEdit的原生Drawer位于Editor主体，只在编辑器禁用序列化字段及子属性并恢复GUI状态；普通C# property不会因此变成可序列化字段。" +
                        "Attributes源码归Foundation，元数据不消费上层Core逻辑；主体身份与源码边界分开核验。"),
                    HelpBlock.Paragraph("显式扩展元数据", "CustomClassNameGui 保存类型显示名；CustomPropertyGui 是抽象字段绘制契约，当前没有内置自动扫描器或通用 Inspector 消费者。" +
                        "自定义 Editor 须显式读取并调用；OnPropertyGui 及其业务 override 必须保持 #if UNITY_EDITOR 隔离，Player 不包含该方法。"),
                    HelpBlock.Notice("声明与文档", "精选 API 来自真实编译元数据，读取不构造对象或访问静态实例属性。缺少 XML 时仍显示声明，不按名字编造摘要。" +
                        "Core 的 YL0001～YL0007 自有分析器、Warning 默认、作用域与源码重建入口见 Core 帮助及 Attributes 文档。"),
                    Document("Attributes、分析器与配置", "Modules/Attributes.md"))
            };
            var types = guides.SelectMany(p => p.FeaturedTypes).Distinct().ToArray();
            var toolTypes = guides.Single(p => p.Id == "tools").FeaturedTypes;
            return new HelpModule
            {
                Guides = guides,
                ApiTypes = types.Select(t => new HelpApiTypeDefinition(t,
                    toolTypes.Contains(t) ? "Tools 精选 API" : "Foundation 精选 API")).ToArray(),
                XmlPaths = types.Select(t => t.Assembly).Distinct().ToDictionary(a => a,
                    a => Path.GetFullPath("Library/" + a.GetName().Name + ".xml"))
            };
        }

        /// <summary>建立工具指南，将所选类型登记为关联 API 与正文精选入口。</summary>
        private static HelpPage Guide(string id, string title, string summary, Type[] types, params HelpBlock[] blocks) =>
            new HelpPage(id, title, "Foundation / Tools 指南", summary, blocks) { RelatedTypes = types, FeaturedTypes = types };

        /// <summary>为框架文档建立本地链接，不在创建帮助数据时读取或打开文档。</summary>
        private static HelpBlock Document(string title, string path) => HelpBlock.Link(title,
            new Uri(Path.GetFullPath("Assets/YLFramework/Documentation/" + path)).AbsoluteUri);
    }
}
