# 存档（Save）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

本页导航：[主要功能](#主要功能) · [基本用法](#基本用法) · [读取与恢复](#读取与恢复) · [序列化字典](#serializeddictionary-用法与边界) · [存储与并发](#存储与并发边界) · [验证与历史](#验证与历史)

## 作用

提供本地存档能力：管理多个存档槽（创建 / 删除 / 排序 / 命名 / 查询）、当前槽与 `ISaveModel`，按“存档槽 + 文件名”保存和读取可序列化对象，并提供全局设置、同步/异步与明确的读取来源。

**当前唯一引擎是 SaveStore，索引只支持 v2；不提供旧 YL 档案或外部档案迁移。** 单文件写入采用“flush 临时文件 → 原子替换 → 保留上一份备份”。损坏主件可从有效备份读取，但只有显式恢复才修盘。跨索引/目录操作不是全根多文件掉电事务，失败证据与 staging/tomb 保留，不承诺解决所有文件系统或超长路径问题。

## 位置

- 运行时：[`Runtime/Systems/Save/`](../../Runtime/Systems/Save/)
- namespace 为 `YLFramework.Save`，程序集仍为 `YLFramework.Runtime`；旧 `YLFramework.Services.Save` 调用方须更新 using/完整名，不提供转发壳。
- 唯一存储引擎：[`SaveStore.cs`](../../Runtime/Systems/Save/SaveStore.cs)；Unity 门面：[`SaveManager.cs`](../../Runtime/Systems/Save/SaveManager.cs)；模型/来源/版本合同：[`SaveContracts.cs`](../../Runtime/Systems/Save/SaveContracts.cs)。
- 文件读写辅助：`SaveFileIO.cs`（原子写入 / 备份恢复）；序列化辅助：[`Serialization/`](../../Runtime/Systems/Save/Serialization/)。
- 通用字典：[`Runtime/Foundation/Serialization/SerializedDictionary.cs`](../../Runtime/Foundation/Serialization/SerializedDictionary.cs)，归 Runtime 主体的 Foundation 职责，不是 Save 专属类型。
- 原生 Inspector：[`Editor/Foundation/Serialization/SerializedDictionaryDrawer.cs`](../../Editor/Foundation/Serialization/SerializedDictionaryDrawer.cs)，归 Editor-only 的 `YLFramework.Editor`。

## 主要功能

- 存档槽：`CreateSaveItem`、`GetAllSaveItems`（内置 / 自定义排序，含名称排序）、`GetSaveItemWithId` / `TryGetSaveItem` / `HasSaveItem` / `GetSaveItemCount`、`DeleteSaveItem` / `DeleteAllSaveItems` / `DeleteAll`。
- 槽位信息：`DisplayName` / `Note`（可选）、`CreateTime` / `LastSaveTime`，`RenameSaveItem` / `SetSaveItemNote`。
- 对象存取：`SaveObject` / `LoadObject<T>` / `TryLoadObject<T>` / `LoadObjectOrDefault<T>` / `DeleteObject` / `HasObject` / `GetObjectNames`（按文件名与存档 ID 重载）。
- 全局设置：`LoadGlobalSetting<T>` / `TryLoadGlobalSetting<T>` / `SaveGlobalSetting` / `DeleteAllSetting`。
- 模型：`CreateModel<T>` / `LoadModel<T>` / `SaveModel<T>` 及对应 Async、`LoadAllModelsAsync<T>`；`CurrentSaveId` 与普通对象槽共用索引和缓存。模型 `Id` 必须与非负槽号一致，创建参数错误抛 `ArgumentException`。C# 槽号使用 `SaveId`，v2 磁盘 JSON 保留 `SaveID/CurrentSaveID` 键。
- 读取与可靠性：`ReadObject<T>` / `ReadObjectAsync<T>` / `ReadGlobalSetting<T>` 返回 `SaveReadResult<T>`；备份恢复、来源与异常边界见 [读取与恢复](#读取与恢复)。
- 可测试 / 可替换存储：`StorageRoot` + `SetStorageRoot(path)`（默认 `Application.persistentDataPath`）；SaveManager 懒建引擎，业务对象按需读取。
- 序列化扩展：`SerializerSettings`（可添加 `JsonConverter`，仅作用于业务对象与全局设置）与 `SerializationFormat`；对象/设置使用裸 JSON。索引 `SaveIndex` 使用内部固定设置，不受用户 converter 影响。
- 不支持的索引版本抛 `SaveVersionException` 并保留原件；没有 `SaveMigration`、v1 复制、外部 Archive 导入、旧档扫描或自动清空坏档。`SetStorageRoot` 只切换路径，不复制或迁移数据；普通 `DeleteObject<T>` 重载也不是迁移工具。
- 序列化辅助类型：Foundation 的统一 `SerializedDictionary<TKey,TValue>`（标准字典接口及 Unity 回调）；Save 自有 `SerializedVector3` / `SerializedVector2`、`SerializedColor` 及对应扩展。

## 主要类型

- `SaveManager`：存档静态入口（含 `SaveItem`、`SaveOrder`）。
- `SaveStore`：BCL 文件引擎，显式构造、独占 root 并由调用方 `Dispose()`；没有第二套 ArchiveManager。
- `ISaveModel`：当前模型的 `Id` 合同；`SaveReadResult<T>`、`SaveReadStatus`、`SaveVersionException`、`SaveRecoveryRequiredException`：来源与错误合同。
- `SaveManager.SaveItem`：存档槽数据（编号、创建 / 最后保存时间、显示名、备注）。
- `SaveFileIO`（internal）：原子写入、备份恢复与 JSON 读写。
- `YLFramework.Serialization.SerializedDictionary<TKey,TValue>`：唯一通用字典；`YLFramework.Save` 不再定义另一份字典。
- `SerializedVector3` / `SerializedVector2` / `SerializedColor`：Unity 值的可序列化包装。

## 依赖

- `Newtonsoft.Json`（Unity 的 `com.unity.nuget.newtonsoft-json` 包）负责对象序列化，由 Runtime asmdef 的 `precompiledReferences` 显式引用；Unity 门面和序列化值包装还使用 UnityEngine。
- Foundation 字典自身不引用 Json.NET、Save/Core 或框架 Editor。Save 通过其 `IDictionary` contract 使用现有 Json.NET，不需 converter。

## 基本用法

以下是 Unity 主线程中的方法体片段（using 放在文件顶部），默认路径由 SaveManager 捕获。应用应按自己的 profile/root 管理槽位，不把测试写入真实用户目录。

```csharp
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Save;

var slot = SaveManager.CreateSaveItem();
SaveManager.RenameSaveItem(slot.SaveId, "进度一");
SaveManager.SaveObject(new Dictionary<string, int> { ["score"] = 100 }, "progress", slot.SaveId);

var result = SaveManager.ReadObject<Dictionary<string, int>>("progress", slot.SaveId);
Debug.Log($"{result.Status}, cached={result.IsCached}");
```

保存后读取可能直接命中同一对象缓存，不是磁盘往返验证。独立 owner 可用 `new SaveStore(rootPath)`，按相同槽号调用 `SaveObject/ReadObject`，结束时 Dispose；同一个 root 不能同时交给另一个 SaveStore 或 SaveManager。

普通对象默认槽号 0 不表示自动创建槽，必须先创建或确认存在。SaveManager 的同步 `SaveObject/LoadObject` 对不存在槽记录 Unity 错误并返回（读取为 null）；`ReadObject`、异步入口及 SaveStore 的需要槽位操作会校验并抛错。需要严格失败处理时，不把无返回值的同步门面调用视为写入成功证明。

主模型用 `CreateModel<T>(saveId)` 创建槽与 `ArchiveModel` 文件，用 `SaveModel` 保存；`LoadModel<T>()` 省略槽号时取 `CurrentSaveId`，没有当前槽会抛错。`LoadAllModelsAsync<T>` 按槽号排序并跳过无主模型的普通对象槽，不另建模型缓存。

## 读取与恢复

- `SaveReadResult<T>.Status` 区分 `Success`、`RecoveredFromBackup`、`Missing`；`IsCached` 表示是否命中缓存，备份来源会随缓存保留。`RecoveredFromBackup` 只说明采用备份值，不表示磁盘已修复。
- 主件和备份都不存在才是 Missing。主件读取失败时会尝试备份，仍无有效结果则抛错；内部 `Failed` 状态在公开读取边界转为异常，IO/权限错误也不会静默当作缺失。`TryLoadObject/TryLoadGlobalSetting` 不吞这些错误，不能把它们当作通用安全回退。
- `LoadGlobalSetting<T>` 仅文件缺失时创建并写入 `new T()`；`TryLoadGlobalSetting` 和 `ReadGlobalSetting` 不自动创建。坏文件不会被默认设置覆盖。
- `RestoreObjectBackup` / `RestoreSettingBackup` / `RestoreIndexBackup` 是显式修盘入口，保留 `.corrupt-*` 原件和原 `.bak`，返回保留路径。索引从备份打开时 `IndexNeedsRecovery` 为 true，需显式恢复后才能进行受保护的索引/槽位写入；健康索引不能用该入口任意回滚。
- 重命名或修改备注使用 `RenameSaveItem/SetSaveItemNote` 持久化，失败回滚内存；直接改 SaveItem 只影响内存。对象文件写成功后才更新该槽的保存时间。

## SerializedDictionary 用法与边界

```csharp
using UnityEngine;
using YLFramework.Serialization;

public class ExampleConfig : MonoBehaviour
{
    [SerializeField] private SerializedDictionary<string, int> scores = new SerializedDictionary<string, int>();

    public void SetScore(string id, int score)
    {
        scores[id] = score;
    }

    public bool TryReadScore(string id, out int score) => scores.TryGetValue(id, out score);
}
```

直接泛型字段可用，也可声明 `[Serializable]` 的具体非泛型子类；字段所属业务 asmdef 引用 `YLFramework.Runtime`。无需挂载字典组件或手动 Refresh。

- 运行时：标准 `IDictionary` / `IReadOnlyDictionary`，缺键索引器抛 `KeyNotFoundException`，`Add` 拒绝重复，`TryAdd` 返回是否新增；只拒绝 CLR null 键，允许空字符串及 default/null 值。来源构造复制条目，不借用外部字典；Keys/Values 是只读实时视图。
- Unity：`datas/Key/Value` 原始条目是事实源，修改写穿列表；原生 Inspector 展示条目、计数及重复行号，支持字段编辑、单对象增删/重排、Undo/Redo 与 Prefab override/apply/revert。只有首个有效键参与运行时，重复/无效原始行不自动删除；修复 Key 后即可重新有效。`Remove(key)` 删除全部同键行，`Clear()` 包括无效行。
- 多对象：逐目标诊断，保留 mixed-value 字段编辑；禁止所有批量列表增删/重排，长度不同或字段缺失时禁止整组字段编辑，须单独选择目标修复。相同位置的值编辑不复制首个目标的键。
- 类型限制：Key/Value 必须符合 Unity 字段序列化规则；Drawer 不为任意泛型键、固定缓冲、嵌套容器、多态或 alias 建立自定义 serializer。Unity 无法通过 `boxedValue` 诊断的键会明确提示，而非按 ToString 判断重复。Unity inline 序列化不保证任意 null 条目保真。
- 对象键：不把 Unity fake-null 当 CLR null 自动清除。原生 `SerializedProperty` 的 None/Missing 无法可靠区分二者，Inspector 标为“待确认”，不虚报精确有效数；跨会话持久化建议使用稳定 ID，不以 Unity 对象引用作 JSON 键。
- UI：同时提供 UI Toolkit `PropertyField`/binding 和 IMGUI 实现，供原生 GenericInspector 与 IMGUI 自定义 Inspector 使用；长诊断在固定高区域内滚动。无需第三方 Inspector，也不承诺第三方抢占的 Editor 会转发原生 Drawer。Sirenix 全局 Inspector 已退出，其他组件回落 Unity 原生或既有 CustomEditor。
- JSON：当前 SaveManager/Json.NET 输出裸对象，例如 `{"a":2,"b":4}`，只保存有效字典投影；不导出 Unity 原始无效行。支持该后端可往返的 string/int/enum 键及相应值，复杂键的 ToString 不代表可逆编码。自定义 converter/resolver、元数据保留名称（`$type/$id/$ref`）、Unity 对象引用与任意保引用设置需调用方另验实际磁盘往返。
- 缓存：`SaveObject(dictionary, "scores", slot.SaveId)` 可直接写盘；随后 `LoadObject<SerializedDictionary<string,int>>("scores", slot.SaveId)` 可能命中同一内存对象，不是磁盘验证。隔离测试才使用独占 root、清缓存及重新读盘，不在真实用户目录为测试切换或清除存档状态。
- 兼容边界：唯一容器是 `YLFramework.Serialization.SerializedDictionary`，位于 `YLFramework.Runtime`，保留脚本 GUID 与 `datas/Key/Value`。旧 Core、Save 专属字典及原 Foundation namespace 调用须更新，没有 facade 或旧双列表 reader。GUID/字段保留不保证旧 AQN、SerializeReference 或旧 JSON 包装兼容；持久身份须按真实样本映射并重导入/往返验证。这不提供旧存档格式转换。

<a id="备注"></a>

## 存储与并发边界

- 存档与设置默认写入 `Application.persistentDataPath/SaveData` 与 `/Setting`；当前索引为 `SaveData/SaveSystemData.json`。没有路径回退或旧格式猜测。
- 禁止用 `TypeNameHandling` 从 JSON 实例化 CLR 类型。并发前配置好 `SerializerSettings`；同步反序列化留在调用线程，异步 DTO/Converter 反序列化在线程池执行，不能调用 Unity API 或重入同 owner。Converter 必须可并发，应用负责 DTO 与反射成员的 IL2CPP 保留。
- 文件名（`saveFileName` / 全局设置 `fileName`）不允许包含路径分隔符、`..`、非法字符、末尾点 / 空格、Windows 保留设备名（CON / PRN / AUX / NUL / COMx / LPTx）或超过 200 字符，非法时抛 `ArgumentException`；不支持子目录。
- 同 root 排他 owner、同槽串行、跨槽可并发；`Dispose/Close/SetStorageRoot` 取消排队并排空旧 owner。缓存返回原实例，调用方负责该对象的并发修改。删除为 `.deleted-*` 软删除，不是安全擦除，也不自动清理。
- 异步保存先在调用线程序列化快照，再异步 IO；取消在单文件原子提交点前有效，提交后报告成功，不把已提交写入改报取消。默认 Unity 路径先在主线程捕获，工作线程可用显式 root。
- 编辑器静态构造不自动初始化，首次 API 调用才懒加载；编辑器工具仍需显式选择隔离 root，不能因懒加载就认为不会访问真实目录。

<a id="当前状态"></a>

## 验证与历史

测试入口为 `Tests/EditMode/Systems/Save/SaveStoreSafetyTests.cs`、`SaveManagerFacadeTests.cs`、`SerializedDictionarySaveTests.cs`；字典原生编辑另见 `Tests/EditMode/Editor/Foundation/Serialization/SerializedDictionaryEditorTests.cs`。覆盖涉及槽位/模型、版本拒绝、文件安全、取消并发、缓存来源/显式恢复、Prefab 保存重开与隔离磁盘往返；字典专题不能替代 Save 全系统故障恢复验收。

集成与历史验证导航见 [融合记录](../IntegrationPlan.md)，详细接收与字典记录保留在 Git 历史中，不是导出包运行依赖。旧格式迁移结果不作为当前 v2 合同或新运行证明。
