# 音频播放（AudioKit）

<a id="audiokit"></a>

> [文档首页](../README.md) · [架构总览](../Architecture.md) · [Resource](Resource.md) · [Action](Action.md) · [Timing](Timing.md)

## 本页导航

- [最短路径](#最短路径) · [默认值与配置](#默认值与配置)
- [命名加载与资源责任](#命名加载与资源责任) · [素材组与组件](#素材组与组件)
- [播放与句柄](#播放与句柄) · [命名控制通道](#命名控制通道)
- [单请求淡入淡出](#单请求淡入淡出) · [音乐 Crossfade](#音乐-crossfade) · [自动首尾淡变](#自动首尾淡变)
- [随机与空间播放](#随机与空间播放) · [设置与去重](#设置与去重) · [Sound 并发与优先级](#sound-并发与优先级)
- [可选 Mixer](#可选-mixer) · [组件与 Action](#组件与-action) · [宿主、池与诊断](#宿主池与诊断)
- [目录与依赖](#目录与依赖) · [测试与帮助](#测试与帮助) · [历史提示](#历史提示)

AudioKit 的直接 `AudioClip` 入口借用外部资源；命名入口通过显式 Provider 取得租约。正式资源后端为 `ResourceAudioClipProvider`，不提供旧资源后端兼容入口。播放、控制、Provider 回调与租约归还均在 Unity 主线程执行。

下文未给出完整类定义的代码均为调用片段：`using` 放在文件顶部，其余语句放入业务方法或标注的协程。Clip、Transform、Slider、Mixer 和 Action 宿主等由调用方提供并按消费期保活。

## 最短路径

准备外部 `AudioClip`，场景由相机或玩家提供一个有效、启用的 `AudioListener`。无需调用 `Initialize`，也无需 Mixer、配置资产、预热或修改并发设置。

### 代码播放

```csharp
using YLFramework.Audio;

AudioKit.PlayMusic(musicClip); // 默认循环
AudioKit.PlayVoice(voiceClip); // 默认单次，替换旧 Voice
AudioKit.PlaySound(soundClip); // 默认单次，允许重叠
```

### 背景音乐组件

在场景对象上添加 `AudioMusicPlayer` 并绑定 Clip；默认 Loop 与 `playOnAwake` 开启，在首次 `Start` 自动播放，不是每次 `OnEnable`。需要手动控制时关闭自动播放，由 UnityEvent 调用无参 `Play/Stop`；组件停用只停止自己持有的音乐。

### 按钮点击音

添加 `AudioEmitter`，在 Clip 列表放入点击音，保持 2D、Loop 关闭。在 `Button.onClick` 中拖入该组件并选择 `AudioEmitter.Play`。普通 2D one-shot 不因按钮面板停用被截断；组件新建/Reset 默认继承全局去重策略，需要同帧重叠时显式选择 EveryOne。

### 音量滑块与 Listener

```csharp
musicSlider.SetValueWithoutNotify(AudioKit.Settings.MusicVolume.Value);
musicSlider.onValueChanged.AddListener(value => AudioKit.Settings.MusicVolume.Value = value);
```

常规 Slider 使用 `[0,1]`；分类音量本身允许有限的非负倍率，包括大于 1。需要同步其他入口的修改时，使用 `MusicVolume.RegisterWithInitValue(value => musicSlider.SetValueWithoutNotify(value))`，并由 UI 所有者注销订阅和事件监听。已有合法 PlayerPrefs 优先于缺省值 1；音量为 0 是静音而非停止。Audio 不创建第二个 Listener；没有有效 Listener 时不能听见声音，Source 状态检查不能替代试听。

| 无需配置 | 仅使用对应高级功能时配置 |
|---|---|
| 初始化、Mixer、配置资产、资源 Loader、并发上限、预热 | Mixer 路由/暴露参数/Snapshot；命名加载时显式指定 Audio Provider |
| 普通 2D 播放的距离、跟随目标、淡变、Priority | At/Following 空间参数；自动淡变/Crossfade；去重、并发与缓存预算 |

## 默认值与配置

### 统一初始默认值

下表是无业务覆盖、无合法存档覆盖时的初始值。组件新建值指编辑器添加/Reset 后的预设，不用于覆盖旧场景或 Prefab。

| 配置 | 初始默认值 | 选择理由 |
|---|---|---|
| 播放类别与 Loop | Sound/Voice 单次，Music 循环 | 点击音/人声自然结束，背景音乐持续播放 |
| 请求 Volume / Pitch / Priority | 1 / 1 / 0 | 不改变素材音量倍率或音调，不预设抢占优势 |
| Settings 分类开关 / 音量 / 临时 Mute | true / 1 / false | 新项目直接可听，已有合法存档仍优先 |
| 位置与空间衰减 | 默认 2D；显式 At/Following 使用距离 1/50、Linear、doppler=0 | 普通 UI 无距离配置；空间效果仅显式启用，避免默认多普勒变调 |
| 路由 | Inherit，分类 Group=null 时 Direct | Mixer 可选，不要求配置资产 |
| 去重模式 / Options.PlaySoundMode | 全局 EveryOne / null 继承 | 默认允许重叠，由全局统一策略或单次覆盖控制 |
| 全局 / 每 Clip 去重窗口 | 5 / 5 帧 | 仅在窗口去重模式下抑制短时连发；按帧计数，不是固定毫秒数 |
| Sound 并发 | Limit=0、DefaultClipLimit=0，不限；RejectNew | 开箱即用不拒播；显式设置上限后不默认抢停旧声音 |
| 闲置 Source 缓存 / 预热 | 10 / 不自动预热 | 复用小规模常规音效，避免无请求时分配音源 |
| 闲置 Clip 缓存 | Capacity=0、IdleSeconds=0 | 默认不缓存资产，按需显式启用；与 Source 池预算完全分离 |
| AutoFade | 首尾时长均 0 | 保留素材原有起止，淡变由业务显式启用 |
| AutoPauseWhenTimeScaleZero | false | 只对显式开启的请求响应零 timeScale |
| CrossFadeMusic API | curve=EqualPower；ignoreTimeScale=true；duration 必填 | 不相关 BGM 交接减少中点功率下陷，暂停游戏时间时仍可过渡 |
| MusicPlayer Crossfade | 关闭；duration=1 秒；unscaled=true；编辑器新建/Reset curve=EqualPower | 常规音乐直接播放，启用过渡时有可用的初始预设 |
| 编辑器新建/Reset MixerController | applyOnAwake=false；transitionDuration=0.5 秒 | 全局路由显式开启，参数/Snapshot 用途不意外清路由；显式 Snapshot 过渡避免默认硬切 |

EveryOne 才表示关闭去重；显式将窗口设为 0 可在 GlobalFrames 或 SoundFrames 下只做同帧去重。默认 5 帧的边界包含最后一帧，约为 60 FPS 下 83 ms、120 FPS 下 42 ms，不是固定秒数。EqualPower 不保证等响度或不叠加峰值，显式 Linear 仍适用于需要线性交接的素材。

| 组件 | 编辑器新建/Reset | 旧资源及未触发 Reset 的字段初始化 |
|---|---|---|
| AudioEmitter | useGlobalPlaySoundMode=true，继承全局 | false，保留原 playSoundMode=EveryOne |
| AudioMusicPlayer | crossFadeCurve=EqualPower，Crossfade 仍关闭 | crossFadeCurve=Linear，保留旧缺字段资源；已有序列化选择不自动改写 |
| AudioMixerController | applyOnAwake=false，transitionDuration=0.5 | 字段初始化 true/0，保留旧资产自动路由与即时 Snapshot 过渡行为 |

运行时 AddComponent 不承诺触发 Reset；需要上述新建预设时应显式配置。新建 MixerController 仅绑定 Config 不自动应用或清空全局路由，必须勾选 Inspector 的 `Apply Routing On Awake`（applyOnAwake）并提供有效 Config，或显式调用 Apply。旧资源已有配置仍按其序列化值工作，不因新预设被重置。

<a id="options-使用"></a>

### Options 配置

`AudioSoundOptions` 是 `[Serializable] sealed class`。构造函数提供有效默认值，可以只修改需要的字段：

```csharp
var soundOptions = new AudioSoundOptions { Volume = 0.5f };
AudioKit.PlaySound(soundClip, soundOptions);

var musicOptions = AudioSoundOptions.MusicDefaults;
musicOptions.Pitch = 1.1f;
AudioKit.PlayMusicWithOptions(musicClip, musicOptions);
// 只调整音乐音量时无需 Options：
AudioKit.PlayMusic(musicClip, volume: 0.5f);
```

| 配置/输入 | 行为 |
|---|---|
| `new AudioSoundOptions()` / `Defaults` | 有效非循环默认值：Volume=1、Pitch=1、2D；Defaults 每次返回新实例 |
| `MusicDefaults` | 每次返回独立实例，Loop=true，其余与 Defaults 一致 |
| 音乐 Options 省略/`null` | 使用与 MusicDefaults 一致的内部值默认，不创建配置类；WithOptions 的位置参数仍必填，但允许显式 null |
| Sound/Voice Options 省略/`null` | 使用与 Defaults 一致的内部值默认，不创建配置类；`default(AudioSoundOptions)` 是 null |
| 音乐显式传入 `Defaults` / `new AudioSoundOptions()` | Loop=false；不按类别把显式 false 改成 true |
| `Volume=0` / `Pitch=0` | 前者合法静音；后者非法抛错，不作为缺省回退 |

播放入口在提交时捕获内部值快照。之后修改原 Options 不影响已提交请求，句柄修改也不反写配置或其他请求。`PlaySoundAction.AllocateWithOptions` 则在 Allocate 时捕获并校验模板，Start 再提交；普通 `var copy = options` 只是类引用赋值，不是快照。嵌套配置按值复制，`OutputRoute.Group` 等 Unity 资源引用仍为借用，不克隆资源。

`new AudioSoundOptions()` 和每次读取 `Defaults/MusicDefaults` 都分配独立可变实例，不是共享静态对象。高频调用可复用业务持有的配置，每次提交仍独立捕获；组件从序列化字段直接构造内部值配置。此设计不承诺整个 Audio 零 GC，初始化、Source 创建、集合增长和 Action 回调闭包等仍可能分配。性能应区分冷路径、预热后稳态和目标 Player 实测，历史测量不构成当前设备保证。

### JSON 与源码迁移

Unity 2022.3 的 `JsonUtility.FromJson<AudioSoundOptions>` 对缺字段使用 `default(T)`，不执行无参构造或字段初始化器；部分 JSON 缺 Pitch 会得到非法 0，不会自动获得类默认值。部分 JSON 推荐覆盖一个已创建的默认实例：

```csharp
var options = AudioSoundOptions.Defaults; // 音乐使用 MusicDefaults
UnityEngine.JsonUtility.FromJsonOverwrite("{\"Volume\":0.5}", options);
AudioKit.PlaySound(soundClip, options);
```

完整旧 JSON 保留原字段格式。显式 Pitch=0 仍非法，Volume=0 仍合法，不按零值猜缺字段。从早期 struct API 迁移时，移除 `Nullable<AudioSoundOptions>`，将值类型可选参数改为 `AudioSoundOptions options=null`；`PlaySoundMode` 字段仍是可空枚举。依赖值复制、值相等、反射签名或生成绑定的使用方需更新，外部预编译程序集必须重新构建；不承诺二进制 ABI 稳定。

`AudioSoundOptions` 及其嵌套 `AudioAutoFade/AudioOutputRoute` 的字段同时构成这里的 `JsonUtility` 合同，因此保留 PascalCase 原名，作为命名规范的协议例外。`FormerlySerializedAs` 只迁移 Unity 资产，不能保护这些 JSON 键；不通过字段改名或自定义回退悄悄改变该合同。

## 命名加载与资源责任

命名入口不重载旧 Clip API，使用 `PlayMusicNamed`、`PlayVoiceNamed`、`PlaySoundNamed`、`CrossFadeMusicNamed`；对应异步入口为 `PlayMusicAsync`、`PlayVoiceAsync`、`PlaySoundAsync`、`CrossFadeMusicAsync`。命名通道使用 `channel.PlayNamed` / `channel.PlayAsync`，仍复用原 Exclusive/Overlap 拓扑与 `AudioHandle`。异步接口返回可观察、可逻辑取消的 `AudioPlayRequest`，不是通用 Task/协程调度框架。

以下为业务调用片段；`resources` 是应用已经显式启动并持有的 `ResourceSystem`。完整启动/关闭流程见 [Resource](Resource.md#初始化与持有)，播放不隐式初始化资源。

```csharp
using YLFramework.Audio;
using YLFramework.Resource;

var audio = resources.GetAudioClipProvider("game-audio");
var click = new AudioClipAddress(audio, "ui/click");
AudioKit.PlaySoundNamed(click);

var music = new AudioClipAddress(audio, "music/theme");
var request = AudioKit.PlayMusicAsync(music,
    onStarted: (clip, originalHandle) => { /* 已提交原 Source.Play，可控制原句柄 */ });
// 在所有者退出或需要撤销本次请求时：
request.Cancel(); // Dispose 等价；不会物理取消共享加载或停止另一等待者
```

Provider 配置和引用身份属于资源身份的一部分。只有同一个 Provider 实例、相同完整 key 的在途加载/租约可以合并；不同 Bundle/Project/后端或不同 key 不按 `clip.name`、Clip 对象相等合并所有权。应复用已配置的 Provider 实例，而不是每次 Play 创建新后端。接口 `IAudioClipProvider` 只负责同步取得和主线程异步完成投递，成功必须返回独立 `AudioClipLease`；Audio 不查 Module 或建立全局资源注册表。Provider 的调用、进度、完成与租约归还均须在 Unity 主线程。

| 阶段/证据 | 语义 |
|---|---|
| `Loading` | 等待后端取得，Handle 无效；不创建播放 Source，也不占 Sound 播放预算 |
| `Ready` / `IsAssetReady` | 资产已取得并保活；异步仍可能等待 `LoadAudioData`，`Progress=1` 只表示资产取得 |
| `IsDataReady` | 音频数据曾达到 `Loaded`；这是属性而非独立 State，Streaming 不承诺全文件驻留内存 |
| `Accepted` | 原 Voice 已绑定责任与句柄；Music/Voice off、通道 Pause 等可能尚未首次启播 |
| `Started` / `HasStarted` | 已提交原 `Source.Play`，不是首采样、DSP 或实际可听见保证 |
| `Ended` / `IsDone` | 本请求恰一次终结；`EndReason`、原 Handle、已开始证据和 `Error` 保留，不复活为新请求 |

`AudioPlayRequest` 不是可直接 yield 的 `CustomYieldInstruction`；协程按需要轮询 `HasStarted` 或 `IsDone`，不要把等待终结误作等待启播。循环请求不会自然完成，退出所有者时必须取消。

[YL0010](Attributes.md#yl0010) 对非泛型 `System.Collections.IEnumerator` 协程直接 `yield return` 静态类型为 AudioPlayRequest 的表达式给出 Warning；普通泛型数据枚举器不报，object 擦除不追踪。自定义调度器若确有其他协议，可在对应方法附近局部抑制，不把标准 Unity 等待语义当成所有调度器的事实。配置入口为 `YLFramework/Core/分析器规则`。

```csharp
IEnumerator WaitForStart(AudioPlayRequest request)
{
    // 失败也可能直接进入 IsDone，不能只等 HasStarted。
    yield return new WaitUntil(() => request.HasStarted || request.IsDone);
}

IEnumerator WaitForEnd(AudioPlayRequest request)
{
    yield return new WaitUntil(() => request.IsDone);
    // 再区分 EndReason.Completed 与失败、取消等终态。
}
```

没有 `AudioPlayRequest.Wait()` API；循环播放的 WaitForEnd 可能一直等待。分析器不自动选择“等开始”“等结束”或“只延迟一帧”，也不负责请求所有权和退出清理。

`Cancel` 可在加载、数据等待、提交重入或已接纳播放期间使用，只取消/停止自己的原请求。`EndReason.Completed` 仅表示自然完成，其他终态区分 `Canceled`、`Stopped`、`Replaced`、`Unavailable`、`Failed`、`Shutdown`、`Rejected`。异步取得/开始异常保存在 `Error`；同步取得或提交异常仍抛给调用方。`onFinished` 只在自然完成调用，不当作全终态通知。自然回调进入前命名请求已终结为 Completed，原 Handle 可仍有效；Clip 保活直到回调返回，不提前归还资源。

Exclusive 的最新有效命名请求撤销同拓扑旧 pending；旧曲在新资源/数据可提交前继续播放，加载失败不先截断旧曲。普通直接 Clip 播放、Stop、通道 Dispose、Sound off 和 Shutdown 同样约束 pending。Overlap 的等待者独立取消，共享 producer 继续。旧会话迟到结果只能归还原资源责任，不能进入新宿主、创建 Source 或投递新会话的开始/失败通知。后端没有物理取消能力时，Audio 不声称底层已经停止；永久不完成的 producer 可结束所有逻辑等待者，但只能由真实后端完成后归还生产责任。

同步命名入口只承诺资产取得同步，不将后台解码/Streaming 伪装为数据同步就绪。同一 Audio 缓存条目尚在异步取得时，同步取得明确抛错，不阻塞主线程或抢占在途 producer；使用异步等待者即可共享结果。跨 Audio 缓存的 Resource 在途同步取得同样明确返回 `InProgress`。

### 租约、预加载与缓存

- 直接 `AudioClip` 入口继续借用，调用方负责保活；Audio 不自行 `Destroy` 或 `Resources.UnloadAsset`。
- `ResourceAudioClipProvider` 捕获所属 ResourceSystem 与 ProjectId，每次成功取得独立 `AssetLease<AudioClip>`，包装为原 `AudioClipLease`。后端元数据按会话/项目复用，不另建资产缓存或 Bundle 引用表，也不隐式初始化 Resource。
- Audio 缓存合并同 Provider/key 的使用；播放、显式预加载或 idle 条目保有一个真实后端租约，最后释放交还 Resource。当前 Resource 不额外缓存零租约 Clip，因此不存在双重 idle 永久持有。Resources 是共享 borrowed 来源，归零只清本系统记录，不调用 `UnloadAudioData/UnloadAsset/Destroy`；AB 最后一个资产/依赖 owner 归还才实际 `Unload(true)`。
- 等待者、数据等待、循环/暂停、Music/Voice off 后待重播、Crossfade 尾轨和未被业务取消的 one-shot 都保留自己的有效责任；Source 闲置池不持有 Clip 资源。
- `AudioKit.AcquireClip(address)` 提供同步预加载租约，必须由调用方 `Dispose`。显式租约可跨 Audio Shutdown 继续由调用方保活；关闭的缓存不再接纳它进入闲置缓存，最终 Dispose 才归还其 owner。
- `ClipCacheCapacity` 限制闲置条目数，`ClipCacheIdleSeconds` 使用 unscaled 时钟；两个值都大于 0 才缓存。`ClearClipCache()` 只释放当下闲置条目。缩容、到期与清缓存不卸载活跃责任，淘汰释放重入后会重新检查条目是否已被取得。Shutdown 释放闲置缓存。
- 应用关闭先结束自己的 Audio 播放/缓存并归还显式 Clip 租约，再关闭所属 ResourceStartup，或由资源 owner 调用 `resources.ShutdownAsync()`；尚有播放、显式租约或 idle 缓存时 Resource 返回可恢复的 Busy，不强制销毁 Clip。Audio Shutdown 后的迟到结果由原 Resource 请求交付给已关闭缓存并立即归还，不会创建新 Audio 宿主。Resource 会话退出则失效请求并继续 native 收尾；旧 Provider 不重新绑定新会话。

```csharp
AudioKit.ClipCacheCapacity = 4;
AudioKit.ClipCacheIdleSeconds = 15;
using (var preload = AudioKit.AcquireClip(music))
{
    // 原租约保活，命名播放可共享同一完整资源身份。
    AudioKit.PlayMusicNamed(music);
}
AudioKit.ClearClipCache(); // 活跃 Music 不受影响
```

## 素材组与组件

`AudioClipLibrary` 是可共享的选曲资产，不是控制通道或第二个播放器。通过 `Assets/Create/YLFramework/Audio/Clip Library` 新建，`groups` 内填写区分大小写的组名 `name` 及 `entries`；一个组可以混用直接 Clip 和命名定位。`Uniform` 在有效条目中均匀选取，忽略存储的 `weight`；`Weighted` 只选正权重条目，`weight=0` 明确排除。每次提交只抽选一次并保存定位快照，不在异步完成后重新随机。作者字段的旧 PascalCase 键由 `FormerlySerializedAs` 读取，类型、引用及枚举数值不变。

| Entry Source | 必要配置与责任 |
|---|---|
| Clip | 绑定外部 AudioClip；继续借用，外部 owner 保活 |
| Resource | ProjectId 如 `game-audio`，Key 为 Catalog 逻辑地址如 `ui/click`；Resources/AB 由正式目录决定，Library 只读取已显式初始化的 ResourceSystem |
| Resources / AssetBundle（旧值） | 仅保留旧序列化值供一次性显式迁移，不参与运行时选择；不把 BundleName 解释为 ProjectId，不回退旧 ResLoader |
| ExternalProvider | Key 由显式后端定义；代码向 GroupPlayer.SetProvider 或 Library.TrySelect 传入同一个 provider 实例，不隐式回退/注册后端 |

空 Clip/空 key、不可用定位不参与抽选；缺组、空/全坏组、重名组、非法选择/来源枚举或 Weighted 的负数/NaN/Infinity 权重返回失败，不停止旧播放。命名加载失败/迟到/取消继续使用 pending、资源租约与最新独占提交契约；异步错误查看 `LastRequest.Error/EndReason`。播放配置不合法仍按正式 API 抛错，不静默修复字段。

旧配置通过 Library Inspector 的“迁移旧资源定位到新配置副本”入口逐条填写经过核对的 ProjectId 与逻辑地址。工具要求每个旧条目都有明确映射，只向既有 Assets 目录创建新 GUID 的 `.asset`，拒绝覆盖或遗留 `.meta`；原配置、直接 Clip、外部 Provider、组名、权重及选择规则不变。`BundleName` 原字段和旧枚举数字保留以读取既有配置，但不提供永久运行时转发。当前 Audio Provider 的 string key 表达主资产逻辑地址；转换遇到 SubAsset 映射会明确拒绝，不会静默丢弃身份。

### Inspector 最短配置

1. 新建 Library，添加 `ui-clicks` 组，放入两个 Clip，选择 Uniform；或选择 Weighted 并填写相对权重。
2. 添加 `YLFramework/Audio/Group Player`，绑定 Library，Group Name 填 `ui-clicks`，Channel Name 填该 owner 的唯一稳定名称，如 `menu/clicks`。组名只选素材；通道名只控制播放。相同登记名冲突明确拒绝，不能按对象名/哈希隐式共享。
3. 点击音选择 Sound + Overlap、Options.Loop=false；独立环境循环选择 Music + Exclusive、显式 Options.Loop=true。新组件即使选 Music 也不自动改写 Loop，自定义 Music 不占缺省双声部。
4. Auto Play 默认 Manual；UnityEvent 绑定无参 `Play/Stop`，或字符串入口 `PlayGroup`。FirstStart 仅首次 Start 播放；EveryEnable 首次也从 Start 播放，之后每次启用重播，不修改现有 MusicPlayer/Emitter 的首次 Start 语义。
5. 命名资源默认 Load Async=true；Persistence ID 默认空，不读写通道键。Options.AutoPauseWhenTimeScaleZero 默认 false，输出默认 Inherit；Inspector 的 Sound 继承全局去重，因为 Unity 不序列化 `Nullable<PlaySoundModes>`。

GroupPlayer 停用取消自己的 pending 并停止自己的播放，销毁撤销原通道登记；不会 StopMusic/StopAllSound 或影响其他 owner。Started UnityEvent 在原句柄/通道归属绑定后触发，事件内 Stop、失活、销毁或异常仍收尾原身份。`Channel` 是已登记身份（读取不创建），`LastHandle` 是最近启播身份，`LastRequest` 仅记录最近异步命名提交，不是第二张请求管理表。登记后的 category/mode/name/persistence 身份保持不变，运行期修改 Inspector 身份不重登，重新创建 owner 才应用新身份。

素材组选曲也可单独用于代码：

```csharp
using YLFramework.Audio;

// library 为绑定的素材库，channel 为 owner 已显式登记的控制通道。
if (library.TrySelect("ambience", out var selected))
{
    var options = AudioSoundOptions.MusicDefaults;
    if (selected.IsNamed) channel.PlayAsync(selected.Address, options);
    else channel.Play(selected.Clip, options);
}
// owner 退出时 Dispose 自己的通道，不关闭全局 Audio 或他人的资源。
```

### 素材组借源

可选 Borrowed Source 接入调用方的 AudioSource，保留其空间曲线/滤镜，只有 Exclusive 支持。必须在调用前明确 Stop 并让渡控制；已播放、已借用（含暂停）或框架自有 Source 拒绝，不自动抢占旧租约。普通组播放默认 2D；借源时沿用调用方空间配置，不重挂/移动/停用/销毁对象。

代码命名借源入口为 `channel.PlayBorrowedNamed(source, address, options)` / `channel.PlayBorrowedAsync(source, address, options)`。加载期间不预留或修改 Source，提交时重新校验；在途 Source 失活/销毁走 Unavailable，占用冲突走异步 Failed/Error（同步抛错）。加载或不可逆替换前校验失败保留旧播放；替换释放旧资源后发生的重入冲突不回滚已结束旧请求，新请求不得覆盖嵌套取得 Source 的 owner，这与原 PlayBorrowed 契约一致。资源在原 Voice 全终态归还，不靠公开 onFinished；释放仍按最后写入字段比较与归属恢复协议，不恢复此前播放历史。同一借源不支持原生 PlayOneShot 重叠；重叠使用池化独立 Source。

迁移旧工程时使用正式的 `YLFramework.Audio.AudioEmitter/AudioMusicPlayer`。旧 `YLFramework.Services.Audio` 的 using、完整类型名及持久 UnityEvent 类型字符串须同步核对；仅有脚本 GUID 不变不足以证明事件接线兼容。不提供旧 Core 音频组件或旧 Loader 的运行时转发层。

## 模块定位

| 能力 | 边界 |
|---|---|
| 缺省 Music / Voice / Sound | Music 最多两轨 Crossfade；Voice 一个独占槽；Sound 使用独立 Source，支持随机、空间、去重与并发控制 |
| 命名控制通道 | 独立 Exclusive/Overlap 拓扑，可独占借入调用方 Source；不是素材组选曲或第二个播放器 |
| 音量与过渡 | 句柄包络、音乐 Crossfade 与可选 Mixer 各自控制，不互相替代 |
| 资源 | 直接 Clip 借用；命名加载与 Clip 缓存只管理后端租约，物理卸载由资源 owner 决定 |
| 持久化 | 六个分类开关/音量键；显式 persistenceId 的局部 Volume/Mute。临时静音、并发、缓存、统计、淡变和路由不持久化 |

namespace 为 `YLFramework.Audio`，与 Action 共用 `YLFramework.Runtime`，音效动作桥接内建。没有裸播放器、Fluent 构建器或 Audio 专属 Binding 包装；不提供逻辑分组预算、无限豁免、距离/可听度抢占、音源虚拟化、自动 Ducking、Mixer 参数淡变调度、DSP 调度、防重复随机、HRTF 或遮挡。

## 目录与依赖

路径相对 `Assets/YLFramework/`：

| 位置 | 职责 |
|---|---|
| [`Runtime/Systems/Audio/AudioKit.cs`](../../Runtime/Systems/Audio/AudioKit.cs) | 播放、设置、槽位控制、去重/Clip/缓存预算与统计查询 |
| [`AudioHandle.cs`](../../Runtime/Systems/Audio/AudioHandle.cs) | 播放代次与所属宿主生命周期校验、控制与只读进度 |
| [`AudioChannel.cs`](../../Runtime/Systems/Audio/AudioChannel.cs) | 会话内命名控制登记、局部设置、独占/重叠拓扑与受控借源入口 |
| [`Config/`](../../Runtime/Systems/Audio/Config/) | 集中存放设置、请求参数及可序列化配置类型；命名空间与 Runtime 程序集不变 |
| [`Config/AudioSoundOptions.cs`](../../Runtime/Systems/Audio/Config/AudioSoundOptions.cs) | 音效、空间语音及 Music/Voice WithOptions 的公开可变配置类、独立 Defaults 实例与校验入口 |
| [`Config/AudioSoundConcurrency.cs`](../../Runtime/Systems/Audio/Config/AudioSoundConcurrency.cs) | Sound 总量/默认 Clip 上限及 RejectNew/StopOldest/StopLowestPriority 策略 |
| [`AudioSoundStats.cs`](../../Runtime/Systems/Audio/AudioSoundStats.cs) | 不分配集合的 Sound 会话统计值快照与资源拥有数 |
| [`Config/AudioAutoFade.cs`](../../Runtime/Systems/Audio/Config/AudioAutoFade.cs) | 可序列化首尾片段秒配置，零值关闭、短片段归一化 |
| [`Config/AudioCrossfadeCurve.cs`](../../Runtime/Systems/Audio/Config/AudioCrossfadeCurve.cs) | Music Crossfade 的 Linear/EqualPower 曲线选择 |
| [`Config/AudioWeightedClip.cs`](../../Runtime/Systems/Audio/Config/AudioWeightedClip.cs) | 可序列化 Clip/Weight 配对项，供纯加权选择及播放使用 |
| [`Config/AudioClipLibrary.cs`](../../Runtime/Systems/Audio/Config/AudioClipLibrary.cs) | 具名选曲组、直接 Clip/显式后端定位及一次抽选快照 |
| [`Config/AudioOutputRoute.cs`](../../Runtime/Systems/Audio/Config/AudioOutputRoute.cs) | 单次播放的继承、指定 MixerGroup、直接输出三态路由 |
| [`Config/AudioKitSettings.cs`](../../Runtime/Systems/Audio/Config/AudioKitSettings.cs) | 基于 YL Binding 的六个持久化属性与 Audio 音量校验、临时静音及默认输出组 |
| [`Config/AudioMixerConfig.cs`](../../Runtime/Systems/Audio/Config/AudioMixerConfig.cs) | 可选的分类 Mixer 路由配置资产 |
| [`Mixer/`](../../Runtime/Systems/Audio/Mixer/) | 独立静态 AudioMixerControl 操作逻辑 |
| [`Internal/`](../../Runtime/Systems/Audio/Internal/) | AudioManager 宿主、AudioVoice 实例、SoundDeduplicator |
| [`Internal/AudioPlaybackOptions.cs`](../../Runtime/Systems/Audio/Internal/AudioPlaybackOptions.cs) | 内部播放值快照、集中有效默认值、公开配置捕获与校验；不新增公开配置类型 |
| [`Components/`](../../Runtime/Systems/Audio/Components/) | AudioEmitter / AudioMusicPlayer、AudioGroupPlayer 组 owner 与 AudioMixerController 薄配置组件 |
| [`Actions/`](../../Runtime/Systems/Audio/Actions/) | PlaySoundAction / PlaySoundExtension |
| [`Resource/Integration/Audio/`](../../Runtime/Systems/Resource/Integration/Audio/) | 内建 Resource/Audio 桥接：`ResourceSystem.Audio.cs` 属于 `YLFramework.Resource`，`ResourceAudioClipProvider.cs` 属于 `YLFramework.Audio`；仍在同一 Runtime |
| [`Diagnostics/`](../../Runtime/Systems/Audio/Diagnostics/) | 被动诊断；运行 GUI 仅 Editor/Development Build |
| [`Editor/Systems/Audio/`](../../Editor/Systems/Audio/) | 分层 Inspector、Debug 编辑器窗口与 Help 提供方 |

核心链复用 `YLFramework.Pooling`、`YLFramework.Binding`、`YLFramework.Events` 和 `YLFramework.Singletons.MonoSingletonHost<AudioManager>`。Resource 桥接在同一 Runtime 内，不是另一套资源服务；同 System/Project 复用 Provider 引用，会话关闭后失效。共享 Runtime 的依赖见 [架构总览](../Architecture.md)，不表示零依赖安装包。

## 播放与句柄

```csharp
using YLFramework.Audio;

// Music 默认循环，Voice 默认不循环；同 Clip 的显式新请求也替换旧请求。
AudioHandle music = AudioKit.PlayMusic(musicClip, loop: true, volume: 0.6f);
AudioHandle voice = AudioKit.PlayVoice(voiceClip,
    onFinished: (clip, handle) => UnityEngine.Debug.Log($"Voice finished: {clip.name}, valid: {handle.IsValid}"));

var options = AudioSoundOptions.Defaults;
options.Volume = 0.8f;
options.Pitch = 1.1f;
options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
AudioHandle sound = AudioKit.PlaySound(soundClip, options,
    (clip, handle) => UnityEngine.Debug.Log($"Sound finished: {clip.name}, time: {handle.Time}"));

float seconds = sound.Time;
float duration = sound.Duration;
float progress = sound.Progress;

sound.Pause();
sound.SetVolumeScale(0.5f);
sound.SetPitch(1.2f);
sound.Resume();
sound.Stop();
```

| 入口 | 返回/行为 |
|---|---|
| `PlayMusic(clip, loop=true, volume=1, onStarted=null, onFinished=null, outputRoute=default)` | 硬替换所有旧音乐并取消 Crossfade，返回句柄 |
| `PlayVoice(clip, loop=false, volume=1, onStarted=null, onFinished=null, outputRoute=default)` | 独立的人声槽位，返回句柄 |
| `PlayMusicWithOptions(clip, options, onStarted=null, onFinished=null)` | 2D 音乐，options 位置必填；null 循环，显式 Defaults.Loop=false |
| `CrossFadeMusic(clip, duration, options=null, onStarted=null, onFinished=null, ignoreTimeScale=true, curve=EqualPower)` | 最新目标与旧曲最多双轨过渡；curve 可显式 Linear，options 省略时 Loop=true，0 时长立即替换 |
| `PlayVoiceWithOptions(clip, options, onStarted=null, onFinished=null)` | 2D 人声，options 位置必填，null 使用 Defaults；支持 Pitch、Volume、OutputRoute 和 AutoFade |
| `PlayVoiceAt(clip, position, options=null, onStarted=null, onFinished=null)` | 固定世界位置的 3D 人声，替换当前 Voice 请求 |
| `PlayVoiceFollowing(clip, target, options=null, onStarted=null, onFinished=null)` | 跟随激活 Transform 的 3D 人声，无效目标不替换当前请求 |
| `PlaySound(clip, options=null, onFinished=null, onStarted=null)` | 2D 音效；省略 options 使用 Defaults |
| `PlayRandomSound(clips, options=null, onFinished=null, onStarted=null)` | 有效 Clip 中均匀选择，空/全无效返回无效句柄 |
| `PlayWeightedRandomSound(clips, options=null, onFinished=null, onStarted=null)` | 有效且正权重 Clip 中加权选择一次，沿用 Sound 去重、并发与回调 |
| `TrySelectWeightedRandomClip(clips, out clip)` | 纯选择，返回 bool；不播放、不初始化 Settings 或宿主，可接 3D/Voice |
| `PlaySoundAt(clip, position, options=null, onFinished=null, onStarted=null)` | 固定世界位置的 3D 音效 |
| `PlaySoundFollowing(clip, target, options=null, onFinished=null, onStarted=null)` | 跟随有效、激活的 Transform，无效目标拒播 |
| `Music` / `Voice` | 最新目标 Music / 当前 Voice 句柄；读取空状态不创建宿主 |
| `SoundCount` | 有效音效请求数，包含暂停、静音和循环请求，不等于正在发声数 |
| `SoundConcurrency` | 整体读写运行时并发配置，Limit/DefaultClipLimit 默认 0 表示不限，默认 RejectNew |
| `SetClipSoundLimit(clip, limit)` / `RemoveClipSoundLimit(clip)` / `ClearClipSoundLimits()` | 按 Clip 实例覆盖/恢复默认/清覆盖；Remove 返回 bool，规则变严即时裁剪 |
| `GetSoundCount(clip)` / `GetEffectiveClipSoundLimit(clip)` | 被动查询实际 Clip 请求数/有效上限，无效 Clip 返回 0 |
| `SoundCacheCapacity` / `PrewarmSounds(cachedCount)` | 独立闲置缓存预算默认 10；显式补足目标闲置数，返回新增缓存数 |
| `SoundStats` / `ResetSoundStats()` | 被动 readonly AudioSoundStats 快照/显式清累计，不创建 Settings 或宿主 |
| `GetPlayingSounds()` | `AudioHandle[]` 快照，仅显式查询时分配 |
| `CopyPlayingSounds(List<AudioHandle>)` | 清空并填充调用方列表，可复用容量 |

- `IsValid` 表示请求有效；`IsPlaying` 表示 Source 正在播放，静音时也可为 true，不代表可听见；`IsPaused` 表示手动、命名通道或 opt-in 自动暂停中的任一种。
- default、拒播、已结束、旧租用或旧会话句柄的 `Pause/Resume/Stop/SetVolumeScale/SetPitch/FadeIn/FadeOut` 安全无效，不验证参数、不初始化宿主。内部 AudioVoice 和 AudioSource 不公开给业务控制。
- `SetVolumeScale(scale)` 只改变当前请求倍率，不改变分类音量或存档；Source 音量为 `Clamp01(当前分类音量 * 局部通道音量 * scale) * gain * transitionGain`。缺省请求的局部通道音量为 1；请求包络 gain 默认 1，未参与音乐过渡时 transitionGain 也为 1，旧行为不变。倍率可大于 1，保留原倍率而不把截断后的结果当倍率，0 仅静音、不停止播放。
- `SetPitch(pitch)` 立即更新当前请求音调。两个修改方法适用于 Sound、Music、Voice，不重启播放、不改变手动暂停或请求代次；Music/Voice 关闭期间也可修改，重新开启使用最新参数。池复用不会继承旧请求的修改。
- 有效句柄的倍率必须非负且有限，Pitch 必须有限且在 `(0,3]` 内，非法参数抛 `ArgumentOutOfRangeException`，不改变当前参数。校验与初始播放参数一致。
- `onStarted/onFinished` 均为 `Action<AudioClip, AudioHandle>`，回调参数顺序为 `(clip, handle)`。Sound 入口的顺序为 `onFinished` 再 `onStarted`；Music/Voice 为 `onStarted` 再 `onFinished`。迁移旧无参回调时更新签名；不提供兼容包装层。
- `onStarted` 在真正启播时同步执行，可能在播放方法返回前触发；应使用回调传入的 handle，而非尚未完成赋值的外部变量。Music/Voice 等待开关开启时延迟启播，重新开启不重复已触发的开始回调。
- 进入自然完成 `onFinished` 时原 handle 有效且 `IsPlaying` 为 false，返回后清理原代次；回调内主动 Stop、替换或 Shutdown 可立即使它失效。不保证完成回调中 `Progress` 为 1，也不保留完成历史。
- 开始/完成回调各每个请求最多一次；循环、Pause/Resume 和开关重播不重复开始通知，Mute 不取消回调。显式 Stop、替换、抢占、裁剪、拒播、关闭、目标丢失及 Shutdown 不触发自然完成；循环请求必须明确停止。回调重入创建的新请求不会被旧代次清理误停。
- `StopMusic/PauseMusic/ResumeMusic` 控制目标及旧尾轨和过渡，Voice 对应方法及 `StopAllSound` 控制各自共享状态，空状态不创建宿主。普通组件停止自己持有的句柄，不能随意停全局音乐。
- 调用方必须保证 Clip 在播放、暂停或等待重播期间存活。停止、替换、自然结束及 Shutdown 后 Audio 断开引用，但不转移资源所有权。

`Time/Duration/Progress` 都是只读 `float`，适用于 Music、Voice、Sound，包括空间播放：

| 属性 | 契约 |
|---|---|
| `Time` | 当前 Clip 内位置（秒），已启播时读取 `AudioSource.time` 并限制在 `[0, Duration]` |
| `Duration` | 本次有效请求的 `clip.length`（秒），不是受 Pitch 影响后的实际播放耗时 |
| `Progress` | `Duration > 0` 时为 `Clamp01(Time / Duration)`，否则为 0 |

default、拒播、过期代次、Stop/替换/完成/Shutdown 后，以及所需 Source 或 Clip 缺失/已销毁时三项均为 0；读取不初始化 Settings 或宿主。Music/Voice 在关闭通道下等待启播或重播时，`Time/Progress` 为 0，`Duration` 仍为有效 Clip 长度。手动 Pause 冻结位置，Resume 续播；Mute 不冻结、不回退位置。循环只展示当前轮，可能从接近 1 回到 0；不支持 seek 或历史查询。

Unity 的 `AudioSource.time` 对压缩音频可能只有分包级精度，不承诺采样级或节拍级同步，也不能用自然完成时读到 1 作为通知条件。Audio 不用 Timer 或累加 deltaTime 模拟播放进度。

回调迁移与同步开始示例：

```csharp
// 旧：AudioKit.PlaySound(soundClip, onFinished: () => UnityEngine.Debug.Log("finished"));
AudioKit.PlaySound(soundClip,
    onFinished: (clip, handle) => UnityEngine.Debug.Log($"Finished: {clip.name}, valid: {handle.IsValid}"),
    onStarted: (clip, handle) => handle.SetVolumeScale(0.8f));

// 不捕获方法返回后才赋值的 music 变量；直接控制本次回调中的 handle。
AudioHandle music = AudioKit.PlayMusic(musicClip,
    onStarted: (clip, handle) => handle.Pause());
```

### Sound 开始提交与签名迁移

Sound 开始回调在 `Source.Play` 之后、方法返回前同步触发。此时实际 Clip、Options、Pitch、Mixer 路由及自动初始增益已配置，原 lease 有效，`SoundCount` 已包含自身，去重记录已经提交。因此回调内再次播放同 Clip 仍受到去重限制；无效 Clip/目标、开关关闭、去重或容量拒绝均不调用开始/自然完成回调。

回调可停止自身、StopAllSound、改变并发、递归播放、停用组件、Reset/Deinit Action 或 Shutdown。外层返回的是本次原 lease，可能已失效，不能用复用同一 Voice 的新请求替换它。开始回调抛异常时原样抛出，仅按本次 Voice+Generation 释放仍归自己的请求，并回滚仍归本次 lease 的去重记录；不清整个窗口，不删除嵌套请求记录，也不误停复用 Voice 的新声音。回调正常返回但主动停止自身仍算成功开始，保留成功播放的去重记录。原生自然完成仍按旧语义等待 Update 检测，不为释放容量提前通知。

普通位置调用 `PlaySound(clip, options, onFinished)` 的第三参数仍是完成回调。迁移旧签名的固定 `GetMethod(..., parameterTypes)`、委托、生成桥接及预编译程序集时，须核对末尾 `Action<AudioClip, AudioHandle>` 并重新构建；Action/sequence 见下文。可选参数不等于二进制兼容。

## 命名控制通道

命名通道是同一 AudioManager 中的请求归属，不是第二个播放器、素材选曲组或 MVC Controller。可通过代码显式登记，也由 AudioGroupPlayer 登记自己的通道。

```csharp
var wind = AudioKit.RegisterChannel("environment/wind");
var rain = AudioKit.RegisterChannel("environment/rain",
    persistenceId: "audio/environment/rain");
var windHandle = wind.Play(windClip, AudioSoundOptions.MusicDefaults);
rain.Play(rainClip, AudioSoundOptions.MusicDefaults);
AudioKit.PlayMusic(bgmClip); // 缺省 Music 的双声部预算仍独立

wind.Volume = 0.4f;
wind.Muted = true;
wind.Pause();
wind.Resume();
wind.Stop(); // 只停止调用进入时本通道的租约
wind.Dispose(); // owner 结束时撤销登记；不会删除持久化值
rain.Dispose();
```

- `RegisterChannel(name, category=Music, mode=Exclusive, persistenceId=null)` 区分大小写。名称/存储身份只能含 ASCII 字母、数字、`.`、`_`、`-`、`/`，不得为空或带首尾空白；名字和非 null 存储身份的同时登记冲突均抛异常，不隐式共用 owner。
- `AudioCategory` 只选择既有 Music/Voice/Sound 分类设置；`AudioChannelMode` 单独选择 Exclusive/Overlap。通道 `Play` 省略 options 使用非循环 Defaults，需要循环必须显式传 `Loop=true` 或 MusicDefaults。null Clip、非法配置、借源占用在替换前发现；容量拒绝遵循原 Sound 策略。
- Exclusive 替换本通道原请求；Overlap 每次使用独立 Source/Handle，不使用不可逐次控制的原生 PlayOneShot。命名 Music/Voice 不占缺省槽；命名 Sound（包括借入 Source）仍计入全局/Clip 并发、Priority、帧去重、SoundCount/统计及 StopAllSound。
- 局部 `Volume` 为 `[0,1]`，初值 1，与分类和请求倍率相乘；`Muted` 初值 false，与分类 Mute 取 OR。它们不会反向修改缺省设置。分类 On/Off 保持旧语义：Music/Voice off 保留请求、on 从头重播；Sound off 终结请求并拒播。输出使用既有 Options.OutputRoute，Inherit 跟随分类路由，ToMixer/Direct 保持独立。
- `Pause` 保持通道暂停，也应用于后续请求；`Resume` 只解除通道原因，不解除逐请求手动暂停或自动暂停。开始回调抛错只终结失败的原租约；批量 Resume/分类开启继续处理其他原请求后汇总报告异常。
- 省略 `persistenceId` 完全不读写通道键，不根据 Source 名字、对象哈希或实例 ID 落盘。显式稳定身份使用 `YLFramework.Audio.Channel:{id}:Volume` / `:Mute`，不改六个分类键、不写旧宿主或 Core 的历史键；登记只读取，坏音量内存回退 1、不重写。Pause/拓扑/路由不持久化，不自动迁移旧键或每次调用 PlayerPrefs.Save。
- `VolumeChanged` / `MutedChanged` 仅观察局部实际变化，先更新原请求再通知，同值写入不通知；不自动投递初始值或分类设置变化。所有者负责退订，Dispose/Shutdown 也撤销原订阅。通知中重入同属性或 Dispose 撤销余下的旧值投递；单个监听抛错后继续仍属原代次的监听并聚合抛出，不回滚已应用值。
- Dispose、Shutdown 或原会话宿主失效后 `IsValid=false`。旧身份的 Stop/Pause/Resume/Dispose 安全 no-op，Play/设置写入明确抛错；不能控制同名新登记或已复用的请求。RequestCount 包含暂停和等待分类启用的请求。

### 受控 Source 借用

```csharp
callerSource.Stop(); // 显式结束 caller 自己的播放/暂停历史并让渡控制
var channel = AudioKit.RegisterChannel("machine", AudioCategory.Sound);
var lease = channel.PlayBorrowed(callerSource, machineClip,
    new AudioSoundOptions { Loop = true, OutputRoute = AudioOutputRoute.Direct });
lease.Pause();
lease.Resume();
lease.Stop(); // 释放后 caller 仍拥有 Source 和 GameObject
channel.Dispose();
```

- 仅 Exclusive 支持；Source 必须有效、active/enabled、已停止且由调用方明确让渡播放控制。框架自有 Source、正在播放或已借用的 Source 均拒绝，暂停中的已有借用也不能重复借用。再次使用同一借源必须先结束其旧 Handle，不做隐式抢占。
- 只配置本次 `clip/loop/pitch/volume/mute/outputAudioMixerGroup`；不改父级、Transform、active/enabled、playOnAwake、空间参数、曲线或滤镜。不把借源放入自有 Source 闲置缓存，也不销毁、停用或卸载调用方对象/Clip。
- Stop/替换/自然结束/失活/Source 销毁/Shutdown 均进入原 Voice 全终态。释放先撤销原请求与 Source 占用，再允许资源清理回调重入；仅在 Source 仍持本次 Clip 时停止，并逐字段比较最后写入值，未改变的字段才恢复原值。调用方已经改动的字段保留；外部换 Clip 则完全放弃停止/恢复，避免覆盖他人的播放。
- 不恢复旧播放位置、暂停或播放历史。调用方须在租约期间通过 Handle/通道控制播放，不得直接对同 Source 调 Play/Pause/Stop/PlayOneShot；Unity 无法可靠识别同 Clip 外部重播或写回相同值，此类冲突不能由字段比较证明归属。同 Source 原生 PlayOneShot 重叠不提供逐次请求控制，需要独立控制时使用普通 Overlap/PlaySound。

### 局部自动暂停

```csharp
var options = new AudioSoundOptions { Loop = true, AutoPauseWhenTimeScaleZero = true };
var handle = AudioKit.PlaySound(loopClip, options);
```

只在捕获了 `AutoPauseWhenTimeScaleZero=true` 的请求上响应 `Time.timeScale == 0f`，由现有 Audio Tick 驱动；进入零 timeScale 时提交的请求会等恢复后首次启播。条件解除仅清自动原因，仍有 Handle.Pause 或通道 Pause 时不恢复。默认 false，原 BGM、点击音和旧组件配置保持原契约；不操作 AudioListener.pause 或新建自动暂停驱动。有效请求的 IsPaused 包含任一暂停原因，进度、fade 和参与的缺省 Music crossfade 都冻结；Music 过渡只继承手动暂停，不把自动原因变成永久手动暂停。

## 单请求淡入淡出

`AudioHandle` 的手动淡变不改分类设置或 Mixer 路由，签名如下：

```csharp
public void FadeIn(float duration, bool ignoreTimeScale = true);
public void FadeOut(float duration, bool stopWhenDone = true, bool ignoreTimeScale = true);
```

```csharp
// Play 返回后同帧设置；不需要 Tween 或另一个调度宿主。
var sound = AudioKit.PlaySound(soundClip);
sound.FadeIn(0.25f);

// 稍后从当前包络淡出，完成时停止，不通知自然结束。
sound.FadeOut(0.5f, stopWhenDone: true);

// 音乐也可使用同步开始回调中的本次 handle。
AudioKit.PlayMusic(musicClip,
    onStarted: (clip, handle) => handle.FadeIn(0.5f));
```

- 包络 gain 独立于分类音量、`Options.Volume` 与 `SetVolumeScale`；无音乐过渡时 Source 为 `Clamp01(category * requestScale) * gain`，Crossfade 再叠乘独立 transitionGain，不把 gain 写回倍率、Settings 或存档。
- 尚未经历任何淡变的新请求，首次调用正时长 `FadeIn` 时立即设 gain 为 0，从 0 向 1；经历过任何淡变后（包含零时长操作），`FadeIn` 从当前包络向 1，不在重复调用时再次跳零。`FadeOut` 从当前包络向 0；新淡变替换旧淡变，不叠加调度。
- 有效句柄的 duration 必须有限且 >=0，非法值抛 `ArgumentOutOfRangeException` 且不改现有状态。0 立即到目标；`FadeOut(0, stopWhenDone: true)` 立即 Stop。`stopWhenDone: false` 到 0 后保留有效请求，可再次淡入。
- default/过期句柄 no-op，连非法 duration 也不验证，与 `SetVolumeScale` 一致，不初始化 Settings 或宿主。
- 默认 `ignoreTimeScale=true` 使用 unscaled 时间；显式 false 使用 scaled 时间。手动 Pause、Music/Voice 通道 off 或未真正启动冻结包络和剩余淡变；Mute 仍推进。
- 音乐/语音关开保留当前包络和剩余淡变，Clip 仍按旧语义从头重播；手动 Pause 不被开启恢复。Sound off 直接 Stop，取消淡变且不复活。
- Stop、自然结束、替换、target 丢失、Shutdown 与池回收均清淡变。淡出 Stop 不调用自然结束 callback；短 Clip 在淡变完成前自然结束时，自然结束优先，正常调用 callback。

淡变基于现有 `AudioManager.Update`，不是 DSP 调度或采样级自动化。同帧调用/同步开始回调不保证首采样已静音、采样级精度或无缝 Crossfade；不增加 Tween 依赖、新宿主或持久化项。

## 音乐 Crossfade

```csharp
public static AudioHandle CrossFadeMusic(AudioClip clip, float duration,
    AudioSoundOptions options = null,
    Action<AudioClip, AudioHandle> onStarted = null,
    Action<AudioClip, AudioHandle> onFinished = null,
    bool ignoreTimeScale = true, AudioCrossfadeCurve curve = AudioCrossfadeCurve.EqualPower);
```

方法内调用：

```csharp
AudioKit.PlayMusic(menuMusic);
AudioHandle target = AudioKit.CrossFadeMusic(battleMusic, 1.5f);
AudioKit.PauseMusic(); // 暂停两轨及共同过渡时钟。
AudioKit.ResumeMusic();
AudioKit.StopMusic(); // 同时清理尾轨。
```

- `options=null` 使用与 MusicDefaults 一致的内部值默认（Loop=true）；显式传入 Defaults 则 Loop=false，尊重传入 Loop。提交捕获值快照，不修改或复制原配置类对象。支持 Volume/Pitch/OutputRoute/AutoFade，忽略空间字段、Sound 去重策略和 Priority，不参与 Sound 预算或抢占。
- duration 须有限且 >=0，0 是立即替换。无效/已销毁 Clip 返回 default，不改变现有音乐；合法 Clip 的配置、路由与自动包络元数据在停止任何旧曲或分配 Source 前校验。
- `AudioKit.Music` 立即代表最新目标，不代表响度最大的曲目；旧 outgoing lease 在实际退场前仍有效，不会重新提升为主曲。最多两条 Music Source，第二条首次需要时才创建，退场后可闲置复用；不使用 Sound 池或并发额度，Shutdown 释放。
- 默认 unscaled 时间，`ignoreTimeScale=false` 随 scaled 时间。`u=Clamp01(elapsed/duration)`；Linear 下 outgoing 过渡增益为 `capturedOutgoingGain*(1-u)`、incoming 为 `u`，EqualPower 下 outgoing 为 `capturedOutgoingGain*cos(u*pi/2)`、incoming 为 `sin(u*pi/2)`。正时长 incoming 在 Source.Play/onStarted 前 transitionGain=0，无旧曲也从零淡入；结束时目标增益为 1，停止旧曲且不通知其自然完成。
- `Source.volume = Clamp01(category * requestScale) * requestEnvelopeGain * transitionGain`。每个请求的手动/自动包络继续独立工作并与过渡叠乘；手动 FadeIn/FadeOut 接管自动包络时不取消 Crossfade。未参与 Crossfade 的请求 transitionGain=1。等功率曲线不保证等响度，也不提供 DSP 或节拍同步保证。
- 最新请求优先、不排队。A->B->C 时先停止 A，B 从当前 transitionGain 退到 0，C 从 0 到 1，最多保留两条；快速切歌可能截断最早尾轨及降低响度，不承诺三曲无损过渡。相同 Clip 也是重开的新请求，不复用播放位置。
- 旧 `PlayMusic/PlayMusicWithOptions` 始终硬替换全部旧音乐并取消过渡。`StopMusic` 清两条；`PauseMusic/ResumeMusic` 暂停/恢复两条及剩余计时，不重启。直接 lease.Pause/Resume/Stop 仅控制自身音频，但任一有效参与请求暂停会冻结共同过渡时钟，另一条音频不因此自动暂停。
- 暂停中请求 Crossfade，新目标继承暂停等待状态，ResumeMusic 才首次启播和通知。旧 PlayMusic* 替换仍不继承暂停。Music off 取消过渡、停止旧尾轨，仅保留目标并恢复 transitionGain=1；on 按旧规则从头重播，已发 onStarted 不重复。off 期间 Crossfade 只替换待播放目标，不保留旧轨或启动过渡。Mute 不冻结时钟。
- Music 分类音量、静音及默认 Mixer 路由实时作用于两条；它们各自的显式覆盖/Direct 路由仍独立。尾轨仍属于 Music，不使用 Voice 设置。
- outgoing 已逻辑替换，取消其自然完成通知，即使原生先结束也不通知。新目标若非循环并在过渡结束前自然完成，正常通知自身一次并清理；旧尾轨继续剩余退场，不复活旧曲。新目标主动 Stop/手动淡出停止/资源丢失也不恢复旧曲；一侧失效不再冻结另一侧，两侧失效清过渡。
- 新目标状态、两条 lease 和过渡 epoch 在开始回调前提交。回调异常原样抛出，只清仍归本次操作拥有的请求及过渡，不恢复被替换音乐；重入的新 Crossfade/普通音乐不受旧返回路径、异常清理或 Update 快照影响。

### 过渡曲线

`CrossFadeMusic` 末尾可选参数 `curve` 选择过渡增益映射，API 默认 `AudioCrossfadeCurve.EqualPower`；显式 `Linear` 仍可用，枚举零值仍为 Linear，不改已有序列化枚举数值。未知枚举在 duration 校验之后、任何副作用之前抛 `ArgumentOutOfRangeException`。可选默认值由 C# 调用方编译时捕获，使用新缺省曲线的调用方需要重新编译；已有显式 Linear 调用不改。固定签名反射、委托绑定、生成绑定与预编译使用方仍需核对真实签名。

| 曲线 | incoming | outgoing | 满增益中点 |
|---|---|---|---|
| `Linear` | `u` | `capturedOutgoingGain*(1-u)` | 各 0.5，平方和≈0.5 |
| `EqualPower` | `sin(u*pi/2)` | `capturedOutgoingGain*cos(u*pi/2)` | 各≈0.7071，平方和≈1 |

- EqualPower 更接近“强度接近且不相关”两首素材的恒定功率；不等于等响度，相关/同相素材仍可能叠加峰值，素材响度匹配与试听仍由业务负责。
- outgoing 始终缩放 `capturedOutgoingGain` 而非重排曲线，连续切歌时从当前过渡增益连续退场，不跳变。
- `duration=0` 立即替换，曲线不参与；`AudioMusicPlayer` 的 crossFadeCurve 字段初始化保留 Linear 以兼容旧缺字段资源，编辑器新建/Reset 设置 EqualPower，仅 Crossfade 开启时生效；Reset 不开启 Crossfade。

## 自动首尾淡变

`AudioAutoFade` 是 `[Serializable]` struct，只有可写公开字段 `float FadeInSeconds`、`float FadeOutSeconds`。构造 `AudioAutoFade(float fadeInSeconds, float fadeOutSeconds)` 仅赋值，不校验；播放入口统一校验。`default`/两项为 0 表示关闭，`AudioSoundOptions.Defaults.AutoFade` 也是全零，不改变旧播放行为，不增加 Enabled、timeScale 或自动 Stop 开关。

```csharp
var options = AudioSoundOptions.Defaults;
options.AutoFade = new AudioAutoFade(2f, 3f);
AudioKit.PlaySound(soundClip, options,
    onFinished: (clip, handle) => UnityEngine.Debug.Log(clip.name));

// options 位置必填，允许 null；显式 Defaults.Loop=false，PlayMusic 默认 true。
AudioKit.PlayMusicWithOptions(musicClip, options);
AudioKit.PlayVoiceWithOptions(voiceClip, options);

// 循环只在首次启动淡入；FadeOutSeconds 必须为 0。
options.Loop = true;
options.AutoFade = new AudioAutoFade(1f, 0f);
var music = AudioKit.PlayMusicWithOptions(musicClip, options);
// 后续由当前 gain 接管；手动 duration 是原有的 unscaled/scaled 秒。
music.FadeOut(0.5f);
```

```csharp
public static AudioHandle PlayMusicWithOptions(AudioClip clip, AudioSoundOptions options,
    Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null);
public static AudioHandle PlayVoiceWithOptions(AudioClip clip, AudioSoundOptions options,
    Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null);
```

两个 WithOptions 入口只提供 2D Music/Voice，仍硬替换当前分类请求，Music 同时取消旧 Crossfade；支持 Loop、Volume、Pitch、OutputRoute、AutoFade，空间字段不生效，Sound 去重策略和 Priority 不应用于 Music/Voice。Sound、At/Following Sound 与 Voice、等权/加权随机均在提交时捕获 options 的内部值快照，其中 Priority 仅作用于 Sound；随机仅抽选一次，按实际选中 Clip 长度归一化，不按列表首项推测，不因拒播重抽。

### 片段时钟与归一化

首尾时长为片段时间而非现实等待秒数。自动包络读取 `AudioSource.timeSamples / (double)clip.frequency`，长度 L 为 `clip.samples / (double)clip.frequency`，位置 t 限制到 `[0,L]`。Pitch=2 时实际淡变耗时减半，运行中改 Pitch 只改变位置推进速度，不重新计时；`Time.timeScale` 不参与，没有 ignoreTimeScale 参数。此内部时钟不改变既有 `AudioHandle.Time/Duration/Progress` 的语义。

非循环配置 x/y 在 x+y>L 时使用 double 加法与同比缩短；两侧不重叠，理论交接点 gain=1，原来为零的一侧仍为零。有限正值不以任意阈值当成关闭：

```text
scale = x+y>L ? L/(x+y) : 1
a = x*scale; b = y*scale
gainIn  = a>0 ? Clamp01(t/a) : 1
gainOut = b>0 ? Clamp01((L-t)/b) : 1
gain = Min(gainIn, gainOut)
Source.volume = Clamp01(category * requestScale) * gain * transitionGain
```

两个字段须有限且非负；`Loop && FadeOutSeconds>0` 明确拒绝，不静默忽略。非零配置还要求有效 samples/frequency、有限且 >0 的 Clip 长度，在分配宿主、提交去重或替换槽位之前校验。纯淡出初始 gain=1；有淡入时在 Source.Play 与 onStarted 前设 gain=0。未启播请求保持初始增益，不消耗包络。

循环只执行首次启动淡入，时长 `min(FadeInSeconds,L)`；到达该时长或检测首轮 PCM 回绕后锁存 gain=1，后续循环不重新淡入。Update 间隔超过整轮时，单靠位置不能可靠识别漏过的循环；短循环与低帧率存在此限制，不引入 DSP 循环调度。更新是帧级，不保证首采样、最后一个采样精确为零或过短包络可被完整观察；压缩/Streaming 音频及目标平台 timeSamples 行为与最终听感需实测。

### 接管与生命周期

- 自动首尾包络只改同一个 gain，不调用 Stop、不改 loop、不提前结束 Clip；自然完成仍按原 Clip/原 lease 最多通知一次。自然完成检测先写 t=L 的最终包络，尾淡出>0 时 gain=0，再进入原有效句柄回调；不能把 Source 已复位的位置 0 当作新淡入。回调重入仍受代次保护，不清理新请求。
- 合法的手动 FadeIn/FadeOut 取消自动控制，从当前 gain 平滑接管，不叠乘第二个请求包络；Crossfade 的 transitionGain 仍独立叠乘。正在播放先采样当前位置，暂停/通道关闭保留最后 gain。非法 duration 不取消自动模式；零时长保持原即时语义。取消后即使 Music/Voice 关开也不复活自动配置，Options 中的配置值不代表运行中仍受自动控制。
- Music/Voice 关开按旧语义从头重播 Clip；仅自动模式仍 active 时重置包络和首轮 loop latch。手动模式保留当前包络及剩余手动淡变。Pause/Resume 不重置，Mute 不冻结，SetVolumeScale、分类音量、Pitch 和路由修改不更换包络控制者。
- Stop、自然结束、替换、跟随 target 丢失、Shutdown 与池回收清除自动状态；旧 lease 控制仍为安全 no-op。手动 FadeOut 的 stopWhenDone=true 主动 Stop 不通知自然完成，false 到零后继续静音，自动尾部不会再次接管。

自动淡变默认关闭，不增加宿主、Timer、Mixer 参数、PlayerPrefs 或曲线资产，也不提供运行中设置/恢复自动包络的句柄方法。Options 的源码迁移与 JSON 缺字段限制见 [JSON 与源码迁移](#json-与源码迁移)。

## 随机与空间播放

```csharp
var options = AudioSoundOptions.Defaults;
options.MinDistance = 2f;
options.MaxDistance = 30f;
options.RolloffMode = UnityEngine.AudioRolloffMode.Logarithmic;

AudioKit.PlayRandomSound(clips, options,
    onFinished: (selectedClip, handle) => UnityEngine.Debug.Log($"Selected clip finished: {selectedClip.name}"));
var fixedSound = AudioKit.PlaySoundAt(clip, worldPosition, options);
var following = AudioKit.PlaySoundFollowing(clip, target, options);

// Voice 仍为独占槽位，两次调用中后一请求替换前一请求。
var fixedVoice = AudioKit.PlayVoiceAt(voiceClip, worldPosition, options);
var characterVoice = AudioKit.PlayVoiceFollowing(voiceClip, character, options);
```

随机选择忽略 null 和已销毁 Clip，选中后按该 Clip 去重，不为整组共用冷却。随机回调的 clip 是实际选中并播放的对象，handle 对应该次原播放代次，不是列表首项或按名字查找的 Clip。`PlaySound/PlayVoice` 默认 2D，Music 仍仅公开 2D 入口。Sound/Voice 的 At/Following 在启播前设置世界位置、`spatialBlend=1`、距离与衰减；默认 `MinDistance=1`、`MaxDistance=50`、`Linear`、`dopplerLevel=0`。

### 轻量加权选择

`AudioWeightedClip` 是 `[Serializable]` struct，只有公开字段 `AudioClip clip`、`float weight` 和构造 `AudioWeightedClip(AudioClip clip, float weight=1f)`。使用配对项而不是两份平行列表；旧 Unity 资产的 `Clip/Weight` 通过 `FormerlySerializedAs` 保留：

```csharp
var weighted = new[]
{
    new AudioWeightedClip(commonClip, 3f),
    new AudioWeightedClip(rareClip, 1f)
};
AudioKit.PlayWeightedRandomSound(weighted, options,
    onFinished: (clip, handle) => UnityEngine.Debug.Log(clip.name));

// 纯选择可接任意既有播放入口，不新增加权 3D/Voice 重载。
if (AudioKit.TrySelectWeightedRandomClip(weighted, out var selected))
    AudioKit.PlayVoiceFollowing(selected, character, options);
```

公开签名为 `AudioHandle PlayWeightedRandomSound(IReadOnlyList<AudioWeightedClip> clips, AudioSoundOptions options=null, Action<AudioClip, AudioHandle> onFinished=null, Action<AudioClip, AudioHandle> onStarted=null)` 和 `bool TrySelectWeightedRandomClip(IReadOnlyList<AudioWeightedClip> clips, out AudioClip clip)`。

所有项 Weight 都必须 finite 且 >=0，包括 null/已销毁 Clip 项；任何非法权重抛 `ArgumentOutOfRangeException`。只有存活 Clip 且 Weight>0 参与，使用 double 累计并只抽选一次。null/空/all0 或没有有效候选时无请求，纯选择返回 false、out clip=null；纯选择不初始化 Settings 或宿主。播放在选中之后沿用 Sound 的参数、开关、去重、并发及回调契约，去重或容量拒播不重抽；连续选中同一 Clip 合法，不提供防重复。旧 `PlayRandomSound` 与旧 clips 均匀选择行为保持，开始参数只在原末尾追加。

空间语音复用 `AudioSoundOptions`，默认不循环，独立受 `IsVoiceOn/VoiceVolume/IsVoiceMuted` 控制；`PlaySoundMode/Priority` 对 Voice 忽略，不受音效去重、Sound 总量/Clip 限额、`IsSoundOn/IsSoundMuted`、`StopAllSound` 影响，也不进入 SoundCount/音效快照/统计。仍只有一个 Voice 槽位，不新增多 NPC 并发语音。无效 Clip/目标或非法参数不会替换当前请求。

Voice 关闭时保留完整空间请求。跟随目标在关闭或暂停期间移动仍会更新位置，开启重播前再次校验目标并使用最新位置；目标失效停止并清空请求，不触发自然完成，不因开关开启而复活。固定位置不绑定调用者生命周期；替换为 2D Voice 时重置所有空间参数和位置。开始/自然结束回调及运行中倍率/Pitch 控制沿用 Voice 契约。

参数可从 `new AudioSoundOptions()` 或 `Defaults` 开始，循环音乐从 `MusicDefaults` 开始；`default(AudioSoundOptions)` 为 null，交由入口按分类解析。公开字段为 `Loop/Volume/Pitch/Priority/PlaySoundMode/MinDistance/MaxDistance/RolloffMode/OutputRoute/AutoFade/AutoPauseWhenTimeScaleZero`。Sound 的 Priority 是非负 int；Volume 非负且有限，Pitch 为 `(0,3]`；空间距离有限且满足 `0<=MinDistance<=MaxDistance`、`MaxDistance>0`，衰减仅支持 Linear/Logarithmic。非法数值在分配 Source 和去重登记之前抛 `ArgumentOutOfRangeException`；自动淡变与路由校验见对应章节。

跟随 Source 不重新父级到业务 Transform，集中更新其世界位置；目标销毁、停用或场景卸载时停止且不触发自然完成。固定位置 one-shot 可以在发起对象销毁后播完，循环由句柄或组件明确停止。池归还与再次分配完整重置空间、位置、音调、倍率、loop、Clip、暂停与回调，防止 3D 状态泄漏到 2D。

Audio 不创建、销毁场景 AudioListener。相机或玩家提供 Listener，缺失时开发环境给出诊断。3D 声音的自动化 Source 参数检查不能替代最终方位、距离和音量听感验证。

## 设置与去重

```csharp
AudioKit.Settings.IsOn = true; // bool：直接读写三个分类开关
AudioKit.Settings.IsMusicOn.Value = true;
AudioKit.Settings.SoundVolume.Value = 0.8f;
AudioKit.Settings.IsMusicMuted.Value = true; // 临时静音，位置继续推进
AudioKit.Settings.IsVoiceMuted.Value = false;
AudioKit.Settings.IsSoundMuted.Value = true;
AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
// 显式业务冷却示例，不是窗口的初始默认值。
AudioKit.GlobalFrameCountForIgnoreSameSound = 10;
AudioKit.SoundFrameCountForIgnoreSameSound = 10;
```

六个 PlayerPrefs 键、默认开关 true 与默认音量 1 保持不变：

| 分类 | 开关键 | 音量键 |
|---|---|---|
| Sound | `KEY_AUDIO_MANAGER_SOUND_ON` | `KEY_AUDIO_MANAGER_SOUND_VOLUME` |
| Music | `KEY_AUDIO_MANAGER_MUSIC_ON` | `KEY_AUDIO_MANAGER_MUSIC_VOLUME` |
| Voice | `KEY_AUDIO_MANAGER_VOICE_ON` | `KEY_AUDIO_MANAGER_VOICE_VOLUME` |

上述六个持久化属性沿用 `YLFramework.Binding.PlayerPrefsBoolProperty/PlayerPrefsFloatProperty` 公开类型，分类音量在 Audio 局部校验。Load/Reload 遇到负数、NaN 或 Infinity 在内存回退 1，不自动重写坏存档；合法 0 和大于 1 的倍率原样保留，Delete 恢复缺省且不写回。通过 `.Value`、`TrySetValue` 或 `SetValueWithoutCallback` 赋非法音量均抛 `ArgumentOutOfRangeException`，不改变内存、存档、通知或播放状态。合法 `.Value` 变化先持久化再通知；`SetValueWithoutCallback` 只改内存，不通知或持久化。UI 使用 `Register/RegisterWithInitValue` 返回的 YL Events 令牌管理订阅；Shutdown 对旧 Settings 执行 `UnregisterAll`。没有旧 Settings 的 Init/Deinit/Initialized，也不修改 Binding 全局规则或 comparer。

`IsMusicMuted/IsVoiceMuted/IsSoundMuted` 是 `BindableProperty<bool>`，通过 `.Value` 修改并可沿用同一订阅模式，默认均为 false，仅保留在当前运行时 Settings，不增加 PlayerPrefs 键或主动 Save。Shutdown 清理订阅；重建 Settings 后恢复 false，旧 Settings 的修改不影响新宿主。

- Mute 只控制对应分类的 Source 静音，与 `IsOn`、分类开关及 Volume 独立，不把分类音量或请求倍率改为 0，也不提供全局 Mute 或单句柄 Mute。
- 静音期间仍接受符合开关/去重/并发规则的播放，位置与跟随正常更新，自然结束及回调照常发生；解除静音从当前位置继续，不重播。暂停时修改 Mute 不改变暂停状态或播放代次。
- 静音期间修改分类音量或请求倍率后，解除静音使用最新的 `Clamp01(分类音量 * 请求倍率) * gain * transitionGain`（无过渡时为 1）。Mute 不冻结请求包络或音乐过渡；新请求与通道重新开启在启播前应用当前分类 Mute，既有关闭/拒播/重播行为不变。
- Music/Voice 关闭停止 Source 但保留完整、仍可 Stop 的请求；重新开启从头重播原 Clip、loop、最新倍率/Pitch 与未触发的回调，开始回调不重复。手动模式保留当前包络/剩余淡变，仍 active 的自动模式重置初始包络与首轮 latch；已被手动取消的自动模式不恢复。
- 关闭期间新请求替换待播放项，即使 Clip 相同也生成新代次。显式 Stop、自然结束和替换清空旧请求，开关不能复活；手动 Pause 不被开启强制恢复。
- Sound 关闭停止全部音效，关闭期间新请求拒绝，不自动恢复。
- Music 有 Crossfade 时，关闭先取消过渡、停止旧尾轨，仅保留最新目标并设 transitionGain=1；其它目标重播/自动与手动包络规则不变。
- 全局模式默认 EveryOne，不去重，但不能绕过并发上限；GlobalFrames 使用所有声音共享窗口起点，SoundFrames 按 Clip 实例身份独立记录。两个运行时窗口默认 5 帧，仅在对应去重模式下生效；显式设置 0 时仅同帧去重，EveryOne 才关闭去重。窗口边界包含最后一帧，不是秒数，显式 10 帧等业务覆盖仍有效。
- 仅接纳成功后提交记录，且在开始回调前完成，失败请求不消耗窗口。独立记录自然结束按句柄代次移除，较早播放结束不删除较晚记录；Stop 不额外改变窗口，过期记录定期淘汰。开始回调异常仅回滚仍归本次 lease 的记录，Global 共享窗口同样受归属保护。

## Sound 并发与优先级

```csharp
// AudioSoundConcurrency 是具有公开可写字段的 [Serializable] struct。
AudioKit.SoundConcurrency = new AudioSoundConcurrency
{
    Limit = 24,
    DefaultClipLimit = 3,
    OverflowPolicy = SoundOverflowPolicy.StopLowestPriority
};

AudioKit.SetClipSoundLimit(explosionClip, 2);
AudioKit.SetClipSoundLimit(uiClip, 0); // 覆盖默认 Clip 上限，不能绕过全局上限。
var options = AudioSoundOptions.Defaults;
options.Priority = 10;
AudioKit.PlaySound(importantClip, options);
```

公开字段为 `int Limit`、`int DefaultClipLimit` 与 `SoundOverflowPolicy OverflowPolicy`，枚举为 `RejectNew=0`、`StopOldest=1`、`StopLowestPriority=2`，旧数值和语义不变。零值为两项限额 0（unlimited）和 RejectNew。`AudioKit.SoundConcurrency` 是运行时整体读写属性，读取配置副本，不是 BindableProperty；先完整校验再初始化/应用，负 Limit/DefaultClipLimit 或未知 enum 抛参数异常，配置和现有请求都不改变。配置可初始化原 Settings/去重器，但不创建 AudioManager、Source 或 Listener；无新 Settings、PlayerPrefs、配置资产或 Resources。Shutdown/新会话恢复零值，旧宿主不影响新会话。

只约束有效 Sound 请求，包含暂停、Mute、Loop；不限制 Music/Voice，不是 Unity 真实/虚拟 voice 数或所有场景原生 AudioSource 的全局上限。所有 Sound 入口、Emitter 和 Action 共用接纳闸门：选择一次实际 Clip -> 输入/目标/路由/包络/Priority 校验 -> 开关 -> 原去重检查（不提交）-> 总量/Clip 联合容量判定 -> 必要抢占唯一受害者 -> 配置并登记有效请求/Clip 计数/Priority/接纳序号 -> 提交去重与统计 -> Source.Play/onStarted。

- RejectNew 满额返回无效句柄，不分配 Source、不提交去重、不触发回调、不停止旧请求。
- StopOldest 满额停止候选范围内最早接纳的有效 Sound，再接纳新请求；同帧按单调接纳序号排序，不按进度或 HashSet 遍历。暂停、静音和循环也可被停止，旧句柄失效，不触发自然完成。RejectNew/StopOldest 不因 Priority 增加保护。
- StopLowestPriority 在候选范围选最低 Priority，同级选最旧；incoming.Priority 低于候选时拒播，同级或更高允许替换。Priority 为非负 int，0 普通、数值越大越重要，支持 int.MaxValue；接纳时捕获，不随音量/Pitch/暂停/Mute 改变。这不是 Unity `AudioSource.priority`，不映射其不同方向的原生可听度优先级。Music/Voice/Crossfade 忽略 Sound Priority。
- 仅全局满额时，候选范围为全部有效 Sound；仅 Clip 满或两项同时满时，只能选同一 Clip，一次接纳最多停止一条。不能先停其它 Clip 再为当前 Clip 额外停一条。
- 降低任一有效限额立即裁剪，先各 Clip 再全局以避免多停。RejectNew 保留较早请求、停止最新；StopOldest 保留较新请求、停止最旧；StopLowestPriority 保留高优先级、同级保较新，裁剪不需要 incoming 比较。0 或提高有效上限不停止请求，切换策略但已符合额度时不裁剪。
- 原生已结束但尚待 Update 清理的有效请求仍占额度；不为了腾容量提前 CheckFinished 或触发用户回调。自然结束、Stop、Sound off、目标失效、回收与 Shutdown 都释放额度，不依赖自然结束回调计数。
- EveryOne 只绕过去重，仍受两级容量；任何 Priority 都不能绕过帧窗口，去重拒播不抢占。等权/加权先抽一次，容量拒绝不重抽、不提交或延长窗口；随后释放容量可在同帧重试，仍受已有成功记录约束。抢占/裁剪使等待中的 Action 按原契约在下一次检查完成，不改它的自然结束或取消语义。主动 Stop/抢占/裁剪不清成功播放的冷却，自然完成保留原 SoundFrames/GlobalFrames 区别。

### 每 Clip 覆盖

```csharp
public static void SetClipSoundLimit(AudioClip clip, int limit);
public static bool RemoveClipSoundLimit(AudioClip clip);
public static void ClearClipSoundLimits();
public static int GetEffectiveClipSoundLimit(AudioClip clip);
public static int GetSoundCount(AudioClip clip);
```

覆盖按 AudioClip 实例身份而非名称、路径或候选列表索引登记；同名不同实例独立，同一 Clip 的所有调用者、2D/3D、Mixer 路由和 PlaySoundMode 共享计数。重复权重候选指向同一 Clip 时也共享预算，候选权重仍独立累计。有覆盖用覆盖值，无覆盖用 DefaultClipLimit；显式覆盖 0 与没有覆盖不同，只绕过默认 Clip 限额，不能绕过全局总量。

Set 要求存活 Clip 和 limit>=0，完整校验后登记和即时裁剪，不创建播放宿主。Remove 返回是否移除并恢复默认，Clear 批量清覆盖；恢复更严格默认也可能触发裁剪。两项 getter 被动、不初始化 Settings/宿主，无效 Clip 返回 0；Remove/Clear 在空会话不初始化。规则借用 Clip，不拥有或卸载资源；调用方在释放前移除，Shutdown/新会话清覆盖与计数。

### 缓存预算与真实预热

```csharp
AudioKit.SoundCacheCapacity = 24;
int added = AudioKit.PrewarmSounds(24); // 目标闲置数；返回实际新增数。
```

`SoundCacheCapacity` 默认 10，0 表示不缓存，负值拒绝；与总量/Clip 活跃上限完全独立，不自动随 Limit 扩容。设置不创建宿主、不预热、不停止活跃请求。缩小立即只释放超额闲置 Voice/Source，活跃源继续播放，归还时按新预算缓存或释放；增大不自动创建。

`PrewarmSounds(int cachedCount)` 返回新增缓存数，参数是目标闲置数，不是追加数，也不包含 active。重复调用只补足目标；0 无操作且不创建宿主，负数或超过 SoundCacheCapacity 在副作用前抛异常，不夹小请求或扩大预算。显式正预热可创建现有宿主及不同的真实 inactive AudioSource，`playOnAwake=false`、无 Clip/路由/目标，不播放、不回调、不占额度、不登记去重、不消费 Random，也不创建 Listener。Sound off 期间可预热，active+idle 可超过活跃上限，这是独立资源预算的有意行为。

### 轻量会话统计

`AudioKit.SoundStats` 返回 `AudioSoundStats` readonly struct。读取和 `ResetSoundStats()` 均被动，不初始化 Settings/宿主，不复制集合或数组，不记录逐请求日志或历史 Clip 表。

| 属性 | 类型/含义 |
|---|---|
| ActiveCount / PeakActiveCount | int，当前有效 Sound 请求数/会话峰值 |
| CachedCount / OwnedSourceCount | int，当前闲置 Voice 数/领域拥有的真实 Sound Source 数 |
| AdmittedCount / StartFailureCount | long，已提交接纳尝试/开始失败；失败不反向扣历史接纳 |
| DedupRejectedCount / GlobalRejectedCount / ClipRejectedCount / PriorityRejectedCount | long，互斥主要拒播原因 |
| StolenCount / TrimmedCount | long，接纳抢占/运行时缩容裁剪 |
| CacheHitCount / CacheMissCount | long，真实播放分配的缓存命中/缺额；预热不伪造播放命中 |
| SourceCreatedCount / SourceReleasedCount | long，真实 Sound Source 创建/发起释放请求；不代表同帧原生已物理销毁 |

一次拒播只计一个主要原因：去重先判；Clip 与全局同时满归 Clip；新 Priority 不足归 Priority，不再加 Clip/global。无效参数、无随机候选与 Sound off 不归容量拒播。onStarted 前当前数、峰值、接纳及去重已提交，回调立即自停不抹峰值；开始失败另计。Music/Voice/Crossfade 固定 Source 不计此 Sound 统计。Reset 清累计、peak 设为当前 active，保留当前 active/cache/source gauges，不停播、不清 Clip 规则或去重；OwnedSourceCount 不是清零后的 created-released 差值。Shutdown/新会话全部重置，旧 lease 的清理不串入新会话。

## 可选 Mixer

### 分类默认与单次路由

`AudioOutputRoute` 是可序列化 struct，嵌套 `RouteMode` 枚举为 `Inherit/MixerGroup/Direct`，公开字段为 `Mode/Group`。`default` 与 `AudioOutputRoute.Inherit` 等价；`Direct` 明确绕过分类默认 Mixer，`ToMixer(group)` 指定单次输出组。未知 Mode、MixerGroup 模式的空/已销毁 Group，以及 Inherit/Direct 携带非空 Group 都是非法组合，在替换槽位、分配 Source 和提交去重记录前拒绝。单次覆盖可使用另一个 Mixer 的组。

```csharp
using YLFramework.Audio;

// 三项均为 BindableProperty<AudioMixerGroup>，默认 null，不写 PlayerPrefs。
AudioKit.Settings.MusicOutputGroup.Value = musicGroup;
AudioKit.Settings.VoiceOutputGroup.Value = voiceGroup;
AudioKit.Settings.SoundOutputGroup.Value = soundGroup;

AudioKit.PlayMusic(musicClip); // Inherit 分类默认
AudioKit.PlayVoice(voiceClip, outputRoute: AudioOutputRoute.Direct);
var options = AudioSoundOptions.Defaults;
options.OutputRoute = AudioOutputRoute.ToMixer(uiGroup);
AudioKit.PlaySound(soundClip, options);
// 相同 options.OutputRoute 也用于随机、固定位置/跟随 Sound 与空间 Voice。
```

改变默认组会即刻更新对应分类的当前 Inherit 请求，包括暂停、Mute、等待开关重播的请求及 Crossfade 两条 Music；不重播、不改变进度、代次、音调、音量或回调。显式 MixerGroup 和 Direct 请求不受影响；开关重播时继承最新默认组。分类默认 null 表示直接输出，不配置 Mixer 的旧调用保持原功能。池归还会清空 Source 的输出组与旧请求的 Group 引用。

### 配置资产与生命周期

通过 `Assets/Create/YLFramework/Audio/Mixer Config` 菜单创建 `AudioMixerConfig`（ScriptableObject），配置其 `Mixer/MusicGroup/VoiceGroup/SoundGroup`；公开只读属性供业务读取。分类组可以留空，留空分类直接输出；非空组必须属于配置的 Mixer，全部留空可作为直接输出配置。

```csharp
AudioKit.ApplyMixerConfig(config); // 完整校验后复制三个默认组，只初始化 Settings
AudioKit.ClearMixerRouting();     // 清空默认组；未初始化时安全无操作
```

Apply/Clear 不创建 AudioManager、AudioSource 或 AudioListener；修改配置资产不会自动广播，需重新 Apply。ClearMixerRouting 不停止请求、不清除单次覆盖、不调用 ClearFloat、不恢复 Snapshot。`Shutdown` 清理默认路由及订阅，但同样不撤销显式 Mixer 参数或 Snapshot 操作。

配置资产、Mixer、Group、Snapshot 和 Clip 均由调用方拥有；Audio 不加载、卸载或销毁它们。释放 Mixer 资源前显式清分类路由并停止仍覆盖该资源的请求。初始无效配置/路由不静默回退；播放期间外部卸载资源不自动重载，不保证继续保留效果。

### 暴露参数与 Snapshot

`AudioMixerControl` 是独立静态入口，显式接收原生 Mixer/Snapshot，不初始化 AudioKit，也不创建计时或播放宿主。所有入口仅支持 Unity 主线程；`TrySetFloat/TrySetVolume` 在 `Start` 或之后调用，不在 `Awake/OnEnable/AfterSceneLoad` 初始化回调中调用。

```csharp
// 在 Start 或之后执行；parameterName 必须是 Mixer 中已暴露的参数名。
bool set = AudioMixerControl.TrySetVolume(mixer, "MusicVolume", 0.5f);
bool read = AudioMixerControl.TryGetVolume(mixer, "MusicVolume", out float volume);
// 需要正增益或效果参数时用原始 Float API，而不是归一化音量入口。
bool gain = AudioMixerControl.TrySetFloat(mixer, "Gain", 3f);
bool raw = AudioMixerControl.TryGetFloat(mixer, "Gain", out float value);
bool released = AudioMixerControl.TryClearFloat(mixer, "MusicVolume");

float db = AudioMixerControl.LinearVolumeToDecibels(0.5f);
float linear = AudioMixerControl.DecibelsToLinearVolume(db);
AudioMixerControl.TransitionToSnapshot(snapshot, 0.25f);
AudioMixerControl.TransitionToSnapshots(mixer, snapshots, weights, 0.5f);
```

合法调用保留原生 SetFloat/GetFloat/ClearFloat 的 false，不伪造成功；TryGet 失败时 out 为 0，不应当作真实参数值。空/已销毁对象、空白参数名、非法数值或数组抛 `ArgumentException` 或派生异常。音量入口接受有限 `[0,1]`：0 映射到 -80 dB，正值为 `Max(-80, 20*Log10(v))`，1 为 0 dB；反向换算 `Clamp01(Pow(10, db/20))`，<= -80 按 0 处理。-80 是 UI 音量下限，不是绝对数字静音保证。

Settings 音量与单请求包络控制 `AudioSource.volume = Clamp01(分类音量 * 请求倍率) * gain * transitionGain`（无音乐过渡时 transitionGain=1），不会自动绑定或写入 Mixer 参数；Source 音量与 Mixer 衰减相乘。需要 Mixer 独占音量控制时由业务将分类音量保持为 1。SetFloat/SetVolume 接管对应暴露参数，Snapshot 不再控制该参数；`TryClearFloat` 把参数交还原生控制，并不清路由。手动 UI 音量与 Snapshot 不应同时争用同一个参数而期待各自独立。

Snapshot 过渡的 duration 必须有限且 >= 0；混合数组非空、等长，Snapshot 存活且属于传入 Mixer，weights 有限、非负且总和 > 0。校验后直接使用 Unity 权重语义，不修改调用方数组或额外承诺归一化/插值曲线。效果链、Send/Receive 和 Ducking 由 Unity Mixer 编辑器配置，无自动 Ducking、状态机或资源加载器。

## 组件与 Action

### 播放组件

| 组件 | 配置与控制 | 停用行为 |
|---|---|---|
| `AudioEmitter` | `clips` 均匀选择，或开启 `useWeightedRandom` 使用 `weightedClips`；2D/固定位置/跟随均支持。UnityEvent 绑定 `Play/Stop` | 停止自有循环与跟随请求；2D/固定位置 one-shot 继续，显式 Stop 才停止所有仍追踪的请求 |
| `AudioMusicPlayer` | 单 Clip，`loop/playOnAwake` 默认 true；`crossFadeOnPlay` 默认 false。UnityEvent 绑定 `Play/Stop` | 只停止自己仍持有的目标和尾轨，不误停别人的新 BGM |
| `AudioGroupPlayer` | 素材库、显式通道名、自动播放时机和命名加载，见 [素材组与组件](#素材组与组件) | 取消自身 pending 并停止本通道；销毁时撤销登记 |

Emitter/MusicPlayer 的 `playOnAwake` 在首次 Start 执行，不是每次 OnEnable。它们不自行加载资源或建立第二个 Source 池；从序列化字段构造值配置，提交给同一内核。Inspector 的 Advanced 按需显示 Pitch/Priority、去重、路由、淡变和过渡等适用字段，折叠或隐藏不清配置。新建/Reset 与旧资源的默认值差异见 [默认值与配置](#默认值与配置)，运行时 AddComponent 应显式配置，不能假定 Reset 已执行。

Emitter 先校验配置、抽选一次并检查 Clip/位置/目标，再停止需替换的旧自有循环。空、全坏或全零权重候选、无效目标不改变旧播放，非法配置抛错；拒播不重抽。`priority` 只透传 Sound 接纳优先级，不映射 `AudioSource.priority`。`Started` 是无参 UnityEvent，先登记本次句柄与停用策略再通知，允许事件内 Stop、失活或递归 Play；拒播没有事件。

两组件的 `autoFade` 默认全零，规则与 [自动首尾淡变](#自动首尾淡变) 相同。MusicPlayer 的空/销毁 Clip 不停止旧音乐；Crossfade 开启时先校验 duration/curve/配置，不预先 Stop 旧轨，未启用的过渡参数不参与校验。输入校验保护不等于容量拒播、分类关闭、原生失败或回调异常时回滚旧播放。组件停用仍会截断自己的尾轨，跨场景完整过渡应由长期存活的音乐 owner 发起。

### Mixer 控制组件

`AudioMixerController` 通过 Inspector 引用 `AudioMixerConfig`。Awake 只在 `applyOnAwake` 开启且 config 存活时应用路由，不初始化参数或切 Snapshot；无 config 跳过，显式操作缺配置或配置非法会报告带组件上下文的错误。新建/Reset 默认不自动 Apply，旧资产仍按自身序列化值工作。

UnityEvent 可绑定无参 `Apply/ClearRouting/ClearParameter/TransitionToSnapshot/TransitionToSnapshots` 及 `SetParameter(float)/SetVolume(float)`。参数名、Snapshot、权重和 duration 来自 Inspector，使用 `config.Mixer`；参数写入在 Start 或之后触发，原生 false 也报告错误。

仅控制参数/Snapshot 时保持自动 Apply 关闭。显式完整 Apply 中 Group=null 表示将对应分类路由清为 Direct，不是保留原路由。`transitionDuration` 仅供显式 Snapshot 过渡，不是参数淡变。路由是全局状态，禁用/销毁组件不 ClearRouting，也不撤销参数/Snapshot；多个自动应用组件最后一次 Apply 生效，不自动恢复旧配置。

### Action 音效

```csharp
using YLFramework.Actions;
using YLFramework.Audio;

ActionKit.Sequence()
    .PlaySound(soundClip, outputRoute: AudioOutputRoute.ToMixer(uiGroup))
    .Callback(() => UnityEngine.Debug.Log("音效等待结束"))
    .Start(host);
```

入口为 `PlaySoundAction.Allocate(clip, onFinish=null, outputRoute=default, onStarted=null)`、`AllocateWithOptions(clip, options, onFinish=null, onStarted=null)`，以及 sequence 的 `PlaySound(clip, outputRoute=default, onStarted=null)` / `PlaySoundWithOptions(clip, options, onStarted=null)`。`onStarted` 为 `Action<AudioClip, AudioHandle>`；`onFinish` 仍无参。WithOptions 的 options 位置必填、允许 null，在 Allocate 时捕获并校验独立模板，修改原对象或其他请求不影响它：

```csharp
var options = AudioSoundOptions.Defaults;
options.Priority = 10;
options.AutoFade = new AudioAutoFade(0.25f, 0.5f);
var action = PlaySoundAction.AllocateWithOptions(soundClip, options, onFinish: () => { },
    onStarted: (clip, handle) => handle.SetVolumeScale(0.8f));
ActionKit.Sequence().PlaySoundWithOptions(soundClip, options,
    onStarted: (clip, handle) => UnityEngine.Debug.Log(clip.name)).Start(host);
```

启动才播放；等待的是请求结束，不保证自然播放成功，拒播、外部 Stop、抢占或裁剪也结束等待。取消/Deinit 仅停止自身请求且不通知成功。Reset 停止旧请求、保留 Clip 与配置模板，下次重新建立包络；Deinit 清模板、回调及借用资源引用，不卸载或销毁资源。旧回调或句柄不能控制新执行代次。暂停动作或序列不自动暂停音频，自然结束仍可标记动作 Finished；完成通知按 Action 契约处理。暂停音频则继续等待。

Action 在用户 onStarted 之前写入实际 lease，允许回调 Reset/Deinit 停止已启播但尚未从 Play 返回的请求。回调返回与播放方法返回后检查 ActionId/执行版本/IsDeinitialized，旧外层赋值不覆盖重入的新执行。并发拒播与抢占仍沿用原等待完成检查，不改变自然完成/取消行为。

该 `onFinish` 仍为无参 `Action`，不迁移为音频的 `(clip, handle)` 签名；它通知 Action 等待结束，包含拒播/外部停止，不等同于 AudioKit 的自然完成回调。Action 序列的 `.Callback(() => ...)` 也保持不变。

Timer 位于 `Runtime/Foundation/Timing`，是独立到期回调服务；Audio 不使用 Timer 或 `clip.length` 代替 Source 完成检测。TimerScope、ActionHandle、AudioHandle 各管自己的所有权与生命周期，详见 [Timing](Timing.md)。

## 宿主、池与诊断

```text
外部 Clip + OutputRoute -> 输入/参数/开关/去重检查 -> AudioManager 容量接纳
    -> Music 目标/尾轨、Voice 槽位或池内 Sound -> 解析路由 -> 完整配置 Source
    -> 提交有效请求/接纳序号/去重 -> Source.Play/onStarted -> 返回原 AudioHandle
Settings 分类音量 -> Source.volume；显式 Mixer 控制 -> AudioMixerControl -> 原生 Mixer/Snapshot
Update -> 跟随位置与自然完成优先检查 -> 请求包络/音乐过渡推进 -> 代次安全清理 -> 音效回闲置池
Shutdown -> 阻止新请求 -> 停播/清池/清订阅与静态状态 -> 释放自有宿主
```

自建宿主归入 `YLFramework/Audio` 并跨场景，借用外部 GameObject 保持层级；Shutdown 仅释放自有组件/Source，不销毁借用 GameObject 或 Listener。闲置池为 LIFO，`SoundCacheCapacity` 默认 10，与 `SoundConcurrency` 总量/Clip 有效请求上限独立；仅显式正预热创建闲置音源，不自动因提高限额而创建。稳态更新复用代次快照列表，分类音量由宿主订阅而非每个音效重复订阅。不据此宣称零 GC；首次第二条 Music Source 是按需冷路径，预热/首次突发容器增长及后续稳态须分别以实际 Profiler 测量。

菜单 `YLFramework/Audio/Debug` 打开独立编辑器窗口。诊断被动展示分类开关、音量、Mute、分类默认组及有效句柄的 `Time/Duration/Progress`、路由模式和实际输出组/Mixer；每条 Sound 显示实际 Clip 实例 ID、该 Clip count/effective limit 及捕获的 Priority，不按名称合并。全局显示 active/peak/接纳/开始失败、四类主要拒播、抢占/裁剪、cache 命中/缺额/预算和 source 领域拥有数/创建/释放请求。冷态仍显示统计和配置；唯一写操作为显式 `Reset Sound statistics`，不提供播放控制或预算编辑。读取与清零不创建 Settings/AudioManager/Source/Listener。运行 GUI 仅 Editor/Development Build 存在，默认隐藏。平台快捷键沿用旧 Input：桌面/WebGL F1，Android Escape/返回，iOS 四指触屏；项目需启用旧 Input Manager 或 Both。

## 业务接入

应用自行决定战斗预算、去重窗口、组内优先级和偏好存储，不能把某个现成游戏的策略当作框架默认合同。新项目按本页正式 API、组件和 Provider 接入，不需要复制原游戏资产或恢复历史联合验收入口；框架生命周期和序列化测试使用自有夹具。

## 测试与帮助

源码仓库的测试入口如下，不随默认框架包导出；测试存在不代表当前版本或目标平台已通过。

| 位置 | 关注范围 |
|---|---|
| `Tests/EditMode/Systems/Audio/` | 设置、参数、快照、去重/并发、所有权、资源 Provider、Action 与会话隔离 |
| `Tests/PlayMode/Systems/Audio/` | 原生播放、进度/暂停/静音、空间目标、回调重入、池复用、淡变与 Action 等待 |
| `Tests/EditMode/Editor/Audio/` | Inspector、帮助、素材库迁移和重复 Play 会话夹具 |

`AudioResourceBundleTests` 使用自有正式 ResourceProject/发布夹具，不涉及原游戏 Bundle；`AudioContinuousPlayTests` 检查同域重复 Play 的隔离。运行前处理自己的脏场景，读取实际执行结果；不要用全局卸载制造归零，或把测试发现列表当作运行结果。

帮助菜单为 `YLFramework/Audio/帮助`，Provider [`AudioKitHelpProvider`](../../Editor/Systems/Audio/Help/AudioKitHelpProvider.cs) 的稳定 id 为 `audiokit`。指南页 ID 为 `overview/control/settings/spatial/mixer/components/action/timer/diagnostics`，API 页反射真实公开类型；`timer` 页仅提供独立 Timing 模块的边界与导航。XML 来自 `Library/YLFramework.Runtime.xml` 构建产物，不默认导出，见 [帮助与导出](../README.md#帮助与导出)。

编译、EditMode、PlayMode、禁用 Domain Reload 的重复会话、目标 Player、真实游戏流程与人工听感是不同验证。`Source.isPlaying`、开始通知或自动 PASS 都不能替代系统出声与听感判断。

<a id="配置分配验证"></a>

## 历史提示

整合、配置分配测量、Bundle 所有权及原游戏验收的历史记录见 [整合记录](../IntegrationPlan.md)。详细档案保留在 Git 历史中，不属于默认导出包依赖。旧记录仅对当时源码、配置和载体有效，不代表当前 Unity 测试、Player 或试听已复验，也不构成零 GC、固定性能或跨平台保证。
