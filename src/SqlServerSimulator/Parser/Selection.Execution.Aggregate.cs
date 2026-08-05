using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Aggregate-mode executor for SELECTs that carry aggregates, GROUP BY, or
/// HAVING. Streams every input tuple through each projection aggregate's
/// accumulator (per group when GROUP BY is in play), then projects one
/// output row per group.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Aggregate-mode executor. Buffers WHERE-filtered input rows once, then
    /// iterates each grouping set (or a single synthesized empty set when
    /// GROUP BY is absent) and partitions the buffer by that set's column
    /// values, accumulating aggregates per partition. Projection runs once
    /// per group with a grouped-away resolver: references to GROUP BY
    /// columns not in the current set return typed NULL (matching SQL
    /// Server's subtotal/total-row semantics), references to other columns
    /// in the parent scope chain through <paramref name="outerResolver"/>.
    /// HAVING runs per group after finalization; any window, then TOP /
    /// OFFSET / FETCH, apply to the concatenated stream across all grouping
    /// sets. Without GROUP BY the output is exactly one row even for empty
    /// input (SQL Server's implicit-empty-GROUP-BY rule).
    /// </summary>
    private static List<SqlValue[]> BuildAggregateProjectionRows(
        FromSource[] sources,
        JoinSpec[] joins,
        Func<MultiPartName, SqlType> resolveColumnType,
        List<Expression> expressions,
        FromClause fromClause,
        string[] outputColumnNames,
        List<OrderBySpec> orderByItems,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        SqlType[] windowOperandTypes,
        SqlType[] windowResultTypes,
        TopSpec top,
        int? offsetCount,
        int? fetchCount,
        bool distinct,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
        => BuildAggregateProjectionRowsCore(
               sources, joins, resolveColumnType, expressions, fromClause, outputColumnNames, orderByItems,
               aggregates, windows, windowOperandTypes, windowResultTypes, top, offsetCount, fetchCount,
               distinct, batch, outerResolver, allowParallel: true)
           ?? BuildAggregateProjectionRowsCore(
               sources, joins, resolveColumnType, expressions, fromClause, outputColumnNames, orderByItems,
               aggregates, windows, windowOperandTypes, windowResultTypes, top, offsetCount, fetchCount,
               distinct, batch, outerResolver, allowParallel: false)!;

    /// <summary>
    /// The executor proper. Returns <see langword="null"/> — and only when
    /// <paramref name="allowParallel"/> was set — to say that the parallel
    /// accumulation was abandoned and the statement has to run again serially:
    /// a worker raised, a merge proved inexact, or the producer raised once
    /// workers existed. The re-run is the canonical row order, so whatever the
    /// abandoned attempt saw is discarded rather than reported. See
    /// <c>Selection.Execution.AggregateParallel.cs</c>.
    /// </summary>
    private static List<SqlValue[]>? BuildAggregateProjectionRowsCore(
        FromSource[] sources,
        JoinSpec[] joins,
        Func<MultiPartName, SqlType> resolveColumnType,
        List<Expression> expressions,
        FromClause fromClause,
        string[] outputColumnNames,
        List<OrderBySpec> orderByItems,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        SqlType[] windowOperandTypes,
        SqlType[] windowResultTypes,
        TopSpec top,
        int? offsetCount,
        int? fetchCount,
        bool distinct,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver,
        bool allowParallel)
    {
        if (top.Count == 0 && top.Percent is null)
            return [];
        var memo = new SourceColumnMemo();

        var aggregateOperandTypes = new SqlType[aggregates.Count];
        var aggregateResultTypes = new SqlType[aggregates.Count];
        for (var i = 0; i < aggregates.Count; i++)
        {
            aggregateOperandTypes[i] = aggregates[i].Operand?.GetSqlType(batch, resolveColumnType) ?? SqlType.Int32;
            aggregateResultTypes[i] = aggregates[i].GetSqlType(batch, resolveColumnType);
        }

        GroupState NewGroup(SqlValue[] keyValues)
        {
            var freshAggregators = new Aggregator[aggregates.Count];
            for (var i = 0; i < aggregates.Count; i++)
                freshAggregators[i] = Aggregator.Create(aggregates[i], aggregateOperandTypes[i], aggregateResultTypes[i]);
            return new(keyValues, freshAggregators);
        }

        // Narrow a single-base-table source by an equality seek when WHERE
        // carries an indexable conjunct — the same acceleration the
        // non-aggregate projector applies. The matched conjuncts stay in
        // Excluders as residual filters below, so the aggregate sees exactly
        // the WHERE-passing rows (e.g. SELECT SUM(x) ... WHERE indexedcol = @v
        // seeks instead of scanning the whole heap).
        sources = MaybeApplyIndexSeek(sources, joins, fromClause.Excluders, batch, outerResolver);
        (sources, joins) = NarrowJoinSources(sources, joins, fromClause.Excluders, batch, outerResolver);

        // Effective grouping sets: parser-built list, or a single synthesized
        // empty set when GROUP BY is absent (the implicit "all rows are one
        // group" case for queries that have aggregates but no GROUP BY).
        var effectiveSets = fromClause.GroupingSets;
        if (effectiveSets.Count == 0)
            effectiveSets = [[]];

        // Row-invariant resolution scaffolding is hoisted out of every per-row
        // loop below: `currentTuple` is a mutable capture rewritten per row,
        // and the resolver is a cached self-referencing lambda (see
        // EnumerateJoinedRows), so each loop allocates one closure + one
        // delegate + one RuntimeContext TOTAL instead of several per row —
        // per-row delegate churn dominated the allocation profile.
        // `keyScratch` is the same idea for the grouping key, and
        // `ungroupedState` the implicit whole-input group (see Accumulate).
        var keyScratch = Array.Empty<SqlValue>();
        var ungroupedState = default(GroupState);
        var currentTuple = default(byte[]?[])!;
        Func<MultiPartName, SqlValue> resolveColumn = null!;
        resolveColumn = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, resolveColumn, memo);
        var rowRuntime = new RuntimeContext(resolveColumn, batch);

        // One grouping set — no GROUP BY at all, or a plain GROUP BY — reads
        // each input row exactly once, so the groups are built straight off the
        // enumeration and nothing is buffered. ROLLUP / CUBE / GROUPING SETS
        // partition the same rows several ways and still buffer, since the
        // second set can't re-read a stream the first consumed.
        //
        // Streaming is also what real does: its plan pipelines Filter into
        // Stream/Hash Aggregate, so an aggregate operand that raises on an
        // early row preempts a WHERE that would have raised on a later one
        // (probe-confirmed — `SELECT SUM(1/a) … WHERE CAST(s AS int) > 0` over
        // a table whose first row zeroes `a` and whose last row's `s` isn't
        // numeric reports Msg 8134, not the conversion error).
        var streaming = effectiveSets.Count == 1;
        var streamedGroups = streaming ? NewGroupMap(effectiveSets[0]) : null;
        var buffered = streaming ? null : new List<byte[]?[]>();

        // Past ParallelRowThreshold rows the per-row consumer work — WHERE,
        // grouping key, aggregate operands, and the decoding all three trigger
        // — forks across worker threads while this thread keeps producing the
        // stream. Deciding mid-stream needs no cardinality estimate and leaves
        // a small query paying nothing; the prefix's groups are already in
        // `streamedGroups` and take part in the merge. See
        // Selection.Execution.AggregateParallel.cs for the gates, the
        // merge contract and the error rule.
        ParallelGroupedAccumulation? parallel = null;
        var parallelConsidered = !allowParallel || !streaming || windows.Count > 0;
        var enumeratedRows = 0L;
        try
        {
            foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
            {
                var rowOrdinal = enumeratedRows++;
                if (parallel is not null)
                {
                    parallel.Offer(tuple, rowOrdinal);
                    continue;
                }

                // Judged on rows *enumerated*, not on rows the WHERE kept: a
                // filter that keeps almost nothing still costs a full read, and
                // that read is exactly what the fork is meant to share.
                if (!parallelConsidered && enumeratedRows >= ParallelRowThreshold)
                {
                    parallelConsidered = true;
                    parallel = ParallelGroupedAccumulation.TryEngage(
                        sources, effectiveSets[0], fromClause.Excluders, aggregates, aggregateResultTypes,
                        batch, NewGroup, outerResolver, (int)enumeratedRows, streamedGroups!.Count);
                    if (parallel is not null)
                    {
                        parallel.Offer(tuple, rowOrdinal);
                        continue;
                    }
                }

                currentTuple = tuple;
                var include = true;
                foreach (var excluder in fromClause.Excluders)
                {
                    if (excluder.Run(rowRuntime) != true)
                    {
                        include = false;
                        break;
                    }
                }
                if (!include)
                    continue;

                if (streamedGroups is not null)
                {
                    Accumulate(streamedGroups, effectiveSets[0], tuple, tupleIsShared: true);
                    continue;
                }

                // `EnumerateJoinedRows` mutates a single shared tuple array in
                // place across iterations, so each buffered row gets snapshotted
                // (the inner byte[] references are immutable, only the outer array
                // slots get rewritten by the join driver).
                var snapshot = new byte[]?[tuple.Length];
                Array.Copy(tuple, snapshot, tuple.Length);
                buffered!.Add(snapshot);
            }

            if (parallel is not null && !parallel.TryComplete(streamedGroups!))
            {
                AggregateDiagnostics.Sink?.Add("Aggregate:SerialRerun(merge)");
                return null;
            }
        }
#pragma warning disable CA1031 // Deliberate: the catch is the parallel path's whole error contract — every kind is discarded and re-raised, in order, by the serial re-run below.
        catch (Exception) when (parallel is not null)
        {
            // Whatever raised — a worker's row, or this thread's own — the
            // serial re-run is the canonical order and reports the canonical
            // error, so this attempt is discarded whole.
            parallel.Cancel();
            AggregateDiagnostics.Sink?.Add("Aggregate:SerialRerun(error)");
            return null;
        }
#pragma warning restore CA1031
        finally
        {
            parallel?.Dispose();
        }

        var output = new List<(SqlValue[] OrderKeys, SqlValue[] Row)>();

        // A small `TOP (n)` / `FETCH` over the sorted group stream keeps only
        // the n best groups in a bounded heap instead of sorting every group —
        // the same path the row-level projection takes, applied to the grouped
        // stream. Ineligible shapes (PERCENT, WITH TIES, OFFSET, DISTINCT) fall
        // back to the full sort below.
        var topNGroups = TopNHeapCap(orderByItems, distinct, top, offsetCount, fetchCount) is { } topNCap
            ? new TopNRowHeap(topNCap, orderByItems)
            : null;
        var projectionScratch = topNGroups is null ? [] : new SqlValue[expressions.Count];
        var orderKeyScratch = topNGroups is null ? [] : new SqlValue[orderByItems.Count];

        // Window pass input. A window in a grouped SELECT spans the query's
        // *whole* grouped result — with ROLLUP / CUBE / GROUPING SETS that
        // means every set's groups as one row set, subtotal and grand-total
        // rows included (probe-confirmed: `SUM(SUM(x)) OVER ()` over
        // `ROLLUP(a)` totals the leaf rows *and* the grand-total row). So the
        // set loop below only buffers survivors, and the single window pass
        // runs after it over the concatenation. Each survivor carries its own
        // grouping set, since GROUPING() / GROUPING_ID() inside a PARTITION BY
        // or window operand reads the group's set, not the loop's.
        var windowSurvivors = windows.Count > 0
            ? new List<(Expression[] GroupingSet, GroupState State, SqlValue[] AggregateValues)>()
            : null;

        // Per-group resolution scaffolding, hoisted out of the grouping-set
        // loop like the per-row loops above: `currentGroupingSet` /
        // `currentState` / `currentProjected` are mutable captures rewritten
        // per set and per group, and the resolvers are cached
        // self-referencing lambdas — a large-group-count GROUP BY (one group
        // per customer) evaluated several expressions per group through fresh
        // delegates otherwise.
        Expression[] currentGroupingSet = [];
        var currentState = default(GroupState)!;
        var currentProjected = default(SqlValue[])!;
        Func<MultiPartName, SqlValue> resolveByGroupKey = null!;
        resolveByGroupKey = name =>
        {
            for (var i = 0; i < currentGroupingSet.Length; i++)
            {
                // Qualifier-aware: matching on the leaf alone made a
                // projected `p.name` bind to a `b.name` grouping key
                // whenever a join brought both into scope.
                if (currentGroupingSet[i] is Reference r
                    && SourceReferenceMatches(r.ReferencedName, name))
                {
                    return currentState.KeyValues[i];
                }
            }
            // Column appears in another grouping set but not this one
            // — return typed NULL to surface the subtotal/total-row
            // semantic. The type comes from the column-type resolver.
            foreach (var expr in fromClause.AllGroupingExpressions)
            {
                if (expr is Reference r
                    && SourceReferenceMatches(r.ReferencedName, name))
                {
                    return SqlValue.Null(expr.GetSqlType(batch, resolveColumnType));
                }
            }

            // Column referenced inside one of this (non-empty) set's
            // grouping expressions: resolve against the group's
            // representative row. ResolveAcrossTuple itself falls back
            // to the outer resolver / Msg 207 when the name isn't a
            // source column, so this subsumes the outer-or-throw tail.
            return currentGroupingSet.Length > 0 && currentState.Representative is { } rep
                ? ResolveAcrossTuple(sources, rep, name, batch, outerResolver, resolveByGroupKey, memo)
                : outerResolver is not null
                    ? outerResolver(name)
                    : throw SimulatedSqlException.InvalidColumnName(name);
        };
        var groupRuntime = new RuntimeContext(resolveByGroupKey, batch);

        // Resolves an ORDER BY item's column references against this
        // group's output (alias / select-list name first), then through
        // the grouped-key resolver — so ORDER BY can reference a select
        // alias, a grouped column, or a grouping expression.
        SqlValue ResolveOrderName(MultiPartName name)
        {
            // A qualified term names a source column, never an output
            // alias — the same rule the non-grouped ORDER BY follows.
            // Matching on the leaf alone made `ORDER BY publisher.name`
            // bind to a projected `book.name` across a join.
            if (name.ImmediateQualifier is null)
            {
                for (var j = 0; j < outputColumnNames.Length; j++)
                {
                    if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                        return currentProjected[j];
                }
            }

            return resolveByGroupKey(name);
        }

        var orderRuntime = new RuntimeContext(ResolveOrderName, batch);

        // The group map one grouping set accumulates into. An empty set is the
        // implicit whole-input group, which exists even over no rows — and is
        // held aside so the accumulation loop reaches it directly instead of
        // hashing the empty key once per input row (a sixth of an ungrouped
        // aggregate's whole-table scan).
        Dictionary<SqlValueKey, GroupState> NewGroupMap(Expression[] groupingSet)
        {
            var groups = new Dictionary<SqlValueKey, GroupState>();
            ungroupedState = null;
            if (groupingSet.Length == 0)
                groups[SqlValueKey.Empty] = ungroupedState = NewGroup([]);
            return groups;
        }

        // One input row into its group under one grouping set. The key tuple is
        // computed into a reused scratch buffer and only copied out on the
        // first row of a group — a per-row key array was one allocation per
        // input row where the group count is what actually needs one. The
        // scratch-backed key is handed to TryGetValue alone, which reads the
        // components without retaining them; the stable copy is what the
        // dictionary and the group state then share.
        //
        // `tupleIsShared` says the caller is handing over the join driver's own
        // mutable tuple, so a retained representative has to be snapshotted.
        void Accumulate(Dictionary<SqlValueKey, GroupState> groups, Expression[] groupingSet, byte[]?[] tuple, bool tupleIsShared)
        {
            GroupState state;
            if (ungroupedState is { } wholeInput)
            {
                state = wholeInput;
            }
            else
            {
                if (keyScratch.Length != groupingSet.Length)
                    keyScratch = new SqlValue[groupingSet.Length];
                for (var i = 0; i < groupingSet.Length; i++)
                    keyScratch[i] = groupingSet[i].Run(rowRuntime);
                if (!groups.TryGetValue(new SqlValueKey(keyScratch), out state!))
                {
                    var keyValues = new SqlValue[groupingSet.Length];
                    Array.Copy(keyScratch, keyValues, groupingSet.Length);
                    state = NewGroup(keyValues);
                    groups[new SqlValueKey(keyValues)] = state;
                }
            }

            // Keep the first row that lands in each group as a
            // representative. Within a group every grouping expression is
            // constant, so any projection / HAVING / ORDER BY column buried
            // inside a grouping expression (e.g. OrderDate under
            // GROUP BY MONTH(OrderDate)) resolves correctly against it.
            if (state.Representative is null)
            {
                if (tupleIsShared)
                {
                    var representative = new byte[]?[tuple.Length];
                    Array.Copy(tuple, representative, tuple.Length);
                    state.Representative = representative;
                }
                else
                {
                    state.Representative = tuple;
                }
            }

            for (var i = 0; i < aggregates.Count; i++)
            {
                var aggregate = aggregates[i];
                // An arm real settled as unreachable while compiling never
                // supplies a value, so its operand isn't evaluated and the
                // aggregator stays at its empty result — which nothing
                // reads, the arm holding it being unreachable.
                if (aggregate.OperandUnreachable)
                    continue;
                if (aggregate.Kind == AggregateKind.StringAgg && state.Aggregators[i] is Aggregators.StringAggAggregator stringAgg)
                {
                    var separatorValue = aggregate.Separator!.Run(rowRuntime);
                    Expressions.StringScalars.RejectLegacyLob(separatorValue, "string_agg", argumentIndex: 2);
                    stringAgg.SetSeparator(separatorValue.IsNull ? string.Empty : separatorValue.AsString);

                    if (aggregate.OrderBy is { } orderBy)
                    {
                        var orderKeys = new SqlValue[orderBy.Count];
                        for (var k = 0; k < orderBy.Count; k++)
                            orderKeys[k] = orderBy[k].Expr!.Run(rowRuntime);
                        stringAgg.AddOrdered(aggregate.Operand!.Run(rowRuntime), orderKeys);
                        continue;
                    }
                }

                // JSON_ARRAYAGG with an in-parens ORDER BY buffers each
                // value + ORDER BY tuple; the aggregator sorts at Result.
                if (aggregate.Kind == AggregateKind.JsonArrayAgg && aggregate.OrderBy is { } jsonOrderBy
                    && state.Aggregators[i] is Aggregators.JsonArrayAggAggregator arrayAgg)
                {
                    var orderKeys = new SqlValue[jsonOrderBy.Count];
                    for (var k = 0; k < jsonOrderBy.Count; k++)
                        orderKeys[k] = jsonOrderBy[k].Expr!.Run(rowRuntime);
                    arrayAgg.AddOrdered(aggregate.Operand!.Run(rowRuntime), orderKeys);
                    continue;
                }

                // JSON_OBJECTAGG needs the per-row key set before the value
                // is streamed; the value flows through the generic Add below.
                if (aggregate.Kind == AggregateKind.JsonObjectAgg
                    && state.Aggregators[i] is Aggregators.JsonObjectAggAggregator objectAgg)
                {
                    objectAgg.SetKey(aggregate.KeyExpression!.Run(rowRuntime));
                }

                var operand = aggregate.CountsRowsOnly ? null : aggregate.Operand;
                state.Aggregators[i].Add(operand is null ? SqlValue.Null(SqlType.Int32) : operand.Run(rowRuntime));
            }
        }

        foreach (var groupingSet in effectiveSets)
        {
            currentGroupingSet = groupingSet;
            Dictionary<SqlValueKey, GroupState> groups;
            if (streamedGroups is not null)
            {
                groups = streamedGroups;
            }
            else
            {
                groups = NewGroupMap(groupingSet);
                foreach (var tuple in buffered!)
                {
                    currentTuple = tuple;
                    Accumulate(groups, groupingSet, tuple, tupleIsShared: false);
                }
            }

            // Windowed grouped query: the windows run over this query's
            // *groups*, not its base rows, so the group stream has to be
            // materialized (post-HAVING — probe-confirmed that a window sees
            // only surviving groups) before any window value can be computed.
            // This set's survivors join the cross-set buffer; projection waits
            // for the window pass after the loop. Each survivor caches its
            // aggregate results so re-binding them for the window pass and
            // again for projection doesn't re-run Aggregator.Result() (which
            // sorts, for STRING_AGG / JSON_ARRAYAGG).
            if (windowSurvivors is not null)
            {
                var savedSetForWindows = batch.GroupingSetExpressions;
                var savedAllForWindows = batch.AllGroupingExpressions;
                batch.GroupingSetExpressions = groupingSet;
                batch.AllGroupingExpressions = fromClause.AllGroupingExpressions;
                try
                {
                    foreach (var (_, state) in groups)
                    {
                        currentState = state;
                        var aggregateValues = new SqlValue[aggregates.Count];
                        for (var i = 0; i < aggregates.Count; i++)
                        {
                            aggregateValues[i] = state.Aggregators[i].Result();
                            aggregates[i].BindResult(batch, aggregateValues[i]);
                        }

                        if (fromClause.Having is { } havingFilter && havingFilter.Run(groupRuntime) != true)
                            continue;

                        windowSurvivors.Add((groupingSet, state, aggregateValues));
                    }
                }
                finally
                {
                    batch.GroupingSetExpressions = savedSetForWindows;
                    batch.AllGroupingExpressions = savedAllForWindows;
                }

                continue;
            }

            foreach (var (_, state) in groups)
            {
                currentState = state;
                for (var i = 0; i < aggregates.Count; i++)
                    aggregates[i].BindResult(batch, state.Aggregators[i].Result());

                // Publish current grouping-set context so GROUPING() /
                // GROUPING_ID() in projection / HAVING expressions can read
                // it. Save & restore around this group's evaluation to keep
                // nested aggregate queries (correlated subqueries) clean.
                var savedSet = batch.GroupingSetExpressions;
                var savedAll = batch.AllGroupingExpressions;
                batch.GroupingSetExpressions = groupingSet;
                batch.AllGroupingExpressions = fromClause.AllGroupingExpressions;

                try
                {
                    if (fromClause.Having is { } having && having.Run(groupRuntime) != true)
                        continue;

                    // Under the bounded heap all but n of these groups are
                    // dropped, so the projection and key tuple are computed into
                    // reused scratch and only copied out on admission.
                    var projected = topNGroups is null ? new SqlValue[expressions.Count] : projectionScratch;
                    for (var i = 0; i < expressions.Count; i++)
                        projected[i] = expressions[i].Run(groupRuntime);

                    // Aggregate-query ORDER BY: keys are computed here, where
                    // each aggregate is bound and the grouping context is
                    // published, then the whole stream is sorted before TOP /
                    // OFFSET / FETCH apply. Ordinal items index the output row;
                    // expression items (aggregates, grouped columns, aliases,
                    // grouping expressions) resolve through ResolveOrderName.
                    currentProjected = projected;
                    var orderKeys = topNGroups is null ? new SqlValue[orderByItems.Count] : orderKeyScratch;
                    for (var k = 0; k < orderByItems.Count; k++)
                    {
                        orderKeys[k] = orderByItems[k].IsOrdinal
                            ? projected[orderByItems[k].Ordinal - 1]
                            : orderByItems[k].Expr!.Run(orderRuntime);
                    }

                    if (topNGroups is not null)
                        topNGroups.OfferCopying(projected, orderKeys);
                    else
                        output.Add((orderKeys, projected));
                }
                finally
                {
                    batch.GroupingSetExpressions = savedSet;
                    batch.AllGroupingExpressions = savedAll;
                }
            }
        }

        // Single window pass over the concatenated group stream, then
        // projection. One grouping set or many, the windows see one row set.
        if (windowSurvivors is not null)
        {
            var savedSetForWindows = batch.GroupingSetExpressions;
            var savedAllForWindows = batch.AllGroupingExpressions;
            batch.AllGroupingExpressions = fromClause.AllGroupingExpressions;
            try
            {
                // Re-points the group slot, restores that group's own grouping
                // set (so GROUPING() / GROUPING_ID() read the row's set rather
                // than whichever ran last), and re-binds its aggregate results
                // so a window operand's inner aggregate (`SUM(SUM(b))`) reads
                // the group's value.
                RuntimeContext RuntimeAtGroup(int index)
                {
                    var (groupingSet, state, aggregateValues) = windowSurvivors[index];
                    currentGroupingSet = groupingSet;
                    batch.GroupingSetExpressions = groupingSet;
                    currentState = state;
                    for (var i = 0; i < aggregates.Count; i++)
                        aggregates[i].BindResult(batch, aggregateValues[i]);
                    return groupRuntime;
                }

                var perWindowKeys = new List<(SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[]>(windowSurvivors.Count);
                for (var g = 0; g < windowSurvivors.Count; g++)
                {
                    var groupContext = RuntimeAtGroup(g);
                    var keys = new (SqlValue[] PartitionKeys, SqlValue[] OrderKeys)[windows.Count];
                    for (var w = 0; w < windows.Count; w++)
                    {
                        var win = windows[w];
                        var partitionKeys = new SqlValue[win.PartitionBy.Length];
                        for (var p = 0; p < win.PartitionBy.Length; p++)
                            partitionKeys[p] = win.PartitionBy[p].Run(groupContext);
                        var orderKeys = new SqlValue[win.OrderBy.Length];
                        for (var o = 0; o < win.OrderBy.Length; o++)
                            orderKeys[o] = win.OrderBy[o].Expr!.Run(groupContext);
                        keys[w] = (partitionKeys, orderKeys);
                    }
                    perWindowKeys.Add(keys);
                }

                var groupWindowResults = ComputeWindowResults(
                    windows, perWindowKeys, windowSurvivors.Count, RuntimeAtGroup, resolveColumnType, windowOperandTypes, windowResultTypes, batch);

                for (var g = 0; g < windowSurvivors.Count; g++)
                {
                    var groupContext = RuntimeAtGroup(g);
                    for (var w = 0; w < windows.Count; w++)
                        windows[w].BindResult(batch, groupWindowResults[w][g]);

                    var projectedGroup = new SqlValue[expressions.Count];
                    for (var i = 0; i < expressions.Count; i++)
                        projectedGroup[i] = expressions[i].Run(groupContext);

                    currentProjected = projectedGroup;
                    var groupOrderKeys = new SqlValue[orderByItems.Count];
                    for (var k = 0; k < orderByItems.Count; k++)
                    {
                        groupOrderKeys[k] = orderByItems[k].IsOrdinal
                            ? projectedGroup[orderByItems[k].Ordinal - 1]
                            : orderByItems[k].Expr!.Run(orderRuntime);
                    }

                    if (topNGroups is not null)
                        topNGroups.Offer(projectedGroup, groupOrderKeys);
                    else
                        output.Add((groupOrderKeys, projectedGroup));
                }
            }
            finally
            {
                batch.GroupingSetExpressions = savedSetForWindows;
                batch.AllGroupingExpressions = savedAllForWindows;
            }
        }

        // The bounded heap already holds exactly the rows the sort-then-cap
        // below would have kept, in the same order, so it answers on its own.
        if (topNGroups is not null)
            return [.. topNGroups.Drain()];

        // DISTINCT dedupes the *grouped* projection, and does so before ORDER
        // BY and any row limiting: `SELECT DISTINCT YEAR(pubdate) … GROUP BY id,
        // pubdate` collapses one row per group to one row per distinct year
        // (probe-confirmed). Grouping alone doesn't imply distinct output —
        // the projection can be narrower than the grouping key.
        if (distinct && output.Count > 1)
        {
            var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
            _ = output.RemoveAll(o => !seen.Add(o.Row));
        }

        // ORDER BY sorts the full grouped stream (across all grouping sets)
        // before any row-count limiting — so TOP / FETCH select the correct
        // rows rather than an arbitrary prefix.
        if (orderByItems.Count > 0)
            output.Sort((a, b) => CompareOrderKeys(a.OrderKeys, b.OrderKeys, orderByItems));

        IEnumerable<(SqlValue[] OrderKeys, SqlValue[] Row)> limited = output;
        if (ComputeTopCap(output, o => o.OrderKeys, orderByItems, top, fetchCount: null) is { } topLimit)
            limited = limited.Take(topLimit);
        if (offsetCount is { } offset && offset > 0)
            limited = limited.Skip(offset);
        if (fetchCount is { } fetchLimit)
            limited = limited.Take(fetchLimit);

        return [.. limited.Select(o => o.Row)];
    }

    /// <summary>
    /// Per-group state inside <c>BuildAggregateProjectionRows</c>: the
    /// resolved key tuple (used to populate non-aggregate projection slots
    /// from the GROUP BY's column references) plus one aggregator per
    /// <see cref="AggregateExpression"/> in the projection.
    /// </summary>
    private sealed class GroupState(SqlValue[] keyValues, Aggregator[] aggregators)
    {
        public readonly SqlValue[] KeyValues = keyValues;
        public readonly Aggregator[] Aggregators = aggregators;

        /// <summary>
        /// First input row that landed in this group, used to resolve columns
        /// buried inside a grouping expression (constant within the group).
        /// </summary>
        public byte[]?[]? Representative;

        /// <summary>
        /// Ordinal of the input row that created this group, counted from the
        /// start of the enumeration. Written only by the parallel accumulation
        /// (<c>Selection.Execution.AggregateParallel.cs</c>), whose merge uses
        /// it to keep the lower-ordinal group's <see cref="KeyValues"/> and
        /// <see cref="Representative"/> — both of which are the creating row's
        /// and are observable when the key's equality is coarser than its
        /// rendering (a case-insensitive collation groups <c>'abc'</c> with
        /// <c>'ABC'</c> and projects whichever row arrived first). The serial
        /// path leaves it at zero and never reads it, its scan order being
        /// the answer by construction.
        /// </summary>
        public long FirstRowOrdinal;
    }
}
