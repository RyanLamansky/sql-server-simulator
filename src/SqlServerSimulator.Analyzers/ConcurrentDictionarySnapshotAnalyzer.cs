using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SqlServerSimulator.Analyzers;

/// <summary>
/// Flags a read of <c>ConcurrentDictionary&lt;TKey, TValue&gt;.Values</c>,
/// <c>.Keys</c> or <c>.IsEmpty</c>. The first two take every one of the
/// dictionary's bucket locks and copy its whole contents into a fresh list on
/// each access, so a loop over <c>dict.Values</c> pays a lock sweep and a full
/// copy just to visit each entry once. <c>IsEmpty</c> takes the same lock
/// sweep whenever its answer is yes — the common answer at the sites that ask
/// it as a fast-path guard, such as the per-row uniqueness check's test for
/// another session's pending key — while the enumerator's first step answers
/// the question lock-free.
/// </summary>
/// <remarks>
/// <para>
/// Enumerating the dictionary itself — <c>foreach (var (_, v) in dict)</c>,
/// <c>dict.Select(p =&gt; p.Value)</c> — takes no lock and allocates only
/// the enumerator. What it gives up is the snapshot: the enumeration is a
/// moving view that can observe (or miss) an entry another thread adds or
/// removes mid-walk, and never throws for it. That is the right trade for a
/// lookup or a catalog walk, which is what nearly every read of these
/// properties is.
/// </para>
/// <para>
/// A site that genuinely needs a point-in-time copy — it adds or removes
/// entries while walking and must not see its own changes, or it needs the
/// copy's count to agree with its contents — says so: <c>dict.ToArray()</c>
/// takes the same lock sweep once and makes the snapshot explicit, and
/// anything else takes <c>#pragma warning disable SSS012</c> with a one-line
/// rationale.
/// </para>
/// <para>
/// <c>Count</c> also takes every lock but copies nothing, and answers a
/// question the enumeration can't answer more cheaply, so it stays off the
/// list.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConcurrentDictionarySnapshotAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "SSS012",
        title: "ConcurrentDictionary.Values / .Keys / .IsEmpty sweeps every lock",
        messageFormat: "'{0}' takes every bucket lock of the ConcurrentDictionary{1}; {2}, which is lock-free but a moving view rather than a snapshot",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ConcurrentDictionary's Values and Keys properties acquire all of the dictionary's locks and copy its contents into a new list on every access, and IsEmpty acquires them all whenever the dictionary is empty. Enumerating the dictionary directly (or dict.IsEmptyLockFree(), its first step) is lock-free and allocates only the enumerator, at the cost of seeing concurrent adds and removes as they happen instead of a point-in-time copy. A site that needs that copy — it mutates the dictionary while iterating and must not observe its own changes, or it needs a stable count — calls dict.ToArray() explicitly or suppresses with #pragma warning disable SSS012 and a rationale.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var concurrentDictionary = start.Compilation.GetTypeByMetadataName("System.Collections.Concurrent.ConcurrentDictionary`2");
            if (concurrentDictionary is null)
                return;

            start.RegisterOperationAction(
                operationContext => AnalyzePropertyReference(operationContext, concurrentDictionary),
                OperationKind.PropertyReference);
        });
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context, INamedTypeSymbol concurrentDictionary)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        var property = reference.Property;
        if (property.Name is not ("Values" or "Keys" or "IsEmpty"))
            return;
        if (!SymbolEqualityComparer.Default.Equals(property.ContainingType.OriginalDefinition, concurrentDictionary))
            return;

        // Point at the member name so the squiggle sits on the thing to
        // change, not on a receiver expression that may span lines.
        var location = reference.Syntax is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Name.GetLocation()
            : reference.Syntax.GetLocation();
        var (cost, fix) = property.Name == "IsEmpty"
            ? (" whenever the dictionary is empty", "ask dict.IsEmptyLockFree(), the enumerator's first step")
            : (" and copies its contents on each read", "enumerate the dictionary itself (foreach (var (_, v) in dict) / dict.Select(p => p.Value))");
        context.ReportDiagnostic(Diagnostic.Create(Rule, location, reference.Syntax.ToString(), cost, fix));
    }
}
