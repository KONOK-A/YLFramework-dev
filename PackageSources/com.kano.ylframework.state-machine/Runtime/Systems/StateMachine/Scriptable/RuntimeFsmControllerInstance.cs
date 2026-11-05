using System.Collections.Generic;
using System.Text;
#if UNITY_EDITOR
#endif
using UnityEngine;

namespace YLFramework.StateMachine
{



    /// <summary>
    /// 一层配置的运行时执行实例，拥有配置克隆、状态脚本、参数、过渡和 Trigger 队列。
    /// </summary>
    public class RuntimeFsmControllerInstance
    {

        /// <summary>构造时记录的源配置名称，用于诊断。</summary>
        public string Name;

        /// <summary>所属宿主中的层名称。</summary>
        public string LayerName;

        /// <summary>本实例拥有的配置克隆，释放后清空，不应替换为共享源资产。</summary>
        public RuntimeFsmController RuntimeFsmController;
        /// <summary>用于创建本实例的共享源配置，不由实例销毁。</summary>
        public RuntimeFsmController SourceController
        {
            get;
        }
        /// <summary>是否已开始永久释放；置位后不再启动或接受参数写入。</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        internal bool IsResettingTriggers
        {
            get; set;
        }
        private bool _isClosing;

        // 所有的参数
        internal Dictionary<int, FsmParameterData> Parameters = new Dictionary<int, FsmParameterData>();

        [Tooltip("参数的默认值")]
        internal Dictionary<int, float> ParameterDefaults = new Dictionary<int, float>();

        // 所有的状态
        internal Dictionary<string, FsmStateNode> States = new Dictionary<string, FsmStateNode>();

        internal List<FsmTransition> Transitions = new List<FsmTransition>();

        // 默认状态
        internal FsmStateNode DefaultState { get; private set; } = null;

        private Dictionary<string, FsmStateNode> _currentStates = new Dictionary<string, FsmStateNode>();

        private List<FsmStateNode> _currentStatesArray = new List<FsmStateNode>();

        /// <summary>最近一次切换流程记录的过渡；默认进入或关闭后可为空，不表示持续中的混合过程。</summary>
        public FsmTransition CurrentTransition { get; private set; } = null;

        /// <summary>驱动本层并接收状态变化通知的宿主组件。</summary>
        public FsmController FsmController
        {
            get; private set;
        }

        /// <summary>
        /// 状态切换计数(为了避免状态切换进入死循环)
        /// </summary>
        private readonly FsmTransitionBudget _transitionBudget = new FsmTransitionBudget();

        /// <summary>
        /// 当前的帧数
        /// </summary>
        private int _loopErrorFrame = -1;

        /// <summary>
        /// 记录Trigger触发的次数
        /// </summary>
        private readonly FsmTriggerQueue _triggerQueue = new FsmTriggerQueue();

        private List<int> _triggerKeys = new List<int>();

        /// <summary>是否已启动且尚未关闭；不等同于源配置或宿主的初始化状态。</summary>
        public bool HasStarted { get; private set; } = false;


        /// <summary>验证配置后克隆数据、反射构造全部状态脚本并绑定过渡，暂不进入默认状态。</summary>
        /// <param name="runtimeFsmController">共享源配置资产。</param>
        /// <param name="controller">拥有此层的宿主组件。</param>
        /// <param name="layerName">宿主中的层名称。</param>
        public RuntimeFsmControllerInstance(RuntimeFsmController runtimeFsmController, FsmController controller, string layerName)
        {
            FsmController = controller;
            SourceController = runtimeFsmController;
            this.LayerName = layerName;
            if (!runtimeFsmController)
            {
                throw new System.ArgumentNullException(nameof(runtimeFsmController));
            }

            if (!controller)
            {
                throw new System.ArgumentNullException(nameof(controller));
            }

            if (!runtimeFsmController.ValidateConfiguration(out string error))
            {
                throw new System.ArgumentException($"Asset '{runtimeFsmController.name}', layer '{layerName}': {error}", nameof(runtimeFsmController));
            }

            Name = runtimeFsmController.name;
            // 默认不执行任何状态
            _currentStates.Clear();

            RuntimeFsmController = GameObject.Instantiate(runtimeFsmController);
#if UNITY_EDITOR
            FsmEditorHooks.CloneCreated?.Invoke(runtimeFsmController, RuntimeFsmController);
#endif
            RuntimeFsmController.name = runtimeFsmController.name;
            try
            {
                // 参数通知只绑定到本层克隆；默认值单独保留，关闭时绕过通知恢复。
                Parameters.Clear();
                for (int i = 0; i < RuntimeFsmController.parameters.Count; i++)
                {
                    FsmParameterData data = RuntimeFsmController.parameters[i];

                    int nameHash = FsmController.StringToHash(data.name);

                    if (Parameters.ContainsKey(nameHash))
                    {
                        continue;
                    }

                    data.nameHash = nameHash;

                    data.ValueChanged = null;
                    Parameters.Add(nameHash, data);
                    ParameterDefaults.Add(nameHash, data.Value);
                }

                // 所有脚本在此构造，OnCreate 则推迟到各脚本首次被进入流程访问时。
                States.Clear();
                for (int i = 0; i < RuntimeFsmController.states.Count; i++)
                {
                    FsmStateNodeData stateData = RuntimeFsmController.states[i];
                    FsmStateNode state = new FsmStateNode(stateData, this);

                    if (States.ContainsKey(stateData.name))
                    {
                        continue;
                    }

                    if (stateData.isDefaultState && stateData.BaseLayer())
                    {
                        DefaultState = state;
                    }

                    States.Add(stateData.name, state);
                }
                // 建立顺序同时决定主动扫描顺序及参数通知的订阅顺序。
                Transitions.Clear();
                foreach (var item in RuntimeFsmController.transitions)
                {
                    FsmTransition transition = new FsmTransition(this, item);
                    Transitions.Add(transition);
                }

                HasStarted = false;
            }
            catch
            {
                Dispose();
                throw;
            }
        }


        /// <summary>启动未释放且未运行的实例，从基础层默认节点进入并检查连锁过渡。</summary>
        public void StartUp()
        {
            if (IsDisposed || HasStarted || _isClosing)
            {
                return;
            }

            HasStarted = true;
            SwitchState(DefaultState, null);
        }

        /// <summary>退出基础层及其活动子状态，清空 Trigger 和过渡预算并恢复参数默认值，保留克隆供重启。</summary>
        /// <remarks>退出回调异常仍会向外传播；不回滚回调已执行的业务动作，也不重置脚本创建标记。</remarks>
        public void Close()
        {
            if (_isClosing)
            {
                return;
            }

            _isClosing = true;
            HasStarted = false;
            try
            {
                ExitState(string.Empty, null);
            }
            finally
            {
                _currentStates.Clear();
                _triggerQueue.Clear();
                _triggerKeys.Clear();
                _transitionBudget.Clear();
                _loopErrorFrame = -1;
                CurrentTransition = null;
                ResetParamter();
                _isClosing = false;
            }
        }

        /// <summary>归还本实例拥有的配置克隆；Close仅重置，不销毁以便重新启用。</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            try
            {
                Close();
            }
            finally
            {
                foreach (var parameter in Parameters.Values)
                {
                    parameter.ValueChanged = null;
                }

                Parameters.Clear();
                ParameterDefaults.Clear();
                States.Clear();
                Transitions.Clear();
                if (RuntimeFsmController)
                {
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(RuntimeFsmController);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(RuntimeFsmController);
                    }
                }
                RuntimeFsmController = null;
            }
        }
        /// <summary>在同一父层先退出旧节点再进入目标，记录过渡后立即检查当前状态的后续过渡。</summary>
        /// <param name="state">目标节点；为空时退出基础层。</param>
        /// <param name="transition">触发本次切换的过渡，默认进入时为空。</param>
        internal void SwitchState(FsmStateNode state, FsmTransition transition)
        {
            if (!HasStarted || IsDisposed || _isClosing)
            {
                return;
            }

            if (state != null && !_transitionBudget.TryEnter(Time.frameCount, state.Data.name))
            {
                if (_loopErrorFrame != Time.frameCount)
                {
                    _loopErrorFrame = Time.frameCount;
                    Debug.LogError($"Asset '{Name}', layer '{LayerName}', state '{state.Data.name}': transition cycle budget exceeded in frame {Time.frameCount}. Check transition conditions.", FsmController);
                }
                return;
            }

            string parent = state != null ? state.Data.Parent : string.Empty;

            ExitState(parent, state);
            EnterState(state);

            if (!HasStarted || IsDisposed)
            {
                return;
            }

            CurrentTransition = transition;

            CheckConditionAndSwitch();
        }


        /// <summary>按配置过渡顺序主动扫描，首个条件满足的过渡发出切换请求后停止本轮扫描。</summary>
        private void CheckConditionAndSwitch()
        {
            if (!HasStarted || IsDisposed || _isClosing)
            {
                return;
            }
            foreach (var item in Transitions)
            {
                if (item.CheckConditionAndSwitch())
                {
                    break;
                }
            }
        }

        /// <summary>先摘除父层当前节点再执行退出，容器先于活动子节点退出；保留首个异常直到递归退出完成。</summary>
        private void ExitState(string parent, FsmStateNode nextState)
        {
            if (!_currentStates.ContainsKey(parent))
            {
                return;
            }

            if (_currentStates[parent] == null)
            {
                return;
            }

            FsmStateNode currentState = _currentStates[parent];

            if (nextState != null)
            {
                currentState.NextState = nextState.Data.name;
            }

            if (nextState != null && nextState.Data.CompareParent(currentState.Data))
            {
                nextState.LastState = currentState.Data.name;
            }

            if (nextState != null)
            {
                nextState.NextState = string.Empty;
            }
            // 在用户退出回调前摘除记录，避免回调重入时仍将旧节点视为该层当前状态。
            _currentStates.Remove(parent);
            System.Exception firstError = null;
            try
            {
                currentState.OnExit();
            }
            catch (System.Exception error)
            {
                firstError = error;
            }

            if (currentState.Data.isSubStateMachine)
            {
                try
                {
                    ExitState(currentState.Data.name, nextState);
                }
                catch (System.Exception error)
                {
                    firstError ??= error;
                }
            }
            if (firstError != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        /// <summary>先登记当前节点，再进入脚本并通知宿主；回调后仍有效时才递归进入子层默认节点。</summary>
        private void EnterState(FsmStateNode state)
        {
            if (state == null || !HasStarted || IsDisposed || _isClosing)
            {
                return;
            }

            string parent = state.Data.Parent;

            if (_currentStates.ContainsKey(parent))
            {
                _currentStates[parent] = state;
            }
            else
            {
                _currentStates.Add(parent, state);
            }

            state?.OnEnter();
            if (!HasStarted || IsDisposed || !state.IsRunning || GetCurrentState(parent) != state)
            {
                return;
            }
            FsmController.StateChanged?.Invoke(RuntimeFsmController.name, state.Data.name);
            if (!HasStarted || IsDisposed || GetCurrentState(parent) != state)
            {
                return;
            }

            if (state.Data.isSubStateMachine)
            {
                FsmStateNodeData data = RuntimeFsmController.GetDefaultStateNodeData(state.Data.name);
                if (data != null && States.ContainsKey(data.name))
                {
                    EnterState(States[data.name]);
                }
            }

        }

        /// <summary>按哈希写入本层布尔参数，类型不匹配或参数不存在时不修改。</summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <param name="v">布尔值，以零或一保存。</param>
        public void SetBool(int nameHash, bool v)
        {
            SetParamter(nameHash, v ? 1 : 0, ParameterType.Bool);
        }

        /// <summary>按哈希写入本层浮点参数，变化通知可同步触发过渡。</summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <param name="v">新浮点值。</param>
        public void SetFloat(int nameHash, float v)
        {
            SetParamter(nameHash, v, ParameterType.Float);
        }


        /// <summary>按哈希写入本层整数参数，底层以浮点数存储。</summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <param name="v">新整数值。</param>
        public void SetInt(int nameHash, int v)
        {
            SetParamter(nameHash, v, ParameterType.Int);
        }


        /// <summary>置位本层 Trigger；已置位时累加待触发次数，后续由逻辑更新逐次取出。</summary>
        /// <param name="name">Trigger 参数名称哈希。</param>
        public void SetTrigger(int name)
        {
            FsmParameterData parameter;
            if (Parameters.TryGetValue(name, out parameter))
            {
                if (parameter.parameterType != ParameterType.Trigger)
                {
                    return;
                }

                if (parameter.value == 1)
                {
                    _triggerQueue.Enqueue(name);
                }
                else
                {
                    SetParamter(name, 1, ParameterType.Trigger);
                }

            }
        }

        /// <summary>
        /// 清除 Trigger 当前值，但保留排队次数供后续更新处理。
        /// </summary>
        /// <param name="name">Trigger 参数名称哈希。</param>
        internal void ClearTrigger(int name)
        {
            SetParamter(name, 0, ParameterType.Trigger);
        }

        //public void ResetTrigger(string name)
        //{
        //    ResetTrigger(FSMController.StringToHash(name));
        //}

        /// <summary>丢弃指定 Trigger 的排队次数并清除当前值。</summary>
        /// <param name="name">Trigger 参数名称哈希。</param>
        public void ResetTrigger(int name)
        {
            _triggerQueue.Reset(name);
            ClearTrigger(name);
        }

        /// <summary>仅向存活实例中类型匹配的参数提交新值，由参数同步发出变化通知。</summary>
        private void SetParamter(int name, float v, ParameterType type)
        {
            if (IsDisposed || _isClosing)
            {
                return;
            }

            FsmParameterData parameter;
            if (Parameters.TryGetValue(name, out parameter))
            {
                if (parameter.parameterType == type)
                {
                    parameter.Value = v;
                }
            }
        }


        //internal float GetParamter(string name)
        //{
        //    return GetParamter(FSMController.StringToHash(name));
        //}

        /// <summary>读取本层参数底层值，缺失时返回零，不检查参数语义类型。</summary>
        /// <param name="name">参数名称哈希。</param>
        /// <returns>参数当前值或零。</returns>
        internal float GetParamter(int name)
        {
            FsmParameterData parameter;
            if (Parameters.TryGetValue(name, out parameter))
            {
                return parameter.value;
            }

            return 0;
        }

        /// <summary>按名称读取 Trigger 的排队次数，不含当前置位值。</summary>
        /// <param name="name">Trigger 参数名称。</param>
        /// <returns>尚未取出的触发次数。</returns>
        internal int GetTriggerCount(string name)
        {
            return GetTriggerCount(FsmController.StringToHash(name));
        }

        /// <summary>按哈希读取 Trigger 的排队次数，不含当前置位值。</summary>
        /// <param name="name">Trigger 参数名称哈希。</param>
        /// <returns>尚未取出的触发次数。</returns>
        internal int GetTriggerCount(int name)
        {

            return _triggerQueue.GetCount(name);
        }


        /// <summary>对当前节点快照转发逻辑更新，随后尝试为每个已清零的 Trigger 取出一次排队触发。</summary>
        /// <remarks>过渡主要由参数通知及进入后的检查触发，此处不逐帧无条件扫描全部过渡。</remarks>
        public void Update()
        {
            if (!HasStarted || IsDisposed)
            {
                return;
            }

            _currentStatesArray.Clear();

            foreach (var item in _currentStates.Keys)
            {
                if (_currentStates[item] == null)
                {
                    continue;
                }

                _currentStatesArray.Add(_currentStates[item]);
            }

            foreach (var item in _currentStatesArray)
            {
                if (!HasStarted || IsDisposed)
                {
                    break;
                }

                if (!item.IsRunning)
                {
                    continue;
                }

                item.OnUpdate();
            }

            UpdateTrigger();
        }

        /// <summary>对当前节点快照转发后置更新，不推进 Trigger 队列。</summary>
        public void LateUpdate()
        {
            if (!HasStarted || IsDisposed)
            {
                return;
            }

            _currentStatesArray.Clear();

            foreach (var item in _currentStates.Keys)
            {
                if (_currentStates[item] == null)
                {
                    continue;
                }

                _currentStatesArray.Add(_currentStates[item]);
            }

            foreach (var item in _currentStatesArray)
            {
                if (!HasStarted || IsDisposed)
                {
                    break;
                }

                if (!item.IsRunning)
                {
                    continue;
                }

                item.OnLateUpdate();
            }
        }

        /// <summary>对当前节点快照转发固定步长更新，不推进 Trigger 队列。</summary>
        public void FixedUpdate()
        {
            if (!HasStarted || IsDisposed)
            {
                return;
            }

            _currentStatesArray.Clear();

            foreach (var item in _currentStates.Keys)
            {
                if (_currentStates[item] == null)
                {
                    continue;
                }

                _currentStatesArray.Add(_currentStates[item]);
            }

            foreach (var item in _currentStatesArray)
            {
                if (!HasStarted || IsDisposed)
                {
                    break;
                }

                if (!item.IsRunning)
                {
                    continue;
                }

                item.OnFixedUpdate();
            }
        }



        /// <summary>遍历待触发哈希快照，每个已清零参数最多取出一次并重新置位。</summary>
        private void UpdateTrigger()
        {
            _triggerQueue.CopyKeysTo(_triggerKeys);
            for (int i = 0; i < _triggerKeys.Count && HasStarted && !IsDisposed; i++)
            {
                int item = _triggerKeys[i];
                if (_triggerQueue.TryTake(item, GetParamter(item) == 0))
                {
                    SetParamter(item, 1, ParameterType.Trigger);
                }
            }
        }

        /// <summary>查询指定直接父层中的当前节点。</summary>
        /// <param name="parent">非 null 的父节点名，空字符串表示基础层。</param>
        /// <returns>该层当前节点；尚未进入或已退出时为 null。</returns>
        public FsmStateNode GetCurrentState(string parent)
        {

            if (_currentStates.ContainsKey(parent))
            {
                return _currentStates[parent];
            }

            return null;
        }
        /// <summary>直接恢复构造时保存的参数默认值，不触发变化通知或条件切换。</summary>
        internal void ResetParamter()
        {
            foreach (var item in ParameterDefaults.Keys)
            {
                FsmParameterData parameter;
                if (Parameters.TryGetValue(item, out parameter))
                {
                    parameter.value = ParameterDefaults[item];
                }
            }
        }
    }
}
