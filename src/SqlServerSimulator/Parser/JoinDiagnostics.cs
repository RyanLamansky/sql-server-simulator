namespace SqlServerSimulator.Parser;

/// <summary>
/// Opt-in, test-only capture of the join strategy each FROM-level resolves to
/// during query execution. Off by default (<see cref="Sink"/> is null) and
/// imposes only a per-join null check at chain-construction time — never a
/// per-row cost. Each writer sits at the exact point its strategy is chosen, so
/// the trace can't drift from the real dispatch decision:
/// <see cref="Selection.ApplyJoin"/> for a FROM level's hash equi-join versus
/// the nested-loop operators, and MERGE's own match phase for the target seek /
/// source hash / target × source scan it settles on. Used by the internal
/// regression tests that guard against a silent fall-back to a quadratic loop
/// (a perf regression the correctness suite wouldn't catch).
/// </summary>
internal static class JoinDiagnostics
{
    /// <summary>
    /// Per-thread strategy log. A test assigns a fresh list, drives a query to
    /// completion on the same thread (execution is synchronous and in-process),
    /// then inspects the entries. Null disables capture.
    /// </summary>
    [ThreadStatic]
    internal static List<string>? Sink;
}
