namespace SqlServerSimulator;

// The columnstore index refusals. Every number, class and state below was
// observed raised by SQL Server 2025 (probed 2026-09-26); the class a statement
// sees is not always the severity sys.messages lists.
partial class SimulatedSqlException
{
    /// <summary>Mimics SQL Server's Msg 35301 — <c>CREATE UNIQUE … COLUMNSTORE INDEX</c>.</summary>
    internal static SimulatedSqlException ColumnstoreIndexCannotBeUnique() =>
        new("The statement failed because a columnstore index cannot be unique. Create the columnstore index without the UNIQUE keyword or create a unique index without the COLUMNSTORE keyword.", 35301, 15, 1);

    /// <summary>Mimics SQL Server's Msg 35302 — <c>ASC</c> / <c>DESC</c> on a columnstore index's column.</summary>
    internal static SimulatedSqlException ColumnstoreIndexSortOrder() =>
        new("The statement failed because specifying sort order (ASC or DESC) is not allowed when creating a columnstore index. Create the columnstore index without specifying a sort order.", 35302, 15, 1);

    /// <summary>Mimics SQL Server's Msg 35307 — a nonclustered columnstore index naming a computed column.</summary>
    internal static SimulatedSqlException ColumnstoreIndexComputedColumn(string columnName, string tableName) =>
        new($"The statement failed because column '{columnName}' on table '{tableName}' is a computed column. Columnstore index cannot include a computed column implicitly or explicitly.", 35307, 16, 1);

    /// <summary>Mimics SQL Server's Msg 35310 — a columnstore index declared on a table variable or table type.</summary>
    internal static SimulatedSqlException ColumnstoreIndexOnTableVariable() =>
        new("The statement failed because columnstore indexes are not allowed on table types and table variables. Remove the column store index specification from the table type or table variable declaration.", 35310, 15, 1);

    /// <summary>Mimics SQL Server's Msg 35311 — <c>INCLUDE</c> on a columnstore index; real's text carries three spaces.</summary>
    internal static SimulatedSqlException ColumnstoreIndexIncludedColumns() =>
        new("The statement failed because a columnstore index cannot have included columns.   Create the columnstore index on the desired columns without specifying any included columns.", 35311, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 35317 — a rowstore-only <c>WITH</c> option
    /// (<c>FILLFACTOR</c>, <c>PAD_INDEX</c>, <c>IGNORE_DUP_KEY</c>,
    /// <c>SORT_IN_TEMPDB</c>, the two statistics options) creating a
    /// columnstore index, named in capitals.
    /// </summary>
    internal static SimulatedSqlException ColumnstoreIndexOption(string option) =>
        new($"The statement failed because specifying {option} is not allowed when creating a columnstore index. Consider creating a columnstore index without specifying {option}.", 35317, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 35318 — a locking option
    /// (<c>ALLOW_ROW_LOCKS</c>, <c>ALLOW_PAGE_LOCKS</c>,
    /// <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c>) creating a columnstore index.
    /// </summary>
    internal static SimulatedSqlException ColumnstoreIndexLockOption(string option) =>
        new($"The statement failed because the {option} option is not allowed when creating a columnstore index. Create the columnstore index without specifying the {option} option.", 35318, 15, 1);

    /// <summary>Mimics SQL Server's Msg 35318 for <c>RESUMABLE = ON</c>, which real raises at class 16 state 3.</summary>
    internal static SimulatedSqlException ColumnstoreResumable() =>
        new("The statement failed because the RESUMABLE option is not allowed when creating a columnstore index. Create the columnstore index without specifying the RESUMABLE option.", 35318, 16, 3);

    /// <summary>Mimics SQL Server's Msg 35327 — Msg 35317's rebuild sibling.</summary>
    internal static SimulatedSqlException ColumnstoreRebuildOption(string option) =>
        new($"ALTER INDEX REBUILD statement failed because specifying {option} is not allowed when rebuilding a columnstore index. Rebuild the columnstore index without specifying {option}.", 35327, 16, 1);

    /// <summary>Mimics SQL Server's Msg 35328 — Msg 35318's rebuild sibling, which <c>ALTER INDEX … SET</c> raises too.</summary>
    internal static SimulatedSqlException ColumnstoreRebuildLockOption(string option) =>
        new($"ALTER INDEX REBUILD statement failed because the {option} option is not allowed when rebuilding a columnstore index. Rebuild the columnstore index without specifying the {option} option.", 35328, 16, 1);

    /// <summary>Mimics SQL Server's Msg 35335 — a column list on a clustered columnstore index.</summary>
    internal static SimulatedSqlException ClusteredColumnstoreKeyList() =>
        new("The statement failed because specifying a key list is not allowed when creating a clustered columnstore index. Create the clustered columnstore index without specifying a key list.", 35335, 15, 1);

    /// <summary>Mimics SQL Server's Msg 35336 — a nonclustered columnstore index with no column list; real's text ends in " ."</summary>
    internal static SimulatedSqlException ColumnstoreKeyListMissing() =>
        new("The statement failed because specifying key list is missing when creating an index. Create the index with specifying key list .", 35336, 15, 1);

    /// <summary>Mimics SQL Server's Msg 35339 — a second columnstore index on one table.</summary>
    internal static SimulatedSqlException MultipleColumnstoreIndexes() =>
        new("Multiple columnstore indexes are not supported.", 35339, 16, 1);

    /// <summary>Mimics SQL Server's Msg 35343 — a column whose type no columnstore index of that kind can hold.</summary>
    internal static SimulatedSqlException ColumnstoreUnsupportedType(string columnName) =>
        new($"The statement failed. Column '{columnName}' has a data type that cannot participate in a columnstore index.", 35343, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 35372 — a clustered columnstore index on a
    /// table that already has a clustered index, without
    /// <c>DROP_EXISTING</c>; state 3.
    /// </summary>
    internal static SimulatedSqlException MoreThanOneClusteredIndexForColumnstore(string tableName) =>
        new($"You cannot create more than one clustered index on table '{tableName}'. Consider creating a new clustered index using 'with (drop_existing = on)' option.", 35372, 16, 3);

    /// <summary>Mimics SQL Server's Msg 35382 — a <c>COMPRESSION_DELAY</c> past 10080 minutes; state 2.</summary>
    internal static SimulatedSqlException CompressionDelayOutOfRange(int minutes) =>
        new($"The specified COMPRESSION_DELAY option value {minutes} is invalid. The valid range for disk-based table is between (0, 10080) minutes and for memory-optimized table is 0 or between (60, 10080) minutes.", 35382, 16, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 10799 — a rowstore <c>DATA_COMPRESSION</c>
    /// level on a columnstore index: class 15 creating it, 16 rebuilding it.
    /// </summary>
    internal static SimulatedSqlException ColumnstoreInvalidCompression(byte @class) =>
        new("This is not a valid data compression setting for a columnstore index. Please choose COLUMNSTORE or COLUMNSTORE_ARCHIVE compression.", 10799, @class, 1);

    /// <summary>Mimics SQL Server's Msg 10798 — a columnstore <c>DATA_COMPRESSION</c> level rebuilding a rowstore index.</summary>
    internal static SimulatedSqlException RowstoreColumnstoreCompression() =>
        new("This is not a valid data compression setting for this object. It can only be used with columnstore indexes. Please choose NONE, PAGE, or ROW compression.", 10798, 16, 1);

    /// <summary>Mimics SQL Server's Msg 35364 — <c>ALTER INDEX … SET (COMPRESSION_DELAY = …)</c> on a rowstore index.</summary>
    internal static SimulatedSqlException CompressionDelayOnRowstoreIndex() =>
        new("ALTER INDEX statement option COMPRESSION_DELAY can only be used with columnstore indexes.", 35364, 16, 1);

    /// <summary>Mimics SQL Server's Msg 16210 — <c>XML_COMPRESSION</c> on a columnstore index, which it names.</summary>
    internal static SimulatedSqlException ColumnstoreXmlCompression(string indexName) =>
        new($"Invalid xml compression setting for columnstore index '{indexName}'.", 16210, 15, 1);
}
