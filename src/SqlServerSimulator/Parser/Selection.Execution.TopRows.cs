using System.Numerics;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Largest row count <see cref="TopRows{T}"/> keeps in a bounded heap
    /// rather than buffering every row. Below it a heap wins twice: most
    /// candidates are rejected on one compare against the root and never
    /// copied, and memory stays at the bound. Past it the sifts of the rows
    /// that do get in add up to more than buffering and selecting once.
    /// </summary>
    private const int TopRowsHeapMaxRows = 4096;

    /// <summary>What <see cref="TopRows{T}.Classify"/> decides for a candidate row.</summary>
    private enum TopRowAdmission
    {
        Rejected,
        Admitted,

        /// <summary>
        /// Outside the bound, but tying the boundary row's keys — kept for a
        /// <c>WITH TIES</c> selection.
        /// </summary>
        Tie,
    }

    /// <summary>
    /// The rows an ORDER BY ranks, each with its sort keys and arrival position,
    /// held so that any window of ranks can be read off without sorting the
    /// rest — the one selection behind a statement's <c>TOP</c> / <c>OFFSET</c>
    /// / <c>FETCH</c>, a grouped query's <c>TOP</c>, and a bounded
    /// <c>ROW_NUMBER()</c> partition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is total</b>: the ORDER BY keys, then the arrival position.
    /// That tiebreak is what makes every strategy here answer with the rows a
    /// full sort would, in the same order, ties at the boundary included —
    /// real leaves a tie's order to the plan, so pinning it to arrival costs no
    /// fidelity and makes the heap and the buffer interchangeable.
    /// </para>
    /// <para>
    /// With a finite capacity this is a bounded max-heap of the <c>n</c>
    /// smallest rows seen, its root the worst row admitted, so once it is full
    /// a candidate is rejected on a single compare — which lets a caller hand
    /// reused scratch to <see cref="Classify"/> and copy only what is admitted.
    /// An <see cref="int.MaxValue"/> capacity buffers every row instead, and
    /// <see cref="Rank"/> selects the window it is asked for in linear
    /// expected time before sorting only that window, so a deep page costs
    /// about three compares a row rather than a full sort's seventeen.
    /// </para>
    /// <para>
    /// Under <c>WITH TIES</c> the heap also keeps the rejected candidates that
    /// tie its root. The root only ever improves, so a tie list is either still
    /// the boundary's (the root's replacement ties the row it evicted, which
    /// joins the list) or wholly outside it (it doesn't, and the list clears).
    /// </para>
    /// </remarks>
    private sealed class TopRows<T>(int capacity, List<OrderBySpec> orderBy, bool withTies = false)
    {
        /// <summary>One ranked row: the caller's payload, its sort keys, and its arrival position.</summary>
        public readonly struct Entry(T payload, SqlValue[] keys, int sequence)
        {
            public readonly T Payload = payload;
            public readonly SqlValue[] Keys = keys;
            public readonly int Sequence = sequence;
        }

        private readonly EntryOrder order = new(orderBy);

        private readonly List<Entry> ties = [];

        private Entry[] entries = new Entry[Math.Min(capacity, 16)];

        /// <summary>The rows held inside the capacity, not counting a <c>WITH TIES</c> selection's rejected ties.</summary>
        public int Count;

        /// <summary>
        /// Whether a candidate with these keys, arriving at
        /// <paramref name="sequence"/>, gets in — decided before the caller has
        /// copied anything.
        /// </summary>
        public TopRowAdmission Classify(SqlValue[] keys, int sequence)
        {
            if (this.Count < capacity)
                return TopRowAdmission.Admitted;
            var root = this.entries[0];
            var comparison = SortOrderKeys(keys, root.Keys, orderBy);
            return comparison < 0 || (comparison == 0 && sequence < root.Sequence)
                ? TopRowAdmission.Admitted
                : withTies && comparison == 0 ? TopRowAdmission.Tie : TopRowAdmission.Rejected;
        }

        /// <summary>
        /// Takes a row <see cref="Classify"/> didn't reject; the payload and
        /// keys are owned from here on.
        /// </summary>
        public void Add(TopRowAdmission admission, T payload, SqlValue[] keys, int sequence)
        {
            var entry = new Entry(payload, keys, sequence);
            if (admission == TopRowAdmission.Tie)
            {
                this.ties.Add(entry);
                return;
            }

            if (this.Count < capacity)
            {
                if (this.Count == this.entries.Length)
                    Array.Resize(ref this.entries, (int)Math.Min((long)this.entries.Length * 2, Array.MaxLength));
                this.entries[this.Count++] = entry;
                if (capacity != int.MaxValue)
                    this.SiftUp(this.Count - 1);
                return;
            }

            var evicted = this.entries[0];
            this.entries[0] = entry;
            this.SiftDown();
            if (withTies)
            {
                if (SortOrderKeys(this.entries[0].Keys, evicted.Keys, orderBy) == 0)
                    this.ties.Add(evicted);
                else
                    this.ties.Clear();
            }
        }

        /// <summary>
        /// The rows ranked <paramref name="start"/> up to (not including)
        /// <paramref name="end"/>, in order, both clamped to the rows held —
        /// followed, under <c>WITH TIES</c>, by every further row tying the
        /// last one. Reorders the storage, so it is read once.
        /// </summary>
        public ArraySegment<Entry> Rank(int start, int end)
        {
            end = Math.Min(end, this.Count);
            start = Math.Min(start, end);
            var span = this.entries.AsSpan(0, this.Count);
            if (capacity != int.MaxValue)
            {
                span.Sort(this.order);
                if (withTies && end > 0 && end == this.Count && this.ties.Count > 0)
                {
                    this.ties.Sort(this.order);
                    var withRest = new Entry[end + this.ties.Count];
                    span[..end].CopyTo(withRest);
                    this.ties.CopyTo(withRest, end);
                    return new(withRest, start, withRest.Length - start);
                }
                return new(this.entries, start, end - start);
            }

            if (end < span.Length)
                NthElement(span, end, this.order);
            if (start > 0)
                NthElement(span[..end], start, this.order);
            span[start..end].Sort(this.order);

            if (withTies && end > 0 && end < span.Length)
            {
                var boundary = span[end - 1].Keys;
                var tied = end;
                for (var i = end; i < span.Length; i++)
                {
                    if (SortOrderKeys(span[i].Keys, boundary, orderBy) == 0)
                    {
                        (span[i], span[tied]) = (span[tied], span[i]);
                        tied++;
                    }
                }
                span[end..tied].Sort(this.order);
                end = tied;
            }

            return new(this.entries, start, end - start);
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (this.order.Compare(this.entries[index], this.entries[parent]) <= 0)
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
                if (left >= this.Count)
                    return;
                var largest = this.order.Compare(this.entries[left], this.entries[index]) > 0 ? left : index;
                var right = left + 1;
                if (right < this.Count && this.order.Compare(this.entries[right], this.entries[largest]) > 0)
                    largest = right;
                if (largest == index)
                    return;
                (this.entries[largest], this.entries[index]) = (this.entries[index], this.entries[largest]);
                index = largest;
            }
        }

        /// <summary>
        /// Rearranges <paramref name="span"/> so the row ranked
        /// <paramref name="nth"/> sits at that index with every smaller row
        /// before it and every larger one after — Hoare's selection, over a
        /// median-of-three pivot. The order is total, so there are no equal
        /// rows to degrade it; a pass count past twice the depth a balanced
        /// split needs sorts the remaining range instead, which caps the worst
        /// case at a sort's.
        /// </summary>
        private static void NthElement(Span<Entry> span, int nth, EntryOrder order)
        {
            var lo = 0;
            var hi = span.Length - 1;
            var passes = (2 * BitOperations.Log2((uint)span.Length)) + 4;
            while (hi - lo > 16)
            {
                if (passes-- == 0)
                    break;
                var mid = lo + ((hi - lo) / 2);
                if (order.Compare(span[mid], span[lo]) < 0)
                    (span[mid], span[lo]) = (span[lo], span[mid]);
                if (order.Compare(span[hi], span[lo]) < 0)
                    (span[hi], span[lo]) = (span[lo], span[hi]);
                if (order.Compare(span[hi], span[mid]) < 0)
                    (span[hi], span[mid]) = (span[mid], span[hi]);

                // span[lo] <= pivot <= span[hi] now bound both scans.
                var pivot = span[mid];
                (span[mid], span[hi - 1]) = (span[hi - 1], span[mid]);
                var i = lo;
                var j = hi - 1;
                while (true)
                {
                    while (order.Compare(span[++i], pivot) < 0)
                    {
                    }
                    while (order.Compare(pivot, span[--j]) < 0)
                    {
                    }
                    if (i >= j)
                        break;
                    (span[i], span[j]) = (span[j], span[i]);
                }
                (span[i], span[hi - 1]) = (span[hi - 1], span[i]);

                if (i == nth)
                    return;
                if (nth < i)
                    hi = i - 1;
                else
                    lo = i + 1;
            }

            span[lo..(hi + 1)].Sort(order);
        }

        /// <summary>The total order: ORDER BY keys, then arrival.</summary>
        private readonly struct EntryOrder(List<OrderBySpec> orderBy) : IComparer<Entry>
        {
            public int Compare(Entry x, Entry y)
            {
                var comparison = SortOrderKeys(x.Keys, y.Keys, orderBy);
                return comparison != 0 ? comparison : x.Sequence.CompareTo(y.Sequence);
            }
        }
    }

    /// <summary>
    /// The select-list column each ORDER BY term reads its key from, or -1 for
    /// a term evaluated against the FROM sources: an ordinal names its column,
    /// and a bare unqualified name matching an output alias names that one —
    /// the binding <see cref="ComputeOrderKeys"/> gives a query without
    /// <c>DISTINCT</c>, settled once rather than per row.
    /// </summary>
    private static int[] OrderKeyProjectionColumns(List<OrderBySpec> orderBy, string[] outputColumnNames)
    {
        var columns = new int[orderBy.Count];
        for (var k = 0; k < orderBy.Count; k++)
        {
            var spec = orderBy[k];
            columns[k] = -1;
            if (spec.IsOrdinal)
            {
                columns[k] = spec.Ordinal - 1;
                continue;
            }
            if (!spec.MayNameAlias)
                continue;
            var term = spec.Expr;
            while (term is Parenthesized { Wrapped: var inner })
                term = inner;
            if (term is not Reference { ReferencedName: { ImmediateQualifier: null } name })
                continue;
            for (var j = 0; j < outputColumnNames.Length; j++)
            {
                if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                {
                    columns[k] = j;
                    break;
                }
            }
        }
        return columns;
    }

    /// <summary>
    /// The rank one past the last row a sorted result returns, out of
    /// <paramref name="count"/> ranked rows: the offset plus the <c>TOP</c> or
    /// <c>FETCH</c> count, a <c>PERCENT</c> of the whole, or every row.
    /// </summary>
    private static int RankWindowEnd(TopSpec top, int? offsetCount, int? fetchCount, int count)
    {
        var end = top.Percent is { } percent ? (long)Math.Ceiling(count * percent / 100.0)
            : (top.Count ?? fetchCount) is { } take ? (long)(offsetCount ?? 0) + take
            : count;
        return (int)Math.Min(end, count);
    }

    /// <summary>Whether <paramref name="expression"/> draws a <c>NEXT VALUE FOR</c> anywhere in it.</summary>
    private static bool DrawsNextValue(ExpressionNode expression)
    {
        var draws = false;
        expression.Walk(ref draws, static (visited, _, ref draws) =>
        {
            draws |= visited is NextValueFor;
            return !draws;
        });
        return draws;
    }

    /// <summary>
    /// A sorted projection without <c>DISTINCT</c>: ranks the rows the WHERE
    /// keeps by their ORDER BY keys alone, then projects only the rows the
    /// <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> window returns — real's
    /// plan, whose select list is computed above its Sort and Top, so an
    /// expression that would raise on a row outside the window never runs
    /// (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A key naming a select-list column (by ordinal or alias) evaluates that
    /// column for every row, as real's sort key must, and the projection
    /// reuses the value — so a <c>NEWID()</c> the rows sort by is the one
    /// they return.
    /// </para>
    /// <para>
    /// The window bound, where it is known before the scan (anything but
    /// <c>PERCENT</c>, which reads the row count), sizes the
    /// <see cref="TopRows{T}"/>: within <see cref="TopRowsHeapMaxRows"/> it is a
    /// heap copying only the rows it admits, otherwise every row is buffered
    /// as its tuple and keys and the window is selected at the end.
    /// </para>
    /// </remarks>
    private static IEnumerable<SqlValue[]> ProjectSorted(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        TopSpec top,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var offset = offsetCount ?? 0;
        // TOP and FETCH are mutually exclusive at parse time (Msg 10741), as
        // are TOP and OFFSET.
        var take = top.Percent is null ? top.Count ?? fetchCount : null;
        var bound = take is { } limit ? (long)offset + limit : long.MaxValue;
        var selection = new TopRows<(byte[]?[]? Tuple, SqlValue[]? Projected)>(
            bound <= TopRowsHeapMaxRows ? (int)Math.Max(bound, 1) : int.MaxValue, orderBy, top.WithTies);

        var keyColumns = OrderKeyProjectionColumns(orderBy, outputColumnNames);
        var keyOfColumn = new int[expressions.Count];
        Array.Fill(keyOfColumn, -1);
        for (var k = keyColumns.Length - 1; k >= 0; k--)
        {
            if (keyColumns[k] >= 0)
                keyOfColumn[keyColumns[k]] = k;
        }

        // A NEXT VALUE FOR (legal here under its own OVER) draws once per row,
        // deduplicated across a row's references by the row stamp, so a
        // select list reading one is projected whole while its row is read.
        var projectEagerly = expressions.Exists(DrawsNextValue);

        // Hoisted per-row resolution scaffolding — see ProjectStreaming.
        var memo = new SourceColumnMemo();
        var currentTuple = default(byte[]?[])!;
        Func<MultiPartName, SqlValue> resolveSource = null!;
        resolveSource = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, memo);
        var rowRuntime = new RuntimeContext(resolveSource, batch);

        // Keys go into reused scratch, copied only for a row the selection takes.
        var keys = new SqlValue[orderBy.Count];
        var sequence = 0;
        // The sort reads every row before the first leaves it, so an error any
        // row raises here precedes every INSERT identity draw.
        try
        {
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

                batch.BumpRowStamp();
                SqlValue[]? projected = null;
                if (projectEagerly)
                {
                    projected = new SqlValue[expressions.Count];
                    for (var i = 0; i < projected.Length; i++)
                        projected[i] = expressions[i].Run(rowRuntime);
                }
                for (var k = 0; k < keys.Length; k++)
                {
                    var column = keyColumns[k];
                    keys[k] = column < 0 ? orderBy[k].Expr!.Run(rowRuntime)
                        : projected is not null ? projected[column]
                        : keyOfColumn[column] < k ? keys[keyOfColumn[column]]
                        : expressions[column].Run(rowRuntime);
                }

                var admission = selection.Classify(keys, sequence);
                if (admission != TopRowAdmission.Rejected)
                    selection.Add(admission, (projected is null ? [.. tuple] : null, projected), [.. keys], sequence);
                sequence++;
            }
        }
        catch (SimulatedSqlException sortInputError)
        {
            sortInputError.RaisedInRowProjection = false;
            throw;
        }

        // The work table STATISTICS IO lists is a full sort's; a small TOP /
        // FETCH with no OFFSET is a Top N Sort, which lists none.
        if (TopNHeapCap(orderBy, distinct: false, top, offsetCount, fetchCount) is null)
            NoteSortWorktable(batch, sources, orderBy, expressions);

        foreach (var entry in selection.Rank(offset, RankWindowEnd(top, offsetCount, fetchCount, selection.Count)))
        {
            if (entry.Payload.Projected is { } eager)
            {
                yield return eager;
                continue;
            }

            currentTuple = entry.Payload.Tuple!;
            batch.BumpRowStamp();
            var projected = new SqlValue[expressions.Count];
            try
            {
                for (var i = 0; i < projected.Length; i++)
                    projected[i] = keyOfColumn[i] >= 0 ? entry.Keys[keyOfColumn[i]] : expressions[i].Run(rowRuntime);
            }
            catch (SimulatedSqlException projectionError)
            {
                // Above the sort, as real's projection is.
                projectionError.RaisedInRowProjection = true;
                throw;
            }
            yield return projected;
        }
    }
}
