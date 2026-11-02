using System;
using UnityEngine.Audio;
using YLFramework.Binding;

namespace YLFramework.Audio
{
    /// <summary>管理持久化分类开关与音量，以及仅运行时生效的静音和 Mixer 路由；沿用既有六个存储键。</summary>
    public sealed class AudioKitSettings : IDisposable
    {
        /// <summary>持久化的 Sound 通道开关。关闭时停止所有有效声音并拒绝新请求，直到重新启用。</summary>
        public PlayerPrefsBoolProperty IsSoundOn { get; } = new PlayerPrefsBoolProperty("KEY_AUDIO_MANAGER_SOUND_ON", true);
        /// <summary>持久化的 Music 通道开关。关闭时停止 Source 但保留请求；重新启用后从头重播。</summary>
        public PlayerPrefsBoolProperty IsMusicOn { get; } = new PlayerPrefsBoolProperty("KEY_AUDIO_MANAGER_MUSIC_ON", true);
        /// <summary>持久化的 Voice 通道开关。关闭时停止 Source 但保留请求；重新启用后从头重播。</summary>
        public PlayerPrefsBoolProperty IsVoiceOn { get; } = new PlayerPrefsBoolProperty("KEY_AUDIO_MANAGER_VOICE_ON", true);
        /// <summary>持久化的有限非负 Sound 音量倍率；默认一，零为无声，允许大于一。</summary>
        public PlayerPrefsFloatProperty SoundVolume { get; } = new AudioVolumeProperty("KEY_AUDIO_MANAGER_SOUND_VOLUME");
        /// <summary>持久化的有限非负 Music 音量倍率；非法存储值读取为一，不回写存储。</summary>
        public PlayerPrefsFloatProperty MusicVolume { get; } = new AudioVolumeProperty("KEY_AUDIO_MANAGER_MUSIC_VOLUME");
        /// <summary>持久化的有限非负 Voice 音量倍率；非法写入在持久化和通知之前抛错。</summary>
        public PlayerPrefsFloatProperty VoiceVolume { get; } = new AudioVolumeProperty("KEY_AUDIO_MANAGER_VOICE_VOLUME");
        /// <summary>静音 Sound，不停止、拒播或改变请求音量；不持久化，关闭后新会话恢复默认值。</summary>
        public BindableProperty<bool> IsSoundMuted { get; } = new BindableProperty<bool>(false).WithComparer((a, b) => a == b);
        /// <summary>静音 Music，不暂停、重播或改变请求音量；不持久化，关闭后新会话恢复默认值。</summary>
        public BindableProperty<bool> IsMusicMuted { get; } = new BindableProperty<bool>(false).WithComparer((a, b) => a == b);
        /// <summary>静音 Voice，不暂停、重播或改变请求音量；不持久化，关闭后新会话恢复默认值。</summary>
        public BindableProperty<bool> IsVoiceMuted { get; } = new BindableProperty<bool>(false).WithComparer((a, b) => a == b);
        /// <summary>Music 默认路由，null 表示直接输出；变化更新继承路由的请求，不重启播放。</summary>
        public BindableProperty<AudioMixerGroup> MusicOutputGroup { get; } = new BindableProperty<AudioMixerGroup>().WithComparer((a, b) => a == b);
        /// <summary>仅运行时生效的 Voice 默认路由；调用方须保活输出组及其 Mixer。</summary>
        public BindableProperty<AudioMixerGroup> VoiceOutputGroup { get; } = new BindableProperty<AudioMixerGroup>().WithComparer((a, b) => a == b);
        /// <summary>Sound 默认路由；显式指定组和直接输出的请求不受其变化影响。</summary>
        public BindableProperty<AudioMixerGroup> SoundOutputGroup { get; } = new BindableProperty<AudioMixerGroup>().WithComparer((a, b) => a == b);
        /// <summary>仅当三个通道开关都为 true 时为 true；赋值会同时修改三者。</summary>
        public bool IsOn
        {
            get => IsMusicOn.Value && IsVoiceOn.Value && IsSoundOn.Value;
            set
            {
                IsMusicOn.Value = value;
                IsVoiceOn.Value = value;
                IsSoundOn.Value = value;
            }
        }
        /// <summary>为各分类音量设置实例比较器，避免修改相同值时重复通知。</summary>
        internal AudioKitSettings()
        {
            SoundVolume.WithComparer((a, b) => a == b);
            MusicVolume.WithComparer((a, b) => a == b);
            VoiceVolume.WithComparer((a, b) => a == b);
        }
        /// <summary>注销全部监听器并清除运行时输出组引用。由 AudioKit.Shutdown 调用。</summary>
        public void Dispose()
        {
            IsSoundOn.UnregisterAll();
            IsMusicOn.UnregisterAll();
            IsVoiceOn.UnregisterAll();
            SoundVolume.UnregisterAll();
            MusicVolume.UnregisterAll();
            VoiceVolume.UnregisterAll();
            IsSoundMuted.UnregisterAll();
            IsMusicMuted.UnregisterAll();
            IsVoiceMuted.UnregisterAll();
            MusicOutputGroup.UnregisterAll();
            VoiceOutputGroup.UnregisterAll();
            SoundOutputGroup.UnregisterAll();
            MusicOutputGroup.SetValueWithoutCallback(null);
            VoiceOutputGroup.SetValueWithoutCallback(null);
            SoundOutputGroup.SetValueWithoutCallback(null);
        }
    }

    /// <summary>在 PlayerPrefs 音量属性上约束有限非负值，并容忍已有非法存储数据。</summary>
    internal sealed class AudioVolumeProperty : PlayerPrefsFloatProperty
    {
        /// <summary>绑定指定存储键，默认音量倍率为一。</summary>
        internal AudioVolumeProperty(string key) : base(key, 1f) { }

        /// <summary>读取持久化值；非法旧值使用默认值但不改写存储。</summary>
        /// <param name="defaultValue">存储缺失或非法时采用的音量倍率。</param>
        protected override float Load(float defaultValue)
        {
            var value = base.Load(defaultValue);
            return AudioSoundOptions.IsFinite(value) && value >= 0f ? value : defaultValue;
        }

        /// <summary>校验后再交由基类写入属性和持久化存储。</summary>
        /// <param name="value">有限非负音量倍率。</param>
        protected override void SetValue(float value)
        {
            Validate(value);
            base.SetValue(value);
        }

        /// <summary>先拒绝非法值，再按比较器决定是否更新和通知。</summary>
        /// <param name="value">有限非负音量倍率。</param>
        public override bool TrySetValue(float value)
        {
            // 必须先校验，不能让调用方的比较器以“未变化”为由跳过非法值检查。
            Validate(value);
            return base.TrySetValue(value);
        }

        /// <summary>拒绝负数、NaN 和无穷大音量。</summary>
        private static void Validate(float value)
        {
            if (!AudioSoundOptions.IsFinite(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Audio volume must be finite and nonnegative.");
            }
        }
    }
}
