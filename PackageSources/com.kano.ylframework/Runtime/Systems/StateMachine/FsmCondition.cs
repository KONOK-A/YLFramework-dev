using System;
using System.Collections.Generic;


namespace YLFramework.StateMachine
{

    /// <summary>单个参数比较条件的即时求值结果。</summary>
    public enum ConditionState
    {
        /// <summary>当前参数满足目标比较。</summary>
        Meet,
        /// <summary>当前参数不满足目标比较或比较器不可用。</summary>
        NotMeet

    }

    /// <summary>将配置条件绑定到实例参数，在参数变化且比较满足时发出通知。</summary>
    public class FsmCondition
    {

        #region 字段
        private FsmConditionData _data;

        private FsmParameterData _parameter;

        /// <summary>重新求值为满足时同步调用的通知，不保证只在不满足变为满足时触发。</summary>
        public Action ConditionMet;

        private static Dictionary<CompareType, IParameterComparer> _compares = null;

        #endregion

        #region 属性
        /// <summary>以当前参数值与配置目标值即时比较，不缓存上次结果。</summary>
        public ConditionState State
        {
            get
            {
                if (Compare != null && Compare.Matches(this._parameter, _data.targetValue))
                {
                    return ConditionState.Meet;
                }
                else
                {
                    return ConditionState.NotMeet;
                }
            }
        }

        private IParameterComparer Compare => GetCompare(_data.compareType);

        /// <summary>此条件绑定的实例参数；未找到参数时为空。</summary>
        public FsmParameterData Parameter => _parameter;


        private static Dictionary<CompareType, IParameterComparer> Compares
        {
            get
            {
                if (_compares == null)
                {
                    _compares = new Dictionary<CompareType, IParameterComparer>();
                    _compares.Add(CompareType.Equal, new EqualCompare());
                    _compares.Add(CompareType.Greater, new GreaterCompare());
                    _compares.Add(CompareType.Less, new LessCompare());
                    _compares.Add(CompareType.NotEqual, new NotEqualCompare());
                }

                return _compares;
            }

        }

        #endregion

        /// <summary>解析参数、订阅其变化，并进行一次初始求值。</summary>
        /// <param name="data">参数名称、比较方式与目标值。</param>
        /// <param name="controller">提供运行时参数的状态机实例。</param>
        public FsmCondition(FsmConditionData data, RuntimeFsmControllerInstance controller)
        {
            this._data = data;

            int parameterNameHash = FsmController.StringToHash(data.parameterName);

            if (controller.Parameters.ContainsKey(parameterNameHash))
            {
                _parameter = controller.Parameters[parameterNameHash];
            }

            if (_parameter != null)
            {
                _parameter.ValueChanged += CheckParameterValueChange;
            }


            CheckParameterValueChange();

        }

        /// <summary>重新求值单个条件，满足时同步通知订阅者继续检查整个条件组。</summary>
        public void CheckParameterValueChange()
        {
            if (State == ConditionState.Meet)
            {
                ConditionMet?.Invoke();
            }
        }

        /// <summary>取得按比较类型共享的比较器。</summary>
        /// <param name="type">所需比较方式。</param>
        /// <returns>对应比较器；未支持的类型返回 null。</returns>
        public static IParameterComparer GetCompare(CompareType type)
        {
            IParameterComparer pamaterCompare;
            if (Compares.TryGetValue(type, out pamaterCompare))
            {
                return pamaterCompare;
            }
            return null;
        }

    }


    /// <summary>以 AND 组合一组条件；多个组在过渡层按 OR 组合。</summary>
    public class FsmConditionGroup
    {

        private List<FsmCondition> _conditions = new List<FsmCondition>();

        /// <summary>成员条件通知后，整组重新求值为满足时同步调用。</summary>
        public Action ConditionMet;

        /// <summary>所有成员均满足时为 true；空组仅在整条过渡无条件且启用自动切换时满足。</summary>
        public bool IsMet
        {
            get
            {
                if (_transition.IsEmpty && _transition.autoSwitch)
                {
                    return true;
                }

                if (ConditionCount == 0)
                {
                    return false;
                }

                foreach (var item in _conditions)
                {
                    if (item.State == ConditionState.NotMeet)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>当前组中已建立的运行时条件数量。</summary>
        public int ConditionCount
        {
            get
            {
                if (_conditions == null)
                {
                    return 0;
                }

                return _conditions.Count;
            }
        }

        private RuntimeFsmControllerInstance _controller;

        private FsmTransitionData _transition;

        /// <summary>创建组内条件并接入逐条件通知，不在此执行状态切换。</summary>
        /// <param name="controller">提供参数及 Trigger 清理入口的实例。</param>
        /// <param name="conditions">此组的条件定义。</param>
        /// <param name="transition">所属过渡，用于判断无条件自动切换。</param>
        public FsmConditionGroup(RuntimeFsmControllerInstance controller, List<FsmConditionData> conditions, FsmTransitionData transition)
        {
            this._controller = controller;
            this._transition = transition;
            this._conditions.Clear();
            foreach (var item in conditions)
            {
                FsmCondition condition = new FsmCondition(item, controller);
                condition.ConditionMet += this.CheckIsMeet;
                this._conditions.Add(condition);
            }
        }


        //private void OnConditionMeet()
        //{
        //    //Debug.Log("条件满足!");
        //    DelayTools.DelayCall(CheckIsMeet);
        //}

        /// <summary>响应单条件满足通知，整组满足时再向过渡发出通知。</summary>
        private void CheckIsMeet()
        {
            if (IsMet)
            {
                ConditionMet?.Invoke();
            }
        }

        /// <summary>清除此组引用的 Trigger 当前值，不丢弃实例队列中的后续触发次数。</summary>
        internal void ResetTrigger()
        {
            foreach (var item in _conditions)
            {
                if (item.Parameter != null && item.Parameter.parameterType == ParameterType.Trigger)
                {
                    _controller.ClearTrigger(item.Parameter.nameHash);
                }
            }
        }

    }

}
