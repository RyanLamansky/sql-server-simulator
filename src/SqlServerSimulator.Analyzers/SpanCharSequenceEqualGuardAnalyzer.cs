using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a <c>switch</c> over a <see cref="System.Span{Char}"/> /
/// <see cref="System.ReadOnlySpan{Char}"/> whose arm is written as a discard
/// guard <c>_ when &lt;governing&gt;.SequenceEqual("literal")</c> instead of
/// the equivalent constant string pattern <c>"literal"</c>. Since C# 11 a
/// span-of-char switch matches string constants directly (the compiler
/// lowers the pattern to the same element-wise comparison), so the
/// hand-written <c>SequenceEqual</c> guard is pure noise — and, being a
/// guard, it also opts the whole switch out of the
/// <c>SortedConstantSwitchAnalyzer</c> (SSS005) ordering check that the
/// constant-pattern form would enjoy.
/// </summary>
/// <remarks>
/// This is the enforcement companion to <c>TransientUpperLowerInvariant</c>
/// (SSS003): that rule pushes an uppercased switch scrutinee onto a
/// <c>stackalloc</c> <c>Span&lt;char&gt;</c>, and the natural next mistake is
/// to reach for <c>SequenceEqual</c> guards — the pre-C#-11 idiom — rather
/// than plain string-literal arms. The <c>ResolveBuiltIn</c> keyword-dispatch
/// tables in <c>Parser/Expression.cs</c> are the reference for the intended
/// shape: a <c>Span&lt;char&gt;</c> scrutinee with bare <c>"NAME" =&gt; …</c>
/// arms. Only the pure single-invocation guard is flagged (a negated or
/// <c>&amp;&amp;</c>-combined condition has no single-pattern equivalent and
/// is left alone), and only when the receiver is the switch's own governing
/// expression (otherwise no constant pattern can replace it).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SpanCharSequenceEqualGuardAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS007",
        title: "Span<char> switch guard should be a constant string pattern",
        messageFormat: "Replace the guard '_ when {0}.SequenceEqual(\"{1}\")' with the constant pattern \"{1}\" — a Span<char>/ReadOnlySpan<char> switch matches string constants directly since C# 11",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A switch over Span<char> / ReadOnlySpan<char> matches string-literal patterns directly since C# 11 — the compiler lowers a constant string pattern to the same element-wise comparison a hand-written SequenceEqual performs. Writing the arm as a '_ when scrutinee.SequenceEqual(\"literal\")' guard is therefore redundant, and because it is a guard it also exempts the whole switch from the SSS005 sorted-arms check that the constant-pattern form would be held to. Only the pure single-invocation guard whose receiver is the switch's governing expression is flagged; a negated or combined condition, or one probing a different span, has no single-pattern equivalent and is left alone.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        // Switch expressions only. A discard-with-guard case label
        // (`case _ when …`) doesn't parse as a discard in a switch statement
        // — `_` reads as an identifier there — so the anti-pattern this rule
        // targets can only be written in the expression form (which is also
        // where the clean span-constant-pattern alternative applies).
        context.RegisterSyntaxNodeAction(AnalyzeSwitchExpression, SyntaxKind.SwitchExpression);
    }

    private static void AnalyzeSwitchExpression(SyntaxNodeAnalysisContext context)
    {
        var switchExpression = (SwitchExpressionSyntax)context.Node;
        var governing = switchExpression.GoverningExpression;
        if (!IsSpanOfChar(governing, context.SemanticModel, context.CancellationToken))
            return;

        foreach (var arm in switchExpression.Arms)
        {
            if (arm.Pattern is DiscardPatternSyntax && arm.WhenClause is { Condition: { } condition })
                InspectGuard(context, governing, condition);
        }
    }

    // Reports when `condition` is exactly `<governing>.SequenceEqual(<const
    // string>)` — the manual spelling of a constant string pattern.
    private static void InspectGuard(SyntaxNodeAnalysisContext context, ExpressionSyntax governing, ExpressionSyntax condition)
    {
        // netstandard2.0 (the analyzer TFM) has no System.Index, so list
        // patterns aren't available — match arity explicitly.
        if (condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "SequenceEqual",
                    Expression: { } receiver,
                },
                ArgumentList.Arguments: { Count: 1 } arguments,
            })
        {
            return;
        }

        var argument = arguments[0].Expression;

        // The receiver must be the switch's own scrutinee — a constant
        // pattern can only replace a guard that probes the governing value.
        if (!SyntaxFactory.AreEquivalent(receiver, governing))
            return;

        if (context.SemanticModel.GetConstantValue(argument, context.CancellationToken).Value is not string text)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, condition.GetLocation(), receiver.ToString(), text));
    }

    // True when the switch scrutinee is System.Span<char> or
    // System.ReadOnlySpan<char> — the two types C# 11's constant-string
    // pattern support applies to.
    private static bool IsSpanOfChar(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetTypeInfo(expression, cancellationToken).Type is INamedTypeSymbol
        {
            Name: "Span" or "ReadOnlySpan",
            ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true },
            TypeArguments: { Length: 1 } typeArguments,
        } && typeArguments[0].SpecialType == SpecialType.System_Char;
}
