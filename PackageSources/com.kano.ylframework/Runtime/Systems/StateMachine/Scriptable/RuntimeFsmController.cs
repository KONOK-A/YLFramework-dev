using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace YLFramework.StateMachine
{
    /// <summary>状态机配置资产，保存节点、参数和有序过渡；执行时由实例克隆以分离运行数据。</summary>
    /// <remarks>查询缓存按需建立；直接修改公开列表后需在编辑器清理缓存，集合本身不自动维护引用。</remarks>
    public class RuntimeFsmController : ScriptableObject
    {

#if UNITY_EDITOR
        /// <summary>
        /// 图编辑器视口的平移位置。
        /// </summary>
        [HideInInspector]
        public Vector3 viewPosition = new Vector3(100, 100, 0);
        /// <summary>图编辑器视口缩放。</summary>
        [HideInInspector]
        public Vector3 viewScale = Vector3.one * 0.8f;

        /// <summary>
        /// 图编辑器当前浏览的子状态机路径，从外到内排列。
        /// </summary>
        [HideInInspector]
        [FormerlySerializedAs("Layers")] public List<string> layers = new List<string>();

        /// <summary>
        /// 运行时配置克隆对应的源资产 GUID，仅供编辑器定位源配置，不代表自动回写。
        /// </summary>
        [HideInInspector]
        [FormerlySerializedAs("originGUID")] public string originGuid;

        /// <summary>向当前浏览路径追加尚未包含的层级名称并请求保存。</summary>
        /// <param name="layer">要进入的子状态机名称。</param>
        public void AddLayer(string layer)
        {
            if (layers.Contains(layer))
            {
                return;
            }

            layers.Add(layer);
            Save();
        }

        /// <summary>移除浏览路径的指定区段并请求保存，索引合法性由调用方保证。</summary>
        /// <param name="index">移除起点。</param>
        /// <param name="count">移除的层级数量。</param>
        public void RemoveLayer(int index, int count)
        {
            layers.RemoveRange(index, count);
            Save();
        }

        /// <summary>
        /// 清空图编辑器浏览路径并请求标脏，不退出运行时状态。
        /// </summary>
        public void ClearLayer()
        {
            layers.Clear();
            Save();
        }
#endif

        /// <summary>
        /// 包含内置节点及各子层节点的完整定义列表。
        /// </summary>
        [HideInInspector]
        public List<FsmStateNodeData> states = new List<FsmStateNodeData>();

        /// <summary>
        /// 参数定义及默认值列表。
        /// </summary>
        [HideInInspector]
        public List<FsmParameterData> parameters = new List<FsmParameterData>();

        /// <summary>
        /// 按配置顺序保存的过渡；主动扫描时采用首个满足项，参数变化也可同步触发求值。
        /// </summary>
        [HideInInspector]
        public List<FsmTransitionData> transitions = new List<FsmTransitionData>();

        /// <summary>在克隆和状态脚本实例化前定位配置中的无效字段。</summary>
        /// <param name="error">首个结构或脚本类型错误；有效时为 null。</param>
        /// <returns>结构有效且脚本均可解析为具有公开无参构造器的具体 FsmState 时为 true。</returns>
        public bool ValidateConfiguration(out string error)
        {
            error = FsmConfigurationValidator.Validate(states, parameters, transitions);
            if (error != null)
            {
                return false;
            }

            foreach (var state in states)
            {
                foreach (var script in state.stateScripts)
                {
                    var type = script == null ? null : YLFramework.Foundation.AssemblyTool.GetType(script.className);
                    if (type != null && !type.IsAbstract && !type.ContainsGenericParameters && typeof(FsmState).IsAssignableFrom(type) && type.GetConstructor(System.Type.EmptyTypes) != null)
                    {
                        continue;
                    }

                    error = $"state '{state.name}'.StateScripts className '{script?.className}' must resolve to a concrete FSMState with a public parameterless constructor.";
                    return false;
                }
            }

            return true;
        }


        private Dictionary<string, FsmStateNodeData> _statesDic = new Dictionary<string, FsmStateNodeData>();
        private Dictionary<string, FsmParameterData> _parametersDic = new Dictionary<string, FsmParameterData>();
        private Dictionary<string, FsmStateNodeData> _defaultStatesDic = new Dictionary<string, FsmStateNodeData>();
        private Dictionary<string, FsmTransitionData> _transitionsDic = new Dictionary<string, FsmTransitionData>();


        /// <summary>按名称从延迟建立的索引查询节点，不创建运行时节点。</summary>
        /// <param name="name">配置内的节点名称。</param>
        /// <returns>节点定义；名称为空引用或未命中时返回 null。</returns>
        public FsmStateNodeData GetStateNodeData(string name)
        {

            if (name == null)
            {
                return null;
            }

            if (_statesDic.Count == 0)
            {
                foreach (FsmStateNodeData state in states)
                {
                    if (_statesDic.ContainsKey(state.name))
                    {
                        throw new System.Exception(string.Format("状态名称重复:{0}", state.name));
                    }

                    _statesDic.Add(state.name, state);
                }
            }

            if (_statesDic.ContainsKey(name))
            {
                return _statesDic[name];
            }

            return null;
        }

        /// <summary>按名称从延迟建立的索引查询参数定义。</summary>
        /// <param name="name">参数名称。</param>
        /// <returns>参数定义；名称为空或未命中时返回 null。</returns>
        public FsmParameterData GetParameterData(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            if (name == null)
            {
                return null;
            }

            if (_parametersDic.Count == 0)
            {
                foreach (var item in parameters)
                {
                    if (_parametersDic.ContainsKey(item.name))
                    {
                        throw new System.Exception(string.Format("参数名称重复:{0}", item.name));
                    }

                    _parametersDic.Add(item.name, item);
                }
            }

            if (_parametersDic.ContainsKey(name))
            {
                return _parametersDic[name];
            }

            return null;
        }

        /// <summary>
        /// 按直接父节点名称查询该层唯一默认状态，重复默认定义会抛错。
        /// </summary>
        /// <param name="parent">直接父节点名，空字符串表示基础层。</param>
        /// <returns>默认节点定义；父节点名为 null 或未命中时返回 null。</returns>
        public FsmStateNodeData GetDefaultStateNodeData(string parent)
        {
            if (parent == null)
            {
                return null;
            }

            if (_defaultStatesDic.Count == 0)
            {
                foreach (var item in states)
                {
                    if (!item.isDefaultState)
                    {
                        continue;
                    }

                    if (_defaultStatesDic.ContainsKey(item.Parent))
                    {
                        throw new System.Exception(string.Format("检测到层级:{0}有多个默认状态!", item.Parent));
                    }

                    _defaultStatesDic.Add(item.Parent, item);
                }
            }

            if (_defaultStatesDic.ContainsKey(parent))
            {
                return _defaultStatesDic[parent];
            }

            return null;
        }

        /// <summary>按来源与目标拼接键查询已配置的过渡，不执行条件求值。</summary>
        /// <param name="from">来源节点名称。</param>
        /// <param name="to">目标节点名称。</param>
        /// <returns>过渡定义；未命中时返回 null。</returns>
        public FsmTransitionData GetTransitionData(string from, string to)
        {

            if (_transitionsDic.Count == 0)
            {
                foreach (var item in transitions)
                {
                    _transitionsDic.Add(item.Key, item);
                }
            }

            string key = string.Format("{0}:{1}", from, to);

            if (_transitionsDic.ContainsKey(key))
            {
                return _transitionsDic[key];
            }

            return null;
        }

#if UNITY_EDITOR

        private Dictionary<string, List<FsmStateNodeData>> _currentShowStates = new Dictionary<string, List<FsmStateNodeData>>();
        private Dictionary<string, List<FsmTransitionData>> _currentShowTransitions = new Dictionary<string, List<FsmTransitionData>>();



        /// <summary>清理查询缓存、追加节点并请求保存；不代替工厂的命名与 Undo 处理。</summary>
        /// <param name="state">要追加的节点定义。</param>
        public void AddState(FsmStateNodeData state)
        {
            ClearCache();
            states.Add(state);
            Save();
        }

        /// <summary>移除节点本身并请求保存；关联过渡和子节点由调用方维护。</summary>
        /// <param name="state">要移除的节点定义。</param>
        public void RemoveState(FsmStateNodeData state)
        {
            ClearCache();
            states.Remove(state);
            Save();
        }

        /// <summary>清理查询缓存、追加参数定义并请求保存。</summary>
        /// <param name="data">要追加的参数定义。</param>
        public void AddParameters(FsmParameterData data)
        {
            ClearCache();
            parameters.Add(data);
            Save();
        }

        /// <summary>移除参数定义并请求保存，引用此参数的条件由调用方维护。</summary>
        /// <param name="data">要移除的参数定义。</param>
        public void RemoveParameters(FsmParameterData data)
        {
            ClearCache();
            parameters.Remove(data);
            Save();
        }

        /// <summary>清理查询缓存、追加过渡并请求保存。</summary>
        /// <param name="data">要追加的过渡定义。</param>
        public void AddTransition(FsmTransitionData data)
        {
            ClearCache();
            transitions.Add(data);
            Save();
        }

        /// <summary>清理查询缓存、移除指定过渡并请求保存。</summary>
        /// <param name="data">要移除的过渡定义。</param>
        public void RemoveTransition(FsmTransitionData data)
        {
            ClearCache();
            transitions.Remove(data);
            Save();
        }


        /// <summary>清空节点、参数、默认状态、过渡及图层分组索引，使下次查询重新建立。</summary>
        public void ClearCache()
        {
            _statesDic.Clear();
            _parametersDic.Clear();
            _defaultStatesDic.Clear();
            _currentShowStates.Clear();
            _currentShowTransitions.Clear();
            _transitionsDic.Clear();
        }


        /// <summary>取得直接属于指定父层的节点列表；找不到该层时清空浏览路径。</summary>
        /// <param name="parent">直接父节点名，空字符串表示基础层。</param>
        /// <returns>缓存中的节点列表；父层为 null 或不存在时返回 null。</returns>
        public List<FsmStateNodeData> GetCurrentShowStateNodeData(string parent)
        {

            if (parent == null)
            {
                return null;
            }

            if (_currentShowStates.Count == 0)
            {
                foreach (var item in states)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (_currentShowStates.ContainsKey(item.Parent))
                    {
                        _currentShowStates[item.Parent].Add(item);
                    }
                    else
                    {
                        _currentShowStates.Add(item.Parent, new List<FsmStateNodeData> { item });
                    }
                }
            }

            if (_currentShowStates.ContainsKey(parent))
            {
                List<FsmStateNodeData> currentShowState = _currentShowStates[parent];
                if (currentShowState.Count == 0)
                {
                    ClearLayer();
                }

                return currentShowState;
            }

            ClearLayer();

            return null;
        }

        /// <summary>按来源父层查询可显示的过渡，忽略端点缺失或跨越不兼容层级的边。</summary>
        /// <param name="parent">直接父节点名，空字符串表示基础层。</param>
        /// <returns>缓存中的过渡列表；父层为空引用或无匹配时返回 null。</returns>
        public List<FsmTransitionData> GetCurrentShowTransitionData(string parent)
        {

            if (parent == null)
            {
                return null;
            }

            if (_currentShowTransitions.Count == 0)
            {
                foreach (var item in transitions)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    FsmStateNodeData from = states.Find(state => state != null && state.name == item.fromStateName);
                    FsmStateNodeData to = states.Find(state => state != null && state.name == item.toStateName);

                    if (from == null || to == null || !from.CompareParent(to))
                    {
                        continue;
                    }

                    if (_currentShowTransitions.ContainsKey(from.Parent))
                    {
                        _currentShowTransitions[from.Parent].Add(item);
                    }
                    else
                    {
                        _currentShowTransitions.Add(from.Parent, new List<FsmTransitionData>() { item });
                    }
                }
            }

            if (_currentShowTransitions.ContainsKey(parent))
            {
                return _currentShowTransitions[parent];
            }

            return null;
        }


#endif


        /// <summary>在编辑器中调用保存桥接，当前实现仅标脏；不自行记录 Undo、落盘或回写源资产。</summary>
        /// <remarks>Player 中无操作，需立即保存资产的作者入口另行调用资产保存 API。</remarks>
        public void Save()
        {
#if UNITY_EDITOR
            FsmEditorHooks.Save?.Invoke(this);
#endif
        }

        /// <summary>在编辑器中逐节点刷新脚本绑定；Player 中不执行资产查找。</summary>
        public void RefreshStateScripts()
        {
#if UNITY_EDITOR
            foreach (var state in states)
            {
                state.RefreshStateScripts(this);
            }
#endif
        }


    }

}
