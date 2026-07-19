using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a chain of two or more <c>if</c>/<c>else if</c> branches (or a run
/// of consecutive <c>if (cond) { return/throw; }</c> early-exit statements)
/// where every condition has the shape
/// <c>&lt;sameScrutinee&gt; is &lt;SameType&gt; { &lt;SameProperty&gt;: ... }</c>.
/// Converting the chain to a single <c>switch</c> with one arm per pattern
/// lets the C# compiler emit one <c>isinst</c> and one property/field read
/// for the whole dispatch, instead of repeating the type check and read on
/// each <c>if</c>.
/// </summary>
/// <remarks>
/// The IL win is real: a two-arm <c>if</c>/<c>else if</c> chain over the
/// same property pattern emits two <c>isinst</c> + two <c>ldfld</c>
/// (verified via <c>ilspycmd -il</c>); the equivalent <c>switch</c> emits
/// one of each and dispatches the discriminant value via plain
/// <c>ldloc</c>/<c>beq</c>. Token-dispatch hot paths (parsers, row
/// decoders) hit this enough that the cumulative cost is observable.
/// The rule is conservative — it only flags chains where the scrutinee
/// is a syntactically simple chain of identifiers / property accesses
/// (locals, parameters, <c>this</c>, dotted names), since those are
/// effectively pure in this codebase. Anything more complex is skipped
/// to avoid a semantic-changing rewrite.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PropertyPatternChainAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS004",
        title: "Repeated property-pattern type checks should be a single switch",
        messageFormat: "{0} consecutive '{1}' branches test the same scrutinee against the same '{2}.{3}' property pattern — convert to a single switch so the compiler emits one isinst and one property read",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An if/else-if chain (or a run of if-{return/throw} early-exit statements) where every condition has the shape '<sameScrutinee> is <SameType> { <SameProperty>: ... }' emits one isinst + one property/field read per arm. The equivalent switch fuses both: the C# compiler emits a single isinst and a single property read for the whole dispatch.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.SwitchSection);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;

        // Only analyze the head of an if/else-if chain. If our parent is an
        // else clause, the outer if was already analyzed and walked the chain
        // through us.
        if (ifStatement.Parent is ElseClauseSyntax)
            return;

        if (!TryExtractPropertyPattern(ifStatement.Condition, out var firstScrutinee, out var firstTypeSyntax, out var firstPropertyName))
            return;

        if (!IsSafeScrutinee(firstScrutinee))
            return;

        if (context.SemanticModel.GetSymbolInfo(firstTypeSyntax, context.CancellationToken).Symbol is not INamedTypeSymbol firstTypeSymbol)
            return;

        var matchCount = 1;
        var current = ifStatement;
        while (current.Else?.Statement is IfStatementSyntax nextIf)
        {
            if (!TryExtractPropertyPattern(nextIf.Condition, out var s, out var t, out var p)
                || !s.IsEquivalentTo(firstScrutinee, topLevel: false)
                || p != firstPropertyName)
            {
                break;
            }

            if (context.SemanticModel.GetSymbolInfo(t, context.CancellationToken).Symbol is not INamedTypeSymbol nextTypeSymbol
                || !SymbolEqualityComparer.Default.Equals(nextTypeSymbol, firstTypeSymbol))
            {
                break;
            }

            matchCount++;
            current = nextIf;
        }

        if (matchCount < 2)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            ifStatement.IfKeyword.GetLocation(),
            matchCount,
            "if/else if",
            firstTypeSymbol.Name,
            firstPropertyName));
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
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
            if (statements[i] is not IfStatementSyntax head
                || head.Else != null
                || !BodyAlwaysExits(head.Statement)
                || !TryExtractPropertyPattern(head.Condition, out var firstScrutinee, out var firstTypeSyntax, out var firstPropertyName)
                || !IsSafeScrutinee(firstScrutinee))
            {
                i++;
                continue;
            }

            if (context.SemanticModel.GetSymbolInfo(firstTypeSyntax, context.CancellationToken).Symbol is not INamedTypeSymbol firstTypeSymbol)
            {
                i++;
                continue;
            }

            var matchCount = 1;
            var j = i + 1;
            while (j < statements.Count)
            {
                if (statements[j] is not IfStatementSyntax nextIf
                    || nextIf.Else != null
                    || !BodyAlwaysExits(nextIf.Statement)
                    || !TryExtractPropertyPattern(nextIf.Condition, out var s, out var t, out var p)
                    || !s.IsEquivalentTo(firstScrutinee, topLevel: false)
                    || p != firstPropertyName)
                {
                    break;
                }

                if (context.SemanticModel.GetSymbolInfo(t, context.CancellationToken).Symbol is not INamedTypeSymbol nextTypeSymbol
                    || !SymbolEqualityComparer.Default.Equals(nextTypeSymbol, firstTypeSymbol))
                {
                    break;
                }

                matchCount++;
                j++;
            }

            if (matchCount >= 2)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    head.IfKeyword.GetLocation(),
                    matchCount,
                    "if-return",
                    firstTypeSymbol.Name,
                    firstPropertyName));
                i = j;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// Pulls (scrutinee, type-syntax, property-name) out of <c>X is T { P: ... }</c>.
    /// Returns false for any other condition shape, including when the property
    /// pattern carries multiple subpatterns or a designation
    /// (<c>X is T { P: ... } v</c>) — the latter would force the rewrite to
    /// rename across arms.
    /// </summary>
    private static bool TryExtractPropertyPattern(
        ExpressionSyntax condition,
        out ExpressionSyntax scrutinee,
        out TypeSyntax typeSyntax,
        out string propertyName)
    {
        scrutinee = null!;
        typeSyntax = null!;
        propertyName = null!;

        if (condition is not IsPatternExpressionSyntax isPattern)
            return false;

        if (isPattern.Pattern is not RecursivePatternSyntax recursive)
            return false;

        if (recursive.Type is null)
            return false;

        if (recursive.Designation is not null)
            return false;

        if (recursive.PositionalPatternClause is not null)
            return false;

        if (recursive.PropertyPatternClause is not { } propertyClause)
            return false;

        if (propertyClause.Subpatterns.Count != 1)
            return false;

        var sub = propertyClause.Subpatterns[0];
        if (sub.NameColon?.Name is not IdentifierNameSyntax propIdent)
            return false;

        scrutinee = isPattern.Expression;
        typeSyntax = recursive.Type;
        propertyName = propIdent.Identifier.ValueText;
        return true;
    }

    /// <summary>
    /// True when <paramref name="expression"/> is a syntactically simple
    /// reference: an identifier, <c>this</c>, or a dotted chain of those.
    /// Method calls, indexers, casts, and other side-effect-capable shapes
    /// are rejected — converting an N-arm if-chain to a switch reduces
    /// scrutinee evaluations from N to 1, which is a semantic change in
    /// the presence of side effects.
    /// </summary>
    private static bool IsSafeScrutinee(ExpressionSyntax expression)
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

    /// <summary>
    /// True when <paramref name="statement"/> unconditionally exits its
    /// enclosing block — a single <c>return</c>/<c>throw</c>/<c>continue</c>/<c>break</c>/<c>goto</c>
    /// or a block whose final statement is one of those (and which has no
    /// fall-through path; we approximate by requiring the last statement to
    /// be the exit). This is the shape that lets if-return chains play the
    /// same dispatch role as if/else-if.
    /// </summary>
    private static bool BodyAlwaysExits(StatementSyntax statement)
    {
        return statement switch
        {
            ReturnStatementSyntax => true,
            ThrowStatementSyntax => true,
            ContinueStatementSyntax => true,
            BreakStatementSyntax => true,
            GotoStatementSyntax => true,
            BlockSyntax block when LastIsExit(block.Statements) => true,
            _ => false,
        };

        static bool LastIsExit(SyntaxList<StatementSyntax> statements) =>
            statements.Count > 0 && BodyAlwaysExits(statements[statements.Count - 1]);
    }
}
