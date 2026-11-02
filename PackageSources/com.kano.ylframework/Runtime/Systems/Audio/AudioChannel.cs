using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>请求使用的既有分类设置与 Sound 预算；不决定独占或重叠拓扑。</summary>
    public enum AudioCategory
    {
        /// <summary>Music 分类设置；命名请求不占缺省双声部。</summary>
        Music,
        /// <summary>Voice 分类设置；命名请求不占缺省独占槽。</summary>
        Voice,
        /// <summary>Sound 分类设置、帧去重及全局/Clip 并发预算。</summary>
        Sound
    }
    /// <summary>Exclusive 替换本通道请求；Overlap 为每次播放分配独立 Source。</summary>
    public enum AudioChannelMode
    {
        /// <summary>新请求替换本通道原请求，支持受控 Source 借用。</summary>
        Exclusive,
        /// <summary>每个请求独立播放和控制，不支持借用同一 Source。</summary>
        Overlap
    }

    /// <summary>当前 Audio 会话内的命名控制身份。Dispose 只撤销自己的登记与请求；旧身份不能控制同名新通道。</summary>
    /// <remarks>所有操作须在 Unity 主线程调用。分类音量与本通道音量相乘，静音取 OR；分类 On/Off 语义保持不变。</remarks>
    public sealed partial class AudioChannel : IDisposable
    {
        internal readonly AudioManager Owner;
        private readonly HashSet<AudioVoice> _requests = new HashSet<AudioVoice>();
        private bool _isDisposed;
        private bool _isPaused;
        private bool _isMuted;
        private float _volume = 1f;
        private ulong _pauseVersion;
        private ulong _volumeVersion;
        private ulong _muteVersion;
        internal ulong Epoch
        {
            get; private set;
        }
        internal ulong PendingVersion;
        /// <summary>区分大小写的登记名，不用作隐式存储身份。</summary>
        public string Name
        {
            get;
        }
        /// <summary>显式稳定持久化身份；null 表示不读写任何通道偏好键。</summary>
        public string PersistenceId
        {
            get;
        }
        /// <summary>通道使用的既有分类设置。</summary>
        public AudioCategory Category
        {
            get;
        }
        /// <summary>固定的独占/重叠拓扑。</summary>
        public AudioChannelMode Mode
        {
            get;
        }
        /// <summary>登记是否仍属于可用的原 Audio 会话。</summary>
        public bool IsValid => !_isDisposed && Owner && Owner.IsAvailable;
        /// <summary>本通道是否显式暂停，不代表每个请求的其他暂停原因。</summary>
        public bool IsPaused => IsValid && _isPaused;
        /// <summary>有效请求数，包含暂停和等待分类启用的请求。</summary>
        public int RequestCount => IsValid ? _requests.Count : 0;
        /// <summary>局部音量实际变化且已更新原请求后通知；同值不通知。Dispose 撤销订阅，不投递初始值。</summary>
        public event Action<float> VolumeChanged;
        /// <summary>局部静音实际变化且已更新原请求后通知；不表示分类静音变化。</summary>
        public event Action<bool> MutedChanged;
        /// <summary>局部 [0,1] 音量，不回写缺省分类设置。</summary>
        public float Volume
        {
            get => _volume;
            set
            {
                RequireValid();
                if (!AudioSoundOptions.IsFinite(value) || value < 0f || value > 1f)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (PersistenceId != null)
                {
                    PlayerPrefs.SetFloat(StorageKey("Volume"), value);
                }

                if (_volume == value)
                {
                    return;
                }

                _volume = value;
                var version = ++_volumeVersion;
                foreach (var request in _requests)
                {
                    request.SetVolume(Owner.GetSharedVolume(request));
                }

                if (VolumeChanged != null)
                {
                    Notify(VolumeChanged, value, () => IsValid && _volumeVersion == version);
                }
            }
        }
        /// <summary>局部静音，不停止、重播或暂停请求；仅显式 PersistenceId 才持久化。</summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                RequireValid();
                if (PersistenceId != null)
                {
                    PlayerPrefs.SetInt(StorageKey("Mute"), value ? 1 : 0);
                }

                if (_isMuted == value)
                {
                    return;
                }

                _isMuted = value;
                var version = ++_muteVersion;
                foreach (var request in _requests)
                {
                    request.SetMute(Owner.GetMuted(request));
                }

                if (MutedChanged != null)
                {
                    Notify(MutedChanged, value, () => IsValid && _muteVersion == version);
                }
            }
        }
        /// <summary>绑定原宿主与固定拓扑，仅有显式持久化身份时读取独立音量和静音偏好。</summary>
        internal AudioChannel(AudioManager owner, string name, AudioCategory category, AudioChannelMode mode, string persistenceId)
        {
            Owner = owner;
            Name = name;
            Category = category;
            Mode = mode;
            PersistenceId = persistenceId;
            if (persistenceId == null)
            {
                return;
            }

            var volume = PlayerPrefs.GetFloat(StorageKey("Volume"), 1f);
            _volume = AudioSoundOptions.IsFinite(volume) && volume >= 0f && volume <= 1f ? volume : 1f;
            _isMuted = PlayerPrefs.GetInt(StorageKey("Mute"), 0) != 0;
        }
        /// <summary>以显式持久化身份生成设置键，不将通道显示名隐式用于存储。</summary>
        private string StorageKey(string setting) => "YLFramework.Audio.Channel:" + PersistenceId + ":" + setting;
        /// <summary>逐个通知并汇总订阅者异常；重入改变身份或版本后停止投递旧通知。</summary>
        private static void Notify<T>(Action<T> listeners, T value, Func<bool> current)
        {
            if (listeners == null)
            {
                return;
            }

            List<Exception> failures = null;
            foreach (Action<T> listener in listeners.GetInvocationList())
            {
                if (!current())
                {
                    break;
                }

                try
                {
                    listener(value);
                }
                catch (Exception exception)
                {
                    (failures ?? (failures = new List<Exception>())).Add(exception);
                }
            }
            if (failures != null)
            {
                throw new AggregateException("Audio channel setting notification failed.", failures);
            }
        }
        /// <summary>要求登记仍属于原可用宿主，过期身份明确拒绝提交和设置写入。</summary>
        internal void RequireValid()
        {
            if (!IsValid)
            {
                throw new InvalidOperationException("Audio channel has expired.");
            }
        }
        /// <summary>校验登记或持久化身份为无首尾空白的稳定 ASCII 名称。</summary>
        internal static void ValidateIdentity(string identity, string parameter)
        {
            if (string.IsNullOrWhiteSpace(identity) || identity != identity.Trim())
            {
                throw new ArgumentException("A nonempty, unpadded identity is required.", parameter);
            }

            foreach (var c in identity)
            {
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-' || c == '/'))
                {
                    throw new ArgumentException("Use stable ASCII letters, digits, '.', '_', '-' or '/'.", parameter);
                }
            }
        }
        /// <summary>播放调用方保活的 Clip。null 选项为普通非循环 Defaults；非法配置不会替换旧请求。</summary>
        /// <param name="clip">由调用方保活至请求结束的 Clip。</param>
        /// <param name="options">本次捕获的配置，后续修改不影响已提交请求。</param>
        /// <param name="onStarted">首次提交 Source.Play 后的实际 Clip 和原句柄，可在入口返回前同步通知。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄；停止或替换不通知。</param>
        public AudioHandle Play(AudioClip clip, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlayWithSnapshot(clip, AudioPlaybackOptions.Capture(options), onStarted, onFinished);
        /// <summary>受控借用有效、已停止且调用方让渡独占控制的 Source；重复借用和框架自有 Source 明确拒绝。</summary>
        /// <remarks>仅 Exclusive 支持。不调用 PlayOneShot，不移动/停用/销毁对象，不改空间配置或曲线。
        /// 只配置 clip/loop/pitch/volume/mute/输出路由；Inherit 使用分类路由。释放比较最后写入值再恢复，不重建先前播放历史。
        /// 调用方在租约期间不得直接 Play/Pause/Stop（同 Clip 的外部重播无法可靠识别）；外部换 Clip 会放弃归属及恢复。</remarks>
        /// <param name="source">调用方显式让渡独占控制的有效、启用且已停止音源。</param>
        /// <param name="clip">由调用方保活的 Clip，不随借源结束自动销毁。</param>
        /// <param name="options">本次捕获的配置；null 使用非循环 Defaults，空间字段不改写借入音源。</param>
        /// <param name="onStarted">首次提交 Source.Play 后的实际 Clip 和原句柄，可同步重入控制。</param>
        /// <param name="onFinished">自然结束且恢复前的实际 Clip 和原句柄。</param>
        public AudioHandle PlayBorrowed(AudioSource source, AudioClip clip, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlayWithSnapshot(clip, AudioPlaybackOptions.Capture(options), onStarted, onFinished, source: source, borrowed: true);
        /// <summary>校验并协调独占替换、Sound 去重和借源资格，再将快照交宿主接纳。</summary>
        internal AudioHandle PlayWithSnapshot(AudioClip clip, AudioPlaybackOptions options,
            Action<AudioClip, AudioHandle> started = null, Action<AudioClip, AudioHandle> finished = null,
            AudioResourceLease resource = null, AudioSource source = null, bool borrowed = false, bool namedCommit = false)
        {
            try
            {
                RequireValid();
                resource?.Validate();
                if (borrowed)
                {
                    if (Mode != AudioChannelMode.Exclusive)
                    {
                        throw new InvalidOperationException("Borrowing requires an exclusive channel.");
                    }

                    Owner.ValidateBorrowedSource(source);
                }
                if (!clip)
                {
                    return default;
                }

                if (Category != AudioCategory.Sound)
                {
                    options.Priority = 0;
                    options.PlaySoundMode = null;
                }
                options.Validate(false);
                options.ValidateClip(clip);
                if (Category == AudioCategory.Sound && !AudioKit.Settings.IsSoundOn.Value)
                {
                    return default;
                }

                var mode = options.PlaySoundMode ?? AudioKit.PlaySoundMode;
                if (Category == AudioCategory.Sound && !AudioKit.Channels.CanPlay(clip.GetInstanceID(), mode, Time.frameCount))
                {
                    AudioKit.SoundCounters.DedupRejectedCount++;
                    return default;
                }
                var epoch = ++Epoch;
                if (!namedCommit && Mode == AudioChannelMode.Exclusive)
                {
                    Owner.CancelPending(Category, this, AudioPlayEnd.Replaced);
                }

                if (!IsValid || Epoch != epoch || resource != null && !resource.CanBind)
                {
                    return default;
                }

                if (Mode == AudioChannelMode.Exclusive)
                {
                    StopRequests(AudioRequestEnd.Replaced);
                }

                if (!IsValid || Epoch != epoch)
                {
                    return default;
                }

                if (Category == AudioCategory.Sound)
                {
                    if (!AudioKit.CurrentSettings.IsSoundOn.Value)
                    {
                        return default;
                    }

                    if (!AudioKit.Channels.CanPlay(clip.GetInstanceID(), mode, Time.frameCount))
                    {
                        AudioKit.SoundCounters.DedupRejectedCount++;
                        return default;
                    }
                }
                if (borrowed)
                {
                    Owner.ValidateBorrowedSource(source);
                }

                return Owner.PlayChannel(this, clip, in options, mode, started, finished, resource, source);
            }
            finally
            {
                if (resource != null && !resource.IsBound)
                {
                    resource.Dispose();
                }
            }
        }
        /// <summary>登记已进入本通道播放路径的原声部。</summary>
        internal void Add(AudioVoice voice) => _requests.Add(voice);
        /// <summary>原声部终结时撤销本通道追踪。</summary>
        internal void Remove(AudioVoice voice) => _requests.Remove(voice);
        /// <summary>追加本通道活跃句柄，用代次快照隔开后续清理与重入播放。</summary>
        internal void CopyTo(List<AudioHandle> destination)
        {
            foreach (var voice in _requests)
            {
                if (voice.IsActive)
                {
                    destination.Add(voice.Handle);
                }
            }
        }
        /// <summary>按原句柄快照逐项停止请求，尝试全部清理后汇总异常，不误停新代次。</summary>
        private void StopRequests(AudioRequestEnd reason)
        {
            var snapshot = new List<AudioHandle>(_requests.Count);
            CopyTo(snapshot);
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
                throw new AggregateException("Audio channel cleanup failed.", failures);
            }
        }
        /// <summary>停止调用进入时的请求，不停其他通道或清理重入中创建的新代次。</summary>
        public void Stop()
        {
            if (!IsValid)
            {
                return;
            }

            Epoch++;
            PendingVersion++;
            var pending = Owner.PendingSnapshot(Category, this);
            try
            {
                StopRequests(AudioRequestEnd.Stopped);
            }
            finally
            {
                AudioManager.EndPending(pending, AudioPlayEnd.Stopped);
            }
        }
        /// <summary>局部暂停也应用于后续本通道请求；不会覆盖逐请求手动暂停。</summary>
        public void Pause()
        {
            if (!IsValid)
            {
                return;
            }

            _pauseVersion++;
            _isPaused = true;
            foreach (var voice in _requests)
            {
                voice.SetChannelPaused(true);
            }
        }
        /// <summary>只解除本通道暂停，不解除请求的手动或自动暂停。</summary>
        public void Resume()
        {
            if (!IsValid)
            {
                return;
            }

            var pauseVersion = ++_pauseVersion;
            _isPaused = false;
            var snapshot = new List<AudioHandle>(_requests.Count);
            CopyTo(snapshot);
            List<Exception> failures = null;
            foreach (var handle in snapshot)
            {
                if (!IsValid || _pauseVersion != pauseVersion || _isPaused)
                {
                    break;
                }

                try
                {
                    if (handle.IsValid)
                    {
                        handle.Voice.SetChannelPaused(false);
                    }
                }
                catch (Exception exception)
                {
                    (failures ?? (failures = new List<Exception>())).Add(exception);
                }
            }
            if (failures != null)
            {
                throw new AggregateException("Audio channel resume failed.", failures);
            }
        }
        /// <summary>撤销登记后再结束原请求；不删除持久化值。重复调用安全。</summary>
        public void Dispose() => Release(AudioRequestEnd.Stopped);
        /// <summary>先使通道和通知失效并撤销登记，再按指定原因终结原等待及播放。</summary>
        internal void Release(AudioRequestEnd reason)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Epoch++;
            VolumeChanged = null;
            MutedChanged = null;
            Owner.UnregisterChannel(this);
            Owner.CancelPending(Category, this, (AudioPlayEnd)reason);
            StopRequests(reason);
        }
    }
}
