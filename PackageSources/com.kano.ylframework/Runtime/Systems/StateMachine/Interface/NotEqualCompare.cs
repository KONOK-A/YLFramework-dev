
namespace YLFramework.StateMachine
{
    /// <summary>以浮点值的 Equals 语义判断不相等，缺失参数不视为满足。</summary>
    public class NotEqualCompare : IParameterComparer
    {
        /// <summary>判断有效参数的当前值是否不等于目标。</summary>
        /// <param name="parameter">待比较参数，缺失时不满足。</param>
        /// <param name="v">条件目标值。</param>
        /// <returns>参数存在且值不相等时为 true。</returns>
        public bool Matches(FsmParameterData parameter, float v)
        {
            if (parameter == null)
            {
                return false;
            }

            return !parameter.Value.Equals(v);
        }
    }

}
