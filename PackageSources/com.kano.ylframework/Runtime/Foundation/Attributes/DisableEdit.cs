using System;
using UnityEngine;

namespace YLFramework.Attributes
{

    /// <summary>
    /// 供 Inspector 绘制器识别的只读显示标记，不限制代码对成员赋值。
    /// </summary>
    /// <remarks>
    /// 实际禁用由 Editor 中的 DisableEditDrawer 对已序列化字段及其子属性执行，特性本身只提供元数据。
    /// 虽然允许标记 C# 属性，但不会使普通属性获得 Unity 序列化资格或自动出现在 Inspector 中。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public class DisableEdit : PropertyAttribute
    {

    }
}
