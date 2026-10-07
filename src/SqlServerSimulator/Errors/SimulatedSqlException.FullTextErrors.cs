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
    /// <summary>Mimics SQL Server's Msg 15601: an <c>sp_help_fulltext_*</c> procedure in a database <c>sp_fulltext_database</c> disabled.</summary>
    internal static SimulatedSqlException FullTextNotEnabledForDatabase() =>
        new("Full-Text Search is not enabled for the current database. Use sp_fulltext_database to enable Full-Text Search. "
            + "The functionality to disable and enable full-text search for a database is deprecated. Please change your application.", 15601, 16, 1);

    /// <summary>Mimics SQL Server's Msg 9966: <c>sp_fulltext_database</c> in master, tempdb or model.</summary>
    internal static SimulatedSqlException FullTextInSystemDatabase() =>
        new("Cannot use full-text search in master, tempdb, or model database.", 9966, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15104: <c>sp_help_fulltext_columns</c> naming a column its table lacks.</summary>
    internal static SimulatedSqlException NoTableWithColumn(string tableName, string columnName) =>
        new($"You do not own a table named '{tableName}' that has a column named '{columnName}'.", 15104, 16, 1);

    /// <summary>Mimics SQL Server's Msg 15218: a full-text help procedure naming a view.</summary>
    internal static SimulatedSqlException ObjectIsNotATable(string objectName) =>
        new($"Object '{objectName}' is not a table.", 15218, 16, 1);

    /// <summary>
    /// The Msg 50000 <c>sys.sp_fulltext_rethrow_error</c> raises for <c>sp_fulltext_load_thesaurus_file</c>'s
    /// error 30050, quoting it whole: an LCID with no thesaurus file, NULL included (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ThesaurusNotLoaded(string lcid) =>
        new($"Error 30050, Level 16, State 1, Procedure sys.sp_fulltext_load_thesaurus_file, Line 41, Message: Both the thesaurus file for lcid '{lcid}' and the global thesaurus could not be loaded.", 50000, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 7601 — the table (or view) named by the
    /// predicate carries no full-text index at all, named as the FROM clause
    /// wrote it: state 2 for a named column or a rowset, 4 for a predicate's
    /// <c>*</c> (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException FullTextTableNotIndexed(string tableName, byte state = 2) =>
        new($"Cannot use a CONTAINS or FREETEXT predicate on table or indexed view '{tableName}' because it is not full-text indexed.",
            7601, 16, state);

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
    /// Mimics SQL Server's Msg 7645 as <c>sys.dm_fts_parser</c> raises it for
    /// a NULL argument: severity 16, state 201 for the query string, 202 for
    /// the LCID and 203 for the accent sensitivity.
    /// </summary>
    internal static SimulatedSqlException FullTextParserNullArgument(byte state) =>
        new("Null or empty full-text predicate.", 7645, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 7696 state 10 — an LCID with no full-text
    /// language behind it, from a <c>LANGUAGE</c> argument or
    /// <c>sys.dm_fts_parser</c>.
    /// </summary>
    internal static SimulatedSqlException FullTextInvalidLocale() =>
        new("Invalid locale ID was specified. Please verify that the locale ID is correct and corresponding language resource has been installed.", 7696, 16, 10);

    /// <summary>
    /// Mimics SQL Server's Msg 7678 state 12 — a <c>LANGUAGE</c> argument
    /// naming no language <c>sys.syslanguages</c> knows by name or alias.
    /// </summary>
    internal static SimulatedSqlException FullTextLanguageAliasUnknown(string name) =>
        new($"The following string is not defined as a language alias in syslanguages: {name}.", 7678, 16, 12);

    /// <summary>
    /// Mimics SQL Server's Msg 30092 — <c>sys.dm_fts_parser</c> given a
    /// stoplist id other than 0 (the system stoplist) or NULL; no other
    /// stoplist exists here.
    /// </summary>
    internal static SimulatedSqlException FullTextStoplistIdNotFound(int stoplistId) =>
        new($"Full-text stoplist ID '{stoplistId}' does not exist.", 30092, 16, 1);

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
    /// Mimics SQL Server's Msg 9987 state 1 — a generic <c>NEAR</c>'s distance
    /// that is neither <c>MAX</c> nor a whole number up to 4294967295.
    /// </summary>
    internal static SimulatedSqlException FullTextNearDistanceInvalid() =>
        new("The max gap argument in NEAR clause must be either the word MAX or an integer greater than or equal to 0.", 9987, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 31201 — a <c>PROPERTY(column, 'name')</c>
    /// search over an index with no search property list, which no index here
    /// can have (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException FullTextPropertySearchUnsupported() =>
        new("Property-scoped full-text queries cannot be specified on the specified table because its full-text index is not configured for property searching. To support property-scoped searches, the full-text index must be associated with a search property list and repopulated. The Transact-SQL syntax for this is: ALTER FULLTEXT INDEX ON <table_name> SET SEARCH PROPERTY LIST <property_list_name>;.", 31201, 16, 1);

    /// <summary>Mimics SQL Server's Msg 7632 state 5 — an <c>ISABOUT</c> weight outside 0.0 to 1.0.</summary>
    internal static SimulatedSqlException FullTextWeightOutOfRange() =>
        new("The value of the Weight argument must be between 0.0 and 1.0.", 7632, 15, 5);

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

    /// <summary>
    /// Msg 574: full-text DDL, <c>DROP DATABASE</c> or <c>CREATE VECTOR
    /// INDEX</c> (state 31) inside a user transaction, naming the statement
    /// (<c>ALTER FULLTEXT INDEX</c>…).
    /// </summary>
    internal static SimulatedSqlException StatementInsideUserTransaction(string statement, byte state = 0) =>
        new($"{statement} statement cannot be used inside a user transaction.", 574, 16, state);

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

    /// <summary>Msg 7663: <c>WITH NO POPULATION</c> while change tracking is on — state 1 from <c>CREATE</c>, 2 from an <c>ALTER</c>'s <c>ADD</c> / <c>DROP</c>.</summary>
    internal static SimulatedSqlException FullTextNoPopulationWithChangeTracking(byte state = 2) =>
        new("Option 'WITH NO POPULATION' should not be used when change tracking is enabled.", 7663, 16, state);

    // The CREATE / DROP refusals below were probed 2026-10-05 against SQL
    // Server 2025.

    /// <summary>Msg 7653: a <c>KEY INDEX</c> that can't key a full-text index — state 1 missing, 2 not unique, single-column, unfiltered and enabled, 3 nullable.</summary>
    internal static SimulatedSqlException FullTextKeyIndexInvalid(string indexName, byte state) =>
        new($"'{indexName}' is not a valid index to enforce a full-text search key. A full-text search key must be a unique, non-nullable, single-column index which is not offline, is not defined on a non-deterministic or imprecise nonpersisted computed column, does not have a filter, and has maximum size of 900 bytes. Choose another index for the full-text key.", 7653, 16, state);

    /// <summary>Msg 9967: <c>CREATE FULLTEXT INDEX</c> naming no catalog in a database with no default one.</summary>
    internal static SimulatedSqlException FullTextDefaultCatalogMissing(string databaseName) =>
        new($"A default full-text catalog does not exist in database '{databaseName}' or user does not have permission to perform this action.", 9967, 16, 1);

    /// <summary>Msg 9960: <c>CREATE FULLTEXT INDEX</c> on a view without a clustered index, named as written.</summary>
    internal static SimulatedSqlException FullTextViewNotIndexed(string viewName) =>
        new($"View '{viewName}' is not an indexed view. Full-text index is not allowed to be created on it.", 9960, 16, 1);

    /// <summary>Msg 9938: <c>CREATE FULLTEXT CATALOG … AUTHORIZATION</c> naming no user or role.</summary>
    internal static SimulatedSqlException FullTextOwnerNotFound(string ownerName) =>
        new($"Cannot find the specified user or role '{ownerName}'.", 9938, 16, 1);

    /// <summary>Msg 7668: <c>DROP FULLTEXT CATALOG</c> of a catalog an index still uses.</summary>
    internal static SimulatedSqlException FullTextCatalogNotEmpty(string catalogName) =>
        new($"Cannot drop full-text catalog '{catalogName}' because it contains a full-text index.", 7668, 16, 1);

    /// <summary>Msg 7699: a <c>TYPE COLUMN</c> on a column that isn't <c>image</c> or <c>varbinary(max)</c>.</summary>
    internal static SimulatedSqlException FullTextTypeColumnNotAllowed() =>
        new("TYPE COLUMN option is not allowed for column types other than image or varbinary(max).", 7699, 16, 1);

    /// <summary>Msg 7671: a <c>TYPE COLUMN</c> that isn't a character column of at most 260 characters.</summary>
    internal static SimulatedSqlException FullTextTypeColumnInvalid(string columnName) =>
        new($"Column '{columnName}' cannot be used as full-text type column for image column. It must be a character-based column with a size less or equal than 260 characters.", 7671, 16, 2);

    /// <summary>Msg 7613: <c>DROP INDEX</c> (state 2, the table as written) or a constraint drop (state 1, its bare name) of the index keying a full-text index.</summary>
    internal static SimulatedSqlException FullTextKeyIndexDropped(string indexName, string tableName, byte state) =>
        new($"Cannot drop index '{indexName}' because it enforces the full-text key for table or indexed view '{tableName}'.", 7613, 16, state);

    /// <summary>Msg 7614: <c>ALTER TABLE … DROP COLUMN</c> of a full-text-indexed column.</summary>
    internal static SimulatedSqlException FullTextColumnDropped(string columnName) =>
        new($"Cannot alter or drop column '{columnName}' because it is enabled for Full-Text Search.", 7614, 16, 1);

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
