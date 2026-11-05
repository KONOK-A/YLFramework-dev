using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace YLFramework.StateMachine
{
    /// <summary>宿主中的命名配置层；执行前指向源资产，活动层列表中指向本层克隆。</summary>
    [Serializable]
    public class RuntimeFsmControllerLayer
    {
        /// <summary>
        /// 状态机层的名称
        /// </summary>
        public string name;
        /// <summary>
        /// 此层使用的配置引用。
        /// </summary>
        public RuntimeFsmController controller;
    }

    /// <summary>Unity 状态机宿主，创建和驱动多个配置层，并提供跨层参数广播与状态查询。</summary>
    /// <remarks>默认在 Start 初始化；禁用时可关闭并复用克隆，销毁或移除层时永久释放所属实例。</remarks>
    public class FsmController : MonoBehaviour
    {

        #region 字段

        [SerializeField]
        [FormerlySerializedAs("_runtimeFSMControllerLayer")]
        private List<RuntimeFsmControllerLayer> runtimeFsmControllerLayers = new List<RuntimeFsmControllerLayer>();

        internal object UserDataValue;
        /// <summary>在启动前为直接挂载的控制器设置状态脚本共享数据，不必使用静态宿主。</summary>
        public object UserData
        {
            get => UserDataValue; set => UserDataValue = value;
        }

        private List<RuntimeFsmControllerInstance> _instances = new List<RuntimeFsmControllerInstance>();
        private readonly List<RuntimeFsmControllerInstance> _phaseSnapshot = new List<RuntimeFsmControllerInstance>();
        private readonly Stack<List<RuntimeFsmControllerInstance>> _broadcastPool = new Stack<List<RuntimeFsmControllerInstance>>();
        private bool _isInitializing;
        private bool _isDestroying;

        private List<RuntimeFsmController> _runtimeControllers = new List<RuntimeFsmController>();

        private List<RuntimeFsmControllerLayer> _activeLayers = new List<RuntimeFsmControllerLayer>();

        [SerializeField]
        [Tooltip("是否在OnDisable时重置状态机")]
        [Header("是否在OnDisable时重置状态机")]
        private bool resetOnDisable = true;

        #endregion

        #region 事件
        /// <summary>
        /// 初始化各层的流程结束后同步通知；不表示每个源配置都成功启动，订阅不会在通知后自动清空。
        /// </summary>
        public Action Initialized;
        /// <summary>
        /// 节点进入回调后同步通知，参数依次为配置名称、节点名称；子层默认节点进入前也会通知容器。
        /// </summary>
        public Action<string, string> StateChanged;

        #endregion

        #region 属性

        /// <summary>
        /// 返回复用的配置列表：运行且初始化中或已完成时为活动克隆，否则为源配置。
        /// </summary>
        /// <remarks>每次读取都会清空并重填同一列表，调用方不应将其作为稳定快照或注册入口。</remarks>
        public List<RuntimeFsmController> RuntimeFsmController
        {
            get
            {
                _runtimeControllers.Clear();

                if (Application.isPlaying && (IsInitialized || _isInitializing))
                {
                    foreach (var item in _activeLayers)
                    {
                        if (item.controller == null)
                        {
                            continue;
                        }

                        _runtimeControllers.Add(item.controller);
                    }
                }
                else
                {
                    foreach (var item in runtimeFsmControllerLayers)
                    {
                        if (item.controller == null)
                        {
                            continue;
                        }

                        _runtimeControllers.Add(item.controller);
                    }
                }

                return _runtimeControllers;
            }
        }

        /// <summary>取得当前使用的层列表：运行且初始化中或已完成时为活动层，否则为源定义。</summary>
        /// <remarks>返回内部列表而非副本；增删执行层应使用对应方法，以同步维护实例所有权。</remarks>
        public List<RuntimeFsmControllerLayer> RuntimeFsmControllerLayers
        {
            get
            {
                if (Application.isPlaying && (IsInitialized || _isInitializing))
                {
                    return _activeLayers;
                }

                return runtimeFsmControllerLayers;
            }
        }

        /// <summary>
        /// 初始层处理流程是否已结束；在调用初始化通知前置位，不保证所有层有效或回调无异常。
        /// </summary>
        public bool IsInitialized { get; private set; } = false;

        #endregion

        #region 生命周期

        /// <summary>在 Unity Start 中初始化预设层，使状态脚本晚于场景组件 Awake 执行。</summary>
        private void Start()
        {
            if (!IsInitialized)
            {
                Init();
            }
        }

        /// <summary>驱动逻辑阶段，并在关闭后重新启用时启动尚未运行的层。</summary>
        private void Update()
            => Tick(0);

        /// <summary>向活动层转发后置更新阶段。</summary>
        private void LateUpdate()
            => Tick(1);

        /// <summary>向活动层转发固定步长更新阶段。</summary>
        private void FixedUpdate()
            => Tick(2);

        /// <summary>按阶段快照驱动仍归属宿主的层，跳过被回调移除的实例。</summary>
        private void Tick(int phase)
        {
            _phaseSnapshot.Clear();
            _phaseSnapshot.AddRange(_instances);
            try
            {
                foreach (var item in _phaseSnapshot)
                {
                    if (!this || !isActiveAndEnabled || _isDestroying)
                    {
                        break;
                    }

                    if (item.IsDisposed || !_instances.Contains(item))
                    {
                        continue;
                    }

                    if (phase == 0 && !item.HasStarted)
                    {
                        StartInstance(item);
                    }

                    if (!this || !isActiveAndEnabled || item.IsDisposed || !_instances.Contains(item))
                    {
                        continue;
                    }

                    if (phase == 0)
                    {
                        item.Update();
                    }
                    else if (phase == 1)
                    {
                        item.LateUpdate();
                    }
                    else
                    {
                        item.FixedUpdate();
                    }
                }
            }
            finally
            {
                _phaseSnapshot.Clear();
            }
        }

        /// <summary>先摘除层记录，再释放所有克隆，并在收尾时清空通知及静态宿主登记。</summary>
        private void OnDestroy()
        {
            _isDestroying = true;
            IsInitialized = false;
            var snapshot = _instances.ToArray();
            _instances.Clear();
            _activeLayers.Clear();
            try
            {
                CloseInstances(snapshot, true);
            }
            finally
            {
                Initialized = null;
                StateChanged = null;
                FsmControllerExtension.Unregister(this);
            }
        }

        /// <summary>按配置关闭各层而不销毁克隆，供下一次逻辑阶段重新启动。</summary>
        private void OnDisable()
        {
            if (resetOnDisable)
            {
                CloseInstances(_instances.ToArray(), false);
            }
        }

        /// <summary>尝试关闭或释放快照中的全部层，完成后重抛首个异常。</summary>
        private static void CloseInstances(RuntimeFsmControllerInstance[] snapshot, bool dispose)
        {
            Exception firstError = null;
            foreach (var instance in snapshot)
            {
                try
                {
                    if (dispose)
                    {
                        instance.Dispose();
                    }
                    else
                    {
                        instance.Close();
                    }
                }
                catch (Exception error)
                {
                    firstError ??= error;
                }
            }
            // 用户退出回调的异常不吞掉，但必须先归还其余层的资源。
            if (firstError != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        #endregion

        #region 方法

        /// <summary>处理源层快照，允许回调增删层；收集错误并在其他层及初始化通知处理后重抛。</summary>
        private void Init()
        {
            _instances.Clear();
            _activeLayers.Clear();

            _isInitializing = true;
            Exception firstError = null;
            try
            {
                foreach (var item in runtimeFsmControllerLayers.ToArray())
                {
                    if (_isDestroying)
                    {
                        break;
                    }

                    if (!runtimeFsmControllerLayers.Contains(item) || !item.controller)
                    {
                        continue;
                    }

                    try
                    {
                        StartLayer(item);
                    }
                    catch (Exception error)
                    {
                        firstError ??= error;
                    }
                }
            }
            finally
            {
                _isInitializing = false;
            }
            if (!_isDestroying)
            {
                IsInitialized = true;
                try
                {
                    Initialized?.Invoke();
                }
                catch (Exception error)
                {
                    firstError ??= error;
                }
            }
            if (firstError != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        /// <summary>验证并创建一层，先登记所有权再按宿主启用状态启动；配置及反射构造错误记录为日志。</summary>
        private void StartLayer(RuntimeFsmControllerLayer source)
        {
            RuntimeFsmControllerInstance instance;
            try
            {
                instance = new RuntimeFsmControllerInstance(source.controller, this, source.name);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError(exception.Message, this);
                return;
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                Debug.LogError($"Asset '{source.controller.name}', layer '{source.name}': state constructor failed. {exception.InnerException}", this);
                return;
            }
            // 先登记所有权，再执行用户回调，允许回调移除自身或其他层。
            _activeLayers.Add(new RuntimeFsmControllerLayer { name = source.name, controller = instance.RuntimeFsmController });
            _instances.Add(instance);
            if (isActiveAndEnabled)
            {
                StartInstance(instance);
            }
        }

        /// <summary>启动所属实例；首次回调失败时摘除并释放该层，再保留原始异常抛出。</summary>
        private void StartInstance(RuntimeFsmControllerInstance instance)
        {
            try
            {
                instance.StartUp();
            }
            catch (Exception error)
            {
                _instances.Remove(instance);
                _activeLayers.RemoveAll(layer => ReferenceEquals(layer.controller, instance.RuntimeFsmController));
                try
                {
                    instance.Dispose();
                }
                catch (Exception cleanupError)
                {
                    Debug.LogException(cleanupError, this);
                }
                // 首次回调失败后不再驱动半初始化实例；保留原异常和调用栈。
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        /// <summary>按名称向各层同名且同类型的布尔参数广播新值。</summary>
        /// <param name="name">参数名称。</param>
        /// <param name="v">新布尔值。</param>
        public void SetBool(string name, bool v)
        {
            SetBool(StringToHash(name), v);
        }

        /// <summary>
        /// 按哈希向各层布尔参数广播新值；初始化前的请求通过初始化通知延后执行。
        /// </summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <param name="v">新布尔值。</param>
        public void SetBool(int nameHash, bool v)
            => BroadcastParameter(nameHash, v ? 1 : 0, ParameterType.Bool);

        /// <summary>按名称向各层同名且同类型的浮点参数广播新值。</summary>
        /// <param name="name">参数名称。</param>
        /// <param name="v">新浮点值。</param>
        public void SetFloat(string name, float v)
        {
            SetFloat(StringToHash(name), v);
        }

        /// <summary>
        /// 按哈希向各层浮点参数广播新值，变化通知可同步执行状态切换。
        /// </summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <param name="v">新浮点值。</param>
        public void SetFloat(int nameHash, float v)
            => BroadcastParameter(nameHash, v, ParameterType.Float);

        /// <summary>按名称向各层同名且同类型的整数参数广播新值。</summary>
        /// <param name="name">参数名称。</param>
        /// <param name="v">新整数值。</param>
        public void SetInt(string name, int v)
        {
            SetInt(StringToHash(name), v);
        }

        /// <summary>
        /// 按哈希向各层整数参数广播新值，底层仍使用浮点数存储。
        /// </summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <param name="v">新整数值。</param>
        public void SetInt(int nameHash, int v)
            => BroadcastParameter(nameHash, v, ParameterType.Int);

        /// <summary>按名称向各层广播一次 Trigger，已置位的层将本次触发排队。</summary>
        /// <param name="name">Trigger 参数名称。</param>
        public void SetTrigger(string name)
        {
            SetTrigger(StringToHash(name));
        }

        /// <summary>
        /// 按哈希向各层广播一次 Trigger，允许参数通知同步触发过渡。
        /// </summary>
        /// <param name="nameHash">Trigger 参数名称哈希。</param>
        public void SetTrigger(int nameHash)
            => BroadcastParameter(nameHash, 1, ParameterType.Trigger);

        /// <summary>按独立层快照同步广播参数请求；初始化前挂到完成通知，递归广播各借用自己的快照。</summary>
        private void BroadcastParameter(int hash, double value, ParameterType type, bool resetTrigger = false)
        {
            if (_isDestroying)
            {
                return;
            }

            if (!IsInitialized)
            {
                Initialized += () => BroadcastParameter(hash, value, type, resetTrigger);
                return;
            }
            if (!ContainsParameter(hash, type))
            {
                if (!resetTrigger)
                {
                    Debug.LogWarningFormat("未查询到参数:{0} 类型:{1}", HashToString(hash), type);
                }

                return;
            }
            // 参数回调允许递归广播及增删层；借用独立快照，预热后不逐帧分配。
            var snapshot = _broadcastPool.Count > 0 ? _broadcastPool.Pop() : new List<RuntimeFsmControllerInstance>();
            snapshot.AddRange(_instances);
            try
            {
                foreach (var item in snapshot)
                {
                    if (_isDestroying || item.IsDisposed || !_instances.Contains(item))
                    {
                        continue;
                    }

                    if (type == ParameterType.Bool)
                    {
                        item.SetBool(hash, value != 0);
                    }
                    else if (type == ParameterType.Float)
                    {
                        item.SetFloat(hash, (float)value);
                    }
                    else if (type == ParameterType.Int)
                    {
                        item.SetInt(hash, (int)value);
                    }
                    else if (resetTrigger)
                    {
                        item.ResetTrigger(hash);
                    }
                    else
                    {
                        item.SetTrigger(hash);
                    }
                }
            }
            finally
            {
                snapshot.Clear();
                _broadcastPool.Push(snapshot);
            }
        }

        /// <summary>按名称清除各层 Trigger 当前值及全部排队次数。</summary>
        /// <param name="name">Trigger 参数名称。</param>
        public void ResetTrigger(string name)
        {
            ResetTrigger(StringToHash(name));
        }

        /// <summary>
        /// 按哈希清除各层 Trigger 当前值及排队次数，未找到参数时不告警。
        /// </summary>
        /// <param name="nameHash">Trigger 参数名称哈希。</param>
        public void ResetTrigger(int nameHash)
            => BroadcastParameter(nameHash, 0, ParameterType.Trigger, true);

        /// <summary>按名称查询各层布尔参数，任一匹配层值为一时返回 true。</summary>
        /// <param name="name">参数名称。</param>
        /// <returns>是否至少有一层的匹配布尔值为真。</returns>
        public bool GetBool(string name)
        {
            return GetBool(StringToHash(name));
        }

        /// <summary>
        /// 按哈希查询布尔参数；不选定单层，而是检查任一匹配层是否为真。
        /// </summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <returns>任一匹配布尔参数值为一时为 true；否则为 false。</returns>
        public bool GetBool(int nameHash)
        {
            foreach (var item in _instances)
            {
                if (item.Parameters.TryGetValue(nameHash, out var parameter) && parameter.parameterType == ParameterType.Bool && item.GetParamter(nameHash) == 1)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按名称取得层顺序中首个非零的匹配浮点参数值。</summary>
        /// <param name="name">参数名称。</param>
        /// <returns>首个非零值；均为零或未命中时为零。</returns>
        public float GetFloat(string name)
        {
            return GetFloat(StringToHash(name));
        }

        /// <summary>
        /// 按哈希取得层顺序中首个非零的匹配浮点参数值，不对各层求和。
        /// </summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <returns>首个非零值；均为零或未命中时为零。</returns>
        public float GetFloat(int nameHash)
        {
            foreach (var item in _instances)
            {
                if (!item.Parameters.TryGetValue(nameHash, out var parameter) || parameter.parameterType != ParameterType.Float)
                {
                    continue;
                }

                float v = item.GetParamter(nameHash);
                if (v != 0)
                {
                    return v;
                }
            }

            return 0;
        }

        /// <summary>按名称取得层顺序中首个非零的匹配整数参数值。</summary>
        /// <param name="name">参数名称。</param>
        /// <returns>首个非零整数值；均为零或未命中时为零。</returns>
        public int GetInt(string name)
        {
            return GetInt(StringToHash(name));
        }

        /// <summary>
        /// 按哈希取得层顺序中首个转换后非零的匹配整数参数值。
        /// </summary>
        /// <param name="nameHash">参数名称哈希。</param>
        /// <returns>首个非零整数值；均为零或未命中时为零。</returns>
        public int GetInt(int nameHash)
        {
            foreach (var item in _instances)
            {
                if (!item.Parameters.TryGetValue(nameHash, out var parameter) || parameter.parameterType != ParameterType.Int)
                {
                    continue;
                }

                int v = (int)(item.GetParamter(nameHash));
                if (v != 0)
                {
                    return v;
                }
            }

            return 0;
        }

        /// <summary>按名称查询各层 Trigger 是否已置位或仍有排队次数。</summary>
        /// <param name="name">Trigger 参数名称。</param>
        /// <returns>任一匹配层有当前或待触发记录时为 true。</returns>
        public bool GetTrigger(string name)
        {
            return GetTrigger(StringToHash(name));
        }

        /// <summary>
        /// 按哈希查询各层 Trigger，包含当前置位值及尚未处理的排队次数。
        /// </summary>
        /// <param name="nameHash">Trigger 参数名称哈希。</param>
        /// <returns>任一匹配层有当前或待触发记录时为 true。</returns>
        public bool GetTrigger(int nameHash)
        {
            foreach (var item in _instances)
            {
                if (item.Parameters.TryGetValue(nameHash, out var parameter) && parameter.parameterType == ParameterType.Trigger && (item.GetTriggerCount(nameHash) > 0 || item.GetParamter(nameHash) == 1))
                {
                    return true;
                }
            }
            return false;
        }


        /// <summary>按名称与类型判断是否至少有一个运行时层包含该参数。</summary>
        /// <param name="name">参数名称。</param>
        /// <param name="type">预期参数类型。</param>
        /// <returns>任一层包含匹配参数时为 true，初始化前通常为 false。</returns>
        public bool ContainsParameter(string name, ParameterType type)
        {
            return ContainsParameter(StringToHash(name), type);
        }

        /// <summary>按名称哈希与类型判断是否至少有一个运行时层包含该参数。</summary>
        /// <param name="name">参数名称哈希。</param>
        /// <param name="type">预期参数类型。</param>
        /// <returns>任一层包含匹配参数时为 true。</returns>
        public bool ContainsParameter(int name, ParameterType type)
        {
            foreach (var item in _instances)
            {
                if (item.Parameters.ContainsKey(name) && item.Parameters[name].parameterType == type)
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>
        /// 使用源配置对象哈希生成层名并添加配置，已初始化时创建运行时实例。
        /// </summary>
        /// <param name="controller">要添加的源配置。</param>
        public void AddRuntimeFsmController(RuntimeFsmController controller)
        {
            if (!controller || _isDestroying)
            {
                return;
            }

            string layerName = string.Format("Layer:{0}", controller.GetHashCode());
            AddRuntimeFsmController(controller, layerName);
        }

        /// <summary>
        /// 添加命名源层，拒绝重复源配置；初始化中或完成后会创建层，宿主启用时立即启动。
        /// </summary>
        /// <param name="controller">要添加的源配置。</param>
        /// <param name="layerName">新层名称，供查询和按名移除使用。</param>
        public void AddRuntimeFsmController(RuntimeFsmController controller, string layerName)
        {
            if (controller == null || _isDestroying)
            {
                return;
            }

            if (Contains(runtimeFsmControllerLayers, controller))
            {
                Debug.LogWarningFormat("状态机:{0}已经执行,请勿重复添加!", controller.name);
                return; // 说明已经添加了该状态机
            }

            RuntimeFsmControllerLayer layer = new RuntimeFsmControllerLayer();
            layer.name = layerName;
            layer.controller = controller;
            runtimeFsmControllerLayers.Add(layer);


            // 初始化前仅登记源定义；初始化中新增的层须立即处理，不依赖旧的源层快照。
            if (IsInitialized || _isInitializing)
            {
                StartLayer(layer);
            }

        }

        /// <summary>按层名移除全部匹配的源定义及活动实例，退出回调前即摘除记录。</summary>
        /// <param name="layerName">要移除的层名称；空名称不处理。</param>
        public void RemoveRuntimeFsmController(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
            {
                return;
            }

            // 同时取消尚未启动的源定义；先摘除，再运行可能重入并添加同名层的退出回调。
            runtimeFsmControllerLayers.RemoveAll(layer => layer.name == layerName);
            _activeLayers.RemoveAll(layer => layer.name == layerName);
            var snapshot = _instances.FindAll(instance => instance.LayerName == layerName).ToArray();
            foreach (var instance in snapshot)
            {
                _instances.Remove(instance);
            }

            CloseInstances(snapshot, true);
        }


        /// <summary>
        /// 以当前层列表下标取得层名，再移除所有同名层及其所属克隆。
        /// </summary>
        /// <param name="index">配置文件下标</param>
        public void RemoveRuntimeFsmController(int index)
        {
            if (index < 0 || index >= RuntimeFsmControllerLayers.Count)
            {
                return;
            }

            RemoveRuntimeFsmController(RuntimeFsmControllerLayers[index].name);
        }

        /// <summary>
        /// 按运行时实例下标查询指定父层中的当前节点。
        /// </summary>
        /// <param name="index">状态配置下标</param>
        /// <param name="subStateName">直接父状态机名称，空字符串表示基础层。</param>
        /// <returns>当前节点；下标无效或该父层未进入时为 null。</returns>
        public FsmStateNode GetCurrentStateInfo(int index, string subStateName = "")
        {
            if (index < 0 || index >= _instances.Count)
            {
                return null;
            }

            return _instances[index].GetCurrentState(subStateName);
        }

        /// <summary>按首个匹配层名查询指定父层中的当前节点。</summary>
        /// <param name="layerName">宿主层名称。</param>
        /// <param name="subStateName">直接父状态机名称，空字符串表示基础层。</param>
        /// <returns>当前节点；层名无效、未命中或父层未进入时为 null。</returns>
        public FsmStateNode GetCurrentStateInfo(string layerName, string subStateName = "")
        {

            if (string.IsNullOrEmpty(layerName))
            {
                return null;
            }

            foreach (var item in _instances)
            {
                if (item.LayerName.Equals(layerName))
                {
                    return item.GetCurrentState(subStateName);
                }
            }

            return null;
        }

        /// <summary>
        /// 查询指定实例最近一次记录的过渡，不表示存在持续中的过渡动画。
        /// </summary>
        /// <param name="index">运行时实例下标。</param>
        /// <returns>记录的过渡；下标无效、默认进入或关闭后可能为 null。</returns>
        public FsmTransition GetCurrentTransition(int index)
        {
            if (index < 0 || index >= _instances.Count)
            {
                return null;
            }

            return _instances[index].CurrentTransition;
        }


        /// <summary>按配置对象引用判断源层列表是否已包含指定配置。</summary>
        private bool Contains(List<RuntimeFsmControllerLayer> layers, RuntimeFsmController controller)
        {
            if (controller == null)
            {
                return false;
            }

            foreach (var layer in layers)
            {
                if (layer == null)
                {
                    continue;
                }

                if (layer.controller == controller)
                {
                    return true;
                }
            }
            return false;
        }

        #endregion


        #region 静态方法

        /// <summary>
        /// 请求静态入口创建按名登记的跨场景宿主，实际状态进入由宿主 Start 执行。
        /// </summary>
        /// <param name="fsmName">状态机名称</param>
        /// <param name="fsm">状态配置文件</param>
        /// <param name="userData">自定义参数或数据</param>
        /// <returns>新建宿主；非运行期、退出中、参数无效或名称已占用时为 null。</returns>
        public static FsmController StartupFsm(string fsmName, RuntimeFsmController fsm, object userData = null)
        {
            return FsmControllerExtension.StartupFsm(fsmName, fsm, userData);
        }

        /// <summary>
        /// 查询通过静态启动入口登记的存活宿主，不搜索场景中的直接挂载组件。
        /// </summary>
        /// <param name="fsmName">状态机名称</param>
        /// <returns>对应宿主；名称无效、未登记或对象已销毁时为 null。</returns>
        public static FsmController GetFsm(string fsmName)
        {
            return FsmControllerExtension.GetFsm(fsmName);
        }

        /// <summary>
        /// 移除静态登记并请求销毁对应宿主，层清理由组件销毁回调执行。
        /// </summary>
        /// <param name="fsmName">状态机名称</param>
        public static void RemoveFsm(string fsmName)
        {
            FsmControllerExtension.RemoveFsm(fsmName);
        }

        #endregion


        #region StringToHash

        /// <summary>按框架共用缓存取得与 Animator 算法一致的状态或参数名称哈希。</summary>
        /// <param name="eventName">非 null 的名称。</param>
        /// <returns>名称对应的整数哈希。</returns>
        public static int StringToHash(string eventName)
        {
            return FsmControllerExtension.StringToHash(eventName);
        }

        /// <summary>取得本轮缓存中此哈希首次登记的名称，供参数诊断使用。</summary>
        /// <param name="hash">名称哈希。</param>
        /// <returns>已缓存名称；未命中时为空字符串。</returns>
        internal static string HashToString(int hash)
        {
            return FsmControllerExtension.HashToString(hash);
        }

        #endregion


    }

}
