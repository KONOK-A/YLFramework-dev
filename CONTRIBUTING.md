# 贡献指南

感谢你参与 YLFramework 的开发。本仓库用于维护框架源码、文档与验证工具，产品代码不依赖开发宿主中的所有插件。

## 熟悉仓库

| 位置 | 内容 |
| --- | --- |
| `PackageSources/<包 ID>/` | 产品代码、随包资产及样例；包集合以 [PackageCatalog.json](Tools/Packaging/PackageCatalog.json) 为准，版本、依赖和文件白名单以各包的 `package.json` 为准 |
| `PackageSources/com.kano.ylframework/Documentation~/` | 公共接入指南、架构与主包能力文档 |
| 各选装包的 `README.md`、`Documentation~/` | 包内接入入口、所属功能的说明与必要的第三方声明 |
| `Packages/`、`ProjectSettings/` | 开发宿主使用的包、解析锁和 Unity 设置，不是产品源码；不要将 Editor 自动产生的无关改动带入 PR |
| `Assets/Scenes/YLFrameworkDev.unity` | 开发宿主场景 |
| `Assets/Editor/YLFrameworkDev/` | 开发仓库专用的资源作者工具 |
| `Assets/Tests/YLFramework/` | Unity 测试、探针与夹具，不随产品包分发 |
| `Tools/Testing/` | 声明式测试执行与隔离设施 |
| `Tools/Packaging/` | 包清单、制品构建与发行准备 |
| `Tools/CodeStyle/`、`Tools/YLFramework.Analyzers/` | 格式检查与 Analyzer 的独立构建工具 |
| 仓库外的测试工作根 | 测试工具拥有的 `Hosts/`、`Results/`、`Cache/`。不要把测试宿主、构建和运行回执放进仓库或产品包 |

修改前阅读[架构](PackageSources/com.kano.ylframework/Documentation~/Architecture.md)和[代码规范](PackageSources/com.kano.ylframework/Documentation~/CodingStyle.md)，再查对应模块的实现、调用方和测试。

例如，修改资源交付逻辑时，产品实现和消费者手册归所属包；开发验收夹具归 `Assets/Tests/YLFramework/`，执行脚本归 `Tools/Testing/`。旧的 `Assets/YLFramework/` 不是第二份产品源码；本机若仍有遗留数据，不在那里继续开发，也不要因目录被 Git 忽略就删除它。

## 准备开发环境

1. 安装 `ProjectSettings/ProjectVersion.txt` 指定的 Unity Editor，以及 Git 和 Git LFS，确保随包 DLL 已实际下载而不是 LFS 指针文件。
2. 用 Unity 打开仓库，等待 UPM 解析和真实编译。宿主通过 `file:` 连接 `PackageSources/`，无需复制产品源码到 `Assets`。
3. 只运行当前任务需要的工具。测试、格式与 Analyzer 有各自的工具链要求，具体前置见对应说明，不随意升级锁定版本。

宿主中的 MCP、商业编辑器工具及可选 SDK 不等于框架使用前置。在独立工程接入框架请看[安装指南](PackageSources/com.kano.ylframework/Documentation~/Installation.md)。

## 提交改进

- 问题反馈请包含目标、复现步骤、预期与实际行为、Unity/包版本，以及脱敏后的必要日志。
- 修改保持聚焦。涉及 API、序列化、资源地址或生成输出时，检查真实调用方、仓库资产和生成模板，不只搜索 C# 引用。
- 使用短期任务分支和 Pull Request 说明变更目的、影响范围、实际验证及未验证项。不要提交无关的宿主设置、缓存、生成工程或本机工具配置。
- Unity 资产移动保留 `.meta`/GUID；独立复制使用新身份。不要因名称或日期看似陈旧而删除测试夹具、项目数据或资源快照。
- 不手工修改 `Library`、`Temp`、生成的 `.csproj`/`.sln` 或 UPM 解析产生的锁文件来修复产品问题。

## 按影响验证

测试 CLI、独立工作根、发现与执行、结果解释统一见[测试工具说明](Tools/Testing/README.md)。格式规则及工具见[代码规范](PackageSources/com.kano.ylframework/Documentation~/CodingStyle.md#格式工具)。Analyzer 见[重建与验证](PackageSources/com.kano.ylframework/Documentation~/Modules/Attributes.md#重建与验证)。

| 改动 | 需要的证据 |
| --- | --- |
| 文档正文 | 事实、链接、锚点、命令与 diff 检查 |
| 包内文档目录或 `files` | 上述检查及 NoUnity 包内容合同 |
| Runtime / Editor 代码 | 实际 Unity 编译、相关模块测试与生命周期/失败路径 |
| 程序集、分发、AOT | 编译图、独立安装、相应 Player 与专项验证 |
| 生成器或序列化 | 正式生成输出与消费者，实际资产/数据的保存、重导入或往返 |

工具层通过不表示 Unity、PlayMode、Player 或特定平台通过；交付中分别记录，不沿用其他提交的结果。打包与未来发行准备见[打包工具说明](Tools/Packaging/README.md)。

## 文档维护

文档描述当前实现和当前可用的接入方式，不作为开发过程记录。

- 正文和标题使用简体中文；类型名、API、namespace、包 ID、路径、命令和专业术语保留英文。法律文本和第三方声明保持原文。
- 根 README 负责项目介绍与阅读入口，包 README 负责该包的用途和接入入口，详细用法放所属包的 `Documentation~`。架构、代码规范和工具操作各有独立权威位置。
- 删除旧 API 对照、一次性迁移、历史回执、测试计数和机器路径；保留当前格式版本、平台约束及所有权说明。不为尚未发布的项目虚构用户迁移需求。
- 同包链接使用相对路径；包内文档跨包或链接源码仓库工具时，使用明确标示的 GitHub 源码当前文档 URL，不假设 UPM 缓存目录的相对位置。
- 改标题同步修改入链。`Attributes.md` 中与诊断 ID 对应的锚点由 Analyzer 使用，不删除；`Resource.md` 等当前 Editor 打开的顶层文档路径也须保留。
- 新增选装包手册时把 `Documentation~` 加入该包 `package.json.files`。隐藏文档无需新增 `.meta`；已有文档移动或删除时同步处理其元数据，不复制 GUID。
- 检查代码围栏、表格、文件路径、标题锚点和术语一致性。导航检查不能替代对 API、示例和限制的人工核对。

在仓库根运行 `pwsh -NoProfile -File Tools/Testing/Tests/Test-YLDocumentationNoUnity.ps1`，可离线检查根文档、工具指南和包手册的导航与本仓库链接。它不会访问外部网站，也不验证 Unity 行为。若改变包内路径或白名单，再按[打包说明](Tools/Packaging/README.md#验证工作区内容)检查实际负载。

### 模块文档

主包模块从[主包能力表](PackageSources/com.kano.ylframework/Documentation~/README.md#主包能力)进入；选装能力从[选装能力表](PackageSources/com.kano.ylframework/Documentation~/README.md#选装能力)进入。新增或改名时应同时更新入口和正文，不为同一 API 在多个包复制一份手册。

| 读者的问题 | 作者要核对的内容 |
| --- | --- |
| 这个模块做什么，要安装什么？ | 说明职责和不适用的场景；从 `package.json`、`.asmdef` 和实际门控核对依赖、平台、Editor/Runtime 边界。区分必需依赖与可选集成。 |
| 最短的可用路径是什么？ | 给出一个与当前 API 一致的示例，注明是完整类型还是方法体片段，交代输入来源、调用时机及预期结果。纯作者工具可用操作步骤替代代码。 |
| 谁负责清理，出错如何判断？ | 涉及订阅、句柄、租约或异步操作时，说明所有者、取消和释放时机；没有这些概念时不填空章节。 |
| 修改后是否仍然成立？ | 对照源码、样例和相关测试。改变路径、标题或入口时检查 Editor 帮助和 Analyzer 链接；公开限制只写已有证据支持的部分。 |

## 计划与交接

计划说明“还要做什么”，手册说明“当前版本怎么用”。需共同评审的范围、取舍和验收条件放在 Issue 或 PR；个人执行顺序、机器路径和长日志不随包分发，也不作为共享规范的前置条件。

- **开始时**写明目标、非目标、基线提交、影响目录和验收条件。一个任务只保留一份当前状态；未确认的设想不要写成已支持能力。
- **执行中**区分待办、阻塞、已完成、未验证。结果引用真实命令、提交和证据位置，不复制整套宿主，也不把历史批准当作本次操作授权。
- **收尾时**将实际用法写回模块手册，命令和输出约定写回工具 README，遗留事项说明复现条件并关联后续任务。过期方案离开活动列表，历史记录不替代当前验收。

本机 `AGENTS.md` 只补充自动化协作约定，`.kilo/plans/` 只保存本机计划；两者均按 `.gitignore` 留在本机，不强制加入 Git，也不放入 `PackageSources/`。所有贡献者都需要遵守的目录、开发与文档规则应维护在本页或其链接的权威文档中。
