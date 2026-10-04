namespace SqlServerSimulator;

// System-versioned temporal table refusals, wording and states probed
// 2026-10-04 against SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>Mimics SQL Server error 13502 / 13503: a table declaring two <c>GENERATED ALWAYS AS ROW START</c> (or <c>END</c>) columns.</summary>
    internal static SimulatedSqlException TemporalGeneratedColumnRepeated(bool start) =>
        start
            ? new("System-versioned table cannot have more than one 'GENERATED ALWAYS AS ROW START' column.", 13502, 16, 1)
            : new("System-versioned table cannot have more than one 'GENERATED ALWAYS AS ROW END' column.", 13503, 16, 1);

    /// <summary>Mimics SQL Server error 13508: a table declaring <c>PERIOD FOR SYSTEM_TIME</c> twice.</summary>
    internal static SimulatedSqlException TemporalPeriodRepeated() =>
        new("System-versioned table cannot have more than one SYSTEM_TIME period definition.", 13508, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13553: a table turned system-versioned without a
    /// primary key. A TRY catches it; uncaught it ends the batch.
    /// </summary>
    internal static SimulatedSqlException TemporalTableRequiresPrimaryKey(string qualifiedTableName) =>
        new($"System versioned temporal table '{qualifiedTableName}' must have primary key defined.", 13553, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13539: a <c>HISTORY_TABLE</c> named in one or three parts rather than two, echoed as written.</summary>
    internal static SimulatedSqlException HistoryTableNotTwoPartName(string writtenName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{writtenName}' is not specified in two-part name format.", 13539, 15, 1);

    /// <summary>Mimics the Msg 102 a <c>SYSTEM_VERSIONING</c> option written twice raises, at severity 16 and a state per option.</summary>
    internal static SimulatedSqlException SystemVersioningOptionRepeated(string option, byte state) =>
        new($"Incorrect syntax near '{option}'.", 102, 16, state);

    /// <summary>Mimics SQL Server error 13511: a <c>HISTORY_TABLE</c> naming an object that is no table, as <c>schema.name</c>.</summary>
    internal static SimulatedSqlException ObjectCannotBeHistoryTable(string schemaQualifiedName) =>
        new($"Specified object '{schemaQualifiedName}' cannot be used as history table.", 13511, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13567: a temporary <c>HISTORY_TABLE</c> name.</summary>
    internal static SimulatedSqlException HistoryTableInTempdb(string name) =>
        new($"Temporal history table '{name}' cannot be created in a 'tempdb' database.", 13567, 15, 1);

    /// <summary>Mimics SQL Server error 13568: a temporary table declared system-versioned.</summary>
    internal static SimulatedSqlException TemporalTableInTempdb(string name) =>
        new($"System-versioned temporal table '{name}' cannot be created in a 'tempdb' database.", 13568, 16, 1);

    /// <summary>Mimics SQL Server error 13572: a table variable declaring a period column.</summary>
    internal static SimulatedSqlException TableVariableWithPeriod() =>
        new("Creating table variables containing PERIOD is not allowed.", 13572, 16, 1);

    /// <summary>Mimics SQL Server error 13566: a <c>HISTORY_TABLE</c> that is itself a system-versioned table.</summary>
    internal static SimulatedSqlException TemporalTableAlreadyInUse(string qualifiedTableName) =>
        new($"Temporal table '{qualifiedTableName}' is already in use.", 13566, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13519: an adopted history table with a computed column, refused ahead of the column count.</summary>
    internal static SimulatedSqlException HistoryTableHasComputedColumn(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' has computed column specification. Consider dropping all computed column specifications and trying again.", 13519, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13580: an adopted history table with a <c>ROWGUIDCOL</c>, refused ahead of the column count.</summary>
    internal static SimulatedSqlException HistoryTableHasRowGuidColumn(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' has ROWGUID column specification. Consider dropping all ROWGUID column specifications and trying again.", 13580, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13530: an adopted history table's column matching a period column is nullable.</summary>
    internal static SimulatedSqlException HistoryPeriodColumnNullable(string columnName, string qualifiedHistoryName, string qualifiedTableName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because system column '{columnName}' in history table '{qualifiedHistoryName}' corresponds to a period column in table '{qualifiedTableName}' and cannot be nullable.", 13530, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13533: matching columns differ in being sparse.</summary>
    internal static SimulatedSqlException HistoryColumnSparseMismatch(string columnName, string qualifiedTableName, string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because column '{columnName}' does not have the same sparse storage attribute in tables '{qualifiedTableName}' and '{qualifiedHistoryName}'.", 13533, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 13541 / 13543 / 13573, the data consistency
    /// check's findings in an adopted history table, all at state 0 and ending
    /// the batch uncaught: a period ending before it starts, one ending in the
    /// future, and two periods of one key overlapping.
    /// </summary>
    internal static SimulatedSqlException HistoryEndBeforeStart(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' contains invalid records with end of period set before start.", 13541, 16, 0) { TerminatesBatch = true };

    /// <inheritdoc cref="HistoryEndBeforeStart"/>
    internal static SimulatedSqlException HistoryEndInFuture(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' contains invalid records with end of period set to a value in the future.", 13543, 16, 0) { TerminatesBatch = true };

    /// <inheritdoc cref="HistoryEndBeforeStart"/>
    internal static SimulatedSqlException HistoryOverlappingRecords(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' contains overlapping records.", 13573, 16, 0) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13542: <c>ADD PERIOD FOR SYSTEM_TIME</c> over a row whose period starts in the future.</summary>
    internal static SimulatedSqlException AddPeriodStartInFuture(string qualifiedTableName) =>
        new($"ADD PERIOD FOR SYSTEM_TIME on table '{qualifiedTableName}' failed because there are open records with start of period set to a value in the future.", 13542, 16, 0) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13749: a <c>HISTORY_RETENTION_PERIOD</c> longer than real keeps.</summary>
    internal static SimulatedSqlException HistoryRetentionTooBig(int count, string unitName) =>
        new($"The period of {count} {unitName} is too big for system versioning history retention.", 13749, 16, 1);

    /// <summary>Mimics SQL Server error 13545: <c>TRUNCATE TABLE</c> of a system-versioned table or its history. A TRY catches it; uncaught it ends the batch.</summary>
    internal static SimulatedSqlException TruncateSystemVersionedTable(string qualifiedTableName) =>
        new($"Truncate failed on table '{qualifiedTableName}' because it is not a supported operation on system-versioned tables.", 13545, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 13560 / 13561 / 13562: a <c>DELETE</c>, <c>UPDATE</c> or <c>MERGE</c> of a history table, refused while the batch compiles.</summary>
    internal static SimulatedSqlException CannotWriteTemporalHistoryTable(string verb, string qualifiedTableName) => verb switch
    {
        "DELETE" => new($"Cannot delete rows from a temporal history table '{qualifiedTableName}'.", 13560, 16, 1),
        "UPDATE" => new($"Cannot update rows in a temporal history table '{qualifiedTableName}'.", 13561, 16, 1),
        _ => new($"Cannot perform MERGE operation on temporal history table '{qualifiedTableName}'.", 13562, 16, 1),
    };

    /// <summary>Mimics SQL Server error 13569: an <c>INSTEAD OF</c> trigger on a system-versioned table (state 2), or any trigger on its history (state 1).</summary>
    internal static SimulatedSqlException CannotCreateTriggerOnTemporalTable(string qualifiedTableName, byte state) =>
        new($"Cannot create a trigger on a system-versioned temporal table '{qualifiedTableName}'.", 13569, 16, state);

    /// <summary>Mimics SQL Server error 13590: a <c>FOR SYSTEM_TIME</c> query of a view whose definition already applies one to the same table.</summary>
    internal static SimulatedSqlException ForSystemTimeAppliedTwice(string tableName) =>
        new($"Temporal FOR SYSTEM_TIME clause can only be set once per temporal table. '{tableName}' has more than one temporal FOR SYSTEM_TIME clause.", 13590, 16, 1);

    /// <summary>Mimics SQL Server error 13704: adding an identity column to a system-versioned table, whose history can't take one.</summary>
    internal static SimulatedSqlException HistoryTableCannotTakeIdentity(string qualifiedHistoryName) =>
        new($"System-versioned table schema modification failed because history table '{qualifiedHistoryName}' has IDENTITY column specification. Consider dropping all IDENTITY column specifications and trying again.", 13704, 16, 1);

    /// <summary>Mimics SQL Server error 13724: adding a computed column to a system-versioned table.</summary>
    internal static SimulatedSqlException ComputedColumnWhileSystemVersioned() =>
        new("System-versioned table schema modification failed because adding computed column while system-versioning is ON is not supported.", 13724, 16, 1);

    /// <summary>Mimics SQL Server error 11418: adding a sparse column a compressed table — an engine-built history — can't hold.</summary>
    internal static SimulatedSqlException SparseColumnsIncompatibleWithCompression(string tableName) =>
        new($"Cannot alter table '{tableName}' because the table either contains sparse columns or a column set column which are incompatible with compression.", 11418, 16, 2);

    /// <summary>Mimics SQL Server error 13550 / 13551 / 13548: adding, dropping or altering a column of a history table directly.</summary>
    internal static SimulatedSqlException HistoryTableColumnChange(string operation, string? columnName, string qualifiedTableName) => operation switch
    {
        "ADD" => new($"Add column operation failed on table '{qualifiedTableName}' because it is not a supported operation on system-versioned temporal tables.", 13550, 16, 1),
        "DROP" => new($"Drop column operation failed on table '{qualifiedTableName}' because it is not a supported operation on system-versioned temporal tables.", 13551, 16, 1),
        _ => new($"Cannot alter column '{columnName}' on table '{qualifiedTableName}' because it is not a supported operation on system-versioned temporal or ledger tables.", 13548, 16, 1),
    };

    /// <summary>Mimics SQL Server error 13564 / 13558: adding a CHECK or a PRIMARY KEY to a history table, each followed by Msg 1750.</summary>
    internal static SimulatedSqlException HistoryTableConstraint(bool primaryKey, string qualifiedTableName) =>
        FollowedByConstraintNotCreated(primaryKey
            ? new($"Cannot add PRIMARY KEY constraint to a temporal history table '{qualifiedTableName}'.", 13558, 16, 1)
            : new($"Adding CHECK constraint to a temporal history table '{qualifiedTableName}' is not allowed.", 13564, 16, 1),
            state: 0);
}
