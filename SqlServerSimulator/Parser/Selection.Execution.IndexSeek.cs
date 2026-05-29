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
    /// prefix across all keys / indexes wins.
    /// </para>
    /// <para>
    /// Returns the same array when no seek applies. Every matched conjunct is
    /// <b>kept</b> in <paramref name="excluders"/> as a residual filter, so the
    /// seek can only narrow the row source — never change results. The value
    /// side is restricted to side-effect-free, row-invariant shapes precisely
    /// so evaluating it once here and again in the residual WHERE is harmless.
    /// </para>
    /// </summary>
    private static FromSource[] MaybeApplyIndexSeek(
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        bool allowCorrelatedColumnValue = true)
    {
        if (sources.Length != 1 || joins.Length != 0 || excluders.Count == 0)
            return sources;
        var source = sources[0];
        if (source.BackingTable is not { } table || source.LateralPlan is not null)
            return sources;

        // The seek narrows the row source, then routes each candidate through
        // the SAME per-row lock / conflict pipeline the full scan uses — so it
        // touches (and locks) only the seeked rows, matching a real index seek.
        // tx-scoped row locks (REPEATABLE READ / SERIALIZABLE / UPDLOCK …) keep
        // the whole-table scan, which deliberately locks every row it reads to
        // end of transaction.
        if (source.HeapPlan is not { } plan || plan.RowTxScoped)
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

        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        // Map each indexable column of THIS source carrying a stable-value
        // equality conjunct (or IN-list / OR-of-equalities on the same column)
        // to its value side(s). First writer wins per column; a redundant
        // later conjunct just stays as a residual filter.
        var equalities = new Dictionary<int, Expression[]>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var left, out var right))
            {
                _ = TryRecordColumnEquality(source, left, right, equalities, allowCorrelatedColumnValue)
                    || TryRecordColumnEquality(source, right, left, equalities, allowCorrelatedColumnValue);
                continue;
            }
            if (conjunct.TryGetEqualityFamily(out var family))
                _ = TryRecordEqualityFamily(source, family, equalities, allowCorrelatedColumnValue);
        }

        if (equalities.Count != 0
            && TrySeekByLongestPrefix(source, table, plan, batch, snapshotXid, outerResolver, equalities, out var seekRows, out var width))
        {
            IndexSeekDiagnostics.Sink?.Add($"Seek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"SeekWidth({table.Name},{width})");
            return SeekedSource(source, seekRows);
        }

        // No equality seek — try a range seek on a leading key column
        // (col > v / col BETWEEN lo AND hi / a one-sided bound). The matched
        // bound conjunct(s) stay in the residual WHERE, so the range only
        // narrows the candidate set.
        if (TrySeekByRange(source, table, plan, batch, snapshotXid, outerResolver, conjuncts, allowCorrelatedColumnValue, out var rangeRows))
        {
            IndexSeekDiagnostics.Sink?.Add($"Seek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"RangeSeek({table.Name})");
            IndexSeekDiagnostics.Sink?.Add($"SeekWidth({table.Name},1)");
            return SeekedSource(source, rangeRows);
        }

        IndexSeekDiagnostics.Sink?.Add($"Scan({table.Name})");
        return sources;
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
    /// index or key, and the bound value(s) are stable. Single leading-column
    /// range only; a range on a non-leading column, or an equality-prefix
    /// continued by a range, isn't narrowed here (the equality path takes the
    /// prefix it can and the range stays residual). The bound conjuncts remain in
    /// the residual WHERE, so the seek only narrows the candidate set.
    /// </summary>
    private static bool TrySeekByRange(
        FromSource source,
        HeapTable table,
        DataLockPlan plan,
        BatchContext batch,
        long? snapshotXid,
        Func<MultiPartName, SqlValue>? outerResolver,
        List<BooleanExpression> conjuncts,
        bool allowCorrelatedColumnValue,
        out IEnumerable<byte[]> seekRows)
    {
        seekRows = [];

        var bounds = new Dictionary<int, RangeBoundExprs>();
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetRangeOperands(out var left, out var op, out var right))
            {
                if (TryIdentifyIndexableColumn(source, left, out var leftOrd) && IsStableValueSide(right, source, allowCorrelatedColumnValue))
                    RecordBound(bounds, leftOrd, op, right);
                else if (TryIdentifyIndexableColumn(source, right, out var rightOrd) && IsStableValueSide(left, source, allowCorrelatedColumnValue))
                    RecordBound(bounds, rightOrd, FlipComparison(op), left);
                continue;
            }

            if (conjunct.TryGetBetweenOperands(out var value, out var lower, out var upper)
                && TryIdentifyIndexableColumn(source, value, out var betweenOrd)
                && IsStableValueSide(lower, source, allowCorrelatedColumnValue)
                && IsStableValueSide(upper, source, allowCorrelatedColumnValue))
            {
                RecordBound(bounds, betweenOrd, RangeComparison.GreaterOrEqual, lower);
                RecordBound(bounds, betweenOrd, RangeComparison.LessOrEqual, upper);
            }
        }

        if (bounds.Count == 0 || FindRangeLeadingOrdinal(table, bounds) is not { } ordinal)
            return false;

        var bound = bounds[ordinal];
        var columnType = source.StoredSchema[ordinal].Type;

        // Evaluate present bounds, unify their promoted type with the column's, and
        // coerce both to it. A NULL bound makes every comparison UNKNOWN, so the
        // range matches nothing — a valid (empty) seek. A promotion / collation
        // failure declines to the full scan.
        SqlType? common = null;
        SqlValue lowerValue = default, upperValue = default;
        if (bound.Lower is { } lowerExpr)
        {
            switch (EvaluateBound(lowerExpr, columnType, batch, outerResolver, out lowerValue, out var lowerCommon))
            {
                case BoundEval.Decline: return false;
                case BoundEval.Null: return true;
                default: common = lowerCommon; break;
            }
        }
        if (bound.Upper is { } upperExpr)
        {
            switch (EvaluateBound(upperExpr, columnType, batch, outerResolver, out upperValue, out var upperCommon))
            {
                case BoundEval.Decline: return false;
                case BoundEval.Null: return true;
                default: common = common is null ? upperCommon : SqlType.Promote(common, upperCommon); break;
            }
        }

        if (common is null)
            return false;

        var hasLower = bound.Lower is not null;
        var hasUpper = bound.Upper is not null;
        if (hasLower)
            lowerValue = lowerValue.CoerceTo(common);
        if (hasUpper)
            upperValue = upperValue.CoerceTo(common);

        var cache = HeapSeekCache.For(table.Heap);
        var candidates = cache.RangeScan(
            table.Heap, source.StoredSchema, source.LobStore, ordinal, common,
            hasLower, lowerValue, bound.LowerInclusive, hasUpper, upperValue, bound.UpperInclusive);

        seekRows = snapshotXid is { } sx
            ? MaterializeSnapshotCandidates(table, batch, sx, candidates)
            : MaterializeWithLockChecks(table, batch, plan, candidates);
        return true;
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
    /// Pushes single-source WHERE equality predicates (<c>leftmostCol = literal /
    /// variable</c>) down onto the leftmost FROM source of a multi-source query,
    /// seeking it before the join runs. The leftmost source is always preserved
    /// (never the NULL-supplied side of an outer join), so narrowing it can't
    /// seeking it before the join runs. The leftmost source is always preserved
    /// (never the NULL-supplied side of an outer join), so narrowing it can't
    /// change join semantics, and the conjuncts stay in the residual WHERE.
    /// Probe values are restricted to non-column constants/variables — a
    /// not-yet-joined sibling column isn't resolvable pre-join. Shrinking the
    /// driving rowset is what lets <see cref="EquiJoinSeekOrHash"/> seek the
    /// inner per outer row for the common filter-then-join shape.
    /// </summary>
    private static FromSource[] NarrowLeftmostJoinSource(
        FromSource[] sources, List<BooleanExpression> excluders, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (sources.Length < 2 || excluders.Count == 0)
            return sources;
        var seeked = MaybeApplyIndexSeek([sources[0]], NoJoins, excluders, batch, outerResolver, allowCorrelatedColumnValue: false);
        if (ReferenceEquals(seeked[0], sources[0]))
            return sources;
        var result = (FromSource[])sources.Clone();
        result[0] = seeked[0];
        return result;
    }

    // Records `column = stableValue` for an indexable, non-LOB column of THIS
    // source. No evaluation happens here — only the value-side expression is
    // captured; it's run lazily (and once) when a prefix actually selects it.
    private static bool TryRecordColumnEquality(
        FromSource source, Expression columnSide, Expression valueSide, Dictionary<int, Expression[]> equalities, bool allowCorrelatedColumnValue)
        => TryIdentifyIndexableColumn(source, columnSide, out var storageOrdinal)
            && IsStableValueSide(valueSide, source, allowCorrelatedColumnValue)
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
        bool allowCorrelatedColumnValue)
    {
        if (family.Count == 0)
            return false;

        int? targetStorageOrdinal = null;
        var values = new Expression[family.Count];
        for (var i = 0; i < family.Count; i++)
        {
            var (left, right) = family[i];
            if (!TryExtractColumnAndValue(source, left, right, allowCorrelatedColumnValue, out var ord, out var value)
                && !TryExtractColumnAndValue(source, right, left, allowCorrelatedColumnValue, out ord, out value))
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
        out int storageOrdinal,
        [NotNullWhen(true)] out Expression? value)
    {
        if (TryIdentifyIndexableColumn(source, columnSide, out storageOrdinal)
            && IsStableValueSide(valueSide, source, allowCorrelatedColumnValue))
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
        out IEnumerable<byte[]> seekRows,
        out int width)
    {
        seekRows = [];
        width = 0;

        var resolved = new Dictionary<int, (SqlType Common, SqlValue[] Probes)?>();

        var bestLen = 0;
        int[]? bestKeyOrdinals = null;
        Storage.Index? bestIndex = null;

        foreach (var key in table.KeyConstraints)
        {
            var ordinals = key.StorageOrdinals;
            var len = 0;
            while (len < ordinals.Length && ResolveComponent(ordinals[len]) is not null)
                len++;
            if (len > bestLen)
            {
                bestLen = len;
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
            if (len > bestLen)
            {
                bestLen = len;
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
        var candidates = new List<(int Page, int Slot)>();
        foreach (var tuple in CartesianProduct(probesPerColumn))
        {
            var bucket = cache.Seek(table.Heap, source.StoredSchema, source.LobStore, prefix, commons, new SqlValueKey(tuple));
            if (bucket.Count != 0)
                candidates.AddRange(bucket);
        }

        seekRows = snapshotXid is { } sx
            ? MaterializeSnapshotCandidates(table, batch, sx, candidates)
            : MaterializeWithLockChecks(table, batch, plan, candidates);
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
        HeapTable table, BatchContext batch, DataLockPlan plan, IReadOnlyList<(int Page, int Slot)> candidates)
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
        HeapTable table, BatchContext batch, long snapshotXid, IReadOnlyList<(int Page, int Slot)> bucketCandidates)
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
    // session variable, or a column resolving to some OTHER source (an outer /
    // correlated reference). An arithmetic node (which is how the parser
    // represents a negative literal: `-1` is `0 - 1`) is stable when both its
    // operands are — a deterministic operator over row-invariant operands is
    // itself row-invariant, so `id = -1` / `id = @v + 1` seek too, and the
    // recursion still excludes a column of THIS source or a non-deterministic /
    // side-effecting function / subquery leaf (those decline).
    // <paramref name="allowCorrelatedColumnValue"/> is false when narrowing the
    // leftmost source of a multi-source FROM <i>before</i> the join runs: a
    // not-yet-joined sibling column isn't resolvable then, so only literals /
    // variables / parameters (and arithmetic over them) qualify as the probe value.
    private static bool IsStableValueSide(Expression expression, FromSource source, bool allowCorrelatedColumnValue = true)
    {
        while (expression.PureConversionOperand is { } inner)
            expression = inner;
        return expression switch
        {
            Value => true,
            VariableReference => true,
            Reference reference => allowCorrelatedColumnValue && FindSourceColumn([source], reference.ReferencedName).SourceIndex < 0,
            TwoSidedExpression arithmetic => arithmetic.BothOperandsMatch(operand => IsStableValueSide(operand, source, allowCorrelatedColumnValue)),
            _ => false,
        };
    }
}
