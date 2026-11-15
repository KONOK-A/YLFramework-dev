using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace YLFramework.Analyzers
{
    internal sealed class AnalyzerSymbols
    {
        internal readonly IAssemblySymbol Core;
        internal readonly INamedTypeSymbol[] Markers;
        internal readonly Compilation Compilation;
        internal readonly INamedTypeSymbol ObjectPool;
        internal readonly HashSet<IMethodSymbol> PoolRents = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        internal readonly INamedTypeSymbol View;
        internal readonly INamedTypeSymbol Inject;
        internal readonly INamedTypeSymbol Module;
        internal readonly INamedTypeSymbol Panel;
        internal readonly INamedTypeSymbol AudioRequest;
        private readonly IFieldSymbol _poolOwner;
        private readonly IFieldSymbol _requestOwner;
        private readonly IFieldSymbol _panelConsumerOwner;
        private readonly bool[] _inherited;
        private readonly ConcurrentDictionary<ISymbol, ImmutableArray<AttributeData>>[] _attributes;
        private readonly ConcurrentDictionary<AttributeData, ImmutableArray<string>> _strings = new ConcurrentDictionary<AttributeData, ImmutableArray<string>>();

        private AnalyzerSymbols(IAssemblySymbol core, Compilation compilation)
        {
            Core = core;
            Compilation = compilation;
            View = core.GetTypeByMetadataName("YLFramework.Core.View");
            Inject = core.GetTypeByMetadataName("YLFramework.DependencyInjection.InjectAttribute");
            Module = core.GetTypeByMetadataName("YLFramework.Core.Module");
            Panel = core.GetTypeByMetadataName("YLFramework.Core.Panel");
            AudioRequest = core.GetTypeByMetadataName("YLFramework.Audio.AudioPlayRequest");
            var names = new[] { "MustCallBase", "RestrictedCall", "WarnInClasses", "DisableNew", "MethodLocalOnly", "ProtectedHierarchy" };
            Markers = names.Select(n => core.GetTypeByMetadataName("YLFramework.Core.RoslynAnalyzers." + n + "Attribute")).ToArray();
            _inherited = Markers.Select(m => m == null || !m.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute" && a.NamedArguments.Any(p => p.Key == "Inherited" && Equals(p.Value.Value, false)))).ToArray();
            _attributes = names.Select(_ => new ConcurrentDictionary<ISymbol, ImmutableArray<AttributeData>>(SymbolEqualityComparer.Default)).ToArray();
            ObjectPool = core.GetTypeByMetadataName("YLFramework.Pooling.ObjectPool`1");
            var poolLease = core.GetTypeByMetadataName("YLFramework.Pooling.PoolLease`1");
            if (poolLease != null)
            {
                foreach (var name in new[] { "ObjectPool`1", "SafeObjectPool`1", "ListPool`1", "DictionaryPool`2" })
                {
                    var pool = core.GetTypeByMetadataName("YLFramework.Pooling." + name);
                    if (pool == null)
                    {
                        continue;
                    }
                    foreach (var method in pool.GetMembers("Rent").OfType<IMethodSymbol>())
                    {
                        if (method.ReturnType is INamedTypeSymbol result &&
                            SymbolEqualityComparer.Default.Equals(result.OriginalDefinition, poolLease))
                        {
                            PoolRents.Add(method.OriginalDefinition);
                        }
                    }
                }
            }
            var args = core.GetTypeByMetadataName("YLFramework.Core.LoadViewArgs");
            var candidates = args?.GetMembers("_pool").OfType<IFieldSymbol>().ToArray();
            var field = candidates?.Length == 1 ? candidates[0] : null;
            if (field != null && field.DeclaredAccessibility == Accessibility.Private && field.IsStatic && field.IsReadOnly &&
                field.Type is INamedTypeSymbol fieldType && SymbolEqualityComparer.Default.Equals(fieldType.OriginalDefinition, ObjectPool) &&
                fieldType.TypeArguments.Length == 1 && SymbolEqualityComparer.Default.Equals(fieldType.TypeArguments[0], args) && !Attributes(args, 4).IsEmpty)
            {
                _poolOwner = field;
            }

            var request = core.GetTypeByMetadataName("YLFramework.Core.LoadViewRequest`1");
            candidates = request?.GetMembers("_args").OfType<IFieldSymbol>().ToArray();
            field = candidates?.Length == 1 ? candidates[0] : null;
            if (request?.Arity == 1 && field != null && field.DeclaredAccessibility == Accessibility.Private && !field.IsStatic &&
                SymbolEqualityComparer.Default.Equals(field.Type, args))
            {
                _requestOwner = field;
            }

            var consumer = core.GetTypeByMetadataName("YLFramework.Core.PanelManager+PanelOperation");
            candidates = consumer?.GetMembers("Args").OfType<IFieldSymbol>().ToArray();
            field = candidates?.Length == 1 ? candidates[0] : null;
            if (consumer?.Arity == 0 && field != null && field.DeclaredAccessibility == Accessibility.Internal && !field.IsStatic &&
                SymbolEqualityComparer.Default.Equals(field.Type, args))
            {
                _panelConsumerOwner = field;
            }
        }

        internal static AnalyzerSymbols Create(Compilation compilation)
        {
            var candidates = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            if (compilation.Assembly.Name == "YLFramework.Runtime")
            {
                candidates.Add(compilation.Assembly);
            }

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly && assembly.Name == "YLFramework.Runtime")
                {
                    candidates.Add(assembly);
                }
            }

            return candidates.Count == 1 ? new AnalyzerSymbols(candidates.Single(), compilation) : null;
        }

        internal bool IsOwner(ISymbol member) => member is IFieldSymbol field &&
            (SymbolEqualityComparer.Default.Equals(field.OriginalDefinition, _poolOwner) || SymbolEqualityComparer.Default.Equals(field.OriginalDefinition, _requestOwner) ||
             SymbolEqualityComparer.Default.Equals(field.OriginalDefinition, _panelConsumerOwner));

        internal (INamedTypeSymbol type, AttributeData attribute) Marked(ITypeSymbol type, int rule, CancellationToken token)
        {
            foreach (var candidate in ClassTypes(type))
            {
                token.ThrowIfCancellationRequested();
                var effective = Attributes(candidate, rule);
                if (effective.IsEmpty)
                {
                    continue;
                }

                for (var owner = candidate; owner != null; owner = owner.BaseType)
                {
                    token.ThrowIfCancellationRequested();
                    if (owner.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, Markers[rule]) && !HasError(a)))
                    {
                        return (owner, effective[0]);
                    }
                }
            }
            return (null, null);
        }

        internal static IEnumerable<INamedTypeSymbol> ClassTypes(ITypeSymbol type)
        {
            var pending = new Stack<ITypeSymbol>();
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            if (type != null)
            {
                pending.Push(type);
            }

            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current) || current.TypeKind == TypeKind.Error)
                {
                    continue;
                }

                if (current is ITypeParameterSymbol parameter)
                {
                    foreach (var constraint in parameter.ConstraintTypes)
                    {
                        if (constraint.TypeKind == TypeKind.Class || constraint.TypeKind == TypeKind.TypeParameter)
                        {
                            pending.Push(constraint);
                        }
                    }
                }
                else if (current is INamedTypeSymbol named)
                {
                    yield return named;
                }
            }
        }

        internal ImmutableArray<AttributeData> Attributes(ISymbol symbol, int rule)
        {
            if (symbol == null || Markers[rule] == null)
            {
                return ImmutableArray<AttributeData>.Empty;
            }

            if (symbol is IMethodSymbol method)
            {
                symbol = (method.ReducedFrom ?? method).OriginalDefinition;
            }

            return _attributes[rule].GetOrAdd(symbol, key =>
            {
                var result = ImmutableArray.CreateBuilder<AttributeData>();
                for (var current = key; current != null; current = Parent(current))
                {
                    result.AddRange(current.GetAttributes().Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, Markers[rule]) && !HasError(a)));
                    if (!_inherited[rule] || (rule != 2 && result.Count != 0))
                    {
                        break;
                    }
                }
                return result.ToImmutable();
            });
        }

        private static ISymbol Parent(ISymbol symbol)
            => symbol is IMethodSymbol method ? method.OverriddenMethod : (symbol as INamedTypeSymbol)?.BaseType;

        internal static bool HasError(AttributeData attribute) => attribute.ConstructorArguments.Any(Error);
        private static bool Error(TypedConstant constant)
            => constant.Kind == TypedConstantKind.Error || (constant.Kind == TypedConstantKind.Array && !constant.IsNull && constant.Values.Any(Error));

        internal ImmutableArray<string> Strings(AttributeData attribute)
            => _strings.GetOrAdd(attribute, a => a.ConstructorArguments.SelectMany(Flatten).Select(v => v.Value as string)
                .Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToImmutableArray());

        internal static IEnumerable<TypedConstant> Flatten(TypedConstant constant)
        {
            if (constant.Kind == TypedConstantKind.Array)
            {
                if (!constant.IsNull)
                {
                    foreach (var value in constant.Values)
                    {
                        foreach (var leaf in Flatten(value))
                        {
                            yield return leaf;
                        }
                    }
                }
            }
            else
            {
                yield return constant;
            }
        }

        internal static IEnumerable<INamedTypeSymbol> Types(AttributeData attribute)
            => attribute.ConstructorArguments.SelectMany(Flatten).Where(v => v.Kind == TypedConstantKind.Type)
                .Select(v => v.Value).OfType<INamedTypeSymbol>();

        internal static bool Matches(ITypeSymbol type, INamedTypeSymbol target)
        {
            if (type == null || type.TypeKind == TypeKind.Error || target.TypeKind == TypeKind.Error)
            {
                return false;
            }

            bool Same(INamedTypeSymbol candidate) => candidate != null && SymbolEqualityComparer.Default.Equals(
                target.IsUnboundGenericType ? candidate.OriginalDefinition : candidate,
                target.IsUnboundGenericType ? target.OriginalDefinition : target);
            for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                if (Same(current))
                {
                    return true;
                }
            }

            return type.AllInterfaces.Any(Same);
        }

        internal static string Display(ISymbol symbol) => symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "<未知>";
        internal string Reason(AttributeData attribute) => string.Join("；", Strings(attribute));
    }
}
