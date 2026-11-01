# 历史设计与迁移索引

> [文档首页](README.md) · [当前架构](Architecture.md)

YLFramework 曾通过源码承接、功能融合、结构整理与独立交付逐步形成当前产品。本文只解释历史记录的查找方式；它不是当前安装步骤、待办列表、兼容承诺或任务授权。

历史中的临时资源后端、原游戏共存验收、阶段暂停、旧程序集与一次性交付路径已经不能代表当前工程。当前产品以 Runtime / Editor 两主体及可选集成为边界，ResourceSystem 负责正式资源链；源仓库是框架开发宿主，不是已迁出的主游戏。

## 当前问题应查哪里

| 问题 | 权威说明 |
| --- | --- |
| 安装、依赖、帮助与正式导出 | [框架首页](README.md#安装与分发) |
| 分层、namespace、资源与生命周期归属 | [Architecture](Architecture.md) |
| Module / View / Panel 与模板迁移 | [UI](Modules/UI.md) |
| Resource、发布制品与 AOT 类型 | [Resource](Modules/Resource.md) |
| 配置资产、工作簿与领域系统 | [Data](Modules/Data.md) · [Domain](Modules/Domain.md) |
| Editor 公共类型与窗口持久身份迁移 | [Editor](Modules/Editor.md#editor-namespace-迁移) |
| 存档格式、错误与恢复 | [Save](Modules/Save.md) |
| 来源与再分发要求 | [LICENSE](../LICENSE.md) |

## 原章节索引

保留原 §1-12 编号，供旧方案和引用定位主题，不保留已经矛盾的过程状态、机器路径、测试计数或可误执行的冻结交付命令。详细原文在本文件的 Git 历史中，不需要复制回产品文档。

| 原节 | 历史主题 | 如何解释 |
| --- | --- | --- |
| §1 | 目标与边界 | 以唯一正式实现取代并行旧链；不意味着永久保留当时的开发宿主 |
| §2 | 主框架与长期系统范围 | 记录来源和融合顺序，不是当前模块、安装包或 DLL 清单 |
| §3 | 总体阶段 | 当时的任务拆分，不是今天仍需顺序执行的门禁 |
| §4 | 长期工程规范 | 当前产品约定见 CodingStyle 和 Architecture |
| §5 | 可复现基线与阶段衔接 | 每份基线只适用于当时输入，不将旧 Hash 或授权外推到新任务 |
| §6 | 阶段 1 启动与任务边界 | 记录 Module / MVC 等承接及当时的中间状态 |
| §7 | 1.4 核心与非干扰验收 | 原游戏与旧宿主扩展共存的历史验证，不是框架默认运行依赖 |
| §8 | 2.3 Update 技术收口 | 当时更新机制和连续 Play 的证据，不是当前完整回归 |
| §9 | 干净导入与独立交付 | 原冻结包、接收工程、Player 与交付身份；不能把旧包当作新源码验收 |
| §10 | Equipment 专项 | 当前合同归 Domain，历史人工结果不替代真实旧资产与当前平台验证 |
| §11 | CommonSystem 补充专项 | 公共作者支撑按当前 Foundation / Common / Domain 所有者维护 |
| §12 | HybridCLR 可选专项 | 原 SDK、基座与原生运行结果按其精确版本解释，不自动升级或重放 |

## 查找原始记录

源码仓库可用 `git log --follow -- Assets/YLFramework/Documentation/IntegrationPlan.md` 查找本文件的完整版本，并结合对应提交、专项任务记录和制品收据定位证据。任务目录 `.kilo/` 属于本机资料，不纳入当前 Git 跟踪或产品包；既有任务原文可从 [Git 历史档案](https://github.com/KONOK-A/YLFramework-dev/tree/3b85ef1e65dd6b0da16422495739afb28e7c8287/.kilo) 查找。缺少它不需要通过恢复旧游戏、SDK 或测试探针解决。

重命名与数据迁移仍须检查真实类型、序列化、反射、资源地址及存量制品。保留 GUID 不等于旧 AQN、旧二进制、Bundle、存档或 HybridCLR 基座自动兼容；当前明确不支持的转换不能从历史“通过”推导出来。

历史失败、修复和冻结制品保留在各自原始记录，不靠改写旧结论或覆盖旧产物制造一致。新的验收应关联新的源码、环境、输入与输出，明确没有验证的平台和场景。
