using System;
using UnityEngine;
using UnityEngine.Audio;

namespace YLFramework.Audio
{
    /// <summary>显式控制原生 Mixer，不初始化 AudioKit、不推断目标，也不维护过渡队列。</summary>
    /// <remarks>
    /// 所有操作在 Unity 主线程调用。参数和音量写入应在 Start 或更晚执行，不在 Awake、OnEnable 或
    /// AfterSceneLoad 初始化回调中执行。显式参数写入会覆盖快照控制，直到主动清除覆盖。
    /// </remarks>
    public static class AudioMixerControl
    {
        private const float MinimumDecibels = -80f;

        /// <summary>写入暴露参数并返回原生结果；非法参数抛异常，不改分类路由或 Settings。</summary>
        /// <param name="mixer">由调用方保活的有效 Mixer。</param>
        /// <param name="parameterName">明确的非空白暴露参数名。</param>
        /// <param name="value">有限参数值，不做单位换算。</param>
        public static bool TrySetFloat(AudioMixer mixer, string parameterName, float value)
        {
            ValidateParameter(mixer, parameterName);
            ValidateFinite(value, nameof(value));
            return mixer.SetFloat(parameterName, value);
        }

        /// <summary>读取暴露参数；原生读取失败时返回 false，并将输出值设为零。</summary>
        /// <param name="mixer">由调用方保活的有效 Mixer。</param>
        /// <param name="parameterName">明确的非空白暴露参数名。</param>
        /// <param name="value">读取的原生参数值；失败时为零。</param>
        public static bool TryGetFloat(AudioMixer mixer, string parameterName, out float value)
        {
            ValidateParameter(mixer, parameterName);
            if (mixer.GetFloat(parameterName, out value))
            {
                return true;
            }

            value = 0f;
            return false;
        }

        /// <summary>解除对暴露参数的显式控制，使其可重新受快照控制，不改变输出路由。</summary>
        /// <param name="mixer">由调用方保活的有效 Mixer。</param>
        /// <param name="parameterName">要清除显式覆盖的非空白暴露参数名。</param>
        public static bool TryClearFloat(AudioMixer mixer, string parameterName)
        {
            ValidateParameter(mixer, parameterName);
            return mixer.ClearFloat(parameterName);
        }

        /// <summary>将 [0,1] 线性音量换算为分贝后写入指定暴露参数，不改变 Settings。</summary>
        /// <param name="mixer">由调用方保活的有效 Mixer。</param>
        /// <param name="parameterName">采用分贝单位的非空白暴露参数名。</param>
        /// <param name="linearVolume">有限的归一化线性音量，零映射至 -80 dB。</param>
        public static bool TrySetVolume(AudioMixer mixer, string parameterName, float linearVolume)
        {
            return TrySetFloat(mixer, parameterName, LinearVolumeToDecibels(linearVolume));
        }

        /// <summary>读取分贝参数并换算为归一化音量；原生读取失败时返回 false 且输出零。</summary>
        /// <param name="mixer">由调用方保活的有效 Mixer。</param>
        /// <param name="parameterName">采用分贝单位的非空白暴露参数名。</param>
        /// <param name="linearVolume">换算后的 [0,1] 音量；失败时为零。</param>
        public static bool TryGetVolume(AudioMixer mixer, string parameterName, out float linearVolume)
        {
            linearVolume = 0f;
            if (!TryGetFloat(mixer, parameterName, out var value))
            {
                return false;
            }

            linearVolume = DecibelsToLinearVolume(value);
            return true;
        }

        /// <summary>将有限的 [0,1] 音量换算为分贝，下限为 -80 dB；该下限不保证绝对静音。</summary>
        /// <param name="linearVolume">有限的归一化线性音量。</param>
        public static float LinearVolumeToDecibels(float linearVolume)
        {
            ValidateFinite(linearVolume, nameof(linearVolume));
            if (linearVolume < 0f || linearVolume > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(linearVolume), "Volume must be in [0,1].");
            }

            return linearVolume == 0f ? MinimumDecibels : Mathf.Max(MinimumDecibels, 20f * Mathf.Log10(linearVolume));
        }

        /// <summary>将有限分贝值换算为 [0,1] 音量；不高于 -80 dB 的值映射为零。</summary>
        /// <param name="decibels">有限分贝值，正值换算后限幅至一。</param>
        public static float DecibelsToLinearVolume(float decibels)
        {
            ValidateFinite(decibels, nameof(decibels));
            return decibels <= MinimumDecibels ? 0f : Mathf.Clamp01(Mathf.Pow(10f, decibels / 20f));
        }

        /// <summary>立即向快照所属原生 Mixer 提交过渡，不排队调度，也不解除参数覆盖。</summary>
        /// <param name="snapshot">由调用方保活且归属有效 Mixer 的快照。</param>
        /// <param name="duration">有限非负过渡秒数；零表示立即切换。</param>
        public static void TransitionToSnapshot(AudioMixerSnapshot snapshot, float duration)
        {
            if (!snapshot)
            {
                throw new ArgumentException("A live snapshot is required.", nameof(snapshot));
            }

            ValidateMixer(snapshot.audioMixer, nameof(snapshot));
            ValidateDuration(duration);
            snapshot.TransitionTo(duration);
        }

        /// <summary>提交同一 Mixer 内多个快照的加权混合过渡，数组原样交给 Unity，不进行归一化。</summary>
        /// <param name="mixer">所有快照共同所属的有效 Mixer。</param>
        /// <param name="snapshots">至少一项且每项均有效的非空快照数组。</param>
        /// <param name="weights">与快照逐项对应的有限非负权重，总和须为正。</param>
        /// <param name="duration">有限非负过渡秒数；零表示立即切换。</param>
        public static void TransitionToSnapshots(AudioMixer mixer, AudioMixerSnapshot[] snapshots, float[] weights, float duration)
        {
            ValidateMixer(mixer, nameof(mixer));
            ValidateDuration(duration);
            if (snapshots == null)
            {
                throw new ArgumentNullException(nameof(snapshots));
            }

            if (weights == null)
            {
                throw new ArgumentNullException(nameof(weights));
            }

            if (snapshots.Length == 0)
            {
                throw new ArgumentException("At least one snapshot is required.", nameof(snapshots));
            }

            if (snapshots.Length != weights.Length)
            {
                throw new ArgumentException("Snapshot and weight arrays must have the same length.", nameof(weights));
            }

            double totalWeight = 0d;
            for (var i = 0; i < snapshots.Length; i++)
            {
                if (!snapshots[i])
                {
                    throw new ArgumentException($"Snapshot at index {i} must be live.", nameof(snapshots));
                }

                if (snapshots[i].audioMixer != mixer)
                {
                    throw new ArgumentException($"Snapshot at index {i} must belong to the supplied Mixer.", nameof(snapshots));
                }

                ValidateFinite(weights[i], nameof(weights));
                if (weights[i] < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(weights), "Weights must be non-negative.");
                }

                totalWeight += weights[i];
            }
            if (totalWeight <= 0d)
            {
                throw new ArgumentException("Weights must have a positive total.", nameof(weights));
            }

            mixer.TransitionToSnapshots(snapshots, weights, duration);
        }

        /// <summary>要求 Mixer 仍存活，并用调用方指定的参数名报告错误。</summary>
        private static void ValidateMixer(AudioMixer mixer, string parameterName)
        {
            if (!mixer)
            {
                throw new ArgumentException("A live Mixer is required.", parameterName);
            }
        }

        /// <summary>在原生参数操作前确认 Mixer 有效且暴露参数名非空白。</summary>
        private static void ValidateParameter(AudioMixer mixer, string parameterName)
        {
            ValidateMixer(mixer, nameof(mixer));
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                throw new ArgumentException("An exposed parameter name is required.", nameof(parameterName));
            }
        }

        /// <summary>拒绝 NaN 和无穷大，保留具体参数名用于异常定位。</summary>
        private static void ValidateFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName, "The value must be finite.");
            }
        }

        /// <summary>在提交原生快照过渡前拒绝负数或非有限时长。</summary>
        private static void ValidateDuration(float duration)
        {
            ValidateFinite(duration, nameof(duration));
            if (duration < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be non-negative.");
            }
        }
    }
}
