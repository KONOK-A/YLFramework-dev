using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.Core.RoslynAnalyzers
{

    // Fixed 编码

    /// <summary>
    /// 方法在指定类型、其派生类型或接口实现类型中被直接调用时，YL0003 默认报告 Warning。
    /// 支持开放泛型类型族和精确闭合泛型；多标记原因合并为同一调用点的一条诊断。
    /// 仅当前词法类型参与匹配，不把外层声明归给普通嵌套 helper；null 类型/名单不匹配。
    /// </summary>
    /// <remarks>类型与原因由分析器从 Attribute 实参读取；标记本身不执行运行时检查。</remarks>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class WarnInClassesAttribute : Attribute
    {

        /// <summary>为一个调用方类型层级声明方法调用警告。</summary>
        /// <param name="type">要匹配的类型、基类或接口，可使用开放泛型类型；null 不匹配。</param>
        /// <param name="message">该类型层级命中时，YL0003 附加到诊断的原因。</param>
        public WarnInClassesAttribute(Type type, string message)
        {
        }

        /// <summary>为多个调用方类型层级声明同一项方法调用警告。</summary>
        /// <param name="type">要匹配的类型层级名单；null 名单和 null 项不匹配。</param>
        /// <param name="message">任一类型层级命中时，YL0003 附加到诊断的原因。</param>
        public WarnInClassesAttribute(Type[] type, string message)
        {
        }

    }
}
