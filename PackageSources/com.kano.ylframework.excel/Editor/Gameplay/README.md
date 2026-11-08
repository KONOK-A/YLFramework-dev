# Excel Gameplay 适配

本目录是 Excel 包的 Editor-only 领域接线，提供 `DomainExcelCommands` 和 Skill / Buff / Mission / Equipment 的类型化工作簿菜单。同时安装 Gameplay 后，由程序集局部 `YL_GAMEPLAY_PACKAGE` Version Define 启用 `YLFramework.Domain.Excel.Editor`，不要求或修改全局宏。

安装前置、工作表、预览 / 确认 / 取消、参数独立提交及恢复边界统一见同包的 [Gameplay 适配指南](../../Documentation~/Gameplay.md)；通用后端见 [Excel 使用手册](../../Documentation~/README.md)。本目录不持有第二份 MiniExcel，也不把运行时持久化接入作者导入。

保留本目录的[许可证](LICENSE.md)与[依赖来源声明](Dependencies.json)，以及后端的 [Apache-2.0 许可证](../Licenses/MiniExcel-LICENSE.txt)和[署名](../Licenses/MiniExcel-ATTRIBUTION.txt)。由 Kano 维护，原始版权声明保留。
