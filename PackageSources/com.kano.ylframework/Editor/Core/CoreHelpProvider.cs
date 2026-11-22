using System;
using System.IO;
using System.Linq;
using UnityEditor;
using YLFramework.Core.RoslynAnalyzers;
using YLFramework.Editor.Core.Analyzers;
using YLFramework.Editor.Help;
using YLFramework.UI;
using YLFramework.Core;

namespace YLFramework.Editor.Core
{
    /// <summary>显式注册 Core 的精选指南、API 类型和 XML 路径，不扫描业务模块或执行生成器。</summary>
    [InitializeOnLoad]
    internal static class CoreHelpProvider
    {
        private const string ModuleId = "core";

        /// <summary>在 Editor 初始化时登记惰性内容工厂，尚不建立模块内容或 API 索引。</summary>
        static CoreHelpProvider() => HelpRegistry.Register(ModuleId, "Core", CreateModule);

        /// <summary>从 Core 菜单打开公共帮助窗口并选择本模块。</summary>
        [MenuItem("YLFramework/Core/帮助")]
        private static void Open() => HelpWindow.Open(ModuleId);

        /// <summary>建立本模块的静态展示数据；XML 路径仅指明读取来源，不生成缺失文档。</summary>
        private static HelpModule CreateModule()
        {
            var guides = new[]
            {
                Guide("module-mvc", "Module 与 MVC", "Module 是业务所有者，Model 保存状态，Controller 协调逻辑，View 负责表现。",
                    new[] { typeof(Module), typeof(SingletonModule<>), typeof(ModuleManager), typeof(Model), typeof(Controller) },
                    HelpBlock.Paragraph("显式启动与结束", "派生 Module 或 SingletonModule<T>，实现 OnStart，并通过 ModuleManager.StartUpModule<T>() 启动。" +
                        "启动请求完成后再使用模块；SingletonModule<T>.Instance 只查询已注册实例，不会创建。Module.Close 结束普通模块，基础模块由 ModuleManager.Shutdown 统一结束。"),
                    HelpBlock.Paragraph("MVC 所有权", "通过 Module.AddModel / GetModel、AddController / GetController 管理注册，不从 View 改写 Model 的受保护状态。" +
                        "Model/Controller 的 OnInit 与 OnExit 对应每次注册；Module 负责退出，业务监听与借用资源仍须按自身所有权收尾。"),
                    HelpBlock.Notice("接入边界", "业务 asmdef 显式引用 YLFramework.Runtime；Core、Foundation 和 ZipTool 共用该主体程序集，仍按职责组织源码。" +
                        "ProjectName 是官方 address 的业务命名空间；Core 将项目/路径投影为 project/path，缺失 Provider 或地址歧义明确失败。" +
                        "Core 与 Audio 借用同一 Addressables 资源服务，不建立第二套内容运行栈。"),
                    HelpBlock.Paragraph("资源启动与退出", "应用持有 ResourceStartup(new ResourceConfiguration())，等待 StartAsync 成功后再启动 Module；默认绑定 Core，可通过 audioProjects 明确装配音频命名空间。" +
                        "业务用 Resources.CreateScope(label) 和 Scope.LoadAsync 借用；Scope.InstantiateAsync 每次新建，RentAsync 才复用，冷同步加载明确拒绝。独立责任使用 AcquireAsync/Retain 并自行归还。" +
                        "先关闭业务 Module、停止播放并结算各自责任，等待 Scope.CloseAsync 后再等待 Startup.StopAsync；Dispose 只发起，不表示完成。Busy 时读取 Resources.GetDiagnostics 的 owner/key 并由真实持有者归还后重试。" +
                        "Scope 场景默认自动激活的 Additive；应用保留中立 Bootstrap 场景，Root 不会为停机临时制造空场景。" +
                        "当前 Module.DefaultStartUpScene 非空时使用 Core 的 Single 入口；需要保留 Bootstrap 时留空并由业务 Scope 显式加载。")),
                Guide("view-ui", "View 与 UI", "Module 唯一拥有业务；Panel 继承 View，使用统一请求和可选配置快照，不需要窗口目录。",
                    new[] { typeof(View), typeof(Panel), typeof(LoadViewArgs), typeof(LoadViewRequest<>), typeof(LoadPanelRequest<>), typeof(PanelConfig), typeof(PanelOverrides), typeof(PanelRule), typeof(UIRootContext) },
                    HelpBlock.Paragraph("加载与参数", "场景或动态对象使用 owner.AttachView(view) 显式纳管；Prefab/具名资产使用 Module.LoadView / LoadPanel 或异步入口。" +
                        "普通根 View 使用 LoadView，根 Panel 使用 LoadPanel/LoadPanelAsync，支持完整地址、类型名和直接 Prefab。异步接受后先 Pending，最早下一帧回调；检查 Status/Exception，提交前可独立 Abort。" +
                        "LoadViewArgs 由入口接管归还，普通 View 在 OnLoaded 借读；Panel 每次有效打开在 OnOpen 借读，不保存参数容器。"),
                    HelpBlock.Paragraph("生命周期", "在 Awake 的直接方法体使用 RegisterEvent 声明订阅，框架按有效启用轮次管理。覆写 Start 时直接调用 base.Start()。" +
                        "使用 OnViewEnabled、OnViewDisabled、OnClosed，不声明 OnEnable/OnDisable/OnDestroy 遮蔽框架消息。关闭走 View.Close，不能靠保存旧 lease 复用会话。"),
                    HelpBlock.Paragraph("配置与根", "Prefab 七字段默认 → Module.BindPanelRules 的不可变规则快照 → 本次 PanelOverrides。显式 false/0 会覆盖；规则按完整地址或 Prefab 精确匹配，不是注册门槛。" +
                        "默认 Single/可缓存/参与 Top；Single 范围是 Module/实际根/已证明来源。根优先级为显式参数 → Module.PanelRoot → YLFrameworkRoot 宿主默认根，接受后固定。" +
                        "UIRootContext 只保留 Canvas、GraphicRaycaster、显式层和可选遮罩；Camera/RenderMode 直接配置 Canvas，缩放只配置原生 CanvasScaler 与 AutomaticAdaptationCanvasScaler。根注册不改变对象寿命。"),
                    HelpBlock.Paragraph("显示与输入", "Layer → 同层 Modal 分组 → Priority → 接受/前置序号生成顺序。CoversRoot 只声明覆盖，不改布局；ParticipatesInTop 独立于 Modal。" +
                        "使用 Hide/Show/BringToFront/UpdateDisplay 五字段更新；OnOpen 中只读显示，不能 Close 当前 Panel/owner 或重入本 Module 的 Panel 打开。输入只门禁标准 UI，不关闭业务 ActionMap，不改写控件业务字段。"),
                    HelpBlock.Notice("资产核验", "已有资产不会运行时自修复。使用 YLFramework/Core/UI Panel/核验正式资产 只读检查正式模板；一次性迁移与旧 fileID 修补入口已退役。自有模板通过 Unity 流程检查实际引用和差异；只读检查或编译不等于输入、连续 Play 或 Player 验收。"),
                    Document("UI 接入、Tips 与 ColorSelector", "Modules/UI.md")),
                Guide("authoring", "创建与生成入口", "创建操作留在所属工具，帮助只提供导航，不执行生成器。",
                    new[] { typeof(Module), typeof(PanelRule) },
                    HelpBlock.Paragraph("Module 模板", "Assets/Create/YLFramework/Core/Module 输出 " + CreateModuleCommand.OutputRoot + " 下的独立用户代码和 asmdef，拒绝覆盖已有目录。" +
                        "namespace 为 YLFramework.Core.Examples；不会生成完整 MVC/View/Panel 代码或改写 Assets/Games。"),
                    HelpBlock.Paragraph("默认资源与资源常量", "YLFramework/Core/Create Missing Default Resources 只补缺失的 Core Canvas/EventSystem，不替换已有身份。" +
                        "官方 Addressables Settings/Groups/Labels/Profiles/AssetReference 是唯一作者事实；YLFramework/Resource/Workbench 只增强校验、官方导航、地址与 Label 常量及正式内容构建。Group 是构建事实，不是运行时加载集合。" +
                        "ResourceEditorOperations.BuildContent/BuildContentUpdate 使用官方完整构建与内容更新，并使用目标 Player 的官方内容状态；Editor 作者工具不启动 Native 资源会话。" +
                        "常量默认生成到 Assets/YLFrameworkData/Generated/ResourceReferences/YLResourceReferences.cs，类型为 YLFramework.Resources.ResourceNames，生成程序集为 YLFramework.Generated.ResourceNames。" +
                        "业务 asmdef 显式引用生成程序集，未知/手写承载拒绝覆盖。这些用户生成物不随 UPM 包交付。"),
                    Document("资源工具与生成承载", "Modules/Resource.md")),
                Guide("analyzers", "自有分析器 " + AnalyzerRuleCatalog.Rules[0].Id + "～" + AnalyzerRuleCatalog.Rules[AnalyzerRuleCatalog.Rules.Count - 1].Id,
                    AnalyzerRuleCatalog.Rules.Count + " 项诊断默认启用且均为 Warning；提示使用约束，不是运行时安全证明。",
                    new[] { typeof(MustCallBaseAttribute), typeof(RestrictedCallAttribute), typeof(WarnInClassesAttribute),
                        typeof(DisableNewAttribute), typeof(MethodLocalOnlyAttribute), typeof(ProtectedHierarchyAttribute), typeof(View) },
                    HelpBlock.Paragraph("规则与改正方向", string.Join("\n", AnalyzerRuleCatalog.Rules.Select(rule =>
                        rule.Id + " " + rule.Description))),
                    HelpBlock.Paragraph("作用域与匹配", "唯一 YLFramework.Analyzers.dll 位于 Runtime/Core/Analyzers，即 YLFramework.Runtime 的 asmdef 范围，带 RoslynAnalyzer 标签。" +
                        "主体 Runtime 和实际引用它的 Editor/Test/游戏消费者参与；外部包、未引用主体或预定义程序集的作用域以真实编译图和 csc 输入为准。" +
                        "匹配正式程序集的 marker/View 符号，不按短名或 namespace 冒认。Editor/Test/Core 不整体豁免；生成代码默认不分析。"),
                    HelpBlock.Notice("编译器资产与静态限制", "DLL 的 Any/Editor/普通平台均禁用，关闭自动 Runtime 引用，只作为 -analyzer 输入，不加入 Runtime precompiledReferences。" +
                        "YL0005 只保留 LoadViewArgs._pool、LoadViewRequest<T>._args 与 PanelManager.PanelOperation.Args 三个真实内部 owner 例外；反射、object 擦除、闭包逃逸、任意跨过程副作用和所有控制流不保证。" +
                        "其他分析器拥有独立身份，不关闭或替代它们。"),
                    HelpBlock.Paragraph("单 ID 配置", "IDE/SDK 使用 .editorconfig 的 dotnet_diagnostic.YL0002.severity 等设置；Unity 2022.3 Console 不自动遵循 IDE 的 editorconfig。" +
                        "Unity 使用消费者 asmdef 目录的叶子 .ruleset 配置单 ID，Error 阻止编译，None 抑制该 ID；不要为局部需求覆盖根 Default.ruleset。" +
                         "故意契约测试可在声明/语句处精确 pragma warning disable 并立即 restore，不禁用整套诊断。"),
                    HelpBlock.Paragraph("规则设置与接入自检", "打开 Project Settings > YLFramework > Analyzers，或菜单 YLFramework/Core/分析器规则。" +
                        "支持业务 asmdef 和预定义程序集；原生 ruleset 先预览再应用，保留既有继承与其他规则。Error 可阻断编译，恢复为 Warning/继承或手改目标文件。" +
                        "接入结构检查、保存成功和零诊断都不是规则实际执行的证明，未知状态明确显示未核实。"),
                    HelpBlock.Paragraph("新增使用契约", "YL0008 允许 private setter，不推测注册或容器扫描模式。YL0009 包含旧泛型重载与确定的 Panel 约束，不把 parent 机械替换为 root。" +
                        "YL0010 仅检查非泛型 IEnumerator 协程；等待开始使用 WaitUntil(() => request.HasStarted || request.IsDone)，等待结束使用 WaitUntil(() => request.IsDone)，循环播放可能不结束。"),
                    HelpBlock.Paragraph("从源码重建", "源码仓库 Tools/YLFramework.Analyzers 使用固定 SDK 9.0.312、Roslyn API 3.8.0、netstandard2.0 产物和 NuGet lock。" +
                        "在仓库根执行 pwsh -NoLogo -NoProfile -NonInteractive -File \"Tools/YLFramework.Analyzers/build.ps1\"，默认 locked restore、Release build/test，不部署；" +
                        "显式追加 -Deploy 才替换唯一 DLL 并保留 meta，随后核对 Unity 导入与实际编译。UnityPackage 携带 DLL/meta/说明，不含 SDK 源码项目、测试、NuGet 或中间产物，重建需取得源码仓库。"),
                    Document("规则示例、配置与重建细节", "Modules/Attributes.md"))
            };
            return new HelpModule
            {
                Guides = guides,
                ApiTypes = guides.SelectMany(p => p.FeaturedTypes).Distinct().Select(t => new HelpApiTypeDefinition(t,
                    t.Namespace == "YLFramework.Core.RoslynAnalyzers" ? "分析器标记" : "Core 精选 API")).ToArray(),
                XmlPaths = { [typeof(Module).Assembly] = Path.GetFullPath("Library/YLFramework.Runtime.xml") }
            };
        }

        /// <summary>建立 Core 指南，并将明确选定的类型同时用于反向关联和正文精选入口。</summary>
        private static HelpPage Guide(string id, string title, string summary, Type[] types, params HelpBlock[] blocks) =>
            new HelpPage(id, title, "Core 指南", summary, blocks) { RelatedTypes = types, FeaturedTypes = types };

        /// <summary>将框架文档的相对路径转换为链接；实际打开交由用户点击触发。</summary>
        private static HelpBlock Document(string title, string path) => HelpBlock.Link(title,
            new Uri(global::YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(CoreHelpProvider).Assembly,
                "Documentation~/" + path)).AbsoluteUri);
    }
}
