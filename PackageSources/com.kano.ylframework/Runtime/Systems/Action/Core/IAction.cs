namespace YLFramework.Actions
{
    /// <summary>
    /// 动作的执行阶段；取消和资源清理由 IsDeinitialized 表示，不是独立的状态值。
    /// </summary>
    public enum ActionStatus
    {
        /// <summary>尚未执行 OnStart，或已通过 Reset 回到起点。</summary>
        NotStart,
        /// <summary>已启动，后续更新调用 OnExecute。</summary>
        Started,
        /// <summary>已标记完成，执行器随后处理完成通知和清理。</summary>
        Finished,
    }

    /// <summary>
    /// 执行器传给动作的时间增量来源，不改变更新频率。
    /// </summary>
    public enum ActionUpdateModes
    {
        /// <summary>使用受 Time.timeScale 影响的 Time.deltaTime。</summary>
        ScaledDeltaTime,
        /// <summary>使用不受 Time.timeScale 影响的 Time.unscaledDeltaTime。</summary>
        UnscaledDeltaTime,
    }

    /// <summary>
    /// 动作的状态、执行钩子和清理契约。
    /// </summary>
    /// <typeparam name="TStatus">动作使用的状态类型。</typeparam>
    public interface IAction<TStatus>
    {
        /// <summary>本次分配的标识，用于区分同一池对象的不同使用周期。</summary>
        ulong ActionId
        {
            get; set;
        }
        /// <summary>由执行驱动维护的当前阶段。</summary>
        TStatus Status
        {
            get; set;
        }
        /// <summary>首次推进时执行的初始化钩子，可以在此标记立即完成。</summary>
        void OnStart();
        /// <summary>启动后未暂停时执行的更新钩子。</summary>
        /// <param name="dt">执行器选定的时间增量，单位为秒。</param>
        void OnExecute(float dt);
        /// <summary>正常完成后的钩子；取消清理不应调用此钩子。</summary>
        void OnFinish();
        /// <summary>是否已清理；为 true 时不可再推进当前使用周期。</summary>
        bool IsDeinitialized
        {
            get; set;
        }
        /// <summary>是否暂停后续 OnExecute；不阻止首次 OnStart。</summary>
        bool IsPaused
        {
            get; set;
        }
        /// <summary>重置执行进度和暂停状态，不重新分配标识，也不复活已清理的动作。</summary>
        void Reset();
        /// <summary>清除本次使用的资源和回调；实现应避免重复清理。</summary>
        void Deinit();
    }

    /// <summary>
    /// 使用 ActionStatus 的标准动作，可交给 Start 或组合动作执行。
    /// </summary>
    public interface IAction : IAction<ActionStatus>
    {
    }

    // 可复用池对象在一次 Execute 调用内被 Reset/复用时，版本号随之变化，供重入保护判断本次执行是否仍然有效。
    internal interface IActionExecutionVersion
    {
        /// <summary>当前执行进度的版本，用于识别 Execute 调用期间发生的重置或复用。</summary>
        ulong ExecutionVersion
        {
            get;
        }
    }

    // 记录子动作加入组合时的使用周期；池对象被重新分配后 Matches 为 false，组合据此取消而不是误操作新动作。
    internal readonly struct ActionLease
    {
        // 池对象可能被再次分配，组合动作只持有加入时的那次使用周期。
        internal readonly IAction Action;
        private readonly ulong _actionId;

        // 快照加入组合时的动作标识，后续只操作这一使用周期。
        internal ActionLease(IAction action)
        {
            Action = action;
            _actionId = action.ActionId;
        }

        internal bool Matches => Action.ActionId == _actionId;
    }

    /// <summary>
    /// 带对象池和延迟回收的动作基类，派生类通过生命周期钩子实现行为。
    /// </summary>
    /// <typeparam name="T">派生动作自身的类型，必须提供公开无参构造函数。</typeparam>
    /// <remarks>完成或取消后的对象会被复用，不应将其作为长期有效的句柄保存。</remarks>
    public abstract class AbstractAction<T> : IAction where T : AbstractAction<T>, new()
    {
        private static readonly ActionPool<T> _pool = new ActionPool<T>(() => new T(), null, 10,
            action =>
            {
                action.ActionId = ActionKit.IdGenerator++;
                action.IsDeinitialized = false;
                try
                {
                    action.Reset();
                }
                catch (System.Exception original)
                {
                    // 交付前失败由公共池结束本次取得；尝试一次领域清理并保留原始异常。
                    if (!action.IsDeinitialized)
                    {
                        action.IsDeinitialized = true;
                        try
                        {
                            action.OnDeinit();
                        }
                        catch (System.Exception cleanup)
                        {
                            original.Data["ActionKit.CleanupException"] = original.Data["ActionKit.CleanupException"] is System.Exception previous
                                ? new System.AggregateException(previous, cleanup) : cleanup;
                        }
                    }
                    throw;
                }
            });

        /// <summary>初始化动作基类；实际使用应通过 Allocate 从池中取得实例。</summary>
        protected AbstractAction()
        {
        }

        /// <summary>
        /// 从池中取得动作，分配新标识并调用 Reset。
        /// </summary>
        /// <returns>尚未启动的动作，调用方应在执行前设置本次使用的数据。</returns>
        public static T Allocate()
        {
            return _pool.Allocate();
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
        public virtual void OnStart()
        {
        }

        /// <inheritdoc />
        public virtual void OnExecute(float dt)
        {
        }

        /// <inheritdoc />
        public virtual void OnFinish()
        {
        }

        /// <summary>重置派生类的执行进度；不应清除仍需重复执行的配置。</summary>
        protected virtual void OnReset()
        {
        }

        /// <summary>在回收前释放派生类持有的资源、数据和回调。</summary>
        protected virtual void OnDeinit()
        {
        }

        /// <inheritdoc />
        public void Reset()
        {
            Status = ActionStatus.NotStart;
            IsPaused = false;
            OnReset();
        }

        /// <summary>
        /// 仅清理一次，并将对象加入延迟回收队列，不触发正常完成通知；清理失败时延迟丢弃，不重试清理。
        /// </summary>
        public void Deinit()
        {
            if (!IsDeinitialized)
            {
                IsDeinitialized = true;
                var cleaned = false;
                try
                {
                    OnDeinit();
                    cleaned = true;
                }
                finally
                {
                    // 此处只入队，实际复用留给队列 Drain，不在 Deinit 调用内直接还池。
                    ActionQueue.AddCallback(new ActionQueueRecycleCallback<T>(_pool, this as T, discard: !cleaned));
                }
            }
        }
    }

    /// <summary>
    /// 动作的推进、完成标记和宿主启动扩展。
    /// </summary>
    public static partial class IActionExtensions
    {
        /// <summary>
        /// 将动作标记为 Finished；不立即调用 OnFinish，也不负责清理或回收。
        /// </summary>
        /// <param name="self">需要正常结束的动作。</param>
        public static void Finish(this IAction self)
        {
            self.Status = ActionStatus.Finished;
        }

        /// <summary>
        /// 推进动作一次，按当前阶段调用 OnStart、OnExecute 或 OnFinish。
        /// </summary>
        /// <param name="self">仍属于当前使用周期的动作。</param>
        /// <param name="dt">本次时间增量，单位为秒；首次启动不调用 OnExecute，但可同步完成并调用 OnFinish。</param>
        /// <returns>本次已到达完成分支且使用周期仍匹配时为 true，否则为 false。</returns>
        /// <remarks>手动调用不负责回收；Finished 后再次调用会再次执行 OnFinish。回调先完成再取消时也可返回 true，调用方仍需检查 IsDeinitialized。ActionKit 关停期间调用一律返回 false，且不再触发任何钩子。</remarks>
        public static bool Execute(this IAction self, float dt)
        {
            var actionId = self.ActionId;
            var versioned = self as IActionExecutionVersion;
            var version = versioned?.ExecutionVersion ?? 0;
            // Shutdown 的清理等待执行栈退出，但后续业务回调必须立即停止。
            bool SameLease() => !ActionKit.IsShuttingDown && self.ActionId == actionId &&
                                (versioned == null || versioned.ExecutionVersion == version);
            bool Current() => !self.IsDeinitialized && SameLease();
            // 同一使用周期先完成再取消仍返回终止结果，由执行器抑制正常完成通知。
            bool FinishedLease() => SameLease() && self.Status == ActionStatus.Finished;
            if (!Current())
            {
                return false;
            }

            if (self.Status == ActionStatus.NotStart)
            {
                self.OnStart();
                if (!Current())
                {
                    return FinishedLease();
                }

                if (self.Status == ActionStatus.Finished)
                {
                    self.OnFinish();
                    return SameLease();
                }

                self.Status = ActionStatus.Started;
            }
            else if (self.Status == ActionStatus.Started)
            {
                if (self.IsPaused)
                {
                    return false;
                }

                self.OnExecute(dt);
                if (!Current())
                {
                    return FinishedLease();
                }

                if (self.Status == ActionStatus.Finished)
                {
                    self.OnFinish();
                    return SameLease();
                }
            }
            else if (self.Status == ActionStatus.Finished)
            {
                self.OnFinish();
                return SameLease();
            }

            return false;
        }
    }
}
