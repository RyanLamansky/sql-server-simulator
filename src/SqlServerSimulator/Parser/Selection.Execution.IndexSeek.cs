using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

internal sealed partial class Selection
{
    /// <summary>
    /// Narrows a single-base-table scan to an index seek when WHERE carries
    /// top-level conjuncts of the shape <c>indexedColumn = &lt;stable value&gt;</c>
    /// — a literal, a variable, or an outer/correlated column reference. This
    /// is what collapses a correlated <c>EXISTS</c> / <c>IN</c> / scalar
    /// subquery (and an unindexed-inner <c>APPLY</c>) from O(outer × inner) to
    /// one cache build plus O(outer) seeks: the inner re-executes per outer row
    /// but the per-table cache persists across those calls.
    /// <para>
    /// When several equality conjuncts line up with the <b>leading prefix</b> of
    /// one index's key columns (<c>a = x AND b = y</c> against an index on
    /// <c>(a, b, …)</c>), the seek keys on the whole matched prefix — so a
    /// non-selective leading column (a flag, a low-cardinality FK) no longer
    /// drags the whole bucket through the residual filter. The longest usable
    /// prefix across all keys / indexes wins. A stable range bound on the key
    /// column immediately after the prefix extends the seek predicate one
    /// column further (<c>a = x AND b &gt; 5</c> seeks the in-range slice of
    /// <c>a</c>'s group) — mirroring a real index seek predicate: an equality
    /// prefix plus at most one range column, everything deeper residual.
    /// </para>
    /// <para>
    /// Returns the same array when no seek applies. Every matched conjunct is
    /// <b>kept</b> in <paramref name="excluders"/> as a residual filter, so the
    /// seek can only narrow the row source — never change results. The value
    /// side is restricted to side-effect-free, row-invariant shapes precisely
    /// so evaluating it once here and again in the residual WHERE is harmless.
    /// </para>
    /// <para>
    /// <paramref name="planSources"/> names the FROM the narrowed source belongs
    /// to, so that a column reference on the value side can be classified as a
    /// sibling of that FROM (declines) or an escape to the enclosing scope
    /// (accepts) — see <see cref="IsEnclosingScopeReference"/>. Null means the
    /// narrowed source is the whole FROM, which is every caller but
    /// <see cref="NarrowJoinSources"/>.
    /// </para>
    /// </summary>
    private static FromSource[] MaybeApplyIndexSeek(
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        FromSource[]? planSources = null)
        => MaybeApplyIndexSeek(sources, joins, excluders, batch, outerResolver, planSources, out _);

    /// <summary>
    /// The narrowing above, additionally reporting how many candidate row
    /// addresses the seek selected (<c>-1</c> when it declined and the source
    /// keeps its full scan). The count is the seek's own pre-materialization
    /// address list, so it costs nothing to report and bounds the row count
    /// from above — the lock / snapshot materializer can still drop a
    /// tombstoned or invisible candidate. <c>NarrowJoinSources</c> reads it to
    /// pick which narrowed source drives a reordered join chain.
    /// </summary>
    private static FromSource[] MaybeApplyIndexSeek(
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        FromSource[]? planSources,
        out int seekedCandidates)
    {
        seekedCandidates = -1;
        if (sources.Length != 1 || joins.Length != 0 || excluders.Count == 0)
            return sources;
        var source = sources[0];
        if (source.BackingTable is not { } table || source.LateralPlan is not null)
            return sources;

        if (source.HeapPlan is not { } plan)
        {
            IndexSeekDiagnostics.Sink?.Add($"Scan({table.Name})");
            return sources;
        }

        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        var equalities = CollectColumnEqualities(
            source, conjuncts, allowCorrelatedColumnValue: true, planSources, table, batch, outerResolver);
        var bounds = CollectRangeBounds(source, conjuncts, allowCorrelatedColumnValue: true, planSources);

        // A SERIALIZABLE / HOLDLOCK reader's phantom fence is settled here,
        // before any candidate address is read: the conjuncts that bound the
        // seek are exactly the ones that bound the range, and locking after
        // the read would leave a window in which a concurrent insert lands
        // unseen yet unfenced. Settling it before the seek is even known to
        // apply costs at worst the whole-table fallback the scan path would
        // have taken anyway.
        SettleSerializablePhantomFence(source, table, plan, batch, outerResolver, equalities, bounds);

        // The seek narrows the row source, then routes each candidate through
        // the SAME per-row lock / conflict pipeline the full scan uses — so it
        // touches (and locks) only the seeked rows, matching a real index seek.
        // tx-scoped row locks (REPEATABLE READ / UPDLOCK / XLOCK) keep the
        // whole-table scan, which deliberately locks every row it reads to end
        // of transaction — their phantom fence is settled above all the same,
        // since a SERIALIZABLE reader carrying UPDLOCK / XLOCK owes one.
        if (plan.RowTxScoped)
        {
            IndexSeekDiagnostics.Sink?.Add($"Scan({table.Name})");
            return sources;
        }

        // A snapshot / RCSI reader sees the version visible at its snapshot, not
        // necessarily the live heap row. ResolveSnapshotXidForRead returns that
        // snapshot xid (and pins the statement / tx snapshot as a side effect the
        // row-touch path relies on whether the read seeks or scans). When it's
        // non-null the seek still runs, but materializes each candidate through
        // the version store and additionally sweeps the rows carrying a version
        // chain — those are the only rows whose snapshot-visible key can differ
        // from their live key, so a live-key-only seek could miss them. With an
        // empty version store the sweep is empty and every candidate resolves to
        // its live bytes. See MaterializeSnapshotCandidates.
        var snapshotXid = batch.ResolveSnapshotXidForRead(table);

        if (equalities.Count != 0
            && TrySeekByLongestPrefix(source, table, plan, batch, snapshotXid, outerResolver, equalities, bounds, out var seekRows, out var width, out var rangeExtended, out var equalityCandidates))
        {
            IndexSeekDiagnostics.Sink?.Add($"Seek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"SeekWidth({table.Name},{width})");
            if (rangeExtended)
                IndexSeekDiagnostics.Sink?.Add($"PrefixRangeSeek({table.Name})");
            seekedCandidates = equalityCandidates;
            return SeekedSource(source, seekRows);
        }

        // No equality seek on the conjunction — try the union of seeks a
        // cross-column OR conjunct offers (`a = 1 OR b = 2`, whose disjuncts
        // seek separately and dedupe by row address). Ordered after the
        // equality prefix so a conjunction that seeks on its own keeps its
        // single access path, and before the range seek because equality
        // probes are the narrower predicate: a read whose WHERE offers both
        // takes the OR's point probes and leaves the bound residual.
        if (TryComputeUnionCandidates(
            source, table, batch, outerResolver, conjuncts, allowCorrelatedColumnValue: true, planSources,
            out var unionCandidates, out var unionDisjuncts))
        {
            IndexSeekDiagnostics.Sink?.Add($"Seek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"UnionSeek({table.Name},{unionDisjuncts})");
            IndexSeekDiagnostics.Sink?.Add($"UnionSeekCandidates({table.Name},{unionCandidates.Count})");
            seekedCandidates = unionCandidates.Count;
            return SeekedSource(source, snapshotXid is { } unionSx
                ? MaterializeSnapshotCandidates(table, batch, unionSx, unionCandidates)
                : MaterializeWithLockChecks(table, batch, plan, unionCandidates));
        }

        // No equality seek — try a range seek on a leading key column
        // (col > v / col BETWEEN lo AND hi / a one-sided bound). The matched
        // bound conjunct(s) stay in the residual WHERE, so the range only
        // narrows the candidate set.
        if (TrySeekByRange(source, table, plan, batch, snapshotXid, outerResolver, bounds, out var rangeRows, out var rangeCandidates))
        {
            IndexSeekDiagnostics.Sink?.Add($"Seek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"RangeSeek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"SeekWidth({table.Name},1)");
            seekedCandidates = rangeCandidates;
            return SeekedSource(source, rangeRows);
        }

        IndexSeekDiagnostics.Sink?.Add($"Scan({table.Name})");
        return sources;
    }

    /// <summary>
    /// Takes the phantom protection a SERIALIZABLE / <c>HOLDLOCK</c> reader is
    /// still owed over <paramref name="table"/>: a key-range lock over the
    /// tuple interval the sargable conjuncts pin on the leading columns of some
    /// key / index, or — when no conjunct offers one — the whole-table S the
    /// scan path falls back to. A no-op for every other isolation level. The
    /// mode comes off the plan, so an <c>UPDLOCK</c> / <c>XLOCK</c> reader
    /// fences the same interval in <c>RangeS-U</c> / <c>RangeX-X</c>.
    /// </summary>
    private static void SettleSerializablePhantomFence(
        FromSource source,
        HeapTable table,
        DataLockPlan plan,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, Expression[]> equalities,
        Dictionary<int, RangeBoundExprs> bounds)
    {
        if (plan.SerializableRangeMode is not { } mode)
            return;
        // Deliberately not short-circuited on an already-settled fence: a
        // correlated inner re-plans per outer row, and each outer value names
        // an interval of its own that has to be fenced too.
        if (ComputeSerializableKeyRange(source, table, batch, outerResolver, equalities, bounds) is not { } range)
        {
            batch.EnsureSerializableTableLock(table, plan);
            return;
        }

        if (plan.Fence is { } fence)
            fence.Settled = true;
        batch.AcquireKeyRangeLockTxScoped(table, range, mode);
    }

    /// <summary>
    /// The key-space interval a SERIALIZABLE reader can fence instead of
    /// locking the whole table, or <c>null</c> when no conjunct offers one.
    /// <para>
    /// Soundness rests on the same property the seek does: every conjunct here
    /// is a top-level <c>AND</c> factor of the predicate, so every row the
    /// query can ever return satisfies it, so every row that could become a
    /// phantom carries a key tuple inside the returned interval. Only a
    /// <b>leading prefix</b> of some key / index qualifies — mirroring real,
    /// which range-locks along an index and takes an object-level S when there
    /// is no index to walk.
    /// </para>
    /// <para>
    /// The fence follows the equality prefix as deep as the conjuncts pin it
    /// (<c>a = 1 AND b = 2</c> against a key on <c>(a, b)</c> fences the single
    /// tuple), and extends one column further when a range bound lands on the
    /// key column right after the prefix (<c>a = 1 AND b BETWEEN 2 AND 5</c>).
    /// A prefix the predicate stops short of stays open, so <c>a = 1</c> alone
    /// fences every <c>b</c> under <c>a = 1</c> and nothing else. The longest
    /// prefix across all keys / indexes wins, keys walked before indexes so a
    /// tie doesn't ride on dictionary enumeration order, and a bound
    /// continuation breaks a tie between equal prefixes.
    /// </para>
    /// <para>
    /// An <c>IN</c> list collapses to the hull of its values — one interval
    /// spanning the lowest to the highest, gaps between them included, which
    /// over-blocks rather than leaving a value unfenced. Across a multi-column
    /// prefix the same hull is taken per column, so the lexicographic interval
    /// spans the whole cartesian product and then some.
    /// </para>
    /// </summary>
    private static KeyRange? ComputeSerializableKeyRange(
        FromSource source,
        HeapTable table,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, Expression[]> equalities,
        Dictionary<int, RangeBoundExprs> bounds)
    {
        if (equalities.Count == 0 && bounds.Count == 0)
            return null;

        var resolved = new Dictionary<int, (SqlType Common, SqlValue[] Probes)?>();
        int[]? bestOrdinals = null;
        var bestLength = 0;
        var bestContinues = false;
        foreach (var ordinals in EnumerateKeyOrdinals(table))
        {
            var length = 0;
            while (length < ordinals.Length && ResolveComponent(ordinals[length]) is not null)
                length++;
            var continues = length < ordinals.Length && bounds.ContainsKey(ordinals[length]);
            if (length == 0 && !continues)
                continue;
            if (bestOrdinals is null || length > bestLength || (length == bestLength && continues && !bestContinues))
                (bestOrdinals, bestLength, bestContinues) = (ordinals, length, continues);
        }

        if (bestOrdinals is null)
            return null;

        var width = bestLength + (bestContinues ? 1 : 0);
        var rangeOrdinals = bestOrdinals[..width];
        var commons = new SqlType[width];
        var lower = new List<SqlValue>(width);
        var upper = new List<SqlValue>(width);
        for (var i = 0; i < bestLength; i++)
        {
            var (common, probes) = resolved[rangeOrdinals[i]]!.Value;
            commons[i] = common;
            var low = probes[0];
            var high = probes[0];
            for (var p = 1; p < probes.Length; p++)
            {
                if (probes[p].CompareTo(low) < 0)
                    low = probes[p];
                if (probes[p].CompareTo(high) > 0)
                    high = probes[p];
            }

            lower.Add(low);
            upper.Add(high);
        }

        var lowerInclusive = true;
        var upperInclusive = true;
        if (bestContinues)
        {
            var boundOrdinal = rangeOrdinals[bestLength];
            var bound = bounds[boundOrdinal];
            // A NULL bound makes the conjunct UNKNOWN for every row present or
            // future, so the query's result is permanently empty and no insert
            // can phantom into it; an unevaluatable one can't narrow anything.
            // Either way the equality prefix behind it is still a sound fence,
            // so keep that and drop the continuation.
            if (EvaluateRangeBounds(bound, source.StoredSchema[boundOrdinal].Type, batch, outerResolver,
                out var boundCommon, out var hasLower, out var lowerValue, out var hasUpper, out var upperValue) == BoundEval.Value)
            {
                commons[bestLength] = boundCommon;
                if (hasLower)
                {
                    lower.Add(lowerValue);
                    lowerInclusive = bound.LowerInclusive;
                }
                if (hasUpper)
                {
                    upper.Add(upperValue);
                    upperInclusive = bound.UpperInclusive;
                }
            }
            else if (bestLength == 0)
            {
                return null;
            }
            else
            {
                rangeOrdinals = rangeOrdinals[..bestLength];
                Array.Resize(ref commons, bestLength);
            }
        }

        return new KeyRange(rangeOrdinals, commons, [.. lower], lowerInclusive, [.. upper], upperInclusive);

        // Resolves (and memoizes) the probe components for one column, null for
        // a column that can't anchor the fence — no stable-value equality, or a
        // dropped probe (NULL, cross-collation, unpromotable) whose hull
        // wouldn't span every value the residual predicate still admits. Either
        // bounds the prefix there rather than under-fencing.
        (SqlType Common, SqlValue[] Probes)? ResolveComponent(int storageOrdinal)
        {
            if (resolved.TryGetValue(storageOrdinal, out var cached))
                return cached;
            (SqlType Common, SqlValue[] Probes)? component = null;
            if (equalities.TryGetValue(storageOrdinal, out var valueSides)
                && EvaluateProbeComponent(source, storageOrdinal, valueSides, batch, outerResolver) is { } evaluated
                && evaluated.Probes.Length == valueSides.Length)
            {
                component = evaluated;
            }

            resolved[storageOrdinal] = component;
            return component;
        }
    }

    // Maps each indexable column of THIS source carrying a stable-value
    // equality conjunct (or IN-list / OR-of-equalities on the same column) to
    // its value side(s). First writer wins per column; a redundant later
    // conjunct just stays as a residual filter.
    // <paramref name="probeTable"/> / <paramref name="probeBatch"/> opt the
    // source into the drive-side transform for a small uncorrelated
    // `col IN (SELECT …)`: its values become one more equality family. Passing
    // neither (every caller but the query path) leaves such a conjunct alone.
    private static Dictionary<int, Expression[]> CollectColumnEqualities(
        FromSource source,
        List<BooleanExpression> conjuncts,
        bool allowCorrelatedColumnValue,
        FromSource[]? planSources = null,
        HeapTable? probeTable = null,
        BatchContext? probeBatch = null,
        Func<MultiPartName, SqlValue>? outerResolver = null)
    {
        var equalities = new Dictionary<int, Expression[]>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var left, out var right))
            {
                _ = TryRecordColumnEquality(source, left, right, equalities, allowCorrelatedColumnValue, planSources)
                    || TryRecordColumnEquality(source, right, left, equalities, allowCorrelatedColumnValue, planSources);
                continue;
            }
            if (conjunct.TryGetEqualityFamily(out var family))
            {
                _ = TryRecordEqualityFamily(source, family, equalities, allowCorrelatedColumnValue, planSources);
                continue;
            }
            if (probeTable is not null && probeBatch is not null)
                _ = TryRecordSubqueryProbeFamily(source, probeTable, probeBatch, outerResolver, conjunct, equalities);
        }

        return equalities;
    }

    /// <summary>
    /// Records a small <b>uncorrelated</b> <c>col IN (SELECT …)</c>'s values as
    /// this column's equality family, which is what makes the read drive from
    /// the values (one seek each) rather than scan every row and probe it
    /// against them. The subject has to be an indexable column that <b>leads</b>
    /// some key / index — the structural precondition for a seek — and that is
    /// asked <em>before</em> the body is materialized, so a body that could
    /// never drive one is never executed on this account. The materialization
    /// itself goes through the statement's subquery memo, so the per-row
    /// evaluation reads the same values rather than running the body again, and
    /// a correlated body simply records itself as per-row there and declines.
    /// The <c>IN</c> conjunct stays in the residual WHERE like every other
    /// matched conjunct, so a NULL among the inner values — left out of the
    /// probes, since it equi-matches nothing — still reaches its three-valued
    /// answer for the rows the seek did select, and the rows it didn't select
    /// were ones the predicate answered FALSE or UNKNOWN for anyway.
    /// </summary>
    private static bool TryRecordSubqueryProbeFamily(
        FromSource source,
        HeapTable table,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        BooleanExpression conjunct,
        Dictionary<int, Expression[]> equalities)
    {
        return conjunct.TryGetSubqueryProbeSubject(out var subject)
            && TryIdentifyIndexableColumn(source, subject, out var ordinal)
            && !equalities.ContainsKey(ordinal)
            && LeadsSomeKeyOrIndex(table, ordinal)
            && conjunct.TryMaterializeProbeFamily(
                batch,
                outerResolver ?? ThrowOnColumnReference,
                source.StoredSchema[ordinal].Type,
                UnionSeekProbeCap,
                out var family)
            && TryRecordEqualityFamily(source, family, equalities, allowCorrelatedColumnValue: false);
    }

    // Whether a storage ordinal is the leading key column of some key / index —
    // the structural precondition a probe family needs to seek at all.
    private static bool LeadsSomeKeyOrIndex(HeapTable table, int storageOrdinal)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.StorageOrdinals.Length > 0 && key.StorageOrdinals[0] == storageOrdinal)
                return true;
        }
        foreach (var index in table.Indexes)
        {
            if (index.KeyColumns.Length > 0 && index.KeyColumns[0].StorageOrdinal == storageOrdinal)
                return true;
        }
        return false;
    }

    // Wraps a narrowed row stream back into a single-source array, preserving the
    // original source's column / storage / view metadata.
    private static FromSource[] SeekedSource(FromSource source, IEnumerable<byte[]> rows) =>
    [
        new FromSource(
            source.Qualifier, source.ColumnNames, source.Columns, source.StoredSchema,
            source.StorageOrdinals, source.LobStore, rows, source.LateralPlan,
            source.BackingTable, source.BackingView),
    ];

    // The lower / upper bound expressions collected for one column from range
    // conjuncts. First writer wins per side: a redundant second bound (e.g. a
    // looser `col > 0` alongside `col > 5`) stays as a residual filter.
    private sealed class RangeBoundExprs
    {
        public Expression? Lower;
        public bool LowerInclusive;
        public Expression? Upper;
        public bool UpperInclusive;
    }

    /// <summary>
    /// Narrows a single-base-table scan to a range seek when WHERE carries a
    /// range bound (<c>col &gt; v</c> / <c>col &lt;= v</c> / <c>col BETWEEN lo AND
    /// hi</c>, either operand order) on the <b>leading</b> key column of some
    /// index or key, and the bound value(s) are stable. This is the no-equality
    /// fallback: an equality-prefix continued by a range is narrowed by the
    /// equality path instead (its seek predicate extends one range column past
    /// the prefix), and a range on a non-leading, non-continuation column stays
    /// residual. The bound conjuncts remain in the residual WHERE, so the seek
    /// only narrows the candidate set.
    /// </summary>
    private static bool TrySeekByRange(
        FromSource source,
        HeapTable table,
        DataLockPlan plan,
        BatchContext batch,
        long? snapshotXid,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, RangeBoundExprs> bounds,
        out IEnumerable<byte[]> seekRows,
        out int candidateCount)
    {
        if (!TryComputeRangeCandidates(source, table, batch, outerResolver, bounds, out var candidates))
        {
            seekRows = [];
            candidateCount = -1;
            return false;
        }

        candidateCount = candidates.Count;
        seekRows = snapshotXid is { } sx
            ? MaterializeSnapshotCandidates(table, batch, sx, candidates)
            : MaterializeWithLockChecks(table, batch, plan, candidates);
        return true;
    }

    /// <summary>
    /// Row count below which the range seek's span gate (see
    /// <see cref="RangeSpanGateDivisor"/>) never engages. Under it the candidate
    /// list is small however wide the interval, so the seek's random-address
    /// materialization can't lose to the sequential scan by enough to matter, and
    /// leaving the gate off keeps the access path predictable for the small
    /// tables a seek is easiest to reason about.
    /// </summary>
    private const int RangeSpanGateMinRows = 1024;

    /// <summary>
    /// The reciprocal of the share of a table's rows a range may select before
    /// the seek stops paying for itself — 4, so an interval selecting more than a
    /// quarter of the rows falls back to the scan. A range seek trades a
    /// sequential page walk for a per-address <c>ReadSlotBytes</c> plus an
    /// ordered-view walk whose per-key comparer calls the scan doesn't pay; at a
    /// wide span those costs dominate (measured 3× SLOWER than the scan for a
    /// 99%-selecting <c>&gt;</c> on a 231k-row table, and 16 MB more allocated).
    /// The bounds are searched first and the span compared afterwards, so the
    /// cache is already built when the gate declines — which is fine: the build
    /// is shared with every other seek against that column and is what makes the
    /// <em>next</em> narrow range on it free.
    /// </summary>
    private const int RangeSpanGateDivisor = 4;

    // Address-only core of the single-column leading range seek, shared by the
    // query path (TrySeekByRange) and the mutation path. Returns true with the
    // in-range (page, slot) candidates (possibly empty — a NULL bound seeks to
    // nothing), or false when no range bound lands on a leading key column or
    // the interval spans too much of the table to be worth seeking.
    private static bool TryComputeRangeCandidates(
        FromSource source,
        HeapTable table,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, RangeBoundExprs> bounds,
        out List<(int Page, int Slot)> candidates)
    {
        candidates = [];

        if (bounds.Count == 0 || FindRangeLeadingOrdinal(table, bounds) is not { } ordinal)
            return false;

        var bound = bounds[ordinal];
        switch (EvaluateRangeBounds(bound, source.StoredSchema[ordinal].Type, batch, outerResolver,
            out var common, out var hasLower, out var lowerValue, out var hasUpper, out var upperValue))
        {
            case BoundEval.Decline: return false;
            case BoundEval.Null: return true;
        }

        var rowCount = table.Heap.RowCount;
        var cache = HeapSeekCache.For(table.Heap);
        var found = cache.RangeScan(
            table.Heap, source.StoredSchema, source.LobStore, ordinal, common,
            hasLower, lowerValue, bound.LowerInclusive, hasUpper, upperValue, bound.UpperInclusive,
            rowCount >= RangeSpanGateMinRows ? rowCount / RangeSpanGateDivisor : int.MaxValue);

        if (found is null)
        {
            IndexSeekDiagnostics.Sink?.Add($"RangeSpanTooWide({table.Name})");
            return false;
        }

        candidates = found;
        return true;
    }

    // Collects, per indexable column of THIS source, the stable-value range
    // bounds among the top-level conjuncts (`col > v` / `col <= v` / `col
    // BETWEEN lo AND hi`, either operand order). First writer wins per side; a
    // redundant looser bound stays a residual filter.
    private static Dictionary<int, RangeBoundExprs> CollectRangeBounds(
        FromSource source, List<BooleanExpression> conjuncts, bool allowCorrelatedColumnValue, FromSource[]? planSources = null)
    {
        var bounds = new Dictionary<int, RangeBoundExprs>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetRangeOperands(out var left, out var op, out var right))
            {
                if (TryIdentifyIndexableColumn(source, left, out var leftOrd) && IsStableValueSide(right, source, allowCorrelatedColumnValue, planSources))
                    RecordBound(bounds, leftOrd, op, right);
                else if (TryIdentifyIndexableColumn(source, right, out var rightOrd) && IsStableValueSide(left, source, allowCorrelatedColumnValue, planSources))
                    RecordBound(bounds, rightOrd, FlipComparison(op), left);
                continue;
            }

            if (conjunct.TryGetBetweenOperands(out var value, out var lower, out var upper)
                && TryIdentifyIndexableColumn(source, value, out var betweenOrd)
                && IsStableValueSide(lower, source, allowCorrelatedColumnValue, planSources)
                && IsStableValueSide(upper, source, allowCorrelatedColumnValue, planSources))
            {
                RecordBound(bounds, betweenOrd, RangeComparison.GreaterOrEqual, lower);
                RecordBound(bounds, betweenOrd, RangeComparison.LessOrEqual, upper);
            }
        }

        return bounds;
    }

    // Evaluates a column's collected range bound(s) against the column type,
    // unifying their promoted type with the column's and coercing both to it.
    // Decline → no usable range (promotion / collation failure — fall back);
    // Null → a NULL bound makes every comparison UNKNOWN, so the range matches
    // nothing (a valid empty seek); Value → the present bound value(s) are
    // coerced to <paramref name="common"/> and usable.
    private static BoundEval EvaluateRangeBounds(
        RangeBoundExprs bound, SqlType columnType, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver,
        out SqlType common, out bool hasLower, out SqlValue lowerValue, out bool hasUpper, out SqlValue upperValue)
    {
        common = columnType;
        hasLower = bound.Lower is not null;
        hasUpper = bound.Upper is not null;
        lowerValue = default;
        upperValue = default;

        SqlType? unified = null;
        if (bound.Lower is { } lowerExpr)
        {
            switch (EvaluateBound(lowerExpr, columnType, batch, outerResolver, out lowerValue, out var lowerCommon))
            {
                case BoundEval.Decline: return BoundEval.Decline;
                case BoundEval.Null: return BoundEval.Null;
                default: unified = lowerCommon; break;
            }
        }
        if (bound.Upper is { } upperExpr)
        {
            switch (EvaluateBound(upperExpr, columnType, batch, outerResolver, out upperValue, out var upperCommon))
            {
                case BoundEval.Decline: return BoundEval.Decline;
                case BoundEval.Null: return BoundEval.Null;
                default: unified = unified is null ? upperCommon : SqlType.Promote(unified, upperCommon); break;
            }
        }

        if (unified is null)
            return BoundEval.Decline;

        common = unified;
        if (hasLower)
            lowerValue = lowerValue.CoerceTo(common);
        if (hasUpper)
            upperValue = upperValue.CoerceTo(common);
        return BoundEval.Value;
    }

    // Records one bound for a column, first-writer-wins per side.
    private static void RecordBound(Dictionary<int, RangeBoundExprs> bounds, int ordinal, RangeComparison op, Expression valueSide)
    {
        if (!bounds.TryGetValue(ordinal, out var bound))
            bounds[ordinal] = bound = new RangeBoundExprs();
        RecordBound(bound, op, valueSide);
    }

    private static void RecordBound(RangeBoundExprs bound, RangeComparison op, Expression valueSide)
    {
        switch (op)
        {
            case RangeComparison.Greater when bound.Lower is null:
                (bound.Lower, bound.LowerInclusive) = (valueSide, false);
                break;
            case RangeComparison.GreaterOrEqual when bound.Lower is null:
                (bound.Lower, bound.LowerInclusive) = (valueSide, true);
                break;
            case RangeComparison.Less when bound.Upper is null:
                (bound.Upper, bound.UpperInclusive) = (valueSide, false);
                break;
            case RangeComparison.LessOrEqual when bound.Upper is null:
                (bound.Upper, bound.UpperInclusive) = (valueSide, true);
                break;
        }
    }

    // Normalizes a range conjunct to (column ordinal, operator, value): the column
    // may be on either side (the operator flips when it's on the right). Returns
    // false when neither side is an indexable column of this source with a stable
    // value on the other.
    private static bool TryNormalizeRangeBound(
        FromSource source, Expression left, RangeComparison op, Expression right,
        out int ordinal, out RangeComparison normalizedOp, out Expression value)
    {
        if (TryIdentifyIndexableColumn(source, left, out ordinal) && IsStableValueSide(right, source))
        {
            (normalizedOp, value) = (op, right);
            return true;
        }
        if (TryIdentifyIndexableColumn(source, right, out ordinal) && IsStableValueSide(left, source))
        {
            (normalizedOp, value) = (FlipComparison(op), left);
            return true;
        }
        (normalizedOp, value) = (op, left);
        return false;
    }

    // Flips a comparison whose column is on the right (`v < col` ≡ `col > v`).
    private static RangeComparison FlipComparison(RangeComparison op) => op switch
    {
        RangeComparison.Greater => RangeComparison.Less,
        RangeComparison.GreaterOrEqual => RangeComparison.LessOrEqual,
        RangeComparison.Less => RangeComparison.Greater,
        _ => RangeComparison.GreaterOrEqual,
    };

    // The leading storage ordinal of the first key / index whose lead column
    // carries a bound, or null if none does. Keys (PK / UNIQUE) are preferred
    // over CREATE INDEX entries, matching the equality path's order.
    private static int? FindRangeLeadingOrdinal(HeapTable table, Dictionary<int, RangeBoundExprs> bounds)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.StorageOrdinals.Length > 0 && bounds.ContainsKey(key.StorageOrdinals[0]))
                return key.StorageOrdinals[0];
        }
        foreach (var index in table.Indexes)
        {
            if (index.KeyColumns.Length > 0 && bounds.ContainsKey(index.KeyColumns[0].StorageOrdinal))
                return index.KeyColumns[0].StorageOrdinal;
        }
        return null;
    }

    private enum BoundEval
    {
        Decline,
        Null,
        Value,
    }

    // Evaluates a bound expression and promotes it against the column type.
    // Decline → fall back to the scan (collation / promotion failure, or a
    // SimulatedSqlException while evaluating). Null → the bound is NULL, so the
    // range matches nothing. Value → `value` (coerced to `common`) is usable.
    private static BoundEval EvaluateBound(
        Expression expr, SqlType columnType, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, out SqlValue value, out SqlType common)
    {
        value = default;
        common = columnType;
        SqlValue evaluated;
        try
        {
            evaluated = expr.Run(new RuntimeContext(
                name => outerResolver is { } resolve ? resolve(name) : SqlValue.Null(SqlType.Int32),
                batch));
        }
        catch (SimulatedSqlException)
        {
            return BoundEval.Decline;
        }

        if (evaluated.IsNull)
            return BoundEval.Null;

        if (columnType.Category == SqlTypeCategory.String
            && evaluated.Type.Category == SqlTypeCategory.String
            && Collation.Resolve(columnType, evaluated.Type) is null)
        {
            return BoundEval.Decline;
        }

        try
        {
            common = SqlType.Promote(columnType, evaluated.Type);
            value = evaluated.CoerceTo(common);
        }
        catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
        {
            return BoundEval.Decline;
        }

        return BoundEval.Value;
    }

    /// <summary>
    /// Eliminates the ORDER BY sort when the requested order matches the key order
    /// of some index / key — streaming the source in key order instead of
    /// buffering and sorting. Covers three shapes, all reusing the same
    /// incrementally-maintained ordered view:
    /// <list type="bullet">
    /// <item>a single NOT NULL leading key column (<c>ORDER BY id</c>), optionally
    /// range-narrowed on that same column (<c>… WHERE id &gt; 100 ORDER BY id</c>);</item>
    /// <item>a multi-column leading prefix (<c>ORDER BY a, b</c> against a key on
    /// <c>(a, b, …)</c>), every order column NOT NULL;</item>
    /// <item>an equality prefix continued by the order columns (<c>WHERE a = @x
    /// ORDER BY b</c> against <c>(a, b)</c>) — the seek positions on <c>a = @x</c>
    /// and the trailing key columns emerge already ordered, so the sort vanishes
    /// and the scan touches only the matching group; a folded range on the first
    /// order column narrows it further (<c>WHERE a = @x AND b &gt; 5 ORDER BY b</c>).</item>
    /// </list>
    /// <para>
    /// All order directions must agree (all ASC → forward, all DESC → reversed) —
    /// a mixed-direction sort declines, since the value-ordered view can't serve
    /// it. Also declines for an ordinal / expression sort key, a nullable order
    /// column (its NULL-key rows aren't in the view), DISTINCT, a SNAPSHOT / RCSI
    /// read (the version-chain sweep can't stay ordered), a tx-scoped row-lock
    /// plan, or a competing equality / range seek on a leading key column the
    /// chosen prefix doesn't consume (the narrower seek + a small sort wins).
    /// Every matched conjunct stays in <paramref name="excluders"/> as a residual
    /// filter, so the ordered scan can only narrow the row source — never reorder
    /// or drop a row. ORDER BY elimination is the one optimization observable if
    /// wrong, so the bar to apply it is deliberately high.
    /// </para>
    /// </summary>
    private static bool TryApplyOrderedScan(
        FromSource[] sources,
        JoinSpec[] joins,
        List<OrderBySpec> orderBy,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        out FromSource[] orderedSources)
    {
        orderedSources = sources;
        if (sources.Length != 1 || joins.Length != 0 || orderBy.Count == 0)
            return false;
        var source = sources[0];
        if (source.BackingTable is not { } table || source.LateralPlan is not null)
            return false;
        if (source.HeapPlan is not { } plan || plan.RowTxScoped)
            return false;
        // A SNAPSHOT / RCSI read materializes through the version store, whose
        // chain sweep appends rows in arbitrary order — an ordered scan couldn't
        // stay ordered, so sort as before.
        if (batch.ResolveSnapshotXidForRead(table) is not null)
            return false;

        // Parse ORDER BY into a column-ordinal list under one shared direction.
        // Any ordinal / expression key, LOB column, or mixed direction declines.
        var descending = orderBy[0].Descending;
        var orderOrds = new int[orderBy.Count];
        for (var i = 0; i < orderBy.Count; i++)
        {
            var spec = orderBy[i];
            if (spec.IsOrdinal || spec.Descending != descending || spec.Expr is not { } orderExpr
                || !TryIdentifyIndexableColumn(source, orderExpr, out orderOrds[i]))
            {
                return false;
            }
        }

        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        // Columns pinned to a single stable equality value can both anchor the
        // seek prefix and drop out of the sort (they're constant within the
        // result), so strip them from the order list. The surviving order columns
        // must be NOT NULL — a NULL-key row isn't in the ordered view, so an
        // eliminated sort would lose it.
        var pins = CollectSingleValuePins(source, conjuncts);
        var effective = new List<int>(orderOrds.Length);
        foreach (var ord in orderOrds)
        {
            if (!pins.ContainsKey(ord))
                effective.Add(ord);
        }

        if (effective.Count == 0)
            return false;
        foreach (var ord in effective)
        {
            if (source.StoredSchema[ord].Nullable)
                return false;
        }

        // Pick the index / key whose leading prefix is a pinned run followed by
        // exactly the effective order columns. Largest pinned run wins (narrowest
        // seek); keys before indexes.
        if (!TryFindOrderedSeekPrefix(table, pins, [.. effective], out var fullPrefix, out var pinnedLength))
            return false;

        // Scan the conjuncts: decline if a competing seek (equality / IN / range)
        // sits on a leading key column the chosen prefix doesn't consume, and fold
        // a range on the first order column into the scan bounds. Pinned columns
        // are consumed; everything else stays residual.
        var firstOrderOrdinal = fullPrefix[pinnedLength];
        var consumed = new HashSet<int>();
        for (var i = 0; i < pinnedLength; i++)
            _ = consumed.Add(fullPrefix[i]);
        var orderColumnBounds = new RangeBoundExprs();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var eqLeft, out var eqRight))
            {
                if (IsUnconsumedLeadingSeek(source, table, eqLeft, eqRight, consumed)
                    || IsUnconsumedLeadingSeek(source, table, eqRight, eqLeft, consumed))
                {
                    return false;
                }

                continue;
            }

            if (conjunct.TryGetEqualityFamily(out var family))
            {
                // An IN-list / OR-family on the first order column lets the
                // composite equality seek pin one column past this ordered
                // prefix (a = @x AND b IN (…) ORDER BY b → a width-2 (a, b)
                // seek), narrower than scanning the whole pinned group, so
                // prefer it. A family on a leading key column is likewise a
                // competing seek.
                if (IsFamilyOnColumn(source, family, firstOrderOrdinal) || IsLeadingKeyFamily(source, table, family))
                    return false;
                continue;
            }

            if (conjunct.TryGetRangeOperands(out var rangeLeft, out var rangeOp, out var rangeRight)
                && TryNormalizeRangeBound(source, rangeLeft, rangeOp, rangeRight, out var boundOrd, out var boundOp, out var boundValue))
            {
                if (boundOrd == firstOrderOrdinal)
                    RecordBound(orderColumnBounds, boundOp, boundValue);
                else if (!consumed.Contains(boundOrd) && IsLeadingKeyColumn(table, boundOrd))
                    return false;
                continue;
            }

            if (conjunct.TryGetBetweenOperands(out var value, out var lower, out var upper)
                && TryIdentifyIndexableColumn(source, value, out var betweenOrd))
            {
                if (betweenOrd == firstOrderOrdinal && IsStableValueSide(lower, source) && IsStableValueSide(upper, source))
                {
                    RecordBound(orderColumnBounds, RangeComparison.GreaterOrEqual, lower);
                    RecordBound(orderColumnBounds, RangeComparison.LessOrEqual, upper);
                }
                else if (betweenOrd != firstOrderOrdinal && !consumed.Contains(betweenOrd) && IsLeadingKeyColumn(table, betweenOrd))
                {
                    return false;
                }
            }
        }

        // Resolve per-column promoted types and the pinned probe values: pinned
        // columns promote against their equality value (matching the equality
        // seek), order columns use their own type, and the first order column
        // additionally promotes against a folded range bound. A NULL pinned probe
        // or range bound seeks to empty; a collation / promotion failure declines.
        var commons = new SqlType[fullPrefix.Length];
        var prefixValues = new SqlValue[pinnedLength];
        for (var i = 0; i < pinnedLength; i++)
        {
            var ord = fullPrefix[i];
            switch (EvaluateBound(pins[ord], source.StoredSchema[ord].Type, batch, outerResolver, out var pinnedValue, out var pinnedCommon))
            {
                case BoundEval.Decline: return false;
                case BoundEval.Null: orderedSources = SeekedSource(source, []); return true;
                default: commons[i] = pinnedCommon; prefixValues[i] = pinnedValue; break;
            }
        }

        for (var i = pinnedLength; i < fullPrefix.Length; i++)
            commons[i] = source.StoredSchema[fullPrefix[i]].Type;

        var orderColumnType = commons[pinnedLength];
        bool hasLower = false, hasUpper = false, lowerInclusive = false, upperInclusive = false;
        SqlValue lowerValue = default, upperValue = default;
        if (orderColumnBounds.Lower is { } lowerExpr)
        {
            switch (EvaluateBound(lowerExpr, orderColumnType, batch, outerResolver, out lowerValue, out var lowerCommon))
            {
                case BoundEval.Decline: return false;
                case BoundEval.Null: orderedSources = SeekedSource(source, []); return true;
                default: commons[pinnedLength] = lowerCommon; hasLower = true; lowerInclusive = orderColumnBounds.LowerInclusive; break;
            }
        }
        if (orderColumnBounds.Upper is { } upperExpr)
        {
            switch (EvaluateBound(upperExpr, commons[pinnedLength], batch, outerResolver, out upperValue, out var upperCommon))
            {
                case BoundEval.Decline: return false;
                case BoundEval.Null: orderedSources = SeekedSource(source, []); return true;
                default: commons[pinnedLength] = hasLower ? SqlType.Promote(commons[pinnedLength], upperCommon) : upperCommon; hasUpper = true; upperInclusive = orderColumnBounds.UpperInclusive; break;
            }
        }
        if (hasLower)
            lowerValue = lowerValue.CoerceTo(commons[pinnedLength]);
        if (hasUpper)
            upperValue = upperValue.CoerceTo(commons[pinnedLength]);

        // Build the composite GetViewBetween bounds. A keyset cursor (a > @x OR
        // (a = @x AND b > @y) ORDER BY a, b) — only without a pinned prefix or a
        // same-column range fold, the other ways to bound the leading column —
        // contributes one exclusive lexicographic bound: the lower for an
        // ascending order, the upper for a descending one (OrderedSeek reverses
        // the ascending in-range list into the descending page). Otherwise the
        // pinned prefix plus the optional single-column range form the bounds.
        SqlValueKey? lowerKey, upperKey;
        bool lowerKeyInclusive, upperKeyInclusive;
        if (pinnedLength == 0 && !hasLower && !hasUpper
            && TryMatchKeyset(conjuncts, source, fullPrefix, descending, batch, outerResolver, commons, out var cursor))
        {
            IndexSeekDiagnostics.Sink?.Add($"KeysetSeek({table.Name})");
            if (descending)
                (lowerKey, lowerKeyInclusive, upperKey, upperKeyInclusive) = (null, true, cursor, false);
            else
                (lowerKey, lowerKeyInclusive, upperKey, upperKeyInclusive) = (cursor, false, null, true);
        }
        else
        {
            lowerKey = ComposeBound(prefixValues, hasLower, lowerValue);
            lowerKeyInclusive = !hasLower || lowerInclusive;
            upperKey = ComposeBound(prefixValues, hasUpper, upperValue);
            upperKeyInclusive = !hasUpper || upperInclusive;
        }

        var cache = HeapSeekCache.For(table.Heap);
        var candidates = cache.OrderedSeek(
            table.Heap, source.StoredSchema, source.LobStore, fullPrefix, commons, descending,
            lowerKey, lowerKeyInclusive, upperKey, upperKeyInclusive);

        IndexSeekDiagnostics.Sink?.Add($"OrderedScan({table.Name})");
        // The ordered scan replaces the source's own enumerable, so a
        // SERIALIZABLE reader's phantom fence has to be taken here. The
        // ordered prefix isn't a value interval the range path can express
        // (the pinned run plus an open-ended order column), so this is the
        // whole-table fallback. The empty-result returns above skip it: a NULL
        // pin or bound makes the predicate UNKNOWN for every row present and
        // future, so nothing can phantom into it.
        batch.EnsureSerializableTableLock(table, plan);
        orderedSources = SeekedSource(source, MaterializeWithLockChecks(table, batch, plan, candidates));
        return true;
    }

    // Builds a GetViewBetween bound: the pinned prefix with the bound value
    // appended (arity prefix+1) when a bound is present; the prefix alone (arity
    // prefix) when not but the prefix is non-empty — under the ragged-arity
    // comparer that sorts equal to every key sharing the prefix, selecting the
    // whole equality run; null (caller uses Min / Max) when neither prefix nor
    // bound constrains that side.
    private static SqlValueKey? ComposeBound(SqlValue[] prefix, bool hasBound, SqlValue bound)
    {
        if (hasBound)
        {
            var components = new SqlValue[prefix.Length + 1];
            Array.Copy(prefix, components, prefix.Length);
            components[prefix.Length] = bound;
            return new SqlValueKey(components);
        }

        return prefix.Length > 0 ? new SqlValueKey(prefix) : null;
    }

    // This source's columns pinned to a single stable equality value (column =
    // literal / variable / outer-ref). IN-list / OR families and the second of
    // two equalities on one column are excluded — only a single value can anchor
    // the ordered seek's prefix (a multi-value IN fans into several ordered runs
    // that would need merging, deferred). The value expression is captured, not
    // evaluated.
    private static Dictionary<int, Expression> CollectSingleValuePins(FromSource source, List<BooleanExpression> conjuncts)
    {
        var pins = new Dictionary<int, Expression>();
        foreach (var conjunct in conjuncts)
        {
            if (!conjunct.TryGetEqualityOperands(out var left, out var right))
                continue;
            if (TryIdentifyIndexableColumn(source, left, out var leftOrd) && IsStableValueSide(right, source))
                _ = pins.TryAdd(leftOrd, right);
            else if (TryIdentifyIndexableColumn(source, right, out var rightOrd) && IsStableValueSide(left, source))
                _ = pins.TryAdd(rightOrd, left);
        }

        return pins;
    }

    // The storage-ordinal sequence of every key / index, keys first. Used to
    // match an ORDER BY (after pinned-column stripping) against a leading prefix.
    private static IEnumerable<int[]> EnumerateKeyOrdinals(HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
            yield return key.StorageOrdinals;
        foreach (var index in table.Indexes)
        {
            var ordinals = new int[index.KeyColumns.Length];
            for (var i = 0; i < ordinals.Length; i++)
                ordinals[i] = index.KeyColumns[i].StorageOrdinal;
            yield return ordinals;
        }
    }

    // Finds the key / index whose leading prefix is a run of pinned columns
    // followed by exactly the effective order columns (in order). Returns that
    // full prefix (pinned ++ order) and the pinned-run length. The pinned run is
    // taken greedily from the front; the largest pinned run across all keys wins
    // (narrowest seek), keys preferred over indexes on a tie.
    private static bool TryFindOrderedSeekPrefix(
        HeapTable table, Dictionary<int, Expression> pins, int[] effective, out int[] fullPrefix, out int pinnedLength)
    {
        fullPrefix = [];
        pinnedLength = 0;
        var bestPinned = -1;
        foreach (var ordinals in EnumerateKeyOrdinals(table))
        {
            var pinned = 0;
            while (pinned < ordinals.Length && pins.ContainsKey(ordinals[pinned]))
                pinned++;
            if (pinned + effective.Length > ordinals.Length)
                continue;

            var matches = true;
            for (var i = 0; i < effective.Length; i++)
            {
                if (ordinals[pinned + i] != effective[i])
                {
                    matches = false;
                    break;
                }
            }

            if (matches && pinned > bestPinned)
            {
                bestPinned = pinned;
                pinnedLength = pinned;
                fullPrefix = ordinals[..(pinned + effective.Length)];
            }
        }

        return bestPinned >= 0;
    }

    // True when `columnSide` is a leading key column of THIS source carrying a
    // stable-value equality whose ordinal the chosen seek prefix doesn't consume
    // — a competing seek that should win over an ordered scan.
    private static bool IsUnconsumedLeadingSeek(
        FromSource source, HeapTable table, Expression columnSide, Expression valueSide, HashSet<int> consumed) =>
        TryIdentifyIndexableColumn(source, columnSide, out var ord)
        && !consumed.Contains(ord)
        && IsLeadingKeyColumn(table, ord)
        && IsStableValueSide(valueSide, source);

    // True when an IN-list / OR-equality family targets the given column ordinal
    // of THIS source with a stable value on the other side.
    private static bool IsFamilyOnColumn(FromSource source, List<(Expression Left, Expression Right)> family, int ordinal)
    {
        foreach (var (left, right) in family)
        {
            if (TryIdentifyIndexableColumn(source, left, out var leftOrd) && leftOrd == ordinal && IsStableValueSide(right, source))
                return true;
            if (TryIdentifyIndexableColumn(source, right, out var rightOrd) && rightOrd == ordinal && IsStableValueSide(left, source))
                return true;
        }

        return false;
    }

    // True when an IN-list / OR-equality family targets a leading key column of
    // THIS source — a competing seek (single-value pins are consumed as the
    // prefix, so a family ordinal is never already consumed).
    private static bool IsLeadingKeyFamily(FromSource source, HeapTable table, List<(Expression Left, Expression Right)> family)
    {
        foreach (var (left, right) in family)
        {
            if (TryIdentifyIndexableColumn(source, left, out var leftOrd) && IsLeadingKeyColumn(table, leftOrd) && IsStableValueSide(right, source))
                return true;
            if (TryIdentifyIndexableColumn(source, right, out var rightOrd) && IsLeadingKeyColumn(table, rightOrd) && IsStableValueSide(left, source))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recognizes a keyset-pagination cursor among the WHERE conjuncts: a single
    /// disjunction matching the lexicographic staircase
    /// <c>e0 &gt;op v0 OR (e0 = v0 AND e1 &gt;op v1) OR …</c> over a leading run of
    /// the order columns <paramref name="orderColumns"/>, where <c>&gt;op</c> is
    /// <c>&gt;</c> for an ascending order and <c>&lt;</c> for a descending one.
    /// Returns the composite cursor tuple <c>(v0, …)</c> coerced to the per-column
    /// promoted types (written into <paramref name="commons"/> for those columns),
    /// so the ordered seek positions just past it. The matched OR stays in the
    /// residual WHERE, so the bound is only an accelerator; recognition therefore
    /// reconciles every term's value for a column (equality and strict factors
    /// alike) to one agreed value and bails on any mismatch / NULL / non-stable
    /// operand, guaranteeing the staircase is exactly <c>(e…) &gt;op (v…)</c> with
    /// no row excluded that the predicate would keep.
    /// </summary>
    private static bool TryMatchKeyset(
        List<BooleanExpression> conjuncts, FromSource source, int[] orderColumns, bool descending,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, SqlType[] commons, out SqlValueKey cursor)
    {
        cursor = default;
        foreach (var conjunct in conjuncts)
        {
            var terms = new List<BooleanExpression>();
            conjunct.CollectDisjuncts(terms);

            // A single term is just a range / equality (handled elsewhere); the
            // staircase needs one term per cursor component, so ≥ 2.
            if (terms.Count >= 2
                && TryBuildKeysetCursor(terms, source, orderColumns, descending, batch, outerResolver, commons, out cursor))
            {
                return true;
            }
        }

        return false;
    }

    // Validates the staircase structure of one disjunction's terms and, if it
    // holds over the leading order columns, evaluates + reconciles the cursor
    // components. Term at depth j must be exactly (e0 = v0 AND … AND e_{j-1} =
    // v_{j-1} AND e_j >op v_j); depths 0..k-1 must each appear once.
    private static bool TryBuildKeysetCursor(
        List<BooleanExpression> terms, FromSource source, int[] orderColumns, bool descending,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, SqlType[] commons, out SqlValueKey cursor)
    {
        cursor = default;
        var depth = terms.Count;
        if (depth > orderColumns.Length)
            return false;

        var valueExprs = new List<Expression>[depth];
        for (var i = 0; i < depth; i++)
            valueExprs[i] = [];
        var coveredDepth = new bool[depth];
        var wantStrict = descending ? RangeComparison.Less : RangeComparison.Greater;

        foreach (var term in terms)
        {
            var factors = new List<BooleanExpression>();
            term.CollectConjuncts(factors);

            var equalities = new Dictionary<int, Expression>();
            var strictOrdinal = -1;
            Expression? strictValue = null;
            var strictCount = 0;
            foreach (var factor in factors)
            {
                if (factor.TryGetRangeOperands(out var rangeLeft, out var rangeOp, out var rangeRight))
                {
                    if (!TryNormalizeRangeBound(source, rangeLeft, rangeOp, rangeRight, out var ord, out var op, out var value) || op != wantStrict)
                        return false;
                    (strictOrdinal, strictValue) = (ord, value);
                    strictCount++;
                }
                else if (factor.TryGetEqualityOperands(out var eqLeft, out var eqRight))
                {
                    if (TryIdentifyIndexableColumn(source, eqLeft, out var leftOrd) && IsStableValueSide(eqRight, source))
                        equalities[leftOrd] = eqRight;
                    else if (TryIdentifyIndexableColumn(source, eqRight, out var rightOrd) && IsStableValueSide(eqLeft, source))
                        equalities[rightOrd] = eqLeft;
                    else
                        return false;
                }
                else
                {
                    return false;
                }
            }

            var j = equalities.Count;
            if (strictCount != 1 || j >= depth || orderColumns[j] != strictOrdinal || coveredDepth[j])
                return false;

            // The j equality factors must be exactly e0..e_{j-1}; finding all of
            // them among `equalities` (which has exactly j entries) proves the set.
            for (var i = 0; i < j; i++)
            {
                if (!equalities.TryGetValue(orderColumns[i], out var equalityValue))
                    return false;
                valueExprs[i].Add(equalityValue);
            }

            valueExprs[j].Add(strictValue!);
            coveredDepth[j] = true;
        }

        for (var i = 0; i < depth; i++)
        {
            if (!coveredDepth[i])
                return false;
        }

        var components = new SqlValue[depth];
        for (var i = 0; i < depth; i++)
        {
            if (!TryReconcileCursorComponent(source, orderColumns[i], valueExprs[i], batch, outerResolver, out commons[i], out components[i]))
                return false;
        }

        cursor = new SqlValueKey(components);
        return true;
    }

    // Evaluates every value expression pinned to one cursor column, promotes them
    // to a single common type, and requires they all agree (a clean keyset uses
    // the same value for a column's equality and strict factors). Declines on a
    // NULL / non-promotable / disagreeing value — the residual OR then filters the
    // unaccelerated scan.
    private static bool TryReconcileCursorComponent(
        FromSource source, int ordinal, List<Expression> valueExprs,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, out SqlType common, out SqlValue value)
    {
        var columnType = source.StoredSchema[ordinal].Type;
        common = columnType;
        value = default;

        var raw = new List<SqlValue>(valueExprs.Count);
        foreach (var expr in valueExprs)
        {
            if (EvaluateBound(expr, columnType, batch, outerResolver, out var evaluated, out var evaluatedCommon) != BoundEval.Value)
                return false;
            try
            {
                common = SqlType.Promote(common, evaluatedCommon);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                return false;
            }

            raw.Add(evaluated);
        }

        var have = false;
        foreach (var v in raw)
        {
            SqlValue coerced;
            try
            {
                coerced = v.CoerceTo(common);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                return false;
            }

            if (!have)
            {
                (value, have) = (coerced, true);
            }
            else if (!value.Equals(coerced))
            {
                return false;
            }
        }

        return have;
    }

    private static bool IsLeadingKeyColumn(HeapTable table, int storageOrdinal)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.StorageOrdinals.Length > 0 && key.StorageOrdinals[0] == storageOrdinal)
                return true;
        }
        foreach (var index in table.Indexes)
        {
            if (index.KeyColumns.Length > 0 && index.KeyColumns[0].StorageOrdinal == storageOrdinal)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Pushes single-source WHERE equality / range predicates (<c>col = literal
    /// / variable</c>, <c>col &gt; literal</c>, …) down onto <b>every</b>
    /// base-table FROM source of a multi-source query, seeking each before the
    /// join runs, and hands the result to
    /// <see cref="ReorderToDriveFromNarrowedSource"/> when the narrowing landed
    /// somewhere the written order doesn't drive from.
    /// <para>
    /// Narrowing one source is semantics-preserving for every join kind because
    /// the matched conjuncts <b>stay</b> in the residual WHERE: a NULL-extended
    /// tuple an outer join would emit because the narrowed side lost its match
    /// is excluded by the very conjunct that justified the narrowing (a column
    /// of a NULL-filled slot reads as NULL, so the conjunct is UNKNOWN) — the
    /// same conjunct that excluded the matched-but-failing tuple before.
    /// </para>
    /// <para>
    /// The whole FROM is handed to the stability test (<c>planSources</c>) so a
    /// probe value naming a column is classified rather than refused outright: a
    /// <b>sibling</b> source's column declines (it isn't resolvable pre-join),
    /// while one escaping to the enclosing scope anchors the seek like a
    /// variable — it is fixed for this execution, since a correlated plan
    /// re-executes per enclosing row. That is what seeks the inner side of a
    /// correlated subquery whose own FROM is a join, instead of hash-building it
    /// per outer row. Shrinking a driving rowset also lets
    /// <see cref="EquiJoinSeekOrHash"/> seek the inner per outer row for the
    /// common filter-then-join shape; a narrowed source that stays on the inner
    /// side becomes a small hash build instead.
    /// </para>
    /// <para>
    /// A source carrying a SERIALIZABLE / <c>HOLDLOCK</c> phantom fence is left
    /// alone past the leftmost slot: the fence is settled inside the seek
    /// attempt, so probing every source would change which key ranges a
    /// SERIALIZABLE reader locks and when. The leftmost slot keeps its
    /// long-standing unconditional attempt.
    /// </para>
    /// <para>
    /// A source no key or index lets the seek narrow falls back to
    /// <see cref="TryPrefilterJoinSource"/>, which filters its row stream by the
    /// same conjuncts rather than seeking on them — the access path a range on an
    /// unindexed column gets, where the predicate can still shrink the join's
    /// input even though nothing can position on it.
    /// </para>
    /// </summary>
    private static (FromSource[] Sources, JoinSpec[] Joins) NarrowJoinSources(
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (sources.Length < 2 || excluders.Count == 0)
            return (sources, joins);

        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        FromSource[]? narrowed = null;
        int[]? seekedCandidates = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (i > 0 && !IsSeekNarrowingTarget(sources[i]))
                continue;
            var seeked = MaybeApplyIndexSeek(
                [sources[i]], NoJoins, excluders, batch, outerResolver, planSources: sources, out var candidates);
            if (ReferenceEquals(seeked[0], sources[i]))
            {
                if (TryPrefilterJoinSource(sources[i], conjuncts, sources, batch, outerResolver) is { } prefiltered)
                {
                    narrowed ??= (FromSource[])sources.Clone();
                    narrowed[i] = prefiltered;
                }

                continue;
            }

            narrowed ??= (FromSource[])sources.Clone();
            if (seekedCandidates is null)
            {
                seekedCandidates = new int[sources.Length];
                Array.Fill(seekedCandidates, -1);
            }

            narrowed[i] = seeked[0];
            seekedCandidates[i] = candidates;
        }

        if (narrowed is null)
            return (sources, joins);

        // The reorder picks its driver by seeked candidate count, which a
        // prefiltered source doesn't have (its stream is lazy and counting it
        // would materialize the table). With nothing seeked, the written order
        // stands and the prefilter alone does the narrowing.
        return seekedCandidates is null
            ? (narrowed, joins)
            : ReorderToDriveFromNarrowedSource(narrowed, joins, seekedCandidates) ?? (narrowed, joins);
    }

    /// <summary>
    /// The pushdown above, restricted to a joined UPDATE / DELETE's
    /// <b>non-target</b> sources — the read side of a mutation, where a seek is
    /// the same pure narrowing it is in a SELECT because the statement re-runs
    /// its whole WHERE per join tuple, so a matched conjunct is still the filter
    /// it was. See <see cref="PrepareMutationJoinSources"/> for why the target
    /// slot and the reorder stay out.
    /// <para>
    /// Every source is gated by <see cref="IsSeekNarrowingTarget"/>, the
    /// leftmost included — the read path's unconditional leftmost attempt is a
    /// long-standing behavior of that path, and extending it to a mutation would
    /// change which key ranges a SERIALIZABLE reader locks around a write. The
    /// mutation's WHERE arrives as the single bound predicate the DML parser
    /// produced rather than a conjunct list; the seek splits it itself.
    /// </para>
    /// </summary>
    private static FromSource[] NarrowMutationJoinSources(
        FromSource[] sources, BooleanExpression where, int targetIndex, BatchContext batch)
    {
        if (sources.Length < 2)
            return sources;

        List<BooleanExpression> excluders = [where];
        FromSource[]? narrowed = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (i == targetIndex || !IsSeekNarrowingTarget(sources[i]))
                continue;
            var seeked = MaybeApplyIndexSeek(
                [sources[i]], NoJoins, excluders, batch, outerResolver: null, planSources: sources);
            if (ReferenceEquals(seeked[0], sources[i]))
                continue;
            narrowed ??= (FromSource[])sources.Clone();
            narrowed[i] = seeked[0];
        }

        return narrowed ?? sources;
    }

    /// <summary>
    /// Whether a non-leftmost source is eligible for the WHERE pushdown: a plain
    /// base-table scan (a deferred / generated source has no heap to seek) whose
    /// lock plan owes no SERIALIZABLE phantom fence and holds no tx-scoped row
    /// lock (where the whole-table scan's locking is load-bearing and the seek
    /// would decline anyway).
    /// </summary>
    private static bool IsSeekNarrowingTarget(FromSource source) =>
        source.BackingTable is not null
        && source.LateralPlan is null
        && source.HeapPlan is { SerializableRangeMode: null, RowTxScoped: false };

    /// <summary>
    /// Reorders a <b>pure INNER equi-join chain</b> so it drives from the source
    /// the WHERE narrowed hardest, instead of whichever source the query happens
    /// to name first. Returns <c>null</c> — leaving the written order — for
    /// anything outside that shape.
    /// <para>
    /// INNER joins commute and their ON conjuncts are WHERE-equivalent, so the
    /// conjunction of every ON conjunct applied over the cross product is the
    /// result whatever order the sources fold in: any permutation that keeps
    /// each conjunct's two sources both placed by the step it attaches to
    /// produces the same rows. Row <em>order</em> can change, which is legal
    /// without an ORDER BY. Column resolution is name-based and rejects an
    /// ambiguous unqualified name outright (Msg 209), so it is order-independent
    /// too — the duplicate-qualifier guard below covers the one case where it
    /// wouldn't be.
    /// </para>
    /// <para>
    /// The reorder engages only when the best driver is a <em>non-leftmost</em>
    /// narrowed source seeking at most <see cref="SeekOuterRowCap"/> rows — the
    /// regime where <see cref="EquiJoinSeekOrHash"/> keeps seeking the next link
    /// per outer row, which is what collapses a deep chain filtered in the
    /// middle. A wider narrowing leaves the written order alone rather than
    /// trading a small outer's per-outer seeks for a large one's hash probes.
    /// </para>
    /// <para>
    /// Placement is greedy from the driver: at each step the sources connected
    /// to the placed set by an ON equi-conjunct are the candidates, and a
    /// candidate whose connecting columns cover one of its own unique keys wins
    /// (that join can't multiply the driving set, so the outer stays inside the
    /// seek cap for the next link); ties break on the written order. A
    /// disconnected join graph declines entirely.
    /// </para>
    /// </summary>
    private static (FromSource[] Sources, JoinSpec[] Joins)? ReorderToDriveFromNarrowedSource(
        FromSource[] sources, JoinSpec[] joins, int[] seekedCandidates)
    {
        var driver = -1;
        for (var i = 1; i < sources.Length; i++)
        {
            if (seekedCandidates[i] is < 0 or > SeekOuterRowCap)
                continue;
            if (driver < 0 || seekedCandidates[i] < seekedCandidates[driver])
                driver = i;
        }

        // Nothing narrowed past the leftmost slot, or the leftmost narrowed at
        // least as hard and already drives.
        if (driver < 0 || (seekedCandidates[0] >= 0 && seekedCandidates[0] <= seekedCandidates[driver]))
            return null;

        foreach (var join in joins)
        {
            if (join.Kind != JoinKind.Inner || join.GroupCount != 1 || join.OnPredicate is null)
                return null;
        }

        for (var i = 0; i < sources.Length; i++)
        {
            // A deferred plan's rows are produced per left-side row, so moving
            // it would change how often it runs; a placeholder belongs to a
            // skipped statement. Two sources sharing an exposed name would make
            // a qualified reference bind to whichever comes first.
            if (sources[i].LateralPlan is not null || sources[i].IsPlaceholder || sources[i].Qualifier is null)
                return null;
            for (var j = i + 1; j < sources.Length; j++)
            {
                if (BuiltInToken.Equals(sources[i].Qualifier, sources[j].Qualifier))
                    return null;
            }
        }

        var edges = new List<JoinEdge>();
        var conjuncts = new List<BooleanExpression>();
        foreach (var join in joins)
        {
            conjuncts.Clear();
            join.OnPredicate!.CollectConjuncts(conjuncts);
            foreach (var conjunct in conjuncts)
            {
                if (!TryExtractEquiEdge(conjunct, sources, out var edge))
                    return null;
                edges.Add(edge);
            }
        }

        var count = sources.Length;
        var order = new int[count];
        var placedAt = new int[count];
        Array.Fill(placedAt, -1);
        order[0] = driver;
        placedAt[driver] = 0;
        for (var step = 1; step < count; step++)
        {
            var best = -1;
            var bestPreservesRows = false;
            for (var candidate = 0; candidate < count; candidate++)
            {
                if (placedAt[candidate] >= 0 || !ConnectsToPlacedSources(edges, placedAt, candidate))
                    continue;
                var preservesRows = JoinPreservesRowCount(sources, edges, placedAt, candidate);
                if (best < 0 || (preservesRows && !bestPreservesRows))
                    (best, bestPreservesRows) = (candidate, preservesRows);
            }

            if (best < 0)
                return null;
            order[step] = best;
            placedAt[best] = step;
        }

        // Each conjunct attaches at the step that places the later of its two
        // sources — which is the step that first makes both readable, whether
        // that step's own source is one of them or the pair was completed
        // earlier in the written order.
        var stepPredicates = new BooleanExpression?[count];
        foreach (var edge in edges)
        {
            var step = Math.Max(placedAt[edge.LeftSource], placedAt[edge.RightSource]);
            stepPredicates[step] = stepPredicates[step] is { } existing
                ? BooleanExpression.And(existing, edge.Conjunct)
                : edge.Conjunct;
        }

        var reorderedSources = new FromSource[count];
        var reorderedJoins = new JoinSpec[count - 1];
        reorderedSources[0] = sources[order[0]];
        for (var step = 1; step < count; step++)
        {
            if (stepPredicates[step] is not { } on)
                return null;
            reorderedSources[step] = sources[order[step]];
            reorderedJoins[step - 1] = new JoinSpec(JoinKind.Inner, on);
        }

        JoinDiagnostics.Sink?.Add($"Reorder({string.Join(",", order)})");
        return (reorderedSources, reorderedJoins);
    }

    /// <summary>
    /// One ON equi-conjunct read as an undirected edge of the join graph: the
    /// two <b>distinct</b> FROM sources it equates and the bare column reference
    /// it reads from each. The conjunct rides along so the reorder can re-attach
    /// it to whichever step completes the pair.
    /// </summary>
    private sealed class JoinEdge(
        int leftSource, Reference leftColumn, int rightSource, Reference rightColumn, BooleanExpression conjunct)
    {
        public readonly int LeftSource = leftSource;
        public readonly Reference LeftColumn = leftColumn;
        public readonly int RightSource = rightSource;
        public readonly Reference RightColumn = rightColumn;
        public readonly BooleanExpression Conjunct = conjunct;
    }

    /// <summary>
    /// Recognizes an ON conjunct of the form <c>sourceA.col = sourceB.col</c>
    /// between two different FROM sources — the level-independent counterpart of
    /// <see cref="TryExtractEquiKey"/>, which classifies relative to one join
    /// level. Anything else (a single-source filter, a non-equality, a
    /// computed operand, a key pair the runtime <c>=</c> couldn't promote)
    /// declines, which declines the whole reorder.
    /// </summary>
    private static bool TryExtractEquiEdge(BooleanExpression conjunct, FromSource[] sources, [NotNullWhen(true)] out JoinEdge? edge)
    {
        edge = null;
        if (!conjunct.TryGetEqualityOperands(out var a, out var b)
            || a is not Reference refA || b is not Reference refB)
        {
            return false;
        }

        var (sourceA, _) = FindSourceColumn(sources, refA.ReferencedName);
        var (sourceB, _) = FindSourceColumn(sources, refB.ReferencedName);
        if (sourceA < 0 || sourceB < 0 || sourceA == sourceB || !TryPromoteEquiKeyTypes(sources, refA, refB, out _))
            return false;

        edge = new JoinEdge(sourceA, refA, sourceB, refB, conjunct);
        return true;
    }

    private static bool ConnectsToPlacedSources(List<JoinEdge> edges, int[] placedAt, int candidate)
    {
        foreach (var edge in edges)
        {
            if ((edge.LeftSource == candidate && placedAt[edge.RightSource] >= 0)
                || (edge.RightSource == candidate && placedAt[edge.LeftSource] >= 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether joining <paramref name="candidate"/> to the already-placed
    /// sources can only match each driving row at most once — true when the
    /// candidate's own columns in the connecting equi-conjuncts cover every key
    /// column of one of its unique keys (a PRIMARY KEY / UNIQUE constraint, or
    /// an enabled unfiltered unique index). Such a step leaves the driving row
    /// count unchanged, which is what keeps a deep chain inside
    /// <see cref="SeekOuterRowCap"/> long enough to seek every link.
    /// </summary>
    private static bool JoinPreservesRowCount(FromSource[] sources, List<JoinEdge> edges, int[] placedAt, int candidate)
    {
        var source = sources[candidate];
        if (source.BackingTable is not { } table)
            return false;

        var ordinals = new HashSet<int>();
        foreach (var edge in edges)
        {
            var own = edge.LeftSource == candidate && placedAt[edge.RightSource] >= 0 ? edge.LeftColumn
                : edge.RightSource == candidate && placedAt[edge.LeftSource] >= 0 ? edge.RightColumn
                : null;
            if (own is not null && TryIdentifyIndexableColumn(source, own, out var ordinal))
                _ = ordinals.Add(ordinal);
        }

        if (ordinals.Count == 0)
            return false;
        foreach (var key in table.KeyConstraints)
        {
            if (KeyColumnsCovered(key.StorageOrdinals, ordinals))
                return true;
        }

        foreach (var index in table.Indexes)
        {
            if (index.IsUnique && !index.IsDisabled && index.Filter is null && KeyColumnsCovered(index.KeyStorageOrdinals, ordinals))
                return true;
        }

        return false;
    }

    private static bool KeyColumnsCovered(int[] keyOrdinals, HashSet<int> available)
    {
        if (keyOrdinals.Length == 0)
            return false;
        foreach (var ordinal in keyOrdinals)
        {
            if (!available.Contains(ordinal))
                return false;
        }

        return true;
    }

    // Records `column = stableValue` for an indexable, non-LOB column of THIS
    // source. No evaluation happens here — only the value-side expression is
    // captured; it's run lazily (and once) when a prefix actually selects it.
    private static bool TryRecordColumnEquality(
        FromSource source, Expression columnSide, Expression valueSide, Dictionary<int, Expression[]> equalities,
        bool allowCorrelatedColumnValue, FromSource[]? planSources = null)
        => TryIdentifyIndexableColumn(source, columnSide, out var storageOrdinal)
            && IsStableValueSide(valueSide, source, allowCorrelatedColumnValue, planSources)
            && equalities.TryAdd(storageOrdinal, [valueSide]);

    // Records `column IN (v1, v2, ...)` (or the equivalent OR-of-equalities)
    // for one indexable, non-LOB column of THIS source — every pair in the
    // family must put the column on one side and a stable value on the other,
    // and they must all agree on the column. Order within the family is
    // preserved; duplicates aren't deduplicated (a row matches at most one
    // probe per column anyway, so duplicate probes just waste a hash lookup).
    private static bool TryRecordEqualityFamily(
        FromSource source,
        List<(Expression Left, Expression Right)> family,
        Dictionary<int, Expression[]> equalities,
        bool allowCorrelatedColumnValue,
        FromSource[]? planSources = null)
    {
        if (family.Count == 0)
            return false;

        int? targetStorageOrdinal = null;
        var values = new Expression[family.Count];
        for (var i = 0; i < family.Count; i++)
        {
            var (left, right) = family[i];
            if (!TryExtractColumnAndValue(source, left, right, allowCorrelatedColumnValue, planSources, out var ord, out var value)
                && !TryExtractColumnAndValue(source, right, left, allowCorrelatedColumnValue, planSources, out ord, out value))
            {
                return false;
            }
            if (targetStorageOrdinal is { } existing && existing != ord)
                return false;
            targetStorageOrdinal = ord;
            values[i] = value;
        }

        return equalities.TryAdd(targetStorageOrdinal!.Value, values);
    }

    private static bool TryExtractColumnAndValue(
        FromSource source,
        Expression columnSide,
        Expression valueSide,
        bool allowCorrelatedColumnValue,
        FromSource[]? planSources,
        out int storageOrdinal,
        [NotNullWhen(true)] out Expression? value)
    {
        if (TryIdentifyIndexableColumn(source, columnSide, out storageOrdinal)
            && IsStableValueSide(valueSide, source, allowCorrelatedColumnValue, planSources))
        {
            value = valueSide;
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryIdentifyIndexableColumn(FromSource source, Expression candidate, out int storageOrdinal)
    {
        storageOrdinal = -1;
        if (candidate is not Reference columnRef)
            return false;
        var (columnSource, columnIndex) = FindSourceColumn([source], columnRef.ReferencedName);
        if (columnSource != 0)
            return false;
        var ord = source.StorageOrdinals is { } ordinals ? ordinals[columnIndex] : columnIndex;
        if (ord < 0 || source.StoredSchema[ord].Type.IsLob)
            return false;
        storageOrdinal = ord;
        return true;
    }

    // Picks the index / key whose leading key-column prefix is the longest run
    // of equality columns with usable (non-NULL, collation-compatible, cleanly
    // promoting) probe values, and seeks on that whole prefix. Probe components
    // are evaluated at most once per column via the local memo. When a column
    // is bound to an IN-list / OR-equality family, every probe expands across
    // the cartesian product of selected columns — `a IN (1,2) AND b = 3` fires
    // two probes against the (a,b) composite cache, `a IN (1,2) AND b IN (3,4)`
    // fires four. A single per-column NULL is skipped silently (never equal
    // under <c>=</c>); if EVERY probe in a column collapses to NULL the column
    // declines and the prefix stops there.
    private static bool TrySeekByLongestPrefix(
        FromSource source,
        HeapTable table,
        DataLockPlan plan,
        BatchContext batch,
        long? snapshotXid,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, Expression[]> equalities,
        Dictionary<int, RangeBoundExprs> bounds,
        out IEnumerable<byte[]> seekRows,
        out int width,
        out bool rangeExtended,
        out int candidateCount)
    {
        if (!TryComputeEqualityCandidates(source, table, batch, outerResolver, equalities, bounds, out var candidates, out width, out rangeExtended))
        {
            seekRows = [];
            candidateCount = -1;
            return false;
        }

        candidateCount = candidates.Count;
        seekRows = snapshotXid is { } sx
            ? MaterializeSnapshotCandidates(table, batch, sx, candidates)
            : MaterializeWithLockChecks(table, batch, plan, candidates);
        return true;
    }

    // Computes the seek-narrowed (page, slot) candidate addresses for the longest
    // usable equality prefix across this table's keys / indexes — the address-only
    // core shared by the query path (TrySeekByLongestPrefix wraps it in the lock /
    // snapshot read materializer) and the mutation path (which materializes them as
    // live rewrite targets). Probe components evaluate at most once per column; see
    // TrySeekByLongestPrefix's doc for the prefix / cartesian rules. Returns false
    // (prefix length 0) when no column carries a usable probe.
    //
    // A stable range bound on the key column immediately after the equality
    // prefix EXTENDS the seek predicate one column further (`a = 1 AND b > 5`
    // against (a, b) seeks the (a, 5..] slice of the ordered view instead of
    // dragging a's whole bucket through the residual filter) — mirroring a real
    // index seek's predicate, which is an equality prefix plus at most one range
    // column, everything deeper residual. The longest equality prefix still
    // wins the index choice; a range continuation only breaks ties. A bound
    // that fails to evaluate falls back to the pure equality seek; a NULL bound
    // seeks to empty (the range conjunct is UNKNOWN for every row, and it stays
    // in the residual WHERE).
    private static bool TryComputeEqualityCandidates(
        FromSource source,
        HeapTable table,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        Dictionary<int, Expression[]> equalities,
        Dictionary<int, RangeBoundExprs> bounds,
        out List<(int Page, int Slot)> candidates,
        out int width,
        out bool rangeExtended)
    {
        candidates = [];
        width = 0;
        rangeExtended = false;

        var resolved = new Dictionary<int, (SqlType Common, SqlValue[] Probes)?>();

        var bestLen = 0;
        var bestContinues = false;
        int[]? bestKeyOrdinals = null;
        Storage.Index? bestIndex = null;

        foreach (var key in table.KeyConstraints)
        {
            var ordinals = key.StorageOrdinals;
            var len = 0;
            while (len < ordinals.Length && ResolveComponent(ordinals[len]) is not null)
                len++;
            var continues = len > 0 && len < ordinals.Length && bounds.ContainsKey(ordinals[len]);
            if (len > bestLen || (len == bestLen && len > 0 && continues && !bestContinues))
            {
                bestLen = len;
                bestContinues = continues;
                bestKeyOrdinals = ordinals;
                bestIndex = null;
            }
        }

        foreach (var index in table.Indexes)
        {
            var keyColumns = index.KeyColumns;
            var len = 0;
            while (len < keyColumns.Length && ResolveComponent(keyColumns[len].StorageOrdinal) is not null)
                len++;
            var continues = len > 0 && len < keyColumns.Length && bounds.ContainsKey(keyColumns[len].StorageOrdinal);
            if (len > bestLen || (len == bestLen && len > 0 && continues && !bestContinues))
            {
                bestLen = len;
                bestContinues = continues;
                bestIndex = index;
                bestKeyOrdinals = null;
            }
        }

        if (bestLen == 0)
            return false;

        var prefix = new int[bestLen];
        var commons = new SqlType[bestLen];
        var probesPerColumn = new SqlValue[bestLen][];
        for (var i = 0; i < bestLen; i++)
        {
            prefix[i] = bestKeyOrdinals is { } ko ? ko[i] : bestIndex!.KeyColumns[i].StorageOrdinal;
            var (common, probes) = resolved[prefix[i]]!.Value;
            commons[i] = common;
            probesPerColumn[i] = probes;
        }

        var cache = HeapSeekCache.For(table.Heap);
        if (bestContinues)
        {
            var rangeOrdinal = bestKeyOrdinals is { } ko ? ko[bestLen] : bestIndex!.KeyColumns[bestLen].StorageOrdinal;
            var bound = bounds[rangeOrdinal];
            switch (EvaluateRangeBounds(bound, source.StoredSchema[rangeOrdinal].Type, batch, outerResolver,
                out var rangeCommon, out var hasLower, out var lowerValue, out var hasUpper, out var upperValue))
            {
                case BoundEval.Null:
                    // The range conjunct is UNKNOWN for every row — a valid
                    // empty seek, narrower than probing the equality prefix.
                    width = bestLen;
                    rangeExtended = true;
                    return true;
                case BoundEval.Value:
                    var extendedPrefix = new int[bestLen + 1];
                    Array.Copy(prefix, extendedPrefix, bestLen);
                    extendedPrefix[bestLen] = rangeOrdinal;
                    var extendedCommons = new SqlType[bestLen + 1];
                    Array.Copy(commons, extendedCommons, bestLen);
                    extendedCommons[bestLen] = rangeCommon;
                    foreach (var tuple in CartesianProduct(probesPerColumn))
                    {
                        candidates.AddRange(cache.PrefixRangeSeek(
                            table.Heap, source.StoredSchema, source.LobStore, extendedPrefix, extendedCommons,
                            new SqlValueKey(tuple),
                            ComposeBound(tuple, hasLower, lowerValue), !hasLower || bound.LowerInclusive,
                            ComposeBound(tuple, hasUpper, upperValue), !hasUpper || bound.UpperInclusive));
                    }

                    width = bestLen;
                    rangeExtended = true;
                    return true;
            }

            // BoundEval.Decline: the bound can't anchor a seek — fall through
            // to the pure equality seek on the same prefix.
        }

        foreach (var tuple in CartesianProduct(probesPerColumn))
        {
            var bucket = cache.Seek(table.Heap, source.StoredSchema, source.LobStore, prefix, commons, new SqlValueKey(tuple));
            if (bucket.Length != 0)
                candidates.AddRange(bucket);
        }

        width = bestLen;
        return true;

        // Resolves (and memoizes) the probe components for one column, but only
        // for columns that carry a stable-value equality conjunct (or IN-list /
        // OR family). Others can't anchor a seek and report as unusable,
        // bounding the prefix there.
        (SqlType Common, SqlValue[] Probes)? ResolveComponent(int storageOrdinal)
        {
            if (!equalities.TryGetValue(storageOrdinal, out var valueSides))
                return null;
            if (resolved.TryGetValue(storageOrdinal, out var cached))
                return cached;
            var component = EvaluateProbeComponent(source, storageOrdinal, valueSides, batch, outerResolver);
            resolved[storageOrdinal] = component;
            return component;
        }
    }

    // Yields every cartesian-product tuple across the per-column probe arrays.
    // For an N-column prefix where the i-th column has c_i probe values, the
    // total tuple count is the product of the c_i; for the common single-value
    // case (all c_i == 1) this yields exactly one tuple.
    private static IEnumerable<SqlValue[]> CartesianProduct(SqlValue[][] perColumn)
    {
        var indices = new int[perColumn.Length];
        while (true)
        {
            var tuple = new SqlValue[perColumn.Length];
            for (var i = 0; i < perColumn.Length; i++)
                tuple[i] = perColumn[i][indices[i]];
            yield return tuple;

            var carry = perColumn.Length - 1;
            while (carry >= 0)
            {
                indices[carry]++;
                if (indices[carry] < perColumn[carry].Length)
                    break;
                indices[carry] = 0;
                carry--;
            }
            if (carry < 0)
                yield break;
        }
    }

    // The most probe tuples a union seek fires across all its disjuncts before
    // declining. A wider OR is a different shape from the one the union serves
    // (a handful of point predicates across a few columns), and the scan it
    // falls back to is one pass rather than dozens of bucket lookups plus a
    // dedup set. The count is an upper bound taken structurally — the product
    // of a disjunct's per-column probe counts over EVERY column it records,
    // rather than only the ones the chosen prefix ends up using — so the cap is
    // settled before any probe evaluates.
    private const int UnionSeekProbeCap = 64;

    /// <summary>
    /// Narrows a single-base-table scan to a <b>union of seeks</b> when a
    /// top-level AND-conjunct is an <c>OR</c> whose every disjunct seeks on its
    /// own: the cross-column disjunction (<c>WHERE a = 1 OR b = 2</c>) a
    /// single-column equality family can't express, and which otherwise
    /// full-scans however well indexed both columns are. Each disjunct fires its
    /// own probe set through the same per-<c>Heap</c> cache a lone equality
    /// would, and the candidates union by <b>row address</b> — the (page, slot)
    /// pair every seek path already carries — so a row several disjuncts match
    /// is read (and locked) once. Real unions two index seeks and dedupes the
    /// same way.
    /// <para>
    /// Semantics are exact by construction: the whole original WHERE, the OR
    /// included, stays in <c>excluders</c> as the residual filter, and each
    /// disjunct's probe set selects a <b>superset</b> of the rows that disjunct
    /// can match — so their union is a superset of the OR's match set, and the
    /// residual drops the rest. NULLs need no special handling: a NULL probe
    /// value is skipped as it is anywhere else, and a row whose columns are all
    /// NULL matches no probe and reads UNKNOWN in the residual.
    /// </para>
    /// <para>
    /// Declines, silently, to whatever the caller does next (a scan, or the
    /// range seek): any disjunct with no stable-value equality on a seekable
    /// non-LOB column of THIS source — an expression-wrapped column, a
    /// non-indexed one, a bare range or <c>NOT</c> / <c>IS NULL</c> disjunct, a
    /// column of another source, a sibling-referencing value side — or a total
    /// probe count over <see cref="UnionSeekProbeCap"/>. A disjunct that is
    /// itself an <c>AND</c> group is <em>not</em> a decline: its conjuncts
    /// collect exactly as a WHERE's do, so the group seeks on whatever prefix
    /// they cover and the rest of the group stays residual like everything else.
    /// </para>
    /// <para>
    /// The claim is settled <b>structurally</b> and takes the first eligible OR
    /// conjunct in written order, so which conjunct anchors the union can't ride
    /// on runtime values (a correlated inner re-planned per outer row keeps one
    /// access path). If that conjunct's probes then fail to anchor — a NULL or
    /// cross-collation value side collapsing some disjunct's prefix — the read
    /// scans rather than passing the claim on: a declined <em>probe</em> doesn't
    /// mean the disjunct matches nothing, so its contribution can't be treated
    /// as empty.
    /// </para>
    /// </summary>
    private static bool TryComputeUnionCandidates(
        FromSource source,
        HeapTable table,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        List<BooleanExpression> conjuncts,
        bool allowCorrelatedColumnValue,
        FromSource[]? planSources,
        [NotNullWhen(true)] out List<(int Page, int Slot)>? candidates,
        out int disjunctCount)
    {
        candidates = null;
        disjunctCount = 0;

        var disjuncts = new List<BooleanExpression>();
        foreach (var conjunct in conjuncts)
        {
            disjuncts.Clear();
            conjunct.CollectDisjuncts(disjuncts);
            if (disjuncts.Count < 2
                || IsSingleColumnEqualityFamily(source, conjunct, allowCorrelatedColumnValue, planSources)
                || !TryPlanUnionDisjuncts(source, table, disjuncts, allowCorrelatedColumnValue, planSources, out var planned))
            {
                continue;
            }

            disjunctCount = disjuncts.Count;
            return TrySeekUnionDisjuncts(source, table, batch, outerResolver, planned, out candidates);
        }

        return false;
    }

    // True when this OR is the same-column equality family the IN-list path
    // already claims (`a = 1 OR a = 2`, which CollectColumnEqualities records as
    // one multi-value equality). It is the whole of the exclusivity between the
    // two claim sites, and it is structural — so the answer doesn't ride on
    // which seek the read actually took. A family spanning two columns
    // (`a = 1 OR a = 2 OR b = 3`) is not one the IN path can record, and stays
    // the union's.
    private static bool IsSingleColumnEqualityFamily(
        FromSource source, BooleanExpression conjunct, bool allowCorrelatedColumnValue, FromSource[]? planSources)
        => conjunct.TryGetEqualityFamily(out var family)
            && TryRecordEqualityFamily(source, family, [], allowCorrelatedColumnValue, planSources);

    // The structural half of the union seek: every disjunct has to record an
    // equality on some key / index leading column of this source, and the whole
    // disjunction has to fit the probe cap. Collects each disjunct's own
    // equality / bound maps (its conjuncts collected exactly as a WHERE's are,
    // so an AND group inside the OR contributes all of its terms) for the seek
    // half to run. No probe evaluates here.
    private static bool TryPlanUnionDisjuncts(
        FromSource source,
        HeapTable table,
        List<BooleanExpression> disjuncts,
        bool allowCorrelatedColumnValue,
        FromSource[]? planSources,
        [NotNullWhen(true)] out List<(Dictionary<int, Expression[]> Equalities, Dictionary<int, RangeBoundExprs> Bounds)>? planned)
    {
        planned = null;
        var totalProbes = 0;
        var terms = new List<BooleanExpression>();
        var perDisjunct = new List<(Dictionary<int, Expression[]> Equalities, Dictionary<int, RangeBoundExprs> Bounds)>(disjuncts.Count);
        foreach (var disjunct in disjuncts)
        {
            terms.Clear();
            disjunct.CollectConjuncts(terms);
            var equalities = CollectColumnEqualities(source, terms, allowCorrelatedColumnValue, planSources);
            if (!HasSeekableLeadingPrefix(table, equalities))
                return false;

            var probes = 1;
            foreach (var values in equalities.Values)
            {
                if (values.Length > UnionSeekProbeCap)
                    return false;
                probes *= values.Length;
                if (probes > UnionSeekProbeCap)
                    return false;
            }

            totalProbes += probes;
            if (totalProbes > UnionSeekProbeCap)
                return false;
            perDisjunct.Add((equalities, CollectRangeBounds(source, terms, allowCorrelatedColumnValue, planSources)));
        }

        planned = perDisjunct;
        return true;
    }

    // The seek half: each planned disjunct runs through the same candidate core
    // a lone equality conjunct does (prefix choice, cartesian probes, an
    // optional range continuation), and the addresses union deduplicated by
    // physical row — not by value, which a row matching two disjuncts on two
    // columns would defeat. One disjunct that can't anchor its probes declines
    // the whole union.
    private static bool TrySeekUnionDisjuncts(
        FromSource source,
        HeapTable table,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        List<(Dictionary<int, Expression[]> Equalities, Dictionary<int, RangeBoundExprs> Bounds)> planned,
        [NotNullWhen(true)] out List<(int Page, int Slot)>? candidates)
    {
        candidates = null;
        var union = new List<(int Page, int Slot)>();
        var seen = new HashSet<(int, int)>();
        foreach (var (equalities, bounds) in planned)
        {
            if (!TryComputeEqualityCandidates(source, table, batch, outerResolver, equalities, bounds, out var part, out _, out _))
                return false;
            foreach (var address in part)
            {
                if (seen.Add(address))
                    union.Add(address);
            }
        }

        candidates = union;
        return true;
    }

    // Evaluates a column's equality value side(s) into promoted probe components,
    // or null when none can anchor a seek: NULL values are skipped (never equal
    // under = ) and an empty result drops the column; cross-collation string
    // compares or promotion failures also drop the individual probe. A single
    // unified promoted type is chosen by promoting pairwise across all surviving
    // probes — pinning the bucket key to one type. The value side is
    // row-invariant (IsStableValueSide), so this runs once per column per
    // inner execution. Duplicates aren't deduplicated; the cache lookup is a
    // hash hit and duplicate keys just probe the same bucket.
    private static (SqlType Common, SqlValue[] Probes)? EvaluateProbeComponent(
        FromSource source, int storageOrdinal, Expression[] valueSides, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var columnType = source.StoredSchema[storageOrdinal].Type;
        var raw = new List<SqlValue>(valueSides.Length);
        SqlType? common = null;

        foreach (var valueSide in valueSides)
        {
            SqlValue value;
            try
            {
                value = valueSide.Run(new RuntimeContext(
                    name => outerResolver is { } resolve ? resolve(name) : SqlValue.Null(SqlType.Int32),
                    batch));
            }
            catch (SimulatedSqlException)
            {
                continue;
            }

            if (value.IsNull)
                continue;

            if (columnType.Category == SqlTypeCategory.String
                && value.Type.Category == SqlTypeCategory.String
                && Collation.Resolve(columnType, value.Type) is null)
            {
                continue;
            }

            SqlType promoted;
            try
            {
                promoted = SqlType.Promote(columnType, value.Type);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                continue;
            }

            try
            {
                common = common is null ? promoted : SqlType.Promote(common, promoted);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                return null;
            }

            raw.Add(value);
        }

        if (common is null || raw.Count == 0)
            return null;

        var probes = new SqlValue[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            try
            {
                probes[i] = raw[i].CoerceTo(common);
            }
            catch (Exception ex) when (ex is NotSupportedException or SimulatedSqlException)
            {
                return null;
            }
        }

        return (common, probes);
    }

    // Yields each candidate row's bytes after running it through the reader's
    // per-row lock / conflict check (RC probe, READPAST skip, NOLOCK pass-through
    // — exactly what the full scan applies, but only to the seeked rows).
    private static IEnumerable<byte[]> MaterializeWithLockChecks(
        HeapTable table, BatchContext batch, DataLockPlan plan, List<(int Page, int Slot)> candidates)
    {
        // Dedup + tombstone-skip mirror the full scan's EnumerateRowsWithAddress
        // (which skips tombstoned / forward-target slots and yields each row once)
        // and neutralize the incrementally-maintained cache's only imprecision: a
        // not-yet-applied or mis-keyed Delete can leave a tombstoned address in a
        // bucket, and a double-applied Insert can list one twice.
        var seen = new HashSet<(int, int)>();
        foreach (var (page, slot) in candidates)
        {
            if (!seen.Add((page, slot)) || table.Heap.IsSlotTombstoned(page, slot))
                continue;
            if (batch.TouchRowForRead(table, page, slot, plan) && table.Heap.ReadSlotBytes(page, slot) is { } bytes)
                yield return bytes;
        }
    }

    /// <summary>
    /// Seek-narrowed live <c>(page, slot, bytes)</c> rows for a single-table
    /// UPDATE / DELETE target whose WHERE carries an indexable equality (literal /
    /// variable / arithmetic value — never a correlated column, since a single-
    /// table mutation has no outer row), IN-list / OR family, composite leading
    /// prefix (optionally extended by a range bound on the next key column), or
    /// single leading-column range. Returns <c>null</c> when nothing
    /// seekable is present, so the caller keeps its full
    /// <see cref="Heap.EnumerateRowsWithAddress"/> scan. The result is an exact
    /// match set for the seekable conjuncts only; the mutation loop re-runs the
    /// full predicate per row (its residual filter), so a partly-seekable WHERE
    /// stays correct and a stale cache entry is discarded there — the same
    /// residual-filter contract as the query path. No lock / snapshot wrapper: the
    /// mutation reads live addresses (exactly what the scan it replaces does) and
    /// X-locks only the rows it commits, so narrowing the candidate set never
    /// changes the lock footprint.
    /// </summary>
    internal static IEnumerable<(int Page, int Slot, byte[] Bytes)>? SeekMutationTarget(
        HeapTable table, BooleanExpression where, BatchContext batch)
    {
        var source = BuildBaseTableSeekSource(table, table.Name);

        var conjuncts = new List<BooleanExpression>();
        where.CollectConjuncts(conjuncts);

        var equalities = new Dictionary<int, Expression[]>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var left, out var right))
            {
                _ = TryRecordColumnEquality(source, left, right, equalities, allowCorrelatedColumnValue: false)
                    || TryRecordColumnEquality(source, right, left, equalities, allowCorrelatedColumnValue: false);
                continue;
            }
            if (conjunct.TryGetEqualityFamily(out var family))
                _ = TryRecordEqualityFamily(source, family, equalities, allowCorrelatedColumnValue: false);
        }

        var bounds = CollectRangeBounds(source, conjuncts, allowCorrelatedColumnValue: false);

        if (equalities.Count != 0
            && TryComputeEqualityCandidates(source, table, batch, outerResolver: null, equalities, bounds, out var eqCandidates, out _, out _))
        {
            return MaterializeMutationCandidates(table, eqCandidates);
        }

        if (TryComputeUnionCandidates(
            source, table, batch, outerResolver: null, conjuncts, allowCorrelatedColumnValue: false, planSources: null,
            out var unionCandidates, out _))
        {
            return MaterializeMutationCandidates(table, unionCandidates);
        }

        if (TryComputeRangeCandidates(source, table, batch, outerResolver: null, bounds, out var rangeCandidates))
            return MaterializeMutationCandidates(table, rangeCandidates);

        // No seekable equality or range conjunct: caller keeps its full scan.
        return null;
    }

    /// <summary>
    /// A per-source-row target seeker for a MERGE whose <c>ON</c> carries a
    /// seekable equality on the target's leading key / index prefix — returns
    /// <c>null</c> when no such equality exists (caller keeps its
    /// <c>target × source</c> scan). The <c>ON</c> conjunct <c>t.k = s.k</c> is
    /// the correlated-seek shape: <c>t.k</c> resolves to the (single-source)
    /// target so it's the column side, <c>s.k</c> doesn't so it's a stable outer
    /// value — exactly what the SELECT path's correlated subquery seek already
    /// recognizes (<c>allowCorrelatedColumnValue: true</c>). The returned delegate
    /// takes one source row's resolver and yields the matching target rows; a NULL
    /// or non-matching probe yields nothing (a valid empty match for that source).
    /// The structural test (does some key's lead column carry an equality?) is run
    /// once here, so per-source the delegate only evaluates probes and seeks. The
    /// caller still re-runs the full <c>ON</c> predicate per candidate — the seek
    /// keys on the equality prefix only, so a residual <c>ON</c> term
    /// (<c>… AND t.active = 1</c>) and a stale cache entry are both filtered there.
    /// </summary>
    internal static Func<Func<MultiPartName, SqlValue>, IEnumerable<(int Page, int Slot, byte[] Bytes)>>? TryPrepareMergeTargetSeek(
        HeapTable table, string? targetQualifier, BooleanExpression on, BatchContext batch)
    {
        var source = BuildBaseTableSeekSource(table, targetQualifier);

        var conjuncts = new List<BooleanExpression>();
        on.CollectConjuncts(conjuncts);

        var equalities = new Dictionary<int, Expression[]>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var left, out var right))
            {
                _ = TryRecordColumnEquality(source, left, right, equalities, allowCorrelatedColumnValue: true)
                    || TryRecordColumnEquality(source, right, left, equalities, allowCorrelatedColumnValue: true);
                continue;
            }
            if (conjunct.TryGetEqualityFamily(out var family))
                _ = TryRecordEqualityFamily(source, family, equalities, allowCorrelatedColumnValue: true);
        }

        var bounds = CollectRangeBounds(source, conjuncts, allowCorrelatedColumnValue: true);
        return HasSeekableLeadingPrefix(table, equalities) ? Seek : null;

        IEnumerable<(int Page, int Slot, byte[] Bytes)> Seek(Func<MultiPartName, SqlValue> outerResolver) =>
            TryComputeEqualityCandidates(source, table, batch, outerResolver, equalities, bounds, out var candidates, out _, out _)
                ? MaterializeMutationCandidates(table, candidates)
                : [];
    }

    // True when some key / index's leading column carries a recorded equality —
    // the structural precondition for a seek (independent of probe values, so a
    // per-source NULL probe later reads as an empty match, not "unseekable").
    private static bool HasSeekableLeadingPrefix(HeapTable table, Dictionary<int, Expression[]> equalities)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.StorageOrdinals.Length > 0 && equalities.ContainsKey(key.StorageOrdinals[0]))
                return true;
        }
        foreach (var index in table.Indexes)
        {
            if (index.KeyColumns.Length > 0 && equalities.ContainsKey(index.KeyColumns[0].StorageOrdinal))
                return true;
        }
        return false;
    }

    // Minimal single-source view of a base table for a seek: real column names +
    // storage metadata so TryIdentifyIndexableColumn resolves the predicate's
    // column references, under the given qualifier (the table's own name for a
    // single-table UPDATE / DELETE, the target alias for a MERGE). The Rows stream
    // is unused — the seek reads addresses straight from the heap's cache.
    private static FromSource BuildBaseTableSeekSource(HeapTable table, string? qualifier)
    {
        var columnNames = new string[table.Columns.Length];
        for (var i = 0; i < columnNames.Length; i++)
            columnNames[i] = table.Columns[i].Name;
        return new FromSource(
            qualifier: qualifier,
            columnNames: columnNames,
            columns: table.Columns,
            storedSchema: table.StoredColumns,
            storageOrdinals: table.StorageOrdinals,
            lobStore: table.Heap,
            rows: []);
    }

    // The mutation analogue of MaterializeWithLockChecks: dedup + tombstone-skip
    // over the seeked candidates, yielding each live row's address and bytes. No
    // per-row lock touch (the mutation path X-locks only what it commits) and no
    // live-key verify (the mutation loop's full-predicate re-check is the residual
    // filter, exactly as the query path leans on its residual WHERE).
    private static IEnumerable<(int Page, int Slot, byte[] Bytes)> MaterializeMutationCandidates(
        HeapTable table, List<(int Page, int Slot)> candidates)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var (page, slot) in candidates)
        {
            if (!seen.Add((page, slot)) || table.Heap.IsSlotTombstoned(page, slot))
                continue;
            if (table.Heap.ReadSlotBytes(page, slot) is { } bytes)
                yield return (page, slot, bytes);
        }
    }

    // Snapshot / RCSI candidate materialization. Mirrors the snapshot branch of
    // BatchContext.WrapWithRowConflictChecks, but over the seeked candidate set
    // rather than the whole heap. Two sources, deduplicated by slot:
    //   1. Bucket candidates — live rows whose CURRENT key matched the probe.
    //      Each resolves to the version visible at the snapshot (live bytes when
    //      the row carries no chain), or drops out when not visible.
    //   2. Version-chain sweep — every slot in RowVersions. These are the only
    //      rows whose snapshot-visible key can differ from their live key (so a
    //      live-key-only bucket lookup could miss them) plus tombstoned slots
    //      whose pre-delete version a pre-delete snapshot still sees. Bounded by
    //      |RowVersions|, which a read-mostly RCSI workload keeps small (the GC
    //      trims versions no open snapshot needs). The whole-table scan this
    //      replaces already walks RowVersions in its own second pass, so the
    //      seek is never more expensive than the scan it supplants.
    // The matched equality conjuncts stay in the residual WHERE, so any candidate
    // whose resolved version doesn't actually match the probe is filtered there.
    private static IEnumerable<byte[]> MaterializeSnapshotCandidates(
        HeapTable table, BatchContext batch, long snapshotXid, List<(int Page, int Slot)> bucketCandidates)
    {
        var tx = batch.Connection.CurrentTransaction;
        var seen = new HashSet<(int, int)>();

        foreach (var (page, slot) in bucketCandidates)
        {
            if (!seen.Add((page, slot)))
                continue;
            if (table.Heap.ReadSlotBytes(page, slot) is { } live
                && Storage.VersionStore.ResolveVisibleVersion(table, (page, slot), live, snapshotXid, tx) is { } resolved)
            {
                yield return resolved;
            }
        }

        foreach (var kv in table.RowVersions)
        {
            var page = kv.Key.PageIndex;
            var slot = kv.Key.SlotIndex;
            if (!seen.Add((page, slot)))
                continue;

            var resolved = table.Heap.IsSlotTombstoned(page, slot)
                ? Storage.VersionStore.ResolveTombstonedSlotForSnapshot(kv.Value, snapshotXid, tx)
                : table.Heap.ReadSlotBytes(page, slot) is { } live
                    ? Storage.VersionStore.ResolveVisibleVersion(table, (page, slot), live, snapshotXid, tx)
                    : null;
            if (resolved is { } bytes)
                yield return bytes;
        }
    }

    // A value side that is constant across the source's rows and free of side
    // effects, so evaluating it once for the seek and again in the residual WHERE
    // is harmless. Pure conversion wrappers (CAST / CONVERT / parens) are peeled
    // first — `id = CAST(@v AS bigint)` is as stable as `id = @v`, matching real
    // SQL Server keeping the cast sargable. The stable leaves are a literal, a
    // session variable, or a column reference that escapes to the enclosing
    // scope (see IsEnclosingScopeReference). An arithmetic node (which is how the
    // parser represents a negative literal: `-1` is `0 - 1`) is stable when both
    // its operands are — a deterministic operator over row-invariant operands is
    // itself row-invariant, so `id = -1` / `id = @v + 1` seek too, and the
    // recursion still excludes a column of the plan's own FROM or a
    // non-deterministic / side-effecting function / subquery leaf (those
    // decline).
    // <paramref name="allowCorrelatedColumnValue"/> is false at a site with no
    // enclosing scope to read a column from (a single-table mutation), where no
    // column reference can be a probe value at all.
    // <paramref name="planSources"/> is the whole FROM of the plan being
    // narrowed, passed when narrowing ONE source of a multi-source FROM so a
    // sibling's column is recognized as such; null means the narrowed source is
    // the whole FROM.
    private static bool IsStableValueSide(
        Expression expression, FromSource source, bool allowCorrelatedColumnValue = true, FromSource[]? planSources = null)
    {
        while (expression.PureConversionOperand is { } inner)
            expression = inner;
        return expression switch
        {
            Value => true,
            VariableReference => true,
            Reference reference => allowCorrelatedColumnValue && IsEnclosingScopeReference(reference, source, planSources),
            TwoSidedExpression arithmetic => arithmetic.BothOperandsMatch(
                operand => IsStableValueSide(operand, source, allowCorrelatedColumnValue, planSources)),
            Negate negate => IsStableValueSide(negate.Operand, source, allowCorrelatedColumnValue, planSources),
            _ => false,
        };
    }

    /// <summary>
    /// Classifies a column reference on a seek's value side, which is what
    /// decides whether that value is fixed for the whole narrowing.
    /// <list type="bullet">
    /// <item>It resolves in the plan's own FROM — the narrowed source itself or
    /// a <b>sibling</b> of the same query — so it varies row by row and (for a
    /// sibling) isn't even readable before the join runs. Declines.</item>
    /// <item>It resolves in none of them, so at runtime
    /// <see cref="ResolveAcrossTuple"/> hands it to the <b>enclosing scope's</b>
    /// resolver — a correlated outer row, or the left row a join / <c>APPLY</c>
    /// already buffered. That value is fixed for the duration of one execution
    /// of this plan (the plan is what re-executes per enclosing row), so it can
    /// anchor the seek exactly as a variable does. Accepts.</item>
    /// </list>
    /// The resolution runs against the same <see cref="FindSourceColumn"/> the
    /// per-row resolver uses, so the two agree on which names are local by
    /// construction. Whether an enclosing resolver is actually installed doesn't
    /// enter into it: a name resolving nowhere would have failed to bind, and a
    /// probe evaluated without a resolver reads NULL and simply declines the
    /// seek.
    /// </summary>
    private static bool IsEnclosingScopeReference(Reference reference, FromSource source, FromSource[]? planSources)
        => FindSourceColumn(planSources ?? [source], reference.ReferencedName).SourceIndex < 0;
}
