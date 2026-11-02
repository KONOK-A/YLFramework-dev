using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;
using YLFramework.Excel;

namespace YLFramework.Integrations.Excel.Editor
{
    /// <summary>在列协议与模型字段之间转换值，解析稳定行键并生成原字段状态快照。</summary>
    internal static class ExcelValues
    {
        // 沿继承链收集可参与字段转换的实例字段，不把此筛选等同于 Unity 完整序列化资格。
        internal static List<FieldInfo> Fields(Type type, bool includeIgnored = false)
        {
            var fields = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(ScriptableObject) && current != typeof(Object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsInitOnly || field.IsNotSerialized)
                    {
                        continue;
                    }

                    if (!includeIgnored && (field.GetCustomAttribute<ExcelIgnore>() != null || field.GetCustomAttribute<JsonIgnoreAttribute>() != null))
                    {
                        continue;
                    }

                    if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null && field.GetCustomAttribute<SerializeReference>() == null)
                    {
                        continue;
                    }

                    if (fields.Any(f => f.Name == field.Name))
                    {
                        throw new InvalidOperationException("Hidden duplicate field: " + type.FullName + "." + field.Name);
                    }

                    fields.Add(field);
                }
            }
            return fields;
        }

        // 优先选取唯一显式 ID 标记，否则使用名称忽略大小写匹配 id 的字段。
        internal static FieldInfo Id(Type type)
        {
            var fields = Fields(type);
            var marked = fields.Where(f => f.GetCustomAttribute<ExcelScriptableObjectId>() != null).ToArray();
            if (marked.Length > 1)
            {
                throw new InvalidOperationException("Multiple Excel ID fields: " + type.FullName);
            }

            return marked.Length == 1 ? marked[0] : fields.FirstOrDefault(f => f.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
        }

        // 将模型字段映射为工作簿协议列名。
        internal static string ColumnName(FieldInfo field)
        {
            // 仅本轮迁移的自有领域字段沿用旧表头；不把用户模型的 Unity 别名扩展为 Excel 协议。
            var owner = field.DeclaringType;
            if (owner.Assembly != typeof(ExcelIgnore).Assembly ||
                owner.Namespace is not ("YLFramework.Skill" or "YLFramework.Buff" or
                    "YLFramework.Mission" or "YLFramework.Equipment" or "YLFramework.DataAssets"))
            {
                return field.Name;
            }
            return field.GetCustomAttributes<FormerlySerializedAsAttribute>().SingleOrDefault()?.oldName ?? field.Name;
        }

        // 将 CLR 字段类型归入列型，非标量且非 Unity 对象的类型按 JSON 处理。
        internal static ExcelFieldsType FieldType(Type type)
        {
            if (type.IsEnum)
            {
                return ExcelFieldsType.Enum;
            }

            if (typeof(Object).IsAssignableFrom(type))
            {
                return ExcelFieldsType.ObjectReference;
            }

            if (type == typeof(string))
            {
                return ExcelFieldsType.String;
            }

            if (type == typeof(bool))
            {
                return ExcelFieldsType.Bool;
            }

            if (type == typeof(float))
            {
                return ExcelFieldsType.Float;
            }

            if (type == typeof(double))
            {
                return ExcelFieldsType.Double;
            }

            if (type == typeof(long) || type == typeof(ulong))
            {
                return ExcelFieldsType.Long;
            }

            if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte))
            {
                return ExcelFieldsType.Int;
            }

            return ExcelFieldsType.Json;
        }

        // 在无目标 CLR 字段时按列型解析值；枚举和资产引用保留为文本，引用仅校验格式。
        internal static object Primitive(object raw, ExcelFieldsType type)
        {
            string text = ExcelTable.Text(raw);
            switch (type)
            {
                case ExcelFieldsType.String:
                    return text;
                case ExcelFieldsType.Enum:
                    return text;
                case ExcelFieldsType.ObjectReference:
                    ValidateReference(text);
                    return text;
                case ExcelFieldsType.Json:
                    return string.IsNullOrWhiteSpace(text) ? JValue.CreateNull() : JToken.Parse(text);
                case ExcelFieldsType.Bool:
                    return text.Length == 0 ? false : bool.Parse(text);
                case ExcelFieldsType.Int:
                    long integer = text.Length == 0 ? 0 : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    if (integer < int.MinValue || integer > uint.MaxValue)
                    {
                        throw new OverflowException("Int cell exceeds the signed/unsigned 32-bit range.");
                    }

                    return integer;
                case ExcelFieldsType.Long:
                    if (text.Length == 0)
                    {
                        return 0L;
                    }

                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed))
                    {
                        return signed;
                    }

                    return ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                case ExcelFieldsType.Float:
                    float single = text.Length == 0 ? 0 : float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (float.IsNaN(single) || float.IsInfinity(single))
                    {
                        throw new FormatException("Non-finite float.");
                    }

                    return single;
                case ExcelFieldsType.Double:
                    double number = text.Length == 0 ? 0 : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (double.IsNaN(number) || double.IsInfinity(number))
                    {
                        throw new FormatException("Non-finite double.");
                    }

                    return number;
                default:
                    throw new FormatException("Unsupported Excel field type: " + type);
            }
        }

        // 优先调用字段自定义转换，否则严格匹配列型后将值写入暂存对象字段。
        internal static void Import(object target, FieldInfo field, object raw, ExcelFieldsType type)
        {
            var custom = field.GetCustomAttribute<CustomExcelFieldImportAndExport>();
            if (custom != null)
            {
                custom.SetValue(target, field, custom.Import(target, raw, type));
                return;
            }
            if (FieldType(field.FieldType) != type)
            {
                throw new FormatException("Header type " + type + " does not match " + field.FieldType.FullName);
            }

            object value;
            string text = ExcelTable.Text(raw);
            if (type == ExcelFieldsType.Enum)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = "0";
                }

                value = Enum.Parse(field.FieldType, text, true);
                if (field.FieldType.GetCustomAttribute<FlagsAttribute>() == null && !Enum.IsDefined(field.FieldType, value))
                {
                    throw new FormatException("Undefined enum value: " + text);
                }
            }
            else if (type == ExcelFieldsType.ObjectReference)
            {
                ValidateReference(text);
                value = text.Length == 0 ? null : ResolveReference(text, field.FieldType);
            }
            else if (type == ExcelFieldsType.Json)
            {
                value = string.IsNullOrWhiteSpace(text) ? null : JsonConvert.DeserializeObject(text, field.FieldType, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error });
            }
            else if (type == ExcelFieldsType.Int || type == ExcelFieldsType.Long)
            {
                // 直接转目标整型，保留uint/ulong边界，不先损失为Int32/Double。
                value = Convert.ChangeType(text.Length == 0 ? "0" : text, field.FieldType, CultureInfo.InvariantCulture);
            }
            else
            {
                value = Convert.ChangeType(Primitive(raw, type), field.FieldType, CultureInfo.InvariantCulture);
            }

            field.SetValue(target, value);
        }

        // 使用自定义转换或默认协议导出字段，长整型和浮点数以文本避免工作簿精度损失。
        internal static object Export(object target, FieldInfo field)
        {
            var value = field.GetValue(target);
            var type = FieldType(field.FieldType);
            var custom = field.GetCustomAttribute<CustomExcelFieldImportAndExport>();
            if (custom != null)
            {
                return custom.Export(target, value, type);
            }

            if (type == ExcelFieldsType.ObjectReference)
            {
                return value == null ? string.Empty : Reference(value as Object);
            }

            if (type == ExcelFieldsType.Json)
            {
                return JsonConvert.SerializeObject(value, Formatting.None);
            }

            if (type == ExcelFieldsType.Enum)
            {
                return value.ToString();
            }

            if (type == ExcelFieldsType.Long)
            {
                return ExcelTable.Text(value);
            }

            if (value is float single)
            {
                return single.ToString("R", CultureInfo.InvariantCulture);
            }

            if (value is double number)
            {
                return number.ToString("R", CultureInfo.InvariantCulture);
            }

            return value;
        }

        // 以非空 ID 文本作为行键；允许无 ID 的普通列表使用位置键，不替代稳定业务身份。
        internal static string Key(object value, int index, bool requireId = false)
        {
            if (value == null)
            {
                throw new InvalidOperationException("Null row at index " + index);
            }

            var id = Id(value.GetType());
            if (id == null)
            {
                if (requireId)
                {
                    throw new InvalidOperationException("Missing ExcelScriptableObjectId or id field: " + value.GetType().FullName);
                }

                return "row:" + index;
            }
            string key = ExcelTable.Text(id.GetValue(value));
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Empty ID at index " + index);
            }

            return key;
        }

        // 差异及并发保护都比较原字段，不能让有损导出或Ignore掩盖实际写入变化。
        internal static string State(object value) => JsonConvert.SerializeObject(Fields(value.GetType(), true).ToDictionary(f => f.Name,
            f => typeof(Object).IsAssignableFrom(f.FieldType) ? f.GetValue(value) == null ? string.Empty : Reference(f.GetValue(value) as Object) : f.GetValue(value)));
        // 接受空引用或 GUID[:localFileID] 文本格式，不在此检查资产存在性。
        internal static void ValidateReference(string text)
        {
            if (text.Length > 0 && !Regex.IsMatch(text, @"\A[0-9a-fA-F]{32}(:-?[0-9]+)?\z"))
            {
                throw new FormatException("Invalid asset GUID[:localFileID]: " + text);
            }
        }
        // 按 GUID 及可选 localFileID 加载类型匹配的真实资产，拒绝缺失或身份不匹配。
        private static Object ResolveReference(string text, Type type)
        {
            var parts = text.Split(':');
            string path = AssetDatabase.GUIDToAssetPath(parts[0]);
            if (string.IsNullOrEmpty(path))
            {
                throw new FormatException("Asset GUID not found: " + text);
            }

            Object value;
            if (parts.Length == 1)
            {
                value = AssetDatabase.LoadAssetAtPath(path, type);
            }
            else
            {
                long id = long.Parse(parts[1], CultureInfo.InvariantCulture);
                value = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(o => type.IsInstanceOfType(o) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long local) && local == id);
            }
            if (value == null || !type.IsInstanceOfType(value))
            {
                throw new FormatException("Asset GUID type/localFileID mismatch: " + text + " -> " + type.FullName);
            }

            return value;
        }
        // 生成可重新解析为同一持久资产的引用文本，拒绝仅存于内存的对象。
        private static string Reference(Object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long local))
            {
                throw new FormatException("Object reference is not a persistent asset: " + value.name);
            }

            string reference = AssetDatabase.IsSubAsset(value) ? guid + ":" + local.ToString(CultureInfo.InvariantCulture) : guid;
            if (ResolveReference(reference, value.GetType()) != value)
            {
                throw new FormatException("Asset reference identity changed: " + reference);
            }

            return reference;
        }
    }
}
