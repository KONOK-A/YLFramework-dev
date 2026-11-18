# DataAssets 配置数据

`com.kano.ylframework.data-assets` 提供 ScriptableObject 配置根、按 ID / 名称查询的条目、显式资源租约和 Unity 作者工具。配置是共享定义，不是每个业务实例的可变状态。

## 依赖与接入

依赖主包 `com.kano.ylframework`。从当前源码以本地 UPM 接入时，显式提供主包和本包；尚未发布的兄弟包不会由 Registry 自动取得。业务 asmdef 引用 `YLFramework.DataAssets`，作者脚本另引用 Editor-only 的 `YLFramework.DataAssets.Editor`。

安装步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，框架入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档。

## 开始使用

1. 在 `Assets/` 下通过 `YLFramework/Data Assets/Configuration Root` 创建根。
2. 打开 `Window/YLFramework/Data Assets`，选择根并创建条目。
3. 用 `ScriptableObjectManager.AddConfig(root)` 登记已加载根，持有并在使用结束时 `Dispose()` 返回的登记句柄。

按地址读取冷资源时使用 `ScriptableObjectManager.LoadAsync(resources, new ResourceAddress("game", "config/items"))`，在协程中等待返回的 `ScriptableObjectAsset<ScriptableObjectConfigs>`，确认成功后借读。官方 Addressables 地址为 `game/config/items`；应用先启动并持有共享 `ResourceSystem`，本包不挂载另一套目录。

同步 `Load` 只增持 YL 已持有的热资源，未就绪明确抛 `ResourceException`，其 `Kind` 为 `ResourceError.InProgress`，不会阻塞等待或返回假成功。加载 owner 独立持有根租约与登记，条目只是借读；使用结束先停止业务借读，再 `Dispose()` 自己的 owner。应用级 `Clear()` 关闭本域所有登记及加载，但不关闭 Resource，也不能用关闭 Resource Scope 代替注销业务登记。

完整示例、资源加载、字段协议与所有权见[使用手册](Documentation~/README.md)。工作簿导入导出另由可选的 [Excel 包](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/README.md)提供；不安装 Excel 也可使用配置和普通 Inspector。

## 许可证

本包采用 [MIT 许可证](LICENSE.md)。
