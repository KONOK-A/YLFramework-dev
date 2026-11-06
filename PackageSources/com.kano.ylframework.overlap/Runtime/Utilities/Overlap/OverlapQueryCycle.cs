namespace YLFramework.Overlap
{
    /// <summary>管理单个组件的查询重入、停用失效和容量提示状态，不提供跨线程同步。</summary>
    internal sealed class OverlapQueryCycle
    {
        private bool _isChecking;
        private bool _hasReportedCapacity;
        /// <summary>当前结果是否已因停用失效，直到下次成功开始查询才重置。</summary>
        public bool IsInvalidated
        {
            get; private set;
        }

        /// <summary>无查询执行时进入保护状态并重置失效标记，同实例递归调用返回 false。</summary>
        public bool TryBegin()
        {
            if (_isChecking)
            {
                return false;
            }

            _isChecking = true;
            IsInvalidated = false;
            return true;
        }

        /// <summary>解除执行保护，不改变结果失效标记或容量提示状态。</summary>
        public void End() => _isChecking = false;

        /// <summary>标记结果失效并重置容量提示，但保留正在执行的查询保护直到 End。</summary>
        public void Invalidate()
        {
            IsInvalidated = true;
            _hasReportedCapacity = false;
        }

        /// <summary>在本轮启用期间首次达到容量时返回 true，低于容量不会重置提示记录。</summary>
        /// <param name="count">查询写入的结果数量。</param>
        /// <param name="capacity">查询缓冲区容量。</param>
        /// <returns>是否需要提示可能截断，不表示已确认存在遗漏。</returns>
        public bool ReportCapacity(int count, int capacity)
        {
            if (count < capacity || _hasReportedCapacity)
            {
                return false;
            }

            _hasReportedCapacity = true;
            return true;
        }
    }
}
