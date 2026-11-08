# Editor 扩展与资产维护

> [文档首页](../README.md) · [架构](../Architecture.md) · [Attributes](Attributes.md) · [Help](Help.md)

本页面向扩展作者工具的开发者。主包 Editor 程序集为 `YLFramework.Editor`，选装包的作者工具归各自安装单元，见[程序集清单](../Architecture.md#目录与程序集)。

## 资产编辑

- 区分只读查询和显式修改；只修改明确选择或拥有的资产，按操作使用 `SerializedObject` / `SerializedProperty`、Undo 与 Prefab 保存机制。方法名或 getter 形式不是无副作用保证。
- 配置定义与运行实例分开。外部引用只解除关联，不因其出现在列表里就删除外部文件。
- 使用工程的配置数据位于 `Assets/YLFrameworkData` 等明确所有者根，不是 UPM 包输入。安装、更新或移除包不覆盖或删除这些数据。
- `EditorAssetTool` 提供资源预览桥，解析器由 Resource Editor 安装；公共 Editor 层不主动初始化 Runtime 或硬编码 Resource 实现。
- 剪贴板与序列化属性扩展的限制见 [Attributes](Attributes.md#编辑器剪贴板)，不要直接覆盖共享资源来实现预览。

`ProjectSettingsScriptableObject<T>.Instance` 在设置缺失时会创建默认实例并保存。Gameplay 的 `BuffSystemSettings.AllEffectDatas` 读取也可能清理失效项、扫描 EffectData 并保存。因此做只读统计前应先核实调用的真实副作用和资产路径。

## ReorderableTreeView

`YLFramework.Editor.Common.ReorderableTreeView<T>` 构造参数为：

```text
(ScriptableObject owner, Func<IList<T>> getData, TreeViewState state[, MultiColumnHeader header])
```

1. 传入实际序列化数据所有者及读取其当前列表的 getter，不捕获 Undo 前的 List。
2. 初始化后调用 `Reload()`，显示字段变更由所属 Inspector 显式刷新。
3. 停用时调用 `Dispose()`，归还订阅与本实例的拖拽责任。

默认一槽位一行，保留空项和重复引用。过滤行或使用业务 ID 时，覆盖 `GetItemIndex(int)` 映射真实索引。陈旧节点起拖会拒绝并刷新；重排只标脏当前 owner，不自动去重、删空或保存资产。

## 设置与生成

Module 生成器输出到 `Assets/YLFrameworkData/Generated/Modules`，namespace 与程序集身份为 `YLFramework.Core.Examples.<ModuleName>`。任意位置存在同名 asmdef、已加载同名程序集或类型时，写入前拒绝并指出来源；不能通过另选输出目录创建第二个同名身份。

Resource 生成器有独立的专属目录与输出保护，见[地址常量生成](Resource/Authoring.md#地址常量生成)。修改生成器时改源模板和输入，再验证正式输出与消费者，不手补生成代码。

`ScriptableObjectExtensions.CreateAsset()` 默认输出 `Assets/YLFrameworkData/ScriptableObjects`，显式路径也只能在该根或子目录中。它只接受尚未归属 AssetDatabase 的新实例，不移动或覆盖已有资产、不接受链接目录，不注册 Undo。失败时由调用者处理自己仍持有的实例。

Core 设置根为 `ProjectSettings/YLFramework/Core`；各工具按自己的当前类型和设置身份访问，不扫描或接管外部设置。Gameplay 的 Buff 设置见源码当前 [Gameplay 手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.gameplay/Documentation~/README.md)。

## 模板与输出所有权

正式 Prefab 与默认 Resources 已随包保存，不在 Editor 启动时重新生成。`UIPanelAssetValidator.ValidateAssets()` 只读核验模板，见 [UI 资产准备](UI.md#资产准备)。自定义模板存放于使用工程自有目录，不修改 PackageCache。

显式创建默认资源时仍须保护只读包、未知输出和已经存在的内容。样例导入后也是工程自有副本；检查重复程序集与自己的改动后再更新。缺失脚本未补齐时不要保存场景。

工作簿、StreamingAssets、Resource 快照、HybridCLR 基座均由使用工程拥有。预览、导出、包更新或卸载不自动认领、覆盖或清理这些输出。SDK/native/AOT/裁剪输入变化需要新的匹配基座，不能直接覆盖冻结产物。

## 公共支撑

| 职责 | 目录与 namespace |
| --- | --- |
| AssetNameTools、EditorAssetTool、属性扩展、Handles、设置基类、TreeView、SplitWindow | `Editor/Common`，`YLFramework.Editor.Common` |
| ClearLog、OpenFolder 等独立命令 | `Editor/Tools`，`YLFramework.Editor.Tools` |
| Core 默认资源、Module 生成、ScriptableObject 创建 | `Editor/Core`，`YLFramework.Editor.Core` |
| Gameplay 资产所有权与领域共享作者策略 | Gameplay 包 `Editor/Shared`，`YLFramework.Editor.Domain` |

菜单归功能所有者；存在 `MenuItem` 不意味着应移入通用 Tools。作者写入、调试、帮助浏览与发行导出是不同副作用，应在入口上区分。

## 相关文档

- [资源工作台与构建](Resource/Authoring.md)
- [帮助注册与 XML](Help.md)
- [选装能力与所属手册](../README.md#选装能力)
- [安装与项目数据](../Installation.md#样例与项目数据)

`Documentation~` 必须从已解析包的真实文件系统路径打开，不能经 AssetDatabase 查找，也不固定 PackageCache 的哈希目录。
