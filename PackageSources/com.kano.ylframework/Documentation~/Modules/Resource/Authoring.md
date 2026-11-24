# 资源制作与构建

> [资源管理](../Resource.md) · [内容交付](Delivery.md) · [文档首页](../../README.md)

在官方 Addressables Settings、Groups、Labels、Profiles 和 Entries 中编辑内容，场景和脚本通过地址或 `AssetReference` 引用。YL 提供校验、导航、常量生成和正式构建编排。这里是普通内容流程；加密另见[可选的 Bundle 加密](Delivery.md#可选的-bundle-加密)。

## 第一次配置

1. 在 `Window > Asset Management > Addressables > Groups` 建立本工程的默认 Settings，先选择目标平台和 Profile。第一次编辑器测试可用 Play Mode Script 的 **Use Asset Database**；用 **Use Existing Build** 之前必须先构建匹配的内容，有远端组还须能访问该构建的 LoadPath。这两个选项不决定 Player 如何加载。
2. 把真实资产加入官方 Group；address 使用稳定且唯一的业务键，例如 `game/ui/inventory`。要按地址、`AssetReference` 或 Label 查询，就分别在 Group 的 Catalog 选项中发布地址、GUID 或 Label。对象移动仍保留原 `.meta`/GUID。
3. 使用 Labels 表达 Runtime 集合。Group 表达编辑与分包策略，不当作可运行时查询的私有组。
4. 启用官方 Settings 中的 **Only update catalogs manually**（对应 `DisableCatalogUpdateOnStartup`），关闭自动启动更新检查。只用本地 Catalog 时，若此选项置灰，先勾选 **Build Remote Catalog** 以修改此选项，再将 **Build Remote Catalog** 关回去，之后才构建。使用正式 Packed Builder，保存确认过的 Settings、Group、Schema 和场景。
5. 打开 `YLFramework/Resource/Workbench` 校验、定位官方配置、生成常量或执行正式内容构建。浏览窗口不初始化 Runtime，不自动切 Profile/平台或保存脏场景。

编辑器脚本可调用 `ResourceWorkbenchWindow.OpenWorkbench()`（`YLFramework.Editor.Resource`，程序集 `YLFramework.Editor`）。

`ResourceAddressablesAuthoring.Validate` 只读检查启动更新前置、参与构建的 Group 中失效的条目，以及正式包单资产键冲突。一个 Label 对应多个资产本身合法，但不能与另一个普通资产的地址或 GUID 撞名：即使具体类型不同，`UnityEngine.Object` 查询也会返回多项。场景按 `SceneInstance` 单独判断；同一条目以同一个字符串发布地址和 Label 不算歧义。`FindUniqueEntry` 是作者导航，不用于证明正式 Catalog 可解析。

工作台、常量生成和构建预检共用同一检查。检查不写入持久资产、不主动改变资产脏状态，也不改变 `.meta` 文件字节；官方目录枚举可以刷新 `IsFolder`、`SubAssets` 等内存缓存，不保证这些列表或其对象实例不变。

安装 Addressables 1.25.0 时，检查按官方 `PlayerDataGroupSchema` 识别启用的 Resources 资产和 Build Settings 场景。如果它们与 Bundle 实际启用的地址、GUID 或已登记 Label 共享同一字符串键，且双方都有匹配 `UnityEngine.Object` 或 `SceneInstance` 的位置，就报告冲突；双方仅共享 Label 不算冲突。内建场景的整数索引不参与比较。一个资产发布多个类型或位置本身不算冲突。Addressables 2.10.3 的官方 Packed 构建不发布这些旧式位置，因此不做此项检查。

**目录格式选择：** 如果使用 `1.25.0` 并依赖 Build Settings 场景键，请保留默认 JSON，不添加 `ENABLE_BINARY_CATALOG`。在 Unity 2022.3 的实建二进制目录中，启用 `IncludeBuildSettingsScenes` 后，官方定位器查询场景键可能抛异常，即使 Bundle 地址与场景键不重名。YL 作者预检通过不代表官方定位器一定能读取它。关闭该选项会停止发布这些内建场景键，并非等价修复；这个非默认组合尚无 Win64 Player 验证。`2.10.3` 默认使用二进制目录，不需要此宏。两版各自的默认目录均已通过 Win64 IL2CPP Player 验证。

官方支持的 Folder、AssetReference 子对象、共享依赖、分包和 Analyze/Build Report 按实际安装版本使用。子对象不能靠旧框架 localId 拼接猜名称；名称重名或身份无法表达时必须修正真实引用。

## 常量与图集

只为参与 Packed 构建的普通 Bundle Group 生成实际启用的地址、已登记且被使用的 Label。`IncludeInBuild` 关闭、无 Bundle schema、关闭对应 Catalog 键输出的条目不提供这些常量；关闭 `IncludeGUIDInCatalog` 会影响通过 GUID 加载的 `AssetReference`。工作台说明开关的影响，不会自动打开。内建键仅用于冲突检查，不生成常量；不支持推断自定义 schema 发布的键。

修改上述设置后重新生成并检查消费者编译：不再发布的常量会被移除，未变化的键保留原字面值与排序。生成与校验不能替代从实际 Packed Catalog 反查，尤其是自定义 Builder、导入器或复杂子对象。

工作台用 **Generate Official Address / Label Constants** 生成 `YLResourceReferences.cs`；默认目录为 `Assets/YLFrameworkData/Generated/ResourceReferences`，自有 asmdef 引用生成的 `YLFramework.Generated.ResourceNames`。目录有未知文件、已有生成物被改写或其他位置已存在同名 asmdef 时会拒绝覆盖。先保留原文件及 `.meta`，由项目所有者核对实际消费者和程序集归属，处理冲突后重新生成，不另建同名程序集。

图集使用 Unity `SpriteAtlas`，准备 Packables 与目标平台设置，登记为官方 Entry。YL 的作者辅助不保留私有 Atlas Runtime 或分包算法。需要读取具体 Sprite 时持有 Atlas Lease，销毁自己得到的 Sprite 克隆后再归还 Atlas。

共享对象和 SpriteAtlas 原资产不因预览、显示或 Runtime 消费被修改。Player 中图集像素、裁剪、依赖和释放必须实测；Editor 构建或 AssetDatabase 预览不是目标平台运行证明。

## 正式内容构建

工作台选择 **Full Official Content Build + Archive State**，或在 Editor-only 的自有 asmdef 中引用 `YLFramework.Editor` 与 `Unity.Addressables.Editor`，调用 `ResourceEditorOperations.BuildContent(settings, contentStateArchivePath, cancellation)`。它使用官方 `AddressableAssetSettings.BuildPlayerContent`。传入当前默认 Settings 和新的归档 `.bin` 路径；父目录须已存在，输出不能覆盖旧文件。下面是 Editor 方法体片段，引用 `UnityEditor.AddressableAssets` 与 `YLFramework.Editor.Resource`；先在工程根准备此例的 `Build/ContentStates` 目录。

```csharp
var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
var report = ResourceEditorOperations.BuildContent(settings,
    "Build/ContentStates/windows-release.bin");
UnityEngine.Debug.Log(report.Summary);
```

归档的是该次真实官方输出的 `addressables_content_state.bin`。报告的 `Target`、`ProfileId`、`SettingsGuid` 和 `ContentStatePath` 只记录本次内容构建，不代表已上传、部署或完成 Player 验证。如何将此内容与 Player 配对，见[首次交付](Delivery.md#内置与远端内容)。

门禁拒绝 Play/编译/导入、并发构建、脏 Settings/Group/schema/scene、无效 Profile 和非正式 packed builder。工具不替作者自动保存或搬组，不把构建失败改成成功空结果。

## 内容更新构建

选择 **Official Content Update From Archived State**，或调用 `BuildContentUpdate(settings, previousContentStatePath, cancellation)`。选择目标 Player 的归档 `.bin`，不是当前活跃构建输出。保持同一 Unity 版本、匹配的 Addressables 版本、平台、Profile 与远端 Catalog LoadPath；该入口会检查归档中的 Editor 版本与 Catalog 地址。

先在官方工具检查并处理内容更新限制；YL 工具不会移动 Entry/Group 来掩盖冲突。Bundle 身份、缓存复用与 Catalog 变化仍由官方决定，参见 [Unity Content Update](https://docs.unity3d.com/Packages/com.unity.addressables@2.10/manual/ContentUpdateWorkflow.html) 与[启动更新窗口](Delivery.md#启动更新窗口)。

## 运行资源样例

1. 在 Package Manager 的 Samples 中导入 **Resource Startup**。准备并保存官方 Settings 后，执行 `YLFramework/Samples/Create Resource Startup Example`。生成器只保存本次创建的样例资产和场景，不保存或关闭用户已有场景。
2. 打开生成的 `Bootstrap.unity`，进入 Play Mode。在 `ResourceStartupExample` 组件上下文菜单选择 **Start Addressables Example (Play Only)**，查看 Inspector 的 **Runtime Result**。
3. 用 **Stop Or Retry Example Shutdown** 等待正常关闭；Player 验证则先按上述流程构建正式内容。源码见[样例运行组件](../../../Samples~/ResourceStartup/Runtime/ResourceStartupExample.cs)。

若需复查进入 Play 时很快结束的请求，先选 **YLFramework → Resource → Record Next Play**，复现后打开 **Workbench → Runtime Diagnostics** 查看请求事件。录制周期、指纹与手动快照的区别见[停止与诊断](../Resource.md#停止与诊断)。

旧私有资源配置与制品不是官方 Addressables 构建输入。保留项目原资产与 `.meta`，在官方 Groups 中登记源资产并重新构建配套内容；不能把旧 Catalog、Bundle 或 HybridCLR 基座当作本次构建输出。
