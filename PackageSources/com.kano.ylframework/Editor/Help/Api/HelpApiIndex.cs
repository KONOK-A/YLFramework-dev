using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace YLFramework.Editor.Help
{
    /// <summary>仅从编译元数据生成帮助声明，不构造对象或执行成员。</summary>
    public static class HelpApiFormatter
    {
        /// <summary>取得类型直接声明的 public/protected API，排除访问器、操作符和委托生成成员。</summary>
        /// <param name="type">要检查的实际类型；委托类型返回空集合。</param>
        /// <returns>该类型直接声明的可展示成员；顺序未定义，由调用方决定展示排序。</returns>
        /// <remarks>包含 protected internal 成员和非静态构造函数；不展开继承成员或嵌套类型，也不执行成员。</remarks>
        public static IEnumerable<MemberInfo> GetDeclaredApi(Type type)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return Array.Empty<MemberInfo>();
            }

            return type.GetMembers(Flags).Where(member =>
            {
                if (member is MethodBase method)
                {
                    return !method.IsSpecialName && Accessible(method) ||
                    method is ConstructorInfo && !method.IsStatic && Accessible(method);
                }

                if (member is PropertyInfo property)
                {
                    return property.GetAccessors(true).Any(Accessible);
                }

                if (member is FieldInfo field)
                {
                    return !field.IsSpecialName && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly);
                }

                if (member is EventInfo signal)
                {
                    return signal.GetAddMethod(true) != null && Accessible(signal.GetAddMethod(true));
                }

                return false;
            });
        }

        /// <summary>确认方法或访问器属于帮助窗口展示的 public、protected 或 protected internal 范围。</summary>
        private static bool Accessible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
        /// <summary>将已选定可见方法的访问级别转换为声明前缀。</summary>
        private static string Access(MethodBase method) => method.IsPublic ? "public " : method.IsFamilyOrAssembly ? "protected internal " : "protected ";

        /// <summary>格式化类型名；指定的模块命名空间使用短名，其他外部类型保留限定名。</summary>
        /// <param name="type">用于展示的实际类型，可包含嵌套类型、泛型或数组。</param>
        /// <param name="shortNamespaces">允许省略的完整命名空间；System 与 System.Collections.Generic 始终使用短名。</param>
        /// <returns>C# 风格的类型显示名；泛型实参与嵌套关系一并展开。</returns>
        public static string FormatType(Type type, params string[] shortNamespaces)
        {
            if (type.IsByRef)
            {
                return FormatType(type.GetElementType(), shortNamespaces);
            }

            if (type.IsArray)
            {
                return FormatType(type.GetElementType(), shortNamespaces) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            }

            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            if (type == typeof(void))
            {
                return "void";
            }

            if (type == typeof(bool))
            {
                return "bool";
            }

            if (type == typeof(int))
            {
                return "int";
            }

            if (type == typeof(ulong))
            {
                return "ulong";
            }

            if (type == typeof(float))
            {
                return "float";
            }

            if (type == typeof(double))
            {
                return "double";
            }

            if (type == typeof(string))
            {
                return "string";
            }

            if (type == typeof(object))
            {
                return "object";
            }

            var name = type.Name.Split('`')[0];
            var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
            var inherited = type.IsNested && type.DeclaringType.IsGenericType ? type.DeclaringType.GetGenericArguments().Length : 0;
            if (arguments.Length > inherited)
            {
                name += "<" + string.Join(", ", arguments.Skip(inherited).Select(argument => FormatType(argument, shortNamespaces))) + ">";
            }

            if (type.IsNested)
            {
                var declaring = type.DeclaringType;
                if (inherited != 0)
                {
                    declaring = declaring.MakeGenericType(arguments.Take(inherited).ToArray());
                }

                return FormatType(declaring, shortNamespaces) + "." + name;
            }
            return string.IsNullOrEmpty(type.Namespace) || type.Namespace == "System" || type.Namespace == "System.Collections.Generic" ||
                   shortNamespaces != null && shortNamespaces.Contains(type.Namespace) ? name : type.Namespace + "." + name;
        }

        /// <summary>格式化类型声明、基类、可见接口和泛型约束。</summary>
        /// <param name="type">要声明的实际类型；委托类型输出 delegate 声明。</param>
        /// <param name="shortNamespaces">允许省略的完整命名空间；System 与 System.Collections.Generic 始终使用短名。</param>
        /// <returns>以 public 开头的 C# 声明文本，包含基类、接口与 where 约束。</returns>
        public static string FormatDeclaration(Type type, params string[] shortNamespaces)
        {
            if (typeof(Delegate).IsAssignableFrom(type))
            {
                var invoke = type.GetMethod("Invoke");
                return "public delegate " + FormatType(invoke.ReturnType, shortNamespaces) + " " + FormatType(type, shortNamespaces) +
                       "(" + Parameters(invoke, shortNamespaces) + ");";
            }
            var kind = type.IsEnum ? "enum " : type.IsInterface ? "interface " : type.IsValueType ? "struct " : "class ";
            var modifiers = !type.IsInterface && !type.IsValueType ? type.IsAbstract && type.IsSealed ? "static " :
                type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "" : "";
            var bases = new List<Type>();
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                if (underlying != typeof(int))
                {
                    bases.Add(underlying);
                }
            }
            else
            {
                if (type.BaseType != null && type.BaseType != typeof(object) && !type.IsValueType)
                {
                    bases.Add(type.BaseType);
                }

                bases.AddRange(type.GetInterfaces().Where(contract => contract.IsVisible)
                    .OrderBy(contract => FormatType(contract, shortNamespaces), StringComparer.Ordinal));
            }
            return "public " + modifiers + kind + FormatType(type, shortNamespaces) +
                   (bases.Count == 0 ? "" : " : " + string.Join(", ", bases.Select(baseType => FormatType(baseType, shortNamespaces)))) +
                   Constraints(type.IsGenericType ? type.GetGenericArguments().Where(argument => argument.IsGenericParameter).ToArray() : Type.EmptyTypes, shortNamespaces);
        }

        /// <summary>格式化一个 API 成员的声明签名，不读取字段或属性的运行时值。</summary>
        /// <param name="member">要格式化的成员。</param>
        /// <param name="shortNamespaces">允许省略的完整命名空间；System 与 System.Collections.Generic 始终使用短名。</param>
        /// <returns>以分号或属性访问器块结尾的 C# 声明文本。</returns>
        /// <remarks>支持构造函数、方法、属性、字段和事件；常量值与可选参数默认值只读取编译元数据。</remarks>
        /// <exception cref="ArgumentException">member 不是受支持的成员种类（例如传入 Type）。</exception>
        public static string FormatSignature(MemberInfo member, params string[] shortNamespaces)
        {
            if (member is ConstructorInfo constructor)
            {
                return Access(constructor) + constructor.DeclaringType.Name.Split('`')[0] + "(" + Parameters(constructor, shortNamespaces) + ");";
            }

            if (member is MethodInfo method)
            {
                var generic = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
                return Access(method) + Modifiers(method) + FormatType(method.ReturnType, shortNamespaces) + " " + method.Name +
                    (generic.Length == 0 ? "" : "<" + string.Join(", ", generic.Select(argument => FormatType(argument, shortNamespaces))) + ">") +
                    "(" + Parameters(method, shortNamespaces) + ")" + Constraints(generic, shortNamespaces) + ";";
            }
            if (member is PropertyInfo property)
            {
                var accessors = property.GetAccessors(true);
                var primary = accessors.FirstOrDefault(method => method.IsPublic) ?? accessors.First(Accessible);
                var index = property.GetIndexParameters();
                string Accessor(MethodInfo method, string name) => method == null ? "" :
                    (method.IsPrivate ? "private " : method.IsAssembly ? "internal " :
                        Accessible(method) && Access(method) != Access(primary) ? Access(method) : "") + name + "; ";
                return Access(primary) + Modifiers(primary) + FormatType(property.PropertyType, shortNamespaces) + " " +
                    (index.Length == 0 ? property.Name : "this[" + string.Join(", ", index.Select(parameter =>
                        FormatType(parameter.ParameterType, shortNamespaces) + " " + parameter.Name)) + "]") +
                    " { " + Accessor(property.GetGetMethod(true), "get") + Accessor(property.GetSetMethod(true), "set") + "}";
            }
            if (member is FieldInfo field)
            {
                if (field.DeclaringType.IsEnum)
                {
                    return field.Name + " = " + Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) + ",";
                }

                return (field.IsPublic ? "public " : field.IsFamilyOrAssembly ? "protected internal " : "protected ") +
                    (field.IsLiteral ? "const " : field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : "") +
                    FormatType(field.FieldType, shortNamespaces) + " " + field.Name +
                    (field.IsLiteral ? " = " + Literal(field.GetRawConstantValue(), shortNamespaces) : "") + ";";
            }
            if (member is EventInfo signal)
            {
                var add = signal.GetAddMethod(true);
                return Access(add) + (add.IsStatic ? "static " : "") + "event " + FormatType(signal.EventHandlerType, shortNamespaces) + " " + signal.Name + ";";
            }
            throw new ArgumentException("不支持的 API 成员：" + member);
        }

        /// <summary>从方法元数据选择静态、覆写或虚方法修饰符，接口成员不额外展示隐含的 abstract。</summary>
        private static string Modifiers(MethodInfo method) => method.IsStatic ? "static " :
            method.GetBaseDefinition() != method ? (method.IsFinal ? "sealed override " : "override ") :
            method.IsAbstract && !method.DeclaringType.IsInterface ? "abstract " :
            method.IsVirtual && !method.IsFinal && !method.DeclaringType.IsInterface ? "virtual " : "";

        /// <summary>组织声明参数列表，从元数据读取扩展接收者、参数数组、引用方式与可选默认值。</summary>
        private static string Parameters(MethodBase method, string[] shortNamespaces)
        {
            var extension = method.IsDefined(typeof(ExtensionAttribute), false);
            return string.Join(", ", method.GetParameters().Select((parameter, index) =>
                (extension && index == 0 ? "this " : "") +
                (parameter.IsDefined(typeof(ParamArrayAttribute), false) ? "params " : parameter.ParameterType.IsByRef ? parameter.IsOut ? "out " : "ref " : "") +
                FormatType(parameter.ParameterType, shortNamespaces) + " " + parameter.Name +
                (parameter.HasDefaultValue ? " = " + Literal(parameter.DefaultValue, shortNamespaces) : "")));
        }

        /// <summary>按泛型参数元数据组织 where 子句，避免为 struct 再重复输出 ValueType 或 new()。</summary>
        private static string Constraints(Type[] arguments, string[] shortNamespaces)
        {
            var result = "";
            foreach (var argument in arguments)
            {
                var constraints = new List<string>();
                var flags = argument.GenericParameterAttributes;
                var value = (flags & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
                if (value)
                {
                    constraints.Add("struct");
                }
                else if ((flags & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                {
                    constraints.Add("class");
                }

                constraints.AddRange(argument.GetGenericParameterConstraints().Where(type => type != typeof(ValueType))
                    .Select(type => FormatType(type, shortNamespaces)));
                if (!value && (flags & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                {
                    constraints.Add("new()");
                }

                if (constraints.Count != 0)
                {
                    result += " where " + argument.Name + " : " + string.Join(", ", constraints);
                }
            }
            return result;
        }

        /// <summary>把编译元数据中的常量或默认值转为展示文本，使用固定区域格式避免本机语言影响数值。</summary>
        private static string Literal(object value, string[] shortNamespaces)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is string text)
            {
                return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }

            if (value is bool flag)
            {
                return flag ? "true" : "false";
            }

            if (value is float number)
            {
                return number.ToString("R", CultureInfo.InvariantCulture) + "f";
            }

            if (value is Enum)
            {
                return FormatType(value.GetType(), shortNamespaces) + "." + value;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>一个成员的只读声明、导航分组与合并后的实际说明。</summary>
    public sealed class HelpApiMember
    {
        /// <summary>原始编译元数据。</summary>
        public readonly MemberInfo Member;
        /// <summary>展示用 C# 签名。</summary>
        public readonly string Signature;
        /// <summary>成员类别的中文名称。</summary>
        public readonly string Group;
        /// <summary>元数据中的成员名。</summary>
        public readonly string Name;
        /// <summary>模块覆盖优先于 XML 的字段级合并说明。</summary>
        public readonly HelpDocumentation Help;

        /// <summary>为一个真实成员建立展示签名和类别，保存已由索引构建流程合并的说明。</summary>
        internal HelpApiMember(MemberInfo member, string[] shortNamespaces, HelpDocumentation help)
        {
            Member = member;
            Signature = HelpApiFormatter.FormatSignature(member, shortNamespaces);
            Name = member.Name;
            Group = member is ConstructorInfo ? "构造函数" : member is PropertyInfo ? "属性" : member is EventInfo ? "事件" :
                member is FieldInfo ? member.DeclaringType.IsEnum ? "枚举值" : "字段与常量" :
                member.IsDefined(typeof(ExtensionAttribute), false) ? "扩展方法" : "方法";
            Help = help;
        }

        /// <summary>签名、五类 XML 说明及其参数名组成的可搜索文本。</summary>
        public string SearchText => Signature + "\n" + HelpApiIndex.DocumentationText(Help);

        /// <summary>把成员名、所属类型、说明与签名按权重加入评分字段。</summary>
        /// <param name="fields">追加搜索字段的调用方缓冲；本方法不清空已有内容。</param>
        /// <param name="owner">声明类型的短名；为空时跳过。</param>
        /// <param name="includeDocumentation">false 时只加入名称类字段，用于判断查询词是否已被类型上下文覆盖。</param>
        internal void AppendSearchFields(List<HelpSearchField> fields, string owner, bool includeDocumentation = true)
        {
            fields.Add(new HelpSearchField(Name, HelpSearch.TitleWeight));
            if (!string.IsNullOrEmpty(owner))
            {
                fields.Add(new HelpSearchField(owner, HelpSearch.TechnicalWeight));
            }

            if (!includeDocumentation)
            {
                return;
            }

            fields.Add(new HelpSearchField(Help.Summary, HelpSearch.SummaryWeight));
            fields.Add(new HelpSearchField(HelpApiIndex.DocumentationNotes(Help), HelpSearch.DocWeight));
            fields.Add(new HelpSearchField(Signature, HelpSearch.TechnicalWeight));
        }
    }

    /// <summary>一个明确选定的类型及其直接声明的 API 索引。</summary>
    public sealed class HelpApiType
    {
        /// <summary>实际类型，不按简单名称猜测身份。</summary>
        public readonly Type Type;
        /// <summary>直接声明的 API，按签名的 Ordinal 顺序排列。</summary>
        public readonly MemberInfo[] Members;
        /// <summary>与 Members 一一对应的说明条目。</summary>
        public readonly HelpApiMember[] Entries;
        /// <summary>与 Members 一一对应的展示签名。</summary>
        public readonly string[] MemberSignatures;
        /// <summary>模块命名空间内的短类型名。</summary>
        public readonly string Name;
        /// <summary>模块指定的导航分组。</summary>
        public readonly string Group;
        /// <summary>合并后的类型摘要。</summary>
        public readonly string Summary;
        /// <summary>类型声明、基类、接口与约束。</summary>
        public readonly string Declaration;
        /// <summary>全部成员签名的展示文本。</summary>
        public readonly string Signatures;
        /// <summary>类型级摘要、备注和类型参数等实际说明。</summary>
        public readonly HelpDocumentation Help;
        /// <summary>RelatedTypes 明确关联到该实际 Type 的指南或关于页面。</summary>
        public readonly HelpPage[] RelatedPages;
        /// <summary>跨程序集不冲突的类型身份。</summary>
        public string Id => Type.AssemblyQualifiedName;

        /// <summary>为明确选定的类型建立直接声明成员索引，合并真实文档并按实际 Type 关联页面。</summary>
        internal HelpApiType(HelpApiTypeDefinition definition, string[] shortNamespaces, HelpPage[] pages,
            HelpXmlDocumentation xml, Func<MemberInfo, HelpDocumentation> documentation)
        {
            Type = definition.Type;
            Group = definition.Group;
            Name = HelpApiFormatter.FormatType(Type, shortNamespaces);
            Declaration = HelpApiFormatter.FormatDeclaration(Type, shortNamespaces);
            Help = HelpXmlDocumentation.Merge(xml.Get(Type), documentation?.Invoke(Type));
            Summary = Help.Summary;
            Members = HelpApiFormatter.GetDeclaredApi(Type)
                .OrderBy(member => HelpApiFormatter.FormatSignature(member, shortNamespaces), StringComparer.Ordinal).ToArray();
            Entries = Members.Select(member => new HelpApiMember(member, shortNamespaces,
                HelpXmlDocumentation.Merge(xml.Get(member), documentation?.Invoke(member)))).ToArray();
            MemberSignatures = Entries.Select(member => member.Signature).ToArray();
            Signatures = string.Join("\n\n", MemberSignatures);
            RelatedPages = pages.Where(page => page.RelatedTypes.Contains(Type)).ToArray();
        }

        /// <summary>按空白分词 AND 搜索声明、说明及关联页面。</summary>
        /// <param name="search">查询文本；null、空或全空白匹配所有类型。</param>
        /// <returns>所有分词都出现在类型可搜索文本中时为 true。</returns>
        public bool Matches(string search) => HelpSearch.Contains(SearchText, search);

        /// <summary>把类型名、限定名、分组、说明与声明按权重加入评分字段；可选纳入成员字段供全局搜索。</summary>
        /// <param name="fields">追加搜索字段的调用方缓冲；本方法不清空已有内容。</param>
        /// <param name="includeMembers">true 时加入成员名、成员说明与签名，使命中成员的查询能显示该类型。</param>
        /// <param name="includeDocumentation">false 时只加入名称与声明等身份字段，用于判断查询词是否已被类型上下文覆盖。</param>
        internal void AppendSearchFields(List<HelpSearchField> fields, bool includeMembers, bool includeDocumentation = true)
        {
            fields.Add(new HelpSearchField(Name, HelpSearch.TitleWeight));
            fields.Add(new HelpSearchField(Type.Namespace == null ? Type.Name : Type.Namespace + "." + Type.Name, HelpSearch.TechnicalWeight));
            if (includeDocumentation)
            {
                fields.Add(new HelpSearchField(Group, HelpSearch.KeywordWeight));
                fields.Add(new HelpSearchField(Summary, HelpSearch.SummaryWeight));
                fields.Add(new HelpSearchField(HelpApiIndex.DocumentationNotes(Help), HelpSearch.DocWeight));
            }
            fields.Add(new HelpSearchField(Declaration, HelpSearch.TechnicalWeight));
            if (!includeMembers)
            {
                return;
            }

            foreach (var entry in Entries)
            {
                fields.Add(new HelpSearchField(entry.Name, HelpSearch.MemberNameWeight));
                fields.Add(new HelpSearchField(entry.Help.Summary, HelpSearch.DocWeight));
                fields.Add(new HelpSearchField(HelpApiIndex.DocumentationNotes(entry.Help), HelpSearch.DocWeight));
                fields.Add(new HelpSearchField(entry.Signature, HelpSearch.TechnicalWeight));
            }
        }

        /// <summary>类型上下文之外的查询词须由成员匹配；纯类型查询不高亮所有成员。</summary>
        /// <param name="member">同一类型下的成员条目。</param>
        /// <param name="search">查询文本。</param>
        /// <returns>查询词中未被类型上下文覆盖的部分全部命中该成员时为 true；没有这类剩余词时为 false。</returns>
        public bool MemberMatches(HelpApiMember member, string search)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return false;
            }

            var context = Type.Namespace + "\n" + Name + "\n" + Declaration;
            var terms = search.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var memberTerms = terms.Where(term => !HelpSearch.Contains(context, term)).ToArray();
            return memberTerms.Length != 0 && memberTerms.All(term => HelpSearch.Contains(member.SearchText, term));
        }

        private string SearchText => Type.Namespace + "\n" + Name + "\n" + Declaration + "\n" + HelpApiIndex.DocumentationText(Help) + "\n" +
            string.Join("\n", Entries.Select(entry => entry.SearchText)) + "\n" + string.Join("\n", RelatedPages.Select(page => page.SearchText));
    }

    /// <summary>建立一次模块索引；仅处理提供方选定的类型，不扫描项目。</summary>
    public static class HelpApiIndex
    {
        /// <summary>保持模块类型顺序，成员按签名排序；XML 缓存和警告仅属于本次构建。</summary>
        /// <param name="module">非 null 的模块内容，由提供方明确指定 API 类型和文档来源。</param>
        /// <returns>本次构建的新索引；类型身份和页面关联使用实际 Type，不按简单名称匹配。</returns>
        /// <remarks>XML 缺失时仍保留声明；文档覆盖回调的异常直接交由调用方处理。</remarks>
        /// <exception cref="ArgumentNullException">module 为 null。</exception>
        public static HelpApiType[] Build(HelpModule module)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            var definitions = module.ApiTypes ?? Array.Empty<HelpApiTypeDefinition>();
            var shortNamespaces = definitions.Select(definition => definition.Type.Namespace).Distinct().ToArray();
            var pages = (module.Guides ?? Array.Empty<HelpPage>()).Concat(module.About ?? Array.Empty<HelpPage>()).ToArray();
            var xml = new HelpXmlDocumentation(module);
            return definitions.Select(definition => new HelpApiType(definition, shortNamespaces, pages, xml, module.Documentation)).ToArray();
        }

        /// <summary>组合摘要与其他已提供的说明，供字面搜索使用，不补造缺失文档。</summary>
        internal static string DocumentationText(HelpDocumentation help) => help.Summary + "\n" + DocumentationNotes(help);

        /// <summary>摘要之外的说明文本：返回值、备注与参数/类型参数说明。</summary>
        internal static string DocumentationNotes(HelpDocumentation help) => help.Returns + "\n" + help.Remarks + "\n" +
            string.Join("\n", help.Parameters.Concat(help.TypeParameters).Select(parameter => parameter.Name + " " + parameter.Description));
    }
}
