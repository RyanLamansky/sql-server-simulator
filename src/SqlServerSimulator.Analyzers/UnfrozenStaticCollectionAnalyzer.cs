using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a <c>static readonly</c> field whose type is one of the
/// general-purpose collections from <c>System.Collections</c>,
/// <c>System.Collections.Generic</c> or <c>System.Collections.Immutable</c>.
/// A collection reachable only through a static field is fixed for the
/// process's lifetime, so it should be declared as an array or a
/// <c>System.Collections.Frozen</c> type — both of which lay out their
/// contents once and read faster than a shape that has to stay ready for
/// mutation.
/// </summary>
/// <remarks>
/// <para>
/// The motivation is throughput, not immutability. <c>FrozenDictionary</c> /
/// <c>FrozenSet</c> spend extra time in construction — which a static field
/// pays exactly once — to pick a lookup strategy specialized to the keys they
/// actually hold, so every subsequent read beats the equivalent
/// <c>Dictionary</c> / <c>HashSet</c>. Arrays are permitted for the same
/// reason rather than any safety one: a fixed run of elements scanned or
/// indexed by position has no cheaper representation.
/// </para>
/// <para>
/// <c>System.Collections.Immutable</c>'s dictionary, set, list, queue and
/// stack types are flagged alongside the mutable ones. They are already safe,
/// but they buy that safety with a tree that costs more per lookup than
/// either permitted shape — which is the wrong trade for contents that were
/// never going to change. <see cref="ImmutableArray{T}"/> is exempt: it is an
/// array.
/// </para>
/// <para>
/// A <see cref="System.Lazy{T}"/> wrapper is unwrapped before the check, so
/// deferring construction doesn't hide the collection inside it — the fix
/// there is to freeze what the factory returns.
/// </para>
/// <para>
/// Two shapes stay off the list deliberately. The
/// <c>System.Collections.Concurrent</c> types exist to be mutated after
/// publication, which is what a static memo cache legitimately does.
/// <c>PriorityQueue</c> has no frozen or array form that preserves its
/// dequeue order. Anything else genuinely mutated after initialization takes
/// a <c>#pragma warning disable SSS008</c> with a one-line rationale, the
/// same escape hatch <c>SortedConstantSwitchAnalyzer</c> (SSS005) uses for a
/// switch ordered by meaning.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnfrozenStaticCollectionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS008",
        title: "Static collection should be an array or a Frozen collection",
        messageFormat: "Static field '{0}' is declared as '{1}'; declare it as {2} — a collection reachable only through a static field never changes after initialization, so it should be laid out once for reading",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A static readonly collection is fixed for the process's lifetime, so it should be declared as an array or a System.Collections.Frozen type. FrozenDictionary / FrozenSet pay a one-time construction cost to specialize their lookup to the keys they hold, making every read faster than the equivalent Dictionary / HashSet; an array is the cheapest representation of a fixed run of elements. System.Collections.Immutable's dictionary, set and list types are flagged too — they are safe but pay a per-lookup tree walk for contents that were never going to change — while ImmutableArray is exempt because it is an array. A Lazy<T> wrapper is unwrapped before the check. Concurrent collections and PriorityQueue are exempt; anything else mutated after initialization suppresses with #pragma warning disable SSS008 and a rationale.");

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

        // static is the whole premise: an instance field's collection is
        // per-object state with a normal mutable lifetime. readonly narrows to
        // the fields that are bound once — a reassignable static holds
        // whatever the last write put there, so its declared type has to stay
        // general. (const can't name a collection type at all.)
        if (!fieldDecl.Modifiers.Any(SyntaxKind.StaticKeyword))
            return;
        if (!fieldDecl.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            return;

        foreach (var declarator in fieldDecl.Declaration.Variables)
        {
            if (context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken) is not IFieldSymbol field)
                continue;

            if (field.Type is not INamedTypeSymbol declaredType)
                continue;

            var collection = UnwrapLazy(declaredType);
            if (ReplacementFor(collection.Name) is not { } replacement)
                continue;

            // The name switch is a cheap filter; the namespace check is what
            // makes the match real. A project-local type named `List` or
            // `Dictionary` is not this rule's business.
            if (!IsCollectionsNamespace(collection.ContainingNamespace))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                declarator.Identifier.GetLocation(),
                field.Name,
                declaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                replacement));
        }
    }

    /// <summary>
    /// The permitted shape a collection named <paramref name="typeName"/>
    /// should be declared as, or <see langword="null"/> when the name isn't
    /// one this rule governs. Key/value collections map to
    /// <c>FrozenDictionary</c>, set-like ones to <c>FrozenSet</c>, and the
    /// purely sequential ones to an array.
    /// </summary>
    private static string? ReplacementFor(string typeName) => typeName switch
    {
        "ArrayList" => "an array",
        "Dictionary" => "a FrozenDictionary",
        "HashSet" => "a FrozenSet",
        "Hashtable" => "a FrozenDictionary",
        "ImmutableDictionary" => "a FrozenDictionary",
        "ImmutableHashSet" => "a FrozenSet",
        "ImmutableList" => "an array",
        "ImmutableQueue" => "an array",
        "ImmutableSortedDictionary" => "a FrozenDictionary",
        "ImmutableSortedSet" => "a FrozenSet",
        "ImmutableStack" => "an array",
        "LinkedList" => "an array",
        "List" => "an array",
        "Queue" => "an array",
        "SortedDictionary" => "a FrozenDictionary",
        "SortedList" => "a FrozenDictionary",
        "SortedSet" => "a FrozenSet",
        "Stack" => "an array",
        _ => null,
    };

    /// <summary>
    /// Peels <see cref="System.Lazy{T}"/> off <paramref name="type"/> so a
    /// deferred collection is judged by what it defers to. Loops rather than
    /// unwrapping once so a nested wrapper can't slip a collection past.
    /// </summary>
    private static INamedTypeSymbol UnwrapLazy(INamedTypeSymbol type)
    {
        while (type is { Name: "Lazy", TypeArguments: { Length: 1 } typeArguments }
            && type.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true }
            && typeArguments[0] is INamedTypeSymbol inner)
        {
            type = inner;
        }

        return type;
    }

    /// <summary>
    /// True for <c>System.Collections</c> and its <c>Generic</c> /
    /// <c>Immutable</c> children — the three namespaces holding the
    /// general-purpose collections this rule governs.
    /// <c>System.Collections.Concurrent</c> is excluded: those types are built
    /// to be written after publication.
    /// </summary>
    private static bool IsCollectionsNamespace(INamespaceSymbol? containingNamespace)
    {
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
}
