namespace SqlServerSimulator.Parser;

/// <summary>
/// Opt-in, test-only capture of whether a single-base-table scan resolved to an
/// equality index seek or a full scan. Off by default (<see cref="Sink"/> is
/// null) and imposes only a per-scan null check — never a per-row cost. The
/// single writer is <c>Selection.MaybeApplyIndexSeek</c>, at the exact
/// point it chooses (or declines) the seek, so the trace can't drift from the
/// real decision. Used by the internal regression test that guards against a
/// silent loss of the seek (a perf regression the correctness suite wouldn't
/// catch, since the seek is result-transparent) and against the seek firing
/// where it must not (snapshot / RCSI, tx-scoped row locks).
/// </summary>
internal static class IndexSeekDiagnostics
{
    /// <summary>
    /// Per-thread decision log: <c>Seek(table)</c> when a scan narrows to an
    /// index seek, <c>Scan(table)</c> when an eligible single-base-table scan
    /// keeps its full scan, <c>OrderedScan(table)</c> when an ORDER BY streams in
    /// key order instead of buffering + sorting (with <c>KeysetSeek(table)</c>
    /// when that ordered scan also positions past a keyset-pagination cursor),
    /// plus the per-Heap cache's own maintenance trace —
    /// <c>CacheBuild</c> when a seek (re)builds an entry from a full scan and
    /// <c>CacheReplay</c> when it instead applies the incremental journal delta
    /// (the "no warm-up" path). A test assigns a fresh list, drives a query to
    /// completion on the same thread (execution is synchronous and in-process),
    /// then inspects the entries. Null disables capture.
    /// </summary>
    [ThreadStatic]
    internal static List<string>? Sink;
}
