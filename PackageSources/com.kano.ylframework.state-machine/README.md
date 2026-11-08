# YLFramework 配置式状态机

`com.kano.ylframework.state-machine` 提供配置资产驱动的层级状态机、参数、条件组、多层运行和图形编辑器。适合需要在 Inspector 与图窗口中配置状态和过渡的项目；主包的轻量纯代码 `YLFramework.FSM` 是另一套独立能力。

## 依赖与接入

- 依赖主包 `com.kano.ylframework`，不要求 Root、Module、Movement2D 或输入包。
- 当前从源码使用本地 UPM 包，须同时为主包与本包提供明确来源；Git 接入也不能仅靠包版本声明取得同仓库兄弟包。
- Runtime asmdef 引用 `YLFramework.StateMachine`；直接调用作者 API 的 Editor asmdef 另引用 `YLFramework.StateMachine.Editor`。
- 安装步骤见[主包安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，框架入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些跨包链接指向源码仓库当前文档。

## 开始使用

通过 `Assets/Create/YLFramework/Asset FSM/FSMController` 创建配置，在 `Window/YLFramework/Asset FSM/StateMachineWindow` 编辑，然后将配置加入 GameObject 上的 `FsmController` 运行层。控制器负责转发 Unity 的三个更新阶段，状态脚本继承 `FsmState`。

## 手册

[使用手册](Documentation~/README.md) 说明创建与绑定脚本、参数与过渡、运行层、生命周期和作者工具边界。生成的脚本、配置资产及使用它们的场景由使用工程持有，卸载本包不会删除这些内容。

## 许可证

[MIT](LICENSE.md)。
