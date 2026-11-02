using System;
using UnityEngine;
using YLFramework.Actions;

namespace YLFramework.Audio
{
    /// <summary>播放借用 Clip 并等待原请求结束的动作；取消只停止本次播放，不销毁调用方的 Clip。</summary>
    public sealed class PlaySoundAction : AbstractAction<PlaySoundAction>, IActionExecutionVersion
    {
        private AudioClip _clip;
        private AudioHandle _handle;
        private AudioPlaybackOptions _options;
        private Action _onFinish;
        private Action<AudioClip, AudioHandle> _onStarted;
        private ulong _version;
        ulong IActionExecutionVersion.ExecutionVersion => _version;
        private bool _hasNotified;
        /// <summary>以默认请求选项和可选输出路由分配音效动作。</summary>
        /// <param name="clip">由调用方保活至播放结束的 Clip。</param>
        /// <param name="onFinish">动作完成通知，不等同于音频自然完成；取消不触发此通知。</param>
        /// <param name="outputRoute">可选的分类继承、显式 Mixer 组或直接输出路由。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄；允许在播放入口返回前取消或 Reset。</param>
        public static PlaySoundAction Allocate(AudioClip clip, Action onFinish = null, AudioOutputRoute outputRoute = default,
            Action<AudioClip, AudioHandle> onStarted = null)
        {
            outputRoute.Validate();
            var options = AudioPlaybackOptions.Defaults;
            options.OutputRoute = outputRoute;
            var action = Allocate();
            action._clip = clip;
            action._onFinish = onFinish;
            action._options = options;
            action._onStarted = onStarted;
            return action;
        }
        /// <summary>以完整请求配置分配音效动作，包含按 Clip 位置计算的自动淡变。</summary>
        /// <param name="clip">由调用方保活的 Clip；缺失时在动作启动阶段拒播。</param>
        /// <param name="options">分配时复制的配置，null 使用 Defaults。</param>
        /// <param name="onFinish">动作完成通知，不等同于音频自然完成；取消不触发此通知。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄；允许在播放入口返回前取消或 Reset。</param>
        public static PlaySoundAction AllocateWithOptions(AudioClip clip, AudioSoundOptions options, Action onFinish = null,
            Action<AudioClip, AudioHandle> onStarted = null)
        {
            var captured = AudioPlaybackOptions.Capture(options);
            captured.Validate(false);
            if (clip)
            {
                captured.ValidateClip(clip);
            }

            var action = Allocate();
            action._clip = clip;
            action._onFinish = onFinish;
            action._options = captured;
            action._onStarted = onStarted;
            return action;
        }
        /// <summary>提交本代次的 Sound 请求，启动回调重入改变动作身份时停止原播放，不接管新执行。</summary>
        public override void OnStart()
        {
            var id = ActionId;
            var version = _version;
            var onStarted = _onStarted;
            var handle = AudioKit.PlaySoundWithSnapshot(_clip, _options, onFinished: (clip, finished) =>
            {
                if (IsDeinitialized || ActionId != id || _version != version)
                {
                    return;
                }

                _handle = default;
                this.Finish();
            }, onStarted: onStarted == null ? null : (clip, started) =>
            {
                if (IsDeinitialized || ActionId != id || _version != version)
                {
                    started.Stop();
                    return;
                }
                _handle = started;
                // 直接递归 Execute 不能重复启动同一次执行；Reset 则通过版本递增开启新执行。
                Status = ActionStatus.Started;
                try
                {
                    onStarted(clip, started);
                }
                catch
                {
                    started.Stop();
                    if (!IsDeinitialized && ActionId == id && _version == version &&
                        ReferenceEquals(_handle.Voice, started.Voice) && _handle.Generation == started.Generation)
                    {
                        _handle = default;
                        Status = ActionStatus.NotStart;
                    }
                    throw;
                }
                if (IsDeinitialized || ActionId != id || _version != version)
                {
                    started.Stop();
                }
            });
            if (IsDeinitialized || ActionId != id || _version != version)
            {
                handle.Stop();
                return;
            }
            _handle = handle;
            CheckPlayback();
        }
        /// <summary>轮询原句柄是否已结束，不用帧间隔推算音频播放进度。</summary>
        /// <param name="dt">动作系统传入的帧间隔，本动作不用于计时。</param>
        public override void OnExecute(float dt) => CheckPlayback();
        /// <summary>原句柄失效即完成动作，涵盖拒播、停止和自然结束，不把它们都视为音频自然完成。</summary>
        private void CheckPlayback()
        {
            if (!_handle.IsValid)
            {
                this.Finish();
            }
        }
        /// <summary>每次执行至多通知一次动作完成，并在回调前标记避免重入重复通知。</summary>
        public override void OnFinish()
        {
            if (_hasNotified)
            {
                return;
            }

            _hasNotified = true;
            _onFinish?.Invoke();
        }
        /// <summary>提升执行版本并停止原句柄，保留 Clip 与配置模板供下一次执行。</summary>
        protected override void OnReset()
        {
            _version++;
            _hasNotified = false;
            _handle.Stop();
            _handle = default;
        }
        /// <summary>先终结原执行，再清空借用资源和回调引用，供动作池回收。</summary>
        protected override void OnDeinit()
        {
            OnReset();
            _clip = null;
            _onFinish = null;
            _onStarted = null;
            _options = default;
        }
    }
    /// <summary>为 ActionKit 序列追加直接借用 Clip 的音效动作，不提供命名资源加载。</summary>
    public static class PlaySoundExtension
    {
        /// <summary>向序列追加采用默认选项和可选输出路由的音效动作。</summary>
        /// <param name="self">接收音效动作的序列。</param>
        /// <param name="clip">由调用方保活至播放结束的 Clip。</param>
        /// <param name="outputRoute">可选的分类继承、显式 Mixer 组或直接输出路由。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄。</param>
        public static ISequence PlaySound(this ISequence self, AudioClip clip, AudioOutputRoute outputRoute = default,
            Action<AudioClip, AudioHandle> onStarted = null) =>
            self.Append(PlaySoundAction.Allocate(clip, outputRoute: outputRoute, onStarted: onStarted));
        /// <summary>向序列追加采用完整请求配置的音效动作，包含按 Clip 位置计算的自动淡变。</summary>
        /// <param name="self">接收音效动作的序列。</param>
        /// <param name="clip">由调用方保活至播放结束的 Clip。</param>
        /// <param name="options">分配动作时复制的配置，null 使用 Defaults。</param>
        /// <param name="onStarted">首次启动后的实际 Clip 和原句柄。</param>
        public static ISequence PlaySoundWithOptions(this ISequence self, AudioClip clip, AudioSoundOptions options,
            Action<AudioClip, AudioHandle> onStarted = null) =>
            self.Append(PlaySoundAction.AllocateWithOptions(clip, options, onStarted: onStarted));
    }
}
