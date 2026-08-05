using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// What an equi-correlated <c>EXISTS</c> / <c>[NOT] IN (SELECT …)</c> subquery
/// needs to be answered from one execution of its inner plan instead of one per
/// outer row: the <b>decorrelated key plan</b> — the same query with its
/// correlation equalities removed and its projection replaced by the inner
/// columns those equalities compared — plus the enclosing-scope expressions
/// whose per-row values probe it.
/// </summary>
/// <remarks>
/// <para>
/// Built once at parse time (<c>Selection.TryBuildSemiJoinShape</c>) and
/// captured in the plan, so the classification is value-independent and a
/// plan-cached <c>Selection</c> carries it across executions. Everything that
/// varies per execution — the built index, the evaluation count — lives on
/// <see cref="StatementContext"/> per the shared-plan contract in
/// <c>docs/claude/plan-cache.md</c>.
/// </para>
/// <para>
/// The key plan projects the correlation key columns first, then — when the
/// inner projects exactly one non-LOB column — that column, which is what an
/// <c>IN</c> compares its left side against. <c>EXISTS</c> ignores it.
/// </para>
/// </remarks>
internal sealed class SemiJoinShape(Selection keyPlan, Expression[] outerKeys, SqlType[] keyTypes, HeapTable? seekableInner)
{
    /// <summary>The inner plan with its correlation equalities stripped, projecting the key columns (and the IN value column when there is one).</summary>
    internal readonly Selection KeyPlan = keyPlan;

    /// <summary>
    /// The single base table the per-row path <b>seeks</b> on a correlation
    /// column, or null when it can't (several sources, a query body, or no key
    /// / index leading on one of those columns). A seekable inner makes the
    /// per-row path cheap enough that the build has to wait for the outer to
    /// grow to a fraction of the inner's size; an unseekable one is a scan per
    /// outer row, which the build beats as soon as the threshold is passed.
    /// </summary>
    internal readonly HeapTable? SeekableInner = seekableInner;

    /// <summary>The enclosing-scope side of each correlation equality, evaluated once per outer row against the outer row's own resolver.</summary>
    internal readonly Expression[] OuterKeys = outerKeys;

    /// <summary>The type each correlation pair compares under — <c>SqlType.Promote</c>'s target, so a hash bucket means what evaluating the <c>=</c> meant.</summary>
    internal readonly SqlType[] KeyTypes = keyTypes;

    /// <summary>Whether <see cref="KeyPlan"/> projects the inner <c>IN</c> column after its key columns; an <c>IN</c> site declines the transform without it.</summary>
    internal bool ProjectsValue => this.KeyPlan.Schema.Length > this.KeyTypes.Length;
}

/// <summary>
/// The inner rows carrying one correlation key: the non-NULL values of the
/// inner <c>IN</c> projection, plus whether any of them was NULL. The NULL flag
/// is <b>per key</b> rather than global — a NULL projection under key
/// <c>k</c> turns a miss into UNKNOWN only for the outer rows whose key is
/// <c>k</c>, exactly as the per-row execution of that key's inner result would.
/// </summary>
internal sealed class SemiJoinGroup
{
    /// <summary>Every non-NULL inner value under this key, in the order the plan produced them.</summary>
    internal readonly List<SqlValue> Values = [];

    /// <summary>Whether any inner row under this key projected NULL.</summary>
    internal bool SawNull;
}

/// <summary>
/// The hash semi / anti-join structure one execution of a
/// <see cref="SemiJoinShape.KeyPlan"/> builds: the key tuples the inner
/// produced, each carrying its own <see cref="SemiJoinGroup"/> when the plan
/// projects an <c>IN</c> value column. A row whose key has any NULL component
/// is dropped while building — <c>NULL = NULL</c> is UNKNOWN, so such a row can
/// equi-match no outer key, including a NULL one.
/// </summary>
internal sealed class SemiJoinIndex
{
    private readonly Dictionary<SqlValueKey, SemiJoinGroup?> groups = [];

    /// <summary>
    /// The array every probe writes its key components into. A lookup never
    /// hands its key to the dictionary — only the build does, and the build
    /// takes a fresh array per row it keeps — so one scratch array serves every
    /// outer row instead of one allocation each. Execution-scoped like the
    /// index itself, so it never crosses two executions of a shared plan.
    /// </summary>
    private SqlValue[]? probeScratch;

    /// <summary>How many distinct correlation keys the inner produced (test / diagnostic observability).</summary>
    internal int KeyCount => this.groups.Count;

    /// <summary>Records a key with no value column — the <c>EXISTS</c> shape.</summary>
    internal void AddKey(SqlValueKey key) => _ = this.groups.TryAdd(key, null);

    /// <summary>Records one inner value under <paramref name="key"/>, folding a NULL into the key's own <see cref="SemiJoinGroup.SawNull"/>.</summary>
    internal void AddValue(SqlValueKey key, SqlValue value)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(this.groups, key, out _);
        slot ??= new SemiJoinGroup();
        if (value.IsNull)
            slot.SawNull = true;
        else
            slot.Values.Add(value);
    }

    /// <summary>Whether the inner produced any row under <paramref name="key"/> — the <c>EXISTS</c> answer.</summary>
    internal bool ContainsKey(SqlValueKey key) => this.groups.ContainsKey(key);

    /// <summary>The rows under <paramref name="key"/>, or false when the inner produced none (an empty inner result for that outer row).</summary>
    internal bool TryGetGroup(SqlValueKey key, [NotNullWhen(true)] out SemiJoinGroup? group)
    {
        group = this.groups.TryGetValue(key, out var found) ? found : null;
        return group is not null;
    }

    /// <summary>
    /// Evaluates this outer row's correlation key against the type the index was
    /// built under. False means a component wouldn't settle there, so this row
    /// takes the per-row path — the comparison the key stands for is what should
    /// raise, in its own row order. <paramref name="hasNull"/> reports a NULL
    /// component, which equi-matches nothing.
    /// </summary>
    internal bool TryProbeKey(RuntimeContext runtime, SemiJoinShape shape, out SqlValueKey key, out bool hasNull)
    {
        var outerKeys = shape.OuterKeys;
        var values = this.probeScratch ??= new SqlValue[outerKeys.Length];
        hasNull = false;
        key = SqlValueKey.Empty;
        for (var i = 0; i < outerKeys.Length; i++)
        {
            var value = outerKeys[i].Run(runtime);
            if (value.IsNull)
            {
                hasNull = true;
                return true;
            }

            var target = shape.KeyTypes[i];
            try
            {
                values[i] = value.Type == target ? value : value.CoerceTo(target);
            }
            catch (SimulatedSqlException)
            {
                return false;
            }
        }

        key = new SqlValueKey(values);
        return true;
    }
}

/// <summary>
/// One site's adaptive state within one statement: how many outer rows have
/// evaluated it, and the index once the switch has been taken.
/// </summary>
internal sealed class SemiJoinSite
{
    /// <summary>Outer-row evaluations so far; the switch is taken once this passes <see cref="SemiJoinProbe.PerRowEvaluationsBeforeBuild"/>.</summary>
    internal int Evaluations;

    /// <summary>The built structure, or null while the site is still running per row.</summary>
    internal SemiJoinIndex? Index;

    /// <summary>Set once this site is known to need per-row execution for the rest of the statement.</summary>
    internal bool Declined;

    /// <summary>
    /// Rows in the seekable inner's table, read once when the threshold is
    /// passed rather than per row (the heap answers from a maintained count).
    /// <c>-1</c> until then; unused when the inner isn't seekable.
    /// </summary>
    internal int InnerRowCount = -1;
}

/// <summary>
/// The adaptive switch between a correlated subquery's per-outer-row execution
/// and the hash semi / anti-join its decorrelated key plan supports.
/// </summary>
/// <remarks>
/// <para>
/// The first <see cref="PerRowEvaluationsBeforeBuild"/> evaluations of a site
/// within one statement run the per-row path unchanged, so a small outer never
/// pays a build it can't amortize — the same philosophy as the join planner's
/// <c>SeekOuterRowCap</c>. The next evaluation executes the key plan once and
/// every later row probes the result.
/// </para>
/// <para>
/// The one-shot execution runs under the same correlation latch the
/// outer-independence probe uses (<see cref="OuterRowProbe"/>): a key plan that
/// consulted the outer row — a residual conjunct or projection correlating
/// through a nested subquery, which the parse-time classification can't see
/// into — or that drew a per-call-varying built-in declines the site for the
/// rest of the statement, and the row that triggered the build falls back to
/// its own per-row execution. An error raised while building declines the same
/// way rather than surfacing, so the per-row path stays the one that decides
/// whether a row's inner result raises.
/// </para>
/// </remarks>
internal static class SemiJoinProbe
{
    /// <summary>
    /// Outer-row evaluations a site runs per row before the hash build is worth
    /// taking. A correlated inner over an indexed key is a seek per row whose
    /// per-<c>Heap</c> cache persists across executions, so a small outer beats
    /// a build that re-runs every statement; past this many rows the build's one
    /// pass wins. Mirrors the join planner's own outer-row cap.
    /// </summary>
    internal const int PerRowEvaluationsBeforeBuild = 128;

    /// <summary>
    /// Past the threshold, a <b>seekable</b> inner keeps its per-row execution
    /// until the outer has grown to this fraction of the inner table: the build
    /// costs one pass over the whole table while the per-row path pays only for
    /// the rows each outer key selects, so the crossover is a ratio rather than
    /// an absolute outer size — the same reasoning, and the same deliberately
    /// conservative constant, the join planner's seek-vs-hash choice rests on
    /// (<c>SeekInnerRowsPerOuterRow</c>). An inner with no key / index on the
    /// correlation column is a scan per outer row and takes no such delay.
    /// <para>
    /// Measured on WideWorldImporters (the 73k-row <c>Sales.Orders</c> as the
    /// inner): a 73k-row outer over a 663-row inner goes 115 ms → 31 ms, the
    /// same-size self-correlated shape 104 ms → 59 ms, and the 663-row outer
    /// whose inner is 111× larger stays on the per-row path, where it is 1.4×
    /// ahead of the build it would otherwise pay for.
    /// </para>
    /// </summary>
    private const int InnerRowsPerOuterRow = 4;

    /// <summary>
    /// The structure to probe for this outer row, or null when the site runs its
    /// plan per row (ineligible shape, still under the threshold, or declined).
    /// </summary>
    internal static SemiJoinIndex? Open(RuntimeContext runtime, Selection inner)
    {
        // Ineligible plans — the common case — never touch the memo.
        if (inner.SemiJoin is not { } shape)
            return null;

        var frame = runtime.Batch.CurrentStatement;
        var memo = frame.SubqueryResults ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        if (!memo.TryGetValue(inner, out var entry))
            memo[inner] = entry = new SemiJoinSite();

        var site = (SemiJoinSite)entry;
        return site.Index is { } built ? built
            : site.Declined || ++site.Evaluations <= PerRowEvaluationsBeforeBuild || StillWorthSeeking(shape, site) ? null
            : Build(runtime, shape, site);
    }

    // Whether the per-row seek is still ahead of the build for an outer this
    // size. The inner's row count is read once and held for the rest of the
    // statement — a heuristic doesn't need it fresh.
    private static bool StillWorthSeeking(SemiJoinShape shape, SemiJoinSite site)
    {
        if (shape.SeekableInner is not { } table)
            return false;
        if (site.InnerRowCount < 0)
            site.InnerRowCount = table.Heap.RowCount;
        return (long)site.Evaluations * InnerRowsPerOuterRow <= site.InnerRowCount;
    }

    private static SemiJoinIndex? Build(RuntimeContext runtime, SemiJoinShape shape, SemiJoinSite site)
    {
        var probe = new OuterRowProbe(runtime);
        SemiJoinIndex index;
        try
        {
            index = Materialize(runtime.Batch, shape, probe.Resolver);
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            // The key plan reads rows the correlated inner would have filtered
            // out, so an error it raises need not be one any outer row reaches.
            // Declining keeps the per-row path the one that decides.
            return Decline(site, "error");
        }

        if (!probe.CanReplay(runtime))
            return Decline(site, "correlated");

        SemiJoinDiagnostics.Sink?.Add($"SemiJoin:Build(keys={shape.OuterKeys.Length},groups={index.KeyCount})");
        site.Index = index;
        return index;
    }

    private static SemiJoinIndex? Decline(SemiJoinSite site, string reason)
    {
        SemiJoinDiagnostics.Sink?.Add($"SemiJoin:Decline({reason})");
        site.Declined = true;
        return null;
    }

    private static SemiJoinIndex Materialize(BatchContext batch, SemiJoinShape shape, Func<MultiPartName, SqlValue> resolver)
    {
        var resultSet = shape.KeyPlan.Execute(batch, resolver);
        var columns = RowDecoder.ColumnsFor(resultSet.Schema);
        var keyTypes = shape.KeyTypes;
        var projectsValue = shape.ProjectsValue;
        var index = new SemiJoinIndex();
        foreach (var rowBytes in resultSet.RowBytes)
        {
            var values = new SqlValue[keyTypes.Length];
            var nullComponent = false;
            for (var i = 0; i < keyTypes.Length; i++)
            {
                var value = RowDecoder.DecodeColumn(columns, rowBytes, i);
                if (value.IsNull)
                {
                    // NULL never equi-matches, so this row belongs to no key.
                    nullComponent = true;
                    break;
                }
                values[i] = value.Type == keyTypes[i] ? value : value.CoerceTo(keyTypes[i]);
            }
            if (nullComponent)
                continue;

            var key = new SqlValueKey(values);
            if (projectsValue)
                index.AddValue(key, RowDecoder.DecodeColumn(columns, rowBytes, keyTypes.Length));
            else
                index.AddKey(key);
        }
        return index;
    }
}

/// <summary>
/// Opt-in, test-only capture of the semi-join switch's decisions — the build
/// that took it and each reason a site declined. Off by default
/// (<see cref="Sink"/> is null); an ineligible plan writes nothing at all,
/// since it never reaches the switch. Mirrors <see cref="JoinDiagnostics"/>.
/// </summary>
internal static class SemiJoinDiagnostics
{
    /// <summary>Per-thread decision log. A test assigns a fresh list, drives a query to completion on the same thread, then inspects the entries. Null disables capture.</summary>
    [ThreadStatic]
    internal static List<string>? Sink;
}
