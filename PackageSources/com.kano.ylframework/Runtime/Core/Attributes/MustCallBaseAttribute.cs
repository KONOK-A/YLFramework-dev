using System;

namespace YLFramework.Core.RoslynAnalyzers
{

    /// <summary>
    /// 标记虚方法的 base 调用契约；重写链上的有效标记由 YL0001 检查并默认报告 Warning。
    /// 当前方法本体需直接调用同一被覆写槽；Lambda、局部函数、方法组及同名 overload 不满足契约。
    /// 仅证明调用存在，不证明所有控制流执行；抽象基类层级冲突需调整标记或覆写结构。
    /// </summary>
    /// <remarks>标记只供分析器读取，不会在运行时补调基类实现。</remarks>
    public class MustCallBaseAttribute : Attribute
    {

        /// <summary>声明后续覆写须直接调用被覆写的基类方法。</summary>
        public MustCallBaseAttribute()
        {
        }

        /// <summary>声明带诊断原因的基类调用契约。</summary>
        /// <param name="message">YL0001 从 Attribute 元数据读取并附加到诊断的说明。</param>
        public MustCallBaseAttribute(string message)
        {
        }

    }

}
