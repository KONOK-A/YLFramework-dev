using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace YLFramework.Analyzers.Tests
{
    public class PanelLoadingRulesTests
    {
        [TestCase("owner.LoadView<P>(\"name\",null,(LoadViewArgs)null)")]
        [TestCase("owner.LoadView<P>((UnityEngine.GameObject)null,null,(LoadViewArgs)null)")]
        [TestCase("owner.LoadView<P>((UnityEngine.Transform)null,(LoadViewArgs)null)")]
        [TestCase("owner.LoadViewAsync<P>(\"name\",null,(LoadViewArgs)null)")]
        [TestCase("owner.LoadViewAsync<P>((UnityEngine.Transform)null,(LoadViewArgs)null)")]
        public Task AllFiveTypedGenericLoadingOverloadsRejectPanelTypes(string call)
            => AnalyzerFixture.Check("using YLFramework.Core;class P:Panel{}class C{void M(Module owner){var p={|YL0009:" +
                call + "|};}}", "LoadPanel");

        [Test]
        public Task EffectivePanelConstraintsAreProvableThroughTypeParameters()
            => AnalyzerFixture.Check(@"using YLFramework.Core;
class C{void M<TPanel,TDerived>(Module owner) where TPanel:Panel where TDerived:TPanel {
 var p={|YL0009:owner.LoadView<TDerived>(""name"",loadViewArgs:null)|};
 var r={|YL0009:owner.LoadViewAsync<TPanel>(""name"",loadViewArgs:null)|};
}}", "LoadPanel");

        [Test]
        public Task UnknownViewTypesAndLegalPanelEntrypointsStayQuiet()
            => AnalyzerFixture.Check(@"using YLFramework.Core;class P:Panel{}
class C{void M<TView>(Module owner) where TView:View {
 owner.LoadView<View>(""name"",loadViewArgs:null);
 owner.LoadView<TView>(""name"",loadViewArgs:null);
 owner.LoadViewAsync<TView>(""name"",loadViewArgs:null);
 owner.LoadView(""name"",loadViewArgs:null);
 owner.LoadViewAsync(""name"",loadViewArgs:null);
 owner.LoadPanel<P>(""name"");owner.LoadPanelAsync<P>(""name"");
 owner.GetView<P>(""name"");owner.GetViews<P>();owner.AttachView<P>(null);
}}");

        [Test]
        public Task SameNamedUserMethodIsNotAFrameworkLoadingEntrypoint()
            => AnalyzerFixture.Check(@"using YLFramework.Core;
class Other{public T LoadView<T>() where T:View=>default;}
class C{void M(Other owner){owner.LoadView<Panel>();}}");

        [Test]
        public Task SameFullNamePanelFromConsumerIsNotTheRealPanel()
            => AnalyzerFixture.Check(@"namespace YLFramework.Core{public class Panel:core::YLFramework.Core.View{}}
class C{void M(core::YLFramework.Core.Module owner){
 owner.LoadView<YLFramework.Core.Panel>(""name"",loadViewArgs:null);
}}");

        [Test]
        public async Task SourceRuntimeAndMetadataUseTheSameContract()
        {
            var source = CSharpSyntaxTree.ParseText(
                "class C{void M(YLFramework.Core.Module owner){owner.LoadView<YLFramework.Core.Panel>(\"name\",loadViewArgs:null);}}",
                AnalyzerFixture.Parse);
            var diagnostics = await AnalyzerFixture.Diagnostics(AnalyzerFixture.CoreCompilation().AddSyntaxTrees(source));
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "YL0009" }));
        }

        [Test]
        public async Task IncompleteAndGeneratedLoadingCallsDoNotCrashOrWarn()
        {
            const string valid = "class C{void M(YLFramework.Core.Module owner){owner.LoadView<YLFramework.Core.Panel>(\"name\",loadViewArgs:null);}}";
            Assert.That(await AnalyzerFixture.Diagnostics(AnalyzerFixture.Compilation(valid, "Loading.g.cs")), Is.Empty);
            var incomplete = AnalyzerFixture.Compilation(valid.Replace("YLFramework.Core.Panel", "Missing"));
            var diagnostics = await incomplete.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new CoreUsageAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.That(diagnostics, Is.Empty);
        }
    }
}
