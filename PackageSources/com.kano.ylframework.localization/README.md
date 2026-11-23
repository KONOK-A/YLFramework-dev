# Localization 本地化

`com.kano.ylframework.localization` 以 Unity Localization 为内核，复用官方 Locale、String/Asset Tables、Smart Strings 与作者工作流，补充 YL 消费者会话、业务数据联动和局部表现协调。

## 依赖与接入

依赖主包、UGUI 和 `com.unity.localization` 1.5.14，由 UPM 解析官方 Addressables。业务 asmdef 引用 `YLFramework.Localization`；TMP 表现引用独立的 `YLFramework.Localization.TMP`。Unity 6 UGUI 内置 TMP，较早版本按实际安装启用。

DataAssets/Excel 是可选 Editor 适配，不会因安装本包被强制安装。翻译内容走官方 Localization/Addressables，不接管 YL Resource Provider。

从当前源码接入时显式提供主包和本包，步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)；基础入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档。

## 开始使用

1. 在官方 Localization Settings 配置 Locale 和表，在官方表中编辑翻译。
2. 给 UGUI/TMP 文本或 Image 添加类型化绑定，直接选择官方引用；需要协调的内容放进 LocalizationGroup 专属子 CanvasGroup。
3. Panel 每次 OnOpen 调用 `group.BeginSession(this, configure)`，用 BindVariable 连接业务数据；关闭时按 owner 自动收尾，不登记语言根或手动管理每个监听。

Package Manager 提供 `Basic UI` 以及可选 `DataAssets Excel` Samples。基础样例包含完整场景/官方数据生成入口和 Core Panel 模板，不要求使用工程先安装配置表工具。

完整接入、所有权、面板绑定及可选工作簿流程见[使用手册](Documentation~/README.md)。

## 许可证

本包采用 [MIT 许可证](LICENSE.md)。
