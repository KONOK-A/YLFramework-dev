using System.Collections.Generic;

namespace YLFramework.StateMachine
{
    /// <summary>按参数哈希记录尚未置位的 Trigger 次数，与参数当前的置位值分开维护。</summary>
    internal sealed class FsmTriggerQueue
    {
        private readonly Dictionary<int, int> _pending = new Dictionary<int, int>();
        /// <summary>累加一次待触发次数，达到整数上限后保持饱和。</summary>
        /// <param name="key">Trigger 参数哈希。</param>
        public void Enqueue(int key)
        {
            int count = GetCount(key);
            if (count < int.MaxValue)
            {
                _pending[key] = count + 1;
            }
        }
        /// <summary>取得指定 Trigger 尚未置位的次数。</summary>
        /// <param name="key">Trigger 参数哈希。</param>
        /// <returns>排队次数；不存在时为零。</returns>
        public int GetCount(int key) => _pending.TryGetValue(key, out int count) ? count : 0;
        /// <summary>仅在当前 Trigger 已清零时取出一次排队触发，不负责写入参数值。</summary>
        /// <param name="key">Trigger 参数哈希。</param>
        /// <param name="unset">当前参数是否已清零。</param>
        /// <returns>是否取出一次待触发记录。</returns>
        public bool TryTake(int key, bool unset)
        {
            int count = GetCount(key);
            if (!unset || count == 0)
            {
                return false;
            }

            if (count == 1)
            {
                _pending.Remove(key);
            }
            else
            {
                _pending[key] = count - 1;
            }

            return true;
        }
        /// <summary>覆盖目标列表以取得待触发参数快照，允许遍历期间修改队列。</summary>
        /// <param name="keys">接收参数哈希的可复用列表。</param>
        public void CopyKeysTo(List<int> keys)
        {
            keys.Clear();
            keys.AddRange(_pending.Keys);
        }
        /// <summary>丢弃指定 Trigger 的全部排队次数，不修改其当前参数值。</summary>
        /// <param name="key">Trigger 参数哈希。</param>
        public void Reset(int key) => _pending.Remove(key);
        /// <summary>丢弃所有 Trigger 的排队次数。</summary>
        public void Clear() => _pending.Clear();
    }
}
