namespace YLFramework.Overlap
{
    /// <summary>Counts for the caller's buffer. Unity objects remain borrowed and may be destroyed later.</summary>
    public readonly struct OverlapQueryResult3D
    {
        public int Count
        {
            get;
        }
        public int RawCount
        {
            get;
        }
        public OverlapQueryStatus Status
        {
            get;
        }
        public OverlapQueryInvalidationReason InvalidationReason
        {
            get;
        }

        internal OverlapQueryResult3D(int count, int rawCount, OverlapQueryStatus status,
            OverlapQueryInvalidationReason invalidationReason = OverlapQueryInvalidationReason.None)
        {
            Count = count;
            RawCount = rawCount;
            Status = status;
            InvalidationReason = invalidationReason;
        }
    }
}
