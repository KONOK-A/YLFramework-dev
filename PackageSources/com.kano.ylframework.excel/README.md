# Excel 作者工具

`com.kano.ylframework.excel` 提供 Editor-only 的 `.xlsx` 导出、SO / List / JSON 导入预览、DataAssets 工作簿面板，以及同时安装 Gameplay 时启用的领域适配。Runtime 字段 Attribute 位于主包，本包不提供 Player 工作簿 IO。

## 依赖与接入

依赖主包、`com.kano.ylframework.data-assets` 与 `com.unity.nuget.newtonsoft-json`。当前从源码以本地 UPM 接入，显式提供全部自有包来源；Git 接入也不能靠 SemVer 声明取得未发布的兄弟包。

安装方式见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，基础入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档。

## 开始使用

打开 `YLFramework/Excel/Data Assets Workbook`，选择已保存的 DataAssets 根，导出工作簿、编辑后预览导入，再明确确认或取消。预览不提交资产；输出工作簿和配置属于使用工程，不写入安装包。

- [Excel 使用手册](Documentation~/README.md)：表头、字段转换、代码入口、提交与故障恢复。
- [Gameplay 适配](Documentation~/Gameplay.md)：Skill / Buff / Mission / Equipment 的条件程序集和导入流程。

## 许可证与来源

本包采用 [MIT 及所列第三方许可](LICENSE.md)。随包的 MiniExcel 1.31.2 位于 `Editor/Plugins/MiniExcel.dll`，采用 Apache-2.0，仅用于 Editor，不在安装时执行 NuGet 下载。保留其[许可证](Editor/Licenses/MiniExcel-LICENSE.txt)、[署名](Editor/Licenses/MiniExcel-ATTRIBUTION.txt)与[依赖来源声明](Editor/Dependencies.json)。由 Kano 维护，原始版权声明保留。
