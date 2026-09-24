using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Where a query sits relative to the statement that holds it, which is what
/// every nesting-dependent parse rule asks.
/// </summary>
internal enum QueryPosition
{
    /// <summary>
    /// The statement's own query: a <c>SELECT</c> statement, a cursor's query,
    /// or a view or function body being created. Its select list reaches the
    /// client or materializes columns.
    /// </summary>
    Statement,

    /// <summary>The source query of <c>INSERT … SELECT</c>, written without parentheses.</summary>
    InsertSource,

    /// <summary>The source query of <c>INSERT … (SELECT …)</c>.</summary>
    ParenthesizedInsertSource,

    /// <summary>
    /// A view or inline function body inlined into the statement that
    /// references it.
    /// </summary>
    Inlined,

    /// <summary>A derived table, a CTE's query, an <c>APPLY</c> body or a <c>MERGE</c> source.</summary>
    Derived,

    /// <summary>A subquery in an expression, or the key plan an <c>IN</c> / <c>EXISTS</c> is answered from.</summary>
    Subquery,

    /// <summary>The query an <c>EXISTS</c> tests, whose select list is never read.</summary>
    Exists,
}

/// <summary>
/// What a query's parse carries down from its surroundings: its
/// <see cref="QueryPosition"/> and the resolver for an enclosing query's
/// columns. Every rule that differs between a statement and a nested query
/// reads one of the members below rather than testing the position itself.
/// </summary>
internal readonly struct QueryScope(QueryPosition position, Func<MultiPartName, SqlType>? outerTypeResolver)
{
    public readonly QueryPosition Position = position;

    /// <summary>
    /// The enclosing query's column-type resolver, or <see langword="null"/>
    /// where nothing encloses this one.
    /// </summary>
    public readonly Func<MultiPartName, SqlType>? OuterTypeResolver = outerTypeResolver;

    public static QueryScope Statement => new(QueryPosition.Statement, null);

    /// <summary>A query nested in this one, at <paramref name="position"/>.</summary>
    public static QueryScope Nested(QueryPosition position, Func<MultiPartName, SqlType>? outerTypeResolver) =>
        new(position, outerTypeResolver);

    /// <summary>This scope with its enclosing-column resolver replaced.</summary>
    public QueryScope WithOuter(Func<MultiPartName, SqlType>? outerTypeResolver) => new(this.Position, outerTypeResolver);

    /// <summary>
    /// Whether a closing parenthesis ends this query. Otherwise the query is a
    /// statement's own, which a <c>;</c> or the next statement's keyword ends
    /// and in which a <c>)</c> is a syntax error.
    /// </summary>
    public bool Parenthesized => this.Position is not (QueryPosition.Statement or QueryPosition.InsertSource or QueryPosition.Inlined);

    /// <summary>
    /// Whether this query's result columns must each settle one collation
    /// (Msg 451 otherwise). Only a statement's own query does: a nested query
    /// hands an unresolved collation on to whatever reads its column — an
    /// enclosing select list, a comparison, a conversion — which settles or
    /// refuses it there (probed 2026-09-23 against SQL Server 2025).
    /// </summary>
    public bool NamesOutputCollation => this.Position is QueryPosition.Statement;

    /// <summary>
    /// Whether the select list may hold <c>SELECT … INTO</c>'s
    /// <c>IDENTITY()</c> function: a statement's own query and an
    /// <c>INSERT</c> source, where a missing <c>INTO</c> is Msg 177. In any
    /// other query the <c>IDENTITY</c> keyword is Msg 156 (probe-confirmed).
    /// </summary>
    public bool AcceptsIdentityFunction => this.Position is QueryPosition.Statement or QueryPosition.InsertSource or QueryPosition.ParenthesizedInsertSource;

    /// <summary>
    /// Whether this query's rows are written to an <c>INSERT</c> target, which
    /// supplies the collation its string columns convert to.
    /// </summary>
    public bool FeedsInsert => this.Position is QueryPosition.InsertSource or QueryPosition.ParenthesizedInsertSource;

    /// <summary>
    /// Whether nothing reads this query's select list, so real never evaluates
    /// it: <c>EXISTS (SELECT 1/0 FROM t)</c> is true over a non-empty <c>t</c>.
    /// </summary>
    public bool ProjectionUnread => this.Position is QueryPosition.Exists;

    /// <summary>
    /// Whether a <c>TOP 100 PERCENT … ORDER BY</c> here orders nothing,
    /// because the rows it returns are read by an enclosing query rather than
    /// returned in order.
    /// </summary>
    public bool OrderingIgnoredUnderFullTop => this.Position is QueryPosition.Inlined or QueryPosition.Derived or QueryPosition.Subquery or QueryPosition.Exists;

    /// <summary>
    /// Whether this query may not carry its own <c>ORDER BY</c> or
    /// <c>FOR XML</c> / <c>FOR JSON</c> clause, which real refuses in a
    /// parenthesized <c>INSERT</c> source as Msg 156 on the keyword even with
    /// the <c>TOP</c> that would license an <c>ORDER BY</c> in a derived table
    /// (probed 2026-08-06 and, for <c>FOR</c>, 2026-09-23).
    /// </summary>
    public bool RefusesTrailingClauses => this.Position is QueryPosition.ParenthesizedInsertSource;
}
