# YLFramework

YLFramework 是 Unity C# 游戏框架，提供基础机制、Module / MVC / UI、资源与音频、配置与领域系统，以及配套作者工具。默认以 **Runtime / Editor 两个主体程序集**交付，能力按需使用，不要求先搭建完整应用框架。

本页是消费方入口。维护源仓库、运行框架测试或准备发行时，使用源码仓库根 README；不要把开发宿主当作游戏工程模板。首版 UPM 包为 `0.1.0-preview.1`，维护者 Kano；版本号本身不表示已完成验收或发布。

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

1. 首选取得冻结 Release 资产及对应 Registry 验收回执；首版仍是 Unreleased，以下配置不表示五个版本已经公开可用。Git 回退则需要本次验收提供的真实完整 SHA，不能安装占位值或假设同名 tag 已发布。
2. Registry 版本就绪后，按下节在消费工程 `Packages/manifest.json` 只声明所选能力的唯一入口，等待 UPM 自动解析闭包。使用 Git 回退时则显式声明全部 Git 闭包。不要复制源宿主的 Packages、ProjectSettings 或整个外部 Plugins / Resources 目录。
3. 消费方 asmdef 显式引用 `YLFramework.Runtime`；仅 Editor 扩展引用 `YLFramework.Editor`。检查真实 Unity 编译，而不是只看 IDE。
4. 按能力完成显式装配。基础工具可直接使用；UI、Resource、命名 Audio 和领域配置遵守各自初始化与所有权，不因放入 Root Prefab 就自动全部启用。
5. 在目标工程验证实际场景与 Player；配置、反射类型、资源闭包和可选 SDK 的平台前置由应用一并准备。

| 默认依赖 | 安装与边界 |
| --- | --- |
| UGUI：`com.unity.ugui` | 包声明 `1.0.0`，由 UPM 按目标 Editor / 工程解析兼容版本；正式 UI 和部分动作依赖 UnityEngine.UI |
| Json.NET：`com.unity.nuget.newtonsoft-json` | 包声明 `3.2.1`，由 UPM 自动解析，不需手动拷 DLL；不可用同名替身 DLL 代替 |
| `BouncyCastle.Cryptography 2.7.0` | netstandard2.0 DLL 与许可已随框架携带，用于 HKDF-SHA256；不要再导入冲突副本 |

主包还显式声明所用 Unity 内置模块。默认产品不包含或要求可选宿主扩展、Odin、MCP、DOTween、MiniExcel 或 HybridCLR。可选依赖只在实际安装相应集成时需要，四个适配包不混入主包。

### Registry 与自动依赖

计划的主发行渠道为 `https://package.openupm.com`，采用 GitHub Release asset tracking，不跟踪开发仓库根目录。五包同步版本为 `0.1.0-preview.1`。完成发布及实际 Registry 验收后，主包的最小工程 manifest 为：

```json
{
  "scopedRegistries": [
    { "name": "OpenUPM", "url": "https://package.openupm.com", "scopes": ["com.kano.ylframework"] }
  ],
  "dependencies": { "com.kano.ylframework": "0.1.0-preview.1" }
}
```

只把 `dependencies` 中的入口替换为下表所需包，不预填主包、Excel、UGUI、Newtonsoft.Json 或全部 Unity 内置模块来制造“自动依赖成功”。各包依赖里的版本号、实际解析版本及 Editor 自带基线是不同证据；Unity 6 的内置 UGUI 可以是 `2.0.0`，不把它机械改成包声明的 `1.0.0`。

| 唯一直接入口 | 自有间接闭包 | 额外前置 |
| --- | --- | --- |
| `com.kano.ylframework` | 无选装包 | 官方依赖由 UPM 解析 |
| `com.kano.ylframework.data-assets` | 主包 | ScriptableObject 根、登记与作者窗口 |
| `com.kano.ylframework.localization` | 主包、UGUI | 显式语言根、文本/ResourceImage 与作者窗口 |
| `com.kano.ylframework.save` | Newtonsoft UPM，无主包依赖 | 槽位、同步/异步JSON、原子单文件和显式恢复 |
| `com.kano.ylframework.excel` | 主包、DataAssets | MiniExcel 与许可随 Excel 包携带 |
| `com.kano.ylframework.domain-excel` | 主包、DataAssets、Excel | 不需要全局 `YL_DOMAIN_EXCEL` |
| `com.kano.ylframework.dotween` | 主包 | SDK 独立准备；无 SDK 的门控检查不是正向集成 |
| `com.kano.ylframework.hybridclr` | 主包 | 额外添加窄 scope `com.code-philosophy.hybridclr`，官方 SDK `9.0.3` 间接解析，不随适配包捆绑 |

不要把 `com.unity`、`com` 等宽 scope 指向 OpenUPM。HybridCLR 完整 manifest 见[适配包说明](../../com.kano.ylframework.hybridclr/README.md#registry-安装)。Registry 验收目标是标准版 `2022.3.62f3` 与 `6000.3.27f1`，与 China `2022.3.62f3c1` 和历史 native 专项分开。实际支持范围仍以对应源码、发布字节和回执为准。

清单内全部包的 `.tgz` 与 `release-manifest.json` 是发行身份记录。真实 UPM LocalTarball 安装只作 ArtifactPreflight，不能替代 Registry 元数据、下载字节、实际来源和间接依赖验收。相同版本重开或重新解析也不能声称完成了跨版本升级。

### Git URL 与依赖闭包

固定格式如下，`?path=` 必须在 `#` 之前，路径指向包根，修订使用完整 40 位 SHA，不用短 SHA 或可变分支：

```text
https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/com.kano.ylframework#<full-commit-sha>
```

其中 `<full-commit-sha>` 仅是格式记号，不能直接安装。验收完成后应交付独立的清单/回执，包含实际已验证提交的完整 SHA、各包完整 URL、可直接使用的 `Packages/manifest.json` 依赖片段及真实验证范围；本文不提前填入未经验证的 SHA，也不让源码提交包含其自身 SHA。首次版本号不等于已存在的发布 tag。

Unity 不支持在 `package.json` 中声明 Git URL 传递依赖。自有包虽然声明 SemVer 依赖，Git 消费方仍须逐项显式列出下表中的闭包；各 YL 包使用同一已验证修订。将 URL 的包目录替换为相应 ID，不能只安装最外层叶子后等待 UPM 去 Registry 找自有包。

| 选择的能力 | 必须显式声明的自有 Git 包 | 额外前置 |
| --- | --- | --- |
| 主包 | `com.kano.ylframework` | UGUI / Json.NET 等 Registry 依赖自动解析 |
| DOTween | 主包 + `com.kano.ylframework.dotween` | 独立安装官方 DOTween Standard `1.2.825`，完成 SDK Setup 和 `DOTWEEN` |
| DataAssets | 主包 + `com.kano.ylframework.data-assets` | 不要求安装 Excel |
| Localization | 主包 + `com.kano.ylframework.localization` | 不要求安装 DataAssets/Excel/TMP |
| Save | 仅 `com.kano.ylframework.save` + Newtonsoft UPM | 不需要主包；若使用主包SerializedDictionary则消费方显式安装两者 |
| 配置式 StateMachine | 主包 + `com.kano.ylframework.state-machine` | Runtime与图编辑器/状态脚本生成一起交付；轻量FSM不随它迁移 |
| Character2D / Platform2D | 主包 + `com.kano.ylframework.movement2d` + physics2d | 一个安装包、两个互不引用的Runtime及一个公共Editor，Overlap不随它迁移 |
| Excel | 主包 + DataAssets + `com.kano.ylframework.excel` | MiniExcel `1.31.2` 与 Apache-2.0 许可随 Excel 包携带 |
| Domain Excel | 主包 + DataAssets + Excel + `com.kano.ylframework.domain-excel` | 安装即可启用，不要求全局 `YL_DOMAIN_EXCEL` |
| HybridCLR | 主包 + `com.kano.ylframework.hybridclr` | 官方 SDK `9.0.3` 也需单独声明其 Git 来源，匹配 native / 平台门禁 |

本地源码开发可从每个包根的 `package.json` 使用本地安装，或在开发宿主 manifest 使用 `file:../PackageSources/<包ID>`。本地连接不代表 Git 包已完成安装验收，也不将宿主 SDK、测试或配置纳入产品依赖。

Resource 默认不启用内容加密。选择 AES-256-GCM 内容加密时，当前原生后端使用 Windows CNG；没有软件 GCM 或临时明文回退。具体平台、key provider 与失败合同见 [可选 Bundle 内容加密](Modules/Resource.md#可选-bundle-内容加密)。

### 帮助与包内容

- 帮助菜单为 `YLFramework/帮助`，以及 Core / Toolkit / Action / Audio 子入口；它只读取索引、元数据与 XML，不执行示例或启动系统，见 [Help](Modules/Help.md)。
- `Documentation~` 不进入 AssetDatabase；从 Package Manager 打开包的实际磁盘目录，再用文件系统路径打开 `Documentation~/README.md` 或模块页。开发仓库中位于 `PackageSources/com.kano.ylframework/Documentation~`，不要用 `AssetDatabase.LoadAssetAtPath<TextAsset>` 查找隐藏文档。
- 产品只走 UPM；旧框架 `.unitypackage` Exporter、导入包和选装导出入口已退役。Resource 发布快照、Player 和 HybridCLR 基座是另外的制品，不是框架安装包。

| 默认交付 | 内容 |
| --- | --- |
| `Runtime/`、`Editor/` | 两主体源码、Core 默认资源及编译器输入 |
| `Plugins/BouncyCastle/` | 随包 DLL 与完整许可 |
| `Prefabs/` | 正式 UI 模板，清单与继承关系见 [UI 资产准备](Modules/UI.md#资产准备与迁移) |
| `Samples~/ResourceStartup/` | 仅在 Package Manager 的 Samples 中显式 Import 后编译；生成的使用工程数据不属于包源码 |
| `Documentation~/`、`LICENSE.md` | 产品文档与来源许可；隐藏文档按实际文件路径打开 |

主包不包含 Tests、四个选装包、源仓库 Tools、游戏、测试探针、SDK、MCP、NuGet 缓存或开发宿主配置。Canvas、EventSystem 和 ScreenTransition 等默认 Resources 是正式运行资产，不随样例一起隐藏。Analyzer 以 RoslynAnalyzer 编译器输入交付，不作为 Player 插件；规则、安装与移除只维护在 [Attributes](Modules/Attributes.md#core-使用诊断)。

包目录视为只读输入，生成和升级不覆盖用户数据。Module 生成到 `Assets/YLFrameworkData/Generated/Modules`，SO 创建助手默认写 `Assets/YLFrameworkData/ScriptableObjects`；已有设置、工作簿、样例副本、外置 SDK/native、StreamingAssets 发布与冻结基座不由包安装/升级/卸载自动迁移或清理。旧工程先按 [UPM 迁移](Migration-UPM.md) 备份并核对真实引用，不能把整个旧目录删除当作升级。

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
| Localization、Excel 字段与作者流程 | [Data](Modules/Data.md)，[Localization 包](../../com.kano.ylframework.localization/README.md) |
| DataAssets 根、租约与作者窗口 | [DataAssets 包](../../com.kano.ylframework.data-assets/README.md) |
| 存档槽、SaveStore、序列化与故障恢复 | [Save](Modules/Save.md) |
| Skill、Buff、Mission、Equipment、Reddot、Logging | [Domain](Modules/Domain.md) |
| HTTP 请求、取消与响应快照 | [HTTP](Modules/Http.md) |
| 配置式 StateMachine | [StateMachine 包](../../com.kano.ylframework.state-machine/README.md)，[Behavior](Modules/Behavior.md#两种状态机)；不同于下方轻量 FSM |

### 工具与 Editor

| 能力 | 文档 |
| --- | --- |
| 轻量 FSM、网格与索引 | [FSM](Modules/FSM.md) · [Grid](Modules/Grid.md) · [Table](Modules/Table.md) |
| JSON 树、压缩与通用扩展 | [Json](Modules/Json.md) · [ZipTool](Modules/ZipTool.md) · [Extensions](Modules/Extensions.md) |
| Overlap、CharacterController2D、MoveablePlatform | [Behavior](Modules/Behavior.md)，角色/平台安装[Movement2D包](../../com.kano.ylframework.movement2d/README.md) |
| 取色器与窗口 | [UI](Modules/UI.md#取色控件colorselector) |
| 作者工具、TreeView、设置与 Editor 迁移 | [Editor](Modules/Editor.md) |
| 帮助提供方、窗口与 XML | [Help](Modules/Help.md) |

## 可选集成

以下均需显式安装，不从默认产品反向引用 SDK。版本、宏、平台与许可按所选集成的声明准备，不以“目录已存在”当作已接入。

| 集成 | 使用边界 |
| --- | --- |
| DOTween | [`com.kano.ylframework.dotween`](../../com.kano.ylframework.dotween/README.md)，外部 DOTween.dll 与 `DOTWEEN`；[Action 接入](Modules/Action.md#扩展方式) |
| DataAssets | [`com.kano.ylframework.data-assets`](../../com.kano.ylframework.data-assets/README.md)，独立配置根、登记与Editor作者工具 |
| Localization | [`com.kano.ylframework.localization`](../../com.kano.ylframework.localization/README.md)，独立语言根、显示绑定和Editor作者工具 |
| Save | [`com.kano.ylframework.save`](../../com.kano.ylframework.save/README.md)，无主包安装依赖；[详细存储合同](Modules/Save.md) |
| 配置式 StateMachine | [`com.kano.ylframework.state-machine`](../../com.kano.ylframework.state-machine/README.md)，图作者工具、原模板/样式与真实编译后的脚本绑定 |
| 二维角色与平台 | [`com.kano.ylframework.movement2d`](../../com.kano.ylframework.movement2d/README.md)，独立Runtime、原Prefab/UnityEvent及实际Physics2D生命周期 |
| Excel | [`com.kano.ylframework.excel`](../../com.kano.ylframework.excel/README.md)，复用DataAssets并携带完整 MiniExcel 后端及许可；[配置作者流程](Modules/Data.md#excel-作者后端) |
| DomainExcel | [`com.kano.ylframework.domain-excel`](../../com.kano.ylframework.domain-excel/README.md)，复用 Excel，安装即可启用；[领域导入](Modules/Domain.md#domainexcel) |
| HybridCLR | [`com.kano.ylframework.hybridclr`](../../com.kano.ylframework.hybridclr/README.md)，使用外置官方 SDK 并复用 Resource；[架构边界](Architecture.md#hybridclr-可选接入) |

跨包链接按源码仓库兄弟目录组织；安装后各包的物理缓存目录可能不同，应从对应包的实际磁盘根打开其 README / LICENSE，不把兄弟目录名硬编码为 PackageCache 路径。

## 维护与来源

框架代码规范见 [CodingStyle](CodingStyle.md)，来源与许可见 [LICENSE](../LICENSE.md)。历史设计与迁移的查找入口为 [历史索引](IntegrationPlan.md)，不是当前待办或安装说明。

源码仓库的 Tests、测试 CLI 与验收夹具不随产品包交付。模块文档中的 `Tests/...` 简写表示源码仓库 `Assets/Tests/YLFramework/...`，不是包内路径。消费方使用自己的真实编译、接入场景和目标平台验证；旧结果、缺失工具或同名 API 都不能代替当前使用环境的证据。
