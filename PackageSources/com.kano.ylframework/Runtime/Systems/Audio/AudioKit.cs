using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>统一 AudioClip 播放内核。直接传 Clip 只借用；显式命名入口通过窄 Provider 持有资源。不创建 AudioListener。</summary>
    /// <remarks>播放回调收到实际 Clip 和原播放句柄；开始回调可能在播放入口返回前同步执行。
    /// 自然完成回调在原责任清理前执行；回调内的停止、替换或 Shutdown 可使原句柄失效。
    /// 停止、拒播、替换、关闭分类和目标丢失本身不触发自然完成回调。
    /// 播放及控制须在 Unity 主线程调用，不提供跨线程调度。
    /// 缺省 Music 使用至多两个过渡声部，Voice 使用独占槽；命名通道的 Music/Voice 不占这些缺省槽，Sound 共用分类预算。</remarks>
    public static partial class AudioKit
    {
        private static AudioKitSettings _settings;
        private static SoundDeduplicator _channels;
        private static AudioSoundConcurrency _soundConcurrency;
        private static int _soundCacheCapacity = 10;
        private static AudioSoundCounters _soundCounters;
        private static ulong _soundSession;
        private static Dictionary<int, ClipSoundRule> _clipSoundLimits;
        private static List<int> _expiredClipRules;
        /// <summary>保留 Clip 原引用及实例容量，避免仅凭复用的实例 ID 误认规则归属。</summary>
        private readonly struct ClipSoundRule
        {
            internal readonly AudioClip Clip;
            internal readonly int Limit;
            /// <summary>记录原 Clip 身份与已经校验的运行时上限。</summary>
            internal ClipSoundRule(AudioClip clip, int limit)
            {
                Clip = clip;
                Limit = limit;
            }
        }
        private static bool _isShuttingDown;
        private static bool _isQuitting;
        internal static bool IsClosing => _isShuttingDown || _isQuitting ||
            Application.isPlaying && !YLFramework.Singletons.SingletonSessionState.CanRunExecution;
        internal static AudioKitSettings CurrentSettings => _settings;
        internal static SoundDeduplicator Channels
        {
            get
            {
                Initialize();
                return _channels;
            }
        }
        /// <summary>持久化的分类通道设置。首次访问会初始化，但不会创建播放宿主。</summary>
        public static AudioKitSettings Settings
        {
            get
            {
                Initialize();
                return _settings;
            }
        }
        /// <summary>最新 Music 请求的句柄；没有请求时返回无效句柄。读取不会创建宿主。</summary>
        public static AudioHandle Music => AudioManager.TryGetInstance(out var host) ? host.Music?.Handle ?? default : default;
        /// <summary>最新 Voice 请求的句柄；没有请求时返回无效句柄。读取不会创建宿主。</summary>
        public static AudioHandle Voice => AudioManager.TryGetInstance(out var host) ? host.Voice?.Handle ?? default : default;
        /// <summary>登记独立命名通道。名称与显式持久化身份冲突均拒绝；省略 persistenceId 不读写通道偏好键。</summary>
        /// <remarks>区分大小写，身份限 ASCII 字母/数字及 . _ - /。登记创建同一 Audio 宿主但不播放；Dispose/Shutdown 后身份失效。</remarks>
        /// <param name="name">当前会话内唯一、区分大小写的通道登记名。</param>
        /// <param name="category">决定分类设置及是否参与 Sound 预算，不决定独占拓扑。</param>
        /// <param name="mode">通道采用替换旧请求还是重叠播放。</param>
        /// <param name="persistenceId">可选的唯一稳定存储身份；null 表示不持久化局部音量和静音。</param>
        public static AudioChannel RegisterChannel(string name, AudioCategory category = AudioCategory.Music,
            AudioChannelMode mode = AudioChannelMode.Exclusive, string persistenceId = null)
        {
            AudioChannel.ValidateIdentity(name, nameof(name));
            if (persistenceId != null)
            {
                AudioChannel.ValidateIdentity(persistenceId, nameof(persistenceId));
            }

            if ((uint)category > (uint)AudioCategory.Sound)
            {
                throw new ArgumentOutOfRangeException(nameof(category));
            }

            if ((uint)mode > (uint)AudioChannelMode.Overlap)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            return AudioManager.Instance.RegisterChannel(name, category, mode, persistenceId);
        }
        /// <summary>当前有效的 Sound 请求数，包含暂停、静音和循环中的请求。读取不会创建宿主。</summary>
        public static int SoundCount => AudioManager.TryGetInstance(out var host) ? host.SoundCount : 0;
        internal static AudioSoundConcurrency CurrentSoundConcurrency => _soundConcurrency;
        internal static ref AudioSoundCounters SoundCounters => ref _soundCounters;
        internal static ulong SoundSession => _soundSession;
        /// <summary>独立的闲置 Sound 声部缓存容量，默认 10，零表示禁用；访问不初始化设置或宿主。</summary>
        /// <remarks>修改容量不会预热或停止活跃请求，超额闲置音源会立即进入释放流程。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">新容量为负数。</exception>
        /// <exception cref="InvalidOperationException">AudioKit 正在关闭。</exception>
        public static int SoundCacheCapacity
        {
            get => _soundCacheCapacity;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (IsClosing)
                {
                    throw new InvalidOperationException("AudioKit is shutting down.");
                }

                _soundCacheCapacity = value;
                if (AudioManager.TryGetInstance(out var host))
                {
                    host.ResizeSoundCache(value);
                }
            }
        }
        /// <summary>准备目标数量的独立闲置 Sound 音源，不播放、不通知回调、不登记去重或占活跃额度。</summary>
        /// <param name="cachedCount">闲置目标数，范围为零至 SoundCacheCapacity；零不会创建宿主。</param>
        /// <returns>本次实际新增的缓存声部数；参数表示目标总量，而不是追加数量。</returns>
        /// <remarks>显式正数预热可创建宿主；活跃与闲置音源总数允许超过活跃 Sound 上限，不创建监听器。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">cachedCount 为负数或大于 SoundCacheCapacity。</exception>
        public static int PrewarmSounds(int cachedCount)
        {
            if (cachedCount < 0 || cachedCount > _soundCacheCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(cachedCount));
            }

            return cachedCount == 0 ? 0 : AudioManager.Instance.PrewarmSounds(cachedCount);
        }
        /// <summary>读取轻量 Sound 会话快照，不初始化设置、宿主或集合。</summary>
        public static AudioSoundStats SoundStats => new AudioSoundStats(_soundCounters, SoundCount,
            AudioManager.TryGetInstance(out var host) ? host.CachedSoundCount : 0);
        /// <summary>清空 Sound 累计统计并将峰值重置为当前活跃数；不改变播放、规则、去重或当前资源量。</summary>
        public static void ResetSoundStats()
        {
            var owned = _soundCounters.OwnedSourceCount;
            _soundCounters = new AudioSoundCounters { PeakActiveCount = SoundCount, OwnedSourceCount = owned };
        }
        /// <summary>运行时 Sound 请求容量，零表示不限；暂停、静音和循环仍计数，降低上限立即裁剪超额请求。</summary>
        /// <exception cref="ArgumentOutOfRangeException">某字段为负数，或 OverflowPolicy 不在已定义取值内。</exception>
        public static AudioSoundConcurrency SoundConcurrency
        {
            get
            {
                Initialize();
                return _soundConcurrency;
            }
            set
            {
                value.Validate();
                Initialize();
                PruneClipSoundLimits();
                _soundConcurrency = value;
                if (AudioManager.TryGetInstance(out var host))
                {
                    host.TrimSounds(value);
                }
            }
        }

        /// <summary>设置有效 Clip 实例的容量覆盖并立即裁剪超额请求；零仅绕过默认 Clip 上限，不绕过全局容量。</summary>
        /// <param name="clip">借用的 Clip；卸载前应移除规则，不转移资产所有权。</param>
        /// <param name="limit">本 Clip 的非负有效请求上限，零表示不限。</param>
        /// <remarks>不初始化设置，也不创建播放宿主。</remarks>
        /// <exception cref="ArgumentNullException">clip 为 null 或已销毁。</exception>
        /// <exception cref="ArgumentOutOfRangeException">limit 为负数。</exception>
        /// <exception cref="InvalidOperationException">AudioKit 正在关闭。</exception>
        public static void SetClipSoundLimit(AudioClip clip, int limit)
        {
            if (!clip)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            if (limit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            if (IsClosing)
            {
                throw new InvalidOperationException("AudioKit is shutting down.");
            }

            PruneClipSoundLimits();
            if (_clipSoundLimits == null)
            {
                _clipSoundLimits = new Dictionary<int, ClipSoundRule>();
            }

            var id = clip.GetInstanceID();
            _clipSoundLimits[id] = new ClipSoundRule(clip, limit);
            if (AudioManager.TryGetInstance(out var host))
            {
                host.TrimClipSounds(id);
            }
        }
        /// <summary>移除精确 Clip 实例的覆盖并按恢复的默认上限协调容量；允许传已销毁对象的托管引用，不初始化会话。</summary>
        /// <param name="clip">原借用实例引用；实例 ID 复用不能移除另一 Clip 的规则。</param>
        /// <returns>该实例是否原本具有覆盖规则。</returns>
        public static bool RemoveClipSoundLimit(AudioClip clip)
        {
            if (ReferenceEquals(clip, null) || _clipSoundLimits == null)
            {
                return false;
            }

            var id = clip.GetInstanceID();
            var removed = _clipSoundLimits.TryGetValue(id, out var rule) && ReferenceEquals(rule.Clip, clip) && _clipSoundLimits.Remove(id);
            PruneClipSoundLimits();
            if (removed && AudioManager.TryGetInstance(out var host))
            {
                host.TrimClipSounds(id);
            }

            return removed;
        }
        /// <summary>移除全部运行时 Clip 覆盖，立即协调默认和全局容量；不初始化设置或宿主。</summary>
        public static void ClearClipSoundLimits()
        {
            if (_clipSoundLimits == null || _clipSoundLimits.Count == 0)
            {
                return;
            }

            _clipSoundLimits.Clear();
            if (AudioManager.TryGetInstance(out var host))
            {
                host.TrimSounds(_soundConcurrency);
            }
        }
        /// <summary>被动读取有效 Clip 实例的实际容量，无效 Clip 返回零；不初始化设置、宿主或集合。</summary>
        /// <param name="clip">借用的 Clip 实例，不是文件名或候选列表位置。</param>
        /// <returns>实例覆盖值，未覆盖则为 DefaultClipLimit；有效 Clip 的零上限表示不限。</returns>
        public static int GetEffectiveClipSoundLimit(AudioClip clip)
        {
            if (!clip)
            {
                return 0;
            }

            return _clipSoundLimits != null && _clipSoundLimits.TryGetValue(clip.GetInstanceID(), out var rule) && ReferenceEquals(rule.Clip, clip)
                ? rule.Limit : _soundConcurrency.DefaultClipLimit;
        }
        /// <summary>被动读取 Clip 实例的有效 Sound 请求数，包含暂停、静音和循环；无效 Clip 返回零。</summary>
        /// <param name="clip">借用的 Clip 实例，同名的不同实例分别计数。</param>
        /// <returns>当前请求数；不初始化设置、宿主或集合。</returns>
        public static int GetSoundCount(AudioClip clip) => clip && AudioManager.TryGetInstance(out var host) ? host.GetSoundCount(clip.GetInstanceID()) : 0;
        /// <summary>移除资源已被外部销毁的实例容量规则，不主动卸载 Clip。</summary>
        private static void PruneClipSoundLimits()
        {
            if (_clipSoundLimits == null || _clipSoundLimits.Count == 0)
            {
                return;
            }

            if (_expiredClipRules == null)
            {
                _expiredClipRules = new List<int>();
            }

            foreach (var pair in _clipSoundLimits)
            {
                if (!pair.Value.Clip)
                {
                    _expiredClipRules.Add(pair.Key);
                }
            }

            foreach (var id in _expiredClipRules)
            {
                _clipSoundLimits.Remove(id);
            }

            _expiredClipRules.Clear();
        }

        /// <summary>Sound 请求在容量接纳之前应用的帧窗口去重策略；Music 和 Voice 不适用。</summary>
        public enum PlaySoundModes
        {
            /// <summary>允许所有请求，不做去重。默认值。</summary>
            EveryOne,
            /// <summary>拒绝在共享全局帧窗口内已接纳过的同一 Clip 实例的请求；窗口由第一个落在窗口外的接纳重新开始计算。</summary>
            IgnoreSameSoundInGlobalFrames,
            /// <summary>拒绝在该 Clip 实例自身帧窗口内已接纳过的同一实例的请求。</summary>
            IgnoreSameSoundInSoundFrames
        }
        /// <summary>Sound 请求的默认去重模式；单个请求可通过自己的 PlaySoundMode 覆盖。读取会初始化 Settings。</summary>
        /// <exception cref="ArgumentOutOfRangeException">赋值为未定义的枚举值。</exception>
        public static PlaySoundModes PlaySoundMode
        {
            get => Channels.DefaultPlaySoundMode;
            set
            {
                if ((uint)value > (uint)PlaySoundModes.IgnoreSameSoundInSoundFrames)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                Channels.DefaultPlaySoundMode = value;
            }
        }
        /// <summary>包含边界的全局去重窗口，默认五帧，仅对应模式使用；零表示只限制同一帧。</summary>
        public static int GlobalFrameCountForIgnoreSameSound
        {
            get => Channels.GlobalFrameCountForIgnoreSameSound;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                Channels.GlobalFrameCountForIgnoreSameSound = value;
            }
        }
        /// <summary>包含边界的单 Clip 去重窗口，默认五帧，仅对应模式使用；零表示只限制同一帧。</summary>
        public static int SoundFrameCountForIgnoreSameSound
        {
            get => Channels.SoundFrameCountForIgnoreSameSound;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                Channels.SoundFrameCountForIgnoreSameSound = value;
            }
        }

        /// <summary>初始化持久化设置和去重记录，不创建宿主、音源或监听器。</summary>
        public static void Initialize()
        {
            if (IsClosing)
            {
                throw new InvalidOperationException("AudioKit is shutting down.");
            }

            if (_settings != null)
            {
                return;
            }

            var settings = new AudioKitSettings();
            try
            {
                _channels = new SoundDeduplicator();
                _settings = settings;
                Application.quitting -= BeginQuit;
                Application.quitting += BeginQuit;
            }
            catch
            {
                settings.Dispose();
                throw;
            }
        }

        /// <summary>结束原 Audio 会话，释放自有音源、订阅及命名资源责任，不自行卸载调用方借入的 Clip。</summary>
        public static void Shutdown()
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            try
            {
                AudioManager.Shutdown();
            }
            finally
            {
                try
                {
                    _settings?.Dispose();
                }
                finally
                {
                    _settings = null;
                    _channels = null;
                    _soundConcurrency = default;
                    _soundCacheCapacity = 10;
                    _soundCounters = default;
                    _soundSession++;
                    _clipSoundLimits = null;
                    _expiredClipRules = null;
                    _clipCacheCapacity = 0;
                    _clipCacheIdleSeconds = 0;
                    _isShuttingDown = false;
                }
            }
        }
        /// <summary>封闭后续初始化和播放入口，再执行原会话关闭。</summary>
        internal static void BeginQuit()
        {
            _isQuitting = true;
            Shutdown();
        }
        // 每次进入播放都会执行（包括关闭 Domain Reload 的会话），避免静态状态在会话之间泄漏。
        /// <summary>在子系统注册阶段重置退出标记并清理上轮 Audio 静态会话。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            _isQuitting = false;
            Shutdown();
        }

        /// <summary>复制校验后的分类 Mixer 默认路由，不创建播放宿主，也不写入 Mixer 参数。</summary>
        /// <remarks>配置和 Mixer 资源由调用方保活；资产编辑后须再次显式应用。</remarks>
        /// <param name="config">有效的分类路由配置，所有已填输出组必须属于其中的 Mixer。</param>
        public static void ApplyMixerConfig(AudioMixerConfig config)
        {
            if (!config)
            {
                throw new ArgumentNullException(nameof(config));
            }

            config.Validate();
            var music = config.MusicGroup;
            var voice = config.VoiceGroup;
            var sound = config.SoundGroup;
            var settings = Settings;
            // 路由写入会同步触发监听器；监听器可能关闭当前会话，因此检测到 Settings 已被移除后停止继续写入。
            settings.MusicOutputGroup.Value = music ? music : null;
            if (IsClosing || !ReferenceEquals(_settings, settings))
            {
                return;
            }

            settings.VoiceOutputGroup.Value = voice ? voice : null;
            if (IsClosing || !ReferenceEquals(_settings, settings))
            {
                return;
            }

            settings.SoundOutputGroup.Value = sound ? sound : null;
        }

        /// <summary>将继承分类路由的请求改回直接输出，不停止显式覆盖请求，也不清除共享 Mixer 参数。</summary>
        public static void ClearMixerRouting()
        {
            var settings = _settings;
            if (settings == null)
            {
                return;
            }
            // 路由写入会同步触发监听器；监听器可能关闭当前会话，因此检测到 Settings 已被移除后停止继续写入。
            settings.MusicOutputGroup.Value = null;
            if (IsClosing || !ReferenceEquals(_settings, settings))
            {
                return;
            }

            settings.VoiceOutputGroup.Value = null;
            if (IsClosing || !ReferenceEquals(_settings, settings))
            {
                return;
            }

            settings.SoundOutputGroup.Value = null;
        }

        /// <summary>替换缺省音乐请求；分类关闭时保留本请求，待重新启用后从头播放。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="loop">是否重复播放 Clip。</param>
        /// <param name="volume">请求音量倍率。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄，可能在本方法返回前同步通知。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄；Stop 或替换不通知。</param>
        /// <param name="outputRoute">可选的分类继承、显式 Mixer 组或直接输出路由。</param>
        public static AudioHandle PlayMusic(AudioClip clip, bool loop = true, float volume = 1f,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null,
            AudioOutputRoute outputRoute = default) => PlaySlot(clip, true, loop, volume, onStarted, onFinished, outputRoute);
        /// <summary>独立于音乐和音效替换缺省 Voice 请求。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="loop">是否重复播放 Clip。</param>
        /// <param name="volume">请求音量倍率。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄，可能在本方法返回前同步通知。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄。</param>
        /// <param name="outputRoute">可选的分类继承、显式 Mixer 组或直接输出路由。</param>
        public static AudioHandle PlayVoice(AudioClip clip, bool loop = false, float volume = 1f,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null,
            AudioOutputRoute outputRoute = default) => PlaySlot(clip, false, loop, volume, onStarted, onFinished, outputRoute);
        /// <summary>以完整 2D 请求选项替换音乐，支持音高和可选自动淡变。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="options">复制的请求配置；null 使用 MusicDefaults，显式 Defaults 不循环；忽略 Sound 去重、Priority 及空间字段。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄；自动包络的初始增益在播放前设置。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄；自动淡变不提前停止请求。</param>
        public static AudioHandle PlayMusicWithOptions(AudioClip clip, AudioSoundOptions options,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlaySlotWithOptions(clip, true, options, onStarted, onFinished);
        /// <summary>以完整 2D 请求选项替换 Voice，支持音高和可选自动淡变。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="options">复制的请求配置；null 使用 Defaults，忽略 Sound 去重、Priority 及空间字段。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄；自动包络的初始增益在播放前设置。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄；自动淡变不提前停止请求。</param>
        public static AudioHandle PlayVoiceWithOptions(AudioClip clip, AudioSoundOptions options,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlaySlotWithOptions(clip, false, options, onStarted, onFinished);
        /// <summary>捕获可变选项，再交给缺省 Music/Voice 快照入口提交。</summary>
        private static AudioHandle PlaySlotWithOptions(AudioClip clip, bool music, AudioSoundOptions options,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished) =>
            PlaySlotWithSnapshot(clip, music, AudioPlaybackOptions.Capture(options, music), started, finished);
        /// <summary>校验缺省槽的 2D 配置并协调命名等待者，未绑定资源在退出入口时归还。</summary>
        internal static AudioHandle PlaySlotWithSnapshot(AudioClip clip, bool music, AudioPlaybackOptions options,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished, AudioResourceLease resource = null, bool namedCommit = false)
        {
            try
            {
                resource?.Validate();
                if (!clip)
                {
                    return default;
                }

                options.PlaySoundMode = null;
                options.Priority = 0;
                options.Validate(false);
                options.ValidateClip(clip);
                if (!namedCommit && !InvalidateNamed(music ? AudioCategory.Music : AudioCategory.Voice))
                {
                    return default;
                }

                return AudioManager.Instance.PlaySlot(music, clip, in options, started, finished, resource: resource);
            }
            finally
            {
                if (resource != null && !resource.IsBound)
                {
                    resource.Dispose();
                }
            }
        }
        /// <summary>以固定世界位置的 3D Voice 替换缺省语音请求，不应用 Sound 去重。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="position">固定的世界坐标。</param>
        /// <param name="options">请求参数，省略时使用 Defaults；忽略 Sound Priority。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄，可能在本方法返回前同步通知。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄。</param>
        /// <exception cref="ArgumentOutOfRangeException">position 包含非有限分量。</exception>
        public static AudioHandle PlayVoiceAt(AudioClip clip, Vector3 position, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null)
        {
            ValidatePosition(position);
            return PlaySpatialVoice(clip, position, null, options, onStarted, onFinished);
        }
        /// <summary>以跟随有效且激活目标的 3D Voice 替换缺省语音请求；目标丢失会停止请求。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="target">要跟随的有效且激活的 Transform。</param>
        /// <param name="options">请求参数，省略时使用 Defaults；忽略 Sound Priority。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄，可能在本方法返回前同步通知。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄；目标丢失不通知。</param>
        /// <exception cref="ArgumentOutOfRangeException">target.position 包含非有限分量。</exception>
        public static AudioHandle PlayVoiceFollowing(AudioClip clip, Transform target, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null)
        {
            if (!target || !target.gameObject.activeInHierarchy)
            {
                return default;
            }

            var position = target.position;
            ValidatePosition(position);
            return PlaySpatialVoice(clip, position, target, options, onStarted, onFinished);
        }
        /// <summary>校验 3D Voice 配置后替换缺省语音槽，不应用 Sound 去重及优先级。</summary>
        private static AudioHandle PlaySpatialVoice(AudioClip clip, Vector3 position, Transform target,
            AudioSoundOptions options, Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished)
        {
            if (!clip)
            {
                return default;
            }

            var resolved = AudioPlaybackOptions.Capture(options);
            resolved.PlaySoundMode = null;
            resolved.Priority = 0;
            resolved.Validate(true);
            resolved.ValidateClip(clip);
            if (!InvalidateNamed(AudioCategory.Voice))
            {
                return default;
            }

            return AudioManager.Instance.PlaySlot(false, clip, in resolved, started, finished, true, position, target);
        }
        /// <summary>把简化参数转为有效快照，校验后协调等待版本并替换缺省槽。</summary>
        private static AudioHandle PlaySlot(AudioClip clip, bool music, bool loop, float volume,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished, AudioOutputRoute outputRoute)
        {
            if (!clip)
            {
                return default;
            }

            var options = AudioPlaybackOptions.Defaults;
            options.Loop = loop;
            options.Volume = volume;
            options.OutputRoute = outputRoute;
            options.Validate(false);
            if (!InvalidateNamed(music ? AudioCategory.Music : AudioCategory.Voice))
            {
                return default;
            }

            var host = AudioManager.Instance;
            return host.PlaySlot(music, clip, in options, started, finished);
        }
        /// <summary>用独立过渡包络重叠至多两个缺省音乐请求；连续发起过渡时以最新请求为准。</summary>
        /// <param name="clip">由调用方保活的目标 Clip；缺失时不替换当前音乐。</param>
        /// <param name="duration">有限非负过渡秒数；零表示立即替换全部旧音乐。</param>
        /// <param name="options">完整 2D 选项；省略时循环，显式 Defaults 不循环；忽略 Sound 去重、Priority 及空间字段。</param>
        /// <param name="onStarted">目标首次启动后的实际 Clip 和原句柄，可能在返回前通知；过渡增益已预先初始化。</param>
        /// <param name="onFinished">仅通知目标自然完成；尾轨按替换结束，不视为自然完成。</param>
        /// <param name="ignoreTimeScale">默认使用不受 timeScale 影响的时间；任一有效参与请求暂停时冻结公共时钟。</param>
        /// <param name="curve">默认 EqualPower，使不相关背景曲更接近恒功率但不保证等响度；需要旧增益映射时显式用 Linear。</param>
        public static AudioHandle CrossFadeMusic(AudioClip clip, float duration, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null,
            bool ignoreTimeScale = true, AudioCrossfadeCurve curve = AudioCrossfadeCurve.EqualPower) =>
            CrossFadeMusicWithSnapshot(clip, duration, AudioPlaybackOptions.Capture(options, true),
                onStarted, onFinished, ignoreTimeScale, curve);
        /// <summary>校验过渡与音乐选项后提交双声部内核，未绑定的命名资源由入口负责归还。</summary>
        internal static AudioHandle CrossFadeMusicWithSnapshot(AudioClip clip, float duration, AudioPlaybackOptions options,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null,
            bool ignoreTimeScale = true, AudioCrossfadeCurve curve = AudioCrossfadeCurve.EqualPower, AudioResourceLease resource = null, bool namedCommit = false)
        {
            try
            {
                resource?.Validate();
                if (!AudioSoundOptions.IsFinite(duration) || duration < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(duration));
                }

                if ((uint)curve > (uint)AudioCrossfadeCurve.EqualPower)
                {
                    throw new ArgumentOutOfRangeException(nameof(curve));
                }

                if (!clip)
                {
                    return default;
                }

                options.PlaySoundMode = null;
                options.Priority = 0;
                options.Validate(false);
                options.ValidateClip(clip);
                if (!namedCommit && !InvalidateNamed(AudioCategory.Music))
                {
                    return default;
                }

                return AudioManager.Instance.MusicPlayback.CrossFade(clip, duration, in options, onStarted, onFinished, ignoreTimeScale, curve, resource);
            }
            finally
            {
                if (resource != null && !resource.IsBound)
                {
                    resource.Dispose();
                }
            }
        }
        /// <summary>停止缺省当前音乐、尾轨和过渡，并终结进入时的缺省音乐等待者；不创建宿主。</summary>
        public static void StopMusic()
        {
            if (!AudioManager.TryGetInstance(out var host))
            {
                return;
            }

            host.AdvancePendingVersion(AudioCategory.Music, null);
            var pending = host.PendingSnapshot(AudioCategory.Music, null);
            try
            {
                host.MusicPlayback.Stop();
            }
            finally
            {
                AudioManager.EndPending(pending, AudioPlayEnd.Stopped);
            }
        }
        /// <summary>暂停缺省音乐的两个参与请求，并冻结公共过渡时钟。</summary>
        public static void PauseMusic()
        {
            if (AudioManager.TryGetInstance(out var host))
            {
                host.MusicPlayback.Pause();
            }
        }
        /// <summary>解除缺省音乐两个请求的手动暂停，并继续剩余过渡。</summary>
        public static void ResumeMusic()
        {
            if (AudioManager.TryGetInstance(out var host))
            {
                host.MusicPlayback.Resume();
            }
        }
        /// <summary>停止当前 Voice 请求，不触发其自然完成回调。没有宿主或请求时安全 no-op。</summary>
        public static void StopVoice()
        {
            if (!AudioManager.TryGetInstance(out var host))
            {
                return;
            }

            host.AdvancePendingVersion(AudioCategory.Voice, null);
            var pending = host.PendingSnapshot(AudioCategory.Voice, null);
            var handle = host.Voice.Handle;
            try
            {
                handle.Stop();
            }
            finally
            {
                AudioManager.EndPending(pending, AudioPlayEnd.Stopped);
            }
        }
        /// <summary>暂停当前 Voice 请求。没有宿主或请求时安全 no-op。</summary>
        public static void PauseVoice() => Voice.Pause();
        /// <summary>恢复当前 Voice 请求；等待 Voice 通道重新启用的请求会保持停止，直到该通道恢复启用。</summary>
        public static void ResumeVoice() => Voice.Resume();

        /// <summary>播放 2D 音效并返回带代次保护的控制句柄。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="options">请求参数，省略时使用 Defaults。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄；停止和拒播不通知。</param>
        /// <param name="onStarted">播放启动后、入口返回前的实际 Clip 和原句柄；容量与去重已经提交。</param>
        public static AudioHandle PlaySound(AudioClip clip, AudioSoundOptions options = null, Action<AudioClip, AudioHandle> onFinished = null,
            Action<AudioClip, AudioHandle> onStarted = null) =>
            PlaySoundInternal(clip, options, onFinished, onStarted, false, Vector3.zero, null);
        /// <summary>在固定世界坐标播放 3D 音效。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="position">固定的世界坐标。</param>
        /// <param name="options">请求参数，省略时使用 Defaults。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄。</param>
        /// <param name="onStarted">完成配置并启动后、入口返回前的实际 Clip 和原句柄。</param>
        /// <exception cref="ArgumentOutOfRangeException">position 包含非有限分量。</exception>
        public static AudioHandle PlaySoundAt(AudioClip clip, Vector3 position, AudioSoundOptions options = null, Action<AudioClip, AudioHandle> onFinished = null,
            Action<AudioClip, AudioHandle> onStarted = null)
        {
            ValidatePosition(position);
            return PlaySoundInternal(clip, options, onFinished, onStarted, true, position, null);
        }
        /// <summary>拒绝包含 NaN 或无穷大分量的空间播放位置。</summary>
        private static void ValidatePosition(Vector3 position)
        {
            if (!AudioSoundOptions.IsFinite(position.x) || !AudioSoundOptions.IsFinite(position.y) || !AudioSoundOptions.IsFinite(position.z))
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }
        }
        /// <summary>播放跟随有效激活目标的音效；目标丢失时停止，而不作为自然完成。</summary>
        /// <param name="clip">由调用方保活至本请求结束的 Clip。</param>
        /// <param name="target">要跟随的有效且激活的 Transform。</param>
        /// <param name="options">请求参数，省略时使用 Defaults。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄；目标丢失不通知。</param>
        /// <param name="onStarted">完成配置并启动后、入口返回前的实际 Clip 和原句柄。</param>
        /// <exception cref="ArgumentOutOfRangeException">target.position 包含非有限分量。</exception>
        public static AudioHandle PlaySoundFollowing(AudioClip clip, Transform target, AudioSoundOptions options = null, Action<AudioClip, AudioHandle> onFinished = null,
            Action<AudioClip, AudioHandle> onStarted = null)
        {
            if (!target || !target.gameObject.activeInHierarchy)
            {
                return default;
            }

            var position = target.position;
            ValidatePosition(position);
            return PlaySoundInternal(clip, options, onFinished, onStarted, true, position, target);
        }
        /// <summary>捕获单次 Sound 选项快照，将 2D、固定位置和跟随入口汇入同一接纳路径。</summary>
        private static AudioHandle PlaySoundInternal(AudioClip clip, AudioSoundOptions options,
            Action<AudioClip, AudioHandle> finished, Action<AudioClip, AudioHandle> started, bool spatial, Vector3 position, Transform target) =>
            PlaySoundWithSnapshot(clip, AudioPlaybackOptions.Capture(options), finished, started, spatial, position, target);
        /// <summary>校验目标、Clip 和请求配置，经分类开关及帧去重筛选后交宿主做容量接纳。</summary>
        internal static AudioHandle PlaySoundWithSnapshot(AudioClip clip, AudioPlaybackOptions options,
            Action<AudioClip, AudioHandle> onFinished = null, Action<AudioClip, AudioHandle> onStarted = null,
            bool spatial = false, Vector3 position = default, Transform target = null, AudioResourceLease resource = null)
        {
            try
            {
                resource?.Validate();
                if (spatial)
                {
                    if (!ReferenceEquals(target, null))
                    {
                        if (!target || !target.gameObject.activeInHierarchy)
                        {
                            return default;
                        }

                        position = target.position;
                    }
                    ValidatePosition(position);
                }
                if (!clip)
                {
                    return default;
                }

                options.Validate(spatial);
                options.ValidateClip(clip);
                if (!Settings.IsSoundOn.Value)
                {
                    return default;
                }

                var mode = options.PlaySoundMode ?? PlaySoundMode;
                if (!Channels.CanPlay(clip.GetInstanceID(), mode, Time.frameCount))
                {
                    _soundCounters.DedupRejectedCount++;
                    return default;
                }
                return AudioManager.Instance.PlaySound(clip, in options, mode, onFinished, onStarted, spatial, position, target, resource);
            }
            finally
            {
                if (resource != null && !resource.IsBound)
                {
                    resource.Dispose();
                }
            }
        }
        /// <summary>从有效 Clip 中均匀抽选一次并播放，不分配筛选集合。</summary>
        /// <param name="clips">由调用方保活的候选，忽略空项和已销毁对象。</param>
        /// <param name="options">请求参数，省略时使用 Defaults。</param>
        /// <param name="onFinished">自然结束且清理前实际选中的 Clip 和原句柄。</param>
        /// <param name="onStarted">接纳并启动后实际选中的 Clip 和原句柄；拒播不通知，也不重选。</param>
        public static AudioHandle PlayRandomSound(IReadOnlyList<AudioClip> clips, AudioSoundOptions options = null, Action<AudioClip, AudioHandle> onFinished = null,
            Action<AudioClip, AudioHandle> onStarted = null) =>
            TrySelectRandomClip(clips, out var clip) ? PlaySound(clip, options, onFinished, onStarted) : default;
        /// <summary>加权抽选一个 Clip 作为 2D 音效播放；拒播后不再换曲尝试。</summary>
        /// <param name="clips">由调用方保活的候选，每项权重都须有限且非负。</param>
        /// <param name="options">请求参数及输出路由，省略时使用 Defaults。</param>
        /// <param name="onFinished">自然结束且清理前实际选中的 Clip 和原句柄。</param>
        /// <param name="onStarted">接纳并启动后实际选中的 Clip 和原句柄；拒播不通知，也不重选。</param>
        public static AudioHandle PlayWeightedRandomSound(IReadOnlyList<AudioWeightedClip> clips, AudioSoundOptions options = null, Action<AudioClip, AudioHandle> onFinished = null,
            Action<AudioClip, AudioHandle> onStarted = null) =>
            TrySelectWeightedRandomClip(clips, out var clip) ? PlaySound(clip, options, onFinished, onStarted) : default;

        /// <summary>按相对权重抽选，不初始化设置或创建播放宿主。</summary>
        /// <param name="clips">调用方的候选集合，抽选不修改它；所有权重均校验，包括没有有效 Clip 的条目。</param>
        /// <param name="clip">选中的有效 Clip，没有正权重有效候选时为 null。</param>
        /// <returns>是否选中有效 Clip；null 和空集合返回 false。</returns>
        /// <exception cref="ArgumentOutOfRangeException">某条目的权重为负数或非有限值。</exception>
        public static bool TrySelectWeightedRandomClip(IReadOnlyList<AudioWeightedClip> clips, out AudioClip clip)
        {
            clip = null;
            if (clips == null)
            {
                return false;
            }

            double total = 0;
            AudioClip lastValid = null;
            for (var i = 0; i < clips.Count; i++)
            {
                var entry = clips[i];
                if (!AudioSoundOptions.IsFinite(entry.weight) || entry.weight < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(clips), "Every weight must be finite and nonnegative.");
                }

                if (!entry.clip || entry.weight == 0f)
                {
                    continue;
                }

                total += entry.weight;
                lastValid = entry.clip;
            }
            if (!lastValid)
            {
                return false;
            }

            var selected = UnityEngine.Random.value * total;
            double cumulative = 0;
            for (var i = 0; i < clips.Count; i++)
            {
                var entry = clips[i];
                if (!entry.clip || entry.weight == 0f)
                {
                    continue;
                }

                cumulative += entry.weight;
                if (selected < cumulative)
                {
                    clip = entry.clip;
                    return true;
                }
            }
            // Random.value 包含一，随机区间的上端点归最后一个有效条目，避免边界值选曲失败。
            clip = lastValid;
            return true;
        }

        /// <summary>先统计有效候选再按索引均匀选择，不创建临时筛选列表。</summary>
        internal static bool TrySelectRandomClip(IReadOnlyList<AudioClip> clips, out AudioClip clip)
        {
            clip = null;
            if (clips == null)
            {
                return false;
            }

            var count = 0;
            for (var i = 0; i < clips.Count; i++)
            {
                if (clips[i])
                {
                    count++;
                }
            }

            if (count == 0)
            {
                return false;
            }

            var selected = UnityEngine.Random.Range(0, count);
            for (var i = 0; i < clips.Count; i++)
            {
                if (clips[i] && selected-- == 0)
                {
                    clip = clips[i];
                    return true;
                }
            }

            return false;
        }
        /// <summary>停止所有有效 Sound 请求，不触发自然完成回调。不影响 Music 和 Voice；没有宿主时安全 no-op。</summary>
        public static void StopAllSound()
        {
            if (AudioManager.TryGetInstance(out var host))
            {
                host.StopSoundsAndPending();
            }
        }
        /// <summary>返回有效 Sound 请求的句柄快照，包含暂停音效；读取不创建宿主。</summary>
        public static AudioHandle[] GetPlayingSounds()
        {
            if (!AudioManager.TryGetInstance(out var host))
            {
                return Array.Empty<AudioHandle>();
            }

            var result = new List<AudioHandle>(host.SoundCount);
            host.CopySounds(result);
            return result.ToArray();
        }
        /// <summary>以当前 Sound 句柄覆盖调用方列表，避免创建快照数组。</summary>
        /// <param name="destination">接收句柄的非空列表，原有元素先被清除。</param>
        public static void CopyPlayingSounds(List<AudioHandle> destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            destination.Clear();
            if (AudioManager.TryGetInstance(out var host))
            {
                host.CopySounds(destination);
            }
        }
    }
}
