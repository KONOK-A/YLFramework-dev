using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 轻量、纯 C#、代码优先的依赖注入容器：类型映射、实例注册、解析与成员注入。
    /// <para>解析优先级：已注册实例 → 类型映射（创建）→ 未注册具体类自动构造（仅无名）→ null。</para>
    /// <para>构造函数注入选择参数最多的公开构造函数；最大参数数量并列时抛出异常，不依赖反射返回顺序。</para>
    /// <para>成员注入处理带 <see cref="InjectAttribute"/> 的实例字段与属性，搜索范围由 <see cref="MemberSearchModes"/> 控制。</para>
    /// <para>解析路径中重复出现同一类型会抛出带依赖链的循环依赖异常，不会无限递归。</para>
    /// <para>容器仅保存注册引用，不接管注册实例与解析结果的释放责任，生命周期由调用方管理。</para>
    /// <para>不保证线程安全，按单线程使用。</para>
    /// </summary>
    public class IOCContainer : IIOCContainer
    {
        /// <summary>
        /// 成员注入的搜索模式，控制扫描哪些可见性的实例字段与属性（static / const / 方法 / 事件永远不参与）。
        /// </summary>
        public enum MemberSearchModes
        {
            /// <summary>只扫描公开实例成员（默认）。</summary>
            OnlyPublic,

            /// <summary>只扫描非公开实例成员（private / protected / internal，包含基类私有成员）。</summary>
            OnlyNonPublic,

            /// <summary>同时扫描公开与非公开实例成员。</summary>
            All,
        }

        private readonly MemberSearchModes _memberSearchMode;
        private readonly ResolutionPath _resolutionPath = new ResolutionPath();

        private bool _isInjectingAll;

        private TypeInstanceCollection _instances;
        private TypeMappingCollection _mappings;
        private TypeRelationCollection _relationshipMappings = new TypeRelationCollection();

        /// <summary>
        /// 创建容器。
        /// </summary>
        /// <param name="memberSearchMode">成员注入搜索模式，默认 <see cref="MemberSearchModes.OnlyPublic"/>。</param>
        public IOCContainer(MemberSearchModes memberSearchMode = MemberSearchModes.OnlyPublic)
        {
            _memberSearchMode = memberSearchMode;
        }

        /// <summary>
        /// 类型映射集合（服务类型 + 名称 → 实现类型）。直接操作集合不会经过注册校验。
        /// </summary>
        public TypeMappingCollection Mappings
        {
            get => _mappings ?? (_mappings = new TypeMappingCollection());
            set => _mappings = value;
        }

        /// <summary>
        /// 已注册实例集合。直接操作集合不会经过注册校验；值允许为 null（显式注册的空实例）。
        /// </summary>
        public TypeInstanceCollection Instances
        {
            get => _instances ?? (_instances = new TypeInstanceCollection());
            set => _instances = value;
        }

        /// <summary>
        /// 关系映射集合（(TFor, TBase) → TConcrete）。直接操作集合不会经过注册校验。
        /// </summary>
        public TypeRelationCollection RelationshipMappings
        {
            get => _relationshipMappings;
            set => _relationshipMappings = value;
        }

        /// <summary>
        /// 注册 TSource 到自身的类型映射。
        /// </summary>
        /// <param name="name">注册名称；null 与空字符串等价。同一键重复注册时后注册覆盖先注册。</param>
        public void Register<TSource>(string name = null) => Register(typeof(TSource), typeof(TSource), name);

        /// <summary>
        /// 注册从 TSource 到 TTarget 的类型映射。
        /// </summary>
        /// <param name="name">注册名称；null 与空字符串等价。同一键重复注册时后注册覆盖先注册。</param>
        public void Register<TSource, TTarget>(string name = null) => Register(typeof(TSource), typeof(TTarget), name);

        /// <summary>
        /// 注册类型映射。abstract / interface 目标允许注册（便于先用实例覆盖），但在没有实例可用时解析会报错。
        /// </summary>
        /// <param name="source">服务类型。</param>
        /// <param name="target">目标实现类型，必须可赋值给 <paramref name="source"/>。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> 或 <paramref name="target"/> 为 null。</exception>
        /// <exception cref="IOCRegistrationException"><paramref name="target"/> 不可赋值给 <paramref name="source"/>。</exception>
        public void Register(Type source, Type target, string name = null)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (!source.IsAssignableFrom(target))
            {
                throw new IOCRegistrationException(
                    $"Cannot register mapping {source.FullName} -> {target.FullName}: {target.FullName} is not assignable to {source.FullName}.");
            }

            Mappings[source, name] = target;
        }

        /// <summary>
        /// 注册实例（无名），显式指定是否立即执行成员注入。
        /// 与命名重载区分：两参调用（自动注入）请使用 <see cref="RegisterInstance(Type,object,string,bool)"/> 的可选参数形式。
        /// </summary>
        /// <param name="baseType">服务类型。</param>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        public void RegisterInstance(Type baseType, object instance, bool injectNow) =>
            RegisterInstance(baseType, instance, null, injectNow);

        /// <summary>
        /// 注册命名实例。同一键重复注册时后注册覆盖先注册；null 实例会被记录，解析结果为 null，但 <see cref="IsRegistered(Type,string)"/> 为 true。
        /// 注册写入先于成员注入：injectNow 为 true 且注入失败时注册会保留（已注入成员不回滚）。
        /// </summary>
        /// <param name="baseType">服务类型。</param>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        /// <exception cref="ArgumentNullException"><paramref name="baseType"/> 为 null。</exception>
        /// <exception cref="IOCRegistrationException"><paramref name="instance"/> 非 null 且不可赋值给 <paramref name="baseType"/>。</exception>
        public virtual void RegisterInstance(Type baseType, object instance = null, string name = null, bool injectNow = true)
        {
            if (baseType == null)
            {
                throw new ArgumentNullException(nameof(baseType));
            }

            if (instance != null && !baseType.IsAssignableFrom(instance.GetType()))
            {
                throw new IOCRegistrationException(
                    $"Cannot register instance of {instance.GetType().FullName} for {baseType.FullName}: instance is not assignable to the registered type.");
            }

            Instances[baseType, name] = instance;
            if (injectNow)
            {
                Inject(instance);
            }
        }

        /// <summary>
        /// 注册实例（无名），默认立即执行成员注入。
        /// </summary>
        /// <typeparam name="TBase">服务类型。</typeparam>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        public void RegisterInstance<TBase>(TBase instance) where TBase : class => RegisterInstance<TBase>(instance, true);

        /// <summary>
        /// 注册实例（无名），可指定是否立即执行成员注入。
        /// </summary>
        /// <typeparam name="TBase">服务类型。</typeparam>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        public void RegisterInstance<TBase>(TBase instance, bool injectNow) where TBase : class =>
            RegisterInstance<TBase>(instance, null, injectNow);

        /// <summary>
        /// 注册命名实例，可指定是否立即执行成员注入。
        /// </summary>
        /// <typeparam name="TBase">服务类型。</typeparam>
        /// <param name="instance">实例，可为 null（显式注册空值）。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="injectNow">是否立即执行成员注入。</param>
        public void RegisterInstance<TBase>(TBase instance, string name, bool injectNow = true) where TBase : class =>
            RegisterInstance(typeof(TBase), instance, name, injectNow);

        /// <summary>
        /// 解析对象（泛型），语义与 <see cref="Resolve(Type,string,bool,object[])"/> 完全一致。
        /// </summary>
        /// <typeparam name="T">要解析的服务类型。</typeparam>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="requireInstance">为 true 时只返回已注册实例，不做映射创建与自动构造。</param>
        /// <param name="args">映射或自动构造时传给构造函数的显式参数。</param>
        /// <returns>解析结果；不可用时返回 null。</returns>
        public T Resolve<T>(string name = null, bool requireInstance = false, params object[] args) where T : class
        {
            var result = Resolve(typeof(T), name, requireInstance, args);
            if (result == null)
            {
                return null;
            }

            if (result is T typed)
            {
                return typed;
            }

            throw new IOCResolutionException(
                $"Resolved object of type {result.GetType().FullName} cannot be cast to {typeof(T).FullName}. Check the registration for {typeof(T).FullName}.",
                _resolutionPath.Format(typeof(T)));
        }

        /// <summary>
        /// 解析对象（非泛型）：优先已注册实例，其次类型映射，最后自动构造未注册的具体类（仅无名），均不可用时返回 null。
        /// 自动构造适用于非抽象、非泛型定义、非数组的 class，但不包括 string 与 delegate。
        /// </summary>
        /// <param name="baseType">要解析的服务类型。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <param name="requireInstance">为 true 时只返回已注册实例，不做映射创建与自动构造。</param>
        /// <param name="constructorArgs">映射或自动构造时传给构造函数的显式参数。</param>
        /// <returns>解析结果；不可用时返回 null。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="baseType"/> 为 null。</exception>
        public object Resolve(Type baseType, string name = null, bool requireInstance = false, params object[] constructorArgs)
        {
            if (baseType == null)
            {
                throw new ArgumentNullException(nameof(baseType));
            }

            var normalizedName = NameKey.Normalize(name);

            if (TryGetRegisteredInstance(baseType, normalizedName, out var instance))
            {
                return instance;
            }

            if (requireInstance)
            {
                return null;
            }

            var mapping = Mappings[baseType, normalizedName];
            if (mapping != null)
            {
                return CreateInstance(mapping, constructorArgs);
            }

            if (normalizedName == null && IsAutoCreatable(baseType))
            {
                return CreateInstance(baseType, constructorArgs);
            }

            return null;
        }

        /// <summary>
        /// 按类型创建实例：显式参数优先直接匹配公开构造函数；否则使用参数最多的公开构造函数并递归解析参数；
        /// 没有公开构造函数时回退非公开无参构造。创建完成后对实例执行成员注入。
        /// </summary>
        /// <param name="type">要创建的类型。</param>
        /// <param name="constructorArgs">至少含一个元素时按显式实参匹配构造函数；null 或空数组使用自动构造注入。</param>
        /// <returns>新创建的实例。</returns>
        /// <remarks>构造或注入失败无事务回滚，也不自动释放已创建的对象及其依赖。</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> 为 null。</exception>
        /// <exception cref="IOCResolutionException">类型不可实例化、构造选择歧义、依赖缺失或构造函数抛异常。</exception>
        /// <exception cref="IOCCircularDependencyException">解析路径中出现循环依赖。</exception>
        public object CreateInstance(Type type, params object[] constructorArgs)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            return CreateInstanceTracked(type, constructorArgs);
        }

        /// <summary>
        /// 解析所有匹配的注册（含无名与命名），匹配规则为“注册键类型可赋值给 <typeparamref name="TType"/>”。
        /// 每个键下已注册实例优先于映射，不重复创建；顺序为无名注册在前、命名注册按名称 Ordinal 排序。
        /// </summary>
        /// <typeparam name="TType">要匹配的服务类型。</typeparam>
        /// <returns>匹配的实例序列；无匹配时为空序列。</returns>
        public IEnumerable<TType> ResolveAll<TType>()
        {
            foreach (var obj in ResolveAll(typeof(TType)))
            {
                yield return (TType)obj;
            }
        }

        /// <summary>
        /// 解析所有匹配的注册（含无名与命名），匹配规则为“注册键类型可赋值给 <paramref name="type"/>”。
        /// 每个键下已注册实例优先于映射，不重复创建；顺序为无名注册在前、命名注册按名称 Ordinal 排序（稳定契约）。
        /// 枚举开始时仅对注册键做快照，各项读取处理当时的实例或映射；
        /// 枚举期间注册不会破坏遍历，某项创建失败立即抛出，已产出的项不回滚。
        /// </summary>
        /// <param name="type">要匹配的服务类型。</param>
        /// <returns>匹配的实例序列；无匹配时为空序列。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> 为 null。</exception>
        public IEnumerable<object> ResolveAll(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            // 参数校验必须立即执行；枚举逻辑单独放在迭代器中，快照在枚举开始时生成。
            return ResolveAllIterator(type);
        }

        /// <summary>按注册键快照延迟解析各项；实例值仍实时读取，不按对象引用去重。</summary>
        private IEnumerable<object> ResolveAllIterator(Type type)
        {
            var sortedKeys = CollectResolveAllKeys(type);

            foreach (var key in sortedKeys)
            {
                if (TryGetRegisteredInstance(key.Item1, key.Item2, out var instance))
                {
                    yield return instance;
                    continue;
                }

                var mapping = Mappings[key.Item1, key.Item2];
                if (mapping != null)
                {
                    yield return CreateInstance(mapping);
                }
            }
        }

        /// <summary>
        /// 向指定对象注入带 <see cref="InjectAttribute"/> 的实例字段与属性。
        /// 已注册 null 依赖按 null 写入；未注册的必要依赖抛出 <see cref="IOCInjectionException"/>；
        /// 成员按元数据顺序注入，某个成员失败时之前已注入的成员保持，异常向上传播（无事务回滚）；
        /// 重复调用会覆盖已有值；<paramref name="obj"/> 为 null 时不执行任何操作。
        /// </summary>
        /// <param name="obj">注入目标；值类型对象会因装箱副本无法回写而抛出异常。</param>
        /// <exception cref="IOCInjectionException">成员不可注入或成员依赖无法解析。</exception>
        public void Inject(object obj)
        {
            if (obj == null)
            {
                return;
            }

            var type = obj.GetType();
            if (type.IsValueType)
            {
                throw new IOCInjectionException(
                    $"Cannot inject value type {type.FullName}: a boxed copy would discard the changes. Inject the owning reference type instead.");
            }

            var metadata = MetadataCache.GetInjectionMetadata(type);
            var members = metadata.GetMembers(_memberSearchMode);
            foreach (var member in members)
            {
                InjectMember(obj, type, member);
            }
        }

        /// <summary>
        /// 对所有已注册实例各执行一次成员注入。开始时对实例集合做快照并按引用去重；
        /// 注入过程中新注册的实例不在本轮处理；装箱值类型实例会被跳过（无法回写成员）；
        /// 注入回调中再次调用 <see cref="InjectAll"/> 会被忽略（由外层快照完成本轮注入，避免无限递归）；
        /// 任一实例注入失败立即抛出（不提供回滚）。
        /// </summary>
        /// <exception cref="IOCInjectionException">任一实例注入失败，内部异常保留原始失败原因。</exception>
        public void InjectAll()
        {
            if (_isInjectingAll)
            {
                return;
            }

            _isInjectingAll = true;
            try
            {
                var snapshot = Instances.Values.ToArray();
                var injected = new HashSet<object>(ObjectReferenceComparer.Instance);

                foreach (var instance in snapshot)
                {
                    if (instance == null || instance.GetType().IsValueType || !injected.Add(instance))
                    {
                        continue;
                    }

                    try
                    {
                        Inject(instance);
                    }
                    catch (Exception e)
                    {
                        throw new IOCInjectionException(
                            $"InjectAll failed while injecting instance of {instance.GetType().FullName}: {e.Message}",
                            _resolutionPath.Format(instance.GetType()),
                            e);
                    }
                }
            }
            finally
            {
                _isInjectingAll = false;
            }
        }

        /// <summary>
        /// 清空所有类型映射、实例与关系映射。不会 Dispose 任何已注册实例（对象所有权归调用方），
        /// 也不会清除静态反射元数据缓存；清空后容器可继续注册与解析。
        /// </summary>
        public void Clear()
        {
            Instances.Clear();
            Mappings.Clear();
            RelationshipMappings.Clear();
        }

        /// <summary>
        /// 注册关系映射 (TFor, TBase) → TConcrete，用于按 (主体类型, 基类型) 解析具体实现。
        /// </summary>
        /// <typeparam name="TFor">关系主体类型。</typeparam>
        /// <typeparam name="TBase">关系基类型。</typeparam>
        /// <typeparam name="TConcrete">具体实现类型，必须可赋值给 TBase。</typeparam>
        public void RegisterRelation<TFor, TBase, TConcrete>() =>
            RegisterRelation(typeof(TFor), typeof(TBase), typeof(TConcrete));

        /// <summary>
        /// 注册关系映射；同一键重复注册时后注册覆盖先注册。
        /// </summary>
        /// <param name="tfor">关系主体类型。</param>
        /// <param name="tbase">关系基类型。</param>
        /// <param name="tconcrete">具体实现类型，必须可赋值给 <paramref name="tbase"/>。</param>
        /// <exception cref="ArgumentNullException">任一类型参数为 null。</exception>
        /// <exception cref="IOCRegistrationException"><paramref name="tconcrete"/> 不可赋值给 <paramref name="tbase"/>。</exception>
        public void RegisterRelation(Type tfor, Type tbase, Type tconcrete)
        {
            if (tfor == null)
            {
                throw new ArgumentNullException(nameof(tfor));
            }

            if (tbase == null)
            {
                throw new ArgumentNullException(nameof(tbase));
            }

            if (tconcrete == null)
            {
                throw new ArgumentNullException(nameof(tconcrete));
            }

            if (!tbase.IsAssignableFrom(tconcrete))
            {
                throw new IOCRegistrationException(
                    $"Cannot register relation ({tfor.FullName}, {tbase.FullName}) -> {tconcrete.FullName}: {tconcrete.FullName} is not assignable to {tbase.FullName}.");
            }

            RelationshipMappings[tfor, tbase] = tconcrete;
        }

        /// <summary>
        /// 按 (tfor, tbase) 解析关系映射的具体实现；关系缺失时返回 null。
        /// 具体实现走 <see cref="CreateInstance"/>，因此包含构造注入、成员注入与循环检测。
        /// </summary>
        /// <param name="tfor">关系主体类型。</param>
        /// <param name="tbase">关系基类型。</param>
        /// <param name="args">传给具体实现构造函数的显式参数。</param>
        /// <returns>解析结果；关系缺失时返回 null。</returns>
        /// <exception cref="ArgumentNullException">任一类型参数为 null。</exception>
        public object ResolveRelation(Type tfor, Type tbase, params object[] args)
        {
            if (tfor == null)
            {
                throw new ArgumentNullException(nameof(tfor));
            }

            if (tbase == null)
            {
                throw new ArgumentNullException(nameof(tbase));
            }

            var concreteType = RelationshipMappings[tfor, tbase];
            if (concreteType == null)
            {
                return null;
            }

            return CreateInstance(concreteType, args);
        }

        /// <summary>
        /// 按 <paramref name="tfor"/> + TBase 解析关系映射；关系缺失时返回 default（引用类型为 null）。
        /// </summary>
        /// <typeparam name="TBase">关系基类型。</typeparam>
        /// <param name="tfor">关系主体类型。</param>
        /// <param name="args">传给具体实现构造函数的显式参数。</param>
        /// <returns>解析结果；关系缺失时返回 default。</returns>
        /// <exception cref="IOCResolutionException">解析结果无法转换为 TBase。</exception>
        public TBase ResolveRelation<TBase>(Type tfor, params object[] args)
        {
            var result = ResolveRelation(tfor, typeof(TBase), args);
            if (result == null)
            {
                return default;
            }

            try
            {
                return (TBase)result;
            }
            catch (InvalidCastException e)
            {
                throw new IOCResolutionException(
                    $"Resolved relation for {tfor.Name} cannot be cast to {typeof(TBase).Name}.",
                    _resolutionPath.Format(typeof(TBase)),
                    e);
            }
        }

        /// <summary>
        /// 按 TFor + TBase 解析关系映射；关系缺失时返回 default（引用类型为 null）。
        /// </summary>
        /// <typeparam name="TFor">关系主体类型。</typeparam>
        /// <typeparam name="TBase">关系基类型。</typeparam>
        /// <param name="arg">传给具体实现构造函数的显式参数。</param>
        /// <returns>解析结果；关系缺失时返回 default。</returns>
        public TBase ResolveRelation<TFor, TBase>(params object[] arg) =>
            ResolveRelation<TBase>(typeof(TFor), arg);

        /// <summary>
        /// 判断类型（可带名称）是否已有显式注册（实例或映射）。注意：未注册但可自动构造的具体类不算已注册。
        /// </summary>
        /// <typeparam name="T">要判断的类型。</typeparam>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>已注册返回 true。</returns>
        public bool IsRegistered<T>(string name = null) => IsRegistered(typeof(T), name);

        /// <summary>
        /// 判断类型与名称是否已有显式注册（实例或映射）；显式注册的 null 实例同样视为已注册。
        /// </summary>
        /// <param name="type">要判断的类型。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>已注册返回 true。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> 为 null。</exception>
        public bool IsRegistered(Type type, string name = null)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            return IsRegisteredCore(type, name);
        }

        /// <summary>
        /// 尝试解析（泛型）。语义与 <see cref="Resolve{T}"/> 完全一致（含未注册具体类自动构造），
        /// 仅当解析结果为空时返回 false；创建与注入阶段的真实错误照常抛出，不会被吞掉。
        /// </summary>
        /// <typeparam name="T">要解析的服务类型。</typeparam>
        /// <param name="value">解析结果；失败时为 null。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>解析到非 null 结果返回 true。</returns>
        public bool TryResolve<T>(out T value, string name = null) where T : class
        {
            value = Resolve<T>(name);
            return value != null;
        }

        /// <summary>
        /// 尝试解析（非泛型）。语义与 <see cref="Resolve(Type,string,bool,object[])"/> 一致，
        /// 仅当解析结果为空时返回 false；创建与注入阶段的真实错误照常抛出，不会被吞掉。
        /// </summary>
        /// <param name="type">要解析的服务类型。</param>
        /// <param name="value">解析结果；失败时为 null。</param>
        /// <param name="name">注册名称；null 与空字符串等价。</param>
        /// <returns>解析到非 null 结果返回 true。</returns>
        public bool TryResolve(Type type, out object value, string name = null)
        {
            value = Resolve(type, name);
            return value != null;
        }

        /// <summary>校验可构造类型并跟踪构造与注入路径；无论成功或失败都移除本层类型记录。</summary>
        private object CreateInstanceTracked(Type type, object[] constructorArgs)
        {
            if (type.IsArray)
            {
                throw new IOCResolutionException(
                    $"Array type {type.FullName} cannot be created directly. Register an instance or use ResolveAll for element registrations.",
                    _resolutionPath.Format(type));
            }

            if (type.IsInterface || type.IsAbstract)
            {
                throw new IOCResolutionException(
                    $"Cannot instantiate {type.FullName}: it is an interface or abstract type. Register a concrete mapping or instance.",
                    _resolutionPath.Format(type));
            }

            if (type.IsGenericTypeDefinition)
            {
                throw new IOCResolutionException(
                    $"Cannot instantiate open generic type {type.FullName}. Register a closed generic type instead.",
                    _resolutionPath.Format(type));
            }

            // 循环依赖检测：同一类型在一次解析链中重复构造即判定为循环，避免无限递归。
            if (_resolutionPath.Contains(type))
            {
                throw new IOCCircularDependencyException(_resolutionPath.Format(type));
            }

            _resolutionPath.Push(type);
            try
            {
                var instance = constructorArgs != null && constructorArgs.Length > 0
                    ? CreateWithExplicitArguments(type, constructorArgs)
                    : CreateWithPlan(type, MetadataCache.GetCreationPlan(type));

                Inject(instance);
                return instance;
            }
            finally
            {
                _resolutionPath.Pop();
            }
        }

        /// <summary>用显式实参匹配公开构造函数，将构造失败及常见反射错误转换为带依赖链的异常。</summary>
        private object CreateWithExplicitArguments(Type type, object[] constructorArgs)
        {
            try
            {
                return Activator.CreateInstance(type, constructorArgs);
            }
            catch (TargetInvocationException e)
            {
                throw BuildConstructorFailure(type, e);
            }
            catch (TypeInitializationException e)
            {
                throw new IOCResolutionException(
                    $"Static constructor of {type.FullName} threw an exception: {DescribeException(e.InnerException ?? e)}",
                    _resolutionPath.Format(type),
                    e.InnerException ?? e);
            }
            catch (Exception e) when (e is MissingMethodException || e is ArgumentException || e is MemberAccessException || e is AmbiguousMatchException)
            {
                throw new IOCResolutionException(
                    $"No public constructor of {type.FullName} matches the provided arguments ({DescribeArguments(constructorArgs)}).",
                    _resolutionPath.Format(type),
                    e);
            }
        }

        /// <summary>执行缓存的构造计划：递归解析公开构造参数，或尝试非公开无参构造；无效计划直接报错。</summary>
        private object CreateWithPlan(Type type, CreationPlan plan)
        {
            if (plan.Kind == CreationPlanKind.Invalid)
            {
                throw new IOCResolutionException(plan.ErrorMessage, _resolutionPath.Format(type));
            }

            if (plan.Kind == CreationPlanKind.NonPublicParameterless)
            {
                try
                {
                    return Activator.CreateInstance(type, true);
                }
                catch (TargetInvocationException e)
                {
                    throw BuildConstructorFailure(type, e);
                }
                catch (MissingMethodException e)
                {
                    throw new IOCResolutionException(
                        $"{type.FullName} has no public constructor and no non-public parameterless constructor.",
                        _resolutionPath.Format(type),
                        e);
                }
                catch (MemberAccessException e)
                {
                    throw new IOCResolutionException(
                        $"Cannot create {type.FullName}: {e.Message}",
                        _resolutionPath.Format(type),
                        e);
                }
            }

            var parameters = plan.Parameters;
            var arguments = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                arguments[i] = ResolveConstructorParameter(type, parameters[i]);
            }

            try
            {
                return plan.Constructor.Invoke(arguments);
            }
            catch (TargetInvocationException e)
            {
                throw BuildConstructorFailure(type, e);
            }
        }

        /// <summary>
        /// 整理构造失败异常：解包 TargetInvocationException；若原始异常本身是 IOC 异常（如构造函数体内
        /// 触发的循环依赖或依赖缺失），返回原异常以保留类型与依赖链，由调用方 throw；否则包装为带上下文的解析异常。
        /// </summary>
        private Exception BuildConstructorFailure(Type type, TargetInvocationException exception)
        {
            var original = exception.InnerException ?? exception;
            if (original is IOCException)
            {
                return original;
            }

            return new IOCResolutionException(
                $"Constructor of {type.FullName} threw an exception: {DescribeException(original)}",
                _resolutionPath.Format(type),
                original);
        }

        /// <summary>
        /// 解析单个构造函数参数：数组参数优先使用显式注册（无名或参数名命名），其次按元素类型生成，
        /// params 参数在无显式注册时传空数组，可选数组参数在无匹配元素注册时使用默认值；
        /// 其余参数按“无名显式注册 → 参数名命名注册 → 完整 Resolve（含具体类自动构造）→ 默认值”顺序解析，
        /// 最终失败时抛出带依赖链的异常。显式注册先于自动构造，避免具体类参数的自动构造遮蔽命名注册。
        /// </summary>
        private object ResolveConstructorParameter(Type ownerType, ParameterInfo parameter)
        {
            var parameterType = parameter.ParameterType;

            if (parameterType.IsArray)
            {
                var elementType = parameterType.GetElementType();
                var registeredArray = Resolve(parameterType);
                if (registeredArray != null)
                {
                    return registeredArray;
                }

                if (IsRegisteredCore(parameterType, null))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(parameter.Name) && IsRegisteredCore(parameterType, parameter.Name))
                {
                    return Resolve(parameterType, parameter.Name);
                }

                if (IsParamsParameter(parameter))
                {
                    return Array.CreateInstance(elementType, 0);
                }

                if (parameter.HasDefaultValue && !HasMatchingRegistrations(elementType))
                {
                    return parameter.DefaultValue;
                }

                return BuildArray(elementType);
            }

            if (IsRegisteredCore(parameterType, null))
            {
                return Resolve(parameterType, null);
            }

            if (!string.IsNullOrEmpty(parameter.Name) && IsRegisteredCore(parameterType, parameter.Name))
            {
                return Resolve(parameterType, parameter.Name);
            }

            var value = Resolve(parameterType);
            if (value != null)
            {
                return value;
            }

            if (parameter.HasDefaultValue)
            {
                return parameter.DefaultValue;
            }

            throw new IOCResolutionException(
                $"Failed to resolve {parameterType.Name} for constructor parameter '{parameter.Name}' of {ownerType.Name}. " +
                $"Register a mapping or instance for {parameterType.Name} (name: {FormatName(parameter.Name)}).",
                _resolutionPath.Format(parameterType));
        }

        /// <summary>
        /// 判断是否存在匹配某个元素类型的注册键（只查键，不创建实例），用于可选数组参数决定是否使用默认值。
        /// </summary>
        private bool HasMatchingRegistrations(Type type)
        {
            foreach (var key in Instances.Keys)
            {
                if (type.IsAssignableFrom(key.Item1))
                {
                    return true;
                }
            }

            foreach (var key in Mappings.Keys)
            {
                if (type.IsAssignableFrom(key.Item1))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>识别 params 参数，使无显式数组注册时传入空数组，而非聚合元素注册。</summary>
        private static bool IsParamsParameter(ParameterInfo parameter) =>
            parameter.GetCustomAttribute<ParamArrayAttribute>() != null;

        /// <summary>按 ResolveAll 的注册顺序解析元素，并装入指定元素类型的数组。</summary>
        private Array BuildArray(Type elementType)
        {
            var items = ResolveAll(elementType).ToList();
            var array = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                array.SetValue(items[i], i);
            }

            return array;
        }

        /// <summary>检查成员可注入性，区分缺失依赖与显式注册的 null，再写入字段或属性并整理写入异常。</summary>
        private void InjectMember(object obj, Type ownerType, InjectableMember member)
        {
            if (!member.CanInject)
            {
                throw new IOCInjectionException(
                    $"Cannot inject {member.KindName} {ownerType.Name}.{member.Member.Name}: {member.InvalidReason}.",
                    _resolutionPath.Format(ownerType));
            }

            var value = Resolve(member.MemberType, member.InjectName);
            if (value == null && !IsRegisteredCore(member.MemberType, member.InjectName))
            {
                throw new IOCInjectionException(
                    $"Failed to resolve {member.MemberType.Name} for injected {member.KindName} {ownerType.Name}.{member.Member.Name}. " +
                    $"Register a mapping or instance for {member.MemberType.Name} (name: {FormatName(member.InjectName)}).",
                    _resolutionPath.Format(member.MemberType));
            }

            try
            {
                if (member.Member is FieldInfo field)
                {
                    field.SetValue(obj, value);
                }
                else if (member.Member is PropertyInfo property)
                {
                    property.SetValue(obj, value, null);
                }
            }
            catch (Exception e) when (!(e is IOCException))
            {
                // 反射调用 setter 会把用户异常包进 TargetInvocationException，这里解包以保留原始类型与消息。
                var original = e is TargetInvocationException invocationException && invocationException.InnerException != null
                    ? invocationException.InnerException
                    : e;

                if (original is IOCException)
                {
                    // setter 内部触发的 IOC 错误（如循环依赖）保留原异常类型与依赖链，不降级为普通注入失败。
                    throw original;
                }

                throw new IOCInjectionException(
                    $"Failed to set {member.KindName} {ownerType.Name}.{member.Member.Name}: {DescribeException(original)}",
                    _resolutionPath.Format(ownerType),
                    original);
            }
        }

        /// <summary>只检查类型与名称对应的显式注册键；不构造对象，已登记空实例也算注册。</summary>
        private bool IsRegisteredCore(Type type, string name)
        {
            var key = new Tuple<Type, string>(type, NameKey.Normalize(name));
            return Instances.ContainsKey(key) || Mappings.ContainsKey(key);
        }

        /// <summary>按归一化名称查找实例键，返回值区分未注册与已显式注册 null。</summary>
        private bool TryGetRegisteredInstance(Type type, string name, out object instance)
        {
            var key = new Tuple<Type, string>(type, NameKey.Normalize(name));
            return Instances.TryGetValue(key, out instance);
        }

        /// <summary>从两个注册集合快照中收集可赋值的唯一键并排序，不保存实例值或提前创建对象。</summary>
        private List<Tuple<Type, string>> CollectResolveAllKeys(Type type)
        {
            var keys = new List<Tuple<Type, string>>();
            var seen = new HashSet<Tuple<Type, string>>();
            foreach (var entry in Instances.ToArray())
            {
                if (type.IsAssignableFrom(entry.Key.Item1) && seen.Add(entry.Key))
                {
                    keys.Add(entry.Key);
                }
            }

            foreach (var entry in Mappings.ToArray())
            {
                if (type.IsAssignableFrom(entry.Key.Item1) && seen.Add(entry.Key))
                {
                    keys.Add(entry.Key);
                }
            }

            keys.Sort(CompareResolveAllKeys);
            return keys;
        }

        /// <summary>将无名键排在前面，其余按名称、类型全名依次进行 Ordinal 比较。</summary>
        private static int CompareResolveAllKeys(Tuple<Type, string> a, Tuple<Type, string> b)
        {
            var aUnnamed = string.IsNullOrEmpty(a.Item2);
            var bUnnamed = string.IsNullOrEmpty(b.Item2);
            if (aUnnamed != bUnnamed)
            {
                return aUnnamed ? -1 : 1;
            }

            var nameComparison = string.CompareOrdinal(a.Item2, b.Item2);
            if (nameComparison != 0)
            {
                return nameComparison;
            }

            return string.CompareOrdinal(a.Item1.FullName, b.Item1.FullName);
        }

        /// <summary>限制无名未注册类型的自动构造范围，排除抽象类、开放泛型定义、数组、string 和委托。</summary>
        private static bool IsAutoCreatable(Type type)
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || type.IsArray)
            {
                return false;
            }

            if (type == typeof(string) || typeof(Delegate).IsAssignableFrom(type))
            {
                return false;
            }

            return true;
        }

        /// <summary>为构造或注入诊断组合原始异常的类型名称与消息。</summary>
        private static string DescribeException(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

        /// <summary>描述显式实参的运行时类型及空值，供构造函数匹配失败消息使用。</summary>
        private static string DescribeArguments(object[] arguments)
        {
            if (arguments == null || arguments.Length == 0)
            {
                return "no arguments";
            }

            return string.Join(", ", arguments.Select(argument => argument == null ? "null" : argument.GetType().Name));
        }

        /// <summary>为诊断消息统一显示无名注册，不改变实际注册键。</summary>
        private static string FormatName(string name) => string.IsNullOrEmpty(name) ? "null" : name;
    }
}
