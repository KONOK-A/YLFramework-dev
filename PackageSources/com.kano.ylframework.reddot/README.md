# YLFramework 红点

`com.kano.ylframework.reddot` 提供按路径聚合的红点计数、独立来源凭据、路径订阅、显式已读状态与只读诊断。`Reddot` 组件只更新绑定的指示器，不修改业务来源。

## 依赖与接入

- 核心只依赖主包 `com.kano.ylframework`，不要求 Save、Gameplay、Excel 或额外字体/UI 包。
- 当前从源码使用本地 UPM 包，须为主包和本包提供明确来源；Git 接入不会由版本声明自动取得兄弟包。
- Runtime namespace 与 asmdef 均为 `YLFramework.RedDots`。安装 Save 后，快照 IO 组合另由条件程序集 `YLFramework.RedDots.Save` 提供。
- 安装步骤见[主包安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，框架入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些跨包链接指向源码仓库当前文档。

## 开始使用

业务在主线程调用 `ReddotManager.RegisterSource(path, sourceId, initialValue)` 并持有返回凭据；UI 通过 `Subscribe` 或 `Reddot` 组件观察路径。业务结束 Dispose 自己的来源，UI 停用时解除自己的订阅。无需创建 Root 或全局存档实例。

## 手册

[使用手册](Documentation~/README.md) 包含路径与聚合规则、Batch、组件生命周期、schema-1 已读快照、可选 Save 组合与诊断窗口。核心不执行 IO、不自动切换账号或槽位。

## 许可证

[MIT](LICENSE.md)。
