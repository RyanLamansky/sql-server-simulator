using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-row ORDER BY key computation and lexicographic key comparison. Shared
/// by the buffered, windowed, and top-level set-op ORDER BY paths.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Evaluates each ORDER BY item against the current row. Ordinal items
    /// index directly into the projected row. Expression items resolve column
    /// references through an output-first resolver; without DISTINCT, names
    /// not in the output fall back to source columns (matching SQL Server's
    /// rule that ORDER BY can reference non-selected source columns). With
    /// DISTINCT, source fallback would be ambiguous post-dedup so a missing
    /// output match raises Msg 145.
    /// </summary>
    /// <summary>
    /// Whether a term names the same column a projection or grouping key
    /// reads. The leaf must match, and two qualifiers must agree when both
    /// sides carry one — either side written unqualified matches on the leaf,
    /// since an unqualified reference that resolves at all is unambiguous.
    /// Shared with the grouped-projection resolver, where matching on the leaf
    /// alone made `p.name` bind to a `b.name` grouping key across a join.
    /// </summary>
    internal static bool SourceReferenceMatches(MultiPartName source, MultiPartName term) =>
        BuiltInToken.Equals(source.Leaf, term.Leaf)
        && (term.ImmediateQualifier is null
            || source.ImmediateQualifier is null
            || BuiltInToken.Equals(source.ImmediateQualifier, term.ImmediateQualifier));

    /// <summary>
    /// The column each projection reads, or null where it isn't a plain column
    /// reference. An alias wrapper is unwrapped first, so <c>c.name AS Col5</c>
    /// reports <c>c.name</c>.
    /// </summary>
    internal static MultiPartName?[]? ProjectionSourceReferences(IReadOnlyList<Expression> projections)
    {
        MultiPartName?[]? sources = null;
        for (var i = 0; i < projections.Count; i++)
        {
            var expression = projections[i] is Expressions.NamedExpression named ? named.Inner : projections[i];
            if (expression is not Expressions.Reference reference)
                continue;
            sources ??= new MultiPartName?[projections.Count];
            sources[i] = reference.ReferencedName;
        }

        return sources;
    }

    private static SqlValue[] ComputeOrderKeys(
        List<OrderBySpec> orderBy,
        SqlValue[] projected,
        string[] outputColumnNames,
        MultiPartName?[]? projectionSources,
        bool distinct,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolveSource)
    {
        var keys = new SqlValue[orderBy.Count];
        for (var i = 0; i < orderBy.Count; i++)
        {
            var spec = orderBy[i];
            if (spec.IsOrdinal)
            {
                keys[i] = projected[spec.Ordinal - 1];
                continue;
            }

            keys[i] = spec.Expr!.Run(new RuntimeContext(name =>
            {
                // A *qualified* term names a source column, never an output
                // alias: real orders `SELECT val AS id FROM ob t ORDER BY t.id`
                // by t's id column even though an output alias `id` exists
                // (probe-confirmed). Only an unqualified term matches the
                // select list, so the alias scan is skipped when a qualifier
                // is present — matching on the leaf alone silently sorted by
                // the wrong column whenever a join brought a same-named
                // column into scope (`ORDER BY child.id` binding to the
                // projected `parent.id`). That holds under DISTINCT too: a
                // qualified term there has to match a projected *source*
                // reference (the check below) or it isn't in the select list at
                // all, which is Msg 145 — leaf-matching it against the output
                // names accepted `SELECT DISTINCT val AS id … ORDER BY t.id`,
                // which real rejects (probe-confirmed 2026-07-31).
                if (name.ImmediateQualifier is null)
                {
                    for (var j = 0; j < outputColumnNames.Length; j++)
                    {
                        if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                            return projected[j];
                    }
                }

                // Under DISTINCT the term must appear in the select list, but
                // it may name the *source* column behind a projected one
                // rather than its output alias: `SELECT DISTINCT c.name AS Col5
                // … ORDER BY c.name` is legal on real, and an ORM aliasing
                // every output positionally leaves no other spelling.
                if (distinct && projectionSources is not null)
                {
                    for (var j = 0; j < projectionSources.Length; j++)
                    {
                        if (projectionSources[j] is { } source && SourceReferenceMatches(source, name))
                            return projected[j];
                    }
                }

                return distinct
                    ? throw SimulatedSqlException.OrderByItemNotInSelectListWithDistinct()
                    : resolveSource(name);
            }, batch));
        }
        return keys;
    }

    /// <summary>
    /// The projected column an <em>unqualified</em> top-level ORDER BY term
    /// names by its output alias, or -1. A qualified term never matches here:
    /// real binds <c>alias.col</c> to the source column, so
    /// <c>SELECT c.extra AS id, c.id AS other … UNION … ORDER BY c.id</c> sorts
    /// by <c>other</c> even though an output alias <c>id</c> exists
    /// (probe-confirmed).
    /// </summary>
    private static int OutputNameOrdinalOf(MultiPartName name, string[] columnNames)
    {
        if (name.ImmediateQualifier is null)
        {
            for (var j = 0; j < columnNames.Length; j++)
            {
                if (BuiltInToken.Equals(columnNames[j], name.Leaf))
                    return j;
            }
        }

        return -1;
    }

    /// <summary>
    /// The projected column whose own *source* reference a top-level ORDER BY
    /// term names, or -1. <c>SELECT num AS Col2 … UNION … ORDER BY num</c>
    /// sorts by Col2 on real (probe-confirmed) — the spelling an ORM is left
    /// with when it aliases every output positionally.
    /// </summary>
    private static int ProjectionSourceOrdinalOf(MultiPartName name, MultiPartName?[]? projectionSources)
    {
        if (projectionSources is not null)
        {
            for (var j = 0; j < projectionSources.Length; j++)
            {
                if (projectionSources[j] is { } source && SourceReferenceMatches(source, name))
                    return j;
            }
        }

        return -1;
    }

    /// <summary>
    /// Computes ORDER BY keys for the top-level (post-set-op) sort directly off
    /// an encoded <c>byte[]</c> row, decoding only the columns an ORDER BY item
    /// references rather than the whole tuple. References resolve against the
    /// inner plan's projected columns / ordinals only — there are no source
    /// columns in the combined stream to fall back to, which is why
    /// <see cref="ValidateSetOpOrderByTerms"/> has already rejected every term
    /// that isn't one of them (Msg 104 / 207 / 4104 / 108 at parse, the way
    /// real binds it). <paramref name="columns"/> is the schema's cached
    /// <see cref="HeapColumn"/>[] so each per-column decode hits the RowLayout
    /// geometry cache.
    /// </summary>
    private static SqlValue[] ComputeTopLevelOrderKeys(
        List<OrderBySpec> orderBy,
        string[] columnNames,
        HeapColumn[] columns,
        MultiPartName?[]? projectionSources,
        byte[] rowBytes,
        BatchContext batch)
    {
        // Only a *bare* reference may resolve through the projection-source
        // fallback: real accepts `… UNION … ORDER BY num` (num behind a
        // projected column) but rejects any expression over such a name.
        // The parse-time validation enforces that for a validated plan; the
        // gate also holds the line for the shapes it declines to judge (a
        // branch whose FROM sources weren't captured, or a skip-mode
        // placeholder source).
        var termIsBareReference = false;
        SqlValue ResolveByOutputName(MultiPartName name)
        {
            var ordinal = OutputNameOrdinalOf(name, columnNames);
            if (ordinal < 0 && termIsBareReference)
                ordinal = ProjectionSourceOrdinalOf(name, projectionSources);
            return ordinal >= 0
                ? RowDecoder.DecodeColumn(columns, rowBytes, ordinal)
                : throw SimulatedSqlException.InvalidColumnName(name);
        }

        var keys = new SqlValue[orderBy.Count];
        for (var i = 0; i < orderBy.Count; i++)
        {
            var spec = orderBy[i];
            termIsBareReference = spec.Expr is Expressions.Reference;
            keys[i] = spec.IsOrdinal
                ? RowDecoder.DecodeColumn(columns, rowBytes, spec.Ordinal - 1)
                : spec.Expr!.Run(new RuntimeContext(ResolveByOutputName, batch));
        }
        return keys;
    }

    /// <summary>
    /// Lexicographic compare of two key tuples per the per-key descending
    /// flags. NULL is treated as the smallest value (NULL first under ASC,
    /// NULL last under DESC), matching SQL Server. Cross-type keys are
    /// promoted via <see cref="SqlType.Promote"/> before comparison.
    /// </summary>
    private static int CompareOrderKeys(SqlValue[] a, SqlValue[] b, List<OrderBySpec> orderBy)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var lk = a[i];
            var rk = b[i];
            int c;
            if (lk.IsNull && rk.IsNull)
            {
                c = 0;
            }
            else if (lk.IsNull)
            {
                c = -1;
            }
            else if (rk.IsNull)
            {
                c = 1;
            }
            else if (lk.Type == rk.Type)
            {
                c = lk.CompareTo(rk);
            }
            else
            {
                var common = SqlType.Promote(lk.Type, rk.Type);
                c = lk.CoerceTo(common).CompareTo(rk.CoerceTo(common));
            }

            if (orderBy[i].Descending)
                c = -c;
            if (c != 0)
                return c;
        }
        return 0;
    }

    /// <summary>
    /// Compares two non-NULL scalar values — the single-key, ascending form of
    /// <see cref="CompareOrderKeys"/>'s per-key compare (promoting to a common
    /// type when the declared types differ). Used to sort the WITHIN GROUP
    /// sort-key values for <c>PERCENTILE_CONT</c> / <c>PERCENTILE_DISC</c>.
    /// </summary>
    private static int CompareScalarValues(SqlValue a, SqlValue b)
    {
        if (a.Type == b.Type)
            return a.CompareTo(b);
        var common = SqlType.Promote(a.Type, b.Type);
        return a.CoerceTo(common).CompareTo(b.CoerceTo(common));
    }

    /// <summary>
    /// Largest <c>TOP (n)</c> the bounded top-N heap will take. Past this the
    /// heap's per-row sift stops being cheaper than sorting once, and the point
    /// of the path — reading a small window of a large scan without paying for
    /// the whole sort — no longer applies.
    /// </summary>
    private const int TopNHeapMaxRows = 1024;

    /// <summary>
    /// The row cap a buffered, sorted projection can serve from a bounded heap
    /// instead of sorting its whole buffer, or <see langword="null"/> when it
    /// can't. Eligible: an ORDER BY with a plain small <c>TOP (n)</c> or
    /// <c>FETCH</c> and nothing that needs the full ordered set behind it —
    /// <c>PERCENT</c> and <c>WITH TIES</c> both read the total row count or the
    /// boundary row's neighbours, an <c>OFFSET</c> skips into the middle of the
    /// order, and <c>DISTINCT</c> has to dedupe the whole set before the cap
    /// means anything.
    /// </summary>
    private static int? TopNHeapCap(List<OrderBySpec> orderBy, bool distinct, TopSpec top, int? offsetCount, int? fetchCount)
    {
        if (orderBy.Count == 0 || distinct || top.RequiresBuffering || offsetCount > 0)
            return null;

        // TOP and FETCH are mutually exclusive at parse time (Msg 10741).
        return (top.Count ?? fetchCount) is > 0 and <= TopNHeapMaxRows and var cap ? cap : null;
    }

    /// <summary>
    /// A bounded max-heap of the <c>n</c> smallest rows seen so far under
    /// <see cref="CompareOrderKeys"/> — the top-N of an ORDER BY, kept in one
    /// pass without sorting everything scanned. The root is the worst row
    /// admitted, so a candidate is rejected on a single compare once the heap
    /// is full, which is what makes the path O(rows) against the full sort's
    /// O(rows log rows) for a scan whose window is tiny.
    /// </summary>
    /// <remarks>
    /// A candidate tying the root is rejected, so among rows with equal keys
    /// the earliest-scanned survive — a stable pick, where the full-sort path's
    /// <c>List&lt;T&gt;.Sort</c> is an unstable introsort and picks arbitrarily.
    /// Real leaves it unspecified too (its Top N Sort operator carries no
    /// stability guarantee), so the two agree on every row whose key is strictly
    /// inside the window and may differ only on which members of a tie group
    /// spanning the boundary come back.
    /// </remarks>
    private sealed class TopNRowHeap(int capacity, List<OrderBySpec> orderBy)
    {
        private readonly List<(SqlValue[] Projected, SqlValue[] Keys)> entries = new(capacity);

        /// <summary>Admits <paramref name="keys"/> if it beats the worst row held, else drops it.</summary>
        public void Offer(SqlValue[] projected, SqlValue[] keys)
        {
            if (this.entries.Count < capacity)
            {
                this.entries.Add((projected, keys));
                this.SiftUp(this.entries.Count - 1);
                return;
            }

            if (CompareOrderKeys(keys, this.entries[0].Keys, orderBy) >= 0)
                return;
            this.entries[0] = (projected, keys);
            this.SiftDown();
        }

        /// <summary>
        /// <see cref="Offer"/> for a caller that reuses one pair of scratch
        /// arrays across candidates: the heap copies whatever it admits, so a
        /// rejected candidate costs no allocation at all. Worth it where the
        /// candidates are groups — a 73k-group <c>TOP (5)</c> allocated a
        /// projection and a key tuple per group to throw all but five away.
        /// The copies are value-identical (<see cref="SqlValue"/> is a readonly
        /// struct), so the rows are the ones <see cref="Offer"/> would hold.
        /// </summary>
        public void OfferCopying(SqlValue[] projected, SqlValue[] keys)
        {
            if (this.entries.Count >= capacity && CompareOrderKeys(keys, this.entries[0].Keys, orderBy) >= 0)
                return;
            this.Offer([.. projected], [.. keys]);
        }

        /// <summary>The rows held, in ORDER BY order — repeated root removal fills the result back to front.</summary>
        public SqlValue[][] Drain()
        {
            var ordered = new SqlValue[this.entries.Count][];
            for (var i = ordered.Length - 1; i >= 0; i--)
            {
                ordered[i] = this.entries[0].Projected;
                this.entries[0] = this.entries[^1];
                this.entries.RemoveAt(this.entries.Count - 1);
                this.SiftDown();
            }
            return ordered;
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (CompareOrderKeys(this.entries[index].Keys, this.entries[parent].Keys, orderBy) <= 0)
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
                var largest = CompareOrderKeys(this.entries[left].Keys, this.entries[index].Keys, orderBy) > 0 ? left : index;
                var right = left + 1;
                if (right < this.entries.Count
                    && CompareOrderKeys(this.entries[right].Keys, this.entries[largest].Keys, orderBy) > 0)
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
