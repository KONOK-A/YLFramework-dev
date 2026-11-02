using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.Core.RoslynAnalyzers
{
    // Fixed 编码

    /// <summary>
    /// 限制方法的直接调用者，YL0002 默认报告 Warning；不限制传入 callback 的声明本身。
    /// 调用者可写方法名、Type.Method 或 Namespace.Outer.Type.Method，按声明类型及名称精确匹配。
    /// Lambda/local 是独立调用体，不能借用外层或同名 local 的身份；标记按有效覆写链继承，不叠加实现接口。
    /// </summary>
    /// <remarks>允许名单由分析器从 Attribute 实参读取；构造器不保存名单，也不执行运行时调用检查。</remarks>
    public class RestrictedCallAttribute : Attribute
    {

        /// <summary>
        /// 配置一个允许的直接调用方法；空值不表示任意调用者。
        /// </summary>
        /// <param name="callerMethod">允许的方法选择器；accessor 可使用 get_P/set_P 或限定的 Type.P，构造器可用 .ctor/.cctor。</param>
        public RestrictedCallAttribute(string callerMethod)
        {
        }

        /// <summary>
        /// 配置允许的直接调用方法集合；null/空项被忽略，没有有效项时不允许直接调用。
        /// </summary>
        /// <param name="callerMethods">允许调用的方法</param>
        public RestrictedCallAttribute(string[] callerMethods)
        {
        }
    }

}
