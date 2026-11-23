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

应用级版本事务、恢复和回滚不在当前合同内。Catalog hash 不等于发布者密码学签名。

## 可选的远端 Bundle 加密

`com.kano.ylframework.resource-encryption` 是独立扩展，主包不依赖它。普通资源项目不用安装；有需求时按[安装指南](../../Installation.md#选装远端资源加密)显式添加。只安装包不会启用加密、修改普通组的 Unity Bundle 缓存或初始化原生库。下表是 Windows x64 Release Player 的本地验证范围，不包含 Editor Play Mode、Development、ARM64 或其他平台。本地 `.tgz` 不是 Registry 发布凭据。

| Unity | Registry Addressables / 默认 Catalog | 后端 / 裁剪 |
| --- | --- | --- |
| `6000.3.27f1` | `2.10.3` / `catalog.bin` | Mono / Disabled，IL2CPP / Medium |
| `2022.3.62f3` | `1.25.0` / `catalog.json` | Mono / Disabled，IL2CPP / Medium |

**作者构建。** 在一个远端 LZ4 Group 加入 `Encrypted Bundle Delivery` Schema，选择 `EncryptedAssetBundleProvider` 作为 **Bundle Provider**，保留官方资产 Provider、带 hash 的 Bundle 名，关闭该组的 Unity Bundle Cache。Schema 只保存 `keyId` 和按 GUID 明确允许公开的依赖，不保存钥材。保护组使用显式非场景资产；其传递依赖默认受保护。将 BuildPath 和官方内容状态指向独立的非公开暂存根，LoadPath 指向新建且不可覆盖的 HTTPS 发布子目录。禁用远端 Catalog、启动更新和随 Player 自动重建 Addressables 内容。由正式 Player 构建入口调用 `EncryptedBundleExporter.Build(...)`，传入项目设置、所选组、Player 构建参数、固定应用命名空间、私有/发布目录和来自 CI 密钥存储的 `IEncryptedBundleKeySource`。只发布审计通过的导出目录；不要上传官方明文暂存、Build Layout 或内容状态。若修改内容、密钥或关联数据，使用新发布目录并重建 Player；重发同一 URL 时只能复用完全相同的冻结产物。

保留官方 `Library` / `Temp` 路径和受保护组的 `Requested Asset and Dependencies` 加载模式；普通远端组使用独立、全新的暂存目录。Unity 2022 若保留 `Built In Data` 的场景别名，`BuildPlayerOptions.scenes` 必须与启用的 Build Settings 场景顺序一致。私有暂存根和最终 Player 目录须在同一 Windows 本地卷；Player 先在私有根构建，随包只带本轮核验过的 Addressables 文件，审计后才移到最终目录。构建机只发布 `Build(...)` 成功返回、具有完整 `delivery.json` 且长度和摘要仍吻合的文件清单，不按目录通配上传。失败的私有产物仍可能含明文，不可发布；没有 `delivery.json` 的目录不能与 Player 配对发布。

**应用启动。** 在 Unity 主线程、任何官方 Addressables 或 YL 消费者启动之前调用 `await EncryptedDeliverySession.StartAsync(dedicatedCipherCache, keys)`；只有它通过后才创建 `ResourceStartup`、开放业务入口。不要启用 YL 启动更新，不要注册额外 Locator、遮蔽已冻结的 Provider、设置自定义 Web 请求覆盖或替换内部地址转换。扩展会拒绝检测到漂移后的新 Provider 操作；Addressables 可能复用已有操作而不再调用 Provider，因此应用仍须保证配置不变，发现漂移时停止发起新请求。受保护资源只用异步加载，不调用 `WaitForCompletion()`。业务仍使用 `scope.LoadAsync<T>(key)`、`AcquireAsync<T>` 或官方异步句柄。关闭时先停止提交新请求，归还业务 Scope、Lease 和直接官方句柄（含预下载），待仍在执行的请求结束，再在主线程 `await session.CloseAsync()`；它会等待已开始的原生卸载和密钥归还，不必轮询内部状态。YL 的 `StopAsync()` 只排空 YL 自身，不归还别人的句柄。

官方和 YL 下载量只报告尚缺的密文字节，预下载只校验并保存密文，不访问密钥。应用需预先准备按 `keyId` 查找的本地密钥，通过 `EncryptedBundleKeyLease` 交付独立副本；`OpenReadKey` 从工作线程调用，不在此发网络请求。缺 K1 时已验证的 K1 密文仍可报零待下载，但不能加载；恢复正确 K1 无需重传。错钥报认证失败；发现缓存损坏时当前请求失败，明确重试后才重新获取。首版不自动跨版本清理私有密文目录。

加密针对 CDN 副本和应用磁盘缓存，不加密 Catalog，不提供 DRM、永久离线授权、发布者签名或在线回滚。能控制客户端的人仍可取得解密能力。Mono 与 IL2CPP 使用同一个 Win64 Tink 插件和 v1 密文格式，但不能混用不同构建的 Player、Catalog 和 `delivery.json`。旧 CNG 密文和旧缓存不能直接使用；从原始资源重新导出。

**停用或移除。** 扩展仍安装时，先移除 `Encrypted Bundle Delivery` Schema，恢复官方 Bundle Provider、普通 BuildPath / LoadPath 及构建和启动入口。删去应用和 Editor 程序集对扩展的引用后，再按需卸包。在新路径上重建匹配的普通 Player、Catalog 和内容；不要在旧加密发布 URL 上改发明文，也不让旧 Player 回退到明文。

命中磁盘缓存也不等于立即可用：实际挂载前仍要校验并完整认证大包，Unity 的随机读还可能产生额外读取。功能默认关闭；在实际项目中测过启动和切场景的资源组、确认等待预算后再启用。框架夹具的合成资源与进程内存采样不能代替业务性能或引擎内存峰值。

## 验收

验证首启、缓存重启、下载/断网/失败、Catalog 检查与显式更新、Locale 共存、资产/实例/场景归还及普通退出。记录真实 Unity/Addressables、平台、后端、裁剪和内容身份。Editor、Play Mode、内容构建、Player 构建与 Player 运行是不同证据，未运行和前置不足不得记为通过。
