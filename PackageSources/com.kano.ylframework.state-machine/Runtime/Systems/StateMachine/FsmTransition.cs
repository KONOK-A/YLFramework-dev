using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.StateMachine
{
    /// <summary>
    /// 将一条配置过渡绑定到运行时条件组，满足来源与条件约束后同步请求切换。
    /// </summary>
    /// <remarks>
    /// 参数通知依订阅顺序逐项重查当时的来源与条件，不保证一次写入只切换一次。
    /// Any State 排除当前目标；普通来源的显式自环仍可重入，受实例的帧预算限制。
    /// </remarks>
    public class FsmTransition
    {
        #region 字段
        private FsmTransitionData _data;
        private RuntimeFsmControllerInstance _controller;
        private FsmStateNode _toState;

        /// <summary>按配置建立的 OR 条件组，首组对应基础条件列表。</summary>
        public List<FsmConditionGroup> ConditionGroups;

        #endregion

        #region 属性

        /// <summary>
        /// 过渡数据
        /// </summary>
        public FsmTransitionData Data => _data;

        /// <summary>
        /// 过渡目标状态
        /// </summary>
        public FsmStateNode ToState => _toState;

        /// <summary>条件组满足且来源在对应父层有效时可切换；Any State 不重复进入当前目标。</summary>
        internal bool IsMet
        {
            get
            {
                // 如果过渡没有条件认为条件满足,可以切换状态
                //if (conditions.Count == 0) return true;

                // 判断是不是所有的条件都满足了
                //foreach (var item in conditions)
                //{
                //    if (item.state == ConditionState.NotMeet) return false;
                //}

                if (!ConditionGroupMeet())
                {
                    return false;
                }

                if (_toState == null)
                {
                    Debug.LogError("查询目标状态失败!");
                    return false;
                }

                FsmStateNodeData from = _controller.RuntimeFsmController.GetStateNodeData(_data.fromStateName);
                if (from == null)
                {
                    return false;
                }

                string parent = from.Parent;
                FsmStateNode currentState = _controller.GetCurrentState(parent);

                if (currentState == null)
                {
                    return false;
                }

                if (!_data.fromStateName.Equals(currentState.Data.name))
                {
                    // AnyState
                    if (!from.IsAnyState)
                    {
                        return false;
                    }

                    // 判断当前的状态 跟 目标状态是不是一个
                    if (currentState.Data.name.Equals(_data.toStateName))
                    {
                        return false;
                    }
                }
                return true;
            }

        }

        #endregion

        #region 方法

        /// <summary>按配置创建条件组、绑定满足通知，并解析目标运行时节点。</summary>
        /// <param name="controller">拥有此过渡的实例。</param>
        /// <param name="data">来源、目标及条件定义。</param>
        internal FsmTransition(RuntimeFsmControllerInstance controller, FsmTransitionData data)
        {
            this._data = data;
            this._controller = controller;

            //this.conditions = new List<FSMCondition>();

            this.ConditionGroups = new List<FsmConditionGroup>();

            FsmConditionGroup group = new FsmConditionGroup(controller, this._data.conditions, data);
            group.ConditionMet += this.CheckCondition;
            ConditionGroups.Add(group);

            foreach (var item in data.groupConditions)
            {
                FsmConditionGroup conditionGroup = new FsmConditionGroup(controller, item.conditions, data);
                conditionGroup.ConditionMet += this.CheckCondition;
                ConditionGroups.Add(conditionGroup);
            }

            if (controller.States.ContainsKey(data.toStateName))
            {
                _toState = controller.States[data.toStateName];
            }

        }

        /// <summary>在参数条件通知中立即尝试过渡，不等待下一帧的逻辑更新。</summary>
        private void CheckCondition()
        {
            CheckConditionAndSwitch();
        }

        /// <summary>重新检查可用性，满足时先消费 Trigger 当前值，再请求状态切换。</summary>
        /// <returns>条件和来源是否满足；不代表切换一定通过实例的循环预算。</returns>
        internal bool CheckConditionAndSwitch()
        {
            if (!_controller.HasStarted || _controller.IsResettingTriggers)
            {
                return false;
            }

            bool isMeet = IsMet;
            if (isMeet)
            {
                ResetTrigger();
                _controller.SwitchState(_toState, this);
            }
            return isMeet;
        }

        /// <summary>清理所有条件组引用的 Trigger，期间阻止清零通知递归触发过渡。</summary>
        private void ResetTrigger()
        {
            _controller.IsResettingTriggers = true;
            try
            {
                foreach (var item in ConditionGroups)
                {
                    item.ResetTrigger();
                }
            }
            finally
            {
                _controller.IsResettingTriggers = false;
            }
        }

        /// <summary>按 OR 规则检查条件组，任一组满足即可。</summary>
        private bool ConditionGroupMeet()
        {
            foreach (var item in ConditionGroups)
            {
                if (item.IsMet)
                {
                    return true;
                }
            }
            return false;
        }

        #endregion

    }

}
