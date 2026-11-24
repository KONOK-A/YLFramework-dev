# 资源加密使用手册

> [包入口](../README.md) · 源码仓库：[主包安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md#选装远端资源加密) · [主包资源交付](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource/Delivery.md)

本包为多个 Addressables 组交付加密 Bundle。密文可以随 Player 安装，也可以放到不可变 HTTPS 发布目录。Catalog 不加密；本包不提供 DRM、发布者签名、授权系统或内容版本事务。能控制客户端的人仍可取得解密能力。普通组继续走官方 Provider 和缓存。

## 前置与支持范围

先安装同一批次的主包和本包。调用运行时 API 的业务 asmdef 引用 `YLFramework.Resource.Encryption`（namespace 同名），使用 YL Resource 时同时引用 `YLFramework.Runtime`。构建代码放 Editor-only 程序集，引用 `YLFramework.Resource.Encryption.Editor`（namespace 同名）、`YLFramework.Resource.Encryption` 和 `Unity.Addressables.Editor`；直接使用官方运行时类型的代码按需引用 `Unity.Addressables`。

以下是 Windows x64 Release Player 的本地验证范围；不支持在 Editor Play Mode、Development Player、ARM64 或其他平台开启会话。Editor 程序集用于构建，不代表 Editor 可以加载密文。

| Unity | Addressables / 默认 Catalog | 后端 / 裁剪 |
| --- | --- | --- |
| `6000.3.27f1` | `2.10.3` / `catalog.bin` | Mono / Disabled，IL2CPP / Medium |
| `2022.3.62f3` | `1.25.0` / `catalog.json` | Mono / Disabled，IL2CPP / Medium |

## 配置与构建

1. 在项目默认 Addressables Settings 中，为每个受保护的 LZ4 组打开 `Include In Build`，添加 `Encrypted Bundle Delivery` Schema，选 `Embedded` 或 `RemoteHttps` 并填写组的 `keyId`。使用 `EncryptedAssetBundleProvider`，保留官方资产 Provider、带 hash 的 Bundle 名和 `Requested Asset and Dependencies`，关闭 `Use Asset Bundle Cache` 和 `Force Unique Provider`。组内使用显式的非场景资产。传递依赖默认受保护；公有依赖豁免需在对应组显式登记 GUID，且不能覆盖任何显式保护资产。随包资产的整个 Bundle 依赖闭包必须在安装内，即使远端依赖是通过一个普通本地包间接到达也不允许。
2. 启用 **Only update catalogs manually**（`DisableCatalogUpdateOnStartup`），最终关闭 **Build Remote Catalog**。若手动更新开关置灰，可先临时开启远端 Catalog 设置开关，勾选手动更新，再关闭远端 Catalog；不要在临时状态构建。选择 **Do not Build Addressables content on Player Build**，不要配置 Initialization Objects、**Compress Local Catalog** 或 **Extract TypeTree Data**。保留官方 `Library` / `Temp` 内部路径。激活 Windows x64 目标并保存要构建的场景；Unity 2022 若保留 `Built In Data` 的场景别名，Player 场景列表必须与已启用的 Build Settings 场景及顺序一致。
3. 选择尚不存在且不重叠的 `privateRoot`、`releaseRoot`，其父目录须已存在并位于同一 Windows 本地卷。为每个保护组指定 `privateRoot` 下不同的明文 BuildPath，官方内容状态也写在私有根内。普通远端组使用独立的新暂存目录、原有 URL 和带 hash 的 Bundle 名；普通本地组可保留官方 `NoHash`，但不允许文件名或路由冲突。`Embedded` 的 LoadPath 使用 `{UnityEngine.AddressableAssets.Addressables.RuntimePath}/yl-encrypted`；所有 `RemoteHttps` 组使用同一个不可覆盖的 HTTPS 发布子目录，末段与 `releaseRoot` 的目录名一致（即本次 `releaseId`）。实际保护包路由会加入组 GUID。
4. 由正式构建入口调用 `EncryptedBundleExporter.Build`，不要另用普通入口重建同一 Player 或 Catalog。`protectedGroups` 是全部启用的保护组，`options` 包含真实场景且 `locationPathName` 为 `<releaseRoot>/Player/<name>.exe`。`keys` 是构建应用显式提供的 `IEncryptedBundleKeySource`，Schema 不保存密钥。

```csharp
EncryptedBundleExporter.Build(settings, protectedGroups, options,
    applicationNamespace, privateRoot, releaseRoot, keys);
```

导出器校验 Win64 后端必需文件及官方 Player 构建报告记录的文件列表和大小；构建回调不得删改已记录的 Player 文件。只使用可信的应用回调：官方报告没有提供生成文件的原始摘要，不能用来识别任意等长替换。所有构建和密钥归还回调结束后，导出器复核来源和完整候选，再用一次同卷、不覆盖的目录改名产出 `releaseRoot`。用独占的发布账户和支持 Windows ACL 的本地卷；应用回调返回时须结束派生写入，不得改暂存区权限。暂存目录只授权给发布账户，改名不会把该权限变成安装权限；安装工具应另行复制 `Player/` 到目标安装目录。发布工具用预期的应用、版本及 URL 校验 `delivery.json` 和实物；只分发 `Player/`，只把 `uploads` 中列出的 `Upload/` 文件发到清单里的准确 URL（包括普通远端依赖）。服务器须原样返回完整密文（HTTP 200，不重定向、不添加内容编码）。确认所有上传对象齐全后再发 Player。不要上传私有明文、`ContentState/` 或调试产物。

把这套安装内容作为初始版本 A，保留其 `ContentState/addressables_content_state.bin`、Build Layout、清单，以及 `Player/.../StreamingAssets/aa` 中配套的 Catalog、settings 和 `yl-encrypted-bundles.json`。这些归档与游戏内容清单不能替代 UPM 发布回执。不要用另一次构建的内容状态替换 A 的归档。

## 应用启动与关闭

在 Unity 主线程、任何官方 Addressables 或 YL 消费者启动之前执行下面的 async 方法体片段。文件引用 `YLFramework.Resource` 和 `YLFramework.Resource.Encryption`。`keys` 是应用已准备好的本地密钥源，`dedicatedCipherCache` 是绝对的 Windows 本地缓存子目录（不要与普通缓存或另一进程共享），`delivery` / `startup` 是应用组合根持有的字段。

```csharp
delivery = await EncryptedDeliverySession.StartAsync(dedicatedCipherCache, keys);
startup = new ResourceStartup(bindCoreResourceProvider: false);
await startup.StartAsync();
// 两次启动均成功后，才向业务开放 startup.Resources。
```

`StartAsync` 不下载、解密或挂载业务资产。纯随包布局允许传 `null`，不接触远端缓存；纯远端布局要等专用缓存占有及无钥扫描完成。混合布局先开放本地章节，缓存路径无效或被另一 Player 占用也不阻止读取。在 Unity 主线程 `await delivery.PrepareRemoteAsync(token)` 成功后，`IsRemoteReady` 才为 `true`，并允许保护远端组预下载和加载。准备失败可以显式重试；取消某个等待者不取消其他等待者。普通远端组不由此开关管理。

应用按精确 `keyId` 在 `OpenReadKey` 提供 `EncryptedBundleKeyLease`（复制一份 32 字节密钥），缺失时返回 `null`。要让第一章离线，应用须能不联网提供该章及其保护依赖的密钥。取钥和归还均可能在工作线程发生，不在其中请求网络、调用 Unity API 或记录密钥。

加载仍用 `scope.LoadAsync<T>(key)`、`AcquireAsync<T>` 或官方异步 API；受保护资源不调用 `WaitForCompletion()`。不要在会话中添加 Locator、更换 Provider 或覆盖 Web 请求 / 内部地址转换。冻结的是本次会话选定的内容版本；目前入口从安装内的 A 启动，不包含后续版本的选择、激活或回滚。Provider 会拒绝检测到漂移的新操作，但官方可能复用既有操作。会话进入验证后若启动失败，修正输入后重启 Player。

应用停止新请求并归还消费者持有的 Scope、Lease 和直接 Addressables 句柄（含预下载），再等待 `startup.StopAsync()`，最后在主线程等待 `delivery.CloseAsync()`。已启动加密会话但后续初始化失败时同样需要有序收尾。`CloseAsync()` 在仍有受保护句柄或加密 Provider 工作在途时拒绝关闭，归还并等其结束后再调用；接受后等待原生卸载和密钥归还。原生卸载失败或未完成时不强行释放仍可能被 Unity 读取的流，不能把它当作关闭成功。

## 缓存、错误与重新交付

- 随包资源估算始终为零，不代表文件完好；混合布局未准备远端时，保护远端项保守报告整包大小。实际预下载只检查或保存密文，不取钥、不挂载。缺钥不意味着要重下；修复密钥后可用同一份有效密文。
- 挂载前从稳定只读句柄完整验证密文、AEAD 与明文摘要，Unity 回读仍走同一受保护流。损坏随包文件不会删除、改写或向 CDN 找替代，应修复安装。远端损坏使本次请求失败，后续独立请求才可重试。
- Mono 与 IL2CPP 使用相同 v2 绑定。不能混用不同构建的 Player、Catalog、描述和清单；v1 或旧 CNG 密文不兼容。改变绑定或密钥要重建到新地址；不要覆盖已交付 URL，更不能在其上改发明文。

## 停用或移除

在扩展仍安装时，先移除加密 Schema，恢复官方 Bundle Provider、普通 BuildPath / LoadPath 以及标准构建和启动入口。移除应用和 Editor 上对扩展的程序集引用后再卸包。在新路径构建匹配的普通 Player、Catalog 和内容；不得在原加密 URL 改发明文，也不让旧 Player 回退到明文。

## 原生后端与来源

只使用本包提供的 Win64 原生后端；同名替换文件并不等同于受验证制品。后端身份见 [NativeBackend.json](NativeBackend.json)，第三方许可见 [Tink](Tink-LICENSE.txt)、[BoringSSL](BoringSSL-LICENSE.txt) 和 [Abseil](Abseil-LICENSE.txt)。上方跨包链接指向源码仓库当前文档，不要求 UPM 包目录相邻。
