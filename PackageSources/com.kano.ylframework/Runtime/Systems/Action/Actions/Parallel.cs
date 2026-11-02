using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Actions
{
    /// <summary>
    /// 在同次执行中逐一推进未完成子动作的组合接口，不使用多线程。
    /// </summary>
    /// <remarks>非空组合在全部子动作完成后结束；空组合不会自动完成。</remarks>
    public interface IParallel : ISequence
    {
    }

    // 并行组合在同一次推进中处理多个子动作，不创建线程，也不保证始终按加入顺序执行。
    // 任一子动作取消或租约失效都会取消整个组合，组合自身取消时会释放所有仍匹配的子动作。
    internal class Parallel : IParallel
    {
        /// <summary>仅供对象池创建空并行组合，子动作由 Append 加入。</summary>
        private Parallel()
        {
        }

        private static ActionPool<Parallel> _pool =
            new ActionPool<Parallel>(() => new Parallel(), null, 5);

        private List<ActionLease> _actions = new List<ActionLease>();

        private int _finishedCount = 0;

        /// <summary>取得空并行组合并分配新标识，不在分配时执行子动作。</summary>
        public static Parallel Allocate()
        {
            var parallel = _pool.Allocate();
            parallel.ActionId = ActionKit.IdGenerator++;
            parallel.IsDeinitialized = false;
            parallel.Reset();
            return parallel;
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
        /// <summary>只检查子动作是否已取消或租约失效，不推进子动作。</summary>
        public void OnStart()
        {
            TryCancelIfChildDeinited();
        }

        /// <summary>遍历当前排列中的未完成子动作，非空组合的最后一项完成后结束。</summary>
        /// <param name="dt">传给各个未完成子动作的同一时间增量，单位为秒。</param>
        public void OnExecute(float dt)
        {
            if (TryCancelIfChildDeinited())
            {
                return;
            }

            for (var i = _finishedCount; i < _actions.Count; i++)
            {
                bool finished;
                try
                {
                    finished = _actions[i].Action.Execute(dt);
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
                if (TryCancelIfChildDeinited())
                {
                    return;
                }

                if (!finished)
                {
                    continue;
                }

                _finishedCount++;

                if (_finishedCount >= _actions.Count)
                {
                    this.Finish();
                }
                else
                {
                    // 将已完成动作移入前段，后续只推进未完成动作。
                    (_actions[i], _actions[_finishedCount - 1]) = (_actions[_finishedCount - 1], _actions[i]);
                }
            }
        }

        /// <summary>正常完成时保留子动作，由组合 Deinit 统一释放。</summary>
        public void OnFinish()
        {
        }

        /// <inheritdoc />
        public ISequence Append(IAction action)
        {
            _actions.Add(new ActionLease(action));
            return this;
        }

        /// <summary>子动作取消或租约失效时取消整个组合，不能视为正常完成。</summary>
        private bool TryCancelIfChildDeinited()
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

        /// <summary>逐项清理仍匹配的子动作并安排自身延迟回收；清理异常汇总后抛出。</summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                // 先快照并清空容器，避免清理回调改变本次迭代；只释放仍匹配的租约。
                var actions = _actions.ToArray();
                _actions.Clear();
                _finishedCount = 0;
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
                    ActionQueue.AddCallback(new ActionQueueRecycleCallback<Parallel>(_pool, this));
                }
                if (exceptions != null)
                {
                    throw new AggregateException(exceptions);
                }
            }
        }

        /// <summary>重置完成计数和有效子动作进度，不恢复被完成项交换改变的排列。</summary>
        public void Reset()
        {
            Status = ActionStatus.NotStart;
            _finishedCount = 0;
            IsPaused = false;
            if (TryCancelIfChildDeinited())
            {
                return;
            }

            var actionId = ActionId;
            // Reset 可能释放订阅并修改或回收其他子动作，快照与租约检查保护本次迭代。
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
    /// 向组合追加并行子组合的链式扩展。
    /// </summary>
    public static class ParallelExtension
    {
        /// <summary>
        /// 立即配置并追加并行子组合；其子动作在同次执行中分别推进。
        /// </summary>
        /// <param name="self">要追加并行子组合的父组合。</param>
        /// <param name="parallelSetting">追加前立即执行的并行子组合配置回调。</param>
        /// <returns>父组合，可继续追加动作。</returns>
        public static ISequence Parallel(this ISequence self, Action<ISequence> parallelSetting)
        {
            var parallel = global::YLFramework.Actions.Parallel.Allocate();
            parallelSetting(parallel);
            return self.Append(parallel);
        }
    }
}
