using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using YLFramework.Editor.Core.Analyzers;

namespace YLFramework.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class CoreUsageAnalyzer : DiagnosticAnalyzer
    {
        internal static readonly ImmutableArray<DiagnosticDescriptor> Rules = AnalyzerRuleCatalog.Rules.Select(rule =>
            new DiagnosticDescriptor(rule.Id, rule.Title, "{0}", rule.Category,
                DiagnosticSeverity.Warning, true, description: rule.Description)).ToImmutableArray();

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Rules;

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var symbols = AnalyzerSymbols.Create(start.Compilation);
                if (symbols == null)
                {
                    return;
                }

                if (symbols.View != null)
                {
                    start.RegisterSymbolAction(c => ViewLifecycleRules.Message(c, symbols), SymbolKind.Method);
                }

                if (symbols.Inject != null)
                {
                    start.RegisterSymbolAction(c => InjectionRules.Member(c, symbols), SymbolKind.Field, SymbolKind.Property);
                    start.RegisterSymbolAction(c => InjectionRules.BackingFields(c, symbols), SymbolKind.NamedType);
                    start.RegisterSyntaxNodeAction(c => InjectionRules.EventFields(c, symbols), SyntaxKind.EventFieldDeclaration);
                }

                if (symbols.Module != null && symbols.Panel != null)
                {
                    start.RegisterOperationAction(c => PanelLoadingRules.Invocation(c, symbols), OperationKind.Invocation);
                }

                if (symbols.AudioRequest != null)
                {
                    start.RegisterSyntaxNodeAction(c => AudioRules.Yield(c, symbols), SyntaxKind.YieldReturnStatement);
                }

                if (symbols.PoolRents.Count != 0)
                {
                    start.RegisterOperationAction(c => PoolRules.Invocation(c, symbols), OperationKind.Invocation);
                }

                if (symbols.Markers[0] != null)
                {
                    start.RegisterSyntaxNodeAction(c => CallRules.MustCallBase(c, symbols), SyntaxKind.MethodDeclaration);
                }

                if (symbols.Markers[1] != null || symbols.Markers[2] != null)
                {
                    start.RegisterOperationAction(c => CallRules.Invocation(c, symbols), OperationKind.Invocation);
                }

                if (symbols.Markers[3] != null)
                {
                    start.RegisterOperationAction(c => OwnershipRules.Creation(c, symbols), OperationKind.ObjectCreation, OperationKind.TypeParameterObjectCreation);
                }

                if (symbols.Markers[4] != null)
                {
                    var storage = new StorageModel(symbols);
                    start.RegisterSymbolAction(c => OwnershipRules.Member(c, symbols, storage), SymbolKind.Field, SymbolKind.Property);
                    start.RegisterSyntaxNodeAction(c => OwnershipRules.RecordParameter(c, symbols, storage), SyntaxKind.Parameter);
                }
                if (symbols.Markers[5] != null)
                {
                    start.RegisterOperationAction(c => ProtectionRules.Write(c, symbols), OperationKind.SimpleAssignment,
                        OperationKind.CompoundAssignment, OperationKind.CoalesceAssignment, OperationKind.Increment,
                        OperationKind.Decrement, OperationKind.DeconstructionAssignment, OperationKind.Argument);
                    start.RegisterSyntaxNodeAction(c => ProtectionRules.Reference(c, symbols), SyntaxKind.RefExpression);
                }
            });
        }
    }
}
