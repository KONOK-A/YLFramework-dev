using System;

namespace YLFramework.Actions
{
    // 即时回调动作：OnStart 中调用一次回调并立刻标记完成，没有逐帧执行阶段；适合插入序列中做同步通知。
    internal class CallbackAction : IAction
    {
        /// <summary>仅供对象池创建回调动作，具体回调由 Allocate 绑定。</summary>
        private CallbackAction()
        {
        }

        private Action _callback;

        private static ActionPool<CallbackAction> _pool =
            new ActionPool<CallbackAction>(() => new CallbackAction(), null, 10);

        /// <summary>取得新使用周期的回调动作并保存回调，不在分配时调用。</summary>
        /// <param name="callback">启动时同步执行的可选回调。</param>
        public static CallbackAction Allocate(Action callback)
        {
            var callbackAction = _pool.Allocate();
            callbackAction.ActionId = ActionKit.IdGenerator++;
            callbackAction.Reset();
            callbackAction.IsDeinitialized = false;
            callbackAction._callback = callback;
            return callbackAction;
        }

        /// <inheritdoc />
        public bool IsPaused
        {
            get; set;
        }
        /// <inheritdoc />
        public bool IsDeinitialized
        {
            get; set;
        }
        /// <inheritdoc />
        public ulong ActionId
        {
            get; set;
        }
        /// <inheritdoc />
        public ActionStatus Status
        {
            get; set;
        }

        /// <summary>同步调用回调，正常返回后标记完成；回调异常直接向外传播。</summary>
        public void OnStart()
        {
            _callback?.Invoke();
            this.Finish();
        }

        /// <summary>不执行逐帧工作，回调已由启动钩子处理。</summary>
        /// <param name="dt">本次时间增量，此动作不使用。</param>
        public void OnExecute(float dt)
        {
        }

        /// <summary>不追加结束处理，业务回调只在启动时调用。</summary>
        public void OnFinish()
        {
        }

        /// <summary>清除回调并安排一次延迟回收，不调用业务回调。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                _callback = null;
                ActionQueue.AddCallback(new ActionQueueRecycleCallback<CallbackAction>(_pool, this));
            }
        }

        /// <summary>重置执行阶段和暂停标记，保留供下一轮启动使用的回调。</summary>
        public void Reset()
        {
            IsPaused = false;
            Status = ActionStatus.NotStart;
        }
    }

    /// <summary>
    /// 向组合追加同步回调动作的链式扩展。
    /// </summary>
    public static class CallbackExtension
    {
        /// <summary>
        /// 追加在启动时调用回调并立即完成的动作，空回调也会完成。
        /// </summary>
        /// <param name="self">要追加回调动作的父组合。</param>
        /// <param name="callback">动作启动时执行的回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Callback(this ISequence self, Action callback)
        {
            return self.Append(global::YLFramework.Actions.CallbackAction.Allocate(callback));
        }
    }
}
