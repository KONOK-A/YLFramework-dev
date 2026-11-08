# Gameplay 领域系统

`com.kano.ylframework.gameplay` 提供 Skill、Buff、Mission、Equipment 四个独立运行时和配套作者工具。各域维护自己的定义与运行实例，不是统一业务 Module，也不自动联动背包、发奖或存档。

## 依赖与接入

依赖主包 `com.kano.ylframework` 与 `com.unity.nuget.newtonsoft-json`，不依赖 DataAssets、Localization、Excel 或 Save。业务 asmdef 按需引用 `YLFramework.Skill`、`YLFramework.Buff`、`YLFramework.Mission`、`YLFramework.Equipment`；共享作者工具属于 Editor-only 的 `YLFramework.Gameplay.Editor`。

当前源码接入显式提供主包和本包，步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，基础入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档。

## 开始使用

创建对应的 `SkillsConfig/BuffsConfig/MissionsConfig/EquipmentsConfig`，通过各域作者窗口配置条目，再向对应 Manager 显式 `AddConfig` 或 `LoadConfig`。Skill / Buff / Equipment 的宿主结束时须收尾自身实例，配置与全域资源由登记方管理。

四域用法、字段协议、回调顺序和关闭边界见[使用手册](Documentation~/README.md)。需要工作簿时另装 Excel，按[领域适配指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/Gameplay.md)接入。

## 许可证

本包采用 [MIT 许可证](LICENSE.md)。
