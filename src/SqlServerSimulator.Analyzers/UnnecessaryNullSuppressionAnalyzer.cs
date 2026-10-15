using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a null-forgiving <c>x!</c> whose operand is not-null by its
/// <em>declaration</em>: a parameter, local, field, property, indexer or method
/// return whose type is a non-nullable reference type, a non-nullable value
/// type, <c>this</c>, an object creation, or a string, <c>nameof</c> or
/// <c>typeof</c> expression. The suppression silences nothing there. It stands
/// in for the SDK's IDE0370, which proves unnecessary suppressions by rebinding
/// the code around each one without it and was the costliest code-style
/// analyzer in a build.
/// </summary>
/// <remarks>
/// <para>
/// The semantic model reports an expression's flow state <em>after</em> its
/// suppression, so the rule can't ask whether a <c>string?</c> local is not-null
/// at a given point; it judges from declarations alone, which costs one symbol
/// lookup per <c>!</c>. That makes it narrower than IDE0370: a suppression on a
/// nullable-declared value that flow analysis has already proven not-null (a
/// <c>var</c> local after a null check, say) goes unreported.
/// </para>
/// <para>
/// A declaration doesn't settle the state where flow analysis starts it
/// maybe-null or where nullability attributes widen it, so the rule also skips:
/// a member of a struct (whose <c>default</c> leaves every field null), a
/// field or property read inside a constructor, a type-parameter type, a
/// member carrying <c>[MaybeNull]</c> / <c>[AllowNull]</c> /
/// <c>[MaybeNullWhen]</c>, and code in a <c>#nullable disable</c> region. A
/// suppression also silences a mismatch in <em>nested</em> nullability (a
/// <c>List&lt;string?&gt;</c> passed as a <c>List&lt;string&gt;</c>), which a
/// declaration can't rule out, so an operand of a generic, array or tuple type
/// is skipped too, as are assignment targets and <c>ref</c> / <c>out</c>
/// arguments.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnnecessaryNullSuppressionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS017",
        title: "Unnecessary null-forgiving operator",
        messageFormat: "'{0}' is declared not-null, so the '!' suppresses nothing; remove it",
        category: "Style",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The null-forgiving operator tells the compiler to trust that an expression isn't null. On a value whose declaration already makes it non-nullable (a non-nullable parameter, local, field, property or method result, a value type, 'this', a 'new' expression or a string) the operator is noise, and it outlives the code that once needed it: a later change that does make the value nullable gets no warning at this site. Remove the '!'. The rule skips struct members, fields and properties read in a constructor, type parameters, members with nullability attributes, generic, array and tuple types (where '!' may silence a nested nullability mismatch), assignment targets, ref and out arguments, and #nullable disable regions. It replaces the SDK's IDE0370, which is disabled for build speed.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SuppressNullableWarningExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var suppression = (PostfixUnaryExpressionSyntax)context.Node;
        switch (suppression.Parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Left == suppression:
            case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
                return;
        }

        var operand = suppression.Operand;
        while (operand is ParenthesizedExpressionSyntax parenthesized)
            operand = parenthesized.Expression;

        if (!IsDeclaredNotNull(operand, context.SemanticModel, context.ContainingSymbol, context.CancellationToken))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, suppression.GetLocation(), suppression.Operand.ToString()));
    }

    private static bool IsDeclaredNotNull(ExpressionSyntax operand, SemanticModel model, ISymbol? containingSymbol, System.Threading.CancellationToken cancellationToken)
    {
        switch (operand)
        {
            case ThisExpressionSyntax:
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }:
            case InterpolatedStringExpressionSyntax:
            case TypeOfExpressionSyntax:
                return true;
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
                return model.GetTypeInfo(operand, cancellationToken).Type is { } created && IsNotNullType(created, NullableAnnotation.NotAnnotated);
            case IdentifierNameSyntax:
            case MemberAccessExpressionSyntax:
            case InvocationExpressionSyntax:
            case ElementAccessExpressionSyntax:
                break;
            default:
                return false;
        }

        return model.GetSymbolInfo(operand, cancellationToken).Symbol switch
        {
            ILocalSymbol local => !local.IsRef && IsNotNullType(local.Type, local.NullableAnnotation),
            IParameterSymbol parameter => parameter.RefKind is RefKind.None or RefKind.In
                && !HasNullabilityAttribute(parameter.GetAttributes())
                && IsNotNullType(parameter.Type, parameter.NullableAnnotation),
            IFieldSymbol field => !IsFlowTrackedMember(field, containingSymbol)
                && !HasNullabilityAttribute(field.GetAttributes())
                && IsNotNullType(field.Type, field.NullableAnnotation),
            IPropertySymbol property => !IsFlowTrackedMember(property, containingSymbol)
                && !HasNullabilityAttribute(property.GetAttributes())
                && (property.GetMethod is null || !HasNullabilityAttribute(property.GetMethod.GetReturnTypeAttributes()))
                && IsNotNullType(property.Type, property.NullableAnnotation),
            IMethodSymbol method when operand is InvocationExpressionSyntax => !method.ReturnsByRef
                && !HasNullabilityAttribute(method.GetReturnTypeAttributes())
                && IsNotNullType(method.ReturnType, method.ReturnNullableAnnotation),
            _ => model.GetConstantValue(operand, cancellationToken) is { HasValue: true, Value: string },
        };
    }

    /// <summary>
    /// True when a value of <paramref name="type"/> declared with
    /// <paramref name="annotation"/> can't be null and carries no nested
    /// annotation a <c>!</c> might be silencing.
    /// </summary>
    private static bool IsNotNullType(ITypeSymbol type, NullableAnnotation annotation)
    {
        if (type is ITypeParameterSymbol || HasNestedNullability(type))
            return false;

        if (type.IsValueType)
            return true;

        return type.IsReferenceType && annotation == NullableAnnotation.NotAnnotated;
    }

    /// <summary>
    /// True when a nullability annotation can sit inside <paramref name="type"/>
    /// rather than only on it: a constructed generic (or a type nested in one,
    /// <see cref="System.Nullable{T}"/> and tuples included), an array or a pointer.
    /// </summary>
    private static bool HasNestedNullability(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol:
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
                return true;
            case INamedTypeSymbol named:
                for (var current = named; current is not null; current = current.ContainingType)
                {
                    if (current.TypeArguments.Length != 0)
                        return true;
                }

                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// True when flow analysis may hold <paramref name="member"/> maybe-null
    /// whatever its declaration says: a member of a struct, whose
    /// <c>default</c> leaves every field null, or a member of the type whose
    /// constructor is running, which starts its fields maybe-null.
    /// </summary>
    private static bool IsFlowTrackedMember(ISymbol member, ISymbol? containingSymbol) =>
        member.ContainingType.IsValueType
        || (containingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor
            && SymbolEqualityComparer.Default.Equals(member.ContainingType, constructor.ContainingType));

    private static bool HasNullabilityAttribute(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            switch (attribute.AttributeClass?.Name)
            {
                case "AllowNullAttribute":
                case "MaybeNullAttribute":
                case "MaybeNullWhenAttribute":
                    return true;
            }
        }

        return false;
    }
}
