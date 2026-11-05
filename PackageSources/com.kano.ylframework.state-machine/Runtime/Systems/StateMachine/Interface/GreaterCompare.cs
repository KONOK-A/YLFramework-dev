
namespace YLFramework.StateMachine
{
    /// <summary>使用浮点值的 CompareTo 结果判断参数严格大于目标。</summary>
    public class GreaterCompare : IParameterComparer
    {
        /// <summary>判断有效参数的当前值是否严格大于目标。</summary>
        /// <param name="parameter">待比较参数，缺失时不满足。</param>
        /// <param name="v">条件目标值。</param>
        /// <returns>参数存在且比较结果为大于时为 true。</returns>
        public bool Matches(FsmParameterData parameter, float v)
        {
            if (parameter == null)
            {
                return false;
            }

            return parameter.Value.CompareTo(v) == 1;
        }
    }

}
