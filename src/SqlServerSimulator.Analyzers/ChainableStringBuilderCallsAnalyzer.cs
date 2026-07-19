using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a run of two or more consecutive statements that each invoke a
/// <see cref="System.Text.StringBuilder"/> instance method returning the
/// builder itself (<c>Append</c>, <c>AppendLine</c>, <c>Insert</c>,
/// <c>Replace</c>, …) on the same builder and discard the result — either as
/// a bare expression statement or via a <c>_ =</c> discard. Such a run
/// collapses into one fluent chain (<c>sb.Append(a).Append(b)</c>) where only
/// the final builder is discarded.
/// </summary>
/// <remarks>
/// Detection is by symbol, not method name: the invoked method's containing
/// type and return type must both be <see cref="System.Text.StringBuilder"/>,
/// so every self-returning builder method qualifies and nothing else does.
/// A statement that already chains (<c>sb.Append(a).Append(b)</c>) is peeled
/// through its self-returning links to the <em>base</em> receiver, so a bare
/// <c>sb.Append(c)</c> beside it groups into the same run and merges onto the
/// existing chain. That base receiver must be a syntactically simple,
/// side-effect-free expression (an identifier, <c>this</c>, or a dotted chain
/// of those) and identical across the run — chaining evaluates it once instead
/// of per statement, which would be a semantic change for a call-valued root.
/// Intervening comments don't break a run: the equivalent chain can be split
/// across lines with the comment slotted between the chained calls.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ChainableStringBuilderCallsAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS006",
        title: "Consecutive StringBuilder calls should be chained",
        messageFormat: "{0} consecutive StringBuilder statements on '{1}' each discard the returned builder — merge them into one fluent chain so only the final result is discarded",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StringBuilder's mutating methods (Append, AppendLine, Insert, Replace, …) return the same builder. Two or more consecutive statements that each call such a method on the same builder and throw the result away repeat the receiver load per statement; the equivalent fluent chain evaluates the receiver once and discards a single builder. The rule only fires when the builder reference is a simple side-effect-free expression, so the rewrite is behavior-preserving.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var stringBuilderType = start.Compilation.GetTypeByMetadataName("System.Text.StringBuilder");
            if (stringBuilderType is null)
                return;

            start.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeStatements(nodeContext, stringBuilderType),
                SyntaxKind.Block,
                SyntaxKind.SwitchSection);
        });
    }

    private static void AnalyzeStatements(SyntaxNodeAnalysisContext context, INamedTypeSymbol stringBuilderType)
    {
        var statements = context.Node switch
        {
            BlockSyntax block => block.Statements,
            SwitchSectionSyntax section => section.Statements,
            _ => default,
        };

        var i = 0;
        while (i < statements.Count)
        {
            if (!TryGetBuilderCall(statements[i], context.SemanticModel, stringBuilderType, context.CancellationToken, out var receiver)
                || !IsSafeReference(receiver))
            {
                i++;
                continue;
            }

            var j = i + 1;
            while (j < statements.Count
                && TryGetBuilderCall(statements[j], context.SemanticModel, stringBuilderType, context.CancellationToken, out var next)
                && next.IsEquivalentTo(receiver, topLevel: false))
            {
                j++;
            }

            var runLength = j - i;
            if (runLength >= 2)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    statements[i].GetLocation(),
                    runLength,
                    receiver.ToString()));
                i = j;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// Recognizes a statement of the form <c>receiver.Method(...);</c> or
    /// <c>_ = receiver.Method(...);</c> where <c>Method</c> is a
    /// <see cref="System.Text.StringBuilder"/> instance method returning the
    /// builder. The receiver itself may already be a chain of such calls; the
    /// out-parameter is the chain's base receiver (the root the whole chain
    /// hangs off), which is what a merge would evaluate once.
    /// </summary>
    private static bool TryGetBuilderCall(
        StatementSyntax statement,
        SemanticModel model,
        INamedTypeSymbol stringBuilderType,
        CancellationToken cancellationToken,
        out ExpressionSyntax baseReceiver)
    {
        baseReceiver = null!;

        if (statement is not ExpressionStatementSyntax expressionStatement)
            return false;

        InvocationExpressionSyntax invocation;
        switch (expressionStatement.Expression)
        {
            case InvocationExpressionSyntax bare:
                invocation = bare;
                break;
            case AssignmentExpressionSyntax { Left: IdentifierNameSyntax left, Right: InvocationExpressionSyntax assigned }
                when model.GetSymbolInfo(left, cancellationToken).Symbol is IDiscardSymbol:
                invocation = assigned;
                break;
            default:
                return false;
        }

        if (!TryGetBuilderCallReceiver(invocation, model, stringBuilderType, cancellationToken, out var receiver))
            return false;

        // Peel any already-chained self-returning links down to the base
        // receiver, so `sb.Append(a).Append(b)` keys off `sb` and merges with
        // an adjacent `sb.Append(c)`.
        while (receiver is InvocationExpressionSyntax innerInvocation
            && TryGetBuilderCallReceiver(innerInvocation, model, stringBuilderType, cancellationToken, out var innerReceiver))
        {
            receiver = innerReceiver;
        }

        baseReceiver = receiver;
        return true;
    }

    /// <summary>
    /// When <paramref name="invocation"/> is <c>x.Method(...)</c> and
    /// <c>Method</c> is a self-returning <see cref="System.Text.StringBuilder"/>
    /// instance method, yields <c>x</c>; otherwise false.
    /// </summary>
    private static bool TryGetBuilderCallReceiver(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        INamedTypeSymbol stringBuilderType,
        CancellationToken cancellationToken,
        out ExpressionSyntax receiver)
    {
        receiver = null!;

        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return false;

        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            return false;

        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, stringBuilderType)
            || !SymbolEqualityComparer.Default.Equals(method.ReturnType, stringBuilderType))
        {
            return false;
        }

        receiver = member.Expression;
        return true;
    }

    /// <summary>
    /// True when <paramref name="expression"/> is a syntactically simple
    /// reference: an identifier, <c>this</c>, or a dotted chain of those.
    /// Method calls, indexers, and other side-effect-capable shapes are
    /// rejected — chaining the run reduces receiver evaluations from N to 1,
    /// which is a semantic change in the presence of side effects.
    /// </summary>
    private static bool IsSafeReference(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case IdentifierNameSyntax:
                case ThisExpressionSyntax:
                    return true;
                case MemberAccessExpressionSyntax member when member.Name is IdentifierNameSyntax:
                    expression = member.Expression;
                    continue;
                default:
                    return false;
            }
        }
    }
}
