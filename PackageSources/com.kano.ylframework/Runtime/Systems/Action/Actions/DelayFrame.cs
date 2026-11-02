using System;
using UnityEngine;

namespace YLFramework.Actions
{
    // 以 Time.frameCount 的全局帧号计时，而不是累计 dt 或执行器更新次数；暂停期间帧号仍在推进。
    internal class DelayFrame : IAction
    {
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

        private static ActionPool<DelayFrame> _pool =
            new ActionPool<DelayFrame>(() => new DelayFrame(), null, 10);

        private Action _onDelayFinish;

        /// <summary>分配帧等待动作，起始帧在启动时记录，而非分配时记录。</summary>
        /// <param name="frameCount">相对启动帧等待的帧数；非正数在启动后的首次执行完成。</param>
        /// <param name="onDelayFinish">检测到帧数满足时调用的可选回调。</param>
        public static DelayFrame Allocate(int frameCount, Action onDelayFinish = null)
        {
            var delayFrame = _pool.Allocate();
            delayFrame.ActionId = ActionKit.IdGenerator++;
            delayFrame.Reset();
            delayFrame.IsDeinitialized = false;
            delayFrame._delayedFrameCount = frameCount;
            delayFrame._onDelayFinish = onDelayFinish;

            return delayFrame;
        }

        private int _startFrameCount;
        private int _delayedFrameCount;

        /// <summary>记录本轮等待的 Unity 起始帧，不检测是否完成。</summary>
        public void OnStart()
        {
            _startFrameCount = Time.frameCount;
        }

        /// <summary>按全局帧号检查等待，满足后先调用回调，再标记完成。</summary>
        /// <param name="dt">本次时间增量；帧等待不使用此值。</param>
        public void OnExecute(float dt)
        {
            if (Time.frameCount >= _startFrameCount + _delayedFrameCount)
            {
                _onDelayFinish?.Invoke();
                this.Finish();
            }
        }

        /// <summary>不追加结束处理，帧等待回调已由 OnExecute 调用。</summary>
        public void OnFinish()
        {
        }

        /// <summary>清除帧等待回调并安排一次延迟回收，取消不发出完成通知。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                _onDelayFinish = null;
                ActionQueue.AddCallback(new ActionQueueRecycleCallback<DelayFrame>(_pool, this));
            }
        }

        /// <summary>清除起始帧并重置执行阶段及暂停标记，保留等待帧数和回调。</summary>
        public void Reset()
        {
            Status = ActionStatus.NotStart;
            IsPaused = false;
            _startFrameCount = 0;
        }
    }

    /// <summary>
    /// 向组合追加按 Unity 全局帧数等待的动作，不按 dt 或执行次数计数。
    /// </summary>
    /// <remarks>暂停动作不冻结全局帧计数，恢复执行时按已过去的帧数判断。</remarks>
    public static class DelayFrameExtension
    {
        /// <summary>
        /// 追加等待指定 Unity 帧数的动作。
        /// </summary>
        /// <param name="self">要追加帧等待动作的父组合。</param>
        /// <param name="frameCount">相对启动帧等待的帧数；非正数在启动后的首次执行完成。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence DelayFrame(this ISequence self, int frameCount)
        {
            return self.Append(global::YLFramework.Actions.DelayFrame.Allocate(frameCount));
        }

        /// <summary>
        /// 追加从动作启动帧起等待一个 Unity 帧的动作。
        /// </summary>
        /// <param name="self">要追加帧等待动作的父组合。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence NextFrame(this ISequence self)
        {
            return self.Append(global::YLFramework.Actions.DelayFrame.Allocate(1));
        }
    }
}
