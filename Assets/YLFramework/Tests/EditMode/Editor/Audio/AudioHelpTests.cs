using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;
using UnityEngine.Scripting.APIUpdating;
using YLFramework.Editor.Help;
using YLFramework.Audio;

namespace YLFramework.Tests.Audio
{
    public class AudioHelpTests
    {
        private static HelpModule CreateHelp()
        {
            var editor = Assembly.Load("YLFramework.Editor");
            var provider = editor.GetType("YLFramework.Editor.Audio.AudioKitHelpProvider", true);
            RuntimeHelpers.RunClassConstructor(provider.TypeHandle);
            return HelpRegistry.GetModules().Single(module => module.Id == "audiokit").Create();
        }

        [Test]
        public void HelpRegistersSharedAudioApiWithoutDependencyTypes()
        {
            AudioKit.Shutdown();
            var module = CreateHelp();
            Assert.IsFalse(AudioManager.HasInstance, "Creating help must not initialize a playback host.");
            Assert.IsNull(AudioKit.CurrentSettings, "Creating help must not initialize Settings.");
            Assert.AreEqual(9, module.Guides.Length);
            var overview = module.Guides.Single(page => page.Id == "overview");
            Assert.That(overview.RelatedTypes.All(type => type.Assembly == typeof(AudioKit).Assembly), Is.True);
            StringAssert.Contains("直接复用 YL 池、Binding 和 Events", overview.SearchText);
            foreach (var contract in new[] { "AudioKit.PlayMusic(musicClip)", "AudioKit.PlayVoice(voiceClip)", "AudioKit.PlaySound(soundClip)",
                         "AudioMusicPlayer", "Button.onClick", "AudioEmitter.Play", "Settings Slider", "SetValueWithoutNotify", "AudioListener",
                         "无需初始化", "MusicDefaults", "Defaults.Loop=false", "Volume=0", "Pitch=0", "不自动改 true",
                         "统一初始默认值", "全局 / 每 Clip 窗口 | 5 / 5 帧", "EveryOne 才关闭去重", "0 不是关闭去重",
                         "CrossFadeMusic API | EqualPower", "applyOnAwake=false、transitionDuration=0.5", "已有序列化选择不自动改写",
                         "1/50、Linear、doppler=0", "缓存 / 预热 | 10 / 无自动预热", "Limit=0、DefaultClipLimit=0 无限" })
            {
                StringAssert.Contains(contract, overview.SearchText);
            }

            CollectionAssert.AreEqual(new[] { "overview", "control", "settings", "spatial", "mixer", "components", "action", "timer", "diagnostics" },
                module.Guides.Select(page => page.Id));
            Assert.AreEqual(1, module.About.Length);
            var runtime = typeof(AudioKit).Assembly;
            Assert.AreEqual("YLFramework.Runtime", runtime.GetName().Name);
            Assert.AreSame(runtime, typeof(PlaySoundAction).Assembly);
            Assert.AreSame(runtime, typeof(PlaySoundExtension).Assembly);
            var requiredTypes = new[]
            {
                typeof(AudioKit), typeof(AudioKit.PlaySoundModes), typeof(AudioHandle), typeof(AudioSoundOptions), typeof(AudioWeightedClip), typeof(AudioAutoFade),
                typeof(AudioSoundConcurrency), typeof(SoundOverflowPolicy), typeof(AudioCrossfadeCurve), typeof(AudioSoundStats),
                typeof(AudioKitSettings), typeof(AudioEmitter), typeof(AudioMusicPlayer), typeof(PlaySoundAction),
                typeof(PlaySoundExtension), typeof(AudioKitDiagnostics), typeof(AudioKitDebugWindow),
                typeof(AudioOutputRoute), typeof(AudioOutputRoute.RouteMode), typeof(AudioMixerConfig),
                typeof(AudioMixerControl), typeof(AudioMixerController), typeof(AudioChannel), typeof(AudioCategory), typeof(AudioChannelMode),
                typeof(AudioClipAddress), typeof(AudioClipLease), typeof(IAudioClipProvider), typeof(ResourceAudioClipProvider),
                typeof(AudioPlayRequest), typeof(AudioPlayState), typeof(AudioPlayEnd), typeof(AudioClipLibrary), typeof(AudioClipGroup),
                typeof(AudioClipEntry), typeof(AudioClipSource), typeof(AudioClipSelectionMode), typeof(AudioClipSelection), typeof(AudioGroupPlayer), typeof(AudioGroupAutoPlay)
            };
            var apiTypes = module.ApiTypes.Select(definition => definition.Type).ToArray();
            foreach (var type in requiredTypes)
            {
                CollectionAssert.Contains(apiTypes, type);
            }

            CollectionAssert.AreEquivalent(runtime.GetExportedTypes().Where(type =>
                type.Namespace == "YLFramework.Audio" ||
                type.Namespace != null && type.Namespace.StartsWith("YLFramework.Audio.", StringComparison.Ordinal)), apiTypes);
            Assert.IsFalse(apiTypes.Any(type => type.Name.Contains("Fluent")));
            CollectionAssert.DoesNotContain(apiTypes, typeof(YLFramework.Resource.ResourceSystem));
            CollectionAssert.DoesNotContain(apiTypes, typeof(AudioManager));
            CollectionAssert.DoesNotContain(apiTypes, typeof(AudioVoice));
            CollectionAssert.DoesNotContain(apiTypes, typeof(SoundDeduplicator));
            CollectionAssert.DoesNotContain(apiTypes, typeof(YLFramework.Binding.BindableProperty<float>));
            CollectionAssert.DoesNotContain(apiTypes, typeof(YLFramework.Binding.BindableProperty<AudioMixerGroup>));
            CollectionAssert.DoesNotContain(apiTypes, typeof(AudioMixer));
            CollectionAssert.DoesNotContain(apiTypes, typeof(AudioMixerSnapshot));
            Assert.IsTrue(module.ApiTypes.All(definition => definition.Type.Assembly == runtime &&
                (definition.Type.Namespace == "YLFramework.Audio" ||
                 (definition.Type.Namespace != null && definition.Type.Namespace.StartsWith("YLFramework.Audio.", StringComparison.Ordinal)))));

            var editor = Assembly.Load("YLFramework.Editor");
            var provider = editor.GetType("YLFramework.Editor.Audio.AudioKitHelpProvider", true);
            var debug = editor.GetType("YLFramework.Editor.Audio.AudioKitDebugEditorWindow", true);
            var movedFrom = debug.GetCustomAttributesData().Single(attribute => attribute.AttributeType == typeof(MovedFromAttribute));
            CollectionAssert.AreEqual(new object[] { true, "Project.AudioKit.Editor", "Project.AudioKit.Editor", "AudioKitDebugEditorWindow" },
                movedFrom.ConstructorArguments.Select(argument => argument.Value));
            Assert.AreEqual("YLFramework/Audio/帮助", provider.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)
                .GetCustomAttribute<MenuItem>().menuItem);
            Assert.AreEqual("YLFramework/Audio/Debug", debug.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)
                .GetCustomAttribute<MenuItem>().menuItem);
        }

        [Test]
        public void MixerGuideDescribesRoutingControlAndExplicitOwnershipWithoutCreatingHost()
        {
            AudioKit.Shutdown();
            var module = CreateHelp();
            var mixer = module.Guides.Single(page => page.Id == "mixer");
            foreach (var type in new[] { typeof(AudioOutputRoute), typeof(AudioOutputRoute.RouteMode), typeof(AudioMixerConfig),
                         typeof(AudioMixerControl), typeof(AudioMixerController), typeof(AudioKitSettings), typeof(AudioKit) })
            {
                CollectionAssert.Contains(mixer.RelatedTypes, type);
                CollectionAssert.Contains(mixer.FeaturedTypes, type);
            }
            foreach (var contract in new[] { "Inherit/MixerGroup/Direct", "options.OutputRoute", "MusicOutputGroup.Value",
                         "VoiceOutputGroup.Value", "SoundOutputGroup.Value", "ApplyMixerConfig", "ClearMixerRouting",
                         "TrySetFloat", "TryGetFloat", "TryClearFloat", "TrySetVolume", "TryGetVolume",
                         "LinearVolumeToDecibels", "DecibelsToLinearVolume", "TransitionToSnapshot", "TransitionToSnapshots",
                         "Start", "Awake/OnEnable/AfterSceneLoad", "[0,1]", "-80", "false", "ArgumentException",
                         "不重播", "不写 PlayerPrefs", "不自动绑定 Mixer", "不加载、卸载或销毁", "禁用/销毁不清全局配置", "不停止单次覆盖",
                         "未配置", "Awake 安静跳过", "显式 Apply", "已配置但非法仍报错", "Group=null", "保持自动 Apply 关闭",
                         "applyOnAwake 字段初始值 true", "Reset 设置 applyOnAwake=false、transitionDuration=0.5",
                         "只绑定 Config 不自动应用或清全局路由", "Apply Routing On Awake", "运行时 AddComponent 不承诺 Reset" })
            {
                StringAssert.Contains(contract, mixer.SearchText);
            }

            StringAssert.Contains("OutputRoute", module.Guides.Single(page => page.Id == "components").SearchText);
            StringAssert.Contains("outputRoute=default", module.Guides.Single(page => page.Id == "action").SearchText);
            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
        }

        [Test]
        public void FadeAndWeightedGuidesDescribeFrozenContractsWithoutCreatingSettingsOrHost()
        {
            AudioKit.Shutdown();
            var module = CreateHelp();
            var control = module.Guides.Single(page => page.Id == "control");
            foreach (var contract in new[] { "FadeIn(float duration, bool ignoreTimeScale=true)",
                         "FadeOut(float duration, bool stopWhenDone=true, bool ignoreTimeScale=true)",
                         "Clamp01(category * requestScale) * gain", "Options.Volume", "gain=0", "不重复跳零",
                         "新淡变替换旧淡变", "ArgumentOutOfRangeException", "0 立即到目标", "default/过期句柄 no-op",
                         "AudioManager Update", "unscaled", "scaled", "Pause", "off", "Mute 仍推进", "剩余淡变",
                         "Sound off", "target 丢失", "池回收", "短 Clip 自然结束优先", "不调用自然结束 callback",
                         "Play 返回后同帧", "onStarted", "Crossfade", "独立 transitionGain", "Tween", "首采样", "DSP" })
            {
                StringAssert.Contains(contract, control.SearchText);
            }

            foreach (var contract in new[] { "PlaySound/PlaySoundAt/PlaySoundFollowing/PlayRandomSound/PlayWeightedRandomSound",
                         "onFinished 之后追加 onStarted=null", "固定签名反射", "委托绑定", "预编译 ABI", "无效 Clip/目标",
                         "Source.Play 之后", "SoundCount 包含自身", "去重已提交", "同 Clip 嵌套", "可能已失效的原 lease",
                         "抛异常", "本次仍拥有的代次", "回滚本次仍拥有的去重记录", "不误停同 Voice 复用的新请求",
                         "CrossFadeMusic(clip, duration, options=null, onStarted=null, onFinished=null, ignoreTimeScale=true, curve=EqualPower)",
                         "options=null", "Loop=true", "Defaults.Loop=false", "0 立即替换", "最新目标", "最多两条 Music Source",
                         "u=Clamp01(elapsed/duration)", "capturedGain*(1-u)", "transitionGain=0", "requestEnvelopeGain * transitionGain",
                         "ignoreTimeScale=false", "curve 追加在参数末尾且默认 EqualPower", "Linear 旧轨 capturedGain*(1-u)",
                         "EqualPower 旧轨 capturedGain*cos(u*pi/2)、新轨 sin(u*pi/2)", "0.7071", "等功率不保证等响度",
                         "A->B->C", "同 Clip 也重开", "硬替换", "StopMusic 停止两条",
                         "任一有效参与者暂停", "共同过渡时钟", "ResumeMusic 才首次启播", "Music off", "transitionGain=1",
                         "on 从头重播", "Mute 不冻结", "旧 outgoing", "新目标提前自然结束", "不复活旧曲", "epoch 与 lease 隔离" })
            {
                StringAssert.Contains(contract, control.SearchText);
            }

            var settings = module.Guides.Single(page => page.Id == "settings");
            foreach (var id in new[] { "overview", "control", "settings", "components" })
            {
                var guide = module.Guides.Single(page => page.Id == id);
                foreach (var type in new[] { typeof(AudioSoundConcurrency), typeof(SoundOverflowPolicy) })
                {
                    CollectionAssert.Contains(guide.RelatedTypes, type, id);
                    CollectionAssert.Contains(guide.FeaturedTypes, type, id);
                }
            }
            foreach (var contract in new[] { "[Serializable] struct", "public int Limit", "public SoundOverflowPolicy OverflowPolicy",
                         "RejectNew=0", "StopOldest=1", "0/unlimited", "整体赋值", "返回配置副本", "完整校验后初始化/应用",
                         "负 Limit", "未知策略", "原配置/请求不变", "不创建宿主、Source 或 Listener", "Shutdown/新会话恢复默认",
                         "暂停、Mute 与 Loop", "不包含 Music/Voice", "无 Source 分配、无去重提交、无回调", "接纳序号",
                          "待 Update 清理", "保留较早请求", "保留较新请求", "StopLowestPriority=2", "DefaultClipLimit", "Priority", "EveryOne", "不重抽" })
            {
                StringAssert.Contains(contract, settings.SearchText);
            }

            foreach (var contract in new[] { "已有合法存档优先", "内存回退 1", "不自动重写坏存档", "Delete", "TrySetValue", "SetValueWithoutCallback",
                         "ArgumentOutOfRangeException", "内存、存档、通知和播放状态不变", "两个窗口默认 5 帧",
                         "仅同帧去重", "EveryOne 才关闭去重", "边界包含最后一帧", "10 帧是显式业务冷却" })
            {
                StringAssert.Contains(contract, settings.SearchText);
            }

            var spatial = module.Guides.Single(page => page.Id == "spatial");
            CollectionAssert.Contains(spatial.RelatedTypes, typeof(AudioWeightedClip));
            CollectionAssert.Contains(spatial.FeaturedTypes, typeof(AudioWeightedClip));
            foreach (var contract in new[] { "[Serializable] struct", "Clip/Weight", "weight=1f", "IReadOnlyList<AudioWeightedClip>",
                         "PlayWeightedRandomSound", "TrySelectWeightedRandomClip", "options=null", "onFinished=null",
                         "out AudioClip clip", "finite", "包括无效 Clip", "Weight>0", "null/空/all0", "out clip=null",
                         "double", "一次抽选", "去重拒播不重抽", "不防重复", "均匀随机模式完全不变", "不初始化 Settings 或宿主" })
            {
                StringAssert.Contains(contract, spatial.SearchText);
            }

            var components = module.Guides.Single(page => page.Id == "components");
            foreach (var contract in new[] { "useWeightedRandom 默认 false", "weightedClips", "Clip/Weight", "三种位置模式",
                         "旧 clips", "去重拒播不重抽", "UnityEvent onStarted 默认空事件", "只读 Started", "无参事件",
                         "先登记 lease 与 StopOnDisable 快照", "Stop/禁用/递归 Play", "不重新登记失效旧 lease",
                         "crossFadeOnPlay 默认 false", "crossFadeDuration 默认 1", "crossFadeIgnoreTimeScale 默认 true", "crossFadeCurve 字段初始值 Linear",
                         "Reset 设置 EqualPower", "兼容旧缺字段资源", "并显式传入组件曲线",
                          "不先 Stop 旧曲", "只停自有目标及旧尾轨", "截断自己的旧尾轨", "长期存活的音乐所有者",
                          "useGlobalPlaySoundMode", "字段缺省 false", "playSoundMode=EveryOne", "Reset", "AddComponent",
                          "options.PlaySoundMode=null", "空候选", "旧循环", "空 Clip", "旧音乐", "duration/curve",
                          "Advanced 默认折叠", "隐藏字段不静默清空", "新增项 Weight=1", "旧 Weight=0" })
            {
                StringAssert.Contains(contract, components.SearchText);
            }

            foreach (var type in new[] { typeof(AudioClipLibrary), typeof(AudioClipGroup), typeof(AudioClipEntry), typeof(AudioClipSource),
                         typeof(AudioClipSelectionMode), typeof(AudioGroupPlayer), typeof(AudioGroupAutoPlay) })
            {
                CollectionAssert.Contains(components.RelatedTypes, type);
                CollectionAssert.Contains(components.FeaturedTypes, type);
            }
            foreach (var contract in new[] { "AudioClipLibrary", "素材组与控制通道是两种身份", "Uniform/Weighted", "ProjectId", "逻辑地址", "显式迁移",
                         "ExternalProvider", "SetProvider", "空 persistenceId 不持久化", "Manual", "FirstStart", "EveryEnable", "PlayGroup(string)",
                         "停用停止所有自有播放与 pending", "nullable PlaySoundMode 不被 Unity 序列化", "不随机抽选", "Prefab Override" })
            {
                StringAssert.Contains(contract, components.SearchText);
            }

            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
        }

        private static void AssertPublicMethod(Type type, string name, bool isStatic, Type returnType,
            Type[] parameterTypes, string[] parameterNames, params object[] optionalDefaults)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.DeclaredOnly |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, parameterTypes, null);
            Assert.IsNotNull(method, type.Name + "." + name + " signature is missing.");
            Assert.AreEqual(returnType, method.ReturnType, name);
            var parameters = method.GetParameters();
            CollectionAssert.AreEqual(parameterNames, parameters.Select(parameter => parameter.Name), name);
            var required = parameters.Length - optionalDefaults.Length;
            for (var i = 0; i < parameters.Length; i++)
            {
                Assert.AreEqual(i >= required, parameters[i].IsOptional, name + "." + parameters[i].Name);
                Assert.AreEqual(i >= required, parameters[i].HasDefaultValue, name + "." + parameters[i].Name);
                if (i < required)
                {
                    continue;
                }
                // Reflection can represent a default value-type argument as null or as its zero value.
                var actual = parameters[i].DefaultValue;
                if (actual == null && parameters[i].ParameterType.IsValueType)
                {
                    actual = Activator.CreateInstance(parameters[i].ParameterType);
                }
                else if (actual != null && parameters[i].ParameterType.IsEnum && actual.GetType() != parameters[i].ParameterType)
                {
                    actual = Enum.ToObject(parameters[i].ParameterType, actual);
                }

                Assert.AreEqual(optionalDefaults[i - required], actual, name + "." + parameters[i].Name);
            }
        }

        [Test]
        public void AutoFadeGuidesDescribeClipClockTakeoverAndDefaultOffWithoutCreatingHost()
        {
            AudioKit.Shutdown();
            var module = CreateHelp();
            var control = module.Guides.Single(page => page.Id == "control");
            foreach (var id in new[] { "overview", "control", "spatial", "components", "action" })
            {
                var guide = module.Guides.Single(page => page.Id == id);
                CollectionAssert.Contains(guide.RelatedTypes, typeof(AudioAutoFade), id);
                CollectionAssert.Contains(guide.FeaturedTypes, typeof(AudioAutoFade), id);
            }
            foreach (var contract in new[] { "[Serializable] struct", "FadeInSeconds/FadeOutSeconds", "仅赋值，不校验",
                         "默认关闭", "default/全零不改变旧行为", "options.AutoFade", "PlayMusicWithOptions", "PlayVoiceWithOptions",
                         "onStarted=null, onFinished=null", "必填 AudioSoundOptions", "Defaults.Loop=false", "旧 PlayMusic 默认 true",
                         "新增方法不同名", "无旧方法重载", "空间字段不生效", "Sound 去重策略不应用", "不初始化 Settings 或宿主",
                         "片段秒", "timeSamples / (double)clip.frequency", "clip.samples / (double)clip.frequency",
                         "不改变 AudioHandle.Time/Duration/Progress", "Pitch=2", "Time.timeScale 不参与", "无 ignoreTimeScale 参数",
                         "x+y>L", "double", "同比缩短", "零时长一侧取 1", "不重叠", "交接点 gain=1", "极小正值",
                         "有限且非负", "分配宿主、去重登记、替换槽位前校验", "Source.Play 和 onStarted 前 gain=0",
                         "纯淡出初始 gain=1", "未启播不消耗", "Update 帧级", "首采样", "最后采样", "压缩/Streaming",
                         "Loop && FadeOutSeconds>0 拒绝", "min(FadeInSeconds,L)", "首轮 PCM 回绕", "loop latch", "后续不重复淡入",
                         "短循环/低帧率", "自动模式仍 active", "Pause/Resume 不重置", "Mute 不冻结",
                         "自动不 Stop", "t=L", "尾淡出>0 时 gain=0", "原 Clip/原 lease 最多一次", "重入清理",
                         "current gain", "取消自动控制", "非法 duration 不取消自动模式", "取消后关开不复活",
                         "stopWhenDone=false", "自动尾部不再接管", "共用单包络", "不新增 host、Timer、Mixer 参数、Prefs",
                          "旧组件自动淡变数据无需迁移", "预编译使用方必须一起重新构建", "不承诺二进制 ABI 稳定",
                           "[Serializable] sealed class", "Defaults/MusicDefaults 每次新实例", "internal struct",
                           "公开入口只捕获一次", "提交与请求存储按值传递", "不复制配置类",
                           "default(AudioPlaybackOptions) 仅用于失效清理", "释放快照中的 Group 引用",
                          "句柄修改不反写原对象", "普通引用赋值不是值复制", "FromJsonOverwrite", "缺 Pitch 得到非法 0" })
            {
                StringAssert.Contains(contract, control.SearchText);
            }

            var spatial = module.Guides.Single(page => page.Id == "spatial");
            foreach (var contract in new[] { "options.AutoFade", "等权/加权随机", "只选择一次", "实际选中 Clip 长度归一化", "拒播不重抽" })
            {
                StringAssert.Contains(contract, spatial.SearchText);
            }

            var components = module.Guides.Single(page => page.Id == "components");
            foreach (var contract in new[] { "private serialized autoFade 默认全零", "等权/加权与三种位置模式全部透传",
                            "Stop 旧循环前拒绝", "与 PlayMusicWithOptions 相同的内部值提交链", "autoFade 全零保持原无包络行为",
                          "直接构造内部 AudioPlaybackOptions", "按值提交", "不在每次 Play 创建公开配置类",
                         "Stop 自有旧句柄前拒绝", "片段秒", "短片段同比缩短", "循环不可尾淡出", "旧数据无需迁移" })
            {
                StringAssert.Contains(contract, components.SearchText);
            }

            var action = module.Guides.Single(page => page.Id == "action");
            foreach (var contract in new[] { "AllocateWithOptions(clip, options, onFinish=null, onStarted=null)",
                         "PlaySoundWithOptions(clip, options, onStarted=null)",
                         "Allocate(clip, onFinish=null, outputRoute=default, onStarted=null)",
                         "sequence.PlaySound(clip, outputRoute=default, onStarted=null)", "typed Action<AudioClip, AudioHandle>",
                           "options 必填", "内部使用值默认并覆盖 OutputRoute", "完整 AudioPlaybackOptions 值快照", "Reset",
                           "Deinit 将模板清为 default(AudioPlaybackOptions)", "释放借用的 OutputRoute.Group 引用", "执行代次与取消契约不变",
                          "用户 onStarted 之前写入实际 lease", "ActionId/执行版本/IsDeinitialized", "不覆盖重入新执行", "不把 onFinish 改为自然完成通知",
                           "Allocate 时捕获并校验", "Start 将模板按值提交内部播放链", "句柄修改不反写动作模板或其他请求",
                           "分配后修改原对象不影响动作", "等待请求结束，不保证自然播放成功",
                          "暂停动作或序列不自动暂停音频" })
            {
                StringAssert.Contains(contract, action.SearchText);
            }

            Assert.IsFalse(AudioManager.HasInstance);
            Assert.IsNull(AudioKit.CurrentSettings);
        }

        [Test]
        public void PublicContractsAppendApprovedSoundCallbacksAndPreserveUnaffectedSignatures()
        {
            var kit = typeof(AudioKit);
            var callback = typeof(Action<AudioClip, AudioHandle>);
            var options = typeof(AudioSoundOptions);
            foreach (var name in new[] { "PlayMusic", "PlayVoice" })
            {
                AssertPublicMethod(kit, name, true, typeof(AudioHandle),
                    new[] { typeof(AudioClip), typeof(bool), typeof(float), callback, callback, typeof(AudioOutputRoute) },
                    new[] { "clip", "loop", "volume", "onStarted", "onFinished", "outputRoute" },
                    name == "PlayMusic", 1f, null, null, default(AudioOutputRoute));
            }

            AssertPublicMethod(kit, "PlaySound", true, typeof(AudioHandle),
                new[] { typeof(AudioClip), options, callback, callback }, new[] { "clip", "options", "onFinished", "onStarted" }, null, null, null);
            AssertPublicMethod(kit, "PlayRandomSound", true, typeof(AudioHandle),
                new[] { typeof(IReadOnlyList<AudioClip>), options, callback, callback }, new[] { "clips", "options", "onFinished", "onStarted" }, null, null, null);
            foreach (var position in new[] { "At", "Following" })
            {
                var spatialType = position == "At" ? typeof(Vector3) : typeof(Transform);
                var spatialName = position == "At" ? "position" : "target";
                AssertPublicMethod(kit, "PlaySound" + position, true, typeof(AudioHandle),
                    new[] { typeof(AudioClip), spatialType, options, callback, callback },
                    new[] { "clip", spatialName, "options", "onFinished", "onStarted" }, null, null, null);
                AssertPublicMethod(kit, "PlayVoice" + position, true, typeof(AudioHandle),
                    new[] { typeof(AudioClip), spatialType, options, callback, callback },
                    new[] { "clip", spatialName, "options", "onStarted", "onFinished" }, null, null, null);
            }
            foreach (var name in new[] { "Initialize", "Shutdown", "ClearMixerRouting", "StopMusic", "PauseMusic", "ResumeMusic",
                         "StopVoice", "PauseVoice", "ResumeVoice", "StopAllSound" })
            {
                AssertPublicMethod(kit, name, true, typeof(void), Type.EmptyTypes, Array.Empty<string>());
            }

            AssertPublicMethod(kit, "ApplyMixerConfig", true, typeof(void), new[] { typeof(AudioMixerConfig) }, new[] { "config" });
            AssertPublicMethod(kit, "GetPlayingSounds", true, typeof(AudioHandle[]), Type.EmptyTypes, Array.Empty<string>());
            AssertPublicMethod(kit, "CopyPlayingSounds", true, typeof(void), new[] { typeof(List<AudioHandle>) }, new[] { "destination" });
            AssertPublicMethod(kit, "SetClipSoundLimit", true, typeof(void), new[] { typeof(AudioClip), typeof(int) }, new[] { "clip", "limit" });
            AssertPublicMethod(kit, "RemoveClipSoundLimit", true, typeof(bool), new[] { typeof(AudioClip) }, new[] { "clip" });
            AssertPublicMethod(kit, "ClearClipSoundLimits", true, typeof(void), Type.EmptyTypes, Array.Empty<string>());
            AssertPublicMethod(kit, "GetEffectiveClipSoundLimit", true, typeof(int), new[] { typeof(AudioClip) }, new[] { "clip" });
            AssertPublicMethod(kit, "GetSoundCount", true, typeof(int), new[] { typeof(AudioClip) }, new[] { "clip" });
            AssertPublicMethod(kit, "PrewarmSounds", true, typeof(int), new[] { typeof(int) }, new[] { "cachedCount" });
            AssertPublicMethod(kit, "ResetSoundStats", true, typeof(void), Type.EmptyTypes, Array.Empty<string>());
            AssertPublicMethod(kit, "CrossFadeMusic", true, typeof(AudioHandle),
                new[] { typeof(AudioClip), typeof(float), options, callback, callback, typeof(bool), typeof(AudioCrossfadeCurve) },
                new[] { "clip", "duration", "options", "onStarted", "onFinished", "ignoreTimeScale", "curve" }, null, null, null, true,
                AudioCrossfadeCurve.EqualPower);
            CollectionAssert.AreEquivalent(new[] { "Initialize", "Shutdown", "ApplyMixerConfig", "ClearMixerRouting",
                "PlayMusic", "PlayVoice", "PlayVoiceAt", "PlayVoiceFollowing", "StopMusic", "PauseMusic", "ResumeMusic",
                "StopVoice", "PauseVoice", "ResumeVoice", "PlaySound", "PlaySoundAt", "PlaySoundFollowing", "PlayRandomSound",
                "StopAllSound", "GetPlayingSounds", "CopyPlayingSounds", "PlayWeightedRandomSound", "TrySelectWeightedRandomClip",
                "PlayMusicWithOptions", "PlayVoiceWithOptions", "CrossFadeMusic", "SetClipSoundLimit", "RemoveClipSoundLimit",
                "ClearClipSoundLimits", "GetEffectiveClipSoundLimit", "GetSoundCount", "PrewarmSounds", "ResetSoundStats", "RegisterChannel",
                "PlayMusicNamed", "PlayVoiceNamed", "PlaySoundNamed", "CrossFadeMusicNamed", "PlayMusicAsync", "PlayVoiceAsync",
                "PlaySoundAsync", "CrossFadeMusicAsync", "AcquireClip", "ClearClipCache" },
                kit.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(method => !method.IsSpecialName).Select(method => method.Name), "Do not add or replace legacy Play overloads.");
            var propertyNames = new[] { "Settings", "Music", "Voice", "SoundCount", "PlaySoundMode",
                "GlobalFrameCountForIgnoreSameSound", "SoundFrameCountForIgnoreSameSound", "SoundConcurrency", "SoundCacheCapacity", "SoundStats", "ClipCacheCapacity", "ClipCacheIdleSeconds" };
            var propertyTypes = new[] { typeof(AudioKitSettings), typeof(AudioHandle), typeof(AudioHandle), typeof(int),
                typeof(AudioKit.PlaySoundModes), typeof(int), typeof(int), typeof(AudioSoundConcurrency), typeof(int), typeof(AudioSoundStats), typeof(int), typeof(double) };
            CollectionAssert.AreEquivalent(propertyNames, kit.GetProperties(BindingFlags.Public | BindingFlags.Static).Select(property => property.Name));
            for (var i = 0; i < propertyNames.Length; i++)
            {
                var property = kit.GetProperty(propertyNames[i]);
                Assert.AreEqual(propertyTypes[i], property.PropertyType, propertyNames[i]);
                Assert.AreEqual(i >= 4 && propertyNames[i] != "SoundStats", property.GetSetMethod() != null, propertyNames[i]);
            }
            var concurrency = typeof(AudioSoundConcurrency);
            Assert.IsTrue(concurrency.IsValueType && !concurrency.IsEnum);
            Assert.IsTrue(concurrency.IsDefined(typeof(SerializableAttribute), false));
            CollectionAssert.AreEquivalent(new[] { "Limit", "OverflowPolicy", "DefaultClipLimit" },
                concurrency.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(field => field.Name));
            Assert.AreEqual(typeof(int), concurrency.GetField("Limit").FieldType);
            Assert.AreEqual(typeof(SoundOverflowPolicy), concurrency.GetField("OverflowPolicy").FieldType);
            Assert.AreEqual(typeof(int), concurrency.GetField("DefaultClipLimit").FieldType);
            Assert.IsFalse(concurrency.GetField("DefaultClipLimit").IsInitOnly);
            Assert.IsFalse(concurrency.GetField("Limit").IsInitOnly);
            Assert.IsFalse(concurrency.GetField("OverflowPolicy").IsInitOnly);
            Assert.IsTrue(typeof(SoundOverflowPolicy).IsEnum);
            CollectionAssert.AreEqual(new[] { "RejectNew", "StopOldest", "StopLowestPriority" }, Enum.GetNames(typeof(SoundOverflowPolicy)));
            Assert.AreEqual(0, (int)SoundOverflowPolicy.RejectNew);
            Assert.AreEqual(1, (int)SoundOverflowPolicy.StopOldest);
            Assert.AreEqual(2, (int)SoundOverflowPolicy.StopLowestPriority);
            Assert.IsTrue(typeof(AudioCrossfadeCurve).IsEnum);
            CollectionAssert.AreEqual(new[] { "Linear", "EqualPower" }, Enum.GetNames(typeof(AudioCrossfadeCurve)));
            Assert.AreEqual(0, (int)AudioCrossfadeCurve.Linear);
            Assert.AreEqual(1, (int)AudioCrossfadeCurve.EqualPower);
            Assert.AreEqual(0, default(AudioSoundConcurrency).Limit);
            Assert.AreEqual(0, default(AudioSoundConcurrency).DefaultClipLimit);
            Assert.AreEqual(SoundOverflowPolicy.RejectNew, default(AudioSoundConcurrency).OverflowPolicy);

            foreach (var name in new[] { "Pause", "Resume", "Stop" })
            {
                AssertPublicMethod(typeof(AudioHandle), name, false, typeof(void), Type.EmptyTypes, Array.Empty<string>());
            }

            AssertPublicMethod(typeof(AudioHandle), "SetVolumeScale", false, typeof(void), new[] { typeof(float) }, new[] { "volumeScale" });
            AssertPublicMethod(typeof(AudioHandle), "SetPitch", false, typeof(void), new[] { typeof(float) }, new[] { "pitch" });
            CollectionAssert.AreEquivalent(new[] { "Pause", "Resume", "Stop", "SetVolumeScale", "SetPitch", "FadeIn", "FadeOut" },
                typeof(AudioHandle).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(method => !method.IsSpecialName).Select(method => method.Name));
            foreach (var name in new[] { "IsValid", "IsPlaying", "IsPaused", "Time", "Duration", "Progress" })
            {
                var property = typeof(AudioHandle).GetProperty(name);
                Assert.AreEqual(name.StartsWith("Is", StringComparison.Ordinal) ? typeof(bool) : typeof(float), property.PropertyType);
                Assert.IsNull(property.GetSetMethod(), name + " must remain read-only.");
            }
        }

        [Test]
        public void FadeAndWeightedHelpContractsMatchRealPublicApi()
        {
            AssertPublicMethod(typeof(AudioHandle), "FadeIn", false, typeof(void), new[] { typeof(float), typeof(bool) },
                new[] { "duration", "ignoreTimeScale" }, true);
            AssertPublicMethod(typeof(AudioHandle), "FadeOut", false, typeof(void), new[] { typeof(float), typeof(bool), typeof(bool) },
                new[] { "duration", "stopWhenDone", "ignoreTimeScale" }, true, true);
            AssertPublicMethod(typeof(AudioKit), "PlayWeightedRandomSound", true, typeof(AudioHandle),
                new[] { typeof(IReadOnlyList<AudioWeightedClip>), typeof(AudioSoundOptions), typeof(Action<AudioClip, AudioHandle>), typeof(Action<AudioClip, AudioHandle>) },
                new[] { "clips", "options", "onFinished", "onStarted" }, null, null, null);
            AssertPublicMethod(typeof(AudioKit), "TrySelectWeightedRandomClip", true, typeof(bool),
                new[] { typeof(IReadOnlyList<AudioWeightedClip>), typeof(AudioClip).MakeByRefType() }, new[] { "clips", "clip" });
            Assert.IsTrue(typeof(AudioKit).GetMethod("TrySelectWeightedRandomClip").GetParameters()[1].IsOut);
            var weighted = typeof(AudioWeightedClip);
            Assert.IsTrue(weighted.IsValueType && !weighted.IsEnum);
            Assert.IsTrue(weighted.IsDefined(typeof(SerializableAttribute), false));
            CollectionAssert.AreEquivalent(new[] { "clip", "weight" },
                weighted.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(field => field.Name));
            Assert.AreEqual(typeof(AudioClip), weighted.GetField("clip").FieldType);
            Assert.AreEqual(typeof(float), weighted.GetField("weight").FieldType);
            Assert.IsFalse(weighted.GetField("clip").IsInitOnly);
            Assert.IsFalse(weighted.GetField("weight").IsInitOnly);
            var constructor = weighted.GetConstructor(new[] { typeof(AudioClip), typeof(float) });
            Assert.IsNotNull(constructor);
            CollectionAssert.AreEqual(new[] { "clip", "weight" }, constructor.GetParameters().Select(parameter => parameter.Name));
            Assert.IsFalse(constructor.GetParameters()[0].IsOptional);
            Assert.IsTrue(constructor.GetParameters()[1].IsOptional);
            Assert.AreEqual(1f, constructor.GetParameters()[1].DefaultValue);
            foreach (var name in new[] { "useWeightedRandom", "weightedClips" })
            {
                var field = typeof(AudioEmitter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, "AudioEmitter." + name);
                Assert.IsTrue(field.IsPrivate);
                Assert.AreEqual(name == "useWeightedRandom" ? typeof(bool) : typeof(List<AudioWeightedClip>), field.FieldType);
                Assert.IsTrue(field.IsDefined(typeof(SerializeField), false));
            }
            var started = typeof(AudioEmitter).GetProperty("Started", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.IsNotNull(started);
            Assert.AreEqual(typeof(UnityEvent), started.PropertyType);
            Assert.IsNotNull(started.GetGetMethod());
            Assert.IsNull(started.GetSetMethod());
            var onStarted = typeof(AudioEmitter).GetField("onStarted", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(onStarted);
            Assert.IsTrue(onStarted.IsPrivate);
            Assert.AreEqual(typeof(UnityEvent), onStarted.FieldType);
            Assert.IsTrue(onStarted.IsDefined(typeof(SerializeField), false));
            foreach (var name in new[] { "crossFadeOnPlay", "crossFadeDuration", "crossFadeIgnoreTimeScale" })
            {
                var field = typeof(AudioMusicPlayer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, "AudioMusicPlayer." + name);
                Assert.IsTrue(field.IsPrivate);
                Assert.AreEqual(name == "crossFadeDuration" ? typeof(float) : typeof(bool), field.FieldType);
                Assert.IsTrue(field.IsDefined(typeof(SerializeField), false));
            }
        }

        [Test]
        public void AutoFadeAndWithOptionsContractsMatchRealPublicApiWithoutInitializingHost()
        {
            AudioKit.Shutdown();
            var autoFade = typeof(AudioAutoFade);
            Assert.IsTrue(autoFade.IsValueType && !autoFade.IsEnum);
            Assert.IsTrue(autoFade.IsDefined(typeof(SerializableAttribute), false));
            CollectionAssert.AreEquivalent(new[] { "FadeInSeconds", "FadeOutSeconds" },
                autoFade.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(field => field.Name));
            foreach (var name in new[] { "FadeInSeconds", "FadeOutSeconds" })
            {
                var field = autoFade.GetField(name);
                Assert.AreEqual(typeof(float), field.FieldType, name);
                Assert.IsFalse(field.IsInitOnly, name);
            }
            Assert.IsEmpty(autoFade.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
            Assert.IsEmpty(autoFade.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
            var constructor = autoFade.GetConstructor(new[] { typeof(float), typeof(float) });
            Assert.IsNotNull(constructor);
            Assert.AreEqual(1, autoFade.GetConstructors().Length);
            CollectionAssert.AreEqual(new[] { "fadeInSeconds", "fadeOutSeconds" }, constructor.GetParameters().Select(parameter => parameter.Name));
            Assert.IsTrue(constructor.GetParameters().All(parameter => !parameter.IsOptional && !parameter.HasDefaultValue));
            var zero = default(AudioAutoFade);
            Assert.AreEqual(0f, zero.FadeInSeconds);
            Assert.AreEqual(0f, zero.FadeOutSeconds);
            var assigned = new AudioAutoFade(-1f, float.PositiveInfinity);
            Assert.AreEqual(-1f, assigned.FadeInSeconds, "The constructor must only assign; playback validates configuration.");
            Assert.AreEqual(float.PositiveInfinity, assigned.FadeOutSeconds);

            var callback = typeof(Action<AudioClip, AudioHandle>);
            foreach (var name in new[] { "PlayMusicWithOptions", "PlayVoiceWithOptions" })
            {
                AssertPublicMethod(typeof(AudioKit), name, true, typeof(AudioHandle),
                    new[] { typeof(AudioClip), typeof(AudioSoundOptions), callback, callback },
                    new[] { "clip", "options", "onStarted", "onFinished" }, null, null);
                Assert.AreEqual(1, typeof(AudioKit).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Count(method => method.Name == name), "No WithOptions overloads: " + name);
            }
            AssertPublicMethod(typeof(PlaySoundAction), "AllocateWithOptions", true, typeof(PlaySoundAction),
                new[] { typeof(AudioClip), typeof(AudioSoundOptions), typeof(Action), callback },
                new[] { "clip", "options", "onFinish", "onStarted" }, null, null);
            AssertPublicMethod(typeof(PlaySoundExtension), "PlaySoundWithOptions", true, typeof(YLFramework.Actions.ISequence),
                new[] { typeof(YLFramework.Actions.ISequence), typeof(AudioClip), typeof(AudioSoundOptions), callback },
                new[] { "self", "clip", "options", "onStarted" }, (object)null);
            Assert.IsTrue(typeof(PlaySoundExtension).GetMethod("PlaySoundWithOptions").IsDefined(typeof(ExtensionAttribute), false));
            foreach (var name in new[] { "Allocate", "AllocateWithOptions" })
            {
                Assert.AreEqual(1, typeof(PlaySoundAction).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Count(method => method.Name == name), "Do not add Action allocation overloads: " + name);
            }

            CollectionAssert.AreEquivalent(new[] { "PlaySound", "PlaySoundWithOptions" },
                typeof(PlaySoundExtension).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(method => method.Name));
            foreach (var type in new[] { typeof(AudioEmitter), typeof(AudioMusicPlayer) })
            {
                var field = type.GetField("autoFade", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, type.Name + ".autoFade");
                Assert.IsTrue(field.IsPrivate);
                Assert.AreEqual(typeof(AudioAutoFade), field.FieldType);
                Assert.IsTrue(field.IsDefined(typeof(SerializeField), false));
                Assert.IsNotNull(field.GetCustomAttribute<TooltipAttribute>());
            }
            Assert.AreEqual(default(AudioAutoFade), AudioSoundOptions.Defaults.AutoFade);
            Assert.IsFalse(AudioManager.HasInstance, "Reflection and configuration construction must not initialize a playback host.");
            Assert.IsNull(AudioKit.CurrentSettings);
        }

        [Test]
        public void OptionsAndSettingsPublicSurfaceAddsDefaultOffAutoFadeAndZeroPriorityAndPreservesOldDefaults()
        {
            Assert.IsTrue(typeof(AudioSoundOptions).IsClass && typeof(AudioSoundOptions).IsSealed);
            Assert.IsTrue(typeof(AudioSoundOptions).IsDefined(typeof(SerializableAttribute), false));
            CollectionAssert.AreEquivalent(new[] { "Defaults", "MusicDefaults" },
                typeof(AudioSoundOptions).GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(property => property.Name));
            Assert.IsEmpty(typeof(AudioSoundOptions).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                "Request copying is internal; do not add a public Clone API.");
            CollectionAssert.AreEquivalent(new[] { "Loop", "Volume", "Pitch", "PlaySoundMode", "MinDistance", "MaxDistance", "RolloffMode", "OutputRoute", "AutoFade", "Priority", "AutoPauseWhenTimeScaleZero" },
                typeof(AudioSoundOptions).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(field => field.Name));
            var optionFields = new[] { "Loop", "Volume", "Pitch", "PlaySoundMode", "MinDistance", "MaxDistance", "RolloffMode", "OutputRoute", "AutoFade", "Priority", "AutoPauseWhenTimeScaleZero" };
            var optionTypes = new[] { typeof(bool), typeof(float), typeof(float), typeof(AudioKit.PlaySoundModes?), typeof(float),
                typeof(float), typeof(AudioRolloffMode), typeof(AudioOutputRoute), typeof(AudioAutoFade), typeof(int), typeof(bool) };
            for (var i = 0; i < optionFields.Length; i++)
            {
                Assert.AreEqual(optionTypes[i], typeof(AudioSoundOptions).GetField(optionFields[i]).FieldType, optionFields[i]);
            }

            var defaults = AudioSoundOptions.Defaults;
            Assert.IsFalse(defaults.Loop);
            Assert.AreEqual(1f, defaults.Volume);
            Assert.AreEqual(1f, defaults.Pitch);
            Assert.IsNull(defaults.PlaySoundMode);
            Assert.AreEqual(1f, defaults.MinDistance);
            Assert.AreEqual(50f, defaults.MaxDistance);
            Assert.AreEqual(AudioRolloffMode.Linear, defaults.RolloffMode);
            Assert.AreEqual(default(AudioOutputRoute), defaults.OutputRoute);
            Assert.AreEqual(default(AudioAutoFade), defaults.AutoFade);
            Assert.AreEqual(0, defaults.Priority);
            Assert.IsFalse(defaults.AutoPauseWhenTimeScaleZero);
            var settings = typeof(AudioKitSettings);
            CollectionAssert.AreEquivalent(new[] { "IsSoundOn", "IsMusicOn", "IsVoiceOn", "SoundVolume", "MusicVolume", "VoiceVolume",
                "IsSoundMuted", "IsMusicMuted", "IsVoiceMuted", "MusicOutputGroup", "VoiceOutputGroup", "SoundOutputGroup", "IsOn" },
                settings.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name));
            foreach (var category in new[] { "Sound", "Music", "Voice" })
            {
                Assert.AreEqual(typeof(YLFramework.Binding.PlayerPrefsBoolProperty), settings.GetProperty("Is" + category + "On").PropertyType);
                Assert.AreEqual(typeof(YLFramework.Binding.PlayerPrefsFloatProperty), settings.GetProperty(category + "Volume").PropertyType);
                Assert.AreEqual(typeof(YLFramework.Binding.BindableProperty<bool>), settings.GetProperty("Is" + category + "Muted").PropertyType);
            }
            Assert.AreEqual(typeof(bool), settings.GetProperty("IsOn").PropertyType);
            Assert.IsNotNull(settings.GetProperty("IsOn").GetSetMethod());
            AssertPublicMethod(settings, "Dispose", false, typeof(void), Type.EmptyTypes, Array.Empty<string>());
            AssertPublicMethod(typeof(PlaySoundAction), "Allocate", true, typeof(PlaySoundAction),
                new[] { typeof(AudioClip), typeof(Action), typeof(AudioOutputRoute), typeof(Action<AudioClip, AudioHandle>) },
                new[] { "clip", "onFinish", "outputRoute", "onStarted" }, null, default(AudioOutputRoute), null);
            AssertPublicMethod(typeof(PlaySoundExtension), "PlaySound", true, typeof(YLFramework.Actions.ISequence),
                new[] { typeof(YLFramework.Actions.ISequence), typeof(AudioClip), typeof(AudioOutputRoute), typeof(Action<AudioClip, AudioHandle>) },
                new[] { "self", "clip", "outputRoute", "onStarted" }, default(AudioOutputRoute), null);
        }

        [Test]
        public void MixerHelpContractsMatchRealPublicApi()
        {
            Assert.IsTrue(typeof(AudioOutputRoute).IsValueType);
            Assert.AreEqual(typeof(AudioOutputRoute.RouteMode), typeof(AudioOutputRoute).GetField("Mode").FieldType);
            Assert.AreEqual(typeof(AudioMixerGroup), typeof(AudioOutputRoute).GetField("Group").FieldType);
            CollectionAssert.AreEqual(new[] { "Inherit", "MixerGroup", "Direct" }, Enum.GetNames(typeof(AudioOutputRoute.RouteMode)));
            Assert.AreEqual(0, (int)AudioOutputRoute.RouteMode.Inherit);
            foreach (var name in new[] { "Inherit", "Direct" })
            {
                Assert.AreEqual(typeof(AudioOutputRoute), typeof(AudioOutputRoute).GetProperty(name, BindingFlags.Public | BindingFlags.Static).PropertyType);
            }

            Assert.AreEqual(typeof(AudioOutputRoute), typeof(AudioOutputRoute).GetMethod("ToMixer", new[] { typeof(AudioMixerGroup) }).ReturnType);
            Assert.AreEqual(typeof(AudioOutputRoute), typeof(AudioSoundOptions).GetField("OutputRoute").FieldType);
            foreach (var name in new[] { "MusicOutputGroup", "VoiceOutputGroup", "SoundOutputGroup" })
            {
                Assert.AreEqual(typeof(YLFramework.Binding.BindableProperty<AudioMixerGroup>), typeof(AudioKitSettings).GetProperty(name).PropertyType);
            }

            foreach (var name in new[] { "PlayMusic", "PlayVoice" })
            {
                var parameters = typeof(AudioKit).GetMethod(name).GetParameters();
                CollectionAssert.AreEqual(new[] { "clip", "loop", "volume", "onStarted", "onFinished", "outputRoute" }, parameters.Select(parameter => parameter.Name));
                CollectionAssert.AreEqual(new[] { typeof(AudioClip), typeof(bool), typeof(float), typeof(Action<AudioClip, AudioHandle>),
                    typeof(Action<AudioClip, AudioHandle>), typeof(AudioOutputRoute) }, parameters.Select(parameter => parameter.ParameterType));
                Assert.IsTrue(parameters.Last().IsOptional);
            }
            var allocate = typeof(PlaySoundAction).GetMethod("Allocate", new[] { typeof(AudioClip), typeof(Action), typeof(AudioOutputRoute),
                typeof(Action<AudioClip, AudioHandle>) });
            Assert.IsNotNull(allocate);
            CollectionAssert.AreEqual(new[] { "clip", "onFinish", "outputRoute", "onStarted" }, allocate.GetParameters().Select(parameter => parameter.Name));
            Assert.AreEqual(typeof(Action), allocate.GetParameters()[1].ParameterType);
            Assert.AreEqual(typeof(AudioOutputRoute), allocate.GetParameters()[2].ParameterType);
            Assert.IsTrue(allocate.GetParameters()[2].IsOptional);
            Assert.AreEqual(typeof(Action<AudioClip, AudioHandle>), allocate.GetParameters().Last().ParameterType);
            Assert.IsTrue(allocate.GetParameters().Last().IsOptional);
            Assert.IsNull(allocate.GetParameters().Last().DefaultValue);
            var sequence = typeof(PlaySoundExtension).GetMethod("PlaySound");
            Assert.AreEqual(typeof(AudioOutputRoute), sequence.GetParameters()[2].ParameterType);
            Assert.AreEqual("outputRoute", sequence.GetParameters()[2].Name);
            Assert.IsTrue(sequence.GetParameters()[2].IsOptional);
            Assert.AreEqual(typeof(Action<AudioClip, AudioHandle>), sequence.GetParameters().Last().ParameterType);
            Assert.AreEqual("onStarted", sequence.GetParameters().Last().Name);
            Assert.IsTrue(sequence.GetParameters().Last().IsOptional);
            Assert.IsNull(sequence.GetParameters().Last().DefaultValue);

            Assert.IsTrue(typeof(AudioMixerConfig).IsSubclassOf(typeof(ScriptableObject)));
            Assert.IsNotNull(typeof(AudioMixerConfig).GetCustomAttribute<CreateAssetMenuAttribute>());
            foreach (var name in new[] { "Mixer", "MusicGroup", "VoiceGroup", "SoundGroup" })
            {
                var property = typeof(AudioMixerConfig).GetProperty(name);
                Assert.AreEqual(name == "Mixer" ? typeof(AudioMixer) : typeof(AudioMixerGroup), property.PropertyType);
                Assert.IsNull(property.GetSetMethod(), "Config properties must not expose public setters.");
            }
            Assert.AreEqual(typeof(void), typeof(AudioKit).GetMethod("ApplyMixerConfig", new[] { typeof(AudioMixerConfig) }).ReturnType);
            Assert.AreEqual(typeof(void), typeof(AudioKit).GetMethod("ClearMixerRouting", Type.EmptyTypes).ReturnType);
            Assert.IsTrue(typeof(AudioMixerControl).IsAbstract && typeof(AudioMixerControl).IsSealed);
            foreach (var name in new[] { "TrySetFloat", "TrySetVolume" })
            {
                Assert.AreEqual(typeof(bool), typeof(AudioMixerControl).GetMethod(name, new[] { typeof(AudioMixer), typeof(string), typeof(float) }).ReturnType);
            }

            foreach (var name in new[] { "TryGetFloat", "TryGetVolume" })
            {
                Assert.AreEqual(typeof(bool), typeof(AudioMixerControl).GetMethod(name, new[] { typeof(AudioMixer), typeof(string), typeof(float).MakeByRefType() }).ReturnType);
            }

            Assert.AreEqual(typeof(bool), typeof(AudioMixerControl).GetMethod("TryClearFloat", new[] { typeof(AudioMixer), typeof(string) }).ReturnType);
            foreach (var name in new[] { "LinearVolumeToDecibels", "DecibelsToLinearVolume" })
            {
                Assert.AreEqual(typeof(float), typeof(AudioMixerControl).GetMethod(name, new[] { typeof(float) }).ReturnType);
            }

            Assert.AreEqual(typeof(void), typeof(AudioMixerControl).GetMethod("TransitionToSnapshot", new[] { typeof(AudioMixerSnapshot), typeof(float) }).ReturnType);
            Assert.AreEqual(typeof(void), typeof(AudioMixerControl).GetMethod("TransitionToSnapshots", new[] { typeof(AudioMixer), typeof(AudioMixerSnapshot[]), typeof(float[]), typeof(float) }).ReturnType);
            Assert.IsTrue(typeof(AudioMixerController).IsSubclassOf(typeof(MonoBehaviour)));
            foreach (var name in new[] { "Apply", "ClearRouting", "ClearParameter", "TransitionToSnapshot", "TransitionToSnapshots" })
            {
                Assert.AreEqual(typeof(void), typeof(AudioMixerController).GetMethod(name, Type.EmptyTypes).ReturnType);
            }

            foreach (var name in new[] { "SetParameter", "SetVolume" })
            {
                Assert.AreEqual(typeof(void), typeof(AudioMixerController).GetMethod(name, new[] { typeof(float) }).ReturnType);
            }
        }

        [Test]
        public void HelpXmlIsGeneratedAndDocumentsClipOnlyAndMixerPublicContracts()
        {
            var module = CreateHelp();
            Assert.AreEqual(1, module.XmlPaths.Count);
            var pair = module.XmlPaths.Single();
            Assert.AreSame(typeof(AudioKit).Assembly, pair.Key);
            Assert.AreSame(typeof(PlaySoundAction).Assembly, pair.Key);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "YLFramework.Runtime.xml")), pair.Value);
            Assert.IsTrue(File.Exists(pair.Value), "Expected Unity csc.rsp XML: " + pair.Value);
            var xml = XDocument.Load(pair.Value);
            Assert.AreEqual("YLFramework.Runtime", xml.Root.Element("assembly").Element("name").Value);
            foreach (var entryType in new[] { typeof(AudioKit), typeof(AudioHandle), typeof(AudioSoundOptions), typeof(AudioWeightedClip), typeof(AudioAutoFade),
                         typeof(AudioSoundConcurrency), typeof(SoundOverflowPolicy), typeof(AudioCrossfadeCurve), typeof(AudioSoundStats), typeof(AudioKitSettings),
                         typeof(PlaySoundAction), typeof(AudioOutputRoute), typeof(AudioOutputRoute.RouteMode), typeof(AudioMixerConfig),
                         typeof(AudioMixerControl), typeof(AudioMixerController) })
            {
                var documentation = xml.Root.Element("members").Elements("member")
                    .Single(member => (string)member.Attribute("name") == "T:" + entryType.FullName.Replace('+', '.'));
                Assert.IsNotNull(documentation.Element("summary"), entryType + " XML summary is missing");
                Assert.IsFalse(string.IsNullOrWhiteSpace(documentation.Element("summary").Value));
            }
            foreach (var name in new[] { "PlayMusic", "PlayVoice" })
            {
                var documentation = xml.Root.Element("members").Elements("member").Single(member =>
                    ((string)member.Attribute("name")).StartsWith("M:YLFramework.Audio.AudioKit." + name + "(", StringComparison.Ordinal));
                var route = documentation.Elements("param").Single(parameter => (string)parameter.Attribute("name") == "outputRoute");
                Assert.IsFalse(string.IsNullOrWhiteSpace(route.Value), name + " must document its appended route parameter.");
            }
        }

        [Test]
        public void HelpXmlDocumentsFadeConcurrencyCrossfadeAndPlaybackPublicContracts()
        {
            var module = CreateHelp();
            var path = module.XmlPaths[typeof(AudioKit).Assembly];
            Assert.IsTrue(File.Exists(path), "Expected Unity csc.rsp XML: " + path);
            var members = XDocument.Load(path).Root.Element("members").Elements("member")
                .ToDictionary(member => (string)member.Attribute("name"));
            var prefix = "YLFramework.Audio.";
            var callback = "System.Action{UnityEngine.AudioClip," + prefix + "AudioHandle}";
            var entries = new[]
            {
                "T:" + prefix + "AudioWeightedClip",
                "F:" + prefix + "AudioWeightedClip.clip",
                "F:" + prefix + "AudioWeightedClip.weight",
                "M:" + prefix + "AudioWeightedClip.#ctor(UnityEngine.AudioClip,System.Single)",
                "M:" + prefix + "AudioHandle.FadeIn(System.Single,System.Boolean)",
                "M:" + prefix + "AudioHandle.FadeOut(System.Single,System.Boolean,System.Boolean)",
                "M:" + prefix + "AudioKit.PlayWeightedRandomSound(System.Collections.Generic.IReadOnlyList{" + prefix + "AudioWeightedClip}," + prefix + "AudioSoundOptions," + callback + "," + callback + ")",
                "M:" + prefix + "AudioKit.TrySelectWeightedRandomClip(System.Collections.Generic.IReadOnlyList{" + prefix + "AudioWeightedClip},UnityEngine.AudioClip@)",
                "T:" + prefix + "AudioAutoFade",
                "F:" + prefix + "AudioAutoFade.FadeInSeconds",
                "F:" + prefix + "AudioAutoFade.FadeOutSeconds",
                "M:" + prefix + "AudioAutoFade.#ctor(System.Single,System.Single)",
                "F:" + prefix + "AudioSoundOptions.AutoFade",
                "M:" + prefix + "AudioKit.PlayMusicWithOptions(UnityEngine.AudioClip," + prefix + "AudioSoundOptions,System.Action{UnityEngine.AudioClip," + prefix + "AudioHandle},System.Action{UnityEngine.AudioClip," + prefix + "AudioHandle})",
                "M:" + prefix + "AudioKit.PlayVoiceWithOptions(UnityEngine.AudioClip," + prefix + "AudioSoundOptions,System.Action{UnityEngine.AudioClip," + prefix + "AudioHandle},System.Action{UnityEngine.AudioClip," + prefix + "AudioHandle})",
                "M:" + prefix + "PlaySoundAction.AllocateWithOptions(UnityEngine.AudioClip," + prefix + "AudioSoundOptions,System.Action," + callback + ")",
                "M:" + prefix + "PlaySoundExtension.PlaySoundWithOptions(YLFramework.Actions.ISequence,UnityEngine.AudioClip," + prefix + "AudioSoundOptions," + callback + ")",
                "T:" + prefix + "AudioSoundConcurrency",
                "F:" + prefix + "AudioSoundConcurrency.Limit",
                "F:" + prefix + "AudioSoundConcurrency.OverflowPolicy",
                "T:" + prefix + "SoundOverflowPolicy",
                "F:" + prefix + "SoundOverflowPolicy.RejectNew",
                "F:" + prefix + "SoundOverflowPolicy.StopOldest",
                "P:" + prefix + "AudioKit.SoundConcurrency",
                "M:" + prefix + "AudioKit.CrossFadeMusic(UnityEngine.AudioClip,System.Single," + prefix + "AudioSoundOptions," + callback + "," + callback + ",System.Boolean," + prefix + "AudioCrossfadeCurve)",
                "T:" + prefix + "AudioCrossfadeCurve",
                "F:" + prefix + "AudioCrossfadeCurve.Linear",
                "F:" + prefix + "AudioCrossfadeCurve.EqualPower",
                "M:" + prefix + "AudioKit.PlaySound(UnityEngine.AudioClip," + prefix + "AudioSoundOptions," + callback + "," + callback + ")",
                "M:" + prefix + "AudioKit.PlaySoundAt(UnityEngine.AudioClip,UnityEngine.Vector3," + prefix + "AudioSoundOptions," + callback + "," + callback + ")",
                "M:" + prefix + "AudioKit.PlaySoundFollowing(UnityEngine.AudioClip,UnityEngine.Transform," + prefix + "AudioSoundOptions," + callback + "," + callback + ")",
                "M:" + prefix + "AudioKit.PlayRandomSound(System.Collections.Generic.IReadOnlyList{UnityEngine.AudioClip}," + prefix + "AudioSoundOptions," + callback + "," + callback + ")",
                "M:" + prefix + "PlaySoundAction.Allocate(UnityEngine.AudioClip,System.Action," + prefix + "AudioOutputRoute," + callback + ")",
                "M:" + prefix + "PlaySoundExtension.PlaySound(YLFramework.Actions.ISequence,UnityEngine.AudioClip," + prefix + "AudioOutputRoute," + callback + ")",
                "P:" + prefix + "AudioEmitter.Started"
            };
            var parameters = new[] { Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new[] { "clip", "weight" },
                new[] { "duration", "ignoreTimeScale" }, new[] { "duration", "stopWhenDone", "ignoreTimeScale" },
                new[] { "clips", "options", "onFinished", "onStarted" }, new[] { "clips", "clip" },
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new[] { "fadeInSeconds", "fadeOutSeconds" },
                Array.Empty<string>(), new[] { "clip", "options", "onStarted", "onFinished" }, new[] { "clip", "options", "onStarted", "onFinished" },
                new[] { "clip", "options", "onFinish", "onStarted" }, new[] { "self", "clip", "options", "onStarted" },
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                new[] { "clip", "duration", "options", "onStarted", "onFinished", "ignoreTimeScale", "curve" },
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                new[] { "clip", "options", "onFinished", "onStarted" }, new[] { "clip", "position", "options", "onFinished", "onStarted" },
                new[] { "clip", "target", "options", "onFinished", "onStarted" }, new[] { "clips", "options", "onFinished", "onStarted" },
                new[] { "clip", "onFinish", "outputRoute", "onStarted" }, new[] { "self", "clip", "outputRoute", "onStarted" }, Array.Empty<string>() };
            Assert.AreEqual(entries.Length, parameters.Length, "Each exact XML member must have a parameter contract.");
            for (var i = 0; i < entries.Length; i++)
            {
                Assert.IsTrue(members.TryGetValue(entries[i], out var documentation), entries[i] + " XML is missing.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(documentation.Element("summary")?.Value), entries[i] + " summary is missing.");
                CollectionAssert.AreEqual(parameters[i], documentation.Elements("param").Select(element => (string)element.Attribute("name")),
                    entries[i] + " must document exactly the real public parameters in declaration order.");
                foreach (var name in parameters[i])
                {
                    var parameter = documentation.Elements("param").Single(element => (string)element.Attribute("name") == name);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(parameter.Value), entries[i] + "." + name + " XML is missing.");
                }
            }
            var budgetEntries = new Dictionary<string, string[]>
            {
                { "F:" + prefix + "AudioSoundOptions.Priority", Array.Empty<string>() },
                { "P:" + prefix + "AudioSoundOptions.Defaults", Array.Empty<string>() },
                { "P:" + prefix + "AudioSoundOptions.MusicDefaults", Array.Empty<string>() },
                { "F:" + prefix + "AudioSoundConcurrency.DefaultClipLimit", Array.Empty<string>() },
                { "F:" + prefix + "SoundOverflowPolicy.StopLowestPriority", Array.Empty<string>() },
                { "P:" + prefix + "AudioKit.SoundCacheCapacity", Array.Empty<string>() },
                { "P:" + prefix + "AudioKit.SoundStats", Array.Empty<string>() },
                { "M:" + prefix + "AudioKit.PrewarmSounds(System.Int32)", new[] { "cachedCount" } },
                { "M:" + prefix + "AudioKit.ResetSoundStats", Array.Empty<string>() },
                { "M:" + prefix + "AudioKit.SetClipSoundLimit(UnityEngine.AudioClip,System.Int32)", new[] { "clip", "limit" } },
                { "M:" + prefix + "AudioKit.RemoveClipSoundLimit(UnityEngine.AudioClip)", new[] { "clip" } },
                { "M:" + prefix + "AudioKit.ClearClipSoundLimits", Array.Empty<string>() },
                { "M:" + prefix + "AudioKit.GetEffectiveClipSoundLimit(UnityEngine.AudioClip)", new[] { "clip" } },
                { "M:" + prefix + "AudioKit.GetSoundCount(UnityEngine.AudioClip)", new[] { "clip" } }
            };
            foreach (var property in typeof(AudioSoundStats).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                budgetEntries.Add("P:" + prefix + "AudioSoundStats." + property.Name, Array.Empty<string>());
            }

            foreach (var entry in budgetEntries)
            {
                Assert.IsTrue(members.TryGetValue(entry.Key, out var documentation), entry.Key + " XML is missing.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(documentation.Element("summary")?.Value), entry.Key);
                CollectionAssert.AreEqual(entry.Value, documentation.Elements("param").Select(element => (string)element.Attribute("name")), entry.Key);
                foreach (var parameter in documentation.Elements("param"))
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(parameter.Value), entry.Key);
                }
            }
        }

        [Test]
        public void SoundStatsIsAReadonlyValueSnapshotWithExactGaugeAndCounterProperties()
        {
            var type = typeof(AudioSoundStats);
            Assert.IsTrue(type.IsValueType && !type.IsEnum);
            Assert.IsTrue(type.IsDefined(typeof(IsReadOnlyAttribute), false));
            Assert.IsEmpty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            var gauges = new[] { "ActiveCount", "PeakActiveCount", "CachedCount", "OwnedSourceCount" };
            var counters = new[] { "AdmittedCount", "StartFailureCount", "DedupRejectedCount", "GlobalRejectedCount", "ClipRejectedCount",
                "PriorityRejectedCount", "StolenCount", "TrimmedCount", "CacheHitCount", "CacheMissCount", "SourceCreatedCount", "SourceReleasedCount" };
            CollectionAssert.AreEquivalent(gauges.Concat(counters), type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => property.Name));
            foreach (var name in gauges.Concat(counters))
            {
                var property = type.GetProperty(name);
                Assert.AreEqual(gauges.Contains(name) ? typeof(int) : typeof(long), property.PropertyType, name);
                Assert.IsNotNull(property.GetGetMethod(), name);
                Assert.IsFalse(property.CanWrite, name);
            }
            var priority = typeof(AudioEmitter).GetField("priority", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(priority);
            Assert.IsTrue(priority.IsPrivate && priority.IsDefined(typeof(SerializeField), false));
            Assert.AreEqual(typeof(int), priority.FieldType);
        }
    }
}
