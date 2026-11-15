using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace YLFramework.Analyzers
{
    internal static class PoolRules
    {
        internal static void Invocation(OperationAnalysisContext context, AnalyzerSymbols symbols)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var call = (IInvocationOperation)context.Operation;
            if (!symbols.PoolRents.Contains(call.TargetMethod.OriginalDefinition))
            {
                return;
            }

            IOperation value = call;
            while (value.Parent is IConversionOperation conversion && conversion.Operand == value && conversion.OperatorMethod == null)
            {
                value = conversion;
            }
            // 只检查确定的直接弃值；有用户转换或传递给其他调用时，不猜测被调方所有权。
            bool discarded = value.Parent is IExpressionStatementOperation ||
                value.Parent is ISimpleAssignmentOperation assignment && assignment.Value == value && assignment.Target is IDiscardOperation;
            if (discarded)
            {
                context.ReportDiagnostic(Diagnostic.Create(CoreUsageAnalyzer.Rules[10], call.Syntax.GetLocation(),
                    "调用 '" + AnalyzerSymbols.Display(call.TargetMethod) +
                    "' 丢弃了 PoolLease 租约；请用 using 保存，或在明确生命周期结束时 Dispose/Discard。仅保留 out 对象不能按租约合同归还。"));
            }
        }
    }
}
