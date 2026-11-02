using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Actions
{
    // 回收回调只是"把对象还回池子"的延迟动作，本身不区分类型，因此用非泛型接口存放在同一队列。
    internal interface IActionQueueCallback
    {
        /// <summary>执行一次延迟回收或丢弃操作，不重新调用动作的业务清理。</summary>
        void Call();
    }

    // 延迟回收项：保存对象所属池、动作及丢弃策略，不再执行动作的业务清理。
    internal struct ActionQueueRecycleCallback<T> : IActionQueueCallback where T : class
    {
        /// <summary>持有本次分配所有权、接收归还或丢弃请求的池。</summary>
        public ActionPool<T> Pool;
        /// <summary>已尝试领域清理、等待还池或丢弃的对象。</summary>
        public T Action;
        private readonly bool _discard;
        private readonly System.Func<T, bool> _discardWhen;

        /// <summary>保存延迟处理对象与策略，是否还池在 Call 时决定。</summary>
        /// <param name="pool">拥有该对象的池。</param>
        /// <param name="action">需要结束本次借用的对象。</param>
        /// <param name="discard">是否直接丢弃；为 true 时不再求值 discardWhen。</param>
        /// <param name="discardWhen">可选延迟判定函数，返回 true 时丢弃而不缓存。</param>
        public ActionQueueRecycleCallback(ActionPool<T> pool, T action, bool discard = false,
            System.Func<T, bool> discardWhen = null)
        {
            Pool = pool;
            Action = action;
            _discard = discard;
            _discardWhen = discardWhen;
        }

        /// <summary>执行预定的归还或丢弃策略，结束时清空池与动作引用，即使操作抛出异常。</summary>
        public void Call()
        {
            try
            {
                if (_discard || (_discardWhen != null && _discardWhen(Action)))
                {
                    Pool.Discard(Action);
                }
                else
                {
                    Pool.Recycle(Action);
                }
            }
            finally
            {
                Pool = null;
                Action = null;
            }
        }
    }

    // 延迟回收队列：动作清理时只入队，常规运行由驱动器 Update 处理；显式 Drain 会立即消费当前批次。
    internal static class ActionQueue
    {
        private static readonly Queue<IActionQueueCallback> _callbacks = new Queue<IActionQueueCallback>();
        private static bool _isDraining;

        internal static int PendingCount => _callbacks.Count;

        /// <summary>加入回收项；播放模式下尝试确保驱动器存在，编辑模式仅入队，留待显式 Drain。</summary>
        public static void AddCallback(IActionQueueCallback callback)
        {
            _callbacks.Enqueue(callback);
            if (Application.isPlaying)
            {
                ActionRecycleDriver.EnsureCreated();
            }
        }

        // 按本轮初始数量消费回收项；回调异常中断本轮，已出队项不重放，嵌套 Drain 不重复进入。
        internal static void Drain()
        {
            if (_isDraining)
            {
                return;
            }
            // 清空过程中加入的回收项留到下一轮，不递归消费回调新产生的工作。
            var count = _callbacks.Count;
            _isDraining = true;
            try
            {
                for (var index = 0; index < count; index++)
                {
                    _callbacks.Dequeue().Call();
                }
            }
            finally
            {
                _isDraining = false;
            }
        }
    }
}
