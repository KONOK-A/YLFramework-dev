using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Xml.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.TestTools;

namespace YLFramework.Core.Editor.Tests
{
    public class AnalyzerRuleSetFileTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private const string Rules = "<Rules AnalyzerId='YLFramework.Analyzers' RuleNamespace='YLFramework.Analyzers'>";
        private string _root;
        private string _asmdef;
        private string _target;

        [SetUp]
        public void SetUp()
        {
            _root = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine("Temp", "YLAnalyzerRuleSetTests", Guid.NewGuid().ToString("N"))));
            _asmdef = Path.Combine(_root, "Assets", "Games", "Feature", "Feature.asmdef");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_asmdef)));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_asmdef), "{\"name\":\"Game.Feature\"}");
            _target = Path.Combine(Path.GetDirectoryName(_asmdef), "Game.Feature.ruleset");
        }

        [TearDown]
        public void TearDown()
        {
            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root)))
            {
                return;
            }
            foreach (var file in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_root), "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(file), FileAttributes.Normal);
            }
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root), true);
        }

        [TestCase("Assembly-CSharp")]
        [TestCase("Assembly-CSharp-firstpass")]
        [TestCase("Assembly-CSharp-Editor")]
        [TestCase("Assembly-CSharp-Editor-firstpass")]
        public void PredefinedAssembliesUseOnlyTheirNativeFileNames(string assembly)
        {
            var preview = Preview(assembly, null, Change("YL0001", "Error"));
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Null);
            Assert.That(Value<string>(preview, "TargetPath"), Is.EqualTo(Path.Combine(_root, "Assets", assembly + ".ruleset")));
            Apply(preview);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_root, "Assets", "Default.ruleset"))), Is.False);
        }

        [Test]
        public void PreviewDoesNotWriteAndApplyCreatesOnlyTheSelectedAsmdefRuleSet()
        {
            var preview = Preview(Change("YL0008", "Error"));
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Null);
            Assert.That(Value<string>(preview, "TargetPath"), Is.EqualTo(_target));
            Assert.That(Value<bool>(preview, "HasChanges"), Is.True);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.False);
            Apply(preview);
            Assert.That(XDocument.Load(_target).Descendants("Rule").Single().Attribute("Action").Value, Is.EqualTo("Error"));
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_root, "Assets")), "*.ruleset", SearchOption.AllDirectories),
                Is.EqualTo(new[] { _target }));
        }

        [Test]
        public void InheritWithoutLocalConfigurationDoesNotCreateAFile()
        {
            var preview = Preview(Change("YL0001", null));
            Assert.That(Value<bool>(preview, "HasChanges"), Is.False);
            Apply(preview);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.False);
        }

        [Test]
        public void NewLocalConfigurationIncludesDefaultRatherThanCopyingOrDroppingIt()
        {
            var path = Path.Combine(_root, "Assets", "Default.ruleset");
            const string original = "<RuleSet Name='Global' ToolsVersion='12.0'><Rules AnalyzerId='Other' RuleNamespace='Other'><Rule Id='OTHER1' Action='Error'/></Rules></RuleSet>";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), original);
            Apply(Preview(Change("YL0002", "None")));
            var document = XDocument.Load(_target);
            var include = document.Root.Element("Include");
            Assert.That(include.Attribute("Path").Value, Is.EqualTo("../../Default.ruleset"));
            Assert.That(include.Attribute("Action").Value, Is.EqualTo("Default"));
            Assert.That(document.Descendants("Rule").Select(rule => rule.Attribute("Id").Value), Is.EqualTo(new[] { "YL0002" }));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(original));
        }

        [Test]
        public void EditingOneIdPreservesOtherAnalyzerUnknownIdsCommentsAndIncludeAll()
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target), "<RuleSet Name='Existing' ToolsVersion='12.0'><!--keep--><IncludeAll Action='Warning'/>" +
                "<Rules AnalyzerId='Other' RuleNamespace='Other'><Rule Id='OTHER1' Action='None'/></Rules>" + Rules +
                "<Rule Id='YL0001' Action='Error'/><Rule Id='YL9999' Action='Hidden'/></Rules></RuleSet>");
            Apply(Preview(Change("YL0001", "Warning")));
            var document = XDocument.Load(_target);
            Assert.That(document.Descendants("Rule").Select(rule => rule.Attribute("Action").Value),
                Is.EqualTo(new[] { "None", "Warning", "Hidden" }));
            Assert.That(document.DescendantNodes().OfType<XComment>().Single().Value, Is.EqualTo("keep"));
            Assert.That(document.Root.Element("IncludeAll").Attribute("Action").Value, Is.EqualTo("Warning"));
        }

        [Test]
        public void InheritRemovesLocalOverrideButExplicitWarningDoesNot()
        {
            WriteLeaf("<Rule Id='YL0001' Action='Error'/>");
            Apply(Preview(Change("YL0001", "Warning")));
            Assert.That(XDocument.Load(_target).Descendants("Rule").Single().Attribute("Action").Value, Is.EqualTo("Warning"));
            Apply(Preview(Change("YL0001", null)));
            Assert.That(XDocument.Load(_target).Descendants("Rule"), Is.Empty);
        }

        [Test]
        public void OnePreviewAppliesSeveralChangesWithoutTouchingUnknownRules()
        {
            WriteLeaf("<Rule Id='YL0001' Action='Error'/><Rule Id='YL0002' Action='Error'/><Rule Id='YL9999' Action='Hidden'/>");
            var preview = Preview(new Dictionary<string, string>
            {
                ["YL0001"] = null,
                ["YL0002"] = "Warning",
                ["YL0010"] = "None"
            });
            Apply(preview);
            Assert.That(XDocument.Load(_target).Descendants("Rule").Select(rule =>
                (string)rule.Attribute("Id") + ":" + (string)rule.Attribute("Action")),
                Is.EqualTo(new[] { "YL0002:Warning", "YL9999:Hidden", "YL0010:None" }));
        }

        [Test]
        public void NoChangePreservesOriginalBytesAndTimestamp()
        {
            WriteLeaf("<Rule Id='YL0001' Action='Warning'/>");
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_target));
            var stamp = new DateTime(2011, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(_target, stamp);
            var preview = Preview(Change("YL0001", "Warning"));
            Assert.That(Value<bool>(preview, "HasChanges"), Is.False);
            Apply(preview);
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(_target), Is.EqualTo(stamp));
        }

        [TestCase("<broken")]
        [TestCase("<!DOCTYPE RuleSet [<!ENTITY x 'value'>]><RuleSet Name='Bad' ToolsVersion='12.0'>&x;</RuleSet>")]
        [TestCase("<RuleSet Name='Bad' ToolsVersion='12.0'><Unknown/></RuleSet>")]
        [TestCase("<RuleSet Name='Bad' ToolsVersion='12.0'><Rules AnalyzerId='Other' RuleNamespace='Other'><Rule Id='OTHER' Action='Warning'><Rule Id='YL0001' Action='Error'/></Rule></Rules></RuleSet>")]
        [TestCase("<RuleSet Name='Bad' ToolsVersion='12.0'>unexpected text</RuleSet>")]
        [TestCase("<RuleSet Name='Bad' ToolsVersion='12.0'><Rules AnalyzerId='Other' RuleNamespace='Other'><Rule Id='OTHER' Action='Warning'>unexpected text</Rule></Rules></RuleSet>")]
        [TestCase("<RuleSet Name='Bad' ToolsVersion='12.0'><IncludeAll Action='Warning'><Unknown/></IncludeAll></RuleSet>")]
        [TestCase("<RuleSet Name='Bad' ToolsVersion='12.0'><Include Path='missing.ruleset' Action='Default'/></RuleSet>")]
        public void UnsupportedOrUnreadableXmlIsReadOnly(string xml)
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target), xml);
            AssertBlockedAndUnchanged(Preview(Change("YL0001", "Error")), xml);
        }

        [Test]
        public void DuplicateKnownIdsAndForeignOwnershipAreNotSilentlyResolved()
        {
            WriteLeaf("<Rule Id='YL0001' Action='Error'/><Rule Id='YL0001' Action='None'/>");
            AssertBlockedAndUnchanged(Preview(Change("YL0001", "Warning")), File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target)));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target), "<RuleSet Name='Foreign' ToolsVersion='12.0'><Rules AnalyzerId='Other' RuleNamespace='Other'><Rule Id='YL0001' Action='Error'/></Rules></RuleSet>");
            AssertBlockedAndUnchanged(Preview(Change("YL0001", "Warning")), File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target)));
        }

        private static IEnumerable<TestCaseData> InvalidRoslynPolicies()
        {
            var cases = new[]
            {
                ("RuleDefault", "<RuleSet Name='Bad' ToolsVersion='12.0'><Rules AnalyzerId='Other' RuleNamespace='Other'><Rule Id='OTHER1' Action='Default'/></Rules></RuleSet>"),
                ("IncludeAllDefault", "<RuleSet Name='Bad' ToolsVersion='12.0'><IncludeAll Action='Default'/></RuleSet>"),
                ("MissingName", "<RuleSet ToolsVersion='12.0'/>"),
                ("MissingToolsVersion", "<RuleSet Name='Bad'/>"),
                ("EmptyName", "<RuleSet Name='' ToolsVersion='12.0'/>"),
                ("EmptyToolsVersion", "<RuleSet Name='Bad' ToolsVersion=''/>") ,
                ("MissingAnalyzerId", "<RuleSet Name='Bad' ToolsVersion='12.0'><Rules RuleNamespace='Other'><Rule Id='OTHER1' Action='Warning'/></Rules></RuleSet>"),
                ("MissingRuleNamespace", "<RuleSet Name='Bad' ToolsVersion='12.0'><Rules AnalyzerId='Other'><Rule Id='OTHER1' Action='Warning'/></Rules></RuleSet>"),
                ("EmptyAnalyzerId", "<RuleSet Name='Bad' ToolsVersion='12.0'><Rules AnalyzerId='' RuleNamespace='Other'><Rule Id='OTHER1' Action='Warning'/></Rules></RuleSet>"),
                ("UnknownIdConflict", "<RuleSet Name='Bad' ToolsVersion='12.0'><Rules AnalyzerId='Other' RuleNamespace='Other'><Rule Id='OTHER1' Action='Warning'/><Rule Id='OTHER1' Action='Error'/></Rules></RuleSet>"),
                ("CrossAnalyzerConflict", "<RuleSet Name='Bad' ToolsVersion='12.0'><Rules AnalyzerId='First' RuleNamespace='First'><Rule Id='OTHER1' Action='Warning'/></Rules><Rules AnalyzerId='Second' RuleNamespace='Second'><Rule Id='OTHER1' Action='Error'/></Rules></RuleSet>")
            };
            foreach (var (name, xml) in cases)
            {
                foreach (var inherited in new[] { false, true })
                {
                    yield return new TestCaseData(xml, inherited).SetName("InvalidRoslynPolicy_" + name + (inherited ? "_Inherited" : "_Local"));
                }
            }
        }

        [TestCaseSource(nameof(InvalidRoslynPolicies))]
        public void InvalidNativePoliciesAreRejectedWithoutModifyingAnyInput(string xml, bool inherited)
        {
            var path = inherited ? Path.Combine(_root, "Assets", "Default.ruleset") : _target;
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), xml);
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var preview = Preview(Change("YL0001", "Error"));
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Not.Null.And.Not.Empty);
            Assert.Throws<InvalidOperationException>(() => Apply(preview));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(original));
            if (inherited)
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.False);
            }
        }

        [Test]
        public void IdenticalUnknownRulesAcrossAnalyzerSectionsRemainOpaqueAndLegal()
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target), "<RuleSet Name='Existing' ToolsVersion='12.0'><Rules AnalyzerId='First' RuleNamespace='First'><Rule Id='OTHER1' Action='Warning'/></Rules><Rules AnalyzerId='Second' RuleNamespace='Second'><Rule Id='OTHER1' Action='Warning'/></Rules></RuleSet>");
            var preview = Preview(Change("YL0001", "Error"));
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Null);
            Apply(preview);
            Assert.That(XDocument.Load(_target).Descendants("Rule").Where(rule => (string)rule.Attribute("Id") == "OTHER1")
                .All(rule => (string)rule.Attribute("Action") == "Warning"), Is.True);
        }

        [Test]
        public void EnvironmentIncludeCannotValidateALiteralFileInsteadOfTheExpandedPolicy()
        {
            var name = "YLF_RULE_POLICY_" + Guid.NewGuid().ToString("N");
            var previous = Environment.GetEnvironmentVariable(name);
            var expanded = Path.Combine(_root, "ExpandedPolicy");
            var literal = Path.Combine(Path.GetDirectoryName(_target), "%" + name + "%");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(expanded));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(literal));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(expanded, "Shared.ruleset")), "<RuleSet Name='Actual' ToolsVersion='12.0'><IncludeAll Action='Error'/></RuleSet>");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(literal, "Shared.ruleset")), "<RuleSet Name='Literal' ToolsVersion='12.0'/>");
            var xml = "<RuleSet Name='Local' ToolsVersion='12.0'><Include Path='%" + name + "%/Shared.ruleset' Action='Default'/></RuleSet>";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target), xml);
            try
            {
                Environment.SetEnvironmentVariable(name, expanded);
                AssertBlockedAndUnchanged(Preview(Change("YL0001", "Error")), xml);
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }

        [Test]
        public void MultipleLocalRuleSetsAreAmbiguous()
        {
            WriteLeaf("");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Path.GetDirectoryName(_target), "Other.ruleset")), "<RuleSet Name='Other' ToolsVersion='12.0'/>");
            AssertBlockedAndUnchanged(Preview(Change("YL0001", "Error")), File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target)));
        }

        [Test]
        public void IncludeCyclesAreNotFollowedOrOverwritten()
        {
            var global = Path.Combine(_root, "Assets", "Default.ruleset");
            var shared = Path.Combine(_root, "Assets", "Shared.ruleset");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(global), "<RuleSet Name='A' ToolsVersion='12.0'><Include Path='Shared.ruleset' Action='Default'/></RuleSet>");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(shared), "<RuleSet Name='B' ToolsVersion='12.0'><Include Path='Default.ruleset' Action='Default'/></RuleSet>");
            var preview = Preview(Change("YL0001", "Error"));
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.False);
        }

        [Test]
        public void ApplyRejectsChangesToTargetInheritanceOrCandidateSetAfterPreview()
        {
            WriteLeaf("<Rule Id='YL0001' Action='Warning'/>");
            var preview = Preview(Change("YL0001", "Error"));
            var original = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target)) + "<!--external-->";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target), original);
            Assert.Throws<IOException>(() => Apply(preview));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.EqualTo(original));

            var global = Path.Combine(_root, "Assets", "Default.ruleset");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(global), "<RuleSet Name='Global' ToolsVersion='12.0'/>");
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_target));
            preview = Preview(Change("YL0001", "Error"));
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(global), "<!--external-->");
            Assert.Throws<IOException>(() => Apply(preview));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.False);

            preview = Preview(Change("YL0001", "Error"));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Path.GetDirectoryName(_target), "New.ruleset")), "<RuleSet Name='New' ToolsVersion='12.0'/>");
            Assert.Throws<IOException>(() => Apply(preview));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.False);
        }

        [Test]
        public void ReadOnlyTargetCannotBeReplaced()
        {
            WriteLeaf("<Rule Id='YL0001' Action='Warning'/>");
            File.SetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(_target), FileAttributes.ReadOnly);
            AssertBlockedAndUnchanged(Preview(Change("YL0001", "Error")), File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target)));
        }

        [TestCase("UnknownAssembly")]
        [TestCase("../Escape")]
        [TestCase("Default")]
        public void UnsupportedPredefinedNamesNeverWriteAFile(string name)
        {
            var preview = Preview(name, null, Change("YL0001", "Error"));
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Not.Null.And.Not.Empty);
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_root), "*.ruleset", SearchOption.AllDirectories), Is.Empty);
        }

        [Test]
        public void PackageFrameworkAndOutsideAssetsTargetsAreReadOnly()
        {
            foreach (var folder in new[] { "Packages/Example", "Packages/com.kano.ylframework/Editor", "ProjectSettings" })
            {
                var path = Path.Combine(_root, folder, "Other.asmdef");
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path)));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "{\"name\":\"Other\"}");
                var preview = Preview("Other", path, Change("YL0001", "Error"));
                Assert.That(Value<string>(preview, "BlockedReason"), Is.Not.Null.And.Not.Empty, folder);
            }
            Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(_root), "*.ruleset", SearchOption.AllDirectories), Is.Empty);
        }

        [TestCase("YL9999", "Error")]
        [TestCase("YL0001", "Invalid")]
        public void UnknownChangesCannotCreateOrRewriteConfiguration(string id, string action)
        {
            var preview = Preview(Change(id, action));
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.False);
        }

        [Test]
        public void ExistingUtf16EncodingAndMetaIdentityArePreserved()
        {
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target), "<?xml version='1.0' encoding='utf-16'?><RuleSet Name='Existing' ToolsVersion='12.0'>" +
                Rules + "<Rule Id='YL0001' Action='Warning'/></Rules></RuleSet>", new UnicodeEncoding(false, true));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target + ".meta"), "guid: unchanged");
            Apply(Preview(Change("YL0001", "Error")));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_target)).Take(2), Is.EqualTo(new byte[] { 255, 254 }));
            Assert.That(XDocument.Load(_target).Descendants("Rule").Single().Attribute("Action").Value, Is.EqualTo("Error"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target + ".meta")), Is.EqualTo("guid: unchanged"));
        }

        [UnityTest]
        public IEnumerator ActualUnityCompilerPreservesIncludedDefaultWhileApplyingLocalOverride()
        {
            var global = Path.Combine(_root, "Assets", "Default.ruleset");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(global), "<RuleSet Name='Global' ToolsVersion='12.0'>" + Rules +
                "<Rule Id='YL0002' Action='Error'/></Rules></RuleSet>");
            Apply(Preview(Change("YL0001", "None")));
            var source = Path.Combine(_root, "Probe.cs");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(source), @"using YLFramework.Core.RoslynAnalyzers;
class Base{[MustCallBase] public virtual void Needed(){}}
class Child:Base{public override void Needed(){}}
class Caller{[RestrictedCall(""Allowed"")]void Restricted(){}void Wrong(){Restricted();}}");
            var runtime = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.name == "YLFramework.Runtime");
            foreach (var path in new[] { global, _target })
            {
                var builder = new AssemblyBuilder(Path.Combine(_root, path == global ? "Baseline.dll" : "Local.dll"), new[] { source })
                {
                    flags = AssemblyBuilderFlags.EditorAssembly,
                    buildTarget = BuildTarget.StandaloneWindows64,
                    buildTargetGroup = BuildTargetGroup.Standalone,
                    referencesOptions = ReferencesOptions.UseEngineModules,
                    additionalReferences = new[] { typeof(YLFramework.Core.Module).Assembly.Location },
                    compilerOptions = new ScriptCompilerOptions
                    {
                        RoslynAnalyzerDllPaths = runtime.compilerOptions.RoslynAnalyzerDllPaths,
                        RoslynAnalyzerRulesetPath = path
                    }
                };
                builder.excludeReferences = builder.defaultReferences.Where(reference =>
                    Path.GetFileName(reference).StartsWith("YLFramework", StringComparison.Ordinal) ||
                    Path.GetFileName(reference).EndsWith("TestRunner.dll", StringComparison.Ordinal)).ToArray();
                CompilerMessage[] messages = null;
                builder.buildFinished += (_, result) => messages = result;
                Assert.That(builder.Build(), Is.True);
                while (builder.status != AssemblyBuilderStatus.Finished)
                {
                    yield return null;
                }
                Assert.That(messages, Is.Not.Null);
                Assert.That(messages.Any(message => message.message.Contains("AD0001") || message.message.Contains("CS8032")), Is.False);
                var first = messages.Where(message => message.message.Contains("YL0001")).ToArray();
                Assert.That(first.Length, Is.EqualTo(path == global ? 1 : 0), string.Join("\n", messages.Select(message => message.message)));
                if (path == global)
                {
                    Assert.That(first[0].type, Is.EqualTo(CompilerMessageType.Warning));
                }
                var second = messages.Where(message => message.message.Contains("YL0002")).ToArray();
                Assert.That(second.Length, Is.EqualTo(1), string.Join("\n", messages.Select(message => message.message)));
                Assert.That(second[0].type, Is.EqualTo(CompilerMessageType.Error));
            }
        }

        [UnityTest]
        public IEnumerator FormalRuntimeUsageHasActualEditorConsumerWitnesses()
            => CoreEditorIdentityTests.CompileNewUsageWitnesses(AssemblyBuilderFlags.EditorAssembly);

        [UnityTest]
        public IEnumerator FormalRuntimeUsageHasActualPlayerConsumerWitnesses()
            => CoreEditorIdentityTests.CompileNewUsageWitnesses(AssemblyBuilderFlags.DevelopmentBuild);

        private void WriteLeaf(string content) => File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target),
            "<RuleSet Name='Existing' ToolsVersion='12.0'>" + Rules + content + "</Rules></RuleSet>");

        private static Dictionary<string, string> Change(string id, string action) =>
            new Dictionary<string, string> { [id] = action };

        private object Preview(IReadOnlyDictionary<string, string> changes) => Preview("Game.Feature", _asmdef, changes);

        private object Preview(string name, string asmdef, IReadOnlyDictionary<string, string> changes)
        {
            var type = System.Reflection.Assembly.Load("YLFramework.Editor").GetType(
                "YLFramework.Editor.Core.Analyzers.AnalyzerRuleSetFile");
            Assert.That(type, Is.Not.Null, "规则集预览与写入功能尚未实现");
            return Invoke(type.GetMethod("Preview", Members), _root, name, asmdef, changes);
        }

        private static void Apply(object preview) => Invoke(preview.GetType().Assembly.GetType(
            "YLFramework.Editor.Core.Analyzers.AnalyzerRuleSetFile").GetMethod("Apply", Members), preview);

        private static T Value<T>(object preview, string name) =>
            (T)preview.GetType().GetProperty(name, Members).GetValue(preview);

        private static object Invoke(MethodInfo method, params object[] arguments)
        {
            try
            {
                return method.Invoke(null, arguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private void AssertBlockedAndUnchanged(object preview, string original)
        {
            Assert.That(Value<string>(preview, "BlockedReason"), Is.Not.Null.And.Not.Empty);
            Assert.Throws<InvalidOperationException>(() => Apply(preview));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_target)), Is.EqualTo(original));
        }
    }
}
