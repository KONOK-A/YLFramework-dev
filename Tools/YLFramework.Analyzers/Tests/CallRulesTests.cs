using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace YLFramework.Analyzers.Tests
{
    public class CallRulesTests
    {
        public static IEnumerable<TestCaseData> Cases()
        {
            yield return Case("Base_Missing", "class B{[MustCallBase(\"base reason\")]public virtual void M(){}} class C:B{public override void {|YL0001:M|}(){}}", "M", "base reason");
            yield return Case("Base_Expression", "class B{[MustCallBase]public virtual void M(){}} class C:B{public override void M()=>base.M();}");
            yield return Case("Base_Exists_NotAllPaths", "class B{[MustCallBase]public virtual void M(){}} class C:B{public override void M(){if(DateTime.Now.Ticks>0)base.M();}}");
            yield return Case("Base_WrongOverload", "class B{[MustCallBase]public virtual void M(){}public void M(int i){}} class C:B{public override void {|YL0001:M|}(){base.M(1);}}", "M");
            yield return Case("Base_LambdaNotOuter", "class B{[MustCallBase]public virtual void M(){}} class C:B{public override void {|YL0001:M|}(){Action a=()=>base.M();}}");
            yield return Case("Base_LocalNotOuter", "class B{[MustCallBase]public virtual void M(){}} class C:B{public override void {|YL0001:M|}(){void F(){base.M();}}}");
            yield return Case("Base_MethodGroupNotCall", "class B{[MustCallBase]public virtual void M(){}} class C:B{public override void {|YL0001:M|}(){Action a=base.M;}}");
            yield return Case("Base_OverrideChain", "class B<T>{[MustCallBase]public virtual void M(T x){}} class C<T>:B<T>{public override void M(T x){base.M(x);}} class D:C<int>{public override void {|YL0001:M|}(int x){}}", "M");
            yield return Case("Base_AbstractConflict", "abstract class B{[MustCallBase]public abstract void M();} class C:B{public override void {|YL0001:M|}(){}}", "抽象");
            yield return Case("Base_ForeignShortName", "class MustCallBaseAttribute:Attribute{} class B{[MustCallBase]public virtual void M(){}} class C:B{public override void M(){}}");
            yield return Case("Caller_AwakeAndCallbackArgument", "class C:YLFramework.Core.View{void Awake(){RegisterEvent(()=>{});}}");
            yield return Case("Caller_Wrong", "class C:YLFramework.Core.View{void Wrong(){ {|YL0002:RegisterEvent(()=>{})|};}}", "Awake", "RegisterEvent");
            yield return Case("Caller_LambdaIndependent", "class C{[RestrictedCall(\"Awake\")]static int F()=>1;void Awake(){F();Action a=()=>{|YL0002:F()|};}}", "Awake");
            yield return Case("Caller_LocalCannotImpersonate", "class C{[RestrictedCall(\"Awake\")]static int F()=>1;void M(){void Awake(){ {|YL0002:F()|};}}}");
            yield return Case("Caller_AccessorInitializer", "class C{[RestrictedCall(\"Awake\")]static int F()=>1;int P=>{|YL0002:F()|};int x={|YL0002:F()|};}");
            yield return Case("Caller_AccessorAllowed", "class C{[RestrictedCall(\"get_P\")]static int F()=>1;int P=>F();}");
            yield return Case("Caller_QualifiedPropertyAllowed", "class C{[RestrictedCall(\"C.P\")]static int F()=>1;int P=>F();}");
            yield return Case("Caller_IndexerAllowed", "class C{[RestrictedCall(\"get_Item\")]static int F()=>1;public int this[int i]=>F();}");
            yield return Case("Caller_MarkerAlias", "using R=YLFramework.Core.RoslynAnalyzers.RestrictedCallAttribute; class C{[R(\"Awake\")]static void F(){}void Awake(){F();}void Wrong(){ {|YL0002:F()|};}}");
            yield return Case("Caller_Constructors", "class C{[RestrictedCall(new[]{\".ctor\",\".cctor\"})]static int F()=>1; C(){F();}static C(){F();}}");
            yield return Case("Caller_QualifiedNestedAndNulls", "namespace N{class Outer{class C<T>{[RestrictedCall(new string[]{null,\"\",\"N.Outer.C.Awake\"})]static int F()=>1;void Awake(){F();}}}}");
            yield return Case("Caller_EmptyNullList", "class C{[RestrictedCall((string[])null)]static int F()=>1;void Awake(){ {|YL0002:F()|};}}");
            yield return Case("Caller_GenericExtension", "static class E{[RestrictedCall(\"Awake\")]public static void F<T>(this T x){} }class C{void Awake(){this.F();}void Wrong(){ {|YL0002:this.F()|};}}");
            yield return Case("Caller_InterfaceStaticTargetOnly", "interface I{[RestrictedCall(\"Awake\")]void F();}class C:I{public void F(){}void M(I i){F(); {|YL0002:i.F()|};}}");
            yield return Case("Warn_ViewAndNestedHelper", "class C:YLFramework.Core.View{void M(){ {|YL0003:YLFramework.Core.RestrictedApi.Observe(()=>{})|};}class Helper{void M(){YLFramework.Core.RestrictedApi.Observe(()=>{});}}}", "C", "Observe", "View reason");
            yield return Case("Warn_StructInterfaceAndDedup", "interface I{} class E{[WarnInClasses(typeof(I),\"reason\")][WarnInClasses(new[]{typeof(I)},\"reason\")]public static void F(){}}struct C:I{void M(){ {|YL0003:E.F()|};}}", "reason");
            yield return Case("Warn_OpenGeneric", "class B<T>{}class C:B<int>{[WarnInClasses(typeof(B<>),null)]void F(){}void M(){ {|YL0003:F()|};}}");
            yield return Case("Warn_ClosedGenericMismatch", "class B<T>{}class C:B<string>{[WarnInClasses(typeof(B<int>),\"reason\")]void F(){}void M(){F();}}");
            yield return Case("Warn_UnrelatedAttributeAndNull", "class OtherAttribute:Attribute{public OtherAttribute(Type t,string m){}}class C{[Other(typeof(C),\"other\")][WarnInClasses((Type[])null,null)]void F(){}void M(){F();}}");
            yield return Case("Warn_InheritedOverride", "class B{[WarnInClasses(typeof(C),\"reason\")]public virtual void F(){}}class D:B{public override void F(){}}class C{void M(D d){ {|YL0003:d.F()|};}}");
            yield return Case("ForeignFullName_AndRealAlias", "namespace YLFramework.Core.RoslynAnalyzers{class MustCallBaseAttribute:Attribute{}}class B{[MustCallBase]public virtual void F(){}[core::YLFramework.Core.RoslynAnalyzers.MustCallBase]public virtual void G(){}}class C:B{public override void F(){}public override void {|YL0001:G|}(){}}");
        }

        private static TestCaseData Case(string name, string source, params string[] parts)
            => new TestCaseData(source, parts).SetName(name);

        [TestCaseSource(nameof(Cases))]
        public Task Contract(string source, string[] parts) => AnalyzerFixture.Check(source, parts);

        [Test]
        public async Task GeneratedCodeIsNotAnalyzed()
        {
            var source = AnalyzerFixture.Prefix + "class C:YLFramework.Core.View{protected override void Start(){}void Wrong(){RegisterEvent(()=>{});}}";
            Assert.That(await AnalyzerFixture.Diagnostics(AnalyzerFixture.Compilation(source, "Generated.g.cs")), Is.Empty);
        }

        [Test]
        public async Task WithoutCoreReferenceLookalikesDoNotEnableRules()
        {
            var source = "using System; namespace YLFramework.Core.RoslynAnalyzers{class MustCallBaseAttribute:Attribute{}} class B{[YLFramework.Core.RoslynAnalyzers.MustCallBase]public virtual void F(){}}class C:B{public override void F(){}}";
            Assert.That(await AnalyzerFixture.Diagnostics(AnalyzerFixture.Compilation(source, core: false)), Is.Empty);
        }

        [Test]
        public async Task InvalidAttributeArgumentsAndUnboundCallsDoNotCreateDiagnostics()
        {
            var source = AnalyzerFixture.Prefix + "class C{[RestrictedCall(new string[]{Missing})]static void F(){}void M(){F();Unknown();}}";
            var compilation = AnalyzerFixture.Compilation(source);
            Assert.That(compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), Is.True);
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new CoreUsageAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics));
        }
    }
}
