# YLFramework

YLFramework 是 Unity C# 游戏框架，提供基础机制、Module / MVC / UI、资源与音频、配置与领域系统，以及配套作者工具。默认以 **Runtime / Editor 两个主体程序集**交付，能力按需使用，不要求先搭建完整应用框架。

本页是消费方入口。维护源仓库、运行框架测试或发行产品时，使用源码仓库根 README；不要把开发宿主当作游戏工程模板。

## 从哪里开始

| 目标 | 建议路径 |
| --- | --- |
| 使用池、事件、计时、数据结构 | 安装后直接使用相应模块，不需要 Root 或 Module |
| 组织业务模块与窗口 | 从 [UI / Module](Modules/UI.md) 开始，选择默认 UI 根或正式模板 |
| 加载资源、场景和命名音频 | 从 [Resource 最小接入](Modules/Resource.md#可运行最小接入) 开始，明确初始化、Provider 和 lease |
| 编辑配置与玩法定义 | 阅读 [配置数据](Modules/Data.md)、[领域系统](Modules/Domain.md) 和 [Editor 扩展](Modules/Editor.md) |
| 理解或扩展框架 | 阅读 [架构](Architecture.md) 和 [代码规范](CodingStyle.md) |

## 安装与分发

### 独立工程安装

主要兼容目标为 Unity 2022.3 LTS 与 Unity 6.3 LTS，源码按 C# 9 维护。Editor、Mono、IL2CPP 与目标平台是不同验证范围；不能把某个补丁版本或 Editor 导入成功扩展为所有平台支持。

Resource 远端 Player 与 IL2CPP 加密后端另有 Windows64 和精确引擎门禁，定点为 `2022.3.62f3c1` / `6000.3.27f1`；详见 [平台与范围](Modules/Resource.md#共存与范围)。本地 Bundle、远端更新、加密与整个框架的支持范围不能混为一谈。

1. 在目标 Unity 工程安装必要依赖，等待 UPM 完成解析。
2. 导入正式 Exporter 生成的 `.unitypackage`。不要复制源宿主的 Packages、ProjectSettings 或整个外部 Plugins / Resources 目录。
3. 消费方 asmdef 显式引用 `YLFramework.Runtime`；仅 Editor 扩展引用 `YLFramework.Editor`。检查真实 Unity 编译，而不是只看 IDE。
4. 按能力完成显式装配。基础工具可直接使用；UI、Resource、命名 Audio 和领域配置遵守各自初始化与所有权，不因放入 Root Prefab 就自动全部启用。
5. 在目标工程验证实际场景与 Player；配置、反射类型、资源闭包和可选 SDK 的平台前置由应用一并准备。

| 默认依赖 | 安装与边界 |
| --- | --- |
| UGUI：`com.unity.ugui` | 使用目标 Editor 对应的兼容版本；正式 UI 和部分动作依赖 UnityEngine.UI |
| Json.NET：`com.unity.nuget.newtonsoft-json` | 安装 Unity UPM 包；分发验证基线为 `3.2.1`，不可用同名替身 DLL 代替 |
| `BouncyCastle.Cryptography 2.7.0` | netstandard2.0 DLL 与许可已随框架携带，用于 HKDF-SHA256；不要再导入冲突副本 |

默认产品不要求可选宿主扩展、Odin、MCP、DOTween、MiniExcel 或 HybridCLR。可选依赖只在实际安装相应集成时需要。

Resource 默认不启用内容加密。选择 AES-256-GCM 内容加密时，当前原生后端使用 Windows CNG；没有软件 GCM 或临时明文回退。具体平台、key provider 与失败合同见 [可选 Bundle 内容加密](Modules/Resource.md#可选-bundle-内容加密)。

### 帮助与导出

- 帮助菜单为 `YLFramework/帮助`，以及 Core / Toolkit / Action / Audio 子入口；它只读取索引、元数据与 XML，不执行示例或启动系统，见 [Help](Modules/Help.md)。
- 默认包由源码仓库的 `Editor/Distribution/ExportUnityPackage.cs` 生成；菜单为 `YLFramework/Toolkit/导出框架包`，自动化入口为 `ExportUnityPackage.Export(absolutePackagePath)`。
- Exporter 递归导出明确根，不使用 IncludeDependencies；不能把宿主实际已安装的依赖误认为会随包自动交付。

| 默认交付 | 内容 |
| --- | --- |
| `Runtime/`、`Editor/` | 两主体源码、Core 默认资源及编译器输入 |
| `Plugins/BouncyCastle/` | 随包 DLL 与完整许可 |
| `Prefabs/` | 正式 UI 模板，清单与继承关系见 [UI 资产准备](Modules/UI.md#资产准备与迁移) |
| `Samples/` | 独立接入样例；生成的使用工程数据不属于该源码根 |
| `Documentation/`、`LICENSE.md` | 产品文档与来源许可 |

不导出 `Tests`、根 `Integrations`、源仓库 Tools、游戏、测试探针、SDK、NuGet 缓存或开发宿主配置。Core 生成器的 `Examples/Core/Modules` 也是非默认交付的生成输出，详见 [Editor 设置与生成](Modules/Editor.md#设置与生成)。Analyzer 以 RoslynAnalyzer 编译器输入交付，不作为 Player 插件；规则、安装与移除只维护在 [Attributes](Modules/Attributes.md#core-使用诊断)。

## 能力导航

### 基础机制

| 能力 | 文档 |
| --- | --- |
| 事件、Channel / Bus 与 Unity 监听 | [Events](Modules/Events.md) |
| 对象池与精确回收 | [Pool](Modules/Pool.md) |
| 计时、作用域与日期投递 | [Timing](Modules/Timing.md) |
| 依赖注入、单例与会话重置 | [IOC](Modules/IOC.md) · [Singleton](Modules/Singleton.md) |
| 属性与方法绑定 | [Binding](Modules/Binding.md) |
| 元数据、Inspector 扩展与编译器诊断 | [Attributes](Modules/Attributes.md) |
| 类型查找与缓存 | [AssemblyTool](Modules/AssemblyTool.md) |
| 更新与协程等待基础 | `Runtime/Foundation/Update`、`Async`；请求基类边界见 [HTTP](Modules/Http.md#与异步基类的区别) |

### 业务与领域

| 能力 | 文档 |
| --- | --- |
| Module / MVC、View / Panel、UI 根与输入 | [UI](Modules/UI.md) · [Core 职责](Architecture.md#core-职责归口) |
| Catalog、AssetBundle、场景、lease、构建发布 | [Resource](Modules/Resource.md) |
| Music / Voice / Sound、命名 Provider、句柄与 Mixer | [Audio](Modules/Audio.md) |
| Action、Observable、组合动作与音频桥接 | [Action](Modules/Action.md) |
| Localization、DataAssets、Excel 字段与作者流程 | [Data](Modules/Data.md) |
| 存档槽、SaveStore、序列化与故障恢复 | [Save](Modules/Save.md) |
| Skill、Buff、Mission、Equipment、Reddot、Logging | [Domain](Modules/Domain.md) |
| HTTP 请求、取消与响应快照 | [HTTP](Modules/Http.md) |
| 配置式 StateMachine | [Behavior](Modules/Behavior.md#两种状态机)，不同于下方轻量 FSM |

### 工具与 Editor

| 能力 | 文档 |
| --- | --- |
| 轻量 FSM、网格与索引 | [FSM](Modules/FSM.md) · [Grid](Modules/Grid.md) · [Table](Modules/Table.md) |
| JSON 树、压缩与通用扩展 | [Json](Modules/Json.md) · [ZipTool](Modules/ZipTool.md) · [Extensions](Modules/Extensions.md) |
| Overlap、CharacterController2D、MoveablePlatform | [Behavior](Modules/Behavior.md) |
| 取色器与窗口 | [UI](Modules/UI.md#取色控件colorselector) |
| 作者工具、TreeView、设置与 Editor 迁移 | [Editor](Modules/Editor.md) |
| 帮助提供方、窗口与 XML | [Help](Modules/Help.md) |

## 可选集成

以下均需显式安装，不从默认产品反向引用 SDK。版本、宏、平台与许可按所选集成的声明准备，不以“目录已存在”当作已接入。

| 集成 | 使用边界 |
| --- | --- |
| DOTween | `Integrations/DOTween`，外部 DOTween.dll 与 `DOTWEEN`；[Action 接入](Modules/Action.md#扩展方式) |
| Excel | `Integrations/Excel/Editor`，完整 MiniExcel 后端及许可；[配置作者流程](Modules/Data.md#excel-作者后端) |
| DomainExcel | `Integrations/DomainExcel`，完整 Excel 后端与 `YL_DOMAIN_EXCEL`；[领域导入](Modules/Domain.md#domainexcel) |
| HybridCLR | 独立导出，使用声明的官方 SDK 并复用 Resource；[架构边界](Architecture.md#hybridclr-可选接入)，完整步骤在选装目录 README |

## 维护与来源

框架代码规范见 [CodingStyle](CodingStyle.md)，来源与许可见 [LICENSE](../LICENSE.md)。历史设计与迁移的查找入口为 [历史索引](IntegrationPlan.md)，不是当前待办或安装说明。

源码仓库的 Tests、测试 CLI 与验收夹具不随默认包交付。消费方使用自己的真实编译、接入场景和目标平台验证；旧结果、缺失工具或同名 API 都不能代替当前使用环境的证据。
