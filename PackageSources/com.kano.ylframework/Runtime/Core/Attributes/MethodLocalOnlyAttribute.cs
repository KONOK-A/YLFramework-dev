using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.Core.RoslynAnalyzers
{
    /// <summary>
    /// 标记只可在方法借用窗口使用的类型，YL0005 默认对显式 field/property 的持有报告 Warning。
    /// 检查数组、已知真实值容器及自有泛型实例值槽，不将签名、delegate/event、phantom 或 static-only wrapper 当持有。
    /// 参数/返回/局部/typeof 不做逃逸分析；仅实际 Core 参数池与 request owner 成员有窄形态豁免。
    /// </summary>
    /// <remarks>标记本身不管理借用期限、回收或引用失效；这些仍由类型实现及其调用方负责。</remarks>
    public class MethodLocalOnlyAttribute : Attribute
    {

        /// <summary>声明类型仅供方法借用窗口使用，不在构造时执行持有检查。</summary>
        public MethodLocalOnlyAttribute()
        {
        }

        /// <summary>声明附带说明文本的借用标记。</summary>
        /// <param name="message">写入 Attribute 元数据的说明；当前 YL0005 诊断不读取此文本。</param>
        public MethodLocalOnlyAttribute(string message)
        {
        }

    }
}
