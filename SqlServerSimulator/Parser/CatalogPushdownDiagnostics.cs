namespace SqlServerSimulator.Parser;

/// <summary>
/// Opt-in, test-only capture of whether a leftmost catalog-view scan pushed a
/// WHERE equality predicate into its row generator (so the generator emits only
/// matching objects) or ran the full generator. Off by default
/// (<see cref="Sink"/> is null) and imposes only a per-scan null check — never a
/// per-row cost. The writers are the two <c>Selection.ForCatalogView</c>
/// overloads, at the exact point each decides whether to hand the generator a
/// <see cref="Schemas.CatalogFilter"/>, so the trace can't drift from the real
/// decision. Used by the internal regression test that guards against a silent
/// loss of the pushdown (a perf regression the correctness suite wouldn't catch,
/// since the pushdown is result-transparent — the full WHERE re-applies as a
/// residual filter), mirroring <see cref="IndexSeekDiagnostics"/>.
/// </summary>
internal static class CatalogPushdownDiagnostics
{
    /// <summary>
    /// Per-thread decision log: <c>Seek(view.column)</c> when a catalog scan
    /// narrows to a single key value, <c>SeekEmpty(view.column)</c> when the
    /// pushed comparand is NULL (so <c>= NULL</c> is UNKNOWN and the generator
    /// yields nothing), and <c>Scan(view)</c> when an eligible catalog view runs
    /// its full generator. A test assigns a fresh list, drives a query to
    /// completion on the same thread (execution is synchronous and in-process),
    /// then inspects the entries. Null disables capture.
    /// </summary>
    [ThreadStatic]
    internal static List<string>? Sink;
}
