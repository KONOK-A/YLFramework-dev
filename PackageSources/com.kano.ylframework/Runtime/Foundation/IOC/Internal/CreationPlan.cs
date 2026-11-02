using System.Reflection;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 构造函数选择结果的内部描述（按类型缓存）。
    /// 选择规则：公开实例构造函数中参数最多者；最大参数数量并列视为歧义并携带错误信息；无公开构造函数时回退非公开无参构造。
    /// </summary>
    internal sealed class CreationPlan
    {
        internal CreationPlanKind Kind
        {
            get;
        }

        internal ConstructorInfo Constructor
        {
            get;
        }

        internal ParameterInfo[] Parameters
        {
            get;
        }

        internal string ErrorMessage
        {
            get;
        }

        /// <summary>保存构造选择结果与失败原因，本身不执行构造或依赖解析。</summary>
        private CreationPlan(CreationPlanKind kind, ConstructorInfo constructor, ParameterInfo[] parameters, string errorMessage)
        {
            Kind = kind;
            Constructor = constructor;
            Parameters = parameters;
            ErrorMessage = errorMessage;
        }

        /// <summary>记录已选定的公开构造函数及参数清单，供后续自动注入使用。</summary>
        internal static CreationPlan ForConstructor(ConstructorInfo constructor, ParameterInfo[] parameters) =>
            new CreationPlan(CreationPlanKind.Constructor, constructor, parameters, null);

        /// <summary>记录需尝试非公开无参构造的计划，不在此确认该构造函数是否存在。</summary>
        internal static CreationPlan NonPublicParameterless() =>
            new CreationPlan(CreationPlanKind.NonPublicParameterless, null, null, null);

        /// <summary>缓存无法自动选择构造函数的原因，实际创建时再报告失败。</summary>
        internal static CreationPlan Invalid(string errorMessage) =>
            new CreationPlan(CreationPlanKind.Invalid, null, null, errorMessage);
    }

    /// <summary>
    /// 构造计划的类别。
    /// </summary>
    internal enum CreationPlanKind
    {
        /// <summary>使用选定的公开构造函数并自动解析参数。</summary>
        Constructor,

        /// <summary>无公开构造函数，回退到非公开无参构造函数。</summary>
        NonPublicParameterless,

        /// <summary>无法确定构造函数（例如同参数数量并列），<see cref="CreationPlan.ErrorMessage"/> 包含原因。</summary>
        Invalid,
    }
}
