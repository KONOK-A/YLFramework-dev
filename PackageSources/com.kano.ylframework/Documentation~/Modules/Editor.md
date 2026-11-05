# Editor 扩展与资产维护

> [文档首页](../README.md) · [架构](../Architecture.md) · [配置数据](Data.md) · [领域系统](Domain.md) · [Help](Help.md)

本页面向扩展作者工具的开发者。框架普通 Editor 共用 `YLFramework.Editor`；可选 Excel / HybridCLR 保留独立程序集。目录职责与依赖方向见架构，本页维护资产编辑与公共支撑的使用合同。

## 资产编辑

- 新增或重构作者工具时，区分只读查询与显式修改；只修改明确选择或拥有的资产，使用适当的 SerializedObject / SerializedProperty、Undo 与 Prefab 保存机制。不能把方法名或 getter 形式视为无副作用保证。
- 共享配置与运行实例分离；外部引用只解除关联，不按列表成员身份删除外部文件。
- 配置数据放在使用工程的 `Assets/YLFrameworkData` 等明确 owner 根下，不属于 UPM 包输入；模块代码生成器的输出约定见下节，不与配置数据或待导入样例混同。安装、升级或移除包不覆盖/删除这些数据。
- `EditorAssetTool` 是资源预览桥，解析器由 Resource Editor 安装。公共层不直接初始化 Runtime 或硬编码 Resource 具体实现。

当前 `ProjectSettingsScriptableObject<T>.Instance` 在设置缺失时会创建默认实例并保存；`BuffSystemSettings.AllEffectDatas` 的读取可能清理失效项、同步扫描 EffectData 并保存设置。二者都不是被动统计或预览入口，调用前应核实资产、设置路径和预期副作用。

## ReorderableTreeView

`YLFramework.Editor.Common.ReorderableTreeView<T>` 的构造为：

```text
(ScriptableObject owner, Func<IList<T>> getData, TreeViewState state[, MultiColumnHeader header])
```

- getter 返回 owner 当前的序列化列表，不能捕获 Undo 之前的旧 List；初始化后调用 `Reload`，停用时 `Dispose` 归还订阅和本实例拖拽。
- 默认一槽位一行，保留空项和重复引用。过滤行或业务 ID 须覆盖 `GetItemIndex(int)` 返回真实数据索引。
- 陈旧节点起拖时拒绝并刷新；显示字段变化由所属 Inspector 显式 Reload。
- 工具只重排并标脏当前 owner，不自动去重、删空或保存资产。

## 设置与生成

Core 的默认资源、Module 生成与专用目录下的 ScriptableObject 创建策略归 `YLFramework.Editor.Core`。中立 Inspector、Scene、Settings、TreeView 与 EditorWindow 支撑归 `YLFramework.Editor.Common`。

Module 生成器输出到 `Assets/YLFrameworkData/Generated/Modules`，生成 namespace 保留 `YLFramework.Core.Examples`；这是使用工程的代码与程序集，不写安装包或 `Samples~`。迁移旧生成目录时保留真实 GUID/程序集身份，不在两个目录重建同名 asmdef。

`ScriptableObjectExtensions.CreateAsset()` 默认写入 `Assets/YLFrameworkData/ScriptableObjects`；显式路径只能在该根或其子目录内。只接受尚未归属 AssetDatabase 的新实例，不移动或覆盖已有资产、不接受链接目录。创建不注册 Undo，失败时由调用方处理自己仍持有的实例；不要将该助手当作已有用户资产的迁移工具。

正式 Prefab / 默认 Resources 已随包保存，不在 Editor 启动时重新生成。消费方的 `UIPanelAssetMigration.ValidateAssets()` 只读核验模板，不写包；历史 `MigrateAssets()` 不是升级步骤。自定义模板留在项目自有目录，见 [UI 资产准备](UI.md#资产准备与迁移)。

Core 设置根仍为 `ProjectSettings/YLFramework/Core`。类型与文件归位不意味着可以更换已有设置路径、菜单、生成存储根或窗口持久身份；涉及 Buff 的精确旧路径时使用 [显式迁移](Domain.md#buff-设置迁移)。

生成器修改源模板与输入，再验证正式输出和消费者，不直接修补生成代码。需要预览或导出时不自动保存用户场景，不认领未知输出或冻结基座。

## Editor namespace 迁移

旧 `YLFramework.Editor.Utilities` 中的公共支撑与领域策略已按所有者归位：

| 类型 / 职责 | 当前归属 |
| --- | --- |
| AssetNameTools、EditorAssetTool、SerializedObject / SerializedProperty 扩展、HandlesExtensions、设置基类、TreeView、SplitWindow | `Editor/Common`，namespace `YLFramework.Editor.Common` |
| ClearLog、OpenFolder | `Editor/Tools`，namespace `YLFramework.Editor.Tools` |
| ScriptableObjectExtensions | `Editor/Core`，namespace `YLFramework.Editor.Core` |
| DomainAssetOwnership | Gameplay包`Editor/Shared`，namespace `YLFramework.Editor.Domain`，内部归属`YLFramework.Gameplay.Editor` |

外部源码更新 using、全限定名和反射字符串，不依赖旧 namespace 包装或 CLR 别名。SplitWindow、TreeViewStateGroup、TreeViewStateInfo 保留适用的 MovedFrom 以迁移窗口或托管引用，但不提供旧 `Type.GetType` 字符串解析。

脚本 GUID 与无关字段保持；迁移用 Unity 实际保存的旧资产检查引用、重导入和持久状态，不用改名后伪造的旧 YAML 代替。

## 工具入口

资源构建与发布见 [Resource](Resource.md)，配置作者流程见 [Data](Data.md)，领域窗口见 [Domain](Domain.md)。帮助提供方、XML 与扩展见 [Help](Help.md)，UPM 安装及消费方边界见 [安装与分发](../README.md#安装与分发)。隐藏的 `Documentation~` 按已解析包的实际文件系统路径打开，不能通过 AssetDatabase 查找。

菜单归模块所有者；存在 MenuItem 不等于应进入通用 Tools。作者操作、调试窗口、帮助浏览与发行导出不能混为同一种副作用。
