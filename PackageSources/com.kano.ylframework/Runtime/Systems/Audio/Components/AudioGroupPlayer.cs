using System;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Audio
{
    /// <summary>显式的组自动播放时机；不改变已有 Emitter/MusicPlayer 的首次 Start 行为。</summary>
    public enum AudioGroupAutoPlay
    {
        /// <summary>仅由显式 Play 或 PlayGroup 入口触发播放。</summary>
        Manual,
        /// <summary>只在组件首次 Start 时自动播放一次。</summary>
        FirstStart,
        /// <summary>首次 Start 及之后每次重新启用时自动播放。</summary>
        EveryEnable
    }

    /// <summary>具名素材组播放所有者；自有音源为 2D，借源保留调用方空间配置，复用唯一内核与显式命名通道。</summary>
    /// <remarks>素材组与控制通道是两种身份。失活取消 pending 并停止自有播放；不控制其他组件的通道。
    /// 均匀/加权只抽选一次，坏组和加载失败不停止旧播放。自定义 Music 通道不占缺省双声部。</remarks>
    [AddComponentMenu("YLFramework/Audio/Group Player")]
    public sealed class AudioGroupPlayer : MonoBehaviour
    {
        [SerializeField] private AudioClipLibrary library;
        [SerializeField] private string groupName;
        [Tooltip("必须显式配置唯一通道名；不以对象名或实例哈希作为登记/存储身份。")]
        [SerializeField] private string channelName;
        [SerializeField] private AudioCategory category = AudioCategory.Sound;
        [SerializeField] private AudioChannelMode channelMode = AudioChannelMode.Exclusive;
        [Tooltip("可选：明确让渡控制的有效、已停止 Source，仅 Exclusive 支持。空值使用框架音源；不移动或销毁借入对象。")]
        [SerializeField] private AudioSource borrowedSource;
        [Tooltip("空值不持久化；仅显式稳定 ID 才读写独立通道偏好。")]
        [SerializeField] private string persistenceId;
        [SerializeField] private AudioGroupAutoPlay autoPlay = AudioGroupAutoPlay.Manual;
        [SerializeField] private bool loadAsync = true;
        [SerializeField] private AudioSoundOptions options = new AudioSoundOptions();
        [SerializeField] private UnityEvent onStarted = new UnityEvent();
        private AudioChannel _channel;
        private IAudioClipProvider _provider;
        private ulong _version;
        private ulong _submission;
        private bool _hasStarted;
        private bool _isDestroyed;

        /// <summary>已登记的原控制通道；未播放/已退出时为 null 或失效身份。</summary>
        public AudioChannel Channel => _channel;
        /// <summary>最近有效提交的命名请求；保留错误/终态供诊断，不代表当前一定在播放。</summary>
        public AudioPlayRequest LastRequest
        {
            get; private set;
        }
        /// <summary>最近开始的原句柄；失效后不能控制复用代次。</summary>
        public AudioHandle LastHandle
        {
            get; private set;
        }
        /// <summary>原句柄已归入本通道后触发；允许同步 Stop/失活/销毁。</summary>
        public UnityEvent Started => onStarted;

        /// <summary>设置 ExternalProvider 条目的显式后端；先停止本所有者的旧请求，不注册全局后端。</summary>
        /// <param name="provider">之后选曲使用的后端；null 表示不提供 ExternalProvider 候选来源。</param>
        public void SetProvider(IAudioClipProvider provider)
        {
            Stop();
            _provider = provider;
        }
        /// <summary>记录首次启动完成，并按自动播放模式决定是否提交首个请求。</summary>
        private void Start()
        {
            _hasStarted = true;
            if (autoPlay != AudioGroupAutoPlay.Manual)
            {
                Play();
            }
        }
        /// <summary>首次 Start 之后，仅 EveryEnable 模式在重启用时自动播放。</summary>
        private void OnEnable()
        {
            if (_hasStarted && autoPlay == AudioGroupAutoPlay.EveryEnable)
            {
                Play();
            }
        }
        /// <summary>按 Inspector 的组名抽选播放；无参入口可绑定 UnityEvent。</summary>
        public void Play() => PlayGroup(groupName);

        /// <summary>按明确组名抽选并提交；不是控制通道名称。失活组件不提交在途请求。</summary>
        /// <param name="name">素材库内区分大小写的组名。</param>
        public void PlayGroup(string name)
        {
            if (_isDestroyed || !isActiveAndEnabled || !library || !library.TrySelect(name, out var selection, _provider))
            {
                return;
            }

            var snapshot = AudioPlaybackOptions.Capture(options);
            if (category != AudioCategory.Sound)
            {
                snapshot.Priority = 0;
                snapshot.PlaySoundMode = null;
            }
            snapshot.Validate(false);
            if (!selection.IsNamed)
            {
                snapshot.ValidateClip(selection.Clip);
            }

            if ((uint)autoPlay > (uint)AudioGroupAutoPlay.EveryEnable)
            {
                throw new ArgumentOutOfRangeException(nameof(autoPlay));
            }

            var borrowed = AudioOutputRoute.HasReference(borrowedSource);
            if (borrowed)
            {
                if (channelMode != AudioChannelMode.Exclusive)
                {
                    throw new InvalidOperationException("Borrowing requires an exclusive channel.");
                }

                if (!borrowedSource || !borrowedSource.isActiveAndEnabled)
                {
                    throw new ArgumentException("Borrowed Source must be live and enabled.", nameof(borrowedSource));
                }
            }
            if (_channel == null || !_channel.IsValid)
            {
                _channel = AudioKit.RegisterChannel(channelName, category, channelMode, string.IsNullOrEmpty(persistenceId) ? null : persistenceId);
            }

            var version = _version;
            var submission = ++_submission;
            Action<AudioClip, AudioHandle> started = (clip, handle) =>
            {
                if (_isDestroyed || version != _version || !isActiveAndEnabled)
                {
                    handle.Stop();
                    return;
                }
                LastHandle = handle;
                try
                {
                    onStarted.Invoke();
                }
                finally
                {
                    if (_isDestroyed || version != _version || !isActiveAndEnabled)
                    {
                        handle.Stop();
                    }
                }
            };
            // 命名路径由 4.3 pending/租约负责加载失败、最新独占提交和迟到结果；这里不提前 Stop。
            if (selection.IsNamed)
            {
                var request = loadAsync ? (borrowed ? _channel.PlayBorrowedAsync(borrowedSource, selection.Address, options, started) :
                    _channel.PlayAsync(selection.Address, options, started)) : null;
                if (!loadAsync)
                {
                    var handle = borrowed ? _channel.PlayBorrowedNamed(borrowedSource, selection.Address, options, started) :
                        _channel.PlayNamed(selection.Address, options, started);
                    if (version != _version || _isDestroyed)
                    {
                        handle.Stop();
                    }

                    if (submission == _submission)
                    {
                        LastRequest = null;
                    }
                }
                else
                {
                    if (version != _version || _isDestroyed)
                    {
                        request.Cancel();
                    }

                    if (submission == _submission)
                    {
                        LastRequest = request;
                    }
                }
            }
            else
            {
                var handle = _channel.PlayWithSnapshot(selection.Clip, snapshot, started, source: borrowed ? borrowedSource : null, borrowed: borrowed);
                if (version != _version || _isDestroyed)
                {
                    handle.Stop();
                }

                if (submission == _submission)
                {
                    LastRequest = null;
                }
            }
        }

        /// <summary>停止进入时本通道的播放与 pending；不停止清理重入中新建的请求。</summary>
        public void Stop()
        {
            _version++;
            _channel?.Stop();
        }
        /// <summary>停用时结束本组件原播放与等待，但保留通道登记供重新启用。</summary>
        private void OnDisable() => Stop();
        /// <summary>先撤销本组件提交版本，再归还原通道登记，防止迟到回调接管新身份。</summary>
        private void OnDestroy()
        {
            _isDestroyed = true;
            _version++;
            var channel = _channel;
            _channel = null;
            channel?.Dispose();
        }
    }
}
