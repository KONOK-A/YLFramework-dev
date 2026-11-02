using System;
using UnityEngine;
using UnityEngine.Audio;

namespace YLFramework.Audio
{
    /// <summary>承载一份可复用播放记录，统一管理音源、暂停原因、音量包络及原请求资源责任。</summary>
    internal sealed class AudioVoice
    {
        private readonly AudioManager _owner;
        /// <summary>决定分类设置、Sound 池回收和固定槽归属的内核分类。</summary>
        internal enum Kind
        {
            Sound,
            Music,
            Voice
        }
        internal readonly Kind Category;
        internal bool IsSound => Category == Kind.Sound;
        internal readonly bool PooledSound;
        internal ulong AdmissionOrder;
        internal int ClipId;
        internal int Priority;
        internal AudioClip Clip => _clip;
        internal float TransitionGain { get; private set; } = 1f;
        internal bool CanAdvanceTransition => IsActive && _hasStarted && _isEnabled && !IsPaused && _clip && _clip.loadState == AudioDataLoadState.Loaded;
        internal bool IsFollowing => _isFollowing;
        internal AudioSource Source
        {
            get; private set;
        }
        internal ulong Generation
        {
            get; private set;
        }
        internal bool IsActive
        {
            get; private set;
        }
        internal bool IsPaused => _isManuallyPaused || _isAutomaticallyPaused || _isChannelPaused;
        private bool _isManuallyPaused;
        private bool _isAutomaticallyPaused;
        private bool _isChannelPaused;
        internal bool IsManuallyPaused => _isManuallyPaused;
        internal AudioChannel Channel
        {
            get; private set;
        }
        private AudioBorrowedSource _borrowed;
        internal float Duration => Source && _clip && Source.clip ? _clip.length : 0f;
        internal float PlaybackTime => _hasStarted && Source && _clip && Source.clip ? Mathf.Clamp(Source.time, 0f, Duration) : 0f;
        internal AudioHandle Handle => IsActive ? new AudioHandle(this) : default;
        private AudioPlaybackOptions _options;
        internal AudioPlaybackOptions Options => _options;
        internal AudioKit.PlaySoundModes Mode;
        private AudioClip _clip;
        private Transform _target;
        private Vector3 _position;
        private bool _spatial;
        private bool _isFollowing;
        private bool _isEnabled;
        private bool _hasStarted;
        private bool _ownsSource;
        private GameObject _sourceRoot;
        private float _fadeGain = 1f;
        private float _fadeFrom = 1f;
        private float _fadeTarget = 1f;
        private float _fadeDuration;
        private double _fadeElapsed;
        private bool _isFading;
        private bool _hasTouchedFade;
        private bool _fadeStopWhenDone;
        private bool _fadeIgnoreTimeScale = true;
        private bool _isAutoFadeActive;
        private bool _hasCompletedAutoLoop;
        private double _autoLength;
        private double _autoFadeIn;
        private double _autoFadeOut;
        private double _autoPreviousTime;
        private Action<AudioClip, AudioHandle> _onStarted;
        private Action<AudioClip, AudioHandle> _onFinished;
        private AudioResourceLease _resource;
        internal AudioRequestEnd EndReason
        {
            get; private set;
        }
        /// <summary>绑定原宿主与分类，记录 Sound 是否参与池回收；音源延迟创建。</summary>
        internal AudioVoice(AudioManager owner, Kind category, bool pooledSound = true)
        {
            _owner = owner;
            Category = category;
            PooledSound = pooledSound;
        }
        /// <summary>同时检查活跃标记、原代次与宿主可用性，不以音源是否正在播放判断身份。</summary>
        internal bool IsValid(ulong generation) => IsActive && Generation == generation && _owner && _owner.IsAvailable;

        /// <summary>绑定资源与新代次，配置自有或借入音源；按分类启用、暂停和提交选项决定是否立即启动。</summary>
        internal AudioHandle Begin(AudioClip clip, in AudioPlaybackOptions options, bool enabled, float sharedVolume, bool muted,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished,
            bool spatial = false, Vector3 position = default, Transform target = null,
            bool startImmediately = true, float initialTransitionGain = 1f, bool paused = false,
            AudioResourceLease resource = null, AudioChannel channel = null, AudioSource borrowedSource = null)
        {
            if (!_owner.IsAvailable)
            {
                throw new InvalidOperationException("Audio host is unavailable.");
            }

            resource?.Bind();
            _resource = resource;
            Generation++;
            EndReason = AudioRequestEnd.None;
            IsActive = true;
            _isManuallyPaused = paused;
            _hasStarted = false;
            _isEnabled = enabled;
            Channel = channel;
            _isChannelPaused = channel != null && channel.IsPaused;
            _isAutomaticallyPaused = options.AutoPauseWhenTimeScaleZero && Time.timeScale == 0f;
            _clip = clip;
            _options = options;
            _onStarted = started;
            _onFinished = finished;
            var generation = Generation;
            try
            {
                ResetFade();
                TransitionGain = initialTransitionGain;
                _isAutoFadeActive = options.AutoFade.IsEnabled;
                if (_isAutoFadeActive)
                {
                    _autoLength = clip.samples / (double)clip.frequency;
                    options.AutoFade.Normalize(_autoLength, options.Loop, out _autoFadeIn, out _autoFadeOut);
                    ResetAutoPosition();
                    _hasTouchedFade = true;
                }
                _target = target;
                _isFollowing = target;
                _spatial = spatial;
                _position = position;
                if (borrowedSource)
                {
                    _owner.BorrowSource(borrowedSource, this);
                    _borrowed = new AudioBorrowedSource(borrowedSource, clip, in options);
                    Source = borrowedSource;
                }
                else
                {
                    EnsureSource();
                    ResetSource();
                    Source.clip = clip;
                    Source.loop = options.Loop;
                    Source.pitch = options.Pitch;
                    Source.spatialBlend = spatial ? 1f : 0f;
                }
                SetOutput(options.OutputRoute.Resolve(_owner.GetOutputGroup(this)));
                if (spatial && _borrowed == null)
                {
                    Source.minDistance = options.MinDistance;
                    Source.maxDistance = options.MaxDistance;
                    Source.rolloffMode = options.RolloffMode;
                }
                if (spatial)
                {
                    Source.transform.position = position;
                }

                SetVolume(sharedVolume);
                SetMute(muted);
                var handle = new AudioHandle(this);
                resource?.Accept(handle);
                if (_borrowed == null)
                {
                    Source.gameObject.SetActive(true);
                }

                if (IsValid(generation) && enabled && startImmediately)
                {
                    StartSource();
                }

                return handle;
            }
            catch
            {
                if (IsActive && Generation == generation)
                {
                    Stop(AudioRequestEnd.Failed);
                }

                throw;
            }
        }
        /// <summary>组合共享音量、请求倍率和两层包络后写入当前音源。</summary>
        internal void SetVolume(float sharedVolume)
        {
            if (!IsActive || !Source)
            {
                return;
            }

            // 基础音量先限幅，再乘请求淡变和音乐过渡，避免高倍率抵消淡出包络。
            var value = Mathf.Clamp01(sharedVolume * Options.Volume) * _fadeGain * TransitionGain;
            if (_borrowed != null)
            {
                _borrowed.SetVolume(value);
            }
            else
            {
                Source.volume = value;
            }
        }
        /// <summary>只更新音乐过渡增益，并使用最新共享音量重算输出。</summary>
        internal void SetTransitionGain(float value)
        {
            TransitionGain = Mathf.Clamp01(value);
            SetVolume(_owner.GetSharedVolume(this));
        }
        /// <summary>撤销音乐尾轨的自然完成回调，保留原请求的终态与资源收尾。</summary>
        internal void SuppressCompletion() => _onFinished = null;
        /// <summary>仅启动配置完成且仍属于指定代次的请求。</summary>
        internal void StartConfigured(ulong generation)
        {
            if (IsValid(generation))
            {
                StartSource();
            }
        }
        /// <summary>更新当前音源静音，并重算音量以保持最新倍率与包络。</summary>
        internal void SetMute(bool value)
        {
            if (!IsActive || !Source)
            {
                return;
            }

            if (_borrowed != null)
            {
                _borrowed.SetMute(value);
            }
            else
            {
                Source.mute = value;
            }
            // 取消静音应使用最新通道音量和请求倍率，不能恢复过期的 Unity 音量快照。
            SetVolume(_owner.GetSharedVolume(this));
        }
        /// <summary>仅对继承路由的活跃请求更新分类输出组，不覆盖显式组或直接输出。</summary>
        internal void SetInheritedOutputGroup(AudioMixerGroup group)
        {
            if (IsActive && Source && Options.OutputRoute.Mode == AudioOutputRoute.RouteMode.Inherit)
            {
                SetOutput(group ? group : null);
            }
        }
        /// <summary>按音源归属写入输出组，借源路径同时保留最后写入值以供恢复。</summary>
        private void SetOutput(AudioMixerGroup group)
        {
            if (_borrowed != null)
            {
                _borrowed.SetOutput(group);
            }
            else if (Source)
            {
                Source.outputAudioMixerGroup = group;
            }
        }
        /// <summary>校验并替换本请求音量倍率，立即按最新共享音量重算输出。</summary>
        internal void SetVolumeScale(float volumeScale)
        {
            if (!AudioSoundOptions.IsFinite(volumeScale) || volumeScale < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(volumeScale));
            }

            _options.Volume = volumeScale;
            SetVolume(_owner.GetSharedVolume(this));
        }
        /// <summary>校验并更新本请求音高，不重新启动音源。</summary>
        internal void SetPitch(float pitch)
        {
            if (!AudioSoundOptions.IsFinite(pitch) || pitch <= 0 || pitch > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(pitch));
            }

            _options.Pitch = pitch;
            if (_borrowed != null)
            {
                _borrowed.SetPitch(pitch);
            }
            else if (Source)
            {
                Source.pitch = pitch;
            }
        }
        /// <summary>以满增益为目标启动手动淡入，首次正时长淡入可从无声开始。</summary>
        internal void FadeIn(float duration, bool ignoreTimeScale) => BeginFade(1f, duration, false, ignoreTimeScale, true);
        /// <summary>以零增益为目标启动手动淡出，并保留是否停止原请求的选项。</summary>
        internal void FadeOut(float duration, bool stopWhenDone, bool ignoreTimeScale) => BeginFade(0f, duration, stopWhenDone, ignoreTimeScale, false);
        /// <summary>从当前增益接管自动或旧手动包络，零时长立即应用目标和可选停止。</summary>
        private void BeginFade(float target, float duration, bool stopWhenDone, bool ignoreTimeScale, bool fromSilence)
        {
            if (!AudioSoundOptions.IsFinite(duration) || duration < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            if (_isAutoFadeActive)
            {
                RefreshAutoFade();
                _isAutoFadeActive = false;
                _hasTouchedFade = true;
            }
            if (fromSilence && !_hasTouchedFade && duration > 0f)
            {
                _fadeGain = 0f;
            }

            _hasTouchedFade = true;
            _fadeFrom = _fadeGain;
            _fadeTarget = target;
            _fadeDuration = duration;
            _fadeElapsed = 0d;
            _fadeStopWhenDone = stopWhenDone;
            _fadeIgnoreTimeScale = ignoreTimeScale;
            _isFading = duration > 0f;
            if (!_isFading)
            {
                _fadeGain = target;
            }

            SetVolume(_owner.GetSharedVolume(this));
            if (!_isFading && stopWhenDone)
            {
                Stop();
            }
        }
        /// <summary>播放与数据均可推进时更新自动或手动包络；暂停和分类关闭时不累计手动时钟。</summary>
        internal void TickFade(float deltaTime, float unscaledDeltaTime)
        {
            if (!IsActive || !_hasStarted || IsPaused || !_isEnabled || !_clip || _clip.loadState != AudioDataLoadState.Loaded)
            {
                return;
            }

            if (_isAutoFadeActive)
            {
                RefreshAutoFade();
                return;
            }
            if (!_isFading)
            {
                return;
            }

            var delta = _fadeIgnoreTimeScale ? unscaledDeltaTime : deltaTime;
            if (delta <= 0f)
            {
                return;
            }

            _fadeElapsed = Math.Min(_fadeDuration, _fadeElapsed + delta);
            _fadeGain = Mathf.Lerp(_fadeFrom, _fadeTarget, (float)(_fadeElapsed / _fadeDuration));
            _isFading = _fadeElapsed < _fadeDuration;
            SetVolume(_owner.GetSharedVolume(this));
            if (!_isFading && _fadeStopWhenDone)
            {
                Stop();
            }
        }
        /// <summary>清除手动、自动及音乐过渡包络状态，供下一代次重新配置。</summary>
        private void ResetFade()
        {
            _fadeGain = _fadeFrom = _fadeTarget = 1f;
            _fadeDuration = 0f;
            _fadeElapsed = 0d;
            _isFading = _hasTouchedFade = _fadeStopWhenDone = false;
            _fadeIgnoreTimeScale = true;
            _isAutoFadeActive = _hasCompletedAutoLoop = false;
            _autoLength = _autoFadeIn = _autoFadeOut = _autoPreviousTime = 0d;
            TransitionGain = 1f;
        }
        /// <summary>重置自动包络的位置观察和循环首轮标记，准备从头播放。</summary>
        private void ResetAutoPosition()
        {
            _autoPreviousTime = 0d;
            _hasCompletedAutoLoop = false;
            _fadeGain = _autoFadeIn > 0d ? 0f : 1f;
        }
        /// <summary>按采样位置计算自动包络；循环请求只在首轮淡入，不在每轮重复淡变。</summary>
        private void RefreshAutoFade()
        {
            if (!IsActive || !_hasStarted || IsPaused || !_isEnabled || !Source || !_clip || !Source.isPlaying)
            {
                return;
            }

            if (Options.Loop && _hasCompletedAutoLoop)
            {
                return;
            }

            var position = Math.Max(0d, Math.Min(_autoLength, Source.timeSamples / (double)_clip.frequency));
            if (Options.Loop && (position >= _autoFadeIn || position < _autoPreviousTime))
            {
                _hasCompletedAutoLoop = true;
            }

            _autoPreviousTime = position;
            _fadeGain = _hasCompletedAutoLoop ? 1f : AudioAutoFade.Evaluate(position, _autoLength, _autoFadeIn, _autoFadeOut);
            SetVolume(_owner.GetSharedVolume(this));
        }
        /// <summary>确认原音源与目标可用后提交 Play，并以原 Clip 和句柄通知首次启动。</summary>
        private void StartSource()
        {
            if (!IsActive || IsPaused || !_isEnabled)
            {
                return;
            }

            if (!Source || !Source.isActiveAndEnabled || !_clip || _borrowed != null && !_borrowed.OwnsPlayback ||
                _isFollowing && (!_target || !_target.gameObject.activeInHierarchy))
            {
                Stop(AudioRequestEnd.Unavailable);
                return;
            }
            if (_clip.loadState == AudioDataLoadState.Failed)
            {
                Stop(AudioRequestEnd.Failed);
                return;
            }
            if (_isFollowing)
            {
                Source.transform.position = _target.position;
            }
            else if (_spatial)
            {
                Source.transform.position = _position;
            }

            SetOutput(Options.OutputRoute.Resolve(_owner.GetOutputGroup(this)));
            if (_isAutoFadeActive)
            {
                ResetAutoPosition();
                SetVolume(_owner.GetSharedVolume(this));
            }
            var generation = Generation;
            var clip = _clip;
            var handle = Handle;
            var musicEpoch = Category == Kind.Music && Channel == null ? _owner.MusicPlayback.Epoch : 0;
            Source.Play();
            _hasStarted = true;
            var callback = _onStarted;
            _onStarted = null;
            try
            {
                callback?.Invoke(clip, handle);
            }
            catch
            {
                if (Category == Kind.Music && Channel == null)
                {
                    _owner.MusicPlayback.StartFailed(handle, musicEpoch);
                }
                else if (IsActive && Generation == generation)
                {
                    Stop(AudioRequestEnd.Failed);
                }

                throw;
            }
        }
        /// <summary>分类关闭时停止 Source 但保留请求，重新启用后从头启动而非解除暂停。</summary>
        internal void SetEnabled(bool value)
        {
            if (!IsActive || _isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            if (!value)
            {
                if (Source && (_borrowed == null || _borrowed.OwnsPlayback))
                {
                    Source.Stop();
                }
                _hasStarted = false;
            }
            else
            {
                StartSource();
            }
        }
        /// <summary>增加手动暂停原因，不覆盖通道或自动暂停状态。</summary>
        internal void Pause()
        {
            SetPauseReason(ref _isManuallyPaused, true);
        }
        /// <summary>仅解除手动暂停，其他暂停原因仍可阻止恢复。</summary>
        internal void Resume()
        {
            SetPauseReason(ref _isManuallyPaused, false);
        }
        /// <summary>同步命名通道的局部暂停原因。</summary>
        internal void SetChannelPaused(bool value) => SetPauseReason(ref _isChannelPaused, value);
        /// <summary>仅在请求显式启用时跟随 timeScale 的零值状态自动暂停。</summary>
        internal void RefreshAutomaticPause(bool timeScaleZero) => SetPauseReason(ref _isAutomaticallyPaused, Options.AutoPauseWhenTimeScaleZero && timeScaleZero);
        /// <summary>更新单一暂停原因，仅在综合暂停状态改变时暂停、续播或首次启动音源。</summary>
        private void SetPauseReason(ref bool reason, bool value)
        {
            if (!IsActive || reason == value)
            {
                return;
            }

            var wasPaused = IsPaused;
            if (!wasPaused && value && _isAutoFadeActive)
            {
                RefreshAutoFade();
            }

            reason = value;
            if (wasPaused == IsPaused)
            {
                return;
            }

            if (IsPaused)
            {
                if (_hasStarted && Source && (_borrowed == null || _borrowed.OwnsPlayback))
                {
                    Source.Pause();
                }

                return;
            }
            if (!_isEnabled)
            {
                return;
            }

            if (_hasStarted && Source && (_borrowed == null || _borrowed.OwnsPlayback))
            {
                Source.UnPause();
            }
            else
            {
                StartSource();
            }
        }
        /// <summary>检查资源和跟随目标存活、更新空间位置，并在自然结束通知后按原代次收尾。</summary>
        internal void CheckFinished()
        {
            if (!IsActive)
            {
                return;
            }

            if (!Source || !_clip || Source.clip != _clip || !Source.isActiveAndEnabled ||
                _isFollowing && (!_target || !_target.gameObject.activeInHierarchy))
            {
                Stop(AudioRequestEnd.Unavailable);
                return;
            }
            var loadState = _clip.loadState;
            if (loadState == AudioDataLoadState.Failed)
            {
                Stop(AudioRequestEnd.Failed);
                return;
            }
            if (_hasStarted && loadState == AudioDataLoadState.Unloaded)
            {
                Stop(AudioRequestEnd.Unavailable);
                return;
            }
            // Play 可由 Unity 延后到背景数据就绪；不能把等待中的 !isPlaying 当自然结束。
            if (loadState != AudioDataLoadState.Loaded)
            {
                return;
            }

            if (_isFollowing)
            {
                Source.transform.position = _target.position;
            }
            else if (_spatial)
            {
                Source.transform.position = _position;
            }

            if (!_hasStarted || IsPaused || !_isEnabled || Source.loop || Source.isPlaying)
            {
                return;
            }

            var generation = Generation;
            var clip = _clip;
            var handle = Handle;
            if (_isAutoFadeActive)
            {
                _fadeGain = AudioAutoFade.Evaluate(_autoLength, _autoLength, _autoFadeIn, _autoFadeOut);
                SetVolume(_owner.GetSharedVolume(this));
            }
            if (IsSound)
            {
                AudioKit.Channels.Finished(clip.GetInstanceID(), handle);
            }

            var callback = _onFinished;
            _onFinished = null;
            // 回调可停止并复用同一 Voice；旧资源保活到原自然通知返回，之后只释放局部责任。
            var resource = _resource;
            _resource = null;
            try
            {
                resource?.End(AudioRequestEnd.Completed);
                callback?.Invoke(clip, handle);
            }
            finally
            {
                try
                {
                    if (IsActive && Generation == generation)
                    {
                        Stop(AudioRequestEnd.Completed);
                    }
                }
                finally
                {
                    resource?.Dispose();
                }
            }
        }
        /// <summary>先使旧代次失效，再按 Sound 池、命名通道或固定槽归属清理，最后结算原资源责任。</summary>
        internal void Stop(AudioRequestEnd reason = AudioRequestEnd.Stopped)
        {
            if (!IsActive)
            {
                return;
            }

            EndReason = reason;
            IsActive = false;
            Generation++;
            var resource = _resource;
            _resource = null;
            var channel = Channel;
            channel?.Remove(this);
            // Sound 先由 owner 摘除计数，再由池 reset 清租用数据；固定槽不进入 Sound 池。
            try
            {
                if (IsSound)
                {
                    _owner.Recycle(this);
                }
                else if (channel != null)
                {
                    _owner.ReleaseChannelVoice(this);
                }
                else
                {
                    ResetPlayback();
                }
            }
            finally
            {
                resource?.End(reason);
                resource?.Dispose();
            }
        }
        /// <summary>清空播放记录和回调；借源有条件恢复，自有音源复位并停用，不销毁共享 Clip。</summary>
        internal void ResetPlayback()
        {
            _isManuallyPaused = _isAutomaticallyPaused = _isChannelPaused = false;
            _hasStarted = _isEnabled = false;
            _onStarted = _onFinished = null;
            _clip = null;
            _target = null;
            _isFollowing = false;
            _spatial = false;
            _position = Vector3.zero;
            _options = default;
            ResetFade();
            ClipId = Priority = 0;
            Channel = null;
            if (_borrowed != null)
            {
                var borrowed = _borrowed;
                _borrowed = null;
                Source = null;
                try
                {
                    borrowed.Release();
                }
                finally
                {
                    _owner.ReturnBorrowedSource(borrowed.Source, this);
                }
            }
            else
            {
                ResetSource();
                if (Source)
                {
                    Source.gameObject.SetActive(false);
                }
            }
        }
        /// <summary>停止并复位框架自有音源的播放、路由及空间字段，避免池复用继承旧配置。</summary>
        private void ResetSource()
        {
            if (!Source)
            {
                return;
            }

            Source.Stop();
            Source.clip = null;
            Source.playOnAwake = false;
            Source.enabled = true;
            Source.outputAudioMixerGroup = null;
            Source.loop = false;
            Source.volume = 1f;
            Source.pitch = 1f;
            Source.mute = false;
            Source.spatialBlend = 0f;
            Source.minDistance = 1f;
            Source.maxDistance = 50f;
            Source.rolloffMode = AudioRolloffMode.Linear;
            Source.dopplerLevel = 0f;
            Source.transform.localPosition = Vector3.zero;
        }
        /// <summary>按需创建归属本宿主的音源对象，部分创建失败时归还已取得的对象责任。</summary>
        internal void EnsureSource()
        {
            if (Source)
            {
                return;
            }
            // AudioSource 组件可能单独被外部销毁，其 GameObject 仍由本声部负责释放。
            ReleaseSource();
            var root = new GameObject(IsSound ? "Sound" : "Slot");
            _sourceRoot = root;
            root.SetActive(false);
            try
            {
                root.transform.SetParent(_owner.transform, false);
                Source = root.AddComponent<AudioSource>();
                _ownsSource = true;
                _owner.RegisterOwnedSource(Source);
                if (IsSound)
                {
                    _owner.SoundSourceCreated();
                }

                ResetSource();
            }
            catch
            {
                ReleaseSource();
                throw;
            }
        }
        /// <summary>以关闭原因停止原请求，并最终释放自有音源对象；不销毁借入 Source。</summary>
        internal void DestroySource()
        {
            try
            {
                Stop(AudioRequestEnd.Shutdown);
            }
            finally
            {
                ReleaseSource();
            }
        }
        /// <summary>撤销自有音源登记和统计后请求销毁原根对象，不释放 Clip 资产。</summary>
        private void ReleaseSource()
        {
            var root = _sourceRoot;
            _sourceRoot = null;
            if (_ownsSource)
            {
                _owner.UnregisterOwnedSource(Source);
            }

            Source = null;
            if (_ownsSource)
            {
                _ownsSource = false;
                if (IsSound)
                {
                    _owner.SoundSourceReleased();
                }
            }
            if (!root)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(root);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
