using System;
using UnityEngine;

namespace YLFramework.Actions
{
    // 按执行器传入的 dt 累计等待时长；完成检测在累加之前，因此跨越阈值的下一次执行才会完成。
    internal class Delay : IAction
    {
        /// <summary>本轮等待时长，单位为秒；动态时长工厂会在启动时覆盖此值。</summary>
        public float DelayTime;

        /// <summary>每次启动时求值一次的可选时长工厂，返回等待秒数。</summary>
        public Func<float> DelayTimeFactory = null;

        /// <summary>检测到等待结束时调用的回调，Deinit 只清除而不调用。</summary>
        public System.Action OnDelayFinish
        {
            get; set;
        }

        /// <summary>启动后各次执行累计的秒数，Reset 时清零。</summary>
        public float CurrentSeconds
        {
            get; set;
        }

        /// <summary>仅供对象池创建延迟动作，等待配置在 Allocate 时设置。</summary>
        private Delay()
        {
        }

        private static readonly ActionPool<Delay> _pool =
            new ActionPool<Delay>(() => new Delay(), null, 10);

        /// <summary>分配固定时长的等待动作，清除上次使用的动态时长工厂。</summary>
        /// <param name="delayTime">等待秒数；非正数仍需在启动之后执行一次才完成。</param>
        /// <param name="onDelayFinish">检测到等待结束时调用的可选回调。</param>
        public static Delay Allocate(float delayTime, System.Action onDelayFinish = null)
        {
            var retNode = _pool.Allocate();
            retNode.ActionId = ActionKit.IdGenerator++;
            retNode.IsDeinitialized = false;
            retNode.Reset();
            retNode.DelayTime = delayTime;
            retNode.DelayTimeFactory = null;
            retNode.OnDelayFinish = onDelayFinish;
            retNode.CurrentSeconds = 0.0f;
            return retNode;
        }

        /// <summary>分配动态时长的等待动作，时长在每次启动时求值。</summary>
        /// <param name="delayTimeFactory">可选时长工厂；为 null 时沿用对象中已有的 DelayTime。</param>
        /// <param name="onDelayFinish">检测到等待结束时调用的可选回调。</param>
        public static Delay Allocate(Func<float> delayTimeFactory, System.Action onDelayFinish = null)
        {
            var retNode = _pool.Allocate();
            retNode.ActionId = ActionKit.IdGenerator++;
            retNode.IsDeinitialized = false;
            retNode.Reset();
            retNode.DelayTimeFactory = delayTimeFactory;
            retNode.OnDelayFinish = onDelayFinish;
            retNode.CurrentSeconds = 0.0f;
            return retNode;
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

        /// <summary>存在动态时长工厂时求值并保存本轮时长，不在启动时检测完成。</summary>
        public void OnStart()
        {
            if (DelayTimeFactory != null)
            {
                DelayTime = DelayTimeFactory();
            }
        }

        /// <summary>先比较已有累计时间，达到时长则标记完成并调用回调，随后累加本次 dt。</summary>
        /// <param name="dt">本次累计的秒数；本次累加跨过阈值时，下一次执行才检测到完成。</param>
        public void OnExecute(float dt)
        {
            if (CurrentSeconds >= DelayTime)
            {
                this.Finish();
                OnDelayFinish?.Invoke();
            }

            CurrentSeconds += dt;
        }

        /// <summary>不追加结束处理，等待结束回调由 OnExecute 调用。</summary>
        public void OnFinish()
        {
        }

        /// <summary>清零累计时间并重置执行阶段及暂停标记，保留时长配置和回调。</summary>
        public void Reset()
        {
            Status = ActionStatus.NotStart;
            IsPaused = false;
            CurrentSeconds = 0.0f;
        }

        /// <inheritdoc />
        public bool IsPaused
        {
            get; set;
        }

        /// <summary>清除时长工厂与回调并安排延迟回收，不发出等待结束通知。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                OnDelayFinish = null;
                DelayTimeFactory = null;
                IsDeinitialized = true;
                ActionQueue.AddCallback(new ActionQueueRecycleCallback<Delay>(_pool, this));
            }
        }

        /// <inheritdoc />
        public bool IsDeinitialized
        {
            get; set;
        }
    }

    /// <summary>
    /// 向组合追加按累计时间等待的动作。
    /// </summary>
    /// <remarks>先比较已有累计时间再加入当前 dt，跨过时长后在下一次执行检测到完成。</remarks>
    public static class DelayExtension
    {
        /// <summary>
        /// 追加等待指定秒数的动作，时间由执行传入的 dt 累计。
        /// </summary>
        /// <param name="self">要追加等待动作的父组合。</param>
        /// <param name="seconds">等待秒数；非正数在启动后的首次执行完成。</param>
        /// <param name="onDelayFinish">检测到等待结束时调用的可选回调，取消不触发。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Delay(this ISequence self, float seconds, Action onDelayFinish = null)
        {
            return self.Append(global::YLFramework.Actions.Delay.Allocate(seconds, onDelayFinish));
        }

        /// <summary>
        /// 追加动态时长等待动作，每次启动时才求值时长，而非追加时求值。
        /// </summary>
        /// <param name="self">要追加等待动作的父组合。</param>
        /// <param name="delayTimeFactory">每次启动时调用一次的时长工厂，返回秒数。</param>
        /// <param name="onDelayFinish">检测到等待结束时调用的可选回调，取消不触发。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Delay(this ISequence self, Func<float> delayTimeFactory, Action onDelayFinish = null)
        {
            return self.Append(global::YLFramework.Actions.Delay.Allocate(delayTimeFactory, onDelayFinish));
        }
    }
}
