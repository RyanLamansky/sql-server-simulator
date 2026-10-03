using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// How many rows the prefilter watches before deciding whether it is earning
    /// its keep. The filter costs one predicate evaluation per source row and
    /// saves the join whatever it drops, so a predicate that drops almost nothing
    /// is pure overhead — past this many rows a pass rate above
    /// <c>1 / <see cref="PrefilterKeepRateDivisor"/></c> switches the filter off
    /// for the rest of the enumeration.
    /// </summary>
    private const int PrefilterProbeRows = 4096;

    /// <summary>
    /// The reciprocal of the share of probed rows the prefilter may pass before
    /// it gives up — 2, so a predicate keeping more than half of the first
    /// <see cref="PrefilterProbeRows"/> rows stops being evaluated. Switching off
    /// mid-stream is always sound: every matched conjunct stays in the residual
    /// WHERE, so the prefilter only ever removes rows the residual would have
    /// removed anyway, and removing none of them is a correct outcome.
    /// </summary>
    private const int PrefilterKeepRateDivisor = 2;

    /// <summary>
    /// Filters one joined source's row stream by the WHERE conjuncts that read
    /// <b>only that source</b>, so a join input restricted by a predicate no
    /// index can seek still reaches the join already narrowed. This is what a
    /// range on an unindexed column buys: <c>FROM o JOIN ol ON … WHERE o.date
    /// BETWEEN @a AND @b</c> has no key to seek <c>o</c> on, so before this pass
    /// the whole table drove the join and the date was settled per joined tuple;
    /// now <c>o</c> arrives already reduced and the join's own adaptive
    /// strategy (<see cref="EquiJoinSeekOrHash"/>) sees a small enough outer to
    /// seek the inner per row instead of hashing all of it.
    /// <para>
    /// Returns <see langword="null"/> when no conjunct qualifies. Only the
    /// <b>sargable</b> shapes are pushed — a comparison, <c>BETWEEN</c> or
    /// <c>[NOT] LIKE</c> whose column side is a bare reference into this source
    /// (<see cref="TryIdentifyIndexableColumn"/>) and whose value side — every
    /// bound, the pattern and its escape — is row-invariant for this execution
    /// (<see cref="IsStableValueSide"/>, which admits a literal, a variable and
    /// an enclosing-scope column but rejects a sibling's). That structural
    /// whitelist is what makes the push provably source-local: both operand
    /// shapes are enumerated node by node, so every name the pushed conjunct
    /// can read is either this source's own column or one the enclosing
    /// resolver answers.
    /// </para>
    /// <para>
    /// <c>LIKE</c> is pushed because it is what EF Core emits for
    /// <c>StartsWith</c> / <c>EndsWith</c> / <c>Contains</c>, and the seek
    /// planner positions on none of it: EF Core's own
    /// <c>NorthwindMiscellaneousQuerySqlServerTest</c> crosses <c>Orders</c>
    /// with itself under <c>WHERE o.CustomerID LIKE N'A%'</c>: unpushed, the
    /// join reads 689k tuples (~315 ms per execution); pushed, <c>o</c> arrives
    /// as its 30 rows (~20 ms).
    /// </para>
    /// <para>
    /// <b>The pushed conjunct stays in the enclosing WHERE.</b> The prefilter is
    /// therefore a pure narrowing — it may drop only rows the residual would have
    /// rejected — which is what makes it safe for every join kind. All the pushed
    /// shapes are NULL-rejecting on the source's own column, so a tuple an outer
    /// join NULL-extends because this side lost a row to the filter reads UNKNOWN
    /// for the very conjunct that dropped it and is excluded exactly as the
    /// matched-but-failing tuple was. It is safe for <c>TOP</c> too: the row cap
    /// applies after the residual WHERE, so the same output rows come out of the
    /// same underlying rows, leaving the scan's lock footprint unchanged.
    /// </para>
    /// </summary>
    private static FromSource? TryPrefilterJoinSource(
        FromSource source,
        List<BooleanExpression> conjuncts,
        FromSource[] planSources,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (source.BackingTable is null || source.LateralPlan is not null || source.IsPlaceholder)
            return null;

        List<BooleanExpression>? pushed = null;
        foreach (var conjunct in conjuncts)
        {
            if (IsSourceLocalSargable(source, conjunct, planSources))
                (pushed ??= []).Add(conjunct);
        }

        if (pushed is null)
            return null;

        IndexSeekDiagnostics.Sink?.Add($"ScanPrefilter({source.BackingTable.Name},{pushed.Count})");
        return source.WithFilteredRows(PrefilteredRows(source, [.. pushed], batch, outerResolver));
    }

    /// <summary>
    /// The prefilter above applied to a joined <c>UPDATE</c> / <c>DELETE</c>'s
    /// <b>target</b>, after the write pipeline has wrapped it with its address
    /// side-channel: the filter passes on the very <c>byte[]</c> instances the
    /// wrapper recorded, in the order it read them, so the write path — which
    /// row addresses it resolves, which qualifying rows it waits on and locks,
    /// what a <c>TOP</c> keeps — sees exactly the rows it saw before, minus
    /// rows a WHERE conjunct on the target alone rejects, which it skipped
    /// anyway. What changes is what those rejected rows cost: unfiltered, each
    /// one drives the join to its partners, an <c>APPLY</c> body's execution
    /// included, before the WHERE turns it away.
    /// <para>
    /// EF Core's <c>ExecuteDelete</c> / <c>ExecuteUpdate</c> over a navigation
    /// emits exactly that shape — the filter on the target, a join or
    /// <c>APPLY</c> to what it navigates — and its
    /// <c>NorthwindBulkUpdatesSqlServerTest</c> deletes from <c>[Order
    /// Details]</c> under <c>WHERE o.OrderID &lt; 10276</c> with a
    /// <c>CROSS APPLY</c> body per row, which runs for all 2,155 target rows
    /// unfiltered (~120 ms per statement) and for the 74 the filter keeps
    /// (~1 ms).
    /// </para>
    /// <para>
    /// Like the read path's, the push can only remove rows the WHERE would
    /// have rejected, and a conjunct that raises keeps its row for the residual
    /// to decide. A rejected row's partners go unevaluated, so an error only
    /// they would raise — an <c>APPLY</c> body dividing by the row's zero —
    /// doesn't surface, which is real's answer: SQL Server 2025
    /// deletes and updates the qualifying rows of that shape without Msg 8134
    /// (probed 2026-10-02). Skip mode declines, since nothing enumerates there.
    /// </para>
    /// </summary>
    internal static FromSource[] PrefilterMutationTarget(FromSource[] sources, int targetIndex, BooleanExpression? where, BatchContext batch)
    {
        if (where is null || sources.Length < 2 || batch.IsSkipping)
            return sources;
        var conjuncts = new List<BooleanExpression>();
        where.CollectConjuncts(conjuncts);
        if (TryPrefilterJoinSource(sources[targetIndex], conjuncts, sources, batch, outerResolver: null) is not { } filtered)
            return sources;
        var narrowed = (FromSource[])sources.Clone();
        narrowed[targetIndex] = filtered;
        return narrowed;
    }

    // Whether a top-level conjunct compares a bare column of THIS source against
    // a value that is fixed for one execution of the plan — the only shapes the
    // prefilter pushes. Every other conjunct (a sibling comparison, a subquery,
    // an IS NULL, anything reaching a function of the row) stays where it is.
    private static bool IsSourceLocalSargable(FromSource source, BooleanExpression conjunct, FromSource[] planSources)
        => conjunct.TryGetEqualityOperands(out var equalLeft, out var equalRight)
            ? IsColumnAgainstStableValue(source, equalLeft, equalRight, planSources)
            : conjunct.TryGetRangeOperands(out var rangeLeft, out _, out var rangeRight)
                ? IsColumnAgainstStableValue(source, rangeLeft, rangeRight, planSources)
                : conjunct.TryGetLikeOperands(out var subject, out var pattern, out var escape)
                    ? IsSourceLocalLike(source, subject, pattern, escape, planSources)
                    : IsSourceLocalBetween(source, conjunct, planSources);

    // The LIKE arm: the subject must be this source's bare column — a pattern
    // reading the row would be no filter on the source alone — and the pattern
    // and escape fixed for the execution. Like every pushed shape it is
    // NULL-rejecting on the column: a NULL subject answers UNKNOWN for LIKE and
    // NOT LIKE alike.
    private static bool IsSourceLocalLike(FromSource source, Expression subject, Expression pattern, Expression? escape, FromSource[] planSources)
        => TryIdentifyIndexableColumn(source, subject, out _)
        && IsStableValueSide(pattern, source, allowCorrelatedColumnValue: true, planSources)
        && (escape is null || IsStableValueSide(escape, source, allowCorrelatedColumnValue: true, planSources));

    // The BETWEEN arm of the shape test: both bounds have to be stable, since the
    // predicate reads them together.
    private static bool IsSourceLocalBetween(FromSource source, BooleanExpression conjunct, FromSource[] planSources)
        => conjunct.TryGetBetweenOperands(out var value, out var lower, out var upper)
        && TryIdentifyIndexableColumn(source, value, out _)
        && IsStableValueSide(lower, source, allowCorrelatedColumnValue: true, planSources)
        && IsStableValueSide(upper, source, allowCorrelatedColumnValue: true, planSources);

    private static bool IsColumnAgainstStableValue(FromSource source, Expression a, Expression b, FromSource[] planSources)
        => (TryIdentifyIndexableColumn(source, a, out _)
            && IsStableValueSide(b, source, allowCorrelatedColumnValue: true, planSources))
        || (TryIdentifyIndexableColumn(source, b, out _)
            && IsStableValueSide(a, source, allowCorrelatedColumnValue: true, planSources));

    // Streams the source's rows, dropping the ones every pushed conjunct answers
    // anything but TRUE for. Re-enumerable (the counters and the scaffolding are
    // per-enumeration locals), so a nested-loop inner re-reading this source
    // filters afresh each pass.
    private static IEnumerable<byte[]> PrefilteredRows(
        FromSource source,
        BooleanExpression[] pushed,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        // Hoisted per-row scaffolding: one single-slot tuple, one cached
        // self-referencing resolver lambda (never a local function passed as its
        // own conversion — that allocates a delegate per resolution
        // per row) and one RuntimeContext for the whole enumeration.
        FromSource[] one = [source];
        var tuple = new byte[]?[1];
        var memo = new SourceColumnMemo();
        SqlValue resolve(MultiPartName name) => ResolveAcrossTuple(one, tuple, name, batch, outerResolver, memo);
        var runtime = new RuntimeContext(resolve, batch);

        var probed = 0;
        var kept = 0;
        var filtering = true;
        foreach (var row in source.RowsFor(batch))
        {
            if (!filtering)
            {
                yield return row;
                continue;
            }

            tuple[0] = row;
            var keep = true;
            foreach (var conjunct in pushed)
            {
                bool? answer;
                try
                {
                    answer = conjunct.Run(runtime);
                }
                catch (SimulatedSqlException)
                {
                    // The residual WHERE decides — and raises, if the join
                    // produces a tuple from this row at all. Dropping it here on
                    // an error the enclosing statement might never have reached
                    // would be the one way this narrowing could change results.
                    break;
                }

                if (answer != true)
                {
                    keep = false;
                    break;
                }
            }

            probed++;
            if (keep)
            {
                kept++;
                yield return row;
            }

            if (probed >= PrefilterProbeRows && kept * PrefilterKeepRateDivisor > probed)
                filtering = false;
        }

        tuple[0] = null;
    }
}
