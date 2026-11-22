# 资源制作与构建

> [资源管理](../Resource.md) · [内容交付](Delivery.md) · [文档首页](../../README.md)

新版以官方 Addressables Settings、Groups、Labels、Profiles、Entries 和 AssetReference 为唯一作者事实。YL 提供校验、导航、常量生成和正式构建编排，不再维护 ResourceProject/Catalog/BundleMap 的同步副本。

## 第一次配置

1. 在官方 `Window/Asset Management/Addressables/Groups` 建立本工程的默认 Settings，明确选择 Profile、目标和 Play Mode Script。
2. 把真实资产加入官方 Group；address 使用稳定且唯一的业务键，例如 `game/ui/inventory`。对象移动仍保留原 `.meta`/GUID。
3. 使用 Labels 表达 Runtime 集合。Group 表达编辑与分包策略，不当作可运行时查询的私有组。
4. 显式关闭自动启动 Catalog 更新检查，即启用 `Disable Catalog Update On Startup`；使用正式 packed content builder，保存自己已确认的设置、Group、schema 和场景。
5. 打开 `YLFramework/Resource/Workbench` 校验、定位官方配置、生成常量或执行正式内容构建。浏览窗口不初始化 Runtime，不自动切 Profile/平台或保存脏场景。

编辑器脚本打开同一窗口时使用 `ResourceWorkbenchWindow.OpenWorkbench()`。若仍引用旧工作台类型，改用 `ResourceWorkbenchWindow` / `ResourceWorkbenchAssetsMenu`；菜单路径不变。

`ResourceAddressablesAuthoring.Validate` 只读检查启动更新前置、参与构建的 Group 中失效的条目，以及正式包单资产键冲突。一个 Label 对应多个资产本身合法，但不能与另一个普通资产的地址或 GUID 撞名：即使具体类型不同，`UnityEngine.Object` 查询也会返回多项。场景按 `SceneInstance` 单独判断；同一条目以同一个字符串发布地址和 Label 不算歧义。`FindUniqueEntry` 是作者导航，不用于证明正式 Catalog 可解析。

工作台、常量生成和构建预检共用同一检查。检查不写入持久资产、不主动改变资产脏状态，也不改变 `.meta` 文件字节；官方目录枚举可以刷新 `IsFolder`、`SubAssets` 等内存缓存，不保证这些列表或其对象实例不变。

安装 Addressables 1.25.0 时，检查按官方 `PlayerDataGroupSchema` 识别启用的 Resources 资产和 Build Settings 场景。如果它们与 Bundle 实际启用的地址、GUID 或已登记 Label 共享同一字符串键，且双方都有匹配 `UnityEngine.Object` 或 `SceneInstance` 的位置，就报告冲突；双方仅共享 Label 不算冲突。内建场景的整数索引不参与比较。一个资产发布多个类型或位置本身不算冲突。Addressables 2.10.3 的官方 Packed 构建不发布这些旧式位置，因此不做此项检查。

**目录格式选择：** 如果使用 `1.25.0` 并依赖 Build Settings 场景键，请保留默认 JSON，不添加 `ENABLE_BINARY_CATALOG`。在 Unity 2022.3 的实建二进制目录中，启用 `IncludeBuildSettingsScenes` 后，官方定位器查询场景键可能抛异常，即使 Bundle 地址与场景键不重名。YL 作者预检通过不代表官方定位器一定能读取它。关闭该选项会停止发布这些内建场景键，并非等价修复；这个非默认组合尚无 Win64 Player 验证。`2.10.3` 默认使用二进制目录，不需要此宏。两版各自的默认目录均已通过 Win64 IL2CPP Player 验证。

官方支持的 Folder、AssetReference 子对象、共享依赖、分包和 Analyze/Build Report 按实际安装版本使用。子对象不能靠旧框架 localId 拼接猜名称；名称重名或身份无法表达时必须修正真实引用。

## 常量与图集

只为参与 Packed 构建的普通 Bundle Group 生成实际启用的地址、已登记且被使用的 Label。`IncludeInBuild` 关闭、无 Bundle schema、关闭对应 Catalog 键输出的条目不提供这些常量；关闭 `IncludeGUIDInCatalog` 会影响通过 GUID 加载的 `AssetReference`。工作台说明开关的影响，不会自动打开。内建键仅用于冲突检查，不生成常量；不支持推断自定义 schema 发布的键。

修改上述设置后重新生成并检查消费者编译：不再发布的常量会被移除，未变化的键保留原字面值与排序。生成与校验不能替代从实际 Packed Catalog 反查，尤其是自定义 Builder、导入器或复杂子对象。

图集使用 Unity `SpriteAtlas`，准备 Packables 与目标平台设置，登记为官方 Entry。YL 的作者辅助不保留私有 Atlas Runtime 或分包算法。需要读取具体 Sprite 时持有 Atlas Lease，销毁自己得到的 Sprite 克隆后再归还 Atlas。

共享对象和 SpriteAtlas 原资产不因预览、显示或 Runtime 消费被修改。Player 中图集像素、裁剪、依赖和释放必须实测；Editor 构建或 AssetDatabase 预览不是目标平台运行证明。

## 正式内容构建

`ResourceEditorOperations.BuildContent(settings, newContentStateArchivePath, cancellation)` 使用官方 `AddressableAssetSettings.BuildPlayerContent`。传入当前默认 Settings 和全新的归档 `.bin` 路径；父目录须已存在，输出不能覆盖旧文件。

```csharp
using UnityEditor.AddressableAssets;
using YLFramework.Editor.Resource;

var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
var report = ResourceEditorOperations.BuildContent(settings,
    "Build/ContentStates/player-v2-windows.bin");
UnityEngine.Debug.Log(report.Summary);
```

归档的是该次真实官方输出的 `addressables_content_state.bin`。`Report.Target/ProfileId/SettingsGuid/ContentStatePath` 记录此次操作，不代表已上传、部署、构建 Player 或完成运行验证。若 Player 构建又重新生成内容，必须保存并配对那次实际内容状态，不能沿用不匹配基线。

门禁拒绝 Play/编译/导入、并发构建、脏 Settings/Group/schema/scene、无效 Profile 和非正式 packed builder。工具不替作者自动保存或搬组，不把构建失败改成成功空结果。

## 内容更新构建

`BuildContentUpdate(settings, frozenContentStatePath, cancellation)` 调用官方 Content Update 流程。使用目标 Player 对应的冻结状态、兼容的 Unity/Addressables 版本、平台、Profile 和远程 Catalog 配置；不是从磁盘目录名猜“最新”。

先在官方工具检查并处理内容更新限制；工具不会擅自移动 Entry/Group 来掩盖冲突。官方 Bundle 身份、缓存复用与 Catalog 变化仍由官方决定，YL 不再生成不可变私有发布快照或 app-level 事务日志。

## 样例与迁移

导入完整 [Resource Startup](../../../Samples~/ResourceStartup/Runtime/ResourceStartupExample.cs) 后执行样例创建菜单，获得单独的 Bootstrap/Business 场景、实际 AssetReference 和官方 Entries。只保存新创建资产，不保存或关闭用户已有场景，也不自动启动资源服务。

旧 ResourceProject/Recipe/Atlas 配置只可作为一次性迁移输入，不参与新版运行或内容构建。先保全源码、原资产与 `.meta`，验证源 GUID/localId、字段和 SHA，预览官方映射及 alias 消费者改写，再在无并发写入窗口受控应用并从干净副本复验。旧 Player、Bundle/Catalog 与 HybridCLR 基座不兼容新版内容，不能自动改写已发布基座。

转换预览通过不等于批准退休。删除前必须验证明示选中的资产及 `.meta`，并检查本地工程内保留下来的文件是否仍引用这些 GUID；无法检查的输入会阻断删除。冷 Verify 从落盘字节启动，并核对成功的整批 Apply 回执、逐文件回执及保留文件的引用范围。隔离演练不能代替源工程回写或源工程的冷 Verify。

选择器、资源名或 Git 忽略状态都不是删除资产的授权；未知配置、输出或冲突保持原样并报告。历史实现研究快照位于开发者本机的非 Unity/发行输入目录，不属于框架产品。
