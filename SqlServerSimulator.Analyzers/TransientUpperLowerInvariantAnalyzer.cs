using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags <see cref="string.ToUpperInvariant"/> / <see cref="string.ToLowerInvariant"/>
/// calls whose result is consumed directly by a <c>switch</c> expression /
/// statement — the transient case where the allocation can be avoided by
/// using the <c>ToUpperInvariant(Span&lt;char&gt;)</c> /
/// <c>ToLowerInvariant(Span&lt;char&gt;)</c> overloads with a stackalloc
/// destination and switching on the resulting <see cref="System.ReadOnlySpan{Char}"/>.
/// </summary>
/// <remarks>
/// Pairs naturally with the parser's keyword-dispatch hot paths — see
/// <c>Parser/Expression.cs:ResolveBuiltIn</c> and <c>Storage/SqlType.cs:GetByName</c>
/// for the established Span pattern. Switch-arm pattern equality on
/// <c>string</c> literals works the same against <see cref="System.ReadOnlySpan{Char}"/>
/// since C# 11, so the call-site rewrite is mechanical:
/// <code>
/// // before:  return s.ToUpperInvariant() switch { "X" =&gt; ..., _ =&gt; null };
/// // after:   Span&lt;char&gt; buf = stackalloc char[s.Length];
/// //          return s.AsSpan().ToUpperInvariant(buf) switch { "X" =&gt; ..., _ =&gt; null };
/// // (or pre-validate length and emit a diagnostic for over-long input)
/// </code>
/// Allocating uses where the upper/lower string itself is the function's
/// returned value (e.g. <c>UPPER</c>'s SQL semantics, GUID-to-string casts)
/// don't trip the rule since their result isn't a switch governing expression.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TransientUpperLowerInvariantAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS003",
        title: "Allocating ToUpperInvariant/ToLowerInvariant feeding a switch",
        messageFormat: "string.{0}() allocates a temporary string but the result is fed directly to a switch — use the Span<char> overload with a stackalloc destination",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.ToUpperInvariant() and string.ToLowerInvariant() each return a new string. When the result is consumed only by a switch (expression or statement), the Span<char> overload writing into a stackalloc buffer does the same case-folding without allocation; switch arms accept ReadOnlySpan<char> against string-literal patterns identically since C# 11.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return;

        var methodName = member.Name.Identifier.ValueText;
        if (methodName is not "ToUpperInvariant" and not "ToLowerInvariant")
            return;

        // The Span<char> overload takes one argument; the allocating one
        // takes none. Skip the already-allocation-free form.
        if (invocation.ArgumentList.Arguments.Count != 0)
            return;

        // Receiver must be a System.String instance — not Span<char>, not a
        // user-defined extension target. The semantic model gives us the
        // canonical type symbol; SpecialType pins the BCL string identity.
        var receiverType = context.SemanticModel.GetTypeInfo(member.Expression, context.CancellationToken).Type;
        if (receiverType?.SpecialType != SpecialType.System_String)
            return;

        if (!IsSwitchGoverningExpression(invocation))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, member.Name.GetLocation(), methodName));
    }

    /// <summary>
    /// True iff <paramref name="node"/> sits in the governing-expression slot
    /// of a <c>switch</c> statement or <c>switch</c> expression — the only
    /// position where the rewrite to <see cref="System.ReadOnlySpan{Char}"/>
    /// is mechanical. Walks through trivial syntactic wrappers (parentheses)
    /// but stops at any other parent kind, so e.g.
    /// <c>s.ToUpperInvariant().Length switch { ... }</c> is not flagged
    /// (the governing expression is the <c>.Length</c> access).
    /// </summary>
    private static bool IsSwitchGoverningExpression(SyntaxNode node)
    {
        var current = node.Parent;
        while (current is ParenthesizedExpressionSyntax parens)
            current = parens.Parent;
        return current is SwitchExpressionSyntax or SwitchStatementSyntax;
    }
}
