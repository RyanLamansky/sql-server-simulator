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
    /// <c>LAST_NODE(name)</c>), a <c>SHORTEST_PATH(start(hop)+)</c> or
    /// <c>…(hop){1, n})</c> repeats one hop, and patterns join only with
    /// <c>AND</c>.
    /// </summary>
    public static new BooleanExpression Parse(ParserContext context)
    {
        _ = context.GetNextRequired(); // '('
        var hops = new List<(Name From, Name Edge, Name To)>();
        (Name Start, Name Edge, Name Node, bool Forward, int? MaxHops)? path = null;
        do
        {
            context.MoveNextRequired();
            if (context.Token is UnquotedString { Value: var word } && word.Equals("SHORTEST_PATH", StringComparison.OrdinalIgnoreCase))
            {
                if (path is not null)
                    throw new NotSupportedException("More than one SHORTEST_PATH in a MATCH isn't modeled.");
                path = ParseShortestPath(context);
                context.MoveNextRequired();
                continue;
            }
            var node = ReadNode(context);
            var hopsBefore = hops.Count;
            while (context.GetNextRequired() is Operator { Character: '-' or '<' } arrow)
            {
                var (edge, next, forward) = ReadHop(context, arrow);
                hops.Add(forward ? (node, edge, next) : (next, edge, node));
                node = next;
            }
            if (hops.Count == hopsBefore)
                throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.Token is ReservedKeyword { Keyword: Keyword.And });
        if (context.Token is not Operator { Character: ')' })
        {
            throw context.Token is ReservedKeyword keyword
                ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
                : SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        return new MatchPredicate(Bind(context, hops, path));
    }

    /// <summary>
    /// One hop after the arrow's first character (<paramref name="arrow"/>,
    /// the cursor on it): the edge and the node it reaches, and whether it
    /// points from the node before it to that one.
    /// </summary>
    private static (Name Edge, Name Next, bool Forward) ReadHop(ParserContext context, Operator arrow)
    {
        if (arrow.Character == '-')
        {
            Expect(context, '(');
            var edge = ReadIdentifier(context.GetNextRequired(), context);
            Expect(context, ')');
            Expect(context, '-');
            Expect(context, '>');
            context.MoveNextRequired();
            return (edge, ReadNode(context), true);
        }
        Expect(context, '-');
        Expect(context, '(');
        var backEdge = ReadIdentifier(context.GetNextRequired(), context);
        Expect(context, ')');
        Expect(context, '-');
        context.MoveNextRequired();
        return (backEdge, ReadNode(context), false);
    }

    /// <summary>A node name, or <c>LAST_NODE(name)</c> — a path's last node — with the cursor on it and left on its end.</summary>
    private static Name ReadNode(ParserContext context)
    {
        if (context.Token is UnquotedString { Value: var word } && word.Equals("LAST_NODE", StringComparison.OrdinalIgnoreCase) && IsAhead(context))
        {
            Expect(context, '(');
            var inner = ReadIdentifier(context.GetNextRequired(), context);
            Expect(context, ')');
            return inner;
        }
        return ReadIdentifier(context.Token!, context);
    }

    /// <summary>
    /// <c>SHORTEST_PATH ( start ( hop ) + )</c> or <c>… ( hop ) { 1 , n } )</c>,
    /// the cursor on <c>SHORTEST_PATH</c>, left on the closing <c>)</c>. A
    /// quantifier not starting at 1 is Msg 13942, one not ending past 1 Msg 13943
    /// (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static (Name Start, Name Edge, Name Node, bool Forward, int? MaxHops) ParseShortestPath(ParserContext context)
    {
        Expect(context, '(');
        context.MoveNextRequired();
        var start = ReadNode(context);
        Expect(context, '(');
        if (context.GetNextRequired() is not Operator { Character: '-' or '<' } arrow)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var (edge, node, forward) = ReadHop(context, arrow);
        if (context.GetNextRequired() is Operator { Character: '-' or '<' })
            throw new NotSupportedException("A SHORTEST_PATH repeating more than one hop isn't modeled.");
        if (context.Token is not Operator { Character: ')' })
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
        return (start, edge, node, forward, maxHops);
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

    private static BooleanExpression Bind(ParserContext context, List<(Name From, Name Edge, Name To)> hops, (Name Start, Name Edge, Name Node, bool Forward, int? MaxHops)? path)
    {
        var scope = context.MatchScope!;
        var sources = scope.Sources ?? context.ScopeSources ?? [];
        var joins = scope.Sources is null ? context.ScopeJoins ?? [] : scope.Joins ?? [];
        var collation = context.Batch.CurrentDatabase.Collation;

        // Every identifier in written order: a name no source answers to, a
        // node position over a non-node or an edge position over a non-edge is
        // reported for each offender, in the order written.
        SimulatedSqlException? errors = null;
        var bound = new (int From, int Edge, int To)[hops.Count];
        for (var i = 0; i < hops.Count; i++)
        {
            var (from, edge, to) = hops[i];
            bound[i] = (Resolve(from, edge: false), Resolve(edge, edge: true), Resolve(to, edge: false));
        }
        (int Start, int Edge, int Node)? pathBound = null;
        if (path is { } written)
        {
            pathBound = (Resolve(written.Start, edge: false), Resolve(written.Edge, edge: true), Resolve(written.Node, edge: false));
            if (errors is null)
            {
                // The recursive section's edge and node have to be FOR PATH.
                if (!sources[pathBound.Value.Edge].ForPath)
                    Append(SimulatedSqlException.ShortestPathNeedsForPath(written.Edge.Value));
                if (!sources[pathBound.Value.Node].ForPath)
                    Append(SimulatedSqlException.ShortestPathNeedsForPath(written.Node.Value));
            }
        }
        if (errors is not null)
            throw errors;

        // An edge used twice — in one pattern or across a WHERE's MATCHes.
        foreach (var (_, edge, _) in bound)
        {
            if (scope.Edges.Contains(sources[edge]))
                throw SimulatedSqlException.MatchEdgeUsedTwice(sources[edge].Qualifier!);
            scope.Edges.Add(sources[edge]);
        }
        if (pathBound is var (pathStart, pathEdge, pathNode))
        {
            if (scope.ShortestPath is not null)
                throw new NotSupportedException("More than one SHORTEST_PATH in a WHERE clause isn't modeled.");
            scope.ShortestPath = new ShortestPathSpec(sources[pathStart], sources[pathEdge], sources[pathNode], path!.Value.Forward, path.Value.MaxHops);
            RejectJoined(pathStart);
            RejectJoined(pathEdge);
            RejectJoined(pathNode);
        }
        foreach (var (from, _, to) in bound)
        {
            if (!scope.Nodes.Contains(sources[from]))
                scope.Nodes.Add(sources[from]);
            if (!scope.Nodes.Contains(sources[to]))
                scope.Nodes.Add(sources[to]);
        }

        // A source joined by JOIN or APPLY rather than a comma: the edges are
        // named ahead of the nodes, each in the order written.
        foreach (var (_, edge, _) in bound)
            RejectJoined(edge);
        foreach (var (from, _, to) in bound)
        {
            RejectJoined(from);
            RejectJoined(to);
        }

        var conjuncts = new List<BooleanExpression>(hops.Count * 4);
        foreach (var (from, edge, to) in bound)
        {
            Endpoint(conjuncts, sources[edge], sources[from], GraphColumnKind.FromObjId, GraphColumnKind.FromId, GraphColumns.FromId);
            Endpoint(conjuncts, sources[edge], sources[to], GraphColumnKind.ToObjId, GraphColumnKind.ToId, GraphColumns.ToId);
        }
        return AndAll([.. conjuncts]);

        int Resolve(Name name, bool edge)
        {
            for (var s = 0; s < sources.Length; s++)
            {
                if (sources[s].Qualifier is not { } qualifier || !collation.Equals(qualifier, name.Value))
                    continue;
                if (edge ? !IsEdgeSource(sources[s]) : !IsNodeSource(sources[s]))
                {
                    Append(edge ? SimulatedSqlException.MatchIdentifierNotEdge(name.Value) : SimulatedSqlException.MatchIdentifierNotNode(name.Value));
                    return 0;
                }
                return s;
            }
            Append(SimulatedSqlException.MatchIdentifierNotBound(name.Value));
            return 0;
        }

        void Append(SimulatedSqlException error) =>
            errors = errors is null ? error : SimulatedSqlException.AndMatchError(errors, error);

        void RejectJoined(int source)
        {
            if (scope.AllJoined || (source > 0 && !joins[source - 1].IsComma) || (source < joins.Length && !joins[source].IsComma))
                throw SimulatedSqlException.MatchIdentifierInJoin(sources[source].Qualifier!);
        }
    }

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
    /// identity when both are base tables, else the rendered identifiers.
    /// </summary>
    private static void Endpoint(List<BooleanExpression> conjuncts, FromSource edge, FromSource node, GraphColumnKind objectIdKind, GraphColumnKind idKind, string pseudoColumn)
    {
        if (edge.BackingTable is { } edgeTable && node.BackingTable is { } nodeTable)
        {
            conjuncts.Add(Equality(
                new Reference(edge.Qualifier!, edgeTable.Columns[GraphColumns.OrdinalOf(edgeTable, objectIdKind)].Name),
                new Value(SqlValue.FromInt32(nodeTable.ObjectId))));
            conjuncts.Add(Equality(
                new Reference(edge.Qualifier!, edgeTable.Columns[GraphColumns.OrdinalOf(edgeTable, idKind)].Name),
                new Reference(node.Qualifier!, nodeTable.Columns[GraphColumns.OrdinalOf(nodeTable, GraphColumnKind.GraphId)].Name)));
            return;
        }
        conjuncts.Add(Equality(
            new Reference(edge.Qualifier!, edge.ColumnNames[GraphColumns.FindPseudoColumn(edge.ColumnNames, pseudoColumn)]),
            new Reference(node.Qualifier!, node.ColumnNames[GraphColumns.FindPseudoColumn(node.ColumnNames, GraphColumns.NodeId)])));
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
/// <c>from</c> end.
/// </summary>
internal sealed class MatchScope
{
    public readonly List<FromSource> Edges = [];
    public readonly List<FromSource> Nodes = [];

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

    /// <summary>The <c>SHORTEST_PATH</c> the MATCH recurses over, if any.</summary>
    public ShortestPathSpec? ShortestPath;

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

/// <summary>
/// A bound <c>SHORTEST_PATH(start(-(edge)-&gt;node)+)</c>: the start node source,
/// the <c>FOR PATH</c> edge and node it repeats, the direction of the hop, and
/// the most hops a <c>{1, n}</c> quantifier allows (null for <c>+</c>).
/// </summary>
internal sealed class ShortestPathSpec(FromSource start, FromSource edge, FromSource node, bool forward, int? maxHops)
{
    public readonly FromSource Start = start;
    public readonly FromSource Edge = edge;
    public readonly FromSource Node = node;
    public readonly bool Forward = forward;
    public readonly int? MaxHops = maxHops;
}
