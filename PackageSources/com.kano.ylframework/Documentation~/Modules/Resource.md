# 资源管理

> [文档首页](../README.md) · [安装](../Installation.md) · [资源制作与构建](Resource/Authoring.md) · [资源交付与更新](Resource/Delivery.md) · [音频播放](Audio.md)

ResourceSystem 管理 Catalog、共享加载、Asset/Instance/Scene 租约和版本事务。应用通过 `ResourceStartup` 显式启动并持有它，业务只消费自己的租约；归还责任与等待请求完成是两件事。

## 阅读路径

| 要完成的任务 | 入口 |
|---|---|
| 跑通样例、初始化、加载资产/实例/场景、连接 Core/Audio、排查 Busy | 本页 |
| 配置 Project/目录/Group、工作台、图集、地址常量、构建与本地发布 | [资源制作与构建](Resource/Authoring.md) |
| 内置部署、远端更新、Store 查询、内容加密、恢复、平台限制 | [资源交付与更新](Resource/Delivery.md) |

Runtime namespace 为 `YLFramework.Resource`，Editor 为 `YLFramework.Editor.Resource`，分别属于主包 `YLFramework.Runtime` / `YLFramework.Editor`。一个 Play/Player 会话只有一个 ResourceSystem 权威。读取 Current、构造配置、打开工作台都不初始化；资源操作与 Unity 对象访问在主线程，只读配置不代表线程无关。

## 最小接入

随包的 [`Samples~/ResourceStartup/`](../../Samples~/ResourceStartup/) 提供完整 owner，包括失败、取消、停用与关闭重试。先在 Package Manager 的 Samples 显式 Import `Resource Startup`，样例默认不参与包编译；Runtime/Editor 分 asmdef。导入副本位于使用工程 `Assets/Samples/<包显示名>/<版本>/<样例名>/`，生成数据也归使用工程，导入新副本前核对已修改内容和同名程序集。

1. 在无脏场景的 Edit Mode 执行 `YLFramework/Samples/Create Resource Startup Example`。生成唯一的 `Assets/YLResourceStartupExample[后缀]/`，含 Text/WAV/Data、Project 和独立 Scene，使用新 GUID，不替用户保存原脏场景。
2. 进入 Play，选中样例组件，Mode 保持 EditorSimulation，点 `Run Selected Mode`。同一个 Run 读取 message/data 并按选项连接 Core/Audio；加载 Scene 或 Inspector 不自动启动。
3. 预期显示 `Passed: Hello from ResourceStartup / Explicit compiled sample type`。ProjectId/Core/Audio 选项在启动时冻结；Sound 被偏好或接纳策略拒绝时，处理应用设置或关闭 Use Audio，样例不强开全局偏好。
4. 退出 Play 并保存样例 Scene，点 `Build And Publish Example Bundle`。发布结果写入 snapshotDirectory/catalogDigest；显式改为 Bundles 并保存，再用同一运行按钮。实际平台门禁见[平台与后端限制](Resource/Delivery.md#平台与后端限制)。

仅运行 Simulation 不需先构建。需要 Player/内置交付时再按[内置部署](Resource/Delivery.md#内置部署)操作；Player 不能用 AssetDatabase/Simulation 补缺，也不能读取开发机发布目录。

## 初始化与关闭

### 选择配置

| 来源 | 配置入口 | 约束 |
|---|---|---|
| 仅绝对本地/URL 图片 | `new ResourceConfiguration()` | 无 Catalog 也须显式 Start |
| Resources 内存目录 | `new ResourceConfiguration(catalogs, images)` | 明确提供目录，不扫描工程设置 |
| 正式 Bundle | `ResourceConfiguration.Create(ResourceLoadMode.Bundles, mounts, cacheRoot, images, decryption)` | 明确完整项目闭包、版本/平台/App 与编译类型 |
| Editor 模拟 | `ResourceEditorSimulation.CreateConfiguration(...)` | 仅 Editor，必须显式选择，不作 Bundle 失败回退 |

Bundle 使用 ResourceProjectMount。无 cacheRoot 时每项目必须有完整本地快照且无 remote；有 cacheRoot 才装配 ArtifactStore。创建配置不挂载、不建缓存目录、不下载。Simulation 不接受 cacheRoot，可用当前 Project，或完整版本闭包冻结身份。Runtime Create 只接受 Bundles，不存在隐式 Auto。

应用可以用以下完整方法构造**本地发布**启动对象；参数来自应用选中的确切版本，不扫描最新目录：

```csharp
using System;
using System.Collections.Generic;
using YLFramework.Resource;

public static class ApplicationResources
{
    public static ResourceStartup Configure(string absoluteVersionDirectory,
        string projectId, string digest, string platform, string appId,
        IEnumerable<Type> compiledTypes = null,
        bool bindCore = false, bool bindAudio = false)
    {
        var snapshot = ResourceLocalSnapshot.Open(absoluteVersionDirectory,
            projectId, digest, platform, appId);
        var mount = new ResourceProjectMount(projectId, platform, appId,
            snapshot, assetTypes: compiledTypes);
        var configuration = ResourceConfiguration.Create(
            ResourceLoadMode.Bundles, new[] { mount });
        return new ResourceStartup(configuration, bindCore,
            bindAudio ? new[] { projectId } : null);
    }
}
```

### 持有启动责任

以下是业务协程内的成功路径片段，startup 由应用字段持有。它不是完整的 MonoBehaviour，失败/停用处理请对照随包样例：

```csharp
using UnityEngine;
using YLFramework.Resource;

using (var start = startup.StartAsync())
{
    yield return start;
    start.TakeResult();
}
using (var request = startup.Resources.AcquireAsync<TextAsset>(
    new ResourceAddress("game", "text/welcome")))
{
    yield return request;
    using var text = request.TakeResult();
    Debug.Log(text.Asset.text);
}
using (var stop = startup.StopAsync())
{
    yield return stop;
    stop.TakeResult();
}
```

TakeResult 失败会抛错，不能依赖代码自然走到末尾关闭，也不能 yield break 后丢弃启动对象。应用必须在失败、取消、停用时归还自己的请求/租约，并保留 startup 执行或重试 StopAsync。Unity 停协程不保证执行 iterator finally，owner 应有显式退出接点。

- 配置身份按**同一对象引用**判断；同配置 join，不同配置 ConfigurationConflict，不要重复构造来假装 join。
- StartAsync 成功 TakeResult 后使用 Resources，按需读取 CoreProvider/AudioProviders。Dispose 启动请求不等于 Stop。
- OwnsResourceSystem 区分自建/借用；借用会话 Stop 只归还本入口绑定，不关闭他人会话。自建会话被其他 startup/live owner 阻塞则 Busy，可恢复、不强卸载。
- 装配失败只结算本入口责任；自建会话被独立 owner 阻塞时 Resources 仍保留，归还后重试 Stop，不能丢掉关闭责任。
- Core/Audio 默认不接入。Core 外部绑定冲突失败，归还只解绑自身引用，不清后来替换者。Audio 装配不启停 AudioKit，不改其开关、路由或缓存。
- 取消 Start 等待者不伤同入口其他等待者；取消 Stop 只退出等待，已开始 native 排空继续。旧会话失效后先 Stop 原对象，再显式重新启动。

## 资产请求与租约

ResourceAddress 由稳定 ProjectId、精确逻辑地址和可选 SubAsset key 组成，不是 Bundle 文件名。完整身份共享 producer，每个成功消费者取得独立责任；同步遇在途返回 InProgress，不忙等、不重复 IO。别名解析回同一规范资产，不另建资源身份。

| 需求 | API 与结果 |
|---|---|
| 主资产/子资产 | `Acquire<T>` / `AcquireAsync<T>(address)`，`AssetLease<T>` |
| Bundle 内 typed 批量 | `AcquireAllAsync<T>(projectId, bundleId)`，`AssetBatchLease<T>`；仅 Catalog 登记的可赋值成员，bundleId 非磁盘路径 |
| 普通 Group | `AcquireGroupAsync(projectId, groupId)`，`AssetBatchLease<UnityEngine.Object>`，不加载 Scene |
| 混合组预加载 | `PreloadGroupAsync(projectId, groupId)`，`ResourceGroupLease` |

Resources Catalog 的 Locator 是相对 Resources、无扩展名的路径，不能是 Assets/ 物理路径。该来源不支持子资产选择或显式依赖执行，不能套用 Bundle 子资产规则。正式 Catalog 的编译类型必须明确提供 `typeof(...)`，不按远端字符串反射创建，不扫描全部资产。

### 请求交割

`ResourceRequest<T>` 是 `CustomYieldInstruction`；`yield return request` **只等待终态**，拥有型结果由 TakeResult 恰一次交割。未领取时 Dispose 归还结果；领取后 Dispose 请求不释放已交给调用方的 lease。Status/IsDone、结果是否已领取和 IsDisposed 是不同事实，Progress 不能代替 IsDone。终态后订阅 Completed 可同步通知。

取消只结束本消费者等待，不误杀他人。迟到 native 结果仍有清理 owner；资源系统会话退出会失效旧请求，不让旧结果进入新会话。Resources/AssetDatabase 是共享 borrowed 来源，归零只 drop 记录，不 UnloadAsset/Destroy；系统只销毁自己创建的对象。Bundle 则等最后资产/依赖 owner 归还后真实 Unload。

### 聚合自己的责任

```csharp
using (var scope = resources.CreateScope())
{
    var request = scope.AcquireAsync<TextAsset>(new ResourceAddress("game", "text/welcome"));
    yield return request;
    var text = scope.TakeResult(request); // 领取并纳入此 Scope。
    Debug.Log(text.Asset.text);
} // 先取消自有请求，再归还接管的租约。
```

ResourceScope 只聚合移交给它的责任，不是第二缓存。Track 仅跟踪请求，不自动接管通过 request.TakeResult 领取的结果；应使用 scope.TakeResult 或显式 scope.Own(lease)。Own 转移释放责任，不新增 retain；关闭后不继续借用 Asset。Scope 不能代替应用关闭 ResourceStartup，也不能因 Dispose SceneLease 就声称 Scene 已卸载。

PreloadGroupAsync 持有期间，普通成员取得资产，SceneBundle 仅准备 Bundle，Simulation ScenePreview 仅验证路径，均不加载 Scene。required 失败整批回滚，optional 保留 cause；普通 AcquireGroupAsync 不替代混合组预加载。

## 实例与场景

### 实例借出

Instantiate/InstantiateAsync 返回 InstanceLease，通过 GameObject 属性借读。Return/Dispose 正常回原缓存，Discard 销毁损坏/失败实例；归还后不再持有对象使用权，旧 lease 不能操作下一次 checkout。

实例缓存复用 Foundation 桶，默认 SceneScoped，可通过 GetInstanceCache 显式管理。桶退休后的晚归还实例销毁；active/idle/destroy-pending 真正结束前均保留 Prefab 依赖图。`ClearInstanceCaches()` 退休调用时本系统开放桶的快照并清理其 idle，不撤销已借出身份；回调新建桶不属于旧快照，不全局清池。业务仍须归还已借出实例，晚归还不再入旧桶。直接 prefab、Attach 或场景实例不自动转交 Resource 所有权。

### 场景加载与卸载

```csharp
// 位于业务协程中，resources 已启动。
using var request = resources.LoadSceneAsync(
    new ResourceAddress("game", "world/room"),
    UnityEngine.SceneManagement.LoadSceneMode.Additive);
yield return request;
using var scene = request.TakeResult();
Debug.Log(scene.Scene.path);

// 业务结束时显式卸载，并等待真实完成。
using var unload = resources.UnloadSceneAsync(scene);
yield return unload;
unload.TakeResult();
```

LoadSceneAsync 使用真实 handle/请求序号，重复 Additive 是独立意图。activateOnLoad=false 时用 SceneRequest.Activate 放行；门控默认 30 秒、范围 `(0,300]`，从 native 开始计时，到期失败并放行，不能无限堵住 Unity async queue。

**SceneLease.Dispose 不卸载 Scene，也不释放 SceneRecord 的 Bundle 图。**需要显式 UnloadSceneAsync(lease/record)，等真实 sceneUnloaded。可在归还 caller lease 前保存 Record，或通过 GetSceneRecords 查询；已放弃 lease 不再用于读 Scene。

Single native 开始后取消不能恢复旧 Scene；最后一个 Scene 卸载可能 Busy。外部同 path 并发引起归属歧义时保守保活，不通过短名猜身份。Unity 2022.3 特定同短名跨 path Scene Bundle 组合存在限制，不承诺该组合或 native 多版本热共存。

## 图片加载

AcquireImage/AcquireImageAsync 接受完整 HTTP(S) URL 或绝对本地 PNG/JPEG。冷 URL 同步 Unsupported，在途 InProgress，Ready 可同步取得；同步本地读取不占异步队列。

ResourceImageOptions 默认编码上限 32MiB、16M 像素；idle LRU 为 128 项/32MiB RGBA 估计/30s TTL；并发默认 4，可选 1..32，FIFO 而非 priority。活跃 owner 不淘汰，普通 Catalog Texture 不进这个 idle 缓存；ClearImageCache 仅清 idle。Refresh 不保证绕过同 URL 缓存，换代应使用版本 URL。

ResourceImage 绑定 Image/RawImage，SetImage(location/address) 持自己的 Request/Lease，刷新、替换、停用、销毁按代次收尾；临时 Sprite 自毁、placeholder 借用。图集消费使用 `SetAtlasSprite(atlasAddress,name)`，非 UI 用 `AcquireAsync<SpriteAtlas>`。GetSprite 返回的克隆由消费者销毁，再归还 Atlas Lease；不能 Destroy 共享 Texture。默认编译类型含 SpriteAtlas，内部打包 Texture 不公开为子资产。

图集必须先完成制作与准备，见[图集生产](Resource/Authoring.md#图集生产)。当前没有 Variant、全局 atlasRequested 或 Prefab Sprite 透明晚绑定；未知 native 图集布局明确拒绝，生成成功不等于目标 Player 像素/生命周期验证通过。

## Core 与 Audio

ResourceStartup 的可选 Provider 装配成功后再启动命名 Module；直接 InitializeAsync 的高级调用方自行显式绑定，仍使用同一个内核。Core 的 Module.ProjectName 对应 Resource ProjectId；直接 prefab、Attach、默认 Canvas/EventSystem 不要求资源初始化，Module Close 不自动卸外部 Scene。

```csharp
using YLFramework.Audio;

var address = new AudioClipAddress(resources.GetAudioClipProvider("game-audio"), "ui/click");
AudioKit.PlaySoundNamed(address);
```

ResourceAudioClipProvider 每次成功交付独立 Clip 租约，由 Audio 播放、尾轨和缓存持有；直接 Clip 仍 borrowed。桥接在同一 Runtime 的 [`Integration/Audio/`](../../Runtime/Systems/Resource/Integration/Audio/)，ResourceSystem.Audio.cs 属于 YLFramework.Resource，ResourceAudioClipProvider.cs 属于 YLFramework.Audio。同 System/Project 复用 Provider 引用，关闭后失效，缺失项目会使启动装配失败并按本入口回滚，不是可选安装包或另一资源服务。

AudioClipLibrary 的 Resource 条目只使用 projectId/key，当前来源为 Clip=0/ExternalProvider=3/Resource=4，没有隐藏 BundleName 字段；配置及拒选规则见[音频素材库](Audio/Resources.md#素材库)。

配置数据接入时，将应用实际类型通过 ResourceProjectMount 的 assetTypes 参数显式提供，包括具体根/直接加载条目类型；可选 Localization/DataAssets 的登记句柄持有根租约，业务先停 UI 再归还句柄，条目借用不得超过根生命周期，不把领域依赖写进 Resource 默认类型表。完整流程见源码仓库当前的 [Localization 手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.localization/Documentation~/README.md)与 [DataAssets 手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.data-assets/Documentation~/README.md)。

## Core 场景控制

YLFramework.Core.SceneController 是 Module 的轻量场景加载 Controller，不是另一 SceneManager。先经 ResourceStartup.bindCore 或显式初始化后 BindCoreResourceProvider；Root、监听组件或读取 Controller 不代为启动 Resource。

```csharp
// module 仍在有效注册期，ProjectName 对应 Resource ProjectId。
var controller = module.LoadController<YLFramework.Core.SceneController>();
using var request = controller.LoadSceneAsync("world/main",
    UnityEngine.SceneManagement.LoadSceneMode.Single);
yield return request;
if (request.Status != YLFramework.Core.CoreResourceStatus.Succeeded)
    throw (System.Exception)request.Error ?? new System.InvalidOperationException("Scene load ended: " + request.Status);
Debug.Log(request.TakeResult().Scene.path);
```

- 默认项目为 Module.ProjectName，另有 LoadSceneAsync(projectId,address,mode)，地址为 Catalog 逻辑地址/alias。无同步入口、隐式已加载复用或内置 LoadingPanel；UI 读场景请求 Progress，不用 Panel 资源进度代替。
- 同 Controller 的 Ordinal `(projectId,address)` 在途键不区分 mode，重复抛 CoreResourceException(InProgress)，不再调用后端；终态后可重投，Additive 为新意图。不同 Controller/项目/地址独立，alias 按提交字符串，不合并解析到同资产的不同 alias。
- 返回后端原 `CoreResourceRequest<CoreSceneResult>`，保留状态、进度、错误、Completed 和单次 TakeResult；终态 Completed 可同步通知。完成只摘 Controller tracking，不提前领取/Dispose 结果，请求由调用方释放。
- CancelPendingLoads 先分离在途快照再逐项 Cancel/Dispose，回调中新提交属于下一轮。OnExit 先拒绝新投递再清原责任，同实例重新注册是新代次；清理异常聚合报告，不声称已全部释放或自动重试，交付失败上下文在 Exception.Data 的 YLFramework.SceneController.Context。
- 关闭 Module/Controller 只结束在途请求，不卸已成功 Scene，不清其他 owner；Single 开始后取消不保证回滚。CoreSceneResult 没有卸载权，需要 Resource/应用 owner 以 SceneRecord/SceneLease 显式卸载，不让 Core 反向依赖具体 ResourceSystem。
- 仅注册主线程使用；无 Provider 为 Unavailable，退出中的 Module 或旧 Controller 拒绝请求，不换来源或配置补救。

## 错误与诊断

GetDiagnosticsReport 按需复制 owner、producer/waiter、IsQueued、Bundle 根 pin/入边、实例、Scene 与版本责任，不持 Unity 对象或触发初始化。诊断历史默认关闭，经 ConfigureDiagnosticsHistory 显式配置；工作台采样和磁盘查询见[资源工作台](Resource/Authoring.md#资源工作台)。

| 结果 | 处理 |
|---|---|
| ConfigurationConflict | 复用同配置对象，核对 Resource/Core 绑定，不强制重置外部会话 |
| Busy | 查 owner，归还自身 Lease/实例/Scene/Audio 或其他 startup，再重试，不全局强清 |
| AddressNotFound / TypeMismatch | 检查 ProjectId、逻辑地址、子资产 key、compiledTypes，不以磁盘名代地址 |
| 快照/兼容失败 | 核对 digest、平台、完整 Unity 版本、App 与文件，不改 Simulation 掩盖问题 |
| StaleBase | 当前版本变化，重新 Check/Prepare；回退要显式 Rollback |
| SessionUnavailable | 原请求/Provider 不绑新会话，结算旧 startup 后显式重启 |
| Unsupported | 检查模式、平台、来源和 Store 配置，不自动忽略或降级 |
| ContentKeyUnavailable | 补齐应用 Provider 的项目/KeyId 后显式重试，不判文件损坏或重下载 |
| ContentAuthenticationFailed / ContentFormatUnsupported | 拒绝故障输入闭包的新取得，核对交付/模式/key，不回退明文 |
| ContentBufferUnavailable | 归还自有输入再重试，或下次启动配置足够预算，不自动扩容/驱逐活跃输入 |

构建路径、作者配置与生成错误见[构建与发布](Resource/Authoring.md#构建与发布)；部署事务、控制格式、签名和内容故障恢复见[资源交付与更新](Resource/Delivery.md)。不能把一种包装错误当作特定底层门禁的证明。

## 实现入口

- [`ResourceStartup.cs`](../../Runtime/Systems/Resource/Integration/ResourceStartup.cs)、[`ResourceConfiguration.cs`](../../Runtime/Systems/Resource/ResourceConfiguration.cs)、[`ResourceSystem.cs`](../../Runtime/Systems/Resource/ResourceSystem.cs)：显式配置、权威与应用接入。
- [`ResourceRequest.cs`](../../Runtime/Systems/Resource/Loading/ResourceRequest.cs)、[`Lifetime/`](../../Runtime/Systems/Resource/Lifetime/)：等待、单次交割与 Scope。
- [`Instances/`](../../Runtime/Systems/Resource/Instances/)、[`Scenes/`](../../Runtime/Systems/Resource/Scenes/)、[`ResourceImage.cs`](../../Runtime/Systems/Resource/Integration/ResourceImage.cs)：实例、场景与图片责任。
- [`SceneController.cs`](../../Runtime/Core/MVC/Controllers/SceneController.cs)：Core 场景桥接与在途跟踪。
- [`ResourceStartupExample.cs`](../../Samples~/ResourceStartup/Runtime/ResourceStartupExample.cs)：完整业务、失败与退出 owner 示例。
