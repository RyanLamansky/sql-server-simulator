namespace SqlServerSimulator.Parser;

/// <summary>
/// Which kind of schema-bound module body a parse is reading, for the
/// constructs such a body refuses while it parses with Msg 1054: a select-list
/// star and <c>GROUP BY ALL</c> (probed 2026-09-30 against SQL Server 2025).
/// </summary>
internal enum SchemaBoundBody : byte
{
    /// <summary>Not a schema-bound body.</summary>
    None,

    /// <summary>
    /// A schema-bound view's or inline function's defining query, where a star
    /// is state 6 (bare) or 7 (qualified) at any depth.
    /// </summary>
    DefiningQuery,

    /// <summary>
    /// A schema-bound scalar or multi-statement function's statement list —
    /// or, after a refusal, the rest of any schema-bound body, which real's
    /// parser recovers into as statements. A star in a statement's own query
    /// (a <c>SELECT</c> statement, a cursor's query, an <c>INSERT</c>
    /// source, each branch of a set operation among them) is state 1 or 2;
    /// in a nested query it is 6 or 7.
    /// </summary>
    Statements,
}
