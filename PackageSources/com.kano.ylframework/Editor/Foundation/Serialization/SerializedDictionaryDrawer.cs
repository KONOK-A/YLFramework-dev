using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using YLFramework.Foundation;
using YLFramework.Serialization;

namespace YLFramework.Editor.Foundation
{
    /// <summary>在 Editor 中绘制可序列化字典的原始行与诊断，支持 UI Toolkit 和 IMGUI。</summary>
    /// <remarks>通过 SerializedProperty 编辑原始数据，不直接重建运行时索引；多选仅在各目标字段存在且行数一致时开放同行字段编辑。</remarks>
    [CustomPropertyDrawer(typeof(SerializedDictionary<,>), true)]
    public class SerializedDictionaryDrawer : PropertyDrawer
    {
        private const float Spacing = 2f;
        private static float DiagnosticHeight => EditorGUIUtility.singleLineHeight * 4;
        private Vector2 _diagnosticScroll;

        /// <summary>创建带诊断的 UI Toolkit 属性树，并在序列化对象变化时刷新诊断和必要的行结构。</summary>
        /// <returns>由 Inspector 承接绑定的根元素；结构变化后新控件在本方法内重新绑定同一序列化对象。</returns>
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var serialized = property.serializedObject;
            var path = property.propertyPath;
            var root = new VisualElement();
            var help = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            var fields = new VisualElement();
            root.Add(help);
            root.Add(fields);
            int previousSize = -1;
            bool previousEditable = false;

            // 重新按路径取得当前属性，只有行数或编辑资格变化才重建控件，避免普通值变化打断编辑。
            void Refresh()
            {
                var current = serialized.FindProperty(path);
                if (current == null)
                {
                    return;
                }

                help.text = Describe(current, out var editable);
                var rows = current.FindPropertyRelative("datas");
                var size = rows.arraySize;
                if (size == previousSize && editable == previousEditable)
                {
                    return;
                }

                previousSize = size;
                previousEditable = editable;
                fields.Clear();
                if (!serialized.isEditingMultipleObjects)
                {
                    fields.Add(new PropertyField(rows, current.displayName + " (Key / Value)"));
                }
                else
                {
                    // 多选不提供列表结构控件，避免原生数组操作覆盖各目标不同的原始行。
                    var foldout = new Foldout { text = current.displayName, value = current.isExpanded };
                    foldout.RegisterValueChangedCallback(evt => current.isExpanded = evt.newValue);
                    for (int i = 0; i < size; i++)
                    {
                        var row = rows.GetArrayElementAtIndex(i);
                        var rowFoldout = new Foldout { text = "Element " + i, value = row.isExpanded };
                        rowFoldout.RegisterValueChangedCallback(evt => row.isExpanded = evt.newValue);
                        // 不绑定数组元素本身，避免原生右键/命令菜单暴露 Duplicate/Delete。
                        rowFoldout.Add(new PropertyField(row.FindPropertyRelative("Key")));
                        rowFoldout.Add(new PropertyField(row.FindPropertyRelative("Value")));
                        foldout.Add(rowFoldout);
                    }
                    foldout.SetEnabled(editable);
                    fields.Add(foldout);
                }
                // 初次由 Inspector 绑定；结构变化后新增的控件也使用相同的原生绑定。
                fields.Bind(serialized);
            }

            Refresh();
            root.TrackSerializedObjectValue(serialized, _ => Refresh());
            return root;
        }

        /// <summary>在 IMGUI 中绘制固定高度诊断与原始行，多选时隐藏结构操作并限制不兼容的字段编辑。</summary>
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            try
            {
                var text = Describe(property, out var editable);
                var contentWidth = Mathf.Max(1, position.width - 18);
                var contentHeight = Mathf.Max(DiagnosticHeight, EditorStyles.helpBox.CalcHeight(new GUIContent(text), contentWidth));
                _diagnosticScroll = GUI.BeginScrollView(new Rect(position.x, position.y, position.width, DiagnosticHeight),
                    _diagnosticScroll, new Rect(0, 0, contentWidth, contentHeight));
                try
                {
                    EditorGUI.HelpBox(new Rect(0, 0, contentWidth, contentHeight), text, MessageType.Info);
                }
                finally
                {
                    GUI.EndScrollView();
                }
                position.y += DiagnosticHeight + Spacing;
                var rows = property.FindPropertyRelative("datas");
                if (!property.serializedObject.isEditingMultipleObjects)
                {
                    position.height = EditorGUI.GetPropertyHeight(rows, label, true);
                    EditorGUI.PropertyField(position, rows, label, true);
                }
                else
                {
                    position.height = EditorGUIUtility.singleLineHeight;
                    property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, label, true);
                    if (!property.isExpanded)
                    {
                        return;
                    }

                    using (new EditorGUI.DisabledScope(!editable))
                    {
                        for (int i = 0; i < rows.arraySize; i++)
                        {
                            position.y += position.height + Spacing;
                            var row = rows.GetArrayElementAtIndex(i);
                            position.height = EditorGUIUtility.singleLineHeight;
                            EditorGUI.LabelField(position, "Element " + i);
                            for (int fieldIndex = 0; fieldIndex < 2; fieldIndex++)
                            {
                                position.y += position.height + Spacing;
                                var field = row.FindPropertyRelative(fieldIndex == 0 ? "Key" : "Value");
                                position.height = EditorGUI.GetPropertyHeight(field, true);
                                EditorGUI.PropertyField(position, field, true);
                            }
                        }
                    }
                }
            }
            finally
            {
                EditorGUI.EndProperty();
            }
        }

        /// <summary>计算诊断区与当前展开行的总高度，匹配单选列表和多选逐字段布局。</summary>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            // 高度不依赖窗口宽度；窄列与长诊断在固定区域滚动，不覆盖后续字段。
            var height = DiagnosticHeight + Spacing;
            var rows = property.FindPropertyRelative("datas");
            if (!property.serializedObject.isEditingMultipleObjects)
            {
                return height + EditorGUI.GetPropertyHeight(rows, label, true);
            }

            height += EditorGUIUtility.singleLineHeight;
            if (property.isExpanded)
            {
                for (int i = 0; i < rows.arraySize; i++)
                {
                    var row = rows.GetArrayElementAtIndex(i);
                    height += EditorGUIUtility.singleLineHeight + Spacing;
                    height += EditorGUI.GetPropertyHeight(row.FindPropertyRelative("Key"), true) + Spacing;
                    height += EditorGUI.GetPropertyHeight(row.FindPropertyRelative("Value"), true) + Spacing;
                }
            }

            return height;
        }

        /// <summary>解析实际字典键类型并汇总各目标诊断，以字段存在性和行数一致性决定多选能否编辑。</summary>
        private string Describe(SerializedProperty property, out bool editable)
        {
            editable = true;
            var dictionaryType = fieldInfo.FieldType;
            if (dictionaryType.IsArray)
            {
                dictionaryType = dictionaryType.GetElementType();
            }
            else if (dictionaryType.IsGenericType && dictionaryType.GetGenericTypeDefinition() == typeof(List<>))
            {
                dictionaryType = dictionaryType.GetGenericArguments()[0];
            }

            while (!dictionaryType.IsGenericType || dictionaryType.GetGenericTypeDefinition() != typeof(SerializedDictionary<,>))
            {
                dictionaryType = dictionaryType.BaseType;
            }

            var keyType = dictionaryType.GetGenericArguments()[0];
            var text = new StringBuilder("首个有效键参与运行时；原始行不会自动删除。行号对应 Element，下方可修改 Key / Value。\n");
            if (!property.serializedObject.isEditingMultipleObjects)
            {
                return text.Append(DescribeRows(property.FindPropertyRelative("datas"), keyType)).ToString();
            }

            int? size = null;
            foreach (var target in property.serializedObject.targetObjects)
            {
                using (var serialized = new SerializedObject(target))
                {
                    var rows = serialized.FindProperty(property.propertyPath)?.FindPropertyRelative("datas");
                    if (rows == null)
                    {
                        editable = false;
                        text.Append(target.name).Append(": 此目标没有对应字段。\n");
                        continue;
                    }
                    if (size.HasValue && size != rows.arraySize)
                    {
                        editable = false;
                    }

                    size = rows.arraySize;
                    text.Append(target.name).Append(": ").Append(DescribeRows(rows, keyType)).Append('\n');
                }
            }
            text.Append(editable ? "多对象仅编辑同行字段，禁止批量增删/重排。" : "各目标结构/长度不同，禁止批量编辑；请分别选择目标修复。禁止批量增删/重排。");
            return text.ToString();
        }

        /// <summary>按实际键比较器诊断空行、空键和重复键；无法判别的 Unity 引用保持待确认，不删除原始行。</summary>
        private static string DescribeRows(SerializedProperty rows, Type keyType)
        {
            var issues = new StringBuilder();
            Dictionary<object, int> firstRows = null;
            int valid = 0;
            int uncertain = 0;
            for (int i = 0; i < rows.arraySize; i++)
            {
                var keyProperty = rows.GetArrayElementAtIndex(i).FindPropertyRelative("Key");
                if (keyProperty == null)
                {
                    issues.Append($"\n行 [{i}]：缺失条目/Key，无法参与运行时。");
                    continue;
                }
                object key;
                // 2022.3 不支持装箱闭合泛型条目，读取其实际 Key 字段而非运行时缓存。
                try
                {
                    key = keyProperty.boxedValue;
                }
                catch (InvalidOperationException exception)
                {
                    return $"原始 {rows.arraySize}；当前 Unity 不支持此字段的 boxedValue 诊断：{exception.Message}。可编辑原始字段，但无法确定有效数。";
                }
                if (key == null)
                {
                    if (typeof(UnityEngine.Object).IsAssignableFrom(keyType))
                    {
                        uncertain++;
                        issues.Append($"\n行 [{i}]：None/Missing 引用，SerializedProperty 无法区分 CLR null 与 Unity fake-null；运行时仅拒绝 CLR null。建议用稳定 ID 作持久化键。");
                    }
                    else
                    {
                        issues.Append($"\n行 [{i}]：CLR null 键，不参与运行时。");
                    }

                    continue;
                }
                if (keyType.IsEnum)
                {
                    key = Enum.ToObject(keyType, key);
                }

                if (firstRows == null)
                {
                    var comparer = (IEqualityComparer)typeof(EqualityComparer<>).MakeGenericType(keyType)
                        .GetProperty("Default").GetValue(null);
                    firstRows = new Dictionary<object, int>(new KeyComparer(comparer));
                }
                if (firstRows.TryGetValue(key, out var first))
                {
                    issues.Append($"\n行 [{i}]：重复键，首个有效行为 [{first}]，此行不参与运行时。");
                }
                else
                {
                    firstRows.Add(key, i);
                    valid++;
                }
            }
            return (uncertain == 0
                ? $"原始 {rows.arraySize} / 有效 {valid} / 无效 {rows.arraySize - valid}"
                : $"原始 {rows.arraySize} / 确认有效 {valid} / 确认无效 {rows.arraySize - valid - uncertain} / 待确认 {uncertain}") + issues;
        }

        /// <summary>将实际键类型的默认比较器适配到装箱键，保持诊断与运行时字典的相等判定一致。</summary>
        private sealed class KeyComparer : IEqualityComparer<object>
        {
            private readonly IEqualityComparer _comparer;
            /// <summary>保存按键类型构造的比较器，供诊断字典复用其相等和哈希规则。</summary>
            public KeyComparer(IEqualityComparer comparer)
            {
                this._comparer = comparer;
            }
            /// <summary>将装箱键的相等比较委托给实际键类型的比较器。</summary>
            public new bool Equals(object x, object y) => _comparer.Equals(x, y);
            /// <summary>使用实际键类型比较器的哈希，与相等规则保持一致。</summary>
            public int GetHashCode(object obj) => _comparer.GetHashCode(obj);
        }
    }
}
