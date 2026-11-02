using System;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Events;
using YLFramework.Foundation;

namespace YLFramework.Mission
{

    /// <summary>
    /// 参数比较类型
    /// </summary>
    public enum CompareType
    {
        /// <summary>
        /// 大于
        /// </summary>
        [Tooltip("大于")]
        Greater,
        /// <summary>
        /// 小于
        /// </summary>
        [Tooltip("小于")]
        Less,
        /// <summary>
        /// 等于
        /// </summary>
        [Tooltip("等于")]
        Equals,
        /// <summary>
        /// 不等于
        /// </summary>
        [Tooltip("不等于")]
        NotEquals,
        /// <summary>
        /// 大于等于
        /// </summary>
        [Tooltip("大于等于")]
        GreaterEqual,
        /// <summary>
        /// 小于等于
        /// </summary>
        [Tooltip("小于等于")]
        LessEqual
    }

    /// <summary>
    /// 任务条件
    /// </summary>
    /// <remarks>由管理器在任务配置副本中绑定运行参数；修改目标或比较方式本身不会自动重新计算。</remarks>
    [Serializable]
    public class MissionCondition
    {

        #region 字段
        /// <summary>
        /// 参数名称
        /// </summary>
        [Tooltip("参数名称")]
        [FormerlySerializedAs("param_name"), JsonProperty("param_name")] public string paramName;
        /// <summary>
        /// 比较方式
        /// </summary>
        [Tooltip("比较方式")]
        public CompareType compareType;
        /// <summary>
        /// 目标值
        /// </summary>
        [Tooltip("目标值")]
        public float target;


        private bool _isMet = false;

        private int _configName;

        private int _paramNameHash;
        [NonSerialized] private MissionParameter _parameter;
        [NonSerialized] private IUnregister _subscription;
        private int _version;

        #endregion

        #region 属性

        /// <summary>
        /// 参数对象
        /// </summary>
        [JsonIgnoreAttribute]
        public MissionParameter Param => _parameter;

        /// <summary>
        /// 条件是否已经满足
        /// </summary>
        /// <remarks>直接设为 true 仅在标记变化时调用 Met，不触发进度通知，也不重新比较参数。</remarks>
        [JsonIgnoreAttribute]
        public bool IsMet
        {
            get
            {
                return _isMet;
            }
            set
            {
                if (_isMet == value)
                {
                    return;
                }

                _isMet = value;
                if (_isMet)
                {
                    Met?.Invoke();
                }
            }
        }

        /// <summary>
        /// 条件进度；未绑定为 0，已满足为 1。
        /// </summary>
        /// <remarks>未满足的数值条件仅对大于及大于等于计算受限比例；布尔条件比较整数转换后的值。</remarks>
        [JsonIgnoreAttribute]
        public float Progress
        {
            get
            {
                if (Param == null)
                {
                    return 0;
                }

                if (IsMet) // 条件已经满足 进度为1
                {
                    return 1;
                }

                switch (Param.type)
                {
                    case MissionParamType.Float:
                    case MissionParamType.Int:

                        // 其余数值比较没有连续进度，未满足时统一为 0。
                        if (compareType != CompareType.Greater && compareType != CompareType.GreaterEqual)
                        {
                            return 0;
                        }

                        if (target == 0)
                        {
                            return 0;
                        }

                        return Mathf.Clamp01(Param.Value / target);
                    case MissionParamType.Bool:
                        int p = (int)Param.Value;
                        int t = (int)target;
                        return p == t ? 1 : 0;
                }

                return 0;
            }
        }

        /// <summary>
        /// 参数名称的hash值
        /// </summary>
        [JsonIgnoreAttribute]
        public int ParamNameHash => _paramNameHash;

        #endregion



        #region 事件

        /// <summary>
        /// 当条件满足时触发
        /// </summary>
        /// <remarks>参数更新时先通知进度，再在未满足转为满足且版本仍有效时调用本委托。</remarks>
        [NonSerialized]
        [Tooltip("当条件满足时触发")]
        public Action Met;

        /// <summary>
        /// 参数更新并完成条件计算后同步触发，不要求进度数值确实变化。
        /// </summary>
        [NonSerialized]
        [Tooltip("当进度改变时触发")]
        public Action<MissionCondition> ProgressValueChanged;

        #endregion


        #region 方法

        /// <summary>解析参数名称哈希，并绑定指定配表的运行参数。</summary>
        internal void Init(int configNameHash)
        {
            this._configName = configNameHash;
            _paramNameHash = MissionManager.StringToHash(paramName);
            Bind(MissionManager.GetMissionParam(configNameHash, _paramNameHash));
        }

        /// <summary>释放旧绑定与回调，订阅新参数，并立即计算条件状态。</summary>
        internal void Bind(MissionParameter parameter)
        {
            Dispose();
            _parameter = parameter ?? throw new InvalidOperationException($"任务条件参数未登记: {paramName}");
            _subscription = parameter.RegisterChanged(UpdateState);
            UpdateState(parameter);
        }

        /// <summary>使当前版本失效，归还参数订阅并清空绑定、满足标记和条件回调。</summary>
        internal void Dispose()
        {
            _version++;
            _subscription?.Unregister();
            _subscription = null;
            _parameter = null;
            _isMet = false;
            Met = null;
            ProgressValueChanged = null;
        }


        /// <summary>按参数类型重算满足状态，先发进度通知，再按版本检查决定是否发出首次满足通知。</summary>
        internal void UpdateState(MissionParameter p)
        {
            var version = _version;
            var parameterVersion = p.Version;
            var wasMeet = _isMet;
            switch (p.type)
            {
                case MissionParamType.Float:

                    switch (compareType)
                    {
                        case CompareType.Greater:
                            _isMet = p.Value > target;
                            break;
                        case CompareType.Less:
                            _isMet = p.Value < target;
                            break;
                        case CompareType.GreaterEqual:
                            _isMet = p.Value >= target;
                            break;
                        case CompareType.LessEqual:
                            _isMet = p.Value <= target;
                            break;
                        case CompareType.Equals:
                            _isMet = p.Value == target;
                            break;
                        case CompareType.NotEquals:
                            _isMet = p.Value != target;
                            break;
                    }

                    break;
                case MissionParamType.Int:

                    float v = Mathf.RoundToInt(p.Value);
                    float t = Mathf.RoundToInt(target);

                    switch (compareType)
                    {
                        case CompareType.Greater:
                            _isMet = v > t;
                            break;
                        case CompareType.Less:
                            _isMet = v < t;
                            break;
                        case CompareType.Equals:
                            _isMet = v == t;
                            break;
                        case CompareType.NotEquals:
                            _isMet = v != t;
                            break;

                        case CompareType.GreaterEqual:
                            _isMet = v >= t;
                            break;

                        case CompareType.LessEqual:
                            _isMet = v <= t;
                            break;
                    }

                    break;
                case MissionParamType.Bool:
                    _isMet = Mathf.RoundToInt(p.Value) == Mathf.RoundToInt(target);
                    break;
            }
            // 先写满足状态供进度回调读取；回调重绑条件或改写参数后不再发送旧值的满足通知。
            ProgressValueChanged?.Invoke(this);
            if (version == _version && parameterVersion == p.Version && _isMet && !wasMeet)
            {
                Met?.Invoke();
            }
        }

        /// <summary>仅清除满足标记，不解绑参数，也不重新计算或发送进度通知。</summary>
        internal void Reset()
        {
            IsMet = false;
        }

        #endregion

    }

}
