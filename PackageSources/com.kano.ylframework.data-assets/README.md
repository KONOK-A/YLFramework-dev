# DataAssets 配置数据

`com.kano.ylframework.data-assets` 提供 ScriptableObject 配置根、按 ID / 名称查询的条目、显式资源租约和 Unity 作者工具。配置是共享定义，不是每个业务实例的可变状态。

## 依赖与接入

依赖主包 `com.kano.ylframework`。从当前源码以本地 UPM 接入时，显式提供主包和本包；尚未发布的兄弟包不会由 Registry 自动取得。业务 asmdef 引用 `YLFramework.DataAssets`，作者脚本另引用 Editor-only 的 `YLFramework.DataAssets.Editor`。

安装步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，框架入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档。

## 开始使用

1. 在 `Assets/` 下通过 `YLFramework/Data Assets/Configuration Root` 创建根。
2. 打开 `Window/YLFramework/Data Assets`，选择根并创建条目。
3. 用 `ScriptableObjectManager.AddConfig(root)` 登记已加载根，持有并在使用结束时 `Dispose()` 返回的登记句柄。

完整示例、资源加载、字段协议与所有权见[使用手册](Documentation~/README.md)。工作簿导入导出另由可选的 [Excel 包](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/README.md)提供；不安装 Excel 也可使用配置和普通 Inspector。

## 许可证

本包采用 [MIT 许可证](LICENSE.md)。
