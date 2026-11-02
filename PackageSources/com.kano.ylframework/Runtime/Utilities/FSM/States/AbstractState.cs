using System;

namespace YLFramework.FSM
{
    /// <summary>
    /// 正式业务状态的抽象基类。
    /// FSM 在注册状态时通过 Init 注入 FSM、状态 ID 与宿主（Owner 可能为 null），
    /// 回收时通过 DeInit 清空这些引用；生命周期方法全部由 FSM 驱动，派生类覆写对应的 OnXxx 即可。
    /// </summary>
    /// <typeparam name="TStateId">状态 ID 类型。</typeparam>
    public abstract class AbstractState<TStateId> : IState
    {
        /// <summary>所属状态机；注册后可用，回收后为 null。</summary>
        protected Fsm<TStateId> StateMachine;

        /// <summary>本状态注册时的状态 ID。</summary>
        protected TStateId StateId;

        /// <summary>状态机宿主；可能为 null（未 Init 或传入 null）。</summary>
        protected IFsmOwner Owner;

        /// <inheritdoc />
        public virtual void Init(params object[] args)
        {
            if (args == null)
            {
                throw new Exception("FrameWorkError: 状态机状态初始化参数为空!");
            }

            if (args.Length > 0)
            {
                StateMachine = args[0] as Fsm<TStateId>;
            }

            if (args.Length > 1)
            {
                StateId = args[1] is TStateId ? (TStateId)args[1] : default;
            }

            if (args.Length > 2)
            {
                Owner = args[2] as IFsmOwner;
            }
        }

        /// <inheritdoc />
        public virtual void DeInit()
        {
            StateMachine = null;
            StateId = default;
            Owner = null;
        }

        /// <inheritdoc />
        bool IState.Condition()
        {
            return OnCondition();
        }

        /// <inheritdoc />
        void IState.Enter()
        {
            OnEnter();
        }

        /// <inheritdoc />
        void IState.Update()
        {
            OnUpdate();
        }

        /// <inheritdoc />
        void IState.LateUpdate()
        {
            OnLateUpdate();
        }

        /// <inheritdoc />
        void IState.FixedUpdate()
        {
            OnFixedUpdate();
        }

        /// <summary>OnGUI 生命周期回调；由 FSM 驱动，派生类覆写即可，不建议外部直接调用。</summary>
        // 此方法同时是 IState.OnGUI 的隐式实现，必须为 public；其余生命周期通过显式实现收敛为 protected OnXxx。
        public virtual void OnGUI()
        {
        }

        /// <inheritdoc />
        void IState.Exit()
        {
            OnExit();
        }

        /// <summary>询问本状态当前是否允许被切入，默认 true；FSM 仅在切换目标时调用，覆写可控制入口条件。</summary>
        protected virtual bool OnCondition()
        {
            return true;
        }

        /// <summary>状态被切入时回调。</summary>
        protected virtual void OnEnter()
        {
        }

        /// <summary>状态机 Update 时回调。</summary>
        protected virtual void OnUpdate()
        {
        }

        /// <summary>状态机 LateUpdate 时回调。</summary>
        protected virtual void OnLateUpdate()
        {
        }

        /// <summary>状态机 FixedUpdate 时回调。</summary>
        protected virtual void OnFixedUpdate()
        {
        }

        /// <summary>状态被切出时回调。</summary>
        protected virtual void OnExit()
        {
        }

        /// <summary>
        /// 切换到指定状态，等价于 StateMachine.ChangeState(stateId)。
        /// </summary>
        /// <param name="stateId">目标状态 ID。</param>
        /// <returns>切换是否成功；FSM 未注册、未启动、暂停中、切换中或条件不满足时返回 false。</returns>
        protected bool ChangeState(TStateId stateId)
        {
            return StateMachine != null && StateMachine.ChangeState(stateId);
        }

        /// <summary>
        /// 切换到指定状态；reenterCurrentState 为 true 时允许对当前状态重新执行 Exit/Enter。
        /// </summary>
        /// <param name="stateId">目标状态 ID。</param>
        /// <param name="reenterCurrentState">目标与当前相同时是否重新执行 Exit/Enter。</param>
        /// <returns>切换是否成功。</returns>
        protected bool ChangeState(TStateId stateId, bool reenterCurrentState)
        {
            return StateMachine != null && StateMachine.ChangeState(stateId, reenterCurrentState);
        }

        /// <summary>当前状态已持续的秒数（口径与 FSM.SecondsOfCurrentState 一致）；不在状态机中时为 0。</summary>
        protected float SecondsInState => StateMachine != null ? StateMachine.SecondsOfCurrentState : 0f;

        /// <summary>当前状态已经历的 Update 帧数（口径与 FSM.FrameCountOfCurrentState 一致）；不在状态机中时为 0。</summary>
        protected long FramesInState => StateMachine != null ? StateMachine.FrameCountOfCurrentState : 0;

        /// <summary>
        /// 以强类型读取宿主；宿主为 null 或不是 TOwner 时返回 null。
        /// </summary>
        /// <typeparam name="TOwner">宿主的具体类型。</typeparam>
        protected TOwner GetOwner<TOwner>() where TOwner : class, IFsmOwner
        {
            return Owner as TOwner;
        }
    }
}
