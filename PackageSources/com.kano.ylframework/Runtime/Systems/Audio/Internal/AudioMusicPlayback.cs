using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace YLFramework.Audio
{
    /// <summary>以两个固定音乐声部管理缺省播放和交叉淡变，通过过渡代次避免回调重入控制后续过渡。</summary>
    internal sealed class AudioMusicPlayback
    {
        private readonly AudioManager _owner;
        private readonly AudioVoice _first;
        private readonly AudioVoice _second;
        private AudioHandle _incoming;
        private AudioHandle _outgoing;
        private double _elapsed;
        private double _duration;
        private float _outgoingGain;
        private bool _isTransitioning;
        private bool _ignoreTimeScale;
        private ulong _controlVersion;
        private AudioCrossfadeCurve _curve;
        internal AudioVoice Current
        {
            get; private set;
        }
        internal AudioHandle Outgoing => _outgoing.IsValid ? _outgoing : default;
        internal ulong Epoch
        {
            get; private set;
        }
        internal AudioCrossfadeCurve CurrentCurve => _curve;

        /// <summary>建立两个固定声部并选择初始当前槽，不在此处创建 AudioSource。</summary>
        internal AudioMusicPlayback(AudioManager owner)
        {
            _owner = owner;
            _first = new AudioVoice(owner, AudioVoice.Kind.Music);
            _second = new AudioVoice(owner, AudioVoice.Kind.Music);
            Current = _first;
        }

        /// <summary>替换当前轨及尾轨；释放回调改变了过渡代次时放弃外层提交。</summary>
        internal AudioHandle Play(AudioClip clip, in AudioPlaybackOptions options,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished, AudioResourceLease resource = null)
        {
            var epoch = Epoch;
            Stop(AudioRequestEnd.Replaced);
            if (!_owner.IsAvailable || Epoch != epoch + 1 || resource != null && !resource.CanBind)
            {
                return default;
            }

            var settings = AudioKit.Settings;
            return Current.Begin(clip, in options, settings.IsMusicOn.Value, settings.MusicVolume.Value,
                settings.IsMusicMuted.Value, started, finished, resource: resource);
        }

        /// <summary>将原当前轨转为尾轨并在另一槽接纳新曲；重复过渡先清除更旧尾轨。</summary>
        internal AudioHandle CrossFade(AudioClip clip, float duration, in AudioPlaybackOptions options,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished, bool ignoreTimeScale,
            AudioCrossfadeCurve curve, AudioResourceLease resource = null)
        {
            var settings = AudioKit.Settings;
            if (!settings.IsMusicOn.Value)
            {
                return Play(clip, in options, started, finished, resource);
            }

            var paused = Current.IsActive && Current.IsManuallyPaused;
            if (duration == 0f)
            {
                var previousEpoch = Epoch;
                Stop(AudioRequestEnd.Replaced);
                if (!_owner.IsAvailable || Epoch != previousEpoch + 1 || resource != null && !resource.CanBind)
                {
                    return default;
                }

                return Current.Begin(clip, in options, true, settings.MusicVolume.Value, settings.IsMusicMuted.Value,
                    started, finished, paused: paused, resource: resource);
            }
            var epoch = ++Epoch;
            if (_outgoing.IsValid)
            {
                _outgoing.Voice.Stop(AudioRequestEnd.Replaced);
            }

            if (!_owner.IsAvailable || Epoch != epoch || resource != null && !resource.CanBind)
            {
                return default;
            }

            _outgoing = Current.Handle;
            if (_outgoing.IsValid)
            {
                Current.SuppressCompletion();
            }

            _outgoingGain = Current.TransitionGain;
            Current = ReferenceEquals(Current, _first) ? _second : _first;
            _elapsed = 0d;
            _duration = duration;
            _ignoreTimeScale = ignoreTimeScale;
            _isTransitioning = true;
            _curve = curve;
            try
            {
                var incoming = Current;
                var result = incoming.Begin(clip, in options, true, settings.MusicVolume.Value, settings.IsMusicMuted.Value,
                    started, finished, startImmediately: false, initialTransitionGain: 0f, paused: paused, resource: resource);
                if (Epoch != epoch)
                {
                    return result;
                }

                _incoming = result;
                incoming.StartConfigured(result.Generation);
                return result;
            }
            catch
            {
                if (Epoch == epoch)
                {
                    Stop(AudioRequestEnd.Failed);
                }

                throw;
            }
        }

        /// <summary>所有有效参与轨均可推进时更新公共过渡时钟，完成后替换尾轨。</summary>
        internal void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (!_owner.IsAvailable || !_isTransitioning)
            {
                return;
            }

            var incoming = _incoming.IsValid;
            var outgoing = _outgoing.IsValid;
            if (!incoming && !outgoing)
            {
                CancelTransition();
                return;
            }
            if (incoming && !_incoming.Voice.CanAdvanceTransition || outgoing && !_outgoing.Voice.CanAdvanceTransition)
            {
                return;
            }

            var delta = _ignoreTimeScale ? unscaledDeltaTime : deltaTime;
            if (delta <= 0f)
            {
                return;
            }

            _elapsed = Math.Min(_duration, _elapsed + delta);
            var progress = (float)(_elapsed / _duration);
            var incomingGain = progress;
            var outgoingScale = 1f - progress;
            if (_curve == AudioCrossfadeCurve.EqualPower)
            {
                var angle = progress * Mathf.PI * .5f;
                incomingGain = Mathf.Sin(angle);
                outgoingScale = Mathf.Cos(angle);
            }
            if (incoming)
            {
                _incoming.Voice.SetTransitionGain(incomingGain);
            }

            if (outgoing)
            {
                _outgoing.Voice.SetTransitionGain(_outgoingGain * outgoingScale);
            }

            if (_elapsed >= _duration)
            {
                CancelTransition();
            }
        }

        /// <summary>先清除过渡身份并提升代次，再结束原尾轨；不直接停止当前轨。</summary>
        private void CancelTransition(AudioRequestEnd reason = AudioRequestEnd.Replaced)
        {
            Epoch++;
            var outgoing = _outgoing;
            _outgoing = default;
            _incoming = default;
            _isTransitioning = false;
            _elapsed = _duration = 0d;
            _curve = AudioCrossfadeCurve.Linear;
            if (outgoing.IsValid)
            {
                outgoing.Voice.Stop(reason);
            }
        }
        /// <summary>停止调用进入时两个槽的原请求，不误停释放回调重入产生的新代次。</summary>
        internal void Stop(AudioRequestEnd reason = AudioRequestEnd.Stopped)
        {
            var first = _first.Handle;
            var second = _second.Handle;
            CancelTransition(reason);
            if (first.IsValid)
            {
                first.Voice.Stop(reason);
            }

            if (second.IsValid)
            {
                second.Voice.Stop(reason);
            }
        }
        /// <summary>给当前轨和尾轨添加手动暂停，公共过渡随参与轨一起冻结。</summary>
        internal void Pause()
        {
            _controlVersion++;
            Current.Handle.Pause();
            _outgoing.Pause();
        }
        /// <summary>解除当前轨与原尾轨的手动暂停；重入改变控制或过渡代次时不继续恢复尾轨。</summary>
        internal void Resume()
        {
            var control = ++_controlVersion;
            var epoch = Epoch;
            var current = Current.Handle;
            var outgoing = _outgoing;
            current.Resume();
            if (Epoch == epoch && _controlVersion == control)
            {
                outgoing.Resume();
            }
        }
        /// <summary>仅在原过渡代次内处理启动回调失败，必要时终结整个当前过渡。</summary>
        internal void StartFailed(AudioHandle failed, ulong epoch)
        {
            if (Epoch != epoch)
            {
                return;
            }

            if (ReferenceEquals(Current, failed.Voice) && failed.IsValid)
            {
                Stop(AudioRequestEnd.Failed);
            }
            else if (failed.IsValid)
            {
                failed.Voice.Stop(AudioRequestEnd.Failed);
            }
        }
        /// <summary>关闭分类时清除尾轨并保留当前请求供重播，启用时恢复原有效参与轨。</summary>
        internal void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                var previousEpoch = Epoch;
                var disabling = Current.Handle;
                CancelTransition();
                if (!_owner.IsAvailable || Epoch != previousEpoch + 1 || AudioKit.CurrentSettings.IsMusicOn.Value)
                {
                    return;
                }

                if (disabling.IsValid)
                {
                    disabling.Voice.SetEnabled(false);
                    disabling.Voice.SetTransitionGain(1f);
                }
                return;
            }
            var control = ++_controlVersion;
            var epoch = Epoch;
            var current = Current.Handle;
            var outgoing = _outgoing;
            if (current.IsValid)
            {
                current.Voice.SetEnabled(true);
            }

            if (Epoch == epoch && _controlVersion == control && outgoing.IsValid)
            {
                outgoing.Voice.SetEnabled(true);
            }
        }
        /// <summary>将分类音量同步到当前轨和仍有效的尾轨，保留各自包络与过渡增益。</summary>
        internal void SetVolume(float value)
        {
            Current.SetVolume(value);
            if (_outgoing.IsValid)
            {
                _outgoing.Voice.SetVolume(value);
            }
        }
        /// <summary>同步当前轨与有效尾轨的静音，不改变过渡时钟。</summary>
        internal void SetMute(bool value)
        {
            Current.SetMute(value);
            if (_outgoing.IsValid)
            {
                _outgoing.Voice.SetMute(value);
            }
        }
        /// <summary>将分类默认组变化应用到当前轨及尾轨中继承路由的请求。</summary>
        internal void SetInheritedOutputGroup(AudioMixerGroup group)
        {
            Current.SetInheritedOutputGroup(group);
            if (_outgoing.IsValid)
            {
                _outgoing.Voice.SetInheritedOutputGroup(group);
            }
        }
        /// <summary>追加当前轨和有效尾轨的句柄，供宿主按代次快照检查。</summary>
        internal void CopyTo(List<AudioHandle> handles)
        {
            if (Current.IsActive)
            {
                handles.Add(Current.Handle);
            }

            if (_outgoing.IsValid)
            {
                handles.Add(_outgoing);
            }
        }
        /// <summary>以关闭原因终结播放，并在清理失败时仍尝试销毁两个自有音源。</summary>
        internal void DestroySources()
        {
            try
            {
                Stop(AudioRequestEnd.Shutdown);
            }
            finally
            {
                try
                {
                    _first.DestroySource();
                }
                finally
                {
                    _second.DestroySource();
                }
            }
        }
    }
}
