using System;
using System.Collections.Generic;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 类型/实例注册、解析与成员注入的容器契约。
    /// 解析优先级、冲突覆盖、循环检测与对象所有权等具体语义以 <see cref="IOCContainer"/> 的文档为准。
    /// </summary>
    /// <remarks>容器不接管对象的释放责任；解析中的构造与注入错误会抛出，不统一转为 null。</remarks>
    public interface IIOCContainer
    {
        /// <summary>
        /// 清空所有类型映射、实例与关系映射；不会 Dispose 已注册实例，清空后可继续注册与解析。
        /// </summary>
        void Clear();

        /// <summary>
        /// 向目标对象注入带 <see cref="InjectAttribute"/> 的实例字段与属性。
        /// </summary>
        /// <param name="obj">注入目标；null 时不执行任何操作。</param>
        void Inject(object obj);

        /// <summary>
        /// 对已注册的非空引用类型实例各执行一次成员注入（快照 + 按引用去重）。
        /// </summary>
        void InjectAll();

        /// <summary>
        /// 注册 TSource 到自身的类型映射。
        /// </summary>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        void Register<TSource>(string name = null);

        /// <summary>
        /// 注册从 TSource 到 TTarget 的类型映射。
        /// </summary>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        void Register<TSource, TTarget>(string name = null);

        /// <summary>
        /// 注册类型映射；同一键重复注册时后注册覆盖先注册。
        /// </summary>
        /// <param name="source">服务类型。</param>
        /// <param name="target">目标实现类型，必须可赋值给 <paramref name="source"/>。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        void Register(Type source, Type target, string name = null);

        /// <summary>
        /// 注册关系映射 (TFor, TBase) → TConcrete。
        /// </summary>
        /// <typeparam name="TFor">关系主体类型。</typeparam>
        /// <typeparam name="TBase">关系基类型。</typeparam>
        /// <typeparam name="TConcrete">具体实现类型，必须可赋值给 TBase。</typeparam>
        void RegisterRelation<TFor, TBase, TConcrete>();

        /// <summary>
        /// 注册关系映射；同一键重复注册时后注册覆盖先注册。
        /// </summary>
        /// <param name="tfor">关系主体类型。</param>
        /// <param name="tbase">关系基类型。</param>
        /// <param name="tconcrete">具体实现类型，必须可赋值给 <paramref name="tbase"/>。</param>
        void RegisterRelation(Type tfor, Type tbase, Type tconcrete);

        /// <summary>
        /// 注册实例（无名），显式指定是否立即执行成员注入。
        /// </summary>
        /// <param name="baseType">服务类型。</param>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        void RegisterInstance(Type baseType, object instance, bool injectNow);

        /// <summary>
        /// 注册命名实例，默认立即执行成员注入。
        /// </summary>
        /// <param name="baseType">服务类型。</param>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        void RegisterInstance(Type baseType, object instance = null, string name = null, bool injectNow = true);

        /// <summary>
        /// 注册实例（无名），默认立即执行成员注入。
        /// </summary>
        /// <typeparam name="TBase">服务类型。</typeparam>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        void RegisterInstance<TBase>(TBase instance) where TBase : class;

        /// <summary>
        /// 注册实例（无名），可指定是否立即执行成员注入。
        /// </summary>
        /// <typeparam name="TBase">服务类型。</typeparam>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        void RegisterInstance<TBase>(TBase instance, bool injectNow) where TBase : class;

        /// <summary>
        /// 注册命名实例，可指定是否立即执行成员注入。
        /// </summary>
        /// <typeparam name="TBase">服务类型。</typeparam>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        void RegisterInstance<TBase>(TBase instance, string name, bool injectNow = true) where TBase : class;

        /// <summary>
        /// 解析对象（泛型）：优先已注册实例，其次类型映射，最后自动构造未注册的具体类（仅无名），不可用时返回 null。
        /// </summary>
        /// <typeparam name="T">要解析的服务类型。</typeparam>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="requireInstance">为 true 时只返回已注册实例。</param>
        /// <param name="args">映射或自动构造时传给构造函数的显式参数。</param>
        /// <returns>解析结果；不可用时返回 null。</returns>
        T Resolve<T>(string name = null, bool requireInstance = false, params object[] args) where T : class;

        /// <summary>
        /// 解析对象（非泛型）：优先已注册实例，其次类型映射，最后自动构造未注册的具体类（仅无名），不可用时返回 null。
        /// </summary>
        /// <param name="baseType">要解析的服务类型。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="requireInstance">为 true 时只返回已注册实例。</param>
        /// <param name="constructorArgs">映射或自动构造时传给构造函数的显式参数。</param>
        /// <returns>解析结果；不可用时返回 null。</returns>
        object Resolve(Type baseType, string name = null, bool requireInstance = false, params object[] constructorArgs);

        /// <summary>
        /// 按类型创建实例（自动解析构造函数参数并执行成员注入）。
        /// </summary>
        /// <param name="type">要创建的类型。</param>
        /// <param name="args">至少含一个元素时按显式实参匹配构造函数；null 或空数组使用自动构造注入。</param>
        /// <returns>新创建的实例。</returns>
        object CreateInstance(Type type, params object[] args);

        /// <summary>
        /// 解析所有匹配的注册（含无名与命名）。
        /// </summary>
        /// <typeparam name="TType">要匹配的服务类型。</typeparam>
        /// <returns>匹配的实例序列；无匹配时为空序列。</returns>
        IEnumerable<TType> ResolveAll<TType>();

        /// <summary>
        /// 解析所有匹配的注册（含无名与命名）。
        /// </summary>
        /// <param name="type">要匹配的服务类型。</param>
        /// <returns>匹配的实例序列；无匹配时为空序列。</returns>
        IEnumerable<object> ResolveAll(Type type);

        /// <summary>
        /// 按 (tfor, tbase) 解析关系映射；关系缺失时返回 null。
        /// </summary>
        /// <param name="tfor">关系主体类型。</param>
        /// <param name="tbase">关系基类型。</param>
        /// <param name="arg">传给具体实现构造函数的显式参数。</param>
        /// <returns>解析结果；关系缺失时返回 null。</returns>
        object ResolveRelation(Type tfor, Type tbase, params object[] arg);

        /// <summary>
        /// 按 tfor + TBase 解析关系映射；关系缺失时返回 default。
        /// </summary>
        /// <typeparam name="TBase">关系基类型。</typeparam>
        /// <param name="tfor">关系主体类型。</param>
        /// <param name="arg">传给具体实现构造函数的显式参数。</param>
        /// <returns>解析结果；关系缺失时返回 default。</returns>
        TBase ResolveRelation<TBase>(Type tfor, params object[] arg);

        /// <summary>
        /// 按 TFor + TBase 解析关系映射；关系缺失时返回 default。
        /// </summary>
        /// <typeparam name="TFor">关系主体类型。</typeparam>
        /// <typeparam name="TBase">关系基类型。</typeparam>
        /// <param name="arg">传给具体实现构造函数的显式参数。</param>
        /// <returns>解析结果；关系缺失时返回 default。</returns>
        TBase ResolveRelation<TFor, TBase>(params object[] arg);

        /// <summary>
        /// 判断类型（可带名称）是否已有显式注册（实例或映射）。
        /// </summary>
        /// <typeparam name="T">要判断的类型。</typeparam>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>已注册返回 true。</returns>
        bool IsRegistered<T>(string name = null);

        /// <summary>
        /// 判断类型与名称是否已有显式注册（实例或映射）。
        /// </summary>
        /// <param name="type">要判断的类型。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>已注册返回 true。</returns>
        bool IsRegistered(Type type, string name = null);

        /// <summary>
        /// 尝试解析（泛型）；仅当解析结果为空时返回 false，创建与注入阶段的真实错误照常抛出。
        /// </summary>
        /// <typeparam name="T">要解析的服务类型。</typeparam>
        /// <param name="value">解析结果；失败时为 null。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>解析到非 null 结果返回 true。</returns>
        bool TryResolve<T>(out T value, string name = null) where T : class;

        /// <summary>
        /// 尝试解析（非泛型）；仅当解析结果为空时返回 false，创建与注入阶段的真实错误照常抛出。
        /// </summary>
        /// <param name="type">要解析的服务类型。</param>
        /// <param name="value">解析结果；失败时为 null。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>解析到非 null 结果返回 true。</returns>
        bool TryResolve(Type type, out object value, string name = null);

        /// <summary>
        /// 类型映射集合（服务类型 + 名称 → 实现类型）。
        /// </summary>
        TypeMappingCollection Mappings
        {
            get; set;
        }

        /// <summary>
        /// 已注册实例集合。
        /// </summary>
        TypeInstanceCollection Instances
        {
            get; set;
        }

        /// <summary>
        /// 关系映射集合（(TFor, TBase) → TConcrete）。
        /// </summary>
        TypeRelationCollection RelationshipMappings
        {
            get; set;
        }
    }
}
