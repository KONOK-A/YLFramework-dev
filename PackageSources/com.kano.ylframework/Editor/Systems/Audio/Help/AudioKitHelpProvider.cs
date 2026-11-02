using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Help;
using YLFramework.Audio;

namespace YLFramework.Editor.Audio
{
    /// <summary>显式注册 AudioKit 帮助工厂，连接静态指南、公开类型和 Runtime 源码 XML。</summary>
    [InitializeOnLoad]
    internal static class AudioKitHelpProvider
    {
        /// <summary>帮助注册表和窗口定位 AudioKit 时使用的稳定标识。</summary>
        internal const string ModuleId = "audiokit";

        /// <summary>在 Editor 程序集初始化时登记内容工厂，不提前构建页面或初始化音频。</summary>
        static AudioKitHelpProvider() => HelpRegistry.Register(ModuleId, "AudioKit", CreateModule);

        /// <summary>打开帮助窗口并选中 AudioKit 模块。</summary>
        [MenuItem("YLFramework/Audio/帮助")]
        private static void Open() => HelpWindow.Open(ModuleId);

        /// <summary>建立 Audio 命名空间的公开 API 与指南定义，XML 缺失时提示而不生成或伪造文档。</summary>
        internal static HelpModule CreateModule()
        {
            var runtime = typeof(AudioKit).Assembly;
            var paths = new[] { runtime }.ToDictionary(assembly => assembly, assembly => Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "Library", assembly.GetName().Name + ".xml")));
            var guides = CreateGuides();
            if (!File.Exists(paths[runtime]))
            {
                guides[0] = new HelpPage("overview", guides[0].Title, guides[0].Group, guides[0].Summary,
                    guides[0].Blocks.Concat(new[] { HelpBlock.Notice("XML 文档尚未生成",
                        "模块 csc.rsp 在 Unity 编译时生成 Library/YLFramework.Runtime.xml。声明来自真实程序集，" +
                        "缺少 XML 时不按名称补造说明；请等待统一脚本编译完成。") }).ToArray())
                {
                    RelatedTypes = guides[0].RelatedTypes,
                    FeaturedTypes = guides[0].FeaturedTypes
                };
            }

            return new HelpModule
            {
                Guides = guides,
                About = CreateAbout(),
                ApiTypes = runtime.GetExportedTypes()
                    .Where(type => type.Namespace == "YLFramework.Audio" ||
                        (type.Namespace != null && type.Namespace.StartsWith("YLFramework.Audio.", StringComparison.Ordinal)))
                    .OrderBy(type => type.FullName, StringComparer.Ordinal)
                    .Select(type => new HelpApiTypeDefinition(type,
                        type == typeof(PlaySoundAction) || type == typeof(PlaySoundExtension) ? "ActionKit Audio 集成" : "Audio Runtime")).ToArray(),
                XmlPaths = paths
            };
        }

        /// <summary>组合指南页，并将指定类型同时作为关联类型和优先展示类型。</summary>
        /// <param name="id">稳定页面标识。</param>
        /// <param name="title">页面标题。</param>
        /// <param name="summary">指南简介。</param>
        /// <param name="types">关联及优先展示的 API 类型。</param>
        /// <param name="blocks">按显示顺序排列的内容块。</param>
        private static HelpPage Guide(string id, string title, string summary, Type[] types, params HelpBlock[] blocks) =>
            new HelpPage(id, title, "使用指南", summary, blocks) { RelatedTypes = types, FeaturedTypes = types };

        /// <summary>建立播放、配置、Provider 接入与诊断指南；示例仅作为文本展示，不在此执行。</summary>
        private static HelpPage[] CreateGuides() => new[]
        {
            Guide("overview", "入门", "提供 Clip 与场景 Listener 即可播放，无需初始化、Mixer 或配置资产；从代码、背景音乐组件或按钮点击音开始。",
                new[] { typeof(AudioKit), typeof(AudioHandle), typeof(AudioSoundOptions), typeof(AudioAutoFade),
                    typeof(AudioSoundConcurrency), typeof(SoundOverflowPolicy), typeof(AudioMusicPlayer), typeof(AudioEmitter), typeof(AudioKitSettings),
                    typeof(AudioClipLibrary), typeof(AudioGroupPlayer) },
                HelpBlock.Code("代码播放三类 Clip", "using YLFramework.Audio；提供外部 Clip，无需调用 Initialize。Music 默认循环，Voice/Sound 默认单次。",
                    "AudioKit.PlayMusic(musicClip);\nAudioKit.PlayVoice(voiceClip);\nAudioKit.PlaySound(soundClip);"),
                HelpBlock.Paragraph("背景音乐组件", "添加 AudioMusicPlayer 并绑定 Clip，默认 Loop 与 playOnAwake 开启，在首次 Start 自动播放，不是每次 OnEnable。" +
                    "需要手动控制时关闭自动播放，通过 UnityEvent 调用无参 Play/Stop，组件停用只停止自有音乐。"),
                HelpBlock.Paragraph("Button 点击音", "添加 AudioEmitter，在 Clip 列表放入点击音，保持 2D、Loop 关闭；Button.onClick 拖入组件并选择 AudioEmitter.Play。" +
                    "普通 2D one-shot 不因面板停用被截断。编辑器新建/Reset 默认继承全局去重；同帧重叠可显式选择 EveryOne。"),
                HelpBlock.Code("Settings Slider", "常规 Slider 范围 [0,1]；已有合法 PlayerPrefs 优先于默认音量 1，0 静音而非停止。",
                    "musicSlider.SetValueWithoutNotify(AudioKit.Settings.MusicVolume.Value);\n" +
                    "musicSlider.onValueChanged.AddListener(value => AudioKit.Settings.MusicVolume.Value = value);"),
                HelpBlock.Notice("场景 Listener 与 UI 生命周期", "相机或玩家提供有效、启用的 AudioListener，Audio 不自动创建第二个监听器；缺少 Listener 时不能听见声音。" +
                    "需要同步其他入口的音量修改时，用 RegisterWithInitValue 与 Slider.SetValueWithoutNotify；UI 所有者负责注销订阅和事件监听。Source 状态不代表听感。"),
                HelpBlock.Paragraph("无需配置与按需启用", "常规 2D 播放无需 Mixer、配置资产、预热或并发上限。只有使用空间播放、Mixer 路由/参数/Snapshot、淡变/Crossfade、去重/并发/缓存时才配置对应高级项。" +
                    "Settings 分类开关默认 true、音量 1、临时 Mute=false；坏音量存档内存回退 1，不自动重写，代码非法赋值拒绝，合法大于 1 倍率保留。"),
                HelpBlock.Paragraph("统一初始默认值", "配置 | 初始值 | 选择理由（无业务/合法存档覆盖；组件指编辑器新建/Reset）\n" +
                    "Loop | Sound/Voice 单次、Music 循环 | 点击音与人声自然结束，BGM 持续播放\n" +
                    "Volume / Pitch / Priority | 1 / 1 / 0 | 不改素材倍率或音调，不预设抢占优势\n" +
                    "Settings 开关 / 音量 / Mute | true / 1 / false | 开箱即听，合法存档优先\n" +
                    "位置 / 空间 | 2D；At/Following 为 1/50、Linear、doppler=0 | 普通 UI 无空间配置，避免默认变调\n" +
                    "路由 | Inherit；无分类 Group 则 Direct | Mixer 可选\n" +
                    "去重模式 / Options.PlaySoundMode | EveryOne / null 继承 | 默认允许重叠，统一或单次控制策略\n" +
                    "全局 / 每 Clip 窗口 | 5 / 5 帧 | 启用去重模式后抑制短时连发，比 10 帧更少影响快速操作；按帧计数\n" +
                    "并发 | Limit=0、DefaultClipLimit=0 无限，RejectNew | 默认不拒播；显式限额后不默认抢停旧声音\n" +
                    "缓存 / 预热 | 10 / 无自动预热 | 小规模复用，不在无请求时分配音源\n" +
                    "AutoFade | 首尾 0 | 保留素材原有起止\n" +
                    "CrossFadeMusic API | EqualPower、unscaled=true、duration 必填 | 不相关 BGM 减少中点功率下陷，游戏暂停仍可过渡\n" +
                    "MusicPlayer Crossfade | off、duration=1、unscaled=true、curve=EqualPower | 直接播放优先，启用过渡时已有预设\n" +
                    "MixerController | applyOnAwake=false、transitionDuration=0.5 | 全局路由显式开启，显式 Snapshot 过渡避免默认硬切"),
                HelpBlock.Notice("新建/Reset 与旧资源", "MusicPlayer 的 crossFadeCurve 字段初始化仍为 Linear，旧缺字段资源保持 Linear；编辑器新建/Reset 为 EqualPower，Crossfade 仍关闭。" +
                    "MixerController 字段初始化仍为 applyOnAwake=true、transitionDuration=0，保留旧资产；编辑器新建/Reset 为 false/0.5。" +
                    "运行时 AddComponent 不承诺触发 Reset，已有序列化选择不自动改写。新 Mixer 预设只绑定 Config 不自动应用或清全局路由，须勾选 Apply Routing On Awake（applyOnAwake）或显式 Apply。" +
                    "EveryOne 才关闭去重；显式窗口 0 仅在 GlobalFrames/SoundFrames 下做同帧去重，0 不是关闭去重。默认 5 帧约为 60 FPS 下 83 ms、120 FPS 下 42 ms，并非固定毫秒数。显式 10 帧和显式 Linear 仍合法。"),
                HelpBlock.Code("只改需要的 Options 字段", "AudioSoundOptions 仍是 [Serializable] sealed class，公开字段名/类型与无参构造不变；构造默认值集中来自内部 AudioPlaybackOptions 值工厂。new/Defaults 为有效非循环默认值；循环音乐用 MusicDefaults，每次 Defaults/MusicDefaults 都分配新的独立可变类实例。",
                    "AudioKit.PlaySound(soundClip, new AudioSoundOptions { Volume = 0.5f });\n" +
                    "var options = AudioSoundOptions.MusicDefaults;\noptions.Pitch = 1.1f;\nAudioKit.PlayMusicWithOptions(musicClip, options);\n" +
                    "AudioKit.PlayMusic(musicClip, volume: 0.5f);"),
                HelpBlock.Paragraph("默认与优先级", "new/Defaults 的 Loop=false、Volume=1、Pitch=1；MusicDefaults 的 Loop=true。省略/null 的音乐 Options 按 MusicDefaults 解析，Sound/Voice 按 Defaults 解析。" +
                    "WithOptions 的 options 位置参数仍必填但允许 null；显式 Defaults.Loop=false 保持非循环，不自动改 true。default(AudioSoundOptions) 现在为 null。" +
                    "Volume=0 合法静音，Pitch=0 非法；路由 Inherit，无分类 Group 则 Direct。options.PlaySoundMode=null 继承全局，初始 EveryOne；单次显式路由/策略优先于分类/全局。" +
                    "新建/Reset Emitter 继承策略，旧缺字段资源保持 EveryOne；运行时 AddComponent 不保证触发 Reset。"),
                HelpBlock.Paragraph("高频配置复用", "业务可持有并复用同一个 AudioSoundOptions，避免每次 new 或读取 Defaults/MusicDefaults；每次提交捕获独立内部值快照，之后修改原对象只影响后续请求。" +
                    "省略/null 使用对应类别的内部值默认，不创建配置类。优化仅针对内部 Options 类分配，不承诺整个 Audio 零 GC；初始化、音源创建、集合扩容和 Action 回调闭包等仍可能分配。" +
                    "Unity 2022.3.62f3c1 Editor PlayMode 预热后的 4096 次配置捕获/校验/值存储测得 0 次 GC.Alloc 事件；完整路径前后数据见 Audio 文档，不是字节统计，不能替代 Player/IL2CPP 设备实测。"),
                HelpBlock.Paragraph("接入", "业务程序集引用 YLFramework.Runtime，代码使用 using YLFramework.Audio。" +
                    "Audio 与 Action 同属共享 Runtime，直接复用 YL 池、Binding 和 Events；播放调度由 Audio 模块自身管理。" +
                    "直接 Clip 由业务保活；具名素材使用显式 AudioClipAddress/provider 与租约，组配置复用同一加载链，不提供旧 Loader 或 Fluent 播放入口。" +
                    "Mixer 路由、暴露参数与 Snapshot 控制可选；不配置 Mixer 的旧调用保持原功能，不自动绑定 Settings 音量或 Ducking。"),
                HelpBlock.Notice("只读帮助", "帮助仅反射 Audio 命名空间内的公开类型，不执行示例，不播放音频，也不创建运行时宿主。" +
                    "声明及源码 XML 说明由共享 Runtime 和 HelpKit 提供，不混入 Foundation 的通用类型。")),
            Guide("control", "播放与句柄", "使用代次安全的 AudioHandle，不持有或控制池内播放器。",
                new[] { typeof(AudioKit), typeof(AudioHandle), typeof(AudioSoundOptions), typeof(AudioAutoFade),
                    typeof(AudioSoundConcurrency), typeof(SoundOverflowPolicy), typeof(AudioCrossfadeCurve) },
                HelpBlock.Code("控制", "句柄只控制本次请求；Music 全局入口控制目标曲与旧尾轨，Voice 控制当前槽位。",
                    "if (sound.IsValid) sound.Pause();\nsound.SetVolumeScale(0.5f);\nsound.SetPitch(1.2f);\nsound.Resume();\nsound.Stop();\n" +
                    "AudioKit.PauseMusic();\nAudioKit.ResumeMusic();\nAudioKit.StopMusic();\n" +
                    "AudioKit.PauseVoice();\nAudioKit.ResumeVoice();\nAudioKit.StopVoice();\nAudioKit.StopAllSound();"),
                HelpBlock.Paragraph("状态与回调", "default 或拒播返回的句柄无效；已结束、旧池租用及旧会话句柄的控制安全无效。" +
                    "IsValid 表示请求仍有效，IsPlaying 表示 Source 正在播放，静音时也可为 true，不代表可听见；IsPaused 表示手动暂停。旧 PlayMusic/PlayVoice 默认 loop 分别为 true/false。" +
                    "onStarted/onFinished 均为 Action<AudioClip, AudioHandle>，顺序为 (clip, handle)。五个 Sound 入口 PlaySound/PlaySoundAt/PlaySoundFollowing/PlayRandomSound/PlayWeightedRandomSound 在原 onFinished 之后追加 onStarted=null；其它参数名、顺序和默认值保持。" +
                    "Options 从 struct 改为 class，原 Nullable<AudioSoundOptions> 参数改为 AudioSoundOptions 引用，optional null 保持；简单 Music/Voice 与句柄方法不因类改造新增重载。" +
                    "不保留旧 Sound 包装重载或无参兼容层；固定签名反射、委托绑定、生成绑定与预编译 ABI 必须调整或重新构建，不能称所有旧签名不变。" +
                    "每请求开始/自然完成各最多一次，循环和 Pause/Resume 不重复开始通知，开关重播也不重复；Mute 不取消回调。" +
                    "Stop、替换、拒播、开关关闭、跟随目标丢失及 Shutdown 不通知自然完成。循环需明确 Stop。"),
                HelpBlock.Code("回调迁移", "旧 () => ... 改为 (clip, handle) => ...。开始回调同步执行，可能早于播放方法返回，应使用回调中的 handle。",
                    "AudioKit.PlaySound(soundClip, onFinished: (clip, handle) => Debug.Log(clip.name),\n    onStarted: (clip, handle) => handle.SetVolumeScale(0.8f));\n" +
                    "var music = AudioKit.PlayMusic(musicClip, onStarted: (clip, handle) => handle.Pause());"),
                HelpBlock.Paragraph("Sound 开始提交", "Source.Play 之后、方法返回之前同步 onStarted；Clip、Options、Pitch、Mixer 路由与自动初始 gain 已配置，SoundCount 包含自身，去重已提交。" +
                    "无效 Clip/目标、关闭、去重拒绝与容量拒绝均无开始或自然完成回调。回调内同 Clip 嵌套请求仍受已提交去重约束。" +
                    "允许 Stop、StopAllSound、改变并发、递归播放、取消 Action 或 Shutdown；回调停止自身后返回的是可能已失效的原 lease，不是复用 Voice 的新句柄。" +
                    "开始回调抛异常时原样抛出，仅释放本次仍拥有的代次并回滚本次仍拥有的去重记录；不清整个窗口、不误停同 Voice 复用的新请求或删除其记录。"),
                HelpBlock.Paragraph("完成回调与重入", "进入自然完成回调时原 handle 有效且 IsPlaying 为 false，回调返回后清理原代次。" +
                    "回调内 Stop、替换或 Shutdown 可立即使该句柄失效；不保证整个回调期间都有效，也不保证 Progress 为 1。旧代次清理不会误停重入创建的新请求。"),
                HelpBlock.Code("只读进度", "Time、Duration、Progress 都是 float，只读，不提供 seek 或完成历史。适用于 Music/Voice/Sound，包括空间播放。",
                    "float seconds = sound.Time;\nfloat duration = sound.Duration;\nfloat progress = sound.Progress;"),
                HelpBlock.Paragraph("进度边界", "Time 读取 AudioSource.time 并限制在 [0, Duration]；Duration 为 clip.length，Pitch 改变推进速度但不改变 Clip 长度；" +
                    "Progress 为 Duration > 0 时的 Clamp01(Time / Duration)，否则为 0。无效、已结束、旧代次、Shutdown 或所需 Source/Clip 缺失时三项均为 0，读取不初始化 Settings 或宿主。" +
                    "Music/Voice 关闭等待启播或重播时 Time/Progress 为 0，Duration 仍为 Clip 长度。Pause 冻结、Resume 续播，Mute 继续推进；循环只显示当前轮，可从接近 1 回到 0。" +
                    "压缩音频的 AudioSource.time 可能只有分包级精度，不承诺采样级或节拍级同步，不能要求自然完成时 Progress 为 1。"),
                HelpBlock.Paragraph("运行中参数", "SetVolumeScale 只改当前请求倍率，最终音量为 Clamp01(当前分类音量 * 倍率) * gain * transitionGain，无音乐过渡时 transitionGain=1，不改变分类设置或存档；" +
                    "倍率为 0 仅静音，允许大于 1 并保留未截断倍率。SetPitch 修改当前请求音调；两者适用于 Sound/Music/Voice，不重播、不改变暂停或代次，" +
                    "Music/Voice 开关重播使用最新参数，池复用不继承旧修改。有效句柄的倍率须非负且有限，Pitch 须有限且在 (0,3] 内，" +
                    "无效参数抛 ArgumentOutOfRangeException 且不改状态；无效句柄不校验参数、不创建宿主。"),
                HelpBlock.Code("单请求淡入淡出", "Play 返回后同帧调用；Sound/Music 也可在同步 onStarted 回调中使用传入的 handle。句柄淡变签名与默认值不变。",
                    "var sound = AudioKit.PlaySound(soundClip);\nsound.FadeIn(0.25f);\n// Later, when this request should end:\nsound.FadeOut(0.5f, stopWhenDone: true);\n" +
                    "AudioKit.PlayMusic(musicClip, onStarted: (clip, handle) => handle.FadeIn(0.5f));"),
                HelpBlock.Paragraph("淡变契约", "FadeIn(float duration, bool ignoreTimeScale=true) 与 FadeOut(float duration, bool stopWhenDone=true, bool ignoreTimeScale=true) 返回 void。" +
                    "包络 gain 独立于 Settings 与 Options.Volume/请求倍率，默认 1；无音乐过渡时 Source 为 Clamp01(category * requestScale) * gain，Crossfade 再叠乘独立 transitionGain，Mixer 路由不变。" +
                    "尚未经历任何淡变的新请求首次正时长 FadeIn 立即设置 gain=0，再从 0 向 1；经历任何淡变后（包括零时长操作），FadeIn 从当前包络向 1，不重复跳零。FadeOut 从当前包络向 0，新淡变替换旧淡变。" +
                    "有效句柄 duration 须有限且 >=0，否则抛 ArgumentOutOfRangeException 且不改状态；0 立即到目标，FadeOut 的 stopWhenDone=true 时立即 Stop。" +
                    "default/过期句柄 no-op，不验证非法值、不初始化 Settings 或宿主。"),
                HelpBlock.Paragraph("淡变时钟与清理", "AudioManager Update 推进，默认 unscaled；ignoreTimeScale=false 使用 scaled 时间。手动 Pause、Music/Voice 通道 off 或未真正启动冻结，Mute 仍推进。" +
                    "手动模式音乐/语音关开保留当前包络和剩余淡变，但 Clip 按旧语义从头重播；Sound off 直接 Stop 并取消淡变。" +
                    "Stop、自然结束、替换、target 丢失、Shutdown 与池回收清淡变。淡出 Stop 不调用自然结束 callback；短 Clip 自然结束优先，正常调用 callback。" +
                    "手动淡变仅控制单请求包络，不替代 Crossfade，也不取消其独立过渡增益；无 Tween 依赖、新宿主或新增持久化，同帧设置不承诺首采样或 DSP 精度。"),
                HelpBlock.Code("音乐交叉淡变", "CrossFadeMusic(clip, duration, options=null, onStarted=null, onFinished=null, ignoreTimeScale=true, curve=EqualPower) 返回 AudioHandle；正时长真实双轨重叠，默认 EqualPower，显式 Linear 仍可用。",
                    "AudioKit.PlayMusic(menuMusic);\nvar target = AudioKit.CrossFadeMusic(battleMusic, 1.5f);\n" +
                    "var battle = AudioKit.CrossFadeMusic(bossMusic, 2f, curve: AudioCrossfadeCurve.EqualPower);\n" +
                    "AudioKit.PauseMusic();\nAudioKit.ResumeMusic();\nAudioKit.StopMusic();"),
                HelpBlock.Paragraph("Crossfade 请求与增益", "options=null 使用与 MusicDefaults 一致的内部值默认（Loop=true）；显式 Defaults.Loop=false，尊重传入 Loop，提交捕获值快照，不修改或复制原配置类对象。支持 Volume/Pitch/OutputRoute/AutoFade，忽略空间字段与 Sound 去重，也忽略 Priority，不参与 Sound 预算。" +
                    "duration 有限且 >=0，0 立即替换；无效 Clip 返回无效句柄且不改变音乐，合法 Clip 的配置、路由、自动元数据在分配 Source 或停止旧曲前校验。" +
                    "AudioKit.Music 立即代表最新目标，不按响度选曲；旧 lease 退场前仍有效。最多两条 Music Source，第二条按需创建，不占 SoundCount/并发额度。" +
                    "u=Clamp01(elapsed/duration)；Linear 旧轨 capturedGain*(1-u)、新轨 u，EqualPower 旧轨 capturedGain*cos(u*pi/2)、新轨 sin(u*pi/2)，满增益不相关素材中点各约 0.7071；正时长新曲在 Source.Play/onStarted 前 transitionGain=0，无旧曲也从零淡入。" +
                    "curve 追加在参数末尾且默认 EqualPower；枚举零值仍为 Linear，不改变旧序列化数值。未知枚举在 duration 校验之后、任何副作用之前抛 ArgumentOutOfRangeException。" +
                    "C# 可选默认值由调用方编译时捕获，使用新缺省曲线需重新编译调用方；已有显式 Linear 不改。" +
                    "Source.volume=Clamp01(category * requestScale) * requestEnvelopeGain * transitionGain；手动/自动包络与过渡独立叠乘，不取消原包络。默认 unscaled，ignoreTimeScale=false 使用 scaled。" +
                    "等功率不保证等响度，相关/同相素材仍可能峰值；DSP 与节拍同步仍不提供。A->B->C 先停 A，B 从当前 transitionGain 退到 0，C 从 0 到 1；不排队、不保证三曲无损过渡，同 Clip 也重开。"),
                HelpBlock.Paragraph("Crossfade 控制与结束", "PlayMusic/PlayMusicWithOptions 硬替换并取消双轨过渡；StopMusic 停止两条。PauseMusic/ResumeMusic 暂停/恢复两条及剩余时钟，不重播。" +
                    "直接 lease.Pause/Resume/Stop 只控制该请求音频，但任一有效参与者暂停会冻结共同过渡时钟，另一曲音频不会自动暂停。暂停中 Crossfade 的新目标继承等待，ResumeMusic 才首次启播并通知。" +
                    "Music off 取消过渡、停止尾轨，仅保留目标并恢复 transitionGain=1；on 从头重播，已发 onStarted 不重复。off 中换曲仅替换待播目标；Mute 不冻结时钟，分类音量/默认 Mixer 同时作用两条，各自覆盖路由仍独立。" +
                    "旧 outgoing 属于替换，不通知自然完成。新目标提前自然结束只通知自身一次，旧尾轨继续剩余退场，不复活旧曲；一侧失效不冻结另一侧。" +
                    "开始回调异常仅清仍归本次过渡拥有的请求并原样抛出，重入的新过渡/普通音乐受 epoch 与 lease 隔离。"),
                HelpBlock.Code("自动首尾淡变", "AudioAutoFade 是 [Serializable] struct，公开可写 float 字段 FadeInSeconds/FadeOutSeconds；构造 (float fadeInSeconds, float fadeOutSeconds) 仅赋值，不校验。框架默认关闭：default/全零不改变旧行为。",
                    "var options = AudioSoundOptions.Defaults;\noptions.AutoFade = new AudioAutoFade(2f, 3f);\n" +
                    "AudioKit.PlaySound(soundClip, options);\nAudioKit.PlayMusicWithOptions(musicClip, options);\nAudioKit.PlayVoiceWithOptions(voiceClip, options);\n" +
                    "options.Loop = true;\noptions.AutoFade = new AudioAutoFade(1f, 0f);\n" +
                    "var music = AudioKit.PlayMusicWithOptions(musicClip, options);\nmusic.FadeOut(0.5f);"),
                HelpBlock.Paragraph("WithOptions 契约", "PlayMusicWithOptions(clip, options, onStarted=null, onFinished=null) 与 PlayVoiceWithOptions(clip, options, onStarted=null, onFinished=null) 返回 AudioHandle。" +
                    "options 为位置必填 AudioSoundOptions 引用参数，允许 null 按类别解析：MusicDefaults/Defaults；显式 Defaults.Loop=false，不同于旧 PlayMusic 默认 true。仅 2D Music/Voice，支持 Volume/Pitch/OutputRoute/AutoFade，空间字段不生效、Sound 去重策略不应用，Priority 也忽略。" +
                    "新增方法不同名，无旧方法重载；公开字段名/类型不变，new/Defaults 的 Volume/Pitch=1。构造配置不初始化 Settings 或宿主，无运行中恢复自动模式的句柄方法。"),
                HelpBlock.Paragraph("配置快照与迁移", "公开 AudioSoundOptions 仍为 [Serializable] sealed class；Defaults/MusicDefaults 每次新实例，default(AudioSoundOptions) 为 null，Nullable<AudioSoundOptions> 不再适用。本次内部值快照优化不改变公开 API。" +
                    "Runtime/Systems/Audio/Internal/AudioPlaybackOptions.cs 定义 internal struct，集中有效 Defaults/MusicDefaults、字段捕获和校验，不新增公开配置类型。公开入口只捕获一次，校验和规范化作用于局部快照，提交与请求存储按值传递，不复制配置类。内部只读传递层使用 in 借用边界局部快照，不借用 Action 模板或调用方字段，回调重入仍保持隔离。" +
                    "Music/Voice 清理不适用的去重/Priority 只作用于快照；之后修改原 Options 不影响已提交请求，句柄修改不反写原对象、其他请求或 Action 模板。嵌套配置按值复制，OutputRoute.Group 等 Unity 资源引用仍为借用，不克隆资源。" +
                    "default(AudioPlaybackOptions) 仅用于失效清理，不是有效播放默认值；请求清理时释放快照中的 Group 引用，不卸载或销毁资源。" +
                    "普通引用赋值不是值复制，依赖值相等/反射/生成绑定的调用需迁移；预编译使用方必须一起重新构建，不承诺二进制 ABI 稳定。"),
                HelpBlock.Code("部分 JSON", "Unity 2022.3 JsonUtility.FromJson 缺字段使用 default(T)，不执行无参构造或字段初始化器；缺 Pitch 得到非法 0，不能承诺部分 JSON 自动补默认值。推荐 FromJsonOverwrite 到 Defaults/MusicDefaults。",
                    "var options = AudioSoundOptions.Defaults;\n" +
                    "JsonUtility.FromJsonOverwrite(\"{\\\"Volume\\\":0.5}\", options);\nAudioKit.PlaySound(soundClip, options);"),
                HelpBlock.Paragraph("JSON 边界", "完整旧 JSON 字段格式保留；显式 Volume=0 合法静音、Pitch=0 仍非法，不按零值猜缺字段。组件独立序列化字段无需改为内嵌 Options。"),
                HelpBlock.Paragraph("自动时钟与短片段", "时长为片段秒，位置取 AudioSource.timeSamples / (double)clip.frequency，L=clip.samples / (double)clip.frequency；不改变 AudioHandle.Time/Duration/Progress。" +
                    "Pitch=2 时实际秒数减半，运行中修改 Pitch 不重新计时；Time.timeScale 不参与，无 ignoreTimeScale 参数。" +
                    "非循环 x+y>L 时用 double 加法与同比缩短：scale=L/(x+y)，a=x*scale、b=y*scale，gain=Min(Clamp01(t/a),Clamp01((L-t)/b))，零时长一侧取 1。" +
                    "两段不重叠，理论交接点 gain=1；极小正值不以阈值当零。字段须有限且非负；非零配置要求有效 samples/frequency 与有限 >0 的长度，分配宿主、去重登记、替换槽位前校验。" +
                    "有淡入在 Source.Play 和 onStarted 前 gain=0，纯淡出初始 gain=1；未启播不消耗。Update 帧级，不保证首采样、最后采样为零或过短包络被完整观察，压缩/Streaming 与目标平台需实测。"),
                HelpBlock.Paragraph("循环首轮与重播", "Loop && FadeOutSeconds>0 拒绝，不静默忽略。循环只首次启动淡入 min(FadeInSeconds,L)，到达窗口或检测首轮 PCM 回绕后 loop latch 锁存 gain=1，后续不重复淡入。" +
                    "Update 间隔超过整轮时，位置不能可靠识别漏过的循环，短循环/低帧率有此限制，不引入 DSP 调度。Music/Voice 关开从头重播，只有自动模式仍 active 才重置包络与 loop latch；Pause/Resume 不重置，Mute 不冻结。"),
                HelpBlock.Paragraph("自动完成与手动接管", "自动不 Stop、不改 loop、不缩短片段；自然完成按 t=L 写最终包络，尾淡出>0 时 gain=0，再通知原 Clip/原 lease 最多一次，原句柄在回调进入时仍有效。" +
                    "不得把 Source 已重置的位置 0 当淡入起点；重入清理仍保护新代次。合法 FadeIn/FadeOut 从 current gain 接管并取消自动控制，正在播放先采样位置，暂停/off 保持最后 gain，非法 duration 不取消自动模式。" +
                    "取消后关开不复活，Options.AutoFade 非零不代表运行中仍受自动控制；0 保持手动即时语义，stopWhenDone=false 静音继续，自动尾部不再接管。" +
                    "手动/自动共用单包络而非彼此叠乘，但与音乐 Crossfade 的 transitionGain 独立叠乘；SetVolumeScale、分类音量、Pitch、Mixer 路由不换控制者。Stop/自然结束/替换/target 丢失/Shutdown/池回收清自动状态，旧 lease 安全 no-op。" +
                    "不新增 host、Timer、Mixer 参数、Prefs 或曲线资产。旧组件自动淡变数据无需迁移；Options 类与 JSON 迁移限制见配置快照说明，预编译使用方必须一起重新构建，不承诺二进制 ABI 稳定。"),
                HelpBlock.Paragraph("所有权", "Audio 不卸载或销毁 Clip。调用方必须保证播放、暂停以及等待开关重播时 Clip 有效；" +
                    "停止、替换或 Shutdown 后断开引用。普通 UI 只停止自己拥有的句柄，不关闭其他对象共享的 AudioKit。")),
            Guide("settings", "Settings、去重与并发", "保留六个存档键；Sound 总量/每 Clip 上限、优先级与闲置缓存预算独立于帧窗口去重。",
                new[] { typeof(AudioKitSettings), typeof(AudioKit), typeof(AudioSoundOptions), typeof(AudioSoundConcurrency), typeof(SoundOverflowPolicy), typeof(AudioSoundStats) },
                HelpBlock.Code("设置", "六个持久化属性复用 YLFramework.Binding，组合 IsOn 是直接读写的 bool；三个临时 Mute 使用 BindableProperty<bool>.Value。下方 10 帧是显式业务冷却，不是初始默认值。",
                    "AudioKit.Settings.IsOn = true;\nAudioKit.Settings.IsMusicOn.Value = true;\n" +
                    "AudioKit.Settings.SoundVolume.Value = 0.8f;\nAudioKit.Settings.IsMusicMuted.Value = true;\n" +
                    "AudioKit.Settings.IsVoiceMuted.Value = false;\nAudioKit.Settings.IsSoundMuted.Value = true;\n" +
                    "AudioKit.PlaySoundMode = AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames;\n" +
                    "AudioKit.SoundFrameCountForIgnoreSameSound = 10;"),
                HelpBlock.Paragraph("临时静音", "IsMusicMuted/IsVoiceMuted/IsSoundMuted 默认 false，仅影响各自分类 Source，不增加全局或单句柄 Mute。" +
                    "Mute 独立于 IsOn 和 Volume，不改分类音量或请求倍率，不停播、不回退位置；静音中正常推进、跟随、自然结束及通知，仍遵循开关与去重规则。" +
                    "解除静音从当前位置继续，使用最新 Clamp01(分类音量 * 请求倍率) * gain * transitionGain（无过渡为 1）。暂停时改 Mute 不改变暂停状态或代次；新请求和通道重播在启播前应用当前 Mute。"),
                HelpBlock.Paragraph("开关联动", "Music/Voice 关闭时停止 Source 但保留有效请求，开启从头重播原 Clip、loop、最新倍率/Pitch 与尚未触发的回调；" +
                    "关闭期间新请求替换待播放项。显式 Stop 或自然结束清空请求，不能被开关复活；手动 Pause 不被开启强制恢复。" +
                    "Sound 关闭停止所有音效并拒绝新请求，不恢复旧声音。Music Crossfade 关闭时先取消过渡、停止旧尾轨，仅保留最新目标并恢复 transitionGain=1。"),
                HelpBlock.Paragraph("设置生命周期", "默认开关 true、音量 1，原六个 KEY_AUDIO_MANAGER_* 键不变。RegisterWithInitValue/ Register " +
                    "返回 YL Events 注销令牌，由 UI 所有者注销。三个 Mute 为运行时 BindableProperty<bool>，不增加 PlayerPrefs 键或主动 Save；" +
                    "MusicOutputGroup/VoiceOutputGroup/SoundOutputGroup 是默认 null 的 BindableProperty<AudioMixerGroup>，通过 .Value 修改，不写 PlayerPrefs；" +
                    "Shutdown 清掉旧 Settings 的订阅和输出组，重建后 Mute 恢复 false、路由恢复 null，旧 Settings 修改不影响新宿主；不提供 Settings.Init/Deinit/Initialized。"),
                HelpBlock.Paragraph("存档与赋值校验", "分类音量公开属性仍为 PlayerPrefsFloatProperty，Audio 局部校验，不改 Binding 全局规则。已有合法存档优先，包括 0 和大于 1 的倍率。" +
                    "Load/Reload 遇到负数、NaN/Infinity 在内存回退 1，不自动重写坏存档；Delete 恢复默认且不写回。" +
                    "Value、TrySetValue、SetValueWithoutCallback 的非法赋值均抛 ArgumentOutOfRangeException，内存、存档、通知和播放状态不变。" +
                    "合法 Value 变化先持久化后通知；SetValueWithoutCallback 只改内存，不通知或持久化。"),
                HelpBlock.Paragraph("窗口", "全局默认 EveryOne，不去重，允许重叠但不绕过并发上限；GlobalFrames 共享窗口起点，SoundFrames 每个 Clip 独立。两个窗口默认 5 帧，属于运行时配置，仅在对应模式下生效；显式 0 仅同帧去重，0 不是关闭去重；EveryOne 才关闭去重。边界包含最后一帧。" +
                    "显式 10 帧冷却仍有效。" +
                    "仅成功接纳的播放登记，先提交后 onStarted；独立记录自然结束按播放代次移除，较早结束不能删除新记录。Stop 不清窗口，开始异常仅回滚自己的记录。单次通过 options.PlaySoundMode 覆盖。"),
                HelpBlock.Code("Sound 并发", "AudioSoundConcurrency 是 [Serializable] struct，public int Limit、public int DefaultClipLimit 与 public SoundOverflowPolicy OverflowPolicy；RejectNew=0、StopOldest=1、StopLowestPriority=2，default 为 0/unlimited + RejectNew，两项限额默认 0。",
                    "AudioKit.SoundConcurrency = new AudioSoundConcurrency\n{\n    Limit = 24,\n    DefaultClipLimit = 3,\n    OverflowPolicy = SoundOverflowPolicy.StopLowestPriority\n};\n" +
                    "AudioKit.SetClipSoundLimit(explosionClip, 2);\nAudioKit.SetClipSoundLimit(uiClip, 0);\n" +
                    "var options = AudioSoundOptions.Defaults;\noptions.Priority = 10;\nAudioKit.PlaySound(importantClip, options);"),
                HelpBlock.Paragraph("并发配置与容量", "SoundConcurrency 是 AudioKit 上运行时整体赋值属性，返回配置副本，完整校验后初始化/应用；负 Limit、负 DefaultClipLimit 或未知策略抛参数异常，原配置/请求不变，不创建宿主、Source 或 Listener。" +
                    "无新 Settings/PlayerPrefs/资源项；Shutdown/新会话恢复默认。只限制有效 Sound 请求，暂停、Mute 与 Loop 都计数，不包含 Music/Voice，不是 Unity 真实/虚拟 voice 或全场景 AudioSource 上限。" +
                    "输入/目标/路由/包络校验、开关、去重检查后才判容量。RejectNew 满额返回无效句柄，无 Source 分配、无去重提交、无回调且不停止旧请求。" +
                    "StopOldest 停最早接纳的有效请求，再接纳新请求；同帧按接纳序号而非播放位置/HashSet 排序，抢占不通知自然完成。已原生结束但待 Update 清理的有效请求仍占额度，不为腾额度提前触发回调。" +
                    "所有 Sound 入口、Emitter 与 Action 共用闸门；等权/加权只抽选一次，容量拒绝不重抽。自然结束、Stop、Sound off、目标失效、回收与 Shutdown 均释放额度。"),
                HelpBlock.Paragraph("优先级与联合接纳", "AudioSoundOptions.Priority 为非负 int，Defaults.Priority=0，数值越大越重要，支持 int.MaxValue；Sound 捕获接纳时的 Priority，之后音量/Pitch/暂停/Mute 不改变优先级。Music/Voice/Crossfade 忽略它。" +
                    "这是 YL 请求优先级，不映射到方向不同的 Unity AudioSource.priority。RejectNew/StopOldest 不增加优先级保护。" +
                    "StopLowestPriority 在候选范围选最低 Priority、同级最旧；新请求低于候选则拒播，同级或更高可替换。仅全局满时范围为所有 Sound；Clip 满时仅同一 Clip，即使总量也满也最多停止一条，不误停其它 Clip。" +
                    "EveryOne 只跳过去重，仍受两级限额；去重先判，任何 Priority 都不绕过帧窗口，容量拒播不提交或延长窗口。"),
                HelpBlock.Paragraph("Clip 覆盖与裁剪", "DefaultClipLimit 是无覆盖 Clip 的默认上限；SetClipSoundLimit(clip, limit) 按 AudioClip 实例身份覆盖，显式 0 仅绕过默认 Clip 限额，不绕过总量。" +
                    "RemoveClipSoundLimit(clip) 返回 bool 并恢复默认，ClearClipSoundLimits() 清覆盖；GetEffectiveClipSoundLimit(clip) 与 GetSoundCount(clip) 被动返回 int，不初始化 Settings/宿主，无效 Clip 返回 0。" +
                    "Set 要求 live Clip 和非负 limit，完整校验后登记，不创建宿主；同名不同实例独立，重复候选、2D/3D、Mixer 和调用者共享实际 Clip 计数。注册借用 Clip，释放前移除覆盖，Shutdown 清规则。" +
                    "降低任一有效上限立即裁剪，先各 Clip 再全局，避免多停；移除覆盖恢复更严格默认时也裁剪。RejectNew 保留较早请求、停止最新；StopOldest 保留较新请求、停止最旧；StopLowestPriority 保留高优先级、同级保较新。提高或撤销有效限额不停止请求。"),
                HelpBlock.Code("独立缓存与真实预热", "SoundCacheCapacity 默认 10，0 不缓存，负值拒绝。配置不创建宿主、不预热、不停止活跃请求；缩小只释放超额闲置源，增大不自动创建。",
                    "AudioKit.SoundCacheCapacity = 24;\nint added = AudioKit.PrewarmSounds(24); // Target idle count, returns newly cached count."),
                HelpBlock.Paragraph("预热边界", "PrewarmSounds(int cachedCount) 返回新增缓存数，不是追加请求或包含 active 的总数；重复调用仅补足目标。0 无操作且不创建宿主，负数或超过缓存预算在副作用前拒绝。" +
                    "正预热可创建宿主和真实 inactive AudioSource，但不创建 Listener、不赋 Clip、不播放、不回调、不占并发、不提交去重、不消费 Random；Sound off 也能预热。" +
                    "active+idle 可超过全局活跃上限，这是独立资源预算；不自动随 Limit 扩容。")),
            Guide("spatial", "随机与空间播放", "保留均匀随机，另提供轻量加权选择；音效与语音支持真正的 3D Source。",
                new[] { typeof(AudioKit), typeof(AudioSoundOptions), typeof(AudioHandle), typeof(AudioWeightedClip), typeof(AudioAutoFade) },
                HelpBlock.Code("同一组参数", "new AudioSoundOptions 或 Defaults 都有有效默认值；default(AudioSoundOptions) 为 null，Sound/Voice 按 Defaults 解析。",
                    "var options = AudioSoundOptions.Defaults;\noptions.Volume = 0.7f;\noptions.Pitch = 1.1f;\n" +
                    "options.PlaySoundMode = AudioKit.PlaySoundModes.EveryOne;\n" +
                    "AudioKit.PlayRandomSound(clips, options, onFinished: (selectedClip, handle) => Debug.Log(selectedClip.name));\n" +
                    "var fixedSound = AudioKit.PlaySoundAt(clip, position, options);\n" +
                    "var following = AudioKit.PlaySoundFollowing(clip, target, options);\n" +
                    "var characterVoice = AudioKit.PlayVoiceFollowing(voiceClip, character, options);"),
                HelpBlock.Paragraph("随机", "忽略 null/已销毁 Clip，空列表或全无效返回无效句柄；随机选中后按该 Clip 去重，不为整组共用冷却。" +
                    "回调中的 clip 是实际选中并播放的对象，handle 对应该次原播放代次，不以名字或列表首项推测结果。" +
                    "options.AutoFade 在 Sound、At/Following Sound 与 Voice、等权/加权随机透传；只选择一次，按实际选中 Clip 长度归一化，拒播不重抽。" +
                    "options.Priority 仅对 Sound 透传，等权/加权与空间 Sound 共用实际 Clip 上限；Music/Voice 忽略 Priority。"),
                HelpBlock.Code("加权随机", "AudioWeightedClip 是 [Serializable] struct，公开字段 Clip/Weight；构造 (clip, weight=1f)。纯选择不初始化 Settings 或宿主，可接 3D/Voice。",
                    "var weighted = new[] { new AudioWeightedClip(commonClip, 3f), new AudioWeightedClip(rareClip, 1f) };\n" +
                    "AudioKit.PlayWeightedRandomSound(weighted, options, onFinished: (clip, handle) => Debug.Log(clip.name));\n" +
                    "if (AudioKit.TrySelectWeightedRandomClip(weighted, out var selected))\n    AudioKit.PlayVoiceFollowing(selected, character, options);"),
                HelpBlock.Paragraph("加权边界", "PlayWeightedRandomSound(IReadOnlyList<AudioWeightedClip> clips, AudioSoundOptions options=null, Action<AudioClip, AudioHandle> onFinished=null, Action<AudioClip, AudioHandle> onStarted=null) 返回 AudioHandle；" +
                    "TrySelectWeightedRandomClip(IReadOnlyList<AudioWeightedClip> clips, out AudioClip clip) 返回 bool，仅选择。" +
                    "所有项 Weight 必须 finite 且 >=0，包括无效 Clip/零参与项，否则抛 ArgumentOutOfRangeException；只有有效 Clip 且 Weight>0 参与。" +
                    "null/空/all0 或无有效候选无请求，纯选择返回 false、out clip=null。double 累计、一次抽选；选中后去重拒播不重抽，不防重复。" +
                    "旧 PlayRandomSound/clips 均匀随机模式完全不变，不增加 Settings 或 Options 默认项。"),
                HelpBlock.Paragraph("空间参数", "At/Following 在启播前设置世界位置和 spatialBlend=1，默认距离 1/50、Linear 衰减、dopplerLevel=0。" +
                    "PlaySound/PlayVoice 默认 2D，Music 仍只公开 2D 入口。Pitch 为 (0,3]，Volume 非负且有限；空间距离有限且满足 0<=MinDistance<=MaxDistance、MaxDistance>0，" +
                    "只支持 Linear/Logarithmic，不支持 Custom。无效参数抛 ArgumentOutOfRangeException，分配和去重登记前检查。"),
                HelpBlock.Paragraph("空间语音", "PlayVoiceAt 与 PlayVoiceFollowing 复用 AudioSoundOptions 和同一播放内核，默认不循环；" +
                    "仍为单个独占 Voice 槽位，新请求替换旧请求，不新增多 NPC 并发。由 Voice 音量/开关/IsVoiceMuted 控制，PlaySoundMode 忽略，" +
                    "不受 Sound 开关/静音、去重或 StopAllSound 影响，不进入音效统计；进度和上下文回调沿用 Voice 契约。关闭时保留空间参数，重播前校验目标并更新到最新位置；" +
                    "目标失效清空请求且不通知自然完成。替换 2D Voice 重置空间状态，非法参数/无效目标不替换当前请求。"),
                HelpBlock.Notice("目标与 Listener", "跟随 Source 不挂到业务 Transform 上；目标移动由宿主更新，销毁、停用或场景卸载时停止且不通知自然完成。" +
                    "固定位置 one-shot 可在发起对象销毁后播完。Audio 不自动创建 Listener，由相机或玩家配置；3D 不代表 HRTF、遮挡或多 Listener。")),
            Guide("mixer", "可选 Unity Mixer", "分类默认、单次覆盖或 Direct；显式参数与 Snapshot 控制不创建宿主。",
                new[] { typeof(AudioOutputRoute), typeof(AudioOutputRoute.RouteMode), typeof(AudioMixerConfig),
                    typeof(AudioMixerControl), typeof(AudioMixerController), typeof(AudioKitSettings), typeof(AudioKit) },
                HelpBlock.Code("分类默认与单次路由", "AudioOutputRoute 为 struct，字段 Mode/Group，嵌套 RouteMode 为 Inherit/MixerGroup/Direct；default 等于 Inherit。",
                    "AudioKit.Settings.MusicOutputGroup.Value = musicGroup;\n" +
                    "AudioKit.Settings.VoiceOutputGroup.Value = voiceGroup;\n" +
                    "AudioKit.Settings.SoundOutputGroup.Value = soundGroup;\n" +
                    "AudioKit.PlayMusic(musicClip);\n" +
                    "AudioKit.PlayVoice(voiceClip, outputRoute: AudioOutputRoute.Direct);\n" +
                    "var options = AudioSoundOptions.Defaults;\noptions.OutputRoute = AudioOutputRoute.ToMixer(uiGroup);\n" +
                    "AudioKit.PlaySound(soundClip, options);"),
                HelpBlock.Paragraph("路由边界", "PlayMusic/PlayVoice 的末尾追加可选 outputRoute=default；Sound、随机、固定位置/跟随 Sound 和空间 Voice 通过 options.OutputRoute 透传。" +
                    "三个分类默认组通过 BindableProperty<AudioMixerGroup>.Value 设置，默认 null，表示直接输出，不写 PlayerPrefs。" +
                    "默认组变化即刻更新对应 Inherit 项，包括暂停、Mute、等待开关重播的请求与 Crossfade 两条 Music；不重播、不改变进度、代次、音调、音量或回调。" +
                    "MixerGroup 覆盖与 Direct 不受影响；覆盖可以使用另一个 Mixer 的组。未知 Mode、空/已销毁的显式 Group、Inherit/Direct 携带非空 Group 均在替换、分配和去重登记前拒绝。池复用清空旧路由。"),
                HelpBlock.Code("配置资产", "菜单 Assets/Create/YLFramework/Audio/Mixer Config 创建 AudioMixerConfig，Inspector 配置 Mixer/MusicGroup/VoiceGroup/SoundGroup；公开只读属性。",
                    "AudioKit.ApplyMixerConfig(config);\nAudioKit.ClearMixerRouting();"),
                HelpBlock.Paragraph("配置与资源所有权", "分类组可留空；非空组必须属于 config.Mixer，全部留空表示直接输出。Apply 完整校验后复制默认组，只初始化 Settings，不创建 AudioManager/Source/Listener。" +
                    "资产运行中修改需重新 Apply；Clear 未初始化时安全无操作。Clip、config、Mixer、Group 和 Snapshot 均由调用方拥有，Audio 不加载、卸载或销毁资产。" +
                    "释放前显式清路由并停止仍覆盖该资产的请求；初始无效引用不静默回退，播放期间外部卸载不自动重载或保证效果。"),
                HelpBlock.Code("参数与音量", "所有入口仅支持 Unity 主线程；TrySetFloat/TrySetVolume 应在 Start 或之后调用，不在 Awake/OnEnable/AfterSceneLoad 初始化回调中调用。",
                    "bool set = AudioMixerControl.TrySetVolume(mixer, \"MusicVolume\", 0.5f);\n" +
                    "bool read = AudioMixerControl.TryGetVolume(mixer, \"MusicVolume\", out float volume);\n" +
                    "bool gain = AudioMixerControl.TrySetFloat(mixer, \"Gain\", 3f);\n" +
                    "bool raw = AudioMixerControl.TryGetFloat(mixer, \"Gain\", out float value);\n" +
                    "bool released = AudioMixerControl.TryClearFloat(mixer, \"MusicVolume\");\n" +
                    "float db = AudioMixerControl.LinearVolumeToDecibels(0.5f);\n" +
                    "float linear = AudioMixerControl.DecibelsToLinearVolume(db);"),
                HelpBlock.Paragraph("参数控制边界", "合法输入保留原生 SetFloat/GetFloat/ClearFloat 的 false；TryGet 失败时 out 为 0，不是参数的真实值。空/已销毁对象、空白名称、非法数值或数组抛 ArgumentException 或派生异常。" +
                    "音量输入为有限 [0,1]，0 -> -80 dB，正值 Max(-80,20*Log10(v))，1 -> 0 dB；反向 Clamp01(Pow(10,db/20))，<= -80 按 0 处理。-80 是 UI 下限，不保证绝对数字静音；正增益/效果参数用原始 Float API。" +
                    "Settings 音量与单请求包络只写 Source 的 Clamp01(分类音量*请求倍率) * gain * transitionGain（无过渡为 1），不自动绑定 Mixer，两层衰减相乘。SetFloat/SetVolume 接管暴露参数，TryClearFloat 交还原生控制，不清路由；不要让手动音量和 Snapshot 同时争用同一参数。"),
                HelpBlock.Code("Snapshot", "效果链、Send/Receive 和 Ducking 在 Unity Mixer 编辑器配置，无自动 Ducking 或状态机；句柄淡变不调度 Mixer 参数。",
                    "AudioMixerControl.TransitionToSnapshot(snapshot, 0.25f);\n" +
                    "AudioMixerControl.TransitionToSnapshots(mixer, snapshots, weights, 0.5f);"),
                HelpBlock.Paragraph("快照校验", "duration 有限且 >= 0；混合数组非空且等长，每个 Snapshot 存活并属于传入 Mixer，权重有限、非负且总和 > 0。" +
                    "校验后直接传给 Unity，不修改调用方数组，不额外归一化或承诺插值曲线。"),
                HelpBlock.Paragraph("UnityEvent 与全局生命周期", "AudioMixerController 引用 config，编辑器新建/Reset 设置 applyOnAwake=false、transitionDuration=0.5；applyOnAwake 字段初始值 true，transitionDuration 字段初始值 0，以兼容旧资产，运行时 AddComponent 不承诺 Reset。" +
                    "新预设只绑定 Config 不自动应用或清全局路由，须显式勾选 Apply Routing On Awake；开启且 config 有效时 Awake 仅 Apply 路由，不自动写初始参数或切 Snapshot。" +
                    "未配置 config 时 Awake 安静跳过，Inspector 提示；开启应用后已配置但非法仍报错。显式 Apply、参数/Snapshot 调用缺配置仍明确报错。" +
                    "仅做参数/Snapshot 控制时保持自动 Apply 关闭；显式完整 Apply 中 Group=null 清为 Direct，不保留先前路由。transitionDuration=0.5 只供显式 Snapshot 调用，不代表自动切换或 Mixer 参数淡变。" +
                    "无参 Apply/ClearRouting/ClearParameter/TransitionToSnapshot/TransitionToSnapshots，以及 SetParameter(float)/SetVolume(float) 供 UnityEvent 绑定；参数名、快照和混合数组来自 Inspector，使用 config.Mixer。" +
                    "SetParameter/SetVolume 的事件须在 Start 或之后触发，错误配置或原生 false 明确报告组件上下文。禁用/销毁不清全局配置，多组件由最后一次 Apply 决定。" +
                    "ClearMixerRouting/ClearRouting 不 ClearFloat、不恢复 Snapshot、不停止单次覆盖；Shutdown 仍停止全部自有请求并清理路由，但不 ClearFloat 或恢复 Snapshot。共享 Mixer 状态由调用方显式管理。")),
            Guide("components", "Inspector 与 UnityEvent", "播放组件管理自己的句柄，Mixer 配置使用显式全局生命周期。",
                new[] { typeof(AudioEmitter), typeof(AudioMusicPlayer), typeof(AudioMixerController), typeof(AudioOutputRoute), typeof(AudioWeightedClip), typeof(AudioAutoFade),
                    typeof(AudioSoundConcurrency), typeof(SoundOverflowPolicy), typeof(AudioCrossfadeCurve), typeof(AudioClipLibrary), typeof(AudioClipGroup),
                    typeof(AudioClipEntry), typeof(AudioClipSource), typeof(AudioClipSelectionMode), typeof(AudioGroupPlayer), typeof(AudioGroupAutoPlay) },
                HelpBlock.Paragraph("AudioEmitter", "在 Inspector 配置 Clip 列表、2D/固定位置/跟随模式和音效参数，UnityEvent 绑定无参 Play/Stop。" +
                    "useGlobalPlaySoundMode 字段缺省 false，旧缺字段资源保持原 playSoundMode=EveryOne；编辑器新建/Reset 设置 true，通过 options.PlaySoundMode=null 继承全局。运行时 AddComponent 不保证触发 Reset。" +
                    "私有序列化 useWeightedRandom 默认 false，旧 clips 仍为均匀模式；开启后使用配对的 weightedClips 列表（每项 Clip/Weight），三种位置模式都支持加权。" +
                    "加权只选择一次，去重拒播不重抽、不防重复；不开启时不改变旧序列化配置或播放参数。" +
                    "OutputRoute 默认 Inherit，可在 Inspector 指定 MixerGroup 或 Direct。EveryOne 允许 2D 点击音同帧重叠；禁用面板不截断已发起的普通 one-shot。有效循环重播替换旧循环，禁用停止循环和所有自有跟随声音。" +
                    "选曲一次，空候选/全无效/全零候选或无效跟随目标不切断旧循环；完整 Options、位置/目标及 Clip 校验在 Stop 前完成，非法配置明确抛错，拒播不重抽。" +
                    "private serialized autoFade 默认全零，等权/加权与三种位置模式全部透传，按实际选中 Clip 归一化；非法新配置或所选 Clip 元数据在 Stop 旧循环前拒绝。" +
                    "private serialized priority 默认 0，Inspector 非负，透传 options.Priority；运行时负值在 Stop 旧循环前拒绝，旧字段/GUID 不改，无需重存旧数据。" +
                    "private serialized UnityEvent onStarted 默认空事件，public 只读 Started 提供无参事件入口；开始包装先登记 lease 与 StopOnDisable 快照再 Invoke，事件可 Stop/禁用/递归 Play。" +
                    "返回路径不重新登记失效旧 lease，异常仅清自己的过期记录；没有回调的拒播也不登记。"),
                HelpBlock.Paragraph("AudioMusicPlayer", "配置 Clip、loop、playOnAwake；UnityEvent 使用 Play/Stop。组件只停止自己持有的句柄，" +
                    "空 Clip 或已销毁 Clip 不停止旧音乐；新配置先验证，Crossfade 开启时 duration/curve 也在替换前校验。" +
                    "OutputRoute 默认 Inherit，透传到音乐请求；旧组件退出不能停止由另一请求替换后的音乐。不自建 Source、加载资源或实现另一套池。" +
                    "private serialized autoFade 默认全零，Crossfade 关闭时使用与 PlayMusicWithOptions 相同的内部值提交链，autoFade 全零保持原无包络行为；非法新配置在 Stop 自有旧句柄前拒绝。" +
                    "private serialized crossFadeOnPlay 默认 false、crossFadeDuration 默认 1、crossFadeIgnoreTimeScale 默认 true；crossFadeCurve 字段初始值 Linear，兼容旧缺字段资源，编辑器新建/Reset 设置 EqualPower，Crossfade 仍关闭。运行时 AddComponent 不承诺 Reset；关闭时不校验无关 Crossfade 参数。" +
                    "开启走 CrossFadeMusic 对应的内部值提交链并显式传入组件曲线，不先 Stop 旧曲；所有新配置先验证，组件跟踪自己仍有效的请求。Stop/OnDisable 只停自有目标及旧尾轨，不误停其它所有者的新曲。" +
                    "旧场景所有者退出可截断自己的旧尾轨；跨场景完整过渡应由长期存活的音乐所有者发起，不忽略 OnDisable 所有权。"),
                HelpBlock.Paragraph("组件值配置", "AudioEmitter/AudioMusicPlayer 从现有序列化字段直接构造内部 AudioPlaybackOptions，按值提交，不在每次 Play 创建公开配置类，也不缓存共享可变 Options。" +
                    "完整校验、一次选曲、无效输入保护旧播放、跟随目标校验、组件所有权和 Started 回调跟踪保持不变；原字段/GUID 不改。"),
                HelpBlock.Paragraph("Inspector 基础与高级", "基础区为 Clip、Volume、Loop、首次自动播放和 Emitter 位置模式；playOnAwake 在首次 Start 执行，不是每次 OnEnable。Advanced 默认折叠，但显示已配置/非法高级字段提示。" +
                    "Pitch/Priority、去重、路由、自动淡变和事件按需展开，空间参数仅非 2D 显示，Group 仅 MixerGroup 显示，隐藏字段不静默清空。" +
                    "加权模式切换不擦除另一列表，编辑器新增项 Weight=1，旧 Weight=0 继续表示排除。autoFade Tooltip 说明片段秒、短片段同比缩短、循环不可尾淡出；两个组件原字段/GUID 不改，旧数据无需迁移或重存。"),
                HelpBlock.Notice("输入保护边界", "空/非法输入先校验，不切断旧自有音频；此保护不保证容量拒播、通道关闭、原生启动失败或回调异常时回滚旧声音。"),
                HelpBlock.Paragraph("AudioClipLibrary 素材组", "菜单 Assets/Create/YLFramework/Audio/Clip Library 创建共享资产；Groups 的 Name 精确区分大小写，不是控制通道名。" +
                    "每组 Selection 为 Uniform/Weighted，Entries 每项 Source/Clip/ProjectId/Key/Weight；Uniform 忽略 Weight，Weighted 的 0 排除，所有加权值必须有限且非负。缺组、重名、非法枚举/权重或无候选返回失败，不停止已有播放。" +
                    "Clip 为借用；Resource 使用正式 ProjectId 与 Key 逻辑地址，物理来源由已初始化的 Resource Catalog 决定。应用先 InitializeAsync，再通过 GetAudioClipProvider 装配领域地址；Library 只读取已初始化的 Current，不隐式初始化。旧 Resources/AssetBundle 配置保留序列化值但须显式迁移，Inspector 按逐条映射创建新 GUID 副本，不猜 BundleName、不覆盖原配置。ExternalProvider 必须代码 SetProvider，不自动注册后端。" +
                    "Inspector 使用 SerializedObject，多对象追加分别保留各自列表长度，新条目为 Clip/null/空 ProjectId/空 Key/空 BundleName/Weight=1；Undo 保留旧零权重，不随机抽选、不加载、不静默修复配置。"),
                HelpBlock.Paragraph("AudioGroupPlayer 组播放所有者", "绑定 library/groupName，再显式配置唯一 channelName；素材组与控制通道是两种身份。category 选择 Music/Voice/Sound 设置，channelMode 为 Exclusive/Overlap；空 persistenceId 不持久化，稳定 ID 只允许 ASCII 字母/数字及 . _ - /，多个实例不得共用通道名或持久化 ID。" +
                    "autoPlay 默认 Manual，FirstStart 在首次 Start 播放，EveryEnable 在 Start 及后续启用时播放；UnityEvent 可绑定无参 Play/Stop 或 PlayGroup(string)。停用停止所有自有播放与 pending，销毁释放通道，不控制其他组件。" +
                    "框架自有 Source 为 2D，借源保留 caller 空间曲线/滤镜，options 不配置空间距离/衰减。options 提供 Volume/Pitch/Loop/OutputRoute/AutoFade/Priority/AutoPauseWhenTimeScaleZero；Music 不自动启用 Loop，Music/Voice 忽略 Sound Priority。nullable PlaySoundMode 不被 Unity 序列化，Inspector Sound 继承全局策略。" +
                    "borrowedSource 默认 null，使用框架自有 Source；可显式引用调用方 AudioSource，直接 Clip 与具名素材都沿用受控借源入口。借源只支持 Exclusive，Source 必须活跃、启用、已停止且未被占用；重播同一借源前也须明确 Stop 让渡，不自动抢占或恢复旧播放历史。" +
                    "具名借源使用 PlayBorrowedNamed/PlayBorrowedAsync，加载中不占 Source，提交时再次校验；Inspector 只检查可观察的活跃/播放状态，不初始化宿主或查询所有权表，暂停或已有借用仍以运行时校验为准。" +
                    "loadAsync 默认 true，仅控制具名素材；每次只抽选一次，选中定位沿用正式 provider/租约，失败不重抽。onStarted 在句柄归入本通道后触发，Started 可 Stop/失活/销毁；没有自然完成 UnityEvent 或自动 Crossfade。" +
                    "通道配置在首次登记捕获，运行中修改 Inspector 不重建已有通道。Inspector 支持多对象、Undo 与 Prefab Override；诊断不写 Prefs、不创建 Settings 或播放宿主。"),
                HelpBlock.Paragraph("AudioMixerController", "配置与 UnityEvent 入口详见 Mixer 页；编辑器新建/Reset 的 applyOnAwake=false、transitionDuration=0.5，字段初始化 true/0 保留旧资产。运行时 AddComponent 不承诺 Reset。" +
                    "新预设只绑定 Config 不自动改全局路由，勾选 Apply Routing On Awake 才自动应用；未配置 config 的 Awake 安静跳过，显式 Apply 缺配置仍报错。仅参数/Snapshot 用途保持自动 Apply 关闭，Group=null 的显式完整 Apply 会清成 Direct。" +
                    "禁用/销毁不恢复旧配置，不采用播放组件的句柄所有权规则。")),
            Guide("action", "ActionKit 音效动作", "Clip-only 动作桥接，等待请求结束，不保证自然播放成功。",
                new[] { typeof(PlaySoundAction), typeof(PlaySoundExtension), typeof(AudioSoundOptions), typeof(AudioAutoFade) },
                HelpBlock.Code("组合", "使用 YLFramework.Actions 与 YLFramework.Audio。",
                    "ActionKit.Sequence()\n    .PlaySound(soundClip, outputRoute: AudioOutputRoute.ToMixer(uiGroup))\n    .Callback(() => Debug.Log(\"音效等待已结束\"))\n    .Start(host);"),
                HelpBlock.Code("完整请求配置", "AllocateWithOptions(clip, options, onFinish=null, onStarted=null) 与 sequence.PlaySoundWithOptions(clip, options, onStarted=null)，options 必填，不新增旧方法重载或 Fluent 层。",
                    "var options = AudioSoundOptions.Defaults;\noptions.Priority = 10;\noptions.AutoFade = new AudioAutoFade(0.25f, 0.5f);\n" +
                    "var action = PlaySoundAction.AllocateWithOptions(soundClip, options);\n" +
                    "ActionKit.Sequence().PlaySoundWithOptions(soundClip, options).Start(host);"),
                HelpBlock.Paragraph("完成与取消", "动作启动才播放，执行时检查请求有效性。自然结束、拒播和外部 Stop 结束等待并按 Action 契约通知一次；" +
                    "PlaySoundAction.Allocate(clip, onFinish=null, outputRoute=default, onStarted=null) 与 sequence.PlaySound(clip, outputRoute=default, onStarted=null) 在已有末尾追加 typed Action<AudioClip, AudioHandle>，原路由默认 Inherit、onFinish 位置不变；" +
                    "外层无参 Action 契约不变，序列 Callback(() => ...) 也不变，不等同于 AudioKit 的自然完成回调。" +
                    "普通 Allocate 内部使用值默认并覆盖 OutputRoute，Priority 默认 0，不创建配置类；WithOptions 的 options 位置必填但 null 使用 Sound 值默认，动作在 Allocate 时捕获并校验完整 AudioPlaybackOptions 值快照（含 AutoFade/Priority），Start 将模板按值提交内部播放链，不再复制配置类。" +
                    "分配后修改原对象不影响动作，句柄修改不反写动作模板或其他请求；不新增 Priority 专用入口。取消/Deinit 只停止自己的句柄，不通知成功。Reset 停止旧请求但保留 Clip 与完整值快照模板，下次执行重新播放、建立自动包络；Deinit 将模板清为 default(AudioPlaybackOptions)，释放借用的 OutputRoute.Group 引用并清除其他资源引用，不卸载或销毁资源，池复用不泄漏，执行代次与取消契约不变。" +
                    "等待请求结束，不保证自然播放成功；拒播、外部停止、抢占或裁剪也结束等待。暂停动作或序列不自动暂停音频；音频自然结束仍可将动作标记完成，通知由 Action 执行契约处理。暂停音频则继续等待。旧动作不能等待或停止池复用后的新代次。" +
                    "Action 在用户 onStarted 之前写入实际 lease，回调中 Reset/Deinit 可停止尚未从 Play 返回的请求；返回后核对 ActionId/执行版本/IsDeinitialized，不覆盖重入新执行。" +
                    "拒播/抢占仍按原 Action 检查完成，不把 onFinish 改为自然完成通知；固定签名反射/委托与预编译调用须按追加参数迁移，无旧重载。")),
            Guide("timer", "Timing 与生命周期边界", "计时工具独立于音频，不增加第二条播放链。",
                Array.Empty<Type>(),
                HelpBlock.Paragraph("工具归属", "Timer位于Runtime/Foundation/Timing，namespace为YLFramework.Timing，属Runtime主体的Foundation职责；默认服务与显式手动TimerScheduler使用同一到期内核。" +
                    "插值和组合使用 Action，音频播放进度直接读 AudioHandle.Time/Duration/Progress。TimerScope、ActionHandle 和 AudioHandle " +
                    "分别管理自己的任务或请求，不共享生命周期索引。Audio 的完成检测使用宿主 Update，不以 clip.length 计时。"),
                HelpBlock.Paragraph("清理", "所有者在选定的 OnDisable/OnDestroy/场景边界取消自有 TimerHandle 或关闭 TimerScope，并清理播放句柄和订阅；不要用 Timer.Shutdown 代替普通组件退出。" +
                    "通用注销与生命周期工具使用 YLFramework.Events，不再提供 Audio 专属包装。完整 Timer API 见 Documentation/Modules/Timing.md。")),
            Guide("diagnostics", "诊断与平台入口", "被动读取 Sound 统计与实际 Clip 预算，唯一写操作为显式统计清零，不创建音频宿主。",
                new[] { typeof(AudioKitDiagnostics), typeof(AudioKitDebugWindow), typeof(AudioKit), typeof(AudioSoundStats) },
                HelpBlock.Paragraph("打开方式", "YLFramework/Audio/Debug 打开独立 Editor 窗口，复用 Runtime 诊断绘制器；运行 GUI 入口仅存在于 Editor/Development Build。" +
                    "快捷键：桌面/WebGL F1、Android Escape/返回、iOS 四指触屏；旧 Input Manager 或 Both 必须可用。"),
                HelpBlock.Code("查询与显示", "查询不会初始化播放宿主；List 可复用以减少快照分配。",
                    "int count = AudioKit.SoundCount;\nAudioHandle[] snapshot = AudioKit.GetPlayingSounds();\n" +
                    "AudioKit.CopyPlayingSounds(reusableList);\nAudioSoundStats stats = AudioKit.SoundStats;\nAudioKit.ResetSoundStats();\nAudioKitDebugWindow.Toggle();"),
                HelpBlock.Paragraph("SoundStats", "AudioSoundStats 是 readonly struct，SoundStats 与 ResetSoundStats 均被动，不初始化 Settings/宿主。int 属性为 ActiveCount/PeakActiveCount/CachedCount/OwnedSourceCount；" +
                    "long 属性为 AdmittedCount/StartFailureCount/DedupRejectedCount/GlobalRejectedCount/ClipRejectedCount/PriorityRejectedCount/StolenCount/TrimmedCount/CacheHitCount/CacheMissCount/SourceCreatedCount/SourceReleasedCount。" +
                    "拒播只计一个主要原因：去重先判，Clip/全局同时满归 Clip，优先级不足归 Priority；无效参数和 Sound off 不归容量拒播。Admitted 包含随后开始失败的接纳，StartFailure 单列。" +
                    "Stolen 是接纳抢占，Trimmed 是缩容裁剪；预热不伪造播放缓存命中、接纳或拒播。SourceReleasedCount 是释放请求，不表示原生同帧已物理销毁；Music/Voice Source 不计入 Sound 统计。" +
                    "Reset 清累计，PeakActiveCount 设为当前 ActiveCount，保留当前 active/cache/source gauges，不停播、不清 Clip 规则或去重；Shutdown/新会话全部重置。"),
                HelpBlock.Notice("性能边界", "SoundCount 是有效请求数，包含暂停声音，不等于发声数。快照数组只在显式查询时分配；List 扩容仍可能分配。" +
                    "诊断显示分类开关、音量、Mute、默认输出组及有效句柄的 Time/Duration/Progress、路由模式和实际输出组/Mixer；Sound 每请求显示实际 Clip 实例 ID、count/effective limit/Priority，不按名字合并。" +
                    "全局显示 peak、主要拒播、抢占/裁剪、cache/source，冷态也显示；只提供 Reset Sound statistics 按钮，不提供播放控制或预算编辑。" +
                    "SoundConcurrency 是独立运行时并发配置，SoundCount 包含暂停、Mute 与 Loop；Music 查询为最新目标而非旧尾轨。SoundCacheCapacity 默认 10 不是并发上限，不宣称零 GC。打开诊断不创建 Settings/AudioManager/Source/Listener。"))
        };

        /// <summary>建立模块归属、程序集版本和 XML 文档来源的说明页。</summary>
        private static HelpPage[] CreateAbout() => new[]
        {
            new HelpPage("usage", "使用与版本", "关于", "",
                HelpBlock.Paragraph("", "AudioKit 是 YLFramework 的 Clip 与显式具名资源音频服务，命名空间 YLFramework.Audio，属于共享程序集 YLFramework.Runtime；" +
                    "音效动作桥接内建于同一程序集。帮助复用Editor主体中的YLFramework.Editor.Help命名空间。\n\n" +
                    "Unity 版本：" + Application.unityVersion + "\nRuntime 程序集版本：" + typeof(AudioKit).Assembly.GetName().Version +
                    "\n程序集版本来自编译元数据，不代表模块单独发布版本。\n\n" +
                    "源码 XML 由共享 Runtime 的 csc.rsp 生成唯一 Library/YLFramework.Runtime.xml，HelpKit 解析；不扫描业务程序集或伪造类型说明。"))
        };
    }
}
