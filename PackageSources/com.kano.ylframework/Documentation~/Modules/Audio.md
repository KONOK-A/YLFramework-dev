# 音频播放

> [文档首页](../README.md) · [安装](../Installation.md) · [资源管理](Resource.md) · [命名资源与素材库](Audio/Resources.md) · [高级播放与诊断](Audio/Advanced.md)

AudioKit 用于播放 Music、Voice 和 Sound，并用 `AudioHandle` 控制某一次请求。直接传入的 `AudioClip` 由调用方保活；需要按地址加载时，使用[命名资源与素材库](Audio/Resources.md)，不要把资源加载责任混入普通播放。

## 阅读路径

| 要完成的任务 | 入口 |
|---|---|
| 播放 Clip、调整 Options、处理回调与退出 | 本页 |
| 配置 Provider、AudioClipLibrary、预加载与 Clip 缓存 | [命名资源与素材库](Audio/Resources.md) |
| 独立通道、借用 AudioSource、Fade/Crossfade、空间/随机、并发、Mixer、组件、Action | [高级播放与诊断](Audio/Advanced.md) |

namespace 为 `YLFramework.Audio`，代码属于主包 `YLFramework.Runtime`。播放、控制、Provider 回调和租约归还均在 Unity 主线程执行。未给出完整类定义的示例是业务方法内的调用片段，Clip、Transform、UI 和 Mixer 引用由调用方提供。

## 最小播放

准备外部 `AudioClip`，并由相机或玩家提供一个有效、启用的 `AudioListener`。不需要显式 `Initialize`、Mixer、配置资产、预热或并发设置。

```csharp
using YLFramework.Audio;

AudioKit.PlayMusic(musicClip); // 默认循环，替换缺省 Music。
AudioKit.PlayVoice(voiceClip); // 默认单次，替换缺省 Voice。
AudioKit.PlaySound(soundClip); // 默认单次，允许重叠。
```

缺省 Music 最多使用两条 Source 做 Crossfade，Voice 是一个独占槽，Sound 每次使用可独立控制的 Source。再次显式播放同一个 Clip 也是新请求，不复用原播放位置。命名通道具有独立拓扑，见[命名通道与借源](Audio/Advanced.md#命名通道与借源)。

不用写代码时，可以添加 `AudioMusicPlayer` 并绑定 Clip；其 `loop/playOnAwake` 默认开启，只在首次 `Start` 自动播放。按钮点击音可添加 `AudioEmitter`，放入 Clip，保持 2D、Loop 关闭，再将 `Button.onClick` 绑定到无参 `Play`。组件停止与自动播放的完整规则见[播放组件与 Action](Audio/Advanced.md#播放组件与-action)。

## Options 与默认值

`AudioSoundOptions` 是可序列化的 `sealed class`，无参构造提供有效默认值。只覆盖需要的字段：

```csharp
var soundOptions = new AudioSoundOptions { Volume = 0.5f };
AudioKit.PlaySound(soundClip, soundOptions);

var musicOptions = AudioSoundOptions.MusicDefaults;
musicOptions.Pitch = 1.1f;
AudioKit.PlayMusicWithOptions(musicClip, musicOptions);

// 仅调整音乐音量不必创建 Options。
AudioKit.PlayMusic(musicClip, volume: 0.5f);
```

### 默认值与输入校验

以下是没有业务覆盖、合法 PlayerPrefs 或已保存组件值覆盖时的默认值：

| 项目 | 默认值与语义 |
|---|---|
| `new AudioSoundOptions()` / `Defaults` | 非循环，Volume=1、Pitch=1、Priority=0；每次创建独立实例 |
| `MusicDefaults` | 独立实例，Loop=true，其余与 Defaults 一致 |
| Music 省略/null Options | 循环；WithOptions 的 options 位置必填，但允许 null |
| Sound/Voice 省略/null Options | 非循环；`default(AudioSoundOptions)` 是 null |
| Music 显式传 `Defaults` / `new AudioSoundOptions()` | Loop=false，不按类别改写显式配置 |
| 空间 | 普通入口 2D；At/Following 为 3D，距离 1/50、Linear、doppler=0 |
| 输出 | `Inherit`；分类 Group=null 时直接输出，不要求 Mixer |
| 去重 | 全局 `EveryOne`，Options.PlaySoundMode=null 表示继承；两种帧窗口均默认 5 |
| Sound 并发 | Limit=0、DefaultClipLimit=0 表示不限；策略 RejectNew |
| 闲置 Source / Clip 缓存 | Source 容量 10、不自动预热；Clip 容量和 idle 秒数均 0，不缓存资产 |
| 淡变与暂停 | AutoFade 首尾均 0，AutoPauseWhenTimeScaleZero=false |
| 分类设置 | 开关 true、音量 1、临时 Mute=false；合法持久化值优先 |

`Volume` 必须有限且非负，可大于 1；0 是合法静音，不停止请求。`Pitch` 必须有限且位于 `(0,3]`，0 非法。Sound 的 `Priority` 为非负 int，只影响其[并发接纳](Audio/Advanced.md#去重与并发)，不是 `AudioSource.priority`；Music/Voice 忽略 Priority 与 Sound 去重字段。空间距离、路由、自动包络分别在使用对应功能时校验，非法输入不靠零值回退或静默修复。

提交时捕获内部值快照，之后修改 Options 不影响已提交请求，句柄修改也不反写配置。`var copy = options` 只是类引用赋值；嵌套配置按值捕获，但 Clip/MixerGroup 等 Unity 资源引用仍借用，不克隆资源。高频调用可复用业务持有的配置，每次提交仍独立捕获。`PlaySoundAction.AllocateWithOptions` 在 Allocate 时捕获模板，而不是 Start 时。

此设计不承诺整个 Audio 零 GC：配置实例、初始化、Source 创建、集合增长和闭包仍可能分配。冷路径、预热后稳态和目标 Player 性能须分别测量。

### JSON 配置

Unity 2022.3 的 `JsonUtility.FromJson<AudioSoundOptions>` 对缺字段使用 `default(T)`，不执行构造函数或字段初始化器。例如缺失 Pitch 会得到非法 0。部分 JSON 应覆盖已创建的有效默认实例：

```csharp
var options = AudioSoundOptions.Defaults; // 循环音乐选 MusicDefaults。
UnityEngine.JsonUtility.FromJsonOverwrite("{\"Volume\":0.5}", options);
AudioKit.PlaySound(soundClip, options);
```

`AudioSoundOptions` 与嵌套 `AudioAutoFade/AudioOutputRoute` 的 PascalCase 字段名也是 `JsonUtility` 的 JSON 键合同，不随一般字段命名规则改写。`FormerlySerializedAs` 不保护 JSON 键。显式 Pitch=0 仍非法，Volume=0 仍合法，不能按值为零推断字段缺失。

## 句柄与回调

```csharp
AudioHandle sound = AudioKit.PlaySound(soundClip,
    new AudioSoundOptions { Volume = 0.8f, Pitch = 1.1f },
    onFinished: (clip, handle) => UnityEngine.Debug.Log($"完成：{clip.name}"),
    onStarted: (clip, handle) => handle.SetVolumeScale(0.5f));

float seconds = sound.Time;
float duration = sound.Duration;
float progress = sound.Progress;
sound.Pause();
sound.SetPitch(1.2f);
sound.Resume();
sound.Stop();
```

### 常用入口

| 入口 | 作用 |
|---|---|
| `PlayMusic(clip, loop=true, volume=1, onStarted=null, onFinished=null, outputRoute=default)` | 硬替换全部缺省 Music，取消过渡 |
| `PlayVoice(clip, loop=false, volume=1, onStarted=null, onFinished=null, outputRoute=default)` | 替换缺省 Voice |
| `PlayMusicWithOptions(clip, options, onStarted=null, onFinished=null)` | 2D 音乐，支持 Pitch、路由、自动包络 |
| `PlayVoiceWithOptions(clip, options, onStarted=null, onFinished=null)` | 2D 人声，null 使用非循环默认值 |
| `PlaySound(clip, options=null, onFinished=null, onStarted=null)` | 2D 音效，受开关、去重和并发限制 |
| `Music` / `Voice` | 最新 Music 目标 / 当前 Voice；空态读取不创建宿主 |
| `SoundCount` | 有效 Sound 请求数，包含暂停、静音和循环，不等于发声数 |
| `GetPlayingSounds()` / `CopyPlayingSounds(list)` | 分配数组快照 / 清空并填充可复用列表 |

随机、At/Following、Crossfade、通道和命名加载分别见子页，不需要为普通播放配置这些功能。

### 状态与控制

- `IsValid` 表示原请求有效；`IsPlaying` 表示 Source 正在播放，静音时也可为 true，不证明可听见。`IsPaused` 包含手动、通道和显式启用的自动暂停原因。
- default、拒播、结束、旧代次或旧会话句柄的 Stop/Pause/Resume/SetVolumeScale/SetPitch/FadeIn/FadeOut 安全无效，连参数也不验证，不初始化宿主。业务不直接控制内部 AudioVoice/AudioSource。
- `SetVolumeScale` 只改变本请求倍率，不改分类设置或存档。实际音量为 `Clamp01(分类音量 * 局部通道音量 * 请求倍率) * gain * transitionGain`，缺省局部音量及未启用的两种 gain 均为 1。
- 有效句柄的音量倍率有限且非负，Pitch 有限且为 `(0,3]`；非法值抛 `ArgumentOutOfRangeException`，不改变状态。修改不重启、不改变代次或暂停原因；Music/Voice 关闭期间也可修改，重播时使用最新值。池复用不继承上次修改。

| 只读属性 | 契约 |
|---|---|
| `Time` | 已启播时读取 `AudioSource.time`，限制在 `[0, Duration]` |
| `Duration` | 当前有效 Clip 的 `clip.length`，不是经 Pitch 调整后的实际耗时 |
| `Progress` | Duration>0 时为 `Clamp01(Time/Duration)`，否则为 0 |

无效或已释放请求、缺失 Source/Clip 的三项均为 0，读取不初始化 Settings/宿主。Music/Voice 关闭并等待启播或重播时，Time/Progress=0，Duration 仍是有效 Clip 长度。Pause 冻结，Mute 不冻结，循环只显示当前轮，不提供 seek 或完成历史。压缩音频的 `AudioSource.time` 可能只有分包精度，不提供采样级、节拍级同步，也不要用 `Progress==1` 判断完成。

### 回调时机与重入

`onStarted/onFinished` 均为 `Action<AudioClip, AudioHandle>`，参数为实际 `(clip, handle)`。注意 Sound 的可选参数顺序为先 onFinished 再 onStarted，Music/Voice 为先 onStarted 再 onFinished，调用时优先写参数名。

- `onStarted` 在提交 `Source.Play` 后执行，可能早于播放方法返回。应控制回调传入的 handle，不捕获尚未完成赋值的返回变量。Music/Voice 等待分类开启时延迟首次开始通知，重播不重复通知。
- Sound 通知前已配置实际 Clip、Pitch、路由、初始 gain，并登记句柄、计数、去重和统计；回调递归播放同 Clip 仍受去重限制。无效输入、关闭、去重或容量拒播不触发开始/自然完成回调。
- 自然完成进入 onFinished 时，原 handle 仍有效且 IsPlaying=false，返回后才清理；不保证此时 Progress=1。回调内 Stop、替换或 Shutdown 可立即使它失效。命名资源也保活到该回调返回。
- 开始/自然完成每请求各最多一次。Stop、替换、抢占、裁剪、拒播、关闭、目标丢失和 Shutdown 都不是自然完成；循环请求必须明确停止。
- 回调可重入 StopAllSound、改变并发、递归播放、停用组件或重置 Action。外层只返回原代次句柄，可能已经失效，不会换成复用 Source 的新请求。开始回调抛异常时向调用方传播，仅清理仍属原代次的请求和去重登记，不误停嵌套新请求；正常回调主动停止自身仍算成功启播，保留成功去重记录。

## 设置与音量

```csharp
AudioKit.Settings.IsOn = true; // 直接读写三个分类开关。
AudioKit.Settings.IsMusicOn.Value = true;
AudioKit.Settings.SoundVolume.Value = 0.8f;
AudioKit.Settings.IsMusicMuted.Value = true;
```

| 操作 | Music / Voice | Sound |
|---|---|---|
| 分类 off | 停 Source，保留可控制的完整请求 | 终结全部请求，关闭期间拒播 |
| 再 on | 从头重播未被停止/替换的目标；不重复 onStarted，不解除手动 Pause | 不恢复先前请求 |
| Mute | 只静音，播放位置、跟随、包络、自然完成继续 | 同左，仍接受符合接纳规则的请求 |
| Volume=0 | 无声但不停止 | 同左 |

Music off 取消 Crossfade、结束尾轨，仅保留最新目标并恢复 transitionGain=1。手动淡变保留当前 gain/剩余时间；仍受自动包络控制的请求在重播时重置包络，已被手动接管的自动配置不复活。详见[淡变](Audio/Advanced.md#自动首尾淡变)。

六个持久化键如下，临时 Mute 不落盘：

| 分类 | 开关键 | 音量键 |
|---|---|---|
| Sound | `KEY_AUDIO_MANAGER_SOUND_ON` | `KEY_AUDIO_MANAGER_SOUND_VOLUME` |
| Music | `KEY_AUDIO_MANAGER_MUSIC_ON` | `KEY_AUDIO_MANAGER_MUSIC_VOLUME` |
| Voice | `KEY_AUDIO_MANAGER_VOICE_ON` | `KEY_AUDIO_MANAGER_VOICE_VOLUME` |

开关/音量使用 `PlayerPrefsBoolProperty/PlayerPrefsFloatProperty`。Audio 局部校验音量：Load/Reload 遇负数、NaN、Infinity 时内存回退 1，不自动重写存档；合法 0 和大于 1 的倍率保留。Delete 恢复缺省且不写回。通过 Value/TrySetValue/SetValueWithoutCallback 赋非法音量均抛错，不改变内存、存档、通知或播放。合法 Value 变化先持久化再通知；SetValueWithoutCallback 仅改内存，不通知或持久化。

Slider 通常使用 `[0,1]`。用 `RegisterWithInitValue` 同步其他入口修改，所有者退出时注销订阅和 UI 事件：

```csharp
// 在 UI owner 中保存该令牌，退出时调用 subscription.Unregister()。
var subscription = AudioKit.Settings.MusicVolume.RegisterWithInitValue(
    value => musicSlider.SetValueWithoutNotify(value));
// Slider 的 onValueChanged 将值写入 AudioKit.Settings.MusicVolume.Value。
```

三个 `Is*Muted` 是 `BindableProperty<bool>`，默认 false，支持同样的订阅模式。Shutdown 注销旧 Settings 的所有订阅；重新创建 Settings 后 Mute=false，旧 Settings 不控制新宿主。没有全局 Mute 或单句柄 Mute；通道可另有局部 IsMuted。

## 生命周期与关闭

单个组件/业务 owner 退出时停止自己的句柄、取消自己的命名请求或 Dispose 自己的通道，不随意停止全局 Music/Sound。`StopMusic/PauseMusic/ResumeMusic` 控制缺省目标、尾轨和过渡；Voice 对应方法只控缺省 Voice，`StopAllSound` 控制所有 Sound，包括命名通道 Sound。空状态控制不创建宿主。

直接 Clip 在播放、暂停、Music/Voice off 等待重播期间都必须存活。Audio 结束后断开引用，不 `Destroy`、`Resources.UnloadAsset` 或接管外部资源；Mixer/Group/Listener 也由应用拥有。命名 Provider 的资源则由实际租约控制，见[租约与缓存](Audio/Resources.md#租约与缓存)。

`AudioKit.Shutdown()` 属于应用级关闭：阻止新请求、结束播放与 pending、清自有池、闲置 Clip、设置订阅和静态会话状态，再释放自有宿主。自建宿主位于 `YLFramework/Audio` 并跨场景；借用 GameObject 保持层级，不因 Shutdown 销毁它或 Listener。旧句柄/迟到结果不能进入新会话。需要关闭 Resource 时，先归还 Audio 播放、尾轨、idle 缓存和显式 Clip 租约，再关闭对应 ResourceStartup，不能靠强卸载解决 Busy。

## 实现入口

- [`AudioKit.cs`](../../Runtime/Systems/Audio/AudioKit.cs)：直接播放、设置、槽位、去重、并发、Source 缓存和统计。
- [`AudioHandle.cs`](../../Runtime/Systems/Audio/AudioHandle.cs)：原请求代次校验、控制与进度。
- [`AudioSoundOptions.cs`](../../Runtime/Systems/Audio/Config/AudioSoundOptions.cs) / [`AudioPlaybackOptions.cs`](../../Runtime/Systems/Audio/Internal/AudioPlaybackOptions.cs)：公开配置与内部值快照、默认值和校验。
- [`AudioKitSettings.cs`](../../Runtime/Systems/Audio/Config/AudioKitSettings.cs)：Binding 设置、持久化、Mute 与分类路由。

Audio 复用主包 Pooling、Binding、Events 和 MonoSingletonHost，不是独立零依赖播放器。安装依赖见[安装](../Installation.md)，Source/原生播放证据与实际听感的区别见[诊断与帮助](Audio/Advanced.md#诊断与帮助)。
