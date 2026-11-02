using System;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>按 Clip 播放位置计算的可选音量包络，不按现实经过时间计时；零值表示禁用。</summary>
    [Serializable]
    public struct AudioAutoFade
    {
        /// <summary>开头淡入占用的 Clip 秒数；非循环短片段会按比例压缩首尾淡变区间。</summary>
        public float FadeInSeconds;
        /// <summary>自然结束前淡出占用的 Clip 秒数；循环播放时必须为零。</summary>
        public float FadeOutSeconds;

        /// <summary>保存自动淡变时长；播放入口会在改变旧请求前校验配置。</summary>
        /// <param name="fadeInSeconds">开头淡入的有限非负 Clip 秒数。</param>
        /// <param name="fadeOutSeconds">结尾淡出的有限非负 Clip 秒数；循环播放时为零。</param>
        public AudioAutoFade(float fadeInSeconds, float fadeOutSeconds)
        {
            this.FadeInSeconds = fadeInSeconds;
            this.FadeOutSeconds = fadeOutSeconds;
        }

        internal bool IsEnabled => FadeInSeconds != 0f || FadeOutSeconds != 0f;
        /// <summary>拒绝非法时长及循环播放的尾部淡出配置。</summary>
        internal void Validate(bool loop)
        {
            if (!AudioSoundOptions.IsFinite(FadeInSeconds) || FadeInSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(FadeInSeconds));
            }

            if (!AudioSoundOptions.IsFinite(FadeOutSeconds) || FadeOutSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(FadeOutSeconds));
            }

            if (loop && FadeOutSeconds > 0f)
            {
                throw new ArgumentException("Looping playback has no final fade-out. Use an initial fade-in and stop it explicitly.", nameof(FadeOutSeconds));
            }
        }

        /// <summary>启用包络时确认 Clip 具备可用于位置计算的采样信息。</summary>
        internal void ValidateClip(AudioClip clip)
        {
            if (!IsEnabled)
            {
                return;
            }

            if (!clip || clip.samples <= 0 || clip.frequency <= 0)
            {
                throw new ArgumentException("Automatic fading requires a live clip with a positive sample count and frequency.", nameof(clip));
            }
        }

        /// <summary>将淡变区间限制在 Clip 长度内；非循环时按比例压缩重叠的首尾区间。</summary>
        internal void Normalize(double length, bool loop, out double fadeIn, out double fadeOut)
        {
            Validate(loop);
            if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            fadeIn = FadeInSeconds;
            fadeOut = FadeOutSeconds;
            if (loop)
            {
                fadeIn = Math.Min(fadeIn, length);
                return;
            }
            var sum = fadeIn + fadeOut;
            if (sum > length)
            {
                var scale = length / sum;
                fadeIn *= scale;
                fadeOut *= scale;
            }
        }

        /// <summary>按当前 Clip 位置取首尾线性包络的较小增益。</summary>
        internal static float Evaluate(double time, double length, double fadeIn, double fadeOut)
        {
            time = Math.Max(0d, Math.Min(length, time));
            var startGain = fadeIn > 0d ? Math.Min(1d, time / fadeIn) : 1d;
            var endGain = fadeOut > 0d ? Math.Min(1d, (length - time) / fadeOut) : 1d;
            return (float)Math.Min(startGain, endGain);
        }
    }
}
