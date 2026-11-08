# Gameplay 使用手册

Gameplay 包含 Skill、Buff、Mission、Equipment 四个独立领域。ScriptableObject 资产保存共享定义，Manager 创建或登记运行实例；领域逻辑不把可变状态写回源资产，也不自动接管背包、属性、奖励发放或存档。

## 前置与入口

安装主包和 `com.kano.ylframework.gameplay`，由 UPM 解析 Newtonsoft.Json。业务 asmdef 按实际使用引用下表的 Runtime 程序集；它们各自引用主包 Runtime，Buff / Mission 另显式引用 Json.NET，没有统一 Gameplay Runtime 总入口。

| 领域 | namespace / Runtime 程序集 | 配置根与入口 |
| --- | --- | --- |
| 技能 | `YLFramework.Skill` | `SkillsConfig`、`SkillManager` |
| Buff | `YLFramework.Buff` | `BuffsConfig`、`BuffManager` |
| 任务 | `YLFramework.Mission` | `MissionsConfig`、`MissionManager` |
| 装备 | `YLFramework.Equipment` | `EquipmentsConfig`、`EquipmentManager` |

所有作者工具属于 Editor-only 的 `YLFramework.Gameplay.Editor`，namespace 分别为 `YLFramework.Editor.Skill/Buff/Mission/Equipment`。普通作者流程不需要 Excel；四域配置也不继承 DataAssets 的根或条目。

## 最小用法

以 Mission 为例，在 Project Create 菜单 `YLFramework/MissionSystem(任务配置)` 创建根；打开 `Window/YLFramework/MissionSystem` 创建 ID 1 的条目，绑定 `YLFramework.Mission.MissionBase` 或其具体派生类。参数与条件按任务需要配置并保存，然后把根赋给以下脚本。此示例独占该根的登记责任，不与其他代码重复登记。

```csharp
using UnityEngine;
using YLFramework.Mission;

public sealed class MissionExample : MonoBehaviour
{
    [SerializeField] private MissionsConfig root;
    private bool registered;

    private void OnEnable()
    {
        MissionManager.AddConfig(root);
        registered = true;
        var mission = MissionManager.GetMission(root.name, 1);
        if (mission != null)
        {
            mission.State = MissionState.InProgress;
            Debug.Log($"任务状态={mission.State}，进度={mission.Progress}");
        }
    }

    private void OnDisable()
    {
        if (registered)
        {
            MissionManager.RemoveConfig(root.name);
            registered = false;
        }
    }
}
```

任务进入执行态时会立即检查已有条件；空条件不会自动完成。此例用直接引用借用资产，应用须让源根及其条目在登记期间存活。

## 配置资源与初始化

`AddConfig` 借用已经加载的根，不取得外部资产所有权。`LoadConfig(resources, address)` 同步取得根租约，由对应 Manager 持有到 `RemoveConfig` 或 `Shutdown`；登记失败会归还本次租约。

使用资源入口前由应用初始化 `ResourceSystem`，在 `ResourceProjectMount` 的 `assetTypes` 或本地目录类型清单中登记实际根及直接加载的具体资产类型。反射创建的技能、Buff、任务、装备 CLR 类型须为具体类型、满足各域构造要求，并由应用在 IL2CPP 中保留。资源类型清单不替代业务反射 / AOT 保留。

Manager 不返回 DataAssets 式的每消费者登记 token。配置登记和移除应由一个明确的应用所有者管理；宿主组件不能在自己退出时随意移除其他宿主共用的根。不同域的 ID 互不混合，Skill / Buff / Equipment 各自在全部已登记根之间要求 ID 唯一；Mission 的任务 ID 属于各根，参数按根内名称查询。

## Skill 技能

宿主实现 `IEquippableSkill`，稳定持有已初始化的 `Dictionary<int, SkillBase> Skills`，并实现 `CanRelease()`。配置条目 `skillClassFullName` 绑定有 public 无参构造的具体 `SkillBase` 派生类。

1. `SkillManager.AddSkills(host, id)` 分配池化运行技能和独立配置副本，挂接宿主并接入更新后调用 `OnAdd`；回调中移除或关闭可使返回值为 null。
2. 调用实例 `Release()`，检查 `ReleaseSkillStatus`，而非只看对象存在。它检查挂接、宿主许可、正在释放、冷却、自身条件和并行限制。
3. `RemoveSkills` 移除指定技能，宿主结束用 `CloseHost(host)`；源配置由登记方 `RemoveConfig(root)`，全域退出用 `Shutdown()`。

释放状态在回调前更新。持续时间达到后仍须 `IsComplete()` 允许才自然结束，冷却只在非释放状态推进。`IsCooldown == true` 表示冷却已完成，可释放；`ReleasingProgress` 在结束时清零，不能据它判断技能是否已经结束。

`Cancel()` 受 `ActiveCancellation` 限制，`Interrupt()` 强制中断且不发自然完成通知；两者不直接清除冷却。并行技能允许双方任一运行列表声明对方 ID。运行计时和取消设置由定义初始化，之后修改配置字段不自动同步这些运行值。

回调同步执行，异常会中断相应通知链；版本检查识别移除和池复用，不是任意重入隔离保证。批量添加 / 移除逐项执行，中途失败不承诺回滚已成功项。

## Buff 与效果

宿主实现 `IAddableBuff`，持有已初始化的 `List<BuffBase> Buffs`，提供 `CanAdd`、`OnAddBuff`、`OnRemoveBuff`。`BuffManager.AddBuff(host, id, userData)` 使用默认时长，也可明确传入本次 `duration`。

- `BuffMode.Single` 先移除宿主已有同 ID 实例，再添加新的，不是在原实例上刷新时间。
- `Additive` 保留多个同 ID 实例，分别计时，不自动合并为层数计数器。持续时间小于等于 0 时不按时间自动移除。
- 添加时复制定义及每项效果，重复效果引用仍保留为重复项。列表与效果索引先提交，再调用实例 `OnAdd`；仍有效时才通知宿主 `OnAddBuff`。
- 宿主或实例拒绝、宿主失效、回调中移除时可返回 null；同宿主同 ID 的嵌套添加抛错。回调失败会清理本域挂接，不撤销外部业务副作用。
- `ContainsEffect` 查询效果 ID，`GetEffects<T>` / `GetEffectsByType<T>` 先清空再写入调用方列表，按效果具体 CLR 类型、ID 或业务分类筛选，不复制或去重；引用只在所属 Buff 有效时使用。`EffectData` 只是定义，不自动执行增益。
- 用 `RemoveBuff` 或实例 `Remove()` 结束单项，宿主结束用 `CloseHost`，全域退出用 `Shutdown`。`userData` 的释放由业务自己负责。

`OnRemoveBuff` 期间实例已出列表但尚未归还，只在回调内读取本次数据，不能保留引用等待下一帧。效果与 Buff 的可变业务行为由实现方自行应用和撤销。

## Mission 任务与参数

`MissionManager.AddConfig` 复制任务定义、条件与参数配置，运行值和订阅独立于源根。根名 / 名称哈希、任务 ID、参数名称和条件引用都在登记阶段校验；失败清理本次运行态。`GetConfig` 返回源根，`GetMission` 返回运行实例。

`MissionBase.State` 为 `NotStarted/InProgress/Completed`。进入 `InProgress` 绑定条件监听并立即校验；非空条件全部满足才自动完成，多条件 `Progress` 是各条件进度的乘积，不是平均值。`rewards` 仅保存奖励定义，不执行发奖。

通过 `SetBool/SetInt/SetFloat`、`AddInt/AddFloat` 更新运行参数，用对应 getter 读取。Manager 执行 `onlySetInProgress` 和 `onlySetLarger` 约束；直接写 `MissionParameter.Value` 不检查这些策略，`SetValueWithoutNotify` 更不会重算条件或通知。

条件驱动完成时，`resetParamOnCompleted` 控制完成后对未被其他执行中任务引用的参数归零；直接将 `State` 设为 `Completed` 不执行这段条件完成重置。状态、进度和参数通知同步执行，业务必须避免让副作用重复触发。

Mission 没有宿主 `CloseHost`。`Reset(rootName)` 保留登记和租约，但重置任务状态、参数值、业务订阅和任务自定义数据，然后重绑条件；需要业务通知时重新订阅。`RemoveConfig(name/hash)` 释放根运行态和租约，`Shutdown()` 关闭全域。

## Equipment 装备

宿主实现 `IEquippable`，持有稳定且已初始化的 `List<IEquipment> Equipments` 和 `CanEquip`；运行装备实现 `IEquipment` 的定义、穿戴者与四个生命周期回调。

`EquipmentType.Asset` 由 Resource 实例化 Prefab，根节点必须恰有一个 `IEquipment` 组件；`Script` 使用有 public 无参构造、非抽象且不继承 `UnityEngine.Object` 的普通类。`projectName/assetName` 是装备 Prefab 的 ProjectId / Path，与图标地址独立。

1. `CreateEquipment(id)` 为自有实例赋共享 `EquipmentData`、空 `Equipper` 后调用 `OnLoaded`，不承诺早于 Prefab 的原生 `Awake/OnEnable`。
2. `Equip(host, equipment)` 返回 `EquipState`；同一实例已有挂载时返回 `AlreadyEquipped`，同 ID 的不同实例可以同时装备。`CreateAndEquip` 合并创建与尝试挂接。
3. `Unequip(equipment, false)` 卸下但保留可重装的自有实例；默认 `destroy == true` 会安排归还。最终用 `DestroyEquipment`、移除所属根或 `Shutdown` 归还保留实例。

Manager 创建的 Asset / Script 实例分别持有 `InstanceLease` 或 CLR 池责任。直接 `Equip` 外部对象只借用，不转移创建、销毁或回池责任，外部 owner 保活定义和资源；自有对象归还触发 `OnUnloaded`，外部对象只解除挂载。

回调失败会撤销本域挂载并清理，不回滚任意外部属性或背包副作用。归还后的原始 `IEquipment` 引用不得继续使用，没有跨 checkout 的公开 Handle 保护。

宿主退出用 `CloseHost`，关闭屏障期间拒绝回调重新装备到该宿主，延迟归还在最外层操作收尾时完成；完整收尾后仍可重新使用有效宿主。临时卸装可用 `Unequip/UnequipAll` 并自行决定保留或归还实例。源定义不保存耐久、冷却等实例状态。

## 作者工具与格式

窗口分别为 `Window/YLFramework/SkillSystem`、`Window/YLFramework/BuffSystem/Buff` / `Effect`、`Window/YLFramework/MissionSystem`、`Window/YLFramework/EquipmentSystem`。支持对应域的列表重排、字段 Undo 和资源预览；各工具按本域所有权删除自有条目，外部引用只解除关联，冲突不覆盖。

装备条目位于 `Assets/YLFrameworkData/Equipment/<根GUID>/`，显式刷新才补入本根条目，getter 不扫描或写盘。Buff 的项目设置精确位于：

```text
ProjectSettings/YLFramework/Core/YLFramework.Editor.Buff.BuffSystemSettings.asset
```

设置保存效果分类、资产登记和类型分组。缺失时创建当前设置，路径存在类型冲突、链接或无法唯一匹配实际类型的对象时拒绝，不覆盖未知输出。配置、效果资产、生成脚本及项目设置属于使用工程，卸载包不会代为删除；卸载前由应用清理自身类型和组件引用。

当前 Unity 字段采用各定义声明的 camelCase。工作簿和 JSON 另有显式格式协议，例如：

| 位置 | 当前协议 |
| --- | --- |
| 根的 Excel 列 | `startId → start_id`、`typeScriptGuid → type_script_guid` |
| Skill Excel | `skillName → skill_name`、`cooldownSeconds → cooling_time`、`simultaneousSkillIds → canbe_released_at_the_same_time`、`allowCancellation → active_cancellation` |
| Buff Excel | `id → buff_id`、`buffName → buff_name`、`buffClassFullName → buff_class_full_name`、`effectName → effect_name` |
| Mission Excel | `missionName → mission_name`、`missionTypeClassFullName → mission_type_class_fullname`、`resetParamOnCompleted → reset_param_on_completed` |
| Mission JSON / Excel | `paramName → param_name`、`onlySetInProgress → only_can_set_in_progress`、`onlySetLarger → only_can_set_larger`、`description → des`、`customData → custom_data` |
| Equipment Excel | `equipmentName → equipment_name`、`equipmentClassFullName → equipment_class_full_name` |
| Mission 剪贴板 | 条目 `YLFramework.Mission.Objects.v1:`、参数 `YLFramework.MissionSystem.CopyParamter` |

这些是当前格式，不应仅按 C# 字段名改写键或前缀。Excel 的具体表头、全量暂存校验和参数独立提交见所属包适配手册，本包不引入 MiniExcel。

## 生命周期、错误与限制

- 在 Unity 主线程访问资产、Manager 和宿主。Skill / Buff 管理器接入更新后按需收尾；宿主的集合通过 Manager 修改，不直接清列表绕过效果、回调与池责任。
- 宿主停用、销毁或场景结束时明确处理本方实例；普通 CLR 宿主必须显式调用关闭入口。`CloseHost` 不销毁宿主，完整收尾后可再次装备；Equipment 的回调屏障与延迟归还见上文。
- 系统退出分别调用自身 `Shutdown`，只归还本域的更新、池、回调和租约。关闭任意业务 Module 不等于关闭领域系统，也不应关闭应用共享的 `ResourceSystem` 来代替精确释放。
- 运行实例和回调引用仅在当前挂接 / checkout 有效，结束后不得保留作后续调用。退订业务监听与释放业务 `UserData` 仍由订阅者 / 创建者负责。
- 配置类型错误、ID 冲突、缺失参数、非法宿主或关闭期间新增责任会按各 API 合同拒绝。清理可能在尝试其余对象后汇总异常；记录并修复原因，不通过重写源定义掩盖失败。
- Resource Bundle 与 Catalog 使用实际类型全名和程序集身份，应由正式工具配套构建 / 发布；不要手改 Catalog 或摘要，也不能由一次 Editor 成功推断任意 SDK、平台和 Player 后端均已验证。

## 相关文档

- [包入口](../README.md)
- [Excel Gameplay 适配](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/Gameplay.md)
- [Resource 使用说明](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource.md)
- [独立 Save 手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.save/Documentation~/README.md)：应用自行选择 DTO 和持久化时机，不自动保存领域运行实例。
- [公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)与[框架快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)

跨包链接指向源码仓库当前文档。
