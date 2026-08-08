using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Rebuilds a <c>ROW_NUMBER()</c>-only window body so that it keeps only the
    /// rows an enclosing statement's constant bound on the row number can use, or
    /// returns null when the bound names an output column that isn't this body's
    /// row number. Set only on the projection plan
    /// <see cref="BuildSqlProjection"/> builds for a body whose single window
    /// function is a bare <c>ROW_NUMBER()</c> projection — see
    /// <see cref="RowNumberWindowShape"/> for the shape rules — so every other
    /// <see cref="FromSource.LateralPlan"/> declines by carrying no delegate.
    /// <para>
    /// The bound travels as an output-column <b>ordinal</b> plus two evaluated
    /// row-number limits, the same currency
    /// <c>Selection.Execution.PredicatePushdown.cs</c>'s templates use: an
    /// ordinal and a constant are the only two things a body and the statement
    /// above it agree on without sharing a name scope.
    /// </para>
    /// <para>
    /// Never mutates: a bind returns a new <see cref="Selection"/> over the same
    /// parse-time tree, per the shared-plan contract in
    /// <c>docs/claude/plan-cache.md</c>.
    /// </para>
    /// </summary>
    internal Func<RowNumberBound, Selection?>? RowNumberBoundPushdown;

    /// <summary>
    /// Largest per-partition row count the bounded selection heap will take.
    /// Past this the per-row sift stops being cheaper than sorting the partition
    /// once — the same crossover <see cref="TopNHeapMaxRows"/> draws for a
    /// statement's own <c>TOP (n)</c>. A wider bound still <em>narrows</em>: the
    /// partition sorts as it always did and only the rows inside the bound are
    /// projected, which is the whole win for a deep-paging
    /// <c>rn BETWEEN 50001 AND 50050</c> read.
    /// </summary>
    private const int BoundedRowNumberHeapMaxRows = 4096;

    /// <summary>
    /// An enclosing statement's constant bound on a derived table's row-number
    /// column: the body's output <see cref="Ordinal"/> the bound was written
    /// against, and the inclusive row-number window
    /// <c>[<see cref="Lower"/>, <see cref="Upper"/>]</c> the enclosing WHERE
    /// leaves surviving. <see cref="Lower"/> is at least 1;
    /// <see cref="Upper"/> may be 0, which is an empty window (a bound like
    /// <c>rn &lt; 1</c> that keeps nothing).
    /// </summary>
    internal readonly struct RowNumberBound(int ordinal, int lower, int upper)
    {
        public readonly int Ordinal = ordinal;
        public readonly int Lower = lower;
        public readonly int Upper = upper;
    }

    /// <summary>
    /// Hands every FROM source reading through a <c>ROW_NUMBER()</c>-only body
    /// the row-number bound the enclosing WHERE puts on it, so the body keeps a
    /// bounded per-partition selection instead of sorting every partition in
    /// full and projecting every row for the filter above to throw away.
    /// Returns <paramref name="sources"/> unchanged (no copy) when nothing
    /// binds.
    /// <para>
    /// <b>The bounding conjunct stays in the enclosing WHERE.</b> That is the
    /// residual invariant every narrowing pass here rests on (see
    /// <see cref="PushWhereIntoDeferredSources"/> and
    /// <see cref="TryPrefilterJoinSource"/>): the body drops only rows whose row
    /// number falls outside the window the conjunct itself rejects, so the pass
    /// can only remove tuples the statement was going to discard. It holds for
    /// every join kind for the same reason the pushed conjuncts do — a bound is
    /// a comparison against the row-number column, so a tuple an outer join
    /// NULL-extends because this side lost its rows reads UNKNOWN for the very
    /// conjunct that justified the bound and is excluded.
    /// </para>
    /// </summary>
    private static FromSource[] BoundRowNumberBodies(
        FromSource[] sources, List<BooleanExpression> excluders, BatchContext batch)
    {
        if (excluders.Count == 0)
            return sources;

        List<BooleanExpression>? conjuncts = null;
        FromSource[]? rewritten = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].LateralPlan is not { RowNumberBoundPushdown: { } bind })
                continue;
            if (conjuncts is null)
            {
                conjuncts = [];
                foreach (var excluder in excluders)
                    excluder.CollectConjuncts(conjuncts);
            }

            foreach (var bound in CollectRowNumberBounds(conjuncts, sources, i, batch))
            {
                if (bind(bound) is not { } bounded)
                    continue;
                rewritten ??= (FromSource[])sources.Clone();
                rewritten[i] = sources[i].WithPushedPlan(bounded);
                WindowStrategyDiagnostics.Sink?.Add(
                    $"RowNumberBound({sources[i].Qualifier},{bound.Lower}..{bound.Upper})");
                break;
            }
        }

        return rewritten ?? sources;
    }

    /// <summary>
    /// Every inclusive row-number window the top-level WHERE conjuncts pin on a
    /// column of the source at <paramref name="index"/>. One is offered per
    /// bounded column rather than per conjunct, so the two halves of
    /// <c>rn &gt; 50000 AND rn &lt;= 50050</c> combine — and the body itself
    /// decides which column is its row number, which is why a bound is collected
    /// for each rather than guessed here.
    /// <para>
    /// A bound's value side has to be row-independent (a literal, a variable, a
    /// parameter, or arithmetic over those) and is evaluated <b>once, here</b>:
    /// the value is fixed for the whole enumeration by construction. An operand
    /// that raises while evaluating declines that conjunct rather than reporting
    /// early, matching the pushdown templates. Rounding is outward on both sides
    /// (<c>rn &lt;= 2.5</c> keeps two rows, <c>rn &gt;= 2.5</c> starts at three),
    /// so a fractional comparand can never tighten the window past what the
    /// residual comparison itself rejects.
    /// </para>
    /// </summary>
    private static List<RowNumberBound> CollectRowNumberBounds(
        List<BooleanExpression> conjuncts, FromSource[] sources, int index, BatchContext batch)
    {
        long[]? lower = null;
        long[]? upper = null;
        var columnCount = sources[index].ColumnNames.Length;

        void Record(int ordinal, RangeComparison op, double value)
        {
            lower ??= FilledBounds(columnCount, 1L);
            upper ??= FilledBounds(columnCount, long.MaxValue);
            switch (op)
            {
                // Row numbers are integers, so each comparison rounds outward to
                // the nearest one it admits — `rn > 2.5` starts at 3, `rn < 2.5`
                // ends at 2 — which can never keep out a row the comparison
                // itself would have kept.
                case RangeComparison.Greater:
                    lower[ordinal] = Math.Max(lower[ordinal], SaturatingAdd(SaturatingFloor(value), 1L));
                    break;
                case RangeComparison.GreaterOrEqual:
                    lower[ordinal] = Math.Max(lower[ordinal], SaturatingCeiling(value));
                    break;
                case RangeComparison.Less:
                    upper[ordinal] = Math.Min(upper[ordinal], SaturatingCeiling(value) - 1L);
                    break;
                case RangeComparison.LessOrEqual:
                    upper[ordinal] = Math.Min(upper[ordinal], SaturatingFloor(value));
                    break;
            }
        }

        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetRangeOperands(out var left, out var op, out var right))
            {
                if (TryIdentifyBoundedColumn(sources, index, left, out var leftOrd)
                    && EvaluateRowNumberBoundValue(right, batch) is { } rightValue)
                {
                    Record(leftOrd, op, rightValue);
                }
                else if (TryIdentifyBoundedColumn(sources, index, right, out var rightOrd)
                    && EvaluateRowNumberBoundValue(left, batch) is { } leftValue)
                {
                    Record(rightOrd, FlipComparison(op), leftValue);
                }

                continue;
            }

            if (conjunct.TryGetBetweenOperands(out var subject, out var low, out var high))
            {
                if (TryIdentifyBoundedColumn(sources, index, subject, out var betweenOrd)
                    && EvaluateRowNumberBoundValue(low, batch) is { } lowValue
                    && EvaluateRowNumberBoundValue(high, batch) is { } highValue)
                {
                    Record(betweenOrd, RangeComparison.GreaterOrEqual, lowValue);
                    Record(betweenOrd, RangeComparison.LessOrEqual, highValue);
                }

                continue;
            }

            // `rn = k` alone, and the OR-of-equalities family an `IN` list
            // decomposes into — a disjunction, so it bounds the window by the
            // smallest and largest of its comparands rather than by any one.
            List<(Expression Left, Expression Right)> pairs;
            if (conjunct.TryGetEqualityOperands(out var equalLeft, out var equalRight))
                pairs = [(equalLeft, equalRight)];
            else if (conjunct.TryGetEqualityFamily(out var family))
                pairs = family;
            else
                continue;

            var familyOrdinal = -1;
            var familyLow = double.PositiveInfinity;
            var familyHigh = double.NegativeInfinity;
            foreach (var (pairLeft, pairRight) in pairs)
            {
                var (ordinal, value) = BoundedEqualityPair(sources, index, pairLeft, pairRight, batch);
                if (value is not { } comparand || (familyOrdinal >= 0 && familyOrdinal != ordinal))
                {
                    familyOrdinal = -1;
                    break;
                }

                familyOrdinal = ordinal;
                familyLow = Math.Min(familyLow, comparand);
                familyHigh = Math.Max(familyHigh, comparand);
            }

            if (familyOrdinal >= 0)
            {
                Record(familyOrdinal, RangeComparison.GreaterOrEqual, familyLow);
                Record(familyOrdinal, RangeComparison.LessOrEqual, familyHigh);
            }
        }

        List<RowNumberBound> bounds = [];
        if (lower is null || upper is null)
            return bounds;
        for (var ordinal = 0; ordinal < columnCount; ordinal++)
        {
            // An unbounded upper limit narrows nothing — a lower limit alone
            // would still have to rank every row of the partition, and the
            // bounded path exists to avoid exactly that.
            if (upper[ordinal] == long.MaxValue)
                continue;
            bounds.Add(new RowNumberBound(
                ordinal,
                (int)Math.Clamp(lower[ordinal], 1L, int.MaxValue),
                (int)Math.Clamp(upper[ordinal], 0L, int.MaxValue)));
        }

        return bounds;
    }

    /// <summary>
    /// One equality leaf read as a bound: the ordinal of whichever side is a
    /// bare column of the source at <paramref name="index"/> and the numeric
    /// value of the other, or <c>(-1, null)</c> when neither side is.
    /// </summary>
    private static (int Ordinal, double? Value) BoundedEqualityPair(
        FromSource[] sources, int index, Expression left, Expression right, BatchContext batch) =>
        TryIdentifyBoundedColumn(sources, index, left, out var leftOrdinal)
            ? (leftOrdinal, EvaluateRowNumberBoundValue(right, batch))
            : TryIdentifyBoundedColumn(sources, index, right, out var rightOrdinal)
                ? (rightOrdinal, EvaluateRowNumberBoundValue(left, batch))
                : (-1, null);

    /// <summary>A <paramref name="length"/>-long bound array preset to <paramref name="fill"/>.</summary>
    private static long[] FilledBounds(int length, long fill)
    {
        var bounds = new long[length];
        Array.Fill(bounds, fill);
        return bounds;
    }

    /// <summary>The smallest integer at least <paramref name="value"/>, clamped to the row-number domain rather than overflowing.</summary>
    private static long SaturatingCeiling(double value) =>
        value <= 1.0 ? 1L : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);

    /// <summary>The largest integer at most <paramref name="value"/>, clamped to the row-number domain rather than overflowing.</summary>
    private static long SaturatingFloor(double value) =>
        value <= 0.0 ? 0L : value >= long.MaxValue ? long.MaxValue : (long)Math.Floor(value);

    /// <summary><paramref name="left"/> + <paramref name="right"/>, saturating at <see cref="long.MaxValue"/>.</summary>
    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    /// <summary>
    /// The output ordinal of <paramref name="candidate"/> when it is a bare
    /// column reference into the source at <paramref name="index"/> — the only
    /// shape a row-number bound can be written against, since anything else
    /// names a value the body's row number isn't.
    /// </summary>
    private static bool TryIdentifyBoundedColumn(
        FromSource[] sources, int index, Expression candidate, out int ordinal)
    {
        while (candidate is Parenthesized parenthesized)
            candidate = parenthesized.Wrapped;
        if (candidate is Reference reference)
        {
            var (source, column) = FindSourceColumn(sources, reference.ReferencedName);
            if (source == index)
            {
                ordinal = column;
                return true;
            }
        }

        ordinal = -1;
        return false;
    }

    /// <summary>
    /// A bound comparand's numeric value, or null when the operand isn't
    /// row-independent, is NULL, or can't be read as a number. Evaluated against
    /// a resolver that refuses column references, so nothing row-dependent can
    /// slip through the <see cref="Expression.IsRowIndependent"/> gate.
    /// </summary>
    private static double? EvaluateRowNumberBoundValue(Expression operand, BatchContext batch)
    {
        if (!operand.IsRowIndependent)
            return null;
        try
        {
            var value = operand.Run(new RuntimeContext(
                static name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), batch));
            if (value.IsNull)
                return null;
            var number = value.CoerceTo(SqlType.Float).AsDouble;
            return double.IsNaN(number) ? null : number;
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the bounded-selection shape of a body whose <b>only</b> window
    /// function is a bare <c>ROW_NUMBER()</c> projection: a plain
    /// SELECT-project-filter otherwise — no DISTINCT, no TOP / OFFSET / FETCH,
    /// no GROUP BY / HAVING / aggregate, no ORDER BY.
    /// <para>
    /// The one-window rule is what makes the bound legal rather than merely
    /// convenient. Every other window kind's per-row value is a property of the
    /// <em>whole</em> partition — a <c>SUM(x) OVER (PARTITION BY p)</c> beside
    /// the row number reads every row of the partition, and <c>RANK</c> /
    /// <c>DENSE_RANK</c> number ties alike, so no row count bounds how many rows
    /// carry a rank at or below <c>k</c>. Only <c>ROW_NUMBER</c> assigns
    /// <c>1 … n</c> one row at a time, which is what lets a partition answer a
    /// row-number window from its own top rows alone.
    /// </para>
    /// </summary>
    private sealed class RowNumberWindowShape(
        SqlType[] schema,
        string[] columnNames,
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        WindowExpression window,
        int rowNumberOrdinal)
    {
        public readonly SqlType[] Schema = schema;
        public readonly string[] ColumnNames = columnNames;
        public readonly FromSource[] Sources = sources;
        public readonly JoinSpec[] Joins = joins;
        public readonly List<Expression> Expressions = expressions;
        public readonly List<BooleanExpression> Excluders = excluders;
        public readonly WindowExpression Window = window;

        /// <summary>The output ordinal this body projects its <c>ROW_NUMBER()</c> at — the one ordinal a bound may name.</summary>
        public readonly int RowNumberOrdinal = rowNumberOrdinal;
    }

    /// <summary>
    /// The window body's <see cref="RowNumberBoundPushdown"/>: a plan reading the
    /// same rows through the bounded per-partition selection. Declines (null)
    /// when the bound names an output column other than the row number, which is
    /// how an unrelated constant comparison on the same derived table — a
    /// <c>WHERE x.OrderID &lt;= 100</c> beside the <c>rn</c> filter — falls
    /// through without bounding anything.
    /// </summary>
    private static Selection? BuildBoundedRowNumberPlan(RowNumberWindowShape shape, RowNumberBound bound) =>
        bound.Ordinal != shape.RowNumberOrdinal
            ? null
            : new Selection(
                shape.Schema,
                shape.ColumnNames,
                hasOrderBy: false,
                hasTopOrOffsetOrFetch: false,
                valueRowSource: (batch, outerResolver) =>
                {
                    var execSources = PushWhereIntoDeferredSources(shape.Sources, shape.Excluders, batch);
                    execSources = BoundRowNumberBodies(execSources, shape.Excluders, batch);
                    execSources = ReduceGroupedBodiesByJoinKeys(execSources, shape.Joins, shape.Excluders, batch, outerResolver);
                    execSources = MaterializeUncorrelatedDeferredSources(execSources, shape.Joins, batch, outerResolver);
                    return ProjectBoundedRowNumberRows(
                        execSources, shape.Joins, shape.Expressions, shape.Excluders, shape.Window,
                        bound.Lower, bound.Upper, batch, outerResolver);
                });

    /// <summary>
    /// The bounded counterpart of <see cref="ProjectWindowedRows"/> for a body
    /// whose single window is a <c>ROW_NUMBER()</c> an enclosing statement
    /// bounded to <c>[<paramref name="lower"/>, <paramref name="upper"/>]</c>.
    /// Each partition collects into its own
    /// <see cref="PartitionTopRows"/> — a bounded max-heap of
    /// <paramref name="upper"/> rows where that fits
    /// <see cref="BoundedRowNumberHeapMaxRows"/>, an ordinary buffer otherwise —
    /// so a row the bound can't reach is dropped as it is read, without being
    /// cloned, ranked or projected.
    /// <para>
    /// <b>Row identity and numbering are those of the unbounded path, exactly.</b>
    /// Both order a partition by the window's ORDER BY keys and then by the row's
    /// own arrival position, a <em>total</em> order — so the top <c>n</c> rows
    /// are the same rows carrying the same numbers whether they were selected by
    /// a heap or read off a full sort, ties at the bound included. See
    /// <c>docs/claude/query.md</c>'s window section for why that tiebreak is what
    /// makes the two paths interchangeable.
    /// </para>
    /// </summary>
    private static IEnumerable<SqlValue[]> ProjectBoundedRowNumberRows(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        WindowExpression window,
        int lower,
        int upper,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        sources = MaybeApplyIndexSeek(sources, joins, excluders, batch, outerResolver);
        (sources, joins) = NarrowJoinSources(sources, joins, excluders, batch, outerResolver);

        // The hoisted per-row scaffolding the other executors use: one mutable
        // tuple slot, one cached self-referencing resolver, one RuntimeContext
        // for the whole enumeration. The same slot serves the scan below and the
        // projection loop at the end.
        var memo = new SourceColumnMemo();
        var currentTuple = default(byte[]?[])!;
        Func<MultiPartName, SqlValue> resolveSource = null!;
        resolveSource = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, memo);
        var rowRuntime = new RuntimeContext(resolveSource, batch);

        var orderByList = new List<OrderBySpec>(window.OrderBy);
        var partitions = new Dictionary<SqlValue[], PartitionTopRows>(RowEqualityComparer.Instance);
        // Partition and order keys go into reused scratch: a partition is keyed
        // by a copy only the first time it is seen, and a row's keys are copied
        // only if the collector admits it — so a row the bound rejects costs no
        // allocation at all, which is most of the win at 73k rows / 663
        // partitions.
        var partitionKeys = new SqlValue[window.PartitionBy.Length];
        var orderKeys = new SqlValue[window.OrderBy.Length];
        var sequence = 0;
        foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
        {
            currentTuple = tuple;
            var include = true;
            foreach (var excluder in excluders)
            {
                if (excluder.Run(rowRuntime) != true)
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            for (var p = 0; p < partitionKeys.Length; p++)
                partitionKeys[p] = window.PartitionBy[p].Run(rowRuntime);
            for (var o = 0; o < orderKeys.Length; o++)
                orderKeys[o] = window.OrderBy[o].Expr!.Run(rowRuntime);

            if (!partitions.TryGetValue(partitionKeys, out var collector))
            {
                collector = new PartitionTopRows(
                    upper <= BoundedRowNumberHeapMaxRows ? Math.Max(upper, 1) : int.MaxValue, orderByList);
                partitions[[.. partitionKeys]] = collector;
            }

            collector.Offer(tuple, orderKeys, sequence++);
        }

        // Rank each partition's retained rows, keep the ones inside the bound,
        // then restore the arrival order the unbounded path yields in.
        List<(int Sequence, byte[]?[] Tuple, long RowNumber)> kept = [];
        foreach (var (_, collector) in partitions)
        {
            var ranked = collector.DrainOrdered();
            var limit = Math.Min(ranked.Count, upper);
            for (var i = lower - 1; i < limit; i++)
                kept.Add((ranked[i].Sequence, ranked[i].Tuple, i + 1));
        }

        kept.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        foreach (var (_, tuple, rowNumber) in kept)
        {
            window.BindResult(batch, SqlValue.FromInt64(rowNumber));
            currentTuple = tuple;
            var projected = new SqlValue[expressions.Count];
            for (var j = 0; j < expressions.Count; j++)
                projected[j] = expressions[j].Run(rowRuntime);
            yield return projected;
        }
    }

    /// <summary>
    /// One partition's candidate rows for a bounded <c>ROW_NUMBER()</c>, ordered
    /// by the window's ORDER BY keys and then by arrival position — the total
    /// order both window paths rank against.
    /// <para>
    /// With a finite capacity this is a bounded max-heap of the <c>n</c> smallest
    /// rows seen (root = the worst row admitted), so once it is full a candidate
    /// is rejected on a single compare and never copied — the same mechanism
    /// <c>TopNRowHeap</c> gives a statement's own <c>TOP (n)</c>, over a
    /// partition rather than the whole result. An <see cref="int.MaxValue"/>
    /// capacity buffers instead and sorts once at the drain, which is what a
    /// bound too wide for the heap (a deep-paging read) falls back to: it still
    /// spares the projection of every row outside the bound.
    /// </para>
    /// </summary>
    private sealed class PartitionTopRows(int capacity, List<OrderBySpec> orderBy)
    {
        private readonly List<(byte[]?[] Tuple, SqlValue[] Keys, int Sequence)> entries = [];

        /// <summary>
        /// Admits the row if it beats the worst held (always, when unbounded),
        /// else drops it. The tuple and its keys are copied only on admission —
        /// the caller hands over reused scratch, and
        /// <see cref="EnumerateJoinedRows"/> rewrites its tuple in place.
        /// </summary>
        public void Offer(byte[]?[] tuple, SqlValue[] keys, int sequence)
        {
            if (this.entries.Count < capacity)
            {
                this.entries.Add(((byte[]?[])tuple.Clone(), [.. keys], sequence));
                if (capacity != int.MaxValue)
                    this.SiftUp(this.entries.Count - 1);
                return;
            }

            if (this.Compare(keys, sequence, this.entries[0]) >= 0)
                return;
            this.entries[0] = ((byte[]?[])tuple.Clone(), [.. keys], sequence);
            this.SiftDown();
        }

        /// <summary>The rows held, best first. A heap fills the result back to front by repeated root removal; a buffer sorts in place.</summary>
        public List<(byte[]?[] Tuple, SqlValue[] Keys, int Sequence)> DrainOrdered()
        {
            if (capacity == int.MaxValue)
            {
                this.entries.Sort((a, b) => this.Compare(a.Keys, a.Sequence, b));
                return this.entries;
            }

            var ordered = new (byte[]?[] Tuple, SqlValue[] Keys, int Sequence)[this.entries.Count];
            for (var i = ordered.Length - 1; i >= 0; i--)
            {
                ordered[i] = this.entries[0];
                this.entries[0] = this.entries[^1];
                this.entries.RemoveAt(this.entries.Count - 1);
                this.SiftDown();
            }

            return [.. ordered];
        }

        private int Compare(SqlValue[] keys, int sequence, (byte[]?[] Tuple, SqlValue[] Keys, int Sequence) other)
        {
            var comparison = CompareOrderKeys(keys, other.Keys, orderBy);
            return comparison != 0 ? comparison : sequence.CompareTo(other.Sequence);
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (this.Compare(this.entries[index].Keys, this.entries[index].Sequence, this.entries[parent]) <= 0)
                    return;
                (this.entries[parent], this.entries[index]) = (this.entries[index], this.entries[parent]);
                index = parent;
            }
        }

        private void SiftDown()
        {
            var index = 0;
            while (true)
            {
                var left = (index * 2) + 1;
                if (left >= this.entries.Count)
                    return;
                var largest = this.Compare(this.entries[left].Keys, this.entries[left].Sequence, this.entries[index]) > 0 ? left : index;
                var right = left + 1;
                if (right < this.entries.Count
                    && this.Compare(this.entries[right].Keys, this.entries[right].Sequence, this.entries[largest]) > 0)
                {
                    largest = right;
                }

                if (largest == index)
                    return;
                (this.entries[largest], this.entries[index]) = (this.entries[index], this.entries[largest]);
                index = largest;
            }
        }
    }
}

/// <summary>
/// Opt-in, test-only capture of the window-execution strategy a query settles
/// on. Off by default (<see cref="Sink"/> is null) and imposes only a per-plan
/// null check — never a per-row cost. The single writer is
/// <c>Selection.BoundRowNumberBodies</c>, at the exact point it binds a
/// derived table's <c>ROW_NUMBER()</c> body to an enclosing statement's row
/// number bound, so the trace can't drift from the real decision. Used by the
/// internal regression tests that guard against a silent loss of the bounded
/// per-partition selection (a perf regression the correctness suite wouldn't
/// catch, since the bound is result-transparent) and against it engaging for a
/// shape that must decline.
/// </summary>
internal static class WindowStrategyDiagnostics
{
    /// <summary>
    /// Per-thread decision log: <c>RowNumberBound(source,lower..upper)</c> when a
    /// windowed body is bound to a row-number window. A test assigns a fresh
    /// list, drives a query to completion on the same thread (execution is
    /// synchronous and in-process), then inspects the entries. Null disables
    /// capture.
    /// </summary>
    [ThreadStatic]
    internal static List<string>? Sink;
}
