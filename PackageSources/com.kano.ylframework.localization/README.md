# Localization 本地化

`com.kano.ylframework.localization` 提供显式多语言配置、确定的逐条回退、UGUI / TextMesh / 字符串成员绑定，以及交由 Resource 管理的图片显示。

## 依赖与接入

依赖主包 `com.kano.ylframework` 与 `com.unity.ugui`，不依赖 DataAssets、Excel、Save、TMP 或外部本地化 SDK。业务 asmdef 引用 `YLFramework.Localization`；作者工具位于 Editor-only 的 `YLFramework.Localization.Editor`。

从当前源码接入时显式提供主包和本包，步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)；基础入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档。

## 开始使用

1. 创建 `MultiLanguageConfigs`，打开 `YLFramework/Localization/多语言配置` 编辑语言与条目。
2. 用 `LocalizationManager.AddConfig(root)` 登记已加载根，保留返回的 `LocalizationConfigHandle`。
3. 设置 `LocalizationManager.Language`，用 `GetString(id)` 或显示组件读取内容；使用结束时释放自己的句柄。

完整示例、语言回退、图片与生命周期见[使用手册](Documentation~/README.md)。

## 许可证

本包采用 [MIT 许可证](LICENSE.md)。
