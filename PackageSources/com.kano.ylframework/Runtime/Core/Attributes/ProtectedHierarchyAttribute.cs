using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Fixed 编码

namespace YLFramework.Core.RoslynAnalyzers
{

    /// <summary>
    /// 保护类型实例的 field/property/indexer，YL0006 默认对指定禁止类型及其基类/接口层级中的写入报告 Warning。
    /// 包含赋值、复合赋值、自增减、解构、ref/out 及可写 ref 暴露；读取、in/ref readonly 与 static 成员不报。
    /// 结构体值链回查外层保护成员，引用对象内部 mutation 不等于外层写；保护类型本身及派生实现保留修改权。
    /// 不追可写 ref 别名后续使用或任意被调副作用，不提供运行时访问控制。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public class ProtectedHierarchyAttribute : Attribute
    {

        /// <summary>
        /// 禁止写入的调用方类型名单；YL0006 沿调用方的基类与接口层级匹配这些类型。
        /// </summary>
        /// <remarks>保留构造时传入的数组引用，不复制名单，也不在运行时拦截修改。</remarks>
        public Type[] ForbiddenModifiers
        {
            get;
        }

        /// <summary>保存作为禁止调用方层级起点的类型名单。</summary>
        /// <param name="forbiddenModifiers">禁止写入的类型、基类或接口；分析器忽略 null 名单和 null 项。</param>
        public ProtectedHierarchyAttribute(params Type[] forbiddenModifiers)
        {
            ForbiddenModifiers = forbiddenModifiers;
        }



    }

}
