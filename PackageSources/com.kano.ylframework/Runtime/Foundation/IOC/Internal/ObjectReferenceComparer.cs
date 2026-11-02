using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 按引用比较对象的内部比较器，用于 InjectAll 对同一实例去重。
    /// </summary>
    internal sealed class ObjectReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ObjectReferenceComparer Instance = new ObjectReferenceComparer();

        /// <summary>限制比较器实例创建，供所有容器共用无状态的引用比较策略。</summary>
        private ObjectReferenceComparer()
        {
        }

        /// <summary>仅按托管引用身份比较，忽略对象自定义的值相等逻辑。</summary>
        bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

        /// <summary>取得与引用身份匹配的哈希值，不调用对象覆写的 GetHashCode。</summary>
        int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
