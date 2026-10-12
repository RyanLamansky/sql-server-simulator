using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>MATCH (a-(e)->b [-(f)->c …] [AND …])</c> — the graph pattern predicate a
/// WHERE clause takes over comma-listed node and edge sources. Each hop
/// desugars into equalities between the edge's endpoint and the node's
/// identity: over two base tables, the edge's hidden <c>from_obj_id</c> /
/// <c>from_id</c> pair against the node table's object id and its hidden
/// <c>graph_id</c>, which the join planner hashes like any written equi-join;
/// over a derived table, view or CTE, the <c>$from_id</c> / <c>$to_id</c> text
/// against the node's <c>$node_id</c>.
/// </summary>
/// <remarks>
/// Bound while it parses, against <see cref="ParserContext.ScopeSources"/>:
/// real's Msg 13900 (unbound name), 13901 / 13902 (a node position that isn't
/// a node, an edge position that isn't an edge), 13903 (one edge in two
/// patterns), 13920 (a source joined with JOIN or APPLY) and 13905 (under OR
/// or NOT), probed 2026-09-27 against SQL Server 2025.
/// </remarks>
internal sealed class MatchPredicate : BooleanExpression
{
    private readonly BooleanExpression desugared;

    private MatchPredicate(BooleanExpression desugared) => this.desugared = desugared;

    /// <summary>Whether the <c>MATCH</c> under the cursor opens a pattern — a <c>(</c> follows it.</summary>
    public static bool IsAhead(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var ahead = context.MoveNext() && context.Token is Operator { Character: '(' };
        context.RestoreCheckpoint(checkpoint);
        return ahead;
    }

    /// <summary>
    /// A <c>MATCH (</c> anywhere but a WHERE clause parses as a call, so the
    /// pattern's arrows are what real reports — Msg 102 near <c>&gt;</c> — and a
    /// call that somehow parses is no built-in (probed 2026-09-27 against SQL
    /// Server 2025). Entered on the first argument token.
    /// </summary>
    public static Expression RejectMisplaced(ParserContext context)
    {
        _ = Expression.Parse(context);
        while (context.Token is Operator { Character: ',' })
            _ = Expression.Parse(context.MoveNextRequiredReturnSelf());
        throw context.Token is Operator { Character: ')' }
            ? SimulatedSqlException.UnrecognizedBuiltInFunction("MATCH")
            : SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Msg 13905 when any of <paramref name="operands"/> — the arms of an OR,
    /// or the operand of a NOT — holds a MATCH.
    /// </summary>
    public static void RejectUnder(List<BooleanExpression> operands)
    {
        foreach (var operand in operands)
        {
            operand.Walk((node, _) => node is MatchPredicate
                ? throw SimulatedSqlException.MatchCombinedWithOrOrNot()
                : true);
        }
    }

    /// <summary>
    /// Parses the pattern list with the cursor on <c>MATCH</c>, leaving it past
    /// the closing <c>)</c>. The grammar is strict: a hop is
    /// <c>-(edge)-&gt;</c> or <c>&lt;-(edge)-</c> between two bare names (or
    /// <c>LAST_NODE(name)</c>), a <c>SHORTEST_PATH(start(hop …)+)</c> or
    /// <c>…(hop …){1, n})</c> repeats a chain of hops, <c>LAST_NODE(x) =
    /// LAST_NODE(y)</c> joins two paths' ends, and patterns join only with
    /// <c>AND</c>.
    /// </summary>
    public static new BooleanExpression Parse(ParserContext context)
    {
        _ = context.GetNextRequired(); // '('
        var written = new WrittenPatterns();
        do
        {
            context.MoveNextRequired();
            if (context.Token is UnquotedString { Value: var word } && word.Equals("SHORTEST_PATH", StringComparison.OrdinalIgnoreCase))
            {
                written.Paths.Add(ParseShortestPath(context));
                context.MoveNextRequired();
                continue;
            }
            var node = ReadNode(context);
            var hopsBefore = written.Hops.Count;
            while (context.GetNextRequired() is Operator { Character: '-' or '<' } arrow)
            {
                var (edge, next, forward) = ReadHop(context, arrow, inRecursiveSection: false);
                written.Hops.Add(forward ? (node, edge, next) : (next, edge, node));
                node = next;
            }
            if (written.Hops.Count == hopsBefore && node.LastNode && context.Token is Operator { Character: '=' })
            {
                written.LastNodeEqualities.Add((node.Name, ReadLastNodeComparison(context)));
                continue;
            }
            if (written.Hops.Count == hopsBefore)
                throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.Token is ReservedKeyword { Keyword: Keyword.And });
        if (context.Token is not Operator { Character: ')' })
        {
            throw context.Token is ReservedKeyword keyword
                ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
                : SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return new MatchPredicate(Bind(context, written));
    }

    /// <summary>A node position as written: a bare name, or <c>LAST_NODE(name)</c>.</summary>
    private readonly struct NodeReference(Name name, bool lastNode)
    {
        public readonly Name Name = name;
        public readonly bool LastNode = lastNode;
    }

    /// <summary>A <c>SHORTEST_PATH</c> as written: its start, the hops its recursive section repeats, and its quantifier's bound.</summary>
    private sealed class WrittenPath(NodeReference start, List<(Name Edge, NodeReference Node, bool Forward)> hops, int? maxHops)
    {
        public readonly NodeReference Start = start;
        public readonly List<(Name Edge, NodeReference Node, bool Forward)> Hops = hops;
        public readonly int? MaxHops = maxHops;
    }

    /// <summary>One MATCH's patterns: its ordinary hops, its shortest paths, and its <c>LAST_NODE</c> equalities.</summary>
    private sealed class WrittenPatterns
    {
        public readonly List<(NodeReference From, Name Edge, NodeReference To)> Hops = [];
        public readonly List<WrittenPath> Paths = [];
        public readonly List<(Name Left, Name Right)> LastNodeEqualities = [];
    }

    /// <summary>
    /// One hop after the arrow's first character (<paramref name="arrow"/>,
    /// the cursor on it): the edge and the node it reaches, and whether it
    /// points from the node before it to that one. A recursive section's
    /// hop reaches a bare name: <c>LAST_NODE</c> there is Msg 102 near it
    /// (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    private static (Name Edge, NodeReference Next, bool Forward) ReadHop(ParserContext context, Operator arrow, bool inRecursiveSection)
    {
        var forward = arrow.Character == '-';
        if (!forward)
            Expect(context, '-');
        Expect(context, '(');
        var edge = ReadIdentifier(context.GetNextRequired(), context);
        Expect(context, ')');
        Expect(context, '-');
        if (forward)
            Expect(context, '>');
        context.MoveNextRequired();
        if (inRecursiveSection && IsLastNode(context))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return (edge, ReadNode(context), forward);
    }

    private static bool IsLastNode(ParserContext context) =>
        context.Token is UnquotedString { Value: var word } && word.Equals("LAST_NODE", StringComparison.OrdinalIgnoreCase) && IsAhead(context);

    /// <summary>
    /// The right side of <c>LAST_NODE(x) = LAST_NODE(y)</c>, the cursor on the
    /// <c>=</c> and left past it: real takes only another <c>LAST_NODE</c>
    /// there (anything else is Msg 102 near it; probed 2026-09-27 against SQL
    /// Server 2025).
    /// </summary>
    private static Name ReadLastNodeComparison(ParserContext context)
    {
        context.MoveNextRequired();
        if (!IsLastNode(context))
        {
            throw context.Token is ReservedKeyword keyword
                ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
                : SimulatedSqlException.SyntaxErrorNear(context);
        }
        var right = ReadNode(context);
        context.MoveNextRequired();
        return right.Name;
    }

    /// <summary>A node name, or <c>LAST_NODE(name)</c> — a path's last node — with the cursor on it and left on its end.</summary>
    private static NodeReference ReadNode(ParserContext context)
    {
        if (IsLastNode(context))
        {
            Expect(context, '(');
            var inner = ReadIdentifier(context.GetNextRequired(), context);
            Expect(context, ')');
            return new(inner, lastNode: true);
        }
        return new(ReadIdentifier(context.Token!, context), lastNode: false);
    }

    /// <summary>
    /// <c>SHORTEST_PATH ( start ( hop … ) + )</c> or <c>… ( hop … ) { 1 , n } )</c>,
    /// the cursor on <c>SHORTEST_PATH</c>, left on the closing <c>)</c>. A
    /// quantifier not starting at 1 is Msg 13942, one not ending past 1 Msg 13943
    /// (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static WrittenPath ParseShortestPath(ParserContext context)
    {
        Expect(context, '(');
        context.MoveNextRequired();
        var start = ReadNode(context);
        Expect(context, '(');
        var hops = new List<(Name Edge, NodeReference Node, bool Forward)>();
        while (context.GetNextRequired() is Operator { Character: '-' or '<' } arrow)
            hops.Add(ReadHop(context, arrow, inRecursiveSection: true));
        if (hops.Count == 0 || context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        int? maxHops = null;
        switch (context.GetNextRequired())
        {
            case Operator { Character: '+' }:
                break;
            case Operator { Character: '{' }:
                var low = ReadCount(context);
                Expect(context, ',');
                var high = ReadCount(context);
                Expect(context, '}');
                if (low != 1)
                    throw SimulatedSqlException.ShortestPathInitialQuantifier();
                if (high <= low)
                    throw SimulatedSqlException.ShortestPathFinalQuantifier();
                maxHops = high;
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        Expect(context, ')');
        return new WrittenPath(start, hops, maxHops);
    }

    private static int ReadCount(ParserContext context) =>
        context.GetNextRequired() is Numeric { Value: var value } && value.Type.Category == SqlTypeCategory.Integer
            ? value.CoerceTo(SqlType.Int32).AsInt32
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    private static Name ReadIdentifier(Token token, ParserContext context) => token switch
    {
        Name name => name,
        ReservedKeyword keyword => throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    private static void Expect(ParserContext context, char character)
    {
        if (context.GetNextRequired() is not Operator op || op.Character != character)
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    private static BooleanExpression Bind(ParserContext context, WrittenPatterns written)
    {
        var scope = context.MatchScope!;
        var ownSources = scope.Sources ?? context.ScopeSources ?? [];
        var joins = scope.Sources is null ? context.ScopeJoins ?? [] : scope.Joins ?? [];
        // An identifier the query's own sources don't answer binds against
        // an enclosing query's, innermost first: a correlated subquery may
        // match from an outer node (probed 2026-10-05 against SQL Server
        // 2025). Those sit past the query's own in one combined list.
        var sources = ownSources;
        var applyLeft = new List<bool>(new bool[ownSources.Length]);
        for (var enclosing = context.EnclosingScopes.Count - 1; enclosing >= 0; enclosing--)
        {
            var outer = context.EnclosingScopes[enclosing];
            sources = [.. sources, .. outer];
            applyLeft.AddRange(Enumerable.Repeat(context.ApplyLeftScopes.Contains(outer), outer.Length));
        }
        var collation = context.Batch.CurrentDatabase.Collation;

        // Every identifier of a hop, its edge first: a name no source answers
        // to, a node position over a non-node or an edge position over a
        // non-edge is reported for each offender. A derived table, view or
        // CTE real can't see through to a node or edge table is Msg 13940
        // instead, reported after the rest, its edges ahead of its nodes
        // (probed 2026-10-11 against SQL Server 2025).
        SimulatedSqlException? errors = null;
        var opaqueEdges = new List<string>();
        var opaqueNodes = new List<string>();
        var hops = written.Hops;
        var bound = new (int From, int Edge, int To)[hops.Count];
        Array.Fill(bound, (-1, -1, -1));
        for (var i = 0; i < hops.Count; i++)
        {
            // Real binds a hop's edge ahead of its two nodes (probed
            // 2026-10-11 against SQL Server 2025).
            var (from, edge, to) = hops[i];
            var boundEdge = Resolve(edge, edge: true);
            bound[i] = (Resolve(from.Name, edge: false, from.LastNode), boundEdge, Resolve(to.Name, edge: false, to.LastNode));
            // The first hop to fail ends the binding: a later hop's misbound
            // names go unreported.
            if (errors is not null)
                break;
        }
        var equalities = new (int Left, int Right)[written.LastNodeEqualities.Count];
        for (var i = 0; i < equalities.Length; i++)
            equalities[i] = (Resolve(written.LastNodeEqualities[i].Left, edge: false, lastNode: true), Resolve(written.LastNodeEqualities[i].Right, edge: false, lastNode: true));
        var paths = new List<(int Start, (int Edge, int Node, bool Forward)[] Hops)>(written.Paths.Count);
        if (errors is null)
        {
            foreach (var path in written.Paths)
            {
                var start = Resolve(path.Start.Name, edge: false, path.Start.LastNode);
                var section = new (int Edge, int Node, bool Forward)[path.Hops.Count];
                for (var h = 0; h < section.Length; h++)
                    section[h] = (Resolve(path.Hops[h].Edge, edge: true), Resolve(path.Hops[h].Node.Name, edge: false), path.Hops[h].Forward);
                paths.Add((start, section));
            }
        }

        // A source joined by JOIN or APPLY rather than a comma — or read from
        // an APPLY's left side — reported paths first, then the edges ahead
        // of the nodes, each in the order written. A derived table on an
        // APPLY's right side is reported at state 3 and the check goes on;
        // anything else stops it (probed 2026-10-11 against SQL Server 2025).
        SimulatedSqlException? joined = null;
        foreach (var (start, section) in paths)
        {
            RejectJoined(start, edge: false);
            foreach (var (edge, node, _) in section)
            {
                RejectJoined(edge, edge: true);
                RejectJoined(node, edge: false);
            }
        }
        foreach (var (_, edge, _) in bound)
            RejectJoined(edge, edge: true);
        foreach (var (from, _, to) in bound)
        {
            RejectJoined(from, edge: false);
            RejectJoined(to, edge: false);
        }
        if (joined is not null)
            throw joined;

        foreach (var opaque in opaqueEdges)
            Append(SimulatedSqlException.MatchDerivedTable(opaque));
        foreach (var opaque in opaqueNodes)
            Append(SimulatedSqlException.MatchDerivedTable(opaque));
        if (errors is not null)
            throw errors;

        // The recursive section's edges and nodes have to be FOR PATH, and
        // each section names a node once across every path.
        for (var p = 0; p < paths.Count; p++)
        {
            for (var h = 0; h < paths[p].Hops.Length; h++)
            {
                if (!sources[paths[p].Hops[h].Edge].ForPath)
                    Append(SimulatedSqlException.ShortestPathNeedsForPath(written.Paths[p].Hops[h].Edge.Value));
                if (!sources[paths[p].Hops[h].Node].ForPath)
                    Append(SimulatedSqlException.ShortestPathNeedsForPath(written.Paths[p].Hops[h].Node.Name.Value));
            }
        }
        if (errors is not null)
            throw errors;
        var sectionNodes = new List<FromSource>();
        foreach (var (_, section) in paths)
        {
            foreach (var (_, node, _) in section)
            {
                if (sectionNodes.Contains(sources[node]))
                    throw SimulatedSqlException.ShortestPathNodeUsedTwice(sources[node].Qualifier!);
                sectionNodes.Add(sources[node]);
            }
        }

        // An edge used twice — in one pattern, a recursive section or across
        // a WHERE's MATCHes.
        foreach (var (_, edge, _) in bound)
            UseEdge(edge, ordinary: true);
        foreach (var (_, section) in paths)
        {
            foreach (var (edge, _, _) in section)
                UseEdge(edge, ordinary: false);
        }
        for (var p = 0; p < paths.Count; p++)
        {
            var (start, section) = paths[p];
            scope.ShortestPaths.Add(new ShortestPathSpec(
                sources[start],
                written.Paths[p].Start.LastNode,
                written.Paths[p].Start.Name.Value,
                [.. section.Select(hop => new ShortestPathHop(sources[hop.Edge], sources[hop.Node], hop.Forward))],
                written.Paths[p].MaxHops));
        }
        for (var i = 0; i < hops.Count; i++)
        {
            var (from, _, to) = bound[i];
            if (from < ownSources.Length && !hops[i].From.LastNode && !scope.Nodes.Contains(sources[from]))
                scope.Nodes.Add(sources[from]);
            if (to < ownSources.Length && !hops[i].To.LastNode && !scope.Nodes.Contains(sources[to]))
                scope.Nodes.Add(sources[to]);
        }

        // A LAST_NODE, and a FOR PATH node named without one, is checked once
        // the whole WHERE has bound its paths.
        var conjuncts = new List<BooleanExpression>((hops.Count * 4) + (equalities.Length * 2));
        for (var i = 0; i < hops.Count; i++)
        {
            var (from, edge, to) = bound[i];
            Note(hops[i].From, from, state: 4);
            Note(hops[i].To, to, state: 5);
            Endpoint(conjuncts, sources[edge], sources[from], hops[i].From.LastNode, GraphColumnKind.FromObjId, GraphColumnKind.FromId, GraphColumns.FromId);
            Endpoint(conjuncts, sources[edge], sources[to], hops[i].To.LastNode, GraphColumnKind.ToObjId, GraphColumnKind.ToId, GraphColumns.ToId);
        }
        for (var i = 0; i < equalities.Length; i++)
        {
            var (left, right) = equalities[i];
            scope.LastNodeEqualities.Add((sources[left], written.LastNodeEqualities[i].Left.Value, sources[right], written.LastNodeEqualities[i].Right.Value));
            conjuncts.Add(Equality(new Reference(sources[left].Qualifier!, ShortestPathSpec.ObjectColumn), new Reference(sources[right].Qualifier!, ShortestPathSpec.ObjectColumn)));
            conjuncts.Add(Equality(new Reference(sources[left].Qualifier!, ShortestPathSpec.IdColumn), new Reference(sources[right].Qualifier!, ShortestPathSpec.IdColumn)));
        }
        return AndAll([.. conjuncts]);

        // A LAST_NODE names a path's last node, which anything else is
        // refused as once the WHERE's paths are known.
        int Resolve(Name name, bool edge, bool lastNode = false)
        {
            for (var s = 0; s < sources.Length; s++)
            {
                if (sources[s].Qualifier is not { } qualifier || !collation.Equals(qualifier, name.Value))
                    continue;
                if (sources[s].BackingTable is null && sources[s].LateralPlan is { } body && !IsTransparent(body))
                {
                    (edge ? opaqueEdges : opaqueNodes).Add(name.Value);
                    return -1;
                }
                if (!lastNode && (edge ? !IsEdgeSource(sources[s]) : !IsNodeSource(sources[s])))
                {
                    Append(edge ? SimulatedSqlException.MatchIdentifierNotEdge(name.Value) : SimulatedSqlException.MatchIdentifierNotNode(name.Value));
                    return -1;
                }
                return s;
            }
            Append(SimulatedSqlException.MatchIdentifierNotBound(name.Value));
            return -1;
        }

        void Append(SimulatedSqlException error) =>
            errors = errors is null ? error : SimulatedSqlException.AndMatchError(errors, error);

        void UseEdge(int edge, bool ordinary)
        {
            if (edge >= ownSources.Length)
                return;
            if (scope.AllEdges.Contains(sources[edge]))
                throw SimulatedSqlException.MatchEdgeUsedTwice(sources[edge].Qualifier!);
            _ = scope.AllEdges.Add(sources[edge]);
            if (ordinary)
                scope.Edges.Add(sources[edge]);
        }

        void Note(NodeReference written, int source, byte state)
        {
            if (written.LastNode)
                scope.LastNodeUses.Add((sources[source], written.Name.Value, state));
            else if (sources[source].ForPath)
                scope.ForPathNodeUses.Add(sources[source]);
        }

        void RejectJoined(int source, bool edge)
        {
            // A MERGE's USING side may inner-join its nodes, not outer-join
            // them (probed 2026-10-11 against SQL Server 2025).
            if (source < 0 || (scope.InMergeOn && !edge && (source == 0 || joins[source - 1] is { IsComma: true } or { Kind: JoinKind.Inner or JoinKind.Cross })))
                return;
            if (source >= ownSources.Length)
            {
                if (applyLeft[source])
                    throw Joined(SimulatedSqlException.MatchIdentifierInJoin(sources[source].Qualifier!));
                return;
            }
            if (!scope.AllJoined && (source == 0 || joins[source - 1].IsComma) && (source >= joins.Length || joins[source].IsComma))
                return;
            if (source > 0 && source <= joins.Length && joins[source - 1].Kind is JoinKind.CrossApply or JoinKind.OuterApply && sources[source] is { BackingTable: null, LateralPlan: not null })
            {
                _ = Joined(SimulatedSqlException.MatchIdentifierInJoin(sources[source].Qualifier!, state: 3));
                return;
            }
            throw Joined(SimulatedSqlException.MatchIdentifierInJoin(sources[source].Qualifier!));
        }

        SimulatedSqlException Joined(SimulatedSqlException error) =>
            joined = joined is null ? error : SimulatedSqlException.AndMatchError(joined, error);
    }

    /// <summary>
    /// Whether real reads through a derived table, view or CTE's body to the
    /// node or edge table under it: a body over one source — itself a table or
    /// such a body — with no row limit, <c>DISTINCT</c>, aggregate or grouping,
    /// or a <c>UNION ALL</c> of such bodies. Anything else is Msg 13940 in a
    /// MATCH (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    private static bool IsTransparent(Selection body) =>
        !body.HasTopOrOffsetOrFetch
        && (body.UpdatabilityRejection == ViewUpdatabilityRejection.UnionAll
            || (body.UpdatabilityProfile is { Sources: [var source] } && (source.BackingTable is not null || (source.LateralPlan is { } inner && IsTransparent(inner)))));

    private static bool IsNodeSource(FromSource source) =>
        source.BackingTable is { } table
            ? table.GraphKind == GraphTableKind.Node
            : GraphColumns.FindPseudoColumn(source.ColumnNames, GraphColumns.NodeId) >= 0;

    private static bool IsEdgeSource(FromSource source) =>
        source.BackingTable is { } table
            ? table.GraphKind == GraphTableKind.Edge
            : GraphColumns.FindPseudoColumn(source.ColumnNames, GraphColumns.FromId) >= 0
                && GraphColumns.FindPseudoColumn(source.ColumnNames, GraphColumns.ToId) >= 0;

    /// <summary>
    /// One end of a hop: the edge's endpoint pair against the node table's
    /// identity when both are base tables — or against a path's last node,
    /// whose identity its path source carries — and the rendered identifiers
    /// otherwise.
    /// </summary>
    private static void Endpoint(List<BooleanExpression> conjuncts, FromSource edge, FromSource node, bool lastNode, GraphColumnKind objectIdKind, GraphColumnKind idKind, string pseudoColumn)
    {
        if (edge.BackingTable is { } edgeTable && (lastNode || node.BackingTable is not null))
        {
            conjuncts.Add(Equality(
                new Reference(edge.Qualifier!, edgeTable.Columns[GraphColumns.OrdinalOf(edgeTable, objectIdKind)].Name),
                lastNode ? new Reference(node.Qualifier!, ShortestPathSpec.ObjectColumn) : new Value(SqlValue.FromInt32(node.BackingTable!.ObjectId))));
            conjuncts.Add(Equality(
                new Reference(edge.Qualifier!, edgeTable.Columns[GraphColumns.OrdinalOf(edgeTable, idKind)].Name),
                lastNode
                    ? new Reference(node.Qualifier!, ShortestPathSpec.IdColumn)
                    : new Reference(node.Qualifier!, node.BackingTable!.Columns[GraphColumns.OrdinalOf(node.BackingTable, GraphColumnKind.GraphId)].Name)));
            return;
        }
        // A LAST_NODE naming no node is refused once the paths are known.
        var edgeColumn = GraphColumns.FindPseudoColumn(edge.ColumnNames, pseudoColumn);
        var nodeColumn = GraphColumns.FindPseudoColumn(node.ColumnNames, GraphColumns.NodeId);
        if (edgeColumn >= 0 && nodeColumn >= 0)
            conjuncts.Add(Equality(new Reference(edge.Qualifier!, edge.ColumnNames[edgeColumn]), new Reference(node.Qualifier!, node.ColumnNames[nodeColumn])));
    }

    public override bool? Run(RuntimeContext runtime) => this.desugared.Run(runtime);

    internal override string DebugDisplay() => $"MATCH({this.desugared.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.desugared);

    internal override void VisitOperandExpressions(Action<Expression> visitor) => this.desugared.VisitOperandExpressions(visitor);

    internal override void VisitSurvivingOperandExpressions(Action<Expression> visitor) => this.desugared.VisitSurvivingOperandExpressions(visitor);

    internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.desugared.Bind(batch, resolveColumnType);

    internal override void CollectConjuncts(List<BooleanExpression> sink) => this.desugared.CollectConjuncts(sink);
}

/// <summary>
/// The node and edge sources one WHERE clause's <c>MATCH</c> predicates bound,
/// each in the order its patterns name it with every hop read from its
/// <c>from</c> end, and the shortest paths and <c>LAST_NODE</c> references
/// the query settles once the whole WHERE is parsed.
/// </summary>
internal sealed class MatchScope
{
    /// <summary>The edges of ordinary patterns, which put the MATCH under the OR / NOT refusal.</summary>
    public readonly List<FromSource> Edges = [];
    public readonly List<FromSource> Nodes = [];

    /// <summary>Every edge any pattern or recursive section names, which no second one may name again.</summary>
    public readonly HashSet<FromSource> AllEdges = [];

    /// <summary>
    /// The sources the MATCH binds against, when not the query scope's — a
    /// joined UPDATE / DELETE's FROM list, or an <c>ON</c> clause's sources —
    /// and the joins between them.
    /// </summary>
    public FromSource[]? Sources;

    /// <inheritdoc cref="Sources"/>
    public JoinSpec[]? Joins;

    /// <summary>Every source counts as joined: the MATCH sits in an <c>ON</c> clause.</summary>
    public bool AllJoined;

    /// <summary>
    /// The MATCH sits in a <c>MERGE</c>'s <c>ON</c>, over the target and the
    /// <c>USING</c> side: a node the <c>USING</c> side joins may take part, and
    /// the predicate may sit under <c>OR</c> or <c>NOT</c> (probed 2026-10-11
    /// against SQL Server 2025).
    /// </summary>
    public bool InMergeOn;

    /// <summary>The <c>SHORTEST_PATH</c>s the WHERE's MATCHes recurse over, in written order.</summary>
    public readonly List<ShortestPathSpec> ShortestPaths = [];

    /// <summary>
    /// Each <c>LAST_NODE(x)</c> an ordinary pattern names, with the Msg 13962
    /// state real reports when <c>x</c> ends no path: 4 at a hop's start, 5 at
    /// its end (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    public readonly List<(FromSource Source, string Written, byte State)> LastNodeUses = [];

    /// <summary>Each <c>LAST_NODE(x) = LAST_NODE(y)</c>, as bound and as written.</summary>
    public readonly List<(FromSource Left, string LeftWritten, FromSource Right, string RightWritten)> LastNodeEqualities = [];

    /// <summary>A <c>FOR PATH</c> node an ordinary pattern names without <c>LAST_NODE</c>.</summary>
    public readonly List<FromSource> ForPathNodeUses = [];

    /// <summary>Whether any pattern bound: an ordinary hop, a shortest path or a <c>LAST_NODE</c> equality.</summary>
    public bool Bound => this.AllEdges.Count > 0 || this.LastNodeEqualities.Count > 0;

    /// <summary>
    /// The order a bare <c>SELECT *</c> expands <paramref name="sources"/> in
    /// under a <c>MATCH</c>: the sources it doesn't bind in FROM order, then
    /// its edges, then its nodes (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    public List<FromSource> StarOrder(List<FromSource> sources)
    {
        var ordered = new List<FromSource>(sources.Count);
        foreach (var source in sources)
        {
            if (!this.Edges.Contains(source) && !this.Nodes.Contains(source))
                ordered.Add(source);
        }
        ordered.AddRange(this.Edges);
        ordered.AddRange(this.Nodes);
        return ordered;
    }
}

/// <summary>One hop of a <c>SHORTEST_PATH</c>'s recursive section: its <c>FOR PATH</c> edge and the node it reaches.</summary>
internal sealed class ShortestPathHop(FromSource edge, FromSource node, bool forward)
{
    public readonly FromSource Edge = edge;
    public readonly FromSource Node = node;

    /// <summary>Whether the hop runs from the node before it to <see cref="Node"/> (<c>-(e)-&gt;</c>) rather than back (<c>&lt;-(e)-</c>).</summary>
    public readonly bool Forward = forward;
}

/// <summary>
/// A bound <c>SHORTEST_PATH(start(hop …)+)</c>: the start source — a node
/// source, or another path's last node read through <c>LAST_NODE</c> — the
/// hops its recursive section repeats, and the most repetitions a
/// <c>{1, n}</c> quantifier allows (null for <c>+</c>).
/// </summary>
internal sealed class ShortestPathSpec(FromSource start, bool startIsLastNode, string startWritten, ShortestPathHop[] hops, int? maxHops)
{
    /// <summary>
    /// The column of a path's source carrying the reached node's object id,
    /// a name no identifier can spell, which <c>LAST_NODE</c> reads.
    /// </summary>
    public const string ObjectColumn = "\u001Dgraph_path_object_id";

    /// <summary>The column carrying the reached node's graph id; see <see cref="ObjectColumn"/>.</summary>
    public const string IdColumn = "\u001Dgraph_path_node_id";

    public readonly FromSource Start = start;
    public readonly bool StartIsLastNode = startIsLastNode;
    public readonly string StartWritten = startWritten;
    public readonly ShortestPathHop[] Hops = hops;
    public readonly int? MaxHops = maxHops;

    /// <summary>The last node of the recursive section, whose rows the path yields.</summary>
    public FromSource End => this.Hops[^1].Node;

    /// <summary>Whether <paramref name="source"/> is one of the recursive section's edges or nodes.</summary>
    public bool Recurses(FromSource source) =>
        Array.Exists(this.Hops, hop => ReferenceEquals(hop.Edge, source) || ReferenceEquals(hop.Node, source));
}
