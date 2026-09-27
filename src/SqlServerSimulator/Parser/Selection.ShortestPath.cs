using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// MATCH(SHORTEST_PATH(…)): the FOR PATH edge and node sources give way to one
// lateral source, run per start row, whose rows are the nodes the start reaches
// with each path's graph path aggregates alongside. See docs/claude/graph.md.
internal sealed partial class Selection
{
    /// <summary>
    /// Settles the query's <c>SHORTEST_PATH</c>, if its WHERE bound one: the
    /// <c>FOR PATH</c> sources' refusals, then the rewrite of the two recursive
    /// sources into the path source and of each graph path aggregate into a
    /// read of the column carrying its value. Runs once the WHERE is parsed and
    /// before <c>SELECT *</c> expands, which leaves the path's columns out.
    /// </summary>
    private static void ApplyShortestPath(ParserContext context, QueryScope scope, List<FromSource> sources, List<JoinSpec> joins, List<Expression> expressions, FromClause fromClause)
    {
        var aggregates = context.GraphPathAggregates ?? [];
        var spec = fromClause.Match?.ShortestPath;
        var anyForPath = false;
        foreach (var source in sources)
        {
            if (!source.ForPath)
                continue;
            anyForPath = true;
            if (spec is not null && (ReferenceEquals(source, spec.Edge) || ReferenceEquals(source, spec.Node)))
                continue;
            // A FOR PATH source a SHORTEST_PATH names outside its recursive
            // section reads by its alias; one it never names by its whole name.
            throw spec is not null && ReferenceEquals(source, spec.Start)
                ? SimulatedSqlException.ForPathSourceUnused(source.Qualifier!, 2)
                : SimulatedSqlException.ForPathSourceUnused(ForPathDisplayName(context, source), 1);
        }
        if (spec is null)
        {
            if (aggregates.Count > 0)
                throw SimulatedSqlException.GraphPathAggregateWithoutPath(aggregates[0].LowerName);
            return;
        }
        // A derived table or CTE may recurse; a subquery may not (probed
        // 2026-09-27 against SQL Server 2025).
        if (scope.Position is QueryPosition.Subquery or QueryPosition.Exists)
            throw SimulatedSqlException.RecursiveMatchInSubquery();
        if (!anyForPath || spec.Edge.BackingTable is not { } edgeTable || spec.Node.BackingTable is not { } nodeTable)
            throw new NotSupportedException("A SHORTEST_PATH over anything but FOR PATH base tables isn't modeled.");
        if (spec.Start.BackingTable is not { GraphKind: GraphTableKind.Node } startTable)
            throw new NotSupportedException("A SHORTEST_PATH starting from anything but a node table isn't modeled.");

        var collation = context.Batch.CurrentDatabase.Collation;
        bool OnPath(MultiPartName name) =>
            name.ImmediateQualifier is { } qualifier
            && (collation.Equals(qualifier, spec.Edge.Qualifier!) || collation.Equals(qualifier, spec.Node.Qualifier!));

        // A path column is legal only inside a graph path aggregate, whose own
        // operand has to read the path and nothing else.
        void RejectPathColumns(ExpressionNode root) =>
            root.Walk((node, _) => node switch
            {
                GraphPathAggregate or MatchPredicate => false,
                Reference { ReferencedName: var name } when OnPath(name) => throw SimulatedSqlException.ForPathColumnOutsideAggregate(name.ToString()),
                _ => true,
            });
        foreach (var expression in expressions)
            RejectPathColumns(expression);
        // A WHERE may not read the path even through an aggregate.
        foreach (var excluder in fromClause.Excluders)
        {
            excluder.Walk((node, _) =>
            {
                if (node is GraphPathAggregate inWhere)
                    RejectPathColumns(inWhere.Operand);
                if (node is Reference { ReferencedName: var name } && OnPath(name))
                    throw SimulatedSqlException.ForPathColumnOutsideAggregate(name.ToString());
                return node is not MatchPredicate;
            });
        }
        foreach (var order in fromClause.OrderBy)
        {
            if (order.Expr is { } orderExpression)
                RejectPathColumns(orderExpression);
        }
        foreach (var aggregate in aggregates)
        {
            var readsPath = false;
            aggregate.Operand.VisitColumnReferences(name =>
            {
                if (!OnPath(name))
                    throw SimulatedSqlException.GraphPathAggregateReadsOffPath(name.ToString(), aggregate.LowerName);
                readsPath = true;
            });
            if (!readsPath)
                throw SimulatedSqlException.GraphPathAggregateWithoutPath(aggregate.LowerName);
        }

        // The path source: the reached node's own columns, then one column per
        // aggregate, every one hidden from SELECT *.
        var originalTypes = ColumnTypeResolverFor([.. sources]);
        var nodeColumnCount = nodeTable.Columns.Length;
        var columns = new HeapColumn[nodeColumnCount + aggregates.Count];
        var schema = new SqlType[columns.Length];
        var names = new string[columns.Length];
        for (var i = 0; i < nodeColumnCount; i++)
        {
            var column = nodeTable.Columns[i];
            columns[i] = new HeapColumn(column.Name, column.Type, column.MaxLength, column.Nullable, isHidden: true);
        }
        var operandTypes = new SqlType[aggregates.Count];
        for (var a = 0; a < aggregates.Count; a++)
        {
            var aggregate = aggregates[a];
            operandTypes[a] = aggregate.Operand.GetSqlType(context.Batch, originalTypes);
            var resultType = aggregate.GetSqlType(context.Batch, originalTypes);
            columns[nodeColumnCount + a] = new HeapColumn($"graph_path_{a}", resultType, VariableLength(resultType), nullable: true, isHidden: true);
        }
        for (var i = 0; i < columns.Length; i++)
        {
            schema[i] = columns[i].Type;
            names[i] = columns[i].Name;
        }

        var path = new ShortestPathPlan(
            startTable, spec.Start.Qualifier!, edgeTable, spec.Edge.Qualifier!, nodeTable, spec.Node.Qualifier!,
            spec.Forward, spec.MaxHops, [.. aggregates], operandTypes, schema);
        var plan = new Selection(schema, names, hasOrderBy: false, hasTopOrOffsetOrFetch: false, path.Rows);
        var pathSource = new FromSource(
            qualifier: spec.Node.Qualifier,
            columnNames: names,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);

        for (var a = 0; a < aggregates.Count; a++)
            aggregates[a].Bound = new Reference(spec.Node.Qualifier!, names[nodeColumnCount + a]);

        // Drop the two recursive sources — comma-joined, as MATCH requires —
        // and apply the path source after everything else.
        foreach (var recursive in new[] { spec.Edge, spec.Node })
        {
            var index = sources.IndexOf(recursive);
            sources.RemoveAt(index);
            if (joins.Count > 0)
                joins.RemoveAt(index > 0 ? index - 1 : 0);
        }
        sources.Add(pathSource);
        joins.Add(new JoinSpec(JoinKind.CrossApply, null));
    }

    /// <summary>
    /// Consumes <c>FOR PATH</c> after a table name, the cursor on the name's
    /// last part and left on <c>PATH</c>; leaves the cursor alone otherwise.
    /// </summary>
    private static bool ParseOptionalForPath(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is Tokens.ReservedKeyword { Keyword: Keyword.For }
            && context.GetNextOptional() is Tokens.UnquotedString { Value: var path } && path.Equals("PATH", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        context.RestoreCheckpoint(checkpoint);
        return false;
    }

    private static string ForPathDisplayName(ParserContext context, FromSource source) =>
        source.BackingTable is { } table
            ? $"{context.Batch.DatabaseFor(table).Name}.{GraphColumns.SchemaOf(context.Batch.DatabaseFor(table), table)?.Name ?? Database.DefaultSchemaName}.{table.Name}"
            : source.Qualifier ?? string.Empty;

    private static int? VariableLength(SqlType type) => type switch
    {
        VarcharSqlType varchar => varchar.length,
        NVarcharSqlType nvarchar => nvarchar.length,
        VarbinarySqlType varbinary => varbinary.length,
        _ => null,
    };

    /// <summary>
    /// What a <c>SHORTEST_PATH</c>'s rows are computed from, and the
    /// computation: a breadth-first walk out of each start row over the edge
    /// table, one row per node reached — the start itself included when a cycle
    /// leads back to it — carrying the path that reached it first.
    /// </summary>
    private sealed class ShortestPathPlan(
        HeapTable startTable,
        string startQualifier,
        HeapTable edgeTable,
        string edgeQualifier,
        HeapTable nodeTable,
        string nodeQualifier,
        bool forward,
        int? maxHops,
        GraphPathAggregate[] aggregates,
        SqlType[] operandTypes,
        SqlType[] schema)
    {
        private readonly MultiPartName startGraphId =
            new MultiPartName(startQualifier).WithAddedPart(startTable.Columns[GraphColumns.OrdinalOf(startTable, GraphColumnKind.GraphId)].Name);

        /// <summary>
        /// The edges out of (walking backwards, into) each node and the path
        /// node table's rows by graph id, as of the two heaps' mutation
        /// generations — rebuilt when either table changes, so repeated
        /// executions and every start row of one share the read.
        /// </summary>
        private sealed class GraphSnapshot(long edgeGeneration, long nodeGeneration)
        {
            public readonly long EdgeGeneration = edgeGeneration;
            public readonly long NodeGeneration = nodeGeneration;
            public readonly Dictionary<(int, long), List<((int, long) Target, byte[] Edge)>> Adjacency = [];
            public readonly Dictionary<long, byte[]> Nodes = [];
        }

        private volatile GraphSnapshot? snapshot;

        private GraphSnapshot Snapshot()
        {
            if (this.snapshot is { } cached
                && cached.EdgeGeneration == edgeTable.Heap.MutationGeneration
                && cached.NodeGeneration == nodeTable.Heap.MutationGeneration)
            {
                return cached;
            }
            var built = new GraphSnapshot(edgeTable.Heap.MutationGeneration, nodeTable.Heap.MutationGeneration);
            var (fromObj, fromId, toObj, toId) = (
                edgeTable.StorageOrdinals[GraphColumns.OrdinalOf(edgeTable, forward ? GraphColumnKind.FromObjId : GraphColumnKind.ToObjId)],
                edgeTable.StorageOrdinals[GraphColumns.OrdinalOf(edgeTable, forward ? GraphColumnKind.FromId : GraphColumnKind.ToId)],
                edgeTable.StorageOrdinals[GraphColumns.OrdinalOf(edgeTable, forward ? GraphColumnKind.ToObjId : GraphColumnKind.FromObjId)],
                edgeTable.StorageOrdinals[GraphColumns.OrdinalOf(edgeTable, forward ? GraphColumnKind.ToId : GraphColumnKind.FromId)]);
            foreach (var edge in ClusteredScan.Rows(edgeTable))
            {
                var key = (Decode(edgeTable, edge, fromObj).AsInt32, Decode(edgeTable, edge, fromId).AsInt64);
                if (!built.Adjacency.TryGetValue(key, out var outgoing))
                    built.Adjacency[key] = outgoing = [];
                outgoing.Add(((Decode(edgeTable, edge, toObj).AsInt32, Decode(edgeTable, edge, toId).AsInt64), edge));
            }
            var graphId = nodeTable.StorageOrdinals[GraphColumns.OrdinalOf(nodeTable, GraphColumnKind.GraphId)];
            foreach (var node in ClusteredScan.Rows(nodeTable))
                built.Nodes[Decode(nodeTable, node, graphId).AsInt64] = node;
            this.snapshot = built;
            return built;
        }

        private static SqlValue Decode(HeapTable table, byte[] row, int storageOrdinal) =>
            RowDecoder.DecodeColumn(table.StoredColumns, row, storageOrdinal, table.Heap);

        public IEnumerable<byte[]> Rows(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
        {
            var startId = outerResolver?.Invoke(this.startGraphId) ?? throw new InvalidOperationException("A SHORTEST_PATH ran without its start row.");
            if (startId.IsNull)
                yield break;
            var start = (startTable.ObjectId, startId.CoerceTo(SqlType.BigInt).AsInt64);
            var graph = this.Snapshot();

            var reachedBy = new Dictionary<(int, long), ((int, long) Previous, byte[] Edge)>();
            List<(int, long)> frontier = [start];
            for (var depth = 1; frontier.Count > 0 && (maxHops is null || depth <= maxHops); depth++)
            {
                List<(int, long)> next = [];
                foreach (var from in frontier)
                {
                    if (!graph.Adjacency.TryGetValue(from, out var outgoing))
                        continue;
                    foreach (var (target, edge) in outgoing)
                    {
                        if (target.Item1 != nodeTable.ObjectId || !graph.Nodes.ContainsKey(target.Item2) || !reachedBy.TryAdd(target, (from, edge)))
                            continue;
                        next.Add(target);
                        yield return this.Row(batch, start, target, reachedBy, graph.Nodes);
                    }
                }
                frontier = next;
            }
        }

        private byte[] Row(
            BatchContext batch,
            (int, long) start,
            (int, long) reached,
            Dictionary<(int, long), ((int, long) Previous, byte[] Edge)> reachedBy,
            Dictionary<long, byte[]> nodes)
        {
            // The reached node's stored columns; its computed ones read NULL,
            // since nothing outside the graph path aggregates may read them and
            // LAST_NODE matches on the stored graph id.
            var values = new SqlValue[schema.Length];
            var nodeRow = nodes[reached.Item2];
            var nodeColumns = nodeTable.Columns.Length;
            for (var c = 0; c < nodeColumns; c++)
            {
                var storage = nodeTable.StorageOrdinals[c];
                values[c] = storage >= 0 ? Decode(nodeTable, nodeRow, storage) : SqlValue.Null(nodeTable.Columns[c].Type);
            }
            if (aggregates.Length == 0)
                return RowEncoder.EncodeRow(schema, values);

            // The path's steps, first to last.
            var steps = new List<(byte[] Edge, byte[] Node)>();
            var at = reached;
            do
            {
                var (previous, edge) = reachedBy[at];
                steps.Add((edge, nodes[at.Item2]));
                at = previous;
            }
            while (at != start);
            steps.Reverse();

            var step = default((byte[] Edge, byte[] Node));
            var runtime = new RuntimeContext(name => this.ResolveStep(name, step, batch), batch);
            for (var a = 0; a < aggregates.Length; a++)
            {
                var aggregate = aggregates[a];
                var resultType = schema[nodeColumns + a];
                if (aggregate.Aggregate is not { } ordinary)
                {
                    step = steps[^1];
                    values[nodeColumns + a] = aggregate.Operand.Run(runtime).CoerceTo(resultType);
                    continue;
                }
                var aggregator = Aggregator.Create(ordinary, operandTypes[a], resultType, batch: batch);
                foreach (var each in steps)
                {
                    step = each;
                    if (aggregator is Aggregators.StringAggAggregator stringAgg)
                    {
                        var separator = ordinary.Separator!.Run(runtime);
                        stringAgg.SetSeparator(separator.IsNull ? string.Empty : separator.AsString);
                    }
                    aggregator.Add(aggregate.Operand.Run(runtime));
                }
                values[nodeColumns + a] = aggregator.Result();
            }
            return RowEncoder.EncodeRow(schema, values);
        }

        /// <summary>
        /// A column of one path step, read off the step's row — decoding just
        /// that column, or evaluating a computed one over the row's own columns.
        /// </summary>
        private SqlValue ResolveStep(MultiPartName name, (byte[] Edge, byte[] Node) step, BatchContext batch)
        {
            var qualifier = name.ImmediateQualifier;
            var (table, row) = qualifier is not null && BuiltInToken.Equals(qualifier, edgeQualifier) ? (edgeTable, step.Edge)
                : qualifier is not null && BuiltInToken.Equals(qualifier, nodeQualifier) ? (nodeTable, step.Node)
                : throw SimulatedSqlException.InvalidColumnName(name);
            var ordinal = ColumnOrdinal(table, name.Leaf);
            return ordinal < 0 ? throw SimulatedSqlException.InvalidColumnName(name) : Column(table, row, ordinal, batch);
        }

        private static int ColumnOrdinal(HeapTable table, string leaf)
        {
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (BuiltInToken.Equals(table.Columns[i].Name, leaf) || GraphColumns.IsPseudoColumnFor(table.Columns[i].Name, leaf))
                    return i;
            }
            return -1;
        }

        private static SqlValue Column(HeapTable table, byte[] row, int ordinal, BatchContext batch)
        {
            var storage = table.StorageOrdinals[ordinal];
            return storage >= 0
                ? Decode(table, row, storage)
                : table.Columns[ordinal].Computed!.Run(new RuntimeContext(name => Column(table, row, ColumnOrdinal(table, name.Leaf), batch), batch));
        }
    }
}
