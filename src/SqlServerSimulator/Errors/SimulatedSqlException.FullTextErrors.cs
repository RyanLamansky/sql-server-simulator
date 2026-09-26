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

    // The full-text DDL refusals below were probed 2026-09-26 against SQL
    // Server 2025; each names what real's does.

    /// <summary>Msg 574: full-text DDL inside a user transaction, naming the statement (<c>ALTER FULLTEXT INDEX</c>…).</summary>
    internal static SimulatedSqlException StatementInsideUserTransaction(string statement) =>
        new($"{statement} statement cannot be used inside a user transaction.", 574, 16, 0);

    /// <summary>Msg 7658: <c>ALTER</c> (state 2) or <c>DROP</c> (state 5) of a full-text index on a table that has none.</summary>
    internal static SimulatedSqlException FullTextIndexMissing(string tableName, byte state) =>
        new($"Table or indexed view '{tableName}' does not have a full-text index or user does not have permission to perform this action.", 7658, 16, state);

    /// <summary>Msg 7670: a full-text column that isn't character, <c>xml</c>, <c>image</c> or <c>varbinary(max)</c>.</summary>
    internal static SimulatedSqlException FullTextColumnTypeInvalid(string columnName) =>
        new($"Column '{columnName}' cannot be used for full-text search because it is not a character-based, XML, image or varbinary(max) type column or it is encrypted.", 7670, 16, 1);

    /// <summary>Msg 7655: an <c>image</c> or <c>varbinary(max)</c> full-text column with no <c>TYPE COLUMN</c>.</summary>
    internal static SimulatedSqlException FullTextTypeColumnRequired() =>
        new("TYPE COLUMN option must be specified with column of image or varbinary(max) type.", 7655, 16, 1);

    /// <summary>Msg 7672: a column named twice, or added when the index already covers it.</summary>
    internal static SimulatedSqlException FullTextDuplicateColumn(string columnName) =>
        new($"A full-text index cannot be created on the table or indexed view because duplicate column '{columnName}' is specified.", 7672, 16, 1);

    /// <summary>Msg 7677: <c>ALTER FULLTEXT INDEX … DROP</c> / <c>ALTER COLUMN</c> of a column the index doesn't cover.</summary>
    internal static SimulatedSqlException FullTextDdlColumnNotIndexed(string columnName) =>
        new($"Column \"{columnName}\" is not full-text indexed.", 7677, 16, 1);

    /// <summary>Msg 7659: <c>ENABLE</c> of an index whose columns were all dropped.</summary>
    internal static SimulatedSqlException FullTextIndexHasNoColumns(string tableName) =>
        new($"Cannot activate full-text search for table or indexed view '{tableName}' because no columns have been enabled for full-text search.", 7659, 16, 2);

    /// <summary>Msg 7663: <c>WITH NO POPULATION</c> on an <c>ADD</c> / <c>DROP</c> while change tracking is on.</summary>
    internal static SimulatedSqlException FullTextNoPopulationWithChangeTracking() =>
        new("Option 'WITH NO POPULATION' should not be used when change tracking is enabled.", 7663, 16, 2);

    /// <summary>Msg 7664: <c>START UPDATE POPULATION</c> with change tracking off.</summary>
    internal static SimulatedSqlException FullTextChangeTrackingNotStarted(string tableName) =>
        new($"Full-text change tracking must be started on table or indexed view '{tableName}' before the changes can be flushed.", 7664, 16, 1);

    /// <summary>Msg 30023: a named stoplist, none of which exist here — state 1 from <c>CREATE</c>, 3 from <c>ALTER</c>.</summary>
    internal static SimulatedSqlException FullTextStoplistNotFound(string stoplistName, byte state) =>
        new($"The fulltext stoplist '{stoplistName}' does not exist or the current user does not have permission to perform this action. Verify that the correct stoplist name is specified and that the user had the permission required by the Transact-SQL statement.", 30023, 16, state);

    /// <summary>Msg 30025: a named search property list, none of which exist here — state 1 from <c>CREATE</c>, 3 from <c>ALTER</c>.</summary>
    internal static SimulatedSqlException SearchPropertyListNotFound(string listName, byte state) =>
        new($"The search property list '{listName}' does not exist or you do not have permission to perform this action. Verify that the correct search property list name is specified and that you have the permission required by the Transact-SQL statement. For a list of the search property lists on the current database, use the sys.registered_search_property_lists catalog view. For information about permissions required by a Transact-SQL statement, see the Transact-SQL reference topic for the statement in SQL Server Books Online.", 30025, 16, state);

    /// <summary>Msg 41209: <c>STATISTICAL_SEMANTICS</c>, which needs a semantic language statistics database the simulator never has.</summary>
    internal static SimulatedSqlException SemanticDatabaseNotRegistered() =>
        new("A semantic language statistics database is not registered. Full-text indexes using 'STATISTICAL_SEMANTICS' cannot be created or populated.", 41209, 16, 3);
}
