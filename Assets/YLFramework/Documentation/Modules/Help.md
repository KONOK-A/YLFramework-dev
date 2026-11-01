# 编辑器帮助（HelpKit）

> [文档首页](../README.md) · [架构总览](../Architecture.md) · [Action](Action.md) · [Audio](Audio.md)

## 模块定位

HelpKit 是 Unity Editor 内的只读帮助窗口基础设施：项目模块显式注册帮助内容，窗口统一提供"使用指南 / API 参考 / 关于"三个页签；API 声明来自编译元数据，中文摘要来自模块覆盖与 XML 文档。

- 主要能力：模块注册表、帮助内容模型、API 索引与签名格式化、XML 文档读取与合并、搜索与导航。
- 不负责：运行时帮助、自动扫描程序集/模块、执行示例代码、代码生成。
- HelpKit 自身不注册帮助模块；它是承载其他模块帮助的容器。

## 位置与隔离

| 目录 | 程序集 | 依赖 |
|---|---|---|
| [`Editor/Help/`](../../Editor/Help/) | `YLFramework.Editor` | 帮助注册、索引与窗口，共享 namespace `YLFramework.Editor.Help` |
| [`Editor/Systems/Action/Help/`](../../Editor/Systems/Action/Help/)、[`Editor/Systems/Audio/Help/`](../../Editor/Systems/Audio/Help/) | `YLFramework.Editor` | 对应领域提供方，按显式类型索引 |
| [`Editor/Help/FoundationToolsHelpProvider.cs`](../../Editor/Help/FoundationToolsHelpProvider.cs) | `YLFramework.Editor` | 基础机制与工具提供方，namespace 为 `YLFramework.Editor.Help` |
| [`Editor/Core/CoreHelpProvider.cs`](../../Editor/Core/CoreHelpProvider.cs) | `YLFramework.Editor` | Core提供方；主体依赖Runtime/UGUI/Json.NET，不要求外部资源后端 |
| 源码仓库 `Tests/EditMode/Editor/Help/` | `YLFramework.Tests.EditMode` | 复用普通 EditMode 程序集，不随默认包交付 |

Core 提供方 namespace 为 `YLFramework.Editor.Core`，Action / Audio 提供方为 `YLFramework.Editor.Actions` / `.Audio`；所有正式编辑器类型共用 Editor-only 主体。帮助核心与领域提供方按职责分离，注册表不发现模块，API 索引不自动收集依赖程序集。

Runtime / Editor 主体的 `autoReferenced` 为 true；具体程序集依赖和旧二进制身份迁移见 [架构总览](../Architecture.md)。

<a id="core-concepts"></a>

## 核心概念

- `HelpRegistry` / `HelpRegistration`：`Register(id, displayName, Func<HelpModule>)`；重复 id 替换并保持登记顺序；内容工厂在被选中时才调用；不扫描程序集。
- `HelpModule`：模块内容描述，包括 `Guides`、`About`、`ApiTypes`（显式选择的类型）、`XmlPaths`（按 `Assembly` 键）、`Documentation` 覆盖回调和初始折叠组。
- `HelpPage` / `HelpBlock`：页面与四种内容块（Text / Code / Notice / Link）；`RelatedTypes` 与 `FeaturedTypes` 关联 API。
- `HelpApiTypeDefinition` / `HelpApiType` / `HelpApiMember`：显式类型 → 索引结果（声明、签名、成员、合并后的 `HelpDocumentation`）。
- `HelpApiIndex.Build(module)`：一次索引构建；只处理显式选择的类型；成员仅 public / protected（含 protected internal）且 declared-only。
- `HelpXmlDocumentation`：编译器成员 ID 映射 Reflection；支持 `inheritdoc`；模块 `Documentation` 的非空白字段优先于 XML。
- `HelpSearch`：空白分词 AND、大小写不敏感；空查询匹配全部。
- `HelpWindow`：三页签（使用指南 / API 参考 / 关于）、模块下拉、搜索高亮、返回历史；窗口状态经 `SerializeField` 持久化。

<a id="main-entry-points"></a>

## 主要入口

1. [`HelpRegistry.cs`](../../Editor/Help/HelpRegistry.cs)：模块接入协议。
2. [`HelpModel.cs`](../../Editor/Help/HelpModel.cs)：内容模型。
3. [`HelpWindow.cs`](../../Editor/Help/HelpWindow.cs)：窗口与 `Open(moduleId)` 入口。
4. [`Api/HelpApiIndex.cs`](../../Editor/Help/Api/HelpApiIndex.cs)：索引、声明 / 签名与合并。
5. [`Api/HelpXmlDocumentation.cs`](../../Editor/Help/Api/HelpXmlDocumentation.cs)：XML 成员 ID 与 `inheritdoc`。

<a id="runtime-flow"></a>

## 编辑器流程

没有运行时生命周期，编辑器内流程如下：

```text
注册：模块 Editor 程序集 [InitializeOnLoad] 静态构造 → HelpRegistry.Register(id, displayName, factory)
打开：菜单 YLFramework/帮助 或模块快捷菜单 → HelpWindow.Open(id) → SelectModule → LoadModule
构建：registration.Create()（调用工厂）→ HelpApiIndex.Build（反射 + XML + Documentation 覆盖合并）
交互：OnGUI → 页签/模块下拉/搜索/返回；注册表变更时检查当前注册项，替换后重建内容
```

<a id="extension-points"></a>

## 接入方式

- 新增模块帮助 = 一个提供方类：`[InitializeOnLoad]` 静态构造中 `HelpRegistry.Register(id, 显示名, Func<HelpModule>)`，并提供模块菜单调用 `HelpWindow.Open(id)`。**不存在 provider 接口**，注册协议就是唯一契约。
- 内容扩展：`HelpPage` / `HelpBlock` 工厂方法；`Documentation` 回调做中文覆盖；`XmlPaths` 指定 XML；`CollapsedGuideGroups` / `CollapsedApiGroups` 控制初始折叠。
- 无渲染扩展点：窗口与渲染为固定 IMGUI 实现，相关类型多为 internal。

最小提供方放在 Editor 程序集中，显式登记指南内容；需要 API 索引时再填入 `ApiTypes`：

```csharp
using UnityEditor;
using YLFramework.Editor.Help;

[InitializeOnLoad]
internal static class InventoryHelp
{
    static InventoryHelp() => HelpRegistry.Register("inventory", "背包", () => new HelpModule
    {
        Guides = new[]
        {
            new HelpPage("start", "接入", "使用指南", "背包模块接入说明",
                HelpBlock.Paragraph("初始化", "在业务入口初始化背包。"))
        }
    });

    [MenuItem("Game/背包帮助")]
    private static void Open() => HelpWindow.Open("inventory");
}
```

类型与页面注册只提供展示数据，不使业务 API 自动执行或接入 Runtime。

## 测试

- `HelpWindowTests`：注册表的显式/惰性/重复 id 替换语义、模块切换不串搜索与页签、空注册时窗口完整可用、菜单与"窗口程序集不依赖业务模块"约束、布局尺寸。
- `ApiHelpTests`：签名/声明格式化（可见性、泛型、默认值）、索引顺序与真实类型关联、XML 读取与合并规则、搜索匹配、XML 缺失回退。
- `FrameworkHelpProviderTests`：Core / Foundation-Tools 的正式注册、精选类型与指南关联、规则检索、缺 XML 声明、真实窗口搜索/页签/重复注册刷新、关闭订阅释放及不创建宿主/改写场景或生成文件。
- 测试复用 `YLFramework.Tests.EditMode`，带 `UNITY_INCLUDE_TESTS`；Action / Audio 提供方的 API 库存、示例编译与 XML 回归位于各自测试目录，见 [Action](Action.md#测试与帮助)、[Audio](Audio.md#测试与帮助)。

上述为源码仓库测试入口，不表示当前环境已执行，也不代替真实 GUI 交互检查；默认分发包不包含 Tests。

## 注册、XML 与菜单

- HelpKit 自身不注册帮助模块；打开 `YLFramework/帮助` 可看到 Core、Foundation / Tools、ActionKit、AudioKit。
- 窗口标题："项目帮助"或 "`<DisplayName>` 帮助"。
- 模块快捷菜单：`YLFramework/Action/帮助`、`YLFramework/Audio/帮助`；稳定注册 id 为 `actionkit` / `audiokit`，显示名仍为 ActionKit / AudioKit。
- `YLFramework/Core/帮助` 的稳定 id 为 `core`：Module/MVC、View/UI 最小接入、现有生成器入口、依赖边界，以及编译器诊断的作用域、配置与源码重建。详细规则只维护在 [Attributes](Attributes.md#core-使用诊断)，Help 按共享目录展示，不另存固定规则数量。
- `YLFramework/Toolkit/帮助` 的稳定 id 为 `foundation-tools`：Foundation、Tools、Attributes 的简洁导航与精选真实 API。CustomClassNameGui/CustomPropertyGui 仍是显式扩展元数据，不宣称已有自动 Inspector 平台。
- 内容工厂在实际加载模块时惰性调用；窗口复用未变化的当前注册项，切换模块或替换该注册项才重新构建。API 索引只读取元数据与文档，不执行成员或示例，也不初始化 Runtime 宿主。
- Action/Audio Provider 按所属 namespace 的精确或点分边界筛选类型；Core/Foundation-Tools 直接列出精选类型，共享 Runtime 不意味着展示全程序集。Observable / Audio bridge 内建，只有 DOTween 指南保留 optional 门控。
- 唯一 [`Runtime/csc.rsp`](../../Runtime/csc.rsp) 生成 `Library/YLFramework.Runtime.xml`，覆盖 Action、Observable、Audio、bridge；optional 生成 `Library/YLFramework.Actions.DOTween.xml`。`XmlPaths` 按真实 Assembly key 去重。
- Help 优先显式 XML 路径，仅文件缺失时回退 DLL 邻接 XML；格式或访问错误只警告，不掩盖为路径回退。`Library/` 是构建产物，不默认导出。
- Core / Foundation 精选类型与其他 Runtime 类型共用 `Library/YLFramework.Runtime.xml`；XML 是编译产物，可能缺失或过时。提供方不额外生成 XML，缺失时保留元数据声明与人工指南，不根据名称伪造成员说明。文档链接仅在点击时打开本地说明，不执行示例或工具操作。
- `HelpWindow` 与 `AudioKitDebugEditorWindow` 针对适用的 Unity layout 旧身份保留 `MovedFrom`；没有 namespace / assembly wrapper，也不使 `Type.GetType` 把旧 AQN 当作别名。旧 API 导航身份无法匹配时沿用窗口回退，不另建持久化名字转换系统。
- 默认框架包包含 Editor 下帮助核心与提供方及 Documentation，不含 Tests 或根目录 optional；导出与依赖边界见 [文档首页](../README.md#帮助与导出)。

<a id="known-notes"></a>

## 使用限制

- 仅供 Editor，不提供运行时帮助窗口；引用程序集为 `YLFramework.Editor`。
- API 索引不展开继承成员，也不执行 API。类型未列入 `ApiTypes` 就不会自动进入索引。
- 内容工厂、`Documentation` 覆盖回调是提供方代码；窗口不把它们变成沙箱，回调应保持只读，异常不能当作“无内容”。
- `CustomPropertyGui` 的领域 Inspector 接入与 Help 的展示无关；已有消费位置见 [Attributes](Attributes.md#custompropertygui)，不能将帮助导航当成自动 Inspector 注册系统。
