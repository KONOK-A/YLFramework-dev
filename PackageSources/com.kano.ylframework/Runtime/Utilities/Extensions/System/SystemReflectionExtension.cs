using System;
using System.Linq;
using System.Reflection;

namespace YLFramework.Extensions
{
    /// <summary>
    /// 反射扩展：创建实例、调用私有方法、Attribute 查询。
    /// 仅做单类型反射，不做程序集扫描；不缓存任何业务实例。
    /// </summary>
    public static class SystemReflectionExtension
    {

        /// <summary>
        /// 通过 Type 创建实例，仅使用公共无参构造函数。
        /// 接口、只有有参构造等找不到公共无参构造时返回 null；
        /// abstract 类型即使声明了公共无参构造，反射调用也会抛 <see cref="MemberAccessException"/>。
        /// 实例无法转换为 T 时也返回 null，但构造过程已经执行。
        /// </summary>
        /// <typeparam name="T">期望返回的类型，可为接口。</typeparam>
        public static T CreateInstance<T>(this Type self) where T : class
        {
            var constructorInfos = self.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
            var ctor = Array.Find(constructorInfos, c => c.GetParameters().Length == 0);
            return ctor != null ? ctor.Invoke(null) as T : null;
        }

        /// <summary>
        /// 调用 <c>typeof(T)</c> 上的非公共实例方法。查找范围：当前类型声明的非公共方法，以及继承的 protected / internal 方法；
        /// 不查找公共方法、不查找基类 private 方法、不支持泛型方法定义。
        /// 方法不存在返回 null；存在重载歧义时由反射抛 <see cref="AmbiguousMatchException"/>；参数不匹配时由反射抛异常。
        /// </summary>
        /// <typeparam name="T">用于查找方法的编译期类型，不使用 self 的运行时类型。</typeparam>
        public static object ReflectionCallPrivateMethod<T>(this T self, string methodName, params object[] args)
        {
            var type = typeof(T);
            var methodInfo = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            return methodInfo?.Invoke(self, args);
        }

        /// <summary>
        /// 调用私有实例方法并转换返回值。方法不存在时结果为 null；
        /// TReturnType 为非可空值类型且结果为 null 时抛 <see cref="NullReferenceException"/>。
        /// </summary>
        /// <typeparam name="T">用于查找方法的编译期类型，不使用 self 的运行时类型。</typeparam>
        /// <typeparam name="TReturnType">方法返回值的类型。</typeparam>
        public static TReturnType ReflectionCallPrivateMethod<T, TReturnType>(this T self, string methodName,
            params object[] args)
        {
            return (TReturnType)self.ReflectionCallPrivateMethod(methodName, args);
        }

        /// <summary>
        /// 检查 Type 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute<T>(this Type type, bool inherit = false) where T : Attribute
        {
            return type.IsDefined(typeof(T), inherit);
        }

        /// <summary>
        /// 检查 Type 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute(this Type type, Type attributeType, bool inherit = false)
        {
            return type.IsDefined(attributeType, inherit);
        }
        /// <summary>
        /// 检查 PropertyInfo 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute<T>(this PropertyInfo prop, bool inherit = false) where T : Attribute
        {
            return prop.IsDefined(typeof(T), inherit);
        }

        /// <summary>
        /// 检查 PropertyInfo 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute(this PropertyInfo prop, Type attributeType, bool inherit = false)
        {
            return prop.IsDefined(attributeType, inherit);
        }
        /// <summary>
        /// 检查 FieldInfo 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute<T>(this FieldInfo field, bool inherit = false) where T : Attribute
        {
            return field.IsDefined(typeof(T), inherit);
        }

        /// <summary>
        /// 检查 FieldInfo 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute(this FieldInfo field, Type attributeType, bool inherit = false)
        {
            return field.IsDefined(attributeType, inherit);
        }

        /// <summary>
        /// 检查 MethodInfo 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute<T>(this MethodInfo method, bool inherit = false) where T : Attribute
        {
            return method.IsDefined(typeof(T), inherit);
        }

        /// <summary>
        /// 检查 MethodInfo 上是否有指定 Attribute。
        /// </summary>
        public static bool HasAttribute(this MethodInfo method, Type attributeType, bool inherit = false)
        {
            return method.IsDefined(attributeType, inherit);
        }


        /// <summary>
        /// 获取 Type 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static T GetAttribute<T>(this Type type, bool inherit = false) where T : Attribute
        {
            return type.GetCustomAttributes<T>(inherit).FirstOrDefault();
        }

        /// <summary>
        /// 获取 Type 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static object GetAttribute(this Type type, Type attributeType, bool inherit = false)
        {
            return type.GetCustomAttributes(attributeType, inherit).FirstOrDefault();
        }

        /// <summary>
        /// 获取 MethodInfo 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static T GetAttribute<T>(this MethodInfo method, bool inherit = false) where T : Attribute
        {
            return method.GetCustomAttributes<T>(inherit).FirstOrDefault();
        }

        /// <summary>
        /// 获取 MethodInfo 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static object GetAttribute(this MethodInfo method, Type attributeType, bool inherit = false)
        {
            return method.GetCustomAttributes(attributeType, inherit).FirstOrDefault();
        }

        /// <summary>
        /// 获取 FieldInfo 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static T GetAttribute<T>(this FieldInfo field, bool inherit = false) where T : Attribute
        {
            return field.GetCustomAttributes<T>(inherit).FirstOrDefault();
        }

        /// <summary>
        /// 获取 FieldInfo 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static object GetAttribute(this FieldInfo field, Type attributeType, bool inherit = false)
        {
            return field.GetCustomAttributes(attributeType, inherit).FirstOrDefault();
        }

        /// <summary>
        /// 获取 PropertyInfo 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static T GetAttribute<T>(this PropertyInfo prop, bool inherit = false) where T : Attribute
        {
            return prop.GetCustomAttributes<T>(inherit).FirstOrDefault();
        }

        /// <summary>
        /// 获取 PropertyInfo 上第一个指定 Attribute；不存在返回 null。
        /// </summary>
        public static object GetAttribute(this PropertyInfo prop, Type attributeType, bool inherit = false)
        {
            return prop.GetCustomAttributes(attributeType, inherit).FirstOrDefault();
        }
    }
}
