using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a non-public type declared as a <c>record</c>. The declaration is a
/// request for a fixed set of synthesized members — <c>Equals</c>,
/// <c>GetHashCode</c>, the equality operators, <c>ToString</c>,
/// <c>PrintMembers</c>, a copy constructor and, positionally,
/// <c>Deconstruct</c> — and the compiler emits all of them whether or not the
/// code calls any. On a non-public type none of that is API surface, so each
/// uncalled member is assembly metadata that ships and an uncovered member in
/// every coverage report.
/// </summary>
/// <remarks>
/// <para>
/// Value equality alone does not justify the shape. A type used as a dictionary
/// key or in a hash set should implement <c>IEquatable&lt;T&gt;</c> directly:
/// that emits the two members the lookup actually calls and nothing else, and
/// it keeps a struct off <c>ValueType.Equals</c>, whose reflection-and-boxing
/// fallback is the real hazard a bare struct carries.
/// </para>
/// <para>
/// The features that do justify a record are the ones nothing else supplies:
/// a <c>with</c> expression, deconstruction, or a printed <c>ToString</c> the
/// code genuinely reads. A record kept for one of those suppresses with
/// <c>#pragma warning disable SSS009</c> and a one-line rationale — the same
/// escape hatch <see cref="SortedConstantSwitchAnalyzer"/> (SSS005) and
/// <see cref="UnfrozenStaticCollectionAnalyzer"/> (SSS008) use. Note that a
/// <c>with</c> expression needs settable or <c>init</c> members, so a record
/// kept for that reason also holds auto-properties and takes an SSS001
/// suppression alongside this one.
/// </para>
/// <para>
/// Public types are exempt, on the same reasoning that exempts them from
/// SSS001: there the synthesized members are deliberate API surface, and a
/// consumer can hold the type to the equality, printing and <c>with</c>
/// contract the keyword advertises.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NonPublicRecordAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS009",
        title: "Non-public record should be a plain class or struct",
        messageFormat: "Non-public type '{0}' is declared as a record; declare it as a plain {1} — the synthesized equality, copy and printing members ship as metadata and read as uncovered unless 'with', deconstruction or the printed ToString is genuinely used, and value equality alone is served better by implementing IEquatable<{0}> directly",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A record synthesizes Equals, GetHashCode, the equality operators, ToString, PrintMembers, a copy constructor and (positionally) Deconstruct, and the compiler emits all of them whether or not the code calls any. On a non-public type none of that is API surface, so each uncalled member is assembly metadata that ships and an uncovered member in every coverage report. Declare a plain class or struct instead. Value equality alone does not justify the shape: a type used as a dictionary key or in a hash set should implement IEquatable<T> directly, which emits the two members the lookup actually calls and keeps a struct off ValueType.Equals's reflection-and-boxing fallback. A record kept for a 'with' expression, deconstruction or a printed ToString the code genuinely reads suppresses with #pragma warning disable SSS009 and a one-line rationale.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeRecordDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
    }

    private static void AnalyzeRecordDeclaration(SyntaxNodeAnalysisContext context)
    {
        var recordDecl = (RecordDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(recordDecl, context.CancellationToken) is not INamedTypeSymbol type)
            return;

        if (IsEffectivelyPublic(type))
            return;

        // Each part of a partial record carries its own `record` keyword and
        // each has to change, so reporting per declaration is the fix list.
        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            recordDecl.Identifier.GetLocation(),
            type.Name,
            PlainShapeFor(recordDecl)));
    }

    /// <summary>
    /// The plain declaration the record should become. <c>record struct</c>
    /// maps to <c>struct</c> and keeps a <c>readonly</c> modifier that is
    /// already there; everything else — <c>record</c>, <c>record class</c> —
    /// is a class.
    /// </summary>
    private static string PlainShapeFor(RecordDeclarationSyntax record) =>
        !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "class"
            : record.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) ? "readonly struct"
            : "struct";

    /// <summary>
    /// True iff <paramref name="type"/> and every containing type up to the namespace are <c>public</c>.
    /// A public type nested in an internal type is not effectively public.
    /// </summary>
    private static bool IsEffectivelyPublic(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public
        && (type.ContainingType is null || IsEffectivelyPublic(type.ContainingType));
}
