namespace YLFramework.StateMachine
{
    /// <summary>按脚本下标保留创建标记，使进入中断后仍能分别完成各脚本的首次创建。</summary>
    internal sealed class FsmCreationTracker
    {
        private readonly bool[] _created;
        /// <summary>为固定数量的脚本建立未创建标记。</summary>
        /// <param name="count">节点持有的脚本数量。</param>
        public FsmCreationTracker(int count) => _created = new bool[count];
        /// <summary>查询指定脚本是否已被首次创建流程访问。</summary>
        /// <param name="index">脚本在节点中的下标。</param>
        /// <returns>是否已记录创建标记。</returns>
        public bool IsCreated(int index) => _created[index];
        /// <summary>在执行用户创建回调前记录标记，不因回调中断或抛错撤销。</summary>
        /// <param name="index">脚本在节点中的下标。</param>
        /// <returns>首次标记时返回 true，否则返回 false。</returns>
        public bool TryMarkCreated(int index)
        {
            if (_created[index])
            {
                return false;
            }

            _created[index] = true;
            return true;
        }
    }
}
