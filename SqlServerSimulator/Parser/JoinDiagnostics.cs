namespace SqlServerSimulator.Parser;

/// <summary>
/// Opt-in, test-only capture of the join strategy each FROM-level resolves to
/// during query execution. Off by default (<see cref="Sink"/> is null) and
/// imposes only a per-join null check at chain-construction time — never a
/// per-row cost. The single writer is <see cref="Selection.ApplyJoin"/>, at the
/// exact point it chooses the hash equi-join fast path over the nested-loop
/// operators, so the trace can't drift from the real dispatch decision. Used by
/// the internal regression test that guards against a silent fall-back to the
/// O(L×R) nested loop (a perf regression the correctness suite wouldn't catch).
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
