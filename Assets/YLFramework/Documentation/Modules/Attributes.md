# 元数据与诊断（Attributes）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供两类互不替代的能力：Foundation 的类型显示名、自定义字段绘制/刷新配置与只读 Inspector 元数据；Core 的编译器使用诊断。前者由 Editor 消费，后者由 Roslyn Analyzer 检查，都不是运行时权限边界。

定位边界：不是 Odin-like Inspector 框架 / PropertyDrawer 框架 / Attribute Processor，不提供 Attribute 自动扫描或注册系统。

## 位置

- 元数据：[`Runtime/Foundation/Attributes/`](../../Runtime/Foundation/Attributes/)，namespace `YLFramework.Attributes`。
- 必要原生 Drawer：[`Editor/Foundation/Attributes/DisableEditDrawer.cs`](../../Editor/Foundation/Attributes/DisableEditDrawer.cs)，namespace `YLFramework.Editor.Foundation`，程序集仍为 `YLFramework.Editor`，只在 Editor 主体中消费 Runtime 元数据。

## CustomClassNameGui

- 语义：为**类型**提供编辑器中的自定义显示名称（纯元数据）。
- 目标：`AttributeTargets.Class | Struct | Interface | Enum`；`AllowMultiple = false`；`Inherited = false`（派生类型不继承显示名，需自行标注）。
- 构造：`CustomClassNameGui(string name)`，允许 `null` / 空字符串；`GetName()` 原样返回，不会 NRE。调用方遇到 `null` / 空字符串时应回退为类型名。
- 消费方式：由编辑器代码读取，框架内暂无内置消费者。

## CustomPropertyGui

- 语义：标记需要自定义 Editor 绘制的序列化字段（metadata + Editor 契约）。
- 目标：`AttributeTargets.Field`；`AllowMultiple = false`；`Inherited = false`。
- 契约：抽象特性，仅供派生；编辑器下派生类**必须实现** `OnPropertyGui(UnityEditor.SerializedProperty)`。Mission / Skill / Buff Inspector 显式读取字段特性，未标注时按字段名选择领域 Drawer；Equipment Inspector 也通过字段名表使用派生 Drawer。它们不是自动扫描或通用 Inspector 平台。
- 构造：无参默认不刷新；`CustomPropertyGui(bool reloadTreeViewOnValueChange)` 配置刷新意图；public 字段 `ReloadTreeViewOnValueChange` 可供命名特性参数与接入方读取/修改，Editor 与 Player 均可用。是否刷新 TreeView 由实际 Inspector 处理。

## DisableEdit

- `PropertyAttribute`，保留 `Field | Property`、单次使用、默认继承的声明；原生 Inspector 实际绘制的是序列化字段，普通 C# property 不因此变为可序列化。
- 已注册的 Drawer 禁用字段及展开子属性，使用原生 `PropertyField` 与对应子属性高度；正常或抛异常时均恢复进入前的 GUI enabled 状态，不应用或保存序列化对象。

## 运行时与编辑器边界

- 三个特性均归 `YLFramework.Runtime`，元数据源码不消费 Core / Systems / Utilities；必要 Drawer 位于 Editor 主体。源码依赖边界与整个 Runtime DLL 的依赖列表应分别看待。
- 仅 `CustomPropertyGui.OnPropertyGui` 位于 `#if UNITY_EDITOR` 内：Editor 编译时存在、Player 编译时不存在，Player 程序集不会残留 `UnityEditor` 引用。
- 使用 `CustomPropertyGui` 的业务代码需注意：Player 下没有 `OnPropertyGui`，派生类中的重写必须包在 `#if UNITY_EDITOR` 内。

## 主要类型

- `CustomClassNameGui`：类型显示名特性。
- `CustomPropertyGui`：字段自定义绘制抽象特性。
- `DisableEdit`：原生 Inspector 只读绘制特性。

## 依赖

- `System`；`CustomPropertyGui` 的绘制方法条件编译依赖 UnityEditor（`#if UNITY_EDITOR`）。

## 编辑器剪贴板

[`Editor/Common/Inspector/SerializedPropertyExtensions.cs`](../../Editor/Common/Inspector/SerializedPropertyExtensions.cs) 使用 `YLFramework.Editor.Common`，提供 `CopyToClipboard`、`IsCanPaste`、`PasteFromClipboard` 与 Unity 原生 Clipboard 格式。粘贴先在独立 SerializedObject 缓冲中解析并完整核对，再以 `ApplyModifiedProperties` 应用；支持同类型、非混值的可编辑单对象和多对象，沿用 Undo/Redo 与 Prefab override。拒绝操作不会应用调用者的待修改数据。

混值、不同类型的多对象、不可编辑目标、不兼容或损坏的数据明确拒绝；原生解析器不能可靠处理的 ManagedReference、ExposedReference、FixedBufferSize 和 double 也拒绝。`IsCanPaste` 对这些输入返回 `false`，实际复制/粘贴抛出 `InvalidOperationException` 说明原因；Unity 私有 API 缺失或签名变化时抛出包含 Unity 版本与成员信息的 `NotSupportedException`，不静默回退。空 ObjectReference/ExposedReference 的 `GetValueText` 显示 `None`。专项回归位于 `Tests/EditMode/Editor/Core/CoreSerializedPropertyTests.cs`。

这些编辑器功能与下述 Core 使用诊断分属不同消费链。外部框架使用自己的特性，不建立跨框架特性桥。

## Core 使用诊断

Editor 内可从 `YLFramework/Core/帮助` 阅读规则和接入摘要；`YLFramework/Toolkit/帮助` 提供 Foundation 展示元数据导航。两个帮助入口均只读。配置与接入自检位于 `Project Settings > YLFramework > Analyzers`，也可从 `YLFramework/Core/分析器规则` 打开；文件写入必须先预览再明确应用。

六个正式 marker 位于 [`Runtime/Core/Attributes/`](../../Runtime/Core/Attributes/)，namespace `YLFramework.Core.RoslynAnalyzers`、程序集 `YLFramework.Runtime`。它们保留原有构造及 AttributeUsage，只提供编译元数据，不执行运行时权限检查。

分析器实现和 SDK 测试位于**源码仓库根**的 `Tools/YLFramework.Analyzers/`，不随默认 `.unitypackage` 分发。规则目录源文件 [`Editor/Core/Analyzers/AnalyzerRuleCatalog.cs`](../../Editor/Core/Analyzers/AnalyzerRuleCatalog.cs) 随 Editor 交付，并由 SDK 项目链接编译，供诊断实现与设置/帮助共享规则元数据。包内唯一编译器 DLL 为 [`Runtime/Core/Analyzers/YLFramework.Analyzers.dll`](../../Runtime/Core/Analyzers/YLFramework.Analyzers.dll)。十项规则默认启用且均为 **Warning**，项目可按 ID 配置严重级别；分析器不依赖 Unity / Core / Workspaces，默认不分析生成代码，缓存仅限当前 compilation。当前不提供 CodeFix，不通过自动修改所有权、生命周期或不可变设计来消除诊断。

| ID | 契约与典型修复 |
|---|---|
| <a id="yl0001"></a>YL0001 | `MustCallBase` 的有效 override 链要求当前方法直接调用同一 base 槽；修复为 `base.Start()`，不是同名 overload 或嵌套 callback 中的调用。仅证明存在，不证明所有控制流执行；抽象层级冲突需调整标记/覆写结构。 |
| <a id="yl0002"></a>YL0002 | `RestrictedCall` 限定直接调用者；例如 `View.RegisterEvent` 在 `Awake` 方法本体调用，callback 参数声明合法，callback 内调用不借用外层身份。 |
| <a id="yl0003"></a>YL0003 | `WarnInClasses` 对当前词法类型的本身/基类/接口匹配警告；View 中用 `RegisterEvent` 代替直接 `EventManager.AddEvent/RemoveEvent`。普通 nested helper 不自动继承外层调用类型。 |
| <a id="yl0004"></a>YL0004 | `DisableNew` 检查外部普通/目标类型 new 及可确定类约束 new T；用 `LoadViewArgs.New()` 等正式工厂。标记类型自身及词法嵌套实现可构造自身，派生类/同程序集不是全免。 |
| <a id="yl0005"></a>YL0005 | `MethodLocalOnly` 检查显式 field/property 持有；将参数保持在方法借用窗口，不保存回收后的 lease 引用。数组、tuple、真实容器与自有实例值槽都可构成存储。 |
| <a id="yl0006"></a>YL0006 | `ProtectedHierarchy` 检查指定类型层级对受保护实例 field/property/indexer 的写入和可写 ref 暴露；例如 View 读取 Model 状态，修改通过 Model 的受管方法进行。 |
| <a id="yl0007"></a>YL0007 | 真实 Core View 派生声明无参、非 static、非 generic 的 `void OnEnable/OnDisable/OnDestroy` 会竞争 private 框架消息；改用 `OnViewEnabled/OnViewDisabled/OnClosed`。不要求新 marker，不禁止 Awake/其他 Unity 消息。 |
| <a id="yl0008"></a>YL0008 | 真实 `InjectAttribute`（包括派生特性）用于 static 成员、const/readonly 字段、无 setter 属性或 indexer；改用构造注入、显式 Resolve 或可写实例成员。允许 private setter，不猜容器扫描模式或注册完整性。 |
| <a id="yl0009"></a>YL0009 | 真实 `Module.LoadView<T>` / `LoadViewAsync<T>` 的 T 确定为 Panel 或其派生/类约束时，改用 `LoadPanel` / `LoadPanelAsync`。覆盖现用及 Obsolete 泛型重载，不把 Transform parent 自动替换成 UIRootContext root。 |
| <a id="yl0010"></a>YL0010 | 非泛型 `IEnumerator` 协程直接 `yield return AudioPlayRequest` 不会按 Unity 协议等待请求；显式选择等待开始或结束。普通泛型数据枚举、object 擦除不追踪；自定义调度器可局部抑制。 |

### 错误与正确示例

```csharp
// YL0001：直接 base 调用必须在本方法，不用 Lambda 或方法组代替。
protected override void Start() { /* 缺少 base */ } // Warning
protected override void Start() { base.Start(); } // 正确

// YL0002/3：以下方法位于 View 子类中。
void Wrong() { RegisterEvent("changed", OnChanged); } // YL0002
void Wrong() { EventManager.AddEvent("changed", OnChanged); } // YL0003
void Awake() { RegisterEvent("changed", OnChanged); } // 正确，框架管理轮次

// YL0004/5：局部工厂借用可以，直接构造和成员保存不可以。
var args = new LoadViewArgs(); // YL0004
LoadViewArgs saved; // YL0005，List<LoadViewArgs> 等真实持有同样报告
void Load() { var args = LoadViewArgs.New(); /* 交给接管参数的正式加载入口 */ }

// YL0006：以下方法位于 View 子类中。
void Wrong(Model model) { model.Id = 7; } // Warning
long Read(Model model) => model.Id; // 合法读取
// 修改应封装在 Model 自身业务方法中；不把所有 setter 访问权交给 View。

// YL0007：只比较消息声明本身，不做运行时全局扫描。
void OnEnable() { Refresh(); } // Warning
protected override void OnViewEnabled() { Refresh(); } // 正确
```

这些片段是各规则的独立对照，不是可同时声明的完整类型。`OnChanged`/`Refresh` 代表业务回调，不是额外框架 API。

### 新增直接误用示例

```csharp
using YLFramework.DependencyInjection;

class Presenter
{
    [Inject] public static object Shared;       // YL0008：实例扫描不会注入 static
    [Inject] public readonly object ReadOnly;   // YL0008：被扫描时不能成员注入
    [Inject] public object Service { get; private set; } // 合法，private setter 可写
}
```

不可变依赖应考虑构造函数注入，不要为了消警删除 readonly。非公开实例成员能否被扫描取决于 `MemberSearchModes`；只看到 private 不报告。值类型 owner、依赖注册与名称解析仍由 IOC 运行时契约处理，详见 [IOC](IOC.md#成员注入)。

```csharp
// SettingsPanel 为业务 Panel 子类；以下为方法内调用片段。
owner.LoadView<SettingsPanel>("ui/settings", loadViewArgs: args); // YL0009
owner.LoadPanel<SettingsPanel>("ui/settings", loadViewArgs: args); // 正式入口
owner.LoadPanelAsync<SettingsPanel>("ui/settings", loadViewArgs: args); // 异步正式入口
owner.GetView<SettingsPanel>("ui/settings"); // 查询合法，不是加载
```

`where T : Panel` 或沿类型参数约束可证明为 Panel 的调用同样报告；仅有 `where T : View` 不报告。非泛型 `LoadView(string/prefab)` 无法仅靠类型判断根组件，不扫描 Prefab，也不误报 `AttachView` 纳管。

```csharp
// 三个独立协程示例；没有 AudioPlayRequest.Wait() 这样的框架 API。
IEnumerator Wrong(AudioPlayRequest request)
{
    yield return request; // YL0010：普通对象，不是 Unity 等待指令
}

IEnumerator WaitForStart(AudioPlayRequest request)
{
    yield return new WaitUntil(() => request.HasStarted || request.IsDone);
}

IEnumerator WaitForEnd(AudioPlayRequest request)
{
    yield return new WaitUntil(() => request.IsDone);
}
```

等待开始也检查 IsDone，避免请求失败时一直等不到 HasStarted。循环播放可能永不自然结束；结束后还应区分 `EndReason.Completed` 与失败、取消等终态。不要把等待终结自动替代为等待启播，详见 [Audio](Audio.md#命名加载与资源责任)。

### 匹配与边界

- marker/View/ObjectPool/Inject/Module/Panel/AudioPlayRequest 必须解析到真实 `YLFramework.Runtime` 的符号；支持主体自身 source compilation 与引用主体的 metadata consumer，短名或同 namespace 的异程序集类型不替代它们。方法 marker 只按真实 AttributeUsage 和 override 链取得，接口只在调用的静态目标本身带标记时生效，不叠加任意实现接口的契约。
- caller 选择器为 `Method`、`Type.Method`、`Namespace.Outer.Type.Method`，区分大小写并使用声明类型，泛型 arity 不写入选择器。accessor 可用 `get_P`/`set_P`、限定的 `Type.P`；构造器可显式用 `.ctor`/`.cctor`。Lambda/local 没有可冒充外层的选择器，field/property initializer 不借用外层方法。null/空调用者被忽略，空名单不允许调用；Warn 的空类型名单不匹配。
- 开放泛型类型匹配原定义，闭合类型按完整构造符号比较；只从有效类/类型参数约束确定受管类型，不因 `IEnumerable<Args>` 等接口签名钻入隐藏内容。
- 存储 profiles 包括实际 framework 的持有容器/对应只读集合、字典键和值、Tuple/ValueTuple/Rest、KeyValuePair、Task/ValueTask 结果、Memory/ReadOnlyMemory、ConcurrentQueue/Stack/Bag/Dictionary 及真实 Foundation ObjectPool；ArraySegment 和含值槽的 Nullable 结构也按真实存储识别。`IEnumerable`、`IEnumerator`、comparer/delegate 的签名本身不是实例持有证据，伪同名第三方容器不因名称进入 BCL profile。
- 自有泛型摘要只沿含参数的实例 field、auto-property backing 与构造 BaseType 证明内容位，有限定义图按固定点收敛。`Box<Box<Args>>` 有值槽则报告，phantom/static-only wrapper 不报告；互环无叶不误判，有叶正确传播，未知 metadata 不猜持有。入口不全对象图扫描无关 non-generic 成员；这不是通用泄漏/closure 逃逸检查。
- YL0005 仅豁免实际 Core 的精确 owner 字段：`LoadViewArgs._pool` 的 private static readonly 真实 `ObjectPool<LoadViewArgs>`、`LoadViewRequest<T>._args` 的 private instance，以及 `PanelManager.PanelOperation.Args` 的 internal instance。后者一对一对应 Module pending 消费者，不是给业务保存参数的公开权限。同名伪类型、其他成员/public/static/错误嵌套或泛型形态不自动豁免。故意契约测试使用声明级局部 pragma，不排除整个 Editor/Test/Core。
- YL0006 包含 compound/coalesce/inc/dec/deconstruction 各左值叶、ref/out/ref local/ref return 的可写暴露；读取、`in`/`ref readonly`、static 成员和 event subscription 不报。结构体值链写回外层保护成员，引用对象内部 mutation 不当成外层写；保护层级自身仍可修改状态。不追任意被调副作用或 ref 别名后续写入。
- YL0007 是声明级提示，普通 MonoBehaviour、非 Core 的 View 或伪同名 View、普通 nested helper、static/有参/generic/非 void overload 不报。消息遮蔽可能破坏订阅轮次，使 OnDestroy 的及时关闭延后到兜底扫描；不等于必然永久泄漏，也不改变 Runtime 可见性。
- YL0008 不检查 init-only **属性**的构造期约束，不推断 private 扫描、容器配置或依赖是否能解析；显式 field-target 按实际字段检查，包括属性/record 参数的 readonly backing field 与 static 事件字段。`[property: Inject]` 的合法可写或 init 属性不等同于 readonly 字段。YL0009 只匹配真实 Module 的两个泛型方法族，同名业务包装方法不是自动受管入口。
- YL0010 只看 yield 所直接归属方法/局部函数的非泛型 `System.Collections.IEnumerator` 返回类型和表达式原始静态类型，不把外层协程身份套给内部泛型数据迭代器。自定义调度器可自定义对象等待协议，因此这条规则按标准 Unity 协程解释，不推测调用者意图。
- object/接口擦除、反射/Activator/dynamic、无约束泛型工厂、所有执行路径和跨过程所有权不保证。错误符号交给编译器，不吞异常或把 analyzer failure 当无诊断；这些 Warning 不是运行时安全边界。

### Unity 接入与配置

DLL 的 Importer 禁用 Any / Editor / 普通平台，关闭自动 Runtime 引用，仅标为 `RoslynAnalyzer`。它位于 `YLFramework.Runtime` 的 asmdef 范围内；分析范围由 Runtime 及真实引用消费者决定，Editor / Tests / 游戏 / 预定义程序集应按引用图和实际 csc 输入核对。不要复制第二套 DLL 扩大范围，不把 Analyzer 或 Roslyn 依赖添加为 Player Runtime 引用，也不要为启用自有诊断关闭其他分析器的规则。

推荐从 `YLFramework/Core/分析器规则` 打开原生 Project Settings 页面，流程为：选择业务程序集、调整单 ID、预览原文/新内容、明确应用。选项为继承、Warning、Error、关闭（None）；**继承是移除本地覆盖，不代表强制恢复 Warning**。页面不会建立第二套 ScriptableObject 设置，也不会写全局 `Assets/Default.ruleset`。

- 业务 asmdef：使用其同目录唯一局部 ruleset；没有时建议 `<AssemblyName>.ruleset`。
- 预定义程序集：使用 `Assets/Assembly-CSharp.ruleset`、`Assembly-CSharp-firstpass.ruleset`、`Assembly-CSharp-Editor.ruleset` 或 `Assembly-CSharp-Editor-firstpass.ruleset`，只允许实际选定的原生程序集名称。
- 框架自有、Packages、工程外或不在 Runtime 引用作用域中的目标只读；不通过给它们复制额外 DLL 来扩范围。
- 新建局部文件时若有 Default，则显式 Include 根文件，保留其后续变化；已有文件只修改已知 YL ID，保留其他分析器、未知 ID、原 Include/IncludeAll、编码和 meta 身份。
- 多份局部文件、冲突 ID、损坏/未知 XML、DTD、循环/无法核实的 Include、工程外/链接/只读目标明确拒绝。根 `Name/ToolsVersion` 与 `Rules` 的 `AnalyzerId/RuleNamespace` 必须非空；`Default` 只允许用于 Include，不能用于 Rule/IncludeAll；冲突 ID 不按 analyzer 节隔离。环境变量 `%VAR%` 形式的 Include 保持只读，不能用同名字面目录替代编译器展开后的策略文件。预览后观察到目标、继承或目录集合变更时拒绝覆盖，先刷新再预览；不是对任意写者的 CAS。

应用会导入受影响文件并请求必要重编译，不保存场景或无关资产。Error 可能阻止目标编译；恢复时显式改为 Warning，或按需要恢复继承。如果根策略本来是 Error，移除本地覆盖仍可能继承 Error。UI 不可用时手动编辑显示路径的 `<Rule Id="..." Action="Warning" />` 或移除相应本地 Rule，重新导入；不要通过删除整个分析器来掩盖单条问题。

自检只读显示唯一 DLL、版本/hash、标签、平台、明确引用和当前编译图等能够取得的事实。结构检查通过、配置保存成功、编译完成或零诊断都不是“规则已实际执行”的证明；无法取得的状态显示未核实。IDE 的额外 `.editorconfig` 或 inspection 策略不会由本页接管。

支持该配置的 IDE/SDK 可以按单 ID 配置 `.editorconfig`，例如在消费者目录：

```ini
[*.cs]
dotnet_diagnostic.YL0002.severity = none
```

不要假定 Unity 生成的旧式 IDE 项目一定读取该文件，或它必然覆盖 ruleset；以实际项目系统的单 ID 正反对照为准。本设置页只维护原生 ruleset，不接管 IDE 策略。Unity 2022.3 的实际 csc 不自动跟随 IDE `.editorconfig`；用 asmdef 目录中的叶子 ruleset（例如 `Game.UI.ruleset`），只配置需要的 ID：

```xml
<?xml version="1.0" encoding="utf-8"?>
<RuleSet Name="Game UI" Description="Local policy" ToolsVersion="12.0">
  <Rules AnalyzerId="YLFramework.Analyzers" RuleNamespace="YLFramework.Analyzers">
    <Rule Id="YL0002" Action="Error" />
  </Rules>
</RuleSet>
```

该最小手工例子假定没有需要继承的 Default。Unity 不会自动逐条合并根文件与局部文件；若项目已有 `Assets/Default.ruleset`，应添加正确的相对 Include，例如局部文件位于 `Assets/Game/UI` 时为 `<Include Path="../../Default.ruleset" Action="Default" />`。根文件不存在时不要加入指向不存在文件的 Include。

`Error` 会阻止该消费者编译，`None` 抑制该 ID；其余 ID 按实际继承和编译器配置处理，未被其他策略覆盖时才是默认 Warning。根 `Default.ruleset` 可影响全工程，勿为局部需求覆盖根策略。故意测试可精确关闭一个声明/语句的 ID，并紧接恢复：

```csharp
#pragma warning disable YL0005 // 仅用于验证 lease 的归还终态，不是业务保存入口。
private readonly HashSet<LoadViewArgs> borrowed = new();
#pragma warning restore YL0005
```

自定义协程调度器若确有识别 AudioPlayRequest 的协议，可用同样方式在对应迭代器声明附近局部关闭/恢复 YL0010；不要为一种非标准调度器关闭整个项目的音频检查。

### 安装、升级与移除

导入同版本框架包时 DLL、原 meta 和 Editor 设置入口一起接入，先用自检核对作用域与编译图。升级时保留原 GUID，不复制第二份 DLL；业务 ruleset 属于使用项目，不由导入或设置页重建。检查新增 ID 与说明，旧七条规则不因本次增强静默改为全路径等更强语义。

仅暂停某条规则优先使用单 ID 的 None 或局部 pragma。完整移除分析器时由开发者明确移除 DLL 及配套 meta，marker 源码本身不提供诊断；不要留下孤儿 meta，也不要自动删除业务规则集或其他分析器。重新启用应恢复发行包的标签、禁用平台与明确引用配置后重编译核对。

### 重建与验证

以下命令仅适用于包含 `Tools/YLFramework.Analyzers/` 的**完整源码仓库**，从仓库根执行。默认分发包只有 Analyzer DLL 与导入配置，缺少 Tools 时不能在包内运行重建命令，应取得对应版本源码。

要求已安装 PowerShell 与 .NET SDK `9.0.312`；工具内 `global.json` 设置 `rollForward: disable`，脚本也核对精确 SDK 版本。Roslyn 固定 `3.8.0`，Analyzer 目标为 `netstandard2.0`，独立测试目标为 `net9.0`。脚本还要求仓库已有 `Temp/` 父目录。

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File "Tools/YLFramework.Analyzers/build.ps1"
pwsh -NoLogo -NoProfile -NonInteractive -File "Tools/YLFramework.Analyzers/build.ps1" -Deploy
```

默认依次执行 locked restore、Release build 和诊断测试，失败即停止、不顶替旧 DLL；显式 `-Deploy` 才将产物替换到 `Assets/YLFramework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll`，保留 `.meta`。部署在 SDK 前及写入前检查原 GUID、精确标签、明确引用和全禁用平台；缺失/错误 meta 直接拒绝，不自动生成新身份。Fixture 编译正式 Core marker 和 IOC Inject 源码，Module/Panel/Audio 的最小签名还必须用真实 Unity 见证复核。入口以 `Temp` 合作锁串行化构建、测试与部署；暂存在工程 Temp，替换前和暂存后核对目标指纹。它不是针对任意外部写者的 compare-and-swap，部署期间不要用其他工具写此 DLL。

Analyzer 构建收据记录编译源、Analyzer csproj/lock、固定 SDK 配置、构建脚本及共享只读规则目录的指纹，不是 Unity 生成的工程文件。共享目录源由 Analyzer 项目 Link 编译，Editor 与 Analyzer 不互相引用。SDK 测试仍通过既有 Tests csproj/lock 构建。bin / obj / TRX / 构建指纹记录均输出到 `Temp/YLFramework.Analyzers/`。不要向 Assets 复制 Roslyn / Immutable / 测试宿主 DLL；部署后还需实际 Unity 诊断、包内 payload/hash 与 Player 隔离门禁。

SDK 编译/测试、Unity csc、PlayMode 生命周期、确定性重建和 Player 输出引用是不同证据，不能相互替代。不要将某次 Windows64 / Mono 验收扩展为 IL2CPP、其他平台或任意独立载体的保证。

### 发布支持边界

发行验收目标为 Windows 上 Unity 2022.3 LTS 与 Unity 6.3 LTS，以及对应 Visual Studio / Rider 的诊断和配置读取；每份发行须记录真实补丁、IDE/集成包版本和结果。目标矩阵不是已经通过的声明，缺少某个环境的证据就不能将该项标为已验证。macOS/Linux、Unity 6.0及其他版本不由本矩阵自动承诺；实现不故意依赖 Windows 专用路径。当前不以 UPM、独立 IDE 插件、CI 或 CodeFix 作为安装前提。

## 测试入口

- `Tests/EditMode/Foundation/AttributesTests.cs`：元数据声明、正式源码及派生构造使用者的 Editor / 无 UnityEditor 引用 Development 源码编译，不等同于完整 Player 构建运行。
- `Tests/EditMode/Editor/Foundation/Attributes/DisableEditDrawerTests.cs`：SerializedObject、展开高度、子 Drawer 禁用与异常状态恢复。
- `Tests/EditMode/Editor/Core/CoreSerializedPropertyTests.cs`：原生剪贴板格式与拒绝边界。
- `Tests/EditMode/Editor/Core/AnalyzerRuleSetFileTests.cs`：局部路径、继承/未知规则保留、无变化不写、冲突拒绝及真实 Unity 编译器 Include/级别见证。
- `Tests/EditMode/Editor/Core/AnalyzerRuleSettingsTests.cs`：设置路由、只读状态、作用域与真实 Provider 渲染无写入/无脏场景。
- `Tests/EditMode/Editor/Core/CoreEditorIdentityTests.cs` 与 `Editor/Distribution/ExportUnityPackageTests.cs`：真实 Editor/Development 新旧诊断、SDK收据/部署/包指纹与编译器资产隔离。导出门禁要求先运行正式构建/部署入口。
- 源码仓库的 `Tools/YLFramework.Analyzers/Tests/`：诊断 ID、级别、位置、符号身份、所有权与配置等离线回归，通过上面的脚本运行。

这些是测试入口，不代表当前环境已执行通过。历史编译器、DLL 和 Player 验收记录见 [集成记录](../IntegrationPlan.md)。
