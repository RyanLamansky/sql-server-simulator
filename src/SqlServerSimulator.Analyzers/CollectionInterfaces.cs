using Microsoft.CodeAnalysis;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// The general-purpose collection interfaces the declared-type rules govern:
/// the ones from <c>System.Collections</c> and its <c>Generic</c> /
/// <c>Immutable</c> children — the same three namespaces
/// <see cref="UnfrozenStaticCollectionAnalyzer"/> (SSS008) frames its static-field
/// rule around.
/// </summary>
/// <remarks>
/// Shared by <see cref="WidenedParameterTypeAnalyzer"/> (SSS010) and
/// <see cref="WidenedReturnTypeAnalyzer"/> (SSS011) rather than duplicated:
/// two copies of the list would be two things to remember when a name is added,
/// and the two rules are the same judgement made in two declaration positions.
/// </remarks>
internal static class CollectionInterfaces
{
    /// <summary>
    /// True when <paramref name="type"/> is one of the governed interfaces. A
    /// project-local interface that happens to be named <c>IList</c> is not
    /// these rules' business, and neither is a domain interface that merely
    /// derives from one of them.
    /// </summary>
    internal static bool IsGoverned(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Interface)
            return false;

        switch (type.Name)
        {
            case "ICollection":
            case "IDictionary":
            case "IEnumerable":
            case "IImmutableDictionary":
            case "IImmutableList":
            case "IImmutableQueue":
            case "IImmutableSet":
            case "IImmutableStack":
            case "IList":
            case "IReadOnlyCollection":
            case "IReadOnlyDictionary":
            case "IReadOnlyList":
            case "IReadOnlySet":
            case "ISet":
                break;
            default:
                return false;
        }

        var containingNamespace = type.ContainingNamespace;
        if (containingNamespace is null)
            return false;

        if (containingNamespace.Name is "Generic" or "Immutable")
            containingNamespace = containingNamespace.ContainingNamespace;

        return containingNamespace is
        {
            Name: "Collections",
            ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true },
        };
    }

    /// <summary>
    /// True when <paramref name="type"/> is or contains a type parameter, so a
    /// replacement read off a call site or a return expression would have to be
    /// respelled in the declaration's own generic vocabulary.
    /// </summary>
    internal static bool MentionsTypeParameter(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return true;
            case IArrayTypeSymbol array:
                return MentionsTypeParameter(array.ElementType);
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments)
                {
                    if (MentionsTypeParameter(argument))
                        return true;
                }

                return named.ContainingType is not null && MentionsTypeParameter(named.ContainingType);
            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="method"/> owns its signature and isn't public
    /// API surface — the two conditions that make rewriting a declared type a
    /// local edit rather than a contract change.
    /// </summary>
    internal static bool IsFlaggableMethod(IMethodSymbol method)
    {
        // A local function is never API surface and never overrides anything;
        // everything below is about members of a type.
        if (method.MethodKind == MethodKind.LocalFunction)
            return true;

        if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor))
            return false;

        if (method.IsOverride || method.IsVirtual || method.IsAbstract || method.IsExtern)
            return false;

        if (!method.ExplicitInterfaceImplementations.IsEmpty)
            return false;

        // A partial method declares the same signature twice, and call sites
        // bind to the defining part alone — reporting either half would leave
        // the other's evidence unread.
        if (method.PartialDefinitionPart is not null || method.PartialImplementationPart is not null)
            return false;

        if (method.ContainingType is not { } containingType)
            return false;

        if (ImplementsInterfaceMember(method, containingType))
            return false;

        var isApiSurface =
            method.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
            && IsEffectivelyPublic(containingType);
        return !isApiSurface;
    }

    /// <summary>
    /// True when <paramref name="method"/> implicitly implements an interface
    /// member — its signature is the interface's, not its own.
    /// </summary>
    private static bool ImplementsInterfaceMember(IMethodSymbol method, INamedTypeSymbol containingType)
    {
        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                if (member is not IMethodSymbol)
                    continue;
                if (SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(member), method))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True iff <paramref name="type"/> and every containing type up to the
    /// namespace are <c>public</c>. Mirrors the
    /// <see cref="WidenedFieldTypeAnalyzer"/> exemption.
    /// </summary>
    private static bool IsEffectivelyPublic(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public
        && (type.ContainingType is null || IsEffectivelyPublic(type.ContainingType));
}
