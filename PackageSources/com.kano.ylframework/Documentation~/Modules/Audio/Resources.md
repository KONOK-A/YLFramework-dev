# 命名音频与素材库

> [音频播放](../Audio.md) · [高级播放与诊断](Advanced.md) · [资源管理](../Resource.md) · [文档首页](../../README.md)

本页用于按地址播放、复用素材组及管理 Clip 保活。直接 Clip 播放不需要 Provider；只有命名入口才取得资源租约。Provider 不等于控制通道，AudioClipLibrary 也不是另一个播放器。

## 命名播放

先由应用显式启动并持有 `ResourceStartup.Resources`，再取得项目 Provider。启动与关闭见[资源管理](../Resource.md#初始化与关闭)，Audio 不隐式初始化 Resource。

```csharp
using YLFramework.Audio;
using YLFramework.Resource;

// resources 是已启动、由应用持有的 ResourceSystem。
var provider = resources.GetAudioClipProvider("game-audio");
var click = new AudioClipAddress(provider, "ui/click");
AudioHandle handle = AudioKit.PlaySoundNamed(click);

var music = new AudioClipAddress(provider, "music/theme");
AudioPlayRequest request = AudioKit.PlayMusicAsync(music,
    onStarted: (clip, originalHandle) => originalHandle.SetVolumeScale(0.8f));
// owner 退出或撤销本次播放时：
request.Cancel();
```

| 用途 | 同步资产取得 | 异步资产取得 |
|---|---|---|
| 缺省分类 | PlayMusicNamed / PlayVoiceNamed / PlaySoundNamed | PlayMusicAsync / PlayVoiceAsync / PlaySoundAsync |
| 音乐过渡 | CrossFadeMusicNamed | CrossFadeMusicAsync |
| 命名控制通道 | channel.PlayNamed | channel.PlayAsync |
| 通道借源，仅 Exclusive | channel.PlayBorrowedNamed | channel.PlayBorrowedAsync |

同步入口返回 `AudioHandle`，异步入口返回 `AudioPlayRequest`。缺省 Music 的 null Options 循环，Voice/Sound 非循环；通道的 null Options 始终非循环，循环要显式传 `MusicDefaults` 或 Loop=true。Sound 回调参数顺序为 onFinished 再 onStarted，其余命名入口为 onStarted 再 onFinished。

命名 Crossfade 使用 `address, duration, options=null, onStarted=null, onFinished=null`，没有直接 Clip `CrossFadeMusic` 的 ignoreTimeScale/curve 可选参数。它复用内核默认的 unscaled/EqualPower 过渡，加载可提交前不截断旧曲，尾轨各持有原资源租约。

同步仅承诺资产取得同步，不保证后台解码或 Streaming 已同步就绪。同一 Audio 缓存身份正在异步取得时，同步取得抛错，不阻塞主线程或抢占 producer；改用异步入口共享结果。跨 Audio 缓存遇 Resource 同身份在途，同步取得也明确返回 InProgress。

### Provider 身份与实现责任

`AudioClipAddress` 的完整身份是**同一个 Provider 实例 + 完整且精确的 key**。Project/Bundle/后端上下文应固定在 Provider 或包含在 key 中；不同 Provider、不同 key 不因 clip.name 或 Clip 对象相等合并资源所有权。复用配置好的 Provider，不要每次 Play 创建后端。

自定义 `IAudioClipProvider` 需要实现：

- `bool CanCache`：仅确有可用于闲置缓存的保活责任才返回 true；共享 borrowed 来源应返回 false。
- `AudioClipLease Load(string key)`：成功必须返回独立租约，失败抛异常，不返回未保活 Clip。
- `LoadAsync(string key, Action<AudioClipLease, Exception> completed, Action<float> progress)`：主线程投递一次成功租约或错误，允许在方法返回前同步完成；每次成功责任独立。所有等待者逻辑取消后，producer 仍须完成自身收尾。

`AudioClipLease(clip, owner)` 包装有效 Clip 与非空 `IDisposable` owner，Dispose 恰一次归还并清引用，不自行卸载或 Destroy Clip。owner 释放异常会传播，重复 Dispose 不重试；不要用空 owner 假装持有真实资源。Provider 的调用、进度、完成及归还均须在 Unity 主线程。Audio 不查 Module，也不建立全局资源注册表。

## 请求状态与取消

`AudioPlayRequest` 是一次播放的稳定身份，不是 Task 或通用协程调度器：

| 状态或属性 | 可观察结果 |
|---|---|
| Loading | 等 Provider，Handle 无效，不创建播放 Source，也不占 Sound 预算 |
| Ready / IsAssetReady | 资产已取得并保活，可能仍等 LoadAudioData；Progress=1 仅表示资产取得 |
| IsDataReady | 数据曾达到 Loaded；这是属性而不是独立 State，Streaming 不保证全文件驻留 |
| Accepted | 已绑定原 Voice/句柄，分类 off、通道 Pause 等仍可能阻止首次启播 |
| Started / HasStarted | 曾提交原 Source.Play，不是首采样、DSP 或实际可听见保证 |
| Ended / IsDone | 恰一次终结；EndReason、Error、原 Handle 和开始证据保留，不复活为新请求 |

它不是 `CustomYieldInstruction`，不能直接 `yield return request` 等待；也没有 `Wait()`。协程按业务目的轮询：

```csharp
// 位于 Unity 协程中，文件顶部引用 UnityEngine。
yield return new WaitUntil(() => request.HasStarted || request.IsDone);
// 失败也可能直接终结，不能只等待 HasStarted。

yield return new WaitUntil(() => request.IsDone);
// 再区分 EndReason.Completed 与取消、拒播或失败。
```

循环请求不会自然完成，owner 退出时必须取消。[YL0010](../Attributes.md#yl0010) 会对非泛型 IEnumerator 协程直接 yield 静态类型为 AudioPlayRequest 的表达式给出 Warning；不追踪 object 擦除，不为业务选择等待开始还是结束。特殊调度器如确有其他协议，可局部抑制；配置入口为 `YLFramework/Core/分析器规则`。

`Cancel()` 与 Dispose 等价，可在加载、数据等待、提交重入或接纳后调用，只结束/停止本次原请求，不停止另一等待者或物理取消共享加载。后端不支持物理取消时，逻辑终态不证明 IO 已停；producer 永不完成时，也只能保留其真实清理责任，不能提前归还仍在使用的资源。

EndReason 只有 Completed 表示自然完成，其他为 Canceled、Stopped、Replaced、Unavailable、Failed、Shutdown、Rejected。异步取得/开始错误保存在 Error；同步取得或提交异常向调用方抛出。onFinished 仅通知自然完成，不是全终态事件；进入自然回调前命名请求已记 Completed，但原 Handle 可仍有效，Clip 保活到回调返回。

Exclusive 的最新有效命名请求撤销同拓扑旧 pending，旧播放保留到新资源/数据可提交，加载失败不先截断旧曲。直接 Clip 播放、Stop、通道 Dispose、Sound off 和 Shutdown 同样约束 pending；Overlap 等待者独立。旧会话迟到结果只归还原责任，不创建新 Source/宿主，也不投递新会话的开始/失败通知。

## 租约与缓存

`ResourceAudioClipProvider` 固定所属 ResourceSystem/ProjectId。每次成功取得独立 `AssetLease<AudioClip>`，再包装为 AudioClipLease；同 System/Project 复用 Provider 引用，会话关闭后旧 Provider 失效，不重新绑定新会话。key 是主资产逻辑地址，不是 Bundle 文件名、Resources 物理路径或 SubAsset 选择表达式。

| 持有者 | 何时保活或归还 |
|---|---|
| 直接 Clip 调用方 | 播放、暂停、分类 off 等待重播期间一直保活；Audio 不接管 |
| Audio 缓存中的同 Provider/key | 共享一个真实后端租约，播放、等待者、显式预加载和 idle 消费各自结算 |
| 播放请求 | 数据等待、循环/暂停、Music/Voice off、Crossfade 尾轨、未取消 one-shot 均保留责任 |
| Source 闲置池 | 不持有 Clip；与资产缓存是两套预算 |
| Resource 后端 | 当前不额外缓存零租约 Clip；Resources borrowed 归零只清记录，AB 最后资产/依赖 owner 归还后才 Unload(true) |

```csharp
AudioKit.ClipCacheCapacity = 4;
AudioKit.ClipCacheIdleSeconds = 15;
using (var preload = AudioKit.AcquireClip(music))
{
    AudioKit.PlayMusicNamed(music); // 与预加载共享同一完整资源身份。
}
AudioKit.ClearClipCache(); // 不影响活跃 Music。
```

`AcquireClip` 同步取得显式保活租约，由调用方 Dispose。ClipCacheCapacity 只限**闲置条目数**，ClipCacheIdleSeconds 使用 unscaled 时钟，两项都大于 0 才缓存。缩容、到期和 ClearClipCache 只释放当下闲置，不卸载活跃责任；淘汰释放重入后重新检查是否已被取得。Shutdown 释放 idle。

显式租约可以跨 Audio Shutdown 继续保活，关闭缓存不再收回它作 idle，最终 Dispose 才归还 owner。应用关闭顺序是：停止自己 Audio 请求/尾轨、清不再需要的 idle、归还显式 Clip 租约，再执行所属 ResourceStartup.StopAsync 或由资源 owner 调 ShutdownAsync。仍有这些责任时 Resource 返回可恢复 Busy，不强制销毁 Clip。Audio Shutdown 的迟到 Resource 结果交给关闭缓存后立即归还；Resource 会话退出则失效请求并继续 native 收尾。

## 素材库

通过 `Assets/Create/YLFramework/Audio/Clip Library` 创建 `AudioClipLibrary`，在 `groups` 填写区分大小写的 `name`、选择方式 `selection` 和 `entries`。同组可混用直接 Clip 与命名定位。每次提交只抽选一次，并保存定位快照，不在异步完成后重抽。

`AudioClipEntry` 当前字段只有 `source/clip/key/projectId/weight`：

| source | 配置与责任 |
|---|---|
| Clip=0 | 绑定 clip，由外部 owner 保活 |
| ExternalProvider=3 | 填后端完整 key；向 GroupPlayer.SetProvider 或 Library.TrySelect 显式传同一个 Provider |
| Resource=4 | 填稳定 projectId 与 Catalog 逻辑 key；仅读取已显式初始化的 ResourceSystem |

编号不连续，不能重新压缩。任何未定义 source（包括 1/2）使整个组拒选，即使该项权重为零、旁边有合法 Clip；不跳过、不随机也不替换当前播放。不存在隐藏 BundleName 字段或根据 Bundle 名猜 ProjectId 的逻辑。

Uniform 在有效条目中均匀选择，忽略存储 weight；Weighted 仅正权重参与，0 排除，任一负数/NaN/Infinity 权重使整组失败。空 Clip、空 key、不能定位的候选不参与；缺组、重名组、空/全坏组或非法 selection 返回 false。播放 Options 非法仍由正式 API 抛错，不由 Library 修复。

```csharp
// library 与 channel 由 owner 提供；退出时 Dispose 自己的通道。
if (library.TrySelect("ambience", out var selected))
{
    var options = AudioSoundOptions.MusicDefaults;
    if (selected.IsNamed) channel.PlayAsync(selected.Address, options);
    else channel.Play(selected.Clip, options);
}
```

### 配置素材组组件

1. 添加 `YLFramework/Audio/Group Player`，绑定 Library，Group Name 填选曲组名，Channel Name 填本 owner 唯一稳定名，如 `menu/clicks`。组名选素材，通道名控请求，相同登记名冲突拒绝。
2. 点击音选择 Sound + Overlap、Loop=false；独立环境循环可选 Music + Exclusive，并显式 Loop=true。仅切 Music 不会自动改写 Loop，也不占缺省 Music 双声部。
3. Auto Play 默认 Manual，UnityEvent 绑定无参 Play/Stop 或字符串 PlayGroup。FirstStart 仅首次 Start；EveryEnable 首次也从 Start 播放，以后每次启用重播。
4. Load Async 默认 true；Persistence ID 默认空，不读写通道键。AutoPauseWhenTimeScaleZero 默认 false，路由 Inherit。Inspector Sound 继承全局去重，因为 Unity 不序列化 Nullable 枚举。
5. 需要保留外部 Source 空间曲线/滤镜时显式配置 Borrowed Source，仅 Exclusive 支持；调用前 Stop 并让渡控制，规则见[受控借源](Advanced.md#受控借源)。

停用取消自己的 pending 并停止通道，销毁撤销登记，不调用全局 StopMusic/StopAllSound。Started UnityEvent 在原句柄归入通道后通知，事件内 Stop、失活、销毁或抛错仍收尾原身份。Channel 只返回已登记身份，读取不创建；LastHandle 为最近开始身份，LastRequest 仅记录最近异步命名提交，可查 Error/EndReason，不是额外请求管理表。登记后的 category/mode/name/persistence 身份固定，运行中改 Inspector 不重登；重新创建 owner 才应用新身份。SetProvider 会先 Stop 本 owner 旧请求，再设置显式 Provider。

## 实现入口

- [`AudioClipAddress.cs`](../../../Runtime/Systems/Audio/AudioClipAddress.cs)：Provider、地址和 ClipLease 合同。
- [`AudioKit.Named.cs`](../../../Runtime/Systems/Audio/AudioKit.Named.cs) / [`AudioPlayRequest.cs`](../../../Runtime/Systems/Audio/AudioPlayRequest.cs)：命名入口、状态和取消。
- [`AudioClipCache.cs`](../../../Runtime/Systems/Audio/Internal/AudioClipCache.cs)：共享取得、等待者、idle 与关闭收尾。
- [`AudioClipLibrary.cs`](../../../Runtime/Systems/Audio/Config/AudioClipLibrary.cs) / [`AudioGroupPlayer.cs`](../../../Runtime/Systems/Audio/Components/AudioGroupPlayer.cs)：素材定义、一次抽选与组件 owner。
- [`Resource/Integration/Audio/`](../../../Runtime/Systems/Resource/Integration/Audio/)：主包内建 Resource/Audio 桥接，不是额外安装包或第二缓存。
