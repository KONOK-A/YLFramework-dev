using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Events;

namespace YLFramework.Audio
{
    /// <summary>唯一 Audio 播放宿主，管理缺省槽、命名通道、Sound 接纳与音源池，并驱动请求及缓存收尾。</summary>
    internal sealed class AudioManager : MonoSingletonHost<AudioManager>
    {
        private ObjectPool<AudioVoice> _pool;
        private readonly HashSet<AudioVoice> _sounds = new HashSet<AudioVoice>();
        private readonly HashSet<AudioVoice> _channelVoices = new HashSet<AudioVoice>();
        private readonly Dictionary<string, AudioChannel> _namedChannels = new Dictionary<string, AudioChannel>(StringComparer.Ordinal);
        private readonly Dictionary<AudioSource, AudioVoice> _borrowedSources = new Dictionary<AudioSource, AudioVoice>();
        private readonly HashSet<AudioSource> _ownedSources = new HashSet<AudioSource>();
        private readonly Dictionary<int, int> _clipCounts = new Dictionary<int, int>();
        private readonly List<int> _clipChecks = new List<int>();
        private readonly List<AudioHandle> _checks = new List<AudioHandle>();
        private readonly List<IUnregister> _subscriptions = new List<IUnregister>(12);
        private bool _isDisposing;
        private ulong _admissionOrder;
        private ulong _soundSession;
        private readonly List<AudioPlayRequest> _pending = new List<AudioPlayRequest>();
        private readonly ulong[] _pendingVersions = new ulong[3];
        internal AudioClipCache ClipCache { get; } = new AudioClipCache();
        internal bool IsAvailable => !IsHostReleased && !_isDisposing && !AudioKit.IsClosing && IsCurrentSoundSession;
        internal AudioMusicPlayback MusicPlayback
        {
            get; private set;
        }
        internal AudioVoice Music => MusicPlayback?.Current;
        internal AudioVoice Voice
        {
            get; private set;
        }
        internal int SoundCount => _sounds.Count;
        internal int CachedSoundCount => _pool?.CurStoreCount ?? 0;
        /// <summary>读取指定 Clip 实例当前计入 Sound 预算的请求数。</summary>
        internal int GetSoundCount(int clipId) => _clipCounts.TryGetValue(clipId, out var count) ? count : 0;
        private bool IsCurrentSoundSession => _soundSession == AudioKit.SoundSession;
        /// <summary>播放模式下将宿主放在框架 Audio 根节点，非播放模式不指定父节点。</summary>
        protected override Transform HostParent => Application.isPlaying ? YLFrameworkRoot.Instance.GetModuleRoot("Audio") : null;

        internal new static AudioManager Instance
        {
            get
            {
                if (AudioKit.IsClosing || !CanCreateHost(true))
                {
                    throw new InvalidOperationException("Audio playback is unavailable during cleanup.");
                }

                AudioKit.Initialize();
                try
                {
                    return GetInstance(true);
                }
                catch
                {
                    // 基类只回滚初始化标记；Audio 必须撤销部分订阅、池和 Source 后才允许重试。
                    try
                    {
                        AudioKit.Shutdown();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                    throw;
                }
            }
        }
        /// <summary>建立 Sound 池与缺省音乐/语音槽，并订阅分类设置；不主动创建 AudioListener。</summary>
        public override void OnSingletonInit()
        {
            _soundSession = AudioKit.SoundSession;
            _pool = new ObjectPool<AudioVoice>(() => new AudioVoice(this, AudioVoice.Kind.Sound),
                reset: voice => voice.ResetPlayback(), onDiscard: voice => voice.DestroySource(),
                maxPoolSize: Math.Max(1, AudioKit.SoundCacheCapacity), cacheOrder: PoolCacheOrder.Lifo);
            MusicPlayback = new AudioMusicPlayback(this);
            Voice = new AudioVoice(this, AudioVoice.Kind.Voice);
            var settings = AudioKit.Settings;
            _subscriptions.Add(settings.IsMusicOn.Register(RefreshMusicEnabled));
            _subscriptions.Add(settings.IsVoiceOn.Register(RefreshVoiceEnabled));
            _subscriptions.Add(settings.IsSoundOn.Register(value =>
            {
                if (!value)
                {
                    StopSoundsAndPending();
                }
            }));
            _subscriptions.Add(settings.MusicVolume.Register(RefreshMusicVolume));
            _subscriptions.Add(settings.VoiceVolume.Register(value =>
            {
                Voice.SetVolume(value);
                RefreshChannelVolume(AudioCategory.Voice);
            }));
            _subscriptions.Add(settings.SoundVolume.Register(SetSoundVolume));
            _subscriptions.Add(settings.IsMusicMuted.Register(RefreshMusicMute));
            _subscriptions.Add(settings.IsVoiceMuted.Register(value =>
            {
                Voice.SetMute(value);
                RefreshChannelMute(AudioCategory.Voice);
            }));
            _subscriptions.Add(settings.IsSoundMuted.Register(SetSoundMute));
            _subscriptions.Add(settings.MusicOutputGroup.Register(RefreshMusicOutputGroup));
            _subscriptions.Add(settings.VoiceOutputGroup.Register(RefreshVoiceOutputGroup));
            _subscriptions.Add(settings.SoundOutputGroup.Register(RefreshSoundOutputGroup));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SceneManager.sceneLoaded += OnSceneLoaded;
            CheckListener();
#endif
        }
        /// <summary>拒绝名称和显式持久化身份冲突后，登记属于本宿主的独立控制通道。</summary>
        internal AudioChannel RegisterChannel(string name, AudioCategory category, AudioChannelMode mode, string persistenceId)
        {
            if (_namedChannels.ContainsKey(name))
            {
                throw new InvalidOperationException("Audio channel name is already registered: " + name);
            }

            if (persistenceId != null)
            {
                foreach (var channel in _namedChannels.Values)
                {
                    if (channel.PersistenceId == persistenceId)
                    {
                        throw new InvalidOperationException("Audio channel storage identity is already registered: " + persistenceId);
                    }
                }
            }

            var result = new AudioChannel(this, name, category, mode, persistenceId);
            _namedChannels.Add(name, result);
            return result;
        }
        /// <summary>按分类及独占拓扑登记等待请求，取消旧等待者后复核重入版本。</summary>
        internal void AddPending(AudioPlayRequest request)
        {
            if (request.Category == AudioCategory.Sound && !AudioKit.Settings.IsSoundOn.Value)
            {
                request.End(AudioPlayEnd.Rejected);
                return;
            }
            var version = AdvancePendingVersion(request.Category, request.Channel);
            if (request.Channel?.Mode != AudioChannelMode.Overlap && (request.Channel != null || request.Category != AudioCategory.Sound))
            {
                CancelPending(request.Category, request.Channel, AudioPlayEnd.Replaced, incrementVersion: false);
            }

            if (!IsAvailable || version != PendingVersion(request.Category, request.Channel) || request.Channel != null && !request.Channel.IsValid)
            {
                request.End(AudioPlayEnd.Unavailable);
                return;
            }
            _pending.Add(request);
        }
        /// <summary>撤销原请求的等待登记，不改变其他请求。</summary>
        internal void RemovePending(AudioPlayRequest request) => _pending.Remove(request);
        /// <summary>读取缺省分类或指定通道的等待控制版本。</summary>
        internal ulong PendingVersion(AudioCategory category, AudioChannel channel) => channel?.PendingVersion ?? _pendingVersions[(int)category];
        /// <summary>提升对应等待控制版本，使重入前的外层提交失去当前资格。</summary>
        internal ulong AdvancePendingVersion(AudioCategory category, AudioChannel channel) => channel != null ? ++channel.PendingVersion : ++_pendingVersions[(int)category];
        /// <summary>取得分类与通道范围内的等待者快照，避免清理期间的重入影响遍历范围。</summary>
        internal AudioPlayRequest[] PendingSnapshot(AudioCategory category, AudioChannel channel, bool allChannels = false)
        {
            if (_pending.Count == 0)
            {
                return Array.Empty<AudioPlayRequest>();
            }

            var result = new List<AudioPlayRequest>();
            foreach (var request in _pending)
            {
                if (request.Category == category && (allChannels || ReferenceEquals(request.Channel, channel)))
                {
                    result.Add(request);
                }
            }

            return result.ToArray();
        }
        /// <summary>逐项终结原等待者快照，尝试完成全部清理后统一抛出收集的异常。</summary>
        internal static void EndPending(AudioPlayRequest[] snapshot, AudioPlayEnd reason)
        {
            List<Exception> failures = null;
            foreach (var request in snapshot)
            {
                try
                {
                    request.End(reason);
                }
                catch (Exception exception)
                {
                    (failures ?? (failures = new List<Exception>())).Add(exception);
                }
            }

            if (failures != null)
            {
                throw new AggregateException("Audio pending cleanup failed.", failures);
            }
        }
        /// <summary>按范围终结等待者，并按调用方要求使旧等待控制版本失效。</summary>
        internal void CancelPending(AudioCategory category, AudioChannel channel, AudioPlayEnd reason, bool allChannels = false, bool incrementVersion = true)
        {
            if (incrementVersion)
            {
                AdvancePendingVersion(category, channel);
            }

            if (_pending.Count == 0)
            {
                return;
            }

            EndPending(PendingSnapshot(category, channel, allChannels), reason);
        }
        /// <summary>先固定 Sound 等待范围，再停止活跃快照与原等待者，不影响清理重入的新播放。</summary>
        internal void StopSoundsAndPending()
        {
            AdvancePendingVersion(AudioCategory.Sound, null);
            var pending = PendingSnapshot(AudioCategory.Sound, null, true);
            try
            {
                StopAllSound();
            }
            finally
            {
                EndPending(pending, AudioPlayEnd.Stopped);
            }
        }
        /// <summary>仅撤销仍指向原通道实例的登记，不删除同名新通道。</summary>
        internal void UnregisterChannel(AudioChannel channel)
        {
            if (_namedChannels.TryGetValue(channel.Name, out var registered) && ReferenceEquals(channel, registered))
            {
                _namedChannels.Remove(channel.Name);
            }
        }
        /// <summary>登记自有音源，防止被外部借源入口重复接管。</summary>
        internal void RegisterOwnedSource(AudioSource source) => _ownedSources.Add(source);
        /// <summary>撤销自有音源登记，供原对象释放时使用。</summary>
        internal void UnregisterOwnedSource(AudioSource source) => _ownedSources.Remove(source);
        /// <summary>借用前要求音源有效、启用、停止，且未被框架拥有或其他请求借用。</summary>
        internal void ValidateBorrowedSource(AudioSource source)
        {
            if (!source)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!source.isActiveAndEnabled)
            {
                throw new ArgumentException("Borrowed Source must be active and enabled.", nameof(source));
            }

            if (_ownedSources.Contains(source) || _borrowedSources.ContainsKey(source) || source.isPlaying)
            {
                throw new InvalidOperationException("Source is owned, already borrowed or still playing. Explicitly stop and relinquish it first.");
            }
        }
        /// <summary>再次校验后登记音源到原 Voice 的独占借用关系。</summary>
        internal void BorrowSource(AudioSource source, AudioVoice voice)
        {
            ValidateBorrowedSource(source);
            _borrowedSources.Add(source, voice);
        }
        /// <summary>仅由当前登记的 Voice 撤销借用，保护重入中新取得音源的所有者。</summary>
        internal void ReturnBorrowedSource(AudioSource source, AudioVoice voice)
        {
            if (_borrowedSources.TryGetValue(source, out var current) && ReferenceEquals(current, voice))
            {
                _borrowedSources.Remove(source);
            }
        }
        /// <summary>在独立通道拓扑中创建播放；Sound 仍共用全局接纳预算，Music/Voice 不占缺省槽。</summary>
        internal AudioHandle PlayChannel(AudioChannel channel, AudioClip clip, in AudioPlaybackOptions options,
            AudioKit.PlaySoundModes mode, Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished,
            AudioResourceLease resource, AudioSource source)
        {
            if (resource != null && !resource.CanBind)
            {
                return default;
            }

            if (channel.Category == AudioCategory.Sound)
            {
                return PlaySound(clip, in options, mode, finished, started, false, default, null, resource, channel, source);
            }

            if (!ReferenceEquals(source, null))
            {
                ValidateBorrowedSource(source);
            }

            var voice = new AudioVoice(this, channel.Category == AudioCategory.Music ? AudioVoice.Kind.Music : AudioVoice.Kind.Voice);
            channel.Add(voice);
            _channelVoices.Add(voice);
            return voice.Begin(clip, in options, GetEnabled(voice.Category), GetCategoryVolume(voice.Category) * channel.Volume,
                GetCategoryMuted(voice.Category) || channel.IsMuted, started, finished, resource: resource, channel: channel, borrowedSource: source);
        }
        /// <summary>撤销命名 Music/Voice 声部登记并释放其自有音源，借入音源仅归还。</summary>
        internal void ReleaseChannelVoice(AudioVoice voice)
        {
            _channelVoices.Remove(voice);
            try
            {
                voice.ResetPlayback();
            }
            finally
            {
                voice.DestroySource();
            }
        }
        /// <summary>提交缺省 Music 或 Voice 请求，替换释放重入后复核原槽是否仍可接纳。</summary>
        internal AudioHandle PlaySlot(bool music, AudioClip clip, in AudioPlaybackOptions options,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished,
            bool spatial = false, Vector3 position = default, Transform target = null, AudioResourceLease resource = null)
        {
            if (music)
            {
                return MusicPlayback.Play(clip, in options, started, finished, resource);
            }

            var slot = Voice;
            slot.Stop(AudioRequestEnd.Replaced);
            if (!IsAvailable || slot.IsActive || resource != null && !resource.CanBind)
            {
                return default;
            }

            var settings = AudioKit.Settings;
            return slot.Begin(clip, in options, settings.IsVoiceOn.Value, settings.VoiceVolume.Value,
                settings.IsVoiceMuted.Value, started, finished, spatial, position, target, resource: resource);
        }
        /// <summary>按 Clip、全局容量和优先级接纳 Sound，提交计数与去重后启动原代次。</summary>
        internal AudioHandle PlaySound(AudioClip clip, in AudioPlaybackOptions options, AudioKit.PlaySoundModes mode,
            Action<AudioClip, AudioHandle> finished, Action<AudioClip, AudioHandle> started, bool spatial, Vector3 position, Transform target,
            AudioResourceLease resource = null, AudioChannel channel = null, AudioSource borrowedSource = null)
        {
            var capacity = AudioKit.CurrentSoundConcurrency;
            var channelEpoch = channel?.Epoch ?? 0;
            var clipId = clip.GetInstanceID();
            var clipLimit = AudioKit.GetEffectiveClipSoundLimit(clip);
            var clipCount = clipLimit > 0 ? GetSoundCount(clipId) : -1;
            var clipFull = clipLimit > 0 && clipCount >= clipLimit;
            var globalFull = capacity.Limit > 0 && _sounds.Count >= capacity.Limit;
            if (clipFull || globalFull)
            {
                if (capacity.OverflowPolicy == SoundOverflowPolicy.RejectNew)
                {
                    if (clipFull)
                    {
                        AudioKit.SoundCounters.ClipRejectedCount++;
                    }
                    else
                    {
                        AudioKit.SoundCounters.GlobalRejectedCount++;
                    }

                    return default;
                }
                // Clip 预算已满时只能替换同 Clip 请求，即使全局预算也满，仍不能误停无关声音。
                var victim = FindSoundVictim(capacity.OverflowPolicy, clipFull ? (int?)clipId : null);
                if (capacity.OverflowPolicy == SoundOverflowPolicy.StopLowestPriority && options.Priority < victim.Priority)
                {
                    AudioKit.SoundCounters.PriorityRejectedCount++;
                    return default;
                }
                var order = _admissionOrder;
                victim.Stop(AudioRequestEnd.Replaced);
                if (!IsAvailable || _admissionOrder != order || !AudioKit.CurrentSettings.IsSoundOn.Value || resource != null && !resource.CanBind ||
                    channel != null && (!channel.IsValid || channel.Epoch != channelEpoch))
                {
                    return default;
                }
                // Stop 的释放回调可同步改变计数，不能继续使用抢占前缓存的预算查询结果。
                clipCount = -1;
                AudioKit.SoundCounters.StolenCount++;
            }
            if (resource != null && !resource.CanBind)
            {
                return default;
            }

            if (!ReferenceEquals(borrowedSource, null))
            {
                ValidateBorrowedSource(borrowedSource);
            }

            if (!borrowedSource)
            {
                if (_pool.CurStoreCount > 0)
                {
                    AudioKit.SoundCounters.CacheHitCount++;
                }
                else
                {
                    AudioKit.SoundCounters.CacheMissCount++;
                }
            }
            var voice = borrowedSource ? new AudioVoice(this, AudioVoice.Kind.Sound, false) : _pool.Allocate();
            _sounds.Add(voice);
            _clipCounts[clipId] = (clipCount >= 0 ? clipCount : GetSoundCount(clipId)) + 1;
            voice.ClipId = clipId;
            voice.Priority = options.Priority;
            voice.AdmissionOrder = ++_admissionOrder;
            voice.Mode = mode;
            channel?.Add(voice);
            var generation = voice.Generation + 1;
            var channels = AudioKit.Channels;
            AudioHandle handle = default;
            try
            {
                var settings = AudioKit.Settings;
                handle = voice.Begin(clip, in options, true, settings.SoundVolume.Value * (channel?.Volume ?? 1f),
                    settings.IsSoundMuted.Value || (channel?.IsMuted ?? false),
                    started, finished, spatial, position, target, startImmediately: false, resource: resource, channel: channel, borrowedSource: borrowedSource);
                if (!handle.IsValid)
                {
                    return handle;
                }

                channels.Record(clipId, mode, Time.frameCount, handle);
                AudioKit.SoundCounters.AdmittedCount++;
                AudioKit.SoundCounters.PeakActiveCount = Math.Max(AudioKit.SoundCounters.PeakActiveCount, _sounds.Count);
                voice.StartConfigured(handle.Generation);
                return handle;
            }
            catch
            {
                if (IsCurrentSoundSession)
                {
                    AudioKit.SoundCounters.StartFailureCount++;
                }

                if (!ReferenceEquals(handle.Voice, null))
                {
                    channels.Rollback(clipId, mode, handle);
                }

                if (voice.Generation == generation)
                {
                    voice.Stop(AudioRequestEnd.Failed);
                }
                else if (!voice.IsActive && _sounds.Contains(voice))
                {
                    Recycle(voice);
                }

                throw;
            }
        }
        /// <summary>在全局或指定 Clip 范围内按保留策略选出应被停止的请求。</summary>
        private AudioVoice FindSoundVictim(SoundOverflowPolicy policy, int? clipId = null)
        {
            AudioVoice candidate = null;
            foreach (var sound in _sounds)
            {
                if (clipId.HasValue && sound.ClipId != clipId.Value)
                {
                    continue;
                }

                if (candidate == null ||
                    (policy == SoundOverflowPolicy.StopLowestPriority && sound.Priority < candidate.Priority) ||
                    (policy != SoundOverflowPolicy.StopLowestPriority || sound.Priority == candidate.Priority) &&
                    (policy == SoundOverflowPolicy.RejectNew ? sound.AdmissionOrder > candidate.AdmissionOrder : sound.AdmissionOrder < candidate.AdmissionOrder))
                {
                    candidate = sound;
                }
            }
            return candidate;
        }
        /// <summary>先协调各 Clip 上限，再协调全局容量，避免重复裁剪无关请求。</summary>
        internal void TrimSounds(AudioSoundConcurrency capacity)
        {
            if (!IsAvailable)
            {
                return;
            }
            // 先消除 Clip 局部超额，避免全局裁剪额外停止本不超额的其他 Clip 请求。
            _clipChecks.Clear();
            foreach (var id in _clipCounts.Keys)
            {
                _clipChecks.Add(id);
            }

            foreach (var id in _clipChecks)
            {
                TrimClipBudget(id, capacity.OverflowPolicy);
            }

            _clipChecks.Clear();
            TrimGlobalBudget(capacity);
        }
        /// <summary>实例规则变化后先裁剪对应 Clip，再复核全局上限。</summary>
        internal void TrimClipSounds(int clipId)
        {
            if (!IsAvailable)
            {
                return;
            }

            var capacity = AudioKit.CurrentSoundConcurrency;
            TrimClipBudget(clipId, capacity.OverflowPolicy);
            TrimGlobalBudget(capacity);
        }
        /// <summary>按当前实例有效上限和策略停止该 Clip 的超额请求。</summary>
        private void TrimClipBudget(int clipId, SoundOverflowPolicy policy)
        {
            var candidate = FindSoundVictim(policy, clipId);
            if (candidate == null)
            {
                return;
            }

            var limit = AudioKit.GetEffectiveClipSoundLimit(candidate.Clip);
            if (limit == 0)
            {
                return;
            }

            while (GetSoundCount(clipId) > limit)
            {
                FindSoundVictim(policy, clipId).Stop();
                AudioKit.SoundCounters.TrimmedCount++;
            }
        }
        /// <summary>按全局上限和保留策略停止超额 Sound 请求，零上限表示不限。</summary>
        private void TrimGlobalBudget(AudioSoundConcurrency capacity)
        {
            if (capacity.Limit == 0)
            {
                return;
            }

            while (_sounds.Count > capacity.Limit)
            {
                FindSoundVictim(capacity.OverflowPolicy).Stop();
                AudioKit.SoundCounters.TrimmedCount++;
            }
        }
        /// <summary>先撤销活跃 Sound 及 Clip 计数，再按其归属回收或释放声部。</summary>
        internal void Recycle(AudioVoice voice)
        {
            if (!_sounds.Remove(voice))
            {
                return;
            }

            var count = GetSoundCount(voice.ClipId);
            if (count == 1)
            {
                _clipCounts.Remove(voice.ClipId);
            }
            else
            {
                _clipCounts[voice.ClipId] = count - 1;
            }

            ReturnIdle(voice);
        }
        /// <summary>非池化声部直接释放；池化 Sound 仅在宿主和缓存预算允许时保留闲置音源。</summary>
        private void ReturnIdle(AudioVoice voice)
        {
            if (!voice.PooledSound)
            {
                try
                {
                    voice.ResetPlayback();
                }
                finally
                {
                    voice.DestroySource();
                }
            }
            else if (_isDisposing || AudioKit.SoundCacheCapacity == 0 || !voice.Source)
            {
                _pool.Discard(voice);
            }
            else
            {
                _pool.Recycle(voice);
                // Source 的 OnDisable 可在 Reset 中改为禁缓存；提交后复核且不重复 Reset。
                if (AudioKit.SoundCacheCapacity == 0)
                {
                    _pool.Clear();
                }
            }
        }
        /// <summary>调整闲置 Sound 池容量并及时释放超额缓存，不停止活跃请求。</summary>
        internal void ResizeSoundCache(int capacity)
        {
            // 场景宿主可能已被 Awake 接管，但尚未完成延迟的播放初始化，此时还没有池。
            if (!IsAvailable || _pool == null)
            {
                return;
            }

            if (capacity == 0)
            {
                _pool.Clear();
            }
            // 公共池的非正容量表示不限；Audio 的 0 由 Clear/Discard 明确禁缓存。
            _pool.MaxPoolSize = Math.Max(1, capacity);
        }
        /// <summary>准备指定目标数量的独立闲置音源，返回本次实际增加的缓存数。</summary>
        internal int PrewarmSounds(int count)
        {
            var original = _pool.CurStoreCount;
            PrepareSoundCapacity(checked(_sounds.Count + count));
            var voices = new List<AudioVoice>(count);
            try
            {
                // 初始化完成前持有全部分配，避免 LIFO 池反复交付同一个声部，造成虚假的预热数量。
                for (var i = 0; i < count; i++)
                {
                    var voice = _pool.Allocate();
                    voices.Add(voice);
                    voice.EnsureSource();
                }
            }
            finally
            {
                foreach (var voice in voices)
                {
                    ReturnIdle(voice);
                }
            }
            return Math.Max(0, _pool.CurStoreCount - original);
        }
        /// <summary>在预热前扩展 Sound 计数和帧检查集合容量，避免后续接纳重复扩容。</summary>
        private void PrepareSoundCapacity(int count)
        {
            _sounds.EnsureCapacity(count);
            _clipCounts.EnsureCapacity(count);
            if (_clipChecks.Capacity < count)
            {
                _clipChecks.Capacity = count;
            }

            if (_checks.Capacity < checked(count + 3))
            {
                _checks.Capacity = checked(count + 3);
            }
        }
        /// <summary>只向原 Sound 会话累计一次真实自有音源创建。</summary>
        internal void SoundSourceCreated()
        {
            if (!IsCurrentSoundSession)
            {
                return;
            }

            AudioKit.SoundCounters.OwnedSourceCount++;
            AudioKit.SoundCounters.SourceCreatedCount++;
        }
        /// <summary>只在原 Sound 会话扣减音源所有量并累计释放请求。</summary>
        internal void SoundSourceReleased()
        {
            if (!IsCurrentSoundSession)
            {
                return;
            }

            AudioKit.SoundCounters.OwnedSourceCount--;
            AudioKit.SoundCounters.SourceReleasedCount++;
        }
        /// <summary>把分类音量与各命名通道音量相乘后更新全部有效 Sound。</summary>
        private void SetSoundVolume(float value)
        {
            foreach (var voice in _sounds)
            {
                voice.SetVolume(value * (voice.Channel?.Volume ?? 1f));
            }
        }
        /// <summary>将 Sound 分类静音与各局部通道静音取 OR 后更新音源。</summary>
        private void SetSoundMute(bool value)
        {
            foreach (var voice in _sounds)
            {
                voice.SetMute(value || (voice.Channel?.IsMuted ?? false));
            }
        }
        /// <summary>取得请求当前的分类与局部通道音量乘积，不包含请求倍率或包络。</summary>
        internal float GetSharedVolume(AudioVoice voice)
        {
            return GetCategoryVolume(voice.Category) * (voice.Channel?.Volume ?? 1f);
        }
        /// <summary>合并请求当前的分类和局部通道静音原因。</summary>
        internal bool GetMuted(AudioVoice voice) => GetCategoryMuted(voice.Category) || (voice.Channel?.IsMuted ?? false);
        /// <summary>按内核分类读取当前分类音量倍率。</summary>
        private float GetCategoryVolume(AudioVoice.Kind category)
        {
            var settings = AudioKit.Settings;
            return category == AudioVoice.Kind.Sound ? settings.SoundVolume.Value : category == AudioVoice.Kind.Music ? settings.MusicVolume.Value : settings.VoiceVolume.Value;
        }
        /// <summary>按内核分类读取当前分类静音状态。</summary>
        private bool GetCategoryMuted(AudioVoice.Kind category)
        {
            var settings = AudioKit.Settings;
            return category == AudioVoice.Kind.Sound ? settings.IsSoundMuted.Value : category == AudioVoice.Kind.Music ? settings.IsMusicMuted.Value : settings.IsVoiceMuted.Value;
        }
        /// <summary>读取所属分类的启用开关，不混同于静音或暂停。</summary>
        private bool GetEnabled(AudioVoice.Kind category)
        {
            var settings = AudioKit.Settings;
            return category == AudioVoice.Kind.Music ? settings.IsMusicOn.Value : category == AudioVoice.Kind.Voice ? settings.IsVoiceOn.Value : settings.IsSoundOn.Value;
        }
        /// <summary>按原句柄快照刷新缺省和命名 Music/Voice 的分类启用状态，并汇总刷新异常。</summary>
        private void RefreshEnabled(AudioCategory category)
        {
            if (!IsAvailable)
            {
                return;
            }

            var snapshot = new List<AudioHandle>(_channelVoices.Count);
            foreach (var voice in _channelVoices)
            {
                if (voice.Channel.Category == category)
                {
                    snapshot.Add(voice.Handle);
                }
            }

            List<Exception> failures = null;
            try
            {
                if (category == AudioCategory.Music)
                {
                    MusicPlayback.SetEnabled(AudioKit.CurrentSettings.IsMusicOn.Value);
                }
                else
                {
                    Voice.SetEnabled(AudioKit.CurrentSettings.IsVoiceOn.Value);
                }
            }
            catch (Exception exception)
            {
                (failures ?? (failures = new List<Exception>())).Add(exception);
            }
            foreach (var handle in snapshot)
            {
                try
                {
                    if (handle.IsValid)
                    {
                        handle.Voice.SetEnabled(GetEnabled(handle.Voice.Category));
                    }
                }
                catch (Exception exception)
                {
                    (failures ?? (failures = new List<Exception>())).Add(exception);
                }
            }

            if (failures != null)
            {
                throw new AggregateException("Audio category enabled refresh failed.", failures);
            }
        }
        /// <summary>刷新指定分类的命名 Music/Voice 请求，保留各通道独立音量。</summary>
        private void RefreshChannelVolume(AudioCategory category)
        {
            if (!IsAvailable)
            {
                return;
            }

            foreach (var voice in _channelVoices)
            {
                if (voice.Channel.Category == category)
                {
                    voice.SetVolume(GetSharedVolume(voice));
                }
            }
        }
        /// <summary>刷新指定分类命名请求的综合静音状态。</summary>
        private void RefreshChannelMute(AudioCategory category)
        {
            if (!IsAvailable)
            {
                return;
            }

            foreach (var voice in _channelVoices)
            {
                if (voice.Channel.Category == category)
                {
                    voice.SetMute(GetMuted(voice));
                }
            }
        }
        /// <summary>响应 Music 开关通知，并以当前设置刷新原有效请求。</summary>
        private void RefreshMusicEnabled(bool _) => RefreshEnabled(AudioCategory.Music);
        /// <summary>响应 Voice 开关通知，并以当前设置刷新原有效请求。</summary>
        private void RefreshVoiceEnabled(bool _) => RefreshEnabled(AudioCategory.Voice);
        /// <summary>以当前 Music 音量更新双声部及同分类命名通道。</summary>
        private void RefreshMusicVolume(float _)
        {
            if (IsAvailable)
            {
                MusicPlayback.SetVolume(AudioKit.CurrentSettings.MusicVolume.Value);
                RefreshChannelVolume(AudioCategory.Music);
            }
        }
        /// <summary>以当前 Music 静音状态更新双声部及同分类命名通道。</summary>
        private void RefreshMusicMute(bool _)
        {
            if (IsAvailable)
            {
                MusicPlayback.SetMute(AudioKit.CurrentSettings.IsMusicMuted.Value);
                RefreshChannelMute(AudioCategory.Music);
            }
        }
        // 较早的监听器可同步改写属性或释放宿主，因此轮到本监听器时须读取当前设置而非旧通知值。
        /// <summary>刷新 Music 双声部和命名通道中继承分类路由的请求。</summary>
        private void RefreshMusicOutputGroup(AudioMixerGroup _)
        {
            if (IsAvailable)
            {
                MusicPlayback.SetInheritedOutputGroup(AudioKit.CurrentSettings.MusicOutputGroup.Value);
            }

            RefreshChannelOutput(AudioCategory.Music);
        }
        /// <summary>刷新缺省 Voice 槽和命名通道中继承分类路由的请求。</summary>
        private void RefreshVoiceOutputGroup(AudioMixerGroup _)
        {
            if (IsAvailable)
            {
                Voice.SetInheritedOutputGroup(AudioKit.CurrentSettings.VoiceOutputGroup.Value);
            }

            RefreshChannelOutput(AudioCategory.Voice);
        }
        /// <summary>将当前分类默认组变化传递给对应的命名 Music/Voice 请求。</summary>
        private void RefreshChannelOutput(AudioCategory category)
        {
            if (!IsAvailable)
            {
                return;
            }

            foreach (var voice in _channelVoices)
            {
                if (voice.Channel.Category == category)
                {
                    voice.SetInheritedOutputGroup(GetOutputGroup(voice));
                }
            }
        }
        /// <summary>按最新 Sound 默认组刷新全部继承路由的音效请求。</summary>
        private void RefreshSoundOutputGroup(AudioMixerGroup _)
        {
            if (!IsAvailable)
            {
                return;
            }

            var group = AudioKit.CurrentSettings.SoundOutputGroup.Value;
            foreach (var voice in _sounds)
            {
                voice.SetInheritedOutputGroup(group);
            }
        }
        /// <summary>按 Voice 分类取得当前默认输出组，不解析请求自己的路由覆盖。</summary>
        internal AudioMixerGroup GetOutputGroup(AudioVoice voice)
        {
            var settings = AudioKit.Settings;
            return voice.IsSound ? settings.SoundOutputGroup.Value :
                voice.Category == AudioVoice.Kind.Music ? settings.MusicOutputGroup.Value : settings.VoiceOutputGroup.Value;
        }
        /// <summary>向目标列表追加当前 Sound 原句柄，供外部读取或内部快照清理。</summary>
        internal void CopySounds(List<AudioHandle> destination)
        {
            foreach (var voice in _sounds)
            {
                destination.Add(voice.Handle);
            }
        }
        /// <summary>按进入时句柄快照停止 Sound，完成全部尝试后汇总清理异常。</summary>
        internal void StopAllSound(AudioRequestEnd reason = AudioRequestEnd.Stopped)
        {
            if (_sounds.Count == 0)
            {
                return;
            }
            // 释放责任可重入播放；只终结本次调用进入时的租约，不能误停新代次。
            var snapshot = new List<AudioHandle>(_sounds.Count);
            CopySounds(snapshot);
            List<Exception> failures = null;
            foreach (var handle in snapshot)
            {
                try
                {
                    if (handle.Voice.IsActive && handle.Voice.Generation == handle.Generation)
                    {
                        handle.Voice.Stop(reason);
                    }
                }
                catch (Exception exception)
                {
                    (failures ?? (failures = new List<Exception>())).Add(exception);
                }
            }

            if (failures != null)
            {
                throw new AggregateException("AudioKit sound cleanup failed.", failures);
            }
        }
        /// <summary>逐帧推进等待请求、Clip 淘汰、播放检查、自动暂停和淡变，再清理过期去重记录。</summary>
        private void Update()
        {
            if (Music == null || !IsAvailable)
            {
                return;
            }

            if (_pending.Count > 0)
            {
                foreach (var request in _pending.ToArray())
                {
                    try
                    {
                        request.Tick();
                    }
                    catch (Exception exception)
                    {
                        request.Fail(exception);
                    }
                }
            }

            ClipCache.Tick(Time.realtimeSinceStartupAsDouble);
            _checks.Clear();
            var musicEpoch = MusicPlayback.Epoch;
            MusicPlayback.CopyTo(_checks);
            if (Voice.IsActive)
            {
                _checks.Add(Voice.Handle);
            }

            CopySounds(_checks);
            foreach (var voice in _channelVoices)
            {
                if (voice.IsActive)
                {
                    _checks.Add(voice.Handle);
                }
            }

            for (var i = 0; i < _checks.Count; i++)
            {
                var handle = _checks[i];
                if (!handle.IsValid)
                {
                    continue;
                }

                try
                {
                    handle.Voice.CheckFinished();
                    if (handle.IsValid)
                    {
                        handle.Voice.RefreshAutomaticPause(Time.timeScale == 0f);
                    }

                    if (handle.IsValid)
                    {
                        handle.Voice.TickFade(Time.deltaTime, Time.unscaledDeltaTime);
                    }
                }
                catch (Exception exception)
                {
                    try
                    {
                        if (handle.IsValid)
                        {
                            handle.Voice.Stop(AudioRequestEnd.Failed);
                        }
                    }
                    catch (Exception cleanup)
                    {
                        Debug.LogException(cleanup);
                    }
                    Debug.LogException(exception);
                }
            }
            _checks.Clear();
            if (IsAvailable && musicEpoch == MusicPlayback.Epoch)
            {
                MusicPlayback.Tick(Time.deltaTime, Time.unscaledDeltaTime);
            }

            if (IsAvailable)
            {
                AudioKit.Channels.Prune(Time.frameCount);
            }
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>开发环境加载场景后检查是否存在有效监听器，不自动补建。</summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => CheckListener();
        /// <summary>播放模式缺少启用的 AudioListener 时输出配置警告。</summary>
        private void CheckListener()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener.isActiveAndEnabled)
                {
                    return;
                }
            }

            Debug.LogWarning("AudioKit: no active scene AudioListener. Configure a listener on the camera or player; AudioKit does not create one.", this);
        }
#endif
        /// <summary>释放缓存宿主及场景中的 Audio 宿主，最后重置宿主引用并汇总异常。</summary>
        internal static void Shutdown()
        {
            List<Exception> failures = null;
            // 单个宿主失败不阻断其余宿主的释放尝试。
            void Release(AudioManager host)
            {
                try
                {
                    host.ReleaseHost();
                }
                catch (Exception exception)
                {
                    (failures ?? (failures = new List<Exception>())).Add(exception);
                }
            }
            if (!ReferenceEquals(CachedHost, null))
            {
                Release(CachedHost);
            }

            foreach (var host in FindObjectsByType<AudioManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Release(host);
            }

            ResetHostReference();
            if (failures != null)
            {
                throw new AggregateException("AudioKit host cleanup failed.", failures);
            }
        }
        /// <summary>撤销订阅，以 Shutdown 结束原等待和播放，归还 Clip 缓存及自有音源；清理异常最后汇总。</summary>
        protected override void OnHostRelease()
        {
            if (_isDisposing)
            {
                return;
            }

            _isDisposing = true;
            List<Exception> failures = null;
            // 每项清理分别记录失败，让其余拥有的责任仍有机会归还。
            void Cleanup(Action operation)
            {
                try
                {
                    operation();
                }
                catch (Exception exception)
                {
                    (failures ?? (failures = new List<Exception>())).Add(exception);
                }
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SceneManager.sceneLoaded -= OnSceneLoaded;
#endif
            foreach (var subscription in _subscriptions)
            {
                Cleanup(subscription.Unregister);
            }

            _subscriptions.Clear();
            foreach (var request in _pending.ToArray())
            {
                Cleanup(() => request.End(AudioPlayEnd.Shutdown));
            }

            _pending.Clear();
            foreach (var channel in new List<AudioChannel>(_namedChannels.Values))
            {
                Cleanup(() => channel.Release(AudioRequestEnd.Shutdown));
            }

            _namedChannels.Clear();
            Cleanup(() => StopAllSound(AudioRequestEnd.Shutdown));
            if (MusicPlayback != null)
            {
                Cleanup(MusicPlayback.DestroySources);
            }

            if (Voice != null)
            {
                Cleanup(Voice.DestroySource);
            }

            if (_pool != null)
            {
                Cleanup(_pool.Dispose);
            }

            Cleanup(ClipCache.Dispose);
            // Update 遍历快照时也可能被回调释放宿主，快照由原 Update 自己清空，避免破坏其索引。
            if (failures != null)
            {
                throw new AggregateException("AudioKit source cleanup failed.", failures);
            }
        }
        /// <summary>仅由当前未释放宿主启动 Audio 退出流程，防止旧宿主的迟到消息关闭新会话。</summary>
        protected override void OnApplicationQuit()
        {
            // 已释放或旧会话宿主的迟到消息不能关闭新会话，亦不能重置公共宿主 gate。
            if (IsHostReleased || !IsCurrentSoundSession)
            {
                return;
            }

            base.OnApplicationQuit();
            AudioKit.BeginQuit();
        }
    }
}
