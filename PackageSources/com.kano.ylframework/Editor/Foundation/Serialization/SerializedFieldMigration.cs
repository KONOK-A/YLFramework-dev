using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Foundation
{
    /// <summary>显式修复 YL 字段改名后的 Prefab override 路径，不自动导入或保存资产。</summary>
    public static class SerializedFieldMigration
    {
        /// <summary>在调用方加载的 Prefab 内容上迁移旧路径；冲突时不写入，保存仍由调用方负责。</summary>
        /// <param name="prefab">已加载的 Prefab 对象，必须在 EditMode 下调用且对象仍然存活。</param>
        /// <returns>是否替换了旧路径或合并了指向同一字段的等值覆盖记录。</returns>
        public static bool MigratePrefabOverrides(GameObject prefab)
        {
            return MigratePrefabOverrides(prefab, _ => true);
        }

        /// <summary>仅迁移筛选通过的目标，先完成路径解析和冲突检查，再一次性写回覆盖记录。</summary>
        internal static bool MigratePrefabOverrides(GameObject prefab, Predicate<Object> include)
        {
            if (!prefab)
            {
                throw new ArgumentNullException(nameof(prefab));
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Prefab field migration requires EditMode.");
            }
            var modifications = PrefabUtility.GetPropertyModifications(prefab);
            if (modifications == null)
            {
                return false;
            }
            var updated = new List<PropertyModification>(modifications.Length);
            var changed = false;
            foreach (var modification in modifications)
            {
                var path = modification.target && include(modification.target)
                    ? ResolvePath(modification.target, modification.propertyPath) : null;
                if (path == null)
                {
                    updated.Add(modification);
                    continue;
                }
                var existing = modifications.FirstOrDefault(candidate => candidate.target == modification.target && candidate.propertyPath == path)
                    ?? updated.FirstOrDefault(candidate => candidate.target == modification.target && candidate.propertyPath == path);
                if (existing != null && (existing.value != modification.value || existing.objectReference != modification.objectReference))
                {
                    throw new InvalidOperationException("Conflicting Prefab field overrides: " + path);
                }
                changed = true;
                if (existing == null)
                {
                    updated.Add(new PropertyModification
                    {
                        target = modification.target,
                        propertyPath = path,
                        value = modification.value,
                        objectReference = modification.objectReference
                    });
                }
            }
            if (changed)
            {
                PrefabUtility.SetPropertyModifications(prefab, updated.ToArray());
            }
            return changed;
        }

        /// <summary>沿字段和数组路径解析 YL 程序集声明的旧字段别名，拒绝不可序列化或无法匹配的路径。</summary>
        /// <returns>校验通过且确有改名的路径；未变化、字段不适用或路径无效时返回 null。</returns>
        private static string ResolvePath(Object target, string path)
        {
            var parts = path.Split('.');
            var type = target.GetType();
            var changed = false;
            for (var index = 0; index < parts.Length; index++)
            {
                if (parts[index] == "Array" && index + 1 < parts.Length)
                {
                    var item = parts[++index];
                    if (type.IsArray && type.GetArrayRank() == 1)
                    {
                        type = type.GetElementType();
                    }
                    else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        type = type.GetGenericArguments()[0];
                    }
                    else
                    {
                        return null;
                    }
                    if (item == "size" && index == parts.Length - 1)
                    {
                        break;
                    }
                    if (!item.StartsWith("data[", StringComparison.Ordinal) || !item.EndsWith("]", StringComparison.Ordinal) ||
                        !int.TryParse(item.Substring(5, item.Length - 6), out var element) || element < 0)
                    {
                        return null;
                    }
                    continue;
                }
                FieldInfo field = null;
                for (var owner = type; owner != null && field == null; owner = owner.BaseType)
                {
                    var fields = owner.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    field = fields.FirstOrDefault(candidate => candidate.Name == parts[index]);
                    if (field == null && owner.Assembly.GetName().Name is "YLFramework.Runtime" or "YLFramework.Editor")
                    {
                        field = fields.SingleOrDefault(candidate => candidate.GetCustomAttributes<FormerlySerializedAsAttribute>()
                            .Any(alias => alias.oldName == parts[index]));
                    }
                }
                if (field == null || field.IsStatic || field.IsInitOnly || field.IsNotSerialized ||
                    (!field.IsPublic && !field.IsDefined(typeof(SerializeField), false) &&
                        !field.IsDefined(typeof(SerializeReference), false)))
                {
                    return null;
                }
                changed |= field.Name != parts[index];
                parts[index] = field.Name;
                type = field.FieldType;
            }
            if (!changed)
            {
                return null;
            }
            var result = string.Join(".", parts);
            using var serialized = new SerializedObject(target);
            // 源集合可为空；Variant 的新增元素按字段类型校验，不以源对象的数组长度拒绝。
            var array = result.IndexOf(".Array.", StringComparison.Ordinal);
            var property = serialized.FindProperty(array < 0 ? result : result.Substring(0, array));
            return property != null && (array < 0 || property.isArray) ? result : null;
        }
    }
}
