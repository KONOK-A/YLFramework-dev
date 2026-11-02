using System;

namespace YLFramework.Attributes
{
    /// <summary>
    /// 为类型提供编辑器中的自定义显示名称。
    /// </summary>
    /// <remarks>
    /// 仅承载元数据，由编辑器代码读取使用（框架内暂无内置消费者）。
    /// 传入 null 或空字符串时按原值返回，调用方应自行回退为类型名。
    /// </remarks>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum,
        AllowMultiple = false,
        Inherited = false)]
    public class CustomClassNameGui : Attribute
    {
        private readonly string _name;

        /// <summary>
        /// 创建显示名称特性。
        /// </summary>
        /// <param name="name">编辑器显示名称，允许 null 或空字符串。</param>
        public CustomClassNameGui(string name)
        {
            _name = name;
        }

        /// <summary>
        /// 获取构造时传入的显示名称，可能为 null 或空字符串。
        /// </summary>
        public string GetName()
        {
            return _name;
        }
    }
}
