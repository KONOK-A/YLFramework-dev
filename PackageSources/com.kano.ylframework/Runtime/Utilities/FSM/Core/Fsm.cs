using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using UnityEngine;
using YLFramework.Pooling;

namespace YLFramework.FSM
{
    /// <summary>
    /// 代码优先的轻量有限状态机。
    /// 生命周期：Allocate() → Init(owner) → AddState(id, state) / State(id) → SetUp(startStateId)
    /// → Update / LateUpdate / FixedUpdate / OnGUI → Pause / Continue → RecycleToCache()。
    /// Init 可选（owner 允许为 null）；SetUp 之后才能驱动与切换状态。
    /// 回收后本次借用结束，不应继续使用；再次 Allocate 得到的实例需重新 Init、注册状态并 SetUp。
    /// 用户回调异常向外传播，内部重入标记在 finally 中解除；启动、切换与暂停的提交时机见各入口。
    /// 驱动回调或状态切换回调（Condition / Exit / OnStateChanged / Enter）中调用 RecycleToCache 会被拒绝，
    /// 避免对象已回池而旧调用栈继续运行。
    /// </summary>
    /// <typeparam name="TStateId">状态 ID 类型，常用枚举，也可为字符串等。</typeparam>
    /// <remarks>
    /// 与资产驱动的 YLFramework.StateMachine 相互独立，不读取控制器资产或自动检查转换边；
    /// 本类不是 Unity 组件，更新入口须由调用方转发。
    /// 两个 Continue 入口没有切换重入拒绝检查，不应从条件求值或进入/退出回调中重入恢复。
    /// </remarks>
    public class Fsm<TStateId> : IOfPool, IPoolType
    {
        /// <summary>当前运行状态（NotStart / Running / Stop）。其中 Stop 表示已暂停而非终止。</summary>
        public MachineState MachineState { get; private set; } = MachineState.NotStart;

        /// <summary>当前状态 ID；未启动或回收后为 default。</summary>
        public TStateId CurrentStateId
        {
            get; private set;
        }

        /// <summary>
        /// 上一个状态 ID。SetUp 后等于起始状态 ID；每次成功提交的切换（含 Continue(stateId)）更新为切换前状态；
        /// 切换失败或回调抛异常导致未提交时不变化；回收后为 default。只保留一层，不记录完整历史。
        /// </summary>
        public TStateId PreviousStateId
        {
            get; private set;
        }

        /// <summary>
        /// 当前状态实例；未启动或回收后为 null。
        /// 暂停（Stop）期间仍保留引用，Continue() 会对其重新 Enter。
        /// </summary>
        public IState CurrentState
        {
            get; private set;
        }

        /// <summary>
        /// 上一个状态实例，由 PreviousStateId 解析得到；未启动（NotStart）或无法解析时为 null。
        /// 仅一层 Previous，不提供历史栈。
        /// </summary>
        public IState PreviousState =>
            MachineState == MachineState.NotStart ? null : GetState(PreviousStateId);

        /// <summary>已注册状态数量。</summary>
        public int StateCount => _stateDict.Count;

        /// <summary>是否处于运行中（MachineState == Running）。</summary>
        public bool IsRunning => MachineState == MachineState.Running;

        /// <summary>是否处于暂停状态（MachineState == Stop）；暂停不是终止，可通过 Continue 恢复。</summary>
        public bool IsPaused => MachineState == MachineState.Stop;

        /// <summary>
        /// 当前状态已经历的 Update 帧数。SetUp / 切换 / Continue(stateId) 后重置为 1；
        /// 每次 Update 在调用完状态自身的 Update 后 +1，因此回调内首帧读到 1。
        /// 若某次 Update 结束时 CurrentState 已不是进入时的实例或已暂停，本帧不计入计时（见 <see cref="Update"/>）。
        /// </summary>
        public long FrameCountOfCurrentState { get; private set; } = 1;

        /// <summary>
        /// 当前状态已持续的秒数（Time.deltaTime 累加）。
        /// 仅在 Update 中、且回调结束时仍为 Running 且 CurrentState 与进入时为同一实例时累加；
        /// FixedUpdate / LateUpdate / OnGUI 不累加。
        /// 暂停期间停止累加；Continue() 恢复累加，Continue(stateId) 切换到新状态时清零。
        /// </summary>
        public float SecondsOfCurrentState
        {
            get; private set;
        }

        /// <summary>
        /// 状态切换成功提交后的回调，参数为（上一状态 ID, 当前状态 ID）。
        /// 触发顺序：旧状态 Exit → 更新 PreviousStateId / CurrentState / CurrentStateId → 本回调
        /// → 重置计时 → 新状态 Enter。
        /// 回调抛异常时切换已提交：计时仍会重置且仍会调用 Enter；若 Enter 也抛错，向外传播后者。
        /// 可空：未赋值或回收后为 null，不会触发。
        /// </summary>
        public Action<TStateId, TStateId> OnStateChanged
        {
            get; set;
        }

        // 宿主
        private IFsmOwner Owner
        {
            get; set;
        }

        // 全部已注册状态
        private readonly Dictionary<TStateId, IState> _stateDict = new();

        // 状态切换中（Condition / Exit / OnStateChanged / Enter 回调内）的重入保护标记
        private bool _isTransitioning;

        // 驱动回调（Update / LateUpdate / FixedUpdate / OnGUI 内）深度，用于拒绝回调内回收
        private int _callbackDepth;

        // 正在执行 OnRecycle 清理
        private bool _isRecycling;

        // 回收时先摘除全部状态引用，再执行 DeInit 回调，避免 DeInit 异常留下半清理对象
        private readonly List<IState> _recycleBuffer = new();

        /// <summary>
        /// 初始化状态机并记录宿主。
        /// 可重复调用（含回调内调用）；重新调用时会对已注册状态再次执行 Init，以同步新的 Owner。owner 允许为 null。
        /// </summary>
        /// <param name="owner">状态机宿主，可为 null。</param>
        /// <returns>当前状态机，便于链式调用。</returns>
        public Fsm<TStateId> Init(IFsmOwner owner)
        {
            Owner = owner;
            foreach (var pair in _stateDict)
            {
                pair.Value.Init(this, pair.Key, owner);
            }
            return this;
        }

        /// <summary>
        /// 启动状态机并进入起始状态。
        /// 仅 NotStart 状态可调用，且起始状态必须已注册；起始状态不检查 Condition。
        /// Enter 成功后才提交 Running；Enter 抛异常时回滚为干净的 NotStart 并原样抛出，可重试。
        /// 启动 Enter 回调内重入 SetUp 会被拒绝（返回 false）。
        /// </summary>
        /// <param name="startStateId">起始状态 ID。</param>
        /// <returns>启动成功返回 true；已启动、回收中、起始状态未注册或重入调用返回 false。</returns>
        public bool SetUp(TStateId startStateId)
        {
            if (MachineState != MachineState.NotStart || _isTransitioning || _isRecycling)
            {
                return false;
            }
            var startState = GetState(startStateId);
            if (startState == null)
            {
                return false;
            }
            //设置属性
            FrameCountOfCurrentState = 1;
            SecondsOfCurrentState = 0.0f;
            PreviousStateId = startStateId;
            CurrentStateId = startStateId;
            CurrentState = startState;
            _isTransitioning = true;
            try
            {
                startState.Enter();
            }
            catch
            {
                // 启动失败：回滚启动数据，保持干净的 NotStart 以便重试；异常继续向上传播
                CurrentState = null;
                CurrentStateId = default;
                PreviousStateId = default;
                throw;
            }
            finally
            {
                _isTransitioning = false;
            }
            // Enter 成功后才提交 Running
            MachineState = MachineState.Running;
            return true;
        }

        /// <summary>
        /// 注册一个状态。
        /// 重复 ID 返回 false 且不触碰已有状态与被拒绝的新状态；state 为 null 抛出 ArgumentNullException；
        /// 状态的 Init 抛出异常时回滚注册并向上抛出。运行中和切换回调内均可注册新 ID；
        /// 回收清理期间返回 false。
        /// 注意：同一个状态实例同一时间只应属于一个 FSM，重复注册到多个 FSM 会互相覆盖注入的引用。
        /// </summary>
        /// <param name="id">状态 ID。</param>
        /// <param name="state">状态实例，由调用方持有，FSM 只保存引用。</param>
        /// <returns>注册成功返回 true。</returns>
        public bool AddState(TStateId id, IState state)
        {
            // 回收清理期间拒绝注册，避免污染即将入池的实例
            if (_isRecycling)
            {
                return false;
            }
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state),
                    $"[FSM<{typeof(TStateId).Name}>] AddState 的 state 不能为 null。");
            }
            if (!_stateDict.TryAdd(id, state))
            {
                return false;
            }
            try
            {
                state.Init(this, id, Owner);
            }
            catch
            {
                // 初始化失败时不保留未就绪的状态
                _stateDict.Remove(id);
                throw;
            }
            return true;
        }

        /// <summary>
        /// 获取指定 ID 的状态；不存在时自动创建并注册一个 CustomState。
        /// 若该 ID 已注册的状态不是 CustomState（例如 AbstractState 子类），返回 null（不会覆盖或新建）。
        /// 本方法有注册副作用，仅查询请使用 TryGetState / HasState。
        /// </summary>
        /// <param name="stateId">状态 ID。</param>
        /// <returns>CustomState 实例；已注册状态不是 CustomState 时返回 null。</returns>
        public CustomState State(TStateId stateId)
        {
            var state = GetState(stateId);
            if (state != null)
            {
                return state as CustomState;
            }
            return CreateState(stateId);
        }

        /// <summary>是否存在指定 ID 的状态。</summary>
        /// <param name="stateId">状态 ID。</param>
        public bool HasState(TStateId stateId)
        {
            return _stateDict.ContainsKey(stateId);
        }

        /// <summary>尝试获取状态，不创建、不转换。</summary>
        /// <param name="stateId">状态 ID。</param>
        /// <param name="state">获取到的状态；不存在时为 null。</param>
        /// <returns>存在返回 true。</returns>
        public bool TryGetState(TStateId stateId, out IState state)
        {
            return _stateDict.TryGetValue(stateId, out state);
        }

        /// <summary>驱动当前状态的 FixedUpdate；仅 Running 时生效。回调内切换状态会立即生效。</summary>
        public void FixedUpdate()
        {
            if (MachineState != MachineState.Running)
            {
                return;
            }

            _callbackDepth++;
            try
            {
                CurrentState?.FixedUpdate();
            }
            finally
            {
                _callbackDepth--;
            }
        }

        /// <summary>
        /// 驱动当前状态 Update，并累计帧数与持续时间。
        /// 计时归属进入本次 Update 时的状态：仅当回调结束后仍为 Running 且 CurrentState 仍是进入时的实例时，
        /// 才在状态自身 Update 之后 +1 帧并累加 Time.deltaTime；否则本帧不计入任何状态，
        /// 避免把旧状态的一帧记到新状态或被暂停的状态上。
        /// 因此状态刚进入的首帧在回调内读到帧数 1、秒数 0。
        /// 仅 Running 时生效；回调内调用 RecycleToCache 会被拒绝。
        /// </summary>
        public void Update()
        {
            if (MachineState != MachineState.Running)
            {
                return;
            }

            var stateAtFrameStart = CurrentState;
            _callbackDepth++;
            try
            {
                CurrentState?.Update();
            }
            finally
            {
                _callbackDepth--;
            }
            // 只有回调结束后 CurrentState 仍是进入时的实例、且未被暂停时，才把本帧计入计时
            if (MachineState == MachineState.Running && ReferenceEquals(CurrentState, stateAtFrameStart))
            {
                FrameCountOfCurrentState++;
                SecondsOfCurrentState += Time.deltaTime;
            }
        }

        /// <summary>驱动当前状态的 LateUpdate；仅 Running 时生效，执行的是调用时的最新 CurrentState。</summary>
        public void LateUpdate()
        {
            if (MachineState != MachineState.Running)
            {
                return;
            }

            _callbackDepth++;
            try
            {
                CurrentState?.LateUpdate();
            }
            finally
            {
                _callbackDepth--;
            }
        }

        /// <summary>驱动当前状态的 OnGUI；仅 Running 时生效，执行的是调用时的最新 CurrentState。</summary>
        public void OnGUI()
        {
            if (MachineState != MachineState.Running)
            {
                return;
            }

            _callbackDepth++;
            try
            {
                CurrentState?.OnGUI();
            }
            finally
            {
                _callbackDepth--;
            }
        }

        /// <summary>
        /// 从暂停恢复：重新进入暂停前的当前状态。计时延续（不重置）。
        /// Enter 成功后才提交 Running；Enter 抛异常时保持 Stop、计时不变并原样抛出，可重试 Continue。
        /// 仅 MachineState == Stop 时生效。
        /// </summary>
        /// <returns>恢复成功返回 true。</returns>
        public bool Continue()
        {
            if (MachineState != MachineState.Stop)
            {
                return false;
            }

            _isTransitioning = true;
            try
            {
                //暂停时当前状态已退出，继续时重新进入，保证 Enter/Exit 配对
                CurrentState?.Enter();
            }
            finally
            {
                _isTransitioning = false;
            }
            // Enter 成功后才提交 Running；失败保持 Stop，可重试
            MachineState = MachineState.Running;
            return true;
        }

        /// <summary>
        /// 从暂停恢复并切换到指定状态。
        /// 需要目标已注册且 Condition 通过；暂停时当前状态已 Exit，这里不再重复 Exit；
        /// PreviousStateId 更新为暂停前状态，重置计时，触发 OnStateChanged 后进入目标状态。
        /// 异常策略与 <see cref="ChangeState"/> 一致：Condition 异常不提交；OnStateChanged 异常仍完成 Enter；
        /// Enter 异常时切换已提交并保持 Running。
        /// </summary>
        /// <param name="stateId">目标状态 ID。</param>
        /// <returns>切换成功返回 true；非 Stop、目标不存在或 Condition 不通过返回 false。</returns>
        public bool Continue(TStateId stateId)
        {
            if (MachineState != MachineState.Stop)
            {
                return false;
            }

            var nextState = GetState(stateId);
            //没有下一个状态或者下一个状态能进入的条件不满足
            if (nextState == null || !EvaluateCondition(nextState))
            {
                return false;
            }

            MachineState = MachineState.Running;
            _isTransitioning = true;
            try
            {
                //当前状态已在暂停时退出，这里不再重复调用 Exit
                CommitTransition(nextState, stateId, false);
            }
            finally
            {
                _isTransitioning = false;
            }

            return true;
        }

        /// <summary>
        /// 切换到指定状态。切换顺序：
        /// 校验（Running、非切换中、目标与条件）→ 旧状态 Exit
        /// → 更新 PreviousStateId / CurrentState / CurrentStateId → OnStateChanged → 重置计时 → 新状态 Enter。
        /// 异常语义：
        /// Condition 抛异常 → 不提交任何变化；
        /// Exit 抛异常 → 尚未提交，Current / Previous / 计时保持旧值；
        /// OnStateChanged 抛异常 → 切换已提交，仍会重置计时并调用 Enter，异常传播规则见 OnStateChanged；
        /// Enter 抛异常 → 切换与计时已提交，MachineState 保持 Running，可用 reenterCurrentState 重进目标。
        /// Condition / Exit / OnStateChanged / Enter 回调内调用 ChangeState 或 Pause 会返回 false。
        /// </summary>
        /// <param name="newStateId">目标状态 ID。</param>
        /// <param name="reenterCurrentState">目标与当前相同时是否重新执行 Exit/Enter；默认 false 表示忽略。</param>
        /// <returns>切换成功返回 true；未启动、暂停、切换中、目标不存在或 Condition 不通过返回 false。</returns>
        public bool ChangeState(TStateId newStateId, bool reenterCurrentState = false)
        {
            //只有运行状态才可以切换状态
            if (MachineState != MachineState.Running)
            {
                return false;
            }

            //状态切换过程中禁止重入，避免 Exit/Enter 生命周期不配对
            if (_isTransitioning)
            {
                return false;
            }

            if (newStateId.Equals(CurrentStateId) && !reenterCurrentState)
            {
                return false;
            }

            var nextState = GetState(newStateId);
            //没有下一个状态或者下一个状态能进入的条件不满足
            if (nextState == null || !EvaluateCondition(nextState))
            {
                return false;
            }

            _isTransitioning = true;
            try
            {
                CommitTransition(nextState, newStateId, true);
            }
            finally
            {
                _isTransitioning = false;
            }

            return true;
        }

        /// <summary>
        /// 暂停状态机：对当前状态调用 Exit 并进入 Stop。
        /// Exit 成功后才提交 Stop；Exit 抛异常时保持 Running、Current 与计时不变，可重试 Pause。
        /// 暂停期间驱动方法与 ChangeState 均不生效，可用 Continue / Continue(stateId) 恢复。
        /// 状态切换回调（Condition / Exit / OnStateChanged / Enter）期间调用会被拒绝（返回 false），避免 Exit/Enter 不配对。
        /// </summary>
        /// <returns>暂停成功返回 true。</returns>
        public bool Pause()
        {
            //只有运行状态才可以暂停；切换/条件回调过程中暂停会造成 Exit/Enter 不配对，直接拒绝
            if (MachineState != MachineState.Running || _isTransitioning)
            {
                return false;
            }
            _isTransitioning = true;
            try
            {
                // 暂停只需退出当前状态；CurrentState 引用保留，由 Continue() 重新 Enter
                CurrentState?.Exit();
            }
            finally
            {
                _isTransitioning = false;
            }
            // Exit 成功后才提交 Stop；失败保持 Running 可重试
            MachineState = MachineState.Stop;
            return true;
        }

        /// <summary>保留对象池的无参创建入口；直接构造被标记为编译错误，业务应调用 Allocate。</summary>
        [Obsolete("请使用FSM<T>.Allocate()来申请一个状态机", true)]
        public Fsm()
        {
        }

        /// <summary>从对象池申请一个状态机实例。</summary>
        public static Fsm<TStateId> Allocate()
        {
            var fsm = SafeObjectPool<Fsm<TStateId>>.Instance.Allocate();
            return fsm;
        }

        /// <summary>
        /// 回收清理（由对象池在回收时调用）：
        /// 先 Pause 退出当前状态；该步抛异常时直接退出，不进入后续清空。
        /// 成功后把 FSM 自身状态清干净（宿主、状态表、当前/上一状态、回调、计时、内部标记），
        /// 再对已摘除的状态逐个执行 DeInit。这样即使某个 DeInit 抛异常，也不会留下半清理对象；
        /// 所有状态的 DeInit 都会被执行，只向上抛出第一个异常；异常时对象池会拒绝入池（不会以脏状态被复用）。
        /// 状态对象本身由调用方持有，这里不会 Dispose，只解除 FSM 的引用并调用 DeInit。
        /// </summary>
        public void OnRecycle()
        {
            _isRecycling = true;
            try
            {
                // 先退出当前状态（若在运行）
                Pause();

                // 摘除全部状态引用，再执行外部 DeInit 回调
                _recycleBuffer.Clear();
                foreach (var pair in _stateDict)
                {
                    _recycleBuffer.Add(pair.Value);
                }
                Owner = null;
                CurrentState = null;
                _stateDict.Clear();
                CurrentStateId = default;
                PreviousStateId = default;
                OnStateChanged = null;
                FrameCountOfCurrentState = 1;
                SecondsOfCurrentState = 0.0f;
                MachineState = MachineState.NotStart;
                _isTransitioning = false;
                _callbackDepth = 0;

                // 继续清理其余状态；只向上传播第一个异常
                Exception firstError = null;
                foreach (var state in _recycleBuffer)
                {
                    if (state == null)
                    {
                        continue;
                    }

                    try
                    {
                        state.DeInit();
                    }
                    catch (Exception ex)
                    {
                        firstError ??= ex;
                    }
                }
                _recycleBuffer.Clear();

                // 防御：DeInit 回调中若重新 Init，不允许 Owner 残留在池中实例上
                Owner = null;

                if (firstError != null)
                {
                    ExceptionDispatchInfo.Capture(firstError).Throw();
                }
            }
            finally
            {
                _isRecycling = false;
            }
        }

        /// <inheritdoc />
        public bool IsRecycled
        {
            get; set;
        }

        /// <summary>
        /// 回收状态机到对象池。回收后不应再使用该实例。
        /// 驱动回调（Update / LateUpdate / FixedUpdate / OnGUI）或状态切换回调（Condition / Exit / OnStateChanged / Enter）中
        /// 调用会抛 <see cref="InvalidOperationException"/>，避免对象已入池而旧调用栈继续访问。
        /// </summary>
        /// <exception cref="InvalidOperationException">在生命周期回调中调用时抛出。</exception>
        public void RecycleToCache()
        {
            if (_isTransitioning || _callbackDepth > 0)
            {
                throw new InvalidOperationException(
                    $"[FSM<{typeof(TStateId).Name}>] 生命周期回调中不能回收状态机" +
                    $"（MachineState={MachineState}, CurrentStateID={CurrentStateId}）。请退出回调后再调用 RecycleToCache。");
            }
            SafeObjectPool<Fsm<TStateId>>.Instance.Recycle(this);
        }

        /// <summary>
        /// 在同一重入保护窗口内求值目标状态的 Condition：
        /// 求值期间禁止重入 ChangeState / Pause / RecycleToCache（分别返回 false 或抛异常），避免破坏外层切换。
        /// Condition 抛异常时原样向上传播，此时不提交任何状态变化。
        /// </summary>
        private bool EvaluateCondition(IState state)
        {
            _isTransitioning = true;
            try
            {
                return state.Condition();
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        /// <summary>在调用方的切换保护内提交目标状态，并在通知失败时仍尝试重置计时和进入目标。</summary>
        private void CommitTransition(IState nextState, TStateId nextStateId, bool exitCurrentState)
        {
            if (exitCurrentState)
            {
                CurrentState?.Exit();
            }
            PreviousStateId = CurrentStateId;
            CurrentState = nextState;
            CurrentStateId = nextStateId;
            try
            {
                OnStateChanged?.Invoke(PreviousStateId, CurrentStateId);
            }
            finally
            {
                // 切换已提交：即使通知回调抛异常，也要完成计时重置与 Enter，避免"Current 已更新但从未 Enter"的半状态
                FrameCountOfCurrentState = 1;
                SecondsOfCurrentState = 0.0f;
                CurrentState?.Enter();
            }
        }

        /// <summary>按 ID 查询已注册状态，不存在时返回 null，不创建状态。</summary>
        private IState GetState(TStateId stateType)
        {
            return _stateDict.GetValueOrDefault(stateType);
        }

        /// <summary>创建并初始化委托状态，再尝试加入注册表，供 State 的按需创建入口使用。</summary>
        private CustomState CreateState(TStateId stateType)
        {
            var newState = new CustomState();
            newState.Init(this, stateType, Owner);
            _stateDict.TryAdd(stateType, newState);
            return newState;
        }
    }
}
