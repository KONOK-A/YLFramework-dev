# 资源制作与构建

> [资源管理](../Resource.md) · [内容交付](Delivery.md) · [文档首页](../../README.md)

新版以官方 Addressables Settings、Groups、Labels、Profiles、Entries 和 AssetReference 为唯一作者事实。YL 提供校验、导航、常量生成和正式构建编排，不再维护 ResourceProject/Catalog/BundleMap 的同步副本。

## 第一次配置

1. 在官方 `Window/Asset Management/Addressables/Groups` 建立本工程的默认 Settings，明确选择 Profile、目标和 Play Mode Script。
2. 把真实资产加入官方 Group；address 使用稳定且唯一的业务键，例如 `game/ui/inventory`。对象移动仍保留原 `.meta`/GUID。
3. 使用 Labels 表达 Runtime 集合。Group 表达编辑与分包策略，不当作可运行时查询的私有组。
4. 显式关闭自动启动 Catalog 更新检查，即启用 `Disable Catalog Update On Startup`；使用正式 packed content builder，保存自己已确认的设置、Group、schema 和场景。
5. 打开 `YLFramework/Resource/Workbench` 校验、定位官方配置、生成常量或执行正式内容构建。浏览窗口不初始化 Runtime，不自动切 Profile/平台或保存脏场景。

`ResourceAddressablesAuthoring.Validate` 只读报告前置与地址冲突。单资产键与 GUID 不能歧义；大小写和路径不被静默标准化。`FindUniqueEntry` 读取官方条目，缺失或多结果拒绝，不选第一项补缺。

官方支持的 Folder、AssetReference 子对象、共享依赖、分包和 Analyze/Build Report 按实际安装版本使用。子对象不能靠旧框架 localId 拼接猜名称；名称重名或身份无法表达时必须修正真实引用。

## 常量与图集

地址和 Label 常量只从官方 Entries/Labels 生成，名称、输出路径和覆盖范围由调用者明确提供。它们是编译便利入口，不是另一个 Catalog；更改地址后仍须更新真实消费者和内容。

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
