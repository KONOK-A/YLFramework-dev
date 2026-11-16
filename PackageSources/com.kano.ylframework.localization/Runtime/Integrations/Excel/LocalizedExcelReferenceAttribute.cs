using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;
using YLFramework.Excel;

namespace YLFramework.Localization
{
    /// <summary>在 Json 列中交换官方表项引用，不读取、复制或修改翻译正文。</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class LocalizedExcelReferenceAttribute : CustomExcelFieldImportAndExport
    {
        /// <summary>明确允许空引用；默认拒绝以空单元格意外擦除必需引用。</summary>
        public bool AllowEmpty
        {
            get; set;
        }

        /// <inheritdoc/>
        public override object Import(object scriptableObject, object value, ExcelFieldsType type)
        {
            RequireJson(type);
            var text = value as string;
            if (value != null && !(value is string))
                throw new FormatException("Localization references must be JSON text.");
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "null", StringComparison.Ordinal))
            {
                if (!AllowEmpty)
                    throw new FormatException("This localization reference cannot be empty.");
                return null;
            }
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            var json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read())
                throw new FormatException("Unexpected content after the localization reference.");
            if (json.Count != 2)
                throw new FormatException("Use only table/entry or tableGuid/entryId.");
            if (json.Property("table") != null && json.Property("entry") != null)
                return new ReferenceValue((TableReference)Text(json, "table"), (TableEntryReference)Text(json, "entry"));
            if (json.Property("tableGuid") == null || json.Property("entryId") == null)
                throw new FormatException("Do not mix readable and stable localization reference fields.");
            if (!Guid.TryParseExact(Text(json, "tableGuid"), "N", out var guid) || guid == Guid.Empty)
                throw new FormatException("tableGuid must be a non-empty 32-character collection GUID.");
            var idText = Text(json, "entryId");
            if (!long.TryParse(idText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id == 0)
                throw new FormatException("entryId must be a non-zero Int64 encoded as decimal text.");
            return new ReferenceValue((TableReference)guid, (TableEntryReference)id);
        }

        /// <inheritdoc/>
        public override object Export(object scriptableObject, object value, ExcelFieldsType type)
        {
            RequireJson(type);
            if (!(value is LocalizedReference reference) || reference.IsEmpty)
            {
                if (AllowEmpty)
                    return "";
                throw new FormatException("This localization reference cannot be empty.");
            }
            var guid = reference.TableReference.TableCollectionNameGuid;
            var id = reference.TableEntryReference.KeyId;
            if (guid == Guid.Empty || id == 0)
                throw new FormatException("Export through LocalizationExcelCommands to resolve readable references to stable IDs.");
            return new JObject
            {
                ["tableGuid"] = guid.ToString("N"),
                ["entryId"] = id.ToString(CultureInfo.InvariantCulture)
            }.ToString(Formatting.None);
        }

        /// <inheritdoc/>
        public override void SetValue(object scriptableObject, FieldInfo fieldInfo, object value)
        {
            if (fieldInfo == null || !typeof(LocalizedReference).IsAssignableFrom(fieldInfo.FieldType) || fieldInfo.FieldType.IsAbstract)
                throw new ArgumentException("Use LocalizedExcelReference on a concrete official localization reference field.");
            var reference = fieldInfo.GetValue(scriptableObject) as LocalizedReference
                ?? (LocalizedReference)Activator.CreateInstance(fieldInfo.FieldType);
            if (value != null)
            {
                if (!(value is ReferenceValue parsed))
                    throw new ArgumentException("The localization reference was not parsed by this converter.");
                reference.SetReference(parsed.Table, parsed.Entry);
            }
            else
                reference.SetReference(default, default);
            fieldInfo.SetValue(scriptableObject, reference);
        }

        private static string Text(JObject json, string key)
        {
            if (json[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)json[key]))
                throw new FormatException(key + " must be non-empty text; numeric IDs must not pass through Excel floating-point cells.");
            return (string)json[key];
        }

        private static void RequireJson(ExcelFieldsType type)
        {
            if (type != ExcelFieldsType.Json)
                throw new FormatException("Localization reference columns must use Json.");
        }

        private sealed class ReferenceValue
        {
            internal readonly TableReference Table;
            internal readonly TableEntryReference Entry;
            internal ReferenceValue(TableReference table, TableEntryReference entry)
            {
                Table = table;
                Entry = entry;
            }
        }
    }
}
