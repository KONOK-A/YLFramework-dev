# 领域系统

> [文档首页](../README.md) · [架构](../Architecture.md) · [配置数据](Data.md) · [Resource](Resource.md)

Skill、Buff、Mission、Equipment、Reddot 和 Logging 各自管理领域状态。它们不是同一个业务 Module 的隐式子系统，也不以配置资产保存运行实例状态。

## 正式入口

| 系统 | namespace / 入口 | 责任边界 |
| --- | --- | --- |
| Skill | `YLFramework.Skill.SkillManager` | 技能配置、宿主与运行实例 |
| Buff | `YLFramework.Buff.BuffManager` | Buff 定义、效果与宿主状态 |
| Mission | `YLFramework.Mission.MissionManager` | 任务与参数运行态，不使用宿主 CloseHost 协议 |
| Equipment | `YLFramework.Equipment.EquipmentManager` | 装备创建、单一挂载归属与归还，不管理背包或存档 |
| Reddot | `YLFramework.RedDots.ReddotManager` | 路径与独立来源登记，表现组件只控制自己的 Indicator |
| Logging | `YLFramework.Logging.LoggerManager` | 显式日志会话，默认不接管处理器或写文件 |

实现位于 [`Runtime/Systems/`](../../Runtime/Systems/)，配套作者工具位于 [`Editor/Systems/`](../../Editor/Systems/)。

## 配置与资源

Skill / Buff / Mission / Equipment 使用各自的 `SkillsConfig`、`BuffsConfig`、`MissionsConfig`、`EquipmentsConfig`，不依赖 DataAssets 基类。

- `AddConfig` 借用已经加载的定义，外部 owner 负责保活。
- `LoadConfig(ResourceSystem, ResourceAddress)` 由相应 Manager 持有根 lease；应用须先初始化资源并显式登记根类型与直接加载的具体派生类型。
- 配置是共享只读定义。技能、效果、任务和装备的可变状态放在运行实例，不能写回共享 ScriptableObject。
- Script 绑定的派生 CLR 类型须为目标 IL2CPP 路径定向保留；登记根类型不等于保留全部反射子类。

## 宿主与关闭

| 系统 | 结束方式 |
| --- | --- |
| Skill / Buff | 宿主结束调用对应 `CloseHost`，系统退出调用自身 `Shutdown` |
| Mission | 按配置使用 `Reset`、`RemoveConfig` 或 `Shutdown` 管理任务与参数 |
| Equipment | 宿主结束调用 `CloseHost`；自有实例最终归还，外部对象只解除挂载 |
| Reddot | 登记者 Dispose 来源 token，组件停用 / 销毁时退订 |
| Logging | `LoggerManager.Disable()` 结束本方会话 |

各系统只归还自己持有的更新、池、回调和 lease。关闭任意 Module 不等于结束外部领域系统，也不应由领域 Manager 关闭应用共享的 ResourceSystem。

## Equipment

`IEquipment` 是运行装备，`IEquippable` 是宿主。Asset / Script 两种创建方式共用单一挂载归属，不自动联动 Skill、Buff、背包或存档。

- `CreateEquipment(id)` 设置共享 `EquipmentData` 和空 `Equipper` 后触发 `OnLoaded`，不承诺早于 Prefab 原生 Awake / OnEnable。Prefab 根须有且仅有一个 IEquipment；Script 须为具体普通类且具有 public 无参构造。
- 同一实例跨宿主返回 AlreadyEquipped，同 ID 的不同实例可同时装备。回调失败会撤销本领域挂载并清理，不回滚任意外部业务副作用。
- Manager 创建的对象由其持有 InstanceLease 或 CLR 池责任；直接 `Equip` 外部对象只借用，外部 owner 自行保活配置与资源，Manager 不代替外部创建、销毁或回池。
- `Unequip(equipment, false)` 保留可重装的自有实例，最终使用 `DestroyEquipment`、`RemoveConfig` 或 `Shutdown` 归还。归还后的原始 IEquipment 引用不可继续使用，没有跨 checkout 的公开 Handle 保护。

作者窗口为 `Window/YLFramework/EquipmentSystem`，Create 菜单为 `YLFramework/EquipmentSystem(装备配置)`。独立条目位于 `Assets/YLFrameworkData/Equipment/<根GUID>/`；显式刷新才补入本根条目，getter 不扫描或写盘。自有资产支持 Undo / Redo，外部引用只解除关联，冲突不覆盖。

## Reddot

`ReddotManager.RegisterPath` 返回独立来源 token，由 owner 退出时 Dispose。普通 `AddPath/RemovePath` 保留集合语义，不取得其他来源的登记权。

`Reddot` 组件只切换明确绑定的 `Indicator`，停用或销毁时精确退订，不遍历切换全部业务子节点。

## Logging

有效 Play 会话的主线程中显式调用 `LoggerManager.Enable()` 才安装处理器和宿主。文件输出另外由 `new LoggerOptions { WriteToFile = true }` 启用；未提供目录时，Manager 使用 `Application.persistentDataPath/YLFramework/Logs`。

- 重复启用返回当前会话并忽略新配置；更换配置先 Disable。
- 关闭只在 Unity 仍使用本方 handler 时恢复先前 handler，不覆盖其他代码后来安装的处理器。
- 读取 `IsEnabled` / `Current` 不创建宿主。ConsoleFilter 与 FileFilter 独立，null 表示全开，空表表示全关。
- 文件输出需要应用审慎选择路径和日志内容；框架默认不写盘，不接管外部日志包或原游戏 Logger。

## 作者工具

窗口为 `Window/YLFramework/SkillSystem`、`Window/YLFramework/BuffSystem/Buff` / `Effect`、`Window/YLFramework/MissionSystem`。支持重排、Undo 与资源预览；条目只删除自己拥有的子资产或根 GUID 目录，外部引用仅解除关联。

### Buff 设置迁移

默认加载器遇到精确旧路径时拒绝写入，使用 `YLFramework/Buff/Migrate Legacy Project Settings` 显式迁移：

```text
旧：ProjectSettings/YLFramework/Core/YLFramework.BuffSystem.BuffSystemSettings.asset
新：ProjectSettings/YLFramework/Core/YLFramework.Editor.Buff.BuffSystemSettings.asset
```

迁移先验证类型、备份原始字节，再不覆盖地移动与重载；不改写 YAML 或认领外部框架设置。新旧并存、类型错误或路径冲突会拒绝；中途失败保留现场，按报错路径处理，不自动覆盖或删除。使用真实旧资产验证，合成夹具不代表所有外部数据已迁移。

## DomainExcel

可选 `Integrations/DomainExcel` 需要完整 Excel 后端与 `YL_DOMAIN_EXCEL`，仅用于领域作者流程，不成为 Runtime 依赖。

- `DomainExcelCommands.PreviewImport` 接受显式工作簿路径，预览后确认或取消；不提供 Mission 条目与参数跨 sheet 原子事务，也不导入全局 Buff Effect 库。
- Skill / Buff / Mission 没有最近工作簿入口。Equipment 的导出、预览 / 确认 / 取消及最近工作簿目录菜单位于 `Assets/YLFramework/Domain Excel/`。
- Equipment 工作簿路径按根 GUID 存 EditorPrefs，不进资产；暂存阶段同步 Script 类 / GUID，跨根 ID 在预览和提交前复核，失败不部分写入。

表头、类型映射和提交边界见 [Excel 作者后端](Data.md#excel-作者后端)。
