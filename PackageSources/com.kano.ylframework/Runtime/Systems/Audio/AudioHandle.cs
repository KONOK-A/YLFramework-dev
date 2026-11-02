using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>一次播放的代次身份，不是 Clip 资源所有权；过期或已回收身份上的控制操作不生效。</summary>
    public readonly struct AudioHandle
    {
        internal readonly AudioVoice Voice;
        internal readonly ulong Generation;
        /// <summary>捕获当前 Voice 及其代次，避免旧句柄控制复用后的新请求。</summary>
        internal AudioHandle(AudioVoice voice)
        {
            Voice = voice;
            Generation = voice.Generation;
        }
        /// <summary>该 lease 是否仍指向可用宿主上的有效请求；过期或已被回收的 lease 均为无效。</summary>
        public bool IsValid => Voice != null && Voice.IsValid(Generation);
        /// <summary>底层 AudioSource 当前是否正在播放。静音时仍为 true，因此不能作为能否听见的依据。</summary>
        public bool IsPlaying => IsValid && Voice.Source && Voice.Source.isPlaying;
        /// <summary>该请求是否被手动、命名通道或 opt-in 自动暂停。等待分类通道重新启用不计为暂停。</summary>
        public bool IsPaused => IsValid && Voice.IsPaused;
        /// <summary>当前 Clip 位置，单位为秒；过期或尚未开始时为零。暂停冻结位置，静音不冻结。</summary>
        /// <remarks>循环播放报告当前循环的位置；压缩音频沿用 AudioSource.time 的数据包精度，不保留已结束播放的历史。</remarks>
        public float Time => IsValid ? Voice.PlaybackTime : 0f;
        /// <summary>Clip 长度，单位为秒；包含等待分类通道启用的有效请求，不可用或过期时为零。</summary>
        public float Duration => IsValid ? Voice.Duration : 0f;
        /// <summary>当前循环的归一化位置 [0,1]；不可用或过期时为零，自然完成回调中也不保证为一。</summary>
        public float Progress
        {
            get
            {
                var duration = Duration;
                return duration > 0f ? Mathf.Clamp01(Time / duration) : 0f;
            }
        }
        /// <summary>暂停播放；会冻结 Time、淡变包络，以及该 lease 参与的音乐 Crossfade 进度。</summary>
        public void Pause()
        {
            if (IsValid)
            {
                Voice.Pause();
            }
        }
        /// <summary>恢复播放。等待分类通道重新启用的请求会保持停止，直到该通道恢复启用。</summary>
        public void Resume()
        {
            if (IsValid)
            {
                Voice.Resume();
            }
        }
        /// <summary>停止并释放该请求，不触发其自然完成回调。</summary>
        public void Stop()
        {
            if (IsValid)
            {
                Voice.Stop();
            }
        }
        /// <summary>修改本请求的音量倍率，不重播也不修改通道音量；过期句柄忽略操作。</summary>
        /// <param name="volumeScale">有限非负倍率；与当前共享音量相乘并限幅后，再叠加请求淡变和音乐过渡增益。</param>
        /// <exception cref="System.ArgumentOutOfRangeException">句柄有效且倍率为负数或非有限值。</exception>
        public void SetVolumeScale(float volumeScale)
        {
            if (IsValid)
            {
                Voice.SetVolumeScale(volumeScale);
            }
        }
        /// <summary>将本请求的独立包络淡入至满增益；首次正时长淡入从无声开始，否则从当前增益继续。</summary>
        /// <param name="duration">有限非负秒数；零表示立即恢复满增益，过期句柄忽略操作。</param>
        /// <param name="ignoreTimeScale">默认使用不受 timeScale 影响的时间；暂停和等待分类启用始终冻结淡变，静音不冻结。</param>
        /// <remarks>替换本请求原淡变，不改变 Settings、请求音量、Clip、路由或播放身份；按帧推进，不是 DSP 调度。</remarks>
        public void FadeIn(float duration, bool ignoreTimeScale = true)
        {
            if (IsValid)
            {
                Voice.FadeIn(duration, ignoreTimeScale);
            }
        }
        /// <summary>将本请求包络淡出至无声，可选择只停止本次播放；淡出停止不触发自然完成回调。</summary>
        /// <param name="duration">有限非负秒数；零表示立即无声，过期句柄忽略操作。</param>
        /// <param name="stopWhenDone">淡出后停止并释放本请求；false 表示以零包络增益保持活跃。</param>
        /// <param name="ignoreTimeScale">默认使用不受 timeScale 影响的时间；暂停和等待分类启用始终冻结淡变，静音不冻结。</param>
        /// <remarks>新淡变替换旧淡变并从当前增益继续；停止、替换、自然结束和 Shutdown 均清除淡变状态。</remarks>
        public void FadeOut(float duration, bool stopWhenDone = true, bool ignoreTimeScale = true)
        {
            if (IsValid)
            {
                Voice.FadeOut(duration, stopWhenDone, ignoreTimeScale);
            }
        }
        /// <summary>修改本请求音高，不重播也不改变暂停状态；过期句柄忽略操作。</summary>
        /// <param name="pitch">范围为 (0,3] 的有限音高。</param>
        /// <exception cref="System.ArgumentOutOfRangeException">句柄有效且音高非有限或超出 (0,3]。</exception>
        public void SetPitch(float pitch)
        {
            if (IsValid)
            {
                Voice.SetPitch(pitch);
            }
        }
    }
}
