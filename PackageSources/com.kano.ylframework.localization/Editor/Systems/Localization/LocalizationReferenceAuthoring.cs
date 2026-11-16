using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;
using YLFramework.Localization;

namespace YLFramework.Editor.Localization
{
    /// <summary>只解析官方集合与条目身份，不创建翻译、不要求每个 Locale 都有值。</summary>
    public static class LocalizationReferenceAuthoring
    {
        /// <summary>检查直接序列化字段，诊断包含对象和字段上下文；不修改对象。</summary>
        public static IReadOnlyList<string> ValidateObject(UnityEngine.Object target)
        {
            if (!target)
                return new[] { "The configuration object is missing." };
            var errors = new List<string>();
            foreach (var field in Fields(target.GetType()))
            {
                try
                {
                    Resolve(field.GetValue(target) as LocalizedReference, field.FieldType, AllowsEmpty(field));
                }
                catch (Exception error) { errors.Add(target.name + "." + field.Name + ": " + error.Message); }
            }
            return errors;
        }

        /// <summary>仅用于调用方拥有的暂存对象；将可读身份转换成官方 GUID/Key ID。</summary>
        public static void NormalizeObject(UnityEngine.Object staged)
        {
            if (!staged)
                throw new ArgumentNullException(nameof(staged));
            foreach (var field in Fields(staged.GetType()))
            {
                try
                {
                    var reference = field.GetValue(staged) as LocalizedReference;
                    var resolved = Resolve(reference, field.FieldType, AllowsEmpty(field));
                    if (resolved.Collection)
                        reference.SetReference(resolved.Collection.SharedData.TableCollectionNameGuid, resolved.Key);
                }
                catch (Exception error) { throw new InvalidOperationException(staged.name + "." + field.Name + ": " + error.Message, error); }
            }
        }

        /// <summary>复制已校验引用的稳定 Excel 文本，不改变原引用。</summary>
        public static string CopyReference(LocalizedReference reference)
        {
            if (reference == null || reference.IsEmpty)
                throw new ArgumentException("Choose an official localization entry.");
            var resolved = Resolve(reference, reference.GetType(), false);
            return "{\"tableGuid\":\"" + resolved.Collection.SharedData.TableCollectionNameGuid.ToString("N") +
                "\",\"entryId\":\"" + resolved.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}";
        }

        /// <summary>定位官方表集合，不打开或维护另一套翻译窗口。</summary>
        public static void PingReference(LocalizedReference reference)
        {
            var resolved = Resolve(reference, reference?.GetType(), false);
            EditorGUIUtility.PingObject(resolved.Collection);
            Selection.activeObject = resolved.Collection;
        }

        private static IEnumerable<FieldInfo> Fields(Type type)
        {
            var hierarchy = new Stack<Type>();
            for (; type != null && type != typeof(UnityEngine.Object); type = type.BaseType)
                hierarchy.Push(type);
            while (hierarchy.Count != 0)
                foreach (var field in hierarchy.Pop().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.IsStatic && !field.IsInitOnly && !field.IsNotSerialized &&
                        (field.IsPublic || field.IsDefined(typeof(SerializeField), true)) && typeof(LocalizedReference).IsAssignableFrom(field.FieldType))
                        yield return field;
        }

        private static bool AllowsEmpty(FieldInfo field) => field.GetCustomAttribute<LocalizedExcelReferenceAttribute>()?.AllowEmpty ?? true;

        private static (LocalizationTableCollection Collection, long Key) Resolve(LocalizedReference reference, Type type, bool allowEmpty)
        {
            if (reference == null || reference.IsEmpty)
            {
                if (allowEmpty)
                    return default;
                throw new InvalidOperationException("A required localization reference is empty.");
            }
            var strings = reference is LocalizedString;
            var candidates = strings
                ? LocalizationEditorSettings.GetStringTableCollections().Cast<LocalizationTableCollection>()
                : LocalizationEditorSettings.GetAssetTableCollections().Cast<LocalizationTableCollection>();
            var table = reference.TableReference;
            var matches = candidates.Where(collection => collection && collection.SharedData &&
                (table.ReferenceType == TableReference.Type.Guid
                    ? collection.SharedData.TableCollectionNameGuid == table.TableCollectionNameGuid
                    : string.Equals(collection.TableCollectionName, table.TableCollectionName, StringComparison.Ordinal))).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Missing or ambiguous " + (strings ? "String" : "Asset") + " Table collection: " + table);
            var collection = matches[0];
            var key = reference.TableEntryReference;
            var entry = key.ReferenceType == TableEntryReference.Type.Id
                ? collection.SharedData.GetEntry(key.KeyId) : collection.SharedData.GetEntry(key.Key);
            if (entry == null)
                throw new InvalidOperationException("Missing entry '" + key + "' in '" + collection.TableCollectionName + "'.");
            if (!strings)
            {
                var baseType = type;
                while (baseType != null && (!baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(LocalizedAsset<>)))
                    baseType = baseType.BaseType;
                if (baseType == null)
                    throw new InvalidOperationException("Use a typed official LocalizedAsset reference.");
                var expected = baseType.GetGenericArguments()[0];
                var actual = ((AssetTableCollection)collection).GetEntryAssetType(entry.Id);
                if (actual != null && actual != typeof(UnityEngine.Object) && !expected.IsAssignableFrom(actual))
                    throw new InvalidOperationException("Entry asset type is " + actual.Name + ", expected " + expected.Name + ".");
            }
            return (collection, entry.Id);
        }
    }
}
