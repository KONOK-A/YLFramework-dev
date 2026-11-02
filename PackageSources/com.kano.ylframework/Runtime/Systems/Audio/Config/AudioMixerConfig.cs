using System;
using UnityEngine;
using UnityEngine.Audio;

namespace YLFramework.Audio
{
    /// <summary>可选的分类路由配置；须显式应用，编辑资产本身不会更新正在使用的路由。</summary>
    [CreateAssetMenu(fileName = "AudioMixerConfig", menuName = "YLFramework/Audio/Mixer Config")]
    public sealed class AudioMixerConfig : ScriptableObject
    {
        [Tooltip("Required when any output group is assigned. Does not bind exposed parameters automatically.")]
        [SerializeField] private AudioMixer mixer;
        [Tooltip("Leave empty for direct output. Assigned groups must belong to this configuration's Mixer.")]
        [SerializeField] private AudioMixerGroup musicGroup;
        [Tooltip("Leave empty for direct output. Assigned groups must belong to this configuration's Mixer.")]
        [SerializeField] private AudioMixerGroup voiceGroup;
        [Tooltip("Leave empty for direct output. Assigned groups must belong to this configuration's Mixer.")]
        [SerializeField] private AudioMixerGroup soundGroup;

        /// <summary>原生 Mixer；所有分类输出组都为空时可不配置。</summary>
        public AudioMixer Mixer => mixer;
        /// <summary>Music 的默认输出组；null 表示直接输出。</summary>
        public AudioMixerGroup MusicGroup => musicGroup;
        /// <summary>Voice 的默认输出组；null 表示直接输出。</summary>
        public AudioMixerGroup VoiceGroup => voiceGroup;
        /// <summary>Sound 的默认输出组；null 表示直接输出。</summary>
        public AudioMixerGroup SoundGroup => soundGroup;

        /// <summary>在应用任何分类路由前校验 Mixer 和全部组的存活及归属。</summary>
        internal void Validate()
        {
            if (AudioOutputRoute.HasReference(mixer) && !mixer)
            {
                throw new ArgumentException("The configured Mixer has been destroyed.", nameof(mixer));
            }

            ValidateGroup(musicGroup, nameof(musicGroup));
            ValidateGroup(voiceGroup, nameof(voiceGroup));
            ValidateGroup(soundGroup, nameof(soundGroup));
        }

        /// <summary>允许空组直接输出，但已填写的组必须存活且属于配置中的 Mixer。</summary>
        private void ValidateGroup(AudioMixerGroup group, string parameterName)
        {
            if (!AudioOutputRoute.HasReference(group))
            {
                return;
            }

            if (!group)
            {
                throw new ArgumentException("The configured output group has been destroyed.", parameterName);
            }

            if (!mixer)
            {
                throw new ArgumentException("An output group requires a configured Mixer.", parameterName);
            }

            if (group.audioMixer != mixer)
            {
                throw new ArgumentException("The output group must belong to the configured Mixer.", parameterName);
            }
        }
    }
}
