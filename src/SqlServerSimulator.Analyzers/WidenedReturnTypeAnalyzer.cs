using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a non-public method or local function whose return type is one of the
/// general-purpose collection interfaces (see
/// <see cref="CollectionInterfaces"/>) when every <c>return</c> in its body
/// produces the same concrete type. The return-position sibling of
/// <see cref="WidenedParameterTypeAnalyzer"/> (SSS010) and of
/// <see cref="WidenedFieldTypeAnalyzer"/> (SSS002).
/// </summary>
/// <remarks>
/// <para>
/// A materialized collection handed back as an interface costs the caller
/// twice: the members it reads dispatch virtually, and a <c>foreach</c> over it
/// allocates <c>List&lt;T&gt;</c>'s boxed enumerator instead of binding the
/// struct enumerator the concrete type exposes. Neither buys anything when the
/// body has exactly one shape to give — the flexibility an interface return
/// genuinely buys is *deferred execution*, and that shape is exempt below.
/// </para>
/// <para>
/// Iterators are exempt by construction: a body with <c>yield</c> can only be
/// declared as <c>IEnumerable&lt;T&gt;</c> / <c>IEnumerator&lt;T&gt;</c>, and
/// its laziness is the reason to keep it that way. The remaining exemptions
/// mirror SSS010's — public and protected members of public types, overrides,
/// abstract and virtual members, interface implementations, partial methods,
/// <c>ref</c> returns, and a return type mentioning a type parameter.
/// </para>
/// <para>
/// Evidence is the body's own <c>return</c> statements (an expression body
/// counts as one), read past nested lambdas and local functions, which return
/// to themselves. A <see langword="null"/> or <c>default</c> return contributes
/// no type but blocks a value-typed replacement that couldn't restate it; a
/// return whose own type is an interface settles the member as genuinely
/// interface-returning; two different concrete types are real polymorphism. A
/// body that only throws has nothing to judge.
/// </para>
/// <para>
/// Unlike SSS010 this rule is local to the declaration, so it needs no
/// compilation-wide pass — but it also can't see a method group converted to a
/// delegate. Return-type covariance makes that conversion survive a narrowing
/// to a reference type; a narrowing to a <em>struct</em> collection would break
/// it, and the compiler says so. That case takes
/// <c>#pragma warning disable SSS011</c> with a one-line rationale.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WidenedReturnTypeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS011",
        title: "Return type declared as a collection interface every return statement satisfies with one concrete type",
        messageFormat: "'{0}' returns '{1}', but every return produces '{2}'; declare the return type as '{2}' so callers read it directly and can bind its own enumerator",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A non-public method or local function that materializes one concrete collection and hands it back as an interface makes every caller pay virtual dispatch and a boxed enumerator for flexibility the body never uses. Declare the concrete type. Iterator methods are exempt by construction — deferred execution is what an interface return legitimately buys — as are public API surface, overrides, interface implementations, and bodies whose returns disagree.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var declaration = (MethodDeclarationSyntax)context.Node;
        Analyze(context, declaration.ReturnType, declaration.Body, declaration.ExpressionBody);
    }

    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context)
    {
        var declaration = (LocalFunctionStatementSyntax)context.Node;
        Analyze(context, declaration.ReturnType, declaration.Body, declaration.ExpressionBody);
    }

    private static void Analyze(
        SyntaxNodeAnalysisContext context,
        TypeSyntax returnTypeSyntax,
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? expressionBody)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, context.CancellationToken) is not IMethodSymbol method)
            return;

        // A ref return names a storage location, not a value: the returned
        // expression's type has to match the declaration exactly.
        if (method.RefKind != RefKind.None)
            return;

        if (!CollectionInterfaces.IsFlaggableMethod(method))
            return;

        if (method.ReturnType is not INamedTypeSymbol declaredType || !CollectionInterfaces.IsGoverned(declaredType))
            return;

        if (CollectionInterfaces.MentionsTypeParameter(declaredType))
            return;

        // An iterator can't name a concrete type at all, and its laziness is
        // the flexibility the interface is there for.
        if (body is not null && ContainsYield(body))
            return;

        ITypeSymbol? settled = null;
        var sawNull = false;

        if (expressionBody is not null)
        {
            if (!Fold(context, expressionBody.Expression, ref settled, ref sawNull))
                return;
        }
        else if (body is not null)
        {
            foreach (var statement in body.DescendantNodes(descendIntoChildren: DescendsIntoBody).OfType<ReturnStatementSyntax>())
            {
                if (statement.Expression is not { } expression)
                    continue;
                if (!Fold(context, expression, ref settled, ref sawNull))
                    return;
            }
        }

        if (settled is null)
            return;

        // A struct collection can't absorb the null an interface return
        // accepted, so the replacement would be inexpressible.
        if (settled.IsValueType && sawNull)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            returnTypeSyntax.GetLocation(),
            method.Name,
            declaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            settled.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    /// <summary>
    /// Folds one returned expression into the verdict so far. Returns
    /// <see langword="false"/> when the body has disqualified itself and the
    /// walk should stop.
    /// </summary>
    private static bool Fold(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, ref ITypeSymbol? settled, ref bool sawNull)
    {
        // `default` takes the declared type by definition, and `null` has no
        // type: neither says anything about what the body materializes.
        if (expression.IsKind(SyntaxKind.DefaultLiteralExpression) || expression.IsKind(SyntaxKind.DefaultExpression))
        {
            sawNull = true;
            return true;
        }

        var type = context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type;
        if (type is null)
        {
            sawNull = true;
            return true;
        }

        // An interface (the body is handing back what it was given), a type
        // parameter, or a type the binder couldn't resolve.
        if (type.TypeKind is TypeKind.Interface or TypeKind.TypeParameter or TypeKind.Error)
            return false;

        if (settled is null)
        {
            settled = type;
            return true;
        }

        return SymbolEqualityComparer.Default.Equals(settled, type);
    }

    /// <summary>
    /// True when the body is an iterator's. Only the body's own
    /// <c>yield</c>s count — one inside a nested lambda or local function
    /// belongs to that function.
    /// </summary>
    private static bool ContainsYield(SyntaxNode body) =>
        body.DescendantNodes(descendIntoChildren: DescendsIntoBody)
            .Any(node => node.IsKind(SyntaxKind.YieldReturnStatement) || node.IsKind(SyntaxKind.YieldBreakStatement));

    /// <summary>
    /// Keeps a body walk out of the nested functions whose <c>return</c>s and
    /// <c>yield</c>s belong to themselves rather than to the declaration being
    /// judged.
    /// </summary>
    private static bool DescendsIntoBody(SyntaxNode node) =>
        !node.IsKind(SyntaxKind.ParenthesizedLambdaExpression)
        && !node.IsKind(SyntaxKind.SimpleLambdaExpression)
        && !node.IsKind(SyntaxKind.AnonymousMethodExpression)
        && !node.IsKind(SyntaxKind.LocalFunctionStatement);
}
