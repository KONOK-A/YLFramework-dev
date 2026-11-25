# 内容交付与更新

> [资源管理](../Resource.md) · [作者构建](Authoring.md) · [安装](../../Installation.md)

内容交付建立在官方 Addressables 的 Catalog、内容状态、Bundle 缓存和 Provider 上。框架包安装、内容构建、上传、Player 构建与运行分别验证；安装包不会自动部署内容或初始化服务。

## 内置与远端内容

1. **配路径。** 随 Player 分发的组优先用默认 Local BuildPath / LoadPath，由官方构建集成放入 `StreamingAssets/aa`。远端组在官方 Groups / Profiles 中指定构建输出目录和 Player 可访问的 URL。如果需要在不重建 Player 的情况下更新目录，还须启用 **Build Remote Catalog**，配置其 BuildPath / LoadPath。
2. **构建配套内容。** 选择实际 Player 平台和 Profile，按[正式内容构建](Authoring.md#正式内容构建)归档本次官方状态。若显式构建内容后再构建 Player，选择 **Do not Build Addressables content on Player Build**，避免重建成不配套的版本。如果允许 Player 自动重建，则以 Player 实际使用的构建与其内容状态为准。
3. **部署并运行。** 上传远端组生成的 Bundle；启用远端 Catalog 时再部署对应 Catalog 和 hash。不要上传私有状态归档，也不把开发机输出绝对路径写进 Player。安装、Editor 的 **Use Asset Database** 和内容构建都不能证明 Player 能访问所配置的 URL。

只将官方 Addressables 制品当作当前资源输入。普通交付不依赖加密扩展、Windows 原生插件或旧私有资源快照。默认 SceneProvider 路径不支持自定义场景 Provider；其他 SDK 或 Provider 要分别核对平台、AOT、内容格式与释放。

## API 边界

| 入口 | 结果 | 不承诺的能力 |
|---|---|---|
| `GetDownloadSizeAsync(keys)` | 当前目录下的下载量。签名会话按签名依赖和已验证文件估算 | 不提供内存峰值或未来目录的下载量 |
| `DownloadDependenciesAsync(keys)` | 官方 Union 依赖下载完成 | 不等于加载业务资产或准备尚未生效的新版本 |
| `CheckForCatalogUpdatesAsync()` | 只读 ID 快照；官方尝试读取的远端 hash 缺失或读取失败时返回错误 | 不自动激活、清缓存或回滚 |
| `ApplyCatalogUpdatesAsync(catalogIds)` | 已登记版本变更返回 `true`，无变化返回 `false`；自定义 Locator 只判断是否替换 | 不校验内容字节，不是完整应用版本事务 |

keys/IDs 在调用时冻结，空输入不能表示“全部”。取消只结束调用者等待，已经开始的官方工作继续排空。控制操作串行，待真实完成、独立 pin 归还及完成回调栈结束后才启动下一轮；不会按句柄 struct 副本猜所有权。

## 启动更新窗口

YL 仅在应用启动时允许切换 Catalog：先更新，再开放业务资源、Core/Audio 与 Localization 绑定。官方 Apply 尚在途时，业务入口拒绝加载，即使调用者已取消等待。第一次业务加载会封闭窗口；同进程重启 YL 服务不能重新获得更新权限。运行中可检查版本并下载当前 Catalog 已知内容，新目录安排到下次应用启动。

受控更新须显式开启。下面是 Unity 主线程 async 方法体片段，业务 asmdef 引用 `YLFramework.Runtime` 并导入 `YLFramework.Resource`。`startup` 是应用字段；即使更新失败也不要丢弃它，应用按[停止与诊断](../Resource.md#停止与诊断)收尾后再决定退出或离线策略。

```csharp
var configuration = new ResourceConfiguration(enableStartupCatalogUpdates: true);
startup = new ResourceStartup(configuration, bindCoreResourceProvider: false);
await startup.StartAsync();
var resources = startup.Resources;
var ids = await resources.CheckForCatalogUpdatesAsync();
if (ids.Count != 0)
{
    await resources.ApplyCatalogUpdatesAsync(ids);
}
// 在这之后开启业务 Scope、Core/Audio 与 Localization 等消费者。
```

组合根须保证本方最先组织初始化，并协调外部 Init/Check/Update。启用时，在官方 Initialize 前通过官方 JsonAssetProvider/ResourceManager 读取同一路径的真实 `settings.json`，确认 `DisableCatalogUpdateOnStartup=true`。已有 locators、自定义 Internal ID 变换、Web 请求覆盖或替代 runtime JSON Provider 时，无法证明官方初始化会读到同一来源，因此拒绝受控更新；默认仅加载模式不接管这些配置。

公开 API 不能可靠检测另一个入口尚在途的初始化，因此不能宣称 YL 自动证明了全进程顺序。该选项不是额外配置数据库，也不能代替应用自己的初始化协调。外部已经初始化时仍支持加载共存，但不能接管该次启动更新。默认不启用时 Check/下载仍可用，Apply 明确拒绝。

Addressables 可能回退到缓存或内置目录。远端 hash 缺失、读取失败或与官方登记版本不一致时，YL 不把这次回退算作更新成功，也不撤销 Unity 已完成的 Locator 变更。应用决定重试、退出或明确的离线策略。YL 和 Localization 各归还自己的操作；YL 停止不清全局 locators、providers、缓存或外部 Handle。

## 归档与内容更新

首次发布保留该 Player 的官方 `addressables_content_state.bin`。后续按[内容更新构建](Authoring.md#内容更新构建)使用配套归档，部署所需新增或更改的 Bundle、Catalog 和 hash。旧客户端可能仍需要原 Bundle，不要先清空服务器内容。已装载的 HybridCLR 代码不能因资源 Lease 归还而卸载或回滚；基座与补丁使用所属包的交付流程。

官方 Catalog hash 只用于官方缓存与版本检查。

## 签名内容更新与重启回滚

会话由 `ResourceStartup` 在首次初始化 Addressables 前选定一份业务 Catalog。组合根先启动资源服务，再启用直接使用 Addressables 的功能和 Core、Audio、Localization 等消费者。每个进程只选一次，已有业务租约继续读取它所属的目录。普通内容只安装主包。加密内容在构建 A 时装入扩展，并在组合根向同一事务提供 `EncryptedResourceContentExtension`。

发布首个支持该协议的 A 时，在 Player 中预置受信 RSA-3072 公钥、应用、渠道、平台、兼容标识、启动配置摘要和发布来源。发布端签名覆盖清单的原始 UTF-8 字节，私钥留在发布端。安装目录的 `StreamingAssets/aa` 包含固定的 `settings.json`、A 的 Catalog 和本地制品，以及 `yl-content-manifest.json` 和分离签名 `yl-content-manifest.sig`。先按[签名版本发布](Authoring.md#签名版本发布)构建和核对这些文件，再构建 A。

Player 的 `ResourceContentManifestTrust` 接收内置公钥的 `RSAParameters`（模数、指数）和允许下载的 HTTPS 目录。公告 URL 也须位于受信目录。组合根可使用下面的主线程方法体。`trust`、`announcementUri` 和 `startup` 由应用持有。

```csharp
string storage = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "resource-content");
var updates = new ResourceContentUpdates(trust, announcementUri, storage);
startup = new ResourceStartup(configuration: null, bindCoreResourceProvider: true,
    audioProjects: null, contentUpdates: updates);
await startup.StartAsync();
var boot = updates.LastStartup;
var content = updates.ReadState();
// 此时可以开放业务，并按 boot.Status 和 boot.Rejection 展示启动结果。
```

| 阶段 | 入口与结果 |
| --- | --- |
| 检查并准备 | `CheckAsync()` 返回 `Available`、`NoUpdate` 或 `Rejected`。将 `Available` 结果交给 `PrepareAsync(candidate)`。准备验证必需制品并排定下次启动，本会话继续使用原资源。 |
| 冷启动选版 | `StartAsync()` 验证选中版本的本地制品、Catalog 和 Provider，完成原子提交后开放业务。应用用 `LastStartup` 区分激活、回退与正常启动。 |
| 处理未完成的尝试 | 提交前强退后，下一次启动服务原版。需要再试时调用 `RetryPreparedAsync()`，再重启。缺少或错误的内容钥会保住原版，补对新钥后可在下一次启动重验。 |
| 补齐可选章节 | 用 `GetDownloadSizeAsync(keys)` 查询指定业务键所需的远端字节。将已选清单中的制品路由交给 `PrepareActiveAsync(routes)` 下载并验证依赖。进程继续使用启动时选定的清单，可选文件缺失时仍可使用已就绪的章节。 |
| 显式回滚 | `RequestRollbackAsync()` 返回是否已排定上一版；在下次冷启动读取 `LastStartup`。成功会消耗上一版指针，最高成功序号和摘要继续保留。回滚被拒绝后需重新显式请求。 |
| 清理 | `CleanupAsync()` 清理本方可核对的无引用制品，保留当前版、上一版和仍待验证的候选。 |

发布端先上传签名指向的不可变制品，再将公告切换为 `{"payload":"…","signature":"…"}`。两个值分别是原始清单和分离签名的 Base64。新公告必须在有效期内。已准备的本地候选、当前版和上一版按原签名复核，可离线启动。低序号及同序号异清单会被拒绝；重发已成功提交的同一清单不产生新提交。发布更高序号可以继续更新。

应用须保留自有数据目录，包括按应用和渠道隔离的持久记录。记录损坏、当前版本不可读或官方初始化后的提交失败会关闭本次资源启动。保留现场并让用户退出重启，按具体启动错误诊断。普通旧安装继续使用前面的官方交付入口。签名模式的版本检查和激活统一走 `ResourceContentUpdates`。

## 可选的 Bundle 加密

`com.kano.ylframework.resource-encryption` 是 Windows x64 Release Player 的选装扩展，不是主包依赖，也不自动启用。一个 Catalog 可以同时包含 `Embedded`、`RemoteHttps` 和普通组。用扩展的构建入口交付完整目录；只分发 `Player/`，按清单上传 `Upload/` 中的文件，并保存初始版本 A 的官方内容状态和加密描述。不要把构建成功当成上传成功。

启动加密会话后再开放 Addressables 与 YL 消费者。纯随包或混合布局可用本地密钥离线读取随包章节；混合布局用 `PrepareRemoteAsync` 显式准备远端，之前保护远端项只报告保守下载量。退出前先归还业务及官方句柄。已按签名协议发行的 Player 可以通过主包事务更新和回滚加密内容。固定交付的应用仍按扩展原入口启动。v1 加密包不能直接读取；停用时恢复官方 Provider 和路径，重新构建，勿在旧密文 URL 发布明文。

先按[安装指南](../../Installation.md#选装远端资源加密)安装，需要组配置、密钥、示例和支持范围时打开扩展自带的 `Documentation~/README.md`，或阅读源码仓库当前的[远端资源加密手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.resource-encryption/Documentation~/README.md)。

## 验收

普通交付验证首启、缓存重启、下载/断网/失败、Locale 共存、资产/实例/场景归还及普通退出；使用远端 Catalog 更新时再验证检查与显式应用。记录实际 Unity/Addressables、平台、后端、裁剪和内容身份。Editor Play Mode、内容构建、Player 构建与 Player 运行是不同证据。

签名内容交付另行验证同一 Release Player 的准备、强退恢复、实际素材、重放和离线回滚。加密配置同时验证缺钥、错钥与恢复后的版本选择。
