using System;
using System.Collections.Generic;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 多键字典：(TFor, TBase) → 具体实现类型。
    /// 同一键重复写入会覆盖旧值。
    /// 属于容器的公开状态，直接操作时不会经过注册校验。
    /// </summary>
    public class TypeRelationCollection : Dictionary<Tuple<Type, Type>, Type>
    {
        /// <summary>
        /// 按 (TFor, TBase) 读写关系映射；键不存在时返回 null。
        /// </summary>
        /// <param name="from">关系主体类型 TFor。</param>
        /// <param name="to">关系基类型 TBase。</param>
        public Type this[Type from, Type to]
        {
            get
            {
                var key = new Tuple<Type, Type>(from, to);
                return TryGetValue(key, out var mapping) ? mapping : null;
            }
            set
            {
                var key = new Tuple<Type, Type>(from, to);
                this[key] = value;
            }
        }
    }
}
