namespace YLFramework.Audio
{
    /// <summary>无需分配的 Sound 会话统计快照，覆盖请求、闲置声部缓存和自有音源，不包含 Music 与 Voice。</summary>
    /// <remarks>音源释放计数表示已放弃所有权并请求销毁，不表示 Unity 已同步销毁原生对象。</remarks>
    public readonly struct AudioSoundStats
    {
        /// <summary>当前有效请求数，包含暂停、静音和循环中的音效。</summary>
        public int ActiveCount
        {
            get;
        }
        /// <summary>自上次统计重置以来的有效请求数峰值。</summary>
        public int PeakActiveCount
        {
            get;
        }
        /// <summary>当前闲置声部缓存数量，不含活跃请求。</summary>
        public int CachedCount
        {
            get;
        }
        /// <summary>本会话当前拥有的 Sound 音源数，包含闲置音源，不含借入音源。</summary>
        public int OwnedSourceCount
        {
            get;
        }
        /// <summary>已提交接纳的累计请求数，包含随后启动失败的请求。</summary>
        public long AdmittedCount
        {
            get;
        }
        /// <summary>Sound 配置或启动路径捕获到异常的累计次数。</summary>
        public long StartFailureCount
        {
            get;
        }
        /// <summary>被帧窗口去重拒绝的累计请求数。</summary>
        public long DedupRejectedCount
        {
            get;
        }
        /// <summary>Clip 容量未阻拦、仅因全局容量被拒绝的累计请求数。</summary>
        public long GlobalRejectedCount
        {
            get;
        }
        /// <summary>因 Clip 容量被拒绝的累计请求数，包含 Clip 与全局容量同时已满的情况。</summary>
        public long ClipRejectedCount
        {
            get;
        }
        /// <summary>因优先级低于可替换候选而被拒绝的累计请求数。</summary>
        public long PriorityRejectedCount
        {
            get;
        }
        /// <summary>为接纳新请求而停止旧请求的累计次数。</summary>
        public long StolenCount
        {
            get;
        }
        /// <summary>为满足运行时容量变更而停止请求的累计次数。</summary>
        public long TrimmedCount
        {
            get;
        }
        /// <summary>播放分配命中闲置声部缓存的累计次数，不含预热。</summary>
        public long CacheHitCount
        {
            get;
        }
        /// <summary>播放分配需要新声部的累计次数，不含预热和借源播放。</summary>
        public long CacheMissCount
        {
            get;
        }
        /// <summary>实际创建 Sound AudioSource 的累计次数，包含预热。</summary>
        public long SourceCreatedCount
        {
            get;
        }
        /// <summary>放弃 Sound 音源所有权并请求销毁的累计次数。</summary>
        public long SourceReleasedCount
        {
            get;
        }

        /// <summary>复制累计计数及当前资源量，形成不再随会话变化的值快照。</summary>
        internal AudioSoundStats(AudioSoundCounters counters, int active, int cached)
        {
            ActiveCount = active;
            CachedCount = cached;
            PeakActiveCount = counters.PeakActiveCount;
            OwnedSourceCount = counters.OwnedSourceCount;
            AdmittedCount = counters.AdmittedCount;
            StartFailureCount = counters.StartFailureCount;
            DedupRejectedCount = counters.DedupRejectedCount;
            GlobalRejectedCount = counters.GlobalRejectedCount;
            ClipRejectedCount = counters.ClipRejectedCount;
            PriorityRejectedCount = counters.PriorityRejectedCount;
            StolenCount = counters.StolenCount;
            TrimmedCount = counters.TrimmedCount;
            CacheHitCount = counters.CacheHitCount;
            CacheMissCount = counters.CacheMissCount;
            SourceCreatedCount = counters.SourceCreatedCount;
            SourceReleasedCount = counters.SourceReleasedCount;
        }
    }

    /// <summary>保存本 Audio 会话的可变 Sound 累计计数与自有音源量。</summary>
    internal struct AudioSoundCounters
    {
        internal int PeakActiveCount;
        internal int OwnedSourceCount;
        internal long AdmittedCount;
        internal long StartFailureCount;
        internal long DedupRejectedCount;
        internal long GlobalRejectedCount;
        internal long ClipRejectedCount;
        internal long PriorityRejectedCount;
        internal long StolenCount;
        internal long TrimmedCount;
        internal long CacheHitCount;
        internal long CacheMissCount;
        internal long SourceCreatedCount;
        internal long SourceReleasedCount;
    }
}
