
namespace YLFramework.StateMachine
{

    /// <summary>参数当前值相对目标值的比较方式。</summary>
    public enum CompareType
    {

        /// <summary>严格大于目标值。</summary>
        Greater = 0,
        /// <summary>严格小于目标值。</summary>
        Less,
        /// <summary>以浮点 Equals 语义等于目标值。</summary>
        Equal,
        /// <summary>以浮点 Equals 语义不等于目标值。</summary>
        NotEqual,

    }

    /// <summary>以参数名称关联运行时值的单个比较条件定义。</summary>
    [System.Serializable]
    public class FsmConditionData
    {

        /// <summary>比较右侧的目标值。</summary>
        public float targetValue;

        /// <summary>引用的配置参数名称。</summary>
        public string parameterName;

        /// <summary>参数值相对目标值采用的比较关系。</summary>
        public CompareType compareType;

    }

}
