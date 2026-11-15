using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace YLFramework.Analyzers.Tests
{
    public class PoolRulesTests
    {
        [TestCase("{|YL0011:p.Rent(out _)|};")]
        [TestCase("_ = {|YL0011:p.Rent(out _)|};")]
        [TestCase("_ = (object)({|YL0011:p.Rent(out _)|});")]
        [TestCase("_ = (System.IDisposable){|YL0011:p.Rent(out _)|};")]
        public Task DroppingObjectRentalReportsAtTheRealInvocation(string body)
            => AnalyzerFixture.Check("using YLFramework.Pooling;class C{void M(ObjectPool<object> p){" + body + "}}", "租约", "Dispose", "Discard");

        [Test]
        public Task AllFourRealPoolEntriesAreChecked()
            => AnalyzerFixture.Check(@"using YLFramework.Pooling;
public class PoolItem:IOfPool{public bool IsRecycled{get;set;}public void OnAllocate(){}public void OnRecycle(){}}
class C{void M(ObjectPool<object> p,SafeObjectPool<PoolItem> safe){
 {|YL0011:p.Rent(out var item)|};
 {|YL0011:safe.Rent(out var domain)|};
 _ = {|YL0011:ListPool<int>.Rent(out var list,32)|};
 _ = {|YL0011:DictionaryPool<string,int>.Rent(out var dictionary)|};
}}");

        [Test]
        public Task VoidExpressionBodiesAndCallbacksDiscardTheirResult()
            => AnalyzerFixture.Check(@"using YLFramework.Pooling;
class C{
 void M(ObjectPool<object> p)=>{|YL0011:p.Rent(out _)|};
 void Callbacks(ObjectPool<object> p){
  System.Action a=()=>{|YL0011:p.Rent(out _)|};
  void Local()=>{|YL0011:p.Rent(out _)|};
  System.Action b=delegate{{|YL0011:p.Rent(out _)|};};
 }
}");

        [Test]
        public Task ForInitializerAndIteratorAreAlsoUnusedExpressions()
            => AnalyzerFixture.Check(@"using YLFramework.Pooling;
class C{void M(ObjectPool<object> p){for({|YL0011:p.Rent(out _)|};false;{|YL0011:p.Rent(out _)|}){}}}");

        [Test]
        public Task OwnershipAndExplicitCompletionStayQuiet()
            => AnalyzerFixture.Check(@"using YLFramework.Pooling;
class C{
 PoolLease<object> saved;
 void Consume(PoolLease<object> lease){lease.Dispose();}
 PoolLease<object> Return(ObjectPool<object> p)=>p.Rent(out _);
 System.Func<PoolLease<object>> Factory(ObjectPool<object> p)=>()=>p.Rent(out _);
 void M(ObjectPool<object> p){
  using(var lease=p.Rent(out var a)){}
  using(p.Rent(out var b)){}
  using var rental=p.Rent(out var c);
  var manual=p.Rent(out var d);manual.Discard();
  saved=p.Rent(out var e);
  Consume(p.Rent(out var f));
  p.Rent(out _).Dispose();p.Rent(out _).Discard();
 }
 void NamedDiscard(ObjectPool<object> p){PoolLease<object> _=default;_=p.Rent(out var item);_.Dispose();}
}");

        [Test]
        public Task NamedUnderscoreLocalIsNotADiscard()
            => AnalyzerFixture.Check(@"using YLFramework.Pooling;
class C{void M(ObjectPool<object> p){var _=p.Rent(out var item);_.Dispose();}}");

        [Test]
        public Task UserConversionCanExplicitlyConsumeOwnership()
            => AnalyzerFixture.Check(@"using YLFramework.Pooling;
class Owner{public static implicit operator Owner(PoolLease<object> lease){lease.Dispose();return new Owner();}}
class C{void M(ObjectPool<object> p){_=(Owner)p.Rent(out _);}}");

        [Test]
        public Task LookalikeOwnersAndTokensDoNotAcquireFrameworkRules()
            => AnalyzerFixture.Check(@"namespace YLFramework.Pooling{
 public struct PoolLease<T>:System.IDisposable{public void Dispose(){}}
 public class ObjectPool<T>{public PoolLease<T> Rent(out T item){item=default;return default;}}
}
class C{void M(YLFramework.Pooling.ObjectPool<object> p){p.Rent(out _);}}
class Wrapper{public core::YLFramework.Pooling.PoolLease<object> Rent(out object item){item=null;return default;}}
class D{void M(Wrapper p){p.Rent(out _);}}");

        [Test]
        public async Task SourceRuntimeAlsoChecksTheRealPoolIdentity()
        {
            var tree = CSharpSyntaxTree.ParseText("class C{void M(YLFramework.Pooling.ObjectPool<object> p){p.Rent(out _);}}", AnalyzerFixture.Parse);
            var diagnostics = await AnalyzerFixture.Diagnostics(AnalyzerFixture.CoreCompilation().AddSyntaxTrees(tree));
            Assert.That(diagnostics.Select(value => value.Id), Is.EqualTo(new[] { "YL0011" }));
        }

        [Test]
        public async Task GeneratedAndIncompleteCallsDoNotProduceAnalyzerFailures()
        {
            const string source = "class C{void M(YLFramework.Pooling.ObjectPool<object> p){p.Rent(out _);}}";
            Assert.That(await AnalyzerFixture.Diagnostics(AnalyzerFixture.Compilation(source, "Pool.g.cs")), Is.Empty);
            var broken = AnalyzerFixture.Compilation(source.Replace("p.Rent", "missing.Rent"));
            var diagnostics = await broken.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new CoreUsageAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.That(diagnostics, Is.Empty);
        }

        [Test]
        public Task DeliberateContractTestsCanSuppressOnlyTheNewRule()
            => AnalyzerFixture.Check(@"using YLFramework.Pooling;
class C{void M(ObjectPool<object> p){
#pragma warning disable YL0011
 p.Rent(out _);
#pragma warning restore YL0011
 {|YL0011:p.Rent(out _)|};
}}");
    }
}
