using System;
using System.Collections.Generic;

namespace YLFramework.Actions
{
    /// <summary>
    /// 可追加子动作的组合接口，供顺序、并行和重复组合共用。
    /// </summary>
    public interface ISequence : IAction
    {
        /// <summary>
        /// 追加子动作，由组合在 Deinit 时负责释放；具体执行顺序由组合类型决定。
        /// </summary>
        /// <param name="action">由组合管理生命周期的子动作。</param>
        /// <returns>当前组合，可继续追加动作。</returns>
        ISequence Append(IAction action);
    }

    // 顺序组合：同一时间只推进一个子动作；启动和切换子动作时会连续执行即时完成的步骤，切换出的后续动作以 dt=0 起步，不继承剩余时间。
    // 子动作取消或租约失效视为整个组合取消，组合取消时释放所有仍匹配的子动作。
    internal class Sequence : ISequence
    {
        private IAction _currentAction = null;
        private int _currentActionIndex = 0;
        private List<ActionLease> _actions = new List<ActionLease>();

        /// <summary>对象池的构造入口。</summary>
        private Sequence()
        {
        }

        private static ActionPool<Sequence> _pool =
            new ActionPool<Sequence>(() => new Sequence(), null, 10);

        /// <summary>为新一轮执行取得空序列并分配动作标识。</summary>
        public static Sequence Allocate()
        {
            var sequence = _pool.Allocate();
            sequence.ActionId = ActionKit.IdGenerator++;
            sequence.Reset();
            sequence.IsDeinitialized = false;
            return sequence;
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

        /// <summary>检查子动作后从首项开始推进即时步骤；空序列直接完成。</summary>
        public void OnStart()
        {
            if (TryCancelIfChildDeinited())
            {
                return;
            }

            if (_actions.Count > 0)
            {
                _currentActionIndex = 0;
                _currentAction = _actions[_currentActionIndex].Action;
                _currentAction.Reset();
                TryExecuteUntilNextNotFinished();
            }
            else
            {
                this.Finish();
            }
        }

        /// <summary>以 dt=0 连续推进同步完成的子动作，遇到未完成项或取消即停止。</summary>
        void TryExecuteUntilNextNotFinished()
        {
            while (_currentAction != null)
            {
                if (TryCancelIfChildDeinited())
                {
                    return;
                }

                var finished = ExecuteChild(_currentAction, 0);
                if (TryCancelIfChildDeinited())
                {
                    return;
                }

                if (!finished)
                {
                    return;
                }

                _currentActionIndex++;

                if (_currentActionIndex < _actions.Count)
                {
                    _currentAction = _actions[_currentActionIndex].Action;
                    _currentAction.Reset();
                }
                else
                {
                    _currentAction = null;
                    this.Finish();
                }
            }
        }

        /// <summary>推进当前子动作，完成后以零增量启动后续步骤，不传递本次剩余时间。</summary>
        /// <param name="dt">仅传给当前子动作的时间增量，单位为秒。</param>
        public void OnExecute(float dt)
        {
            if (TryCancelIfChildDeinited())
            {
                return;
            }

            if (_currentAction != null)
            {
                var finished = ExecuteChild(_currentAction, dt);
                if (TryCancelIfChildDeinited())
                {
                    return;
                }

                if (finished)
                {
                    _currentActionIndex++;

                    if (_currentActionIndex < _actions.Count)
                    {
                        _currentAction = _actions[_currentActionIndex].Action;
                        _currentAction.Reset();

                        TryExecuteUntilNextNotFinished();
                    }
                    else
                    {
                        this.Finish();
                    }
                }
            }
            else
            {
                this.Finish();
            }
        }

        /// <summary>正常完成时不释放子动作，子动作保留到组合 Deinit，以便 Reset 后重复执行。</summary>
        public void OnFinish()
        {
        }

        /// <inheritdoc />
        public ISequence Append(IAction action)
        {
            _actions.Add(new ActionLease(action));
            return this;
        }

        // 子动作取消或租约失效时取消整个组合，不能视为正常完成。
        internal bool TryCancelIfChildDeinited()
        {
            if (IsDeinitialized)
            {
                return true;
            }

            foreach (var lease in _actions)
            {
                if (lease.Matches && !lease.Action.IsDeinitialized)
                {
                    continue;
                }

                Deinit();
                return true;
            }
            return false;
        }

        /// <summary>推进子动作；异常时补查组合取消状态，清理也失败则一并抛出。</summary>
        private bool ExecuteChild(IAction action, float dt)
        {
            try
            {
                return action.Execute(dt);
            }
            catch (Exception exception)
            {
                try
                {
                    TryCancelIfChildDeinited();
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(exception, cleanupException);
                }
                throw;
            }
        }

        /// <summary>逐项清理仍匹配的子动作并安排自身延迟回收；子动作清理异常在遍历后聚合抛出。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                // 先快照并清空容器，避免清理回调改变本次迭代；只释放仍匹配的租约。
                var actions = _actions.ToArray();
                _actions.Clear();
                _currentAction = null;
                _currentActionIndex = 0;
                List<Exception> exceptions = null;
                try
                {
                    foreach (var lease in actions)
                    {
                        try
                        {
                            if (!lease.Matches)
                            {
                                continue;
                            }

                            var action = lease.Action;
                            if (!ReferenceEquals(action, this) && !action.IsDeinitialized)
                            {
                                action.Deinit();
                            }
                        }
                        catch (Exception exception)
                        {
                            if (exceptions == null)
                            {
                                exceptions = new List<Exception>();
                            }

                            exceptions.Add(exception);
                        }
                    }
                }
                finally
                {
                    // 回收入队后延迟执行，不在此处立即复用对象。
                    ActionQueue.AddCallback(new ActionQueueRecycleCallback<Sequence>(_pool, this));
                }
                if (exceptions != null)
                {
                    throw new AggregateException(exceptions);
                }
            }
        }

        /// <summary>重置序列和全部仍有效的子动作进度；发现子动作取消或租约失效则取消组合。</summary>
        public void Reset()
        {
            _currentActionIndex = 0;
            Status = ActionStatus.NotStart;
            IsPaused = false;
            if (TryCancelIfChildDeinited())
            {
                return;
            }

            var actionId = ActionId;
            // 子动作 Reset 的清理回调可能追加或回收其他子动作，快照与租约检查保护本次迭代。
            var actions = _actions.ToArray();
            foreach (var lease in actions)
            {
                if (IsDeinitialized || ActionId != actionId)
                {
                    return;
                }

                if (!lease.Matches || lease.Action.IsDeinitialized)
                {
                    Deinit();
                    return;
                }
                lease.Action.Reset();
                if (IsDeinitialized || ActionId != actionId)
                {
                    return;
                }

                if (!lease.Matches || lease.Action.IsDeinitialized)
                {
                    Deinit();
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 向组合追加嵌套顺序序列的链式扩展。
    /// </summary>
    public static class SequenceExtension
    {
        /// <summary>
        /// 立即配置并追加子序列；子序列按追加顺序执行动作。
        /// </summary>
        /// <param name="self">要追加子序列的父组合。</param>
        /// <param name="sequenceSetting">追加前立即执行的子序列配置回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Sequence(this ISequence self, Action<ISequence> sequenceSetting)
        {
            var sequence = global::YLFramework.Actions.Sequence.Allocate();
            sequenceSetting(sequence);
            return self.Append(sequence);
        }
    }
}
