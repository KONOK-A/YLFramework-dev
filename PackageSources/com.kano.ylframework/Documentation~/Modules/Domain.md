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

Skill/Buff/Mission/Equipment安装 [Gameplay包](../../../com.kano.ylframework.gameplay/README.md)，分别属于`YLFramework.Skill`、`YLFramework.Buff`、`YLFramework.Mission`、`YLFramework.Equipment`四Runtime，源码在该包的`Runtime/各域`，配套作者工具与共享退出逻辑属于`YLFramework.Gameplay.Editor`（`Editor/各域`及`Editor/Shared`）。Reddot独立安装[红点包](../../../com.kano.ylframework.reddot/README.md)，原namespace保持，程序集改为`YLFramework.RedDots`，consumer asmdef显式引用；Logging仍位于主包`Runtime/Systems`。Gameplay产品不依赖红点。

## 配置与资源

Skill/Buff/Mission/Equipment 当前字段采用已声明的 camelCase，旧 Unity 字段 reader 已退役；`ExcelColumnName`、任务 `JsonProperty` 和剪贴板前缀属于仍有效的独立协议，不随字段清理改键。当前根/条目/参数/效果由 Unity 正式保存后冷读，脚本与资产 GUID、false/zero、父子/效果引用保留；真实旧字段输入须在原版本仍可读时先转换自己的资产，不能重写冻结样本或改名壳掩盖丢值。

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

数值聚合、嵌套Batch与订阅、显式`ReddotReadState`导入导出、可选`YLFramework.RedDots.Save`以及只读诊断见[红点包说明](../../../com.kano.ylframework.reddot/README.md)。核心不自动持久化、恢复或保存槽位；来源凭据与已读状态都有明确owner，不因安装Save而自动写盘。

## Logging

有效 Play 会话的主线程中显式调用 `LoggerManager.Enable()` 才安装处理器和宿主。文件输出另外由 `new LoggerOptions { WriteToFile = true }` 启用；未提供目录时，Manager 使用 `Application.persistentDataPath/YLFramework/Logs`。

- 重复启用返回当前会话并忽略新配置；更换配置先 Disable。
- 关闭只在 Unity 仍使用本方 handler 时恢复先前 handler，不覆盖其他代码后来安装的处理器。
- 读取 `IsEnabled` / `Current` 不创建宿主。ConsoleFilter 与 FileFilter 独立，null 表示全开，空表表示全关。
- 文件输出需要应用审慎选择路径和日志内容；框架默认不写盘，不接管外部日志包或原游戏 Logger。

## 作者工具

窗口为 `Window/YLFramework/SkillSystem`、`Window/YLFramework/BuffSystem/Buff` / `Effect`、`Window/YLFramework/MissionSystem`。支持重排、Undo 与资源预览；条目只删除自己拥有的子资产或根 GUID 目录，外部引用仅解除关联。

### Buff 当前设置

当前设置只按实际类型全名访问下列精确路径，不扫描、读取或移动旧名称及未知外部设置：

```text
ProjectSettings/YLFramework/Core/YLFramework.Editor.Buff.BuffSystemSettings.asset
```

一次性 BuffSettingsMigration、旧名称标注及加载器旧路径分支已退役，不提供迁移菜单或换名壳。缺失时正常创建当前设置；当前文件不是唯一且类型完全匹配的对象、或路径包含链接/类型冲突时拒绝，不覆盖未知输出。已有旧工程升级前须在旧定义仍可用时定向保存/保全自己的实际数据；本仓库当前设置目录为空，未创建假旧文件来代替真实迁移，也未接管旧外部根。

## DomainExcel

领域适配已包含在`com.kano.ylframework.excel/Editor/Gameplay`，同时安装Gameplay后由包Version Define启用原`YLFramework.Domain.Excel.Editor`，不再安装独立DomainExcel包。Git消费方显式声明main、DataAssets、Excel、Gameplay四项同SHA。Excel与Gameplay产品不互相依赖，不要求或改写任何全局宏；已有业务`#if YL_DOMAIN_EXCEL`仍由使用工程自行审查，不会自动变成包存在性判断。

- `DomainExcelCommands.PreviewImport` 接受显式工作簿路径，预览后确认或取消；不提供 Mission 条目与参数跨 sheet 原子事务，也不导入全局 Buff Effect 库。
- 全部类型化暂存行通过现有 afterImport 规范化与校验，不从展示差异的 JSON 反解析领域对象。未变化的非法行同样阻止提交；行错误进入原计划错误列表，根/外部引用校验通过既有 AddValidation 在预览与实际写入前各执行，不在预览改资源。
- Skill / Buff / Mission 没有最近工作簿入口。Equipment 的导出、预览 / 确认 / 取消及最近工作簿目录菜单为 `Assets/YLFramework/Domain Excel/`；这是 Project 右键菜单名，不是安装或输出目录。
- Equipment 工作簿路径按根 GUID 存 EditorPrefs，不进资产；暂存阶段同步 Script 类 / GUID，跨根 ID 在预览和提交前复核，失败不部分写入。
- 参数根的 Clean/引用复核属于参数计划本身，即使不通过 UI 提交也生效。菜单复用公共 ExcelPreviewWindow，Applied 后才保存/刷新根；列表、JSON 和 SO 仍各自负责提交与恢复，不保留第二套参数预览窗口。

表头、类型映射和提交边界见 [Excel 作者后端](Data.md#excel-作者后端)。
