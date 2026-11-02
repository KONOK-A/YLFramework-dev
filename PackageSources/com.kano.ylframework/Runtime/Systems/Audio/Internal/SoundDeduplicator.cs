using System.Collections.Generic;

namespace YLFramework.Audio
{
    // 帧窗口为闭区间：请求所在帧仍在有效窗口内时会拒播。
    // SoundFrames 模式按每个 Clip 自己的记录帧计算；GlobalFrames 模式共用一个窗口起点，
    // 由第一个落在窗口外的接纳重新开始计算，窗口外的旧记录不再拦截请求。
    /// <summary>按 Clip 实例和接纳帧记录 Sound 去重窗口，不管理并发容量或资源保活。</summary>
    internal sealed class SoundDeduplicator
    {
        internal AudioKit.PlaySoundModes DefaultPlaySoundMode = AudioKit.PlaySoundModes.EveryOne;
        internal int GlobalFrameCountForIgnoreSameSound = 5;
        internal int SoundFrameCountForIgnoreSameSound = 5;
        private readonly Dictionary<int, AudioHandle> _global = new Dictionary<int, AudioHandle>();
        private readonly Dictionary<int, RecordData> _sounds = new Dictionary<int, RecordData>();
        private readonly List<int> _expired = new List<int>();
        private int _globalFrame;
        private int _pruneFrame;
        /// <summary>保留单个 Clip 的接纳帧及原播放代次，供完成和回滚时精确匹配。</summary>
        private readonly struct RecordData
        {
            internal readonly int Frame;
            internal readonly AudioHandle Handle;
            /// <summary>捕获本次接纳的帧号与播放身份。</summary>
            internal RecordData(int frame, AudioHandle handle)
            {
                Frame = frame;
                Handle = handle;
            }
        }
        /// <summary>只查询当前请求是否被对应帧窗口阻拦，不提前消耗接纳记录。</summary>
        internal bool CanPlay(int clipId, AudioKit.PlaySoundModes mode, int frame)
        {
            if (mode == AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)
            {
                return frame < _globalFrame || frame - _globalFrame > GlobalFrameCountForIgnoreSameSound || !_global.ContainsKey(clipId);
            }

            if (mode == AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames && _sounds.TryGetValue(clipId, out var record))
            {
                return frame < record.Frame || frame - record.Frame > SoundFrameCountForIgnoreSameSound;
            }

            return true;
        }
        /// <summary>在容量接纳后登记去重身份，必要时开启新的全局窗口。</summary>
        internal void Record(int clipId, AudioKit.PlaySoundModes mode, int frame, AudioHandle handle)
        {
            if (mode == AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)
            {
                if (frame < _globalFrame || frame - _globalFrame > GlobalFrameCountForIgnoreSameSound)
                {
                    _globalFrame = frame;
                    _global.Clear();
                }
                _global[clipId] = handle;
            }
            else if (mode == AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames)
            {
                _sounds[clipId] = new RecordData(frame, handle);
            }
        }
        /// <summary>自然完成时只移除匹配原代次的单 Clip 窗口记录，不清除全局窗口记录。</summary>
        internal void Finished(int clipId, AudioHandle handle)
        {
            if (_sounds.TryGetValue(clipId, out var record) && ReferenceEquals(record.Handle.Voice, handle.Voice) && record.Handle.Generation == handle.Generation)
            {
                _sounds.Remove(clipId);
            }
        }
        /// <summary>启动失败时撤销本次接纳记录，不覆盖同 Clip 后续代次的记录。</summary>
        internal void Rollback(int clipId, AudioKit.PlaySoundModes mode, AudioHandle handle)
        {
            if (mode == AudioKit.PlaySoundModes.IgnoreSameSoundInGlobalFrames)
            {
                if (_global.TryGetValue(clipId, out var owner) && ReferenceEquals(owner.Voice, handle.Voice) && owner.Generation == handle.Generation)
                {
                    _global.Remove(clipId);
                }
            }
            else if (mode == AudioKit.PlaySoundModes.IgnoreSameSoundInSoundFrames &&
                _sounds.TryGetValue(clipId, out var record) && ReferenceEquals(record.Handle.Voice, handle.Voice) && record.Handle.Generation == handle.Generation)
            {
                _sounds.Remove(clipId);
            }
        }
        /// <summary>按窗口间隔清理过期记录，并处理帧号回退后的旧记录。</summary>
        internal void Prune(int frame)
        {
            // 距上次 Prune 未超过 SoundFrames 窗口长度时，本帧无需扫描。
            if (frame >= _pruneFrame && frame - _pruneFrame <= SoundFrameCountForIgnoreSameSound)
            {
                return;
            }

            _pruneFrame = frame;
            _expired.Clear();
            foreach (var pair in _sounds)
            {
                if (frame < pair.Value.Frame || frame - pair.Value.Frame > SoundFrameCountForIgnoreSameSound)
                {
                    _expired.Add(pair.Key);
                }
            }

            foreach (var key in _expired)
            {
                _sounds.Remove(key);
            }

            _expired.Clear();
            if (frame < _globalFrame || frame - _globalFrame > GlobalFrameCountForIgnoreSameSound)
            {
                _global.Clear();
            }
        }
    }
}
