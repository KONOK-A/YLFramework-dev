using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Fixed 编码

namespace YLFramework.Editor.Common
{
    /// <summary>为 Inspector 提供属性值同步、原生剪贴板预检和按字段路径查询特性的工具。</summary>
    public static class SerializedPropertyExtensions
    {
        /// <summary>
        /// 按接收方属性类型复制源值，仅写入序列化缓冲，不自动应用到对象。
        /// </summary>
        /// <param name="serializedProperty">接收值的属性。</param>
        /// <param name="target">具有兼容类型的源属性，本方法不执行类型兼容预检。</param>
        /// <remarks>对象引用只复制引用；Generic、Character、Gradient、FixedBufferSize 及未列出的类型不处理。</remarks>
        public static void SyncFrom(this SerializedProperty serializedProperty, SerializedProperty target)
        {
            switch (serializedProperty.propertyType)
            {
                case SerializedPropertyType.Generic:
                    break;
                case SerializedPropertyType.Integer:
                    serializedProperty.intValue = target.intValue;
                    break;
                case SerializedPropertyType.Boolean:
                    serializedProperty.boolValue = target.boolValue;
                    break;
                case SerializedPropertyType.Float:
                    serializedProperty.floatValue = target.floatValue;
                    break;
                case SerializedPropertyType.String:
                    serializedProperty.stringValue = target.stringValue;
                    break;
                case SerializedPropertyType.Color:
                    serializedProperty.colorValue = target.colorValue;
                    break;
                case SerializedPropertyType.ObjectReference:
                    serializedProperty.objectReferenceValue = target.objectReferenceValue;
                    break;
                case SerializedPropertyType.LayerMask:
                    serializedProperty.intValue = target.intValue;
                    break;
                case SerializedPropertyType.Enum:
                    serializedProperty.enumValueIndex = target.enumValueIndex;
                    break;
                case SerializedPropertyType.Vector2:
                    serializedProperty.vector2Value = target.vector2Value;
                    break;
                case SerializedPropertyType.Vector3:
                    serializedProperty.vector3Value = target.vector3Value;
                    break;
                case SerializedPropertyType.Vector4:
                    serializedProperty.vector4Value = target.vector4Value;
                    break;
                case SerializedPropertyType.Rect:
                    serializedProperty.rectValue = target.rectValue;
                    break;
                case SerializedPropertyType.ArraySize:
                    serializedProperty.arraySize = target.arraySize;
                    break;
                case SerializedPropertyType.Character:
                    break;
                case SerializedPropertyType.AnimationCurve:
                    serializedProperty.animationCurveValue = target.animationCurveValue;
                    break;
                case SerializedPropertyType.Bounds:
                    serializedProperty.boundsValue = target.boundsValue;
                    break;
                case SerializedPropertyType.Gradient:
                    break;
                case SerializedPropertyType.Quaternion:
                    serializedProperty.quaternionValue = target.quaternionValue;
                    break;
                case SerializedPropertyType.ExposedReference:
                    serializedProperty.exposedReferenceValue = target.exposedReferenceValue;
                    break;
                case SerializedPropertyType.FixedBufferSize:
                    //serializedProperty.fixedBufferSize = target.fixedBufferSize;
                    break;
                case SerializedPropertyType.Vector2Int:
                    serializedProperty.vector2IntValue = target.vector2IntValue;
                    break;
                case SerializedPropertyType.Vector3Int:
                    serializedProperty.vector3IntValue = target.vector3IntValue;
                    break;
                case SerializedPropertyType.RectInt:
                    serializedProperty.rectIntValue = target.rectIntValue;
                    break;
                case SerializedPropertyType.BoundsInt:
                    serializedProperty.boundsIntValue = target.boundsIntValue;
                    break;
            }
        }

        /// <summary>
        /// 取得属性值的显示文本；枚举显示索引，对象引用显示对象文本或 None，不用于无损序列化。
        /// </summary>
        /// <param name="serializedProperty">要读取的有效属性。</param>
        /// <returns>当前类型的文本表示；未支持的属性类型返回空字符串。</returns>
        public static string GetValueText(this SerializedProperty serializedProperty)
        {
            switch (serializedProperty.propertyType)
            {
                case SerializedPropertyType.Generic:
                    break;
                case SerializedPropertyType.Integer:
                    return serializedProperty.intValue.ToString();

                case SerializedPropertyType.Boolean:
                    return serializedProperty.boolValue.ToString();

                case SerializedPropertyType.Float:
                    return serializedProperty.floatValue.ToString();
                case SerializedPropertyType.String:
                    return serializedProperty.stringValue;

                case SerializedPropertyType.Color:
                    return serializedProperty.colorValue.ToString();

                case SerializedPropertyType.ObjectReference:
                    return serializedProperty.objectReferenceValue ? serializedProperty.objectReferenceValue.ToString() : "None";
                case SerializedPropertyType.LayerMask:
                    return serializedProperty.intValue.ToString();

                case SerializedPropertyType.Enum:
                    return serializedProperty.enumValueIndex.ToString();

                case SerializedPropertyType.Vector2:
                    return serializedProperty.vector2Value.ToString();

                case SerializedPropertyType.Vector3:
                    return serializedProperty.vector3Value.ToString();

                case SerializedPropertyType.Vector4:
                    return serializedProperty.vector4Value.ToString();

                case SerializedPropertyType.Rect:
                    return serializedProperty.rectValue.ToString();

                case SerializedPropertyType.ArraySize:
                    return serializedProperty.arraySize.ToString();

                case SerializedPropertyType.Character:
                    break;
                case SerializedPropertyType.AnimationCurve:
                    return serializedProperty.animationCurveValue.ToString();

                case SerializedPropertyType.Bounds:
                    return serializedProperty.boundsValue.ToString();

                case SerializedPropertyType.Gradient:
                    break;
                case SerializedPropertyType.Quaternion:
                    return serializedProperty.quaternionValue.ToString();

                case SerializedPropertyType.ExposedReference:
                    return serializedProperty.exposedReferenceValue ? serializedProperty.exposedReferenceValue.ToString() : "None";

                case SerializedPropertyType.FixedBufferSize:
                    //serializedProperty.fixedBufferSize = target.fixedBufferSize;
                    break;
                case SerializedPropertyType.Vector2Int:
                    return serializedProperty.vector2IntValue.ToString();

                case SerializedPropertyType.Vector3Int:
                    return serializedProperty.vector3IntValue.ToString();

                case SerializedPropertyType.RectInt:
                    return serializedProperty.rectIntValue.ToString();

                case SerializedPropertyType.BoundsInt:
                    return serializedProperty.boundsIntValue.ToString();

            }

            return string.Empty;

        }

        /// <summary>
        /// 使用 Unity 原生格式复制属性；混值或原生解析器不支持的属性明确拒绝。
        /// </summary>
        /// <param name="serializedProperty">待复制的有效属性；结构内部也不能含混值或不支持的类型。</param>
        public static void CopyToClipboard(this SerializedProperty serializedProperty)
        {
            string error = ClipboardPropertyError(serializedProperty, false);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }

            GetClipboardMethod("UnityEditor.Clipboard", "SetSerializedProperty", typeof(void), typeof(SerializedProperty))
                .Invoke(null, new object[] { serializedProperty });
        }

        /// <summary>
        /// 完整预检后粘贴并应用修改，沿用 SerializedObject 的 Undo 与 Prefab override 契约。
        /// 不支持混值、多种目标类型或不兼容的原生数据；拒绝时不应用任何修改。
        /// </summary>
        /// <param name="serializedProperty">可编辑且与剪贴板数据兼容的目标属性。</param>
        /// <remarks>应用的是该 SerializedObject 的全部待提交修改，不仅是本属性；不负责保存资产或场景文件。</remarks>
        public static void PasteFromClipboard(this SerializedProperty serializedProperty)
        {
            if (!PrepareClipboardPaste(serializedProperty, out var staged, out string error))
            {
                throw new InvalidOperationException(error);
            }

            using (staged)
            {
                serializedProperty.serializedObject.CopyFromSerializedProperty(staged.FindProperty(serializedProperty.propertyPath));
                serializedProperty.serializedObject.ApplyModifiedProperties();
            }
        }

        /// <summary>
        /// 判断原生 Clipboard 数据能否完整粘贴；不修改对象、待应用修改或系统粘贴板。
        /// Unity 私有 API 不可用时抛出带版本与成员信息的 NotSupportedException。
        /// </summary>
        /// <param name="serializedProperty">要预检的粘贴目标。</param>
        /// <returns>完整解析并回读比对一致时为 true；目标或数据不兼容时为 false。</returns>
        public static bool IsCanPaste(this SerializedProperty serializedProperty)
        {
            bool compatible = PrepareClipboardPaste(serializedProperty, out var staged, out _);
            staged?.Dispose();
            return compatible;
        }

        /// <summary>按精确签名解析 Unity 内部静态方法，版本不兼容时明确报错。</summary>
        private static MethodInfo GetClipboardMethod(string typeName, string name, Type returnType, params Type[] parameters)
        {
            // 只解析 Unity Editor 自身的类型，不从业务程序集接收同名替身。
            var type = typeof(UnityEditor.Editor).Assembly.GetType(typeName);
            var method = type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, parameters, null);
            if (method == null || method.ReturnType != returnType)
            {
                throw new NotSupportedException($"Unity {Application.unityVersion} Clipboard API 不兼容：{typeName}.{name}({string.Join(", ", parameters.Select(p => p.Name))}) -> {returnType.Name}。");
            }

            return method;
        }

        /// <summary>检查目标可编辑性、目标类型及整棵属性子树的混值和不支持类型，返回拒绝原因。</summary>
        private static string ClipboardPropertyError(SerializedProperty property, bool paste)
        {
            if (property == null)
            {
                return "Clipboard 操作需要有效的 SerializedProperty。";
            }

            if (paste && (!property.editable || property.serializedObject.targetObjects.Any(target =>
                !target || (target.hideFlags & HideFlags.NotEditable) != 0 ||
                PrefabUtility.IsPartOfImmutablePrefab(target) ||
                (EditorUtility.IsPersistent(target) && !AssetDatabase.IsOpenForEdit(target)))))
            {
                return "Clipboard 目标不可编辑。";
            }

            if (property.serializedObject.targetObjects.Select(target => target.GetType()).Distinct().Count() != 1)
            {
                return "Clipboard 不支持不同类型的多对象操作。";
            }

            using var iterator = property.Copy();
            using var end = property.GetEndProperty();
            do
            {
                if (iterator.hasMultipleDifferentValues)
                {
                    return "Clipboard 不支持混值属性，请分别编辑目标对象。";
                }

                if (iterator.propertyType == SerializedPropertyType.ManagedReference ||
                    iterator.propertyType == SerializedPropertyType.ExposedReference ||
                    iterator.propertyType == SerializedPropertyType.FixedBufferSize ||
                    (iterator.propertyType == SerializedPropertyType.Float && iterator.type == "double"))
                {
                    return "Unity 原生 Clipboard 不能可靠处理此属性：" + iterator.propertyPath;
                }
            } while (iterator.Next(true) && !SerializedProperty.EqualContents(iterator, end));
            return null;
        }

        /// <summary>在独立缓冲中解析并回读原生剪贴板数据；成功时交出待释放缓冲，失败时自行释放。</summary>
        private static bool PrepareClipboardPaste(SerializedProperty property, out SerializedObject staged, out string error)
        {
            staged = null;
            error = ClipboardPropertyError(property, true);
            if (error != null)
            {
                return false;
            }

            var has = GetClipboardMethod("UnityEditor.Clipboard", "HasSerializedProperty", typeof(bool));
            var read = GetClipboardMethod("UnityEditor.Clipboard", "GetSerializedProperty", typeof(void), typeof(SerializedProperty));
            var write = GetClipboardMethod("UnityEditor.ClipboardParser", "WriteGenericSerializedProperty",
                typeof(Dictionary<string, object>), typeof(SerializedProperty));
            var deserialize = GetClipboardMethod("UnityEditor.Json", "Deserialize", typeof(object), typeof(string));
            var serialize = GetClipboardMethod("UnityEditor.Json", "Serialize", typeof(string), typeof(object), typeof(bool), typeof(string));
            error = "Clipboard 数据损坏、不兼容或无法完整粘贴；未应用修改。";
            if (!(bool)has.Invoke(null, null))
            {
                return false;
            }
            // 使用 Unity 自身的格式、编码器与解析器，不建立自有 Clipboard 协议。
            var clipboardType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Clipboard");
            var prefixField = clipboardType.GetField("kGenericPrefix", BindingFlags.NonPublic | BindingFlags.Static);
            if (prefixField == null || !prefixField.IsLiteral || prefixField.FieldType != typeof(string))
            {
                throw new NotSupportedException($"Unity {Application.unityVersion} Clipboard API 不兼容：kGenericPrefix。");
            }

            string prefix = (string)prefixField.GetRawConstantValue();
            string contents = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(prefix) || !contents.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            bool accepted = false;
            try
            {
                var expected = deserialize.Invoke(null, new object[] { contents.Substring(prefix.Length) }) as Dictionary<string, object>;
                if (expected == null || !expected.TryGetValue("name", out var name) ||
                    !(name is string sourceName) || string.IsNullOrEmpty(sourceName))
                {
                    return false;
                }
                // 独立缓冲以相同目标初始化；原生解析即使部分写入，也不会碰到调用者或真实对象。
                staged = new SerializedObject(property.serializedObject.targetObjects);
                staged.CopyFromSerializedProperty(property);
                var destination = staged.FindProperty(property.propertyPath);
                read.Invoke(null, new object[] { destination });
                var encoded = write.Invoke(null, new object[] { destination });
                // 再经过 Unity JSON 编解码统一数值表示，检查原生解析是否遗漏或改变任何字段。
                string json = (string)serialize.Invoke(null, new object[] { encoded, false, "" });
                var actual = deserialize.Invoke(null, new object[] { json }) as Dictionary<string, object>;
                expected["name"] = destination.name;
                accepted = ClipboardValuesEqual(expected, actual);
                return accepted;
            }
            catch (Exception exception) when (exception.GetBaseException() is ArgumentException ||
                exception.GetBaseException() is FormatException || exception.GetBaseException() is InvalidCastException ||
                exception.GetBaseException() is OverflowException)
            {
                error += " " + exception.GetBaseException().Message;
                return false;
            }
            finally
            {
                if (!accepted)
                {
                    staged?.Dispose();
                    staged = null;
                }
            }
        }

        /// <summary>递归比较原生解析前后的字段集合、列表顺序和规范化叶子值是否一致。</summary>
        private static bool ClipboardValuesEqual(object expected, object actual)
        {
            if (expected is Dictionary<string, object> dictionary)
            {
                return actual is Dictionary<string, object> other && dictionary.Count == other.Count &&
                    dictionary.All(pair => other.TryGetValue(pair.Key, out var value) && ClipboardValuesEqual(pair.Value, value));
            }

            if (expected is List<object> list)
            {
                return actual is List<object> otherList && list.Count == otherList.Count &&
                    list.Select((value, index) => ClipboardValuesEqual(value, otherList[index])).All(equal => equal);
            }

            return Equals(expected, actual);
        }


        /// <summary>
        /// 查询属性路径末端字段的首个指定特性，字段解析规则与 GetAttributes 相同。
        /// </summary>
        /// <typeparam name="T">要查询的 Attribute 类型。</typeparam>
        /// <param name="prop">用于定位字段的有效序列化属性。</param>
        /// <returns>首个匹配特性；路径或特性未找到时为 null。</returns>
        public static T GetAttribute<T>(this SerializedProperty prop) where T : Attribute
        {
            var attrs = prop.GetAttributes<T>();
            return attrs != null && attrs.Length > 0 ? attrs[0] : null;
        }

        /// <summary>
        /// 按首个目标对象的声明字段路径查询末端字段的特性，支持基类字段和集合元素路径。
        /// </summary>
        /// <typeparam name="T">要查询的 Attribute 类型。</typeparam>
        /// <param name="prop">其 propertyPath 用于反射定位字段，不读取元素的运行时派生类型。</param>
        /// <returns>匹配特性的数组；目标、字段或特性不存在时为空数组。</returns>
        public static T[] GetAttributes<T>(this SerializedProperty prop) where T : Attribute
        {
            var target = prop.serializedObject.targetObject;
            if (target == null)
            {
                return Array.Empty<T>();
            }

            // 把 "myList.Array.data[0].field" 转成 "myList[0].field"
            var path = prop.propertyPath.Replace(".Array.data[", "[");
            var parts = path.Split('.');

            Type currentType = target.GetType();
            FieldInfo field = null;

            foreach (var part in parts)
            {
                string name = part;
                // 路径中的 list[0] 按集合字段 list 查找，索引不参与反射字段名。
                var bracket = part.IndexOf('[');
                if (bracket > 0)
                {
                    name = part.Substring(0, bracket);
                }

                field = GetFieldInHierarchy(currentType, name);
                if (field == null)
                {
                    return Array.Empty<T>();
                }

                currentType = field.FieldType;
                // 如果是集合，切换到元素类型
                if (currentType.IsArray)
                {
                    currentType = currentType.GetElementType();
                }
                else if (currentType.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(currentType))
                {
                    var args = currentType.GetGenericArguments();
                    if (args.Length > 0)
                    {
                        currentType = args[0];
                    }
                }
            }

            return field?.GetCustomAttributes(typeof(T), true).Cast<T>().ToArray() ?? Array.Empty<T>();
        }

        /// <summary>沿继承链查找同名实例字段，包含各层的非公开字段。</summary>
        static FieldInfo GetFieldInHierarchy(Type type, string name)
        {
            while (type != null)
            {
                var fi = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null)
                {
                    return fi;
                }

                type = type.BaseType;
            }
            return null;
        }

        /// <summary>
        /// 收集当前属性的可见子属性，可选择递归收集各层后代。
        /// </summary>
        /// <param name="property">作为遍历边界的有效父属性。</param>
        /// <param name="recursive">是否递归进入可见子属性的后代。</param>
        /// <returns>仍依赖原 SerializedObject 的属性游标副本，非值快照；使用与释放由调用方管理。</returns>
        public static List<SerializedProperty> GetChildrens(this SerializedProperty property, bool recursive = false)
        {

            var list = new List<SerializedProperty>();

            if (!property.hasChildren)
            {
                // 没有子属性，直接返回空列表
                return list;
            }

            var iterator = property.Copy();
            var end = iterator.GetEndProperty();

            // 先进入子层，再用父属性的结束游标限界，避免把后续兄弟属性也收进结果。
            if (!iterator.NextVisible(true))
            {
                return list;
            }

            while (!SerializedProperty.EqualContents(iterator, end))
            {
                SerializedProperty p = iterator.Copy();

                list.Add(p);
                if (recursive && p.hasChildren)
                {
                    list.AddRange(p.GetChildrens(recursive));
                }

                if (!iterator.NextVisible(false))
                {
                    break;
                }
            }

            return list;

        }


        /// <summary>
        /// 判断属性是否为名为 m_Script、且引用有效 MonoScript 的脚本字段，供 Inspector 决定是否禁用编辑。
        /// </summary>
        /// <param name="property">待判断属性，可为 null。</param>
        /// <returns>属性名与有效脚本引用均匹配时为 true；缺失脚本引用返回 false。</returns>
        public static bool IsScript(this SerializedProperty property)
        {
            if (property == null)
            {
                return false;
            }

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                if (property.objectReferenceValue)
                {
                    if (property.objectReferenceValue is MonoScript && property.name == "m_Script")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

    }

}
