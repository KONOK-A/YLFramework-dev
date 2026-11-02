using System;
using UnityEngine;

namespace YLFramework.Audio
{
    public static partial class AudioKit
    {
        private static int _clipCacheCapacity;
        private static double _clipCacheIdleSeconds;
        /// <summary>最大闲置 Clip 条目数，默认 0 禁缓存；不包含等待者、播放或 Source 池。</summary>
        public static int ClipCacheCapacity
        {
            get => _clipCacheCapacity;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (IsClosing)
                {
                    throw new InvalidOperationException("AudioKit is shutting down.");
                }

                _clipCacheCapacity = value;
                if (AudioManager.TryGetInstance(out var host))
                {
                    host.ClipCache.Tick(Time.realtimeSinceStartupAsDouble);
                }
            }
        }
        /// <summary>闲置资源的 unscaled 存活秒数，默认 0 禁缓存。重新取得取消闲置到期；活跃责任不受影响。</summary>
        public static double ClipCacheIdleSeconds
        {
            get => _clipCacheIdleSeconds;
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (IsClosing)
                {
                    throw new InvalidOperationException("AudioKit is shutting down.");
                }

                _clipCacheIdleSeconds = value;
                if (AudioManager.TryGetInstance(out var host))
                {
                    host.ClipCache.Tick(Time.realtimeSinceStartupAsDouble);
                }
            }
        }
        /// <summary>只释放本 Audio 会话的闲置 Clip 缓存，不释放仍有等待者/播放/显式租约的资源。</summary>
        public static void ClearClipCache()
        {
            if (AudioManager.TryGetInstance(out var host))
            {
                host.ClipCache.ClearIdle();
            }
        }
        /// <summary>显式同步预加载/保活；调用方必须 Dispose 返回租约。不同后端不合并所有权。</summary>
        /// <param name="address">Provider 实例与该实例内的完整资源键。</param>
        /// <returns>调用方独立持有的 Clip 租约；归还不保证底层资源立即卸载。</returns>
        public static AudioClipLease AcquireClip(AudioClipAddress address)
        {
            address.Validate();
            return AudioManager.Instance.ClipCache.Load(address);
        }
        /// <summary>直接播放接管缺省分类前终结旧命名等待者，并确认释放重入未改变提交版本。</summary>
        internal static bool InvalidateNamed(AudioCategory category, AudioPlayEnd reason = AudioPlayEnd.Replaced)
        {
            if (!AudioManager.TryGetInstance(out var host))
            {
                return true;
            }

            var version = host.PendingVersion(category, null);
            host.CancelPending(category, null, reason);
            return host && host.IsAvailable && host.PendingVersion(category, null) == version + 1;
        }
        /// <summary>校验命名地址和请求快照，登记独立等待身份后按同步或异步路径取得资源。</summary>
        private static AudioPlayRequest PlayNamed(AudioClipAddress address, AudioCategory category, AudioChannel channel,
            AudioSoundOptions options, Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished,
            bool asynchronous, float crossfade = -1f, AudioSource source = null, bool borrowed = false)
        {
            address.Validate();
            channel?.RequireValid();
            var snapshot = AudioPlaybackOptions.Capture(options, channel == null && category == AudioCategory.Music);
            if (category != AudioCategory.Sound)
            {
                snapshot.Priority = 0;
                snapshot.PlaySoundMode = null;
            }
            snapshot.Validate(false);
            var host = channel?.Owner ?? AudioManager.Instance;
            if (borrowed)
            {
                if (channel == null || channel.Mode != AudioChannelMode.Exclusive)
                {
                    throw new InvalidOperationException("Borrowing requires an exclusive channel.");
                }

                host.ValidateBorrowedSource(source);
            }
            var request = new AudioPlayRequest(host, address, category, channel, snapshot, started, finished, asynchronous, crossfade, source, borrowed);
            host.AddPending(request);
            request.Load(asynchronous);
            return request;
        }
        /// <summary>同步取得命名 Music 并提交到原双声部内核；省略 options 仍默认循环。资产加载同步不等于后台解码同步。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">捕获的 2D 配置，null 使用 MusicDefaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，可在入口返回前同步通知。</param>
        /// <param name="onFinished">自然完成且原资源责任归还前的实际 Clip 和原句柄。</param>
        public static AudioHandle PlayMusicNamed(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlayNamed(address, AudioCategory.Music, null, options, onStarted, onFinished, false).Handle;
        /// <summary>异步取得 Music，等待音频数据就绪后接纳；最新有效独占请求决定提交，失败不停止旧播放。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">入口捕获的 2D 配置，null 使用 MusicDefaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，Provider 可同步完成并触发通知。</param>
        /// <param name="onFinished">自然完成且原资源责任归还前的实际 Clip 和原句柄。</param>
        public static AudioPlayRequest PlayMusicAsync(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlayNamed(address, AudioCategory.Music, null, options, onStarted, onFinished, true);
        /// <summary>同步取得 Voice，省略 options 不循环；资源由原请求全终态归还。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">捕获的 2D 配置，null 使用 Defaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，可在入口返回前同步通知。</param>
        /// <param name="onFinished">自然完成且原资源责任归还前的实际 Clip 和原句柄。</param>
        public static AudioHandle PlayVoiceNamed(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlayNamed(address, AudioCategory.Voice, null, options, onStarted, onFinished, false).Handle;
        /// <summary>异步取得 Voice，加载中句柄无效；不物理取消共享 producer。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">入口捕获的 2D 配置，null 使用 Defaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，Provider 可同步完成并触发通知。</param>
        /// <param name="onFinished">自然完成且原资源责任归还前的实际 Clip 和原句柄。</param>
        public static AudioPlayRequest PlayVoiceAsync(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            PlayNamed(address, AudioCategory.Voice, null, options, onStarted, onFinished, true);
        /// <summary>同步取得 Sound，沿用 Sound 去重/容量/优先级接纳，不将 Clip 缓存混作 Source 预算。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">捕获的 2D Sound 配置，null 使用 Defaults。</param>
        /// <param name="onFinished">仅自然完成时通知实际 Clip 和原句柄，随后归还原资源责任。</param>
        /// <param name="onStarted">接纳并启动后的实际 Clip 和原句柄，可在入口返回前同步通知。</param>
        public static AudioHandle PlaySoundNamed(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onFinished = null, Action<AudioClip, AudioHandle> onStarted = null) =>
            PlayNamed(address, AudioCategory.Sound, null, options, onStarted, onFinished, false).Handle;
        /// <summary>异步 Sound 请求各自等待/取消，同完整 Provider key 合并在途取得，不合并播放生命周期。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">入口捕获的 2D Sound 配置，null 使用 Defaults。</param>
        /// <param name="onFinished">仅自然完成时通知实际 Clip 和原句柄，随后归还原资源责任。</param>
        /// <param name="onStarted">接纳并启动后的实际 Clip 和原句柄，Provider 可同步完成并触发通知。</param>
        public static AudioPlayRequest PlaySoundAsync(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onFinished = null, Action<AudioClip, AudioHandle> onStarted = null) =>
            PlayNamed(address, AudioCategory.Sound, null, options, onStarted, onFinished, true);
        /// <summary>同步取得后使用原 Music crossfade；尾轨自己的后端租约保活到过渡终结。</summary>
        /// <param name="address">目标曲目的显式 Provider 与完整资源键。</param>
        /// <param name="duration">有限非负过渡秒数；零立即替换旧音乐。</param>
        /// <param name="options">目标 2D 配置，null 使用 MusicDefaults。</param>
        /// <param name="onStarted">目标首次提交 Play 后的实际 Clip 和原句柄，通知前已设置过渡增益。</param>
        /// <param name="onFinished">仅目标自然完成时通知，不把尾轨替换视为自然完成。</param>
        public static AudioHandle CrossFadeMusicNamed(AudioClipAddress address, float duration, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null)
        {
            ValidateNamedCrossfade(duration);
            return PlayNamed(address, AudioCategory.Music, null, options, onStarted, onFinished, false, duration).Handle;
        }
        /// <summary>异步取得后才开始原 Music crossfade；加载期间不截断旧曲或尾轨。</summary>
        /// <param name="address">目标曲目的显式 Provider 与完整资源键。</param>
        /// <param name="duration">有限非负过渡秒数，目标接纳且有效参与轨可推进时才计时。</param>
        /// <param name="options">入口捕获的目标 2D 配置，null 使用 MusicDefaults。</param>
        /// <param name="onStarted">目标首次提交 Play 后的实际 Clip 和原句柄，可在异步入口返回前同步通知。</param>
        /// <param name="onFinished">仅目标自然完成时通知，不把尾轨替换视为自然完成。</param>
        public static AudioPlayRequest CrossFadeMusicAsync(AudioClipAddress address, float duration, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null)
        {
            ValidateNamedCrossfade(duration);
            return PlayNamed(address, AudioCategory.Music, null, options, onStarted, onFinished, true, duration);
        }
        /// <summary>在登记命名请求前拒绝负数或非有限的过渡时长。</summary>
        private static void ValidateNamedCrossfade(float duration)
        {
            if (!AudioSoundOptions.IsFinite(duration) || duration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }
        }
        /// <summary>将通道分类、借源选项和回调汇入统一命名加载路径。</summary>
        internal static AudioPlayRequest PlayChannelNamed(AudioChannel channel, AudioClipAddress address, AudioSoundOptions options,
            Action<AudioClip, AudioHandle> started, Action<AudioClip, AudioHandle> finished, bool asynchronous,
            AudioSource source = null, bool borrowed = false) =>
            PlayNamed(address, channel.Category, channel, options, started, finished, asynchronous, source: source, borrowed: borrowed);
    }

    public sealed partial class AudioChannel
    {
        /// <summary>同步命名播放，复用本通道接纳/借用资源收尾。省略 options 为非循环 Defaults。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">本次捕获的配置，null 使用非循环 Defaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，可在入口返回前同步通知。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄。</param>
        public AudioHandle PlayNamed(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            AudioKit.PlayChannelNamed(this, address, options, onStarted, onFinished, false).Handle;
        /// <summary>异步命名播放；Exclusive 以最新有效请求提交，Overlap 保留各自等待者。</summary>
        /// <param name="address">显式 Provider 与完整资源键。</param>
        /// <param name="options">入口捕获的配置，null 使用非循环 Defaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，Provider 可同步完成并触发通知。</param>
        /// <param name="onFinished">自然结束且清理前的实际 Clip 和原句柄。</param>
        public AudioPlayRequest PlayAsync(AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            AudioKit.PlayChannelNamed(this, address, options, onStarted, onFinished, true);
        /// <summary>同步取得命名 Clip 后借用 Source；仅 Exclusive，省略 options 为非循环 Defaults。加载或替换前校验失败不停止旧请求。</summary>
        /// <remarks>主线程调用。入口要求 Source 有效、启用、已停止且未被借用；提交前再次校验，不抢占旧播放。
        /// 资源由原 Voice 全终态归还；字段恢复、caller 控制限制与 PlayBorrowed 相同，不恢复先前播放历史。
        /// 替换释放旧资源后发生的重入冲突会终结新提交，不回滚旧播放；原请求不能覆盖嵌套取得 Source 的新 owner。</remarks>
        /// <param name="source">调用方显式让渡控制的有效、启用且已停止音源。</param>
        /// <param name="address">要取得的 Clip 的显式 Provider 与完整资源键。</param>
        /// <param name="options">本次捕获的配置，null 使用非循环 Defaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，可在入口返回前同步通知。</param>
        /// <param name="onFinished">自然结束且资源归还、借源恢复前的实际 Clip 和原句柄。</param>
        public AudioHandle PlayBorrowedNamed(AudioSource source, AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            AudioKit.PlayChannelNamed(this, address, options, onStarted, onFinished, false, source, true).Handle;
        /// <summary>异步取得命名 Clip 后借用 Source；仅 Exclusive。加载期间不占用、停止或修改 Source。</summary>
        /// <remarks>入口须满足 PlayBorrowed 的源校验；加载就绪时再次校验，占用冲突以 Failed/Error 结束而不抢占。
        /// 等待中的源失活或销毁由原请求 Tick 结束为 Unavailable；取消只撤销自身等待者/播放，迟到结果成对归还。</remarks>
        /// <param name="source">调用方显式让渡控制的音源，入口及实际提交时均须满足借用条件。</param>
        /// <param name="address">要取得的 Clip 的显式 Provider 与完整资源键。</param>
        /// <param name="options">入口捕获的配置，null 使用非循环 Defaults。</param>
        /// <param name="onStarted">首次提交 Play 后的实际 Clip 和原句柄，Provider 可同步完成并触发通知。</param>
        /// <param name="onFinished">自然结束且资源归还、借源恢复前的实际 Clip 和原句柄。</param>
        public AudioPlayRequest PlayBorrowedAsync(AudioSource source, AudioClipAddress address, AudioSoundOptions options = null,
            Action<AudioClip, AudioHandle> onStarted = null, Action<AudioClip, AudioHandle> onFinished = null) =>
            AudioKit.PlayChannelNamed(this, address, options, onStarted, onFinished, true, source, true);
    }
}
