# 资源管理（Resource）

> [文档首页](../README.md) · [架构总览](../Architecture.md) · [Audio](Audio.md)

本文介绍正式 `ResourceSystem` 的配置、加载、构建和交付。普通本地 Bundle、远端更新与内容加密的平台限制不同，见 [共存与范围](#共存与范围)；不提供旧资源后端兼容入口或旧资源栈 fallback。

## 本页导航

- [正式入口](#正式入口) · [初始化与持有](#初始化与持有) · [可运行最小接入](#可运行最小接入)
- [实例与场景](#实例与场景) · [Core 与 Audio](#core-与-audio) · [Core SceneController](#core-scenecontroller)
- [图片与图集](#图片与图集) · [自动 SpriteAtlas](#自动-spriteatlas) · [预加载与诊断](#预加载与诊断)
- [资源工作台](#resource-workbench) · [多目录与加载地址](#多目录与加载地址) · [可共享构建配方](#可共享构建配方)
- [交付与挂载](#交付与挂载) · [安全内置部署](#安全内置部署) · [Store 只读查询](#store-只读查询)
- [代码生成](#代码生成) · [常见错误](#常见错误) · [可选 Bundle 内容加密](#可选-bundle-内容加密) · [轮换与交付](#轮换与交付)
- [共存与范围](#共存与范围) · [历史提示](#历史提示)

## 正式入口

`YLFramework.Resource.ResourceSystem` 是每个 Play/Player 会话唯一的资源权威，管理 Catalog、共享生产、租约、实例、Scene 和版本事务。读取 `Current`、构造配置或打开工作台都不初始化；资源操作与 Unity 对象访问在主线程执行，只读配置不代表入口线程无关。

- 应用接入：[`ResourceStartup`](../../Runtime/Systems/Resource/Integration/ResourceStartup.cs) 显式启动/关闭并管理自身责任；高级调用方可直接使用 `ResourceSystem.InitializeAsync`，不是第二套内核。
- [Runtime](../../Runtime/Systems/Resource/) / [Editor](../../Editor/Systems/Resource/) 分属 `YLFramework.Runtime` / `YLFramework.Editor`，namespace 为 `YLFramework.Resource` / `YLFramework.Editor.Resource`；共享程序集沿用 UGUI/Json.NET 等依赖，不依赖已退休宿主扩展。
- 旧 Manager/Loader、ResData/BinaryFormatter 与模拟 HotUpdate 栈已退役，不提供旧表、旧资源后端或另一资源权威的自动 fallback。

Editor 源码均属于 `YLFramework.Editor.Resource`，目录不另建 namespace 或程序集：

| 路径（相对 Editor/Systems/Resource） | 职责 |
|---|---|
| [`Workbench/`](../../Editor/Systems/Resource/Workbench/) | 工作台窗口、页面、表格与交付页摘要，含 ResServiceEditorWindow |
| [`Inspectors/`](../../Editor/Systems/Resource/Inspectors/) | ResourceAtlasConfiguration、ResourceBuildRecipe、ResourceImage 的 Inspector |
| [`Build/`](../../Editor/Systems/Resource/Build/) | 构建器、Workspace、ResourceBuildProgress 与 ResourceBuildSourceLocator |
| [`Publishing/`](../../Editor/Systems/Resource/Publishing/) | 发布、签发与安全内置部署 |
| [`Data/`](../../Editor/Systems/Resource/Data/) / [`CodeGeneration/`](../../Editor/Systems/Resource/CodeGeneration/) | 作者配置与地址常量生成 |
| [`ResourceEditorOperations.cs`](../../Editor/Systems/Resource/ResourceEditorOperations.cs)、[`ResourceEditorSimulation.cs`](../../Editor/Systems/Resource/ResourceEditorSimulation.cs) | 根部正式操作与模拟入口 |

`ResServiceEditorWindow` 等类短名不表示存在另一套资源服务。

## 初始化与持有

### 选择配置

| 来源 | 正式配置入口 | 前置条件 |
|---|---|---|
| 仅本地/URL 图片 | `new ResourceConfiguration()` | 无 Catalog，也须显式启动 |
| Resources 内存目录 | `new ResourceConfiguration(catalogs, images)` | 调用方明确提供目录，不扫描旧设置 |
| 正式 Bundle | `ResourceConfiguration.Create(ResourceLoadMode.Bundles, mounts, cacheRoot, images)` | 明确项目闭包、版本/兼容信息和编译类型 |
| Editor 模拟/Bundle 选择 | `ResourceEditorSimulation.CreateConfiguration(mode, projects, mounts, cacheRoot, images)` | 仅 Editor 使用；不因 Bundle 错误回退模拟 |

Bundle mount 使用 `ResourceProjectMount`。不传 `cacheRoot` 时每项目须有完整本地快照且不接受 remote；传入时装配 ArtifactStore，创建配置本身不 Mount、建缓存目录或下载。Simulation 不接受 cacheRoot；可使用当前定义，或传完整版本闭包冻结身份。没有隐式 Auto 模式，Runtime 的 `ResourceConfiguration.Create` 只接受 Bundles，模拟须用 Editor 工厂。

下面是完整的本地发布配置方法。参数来自应用明确选择的交付，不扫描“最新”目录；自定义类型通过已编译的 `typeof(...)` 传入，不按远端字符串反射创建。

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
        var project = new ResourceProjectMount(projectId, platform, appId,
            snapshot, assetTypes: compiledTypes);
        var configuration = ResourceConfiguration.Create(
            ResourceLoadMode.Bundles, new[] { project });
        return new ResourceStartup(configuration, bindCore,
            bindAudio ? new[] { projectId } : null);
    }
}
```

### 启动与关闭

1. 保存配置与启动对象，显式调用 `StartAsync()`，等待并 `TakeResult()` 后使用 `Resources`；按需读取 `CoreProvider`、`AudioProviders[projectId]`。
2. 业务结束先归还自身请求/Scope、实例、播放和 Scene 责任，再等待 `StopAsync()` 成功。完整协程见正式样例，不以 Dispose 启动请求代替关闭。

- 配置身份按**同一对象引用**判断：同配置 join，不同配置冲突，不要反复重建配置来假装 join。每个启动对象只管理自己的接入责任。
- `OwnsResourceSystem` 区分自建与借用；借用会话不会被 Stop 关闭。自建会话遇其他 startup/live owner 返回 Busy，保持可恢复，不强制归还 Lease 或卸载外部 Scene。
- 装配失败只清自身责任；若自建会话被独立 owner 阻塞，`Resources` 仍保留，错误说明原因，归还后继续 `StopAsync()`，不能丢掉关闭责任。
- Core/Audio 默认不接入。Core 外部绑定冲突明确失败，归还只解绑自身引用，不清后来替换者；Audio 装配不启动或关闭全局 AudioKit，不改用户开关/路由/缓存。
- 取消 Start 等待者不伤同入口其他等待者；取消 Stop 只退出等待，已开始的 native 排空继续。旧会话失效后先 Stop 旧对象，再显式重新启动。

### 取得与归还

| 需求 | API / 请求结果 |
|---|---|
| 主资产/子资产 | `Acquire<T>` / `AcquireAsync<T>(ResourceAddress)`，独立 `AssetLease<T>` |
| Bundle 内 typed 批量 | `AcquireAllAsync<T>(projectId, bundleId)`，`AssetBatchLease<T>`；仅 Catalog 登记的可赋值成员，bundleId 不是磁盘路径 |
| 普通 Group | `AcquireGroupAsync(projectId, groupId)`，`AssetBatchLease<UnityEngine.Object>`，不加载 Scene |
| 混合组预加载 | `PreloadGroupAsync(projectId, groupId)`，`ResourceGroupLease`；普通资产、SceneBundle/ScenePreview 各自明确 |

`ResourceAddress` 使用稳定 ProjectId、区分大小写的逻辑地址及可选子资产 key，不是 Bundle 文件名。完整身份共享 producer，每个成功消费者有独立责任；同步遇在途返回 InProgress，不忙等或重复 IO。

主资产/子资产能力取决于来源。Resources Catalog 的 Locator 是相对 Resources、无扩展名的路径，不接受 `Assets/` 物理路径；该来源不支持子资产选择或显式依赖执行，不能把 Bundle 的子资产规则直接套用。

`yield return request` 只等待；拥有型结果由 `TakeResult()` 恰一次交割。未领取 Dispose 释放结果，领取后 Dispose 请求不释放 lease。Scope 聚合自己的请求/租约，不维护第二缓存；取消不误杀他人，迟到 native 仍有清理 owner。Resources/AssetDatabase 是 borrowed，归零只 drop 记录；只销毁系统自建对象。

下面是业务协程内的读取片段，`resources` 来自已成功启动并由应用持有的 `ResourceStartup.Resources`；地址按自己的 Catalog 替换。失败由 `TakeResult` 抛出，请求与已领取租约各自释放，启动对象仍由应用关闭：

```csharp
using UnityEngine;
using YLFramework.Resource;

using (var request = resources.AcquireAsync<TextAsset>(new ResourceAddress("game", "text/welcome")))
{
    yield return request;
    using var text = request.TakeResult();
    Debug.Log(text.Asset.text); // 仅在租约持有期内借用 Asset。
}
```

配置数据接入时，通过 `ResourceProjectMount(..., assetTypes: compiledTypes)` 显式提供编译类型：`typeof(YLFramework.Localization.MultiLanguageConfigs)`、`typeof(YLFramework.DataAssets.ScriptableObjectConfigs)` 及应用实际使用的具体根/直接加载条目类型。不扫描全部资产，也不向 Resource 默认类型表写入领域依赖。Localization/DataAssets 返回的登记句柄持有根租约，业务先停 UI 再归还句柄，条目借用不得超过根生命周期。见 [配置数据接入](Data.md#资源准备与生命周期)。

## 可运行最小接入

[`Samples/ResourceStartup/`](../../Samples/ResourceStartup/) 的 Runtime/Editor 分 asmdef，不引用 MainModule、旧资源后端、测试或机器临时路径；默认框架导出包含样例源码，生成的应用数据不在导出根。

1. 无脏场景的 Edit Mode 执行 `YLFramework/Samples/Create Resource Startup Example`，新建唯一 `Assets/YLResourceStartupExample[后缀]/` 的 Text/WAV/Data、Project 与独立 Scene，使用新 GUID；不会替用户保存原脏场景。
2. Play 中选择组件，保持 Inspector 的 Mode 为 EditorSimulation，点 `Run Selected Mode`。同一 `Run` 读取 `message`/`data` 并按选项接入 Core/Audio；Scene/Inspector 加载不自动启动。
3. 退出并保存示例 Scene，点 `Build And Publish Example Bundle`，由正式 Builder/Publisher 写入 `snapshotDirectory/catalogDigest`；明确切为 Bundles 并保存，再使用同一运行按钮。
4. 成功时样例显示 `Passed: Hello from ResourceStartup / Explicit compiled sample type`。自定义 Data 在 `CreateMount` 通过 typeof 登记；ProjectId/Core/Audio 选择在启动时冻结，Inspector 中途修改仅影响下次。
5. Audio 被应用 Sound 设置或接纳策略拒绝时，按应用设置处理或关闭 Use Audio；样例不强开偏好。外部责任导致 Busy 时用 `Stop / Retry Shutdown`，不调用全局 AudioKit.Shutdown/Clear。

加密使用同一份样例和 `Run`：在生成的 `Project.asset` 选择 `contentEncryption=None` 或 `Aes256GcmHkdfSegmentedV1`，加密时填写非秘密 `contentKeyId` 并显式保存。应用在运行前设置 `example.ContentKeyProvider = applicationOwnedKeys`；该字段不序列化，选择配置不会取 key 或初始化。Editor 构建按钮将同一 provider 传给 Builder/Publisher。

非生产开发可在Inspector填**进程环境变量名**，点 `Inject Development Environment Key Provider`，复用正式的32字节Base64校验；不在Inspector输入key。未显式注入时，原Editor构建合同仍读取 `YL_RESOURCE_CONTENT_KEY`，但这不等于Runtime样例已获得key。进入Play/域重载后要重新注入，再按原按钮运行。此便利入口仅供开发，Player由应用注入provider，不自行下载key，不依赖EditorPrefs或开发机环境。缺/错key按下文错误处理，不转为Simulation、重下载或别的版本。

要交付内置资源，退出 Play 后保持已选的确切 `catalogDigest`，先点 `Preflight Selected Built-In Release` 查看来源、目标和内容，再点 `Deploy Selected Release To Built-In`。成功仅将样例 `useBuiltIn=true`，不启动服务、不代保存 Scene、不自动改为 Bundles。明确选择 Mode 为 Bundles 并保存应用 Scene 后再运行；后续 Build 会选中新发布并重置 `useBuiltIn=false`，须再次明确预检/部署。中断使用 `Recover Built-In Deployment`，再按结果重新预检。

Player 使用合法 Bundles 配置调用 `BeginBundles()` 或 `Begin/Run`。`useBuiltIn=true` 时只按 ProjectId/Platform/App/CatalogDigest 经 `OpenBuiltIn` 定位，不读取开发机的 `snapshotDirectory`。样例当前序列化字段为 camelCase，只有运行时注入字段保留 `ContentKeyProvider`；`FormerlySerializedAs` 负责读取原 Unity 资产字段，不代表旧源码成员名仍可用。

## 实例与场景

- `Instantiate/InstantiateAsync` 交付 `InstanceLease`；`Return/Dispose` 回缓存，`Discard` 销毁。复用 Foundation 桶，默认 scene-scoped；退休后的晚归还实例销毁，active/idle/destroy-pending 真实结束前均持 Prefab 图。
- `ClearInstanceCaches()` 只清本系统缓存，不全局清池；直接 prefab、Attach/场景实例不自动接管资产。业务仍须归还自己借出的实例。
- `LoadSceneAsync` 返回 SceneRequest，使用真实 handle/序号；显式门控通过 `Activate()` 放行，默认期限 30 秒，范围 `(0,300]`，不能无限占住 Unity async queue。`SceneLease.Dispose()` **不卸载 Scene**，须显式卸载并等真实 sceneUnloaded。
- Single 开始后取消不能恢复旧 Scene；最后 Scene 卸载可 Busy；外部同 path 并发歧义保守保活。特定同短名跨 path Scene Bundle 有 Unity 2022.3 限制，不承诺该组合或 native 多版本热共存。

## Core 与 Audio

- 使用 ResourceStartup 的可选 Provider 装配后再启动命名 Module；直接 InitializeAsync 的高级调用方自行显式绑定。Core `ProjectName` 是 Resource ProjectId；直接 prefab、Attach、默认 Canvas/EventSystem 不要求初始化资源，Module Close 不自动卸外部 Scene。
- Audio 使用 `new AudioClipAddress(resources.GetAudioClipProvider(projectId), logicalAddress)`；每次成功交付独立 clip lease，播放/尾轨/缓存负责消费期持有。直 Clip 是 borrowed，不由 Resource 或 Audio 擅自卸载。
- 内建桥接集中在 [`Runtime/Systems/Resource/Integration/Audio/`](../../Runtime/Systems/Resource/Integration/Audio/)：`ResourceSystem.Audio.cs` 使用 `YLFramework.Resource`，`ResourceAudioClipProvider.cs` 使用 `YLFramework.Audio`。两者仍在同一 Runtime，保持同 System/Project 的 Provider 引用身份、关闭后失效、缺失项目启动失败与原回滚责任，不是可选 Integration 或另一套资源服务。
- AudioClipLibrary 的旧枚举及隐藏 BundleName 仅供持久化迁移，不是旧后端。显式迁移要求地址映射，只创建新 GUID 副本、不改原资产；详见 [Audio 素材组](Audio.md#素材组与组件)。

### Core SceneController

`YLFramework.Core.SceneController` 是 Module 的轻量场景加载 Controller，不是第二个 SceneManager。先通过 ResourceStartup 的 Core 装配，或显式 InitializeAsync 后调用 `BindCoreResourceProvider`；Root、添加监听组件或读取 Controller 不会代为启动资源。

下面是业务协程内的片段，`module` 为仍在有效注册期内的 Module，其 `ProjectName` 对应目录项目：

```csharp
var controller = module.LoadController<YLFramework.Core.SceneController>();
using var request = controller.LoadSceneAsync("world/main", UnityEngine.SceneManagement.LoadSceneMode.Single);
yield return request;
if (request.Status != YLFramework.Core.CoreResourceStatus.Succeeded)
{
    throw (System.Exception)request.Error ?? new System.InvalidOperationException("Scene load ended: " + request.Status);
}
var scene = request.TakeResult().Scene;
UnityEngine.Debug.Log(scene.path);
```

- 默认项目为owner的`Module.ProjectName`；另有`LoadSceneAsync(projectId, address, mode)`。地址是目录登记的逻辑地址/alias，不自动把旧资源系统短名映射为地址；无同步入口、force_loading、隐式已加载复用或内置LoadingPanel。业务UI读取场景请求Progress，不使用Panel资源进度代替。
- 相同Controller的Ordinal `(projectId,address)` 在途键不区分mode，重复抛`CoreResourceException(InProgress)`且不再调用后端；终态后可再次提交，重复Additive会产生新的场景意图。不同Controller/项目/地址互不合并；别名按提交字符串判定，不声称解析到同一资产的不同alias也会合并。
- 直接返回后端原`CoreResourceRequest<CoreSceneResult>`，状态、进度、错误、Completed与单次TakeResult规则不变；终态后订阅Completed可能同步通知。完成仅摘Controller的tracking，不Dispose或提前领取调用方结果；成功/终态请求由调用方释放。
- `CancelPendingLoads()`先分离调用时在途快照，再逐项Cancel/Dispose；正常取消回调里新提交的请求属于下一轮。OnExit先拒绝新投递再清理快照；同实例重新注册属于新代次，旧回调不取消/摘除新责任。清理失败会聚合报告，不宣称已全部释放或自动重试；原后端异常保留，交付失败上下文位于Exception.Data的`YLFramework.SceneController.Context`。
- 关闭Module/Controller仅结束仍在途请求，不卸载已成功Scene、不清其他owner；Single开始后的取消不保证回滚。CoreSceneResult没有卸载权，需要显式卸载时由Resource/应用owner使用SceneRecord/SceneLease调用UnloadSceneAsync，而不是在Core反向引用具体ResourceSystem。
- Controller经Module正式注册、且仅在注册主线程使用。未绑定provider明确报Unavailable，关闭中的Module或旧Controller拒绝新请求；不因失败回退旧资源后端或另一配置。原游戏的全局同名SceneController保持不变，消费者按需使用完整YL类型名。

## 图片与图集

- `AcquireImage/AcquireImageAsync` 接受完整 HTTP(S) URL 或绝对本地 PNG/JPEG。冷 URL 同步 Unsupported，在途 InProgress，Ready 可同步取得；同步本地读取不占异步队列。
- ResourceImageOptions 默认编码上限 32MiB、16M 像素；idle LRU 为 128 项/32MiB RGBA 估计/30s TTL；并发默认 4、可 1～32，按 FIFO 调度，不是 priority。活跃 owner 不淘汰，普通 Catalog Texture 不进入此 idle 缓存，归零按其 Source 归还。
- ResourceImage 绑定 Image/RawImage，`SetImage(location/address)` 持自己的 Request/Lease，临时 Sprite 自毁、placeholder 借用；刷新/替换/停用/销毁按代次收尾。Refresh 不保证绕过同 URL 缓存，换代使用版本 URL；ClearImageCache 只清 idle。
- 手工 Atlas 必须位于已登记资源目录中、非 Variant、Include in Build 关闭、Sprite 名唯一，并作为主资产加入 Group。首次或修改 packables 后保存配置，再显式调用 `ResourceEditorOperations.PrepareAtlases(project)`；它会先生成/更新已配置的自动图集，再准备本项目图集，不是只读操作，也不写无关项目。
- Image 使用 `SetAtlasSprite(atlasAddress, name)`；非 UI 用 `AcquireAsync<SpriteAtlas>`。消费者销毁 GetSprite 克隆，再归还 Atlas Lease，不能销毁共享 Texture。默认编译类型表已含 SpriteAtlas，内部打包 Texture 不公开成子资产。
- 自动生成见下节；Variant、全局 atlasRequested/Prefab Sprite 透明晚绑定未实现。未知 native 图集布局明确拒绝，不按历史 V1/V2 记录推断任意引擎或平台可用。

### 自动 SpriteAtlas

自动图集是 **Editor 规则 -> 自有 Atlas 主资产 -> 原 Group/BuildMap/Bundle/Catalog** 的正式生产流程，不是另一个 AtlasManager、Runtime 映射表或透明 Sprite 替换服务。输出为 `.spriteatlasv2`，只准备本配置拥有的图集，Include in Build 始终关闭；源 Texture/Sprite Importer、手工 Atlas、全局图集设置和 Scene/Prefab 不被修改。

1. 在ResourceProject中登记来源目录和目标Group。规则可以使用多个来源目录中的Sprite；Group也可只由自动图集组成，不必添加虚假的手工条目。
2. 保存资源项目，在“资源配置 > 图集”或Project Inspector中明确选择Assets或其下已有的文件夹作为父目录，再用“新建并登记生成目录”创建此前不存在的子目录。父目录使用真实GUID登记，不从来源列表推断。建议放在所有资源目录之外；不使用自动图集时无需设置。已有目录（即使为空）、文件或孤立meta均不接管，选择父目录也不授予对其其他内容的管理权限。新目录连同`AutomaticAtlases.asset`、Atlas及meta须一并保留；丢失或换绑原登记身份会阻止构建，不会静默漏掉图集。
3. 在配置 Inspector 的 `rules` 添加规则：`id` 为稳定技术键，`groupId` 对应已有 Group，`sources` 选目录或已按 Sprite 导入的 Texture2D。目录递归收集 Sprite 纹理（支持 Multiple Sprite），普通非 Sprite 纹理不自动改 Importer；空目录不会产生空 Atlas。同一纹理不能进入多个自动规则，每个图集的 Sprite 名必须非空且唯一。
4. 选择“合并为一个图集（Together）”，本条规则的Sprite聚为一图，`Address`即完整逻辑地址，例如`atlases/ui`。选择“按实际文件夹生成（PerFolder）”，则按纹理真实父目录分图，地址由规则地址、所属资源目录的持久前缀及相对文件夹路径组成。PerFolder输出身份仍含源文件夹GUID，不会把两个同名末级文件夹当成同一来源；移动后图集GUID可以保持，但派生地址可能变化。需要跨移动稳定的单一业务地址时用Together。
5. 配置 Padding（2/4/8）、MaxTextureSize（32..8192 二次幂）、旋转/紧密打包、Readable、MipMaps、sRGB、FilterMode 和压缩；默认不旋转、不紧密、无 mip、不可读、Bilinear、2048、Uncompressed。过大内容由 Unity 多页承载，不按不稳定成员 hash 重命名。Sources 不能与生成范围重叠，包括选择其祖先资源根；不要把输出或配置再当来源。
6. 保存规则后预览待创建、待更新、当前有效及已失效的输出，再点击“生成、更新并准备”。生成只对明确来源候选做必要的正常同步导入，避免PNG已变但缓存仍旧；不改Importer设置，也不替用户保存未提交的Importer改动。正式Workbench/API Build会先生成并准备自有图集，再冻结输入、分析依赖和构建；无需手动把输出加入Group。
7. 存续 Atlas 保留原 `.meta` 和 GUID，相同有效输入不改资产字节。改地址/设置不会改 GUID；改规则 Id 则是新身份，旧输出转为 Obsolete。失效输出退出正式选择但不自动删除；Inspector 的“检查引用并删除失效输出”或 `DeleteObsolete(project, key)` 会重新检查归属和当前资产/资源定义引用，仍被使用则拒绝。

文件夹Group选择会排除已登记生成范围，避免重复收集配置、失效输出或自动Atlas。生成目录不必登记成普通资源目录，有效Atlas主资产仍经原Group/BuildMap进入Bundle/Catalog；图集中的Sprite通过Atlas Lease与`GetSprite`或`SetAtlasSprite`查找，不会自动变成Catalog子资产条目。另行选择源纹理产生的普通Sprite子资产仍按原规则处理。手工再次选择生成目录中的配置或输出不是合法添加方式。规则和生成配置继续纳入构建后验；只读BuildMap/模拟索引遇待生成或更新会明确拒绝，不读取旧发布冒充新结果。

图集配置统一记录明确生成父目录。脚本只使用`CreateConfiguration(project, generationParentPath, folderName)`，不会根据来源列表第一项推断位置。生成目录可以位于某个来源目录内；Group文件夹递归选择会排除已登记生成范围，但Atlas规则来源仍不能与生成范围相交。

脚本复用同一入口。下面要求资源项目已保存、`ui-atlases` Group已存在、Sprite已导入且位于任一已登记来源目录，`generationParentPath`为明确选择的现有Assets父目录。只新建子目录，不认领父目录或已有内容：

```csharp
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

public static class ApplicationAtlases
{
    public static ResourceAtlasConfiguration Configure(ResourceProject project, Texture2D sprites, string generationParentPath)
    {
        var configuration = ResourceAtlasGenerator.CreateConfiguration(project, generationParentPath, "GeneratedAtlases");
        configuration.rules.Add(new ResourceAtlasRule
        {
            id = "ui", groupId = "ui-atlases", address = "atlases/ui",
            grouping = ResourceAtlasGrouping.Together,
            sources = new Object[] { sprites }
        });
        EditorUtility.SetDirty(configuration);
        AssetDatabase.SaveAssetIfDirty(configuration);
        return configuration;
    }

    public static ResourceEditorBuildReport Build(ResourceProject project)
    {
        Debug.Log(ResourceEditorOperations.Preflight(project, "app-v1"));
        return ResourceEditorOperations.BuildAndPublish(project, "app-v1");
    }
}
```

也可显式 `ResourceAtlasGenerator.Preview(project)` / `Generate(project, cancellation)` 后调用原 Builder。`Preview` 不导入、保存或准备；冲突、非法配置、未知文件、缺失输出/meta、外部修改明确抛错，不能按普通 Update 强盖。待生成时 `ResourceEditorOperations.Preflight` 报告计划，并说明尚未完成最终依赖/Workspace 检查；正式 Build 仍执行生成后的全部验证。

普通生成异常/取消会回滚本次仍能确认归属的输出和登记写入，并停止本次构建；回滚只处理实际开始写入的资产对，写入前与恢复前均复验，不会删除被回调占用但自己尚未写入的路径。若写入后该路径又被外部修改，保留冲突内容并报告原异常与恢复异常，不强行覆盖；磁盘 meta GUID 也会与身份核对，不能只靠 AssetDatabase 缓存。旧有效图集不会被拿来冒充新构建成功。

应检查 `BuildError`、`PublicationError`、`GenerationError` 各阶段结果，后者是既有**资源常量**生成错误，不代表自动 Atlas 成功与否。成功的 `ResourceBuildResult.AtlasGeneration` 提供本次生成前计划；不承诺进程崩溃/断电的绝对原子恢复。外部修改请恢复已登记 Atlas/meta 的原字节，或在人工确认引用和归属后另设新范围，不自动接管或删除未知资产。

发布后用普通 `new ResourceAddress(projectId, "atlases/ui")` 取得 `SpriteAtlas` Lease，或 `ResourceImage.SetAtlasSprite(address, "SpriteName")`。GetSprite 的克隆由消费者销毁，Atlas Lease 持有期内共享 Texture 仍由原 Bundle 所有权保活；不要 Destroy 共享 Texture，不必注册 `atlasRequested`，也不会自动给 Prefab 中的 Sprite 做晚绑定。

自动图集的生成规则不等于目标 Player 的实际渲染保证。修改引擎、平台或图集设置后，应分别验证构建、实际像素和 Atlas Lease 生命周期，不能只检查生成资产存在。

## 预加载与诊断

- PreloadGroupAsync 领取的 ResourceGroupLease 在持有期保活：普通成员取得资产，SceneBundle 只准备 Bundle，Simulation 的 ScenePreview 只验证路径，**均不加载 Scene**。required 失败整批回滚，optional 保留 cause；普通 AcquireGroupAsync 不替代混合组预加载。
- GetDiagnosticsReport 按需复制 owner、producer/waiter、IsQueued、Bundle 根 pin/入边、实例、Scene 与版本责任，不持有 Unity 对象或初始化。历史默认关闭，由 ConfigureDiagnosticsHistory 显式开启。
- 构建 Placement/SourceBytes/Dependents 与实际 FileBytes/SHA 分开；源文件字节、压缩制品、native memory、累计 IO 不是同一指标，不能以共享引用次数判重复打包。

<a id="resource-workbench"></a>

## 资源工作台

### 第一次构建与本地发布

资源项目是Unity工程内的一份`ResourceProject`配置，不是Unity工程本身。一个资源项目可以登记多个资源目录、使用多个Group；Group用于组织资源和设置分包方式，不是磁盘目录。新增目录不会建立另一个独立发布版本。

1. 打开 `YLFramework/Resource/Workbench`，在左上角选择已有资源项目，或使用选择器的“新建Project...”创建配置。
2. 在“项目设置”逐项点击“添加资源目录”，选择这个资源项目会使用的文件夹。音频、图片、Prefab及Scene可以放在不同位置，无需移动、复制或把整个Assets登记为资源根。目录登记只决定允许从哪里选择，不自动选择其中所有文件；生成目录也不是普通构建的必填项。
3. 在“资源配置 > 主资源”创建Group，从Unity Project窗口选中文件或文件夹后，通过“添加资源…”菜单添加选中项，或直接拖入当前Group的列表。同一个Group可以包含多个资源目录中的文件。文件夹会在解析、构建时递归展开；扩展名留空表示全部可打包类型，筛选示例为`.png`。文件地址留空使用所属目录的固定地址前缀和相对路径，地址别名是同一主资源的额外地址。
4. 点击顶部“保存”保存当前资源项目。初次可先使用内容加密 `None`；它表示不加密，不表示不压缩，也不会替用户修改已有压缩或加密设置。
5. 进入“构建发布”，不选择构建配方也可以执行。填写应用约定的“资源兼容标识（App）”，例如 `app-v1`（仅示例，不是游戏版本号），核对当前平台，再点击“构建并本地发布”。遇到未保存配置、脏场景或资源检查阻塞时先处理提示；工作台不会自动保存场景或切平台。
6. 先看构建结果，再到“版本交付 > 发布版本”点击“读取已登记版本”。本地发布不等于导出、签发、内置部署或运行时激活。这些任务，以及图集、分包分析和缓存查询，均按需使用，不是第一次打包的前置步骤。

“诊断 > 开始使用”和资源空状态提供同一路径及页面入口；“前往构建发布”等入口只切换原有页面，不执行构建或自动保存。

### 多目录与加载地址

普通Unity工程可以直接登记以下已有目录，不需要每种资源类型各建一个ResourceProject：

```text
Assets/Art/Textures/     地址前缀：textures
Assets/Art/Materials/    地址前缀：materials
Assets/Audio/            地址前缀：audio
Assets/GamePrefabs/      地址前缀：prefabs
Assets/GameScenes/       地址前缀：scenes
```

例如`Characters` Group可以同时选择`GamePrefabs/Hero.prefab`和`Audio/Hero.wav`。Prefab引用的Material及Texture可以位于其他已登记目录，仍由原BuildMap解析并处理共享依赖；没有选择的独立文件不会因登记目录而自动进入构建。

- 目录以Unity GUID记录，显示和检查时重新解析真实Assets路径。来源只允许原策略支持的Assets真实子目录，不能是整个Assets、StreamingAssets、Packages、外部磁盘目录或链接目标。脚本、Unity内置依赖及Packages代码继续按原合法依赖规则处理。
- 同一资源项目中的目录不能重复或互相包含；不同ResourceProject的普通来源范围也沿用原互斥规则，不能相同或嵌套。其他资源项目的生成范围继续受到保护，不新增跨Project依赖协议。来源登记只有读取准入，不授予删除、覆盖未知文件或meta的权限。
- 默认地址为所属目录的持久前缀加相对路径，如`prefabs/Hero.prefab`；前缀留空则只使用相对路径。增加其他目录、重排列表或改变显示名称不会改已有地址。修改前缀或把文件移到另一目录可能改变下次构建地址，长期固定的加载地址应显式填写。历史发布不被重写，也不会自动追加alias。
- 地址、alias及大小写冲突继续严格拒绝并显示两个实际来源，不自动添加索引或随机后缀。子资产仍由原主资源规则派生，没有第二套子资产选择协议。
- 新增、移除、替换只修改配置，可Undo/Redo，不删除源文件或Group条目。允许保留待修正草稿；目录失效、重复、重叠及失去合法来源的条目或图集规则会明确阻止构建。先恢复登记或明确调整相关条目，再保存和重算，不会静默漏包或扩大到其他目录。

`ResourceProject` 只使用 `sourceDirectories` 列表；清空表示没有来源，构建明确拒绝，不回退到隐藏字段。浏览、解析和保存不自动迁移旧单根 Editor 配置，外部旧配置须显式转换；这不改变 Runtime 对既有 Catalog v1 发布的读取方式。

下面是 Editor 方法内的片段，`project` 为所选 `ResourceProject`；文件顶部需引用 `UnityEditor` 和 `YLFramework.Editor.Resource`。可直接编辑列表，或使用支持 Undo 的正式入口：

```csharp
ResourceEditorOperations.SetSourceDirectories(project, new[]
{
    new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID("Assets/Art/Textures"), addressPrefix = "textures" },
    new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID("Assets/GamePrefabs"), addressPrefix = "prefabs" }
});
ResourceEditorOperations.SaveProject(project);
```

有效目录集合、前缀和生成登记进入原Builder设置冻结及后验，展示顺序不构成新构建语义；正式Recipe也读取同一资源项目配置。普通浏览、选中和排序不自动重算依赖或SHA。工具源码本身参与Workspace指纹，升级工具可能使旧缓存合法失效；新配置不迁移或删除旧owned缓存。Catalog Schema v1、Runtime地址解释和旧发布读取路径没有增加多目录协议，旧v1发布无需重写。

### 工作区与操作边界

工作台有五个任务页；打开、浏览、排序或搜索不初始化 Runtime，也不自动构建、读取发布历史或扫描缓存。

| 页面 | 用途与执行边界 |
|---|---|
| 资源配置 | 编辑真实 Group/文件/目录和图集规则；“计算预览”才解析 Map。图集的预览、生成、准备和删除失效输出均为显式操作 |
| 分包分析 | 在当前配置预估与本窗口所选 BuildResult 间切换，查看物理 Bundle、来源、共享依赖与被依赖树 |
| 构建发布 | 选择可选 Recipe，或使用无配方的 App/force/常量生成选项；分别报告构建、发布、代码生成结果 |
| 版本交付 | 显式读取历史/当前索引，选择确切版本后导出、签发或部署；Publisher 恢复与内置部署恢复分开 |
| 诊断 | 采样已启动的全会话、只读查询明确 Store，或查看开始使用指南；导航和文档按钮不运行样例或注入 key |

**编辑与保存：**顶部选择器可按显示名、ProjectId 或配置路径检索，完整路径/GUID 可查看、复制和定位。新建只创建配置，不移动源文件或接管输出。进入具体 Group 才能增删改，“全部 Group”只读聚合；高亮不是构建开关。Ctrl/Shift 多选，筛选隐藏的选择仍计入批量操作，先核对底栏范围。文件夹仍是一条真实定义，不展开成可删除的伪条目；拖入子资产按主文件 GUID 登记。移除定义、目录项或图集来源可 Undo，不删源文件；删除 Group 只删组及其选择定义。

Ctrl+F 聚焦搜索，Alt+G 新增组。顶栏保存始终只存 Project；项目设置、主资源/解析页 Ctrl+S 也只存 Project。自动图集页的保存/Ctrl+S 只存当前规则，配方页只存所选 Recipe。Enter、失焦或保存前提交 Group 名称编辑，但切页、关闭、收起详情不自动保存资产，也不代存场景。编辑可 Undo/Redo，正式执行前仍须处理脏配置/场景并复验。

**图集：**规则、来源、打包/纹理设置和输出共用同一真实配置，移除规则不等于删除输出。先保存，再预览 Create/Update/Current/Obsolete，最后“生成 / 更新并准备”。失效输出保留，只有显式删除且归属、内容/meta、引用复验通过才可删除；登记损坏不当作未配置。手动页可复核后定位原生 Inspector；“准备本 Project 图集”也会先执行自动生成前置。规则分组 Together/PerFolder 与 Bundle 分包 Together/PerAsset 不是同一概念。

**分包与来源：**Group 不是物理 Bundle 固定父节点，最终包仍受共享依赖、Scene 分离和环归并影响。依赖闭包源文件不等于逐文件因果边，不能据此断言某 Prefab 精确引入某文件。`ResourceBuildAsset.Source` 只是 Map/BuildResult 内的只读来源副本，不是第二份 Project；定位定义时重验 Project/Group/Selection GUID、内容与路径，或图集配置 GUID/规则 Id。来源变更明确拒绝，不按旧列表 index 编辑另一条定义。过期结果保留原事实，不用新 Project 覆盖旧报告。

**发布与部署：**“读取已登记版本”与“读取当前发布索引”各自保留时间和失效状态。所选版本、Publisher 当前索引与 Runtime active 互不等同，单项刷新不复验另一快照，不从目录名猜“最新”。导出捕获所选完整 Release，不覆盖、同步或上传；所选版签发仍严格 ReadVersion，“恢复与其他”的当前索引签发入口是另一明确选择。

内置部署先只读预检。确认框展示身份、来源/目标、文件数/逻辑字节，以及内置文件、外部暂存/锁/journal、目录 meta/Unity 导入的影响；可复制完整范围，关闭或 Esc 取消。确认后 Deployer 仍复验，预检不是永久许可。逻辑复制字节不是磁盘峰值，不能据此认定空间足够。恢复不解析错误文本猜 journal，不手改控制记录、不覆盖旧部署、不切 active/LKG，见 [安全内置部署](#安全内置部署)。

**诊断与查询：**运行诊断属于应用已创建的全会话，不冒充当前所选 Project；未初始化表示不可用，不是运行故障。数据预算、native read buffer、CNG owner/key 成本分别解释，不相加为进程总内存。自动采样默认关闭，显式开启后从 5 秒间隔开始；离页、隐藏、关闭、转换或重载暂停，返回不自启。只读查看不改历史设置；显式配置/关闭历史会清空 buffer，不回填。

磁盘查询须输入应用实际的绝对 cacheRoot 和完整 ProjectId/Platform/App 闭包，不猜 persistentDataPath、不写入 Project/Recipe。每次最多一个在途查询，输入变化、取消、离页或重载会 Dispose 枚举器；旧报告保留并标旧，不伪造新 Canceled 结果。Ready 不等于 App 兼容、解密、签名或可挂载认证，owner 本身只校验项目/平台。此页没有 Recover/Cleanup/删除/激活，不 Mount、取 key、加 pin 或创建 root/锁；完整含义见 [Store 只读查询](#store-只读查询)。

**窗口状态：**分隔、输出高度、表头及可复核选择属于本地窗口状态，不写业务资产。窄窗按需切换列表/详情；跨重载重新核对身份，失效或歧义选择清除。临时报告不跨重建恢复，重载不会重执行操作。输出保留原项目/时间与完整结果，窗口最多保留 32 次构建尝试；切项目清除原排队回调，但已开始的同步构建/发布不可交互取消。阶段只代表已到达边界，不提供百分比、剩余时间或随时取消。

### 构建与发布边界

`ResourceProject.compression` 仅支持 Lz4/Lzma/Uncompressed，始终 StrictMode；压缩进入 Workspace 身份，Force Rebuild 须显式选择，不接受任意 flags。只构建已激活、已安装、主机可 native 读回的桌面目标，不切平台。Publisher 输出 `resource-catalog.json` 与 release index；BuildId 标识 payload，CatalogDigest 标识最终字节，摘要不等于签名。

Workspace 按 Project GUID/设置隔离，不可变发布在 `AssetBundles/YLFramework/<ProjectId>/<Platform>/<CatalogDigest>/`。ReadHistory/ReadVersion 只读取 owned 完整版本，ExportRelease 只向工程外不存在的 digest 目录导出并复验。未知文件/归属、链接或篡改明确拒绝，不覆盖旧目录；Service/ResService 等旧加载栈和 BuildScript/AssetBundleExporter 不是当前入口。

### 可共享构建配方

`ResourceBuildRecipe` 是保存在 `Assets/` 的共享配置资产，通过 `Create/YLFramework/Resource/Build Recipe` 创建。设置 `project/target/appContentCompatibilityId/action/forceRebuild` 及必要的常量生成选项后明确保存。Workbench 编辑同一资产，不复制 Project；所选配方丢失明确报错，不静默切回无配方。保存范围见 [工作区与操作边界](#工作区与操作边界)。不使用配方时仍可直接调用 `BuildOnly/BuildAndPublish` 或 Builder/Publisher。

作者配置的 C# 字段使用 camelCase，例如 `ResourceProject.projectId/sourceDirectories/groups` 与 Recipe 的 `project/target/action/forceRebuild/generateReferences/referenceDirectory`。旧 Unity 资产通过精确 `FormerlySerializedAs` 读取；Catalog、workspace 与发布回执中的 `ProjectId/Groups` 等持久协议键不随作者字段改名。

- 资源选择、Group、压缩与内容加密始终来自引用的 `ResourceProject`，配方不覆盖、不复制、不临时改写 Project，也没有多级继承。
- `target` 必须明确、已安装且已激活并匹配本机可读回的桌面目标；配置不会切平台。`action=BuildOnly` 不发布/生成；BuildAndPublish 才可选择 `generateReferences/referenceDirectory`，常量由正式生成器及完整发布集合决定。
- “查看有效执行选项”/`ResolveRecipe` 只解析非秘密值，不取 key、不构建/生成/发布/初始化。`ExecuteRecipe` 要求配方与 Project 已显式保存，开始时将执行选项复制为只读 `ResourceBuildInvocation`；配方后续编辑影响下一次。资源输入继续由原 Builder 冻结并做完整后验，无法冻结的回调继续阻止整体 skip。
- Inspector 与无窗口入口都调用 `ResourceEditorOperations.ExecuteRecipe`。结果 `Invocation`/`Summary` 说明配方路径、实际 Project/GUID、目标、App、操作、force、压缩/交付和生成选项；配方名称/GUID不加入构建内容身份。不把发布成功等同于常量生成成功，应分别检查 `BuildError`、`PublicationError` 和 `GenerationError`。
- Workbench 主动作按 `recipe.action` 经同一 ExecuteRecipe 执行，不临时改 Action。`recipe.project` 与工作台上下文不符时显示双方 Id/路径/GUID，阻止执行并提供显式切换/解除选择；不自动改配方。开始执行仍重新冻结与正式复验，不把上一次绿色提示当许可。
- 资源地址常量是当前平台已登记发布项目的Catalog集合，不仅是当前资源项目；集合要求Unity/App兼容标识一致。某个项目发布成功后，集合检查或代码生成仍可能失败，界面保留真实原因并说明版本仍有效。处理具体问题后可在“版本交付 > 恢复与其他”单独生成，不自动重发或回滚，也不一概归因为目标目录。
- `CheckReadiness`及`InspectRecipePreflight(full:false)`只检查静态前缀，不取key、不建Map、不扫描Workspace/历史、不做SHA。显式`InspectPreflight`/`InspectRecipePreflight(full:true)`与原string `Preflight`共用唯一正式控制流，结构化区分StaticBlocked/StaticReady、AtlasPreparationRequired、Complete和Failed；图集待生成不是完整成功，原string的异常及有效早返回语义保留。
- `ResourceBuildProgress.StageReached`是主线程只读实际边界通知，非执行控制或测试故障注入。订阅者须自行解绑，不应写入或重入执行；Builder/EditorOperations在observer回调内的执行重入明确拒绝，观察异常隔离且不输出其异常秘密，不替换原输入冻结、provider边界、提交或finally。Workbench只在自己的同步操作作用域订阅并在finally/关闭解绑。
- 确认窗口完整文本可滚动/选择/复制，不受原生DisplayDialog长文本截断；关闭或Esc是未开始执行的取消，确认后仍由原服务复验。真实结果分开显示构建、发布和常量生成；发布成功而生成失败是部分成功，不自动重发或回滚。发布提交未确认须用独立Publisher恢复入口确认，不断言旧Index必然未变。发布已验证构建始终针对所选确切BuildResult，由Publisher重新验证源及Workspace。
- 密钥与签名私钥不在配方中。加密继续传应用持有的 `IResourceContentKeyProvider`，或沿用原明确进程来源 `YL_RESOURCE_CONTENT_KEY`；查看/保存配方不读取环境值。配方不签发、不部署 StreamingAssets、不上传或激活 Runtime。缺 key、无效目标、取消与失败沿用原 owned 锁/路径/保旧合同。

无窗口 Editor 调用可复用随包样例 [`ResourceBuildRecipeExample.Build`](../../Samples/ResourceStartup/Editor/ResourceBuildRecipeExample.cs)，也可直接调用 `ExecuteRecipe`。以下为完整的转发类型，须放在 Editor-only 程序集中；自建 asmdef 需引用 `YLFramework.ResourceStartup.Sample.Editor` 及所用框架程序集：

```csharp
using YLFramework.Editor.Resource;
using YLFramework.Samples.Resource.Editor;
using YLFramework.Resource;

public static class ApplicationResourceBuild
{
    public static ResourceEditorBuildReport Build(string recipeAssetPath,
        IResourceContentKeyProvider applicationKeys = null)
    {
        return ResourceBuildRecipeExample.Build(recipeAssetPath, applicationKeys);
    }
}
```

在已稳定的 Editor 主线程传入工程内所选配方资产路径，无需打开窗口。配方路径只是加载配置的入口，不是输出路径；输出仍由 Workspace/Publisher 决定。这不是通用 CLI/batchmode 入口，不代表独立 CI 已验收。

<a id="代表性成本与复跑"></a>

### 性能测量边界

- 构建、指纹/SHA 后验、内容认证、复制、下载和 native 物化分别计量，缓存命中不等于整体 no-op；存在构建回调等不能冻结的输入时仍可能执行 native 重建。
- 首次 Acquire 包含 IO、SHA、调度与物化交接，不等于纯 CPU 时间或单帧卡顿。受控回环吞吐不能外推公网/CDN，Editor 不能代替 Player。
- 内容数据预算不等于进程总内存。声明缓冲、CNG key-object、managed/native、working set 与采样峰值分别解释；不可用计数保留 null/Unavailable，不按 0 宣称零分配或零占用。
- 性能复跑设施位于源码仓库的 `Tests/EditMode/Systems/Resource/ResourceCostBaselineTests*.cs`，准备与收尾步骤见历史档案，不属于产品接入前置。使用自有场景、输出和非生产回环凭据，不覆盖旧证据、不清用户 Library，也不把测试依赖带进 Runtime/Player。

## 交付与挂载

- 内置身份约定 `YLFrameworkResources/<ProjectId>/<Platform>/<CatalogDigest>/`，由应用明确 digest。`OpenBuiltIn(projectId, digest, platform, appId, resourcesRoot)` 只读定位；默认根为 `Application.streamingAssetsPath/YLFrameworkResources`，不扫描最新、不创建或复制目录。自有部署外壳会被完整验证后适配到内部 `payload~`，既有普通导出布局仍走原严格 Open，不能把 `payload~` 直接传给普通 Open 绕过外壳。
- 将已验证快照及编译类型组成 mounts，显式传 cacheRoot 才装配原 ArtifactStore；有效 active/LKG 优先，无有效持久版本再回退明确内置，整组选择不拼代。仅消费内置不建立持久 cache。
- 正式 payload 的闭合文件集合连 `.meta` 也不忽略。安全部署与 Unity 元数据边界见下节，不以工程外导出或导入前复制成功冒充真实 StreamingAssets 验证。
- 更新要求已配置 ArtifactStore。顺序为 CheckUpdatesAsync（本地完整项目闭包）或 CheckRemoteUpdatesAsync -> PrepareUpdateAsync -> 应用归还责任 -> ActivatePreparedUpdateAsync；Prepared 可查询/续用，但 **Prepare 不等于激活**。
- Check 纯读，Prepare/取消不写 active；激活 Busy 后旧版仍可用，from 变化返回 StaleBase。RollbackAsync 显式选 LKG 并走同一闸门，不是随意激活旧 prepared。CleanupVersionsAsync 安全清理 owned 内容；清理前查询见下节。
- 远端由应用注入受信公钥、端点、channel 与最低序号，沿签名、HTTPS、ETag/Range 和事务链处理。签名不证明最新，不防本地状态恶意回滚；历史导出不等于 Runtime 回滚。仅开放 WindowsEditor 及精确 Unity `2022.3.62f3c1` / `6000.3.27f1` 的 Windows64 Mono/IL2CPP 固定候选入口，其他 Player、位数或版本明确 Unsupported。
- 普通 Player 使用 Unity 默认 TLS 证书链校验，不提供生产忽略证书或测试 pin 配置。仅 Editor，或同时具备 `UNITY_STANDALONE_WIN`、`ENABLE_MONO/ENABLE_IL2CPP` 后端、`DEVELOPMENT_BUILD`、`YL_RESOURCE_ACCEPTANCE_TESTS` 的隔离验收构建保留内部 loopback 精确 pin，不修改系统信任。正常应用不得加入验收符号；loopback 结果不能代替默认 CA 链正向联网验证。
- partial 续传以强 ETag/If-Range 精确绑定实际 offset；200 忽略 Range 或 ETag 变化从零获取，失配 Content-Range 拒绝后重头重试，弱 ETag 不用于续传。最终仍须逐文件 length/SHA 和内容认证，不能盲目追加后提交。
- 公开请求错误沿用现有合同：元数据传输、签名/摘要及确认序号拒绝通常包装为`UpdateFailed`，内层异常保留具体原因；不能把“任意失败”当作某个特定拒绝门禁通过。

### Player 持久流程

首次包内版本由 Builder/Publisher 发布，经 `ResourceBuiltInDeployer.Preflight/Deploy` 明确部署，再构建 Player。运行时按完整内置身份定位到 `StreamingAssets/YLFrameworkResources/<ProjectId>/<Platform>/<Digest>/payload~/`，不能读取开发机发布目录或用 AssetDatabase/Simulation 补缺。调用方须先准备合法 StreamingAssets 父目录；Windows Mono 的长路径约束也影响构建载体和最终包。

同一应用后续进程复用同一独占持久 Store，不随机更换 cacheRoot。下面只展示**协程成功路径片段**，不是包含错误处理的完整宿主；文件顶部需引用 `UnityEngine` 和 `YLFramework.Resource`。ProjectId、digest、App、`trustedRemoteSource` 和解密配置由应用提供，公钥不能取自待验发布本身；None 的 `applicationDecryptionOptions` 可为 null。

`TakeResult()` 会抛出请求错误。实际宿主必须在失败、取消和停用时同样结算已取得责任，并保留启动对象执行/重试 Stop，不能依赖代码自然走到末尾或用 `yield break` 跳过关闭。完整 owner 接点见 [最小接入样例](#可运行最小接入)。

```csharp
var builtIn = ResourceLocalSnapshot.OpenBuiltIn(
    projectId, packagedDigest, "StandaloneWindows64", appContentCompatibilityId);
var mount = new ResourceProjectMount(projectId, "StandaloneWindows64",
    appContentCompatibilityId, builtIn, remote: trustedRemoteSource);
var configuration = ResourceConfiguration.Create(ResourceLoadMode.Bundles,
    new[] { mount }, System.IO.Path.Combine(Application.persistentDataPath, "ResourceStore"),
    decryption: applicationDecryptionOptions);
var startup = new ResourceStartup(configuration, bindCore: true,
    audioProjects: new[] { projectId });
using (var start = startup.StartAsync())
{
    yield return start;
    start.TakeResult();
}
UpdatePlan plan;
using (var check = startup.Resources.CheckRemoteUpdatesAsync())
{
    yield return check;
    plan = check.TakeResult();
}
PreparedUpdate prepared;
using (var prepare = startup.Resources.PrepareUpdateAsync(plan))
{
    yield return prepare;
    prepared = prepare.TakeResult();
}
using (var activate = startup.Resources.ActivatePreparedUpdateAsync(prepared))
{
    yield return activate;
    activate.TakeResult();
}
// 先归还本宿主的Lease、实例、Scene和Audio责任；统一失败路径同样执行此退出段。
using (var stop = startup.StopAsync())
{
    yield return stop;
    stop.TakeResult();
}
```

重启后的Start自动恢复journal并选择active/LKG整组，不挑最新目录。Prepare持久化后可在下一进程用`GetPreparedUpdates()`取回再显式Activate；即使离线激活，也保持同一Project/平台/App及受信远端公钥配置。本地更新用`CheckUpdatesAsync(完整快照闭包)`接同一Prepare/Activate流程。

显式 Rollback 不降低最高**成功激活**序号；只读 Check、首次失败下载不确认新序号，不承诺首次安装或本地历史丢失后的抗旧发布冻结。进程强停恢复不等同于断电保证。正常 Stop 后检查 authority/Core 绑定、Lease/Bundle/实例/Scene，以及内容输入、在途、预算和 CNG 责任归还；不要用全局清理或终止主 Editor 代替。

### Windows64 IL2CPP

- 本指南的验证基线为 Unity `2022.3.62f3c1`、StandaloneWindows64、Managed Stripping Level Low、C++ Release。需要 Windows IL2CPP 模块及可用 MSVC/Windows SDK；实际部署仍须核对 BuildReport、GameAssembly/metadata 和目标运行，不能用 Editor 或 Mono 代替。
- ResourceStartup、领域 Provider 和更新 API 不变。None 用文件输入，加密用同一 CNG/BC 认证 Stream，不整包解密落盘、不静默软件回退。
- CNG 适配器按确切 System32 模块句柄绑定，字符串使用 LPWStr，保持 x64 ABI。系统模块是进程期依赖，不是每 Bundle 的 provider/key owner；key-object 和内容预算分别确定释放并诊断，系统 DLL 不随框架分发。
- 该版 IL2CPP 的 DriveInfo/GetDriveFormat 有实现限制；ArtifactStore 使用 Windows 可用空间 API，保留调用者配额和 64 位字节数。查询失败拒绝，不以无限空间或跳过检查降级。
- 框架不添加全量 Preserve。Low 以外裁剪、应用新增反射/动态泛型类型及其他引擎版本须定向验证，不能从默认编译类型表推断全部 AOT 入口已保留。
- 输入、work、Lease/native 图、CNG 和预算按同一责任图归还。延迟坏 Stream 的已知 Scene 风险不是合法输入崩溃、损坏、提前释放或泄漏的豁免。

## 安全内置部署

正式 Editor 入口为 [`ResourceBuiltInDeployer`](../../Editor/Systems/Resource/Publishing/ResourceBuiltInDeployer.cs)，只在已稳定、非 Play/编译/导入中的 Unity 主线程使用。Builder/Publisher 仍不隐式写 StreamingAssets；部署不会初始化 ResourceSystem、启动 Core/Audio、修改 active/LKG 或强制卸载现有资源。

```csharp
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

public static class ApplicationBuiltInDelivery
{
    public static ResourceBuiltInPlan Inspect(ResourceProject project,
        string catalogDigest, string appId)
    {
        var release = ResourcePublisher.ReadVersion(project,
            EditorUserBuildSettings.activeBuildTarget, appId, catalogDigest);
        var plan = ResourceBuiltInDeployer.Preflight(release);
        Debug.Log(plan.ToString());
        return plan;
    }

    public static ResourceBuiltInDeployment Deploy(ResourceBuiltInPlan plan)
        => ResourceBuiltInDeployer.Deploy(plan);
}
```

1. 在 Publisher 历史中明确选择完整版本，将确切 digest 传给 `ReadVersion`，再调用 `Preflight(release)`。也可对已用严格 `ResourceLocalSnapshot.Open` 打开的完整发布快照调用 `Preflight(snapshot)`；两者都不会猜最新、建目录、加锁或恢复写入。异常说明来源、目标或归属冲突，不能按“目标为空”自动认领。
2. 检查 `plan.Source.Document`、`ProjectGuid`、`DirectoryPath`、`Files`、`PayloadBytes`、`WorkspacePath`、`IsRepeat`，然后显式 `Deploy(plan, cancellation)`。部署会重新预检，预览不是跳过复验的授权。结果提供实际 `Snapshot`、相对 StreamingAssets 的 `RelativeDirectory`、`CopiedBytes` 与 `IsRepeat`，身份仍以 `Snapshot.Document` 为准。
3. 将结果的 ProjectId/Platform/AppContentCompatibilityId/CatalogDigest 记录在应用配置；运行时通过下面的身份工厂定位，不序列化 Editor 发布绝对路径。多个必需项目逐项明确部署，再显式组成完整 mounts；Catalog v1 不引入跨项目依赖或另一套全局闭包权威。

```csharp
using System;
using System.Collections.Generic;
using YLFramework.Resource;

public static class ApplicationBuiltInStartup
{
    public static ResourceStartup Configure(string projectId, string digest,
        string platform, string appId, IEnumerable<Type> compiledTypes = null,
        string privateCacheRoot = null)
    {
        var snapshot = ResourceLocalSnapshot.OpenBuiltIn(projectId, digest, platform, appId);
        var mount = new ResourceProjectMount(projectId, platform, appId,
            snapshot, assetTypes: compiledTypes);
        var configuration = ResourceConfiguration.Create(ResourceLoadMode.Bundles,
            new[] { mount }, cacheRoot: privateCacheRoot);
        return new ResourceStartup(configuration);
    }
}
```

随后沿用前文 `StartAsync/TakeResult`、Scope 取得资源与 `StopAsync` 的责任顺序。无 cacheRoot 时直接消费严格内置快照；有明确应用私有 cacheRoot 时仍复用原 ArtifactStore 的 active/LKG/内置优先级。Store 重开快照会连同原外壳一起复验，不仅验证内部文件；首次仅回退内置不会因此建立持久目录。

**布局与元数据：**正式文件位于 `Assets/StreamingAssets/YLFrameworkResources/<ProjectId>/<Platform>/<CatalogDigest>/payload~/`。root/project/platform 各有 `ylr-owner.json~`，版本外壳有 `ylr-deployment.json~`。Unity 2022.3 不导入 `~` 后缀目录/控制文件；正常目录 `.meta` 仅允许紧邻明确 owned 的 root/project/platform/digest 目录，且须为合法 folder meta。`payload~` 不生成目录或内部文件 meta，内部 Catalog v1/Complete/资源字节保持原样；外壳 receipt 与 Complete 的 ProjectGuid/身份交叉核对。额外 payload `.meta`、孤立 meta、未知文件/目录、损坏归属及 symlink/junction 均拒绝，不靠全局 ignore、删除 Unity 生成文件或关闭刷新实现。

**提交与恢复：**外部 `AssetBundles/YLBI_<token>/` 仅为本操作 workspace，完整 GUID/平台/工程根仍由 owner 校验，短 token 碰撞只能拒绝。journal 登记本次 staging 的闭合范围，复制、校验与目标复查完成后，将包含完整版本的最外层缺失节点搬入目标；相同完整版本幂等，不覆盖或自动删除旧部署。提交前取消/失败保留旧版和可恢复的私有 stage；提交后异常不代表未安装，必须调用 `ResourceBuiltInDeployer.Recover(project, target)`（或明确 projectId/platform/ProjectGuid 的重载）。记录保留至真实导入、严格复验及结果构造全部成功；已安装完整版本返回确认结果，未安装则仅清理 journal 登记的 stage 并返回 null，重新预检部署。未知或损坏控制记录拒绝自动删除/修补；Recover 不是任意目录清理授权。多项目分别部署不是绝对断电原子事务。

部署只允许新建或已验证由新 YL 管理的 `YLFrameworkResources` 边界，不认领已有空/未知根，不改父目录已有 meta，不触碰原 MainModule、其他 StreamingAssets 或用户文件。stage/锁/journal 均在 StreamingAssets 之外，不进入 Player 或正式 payload；本入口不提供全局旧版本垃圾回收。

部署成功只说明这次交付操作完成，不能替代实际 Player 包内文件、首次启动和持久重启检查。Android/JAR/WebGL 提取未实现；其他主机不能只凭 Windows 导入结果推断可用。

## Store 只读查询

正式入口为 `ResourceConfiguration.QueryArtifactsAsync(completed, maxEntries, cancellation)`，也可直接调用已配置的 `ArtifactStore.QueryAsync(...)`。两者均返回由调用方驱动的 `IEnumerator`，不创建协程宿主；每次 `MoveNext` 和 `Dispose` 都应在 Unity 主线程进行。无需先初始化 ResourceSystem，不 Mount、不下载、不 Prepare、不 Recover、不清理，不创建 root、缓存、staging 或锁文件。**不要用原 `GetPreparedUpdates()` 替代只读查询**：该入口会恢复元数据，并重建可操作的 PreparedUpdate。

下面的方法可由现有 MonoBehaviour 的 `StartCoroutine` 驱动。`cacheRoot` 必须是应用明确管理的绝对私有目录；项目/平台闭包必须与该 Store 的 owner 完全一致。此例只查询，不要求 built-in 快照，不初始化资源服务，也不会认领一个已有但无 owner 的目录。

```csharp
using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using YLFramework.Resource;

public static class ArtifactQueryExample
{
    public static IEnumerator Inspect(string cacheRoot, CancellationToken cancellation = default)
    {
        var store = new ArtifactStore(cacheRoot, new[]
        {
            new ResourceProjectMount("my-project", "StandaloneWindows64", "app-v1")
        });
        var configuration = new ResourceConfiguration(store);
        ArtifactStoreReport report = null;
        var query = configuration.QueryArtifactsAsync(value => report = value,
            maxEntries: 10000, cancellation: cancellation);
        try
        {
            while (query.MoveNext())
            {
                yield return query.Current;
            }
        }
        finally
        {
            (query as IDisposable)?.Dispose();
        }

        Debug.Log($"Store={report.Status}; observed={report.ObservedBytes}; " +
            $"sizeComplete={report.IsSizeComplete}; candidates={report.CleanupCandidateBytes}");
        foreach (var issue in report.Issues)
        {
            Debug.LogWarning(issue);
        }
        foreach (var entry in report.Entries)
        {
            Debug.Log($"{entry.ProjectId}/{entry.Platform} {entry.Kind} {entry.State} " +
                $"{entry.Path}; bytes={entry.ObservedBytes}; sizeComplete={entry.IsSizeComplete}; " +
                $"protection={entry.Protection}; candidate={entry.IsCleanupCandidate}; " +
                string.Join("; ", entry.Issues));
        }
        // 不根据 Path 或旧 candidate 自行删除。实际清理另行显式调用 CleanupVersionsAsync。
    }
}
```

- `Projects` 是明确配置的项目/平台/App 闭包，不扫描整个 persistentDataPath 或工程寻找其他 Store。`Entries` 包含版本、staging、partial 文件组、控制元数据及受管层级中遇到的未知项。`PreparedUpdates` 只复制记录身份和每项目 from/to，不生成可激活对象。
- `Protection` 是可组合的 `Active`、`LastKnownGood`、`PreparedFrom`、`PreparedTo`、`Mounted`、`Held`、`InFlight`。active/LKG 的完整镜像及 Prepared 整组闭包复用原校验；Mounted/Held/InFlight 来自当前 authority 和仍排空的旧会话，与正式清理共用持有快照来源。`ProtectedVersions` 也保留磁盘条目之外的明确内置/持有路径，不遍历它们。版本操作在途时通过 `InFlightOperations` 和 Busy 受限结果报告。
- `CompleteRecord` 仅代表有效 owned Complete 身份及登记文件集合，**不是 Bundle 内容 SHA-256、可挂载性或签名新鲜度认证**。查询校验必要控制记录和 Complete 身份，不默认重读全部 Bundle；原清理仍保留其完整复验。已知残余目录可为 `Incomplete` 且仍是原规则允许回收的 owned remainder。损坏记录、未知内容、外来归属、链接和读失败不成为候选。
- `ObservedBytes` 是普通文件的逻辑长度，包含实际观测到的数据和控制文件；partial 的 `.json`、`.part`、`.next` 作为一组只算一次，版本/staging 父项和其文件不重复计数。`IsSizeComplete=false` 表示缺少可读统计或未知目录内部未遍历，此时字节数仅是已观测下界，不是完整的 0。未知目录、symlink/junction 目标不扫描。`CleanupCandidateBytes` 仅在 Ready 时统计已确认候选的逻辑字节，不等于实际磁盘释放空间或 native memory。
- `NotConfigured`、`Missing`、`RecoveryRequired`、`Invalid`、`Unreadable` 分别表示无 Store、root 不存在、待显式恢复、无效归属/控制闭包、读取失败；不是同一种空目录。Ready 只表示本次保护上下文/扫描完成，各条目仍须查看 `State`、`Issues`、`IsSizeComplete`。局部版本读取失败会保留为 Unreadable 条目；控制闭包无法确认时整体不提供候选。
- 每段最多 32 个步骤，默认总上限 10000，可显式选择 `1..100000`。上限含必要复查，达到上限返回 Incomplete；取消返回 Canceled。中止枚举器须 Dispose，直接中止不会产生报告。文件系统单次调用仍受 OS IO 延迟影响，不承诺硬实时帧预算。
- 查询不占用整个扫描期的 Store 写锁。任一进程内 Store 事务、相关会话或持有集合变化返回 Changed；当前操作/已有外部写锁返回 Busy。观测路径的属性、长度和 mtime 变化也使结论失效。所有非 Ready 结果的候选标记均撤销、`CleanupCandidateBytes=null`，须在正常操作结束后重新查询。该边界针对遵守既有主线程/Store 协调的调用，不是跨进程文件系统原子快照，也不防绕过合同并伪造相同属性的外部篡改。

报告和集合均为脱离内部记录的只读副本，不增加资源 pin，不持有 Unity 对象。报告只描述 `StartedUtc..CompletedUtc` 的一次观测；取得报告之后任何保护变化都可使预览过时。实际删除必须显式通过已初始化服务的 `CleanupVersionsAsync()`，重新核对当前归属、active/LKG、Prepared、在途和持有责任；API 不接受旧报告作为删除授权。

## 代码生成

- 输入为正式发布 Catalog；默认文件 `Assets/YLFrameworkData/YLResourceReferences.cs`、namespace `YLFramework.Resources`，仅生成 ProjectId/Addresses/Aliases/Groups/SubAssets 键，不是可变目录或加载器。
- 程序集 `YLFramework.Generated.ResourceNames` 不依赖框架/Unity/Editor/插件；预定义程序集自动引用，业务 asmdef 显式引用它及 YLFramework.Runtime。
- 标识符按 Unicode 字母/数字分词并转为 Invariant PascalCase，例如 `hero_icon` 生成 `HeroIcon`、`http_url` 生成 `HttpUrl`，保留 `UI/IO`；数字开头使用必要的 `_` 前缀。只改 C# 标识符，不改变常量中的资源地址、alias、Group 或 ProjectId；转换后重名仍拒绝生成，不自动添加后缀。
- 碰撞/类型校验先于写入，未知/篡改文件与孤立 meta 不接管；幂等更新保留 GUID，失败保留旧输出。发布、生成、Unity 消费者编译是三个结果；生成物不属默认框架导出。

## 常见错误

Unity `2022.3.62f3c1` 的 Windows Mono 文件 API 仍有 260 字符限制，原始 Workspace 与加密交付文件均须在边界内。使用足够短的工程/输出路径，实际余量取决于 Bundle 名字，没有适用于所有工程的固定工程路径上限。构建器在认领 Workspace/调用 native 管线前检查实际产物全路径，超限明确拒绝，不自动改名、迁移或清理已有产物。

| 结果 | 处理方式 |
|---|---|
| ConfigurationConflict | 重用同一配置对象；核对已有 Resource/Core 绑定，不强制重置外部会话 |
| Busy | 查看 Diagnostics，归还自身 Lease/实例/Scene/Audio 或其他 startup，再重试；不全局清缓存/强停业务 |
| AddressNotFound / TypeMismatch | 核对 ProjectId、逻辑地址、子资产 key 和 compiledTypes，不把磁盘名作地址 |
| 快照/兼容验证失败 | 核对确切 digest、目录、平台/完整 Unity 版本/App 标识和文件完整性，不改成模拟来源 |
| StaleBase | 当前版本已变化，重新 Check/Prepare；要回退则明确 Rollback |
| SessionUnavailable | 旧请求/Provider 不能绑定新会话，结算旧启动对象后显式启动 |
| Unsupported | 检查模式/平台/来源及是否配置 Store；不是自动忽略或降级指令 |
| ContentKeyUnavailable | 向应用 provider 配齐当前项目/KeyId 后显式重试；不是文件损坏，不自动下载或切换版本 |
| ContentAuthenticationFailed / ContentFormatUnsupported | 拒绝当前输入及其依赖闭包的新取得；核对确切交付、模式与 key，不回退明文/Simulation |
| ContentBufferUnavailable | 释放本应用拥有的输入责任后显式重试，或在下次启动配置足够预算；不会自动扩容或驱逐活跃输入 |
| 资源不在已登记目录中 | 在“项目设置”登记其真实来源目录，或改选范围内资源；不会自动扩大读取范围 |
| 目录重复、重叠或失效 | 根据显示的路径/GUID保留唯一有效范围；修正配置不会自动删除源文件或Group条目 |
| 地址或alias冲突 | 根据双方来源，为其中一个设置显式地址、不同持久目录前缀或别名，再重新计算 |
| 配置尚未保存 | 保存资源项目及已修改图集规则；若有dirty场景需单独处理，顶部保存不代为保存全部内容 |
| 已发布但地址常量生成失败 | 已发布版本仍有效。修正输出目录或生成配置后单独重试代码生成，不自动重发或回滚 |
| 无法确认文件属于当前资源项目 | 不覆盖或删除。保留文件，按技术详情核对Project、配置、GUID/meta和登记记录，不通过来源登记取得写权限 |

## 可选 Bundle 内容加密

内容加密是 Bundle 交付的可选编码，不是签名、密钥分发服务或另一个加载器。应用持有 key provider，资源权威和租约图不变。

- Project 的 `contentEncryption` 默认 `None`，旧明文发布不需重写。`None` 继续 `LoadFromFile`/`LoadFromFileAsync`，Editor Simulation、Resources 和直接图片来源不改变。新增默认字段不进入 None 的 build settings 身份；正常保存 SO/代码输入变化仍可能使旧 input fingerprint 自然失效一次。
- 启用 `Aes256GcmHkdfSegmentedV1` 时，填写仅为标识的 `contentKeyId`，覆盖该 Project 的内容包、共享依赖包和二进制 root manifest。`.manifest` 文本旁车不加密。正式后端固定为 Windows CNG `Microsoft Primitive Provider` AES-256-GCM，官方 BC 2.7.0 netstandard2.0 仅提供 HKDF-SHA256；不复制系统 DLL，不静默软件 GCM fallback。
- 实现仅开放 Windows x64 CNG 后端；Windows64 IL2CPP 还硬性核对精确 Unity `2022.3.62f3c1` / `6000.3.27f1`。本指南的固定候选为上述版本的 Editor/Mono/IL2CPP 配置，不因 None 可编译或 Mono 路径未拦截某个版本就推断其他引擎、主机或移动平台已验证。远端另有精确版本门禁，见 [共存与范围](#共存与范围)。

Editor 使用显式进程 secret 名或调用方 provider，不在 SO、Inspector、EditorPrefs、指纹或日志中保存 key。工作台默认进程变量为 `YL_RESOURCE_CONTENT_KEY`，严格接受 Base64 编码的 32 字节；非秘密 KeyId 与环境变量名不是密钥材料。

以下为 Editor 方法内的配置/构建片段，`project` 是已有的、归属明确的 ResourceProject；文件顶部需引用 `UnityEditor`、`YLFramework.Editor.Resource` 和 `YLFramework.Resource`。此调用会明确保存该 Project，构建目标须已激活且可用：

```csharp
project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
project.contentKeyId = "content-v1";
EditorUtility.SetDirty(project);
ResourceEditorOperations.SaveProject(project);
var keys = ResourceEditorOperations.ContentKeyFromEnvironment(
    project.projectId, project.contentKeyId, "YL_RESOURCE_CONTENT_KEY");
var build = ResourceBundleBuilder.BuildWithContentKey(
    project, BuildTarget.StandaloneWindows64, keys);
var release = ResourcePublisher.PublishWithContentKey(build, "app-v1", keys);
```

选择 None 时设置并保存 `project.contentEncryption = ResourceContentEncryption.None`，使用 `Build/Publish` 即可，不需要 key。构建与发布成功后记录返回的确切 ProjectId/Platform/App/CatalogDigest；应用明确 Open 的发布目录或 OpenBuiltIn 身份，不猜最新目录。后者不读取样例 `snapshotDirectory`、raw workspace 或开发机发布绝对路径。

raw workspace 仍供原 BuildPipeline 与图集前置使用，独立 delivery 在 Catalog 封口前生成。Catalog v1 的 length/SHA 始终描述实际交付字节；Report 分列 raw/delivery SHA、字节及编码成本。同身份/key 的候选仅在本次完整认证成立时复用确切密文，新加密使用新随机材料；同一 KeyId 换材料不会偷偷重新加密为成功。轮换使用新的 KeyId/配置分区。重复发布也需当前 key 及内容验证；失败/取消不切 index、不破坏旧版本。

Runtime 的应用 provider 实现 `IResourceContentKeyProvider.TryCopyKey(projectId, keyId, destination)`，向框架给出的确切 32 字节目标复制 key，不得持有该缓冲。provider 生命周期归应用，框架仅持自己的副本/派生状态；应用回调只发生在主线程，不发生在 native Stream 回调。不要在 provider 异常文本中携带 key；框架保留诊断因果但不串出其敏感原文。

下面只展示业务协程的成功路径，`mounts/cacheRoot/appOwnedKeys/address` 由应用提供；文件顶部需引用 `UnityEngine` 和 `YLFramework.Resource`：

```csharp
var config = ResourceConfiguration.Create(
    ResourceLoadMode.Bundles,
    mounts,
    cacheRoot,
    decryption: new ResourceDecryptionOptions(appOwnedKeys, 128L * 1024 * 1024));
var startup = new ResourceStartup(config, bindCore: true);
using (var start = startup.StartAsync())
{
    yield return start;
    start.TakeResult();
}
using (var request = startup.Resources.AcquireAsync<GameObject>(address))
{
    yield return request;
    using var prefab = request.TakeResult();
    // 仅在租约持有期消费 prefab.Asset，不把裸引用留到归还之后。
}
using (var stop = startup.StopAsync())
{
    yield return stop;
    stop.TakeResult();
}
```

只消费本地/内置时 cacheRoot 可为 null。以上不是完整错误收尾：TakeResult 失败会抛错，宿主仍须在失败/取消/停用时结算责任并执行或重试 Stop，见 [可运行最小接入](#可运行最小接入)。不要吞错后继续取资源。实例、Audio 尾轨/缓存或 Scene 责任也须归还后才能 Stop；归还 SceneLease 不等于卸载 Scene。

- 同步取得如实承担完整 SHA/输入准备成本；异步入口的纯 IO/SHA/认证可取消并在 worker 执行，应用须等待请求终态后 `TakeResult`，不能假设方法名为 Async 却总是即时完成。native 开始后不靠提前关闭输入抢占取消。
- 每个共享 BundleRecord 只有一个输入。输入与 CNG owner/预算保持到真实 native unload；SceneLease 归还不等于 Scene 卸载，实例 destroy-pending、Audio 尾轨及缓存仍沿原责任图结算。实例化、Ready 复用、Group、Scene、Audio 缓存的新借出均执行依赖闭包故障准入；旧已交付对象不因首 fault 被强销毁。
- 64KiB 分段、两个明文缓存槽和 32KiB native read buffer；单输入固定数据缓冲为 196576B，默认 128MiB 预算不预分配整笔。新增闭包先原子预留，全部 live 输入与在途预留计量，共享不双计；不足明确失败后显式重试。`GetContentDiagnosticsSnapshot()` 与 `GetDiagnosticsReport().Content` 分列数据预算、native bridge 声明值及进程内该适配器的 CNG provider/key/key-object 成本，绝不是整个密码模块或进程总内存上限。
- 初始化成功前只对当前所选项目/KeyId 集合做一次轻量认证，同配置 join 不重复；不要求未选历史 key，不以创建 native Bundle 代替探针，全 None 不执行额外密码工作。每次 Prepare（包括重复调用）仍完整验证 To 闭包的 SHA/内容；幂等复用已安装版本及相同准备记录不代表跳过复验。激活/回滚在提交前完整认证，持有稳定输入到提交结束。恢复、Mount、Check 和只读 Store 查询不取 key、不解密；缺 key 不隐式重下载或切 LKG。
- Catalog v1 不变不代表旧客户端支持密文。签发加密 release 前须确认渠道仅投放给支持此编码的客户端；签名 payload 不增加能力协商字段，不能继续投放给不支持加密的旧客户端。远端仍只开放上述精确版本的 Windows64 Mono/IL2CPP，不因加密配置放宽平台或密码后端。

### 轮换与交付

- 轮换创建新的KeyId和密文发布，先Check/Prepare再在业务neutral边界激活；保留必要旧key才可显式回滚到旧密文LKG。缺旧key明确失败，原active/LKG及发布序号合同不改变。回滚目标仍由原LKG选择，不把“历史目录存在”当作回滚授权；回滚兼容明文LKG不需要内容key。
- 远端length、SHA、Range、ETag描述**同一份固定密文**。续传须有实际partial、强ETag与If-Range，并按确切offset追加；服务器200忽略Range要重置，实体变化要重新获取，错误/损坏不能提交半成品。最终还须通过内容认证和原Prepare/激活，不能把HTTPS或SHA成功当作解密成功。缺/错key不是网络重试理由。
- `ResourceBuiltInDeployer.Preflight/Deploy/Recover` 复制/验证交付字节，不需内容 key，不改 Catalog/Complete/密文；导入后经 OpenBuiltIn 严格定位到 `payload~`。Runtime 仍须提供 key。只读 Store 查询无需 key、初始化、恢复或解密；预览不允许删除，实际清理复查最新保护状态。
- **Resource发布快照**使用 `ResourceEditorOperations.ExportRelease(release, destination)`，目标为工程外已有本地父目录下**不存在且等于CatalogDigest的子目录**；精确交付Catalog、Complete和登记文件，不包含raw缓存或内容key。
- **YLFramework 框架包**使用框架导出菜单，或 `YLFramework.Editor.Distribution.ExportUnityPackage.Export(absolutePackagePath)` 向现有本地父目录导出。API 默认拒绝覆盖、不保存其他资产；菜单可显式保存/确认覆盖。包包含源码、必要 meta、样例和随附 BC DLL，不含 Tests、原游戏、宿主扩展、临时 runner/缓存/私钥/证书或系统 CNG DLL；安装与依赖见 [文档首页](../README.md)。
- BC 确切来源、官方 2.7.0 netstandard2.0 二进制 SHA 见 [`Plugins/BouncyCastle/SOURCE.txt`](../../Plugins/BouncyCastle/SOURCE.txt)。UGUI/Json.NET 等依赖和商业插件再分发限制不因导出消失；成功导出不能代替干净工程导入、编译和运行验证。

**已知风险：**Unity Scene native 接手后，延迟 Stream 读取失败可能引发 native 崩溃，不能承诺所有损坏输入都变成受控资源错误。此风险不包含合法输入崩溃、定位/竞争/提前释放或取消/退出/预算缺陷。native 前完整密文 length/SHA、首末认证、读取时逐段认证，以及发布/Prepare/激活/回滚的完整认证仍须保留；不输出完整明文文件或使用整包内存 fallback。

## 共存与范围

| 能力或环境 | 当前实现边界 |
|---|---|
| Editor Simulation | 仅 Editor 工厂，必须显式选择，不作为 Bundle 失败回退 |
| 普通本地 Bundle / 内置快照 | mount/Catalog 接受 StandaloneWindows64、StandaloneOSX、StandaloneLinux64；挂载时须与当前平台及完整 Unity 版本匹配。构建还要求已激活、安装且主机可读回，不表示三平台都完成实际运行验证 |
| Windows 远端更新 | WindowsEditor，或精确 Unity `2022.3.62f3c1` / `6000.3.27f1` 的 x64 Mono/IL2CPP Windows Player 固定候选；其他 Player/32 位/引擎版本明确拒绝；loopback pin 不代替默认 CA 链正向联网验证 |
| 加密内容 | Windows x64 CNG；IL2CPP 额外硬检查上述 Unity 版本。其他主机没有软件 GCM 回退 |
| Windows64 IL2CPP 应用 | 基线为上述版本、Low 裁剪/C++ Release；不同裁剪与新增反射/泛型入口须目标验证 |
| Android / iOS / WebGL Bundle | 当前 mount/Catalog 不开放，JAR/WebGL 提取未实现，不只是缺设备测试 |
| Resources / 直接图片 | 不受 Bundle mount 三桌面枚举约束；仍受各自来源、文件系统、Unity API 和目标平台行为限制，不能据此推断上述更新/加密能力可用 |

原游戏、已退休宿主扩展、MainModule 与历史资产保持独立链，不是框架接入前置；不能混用旧 Manager/Loader 与正式 ResourceSystem 来共享资源权威。Variant/透明晚绑定、archive 与其他平台扩展未由当前 API 提供。

## 历史提示

整合、自动图集、构建/下载成本、内置部署、Mono/IL2CPP 与独立接收工程的历史结果见 [整合记录](../IntegrationPlan.md)。详细方案与冻结证据保留在 Git 历史中，不是默认导出包必需文件。历史记录中的失败、未定位回滚 UpdateFailed、测量能力限制及 Scene native 风险保留原结论，不升级为当前验证结果。

历史通过只针对当时源码、配置和载体，不表示当前工程重新完成了 Unity 测试、干净导入、Player 或默认 CA 链正向联网验证；也不保证其他主机、引擎、高裁剪等级、固定性能或零分配。
