
namespace YLFramework.StateMachine
{
    /// <summary>使用参数当前值与条件目标值进行比较的策略。</summary>
    public interface IParameterComparer
    {
        /// <summary>判断参数是否满足对应比较，内置实现对缺失参数返回 false。</summary>
        /// <param name="parameter">待比较的运行时参数。</param>
        /// <param name="v">条件目标值。</param>
        /// <returns>参数是否满足比较关系。</returns>
        bool Matches(FsmParameterData parameter, float v);
    }

}
