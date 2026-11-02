using System;
using System.Collections.Generic;

namespace YLFramework.Foundation
{
    /// <summary>
    /// 按类型完整名称在当前 AppDomain 已加载的程序集中查找 <see cref="Type"/>。
    /// </summary>
    public static class AssemblyTool
    {
        // 仅记录成功结果；失败不缓存，避免新程序集加载后命中永久失效的 null。
        private static readonly Dictionary<string, Type> _typesByName = new(StringComparer.Ordinal);

        /// <summary>
        /// 按类型完整名称查找类型，成功结果会被缓存。
        /// </summary>
        /// <param name="classFullName">
        /// 类型完整名称（含命名空间）；嵌套类型以 '+' 分隔，泛型定义使用反引号形式（如 System.Collections.Generic.List`1）。
        /// </param>
        /// <returns>找到的第一个匹配类型；传入 null / 空白或未找到时返回 null。</returns>
        /// <remarks>
        /// 遍历顺序为 AppDomain.CurrentDomain.GetAssemblies() 的返回顺序；多个程序集存在同名类型时返回第一个匹配项。
        /// 只接受类型完整名称，不保证支持程序集限定名（assembly-qualified name）；畸形名称可能由 Assembly.GetType 抛出异常。
        /// 仅缓存成功结果，查找失败不会污染缓存；非线程安全，按 Unity 主线程使用。
        /// </remarks>
        public static Type GetType(string classFullName)
        {
            if (string.IsNullOrWhiteSpace(classFullName))
            {
                return null;
            }

            if (_typesByName.TryGetValue(classFullName, out var cached) && cached != null)
            {
                return cached;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                var type = assembly.GetType(classFullName);
                if (type == null)
                {
                    continue;
                }

                _typesByName[classFullName] = type;
                return type;
            }

            return null;
        }
    }
}
