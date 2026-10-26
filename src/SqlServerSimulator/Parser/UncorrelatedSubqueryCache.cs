using System.Runtime.InteropServices;
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
/// <b>A plan that read the outer row replays for a row that reads the same.</b>
/// A deterministic plan handed the same values for the names it consults
/// takes the same path through its rows, consulting the same names, and
/// reaches the same result, so a scalar or quantified site whose first
/// execution read the outer row keys its results by the values of the names
/// it read (<see cref="KeyedSite"/>): a correlated aggregate over a handful of
/// groups runs once per group rather than once per row. A later execution that
/// reads a name outside the key starts the memo over under the wider key.
/// <c>EXISTS</c> and <c>IN</c> keep their per-row runs for the switch to a
/// hash semi-join that counts them (<c>SemiJoinProbe</c>).
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
    /// site is already known to need per-row execution. With
    /// <paramref name="replaysByKey"/> a run that read the outer row is filed
    /// by the values it read, for a later row reading the same.
    /// </summary>
    internal static SubqueryMemo Open(RuntimeContext runtime, object site, bool replaysByKey = false)
    {
        var entry = runtime.Batch.CurrentStatement.SubqueryResults is { } memo && memo.TryGetValue(site, out var found)
            ? found
            : null;
        if (entry is KeyedSite keyed)
        {
            var key = keyed.KeyOf(runtime.ResolveColumn);
            if (keyed.Results.TryGetValue(key, out var replayed))
                return new SubqueryMemo(replayed, null);
            runtime.Batch.Connection.SubqueryPlanExecutions++;
            return new SubqueryMemo(null, new OuterRowProbe(runtime, replaysByKey: true, key));
        }
        if (entry is not null && !ReferenceEquals(entry, PerRowSite))
            return new SubqueryMemo(entry, null);

        runtime.Batch.Connection.SubqueryPlanExecutions++;
        return new SubqueryMemo(null, entry is null ? new OuterRowProbe(runtime, replaysByKey) : null);
    }

    /// <summary>
    /// Files the outcome of a probed execution: <paramref name="result"/> for
    /// the rest of the statement when the execution read nothing of the outer
    /// row, under the values it read when the site replays by key and it read
    /// them deterministically, and the <see cref="PerRowSite"/> marker
    /// otherwise.
    /// </summary>
    internal static void Store(RuntimeContext runtime, object site, OuterRowProbe probe, object result)
    {
        var memo = runtime.Batch.CurrentStatement.SubqueryResults ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        memo[site] = probe.DrewVolatile(runtime) ? PerRowSite
            : probe.Key is null && probe.ConsultedNothing ? result
            : probe.ReplaysByKey ? KeyedSite.File(memo.GetValueOrDefault(site) as KeyedSite, probe, runtime, result) ?? PerRowSite
            : PerRowSite;
    }

    /// <summary>
    /// A site whose plan read the outer row: its results by the values of the
    /// <see cref="Names"/> its executions consulted, compared as
    /// <see cref="SqlValue.IsIdenticalTo"/> does — a plan may hand a consulted
    /// value back, so a row equal to another only under the collation or the
    /// trailing-space rule is a different key.
    /// </summary>
    private sealed class KeyedSite(MultiPartName[] names)
    {
        /// <summary>The most names a key reads before the site runs per row.</summary>
        public const int MaxNames = 8;

        /// <summary>The most results a site keeps; a key past them runs its plan without filing it.</summary>
        private const int Capacity = 1024;

        public readonly MultiPartName[] Names = names;

        public readonly Dictionary<SqlValue[], object> Results = new(IdenticalValues.Instance);

        /// <summary>The key of the row <paramref name="resolve"/> reads.</summary>
        public SqlValue[] KeyOf(Func<MultiPartName, SqlValue> resolve)
        {
            var key = new SqlValue[this.Names.Length];
            for (var i = 0; i < key.Length; i++)
                key[i] = resolve(this.Names[i]);
            return key;
        }

        /// <summary>
        /// Files <paramref name="result"/> in <paramref name="site"/> under the
        /// key <paramref name="probe"/>'s miss looked up, or — on the site's
        /// first execution, or once an execution consulted a name outside the
        /// site's — in a site keyed by both names, its results starting over
        /// since their keys are too short; null when that key would read more
        /// than <see cref="MaxNames"/> names or a value whose identity
        /// <see cref="SqlValue.IsIdenticalTo"/> doesn't settle (see
        /// <see cref="Keys"/>).
        /// </summary>
        public static KeyedSite? File(KeyedSite? site, OuterRowProbe probe, RuntimeContext runtime, object result)
        {
            if (site is not null && probe.Key is { } key && probe.ConsultedWithin(site.Names))
            {
                if (site.Results.Count < Capacity)
                    site.Results[key] = result;
                return site;
            }

            var names = new List<MultiPartName>(site?.Names ?? []);
            probe.AddConsultedTo(names);
            if (names.Count > MaxNames)
                return null;
            var grown = new KeyedSite([.. names]);
            var grownKey = grown.KeyOf(runtime.ResolveColumn);
            foreach (var value in grownKey)
            {
                if (!Keys(value.Type))
                    return null;
            }
            grown.Results[grownKey] = result;
            return grown;
        }

        /// <summary>
        /// Whether a name of <paramref name="type"/> can key a result: the
        /// numbers, the date and time types, <c>uniqueidentifier</c> and the
        /// character strings, whose values <see cref="SqlValue.IsIdenticalTo"/>
        /// tells apart exactly. <c>xml</c> and the spatial types share the
        /// string category without being compared as strings, and a
        /// <c>sql_variant</c> compares its inner value by equality.
        /// </summary>
        private static bool Keys(SqlType type) =>
            type.Category is not (SqlTypeCategory.Other or SqlTypeCategory.String) || SqlType.IsCollatedString(type);
    }

    /// <summary>Compares two keys value by value under <see cref="SqlValue.IsIdenticalTo"/>.</summary>
    private sealed class IdenticalValues : IEqualityComparer<SqlValue[]>
    {
        public static readonly IdenticalValues Instance = new();

        public bool Equals(SqlValue[]? x, SqlValue[]? y)
        {
            if (x!.Length != y!.Length)
                return false;
            for (var i = 0; i < x.Length; i++)
            {
                if (!x[i].IsIdenticalTo(y[i]))
                    return false;
            }
            return true;
        }

        // Identical values are equal values, so equality's hash serves identity.
        public int GetHashCode(SqlValue[] obj)
        {
            var hash = new HashCode();
            foreach (var value in obj)
                hash.Add(value.GetHashCode());
            return hash.ToHashCode();
        }
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
/// under, plus the evidence <see cref="UncorrelatedSubqueryCache.Store"/>
/// weighs afterwards.
/// </summary>
internal sealed class OuterRowProbe
{
    /// <summary>
    /// The resolver to execute the plan with: hands the name to the caller's
    /// own resolver unchanged, recording the consult once that answers. A
    /// name the enclosing row can't answer was consulted of nothing — an
    /// inner plan's values materialized ahead of the scan its correlation
    /// reads ask for that scan's column through this resolver, fail and
    /// decline — so it never joins a keyed site's key.
    /// </summary>
    internal readonly Func<MultiPartName, SqlValue> Resolver;

    /// <summary>Whether the site files a run that read the outer row by the values it read.</summary>
    internal readonly bool ReplaysByKey;

    /// <summary>The key a keyed site missed on for this execution's row; null on a site's first execution.</summary>
    internal readonly SqlValue[]? Key;

    private readonly long volatileEvaluationsAtStart;

    /// <summary>The names the execution consulted, each once; null while it has consulted none.</summary>
    private List<MultiPartName>? consulted;

    internal OuterRowProbe(RuntimeContext runtime, bool replaysByKey = false, SqlValue[]? key = null)
    {
        var outer = runtime.ResolveColumn;
        this.ReplaysByKey = replaysByKey;
        this.Key = key;
        this.Resolver = name =>
        {
            var value = outer(name);
            this.Note(name);
            return value;
        };
        this.volatileEvaluationsAtStart = runtime.Batch.Connection.VolatileEvaluations;
    }

    /// <summary>Whether the execution read nothing of the outer row.</summary>
    internal bool ConsultedNothing => this.consulted is null;

    /// <summary>
    /// Whether the execution this probe watched can stand in for every later
    /// row: it neither read the outer row nor evaluated a built-in that draws
    /// a fresh value per call.
    /// </summary>
    internal bool CanReplay(RuntimeContext runtime) => this.ConsultedNothing && !this.DrewVolatile(runtime);

    /// <summary>Whether the execution evaluated a built-in that draws a fresh value per call.</summary>
    internal bool DrewVolatile(RuntimeContext runtime) =>
        runtime.Batch.Connection.VolatileEvaluations != this.volatileEvaluationsAtStart;

    /// <summary>Whether every name the execution consulted is one of <paramref name="names"/>.</summary>
    internal bool ConsultedWithin(MultiPartName[] names)
    {
        foreach (var name in this.consulted ?? [])
        {
            if (IndexOf(names, name) < 0)
                return false;
        }
        return true;
    }

    /// <summary>Adds to <paramref name="names"/> each name the execution consulted that it lacks.</summary>
    internal void AddConsultedTo(List<MultiPartName> names)
    {
        foreach (var name in this.consulted ?? [])
        {
            if (IndexOf(CollectionsMarshal.AsSpan(names), name) < 0)
                names.Add(name);
        }
    }

    private void Note(MultiPartName name)
    {
        var consulted = this.consulted ??= [];
        if (IndexOf(CollectionsMarshal.AsSpan(consulted), name) < 0)
            consulted.Add(name);
    }

    private static int IndexOf(ReadOnlySpan<MultiPartName> names, MultiPartName name)
    {
        for (var i = 0; i < names.Length; i++)
        {
            if (DeferredColumn.SameName(names[i], name))
                return i;
        }
        return -1;
    }
}
