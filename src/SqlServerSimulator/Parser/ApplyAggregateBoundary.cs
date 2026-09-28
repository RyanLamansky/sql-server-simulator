namespace SqlServerSimulator.Parser;

/// <summary>
/// The aggregate collector an <c>APPLY</c>'s right side sees as its enclosing
/// query's: an aggregate moving out of the right side reading only the
/// <c>APPLY</c>'s left-side columns is real's Msg 4101, since the query holding
/// the <c>APPLY</c> would own it, while one reading columns from further out
/// passes through to <see cref="Enclosing"/> (probed 2026-09-28 against SQL
/// Server 2025: <c>SELECT (SELECT x.m FROM u CROSS APPLY (SELECT MAX(t.a) m
/// FROM u u2) x) FROM t</c> answers). <c>Selection.MoveToEnclosingQuery</c>
/// walks through it; only an aggregate a function argument on the right side
/// registers lands in it, and moves on once the right side has parsed.
/// </summary>
internal sealed class ApplyAggregateBoundary(FromSource[] leftSources, List<Expressions.AggregateExpression>? enclosing) : List<Expressions.AggregateExpression>
{
    /// <summary>The sources to the left of the <c>APPLY</c>, as the right side sees them.</summary>
    public readonly FromSource[] LeftSources = leftSources;

    /// <summary>The collector of the query holding the <c>APPLY</c>, itself possibly a boundary.</summary>
    public readonly List<Expressions.AggregateExpression>? Enclosing = enclosing;
}
