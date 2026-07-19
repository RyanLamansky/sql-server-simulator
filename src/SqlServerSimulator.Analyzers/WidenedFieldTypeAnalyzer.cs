using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags <c>readonly</c> fields in non-public-API types whose declared type
/// is a strict supertype (base class or implemented interface) of the
/// immediately-assigned initializer's static type.
/// </summary>
/// <remarks>
/// <para>
/// In a non-public type, no API-stability boundary justifies hiding the
/// concrete shape of an immediately-bound singleton. Declaring the field as
/// the abstract type forces every same-assembly read to go through virtual
/// dispatch (and stops the IDE from offering members the concrete type
/// adds). Declaring the field as the concrete type lets callers see
/// non-virtual / non-overridden members directly and shrinks the call-site
/// indirection.
/// </para>
/// <para>
/// The rule fires only when an immediate initializer pins the runtime type
/// at compile time. Fields without initializers, fields whose initializer
/// already matches the declared type, and fields whose initializer is a
/// value type (treating the declared abstract type as a deliberate boxing
/// boundary) are exempt. Public types are also exempt: the declared type
/// there is the documented API contract.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WidenedFieldTypeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS002",
        title: "Field declared as an abstract type but initialized with a more specific value",
        messageFormat: "Field '{0}' is declared as '{1}' but its initializer produces '{2}'; in non-public type '{3}', declare the field as '{2}' to expose the concrete shape directly",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A readonly field whose declared type is a base class or interface of its immediate initializer's runtime type provides no API-stability benefit when the containing type isn't part of the public API. Declare the field as the concrete type so same-assembly callers see the specific members and avoid virtual dispatch.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeFieldDeclaration, SyntaxKind.FieldDeclaration);
    }

    private static void AnalyzeFieldDeclaration(SyntaxNodeAnalysisContext context)
    {
        var fieldDecl = (FieldDeclarationSyntax)context.Node;

        // Const fields are restricted by the compiler to compile-time
        // primitives that must match the declared type exactly — there's no
        // widening to flag. readonly is the gate; non-readonly fields can
        // legitimately be reassigned to less-specific values later.
        if (fieldDecl.Modifiers.Any(SyntaxKind.ConstKeyword))
            return;
        if (!fieldDecl.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            return;

        foreach (var declarator in fieldDecl.Declaration.Variables)
        {
            if (declarator.Initializer is not { Value: { } initializerExpression })
                continue;

            if (context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken) is not IFieldSymbol field)
                continue;

            var containingType = field.ContainingType;
            if (containingType is null || IsEffectivelyPublic(containingType))
                continue;

            if (field.Type is not INamedTypeSymbol declaredType)
                continue;

            var initializerType = context.SemanticModel.GetTypeInfo(initializerExpression, context.CancellationToken).Type;
            if (initializerType is not INamedTypeSymbol assignedType)
                continue;

            // Same type → no widening; nothing to flag. (Reference equality
            // via SymbolEqualityComparer is the right check; nominal name
            // matches won't appear here because the semantic model returns
            // the same canonical symbol on both sides.)
            if (SymbolEqualityComparer.Default.Equals(declaredType, assignedType))
                continue;

            // Value-typed initializer with a reference-typed field is a
            // deliberate boxing boundary — declaring the field as the value
            // type would change semantics. Leave it alone.
            if (assignedType.IsValueType)
                continue;

            // Confirm the declared type is actually a supertype of the
            // assigned type. (Defensive: the code compiled, so this should
            // always hold for a non-equal pair, but null and tuple types
            // can sneak through TypeInfo.)
            if (!IsAssignableTo(assignedType, declaredType))
                continue;

            var location = declarator.Identifier.GetLocation();
            var diagnostic = Diagnostic.Create(
                Rule,
                location,
                field.Name,
                declaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                assignedType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                containingType.Name);
            context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>
    /// True when <paramref name="source"/> is a strict subtype of
    /// <paramref name="target"/> — either through the base-class chain or
    /// because <paramref name="target"/> is one of <paramref name="source"/>'s
    /// implemented interfaces.
    /// </summary>
    private static bool IsAssignableTo(INamedTypeSymbol source, INamedTypeSymbol target)
    {
        for (var current = source.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        }
        foreach (var iface in source.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, target))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True iff <paramref name="type"/> and every containing type up to the
    /// namespace are <c>public</c>. Mirrors the
    /// <see cref="WrapperPropertyAnalyzer"/> exemption.
    /// </summary>
    private static bool IsEffectivelyPublic(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public
        && (type.ContainingType is null || IsEffectivelyPublic(type.ContainingType));
}
