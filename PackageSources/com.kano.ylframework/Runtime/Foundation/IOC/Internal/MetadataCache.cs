using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 静态反射元数据缓存：类型 → 注入成员清单 / 构造计划。
    /// 只缓存纯静态元数据，不缓存容器实例与解析出的对象；<see cref="IOCContainer.Clear"/> 不影响本缓存。
    /// 与容器一致按单线程使用，未做并发保护。
    /// </summary>
    internal static class MetadataCache
    {
        private static readonly Dictionary<Type, InjectionMetadata> _injectionMetadataByType =
            new Dictionary<Type, InjectionMetadata>();

        private static readonly Dictionary<Type, CreationPlan> _creationPlansByType =
            new Dictionary<Type, CreationPlan>();

        /// <summary>取得类型的成员注入元数据，首次访问才扫描并保存完整继承链结果。</summary>
        internal static InjectionMetadata GetInjectionMetadata(Type type)
        {
            if (!_injectionMetadataByType.TryGetValue(type, out var metadata))
            {
                metadata = BuildInjectionMetadata(type);
                _injectionMetadataByType[type] = metadata;
            }

            return metadata;
        }

        /// <summary>取得类型的自动构造计划，歧义等无效结果也缓存，留到创建时报告。</summary>
        internal static CreationPlan GetCreationPlan(Type type)
        {
            if (!_creationPlansByType.TryGetValue(type, out var plan))
            {
                plan = BuildCreationPlan(type);
                _creationPlansByType[type] = plan;
            }

            return plan;
        }

        /// <summary>
        /// 扫描完整继承链上的实例字段与属性，收集带 InjectAttribute 的成员（含不可注入成员，供注入时报错）。
        /// 使用 DeclaredOnly + 手动层级遍历以保证基类私有成员可见且顺序确定；特性查找不沿继承链重复（inherit: false）。
        /// </summary>
        private static InjectionMetadata BuildInjectionMetadata(Type type)
        {
            var members = new List<InjectableMember>();
            var hierarchy = new List<Type>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                hierarchy.Add(current);
            }

            hierarchy.Reverse();

            foreach (var level in hierarchy)
            {
                var fields = level.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
                foreach (var field in fields)
                {
                    var attribute = field.GetCustomAttributes(typeof(InjectAttribute), false).FirstOrDefault() as InjectAttribute;
                    if (attribute == null)
                    {
                        continue;
                    }

                    var invalidReason = field.IsLiteral
                        ? "const fields cannot be injected"
                        : field.IsInitOnly
                            ? "readonly fields cannot be injected"
                            : null;

                    members.Add(new InjectableMember
                    {
                        Member = field,
                        MemberType = field.FieldType,
                        InjectName = NameKey.Normalize(attribute.Name),
                        IsPublic = field.IsPublic,
                        CanInject = invalidReason == null,
                        InvalidReason = invalidReason,
                    });
                }

                var properties = level.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Array.Sort(properties, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
                foreach (var property in properties)
                {
                    var attribute = property.GetCustomAttributes(typeof(InjectAttribute), false).FirstOrDefault() as InjectAttribute;
                    if (attribute == null)
                    {
                        continue;
                    }

                    string invalidReason = null;
                    if (property.GetIndexParameters().Length > 0)
                    {
                        invalidReason = "indexer properties cannot be injected";
                    }
                    else if (property.GetSetMethod(true) == null)
                    {
                        invalidReason = "properties without a setter cannot be injected";
                    }

                    var getter = property.GetMethod;
                    var setter = property.SetMethod;
                    var isPublic = (getter != null && getter.IsPublic) || (setter != null && setter.IsPublic);

                    members.Add(new InjectableMember
                    {
                        Member = property,
                        MemberType = property.PropertyType,
                        InjectName = NameKey.Normalize(attribute.Name),
                        IsPublic = isPublic,
                        CanInject = invalidReason == null,
                        InvalidReason = invalidReason,
                    });
                }
            }

            return new InjectionMetadata(members.ToArray());
        }

        /// <summary>
        /// 选择构造函数：公开构造函数中参数最多者；并列视为歧义（不依赖反射返回顺序）；无公开构造函数则回退非公开无参构造。
        /// </summary>
        private static CreationPlan BuildCreationPlan(Type type)
        {
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (constructors.Length == 0)
            {
                return CreationPlan.NonPublicParameterless();
            }

            var maxParameterCount = constructors.Max(constructor => constructor.GetParameters().Length);
            var candidates = constructors.Where(constructor => constructor.GetParameters().Length == maxParameterCount).ToArray();
            if (candidates.Length > 1)
            {
                return CreationPlan.Invalid(
                    $"Type {type.FullName} has {candidates.Length} public constructors with {maxParameterCount} parameter(s) " +
                    $"({string.Join(", ", candidates.Select(DescribeSignature))}); cannot choose automatically. " +
                    "Provide explicit arguments via CreateInstance(type, args) or keep a single constructor with the maximum parameter count.");
            }

            var selected = candidates[0];
            return CreationPlan.ForConstructor(selected, selected.GetParameters());
        }

        /// <summary>为构造函数选择歧义消息生成类型名及参数类型清单。</summary>
        private static string DescribeSignature(ConstructorInfo constructor) =>
            constructor.DeclaringType != null
                ? $"{constructor.DeclaringType.Name}({string.Join(", ", constructor.GetParameters().Select(parameter => parameter.ParameterType.Name))})"
                : constructor.ToString();
    }
}
