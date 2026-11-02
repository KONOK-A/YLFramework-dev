using System;

namespace YLFramework.Actions
{
    /// <summary>
    /// 每次启动时只检测一次条件，为真时创建子序列并在后续执行中推进，否则直接完成。
    /// </summary>
    public class ConditionSequence : IAction
    {
        private Func<bool> _condition;
        private Action<ISequence> _sequenceSetting;
        private ISequence _sequence;

        private static ActionPool<ConditionSequence> _pool =
            new ActionPool<ConditionSequence>(() => new ConditionSequence(), null, 10, action =>
            {
                action.ActionId = ActionKit.IdGenerator++;
                action.IsDeinitialized = false;
                try
                {
                    action.Reset();
                }
                catch
                {
                    action.IsDeinitialized = true;
                    action._condition = null;
                    action._sequenceSetting = null;
                    throw;
                }
            });

        /// <summary>仅供对象池创建条件分支，不在构造时判断条件或创建子序列。</summary>
        private ConditionSequence()
        {
        }

        /// <summary>
        /// 分配条件分支动作，配置回调只在启动时条件为真后执行。
        /// </summary>
        /// <param name="condition">每次启动时求值一次的条件，不持续轮询。</param>
        /// <param name="sequenceSetting">条件为真时配置新子序列的回调。</param>
        /// <returns>尚未判断条件的分支动作；配置回调不在分配时执行。</returns>
        public static ConditionSequence Allocate(Func<bool> condition, Action<ISequence> sequenceSetting)
        {
            var whenAction = _pool.Allocate();
            whenAction._condition = condition;
            whenAction._sequenceSetting = sequenceSetting;
            return whenAction;
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
        /// <summary>判断一次条件；为真时配置新子序列，为假时直接标记完成。</summary>
        public void OnStart()
        {
            if (_condition())
            {
                var sequence = ActionKit.Sequence();
                _sequence = sequence;
                try
                {
                    _sequenceSetting.Invoke(sequence);
                    if (sequence.IsDeinitialized)
                    {
                        Deinit();
                    }
                }
                catch (Exception settingException)
                {
                    try
                    {
                        sequence.Deinit();
                    }
                    catch (Exception deinitException)
                    {
                        // 保留配置异常作为主异常，同时记录清理失败信息。
                        settingException.Data["ConditionSequence.DeinitException"] = deinitException;
                    }
                    finally
                    {
                        _sequence = null;
                    }
                    throw;
                }
            }
            else
            {
                this.Finish();
            }
        }

        /// <summary>推进已配置的子序列；子序列取消时取消当前分支。</summary>
        /// <param name="dt">传给子序列的时间增量，单位为秒。</param>
        public void OnExecute(float dt)
        {
            var sequence = _sequence;
            if (sequence == null)
            {
                return;
            }

            if (sequence.IsDeinitialized)
            {
                Deinit();
                return;
            }
            Exception executionError = null;
            try
            {
                if (sequence.Execute(dt) && !sequence.IsDeinitialized)
                {
                    this.Finish();
                }
            }
            catch (Exception exception)
            {
                executionError = exception;
                throw;
            }
            finally
            {
                if (sequence.IsDeinitialized && !IsDeinitialized)
                {
                    try
                    {
                        Deinit();
                    }
                    catch (Exception cleanupError)
                    {
                        if (executionError == null)
                        {
                            throw;
                        }

                        executionError.Data["ConditionSequence.DeinitException"] = cleanupError;
                    }
                }
            }
        }

        /// <summary>正常完成后释放本轮子序列，不清除条件和配置回调。</summary>
        public void OnFinish()
        {
            if (_sequence != null)
            {
                _sequence.Deinit();
                _sequence = null;
            }
        }

        /// <summary>释放子序列并清除条件及配置回调，安排延迟回收。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                try
                {
                    _sequence?.Deinit();
                }
                finally
                {
                    _sequence = null;
                    _condition = null;
                    _sequenceSetting = null;
                    ActionQueue.AddCallback(new ActionQueueRecycleCallback<ConditionSequence>(_pool, this));
                }
            }
        }

        /// <summary>释放旧子序列并重置执行阶段；下次启动重新判断条件和配置子序列。</summary>
        public void Reset()
        {
            // 先释放旧子序列，下次启动重新判断条件并配置新序列。
            try
            {
                _sequence?.Deinit();
            }
            finally
            {
                _sequence = null;
                IsPaused = false;
                Status = ActionStatus.NotStart;
            }
        }
    }

    /// <summary>
    /// 向组合追加只在启动时判断条件的子序列分支。
    /// </summary>
    public static class ConditionSequenceExtension
    {
        /// <summary>
        /// 追加条件子序列；启动时条件为假则跳过，为真才配置并执行子序列。
        /// </summary>
        /// <param name="self">要追加条件子序列的父组合。</param>
        /// <param name="condition">每次启动时求值一次的条件，不等待其变为真。</param>
        /// <param name="sequenceSetting">条件为真时配置新子序列的回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence ConditionSequence(this ISequence self, Func<bool> condition, Action<ISequence> sequenceSetting)
        {
            return self.Append(global::YLFramework.Actions.ConditionSequence.Allocate(condition, sequenceSetting));
        }
    }
}
