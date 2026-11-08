# YLFramework

面向 Unity 的开源 C# 游戏开发框架，提供可按需使用的基础机制、游戏系统和编辑器工具。

YLFramework 希望减少游戏项目中重复搭建基础设施的工作：小型功能可以直接使用事件、对象池和计时工具，需要组织业务时再接入 Module、UI、资源与其他系统，不要求从一套完整的应用模板开始。

## 主要能力

| 方向 | 内容 |
| --- | --- |
| 基础机制 | 事件与订阅、对象池、计时、依赖注入、数据绑定、协程与更新桥接 |
| 业务与表现 | Module / MVC、View / Panel、UI 根与输入协调、组合动作、音频播放 |
| 资源管理 | 资产与场景租约、AssetBundle 构建、内置部署、远端更新 |
| 选装功能 | 配置数据、本地化、存档、状态机、二维移动与检测、玩法系统、红点、Excel、HybridCLR |
| 编辑器工具 | 配置编辑、资源工作台、代码生成、帮助索引与使用诊断 |

主包与选装包的用途及依赖见[文档导航](PackageSources/com.kano.ylframework/Documentation~/README.md)。

## 开发状态

框架仍在开发中，尚未正式发布，也尚无真实项目使用。API、包结构与功能可能继续调整，不提供稳定版本承诺。

当前开发宿主使用 Unity `6000.3.27f1`。包声明、接入前置和具体能力限制见[安装指南](PackageSources/com.kano.ylframework/Documentation~/Installation.md)，不能把开发环境版本当作所有平台均已验证的结论。

## 开始使用

1. 按[安装指南](PackageSources/com.kano.ylframework/Documentation~/Installation.md)获取源码，将需要的本地 UPM 包接入自己的 Unity 工程。
2. 跟随[快速开始](PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)运行第一个事件示例。
3. 从[能力导航](PackageSources/com.kano.ylframework/Documentation~/README.md#主包能力)选择需要的模块，选装功能有各自独立手册。

本仓库 `YLFrameworkDev` 是框架的开发与验证宿主，不是游戏模板。使用框架不需要复制宿主的 `Assets`、`Packages` 或 `ProjectSettings`。

## 文档与贡献

- [使用手册](PackageSources/com.kano.ylframework/Documentation~/README.md)：安装、入门与能力导航。
- [架构说明](PackageSources/com.kano.ylframework/Documentation~/Architecture.md)：分层、依赖与生命周期所有权。
- [贡献指南](CONTRIBUTING.md)：源码组织、开发验证与文档维护。

欢迎通过 [Issues](https://github.com/KONOK-A/YLFramework-dev/issues)反馈可复现的问题或讨论功能需求，通过 Pull Request 提交改进。

## 许可证

YLFramework 使用 [MIT License](LICENSE)。第三方依赖的来源、署名与适用条款保留在各包的 `LICENSE.md` 和对应依赖声明中，详见[主包许可证](PackageSources/com.kano.ylframework/LICENSE.md)。
