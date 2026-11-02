using System;

namespace YLFramework.Audio
{
    /// <summary>Sound 活跃请求达到容量上限时的接纳策略。</summary>
    public enum SoundOverflowPolicy
    {
        /// <summary>拒绝新请求，不停止旧声音，也不记录本次去重身份。</summary>
        RejectNew = 0,
        /// <summary>先停止最早接纳的活跃声音，再接纳新请求。</summary>
        StopOldest = 1,
        /// <summary>替换候选中优先级最低、同级最早接纳的请求；新请求优先级更低时拒绝接纳。</summary>
        StopLowestPriority = 2
    }

    /// <summary>仅运行时生效的 Sound 请求容量；暂停、静音和循环仍计数，Music 与 Voice 不计入。</summary>
    /// <remarks>零值配置为不限容量且使用 RejectNew。降低上限时，RejectNew 保留较早请求，StopOldest 保留较新请求。
    /// StopLowestPriority 保留高优先级请求，同级保留较新请求；Clip 与全局容量使用同一策略。</remarks>
    [Serializable]
    public struct AudioSoundConcurrency
    {
        /// <summary>活跃 Sound 请求总上限；零表示不限，负值非法。</summary>
        public int Limit;
        /// <summary>容量已满时的接纳策略，同时决定降低容量后保留哪些请求。</summary>
        public SoundOverflowPolicy OverflowPolicy;
        /// <summary>每个 AudioClip 实例的默认请求上限；零表示不限，实例覆盖规则优先，但不能绕过全局上限。</summary>
        public int DefaultClipLimit;

        /// <summary>在应用容量前拒绝负数上限和未定义的溢出策略。</summary>
        internal void Validate()
        {
            if (Limit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Limit));
            }

            if (DefaultClipLimit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(DefaultClipLimit));
            }

            if ((uint)OverflowPolicy > (uint)SoundOverflowPolicy.StopLowestPriority)
            {
                throw new ArgumentOutOfRangeException(nameof(OverflowPolicy));
            }
        }
    }
}
