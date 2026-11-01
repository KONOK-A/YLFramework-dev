# 配置数据与作者流程

> [文档首页](../README.md) · [架构](../Architecture.md) · [Resource](Resource.md) · [领域系统](Domain.md)

本页说明 Localization、DataAssets 和可选 Excel 作者流程。配置资产是共享定义，运行时登记与资源租约是独立责任；存档文件及故障恢复见 [Save](Save.md)，不把所有数据合并为同一个 Manager。

## 能力与入口

| 能力 | 运行时入口 | 配置与责任 |
| --- | --- | --- |
| 多语言 | `YLFramework.Localization.LocalizationManager` | MultiLanguageConfigs → LanguageConfig → LanguageInfo；配置句柄由登记者释放 |
| 通用 SO 数据 | `YLFramework.DataAssets.ScriptableObjectManager` | ScriptableObjectConfigs / ScriptableObjectData；允许多 owner 登记同一根，独立归还责任 |
| Excel 字段合同 | `YLFramework.Excel` | Runtime 只定义字段 Attribute、转换器和 `ExcelFieldsType`，不执行 xlsx IO |
| Excel 作者后端 | `YLFramework.Integrations.Excel.Editor` | 可选 Editor-only 后端，预览后显式提交，依赖和许可证一并安装 |

源码入口：[Localization](../../Runtime/Systems/Localization/) · [DataAssets](../../Runtime/Systems/DataAssets/) · [Excel 字段合同](../../Runtime/Utilities/Excel/) · [可选后端](../../Integrations/Excel/Editor/)。

## 资源准备与生命周期

1. 使用 Resource 加载时，先初始化 ResourceSystem，并通过 `ResourceProjectMount(..., assetTypes: compiledTypes)` 或 `ResourceLocalSnapshot.CreateCatalog` 提供显式编译类型清单。
2. 登记实际加载的根与具体条目类型，如 `typeof(MultiLanguageConfigs)`、`typeof(ScriptableObjectConfigs)`；不能使用开放泛型替代具体运行时根。自定义派生类型和反射成员的 IL2CPP 保留由应用负责。
3. 持有本次登记或异步加载返回的句柄，业务只在其有效期间借读。结束时先停止使用配置的 UI / 业务，再释放配置句柄及其根资源；不要关闭外部 ResourceSystem 来代替精确归还。

直接 `AddConfig` 借用已经加载的配置，外部 owner 负责保活；通过资源入口加载则由相应句柄持有租约。共享资产不能用来保存每个业务实例的可变状态。

## Localization

`LocalizationManager.AddConfig` 与 `LoadConfigAsync` 返回 `LocalizationConfigHandle`。调用方 Dispose 自己的句柄，不撤销其他 owner 的登记。

- `LocalizeString` / `LocalizeImage` 直接刷新表现，不需要重启业务 GameObject。
- 回退顺序为请求语言、适用的中文变体、English、登记顺序；不是任意猜测系统语言或扫描其他资产。
- Other 模式的公开 text 反射成员需由应用在 IL2CPP 中保留。
- Localization 当前没有 Excel 作者流程，不能将可选后端存在解释为所有配置均支持工作簿导入。

## DataAssets

`ScriptableObjectManager.AddConfig` / `Load` 返回独立登记；`LoadAsync` 返回持有消费者请求、成功 lease 和登记的加载句柄。

- 根按资产对象名称与 hash 查询，不按 `displayName` 查询；碰撞会被拒绝。
- 多 owner 可共享同一根，每个 owner 只 Dispose 自己的登记或加载句柄。
- 作者入口为 `Window/YLFramework/Data Assets`，只编辑明确选择的 YL 根，不扫描或认领其他框架的数据。

当前持久字段是资产协议，不因 C# 整理随意改变：

| 资产 | 字段 |
| --- | --- |
| SO 根 | `displayName/configs/typeScriptGuid` |
| SO 条目 | `parent/id/configName/icon/description` |
| 多语言根 | `displayName/languages`，公开读取为 `Languages` |
| 语言 | `language/displayName/languagesInfo` |
| 语言条目 | `id/type/str/image/url/notes` |

资源地址字段 `projectName/imageName/subAsset` 表示 YL ProjectId / Path / SubAsset，不转换为其他资源系统的地址。作者工具生成到 `Assets/YLFrameworkData` 的条目属于使用工程，不是默认框架包源码。

## Excel 作者后端

Runtime 不要求安装 MiniExcel。需要 xlsx 时显式安装完整 `Integrations/Excel/Editor`、声明的依赖及许可证；领域菜单另需 [DomainExcel](Domain.md#domainexcel)。

- `ExcelAuthoring` / `ExcelImportPlan` 先产生 `Errors/Changes/CanCommit` 预览，再显式 `Commit(true)`；取消不写入，过期输入或引用失效拒绝提交或按合同回滚。
- 支持 SO / List / JSON 的导出与导入预览；保留 `boolean → Bool`、`int64 → Long` 字段合同。
- SO 根菜单位于 `Assets/YLFramework/Excel/`。工作簿绝对路径按资产 GUID 保存于 EditorPrefs，不写入资产；只有文件存在时才启用打开目录操作。
- 领域工作表列名是独立协议。自有模型通过唯一的 `FormerlySerializedAs` 旧名映射维持已有表头，如 `buff_id/skill_name/equipment_name`；外部模型使用自身字段规则。未知列、重复列和 ID 校验保持严格。
- `ExcelImportPlan.AddValidation` 是预览与提交前的只读约束，不用于提交后修补数据。

改名或升级时，用真实旧资产及工作簿验证字段、引用和往返结果，不以 GUID 未变或预览成功代替实际提交验证。
