namespace SqlServerSimulator;

// Vector indexes' and VECTOR_SEARCH's diagnostics, probed 2026-09-29 against
// SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 343 state 2: <c>CREATE VECTOR INDEX</c> in a database whose
    /// <c>PREVIEW_FEATURES</c> scoped configuration is off, raised compiling
    /// the batch.
    /// </summary>
    internal static SimulatedSqlException UnknownObjectTypeVector() =>
        new("Unknown object type 'vector' used in a CREATE, DROP, or ALTER statement.", 343, 15, 2);

    /// <summary>Msg 153 state 7: a vector index's <c>WITH</c> list without <c>METRIC</c>.</summary>
    internal static SimulatedSqlException VectorIndexMetricMissing() =>
        new("Invalid usage of the option metric in the CREATE VECTOR INDEX statement.", 153, 15, 7);

    /// <summary>
    /// Msg 155 state 6: a relational index option <c>CREATE VECTOR INDEX</c>
    /// doesn't take, echoed as written.
    /// </summary>
    internal static SimulatedSqlException VectorIndexOptionNotTaken(string optionName) =>
        new($"'{optionName}' is not a recognized CREATE VECTOR INDEX option.", 155, 15, 6);

    /// <summary>Msg 155 then Msg 153 for an option no index statement knows.</summary>
    internal static SimulatedSqlException UnrecognizedVectorIndexOption(string optionName) =>
        Aggregate([UnrecognizedIndexOption(optionName, "CREATE VECTOR INDEX"), InvalidUsageOfIndexOption(optionName)]);

    /// <summary>Msg 304: an index <c>MAXDOP</c> outside 0 to 32767, echoed as written.</summary>
    internal static SimulatedSqlException IndexMaxDopOutOfRange(string written) =>
        new($"'{written}' is out of range for index/statistics option 'maxdop'. See sp_configure option 'max degree of parallelism' for valid values.", 304, 16, 1);

    /// <summary>Msg 42215 state 3: the indexed column isn't <c>vector</c>.</summary>
    internal static SimulatedSqlException VectorIndexColumnNotVector(string columnName, string tableName) =>
        new($"Could not create the vector index on the column '{columnName}' on table '{tableName}', because it is not of type vector.", 42215, 16, 3);

    /// <summary>
    /// Msg 42217: the table's clustered primary key isn't one <c>int</c>
    /// column — state 1 when there is no single-column clustered primary key
    /// at all, 2 when its one column has another type, 4 (naming no table)
    /// when <c>VECTOR_SEARCH</c> is pointed at a view.
    /// </summary>
    internal static SimulatedSqlException VectorIndexNeedsIntClusteredKey(string tableName, byte state) =>
        new($"Table '{tableName}' must have a clustered primary key on a single 4 byte INT column to create a vector index.", 42217, 16, state);

    /// <summary>Msg 42220: a vector index on a temp table, named as written.</summary>
    internal static SimulatedSqlException VectorIndexOnTempObject(string writtenName) =>
        new($"Cannot create the vector index on temp objects. '{writtenName}' is identified as a temp object.", 42220, 16, 1);

    /// <summary>Msg 42230: the column already carries a vector index.</summary>
    internal static SimulatedSqlException VectorIndexAlreadyOnColumn(string columnName) =>
        new($"Cannot create vector index on column '{columnName}' because it already has an existing vector index.", 42230, 16, 1);

    /// <summary>
    /// Msg 42231: a statement writing a table that carries a vector index —
    /// state 1 for the statement's own target, 3 for a table a cascading
    /// foreign key would write. Raised compiling the statement, and it ends
    /// the batch.
    /// </summary>
    internal static SimulatedSqlException VectorIndexedTableIsReadOnly(string tableName, byte state = 1) =>
        new($"Data modification statement failed because table '{tableName}' has a vector index on it.", 42231, 16, state) { TerminatesBatch = true };

    /// <summary>Msg 42232: <c>TRUNCATE TABLE</c> on a table that carries a vector index.</summary>
    internal static SimulatedSqlException VectorIndexedTableTruncate(string tableName) =>
        new($"TRUNCATE TABLE statement failed because table '{tableName}' has a vector index on it.", 42232, 16, 1);

    /// <summary>Msg 42250: any <c>ALTER INDEX</c> form reaching a vector index.</summary>
    internal static SimulatedSqlException VectorIndexAlterUnsupported() =>
        new("One or more of the specified ALTER INDEX options is unsupported for a Vector Index.", 42250, 16, 1);

    /// <summary>Msg 3768 then 3727: dropping the primary key of a table with a vector index.</summary>
    internal static SimulatedSqlException PrimaryKeyDropBlockedByVectorIndex(string constraintName) =>
        FollowedByConstraintNotDropped(new($"Could not drop the primary key constraint '{constraintName}' because the table has a vector index.", 3768, 16, 1));

    /// <summary>
    /// Msg 290 state 2 from line 867 of <c>sp_rename</c>: a vector index can't
    /// be renamed, which real finds only after its caution message, and the
    /// batch ends.
    /// </summary>
    internal static SimulatedSqlException VectorIndexRename()
    {
        const string Message = "Invalid EXECUTE statement using object \"IndexOrStats\", method \"SetName\".";
        return new(Message, new SimulatedError(@class: 16, lineNumber: 867, Message, 290, procedure: "sp_rename", server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 2)) { TerminatesBatch = true };
    }

    /// <summary>Msg 3766 state 1: <c>DROP INDEX table.index</c> naming a vector index.</summary>
    internal static SimulatedSqlException VectorIndexDropNeedsOnSyntax(string writtenName) =>
        new($"Cannot drop vector index '{writtenName}' using old 'Table.Index' syntax, use 'Index ON Table' syntax instead.", 3766, 16, 1);

    /// <summary>The number of the informational message <c>CREATE VECTOR INDEX</c> sends as it builds.</summary>
    internal const int JoinOrderEnforcedMessageNumber = 8625;

    /// <inheritdoc cref="JoinOrderEnforcedMessageNumber"/>
    internal const string JoinOrderEnforcedMessage = "Warning: The join order has been enforced because a local join hint is used.";

    /// <summary>
    /// Msg 102 naming <c>TOP_N</c>: a <c>VECTOR_SEARCH</c> argument list
    /// ending after <c>METRIC</c>, which real reports at the argument it
    /// wanted rather than at the closing parenthesis.
    /// </summary>
    internal static SimulatedSqlException VectorSearchTopNMissing() =>
        new("Incorrect syntax near 'TOP_N'.", 102, 15, 1);

    /// <summary>Msg 207 state 20: <c>VECTOR_SEARCH</c>'s <c>COLUMN</c> names no column of its table.</summary>
    internal static SimulatedSqlException VectorSearchColumnMissing(string columnName) =>
        new($"Invalid column name '{columnName}'.", 207, 16, 20);

    /// <summary>Msg 42226: <c>VECTOR_SEARCH</c>'s <c>COLUMN</c> isn't <c>vector</c>.</summary>
    internal static SimulatedSqlException VectorSearchColumnNotVector(string columnName) =>
        new($"The column '{columnName}' is not of vector type. Vector search cannot be performed on a non-vector column.", 42226, 16, 1);

    /// <summary>
    /// Msg 42227: no vector index on <c>VECTOR_SEARCH</c>'s column carries
    /// its metric, the metric as written.
    /// </summary>
    internal static SimulatedSqlException VectorSearchIndexMissing(string metric, string columnName) =>
        new($"Cannot find a vector index with metric '{metric}' on column '{columnName}'.", 42227, 16, 1);
}
