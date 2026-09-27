using SqlServerSimulator.Storage;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An aggregate over one <c>SHORTEST_PATH</c> — <c>STRING_AGG</c>,
/// <c>COUNT</c>, <c>SUM</c>, <c>AVG</c>, <c>MIN</c>, <c>MAX</c> or
/// <c>LAST_VALUE</c> followed by <c>WITHIN GROUP (GRAPH PATH)</c>. Its operand
/// reads the <c>FOR PATH</c> edge and node of each step, in path order. The
/// result types are the ordinary aggregate's, and <c>LAST_VALUE</c>'s is its
/// operand's (probed 2026-09-27 against SQL Server 2025).
/// </summary>
/// <remarks>
/// The query's plan computes each path's values alongside the path's rows
/// (<c>Selection.ApplyShortestPath</c>), then binds this node to the column
/// that carries them, which is all <see cref="Run"/> reads.
/// </remarks>
internal sealed class GraphPathAggregate : Expression
{
    /// <summary>The ordinary aggregate the operand feeds, or null for <c>LAST_VALUE</c>.</summary>
    public readonly AggregateExpression? Aggregate;

    public readonly Expression Operand;

    /// <summary>The column of the path source carrying this aggregate's value, once the plan binds it.</summary>
    public Reference? Bound;

    private GraphPathAggregate(AggregateExpression? aggregate, Expression operand)
    {
        this.Aggregate = aggregate;
        this.Operand = operand;
    }

    /// <summary>The function name real's messages use, in lower case.</summary>
    public string LowerName => this.Aggregate?.LowerName ?? "last_value";

    /// <summary>
    /// Whether the cursor, on a <c>WITHIN</c> after a call, opens
    /// <c>WITHIN GROUP (GRAPH PATH)</c>.
    /// </summary>
    public static bool IsAhead(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var ahead = context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Within }
            && context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.Group }
            && context.MoveNext() && context.Token is Operator { Character: '(' }
            && context.MoveNext() && context.Token is UnquotedString { Value: var graph } && graph.Equals("GRAPH", StringComparison.OrdinalIgnoreCase);
        context.RestoreCheckpoint(checkpoint);
        return ahead;
    }

    /// <summary>
    /// Turns the aggregate just parsed into its path form, the cursor on the
    /// <c>WITHIN</c>; leaves the cursor on the clause's closing <c>)</c>. The
    /// aggregate stops being one of the query's own. <c>COUNT(*)</c> has no path
    /// form (Msg 102 near <c>within</c>).
    /// </summary>
    public static GraphPathAggregate FromAggregate(AggregateExpression aggregate, ParserContext context)
    {
        if (aggregate.Operand is null || aggregate.Distinct)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.AggregateCollector is { } collector && collector.Remove(aggregate))
            context.AggregatesParsed--;
        return Register(context, new GraphPathAggregate(aggregate, aggregate.Operand));
    }

    /// <summary><c>LAST_VALUE(operand) WITHIN GROUP (GRAPH PATH)</c>, the cursor on the <c>WITHIN</c>.</summary>
    public static GraphPathAggregate LastValue(Expression operand, ParserContext context) =>
        Register(context, new GraphPathAggregate(null, operand));

    private static GraphPathAggregate Register(ParserContext context, GraphPathAggregate aggregate)
    {
        // WITHIN GROUP ( GRAPH PATH )
        context.MoveNextRequired();
        context.MoveNextRequired();
        context.MoveNextRequired();
        if (context.GetNextRequired() is not UnquotedString { Value: var path } || !path.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GraphPathAggregates is not { } collector)
            throw SimulatedSqlException.GraphPathAggregateWithoutPath(aggregate.LowerName);
        collector.Add(aggregate);
        return aggregate;
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        this.Bound is { } bound ? bound.Run(runtime) : throw SimulatedSqlException.GraphPathAggregateWithoutPath(this.LowerName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.Bound is { } bound
            ? bound.GetSqlType(batch, resolveColumnType)
            : this.Aggregate?.GetSqlType(batch, resolveColumnType) ?? this.Operand.GetSqlType(batch, resolveColumnType);

    internal override bool ResultIsNullable(NullabilityContext context) =>
        this.Aggregate is null ? this.Operand.ResultIsNullable(context) : this.Aggregate.Kind != AggregateKind.Count;

    internal override string DebugDisplay() => $"{this.LowerName.ToUpperInvariant()}({this.Operand.DebugDisplay()}) WITHIN GROUP (GRAPH PATH)";

    // Once bound, the operand's names are the path's, which no longer resolve
    // in the query's own scope.
    internal override void Describe(NodeShape shape) =>
        _ = this.Bound is { } bound ? shape.Local(this.LowerName).Child(bound) : shape.Local(this.LowerName).Child(this.Operand);
}
