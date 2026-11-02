using UnityEngine;
using UnityEngine.Audio;

namespace YLFramework.Audio
{
    // 不触碰 caller 的层级、启用状态、空间设置或滤镜；每个字段只恢复仍等于本租约最后写入的值。
    /// <summary>记录受控借入音源的字段原值和本次写入值，供播放结束时有条件恢复。</summary>
    internal sealed class AudioBorrowedSource
    {
        internal readonly AudioSource Source;
        private readonly AudioClip _originalClip;
        private readonly AudioClip _clip;
        private readonly bool _originalLoop;
        private readonly bool _originalMute;
        private readonly float _originalVolume;
        private readonly float _originalPitch;
        private readonly AudioMixerGroup _originalOutput;
        private float _volume;
        private float _pitch;
        private bool _mute;
        private bool _loop;
        private AudioMixerGroup _output;
        internal bool OwnsPlayback => Source && Source.clip == _clip;
        /// <summary>保存原配置后绑定本次 Clip、循环和音高，不恢复或接管先前播放历史。</summary>
        internal AudioBorrowedSource(AudioSource source, AudioClip clip, in AudioPlaybackOptions options)
        {
            Source = source;
            _clip = clip;
            _originalClip = source.clip;
            _originalLoop = source.loop;
            _originalVolume = source.volume;
            _originalPitch = source.pitch;
            _originalMute = source.mute;
            _originalOutput = source.outputAudioMixerGroup;
            source.clip = clip;
            source.loop = _loop = options.Loop;
            SetPitch(options.Pitch);
        }
        /// <summary>仍持有本次 Clip 时更新音量，并记住用于归还比较的最后写入值。</summary>
        internal void SetVolume(float value)
        {
            if (OwnsPlayback)
            {
                Source.volume = _volume = value;
            }
        }
        /// <summary>仍持有本次 Clip 时更新音高及最后写入记录。</summary>
        internal void SetPitch(float value)
        {
            if (OwnsPlayback)
            {
                Source.pitch = _pitch = value;
            }
        }
        /// <summary>仍持有本次 Clip 时更新静音及最后写入记录。</summary>
        internal void SetMute(bool value)
        {
            if (OwnsPlayback)
            {
                Source.mute = _mute = value;
            }
        }
        /// <summary>仍持有本次 Clip 时更新输出组及最后写入记录。</summary>
        internal void SetOutput(AudioMixerGroup value)
        {
            if (OwnsPlayback)
            {
                Source.outputAudioMixerGroup = _output = value;
            }
        }
        /// <summary>仍拥有播放时停止音源并比较恢复字段；外部已换 Clip 时放弃恢复，不销毁借入对象。</summary>
        internal void Release()
        {
            if (!OwnsPlayback)
            {
                return;
            }

            Source.Stop();
            if (Source.loop == _loop)
            {
                Source.loop = _originalLoop;
            }

            if (Source.volume == _volume)
            {
                Source.volume = _originalVolume;
            }

            if (Source.pitch == _pitch)
            {
                Source.pitch = _originalPitch;
            }

            if (Source.mute == _mute)
            {
                Source.mute = _originalMute;
            }

            if (Source.outputAudioMixerGroup == _output)
            {
                Source.outputAudioMixerGroup = _originalOutput;
            }

            Source.clip = _originalClip ? _originalClip : null;
        }
    }
}
