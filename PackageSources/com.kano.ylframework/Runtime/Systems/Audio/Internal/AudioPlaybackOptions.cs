using System;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>由单次请求持有的配置值快照；其中 Unity 资源仍是借用引用，不复制或接管资源。</summary>
    internal struct AudioPlaybackOptions
    {
        internal bool Loop;
        internal float Volume;
        internal float Pitch;
        internal AudioKit.PlaySoundModes? PlaySoundMode;
        internal float MinDistance;
        internal float MaxDistance;
        internal AudioRolloffMode RolloffMode;
        internal AudioOutputRoute OutputRoute;
        internal AudioAutoFade AutoFade;
        internal int Priority;
        internal bool AutoPauseWhenTimeScaleZero;

        // 零值只用于清空记录，不能当作有效播放默认值，例如音高不能为零。
        internal static AudioPlaybackOptions Defaults => new AudioPlaybackOptions
        {
            Volume = 1f,
            Pitch = 1f,
            MinDistance = 1f,
            MaxDistance = 50f,
            RolloffMode = AudioRolloffMode.Linear
        };
        internal static AudioPlaybackOptions MusicDefaults
        {
            get
            {
                var options = Defaults;
                options.Loop = true;
                return options;
            }
        }
        /// <summary>复制可变选项，未提供时按入口选择普通默认值或音乐循环默认值。</summary>
        internal static AudioPlaybackOptions Capture(AudioSoundOptions options, bool music = false)
        {
            if (options == null)
            {
                return music ? MusicDefaults : Defaults;
            }

            return new AudioPlaybackOptions
            {
                Loop = options.Loop,
                Volume = options.Volume,
                Pitch = options.Pitch,
                PlaySoundMode = options.PlaySoundMode,
                MinDistance = options.MinDistance,
                MaxDistance = options.MaxDistance,
                RolloffMode = options.RolloffMode,
                OutputRoute = options.OutputRoute,
                AutoFade = options.AutoFade,
                Priority = options.Priority,
                AutoPauseWhenTimeScaleZero = options.AutoPauseWhenTimeScaleZero
            };
        }
        /// <summary>统一校验路由、包络及请求参数，仅空间播放校验距离与衰减模式。</summary>
        internal void Validate(bool spatial)
        {
            OutputRoute.Validate();
            AutoFade.Validate(Loop);
            if (Priority < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Priority));
            }

            if (!AudioSoundOptions.IsFinite(Volume) || Volume < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Volume));
            }

            if (!AudioSoundOptions.IsFinite(Pitch) || Pitch <= 0 || Pitch > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(Pitch));
            }

            if (PlaySoundMode.HasValue && (uint)PlaySoundMode.Value > (uint)AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(PlaySoundMode));
            }

            if (!spatial)
            {
                return;
            }

            if (!AudioSoundOptions.IsFinite(MinDistance) || MinDistance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinDistance));
            }

            if (!AudioSoundOptions.IsFinite(MaxDistance) || MaxDistance <= 0 || MaxDistance < MinDistance)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxDistance));
            }

            if (RolloffMode != AudioRolloffMode.Linear && RolloffMode != AudioRolloffMode.Logarithmic)
            {
                throw new ArgumentOutOfRangeException(nameof(RolloffMode));
            }
        }
        /// <summary>补充校验自动淡变对 Clip 采样数据的要求。</summary>
        internal void ValidateClip(AudioClip clip) => AutoFade.ValidateClip(clip);
    }
}
