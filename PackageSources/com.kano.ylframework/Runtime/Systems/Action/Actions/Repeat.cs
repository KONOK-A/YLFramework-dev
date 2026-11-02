using System;

namespace YLFramework.Actions
{
    /// <summary>
    /// 按指定轮数重复执行子序列的组合接口。
    /// </summary>
    /// <remarks>正数为总轮数；-1 和 0 均表示无限重复。</remarks>
    public interface IRepeat : ISequence
    {
    }

    /// <summary>
    /// 重复执行内部顺序序列的池化动作，在仍需重复时重置已完成的子序列。
    /// </summary>
    public class Repeat : IRepeat
    {
        private Sequence _sequence = null;

        private int _repeatCount = -1;
        private int _currentRepeatCount = 0;

        private static ActionPool<Repeat> _pool =
            new ActionPool<Repeat>(() => new Repeat(), null, 5, action =>
            {
                action.ActionId = ActionKit.IdGenerator++;
                action.IsDeinitialized = false;
                try
                {
                    action._sequence = Sequence.Allocate();
                    action.Reset();
                }
                catch (Exception original)
                {
                    try
                    {
                        action.Deinit();
                    }
                    catch (Exception cleanup)
                    {
                        original.Data["ActionKit.CleanupException"] = original.Data["ActionKit.CleanupException"] is Exception previous
                            ? new AggregateException(previous, cleanup) : cleanup;
                    }
                    throw;
                }
            });

        /// <summary>仅供对象池创建重复组合，内部序列在每次分配时另行取得。</summary>
        private Repeat()
        {
        }

        /// <summary>
        /// 分配并初始化重复动作。
        /// </summary>
        /// <param name="repeatCount">总轮数；-1 或 0 无限重复，其他负数不执行且不自动完成。</param>
        /// <returns>尚未启动的重复组合，子动作可通过 Append 添加。</returns>
        public static Repeat Allocate(int repeatCount = -1)
        {
            var repeat = _pool.Allocate();
            repeat._repeatCount = repeatCount;
            return repeat;
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

        /// <summary>清零已完成轮数并检查子动作是否已取消；本次不推进子序列。</summary>
        public void OnStart()
        {
            _currentRepeatCount = 0;
            if (IsDeinitialized)
            {
                return;
            }

            var sequence = _sequence;
            try
            {
                sequence.TryCancelIfChildDeinited();
            }
            finally
            {
                if (sequence.IsDeinitialized && !IsDeinitialized)
                {
                    Deinit();
                }
            }
        }

        /// <summary>推进当前轮，未达总轮数时重置子序列，下一次更新再开始新一轮。</summary>
        /// <param name="dt">传给当前子序列的时间增量，单位为秒。</param>
        public void OnExecute(float dt)
        {
            if (IsDeinitialized)
            {
                return;
            }

            var sequence = _sequence;
            try
            {
                if (sequence.TryCancelIfChildDeinited())
                {
                    return;
                }
                // -1 与 0 均无限重复；一轮结束后只重置，下一次执行才推进新一轮。
                if (_repeatCount == -1 || _repeatCount == 0)
                {
                    var finished = sequence.Execute(dt);
                    if (IsDeinitialized || sequence.IsDeinitialized)
                    {
                        return;
                    }

                    if (finished)
                    {
                        sequence.Reset();
                    }
                }
                else if (_currentRepeatCount < _repeatCount)
                {
                    var finished = sequence.Execute(dt);
                    if (IsDeinitialized || sequence.IsDeinitialized)
                    {
                        return;
                    }

                    if (finished)
                    {
                        _currentRepeatCount++;

                        if (_currentRepeatCount >= _repeatCount)
                        {
                            this.Finish();
                        }
                        else
                        {
                            sequence.Reset();
                        }
                    }
                }
            }
            finally
            {
                if (sequence.IsDeinitialized && !IsDeinitialized)
                {
                    Deinit();
                }
            }
        }

        /// <inheritdoc />
        public void OnFinish()
        {
        }

        /// <inheritdoc />
        public ISequence Append(IAction action)
        {
            _sequence.Append(action);
            return this;
        }

        /// <summary>释放内部子序列并安排延迟回收；取消不触发正常完成通知。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                // 取消时释放内部序列并安排延迟回收，不标记正常完成。
                var sequence = _sequence;
                _sequence = null;
                try
                {
                    if (sequence != null && !sequence.IsDeinitialized)
                    {
                        sequence.Deinit();
                    }
                }
                catch (Exception exception)
                {
                    throw new AggregateException(exception);
                }
                finally
                {
                    ActionQueue.AddCallback(new ActionQueueRecycleCallback<Repeat>(_pool, this));
                }
            }
        }

        /// <summary>重置轮数、执行阶段和子序列进度，保留总轮数和子动作配置。</summary>
        public void Reset()
        {
            _currentRepeatCount = 0;
            Status = ActionStatus.NotStart;
            IsPaused = false;
            _sequence?.Reset();
        }
    }

    /// <summary>
    /// 向组合追加重复子序列的链式扩展。
    /// </summary>
    public static class RepeatExtension
    {
        /// <summary>
        /// 立即配置并追加无限重复的子序列。
        /// </summary>
        /// <param name="self">要追加重复子序列的父组合。</param>
        /// <param name="repeatSetting">追加前立即执行的重复子序列配置回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Repeat(this ISequence self, Action<IRepeat> repeatSetting)
        {
            var repeat = global::YLFramework.Actions.Repeat.Allocate();
            repeatSetting(repeat);
            return self.Append(repeat);
        }

        /// <summary>
        /// 立即配置并追加按指定总轮数重复的子序列。
        /// </summary>
        /// <param name="self">要追加重复子序列的父组合。</param>
        /// <param name="repeatCount">总轮数；-1 或 0 无限重复，其他负数不执行且不自动完成。</param>
        /// <param name="repeatSetting">追加前立即执行的重复子序列配置回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Repeat(this ISequence self, int repeatCount, Action<IRepeat> repeatSetting)
        {
            var repeat = global::YLFramework.Actions.Repeat.Allocate(repeatCount);
            repeatSetting(repeat);
            return self.Append(repeat);
        }
    }
}
