# Excel 使用手册

Excel 包把 `.xlsx` 作为作者输入：先解析和校验为一次性预览计划，再由用户明确确认写入。支持 ScriptableObject 定义资产、普通 List 与 JSON 输出，三种后端的所有权和恢复能力不同。

## 前置与程序集

安装主包、DataAssets 和 `com.kano.ylframework.excel`；由 UPM 解析 Newtonsoft.Json。随包 MiniExcel 1.31.2 只在 Editor 使用，不另行下载或复制 DLL。

作者脚本放在使用工程的 Editor 目录或 Editor-only asmdef 中，引用 `YLFramework.Excel.Editor`，使用 `YLFramework.Integrations.Excel.Editor` namespace。需要直接操作 DataAssets 根时再引用 `YLFramework.DataAssets`，调用作者 API 时引用 `YLFramework.DataAssets.Editor`。本包没有 Runtime xlsx IO；主包 `YLFramework.Excel` 仅定义字段 Attribute、转换器基类与 `ExcelFieldsType`。

## 最小作者流程

1. 在使用工程创建并保存 DataAssets 根，使用 DataAssets 工具创建条目、修复诊断；根和条目不能有未保存改动。
2. 打开 `YLFramework/Excel/Data Assets Workbook`，明确选择 `Data Root`，点击 `Export Workbook` 选择输出文件。父目录必须存在。
3. 编辑导出的 `.xlsx`，保留三行表头和稳定 ID。导出会替换整个目标工作簿，包括其他工作表与格式，覆盖前先确认。
4. 选择已有工作簿后点击 `Preview Import`，检查 `Errors`、新增 / 更新 / 删除差异，再确认提交；关闭窗口或取消均不写入目标。
5. 确认成功后检查资产字段和引用。文件、目标或依赖引用在预览后变化时，重新生成预览，不能强行复用原计划。

Project 右键菜单位于 `Assets/YLFramework/Excel/`，这是菜单名而非输出目录。普通 DataAssets Inspector 不需要 Excel。最近工作簿按根 GUID 以 `YLFramework.DataAssets.LastWorkbook.` 前缀保存在本机 `EditorPrefs`，不序列化到配置中；忘记路径不删除文件，工作簿存在时才允许定位目录。

## 工作簿格式

| 行 | 内容 |
| --- | --- |
| 第 1 行 | 字段协议名，大小写精确，不能重复 |
| 第 2 行 | `Int/Float/Double/String/Bool/Long/Json/ObjectReference/Enum` 列型，不区分大小写 |
| 第 3 行 | 描述，导出时取字段 `Tooltip` |
| 第 4 行起 | 数据，完全空白的行跳过 |

`boolean` 作为 `Bool`，`int64` 作为 `Long` 解释；`None` 不能作为有效列型。数值转换使用 `InvariantCulture`，非有限浮点数拒绝；长整型和浮点导出使用文本避免工作簿数值精度损失。只支持 `.xlsx`，不是通用 Excel 格式或公式计算引擎。

默认列名为 `field.Name`，用主包的 `ExcelColumnName` 显式声明协议名：

```csharp
using YLFramework.Excel;

public sealed class ItemRow
{
    [ExcelScriptableObjectId] public int id;
    [ExcelColumnName("display_name")] public string displayName;
    public int price;
}
```

`ExcelIgnore` / `JsonIgnore` 排除字段。字段可以是 public，或显式标记 `SerializeField/SerializeReference` 的实例字段；readonly、非序列化字段不参与，属性不是普通列字段。反射转换资格不等于 Unity 一定能把该字段持久化。

SO 必须有唯一 `ExcelScriptableObjectId` 标记字段，或可识别的 `id` 字段。ID 限稳定的 string、enum 或整数，不能空、重复，不能使用自定义 ID 转换。SO 导出包含 `DATA_TYPE_FULL_CLASS_NAME` 列，导入只接受与绑定数据类型兼容、可实例化且唯一可解析的具体类型。

`ObjectReference` 使用 `GUID[:localFileID]`，SO / List 导入会解析真实资产并检查目标类型；JSON 输出只校验格式并保留文本。`Json` 列解释为 JSON 值，enum 使用枚举文本；不根据 namespace、程序集或 Unity 字段别名猜列名。

## 代码入口

### SO、List 与 JSON

| 任务 | API 与边界 |
| --- | --- |
| SO 导出 | `ExcelAuthoring.ScriptableObjectToExcel(values, path, sheetName, dataType)`，稳定 ID 与具体类型列 |
| SO 预览 | `PreviewScriptableObjects(path, values, owner, assetFolder, ...)`，`values` 必须是 owner 实际序列化列表，不接受副本 |
| List 导出 | `ListToExcel<T>`，按声明行类型导出字段，无 `class, new()` 泛型约束 |
| List 预览 | `PreviewList<T>`，要求 `T : class, new()`；确认后用新行替换整个列表，不在原行合并 |
| JSON 预览 | `PreviewJson(path, jsonPath, excludePrefix, sheetName)`；输出 JSON 数组，输入输出必须不同 |
| DataAssets | `DataAssetsExcelCommands.Export(root, path)` / `PreviewImport(root, path)`，校验根绑定与条目所有权 |
| 公共预览窗口 | `ExcelAuthoringCommands.ShowPreview(plan)`，窗口持有并在关闭时释放计划 |

JSON 的界面入口为 `YLFramework/Excel/Workbook To JSON`。`excludePrefix` 按大小写精确排除列；输出父目录须存在，提交前会复核输入和输出文件哈希。

对于自定义 Editor 代码，可让公共窗口接管计划：

```csharp
// Editor 方法体片段，root 是已保存的 DataAssets 根，path 是明确选择的绝对 .xlsx 路径。
var plan = DataAssetsExcelCommands.PreviewImport(root, path);
ExcelAuthoringCommands.ShowPreview(plan);
```

若不使用窗口，自己检查错误并取得业务确认，最后无论确认、取消或异常都释放计划：

```csharp
// rows/path/confirmed 由调用方提供；ItemRow 如上定义。
using (var plan = ExcelAuthoring.PreviewList<ItemRow>(path, rows))
{
    foreach (var error in plan.Errors)
        UnityEngine.Debug.LogError(error);

    var result = plan.Commit(confirmed);
    UnityEngine.Debug.Log(result);
}
```

`CanCommit` 表示待处理、无已记录错误且有应用回调，不预先执行提交校验。`Changes` 为空也可能仍有应用回调；不要只凭差异数量跳过必要的规范化和校验。

### 扩展校验与转换

SO 的 `initialize` 仅初始化暂存对象，`afterImport` 在每一行字段转换后、差异生成前规范化暂存对象，不得改外部资产或全局状态。自定义字段转换也必须控制副作用，后端不能回滚任意外部行为。

`ExcelImportPlan.AddValidation(check)` 用于只读约束：添加时立即执行，成功后在真正写入前再次执行。添加时异常直接抛出；提交时异常记入计划错误。它不是提交后修补入口。

## 所有权与提交

`ExcelImportPlan` 是一次性 `IDisposable`。`Commit(true)` 明确确认，`Commit(false)` 永久取消；已终结计划只返回原 `Result`。终态为 `Applied/Cancelled/Failed`，`Dispose()` 取消仍待处理的计划并释放暂存对象，不撤销已应用结果。

SO 默认只管理 owner 同文件的子资产；`assetFolder` 显式选择独占目录中的独立主资产。owner 必须是 `Assets/` 下已保存的主 SO，操作不在 Play / 即将进入 Play 时进行。导入检查实际列表、现有条目所有权与输出路径，拒绝覆盖不属于本次操作的资产。

同 ID 同类型条目在暂存副本导入，确认时原位复制；新增或换型创建新资产，不保证换型保留资产身份。缺少于输入的自有条目可被删除，预览必须核对删除差异。DataAssets 导入使用本根 GUID 目录，非本根所有的条目会使预检失败。

提交前复核工作簿、owner、列表、原条目、暂存状态、外部资产引用、磁盘文件与目标路径。过期输入被拒绝，修复后重新预览。成功导入保存目标资产，但不自动刷新已登记的 Runtime 领域实例。

## 错误、恢复与限制

- 工作簿解析、单元格转换、重复列 / ID、非空未知列等错误进入 `Errors` 并阻止提交。缺少必需 ID 列不会静默生成身份。
- 取消和仅预览不写目标；提交失败会记录异常。暂存清理异常可直接传播，不能只看 `Result` 忽略释放失败。
- SO 提交捕获原文件和内存状态，失败时尝试恢复；回滚不完整会保留 `Temp/Extensions.Data.Excel/Recovery/<标识>/` 中的原文件与清单并报告路径。先保全证据、检查实际资产状态，再决定恢复或重试。
- SO 导入不是跨文件原子事务，也不提供通用 Undo 撤销保证；普通作者字段编辑的 Undo 不等于工作簿导入可整批 Undo。批量导入前使用版本控制或备份，不依赖预览成功替代提交验证。
- List 只替换目标容器；JSON 使用临时文件替换目标文件。两者不与 SO 或其他工作簿组成原子事务，源文件哈希检查也不是持续文件锁。
- 本包只用于 Editor。输出配置、工作簿与最近路径属于使用工程，更新或移除包不会自动覆盖 / 删除它们。
- 同时安装 Gameplay 才启用领域适配，详见[Gameplay 工作簿](Gameplay.md)。Localization 没有由本包提供的导入流程。

## 相关文档与来源

- [包入口](../README.md)
- [Gameplay 适配](Gameplay.md)
- [DataAssets 配置手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.data-assets/Documentation~/README.md)
- [公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)与[框架快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)
- [MiniExcel 许可证](../Editor/Licenses/MiniExcel-LICENSE.txt)、[署名](../Editor/Licenses/MiniExcel-ATTRIBUTION.txt)与[依赖来源声明](../Editor/Dependencies.json)

跨包链接指向源码仓库当前文档。
