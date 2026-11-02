using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>序列化的音乐所有者：通过 AudioKit 播放单个 Clip，并追踪自己持有的 lease。</summary>
    /// <remarks>Play 会替换当前音乐，可选择用 Crossfade 过渡。Stop 和 OnDisable 只停止该组件仍在追踪的 lease；Clip 为空时不会影响正在播放的音乐。</remarks>
    public class AudioMusicPlayer : MonoBehaviour
    {
        [SerializeField] private AudioClip clip;
        [SerializeField] private bool loop = true;
        [Tooltip("Play once in the first Start, not on every enable. Disabling stops this component's owned music and outgoing tail.")]
        [SerializeField] private bool playOnAwake = true;
        [Tooltip("Request volume multiplier: 0 is silent, 1 is unchanged; values above 1 are allowed.")]
        [SerializeField] private float volume = 1f;
        [Tooltip("Inherit the Music default, override with a mixer group, or use Direct output. Only MixerGroup accepts a Group.")]
        [SerializeField] private AudioOutputRoute outputRoute;
        [Tooltip("Fade durations use clip seconds. Short clips compress both fades proportionally. Looping clips cannot use a tail fade.")]
        [SerializeField] private AudioAutoFade autoFade;
        [Tooltip("Overlap music on Play. Disabling this owner also stops its outgoing tail.")]
        [SerializeField] private bool crossFadeOnPlay = false;
        [SerializeField] private float crossFadeDuration = 1f;
        [SerializeField] private bool crossFadeIgnoreTimeScale = true;
        [Tooltip("New Editor components use EqualPower for background tracks; existing serialized Linear values stay unchanged. Only used when cross fade is enabled.")]
        [SerializeField] private AudioCrossfadeCurve crossFadeCurve = AudioCrossfadeCurve.Linear;

        private readonly List<AudioHandle> _owned = new List<AudioHandle>(2);

        /// <summary>为新添加的 Editor 组件选择等功率过渡，不改已有资产保存的 Linear 值。</summary>
        private void Reset()
        {
            crossFadeCurve = AudioCrossfadeCurve.EqualPower;
        }

        /// <summary>按配置仅在首次 Start 自动播放音乐。</summary>
        private void Start()
        {
            if (playOnAwake)
            {
                Play();
            }
        }

        /// <summary>开始播放配置的 Clip，可选择对当前音乐做 Crossfade。配置校验会在替换该组件现有 lease 之前完成。</summary>
        public void Play()
        {
            if (!clip)
            {
                return;
            }

            if (crossFadeOnPlay)
            {
                if (!AudioSoundOptions.IsFinite(crossFadeDuration) || crossFadeDuration < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(crossFadeDuration));
                }

                if ((uint)crossFadeCurve > (uint)AudioCrossfadeCurve.EqualPower)
                {
                    throw new ArgumentOutOfRangeException(nameof(crossFadeCurve));
                }
            }

            var options = AudioPlaybackOptions.Defaults;
            options.Loop = loop;
            options.Volume = volume;
            options.OutputRoute = outputRoute;
            options.AutoFade = autoFade;
            options.Validate(false);
            options.ValidateClip(clip);

            // Crossfade 自身会管理退场的旧音乐；若这里先停止已追踪的 lease，会截断重叠过渡。
            if (!crossFadeOnPlay)
            {
                Stop();
            }

            var handle = crossFadeOnPlay
                ? AudioKit.CrossFadeMusicWithSnapshot(clip, crossFadeDuration, options, ignoreTimeScale: crossFadeIgnoreTimeScale, curve: crossFadeCurve)
                : AudioKit.PlaySlotWithSnapshot(clip, true, options, null, null);
            // 保留该 lease 供 Stop/OnDisable 使用，并移除已经结束的 lease。
            for (var i = _owned.Count - 1; i >= 0; i--)
            {
                if (!_owned[i].IsValid)
                {
                    _owned.RemoveAt(i);
                }
            }
            if (handle.IsValid)
            {
                _owned.Add(handle);
            }
        }

        /// <summary>停止该组件仍在追踪的所有音乐 lease，并清空追踪列表。</summary>
        public void Stop()
        {
            foreach (var handle in _owned)
            {
                handle.Stop();
            }

            _owned.Clear();
        }

        /// <summary>停用时停止本组件仍持有的当前曲目和尾轨身份，不控制其他所有者的播放。</summary>
        private void OnDisable()
        {
            Stop();
        }
    }
}
