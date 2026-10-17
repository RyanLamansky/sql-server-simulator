using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// MATCH(SHORTEST_PATH(…)): each path's FOR PATH edges and nodes give way to
// one lateral source, run per start row, whose rows are the nodes the start
// reaches with each path's graph path aggregates alongside. Also what MATCH
// asks of the rest of the plan: its node tables' outer joins, and the graph
// identity a body carries for a MATCH to read through. See
// docs/claude/graph.md.
internal sealed partial class Selection
{
    /// <summary>
    /// How many hidden columns carrying a node or edge table's identity trail
    /// this body's own (see <see cref="AppendGraphIdentityColumns"/>): one for
    /// a node table's graph id, four for an edge table's endpoint pairs.
    /// </summary>
    internal int GraphIdentityColumnCount;

    /// <summary>The node or edge table whose identity <see cref="GraphIdentityColumnCount"/> columns carry.</summary>
    internal HeapTable? GraphIdentityTable;

    /// <summary>How many hidden columns trail this body's own: a browse statement's, or a graph identity's.</summary>
    internal int TrailingHiddenColumnCount => this.BrowseFlattened ? this.HiddenColumnCount : this.GraphIdentityColumnCount;

    /// <summary>The name a FROM source over this body gives the hidden column at <paramref name="ordinal"/>.</summary>
    internal string TrailingHiddenName(int ordinal) => this.BrowseFlattened
        ? BrowseHiddenName(ordinal)
        : GraphIdentityName(ordinal - (this.Schema.Length - this.GraphIdentityColumnCount));

    /// <summary>The name of a body's graph identity column <paramref name="index"/>, one no identifier can spell.</summary>
    internal static string GraphIdentityName(int index) => "\u0001graph" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly GraphColumnKind[] NodeIdentityKinds = [GraphColumnKind.GraphId];

    private static readonly GraphColumnKind[] EdgeIdentityKinds = [GraphColumnKind.FromObjId, GraphColumnKind.FromId, GraphColumnKind.ToObjId, GraphColumnKind.ToId];

    /// <summary>The identity columns, in order, a body over <paramref name="table"/> carries.</summary>
    internal static GraphColumnKind[] GraphIdentityKinds(HeapTable table) =>
        table.GraphKind == GraphTableKind.Node ? NodeIdentityKinds : EdgeIdentityKinds;

    /// <summary>
    /// Appends, to a body reading one node or edge table — directly, or through
    /// a body that carries its identity in turn — that table's identity columns
    /// as hidden trailing columns, so a <c>MATCH</c> reads through a derived
    /// table, view or CTE that projects no pseudo-column, or projects one under
    /// another name (probed 2026-10-16 against SQL Server 2025); returns how
    /// many it appended.
    /// </summary>
    private static int AppendGraphIdentityColumns(FromSource[] sources, List<Expression> expressions, out HeapTable? table)
    {
        table = null;
        if (sources is not [{ Qualifier: { } qualifier } source])
            return 0;
        if (source.BackingTable is { GraphKind: GraphTableKind.Node or GraphTableKind.Edge } graph)
        {
            foreach (var kind in GraphIdentityKinds(graph))
                expressions.Add(new Reference(qualifier, graph.Columns[GraphColumns.OrdinalOf(graph, kind)].Name));
            table = graph;
        }
        else if (source.GraphIdentityTable is { } inner)
        {
            var count = GraphIdentityKinds(inner).Length;
            for (var i = 0; i < count; i++)
                expressions.Add(new Reference(qualifier, GraphIdentityName(i)));
            table = inner;
        }
        return table is null ? 0 : GraphIdentityKinds(table).Length;
    }

    /// <summary>
    /// Settles the query's <c>SHORTEST_PATH</c>s, if its WHERE bound any: the
    /// <c>FOR PATH</c> sources' refusals and the <c>LAST_NODE</c> references',
    /// then the rewrite of each path's recursive sources into its path source
    /// and of each graph path aggregate into a read of the column carrying its
    /// value. Runs once the WHERE is parsed and before <c>SELECT *</c> expands,
    /// which leaves the paths' columns out.
    /// </summary>
    private static List<(ShortestPathSpec Spec, ShortestPathPlan Plan)> ApplyShortestPath(ParserContext context, QueryScope scope, List<FromSource> sources, List<JoinSpec> joins, List<Expression> expressions, FromClause fromClause)
    {
        var aggregates = context.GraphPathAggregates ?? [];
        var match = fromClause.Match;
        var specs = match?.ShortestPaths ?? [];
        bool Recursive(FromSource source) => specs.Exists(spec => spec.Recurses(source));
        // LAST_NODE names a path's last node — at a hop, a path's start or
        // either side of an equality — and a recursive section's node is
        // named nowhere else (probed 2026-10-11 against SQL Server 2025).
        bool EndsAPath(FromSource source) => specs.Exists(spec => ReferenceEquals(spec.End, source));
        if (match is not null)
        {
            foreach (var spec in specs)
            {
                if (spec.StartIsLastNode && !EndsAPath(spec.Start))
                {
                    throw SimulatedSqlException.AndMatchError(
                        SimulatedSqlException.LastNodeNotAPathEnd(spec.StartWritten, 2),
                        SimulatedSqlException.LastNodeNotAPathEnd(spec.StartWritten, 1));
                }
                if (!spec.StartIsLastNode && Recursive(spec.Start))
                    throw SimulatedSqlException.LastNodeRequired(spec.StartWritten, 1);
            }
            foreach (var (source, written, state) in match.LastNodeUses)
            {
                if (!EndsAPath(source))
                    throw SimulatedSqlException.LastNodeNotAPathEnd(written, state);
            }
            foreach (var (left, leftWritten, right, rightWritten) in match.LastNodeEqualities)
            {
                if (specs.Count == 0)
                    throw SimulatedSqlException.LastNodeNotAPathEnd(leftWritten, 11);
                if (!EndsAPath(left))
                    throw SimulatedSqlException.LastNodeNotAPathEnd(leftWritten, 6);
                if (!EndsAPath(right))
                    throw SimulatedSqlException.LastNodeRequired(rightWritten, 7);
                if (!ReferenceEquals(UnderlyingObject(left), UnderlyingObject(right)))
                    throw SimulatedSqlException.LastNodeEqualityOverDifferentObjects(leftWritten, rightWritten);
            }
            foreach (var source in match.ForPathNodeUses)
            {
                if (Recursive(source))
                    throw SimulatedSqlException.ForPathNodeOutsideRecursiveSection(source.Qualifier!);
            }
        }
        // A query with no MATCH reads its FOR PATH sources as ordinary ones
        // (probed 2026-10-16 against SQL Server 2025).
        foreach (var source in match is null ? [] : sources)
        {
            if (!source.ForPath || Recursive(source))
                continue;
            // A FOR PATH source a SHORTEST_PATH names outside its recursive
            // section reads by its alias; one it never names by its whole name.
            throw specs.Exists(spec => ReferenceEquals(spec.Start, source))
                ? SimulatedSqlException.ForPathSourceUnused(source.Qualifier!, 2)
                : SimulatedSqlException.ForPathSourceUnused(ForPathDisplayName(context, source), 1);
        }

        if (specs.Count == 0)
        {
            if (aggregates.Count > 0)
                throw SimulatedSqlException.GraphPathAggregateWithoutPath(aggregates[0].LowerName);
            return [];
        }
        // A derived table or CTE may recurse; a subquery may not (probed
        // 2026-09-27 against SQL Server 2025).
        if (scope.Position is QueryPosition.Subquery or QueryPosition.Exists)
            throw SimulatedSqlException.RecursiveMatchInSubquery();

        // Real can't walk a node or edge table a row-level security filter
        // covers: the recursive section's node is Msg 4104 at state 3 ahead
        // of its edge at state 2, as the batch compiles (probed 2026-10-16
        // against SQL Server 2025).
        if (context.Batch.Connection.Simulation.DeclaresSecurityPolicies)
        {
            foreach (var edges in (ReadOnlySpan<bool>)[false, true])
            {
                foreach (var spec in specs)
                {
                    foreach (var hop in spec.Hops)
                    {
                        var walked = edges ? hop.Edge : hop.Node;
                        if (walked.BackingTable is { } table && RowSecurity.For(context.Batch, table)?.Filter is not null)
                            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(walked.Qualifier!, edges ? (byte)2 : (byte)3);
                    }
                }
            }
        }

        var collation = context.Batch.CurrentDatabase.Collation;
        ShortestPathSpec? PathOf(MultiPartName name)
        {
            if (name.ImmediateQualifier is not { } qualifier)
                return null;
            foreach (var spec in specs)
            {
                foreach (var hop in spec.Hops)
                {
                    if (collation.Equals(qualifier, hop.Edge.Qualifier!) || collation.Equals(qualifier, hop.Node.Qualifier!))
                        return spec;
                }
            }
            return null;
        }

        // A path column is legal only inside a graph path aggregate, whose own
        // operand has to read one path and nothing else. Every offending name
        // is reported.
        SimulatedSqlException? outside = null;
        void RejectPathColumns(ExpressionNode root) =>
            root.Walk((node, _) =>
            {
                switch (node)
                {
                    case GraphPathAggregate or MatchPredicate:
                        return false;
                    case Reference { ReferencedName: var name } when PathOf(name) is not null:
                        var error = SimulatedSqlException.ForPathColumnOutsideAggregate(name.ToString());
                        outside = outside is null ? error : SimulatedSqlException.AndMatchError(outside, error);
                        return true;
                    default:
                        return true;
                }
            });
        // Real reports them clause by clause, ORDER BY first and the select
        // list last, each clause's in the order written (probed 2026-10-11
        // against SQL Server 2025).
        foreach (var order in fromClause.OrderBy)
        {
            if (order.Expr is { } orderExpression)
                RejectPathColumns(orderExpression);
        }
        foreach (var grouping in fromClause.AllGroupingExpressions)
            RejectPathColumns(grouping);
        if (fromClause.Having is { } having)
            RejectPathColumns(having);
        // A WHERE may not read the path even through an aggregate.
        foreach (var excluder in fromClause.Excluders)
        {
            excluder.Walk((node, _) =>
            {
                if (node is GraphPathAggregate inWhere)
                {
                    RejectPathColumns(inWhere.Operand);
                    return false;
                }
                if (node is Reference { ReferencedName: var name } && PathOf(name) is not null)
                {
                    var error = SimulatedSqlException.ForPathColumnOutsideAggregate(name.ToString());
                    outside = outside is null ? error : SimulatedSqlException.AndMatchError(outside, error);
                }
                return node is not MatchPredicate;
            });
        }
        foreach (var expression in expressions)
            RejectPathColumns(expression);
        if (outside is not null)
            throw outside;
        var aggregatePaths = new ShortestPathSpec[aggregates.Count];
        for (var a = 0; a < aggregates.Count; a++)
        {
            var aggregate = aggregates[a];
            ShortestPathSpec? read = null;
            aggregate.Operand.VisitColumnReferences(name =>
            {
                if (PathOf(name) is not { } spec)
                    throw SimulatedSqlException.GraphPathAggregateReadsOffPath(name.ToString(), aggregate.LowerName);
                if (read is not null && !ReferenceEquals(read, spec))
                    throw SimulatedSqlException.GraphPathAggregateReadsTwoPaths(aggregate.LowerName);
                read = spec;
            });
            aggregatePaths[a] = read ?? throw SimulatedSqlException.GraphPathAggregateWithoutPath(aggregate.LowerName);
        }

        // A path starting from another's last node runs after it, whichever
        // the MATCH wrote first.
        var ordered = new List<ShortestPathSpec>(specs.Count);
        while (ordered.Count < specs.Count)
        {
            var progressed = false;
            foreach (var spec in specs)
            {
                if (ordered.Contains(spec) || (spec.StartIsLastNode && specs.Find(other => ReferenceEquals(other.End, spec.Start)) is { } before && !ordered.Contains(before)))
                    continue;
                ordered.Add(spec);
                progressed = true;
            }
            if (!progressed)
                throw SimulatedSqlException.LastNodeNotAPathEnd(specs.Find(spec => !ordered.Contains(spec))!.StartWritten, 1);
        }

        var originalTypes = ColumnTypeResolverFor([.. sources]);
        var applied = new List<(ShortestPathSpec, ShortestPathPlan)>(ordered.Count);
        foreach (var spec in ordered)
        {
            var pathAggregates = new List<GraphPathAggregate>();
            for (var a = 0; a < aggregates.Count; a++)
            {
                if (ReferenceEquals(aggregatePaths[a], spec))
                    pathAggregates.Add(aggregates[a]);
            }

            // The path source: the reached node's own columns, its identity,
            // then one column per aggregate, every one hidden from SELECT *.
            var end = spec.End;
            var nodeColumnCount = end.Columns.Length;
            var columns = new HeapColumn[nodeColumnCount + 2 + pathAggregates.Count];
            for (var i = 0; i < nodeColumnCount; i++)
            {
                var column = end.Columns[i];
                columns[i] = new HeapColumn(end.ColumnNames[i], column.Type, column.MaxLength, nullable: true, isHidden: true);
            }
            columns[nodeColumnCount] = new HeapColumn(ShortestPathSpec.ObjectColumn, SqlType.Int32, maxLength: null, nullable: false, isHidden: true);
            columns[nodeColumnCount + 1] = new HeapColumn(ShortestPathSpec.IdColumn, SqlType.BigInt, maxLength: null, nullable: false, isHidden: true);
            var operandTypes = new SqlType[pathAggregates.Count];
            for (var a = 0; a < pathAggregates.Count; a++)
            {
                var aggregate = pathAggregates[a];
                operandTypes[a] = aggregate.Operand.GetSqlType(context.Batch, originalTypes);
                var resultType = aggregate.GetSqlType(context.Batch, originalTypes);
                columns[nodeColumnCount + 2 + a] = new HeapColumn($"graph_path_{a}", resultType, VariableLength(resultType), nullable: true, isHidden: true);
            }
            var schema = new SqlType[columns.Length];
            var names = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                schema[i] = columns[i].Type;
                names[i] = columns[i].Name;
            }

            var path = new ShortestPathPlan(spec, context.Batch.CurrentDatabase, [.. pathAggregates], operandTypes, schema);
            applied.Add((spec, path));
            var plan = new Selection(schema, names, hasOrderBy: false, hasTopOrOffsetOrFetch: false, path.Rows);
            var pathSource = new FromSource(
                qualifier: end.Qualifier,
                columnNames: names,
                columns: columns,
                storedSchema: columns,
                storageOrdinals: null,
                lobStore: null,
                rows: [],
                lateralPlan: plan);
            for (var a = 0; a < pathAggregates.Count; a++)
                pathAggregates[a].Bound = new Reference(end.Qualifier!, names[nodeColumnCount + 2 + a]);

            // Drop the recursive sources — comma-joined, as MATCH requires —
            // and apply the path source after everything else.
            foreach (var hop in spec.Hops)
            {
                foreach (var recursive in (ReadOnlySpan<FromSource>)[hop.Edge, hop.Node])
                {
                    var index = sources.IndexOf(recursive);
                    sources.RemoveAt(index);
                    if (joins.Count > 0)
                        joins.RemoveAt(index > 0 ? index - 1 : 0);
                }
            }
            sources.Add(pathSource);
            joins.Add(new JoinSpec(JoinKind.CrossApply, null));
        }
        return applied;
    }

    /// <summary>The qualifier of <see cref="StartPathsFromMissingNodes"/>'s source, a name no identifier can spell.</summary>
    private const string MissingStartQualifier = "\u001Dgraph_path_missing_start";

    /// <summary>
    /// Starts a path from each node its table no longer holds that the first
    /// hop's edges still leave — real starts one there as from any node,
    /// reporting the start's columns, its <c>$node_id</c> included, as NULL
    /// (probed 2026-10-17 against SQL Server 2025). The missing starts come
    /// from a source <c>FULL JOIN</c>ed to the start's node table on a false
    /// condition, so they add rows of their own where the start's are
    /// NULL-filled; the path reads its start's graph id there. Applied when
    /// the start is a node table leading the FROM clause and the first hop's
    /// edges are a table's.
    /// </summary>
    private static void StartPathsFromMissingNodes(List<(ShortestPathSpec Spec, ShortestPathPlan Plan)> paths, List<FromSource> sources, List<JoinSpec> joins)
    {
        foreach (var (spec, plan) in paths)
        {
            if (spec.StartIsLastNode
                || spec.Start is not { BackingTable: { GraphKind: GraphTableKind.Node } nodeTable, LateralPlan: null } start
                || spec.Hops[0].Edge is not { BackingTable: { } edgeTable, LateralPlan: null }
                || sources.Count == 0 || !ReferenceEquals(sources[0], start))
            {
                continue;
            }
            var forward = spec.Hops[0].Forward;
            var columns = new[] { new HeapColumn(ShortestPathSpec.IdColumn, SqlType.BigInt, maxLength: null, nullable: false, isHidden: true) };
            SqlType[] schema = [SqlType.BigInt];
            string[] names = [ShortestPathSpec.IdColumn];
            var missing = new Selection(schema, names, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
                (_, _) => MissingStartRows(nodeTable, edgeTable, forward, schema));
            sources.Insert(1, new FromSource(
                qualifier: MissingStartQualifier,
                columnNames: names,
                columns: columns,
                storedSchema: columns,
                storageOrdinals: null,
                lobStore: null,
                rows: [],
                lateralPlan: missing));
            joins.Insert(0, new JoinSpec(JoinKind.Full, BooleanExpression.Equality(new Value(SqlValue.FromInt32(1)), new Value(SqlValue.FromInt32(0)))));
            plan.MissingStarts = MissingStartQualifier;
            return;
        }
    }

    /// <summary>
    /// The graph ids, ascending, that <paramref name="edgeTable"/>'s edges
    /// leave (walking backwards, enter) in <paramref name="nodeTable"/> where
    /// it holds no row.
    /// </summary>
    private static List<byte[]> MissingStartRows(HeapTable nodeTable, HeapTable edgeTable, bool forward, SqlType[] schema)
    {
        var graphId = nodeTable.StorageOrdinals[GraphColumns.OrdinalOf(nodeTable, GraphColumnKind.GraphId)];
        var present = new HashSet<long>();
        foreach (var row in ClusteredScan.Rows(nodeTable))
            _ = present.Add(RowDecoder.DecodeColumn(nodeTable.StoredColumns, row, graphId, nodeTable.Heap).AsInt64);
        var objectOrdinal = edgeTable.StorageOrdinals[GraphColumns.OrdinalOf(edgeTable, forward ? GraphColumnKind.FromObjId : GraphColumnKind.ToObjId)];
        var idOrdinal = edgeTable.StorageOrdinals[GraphColumns.OrdinalOf(edgeTable, forward ? GraphColumnKind.FromId : GraphColumnKind.ToId)];
        var missing = new SortedSet<long>();
        foreach (var row in ClusteredScan.Rows(edgeTable))
        {
            var objectId = RowDecoder.DecodeColumn(edgeTable.StoredColumns, row, objectOrdinal, edgeTable.Heap);
            var id = RowDecoder.DecodeColumn(edgeTable.StoredColumns, row, idOrdinal, edgeTable.Heap);
            if (!objectId.IsNull && !id.IsNull && objectId.AsInt32 == nodeTable.ObjectId && !present.Contains(id.AsInt64))
                _ = missing.Add(id.AsInt64);
        }
        var rows = new List<byte[]>(missing.Count);
        foreach (var id in missing)
            rows.Add(RowEncoder.EncodeRow(schema, [SqlValue.FromInt64(id)]));
        return rows;
    }

    /// <summary>
    /// Reads each MATCH node table the WHERE's patterns outer-join
    /// (<see cref="MatchScope.OuterNodes"/>) as a <c>LEFT JOIN</c> on its
    /// anchor endpoint, placed after the query's other sources and ahead of
    /// its paths, and switches each MATCH to the form comparing the node's
    /// other endpoints with that anchor. Column resolution is by name, so the
    /// move changes no reference. A FROM clause holding anything but commas
    /// and the paths' applies keeps the inner form.
    /// </summary>
    private static void ApplyMatchOuterNodes(List<FromSource> sources, List<JoinSpec> joins, FromClause fromClause)
    {
        if (fromClause.Match is not { InMergeOn: false, OuterNodes.Count: > 0 } match)
            return;
        var pathsStart = joins.FindIndex(join => join.Kind == JoinKind.CrossApply);
        var ordinary = pathsStart < 0 ? joins.Count : pathsStart;
        for (var i = 0; i < ordinary; i++)
        {
            if (joins[i] is not { Kind: JoinKind.Cross, OnPredicate: null, IsComma: true })
                return;
        }
        foreach (var predicate in match.Predicates)
            predicate.UseOuterNodes();
        foreach (var (node, on) in match.OuterNodes)
        {
            var index = sources.IndexOf(node);
            sources.RemoveAt(index);
            joins.RemoveAt(index > 0 ? index - 1 : 0);
            var place = joins.FindIndex(join => join.Kind == JoinKind.CrossApply);
            if (place < 0)
                place = joins.Count;
            sources.Insert(place + 1, node);
            joins.Insert(place, new JoinSpec(JoinKind.Left, on));
        }
    }

    /// <summary>
    /// What a <c>LAST_NODE</c> equality compares two path ends' sources by: the
    /// node table, or the view, CTE or derived table standing for one.
    /// </summary>
    private static object UnderlyingObject(FromSource source) =>
        (object?)source.BackingTable ?? (object?)source.BackingView ?? (object?)source.Cte ?? (object?)source.LateralPlan ?? source;

    /// <summary>
    /// Consumes <c>FOR PATH</c> after a source, the cursor on the source's last
    /// token and left on <c>PATH</c>; leaves the cursor alone otherwise.
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
    /// computation: a breadth-first walk out of each start row, each step one
    /// repetition of the recursive section, one row per node the walk reaches —
    /// the start itself included when a cycle leads back to it — carrying the
    /// path that reached it first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk follows edges whatever their endpoints hold: an edge into a
    /// node its table no longer holds — or a node a derived source's filter
    /// leaves out — still leads on, the node reading NULL to the aggregates.
    /// Such a node reached as the path's last is a row over a node table, its
    /// columns NULL but for its graph id, and no row over a derived source
    /// (probed 2026-10-11 against SQL Server 2025). A step repeats only from a
    /// node of the start's own table, as real's recursion filters it.
    /// </para>
    /// <para>
    /// Each depth's newly reached nodes come out in graph id order, which is
    /// the order real's plan inserts them in, and of several shortest paths to
    /// one node the first found wins, the frontier walked in that same order.
    /// Real settles that tie in a distinct sort whose surviving duplicate is
    /// not the first found, so a node two equally short paths reach can name
    /// the other path there.
    /// </para>
    /// </remarks>
    private sealed class ShortestPathPlan(ShortestPathSpec spec, Database database, GraphPathAggregate[] aggregates, SqlType[] operandTypes, SqlType[] schema)
    {
        private readonly ShortestPathHop[] hops = spec.Hops;

        /// <summary>
        /// The qualifier of the source carrying the start's graph id where
        /// the start's table holds no row (see
        /// <see cref="StartPathsFromMissingNodes"/>), or null.
        /// </summary>
        public string? MissingStarts;

        /// <summary>The edges out of (walking backwards, into) each node, per hop, and each hop's node rows by identity.</summary>
        private sealed class GraphSnapshot(long[] generations, int hopCount)
        {
            public readonly long[] Generations = generations;
            public readonly Dictionary<(int, long), List<((int, long) Target, byte[] Edge)>>[] Adjacency = [.. Enumerable.Range(0, hopCount).Select(static _ => new Dictionary<(int, long), List<((int, long) Target, byte[] Edge)>>())];
            public readonly Dictionary<(int, long), byte[]>[] Nodes = [.. Enumerable.Range(0, hopCount).Select(static _ => new Dictionary<(int, long), byte[]>())];
            public readonly HashSet<int>[] NodeObjects = [.. Enumerable.Range(0, hopCount).Select(static _ => new HashSet<int>())];
        }

        private volatile GraphSnapshot? snapshot;

        /// <summary>
        /// The heaps' mutation generations a snapshot over base tables alone
        /// is good for, or null when a hop reads a derived source, whose rows
        /// are read afresh for every start row.
        /// </summary>
        private long[]? Generations()
        {
            var generations = new long[this.hops.Length * 2];
            for (var h = 0; h < this.hops.Length; h++)
            {
                if (this.hops[h].Edge is not { BackingTable: { } edge, LateralPlan: null } || this.hops[h].Node is not { BackingTable: { } node, LateralPlan: null })
                    return null;
                generations[h * 2] = edge.Heap.MutationGeneration;
                generations[(h * 2) + 1] = node.Heap.MutationGeneration;
            }
            return generations;
        }

        private GraphSnapshot Snapshot(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
        {
            var generations = this.Generations();
            if (generations is not null && this.snapshot is { } cached && cached.Generations.AsSpan().SequenceEqual(generations))
                return cached;
            var built = new GraphSnapshot(generations ?? [], this.hops.Length);
            for (var h = 0; h < this.hops.Length; h++)
            {
                var hop = this.hops[h];
                var adjacency = built.Adjacency[h];
                foreach (var (from, to, edge) in Edges(hop, batch, outerResolver))
                {
                    var (key, target) = hop.Forward ? (from, to) : (to, from);
                    if (!adjacency.TryGetValue(key, out var outgoing))
                        adjacency[key] = outgoing = [];
                    outgoing.Add((target, edge));
                }
                foreach (var (identity, node) in Nodes(hop.Node, batch, outerResolver))
                {
                    built.Nodes[h][identity] = node;
                    _ = built.NodeObjects[h].Add(identity.Item1);
                }
                if (hop.Node.BackingTable is { } nodeTable)
                    _ = built.NodeObjects[h].Add(nodeTable.ObjectId);
            }
            if (generations is not null)
                this.snapshot = built;
            return built;
        }

        /// <summary>An edge source's rows with the node identities at either end, rows naming no node left out.</summary>
        private IEnumerable<((int, long) From, (int, long) To, byte[] Row)> Edges(ShortestPathHop hop, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
        {
            var source = hop.Edge;
            if (source is { BackingTable: { } table, LateralPlan: null })
            {
                var (fromObj, fromId, toObj, toId) = (
                    table.StorageOrdinals[GraphColumns.OrdinalOf(table, GraphColumnKind.FromObjId)],
                    table.StorageOrdinals[GraphColumns.OrdinalOf(table, GraphColumnKind.FromId)],
                    table.StorageOrdinals[GraphColumns.OrdinalOf(table, GraphColumnKind.ToObjId)],
                    table.StorageOrdinals[GraphColumns.OrdinalOf(table, GraphColumnKind.ToId)]);
                foreach (var row in ClusteredScan.Rows(table))
                {
                    var (a, b, c, d) = (Decode(table, row, fromObj), Decode(table, row, fromId), Decode(table, row, toObj), Decode(table, row, toId));
                    if (!a.IsNull && !b.IsNull && !c.IsNull && !d.IsNull)
                        yield return ((a.AsInt32, b.AsInt64), (c.AsInt32, d.AsInt64), row);
                }
                yield break;
            }
            var fromColumn = GraphColumns.FindPseudoColumn(source.ColumnNames, GraphColumns.FromId);
            var toColumn = GraphColumns.FindPseudoColumn(source.ColumnNames, GraphColumns.ToId);
            foreach (var row in source.LateralPlan!.Execute(batch, outerResolver).RowBytes)
            {
                if (this.Identity(source, fromColumn, row) is { } from && this.Identity(source, toColumn, row) is { } to)
                    yield return (from, to, row);
            }
        }

        /// <summary>A node source's rows by identity.</summary>
        private IEnumerable<((int, long) Identity, byte[] Row)> Nodes(FromSource source, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
        {
            if (source is { BackingTable: { } table, LateralPlan: null })
            {
                var graphId = table.StorageOrdinals[GraphColumns.OrdinalOf(table, GraphColumnKind.GraphId)];
                foreach (var row in ClusteredScan.Rows(table))
                    yield return ((table.ObjectId, Decode(table, row, graphId).AsInt64), row);
                yield break;
            }
            var nodeColumn = GraphColumns.FindPseudoColumn(source.ColumnNames, GraphColumns.NodeId);
            foreach (var row in source.LateralPlan!.Execute(batch, outerResolver).RowBytes)
            {
                if (this.Identity(source, nodeColumn, row) is { } identity)
                    yield return (identity, row);
            }
        }

        /// <summary>The node a derived source's rendered identifier column names, or null.</summary>
        private (int, long)? Identity(FromSource source, int column, byte[] row)
        {
            var text = RowDecoder.DecodeColumn(source.StoredSchema, row, column, source.LobStore);
            return !text.IsNull && GraphColumns.TryParseStrict(text.AsString, database, edge: false, out var table, out var id)
                ? (table.ObjectId, id)
                : null;
        }

        private static SqlValue Decode(HeapTable table, byte[] row, int storageOrdinal) =>
            RowDecoder.DecodeColumn(table.StoredColumns, row, storageOrdinal, table.Heap);

        /// <summary>The node the start row names: a node table's row, another path's last node, or a derived source's rendered identifier.</summary>
        private (int, long)? Start(Func<MultiPartName, SqlValue> outerResolver)
        {
            var start = spec.Start;
            if (spec.StartIsLastNode)
            {
                var objectId = outerResolver(new MultiPartName(start.Qualifier!).WithAddedPart(ShortestPathSpec.ObjectColumn));
                var nodeId = outerResolver(new MultiPartName(start.Qualifier!).WithAddedPart(ShortestPathSpec.IdColumn));
                return objectId.IsNull || nodeId.IsNull ? null : (objectId.AsInt32, nodeId.AsInt64);
            }
            if (start.BackingTable is { } table)
            {
                var graphId = outerResolver(new MultiPartName(start.Qualifier!).WithAddedPart(table.Columns[GraphColumns.OrdinalOf(table, GraphColumnKind.GraphId)].Name));
                if (graphId.IsNull && this.MissingStarts is { } missing)
                    graphId = outerResolver(new MultiPartName(missing).WithAddedPart(ShortestPathSpec.IdColumn));
                return graphId.IsNull ? null : (table.ObjectId, graphId.CoerceTo(SqlType.BigInt).AsInt64);
            }
            var text = outerResolver(new MultiPartName(start.Qualifier!).WithAddedPart(start.ColumnNames[GraphColumns.FindPseudoColumn(start.ColumnNames, GraphColumns.NodeId)]));
            return !text.IsNull && GraphColumns.TryParseStrict(text.AsString, database, edge: false, out var startTable, out var id)
                ? (startTable.ObjectId, id)
                : null;
        }

        public IEnumerable<byte[]> Rows(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
        {
            if (this.Start(outerResolver ?? throw new InvalidOperationException("A SHORTEST_PATH ran without its start row.")) is not { } start)
                yield break;
            var graph = this.Snapshot(batch, outerResolver);
            var last = this.hops.Length - 1;
            var emitsMissing = this.hops[last].Node is { BackingTable: not null, LateralPlan: null };

            var reachedBy = new Dictionary<(int, long), ((int, long) Previous, (byte[] Edge, byte[]? Node)[] Steps)>();
            var chain = new (byte[] Edge, byte[]? Node)[this.hops.Length];
            List<(int, long)> frontier = [start];
            for (var depth = 1; frontier.Count > 0 && (spec.MaxHops is null || depth <= spec.MaxHops); depth++)
            {
                List<(int, long)> next = [];
                foreach (var from in frontier)
                {
                    if (depth == 1 || from.Item1 == start.Item1)
                        Expand(from, from, 0);
                }
                next.Sort();
                foreach (var reached in next)
                {
                    if (emitsMissing || graph.Nodes[last].ContainsKey(reached))
                        yield return this.Row(batch, start, reached, reachedBy);
                }
                frontier = next;

                void Expand((int, long) origin, (int, long) at, int hop)
                {
                    if (!graph.Adjacency[hop].TryGetValue(at, out var outgoing))
                        return;
                    foreach (var (target, edge) in outgoing)
                    {
                        if (!graph.NodeObjects[hop].Contains(target.Item1))
                            continue;
                        chain[hop] = (edge, graph.Nodes[hop].GetValueOrDefault(target));
                        if (hop < last)
                            Expand(origin, target, hop + 1);
                        else if (reachedBy.TryAdd(target, (origin, [.. chain])))
                            next.Add(target);
                    }
                }
            }
        }

        private byte[] Row(
            BatchContext batch,
            (int, long) start,
            (int, long) reached,
            Dictionary<(int, long), ((int, long) Previous, (byte[] Edge, byte[]? Node)[] Steps)> reachedBy)
        {
            var values = new SqlValue[schema.Length];
            var end = spec.End;
            var nodeColumns = end.Columns.Length;
            var nodeRow = reachedBy[reached].Steps[^1].Node;
            for (var c = 0; c < nodeColumns; c++)
                values[c] = nodeRow is null ? SqlValue.Null(end.Columns[c].Type) : EndColumn(end, nodeRow, c, batch);
            // A node table's row its table no longer holds keeps its graph id,
            // which a LAST_NODE hop reads on.
            if (nodeRow is null && end.BackingTable is { } endTable)
                values[GraphColumns.OrdinalOf(endTable, GraphColumnKind.GraphId)] = SqlValue.FromInt64(reached.Item2);
            values[nodeColumns] = SqlValue.FromInt32(reached.Item1);
            values[nodeColumns + 1] = SqlValue.FromInt64(reached.Item2);
            if (aggregates.Length == 0)
                return RowEncoder.EncodeRow(schema, values);

            // The path's steps, first to last.
            var steps = new List<(byte[] Edge, byte[]? Node)[]>();
            var at = reached;
            do
            {
                var (previous, chain) = reachedBy[at];
                steps.Add(chain);
                at = previous;
            }
            while (at != start || steps.Count == 0);
            steps.Reverse();

            var step = default((byte[] Edge, byte[]? Node)[]);
            var runtime = new RuntimeContext(name => this.ResolveStep(name, step!, batch), batch);
            for (var a = 0; a < aggregates.Length; a++)
            {
                var aggregate = aggregates[a];
                var resultType = schema[nodeColumns + 2 + a];
                if (aggregate.Aggregate is not { } ordinary)
                {
                    step = steps[^1];
                    values[nodeColumns + 2 + a] = aggregate.Operand.Run(runtime).CoerceTo(resultType);
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
                values[nodeColumns + 2 + a] = aggregator.Result();
            }
            return RowEncoder.EncodeRow(schema, values);
        }

        /// <summary>
        /// One column of the reached node: a node table's stored column or its
        /// rendered <c>$node_id</c> — its other computed columns read NULL, since
        /// nothing outside the graph path aggregates may read them — or a
        /// derived source's column.
        /// </summary>
        private static SqlValue EndColumn(FromSource end, byte[] row, int column, BatchContext batch)
        {
            if (end is not { BackingTable: { } table, LateralPlan: null })
                return RowDecoder.DecodeColumn(end.StoredSchema, row, column, end.LobStore);
            return table.StorageOrdinals[column] >= 0 || table.Columns[column].GraphKind == GraphColumnKind.GraphIdComputed
                ? Column(table, row, column, batch)
                : SqlValue.Null(table.Columns[column].Type);
        }

        /// <summary>
        /// A column of one path step, read off the row of the hop's edge or
        /// node it names — decoding just that column, or evaluating a computed
        /// one over the row's own columns; a node the walk passed through
        /// without its row reads NULL.
        /// </summary>
        private SqlValue ResolveStep(MultiPartName name, (byte[] Edge, byte[]? Node)[] step, BatchContext batch)
        {
            if (name.ImmediateQualifier is { } qualifier)
            {
                for (var h = 0; h < this.hops.Length; h++)
                {
                    var (source, row) = BuiltInToken.Equals(qualifier, this.hops[h].Edge.Qualifier) ? (this.hops[h].Edge, step[h].Edge)
                        : BuiltInToken.Equals(qualifier, this.hops[h].Node.Qualifier) ? (this.hops[h].Node, step[h].Node)
                        : (null, null);
                    if (source is null)
                        continue;
                    var ordinal = SourceOrdinal(source, name.Leaf);
                    if (ordinal < 0)
                        break;
                    return row is null ? SqlValue.Null(source.Columns[ordinal].Type)
                        : source is { BackingTable: { } table, LateralPlan: null } ? Column(table, row, ordinal, batch)
                        : RowDecoder.DecodeColumn(source.StoredSchema, row, ordinal, source.LobStore);
                }
            }
            throw SimulatedSqlException.InvalidColumnName(name);
        }

        private static int SourceOrdinal(FromSource source, string leaf)
        {
            for (var i = 0; i < source.ColumnNames.Length; i++)
            {
                if (BuiltInToken.Equals(source.ColumnNames[i], leaf) || GraphColumns.IsPseudoColumnFor(source.ColumnNames[i], leaf))
                    return i;
            }
            return -1;
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
