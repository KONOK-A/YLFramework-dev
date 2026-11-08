# YLFramework HybridCLR 集成

`com.kano.ylframework.hybridclr` 是选装的代码加载与构建编排适配：复用 Resource 发布/更新和 Core Module 启动，提供新基座构建、冻结 AOT 基线、项目补丁与显式运行时加载。不包含官方 SDK 或 native runtime。

## 依赖与接入

- 依赖主包 `com.kano.ylframework`、官方 `com.code-philosophy.hybridclr` SDK `9.0.3` 与 Newtonsoft.Json。
- 当前使用源码/本地 UPM 接入主包和本适配，官方 SDK 另行显式声明。原生构建门禁要求官方 Git 来源 `https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3` 及精确解析 hash，不能用版本号相同的其他来源代替。
- Runtime asmdef 引用 `YLFramework.HybridCLR`，namespace 为 `YLFramework.Integrations.HybridCLR`；作者 API 在 `YLFramework.HybridCLR.Editor`，namespace 为 `YLFramework.Integrations.HybridCLR.Editor`。
- 当前原生目标限定 Windows64、IL2CPP、.NET Framework；Editor 模拟不代表原生验证。
- 公共接入见[安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)和[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些跨包链接指向源码仓库当前文档。

## 开始使用

先配置官方热更程序集与项目本地 native runtime，再打开 `YLFramework/Integrations/HybridCLR` 创建配置并检查环境。首发用 `BuildBasePlayer` 建立新的冻结基座，后续代码发布用 `PublishPatch`。运行时先启动 Resource，再由应用持有 `HybridCLRLoader`；关闭 Module 后才释放 loader，最后关闭自有 ResourceStartup。

## 手册

[使用手册](Documentation~/README.md) 包含精确 SDK 门禁、当前 Profile 字段、生成资源映射、共享基座、补丁限制、加载/重启与关闭责任，以及本包 `HybridCLR Module` 样例的显式导入流程。

## 许可证

[MIT](LICENSE.md)。官方 SDK 与 native runtime 由各自来源提供，本包不镜像这些内容。
