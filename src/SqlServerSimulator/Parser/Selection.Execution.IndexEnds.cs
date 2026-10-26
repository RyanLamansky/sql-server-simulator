using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

internal sealed partial class Selection
{
    /// <summary>
    /// Each aggregate's answer read off one end of an index, for a query whose
    /// every aggregate is a <c>MIN</c> or <c>MAX</c> of a column an index
    /// leads, after the columns its WHERE pins, over one table whose WHERE
    /// that index's seek consumes whole: the first row in the aggregate's
    /// direction holding a value, read as a <c>TOP (1)</c> over the ordered
    /// seek reads it. That is real's plan for one such aggregate at any size —
    /// a seek, which meets no NULL and so raises no Msg 8153 — where a WHERE
    /// the seek leaves anything of scans and aggregates every row; several
    /// over a nullable column scan a small table as one, warning of its
    /// NULLs, so those keep the scan, and several over columns that hold none
    /// read alike either way (probed 2026-10-26 against SQL Server 2025).
    /// Null for any other shape, a read taking locks of its own, and one the
    /// ordered seek declines.
    /// </summary>
    private static SqlValue?[]? TryReadIndexEnds(
        FromSource[] sources, JoinSpec[] joins, List<BooleanExpression> excluders, List<AggregateExpression> aggregates,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (sources.Length != 1 || joins.Length != 0 || aggregates.Count == 0
            || sources[0] is not { BackingTable: { } table, LateralPlan: null, HeapPlan: { RowMode: null, SerializableRangeMode: null, Pages: null, WriteTargetAddresses: null, RowTxScoped: false } })
        {
            return null;
        }
        var source = sources[0];
        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);
        var pins = CollectSingleValuePins(source, conjuncts);
        foreach (var aggregate in aggregates)
        {
            if (aggregate.Kind is not (AggregateKind.Min or AggregateKind.Max) || aggregate.OperandUnreachable
                || !TryIdentifyIndexableColumn(source, aggregate.Operand!, out var ordinal)
                || (aggregates.Count > 1 && source.StoredSchema[ordinal].Nullable)
                || !TryFindOrderedSeekPrefix(table, pins, [ordinal], out var prefix, out var pinnedLength, out _)
                || !SeekConsumesWhere(source, conjuncts, prefix.AsSpan(0, pinnedLength), ordinal))
            {
                return null;
            }
        }

        // Every read settled before any row is: a decline leaves the scan to
        // read the table as though nothing here had run.
        var reads = new FromSource?[aggregates.Count];
        for (var i = 0; i < aggregates.Count; i++)
        {
            if (aggregates[i].ResultUnread)
                continue;
            List<OrderBySpec> order = [OrderBySpec.FromExpression(aggregates[i].Operand!, descending: aggregates[i].Kind == AggregateKind.Max)];
            if (!TryApplyOrderedScan(sources, joins, order, excluders, skip: 0, batch, outerResolver, out var ordered, out _, skipsNullKeys: true))
                return null;
            reads[i] = ordered[0];
        }

        var ends = new SqlValue?[aggregates.Count];
        var tuple = new byte[]?[1];
        var memo = new SourceColumnMemo();
        SqlValue resolve(MultiPartName name) => ResolveAcrossTuple(sources, tuple, name, batch, outerResolver, memo);
        var runtime = new RuntimeContext(resolve, batch);
        for (var i = 0; i < reads.Length; i++)
        {
            if (reads[i] is not { } read)
                continue;
            foreach (var row in read.RowsFor(batch))
            {
                tuple[0] = row;
                if (!ExcludersPass(excluders, runtime) || aggregates[i].Operand!.Run(runtime) is not { IsNull: false } value)
                    continue;
                ends[i] = value;
                break;
            }
        }
        return ends;

        static bool ExcludersPass(List<BooleanExpression> excluders, RuntimeContext runtime)
        {
            foreach (var excluder in excluders)
            {
                if (excluder.Run(runtime) != true)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Whether every conjunct is one an ordered seek of the index consumes:
    /// an equality pinning one of <paramref name="pinned"/> to a value fixed
    /// for the read, or a range or <c>BETWEEN</c> on the column at storage
    /// ordinal <paramref name="ordered"/> bounded by such values.
    /// </summary>
    private static bool SeekConsumesWhere(FromSource source, List<BooleanExpression> conjuncts, ReadOnlySpan<int> pinned, int ordered)
    {
        foreach (var conjunct in conjuncts)
        {
            if (conjunct.TryGetEqualityOperands(out var left, out var right))
            {
                if ((TryIdentifyIndexableColumn(source, left, out var column) && IsStableValueSide(right, source) && pinned.Contains(column))
                    || (TryIdentifyIndexableColumn(source, right, out column) && IsStableValueSide(left, source) && pinned.Contains(column)))
                {
                    continue;
                }
                return false;
            }
            if (conjunct.TryGetRangeOperands(out var rangeLeft, out var op, out var rangeRight))
            {
                if (TryNormalizeRangeBound(source, rangeLeft, op, rangeRight, out var bounded, out _, out _) && bounded == ordered)
                    continue;
                return false;
            }
            if (conjunct.TryGetBetweenOperands(out var value, out var lower, out var upper)
                && TryIdentifyIndexableColumn(source, value, out var between) && between == ordered
                && IsStableValueSide(lower, source) && IsStableValueSide(upper, source))
            {
                continue;
            }
            return false;
        }
        return true;
    }
}
