using System.Collections.Generic;

namespace YLFramework.StateMachine
{
    /// <summary>以帧号限制同步连锁切换次数，避免零时间间隔的过渡环递归不止。</summary>
    internal sealed class FsmTransitionBudget
    {
        private readonly Dictionary<string, int> _visits = new Dictionary<string, int>();
        private int _frame = -1;
        private int _total;
        private bool _isBlocked;
        /// <summary>为一次目标进入计数；同帧总数超过 256 或单状态超过 32 后阻止本帧后续请求。</summary>
        /// <param name="frameCount">当前帧号，变化后自动重置预算。</param>
        /// <param name="state">本次请求的目标状态名。</param>
        /// <returns>当前请求是否仍在预算内。</returns>
        public bool TryEnter(int frameCount, string state)
        {
            if (_frame != frameCount)
            {
                Clear();
                _frame = frameCount;
            }
            if (_isBlocked)
            {
                return false;
            }

            _visits.TryGetValue(state, out int count);
            _visits[state] = ++count;
            _isBlocked = ++_total > 256 || count > 32;
            return !_isBlocked;
        }
        /// <summary>清空访问计数及阻塞标记，供实例关闭或新帧重新计数。</summary>
        public void Clear()
        {
            _visits.Clear();
            _frame = -1;
            _total = 0;
            _isBlocked = false;
        }
    }
}
