using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Help;
using Assembly = System.Reflection.Assembly;

namespace YLFramework.Tests.Help
{
    public sealed class ApiTests
    {
        private string _folder;
        private static readonly Type _fixture = typeof(Fixtures.ApiFixture<>);
        private static readonly string[] _shortNamespaces = { "YLFramework.Tests.Help.Fixtures", "YLFramework.Tests.Help.Other" };

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "kilo",
                "HelpKitTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_folder));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_folder)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_folder), true);
            }
        }

        private HelpModule Module(params Type[] types) => new HelpModule
        {
            ApiTypes = types.Select(type => new HelpApiTypeDefinition(type, "测试 API")).ToArray(),
            XmlPaths = types.Select(type => type.Assembly).Distinct().ToDictionary(assembly => assembly,
                assembly => Path.Combine(_folder, "missing-" + assembly.GetName().Name + ".xml"))
        };

        private string Xml(Assembly assembly, params XElement[] members)
        {
            var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".xml");
            new XDocument(new XElement("doc", new XElement("assembly", new XElement("name", assembly.GetName().Name)),
                new XElement("members", members))).Save(path);
            return path;
        }

        private static XElement Entry(MemberInfo member, params object[] tags) => new XElement("member",
            new XAttribute("name", HelpXmlDocumentation.MemberId(member)), tags);

        [Test]
        public void FormatterPreservesVisibilityAndDeclaredOnlyMemberCategories()
        {
            var members = HelpApiFormatter.GetDeclaredApi(_fixture).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "Run", "Protected", "ProtectedInternal", "Value", "Item", "Changed", "Constant", "Readonly", ".ctor" },
                members.Select(member => member.Name).ToArray());
            Assert.That(members.All(member => member.DeclaringType == _fixture), Is.True);
            Assert.That(members.Any(member => member.Name == "Inherited" || member.Name == "Hidden" || member.Name == "Internal" ||
                                            member.Name.StartsWith("get_", StringComparison.Ordinal) || member.Name.StartsWith("add_", StringComparison.Ordinal) ||
                                            member.Name.StartsWith("op_", StringComparison.Ordinal) || member is Type), Is.False);
            Assert.That(members.OfType<ConstructorInfo>().All(constructor => !constructor.IsStatic), Is.True);
            Assert.That(HelpApiFormatter.GetDeclaredApi(typeof(Fixtures.HelpDelegate)), Is.Empty);
        }

        [Test]
        public void FormatterUsesAllSelectedNamespacesAndHandlesGlobalAndNestedGenerics()
        {
            Assert.That(HelpApiFormatter.FormatType(typeof(HelpKitGlobalFixture)), Is.EqualTo("HelpKitGlobalFixture"));
            Assert.That(HelpApiFormatter.FormatType(typeof(Fixtures.Outer<int>.Inner<string>), _shortNamespaces), Is.EqualTo("Outer<int>.Inner<string>"));
            Assert.That(HelpApiFormatter.FormatType(typeof(Fixtures.Outer<int>.Leaf), _shortNamespaces), Is.EqualTo("Outer<int>.Leaf"));
            Assert.That(HelpApiFormatter.FormatType(typeof(Dictionary<string, int[,]>)), Is.EqualTo("Dictionary<string, int[,]>"));
            Assert.That(HelpApiFormatter.FormatType(typeof(Other.ApiFixture), _shortNamespaces), Is.EqualTo("ApiFixture"));
            Assert.That(HelpApiFormatter.FormatType(typeof(Other.ApiFixture)), Is.EqualTo("YLFramework.Tests.Help.Other.ApiFixture"));
            Assert.That(HelpApiFormatter.FormatType(typeof(UnityEngine.Vector3), _shortNamespaces), Is.EqualTo("UnityEngine.Vector3"));
            var index = HelpApiIndex.Build(Module(_fixture, typeof(Other.ApiFixture)));
            Assert.That(index[0].Declaration, Does.Not.Contain("Run"), "The type declaration must not include member bodies.");
            Assert.That(index[1].Name, Is.EqualTo("ApiFixture"));
            Assert.That(index[0].Entries.Single(entry => entry.Name == "OtherModule").Signature, Is.EqualTo("public void OtherModule(ApiFixture other);"));
        }

        [Test]
        public void DeclarationsAndSignaturesRetainConstraintsDefaultsAndMetadataOnlyBehavior()
        {
            var declaration = HelpApiFormatter.FormatDeclaration(_fixture, _shortNamespaces);
            Assert.That(declaration, Does.StartWith("public class ApiFixture<T> : ApiBase"));
            Assert.That(declaration, Does.Contain("IApiContract"));
            Assert.That(declaration, Does.EndWith("where T : class, new()"));
            Assert.That(HelpApiFormatter.FormatDeclaration(typeof(Fixtures.HelpDelegate), _shortNamespaces),
                Is.EqualTo("public delegate int HelpDelegate(string text);"));
            Assert.That(HelpApiFormatter.FormatSignature(_fixture.GetMethod("Run"), _shortNamespaces),
                Is.EqualTo("public int Run(int value, string label = \"demo\");"));
            Assert.That(HelpApiFormatter.FormatSignature(_fixture.GetMethod("Convert"), _shortNamespaces),
                Is.EqualTo("public U Convert<U>(T input, ref U output) where U : struct;"));
            Assert.That(HelpApiFormatter.FormatSignature(_fixture.GetProperty("Value"), _shortNamespaces),
                Is.EqualTo("public int Value { get; private set; }"));
            Assert.That(HelpApiFormatter.FormatSignature(_fixture.GetProperty("Item"), _shortNamespaces),
                Is.EqualTo("public int this[int position] { get; }"));
            Assert.That(HelpApiFormatter.FormatSignature(_fixture.GetField("Constant"), _shortNamespaces), Is.EqualTo("public const string Constant = \"fixture\";"));
            Assert.That(HelpApiFormatter.FormatSignature(_fixture.GetField("Readonly"), _shortNamespaces), Is.EqualTo("public static readonly int Readonly;"));
            Assert.That(HelpApiFormatter.FormatSignature(typeof(Fixtures.Extensions).GetMethod("Inspect"), _shortNamespaces),
                Is.EqualTo("public static void Inspect(this ApiBase value, params string[] labels);"));
            Assert.That(HelpApiFormatter.FormatSignature(typeof(Fixtures.FixtureEnum).GetField("Two"), _shortNamespaces), Is.EqualTo("Two = 2,"));
            // The fixture initializer, constructor, getter and methods throw if executed.
            Assert.DoesNotThrow(() => HelpApiIndex.Build(Module(_fixture)));
        }

        [Test]
        public void IndexPreservesTypeOrderSortsSignaturesAndAssociatesActualTypes()
        {
            var module = Module(typeof(Other.ApiFixture), _fixture, typeof(Fixtures.Outer<>.Inner<>));
            var exact = new HelpPage("exact", "准确关联", "指南", "嵌套 API") { RelatedTypes = new[] { _fixture } };
            var other = new HelpPage("other", "同名不关联", "指南", "") { RelatedTypes = new[] { typeof(Other.ApiFixture) } };
            var featuredOnly = new HelpPage("featured", "精选不等于关联", "指南", "") { FeaturedTypes = new[] { _fixture } };
            module.Guides = new[] { exact, other, featuredOnly };
            module.About = new[] { new HelpPage("about", "关于关联", "关于", "") { RelatedTypes = new[] { _fixture } } };
            var index = HelpApiIndex.Build(module);
            CollectionAssert.AreEqual(module.ApiTypes.Select(definition => definition.Type).ToArray(), index.Select(type => type.Type).ToArray());
            CollectionAssert.AreEqual(index[1].MemberSignatures.OrderBy(signature => signature, StringComparer.Ordinal).ToArray(), index[1].MemberSignatures);
            CollectionAssert.AreEqual(new[] { "exact", "about" }, index[1].RelatedPages.Select(page => page.Id).ToArray());
            Assert.That(index[2].RelatedPages, Is.Empty, "Nested types are not implicitly associated with their parents.");
            Assert.That(index[1].Id, Is.EqualTo(_fixture.AssemblyQualifiedName));
            Assert.That(index[1].Entries.Select(entry => entry.Member).ToArray(), Is.EqualTo(index[1].Members));
        }

        [Test]
        public void SearchAndsChineseAndEnglishAndDoesNotHighlightPureTypeQueries()
        {
            var module = Module(_fixture);
            module.Documentation = member => member == _fixture ? new HelpDocumentation("通用测试类型", null, "类型备注", null,
                new[] { new HelpParameter("T", "泛型数据") }) : member.Name == "Run" ? new HelpDocumentation("执行并取消", "执行结果", null,
                new[] { new HelpParameter("value", "计数参数") }, new[] { new HelpParameter("U", "成员类型说明") }) : null;
            module.Guides = new[] { new HelpPage("guide", "指南联动", "指南", "停止屏幕") { RelatedTypes = new[] { _fixture } } };
            var api = HelpApiIndex.Build(module).Single();
            var run = api.Entries.Single(entry => entry.Name == "Run");
            Assert.That(api.Matches("apifixture \t取消"), Is.True);
            Assert.That(api.Matches("APIFIXTURE 屏幕"), Is.True);
            Assert.That(api.Matches("泛型数据 类型备注"), Is.True);
            Assert.That(api.Matches("执行 未出现"), Is.False);
            Assert.That(api.MemberMatches(run, "ApiFixture"), Is.False);
            Assert.That(api.MemberMatches(run, "ApiFixture RUN 取消"), Is.True);
            Assert.That(api.MemberMatches(run, "ApiFixture 屏幕"), Is.False);
            Assert.That(api.MemberMatches(run, "成员类型说明 计数参数"), Is.True);
            Assert.That(api.MemberMatches(run, " \t"), Is.False);
            Assert.That(HelpSearch.Contains(null, ""), Is.True);
            Assert.That(HelpSearch.Contains(null, "word"), Is.False);
            Assert.That(module.Guides[0].Matches("指南 屏幕"), Is.True);
        }

        [Test]
        public void MissingDocumentationIsEmptyAndModuleBuildsDoNotShareCaches()
        {
            var first = Module(_fixture);
            var absent = HelpApiIndex.Build(first).Single();
            Assert.That(absent.Help.Summary, Is.Empty);
            Assert.That(absent.Help.Parameters, Is.Empty);
            Assert.That(absent.Entries.All(entry => entry.Help.Summary.Length == 0 && entry.Help.Parameters.Length == 0), Is.True);
            var path = first.XmlPaths[_fixture.Assembly];
            new XDocument(new XElement("doc", new XElement("assembly", new XElement("name", _fixture.Assembly.GetName().Name)),
                new XElement("members", Entry(_fixture, new XElement("summary", "后来生成"))))).Save(path);
            Assert.That(HelpApiIndex.Build(first).Single().Summary, Is.EqualTo("后来生成"));
            var second = Module(_fixture);
            second.Documentation = member => new HelpDocumentation("另一模块", null, null, null);
            Assert.That(HelpApiIndex.Build(second).Single().Summary, Is.EqualTo("另一模块"));
            Assert.That(HelpApiIndex.Build(first).Single().Matches("另一模块"), Is.False);
            Assert.That(HelpApiIndex.Build(new HelpModule()), Is.Empty);
        }

        [Test]
        public void XmlReadsFiveTagsInlineNamesParagraphsAndSparseOverridesByName()
        {
            var run = _fixture.GetMethod("Run");
            var module = Module(_fixture);
            module.XmlPaths[_fixture.Assembly] = Xml(_fixture.Assembly,
                Entry(_fixture, new XElement("summary", "XML 类型"), new XElement("remarks", "类型说明"),
                    new XElement("typeparam", new XAttribute("name", "T"), "XML 泛型参数")),
                Entry(run, new XElement("summary", "读取 ", new XElement("paramref", new XAttribute("name", "value")),
                        new XElement("para", "中文段落 ", new XElement("see", new XAttribute("cref", "T:System.String")))),
                    new XElement("returns", "XML 返回"), new XElement("remarks", new XElement("typeparamref", new XAttribute("name", "T")), " 限制"),
                    new XElement("param", new XAttribute("name", "label"), "XML 标签"),
                    new XElement("param", new XAttribute("name", "value"), "XML 计数")));
            module.Documentation = member => member == _fixture ? new HelpDocumentation("已核实类型", " ", "", null,
                new[] { new HelpParameter("T", " ") }) : member.Name == "Run" ? new HelpDocumentation(null, "已核实返回", "\t",
                new[] { new HelpParameter("value", "已核实计数"), new HelpParameter("label", ""), new HelpParameter("extra", "额外覆盖") }) : null;
            var api = HelpApiIndex.Build(module).Single();
            var help = api.Entries.Single(entry => entry.Name == "Run").Help;
            Assert.That(api.Summary, Is.EqualTo("已核实类型"));
            Assert.That(api.Help.Remarks, Is.EqualTo("类型说明"));
            Assert.That(api.Help.TypeParameters.Single().Description, Is.EqualTo("XML 泛型参数"));
            Assert.That(api.Matches("XML 泛型参数"), Is.True);
            Assert.That(help.Summary, Is.EqualTo("读取 value\n中文段落 System.String"));
            Assert.That(help.Returns, Is.EqualTo("已核实返回"));
            Assert.That(help.Remarks, Is.EqualTo("T 限制"));
            CollectionAssert.AreEqual(new[] { "label", "value", "extra" }, help.Parameters.Select(parameter => parameter.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "XML 标签", "已核实计数", "额外覆盖" }, help.Parameters.Select(parameter => parameter.Description).ToArray());
            module.Documentation = member => member == _fixture ? new HelpDocumentation(null, null, null, null,
                new[] { new HelpParameter("T", "已核实类型参数") }) : null;
            Assert.That(HelpApiIndex.Build(module).Single().Help.TypeParameters.Single().Description, Is.EqualTo("已核实类型参数"));
        }

        [Test]
        public void XmlMemberIdsCoverGenericNestedOverloadedByRefArraysAndIndexers()
        {
            const string Owner = "YLFramework.Tests.Help.Fixtures.ApiFixture`1";
            Assert.That(HelpXmlDocumentation.MemberId(_fixture), Is.EqualTo("T:" + Owner));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetConstructor(Type.EmptyTypes)), Is.EqualTo("M:" + Owner + ".#ctor"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetConstructor(new[] { typeof(int) })), Is.EqualTo("M:" + Owner + ".#ctor(System.Int32)"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.TypeInitializer), Is.EqualTo("M:" + Owner + ".#cctor"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetMethod("Convert")), Is.EqualTo("M:" + Owner + ".Convert``1(`0,``0@)"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetMethod("Arrays")), Is.EqualTo("M:" + Owner + ".Arrays(System.Int32[],System.String[0:,0:],System.Int32[][])"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetMethod("ByRef")), Is.EqualTo("M:" + Owner + ".ByRef(System.Int32@,System.String@,System.Int32@)"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetProperty("Item")), Is.EqualTo("P:" + Owner + ".Item(System.Int32)"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetField("Constant")), Is.EqualTo("F:" + Owner + ".Constant"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetEvent("Changed")), Is.EqualTo("E:" + Owner + ".Changed"));
            Assert.That(HelpXmlDocumentation.MemberId(typeof(Fixtures.Outer<>.Inner<>)), Is.EqualTo("T:YLFramework.Tests.Help.Fixtures.Outer`1.Inner`1"));
            Assert.That(HelpXmlDocumentation.MemberId(_fixture.GetMethod("Nested")),
                Is.EqualTo("M:" + Owner + ".Nested(YLFramework.Tests.Help.Fixtures.Outer{System.Int32}.Inner{System.String})"));
            Assert.That(HelpXmlDocumentation.MemberId(typeof(Fixtures.ApiFixture<Fixtures.ApiBase>).GetMethod("Convert").MakeGenericMethod(typeof(int))),
                Is.EqualTo("M:" + Owner + ".Convert``1(`0,``0@)"));
            Assert.That(HelpXmlDocumentation.MemberId(typeof(HelpKitGlobalFixture)), Is.EqualTo("T:HelpKitGlobalFixture"));
        }

        [Test]
        public void InheritdocFillsMissingFieldsFromBaseInterfaceOrExplicitCrefAndBreaksCycles()
        {
            var derived = typeof(Fixtures.Derived);
            var module = Module(derived, typeof(Fixtures.ApiBase), typeof(Fixtures.IApiContract));
            var baseMethod = typeof(Fixtures.ApiBase).GetMethod("Inherited");
            var inherited = derived.GetMethod("Inherited");
            var contractMethod = typeof(Fixtures.IApiContract).GetMethod("Contract");
            var property = derived.GetProperty("InheritedValue");
            var first = derived.GetMethod("First");
            var second = derived.GetMethod("Second");
            module.XmlPaths[derived.Assembly] = Xml(derived.Assembly,
                Entry(typeof(Fixtures.ApiBase), new XElement("summary", "基类说明")),
                Entry(derived, new XElement("inheritdoc")),
                Entry(baseMethod, new XElement("summary", "基类方法"), new XElement("returns", "基类返回"), new XElement("param", new XAttribute("name", "value"), "基类参数")),
                Entry(inherited, new XElement("inheritdoc"), new XElement("summary", "自己的方法")),
                Entry(contractMethod, new XElement("summary", "接口方法")),
                Entry(derived.GetMethod("Contract"), new XElement("inheritdoc")),
                Entry(typeof(Fixtures.ApiBase).GetProperty("InheritedValue"), new XElement("summary", "基类属性")),
                Entry(property, new XElement("inheritdoc")),
                Entry(derived.GetMethod("Explicit"), new XElement("inheritdoc", new XAttribute("cref", HelpXmlDocumentation.MemberId(baseMethod)))),
                Entry(derived.GetMethod("Unknown"), new XElement("inheritdoc", new XAttribute("cref", "M:Missing.Type.Unknown"))),
                Entry(derived.GetMethod("UnsupportedPath"), new XElement("inheritdoc", new XAttribute("cref", HelpXmlDocumentation.MemberId(baseMethod)),
                    new XAttribute("path", "/summary"))),
                Entry(first, new XElement("inheritdoc", new XAttribute("cref", HelpXmlDocumentation.MemberId(second)))),
                Entry(second, new XElement("inheritdoc", new XAttribute("cref", HelpXmlDocumentation.MemberId(first)))));
            var api = HelpApiIndex.Build(module)[0];
            Assert.That(api.Summary, Is.EqualTo("基类说明"));
            var help = api.Entries.Single(entry => entry.Name == "Inherited").Help;
            Assert.That(help.Summary, Is.EqualTo("自己的方法"));
            Assert.That(help.Returns, Is.EqualTo("基类返回"));
            Assert.That(help.Parameters.Single().Description, Is.EqualTo("基类参数"));
            Assert.That(api.Entries.Single(entry => entry.Name == "Contract").Help.Summary, Is.EqualTo("接口方法"));
            Assert.That(api.Entries.Single(entry => entry.Name == "InheritedValue").Help.Summary, Is.EqualTo("基类属性"));
            Assert.That(api.Entries.Single(entry => entry.Name == "Explicit").Help.Summary, Is.EqualTo("基类方法"));
            Assert.That(api.Entries.Single(entry => entry.Name == "Unknown").Help.Summary, Is.Empty);
            Assert.That(api.Entries.Single(entry => entry.Name == "UnsupportedPath").Help.Summary, Is.Empty);
            Assert.That(api.Entries.Single(entry => entry.Name == "First").Help.Summary, Is.Empty);
            Assert.That(api.Entries.Single(entry => entry.Name == "Second").Help.Summary, Is.Empty);
        }

        [TestCase("<doc><broken></doc>")]
        [TestCase("<!DOCTYPE doc [<!ENTITY external SYSTEM 'file:///missing-helpkit-entity'>]><doc>&external;</doc>")]
        [TestCase("<doc><assembly><name>WrongAssembly</name></assembly><members/></doc>")]
        public void InvalidXmlWarnsOncePerBuildAndPreservesMetadata(string contents)
        {
            var path = Path.Combine(_folder, "invalid.xml");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), contents);
            var module = Module(_fixture, typeof(Fixtures.ApiBase));
            module.XmlPaths[_fixture.Assembly] = path;
            LogAssert.Expect(LogType.Warning, new Regex("HelpKit 无法读取 XML 文档"));
            var index = HelpApiIndex.Build(module);
            Assert.That(index[0].Entries, Is.Not.Empty);
            Assert.That(index.All(type => type.Help.Summary.Length == 0), Is.True);
            LogAssert.NoUnexpectedReceived();
            LogAssert.Expect(LogType.Warning, new Regex("HelpKit 无法读取 XML 文档"));
            HelpApiIndex.Build(module);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void UnreadableXmlWarnsOnceAndDoesNotHideProviderErrors()
        {
            var module = Module(_fixture, typeof(Fixtures.ApiBase));
            var path = Xml(_fixture.Assembly, Entry(_fixture, new XElement("summary", "锁定文档")));
            module.XmlPaths[_fixture.Assembly] = path;
            using (var locked = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                LogAssert.Expect(LogType.Warning, new Regex("HelpKit 无法读取 XML 文档"));
                Assert.That(HelpApiIndex.Build(module)[0].Summary, Is.Empty);
                LogAssert.NoUnexpectedReceived();
            }
            Assert.That(HelpApiIndex.Build(module)[0].Summary, Is.EqualTo("锁定文档"));
            module.Documentation = member => throw new InvalidOperationException("provider bug");
            Assert.Throws<InvalidOperationException>(() => HelpApiIndex.Build(module));
        }

        [UnityTest]
        public IEnumerator MemberIdsAndXmlMatchingUseActualCompilerOutputWithoutModuleDependencies()
        {
            var source = Path.Combine(_folder, "Fixture.cs");
            var output = Path.Combine(_folder, "HelpKitFixture" + Guid.NewGuid().ToString("N") + ".dll");
            var xml = Path.ChangeExtension(output, ".xml");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(source), CompilerFixture);
            var builder = new AssemblyBuilder(output, new[] { source });
            var framework = CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.SystemAssembly);
            builder.excludeReferences = builder.defaultReferences.Except(framework).ToArray();
            builder.additionalReferences = framework;
            builder.compilerOptions.AdditionalCompilerArguments = new[] { "-doc:\"" + xml.Replace('\\', '/') + "\"" };
            CompilerMessage[] messages = null;
            builder.buildFinished += (_, result) => messages = result;
            Assert.That(builder.Build(), Is.True);
            while (builder.status != AssemblyBuilderStatus.Finished)
            {
                yield return null;
            }

            Assert.That(messages, Is.Not.Null);
            Assert.That(messages.Where(message => message.type == CompilerMessageType.Error).Select(message => message.message), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(xml)), Is.True);
            // Byte loading avoids locking temporary DLLs and leaves no runtime instance behind.
            var assembly = Assembly.Load(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(output)));
            var types = assembly.GetExportedTypes();
            var docIds = XDocument.Load(xml).Root.Element("members").Elements("member").Select(entry => (string)entry.Attribute("name")).ToArray();
            var members = types.Cast<MemberInfo>().Concat(types.SelectMany(type => HelpApiFormatter.GetDeclaredApi(type))).ToArray();
            Assert.That(members.Length, Is.GreaterThan(15));
            foreach (var member in members)
            {
                Assert.That(docIds, Does.Contain(HelpXmlDocumentation.MemberId(member)), member.ToString());
            }

            var module = new HelpModule
            {
                ApiTypes = types.Select(type => new HelpApiTypeDefinition(type, "编译 fixture")).ToArray(),
                XmlPaths = new Dictionary<Assembly, string> { { assembly, xml } }
            };
            var index = HelpApiIndex.Build(module);
            Assert.That(index.All(type => type.Summary.Length != 0), Is.True);
            Assert.That(index.SelectMany(type => type.Entries).All(member => member.Help.Summary.Length != 0), Is.True);
            Assert.That(index.Single(type => type.Type.Name == "Sample`1").Help.TypeParameters.Single().Description, Is.EqualTo("编译类型参数"));
            var generic = index.Single(type => type.Type.Name == "Sample`1").Entries.Single(member => member.Name == "Map");
            Assert.That(generic.Help.TypeParameters.Single().Description, Is.EqualTo("编译方法参数"));
            Assert.That(generic.Help.Parameters.Single(parameter => parameter.Name == "value").Description, Is.EqualTo("输入参数"));
            Assert.That(generic.Help.Returns, Is.EqualTo("返回说明"));
            Assert.That(generic.Help.Remarks, Is.EqualTo("备注说明"));
        }

        private const string CompilerFixture = @"
using System;
using System.Collections.Generic;
/// <summary>全局编译类型</summary>
public class GlobalSample
{
    /// <summary>全局构造</summary>
    public GlobalSample() { }
}
namespace CompiledFixture
{
    /// <summary>编译泛型类型</summary>
    /// <typeparam name=""T"">编译类型参数</typeparam>
    public class Sample<T>
    {
        /// <summary>构造说明</summary>
        public Sample() { }
        /// <summary>重载构造说明</summary>
        public Sample(int value) { }
        /// <summary>字段说明</summary>
        public const int Field = 2;
        /// <summary>属性说明</summary>
        public int Value { get; private set; }
        /// <summary>索引器说明</summary>
        public T this[int index, string name] { get { return default(T); } }
        /// <summary>事件说明</summary>
        public event Action Changed { add { } remove { } }
        /// <summary>重载一</summary>
        public void Overload(int value) { }
        /// <summary>重载二</summary>
        public void Overload(string value) { }
        /// <summary>编译泛型方法</summary>
        /// <typeparam name=""U"">编译方法参数</typeparam>
        /// <param name=""value"">输入参数</param>
        /// <param name=""result"">输出参数</param>
        /// <returns>返回说明</returns>
        /// <remarks>备注说明</remarks>
        public U Map<U>(T value, ref U result) { return result; }
        /// <summary>数组与按引用说明</summary>
        public void Arrays(int[] one, string[,] many, int[][] jagged, ref T value, out int result, in int input) { result = 0; }
        /// <summary>嵌套实参说明</summary>
        public void Nested(Outer<int>.Inner<string> value, Outer<int>.Leaf leaf, Dictionary<string, T> map) { }
        /// <summary>嵌套泛型类型</summary>
        public class Child<U>
        {
            /// <summary>嵌套构造说明</summary>
            public Child() { }
            /// <summary>外层与内层类型参数说明</summary>
            public void Accept(T first, U second) { }
        }
    }
    /// <summary>外层编译类型</summary>
    public class Outer<T>
    {
        /// <summary>外层构造</summary>
        public Outer() { }
        /// <summary>内层编译类型</summary>
        public class Inner<U>
        {
            /// <summary>内层构造</summary>
            public Inner() { }
        }
        /// <summary>无独立泛型参数嵌套类型</summary>
        public class Leaf
        {
            /// <summary>叶构造</summary>
            public Leaf() { }
        }
    }
}";
    }
}

public class HelpKitGlobalFixture
{
}

namespace YLFramework.Tests.Help.Fixtures
{
    public interface IApiContract
    {
        void Contract();
    }
    public interface IChildContract : IApiContract
    {
    }
    public class ApiBase
    {
        public virtual int Inherited(int value) => throw new InvalidOperationException("Metadata only.");
        public virtual int InheritedValue => throw new InvalidOperationException("Metadata only.");
    }
    public class ApiFixture<T> : ApiBase, IApiContract where T : class, new()
    {
        static ApiFixture()
        {
            throw new InvalidOperationException("Do not initialize the API fixture.");
        }
        public ApiFixture()
        {
            throw new InvalidOperationException("Do not construct the API fixture.");
        }
        public ApiFixture(int value)
        {
            throw new InvalidOperationException("Do not construct the API fixture.");
        }
        public const string Constant = "fixture";
        public static readonly int Readonly;
        public int Value
        {
            get => throw new InvalidOperationException("Do not read properties."); private set => throw new InvalidOperationException();
        }
        public int this[int position] => throw new InvalidOperationException("Do not read indexers.");
        public event Action Changed { add => throw new InvalidOperationException(); remove => throw new InvalidOperationException(); }
        public int Run(int value, string label = "demo") => throw new InvalidOperationException();
        public U Convert<U>(T input, ref U output) where U : struct => throw new InvalidOperationException();
        public void Arrays(int[] one, string[,] many, int[][] jagged) => throw new InvalidOperationException();
        public void ByRef(ref int value, out string result, in int input) => throw new InvalidOperationException();
        public void Nested(Outer<int>.Inner<string> value) => throw new InvalidOperationException();
        public void OtherModule(Other.ApiFixture other) => throw new InvalidOperationException();
        public void Contract() => throw new InvalidOperationException();
        protected void Protected() => throw new InvalidOperationException();
        protected internal void ProtectedInternal() => throw new InvalidOperationException();
        private void Hidden() => throw new InvalidOperationException();
        internal void Internal() => throw new InvalidOperationException();
        public static ApiFixture<T> operator +(ApiFixture<T> left, ApiFixture<T> right) => throw new InvalidOperationException();
        public class NestedType
        {
        }
    }
    public class Outer<T>
    {
        public class Inner<U>
        {
        }
        public class Leaf
        {
        }
    }
    public class Derived : ApiBase, IChildContract
    {
        public override int Inherited(int value) => throw new InvalidOperationException();
        public override int InheritedValue => throw new InvalidOperationException();
        public void Contract() => throw new InvalidOperationException();
        public void Explicit() => throw new InvalidOperationException();
        public void Unknown() => throw new InvalidOperationException();
        public void UnsupportedPath() => throw new InvalidOperationException();
        public void First() => throw new InvalidOperationException();
        public void Second() => throw new InvalidOperationException();
    }
    public delegate int HelpDelegate(string text);
    public enum FixtureEnum : ulong
    {
        Two = 2
    }
    public static class Extensions
    {
        public static void Inspect(this ApiBase value, params string[] labels) => throw new InvalidOperationException();
    }
}

namespace YLFramework.Tests.Help.Other
{
    public class ApiFixture
    {
    }
}
