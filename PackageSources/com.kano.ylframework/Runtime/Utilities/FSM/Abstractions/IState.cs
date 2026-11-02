namespace YLFramework.FSM
{
    /// <summary>
    /// 状态机中的单个状态契约。
    /// 状态对象由调用方提供并持有，FSM 仅保存引用：注册时调用 Init，回收时调用 DeInit，
    /// 不会主动 Dispose 状态对象。
    /// 同一个状态实例同一时间只应注册到一个 FSM；注册到多个 FSM 会互相覆盖 Init 注入的引用。
    /// </summary>
    public interface IState
    {
        /// <summary>
        /// 询问此状态当前是否允许被切入。
        /// FSM 仅在它作为 ChangeState / Continue(stateId) 的目标时调用；
        /// 返回 false 时切换失败，且不会触发任何 Exit / Enter / OnStateChanged 回调。
        /// FSM 不会用它判断“当前状态是否应继续”，当前状态的退出由业务代码主动切换完成。
        /// </summary>
        /// <returns>允许切入返回 true。</returns>
        bool Condition();

        /// <summary>
        /// 状态初始化。FSM 在 AddState / State(id) 创建状态时调用；
        /// 若之后重新调用 FSM.Init(owner) 更换宿主，会对已注册状态再次调用。
        /// </summary>
        /// <param name="args">依次为：FSM 实例、状态 ID、IFsmOwner（可能为 null）。</param>
        void Init(params object[] args);

        /// <summary>
        /// 释放状态持有的引用。FSM 每次回收（RecycleToCache）时对每个已注册状态各调用一次，
        /// 之后状态对象可被重新注册复用。
        /// </summary>
        void DeInit();

        /// <summary>状态被切入时由 FSM 调用。</summary>
        void Enter();

        /// <summary>状态机 Update 时由 FSM 调用。</summary>
        void Update();

        /// <summary>状态机 LateUpdate 时由 FSM 调用。</summary>
        void LateUpdate();

        /// <summary>状态机 FixedUpdate 时由 FSM 调用。</summary>
        void FixedUpdate();

        /// <summary>状态机 OnGUI 时由 FSM 调用。</summary>
        void OnGUI();

        /// <summary>状态被切出时由 FSM 调用。</summary>
        void Exit();
    }
}
