namespace YLFramework.Overlap
{
    /// <summary>Completeness of one synchronous query, not ownership of its Unity objects.</summary>
    public enum OverlapQueryStatus
    {
        Invalidated = 0,
        Complete = 1,
        MayBeTruncated = 2
    }

    /// <summary>An explicit reason for a query that could not produce a reliable snapshot.</summary>
    public enum OverlapQueryInvalidationReason
    {
        None = 0,
        InvalidScene = 1,
        TriggerQueriesDisabled = 2,
        WorkspaceRequired = 3,
        UnsupportedGeometry = 4,
        GeometryBudgetExceeded = 5,
        InvalidGeometry = 6,
        UnsupportedCast = 7
    }
}
