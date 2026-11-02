using System;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 通用对象扩展：链式回调、托管判空与引用转换。
    /// IsNull / IsNotNull 只检查托管引用，不能判断 UnityEngine.Object 的 fake-null（已销毁对象）。
    /// </summary>
    public static class SystemObjectExtension
    {
        /// <summary>
        /// 把自身传入 Action 后返回自身，用于插入副作用；onDo 为 null 时直接返回自身。
        /// </summary>
        public static T Self<T>(this T self, Action<T> onDo)
        {
            onDo?.Invoke(self);
            return self;
        }

        /// <summary>
        /// 把自身传入 Func&lt;T,T&gt; 并返回其结果；onDo 为 null 会抛 <see cref="NullReferenceException"/>。
        /// </summary>
        public static T Self<T>(this T self, Func<T, T> onDo)
        {
            return onDo.Invoke(self);
        }

        /// <summary>
        /// 判断托管引用是否为 null。对已销毁的 UnityEngine.Object 会返回 false，
        /// 需要 Unity fake-null 语义时请直接使用 <c>obj == null</c>。
        /// </summary>
        public static bool IsNull<T>(this T selfObj) where T : class
        {
            return null == selfObj;
        }

        /// <summary>
        /// 判断托管引用是否非 null。对已销毁的 UnityEngine.Object 会返回 true，
        /// 需要 Unity fake-null 语义时请直接使用 <c>obj != null</c>。
        /// </summary>
        public static bool IsNotNull<T>(this T selfObj) where T : class
        {
            return null != selfObj;
        }

        /// <summary>
        /// 引用转换：转换失败返回 null（等价于 <c>as</c>）。
        /// </summary>
        public static T As<T>(this object selfObj) where T : class
        {
            return selfObj as T;
        }
    }
}
