using System;
using System.Collections.Generic;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 多键字典：(服务类型, 名称) → 目标实现类型。
    /// 类型/名称索引器将 null 与空名称视为等价；继承的 Tuple 键入口不归一化名称。
    /// 同一键重复写入会覆盖旧值。
    /// 属于容器的公开状态，直接操作时不会经过注册校验。
    /// </summary>
    public class TypeMappingCollection : Dictionary<Tuple<Type, string>, Type>
    {
        /// <summary>
        /// 按服务类型与名称读写映射；键不存在时返回 null。
        /// </summary>
        /// <param name="from">服务类型（注册源类型）。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        public Type this[Type from, string name = null]
        {
            get
            {
                var key = new Tuple<Type, string>(from, NameKey.Normalize(name));
                return TryGetValue(key, out var mapping) ? mapping : null;
            }
            set
            {
                var key = new Tuple<Type, string>(from, NameKey.Normalize(name));
                this[key] = value;
            }
        }
    }
}
