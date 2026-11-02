using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Audio
{
    /// <summary>序列化的音效所有者：从候选 Clip 中选取一个（均匀或加权随机）并通过 AudioKit 播放。</summary>
    /// <remarks>组件停用时，循环和跟随播放的请求会被停止，2D 与固定位置的 one-shot 继续播放。选曲与校验会在替换自有循环播放之前完成。</remarks>
    public class AudioEmitter : MonoBehaviour
    {
        /// <summary>每次播放的声音在空间中的定位方式。</summary>
        public enum PositionMode
        {
            /// <summary>2D 播放；忽略空间距离和跟随目标。</summary>
            TwoDimensional,
            /// <summary>3D 播放，位置取每次 Play 时该组件 Transform 的世界坐标。</summary>
            FixedPosition,
            /// <summary>3D 播放，跟随 followTarget；followTarget 为空时跟随该组件的 Transform。</summary>
            FollowTransform
        }

        [SerializeField] private List<AudioClip> clips = new List<AudioClip>();
        [SerializeField] private bool useWeightedRandom = false;
        [SerializeField] private List<AudioWeightedClip> weightedClips = new List<AudioWeightedClip>();
        [Tooltip("Request volume multiplier: 0 is silent, 1 is unchanged; values above 1 are allowed.")]
        [SerializeField] private float volume = 1f;
        [SerializeField] private float pitch = 1f;
        [Tooltip("Sound admission priority: 0 is ordinary; higher values are more important. Not Unity AudioSource.priority.")]
        [SerializeField, Min(0)] private int priority = 0;
        [SerializeField] private bool loop;
        [Tooltip("Play once in the first Start, not on every enable. Disabling stops owned loops and following sounds, but not fixed/2D one-shots.")]
        [SerializeField] private bool playOnAwake;
        [Tooltip("Inherit AudioKit.PlaySoundMode. Reset enables this for newly added Editor components; missing fields in old assets keep the explicit mode.")]
        [SerializeField] private bool useGlobalPlaySoundMode = false;
        [SerializeField] private AudioKit.PlaySoundModes playSoundMode = AudioKit.PlaySoundModes.EveryOne;
        [SerializeField] private PositionMode positionMode;
        [Tooltip("Leave empty to follow this component's Transform.")]
        [SerializeField] private Transform followTarget;
        [SerializeField] private float minDistance = 1f;
        [SerializeField] private float maxDistance = 50f;
        [SerializeField] private AudioRolloffMode rolloffMode = AudioRolloffMode.Linear;
        [Tooltip("Inherit the Sound default, override with a mixer group, or use Direct output. Only MixerGroup accepts a Group.")]
        [SerializeField] private AudioOutputRoute outputRoute;
        [Tooltip("Fade durations use clip seconds. Short clips compress both fades proportionally. Looping clips cannot use a tail fade.")]
        [SerializeField] private AudioAutoFade autoFade;
        [SerializeField] private UnityEvent onStarted = new UnityEvent();

        /// <summary>每次音效接纳并启动、原句柄纳入本组件追踪后通知一次；允许同步停止或停用组件。</summary>
        public UnityEvent Started => onStarted;

        /// <summary>本组件追踪的一次播放身份及停用时是否应停止的责任。</summary>
        private struct Playback
        {
            /// <summary>仅控制原代次的播放句柄。</summary>
            public AudioHandle Handle;
            /// <summary>循环或跟随请求在组件停用时需要停止。</summary>
            public bool StopOnDisable;
        }

        private readonly List<Playback> _playbacks = new List<Playback>();
        private Action<AudioClip, AudioHandle> _startedCallback;

        /// <summary>为新添加的 Editor 组件默认启用全局去重模式继承，不改旧资产缺省字段值。</summary>
        private void Reset()
        {
            useGlobalPlaySoundMode = true;
        }

        /// <summary>按配置只在首次 Start 自动播放，不在每次启用时重复触发。</summary>
        private void Start()
        {
            if (playOnAwake)
            {
                Play();
            }
        }

        /// <summary>按该组件的配置选曲并播放。选曲与校验会在替换自有循环播放之前完成。</summary>
        public void Play()
        {
            var options = AudioPlaybackOptions.Defaults;
            options.Loop = loop;
            options.Volume = volume;
            options.Pitch = pitch;
            options.Priority = priority;
            options.PlaySoundMode = useGlobalPlaySoundMode ? (AudioKit.PlaySoundModes?)null : playSoundMode;
            options.MinDistance = minDistance;
            options.MaxDistance = maxDistance;
            options.RolloffMode = rolloffMode;
            options.OutputRoute = outputRoute;
            options.AutoFade = autoFade;
            if ((uint)positionMode > (uint)PositionMode.FollowTransform)
            {
                throw new ArgumentOutOfRangeException(nameof(positionMode));
            }

            options.Validate(positionMode != PositionMode.TwoDimensional);

            // 先选定一次并完成全部输入校验，再替换自有循环，避免坏候选或配置截断旧播放。
            AudioClip selectedClip;
            if (!(useWeightedRandom
                    ? AudioKit.TrySelectWeightedRandomClip(weightedClips, out selectedClip)
                    : AudioKit.TrySelectRandomClip(clips, out selectedClip)))
            {
                return;
            }

            options.ValidateClip(selectedClip);
            var target = AudioOutputRoute.HasReference(followTarget) ? followTarget : transform;
            if (positionMode == PositionMode.FollowTransform && (!target || !target.gameObject.activeInHierarchy))
            {
                return;
            }

            var position = positionMode == PositionMode.FollowTransform ? target.position : transform.position;
            if (positionMode != PositionMode.TwoDimensional &&
                (!AudioSoundOptions.IsFinite(position.x) || !AudioSoundOptions.IsFinite(position.y) || !AudioSoundOptions.IsFinite(position.z)))
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            if (loop)
            {
                Stop();
            }

            for (var i = _playbacks.Count - 1; i >= 0; i--)
            {
                if (!_playbacks[i].Handle.IsValid)
                {
                    _playbacks.RemoveAt(i);
                }
            }
            if (_startedCallback == null)
            {
                _startedCallback = OnPlaybackStarted;
            }

            _ = AudioKit.PlaySoundWithSnapshot(selectedClip, options, onStarted: _startedCallback,
                spatial: positionMode != PositionMode.TwoDimensional,
                position: positionMode == PositionMode.TwoDimensional ? Vector3.zero : position,
                target: positionMode == PositionMode.FollowTransform ? target : null);
        }

        /// <summary>先追踪实际开始的句柄再投递事件，失败时停止并移除原播放身份。</summary>
        private void OnPlaybackStarted(AudioClip clip, AudioHandle handle)
        {
            _playbacks.Add(new Playback
            {
                Handle = handle,
                StopOnDisable = handle.Voice.Options.Loop || handle.Voice.IsFollowing
            });
            try
            {
                onStarted.Invoke();
            }
            catch
            {
                handle.Stop();
                RemovePlayback(handle);
                throw;
            }
            if (!handle.IsValid)
            {
                RemovePlayback(handle);
            }
        }

        /// <summary>按 Voice 引用和原代次移除追踪，不误删复用后的新播放。</summary>
        private void RemovePlayback(AudioHandle handle)
        {
            for (var i = _playbacks.Count - 1; i >= 0; i--)
            {
                var owned = _playbacks[i].Handle;
                if (ReferenceEquals(owned.Voice, handle.Voice) && owned.Generation == handle.Generation)
                {
                    _playbacks.RemoveAt(i);
                }
            }
        }

        /// <summary>停止该组件仍在追踪的所有声音，并清空追踪列表。</summary>
        public void Stop()
        {
            foreach (var playback in _playbacks)
            {
                playback.Handle.Stop();
            }

            _playbacks.Clear();
        }

        /// <summary>停用时只停止循环和跟随请求，清理失效追踪，允许固定位置和 2D 单次音效继续。</summary>
        private void OnDisable()
        {
            // UI 与固定位置单次音效允许比触发对象活得更久，不随该组件停用而截断。
            for (var i = _playbacks.Count - 1; i >= 0; i--)
            {
                var playback = _playbacks[i];
                if (playback.StopOnDisable)
                {
                    playback.Handle.Stop();
                }

                if (playback.StopOnDisable || !playback.Handle.IsValid)
                {
                    _playbacks.RemoveAt(i);
                }
            }
        }
    }
}
