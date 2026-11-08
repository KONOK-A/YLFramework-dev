# Gameplay 工作簿

Excel 包内置 Skill、Buff、Mission、Equipment 的 Editor 适配，将工作簿导入连接到各域的配置作者流程；它只修改定义资产，不创建或刷新运行实例。

## 前置与启用

显式安装主包、DataAssets、Excel 和 Gameplay，使用同一源码修订。本地 / Git 来源须为全部自有包提供地址，完整步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)。

适配位于本包 `Editor/Gameplay/`，程序集为 `YLFramework.Domain.Excel.Editor`，namespace 为 `YLFramework.Integrations.DomainExcel.Editor`。它通过 `com.kano.ylframework.gameplay` 的程序集局部 Version Define `YL_GAMEPLAY_PACKAGE` 启用，不要求或修改全局 scripting symbol。

Excel 没有 Gameplay 产品依赖：不装 Gameplay 仍可使用通用 SO / List / JSON 与 DataAssets 工作簿；Gameplay 不装 Excel 也可正常使用运行时及普通作者工具。自定义适配代码只能放在 Editor-only 程序集中，并显式引用本适配及用到的领域程序集。

## 最小流程

1. 退出 Play，选择已保存且无未保存改动的领域根，或该根的条目。先用各域普通作者工具完成类型绑定和基础配置。
2. 使用 Project 右键 `Assets/YLFramework/Domain Excel/Export Entries` 导出工作簿，按需要编辑下面表中的工作表。
3. 使用 `Preview Import Entries` 选择明确的 `.xlsx` 路径，检查错误和新增 / 更新 / 删除差异，再在公共预览窗口确认或取消。
4. 仅 `Applied` 后执行菜单接线的缓存刷新；Equipment 同时记录最近工作簿路径。运行时重新登记按应用生命周期完成，不由作者导入隐式接管。

| 根 | 工作表 | 主要校验 |
| --- | --- | --- |
| `SkillsConfig` | `skills` | `SkillBase` 具体类型可解析，具备 public 无参构造 |
| `BuffsConfig` | `buffs` | `BuffBase` 类型及真实 `EffectData` 引用 |
| `MissionsConfig` | `missions` | `MissionBase` 类型，条件数组引用根中已存在的参数 |
| `EquipmentsConfig` | `equipments` | Script 装备类型或 Asset 资源地址，跨根 ID 唯一 |

上述路径是 Project 菜单，不是包安装或生成目录。导出替换整个目标工作簿，不保留其他 sheet 或格式。

## API 与校验责任

`DomainExcelCommands.PreviewImport(root, path)` 为四类根分别提供类型化重载，返回须确认、取消或释放的 `ExcelImportPlan`。Editor 方法体可用：

```csharp
using YLFramework.Integrations.DomainExcel.Editor;
using YLFramework.Integrations.Excel.Editor;

// root 为已保存且干净的领域根，path 由调用方明确选择。
var plan = DomainExcelCommands.PreviewImport(root, path);
ExcelAuthoringCommands.ShowPreview(plan);
```

根不合法或领域外部约束错误可能直接抛出，不能假设所有错误都只在 `plan.Errors`。全部类型化暂存行通过 `afterImport` 规范化和校验，未变化的非法行也会阻止提交；显示差异中的 JSON 不是领域对象传输格式。

`AddValidation` 在预览及写入前复核根状态、类型、效果 / 参数引用和跨根 ID，不在预览期间写资源，也不在提交后修补。脚本可解析或地址合法只表示定义通过该阶段校验，不表示实际 Prefab 已加载、组件完整或目标 Player 已保留类型。

## Buff 与 Equipment

Buff 的效果列通过领域转换器解析为真实 `EffectData` 引用。效果资产在提交前再次核对；本适配不导入或创建全局 Buff Effect 库。

Equipment 在暂存阶段同步 Script 类型及 `equipmentClassGuid`。Script 模式要求普通、具体且有 public 无参构造的 `IEquipment` 实现；Asset 模式校验 `projectName/assetName` 可组成 `ResourceAddress`，不在导入时加载 Prefab 或检查其 `IEquipment` 组件。

Equipment 采用本根独占目录 `Assets/YLFrameworkData/Equipment/<根GUID>/`，即使目录尚不存在也不回退到不明确的输出；预览 / 取消不创建该目录。跨根 ID 在预览和提交前复核，失败不作为部分导入成功。

`ExportEquipment(root, path)` 导出并记录路径；`GetEquipmentWorkbook(root)` 读取本机记录，键为 `YLFramework.Equipment.Excel.<根GUID>`。成功菜单导入同样记住路径；`Open Equipment Workbook Folder` 仅在文件存在时启用。Skill、Buff、Mission 没有最近工作簿入口，路径也不存入根资产。

## Mission 参数

任务条目与参数使用独立工作簿、独立提交，没有跨 sheet 的原子导入。推荐先准备参数，再导入引用它们的任务条件；不能把两次覆盖同一工作簿当作合并导出。

- `ExportParameters(values, path)` 输出单独的 `params` 工作表，建议使用不同的 `.params.xlsx` 文件。
- Project 菜单 `Export Mission Parameters` / `Preview Import Mission Parameters` 为选中根操作参数，复用公共 `ExcelPreviewWindow`。
- 根感知参数计划在预览和提交前检查根为干净状态，以及变更后所有任务条件仍能找到参数；改名或删除会破坏引用时，先通过普通作者工具同步条件。
- 只有 `Applied` 后菜单才保存根并 `ClearCache()`。计划自身承担只读约束，不依赖窗口来维持提交前检查。
- public 的 `PreviewParameters(path, List<MissionParameter>)` 是通用列表计划，不知道所属根，不检查任务条件、不保存根、不刷新 Manager；自定义调用者承担这些额外责任，不能把该重载当成根感知事务。

参数 `name` 是稳定行键。当前 JSON / Excel 协议中的 `param_name/only_can_set_in_progress/only_can_set_larger/des/custom_data` 按显式声明解释，不从 C# 字段拼写推断。

## 错误、所有权与限制

调用方或预览窗口拥有一次性计划，关闭或取消释放暂存对象；确认后检查实际 `Result/Errors`。预览过期、引用变化或验证失败时重新生成计划，不跳过约束重试提交。

SO / List / JSON 的提交和恢复边界分别由[通用 Excel 手册](README.md#错误恢复与限制)说明。SO 失败会尝试恢复本次文件和内存状态，回滚不完整保留恢复目录；不承诺多工作簿事务、通用 Undo 或回滚任意业务副作用。

## 相关文档

- [Excel 使用手册](README.md)
- [Gameplay 使用手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.gameplay/Documentation~/README.md)
- [适配源码与许可入口](../Editor/Gameplay/README.md)

跨包链接指向源码仓库当前文档。
