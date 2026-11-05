# YLFramework 架构

> [文档首页](README.md) · [代码规范](CodingStyle.md)

本文只维护产品分层、依赖方向、装配与所有权。安装、API 和操作分别归首页及模块页；源码仓库开发流程归仓库 README，历史来源见 [历史索引](IntegrationPlan.md)。

## 产品边界

YLFramework 按需交付与使用。主包有 `YLFramework.Runtime` 与 `YLFramework.Editor` 两个主体程序集；独立功能包只在真实消费者需要时安装，不要求使用一个工具就启动全部系统。

| 内容 | 所属与边界 |
| --- | --- |
| `Runtime` | Foundation、Core、Systems、Utilities 四个职责区，共用 Runtime 主体 |
| `Editor` | 作者工具、公共支撑与 Help，共用 Editor-only 主体 |
| 独立选装 UPM 包 | DataAssets、Localization、DOTween、Excel、DomainExcel、HybridCLR 的可选编译与交付边界，不在主包内 |
| `Prefabs`、`Samples~`、`Documentation~` | 正式模板、显式导入后才编译的接入样例、按文件路径打开的产品指南 |
| `Plugins/BouncyCastle` | 随包的批准依赖及完整来源许可；不递归整个宿主 Plugins |
| `Assets/Tests/YLFramework` | 源仓库测试与夹具，不在任何产品包内 |

`YLFrameworkDev` 场景、宿主 ProjectSettings、MCP 和商业 Editor 插件属于源码开发环境，不是产品运行依赖。主体依赖显式声明，不依赖已退休宿主扩展；承接源码的来源与许可见 [产品许可证](../LICENSE.md)。`Assets/YLFrameworkData` 是使用工程的作者数据；Resource 发布快照、HybridCLR 基座和普通框架包是不同交付物。

## 目录与程序集

```text
PackageSources/
  com.kano.ylframework/
    Runtime/     Foundation/  Core/  Systems/  Utilities/
    Editor/      Foundation/  Core/  Systems/  Utilities/
                 Common/  Tools/  Help/
    Prefabs/  Plugins/BouncyCastle/
    Samples~/ResourceStartup/  Documentation~/
    package.json  README.md  CHANGELOG.md  LICENSE.md
  com.kano.ylframework.data-assets/
  com.kano.ylframework.localization/
  com.kano.ylframework.dotween/
  com.kano.ylframework.excel/
  com.kano.ylframework.domain-excel/
  com.kano.ylframework.hybridclr/  Samples~/HybridCLR/
Assets/Tests/YLFramework/          # repository only
```

开发宿主按需以 `file:` 连接这些包；安装后的主包逻辑资产根为 `Packages/com.kano.ylframework`，不是旧 Assets 路径。`Documentation~` / `Samples~` 不进入 AssetDatabase，文档按实际磁盘路径打开，样例经 Package Manager Import 后才在使用工程编译。有 asmdef 的消费方仍需显式声明所需引用。正式 Sample 有自身 Runtime / Editor asmdef，导入后启用自动引用；“两个主体”不代表包中没有待导入的 sample asmdef。实际平台、define、预编译白名单及仓库测试归属以 `.asmdef` / `.asmref` 为准。

| 程序集 | autoReferenced | 依赖与限制 |
| --- | --- | --- |
| `YLFramework.Runtime` | true | Unity / UGUI、Json.NET、随包 BouncyCastle；不引用框架 Editor 或已退休宿主扩展 |
| `YLFramework.Editor` | true | Runtime、UGUI、Json.NET，仅 Editor；Help 不另建主体 DLL |
| `YLFramework.DataAssets` / `.Editor` | true | Runtime 引用主 Runtime；Editor 引用自身 Runtime 与主 Editor，不引用 Excel/MiniExcel |
| `YLFramework.Localization` / `.Editor` | true | Runtime 引用主 Runtime 与 UGUI；Editor 引用自身 Runtime 与主 Editor，不引用 DataAssets/Excel |
| `YLFramework.Actions.DOTween` | false | Runtime + 外部 DOTween.dll，受 `DOTWEEN` 约束 |
| `YLFramework.Excel.Editor` | true | 主 Runtime/Editor、DataAssets Runtime/Editor、MiniExcel、Json.NET，仅 Editor |
| `YLFramework.Domain.Excel.Editor` | true | Excel 后端与主体，仅 Editor；安装 Domain Excel 包即可启用，不再要求全局宏 |
| `YLFramework.HybridCLR` / `.Editor` | false | 主体与声明的官方 HybridCLR SDK；dnlib 仅 Editor，主体不反向依赖 |

### Namespace 与迁移

namespace 表达功能域，不机械复制物理目录。典型差异包括 `DependencyInjection`、`Pooling`、`Actions`、`RedDots`、`Character2D`、`Platform2D`；实际完整类型名以声明为准。

Editor 的中立支撑归 `YLFramework.Editor.Common`，领域共享策略归 `.Domain`，无领域归属的独立命令归 `.Tools`。具体旧名迁移见 [Editor 扩展](Modules/Editor.md#editor-namespace-迁移)。

类型全名、程序集、序列化字段、资源地址、JSON/Excel 键是独立合同。移动文件保留 `.meta` / GUID，不代表旧 AQN、反射字符串或旧 Bundle 自动兼容；`rootNamespace` 不改变已有类型身份。必要迁移跟随真实消费者与旧数据，不为假设需求保留旧 DLL 或转发壳。

## 依赖方向

同一 Runtime 主体不取消内部依赖约束：

| 职责区 | 允许的内部依赖 | 所有的责任 |
| --- | --- | --- |
| Foundation | 不反向消费 Core / Systems / Utilities | 异步、事件、池、计时、更新、注入、序列化及必要 Unity 适配 |
| Core | Foundation 与自身窄合同，不依赖具体 Systems 实现 | Module / MVC / View / Panel / UI 会话与资源消费合同 |
| Systems | Foundation、Core 合同及适用 Utilities | Resource、Audio、Save 与领域状态；跨系统协作明确登记 |
| Utilities | 按需组合的基础机制；有明确用途时消费 Core | 数据结构、扩展、检测/移动组件与控件，不升级为全局服务 |

Foundation 的 Unity 挂载 / 销毁直接使用原生 API，不为了便利扩展反向依赖 Utilities。ColorSelector 消费 Core Panel 是明确的控件依赖，Core 不反向依赖取色工具；完整配置 StateMachine 与轻量 FSM 分属 Systems / Utilities。

Runtime 不引用框架 Editor 程序集；其中编辑器专用代码使用 `#if UNITY_EDITOR` 隔离，Player 不因此依赖 UnityEditor。编译图检查与源码边界测试共同约束这条方向，不以“同一个 DLL”取消架构检查。

Action 对 `YLFramework.Editor` 的 InternalsVisibleTo 是受控内部可见性，不是 Runtime 反向程序集引用。Analyzer SDK 链接编译 Editor 下 BCL-only 的规则目录源文件，两边不互相程序集引用；不要为追求字面“零耦合”删除这些接线或放大公共 API。

### Core 职责归口

| 所有者 | 责任 |
| --- | --- |
| Module / ModuleManager | 业务注册、启动、关闭及 Models / Controllers / Views / Panels 的会话责任 |
| View / ViewManager | 表现会话、索引、加载与查询；Panel 复用该生命周期，不维护第二套窗口 owner |
| PanelManager / UIRootContext | 根、层、遮罩、排序与输入协调；根配置不变成独立资源缓存 |
| SceneController | 通过 `ICoreResourceProvider` 提交场景请求，只清自己的在途责任，不持有成功 Scene 的卸载权 |
| EventManager | Core 事件薄入口，不复制 Foundation 总线 |
| 默认资源与 Analyzer marker | Core 自身默认配置与 Runtime 元数据，不塞入中立 Foundation；Analyzer DLL 另作为编译器输入 |

TimerObject 属于 Foundation Timing；物理、动画和 Unity 生命周期监听属于 Foundation Events。它们不因曾服务 Core 就继续混入 MVC。

### 跨系统协作

- Core 经自身资源合同接入，Resource 提供适配；UI / Module 不静态绑定具体资源实现。
- Resource → Audio 的内建装配在 `Systems/Resource/Integration/Audio`。命名 Clip Provider 用独立 AssetLease 保活或受控闲置缓存，资源服务仍是资产 / Bundle 的唯一释放权威。
- `Systems/Audio/Actions/PlaySoundAction` 是同一 Runtime 内的 Action / Audio 协作，不是假装可拆卸的第三方集成；DOTween 才是独立可选边界。
- 独立 Localization/DataAssets 与领域配置都可借用 ResourceSystem，不再建立第二套地址、下载或资源释放权威；主包不反向引用选装包。

### HTTP / AsyncOperation 边界

HTTP 独占每笔原生传输并自管稳定终态；Foundation 异步基类只提供等待和通知。取消本消费者、终止独占传输与归还共享 producer 的责任不能混同，详见 [HTTP](Modules/Http.md)。

### Timer / Action / Audio 边界

| 能力 | 负责什么 | 不接管什么 |
| --- | --- | --- |
| Timing | 秒 / 帧 / 日期投递、周期与 owner scope | 不提供逐帧 progress，不建立统一全局任务表 |
| Action | 逐帧进度、插值、条件与动作组合 | 不将 Delay 强制改成 Timer，也不取得音频资源权 |
| Audio | 播放、逻辑取消、代次句柄与可选 Mixer | 不代替 Resource 释放 Bundle，不增加第二个播放器 |

各自时钟、首帧、循环、异常和名称索引遵循模块合同，不为表面统一合并调度器。用法见 [Timing](Modules/Timing.md)、[Action](Modules/Action.md)、[Audio](Modules/Audio.md)。

## 装配与所有权

`Runtime/YLFrameworkRoot.cs` 是常驻宿主及 UI 装配入口，不是自动启动器。纯工具和数据结构不要求 Root / Module；使用默认根或完整模板时，应用仍决定资源、Provider、业务 Module 与关闭顺序。

| 责任 | 持有者与释放顺序 |
| --- | --- |
| 资产 / 实例 / 场景 | ResourceSystem 是唯一权威；消费者持 lease，不擅自卸载共享底层内容 |
| Module / View / Panel | 会话 owner 清理自己的注册和在途工作，不以关闭一个 Module 清全局服务 |
| 命名音频 | Audio Provider 持对应资源租约；先停播放，再归还 Provider / 资源责任 |
| 配置登记 | 登记者持句柄；先停止使用配置的业务与 UI，再归还登记和根资源 |
| 领域运行态 | Skill / Buff / Equipment 按宿主结束，Mission 按任务配置结束；系统只清自身责任 |
| Timer / Action / HTTP | 创建或登记方结束自身工作；“等待结束”不自动代表取消、成功或共享资源归还 |

共享定义与实例状态分离。取消、重入、池化复用、停用 / 销毁、场景结束和禁用 Domain Reload 必须仍能追溯到准确 owner，而不是依赖一个兜底 Shutdown 清除所有状态。

详细接入见 [UI](Modules/UI.md)、[Resource](Modules/Resource.md)、[配置数据](Modules/Data.md) 与 [领域系统](Modules/Domain.md)。完整模板及 Variant 清单只维护在 [UI 资产准备](Modules/UI.md#资产准备与迁移)。

## Editor 工具

Editor 按 Runtime 所有者组织 Foundation / Core / Systems / Utilities；`Common` 只承载中立资产、Inspector、Scene、Settings、TreeView 与 Window 支撑。领域共享生命周期和资源作者策略留在 `Systems/Shared`，不塞回 Core 或 Common。

Help 提供共享索引与领域提供方；发行与安装只走 UPM，旧 unitypackage Distribution 出口退役。不根据 MenuItem 的存在把模块窗口移入通用 Tools。预览桥由领域侧安装解析器，公共层不主动初始化 Runtime。

作者操作必须区分只读包输入、用户配置和生成输出，尊重 Undo、Prefab、脏场景及真实 owner。Module 输出在 `Assets/YLFrameworkData/Generated/Modules`，SO 创建助手默认在 `Assets/YLFrameworkData/ScriptableObjects`；包升级/移除不覆盖或删除用户数据、SDK/native 和冻结基座。具体公共支撑合同、设置与迁移见 [Editor 扩展](Modules/Editor.md)，配置作者流程见 [Data](Modules/Data.md)。

## 可选集成

默认产品不能静态依赖可选集成的 SDK、宏或供应商 DLL。具体安装、版本和许可证由各集成目录的声明维护，首页只导航。

### HybridCLR 可选接入

HybridCLR 是独立选装 UPM 包，复用唯一 ResourceSystem，官方 SDK `9.0.3` 及匹配 native 独立安装并接受版本/平台门禁。基座构建完成官方生成、剥离、metadata、资源发布 / 部署与 Player 后才冻结持久基线；补丁只读基线，不重新剥离或改变基座。更换包、SDK 或 native 不等于旧基座自动兼容，也不自动覆盖旧基线。

运行时先关闭 loader 启动的 Module，再 Dispose loader，最后关闭应用拥有的资源系统。Dispose 不卸载程序集，部分不可逆失败或同进程内容冲突要求重启。Native、EditorSimulation、EditorFast 是不同执行模式，不互相充当原生证据，详见 [集成说明](../../com.kano.ylframework.hybridclr/README.md)。

## XML 与分发

`Runtime/csc.rsp` 为主体生成 XML，DOTween 有独立 XML 配置；Help 按真实 Assembly 去重读取。`Library` 下 XML 是生成内容，不随产品包交付；缺 XML 时仍可展示可反射的声明。

`Runtime/Core/Analyzers/YLFramework.Analyzers.dll` 是唯一编译器输入，通过 RoslynAnalyzer 标签生效，不是 Player 插件或 Runtime precompiledReference。诊断、规则配置和重建说明只维护在 [Attributes](Modules/Attributes.md#core-使用诊断)。

UGUI / Json.NET 等 Registry 依赖由 UPM 根据声明自动解析。Git 消费方必须在工程 manifest 显式列出所选自有包的 Git 闭包，不能以包内 SemVer 依赖代替 Git 来源；随附依赖与来源许可完整保留。准确安装格式和交付清单见 [首页](README.md#安装与分发)。内容加密的 Windows CNG 限制属于 [Resource](Modules/Resource.md#可选-bundle-内容加密)，不扩写为整个框架全平台承诺。

## 测试

测试不随默认包交付。源仓库的模块声明与执行入口位于 `Tools/Testing`；普通源码按 Foundation / Core / Systems / Utilities 归类，可选集成有自己的测试程序集。

| 架构守卫 | 检查责任 |
| --- | --- |
| FoundationBoundaryTests | Foundation 禁止向上引用 |
| CoreArchitectureBoundaryTests | Foundation + Core + Root 的允许依赖与资源消费边界 |
| AudioSourceIndependenceTests | Audio 自有闭包及引用白名单 |
| FrameworkAssemblyIndependenceTests | 主体编译图、二进制引用与独立源码编译 |

这些守卫不替代真实生命周期、序列化迁移、资源租约、干净导入或 Player。验证结果绑定当时的源码、配置、环境和制品，不在架构页维护会漂移的叶子数量或“全部已通过”状态。
