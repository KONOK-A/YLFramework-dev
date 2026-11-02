using System;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>可修改的播放配置，构造时具有有效默认值；播放捕获值快照，Sound 去重仅对音效分类生效。</summary>
    [Serializable]
    public sealed class AudioSoundOptions
    {
        /// <summary>Clip 结束后是否重复播放；请求会持续到被停止或替换。</summary>
        public bool Loop;
        /// <summary>非负请求音量倍率；零为无声，一为不变，允许大于一，播放时要求有限值。</summary>
        public float Volume;
        /// <summary>播放音高，范围为 (0,3]；省略时为一，显式零值非法。</summary>
        public float Pitch;
        /// <summary>可选的单请求 Sound 去重覆盖；null 表示继承 AudioKit.PlaySoundMode。Music 和 Voice 会忽略该字段。</summary>
        public AudioKit.PlaySoundModes? PlaySoundMode;
        /// <summary>3D 衰减起始距离；仅空间播放的请求会校验该值。</summary>
        public float MinDistance;
        /// <summary>3D 衰减结束距离；必须为正数且不小于 MinDistance，仅空间播放的请求会校验该值。</summary>
        public float MaxDistance;
        /// <summary>3D 衰减模式；空间播放仅接受 Linear 和 Logarithmic。</summary>
        public AudioRolloffMode RolloffMode;
        /// <summary>继承分类输出组、显式指定输出组，或绕过 Mixer 直接输出。</summary>
        public AudioOutputRoute OutputRoute;
        /// <summary>按 Clip 位置计算的可选包络；默认零时长不启用自动淡变。</summary>
        public AudioAutoFade AutoFade;
        /// <summary>Sound 接纳优先级，非负且越大越重要；Music 和 Voice 忽略，不是 Unity 的 AudioSource.priority。</summary>
        public int Priority;
        /// <summary>显式启用 timeScale == 0 时的局部自动暂停。默认 false；解除自动暂停不解除手动或通道暂停。</summary>
        public bool AutoPauseWhenTimeScaleZero;
        /// <summary>创建具有有效播放默认值的非循环配置。</summary>
        public AudioSoundOptions()
        {
            var defaults = AudioPlaybackOptions.Defaults;
            Loop = defaults.Loop;
            Volume = defaults.Volume;
            Pitch = defaults.Pitch;
            PlaySoundMode = defaults.PlaySoundMode;
            MinDistance = defaults.MinDistance;
            MaxDistance = defaults.MaxDistance;
            RolloffMode = defaults.RolloffMode;
            OutputRoute = defaults.OutputRoute;
            AutoFade = defaults.AutoFade;
            Priority = defaults.Priority;
        }
        /// <summary>每次返回一份新的有效非循环配置。</summary>
        public static AudioSoundOptions Defaults => new AudioSoundOptions();
        /// <summary>每次返回一份新的背景音乐循环配置。</summary>
        public static AudioSoundOptions MusicDefaults => new AudioSoundOptions { Loop = true };
        /// <summary>判断数值是否既非 NaN 也非无穷大。</summary>
        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        /// <summary>通过播放快照统一校验配置，空间字段仅在空间播放时参与校验。</summary>
        internal void Validate(bool spatial) => AudioPlaybackOptions.Capture(this).Validate(spatial);
        /// <summary>校验自动淡变所需的 Clip 采样信息。</summary>
        internal void ValidateClip(AudioClip clip) => AutoFade.ValidateClip(clip);
    }
}
