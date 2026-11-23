# 远端资源加密使用手册

> [包入口](../README.md) · 源码仓库：[主包安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md#选装远端资源加密) · [主包资源交付](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource/Delivery.md)

本包给一个远端 Addressables 组的 Bundle 加密，保护 CDN 副本与应用磁盘缓存。Catalog 不加密；本包不提供 DRM、发布者签名、永久离线授权或在线回滚。能控制客户端的人仍可取得解密能力。普通组继续走官方 Provider 和缓存。

## 前置与支持范围

先安装同一批次的主包和本包。调用运行时 API 的业务 asmdef 引用 `YLFramework.Resource.Encryption`（namespace 同名），使用 YL Resource 时同时引用 `YLFramework.Runtime`。构建代码放 Editor-only 程序集，引用 `YLFramework.Resource.Encryption.Editor`（namespace 同名）、`YLFramework.Resource.Encryption` 和 `Unity.Addressables.Editor`；直接使用官方运行时类型的代码按需引用 `Unity.Addressables`。

以下是 Windows x64 Release Player 的本地验证范围；不支持在 Editor Play Mode、Development Player、ARM64 或其他平台开启会话。Editor 程序集用于构建，不代表 Editor 可以加载密文。

| Unity | Addressables / 默认 Catalog | 后端 / 裁剪 |
| --- | --- | --- |
| `6000.3.27f1` | `2.10.3` / `catalog.bin` | Mono / Disabled，IL2CPP / Medium |
| `2022.3.62f3` | `1.25.0` / `catalog.json` | Mono / Disabled，IL2CPP / Medium |

## 配置与构建

1. 在项目的官方默认 Addressables Settings 中，只选择一个远端 LZ4 组。打开 `Include In Build`，添加 `Encrypted Bundle Delivery` Schema，指定 **Asset Bundle Provider** 为 `EncryptedAssetBundleProvider`，保留官方资产 Provider、带 hash 的 Bundle 名和 `Requested Asset and Dependencies` 加载方式，关闭该组的 `Use Asset Bundle Cache`。组内使用显式的非场景资产；传递依赖默认受保护。Schema 只保存 `keyId` 和显式允许公开的依赖 GUID，不保存密钥。
2. 启用 **Only update catalogs manually**（`DisableCatalogUpdateOnStartup`），最终关闭 **Build Remote Catalog**。若手动更新开关置灰，可先临时开启远端 Catalog 设置开关，勾选手动更新，再关闭远端 Catalog；不要在临时状态构建。选择 **Do not Build Addressables content on Player Build**，不要配置 Initialization Objects、**Compress Local Catalog** 或 **Extract TypeTree Data**。保留官方 `Library` / `Temp` 内部路径。激活 Windows x64 目标并保存要构建的场景；Unity 2022 若保留 `Built In Data` 的场景别名，Player 场景列表必须与已启用的 Build Settings 场景及顺序一致。
3. 选择尚不存在、互不重叠的私有暂存根、Player 输出目录和密文输出目录，其父目录须已存在。私有暂存根与 Player 目录须在同一 Windows 本地卷。所选组的 BuildPath 与官方内容状态目录都必须位于私有暂存根内；普通远端组用独立的新暂存目录，发布 URL 也不能与受保护 URL 重叠。所选组的 LoadPath 指向新的、不可覆盖的 HTTPS 发布子目录，末段须与密文输出目录名相同。
4. 由项目的正式构建入口调用 `EncryptedBundleExporter.Build`，不要另用普通内容构建或在此后单独重建 Player。下面是 Editor 方法体片段：`settings` 是项目当前默认设置，`protectedGroup` 是该组，`options` 是含真实场景与绝对 exe 路径的 Win64 Release `BuildPlayerOptions`，`applicationNamespace` 是应用固定命名空间，`privateRoot` / `releaseRoot` 是上述绝对目录，`keys` 是构建端从 CI 秘密存储预备的 `IEncryptedBundleKeySource`。

```csharp
EncryptedBundleExporter.Build(settings, protectedGroup, options,
    applicationNamespace, privateRoot, releaseRoot, keys);
```

构建正常返回后，核对密文输出中 `delivery.json` 的文件清单、字节长度和摘要，只把列出的密文上传到对应 HTTPS 目录。服务器直接返回完整字节（HTTP 200，不重定向、不做内容编码）。普通远端组的制品仍按其独立的 BuildPath / LoadPath 交付；绝不上传加密组明文、官方 Build Layout 或内容状态。Player 自带配套 `StreamingAssets/aa` 下的 Catalog、设置和 `yl-encrypted-bundles.json`；它不是 CDN 端的 `delivery.json`。失败输出或没有完成清单的目录不发布。

## 应用启动与关闭

在 Unity 主线程、任何官方 Addressables 或 YL 消费者启动之前执行下面的 async 方法体片段。文件引用 `YLFramework.Resource` 和 `YLFramework.Resource.Encryption`。`keys` 是应用已准备好的本地密钥源，`dedicatedCipherCache` 是绝对的 Windows 本地缓存子目录（不要与普通缓存或另一进程共享），`delivery` / `startup` 是应用组合根持有的字段。

```csharp
delivery = await EncryptedDeliverySession.StartAsync(dedicatedCipherCache, keys);
startup = new ResourceStartup(bindCoreResourceProvider: false);
await startup.StartAsync();
// 两次启动均成功后，才向业务开放 startup.Resources。
```

`StartAsync` 校验并初始化随 Player 交付的固定 Catalog，不下载业务资产，也不保证密钥可用。应用按精确 `keyId` 在 `OpenReadKey` 提供 `EncryptedBundleKeyLease`（复制一份 32 字节密钥），缺失时返回 `null`。运行时该调用可发生在工作线程，不在其中发起网络取钥或调用 Unity API；可选归还回调也不保证在主线程。密钥不得写入项目配置或日志。

加载仍用 `scope.LoadAsync<T>(key)`、`AcquireAsync<T>` 或官方异步 API；受保护资源不调用 `WaitForCompletion()`。不启用 YL 启动更新，不添加 Locator、改变已冻结 Provider 或覆盖 Web 请求 / 内部地址转换。Provider 会拒绝检测到漂移的新操作，但官方可能复用既有操作；应用发现漂移时须停止新请求。会话进入固定目录校验后，即使启动失败也不要在同进程尝试重开；修正输入后重启 Player。

应用停止新请求并归还消费者持有的 Scope、Lease 和直接 Addressables 句柄（含预下载），再等待 `startup.StopAsync()`，最后在主线程等待 `delivery.CloseAsync()`。已启动加密会话但后续初始化失败时同样需要有序收尾。`CloseAsync()` 在仍有受保护句柄或加密 Provider 工作在途时拒绝关闭，归还并等其结束后再调用；接受后等待原生卸载和密钥归还。原生卸载失败或未完成时不强行释放仍可能被 Unity 读取的流，不能把它当作关闭成功。

## 缓存、错误与重新交付

- 官方及 YL 的下载量只报告尚缺的密文字节，预下载仅校验并保存密文，不访问密钥。有效密文缓存即使缺钥也可报零待下载，但仍不能挂载；恢复正确密钥无需重传。错钥报认证失败，密文损坏使本次请求失败，明确重试才重新获取。
- 命中缓存仍需在挂载前对大包完整认证；Unity 随机读还可能增加读取。启用前用实际资源测试首启、缓存重启、断网、错钥/文件损坏、归还与退出，测量应用的加载预算。缓存不会自动跨版本清理。
- Mono 和 IL2CPP 使用相同的 v1 密文格式，但不同构建的 Player、Catalog、清单不能混用。改变内容、密钥或绑定数据时用新发布目录和配套 Player；同一 URL 只能重发完全相同的制品。旧 CNG 密文和缓存不能直接使用，从源资产重新构建。

## 停用或移除

在扩展仍安装时，先移除加密 Schema，恢复官方 Bundle Provider、普通 BuildPath / LoadPath 以及标准构建和启动入口。移除应用和 Editor 上对扩展的程序集引用后再卸包。在新路径构建匹配的普通 Player、Catalog 和内容；不得在原加密 URL 改发明文，也不让旧 Player 回退到明文。

## 原生后端与来源

只使用本包提供的 Win64 原生后端；同名替换文件并不等同于受验证制品。后端身份见 [NativeBackend.json](NativeBackend.json)，第三方许可见 [Tink](Tink-LICENSE.txt)、[BoringSSL](BoringSSL-LICENSE.txt) 和 [Abseil](Abseil-LICENSE.txt)。上方跨包链接指向源码仓库当前文档，不要求 UPM 包目录相邻。
