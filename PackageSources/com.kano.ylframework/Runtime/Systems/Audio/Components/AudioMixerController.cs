using System;
using UnityEngine;
using UnityEngine.Audio;

namespace YLFramework.Audio
{
    /// <summary>连接 Inspector 与 UnityEvent 的显式 Mixer 控制组件，提供路由应用、参数写入和快照过渡。</summary>
    /// <remarks>Awake 只按配置应用路由；停用或销毁本组件不会清除全局路由及 Mixer 状态。</remarks>
    public sealed class AudioMixerController : MonoBehaviour
    {
        [Tooltip("Routing configuration and Mixer used for explicit parameter and snapshot operations.")]
        [SerializeField] private AudioMixerConfig config;
        [Tooltip("Explicitly enable to apply routing in Awake. New Editor components leave global routing unchanged. Does not write parameters or transition snapshots.")]
        [SerializeField] private bool applyOnAwake = true;
        [Tooltip("Explicit exposed parameter name; no automatic binding. Invoke SetParameter/SetVolume in Start or later, not Awake, OnEnable or AfterSceneLoad.")]
        [SerializeField] private string parameterName;
        [Tooltip("Used only by the explicit TransitionToSnapshot event. Must belong to the configured Mixer.")]
        [SerializeField] private AudioMixerSnapshot snapshot;
        [Tooltip("Finite, non-negative transition seconds. New Editor components use 0.5 seconds; zero is an immediate switch. No transition starts automatically.")]
        [SerializeField] private float transitionDuration;
        [Tooltip("Used only by the explicit TransitionToSnapshots event. All snapshots must belong to the configured Mixer.")]
        [SerializeField] private AudioMixerSnapshot[] snapshots;
        [Tooltip("One finite, non-negative weight per snapshot, with a positive total. Forwarded to Unity without normalization.")]
        [SerializeField] private float[] weights;

        /// <summary>新 Editor 组件默认不在 Awake 改全局路由，并提供半秒的显式过渡时长。</summary>
        private void Reset()
        {
            applyOnAwake = false;
            transitionDuration = .5f;
        }

        /// <summary>显式启用且配置存在时只应用路由，不写参数或启动快照过渡。</summary>
        private void Awake()
        {
            if (applyOnAwake && config)
            {
                Apply();
            }
        }

        /// <summary>将完整配置应用到分类路由，不写 Mixer 参数；配置参数错误记录到组件日志。</summary>
        public void Apply()
        {
            if (!config)
            {
                LogError(nameof(Apply), "A live AudioMixerConfig is required.");
                return;
            }
            try
            {
                AudioKit.ApplyMixerConfig(config);
            }
            catch (ArgumentException exception)
            {
                LogError(nameof(Apply), exception.Message);
            }
        }

        /// <summary>仅清除分类默认路由，保留请求显式路由和 Mixer 参数。</summary>
        public void ClearRouting()
        {
            AudioKit.ClearMixerRouting();
        }

        /// <summary>写入配置的暴露参数；应在主线程 Start 或更晚调用，原生拒绝和参数错误记录到日志。</summary>
        /// <param name="value">要写入的有限参数值，不执行音量单位换算。</param>
        public void SetParameter(float value)
        {
            var mixer = GetMixer(nameof(SetParameter));
            if (!mixer)
            {
                return;
            }

            try
            {
                if (!AudioMixerControl.TrySetFloat(mixer, parameterName, value))
                {
                    LogError(nameof(SetParameter), $"Mixer rejected parameter '{parameterName}' (missing parameter or snapshots being edited).");
                }
            }
            catch (ArgumentException exception)
            {
                LogError(nameof(SetParameter), exception.Message);
            }
        }

        /// <summary>在 Start 或更晚将 [0,1] 线性音量写入配置的分贝参数，不改变 AudioKit.Settings。</summary>
        /// <param name="linearVolume">有限的归一化线性音量；零映射至 -80 dB，不保证绝对静音。</param>
        public void SetVolume(float linearVolume)
        {
            var mixer = GetMixer(nameof(SetVolume));
            if (!mixer)
            {
                return;
            }

            try
            {
                if (!AudioMixerControl.TrySetVolume(mixer, parameterName, linearVolume))
                {
                    LogError(nameof(SetVolume), $"Mixer rejected parameter '{parameterName}' (missing parameter or snapshots being edited).");
                }
            }
            catch (ArgumentException exception)
            {
                LogError(nameof(SetVolume), exception.Message);
            }
        }

        /// <summary>解除对配置参数的显式控制，不清除输出路由。</summary>
        public void ClearParameter()
        {
            var mixer = GetMixer(nameof(ClearParameter));
            if (!mixer)
            {
                return;
            }

            try
            {
                if (!AudioMixerControl.TryClearFloat(mixer, parameterName))
                {
                    LogError(nameof(ClearParameter), $"Mixer could not clear parameter '{parameterName}'.");
                }
            }
            catch (ArgumentException exception)
            {
                LogError(nameof(ClearParameter), exception.Message);
            }
        }

        /// <summary>显式过渡到配置快照，不解除已写入的参数覆盖。</summary>
        public void TransitionToSnapshot()
        {
            var mixer = GetMixer(nameof(TransitionToSnapshot));
            if (!mixer)
            {
                return;
            }

            try
            {
                if (snapshot && snapshot.audioMixer != mixer)
                {
                    throw new ArgumentException("The snapshot must belong to the configured Mixer.", nameof(snapshot));
                }

                AudioMixerControl.TransitionToSnapshot(snapshot, transitionDuration);
            }
            catch (ArgumentException exception)
            {
                LogError(nameof(TransitionToSnapshot), exception.Message);
            }
        }

        /// <summary>显式提交配置快照的加权混合过渡，不排队调度，也不修改配置数组。</summary>
        public void TransitionToSnapshots()
        {
            var mixer = GetMixer(nameof(TransitionToSnapshots));
            if (!mixer)
            {
                return;
            }

            try
            {
                AudioMixerControl.TransitionToSnapshots(mixer, snapshots, weights, transitionDuration);
            }
            catch (ArgumentException exception)
            {
                LogError(nameof(TransitionToSnapshots), exception.Message);
            }
        }

        /// <summary>取得配置中的有效 Mixer，缺失时按当前操作记录错误并返回 null。</summary>
        private AudioMixer GetMixer(string operation)
        {
            if (!config)
            {
                LogError(operation, "A live AudioMixerConfig is required.");
                return null;
            }
            var mixer = config.Mixer;
            if (!mixer)
            {
                LogError(operation, "The configuration must provide a live Mixer for this operation.");
                return null;
            }
            return mixer;
        }

        /// <summary>将操作名与错误内容关联到本组件，便于定位 Inspector 或事件绑定问题。</summary>
        private void LogError(string operation, string message)
        {
            Debug.LogError($"{nameof(AudioMixerController)}.{operation}: {message}", this);
        }
    }
}
