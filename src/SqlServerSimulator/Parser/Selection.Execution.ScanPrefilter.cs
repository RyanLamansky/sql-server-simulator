using SqlServerSimulator.Parser.Expressions;
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
    /// The scan prefilter's narrowing for a source a seek already narrowed:
    /// filters <paramref name="seeked"/>'s rows by the source-local sargable
    /// WHERE conjuncts the seek didn't answer, or returns <see langword="null"/>
    /// when there are none. <c>WHERE o.CustomerID = @c AND o.OrderDate BETWEEN
    /// @a AND @b</c> seeks on the customer, and the date then cuts the
    /// customer's rows before the join reads them rather than per joined
    /// tuple — the shape a low-cardinality equality and a selective range on
    /// another column make, where the seek alone leaves the join most of a
    /// bucket.
    /// <para>
    /// The terms of <see cref="TryPrefilterJoinSource"/> hold — the conjuncts
    /// stay in the residual WHERE, each is NULL-rejecting on the source's
    /// column, and one that raises keeps its row for the WHERE to decide — and
    /// two more keep the WHERE's own errors where they were: only the leading
    /// run of the WHERE's conjuncts is pushed, every one of them source-local
    /// and sargable, and a row is dropped only on a conjunct answering FALSE,
    /// never UNKNOWN. A dropped row is then one the WHERE, asking the same
    /// conjuncts first, would have rejected before it asked anything that
    /// could raise. An equality on a column the seek keyed on
    /// (<paramref name="keyedOrdinals"/>) isn't pushed, since every candidate
    /// already answers it.
    /// </para>
    /// <para>
    /// The filter judges itself over its first <see cref="SeekOuterRowCap"/>
    /// rows rather than <see cref="PrefilterProbeRows"/>: a conjunct keeping
    /// most of a seek's rows costs an evaluation each and saves the join
    /// nothing, and a seek's rows are few enough that a long probe would be
    /// most of them (measured 2026-10-11 over a 1,000-row seek joined per row
    /// to a 200k-row table: a conjunct keeping every row read 7% slower than
    /// the unfiltered seek over the full probe and 1% over the shorter one,
    /// at the price of one keeping four rows in five, which read 7% faster
    /// over the full probe and level over the shorter).
    /// </para>
    /// </summary>
    private static FromSource? TryPrefilterSeekedSource(
        FromSource source,
        FromSource seeked,
        int[]? keyedOrdinals,
        List<BooleanExpression> conjuncts,
        FromSource[] planSources,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        List<BooleanExpression>? pushed = null;
        foreach (var conjunct in conjuncts)
        {
            if (!IsSourceLocalSargable(source, conjunct, planSources))
                break;
            if (keyedOrdinals is null || !IsKeyedEquality(source, conjunct, keyedOrdinals))
                (pushed ??= []).Add(conjunct);
        }

        if (pushed is null)
            return null;

        IndexSeekDiagnostics.Sink?.Add($"SeekPrefilter({source.BackingTable!.Name},{pushed.Count})");
        return seeked.WithFilteredRows(PrefilteredRows(seeked, [.. pushed], batch, outerResolver, SeekOuterRowCap, dropOnUnknown: false));
    }

    // Whether a sargable conjunct is an equality on one of the columns a seek
    // keyed on, which every row the seek yields answers already.
    private static bool IsKeyedEquality(FromSource source, BooleanExpression conjunct, int[] keyedOrdinals) =>
        conjunct.TryGetEqualityOperands(out var left, out var right)
        && ((TryIdentifyIndexableColumn(source, left, out var ordinal) && Array.IndexOf(keyedOrdinals, ordinal) >= 0)
            || (TryIdentifyIndexableColumn(source, right, out ordinal) && Array.IndexOf(keyedOrdinals, ordinal) >= 0));

    /// <summary>
    /// Reads <paramref name="source"/>'s first <see cref="SeekOuterRowCap"/> + 1
    /// rows ahead and reports how many it has — <paramref name="count"/>, or -1
    /// when the rows ran past the sample — returning the source the join reads
    /// in its place. Its first read replays the rows read ahead and then reads
    /// on from where the sample stopped, so the sample is the start of the read
    /// the join makes anyway rather than an extra one; any later read (a nested
    /// loop's next pass) reads the rows afresh, as it did before.
    /// </summary>
    private static FromSource SampleFilteredSource(FromSource source, BatchContext batch, out int count)
    {
        var rows = source.RowsFor(batch);
        var reader = rows.GetEnumerator();
        var sample = new List<byte[]>();
        var more = true;
        while (sample.Count <= SeekOuterRowCap && (more = reader.MoveNext()))
            sample.Add(reader.Current);
        if (!more)
        {
            reader.Dispose();
            reader = null;
        }

        count = more ? -1 : sample.Count;
        IndexSeekDiagnostics.Sink?.Add($"SampledFilter({source.BackingTable!.Name},{count})");
        return source.WithFilteredRows(new SampledRows(sample, reader, rows));
    }

    /// <summary>
    /// A filtered source's rows after <see cref="SampleFilteredSource"/> read
    /// some ahead: the first enumeration yields those and then the rest of the
    /// read they began, every later one reads <c>rows</c> afresh.
    /// </summary>
    private sealed class SampledRows(List<byte[]> sample, IEnumerator<byte[]>? rest, IEnumerable<byte[]> rows) : IEnumerable<byte[]>
    {
        private List<byte[]>? sample = sample;
        private IEnumerator<byte[]>? rest = rest;

        public IEnumerator<byte[]> GetEnumerator()
        {
            if (this.sample is not { } first)
                return rows.GetEnumerator();
            var reader = this.rest;
            this.sample = null;
            this.rest = null;
            return Resume(first, reader);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        private static IEnumerator<byte[]> Resume(List<byte[]> sample, IEnumerator<byte[]>? rest)
        {
            try
            {
                foreach (var row in sample)
                    yield return row;
                while (rest is not null && rest.MoveNext())
                    yield return rest.Current;
            }
            finally
            {
                rest?.Dispose();
            }
        }
    }

    /// <summary>
    /// Narrows a deferred source the materialization pass already ran — a
    /// derived table, CTE or view whose rows are held in a list for this
    /// execution (<see cref="FromSource.MaterializedRows"/>) — by the same
    /// source-local sargable WHERE conjuncts the scan prefilter pushes, and
    /// returns the rows that pass, or <see langword="null"/> when no conjunct
    /// qualifies.
    /// <para>
    /// A body nothing can seek into is otherwise read whole by the join however
    /// selective the WHERE is on it, and the conjunct that would have narrowed
    /// it — typically one equating its column with an enclosing query's, as a
    /// correlated <c>EXISTS</c> or <c>APPLY</c> body joining a body that reads
    /// no outer row does — is only asked once the join has paired every row of
    /// it, after whichever conjuncts precede it in the WHERE. Evaluated here,
    /// eagerly since the rows are already in hand, the narrowing hands back a
    /// count, so a source it cuts to a few rows can drive the chain
    /// (<see cref="ReorderJoinChain"/>) and seek its base-table partner per row
    /// instead of the partner's whole table pairing with it on every execution.
    /// </para>
    /// <para>
    /// It is a pure narrowing on the scan prefilter's terms: every pushed
    /// conjunct stays in the residual WHERE and is NULL-rejecting on this
    /// source's column, and a conjunct that raises keeps its row for the
    /// residual to decide. A source whose columns the reader re-draws per
    /// output row (<see cref="FromSource.VolatileRefresh"/>) declines, since the
    /// materialized value a conjunct here would read isn't the one the residual
    /// reads.
    /// </para>
    /// </summary>
    private static List<byte[]>? TryNarrowMaterializedSource(
        FromSource source,
        List<BooleanExpression> conjuncts,
        FromSource[] planSources,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (source.MaterializedRows is null || source.VolatileRefresh is not null || source.Qualifier is null)
            return null;

        List<BooleanExpression>? pushed = null;
        foreach (var conjunct in conjuncts)
        {
            if (IsSourceLocalSargable(source, conjunct, planSources))
                (pushed ??= []).Add(conjunct);
        }

        if (pushed is null)
            return null;

        var kept = TryNarrowByEqualityIndex(source, pushed, batch, outerResolver) ?? [.. PrefilteredRows(source, [.. pushed], batch, outerResolver)];
        IndexSeekDiagnostics.Sink?.Add($"MaterializedFilter({source.Qualifier},{pushed.Count},{kept.Count})");
        return kept;
    }

    /// <summary>
    /// The hash <see cref="TryNarrowByEqualityIndex"/> keeps over one
    /// materialized source's rows for one equality conjunct, per statement
    /// (<see cref="StatementContext.MaterializedEqualityIndexes"/>): built on
    /// the second execution asking over the same rows, so a statement that
    /// narrows them once pays the one filter pass and no build, and left
    /// unusable (<see cref="Buckets"/> null once built) when the column's
    /// values aren't all of one type.
    /// </summary>
    internal sealed class MaterializedEqualityIndex(List<byte[]> rows)
    {
        public readonly List<byte[]> Rows = rows;

        public bool Built;

        /// <summary>The ordinals of the rows holding each non-NULL value, ascending.</summary>
        public Dictionary<SqlValueKey, List<int>>? Buckets;

        /// <summary>The one type every value of the column has, NULLs included.</summary>
        public SqlType? KeyType;
    }

    /// <summary>
    /// <see cref="PrefilteredRows"/>'s answer over a materialized source for
    /// <paramref name="pushed"/> whose first conjunct is a plain equality
    /// between this source's column and a value fixed for the execution, read
    /// from a hash over the rows rather than by asking every row — the shape a
    /// correlated body joining a body that reads no outer row repeats once per
    /// outer row over the same rows. Null, for the filter pass to answer, the
    /// first time a statement asks over these rows, and wherever the hash
    /// can't answer exactly as the pass would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactness rests on the equality being the first conjunct the pass asks:
    /// a row it answers FALSE or UNKNOWN for is dropped before any later
    /// conjunct runs, so only the rows the hash finds go on to the rest, which
    /// run as the pass runs them, a raise keeping the row. The equality itself
    /// neither converts nor raises: every value of the column and the probe
    /// value have one type, which the comparison answers by
    /// <see cref="SqlValue.Equals(SqlValue)"/>, the equality the hash keys by.
    /// Any other pairing, a <c>sql_variant</c> or a value side that raises,
    /// leaves the answer to the pass. The pass's give-up past
    /// <see cref="PrefilterProbeRows"/> rows is replayed over the found rows'
    /// ordinals, so the rows kept, their order and their count are the pass's.
    /// </para>
    /// </remarks>
    private static List<byte[]>? TryNarrowByEqualityIndex(
        FromSource source, List<BooleanExpression> pushed, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var equality = pushed[0];
        if (!equality.IsPlainEquality || !equality.TryGetEqualityOperands(out var left, out var right))
            return null;
        FromSource[] one = [source];
        int columnIndex;
        Expression valueSide;
        if (left is Reference leftColumn && FindSourceColumn(one, leftColumn.ReferencedName) is (0, var leftIndex))
            (columnIndex, valueSide) = (leftIndex, right);
        else if (right is Reference rightColumn && FindSourceColumn(one, rightColumn.ReferencedName) is (0, var rightIndex))
            (columnIndex, valueSide) = (rightIndex, left);
        else
            return null;

        var rows = source.MaterializedRows!;
        if (source.Columns[columnIndex].Computed is not null || rows.Count == 0)
            return null;
        var indexes = batch.CurrentStatement.MaterializedEqualityIndexes ??= new(ReferenceEqualityComparer.Instance);
        if (!indexes.TryGetValue(equality, out var index) || !ReferenceEquals(index.Rows, rows))
        {
            indexes[equality] = new MaterializedEqualityIndex(rows);
            return null;
        }
        if (!index.Built)
            BuildEqualityIndex(index, source, columnIndex, batch);
        if (index.Buckets is not { } buckets)
            return null;

        var tuple = new byte[]?[1];
        var memo = new SourceColumnMemo();
        SqlValue resolve(MultiPartName name) => ResolveAcrossTuple(one, tuple, name, batch, outerResolver, memo);
        var runtime = new RuntimeContext(resolve, batch);
        SqlValue probe;
        try
        {
            probe = valueSide.Run(runtime);
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
        if (probe.Type != index.KeyType)
            return null;

        IndexSeekDiagnostics.Sink?.Add($"MaterializedLookup({source.Qualifier})");
        var found = !probe.IsNull && buckets.TryGetValue(new SqlValueKey([probe]), out var bucket) ? bucket : [];
        var kept = new List<byte[]>();
        var keptCount = 0;
        var stoppedAfter = -1;
        var checkpointPassed = rows.Count < PrefilterProbeRows;
        foreach (var ordinal in found)
        {
            // The pass asks whether to give up after every row from the probe
            // window's last on; between found rows its kept count stands while
            // its row count grows, so only the window's last row and each found
            // row past it can trip it.
            if (!checkpointPassed && ordinal >= PrefilterProbeRows)
            {
                checkpointPassed = true;
                if (keptCount * PrefilterKeepRateDivisor > PrefilterProbeRows)
                {
                    stoppedAfter = PrefilterProbeRows - 1;
                    break;
                }
            }

            var row = rows[ordinal];
            tuple[0] = row;
            var keep = true;
            for (var c = 1; c < pushed.Count; c++)
            {
                bool? answer;
                try
                {
                    answer = pushed[c].Run(runtime);
                }
                catch (SimulatedSqlException)
                {
                    break;
                }
                if (answer != true)
                {
                    keep = false;
                    break;
                }
            }
            if (keep)
            {
                keptCount++;
                kept.Add(row);
            }
            if (ordinal >= PrefilterProbeRows - 1)
            {
                checkpointPassed = true;
                if (keptCount * PrefilterKeepRateDivisor > ordinal + 1)
                {
                    stoppedAfter = ordinal;
                    break;
                }
            }
        }
        if (stoppedAfter < 0 && !checkpointPassed && keptCount * PrefilterKeepRateDivisor > PrefilterProbeRows)
            stoppedAfter = PrefilterProbeRows - 1;
        if (stoppedAfter >= 0)
        {
            for (var ordinal = stoppedAfter + 1; ordinal < rows.Count; ordinal++)
                kept.Add(rows[ordinal]);
        }
        return kept;
    }

    /// <summary>
    /// Fills <paramref name="index"/> from its rows' values of column
    /// <paramref name="columnIndex"/>, leaving it unusable when those values
    /// aren't all of one type, or are of a type no hash keys.
    /// </summary>
    private static void BuildEqualityIndex(MaterializedEqualityIndex index, FromSource source, int columnIndex, BatchContext batch)
    {
        index.Built = true;
        var buckets = new Dictionary<SqlValueKey, List<int>>();
        SqlType? keyType = null;
        var rows = index.Rows;
        for (var ordinal = 0; ordinal < rows.Count; ordinal++)
        {
            var value = DecodeOrCompute(source, columnIndex, rows[ordinal], batch);
            if (keyType is null)
            {
                if (value.Type is SqlVariantSqlType || value.Type.IsLob)
                    return;
                keyType = value.Type;
            }
            else if (value.Type != keyType)
            {
                return;
            }
            if (value.IsNull)
                continue;
            ref var bucket = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(buckets, new SqlValueKey([value]), out _);
            (bucket ??= []).Add(ordinal);
        }
        index.KeyType = keyType;
        index.Buckets = buckets;
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

    // Streams the source's rows, dropping the ones a pushed conjunct answers
    // FALSE for — or UNKNOWN, with dropOnUnknown — and keeping one a conjunct
    // raises on. Re-enumerable (the counters and the scaffolding are
    // per-enumeration locals), so a nested-loop inner re-reading this source
    // filters afresh each pass.
    private static IEnumerable<byte[]> PrefilteredRows(
        FromSource source,
        BooleanExpression[] pushed,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        int probeRows = PrefilterProbeRows,
        bool dropOnUnknown = true)
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

                if (answer == false || (answer is null && dropOnUnknown))
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

            if (probed >= probeRows && kept * PrefilterKeepRateDivisor > probed)
                filtering = false;
        }

        tuple[0] = null;
    }
}
