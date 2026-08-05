namespace SqlServerSimulator.Parser;

/// <summary>
/// Opt-in, test-only capture of whether a grouped aggregate ran serially or
/// forked into the parallel accumulation, and — when it forked — whether the
/// merge stood or the statement fell back. Off by default (<see cref="Sink"/>
/// is null) and costs one null check at the point the decision is taken, never
/// a per-row cost. The writers sit at the decision sites themselves
/// (<c>ParallelGroupedAccumulation.TryEngage</c> and the fallback in
/// <c>BuildAggregateProjectionRows</c>), so the trace can't drift from the real
/// dispatch, which is what lets the regression tests assert on a gate
/// <em>declining</em> rather than only on the answer being right.
/// </summary>
internal static class AggregateDiagnostics
{
    /// <summary>
    /// Per-thread strategy log. A test assigns a fresh list, drives a query to
    /// completion on the same thread (the coordinator is the calling thread —
    /// workers never write here), then inspects the entries. Null disables
    /// capture.
    /// </summary>
    [ThreadStatic]
    internal static List<string>? Sink;

    /// <summary>
    /// Per-thread switch admitting the parallel grouped accumulation, <b>off by
    /// default</b> — so the shipped behaviour is the serial path, unchanged and
    /// costing nothing.
    /// <para>
    /// The mechanism is built, proven and measured: it answers value-for-value
    /// with the serial path, and it is worth 1.2-2.4× on a single-session
    /// analytical battery. What keeps it off is the other measurement — on the
    /// concurrent AdventureWorks workload driver, a process that forks even a
    /// handful of times loses ~25-30% of its subsequent throughput, and does so
    /// even over a phase in which the fan-out never engages again. That
    /// persistent, process-wide cost is not yet explained (thread lifetime and
    /// block-allocation size were both ruled out by measurement), and a default
    /// that can cost a concurrent workload a quarter of its throughput is not a
    /// default worth having. Flipping it on is a decision for whoever can weigh
    /// the analytical win against that, not one this switch should presume.
    /// </para>
    /// <para>
    /// Turning it on is also what lets a test compare the two answers over the
    /// same rows, which is the merge's real contract.
    /// </para>
    /// </summary>
    [ThreadStatic]
    internal static bool EnableParallelAccumulation;
}
