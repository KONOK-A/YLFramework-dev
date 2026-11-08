# 贡献指南

感谢你参与 YLFramework 的开发。本仓库用于维护框架源码、文档与验证工具，产品代码不依赖开发宿主中的所有插件。

## 熟悉仓库

| 位置 | 内容 |
| --- | --- |
| `PackageSources/` | 主包与十个选装包，各包的 `package.json` 定义安装身份和直接依赖 |
| `PackageSources/com.kano.ylframework/Documentation~/` | 公共接入指南、架构与主包能力文档 |
| 各选装包的 `Documentation~/` | 对应包的完整使用说明 |
| `Assets/Scenes/YLFrameworkDev.unity` | 开发宿主场景 |
| `Assets/Editor/YLFrameworkDev/` | 开发仓库专用的资源作者工具 |
| `Assets/Tests/YLFramework/` | Unity 测试、探针与夹具，不随产品包分发 |
| `Tools/Testing/` | 声明式测试执行与隔离设施 |
| `Tools/Packaging/` | 包清单、制品构建与发行准备 |
| `Tools/CodeStyle/`、`Tools/YLFramework.Analyzers/` | 格式检查与 Analyzer 的独立构建工具 |

修改前阅读[架构](PackageSources/com.kano.ylframework/Documentation~/Architecture.md)和[代码规范](PackageSources/com.kano.ylframework/Documentation~/CodingStyle.md)，再查对应模块的实现、调用方和测试。

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
- 使用“用途与边界、前置、最小示例、常用任务、生命周期与错误、限制、相关文档”的结构，按内容取舍，不为简单工具填充空章节。
- 示例注明完整类型或方法体片段，交代变量来源、程序集、初始化、预期结果和清理责任。不得以省略必要释放来缩短示例。
- 删除旧 API 对照、一次性迁移、历史回执、测试计数和机器路径；保留当前格式版本、平台约束及所有权说明。不为尚未发布的项目虚构用户迁移需求。
- 同包链接使用相对路径；包内文档跨包或链接源码仓库工具时，使用明确标示的 GitHub 源码当前文档 URL，不假设 UPM 缓存目录的相对位置。
- 改标题同步修改入链。`Attributes.md` 的 `yl0001` 至 `yl0010` 是当前诊断工具入口，不删除；`Resource.md` 等当前 Editor 打开的顶层文档路径也须保留。
- 新增选装包手册时把 `Documentation~` 加入该包 `package.json.files`。隐藏文档无需新增 `.meta`；已有文档移动或删除时同步处理其元数据，不复制 GUID。
- 检查代码围栏、表格、文件路径、标题锚点和术语一致性；不以 CI 通过代替阅读质量检查，当前 CI 没有通用 Markdown 链接检查。
