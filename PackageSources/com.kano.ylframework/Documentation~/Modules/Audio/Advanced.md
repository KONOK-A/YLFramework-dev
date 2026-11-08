# 高级音频播放与诊断

> [音频播放](../Audio.md) · [命名资源与素材库](Resources.md) · [Action](../Action.md) · [Timing](../Timing.md) · [文档首页](../../README.md)

本页按控制任务介绍通道、借源、包络、音乐过渡、空间/随机、接纳预算、Mixer 与组件。普通播放、Options 快照、句柄和分类设置见[音频播放](../Audio.md)；所有操作仍在 Unity 主线程。

## 命名通道与借源

命名通道是同一 AudioManager 内的请求归属，不是素材组选曲或第二个播放器。一个 owner 显式登记并负责 Dispose：

```csharp
using YLFramework.Audio;

var wind = AudioKit.RegisterChannel("environment/wind");
var rain = AudioKit.RegisterChannel("environment/rain",
    persistenceId: "audio/environment/rain");
wind.Play(windClip, AudioSoundOptions.MusicDefaults);
rain.Play(rainClip, AudioSoundOptions.MusicDefaults);
AudioKit.PlayMusic(bgmClip); // 缺省 Music 双声部预算独立。

wind.Volume = 0.4f;
wind.IsMuted = true;
wind.Pause();
wind.Resume();
wind.Stop();
wind.Dispose();
rain.Dispose();
```

`RegisterChannel(name, category=Music, mode=Exclusive, persistenceId=null)` 区分大小写。名称与非 null 存储身份只允许 ASCII 字母、数字、`.`、`_`、`-`、`/`，非空且无首尾空白；同时登记冲突抛错，不隐式共用 owner。

| 配置或操作 | 契约 |
|---|---|
| Category | 选择 Music/Voice/Sound 分类设置，不决定拓扑 |
| Exclusive | 替换本通道原请求，可受控借入 Source |
| Overlap | 每次使用独立 Source/Handle，不使用不可逐次控制的原生 PlayOneShot |
| null Options | 非循环 Defaults，即使 Category=Music 也要显式设置 Loop |
| Music/Voice 通道 | 不占缺省 Music 双声部或 Voice 槽位 |
| Sound 通道 | 包括借源，仍计全局/Clip 并发、Priority、去重、SoundCount/统计和 StopAllSound |
| Volume / IsMuted | 局部 `[0,1]` 音量初值 1；静音初值 false，与分类音量相乘、Mute 取 OR，不反写分类 |
| Pause / Resume | 暂停状态也应用于后续请求；Resume 只解除通道原因，不解除句柄/自动暂停 |
| RequestCount | 有效请求数，包含暂停与等待分类开启，不等于可听见数 |

分类 off 保持统一语义：Music/Voice 保留请求，on 从头重播；Sound 终结并拒播。Options.OutputRoute 也沿用统一语义，Inherit 跟随分类默认组，显式 MixerGroup/Direct 独立。null Clip、非法配置和可提前发现的借源冲突不替换原请求；接纳拒绝仍遵循 Sound 策略。开始回调抛错只终结失败原租约，批量 Resume/分类开启尝试其他原请求后汇总异常。

只有显式 persistenceId 才读写 `YLFramework.Audio.Channel:{id}:Volume` / `:Mute`。省略时不按 Source 名、对象哈希或实例 ID 存储；登记只读取，坏音量内存回退 1、不重写。Dispose 不删除偏好，Pause/拓扑/路由不持久化，也不每次调用 PlayerPrefs.Save。

VolumeChanged/MutedChanged 仅通知局部实际变化，先更新原请求再通知，不发初始值或分类变化。同值不通知；重入写属性或 Dispose 会停止旧值余下通知。单个监听抛错后继续仍属原代次的监听，最后聚合抛出，不回滚已应用值。owner 负责退订，Dispose/Shutdown 也撤销订阅。

Dispose、Shutdown 或原宿主失效后 IsValid=false。旧通道 Stop/Pause/Resume/Dispose 安全 no-op，Play/设置写入明确抛错，不能控制同名新登记或复用请求。

### 受控借源

```csharp
callerSource.Stop(); // 明确结束 caller 的播放/暂停历史并让渡控制。
var channel = AudioKit.RegisterChannel("machine", AudioCategory.Sound);
var handle = channel.PlayBorrowed(callerSource, machineClip,
    new AudioSoundOptions { Loop = true, OutputRoute = AudioOutputRoute.Direct });
handle.Pause();
handle.Resume();
handle.Stop();
channel.Dispose(); // Source 和 GameObject 仍归 caller。
```

- 仅 Exclusive 支持。Source 必须有效、active/enabled、已停止并明确让渡控制；框架自有、正在播放或已借用（含暂停）的 Source 拒绝。再次借同一 Source 前须结束原 Handle，不隐式抢占。
- 仅配置 clip/loop/pitch/volume/mute/outputAudioMixerGroup，不改父级、Transform、active/enabled、playOnAwake、空间曲线或滤镜；不放入 Source 缓存、不销毁或停用 caller 对象。
- Stop、替换、自然结束、失活、Source 销毁和 Shutdown 都进入原 Voice 全终态。先撤销请求与占用，再允许资源释放回调重入；仅 Source 仍持本次 Clip 时停止，逐字段比较最后写入值，未被外部改变的字段才恢复原值。外部换 Clip 则完全放弃停止/恢复。
- 不恢复之前的播放位置、暂停或播放历史。租约期间只能经 Handle/通道控制，不得直接对同 Source Play/Pause/Stop/PlayOneShot；同 Clip 外部重播或写回相同值无法可靠识别，字段比较不能证明此类归属。
- 同 Source 的原生 PlayOneShot 重叠不提供逐请求控制，需独立控制时使用普通 Overlap/PlaySound。

命名借源为 `PlayBorrowedNamed(source, address, options)` / `PlayBorrowedAsync(...)`。加载期间不占用或修改 Source，提交时复验；等待中失活/销毁为 Unavailable，占用冲突异步为 Failed/Error、同步抛错。加载或替换前校验失败保留旧播放；释放旧资源后遇重入冲突不回滚已结束请求，也不覆盖嵌套取得 Source 的 owner。Clip 按全终态归还，不依赖 onFinished。

### 局部自动暂停

```csharp
var options = new AudioSoundOptions { Loop = true, AutoPauseWhenTimeScaleZero = true };
var handle = AudioKit.PlaySound(loopClip, options);
```

只对捕获该选项的请求响应 `Time.timeScale==0f`，由现有 Tick 驱动；零 timeScale 提交的请求等待恢复才首次开始。条件解除只清自动原因，手动/通道暂停仍在时不续播。IsPaused 包含任一原因，进度、fade 和参与的缺省 Music crossfade 都冻结，不操作 AudioListener.pause。Music 过渡只继承手动暂停，不把自动原因变为永久手动暂停。

## 单请求淡变

```csharp
var sound = AudioKit.PlaySound(soundClip);
sound.FadeIn(0.25f);
sound.FadeOut(0.5f, stopWhenDone: true, ignoreTimeScale: true);

AudioKit.PlayMusic(musicClip,
    onStarted: (clip, handle) => handle.FadeIn(0.5f));
```

签名为 `FadeIn(float duration, bool ignoreTimeScale=true)` 与 `FadeOut(float duration, bool stopWhenDone=true, bool ignoreTimeScale=true)`。包络 gain 独立于分类/局部音量、Options.Volume 和 SetVolumeScale，与 Music transitionGain 相乘，不回写 Settings/存档。

- 从未经历淡变的新请求，首次正时长 FadeIn 立即 gain=0 再趋向 1；经历过任何淡变（含零时长）后，从当前 gain 趋向 1，不重复跳零。FadeOut 从当前值趋向 0，新淡变替换旧调度。
- 有效句柄 duration 有限且非负，非法值抛 ArgumentOutOfRangeException 且不改状态。0 立即到目标；FadeOut(0, true) 立即 Stop；stopWhenDone=false 到 0 后仍有效，可再次淡入。无效句柄连非法参数也不校验。
- 默认 unscaled，显式 ignoreTimeScale=false 使用 scaled。手动 Pause、分类 off 或尚未开始冻结包络和剩余时间；Mute 仍推进。Music/Voice 重新开启从头重播 Clip，但保留手动 gain 和剩余淡变。
- Stop、自然结束、替换、目标丢失、Shutdown 和回池清理淡变。淡出 Stop 不通知自然完成；Clip 在淡变结束前自然完成则自然完成优先，正常通知。

淡变使用 AudioManager.Update，不是 DSP/采样级调度。同帧调用或开始回调不保证首采样已静音，也不保证采样级无缝交接；无需另建 Tween/Timer 宿主。

## 音乐交叉淡变

```csharp
AudioKit.PlayMusic(menuMusic);
AudioHandle target = AudioKit.CrossFadeMusic(battleMusic, 1.5f,
    ignoreTimeScale: true, curve: AudioCrossfadeCurve.EqualPower);
AudioKit.PauseMusic();
AudioKit.ResumeMusic();
AudioKit.StopMusic();
```

直接 Clip 签名为 `CrossFadeMusic(clip, duration, options=null, onStarted=null, onFinished=null, ignoreTimeScale=true, curve=EqualPower)`。null Options 默认 Loop=true，显式 Defaults 则 Loop=false；捕获 Volume/Pitch/OutputRoute/AutoFade，忽略空间字段、去重和 Priority，不使用 Sound 池/预算。命名版本的实际参数见[命名播放](Resources.md#命名播放)。

duration 有限且非负，0 立即替换；无效 Clip 返回 default、不改变音乐。合法 Clip 的参数、曲线、路由和包络元数据都在停止旧曲或分配 Source 前校验。未知曲线在 duration 校验后、副作用前抛错。

### 曲线与双轨

令 `u=Clamp01(elapsed/duration)`：

| 曲线 | incoming | outgoing | 满增益中点 |
|---|---|---|---|
| Linear=0 | u | capturedOutgoingGain*(1-u) | 各 0.5，平方和约 0.5 |
| EqualPower | sin(u*pi/2) | capturedOutgoingGain*cos(u*pi/2) | 各约 0.7071，平方和约 1 |

正时长 incoming 在 Source.Play/onStarted 前 transitionGain=0；没有旧曲也从零淡入。结束目标增益=1，停止尾轨且不通知其自然完成。实际音量仍为 `Clamp01(category * requestScale) * requestEnvelopeGain * transitionGain`；命名通道才另乘局部音量。请求包络与过渡独立，手动接管 AutoFade 不取消 Crossfade。

EqualPower 适合强度接近且不相关素材，不保证等响度；相关/同相素材仍可能叠加峰值，需响度匹配与试听。outgoing 按捕获的当前增益退场，连续切歌不先跳回 1。duration=0 时曲线不参与。

- AudioKit.Music 立即指最新目标，不指响度最大的曲目；尾轨退场前原 handle 仍有效，但不会升回主曲。最多两条 Music Source，第二条按需创建、退场后可复用，Shutdown 释放。
- 最新请求优先、不排队。A→B→C 时先停 A，B 从当前增益退到 0，C 从 0 到 1；快速切歌可截断最早尾轨并降低响度，不保证三曲无损交接。同 Clip 也是重开的新请求。
- PlayMusic/PlayMusicWithOptions 硬替换全部 Music 并取消过渡；StopMusic 清两轨；PauseMusic/ResumeMusic 暂停两轨及共同计时。单个 handle 控制自身，但任一有效参与轨暂停会冻结共同时钟，不自动暂停另一轨。
- 暂停中发起 Crossfade，新目标继承暂停，ResumeMusic 才首次启播；普通 PlayMusic 替换不继承暂停。Music off 取消过渡、结束尾轨，只保留目标并设 transitionGain=1；off 中 Crossfade 只换待播放目标。Mute 不冻结。
- 分类音量/Mute/Inherit 路由实时影响两轨，各自 Direct/显式路由仍独立。尾轨属于 Music，不用 Voice 设置。
- 尾轨已逻辑替换，即使原生先结束也不通知自然完成。非循环目标若提前自然结束，通知自身一次，尾轨继续剩余退场；目标主动 Stop/淡出停止/资源丢失不恢复旧曲。一侧失效不冻结另一侧，两侧失效清过渡。
- 新目标、两条租约和过渡 epoch 在开始回调前提交。回调异常只清仍属本操作的请求与过渡，不恢复已替换音乐，也不误清重入提交的新请求。

## 自动首尾淡变

`AudioAutoFade` 是可序列化 struct，字段为 FadeInSeconds/FadeOutSeconds；构造仅赋值，播放入口校验。default/两项 0 关闭，时长按**Clip 时间**而非现实等待秒数。

```csharp
var options = AudioSoundOptions.Defaults;
options.AutoFade = new AudioAutoFade(2f, 3f);
AudioKit.PlaySound(soundClip, options);
AudioKit.PlayVoiceWithOptions(voiceClip, options);

options.Loop = true;
options.AutoFade = new AudioAutoFade(1f, 0f); // 循环不允许自动尾淡出。
var music = AudioKit.PlayMusicWithOptions(musicClip, options);
music.FadeOut(0.5f); // 手动接管，按 unscaled 秒淡出并停止。
```

自动时钟使用 `AudioSource.timeSamples/(double)clip.frequency`，长度为 samples/frequency；Pitch=2 时现实耗时减半，运行中改 Pitch 只改变位置推进，不重新计时。Time.timeScale 不参与，也没有自动包络 ignoreTimeScale 参数，不改变公开 Handle.Time/Duration/Progress 的合同。

非循环首尾 x/y 超过长度 L 时按 double 同比缩短，两侧不重叠，理论交接 gain=1：

```text
scale = x+y>L ? L/(x+y) : 1
a = x*scale; b = y*scale
gainIn  = a>0 ? Clamp01(t/a) : 1
gainOut = b>0 ? Clamp01((L-t)/b) : 1
gain = Min(gainIn, gainOut)
```

两字段有限且非负，`Loop && FadeOutSeconds>0` 明确拒绝。非零包络还要求有效 samples/frequency、有限正长度，在分配宿主、登记去重或替换槽位前验证。纯淡出初始 gain=1，有淡入则在 Play/onStarted 前 gain=0；未启播不消耗包络。随机只抽一次，按实际选中 Clip 归一化，不按列表首项推测，不因拒播重抽。

循环仅首次淡入，时长 min(FadeInSeconds,L)，达到时长或检测首轮 PCM 回绕后锁存 gain=1。低帧率跨过整轮时无法仅凭位置可靠识别遗漏循环；短循环、压缩/Streaming 与目标平台 timeSamples 须实测。帧级更新不保证首/末采样精确为零，也不保证极短包络可完整观察。

自动包络不 Stop、不改 loop、不提前结束，完成检测先写 t=L 的最终包络再通知自然完成。合法手动 FadeIn/FadeOut 取消自动控制并从当前位置 gain 接管，不叠第二个请求包络；非法手动时长不取消自动模式。Pause/off 保留最后 gain，Mute 不冻结。Music/Voice off/on 仅在自动模式仍 active 时重置首轮状态，手动接管后不复活自动配置。Stop、结束、替换、目标丢失、Shutdown、回池清状态；没有运行中恢复自动模式的句柄 API。

## 随机与空间播放

```csharp
var options = new AudioSoundOptions
{
    MinDistance = 2f, MaxDistance = 30f,
    RolloffMode = UnityEngine.AudioRolloffMode.Logarithmic
};
AudioKit.PlayRandomSound(clips, options);
AudioKit.PlaySoundAt(soundClip, worldPosition, options);
AudioKit.PlaySoundFollowing(soundClip, target, options);
AudioKit.PlayVoiceAt(voiceClip, worldPosition, options);
AudioKit.PlayVoiceFollowing(voiceClip, character, options);
```

均匀随机忽略 null/已销毁 Clip，空/全坏返回无效句柄；选中后按实际 Clip 去重，回调返回实际 Clip/原代次。普通 Sound/Voice 是 2D，Music 只公开 2D 入口；At/Following 在开始前设世界位置、spatialBlend=1，默认距离 1/50、Linear、doppler=0。

空间距离有限且 `0<=MinDistance<=MaxDistance`、MaxDistance>0，衰减仅 Linear/Logarithmic；无效目标、非法参数不替换 Voice。Voice 仍只有一个缺省槽，不受 Sound 去重/容量/静音/StopAllSound/统计影响，使用 Voice 分类设置，不提供多 NPC 并发 Voice 槽。

Following 不把 Source 重挂到业务 Transform，而是更新世界位置；目标销毁、停用、场景卸载时结束，不通知自然完成。Voice off/暂停仍跟踪目标，重播前再验证。固定位置 one-shot 可在发起对象销毁后播完，循环须主动停止。2D 替换与池复用重置空间、位置、音调、倍率、loop、Clip、暂停与回调。

### 加权选择

```csharp
var weighted = new[]
{
    new AudioWeightedClip(commonClip, 3f),
    new AudioWeightedClip(rareClip, 1f)
};
AudioKit.PlayWeightedRandomSound(weighted, options);
if (AudioKit.TrySelectWeightedRandomClip(weighted, out var selected))
    AudioKit.PlayVoiceFollowing(selected, character, options);
```

AudioWeightedClip 的字段为 `clip/weight`，默认构造参数 weight=1。API 接受 `IReadOnlyList<AudioWeightedClip>`。所有项权重都必须有限且非负，包括 null/已销毁 Clip 项；非法权重抛 ArgumentOutOfRangeException。仅有效 Clip 且 weight>0 参与，double 累计，只抽一次。null/空/全零返回无请求；纯选择返回 false/out=null，且不初始化 Settings/宿主。拒播不重抽，连续同 Clip 合法，不提供防重复随机。需要可共享具名组时用[AudioClipLibrary](Resources.md#素材库)。

## 去重与并发

```csharp
AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames;
AudioKit.GlobalFrameCountForIgnoreSameSound = 10; // 显式业务覆盖，默认 5。
AudioKit.SoundFrameCountForIgnoreSameSound = 10;
AudioKit.SoundConcurrency = new AudioSoundConcurrency
{
    Limit = 24, DefaultClipLimit = 3,
    OverflowPolicy = SoundOverflowPolicy.StopLowestPriority
};
AudioKit.SetClipSoundLimit(explosionClip, 2);
AudioKit.SetClipSoundLimit(uiClip, 0); // 只绕过默认 Clip 上限，不绕过全局。
AudioKit.PlaySound(importantClip, new AudioSoundOptions { Priority = 10 });
```

EveryOne 才关闭去重；GlobalFrames 使用共享窗口起点，SoundFrames 按 Clip 实例独立记录。两窗口默认 5 帧，显式 0 仅同帧去重，边界包含末帧，不是毫秒冷却。只有成功接纳才提交记录，开始回调前已提交。较早自然结束不会删较晚代次记录；Stop 不额外清窗口，过期记录定期淘汰，开始异常只回滚原租约记录。

SoundConcurrency 是 struct 整体读写属性，Limit/DefaultClipLimit 为 0 表示不限，默认 RejectNew=0，另有 StopOldest=1、StopLowestPriority=2。负限额/未知枚举在应用前拒绝，不改变配置或请求。配置可初始化 Settings/去重器，但不创建 AudioManager/Source/Listener，不持久化；Shutdown/新会话恢复默认。

只限制有效 Sound（含 Pause/Mute/Loop），不是 Unity native/virtual voice 数或场景全部 AudioSource 上限。所有 Sound 入口、Emitter、Action 和命名 Sound 共用：选择一次 Clip → 校验 → 开关 → 去重检查 → 全局/Clip 联合容量 → 必要抢占一条 → 登记句柄/计数/Priority/序号 → 提交去重与统计 → Play/onStarted。

| 策略 | 满额接纳 | 降低上限时裁剪 |
|---|---|---|
| RejectNew | 拒新，不分配、不回调、不提交去重、不停旧请求 | 保留较早，停止最新 |
| StopOldest | 停候选中最早接纳者，同帧按单调序号 | 保留较新，停止最旧 |
| StopLowestPriority | 最低 Priority，同级最旧；incoming 更低则拒绝，同级或更高可替换 | 保留高优先级，同级保留较新 |

仅全局满时候选为全部 Sound；Clip 满或两项同时满时只能选同一 Clip，一次最多停止一条。缩容先各 Clip 再全局，避免多停；提高/设 0 不停请求。Priority 非负且越大越重要，支持 int.MaxValue，不映射原生反向优先级；RejectNew/StopOldest 不因 Priority 获保护。任何 Priority 不绕过去重，EveryOne 不绕过容量。

原生结束尚未 Update 清理的有效请求仍占容量，不为腾位提前通知。结束、Stop、off、目标失效、回池与 Shutdown 释放额度；抢占/裁剪无自然完成。拒播不重抽或延长窗口，释放容量后同帧可重试但仍受已有成功去重约束。Action 在后续检查观察到原句柄结束，不将抢占当自然完成。

### 单个 Clip 与 Source 缓存

SetClipSoundLimit/RemoveClipSoundLimit/ClearClipSoundLimits 设置、恢复默认或清覆盖；GetEffectiveClipSoundLimit/GetSoundCount 被动查询，无效 Clip 返回 0。按 Clip 实例而非名称/路径合并计数，同一 Clip 各调用者、2D/3D/路由共用；显式覆盖 0 与无覆盖不同。Set 要求存活 Clip/limit>=0，规则变严立即裁剪；规则借用 Clip，不拥有资产，卸载前由调用方移除，Shutdown 清空。

```csharp
AudioKit.SoundCacheCapacity = 24;
int added = AudioKit.PrewarmSounds(24);
```

SoundCacheCapacity 默认 10，0 不缓存，负数拒绝；独立于活跃上限和[Clip 缓存](Resources.md#租约与缓存)。设置不创建宿主/预热/停活跃请求；缩容只释放超额 idle，活跃归还时按新预算处理，增大不自动创建。

PrewarmSounds 的参数是目标 idle 数，不是追加数、不含 active；重复只补足目标，返回新增数。0 不创建宿主，负数或超过预算在副作用前拒绝。正预热创建真实不同的 inactive Source，playOnAwake=false，无 Clip/路由/目标、不播放、不回调、不占容量、不登记去重、不消费 Random、不建 Listener。Sound off 时也可预热，active+idle 可超过活跃上限。

### 会话统计

SoundStats 返回不复制集合的 readonly AudioSoundStats，读取/ResetSoundStats 均不初始化 Settings/宿主：

| 指标 | 含义 |
|---|---|
| ActiveCount / PeakActiveCount | 当前有效 Sound / 会话峰值 |
| CachedCount / OwnedSourceCount | idle Voice / 领域拥有的真实 Sound Source |
| AdmittedCount / StartFailureCount | 已提交接纳尝试 / 开始失败，失败不回扣接纳历史 |
| DedupRejectedCount / GlobalRejectedCount / ClipRejectedCount / PriorityRejectedCount | 互斥的主要拒播原因 |
| StolenCount / TrimmedCount | 接纳抢占 / 运行期缩容 |
| CacheHitCount / CacheMissCount | 真实播放缓存命中/缺额，预热不伪造命中 |
| SourceCreatedCount / SourceReleasedCount | 创建 / 发起释放，不证明同帧 native 已销毁 |

去重先判；Clip/全局同时满归 Clip，优先级不足归 Priority，不重复计。无效参数、无候选或 Sound off 不计容量拒播。onStarted 前 active/peak/接纳已提交，回调自停不抹峰值；Music/Voice Source 不计入。Reset 清累计，peak=当前 active，保留 gauges、不停播/清规则；OwnedSourceCount 不等于清零后的 created-released。新会话重置，旧句柄不串数。

## Mixer 控制

### 分类路由与单次覆盖

AudioOutputRoute 是 struct，字段 Mode/Group，模式为 Inherit/MixerGroup/Direct。default=Inherit；分类组 null 时 Direct，ToMixer(group) 可使用另一个 Mixer 的组。未知模式、MixerGroup 空/销毁组、Inherit/Direct 携非空 Group 都在替换/分配/去重前拒绝。

```csharp
AudioKit.Settings.MusicOutputGroup.Value = musicGroup;
AudioKit.Settings.VoiceOutputGroup.Value = voiceGroup;
AudioKit.Settings.SoundOutputGroup.Value = soundGroup;
AudioKit.PlayMusic(musicClip);
AudioKit.PlayVoice(voiceClip, outputRoute: AudioOutputRoute.Direct);
AudioKit.PlaySound(soundClip, new AudioSoundOptions
{
    OutputRoute = AudioOutputRoute.ToMixer(uiGroup)
});
```

分类组三项为 `BindableProperty<AudioMixerGroup>`，默认 null，不持久化。变化即时更新当前 Inherit 请求，包含暂停、Mute、off 等待和 Crossfade 两轨，不重播或改代次/进度；Direct/显式组不受影响。回池清路由引用。

通过 `Assets/Create/YLFramework/Audio/Mixer Config` 创建 AudioMixerConfig，配置 Mixer/MusicGroup/VoiceGroup/SoundGroup。非空组须属于配置 Mixer，空分类明确 Direct。`AudioKit.ApplyMixerConfig(config)` 完整校验后复制三组，只初始化 Settings；改资产后须再 Apply。ClearMixerRouting 只清分类默认，空态 no-op，不停播、不清单次覆盖、不 ClearFloat 或恢复 Snapshot。Shutdown 也不撤销原生参数/Snapshot 操作。

Mixer/Group/Snapshot/配置资产归应用；释放前清分类路由、结束仍覆盖该资源的请求。非法初始配置不静默回退，外部卸载也不自动重载。

### 参数与快照

AudioMixerControl 独立接收原生 Mixer/Snapshot，不初始化 AudioKit 或计时宿主。主线程使用，TrySetFloat/TrySetVolume 在 Start 或之后，不在 Awake/OnEnable/AfterSceneLoad 初始化回调：

```csharp
bool set = AudioMixerControl.TrySetVolume(mixer, "MusicVolume", 0.5f);
bool read = AudioMixerControl.TryGetVolume(mixer, "MusicVolume", out float volume);
bool raw = AudioMixerControl.TrySetFloat(mixer, "Gain", 3f);
bool got = AudioMixerControl.TryGetFloat(mixer, "Gain", out float value);
bool released = AudioMixerControl.TryClearFloat(mixer, "MusicVolume");
float db = AudioMixerControl.LinearVolumeToDecibels(0.5f);
float linear = AudioMixerControl.DecibelsToLinearVolume(db);
AudioMixerControl.TransitionToSnapshot(snapshot, 0.25f);
AudioMixerControl.TransitionToSnapshots(mixer, snapshots, weights, 0.5f);
```

合法调用保留原生 false，TryGet 失败 out=0 不是实际参数值；空/销毁对象、空白参数名、非法数值/数组抛参数异常。音量 API 接受有限 `[0,1]`，0→-80dB，正值→Max(-80,20*Log10(v))，1→0dB；反向 Clamp01(Pow(10,db/20))，<=-80 视为 0。-80 是 UI 下限，不是绝对数字静音。正增益/效果参数用 Float API。

Settings 音量和请求包络仍改 Source.volume，与 Mixer 衰减相乘，不自动绑定参数。需 Mixer 独占音量时，业务将分类音量保持 1。SetFloat/SetVolume 接管暴露参数，Snapshot 不再控制它；ClearFloat 交回原生控制，不清路由。

Snapshot duration 有限且非负；混合数组非空、等长，Snapshot 存活且属于 Mixer，weights 有限非负且总和>0。校验后使用 Unity 权重语义，不修改数组或额外承诺归一化/曲线。效果链、Send/Receive、Ducking 由 Unity Mixer 配置，无自动 Ducking/参数淡变调度。

## 播放组件与 Action

| 组件 | 用法与停用责任 |
|---|---|
| AudioEmitter | clips 均匀，useWeightedRandom/weightedClips 加权；2D/固定/跟随；停用停止自有循环和跟随，2D/固定 one-shot 可续播，显式 Stop 停仍追踪全部请求 |
| AudioMusicPlayer | 单 Clip，loop/playOnAwake=true，crossFadeOnPlay=false，duration=1、unscaled=true、curve=EqualPower；停用只停自有目标/尾轨，不误停他人新 BGM |
| AudioGroupPlayer | 具名素材库、显式通道、异步命名与借源；[配置入口](Resources.md#配置素材组组件)，停用取消本通道 pending/播放，销毁撤销登记 |

Emitter/MusicPlayer 只在首次 Start 执行 playOnAwake，不在每次 OnEnable；编辑器/运行时创建分别统一 useGlobalPlaySoundMode=true 与 curve=EqualPower，明确保存 false/Linear 优先。它们不加载 Clip、不另建池，从字段构造内部值配置；Inspector 折叠/隐藏不清值。autoFade 默认全零。

Emitter 先验证、抽一次、检查 Clip/位置/目标，再停需替换的自有循环；坏候选/目标不改变原播放，非法配置抛错，容量拒播不重抽。Started 为无参 UnityEvent，先登记句柄/停用策略再通知，可重入 Stop/失活/递归 Play；拒播不通知。MusicPlayer 空 Clip 不停旧曲，启用 Crossfade 后先校验，不预先停尾轨；未启用过渡的参数不参与校验。输入保护不表示原生/回调/容量失败可回滚。跨场景完整尾轨交接应由长寿命音乐 owner 发起。

### 控制 Mixer 的组件

AudioMixerController 的 Awake 只在 applyOnAwake=true 且 config 有效时 Apply 路由，不写参数或切 Snapshot。**编辑器新建/Reset** 为 applyOnAwake=false、transitionDuration=0.5；字段初始化为 true/0，运行时 AddComponent 不承诺调用 Reset，因此不能把编辑器预设当运行时创建保证。无 config 的 Awake 跳过，显式缺配置/非法配置及原生 false 会报告带组件上下文的错误。

UnityEvent 可绑 Apply/ClearRouting/ClearParameter/TransitionToSnapshot/TransitionToSnapshots 及 SetParameter(float)/SetVolume(float)，使用 config.Mixer。参数写入从 Start 或之后触发。仅控制参数/Snapshot 时关闭自动 Apply；完整 Apply 的空 Group 明确清 Direct，不表示保留。transitionDuration 只供显式 Snapshot 过渡。路由为全局状态，停用/销毁不恢复路由/参数/Snapshot，多组件最后一次 Apply 生效。

### 音效动作桥

```csharp
using YLFramework.Actions;
using YLFramework.Audio;

var options = new AudioSoundOptions
{
    Priority = 10, AutoFade = new AudioAutoFade(0.25f, 0.5f)
};
ActionKit.Sequence()
    .PlaySoundWithOptions(soundClip, options,
        onStarted: (clip, handle) => handle.SetVolumeScale(0.8f))
    .Callback(() => UnityEngine.Debug.Log("音效等待结束"))
    .Start(host);
```

入口为 PlaySoundAction.Allocate(clip, onFinish=null, outputRoute=default, onStarted=null)、AllocateWithOptions(clip, options, onFinish=null, onStarted=null)，以及 sequence.PlaySound(clip, outputRoute=default, onStarted=null) / PlaySoundWithOptions(clip, options, onStarted=null)。WithOptions 位置必填、可 null，在 Allocate 捕获/校验独立模板，Start 才播放；仅支持直接借用 Clip。

等待原请求结束，不保证自然播放成功：拒播、外部 Stop、抢占或裁剪也结束等待。onFinish 是无参 Action，与 Audio onFinished 的自然完成含义不同。取消/Deinit 只停自身且不通知成功；Reset 停旧请求、保留模板，下次重新建包络；Deinit 清模板/回调/引用，不卸载 Clip。暂停 Action/sequence 不自动暂停音频，音频自然结束仍可标记 Finished；暂停音频则继续等待。

用户 onStarted 前已写实际租约，可回调 Reset/Deinit；返回后复验 ActionId/执行代次，旧外层不覆盖新执行。Timer 是独立到期服务，不用 clip.length/Timer 模拟音频完成；TimerScope、ActionHandle 和 AudioHandle 各管自己的责任，见[Timing](../Timing.md)。

## 诊断与帮助

菜单 `YLFramework/Audio/Debug` 打开被动编辑器窗口，显示分类设置、Time/Duration/Progress、路由、实际 Clip 实例、Clip count/limit/Priority、并发拒播与池统计。冷态也可读，不创建 Settings/AudioManager/Source/Listener；唯一写操作是显式 Reset Sound statistics，不提供播放或预算编辑。

运行 GUI 只在 Editor/Development Build，默认隐藏，使用旧 Input：桌面/WebGL F1、Android Escape/返回、iOS 四指；项目需启用旧 Input Manager 或 Both。GUI 的平台快捷键不表示 Resource Bundle/远端也支持该平台。

帮助菜单为 `YLFramework/Audio/帮助`，提供方稳定 id 为 audiokit；指南页 overview/control/settings/spatial/mixer/components/action/timer/diagnostics，API 页反射当前公开类型。timer 页只导航独立 Timing。XML 来自 `Library/YLFramework.Runtime.xml` 构建产物，不随包交付；系统帮助入口见[帮助系统](../Help.md)。

排查无声时分别检查 Listener、分类开关/Mute/音量、句柄有效性、去重/容量统计、空间距离/目标和 Mixer 参数。Audio 不创建第二 Listener；IsPlaying/Started/Source 参数正确不证明系统出声。编译、EditMode、PlayMode、禁用 Domain Reload 重复会话、目标 Player 和人工听感是不同证据，实际设备上的播放与听感须分别观测。

自有宿主跨场景，Sound idle 为 LIFO；Update 复用代次快照、分类设置由宿主订阅，不等于零 GC。Source 创建/释放计数不是 native 内存；首次第二 Music Source、突发扩容和稳态需分别 Profiler。框架不提供逻辑分组预算、距离/可听度抢占、虚拟化、DSP/节拍调度、HRTF、遮挡或防重复随机。

## 实现入口

- [`AudioChannel.cs`](../../../Runtime/Systems/Audio/AudioChannel.cs) / [`AudioBorrowedSource.cs`](../../../Runtime/Systems/Audio/Internal/AudioBorrowedSource.cs)：通道身份与按归属恢复借源。
- [`AudioVoice.cs`](../../../Runtime/Systems/Audio/Internal/AudioVoice.cs) / [`AudioMusicPlayback.cs`](../../../Runtime/Systems/Audio/Internal/AudioMusicPlayback.cs)：请求包络、完成与双轨过渡。
- [`Config/`](../../../Runtime/Systems/Audio/Config/)：AutoFade、CrossfadeCurve、WeightedClip、SoundConcurrency、OutputRoute 和 MixerConfig。
- [`AudioSoundStats.cs`](../../../Runtime/Systems/Audio/AudioSoundStats.cs) / [`Diagnostics/`](../../../Runtime/Systems/Audio/Diagnostics/)：会话计数与被动诊断。
- [`AudioMixerControl.cs`](../../../Runtime/Systems/Audio/Mixer/AudioMixerControl.cs)、[`Components/`](../../../Runtime/Systems/Audio/Components/)、[`PlaySoundAction.cs`](../../../Runtime/Systems/Audio/Actions/PlaySoundAction.cs)：Mixer、薄组件及 Action 桥。
- [`Editor/Systems/Audio/`](../../../Editor/Systems/Audio/)：Inspector、编辑器诊断与帮助提供方。
