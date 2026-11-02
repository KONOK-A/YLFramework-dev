using System;

namespace YLFramework.Binding
{
    /// <summary>
    /// 外部来源只读属性：值始终由构造时传入的 getter 决定。
    /// 读取 Value 时会调用 getter 并把结果缓存到内部字段；通过 Value 赋值写入的值会在下一次读取时被 getter 覆盖，
    /// 因此写入主要用于“比较后触发通知”，不能改变最终值。
    /// 适用于把外部数据（配置、时间、第三方状态）适配成可绑定属性。
    /// </summary>
    /// <typeparam name="T">属性值类型。</typeparam>
    [Serializable]
    public class CustomProperty<T> : BindableProperty<T>
    {
        /// <summary>
        /// 创建由 getter 提供值的属性。
        /// </summary>
        /// <param name="valueGetter">取值委托，必须非 null；否则首次读取时会抛出 NullReferenceException。</param>
        public CustomProperty(Func<T> valueGetter)
        {
            _valueGetter = valueGetter;
        }

        private Func<T> _valueGetter;

        /// <summary>
        /// 调用 getter 取值并更新内部缓存；getter 抛出异常时异常向上传播。
        /// </summary>
        protected override T GetValue()
        {
            StoredValue = _valueGetter.Invoke();
            return StoredValue;
        }
    }
}
