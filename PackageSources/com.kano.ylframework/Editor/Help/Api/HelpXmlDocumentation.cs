using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UnityEngine;

namespace YLFramework.Editor.Help
{
    /// <summary>按程序集读取 XML，并通过编译器成员标识映射 Reflection 元数据。</summary>
    /// <remarks>每个读取器仅服务一次索引构建；缺失文件与警告缓存不会跨构建复用。</remarks>
    internal sealed class HelpXmlDocumentation
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly HelpModule _module;
        private readonly Dictionary<Assembly, Dictionary<string, XElement>> _assemblies = new Dictionary<Assembly, Dictionary<string, XElement>>();
        private readonly Dictionary<string, XDocument> _files = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>保存本次索引的显式文档来源与类型范围，读取和缓存留到成员首次查询时进行。</summary>
        internal HelpXmlDocumentation(HelpModule module)
        {
            _module = module;
        }

        /// <summary>读取一个成员的 XML 说明并解析其 inheritdoc；缺失条目返回空文档。</summary>
        internal HelpDocumentation Get(MemberInfo member)
        {
            var normalized = Normalize(member);
            return Resolve(normalized.Module.Assembly, MemberId(normalized), normalized, new HashSet<string>(StringComparer.Ordinal));
        }

        /// <summary>在指定程序集的 XML 条目中解析该成员，并按 inheritdoc 的 cref 或基类/接口补足说明。</summary>
        private HelpDocumentation Resolve(Assembly assembly, string id, MemberInfo member, HashSet<string> visiting)
        {
            var key = assembly.FullName + "\n" + id;
            // visiting 记录当前解析路径，自引用或循环 inheritdoc 直接返回空文档。
            if (!visiting.Add(key))
            {
                return HelpDocumentation.Empty;
            }

            try
            {
                var entries = Entries(assembly);
                if (!entries.TryGetValue(id, out var entry))
                {
                    return HelpDocumentation.Empty;
                }

                var own = new HelpDocumentation(Text(entry.Element("summary")), Text(entry.Element("returns")), Text(entry.Element("remarks")),
                    Parameters(entry, "param"), Parameters(entry, "typeparam"));
                foreach (var inherit in entry.Elements("inheritdoc"))
                {
                    if (!string.IsNullOrWhiteSpace((string)inherit.Attribute("path")))
                    {
                        continue;
                    }

                    var cref = (string)inherit.Attribute("cref");
                    if (!string.IsNullOrWhiteSpace(cref))
                    {
                        var candidates = new[] { assembly }.Concat((_module.ApiTypes ?? Array.Empty<HelpApiTypeDefinition>())
                            .Select(definition => definition.Type.Assembly));
                        if (member != null)
                        {
                            candidates = candidates.Concat(InheritedMembers(member).Select(target => target.Module.Assembly));
                        }

                        foreach (var source in candidates.Distinct())
                        {
                            if (!Entries(source).ContainsKey(cref))
                            {
                                continue;
                            }

                            own = Merge(Resolve(source, cref, FindKnownMember(source, cref, member), visiting), own);
                            break;
                        }
                    }
                    else if (member != null)
                    {
                        var inherited = HelpDocumentation.Empty;
                        foreach (var target in InheritedMembers(member))
                        {
                            var normalized = Normalize(target);
                            // 先解析的基类说明优先，接口说明只补足缺失字段。
                            inherited = Merge(Resolve(normalized.Module.Assembly, MemberId(normalized), normalized, visiting), inherited);
                        }
                        own = Merge(inherited, own);
                    }
                }
                return own;
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        // cref 只给出成员标识，需要在模块选定类型与当前上下文的类型中找回对应的 Reflection 成员。
        private MemberInfo FindKnownMember(Assembly assembly, string id, MemberInfo context)
        {
            var types = (_module.ApiTypes ?? Array.Empty<HelpApiTypeDefinition>()).Select(definition => definition.Type);
            if (context != null)
            {
                types = types.Concat(new[] { context as Type ?? context.DeclaringType })
                    .Concat(InheritedMembers(context).Select(target => target as Type ?? target.DeclaringType));
            }

            foreach (var type in types.Distinct().Where(type => type != null && type.Assembly == assembly))
            {
                if (MemberId(type) == id)
                {
                    return Normalize(type);
                }

                foreach (var candidate in type.GetMembers(AllMembers | BindingFlags.DeclaredOnly))
                {
                    if (!(candidate is Type) && MemberId(candidate) == id)
                    {
                        return Normalize(candidate);
                    }
                }
            }
            return null;
        }

        /// <summary>惰性读取并缓存程序集的 XML 条目；无可用文档时保持空表，不生成成员说明。</summary>
        private Dictionary<string, XElement> Entries(Assembly assembly)
        {
            if (_assemblies.TryGetValue(assembly, out var cached))
            {
                return cached;
            }

            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            _assemblies.Add(assembly, result);
            XDocument document = null;
            string path = null;
            var missing = true;
            if (_module.XmlPaths != null && _module.XmlPaths.TryGetValue(assembly, out var explicitPath) && !string.IsNullOrWhiteSpace(explicitPath))
            {
                path = explicitPath;
                document = Load(path, out missing);
            }
            if (missing && !assembly.IsDynamic)
            {
                // 仅文件缺失时查找 DLL 邻接文档；格式错误或访问失败不能被备用路径掩盖。
                var location = assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    path = Path.ChangeExtension(location, ".xml");
                    document = Load(path, out missing);
                }
            }
            if (document == null)
            {
                return result;
            }
            // 只接受结构完整且 assembly/name 与当前程序集一致的文档，避免误用同名或损坏的 XML。
            var root = document.Root;
            if (root?.Name != "doc" || root.Element("members") == null ||
                (string)root.Element("assembly")?.Element("name") != assembly.GetName().Name)
            {
                Warn(Path.GetFullPath(path), "文档结构或程序集名称不匹配");
                return result;
            }
            foreach (var entry in root.Element("members").Elements("member"))
            {
                var id = (string)entry.Attribute("name");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result[id] = entry;
                }
            }
            return result;
        }

        // 成功与失败都按完整路径缓存，避免重复 IO；missing 决定是否继续尝试 DLL 邻接文档。
        private XDocument Load(string path, out bool missing)
        {
            missing = false;
            var key = path;
            try
            {
                key = Path.GetFullPath(path);
                if (_files.TryGetValue(key, out var cached))
                {
                    missing = _missing.Contains(key);
                    return cached;
                }
                // 即使读取的是项目内文件，也禁用 DTD 与外部解析器，避免解析外部实体。
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var stream = File.OpenRead(key))
                using (var reader = XmlReader.Create(stream, settings))
                {
                    var document = XDocument.Load(reader);
                    _files[key] = document;
                    return document;
                }
            }
            catch (FileNotFoundException)
            {
                missing = true;
                _missing.Add(key);
            }
            catch (DirectoryNotFoundException)
            {
                missing = true;
                _missing.Add(key);
            }
            catch (XmlException exception)
            {
                Warn(key, exception.Message);
            }
            catch (IOException exception)
            {
                Warn(key, exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                Warn(key, exception.Message);
            }
            catch (SecurityException exception)
            {
                Warn(key, exception.Message);
            }
            catch (ArgumentException exception)
            {
                Warn(key, exception.Message);
            }
            catch (NotSupportedException exception)
            {
                Warn(key, exception.Message);
            }
            _files[key] = null;
            return null;
        }

        /// <summary>对本次索引内同一路径只报告一次读取或结构警告，不影响反射声明索引。</summary>
        private void Warn(string path, string reason)
        {
            if (_warnings.Add(path))
            {
                Debug.LogWarning("HelpKit 无法读取 XML 文档，已保留 API 声明：" + path + "\n" + reason);
            }
        }

        /// <summary>非空白覆盖优先于 XML；参数和类型参数按名称补充或替换，不清空已有说明。</summary>
        internal static HelpDocumentation Merge(HelpDocumentation xml, HelpDocumentation overrides)
        {
            xml = xml ?? HelpDocumentation.Empty;
            if (overrides == null)
            {
                return xml;
            }

            string Field(string original, string replacement) => string.IsNullOrWhiteSpace(replacement) ? original : replacement;
            HelpParameter[] Named(HelpParameter[] original, HelpParameter[] replacement)
            {
                var result = original.ToList();
                foreach (var parameter in replacement)
                {
                    if (string.IsNullOrWhiteSpace(parameter.Name) || string.IsNullOrWhiteSpace(parameter.Description))
                    {
                        continue;
                    }

                    var index = result.FindIndex(item => item.Name == parameter.Name);
                    if (index < 0)
                    {
                        result.Add(parameter);
                    }
                    else
                    {
                        result[index] = parameter;
                    }
                }
                return result.ToArray();
            }
            return new HelpDocumentation(Field(xml.Summary, overrides.Summary), Field(xml.Returns, overrides.Returns),
                Field(xml.Remarks, overrides.Remarks), Named(xml.Parameters, overrides.Parameters), Named(xml.TypeParameters, overrides.TypeParameters));
        }

        /// <summary>提取带非空名称的 param 或 typeparam 说明，供后续按名称合并。</summary>
        private static HelpParameter[] Parameters(XElement entry, string tag) => entry.Elements(tag)
            .Where(element => !string.IsNullOrWhiteSpace((string)element.Attribute("name")))
            .Select(element => new HelpParameter((string)element.Attribute("name"), Text(element))).ToArray();

        /// <summary>把说明节点折叠成纯文本：合并空白、去掉空行，see/paramref 等内联标签取可读名称。</summary>
        private static string Text(XElement element)
        {
            if (element == null)
            {
                return "";
            }

            var text = new StringBuilder();
            void Append(XNode node)
            {
                if (node is XText value)
                {
                    text.Append(Regex.Replace(value.Value, @"\s+", " "));
                    return;
                }
                if (!(node is XElement child))
                {
                    return;
                }

                var tag = child.Name.LocalName;
                if (tag == "para")
                {
                    text.Append('\n');
                }

                if ((tag == "see" || tag == "seealso") && !child.Nodes().Any())
                {
                    var reference = (string)child.Attribute("langword") ?? (string)child.Attribute("cref") ?? (string)child.Attribute("href") ?? "";
                    text.Append(reference.Length > 1 && reference[1] == ':' ? reference.Substring(2) : reference);
                }
                else if (tag == "paramref" || tag == "typeparamref")
                {
                    text.Append((string)child.Attribute("name"));
                }
                else
                {
                    foreach (var nested in child.Nodes())
                    {
                        Append(nested);
                    }
                }

                if (tag == "para")
                {
                    text.Append('\n');
                }
            }
            foreach (var node in element.Nodes())
            {
                Append(node);
            }

            return string.Join("\n", text.ToString().Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0));
        }

        /// <summary>生成编译器 XML 成员标识，区分成员种类、泛型参数、重载与按引用参数。</summary>
        internal static string MemberId(MemberInfo member)
        {
            member = Normalize(member);
            if (member is Type type)
            {
                return "T:" + TypeName(type);
            }

            var prefix = member is MethodBase ? "M:" : member is PropertyInfo ? "P:" : member is FieldInfo ? "F:" : member is EventInfo ? "E:" : null;
            if (prefix == null)
            {
                throw new ArgumentException("不支持的 XML 文档成员：" + member, nameof(member));
            }

            var name = member is ConstructorInfo constructor ? constructor.IsStatic ? "#cctor" : "#ctor" :
                member.Name.Replace('.', '#').Replace('<', '{').Replace('>', '}');
            if (member is MethodInfo method && method.IsGenericMethod)
            {
                name += "``" + method.GetGenericArguments().Length;
            }

            var parameters = member is MethodBase callable ? callable.GetParameters() : member is PropertyInfo property ? property.GetIndexParameters() : Array.Empty<ParameterInfo>();
            var id = prefix + TypeName(member.DeclaringType) + "." + name +
                (parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(parameter => ParameterTypeName(parameter.ParameterType))) + ")");
            if (member is MethodInfo conversion && (conversion.Name == "op_Implicit" || conversion.Name == "op_Explicit"))
            {
                id += "~" + ParameterTypeName(conversion.ReturnType);
            }

            return id;
        }

        /// <summary>将闭合泛型成员还原为定义声明，以匹配 XML 记录的身份。</summary>
        private static MemberInfo Normalize(MemberInfo member)
        {
            // XML 记录的是泛型定义；闭合类型或方法通过同模块的 MetadataToken 找回原声明。
            if (member is Type type)
            {
                return type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
            }

            if (member is MethodInfo method && method.IsGenericMethod && !method.IsGenericMethodDefinition)
            {
                member = method.GetGenericMethodDefinition();
            }

            var declaring = member.DeclaringType;
            if (declaring != null && declaring.IsGenericType && !declaring.IsGenericTypeDefinition)
            {
                return declaring.GetGenericTypeDefinition().GetMembers(AllMembers | BindingFlags.DeclaredOnly)
                    .First(candidate => candidate.Module == member.Module && candidate.MetadataToken == member.MetadataToken);
            }

            return member;
        }

        /// <summary>生成 XML 成员所属类型名，保留泛型定义元数并将嵌套类型分隔符转换为点号。</summary>
        private static string TypeName(Type type) => (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName.Replace('+', '.');

        /// <summary>编码 XML 参数类型，区分引用、数组及类型/方法的泛型参数。</summary>
        private static string ParameterTypeName(Type type)
        {
            // XML 参数类型编码不同于展示签名：按引用用 @，类型和方法泛型参数分别用 ` 与 ``。
            if (type.IsByRef)
            {
                return ParameterTypeName(type.GetElementType()) + "@";
            }

            if (type.IsPointer)
            {
                return ParameterTypeName(type.GetElementType()) + "*";
            }

            if (type.IsArray)
            {
                return ParameterTypeName(type.GetElementType()) + (type == type.GetElementType().MakeArrayType() ? "[]" :
                    "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]");
            }

            if (type.IsGenericParameter)
            {
                return (type.DeclaringMethod == null ? "`" : "``") + type.GenericParameterPosition;
            }

            if (!type.IsGenericType)
            {
                return TypeName(type);
            }

            var arguments = type.GetGenericArguments();
            var inherited = type.IsNested && type.DeclaringType.IsGenericType ? type.DeclaringType.GetGenericArguments().Length : 0;
            var declaring = type.DeclaringType;
            if (inherited != 0)
            {
                declaring = declaring.MakeGenericType(arguments.Take(inherited).ToArray());
            }

            var name = (type.IsNested ? ParameterTypeName(declaring) + "." : string.IsNullOrEmpty(type.Namespace) ? "" : type.Namespace + ".") + type.Name.Split('`')[0];
            return name + (arguments.Length == inherited ? "" : "{" + string.Join(",", arguments.Skip(inherited).Select(ParameterTypeName)) + "}");
        }

        // 去掉由基类或父接口重复引入的接口，只留下当前类型直接引入的部分。
        private static IEnumerable<Type> DirectInterfaces(Type type)
        {
            var interfaces = type.GetInterfaces();
            var inherited = interfaces.SelectMany(contract => contract.GetInterfaces())
                .Concat(type.BaseType == null ? Type.EmptyTypes : type.BaseType.GetInterfaces());
            return interfaces.Except(inherited);
        }

        /// <summary>按基类优先、接口随后枚举 inheritdoc 候选；属性和事件经访问器映射回所属成员。</summary>
        private static IEnumerable<MemberInfo> InheritedMembers(MemberInfo member)
        {
            if (member is Type type)
            {
                if (type.BaseType != null)
                {
                    yield return type.BaseType;
                }

                foreach (var contract in DirectInterfaces(type))
                {
                    yield return contract;
                }

                yield break;
            }
            var owner = member.DeclaringType;
            var accessors = member is MethodInfo method ? new[] { method } : member is PropertyInfo property ? property.GetAccessors(true) :
                member is EventInfo signal ? new[] { signal.GetAddMethod(true), signal.GetRemoveMethod(true) }.Where(accessor => accessor != null).ToArray() : Array.Empty<MethodInfo>();
            foreach (var accessor in accessors)
            {
                if (owner.BaseType != null)
                {
                    var root = accessor.GetBaseDefinition();
                    if (root != accessor)
                    {
                        var source = owner.BaseType.GetMethods(AllMembers).FirstOrDefault(candidate => candidate.GetBaseDefinition() == root);
                        var target = OwningMember(source, member);
                        if (target != null)
                        {
                            yield return target;
                        }
                    }
                }
                // 接口映射只包含当前接口的声明，父接口成员需逐个接口继续查找。
                foreach (var contract in DirectInterfaces(owner).Concat(owner.GetInterfaces()).Distinct())
                {
                    MethodInfo source = null;
                    if (owner.IsInterface)
                    {
                        source = contract.GetMethods().FirstOrDefault(candidate => candidate.Name == accessor.Name &&
                            candidate.GetGenericArguments().Length == accessor.GetGenericArguments().Length &&
                            candidate.GetParameters().Select(parameter => ParameterTypeName(parameter.ParameterType))
                                .SequenceEqual(accessor.GetParameters().Select(parameter => ParameterTypeName(parameter.ParameterType))));
                    }
                    else
                    {
                        var map = owner.GetInterfaceMap(contract);
                        var index = Array.IndexOf(map.TargetMethods, accessor);
                        if (index >= 0)
                        {
                            source = map.InterfaceMethods[index];
                        }
                    }
                    var target = OwningMember(source, member);
                    if (target != null)
                    {
                        yield return target;
                    }
                }
            }
        }

        /// <summary>将反射映射得到的访问器还原为对应的属性、事件或方法声明。</summary>
        private static MemberInfo OwningMember(MethodInfo accessor, MemberInfo original)
        {
            if (accessor == null)
            {
                return null;
            }

            if (original is MethodInfo)
            {
                return accessor;
            }

            if (original is PropertyInfo)
            {
                return accessor.DeclaringType.GetProperties(AllMembers)
                .FirstOrDefault(property => property.GetAccessors(true).Contains(accessor));
            }

            if (original is EventInfo)
            {
                return accessor.DeclaringType.GetEvents(AllMembers)
                .FirstOrDefault(signal => signal.GetAddMethod(true) == accessor || signal.GetRemoveMethod(true) == accessor);
            }

            return null;
        }
    }
}
