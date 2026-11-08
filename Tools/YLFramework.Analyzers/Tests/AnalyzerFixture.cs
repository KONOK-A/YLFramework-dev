using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace YLFramework.Analyzers.Tests
{
    internal static class AnalyzerFixture
    {
        internal const string Prefix = "extern alias core; using System; using System.Collections.Generic; using YLFramework.Core.RoslynAnalyzers;\n";
        internal static readonly CSharpParseOptions Parse = new CSharpParseOptions(LanguageVersion.CSharp9);
        internal static readonly MetadataReference[] Platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
        internal static readonly string Root = FindRoot();
        internal const string PoolSource = "namespace YLFramework.Pooling { public class ObjectPool<T> { private T[] storage; public ObjectPool(System.Func<T> factory) {} public T Allocate()=>default; } }";
        internal static readonly MetadataReference Core = CreateCore();

        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ProjectSettings/ProjectVersion.txt")))
                directory = directory.Parent;
            if (directory == null)
                throw new InvalidOperationException("Tests must run from the configured repository output directory.");
            return directory.FullName;
        }

        internal static MetadataReference Emit(string name, IEnumerable<string> sources, IEnumerable<MetadataReference> references)
        {
            var compilation = CSharpCompilation.Create(name, sources.Select(s => CSharpSyntaxTree.ParseText(s, Parse)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            return Emit(compilation);
        }

        private static MetadataReference Emit(CSharpCompilation compilation)
        {
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics));
            return MetadataReference.CreateFromImage(stream.ToArray());
        }

        internal static CSharpCompilation CoreCompilation(string replacement = null, bool includeInject = true)
        {
            var sources = Directory.GetFiles(Path.Combine(Root, "PackageSources/com.kano.ylframework/Runtime/Core/Attributes"), "*.cs")
                .OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllText).ToList();
            if (includeInject)
            {
                sources.Add(File.ReadAllText(Path.Combine(Root,
                    "PackageSources/com.kano.ylframework/Runtime/Foundation/IOC/Attributes/InjectAttribute.cs")));
            }
            sources.Add("namespace UnityEngine {}");
            sources.Add("namespace UnityEngine { public class Transform {} public class GameObject {} public class WaitUntil { public WaitUntil(System.Func<bool> predicate) {} } }");
            sources.Add(PoolSource);
            sources.Add(replacement ?? @"using System;
namespace UnityEngine {}
namespace YLFramework.Core {
 public class View {
  private void OnEnable(){} private void OnDisable(){} private void OnDestroy(){}
  protected virtual void OnViewEnabled(){} protected virtual void OnViewDisabled(){} protected virtual void OnClosed(){}
  [RoslynAnalyzers.MustCallBase(""Start reason"")] protected virtual void Start(){}
  [RoslynAnalyzers.RestrictedCall(""Awake"")] protected void RegisterEvent(Action callback){}
 }
 [RoslynAnalyzers.ProtectedHierarchy(typeof(View))] public class Model { public int Value; public int Property {get;set;} }
  public class RestrictedApi {
   [RoslynAnalyzers.WarnInClasses(typeof(View),""View reason"")] public static void Observe(Action callback){}
 }
 [RoslynAnalyzers.DisableNew(""Use New"")][RoslynAnalyzers.MethodLocalOnly(""Lease reason"")]
 public class LoadViewArgs {
   private static readonly Pooling.ObjectPool<LoadViewArgs> _pool = new Pooling.ObjectPool<LoadViewArgs>(()=>new LoadViewArgs());
   public static LoadViewArgs New()=>_pool.Allocate();
 }
  public class LoadViewRequest<T> { private LoadViewArgs _args; }
  public class PanelManager { internal sealed class PanelOperation { internal LoadViewArgs Args; } }
  public class Panel : View {}
  public class LoadPanelRequest<T> {}
  public abstract class Module {
   public T LoadView<T>(string assetName,UnityEngine.Transform parent=null,LoadViewArgs loadViewArgs=null) where T:View=>default;
   public T LoadView<T>(UnityEngine.GameObject prefab,UnityEngine.Transform parent=null,LoadViewArgs loadViewArgs=null) where T:View=>default;
   public T LoadView<T>(UnityEngine.Transform parent=null,LoadViewArgs loadViewArgs=null) where T:View=>default;
   public LoadViewRequest<T> LoadViewAsync<T>(string assetName,UnityEngine.Transform parent=null,LoadViewArgs loadViewArgs=null) where T:View=>default;
   public LoadViewRequest<T> LoadViewAsync<T>(UnityEngine.Transform parent=null,LoadViewArgs loadViewArgs=null) where T:View=>default;
   public View LoadView(string assetName,UnityEngine.Transform parent=null,LoadViewArgs loadViewArgs=null)=>default;
   public LoadViewRequest<View> LoadViewAsync(string assetName,UnityEngine.Transform parent=null,LoadViewArgs loadViewArgs=null)=>default;
   public T LoadPanel<T>(string assetName,LoadViewArgs loadViewArgs=null) where T:Panel=>default;
   public LoadPanelRequest<T> LoadPanelAsync<T>(string assetName,LoadViewArgs loadViewArgs=null) where T:Panel=>default;
   public T AttachView<T>(T view,LoadViewArgs loadViewArgs=null) where T:View=>view;
   public T GetView<T>(string assetName) where T:View=>default;
   public System.Collections.Generic.List<T> GetViews<T>() where T:View=>default;
  }
 }
 namespace YLFramework.Audio {
  public sealed class AudioPlayRequest:System.IDisposable {
   public bool IsDone=>false;public bool HasStarted=>false;public void Dispose(){}
  }
 }");
            return CSharpCompilation.Create("YLFramework.Runtime", sources.Select(s => CSharpSyntaxTree.ParseText(s, Parse)),
                Platform, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        private static MetadataReference CreateCore() => Emit(CoreCompilation())
            .WithProperties(new MetadataReferenceProperties(aliases: ImmutableArray.Create("global", "core")));

        internal static CSharpCompilation Compilation(string source, string path = "Consumer.cs", bool core = true)
            => CSharpCompilation.Create("Consumer", new[] { CSharpSyntaxTree.ParseText(source, Parse, path) },
                core ? Platform.Concat(new[] { Core }) : Platform,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        internal static async Task<ImmutableArray<Diagnostic>> Diagnostics(CSharpCompilation compilation, CancellationToken token = default)
        {
            var errors = compilation.GetDiagnostics(token).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(d => d.ToString())));
            var result = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new CoreUsageAnalyzer()),
                cancellationToken: token).GetAnalyzerDiagnosticsAsync(token);
            Assert.That(result.Where(d => d.Id == "AD0001"), Is.Empty, string.Join("\n", result));
            return result;
        }

        internal static async Task Check(string marked, params string[] messageParts)
        {
            var clean = new StringBuilder(Prefix);
            var expected = new List<(string id, TextSpan span)>();
            var end = 0;
            foreach (Match match in Regex.Matches(marked, @"\{\|(YL\d{4}):(.+?)\|\}", RegexOptions.Singleline))
            {
                clean.Append(marked, end, match.Index - end);
                expected.Add((match.Groups[1].Value, new TextSpan(clean.Length, match.Groups[2].Length)));
                clean.Append(match.Groups[2].Value);
                end = match.Index + match.Length;
            }
            clean.Append(marked, end, marked.Length - end);
            var diagnostics = (await Diagnostics(Compilation(clean.ToString()))).OrderBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id).ToArray();
            var wanted = expected.OrderBy(e => e.span.Start).ThenBy(e => e.id).ToArray();
            Assert.That(diagnostics.Length, Is.EqualTo(wanted.Length), string.Join("\n", diagnostics.Select(d => d.ToString())));
            for (int i = 0; i < wanted.Length; i++)
            {
                Assert.That(diagnostics[i].Id, Is.EqualTo(wanted[i].id));
                Assert.That(diagnostics[i].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
                Assert.That(diagnostics[i].Location.SourceSpan, Is.EqualTo(wanted[i].span));
                Assert.That(diagnostics[i].GetMessage(), Does.Match(@"[\u4e00-\u9fff]"));
                foreach (var part in messageParts)
                    Assert.That(diagnostics[i].GetMessage(), Does.Contain(part));
            }
        }
    }
}
