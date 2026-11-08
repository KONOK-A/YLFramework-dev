# YLFramework 主包

`com.kano.ylframework` 提供框架的基础机制、Module / MVC / UI、资源、音频、动作与配套编辑器工具。项目仍在开发中，尚未正式发布。

## 能力与边界

- 基础机制可独立使用，不要求创建 `YLFrameworkRoot` 或启动业务 Module。
- `YLFramework.Runtime` / `YLFramework.Editor` 是两个主体程序集；Foundation、Core、Systems、Utilities 是包内职责区，不是四个独立安装包。
- 包含正式 UI 模板、BouncyCastle 与许可证、使用手册、Roslyn Analyzer，以及需显式导入的 Resource Startup 样例。
- DOTween Action 适配源码随主包提供，只有准备外置 SDK 和 `DOTWEEN` 时才编译；主包不捆绑 SDK。
- 配置、存档、本地化、玩法等选装能力各自有独立包；测试、仓库工具、MCP 和开发宿主设置不属于本包。

## 接入

当前推荐从源码使用本地 UPM 包，按[安装指南](Documentation~/Installation.md)添加本目录的 `package.json`。UGUI、Json.NET 与内置 Unity 模块按包声明由 UPM 解析，不需要另拷 DLL。

业务 asmdef 引用 `YLFramework.Runtime`；仅 Editor 代码引用 `YLFramework.Editor`。从[快速开始](Documentation~/QuickStart.md)运行事件示例，再选择需要的模块。

## 文档

- [使用手册与能力导航](Documentation~/README.md)
- [架构与所有权](Documentation~/Architecture.md)
- [UI 与 Module](Documentation~/Modules/UI.md)
- [资源接入](Documentation~/Modules/Resource.md)
- [音频播放](Documentation~/Modules/Audio.md)
- [编辑器工具与生成输出](Documentation~/Modules/Editor.md)

`Documentation~` 不进入 AssetDatabase，请从包的实际磁盘目录打开 Markdown。Package Manager 的 Samples 中显式 Import 后，`Samples~/ResourceStartup` 才作为使用工程副本参与编译。

包源码是输入；配置、工作簿和生成代码由使用工程拥有，默认根为 `Assets/YLFrameworkData`。包的安装或移除不负责删除这些数据。

## 许可证

许可及第三方来源见 [LICENSE.md](LICENSE.md)。
