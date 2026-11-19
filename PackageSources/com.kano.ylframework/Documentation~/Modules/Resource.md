# 资源管理

> [文档首页](../README.md) · [安装](../Installation.md) · [资源制作](Resource/Authoring.md) · [内容交付](Resource/Delivery.md) · [音频](Audio.md)

YL Resource 以官方 Addressables 为唯一资源内核。官方负责定位、依赖、加载、Bundle、Catalog、缓存和内容构建；YL 负责业务 Scope、独立 Lease、实例复用、框架接入与责任诊断。不维护另一份资源目录、Bundle 依赖图或版本存储。

Runtime namespace 为 `YLFramework.Resource`，Editor 为 `YLFramework.Editor.Resource`，属于主包。主包正式依赖 Addressables；安装包、构造配置、读取 `ResourceSystem.Current` 或打开工作台都不会初始化资源。操作及 Unity 对象访问在主线程，不把 `Task.Run` 当加载入口。

## 最小接入

应用组合根长期持有一个 `ResourceStartup`，等待 `StartAsync()` 成功后才使用 `Resources`。同一执行会话同时只允许一个启动所有者；其他模块借用服务并创建自己的 Scope，而不是再创建 Startup 加入会话。

```csharp
// startup 是应用 owner 的字段，关闭失败时继续保留，不能在方法退出后丢弃。
startup = new ResourceStartup(new ResourceConfiguration(),
    bindCoreResourceProvider: true, audioProjects: new[] { "game-audio" });
await startup.StartAsync();
var resources = startup.Resources;
```

Core 默认绑定；可传 `false` 只使用资源能力。音频命名空间登记不启动 Audio、不修改偏好或接管 Audio 生命周期。已有外部 Core Provider 冲突会明确失败，不强制替换。

完整组件、取消、失败、`OnDestroy` 收尾及重试见随包的 [Resource Startup](../../Samples~/ResourceStartup/Runtime/ResourceStartupExample.cs)。从 Package Manager 显式导入完整样例，创建并保存官方内容，打开生成的 Bootstrap 后在 Play 中用组件菜单显式开始。样例展示 Scope 重用与销毁绑定、预热与归还前复位、Core 文本、音频 Clip 租约和 `ResourceImage`。可选的 `startupCatalogId` 只在受控首次启动检查后应用指定 Catalog，再开放业务；本地样例默认不更新。Editor 创建入口只保存本次新建的场景，不保存或关闭用户场景。

## 默认用 Scope 借读

Scope 是业务生命周期，不是地址分组或新的全局缓存。普通调用直接取得借用对象，不需要为每次读取手动编写 Request/TakeResult/Lease 三层管理。

```csharp
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using YLFramework.Resource;

public static class ScreenResources
{
    public static async Task ReadAsync(ResourceSystem resources, CancellationToken cancellation)
    {
        var scope = resources.CreateScope("inventory-screen");
        try
        {
            var text = await scope.LoadAsync<TextAsset>("game/text/welcome", cancellation);
            Debug.Log(text.text);
            // 同 Scope、同精确 key 和类型复用一份持有，不因刷新次数累积引用。
            var same = await scope.LoadAsync<TextAsset>("game/text/welcome", cancellation);
            Debug.Assert(ReferenceEquals(text, same));
        }
        finally
        {
            await scope.CloseAsync();
        }
    }
}
```

- `LoadAsync<T>` 支持字符串和 `AssetReferenceT<T>`；后者冻结官方 RuntimeKey，不占用 AssetReference 实例上的单一 operation 槽。
- 不同 Scope 各持自己的引用，底层共享工作交给 Addressables。同 Scope 的等待者独立取消；`CloseAsync` 等待本 Scope 迟到的官方引用归还，不等待无关所有者的请求或独立 Lease。
- `address` 是作者在 Addressables Entry 中配置的地址；`key` 是运行时解析输入，可来自地址或 `AssetReference.RuntimeKey`。单资产或场景 key 必须解析到恰好一个符合类型的 location；缺失和歧义不取第一项。Labels 用于多资产组织或预下载，不把官方 Group 当 Runtime 集合。
- 失败项允许重试。关闭后拒绝新投递；成功完成到领取之间 Scope 已关闭，也不能交付失效对象。
- `TryGetLoaded<T>` 只借读当前 Scope 的就绪内容，不加载或增持。借用不能越过 Scope 生命周期。

可选 `scope.BindToDestroy(gameObject)` 会在真实场景对象销毁后关闭 Scope，覆盖从未激活的对象；不随 `OnDisable`、隐藏或暂停自动关闭。View/Panel 关闭和池归还要在业务接点显式关闭本轮 Scope，下一轮重新创建，不能复用已关闭 Scope。

## 显式独立 Lease

跨 Scope 保活或需要精确归还时，使用高级入口：

```csharp
using var asset = await resources.AcquireAsync<TextAsset>("game/text/welcome", cancellation);
using var anotherOwner = asset.Retain("background-reader");
Debug.Log(anotherOwner.Asset.text);
```

`AcquireAsync` 每次取得独立责任；`Retain` 明确增持。复制裸对象或官方 Handle struct 不是增持。`Scope.Own(lease)` 转交现有责任，不增加引用；重复或跨 Scope 归属拒绝。资产、实例、场景是各自明确的 Lease，不接管任意外部 `IDisposable`。

`AssetLease.Asset` 只借读；归还后不得继续使用。`TryAcquireLoaded<T>` 仅从 YL 已有的就绪责任增持，不反射官方缓存，也不阻塞加载。冷资源全部走异步，不调用 `WaitForCompletion`。

`ResourceAddress(projectId, path)` 仍可表达框架模块的业务命名空间，投影为官方 `projectId/path`，不表示私有 Catalog 挂载。新资产和子对象优先使用官方地址/AssetReference；旧 localId 字符串不能直接充当官方子对象名。

## 必需资源批量

```csharp
using var batch = await resources.AcquireBatchAsync<TextAsset>(
    new[] { "ui/common-texts", "game/text/welcome" }, cancellation);
foreach (var text in batch.Assets)
    Debug.Log(text.text);
```

显式地址和官方 Labels 按结果并集加载，同一个官方 location 不重复交付，不承诺 Label 内部顺序。Editor Fast Mode 为同一条目创建的等价 location 也会合并；无法证明等价的共享身份视为歧义，不用依赖哈希判断相等。每个 key 必须有符合类型的结果；任一加载失败就不交付部分结果。`AssetBatchLease<T>` 归还一个官方批量引用，也可用 `Scope.Own(batch)` 转交已有责任。取消是本方退出等待；在途官方工作继续排空，不影响其他调用者。

## await 与协程

`ResourceRequest<T>` 同时支持 await 和 yield，共用终态与清理逻辑，不依赖部分平台不提供的 `AsyncOperationHandle.Task`。

- await 为唯一领取者预约结果，`GetResult` 领取并结束请求跟踪；不能重复 await，也不能再手动争抢同一结果。
- `yield return request` 只等待；协程随后检查状态或 `TakeResult()`，并用 `using`/`Dispose` 结束请求。已领取的独立 Lease 不由请求再次释放。
- 未领取的拥有型结果由请求关闭归还。取消等待不是杀死官方共享加载；晚到结果有本方排空责任。
- `Completed` 只表示请求进入终态，不代表结果已领取，也不代表底层操作已排空。
- await 取消抛 `OperationCanceledException`，保留调用 token；失败保留原始资源异常。进度不是完成或交割凭证。

```csharp
using var request = resources.AcquireAsync<TextAsset>("game/text/welcome");
yield return request;
using var lease = request.TakeResult();
Debug.Log(lease.Asset.text);
```

## 新建与实例复用

| 入口 | 语义 | 归还 |
|---|---|---|
| `Scope.InstantiateAsync` | 每次新建，Scope 持有 | 关闭时销毁，不入池 |
| `Scope.RentAsync` | 明确借池，缺少才新建 | 关闭时归还本次 checkout |
| `ResourceSystem.InstantiateAsync` | 返回独立 `InstanceLease` | Dispose/Return 销毁 |
| `ResourceSystem.RentAsync` | 返回独立池 checkout | Return 回池，Discard 销毁 |

两条路径都通过 Addressables 取得 Prefab 并保活依赖。只有池借出才复用状态，框架不自动重置任意脚本、事件、动画或物理状态。实例在创建前预约，登记后才激活；`Awake`/`OnEnable`/归还重入关闭时拒绝交付，真正死亡后才释放 pin。

池复用 Foundation 机制，默认 SceneScoped、空闲上限 32。`GetOrCreateInstanceCache(prefabLease)` 取得或创建共享池，新建时独立增持 Prefab，空池也保留。`RetireInstanceCaches()` 清除当前池的空闲实例，不强制销毁借出中的实例；后者在归还时销毁。已有 Prefab 租约的同步 `Instantiate` / `Rent` 不执行 IO。

## 场景与关闭

Scope 简单场景入口自动激活，默认 Additive：

```csharp
var world = resources.CreateScope("level-one");
var scene = await world.LoadSceneAsync("game/scenes/room");
// 结束本关时等待实例、场景及资产依次收尾。
await world.CloseAsync();
```

分阶段加载使用独立 `SceneLease` 的 `ActivateAsync/UnloadAsync`，也可显式交给 Scope。`SceneLease.Dispose` 发起收尾但不代表已卸载；完成依真实 Scene identity、原生操作和卸载事件确认，不按名称猜测。

依赖与加载仍走官方 ResourceManager/default SceneProvider；原生卸载由 YL 协调，真实结束后恰一次归还 H，不进入官方门面的另一份场景自动追踪。首版不支持自定义 SceneProvider 或自定义场景资产数据 Provider。

延迟激活保护默认 30 秒，范围 `(0,300]`。取消也须放行并排空原生队列。Single 已发生的切换不承诺回滚；最后场景无法卸载时保留 owner 和可重试错误。保留明确的 Bootstrap 中立场景，不运行时制造空场景让停止假成功。

Core 的默认 Module 场景与 SceneController 默认仍为 Single，不等于 Scope 默认 Additive。Module 关闭只结束自己的等待；已成功场景归应用场景 Scope，Core 的观察结果没有卸载权。需要保留 Bootstrap 时不要设置默认 Single 启动场景，由应用 Scope 明确加载业务场景。

## 图片与系统接入

`AcquireImageAsync` 通过受限官方 Provider 加载完整 HTTP(S) URL 或绝对本地 PNG/JPEG，仍使用同一 Handle/Lease。默认编码上限 32 MiB、16M 像素、并发 4；空闲缓存为 128 项、32 MiB RGBA 估计、30 秒 TTL。`ClearIdleImageCache()` 只归还空闲缓存引用，保留活跃租约。加载不改全局下载并发/证书策略；换代用版本 URL，不保证同 URL 强制刷新。

`ResourceImage` 隔离替换、停用、销毁与晚回调；placeholder 借用，自建 Sprite 克隆自己销毁，不能销毁共享 Texture。直接指定图集及 Sprite 名时，先销毁组件自建的克隆，再归还 Atlas Lease。`AssetReferenceAtlasedSprite` 按 `Sprite` 查询，图集子对象由官方 Provider 持有和释放。

Core 保留 `ICoreResourceProvider` 窄合同，不反向引入 Addressables 类型。`CorePrefabLease.Rent` 允许后端复用实例，不消费 Prefab 租约。Audio 播放、缓存和交叉淡化尾轨各持独立 Clip 责任。DataAssets/Gameplay 的配置根由各 Manager 持有，条目只借读；Scope 不能替代业务登记注销。Localization 仍走官方包，与 YL 共享官方设施而不共享释放权。详见 [Audio](Audio.md) 及各选装包手册。

## 停止与诊断

先结束业务模块、Audio 尾轨/缓存、界面与独立 Lease，再等待 `startup.StopAsync()`。停止冻结新工作，先检查外部独立责任；存在则及时返回 `Busy`，错误消息记录本轮阻塞租约的 ID、键和持有者，不先卸场景或销毁 parent 破坏仍借出的对象。归还后重试原启动 owner；无外部阻塞才关闭自有 Scopes、退休缓存、等待真实排空。

`Scope.Dispose`、`Startup.Dispose` 是无法等待接点的发起操作，不是关闭成功证明。异步失败要保留原 owner、原因和可重试责任。Unity 退出可能不再有下一帧，不能将尚未完成的清理写成通过。

`GetDiagnostics()` 返回 YL lease、owner/key、Scope、请求、工作和排空的值快照，不保活资源，不是官方总引用数/native 内存。默认无历史；配置非零 `diagnosticHistoryCapacity` 后仅将显式查询的快照记录到有界历史，使用 `GetDiagnosticsHistory/ClearDiagnosticsHistory` 读取/清除。不开窗口不自动扫描全图或采样。

缺失 key 检查官方 Entry/类型；歧义明确修配置；`ConfigurationConflict` 检查唯一 Startup 与外部绑定；`Busy` 找真实持有者；`SessionUnavailable` 结算旧会话而不是把旧请求挂新会话。性能以相同内容、分包、平台与正确生命周期测量，不能由包装层代码量推断优于裸 Addressables。

## API 改名迁移

- 将 `CorePrefabLease.Instantiate` 的调用和覆写改为 `Rent`。Resource 的 `Instantiate` / `InstantiateAsync` 仍保证新建。
- 将 `GetInstanceCache`、`ClearInstanceCaches`、`ClearImageCache` 分别改为 `GetOrCreateInstanceCache`、`RetireInstanceCaches`、`ClearIdleImageCache`。
- 读取资源来源 ID 改用 `ResourceException.SourceId`，构造时使用 `sourceId:`。`Source` 仍可编译，但现在读取的是 .NET 异常诊断字符串。
- 以字符串调用 `ResourceScope.LoadAsync`、`ResourceSystem.AcquireAsync` 或 `ResourceSystem.LoadSceneAsync` 时，具名实参从 `address:` 改为 `key:`；`SceneLease.Address` 改为 `Key`。不需要修改已配置的地址、标签或序列化字段。
