namespace SqlServerSimulator;

// Factories for the full-text query pipeline's diagnostics — Msg 7601 / 7630 /
// 7645. Numbers, severities, states and wording are probe-confirmed against
// SQL Server 2025 (17.0.4065.4) with Full-Text Search installed.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server's Msg 7601 state 2 — the table (or indexed view) named
    /// by the predicate carries no full-text index at all.
    /// </summary>
    internal static SimulatedSqlException FullTextTableNotIndexed(string tableName) =>
        new($"Cannot use a CONTAINS or FREETEXT predicate on table or indexed view '{tableName}' because it is not full-text indexed.",
            7601, 16, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 7601 state 3 — the table is indexed but the
    /// named column isn't one of the indexed columns.
    /// </summary>
    internal static SimulatedSqlException FullTextColumnNotIndexed(string columnName) =>
        new($"Cannot use a CONTAINS or FREETEXT predicate on column '{columnName}' because it is not full-text indexed.",
            7601, 16, 3);

    /// <summary>
    /// Mimics SQL Server's Msg 7645 — the search condition was NULL or held no
    /// characters. Real reports the same state for a NULL literal reaching the
    /// predicate through a variable, for the empty string, and for a string of
    /// only whitespace; a bare <c>NULL</c> keyword written in the call is
    /// rejected earlier by the grammar (Msg 156).
    /// </summary>
    internal static SimulatedSqlException FullTextNullOrEmptyPredicate() =>
        new("Null or empty full-text predicate.", 7645, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 7630 state 1 — the condition ran out while a
    /// term or a closing parenthesis was still owed.
    /// </summary>
    internal static SimulatedSqlException FullTextSyntaxErrorAtEnd(string condition) =>
        FullTextSyntaxError("<end of input>", condition, state: 1);

    /// <summary>
    /// Mimics SQL Server's Msg 7630 state 2 — a punctuation token stood where a
    /// term belonged, including the opening quote of a phrase that was never
    /// closed.
    /// </summary>
    internal static SimulatedSqlException FullTextSyntaxErrorNearPunctuation(string token, string condition) =>
        FullTextSyntaxError(token, condition, state: 2);

    /// <summary>
    /// Mimics SQL Server's Msg 7630 state 3 — a word stood where an operator or
    /// the end of the condition belonged.
    /// </summary>
    internal static SimulatedSqlException FullTextSyntaxErrorNearWord(string token, string condition) =>
        FullTextSyntaxError(token, condition, state: 3);

    private static SimulatedSqlException FullTextSyntaxError(string token, string condition, byte state) =>
        new($"Syntax error near '{token}' in the full-text search condition '{condition}'.", 7630, 15, state);

    /// <summary>
    /// Mimics SQL Server's Msg 1046 — real classifies a full-text predicate as
    /// a rowset construct, so writing one where only a scalar expression may
    /// stand (a CHECK constraint, a computed column) reports the
    /// subquery-not-allowed error rather than anything full-text specific.
    /// </summary>
    internal static SimulatedSqlException FullTextPredicateNotAllowedHere() =>
        SubqueriesNotAllowedInThisContext();

    /// <summary>
    /// Real's severity-10 Msg 9927, raised through the <c>InfoMessage</c>
    /// surface rather than thrown: the condition named at least one system
    /// stopword, which the engine ignored.
    /// </summary>
    internal const int FullTextNoiseWordMessageNumber = 9927;

    /// <inheritdoc cref="FullTextNoiseWordMessageNumber"/>
    internal const string FullTextNoiseWordMessage = "Informational: The full-text search condition contained noise word(s).";
}
