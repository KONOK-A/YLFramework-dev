# Gameplay 领域系统

`com.kano.ylframework.gameplay` 提供 Skill、Buff、Mission、Equipment 四个独立运行时和配套作者工具。各域维护自己的定义与运行实例，不是统一业务 Module，也不自动联动背包、发奖或存档。

## 依赖与接入

依赖主包 `com.kano.ylframework` 与 `com.unity.nuget.newtonsoft-json`，不依赖 DataAssets、Localization、Excel 或 Save。业务 asmdef 按需引用 `YLFramework.Skill`、`YLFramework.Buff`、`YLFramework.Mission`、`YLFramework.Equipment`；共享作者工具属于 Editor-only 的 `YLFramework.Gameplay.Editor`。

当前源码接入显式提供主包和本包，步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，基础入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档。

## 开始使用

创建对应的 `SkillsConfig/BuffsConfig/MissionsConfig/EquipmentsConfig`，通过各域作者窗口配置条目。`AddConfig` 借用已经加载的根；冷资源用 `await Manager.LoadConfigAsync(resources, address)`，同步 `LoadConfig` 仅用于 YL 已持有的热资源，未就绪抛 `ResourceException`，其 `Kind` 为 `ResourceError.InProgress`。`new ResourceAddress("game", "config/skills")` 对应官方 Addressables 地址 `game/config/skills`，无需挂载私有目录。

每个 Manager 独立持有自己取得的配置根 `AssetLease`，条目由本域借读。Skill / Buff / Equipment 宿主退出先 `CloseHost` 收尾自身实例，配置登记方再移除自己的根；全域退出才调用对应 `Shutdown()`，最后由应用停止共享 Resource。关闭 Resource Scope 不等于清除业务登记，也不能靠停 Resource 强收其他消费者的租约。

Asset 装备的冷路径使用 `CreateEquipmentAsync(id)` 或 `CreateAndEquipAsync(host, id)`，内部先异步取得 Prefab，再 `Rent`，保留实例复用语义；Script 装备及已加载 Prefab 仍可用同步入口。加载、配表和组件错误会明确失败，不被吞成 `null`；业务许可拒绝仍按原合同返回 `null`。

四域用法、字段协议、回调顺序和关闭边界见[使用手册](Documentation~/README.md)。需要工作簿时另装 Excel，按[领域适配指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/Gameplay.md)接入。

## 许可证

本包采用 [MIT 许可证](LICENSE.md)。
