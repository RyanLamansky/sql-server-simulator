using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Executes a subquery plan once per statement instead of once per outer row,
/// for the plans that never read the enclosing row. Shared by the four
/// subquery-consuming expressions — the scalar <c>(SELECT …)</c>,
/// <c>EXISTS</c>, <c>[NOT] IN (SELECT …)</c> and the quantified
/// <c>op {ANY|SOME|ALL} (SELECT …)</c> — each of which stores its own result
/// shape under its own expression instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detection is a runtime probe on the first execution</b>, not a parse-time
/// analysis. The site wraps the caller's outer-row resolver in a delegate that
/// latches when it is consulted, runs the plan under that wrapper, and asks
/// afterwards whether the latch tripped. An untripped latch means the work that
/// produced this result never looked at the outer row, so — data being fixed
/// for the statement's duration — every later row would recompute the identical
/// value. A tripped latch stores <see cref="PerRowSite"/> instead, and the site
/// executes per row exactly as it did before; the probing execution's own
/// result is still correct for the row that triggered it, so nothing is wasted.
/// </para>
/// <para>
/// The reasoning holds for a plan the caller only partially consumes
/// (<c>EXISTS</c> stops at the first row): what matters is that the consumed
/// prefix was produced without reading the outer row, which makes that prefix —
/// and therefore the answer drawn from it — the same for every outer row.
/// </para>
/// <para>
/// <b>A per-call-varying built-in declines the reuse.</b>
/// <see cref="SimulatedDbConnection.VolatileEvaluations"/> is sampled around
/// the probing execution; a plan that drew a <c>NEWID()</c> or advanced a
/// sequence stores <see cref="PerRowSite"/> however uncorrelated it is.
/// Probe-confirmed against SQL Server 2025: reading
/// <c>(SELECT TOP 1 NEWID() FROM Sales.Customers)</c> once per row over a
/// 100-row outer yields 100 distinct values on real, so replaying one draw
/// would be a fidelity regression rather than an optimization. <c>RAND()</c>
/// and the current-time family need no gate — both engines freeze them for the
/// statement already, and real reports one distinct <c>RAND()</c> across the
/// same 100 rows.
/// </para>
/// <para>
/// Results live on <see cref="StatementContext.SubqueryResults"/> rather than
/// on the expression, per the shared-plan contract in
/// <c>docs/claude/plan-cache.md</c>: one <c>Selection</c> is executed by many
/// commands, possibly concurrently, so anything that varies per execution
/// belongs to execution-scoped state.
/// </para>
/// </remarks>
internal static class UncorrelatedSubqueryCache
{
    /// <summary>
    /// Stored in place of a result once a site's first execution proved it has
    /// to run per row — because it read the outer row, or because a per-call-
    /// varying built-in ran inside it. Reading it back skips the probe
    /// allocation for the rest of the statement.
    /// </summary>
    private static readonly object PerRowSite = new();

    /// <summary>
    /// Looks <paramref name="site"/> up in the executing statement's memo and
    /// hands back what the caller needs in one step: a
    /// <see cref="SubqueryMemo.Result"/> to return as-is, or — counting the
    /// inner-plan execution the caller is about to run — a
    /// <see cref="SubqueryMemo.Probe"/> to run it under, or neither when the
    /// site is already known to need per-row execution.
    /// </summary>
    internal static SubqueryMemo Open(RuntimeContext runtime, object site)
    {
        var entry = runtime.Batch.CurrentStatement.SubqueryResults is { } memo && memo.TryGetValue(site, out var found)
            ? found
            : null;
        if (entry is not null && !ReferenceEquals(entry, PerRowSite))
            return new SubqueryMemo(entry, null);

        runtime.Batch.Connection.SubqueryPlanExecutions++;
        return new SubqueryMemo(null, entry is null ? new OuterRowProbe(runtime) : null);
    }

    /// <summary>
    /// Files the outcome of a probed execution: <paramref name="result"/> for
    /// the rest of the statement when the execution proved replayable, and the
    /// <see cref="PerRowSite"/> marker when it didn't.
    /// </summary>
    internal static void Store(RuntimeContext runtime, object site, OuterRowProbe probe, object result)
    {
        var frame = runtime.Batch.CurrentStatement;
        (frame.SubqueryResults ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance))[site] =
            probe.CanReplay(runtime) ? result : PerRowSite;
    }
}

/// <summary>
/// What <see cref="UncorrelatedSubqueryCache.Open"/> found for one site: at
/// most one of the two members is non-null, and both being null means the site
/// runs its plan per row without being watched.
/// </summary>
internal readonly struct SubqueryMemo(object? result, OuterRowProbe? probe)
{
    /// <summary>The result this site already produced for the statement, or null when the plan has to run.</summary>
    internal readonly object? Result = result;

    /// <summary>The probe to watch this execution with, or null when the site is already known to need per-row execution.</summary>
    internal readonly OuterRowProbe? Probe = probe;

    /// <summary>The resolver to execute the inner plan under: the probe's when one is open, the caller's own otherwise.</summary>
    internal Func<MultiPartName, SqlValue> ResolverFor(RuntimeContext runtime) =>
        this.Probe is { } probe ? probe.Resolver : runtime.ResolveColumn;

    /// <summary>
    /// Files <paramref name="result"/> against <paramref name="site"/> when
    /// this execution was watched; a no-op for the already-known-per-row case.
    /// </summary>
    internal void Remember(RuntimeContext runtime, object site, object result)
    {
        if (this.Probe is { } probe)
            UncorrelatedSubqueryCache.Store(runtime, site, probe, result);
    }
}

/// <summary>
/// One subquery execution's correlation probe: the resolver to run the plan
/// under, plus the evidence <see cref="CanReplay"/> weighs afterwards.
/// </summary>
internal sealed class OuterRowProbe
{
    /// <summary>
    /// The resolver to execute the plan with: records the consult, then hands
    /// the name to the caller's own resolver unchanged.
    /// </summary>
    internal readonly Func<MultiPartName, SqlValue> Resolver;

    private readonly long volatileEvaluationsAtStart;

    private bool consulted;

    internal OuterRowProbe(RuntimeContext runtime)
    {
        var outer = runtime.ResolveColumn;
        this.Resolver = name =>
        {
            this.consulted = true;
            return outer(name);
        };
        this.volatileEvaluationsAtStart = runtime.Batch.Connection.VolatileEvaluations;
    }

    /// <summary>
    /// Whether the execution this probe watched can stand in for every later
    /// row: it neither read the outer row nor evaluated a built-in that draws
    /// a fresh value per call.
    /// </summary>
    internal bool CanReplay(RuntimeContext runtime) =>
        !this.consulted && runtime.Batch.Connection.VolatileEvaluations == this.volatileEvaluationsAtStart;
}
