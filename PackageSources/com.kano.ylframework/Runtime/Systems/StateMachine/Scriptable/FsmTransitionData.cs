using System.Collections.Generic;
using UnityEngine.Serialization;

namespace YLFramework.StateMachine
{

    /// <summary>可序列化的附加条件组，组内条件按 AND 组合。</summary>
    [System.Serializable]
    public class GroupCondition
    {
        /// <summary>此组要求同时满足的条件。</summary>
        public List<FsmConditionData> conditions = new List<FsmConditionData>();
    }

    /// <summary>同父层状态之间的过渡定义，基础条件组与附加组之间按 OR 组合。</summary>
    [System.Serializable]
    public class FsmTransitionData
    {

        /// <summary>来源节点的全局唯一名称，也可引用同父层 Any State 内置节点。</summary>
        public string fromStateName;
        /// <summary>目标节点的全局唯一名称。</summary>
        public string toStateName;

        /// <summary>以来源与目标名称拼接的过渡查找键。</summary>
        public string Key
        {
            get
            {
                return string.Format("{0}:{1}", fromStateName, toStateName);
            }
        }


        /// <summary>基础条件组，组内各条件必须同时满足。</summary>
        public List<FsmConditionData> conditions = new List<FsmConditionData>();

        /// <summary>与基础组互为 OR 的附加条件组。</summary>
        [FormerlySerializedAs("group_conditions")] public List<GroupCondition> groupConditions = new List<GroupCondition>();

        /// <summary>整条过渡没有任何条件时允许自动切换；有条件时不替代条件求值。</summary>
        [FormerlySerializedAs("AutoSwtich")] public bool autoSwitch = false;

        /// <summary>基础组及所有附加组均不含条件时为 true。</summary>
        public bool IsEmpty
        {
            get
            {

                if (conditions.Count != 0)
                {
                    return false;
                }

                foreach (GroupCondition condition in groupConditions)
                {
                    if (condition.conditions.Count != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

    }

}
