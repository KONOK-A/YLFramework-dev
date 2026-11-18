# 内容交付与更新

> [资源管理](../Resource.md) · [作者构建](Authoring.md) · [安装](../../Installation.md)

内容交付建立在官方 Addressables 的 Catalog、内容状态、Bundle 缓存和 Provider 上。框架包安装、内容构建、上传、Player 构建与运行分别验证；安装包不会自动部署内容或初始化服务。

## 内置与远端内容

本地/远端 BuildPath、LoadPath、Catalog 与环境选择直接配置官方 Groups/Profiles。正式内容须针对实际 Player 平台构建，随 Player 的内容由官方构建集成进入正确的 `StreamingAssets/aa` 布局；远程内容部署到应用明确控制的服务。

不要将开发机输出绝对路径写入 Player，也不要将旧 `StreamingAssets/YLFrameworkResources` 私有快照当新版内置内容。旧数据保全、清单确认与移出活动输入应是显式迁移，不靠 ignore 隐藏，也不自动删其他 StreamingAssets 数据。

新增 SDK 或自定义 Provider 仍须核对平台、AOT、内容格式与资源释放。默认 SceneProvider 路径不支持自定义场景 Provider；普通官方资源与 YL 动态图片 Provider 不再受旧 Windows/CNG 密码后端门禁约束。没有实际测试的平台明确为未验证，不由 Editor 成功推断支持。

## API 边界

| 入口 | 结果 | 不承诺的能力 |
|---|---|---|
| `GetDownloadSizeAsync(keys)` | 当前官方目录下的下载量 | 不是内存峰值或未来目录的下载量 |
| `DownloadDependenciesAsync(keys)` | 官方 Union 依赖下载完成 | 不等于加载业务资产或准备尚未生效的新版本 |
| `CheckForCatalogUpdatesAsync()` | 只读 ID 快照；官方尝试读取的远端 hash 缺失或读取失败时返回错误 | 不自动激活、清缓存或回滚 |
| `ApplyCatalogUpdatesAsync(catalogIds)` | 已登记版本变更返回 `true`，无变化返回 `false`；自定义 Locator 只判断是否替换 | 不校验内容字节，不是完整应用版本事务 |

keys/IDs 在调用时冻结，空输入不能表示“全部”。取消只结束调用者等待，已经开始的官方工作继续排空。控制操作串行，待真实完成、独立 pin 归还及完成回调栈结束后才启动下一轮；不会按句柄 struct 副本猜所有权。

## 启动更新窗口

首版仅支持应用启动时切换 Catalog：先更新，再开放业务资源、Core/Audio 与 Localization 绑定。官方 Apply 尚在途时，业务入口拒绝加载，即使调用者已取消等待。第一次业务加载会封闭窗口；同进程重启 YL 服务不能重新获得更新权限。运行中可检查版本并下载当前 Catalog 已知内容，新目录安排到下次应用启动，不伪装成 Prepared 事务。

受控启动更新是显式选择：

```csharp
var configuration = new ResourceConfiguration(enableStartupCatalogUpdates: true);
startup = new ResourceStartup(configuration, bindCoreResourceProvider: false);
await startup.StartAsync();
var resources = startup.Resources;
var ids = await resources.CheckForCatalogUpdatesAsync();
if (ids.Count != 0)
    await resources.ApplyCatalogUpdatesAsync(ids);
// 在这之后开启业务 Scope、Core/Audio 与 Localization 等消费者。
```

组合根须保证本方最先组织初始化，并协调外部 Init/Check/Update。启用时，在官方 Initialize 前通过官方 JsonAssetProvider/ResourceManager 读取同一路径的真实 `settings.json`，确认 `DisableCatalogUpdateOnStartup=true`。已有 locators、自定义 Internal ID 变换、Web 请求覆盖或替代 runtime JSON Provider 时，无法证明官方初始化会读到同一来源，因此拒绝受控更新；默认仅加载模式不接管这些配置。

公开 API 不能可靠检测另一个入口尚在途的初始化，因此不能宣称 YL 自动证明了全进程顺序。该选项不是额外配置数据库，也不能代替应用自己的初始化协调。外部已经初始化时仍支持加载共存，但不能接管该次启动更新。默认不启用时 Check/下载仍可用，Apply 明确拒绝。

Addressables 可能回退到缓存或内置目录。远端 hash 缺失、读取失败或与官方登记版本不一致时，YL 不把这次回退算作更新成功，也不撤销 Unity 已完成的 Locator 变更。应用决定重试、退出或明确的离线策略。YL 和 Localization 各归还自己的操作；YL 停止不清全局 locators、providers、缓存或外部 Handle。

## 版本状态与后续能力

首次发布保留真正随 Player 使用的官方内容状态。更新继续使用该基线和官方 Content Update 检查，而不是套用旧私有 Catalog digest/App/ArtifactStore 协议。HybridCLR 的新基座与 payload/manifest 使用新合同，拒绝旧基座；已装载代码不可因资源 Lease 释放而卸载或回滚。

加密交付、应用级版本事务、恢复和回滚是明确的后续阶段，首版没有这些保证。Catalog hash 不等于发布者密码学签名，客户端密钥也不能承诺绝对防提取；后续扩展需要独立的安全目标、平台实现、密钥与故障验收，不恢复第二个加载内核。

## 验收

验证首启、缓存重启、下载/断网/失败、Catalog 检查与显式更新、Locale 共存、资产/实例/场景归还及普通退出。记录真实 Unity/Addressables、平台、后端、裁剪和内容身份。Editor、Play Mode、内容构建、Player 构建与 Player 运行是不同证据，未运行和前置不足不得记为通过。
