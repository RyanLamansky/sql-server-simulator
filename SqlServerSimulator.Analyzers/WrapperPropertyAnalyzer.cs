using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags properties on non-public types that could be plain fields.
/// </summary>
/// <remarks>
/// <para>
/// In a non-public type, both auto-properties (<c>public T Foo { get; }</c>) and
/// trivial wrapper properties (<c>public T Foo =&gt; this.field;</c>) compile to
/// the same shape — a backing field plus a getter method. Since there is no
/// public API surface to protect, the property metadata is pure overhead. This
/// analyzer flags both forms; the recommended fix is to expose the underlying
/// field directly (typically with <c>readonly</c>).
/// </para>
/// <para>
/// Public types are exempt — there the property provides genuine API-stability
/// flexibility (the rationale behind CA2227 and similar guidance). Overrides and
/// explicit interface implementations are also exempt: their API shape is
/// dictated by the base type or interface and isn't optional. Static
/// auto-properties / wrappers are <em>not</em> exempt — they carry the same
/// backing-field + getter-method overhead as instance ones with no compensating
/// API-stability benefit on a non-public type.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WrapperPropertyAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS001",
        title: "Property in non-public type carries unnecessary metadata over a plain field",
        messageFormat: "Property '{0}' in non-public type '{1}' should be a plain field; non-public types gain no API-stability benefit from property wrappers or auto-properties",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "In non-public types, both auto-properties and trivial wrapper properties carry property metadata (a getter method and a vtable slot) without an API-stability benefit. Expose the underlying field directly instead.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzePropertyDeclaration, SyntaxKind.PropertyDeclaration);
    }

    private static void AnalyzePropertyDeclaration(SyntaxNodeAnalysisContext context)
    {
        var propertyDecl = (PropertyDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(propertyDecl, context.CancellationToken) is not IPropertySymbol property)
            return;

        if (property.GetMethod is null)
            return;

        // Overrides, abstract/extern declarations, explicit interface
        // implementations, and implicit interface implementations all have
        // their API shape dictated by something else — the property isn't
        // optional. (Implicit implementation: a regular public property
        // whose name and signature match an interface member of an
        // implemented interface.)
        if (property.IsOverride || property.IsAbstract || property.IsExtern
            || property.ExplicitInterfaceImplementations.Length > 0
            || ImplementsInterfaceMember(property))
        {
            return;
        }

        if (IsEffectivelyPublic(property.ContainingType))
            return;

        if (!IsPropertyConvertibleToField(propertyDecl, context.SemanticModel, property.ContainingType, context.CancellationToken))
            return;

        var diagnostic = Diagnostic.Create(
            Rule,
            propertyDecl.Identifier.GetLocation(),
            property.Name,
            property.ContainingType.Name);
        context.ReportDiagnostic(diagnostic);
    }

    /// <summary>
    /// Returns <c>true</c> when the property is either an auto-property (no
    /// custom accessors) or a trivial wrapper whose getter resolves to a field
    /// of the same containing type.
    /// </summary>
    private static bool IsPropertyConvertibleToField(PropertyDeclarationSyntax property, SemanticModel semanticModel, INamedTypeSymbol containingType, CancellationToken cancellationToken)
    {
        if (IsAutoProperty(property))
            return true;

        var bodyExpression = GetTrivialGetterExpression(property);
        if (bodyExpression is null)
            return false;

        var symbol = semanticModel.GetSymbolInfo(bodyExpression, cancellationToken).Symbol;
        return symbol is IFieldSymbol field
            && SymbolEqualityComparer.Default.Equals(field.ContainingType, containingType);
    }

    /// <summary>
    /// True when every accessor has neither a body nor an expression body —
    /// i.e., the property is auto-implemented (the compiler supplies a backing
    /// field).
    /// </summary>
    private static bool IsAutoProperty(PropertyDeclarationSyntax property) =>
        property.ExpressionBody is null
        && property.AccessorList is { } accessorList
        && accessorList.Accessors.All(a => a.Body is null && a.ExpressionBody is null);

    /// <summary>
    /// True iff <paramref name="type"/> and every containing type up to the namespace are <c>public</c>.
    /// A public type nested in an internal type is not effectively public.
    /// </summary>
    private static bool IsEffectivelyPublic(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public
        && (type.ContainingType is null || IsEffectivelyPublic(type.ContainingType));

    /// <summary>
    /// True when <paramref name="property"/> is the implementation of an
    /// interface member declared on one of the containing type's
    /// implemented interfaces. Catches the implicit-implementation case
    /// (regular public property satisfying an interface contract) that
    /// <see cref="IPropertySymbol.ExplicitInterfaceImplementations"/> doesn't
    /// surface.
    /// </summary>
    private static bool ImplementsInterfaceMember(IPropertySymbol property)
    {
        var containingType = property.ContainingType;
        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers())
            {
                if (ifaceMember is not IPropertySymbol)
                    continue;
                if (containingType.FindImplementationForInterfaceMember(ifaceMember) is { } impl
                    && SymbolEqualityComparer.Default.Equals(impl, property))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the expression that the property's getter resolves to, when that
    /// getter is a single-expression body or a single-statement <c>return</c>.
    /// Returns <c>null</c> for auto-properties, computed bodies, multi-statement
    /// getters, or properties without a getter.
    /// </summary>
    private static ExpressionSyntax? GetTrivialGetterExpression(PropertyDeclarationSyntax property)
    {
        var expression = property.ExpressionBody?.Expression ?? FindGetterBody(property.AccessorList);
        return expression is null ? null : Unwrap(expression);
    }

    private static ExpressionSyntax? FindGetterBody(AccessorListSyntax? accessors)
    {
        var getter = accessors?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        return getter switch
        {
            { ExpressionBody: { } expression } => expression.Expression,
            { Body.Statements: { Count: 1 } statements } when statements[0] is ReturnStatementSyntax { Expression: { } returned } => returned,
            _ => null,
        };
    }

    /// <summary>Strips redundant parentheses (<c>(x)</c> → <c>x</c>) before symbol lookup.</summary>
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }
}
